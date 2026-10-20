using System;
using System.Collections.Generic;
using HarmonyLib;
using Il2CppInterop.Runtime.InteropTypes.Arrays;
using UnityEngine;

namespace CameraMod;

/// <summary>
/// v5: keeps the first-person view full screen and gives a way to find what limits it.
///  * Viewport guard: the mod camera's Camera.rect is forced to (0,0,1,1) every frame in first person (original restored on
///    exit). The only game code that writes Camera.rect is NGUI's UIViewport.LateUpdate (VA 0x181A8F380); a Harmony prefix
///    skips it for the mod camera while first person is on.
///  * Helper cameras: cameras parented under the mod camera that copy its frustum once (CamXRay's "CamDepthXRay" depth
///    camera copies near/far/fov in CamXRay.OnEnable and renders the _DepthBlockers RT used for the x-ray silhouettes) get
///    the mod camera's fov/near/far every frame; restored on exit.
///  * Camera-attached renderers (meshes parented under the camera, e.g. a frame/backdrop built for the game's FOV 47) are
///    disabled in first person (HideCameraAttachedRenderers); restored on exit.
///  * DisableCameraComponents: Behaviours on the camera GameObject (by type name) disabled in first person; restored on exit.
///  * Diagnostics: full camera dump before entering, right after, and ~1 s later (CamDiag); a watchdog logs any change of
///    the camera's rect/pixelRect/target texture/projection while first person is on; DiagKey cycles single-factor
///    bisect steps (game FOV, game near, game far, sky off, game fog, game layerCullDistances, then each camera Behaviour
///    off), logging the state of every step.
/// </summary>
internal static class FpView
{
    private static Camera _cam;
    private static Transform _player;
    private static bool _active;
    private static Rect _savedRect;
    private static bool _rectSaved;
    private static int _dumpFrame = -1;
    private static string _lastWatch = "";
    private static int _watchLogs;

    private struct Helper { public Camera C; public float Fov, Near, Far; }
    private static readonly List<Helper> _helpers = new();
    private static readonly List<Renderer> _hiddenAttached = new();
    private static readonly List<Behaviour> _disabledComps = new();

    // bisect
    private static int _step;
    private static List<string> _steps;
    private static List<Behaviour> _stepComps;
    internal static bool KeepGameFov { get; private set; }

    internal static Camera Cam => _active ? _cam : null;

    internal static void Install(Harmony h)
    {
        Plugin.TryPatch(h, typeof(UIViewport), "LateUpdate", nameof(Plugin.UIViewportPrefix), null,
            "UIViewport.LateUpdate (VA 0x181A8F380) prefix: the only game writer of Camera.rect; skipped for the first-person camera");
    }

    /// <summary>Harmony prefix body: false = skip the game's UIViewport.LateUpdate.</summary>
    internal static bool UIViewportAllowed(UIViewport v)
    {
        if (!_active || _cam == null) return true;
        var c = v.GetComponent<Camera>();
        if (c == null || c.Pointer != _cam.Pointer) return true;
        if (_watchLogs++ < 3) Plugin.L.LogInfo("CameraMod: first person: UIViewport on the camera wanted to set its rect - skipped");
        return false;
    }

    /// <summary>Before the mod touches the camera.</summary>
    internal static void BeforeEnter(Camera cam, Transform player)
    {
        if (!Plugin.CameraDiagnostics.Value) return;
        try { CamDiag.DumpAll("before first person", cam, player); } catch (Exception ex) { Plugin.L.LogWarning("CameraMod: camera dump failed: " + ex.Message); }
    }

    /// <summary>After fov/near/far/sky/fog were applied.</summary>
    internal static void Enter(Camera cam, Transform player)
    {
        _cam = cam; _player = player; _active = true;
        _step = 0; _steps = null; _stepComps = null; KeepGameFov = false;
        _watchLogs = 0; _lastWatch = "";
        _helpers.Clear(); _hiddenAttached.Clear(); _disabledComps.Clear();

        try { _savedRect = cam.rect; _rectSaved = true; } catch (Exception ex) { _rectSaved = false; Plugin.L.LogWarning("CameraMod: camera rect: " + ex.Message); }
        ForceViewport(true);

        if (Plugin.SyncHelperCameras.Value)
        {
            try
            {
                foreach (var c in cam.GetComponentsInChildren<Camera>(true))
                {
                    if (c == null || c.Pointer == cam.Pointer || c.orthographic) continue;
                    _helpers.Add(new Helper { C = c, Fov = c.fieldOfView, Near = c.nearClipPlane, Far = c.farClipPlane });
                    Plugin.L.LogInfo("CameraMod: first person: helper camera '" + CamDiag.PathOf(c.transform) + "' (fov " + CamDiag.F(c.fieldOfView) + ", near "
                                     + CamDiag.F(c.nearClipPlane) + ", far " + CamDiag.F(c.farClipPlane) + ") follows the first-person fov/near/far");
                }
            }
            catch (Exception ex) { Plugin.L.LogWarning("CameraMod: helper cameras: " + ex.Message); }
            SyncHelpers();
        }

        if (Plugin.HideCameraAttachedRenderers.Value)
        {
            try
            {
                foreach (var r in cam.GetComponentsInChildren<Renderer>(true))
                {
                    if (r == null || !r.enabled) continue;
                    string tn = r.GetIl2CppType().Name;
                    // particles / trails / lines under the camera are weather or screen effects: kept
                    if (tn != "MeshRenderer" && tn != "SkinnedMeshRenderer" && tn != "SpriteRenderer") { Plugin.L.LogInfo("CameraMod: first person: camera-attached " + tn + " '" + r.name + "' kept"); continue; }
                    r.enabled = false;
                    _hiddenAttached.Add(r);
                    Plugin.L.LogInfo("CameraMod: first person: disabled camera-attached renderer '" + r.name + "' (" + r.GetIl2CppType().Name + ") " + CamDiag.PathOf(r.transform));
                }
            }
            catch (Exception ex) { Plugin.L.LogWarning("CameraMod: camera-attached renderers: " + ex.Message); }
        }

        string names = Plugin.DisableCameraComponents.Value ?? "";
        if (names.Trim().Length > 0)
        {
            var set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var s in names.Split(',', ';')) if (s.Trim().Length > 0) set.Add(s.Trim());
            foreach (var b in CameraBehaviours(cam))
            {
                string tn = b.GetIl2CppType().Name;
                if (!set.Contains(tn) || !b.enabled) continue;
                b.enabled = false;
                _disabledComps.Add(b);
                Plugin.L.LogInfo("CameraMod: first person: disabled camera component " + tn + " (DisableCameraComponents)");
            }
        }

        if (Plugin.CameraDiagnostics.Value)
        {
            try { CamDiag.DumpAll("first person entered", cam, player); } catch (Exception ex) { Plugin.L.LogWarning("CameraMod: camera dump failed: " + ex.Message); }
            _dumpFrame = Time.frameCount + 60;
        }
        Plugin.L.LogInfo("CameraMod: first person view: " + CamDiag.Short(cam) + "; bisect key " + Plugin.DiagKey.Value);
    }

    private static IEnumerable<Behaviour> CameraBehaviours(Camera cam)
    {
        var list = new List<Behaviour>();
        try
        {
            foreach (var comp in cam.gameObject.GetComponents(Il2CppInterop.Runtime.Il2CppType.Of<Behaviour>()))
            {
                var b = comp.TryCast<Behaviour>();
                if (b != null) list.Add(b);
            }
        }
        catch (Exception ex) { Plugin.L.LogWarning("CameraMod: camera components: " + ex.Message); }
        return list;
    }

    private static void ForceViewport(bool log)
    {
        if (!Plugin.ForceFullViewport.Value || _cam == null) return;
        try
        {
            var r = _cam.rect;
            if (CamDiag.IsFull(r)) return;
            _cam.rect = new Rect(0, 0, 1, 1);
            if (log || _watchLogs++ < 5) Plugin.L.LogInfo("CameraMod: first person: camera rect " + CamDiag.R(r) + " -> full screen (ForceFullViewport)");
        }
        catch (Exception ex) { Plugin.L.LogWarning("CameraMod: force viewport: " + ex.Message); }
    }

    private static void SyncHelpers()
    {
        if (_cam == null) return;
        for (int i = 0; i < _helpers.Count; i++)
        {
            var c = _helpers[i].C;
            if (c == null) continue;
            if (Math.Abs(c.fieldOfView - _cam.fieldOfView) > 0.01f) c.fieldOfView = _cam.fieldOfView;
            if (Math.Abs(c.nearClipPlane - _cam.nearClipPlane) > 1e-4f) c.nearClipPlane = _cam.nearClipPlane;
            if (Math.Abs(c.farClipPlane - _cam.farClipPlane) > 0.01f) c.farClipPlane = _cam.farClipPlane;
        }
    }

    /// <summary>Per frame in first person, after the camera pose and fov are final.</summary>
    internal static void Update()
    {
        if (!_active || _cam == null) return;
        int frame = Time.frameCount;
        // v7: the only game writer of Camera.rect (UIViewport.LateUpdate) is already blocked by a prefix, and the helper
        // cameras only change when the mod changes fov/near/far: both are re-checked every 10 frames instead of every frame
        if (frame % 10 == 0)
        {
            if (!_bisectNoGuard) ForceViewport(false);
            if (_helpers.Count > 0 && !_bisectNoGuard) { try { SyncHelpers(); } catch (Exception ex) { _helpers.Clear(); Plugin.L.LogWarning("CameraMod: helper camera sync stopped: " + ex.Message); } }
        }

        if (Plugin.CameraDiagnostics.Value)
        {
            if (_dumpFrame > 0 && Time.frameCount >= _dumpFrame)
            {
                _dumpFrame = -1;
                try { CamDiag.DumpAll("first person +1s", _cam, _player); } catch (Exception ex) { Plugin.L.LogWarning("CameraMod: camera dump failed: " + ex.Message); }
            }
            if ((frame % 120) == 0)
            {
                string w = Watch();
                if (w != _lastWatch)
                {
                    if (_lastWatch.Length > 0 && _watchLogs++ < 20) Plugin.L.LogInfo("CameraMod: first person: camera state changed: " + w);
                    _lastWatch = w;
                }
            }
        }

        try
        {
            if (Input.GetKeyDown(Plugin.DiagKey.Value)) NextStep();
        }
        catch (Exception ex) { Plugin.L.LogWarning("CameraMod: bisect step failed: " + ex); }
    }

    private static string Watch()
    {
        try
        {
            string t = _cam.targetTexture == null ? "screen" : _cam.targetTexture.name + " " + _cam.targetTexture.width + "x" + _cam.targetTexture.height;
            return "rect " + CamDiag.R(_cam.rect) + " pixelRect " + CamDiag.R(_cam.pixelRect) + " target " + t + " " + CamDiag.Projection(_cam, out _, out _);
        }
        catch { return ""; }
    }

    // ---------------- bisect ----------------

    private static void BuildSteps()
    {
        _steps = new List<string> { "all first-person changes on" };
        _steps.Add("game FOV " + CamDiag.F(CameraLogic.SavedFov) + " (instead of " + CamDiag.F(Plugin.FpFov.Value) + ")");
        _steps.Add("game near clip " + CamDiag.F(CameraLogic.SavedNear));
        _steps.Add("game far clip " + CamDiag.F(CameraLogic.SavedFar));
        _steps.Add("sky off");
        _steps.Add("game fog");
        _steps.Add("game layerCullDistances");
        _steps.Add("helper cameras not synced + viewport guard off");
        _stepComps = new List<Behaviour>();
        foreach (var b in CameraBehaviours(_cam))
        {
            if (!b.enabled) continue;
            string tn = b.GetIl2CppType().Name;
            if (tn == "Camera" || tn == "StatusOverlay") continue;
            _stepComps.Add(b);
            _steps.Add("camera component " + tn + " disabled");
        }
    }

    private const int FirstCompStep = 8;

    private static void NextStep()
    {
        if (_steps == null) BuildSteps();
        Apply(_step, false);
        _step = (_step + 1) % _steps.Count;
        Apply(_step, true);
        string msg = "bisect " + _step + "/" + (_steps.Count - 1) + ": " + _steps[_step] + " (" + Plugin.DiagKey.Value + " = next)";
        StatusOverlay.Show(msg);
        Plugin.L.LogInfo("CameraMod: bisect " + _step + " state: " + CamDiag.Short(_cam));
    }

    private static void Apply(int step, bool on)
    {
        var cam = _cam;
        switch (step)
        {
            case 0: return;
            case 1:
                KeepGameFov = on;
                if (CameraLogic.SavedFov > 1f) cam.fieldOfView = on ? CameraLogic.SavedFov : Math.Clamp(Plugin.FpFov.Value > 1f ? Plugin.FpFov.Value : CameraLogic.SavedFov, 30f, 120f);
                return;
            case 2:
                cam.nearClipPlane = on ? CameraLogic.SavedNear : Plugin.NearClip.Value;
                return;
            case 3:
                cam.farClipPlane = on ? CameraLogic.SavedFar : (Perf.FarClipMayLower ? Perf.FarClip : Math.Max(CameraLogic.SavedFar, Perf.FarClip));
                return;
            case 4:
                if (on) FpSky.Exit(); else FpSky.Enter(cam);
                return;
            case 5:
                if (on) FogControl.Exit(); else FogControl.Enter(cam);
                return;
            case 6:
                if (on) { if (CameraLogic.SavedCull != null) cam.layerCullDistances = CameraLogic.SavedCull; }
                else if (CameraLogic.LayerCullCleared && CameraLogic.SavedCull != null) cam.layerCullDistances = new Il2CppStructArray<float>(32);
                return;
            case 7:
                _bisectNoGuard = on;
                if (on)
                {
                    foreach (var hlp in _helpers) if (hlp.C != null) { hlp.C.fieldOfView = hlp.Fov; hlp.C.nearClipPlane = hlp.Near; hlp.C.farClipPlane = hlp.Far; }
                    if (_rectSaved) cam.rect = _savedRect;
                }
                return;
            default:
                int i = step - FirstCompStep;
                if (_stepComps == null || i < 0 || i >= _stepComps.Count || _stepComps[i] == null) return;
                _stepComps[i].enabled = !on;
                return;
        }
    }

    private static bool _bisectNoGuard;
    internal static bool GuardsOff => _bisectNoGuard || _step != 0;

    internal static void Exit()
    {
        if (!_active) return;
        // undo the bisect step; near/far/fov/cull/sky/fog are restored to the game's values by CameraLogic right after
        try { if (_step >= FirstCompStep && _steps != null) Apply(_step, false); } catch { }
        _step = 0; KeepGameFov = false; _bisectNoGuard = false;
        _active = false;
        foreach (var h in _helpers)
        {
            try { if (h.C != null) { h.C.fieldOfView = h.Fov; h.C.nearClipPlane = h.Near; h.C.farClipPlane = h.Far; } } catch { }
        }
        _helpers.Clear();
        foreach (var r in _hiddenAttached) { try { if (r != null) r.enabled = true; } catch { } }
        _hiddenAttached.Clear();
        foreach (var b in _disabledComps) { try { if (b != null) b.enabled = true; } catch { } }
        _disabledComps.Clear();
        try { if (_rectSaved && _cam != null) _cam.rect = _savedRect; } catch { }
        _rectSaved = false;
        _cam = null; _player = null; _dumpFrame = -1;
    }
}
