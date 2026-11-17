using System.Numerics;
using AlbionLocal.Modules;
using AlbionLocal.Server.Entities;
using AlbionLocal.Server.Game;
using AlbionLocal.Server.Network;
using AlbionLocal.Server.Protocol;
using AlbionLocal.Server.Systems;
using static AlbionLocal.SelfTest.AlbionTests;
using static AlbionLocal.SelfTest.CombatTest;

namespace AlbionLocal.SelfTest;

/// <summary>
/// The plugin API v2 hooks (docs/modules/README.md "Hooks"), light: a refused cast and a changed hit, a scripted spell with an entity data
/// bag and timer, the mount hooks. In-process plugins on the combat arena (T4 axe, a ghoul at 1.5 m).
/// </summary>
public static class ModuleHooksTest
{
    public static IEnumerable<(string, Action)> All =>
    [
        ("modules: a hit between players cancelled leaves no damage and no combat, its CastHit still goes out; a changed Amount is what lands; a handler's own hit waits for the next tick", CancelPvp),
        ("modules: a handler that sets off its own hook is stopped and the module disabled, nothing overflows", HookLoop),
        ("modules: Combat.OnBeforeDamage Cancel leaves no trace of a hit (no damage, combat or target), a changed Amount is what lands, OnAfterDamage sees it", CancelDamage),
        ("modules: Spells.OnBeforeCast(\"RENDINGSTRIKE\") refuses the cast (CastStart rc + the reason line, no cast), OnBeforeDamage(\"AXESMASH\") × 1.5 on its HealthUpdate", CancelAndModify),
        ("modules: a scripted AXESMASH (ReplaceEffects): OnCast + OnHit, Game.ApplyBuff puts a 3 s buff on the caster, no spells.xml damage; a data bag and a 2-shot entity timer", ScriptedSpell),
        ("modules: Items.OnEquip at login; Mounts.OnBeforeMount refuses once (Mount rc), OnMounted fires and its GallopBuff = null takes the gallop away", MountHooks),
        ("modules: Mobs.OnBeforeThink Cancel stops a mob's AI next to a player, OnBeforeAggro keeps it idle, then OnAggro and OnLeaveCombat tell the fight", ThinkAndAggro),
        ("modules: IMob.CastSpell starts a cast through the mobs' path and refuses what it cannot; SetTarget, MoveTo and Despawn; a driven mob's walk goes on", MobActions),
        ("modules: Players.OnBeforeCommand Cancel: the command does not run, the reason is its error; the console has no player", CommandHook),
        ("modules: Players.OnFameGained and OnSilverChanged in whole units; Maps.OnLoaded and OnUnloaded", FameSilverMaps),
        ("modules: e.IsDirectHit is an attack or a spell's hit and not its tick over time; Roll.Chance and Roll.Pick", DirectHitAndRoll),
        ("modules: Spells.OnAfterHit says how many units a cast reached, a player's and a mob's", UnitsHitCount),
        ("modules: a boss of a module's own: DefineMob on a base type's model, Attack, Threat, CurrentSpell, a cast's time, a mob's instant and scripted spells, OnBeforeLeaveCombat, Stats, a seen area, gone at the unload", OwnBoss),
        ("modules: what became of a hit: OnAfterDamage's origin, shields (OnBeforeAbsorb, OnAfterAbsorb), OnHitAvoided (DODGE, a spell immunity, a cancel), OnBeforeTrigger (ENRAGE), the mount's bar", HitOutcomes),
        ("modules: items, spells, mobs and zones come as types: an empty hand is null though a helmet is worn, a claymore is a sword, @n is no trouble", TypedItems),
        ("modules: Items.OnBeforeLootTake leaves an item in the bag; Mobs.OnBeforeRespawn Cancel keeps a spawn point's mob away", LootAndRespawn),
    ];

    private sealed class Thinker : MobScript
    {
        public bool Block = true, NoAggro;
        public int Thinks, Refused, Aggros, Left;
        public double Delta;
        public string? Reason;

        public override void OnBeforeThink(BeforeThinkArgs e)
        {
            Thinks++;
            Delta = e.DeltaSeconds;
            if (Block) e.Cancel();
        }

        public override void OnBeforeAggro(BeforeAggroArgs e)
        {
            if (!NoAggro) return;
            Refused++;
            e.Cancel();
        }

        public override void OnAggro(MobAggroArgs e) => Aggros++;

        public override void OnLeaveCombat(MobLeaveCombatArgs e)
        {
            Left++;
            Reason = e.Detail;
        }
    }

    private static void ThinkAndAggro()
    {
        using var arena = new Arena(ai: true);
        var world = arena.World;
        var thinker = new Thinker();
        Check(world.Modules.Load("thinker", thinker) is null, "the in-process module loads");
        var (_, peer, me) = arena.Join("Bait", new InventoryItem("T4_MAIN_SWORD"));
        var ghoul = arena.Spawn("T4_MOB_ROAMING_UNDEAD_GHOUL", new Vector2(1.5f, 0));
        var health = me.Health.At(world.GameNow);
        var mark = peer.Events.Count;
        arena.Run(4);
        Check(thinker.Thinks > 0 && thinker.Delta > 0 && ghoul.TargetId == 0 && thinker.Aggros == 0 && me.Health.At(world.GameNow) == health
              && !Since(peer, mark).Any(e => e.Code == EventCodes.Attack),
              $"cancelled, the ghoul 1.5 m from the player does nothing (thinks {thinker.Thinks}, target {ghoul.TargetId}, health {me.Health.At(world.GameNow)})");
        thinker.Block = false;
        thinker.NoAggro = true;
        arena.Run(1);
        Check(thinker.Refused > 0 && ghoul.TargetId == 0 && thinker.Aggros == 0, $"OnBeforeAggro Cancel keeps it idle (refused {thinker.Refused}, target {ghoul.TargetId})");
        thinker.NoAggro = false;
        arena.Run(4);
        Check(ghoul.TargetId == me.Id && thinker.Aggros == 1 && me.Health.At(world.GameNow) < health,
              $"let go, it takes the player and hits it, OnAggro once (target {ghoul.TargetId}, aggros {thinker.Aggros}, health {me.Health.At(world.GameNow)})");
        world.MobAi = false;
        Check(thinker.Left == 1 && thinker.Reason is { Length: > 0 }, $"OnLeaveCombat when the fight ends (left {thinker.Left}, {thinker.Reason})");
        world.Modules.Shutdown();
    }

    private sealed class Driver : MobScript
    {
        public IServer Game_ => Server;
        public bool Drive;

        public override void OnBeforeThink(BeforeThinkArgs e)
        {
            if (Drive) e.Cancel();
        }
    }

    private sealed class CastSeen : SpellScript
    {
        public int Casts;

        public override void OnCast(SpellCastArgs e)
        {
            if (e.Caster is IMob) Casts++;
        }
    }

    private static void MobActions()
    {
        using var arena = new Arena(ai: true, spells: true);
        var world = arena.World;
        var driver = new Driver { Drive = true };
        var seen = new CastSeen();
        Check(world.Modules.Load("driver", driver) is null && world.Modules.Load("seen", seen) is null, "the in-process modules load");
        var (_, peer, me) = arena.Join("Target", new InventoryItem("T4_MAIN_SWORD"));
        var ghoul = arena.Spawn("T4_MOB_ROAMING_UNDEAD_GHOUL", new Vector2(1.5f, 0));
        var mob = driver.Game_.Mobs("0204").Single(m => m.ObjectId == ghoul.Id);
        var target = driver.Game_.FindPlayer("Target")!;
        Check(mob.Target is null && !mob.CastSpell("NO_SUCH_SPELL", target) && !mob.CastSpell("MOB_UNDEAD_GHOUL_BITE"), "no such spell, and no target to bite: refused");
        Check(mob.SetTarget(target) && ghoul.TargetId == me.Id && mob.Target?.ObjectId == me.Id && mob.InCombat, $"SetTarget: the player is its target (target {ghoul.TargetId})");

        var bite = Client.Spells!.Template("MOB_UNDEAD_GHOUL_BITE")!;
        var mark = peer.Events.Count;
        Check(mob.CastSpell("MOB_UNDEAD_GHOUL_BITE", target), "the bite starts");
        var start = Since(peer, mark).FirstOrDefault(e => e.Code == EventCodes.CastStart && L(e.P, 0) == ghoul.Id);
        Check(start.P is not null && Convert.ToInt16(start.P[5]) == bite.Id && L(start.P, 7) == me.Id, $"CastStart of the bite at the player (got {Show(Since(peer, mark))})");
        arena.Run(1);
        Check(seen.Casts == 1 && Since(peer, mark).Any(e => e.Code == EventCodes.CastFinished && L(e.P, 0) == ghoul.Id), $"it finishes through the mobs' path, Spells.OnCast for a mob (casts {seen.Casts})");

        var from = ghoul.Position;
        Check(mob.MoveTo(from.X + 3, from.Y), "the walk starts");
        arena.Run(10);
        Check(!ghoul.Motion.Moving && Vector2.Distance(ghoul.Position, from + new Vector2(3, 0)) < 0.5f, $"its AI cancelled, the walk still arrives (at {ghoul.Position}, from {from})");
        Check(mob.Despawn(respawn: false) && ghoul.Removed && !mob.Despawn() && !mob.CastSpell("MOB_UNDEAD_GHOUL_BITE", target), "Despawn takes it off the map, then nothing works");
        world.Modules.Shutdown();
    }

    private sealed class CommandRules : PlayerScript
    {
        public readonly List<(string? Player, string Line)> Seen = [];
        public bool Allow;

        public void Register(Action<ICommandCall> handler) => Commands.Register("gift", PlayerLevel.Player, "a test", handler);

        public override void OnBeforeCommand(BeforeCommandArgs e)
        {
            if (Allow) return;
            Seen.Add((e.Player?.Name, e.Line));
            e.Cancel("Not today");
        }
    }

    private static void CommandHook()
    {
        using var arena = new Arena();
        var world = arena.World;
        var rules = new CommandRules();
        Check(world.Modules.Load("rules", rules) is null, "the in-process module loads");
        var (_, _, me) = arena.Join("Typist", new InventoryItem("T4_MAIN_SWORD"));
        var replies = new List<(string Text, bool Error)>();
        (IPlayer? To, ItemType? Gift, SpellInfo? Spell, int Count)? asked = null;
        rules.Register(call => asked = (call.PlayerArg(0), call.ItemArg(1), call.SpellArg(2), call.IntArg(3, 1)));
        var context = new AlbionLocal.Server.Commands.CommandContext(world, me, AlbionLocal.Server.Persistence.AccountLevel.Admin, (t, e) => replies.Add((t, e)));
        rules.Allow = true;
        world.Commands.Execute(context, ".gift Typist T4_MAIN_SWORD@1 RENDINGSTRIKE 3");
        Check(asked is { To.Name: "Typist", Gift.Enchantment: 1, Spell.Name: "RENDINGSTRIKE", Count: 3 } && replies.Count == 0, $"a command's arguments as a player, an item, a spell, a number ({asked}, {string.Join(" | ", replies)})");
        world.Commands.Execute(context, ".gift Nobody T4_MAIN_SWROD");
        Check(asked is { To: null, Gift: null, Spell: null } && replies.Any(r => r.Error && r.Text.Contains("Nobody")) && replies.Any(r => r.Error && r.Text.Contains("T4_MAIN_SWORD")),
            $"a bad one is null, and the caller is told, with the closest name ({string.Join(" | ", replies)})");
        replies.Clear();
        rules.Allow = false;
        var scale = world.MobRespawnScale;
        world.Commands.Execute(new AlbionLocal.Server.Commands.CommandContext(world, me, AlbionLocal.Server.Persistence.AccountLevel.Admin, (t, e) => replies.Add((t, e))), ".mob respawnscale 7");
        Check(world.MobRespawnScale == scale && replies is [("Not today", true)] && rules.Seen is [("Typist", ".mob respawnscale 7")],
              $"it does not run, the reason is its error (scale {world.MobRespawnScale}, replies {string.Join(" | ", replies)})");
        world.Commands.ExecuteConsole(world, ".mob respawnscale 7", (t, e) => replies.Add((t, e)));
        Check(world.MobRespawnScale == scale && rules.Seen.Count == 2 && rules.Seen[1].Player is null, $"from the console: no player (got {rules.Seen[^1].Player})");
        world.Modules.Shutdown();
    }

    private sealed class Ledger : PlayerScript
    {
        public IServer Game_ => Server;
        public double Fame, Delta, Total;
        public AlbionLocal.Modules.FameSource Source;

        public override void OnFameGained(FameGainedArgs e) => (Fame, Source) = (e.Amount, e.Source);

        public override void OnSilverChanged(SilverChangedArgs e) => (Delta, Total) = (e.Delta, e.Total);
    }

    private sealed class MapLog : MapScript
    {
        public readonly List<string> Log_ = [];

        public override void OnLoaded(MapArgs e) => Log_.Add("+" + e.Zone.Cluster);

        public override void OnUnloaded(MapArgs e) => Log_.Add("-" + e.Zone.Cluster);
    }

    private sealed class Wearer : ItemScript
    {
        public IServer Game_ => Server;
        public ItemInfo? Equipped;
        public EquipSlot Slot;

        public override void OnEquip(EquipArgs e)
        {
            if (e.Slot == EquipSlot.Head) (Equipped, Slot) = (e.Item, e.Slot);
        }
    }

    private static void TypedItems()
    {
        using var arena = new Arena();
        var wearer = new Wearer();
        Check(arena.World.Modules.Load("wearer", wearer) is null, "the in-process module loads");
        // No weapon, but a helmet: the first worn item is the helmet, and MainHand still says what it says.
        arena.Setup = c =>
        {
            c.Equipment[0] = null;
            c.Equipment[2] = new InventoryItem("T5_HEAD_PLATE_SET1");
            c.Equipment[3] = new InventoryItem("T4_ARMOR_LEATHER_SET1");
        };
        arena.Join("Wearer", new InventoryItem("T4_MAIN_SWORD"));
        var server = wearer.Game_;
        var player = server.FindPlayer("Wearer")!;

        Check(player.MainHand is null && player.Worn(EquipSlot.MainHand) is null && player.Equipment[0].Slot != EquipSlot.MainHand,
            $"an empty hand is null though other things are worn (main hand {player.MainHand}, first worn {player.Equipment[0]})");
        Check(player.Head is { Armor: ArmorType.Plate, Tier: 5, Category: ItemCategory.Head, Slot: EquipSlot.Head } && player.Armor is { Armor: ArmorType.Leather },
            $"the helmet and armour say what they are ({player.Head}, {player.Armor})");
        Check(wearer.Equipped is { Armor: ArmorType.Plate } && wearer.Slot == EquipSlot.Head, $"OnEquip hands out the item and its slot ({wearer.Equipped}, {wearer.Slot})");
        Check(player.IsWearing("T*_HEAD_PLATE_*") && !player.IsWearing("T5_HEAD_CLOTH*"), "IsWearing takes a mask");
        Check(player is { Head.Armor: ArmorType.Plate } and not { MainHand.Weapon: WeaponType.Bow } && player.CountItem(player.MainHand) == 0, "patterns reach through a slot, and an empty hand as a name finds nothing");

        var sword = server.Item("T4_MAIN_SWORD@1");
        Check(sword is { Weapon: WeaponType.Sword, Tier: 4, Enchantment: 1, BaseName: "T4_MAIN_SWORD", Family: "MAIN_SWORD", Slot: EquipSlot.MainHand, IsTwoHanded: false, IsWeapon: true },
            $"an enchanted sword reads as parts ({sword?.Name}, {sword?.Weapon}, T{sword?.Tier}, @{sword?.Enchantment}, {sword?.Family})");
        Check(sword!.Is("T4_MAIN_SWORD") && sword.Is("t*_main_sword") && sword.Is("T4_MAIN_SWORD@1") && !sword.Is("T4_MAIN_SWORD@2") && !sword.Is("T4_MAIN"), "Is: with or without @n, a mask, whole names only");
        Check(server.Item("T6_2H_CLAYMORE") is { Weapon: WeaponType.Sword, IsTwoHanded: true }, "a claymore is a sword without reading its name");
        Check(server.Item("T4_2H_BOW") is { Weapon: WeaponType.Bow } && server.Item("T4_MAIN_FIRESTAFF") is { Weapon: WeaponType.FireStaff }, "a bow and a fire staff");
        Check(server.Item("T4_OFF_SHIELD") is { OffHand: OffHandType.Shield, Slot: EquipSlot.OffHand, Weapon: WeaponType.None }, "a shield");
        Check(server.Item("T4_2H_TOOL_PICK") is { Tool: ToolType.Ore, Slot: EquipSlot.MainHand, IsWeapon: false } && server.Item("T4_2H_TOOL_FISHINGROD") is { Tool: ToolType.Fishing },
            $"tools are tools, not weapons ({server.Item("T4_2H_TOOL_PICK")?.Tool}, {server.Item("T4_2H_TOOL_FISHINGROD")?.Tool})");
        Check(server.Item("T4_POTION_HEAL") is { Consumable: ConsumableType.Potion, Slot: EquipSlot.Potion } && server.Item("T4_ORE") is { Category: ItemCategory.Resource, Slot: EquipSlot.None }, "a potion and an ore");
        Check(server.Item("MAIN_SWORD", 6, 2)?.Name == "T6_MAIN_SWORD@2" && server.Item("MAIN_SWORD", 6)?.Name == "T6_MAIN_SWORD" && server.Item("NO_SUCH_THING") is null, "an item by family and tier");
        Check(server.Items.Count(i => i.Weapon == WeaponType.Bow && i.Tier == 8 && i.Enchantment == 0) >= 3, "the catalogue can be searched");
        Check(sword == server.Item("T4_MAIN_SWORD@1") && sword != server.Item("T4_MAIN_SWORD") && player.Head == server.Item("T5_HEAD_PLATE_SET1"), "items are equal by kind, whatever the quality or the count");

        Check(player.GiveItem(sword, 1, ItemQuality.Excellent) == 1 && player.CountItem(sword) == 1 && player.Inventory.Any(i => i == sword && i.Quality == ItemQuality.Excellent),
            "an item type goes where the API takes a name, with a typed quality");
        Check(player.GiveItem("T4_MAIN_SWROD") == 0, "a misspelt item gives nothing (and a hint in the log)");

        var strike = server.Spell("RENDINGSTRIKE");
        Check(strike is { Kind: SpellKind.Active, Target: SpellTarget.Enemy } && server.Spell("NO_SUCH_SPELL") is null, $"a spell says what it is ({strike?.Kind}, {strike?.Target}, {strike?.Category})");
        var ghoul = server.Mob("T4_MOB_ROAMING_UNDEAD_GHOUL");
        Check(ghoul is { Faction: MobFaction.Undead, Tier: 4 }, $"a mob type says what it is ({ghoul?.Faction}, T{ghoul?.Tier}, {ghoul?.Category})");
        Check(player.Zone.Cluster == player.Cluster && player.Zone == server.Zone(player.Cluster) && server.Zone("no such cluster") is null, $"a unit's zone ({player.Zone}, {player.Zone.Kind}, {player.Zone.Pvp})");
        var city = server.Zone("0000");
        Check(city is { Kind: ZoneKind.City, Color: ZoneColor.Blue }, $"cluster 0000 is a blue city ({city?.Type}: {city?.Kind}, {city?.Color})");
    }

    private const string WardenName = "TEST_WARDEN";
    private const string WardenBase = "T4_MOB_ROAMING_UNDEAD_SOLDIER_MINIBOSS";
    private const string WardenShock = "MOB_ROYAL_GUARD_MAGE_MAGICSHOCK"; // a ground spell of radius 2.5, cast in 1.3 s

    [ForId(WardenName)]
    private sealed class Warden : MobScript
    {
        public IGameplay Game_ => Game;
        public IServer Server_ => Server;
        public IHooks Hooks_ => Hooks;
        public MobType? Defined, Again, NoBase;
        public bool Drive, Hold;
        public int Thinks, Asked, Left;
        public LeaveCombatReason Why;

        protected override void OnLoad()
        {
            Defined = Server.DefineMob(WardenName, WardenBase, m =>
            {
                m.MaxHealth = 9000;
                m.AttackDamage = 300;
                m.RunSpeed = 8;
                m.NoRotation = true;
                m.Title = "The Warden";
            });
            Again = Server.DefineMob(WardenName, WardenBase);
            NoBase = Server.DefineMob("TEST_NOTHING", "NO_SUCH_MOB");
        }

        public void Do(Action action) => Timers.After(TimeSpan.Zero, action);

        public override void OnBeforeThink(BeforeThinkArgs e)
        {
            Thinks++;
            if (Drive) e.Cancel();
        }

        public override void OnBeforeLeaveCombat(BeforeLeaveCombatArgs e)
        {
            Asked++;
            Why = e.Reason;
            if (Hold) e.Cancel();
        }

        public override void OnLeaveCombat(MobLeaveCombatArgs e) => Left++;
    }

    private sealed class HeroHits : CombatScript
    {
        public readonly List<(DamageOrigin Origin, double Amount)> Hits = [];

        public override void OnBeforeDamage(BeforeDamageArgs e)
        {
            if (e.Target is not IPlayer) return;
            Hits.Add((e.Origin, e.Amount));
            e.Amount = 0; // the hero lives on
        }
    }

    private static void OwnBoss()
    {
        using var arena = new Arena(ai: true, spells: true);
        var world = arena.World;
        var (_, peer, me) = arena.Join("Hero", new InventoryItem("T4_MAIN_SWORD"));
        var (_, _, other) = arena.Join("Other", new InventoryItem("T4_MAIN_SWORD"));
        var warden = new Warden();
        var hits = new HeroHits();
        Check(world.Modules.Load("warden", warden) is null && world.Modules.Load("hits", hits) is null, "the in-process modules load");
        var (server, game) = (warden.Server_, warden.Game_);
        var hero = server.FindPlayer("Hero")!;
        var second = server.FindPlayer("Other")!;
        void In(Action action)
        {
            warden.Do(action);
            arena.Run(0.1);
        }
        (float X, float Y) Here() => (me.PositionAt(DateTime.UtcNow).X, me.PositionAt(DateTime.UtcNow).Y);

        // The type: its numbers, the base type's id for the clients, its name for the hooks.
        var plain = Client.Clusters!.Mobs.Find(WardenBase)!;
        Check(warden.Defined is { Name: WardenName, MaxHealth: 9000 } && warden.Again is null && warden.NoBase is null, $"defined once, a taken name and a missing base type refused ({warden.Defined?.MaxHealth})");
        Check(server.Mob(WardenName) is { Name: WardenName } && Client.Clusters.Mobs.All.All(m => m.Name != WardenName), "Server.Mob finds it, the client's list does not have it");
        var mark = peer.Events.Count;
        var (x, y) = Here();
        var boss = server.SpawnMob("0204", WardenName, x + 1, y)!;
        Check(boss is { Name: WardenName, MaxHealth: 9000, Health: 9000 }, $"it spawns with its numbers ({boss?.Name}, {boss?.MaxHealth})");
        var real = (Mob)world.Maps.Find("0204")!.Objects[boss.ObjectId];
        var shown = Since(peer, mark).First(e => e.Code == EventCodes.NewMob && L(e.P, 0) == boss.ObjectId);
        Check(Convert.ToInt32(shown.P[1]) == plain.Id && shown.P[6] is "The Warden" && F(shown.P, 14) == 9000, $"the clients get the base type's id, the title and the health ({shown.P[1]} of {plain.Id}, {shown.P[6]}, {F(shown.P, 14)})");
        var thinks = warden.Thinks;
        var fellow = server.SpawnMob("0204", WardenBase, x + 30, y)!;
        arena.Run(0.5);
        Check(warden.Thinks > thinks && fellow.Name == WardenBase && fellow.MaxHealth == plain.HitPointsMax, "[ForId] reaches it; the base type keeps its numbers");
        var made = warden.Thinks;
        fellow.Despawn(false);
        Check(server.SpawnBoss("0204", WardenName, x + 40, y) is { IsWorldBoss: true } oneOff && oneOff.Despawn(false), "SpawnBoss takes it");
        arena.Run(0.1);
        Check(warden.Thinks > made, "it thinks");

        // Driven by the module: a swing on order, with the type's damage and the time between swings.
        warden.Drive = true;
        Check(boss.Target is null && boss.Threat.Count == 0 && !boss.IsCasting && boss.CurrentSpell is null, "nothing yet");
        Check(!boss.Attack(hero) && boss.Target?.ObjectId == me.Id, "Attack takes the target; the first swing of a fight waits for its reaction time");
        arena.Run(2);
        Check(hits.Hits.Count == 0, "driven, it does not swing by itself");
        Check(boss.Attack(hero) && !boss.Attack(hero), "Attack swings; the next swing is not due yet");
        arena.Run(2.6); // 0.4 swings a second
        Check(hits.Hits.Count == 1 && hits.Hits[0] is { Origin: DamageOrigin.MeleeAutoAttack, Amount: > 90 }, $"one swing lands, by the type's 300 damage ({string.Join(", ", hits.Hits)})");
        Check(boss.Attack(hero), "due again");
        lock (world.Sync) other.Teleport(me.PositionAt(DateTime.UtcNow) + new Vector2(20, 0), 0, DateTime.UtcNow);
        arena.Run(2.6);
        Check(!boss.Attack(second) && !boss.InReach(second) && boss.InReach(hero), "out of reach: no swing");

        // The threat list.
        Check(boss.AddThreat(hero, 100) && boss.AddThreat(second, 5000), "threat added");
        var threat = boss.Threat;
        Check(threat.Count == 2 && threat[0].Player.Name == "Other" && threat[1].Player.Name == "Hero" && threat[0].Amount > threat[1].Amount, $"the threat list, the most first ({string.Join(", ", threat)})");

        // Walking and stopping.
        Check(boss.MoveTo(boss.X + 3, boss.Y) && boss.IsMoving && boss.Stop() && !boss.IsMoving && !boss.Stop(), "MoveTo, IsMoving, Stop");
        Check(boss.FaceTo(boss.X, boss.Y + 5), "FaceTo");

        // A cast with its own time, and what it is casting.
        hits.Hits.Clear();
        (x, y) = Here();
        Check(boss.CastSpell(WardenShock, null, x, y, castSeconds: 0.2) && boss.IsCasting && boss.CurrentSpell?.Name == WardenShock && !boss.Attack(hero), "casting: CurrentSpell says what, and no swing");
        arena.Run(0.6);
        Check(!boss.IsCasting && hits.Hits.Count == 1, $"0.2 s instead of the spell's 1.3 s ({hits.Hits.Count} hit, casting {boss.IsCasting})");

        // A spell at once, with no cast bar.
        hits.Hits.Clear();
        In(() => Check(game.CastSpell(boss, WardenShock, null, x, y), "Game.CastSpell for a mob"));
        Check(hits.Hits.Count == 1 && !boss.IsCasting, $"it hit at once ({hits.Hits.Count})");

        // A scripted spell on a mob's cast: the look of the spell, the effects of the module.
        hits.Hits.Clear();
        var scripted = new List<(AlbionLocal.Modules.IUnit Caster, int Tick)>();
        IHookRegistration? script = null;
        In(() => script = warden.Hooks_.Spells.Register(WardenShock, new CustomSpell { OnHit = c => scripted.Add((c.Caster, c.Tick)) }));
        Check(boss.CastSpell(WardenShock, null, x, y, castSeconds: 0.1), "the scripted spell is cast");
        arena.Run(0.5);
        Check(scripted.Count == 1 && scripted[0].Caster is IMob && hits.Hits.Count == 0, $"OnHit runs for the mob, and ReplaceEffects keeps the spell's own damage off ({scripted.Count}, {hits.Hits.Count} hits)");
        In(() => script!.Dispose());

        // OnBeforeHit for a mob's cast.
        var stopped = 0;
        IHookRegistration? before = null;
        In(() => before = warden.Hooks_.Spells.OnBeforeHit(WardenShock, e =>
        {
            if (e.Caster is IMob) stopped++;
            e.Cancel();
        }));
        Check(boss.CastSpell(WardenShock, null, x, y, castSeconds: 0.1), "cast again");
        arena.Run(0.5);
        Check(stopped == 1 && hits.Hits.Count == 0, $"OnBeforeHit cancels a mob's hit ({stopped}, {hits.Hits.Count} hits)");
        In(() => before!.Dispose());

        // Stats with what is on it.
        var armour = game.Stats(boss).ArmorResist;
        In(() => game.SetModifier(boss, "t", Stat.Armor, 500));
        Check(game.Stats(boss).ArmorResist == armour + 500, $"Game.Stats says the armour with its buffs ({armour} -> {game.Stats(boss).ArmorResist})");
        In(() => game.ClearModifier(boss, "t"));

        // A module area the clients see.
        mark = peer.Events.Count;
        IModuleArea? area = null;
        In(() => area = game.CreateArea(boss, x, y, 3, TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(1), _ => { }, visual: "FIREWALL"));
        var drawn = Since(peer, mark).FirstOrDefault(e => e.Code == EventCodes.NewSpellEffectArea);
        Check(area is not null && drawn.P is not null && Convert.ToInt32(drawn.P[4]) == Client.Spells!.IdOf("FIREWALL") && L(drawn.P, 1) == boss.ObjectId, "NewSpellEffectArea of the spell named, the mob its owner");
        arena.Run(1.5);
        Check(area!.Ended && Since(peer, mark).Any(e => e.Code == EventCodes.Leave && L(e.P, 0) == L(drawn.P, 0)), "at its end a Leave");

        // The fight stays until the module lets it go.
        warden.Drive = false;
        warden.Hold = true;
        lock (world.Sync)
        {
            me.Teleport(me.PositionAt(DateTime.UtcNow) + new Vector2(200, 0), 0, DateTime.UtcNow); // too far to catch in time
            other.Teleport(me.PositionAt(DateTime.UtcNow), 0, DateTime.UtcNow);
        }
        arena.Run(0.1);
        Check(boss.SetTarget(hero), "the fight goes on");
        arena.Run(6);
        Check(warden.Asked > 0 && warden.Left == 0 && boss.InCombat, $"OnBeforeLeaveCombat keeps it in ({warden.Asked} asked, {warden.Why}, left {warden.Left})");
        warden.Hold = false;
        arena.Run(0.5);
        Check(warden.Left == 1 && !boss.InCombat, $"let go, it leaves ({warden.Left}, {warden.Why})");

        // The module goes: the type and its mobs with it.
        world.Modules.Unload("warden", "test");
        arena.Run(0.1);
        Check(real.Removed && Client.Clusters.Mobs.Find(WardenName) is null, $"at the unload the type and its mob are gone (removed {real.Removed})");
        world.Modules.Shutdown();
    }

    private sealed class HitCounter : SpellScript
    {
        public IServer Server_ => Server;
        public readonly List<SpellHitArgs> Hits = [];

        public override void OnAfterHit(SpellHitArgs e) => Hits.Add(e);
    }

    private static void UnitsHitCount()
    {
        using var arena = new Arena(spells: true);
        var world = arena.World;
        var (session, _, me) = arena.Join("Caster", new InventoryItem("T4_MAIN_AXE") { Active = ["RENDINGSTRIKE", "AXESMASH", "AXETHROW"], Passive = [] },
            ("COMBAT_AXES_AXE", 100), ("COMBAT_AXES", 100), ("COMBAT_AXES_GREAT", 25));
        var ghoul = arena.Spawn("T4_MOB_ROAMING_UNDEAD_GHOUL", new Vector2(1.5f, 0));
        var counter = new HitCounter();
        Check(world.Modules.Load("counter", counter) is null, "the in-process module loads");
        Send(session, new CastStartRequest { wf = world.GameNow, dx = 0, d0 = Client.Spells!.IdOf("RENDINGSTRIKE"), d1 = ghoul.Id, d4 = 1 });
        arena.Run(2);
        Check(counter.Hits.Count > 0 && counter.Hits[^1] is { Spell.Name: "RENDINGSTRIKE", UnitsHit: 1, Caster: IPlayer }, $"a player's cast at a mob reached it ({string.Join(", ", counter.Hits.Select(h => $"{h.Spell}: {h.UnitsHit}"))})");

        const string Shock = "MOB_ROYAL_GUARD_MAGE_MAGICSHOCK"; // a ground spell of radius 2.5
        var mob = counter.Server_.Mobs("0204").Single(m => m.ObjectId == ghoul.Id);
        var where = me.PositionAt(DateTime.UtcNow) + new Vector2(6, 0);
        Check(mob.CastSpell(Shock, null, where.X, where.Y), "the mob aims at the ground, away from the player");
        arena.Run(3);
        Check(counter.Hits[^1] is { Spell.Name: Shock, UnitsHit: 0, Caster: IMob }, $"the player was not there ({counter.Hits[^1].Spell}: {counter.Hits[^1].UnitsHit})");
        Check(mob.CastSpell(Shock, null, me.PositionAt(DateTime.UtcNow).X, me.PositionAt(DateTime.UtcNow).Y), "the mob aims at the player");
        arena.Run(3);
        Check(counter.Hits[^1] is { Spell.Name: Shock, UnitsHit: 1 }, $"the player stood there ({counter.Hits[^1].Spell}: {counter.Hits[^1].UnitsHit})");
        world.Modules.Shutdown();
    }

    private sealed class Outcomes : CombatScript
    {
        public IGameplay Game_ => Game;
        public IServer Server_ => Server;
        public readonly List<HealthChangeArgs> Lost = [];
        public readonly List<AbsorbArgs> Absorbed = [];
        public readonly List<HitAvoidedArgs> Avoided = [];
        public Action<AbsorbArgs>? Before;

        public override void OnAfterDamage(HealthChangeArgs e) => Lost.Add(e);

        public override void OnBeforeAbsorb(AbsorbArgs e) => Before?.Invoke(e);

        public override void OnAfterAbsorb(AbsorbArgs e) => Absorbed.Add(e);

        public override void OnHitAvoided(HitAvoidedArgs e) => Avoided.Add(e);
    }

    private sealed class Reactions : SpellScript
    {
        public readonly List<SpellTriggerArgs> Seen = [];
        public bool Stop;

        public override void OnBeforeTrigger(SpellTriggerArgs e)
        {
            Seen.Add(e);
            if (Stop) e.Cancel();
        }
    }

    private sealed class MountHits : MountScript
    {
        public MountDamageArgs? Last;

        public override void OnCalcMountDamage(MountDamageArgs e) => Last = e;
    }

    private sealed class CancelsHits : CombatScript
    {
        public override void OnBeforeDamage(BeforeDamageArgs e) => e.Cancel();
    }

    private static void HitOutcomes()
    {
        using var arena = new Arena(config: new AlbionLocal.Server.ServerConfig { JoinByCharacterId = true }, pvp: Yellow);
        arena.Setup = ch => ch.Flag = Pvp.Red;
        var world = arena.World;
        var (session, _, a) = arena.Join("Striker", new InventoryItem("T4_MAIN_AXE") { Active = ["RENDINGSTRIKE", "AXESMASH", "AXETHROW"], Passive = [] },
            ("COMBAT_AXES_AXE", 100), ("COMBAT_AXES", 100), ("COMBAT_AXES_GREAT", 25));
        var (_, _, b) = arena.Join("Struck", new InventoryItem("T4_MAIN_SWORD"));
        var ghoul = arena.Spawn("T4_MOB_ROAMING_UNDEAD_GHOUL", new Vector2(1.5f, 0));
        var seen = new Outcomes();
        var reactions = new Reactions();
        var mounts = new MountHits();
        Check(world.Modules.Load("outcomes", seen) is null && world.Modules.Load("reactions", reactions) is null && world.Modules.Load("mounts", mounts) is null, "the in-process modules load");
        var game = seen.Game_;
        var struck = seen.Server_.FindPlayer("Struck")!;
        var mob = seen.Server_.Mobs("0204").Single(m => m.ObjectId == ghoul.Id);
        void Hit(HitOrigin origin = HitOrigin.Direct)
        {
            lock (world.Sync) Pvp.Damage(a, b, 40, EffectType.Physical, origin, -1, world.GameNow);
        }

        // The health: what caused it.
        Hit();
        Check(seen.Lost[^1] is { Origin: DamageOrigin.Direct, IsDirectHit: true }, "OnAfterDamage says a direct hit");
        Hit(HitOrigin.OverTime);
        Check(seen.Lost[^1] is { Origin: DamageOrigin.OverTime, IsDirectHit: false }, "and a tick over time");

        // A shield: all of a small hit, no health lost, and it says who hit what.
        Check(game.ApplyBuff(struck, "ENERGY_BARRIER_SHIELD", seconds: 600), "the shield goes on");
        arena.Run(0.1);
        var lost = seen.Lost.Count;
        Hit();
        var taken = seen.Absorbed.Single();
        Check(seen.Lost.Count == lost && taken is { Shield.Spell.Name: "ENERGY_BARRIER_SHIELD", Origin: DamageOrigin.Direct, IsDirectHit: true, IsBroken: false, DamageLeft: 0, Spell: null } && taken.Amount > 0
              && taken.Target.ObjectId == b.Id && taken.Attacker?.ObjectId == a.Id, $"all on the shield: no health lost ({taken.Amount} taken, {taken.ShieldLeft} left, {taken.DamageLeft} past it)");
        var holds = taken.ShieldLeft;

        // A module lets a hit through, or halves what the shield takes.
        seen.Before = e => e.Cancel();
        Hit();
        Check(seen.Absorbed.Count == 1 && seen.Lost.Count == lost + 1, "Cancel in OnBeforeAbsorb: through the shield, on to the health");
        seen.Before = e => e.Amount /= 2;
        Hit();
        Check(seen.Absorbed.Count == 2 && Math.Abs(seen.Absorbed[^1].Amount - taken.Amount / 2) < 1 && seen.Absorbed[^1].DamageLeft > 0 && seen.Lost.Count == lost + 2 && seen.Absorbed[^1].ShieldLeft < holds,
              $"a smaller Amount: the shield takes less and the rest goes on ({seen.Absorbed[^1].Amount} of {taken.Amount}, {seen.Absorbed[^1].DamageLeft} past it)");
        seen.Before = null;

        // The hit that uses it up, a mob's.
        lock (world.Sync) Combat.DamagePlayer(ghoul, b, seen.Absorbed[^1].ShieldLeft + 10, EffectType.Physical, HitOrigin.MeleeAutoAttack, -1, world.GameNow);
        Check(seen.Absorbed[^1] is { IsBroken: true, Origin: DamageOrigin.MeleeAutoAttack, Attacker: IMob },
              $"a mob's hit breaks it ({seen.Absorbed[^1].DamageLeft} past it)");

        // A player's hit on a mob's shield.
        Check(game.ApplyBuff(mob, "ENERGY_BARRIER_SHIELD", seconds: 600), "the shield goes on the mob");
        arena.Run(0.1);
        lock (world.Sync) Combat.Damage(a, ghoul, 5, EffectType.Physical, HitOrigin.MeleeAutoAttack, -1, world.GameNow);
        Check(seen.Absorbed[^1] is { Target: IMob, Attacker: IPlayer, Origin: DamageOrigin.MeleeAutoAttack }, "a player's hit on a mob's shield");
        game.RemoveBuff(mob, "ENERGY_BARRIER_SHIELD");

        // DODGE: invincible, on every path.
        Check(game.ApplyBuff(struck, "DODGE", seconds: 600) && game.ApplyBuff(mob, "DODGE", seconds: 600), "DODGE goes on");
        arena.Run(0.1);
        Hit();
        Check(seen.Avoided[^1] is { Reason: HitAvoidReason.Invincible, ProtectedBy.Spell.Name: "DODGE", IsDirectHit: true, Attacker: IPlayer, Target: IPlayer, Amount: > 0 }, $"DODGE against a player ({seen.Avoided[^1].Reason}, {seen.Avoided[^1].ProtectedBy?.Spell}, {seen.Avoided[^1].Detail})");
        lock (world.Sync) Combat.DamagePlayer(ghoul, b, 40, EffectType.Physical, HitOrigin.MeleeAutoAttack, -1, world.GameNow);
        Check(seen.Avoided[^1] is { Reason: HitAvoidReason.Invincible, ProtectedBy.Spell.Name: "DODGE", Attacker: IMob }, "against a mob");
        lock (world.Sync) Combat.Damage(a, ghoul, 5, EffectType.Physical, HitOrigin.MeleeAutoAttack, -1, world.GameNow);
        Check(seen.Avoided[^1] is { Reason: HitAvoidReason.Invincible, Target: IMob, Attacker: IPlayer }, "on a mob");
        game.RemoveBuff(struck, "DODGE");
        game.RemoveBuff(mob, "DODGE");

        // A mob running home, and no event when nothing protects the target.
        var avoided = seen.Avoided.Count;
        lock (world.Sync) Combat.Damage(a, ghoul, 5, EffectType.Physical, HitOrigin.MeleeAutoAttack, -1, world.GameNow);
        Check(seen.Avoided.Count == avoided, "a hit that lands is not avoided");
        typeof(Mob).GetProperty(nameof(Mob.Immune))!.SetValue(ghoul, true);
        lock (world.Sync) Combat.Damage(a, ghoul, 5, EffectType.Physical, HitOrigin.MeleeAutoAttack, -1, world.GameNow);
        typeof(Mob).GetProperty(nameof(Mob.Immune))!.SetValue(ghoul, false);
        Check(seen.Avoided[^1].Reason == HitAvoidReason.Evading, $"a mob running home ({seen.Avoided[^1].Reason})");

        // A spell immunity keeps a real cast off, once.
        Check(game.ApplyBuff(mob, "MOB_RETURNHOME", seconds: 600), "a spell immunity goes on the mob");
        avoided = seen.Avoided.Count;
        Send(session, new CastStartRequest { wf = world.GameNow, dx = 0, d0 = Client.Spells!.IdOf("RENDINGSTRIKE"), d1 = ghoul.Id, d4 = 1 });
        arena.Run(2);
        Check(seen.Avoided.Count == avoided + 1 && seen.Avoided[^1] is { Reason: HitAvoidReason.SpellImmunity, Spell.Name: "RENDINGSTRIKE", ProtectedBy.Spell.Name: "MOB_RETURNHOME" },
              $"a cast on it ({string.Join(", ", seen.Avoided.Skip(avoided).Select(x => $"{x.Reason} {x.Spell} {x.ProtectedBy?.Spell}"))})");
        game.RemoveBuff(mob, "MOB_RETURNHOME");

        // Another module's cancel.
        Check(world.Modules.Load("canceller", new CancelsHits()) is null, "the cancelling module loads");
        Hit();
        Check(seen.Avoided[^1] is { Reason: HitAvoidReason.Cancelled, ProtectedBy: null }, $"a module's cancel ({seen.Avoided[^1].Reason})");
        world.Modules.Unload("canceller", "test");

        // A reaction: ENRAGE on a hit taken, and stopped.
        Check(game.ApplyBuff(struck, "ENRAGE", seconds: 600), "ENRAGE goes on");
        arena.Run(0.1);
        Hit();
        Check(reactions.Seen.Count > 0 && reactions.Seen[^1] is { Event: SpellTriggerEvent.ReceiveDamage or SpellTriggerEvent.ReceiveDirectDamage, Effect.Name: "ENRAGE", Spell.Name: "ENRAGE_BUFF", IsPassive: false } && reactions.Seen[^1].Other?.ObjectId == a.Id
              && game.FindBuff(struck, "ENRAGE_BUFF") is not null, $"OnBeforeTrigger on the hit taken ({reactions.Seen.Count}), and it ran");
        game.RemoveBuff(struck, "ENRAGE_BUFF");
        reactions.Stop = true;
        Hit();
        Check(game.FindBuff(struck, "ENRAGE_BUFF") is null, "Cancel stops it");

        // The mount's bar.
        lock (world.Sync) Mounts.Damage(b, a.Id, 1, EffectType.Physical, HitOrigin.Area, -1, world.GameNow);
        Check(mounts.Last is { Origin: DamageOrigin.Area, IsDirectHit: true, Spell: null }, "OnCalcMountDamage says what caused it");
        world.Modules.Shutdown();
    }

    private sealed class HitWatcher : CombatScript
    {
        public readonly List<BeforeDamageArgs> Hits = [];

        public override void OnBeforeDamage(BeforeDamageArgs e) => Hits.Add(e);
    }

    private static void DirectHitAndRoll()
    {
        var direct = Enum.GetValues<DamageOrigin>().Where(o => new BeforeDamageArgs(null!, null!, DamageKind.PlayerToMob, o, false, null, 1, 1).IsDirectHit);
        Check(direct.SequenceEqual([DamageOrigin.MeleeAutoAttack, DamageOrigin.RangedAutoAttack, DamageOrigin.Direct, DamageOrigin.Area]), $"the origins that are a hit ({string.Join(", ", direct)})");

        // The same on real hits: an auto-attack, then a spell that also ticks over time.
        var (arena, session, _, _, ghoul) = Fight();
        using var _ = arena;
        var world = arena.World;
        var watcher = new HitWatcher();
        Check(world.Modules.Load("watcher", watcher) is null, "the in-process module loads");
        Send(session, new AttackStartRequest { wf = world.GameNow, co = ghoul.Id, cp = 0 });
        arena.Run(2);
        Send(session, new CastStartRequest { wf = world.GameNow, dx = 0, d0 = Client.Spells!.IdOf("RENDINGSTRIKE"), d1 = ghoul.Id, d4 = 1 });
        arena.Run(6);
        var seen = string.Join(", ", watcher.Hits.Select(h => $"{h.Origin}/{h.Spell?.Name}"));
        Check(watcher.Hits.Any(h => h is { Origin: DamageOrigin.MeleeAutoAttack, IsDirectHit: true, Spell: null }), $"an auto-attack is a hit ({seen})");
        Check(watcher.Hits.Any(h => h is { Origin: DamageOrigin.Direct, IsDirectHit: true, Spell: not null }), $"a spell's hit is a hit ({seen})");
        Check(watcher.Hits.Any(h => h is { Origin: DamageOrigin.OverTime, IsDirectHit: false }), $"its tick over time is not ({seen})");

        var chance = Enumerable.Range(0, 10000).Count(_ => Roll.Chance(0.25));
        Check(chance is > 2200 and < 2800, $"Roll.Chance(0.25) about a quarter of the time ({chance} of 10000)");
        Check(!Enumerable.Range(0, 1000).Any(_ => Roll.Chance(0)) && Enumerable.Range(0, 1000).All(_ => Roll.Chance(1)), "Roll.Chance(0) never, Roll.Chance(1) always");
        string[] debuffs = ["A", "B", "C"];
        Check(Enumerable.Range(0, 1000).Select(_ => Roll.Pick(debuffs)).ToHashSet().SetEquals(debuffs) && Roll.Pick(new List<int> { 7 }) == 7, "Roll.Pick takes any list and reaches all of it");
        var empty = false;
        try { Roll.Pick(Array.Empty<string>()); }
        catch (ArgumentException e) { empty = e.Message.Contains("empty", StringComparison.Ordinal); }
        Check(empty, "Roll.Pick of an empty list says so");
    }

    private static void FameSilverMaps()
    {
        using var arena = new Arena();
        var world = arena.World;
        var ledger = new Ledger();
        var maps = new MapLog();
        Check(world.Modules.Load("ledger", ledger) is null && world.Modules.Load("maps", maps) is null, "the in-process modules load");
        var (session, _, me) = arena.Join("Banker", new InventoryItem("T4_MAIN_SWORD"));
        Check(maps.Log_ is ["+0204"], $"Maps.OnLoaded when the Join builds the map (got {string.Join(", ", maps.Log_)})");
        var player = ledger.Game_.FindPlayer("Banker")!;
        var silver = player.Silver;
        Check(player.GiveSilver(50) && ledger.Delta == 50 && ledger.Total == silver + 50, $"+50 silver (delta {ledger.Delta}, total {ledger.Total})");
        Check(player.TakeSilver(20) && ledger.Delta == -20 && ledger.Total == silver + 30, $"-20 silver (delta {ledger.Delta}, total {ledger.Total})");
        Check(player.GiveFame(120) && ledger.Fame == 120 && ledger.Source == AlbionLocal.Modules.FameSource.Kill, $"120 fame in whole units (got {ledger.Fame}, {ledger.Source})");
        session.OnDisconnected("test");
        Check(world.Maps.Unload(world.Maps.Find("0204")!, force: true) && maps.Log_ is ["+0204", "-0204"], $"Maps.OnUnloaded when it is dropped (got {string.Join(", ", maps.Log_)})");
        world.Modules.Shutdown();
    }

    private sealed class LootRules : ItemScript
    {
        [ForId("T4_MAIN_SWORD")]
        [ForId("T4_WOOD")]
        public override void OnBeforeLootTake(BeforeLootTakeArgs e) => e.Cancel("That sword is cursed");
    }

    private sealed class NoRespawn : MobScript
    {
        public ItemCategory? First;
        public bool AddedUnknown = true;

        public override void OnLootRolled(LootArgs e)
        {
            First = e.Items[0].Item.Category;
            AddedUnknown = e.Items.Add("T4_NO_SUCH_ITEM");
            e.Items.Add("T4_ROCK", 2);
        }

        public int Asked;

        public override void OnBeforeRespawn(BeforeRespawnArgs e)
        {
            Asked++;
            e.Cancel();
        }
    }

    private static void LootAndRespawn()
    {
        using var arena = new Arena(_ => [("T4_ORE", 5), ("T4_MAIN_SWORD", 1), ("T4_WOOD", 5)]);
        var world = arena.World;
        var rules = new LootRules();
        var respawn = new NoRespawn();
        Check(world.Modules.Load("rules", rules) is null && world.Modules.Load("respawn", respawn) is null, "the in-process modules load");
        var (session, peer, me) = arena.Join("Looter", new InventoryItem("T4_MAIN_AXE"));
        var ghoul = arena.Spawn("T4_MOB_ROAMING_UNDEAD_GHOUL", new Vector2(1.5f, 0));
        ghoul.Health.Set(1, world.GameNow);
        Send(session, new AttackStartRequest { wf = world.GameNow, co = ghoul.Id, cp = 0 });
        arena.Run(0.5);
        var bag = world.Maps.Find("0204")!.Objects.Values.OfType<LootBag>().Single();
        Send(session, new RegisterToObjectRequest { zg = bag.Id });
        Check(respawn.First == ItemCategory.Resource && !respawn.AddedUnknown && bag.Items.Items.Any(i => i is { Template.Name: "T4_ROCK", Count: 2 }),
              $"OnLootRolled reads the rolled items as types and adds by name; an unknown name is refused (first {respawn.First}, unknown added {respawn.AddedUnknown})");
        var ore = bag.Items.Items.Single(i => i.Template.Name == "T4_ORE");
        var sword = bag.Items.Items.Single(i => i.Template.Name == "T4_MAIN_SWORD");
        var mark = peer.Events.Count;
        Send(session, new InventoryMoveGivenItemsRequest { ro = bag.Items.Id, rp = 1, rq = me.Inventory.Bag.Id, rr = 5, rs = [ore.Id, sword.Id], rt = [5, 1] });
        var lines = Since(peer, mark).Where(e => e.Code == EventCodes.SystemMessage).Select(e => e.P[0] as string).ToList();
        Check(bag.Items.Contains(sword) && !bag.Items.Contains(ore) && lines.Contains("That sword is cursed"),
              $"the ore is taken, the sword stays, the reason is told (lines: {string.Join(" | ", lines)})");

        var wood = bag.Items.Items.Single(i => i.Template.Name == "T4_WOOD");
        Send(session, new InventorySplitStackIntoRequest { si = bag.Items.Id, sh = Enumerable.Range(0, bag.Items.Size).First(i => bag.Items[i] == wood), sj = me.Inventory.Bag.Id, sk = 6, sn = 2 });
        Check(wood.Count == 5 && bag.Items.Contains(wood), $"a split of a refused stack takes nothing (count {wood.Count})");

        var pointMob = world.Maps.Find("0204")!.Objects.Values.OfType<Mob>().First(m => m.Slot is not null);
        var slot = pointMob.Slot!;
        world.MobRespawnScale = 0.0001f;
        lock (world.Sync) AlbionLocal.Server.SpawnManager.Despawn(pointMob, world);
        arena.Run(0.2);
        Check(respawn.Asked >= 1 && slot.Mob is null && slot.RespawnAt is not null, $"OnBeforeRespawn Cancel: no mob, the point waits again (asked {respawn.Asked}, mob {slot.Mob}, at {slot.RespawnAt})");
        world.Modules.Shutdown();
    }

    private static void Check(bool cond, string what) => AlbionTests.Check(cond, what);

    private static (Arena Arena, WorldSession Session, FakePeer Peer, Player Me, Mob Ghoul) Fight()
    {
        var arena = new Arena();
        arena.Setup = c => c.Equipment[3] = new InventoryItem("T4_ARMOR_PLATE_SET3");
        var (session, peer, me) = arena.Join("Hooked", new InventoryItem("T4_MAIN_AXE") { Active = ["RENDINGSTRIKE", "AXESMASH", "AXETHROW"], Passive = [] },
            ("COMBAT_AXES_AXE", 100), ("COMBAT_AXES", 100), ("COMBAT_AXES_GREAT", 25));
        arena.Setup = null;
        var ghoul = arena.Spawn("T4_MOB_ROAMING_UNDEAD_GHOUL", new Vector2(1.5f, 0));
        return (arena, session, peer, me, ghoul);
    }

    private sealed class SpellRules : AlbionLocal.Modules.Module
    {
        public double Seen;
        public int Cancels;
    
        protected override void OnLoad()
        {
            Hooks.Spells.OnBeforeCast("RENDINGSTRIKE", a => a.Cancel("Rending Strike is forbidden here"));
            Hooks.Spells.OnCancel(_ => Cancels++);
            Hooks.Spells.OnBeforeDamage("AXESMASH", a =>
            {
                if (a.IsHeal) return;
                Seen = a.Amount;
                a.Amount *= 1.5;
            });
        }
    }

    private static void CancelAndModify()
    {
        var (arena, session, peer, me, ghoul) = Fight();
        using var _ = arena;
        var world = arena.World;
        var rules = new SpellRules();
        Check(world.Modules.Load("rules", rules) is null, "the in-process module loads");
        var spells = Client.Spells!;
        short rending = spells.IdOf("RENDINGSTRIKE"), axesmash = spells.IdOf("AXESMASH");

        var (responses, mark) = (peer.Responses.Count, peer.Events.Count);
        Send(session, new CastStartRequest { wf = world.GameNow, dx = 0, d0 = rending, d1 = ghoul.Id, d4 = 1 });
        var lines = Since(peer, mark).Where(e => e.Code == EventCodes.SystemMessage).Select(e => e.P[0] as string).ToList();
        Check(peer.Responses.Count == responses + 1 && peer.Responses[^1].ReturnCode == WorldSession.CastRefused && me.Combat.Cast is null
              && lines.Any(l => l?.Contains("forbidden", StringComparison.Ordinal) == true) && rules.Cancels == 0,
              $"RENDINGSTRIKE refused by the module: the CastRefused response, the reason as a system line, nothing under way (lines: {string.Join(" | ", lines)})");

        mark = peer.Events.Count;
        var before = ghoul.Health.At(world.GameNow);
        Send(session, new CastStartRequest { wf = world.GameNow, dx = 1, d0 = axesmash, d1 = ghoul.Id, d4 = 2 });
        arena.Run(0.5);
        var hit = Since(peer, mark).FirstOrDefault(e => e.Code == EventCodes.HealthUpdate && L(e.P, 0) == ghoul.Id && Convert.ToInt16(e.P[7]) == axesmash).P;
        var expected = (float)Math.Round(rules.Seen * 1.5);
        Check(rules.Seen > 150 && hit is not null && F(hit, 2) == -expected && Math.Abs(ghoul.Health.At(world.GameNow) - (before - expected)) < 0.5,
              $"AXESMASH's hit {rules.Seen:0.#} × 1.5 = {expected} in the HealthUpdate and on the ghoul (got {(hit is null ? "none" : F(hit, 2))})");
        world.Modules.Shutdown();
    }

    private sealed class NoHits : CombatScript
    {
        public int Before, After;
        public bool Lethal;

        public override void OnBeforeDamage(BeforeDamageArgs e)
        {
            Before++;
            Lethal |= e.IsLethal;
            e.Cancel();
        }

        public override void OnAfterDamage(HealthChangeArgs e) => After++;
    }

    private sealed class HalfHits : CombatScript
    {
        public double Seen, Lost;

        public override void OnBeforeDamage(BeforeDamageArgs e)
        {
            Seen = e.Amount;
            e.Amount /= 2;
        }

        public override void OnAfterDamage(HealthChangeArgs e) => Lost = e.Amount;
    }

    /// <summary>A cancelled hit leaves no trace: no damage, no combat, no target. Another module's hook then halves a hit and sees what it did.</summary>
    private static void CancelDamage()
    {
        var (arena, session, peer, me, ghoul) = Fight();
        using var _ = arena;
        var world = arena.World;
        var noHits = new NoHits();
        Check(world.Modules.Load("no-hits", noHits) is null, "the in-process module loads");
        var axesmash = Client.Spells!.IdOf("AXESMASH");
        var mark = peer.Events.Count;
        var before = ghoul.Health.At(world.GameNow);
        Send(session, new CastStartRequest { wf = world.GameNow, dx = 1, d0 = axesmash, d1 = ghoul.Id, d4 = 1 });
        arena.Run(0.5);
        Check(noHits.Before >= 1 && noHits.After == 0 && ghoul.Health.At(world.GameNow) == before, $"the hit is cancelled: the ghoul keeps its health (before-hooks {noHits.Before}, after-hooks {noHits.After})");
        Check(!Since(peer, mark).Any(e => e.Code == EventCodes.HealthUpdate && L(e.P, 0) == ghoul.Id) && ghoul.TargetId == 0 && !me.Combat.InCombat,
              $"no HealthUpdate, the ghoul has no target and the player is not in combat (target {ghoul.TargetId}, in combat {me.Combat.InCombat})");
        world.Modules.Unload("no-hits", "test");

        var half = new HalfHits();
        Check(world.Modules.Load("half", half) is null, "the second module loads");
        arena.Run(40); // its cooldown
        before = ghoul.Health.At(world.GameNow);
        Send(session, new CastStartRequest { wf = world.GameNow, dx = 1, d0 = axesmash, d1 = ghoul.Id, d4 = 2 });
        arena.Run(0.5);
        Check(half.Seen > 0 && Math.Abs(half.Lost - Math.Round(half.Seen / 2)) <= 1 && ghoul.Health.At(world.GameNow) < before,
              $"a changed Amount is what lands: {half.Seen:0.#} / 2, the ghoul lost {half.Lost:0.#}");
        world.Modules.Shutdown();
    }

    private static readonly AlbionLocal.Server.Data.ClusterPvp Yellow = new(AlbionLocal.Server.Data.PvpRule.PvpAllowed, PlayersAreLethal: false);

    private sealed class PvpRules : CombatScript
    {
        public bool Cancelling = true;
        public double Set;
        public int Before;
        public double Own = -1;

        public override void OnBeforeDamage(BeforeDamageArgs e)
        {
            Before++;
            if (Cancelling) e.Cancel();
            else if (Set > 0) e.Amount = Set;
        }

        public IMob? Spawned;

        public override void OnAfterDamage(HealthChangeArgs e)
        {
            if (Own >= 0) return;
            Own = Game.Damage(e.Source!, e.Unit, 1);
            Spawned = Game.Summon(e.Unit, "T4_MOB_ROAMING_UNDEAD_GHOUL", e.Unit.X, e.Unit.Y);
        }
    }

    private static void CancelPvp()
    {
        using var arena = new Arena(config: new AlbionLocal.Server.ServerConfig { JoinByCharacterId = true }, pvp: Yellow);
        arena.Setup = ch => ch.Flag = Pvp.Red;
        var world = arena.World;
        var (_, _, a) = arena.Join("Striker", new InventoryItem("T4_MAIN_SWORD"));
        var (_, bPeer, b) = arena.Join("Struck", new InventoryItem("T4_MAIN_SWORD"));
        var rules = new PvpRules();
        Check(world.Modules.Load("pvp-rules", rules) is null, "the in-process module loads");

        var before = b.Health.At(world.GameNow);
        var mark = bPeer.Events.Count;
        var castHit = Combat.CastHit(b.Id, a.Id, 1, 1);
        lock (world.Sync) Pvp.Damage(a, b, 100, EffectType.Physical, HitOrigin.Direct, -1, world.GameNow, castHit);
        Check(rules.Before == 1 && b.Health.At(world.GameNow) == before && !a.Combat.InCombat && !b.Combat.InCombat,
              $"cancelled: no damage, neither is in combat, (health {b.Health.At(world.GameNow)}, in combat {a.Combat.InCombat}/{b.Combat.InCombat})");
        Check(Since(bPeer, mark).Any(e => e.Code == EventCodes.CastHit), "the CastHit still goes out, without an exception");

        rules.Cancelling = false;
        rules.Set = 37;
        mark = bPeer.Events.Count;
        lock (world.Sync) Pvp.Damage(a, b, 100, EffectType.Physical, HitOrigin.Direct, -1, world.GameNow);
        var lost = -Since(bPeer, mark).Where(e => e.Code == EventCodes.HealthUpdate && L(e.P, 0) == b.Id).Sum(e => F(e.P, 2));
        Check(lost == 37, $"the amount a handler sets is what lands (lost {lost})");
        Check(rules.Own == 0, $"Game.Damage inside a hook returns 0: it waits (got {rules.Own})");
        Check(rules.Spawned is null, "no spawning inside a damage hook");
        var health = b.Health.At(world.GameNow);
        arena.Run(0.2);
        Check(b.Health.At(world.GameNow) < health + 1 && rules.Before >= 3, $"at the next tick it lands, through the hook again (before-hooks {rules.Before})");
        world.Modules.Shutdown();
    }

    private sealed class Looper : CombatScript
    {
        public int Runs;

        public override void OnBeforeDamage(BeforeDamageArgs e)
        {
            Runs++;
            AlbionLocal.Server.Systems.Combat.DamagePlayer(AlbionLocal.Server.Modules.ModuleCore.Mob((IMob)e.Attacker), AlbionLocal.Server.Modules.ModuleCore.Player((IPlayer)e.Target), 1, EffectType.Physical, HitOrigin.Direct, -1, AlbionLocal.Server.Modules.ModuleCore.World(Context).GameNow);
        }
    }

    private static void HookLoop()
    {
        using var arena = new Arena(config: new AlbionLocal.Server.ServerConfig { JoinByCharacterId = true, Modules = new AlbionLocal.Server.ModuleOptions { MaxErrors = 1 } });
        var world = arena.World;
        var (_, _, me) = arena.Join("Looped", new InventoryItem("T4_MAIN_SWORD"));
        var ghoul = arena.Spawn("T4_MOB_ROAMING_UNDEAD_GHOUL", new Vector2(1.5f, 0));
        var looper = new Looper();
        Check(world.Modules.Load("looper", looper) is null, "the in-process module loads");
        lock (world.Sync) Combat.DamagePlayer(ghoul, me, 1, EffectType.Physical, HitOrigin.Direct, -1, world.GameNow);
        Check(looper.Runs <= Depth && looper.Runs > 1, $"the loop is cut (ran {looper.Runs} times)");
        arena.Run(0.2);
        Check(!world.Modules.IsLoaded("looper"), $"the module that looped is disabled (state: {world.Modules.StateOf("looper")})");
        world.Modules.Shutdown();
    }

    private const int Depth = 16;

    private sealed class Counter
    {
        public int N;
    }

    private sealed class Scripted : AlbionLocal.Modules.Module
    {
        public int Casts, Hits, Applied, TimerRuns;
        public string? Target;
    
        protected override void OnLoad()
        {
            Hooks.Spells.Register("AXESMASH", new CustomSpell
            {
                OnCast = c =>
                {
                    Casts++;
                    c.Caster.GetData("casts", () => new Counter()).N++;
                    c.Caster.AddTimer(TimeSpan.FromMilliseconds(500), 2, _ => TimerRuns++, "echo");
                },
                OnHit = c =>
                {
                    Hits++;
                    Target = c.Target?.Name;
                    c.Game.ApplyBuff(c.Caster, "AXESMASH_EFFECT", c.Caster, seconds: 3);
                },
            });
            Hooks.Buffs.OnApply("AXESMASH_EFFECT", a => Applied++);
        }
    }

    private static void ScriptedSpell()
    {
        var (arena, session, peer, me, ghoul) = Fight();
        using var _ = arena;
        var world = arena.World;
        var script = new Scripted();
        Check(world.Modules.Load("script", script) is null, "the in-process module loads");
        var axesmash = Client.Spells!.IdOf("AXESMASH");
        var mark = peer.Events.Count;
        var before = ghoul.Health.At(world.GameNow);
        Send(session, new CastStartRequest { wf = world.GameNow, dx = 1, d0 = axesmash, d1 = ghoul.Id, d4 = 1 });
        arena.Run(0.5);
        var events = Since(peer, mark);
        var list = events.LastOrDefault(e => e.Code == EventCodes.ActiveSpellEffectsUpdate && L(e.P, 0) == me.Id).P;
        Check(script.Casts == 1 && script.Hits == 1 && script.Applied == 1 && script.Target == ghoul.Template.Name
              && events.Any(e => e.Code == EventCodes.CastFinished) && ghoul.Health.At(world.GameNow) == before
              && !events.Any(e => e.Code == EventCodes.HealthUpdate && L(e.P, 0) == ghoul.Id),
              $"OnCast and OnHit ran once at the ghoul, CastFinished still went out, the spells.xml damage did not (casts {script.Casts}, hits {script.Hits}, applied {script.Applied}, target {script.Target})");
        Check(list is not null && list[8] is int[] durations && durations.Length > 0 && durations.All(d => d == 3000)
              && me.Effects.Spells.Any(s => s.Spell.Name == "AXESMASH_EFFECT" && s.Caster == me.Id),
              $"ApplyBuff: AXESMASH_EFFECT on the caster with its durations 3000 ms in the list (got {(list is null ? "no list" : Show([(EventCodes.ActiveSpellEffectsUpdate, list)]))})");
        arena.Run(1.6);
        Check(script.TimerRuns == 2, $"the entity timer ran its 2 shots (ran {script.TimerRuns})");
        arena.Run(4);
        Check(!me.Effects.Spells.Any(s => s.Spell.Name == "AXESMASH_EFFECT" && s.Start < world.GameNow - SpellCasts.Seconds(4)), "the 3 s buff has run out");
        world.Modules.Shutdown();
    }

    private sealed class MountRules : AlbionLocal.Modules.Module
    {
        public int Refusals, Mounted, LoginEquips;
        public string? Ridden;
    
        protected override void OnLoad()
        {
            Hooks.Items.OnEquip("T5_MOUNT_OX", a =>
            {
                if (a.AtLogin && a.Slot == EquipSlot.Mount) LoginEquips++;
            });
            Hooks.Mounts.OnBeforeMount(a =>
            {
                if (Refusals++ == 0) a.Cancel("Not yet");
            });
            Hooks.Mounts.OnMounted("T5_MOUNT_OX", a =>
            {
                Mounted++;
                Ridden = a.Mount;
                a.GallopBuff = null;
            });
        }
    }

    private static void MountHooks()
    {
        using var arena = new Arena();
        var world = arena.World;
        var rules = new MountRules();
        Check(world.Modules.Load("rules", rules) is null, "the in-process module loads");
        arena.Setup = c => c.Equipment[Mounts.MountSlot] = new InventoryItem("T5_MOUNT_OX");
        var (session, peer, me) = arena.Join("Rider", new InventoryItem("T4_MAIN_SWORD"));
        arena.Setup = null;
        Check(rules.LoginEquips == 1, $"Items.OnEquip(\"T5_MOUNT_OX\") ran at the login for slot 7 (got {rules.LoginEquips})");
        var responses = peer.Responses.Count;
        Send(session, new MountRequest { wf = world.GameNow, v3 = Mounts.ItemOf(me)!.Id, v4 = true });
        Check(peer.Responses.Count == responses + 1 && peer.Responses[^1].ReturnCode == Mounts.Refused && me.Mount.State == MountState.None,
              "the first mount is refused by the module: the Mount refusal code, no mount bar");
        Send(session, new MountRequest { wf = world.GameNow, v3 = Mounts.ItemOf(me)!.Id, v4 = true });
        arena.Run(3.1);
        Check(me.IsMounted && rules.Mounted == 1 && rules.Ridden == "T5_MOUNT_OX" && me.Mount.Speed is null,
              $"the second mounts: OnMounted for the ox, and its GallopBuff null leaves no gallop (mounted {me.IsMounted}, hooks {rules.Mounted}, speed {me.Mount.Speed?.Spell.Name ?? "none"})");
        world.Modules.Shutdown();
    }
}
