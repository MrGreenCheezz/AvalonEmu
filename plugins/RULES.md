# No-code rules

A rule is **When → If → Do → Whom** without code: "mob X dies → 10 % chance → give item Y → to the killer",
"at night in black zones → fame × 1.5", "chat command `.home` → teleport, once per 10 minutes". Rules run on the server only
(the client is not changed), so saving one applies it to the running server at once: no Generate, install or restart.

Russian guide: `MODDING.md`, "Правила без кода". Design: `analysis/work/stageN/plugins/SPEC.md` §9. Code:
`src/AlbionLocal.Server/Rules/` (schema, parser, engine, host, templates), `Web/WebPanel.Rules.cs` (Studio API),
`web/studio/editors/rules.mjs` (editor), `Commands/Builtin/RulesCommands.cs`, tests `tests/AlbionLocal.SelfTest/RulesTest.cs`.

## How they run

- Files: `mods/<pack>/rules/<id>.json`, one rule per file (`id` = file name: 1-64 of `a-z 0-9 _ -`). The rules of the packs in
  `Mods:Packs` load (like a pack's drops and plugins).
- At the start (after the C# plugins) every file is read and checked. A rule with a **blocker** is reported in the log and left
  out; warnings are logged. The rest run in one built-in plugin, **`rules`**, loaded through the same `PluginHost` as C# plugins
  (same guard, slow-call log; storage in `data/plugins/rules/storage.json`). It subscribes to exactly the events and hooks its
  rules name (filtered by id where the rule names one exact id), so an unused trigger costs nothing.
- Hot reload: `.rules reload` (Admin), and every save/delete in the Studio. The `rules` plugin is dropped (its hooks, commands,
  timers go) and loaded again; variables, max-fire counters and cooldowns of a minute or more are kept in its storage, shorter
  cooldowns in memory across reloads.
- Commands: `.rules` (GM: summary, each rule with its trigger, state, fires, errors), `.rules reload` (Admin), `.rules problems`.
- Safety: each run is wrapped; a rule that throws `Plugins:MaxErrors` times is switched off (the rest go on, `.rules problems`
  says why). `limits.perSecond` (default 20) caps fires per second per rule; the rest of that second is skipped (logged once).
  Chains nest at most 8 deep.

## File format

```json
{
  "schema": 1,
  "id": "ghoul_drop",
  "name": "Ghouls drop ore",
  "enabled": true,
  "trigger": { "type": "mob.died", "id": "T4_MOB_ROAMING_UNDEAD_GHOUL", "params": {} },
  "match": "all",                       // or "any"
  "conditions": [ { "type": "chance", "params": { "percent": 10 } },
                  { "type": "health", "target": "killer", "not": true, "params": { "op": "<", "percent": 20 } } ],
  "actions": [ { "type": "give.item", "target": "killer", "params": { "item": "T4_ORE", "count": 2 } },
               { "type": "message", "target": "radius", "radius": 30, "params": { "text": "{player} found ore on {mob}" } } ],
  "target": "self",                     // the actions' default target
  "cooldown": { "seconds": 600, "per": "player" },       // or "global"
  "limits": { "maxFires": 0, "per": "player", "perSecond": 20 },
  "failMessage": "Not now!"            // command rules: said when the conditions fail
}
```

- `trigger.id`: the id filter (a spell, buff, item, mount or mob uniquename, a cluster id, a chain name, or text for chat/goal):
  `*` matches any text, a comma list matches any of its entries; empty = every one. Items match with and without `@n`.
- Conditions: `target` is the unit looked at (`self`, `killer`, `victim`, `caster`, `target`, `mob`; default self); `not` inverts.
- Actions: `target` is whom (any selector below; default the rule's `target`); `radius` (m) for the `radius` selector.

## Targets

| Selector | |
|---|---|
| `self` | the player of the event (the killer of a mob, the caster of a spell, the unit of a buff, the player of a command; for a timer with `eachPlayer` that player) |
| `killer`, `victim` | mob deaths, loot, player deaths/knockdowns |
| `caster`, `target` | spells, damage, buffs (caster), formulas |
| `mob` | the mob of the event |
| `party` | self and the party members online in the same cluster (+ the kill's credited players) |
| `cluster` | every player in the event's cluster |
| `radius` | the players within `radius` m of self |
| `all` | everyone online |

## Triggers

Each trigger says what it offers: a number to change (**amount**, for `modify` and the `amount` condition), a refusal
(**cancel**), skipping the game's effect (**prevent**), the rolled loot (**loot**), a command.

| Trigger | id filter | Source | Offers |
|---|---|---|---|
| `player.login` / `player.logout` | – | PlayerLogin / PlayerLogout (flag `firstLogin`) | |
| `zone.enter` / `zone.leave` | cluster | ZoneEnter / ZoneLeave (flag `clusterChange`) | |
| `player.knockdown` / `player.died` | – | PlayerKnockedDown / PlayerDied (killer = the unit that did it) | |
| `player.revive`, `combat.enter`, `combat.leave` | – | Units.OnRevive / OnEnterCombat / OnLeaveCombat (players) | |
| `health.below` (`percent`) | mob (empty = players) | Units.OnHealthChange, when the health share crosses below | |
| `chat` | text contained | ChatMessage | |
| `command` (`command`, `level`, `help`) | – | a chat command registered by the rule (`.home`); a cooldown or limit answers "wait N s" / "no more uses" | command |
| `party.joined` / `party.left`, `goal.completed` | – / goal kind | PartyJoined / PartyLeft / GoalCompleted | |
| `mob.died` | mob | MobDied (flags `boss`, `event`) | |
| `mob.spawned` | mob | MobSpawned | |
| `loot.rolled` | mob | LootRolled | loot |
| `damage` | – | the v1 Damage event: every hit, auto-attacks too (flags `overTime`, `pvp`, `toMob`, `toPlayer`) | amount |
| `item.used`, `item.crafted`, `harvest` | item | ItemUsed / ItemCrafted / Harvested (flag `fish`) | |
| `item.equip` / `item.unequip`, `item.break` | item | Items.OnEquip / OnUnequip (flags `atLogin`, `weapon`) / OnBreak | |
| `spell.check` | spell | Spells.OnCheckCast | cancel |
| `spell.cast` | spell | Spells.OnCast (mobs too: flag `mob`) | |
| `spell.hit` | spell | Spells.OnBeforeHit (flag `projectile`) | prevent |
| `spell.damage` | spell | Spells.OnDamageEarly (flags `heal`, `overTime`, `area`, `magic`, `toMob`, `toPlayer`) | amount, prevent |
| `spell.cancel` | spell | Spells.OnCancel (flag `interrupted`) | |
| `extrabar.changed` | spell (the new one, "" when emptied) | Spells.OnExtraBarChanged (flags `byGear`, `byPlugin`, `emptied`; `{spell}` = the new spell, `{item}` the granting item, text = the slot 1-12) | |
| `slot.changed` | spell (the new one) | Spells.OnSlotSpellChanged (flags `byPlayer`, `byPlugin`, `revert`, `temporary`; `{spell}` = the new spell, `{item}` the item) | |
| `buff.check` | buff | Buffs.OnCheckApply | cancel |
| `buff.applied` / `buff.removed` / `buff.tick` | buff | Buffs.OnApply (flag `new`) / OnRemove (`expired`, `removed`, `cleared`) / OnTick | |
| `mount.check` | mount | Mounts.OnCheckMount (flag `remount`) | cancel |
| `mount.mounted` / `mount.dismount` / `mount.damage` | mount | OnMounted / OnDismount (`died`, `knockdown`) / OnMountDamage | amount (damage) |
| `calc.cooldown`, `calc.casttime`, `calc.energy`, `calc.heal` | spell | Formulas.OnCalc* | amount |
| `calc.fame`, `calc.silver`, `calc.threat` | mob | Formulas.OnCalcFame / Silver / Threat | amount |
| `timer.every` (`seconds`, `eachPlayer`) | – | plugin timer | |
| `timer.daily` (`time` HH:MM server clock, `eachPlayer`) | – | checked every 20 s, once a day (stored) | |
| `chain` | chain name | the `run.chain` action of a rule of the same pack (the caller's event is passed on) | the caller's, when run at once |

## Conditions

`chance` (percent) · `account.level` (min) · `has.item` (item, count) · `equipped` (item, `*` allowed) · `has.buff` (buff) ·
`health` (op, percent) · `in.combat` · `unit.kind` (player/mob) · `silver` (op, value) · `party.size` (op, value) ·
`cluster` (clusters, comma list) · `zone.color` (safe, yellow, orange, red, black: worldsettings pvpwarningtype) ·
`time` (from, to; HH:MM server clock, over midnight works) · `weekday` (days) · `mob.tier` (op, value) · `mob.category`
(categories) · `mob.boss` · `slot.is` (slot, spell; `*` patterns and comma lists) · `extrabar.has` (spell: `*` patterns and comma lists; slot 0 = any, 1-12) · `var` (scope player/global, name, op, value) · `amount` (op, value; triggers with a number) ·
`flag` (an event detail from the tables above) · `text` (contains; chat text, goal description, command arguments).
Operators: `<`, `<=`, `=`, `>=`, `>`, `!=`.

## Actions

| Action | Params | |
|---|---|---|
| `message` | text, channel `chat`/`error`/`announce`/`zone` | placeholders `{player} {target} {killer} {mob} {spell} {item} {buff} {mount} {cluster} {amount} {args} {var:name} {gvar:name}` |
| `give.item` / `take.item` | item, count (, quality) | players only |
| `silver` | amount (negative takes) | |
| `fame` | amount, tier | PvE kill fame (destiny board) |
| `buff.apply` / `buff.remove` | buff, seconds (0 = its own), stacks | `Game.ApplyBuff` / `RemoveBuff` |
| `damage` / `heal` | amount or percent of max health (, magic) | through the engine's damage path, source = caster or self |
| `teleport` | cluster (empty = same), x, y (empty = spawn point) | |
| `move` | kind knockback/pull/dash, distance, speed | from / towards the caster or self; dash towards the event's other unit |
| `summon` | mob, count, lifetime | around each target |
| `boss.spawn` | mob | at the first target |
| `event.start` | kind, tier | a world event of content/events.json at the first target |
| `var.set` | scope, name, op set/add/sub, value | per pack; player scope per target |
| `loot.add` | item, count | `loot.rolled` |
| `modify` | op mul/add/set, value | the event's number (≥ 0) |
| `cancel` | reason | refusal triggers |
| `prevent` | – | `spell.hit`, `spell.damage` |
| `cast` | spell | self (or each target player) runs a spells.xml spell's effects at the target |
| `cooldown` | spell (empty = reset all), seconds | |
| `extrabar.set` | slot 1-12 (1-6 bar 1, 7-12 bar 2), spell (empty = the gear's again) | `IPlayer.SetExtraSlot` (README "Extra action bars"): an activespell over the gear's, in memory until cleared or a restart; a refusal goes to the log |
| `slot.set` | slot (Q W E R D F Potion Food WeaponPassive ArmorPassive …), spell, mode `fromItemList`/`any`, duration (s, 0 = for good), cooldown `keep`/`carry`/`reset` | `IPlayer.SetSlotSpell` (README "Slot spells"; `any` is experimental); a refusal goes to the log |
| `run.chain` | chain, delay | rules with trigger `chain` and that id, now (with the event) or later (a timer) |
| `log` | text | the server log |

## Validation

At load and in the Studio (inline at the fields): unknown trigger/condition/action/target, missing or ill-typed parameters
(numbers, ranges, options, HH:MM), items/spells/buffs/mobs not in the game data (patterns are not checked), a target the
trigger does not have, an action that needs a number/refusal/loot the trigger does not offer, a command that is built-in or
used by another rule, a chain without a name, the id not matching the file name. Warnings: unknown parameters, no actions,
the pack not in `Mods:Packs`.

## Studio

"Rules" in the pack's sidebar (+ = new) and "+ New… → Rule — from a template…". The editor: When (trigger, id filter with the
catalogue pickers, trigger parameters, what the event offers), If (all/any, conditions with NOT and "looks at", add / remove /
reorder / copy), Do (actions with their target), Whom (default target), Limits; problems inline and in the right pane;
**Test (simulate)**: choose an online player, the conditions are evaluated on that player now (chance not rolled), the actions are
only described (made-up event: the nearest mob as mob/victim/target, 100 as the number); JSON view / edit. Save writes the file
and reloads the rules on the running server (the toast says how many run). API: `GET /studio/api/rules/palette|players|list?pack=|get?pack=&id=`,
`POST /studio/api/rules/save|delete|validate|simulate|reload`.

Templates: +50 % fame at night in black zones · a boss drops an item to the whole party · potions heal 20 % more for healers ·
announce a world boss kill · daily login reward · `.home` with a 10-minute cooldown · every 100 kills a reward (two rules
joined by a chain) · no mounting in combat · a world event every evening.

## Known limits

- Rules see what the plugin API exposes (README "API v2" limits apply: mobs' casts have no check/hit hooks, auto-attacks only
  through `damage`, buff `tick` only for over-time ticks).
- `timer.daily` and `time`/`weekday` use the server's local clock. Zone colours are those of loaded clusters (`safe` covers blue
  zones and cities).
- `party` and `zone.color` read server state directly (the plugin API has no party view yet).
- The simulation needs an online player and cannot roll chances or predict other plugins' changes.
