<!-- Thanks for contributing! Please read CONTRIBUTING.md first. One topic per pull request.
     The description can be in English, Russian or Spanish; the title and the commit messages in English. -->

## What and why

<!-- What changes, and why. For a fix: what was wrong and how you found it. -->

## Linked issue

<!-- "Closes #123" if this fixes an issue; "None" otherwise. -->

## How it was checked

<!-- Paste the self-test summary line, e.g. "512 passed, 0 failed, 3 skipped". -->

- [ ] The whole self-test passes (`dotnet run --project tests/AlbionLocal.SelfTest -- --client-gamedata=...`)
- [ ] The new behaviour has a short test
- [ ] Tried in the game client (say what you did below), or not tried in the client (say so)

## Checklist

- [ ] One topic, no unrelated reformatting, renames or moved files
- [ ] Under `Preset: official` the server still behaves as the official game; anything else sits behind a configuration switch that is off there, or in a module
- [ ] Guessed behaviour is marked as an inference in the code and mentioned here
- [ ] Database changes are a new migration step at the end; released steps are unchanged
- [ ] No hand edits in `src/AlbionLocal.Protocol/Generated`
- [ ] Docs and configuration comments are updated where behaviour or keys change
- [ ] No client files, game data, client patches, anti-cheat workarounds, or code that talks to the official servers
- [ ] No web shop, real-money payments or other monetization
