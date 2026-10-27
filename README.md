# AvalonEmu
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
  client file, any modified client, any client patch or any tool that modifies the client. It contains no game assets
  and no game data files. At startup the server reads the GameData files of a client installation that
  *you* already have; nothing is copied into this repository.
- **No anti-cheat circumvention.** Nothing here bypasses, disables or interferes with any anti-cheat or protection
  system, and such contributions will not be accepted.
- **Never use it against official servers.** The code only *answers* a client as a server. It does not connect to,
  automate or interfere with official servers or other players.
- **Your responsibility.** How (and whether) you point a client at a local server is entirely your own decision and
  responsibility; check the terms of service that apply to you. The authors provide no support for that.

## What works

The server implements a large part of the game as a local, self-contained emulation. Like TrinityCore or AzerothCore,
it behaves like the official game by default (`Preset: official`); everything it adds on top is behind a config key, and
the `pve` preset turns the PvE-friendly additions on. What changed lately: [docs/CHANGELOG.md](docs/CHANGELOG.md).

- **Core:** login and game servers over Photon UDP (reliable channels, fragmentation, DH/AES encryption), accounts
  with PBKDF2 passwords, several characters per account, GM levels and a chat command system, SQLite persistence with
  schema migrations.
- **World:** cluster loading from the client's GameData, visibility, cluster travel, paid fast travel by cargo, per-city
  banks and markets, islands with farming and animals, stables and mounts, crafting and refining stations in every city
  with city bonuses, enchanting, quality rolls.
- **Combat:** auto-attacks, spells with conditions and effects, buffs and crowd control, mob AI with path finding and
  line of sight, boss mechanics (spawn/death casts, dashes, knockbacks), knock-down and party revive, PvP flags and
  duels; zone-based PvP with full loot under `official`, PvP only by consent under `pve`.
- **PvE content:** open-world camps and chests, elite mobs, world bosses, journals, expeditions,
  random dungeons with the client's own layout generator and shrines, static dungeons, corrupted dungeons, hellgates,
  Roads of Avalon, the Mists, the Adventurer's Challenge; under `pve` also on-demand invasions, bounties and PvP goals.
- **Social:** chat server (whispers, guild, party, world), friends, mail, trading, guilds with ranks, contracts and goals,
  emotes, loadouts, map pings.
- **Bots (optional, off under `official`):** player bots (city walkers, gatherers, patrols, hostile bots in red and
  black zones) with a switch per kind, a stable roster of names, population by hour of day, whispers and local chat,
  and bots that accept your party invite and play along; companions you hire into the party (tank, healer, melee,
  ranged).
- **More systems:** arena queues (1v1/2v2/5v5; bots and Elo ladders under `pve`), hunts (tracking toolkits, wild tracks, hunt
  quest chains), island construction, labourers and furniture, awakened weapons and item traits, the PvE side of
  faction warfare, gathering tomes and fishing bait, expedition bonus chests, market price history, cosmetics from
  content, a richer starter kit.
- **Opening tutorial (`Tutorial:Opening`, on by default):** new characters start on the tutorial island at The Lighthouse
  and follow its quests through departure to a starter town, in a private instance per character (contributed in
  PR #1; not every branch is visually verified yet).
- **Web panel** (`http://127.0.0.1:8080/`): status, registration, admin pages (players, accounts, bans, log, backups).

Modding (C# plugins, no-code rules, the Studio editor, ModKit content packs, extra action bars) and optional client
mods are kept apart from this classic server, in the
[`modifications`](https://github.com/MrGreenCheezz/albion-local-server/tree/modifications) branch.

The detailed, per-feature reference (English) is in [docs/REFERENCE.md](docs/REFERENCE.md); the architecture notes
(Russian) are in [docs/ARCHITECTURE.ru.md](docs/ARCHITECTURE.ru.md). Both mention a private analysis workspace
(`../analysis`, `../captures`) that is not part of this repository.

## Repository layout

| Path | Contents |
|---|---|
| `src/AlbionLocal.Photon` | Photon UDP transport, Protocol18 serializer, encryption, message layer |
| `src/AlbionLocal.Protocol` | Message classes (generated) and wire rules |
| `src/AlbionLocal.Server` | Login (5055), game (5056) and chat (5057) servers, world simulation, systems, persistence |
| `tests/AlbionLocal.SelfTest` | Offline self-test with an in-process client (no network, no real client needed to run it, but it reads the GameData files) |
| `tools/AlbionLocal.CapDecode` | Decoder for packet captures of your own sessions |
| `tools/start-server.ps1` | Builds the server and starts it against your client's GameData (`-GameDir`) |
| `config/common.jsonc` | Server settings (every key optional, commented) |
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

Tell the server where the GameData folder is, either in `config/common.jsonc` (`Paths:ClientGameData`) or on the
command line, then start it from the repository root (it keeps its database and log in `./data`):

```bash
dotnet run --project src/AlbionLocal.Server --no-launch-profile -- -v --Paths:ClientGameData="C:/Games/AlbionOnline/game/Albion-Online_Data/StreamingAssets/GameData"
```

On start it prints the database schema version and the three listening ports. By default it listens on `127.0.0.1`
only; see the "second PC" part of [docs/REFERENCE.md](docs/REFERENCE.md) for LAN settings (`Network:BindAddress`,
`Network:PublicAddress`) and the firewall ports.

Run the self-test. By default it reads the GameData folder from `../game-local/Albion-Online_Data/StreamingAssets/GameData`
next to the repository (for example a directory junction named `game-local` to your client installation), or pass the
folder yourself:

```bash
dotnet run --project tests/AlbionLocal.SelfTest -- --client-gamedata="C:/Games/AlbionOnline/game/Albion-Online_Data/StreamingAssets/GameData"
```

`--only=<text>` runs the tests whose names contain the text, `--report=<file>` writes a JSON report, `--help` lists the
options.

Comparisons against generated "golden" data files are skipped, because those files are not published.

## Configuration

- `config/*.jsonc` — five area files (common, login, world, chat, persistence). `Preset` is `official` by default
  (the official rules, rates ×1); `pve` turns on PvP by consent, no full loot, rate multipliers and the server's PvE
  additions. Any key can be overridden on the command line, e.g. `--Log:Level=Debug`.
- `content/*.json` — gameplay content the server adds or tunes (events, bosses, dungeons, goals, bots, kits…).
- Accounts: unknown logins are refused when AutoRegister is disabled; register through the client or console (see "Accounts" in [docs/REFERENCE.md](docs/REFERENCE.md)); console
  commands such as `.account create <name> <password>` manage them.

With the opening tutorial enabled, the Lighthouse (`1008`) is private to each character.
The Cove (`1011`), forest (`1009`) and fort (`1010`) are shared: nearby players can see one another,
while quest progress and tutorial obstacles remain individual. Reconnecting preserves the authored
cluster and saved quest state. Tutorial merchant offers show one item and remain available after purchase.

Timed crafting delivers the whole batch and its fame together at the end of the progress bar.
Cancelling or logging out settles only the portion completed by elapsed crafting time and refunds
the uncompleted portion; crafting still dismounts a rider before starting.

The Destiny Board HUD automatically tracks a node from the latest fame gain, preferring
activity-specific nodes over general fame. The selection persists across map changes and reconnects.
This selection policy has automated coverage; its exact match to the official game's policy remains unverified.

## Status

Work in progress and built for learning: expect gaps, guesses (marked in the docs as inferences) and breaking changes.
Client build 345319 can show an unknown availability status for its built-in Local entries:
their status-probe address is unset. The server menu's availability label is separate from ping;
the performance HUD measures latency through the active game connection. This does not prevent local login or gameplay.
Issues about protocol details or server architecture are welcome; requests involving the official game, its servers,
its client or anti-cheat are not.

## License

MIT (see [LICENSE](LICENSE)): you may use, change and share the code freely, as long as you keep the copyright
notice, i.e. credit the original source (https://github.com/MrGreenCheezz/albion-local-server). The license covers this
repository's own code only; it grants nothing about the game, its client, its data or its trademarks.

## Configuration files

The server requires five JSONC area files: common, login, world, chat and persistence.
See [configuration](docs/CONFIGURATION.md) for migration, private paths, validation and temporary rates.
The old single configuration file and persisted rates are no longer read.
