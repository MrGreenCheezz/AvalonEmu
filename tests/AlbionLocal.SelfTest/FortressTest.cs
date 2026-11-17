using System.Numerics;
using AlbionLocal.Photon.Serialization;
using AlbionLocal.Server;
using AlbionLocal.Server.Content;
using AlbionLocal.Server.Entities;
using AlbionLocal.Server.Game;
using AlbionLocal.Server.Network;
using AlbionLocal.Server.Protocol;
using AlbionLocal.Server.Systems;
using static AlbionLocal.SelfTest.AlbionTests;

namespace AlbionLocal.SelfTest;

/// <summary>
/// StageR fw-zone (analysis/work/stageR/fw-zone/SPEC.md; <see cref="FactionFortresses"/>, <see cref="RedZoneEvents"/>): 4206's Mountain fortress
/// besieged and taken by a Forest player, and one bandit assault rolled at a start time, run through its phases and ended with the placings.
/// </summary>
public static class FortressTest
{
    public static IEnumerable<(string, Action)> All =>
    [
        ("fortress: 4206's tower (652 guid/owner Mountain/Inactive), 9 Mountain T8 guards; a Forest siege 1 → 2 → 3 (659), the guards killed, the capture area drained in 2 s and filled in 30 s: 656, 661, owner Forest, 468 key 9", Siege),
        ("red-zone event: rolled at a start time (Always), 476/479 announced, phase 1 hands 4215's towers to Caerleon (468 keys 11-13), standings (540), phase 2, the end: 1st place's bonus points, the towers back", RedZone),
    ];

    private static void Check(bool cond, string what) => AlbionTests.Check(cond, what);

    private static long I(Dictionary<byte, object> p, byte key) => Convert.ToInt64(p.GetValueOrDefault(key) ?? 0);

    private static int Code(EventData e) => Convert.ToInt32(e.Parameters.GetValueOrDefault(ReservedKeys.EventCode));

    private static (World World, WorldSession Session, FakePeer Peer, ManualClock Clock, Map Map) Join(string cluster, Vector2 at, byte flag, ServerConfig config, long start)
    {
        var clock = new ManualClock();
        var world = new World(config, Repository(NewDir()), Client.Items, Client.GameData, Client.Spells)
        {
            Sync = new object(), Clock = clock, Clusters = Client.Clusters, MapSeed = 3, GameTicks = () => start + clock.Now.Ticks,
        };
        world.MobAi = false;
        var characters = (AlbionLocal.Server.Persistence.SqliteCharacterRepository)world.Characters;
        var c = StarterKit.Default.NewCharacter("F" + Guid.NewGuid().ToString("N")[..10]);
        (c.Cluster, c.PositionX, c.PositionY, c.Flag, c.Faction) = (cluster, at.X, at.Y, flag, flag);
        Check(characters.Create(c), "character created");
        var peer = new FakePeer();
        var session = new WorldSession(peer, world);
        lock (world.Sync) session.OnRequest(Request(new JoinRequest { ss = c.Id }, null), encrypted: true);
        var map = world.Maps.Find(cluster)!;
        for (var i = 0; i < 100 && map.Template is null; i++)
        {
            Thread.Sleep(20);
            clock.Advance(TimeSpan.FromSeconds(0.1));
            lock (world.Sync) world.Update();
        }
        return (world, session, peer, clock, map);
    }

    private static void Siege()
    {
        var template = Client.Clusters!.Get("4206");
        var spawn = template.Fortresses.SingleOrDefault();
        Check(spawn is { Tile: "MOUNTAIN_RED_FACTION_TOWER", Guards.Count: 9, CaptureRadius: 10 } && spawn.Position == new Vector2(265, -285)
              && spawn.Guid == Guid.Parse("09c2f9b2-705d-4620-aaf2-18d797677d04") && spawn.Guards.Count(g => g.GuardType == "T8_MOB_CITY_FORTRESS_SWORD") == 5,
              $"4206: one fortress tower at the world.xml capturepointposition (265, -285), its id, 9 guard spots (5 sword) (got {spawn?.Tile} {spawn?.Position} {spawn?.Guards.Count})");
        var (world, session, peer, clock, map) = Join("4206", new Vector2(265, -285), 2, new ServerConfig { JoinByCharacterId = true }, DateTime.UtcNow.Ticks);
        void Tick(double seconds)
        {
            clock.Advance(TimeSpan.FromSeconds(seconds));
            lock (world.Sync) world.Update();
        }
        Tick(0.2);
        var created = peer.Events.FirstOrDefault(e => Code(e) == (int)EventCodes.NewFactionFortressObject)?.Parameters;
        var tower = map.Objects.Values.OfType<FortressObject>().Single();
        var s = tower.State;
        Check(created is not null && created[1] is float[] { Length: 2 } xz && xz[0] == 265 && xz[1] == -285 && created[3] is byte[] g && g.SequenceEqual(spawn!.Guid.ToByteArray())
              && Convert.ToInt32(created[4]) == 4 && !created.ContainsKey(5) && Convert.ToInt32(created[8]) == Outposts.Unclaimable,
              $"Join: 652 {{1 (265, -285), 3 the fortress id, 4 Mountain, 5 Inactive (the default, left out), 8 Unclaimable}} (got {(created is null ? "none" : string.Join(" ", created.Select(kv => $"{kv.Key}={kv.Value}")))})");
        Check(s.Squad.Count == 9 && s.Squad.All(m => m is { Dead: false } && m.Flagging == 4) && s.Squad.Count(m => m!.Template.Name == "T8_MOB_CITY_FORTRESS_MOUNTAIN_SWORD") == 5,
              $"9 Mountain tower guards stand (5 T8 swords) (got {string.Join(", ", s.Squad.Select(m => m?.Template.Name))})");
        // The siege: announcement → pre-siege → active.
        peer.Events.Clear();
        lock (world.Sync) world.Fortresses.Gm(map, "siege", 2);
        var phases = new List<int>();
        void Phases() => phases.AddRange(peer.Events.Where(e => Code(e) == (int)EventCodes.FactionFortressFightStateUpdate).Select(e => Convert.ToInt32(e.Parameters[3])));
        Tick(61);
        Phases();
        peer.Events.Clear();
        Tick(541);
        Phases();
        var state = peer.Events.Where(e => Code(e) == (int)EventCodes.NewFactionFortressObject).Select(e => Convert.ToInt32(e.Parameters[5])).LastOrDefault(-1);
        Check(phases.SequenceEqual([1, 2, 3]) && state == FactionFortresses.ActiveSiegeState && s.Fight is { Attacker: 2, Defender: 4, Phase: FactionFortresses.ActiveSiege },
              $"siege: 659 phases 1, 2, 3 after 60 + 540 s, 652 again with ActiveSiege (got {string.Join(",", phases)}, state {state}, {s})");
        // The Forest player kills the tower guards and holds the capture area: Mountain's points go in 2 s, Forest's fill in 30 s.
        var me = map.Players.Values.Single();
        lock (world.Sync)
            foreach (var m in s.Squad.ToList()) Combat.Damage(me, m!, 1e9, EffectType.Physical, HitOrigin.Direct, 0, world.GameNow);
        peer.Events.Clear();
        Tick(0.5);
        var progress = peer.Events.Where(e => Code(e) == (int)EventCodes.FactionCaptureAreaProgressUpdate).Select(e => e.Parameters).LastOrDefault();
        Check(s.Claim == Outposts.Claimable && progress is not null && Convert.ToInt64(progress[7]) == -20_000,
              $"guards dead: Claimable, 655 drains Mountain's points at 1 per 2 ms (key 7 -20000) (got {s})");
        for (var t = 0; t < 70 && s.Owner == 4; t++) Tick(0.5);
        var claimed = peer.Events.FirstOrDefault(e => Code(e) == (int)EventCodes.FactionFortressClaimed)?.Parameters;
        var ended = peer.Events.FirstOrDefault(e => Code(e) == (int)EventCodes.FactionFortressFightEnded)?.Parameters;
        Check(s.Owner == 2 && claimed is not null && Convert.ToInt32(claimed[1]) == 2 && ended is not null && Convert.ToInt32(ended[1]) == 2 && ended[2] is true
              && s.Fight is { Phase: FactionFortresses.Finished } && world.Fortresses.ZoneOverride("4206") is (2, 0x80),
              $"taken: 656 {{1 Forest}}, 661 {{1 Forest, 2 true}}, Finished; 468 key 9 Forest for the duchy (got {s})");
        Tick(601);
        Check(s.Fight is null && s.Squad.All(m => m is { Dead: false } && m.Flagging == 2), $"600 s later the fight is over and Forest's guards stand (got {s})");
        world.Dispose();
    }

    private static void RedZone()
    {
        // 30 s before a start time of the default file: Always rolls it.
        var (world, session, peer, clock, map) = Join("4215", new Vector2(0, 0), 6, new ServerConfig { JoinByCharacterId = true, FactionWarfare = new FactionWarfareOptions { RedZoneEvents = RedZoneEventRule.Always } },
            StartBefore());
        long StartBefore()
        {
            // The first start of the day (FW_DUCHY_EVALUATION_TIME_OF_DAY_1 13:00 − 900 s = 12:45 UTC in times.xml) minus 30 s.
            var day = DateTime.UtcNow.Date.AddDays(1);
            return (day + new TimeSpan(12, 45, 0) - TimeSpan.FromSeconds(30)).Ticks;
        }
        void Tick(double seconds)
        {
            clock.Advance(TimeSpan.FromSeconds(seconds));
            lock (world.Sync) world.Update();
        }
        Tick(1);
        var starts = world.RedZone.Data.Starts.Count;
        Check(world.RedZone.Phase == RedZoneEvents.None && starts >= 11, $"times.xml: {starts} start times a day, nothing yet");
        peer.Events.Clear();
        Tick(31);
        var note = peer.Events.FirstOrDefault(e => Code(e) == (int)EventCodes.RedZonePlayerNotification)?.Parameters;
        var mapEvent = peer.Events.FirstOrDefault(e => Code(e) == (int)EventCodes.RedZoneWorldMapEvent)?.Parameters;
        Check(world.RedZone.Phase == RedZoneEvents.Announcement && note is not null && I(note, 0) == RedZoneEvents.Announced
              && mapEvent is not null && I(mapEvent, 1) == RedZoneEvents.Announcement && I(mapEvent, 0) == world.RedZone.Until
              && world.RedZone.Until - world.GameNow > TimeSpan.FromMinutes(14).Ticks,
              $"12:45 UTC: the assault is announced (476 kind 0, 479 Announcement until +15 min) (got phase {world.RedZone.Phase})");
        // Phase 1: the red duchies' towers are Caerleon's; 468 in 4215 says PhaseActive / Phase1.
        peer.Events.Clear();
        Tick(15 * 60 + 1);
        Tick(1);
        var towers = map.Objects.Values.OfType<OutpostObject>().ToList();
        var cluster = peer.Events.Where(e => Code(e) == (int)EventCodes.FactionWarfareClusterState).Select(e => e.Parameters).LastOrDefault();
        Check(world.RedZone.Phase == RedZoneEvents.Phase1 && towers.Count > 0 && towers.All(t => t.State.Owner == 6)
              && cluster is not null && I(cluster, 11) == RedZoneEvents.PhaseActive && I(cluster, 12) == RedZoneEvents.Phase1
              && I(cluster, 13) == world.RedZone.Until,
              $"phase 1: 4215's {towers.Count} tower(s) Caerleon's, 468 keys 11 PhaseActive, 12 Phase1, 13 its end (got {cluster?.GetValueOrDefault((byte)11)}/{cluster?.GetValueOrDefault((byte)12)})");
        for (var t = 0; t < 60; t++) Tick(5);
        var standings = peer.Responses.Count;
        lock (world.Sync) session.OnRequest(Request(new RequestRedZoneEventStandingsRequest(), 7), encrypted: true);
        var answer = peer.Responses.Skip(standings).Single().Parameters;
        Check(answer.ContainsKey(0), $"540 answered (got {string.Join(" ", answer.Select(kv => $"{kv.Key}={kv.Value}"))}; {world.RedZone.Describe()})");
        Check(answer[0] is long[] { Length: 6 } st && st[5] > 0, $"540: the standings by faction, Caerleon's growing (got {string.Join("/", answer.GetValueOrDefault((byte)0) as long[] ?? [])})");
        lock (world.Sync) world.RedZone.Advance();
        Check(world.RedZone.Phase == RedZoneEvents.Phase2 && world.RedZone.Phase2Duchies.Count == 2, $"phase 2 in {string.Join(", ", world.RedZone.Phase2Duchies)}");
        peer.Events.Clear();
        var before = map.Players.Values.Single().Character.FactionPoints[5];
        Tick(30 * 60 + 1);
        var end = peer.Events.FirstOrDefault(e => Code(e) == (int)EventCodes.RedZonePlayerNotification)?.Parameters;
        var after = map.Players.Values.Single().Character.FactionPoints[5];
        Check(world.RedZone.Phase == RedZoneEvents.None && end is not null && I(end, 0) == RedZoneEvents.Ended && I(end, 1) == 1
              && I(end, 2) == (long)Math.Floor(496217.11 / 20 * 10000) && after - before >= I(end, 2) && towers.All(t => t.State.Owner == 5),
              $"the end: 476 {{0 ended, 1 place 1, 2 496217.11 / 20 points}}, the points earned, the towers back to Swamp (got {end?.GetValueOrDefault((byte)1)} {end?.GetValueOrDefault((byte)2)}, {before} → {after})");
        world.Dispose();
    }
}
