# Contributing to AvalonEmu

Thanks for helping. Bug reports, fixes, protocol findings and new server features are all welcome. Please read this
page and the [disclaimer](README.md#disclaimer--please-read) first: a contribution that falls outside them is closed
however good the code is.

Issues, pull request descriptions and discussions can be in English, Russian or Spanish. Code, code comments, commit
messages and pull request titles are in English.

## What fits

- Server behaviour that matches the official game: protocol handling, world rules, content, persistence.
- Fixes, with a short description of what was wrong and how you found it.
- Extra or non-official features, as long as they sit behind a configuration switch that is off under
  `Preset: official`, or in a server module (branch `modules`, see `docs/modules/README.md` there).
- Documentation, configuration comments and self-tests.

## What does not fit

These are closed without review:

- The game client, client files, game data or assets, or links to them.
- Client patches, tools that modify the client, or anything that bypasses, disables or works around anti-cheat.
- Anything that connects to, automates or interferes with the official servers or their players.
- Web shops, real-money payments or any other monetization. In-game features of the official game (the gold market,
  premium bought with gold or silver) are fine.

## Reporting a bug

Use the bug form when you open an issue. The most useful reports name the AvalonEmu commit, the client build, the
preset, the steps, and a few lines of `data/server.log` around the problem (start the server with `-v` for more
detail). Remove account and character names, and other players' data, before you post a log or a capture.

If you found a security problem, for example in the login or the web panel, describe the impact in an issue but leave
out a working exploit.

## Before you start a pull request

- **One topic per pull request.** A fix, a feature or a refactor, not several at once. Large features: open an issue
  first so the design can be agreed before you write most of the code.
- **Keep the diff to the change.** No unrelated reformatting, renames or moved files. Shared files (`README.md`,
  `config/*.jsonc`, `ServerConfig.cs`, the database migration list) only as far as the change needs, so that parallel
  pull requests do not collide.
- **Official defaults.** With `Preset: official` the server should behave as the official game does. When behaviour is
  guessed rather than known from the client code or a capture, say so in a comment (the code marks it `[infer: …]`)
  and in the pull request.
- **Database.** A schema change is a new step at the end of `Migrations` in
  `src/AlbionLocal.Server/Persistence/SqliteCharacterRepository.cs`. Released steps are never edited.
- **Generated protocol classes.** Files under `src/AlbionLocal.Protocol/Generated` are regenerated from the client
  build. Do not edit them by hand; extend a message in a separate partial class, or describe the wrong or missing field
  in the pull request.
- **Match the surrounding code**: its naming, comment style and structure.

## Build and test

You need the .NET 10 SDK and the GameData folder of a client installation you own; [docs/INSTALL.md](docs/INSTALL.md)
has the full setup.

```bash
dotnet build
dotnet run --project tests/AlbionLocal.SelfTest -- --client-gamedata="<client>/Albion-Online_Data/StreamingAssets/GameData"
```

`--only=<text>` runs only the tests whose names contain the text. Before you open a pull request:

- the whole self-test passes (comparisons against unpublished golden files are skipped, which is expected);
- the new behaviour has a short, focused test, not an exhaustive suite;
- if you tried it in the game client, say what you did; if you did not, say that too.

## Pull requests

Fill in the template. Write the commit messages as a short summary line followed by what changed and why. Link the
issue the pull request fixes (`Closes #123`).

By contributing you agree that your contribution is licensed under the [MIT license](LICENSE) of this repository.
