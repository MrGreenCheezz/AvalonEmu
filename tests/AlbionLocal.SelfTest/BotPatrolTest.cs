using System.Numerics;
using AlbionLocal.Server;
using AlbionLocal.Server.Bots;
using AlbionLocal.Server.Content;
using AlbionLocal.Server.Data;
using AlbionLocal.Server.Entities;
using AlbionLocal.Server.Game;
using AlbionLocal.Server.Protocol;
using AlbionLocal.Server.Systems;
using static AlbionLocal.SelfTest.AlbionTests;
using static AlbionLocal.SelfTest.CombatTest;
using Field = AlbionLocal.SelfTest.BotTest.Field;

namespace AlbionLocal.SelfTest;

/// <summary>
/// Stage I bots, phase 3 (analysis/work/stageI/bots/SPEC.md §6): patrol bots in Thetford (0000) under a yellow or red PvP override and on
/// Birchcopse's roads (1207), with <see cref="FakePeer"/> humans on a <see cref="ManualClock"/>, the pve preset. The relation (a Blue player
/// may attack a patrol, which answers; it starts a fight only with a Red one), the on-sight attack and its exemptions, the group, the way
/// gate, a city refusing patrols; a bot's knockdown of a player (spared); a defeat's rewards (fame without KilledPlayer, bag and silver at
/// the despawn, the items moved with a player's spell layout, the PvP goals' credit), a death in a lethal zone, the farming cap; a
/// gatherer's "gathered" shares in its bag; Bots:Combat:HealthFactor; the group's answer after a member's defeat, a new group after a
/// turnover, the bag of a bot cleared early, a knockdown's crime, the chase, the walk back after the leash, duelists and riders.
/// </summary>
public static class BotPatrolTest
{
    public static IEnumerable<(string, Action)> All =>
    [
        ("bots patrol: phase 3 keys (defaults, a group, LootProtectionSeconds null or seconds, a bad value refused) and content/bots.json Rewards", Config),
        ("bots patrol: Birchcopse (yellow) is populated with a patrol group of two, out of view, the leader on its route", Population),
        ("bots patrol: a Blue player may attack a Red patrol in a yellow zone, not it him; one hit lets it answer for 20 s; PvpRules Official: Hostile at once", Relations),
        ("bots patrol: on sight it attacks a Red player 8 m away (Attack, HealthUpdate from it), never a Blue one", OnSight),
        ("bots patrol: not within Bots:Patrol:JoinGraceSeconds of a player's arrival", JoinGrace),
        ("bots patrol: a group of three takes the Red player on together", Group),
        ("bots patrol: no patrols in a city (.bot spawn refused, Friendly); routes from 1207's roads; no aggro around a wall", WaysAndCities),
        ("bots patrol: it knocks a Red player down: KnockedDown from it, no crime, the group spares him Bots:Patrol:SpareMinutes", DownsPlayer),
        ("bots patrol: knocked down: UpdateFame, no KilledPlayer, PvP goal credit; bag and silver at the despawn with the moved gear", DefeatRewards),
        ("bots patrol: a death in a lethal zone: Died, KilledPlayer, the bag at once", LethalDefeat),
        ("bots patrol: the same persona again within Bots:Rewards:SameBotMinutes: a quarter of the fame, no bag or silver, the goal refused", FarmingCap),
        ("bots patrol: a defeated gatherer's bag holds its share of what it gathered", GathererLoot),
        ("bots patrol: Bots:Combat:HealthFactor 2 doubles a bot's maximum health", HealthFactor),
        ("bots patrol: Bots:Pvp:LethalToPlayers with Pvp:LethalDeath: its lethal hit in a lethal zone kills (Died by it), no knockdown", LethalToPlayers),
        ("bots patrol: a Blue player defeats one of two members: the other keeps answering him (the group's engagement)", GroupAfterDefeat),
        ("bots patrol: a newcomer after the group's stay is over walks with a new group and a fresh stay", NewGroupAfterStay),
        ("bots patrol: .bot clear within the knockdown's 10 s still leaves its bag and silver", DropOnClear),
        ("bots patrol: Bots:Pvp:CrimesAgainstBots: a criminal knockdown of a bot costs the knockdown penalty (reason 5), not the kill's", KnockdownCrime),
        ("bots patrol: a chase on 1207: no movement sample's segment crosses a blocked cell", ChaseOnWalkable),
        ("bots patrol: the target beyond the leash: every member gives up, the leader walks back to a stop, the others behind it", LeashWalkBack),
        ("bots patrol: two Red players in a duel next to it are not attacked", Duelists),
        ("bots patrol: a riding Red player hit by it: its hits on the mount bar, the hostile dismount at 0 (Mounted key 13)", RiderDismounted),
        ("bots botpower: a pve patrol has a full-spec player's health and board, potions; at low health it drinks one", PvePower),
        ("bots botpower: a ranged patrol steps back from a Red player 2 m away along the walk grid", Kites),
    ];

    private static void Check(bool cond, string what) => AlbionTests.Check(cond, what);

    private static List<Dictionary<byte, object?>> Of(FakePeer peer, EventCodes code, int from = 0) => BotTest.Of(peer, code, from);

    /// <summary>The pve preset with bots, no join grace, fast thinking, long dwells (a patrol stays at its stop) and no drops but the ones a test asks for.</summary>
    private static BotOptions Quick => new()
    {
        Enabled = true, ThinkSeconds = (0.3, 0.3), CrimesAgainstBots = false, JoinGraceSeconds = 0, RouteDwellSeconds = (300, 300), RouteEndDwellSeconds = (300, 300),
        GearDropChance = 0,
    };

    private static readonly ClusterPvp Yellow = new(PvpRule.PvpAllowed, PlayersAreLethal: false, WarningType: "yellow");

    private static readonly ClusterPvp RedZone = new(PvpRule.PvpAllowed, PlayersAreLethal: true, WarningType: "red");

    /// <summary>Runs until <paramref name="done"/> (at most <paramref name="seconds"/>).</summary>
    private static void Until(Field f, double seconds, Func<bool> done)
    {
        for (var i = 0; i < seconds * 20 && !done(); i++) f.Run(0.05);
    }

    private static void Hit(Field f, Player attacker, Player target, double amount)
    {
        lock (f.World.Sync) Pvp.Damage(attacker, target, amount, EffectType.Physical, HitOrigin.Direct, -1, f.World.GameNow);
    }

    /// <summary>
    /// A GM patrol of <paramref name="tier"/> at Thetford's square, walked (or ridden) to its first stop and dwelling there on foot (the
    /// test's spot); a Blue watcher 30 m away joins first when nobody is on the map yet.
    /// </summary>
    private static Player Patrol(Field f, ClusterNav nav, int tier = 4)
    {
        if (f.Map.Template is null) f.Join("Wache", nav.Exits[0].Arrival + new Vector2(30, 0)); // the map in use (its template in)
        Player bot;
        lock (f.World.Sync) bot = f.World.Bots.Spawn(f.Map, BotRole.Patrol, tier, nav.Exits[0].Arrival, 0) ?? throw new InvalidOperationException("no patrol spawned");
        Settle(f, bot);
        if (bot.IsMounted)
            lock (f.World.Sync) Mounts.Dismount(bot);
        return bot;
    }

    /// <summary>Runs until the group's leader <paramref name="leader"/> dwells at its stop and its followers stand (at most 60 s).</summary>
    private static void Settle(Field f, Player leader)
    {
        var group = leader.Bot!.Group!;
        Until(f, 60, () => group.LastLeader == leader && !group.Walking && group.Members.All(m => !m.Bot!.Motion.Busy && m.Mount.State != MountState.Mounting));
    }

    /// <summary>A walkable spot <paramref name="distance"/> m (about) from <paramref name="bot"/> with a clear line to it.</summary>
    private static Vector2 Near(Field f, ClusterNav nav, Player bot, float distance)
    {
        var at = bot.PositionAt(f.World.Bots.Time);
        for (var i = 0; i < 24; i++)
        {
            var angle = i * Math.PI / 12;
            var spot = at + new Vector2((float)Math.Sin(angle), (float)Math.Cos(angle)) * distance;
            if (nav.Grid.Walkable(spot) && nav.Grid.Clear(at, spot)) return spot;
        }
        throw new InvalidOperationException($"no open spot {distance} m from the patrol at {at}");
    }

    private static void Relations()
    {
        foreach (var rule in new[] { BotPvpRules.Consent, BotPvpRules.Official })
        {
            using var f = new Field(Quick with { PvpRules = rule }, BotTest.NoPopulation, Yellow);
            var nav = f.Nav();
            var (_, _, blue) = f.Join("Bern", nav.Exits[0].Arrival + new Vector2(30, 0));
            var bot = Patrol(f, nav);
            if (rule == BotPvpRules.Official)
            {
                Check(Pvp.Of(bot, blue) == Relation.Hostile, "Bots:PvpRules Official: the patrol may attack a Blue player in a yellow zone at once");
                continue;
            }
            Check(bot.Flag == Pvp.Red && Pvp.Of(blue, bot) == Relation.Hostile && Pvp.Of(bot, blue) == Relation.Neutral,
                  $"a Red patrol (flag {bot.Flag}) in a yellow zone: Hostile to the Blue player, he Neutral to it (pve: no unprovoked attack)");
            Hit(f, blue, bot, 1);
            Check(Pvp.Of(bot, blue) == Relation.Hostile, "after his hit it may answer him");
            f.Run(21);
            Check(Pvp.Of(bot, blue) == Relation.Neutral, $"21 s after his last hit Neutral again ({bot.Bot!.Brain.State})");
        }
        var yellowOfficial = new Dictionary<string, BotPvpRules>(StringComparer.OrdinalIgnoreCase) { ["yellow"] = BotPvpRules.Official };
        using (var f = new Field(Quick with { PvpRulesByZone = yellowOfficial }, BotTest.NoPopulation, Yellow))
        {
            var nav = f.Nav();
            var (_, _, blue) = f.Join("Bern", nav.Exits[0].Arrival + new Vector2(30, 0));
            var bot = Patrol(f, nav);
            Check(Pvp.Of(bot, blue) == Relation.Hostile, "Bots:PvpRulesByZone { yellow: Official }: Hostile at once in a yellow zone");
        }
    }

    private static void OnSight()
    {
        using var f = new Field(Quick, BotTest.NoPopulation, Yellow);
        var nav = f.Nav();
        var bot = Patrol(f, nav);
        var (_, bluePeer, blue) = f.Join("Bea", Near(f, nav, bot, 8));
        var mark = bluePeer.Events.Count;
        f.Run(4);
        Check(!Of(bluePeer, EventCodes.Attack, mark).Any(p => L(p, 0) == bot.Id) && bot.Bot!.Fight.TargetId == 0,
              $"a Blue player 8 m from the patrol is never attacked ({bot.Bot!.Brain.State})");
        var (_, peer, red) = f.Join("Rik", Near(f, nav, bot, 8), Pvp.Red);
        mark = peer.Events.Count;
        var interval = SpellCasts.SwingInterval(bot) / (double)TimeSpan.TicksPerSecond;
        Until(f, f.World.Config.Bots.ReactionMs / 1000.0 + interval + 4, () => Of(peer, EventCodes.HealthUpdate, mark).Any(p => L(p, 0) == red.Id && L(p, 6) == bot.Id));
        Check(Of(peer, EventCodes.Attack, mark).Any(p => L(p, 0) == bot.Id && L(p, 2) == red.Id)
              && Of(peer, EventCodes.HealthUpdate, mark).Any(p => L(p, 0) == red.Id && L(p, 6) == bot.Id && Convert.ToInt32(p[7]) == -1),
              $"a Red player 8 m away: its Attack 13 {{0 bot, 2 him}} and his HealthUpdate {{6 bot, 7 -1}} (target {bot.Bot.Fight.TargetId})");
        Check(!Of(bluePeer, EventCodes.HealthUpdate, 0).Any(p => L(p, 0) == blue.Id), "the Blue player was never hit");
    }

    private static void JoinGrace()
    {
        using var f = new Field(Quick with { JoinGraceSeconds = 10 }, BotTest.NoPopulation, Yellow);
        var nav = f.Nav();
        var bot = Patrol(f, nav);
        var (_, peer, red) = f.Join("Ronja", Near(f, nav, bot, 6), Pvp.Red);
        f.Run(8);
        var early = Of(peer, EventCodes.Attack, 0).Any(p => L(p, 0) == bot.Id);
        Until(f, 6, () => Of(peer, EventCodes.Attack, 0).Any(p => L(p, 0) == bot.Id));
        Check(!early && Of(peer, EventCodes.Attack, 0).Any(p => L(p, 0) == bot.Id && L(p, 2) == red.Id),
              $"no attack in the first 8 s on the map (early {early}), then after the 10 s grace one");
    }

    private static void Group()
    {
        using var f = new Field(Quick with { DamageFactor = 0 }, BotTest.NoPopulation, Yellow);
        var nav = f.Nav();
        var (_, _, gm) = f.Join("Gunda", nav.Exits[0].Arrival);
        var reply = f.Gm(gm, ".bot spawn patrol 3 4");
        var bots = f.Map.Bots.Values.ToList();
        Check(bots.Count == 3 && bots.All(b => b.Bot!.Role == BotRole.Patrol && b.Bot.Group == bots[0].Bot!.Group) && bots[0].Bot!.Group!.Members.Count == 3,
              $".bot spawn patrol 3: one group of three ({reply})");
        var leader = bots[0].Bot!.Group!.Leader!;
        Settle(f, leader);
        var (_, peer, red) = f.Join("Rasmus", Near(f, nav, leader, 7), Pvp.Red);
        Until(f, 10, () => bots.All(b => Of(peer, EventCodes.Attack, 0).Any(p => L(p, 0) == b.Id && L(p, 2) == red.Id)));
        Check(bots.All(b => Of(peer, EventCodes.Attack, 0).Any(p => L(p, 0) == b.Id && L(p, 2) == red.Id)),
              $"all three attack him ({string.Join(", ", bots.Select(b => $"{b.Character.Name}: {b.Bot!.Brain.State}, target {b.Bot.Fight.TargetId}"))})");
    }

    private static void WaysAndCities()
    {
        using (var f = new Field(Quick, BotTest.NoPopulation, new ClusterPvp(PvpRule.PvpDisabled, WarningType: "safe")))
        {
            var nav = f.Nav();
            var (_, _, gm) = f.Join("Carla", nav.Exits[0].Arrival, Pvp.Red);
            var reply = f.Gm(gm, ".bot spawn patrol");
            Check(reply.StartsWith("error", StringComparison.Ordinal) && f.Map.Bots.Count == 0 && !f.World.Bots.PatrolsAllowed(f.Map), $"a city: .bot spawn patrol refused ({reply})");
        }
        using (var f = new Field(Quick, BotSettings.Default with
        {
            Override = new Dictionary<string, IReadOnlyDictionary<BotRole, int>> { ["1207"] = new Dictionary<BotRole, int> { [BotRole.Gatherer] = 0, [BotRole.Patrol] = 0 } },
        }, Yellow, cluster: "1207"))
        {
            var nav = f.Nav();
            var route = PatrolRoute.Pick(nav, Client.Clusters!.Get("1207"), new Random(7));
            Check(nav.Roads.Routes.Count > 0 && route is { Loop: false } && route.Stops.Count >= 2 && route.Stops.All(nav.Grid.Walkable)
                  && route.Stops.All(s => nav.Roads.Routes.Any(r => r.Any(p => Vector2.Distance(p, s) < 6))),
                  $"1207: a route along one of its {nav.Roads.Routes.Count} roads, every stop walkable ({route?.Stops.Count} stops)");
        }
        // A hand-made map: a wall along z = 5 from x = -55 to 55; behind it only a long way round.
        using (var f = new Field(Quick, BotTest.NoPopulation, Yellow))
        {
            var grid = WalkGrid.FromCells(-60, -60, 60, 60, Enumerable.Range(-55, 111).Select(x => (x, 5)), [new Vector2(0, 0), new Vector2(0, 12)]);
            var nav = new ClusterNav { Cluster = "test", Grid = grid, Roads = RoadMap.Build(grid, 4, [], 20000), Pois = [], Exits = [] };
            var bots = f.World.Bots;
            bool behind, open;
            lock (f.World.Sync) (behind, open) = (BotCombat.Reachable(bots, nav, new Vector2(0, 0), new Vector2(0, 12)), BotCombat.Reachable(bots, nav, new Vector2(0, 0), new Vector2(10, 0)));
            Check(!behind && open && bots.FindPath(nav, new Vector2(0, 0), new Vector2(0, 12)) is not null,
                  "a player 12 m away behind a wall (a way round of 100+ m) is out of reach, one 10 m away in the open is not");
        }
    }

    private static void DownsPlayer()
    {
        using var f = new Field(Quick with { DamageFactor = 1000 }, BotTest.NoPopulation, Yellow);
        var nav = f.Nav();
        var bot = Patrol(f, nav);
        var (_, peer, red) = f.Join("Dagny", Near(f, nav, bot, 4), Pvp.Red);
        Until(f, 8, () => red.Combat.Down is not null);
        var down = Of(peer, EventCodes.KnockedDown, 0).FirstOrDefault(p => L(p, 0) == red.Id);
        var spared = bot.Bot!.Fight.Spared.GetValueOrDefault(red.Character.Id);
        var minutes = (spared - f.World.GameNow) / (double)TimeSpan.TicksPerMinute;
        Check(down is not null && L(down, 2) == bot.Id && red.Combat.Down is { Kind: KnockdownKind.Pvp } && bot.Bot.Fight.TargetId == 0
              && minutes > 4.9 && minutes <= 5 && Of(peer, EventCodes.ReputationUpdate, 0).Count == 0,
              $"KnockedDown {{0 him, 2 the patrol}}, its target dropped, spared {minutes:0.##} min, no ReputationUpdate");
    }

    private static void DefeatRewards()
    {
        using var f = new Field(Quick with { GearDropChance = 1 }, BotTest.NoPopulation, Yellow);
        var nav = f.Nav();
        var bot = Patrol(f, nav);
        var (_, peer, blue) = f.Join("Edda", Near(f, nav, bot, 3));
        var worn = bot.Inventory.Equipment.Items.ToList();
        var persona = bot.Bot!.PersonaId;
        var mark = peer.Events.Count;
        Hit(f, blue, bot, 1e7);
        var downs = Of(peer, EventCodes.KnockedDown, mark).Where(p => L(p, 0) == bot.Id).ToList();
        var fame = Of(peer, EventCodes.UpdateFame, mark).Where(p => L(p, 0) == blue.Id).Sum(p => L(p, 2));
        Check(downs.Count == 1 && L(downs[0], 2) == blue.Id && fame > 0 && Of(peer, EventCodes.KilledPlayer, mark).Count == 0,
              $"KnockedDown {{0 bot, 2 him}}, UpdateFame +{fame} and no KilledPlayer");
        Check(blue.Character.Goals?.PvpVictims.GetValueOrDefault("b:" + persona) is { Count: 1 }, $"its PvP goal credit, keyed by the persona b:{persona}");
        f.Run(5);
        var early = Of(peer, EventCodes.NewLoot, mark).Count + Of(peer, EventCodes.NewSilverObject, mark).Count;
        f.Run(5.5);
        var bag = f.Map.Objects.Values.OfType<PlayerLootBag>().SingleOrDefault(b => b.VictimId == bot.Id);
        var pile = f.Map.Objects.Values.OfType<SilverPile>().SingleOrDefault(p => p.SourceId == bot.Id);
        var inBag = bag?.Items.Items.ToList() ?? [];
        var left = bot.Inventory.Equipment.Items.ToList();
        Check(early == 0 && bag is not null && Of(peer, EventCodes.NewLoot, mark).Count(p => L(p, 0) == bag.Id) == 1 && Of(peer, EventCodes.Leave, mark).Count(p => L(p, 0) == bot.Id) == 1
              && bag.Looters.SequenceEqual([blue.Character.Id]),
              $"nothing before the despawn ({early}); then one Leave, its bag (NewLoot) for him first");
        var (bagIds, leftIds) = (inBag.Select(i => i.Id).ToList(), left.Select(i => i.Id).ToList()); // the same objects, by their ids
        Check(inBag.Count >= 5 && !bagIds.Intersect(leftIds).Any() && bagIds.Concat(leftIds).ToHashSet().SetEquals(worn.Select(i => i.Id)) && inBag.All(i => i.Template.Slot != "mount" && i.Template.Slot != "bag"),
              $"its {worn.Count} items: {inBag.Count} moved into the bag ({string.Join(", ", inBag.Select(i => i.Template.Name))}), {left.Count} left on it, none in both");
        Check(pile is { Amount: > 0, SourceKind: SilverPile.FromPlayer } && pile.Owner == blue.Character.Id && Of(peer, EventCodes.NewSilverObject, mark).Any(p => L(p, 0) == pile.Id),
              $"its silver ({pile?.Amount}) as a player's pile for him");
        var spells = Client.Spells!;
        Check(inBag.Any(i => spells.SlotsOf(i.Template.Name) is { PassiveSlots: > 0 })
              && inBag.All(i => spells.SlotsOf(i.Template.Name) is not { } s || (i.Active.Length == s.ActiveSlots && i.Passive.Length == s.PassiveSlots)),
              $"the bag's gear has a player's spell slots (a bot's own has no passives): {string.Join(", ", inBag.Select(i => $"{i.Template.Name} {i.Active.Length}/{i.Passive.Length}"))}");
    }

    private static void LethalDefeat()
    {
        using var f = new Field(Quick with { GearDropChance = 1 }, BotTest.NoPopulation, RedZone);
        var nav = f.Nav();
        var bot = Patrol(f, nav);
        var (_, peer, blue) = f.Join("Freya", Near(f, nav, bot, 3));
        var mark = peer.Events.Count;
        Hit(f, blue, bot, 1e7);
        var died = Of(peer, EventCodes.Died, mark).FirstOrDefault(p => L(p, 1) == bot.Id);
        var killed = Of(peer, EventCodes.KilledPlayer, mark).FirstOrDefault();
        Check(died is not null && L(died, 9) == blue.Id && killed is not null && L(killed, 0) == blue.Id && L(killed, 1) == bot.Id
              && f.Map.Objects.Values.OfType<PlayerLootBag>().Any(b => b.VictimId == bot.Id && Of(peer, EventCodes.NewLoot, mark).Any(p => L(p, 0) == b.Id)),
              "a red zone: Died {1 bot, 9 him}, KilledPlayer {0 him, 1 bot} and its bag at once");
        f.Run(3.5);
        Check(bot.Map is null && Of(peer, EventCodes.Leave, mark).Count(p => L(p, 0) == bot.Id) == 1, "gone 3 s later with one Leave");
    }

    private static void FarmingCap()
    {
        var one = BotSettings.Default with
        {
            Override = new Dictionary<string, IReadOnlyDictionary<BotRole, int>> { ["0000"] = new Dictionary<BotRole, int> { [BotRole.City] = 0, [BotRole.Patrol] = 1 } },
        };
        using var f = new Field(Quick with { GearDropChance = 1, PopulateSeconds = 0.5, MinSpawnCells = 0, RespawnSeconds = (1, 1), GroupSize = (1, 1) }, one, Yellow);
        var nav = f.Nav();
        var (_, peer, blue) = f.Join("Gerd", nav.Exits[0].Arrival);
        var gains = new List<(double PerFame, int Drops)>();
        string? persona = null;
        for (var round = 0; round < 2; round++)
        {
            Until(f, 10, () => f.Map.Bots.Count == 1);
            var bot = f.Map.Bots.Values.Single();
            persona ??= bot.Bot!.PersonaId;
            Check(bot.Bot!.PersonaId == persona, $"round {round + 1}: the slot's persona {bot.Bot.PersonaId}");
            var mark = peer.Events.Count;
            var kill = Death.KillFame(bot);
            Hit(f, blue, bot, 1e7);
            var fame = Of(peer, EventCodes.UpdateFame, mark).Where(p => L(p, 0) == blue.Id).Sum(p => L(p, 2));
            f.Run(10.5);
            var objects = f.Map.Objects.Values;
            gains.Add((fame / (double)kill, objects.OfType<PlayerLootBag>().Count(b => b.VictimId == bot.Id) + objects.OfType<SilverPile>().Count(x => x.SourceId == bot.Id)));
        }
        var ratio = gains[1].PerFame / gains[0].PerFame;
        Check(gains[0].Drops == 2 && gains[1].Drops == 0 && Math.Abs(ratio - BotRewards.SameBotFameShare) < 0.02,
              $"first: bag and silver ({gains[0].Drops}); again within 10 min: none ({gains[1].Drops}), fame per kill fame x {ratio:0.###}");
        var g = blue.Character.Goals!;
        Check(g.PvpVictims.GetValueOrDefault("b:" + persona)?.Count == 1 && g.PvpRefused.GetValueOrDefault("same victim cooldown") == 1,
              "the PvP goals saw both: one credit, the second refused (same victim cooldown)");
    }

    private static void GathererLoot()
    {
        using var f = new Field(Quick with { ResourceDropShare = 1 }, BotTest.NoPopulation, Yellow);
        var nav = f.Nav();
        var tree = Client.Clusters!.Get("0000").Harvestables.MinBy(h => Vector2.Distance(h.Position, new Vector2(44.5f, 3.5f)))!;
        f.Join("Hedda", tree.Position - new Vector2(2, 0));
        Player bot;
        lock (f.World.Sync) bot = f.World.Bots.Spawn(f.Map, BotRole.Gatherer, 2, nav.Grid.Nearest(tree.Position + new Vector2(6, 0), 40)!.Value, 0)!;
        Until(f, 40, () => bot.Bot!.Fight.CarriedTotal > 0);
        var item = bot.Bot!.Fight.Carried.Keys.First();
        var other = item == "T3_ROCK" ? "T2_WOOD" : "T3_ROCK";
        bot.Bot.Fight.Carried[other] = 7; // as if it had touched a node of another tier of its band too
        var carried = bot.Bot.Fight.Carried.ToDictionary();
        var (_, _, red) = f.Join("Ivar", nav.Grid.Nearest(bot.PositionAt(f.World.Bots.Time), 6)!.Value, Pvp.Red);
        Hit(f, red, bot, 1e7);
        f.Run(10.5);
        var bag = f.Map.Objects.Values.OfType<PlayerLootBag>().SingleOrDefault(b => b.VictimId == bot.Id);
        var stacks = bag?.Items.Items.ToDictionary(i => i.Template.Name, i => i.Count) ?? [];
        Check(carried[item] > 0 && stacks.Count == carried.Count && carried.All(c => stacks.GetValueOrDefault(c.Key) == c.Value),
              $"it gathered {string.Join(", ", carried.Select(c => $"{c.Value} x {c.Key}"))}: its bag holds each as its own stack (ResourceDropShare 1, no gear: {string.Join(", ", stacks.Select(s => $"{s.Key} x{s.Value}"))})");
    }

    private static void HealthFactor()
    {
        using var f = new Field(Quick with { HealthFactor = 2 }, BotTest.NoPopulation, Yellow);
        var nav = f.Nav();
        f.Join("Jana", nav.Exits[0].Arrival + new Vector2(30, 0));
        Player bot;
        lock (f.World.Sync) bot = f.World.Bots.Spawn(f.Map, BotRole.Patrol, 5, nav.Exits[0].Arrival, 0)!;
        var plain = bot.ComputeStats().MaxHealth;
        Check(plain > 0 && Math.Abs(bot.Health.Max - Math.Round(plain * 2)) < 1, $"its maximum health {bot.Health.Max} = 2 x {plain}");
    }

    /// <summary>StageN botpower: the pve preset's bot keys as they are (full mastery and health, Bots:Combat:Potions) on a T6 patrol.</summary>
    private static void PvePower()
    {
        var pve = ServerConfig.PveDefaults.Bots with
        {
            ThinkSeconds = (0.3, 0.3), JoinGraceSeconds = 0, RouteDwellSeconds = (300, 300), RouteEndDwellSeconds = (300, 300), GearDropChance = 0, Potions = (2, 2),
        };
        using var f = new Field(pve, BotTest.NoPopulation, Yellow);
        var nav = f.Nav();
        var bot = Patrol(f, nav, 6);
        var potion = bot.Inventory.Equipment[8];
        Check(bot.Progression == f.World.UnlockAll && bot.Progression is not null && Math.Abs(bot.Health.Max - bot.ComputeStats().MaxHealth) < 1
              && potion is { Count: 2 } && potion.Template.Name.EndsWith("_POTION_HEAL", StringComparison.Ordinal) && bot.Inventory.Equipment.Items.Where(i => i.Template.Slot is "head" or "armor").All(i => i.Quality is >= 1 and <= 2),
              $"a T6 patrol on the full board with its gear's whole health {bot.Health.Max} (stats {bot.ComputeStats().MaxHealth}), {potion?.Count} x {potion?.Template.Name}");
        var (_, _, red) = f.Join("Rolf", Near(f, nav, bot, 4), Pvp.Red);
        Until(f, 4, () => bot.Bot!.Fight.TargetId == red.Id);
        lock (f.World.Sync) bot.Health.Set(bot.Health.Max * 0.2f, f.World.GameNow);
        Until(f, 3, () => bot.Inventory.Equipment[8] is not { Count: 2 });
        Check(bot.Bot!.Fight.TargetId == red.Id && bot.Inventory.Equipment[8] is not { Count: 2 },
              $"at 20 % health it drinks a potion ({bot.Inventory.Equipment[8]?.Count ?? 0} left, health {bot.Health.At(f.World.GameNow):0}/{bot.Health.Max})");
    }

    /// <summary>StageN botpower: Bots:Combat:KiteDistance; a T4 bow for the patrol's weapon.</summary>
    private static void Kites()
    {
        using var f = new Field(Quick with { Potions = (0, 0) }, BotTest.NoPopulation, Yellow);
        var nav = f.Nav();
        var bot = Patrol(f, nav);
        lock (f.World.Sync)
        {
            var bow = f.World.Items.Find("T4_2H_BOW")!;
            bot.Inventory.Equipment.Take(1);
            bot.Inventory.Equipment.Take(0);
            bot.Inventory.Equipment.Put(0, new Item(f.World.ObjectIds.Next(), bow, 1, 1, bow.FullDurabilityRaw, [], []));
            bot.RecalculateStats(f.World.GameNow);
        }
        var (_, _, red) = f.Join("Ria", Near(f, nav, bot, 2), Pvp.Red);
        var from = red.PositionAt(DateTime.UtcNow);
        var start = Vector2.Distance(bot.PositionAt(f.World.Bots.Time), from);
        Until(f, 5, () => bot.Bot!.Fight.KiteUntil != 0 && !bot.Bot.Motion.Busy);
        var end = Vector2.Distance(bot.PositionAt(f.World.Bots.Time), from);
        Check(bot.Bot!.Fight.KiteUntil != 0 && end > start + 2 && nav.Grid.Walkable(bot.PositionAt(f.World.Bots.Time)),
              $"it stepped back from {start:0.#} m to {end:0.#} m, on walkable ground");
    }

    private static void LethalToPlayers()
    {
        using var f = new Field(Quick with { DamageFactor = 1000, LethalToPlayers = true }, BotTest.NoPopulation, RedZone, config: c => c with { PvpLethalDeath = true });
        var nav = f.Nav();
        var bot = Patrol(f, nav);
        var (_, peer, red) = f.Join("Kaja", Near(f, nav, bot, 4), Pvp.Red);
        Until(f, 8, () => red.Combat.Down is not null);
        var died = Of(peer, EventCodes.Died, 0).FirstOrDefault(p => L(p, 1) == red.Id);
        Check(red.Combat.Down is { Kind: KnockdownKind.Dead } && died is not null && L(died, 9) == bot.Id && !Of(peer, EventCodes.KnockedDown, 0).Any(p => L(p, 0) == red.Id),
              $"a red zone with Pvp:LethalDeath: Died {{1 him, 9 the patrol}}, no KnockedDown (down {red.Combat.Down?.Kind})");
    }

    private static void Population()
    {
        using var f = new Field(Quick with { PopulateSeconds = 0.5, GroupSize = (2, 2), RouteDwellSeconds = (2, 2), RouteEndDwellSeconds = (2, 2), ActiveRange = 10000 }, BotSettings.Default with
        {
            Override = new Dictionary<string, IReadOnlyDictionary<BotRole, int>> { ["1207"] = new Dictionary<BotRole, int> { [BotRole.Gatherer] = 0, [BotRole.Patrol] = 2 } },
        }, Yellow, cluster: "1207");
        var nav = f.Nav();
        var (_, _, human) = f.Join("Lina", nav.Exits[0].Arrival);
        Until(f, 10, () => f.Map.Bots.Count == 2);
        var bots = f.Map.Bots.Values.ToList();
        var group = bots.FirstOrDefault()?.Bot!.Group;
        Check(bots.Count == 2 && bots.All(b => b.Bot!.Role == BotRole.Patrol && b.Bot.Group == group && b.Flag == Pvp.Red && b.Bot.Slot is not null)
              && bots.All(b => !b.Observers.Contains(human)) && group!.Route.Stops.Count >= 2,
              $"two Red slot patrols of one group, none in the human's view ({bots.Count} bots, route of {group?.Route.Stops.Count} stops)");
        var stops = new HashSet<int>();
        f.Run(90, () => stops.Add(group!.Stop));
        Check(stops.Count >= 2, $"its leader walks the route: stops {string.Join(", ", stops.Order())} in 90 s ({group!.Leader?.Bot!.Brain.State})");
    }

    private static void Config()
    {
        var dir = NewDir();
        var file = Path.Combine(dir, "albionlocal.json");
        File.WriteAllText(file, """{ "Bots": { "Patrol": { "AggroRadius": 12, "GroupSize": "2-3" }, "Rewards": { "LootProtectionSeconds": null }, "PvpRulesByZone": { "Red": "Official" } } }""");
        var c = ServerConfig.Load(file, []).Bots;
        Check(c is { AggroRadius: 12, GroupSize: (2, 3), LootProtectionSeconds: null, SpareMinutes: 5, JoinGraceSeconds: 10, AttackMounted: true, LethalToPlayers: false,
                  DamageFactor: 1, HealthFactor: 1 /* the pve preset since stageN botpower (0.7 before) */, FameFactor: 0.5, GearDropChance: 0.2, ResourceDropShare: 0.5, BagsPerHour: 30, SameBotMinutes: 10 }
              && ServerConfig.Load(file, ["--Bots:Rewards:LootProtectionSeconds=60"]).Bots.LootProtectionSeconds == 60
              && c.PvpRulesIn("red") == BotPvpRules.Official && c.PvpRulesIn("black") == BotPvpRules.Consent && new BotOptions().PvpRulesByZone.Count == 0,
              "Patrol:* and Rewards:* keys with their defaults; LootProtectionSeconds null (gamedata) or seconds; PvpRulesByZone red Official, the rest PvpRules");
        var pve = ServerConfig.Load(Path.Combine(dir, "none.json"), []).Bots;
        Check(pve.PvpRulesIn("red") == BotPvpRules.Official && pve.PvpRulesIn("black") == BotPvpRules.Official && pve.PvpRulesIn("yellow") == BotPvpRules.Consent
              && ServerConfig.Load(Path.Combine(dir, "none.json"), ["--Preset=official"]).Bots.PvpRulesByZone.Count == 0,
              "pve preset: patrols use the official rules in red and black zones, consent elsewhere; official preset: no zone rules");
        static bool Refused(string file, string arg)
        {
            try { ServerConfig.Load(file, [arg]); return false; }
            catch (InvalidDataException) { return true; }
        }
        Check(Refused(file, "--Bots:Rewards:GearDropChance=2") && Refused(file, "--Bots:Patrol:GroupSize=0-3") && Refused(file, "--Bots:Rewards:LootProtectionSeconds=soon")
              && Refused(file, "--Bots:PvpRulesByZone:black=Never"),
              "a chance above 1, a group of 0, a protection that is no number or an unknown zone rule refused");
        var shipped = BotSettings.Load(Path.Combine(DataDir(), "..", "content", "bots.json"));
        var parsed = BotSettings.Parse("""{ "Rewards": { "Silver": { "4": 300 }, "PatrolExtra": [{ "Item": "T{t}_POTION_HEAL", "Chance": 0.1 }] } }""", "test");
        Check(shipped.Silver.OrderBy(x => x.Key).SequenceEqual(BotSettings.Default.Silver.OrderBy(x => x.Key)) && shipped.PatrolExtra.SequenceEqual(BotSettings.Default.PatrolExtra)
              && shipped.PatrolExtra.SequenceEqual([("T{t}_POTION_HEAL", 0.25), ("T{t}_MEAL_STEW", 0.15)])
              && parsed.Silver.Count == 1 && parsed.Silver[4] == 300 && parsed.PatrolExtra.Single() == ("T{t}_POTION_HEAL", 0.1),
              "content/bots.json Rewards: the built-in silver table and extras (stageM: POTION_HEAL 25 %, MEAL_STEW 15 %); a file's Silver and PatrolExtra read");
        try { Directory.Delete(dir, true); } catch (IOException) { }
    }

    private static void GroupAfterDefeat()
    {
        using var f = new Field(Quick with { DamageFactor = 0 }, BotTest.NoPopulation, Yellow);
        var nav = f.Nav();
        var (_, _, gm) = f.Join("Ulla", nav.Exits[0].Arrival);
        f.Gm(gm, ".bot spawn patrol 2 4");
        var group = f.Map.Bots.Values.First().Bot!.Group!;
        var leader = group.Leader!;
        Settle(f, leader);
        var other = group.Members.Single(b => b != leader);
        var (_, peer, blue) = f.Join("Vera", Near(f, nav, leader, 3));
        Hit(f, blue, leader, 1);
        Until(f, 3, () => other.Bot!.Fight.TargetId == blue.Id);
        var joined = other.Bot!.Fight.TargetId == blue.Id;
        Hit(f, blue, leader, 1e7);
        var mark = peer.Events.Count;
        f.Run(4);
        Check(joined && leader.Bot!.Fight.Defeated && other.Bot.Fight.TargetId == blue.Id && other.Bot.Fight.Engaged.Count == 0
              && Of(peer, EventCodes.Attack, mark).Any(p => L(p, 0) == other.Id && L(p, 2) == blue.Id),
              $"the member he never hit joined in ({joined}) and still attacks him 4 s after the leader's defeat ({other.Bot.Brain.State}, target {other.Bot.Fight.TargetId})");
    }

    private static void NewGroupAfterStay()
    {
        using var f = new Field(Quick with { PopulateSeconds = 0.5, GroupSize = (2, 2), ArrivalSeconds = (1, 1), ActiveRange = 10000 }, BotSettings.Default with
        {
            Override = new Dictionary<string, IReadOnlyDictionary<BotRole, int>> { ["1207"] = new Dictionary<BotRole, int> { [BotRole.Gatherer] = 0, [BotRole.Patrol] = 2 } },
        }, Yellow, cluster: "1207");
        var nav = f.Nav();
        f.Join("Yrsa", nav.Exits[0].Arrival);
        Until(f, 10, () => f.Map.Bots.Count == 2);
        var old = f.Map.Bots.Values.First().Bot!.Group!;
        var (gone, kept) = (old.Members[0], old.Members[1]);
        lock (f.World.Sync)
        {
            old.StayUntil = f.World.Clock.Now; // the group's stay is over; its other member is still on its way
            f.World.Bots.Despawn(gone, "test: left through an exit", BotRefill.Arrival);
        }
        Until(f, 5, () => f.Map.Bots.Count == 2);
        var newcomer = f.Map.Bots.Values.FirstOrDefault(b => b != kept);
        f.Run(2);
        Check(newcomer?.Bot is { } info && info.Group != old && info.Group!.StayUntil > f.World.Clock.Now && info.StayUntil > f.World.Clock.Now && newcomer.Map == f.Map
              && kept.Bot!.Group == old,
              $"the newcomer walks with a new group and stays ({newcomer?.Bot?.Brain.State}); the old member keeps its own");
    }

    private static void DropOnClear()
    {
        using var f = new Field(Quick with { GearDropChance = 1 }, BotTest.NoPopulation, Yellow);
        var nav = f.Nav();
        var bot = Patrol(f, nav);
        var (_, _, blue) = f.Join("Wanda", Near(f, nav, bot, 3));
        Hit(f, blue, bot, 1e7);
        f.Run(2);
        var reply = f.Gm(blue, ".bot clear");
        var objects = f.Map.Objects.Values;
        Check(bot.Map is null && objects.OfType<PlayerLootBag>().Any(b => b.VictimId == bot.Id && b.Items.Items.Any()) && objects.OfType<SilverPile>().Any(p => p.SourceId == bot.Id),
              $".bot clear 2 s after the knockdown ({reply}): its bag and silver lie there");
    }

    private static void KnockdownCrime()
    {
        using var f = new Field(Quick with { CrimesAgainstBots = true }, BotTest.NoPopulation, Yellow);
        var nav = f.Nav();
        var tree = Client.Clusters!.Get("0000").Harvestables.MinBy(h => Vector2.Distance(h.Position, new Vector2(44.5f, 3.5f)))!;
        var (_, peer, red) = f.Join("Xenia", tree.Position - new Vector2(2, 0), Pvp.Red);
        Player bot;
        lock (f.World.Sync) bot = f.World.Bots.Spawn(f.Map, BotRole.Gatherer, 2, nav.Grid.Nearest(tree.Position + new Vector2(6, 0), 40)!.Value, 0)!;
        var mark = peer.Events.Count;
        Hit(f, red, bot, 1e7);
        var reasons = Of(peer, EventCodes.ReputationUpdate, mark).Select(p => L(p, 6)).ToList();
        Check(bot.Flag == Pvp.Blue && bot.Bot!.Fight.Defeated && reasons.SequenceEqual([Reputation.LostAttack, Reputation.LostKnockdown]),
              $"a Red player knocks a Blue gatherer down: ReputationUpdate reasons {string.Join(", ", reasons)} (3 the attack, 5 the knockdown)");
    }

    private static void ChaseOnWalkable()
    {
        using var f = new Field(Quick with { DamageFactor = 0, AggroRadius = 30 }, BotSettings.Default with
        {
            Override = new Dictionary<string, IReadOnlyDictionary<BotRole, int>> { ["1207"] = new Dictionary<BotRole, int> { [BotRole.Gatherer] = 0, [BotRole.Patrol] = 0 } },
        }, Yellow, cluster: "1207");
        var nav = f.Nav();
        var bot = Patrol(f, nav);
        var here = bot.PositionAt(f.World.Bots.Time);
        Vector2? around = null; // a spot 22 m away without a straight line to it, when there is one
        lock (f.World.Sync)
            for (var i = 0; i < 48 && around is null; i++)
            {
                var spot = here + new Vector2((float)Math.Sin(i * Math.PI / 24), (float)Math.Cos(i * Math.PI / 24)) * 22;
                if (nav.Grid.Walkable(spot) && !nav.Grid.Clear(here, spot) && BotCombat.Reachable(f.World.Bots, nav, here, spot)) around = spot;
            }
        var (_, peer, red) = f.Join("Ansgar", around ?? Near(f, nav, bot, 22), Pvp.Red);
        var (samples, crossing) = (0, new List<string>());
        f.Run(15, () =>
        {
            if (bot.Movement is not { Speed: > 0 } m) return;
            samples++;
            if (!nav.Grid.ClearOpen(m.From, m.Target)) crossing.Add($"{m.From}→{m.Target}");
        });
        Check(samples > 0 && crossing.Count == 0 && Of(peer, EventCodes.Attack, 0).Any(p => L(p, 0) == bot.Id && L(p, 2) == red.Id),
              $"the chase to the Red player 22 m away ({(around is null ? "a straight line" : "around an obstacle")}): {samples} samples, none across a blocked cell ({string.Join("; ", crossing.Take(3))})");
    }

    private static void LeashWalkBack()
    {
        using var f = new Field(Quick with { DamageFactor = 0 }, BotTest.NoPopulation, Yellow);
        var nav = f.Nav();
        var (_, _, gm) = f.Join("Zora", nav.Exits[0].Arrival);
        f.Gm(gm, ".bot spawn patrol 3 4");
        var group = f.Map.Bots.Values.First().Bot!.Group!;
        var bots = group.Members.ToList();
        var leader = group.Leader!;
        Settle(f, leader);
        var (_, _, red) = f.Join("Rune", Near(f, nav, leader, 7), Pvp.Red);
        Until(f, 10, () => bots.All(b => b.Bot!.Fight.TargetId == red.Id));
        var fought = bots.All(b => b.Bot!.Fight.TargetId == red.Id);
        var centre = leader.Bot!.Fight.EngagePoint ?? leader.PositionAt(f.World.Bots.Time);
        var far = Enumerable.Range(0, 96).Select(i => centre + new Vector2((float)Math.Sin(i * Math.PI / 12), (float)Math.Cos(i * Math.PI / 12)) * (50 + 10 * (i / 24)))
            .FirstOrDefault(nav.Grid.Walkable); // a walkable spot 50-80 m away
        lock (f.World.Sync) red.Teleport(far, 0, DateTime.UtcNow);
        Vector2 At(Player b) => b.PositionAt(f.World.Bots.Time);
        bool Back() => bots.All(b => !b.Bot!.Fight.Active) && !leader.Bot!.Motion.Busy && group.Route.Stops.Any(s => Vector2.Distance(s, At(leader)) < 1.5f)
            && bots.All(b => Vector2.Distance(At(b), At(leader)) <= PatrolBrain.FollowSpacing * bots.Count + 2);
        Until(f, 40, Back);
        Check(fought && Back(),
              $"all three fought him, gave him up beyond the leash and the group is back on a stop of its route ({string.Join(", ", bots.Select(b => $"{b.Character.Name}: {b.Bot!.Brain.State}"))})");
    }

    private static void Duelists()
    {
        using var f = new Field(Quick, BotTest.NoPopulation, Yellow);
        var nav = f.Nav();
        var bot = Patrol(f, nav);
        var spot = Near(f, nav, bot, 8);
        var (_, peer, a) = f.Join("Brun", spot, Pvp.Red);
        var (_, _, b) = f.Join("Cora", spot + new Vector2(1, 0), Pvp.Red);
        short challenged, accepted;
        lock (f.World.Sync) (challenged, accepted) = (f.World.Duels.Challenge(a, b.Id, 0), f.World.Duels.Accept(b));
        f.Run(5);
        Check(challenged == 0 && accepted == 0 && f.World.Duels.OpponentOf(a) == b && bot.Bot!.Fight.TargetId == 0 && !Of(peer, EventCodes.Attack, 0).Any(p => L(p, 0) == bot.Id),
              $"a duel (challenge {challenged}, accept {accepted}) 8 m from the patrol: no Attack on them in 5 s ({bot.Bot!.Brain.State})");
    }

    private static void RiderDismounted()
    {
        using var f = new Field(Quick with { JoinGraceSeconds = 6, DamageFactor = 20 }, BotTest.NoPopulation, Yellow);
        var nav = f.Nav();
        var bot = Patrol(f, nav);
        var (_, peer, red) = f.Join("Dorte", Near(f, nav, bot, 5), Pvp.Red, c => c.Equipment[Mounts.MountSlot] = new InventoryItem("T5_MOUNT_OX"));
        lock (f.World.Sync) Mounts.Start(red, remount: false);
        Until(f, 5, () => red.IsMounted);
        var rode = red.IsMounted;
        var mark = peer.Events.Count;
        Until(f, 20, () => !red.IsMounted);
        var bars = Of(peer, EventCodes.MountHealthUpdate, mark).Count(p => L(p, 0) == red.Id && L(p, 6) == bot.Id);
        var off = Of(peer, EventCodes.Mounted, mark).FirstOrDefault(p => L(p, 0) == red.Id);
        Check(rode && bars > 0 && !red.IsMounted && off is not null && off.ContainsKey(13),
              $"he rode ({rode}); {bars} MountHealthUpdate {{6 the patrol}}, then the hostile dismount (Mounted key 13: {off?.ContainsKey(13)})");
    }
}
