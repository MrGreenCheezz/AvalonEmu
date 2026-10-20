# CameraMod — findings on the game camera

> Paths such as `analysis/...` and `tools/...` refer to the author's private analysis workspace, which is not part of
> this repository. `<GameDir>` is the client folder (the one with `Albion-Online.exe`) after BepInEx has run once.

Sources: `analysis/il2cpp/cs/ActorCameraController.cs` (Il2CppInspector stub), `analysis/cpp2il/DiffableCs/Assembly-CSharp/ActorCameraController.cs`,
headless Ghidra decompile (`tools/decomp.sh`) of the methods below, interop `<GameDir>/BepInEx/interop/Assembly-CSharp.dll`.
Addresses are VAs in GameAssembly.dll (image base 0x180000000; RVA = VA - 0x180000000).

## The follow camera: `ActorCameraController : MonoBehaviour` (global namespace, TypeDefIndex 2335)

Attached to the local player view (`LocalPlayerCharacterView.ActorCameraController`, setter 0x1805B80D0; also `LocalActorFake`).

| Field | Offset | Meaning |
|---|---|---|
| `State TestState` | 0x20 | |
| `Transform CameraTransform` | 0x58 | transform the controller writes every frame |
| `State Outside` / `State Inside` | 0x60 / 0x98 | camera presets outdoors / inside buildings |
| `Transition InsideTransition` | 0xD0 | DurationIn, DurationOut, AnimationCurve |
| `float Interior` | 0xE0 | 0..1 blend outside→inside |
| `float Zoom` | 0xE4 | **normalised zoom 0..1; 0 = Far frame, 1 = Near frame** |
| `static AnimationCurve ZoomCurve` | static+0x48 | maps Zoom → lerp alpha |
| `static int ZoomFixedStep` | static+0x50 | gamepad fixed zoom steps (0 / 0.5 / 1) |
| `Vector3 min/maxZoomCascadeSettings{High,Ultra}Quality` | 0xE8..0x114 | shadow cascades per zoom |
| `Transform e` | 0x128 | **follow target (player)**; interop property `e` |
| `bool f` | 0x130 | inputs enabled (`SetInputs`) — interop property `f` |
| `bool h` | 0x132 | "inside" flag |
| `bool bUseExtendedCameraZoomState` | 0x180 | |
| `bool l` | 0x181 | override state active (`EnableOverrideState`) → uses `m` (0x184) / `n` (0x1C0) |
| `float o` | 0x1D0 | position catch-up 0..1 (+0.25*dt per frame) |
| `State p` | 0x1D4 | extended zoom state (used by `EnableExtendedCameraZoomState`) |

Nested: `Frame { float Distance; Vector3 Rotation (Euler deg); Vector3 Offset }`, `State { Frame Far @0; Frame Near @0x1C }`,
`Transition { float DurationIn, DurationOut; AnimationCurve Curve }`.

### Methods (VA)
| Method | VA | Notes |
|---|---|---|
| `LateUpdate` | 0x181741850 | **per-frame entry** (body identical to `UpdateCamera`, which is not called separately). UpdateZoom → Shader.SetGlobalFloat(CharacterPosYId, y) → UpdateMovement → VfxFactory camera shake on CameraTransform |
| `UpdateCamera` | 0x181741CD0 | same code as LateUpdate (public entry) |
| `UpdateZoom` (private) | 0x1817424A0 | gated by `f`, UI focus (`g1z.a()`), `!UICamera.isOverUI`, CameraTransform active, not LeftShift+placement ghost (`Input.GetKey(304)`), not TravelModeClusterMapGui open. `Zoom = clamp01(Zoom + dla.a())`; then shadow cascade update via GraphicOptions. Gamepad: Zoom = ZoomFixedStep {0,0.5,1} |
| `dla.a()` (static helper) | 0x180D99840 | `Input.GetAxis("Mouse ScrollWheel")` (legacy input manager), 0 when blocked → wheel up = +Zoom = closer |
| `UpdateMovement` (private) | 0x181741F50 | advances `Interior`, `Interpolate(Outside or m, Zoom)` and `Interpolate(Inside or j, Zoom)`, blends by InsideTransition curve, adds target position, smooths with `o`, **`CameraTransform.position = ...; CameraTransform.rotation = Slerp(...)`** |
| `Interpolate(State, float alpha, out Vector3, out Quaternion)` static | 0x1817413F0 | alpha → ZoomCurve; Far/Near: `Position(Offset, Euler(Rotation), Distance)` lerp + Slerp |
| `Position(Vector3 offset, Quaternion rot, float dist)` static | 0x181741A60 | `rot * Vector3.forward * -dist + rot * offset` |
| `CalculatePositionAndRotation` | 0x181740CE0 | same maths without writing transform |
| `EvaluateAndSwitchTransitionState` | 0x181741110 | |
| `EnableExtendedCameraZoomState` | 0x181740F80 | copies `p` into `j`, sets Atmosphere fields (0x99/0x9C) |
| `DisableExtendedCameraZoomState` | 0x181740EE0 | |
| `EnableOverrideState(State, Transition)` / `DisableOverrideState` | 0x1817410C0 / 0x181740F70 | |
| `SetInputs(bool)` | 0x1805F3C70 | writes `f` |
| `UpdateTargetTransform(Transform, bool)` | 0x181742440 | sets follow target `e` |
| `ToggleZoomFixedStep` static | 0x181741C60 | |
| `Awake` | 0x181740810 | logs "No Camera assigned or no tagged ..." if CameraTransform missing |

So: the min/max zoom are not numbers in code — they are the **Far and Near Frames** of the `Outside`/`Inside` States
(serialized on the prefab: distance, Euler angles, offset), and the clamp is `Zoom ∈ [0,1]` in UpdateZoom. Pitch/yaw are
fixed per frame (Frame.Rotation), there is no rotation input in the stock camera.

## Other camera classes (not used by the mod)
- `AlbionCamera` (TypeDefIndex 2846): wrapper around the main `Camera`, `static Camera Main` getter 0x1818640A0, aspect ratio.
- `CameraViewLimits` (3148): plane/frustum limits (`fMaxHeight`, `CalculateAllowedMaxDistanceFromPlane`) — likely world-map/strategic views.
- `FreeCameraController`, `CameraOrbit` (LateUpdate), `CameraRotator` (aspect sensor shift), `CameraHook`, `ArenaGui.SpectatorMode.ArenaCameraController`,
  `WorldMap.WorldmapCamera`, `UICamera` (NGUI; `isOverUI` static).
- `Camera.layerCullDistances` (UnityEngine) — no game-specific code found for distance culling in the stubs; the mod clears it in first person.

## Input
The game reads the wheel through the legacy `UnityEngine.Input` ("Mouse ScrollWheel" axis) and `Input.GetKey`, so the legacy
input manager is active; the mod uses `UnityEngine.Input` (InputLegacyModule interop): `GetKeyDown`, `GetKey`, `GetMouseButton`,
`mousePosition`, `mouseScrollDelta`. (Unity.InputSystem.dll interop also exists but is not needed.)

## Hook strategy used
Harmony prefix + postfix on the interop `ActorCameraController.LateUpdate` (Il2CppInterop native detour; no class injection
needed). Prefix stores `Zoom`; postfix applies: extra distance along the camera's forward axis (and pins the game's `Zoom`
at its limit while extended, re-running `UpdateMovement()`), orbit (yaw around world up, pitch around camera right)
about `target + up*PivotHeight`, or the first-person pose. The IMGUI status overlay is an injected MonoBehaviour
(`BasePlugin.AddComponent`), optional — on failure the mod logs only.

## First person v2 (fog, own body, controls)

### Fog / view distance
- `Atmosphere : MonoBehaviour` (TypeDefIndex 2447). `Update` (VA 0x181791250) blends `AtmosphereFrames` key frames every frame and calls
  `ApplyLighting`, `ApplyPostEffectValues`, **`ApplyFog` (0x18178D3B0)**, `ApplyWaterSettings`.
- `ApplyFog`: key frame fog settings (`KeyFrame+0x40`, or `+0x48` when `AreSeparateFogSettingsEnabled` 0x18178F360) →
  `RenderSettings.fogColor` (+0x10, or `Atmosphere.FogColor` nullable @0xF4), `fogDensity` (+0x30), `fogStartDistance` (+0x34),
  `fogEndDistance` (+0x38); if `bUseExtendedCamera` (0x99) adds `pExtendedCameraOffset` (0x9C) to start/end; free-camera
  statics shift start/end and set `Camera.backgroundColor` (+0x20). So the fog is **plain Unity RenderSettings fog**.
- `ApplySeparateFogSettings` (0x18178E9A0): keyword `_GLOBAL_FOG_DISTANCE_MOD` + two global vectors (Curve* fields 0xA0..0xB4).
- `GlobalFog` (2566) image effect, `OnRenderImage` 0x1817AE910 — only active in deferred rendering (`actualRenderingPath == 3`).
- Culling: `CullingGroupManager`/`CullingController` use a Unity `CullingGroup` with bounding spheres only (frustum
  visibility, no `SetBoundingDistances`) — no distance culling. `QualitySettings.lodBias` is only written by Unity
  AdaptivePerformance `AdaptiveLOD`; `layerCullDistances` only by `AdaptiveLayerCulling`. No client-side streaming radius
  found (static cluster geometry is loaded whole). Mobs/players/resources beyond ~40 m are not *sent* by our server:
  `server/config/albionlocal.json` `Visibility: { CellSize 10, EnterCells 4, LeaveCells 6 }` — raise EnterCells/LeaveCells for more.
- Mod: Harmony postfix on `Atmosphere.ApplyFog` remembers the base values and re-applies the override (Off: fog=false,
  start/end 1e5/2e5, density 0, keyword off, GlobalFog disabled; Reduced: start/end × m, density / m²); also re-applied per
  frame from the base values. Exit: restore values, re-run `ApplySeparateFogSettings` + `ApplyFog`, lodBias, GlobalFog.

### Own body
- v1 bug: renderers re-enabled by the game were never re-hidden (known renderers were skipped). v2 (`BodyHider`):
  `GetComponentsInChildren<Renderer>(true)` every 20 frames + scene scan for `SkinnedMeshRenderer` whose `rootBone` is under
  the player (every ~3 s), hide via `shadowCastingMode = ShadowsOnly` or `forceRenderingOff`, re-asserted each scan;
  particle/trail/line renderers kept. Head detection: under head bone, rootBone under head bone, or name words.
  Renderer list is logged once on first FP entry. Eye = head bone + HeadUpOffset + yaw-forward × HeadForwardOffset.

### Controls
- `Cursor.lockState` setter is stripped in this build (getter only, 0x183A01030), so mouse look uses Win32
  `GetCursorPos`/`SetCursorPos` to the window centre + `Cursor.visible = false`. The OS cursor stays on the crosshair, so
  the game's own click-to-move, target hover and spell ground aim use the screen-centre ray without game patches.
- Instances: `LocalInputHandler.Instance` (static, 0x1819B9F10); `LocalPlayerCharacterView.TryGet` (0x1819CE670), `.InputHandler`.
- Click path: `UICamera.ProcessMouse` → `LocalInputHandler.OnHandleInput` (0x1819A4700) → `HandleInputActions` (0x18199EF20) →
  `ScreenPointToRay` → `GetClickDetailsFor(Ray, Vector2)` (0x18199B940) → `HandleInputActionsMove(Ray,..)` (0x18199DF30) →
  `DoMoveTriggered(Vector3)` (0x181996280) → `LocalPlayerCharacterView.RequestMove(Albion Vector2)` (0x1819C9550).
- **Direction move (used by the mod):** `LocalInputHandler.HandleInputActionsMove(Vector2 vInput, bool bForced)` (0x18199DCA0):
  target = player pos + `WorldSpaceRotation` (fixed AngleAxis(-48°, up), 0x1819BAA70) * (x,0,y); callers are the game's
  `HandleKeyboardMovement` (0x18199F580), `UpdateInputGamepad` (0x1819B3680), `VirtualJoystick.Update`. Stop: `StopMovementAction` (0x1819B1280).
- Hidden built-in WASD mode: static `dk4.a()` (0x180D92BF0) sets the enable flag; keybinds "WalkUp/Down/Left/Right" (`dk4.a(bool,out bool)`
  0x180D92CE0); no callers found. Not used (fixed isometric rotation, unknown default keys).
- Aim: `LocalInputHandler.GetGroundPos(Vector2)` (0x18199C010) → `RayCastGroundHit(Ray, out Collider)` (0x1819A5C50; Physics.Raycast 500,
  miss → plane at player height, behind → player position). Hover: `MouseOverRayCast` (0x1819A2650) from `UICamera.GetMouse(0).pos`.
  Spell markers: `drv.a(out Vector2, Camera)` (0x181673D40) reads `Input.mousePosition`. Screen rescale helper `drv.a(Vector2, Camera)` (0x181677E50).
  All bottom out in `Input.mousePosition` → the Win32 cursor warp to the window centre covers them without patches.


## First person v3 (WASD, game hotkeys, effects, sky)

### Game hotkeys
- Key-binding manager `dvh` (TypeDef token 0x2000B53; constant "@HOTKEY_"), held by the singleton `dw1` (static field 0 /
  getter `dw1.ab()`, instance getter `ac()` -> `dvh` @0x10; `dk4.a(bool,out bool)` reads it the same way).
  Storage interface `dvl` (impls `dvi`, `dvg`, `g0a`): `asa(dvf action, HotKeyDefinition)` binds, `asc(HotKeyDefinition, out dvf)` looks up.
  `dvf` = action {ActionCategory @0x10, name @0x18, Action down @0x20 / up @0x28, Action<object> down @0x30 / up @0x38, param @0x48, enabled @0x50}.
  `HotKeyDefinition` {KeyCode @0x10, EventModifiers @0x14}.
- **Dispatcher `dvh.f(KeyCode, EventModifiers, bool down)` (RVA 0x1894130, VA 0x181894130):** returns false when `dvh.g` (@0x38,
  hotkey block counter, e.g. text input) > 0; builds HotKeyDefinition(key, mods & ~CapsLock), `dvl.asc` lookup, runs the dvf
  action for down/up. Called by `dvh.f(Event)` (0x181894B50) for IMGUI KeyDown (only if `Input.GetKeyDown(key)`) and KeyUp,
  `Event.Use()` when handled.
- Spell slot defaults: `SpellButtonGui.BindHotKeysForSpellSlot(dvl)` (0x181475530) -> `SetHotkeyForSpellSlot(slot, key, dvl)`
  (0x1814775A0): slot0 Q, 1 W, 2 E, 3 R, 4 D, 5 F, 12 '1', 13 '2', 14 A (action name from `do9.a(slot)`); slots < 13 also get
  "<name>_self" = key + Alt (EventModifiers 4) and "<name>_alt" (unbound). Callback `SpellButtonGui.OnHotKey(object slot, bool alt)` (0x181476D20).
- Mod: Harmony prefix on the dispatcher; in first person it swallows the four movement keys and the `FpSpellKeys` sources
  (`__result = false`, skip), and `FpKeys.Update` injects the remapped targets by calling `dvh.f(target, mods, down/up)` itself
  (re-entrancy flag). Mouse side buttons work too because the injection does not depend on IMGUI key events.

### Movement
- `HandleInputActionsMove(Vector2, bool bForced)` (0x18199DCA0): false if `LocalPlayerCharacterView.CurrentStateWalkLocked`, or
  (keyboard mode, not forced) the character is in a blocking state (`cpo.azu/azw/azs`); else `DoMoveTriggered(pos + WorldSpaceRotation * (x,0,y))`.
  The game's `HandleKeyboardMovement` passes bForced = "a walk key went down this frame", vector = dk4 int axes x dt*60.
- `WorldSpaceRotation` (0x1819BAA70): cached `Quaternion.AngleAxis(-48, Vector3.up)` (Vector3 static +0x18 = up). The mod reads the
  property and applies its inverse to the camera-relative direction (fallback config YawCompensation = +48).
- `DoMoveTriggered(Vector3)` (0x181996280): 3 if input actions blocked (@0x280), 0 if within `MinMoveDistance` (@0x294), 4 if
  refused, 2 if queued behind a cast (action @0x2A8), else `LocalPlayerCharacterView.RequestMove(Albion Vector2)` (rate-limited by
  a config interval; stores the target @0x4B8, sends the Move op) and 0. This is the ClickMove path.
- v2 had no "first-person move" log because the default keys were arrows; v3 uses W/A/S/D, logs every press start
  (`first-person move: dir=(x,y) ... via ... -> result`) and Auto switches to ClickMove if the player did not move in 0.5 s.
- Typing guard: `UIInput.selection != null` (NGUI focused input) -> no movement / no injection.

### Effects near the eye
- Own overhead GUI: `WorldObjectAnchoredGui : UIRect` (`AnchoredWorldObjectView` @0xB0, `OnWorldAnchoredUpdate` 0x181480650 positions it;
  subclasses `CharacterName`, `MobHealthBar`, `DangerIcon`, ...). Mod: instances anchored to the local view get `localScale = 0` while in
  first person (re-asserted every 30 frames), restored on exit.
- Particle/Trail/Line renderers under the character: hidden with `forceRenderingOff` (NearCamera: under head/neck or bounds within
  `FpEffectRadius` of the eye; AllOnSelf: all), plus scene `ParticleSystemRenderer`s whose bounds touch the eye radius.

### Sky
- The main camera clears to a solid colour; no skybox material in the scenes. `Atmosphere` has `SkyColor`/`GroundColor`/`FogColor`
  nullable overrides (@0xE0/@0xCC/@0xF4, ambient/fog), `ReflectionMap` cubemap (@0x68), `CloudSettings` (cloud *shadow* texture on the
  ground, `SunLight` @0x48), static sun `Light` (Atmosphere static +0). No sky dome/cloud mesh found in the stubs.
- Shaders: `globalgamemanagers` lists `Skybox/Procedural`, `Skybox/6 Sided`, `Skybox/Cubemap`, `Skybox/Panoramic` only as built-in
  shader *names*; `Resources/unity_builtin_extra` contains no Skybox shader (it has `UI/Default` and NGUI `Unlit/Transparent Colored` ...),
  `unity default resources` has `Hidden/Internal-Colored`. So SkyMode Auto falls back to the dome unless `Shader.Find("Skybox/Procedural")`
  succeeds at runtime.
- Dome: generated UV sphere drawn with `Graphics.DrawMesh(mesh, TRS(eye, r), mat, layer, mainCamera)` (only the main camera renders it),
  `Hidden/Internal-Colored` with `_ZWrite 0, _ZTest 4, _Cull 0, _SrcBlend 1, _DstBlend 0`, renderQueue 1000; vertex colours = fog colour at
  the horizon -> `SkyZenithColor`, sun glow from the brightest directional light; cloud layer = flattened upper hemisphere with a runtime
  tileable fBm texture (`Sprites/Default`, queue 1001), drifted through mesh UVs. Camera `backgroundColor` = horizon while active.

**Краш v3 (20.10):** `ColorUtility.ToHtmlStringRGB` / `TryParseHtmlString` через интероп (`Il2CppSystem.String.Format`) роняют клиент (AccessViolation, не ловится). Заменены управляемым кодом. Правило: в модах не вызывать методы Unity, которые форматируют строки внутри IL2CPP (`ColorUtility`, `Vector3.ToString()` и т. п.) — форматировать самим.

## First person v4 ("playable": controls, cosmetic effects, smoothing)

### Mouse buttons / click actions
- `LocalInputHandler.EvaluateActionsByMouseInput(int button, g event, out h mods)` (VA 0x1819977E0) -> action mask `LocalInputHandler.e`
  (short flags). `MouseOptions` scheme 0 ("new"): left press 0x454, right press 0x28, held/release 0x3000/0x800/0x1000/0x200;
  scheme 1 -> `EvaluateActionsByMouseInputOriginal` (0x1819975A0): left press 0x20 | 0xF8 (or 0x1F0) | 0x404 [+0x1 move], right 0x200 / 0x20 (cancel targeting).
  Shift (KeyCode 0x130/0x12F) sets mods bit 1.
- `HandleInputActions(Vector2 screenPos, e mask, h mods, int touchId = -10)` (0x18199EF20): `Camera(0x1F8).ScreenPointToRay`,
  `GetClickDetailsFor` (0x18199B940), `HandleInputActionsNotMove` (0x18199EAA0: placement ghost; spell-target mode when short @0x2B0 >= 0
  -> `HandleInputActionsNotMove_SelectSpellTarget`; per clicked object `HandleActionNotMove_Single` (0x18199CC60): **0x8 = DoActionAttack**
  (0x181993A20), **0x10 = DoActionStaticObjectInteraction** (0x1819952C0), **0x1C00 = DoActionChargeInteraction** (0x181994120); if nothing handled and
  **0x4 -> HandleInputActionsNotMove_Select** (0x18199E9C0)); then, if `mask & 0x201`, `HandleInputActionsMove(Ray, l, e)` (0x18199DF30) = walk to the ground point.
  Result codes `LocalInputHandler.f`: 0 ok, 2 queued, 3 not handled, 4 refused.
- `AttackCurrentTarget` (0x18198F310) builds a context with mask 8 on the selected view and calls DoActionAttack; `StartDynamicActionOrAttack`
  (0x1819AF1A0) = the "nearest interactable" action from `DynamicActionData` (@0x50).
- Mod: Harmony prefix on `HandleInputActions` (only while the cursor is held in first person): left press (`mask & 0x4`) gets 0x8|0x10
  (attack + interact, as in the classic scheme); move flags 0x201 stripped (`ClickMove = false`); right-button masks dropped for
  `RightMouse = None/Hotkey`. Interact key: `HandleInputActions(screen centre, 0x454, 0, -10)` with the prefix bypassed.
- No standalone "face direction" request exists (`LocalPlayerCharacterView` has only `RequestMove` 0x1819C9550 / `RequestLockAutoTarget`);
  the character turns to the move direction and to the cast target (the game cursor point), so no facing op is sent.

### Ground aim above the horizon
- `RayCastGroundHit` falls back to the player position when the ray points up, so in first person spells aimed at the horizon landed on the
  player. Mod: if the crosshair ray does not meet the plane at the player's height within `AimMaxDistance` (15 m), the Win32 cursor is
  warped to `Camera.WorldToScreenPoint(player + viewYawDir * AimMaxDistance)` instead of the centre (crosshair stays centred; a small dot
  marks the game cursor). Mouse delta = cursor position - last warp position.

### Open windows (auto free cursor)
- `GameGui.Instance` (static) . `IsAnyNoneHudGuiOpenByAlignment(GuiAlignment, HashSet<GuiBase> except)` (0x1813D0400): `GuiRegistry.d`
  (Dictionary<GuiAlignment, List<GuiBase>> @0x30) `[alignment]` (throws KeyNotFound if no gui has that alignment), skips guis in
  category 1 (HUD, `GuiRegistry.c` @0x28) and `IsClosingCheckWhitelist`, returns true if any `GuiBase.IsOpen` (vtable 0x178).
  `WindowGuiBase.IsOpen` uses `AnimateOpenWindow.bIsOpen`. Mod polls all 10 alignments every 6 frames (alignments that throw are skipped).
  Text input: `UIInput.selection != null`.

### Cosmetic / vanity / equipment / buff effects
- Not children of the character: separate GameObjects following a socket through the constraint system.
  `Vfx : MonoBehaviour, gw3 (IConstrainableObject)` (TypeDef 2762): `renderers` @0xA0, `iInstigatorId` @0xD0, `pNewVfxHandler duk` @0x80,
  **`pConstraintCtrl gw1` @0x148**; created by `Vfx.Create(dtc master, gwh, string prefab, string socket, bbq, ...)` (0x181840F10 / 0x1818405D0),
  `CreateAssetVfxOnSimulationObject` (0x18183D2C0), `InitializeEquipVfxObject` -> `EquipVfxVisualStateController : VfxVisualStateController`
  (equip/vanity VFX states), `InitializeSpellEffectVfxObject` (0x181843E80).
  `ConstrainableObject : MonoBehaviour, gw3` (2381): `pConstraintCtrl` @0x20, `AttachTo(dtc, string socket, ScalingOptions)`.
  `gw1` (constraint controller, 7869): `a gw3` slave @0x10, **`b dtc` master @0x18**, `c bbq` settings, **`q string` socket name @0x60**,
  **`r dta` socket @0x68** -> `dta.b Transform` @0x18 (the bone). `dtc` = constraint master interface (FightingObjectView, MountObjectView, ...:
  `get_transform`, `get_AttachmentSockets` Dictionary<string, dta>).
- Mod: every 12 frames `Object.FindObjectsOfType(Il2CppType.Of<Vfx>())` / `<ConstrainableObject>` (non-generic API); "on self" = socket transform
  under the local character or master transform == / under / above it; hides their renderers (`GetComponentsInChildren<Renderer>(true)`:
  ParticleSystemRenderer, TrailRenderer, LineRenderer, MeshRenderer, SkinnedMeshRenderer, VFXRenderer) with `forceRenderingOff`.
  Logs each found object (`<kind> on you '<name>' socket '<socket>' renderers N [types]`) and a summary of counts by kind/renderer type.
- v3 "scene effect scan failed: invalid program": `Bounds.SqrDistance` is not in the IL2CPP build (no `NativeMethodInfoPtr` in the interop; the
  body is Il2CppInterop's unstripped IL translation -> `InvalidProgramException`). Rule: before calling a Unity struct/static helper, check the
  interop for a `NativeMethodInfoPtr_<name>` field; methods without one are unstripped and may be invalid. `Renderer.bounds`, `Camera.WorldToScreenPoint`,
  `Camera.fieldOfView`, Vector3/Quaternion operators used here are native.

### Camera smoothing
- Eye = root + head offset; the head offset (head bone - root) is low-pass filtered (`EyeHeightAdaptSec`), `HeadBob` blends back to the raw bone;
  then a managed critically damped spring (SmoothDamp maths, `PositionSmoothingMs`), snap if > 5 m. Mouse look is applied directly
  (`LookSmoothingMs` 0 default). FOV `FieldOfView` (75) set on entry, re-asserted, restored on exit. Entry yaw = Animator transform forward.

### Movement send policy
- `HandleInputActionsMove(Vector2, bForced)` sent at once on a key change (bForced = true), on a view turn > 3 deg (<= 30/s), else every `IntervalMs`;
  release -> `StopMovementAction`.

## First person v5 (black frame around the world)

User test of v4: the world (incl. the procedural sky) is drawn only inside a central rectangle (~78% x 69% of the screen),
the rest is black, HUD on top is fine. Static analysis (no runtime data yet) of everything that could limit the view:

| Candidate | Finding |
|---|---|
| `Camera.rect` / `pixelRect` writers (xref of `Camera_set_rect` 0x1839EA190 / `set_pixelRect` 0x1839E9FD0) | only NGUI **`UIViewport.LateUpdate`** (VA 0x181A8F380; the seed list mislabels it `UITexture_set_pixelSize`), `PlayerInputManager` (split screen) and `CollisionGridViz` (debug). No Albion code. |
| `AlbionCamera` (main camera wrapper) | has a serialized `ClearCamera` and `SetAspectRatio(float, bool)` (0x181863F60) but that is a stub (null checks only); `UpdateAspectRatioFromSetting` is `ret`. No letterbox code. |
| `CameraRotator` (on the main camera) | `OnPreRender` (0x1818656D0): `projectionMatrix = T(sensorShift) * Perspective(fieldOfView, aspect, near, far)` (x mirror if `drv.ah()==1`), `GL.invertCulling`; `OnPostRender` only resets invertCulling, never `ResetProjectionMatrix` -> the camera always has a custom projection, rebuilt from the *current* fov each frame. Sensor shift = 0 unless `Screen aspect < AspectThreshold` (then from `ViewportOcclusionConfigurator.Occlusion` pixels / UIRoot pixel size). Not FOV-stale. |
| `CamXRay` (main camera) | `OnEnable` (0x1817A5FC0) creates child camera **`CamDepthXRay`** (depth = main - 1, clear Depth, renderingPath Forward, cullingMask Ground/XRay/...), copying near/far/ortho/fov **once**; `Update` (0x1817A6BE0) gives it RT `_DepthBlockers` (main pixel size). Command buffers `XRayOccluderMark` / `XRay` on the main camera at CameraEvent 12 (BeforeImageEffectsOpaque). With FOV 75 / near 0.05 the depth camera stays at 47 / 0.3 -> x-ray mask misaligned. v5 syncs it. |
| `ReduceResolution` | `Enable(w,h)`: main camera -> RT, child `BlitCamera` (ortho 0.5, clear Nothing, depth main + 0.25, quad scaled (aspect,1,1) on layer BLIT_3D). Full-screen; logged via `IsActive`. |
| `BlendedBounds` (0x1817A4570), `GlobalFog` (0x1817AE910) | deferred only (`actualRenderingPath == 3`); frustum-corner rays from the live fov/near/far, GL.LoadOrtho full quad; BlendedBounds darkens outside the cluster rectangle (`ClusterBounds` from `ciy` singleton +0x28 -> +0x98..0xA4) in world space, not screen space. |
| PPv2 (`PostProcessLayer`, custom `Vignette`/`VignetteDistortion` AfterStack, MSVO AO, SSR, TAA) | all use the live projection; custom Vignette is radial. |
| Dynamic resolution | no `ScalableBufferManager.ResizeBuffers` caller (only the getter is in the binary). |

So no game code limits the viewport from the FOV statically; the cause must be confirmed at runtime. v5 adds:
- **CamDiag** (`CameraDiagnostics`): logs every Camera (`FindObjectsOfType(Camera)`, sorted by depth) before entering first
  person, right after and ~1 s later: path, enabled, depth, type, rect, pixelRect, pixel size, target texture, clear flags +
  background, cullingMask, fov/near/far, aspect vs pixel aspect, rendering path (set/actual), HDR, dynamic resolution,
  usePhysicalProperties, and the **effective projection** (vertical FOV = 2 atan(1/m11), aspect = m11/m00, shift m02/m12,
  CUSTOM when it differs from fieldOfView/aspect). `lensShift` / `sensorSize` / `gateFit` are stripped in this build (no
  NativeMethodInfoPtr) -> "n/a". Plus: Behaviours on each camera GameObject, renderers parented under the mod camera,
  enabled scene renderers whose bounds are within 1 m of the eye (not the player's), UIViewport instances,
  `ReduceResolution.IsActive`, `ScalableBufferManager` factors, Screen size/mode. Watchdog (every 30 frames) logs changes
  of rect/pixelRect/target/projection while in first person. Rect/Matrix4x4/Bounds read via fields (`m_XMin`, `m00`, `m_Center`).
- **FpView** guards (all restored on exit): `ForceFullViewport` (rect = full; Harmony prefix skips `UIViewport.LateUpdate`
  for the mod camera), `SyncHelperCameras` (child cameras copy fov/near/far), `HideCameraAttachedRenderers` (Mesh/Skinned/Sprite
  renderers under the camera disabled via `Renderer.enabled`; particles kept), `DisableCameraComponents` (type names).
- **Bisect key** (`BisectKey`, F10): single-factor steps relative to the full first-person state: 1 game FOV, 2 game near,
  3 game far, 4 sky off, 5 game fog, 6 game layerCullDistances, 7 v5 guards off, 8.. each enabled Behaviour on the camera
  disabled; each step logs `bisect N state: rect ... fov ... proj ...` and shows `bisect N/M: ...` on screen.
- `FirstPersonSky.Enabled` master switch; `CrosshairOutline` (dark 1 px outline via `GUI.color`, IMGUI static setter is native).
- Note: `Renderer.forceRenderingOff` has no `NativeMethodInfoPtr_*forceRenderingOff*` string in the CoreModule interop
  (BodyHider/FpEffects use it and report it working); new code uses `Renderer.enabled`.

## Third person v6 (WoW-style over-the-shoulder camera)

- Modes: `CamMode { TopDown, ThirdPerson, FirstPerson }` in `CameraLogic`; F7 (`ThirdPerson.ModeKey`) cycles, F8 toggles first person.
  The former first-person enter/exit is now the shared **close camera** state (`EnterClose` / `ExitClose`: saved fov/near/far/cull,
  camera local pose, `c.Zoom` (restored on exit; the game's wheel zoom keeps running underneath), fog, sky, WASD, keys, view guards).
  Mode parts (`EnterModeParts` / `LeaveModeParts`): first person = BodyHider + FpEffects; third person = `ThirdPerson`. FP <-> TP switches
  only the mode parts (yaw carried over). `FpKeys` / `FpControls` check `InCloseCamera`.
- Pose: pivot = target + up * PivotHeight (critically damped spring), rot = Euler(pitch, heading + orbit, 0),
  desired = pivot + yawRight * shoulder - forward * distance. Heading = WASD movement yaw. RMB (WoW) / captured look folds the orbit
  offset into the heading; LMB changes only the orbit offset; AutoFollow decays the offset while walking.
- WoW mouse (`MouseLock.UpdateWow`): on a button press not over NGUI (`UICamera.isOverUI`), the Win32 cursor position is the anchor;
  while held the cursor is hidden and warped back to the anchor every frame (delta = movement). The game therefore sees the press
  and release at the anchor -> a click without drag is a normal click; held-button move flags are stripped by the HandleInputActions
  prefix (now also active for WoW with a free cursor). MouseLockKey = centre capture (Shooter-like). Interact uses `Input.mousePosition`
  in WoW (free cursor), the screen centre otherwise. Ground-aim correction (`AimPoint`) only while centre-captured.
- Collision: Physics interop check — `Physics.SphereCast(...)` returning bool is NOT in the interop; native ones used:
  `Physics.SphereCastNonAlloc(Vector3, float, Vector3, RaycastHit[], float, int, QueryTriggerInteraction)` (preallocated
  `Il2CppStructArray<RaycastHit>(24)`, hits unordered, distance 0 = start overlap -> ignored, colliders under the player skipped) and
  fallback `Physics.Raycast(Vector3, Vector3, out RaycastHit, float, int, QTI)`. `RaycastHit.distance` / `.collider` have native getters.
  `LayerMask.NameToLayer` is native, `LayerToName` is stripped.
- Layers (TagManager in `globalgamemanagers`, order not verified): Default, TransparentFX, Ignore Raycast, Water, LocalPlayerCharacter,
  ClickableObject, GUI, Ground, PlayerPositionTrigger, IgnoreClickDecal, IgnoreClickDecalAndXRay, PlayerPositionCollider, EFFECT_ZONE,
  MAP_PROP, PostEffectVolume, XRay, GUI_3D, MATTE_3D, BLIT_3D, GroundNoXRay, FadeOutTrigger, NO_XRAY, OCEAN, ... The game's own ground ray
  (`RayCastGroundHit`) uses Physics.Raycast, so ground colliders exist; buildings are expected on XRay / NO_XRAY / MAP_PROP / Default.
  Default `CollisionLayers` = Default, Ground, GroundNoXRay, XRay, NO_XRAY, IgnoreClickDecal, IgnoreClickDecalAndXRay, MAP_PROP;
  the resolved mask and every new blocking collider (`camera pulled in by '<name>' (layer N)`) are logged to tune it.
- Fade: camera within FadeDistance of the pivot/head -> `BodyHider.Enter(target, head, hideAll: true)` + `FpEffects`; restored with 0.3 m hysteresis.

**Краш 20.10:** `Il2CppClassPointerStore<Il2CppSystem.Int32>.NativeClassPtr` в этой сборке может быть 0 → `il2cpp_value_box(null)` роняет клиент. Упаковка чисел теперь ищет класс по имени (`IL2CPP.GetIl2CppClass("mscorlib.dll","System",...)`) и при неудаче бросает управляемое исключение; отправка обёрнута в try.

## v7 (0.7.0): performance + WoW right button

### Right button walked the character (third person WoW)
- Log 20.10: `HarmonyX: Failed to patch LocalInputHandler::HandleInputActions(...): Cannot get result from void method` for both
  ActionBarsMod and CameraMod -> the click filter **never ran** (no `click: HandleInputActions mask` line in any log). ActionBarsMod's
  prefix on that void method declares `ref LocalInputHandler.f __result`; HarmonyX keeps the failed patch in the method's shared
  patch info, so every later patch of the same method fails too (load order ActionBarsMod -> CameraMod).
- Masks (`EvaluateActionsByMouseInput` 0x1819977E0, MouseOptions 0): button 1 (right) press = 0x28, **held / release = 0x200 (move)**;
  button 0 (left) press 0x454, release 0x3000 / held 0x800 / 0x1000 (charge interaction). `UpdateInputForButtonHeld` (0x1819B3330)
  calls `OnHandleInput(touch, id, scheme, 2, ..)` every frame for each active touch (mouse buttons included), so holding RMB = the
  game walks to the cursor every frame; with WoW look the cursor is parked at the press point -> the character walks there.
- `OnHandleInput` (0x1819A4700): `mask = EvaluateActionsByInput(touchId, scheme, event, out mods)` (0x181997390); if mask != 0:
  `|2` when (mask & 0x201) and event 1, `|8` when mask & 0x100, then `HandleInputActions(drv.a(pos, camera), mask, mods, touchId)`.
  Events `g`: 0 press, 1 release, 2 held. NGUI mouse touch ids: -1 left, -2 right, -3 middle.
- Fix: Harmony **postfix on `EvaluateActionsByInput`** (returns the mask; not patched by anyone else). Close camera with a captured
  cursor or WoW: move flags 0x201 stripped (`ClickMove = false`), left press + 0x8|0x10 (`LeftClickAttack`). WoW with a free cursor:
  right press -> 0 (mask remembered without 0x201), held -> 0, release -> remembered press mask only if the mouse moved
  <= `RightClickMaxDragPixels` (6) and was held <= `RightClickMaxSeconds` (0.6 s) (`MouseLock` accumulates the parked-cursor deltas),
  else 0. Safety net in `ThirdPerson.RmbGuard`: if the character stood still at the RMB press and moves > 0.4 m within 1.5 s with no
  movement key and not both buttons -> `StopMovementAction` once (logged).

### Per-frame cost (static review; the profiler confirms in the next log)
| Hotspot (v6) | Why it costs | v7 |
|---|---|---|
| FpEffects: `FindObjectsOfType(Vfx)` + `(ConstrainableObject)` every 12 frames, `(ParticleSystemRenderer)` every 15, `(WorldObjectAnchoredGui)` every 30 | Unity 6 `FindObjectsOfType` = scene-wide search sorted by instance id; then a wrapper (GC handle) per element, `TryCast`, field reads and parent walks (`IsUnder`: up to 60 `transform.parent` calls, each a wrapper) for every object in the scene, `Renderer.bounds` for every particle renderer | `FindObjectsByType(type, Exclude, SortMode.None)`; element pointers read from the native array (`Perf.Ptrs`, data at +4*IntPtr.Size), walked `SliceSize` (32 Performance / 64) per frame; per-object cache (Vfx ptr + constraint ctrl ptr -> self/socket/renderers, not-self re-checked every 4th scan); self test = membership in a pointer set of the character's transforms (`Perf.Subtree`, one `GetComponentsInChildren<Transform>(true)`) and its ancestors; interval 60 frames (Performance) / `EffectsScanFrames`; nameplates searched every `SceneScanFrames` (600 / 180), re-asserted every 30 frames |
| BodyHider: `GetComponentsInChildren<Renderer>(true)` every 20 frames; per renderer `GetIl2CppType().Name` (string from IL2CPP), head test with parent walks, name lowercasing; deep scan `FindObjectsOfType(SkinnedMeshRenderer)` + `rootBone` walk for every skinned mesh every 180 frames | many interop calls + allocations per scan | classification cached per renderer; every 20 frames only the hidden ones are re-asserted; renderer list every `BodyRescanFrames` (120 / 3x EffectsScanFrames); deep scan every `SceneScanFrames` with `FindObjectsByType`, pointer sets, skinned meshes known not to be ours skipped |
| StatusOverlay.OnGUI | runs for every IMGUI event (Layout + Repaint + each input event); each call rebuilt the info line (concat, enum ToString), marshalled strings to IL2CPP (`GUI.Label(Rect,string)`), fetched `GUI.skin` styles | returns unless `Event.current.type == Repaint` (no GUILayout used); cached `GUIContent` per text, styles + `Texture2D.whiteTexture` once, info line rebuilt <= 5x/s, hint key every 32 frames; status timeout via Stopwatch |
| ThirdPerson collision | per hit `IsUnder(collider.transform, target)`; blocker string (`col.name + layer`) built every frame while blocked | collider pointer -> self cache; blocker name only when the blocking collider changes and logs remain |
| FpView.Update | rect read + helper camera sync (6 interop calls per helper) every frame, watchdog string (projection dump) every 30 frames | every 10 frames; watchdog every 120 |
| FogControl | 3-4 `RenderSettings` writes per frame on top of the `ApplyFog` postfix (which already re-applies after every game write) | postfix only; per-frame fallback every 30 frames; postfix returns at once in top-down |
| FpKeys.Remap | string concat + 2 enum `ToString` per frame and per hotkey event | reference/enum compare |
| FpSky | procedural: 2 `SetColor(string)` + 3 `HasProperty` + `SetFloat` every 32 frames; dome clouds: new `Vector2[1225]` + managed->IL2CPP copy every 2nd frame | colours only when changed (checked every 60 frames, property ids cached); one reused `Il2CppStructArray<Vector2>` written in place, every 4th frame |
| Render settings | close camera: far 1000 -> **1500** while the linear fog ends at ~210 m (game 26-53 m x FogDistanceMultiplier 4): everything from ~210 to 1500 m drawn fully fogged; `layerCullDistances` cleared; lodBias 2; GraphicsMod low camera shadows **250 m x 4 cascades VeryHigh** (game: 85 m, 1 cascade) | Performance preset: far 600, fitted to the fog end (`FarClipFitFog`: end x 1.5 + 50 m, >= 250 m, checked every 30 frames), game layer cull kept, lodBias 1.5; GraphicsMod `LowCameraPreset = Performance`: low-camera shadows <= 120 m |
| GraphicsMod per frame | `Camera.main`, 5 `QualitySettings` getters, per-light getter every frame; `FindObjectsOfType(Light)` every 120 frames; `FindObjectsOfType(SkinnedMeshRenderer)` + `GetComponentsInChildren` per root (proxy test re-run every scan) every 300 frames | check every 4th frame; lights every 600 frames; character scan incremental (seen set, cleared every 60 s), no sort |

Interop checks for the new calls (NativeMethodInfoPtr present): `Object.FindObjectsByType(Type, FindObjectsInactive, FindObjectsSortMode)`,
`Event.current`, `Event.type`, `GUI.skin`, `GUISkin.label`, `GUIContent(string)`, `GUI.Label(Rect, GUIContent, GUIStyle)`,
`Texture2D.whiteTexture`, `Shader.PropertyToID`, `Material.SetColor(int, Color)`, `Mesh.uv` setter (Il2CppStructArray<Vector2>),
`LocalInputHandler.EvaluateActionsByInput_Private_e_Int32_b_g_byref_h_0`. `ProfilerRecorder.StartNew` exists but the
`ProfilerCategory` getters are stripped, so no render counters (draw calls / shadow casters) in the profiler.

### Profiler
`Perf` (Stopwatch timestamps, managed only): sections total (whole LateUpdate postfix), pose, collision, body, effects, fog, move, keys,
controls, sky, view, aim, gui (OnGUI), click (EvaluateActionsByInput postfix), hotkey (dispatcher prefix), fogHook (ApplyFog postfix).
Every `ProfileIntervalSec` and on each mode change: `CameraMod perf [<mode>, <s> s, <n> frames]: fps F (frame avg A ms, max M ms);
mod T ms/frame, <section> avg (max X) ... | render: far, fov, fog on/off end, shadows m x cascades, lodBias, layerCull, preset,
effects hidden, body hidden`. Sections < 0.005 ms/frame with max < 0.2 ms are omitted. GraphicsMod logs `GraphicsMod perf [...]`.
