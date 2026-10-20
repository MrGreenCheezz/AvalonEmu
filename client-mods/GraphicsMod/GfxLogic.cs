using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;
using Il2CppInterop.Runtime;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Rendering.PostProcessing;

namespace GraphicsMod;

/// <summary>
/// All state and Unity calls. Only native (non-stripped) Unity accessors are used: QualitySettings getters exist for
/// shadows/shadowDistance/shadowCascades/shadowResolution/shadowProjection/lodBias; pixelLightCount, anisotropicFiltering
/// and shadowCascade4Split are set-only (getters stripped), antiAliasing/maximumLODLevel/shadowmaskMode are stripped.
/// Strings are formatted in managed code only (see CameraMod/NOTES.md crash rule).
/// </summary>
internal static class GfxLogic
{
    internal static bool Active;            // mod values wanted
    internal static bool DriverAlive;       // injected Driver.Update is running
    private static bool _applied;           // mod values currently applied (baseline captured)

    // effective target values (config + preset), computed on activation
    private static float _dist, _distFp, _fpHeight, _lodBias, _strength, _bias, _normalBias;
    private static int _cascades, _pixelLights;
    private static ResSetting _res, _lightRes;
    private static ProjSetting _proj;
    private static AnisoSetting _aniso;
    private static AaSetting _aa;
    private static bool _soft, _forceOn, _charShadows;
    private static Vector3 _splits; private static bool _hasSplits;

    // game baseline (what the game set last)
    private static ShadowQuality _bShadows; private static float _bDist; private static int _bCascades;
    private static ShadowResolution _bRes; private static ShadowProjection _bProj; private static float _bLod;
    private static bool _haveBase;
    private static float _lodWeSet = -1f;

    private static GraphicOptions _options;
    private static ActorCameraController _ctrl;
    private static int _ctrlFrame = -1000;
    private static bool _lowCamera;
    private static bool _optionsLogged, _inWorldLogged;

    private sealed class LightRec { public Light Light; public LightShadows Orig; public bool ShadowsChanged, ResChanged; }
    private static readonly Dictionary<IntPtr, LightRec> _lights = new();
    private static readonly Dictionary<IntPtr, SkinnedMeshRenderer> _charChanged = new();
    private static PostProcessLayer _ppl; private static IntPtr _pplPtr;
    private static PostProcessLayer.Antialiasing _origAa; private static SubpixelMorphologicalAntialiasing.Quality _origSmaa; private static bool _aaSaved;

    private static int _corrections, _correctionsLogged; private static float _nextSummary;
    private static int _failCount;

    // ---------------------------------------------------------------- formatting (managed only)
    internal static string F(float v) => v.ToString("0.##", CultureInfo.InvariantCulture);
    private static string V(Vector3 v) => "(" + F(v.x) + ", " + F(v.y) + ", " + F(v.z) + ")";
    private static int Frame => Time.frameCount;

    internal static void Fail(string where, Exception ex)
    {
        _failCount++;
        if (_failCount <= 10) Plugin.L.LogWarning("GraphicsMod: error in " + where + ": " + ex.GetType().Name + ": " + ex.Message);
    }

    // ---------------------------------------------------------------- settings
    private static void ComputeTargets()
    {
        _dist = Math.Max(0f, Plugin.ShadowDistance.Value);
        _distFp = Plugin.ShadowDistanceFirstPerson.Value > 0 ? Plugin.ShadowDistanceFirstPerson.Value : _dist;
        _fpHeight = Plugin.FirstPersonHeight.Value;
        _cascades = Plugin.ShadowCascades.Value;
        if (_cascades != 0 && _cascades != 1 && _cascades != 2 && _cascades != 4) _cascades = 4;
        _res = Plugin.ShadowResolution.Value; _lightRes = Plugin.LightShadowResolution.Value; _proj = Plugin.ShadowProjection.Value;
        _soft = Plugin.SoftShadows.Value; _forceOn = Plugin.ForceShadowsOn.Value; _charShadows = Plugin.CharacterShadows.Value;
        _strength = Plugin.ShadowStrength.Value; _bias = Plugin.ShadowBias.Value; _normalBias = Plugin.ShadowNormalBias.Value;
        _lodBias = Plugin.LodBias.Value; _pixelLights = Plugin.PixelLightCount.Value;
        _aniso = Plugin.Anisotropic.Value; _aa = Plugin.AntiAliasing.Value;
        _hasSplits = ParseSplits(Plugin.CascadeSplits.Value, out _splits);

        if (Plugin.PresetCfg.Value == Preset.UltraShadows)
        {
            _dist = Math.Max(_dist, 300f); _distFp = Math.Max(_distFp, 400f);
            _cascades = 4; _res = ResSetting.VeryHigh; _lightRes = ResSetting.VeryHigh; _proj = ProjSetting.StableFit;
            _soft = true; _forceOn = true; _lodBias = Math.Max(_lodBias, 3f);
            _splits = new Vector3(0.025f, 0.08f, 0.25f); _hasSplits = true;
        }
        else if (Plugin.LowCameraPreset.Value == LowCamPreset.Performance && Plugin.LowCameraShadowDistance.Value > 0)
            _distFp = Math.Min(_distFp > 0 ? _distFp : float.MaxValue, Plugin.LowCameraShadowDistance.Value);
    }

    private static bool ParseSplits(string s, out Vector3 v)
    {
        v = default;
        if (string.IsNullOrWhiteSpace(s)) return false;
        var p = s.Split(new[] { ',', ';', ' ' }, StringSplitOptions.RemoveEmptyEntries);
        if (p.Length != 3) { Plugin.L.LogWarning("GraphicsMod: CascadeSplits needs three numbers, using the game's splits"); return false; }
        var f = new float[3];
        for (int i = 0; i < 3; i++)
            if (!float.TryParse(p[i], NumberStyles.Float, CultureInfo.InvariantCulture, out f[i])) { Plugin.L.LogWarning("GraphicsMod: CascadeSplits not a number: " + p[i]); return false; }
        if (!(f[0] > 0 && f[0] < f[1] && f[1] < f[2] && f[2] < 1)) { Plugin.L.LogWarning("GraphicsMod: CascadeSplits must be increasing in 0..1, using the game's splits"); return false; }
        v = new Vector3(f[0], f[1], f[2]);
        return true;
    }

    private static string TargetLine()
    {
        var sb = new StringBuilder();
        sb.Append("preset ").Append(Plugin.PresetCfg.Value.ToString()).Append(", low camera preset ").Append(Plugin.LowCameraPreset.Value.ToString())
          .Append(", shadow distance ").Append(_dist > 0 ? F(_dist) : "game").Append(" (low camera < ").Append(F(_fpHeight)).Append(" m: ").Append(F(_distFp)).Append(")")
          .Append(", cascades ").Append(_cascades > 0 ? _cascades.ToString(CultureInfo.InvariantCulture) : "game")
          .Append(", splits ").Append(_hasSplits ? V(_splits) : "game")
          .Append(", resolution ").Append(_res.ToString()).Append(", projection ").Append(_proj.ToString())
          .Append(", soft ").Append(_soft ? "yes" : "game").Append(", force on ").Append(_forceOn ? "yes" : "no")
          .Append(", strength ").Append(_strength >= 0 ? F(_strength) : "game")
          .Append(", light res ").Append(_lightRes.ToString())
          .Append(", bias ").Append(_bias >= 0 ? F(_bias) : "game").Append("/").Append(_normalBias >= 0 ? F(_normalBias) : "game")
          .Append(", character shadows ").Append(_charShadows ? "on" : "off")
          .Append(", lodBias ").Append(_lodBias > 0 ? ">= " + F(_lodBias) : "game")
          .Append(", pixel lights ").Append(_pixelLights > 0 ? _pixelLights.ToString(CultureInfo.InvariantCulture) : "game")
          .Append(", aniso ").Append(_aniso.ToString()).Append(", AA ").Append(_aa.ToString());
        return sb.ToString();
    }

    // ---------------------------------------------------------------- logging
    internal static void LogEnvironment()
    {
        string pipe;
        try { pipe = GraphicsSettings.currentRenderPipeline == null ? "built-in (GraphicsSettings.currentRenderPipeline = null)" : "SRP asset '" + GraphicsSettings.currentRenderPipeline.name + "'"; }
        catch (Exception ex) { pipe = "unknown (" + ex.GetType().Name + ")"; }
        string path = "no main camera";
        try { var c = Camera.main; if (c != null) path = c.renderingPath.ToString() + " -> actual " + c.actualRenderingPath.ToString(); } catch { }
        Plugin.L.LogInfo("GraphicsMod: render pipeline " + pipe + ", main camera path " + path);
    }

    internal static string StateLine()
    {
        var sb = new StringBuilder();
        try
        {
            sb.Append("quality level ").Append(QualitySettings.GetQualityLevel().ToString(CultureInfo.InvariantCulture))
              .Append(", shadows ").Append(QualitySettings.shadows.ToString())
              .Append(", distance ").Append(F(QualitySettings.shadowDistance))
              .Append(", cascades ").Append(QualitySettings.shadowCascades.ToString(CultureInfo.InvariantCulture))
              .Append(", resolution ").Append(QualitySettings.shadowResolution.ToString())
              .Append(", projection ").Append(QualitySettings.shadowProjection.ToString())
              .Append(", lodBias ").Append(F(QualitySettings.lodBias));
        }
        catch (Exception ex) { sb.Append("QualitySettings read failed: ").Append(ex.Message); }
        try
        {
            var c = Camera.main;
            if (c != null)
            {
                sb.Append(", camera '").Append(c.name).Append("' path ").Append(c.actualRenderingPath.ToString())
                  .Append(" far ").Append(F(c.farClipPlane));
                var ppl = FindPpl(c);
                if (ppl != null) sb.Append(", AA ").Append(ppl.antialiasingMode.ToString()).Append(" smaa ").Append(ppl.subpixelMorphologicalAntialiasing.quality.ToString());
                else sb.Append(", no PostProcessLayer");
            }
        }
        catch (Exception ex) { sb.Append(", camera read failed: ").Append(ex.Message); }
        try
        {
            int n = 0;
            foreach (var o in UnityEngine.Object.FindObjectsOfType(Il2CppType.Of<Light>()))
            {
                var l = o.TryCast<Light>();
                if (l == null || l.type != LightType.Directional) continue;
                if (n++ >= 4) break;
                sb.Append(", dir light '").Append(l.name).Append("' ").Append(l.enabled ? "on" : "off")
                  .Append(" shadows ").Append(l.shadows.ToString()).Append(" strength ").Append(F(l.shadowStrength))
                  .Append(" intensity ").Append(F(l.intensity));
            }
            if (n == 0) sb.Append(", no directional light");
        }
        catch (Exception ex) { sb.Append(", light read failed: ").Append(ex.Message); }
        sb.Append(", low camera ").Append(_lowCamera ? "yes" : "no");
        return sb.ToString();
    }

    internal static void Log(string when) => Plugin.L.LogInfo("GraphicsMod: [" + when + "] " + StateLine());

    private static void LogGameOptions(GraphicOptions go)
    {
        var sb = new StringBuilder("GraphicsMod: game graphics options: quality ");
        try { sb.Append(go.g.ToString()); } catch { sb.Append('?'); }
        GraphicsOptionType[] types = {
            GraphicsOptionType.Shadow, GraphicsOptionType.ShadowResolution, GraphicsOptionType.ShadowCascades, GraphicsOptionType.ShadowDistance,
            GraphicsOptionType.PixelLightCount, GraphicsOptionType.AnisotropicTextureFiltering, GraphicsOptionType.SMAA, GraphicsOptionType.PrefabQuality };
        foreach (var t in types)
        {
            sb.Append(", ").Append(t.ToString()).Append(' ');
            try { sb.Append(go.b(t).ToString()).Append('=').Append(go.d(t).ToString(CultureInfo.InvariantCulture)); }
            catch (Exception ex) { sb.Append("? (").Append(ex.GetType().Name).Append(')'); }
        }
        Plugin.L.LogInfo(sb.ToString());
    }

    // ---------------------------------------------------------------- baseline
    private static void CaptureBaseline()
    {
        _bShadows = QualitySettings.shadows; _bDist = QualitySettings.shadowDistance; _bCascades = QualitySettings.shadowCascades;
        _bRes = QualitySettings.shadowResolution; _bProj = QualitySettings.shadowProjection; _bLod = QualitySettings.lodBias;
        _haveBase = true;
    }

    // ---------------------------------------------------------------- apply / restore
    internal static void Toggle()
    {
        if (Active) { Active = false; Restore("toggle"); }
        else { Active = true; Activate("toggle"); }
    }

    internal static void Activate(string why)
    {
        ComputeTargets();
        if (!_applied) CaptureBaseline();
        _applied = true;
        Plugin.L.LogInfo("GraphicsMod: mod values ON (" + why + "): " + TargetLine());
        ApplyAll("activate");
        Log("after apply");
    }

    private static void ApplyAll(string why)
    {
        Enforce(true, why);
        ApplyOnceValues();
        RefreshLights(); ApplyLights(true);
        ApplyAa();
        if (_charShadows) ScanCharacters();
    }

    /// <summary>Values the game only sets in GraphicOptions.d(): set them on activation and after d().</summary>
    private static void ApplyOnceValues()
    {
        if (_lodBias > 0 && QualitySettings.lodBias < _lodBias) { QualitySettings.lodBias = _lodBias; _lodWeSet = _lodBias; }
        if (_pixelLights > 0) QualitySettings.pixelLightCount = _pixelLights;
        if (_aniso != AnisoSetting.Game)
            QualitySettings.anisotropicFiltering = _aniso == AnisoSetting.Disable ? AnisotropicFiltering.Disable
                : _aniso == AnisoSetting.Enable ? AnisotropicFiltering.Enable : AnisotropicFiltering.ForceEnable;
    }

    /// <summary>Cheap per-call check of the QualitySettings shadow values; sets only what differs.</summary>
    private static void Enforce(bool full, string why)
    {
        if (!_applied) return;
        string changed = null;
        var cur = QualitySettings.shadows;
        ShadowQuality want = cur;
        if (cur == ShadowQuality.Disable) { if (_forceOn) want = _soft ? ShadowQuality.All : ShadowQuality.HardOnly; }
        else if (_soft && cur == ShadowQuality.HardOnly) want = ShadowQuality.All;
        if (want != cur) { QualitySettings.shadows = want; changed = Add(changed, "shadows " + cur.ToString() + "->" + want.ToString()); }

        float d = _lowCamera ? _distFp : _dist;
        if (d > 0)
        {
            float cd = QualitySettings.shadowDistance;
            if (Math.Abs(cd - d) > 0.01f) { QualitySettings.shadowDistance = d; changed = Add(changed, "distance " + F(cd) + "->" + F(d)); }
        }
        if (_cascades > 0)
        {
            int cc = QualitySettings.shadowCascades;
            if (cc != _cascades) { QualitySettings.shadowCascades = _cascades; changed = Add(changed, "cascades " + cc.ToString(CultureInfo.InvariantCulture) + "->" + _cascades.ToString(CultureInfo.InvariantCulture)); }
        }
        if (_res != ResSetting.Game)
        {
            var wr = (ShadowResolution)((int)_res - 1);
            var cr = QualitySettings.shadowResolution;
            if (cr != wr) { QualitySettings.shadowResolution = wr; changed = Add(changed, "resolution " + cr.ToString() + "->" + wr.ToString()); }
        }
        if (_proj != ProjSetting.Game)
        {
            var wp = _proj == ProjSetting.CloseFit ? UnityEngine.ShadowProjection.CloseFit : UnityEngine.ShadowProjection.StableFit;
            var cp = QualitySettings.shadowProjection;
            if (cp != wp) { QualitySettings.shadowProjection = wp; changed = Add(changed, "projection " + cp.ToString() + "->" + wp.ToString()); }
        }
        if ((changed != null || full) && _hasSplits && QualitySettings.shadowCascades == 4) QualitySettings.shadowCascade4Split = _splits;

        if (changed != null && !full)
        {
            _corrections++;
            if (_correctionsLogged < 8) { _correctionsLogged++; Plugin.L.LogInfo("GraphicsMod: re-applied after game change (" + why + "): " + changed); }
        }
    }

    private static string Add(string a, string b) => a == null ? b : a + ", " + b;

    internal static void Restore(string why)
    {
        if (!_applied) return;
        _applied = false;
        try
        {
            if (_haveBase)
            {
                QualitySettings.shadows = _bShadows; QualitySettings.shadowDistance = _bDist; QualitySettings.shadowCascades = _bCascades;
                QualitySettings.shadowResolution = _bRes; QualitySettings.shadowProjection = _bProj;
                if (_lodWeSet > 0 && Math.Abs(QualitySettings.lodBias - _lodWeSet) < 0.001f) QualitySettings.lodBias = _bLod;
                _lodWeSet = -1f;
            }
            if (_options != null)
            {
                if (_pixelLights > 0) QualitySettings.pixelLightCount = _options.d(GraphicsOptionType.PixelLightCount);
                if (_aniso != AnisoSetting.Game) QualitySettings.anisotropicFiltering = (AnisotropicFiltering)_options.d(GraphicsOptionType.AnisotropicTextureFiltering);
            }
            else if (_pixelLights > 0 || _aniso != AnisoSetting.Game)
                Plugin.L.LogInfo("GraphicsMod: game options object not seen yet - pixel light count / anisotropic filtering stay until the game re-applies its options");
            // let the game recompute its zoom-dependent shadow distance and cascade splits
            if (_ctrl != null && Frame - _ctrlFrame < 10) _ctrl.OnQualityChanged();
        }
        catch (Exception ex) { Fail("restore quality", ex); }
        try { RestoreLights(); } catch (Exception ex) { Fail("restore lights", ex); }
        try { RestoreAa(); } catch (Exception ex) { Fail("restore AA", ex); }
        try { RestoreCharacters(); } catch (Exception ex) { Fail("restore characters", ex); }
        Plugin.L.LogInfo("GraphicsMod: mod values OFF (" + why + "), game values restored");
        Log("game values");
    }

    // ---------------------------------------------------------------- lights
    private static void RefreshLights()
    {
        var seen = new HashSet<IntPtr>();
        foreach (var o in UnityEngine.Object.FindObjectsByType(Il2CppType.Of<Light>(), FindObjectsInactive.Exclude, FindObjectsSortMode.None))
        {
            var l = o.TryCast<Light>();
            if (l == null || l.type != LightType.Directional) continue;
            seen.Add(l.Pointer);
            if (!_lights.ContainsKey(l.Pointer)) _lights[l.Pointer] = new LightRec { Light = l, Orig = l.shadows };
        }
        var dead = new List<IntPtr>();
        foreach (var kv in _lights) if (!seen.Contains(kv.Key)) dead.Add(kv.Key);
        foreach (var k in dead) _lights.Remove(k);
    }

    private static void ApplyLights(bool full)
    {
        if (!_applied) return;
        foreach (var r in _lights.Values)
        {
            var l = r.Light;
            if (l == null || l.WasCollected) continue;
            var s = l.shadows;
            if (s == LightShadows.None) continue;   // e.g. the bounce light: never turn shadows on for it
            if (_soft && s == LightShadows.Hard) { if (!r.ShadowsChanged) r.Orig = s; l.shadows = LightShadows.Soft; r.ShadowsChanged = true; }
            if (_strength >= 0) l.shadowStrength = Math.Clamp(_strength, 0f, 1f);
            if (full)
            {
                if (_lightRes != ResSetting.Game && !r.ResChanged) { l.shadowResolution = (LightShadowResolution)((int)_lightRes - 1); r.ResChanged = true; }
                if (_bias >= 0) l.shadowBias = _bias;
                if (_normalBias >= 0) l.shadowNormalBias = _normalBias;
            }
        }
    }

    private static void RestoreLights()
    {
        foreach (var r in _lights.Values)
        {
            var l = r.Light;
            if (l == null || l.WasCollected) continue;
            if (r.ShadowsChanged) { l.shadows = r.Orig; r.ShadowsChanged = false; }
            if (r.ResChanged) { l.shadowResolution = LightShadowResolution.FromQualitySettings; r.ResChanged = false; }
        }
        if (_bias >= 0 || _normalBias >= 0)
            foreach (var o in UnityEngine.Object.FindObjectsOfType(Il2CppType.Of<Atmosphere>()))
            {
                var a = o.TryCast<Atmosphere>();
                if (a != null) a.ApplyShadowBiases();
            }
    }

    // ---------------------------------------------------------------- anti-aliasing (PPv2 on the main camera)
    private static PostProcessLayer FindPpl(Camera c)
    {
        var comp = c.GetComponent(Il2CppType.Of<PostProcessLayer>());
        return comp == null ? null : comp.TryCast<PostProcessLayer>();
    }

    private static void ApplyAa()
    {
        if (!_applied || _aa == AaSetting.Game) return;
        var c = Camera.main;
        if (c == null) return;
        var ppl = FindPpl(c);
        if (ppl == null) return;
        if (ppl.Pointer != _pplPtr) { _ppl = ppl; _pplPtr = ppl.Pointer; _origAa = ppl.antialiasingMode; _origSmaa = ppl.subpixelMorphologicalAntialiasing.quality; _aaSaved = true; }
        var want = _aa switch
        {
            AaSetting.Off => PostProcessLayer.Antialiasing.None,
            AaSetting.FXAA => PostProcessLayer.Antialiasing.FastApproximateAntialiasing,
            AaSetting.TAA => PostProcessLayer.Antialiasing.TemporalAntialiasing,
            _ => PostProcessLayer.Antialiasing.SubpixelMorphologicalAntialiasing,
        };
        if (ppl.antialiasingMode != want) ppl.antialiasingMode = want;
        if (want == PostProcessLayer.Antialiasing.SubpixelMorphologicalAntialiasing && ppl.subpixelMorphologicalAntialiasing.quality != SubpixelMorphologicalAntialiasing.Quality.High)
            ppl.subpixelMorphologicalAntialiasing.quality = SubpixelMorphologicalAntialiasing.Quality.High;
    }

    private static void RestoreAa()
    {
        if (!_aaSaved || _ppl == null || _ppl.WasCollected) return;
        _ppl.antialiasingMode = _origAa;
        _ppl.subpixelMorphologicalAntialiasing.quality = _origSmaa;
        _aaSaved = false; _pplPtr = IntPtr.Zero; _ppl = null;
    }

    // ---------------------------------------------------------------- character shadows
    private static void ScanCharacters()
    {
        if (!_applied || !_charShadows) return;
        int found = 0, proxied = 0; var names = new StringBuilder();
        var proxyRoots = new Dictionary<IntPtr, bool>();
        // v7: forget the seen set once a minute (pooled models may be reused with different settings)
        if (Time.realtimeSinceStartup > _charSeenReset) { _charSeen.Clear(); _charSeenReset = Time.realtimeSinceStartup + 60f; }
        var arr = UnityEngine.Object.FindObjectsByType(Il2CppType.Of<SkinnedMeshRenderer>(), FindObjectsInactive.Exclude, FindObjectsSortMode.None);
        int n = arr == null ? 0 : arr.Length;
        IntPtr start = n > 0 ? IntPtr.Add(arr.Pointer, 4 * IntPtr.Size) : IntPtr.Zero;
        for (int i = 0; i < n; i++)
        {
            IntPtr p = System.Runtime.InteropServices.Marshal.ReadIntPtr(start, i * IntPtr.Size);
            if (p == IntPtr.Zero || _charSeen.Contains(p) || _charChanged.ContainsKey(p)) continue;
            var r = new SkinnedMeshRenderer(p);
            if (!r.enabled) continue;                 // not marked seen: may be enabled later
            _charSeen.Add(p);
            if (r.shadowCastingMode != ShadowCastingMode.Off) continue;
            // a model that already has a shadow-only proxy mesh would get a double shadow: leave it alone
            var rb = r.rootBone;
            var root = rb != null && rb.parent != null ? rb.parent : (r.transform.parent != null ? r.transform.parent : r.transform);
            if (!proxyRoots.TryGetValue(root.Pointer, out bool hasProxy))
            {
                hasProxy = false;
                foreach (var rr in root.GetComponentsInChildren<Renderer>(true))
                    if (rr != null && rr.shadowCastingMode == ShadowCastingMode.ShadowsOnly) { hasProxy = true; break; }
                proxyRoots[root.Pointer] = hasProxy;
            }
            if (hasProxy) { proxied++; continue; }
            r.shadowCastingMode = ShadowCastingMode.On;
            _charChanged[r.Pointer] = r;
            if (found < 8) names.Append(found == 0 ? "" : ", ").Append(r.name);
            found++;
        }
        if (found > 0) Plugin.L.LogInfo("GraphicsMod: character shadows: " + found.ToString(CultureInfo.InvariantCulture) + " skinned meshes had shadow casting Off -> On (" + names.ToString() + (found > 8 ? ", ..." : "") + ")");
        if (proxied > 0 && !_proxyLogged) { _proxyLogged = true; Plugin.L.LogInfo("GraphicsMod: character shadows: " + proxied.ToString(CultureInfo.InvariantCulture) + " skinned meshes with shadows Off left unchanged (their model has a shadow-only mesh)"); }
    }
    private static bool _proxyLogged;
    private static readonly HashSet<IntPtr> _charSeen = new();
    private static float _charSeenReset;

    private static void RestoreCharacters()
    {
        foreach (var r in _charChanged.Values)
            if (r != null && !r.WasCollected) r.shadowCastingMode = ShadowCastingMode.Off;
        _charChanged.Clear();
        _charSeen.Clear();
    }

    // ---------------------------------------------------------------- hooks
    internal static void AfterCameraLateUpdate(ActorCameraController c)
    {
        int frame = Frame;
        _ctrl = c; _ctrlFrame = frame;
        if (!DriverAlive)
        {
            // fallback when the injected driver is not running: toggle key and activation from here
            var k = Plugin.ToggleKey.Value;
            if (k != KeyCode.None && Input.GetKeyDown(k)) Toggle();
            if (Active && !_applied) { Log("in world (before mod)"); Activate("in world"); }
        }
        // v7: the game changes the shadow values only in GraphicOptions.d()/b() and QualitySettingsOverwrite (all have
        // postfixes that re-apply at once); the per-frame safety check now runs every 4th frame (~15 interop calls each)
        if ((frame & 3) != 0) return;
        UpdateLowCamera(c);
        if (!_applied) return;
        Enforce(false, "frame check");
        ApplyLights(false);
        if (!_inWorldLogged) { _inWorldLogged = true; Log("in world, mod values"); }
        if (_corrections > _correctionsLogged && Time.realtimeSinceStartup > _nextSummary)
        {
            _nextSummary = Time.realtimeSinceStartup + 60f;
            Plugin.L.LogInfo("GraphicsMod: game changed shadow settings " + _corrections.ToString(CultureInfo.InvariantCulture) + " times so far, all re-applied");
        }
    }

    private static void UpdateLowCamera(ActorCameraController c)
    {
        Transform target = c.e;
        if (target == null) return;
        Transform cam = null;
        var mc = Camera.main;
        if (mc != null) cam = mc.transform; else cam = c.CameraTransform;
        if (cam == null) return;
        float h = cam.position.y - target.position.y;
        bool low = _lowCamera ? h < _fpHeight + 1f : h < _fpHeight;
        if (low != _lowCamera)
        {
            _lowCamera = low;
            if (_applied && Math.Abs(_dist - _distFp) > 0.01f)
                Plugin.L.LogInfo("GraphicsMod: camera " + (low ? "low" : "high") + " (" + F(h) + " m above the player) -> shadow distance " + F(low ? _distFp : _dist));
        }
    }

    internal static void AfterGameOptionsApplied(GraphicOptions go)
    {
        _options = go;
        if (!_optionsLogged) { _optionsLogged = true; LogGameOptions(go); }
        CaptureBaseline();                 // the game just reset everything (SetQualityLevel + its options)
        Plugin.L.LogInfo("GraphicsMod: game applied its graphics options: " + StateLine());
        if (!_applied) return;
        ApplyAll("game options");
        Log("after re-apply");
    }

    internal static void AfterZoomShadow(GraphicOptions go)
    {
        _options = go;
        _bDist = QualitySettings.shadowDistance;   // the game's zoom-dependent distance
        if (!_applied) return;
        Enforce(true, "zoom");
    }

    internal static void AfterPostFx(GraphicOptions go)
    {
        _options = go;
        _aaSaved = false; _pplPtr = IntPtr.Zero;    // the game wrote its own AA values: they are the new originals
        if (_applied) ApplyAa();
    }

    internal static void AfterQso()
    {
        if (!_haveBase) return;
        _bShadows = QualitySettings.shadows; _bDist = QualitySettings.shadowDistance;
        _bRes = QualitySettings.shadowResolution; _bProj = QualitySettings.shadowProjection;
        if (_applied) Enforce(true, "QualitySettingsOverwrite");
    }

    /// <summary>Driver.Update: menus/loading (no follow camera) and periodic scans.</summary>
    internal static void DriverTick(int frame)
    {
        if (!_applied) return;
        bool inWorld = frame - _ctrlFrame < 5;
        if (!inWorld && frame % 10 == 0) { Enforce(false, "menu check"); ApplyLights(false); }
        // v7: light list every 10 s instead of 2 s (scene-wide search), AA every 2 s, character scan every 5 s but
        // incremental (only skinned meshes not seen before) and with FindObjectsByType (no sort)
        if (frame % 600 == 0) { long t = PerfBegin(); RefreshLights(); ApplyLights(true); PerfEnd(2, t); }
        if (frame % 120 == 0) ApplyAa();
        if (frame % 300 == 0 && _charShadows) { long t = PerfBegin(); ScanCharacters(); PerfEnd(3, t); }
    }

    // ---------------------------------------------------------------- profiler (managed Stopwatch, logged every N s)
    private static readonly string[] PerfNames = { "frame check", "driver", "light scan", "character scan" };
    private static readonly long[] _pTicks = new long[4], _pMax = new long[4];
    private static int _pFrames; private static long _pStart, _pLast, _pFrameSum;
    private static readonly double TickMs = 1000.0 / System.Diagnostics.Stopwatch.Frequency;
    internal static long PerfBegin() => Plugin.ProfileLog.Value ? System.Diagnostics.Stopwatch.GetTimestamp() : 0;
    internal static void PerfEnd(int i, long t0)
    {
        if (t0 == 0) return;
        long d = System.Diagnostics.Stopwatch.GetTimestamp() - t0;
        _pTicks[i] += d; if (d > _pMax[i]) _pMax[i] = d;
    }
    internal static void PerfFrame()
    {
        if (!Plugin.ProfileLog.Value) return;
        long now = System.Diagnostics.Stopwatch.GetTimestamp();
        if (_pStart == 0) { _pStart = _pLast = now; return; }
        _pFrameSum += now - _pLast; _pLast = now; _pFrames++;
        if ((now - _pStart) * TickMs < Math.Max(2f, Plugin.ProfileIntervalSec.Value) * 1000.0) return;
        try
        {
            if (_pFrames > 0)
            {
                double avg = _pFrameSum * TickMs / _pFrames;
                var sb = new StringBuilder("GraphicsMod perf [");
                sb.Append(_pFrames.ToString(CultureInfo.InvariantCulture)).Append(" frames, fps ").Append((avg > 0 ? 1000.0 / avg : 0).ToString("0.0", CultureInfo.InvariantCulture))
                  .Append(", camera ").Append(_lowCamera ? "low" : "high").Append("]: ");
                for (int i = 0; i < 4; i++)
                {
                    if (i > 0) sb.Append(", ");
                    sb.Append(PerfNames[i]).Append(' ').Append((_pTicks[i] * TickMs / _pFrames).ToString("0.000", CultureInfo.InvariantCulture)).Append(" ms/frame");
                    if (_pMax[i] * TickMs >= 0.5) sb.Append(" (max ").Append((_pMax[i] * TickMs).ToString("0.0", CultureInfo.InvariantCulture)).Append(" ms)");
                }
                sb.Append("; shadows ").Append(F(QualitySettings.shadowDistance)).Append(" m x").Append(QualitySettings.shadowCascades.ToString(CultureInfo.InvariantCulture))
                  .Append(", lodBias ").Append(F(QualitySettings.lodBias)).Append(", lights tracked ").Append(_lights.Count.ToString(CultureInfo.InvariantCulture))
                  .Append(", character meshes seen ").Append(_charSeen.Count.ToString(CultureInfo.InvariantCulture));
                Plugin.L.LogInfo(sb.ToString());
            }
        }
        catch (Exception ex) { Fail("perf log", ex); }
        Array.Clear(_pTicks, 0, 4); Array.Clear(_pMax, 0, 4); _pFrames = 0; _pFrameSum = 0; _pStart = now;
    }
}
