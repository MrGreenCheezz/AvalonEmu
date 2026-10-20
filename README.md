# albion-local-server

An educational server emulator, written in C# (.NET 10), for studying how an MMO client and server talk to each other
(Photon UDP transport, Protocol18 serialization, encrypted handshakes, world simulation). It is designed to run on your
own machine or LAN, for one player or a few friends.

## Disclaimer — please read

- **Educational project.** This is a personal learning and research project about network protocols and game-server
  architecture. It is not a commercial product and is not meant to replace, compete with or harm any official service.
- **No affiliation.** This project has nothing to do with Albion Online, Sandbox Interactive GmbH or any of their
  partners. It is not endorsed, sponsored or supported by them. "Albion Online" is a trademark of its owner and is
  mentioned only to describe what the code interoperates with.
- **No client, no client patches.** This repository does **not** contain, distribute or link to the game client, any
  client file, any modified client or any patch of the game's own files. It contains no game assets and no game data
  files. At startup the server reads the GameData files of a client installation that *you* already have; nothing is
  copied into this repository. The `main` branch contains no client modifications at all. **This `modifications`
  branch** adds *optional* client-side plugins (BepInEx plugins, as source code plus our own prebuilt DLLs, see
  `client-mods/`) and an installer that downloads BepInEx from its official source at install time; still no game
  files, no patches of the game's own files and no anti-cheat circumvention. Its optional ModKit
  (`tools/AlbionLocal.ModKit`) can, when *you* run it, rebuild GameData files with your own content packs into a
  *separate local client copy*; it never writes into the original install, and nothing it produces is part of this
  repository.
- **No anti-cheat circumvention.** Nothing here bypasses, disables or interferes with any anti-cheat or protection
  system, and such contributions will not be accepted.
- **Never use it against official servers.** The code only *answers* a client as a server. It does not connect to,
  automate or interfere with official servers or other players.
- **Your responsibility.** How (and whether) you point a client at a local server is entirely your own decision and
  responsibility; check the terms of service that apply to you. The authors provide no support for that.

## Modifications branch

You are on the **`modifications`** branch: the server of `main` plus everything added since, and optional client-side
mods. `main` stays the plain server.

**New on the server** (compared with `main`):

- **Modding without recompiling the server:** C# **plugins** on a stable plugin API (`src/AlbionLocal.Plugin.Api`,
  `plugins/README.md`; gameplay hooks, scripted spells, hot reload with `.plugins reload`), **no-code rules**
  (When → If → Do → Whom, `plugins/RULES.md`, applied live), **slot spells** (change the ability in a slot from plugins,
  rules or `.slot`).
- **Studio** (`/studio` in the web panel): a visual content editor for items, abilities, recipes, drops, goals and rules,
  with icons, pickers, live preview and validation.
- **ModKit** (`tools/AlbionLocal.ModKit`, Russian guide in `docs/MODDING.ru.md`): content packs with your own items and
  abilities made from the official assets (`mods/example-blade` is a sample).
- **Web panel** (`http://127.0.0.1:8080/`): status, registration, admin pages (players, accounts, bans, log, backups),
  a mods editor with client icons.
- **Extra action bars:** two bars of six abilities granted by worn items (rules in `content/extraspells.json`: every
  official item grants spells from its own data, plus enchantment, masterpiece and artifact bonuses). Needs the
  client's `ActionBarsMod`.
- **Gameplay:** companions (hire tank/healer/melee/ranged bots into the party), arena queues (1v1/2v2/5v5) with bots and
  Elo ladders, hunts (tracking toolkits, wild tracks, hunt quest chains), labourers (island houses, furniture, labour),
  awakened weapons and item traits, the PvE side of faction warfare, island construction and animals, gathering tomes
  and fishing bait, expedition bonus chests, market price history, cosmetics from content, stronger hostile bots, a
  richer starter kit, mob AI and spell-condition improvements, loot balance.
- **Client checklist and checker:** `content/checks.json` (152 checks by area) and the GM command `.check`, which sets up
  each check in game and records the result.

**Optional client mods** (`client-mods/`, details in [client-mods/README.md](client-mods/README.md)): `CameraMod`
(far zoom, free rotation, third- and first-person views), `GraphicsMod` (shadows and draw distance beyond the game's
settings), `ActionBarsMod` (the extra action bars). They are BepInEx 6 plugins, built for client build
**1.32.030.345319** and **BepInEx 6 bleeding edge #788**.

> **Client mods disclaimer.** The client mods are optional client-side plugins for use with **this local server only**,
> never with the official servers. No game files are included; BepInEx is downloaded from its official source
> (`builds.bepinex.dev`) at install time and verified by SHA-256; the installer only adds files and never modifies,
> patches or bypasses anything, anti-cheat included. Installing them is entirely your own decision and responsibility.

### Quick install

**Server** (needs the [.NET 10 SDK](https://dotnet.microsoft.com/) and a client installation you own):

```powershell
git clone -b modifications https://github.com/MrGreenCheezz/albion-local-server.git
cd albion-local-server
powershell -ExecutionPolicy Bypass -File tools\start-server.ps1 -GameDir "C:\Games\Albion\game"
```

`-GameDir` is the client folder with `Albion-Online.exe` (the server reads its `Albion-Online_Data\StreamingAssets\GameData`).
The script builds the server into `bin\run` and starts it with its console. On the console, create an admin account
for the web panel: `.account create <name> <password>`, then `.account level <name> Admin`.

**Client mods** (optional; best on a separate client copy that is only used with the local server):

```powershell
powershell -ExecutionPolicy Bypass -File tools\install-client-mods.ps1 -GameDir "C:\Games\Albion\game-local"
```

It downloads BepInEx #788, checks its SHA-256, adds it without overwriting existing files and copies the prebuilt
plugins from `client-mods/dist`. The first client start then takes several minutes (BepInEx generates its interop
assemblies; log: `BepInEx\LogOutput.log`). If the client copy was made with NTFS hard links to another install, adding
files is safe, but never edit an existing file in place. Remove everything with `tools\uninstall-client-mods.ps1`;
build the mods yourself with `tools\build-client-mods.ps1` (after one client start with BepInEx).

### Кратко по-русски

Ветка **`modifications`** — сервер из `main` плюс всё новое (плагины на C#, правила без кода, Студия `/studio`,
ModKit, веб-панель, дополнительные панели способностей, компаньоны, арена, охота, работники, пробуждённое оружие и др.)
и необязательные моды клиента в `client-mods/` (камера, графика, доп. панели; исходники и готовые DLL). Моды — только
для этого локального сервера, никогда для официальных серверов. Игровых файлов здесь нет; BepInEx скачивается с
официального сайта при установке и проверяется по SHA-256; установщик только добавляет файлы и ничего не обходит
(античит не затрагивается). Ставите — на свою ответственность. Сервер: `tools\start-server.ps1 -GameDir "<папка клиента>"`;
моды: `tools\install-client-mods.ps1 -GameDir "<папка клиента>"`, удаление — `tools\uninstall-client-mods.ps1`.
Подробно: [client-mods/README.ru.md](client-mods/README.ru.md).

## What works

The server implements a large part of what a PvE player meets in the game, as a local, self-contained emulation:

- **Core:** login and game servers over Photon UDP (reliable channels, fragmentation, DH/AES encryption), accounts
  with PBKDF2 passwords, several characters per account, GM levels and a chat command system, SQLite persistence with
  schema migrations.
- **World:** cluster loading from the client's GameData, visibility, cluster travel, paid fast travel by cargo, per-city
  banks and markets, islands with farming and animals, stables and mounts, crafting and refining stations in every city
  with city bonuses, enchanting, quality rolls.
- **Combat:** auto-attacks, spells with conditions and effects, buffs and crowd control, mob AI with path finding and
  line of sight, boss mechanics (spawn/death casts, dashes, knockbacks), knock-down and party revive, PvP flags and
  duels (PvP only by consent in the default PvE preset).
- **PvE content:** open-world camps and chests, elite mobs, world bosses, journals, on-demand invasions, expeditions,
  random dungeons with the client's own layout generator and shrines, static dungeons, corrupted dungeons, hellgates,
  Roads of Avalon, the Mists, season goals and bounties.
- **Social:** chat server (whispers, guild, party, world), friends, mail, trading, guilds with ranks, contracts and goals,
  emotes, loadouts, map pings.
- **Bots:** simple player bots (city walkers, gatherers, patrols).

The detailed, per-feature reference (English) is in [docs/REFERENCE.md](docs/REFERENCE.md); the architecture notes
(Russian) are in [docs/ARCHITECTURE.ru.md](docs/ARCHITECTURE.ru.md), and the modding guide (Russian, referred to as
`MODDING.md` in the docs) in [docs/MODDING.ru.md](docs/MODDING.ru.md). Both mention a private analysis workspace
(`../analysis`, `../captures`) that is not part of this repository.

## Repository layout

| Path | Contents |
|---|---|
| `src/AlbionLocal.Photon` | Photon UDP transport, Protocol18 serializer, encryption, message layer |
| `src/AlbionLocal.Protocol` | Message classes (generated) and wire rules |
| `src/AlbionLocal.Server` | Login (5055), game (5056) and chat (5057) servers, world simulation, systems, persistence |
| `tests/AlbionLocal.SelfTest` | Offline self-test with an in-process client (no network, no real client needed to run it, but it reads the GameData files) |
| `src/AlbionLocal.Plugin.Api` | The stable API that server plugins compile against |
| `tools/AlbionLocal.CapDecode` | Decoder for packet captures of your own sessions |
| `tools/AlbionLocal.ModKit` | Content packs: own items and abilities from the official assets |
| `tools/*.ps1` | `start-server`, `install-client-mods`, `uninstall-client-mods`, `build-client-mods` |
| `tools/checklist` | Generator of a readable checklist from `content/checks.json` |
| `plugins/` | Sample C# plugins (`welcome`, `example-spells`, `bounty-hunter`), plugin and rules guides |
| `mods/` | Content packs (`example-blade` sample) |
| `web/studio` | The Studio (visual editor) served by the web panel |
| `client-mods/` | Optional BepInEx client plugins: sources, notes, prebuilt DLLs in `dist/` |
| `config/albionlocal.json` | Server settings (every key optional, commented) |
| `content/` | Server-side content: spawns, kits, events, dungeons, goals, bots and other tunables |

## Requirements

- [.NET 10 SDK](https://dotnet.microsoft.com/)
- Windows, Linux or macOS (developed on Windows)
- The GameData folder of a client installation you own (`<client>/Albion-Online_Data/StreamingAssets/GameData`).
  The server will not start without it.

## Client version

The server is built against client build **1.32.030.345319** (`albiononline-win32-full-1.32.030.345319`, the first
line of the client's `version.txt`). The protocol (operation and event codes, message fields) and the GameData formats
follow that build.

Other builds are not supported: an update can renumber operations and events or change the data files, and the server
may then fail to start or misbehave in game. At each start the server logs the build and a SHA-256 of every GameData
file it reads, keeps them in `data/clientdata.json`, and warns when they differ from the previous start.

## Build and run

```bash
dotnet build
```

Tell the server where the GameData folder is, either in `config/albionlocal.json` (`Paths:ClientGameData`) or on the
command line, then start it from the repository root (it keeps its database and log in `./data`):

```bash
dotnet run --project src/AlbionLocal.Server -- -v --Paths:ClientGameData="C:/Games/AlbionOnline/game/Albion-Online_Data/StreamingAssets/GameData"
```

On start it prints the database schema version and the three listening ports. By default it listens on `127.0.0.1`
only; see the "second PC" part of [docs/REFERENCE.md](docs/REFERENCE.md) for LAN settings (`Network:BindAddress`,
`Network:PublicAddress`) and the firewall ports.

Run the self-test. It reads the GameData folder from the fixed path `../game-local/Albion-Online_Data/StreamingAssets/GameData`
next to the repository, so put a link there to your client installation (for example a directory junction or symlink
named `game-local` next to this folder):

```bash
dotnet run --project tests/AlbionLocal.SelfTest
```

Comparisons against generated "golden" data files are skipped, because those files are not published.

## Configuration

- `config/albionlocal.json` — ports, presets (`pve` by default: PvP by consent, no full loot, rate multipliers), world
  and economy options. Any key can be overridden on the command line, e.g. `--Log:Level=Debug`.
- `content/*.json` — gameplay content the server adds or tunes (events, bosses, dungeons, goals, bots, kits…).
- Accounts: the first login creates the account (see "Accounts" in [docs/REFERENCE.md](docs/REFERENCE.md)); console
  commands such as `.account create <name> <password>` manage them.

## Status

Work in progress and built for learning: expect gaps, guesses (marked in the docs as inferences) and breaking changes.
Issues about protocol details or server architecture are welcome; requests involving the official game, its servers,
its client or anti-cheat are not.

## License

MIT (see [LICENSE](LICENSE)): you may use, change and share the code freely, as long as you keep the copyright
notice, i.e. credit the original source (https://github.com/MrGreenCheezz/albion-local-server). The license covers this
repository's own code only; it grants nothing about the game, its client, its data or its trademarks.
