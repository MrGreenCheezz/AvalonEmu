using System;
using BepInEx;
using BepInEx.Configuration;
using BepInEx.Logging;
using BepInEx.Unity.IL2CPP;
using HarmonyLib;
using UnityEngine;

namespace GraphicsMod;

public enum Preset { Config, UltraShadows }
public enum ResSetting { Game, Low, Medium, High, VeryHigh }
public enum ProjSetting { Game, CloseFit, StableFit }
public enum AnisoSetting { Game, Disable, Enable, ForceEnable }
public enum AaSetting { Game, Off, FXAA, SMAA, TAA }
public enum LowCamPreset { Config, Performance }

/// <summary>
/// Graphics mod for the local client: shadow distance / cascades / resolution / soft shadows, LOD bias, anisotropic
/// filtering and post-process anti-aliasing beyond the game's options. Built-in render pipeline, deferred shading
/// (see NOTES.md). The game re-applies its options in GraphicOptions.d() (starts with QualitySettings.SetQualityLevel)
/// and lerps the shadow distance with the camera zoom in GraphicOptions.b(float, bool, Vector3, Vector3); both get
/// Harmony postfixes that re-apply our values, plus a cheap per-frame check in ActorCameraController.LateUpdate.
/// F9 switches the mod values on/off (the game's own values are restored) for A/B comparison.
/// </summary>
[BepInPlugin("albionlocal.graphics", "AlbionLocal GraphicsMod", "0.2.0")]
public class Plugin : BasePlugin
{
    internal static ManualLogSource L;

    internal static ConfigEntry<bool> Enabled, StartActive;
    internal static ConfigEntry<KeyCode> ToggleKey;
    internal static ConfigEntry<Preset> PresetCfg;

    internal static ConfigEntry<float> ShadowDistance, ShadowDistanceFirstPerson, FirstPersonHeight;
    internal static ConfigEntry<int> ShadowCascades;
    internal static ConfigEntry<ResSetting> ShadowResolution, LightShadowResolution;
    internal static ConfigEntry<ProjSetting> ShadowProjection;
    internal static ConfigEntry<bool> SoftShadows, ForceShadowsOn, CharacterShadows;
    internal static ConfigEntry<string> CascadeSplits;
    internal static ConfigEntry<float> ShadowStrength, ShadowBias, ShadowNormalBias;

    internal static ConfigEntry<float> LodBias;
    internal static ConfigEntry<int> PixelLightCount;
    internal static ConfigEntry<AnisoSetting> Anisotropic;
    internal static ConfigEntry<AaSetting> AntiAliasing;
    internal static ConfigEntry<LowCamPreset> LowCameraPreset;
    internal static ConfigEntry<float> LowCameraShadowDistance, ProfileIntervalSec;
    internal static ConfigEntry<bool> ProfileLog;

    public override void Load()
    {
        L = Log;
        Bind(Config);
        if (!Enabled.Value) { L.LogInfo("GraphicsMod: disabled in config (General.Enabled = false)"); return; }

        try { AddComponent<Driver>(); L.LogInfo("GraphicsMod: driver component injected"); }
        catch (Exception ex) { L.LogWarning("GraphicsMod: class injection failed (" + ex.GetType().Name + ": " + ex.Message + "); toggle key and menu-time apply unavailable, in-game hooks still work"); }

        var h = new Harmony("albionlocal.graphics");
        TryPatch(h, typeof(ActorCameraController), "LateUpdate", Type.EmptyTypes, nameof(CamLateUpdatePostfix), "ActorCameraController.LateUpdate postfix (per-frame check)");
        TryPatch(h, typeof(GraphicOptions), "d", Type.EmptyTypes, nameof(OptionsApplyPostfix), "GraphicOptions.d() postfix (game applies its graphics options, VA 0x1818A6E90)");
        TryPatch(h, typeof(GraphicOptions), "b", new[] { typeof(float), typeof(bool), typeof(Vector3), typeof(Vector3) }, nameof(ZoomShadowPostfix), "GraphicOptions.b(float,bool,Vector3,Vector3) postfix (zoom-dependent shadow distance/splits, VA 0x1818A6110)");
        TryPatch(h, typeof(GraphicOptions), "ag", Type.EmptyTypes, nameof(PostFxPostfix), "GraphicOptions.ag() postfix (post-processing / SMAA, VA 0x1818A38A0)");
        TryPatch(h, typeof(QualitySettingsOverwrite), "ApplyShadowSettings", Type.EmptyTypes, nameof(QsoPostfix), "QualitySettingsOverwrite.ApplyShadowSettings postfix");
        TryPatch(h, typeof(QualitySettingsOverwrite), "ResetShadowSettings", Type.EmptyTypes, nameof(QsoPostfix), "QualitySettingsOverwrite.ResetShadowSettings postfix");
        GfxLogic.Active = StartActive.Value;
        L.LogInfo("GraphicsMod: loaded, preset " + PresetCfg.Value + ", low camera " + LowCameraPreset.Value + ", toggle key " + ToggleKey.Value + ", start " + (GfxLogic.Active ? "ON" : "OFF"));
    }

    private static void Bind(ConfigFile c)
    {
        Enabled = c.Bind("General", "Enabled", true, "Master switch. false = the mod does nothing (no hooks).");
        StartActive = c.Bind("General", "StartActive", true, "Mod values active at start. The toggle key switches between mod values and the game's own values.");
        ToggleKey = c.Bind("General", "ToggleKey", KeyCode.F9, "Switch mod graphics values on/off (A/B comparison). Off restores the game's values. None = no key.");
        PresetCfg = c.Bind("General", "Preset", Preset.Config, "Config = use the values below. UltraShadows = shadow distance >= 300 (first person >= 400), 4 cascades, VeryHigh, soft, StableFit, tighter cascade splits, sun shadow map VeryHigh, LodBias >= 3.");

        ShadowDistance = c.Bind("Shadows", "ShadowDistance", 150f, "Shadow draw distance in metres (QualitySettings.shadowDistance). The game uses its option value and lowers it when zoomed in. 0 = game.");
        ShadowDistanceFirstPerson = c.Bind("Shadows", "ShadowDistanceFirstPerson", 150f, "Shadow distance while the camera is low (first person / over the shoulder, see FirstPersonHeight). 0 = same as ShadowDistance. Performance.LowCameraPreset = Performance caps it at LowCameraShadowDistance.");
        FirstPersonHeight = c.Bind("Shadows", "FirstPersonHeight", 5f, "Camera height above the player (m) below which ShadowDistanceFirstPerson is used (works with any camera mod, it only measures the camera).");
        ShadowCascades = c.Bind("Shadows", "ShadowCascades", 4, "Directional shadow cascades: 1, 2 or 4 (more = sharper near shadows over a long distance). 0 = game.");
        CascadeSplits = c.Bind("Shadows", "CascadeSplits", "0.04, 0.12, 0.35", "4-cascade split points as fractions of the shadow distance (three increasing numbers in 0..1). With 150 m: 6 / 18 / 52 m. Empty = game (it changes them with the zoom).");
        ShadowResolution = c.Bind("Shadows", "ShadowResolution", ResSetting.VeryHigh, "QualitySettings.shadowResolution. Game = keep.");
        ShadowProjection = c.Bind("Shadows", "ShadowProjection", ProjSetting.StableFit, "StableFit = no shimmering when the camera moves; CloseFit = sharper but shimmers. Game = keep.");
        SoftShadows = c.Bind("Shadows", "SoftShadows", true, "Soft shadows: QualitySettings.shadows = All and directional lights with hard shadows -> soft. false = keep the game's choice.");
        ForceShadowsOn = c.Bind("Shadows", "ForceShadowsOn", true, "Turn shadows on even if the game option has them off.");
        ShadowStrength = c.Bind("Shadows", "ShadowStrength", -1f, "Shadow darkness of directional lights 0..1 (the game sets it every frame from the time of day). -1 = game.");
        LightShadowResolution = c.Bind("Shadows", "LightShadowResolution", ResSetting.Game, "Per-light shadow map resolution of directional lights. Game = from QualitySettings (ShadowResolution).");
        ShadowBias = c.Bind("Shadows", "ShadowBias", -1f, "Directional light shadow bias (the game sets it from Atmosphere). -1 = game.");
        ShadowNormalBias = c.Bind("Shadows", "ShadowNormalBias", -1f, "Directional light shadow normal bias. -1 = game.");
        CharacterShadows = c.Bind("Shadows", "CharacterShadows", true, "Skinned meshes (characters, mobs, mounts) that have shadow casting Off get it On (scan every 5 s, restored by the toggle). The log says how many were found.");

        LodBias = c.Bind("Quality", "LodBias", 2f, "QualitySettings.lodBias (higher = detailed models farther away). Only raised, never lowered. 0 = game.");
        PixelLightCount = c.Bind("Quality", "PixelLightCount", 0, "QualitySettings.pixelLightCount (affects only forward-rendered objects; the game renders deferred). 0 = game.");
        Anisotropic = c.Bind("Quality", "AnisotropicFiltering", AnisoSetting.ForceEnable, "Texture anisotropic filtering. Game = keep.");
        LowCameraPreset = c.Bind("Performance", "LowCameraPreset", LowCamPreset.Performance, "Performance (default) = while the camera is low (first person / over the shoulder) the shadow distance is at most LowCameraShadowDistance: a low camera sees far more shadow casters than the top-down view, and 4 cascades over 250 m cost a lot of frame time. Config = use Shadows.ShadowDistanceFirstPerson as is. Ignored with Preset = UltraShadows.");
        LowCameraShadowDistance = c.Bind("Performance", "LowCameraShadowDistance", 120f, "Shadow distance cap for the low camera with LowCameraPreset = Performance (m).");
        ProfileLog = c.Bind("Performance", "ProfileLog", true, "Log one 'GraphicsMod perf' line every ProfileIntervalSec: ms per frame spent in the per-frame check and in the periodic scans (lights, character shadows).");
        ProfileIntervalSec = c.Bind("Performance", "ProfileIntervalSec", 10f, "Seconds per profiler log line.");
        AntiAliasing = c.Bind("Quality", "AntiAliasing", AaSetting.SMAA, "Post-process anti-aliasing on the main camera (PostProcessLayer). MSAA is not possible: the game renders with deferred shading. SMAA = SMAA High; FXAA; TAA (may smear); Off; Game = keep the game's SMAA option.");
    }

    private static void TryPatch(Harmony h, Type type, string method, Type[] args, string postfix, string what)
    {
        try
        {
            var m = AccessTools.Method(type, method, args);
            if (m == null) { L.LogWarning("GraphicsMod: hook NOT applied: " + what + " (method not found)"); return; }
            h.Patch(m, postfix: new HarmonyMethod(typeof(Plugin), postfix));
            L.LogInfo("GraphicsMod: hooked " + what);
        }
        catch (Exception ex) { L.LogWarning("GraphicsMod: hook NOT applied: " + what + ": " + ex.GetType().Name + ": " + ex.Message); }
    }

    private static void CamLateUpdatePostfix(ActorCameraController __instance)
    {
        long t0 = GfxLogic.PerfBegin();
        try { GfxLogic.AfterCameraLateUpdate(__instance); } catch (Exception ex) { GfxLogic.Fail("LateUpdate", ex); }
        GfxLogic.PerfEnd(0, t0);
    }
    private static void OptionsApplyPostfix(GraphicOptions __instance)
    {
        try { GfxLogic.AfterGameOptionsApplied(__instance); } catch (Exception ex) { GfxLogic.Fail("GraphicOptions.d", ex); }
    }
    private static void ZoomShadowPostfix(GraphicOptions __instance)
    {
        try { GfxLogic.AfterZoomShadow(__instance); } catch (Exception ex) { GfxLogic.Fail("GraphicOptions.b(zoom)", ex); }
    }
    private static void PostFxPostfix(GraphicOptions __instance)
    {
        try { GfxLogic.AfterPostFx(__instance); } catch (Exception ex) { GfxLogic.Fail("GraphicOptions.ag", ex); }
    }
    private static void QsoPostfix()
    {
        try { GfxLogic.AfterQso(); } catch (Exception ex) { GfxLogic.Fail("QualitySettingsOverwrite", ex); }
    }
}

/// <summary>Injected MonoBehaviour: toggle key, start-up log, apply while no follow camera exists (login / loading), scans.</summary>
public class Driver : MonoBehaviour
{
    public Driver(IntPtr ptr) : base(ptr) { }

    private int _frame;
    private bool _startLogged;

    public void Update()
    {
        _frame++;
        GfxLogic.DriverAlive = true;
        try
        {
            if (!_startLogged && _frame >= 60)
            {
                _startLogged = true;
                GfxLogic.LogEnvironment();
                GfxLogic.Log("at start (before mod)");
                if (GfxLogic.Active) { GfxLogic.Activate("start"); }
            }
            var k = Plugin.ToggleKey.Value;
            if (k != KeyCode.None && Input.GetKeyDown(k)) GfxLogic.Toggle();
            long t0 = GfxLogic.PerfBegin();
            GfxLogic.DriverTick(_frame);
            GfxLogic.PerfEnd(1, t0);
            GfxLogic.PerfFrame();
        }
        catch (Exception ex) { GfxLogic.Fail("Driver.Update", ex); }
    }
}
