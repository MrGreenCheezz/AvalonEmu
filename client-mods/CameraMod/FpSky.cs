using System;
using Il2CppInterop.Runtime;
using Il2CppInterop.Runtime.InteropTypes.Arrays;
using UnityEngine;

namespace CameraMod;

public enum SkyMode { Off, Auto, Procedural, Dome }

/// <summary>
/// Sky for first person. The game is top-down: the main camera clears to a solid background colour and the scene has no
/// skybox (Atmosphere only drives ambient/fog colours, CloudSettings only cloud *shadows*). The build ships no Skybox/*
/// shader in unity_builtin_extra (only the names in globalgamemanagers' built-in list), so:
///  (a) Procedural: Shader.Find("Skybox/Procedural") - used when it exists at runtime: material tinted from config/fog,
///      RenderSettings.skybox + camera.clearFlags = Skybox, sun = brightest directional light.
///  (b) Dome (fallback): a generated sphere mesh drawn with Graphics.DrawMesh for the main camera only, around the eye,
///      radius 0.9 x far clip, shader "Hidden/Internal-Colored" (always in 'unity default resources'; vertex colour,
///      _ZWrite 0 / _ZTest LEqual / _Cull Off set on the material, Background queue) with a vertex-colour gradient
///      horizon = fog colour (so distant terrain fades into it) -> zenith = SkyZenithColor, plus a sun glow; optional cloud
///      layer: second dome with a runtime-generated tileable fBm noise texture (Sprites/Default, alpha blended), scrolling.
/// Everything is restored on exit (clearFlags, backgroundColor, RenderSettings.skybox/sun).
/// </summary>
internal static class FpSky
{
    private static bool _active, _procedural, _broken;
    private static Camera _cam;
    private static CameraClearFlags _savedFlags;
    private static Color _savedBg;
    private static Material _savedSkybox;
    private static Light _savedSun;
    private static Material _skyMat, _domeMat, _cloudMat;
    private static Mesh _dome, _cloudDome;
    private static Texture2D _cloudTex;
    private static Light _sun;
    private static Vector2[] _cloudUv;
    private static int _cloudFrame;
    private static Il2CppStructArray<Vector2> _uvBuf;
    private static Color _lastHorizon = new Color(-1, 0, 0), _lastZenith;
    private static Vector3 _lastSunDir;
    private static int _layer;
    private const int Rings = 24, Segs = 48;

    internal static string Enter(Camera cam)
    {
        _active = false;
        var mode = Plugin.SkyModeCfg.Value;
        if (!Plugin.SkyEnabled.Value) return "sky off (FirstPersonSky.Enabled = false)";
        if (mode == SkyMode.Off || cam == null || _broken) return "sky off";
        _cam = cam;
        try
        {
            _savedFlags = cam.clearFlags;
            _savedBg = cam.backgroundColor;
            _savedSkybox = RenderSettings.skybox;
            _savedSun = RenderSettings.sun;
            _sun = FindSun();
            _layer = PickLayer(cam);
            string info = $"camera clear={_savedFlags}, bg={Hex(_savedBg)}, RenderSettings.skybox={(_savedSkybox != null ? _savedSkybox.name : "none")}, sun={(_sun != null ? _sun.name : "none")}";

            _procedural = false;
            if (mode == SkyMode.Auto || mode == SkyMode.Procedural)
            {
                var sh = Shader.Find("Skybox/Procedural");
                if (sh != null && sh.isSupported)
                {
                    _skyMat ??= new Material(sh) { hideFlags = HideFlags.HideAndDontSave };
                    UpdateProcedural();
                    RenderSettings.skybox = _skyMat;
                    if (_sun != null) RenderSettings.sun = _sun;
                    cam.clearFlags = CameraClearFlags.Skybox;
                    _procedural = true;
                    info += "; using Skybox/Procedural";
                }
                else info += $"; Skybox/Procedural {(sh == null ? "not in build" : "not supported")} -> dome";
            }
            if (!_procedural)
            {
                if (!BuildDome(out string why)) { Plugin.L.LogWarning("CameraMod: sky dome unavailable: " + why); return info + "; dome unavailable: " + why; }
                info += "; dome (" + why + ")";
            }
            _active = true;
            Plugin.L.LogInfo("CameraMod: first-person sky: " + info);
            return _procedural ? "sky procedural" : "sky dome";
        }
        catch (Exception ex) { _broken = true; Plugin.L.LogError("CameraMod: sky setup failed, disabled: " + ex); Restore(); return "sky failed"; }
    }

    // Managed only: UnityEngine.ColorUtility goes through Il2CppSystem.String.Format, which crashes the client
    // (AccessViolation in il2cpp_gchandle_get_target) under this BepInEx build.
    private static string Hex(Color c) => "#" + B(c.r).ToString("X2") + B(c.g).ToString("X2") + B(c.b).ToString("X2");

    private static int B(float v) => (int)Math.Round(Math.Clamp(v, 0f, 1f) * 255f);

    private static Light FindSun()
    {
        try
        {
            if (RenderSettings.sun != null) return RenderSettings.sun;
            Light best = null;
            foreach (var o in UnityEngine.Object.FindObjectsOfType(Il2CppType.Of<Light>()))
            {
                var l = o.TryCast<Light>();
                if (l == null || !l.isActiveAndEnabled || l.type != LightType.Directional) continue;
                if (best == null || l.intensity > best.intensity) best = l;
            }
            return best;
        }
        catch (Exception ex) { Plugin.L.LogWarning("CameraMod: sun light search: " + ex.Message); return null; }
    }

    private static int PickLayer(Camera cam)
    {
        int mask = cam.cullingMask;
        if ((mask & 1) != 0) return 0;
        for (int i = 0; i < 32; i++) if ((mask & (1 << i)) != 0) return i;
        return 0;
    }

    private static Shader FindShader(params string[] names)
    {
        foreach (var n in names) { var s = Shader.Find(n); if (s != null && s.isSupported) return s; }
        return null;
    }

    private static bool BuildDome(out string why)
    {
        if (_domeMat == null)
        {
            var sh = FindShader("Hidden/Internal-Colored", "Sprites/Default", "Unlit/Color");
            if (sh == null) { why = "no unlit shader (Hidden/Internal-Colored, Sprites/Default, Unlit/Color) found"; return false; }
            _domeMat = new Material(sh) { hideFlags = HideFlags.HideAndDontSave };
            TrySet(_domeMat, "_SrcBlend", 1); TrySet(_domeMat, "_DstBlend", 0);
            TrySet(_domeMat, "_ZWrite", 0); TrySet(_domeMat, "_ZTest", 4); TrySet(_domeMat, "_Cull", 0);
            _domeMat.renderQueue = 1000;
            Plugin.L.LogInfo($"CameraMod: sky dome shader '{sh.name}'");
        }
        _dome ??= MakeDome(false);
        if (Plugin.SkyClouds.Value && _cloudMat == null)
        {
            var csh = FindShader("Sprites/Default", "Legacy Shaders/Particles/Alpha Blended", "Unlit/Transparent Colored");
            if (csh == null) Plugin.L.LogWarning("CameraMod: sky clouds: no alpha-blended textured shader found, clouds off");
            else
            {
                _cloudTex ??= MakeCloudTexture(256);
                _cloudMat = new Material(csh) { hideFlags = HideFlags.HideAndDontSave, mainTexture = _cloudTex };
                _cloudMat.renderQueue = 1001;
                _cloudDome ??= MakeDome(true);
                Plugin.L.LogInfo($"CameraMod: sky cloud shader '{csh.name}'");
            }
        }
        why = $"{_domeMat.shader.name}{(_cloudMat != null && Plugin.SkyClouds.Value ? " + clouds" : "")}, layer {_layer}";
        return true;
    }

    private static void TrySet(Material m, string p, float v) { try { if (m.HasProperty(p)) m.SetFloat(p, v); } catch { } }

    /// <summary>UV sphere, Rings x Segs, radius 1. Cloud variant: upper hemisphere with planar-projected UVs.</summary>
    private static Mesh MakeDome(bool clouds)
    {
        int rings = clouds ? Rings / 2 : Rings;
        var verts = new Vector3[(rings + 1) * (Segs + 1)];
        var uv = new Vector2[verts.Length];
        var cols = new Color[verts.Length];
        int k = 0;
        for (int r = 0; r <= rings; r++)
        {
            // elevation: full sphere -90..90, cloud dome 0..90
            float el = clouds ? (float)r / rings * 90f : -90f + 180f * r / rings;
            float elr = el * Mathf.Deg2Rad;
            for (int s = 0; s <= Segs; s++, k++)
            {
                float az = (float)s / Segs * 2f * Mathf.PI;
                var d = new Vector3(Mathf.Cos(elr) * Mathf.Sin(az), Mathf.Sin(elr), Mathf.Cos(elr) * Mathf.Cos(az));
                verts[k] = d;
                if (clouds)
                {
                    float y = Math.Max(0.06f, d.y);
                    uv[k] = new Vector2(d.x / y, d.z / y) * 0.25f;
                    float a = Mathf.Clamp01((d.y - 0.03f) / 0.25f);
                    cols[k] = new Color(1, 1, 1, a);
                }
                else cols[k] = Color.white;
            }
        }
        var tris = new int[rings * Segs * 6];
        int t = 0;
        for (int r = 0; r < rings; r++)
            for (int s = 0; s < Segs; s++)
            {
                int a = r * (Segs + 1) + s, b = a + Segs + 1;
                // inward facing (viewed from the centre); the materials also use Cull Off where possible
                tris[t++] = a; tris[t++] = a + 1; tris[t++] = b;
                tris[t++] = b; tris[t++] = a + 1; tris[t++] = b + 1;
            }
        var m = new Mesh { hideFlags = HideFlags.HideAndDontSave, name = clouds ? "CameraMod.CloudDome" : "CameraMod.SkyDome" };
        if (clouds) _cloudUv = uv;
        m.vertices = verts;
        m.uv = uv;
        m.colors = cols;
        m.triangles = tris;
        m.bounds = new Bounds(Vector3.zero, Vector3.one * 2f);
        return m;
    }

    // ---- tileable value-noise fBm ----
    private static float Hash(int x, int y, int p)
    {
        x = ((x % p) + p) % p; y = ((y % p) + p) % p;
        uint h = (uint)(x * 374761393 + y * 668265263);
        h = (h ^ (h >> 13)) * 1274126177u;
        return ((h ^ (h >> 16)) & 0xFFFF) / 65535f;
    }

    private static float Noise(float x, float y, int p)
    {
        int xi = (int)Math.Floor(x), yi = (int)Math.Floor(y);
        float xf = x - xi, yf = y - yi;
        float u = xf * xf * (3 - 2 * xf), v = yf * yf * (3 - 2 * yf);
        float a = Hash(xi, yi, p), b = Hash(xi + 1, yi, p), c = Hash(xi, yi + 1, p), d = Hash(xi + 1, yi + 1, p);
        return a + (b - a) * u + (c - a) * v + (a - b - c + d) * u * v;
    }

    private static Texture2D MakeCloudTexture(int size)
    {
        var px = new Color[size * size];
        float cover = Mathf.Clamp01(Plugin.SkyCloudCover.Value);
        for (int y = 0; y < size; y++)
            for (int x = 0; x < size; x++)
            {
                float n = 0, amp = 0.5f, tot = 0;
                int period = 4;
                for (int o = 0; o < 5; o++)
                {
                    float fx = (float)x / size * period, fy = (float)y / size * period;
                    n += Noise(fx, fy, period) * amp; tot += amp;
                    amp *= 0.5f; period *= 2;
                }
                n /= tot;
                float a = Mathf.Clamp01((n - (1f - cover)) / 0.35f);
                a = a * a * (3 - 2 * a);
                float shade = 0.85f + 0.15f * n;
                px[y * size + x] = new Color(shade, shade, shade, a * 0.9f);
            }
        var tex = new Texture2D(size, size, TextureFormat.RGBA32, true) { hideFlags = HideFlags.HideAndDontSave, wrapMode = TextureWrapMode.Repeat, name = "CameraMod.Clouds" };
        tex.SetPixels(px);
        tex.Apply(true);
        return tex;
    }

    private static Color Horizon() => Plugin.SkyHorizonFromFog.Value ? RenderSettings.fogColor : ParseColor(Plugin.SkyHorizonColor.Value, new Color(0.75f, 0.82f, 0.9f));
    private static Color Zenith() => ParseColor(Plugin.SkyZenithColor.Value, new Color(0.29f, 0.5f, 0.78f));

    private static Color ParseColor(string s, Color def)
    {
        if (string.IsNullOrWhiteSpace(s)) return def;
        s = s.Trim();
        if (s.StartsWith("#")) s = s[1..];
        // Managed parse of RRGGBB or RRGGBBAA (no ColorUtility: see Hex).
        if ((s.Length != 6 && s.Length != 8) || !uint.TryParse(s, System.Globalization.NumberStyles.HexNumber, null, out var v)) return def;
        if (s.Length == 6) v = (v << 8) | 0xFF;
        return new Color(((v >> 24) & 0xFF) / 255f, ((v >> 16) & 0xFF) / 255f, ((v >> 8) & 0xFF) / 255f, (v & 0xFF) / 255f);
    }

    private static Vector3 SunDir() => _sun != null ? -_sun.transform.forward : new Vector3(0.3f, 0.6f, 0.3f).normalized;

    private static void UpdateProcedural()
    {
        var z = Zenith();
        _skyMat.SetColor("_SkyTint", z);
        _skyMat.SetColor("_GroundColor", Horizon());
        if (_skyMat.HasProperty("_Exposure")) _skyMat.SetFloat("_Exposure", 1.2f);
        if (_skyMat.HasProperty("_AtmosphereThickness")) _skyMat.SetFloat("_AtmosphereThickness", 1f);
        if (_skyMat.HasProperty("_SunSize")) _skyMat.SetFloat("_SunSize", 0.04f);
    }

    private static void Recolour(Color hor, Color zen, Vector3 sun)
    {
        var verts = _dome.vertices;
        var cols = new Color[verts.Length];
        float glow = Math.Max(0f, Plugin.SkySunGlow.Value);
        Color sunCol = _sun != null ? _sun.color : new Color(1f, 0.95f, 0.85f);
        for (int i = 0; i < verts.Length; i++)
        {
            Vector3 d = verts[i];
            float t = d.y <= 0 ? 0 : Mathf.Pow(d.y, 0.55f);
            Color c = Color.Lerp(hor, zen, t);
            float s = Mathf.Max(0, Vector3.Dot(d, sun));
            c += sunCol * (glow * Mathf.Pow(s, 24f) + glow * 0.35f * Mathf.Pow(s, 4f));
            c.a = 1;
            cols[i] = c;
        }
        _dome.colors = cols;
    }

    /// <summary>Per frame in first person (after the camera pose is final).</summary>
    internal static void Update(Vector3 eye)
    {
        if (!_active || _cam == null) return;
        try
        {
            if (_procedural)
            {
                // v7: material colours only when the fog/zenith colour actually changed (checked every 60 frames)
                if (Time.frameCount % 60 == 0)
                {
                    Color h = Horizon(), z = Zenith();
                    if (Diff(h, _lastHorizon) > 0.01f || Diff(z, _lastZenith) > 0.01f) { _lastHorizon = h; _lastZenith = z; UpdateProcedural(); }
                }
                return;
            }
            Color hor = Horizon(), zen = Zenith();
            Vector3 sun = SunDir();
            if (Diff(hor, _lastHorizon) > 0.01f || Diff(zen, _lastZenith) > 0.01f || (sun - _lastSunDir).sqrMagnitude > 1e-4f)
            {
                _lastHorizon = hor; _lastZenith = zen; _lastSunDir = sun;
                Recolour(hor, zen, sun);
                _cam.backgroundColor = hor;
            }
            float r = Math.Max(50f, Math.Min(_cam.farClipPlane * 0.9f, Plugin.SkyRadius.Value));
            Graphics.DrawMesh(_dome, Matrix4x4.TRS(eye, Quaternion.identity, Vector3.one * r), _domeMat, _layer, _cam);
            if (_cloudMat != null && Plugin.SkyClouds.Value)
            {
                float sp = Plugin.SkyCloudSpeed.Value * 0.002f * Time.time;
                // Sprites/Default ignores _MainTex_ST, so the cloud drift is done in the mesh UVs (every 2nd frame)
                if (_cloudUv != null && (++_cloudFrame & 3) == 0)
                {
                    // v7: one native buffer reused (written in place, no managed array + copy per update), every 4th frame
                    var off = new Vector2(sp, sp * 0.6f);
                    _uvBuf ??= new Il2CppStructArray<Vector2>(_cloudUv.Length);
                    for (int i = 0; i < _cloudUv.Length; i++) _uvBuf[i] = _cloudUv[i] + off;
                    _cloudDome.uv = _uvBuf;
                }
                _cloudMat.color = Color.Lerp(Color.white, hor, 0.25f);
                Graphics.DrawMesh(_cloudDome, Matrix4x4.TRS(eye, Quaternion.identity, new Vector3(r * 0.98f, r * 0.35f, r * 0.98f)), _cloudMat, _layer, _cam);
            }
        }
        catch (Exception ex) { _broken = true; _active = false; Plugin.L.LogError("CameraMod: sky update failed, disabled: " + ex); Restore(); }
    }

    private static float Diff(Color a, Color b) => Math.Abs(a.r - b.r) + Math.Abs(a.g - b.g) + Math.Abs(a.b - b.b);

    private static void Restore()
    {
        try
        {
            if (_cam != null) { _cam.clearFlags = _savedFlags; _cam.backgroundColor = _savedBg; }
            if (_procedural) { RenderSettings.skybox = _savedSkybox; RenderSettings.sun = _savedSun; }
        }
        catch (Exception ex) { Plugin.L.LogWarning("CameraMod: sky restore: " + ex.Message); }
    }

    internal static void Exit()
    {
        if (!_active) { _cam = null; return; }
        _active = false;
        Restore();
        _procedural = false;
        _cam = null;
        _lastHorizon = new Color(-1, 0, 0);
    }
}
