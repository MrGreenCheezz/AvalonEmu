using System.Numerics;
using System.Text.Json;
using System.Text.RegularExpressions;
using AlbionLocal.Photon.Serialization;
using AlbionLocal.Server;
using AlbionLocal.Server.Content;
using AlbionLocal.Server.Data;
using AlbionLocal.Server.Entities;
using AlbionLocal.Server.Game;
using AlbionLocal.Server.Network;
using AlbionLocal.Server.Persistence;
using AlbionLocal.Server.Protocol;
using AlbionLocal.Server.Systems;
using static AlbionLocal.SelfTest.AlbionTests;

namespace AlbionLocal.SelfTest;

/// <summary>
/// Expeditions (analysis/work/stageH/expeditions/SPEC.md §10, "SPEC" here): the client tables against the golden exp_tables.json, the
/// Mine-SOLO instance objects and the spawn aliases; then, on FakePeer sessions with a manual clock, T3_STANDARD from the Thetford agent:
/// the register and its refusals, the Join channel (cancelled, then the push), the instance (objects, 260/261), the kills, checkpoints,
/// a death, the completion with the bonus rewards, the finish exit and a timed-out run.
/// </summary>
public static class ExpeditionTest
{
    public static IEnumerable<(string, Action)> All =>
    [
        ("expeditions: client tables = exp_tables.json (categories, agent 4, 26 playable), Mine-SOLO objects and start, spawn aliases, rewards exist", Data),
        ("expeditions: Thetford T3 register (10858/10856), Join channel (279, 10 s, push {0, 1}), instance 251/252/315, 260/261, kills, checkpoints, death, bonus rewards, finish exit, timeout", Live),
        ("expeditions: T4_VETERAN queue of two, party leader invites (262, 273), one instance for 2 (x0.70), hardcore level 2 by token (10864, buff 02, x2.516, D3 token, level 3 open)", Groups),
        ("expeditions (econ SPEC §3): hardcore level 18 completed spawns HARCORE_EXPEDITION_LVL_18 at the map's HCE_FINAL_REWARD point; a T6 Avalonian elite map from the bag opens T6_AVALON_ELITE_CONSUMABLE level 1 on an ELITE marker", Leftovers),
    ];

    private static ClusterStore Store => Client.Clusters!;

    private static void Check(bool cond, string what) => AlbionTests.Check(cond, what);

    private static int Code(EventData e) => Convert.ToInt32(e.Parameters.GetValueOrDefault(ReservedKeys.EventCode));

    private static int Int(EventData e, byte key) => Convert.ToInt32(e.Parameters.GetValueOrDefault(key) ?? 0);

    private static Vector2 V(JsonElement a) => new(a[0].GetSingle(), a[1].GetSingle());

    private static void Data()
    {
        var data = Store.Expeditions;
        using var doc = JsonDocument.Parse(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Fixtures", "exp_tables.json")));
        var golden = doc.RootElement;
        var cats = golden.GetProperty("categories").EnumerateArray().ToList();
        Check(data.Categories.Count == 12 && cats.Count == 12 && data.Categories.Zip(cats).All(x => x.First.Name == x.Second.GetProperty("uniquename").GetString()
                  && x.First.GroupType == x.Second.GetProperty("grouptype").GetString() && x.First.MinItemPower == x.Second.GetProperty("minitempoweraverage").GetDouble())
              && data.CategoryAt(3) is { Name: "T3_STANDARD", GroupSize: 1, Rewards: [{ Kind: "silver", Amount: 1000 }], BonusRewards.Count: 3 }
              && data.CategoryAt(7)?.Name == "T4_VETERAN" && data.CategoryAt(10) is { Name: "T6_HARDCORE", Difficulties.Count: 18 }
              && data.CategoriesOf(4).SequenceEqual([3, 4, 5, 7, 8, 9, 10, 11]),
              "12 categories as golden (3 T3_STANDARD solo, 1000 silver, 3 bonus rewards; 7 T4_VETERAN; 10 T6_HARDCORE with 18 levels); agent 4 offers 3,4,5,7,8,9,10,11");
        var exps = golden.GetProperty("expeditions").EnumerateArray().ToList();
        var same = data.Expeditions.Count == exps.Count && data.Expeditions.Zip(exps).All(x => x.First.Name == x.Second.GetProperty("uniquename").GetString()
            && x.First.World == (x.Second.GetProperty("cluster").GetString() ?? "") && x.First.CategoryIndex == (x.Second.GetProperty("category_index").ValueKind == JsonValueKind.Number ? x.Second.GetProperty("category_index").GetInt32() : -1)
            && x.First.Timeout == x.Second.GetProperty("timeout").GetInt32() && x.First.BonusTimeout == x.Second.GetProperty("bonustimeout").GetInt32()
            && x.First.Steps.Count == x.Second.GetProperty("steps").GetArrayLength()
            && x.First.Steps.Zip(x.Second.GetProperty("steps").EnumerateArray()).All(s => s.First.Select(t => t.Count).SequenceEqual(s.Second.EnumerateArray().Select(t => t.GetProperty("count").GetInt32()))));
        var playable = data.Expeditions.Where(e => ExpeditionData.Playable(e, id => Store.World.Find(id) is not null)).ToList();
        Check(same && playable.Count == 26 && playable.Count == exps.Count(e => e.TryGetProperty("world", out _))
              && data.ExpeditionAt(3) is { Name: "T3_EXP_STD_HERETIC_MINE", World: "Mine-SOLO", Timeout: 3600, BonusTimeout: 3600 }
              && data.ExpeditionAt(20) is { Name: "T6_EXP_HRD_UNDEAD_RECRUITMENT", Timeout: 2600, BonusTimeout: 1730 },
              $"31 expeditions as golden (world cluster, category, times, steps and counts); 26 playable (got {playable.Count}); 3 Mine-SOLO 3600 s; 20 hardcore 2600/1730 s");

        // Mine-SOLO (§10): start, exits, narrators, checkpoints in world positions, 66 active spawn points.
        var mine = Store.Get("Mine-SOLO");
        var t = exps[3].GetProperty("world").GetProperty("templates")[0];
        var exits = mine.ExpeditionObjects.Where(o => o.Kind == "expeditionexit").ToList();
        var narrators = mine.ExpeditionObjects.Where(o => o.Kind == "expeditionnarrator").ToList();
        var checkpoints = mine.ExpeditionObjects.Where(o => o.Kind == "expeditioncheckpoint").ToList();
        Check(mine.Info.Type == "T3_EXPEDITION_STANDARD" && mine.Critical.Single(m => m.Type == "startpoint").Position == new Vector2(119, -170)
              && exits.Select(e => (e.Position, e.SpawnAtStartup)).OrderBy(e => e.SpawnAtStartup).SequenceEqual(t.GetProperty("exits").EnumerateArray().Select(e => (V(e.GetProperty("pos")), e.GetProperty("spawnatstartup").GetBoolean())).OrderBy(e => e.Item2))
              && narrators.Select(n => (n.Tile, n.Position, n.Direction)).SequenceEqual(t.GetProperty("narrators").EnumerateArray().Select(n => (n.GetProperty("tile").GetString()!, V(n.GetProperty("pos")), n.GetProperty("roty").GetSingle())))
              && checkpoints.Select(c => c.Position).SequenceEqual(t.GetProperty("checkpoints").EnumerateArray().Select(c => V(c.GetProperty("pos"))))
              && checkpoints[0].ColliderDistance == 25.02f && mine.MobSpawnPoints.Count == 66 && mine.MobSpawnPoints.All(p => p.Template is not null),
              $"Mine-SOLO: start (119, -170), exits (115, -165) start / (-184, 120) finish, the two narrators r90, 4 checkpoints, 66 bound spawn points (got {mine.MobSpawnPoints.Count})");

        // §4.3 spawn names mobs.xml lacks: aliases to the task mobs; the hardcore chest point is never a mob.
        var mobs = Store.Mobs;
        Check(ExpeditionData.ResolveSpawn(mobs, "T5_MOB_EXP_UNDEAD_GHOUL_VETERAN_BOSS")?.Name == "T5_MOB_EXP_UNDEAD_GHOUL_BOSS_VETERAN"
              && ExpeditionData.ResolveSpawn(mobs, "T6_MOB_EXP_HERETIC_GRENADE_VETERAN_MINIBOSS")?.Name == "T6_MOB_EXP_HERETIC_DYNAMITE_VETERAN_MINIBOSS"
              && ExpeditionData.ResolveSpawn(mobs, "LOOTCHEST_HCE_FINAL_REWARD") is null
              && Store.Get("SewersMaintenance-GROUP").MobSpawnPoints.Any(p => p.Template?.Name == "T5_MOB_EXP_UNDEAD_GHOUL_BOSS_VETERAN"),
              "spawn aliases: the SewersCrypt ghoul boss and the FistfulOfSilver dynamite miniboss; LOOTCHEST_HCE_FINAL_REWARD unbound; SewersMaintenance-GROUP spawns the boss");
        Check(Client.Items.Find("T4_LOOTBAG_EXPEDITION_ROYAL_SIGIL") is { CanCreate: true } && Client.Items.Find("T3_SKILLBOOK_NONTRADABLE") is { CanCreate: true }
              && ExpeditionSettings.Parse(File.ReadAllText(Path.Combine(DataDir(), "..", "content", "expeditions.json")), "expeditions.json") is var file
              && file with { EveryRunItems = ExpeditionSettings.Default.EveryRunItems, Substitutes = ExpeditionSettings.Default.Substitutes, SpawnAliases = ExpeditionSettings.Default.SpawnAliases } == ExpeditionSettings.Default
              && file.EveryRunItems.SequenceEqual(ExpeditionSettings.Default.EveryRunItems) && file.Substitutes.Count == 0 && file.SoloGroupCategories && !file.LeavePenalty
              && file.SpawnAliases.OrderBy(x => x.Key).SequenceEqual(ExpeditionData.DefaultSpawnAliases.OrderBy(x => x.Key))
              && ExpeditionSettings.Parse("""{"SpawnAliases": {"A_MOB": "B_MOB", "T6_MOB_EXP_HERETIC_TRAP_VETERAN": ""}}""", "t").SpawnAliases is { Count: 4 } merged
              && merged["A_MOB"] == "B_MOB" && !merged.ContainsKey("T6_MOB_EXP_HERETIC_TRAP_VETERAN"),
              "the bonus rewards are items this server creates (the royal-sigil lootbag, the T3 tome); content/expeditions.json holds the built-in values (solo group categories on, no leave penalty, the 4 aliases); SpawnAliases merges over them, \"\" drops one");
    }

    private static void Live()
    {
        var dir = NewDir();
        var characters = Repository(dir);
        var clock = new ManualClock();
        var world = new World(new ServerConfig { JoinByCharacterId = true }, characters, Client.Items, Client.GameData, Client.Spells)
        {
            Sync = new object(), Clock = clock, Clusters = Store, MapSeed = 3,
        };
        world.MobAi = false;
        void Tick(double seconds)
        {
            clock.Advance(TimeSpan.FromSeconds(seconds));
            lock (world.Sync) world.Update();
        }
        var city = Store.Get("0000");
        _ = Store.Get("Mine-SOLO"); // resolved: the instance's Join fills its map at once
        var agentSpot = city.ExpeditionAgents.Single(a => a.AgentId == 4).Position;
        var hero = StarterKit.Default.NewCharacter("X" + Guid.NewGuid().ToString("N")[..10]);
        (hero.Cluster, hero.PositionX, hero.PositionY) = ("0000", agentSpot.X + 1, agentSpot.Y);
        Check(characters.Create(hero), "character created");
        var (s, peer, _) = Join(world, hero.Id);
        var me = s.Player!;
        var agent = me.Map!.Objects.Values.OfType<ExpeditionAgent>().Single();
        OperationResponse Call(WorldSession session, FakePeer on, AlbionRequest req, int id)
        {
            lock (world.Sync) session.OnRequest(Request(req, id), encrypted: false);
            return on.Responses[^1];
        }
        List<EventData> Of(FakePeer on, EventCodes code, int from = 0) => [.. on.Events.Skip(from).Where(e => Code(e) == (int)code)];

        // Register (§3.2): an unknown category 15 → 10858, then T3_STANDARD with no expedition chosen → RC 0, 263 kind 1 and 5; again → 10856.
        var bad = Call(s, peer, new ExpeditionRegisterRequest { kr = agent.Id, ks = 99, kt = -1 }, 7);
        var good = Call(s, peer, new ExpeditionRegisterRequest { kr = agent.Id, ks = 3, kt = -1 }, 8);
        var infos = Of(peer, EventCodes.ExpeditionRegistrationInfo);
        var twice = Call(s, peer, new ExpeditionRegisterRequest { kr = agent.Id, ks = 3, kt = -1 }, 9);
        Check(bad.ReturnCode == 10858 && bad.Parameters[ReservedKeys.RequestId] is 7 && good is { ReturnCode: 0 } && good.Parameters[ReservedKeys.RequestId] is 8
              && good.Parameters.Keys.All(k => k >= 250) && twice.ReturnCode == 10856
              && infos is [var k1, var k5] && Int(k1, 3) == 1 && Int(k5, 3) == 5 && infos.All(e => Convert.ToInt64(e.Parameters[0]) == me.Id && e.Parameters[1] is "T3_STANDARD"
                  && Int(e, 2) == 3 && e.Parameters[5] is (byte)1 && Int(e, 10) == 3 && e.Parameters.ContainsKey(4)),
              $"register: 10858 for category 99, RC 0 (255 echoed, no body) and 263 kinds 1, 5 {{0 me, 1 T3_STANDARD, 2 3, 5 1, 10 3}}, then 10856 (got {bad.ReturnCode}/{good.ReturnCode}/{twice.ReturnCode}, {infos.Count} x 263)");

        // The Join channel (§2.5): 278 → kind 6; 279 → 265 false, kind 5; 278 again, 10 s → 265 true and the push {0: id, 1: Mine-SOLO}.
        Call(s, peer, new EnteringExpeditionStartRequest { wf = world.GameNow }, 10);
        Call(s, peer, new EnteringExpeditionCancelRequest { wf = world.GameNow }, 11);
        var cancelled = Of(peer, EventCodes.EnteringExpeditionCancel);
        Check(Of(peer, EventCodes.ExpeditionRegistrationInfo).Skip(2).Select(e => Int(e, 3)).SequenceEqual([6, 5]) && cancelled is [var c0] && c0.Parameters.GetValueOrDefault((byte)1) is null or false,
              "278: 263 kind 6; 279: 265 {1: false} and kind 5 again");
        Call(s, peer, new EnteringExpeditionStartRequest { wf = world.GameNow }, 12);
        Tick(9.5);
        Check(world.Expeditions.Instances.Count == 0, "no instance before the 10 s");
        var before = peer.Responses.Count;
        Tick(0.6);
        var push = peer.Responses.Skip(before).SingleOrDefault();
        var instance = world.Expeditions.Instances.SingleOrDefault();
        Check(push is { ReturnCode: 0 } && !push.Parameters.ContainsKey(ReservedKeys.RequestId) && push.Parameters[0] is string id && Regex.IsMatch(id, "^@EXPEDITION@[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}$")
              && push.Parameters[1] is "Mine-SOLO" && push.Parameters.Keys.Where(k => k < 250).Order().SequenceEqual(new byte[] { 0, 1 }) && instance?.Id == id
              && Of(peer, EventCodes.EnteringExpeditionCancel) is [_, var done] && done.Parameters[1] is true && s.Status == SessionStatus.Transferring,
              $"10 s later 265 {{1: true}}, then the push {{0: @EXPEDITION@<guid>, 1: Mine-SOLO}} without 255 (got {string.Join(",", push?.Parameters.Keys.Order() ?? Enumerable.Empty<byte>())})");

        // The instance's Join (§2.6-§2.8): key 8 the id at the start point; 251 x2, 252 x2, 315 x4; then 263 kind 7, 260 before 261.
        var (s2, peer2, join) = Join(world, hero.Id);
        me = s2.Player!;
        var map = me.Map!;
        var exitEvents = Of(peer2, EventCodes.NewExpeditionExit);
        var narratorEvents = Of(peer2, EventCodes.NewExpeditionNarrator);
        var cps = Of(peer2, EventCodes.NewExpeditionCheckPoint);
        var full = peer2.Events.FindIndex(e => Code(e) == (int)EventCodes.FullExpeditionInfo);
        var progress = peer2.Events.FindIndex(e => Code(e) == (int)EventCodes.ExpeditionQuestProgressInfo);
        var first = progress >= 0 ? peer2.Events[progress] : null;
        Check(join.a8i == instance!.Id && join.a8j == new Vector2(119, -170) && map.Template?.Id == "Mine-SOLO" && map.NoRespawn
              && exitEvents.Count == 2 && exitEvents.Count(e => e.Parameters.GetValueOrDefault((byte)4) is true) == 1 && exitEvents.All(e => e.Parameters[3] is "ExpeditionExit")
              && narratorEvents.Select(e => (e.Parameters[3], e.Parameters.GetValueOrDefault((byte)4) is true)).Order().SequenceEqual([("ExpeditionNarrator", false), ("ExpeditionNarratorFinish", true)])
              && cps.Count == 4 && cps.All(e => e.Parameters[1] is float[] { Length: 2 } && Int(e, 2) == 0)
              && Of(peer2, EventCodes.ExpeditionRegistrationInfo) is [var inside] && Int(inside, 3) == 7
              && full >= 0 && full < progress && Convert.ToInt64(peer2.Events[full].Parameters[0]) == me.Id && Int(peer2.Events[full], 1) == 3
              && Convert.ToInt64(peer2.Events[full].Parameters[2]) == instance.StartTicks
              && first is not null && Int(first, 2) == 0 && first.Parameters[3] is "[[0],[0],[0]]" && first.Parameters[4] is bool[] { Length: 3 } f0 && !f0.Any(x => x)
              && Int(first, 5) == -1 && Int(first, 6) == -1,
              $"Join of {instance.Id} at (119, -170): 2 exits (one start), the two narrators, 4 checkpoints in state 0; 263 kind 7, 260 {{0 me, 1 3, 2 start}} then 261 \"[[0],[0],[0]]\" (got {join.a8i} {join.a8j}, {exitEvents.Count}/{narratorEvents.Count}/{cps.Count})");

        // Kills (§5): 4 archers → [[4],[0],[0]] [true, false, false], 5/6 = 0/0; a 5th changes nothing.
        Mob[] Mobs(string name) => [.. map.Objects.Values.OfType<Mob>().Where(m => m.Template.Name == name && !m.Dead)];
        void Kill(Mob m)
        {
            lock (world.Sync) Combat.Damage(me, m, 1e9, EffectType.Physical, HitOrigin.Direct, 0, world.GameNow);
        }
        var archers = Mobs("T2_MOB_EXP_HERETIC_ARCHER");
        foreach (var m in archers.Take(4)) Kill(m);
        var afterFour = Of(peer2, EventCodes.ExpeditionQuestProgressInfo);
        Kill(archers[4]);
        var last = afterFour[^1];
        Check(archers.Length == 5 && afterFour.Count == 5 && last.Parameters[3] is "[[4],[0],[0]]" && last.Parameters[4] is bool[] f1 && f1.SequenceEqual([true, false, false])
              && Int(last, 5) == 0 && Int(last, 6) == 0 && Of(peer2, EventCodes.ExpeditionQuestProgressInfo).Count == 5,
              $"4 archers: 261 [[4],[0],[0]] [true, false, false] 5/6 0/0; a 5th sends nothing (got {afterFour.Count}: {last.Parameters[3]})");
        Tick(30);
        Check(Mobs("T2_MOB_EXP_HERETIC_ARCHER").Length == 0, "no respawn in an instance: 30 s later the archers are still gone");

        // Checkpoints (§2.7.3): 280 on the first → 315 state 1; on the second → state 2 for the first, 1 for the second.
        var points = map.Objects.Values.OfType<ExpeditionCheckPoint>().ToList();
        void Move(WorldSession session, Vector2 to)
        {
            lock (world.Sync) session.OnRequest(Request(new MoveRequest { wf = DateTime.UtcNow.Ticks, v6 = [to.X, to.Y], v8 = [to.X, to.Y], v9 = 0f }, null), encrypted: false);
        }
        var mark = peer2.Events.Count;
        Move(s2, points[0].Position);
        Call(s2, peer2, new ActivateExpeditionCheckPointRequest { ai = points[0].Id }, 20);
        Move(s2, points[1].Position);
        Call(s2, peer2, new ActivateExpeditionCheckPointRequest { ai = points[1].Id }, 21);
        var states = Of(peer2, EventCodes.NewExpeditionCheckPoint, mark).Select(e => (Convert.ToInt64(e.Parameters[0]), Int(e, 2))).ToList();
        Check(states.SequenceEqual([(points[0].Id, 1), (points[0].Id, 2), (points[1].Id, 1)]) && instance.Active == points[1],
              $"280: checkpoint 1 → 1; checkpoint 2 → 2 for the first, 1 for the second (got {string.Join(" ", states)})");

        // A death (§6): the respawn pushes into the same instance {0: id, 1: Mine-SOLO}, at the active checkpoint.
        lock (world.Sync)
        {
            Knockdown.Start(me, me.Id, "test", world.GameNow, KnockdownKind.Dead);
            Check(Death.Respawn(me, "test"), "respawn");
        }
        var again = peer2.Responses[^1];
        var (s3, peer3, rejoin) = Join(world, hero.Id);
        me = s3.Player!;
        Check(again.Parameters[0] is string rid && rid == instance.Id && again.Parameters[1] is "Mine-SOLO" && rejoin.a8i == instance.Id && rejoin.a8j == points[1].Position
              && Of(peer3, EventCodes.FullExpeditionInfo).Count == 1 && Of(peer3, EventCodes.NewExpeditionCheckPoint).Any(e => Convert.ToInt64(e.Parameters[0]) == points[1].Id && Int(e, 2) == 1),
              $"dead: the push {{0: the id, 1: Mine-SOLO}}, the Join at the active checkpoint with 260 again and its state 1 (got {rejoin.a8j})");

        // The finish exit is shut before the completion; the mages and the boss complete it with the bonus (§5.1).
        var finishExit = map.Objects.Values.OfType<ExpeditionExit>().Single(e => !e.Start);
        Move(s3, finishExit.Position);
        Call(s3, peer3, new ExitEnterStartRequest { wf = world.GameNow, kq = finishExit.Id }, 30);
        Check(Of(peer3, EventCodes.ExitEnterCancel).Count == 1, "the finish exit before the completion: 254");
        var silver = me.Character.Silver;
        foreach (var m in Mobs("T2_MOB_EXP_HERETIC_MAGE").Take(5).Concat(Mobs("T2_MOB_EXP_HERETIC_OVERSEER_BOSS"))) Kill(m);
        var end = Of(peer3, EventCodes.ExpeditionQuestProgressInfo)[^1];
        var bag = me.Inventory.Bag.Items.Select(i => i.Template.Name).ToList();
        Check(instance.Finished && end.Parameters[4] is bool[] f2 && f2.All(x => x) && f2.Length == 3 && me.Character.Silver - silver == 3000 * 10000
              && bag.Contains("T4_LOOTBAG_EXPEDITION_ROYAL_SIGIL") && bag.Contains("T3_SKILLBOOK_NONTRADABLE") && me.Character.Expeditions is { Last: "T3_EXP_STD_HERETIC_MINE", Bonus.Count: 1 }
              && !world.Expeditions.BonusAvailable(me.Character, instance.Expedition),
              $"5 mages and the boss: 261 all true, the bonus rewards (+3000 silver, the royal-sigil lootbag, the T3 tome), the daily bonus taken (got {(me.Character.Silver - silver) / 10000})");

        // Out through the finish exit (§2.10): no response, 5 s, 255, 263 kind 3, the push {0: 0000}; the Join at the return point.
        mark = peer3.Events.Count;
        var answers = peer3.Responses.Count;
        lock (world.Sync) s3.OnRequest(Request(new ExitEnterStartRequest { wf = world.GameNow, kq = finishExit.Id }, 31), encrypted: false);
        var noAnswer = peer3.Responses.Count == answers; // any 237 response would end the client's 5 s bar at once
        Tick(5.1);
        var outPush = peer3.Responses[^1];
        Check(noAnswer && Of(peer3, EventCodes.ExitEnterCancel, mark).Count == 0 && Of(peer3, EventCodes.ExitEnterFinished, mark).Count == 1
              && Of(peer3, EventCodes.ExpeditionRegistrationInfo, mark) is [var ended] && Int(ended, 3) == 3 && outPush.Parameters[0] is "0000" && !outPush.Parameters.ContainsKey(1),
              "the finish exit after the completion: no response, 5 s later 255, 263 kind 3 and the push {0: 0000}");
        var (s4, peer4, home) = Join(world, hero.Id);
        Check(home.a8i == "0000" && Vector2.Distance(home.a8j, agentSpot) < 2 && !instance.Members.ContainsKey(hero.Id), $"back in Thetford in front of the agent (got {home.a8j})");

        // A second run: a reward into a full bag waits (Unclaimed); a relog inside (§10): SelectCharacter key 1 the id, key 2 Mine-SOLO, and
        // the Join gives the waiting item once.
        ExpeditionInstance late;
        lock (world.Sync)
        {
            late = world.Expeditions.Create(instance.Expedition, instance.Category);
            world.Expeditions.Send(s4.Player!, late, new DungeonReturn("0000", agentSpot.X + 1, agentSpot.Y, 0), "test");
        }
        var (s5, _, _) = Join(world, hero.Id);
        var sword = Client.Items.Find("T4_MAIN_SWORD")!;
        string Lootbags(Player p) => string.Join(",", p.Inventory.Bag.Items.Where(x => x.Template.Name == "T4_LOOTBAG_EXPEDITION_ROYAL_SIGIL").Select(x => x.Count));
        lock (world.Sync)
        {
            var bag5 = s5.Player!.Inventory.Bag;
            var fill = Enumerable.Range(0, bag5.Size).Where(n => bag5[n] is null).ToList();
            foreach (var n in fill) bag5.Put(n, new Item(world.ObjectIds.Next(), sword, 1, 1, 0, [], []));
            world.Expeditions.Reward(s5.Player!, late);
            foreach (var n in fill) bag5.Take(n);
        }
        var bags = Lootbags(s5.Player!);
        Check(s5.Player!.Character.Expeditions is { Unclaimed: [{ Name: "T4_LOOTBAG_EXPEDITION_ROYAL_SIGIL", Count: 1 }] }, "a full bag: the reward lootbag waits in Unclaimed");
        lock (world.Sync) s5.OnDisconnected("logout");
        var loginPeer = new FakePeer();
        var login = new LoginSession(loginPeer, world.Config, characters, StarterKit.Default) { World = world };
        lock (world.Sync)
        {
            login.OnRequest(Request(TestLogin(), 1), encrypted: true);
            login.OnRequest(Request(new SelectCharacterRequest { abc = hero.Name }, null), encrypted: true);
        }
        var select = new SelectCharacterResponse();
        AlbionSerializer.FillFields(select, loginPeer.Responses[^1].Parameters);
        var (s6, peer6, back6) = Join(world, hero.Id);
        Check(select.blo == late.Id && select.blp == "Mine-SOLO" && back6.a8i == late.Id && s6.Player!.Character.Expeditions is { Unclaimed.Count: 0 }
              && Lootbags(s6.Player!) == (bags.Length == 0 ? "1" : bags + ",1"),
              $"relog inside: SelectCharacter {{1: the id, 2: Mine-SOLO}}, the Join into the instance, the waiting lootbag given once (got {select.blo}/{select.blp}, {Lootbags(s6.Player!)})");

        // The run times out (§7) with the player dead inside: out to the way back as a respawn (263 kind 3, no reward), the instance closed.
        silver = s6.Player!.Character.Silver;
        lock (world.Sync) Knockdown.Start(s6.Player!, s6.Player!.Id, "test", world.GameNow, KnockdownKind.Dead);
        Tick(3601);
        var (_, peer7, home7) = Join(world, hero.Id);
        Check(late.Closed && !world.Expeditions.IsLive(late.Id) && peer6.Responses[^1].Parameters[0] is "0000" && world.LoadCharacter(hero.Id)!.Silver == silver
              && Of(peer6, EventCodes.ExpeditionRegistrationInfo).Select(e => Int(e, 3)).SequenceEqual([7, 3])
              && home7.a8i == "0000" && Of(peer7, EventCodes.Respawn).Count == 1,
              "after 3600 s, dead: failed, pushed out to 0000 with 263 kind 3, no reward, the instance closed; the Join there is the respawn (88)");

        // A character saved in an instance that is gone (§7): back at its way back, which is cleared.
        var gone = world.LoadCharacter(hero.Id)!.Clone();
        (gone.Cluster, gone.DungeonReturn) = (late.Id, new DungeonReturn("0000", 10, 20, 0));
        bool returned;
        lock (world.Sync) returned = world.Expeditions.ReturnIfClosed(gone);
        Check(returned && gone is { Cluster: "0000", PositionX: 10, PositionY: 20, DungeonReturn: null }, "ReturnIfClosed: a closed instance id → its way back, cleared");
        world.Dispose();
        characters.Dispose();
        try { Directory.Delete(dir, true); } catch (IOException) { }
    }

    /// <summary>
    /// PVE-DESIGN step 9 (SPEC §3.3, §12): two players without a party queue T4_VETERAN into one group (not when they chose different
    /// expeditions); as a party, auto-accepted (no 262), and with PartyAutoAccept off the leader's
    /// registration invites the member (262, 263 kind 2), 273 makes both ready, both go into one instance scaled for 2 (health and damage
    /// × 0.70); a hardcore level 2 is refused (a62 21) until a D2 token opens it, its instance carries MOB_HARDCORE_EXPEDITION_BUFF_02, and
    /// its completion pays × 2.516, gives a D3 token, takes the D2 token and opens level 3.
    /// </summary>
    private static void Groups()
    {
        var dir = NewDir();
        var characters = Repository(dir);
        var clock = new ManualClock();
        var world = new World(new ServerConfig { JoinByCharacterId = true }, characters, Client.Items, Client.GameData, Client.Spells)
        {
            Sync = new object(), Clock = clock, Clusters = Store, MapSeed = 5,
        };
        world.MobAi = false;
        void Tick(double seconds)
        {
            clock.Advance(TimeSpan.FromSeconds(seconds));
            lock (world.Sync) world.Update();
        }
        var data = Store.Expeditions;
        foreach (var e in data.Expeditions.Where(e => e.CategoryIndex is 7 or 10 && ExpeditionData.Playable(e, id => Store.World.Find(id) is not null))) _ = Store.Get(e.World);
        _ = Store.Loot; // the token lists
        var agentSpot = Store.Get("0000").ExpeditionAgents.Single(a => a.AgentId == 4).Position;
        (WorldSession S, FakePeer Peer) Hero(float dx)
        {
            var c = StarterKit.Default.NewCharacter("X" + Guid.NewGuid().ToString("N")[..10]);
            (c.Cluster, c.PositionX, c.PositionY) = ("0000", agentSpot.X + dx, agentSpot.Y);
            Check(characters.Create(c), "character created");
            var (s, peer, _) = Join(world, c.Id);
            return (s, peer);
        }
        var (s1, peer1) = Hero(1);
        var (s2, peer2) = Hero(-1);
        var (a, b) = (s1.Player!, s2.Player!);
        var agent = a.Map!.Objects.Values.OfType<ExpeditionAgent>().Single();
        OperationResponse Call(WorldSession session, FakePeer on, AlbionRequest req)
        {
            lock (world.Sync) session.OnRequest(Request(req, 40), encrypted: false);
            return on.Responses[^1];
        }
        List<EventData> Of(FakePeer on, EventCodes code, int from = 0) => [.. on.Events.Skip(from).Where(e => Code(e) == (int)code)];
        List<int> Kinds(FakePeer on, int from) => [.. Of(on, EventCodes.ExpeditionRegistrationInfo, from).Select(e => Int(e, 3))];
        var exps = world.Expeditions;

        // The queue (§3.3 [design]): both register alone within GroupQueueSeconds → one group, one expedition, both ready.
        var (m1, m2) = (peer1.Events.Count, peer2.Events.Count);
        var q1 = Call(s1, peer1, new ExpeditionRegisterRequest { kr = agent.Id, ks = 7, kt = -1 });
        Tick(1);
        var q2 = Call(s2, peer2, new ExpeditionRegisterRequest { kr = agent.Id, ks = 7, kt = -1 });
        Tick(4.5);
        var (ra, rb) = (exps.RegistrationOf(a.Character.Id), exps.RegistrationOf(b.Character.Id));
        Check(ra is { State: ExpeditionState.Ready } && rb is { State: ExpeditionState.Ready } && ra.Group is { Members.Count: 2 } && ra.Group == rb.Group && ra.Expedition == rb.Expedition
              && Kinds(peer1, m1).Contains(5) && Of(peer2, EventCodes.ExpeditionRegistrationInfo, m2).LastOrDefault(e => Int(e, 3) == 5) is { } r5 && Int(r5, 10) == ra.Expedition.Index,
              $"queue: two lone T4_VETERAN registrations 1 s apart → one group of 2, the same expedition, both 263 kind 5 (got {q1.ReturnCode}/{q2.ReturnCode}, {ra?.State}/{rb?.State})");
        Call(s1, peer1, new ExpeditionRegisterCancelRequest());
        Call(s2, peer2, new ExpeditionRegisterCancelRequest());
        // A pinned expedition (chosen, or opened by a token) is not merged into a group of another one.
        var vets = data.Expeditions.Where(e => e.CategoryIndex == 7 && exps.Playable(e)).Take(2).ToList();
        Call(s1, peer1, new ExpeditionRegisterRequest { kr = agent.Id, ks = 7, kt = vets[0].Index });
        Call(s2, peer2, new ExpeditionRegisterRequest { kr = agent.Id, ks = 7, kt = vets[1].Index });
        Tick(5.5);
        Check(exps.RegistrationOf(a.Character.Id) is { Group.Members.Count: 1 } pa && pa.Expedition == vets[0] && exps.RegistrationOf(b.Character.Id) is { Group.Members.Count: 1 } pb && pb.Expedition == vets[1],
              "queue: two registrations that chose different expeditions stay two groups");
        Call(s1, peer1, new ExpeditionRegisterCancelRequest());
        Call(s2, peer2, new ExpeditionRegisterCancelRequest());

        // The party (§3.3), PartyAutoAccept (the default): the leader's registration registers the member near the agent, both ready at once.
        lock (world.Sync)
            Check(world.Parties.Invite(a, b.Character.Id, null, out _) == 0 && world.Parties.AnswerInvitation(b, true, false, 0) == 0, "party of two");
        (m1, m2) = (peer1.Events.Count, peer2.Events.Count);
        Call(s1, peer1, new ExpeditionRegisterRequest { kr = agent.Id, ks = 7, kt = -1 });
        Check(Of(peer2, EventCodes.InvitedToExpedition, m2).Count == 0 && Kinds(peer2, m2).SequenceEqual([1, 8, 5, 8])
              && exps.RegistrationOf(b.Character.Id) is { State: ExpeditionState.Ready, Group.FromParty: true },
              $"auto-accept: no 262, the member gets 263 kinds 1, 8, 5, 8 (got {string.Join(",", Kinds(peer2, m2))})");
        Call(s1, peer1, new ExpeditionRegisterCancelRequest());
        Call(s2, peer2, new ExpeditionRegisterCancelRequest());

        // PartyAutoAccept off: the leader's registration invites the member near the agent; 273 → both ready; both go into one instance for 2.
        lock (world.Sync) world.ExpeditionSettings = ExpeditionSettings.Default with { PartyAutoAccept = false };
        (m1, m2) = (peer1.Events.Count, peer2.Events.Count);
        var reg = Call(s1, peer1, new ExpeditionRegisterRequest { kr = agent.Id, ks = 7, kt = -1 });
        var invited = Of(peer2, EventCodes.InvitedToExpedition, m2);
        Check(reg.ReturnCode == 0 && invited is [var inv] && inv.Parameters[0] as string == a.Character.Name && Int(inv, 4) == exps.RegistrationOf(a.Character.Id)!.Expedition.Index
              && Kinds(peer2, m2).SequenceEqual([2, 8]) && Kinds(peer1, m1).SequenceEqual([1, 8]) && exps.RegistrationOf(b.Character.Id) is { State: ExpeditionState.Invited },
              $"the leader registers T4_VETERAN: 262 {{0 leader, 4 expedition}} and 263 kinds 2, 8 to the member; kinds 1, 8 to the leader (got {string.Join(",", Kinds(peer2, m2))} / {string.Join(",", Kinds(peer1, m1))})");
        Call(s2, peer2, new JoinExpeditionRequest());
        Check(Kinds(peer2, m2).Contains(5) && Kinds(peer1, m1).Contains(5) && exps.RegistrationOf(a.Character.Id) is { State: ExpeditionState.Ready } r1 && r1.Group!.Members.Count == 2,
              "273: the member accepts, both 263 kind 5");
        Call(s1, peer1, new EnteringExpeditionStartRequest { wf = world.GameNow });
        Call(s2, peer2, new EnteringExpeditionStartRequest { wf = world.GameNow });
        Tick(10.1);
        var instance = exps.Instances.SingleOrDefault();
        Check(instance is { Players: 2, Members.Count: 2 } && Math.Abs(instance.HealthFactor - 0.70) < 1e-9 && Math.Abs(instance.DamageFactor - 0.70) < 1e-9
              && peer1.Responses[^1].Parameters[0] as string == instance.Id && peer2.Responses[^1].Parameters[0] as string == instance.Id,
              $"both pushed into one instance made for 2, mobs health and damage x0.70 (got {exps.Instances.Count} instance(s), {instance?.Players}, x{instance?.HealthFactor})");
        var (sa, _, _) = Join(world, a.Character.Id);
        var map = sa.Player!.Map!;
        var plain = map.Objects.Values.OfType<Mob>().Where(m => m.Effects.Count == 0).ToList();
        Check(plain.Count > 0 && plain.All(m => m.HealthFactor == instance!.HealthFactor && m.DamageFactor == instance.DamageFactor
                  && Math.Abs(m.Health.Max - MathF.Round(m.Template.HitPointsMax * 0.70f)) <= 1),
              $"the instance's mobs: factor 0.70 on health and damage, max health = 0.70 x hitpointsmax ({plain.Count} mobs)");
        // The completion while the second member is still loading: its rewards come with its Join, once.
        var silverB = b.Character.Silver;
        lock (world.Sync) exps.Finish(instance!);
        var (sb, _, _) = Join(world, b.Character.Id);
        var after = sb.Player!.Character.Silver;
        var (sb2, _, _) = Join(world, b.Character.Id);
        var paid = after - silverB;
        Check(paid > 0 && sb2.Player?.Character.Silver == after && instance!.Owed.Count == 0,
              $"a member not on the map at the completion is paid at its Join, once (got {paid / 10000})");
        lock (world.Sync) exps.Close(instance!, "test");

        // The hardcore ladder (§12.2): level 2 is locked (a62 21 → 10864); a D2 token opens it.
        var (s3, peer3) = Hero(0.5f);
        var h = s3.Player!;
        var locked = Call(s3, peer3, new ExpeditionRegisterRequest { kr = agent.Id, ks = 10, kt = -1, ku = 2 });
        var recruitment = data.ExpeditionAt(20)!;
        var token = Client.Items.Find(Expeditions.TokenName(2, recruitment))!;
        lock (world.Sync) h.Inventory.Bag.Put(Enumerable.Range(0, h.Inventory.Bag.Size).First(n => h.Inventory.Bag[n] is null), new Item(world.ObjectIds.Next(), token, 1, 1, 0, [], []));
        var open = Call(s3, peer3, new ExpeditionRegisterRequest { kr = agent.Id, ks = 10, kt = -1, ku = 2 });
        Check(locked.ReturnCode == 10864 && open.ReturnCode == 0 && exps.RegistrationOf(h.Character.Id) is { Difficulty: 2, Token: not null } hr && hr.Expedition == recruitment,
              $"hardcore level 2: 10864 without a token; with QUESTITEM_EXP_TOKEN_D2_T6_EXP_HRD_UNDEAD_RECRUITMENT accepted for that expedition (got {locked.ReturnCode}/{open.ReturnCode})");
        Tick(5.1);
        Call(s3, peer3, new EnteringExpeditionStartRequest { wf = world.GameNow });
        Tick(10.1);
        var hard = exps.Instances.SingleOrDefault(i => i.Difficulty == 2);
        var (sh, _, _) = Join(world, h.Character.Id);
        h = sh.Player!;
        string[] Bag() => [.. h.Inventory.Bag.Items.Select(i => i.Template.Name)];
        Check(hard is { Players: 1 } && Math.Abs(hard.HealthFactor - 0.55) < 1e-9 && h.Map!.Difficulty is { MobModifier: "MOB_HARDCORE_EXPEDITION_BUFF_02" }
              && h.Map.Objects.Values.OfType<Mob>().Any(m => m.LevelBuff?.Name == "MOB_HARDCORE_EXPEDITION_BUFF_02") && !Bag().Contains(token.Name),
              $"alone after the queue: a level 2 instance for 1 (x0.55), the mobs buffed with MOB_HARDCORE_EXPEDITION_BUFF_02, the token taken (got {hard?.Players}, {h.Map?.Difficulty?.MobModifier})");
        var silver = h.Character.Silver;
        lock (world.Sync) exps.Finish(hard!);
        Check(h.Character.Silver - silver == (long)Math.Round(50000 * 10000 * 2.516) && Bag().Any(n => n.StartsWith("QUESTITEM_EXP_TOKEN_D3_", StringComparison.Ordinal))
              && h.Character.Expeditions is { Hardcore: 3 } && exps.HardcoreLevel(h.Character, hard!.Category) == 3,
              $"level 2 completed in the bonus time: 50000 x 2.516 silver, a D3 token, level 3 open (got {(h.Character.Silver - silver) / 10000})");
        world.Dispose();
        characters.Dispose();
        try { Directory.Delete(dir, true); } catch (IOException) { }
    }

    private static void Leftovers()
    {
        var dir = NewDir();
        var characters = Repository(dir);
        var clock = new ManualClock();
        var world = new World(new ServerConfig { JoinByCharacterId = true }, characters, Client.Items, Client.GameData, Client.Spells)
        {
            Sync = new object(), Clock = clock, Clusters = Store, MapSeed = 9, LoadCluster = id => Task.FromResult(Store.Get(id)),
        };
        world.MobAi = false;
        void Tick(double seconds)
        {
            clock.Advance(TimeSpan.FromSeconds(seconds));
            lock (world.Sync) world.Update();
        }
        var elite = Store.Get("2326"); // T6 black zone with ELITE markers
        var marker = elite.DungeonMarkers.First(m => m.Type == "ELITE");
        var c = StarterKit.Default.NewCharacter("X" + Guid.NewGuid().ToString("N")[..10]);
        (c.Cluster, c.PositionX, c.PositionY) = ("2326", marker.Position.X + 3, marker.Position.Y);
        c.Inventory = [new InventoryItem("T6_RANDOM_DUNGEON_ELITE_TOKEN_D1", 1)];
        Check(characters.Create(c), "character created");
        var (s, peer, _) = Join(world, c.Id);
        var me = s.Player!;

        // Level 18 (§3.1): the instance, the player in it, the completion: the bonus chest at the spawn point.
        var data = Store.Expeditions;
        var cat = data.CategoryAt(10)!;
        var e = data.ExpeditionAt(20)!;
        var template = Store.Get(e.World);
        var point = template.LootChests.FirstOrDefault(p => p.SpawnBehaviour == Expeditions.BonusChestSpawnpoint);
        var exps = world.Expeditions;
        ExpeditionInstance instance;
        lock (world.Sync)
        {
            instance = exps.Create(e, cat, 18);
            exps.Send(me, instance, new DungeonReturn("2326", c.PositionX, c.PositionY, 0), "test");
        }
        Tick(0.2);
        (s, peer, _) = Join(world, c.Id);
        me = s.Player!;
        Tick(0.2);
        lock (world.Sync) exps.Finish(instance);
        var chest = me.Map?.Objects.Values.OfType<LootChest>().FirstOrDefault();
        Check(cat.Name == "T6_HARDCORE" && Expeditions.LevelOf(instance) is { BonusChest: true } && point is not null && me.Map?.Cluster == instance.Id
              && chest is not null && chest.Template.Name == "HARCORE_EXPEDITION_LVL_18" && Vector2.Distance(chest.Position, point.Position) < 0.01f,
              $"{e.Name} level 18 completed: spawnbonuslootchest → the HCE_FINAL_REWARD chest at {point?.Position} (got {chest?.Template.Name} at {chest?.Position})");
        lock (world.Sync) exps.Close(instance, "test");
        Tick(0.2);

        // The Avalonian elite map (§3.2): used in 2326, its exit within 0-3 clusters of the same archetype (only clusters already read).
        (s, peer, _) = Join(world, c.Id);
        me = s.Player!;
        var map = me.Inventory.Bag.Items.First(i => i.Template.Name == "T6_RANDOM_DUNGEON_ELITE_TOKEN_D1");
        lock (world.Sync) s.OnRequest(Request(new UseConsumableFromInventoryRequest { ah5 = map.Id, ah6 = 1 }, 30), encrypted: false);
        for (var i = 0; i < 5; i++) Tick(0.2);
        var exits = world.Maps.Maps.SelectMany(world.Dungeons.ExitsOf).Where(x => x.FromMap).ToList();
        Check(peer.Responses[^1].ReturnCode == 0 && exits is [{ Name: "T6_AVALON_ELITE_CONSUMABLE", Level: 1, Marker.Type: "ELITE" } x0] && map.Count == 0
              && x0.Info.Dungeons is [("AVALON_RANDOM_DUNGEON_ELITE", 6, _)],
              $"T6_RANDOM_DUNGEON_ELITE_TOKEN_D1 used: T6_AVALON_ELITE_CONSUMABLE (AVALON_RANDOM_DUNGEON_ELITE, level 1) on an ELITE marker (got RC {peer.Responses[^1].ReturnCode}, {string.Join("; ", exits.Select(x => $"{x.Name} L{x.Level} {x.Marker?.Type} in {x.Map?.Cluster}"))})");
        world.Dispose();
        characters.Dispose();
        try { Directory.Delete(dir, true); } catch (IOException) { }
    }

    private static (WorldSession Session, FakePeer Peer, JoinResponse Join) Join(World world, Guid key)
    {
        var peer = new FakePeer();
        var session = new WorldSession(peer, world);
        lock (world.Sync) session.OnRequest(Request(new JoinRequest { ss = key }, null), encrypted: true);
        Check(peer.Responses is [{ ReturnCode: 0 }], "join answered");
        var join = new JoinResponse();
        AlbionSerializer.FillFields(join, peer.Responses[0].Parameters);
        return (session, peer, join);
    }
}
