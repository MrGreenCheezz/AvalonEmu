using System.Numerics;
using AlbionLocal.Photon.Serialization;
using AlbionLocal.Server;
using AlbionLocal.Server.Bots;
using AlbionLocal.Server.Content;
using AlbionLocal.Server.Entities;
using AlbionLocal.Server.Data;
using AlbionLocal.Server.Game;
using AlbionLocal.Server.Network;
using AlbionLocal.Server.Protocol;
using AlbionLocal.Server.Systems;
using static AlbionLocal.SelfTest.AlbionTests;
using static AlbionLocal.SelfTest.CombatTest;
using Field = AlbionLocal.SelfTest.BotTest.Field;

namespace AlbionLocal.SelfTest;

/// <summary>
/// StageM bots (analysis/work/stageM/bots/NOTES.md, the user's reports 3-6): an armed gatherer draws its weapon when attacked and casts
/// its spells, a bot's attack spell goes before its buff, usableincombat="false" (OUTOFCOMBATHEAL) is refused in a fight, the bag's
/// MinItems / DeathGearDropChance / start carry / tier-resolved PatrolExtra, and the inspect window (GetCharacterEquipment, op 143) of a
/// bot, a human and an unknown Guid. Thetford (0000) under a yellow or red override, <see cref="FakePeer"/> humans, a manual clock.
/// </summary>
public static class BotStageMTest
{
    public static IEnumerable<(string, Action)> All =>
    [
        ("bots M: an armed gatherer draws its sword when hit (CharacterEquipmentChanged, Q/W/E), casts a sword spell, takes its tool back after the fight", ArmedGatherer),
        ("bots M: a patrol with a bolt and a self buff casts the bolt first; OUTOFCOMBATHEAL is refused in combat (InCombat), never in a bot kit", SpellIntent),
        ("bots M: rewards: MinItems 1 with GearDropChance 0 moves exactly one worn item, a T5 patrol's POTION_HEAL resolves to T4; a red-zone death with DeathGearDropChance 1 empties every drop slot", Rewards),
        ("bots M: a gatherer's start carry (Gear.GathererStartCarry) is in its bag at its first defeat", StartCarry),
        ("bots M: a T1 gatherer with Weapons [spear, sword] and share 1 is armed every time (the sword, the one entry at T1)", LowTierWeapon),
        ("bots M: GetCharacterEquipment (op 143) of a bot, a human and a human on another map (a duel invite): NewEquipmentItem of each worn item, then {0 Guid, 1 ids by slot, 2 mastery level+1, 3 item power}; an unknown Guid rc 1", Inspect),
    ];

    private static void Check(bool cond, string what) => AlbionTests.Check(cond, what);

    private static readonly ClusterPvp Yellow = new(PvpRule.PvpAllowed, PlayersAreLethal: false, WarningType: "yellow");

    private static readonly ClusterPvp RedZone = new(PvpRule.PvpAllowed, PlayersAreLethal: true, WarningType: "red");

    /// <summary>Fast-thinking bots that cast at once (SpellChance 1, no reaction time), patrols dwelling at their stop, no drops unless asked.</summary>
    private static BotOptions Quick => new()
    {
        Enabled = true, ThinkSeconds = (0.3, 0.3), CrimesAgainstBots = false, JoinGraceSeconds = 0, RouteDwellSeconds = (300, 300), RouteEndDwellSeconds = (300, 300),
        GearDropChance = 0, DeathGearDropChance = 0, SpellChance = 1, ReactionMs = 0,
    };

    /// <summary>The built-in tables without Thetford's population, extras or start carry (each test adds its own).</summary>
    private static BotSettings Plain => BotTest.NoPopulation with { PatrolExtra = [], GathererStartCarry = (0, 0), GathererWeaponShare = 0 };

    private static List<Dictionary<byte, object?>> Of(FakePeer peer, EventCodes code, int from = 0) => BotTest.Of(peer, code, from);

    private static void Hit(Field f, Player attacker, Player target, double amount)
    {
        lock (f.World.Sync) Pvp.Damage(attacker, target, amount, EffectType.Physical, HitOrigin.Direct, -1, f.World.GameNow);
    }

    private static Player Patrol(Field f, Vector2 at, int tier)
    {
        lock (f.World.Sync)
        {
            var bot = f.World.Bots.Spawn(f.Map, BotRole.Patrol, tier, at, 0) ?? throw new InvalidOperationException("no patrol spawned");
            if (bot.IsMounted) Mounts.Dismount(bot);
            return bot;
        }
    }

    /// <summary>A T2 gatherer by Thetford's tree at (44.5, 3.5) (the patrol tests' spot), seen by a human 2 m from the tree.</summary>
    private static (Player Bot, FakePeer Watcher) Gatherer(Field f)
    {
        var nav = f.Nav();
        var tree = Client.Clusters!.Get("0000").Harvestables.MinBy(h => Vector2.Distance(h.Position, new Vector2(44.5f, 3.5f)))!;
        var (_, watcher, _) = f.Join("Wache", tree.Position - new Vector2(2, 0));
        Player bot;
        lock (f.World.Sync) bot = f.World.Bots.Spawn(f.Map, BotRole.Gatherer, 2, nav.Grid.Nearest(tree.Position + new Vector2(6, 0), 40)!.Value, 0)!;
        return (bot, watcher);
    }

    private static bool IsTool(Item? item) => item is not null && item.Template.Name.Contains("_TOOL_", StringComparison.Ordinal);

    private static void ArmedGatherer()
    {
        using var f = new Field(Quick with { EngageSeconds = 3 }, Plain with { GathererWeaponShare = 1, Weapons = ["MAIN_SWORD"] }, Yellow);
        var (bot, watcher) = Gatherer(f);
        var sword = bot.Inventory.Bag.Items.SingleOrDefault(i => i.Template.Slot == "mainhand");
        Check(IsTool(bot.Inventory.Equipment[0]) && sword is { Template.Name: "T2_MAIN_SWORD" } && sword.Active.Count(a => a >= 0) == 3,
              $"spawned with its tool in hand ({bot.Inventory.Equipment[0]?.Template.Name}) and a sword with 3 spells in its bag ({sword?.Template.Name})");
        var nav = f.Nav();
        var (redSession, _, red) = f.Join("Ivar", nav.Grid.Nearest(bot.PositionAt(f.World.Bots.Time), 4)!.Value, Pvp.Red);
        var mark = watcher.Events.Count;
        Hit(f, red, bot, 1);
        var changed = Of(watcher, EventCodes.CharacterEquipmentChanged, mark).Where(p => L(p, 0) == bot.Id).ToList();
        var slots = changed.Count == 1 && changed[0][7] is int[] seven ? seven : [];
        Check(bot.Inventory.Equipment[0] == sword && IsTool(bot.Inventory.Bag.Items.FirstOrDefault(i => i.Template.Slot == "mainhand")) && changed.Count == 1
              && slots.Take(3).SequenceEqual(sword!.Active.Select(a => (int)a)),
              $"hit: the sword in its mainhand, the tool in the bag, one CharacterEquipmentChanged to the watcher ({changed.Count}), Q/W/E = the sword's");
        f.Run(4);
        var casts = Of(watcher, EventCodes.CastStart, mark).Where(p => L(p, 0) == bot.Id).Select(p => Convert.ToInt32(p[5])).ToList();
        Check(casts.Any(id => sword!.Active.Contains((short)id)), $"it fights back with a sword spell ({string.Join(", ", casts.Select(id => Client.Spells!.Name(id)))})");
        lock (f.World.Sync) redSession.OnDisconnected("test"); // its attacker gone: the fight ends Bots:Pvp:EngageSeconds after his last hit
        f.Run(6);
        var swaps = Of(watcher, EventCodes.CharacterEquipmentChanged, mark).Where(p => L(p, 0) == bot.Id).ToList();
        Check(IsTool(bot.Inventory.Equipment[0]) && bot.Inventory.Bag.Items.Contains(sword) && !bot.Bot!.Armed && swaps.Count == 2,
              $"the fight over: its tool back in hand ({bot.Inventory.Equipment[0]?.Template.Name}), the sword in the bag, CharacterEquipmentChanged again ({swaps.Count} in all)");
    }

    private static void SpellIntent()
    {
        using var f = new Field(Quick, Plain, Yellow);
        var at = f.Nav().Exits[0].Arrival;
        var (_, peer, red) = f.Join("Rhea", at + new Vector2(6, 0), Pvp.Red);
        var bot = Patrol(f, at, 4);
        var spells = Client.Spells!;
        var bolt = spells.Template("FIRESTAFFBOLT2")!;
        var buff = spells.Template("BLOCK")!; // target="self", a buff
        lock (f.World.Sync)
        {
            var staff = Client.Items.Find("T4_MAIN_FIRESTAFF")!;
            bot.Inventory.Equipment.Put(0, new Item(f.World.ObjectIds.Next(), staff, 1, 1, staff.FullDurabilityRaw, [bolt.Id, -1, -1], []));
            foreach (var slot in new[] { 1, 2, 3, 4 })
                if (bot.Inventory.Equipment[slot] is { } item) Array.Fill(item.Active, (short)-1);
            bot.Inventory.Equipment[3]!.Active[0] = buff.Id; // the armour's slot: the buff
            bot.RecalculateStats(f.World.GameNow);
        }
        var mark = peer.Events.Count;
        f.Run(3);
        var first = Of(peer, EventCodes.CastStart, mark).FirstOrDefault(p => L(p, 0) == bot.Id);
        Check(first is not null && Convert.ToInt16(first[5]) == bolt.Id, $"its first cast is the bolt, not the buff ({(first is null ? "none" : spells.Name(Convert.ToInt32(first[5])))})");
        var heal = spells.Template("OUTOFCOMBATHEAL")!;
        lock (f.World.Sync)
        {
            int[] slots = [heal.Id];
            Combat.EnterCombat(red, null);
            var (cast, why) = SpellCasts.Check(red, slots, 0, heal.Id, 0, red.PositionAt(DateTime.UtcNow), 1, f.World.GameNow);
            Check(!heal.UsableInCombat && cast is null && why == CastRefusal.InCombat, $"OUTOFCOMBATHEAL in combat: refused {why}");
        }
        // The armour that offers it: twelve city bots in that armour never chose it.
        var material = new[] { "PLATE", "LEATHER", "CLOTH" }.FirstOrDefault(m => Enumerable.Range(1, 3)
            .Any(set => spells.SlotsOf($"T6_ARMOR_{m}_SET{set}") is { } a && a.Active.Length > 0 && a.Active[0].Contains(heal.Id)));
        Check(material is not null, $"OUTOFCOMBATHEAL is an armour choice ({material})");
        using var city = new Field(Quick, Plain with { Materials = [material!], CityTiers = new Dictionary<int, int> { [6] = 1 } });
        var square = city.Nav().Exits[0].Arrival;
        city.Join("Hana", square + new Vector2(30, 0));
        var robes = new List<Item>();
        lock (city.World.Sync)
            for (var i = 0; i < 12; i++)
                if (city.World.Bots.Spawn(city.Map, BotRole.City, 6, square, 0)?.Inventory.Equipment[3] is { } armour) robes.Add(armour);
        Check(robes.Count == 12 && robes.All(r => !r.Active.Contains(heal.Id)),
              $"12 bots in T6 {material} armour: none chose OUTOFCOMBATHEAL ({string.Join(", ", robes.Select(r => r.Active.Length > 0 && r.Active[0] >= 0 ? spells.Name(r.Active[0]) : "-").Distinct())})");
    }

    private static void Rewards()
    {
        {
            using var f = new Field(Quick with { MinItems = 1 }, Plain with { PatrolExtra = [("T{t}_POTION_HEAL", 1.0)] }, Yellow);
            var at = f.Nav().Exits[0].Arrival;
            var (_, _, blue) = f.Join("Edda", at + new Vector2(3, 0));
            var bot = Patrol(f, at, 5);
            var worn = bot.Inventory.Equipment.Items.Select(i => i.Id).ToHashSet();
            Hit(f, blue, bot, 1e7);
            f.Run(10.5); // a knockdown: the bag at the despawn
            var bag = f.Map.Objects.Values.OfType<PlayerLootBag>().SingleOrDefault(b => b.VictimId == bot.Id);
            var items = bag?.Items.Items.ToList() ?? [];
            var gear = items.Where(i => worn.Contains(i.Id)).ToList();
            Check(gear.Count == 1 && items.Count == 2 && items.Any(i => i.Template.Name == "T4_POTION_HEAL") && !bot.Inventory.Equipment.Items.Any(i => i.Id == gear[0].Id),
                  $"a knockdown with GearDropChance 0, MinItems 1: one worn item moved ({string.Join(", ", items.Select(i => i.Template.Name))}), T5 POTION_HEAL as T4");
        }
        {
            using var f = new Field(Quick with { DeathGearDropChance = 1 }, Plain, RedZone);
            var at = f.Nav().Exits[0].Arrival;
            var (_, _, blue) = f.Join("Freya", at + new Vector2(3, 0));
            var bot = Patrol(f, at, 4);
            var drops = new[] { 0, 1, 2, 3, 4, 6 }.Count(s => bot.Inventory.Equipment[s] is not null);
            Hit(f, blue, bot, 1e7);
            var bag = f.Map.Objects.Values.OfType<PlayerLootBag>().SingleOrDefault(b => b.VictimId == bot.Id);
            Check(bag is not null && bag.Items.Count == drops && new[] { 0, 1, 2, 3, 4, 6 }.All(s => bot.Inventory.Equipment[s] is null),
                  $"a red-zone death with DeathGearDropChance 1 (GearDropChance 0): all {drops} combat items in the bag ({bag?.Items.Count})");
        }
    }

    private static void StartCarry()
    {
        using var f = new Field(Quick with { ResourceDropShare = 1 }, Plain with { GathererStartCarry = (10, 10) }, Yellow);
        var (bot, _) = Gatherer(f);
        var carried = bot.Bot!.Fight.Carried.ToDictionary();
        var (_, _, red) = f.Join("Ivar", f.Nav().Grid.Nearest(bot.PositionAt(f.World.Bots.Time), 6)!.Value, Pvp.Red);
        Hit(f, red, bot, 1e7);
        f.Run(10.5);
        var bag = f.Map.Objects.Values.OfType<PlayerLootBag>().SingleOrDefault(b => b.VictimId == bot.Id);
        var (item, amount) = carried.Single();
        Check(amount == 10 && f.World.BotSettings.Tools.Keys.Any(kind => item.EndsWith("_" + kind, StringComparison.OrdinalIgnoreCase)),
              $"it spawned carrying {amount} x {item}, a raw resource of a gatherer's kind");
        Check(bag?.Items.Items.SingleOrDefault(i => i.Template.Name == item)?.Count == 10, $"its bag holds the 10 {item} (ResourceDropShare 1)");
    }

    private static void LowTierWeapon()
    {
        using var f = new Field(Quick, Plain with { GathererWeaponShare = 1, Weapons = ["MAIN_SPEAR", "MAIN_SWORD"] }, Yellow);
        var picks = Enumerable.Range(0, 8).Select(_ => f.World.Bots.Factory.GathererWeapon(1, f.World.Bots.Random)?.Name).ToList();
        Check(picks.All(n => n == "T1_MAIN_SWORD"), $"8 picks at T1: {string.Join(", ", picks.Select(n => n ?? "none"))}");
    }

    private static void Inspect()
    {
        using var f = new Field(Quick, Plain, Yellow);
        var at = f.Nav().Exits[0].Arrival;
        var (session, peer, me) = f.Join("Edda", at + new Vector2(3, 0));
        var (_, _, other) = f.Join("Gerd", at + new Vector2(5, 0));
        var bot = Patrol(f, at, 6);
        var farAt = f.World.Bots.Nav.Preload(Client.Clusters!.Get("1207")).Exits[0].Arrival;
        var (_, _, far) = f.Join("Hild", at, edit: c => (c.Cluster, c.PositionX, c.PositionY) = ("1207", farAt.X, farAt.Y));
        OperationResponse Ask(Guid who)
        {
            lock (f.World.Sync) session.OnRequest(Request(new GetCharacterEquipmentRequest { md = who, me = true }, 9), encrypted: false);
            return peer.Responses[^1];
        }
        foreach (var target in new[] { bot, other, far })
        {
            var mark = peer.Events.Count;
            var r = Ask(target.Character.Id);
            var ids = (long[])r.Parameters[1]!;
            var worn = Enumerable.Range(0, 10).Select(s => target.Inventory.Equipment[s]?.Id ?? 0).ToArray();
            // Stackables (a botpower bot's potion and meal) go as NewSimpleItem.
            var sent = Of(peer, EventCodes.NewEquipmentItem, mark).Concat(Of(peer, EventCodes.NewSimpleItem, mark)).Select(p => L(p, 0)).ToHashSet();
            var mastery = (byte[])r.Parameters[2]!;
            var expected = Enumerable.Range(0, 10).Select(s => s < 5 && target.Inventory.Equipment[s] is { } i ? (byte)(WorldSession.MasteryLevel(target, i) + 1) : (byte)0).ToArray();
            var who = target == far ? "a human on another map" : target.IsBot ? "the bot" : "a human";
            Check(r.ReturnCode == 0 && new Guid((byte[])r.Parameters[0]!) == target.Character.Id && ids.SequenceEqual(worn) && worn.Where(id => id != 0).All(sent.Contains)
                  && Convert.ToSingle(r.Parameters[3]) > 0 && mastery.SequenceEqual(expected) && (!target.IsBot || mastery[0] > 1),
                  $"{who}: rc 0, its Guid, {worn.Count(id => id != 0)} item ids by slot each sent as NewEquipmentItem (stackables NewSimpleItem), mastery+1 [{string.Join(",", mastery)}], item power {r.Parameters.GetValueOrDefault((byte)3)}");
        }
        Check(Ask(Guid.NewGuid()).ReturnCode == WorldSession.InspectNotFound, "an unknown Guid: rc 1 (the client's OnRequestFailed)");
    }
}
