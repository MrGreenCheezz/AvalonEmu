using System;
using UnityEngine;

namespace CameraMod;

public enum FpFog { Off, Reduced, Default }

/// <summary>
/// Distance fog / view distance while in first person.
/// The game's fog is plain Unity RenderSettings fog written by Atmosphere.ApplyFog (VA 0x18178D3B0), which Atmosphere.Update
/// calls every frame from the blended AtmosphereFrames key frame (density/start/end/colour), plus the shader keyword
/// _GLOBAL_FOG_DISTANCE_MOD (Atmosphere.ApplySeparateFogSettings, 0x18178E9A0) and, in deferred rendering only, the
/// GlobalFog image effect on the camera. A Harmony postfix on ApplyFog re-applies our override after every game write;
/// the per-frame camera postfix re-applies it as well (from the base values the game last wrote, so nothing compounds).
/// Everything is restored on leaving first person (the game's own ApplyFog/ApplySeparateFogSettings are re-run).
/// </summary>
internal static class FogControl
{
    private static bool _active;
    private static Atmosphere _atmo;
    private static bool _savedFogOn;
    private static float _baseStart, _baseEnd, _baseDensity;
    private static bool _haveBase;
    private static float _savedLodBias = -1;
    private static Behaviour _globalFog;
    private static bool _globalFogWasEnabled;
    private static bool _loggedHook;

    internal static FpFog Mode => Plugin.FirstPersonFog.Value;

    /// <summary>Harmony postfix of Atmosphere.ApplyFog: remember the instance and the values the game just wrote.</summary>
    internal static void AfterApplyFog(Atmosphere a)
    {
        _atmo = a;
        if (!_active && _loggedHook) return; // top-down: nothing to do (the base is captured again on entry)
        CaptureBase();
        if (!_loggedHook) { _loggedHook = true; Plugin.L.LogInfo($"CameraMod: Atmosphere.ApplyFog seen: fog={RenderSettings.fog}, mode={RenderSettings.fogMode}, start={_baseStart:0.#}, end={_baseEnd:0.#}, density={_baseDensity:0.####}"); }
        if (_active) Apply();
    }

    private static void CaptureBase()
    {
        _baseStart = RenderSettings.fogStartDistance;
        _baseEnd = RenderSettings.fogEndDistance;
        _baseDensity = RenderSettings.fogDensity;
        _haveBase = true;
    }

    internal static string Enter(Camera cam)
    {
        _active = true;
        _savedFogOn = RenderSettings.fog;
        CaptureBase();
        string info = $"fog {Mode} (game: {(RenderSettings.fog ? "on" : "off")}, {RenderSettings.fogMode}, {_baseStart:0}-{_baseEnd:0} m, d={_baseDensity:0.####})";

        _savedLodBias = -1;
        float lod = Perf.LodBias;
        if (lod > 0)
        {
            try { _savedLodBias = QualitySettings.lodBias; QualitySettings.lodBias = lod; info += $", lodBias {_savedLodBias:0.##}->{lod:0.##}"; }
            catch (Exception ex) { Plugin.L.LogWarning("CameraMod: lodBias: " + ex.Message); }
        }

        _globalFog = null;
        if (Mode == FpFog.Off && cam != null)
        {
            try
            {
                var gf = cam.GetComponent<GlobalFog>();
                if (gf != null) { _globalFog = gf; _globalFogWasEnabled = gf.enabled; gf.enabled = false; info += ", GlobalFog effect off"; }
            }
            catch (Exception ex) { Plugin.L.LogWarning("CameraMod: GlobalFog: " + ex.Message); }
        }
        Apply();
        return info;
    }

    /// <summary>Re-apply the override (called every frame in first person and after each Atmosphere.ApplyFog).</summary>
    internal static void Apply()
    {
        if (!_active || !_haveBase) return;
        switch (Mode)
        {
            case FpFog.Off:
                RenderSettings.fog = false;
                RenderSettings.fogStartDistance = 100000f;
                RenderSettings.fogEndDistance = 200000f;
                RenderSettings.fogDensity = 0f;
                try { Shader.DisableKeyword("_GLOBAL_FOG_DISTANCE_MOD"); } catch { }
                break;
            case FpFog.Reduced:
                float m = Math.Max(1f, Plugin.FogDistanceMultiplier.Value);
                RenderSettings.fogStartDistance = _baseStart * m;
                RenderSettings.fogEndDistance = _baseEnd * m;
                RenderSettings.fogDensity = _baseDensity / (m * m);
                break;
        }
    }

    internal static void Exit()
    {
        if (!_active) return;
        _active = false;
        try
        {
            RenderSettings.fog = _savedFogOn;
            if (_haveBase)
            {
                RenderSettings.fogStartDistance = _baseStart;
                RenderSettings.fogEndDistance = _baseEnd;
                RenderSettings.fogDensity = _baseDensity;
            }
            if (_atmo != null)
            {
                _atmo.ApplySeparateFogSettings(); // restores the _GLOBAL_FOG_DISTANCE_MOD keyword the game's way
                _atmo.ApplyFog();
            }
        }
        catch (Exception ex) { Plugin.L.LogWarning("CameraMod: fog restore: " + ex.Message); }
        try { if (_savedLodBias > 0) QualitySettings.lodBias = _savedLodBias; } catch { }
        try { if (_globalFog != null) _globalFog.enabled = _globalFogWasEnabled; } catch { }
        _globalFog = null;
    }
}
