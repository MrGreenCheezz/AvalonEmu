using System.Numerics;
using System.Text.RegularExpressions;
using AlbionLocal.Photon.Serialization;
using AlbionLocal.Server;
using AlbionLocal.Server.Bots;
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
/// Hellgates, phase A (analysis/work/stageL/hellgates/SPEC.md §6, "SPEC" here): hellgate.xml's modes and Thetford's Hell Den shrine (400 per
/// preset); then, on FakePeer sessions with a manual clock, a PvE run of hellgate_2v2_non_lethal on HELLGATE-06-2v2-01 for a party of two with one
/// at the shrine: op 255's range, 273/401 (to the party only), 274, a client cancel during the lock, the push {0, 1}, the start point, the
/// tracker (454, 453, 455), the scaling, the guardian, the chests, the exit portal, slot 21, the escape and the closing; a versus run of
/// hellgate_2v2 with its bot team (Hostile to the party alone), its pvp chest and REWARD chests, a wipe, and a team gone unbeaten. Phase B (SPEC §7):
/// the chain and the win's infamy; under the official preset the party-vs-party match, the spawn buffs and the restricted area.
/// </summary>
public static class HellgateTest
{
    public static IEnumerable<(string, Action)> All =>
    [
        ("hellgates: hellgate.xml (5 modes, id 3 = hellgate_2v2, weights 200), 0000-HellDen's shrine at (760, 130), 400 keys 0-6 under pve (fixed, the mode) and official (0), Hellgates:* and content/hellgates.json", Data),
        ("hellgates: PvE 2v2 on HELLGATE-06: 255 at 20 m RC 1, at 2 m 273 and 401 to the party only, 274, a cancel in the lock ignored, push {0, 1}, start A, 454/453, x0.786, points, 455 2-4, the guardian, BOSS and objective chests, 250, slot 21, the escape, closed", Run),
        ("hellgates: versus 2v2: another mode refused, 2 bots T6 at start B Hostile to the party alone, beaten: 455 3, a HELL_HRD_PVP_NORMAL chest, the REWARD chests open; a wipe: 455 6, nothing lost; a team gone unbeaten: the guardian", Versus),
        ("hellgates B: phase B keys per preset, bct.af, the H2 milestones, HELLGATE-06's area; the win's 446 (+100), the claim's 402 and chain exit, its walk-in: the next instance chain 1 x bct.af(1), the first way back; 446 again", Chain),
        ("hellgates B: official 2v2: a second party matched at start B, spawn buffs, the two Hostile, 452 at +90 s (100 -> 30 m at 0.3 m/s, 70 m 100 s later), the second party down: 455 6 to it, 3 to the winners, 452 off, its chest refused to the losers (also via RegisterToObject after a winner opened it), the BOSS chest open, HELL_2V2_ENTER gone outside", Match),
    ];

    private static ClusterStore Store => Client.Clusters!;

    private const string Map06 = "HELLGATE-06-2v2-01", Map06Hrd = "HELLGATE-06-2v2-01-HRD";

    private static readonly Vector2 Shrine = new(760, 130);

    private static void Check(bool cond, string what) => AlbionTests.Check(cond, what);

    private static int Code(EventData e) => Convert.ToInt32(e.Parameters.GetValueOrDefault(ReservedKeys.EventCode));

    private static int Key(EventData e, byte key) => Convert.ToInt32(e.Parameters.GetValueOrDefault(key) ?? 0);

    private static List<EventData> Of(FakePeer on, EventCodes code, int from = 0) => [.. on.Events.Skip(from).Where(e => Code(e) == (int)code)];

    private static void Data()
    {
        var data = Store.Hellgates;
        Check(data.Modes.Select(m => m.Name).SequenceEqual(["hellgate_generic", .. HellgateOptions.AllModes]) && data.At(3) is { Name: "hellgate_2v2", TeamSize: 2, ItemPower: 900, PointsRequirement: 500, Lethal: true }
              && data.At(1) is { PointsRequirement: 100, PveChest: "HELL_STD_PVE_NORMAL", Lethal: false } && data.MapsOf(data.At(3)!).Sum(m => m.Weight) == 200 && data.MapsOf(data.At(1)!)[5].Id == Map06,
              "5 modes by list index (0 generic), 3 = hellgate_2v2 (2v2, IP 900, 500 points), 1 = 100 points with HELL_STD_PVE_NORMAL; mapset weights sum to 200");
        var den = Store.Get("0000-HellDen");
        Check(den.HellgateShrines.Select(s => (s.Position, s.Tile)).SequenceEqual([(Shrine, "SHARED_HELLGATE_ENTRANCE")]),
              $"0000-HellDen: one shrine SHARED_HELLGATE_ENTRANCE at (760, 130) (got {string.Join(", ", den.HellgateShrines.Select(s => s.Position))})");
        foreach (var official in new[] { false, true })
        {
            var (world, _, dir, characters) = NewWorld(official ? new ServerConfig { JoinByCharacterId = true } : PveConfig(), 3);
            var (_, peer, _) = Join(world, Hero(characters, Shrine + new Vector2(3, 0)).Id);
            var shrine = Of(peer, EventCodes.NewHellgateShrine).SingleOrDefault();
            var keys = shrine?.Parameters.Keys.Where(k => k < 250).Order().ToList();
            Check(keys is not null && keys.SequenceEqual(new byte[] { 0, 1, 2, 3, 4, 5, 6 }) && shrine!.Parameters[3] is "SHARED_HELLGATE_ENTRANCE"
                  && Key(shrine, 4) == (official ? 0 : 1) && shrine.Parameters[5] is bool fixedMode && fixedMode != official,
                  $"{(official ? "official" : "pve")}: 400 keys 0-6, 3 the tile, 5 {!official} and 4 {(official ? "0" : "1, the default mode")} (got {(keys is null ? "none" : string.Join(",", keys))})");
            world.Dispose();
            characters.Dispose();
            try { Directory.Delete(dir, true); } catch (IOException) { }
        }
        var file = HellgateSettings.Parse(File.ReadAllText(Path.Combine(DataDir(), "..", "content", "hellgates.json")), "hellgates.json");
        var cfg = NewDir();
        var none = Path.Combine(cfg, "albionlocal.json");
        var pve = LoadTestConfig(none, ["--Preset=pve"]).Hellgates;
        var off = LoadTestConfig(none, ["--Preset=official"]).Hellgates;
        Check(file.Maps.Count == 0 && file.Texts.OrderBy(t => t.Key).SequenceEqual(HellgateSettings.DefaultTexts.OrderBy(t => t.Key))
              && pve is { ShrineMode: HellgateShrineMode.Fixed, CheckItemPower: false, BossMapFactor: 3, GroupScaling: true, EnemyTeamSize: HellgateEnemyTeam.Party, KnockdownSeconds: 30, LeaveCommand: true }
              && pve.VersusModes.SequenceEqual(HellgateOptions.LethalModes)
              && off is { ShrineMode: HellgateShrineMode.Gui, CheckItemPower: true, BossMapFactor: 1, GroupScaling: false, KnockdownSeconds: null, LeaveCommand: false } && off.VersusModes.Count == 4
              && LoadTestConfig(none, ["--Hellgates:VersusModes=hellgate_5v5", "--Hellgates:KnockdownSeconds=archetype"]).Hellgates is { KnockdownSeconds: null, VersusModes: ["hellgate_5v5"] },
              "content/hellgates.json holds the built-in texts; Hellgates:* under pve (fixed shrine, lethal modes versus, scaling, 30 s knockdowns, .hg leave) and official; explicit keys win");
        try { Directory.Delete(cfg, true); } catch (IOException) { }
    }

    private static ServerConfig PveConfig() => ServerConfig.PveDefaults with { JoinByCharacterId = true, Bots = new BotOptions { Enabled = true } };

    /// <summary>A world of <paramref name="config"/> on the real client data, bots without a population, one map per mapset (HELLGATE-06).</summary>
    private static (World World, ManualClock Clock, string Dir, SqliteCharacterRepository Characters) NewWorld(ServerConfig config, int seed)
    {
        var dir = NewDir();
        var characters = Repository(dir);
        var clock = new ManualClock();
        var anchor = DateTime.UtcNow.Ticks;
        var world = new World(config, characters, Client.Items, Client.GameData, Client.Spells)
        {
            Sync = new object(), Clock = clock, Clusters = Store, MapSeed = seed, GameTicks = () => anchor + clock.Now.Ticks,
            BotSettings = BotSettings.Default with { Counts = new Dictionary<string, IReadOnlyDictionary<BotRole, int>>(StringComparer.OrdinalIgnoreCase) },
        };
        world.MobAi = false;
        var data = Store.Hellgates;
        world.HellgateSettings = HellgateSettings.Default with
        {
            Maps = data.MapSets.Values.SelectMany(s => s).ToDictionary(m => m.Id, m => m.Id is Map06 or Map06Hrd ? 1.0 : 0, StringComparer.Ordinal),
        };
        _ = Store.Get("0000-HellDen");
        return (world, clock, dir, characters);
    }

    private static Character Hero(SqliteCharacterRepository characters, Vector2 at)
    {
        var hero = StarterKit.Default.NewCharacter("H" + Guid.NewGuid().ToString("N")[..10]);
        (hero.Cluster, hero.PositionX, hero.PositionY) = ("0000-HellDen", at.X, at.Y);
        Check(characters.Create(hero), "character created");
        return hero;
    }

    private static OperationResponse Call(World world, WorldSession session, FakePeer on, AlbionRequest req, int id)
    {
        lock (world.Sync) session.OnRequest(Request(req, id), encrypted: false);
        return on.Responses[^1];
    }

    private static void Party(World world, Player a, Player b) =>
        Check(world.Parties.Invite(a, b.Character.Id, null, out _) == 0 && world.Parties.AnswerInvitation(b, true, false, Parties.NoReason) == 0, "party of two");

    private static long ShrineId(Player p) => p.Map!.Objects.Values.OfType<HellgateShrine>().Single().Id;

    private static void Run()
    {
        var (world, clock, dir, characters) = NewWorld(PveConfig(), 7);
        void Tick(double seconds)
        {
            clock.Advance(TimeSpan.FromSeconds(seconds));
            lock (world.Sync) world.Update();
        }
        var template = Store.Get(Map06);
        world.Bots.Nav.Preload(template);
        var heroChar = Hero(characters, Shrine + new Vector2(2, 0));
        var friendChar = Hero(characters, Shrine + new Vector2(-70, 0)); // the party's second, away from the shrine
        var outsiderChar = Hero(characters, Shrine + new Vector2(20, 0));
        var (s, peer, _) = Join(world, heroChar.Id);
        var (_, friendPeer, _) = Join(world, friendChar.Id);
        var (so, outsiderPeer, _) = Join(world, outsiderChar.Id);
        var me = s.Player!;
        lock (world.Sync) Party(world, me, world.Parties.Online(friendChar.Id)!);
        var shrine = ShrineId(me);
        UseHellgateShrineRequest Use(bool cancel = false) => new() { wf = world.GameNow, aic = shrine, aid = cancel, aie = (short)(cancel ? -1 : 1) };

        // SPEC §6 check 2: 20 m off: RC 1; at 2 m: RC 0, 273 {1 true}, 401 {1 1, 3 party, 4 1} to the party (the friend too), the outsider in view none.
        var far = Call(world, so, outsiderPeer, Use(), 2);
        var mark = (Me: peer.Events.Count, Friend: friendPeer.Events.Count, Outsider: outsiderPeer.Events.Count);
        var ok = Call(world, s, peer, Use(), 3);
        var party = world.Parties.Of(heroChar.Id)!.Id;
        var update = Of(peer, EventCodes.UpdateHellgateShrine, mark.Me).SingleOrDefault();
        Check(far.ReturnCode == 1 && ok.ReturnCode == 0 && Of(peer, EventCodes.UsingHellgateShrine, mark.Me) is [var using0] && using0.Parameters[1] is true && Key(using0, 2) == 0
              && update is not null && Key(update, 1) == 1 && Key(update, 3) == party && Key(update, 4) == 1 && Of(friendPeer, EventCodes.UpdateHellgateShrine, mark.Friend).Count == 1
              && Of(outsiderPeer, EventCodes.UpdateHellgateShrine, mark.Outsider).Count == 0 && me.Observers.Any(o => o.Character.Id == outsiderChar.Id),
              $"op 255 at 20 m RC 1, at 2 m RC 0: 273 {{1 true}}, 401 {{1 1, 3 party, 4 1}} to both members, none to the outsider in view (got {far.ReturnCode}/{ok.ReturnCode})");

        // 3 s: 273 {2 true} and the lock (274 to the player and the observer); a client cancel during it: RC 0, the lock goes on; 3 s more: the push.
        Tick(3);
        var locked = Of(peer, EventCodes.EnteringHellgateLockStart, mark.Me).Count == 1 && Of(outsiderPeer, EventCodes.EnteringHellgateLockStart, mark.Outsider).Count == 1
                     && Of(peer, EventCodes.UsingHellgateShrine, mark.Me).Any(e => e.Parameters.GetValueOrDefault((byte)2) is true);
        var cancel = Call(world, s, peer, Use(cancel: true), 4);
        Tick(3);
        var push = peer.Responses[^1];
        var i = world.Hellgates.Instances.SingleOrDefault();
        Check(locked && cancel.ReturnCode == 0 && Of(peer, EventCodes.EnteringHellgateLockCancel, mark.Me).Count == 0 && i is not null
              && push.Parameters.Keys.Where(k => k < 250).Order().SequenceEqual(new byte[] { 0, 1 }) && push.Parameters[0] is string id && id == i.Id
              && Regex.IsMatch(id, "^@HELLCLUSTER@[0-9a-f-]{36}$") && push.Parameters[1] is Map06 && s.Status == SessionStatus.Transferring,
              $"273 {{2 true}} and 274 after 3 s, the cancel answered and ignored, 3 s later the push {{0 @HELLCLUSTER@<guid>, 1 {Map06}}} (got {string.Join(",", push.Parameters.Where(x => x.Key < 250).Select(x => $"{x.Key}={x.Value}"))})");

        // The Join: start A in the walk grid's component, 60 m or more from the chest; 454 {1 1, 2 1, 3 0} then 453; mobs x0.786; no boss yet.
        var (s2, peer2, join) = Join(world, heroChar.Id);
        me = s2.Player!;
        var map = me.Map!;
        var nav = world.Bots.Nav.Find(Map06)!;
        var chestMarker = template.HellgateMarkers.Single(m => m.Name == HellgateData.ObjectiveChest).Position;
        var status = Of(peer2, EventCodes.HellgateStatus).SingleOrDefault();
        var order = peer2.Events.FindIndex(e => Code(e) == (int)EventCodes.HellgateStatus) < peer2.Events.FindIndex(e => Code(e) == (int)EventCodes.HellgateInfamy);
        Check(join.a8i == i!.Id && nav.Grid.ComponentCells > 0 && nav.Grid.Walkable(join.a8j) && Vector2.Distance(join.a8j, chestMarker) >= 60
              && status is not null && Key(status, 1) == 1 && Key(status, 2) == 1 && Key(status, 3) == 0 && order
              && Math.Abs(map.MobHealthFactor - 0.786) < 0.001 && i.GuardianPoint is not null && !map.Objects.Values.OfType<Mob>().Any(m => HellgateData.IsBoss(m.Spawn.SpawnBehavior))
              && i.RewardChests.Count == 1 && i.RewardChests[0].LockState == RandomDungeons.BossLocked,
              $"the Join at start A ({join.a8j.X:0}, {join.a8j.Y:0}): walkable ({nav.Grid.ComponentCells} cells), {Vector2.Distance(join.a8j, chestMarker):0} m from the chest; 454 {{1 1, 2 1, 3 0}} before 453; mobs x{map.MobHealthFactor:0.###}; the boss held back, its chest locked");

        // Kills: 453 by round(fame / 100) each; at 100: 455 {2} and the guardian T5_MOB_HG_DEMON_BRUTE_BOSS.
        void Kill(Mob m)
        {
            lock (world.Sync) Combat.Damage(me, m, 1e9, EffectType.Physical, HitOrigin.Direct, 0, world.GameNow);
        }
        mark.Me = peer2.Events.Count;
        var first = map.Objects.Values.OfType<Mob>().Where(m => m.Template.Fame > 0).OrderBy(m => m.Id).First();
        Kill(first);
        var gain = Of(peer2, EventCodes.HellgateInfamy, mark.Me) is [var p1] ? Key(p1, 1) : -1;
        foreach (var m in map.Objects.Values.OfType<Mob>().Where(m => !m.Dead && m.Template.Fame > 0).OrderBy(m => m.Id).ToList())
        {
            if (i.State != HellgateState.Progress) break;
            Kill(m);
        }
        var guardian = map.Objects.GetValueOrDefault(i.GuardianId) as Mob;
        Check(gain == (int)Math.Round(first.Template.Fame / 100, MidpointRounding.AwayFromZero) && i.Points >= 100 && i.State == HellgateState.Defeat
              && Of(peer2, EventCodes.HellgateStatusUpdate, mark.Me) is [var two] && Key(two, 1) == 2 && guardian is { Dead: false, Template.Name: "T5_MOB_HG_DEMON_BRUTE_BOSS" }
              && i.RewardChests[0].LockState == RandomDungeons.BossLocked,
              $"a kill of fame {first.Template.Fame}: 453 {gain}; at {i.Points} points 455 {{2}}, the guardian {guardian?.Template.Name} out, the BOSS chest still locked");

        // The guardian dies: the BOSS chest unlocks, 455 {3}, the objective chest HELL_STD_PVE_NORMAL_* at the marker; opened: 455 {4}, 250 at EXIT_PORTAL
        // (both far from start A: their New* events as a viewer near them gets them).
        mark.Me = peer2.Events.Count;
        Kill(guardian!);
        var objective = i.ObjectiveChest;
        var shown = objective is not null && map.Objects.ContainsKey(objective.Id) && objective.BuildCreate(me).Select(AlbionSerializer.BuildEvent).Any(e => Code(e) == (int)EventCodes.NewLootChest);
        string? opened;
        lock (world.Sync) opened = objective is null ? "none" : world.Dungeons.OpenChest(me, objective);
        var exit = template.HellgateMarkers.Single(m => m.Name == HellgateData.ExitPortal).Position;
        var portal = map.Objects.Values.OfType<HellgateExitPortal>().SingleOrDefault()?.BuildCreate(me).Select(AlbionSerializer.BuildEvent).SingleOrDefault();
        Check(i.RewardChests[0].LockState == 6 && objective is { Template.Name: var chestName } && chestName.StartsWith("HELL_STD_PVE_NORMAL_", StringComparison.Ordinal)
              && objective.Position == chestMarker && shown && opened is null && i.State == HellgateState.Claimed
              && Of(peer2, EventCodes.HellgateStatusUpdate, mark.Me).Select(e => Key(e, 1)).SequenceEqual([3, 4])
              && portal is not null && Code(portal) == (int)EventCodes.NewHellgateExitPortal && portal.Parameters[1] is float[] at && new Vector2(at[0], at[1]) == exit,
              $"the guardian slain: the BOSS chest open, 455 {{3}}, {objective?.Template.Name} at the marker (NewLootChest); opened ({opened ?? "ok"}): 455 {{4}}, 250 at EXIT_PORTAL");

        // Slot 21 gets no answer but a 7 s server cast ending in 467; the escape {0: 0, 3: true}: {0: 0000-HellDen}; 60 s empty: closed; a character saved inside goes to its way back.
        var before = peer2.Responses.Count;
        lock (world.Sync) s2.OnRequest(Request(new CastStartRequest { wf = world.GameNow, dx = Hellgates.EscapeSlot, d0 = world.Spells!.IdOf("ESCAPE_DUNGEON"), d4 = 7 }, 9), encrypted: false);
        var quiet = peer2.Responses.Count == before;
        var castMark = peer2.Events.Count;
        Tick(8); // ESCAPE_DUNGEON's 7 s server cast: its end is DungonEscapeReady (467), on which the client sends the escape (stageN corrupted-exit)
        quiet &= Of(peer2, EventCodes.DungonEscapeReady, castMark).Count == 1;
        var back = i.Members[heroChar.Id];
        var escape = Call(world, s2, peer2, new ChangeClusterRequest { ee = 0, eh = true }, 10);
        var (_, _, home) = Join(world, heroChar.Id);
        Tick(1);
        Tick(60);
        var gone = world.LoadCharacter(heroChar.Id)!.Clone();
        (gone.Cluster, gone.DungeonReturn) = (i.Id, back);
        bool returned;
        lock (world.Sync) returned = world.Hellgates.ReturnIfClosed(gone);
        Check(quiet && escape is { ReturnCode: 0 } && escape.Parameters[0] is "0000-HellDen" && home.a8i == "0000-HellDen" && Vector2.Distance(home.a8j, new Vector2(back.X, back.Y)) < 0.01
              && Math.Max(Math.Abs(back.X - Shrine.X), Math.Abs(back.Y - Shrine.Y)) > 3.5f
              && i.Closed && !world.Hellgates.IsLive(i.Id) && world.Maps.Find(i.Id) is null && returned && gone is { Cluster: "0000-HellDen", DungeonReturn: null },
              $"slot 21: no answer; the escape {{0: 0000-HellDen}} by the shrine, outside its 7x7 trigger ({back.X:0.#}, {back.Y:0.#}); 60 s empty: closed and unloaded; a character saved inside goes back (got {escape.ReturnCode})");
        world.Dispose();
        characters.Dispose();
        try { Directory.Delete(dir, true); } catch (IOException) { }
    }

    private static void Versus()
    {
        var (world, clock, dir, characters) = NewWorld(PveConfig(), 11);
        void Tick(double seconds)
        {
            clock.Advance(TimeSpan.FromSeconds(seconds));
            lock (world.Sync) world.Update();
        }
        var template = Store.Get(Map06Hrd);
        world.Bots.Nav.Preload(template);
        var aChar = Hero(characters, Shrine + new Vector2(2, 0));
        var bChar = Hero(characters, Shrine + new Vector2(0, 2));
        var outsiderChar = Hero(characters, Shrine + new Vector2(-30, 0));
        var (sa, pa, _) = Join(world, aChar.Id);
        var (sb, pb, _) = Join(world, bChar.Id);
        var (_, _, _) = Join(world, outsiderChar.Id);
        lock (world.Sync) Party(world, sa.Player!, sb.Player!);
        var shrine = ShrineId(sa.Player!);
        UseHellgateShrineRequest Use(short mode = 3) => new() { wf = world.GameNow, aic = shrine, aie = mode };

        // Both channel (the second's other mode refused first: never switched), the lock, the push of both into one instance: no bots before the points.
        Check(Call(world, sa, pa, Use(), 2).ReturnCode == 0 && Call(world, sb, pb, Use(1), 2).ReturnCode == 1 && Call(world, sb, pb, Use(), 2).ReturnCode == 0,
              "both members channel for hellgate_2v2, a start for another mode refused");
        Tick(3);
        Tick(3);
        var i = world.Hellgates.Instances.SingleOrDefault();
        var (sa2, _, _) = Join(world, aChar.Id);
        var (sb2, pb2, _) = Join(world, bChar.Id);
        var (a, b) = (sa2.Player!, sb2.Player!);
        var map = a.Map!;
        var outsider = world.Parties.Online(outsiderChar.Id)!;
        Check(i is { Versus: true, Players: 2, Mode.Name: "hellgate_2v2" } && b.Map == map && map.Bots.Count == 0 && Math.Abs(map.MobHealthFactor - 1) < 1e-9,
              $"one versus instance for both ({i}), no bots during the points, full scaling for a full team");

        // The points done (.hg finish): 2 bots of T6 at start B, Hostile to both members both ways, Neutral to the outsider.
        lock (world.Sync) world.Hellgates.Advance(i!, map);
        var bots = map.Bots.Values.ToList();
        Check(i!.State == HellgateState.Defeat && bots.Count == 2 && bots.All(x => x.Bot is { Role: BotRole.Hellgate, Tier: 6 } && Vector2.Distance(x.PositionAt(world.Bots.Time), i.StartB) < 8)
              && bots.All(x => BotPvp.Of(x, a) == Relation.Hostile && BotPvp.Of(b, x) == Relation.Hostile && BotPvp.Of(x, outsider) == Relation.Neutral)
              && Vector2.Distance(i.StartB, i.Start.Position) > 60,
              $"objective 2: {bots.Count} bots T{bots.FirstOrDefault()?.Bot?.Tier} at start B, Hostile to the party both ways, Neutral to the outsider");

        // Both bots beaten: 455 {3} and the pvp chest HELL_HRD_PVP_NORMAL_*; the demons gone 2.4 s after the start of objective 2.
        var mark = pb2.Events.Count;
        Tick(3);
        var demons = map.Objects.Values.OfType<Mob>().Count(m => !m.Dead);
        foreach (var bot in bots) lock (world.Sync) Pvp.Damage(a, bot, 1e9, EffectType.Physical, HitOrigin.Direct, 0, world.GameNow);
        Check(i.State == HellgateState.Defeated && Of(pb2, EventCodes.HellgateStatusUpdate, mark).Select(e => Key(e, 1)).SequenceEqual([3]) && demons == 0
              && i.ObjectiveChest is { Template.Name: var name } && name.StartsWith("HELL_HRD_PVP_NORMAL_", StringComparison.Ordinal)
              && i.RewardChests.Count > 0 && i.RewardChests.All(c => c.LockState == 6),
              $"the demons gone, both bots beaten: 455 {{3}}, the chest {i.ObjectiveChest?.Template.Name}, the REWARD layer's {i.RewardChests.Count} chest(s) open");
        lock (world.Sync) world.Hellgates.Close(i, "test");

        // A second run: the bots knock both members down: 455 {6}, the exit portal, no item lost (Pvp:LethalDeath false).
        var (sa3, pa3, _) = Join(world, aChar.Id);
        var (sb3, pb3, _) = Join(world, bChar.Id);
        Check(Call(world, sa3, pa3, Use(), 3).ReturnCode == 0 && Call(world, sb3, pb3, Use(), 3).ReturnCode == 0, "a second entry");
        Tick(3);
        Tick(3);
        var second = world.Hellgates.Instances.Single();
        var (sa4, pa4, _) = Join(world, aChar.Id);
        var (sb4, _, _) = Join(world, bChar.Id);
        (a, b) = (sa4.Player!, sb4.Player!);
        map = a.Map!;
        var items = a.Inventory.Equipment.Items.Count() + a.Inventory.Bag.Items.Count();
        lock (world.Sync) world.Hellgates.Advance(second, map);
        mark = pa4.Events.Count;
        var enemy = map.Bots.Values.First();
        lock (world.Sync)
        {
            Pvp.Damage(enemy, a, 1e9, EffectType.Physical, HitOrigin.Direct, 0, world.GameNow);
            Pvp.Damage(enemy, b, 1e9, EffectType.Physical, HitOrigin.Direct, 0, world.GameNow);
        }
        Tick(0.5);
        Check(a.Combat.Down is { Kind: KnockdownKind.Pvp } && second.State == HellgateState.Lost && Of(pa4, EventCodes.HellgateStatusUpdate, mark).Select(e => Key(e, 1)).SequenceEqual([6])
              && second.Portal is not null && map.Bots.Count == 0 && second.ObjectiveChest is null && a.Inventory.Equipment.Items.Count() + a.Inventory.Bag.Items.Count() == items
              && Math.Abs(Knockdown.Seconds(a, KnockdownKind.Pvp) - 30) < 1e-9,
              $"both knocked down by a bot (Pvp, 30 s): 455 {{6}}, the exit portal, the bots gone, no chest, no item lost (got state {second.State})");
        lock (world.Sync) world.Hellgates.Close(second, "test");

        // A third run: the enemy team leaves unbeaten (.bot off): the guardian comes instead, so objective 2 never stays without an enemy.
        var (sa5, pa5, _) = Join(world, aChar.Id);
        var (sb5, pb5, _) = Join(world, bChar.Id);
        Check(Call(world, sa5, pa5, Use(), 3).ReturnCode == 0 && Call(world, sb5, pb5, Use(), 3).ReturnCode == 0, "a third entry");
        Tick(3);
        Tick(3);
        var third = world.Hellgates.Instances.Single();
        var (sa6, _, _) = Join(world, aChar.Id);
        map = sa6.Player!.Map!;
        lock (world.Sync)
        {
            world.Hellgates.Advance(third, map);
            world.Bots.Clear(map, "test", BotRefill.Now);
        }
        Tick(0.5);
        Check(third is { State: HellgateState.Defeat, FoughtTeam: false, GuardianId: not 0 } && map.Objects.GetValueOrDefault(third.GuardianId) is Mob { Dead: false },
              $"the enemy team gone unbeaten: the guardian instead (got state {third.State}, guardian {third.GuardianId})");
        world.Dispose();
        characters.Dispose();
        try { Directory.Delete(dir, true); } catch (IOException) { }
    }

    /// <summary>
    /// SPEC §7 self-test B 1 and 3: the phase B config and data; a PvE run on HELLGATE-06 for one player (.hg enter's way): the win's infamy (446
    /// {1 2, 2 100, 3 100}), the claim's chain exit (402, a walk-in 324 without keys 12/13), its walk-in answered {0 the next id, 1 the map}: a
    /// second instance with chain 1 and bct.af(1), the first way back kept; its win adds the infamy again (GetInfamyInfo key 1).
    /// </summary>
    private static void Chain()
    {
        var cfg = NewDir();
        var none = Path.Combine(cfg, "albionlocal.json");
        var pve = LoadTestConfig(none, ["--Preset=pve"]).Hellgates;
        var off = LoadTestConfig(none, ["--Preset=official"]).Hellgates;
        var data = Store.Hellgates;
        var mode = data.At(1)!;
        var af1 = 1 + 0.2 * (1 - Math.Exp(-1 / 43.44769151));
        var area = Store.Get(Map06).HellgateAreas.SingleOrDefault();
        Check(pve is { Chain: true, RestrictedArea: false, SpawnBuffs: false, Matchmaking: HellgateMatchmaking.Bots, InfamyWin: 100 }
              && off is { Chain: true, RestrictedArea: true, SpawnBuffs: true, Matchmaking: HellgateMatchmaking.Players, MatchSeconds: 60 }
              && Math.Abs(mode.ChainFactor(1) - af1) < 1e-12 && mode.ChainFactor(0) == 1 && mode is { InfamyType: 2, AreaDelay: 90, AreaVelocity: 0.3 } && data.At(4)!.InfamyType == 3
              && data.Milestones.GetValueOrDefault(2) is [50, 100, ..] && area is { Center: { X: 9, Y: -9 }, StartRadius: 100, EndRadius: 30 },
              $"phase B keys: pve chain, no area, no spawn buffs, bots; official area, spawn buffs, players in 60 s; bct.af(1) = {mode.ChainFactor(1):0.#####}; H2 milestones; HELLGATE-06's area {area?.Center} {area?.StartRadius} -> {area?.EndRadius}");
        try { Directory.Delete(cfg, true); } catch (IOException) { }

        var (world, clock, dir, characters) = NewWorld(PveConfig(), 13);
        var template = Store.Get(Map06);
        world.Bots.Nav.Preload(template);
        var heroChar = Hero(characters, Shrine + new Vector2(2, 0));
        var (s, _, _) = Join(world, heroChar.Id);
        var me = s.Player!;
        var back = new DungeonReturn("0000-HellDen", Shrine.X + 6, Shrine.Y, 0);
        HellgateInstance i;
        lock (world.Sync)
        {
            i = world.Hellgates.Create(mode, Map06, 0, 1, Hellgates.StartsOf(template, world.Bots.Nav.Find(Map06)!.Grid, 25));
            world.Hellgates.Send(me, i, back);
        }
        var (s2, peer2, _) = Join(world, heroChar.Id);
        me = s2.Player!;
        var map = me.Map!;
        var mark = peer2.Events.Count;
        lock (world.Sync)
        {
            world.Hellgates.Advance(i, map);
            world.Hellgates.Advance(i, map);
        }
        var infamy = Of(peer2, EventCodes.UpdateInfamy, mark);
        Check(i.State == HellgateState.Defeated && infamy is [var won] && Key(won, 1) == 2 && Key(won, 2) == 100 && Key(won, 3) == 100 && me.Character.Infamy?.Current[2] == 100,
              $"the win: 446 {{1 2 (Hell2v2), 2 100, 3 100}} (got {string.Join(" ", infamy.Select(e => $"{Key(e, 1)}/{Key(e, 2)}/{Key(e, 3)}"))})");

        // The claim: 402 {1 CHAIN_EXIT}, the chain exit (324: the walk-in, no keys 12/13, the HELLGATE_CHAIN_EXIT prefab), the next map picked.
        mark = peer2.Events.Count;
        lock (world.Sync) world.Hellgates.Advance(i, map);
        var marker = template.HellgateMarkers.Single(m => m.Name == HellgateData.ChainExit).Position;
        var activate = Of(peer2, EventCodes.ActivateHellgateExit, mark).SingleOrDefault();
        var exit = i.ChainExit?.BuildCreate(me).Select(AlbionSerializer.BuildEvent).SingleOrDefault();
        Check(i.State == HellgateState.Claimed && activate?.Parameters[1] is float[] at && new Vector2(at[0], at[1]) == marker && i.ChainExit?.Position == marker
              && exit is not null && Code(exit) == (int)EventCodes.NewRandomDungeonExit && !exit.Parameters.ContainsKey(12) && !exit.Parameters.ContainsKey(13)
              && exit.Parameters[6] is "SHARED_HELLGATE_EXIT_LETHAL" && i.NextWorld == Map06 && map.Objects.ContainsKey(i.ChainExit.Id),
              $"the claim: 402 at CHAIN_EXIT {marker}, the chain exit 324 ({exit?.Parameters.GetValueOrDefault((byte)6)}) without keys 12/13, the next map {i.NextWorld}");

        // The walk-in at the chain exit (after the exit cooldown): {0 the next id, 1 HELLGATE-06-2v2-01}; a new instance, chain 1, chests x bct.af(1), the way back kept.
        clock.Advance(TimeSpan.FromSeconds(60));
        lock (world.Sync)
        {
            world.Update();
            me.Move(marker, marker, 0, 0, 0, DateTime.UtcNow);
        }
        var walk = Call(world, s2, peer2, new ChangeClusterRequest { ee = i.ChainExit!.Id }, 5);
        var next = world.Hellgates.Instances.FirstOrDefault(x => x != i);
        Check(walk.ReturnCode == 0 && walk.Parameters.Keys.Where(k => k < 250).Order().SequenceEqual(new byte[] { 0, 1 }) && next is not null && walk.Parameters[0] is string id && id == next.Id
              && walk.Parameters[1] is Map06 && next is { Chain: 1, Mode.Id: 1 } && Math.Abs(next.ChainFactor - af1) < 1e-12 && next.Members.GetValueOrDefault(heroChar.Id) == back
              && !i.Members.ContainsKey(heroChar.Id),
              $"the chain exit: {{0 {next?.Id}, 1 {Map06}}}, chain {next?.Chain} x{next?.ChainFactor:0.#####}, the first way back (got {walk.ReturnCode})");

        // The next one won: 446 +100 (100 x 1.0046 rounded), 200 in GetInfamyInfo key 1 (InfamyType 2).
        var (s3, peer3, join) = Join(world, heroChar.Id);
        me = s3.Player!;
        mark = peer3.Events.Count;
        lock (world.Sync)
        {
            world.Hellgates.Advance(next!, me.Map!);
            world.Hellgates.Advance(next!, me.Map!);
        }
        var info = Call(world, s3, peer3, new GetInfamyInfoRequest(), 6);
        Check(join.a8i == next!.Id && Of(peer3, EventCodes.UpdateInfamy, mark) is [var again] && Key(again, 2) == 200 && Key(again, 3) == 100
              && Convert.ToInt64(info.Parameters.GetValueOrDefault((byte)1) ?? 0) == 200,
              $"the chained hellgate won: 446 total 200, GetInfamyInfo key 1 = {info.Parameters.GetValueOrDefault((byte)1)}");
        world.Dispose();
        characters.Dispose();
        try { Directory.Delete(dir, true); } catch (IOException) { }
    }

    /// <summary>
    /// SPEC §7 self-test B 2 and 4, under the official preset (Matchmaking Players, RestrictedArea, SpawnBuffs; no server item power gate): two
    /// parties of two channel hellgate_2v2_non_lethal one after the other and share one instance at opposite starts; the second objective is
    /// their fight (Hostile across, not within); the area 90 s later and its radius; the second party knocked down loses (the REWARD BOSS chest
    /// opens; the pvp chest, once a winner opened it, still not shown to a loser); a winner's leave ends HELL_2V2_ENTER.
    /// </summary>
    private static void Match()
    {
        var config = new ServerConfig { JoinByCharacterId = true, Bots = new BotOptions { Enabled = true }, Hellgates = new HellgateOptions { CheckItemPower = false } };
        var (world, clock, dir, characters) = NewWorld(config, 17);
        void Tick(double seconds)
        {
            clock.Advance(TimeSpan.FromSeconds(seconds));
            lock (world.Sync) world.Update();
        }
        var template = Store.Get(Map06);
        world.Bots.Nav.Preload(template);
        var chars = Enumerable.Range(0, 4).Select(k => Hero(characters, Shrine + new Vector2(k % 2 == 0 ? 2 : -2, k < 2 ? 1 : -1))).ToList();
        var sessions = chars.Select(c => Join(world, c.Id)).ToList();
        lock (world.Sync)
        {
            Party(world, sessions[0].Session.Player!, sessions[1].Session.Player!);
            Party(world, sessions[2].Session.Player!, sessions[3].Session.Player!);
        }
        var shrine = ShrineId(sessions[0].Session.Player!);
        UseHellgateShrineRequest Use() => new() { wf = world.GameNow, aic = shrine, aie = 1 };
        Check(Call(world, sessions[0].Session, sessions[0].Peer, Use(), 2).ReturnCode == 0 && Call(world, sessions[1].Session, sessions[1].Peer, Use(), 2).ReturnCode == 0, "the first party channels");
        Tick(3);
        Tick(3);
        var i = world.Hellgates.Instances.SingleOrDefault();
        Check(Call(world, sessions[2].Session, sessions[2].Peer, Use(), 3).ReturnCode == 0 && Call(world, sessions[3].Session, sessions[3].Peer, Use(), 3).ReturnCode == 0, "the second party channels");
        Tick(3);
        Tick(3);
        var push = sessions[2].Peer.Responses[^1];
        var joins = chars.Select(c => Join(world, c.Id)).ToList();
        var players = joins.Select(j => j.Session.Player!).ToList();
        var partyB = world.Parties.Of(chars[2].Id)!.Id;
        Check(i is not null && world.Hellgates.Instances.Count == 1 && i.PartyB == partyB && push.Parameters[0] is string id && id == i.Id
              && Vector2.Distance(joins[2].Join.a8j, i.StartB) < 0.01 && Vector2.Distance(joins[0].Join.a8j, i.Start.Position) < 0.01 && Vector2.Distance(i.StartB, i.Start.Position) > 60
              && players.All(p => p.Map == players[0].Map) && i.TeamB.SetEquals([chars[2].Id, chars[3].Id]),
              $"the second party matched into {i?.Id} at start B ({joins[2].Join.a8j.X:0}, {joins[2].Join.a8j.Y:0}), the first at start A, {Vector2.Distance(i?.StartB ?? default, i?.Start.Position ?? default):0} m apart");
        Check(template.SpawnBuffs.Select(b => b.Name).SequenceEqual(["BUFFSHRINE_OUTLANDCITY_INVISIBILITY_REMOVE", "HELL_2V2_ENTER", "SPAWN_INVULNERABILITY_HELL"])
              && players.All(p => p.Effects.Spells.Any(e => e.Spell.Name == "HELL_2V2_ENTER")),
              $"the spawn buffs of DUNGEON_HELL_2V2_NON_LETHAL on arrival (got {string.Join(", ", players[0].Effects.Spells.Select(e => e.Spell.Name))})");

        // The points done: the match (no bots), Hostile across the parties, not within one.
        var map = players[0].Map!;
        lock (world.Sync) world.Hellgates.Advance(i!, map);
        Check(i!.State == HellgateState.Defeat && i.Match && map.Bots.Count == 0 && Pvp.Of(players[0], players[2]) == Relation.Hostile && Pvp.Of(players[3], players[1]) == Relation.Hostile
              && Pvp.Of(players[0], players[1]) != Relation.Hostile && Pvp.Of(players[2], players[3]) != Relation.Hostile,
              "objective 2: party against party, no bots, Hostile across and not within");

        // 90 s later 452 {1 true, 3 (9, -9), 4 100, 5 30, 8 0.3}; 100 s on the radius is 70 m.
        var mark = joins[0].Peer.Events.Count;
        Tick(90);
        var area = Of(joins[0].Peer, EventCodes.HellgateRestrictedAreaUpdate, mark).SingleOrDefault();
        Tick(100);
        var radius = i.Area?.RadiusAt(world.Now) ?? -1;
        Check(area is not null && area.Parameters[1] is true && area.Parameters[3] is float[] c && c[0] == 9 && c[1] == -9 && area.Parameters[4] is 100f && area.Parameters[5] is 30f
              && area.Parameters[8] is float v && Math.Abs(v - 0.3f) < 1e-6 && Math.Abs(radius - 70) < 0.5,
              $"452 at +90 s (centre, 100 -> 30 m, 0.3 m/s), the radius {radius:0.#} m 100 s later");

        // The second party knocked down: 455 {6} to it, {3} to the winners, 452 off, the pvp chest refused to the losers; the winners' infamy.
        mark = joins[0].Peer.Events.Count;
        var markB = joins[2].Peer.Events.Count;
        lock (world.Sync)
        {
            Pvp.Damage(players[0], players[2], 1e9, EffectType.Physical, HitOrigin.Direct, 0, world.GameNow);
            Pvp.Damage(players[0], players[3], 1e9, EffectType.Physical, HitOrigin.Direct, 0, world.GameNow);
        }
        Tick(0.5);
        string? refused;
        lock (world.Sync) refused = i.ObjectiveChest is { } chest ? world.Dungeons.OpenChest(players[2], chest) : "no chest";
        Check(players[2].Combat.Down is not null && i is { State: HellgateState.Defeated, LostTeam: 2 } && Of(joins[2].Peer, EventCodes.HellgateStatusUpdate, markB).Select(e => Key(e, 1)).SequenceEqual([6])
              && Of(joins[0].Peer, EventCodes.HellgateStatusUpdate, mark).Select(e => Key(e, 1)).SequenceEqual([3])
              && Of(joins[0].Peer, EventCodes.HellgateRestrictedAreaUpdate, mark) is [var offEvent] && offEvent.Parameters[1] is false && i.Area is null
              && i.ObjectiveChest is { Template.Name: var name } && name.StartsWith("HELL_STD_PVP_NORMAL_", StringComparison.Ordinal) && refused is not null
              && Of(joins[0].Peer, EventCodes.UpdateInfamy, mark).Count == 1 && Of(joins[2].Peer, EventCodes.UpdateInfamy, markB).Count == 0,
              $"the second party down: 455 {{6}} to it, {{3}} to the winners, 452 off, {i.ObjectiveChest?.Template.Name} refused to the losers ({refused}), 446 to the winners only");

        // The REWARD layer's BOSS chest opens with the match (no guardian in a versus run); a winner opens the pvp chest, then RegisterToObject at
        // it: AttachItemContainer to the other winner, none to a loser (up again, at the chest).
        var objective = i.ObjectiveChest!;
        string? opened;
        var (winnerMark, loserMark) = (joins[1].Peer.Events.Count, joins[2].Peer.Events.Count);
        lock (world.Sync)
        {
            opened = world.Dungeons.OpenChest(players[0], objective);
            Knockdown.GetUp(players[2]);
            foreach (var p in new[] { players[1], players[2] }) p.Move(objective.Position, objective.Position, 0, 0, 0, DateTime.UtcNow);
            foreach (var k in new[] { 1, 2 }) joins[k].Session.OnRequest(Request(new RegisterToObjectRequest { zg = objective.Id }, null), encrypted: false);
        }
        var boss = i.RewardChests.Where(c => c.Spawn is LootChestSpawn sp && RandomDungeons.IsBossChest(sp.SpawnBehaviour)).ToList();
        Check(boss.Count > 0 && boss.All(c => c.LockState == 6) && opened is null && players[2].Combat.Down is null
              && Of(joins[1].Peer, EventCodes.AttachItemContainer, winnerMark).Count == 1 && Of(joins[2].Peer, EventCodes.AttachItemContainer, loserMark).Count == 0,
              $"the match won: the BOSS chest open ({string.Join(" ", boss.Select(c => c.LockState))}); the pvp chest opened by a winner shown to the other winner, not to a loser");

        // A winner leaves (.hg leave): its Join at the way back without HELL_2V2_ENTER (time 0, persistsafterclusterchange false).
        lock (world.Sync) world.Hellgates.Leave(players[0], i, "test");
        var home = Join(world, chars[0].Id).Session.Player!;
        Check(home.Map?.Cluster == "0000-HellDen" && !home.Effects.Spells.Any(e => e.Spell.Name == "HELL_2V2_ENTER"),
              $"out of the 2v2 hellgate: HELL_2V2_ENTER gone (got {string.Join(", ", home.Effects.Spells.Select(e => e.Spell.Name))} on {home.Map?.Cluster})");
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
