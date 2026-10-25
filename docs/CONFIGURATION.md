# Server configuration

The server reads five required JSONC (JSON with comments) files from `config` in the current working directory.
These `.jsonc` files support comments and trailing commas; they are not standard JSON.
There is no main configuration file or manifest. `albionlocal.json` is no longer read; move any private
settings to their owning files manually before starting. Keep all five files when selecting another directory.

| File | Options |
|---|---|
| `common.jsonc` | `Preset`, network bind/public addresses, GameData/content paths, logging, web panel and backups |
| `login.jsonc` | Login port, accounts, registration and maintenance |
| `world.jsonc` | Game port, tutorial, character progression, rates and other gameplay rules |
| `chat.jsonc` | Chat port and communication settings |
| `persistence.jsonc` | `Persistence:Provider` and `Paths:Saves` |

Individual options may be omitted to use preset defaults. The distributed files select `official`,
rates ×1 and the tutorial for new characters. The preset name does not establish complete fidelity
to the original server. Existing characters keep their progress. SQLite is the only supported provider;
the database remains `albionlocal.db` inside `Paths:Saves`.

## Select and check settings

```powershell
dotnet run --project src/AlbionLocal.Server --no-launch-profile -- --config-dir ./config --check-config
dotnet run --project src/AlbionLocal.Server --no-launch-profile -- --config-dir=./config --Rates:Fame=2
```

Relative operational paths are resolved against the parent of the selected configuration directory.
The startup summary prints absolute paths and explicit argument overrides. Precedence is preset,
file settings, explicit arguments, then temporary runtime commands. Unknown, duplicate or misplaced
options, invalid values and conflicting ports stop startup before services or saves are opened.
`--check-config` validates without opening listeners, databases or log files, and does not load GameData.

## Visual Studio: F5

Copy `src/AlbionLocal.Server/Properties/launchSettings.example.json` to `launchSettings.json`
beside it. The actual launch settings are ignored so machine-specific choices stay private.
Open the solution and select AlbionLocal.Server as the startup project. The **Check configuration**
profile validates without starting services; **Albion Local Server** starts the emulator.
Both example profiles use the repository root and `--config-dir=config`.

Visual Studio expands `MSBuildProjectDirectory` in the example working directory. For command-line
use, start in the repository root and use `--no-launch-profile` as shown above.

## Rates and future administration

Edit `Rates` in `world.jsonc` for permanent changes. Combat fame uses `Fame × CombatFame`, gathering uses
`Fame × GatheringFame`, and crafting uses `Fame × CraftingFame`; existing game bonuses and rounding remain.
The general fame multiplier also applies to existing sources such as tomes.

`.rates` displays the active rates. An Admin can use `.rates fame 2` to change the entire running world;
logging out does not reset it. `.rates reset` restores effective startup values. Restarting discards
temporary changes. The `save` argument is rejected without changing values. Old `rates.json` files
are preserved but ignored, with a warning.

Files are read at startup in this version. Rates are classified for future reload support; other options
require restart. There is no reload command or file watcher. The existing web panel remains available through
`Web:*`; its configuration and `Backup:*` belong in common.jsonc. Editing or reloading these five
files through that panel is not implemented. The model and validator are extension points for that work. Already awarded rewards
and scheduled timers are not recalculated by a rate change.

## Private configuration and first login

Copy all five files from `config` to the ignored `config.private` directory. The two directories
share the same parent, so relative operational paths retain their meaning. Set the private GameData
path and verify the existing save path before starting; no database needs to be copied or replaced.

```powershell
dotnet run --project src/AlbionLocal.Server --no-launch-profile -- --config-dir=config.private --check-config
dotnet run --project src/AlbionLocal.Server --no-launch-profile -- --config-dir=config.private
```

For F5, select `--config-dir=config.private` in the ignored `launchSettings.json`.
There is no additional main configuration file. Never commit the private directory or actual launch settings.

New save databases start without characters, with or without the tutorial. Unknown logins are
refused when `Accounts:AutoRegister` is false. Register through the client when Registration is
enabled or use `.account create` on the server console. Automatic registration creates an account
at DefaultLevel. Legacy account claiming only applies when that unnamed account owns characters.
