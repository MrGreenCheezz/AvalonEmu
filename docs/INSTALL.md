# Installation guide

This guide takes you from an empty machine to a running server, one step at a time: what the server is made of, what
to install, how to compile it, where every setting lives, and how to run, back up and update it.

**This copy describes the `modifications` branch:** the server of `main` plus modding tools and optional client mods.
[Section 15](#15-modding-this-branch) covers what is specific to this branch.

The build, run, configuration, plugin and self-test commands were run on Windows 10 with .NET SDK 10.0.400 at commit
`60c9003`. These were not run for this guide: the `winget` and firewall commands, Linux, macOS, the Visual Studio
profile, ModKit on a real client and the client mod scripts. Stopping with Ctrl+C is described from the code.

**Connecting a game client is not covered.** This repository contains no client and no client files, and the authors
give no support for pointing a client at a server. See the [disclaimer](../README.md#disclaimer--please-read) and
[section 16](#16-what-this-guide-does-not-cover).

## Contents

1. [How the server is put together](#1-how-the-server-is-put-together)
2. [Requirements](#2-requirements)
3. [Install the .NET SDK](#3-install-the-net-sdk)
4. [Get the source](#4-get-the-source)
5. [Compile](#5-compile)
6. [Point the server at GameData](#6-point-the-server-at-gamedata)
7. [Check the configuration](#7-check-the-configuration)
8. [Start and stop the server](#8-start-and-stop-the-server)
9. [Create accounts](#9-create-accounts)
10. [Where every setting lives](#10-where-every-setting-lives)
11. [Run the self-test](#11-run-the-self-test)
12. [Play on a LAN](#12-play-on-a-lan)
13. [Web panel and backups](#13-web-panel-and-backups)
14. [Update the server](#14-update-the-server)
15. [Modding (this branch)](#15-modding-this-branch)
16. [What this guide does not cover](#16-what-this-guide-does-not-cover)
17. [Troubleshooting](#17-troubleshooting)

### The short version

If you already have the .NET 10 SDK and a GameData folder:

```bash
git clone -b modifications https://github.com/MrGreenCheezz/AvalonEmu.git
cd AvalonEmu
dotnet build
dotnet run --project src/AlbionLocal.Server --no-launch-profile -- --Paths:ClientGameData="C:/Games/AlbionOnline/game/Albion-Online_Data/StreamingAssets/GameData"
```

Then type `.account create <name> <password>` in the server's console.

## 1. How the server is put together

The server is one process, `AlbionLocal.Server`, that runs these listeners:

| Listener | Protocol and default port | Config key | File |
|---|---|---|---|
| Login server | UDP 5055 | `Network:LoginPort` | `config/login.jsonc` |
| Game server (the world) | UDP 5056 | `Network:GamePort` | `config/world.jsonc` |
| Chat server | UDP 5057 | `Network:ChatPort`, `Chat:Enabled` | `config/chat.jsonc` |
| Web panel (off under `Preset: official`) | TCP 8080 | `Web:Port`, `Web:Enabled` | `config/common.jsonc` |

All of them listen on `127.0.0.1` by default, so only the same PC can reach them until you change that
([section 12](#12-play-on-a-lan)).

What the server reads and writes:

| Location | Access | Contents |
|---|---|---|
| `config/*.jsonc` | read at start | The five settings files |
| `content/` | read at start | Server-side tables: spawns, kits, events, dungeons, goals, bots |
| The client's `GameData` folder | read only | Items, spells, clusters and other game data. Never copied or changed |
| `data/` | read and write | Database, log, backups. Created on the first start |

Unlike TrinityCore, there is no database server to install, no SQL to import and no extractor to run. The database is
an embedded SQLite file whose schema is created and upgraded automatically, and the game data is read directly from
the client's files at every start.

The source tree:

```text
AlbionLocal.slnx              the solution
client-mods/                  optional client plugins: sources and prebuilt DLLs
config/                       the five settings files
content/                      server-side content tables
docs/                         this guide, configuration notes, feature reference, modding guide, changelog
mods/                         content packs (example-blade is a sample)
plugins/                      sample C# plugins, plugin and rules guides
src/AlbionLocal.Photon/       Photon UDP transport, serializer, encryption
src/AlbionLocal.Plugin.Api/   the stable API that plugins compile against
src/AlbionLocal.Protocol/     message classes and wire rules
src/AlbionLocal.Server/       the server itself
tests/AlbionLocal.SelfTest/   offline self-test
tools/AlbionLocal.CapDecode/  decoder for captures of your own sessions
tools/AlbionLocal.ModKit/     content pack tool
tools/checklist/              generator of a readable checklist from content/checks.json
tools/start-server.ps1        Windows script that builds and starts the server
tools/*-client-mods.ps1       install, uninstall and build the client mods
web/studio/                   the Studio's front-end, embedded into the server at build time
```

## 2. Requirements

### Required

| What | Needed for | Notes |
|---|---|---|
| [.NET 10 SDK](https://dotnet.microsoft.com/download/dotnet/10.0) | Building and running | All projects target `net10.0`. A published build only needs the .NET 10 runtime, unless it builds plugins from source ([section 5](#5-compile)) |
| Internet access to nuget.org | The first build | Only to restore the packages listed below |
| GameData of client build **1.32.030.345319** | Running and the self-test | Not needed to compile or for `--check-config`. See [section 6](#6-point-the-server-at-gamedata) |

Operating system: Windows, Linux or macOS. The project is developed on Windows, so that is the best-tested platform.

### Installed automatically

`dotnet build` downloads these from nuget.org, as set in `nuget.config`. Only `AlbionLocal.Server` uses external
packages:

| Package | Version | Purpose |
|---|---|---|
| `Microsoft.Data.Sqlite` | 10.0.8 | SQLite access |
| `SQLitePCLRaw.bundle_e_sqlite3` | 2.1.14 | The native SQLite library, pinned to a version with security fixes |

### Optional

| What | Why |
|---|---|
| Git | To clone and update. You can also download a ZIP from GitHub, but then you cannot `git pull` |
| Windows PowerShell or PowerShell 7 | For the `tools/*.ps1` scripts (Windows) |
| An IDE with .NET 10 and `.slnx` support | For debugging. See [Visual Studio](#visual-studio) |

### Not needed

MySQL or any other database server, CMake or a C++ compiler, Docker, or a separate SQLite install. Python is only used
by the optional `tools/checklist` generator.

## 3. Install the .NET SDK

**Windows:** use the installer from the [download page](https://dotnet.microsoft.com/download/dotnet/10.0), or:

```powershell
winget install Microsoft.DotNet.SDK.10
```

**Linux:** follow [Install .NET on Linux](https://learn.microsoft.com/dotnet/core/install/linux) for your distribution.

**macOS:** follow [Install .NET on macOS](https://learn.microsoft.com/dotnet/core/install/macos).

Open a new terminal and check that a 10.x SDK is listed:

```bash
dotnet --list-sdks
```

## 4. Get the source

```bash
git clone -b modifications https://github.com/MrGreenCheezz/AvalonEmu.git
cd AvalonEmu
```

Run every later command from this folder (the repository root) unless the step says otherwise. Paths such as `config`
are relative to the current directory.

The repository has two branches:

| Branch | Contents |
|---|---|
| [`main`](https://github.com/MrGreenCheezz/AvalonEmu/tree/main) (default) | The classic server without modding |
| `modifications` | The server plus modding tools and optional client mods. This guide describes this branch |

The branches can be at different database schema versions, and a database upgraded by a newer schema cannot be opened
by an older one. When this was written, `main` was at v43 and this branch at v42. If you use both, give each its own
`Paths:Saves` folder.

## 5. Compile

Build everything (the server, the self-test and the tools):

```bash
dotnet build
```

The first build also restores the NuGet packages. A full build took about 22 seconds on the test machine. It should end
like this; the odd compiler warning does not matter:

```text
Build succeeded.
    0 Error(s)
```

Other useful forms:

| Command | Result |
|---|---|
| `dotnet build` | Debug build of everything. The server lands in `src/AlbionLocal.Server/bin/Debug/net10.0/` |
| `dotnet build -c Release` | The same with optimizations, in `src/AlbionLocal.Server/bin/Release/net10.0/` |
| `dotnet build src/AlbionLocal.Server` | The server and its dependencies only |
| `dotnet build src/AlbionLocal.Server -o bin/run` | The server into `bin/run`, which is what `tools/start-server.ps1` does |

A running server locks its own output folder, so a rebuild into that folder fails. Building into a folder of its own
such as `bin/run` lets `dotnet build` and the self-test keep working while the server runs.

### A standalone copy

To run the server outside the source tree, publish it and copy `config`, `content`, `mods` and `plugins` next to it,
because publishing does not include them. The Studio's files are embedded in the server, so `web` is not needed. The
target machine only needs the .NET 10 runtime, unless it builds plugins from source, which takes the SDK.

```bash
dotnet publish src/AlbionLocal.Server -c Release -o ../avalon-server
cp -r config content mods plugins ../avalon-server/
```

In PowerShell, replace the `cp` line with:

```powershell
Copy-Item -Recurse config, content, mods, plugins ..\avalon-server\
```

## 6. Point the server at GameData

The server does not start without the GameData folder of a client installation you own:

```text
<client>/Albion-Online_Data/StreamingAssets/GameData
```

Check the build first. The first line of `<client>/version.txt` should be:

```text
albiononline-win32-full-1.32.030.345319
```

Other builds are not supported: an update can renumber operations or change the data files. See
[Client version](../README.md#client-version).

Then choose one of these ways to give the server the path.

### Option A: a private configuration folder (recommended)

`config.private` is ignored by git, so your settings stay out of `git status` and `git pull` never conflicts with them.

Copy all five files:

```bash
cp -r config config.private
```

In PowerShell:

```powershell
Copy-Item -Recurse config config.private
```

Edit `config.private/common.jsonc` and set the path:

```jsonc
"Paths": {
  "ClientGameData": "C:/Games/AlbionOnline/game/Albion-Online_Data/StreamingAssets/GameData",
  "Content": "content"
},
```

Use forward slashes. A single backslash is an invalid escape in JSON (`C:\\Games\\...` also works).

From now on, add `--config-dir=config.private` to every server command.

### Option B: edit `config/common.jsonc`

Set the same key in the distributed file. This is simpler, but git then shows the file as changed.

### Option C: on the command line

```bash
--Paths:ClientGameData="C:/Games/AlbionOnline/game/Albion-Online_Data/StreamingAssets/GameData"
```

### Option D: use the default location

The default value is `../game-local/Albion-Online_Data/StreamingAssets/GameData`, which is a folder named `game-local`
next to the repository. A directory link to your client installation works, and the self-test reads the same default.

On Windows, in PowerShell:

```powershell
New-Item -ItemType Junction -Path ..\game-local -Target "C:\Games\AlbionOnline\game"
```

On Linux or macOS:

```bash
ln -s /path/to/client ../game-local
```

## 7. Check the configuration

Validate the settings without starting anything:

```bash
dotnet run --project src/AlbionLocal.Server --no-launch-profile -- --config-dir=config.private --check-config
```

It prints the values it would use and exits with code 0:

```text
Configuration directory: C:\AvalonEmu\config.private
Files: common.jsonc, login.jsonc, world.jsonc, chat.jsonc, persistence.jsonc
Preset: Official; tutorial: True; persistence: SQLite
Login: 127.0.0.1:5055; world: 127.0.0.1:5056; chat: 127.0.0.1:5057 (enabled: True); public:
GameData: C:\Games\AlbionOnline\game\Albion-Online_Data\StreamingAssets\GameData
Content: C:\AvalonEmu\content
Saves: C:\AvalonEmu\data
Log: C:\AvalonEmu\data\server.log
Rates: Fame 1, CombatFame 1, GatheringFame 1, CraftingFame 1, Harvest 1, Silver 1, Loot 1, Respawn 1, Regrowth 1, CraftingReturn 1, ChampionWeight 1, ChestRespawn 1
Effective fame: combat x1, gathering x1, crafting x1; existing game bonuses still apply
```

Read the `GameData:` line carefully. `--check-config` opens no ports, database or log file and **does not load
GameData**, so a wrong path only shows up at the real start or in the checks below.

Two more offline checks read GameData without opening ports or saves:

```bash
dotnet run --project src/AlbionLocal.Server --no-launch-profile -- --config-dir=config.private --check-tutorial
dotnet run --project src/AlbionLocal.Server --no-launch-profile -- --config-dir=config.private --check-content
```

`--check-content` resolves every cluster of the client's world and took about 12 seconds on the test machine. A good
run ends with a line like `1427 of 1427 clusters pass every check`.

## 8. Start and stop the server

### On Windows: the script

```powershell
powershell -ExecutionPolicy Bypass -File tools\start-server.ps1 -ServerArgs --config-dir=config.private
```

The script builds into `bin\run`, so a running server never blocks a rebuild, then starts the server in the same window.
It always adds `-v` (debug logging).

| Parameter | Meaning |
|---|---|
| `-GameDir <folder>` | The client folder that holds `Albion-Online.exe` |
| `-GameData <folder>` | The GameData folder itself (overrides `-GameDir`) |
| `-NoBuild` | Start the last build without building |
| `-ServerArgs <arguments>` | Further server arguments |

To pass several server arguments, use `-Command`, because `-File` takes them as one string:

```powershell
powershell -ExecutionPolicy Bypass -Command "& .\tools\start-server.ps1 -ServerArgs '--config-dir=config.private','--Log:Level=Debug'"
```

The script is written for Windows. On Linux and macOS, use `dotnet run`.

### On any system: `dotnet run`

```bash
dotnet run --project src/AlbionLocal.Server --no-launch-profile -- --config-dir=config.private
```

Everything after `--` goes to the server. Keep `--no-launch-profile`, so that a local `launchSettings.json` does not
change the arguments or the working directory.

### A built or published copy

Run it from the folder that holds `config`, `content` and `plugins`, because the configuration and plugin folders are
looked up in the current directory:

```bash
cd ../avalon-server
./AlbionLocal.Server --config-dir=config
```

### Visual Studio

Copy `src/AlbionLocal.Server/Properties/launchSettings.example.json` to `launchSettings.json` beside it (git ignores
that file). Open `AlbionLocal.slnx` and make `AlbionLocal.Server` the startup project. The **Albion Local Server**
profile starts the server, and **Check configuration** only validates. Both use `--config-dir=config`, so edit the copy
if you use `config.private`.

### What a good start looks like

The first start took about 3 seconds on the test machine. It creates `data/` and walks the new database through every
schema version. The end of the log looks like this:

```text
Info  [data] client build albiononline-win32-full-1.32.030.345319
Info  [data] client data: 12237 items, 9326 spells (1469 items with spell slots), 704 achievements from C:\Games\AlbionOnline\game\Albion-Online_Data\StreamingAssets\GameData in 1.01 s
...
Info  [db] SQLite: C:\AvalonEmu\data\albionlocal.db
...
Info  [plugins] 3 plugin(s) found, 0 to load:
Info  [login] listening on udp 127.0.0.1:5055
Info  [game] listening on udp 127.0.0.1:5056
Info  [chat] listening on udp 127.0.0.1:5057
Info  [main] 0 persisted character(s); new character start cluster 1008
Info  [main] press Ctrl+C to stop
Info  [main] commands: type .help here, or in the game's Say chat
```

A few `[world] tick took N ms` warnings right after the start are normal.

### Stopping

Press **Ctrl+C** in the server's window. The server saves everything, closes its listeners and the database, then
exits. Killing the process skips that final save, so use it only as a last resort. `.save` on the console saves every
player in game at any time.

The console reads commands from standard input. A server started in the background has no console input, so create
your first accounts in a normal window.

## 9. Create accounts

A new database has no player accounts, and with the distributed settings an unknown login is refused. Type these in
the server's console (the leading dot is optional there):

```text
.account create <name> <password>
.account level <name> Admin
.account list
```

- Passwords are 8 to 72 printable ASCII characters, with no space at either end, and cannot equal the name.
- New accounts start at `Accounts:DefaultLevel`, which is `Player`. The levels are `Player`, `GameMaster` and `Admin`.
- `.account list` also shows a `(local)` account with no characters. That is normal: it exists for saves from old
  versions, and it cannot be claimed while it is empty.
- `.help` lists the commands, and `.help <command>` explains one. The full list is under
  [GM commands](REFERENCE.md#gm-commands).

Other ways to make accounts:

| Way | Controlled by |
|---|---|
| The client's "Create account" dialog | `Accounts:Registration` (on by default) |
| Automatically, at the first login with a new name | `Accounts:AutoRegister` (off by default) |
| The web panel's `/register` page | `Web:Enabled`, `Web:Registration` and `Accounts:Registration` |

Five wrong passwords in a row lock an account for 15 minutes (`Accounts:MaxFailedLogins`, `Accounts:LockMinutes`).
`.account unban <name>` or `.account password <name> <new>` ends the lock early. Details are under
[Accounts](REFERENCE.md#accounts).

## 10. Where every setting lives

### How settings are loaded

1. The `Preset` supplies a default for every key you leave out.
2. The five `.jsonc` files override the preset.
3. Command-line arguments (`--Section:Key=value`) override the files.
4. Temporary runtime commands such as `.rates fame 2` override everything until the next restart.

Further rules:

- **All five files are required**, even if they are nearly empty. Keys inside them are optional.
- The files are JSONC: comments and trailing commas are allowed. Key names ignore case.
- Each key belongs to exactly one file. An unknown, duplicate or misplaced key, or an invalid value, stops the start
  with a message that names it.
- Relative paths are resolved against the **parent of the configuration folder**, not against the file. With
  `config/` or `config.private/` in the repository, that parent is the repository root. The exception is
  `Plugins:Path`, which is relative to the current directory.
- The files are read once, at the start. **Restart to apply an edit.** There is no reload command or file watcher.
- Many keys in the distributed files are commented-out examples that show the official value, with the `pve` value in
  the note. Uncomment and edit one to change it.

### The five files

| File | What it controls |
|---|---|
| `common.jsonc` | `Preset`, `Network:BindAddress`, `Network:PublicAddress`, `Paths:ClientGameData`, `Paths:Content`, `Log:*`, `Web:*`, `Backup:*` |
| `login.jsonc` | `Network:LoginPort`, `Accounts:*` (registration, default level, lockouts, maintenance) |
| `world.jsonc` | `Network:GamePort`, `Tutorial:*`, `World:*`, `Rates:*` and all gameplay rules. This is also the owner of any key not listed for another file |
| `chat.jsonc` | `Network:ChatPort`, `Chat:*` |
| `persistence.jsonc` | `Persistence:Provider` (only `SQLite`), `Paths:Saves` |

### Common tasks

| To change | File | Key |
|---|---|---|
| The ruleset | `common.jsonc` | `Preset`: `official` or `pve` |
| The GameData location | `common.jsonc` | `Paths:ClientGameData` |
| The content folder | `common.jsonc` | `Paths:Content` |
| The listening address | `common.jsonc` | `Network:BindAddress`, `Network:PublicAddress` |
| The ports | `login.jsonc`, `world.jsonc`, `chat.jsonc` | `Network:LoginPort`, `Network:GamePort`, `Network:ChatPort` |
| Log detail and log file | `common.jsonc` | `Log:Level` (`Trace`, `Debug`, `Info`, `Warn`, `Error`), `Log:File` (empty = console only) |
| The web panel | `common.jsonc` | `Web:Enabled`, `Web:BindAddress`, `Web:Port`, `Web:Registration`, `Web:SessionMinutes` |
| Automatic backups | `common.jsonc` | `Backup:EveryHours`, `Backup:Keep` |
| Registration and account levels | `login.jsonc` | `Accounts:Registration`, `Accounts:AutoRegister`, `Accounts:DefaultLevel` |
| Maintenance mode | `login.jsonc` | `Accounts:Maintenance` |
| The chat channels and limits | `chat.jsonc` | `Chat:*` |
| The database and save folder | `persistence.jsonc` | `Paths:Saves` |
| Fame, loot, silver and respawn rates | `world.jsonc` | `Rates:*` |
| The opening tutorial | `world.jsonc` | `Tutorial:Opening` |
| What new characters start with | `world.jsonc` | `Progression:NewCharacters` |
| Player bots and companions | `world.jsonc` | `Bots:*`, `Companions:*` |
| C# plugins | `world.jsonc` | `Plugins:Enabled`, `Plugins:List`, `Plugins:Path`, `Plugins:Build`, `Plugins:MaxErrors`, `Plugins:SlowMs` |
| Content packs | `world.jsonc` | `Mods:Path`, `Mods:Packs` |
| PvP rules | `world.jsonc` | `Pvp:*` |

### Main sections of `world.jsonc`

| Area | Sections |
|---|---|
| World | `World`, `Visibility`, `Transfer`, `Rates`, `Tutorial`, `Progression` |
| Character | `Combat`, `Consumables`, `Crafting`, `Focus`, `LearningPoints`, `Premium`, `Vanity` |
| Social | `Party`, `Guilds`, `Alliances`, `Social`, `Mail`, `Trade` |
| Economy and loot | `Economy`, `Bank`, `Bm`, `Chests`, `Loot` |
| PvE content | `Events`, `Bounties`, `Season`, `Dungeons`, `Expeditions`, `WorldBosses`, `Corrupted`, `Hellgates`, `Roads`, `Mists`, `Pve` |
| PvP content | `Pvp`, `PvpGoals`, `DeathRecap`, `Arena`, `Territories`, `Castles`, `Hideouts` |
| Bots | `Bots`, `Companions` |
| Housing | `Islands` |
| Modding | `Mods`, `Plugins` |

### Presets

`Preset` is set in `common.jsonc`:

- `official` (distributed, and the default if the key is missing) follows the official rules at rates ×1. The server's
  own additions are off. The name is a goal, not a promise of complete fidelity.
- `pve` turns on PvP by consent, no full loot, bots, companions, free premium, the web panel, plugins and more.
  The table under [PvE preset](REFERENCE.md#pve-preset) lists each key with both values.

Any key you set yourself wins over either preset, and that includes the keys that are not commented out in the
distributed files. So switching to `pve` alone leaves every `Rates` key at 1, and `Chat:WorldChannel` and
`Chat:MergeLanguageChannels` at false. Change or remove those keys if you want something else.

### Content files

`content/` holds tables the client data does not settle. Files are read at the start, and most keys are optional, with
built-in defaults. Depending on the file, a mistake is either logged or stops the start, so watch the log after an
edit. Under `Preset: official`, config keys keep many of the server's own additions off whatever these files say.

| File | Contents |
|---|---|
| `starter.json` | Start cluster, position and kit of new characters when `Tutorial:Opening` is false |
| `starter-fair.json` | The kit under `Progression:NewCharacters` = `FairStart` |
| `kits.json` | The GM's item kits for `.kit` |
| `combat.json`, `fishing.json` | Numbers the client data leaves open |
| `dungeons.json`, `corrupted.json`, `hellgates.json`, `mists.json` | Dungeon, hellgate and Mists tables |
| `bosses.json`, `events.json`, `expeditions.json` | World bosses, world events, expeditions |
| `goals.json`, `journals.json` | Challenges, bounties, guild goals, labourer journals |
| `bots.json` | Bot tables. The switches are in `world.jsonc` |
| `opcode-stubs.json` | Answers to side requests that have no handler |
| `spawns/<cluster>.json` | Buildings the server adds to a cluster, such as crafting stations |
| `extraspells.json` | Spells that worn items grant on the extra action bars |
| `checks.json` | The client checklist behind `.check` |

### Command-line options

| Option | Meaning |
|---|---|
| `--config-dir=<folder>` | The configuration folder. Default: `config` in the current directory |
| `--check-config` | Validate the settings and exit. No ports, database, log or GameData |
| `--check-content` | Check the cluster data of the whole world and exit. `-v` lists every failure |
| `--check-tutorial` | Check the tutorial's data definitions and exit. `-v` gives details |
| `-v`, `-vv` | Debug or Trace logging. Overrides `Log:Level` |
| `--protocol-trace` | Turn on protocol tracing |
| `--plugins-build [name...]` | Build the named plugins, or all of them, and exit. No ports are opened. `--force` rebuilds unchanged ones |
| `--Section:Key=value` | Override any key, for example `--Rates:Fame=2`. The startup summary lists each override |

### Files the server creates

| Path | Contents |
|---|---|
| `data/albionlocal.db` | The SQLite database: accounts, characters, guilds, market and all other saved state. **Back this up** |
| `data/server.log` | The log (`Log:File`). The log never shows passwords |
| `data/clientdata.json` | The client build and GameData hashes of the last start, used to warn about changes |
| `data/backups/` | Database backups, `albionlocal-yyyyMMdd-HHmmss.db` |
| `data/plugins-bin/` | Plugin build output |
| `data/plugins/` | Each plugin's own data: `storage.json` and the owner's `config.json` |
| `data/icon-cache/` | Icons for the web panel's mod pages |
| `data/modkit-extract/` | XML from ModKit's `extract` verb |

`Paths:Saves` moves the database, `clientdata.json` and `backups/`. `Log:File` moves the log. All of these are ignored
by git.

### Temporary rates

`.rates` shows the current rates, and an Admin can change them for the running world with, for example, `.rates fame 2`.
`.rates reset` restores the values from the files, and a restart discards the changes. For a permanent change, edit
`Rates` in `world.jsonc`. See [CONFIGURATION.md](CONFIGURATION.md) for more on rates.

## 11. Run the self-test

The self-test runs offline with an in-process client: no network and no real client, but it reads GameData. By default
it looks in `../game-local/...` ([option D](#option-d-use-the-default-location)), or you can name the folder:

```bash
dotnet run --project tests/AlbionLocal.SelfTest -- --client-gamedata="C:/Games/AlbionOnline/game/Albion-Online_Data/StreamingAssets/GameData"
```

The full suite is large. To run part of it, select tests whose names contain a text:

```bash
dotnet run --project tests/AlbionLocal.SelfTest -- --client-gamedata="C:/Games/AlbionOnline/game/Albion-Online_Data/StreamingAssets/GameData" --only=Account
```

The last line sums up the run, for example `17 passed, 0 failed, 0 skipped; 17 selected`. Log lines with `Warn` during
a run are often the tests provoking errors on purpose; what counts is that summary.

| Option | Meaning |
|---|---|
| `--client-gamedata=<folder>` | The GameData folder |
| `--only=<text>` | Run only tests whose names contain the text |
| `--report=<file.json>` | Also write a JSON report |
| `--lab=all\|serialization\|transport\|session\|messages` | The protocol lab runs |
| `--protocol-trace`, `-v` | More output |
| `--help` | List the options |

Comparisons against generated "golden" data files are skipped, because those files are not published.

## 12. Play on a LAN

The project is meant for your own PC or LAN, not the open internet. On the host:

**1. Listen on the network.** In `common.jsonc`, with your host's LAN address:

```jsonc
"Network": {
  "BindAddress": "0.0.0.0",
  "PublicAddress": "192.168.1.10"
},
```

`PublicAddress` is required with `0.0.0.0`: it is the game server address handed to every client, including one on the
host. A loopback `BindAddress` with a LAN `PublicAddress` reaches nobody, and the server warns about it at the start.

**2. Open the UDP ports for your LAN.** Without 5057 the game works, but chat channels and whispers stay empty.

On Windows, in an administrator PowerShell:

```powershell
New-NetFirewallRule -DisplayName AlbionLocal -Direction Inbound -Protocol UDP -LocalPort 5055,5056,5057 -Action Allow -Profile Private
```

On Linux with `ufw`:

```bash
sudo ufw allow 5055:5057/udp
```

**3. Decide who can register.** `Accounts:Registration` false closes the client's dialog and the web page. Use passwords
you use nowhere else.

One account can play on one client at a time; a second login closes the first one's connections. Details are under
[Several players](REFERENCE.md#several-players).

## 13. Web panel and backups

### Web panel

The panel is off under `Preset: official`. Turn it on in `common.jsonc`:

```jsonc
"Web": { "Enabled": true, "BindAddress": "127.0.0.1", "Port": 8080 },
```

Restart, then open <http://127.0.0.1:8080/> for the status page. For the admin pages (players, accounts, bans, log,
backups, mods), create an `Admin` account as in [section 9](#9-create-accounts) and log in at
<http://127.0.0.1:8080/login>. The panel is plain HTTP, so keep `Web:BindAddress` at `127.0.0.1` unless you accept that
on your LAN. See [Web panel](REFERENCE.md#web-panel).

### Backups

| Method | How |
|---|---|
| Automatic | `"Backup": { "EveryHours": 6, "Keep": 10 }` in `common.jsonc`. Works without the panel. `EveryHours` 0 is off (the default) |
| By hand, server running | **Back up now** on the panel's Backups page. This takes a consistent snapshot |
| By hand, server stopped | Copy `data/albionlocal.db` |

To restore, stop the server and copy a backup over `data/albionlocal.db`.

## 14. Update the server

1. Stop the server with Ctrl+C.
2. Copy `data/albionlocal.db` somewhere safe.
3. Read [CHANGELOG.md](CHANGELOG.md) for what changed.
4. Get the new code and build it:

   ```bash
   git pull
   dotnet build
   ```

5. Run `--check-config` ([section 7](#7-check-the-configuration)). New keys are optional and take preset defaults, but a
   key that was removed or renamed stops the start with a message.
6. Start the server. The database is upgraded automatically, and the log shows a `schema vN` line for each step.

**You cannot downgrade without a backup.** Once upgraded, a database is refused by older builds, so going back means
restoring the copy from step 2.

**Keep the GameData at the supported build.** The server code follows one client build
([section 6](#6-point-the-server-at-gamedata)). Each start compares the build and the file hashes with the previous
start and warns when they differ.

## 15. Modding (this branch)

Nothing in this section is needed to run the server. The full guide, in Russian, is
[docs/MODDING.ru.md](MODDING.ru.md).

| Tool | What it does | How to turn it on | Details |
|---|---|---|---|
| C# plugins | Server-side code on a stable API: gameplay hooks, scripted spells, hot reload | `Plugins:Enabled` | [plugins/README.md](../plugins/README.md) |
| No-code rules | "When, If, Do, Whom" rules, applied live | The pack is in `Mods:Packs`; edit in the Studio | [plugins/RULES.md](../plugins/RULES.md) |
| Studio | Visual editor at `/studio` for items, abilities, recipes, drops, goals and rules | `Web:Enabled` and an `Admin` account | [MODDING.ru.md](MODDING.ru.md) |
| Mods editor | The older editor at `/admin/mods`, with an item and ability catalogue | `Web:Enabled` and an `Admin` account | [MODDING.ru.md](MODDING.ru.md) |
| ModKit | Content packs: your own items and abilities made from the official assets | Run by hand | [ModKit README](../tools/AlbionLocal.ModKit/README.md) |
| Extra action bars | Two bars of abilities granted by worn items (`content/extraspells.json`) | Needs the `ActionBarsMod` client mod | [README](../README.md#modifications-branch) |
| Client checklist | `.check` sets up each check of `content/checks.json` in game and records the result | GM command | [README](../README.md#modifications-branch) |

### Enable a plugin

Plugins are off under `Preset: official`. To try the `welcome` sample, add this to `world.jsonc`:

```jsonc
"Plugins": { "Enabled": true, "List": ["welcome"] },
```

Restart the server. It builds the plugin with the local SDK into `data/plugins-bin/` and logs:

```text
Info  [plugins] 3 plugin(s) found, 1 to load: welcome
Info  [plugins] loaded welcome 1.0.0 by AlbionLocal: PlayerLogin; .motd; 0 timer(s)
```

`.plugins` lists the plugins, and `.plugins reload welcome` rebuilds and reloads one without a restart. To build without
starting the server, name the plugins, or none to build them all. `--force` rebuilds plugins that did not change.

```bash
dotnet run --project src/AlbionLocal.Server --no-launch-profile -- --plugins-build welcome
```

- A plugin is ordinary C# that runs inside the server process, so only run code you trust.
- Building from source needs the .NET SDK on that machine. With `Plugins:Build` false, only prebuilt DLLs are loaded.
- `Plugins:Path` is relative to the current directory, unlike the other paths ([section 10](#how-settings-are-loaded)).

### Studio and the Mods editor

1. Turn on the web panel and create an `Admin` account ([section 13](#13-web-panel-and-backups)).
2. Open <http://127.0.0.1:8080/studio> or <http://127.0.0.1:8080/admin/mods>. Without a session you are sent to the
   login page.
3. After an install or a restore from these pages, restart the server yourself. The panel does not do it.

Item and ability icons need one run of ModKit's `icons` verb. The pages work without them.

### Content packs (ModKit)

ModKit rebuilds GameData files with your packs into a **separate local client copy**. Point `--game-local` only at such
a copy, never at your original install, and read the [ModKit README](../tools/AlbionLocal.ModKit/README.md) before
your first `build`.

```bash
dotnet run --project tools/AlbionLocal.ModKit -- <verb> [args] [options]
```

| Verb | What it does |
|---|---|
| `check <packs...>` | Builds the packs in memory and reports problems. Writes nothing |
| `build <packs...>` | Applies the packs to the official files and installs the result into the copy |
| `restore` | Puts the official files back into the copy |
| `status` | Shows what is modded, by which packs, and whether the files are still as built |
| `extract [files...]` | Decrypts GameData files to readable XML in `data/modkit-extract/` |
| `icons` | Once: builds the icon cache for the web panel's pages |

| Option | Default |
|---|---|
| `--game-local <GameData folder>` | `../game-local/Albion-Online_Data/StreamingAssets/GameData` |
| `--official <GameData folder>` | The same path under `../game` |
| `--mods <folder>` | `mods` |
| `--server <folder>` | Found automatically |

The server must read the same copy, so `Paths:ClientGameData` has to point at it. To roll a pack's drops too, list it
in `world.jsonc` and restart:

```jsonc
"Mods": { "Packs": ["example-blade"] },
```

### Client mods

`client-mods/` holds optional BepInEx plugins for the client, with their install scripts in `tools/`. The server does
not need them, except for the extra action bars. They are for use with this local server only, never with official
servers, and installing them is your own decision and responsibility. Read
[client-mods/README.md](../client-mods/README.md) first. This guide does not cover them.

## 16. What this guide does not cover

- **Connecting a game client.** How, and whether, you point a client at a local server is your own decision and
  responsibility. Check the terms of service that apply to you. The authors provide no support for it.
- **Anti-cheat.** Nothing here bypasses or interferes with any anti-cheat or protection system, and such contributions
  are not accepted.
- **Official servers.** The code only answers a client as a server, and must not be used against official servers.

## 17. Troubleshooting

### Build

| Symptom | Cause and fix |
|---|---|
| `dotnet` is not recognized | The SDK is not installed or the terminal is older than the install. Open a new terminal |
| `NETSDK1045`: the SDK does not support targeting .NET 10.0 | Install the .NET 10 SDK and check `dotnet --list-sdks` |
| `NU1301`: unable to load the service index | No access to nuget.org. Check your connection or proxy |
| A build error says a file is locked by `AlbionLocal.Server` | The server is running from that output folder. Stop it, or build into `bin/run` ([section 5](#5-compile)) |

### Configuration

| Message | Cause and fix |
|---|---|
| `Required configuration file missing: ...common.jsonc` | Wrong directory or incomplete folder. Run from the repository root or pass `--config-dir`, and keep all five files |
| `Use --config-dir=<directory>; there is no main configuration file` | `--config` no longer exists. Use `--config-dir` |
| `unknown config key(s): ...` | A typo, or a key this version does not have |
| `...: Accounts:Registration belongs in login.jsonc` | The key is in the wrong file. Move it to the file named |
| `'G' is an invalid escapable character within a JSON string` | Single backslashes in a path. Use forward slashes |
| `Rates:Fame: 'abc' is not a number` | An invalid value. The message names the key |
| `Network:BindAddress 0.0.0.0 needs Network:PublicAddress ...` | Set `Network:PublicAddress` ([section 12](#12-play-on-a-lan)) |
| `Network:LoginPort and Network:GamePort are both 5056 ...` | Give each listener its own port |

### Start

| Message | Cause and fix |
|---|---|
| `Paths:ClientGameData: ... not found` | The GameData path is wrong. Compare it with the `GameData:` line of the startup summary |
| `SocketException (10048): Only one usage of each socket address` (Windows) or `Address already in use` | The port is taken, usually by another running server. Check with `netstat -ano -p udp` on Windows, or change the ports |
| `schema version N is newer than this server's (M)` | This database was upgraded by a newer build or another branch. Update the code, restore a backup or use another `Paths:Saves` |
| `client build unknown: no version.txt in or above ...` | The GameData folder was moved away from its client folder. Check the build yourself ([section 6](#6-point-the-server-at-gamedata)) |
| `the client's GameData differs from the last start ...` | The client files changed, probably through an update. Other builds are not supported |
| `... is loopback but Network:PublicAddress ... is not` | Use `BindAddress` `0.0.0.0` ([section 12](#12-play-on-a-lan)) |

### Running

| Symptom | Cause and fix |
|---|---|
| A login is refused as incorrect | Unknown names are refused like wrong passwords. Create the account ([section 9](#9-create-accounts)) |
| <http://127.0.0.1:8080/> does not open | The panel is off under `Preset: official`. Set `Web:Enabled` ([section 13](#13-web-panel-and-backups)) |
| Console commands do nothing | A server started in the background has no console input. With several players in game, put the character name first: `Name .gps` |
| An edit to a config or content file has no effect | Files are read at the start. Restart the server, and check that you edited the folder named in `Configuration directory:` |
| `[world] tick took N ms` warnings | A few are normal. Constant warnings mean the machine cannot keep up with the world loop |
| `Plugins:Enabled is false: nothing loads at the start` | Plugins are off under `Preset: official`. Set `Plugins:Enabled` ([section 15](#15-modding-this-branch)) |
| `FAIL  <name>: ...` from `--plugins-build` | The plugin did not compile, or there is no .NET SDK on this machine. The message gives the reason |
| `/studio` opens the login page | Log in with an `Admin` account ([section 9](#9-create-accounts)) |
| ModKit says `no official GameData next to ...` | Pass `--official` and `--game-local` ([section 15](#content-packs-modkit)) |

For more detail, turn up the log with `-v`, or see [CONFIGURATION.md](CONFIGURATION.md) and
[REFERENCE.md](REFERENCE.md). Issues about protocol details or server architecture are welcome. Requests involving the
official game, its servers, its client or anti-cheat are not.
