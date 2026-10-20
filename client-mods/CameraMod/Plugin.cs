using System;
using BepInEx;
using BepInEx.Configuration;
using BepInEx.Logging;
using BepInEx.Unity.IL2CPP;
using HarmonyLib;
using KeyCode = UnityEngine.KeyCode;

namespace CameraMod;

/// <summary>
/// Camera mod for the local client: extended zoom, free orbit and an experimental first-person view.
/// It hooks ActorCameraController.LateUpdate (the game's follow camera, see NOTES.md) with a Harmony
/// prefix/postfix and only rewrites the camera transform after the game has computed it, so with nothing
/// pressed the camera behaves exactly like the stock one.
/// </summary>
[BepInPlugin("albionlocal.camera", "AlbionLocal CameraMod", "0.7.0")]
public class Plugin : BasePlugin
{
    internal static ManualLogSource L;

    // [General]
    internal static ConfigEntry<bool> Enabled;
    internal static ConfigEntry<bool> ShowStatus;
    // [Zoom]
    internal static ConfigEntry<bool> ExtendedZoom;
    internal static ConfigEntry<float> MaxDistanceMultiplier;
    internal static ConfigEntry<float> MinDistanceMultiplier;
    internal static ConfigEntry<float> ZoomStep;
    internal static ConfigEntry<float> ZoomSmoothing;
    // [Rotation]
    internal static ConfigEntry<bool> FreeRotation;
    internal static ConfigEntry<bool> AltRightDrag;
    internal static ConfigEntry<float> RotateSensitivity;
    internal static ConfigEntry<float> MinPitch;
    internal static ConfigEntry<float> MaxPitch;
    internal static ConfigEntry<float> PivotHeight;
    internal static ConfigEntry<KeyCode> ResetKey;
    // [FirstPerson]
    internal static ConfigEntry<KeyCode> FirstPersonKey;
    internal static ConfigEntry<bool> FirstPersonEnabled;
    internal static ConfigEntry<float> LookSensitivity;
    internal static ConfigEntry<LookMode> LookMode;
    // [FirstPersonControls] (v4)
    internal static ConfigEntry<KeyCode> FreeCursorKey, InteractKey, HintKey, RightMouseHotkey;
    internal static ConfigEntry<bool> AutoFreeCursor, InvertY, LeftClickAttack, ClickMoveInFp, ShowHintOnEnter;
    internal static ConfigEntry<float> PitchUpLimit, PitchDownLimit, AimMaxDistance;
    internal static ConfigEntry<RightMouseMode> RightMouse;
    // [FirstPersonCamera] (v4)
    internal static ConfigEntry<float> FpPositionSmoothingMs, FpHeadBob, FpEyeHeightAdaptSec, FpLookSmoothingMs, FpFov;
    internal static ConfigEntry<float> FpCosmeticRadius;
    internal static ConfigEntry<KeyCode> MouseLockKey;
    internal static ConfigEntry<bool> Crosshair;
    internal static ConfigEntry<bool> CrosshairOnlyWhenLocked;
    internal static ConfigEntry<float> CrosshairSize;
    internal static ConfigEntry<ShowBody> ShowBodyMode;
    internal static ConfigEntry<HideMode> HideRenderMode;
    internal static ConfigEntry<bool> DeepScan;
    internal static ConfigEntry<float> NearClip;
    internal static ConfigEntry<bool> DisableLayerCullDistances;
    internal static ConfigEntry<float> HeadUpOffset;
    internal static ConfigEntry<float> HeadForwardOffset;
    internal static ConfigEntry<float> FallbackEyeHeight;
    // [FirstPersonView]
    internal static ConfigEntry<FpFog> FirstPersonFog;
    internal static ConfigEntry<float> FogDistanceMultiplier;
    internal static ConfigEntry<float> FirstPersonFarClip;
    internal static ConfigEntry<float> LodBias;
    // [FirstPersonMove]
    internal static ConfigEntry<bool> KeyboardMove, BlockGameMoveKeys;
    internal static ConfigEntry<string> FpSpellKeys;
    internal static ConfigEntry<MoveMethod> MoveMethodCfg;
    // [FirstPersonEffects]
    internal static ConfigEntry<FpEffectsMode> FpHideEffects;
    internal static ConfigEntry<float> FpEffectRadius;
    internal static ConfigEntry<bool> FpHideOwnNameplate;
    // [FirstPersonSky]
    internal static ConfigEntry<SkyMode> SkyModeCfg;
    internal static ConfigEntry<string> SkyZenithColor, SkyHorizonColor;
    internal static ConfigEntry<bool> SkyHorizonFromFog, SkyClouds;
    internal static ConfigEntry<float> SkySunGlow, SkyCloudCover, SkyCloudSpeed, SkyRadius;
    internal static ConfigEntry<bool> SkyEnabled;
    // [FirstPersonDiagnostics] (v5)
    internal static ConfigEntry<bool> CameraDiagnostics, ForceFullViewport, SyncHelperCameras, HideCameraAttachedRenderers, CrosshairOutline;
    internal static ConfigEntry<string> DisableCameraComponents;
    internal static ConfigEntry<KeyCode> DiagKey;
    internal static ConfigEntry<KeyCode> MoveForwardKey, MoveBackKey, MoveLeftKey, MoveRightKey;
    internal static ConfigEntry<float> MoveIntervalMs, MoveLookAhead, MoveYawCompensation;
    // [ThirdPerson] (v6)
    internal static ConfigEntry<KeyCode> ModeKey, TpShoulderKey;
    internal static ConfigEntry<TpLook> TpLookMode;
    internal static ConfigEntry<float> TpDistance, TpMinDistance, TpMaxDistance, TpZoomStep, TpZoomSmoothing, TpShoulderOffset, TpPivotHeight;
    internal static ConfigEntry<float> TpPitchMin, TpPitchMax, TpDefaultPitch, TpFov, TpPositionSmoothingMs, TpAutoFollowSec;
    internal static ConfigEntry<bool> TpCrosshair, TpCollision, TpAutoFollow, TpBothButtonsForward;
    internal static ConfigEntry<float> TpCollisionRadius, TpCollisionMargin, TpCollisionReturnSpeed, TpFadeDistance;
    internal static ConfigEntry<string> TpCollisionLayers;
    internal static ConfigEntry<float> TpRightClickMaxDrag, TpRightClickMaxSec;
    // [Performance] (v7)
    internal static ConfigEntry<PerfPreset> PerfPresetCfg;
    internal static ConfigEntry<bool> ProfileLog, FarClipFitFog;
    internal static ConfigEntry<float> ProfileIntervalSec;
    internal static ConfigEntry<int> EffectsScanFrames;

    public override void Load()
    {
        L = Log;
        BindConfig(Config);
        if (!Enabled.Value) { L.LogInfo("CameraMod: disabled in config (General.Enabled = false)"); return; }

        // Optional IMGUI status overlay (needs class injection; the camera itself does not).
        try
        {
            AddComponent<StatusOverlay>();
            L.LogInfo("CameraMod: status overlay injected (IMGUI)");
        }
        catch (Exception ex)
        {
            L.LogWarning($"CameraMod: class injection failed, status goes to the log only ({ex.GetType().Name}: {ex.Message})");
        }

        try
        {
            var harmony = new Harmony("albionlocal.camera");
            var target = AccessTools.Method(typeof(ActorCameraController), "LateUpdate");
            if (target == null) { L.LogError("CameraMod: ActorCameraController.LateUpdate not found in interop - mod inactive"); return; }
            harmony.Patch(target,
                prefix: new HarmonyMethod(typeof(Plugin), nameof(LateUpdatePrefix)),
                postfix: new HarmonyMethod(typeof(Plugin), nameof(LateUpdatePostfix)));
            L.LogInfo("CameraMod: hooked ActorCameraController.LateUpdate (prefix+postfix); per-frame path = Harmony postfix");
            TryPatch(harmony, typeof(Atmosphere), "ApplyFog", null, nameof(ApplyFogPostfix), "Atmosphere.ApplyFog (VA 0x18178D3B0) postfix: first-person fog override");
            FpKeys.Install(harmony);
            FpControls.Install(harmony);
            FpView.Install(harmony);
            L.LogInfo($"CameraMod: keys: free rotate = middle mouse{(AltRightDrag.Value ? " or Alt+right mouse" : "")}, reset = {ResetKey.Value}, camera mode = {ModeKey.Value} (top-down -> third person ({TpLookMode.Value}) -> first person), first person = {FirstPersonKey.Value}, mouse look = {LookMode.Value} (free cursor: hold {FreeCursorKey.Value} / toggle {MouseLockKey.Value}), interact = {InteractKey.Value}, hints = {HintKey.Value}");
        }
        catch (Exception ex)
        {
            L.LogError("CameraMod: hook failed, mod inactive: " + ex);
        }
    }

    private static void BindConfig(ConfigFile c)
    {
        Enabled = c.Bind("General", "Enabled", true, "Master switch. false = the mod does nothing.");
        ShowStatus = c.Bind("General", "ShowStatus", true, "Short on-screen status (IMGUI) when a mode changes. Always logged too.");

        ExtendedZoom = c.Bind("Zoom", "ExtendedZoom", true, "Mouse wheel continues past the game's zoom limits.");
        MaxDistanceMultiplier = c.Bind("Zoom", "MaxDistanceMultiplier", 2.0f, "Farthest distance = game's farthest distance x this (1 = stock).");
        MinDistanceMultiplier = c.Bind("Zoom", "MinDistanceMultiplier", 0.5f, "Closest distance = game's closest distance x this (1 = stock).");
        ZoomStep = c.Bind("Zoom", "ZoomStepMeters", 2.0f, "Extra metres per wheel notch beyond the game's limits.");
        ZoomSmoothing = c.Bind("Zoom", "Smoothing", 10f, "Higher = faster approach to the target distance.");

        FreeRotation = c.Bind("Rotation", "FreeRotation", true, "Hold middle mouse (or Alt+right mouse) and drag to orbit/tilt the camera.");
        AltRightDrag = c.Bind("Rotation", "AltRightDrag", true, "Also allow Alt + right mouse drag.");
        RotateSensitivity = c.Bind("Rotation", "Sensitivity", 0.25f, "Degrees per pixel of mouse movement.");
        MinPitch = c.Bind("Rotation", "MinPitch", 5f, "Lowest camera tilt in degrees (0 = horizontal).");
        MaxPitch = c.Bind("Rotation", "MaxPitch", 85f, "Highest camera tilt in degrees (90 = straight down).");
        PivotHeight = c.Bind("Rotation", "PivotHeight", 1.2f, "Orbit pivot height above the player's feet (m).");
        ResetKey = c.Bind("Rotation", "ResetKey", KeyCode.Home, "Reset rotation and extra zoom to the default view.");

        FirstPersonEnabled = c.Bind("FirstPerson", "Enabled", false, "First-person view available (F8 and in the F7 cycle). Off by default: the third-person view plays better; F7 then toggles top-down <-> third person.");
        FirstPersonKey = c.Bind("FirstPerson", "ToggleKey", KeyCode.F8, "Toggle the experimental first-person view (only when FirstPerson.Enabled = true).");
        LookSensitivity = c.Bind("FirstPerson", "Sensitivity", 0.15f, "Degrees per pixel of mouse movement.");
        LookMode = c.Bind("FirstPersonControls", "MouseLook", CameraMod.LookMode.Always,
            "Always = FPS style: in first person the cursor is hidden and held at the crosshair, the mouse turns the view; FreeCursorKey (hold) or MouseLockKey (toggle) frees it for the UI, and it is freed automatically while a game window or the chat input is open. HoldRight = look only while the right mouse button is held (or MouseLockKey toggled it on); Toggle = only MouseLockKey.");
        MouseLockKey = c.Bind("FirstPerson", "MouseLockKey", KeyCode.CapsLock, "Toggles between mouse look (cursor held at the crosshair) and a free cursor. The game clicks, aims and targets through the crosshair while the cursor is held.");
        Crosshair = c.Bind("FirstPerson", "Crosshair", true, "Draw a small crosshair at the screen centre in first person.");
        CrosshairOnlyWhenLocked = c.Bind("FirstPerson", "CrosshairOnlyWhenLocked", true, "Show the crosshair only while the cursor is locked (mouse look).");
        CrosshairSize = c.Bind("FirstPerson", "CrosshairSize", 7f, "Half length of the crosshair lines in pixels.");
        FreeCursorKey = c.Bind("FirstPersonControls", "FreeCursorKey", KeyCode.LeftAlt, "Hold to free the cursor (inventory, loot, menus). Alt+spell key still self-casts as in the game. None = off.");
        AutoFreeCursor = c.Bind("FirstPersonControls", "AutoFreeCursor", true, "Free the cursor automatically while a game window is open (GameGui.IsAnyNoneHudGuiOpenByAlignment) or the chat/text input has focus; captured again when it closes.");
        InvertY = c.Bind("FirstPersonControls", "InvertY", false, "Invert vertical mouse look.");
        PitchUpLimit = c.Bind("FirstPersonControls", "PitchUpLimit", 80f, "How far you can look up (degrees above the horizon).");
        PitchDownLimit = c.Bind("FirstPersonControls", "PitchDownLimit", 85f, "How far you can look down (degrees below the horizon).");
        LeftClickAttack = c.Bind("FirstPersonControls", "LeftClickAttack", true, "Cursor held: a left click also carries the game's attack + interact action flags (like the classic mouse scheme), so it attacks the unit under the crosshair, opens chests/loot, talks to NPCs.");
        ClickMoveInFp = c.Bind("FirstPersonControls", "ClickMove", false, "Cursor held: false = clicks never walk to the ground point under the crosshair (move with WASD); true = game default.");
        RightMouse = c.Bind("FirstPersonControls", "RightMouse", RightMouseMode.Game, "Right mouse while the cursor is held: Game = the game's own right click; None = ignored; Hotkey = acts as the game key RightMouseHotkey (e.g. W = second weapon spell).");
        RightMouseHotkey = c.Bind("FirstPersonControls", "RightMouseHotkey", KeyCode.W, "Game key for RightMouse = Hotkey.");
        InteractKey = c.Bind("FirstPersonControls", "InteractKey", KeyCode.G, "Interact with what is under the crosshair (loot, chests, NPCs, resources, doors): the game's own left-click 'use' action through the crosshair ray. None = off. The game hotkey on this key is blocked in first person.");
        AimMaxDistance = c.Bind("FirstPersonControls", "AimMaxDistance", 15f, "When the crosshair ray does not reach the ground within this distance (looking at the horizon or up), the game's cursor is placed on the ground at this distance in the view direction, so ground-target spells land in front of you instead of on yourself. 0 = off.");
        HintKey = c.Bind("FirstPersonControls", "HintKey", KeyCode.F1, "Show/hide the key hint panel in first person.");
        ShowHintOnEnter = c.Bind("FirstPersonControls", "ShowHintOnEnter", true, "Show the key hint panel when entering first person.");
        FpPositionSmoothingMs = c.Bind("FirstPersonCamera", "PositionSmoothingMs", 70f, "Camera position follow time (critically damped spring); removes position jitter (server corrections, animation). 0 = none. Mouse look is never smoothed by this.");
        FpHeadBob = c.Bind("FirstPersonCamera", "HeadBob", 0.15f, "0 = stable eye (head offset low-pass filtered, no animation bob), 1 = camera fixed to the head bone as in v3.");
        FpEyeHeightAdaptSec = c.Bind("FirstPersonCamera", "EyeHeightAdaptSec", 0.4f, "Time constant of the head-offset filter (follows mount/sit/crouch height changes).");
        FpLookSmoothingMs = c.Bind("FirstPersonCamera", "LookSmoothingMs", 0f, "Mouse look smoothing (0 = direct, recommended; 10-30 = slightly softer).");
        FpFov = c.Bind("FirstPersonCamera", "FieldOfView", 75f, "Vertical field of view in first person (degrees). 0 = keep the game's value. Restored on exit.");
        ShowBodyMode = c.Bind("FirstPerson", "ShowBody", ShowBody.None,
            "None = hide every mesh of your character; BodyOnly = hide only head/hair/helmet/face meshes (keeps hands, body and weapon); All = hide nothing (use HeadForwardOffset).");
        HideRenderMode = c.Bind("FirstPerson", "HideMode", HideMode.ShadowsOnly, "ShadowsOnly = hidden meshes still cast their shadow; Disable = not rendered at all (Renderer.forceRenderingOff).");
        DeepScan = c.Bind("FirstPerson", "DeepScan", true, "Also look (every ~3 s) for skinned meshes bound to your bones but parented elsewhere in the scene.");
        NearClip = c.Bind("FirstPerson", "NearClip", 0.05f, "Camera near clip plane in first person.");
        DisableLayerCullDistances = c.Bind("FirstPerson", "DisableLayerCullDistances", true, "Clear Camera.layerCullDistances in first/third person (restored on exit). Only with Performance.Preset = Custom (Performance keeps the game's cull distances).");
        HeadUpOffset = c.Bind("FirstPerson", "HeadUpOffset", 0.25f, "Metres above the head bone for the eye position.");
        HeadForwardOffset = c.Bind("FirstPerson", "HeadForwardOffset", 0.15f, "Metres in front of the head bone (along the view yaw) for the eye position.");
        FallbackEyeHeight = c.Bind("FirstPerson", "FallbackEyeHeight", 1.7f, "Eye height above the player's position if no head bone is found.");

        FirstPersonFog = c.Bind("FirstPersonView", "FirstPersonFog", FpFog.Reduced,
            "Distance fog in first person: Off = no fog (RenderSettings.fog off, GlobalFog effect off), Reduced = fog start/end x FogDistanceMultiplier, Default = game fog. Restored on exit.");
        FogDistanceMultiplier = c.Bind("FirstPersonView", "FogDistanceMultiplier", 4f, "For FirstPersonFog = Reduced: fog start/end distance multiplier (density / m^2).");
        FirstPersonFarClip = c.Bind("FirstPersonView", "FirstPersonFarClip", 1500f, "Camera far clip plane in first/third person (only raised, never lowered). Only with Performance.Preset = Custom (Performance: 600 m fitted to the fog end).");
        LodBias = c.Bind("FirstPersonView", "LodBias", 2f, "QualitySettings.lodBias in first/third person (higher = detailed LODs farther away). 0 = do not change. Only with Performance.Preset = Custom (Performance: 1.5).");

        KeyboardMove = c.Bind("FirstPersonMove", "KeyboardMove", true, "First person: move with the keys below relative to the view direction.");
        MoveForwardKey = c.Bind("FirstPersonMove", "MoveForward", KeyCode.W, "Movement keys in first person. While first person is on, the game's own hotkeys on these keys (W/D spells, A) are blocked (BlockGameMoveKeys); see FpSpellKeys.");
        MoveBackKey = c.Bind("FirstPersonMove", "MoveBack", KeyCode.S, "");
        MoveLeftKey = c.Bind("FirstPersonMove", "MoveLeft", KeyCode.A, "");
        MoveRightKey = c.Bind("FirstPersonMove", "MoveRight", KeyCode.D, "");
        BlockGameMoveKeys = c.Bind("FirstPersonMove", "BlockGameMoveKeys", true, "In first person the game's hotkeys bound to the four movement keys (and to the FpSpellKeys sources) are ignored (hotkey dispatcher dvh.f patched). Outside first person nothing changes.");
        FpSpellKeys = c.Bind("FirstPersonMove", "FpSpellKeys", "Alpha3=W, Alpha4=D, Mouse3=W, Mouse4=D, Z=A",
            "First person only: Source=GameKey pairs (Unity KeyCode names). Pressing Source acts as pressing GameKey in the game (spells on W/D, A). Defaults: 3 / mouse 4 (back side button) = W spell (2nd weapon spell), 4 / mouse 5 (forward side button) = D spell (helmet), Z = A (slot 14). Q, E, R, F, 1, 2 keep working unchanged. Alt held = self-cast variant as usual. Empty = no remap.");
        MoveMethodCfg = c.Bind("FirstPersonMove", "MoveMethod", MoveMethod.Auto,
            "InputAction = LocalInputHandler.HandleInputActionsMove (the game's keyboard/gamepad move entry); ClickMove = LocalInputHandler.DoMoveTriggered(point ahead), the end of the ground-click path; Auto = InputAction, switches to ClickMove if the player did not move within 0.5 s.");
        MoveIntervalMs = c.Bind("FirstPersonMove", "IntervalMs", 100f, "How often the move request is repeated while the keys and the view direction stay the same (a key change or a turn of more than 3 degrees is sent at once).");
        MoveLookAhead = c.Bind("FirstPersonMove", "LookAheadMeters", 3f, "Distance of the move target ahead of the player.");
        MoveYawCompensation = c.Bind("FirstPersonMove", "YawCompensation", 48f, "Fallback only (used if LocalInputHandler.WorldSpaceRotation cannot be read): undoes the game's fixed -48 deg isometric rotation.");

        FpHideEffects = c.Bind("FirstPersonEffects", "FpHideEffects", FpEffectsMode.NearCamera,
            "Off = keep all effects; NearCamera = hide effects attached to your character (cosmetic/vanity, equipment and buff Vfx that follow your sockets) on head/neck/face sockets or within CosmeticRadius of the camera, plus any particle effect in the scene within FpEffectRadius of the camera; AllOnSelf = hide every effect attached to your character. Effects on other units stay visible.");
        FpEffectRadius = c.Bind("FirstPersonEffects", "FpEffectRadius", 0.6f, "Metres around the camera in which effects are hidden (NearCamera / scene scan).");
        FpCosmeticRadius = c.Bind("FirstPersonEffects", "CosmeticRadius", 1.5f, "Effects/attachments bound to your character (vanity heads, capes, auras, buffs: Vfx / ConstrainableObject attached to your sockets) are hidden when any of their renderers comes within this many metres of the camera (NearCamera), or always (AllOnSelf).");
        FpHideOwnNameplate = c.Bind("FirstPersonEffects", "HideOwnNameplate", true, "Hide your own name / health plate (WorldObjectAnchoredGui anchored to your character) in first person.");

        SkyEnabled = c.Bind("FirstPersonSky", "Enabled", true, "Master switch for the first-person sky. false = the camera keeps the game's own clear/background (same as SkyMode = Off).");
        SkyModeCfg = c.Bind("FirstPersonSky", "SkyMode", SkyMode.Auto,
            "First-person sky. Off = game default (solid background); Procedural = Unity Skybox/Procedural if the shader exists in the build; Dome = generated gradient sky dome (+clouds); Auto = Procedural if available, else Dome.");
        SkyZenithColor = c.Bind("FirstPersonSky", "SkyZenithColor", "#4A80C8", "Colour straight up (hex RRGGBB).");
        SkyHorizonFromFog = c.Bind("FirstPersonSky", "SkyHorizonFromFog", true, "Horizon colour = current fog colour (distant terrain blends into the sky). false = SkyHorizonColor.");
        SkyHorizonColor = c.Bind("FirstPersonSky", "SkyHorizonColor", "#BFD1E5", "Horizon colour when SkyHorizonFromFog = false.");
        SkySunGlow = c.Bind("FirstPersonSky", "SunGlow", 0.6f, "Brightness of the glow around the sun direction on the dome (0 = none).");
        SkyClouds = c.Bind("FirstPersonSky", "Clouds", true, "Dome mode: drifting procedural cloud layer.");
        SkyCloudCover = c.Bind("FirstPersonSky", "CloudCover", 0.5f, "0..1 fraction of the sky covered by clouds (applied when the texture is first generated, i.e. restart the game to change).");
        SkyCloudSpeed = c.Bind("FirstPersonSky", "CloudSpeed", 1f, "Cloud drift speed (0 = still).");
        CameraDiagnostics = c.Bind("FirstPersonDiagnostics", "CameraDiagnostics", true, "Log every camera (rect, pixelRect, target texture, clear, culling mask, clip planes, effective projection, components) before entering first person, right after and ~1 s later, plus any later change of the first-person camera's rect/target/projection.");
        DiagKey = c.Bind("FirstPersonDiagnostics", "BisectKey", KeyCode.F10, "First person: each press switches to the next single-factor test (game FOV, game near clip, game far clip, sky off, game fog, game layerCullDistances, guards off, then each component on the camera disabled one at a time) and logs the camera state. Back to step 0 after the last one. None = off.");
        ForceFullViewport = c.Bind("FirstPersonDiagnostics", "ForceFullViewport", true, "First person: keep Camera.rect = full screen (restored on exit); NGUI UIViewport is not allowed to change it.");
        SyncHelperCameras = c.Bind("FirstPersonDiagnostics", "SyncHelperCameras", true, "First person: cameras parented under the main camera (e.g. CamXRay's CamDepthXRay depth camera) follow its fov/near/far (restored on exit).");
        HideCameraAttachedRenderers = c.Bind("FirstPersonDiagnostics", "HideCameraAttachedRenderers", true, "First person: mesh/sprite renderers parented under the camera are disabled (restored on exit). Particles under the camera (weather) are kept.");
        DisableCameraComponents = c.Bind("FirstPersonDiagnostics", "DisableCameraComponents", "", "First person: comma-separated type names of components on the camera to disable (restored on exit), e.g. BlendedBounds, GlobalFog, PostProcessLayer. Use the names from the 'behaviours:' log line or the bisect result.");
        CrosshairOutline = c.Bind("FirstPerson", "CrosshairOutline", true, "Dark 1-pixel outline around the crosshair so it stays visible on bright ground and sky.");
        SkyRadius = c.Bind("FirstPersonSky", "DomeRadius", 1000f, "Dome radius in metres (capped at 0.9 x camera far clip).");

        ModeKey = c.Bind("ThirdPerson", "ModeKey", KeyCode.F7, "Cycles the camera: top-down (game) -> third person -> first person -> top-down. FirstPerson.ToggleKey (F8) still toggles first person directly. Third person uses all first-person controls (WASD, G, spell remaps, left click attack/use, fog/sky/fov/viewport guards).");
        TpLookMode = c.Bind("ThirdPerson", "LookMode", TpLook.WoW, "WoW = cursor visible: hold right mouse = mouse look that also turns the movement direction, hold left mouse = orbit the camera only (a click without dragging is a normal game click at the cursor), both buttons = walk forward, MouseLockKey toggles permanent mouse look. Shooter = cursor captured at the crosshair like first person (FirstPersonControls.MouseLook rules).");
        TpDistance = c.Bind("ThirdPerson", "Distance", 5f, "Start distance from the pivot (m). Mouse wheel changes it.");
        TpMinDistance = c.Bind("ThirdPerson", "MinDistance", 1.5f, "Closest wheel distance (m).");
        TpMaxDistance = c.Bind("ThirdPerson", "MaxDistance", 15f, "Farthest wheel distance (m).");
        TpZoomStep = c.Bind("ThirdPerson", "ZoomStep", 1f, "Metres per wheel notch at 5 m (scaled with the distance).");
        TpZoomSmoothing = c.Bind("ThirdPerson", "ZoomSmoothing", 10f, "Higher = faster approach to the wheel distance.");
        TpShoulderOffset = c.Bind("ThirdPerson", "ShoulderOffset", 0.6f, "Sideways camera offset (m, + = right shoulder). 0 = centred behind the back.");
        TpShoulderKey = c.Bind("ThirdPerson", "ShoulderSwapKey", KeyCode.V, "Swap the shoulder (left/right). The game hotkey on this key is blocked in third person. None = off.");
        TpPivotHeight = c.Bind("ThirdPerson", "PivotHeight", 1.6f, "Orbit pivot height above the feet of the player (m).");
        TpPitchMin = c.Bind("ThirdPerson", "PitchMin", -40f, "Lowest camera pitch (degrees; negative = camera below the pivot looking up).");
        TpPitchMax = c.Bind("ThirdPerson", "PitchMax", 70f, "Highest camera pitch (degrees; positive = camera above looking down).");
        TpDefaultPitch = c.Bind("ThirdPerson", "DefaultPitch", 15f, "Pitch when entering third person from the top-down view.");
        TpFov = c.Bind("ThirdPerson", "FieldOfView", 70f, "Vertical field of view in third person (degrees). 0 = keep the game value. Restored on exit.");
        TpCrosshair = c.Bind("ThirdPerson", "Crosshair", true, "Draw the crosshair in third person while the cursor is captured (Shooter, or WoW with mouse look toggled on).");
        TpPositionSmoothingMs = c.Bind("ThirdPerson", "PositionSmoothingMs", 60f, "Pivot follow time (critically damped spring). 0 = none.");
        TpAutoFollow = c.Bind("ThirdPerson", "AutoFollow", true, "WoW style: after orbiting with the left button, walking swings the camera back behind the movement direction.");
        TpAutoFollowSec = c.Bind("ThirdPerson", "AutoFollowSec", 0.5f, "Time constant of the AutoFollow swing (s).");
        TpBothButtonsForward = c.Bind("ThirdPerson", "BothButtonsForward", true, "WoW: holding both mouse buttons walks forward.");
        TpCollision = c.Bind("ThirdPerson", "Collision", true, "Keep the camera out of walls, terrain and buildings (sphere cast pivot -> camera, pulled in at once, eased back out).");
        TpCollisionLayers = c.Bind("ThirdPerson", "CollisionLayers", "Default,Ground,GroundNoXRay,XRay,NO_XRAY,IgnoreClickDecal,IgnoreClickDecalAndXRay,MAP_PROP",
            "Layer names (or numbers) that block the camera, resolved with LayerMask.NameToLayer (the resolved list is logged on entry). Units (ClickableObject), the local player, triggers and water are not included.");
        TpCollisionRadius = c.Bind("ThirdPerson", "CollisionRadius", 0.25f, "Sphere radius of the collision probe (m).");
        TpCollisionMargin = c.Bind("ThirdPerson", "CollisionMargin", 0.1f, "Extra gap kept in front of the hit (m).");
        TpCollisionReturnSpeed = c.Bind("ThirdPerson", "CollisionReturnSpeed", 4f, "How fast the camera moves back out after an obstacle (higher = faster).");
        TpFadeDistance = c.Bind("ThirdPerson", "FadeDistance", 1f, "Hide the character (and its near effects) while the camera is closer than this to it (m). 0 = never.");
        TpRightClickMaxDrag = c.Bind("ThirdPerson", "RightClickMaxDragPixels", 6f, "WoW: the right button is mouse look. Its press is never passed to the game; on release the game gets its right click (attack what is under the cursor) only if the mouse moved at most this many pixels (and FirstPersonControls.RightMouse = Game). Holding/dragging never makes the character walk.");
        TpRightClickMaxSec = c.Bind("ThirdPerson", "RightClickMaxSeconds", 0.6f, "WoW: a right press held longer than this is never a click.");

        PerfPresetCfg = c.Bind("Performance", "Preset", PerfPreset.Performance,
            "Performance (default) = close camera (first/third person) tuned for frame rate: far clip 600 m (lowered further to the fog end with FarClipFitFog: geometry hidden by full fog is not drawn), lodBias 1.5, the game's layer cull distances kept, effect scans every 60 frames, scene scans every 10 s, all spread over frames. Custom = use FirstPersonView.FirstPersonFarClip / LodBias, FirstPerson.DisableLayerCullDistances and EffectsScanFrames below. (GraphicsMod has its own low-camera shadow preset.)");
        ProfileLog = c.Bind("Performance", "ProfileLog", true, "Log one 'CameraMod perf [...]' line every ProfileIntervalSec and on each camera mode change: fps, frame time, the mod's ms per frame per subsystem, and the render settings (far clip, fog end, shadow distance/cascades, lodBias, layer culling).");
        ProfileIntervalSec = c.Bind("Performance", "ProfileIntervalSec", 10f, "Seconds per profiler log line.");
        EffectsScanFrames = c.Bind("Performance", "EffectsScanFrames", 12, "Preset = Custom: frames between scans of the effects attached to your character (first person / faded third person). Character renderer list is re-read every 3x this.");
        FarClipFitFog = c.Bind("Performance", "FarClipFitFog", true, "Close camera: lower the far clip to the end of the linear fog (fog end x 1.5 + 50 m, at least 250 m): everything beyond is fully fogged anyway. With Preset = Custom it never goes below the game's own far clip. false = keep the preset far clip.");
    }
    internal static bool TryPatch(Harmony h, Type type, string method, string prefix, string postfix, string what)
    {
        try
        {
            var m = AccessTools.Method(type, method);
            if (m == null) { L.LogWarning($"CameraMod: hook NOT applied: {what} (method not found)"); return false; }
            h.Patch(m, prefix: prefix == null ? null : new HarmonyMethod(typeof(Plugin), prefix), postfix: postfix == null ? null : new HarmonyMethod(typeof(Plugin), postfix));
            L.LogInfo("CameraMod: hooked " + what);
            return true;
        }
        catch (Exception ex) { L.LogWarning($"CameraMod: hook NOT applied: {what}: {ex.GetType().Name}: {ex.Message}"); return false; }
    }

    private static void ApplyFogPostfix(Atmosphere __instance)
    {
        try { FogControl.AfterApplyFog(__instance); }
        catch (Exception ex) { CameraLogic.Fail("ApplyFog postfix", ex); }
    }

    internal static bool UIViewportPrefix(UIViewport __instance)
    {
        try { return FpView.UIViewportAllowed(__instance); }
        catch { return true; }
    }

    private static void LateUpdatePrefix(ActorCameraController __instance)
    {
        try { CameraLogic.Before(__instance); }
        catch (Exception ex) { CameraLogic.Fail("prefix", ex); }
    }

    private static void LateUpdatePostfix(ActorCameraController __instance)
    {
        try { CameraLogic.After(__instance); }
        catch (Exception ex) { CameraLogic.Fail("postfix", ex); }
    }
}
