# Server modules

A module is a mod for the server, in the spirit of AzerothCore's modules: a folder you drop into `modules/`. The server builds it, loads it and connects it, and you can reload
it while the game runs. A module can change nearly anything the server does, and it needs no change to the server's code and no pull request.

Coming from AzerothCore? [AZEROTHCORE.md](AZEROTHCORE.md) maps what you know. Every hook is listed in [HOOKS.md](HOOKS.md). Working on the server itself? Read [CONTRIBUTING.md](CONTRIBUTING.md).

## A first module

```bash
AlbionLocal.Server --new-module mod-my-feature
```

That makes `modules/mod-my-feature/`. Open `mod-my-feature.csproj` in your IDE for the hints (build the server solution once first, the hints come from its API assembly). The whole of a module can be this small:

```csharp
// modules/mod-no-friendly-fire/src/NoFriendlyFire.cs
using AlbionLocal.Modules;

public sealed class NoFriendlyFire : CombatScript
{
    public override void OnBeforeDamage(BeforeDamageArgs e)
    {
        if (e.Attacker is IPlayer a && e.Target is IPlayer t && a.IsInPartyWith(t)) e.Cancel();
    }
}
```

```jsonc
// modules/mod-no-friendly-fire/module.jsonc
{ "id": "mod-no-friendly-fire", "version": "1.0.0", "api": "1.0" }
```

Nothing registers it: the server finds every class that derives from a `*Script` class. Start the server, or type `.modules reload mod-no-friendly-fire` in the game. `.modules` shows what is loaded. The folder [modules/examples](../../modules/examples) has working examples.

## The folder

```
modules/mod-x/
  module.jsonc              what the server needs to know
  src/*.cs                  the code (any folder works; bin/ and obj/ are left out)
  conf/mod-x.jsonc.dist     the default settings, with comments
  lib/*.dll                 the module's own dependencies, optional
  mod-x.csproj              for your IDE only: the server builds with a project of its own
  README.md
```

`modules/` is ignored by git, apart from the examples, so each module can have its own repository: clone it into `modules/`. The server keeps its build in `data/modules-bin/<id>/` and the module's data in `data/modules/<id>/`.

`module.jsonc`:

| Key | Default | |
|---|---|---|
| `id` | the folder's name | the module's name everywhere |
| `version` | `1.0.0` | free text |
| `author`, `description` | | shown by `.modules` |
| `api` | this server's | the module API it was written for: the same major number and no newer minor loads |
| `enabled` | `true` | loads by itself. `false`: only when named in `Modules:List` or by `.modules enable` |
| `requires` | `[]` | modules that must be loaded first. Their public classes are there to use, and when one goes, this one goes with it |
| `loadAfter` | `[]` | load after these when they are present |
| `coreAccess` | `false` | see [Reaching further](#reaching-further) |
| `assembly` | | a prebuilt DLL in the folder, instead of sources. A server without the .NET SDK needs this, or `Modules:Build` false and an earlier build |

Server-wide switches are in the optional `config/modules.jsonc`: `Enabled`, `Path`, `List`, `Build`, `MaxErrors`, `SlowMs`.

## Settings

`conf/<id>.jsonc.dist` holds the defaults. At the module's first load the server copies it to `config/modules/<id>.jsonc`, which is the server owner's to edit: a module update never overwrites it. A key `"Enabled": false` there keeps the module from loading. A key the defaults do not have gets a warning with the closest one.

```csharp
public sealed class Settings { public double LootFactor { get; set; } = 2; }

protected override void OnLoad()
{
    var settings = Config.Bind<Settings>();            // the whole file as a class
    var factor = Config.Get("LootFactor", 2.0);        // or one key
}
```

## Hooks

Every hook has the same shape, so the name tells you what you can do:

- **`OnBefore*`**: something is about to happen. `e.Cancel()` stops it, its values can be set.
- **`OnAfter*`, `On*`**: it happened. Read only.
- **`OnCalc*`**: the engine is about to use a number. Set `e.Value`.

Write them as a method in a `*Script` class (type `override` and the IDE lists them) or as a lambda under `Hooks`. They are the same hooks.

```csharp
public sealed class MyRules : SpellScript
{
    [ForId("FIREBALL")]                                   // only this spell; "T4_*" masks work too
    [Priority(HookPriority.Early)]
    public override void OnBeforeCast(BeforeCastArgs e)
    {
        if (e.Player?.Zone.Kind == ZoneKind.City) e.Cancel("No fire here");
    }
}

public sealed class MySetup : Module
{
    protected override void OnLoad()
    {
        Hooks.Combat.OnBeforeDamage(e => e.Amount *= 2);
        Hooks.Mobs.OnDied("T8_MOB_BOSS", e => Server.Broadcast($"{e.Killer.Name} did it!"));
        Commands.Register("hello", PlayerLevel.Player, "says hello", call => call.Reply("hello"));
        Timers.Every(TimeSpan.FromMinutes(5), () => Log.Info($"{Server.OnlinePlayers.Count} online"));
    }
}
```

The classes: `ServerScript`, `PlayerScript`, `CombatScript`, `MobScript`, `SpellScript`, `BuffScript`, `ItemScript`, `MountScript`, `UnitScript`, `FormulaScript`, `SocialScript`, `MapScript`, `NetworkScript`. `Module` has no hooks of its own and is the place for start-up code. A module can have any number of them.

**Order.** Handlers run by priority: `First`, `Early`, `Normal`, `Late`, `Last`, then `Monitor`. Equal priorities run in load order (`loadAfter`). A cancelled event skips the remaining handlers, except `Monitor` and those marked `[ReceiveCancelled]`. A `Monitor` only watches. `e.CancelledBy` names the module that cancelled.

**Damage.** `Combat.OnBeforeDamage` sees every hit, from auto-attacks to damage over time, before anything of it has happened. Cancelled, it leaves no trace: no damage, no combat, no threat. `e.IsLethal` tells a killing blow; setting `e.Amount` below `e.TargetHealth` keeps the target alive. `Combat.OnAfterDamage` tells what really landed after damage shields. `Spells.OnBeforeDamage` is the same for one spell's effects.

**Mobs.** A `MobScript` limited with `[ForId]` is a script for that mob, like AzerothCore's `CreatureScript`. `OnBeforeThink` runs at each update of its AI: `e.Cancel()` skips the engine's AI for that update, and `e.Mob.CastSpell`, `SetTarget`, `AddThreat`, `MoveTo` and `Despawn` let you drive it yourself. See [mod-scripted-boss](../../modules/examples/mod-scripted-boss).

**Inside a damage or heal hook** `Game.Damage`, `Game.Heal` and `Game.CastSpell` wait for the next tick and return 0 or true, so a handler never hits into the middle of a hit. Spawning (`Game.Summon`, `Server.SpawnMob`) is refused there: do it a moment later with `Timers.After(TimeSpan.Zero, ...)`. `Mobs.OnAggro` is told at the next tick. Hooks running inside each other are cut off 16 levels deep, and that counts as an error of the module. What a handler sends from `Network.OnEvent` goes straight out, without the hook.

**Safety.** A handler that throws is logged, the game goes on, and after `Modules:MaxErrors` errors the module is switched off. A slow handler is logged: handlers run inside the world's tick, so they hold everything up. Keep them short and never wait on anything. Bots do not fire most player hooks; `IPlayer.IsBot` tells them apart where they do.

**Threads.** Everything runs on the world thread. Work on another thread comes back with `Context.Post(...)`, the one call that is safe from anywhere.

**Lifetime.** Args are valid only during the call: keep the `IPlayer`, not the args. No static state: it would keep the old code alive after a reload. What a module registers goes with it when it unloads.

## Types, not text

Nothing about an item, a spell, a mob or a zone has to be read out of its name. The hooks and `Server` hand out objects that say what they are, and a set of values is an enum, so type `.` and the IDE lists them.

```csharp
// What a player wears. An empty slot is null.
if (player.MainHand is { Weapon: WeaponType.Sword, Tier: >= 6, Quality: >= ItemQuality.Excellent }) ...
if (player.Head is { Armor: ArmorType.Plate }) ...
var shoes = player.Worn(EquipSlot.Shoes);

// In hooks.
public override void OnEquip(EquipArgs e)
{
    if (e.Slot == EquipSlot.MainHand && e.Item.IsTwoHanded) ...
}

public override void OnBeforeDamage(BeforeDamageArgs e)
{
    if (e.Spell is { Category: SpellCategory.CrowdControl }) e.Cancel();
    if (e.Attacker is IPlayer { MainHand.Weapon: WeaponType.Bow } && e.Target is IMob { Category: MobCategory.Boss }) e.Amount *= 1.2;
    if (e.IsDirectHit && Roll.Chance(0.15)) Game.ApplyBuff(e.Target, Roll.Pick(Debuffs), e.Attacker);   // an attack or a spell, not a tick of poison
}

// Where a player is.
if (player.Zone is { Color: ZoneColor.Black, IsLethal: true }) ...

// The whole of the game's data, to search.
var bows = Server.Items.Where(i => i.Weapon == WeaponType.Bow && i.Tier == 8);
var better = Server.Item(item.Family, item.Tier + 1);              // the same item, a tier up
var spell = Server.Spell("FIREBALL");

// Stats, a unit's own data, a command's arguments, settings.
Game.SetModifier(unit, "rage", Stat.AttackDamage, 0.2);
player.Data<Rage>().Stacks++;                                      // a class of yours is the key
if (call.PlayerArg(0) is not { } target || call.ItemArg(1) is not { } gift) return;   // the caller has been told what is wrong
target.GiveItem(gift, call.IntArg(2, 1));
public List<WeaponType> Weapons { get; set; } = [WeaponType.Sword]; // in the settings file: "Weapons": ["Sword", "Bow"]
```

| You have | It says | Comes from |
|---|---|---|
| `ItemType` | `Tier`, `Enchantment`, `Slot`, `Category`, `Weapon`, `Armor`, `OffHand`, `Tool`, `Consumable`, `IsTwoHanded`, `ItemPower`, `Family`, `BaseName` | `Server.Item`, `Server.Items`, hooks about a kind of item (crafted, harvested, used) |
| `ItemInfo` | all of `ItemType`, with `Count`, `Quality`, `Durability` | `player.MainHand`, `Worn`, `Equipment`, `Inventory`, hooks about a real item (equip, break) |
| `SpellInfo` | `Kind`, `Category`, `Target`, `CastTime`, `Cooldown`, `Range`, `IsChanneled`, `IsProjectile` | `Server.Spell`, every spell and damage hook |
| `MobType` | `Tier`, `Category`, `Faction`, `Fame`, `IsElite` | `Server.Mob`, `mob.Type` |
| `ZoneInfo` | `Kind`, `Color`, `Pvp`, `IsLethal`, `DisplayName` | `unit.Zone`, `Server.Zone`, zone and map hooks |

**Naming one thing is still a name**: `[ForId("T4_MAIN_SWORD")]`, `Server.Item("T4_MAIN_SWORD")`, `player.GiveItem("T4_ORE", 3)`. A misspelt one gets a line in the log with the closest name. An object goes wherever a name goes: `player.GiveItem(e.Item)`, `Game.ApplyBuff(target, e.Spell)`, `Game.Summon(owner, mobType, x, y)`, and `e.Item.Is("T*_MAIN_SWORD")` takes a name or a mask.

**Two items are equal when they are the same kind**, `T4_MAIN_SWORD@1` whatever its quality or count. `Equipment` lists only the filled slots, so `Equipment[0]` need not be the main hand: ask for `MainHand` or `Worn(slot)`.

**For what no type covers** the raw data is still there: `item.Attribute("attackdamage")`, `item.Subcategory`, `spell.CategoryName`, `mob.FactionName`, `zone.Type`. Words such as `e.Detail` or `SilverChangedArgs.Reason` are for logs and may change: do not read them in code. The network hooks stay by name (`Hooks.Network.OnRequest("Move", ...)`), because that level is the protocol itself: `.modules ops` lists the names, and a misspelt one gets a hint.

## What became of a hit

| The hit | The hook |
|---|---|
| is about to land: change it, or cancel it | `Combat.OnBeforeDamage` |
| meets a damage shield: change what the shield takes, or let it through | `Combat.OnBeforeAbsorb` |
| was taken by a shield: how much, what is left, whether it broke | `Combat.OnAfterAbsorb` |
| took health | `Combat.OnAfterDamage` |
| did not land: `e.Reason`, and `e.ProtectedBy` for the effect that stopped it | `Combat.OnHitAvoided` |
| goes on a mount's bar | `Mounts.OnCalcMountDamage` |
| comes back reflected: a new hit with `Origin: DamageOrigin.Reflected` | `Combat.OnBeforeDamage` |
| sets off a reaction (ENRAGE, a parry's riposte, a passive's proc) | `Spells.OnBeforeTrigger` |

`e.IsDirectHit` is on all of them but the last: true for an attack or a spell's hit, false for a tick over time, reflected damage or a passive's proc.

**The game has no dodge roll.** Its DODGE spell is 0.8 seconds of invincibility and of immunity to debuffs, so a dodge comes to `OnHitAvoided`. A dodge of your own is a cancelled hit, which the other modules see as `HitAvoidReason.Cancelled`. Stepping out of a spell's area hits nobody: `Spells.OnAfterHit` tells of that for players' and mobs' casts alike, with `e.UnitsHit` at 0.

```csharp
public override void OnHitAvoided(HitAvoidedArgs e)
{
    if (e.ProtectedBy is { Spell.Name: "DODGE" } && e.Target is IPlayer p) p.SendMessage("Dodged!");
}

public override void OnBeforeAbsorb(AbsorbArgs e)
{
    if (e.Attacker is IPlayer { MainHand.Weapon: WeaponType.Bow }) e.Cancel();   // arrows pass through shields
}

public override void OnBeforeDamage(BeforeDamageArgs e)
{
    if (e.IsDirectHit && e.Target is IPlayer { Armor.Armor: ArmorType.Leather } && Roll.Chance(0.1)) e.Cancel();   // leather dodges one hit in ten
}
```

## A boss of your own

`Server.DefineMob` makes a mob type of your own, a copy of one from mobs.xml with your numbers. The hooks and the spawns go by your name and no mob of the base type is touched. The clients see the base type's model, name and animations, because those live in their own files. See [mod-scripted-boss](../../modules/examples/mod-scripted-boss).

```csharp
[ForId("WARDEN")]
public sealed class Warden : MobScript
{
    protected override void OnLoad() => Server.DefineMob("WARDEN", "T4_MOB_ROAMING_UNDEAD_SOLDIER_MINIBOSS", m =>
    {
        m.MaxHealth = 250000;
        m.AttackDamage = 900;
        m.NoRotation = true;                                    // only what this module tells it to cast
    });

    public override void OnBeforeThink(BeforeThinkArgs e)
    {
        var boss = e.Mob;
        if (boss.Threat is not [var top, ..]) return;           // nobody to fight: the AI
        e.Cancel();                                             // the rest of this update is yours
        if (boss.IsCasting) return;
        if (Roll.Chance(0.01)) boss.CastSpell("MOB_ROYAL_GUARD_MAGE_MAGICSHOCK", null, top.Player.X, top.Player.Y, castSeconds: 2);
        else if (!boss.InReach(top.Player)) boss.MoveTo(top.Player.X, top.Player.Y);
        else boss.Attack(top.Player);
    }
}
```

| To | Use |
|---|---|
| spawn it | `Server.SpawnMob`, `Server.SpawnBoss`, `Game.Summon`, on a loaded map (`Maps.OnLoaded` tells of one) |
| change its numbers in a fight | `Game.SetModifier(boss, "phase", Stat.AttackDamage, 1)`: `Stat.MaxHealth`, `AttackSpeed`, `MoveSpeed`, `Armor`, `MagicResist` |
| think for it | `Mobs.OnBeforeThink` with `e.Cancel()`, for all of a fight or a phase of it |
| order it | `Attack`, `MoveTo`, `Stop`, `FaceTo`, `CastSpell`, `SetTarget`, `AddThreat`, `Despawn` |
| ask it | `Target`, `Threat`, `InReach`, `CurrentSpell`, `IsCasting`, `IsMoving`, `Data<T>()` for what you keep on it |
| a spell with a cast bar, in your time | `boss.CastSpell(spell, target, x, y, castSeconds: 2)` |
| a spell at once | `Game.CastSpell(boss, spell, target, x, y)` |
| a spell's look with your effects | `Hooks.Spells.Register(spell, new CustomSpell { OnHit = c => ... })`: `c.Caster` is the mob |
| something on the ground | `Game.CreateArea(boss, x, y, radius, duration, interval, tick => ..., visual: "FIREWALL")` |
| keep it in the fight | `Mobs.OnBeforeLeaveCombat` with `e.Cancel()` |
| make it invulnerable | `Combat.OnBeforeDamage` with `e.Cancel()` |
| its loot and rewards | `Mobs.OnLootRolled`, `Formulas.OnCalcFame`, `OnCalcSilver` |

A mob that casts does not swing, and a mob left cancelled neither chases nor swings: that is `Attack` and `MoveTo`, as above. What the clients draw comes from their files: `visual` shows a spell's area with its own size and look, whatever the radius. There is no fight between mobs yet: `Game.Damage` of a mob on a mob does nothing.

## What else a module has

| | |
|---|---|
| `Server` | who is online, messages, spawning mobs and bosses, world events |
| `Game` | damage, heal, buffs, forced movement, areas, summons, stat modifiers, cooldowns, running a spell |
| `IPlayer`, `IMob` | a live view of a unit: items, silver, fame, teleport, a mob's target, threat, casts and movement, data and timers bound to the unit |
| `Timers` | timers on the world clock |
| `Roll` | chance: `Roll.Chance(0.15)` is true 15 % of the time, `Roll.Pick(list)` is one of the list |
| `Commands` | your own chat and console commands |
| `Storage` | a small saved key-value store (`data/modules/<id>/storage.json`) |
| `Hooks.Spells.Register` | a spell implemented in code |
| `Context.DataDirectory` | a folder for files of your own |

A database API is planned. Until then use `Storage` or your own files.

## Reaching further

The hooks are the stable contract: they do not change except in a new major API version. When there is no hook for what you need, you do not have to wait for one:

**Network hooks** see everything between a client and the server, with no change to the server: every operation a client sends (also those the server has no handler for), every event and response it gets.

```csharp
public sealed class NoMarket : NetworkScript
{
    [ForId("AuctionCreateOffer")]
    public override void OnRequest(RequestArgs e) => e.Reject(reason: "The market is closed");
}
```

`e.Reject(code, reason)` refuses an operation, `e.Respond(response)` answers it yourself, `e.Drop()` ignores it. `e.Cancel()` keeps an event or response from one client, `e.Replace(message)` sends another. `.modules ops` lists the names. Never change an outgoing message in place: one object may be going to many clients.

**Core access.** With `"coreAccess": true` in `module.jsonc` a module compiles against the server's own assemblies and can use anything public in them. `ModuleCore` leads from the API's objects to the server's. The message classes for typed network hooks are there too (`e.As<SayRequest>()`), with the client's obfuscated field names.

```csharp
var player = ModuleCore.Player(e.Player);   // AlbionLocal.Server.Entities.Player
var world = ModuleCore.World(Context);      // AlbionLocal.Server.World
```

Nothing there is promised to stay. After a server update such a module may not build: the server says why, leaves it out and runs on.

**Patching any method** (Harmony) is not available yet: the Harmony build at hand crashes on .NET 10, and a newer one still has to be tried.

**A new hook** is usually one line in the server. Open a pull request or an issue; meanwhile the above gets you there.

Known limits: `Mobs.OnBeforeAggro` covers only a mob's own pick of a target (damage, threat and alerts start a fight without asking; `OnAggro` tells of them). `Players.OnSilverChanged` misses market credits applied before the Join. A cancelled heal still counts for healing sickness. Without a listener each hook costs one check; this has not been measured under load yet.

A client shows only what is in its own data: a new item or spell needs the client's data changed too, which a server module cannot do.

Modules are full-trust code: there is no sandbox. Install only what you trust.

## Commands

| Command | Level | |
|---|---|---|
| `.modules`, `.modules list` | GM | every module found: version, state, errors |
| `.modules info <id>` | GM | one module: folder, hooks, commands, timers, last error |
| `.modules reload <id>` | Admin | unload, build if the sources changed, load |
| `.modules enable <id>`, `.modules disable <id>` | Admin | until the restart |
| `.modules doctor` | GM | checks manifests, API versions, dependencies and settings, and says what to fix |
| `.modules hooks [filter]` | GM | the hooks and who listens |
| `.modules trace <hook>` | GM | logs each handler run of a hook with its time and whether it cancelled; again to stop, `off` for all |
| `.modules timings` | GM | the handlers by the time they took |
| `.modules ops [filter]` | GM | the operation and event names for the network hooks |

On the command line, without a running server: `--new-module <id>`, `--check-modules [--build]`, `--modules-build [id...] [--force]`, `--modules-docs <file>`, `--modules-api-baseline <file>`.
