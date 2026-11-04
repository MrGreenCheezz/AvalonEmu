# Working on the server without breaking modules

You do not have to know the module system to send a pull request. A change that knows nothing of modules compiles and merges as before. Only these things are worth knowing.

## You see a line like this in the game's code

```csharp
if (world.Modules.BeforeDamage(attacker, target, ref damage, type, origin, spell, hit)) return; // the modules' Combat.OnBeforeDamage
```

That is where modules connect. With no module loaded it costs one check. If you move or rewrite the code around such a line, keep the line in the same place in the logic. If it no longer makes sense, say so in the pull request.

The self-test `modules contract: every hook is raised` fails when a call has gone, and names it. Move the call with the code. If your change really removes the thing the hook is about, ask in the pull request and it gets sorted out there.

## New features

New official behaviour goes into the server, as always. It needs no hooks from you. If a hook is obvious and cheap, adding one is welcome, and it is one line plus a small method:

1. The args and the registration in `src/AlbionLocal.Modules.Api/Hooks.<Group>.cs`, the virtual method in `Scripts.cs`. Args hand out `ItemType`, `SpellInfo`, `MobType`, `ZoneInfo` and enums, not names or words to read: the game's code goes on passing its own, and `Modules/ModuleTypes.cs` turns them into types.
2. The hook point and its facade in `src/AlbionLocal.Server/Modules/HookPoint.cs`.
3. A method in `ModuleHost` that checks `Hooks.X.Wants(id)` first and builds the args only then, so an unused hook allocates nothing.
4. One line in the game's code, and raise `ModuleApi.Minor`.

Keep the line in the game's code to one line, and do not re-indent the code around it: an early `return` instead of wrapping it in an `if`. That keeps merges easy for everyone.

Things that are not official behaviour belong in a module, not in the server. A module needs no pull request at all: it can live in its own repository.

## The module API is a promise

`src/AlbionLocal.Modules.Api` is what modules compile against, so its members do not change or go away. The self-test `modules contract: the public API` compares it with `docs/modules/api-baseline.txt`:

- **Adding** is always fine. Refresh the file: `AlbionLocal.Server --modules-api-baseline docs/modules/api-baseline.txt`. An optional parameter added to an existing method counts as changing it: add an overload.
- **Removing or changing** fails the test. If it has to happen, it is a new major version (`ModuleApi.Major`), which is a decision to discuss first.

The classes in `src/AlbionLocal.Server` promise nothing: `coreAccess` modules use them at their own risk, so change them freely.
