# GraphicsMod — how the game sets graphics (built-in pipeline)

> Paths such as `analysis/...` and `tools/...` refer to the author's private analysis workspace, which is not part of
> this repository. `<GameDir>` is the client folder (the one with `Albion-Online.exe`) after BepInEx has run once.

Sources: `analysis/il2cpp/cs/GraphicOptions.cs`, `QualitySettingsOverwrite.cs`, cpp2il DiffableCs, headless Ghidra (`tools/decomp.sh`),
`tools/xref.py`, interop `<GameDir>/BepInEx/interop`. VAs in GameAssembly.dll.

- Pipeline: built-in (no SRP asset in the game code paths; PPv2 `PostProcessLayer`, `GlobalFog`/`BlendedBounds` image effects).
  Main camera `LocalActorCamera`: `UsePlayerSettings -> DeferredShading`, HDR off (CameraMod CamDiag log). MSAA does not apply.
- `GraphicOptions : OptionsGroup` (TypeDefIndex 2961). Option types `GraphicsOptionType` (Shadow 1, ShadowResolution 2,
  ShadowCascades 3, ShadowDistance 4, PixelLightCount 8, AnisotropicTextureFiltering 9, SMAA 12 ...), values `GraphicsOptionValue`.
  `int d(GraphicsOptionType)` (0x1818A7570) = numeric value of the current option, `GraphicsOptionValue b(GraphicsOptionType)` = option value.
- **Apply: `private void d()` (0x1818A6E90)**: `QualitySettings.SetQualityLevel(Quality)`, then `shadows = d(Shadow)`,
  `shadowDistance = d(ShadowDistance)`, `shadowResolution = d(ShadowResolution)`, `shadowCascades = ...`, softParticles,
  `pixelLightCount = d(8)`, `anisotropicFiltering = d(9)`, skinWeights, `realtimeReflectionProbes = false`,
  `Shader.globalMaximumLOD = d(ShaderQuality)`, CamXRay, `_VTXANIM`, `ag()` (post-processing), prefab quality, collision grid,
  vSync / frame rate, main camera `QualitySettingsOverwrite.ApplyShadowSettings`, water shader LOD, `ENABLE_CLOUDS`, event.
  Called from `b(GraphicsOptionType, GraphicsOptionValue, bool)` (0x1818A5540) when an option changes.
- **Zoom: `b(float zoom, bool force, Vector3 a, Vector3 b)` (0x1818A6110)**, called by `ActorCameraController.UpdateZoom`
  (0x1817424A0) and `OnQualityChanged` (0x181741920): when int(zoom*100) changes, `shadowDistance = lerp(d(ShadowDistance),
  value of ShadowDistance option 'Low', zoom)` and, if `shadowCascades == 4`, `shadowCascade4Split = lerp(b, a, zoom)` where a/b are the
  controller's `min/maxZoomCascadeSettings{High,Ultra}Quality` (Ultra when shadowDistance > 80).
- `ag()` (0x1818A38A0): SSAO/Bloom/ColorGrading profile, per scene: `QualitySettingsOverwrite.ApplyShadowSettings` where
  `SwitchShadowsSettingsOnEnable`, `PostProcessLayer.antialiasingMode = SMAA ? 2 : 0`, `smaa.quality = d(SMAA)`.
- `QualitySettingsOverwrite.ApplyShadowSettings` (0x181707570): if shadows on, saves, sets shadows All, distance = own `ShadowDistance`,
  CloseFit, resolution 3; `ResetShadowSettings` / `OnDisable` restore.
- Other `QualitySettings.shadowDistance` writers (xref): `AchievementBoardGui` Show/Close/OnEnterGame, `gx7.d`. `lodBias` only AdaptivePerformance.
- Sun: `Atmosphere.ApplyLighting` (0x18178D650, every Update) sets `LightSettings.Sky/Bounce` shadowStrength, intensity, colour, rotation;
  `ApplyShadowBiases` (0x18178EBA0) sets shadowBias/normalBias from Atmosphere statics when `UseGlobalShadowBiases`.
- `Renderer.shadowCastingMode` writers (xref): DebugDraw2D, Castle ctor, FadeOutShadowed.SetAlphaCtrl, Outline, ReduceResolution,
  Graph/Path/Poly renderers, UIInput — nothing for characters.
- Stripped in this build (no NativeMethodInfoPtr): QualitySettings getters of pixelLightCount / anisotropicFiltering / shadowCascade4Split,
  antiAliasing, maximumLODLevel, shadowmaskMode, shadowCascade2Split, shadowNearPlaneOffset; Light shadowResolution getter,
  shadowBias getters, shadowCustomResolution.

Mod: postfixes on `ActorCameraController.LateUpdate` (per-frame compare-and-set, sun strength/soft, low-camera detection),
`GraphicOptions.d()` (baseline capture + full re-apply), `GraphicOptions.b(float,bool,Vector3,Vector3)`, `GraphicOptions.ag()`,
`QualitySettingsOverwrite.Apply/ResetShadowSettings`; injected `Driver` MonoBehaviour for F9, the start log and menu-time checks.
Off restores the captured baseline, pixel lights/aniso from `GraphicOptions.d(type)`, then `ActorCameraController.OnQualityChanged()`.

## 0.2.0 (performance)
- Low camera (< FirstPersonHeight): the user saw FPS drops in first/third person. A low camera sees many more shadow casters than
  the top-down view and the mod rendered 4 cascades VeryHigh over 250 m (the game: 85 m, 1 cascade). New `[Performance]
  LowCameraPreset = Performance` caps the low-camera distance at `LowCameraShadowDistance` (120 m); `ShadowDistanceFirstPerson`
  default 250 -> 150 (existing cfg files keep their value). Ignored with Preset = UltraShadows.
- Per-frame check (LateUpdate postfix) every 4th frame (the game only changes these values in hooked methods); light list every
  600 frames; character-shadow scan every 300 frames but incremental (seen set, reset every 60 s) with
  `FindObjectsByType(.., SortMode.None)` and raw element pointers.
- Profiler: `GraphicsMod perf [N frames, fps F, camera low/high]: frame check / driver / light scan / character scan ms per frame
  (max), shadows m x cascades, lodBias, lights tracked, character meshes seen` every ProfileIntervalSec (10 s).
