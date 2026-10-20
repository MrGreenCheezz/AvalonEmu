# Server plugins (C#)

A plugin is a folder with a `plugin.json` and C# sources (or a prebuilt DLL). The server builds it with the local .NET SDK,
loads it into its own collectible `AssemblyLoadContext`, and calls it on game events. Plugins reference only
**`AlbionLocal.Plugin.Api`** (`src/AlbionLocal.Plugin.Api`), never the server's own assemblies, so a plugin keeps working
across server versions as long as the API version stays the same.

Design notes: `analysis/work/stageN/plugins/SPEC.md`. Russian summary: `MODDING.md`, section "Плагины".

No code needed for many things: **no-code rules** (`mods/<pack>/rules/*.json`, edited in the Studio, applied live) run on this
same API as one built-in plugin, "rules": see `RULES.md`.

## Quick start

1. Copy `plugins/welcome` to `plugins/my-plugin`, rename the class, set `"name": "my-plugin"` in `plugin.json`.
2. Enable it: `"enabled": true` in its `plugin.json`, or list it in the config (`"Plugins": { "List": ["my-plugin"] }`).
3. Start the server (`Plugins:Enabled` is on under the pve preset). It builds the plugin into `data/plugins-bin/my-plugin/`
   (a few seconds the first time, skipped while nothing changed) and loads it. Look for `[plugins] loaded my-plugin` in the log.
4. Edit the sources, then type `.plugins reload my-plugin` in game (Admin) or on the console: unload, rebuild, load,
   without restarting the server.

Build without starting the server (no port is opened): `dotnet run --project src/AlbionLocal.Server -- --plugins-build [name...] [--force]`.

## Folder layout

```
plugins/<name>/
  plugin.json        required
  *.cs               the sources (any sub-folders; bin/ and obj/ are ignored)
  config.json        optional defaults, read through IPluginConfig
  lib/*.dll          optional extra libraries (referenced by the build, loaded into the plugin's context)
  <name>.csproj      optional, for the IDE only (the server builds with a project of its own)
mods/<pack>/plugins/<name>/   the same, shipped with a mod pack (loads when the pack is in Mods:Packs)
data/plugins/<name>/          the plugin's data: storage.json, and the owner's config.json override
data/plugins-bin/<name>/      the generated project (build/) and the build output (out/)
```

`plugin.json`:

```json
{
  "name": "my-plugin",          // defaults to the folder name; no spaces, / \ :
  "version": "1.0.0",
  "author": "me",
  "description": "what it does",
  "apiVersion": 2,              // the API it is written for: 1 or 2 load (PluginApi.MinVersion..Version), others are refused
  "enabled": true,              // loads at the start without being in Plugins:List (default true)
  "dependencies": ["other"],    // loaded first; a missing one fails the load
  "assembly": ""                // a prebuilt DLL (relative path): no build. Empty: build the *.cs, or use the folder's one DLL
}
```

A prebuilt plugin is a class library for `net10.0` that references `AlbionLocal.Plugin.Api.dll` with `Private=false`
(do not ship that DLL: the server's own is used).

## Configuration (`config/albionlocal.json`)

| Key | Default | Meaning |
|---|---|---|
| `Plugins:Enabled` | pve `true`, official `false` | load plugins at the start |
| `Plugins:Path` | `plugins` | the plugin folders |
| `Plugins:List` | `[]` | names to load whatever `enabled` says; empty = every plugin with `"enabled": true` |
| `Plugins:Build` | `true` | build from sources when they changed; `false` = prebuilt DLLs (or an earlier build) only |
| `Plugins:MaxErrors` | `10` | a plugin whose calls threw this many times is disabled |
| `Plugins:SlowMs` | `50` | a call that takes longer is logged as slow (once a minute per plugin) |

The packs in `Mods:Packs` bring their `plugins/` folders: a mod is data patches + server drops + plugins in one folder.
Their plugins load unless their `plugin.json` says `"enabled": false` (or `Plugins:List` is used, then only the listed ones).

## Commands

| Command | Level | |
|---|---|---|
| `.plugins` / `.plugins list` | GameMaster | every plugin found: version, state (loaded, not enabled, failed: why, disabled: why), errors, pack |
| `.plugins reload <name>` | Admin | unload (its handlers, commands and timers dropped, its `Unload` called, storage saved), build when changed (in the background), load |
| `.plugins enable <name>` | Admin | load a plugin that is not loaded (until the restart) |
| `.plugins disable <name>` | Admin | unload a plugin (until the restart) |

Plugin commands show in `.help` with "(plugin name)".

## Writing a plugin

```csharp
using AlbionLocal.Plugin.Api;

public sealed class MyPlugin : PluginBase            // or implement IPlugin yourself
{
    public override string Name => "my-plugin";

    protected override void OnLoad()
    {
        var greeting = Config.Get("Greeting", "Hello, {name}!");
        Events.PlayerLogin += e => e.Player.SendMessage(greeting.Replace("{name}", e.Player.Name));
        Events.MobDied += e =>
        {
            if (e.Mob.Category == "boss") Server.Broadcast($"{e.Killer.Name} killed {e.Mob.Name}!");
        };
        Timers.Every(TimeSpan.FromMinutes(5), () => Log.Info($"{Server.OnlinePlayers.Count} online"));
        Commands.Register("hello", PlayerLevel.Player, "says hello", call => call.Reply($"hello, {call.Player?.Name}"));
    }
}
```

Exactly one public non-abstract class implementing `IPlugin` with a parameterless constructor per plugin. No static state
that outlives the plugin (it would keep the old assembly alive after a reload).

### Threading and safety

- Every event handler, timer, command and `Load`/`Unload` runs **on the world thread**, inside the game's own processing.
  The world takes no locks for you: the API is not thread-safe and must not be called from other threads. Work of your own
  on another thread (a file, a web request) comes back with `Context.Post(action)`, the one call that is safe from any thread.
- Keep handlers short. A handler blocks the whole world while it runs; one that takes longer than `Plugins:SlowMs` is logged.
  The server cannot preempt a handler that never returns (an endless loop hangs the server) or survive a stack overflow:
  those are the plugin author's responsibility.
- An exception in any plugin call is caught, logged with the plugin's name and counted; at `Plugins:MaxErrors` the plugin is
  disabled (no more calls at once, unloaded in the next tick). Other plugins and the game go on. `.plugins reload` brings it back.
- `Load` that throws fails the load (nothing it registered stays).
- The `…Args` objects are only valid during the call; keep `IPlayer` / `IMob` instead (they are live views: `IsOnline`,
  `IsAlive` tell whether they still exist).

## API reference

`IPluginContext` (given to `Load`; `PluginBase` exposes its parts as properties):

| Member | |
|---|---|
| `Name`, `PluginDirectory`, `DataDirectory` | the plugin's name, its folder, `data/plugins/<name>/` |
| `Events` | `IPluginEvents`, below |
| `Server` | `IServer`, below |
| `Log` | `Debug/Info/Warn/Error`, prefixed `plugin:<name>` |
| `Config` | `Get<T>(key, fallback)`, `Bind<T>()`, `Root` (JsonElement), `Reload()`: `config.json` of the folder with `data/plugins/<name>/config.json` over it key by key (comments allowed) |
| `Storage` | `Get<T>(key, fallback)`, `Set<T>(key, value)`, `Remove`, `Contains`, `Keys`, `Save()`: JSON key-value store in `data/plugins/<name>/storage.json`, written 5 s after a change, at unload and at the stop |
| `Timers` | `Every(interval, action)`, `After(delay, action)` on the world clock (at most a tick late); `Dispose()` the result to cancel |
| `Commands` | `Register(path, level, help, handler)`: `.path args…` in chat or on the console; a path a built-in command or another plugin has is refused; the longest matching path wins (a plugin's `.bounty top` would beat the built-in `.bounty`) |
| `Post(action)` | run on the world thread at the next tick (thread-safe) |
| `Hooks` (v2) | the gameplay hooks, below ("API v2") |
| `Game` (v2) | the gameplay actions, below ("API v2") |

`ICommandCall`: `Player` (null on the console without a player), `Level`, `FromConsole`, `Args` (text after the path),
`Words`, `Reply(text)`, `Error(text)`.

### Events (`IPluginEvents`)

| Event | Args | Fires | What you can do |
|---|---|---|---|
| `ServerStarted` | – | the server has started (not for a plugin loaded later) | schedule, read state |
| `ServerStopping` | – | the stop begins; players still in game | save |
| `Tick` | `Seconds`, `DeltaSeconds` | every world tick (50 ms) | prefer `Timers` |
| `PlayerLogin` | `Player`, `FirstLogin` | the first Join after a login, the client in the world | messages, items, buffs |
| `PlayerLogout` | `Player` | the connection ended (not a cluster change) | bookkeeping (messages go nowhere) |
| `ZoneEnter` | `Player`, `Cluster`, `ClusterChange` | every Join (after `PlayerLogin`) | zone greetings, zone rules |
| `ZoneLeave` | `Player`, `Cluster`, `ClusterChange` | leaving a cluster (change or logout), still on the map | |
| `MobSpawned` | `Mob` | a mob put on a map (spawn, respawn, GM, event, plugin; every mob when a cluster loads) | keep it cheap |
| `MobDied` | `Mob`, `Killer`, `Credited` | a kill, after rewards, loot and every system | rewards, announcements |
| `PlayerKnockedDown` / `PlayerDied` | `Player`, `KillerName`, `Killer` | knockdown / death | |
| `Damage` | `Attacker`, `Target`, `Kind`, `OverTime`, `Amount` (settable) | every hit player→mob, mob→player, player→player, before shields | change `Amount`; very hot: keep it trivial |
| `ItemUsed` | `Player`, `Item`, `Count`, `Source` | a consumable use ("inventory" or "slot") | |
| `ItemCrafted` | `Player`, `Item`, `Count`, `Source` (station) | a craft batch (or an NPC shop purchase) | |
| `LootRolled` | `Mob`, `Killer`, `Items` (List, editable) | a mob's loot rolled, before the bag | add / remove drops |
| `Harvested` | `Player`, `Item`, `Count`, `Kind` ("gather"/"fish") | a harvest or catch | |
| `GoalCompleted` | `Player`, `Kind` ("bounty"/"expedition"/"corrupted"), `Description` | a goal done | |
| `BuildingAction` | `Player`, `Building`, `Action`, `Accepted` | a station action asked for | |
| `PartyJoined` / `PartyLeft` | `CharacterId`, `Name`, `Player?`, `PartyId`, `Members`, `Reason` | party changes (companions left out) | |
| `ChatMessage` | `Player`, `Text` | a Say line that is not a command | |

### Actions (`IServer`, `IPlayer`, `IMob`)

`IServer`: `OnlinePlayers`, `FindPlayer(name)`, `LoadedClusters`, `PopulatedClusters`, `Mobs(cluster)`, `UtcNow`, `Preset`,
`Broadcast(text)` (the Announcement channel or a system line to everyone), `SendToZone(cluster, text)`, `ItemExists(name)`,
`SpawnMob(cluster, mob, x, y, facing)` (no respawn), `SpawnBoss(cluster, mob, x, y, facing)` (a one-off world boss with
announcement and boss loot), `StartEvent(kind, cluster, x, y, tier)` (a world event of content/events.json: invasion,
champion, …; returns why not, null when started), `EventKinds`.

`IPlayer` (also an `IUnit`: `ObjectId`, `Name`, `Cluster`, `X`, `Y`, `Health`, `MaxHealth`): `CharacterId` (stable key),
`Level`, `IsOnline`, `Silver`, `Fame` (whole units), `Equipment`, `Inventory` (`ItemInfo(Name, Count, Quality)`),
`CountItem(name)`; `SendMessage(text, error)`, `GiveItem(name, count, quality)` (returns how many fit), `TakeItem(name, count)`,
`GiveSilver(n)`, `TakeSilver(n)`, `GiveFame(n, tier)` (PvE kill fame: feeds the destiny board), `Teleport(x, y)` (same
cluster), `TeleportTo(cluster, x?, y?)` (loading screen), `ApplyBuff(spell)`, `RemoveBuff(spell)`, `HasBuff(spell)`;
v2: `GetSlotSpell(slot)`, `GetSlotSpellOptions(slot)`, `SetSlotSpell(slot, spell, mode, duration?, cooldown)` (below, "Slot spells").

`IMob`: `Tier`, `Category`, `Fame`, `IsAlive`, `IsWorldBoss`, `IsEventMob` (+ `IUnit`).

Silver and fame are whole units in the API (the server keeps them ×10000).

## API v2: gameplay hooks (`Hooks`) and actions (`Game`)

API version 2 (`"apiVersion": 2`) adds custom gameplay logic: spells, buffs, weapons and equipment, mounts, units and the
game's formulas. A version-1 plugin loads unchanged (the server takes `apiVersion` 1 and 2). The shape follows tswow's
livescripts events: every hook is registered **for every id or for one id** (a spell, buff, item, mount or mob uniquename):

```csharp
Hooks.Spells.OnCheckCast(a => ...);                    // every spell
Hooks.Spells.OnCheckCast("FIRESTAFFBOLT2", a => ...);  // only this one (a dictionary lookup at dispatch)
var reg = Hooks.Buffs.OnApply("ENRAGE_BUFF", a => ...);
reg.Dispose();                                          // unregister (all go at the unload anyway)
```

A hook nobody listens to costs one count check where the game would call it. Every handler runs on the world thread in the
same safety wrapper as the events (exceptions logged and counted, `Plugins:SlowMs`). Args are valid only during the call.

**The pattern.** Args that can stop something have `Cancel(reason)` (the engine still sends the client what a refusal needs:
the cast refusal code, the mount refusal code; a non-empty reason goes to the player as an error line). Args whose default
effect can be skipped have `PreventDefault()`. Numbers a handler may change are `Mutable<T>` (`Get()`/`Set(v)`/`Value`,
`Original`). `OnCalc*` formula hooks change a number the engine is about to use.

### Spells (`Hooks.Spells`, id = spell uniquename)

The lifecycle of a player's cast (as TrinityCore's SpellScript):

| Hook | Args | When | Can |
|---|---|---|---|
| `OnCheckCast` | `SpellCheckCastArgs`: `Caster`/`Player`, `Spell`, `Slot` (Q W E Armor Head Shoes Potion Food), `SlotIndex`, `Item` (what granted it), `MountSpell`, `Target`, `X`, `Y` | a CastStart passed the engine's checks | `Cancel(reason)`: CastStart is answered with the refusal, nothing starts |
| `OnCast` | `SpellCastArgs` (same info) | the cast went through: energy paid, CastFinished sent, cooldown started; also mobs' casts (`Caster` an `IMob`, slot 255) | react (effects of your own) |
| `OnCancel` | `SpellCancelArgs` + `Interrupted` | a cast under way dropped: interrupted (stun, damage, dead target) or cancelled (the player, a new cast); mobs too | |
| `OnBeforeHit` / `OnAfterHit` | `SpellHitArgs` + `Projectile` | the hit lands (after hitdelay and a projectile's flight) | Before: `PreventDefault()` skips the spells.xml effects of the hit |
| `OnEffect` | `SpellEffectArgs`: `Caster`, `Spell` (the acting one), `RootSpell`, `Unit`, `FromArea` | a spell (the cast one, one it applies, an area's effect) is about to act on one unit | `PreventDefault()`: none of it on that unit (the client sees a blocked hit, as by a spell immunity) |
| `OnDamageEarly` | `SpellDamageArgs`: `Caster`, `Spell`, `Target`, `Amount` (Mutable), `IsHeal`, `OverTime`, `Area`, `Magic` | each health change of a spell on one unit (players' and mobs' spells, heals too), after the target's resistance, before shields and defence | change `Amount`; `PreventDefault()` drops it (the CastHit still shows) |
| `OnDamageLate` | the same + `HealthLeft` | after it landed (`Amount` = what the health lost or gained) | react |
| `OnTick` | `SpellTickArgs`: `Spell`, `Channel`, `Tick`, `Count`, `Target`, `X`, `Y` | each tick of a channelled spell | |
| `OnAreaCreate` / `OnAreaTick` | `SpellAreaArgs`: `Owner`, `Spell`, `Effect`, `AreaId`, `X`, `Y`, `Radius`, `Seconds`, `Inside` | a spelleffectarea is placed / every tick (the units inside) | |
| `OnExtraBarChanged` | `ExtraBarChangedArgs`: `Player`, `Index` (0-11), `Slot` (Extra1..12), `From`, `To`, `Item`, `Source` (`Equipment`, `Plugin`) | an extra action bar slot holds another spell (gear change, override, reload); id = the new spell, "" = emptied | react |
| `OnSlotSpellChanged` | `SlotSpellChangedArgs`: `Player`, `Slot`, `Item`, `From`, `To`, `Source` (`Player`, `Plugin`, `Revert`), `Temporary` | a slot of an equipped item holds another spell (id = the new spell): the player's choice in the item window, `SetSlotSpell`, a temporary change running out | react |

**Scripted spells** — `Hooks.Spells.Register("MY_SPELL", new SpellScript { OnCast = …, OnHit = …, OnTick = …, ReplaceEffects = true })`:
the callbacks implement the spell server-side. `OnCast` when the cast goes through, `OnHit` when its hit lands, `OnTick` per
channel tick; each gets a `SpellScriptContext` (`Caster`, `Spell`, `Slot`, `Item`, `Target`, `X`, `Y`, `AbilityPower`, `Tick`,
`Game`). With `ReplaceEffects` (the default) the spells.xml effects of the hit and of the channel ticks do not run: the
script is the spell. With `false` the script adds to them. One script per spell (a second registration throws).

How a **ModKit pack's new spell and a plugin work together**: the pack adds the spell to spells.xml (`<activespell
uniquename="MY_SPELL" …>` in its `spells.xml` patch) so the client knows it — icon, cast bar, animation, target type,
range, energy and cooldown come from there — and an item that grants it. The plugin registers a script **by the same
uniquename**: the name is the whole link (no marker attribute). A pack can ship the plugin in its own `plugins/` folder, so
the spell's data and its logic travel together. Keep the XML spell's effects to the visuals (a `nop`, or none) when the
script replaces them, or keep real effects and use `ReplaceEffects = false` to add to them. `plugins/example-spells` scripts
the example-blade pack's `CLOCKWORK_DASH`.

### Buffs (`Hooks.Buffs`, id = the buff spell's uniquename)

| Hook | Args | When | Can |
|---|---|---|---|
| `OnCheckApply` | `BuffCheckArgs`: `Unit`, `Buff`, `Caster` | a spell's timed effects are about to go on a unit (cast spells, area effects, procs, `Game.ApplyBuff`) | `Cancel()`: it stays off |
| `OnApply` | `BuffArgs`: `Unit`, `Buff` (`IBuff`), `IsNew` | it went on (new, or re-applied: a stack more) | |
| `OnStackChange` | `BuffStackArgs` + `Before`, `After` | stacks changed (re-application, charges consumed) | |
| `OnTick` | `BuffArgs` | a damage/heal-over-time tick of it | |
| `OnRemove` | `BuffRemoveArgs` + `How` (`Expired`, `Removed`, `Cleared`) | it went off: ran out, was removed (a cleanse, a dispel, a plugin), cleared (death, reset, knockdown) | |

`IBuff`: `Spell`, `CasterId`, `Caster`, `Stacks`, `SecondsLeft`, `GetData<T>(key, factory)` / `SetData` (this plugin's data
on that buff entry, gone with it). Query a unit's buffs with `unit.Buffs`, `Game.Buffs(unit)`, `Game.FindBuff(unit, spell)`.

### Items and weapons (`Hooks.Items`, id = item uniquename without `@n`)

| Hook | Args | When |
|---|---|---|
| `OnEquip` / `OnUnequip` | `EquipArgs`: `Player`, `Item`, `Quality`, `Slot` (0 main hand … 7 mount …), `IsWeapon`, `AtLogin` | an item into / out of an equipment slot; every worn item at a login (`AtLogin`) |
| `OnWeaponSwap` | `WeaponSwapArgs`: `From`, `To` (id = the new weapon, "" for an empty hand) | the main-hand weapon changed |
| `OnBreak` | `ItemBreakArgs`: `Item`, `Cause` ("death", "knockdown", "gathering", "fishing") | durability reached 0 |
| `OnCalcPassives` | `PassiveArgs`: `Worn`, `Item`, `Add(attribute, value)` | the worn items' passives are worked out again (equipment changed); a filtered hook runs when that item is worn: add stat modifiers that hold while this equipment is worn |

### Mounts (`Hooks.Mounts`, id = mount item)

| Hook | Args | When | Can |
|---|---|---|---|
| `OnCheckMount` | `MountCheckArgs`: `Player`, `Mount`, `Remount` | a mount request passed the checks | `Cancel(reason)`: refused (the client's mount bar aborts) |
| `OnMountStart` | `MountArgs`: `Player`, `Mount`, `Reason` ("mount"/"remount") | the mount bar starts | |
| `OnMounted` | `MountedArgs` + `GallopBuff` (Mutable) | the bar is full, the rider is mounted | change the gallop: the spell the mount's speed buff (castspellaftermoving) applies, another spells.xml buff for a faster or slower gallop, `null` for none |
| `OnMountCancel` | `MountArgs` (`Reason` why) | the bar was cancelled (a walk-off, an action, op 200) | |
| `OnDismount` | `MountArgs`, `Reason` "dismount", "died" (the mount bar ran out), "knockdown" | off the mount | |
| `OnMountDamage` | `MountDamageArgs`: `Amount` (Mutable), `Attacker` | a hit on the mount bar | change `Amount` |
| `OnMountSpell` | `SpellCastArgs` | the ridden mount's own spell was cast (id = the mount) | |

### Units (`Hooks.Units`, id = a mob's uniquename; players match the global registration)

`OnHealthChange` / `OnEnergyChange` (`UnitValueArgs`: `Unit`, `Before`, `After`, `Max`, `Share`, `Source`, `Spell`; hits,
heals, spell energy; not the regeneration), `OnEnterCombat` / `OnLeaveCombat` (players), `OnRevive` (a knocked-down player got
up: timer, ally revive, resurrection, GM), `OnReload` (see below). Knockdown and death are the v1 events `PlayerKnockedDown` /
`PlayerDied`.

### Formulas (`Hooks.Formulas`, `OnCalc*`: `Value` is a `Mutable<double>`)

| Hook | id | Value |
|---|---|---|
| `OnCalcCastTime` | spell | a player's cast time in seconds (after cast-time reductions) |
| `OnCalcCooldown` | spell | a player's cooldown in seconds (sent to the client as CastFinished's factor, so it shows the same) |
| `OnCalcEnergyCost` | spell | a player's energy cost (a cost above the energy refuses the cast) |
| `OnCalcHeal` | spell | a spell's heal on a player (then `OnDamageEarly` with `IsHeal`) |
| `OnCalcThreat` | mob | the damage a player's hit counts as threat on the mob (× its damageaggrofactor) |
| `OnCalcFame` | mob | the fame of a kill for each player of the fame group (whole units, after rates and fame buffs) |
| `OnCalcSilver` | mob | the amount of a silver pile the mob drops |
| `OnCalcItemPower` | item | an item's item power as worn by a player (ability power, damage and stats scale with it; called often: keep it cheap) |

Hit, crit, miss and resist chances do not exist in Albion's combat (every hit lands): there is no hook for them.

### Slot spells (`IPlayer.SetSlotSpell`)

Change the ability in a slot of the equipped items, as the player does in the item's spell window, from a plugin:

```csharp
var why = player.SetSlotSpell(SpellSlot.Q, "CLEAVE");                                  // an option of the equipped weapon
player.SetSlotSpell(SpellSlot.W, "RENDINGSTRIKE", SlotSpellMode.Any, TimeSpan.FromSeconds(30)); // any active spell, 30 s
player.SetSlotSpell(SpellSlot.ArmorPassive, "PASSIVE_BLEEDCHANCE", SlotSpellMode.Any);     // a passive slot
string? now = player.GetSlotSpell(SpellSlot.Q); var options = player.GetSlotSpellOptions(SpellSlot.Q);
```

- **Slots** (`SpellSlot`): `Q`, `W`, `E` (the main hand's third spell, or the off hand's first), `Armor` (R), `Head` (D), `Shoes` (F),
  `Potion`, `Food`, and the passive slots `WeaponPassive`, `ArmorPassive`, `ArmorPassive2`, `HeadPassive`, `ShoesPassive`, `CapePassive`,
  `BagPassive`, `MountPassive`, `MountPassive2` (the item must have that slot). A mount's own active spells are fixed (not a slot).
- **Mode.** `FromItemList` (default): only an option of the equipped item for that slot (`GetSlotSpellOptions`), else refused.
  `Any` (**experimental**): any spell of the slot's kind (active spells for Q-F/potion/food, passives for the passive slots). The server
  casts it as if the item offered it. The client looks a slot's spell id up in its whole spell table (NewEquipmentItem keys 9/10:
  c1r.af 0x180A20170 / be7.abq; CharacterEquipmentChanged key 7: cp0.x8 0x180BDAB90; no check against the item's list, decompiled),
  so the action bar shows its icon and tooltip and casts it; the item's own spell window marks none of its options. Whether the
  client plays a fitting animation for a spell of another weapon is not verified (checklist COMB-06).
- **Duration.** None: kept like the player's choice (saved with the item; potion and food: while that stack stays in the slot, not
  saved). With one: the previous spell comes back when it runs out; meanwhile the save keeps the previous one (a zone change or relog
  ends it early), and the player's own choice in the item window ends it.
- **Cooldown** (`SlotSpellCooldown`): `Keep` (each spell its own, default), `Carry` (the new spell takes the rest of the old one's),
  `Reset` (ready at once); the slot's cooldown is sent to the client (SpellCooldownUpdate) so the old spell's does not stay on it.
- What it does: NewEquipmentItem + CharacterEquipmentChanged (key 9 false: no spell-change lock in the item window), the passives
  worked out again (OnCalcPassives runs), the save, `Spells.OnSlotSpellChanged`. The player's own checks (unlocks, combat) do not apply.
  Returns null when done, else why not.
- GM command for testing: `.slot list` (each slot: item, spell, options), `.slot <slot> <spell> [any] [seconds]`
  (slot names Q W E R D F Potion Food WeaponPassive …; e.g. `.slot Q CLEAVE`, `.slot Q RENDINGSTRIKE any 20`).

### Extra action bars (`IPlayer.GetExtraSlots` / `SetExtraSlot`)

Two extra bars of six slots filled from the worn items' extra spells (`content/extraspells.json`, `mods/<pack>/server/extraspells.json`;
README "Extra action bars", SPEC `analysis/work/stageN/extrabars/SPEC.md`), cast through the client mod:

```csharp
IReadOnlyList<string?> bars = player.GetExtraSlots();     // index 0-5 bar 1, 6-11 bar 2; null = empty
var why = player.SetExtraSlot(0, "BLINK");                 // an activespell over the gear's (null when done)
player.SetExtraSlot(0, null);                              // the gear's spell again
Hooks.Spells.OnExtraBarChanged(a => ...);                  // ExtraBarChangedArgs: Player, Index, Slot (Extra1..12), From, To, Item, Source
Hooks.Spells.OnCheckCast("BLINK", a => { if (a.Slot == SpellSlot.Extra1) a.Cancel("not here"); });
```

- Overrides are kept in memory per character (zone changes keep them; not saved, gone at a restart); the same spell elsewhere in the bars
  leaves that slot. Toggles and passives are refused. `OnExtraBarChanged` fires once per changed slot (id = the new spell, "" = emptied),
  not for the first build at a login/zone change.
- Casts from an extra slot run every cast hook with `Slot` = `SpellSlot.Extra1..Extra12` (`SlotIndex` 200-211; `SpellSlots.ExtraIndex`)
  and the granting worn item as `Item`; the cooldown is the spell's own (shared with a Q-F slot holding the same spell).
- GM: `.extrabar list|set <n> <spell>|clear [n]|reload`.

### Per-entity data and timers

On every `IUnit` (player or mob), callable from the plugin's own handlers, timers and commands:

- `unit.GetData<T>(key, () => new T())`, `SetData(key, value)`, `RemoveData(key)`: the calling plugin's data bag on that
  unit, made on first use, gone with the unit (a player object lives until it leaves its map; a mob until it dies) and
  cleared at the plugin's unload. Each plugin has its own bags.
- `unit.AddTimer(delay, repeats, t => …, name)`: runs every `delay` until it ran `repeats` times (0 = until cancelled);
  cancelled when the unit goes and at the unload; a named timer replaces this plugin's timer of that name on the unit;
  `RemoveTimer(name)`.
- Also on `IUnit`: `Energy`, `MaxEnergy`, `IsAlive`, `InCombat`, `Buffs`.

**Reload.** At a `.plugins reload` the old instance's hooks, bags, timers, areas and stat modifiers go. Right after the new
`Load`, its `Hooks.Units.OnReload` runs once for every joined player and living mob (filtered by mob name when registered so),
so it can rebuild per-entity state.

### Actions (`Game`, `IGameplay`)

| Action | |
|---|---|
| `Spell(name)` | the spells.xml spell (`SpellInfo`: id, kind, category, cast time, cooldown, range, target, stack limit) |
| `Damage(source, target, amount, magic, spell?)` | through the engine's damage path (PvP rules, shields, combat, kills, knockdowns); `amount` is the raw hit (no armour applied). Player→mob, mob→player, player→Hostile player |
| `Heal(source, target, amount, spell?)` | a player or a mob |
| `ApplyBuff(target, spell, caster?, seconds, stacks, abilityPower)` | the timed effects of a spells.xml spell (stats, stun, root, silence, shield, …), with your duration and stacks; its instant effects do not run (use `CastSpell`) |
| `RemoveBuff(target, spell, stacks)`, `Buffs(unit)`, `FindBuff(unit, spell)` | |
| `Knockback(target, fromX, fromY, distance, speed)`, `Pull(target, x, y, speed, stopAt)`, `Dash(unit, x, y, speed)`, `Teleport(unit, x, y)` | forced movement sent as ForcedMovement; a wall on the map's walk grid stops it (the spells' own movement code) |
| `CreateArea(owner, x, y, radius, duration, interval, onTick)` | a server-side area that calls `onTick` with the units inside (first at once); invisible to the clients (pair it with a visual spell); `Dispose()` ends it |
| `Summon(owner, mob, x, y, lifetime?)` | a mob that never respawns, gone after `lifetime` |
| `SetModifier(unit, source, attribute, value)`, `ClearModifier(unit, source, attribute?)` | add to a buff attribute (`physicalattackdamagebonus`, `magicspelldamagebonus`, `bonusdefensevsmobs`, `magiccooldownreduction`, …) until cleared; server-side only (movement speed is the client's: use a buff spell for speed) |
| `CooldownLeft(player, spell)`, `SetCooldown(player, spell, seconds)`, `ResetCooldowns(player)` | the client is told (SpellCooldownUpdate) |
| `CastSpell(caster, spell, target?, x?, y?)` | run a spells.xml spell's effects as the caster's hit (no cast bar, cost or cooldown) |
| `Interrupt(unit)`, `Stats(unit)` (health, energy, item power, armour, magic and CC resistance, tier), `UnitsAround(cluster, x, y, radius)`, `IsEnemy(a, b)` | |

### Example: `plugins/example-spells`

- (a) a scripted `CLOCKWORK_DASH` (the example-blade pack; `FallbackSpell` without it) with `ReplaceEffects = false`: its dash
  runs as spells.xml says, and `OnHit` leaves a 6 s plugin area at the aim point that slows every enemy in it.
- (b) `Mounts.OnMounted("T5_MOUNT_HORSE")`: at night (or with `WhenBuff` on the rider) the gallop applies `FastGallopBuff`.
- (c) a sword (`Weapons`) gives +10 % physical damage while its wielder is below 30 % health: `Units.OnHealthChange` and
  `Items.OnWeaponSwap` set or clear a `Game.SetModifier`, remembered in a data bag; `Units.OnReload` re-checks after a reload.

## Examples

- `plugins/welcome` — greets at login (`Greeting`, `FirstGreeting`), optional login announcements, `.motd` (a GM's
  `.motd set <a|b>` / `.motd reset` kept in storage), login counts per character in storage.
- `plugins/bounty-hunter` — every `IntervalMinutes`, a bounty on a random living elite type (`Categories`, `MinTier`) in a
  populated cluster; the killer gets silver + fame and an announcement; leaderboard and the open bounty in storage;
  `.bounties` (GM: `.bounties new|clear`).
- `plugins/example-spells` (API v2) — a scripted Clockwork Dash with a lingering slow field, a faster night gallop, a sword
  that hits harder at low health (see "API v2" above).

All ship `"enabled": false`. Turn one on with `"Plugins": { "List": ["welcome"] }` or `.plugins enable welcome`.

## Known limits

- The API covers what the events and facades above expose; there is no access to packets, the database or other plugins' state.
- A handler cannot be preempted: an endless loop hangs the world.
- `.plugins enable/disable` last until the restart; the persistent switches are `plugin.json` `enabled` and `Plugins:List`.
- Reload keeps no in-memory state: keep what must survive in `Storage`.
- v2: scripted spells and the cast hooks cover players' casts (mobs' casts: `OnCast`, `OnCancel`, `OnDamageEarly/Late` only); the
  auto-attacks go through the v1 `Damage` event. Toggle spells keep their own cooldown (no `OnCalcCooldown`). Plugin areas are
  invisible to the clients; plugin forced moves go out with spell -1 (not checked against the client's animations yet).
  Energy changes of spells come through `OnEnergyChange`, not `OnDamageEarly`. Mob-on-mob damage is not modelled.
