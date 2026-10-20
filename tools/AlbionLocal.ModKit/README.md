# AlbionLocal.ModKit: content packs

Add your own items and abilities to the local server and its client (game-local), built from the
assets the client already has: new item and spell definitions, crafting recipes, names and
descriptions, and where things drop. New 3D models, icons or effects are not possible: a pack
reuses the official ones.

Run everything from the `server/` folder:

```
dotnet run --project tools/AlbionLocal.ModKit -- <verb> [args] [options]
```

| Verb | What it does |
|------|--------------|
| `extract [files...]` | Decrypts GameData files to readable XML in `data/modkit-extract/` (default: items, spells, localization, buildings; `*` for all; `--out <dir>`; `--local` reads game-local instead of the official install). For reference only. |
| `check <packs...>` | Builds the packs in memory and reports errors, warnings and the new ids. Writes nothing. |
| `build <packs...>` | Applies the packs (in that order) to the **pristine official files** and installs the result into game-local. List every pack you want at once: each build starts again from the official files and replaces the previous one. `--force` after an official client update (see below). |
| `restore` | Puts the official files back into game-local (hard links to the official install again, so disk use is back to normal). |
| `status` | What is modded, by which packs, file hashes, and whether the files are still as built. |
| `icons` | Once: the item and spell icons for the web panel's Mods pages. Reads the NGUI atlases `ItemAtlas`, `ItemAtlas_01`, `ItemAtlas_02` and `SpellAtlas` out of the official `Albion-Online_Data/resources.assets` (+ `.resS`), **read only**, and writes one PNG per sprite plus `index.json` into `data/icon-cache/` (git-ignored: derived from the client, never commit or publish it). About 20 s, ~2900 icons, ~70 MB. `--assets <resources.assets>`, `--out <dir>` (refused inside the game folders), `--atlas <name,name>`. Run it again after a client update. |

Options: `--server <dir>` (the server folder, found automatically), `--game-local <GameData dir>`
(default `../game-local/Albion-Online_Data/StreamingAssets/GameData`), `--official <GameData dir>`
(default: the same path under `../game`), `--mods <dir>` (default `mods`).

Example: `dotnet run --project tools/AlbionLocal.ModKit -- build example-blade`, then start the
server with `--Mods:Packs=example-blade` (or `"Mods": { "Packs": ["example-blade"] }` in
`config/albionlocal.json`) for the pack's drops. Undo with `restore`.

## Safety

Every file in game-local's GameData is an NTFS hard link to the official install (`Reverse/game`):
writing into it would change the official game too. The modkit therefore:

- never opens a game-local file for writing: it writes a new temp file next to it, deletes the
  game-local name (which only removes that link) and moves the temp file in; afterwards it checks
  that the file has exactly one link and the built content, and that the official file still has
  the hash it had before (otherwise it stops);
- refuses any write whose path (junctions resolved) lies in the official install;
- reads the originals only from the official install, so a rebuild always starts from pristine data;
- records the official file hashes and the client build at the first build
  (`StreamingAssets/modkit-state.json` in game-local, a new file). If the official client changed
  since (an update), `build` refuses until you checked your packs and pass `--force`.

The server checks the modded data at every start: every official item and spell must still have
its id, and every new item and spell the id the modkit gave it; otherwise it does not start
(rebuild or restore).

## Pack format

A pack is a folder `mods/<pack>/`:

```
mods/example-blade/
  pack.json          name, version, the client build it was written for
  items.xml          patch of items.bin
  spells.xml         patch of spells.bin
  buildings.xml      patch of buildings.bin (crafting stations)
  localization.xml   names and descriptions
  server/drops.json  server-only: where the items drop (optional)
```

`pack.json`:

```json
{ "Name": "example-blade", "Version": "1.0.0", "Client": "albiononline-win32-full-1.32.030.345319", "Description": "..." }
```

Any GameData file can be patched: the patch file is named after it (`mobs.xml` → `mobs.bin`,
`loot.xml` → `loot.bin`, ...). A patch is `<patch>` with operations; elements are found by their
`uniquename` (in localization by `tuid`):

| Operation | Meaning |
|-----------|---------|
| `<add> <weapon uniquename="NEW" .../> </add>` | Appends new top-level elements (the name must be new). |
| `<add uniquename="T6_FORGE" path="craftingitemlist"> <craftitem .../> </add>` | Appends children inside an existing element (`path`: child steps, `name[attr=value]` filters). |
| `<replace uniquename="X"> <weapon uniquename="X" .../> </replace>` | Replaces a whole element (same name). With `path`: replaces that child. |
| `<set uniquename="X" attr="itempower" value="950"/>` | Sets one attribute; `path="enchantments/enchantment[enchantmentlevel=1]"` for a child; `unset="true"` removes it. |
| `<remove uniquename="X" path="craftingitemlist/craftitem[uniquename=Y]"/>` | Removes a child. Removing a whole top-level element is refused (see ids). |
| `<entry tuid="@ITEMS_X"> <text lang="EN-US">..</text> <text lang="RU-RU">..</text> </entry>` | localization.xml only: adds the text or sets those languages. |

Names: an item uses `@ITEMS_<uniquename>` and `@ITEMS_<uniquename>_DESC`; a spell its
`namelocatag` / `descriptionlocatag` (default `@SPELLS_<uniquename>`, `..._DESC`). Write at least
EN-US and RU-RU (the check warns otherwise).

### Ids (why only appending)

The client numbers items, spells, buildings, mobs, ... by their order in the file, and those ids go
over the wire. New elements are therefore always appended, official ones keep their place and
their enchantments and channeling parts, and removing a top-level element is refused. The check
compares every official id with the pristine file and lists the new ones (the first new item is
11972, the first new spell 9326 in build 345319). The journal `_EMPTY`/`_FULL` variants the client
creates after the whole file move up by the number of new item ids, identically on client and
server; saves store item names, not ids, so nothing breaks.

### What the check validates

- references: crafting resources, spell lists (`craftspell`, `craftingspelllist reference`),
  `applyspell`/`effect`/`endeffect`, crafting station items, ... must name existing (or added)
  items or spells. Which attributes are references is learned from the official data.
- assets: every model/icon/effect/sound attribute (`prefab`, `uisprite`, `mesh`, `AudioInfo name`,
  `castanimation`, ...) must use a value the official data uses. A new item needs
  `uisprite="..."` (icon; the client's default is the name without its `Tn_`, which a new item
  does not have) and, for equipment, `mesh="..."` (model `CharacterBuilder/EQUIPMENT/.../<mesh>_01`,
  checked against `CustomMeshes/meshes.toc`). Example: `mesh="T6_MAIN_SWORD" uisprite="MAIN_SWORD"`.
- ids: as above. Localization: missing EN-US/RU-RU names are warned about.

### Server-only extras: `server/drops.json`

Read by the server at start for the packs in `Mods:Packs`; nothing in game-local changes.

```jsonc
{ "Drops": [
  { "Item": "T6_MAIN_CLOCKWORKBLADE", "Mobs": ["WORLD_BOSS_*"], "Chance": 0.03, "Amount": 1 },
  { "Item": "T6_MAIN_CLOCKWORKBLADE", "Chests": ["*BOSS*"], "Chance": 0.01 },
  { "Item": "T6_MAIN_CLOCKWORKBLADE", "Category": "miniboss", "Chance": 0.005 }
] }
```

`Mobs`/`Chests`: mob or lootchest names with `*` wildcards; `Category`: the mob's mobs.xml category
(boss, miniboss, ...; with `Mobs` both must match). The chance is per kill (or per chest's first
roll) × `Rates:Loot`; the item goes into the normal loot bag or chest.

## The example pack: mods/example-blade

- `T6_MAIN_CLOCKWORKBLADE` "Master's Clockwork Blade" / «Заводной клинок (мастер)»: a T6 one-handed
  sword with the broadsword's model and icon, craftable at the Warrior's Forge (T6-T8) from 16 metal
  bars, 8 leather and 10 runes (T6).
- Clockwork Dash (`CLOCKWORK_DASH`, second slot): the hamstring sword's dash; on arrival 95 physical
  damage around and, for 4 s, the frost path's ground that slows enemies inside by 35 %.
- 3 % drop from world bosses (`server/drops.json`).

It is not installed. To try it: `build example-blade`, start the server with
`--Mods:Packs=example-blade`, start the client from game-local. To undo: `restore`.

## Limits and risks

- Only official assets: no new models, icons, animations, effects or sounds.
- The client reads everything it knows; unknown attributes are ignored, a wrong value can make it
  fail to load the data. Check first, keep packs small, restore if the client misbehaves.
- After an official client update: the official files change, `build` refuses; check the packs
  against the new data (`extract`), then `build --force`. game-local's modded files do not get the
  update until you rebuild or restore.
- Never use the modded client against the official servers: it is only for this local server.
