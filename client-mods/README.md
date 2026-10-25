# Client mods (BepInEx 6, for the local server only)

Russian version (more detail, full log-line reference): [README.ru.md](README.ru.md).

Optional client-side plugins that make playing on **this local server** nicer: a free camera with third- and
first-person views, better shadows and draw distance, and two extra action bars fed by the server.

> **Read first.**
> - **Local server only.** Never start a client that has BepInEx installed against the official servers.
> - **Your own responsibility.** Installing a plugin loader into a game client may violate the terms of service that
>   apply to you; whether you do it is your decision.
> - **No game files here.** This folder contains only our own source code, notes and our own compiled plugin DLLs. It
>   contains no game files, no interop or BepInEx binaries and no decompiled game code. BepInEx is downloaded from its
>   official build server at install time.
> - **Anti-cheat is not touched.** Nothing here disables, bypasses or interferes with any anti-cheat or protection
>   system, and the installer never modifies existing game files. BepInEx and an anti-cheat do not mix: use a client
>   copy that you only ever run against the local server.

Built for client build **1.32.030.345319** and **BepInEx 6 bleeding edge #788**
(`BepInEx-Unity.IL2CPP-win-x64-6.0.0-be.788+5b766a3.zip` from `builds.bepinex.dev`, SHA-256
`f4cc496bd098a0df4164b81e3737297707f13a47c2478dba2f60eefab784817a`). Other client builds or BepInEx builds may fail to
hook (the mods log what they could not hook and keep the game running).

| Folder | What it is |
|---|---|
| `CameraMod/` | Camera: farther zoom, free rotation, third-person ("WoW-style") and first-person views. Notes: `CameraMod/NOTES.md` |
| `GraphicsMod/` | Shadows, shadow cascades, LOD and anti-aliasing beyond the game's settings. Notes: `GraphicsMod/NOTES.md` |
| `ActionBarsMod/` | Two extra action bars of six buttons with abilities granted by worn items (needs this server) |
| `HelloMod/` | Tiny compatibility probe (source only): logs that BepInEx loaded it |
| `dist/` | Prebuilt `CameraMod.dll`, `GraphicsMod.dll`, `ActionBarsMod.dll` (built from these sources) |

## Install (prebuilt)

From the repository root, in PowerShell:

```powershell
powershell -ExecutionPolicy Bypass -File tools\install-client-mods.ps1 -GameDir "C:\Games\Albion\game"
```

`-GameDir` is the client folder that contains `Albion-Online.exe`. The script

1. checks the folder and prints the warnings above;
2. downloads BepInEx #788 from `builds.bepinex.dev` and **aborts if the SHA-256 does not match**;
3. extracts it **without overwriting** any file that already exists (except files it installed itself on an earlier run);
4. copies `client-mods/dist/*.dll` into `<GameDir>\BepInEx\plugins`.

It adds to the client folder: `winhttp.dll` (the Doorstop loader), `doorstop_config.ini`, `.doorstop_version`,
`changelog.txt`, `BepInEx\` and `dotnet\`. `-SkipBepInEx` only copies the plugins (BepInEx already installed).

**Hard-linked client copies.** If the client folder is a copy made with NTFS hard links to another install, its files
share their data with the original. Adding new files is safe; **never edit an existing file in place** (the original
would change too). The installer only adds files and warns when it detects hard links.

**First launch** takes several minutes: BepInEx downloads the Unity base libraries
(`https://unity.bepinex.dev/libraries/6000.3.12.zip`) and generates the interop assemblies into `BepInEx\interop`.
Log: `<GameDir>\BepInEx\LogOutput.log`. Every mod writes its lines with its own prefix (`CameraMod:`, `GraphicsMod:`,
`ActionBarsMod:`).

**Disable** all mods: rename or delete `<GameDir>\winhttp.dll` (the client then starts as usual). **Disable one** mod:
delete its DLL from `BepInEx\plugins` or set `General.Enabled = false` in its config.
**Uninstall:** `tools\uninstall-client-mods.ps1 -GameDir "..."` (asks first; `-Force` skips the question).

## Build from source

Needs the .NET SDK and one game launch with BepInEx (so that `BepInEx\core` and `BepInEx\interop` exist):

```powershell
powershell -ExecutionPolicy Bypass -File tools\build-client-mods.ps1 -GameDir "C:\Games\Albion\game"
```

or by hand: `dotnet build -c Release -p:GameDir="C:\Games\Albion\game"` in a mod folder, then copy
`bin\Release\net6.0\<Mod>.dll` into `<GameDir>\BepInEx\plugins`. Without `-p:GameDir` the projects look for a folder
named `game-local` next to the repository.

## Configuration

Each mod creates its config on the first run in `<GameDir>\BepInEx\config\`: `albionlocal.camera.cfg`,
`albionlocal.graphics.cfg`, `albionlocal.actionbars.cfg`. Changes apply after a client restart. Every option is
commented in the file.

## CameraMod

Hooks the game's follow camera (`ActorCameraController.LateUpdate`) with Harmony and only adjusts the position the game
already computed. Until you press something, the camera behaves as in the normal game.

**Top-down view (default):**
- Mouse wheel works as in the game; past the game's limit it keeps zooming out (`MaxDistanceMultiplier`, x2) or in
  (`MinDistanceMultiplier`, x0.5), smoothly.
- Middle mouse button (or Alt + right button) + mouse move: rotate around the character and tilt (`MinPitch`..`MaxPitch`, 5..85°).
- `Home`: reset rotation and extra zoom.

**Views:** `F7` cycles top-down → third person → first person → top-down; `F8` toggles first person directly. Leaving
either view restores everything (FOV, clip planes, fog, sky, body, effects, the game's zoom).

**Third person ("WoW-style", `ThirdPerson` section):**
- `LookMode = WoW` (default): cursor visible. Hold **right button** to look (walking direction turns with the camera),
  hold **left button** to orbit without changing direction, **both buttons** walk forward. A short right click without
  dragging is passed to the game (attack under the cursor). **CapsLock** toggles permanent mouse-look with a crosshair.
  `LookMode = Shooter`: cursor captured on the crosshair as in first person.
- **WASD** walks relative to the camera; **G** interacts with what is under the cursor/crosshair.
- Wheel: distance `MinDistance` 1.5 .. `MaxDistance` 15 m (default `Distance` 5). `ShoulderOffset` 0.6 m, **V** swaps
  the shoulder. Pitch -40..70°, FOV 70.
- Camera collision against the world (`Collision`, `CollisionLayers`); the character fades when the camera is closer
  than `FadeDistance` (1 m).

**First person:**
- Shooter-style mouse (`FirstPersonControls.MouseLook = Always`): hold **left Alt** or toggle **CapsLock** to free the
  cursor; it is freed automatically while a game window or the chat is open. `Sensitivity`, `InvertY`, pitch limits.
- **Left click** attacks/uses what is under the crosshair; clicking the ground does not move the character (walk with
  **WASD**). **G** interacts. Right button: `RightMouse = Game / None / Hotkey`.
- Abilities: Q E R F 1 2 as usual; `FpSpellKeys` remaps the keys that WASD takes over (default: **3** or mouse back
  button = W, **4** or mouse forward button = D, **Z** = A). Alt + key casts on yourself, as in the game. Ground
  abilities aim at the crosshair point (`AimMaxDistance` 15 m when aiming at the horizon).
- Own body: `ShowBody = None / BodyOnly / All`; own effects near the face hidden (`FirstPersonEffects`); own nameplate
  hidden (`HideOwnNameplate`).
- Fog (`FirstPersonFog = Off / Reduced / Default`), a sky dome or Unity procedural sky (`FirstPersonSky`), smoothing
  and eye height (`FirstPersonCamera`), FOV 75.
- **F1** shows a key help panel; a status line at the bottom left shows the mode and cursor state.
- **F10** (`FirstPersonDiagnostics.BisectKey`) undoes one change at a time, to find a rendering problem.

**Performance:** `[Performance] Preset = Performance` (default) keeps the far clip at 600 m or the end of the fog,
lodBias 1.5 and the game's layer cull distances; `Preset = Custom` uses the individual settings. `ProfileLog = true`
writes a `CameraMod perf` line every 10 s.

**Limitations:** the game is designed for a top-down view: at low angles you may see unloaded map areas, holes,
clipped shadows and LOD changes. Mobs and players farther than about 40 m are not sent by the server (`Visibility` in
`config/world.jsonc`). Inside buildings the game switches to its interior camera; extra zoom and rotation apply on
top of it.

## GraphicsMod

The game uses Unity's built-in pipeline with deferred lighting, applies its graphics options in `GraphicOptions` and
shortens the shadow distance when the camera zooms in. After each of those (Harmony postfixes) and cheaply every few
frames, the mod sets its own values:

- shadow distance `ShadowDistance` (150 m); with a low camera (third/first person) `ShadowDistanceFirstPerson`, capped
  by `[Performance] LowCameraShadowDistance` (120 m) under `LowCameraPreset = Performance`;
- 4 shadow cascades (`CascadeSplits`), VeryHigh resolution, StableFit projection, soft shadows, shadows on even when
  the game menu turns them off (`ForceShadowsOn`);
- `LodBias` 2 (only raises it), forced anisotropic filtering, SMAA High (MSAA is impossible with deferred lighting);
- characters and mobs cast shadows (`CharacterShadows`);
- `Preset = UltraShadows`: 300 m (400 m low camera), denser cascades, LodBias 3.

**Keys:** `F9` toggles the mod's values on/off, to compare with the game's own.
**Options:** `0` / `-1` / `Game` keeps the game's value; also `ShadowStrength`, `LightShadowResolution`, `ShadowBias`,
`ShadowNormalBias`, `PixelLightCount`, `AntiAliasing` (Game/Off/FXAA/SMAA/TAA), `StartActive`.
**Limitations:** shadows beyond ~150-300 m cost frame time; shadow maps above VeryHigh, `maximumLODLevel` and MSAA are
stripped from the game build.

## ActionBarsMod (needs this server)

Two rows of six buttons above the game's ability bar (Q W E R D F), filled with abilities granted by the items you
wear. It talks to this server through its own operations and events (9001-9005): after each zone change it says
hello and only then does the server send bars, cooldowns, refusals and item→ability data. A client without the mod sees
none of it. Use it only with this server.

- **Look (`Layout.Style`):** `Native` (default) clones the game's own ability button, so the bars look exactly like the
  game's; `Atlas` draws them from the game's sprites; `Classic` is a flat look.
- **Tooltips:** `SpellTooltips = Game` (the game's own ability tooltip) / `Mod` / `Off`. `ItemTooltips` adds an
  "Extra abilities" section to item tooltips (inventory, bank, market, loot) and a panel next to the item info window,
  with the reason for each ability (unchosen variant, item type, enchantment, masterpiece, artifact).
- **Keys:** lower bar `Shift+1..6`, upper bar `Ctrl+1..6` (configurable in `[Keys]`, e.g. `Ctrl+Q`, `Alt+3`,
  `Mouse3`); clicking a button works too. Keys do nothing while the chat is open; `1`/`2` (potion/food) stay the game's.
- **Casting:** `CastMethod = Auto` (default) runs the ability through the client's own cast path (aiming, cast bar and
  animation as for normal abilities); otherwise it asks the server directly (`Op`).
- **Which abilities (server side, `content/extraspells.json`):** every worn official item grants abilities from its own
  data: the weapon its unchosen Q/W/E variants, the off-hand one by type, helmet/armour/boots their unchosen variants,
  cape SPRINT, bag a cleanse or barrier, mount a sprint ability; bonuses for enchantment, masterpiece quality and
  artifacts; up to 12. Explicit `Items` entries override the rules; the Studio's item editor can generate them.
  GM commands: `.extrabar list | explain | set <1-12> <SPELL|-> | clear [n] | reload`.

## Logs worth sending with a bug report

`<GameDir>\BepInEx\LogOutput.log`: every line starting with `CameraMod:`, `GraphicsMod:` or `ActionBarsMod:`,
especially `hooked ...` lines and anything with `failed`, `NOT applied`, `unavailable` or `error`.
