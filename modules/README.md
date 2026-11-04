# Server modules

Each folder here with a `module.jsonc` is a server module; the server builds and loads it at the start. This folder is ignored by git (except `examples/`), so a module
can live in its own repository: clone it here. `docs/modules/README.md` explains everything.

```bash
AlbionLocal.Server --new-module mod-my-feature
```

The modules in `examples/` are switched off (`"enabled": false`): copy one here, or turn it on with `.modules enable mod-skeleton` in the game.
