using System.Numerics;
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
/// The Mists, phase A (analysis/work/stageL/mists/SPEC.md §7, "SPEC" here; light: one check per item): the client tables, the map filter, the a9i
/// layout and its name and bounds, the presets (A1-A3, A11, A12); then on FakePeer sessions with a manual clock: a portal on a royal T5 marker
/// (324 key 16, the distribution), the enter cast and its push, the zone's objects and 521, the camps, a mist boss and its chest, a party in a
/// solo and a duo zone, the ways out (Brecilien, the open world, a border, op 476), the relog, the shutdown and the empty close (A4-A10).
/// </summary>
public static class MistsTest
{
    public static IEnumerable<(string, Action)> All =>
    [
        ("mists: mists.xml and its metadata (11 modes, 309 maps, 21 pools, 23 masters, 83 slots), the map filter, the a9i round trip, name and bounds, presets, Brecilien", Data),
        ("mists: a portal on a royal T5 marker (324 key 16), the distribution split by lethality, ENTER_MISTS (960, cancel, the push {0, 3, 4, 5}), 537/520/521, camps, ways out, relog", Live),
        ("mists: a party in a solo and a duo portal, a duo zone alone at 0.6, a mist boss's reward chest, the shutdown push and the empty close", Teams),
        ("mists: Shared (official): a border never leads back into its own zone; the Random open-world exit on the entrance's continent", Shared),
    ];

    private static ClusterStore Store => Client.Clusters!;

    private static void Check(bool cond, string what) => AlbionTests.Check(cond, what);

    internal static int Code(EventData e) => Convert.ToInt32(e.Parameters.GetValueOrDefault(ReservedKeys.EventCode));

    private static void Data()
    {
        var data = Store.Mists;
        var yellow = data.Modes["MISTS_SOLO_YELLOW"];
        var mists = Store.Dungeons.DifficultyGroups.GetValueOrDefault("mists");
        Check(data.Modes.Count == 11 && data.Mapsets.Count == 11 && data.MapCount == 309 && data.Pools.Count == 21 && data.Masters.Count == 23
              && data.Masters.Values.Sum(m => m.Slots.Count) == 83 && data.Templates.Count == 51 && data.Dungeons.GetValueOrDefault("MISTS_DNG_UND_01") == 124
              && data.CityTarget == "5001" && yellow.MaxTeamSize == 1 && data.Modes["MISTS_DUO_BLACK"].MaxTeamSize == 2
              && mists?.GetValueOrDefault(4) is { } l4 && Math.Abs(l4.Loot - 1.8106) < 1e-4,
              $"11 modes, 11 mapsets, 309 maps, 21 pools, 23 masters with 83 slots, 51 templates, 124 rooms; city 5001; team sizes 1/2; mists level 4 loot 1.8106 (got {data.MapCount} maps)");

        // A2: rarity 0 draws the 15 COMMON T5 maps, rarity 4 the 15 RARE T7 ones.
        var common = data.MapsFor(yellow, 0);
        var rare = data.MapsFor(yellow, 4);
        Check(common.Count == 15 && common.All(m => m.Id.StartsWith("SOLO_YELLOW_COMMON_", StringComparison.Ordinal) && m.Tier == 5)
              && rare.Count == 15 && rare.All(m => m.Id.StartsWith("SOLO_YELLOW_RARE_", StringComparison.Ordinal) && m.Tier == 7),
              $"rarity 0: the 15 COMMON T5 maps, rarity 4: the 15 RARE T7 ones (got {common.Count}/{rare.Count})");

        // A3: a zone's a9i round trip: type 2, the master then its five slots, i32 0 rooms, the tail; slots at R(master)·(x, z) with their y; the name; the bounds.
        var map = data.Mapsets["MAPSET_SOLO_YELLOW"].First(m => m.Master == "MISTS_006_M1_S4");
        var id = MistsIds.InstanceId(Guid.NewGuid());
        var layout = MistsGenerator.Zone(Store, data, yellow, map, 0, "UNDEAD", id, new Random(5));
        var read = MistsLayout.Read(layout.Write());
        var rot = read.Templates[0].Rotation;
        var slots = data.Masters["MISTS_006_M1_S4"].Slots;
        bool At(DungeonTemplate t, MistsSlot s)
        {
            var (c, n) = (MathF.Round(MathF.Cos(rot * MathF.PI / 180)), MathF.Round(MathF.Sin(rot * MathF.PI / 180)));
            return Vector2.Distance(t.Position, new Vector2(s.X * c + s.Z * n, -s.X * n + s.Z * c)) < 0.01f && Math.Abs(t.Y - s.Y) < 0.01f;
        }
        var name = ClusterNameInfo.Parse(read.Name);
        var (o, z) = rot switch { 0 => (new Vector2(-465, -465), new Vector2(940, 970)), 180 => (new Vector2(-475, -505), new Vector2(940, 970)), _ => (new Vector2(-505, -475), new Vector2(970, 940)) };
        Check(read is { Id: var rid, Mode: "MISTS_SOLO_YELLOW", Level: 0 } && rid == id && read.Map == map.Id && read.Templates.Count == 6 && read.Templates[0] is { Slot: "instanceslot_00", Template: "MISTS_006_M1_S4" }
              && read.Templates.All(t => t.Rooms is { Length: 0 }) && read.Templates.Skip(1).Select((t, i) => At(t, slots[i])).All(x => x)
              && read.Templates.Count(t => t.Template is "MISTS_S_EXIT_NON_ROADS_01") <= 1 && read.MiniMap.Preset == "Mists"
              && name is { Type: 21, Biome: 8, Continent: 4, Tier: 5 } && !name.Id.Contains('_') && read.Name.Contains("_MIS_MI_GRN_T5_", StringComparison.Ordinal)
              && (rot is 90 or 270 || read.Origin == o && read.Size == z) && (rot is 0 or 180 || read.Size == z),
              $"the a9i of {map.Id}: {read.Name}, master turned {rot}, {read.Templates.Count} templates, origin {read.Origin} size {read.Size}");
        var stat = Store.Get("5100");
        Check(stat.Origin == new Vector2(-505, -511) && stat.Size == new Vector2(980, 986) && MistsGenerator.Turned(new Vector2(-505, -511), new Vector2(475, 475), 0) == (stat.Origin, stat.Size)
              && stat.Critical.Count(m => m.Type == "mistscityentrance") == 3,
              "5100 (MISTS_006_STATIC_NORTH at rotation 0): its file's bounds are the master's collider bounds; 3 city entrances");

        // A11, A12: the presets, content/mists.json, Brecilien's stations and its bank's own vault.
        var cfg = NewDir();
        var none = Path.Combine(cfg, "albionlocal.json");
        var file = MistsSettings.Parse(File.ReadAllText(Path.Combine(DataDir(), "..", "content", "mists.json")), "mists.json");
        var stations = StationSpawns.Load(Path.Combine(DataDir(), "..", "content", "spawns"));
        Check(LoadTestConfig(none, ["--Preset=pve"]).Mists is { Matchmaking: MistsMatchmaking.Private, LifetimeFactor: 2, BlockFactor: 0.5, DuoSoloScaling: 0.6, OpenWorldExit: MistsExitTarget.Entrance, BossChest: true, RarityWeights.Count: 5 }
              && LoadTestConfig(none, ["--Preset=official"]).Mists == new MistsOptions()
              && LoadTestConfig(none, ["--Preset=official", "--Mists:LifetimeFactor=3"]).Mists is { LifetimeFactor: 3, Matchmaking: MistsMatchmaking.Shared }
              && file.Texts.OrderBy(t => t.Key).SequenceEqual(MistsSettings.DefaultTexts.OrderBy(t => t.Key)) && file.Treasures.Count == MistsSettings.DefaultTreasures.Count
              && stations.For("5000").Count == 13 && Character.VaultKey(Islands.VaultLocation(new Map("5002")), Guid.Empty) != Character.VaultKey(Islands.VaultLocation(new Map("0000")), Guid.Empty),
              "Mists:* under pve (Private, x2, x0.5, 0.6, Entrance, boss chest, rarity weights), official (the record's), a key wins; content/mists.json; 12 Brecilien stations and a stable; 5002's vault is its own");
        try { Directory.Delete(cfg, true); } catch (IOException) { }
    }

    /// <summary>A world of the record's config (official) with the given mists options, on the real client data.</summary>
    internal static (World World, ManualClock Clock, string Dir, SqliteCharacterRepository Characters) NewWorld(MistsOptions options, int seed)
    {
        var dir = NewDir();
        var characters = Repository(dir);
        var clock = new ManualClock();
        var anchor = DateTime.UtcNow.Ticks;
        var world = new World(new ServerConfig { JoinByCharacterId = true, Mists = options }, characters, Client.Items, Client.GameData, Client.Spells)
        {
            Sync = new object(), Clock = clock, Clusters = Store, MapSeed = seed, GameTicks = () => anchor + clock.Now.Ticks,
        };
        world.MobAi = false;
        return (world, clock, dir, characters);
    }

    /// <summary>The first royal T5 cluster with SOLO exit markers (V4: Royal T4 has no mists entry), resolved.</summary>
    internal static ClusterTemplate RoyalT5()
    {
        foreach (var id in new[] { "4201", "2213", "1204", "0209", "4206", "4209", "4213", "4212" })
            if (Store.Get(id) is { Name.Tier: 5 } t && t.DungeonMarkers.Any(m => m.Type == "SOLO")) return t;
        throw new InvalidOperationException("no royal T5 cluster with SOLO markers");
    }

    internal static Character Hero(SqliteCharacterRepository characters, string cluster, Vector2 at)
    {
        var hero = StarterKit.Default.NewCharacter("M" + Guid.NewGuid().ToString("N")[..10]);
        (hero.Cluster, hero.PositionX, hero.PositionY) = (cluster, at.X, at.Y);
        Check(characters.Create(hero), "character created");
        return hero;
    }

    /// <summary>A mists portal of <paramref name="mode"/> at <paramref name="rarity"/> on the marker nearest <paramref name="near"/>.</summary>
    internal static RandomDungeonExit Portal(World world, string cluster, Vector2 near, string mode, byte rarity)
    {
        lock (world.Sync)
        {
            var map = world.Maps.Find(cluster)!;
            var marker = map.Template!.DungeonMarkers.Where(m => m.Type == "SOLO").MinBy(m => Vector2.Distance(m.Position, near))!;
            var exit = world.Dungeons.SpawnOnMarker(map, marker, mode);
            world.Dungeons.Reroll(exit, rarity);
            return exit;
        }
    }

    /// <summary>ENTER_MISTS at <paramref name="exit"/> by <paramref name="s"/>, the build waited for, 3 s ticked: the push (or the last response).</summary>
    internal static void Enter(World world, ManualClock clock, WorldSession s, RandomDungeonExit exit, byte counter)
    {
        lock (world.Sync) s.OnRequest(Request(Cast(world, exit.Position, counter), null), encrypted: false);
        world.Mists.Building().Wait(TimeSpan.FromSeconds(60));
        Tick(world, clock, 3.1);
    }

    internal static CastStartRequest Cast(World world, Vector2 aim, byte counter) =>
        new() { wf = world.GameNow, dx = Mists.EnterSlot, d0 = world.Spells!.IdOf(Mists.EnterSpell), d2 = aim, d4 = counter };

    internal static void Tick(World world, ManualClock clock, double seconds)
    {
        clock.Advance(TimeSpan.FromSeconds(seconds));
        lock (world.Sync) world.Update();
    }

    internal static void Move(World world, WorldSession s, Vector2 to)
    {
        lock (world.Sync) s.OnRequest(Request(new MoveRequest { wf = DateTime.UtcNow.Ticks, v6 = [to.X, to.Y], v8 = [to.X, to.Y], v9 = 0f }, null), encrypted: false);
    }

    private static void Live()
    {
        var (world, clock, dir, characters) = NewWorld(ServerConfig.PveDefaults.Mists, 21);
        var t5 = RoyalT5();
        var spot = t5.DungeonMarkers.First(m => m.Type == "SOLO").Position + new Vector2(3, 0);
        var hero = Hero(characters, t5.Id, spot);
        var watcherChar = Hero(characters, t5.Id, spot + new Vector2(0, 2));
        var (s, peer, _) = Join(world, hero.Id);
        var (_, watcher, _) = Join(world, watcherChar.Id);
        List<EventData> Of(FakePeer on, EventCodes code, int from = 0) => [.. on.Events.Skip(from).Where(e => Code(e) == (int)code)];

        // A4: the forced portal's 324 {3, 16 mode, 17 -1, no 4}; the T5 SOLO choices hold it; the V5 split.
        var exit = Portal(world, t5.Id, spot, "MISTS_SOLO_YELLOW", 1);
        var shown = Of(peer, EventCodes.NewRandomDungeonExit).LastOrDefault(e => Convert.ToInt64(e.Parameters[0]) == exit.Id);
        var dd = Store.Dungeons;
        Check(shown?.Parameters.GetValueOrDefault((byte)16) is "MISTS_SOLO_YELLOW" && Convert.ToInt64(shown.Parameters[17]) == -1 && !shown.Parameters.ContainsKey(4)
              && Convert.ToInt32(shown.Parameters[9]) == 1 && world.Dungeons.ChoicesOn(world.Maps.Find(t5.Id)!, "SOLO").Any(c => c.Exit == "MISTS_SOLO_YELLOW" && c.Weight > 0)
              && dd.DistributionFor("Royal", "SOLO", 6, false)?.Exits.FirstOrDefault(c => c.Exit == "MISTS_SOLO_YELLOW")?.Weight == 46
              && dd.DistributionFor("Royal", "SOLO", 6, true)?.Exits.FirstOrDefault(c => c.Exit == "MISTS_SOLO_BLACK")?.Weight == 35,
              $"324 {{3 MISTS_SOLO_YELLOW, 9 1, 16 the mode, 17 -1, no 4}} in {t5.Id}; the T5 choices hold it; Royal SOLO T6 non-lethal YELLOW 46, lethal BLACK 35");

        // A5: 10 m off: 960; at the portal: no answer, CastStart {9 25} to the observer; a CastCancel within 3 s: no push.
        var before = peer.Responses.Count;
        lock (world.Sync) s.OnRequest(Request(Cast(world, exit.Position + new Vector2(10, 0), 1), 3), encrypted: false);
        var far = peer.Responses[^1];
        var mark = watcher.Events.Count;
        lock (world.Sync) s.OnRequest(Request(Cast(world, exit.Position, 2), null), encrypted: false);
        var started = Of(watcher, EventCodes.CastStart, mark).SingleOrDefault();
        world.Mists.Building().Wait(TimeSpan.FromSeconds(60));
        Tick(world, clock, 1);
        bool hitKeeps;
        lock (world.Sync)
        {
            Flagging.Interrupt(s.Player!, "damage"); // ENTER_MISTS: damagecancelscasting false (FakeSlotCast.DamageCancels)
            hitKeeps = world.Mists.Entering(s.Player!);
        }
        lock (world.Sync) s.OnRequest(Request(new CastCancelRequest { wf = world.GameNow }, null), encrypted: false);
        Tick(world, clock, 3);
        Check(far.ReturnCode == WorldSession.CastRefused && peer.Responses.Count == before + 1 && Convert.ToInt32(started?.Parameters[9]) == Mists.EnterSlot
              && hitKeeps && world.Mists.Zones.Count == 0 && s.Status == SessionStatus.InWorld,
              $"10 m off: 960 (got {far.ReturnCode}); at the portal no answer, CastStart {{9 25}} to the observer; a hit keeps it; cancelled: no zone, no push");

        // A5: the push after the 3 s: 41 without 255, {0 @MISTS@, 3 a9i, 4 MISTS_SOLO_YELLOW, 5 the tier}; the Join's key 8.
        Enter(world, clock, s, exit, 3);
        var push = peer.Responses[^1];
        var zone = world.Mists.Zones.SingleOrDefault();
        Check(zone is not null && Convert.ToInt32(push.Parameters[ReservedKeys.OperationCode]) == (int)OperationCodes.ChangeCluster && !push.Parameters.ContainsKey(ReservedKeys.RequestId)
              && push.Parameters.Keys.Where(k => k < 250).Order().SequenceEqual(new byte[] { 0, 3, 4, 5 }) && push.Parameters[0] is string pid && pid == zone.Id
              && pid.StartsWith(MistsIds.Prefix, StringComparison.Ordinal) && push.Parameters[4] is "MISTS_SOLO_YELLOW" && Convert.ToInt32(push.Parameters[5]) == zone.Tier
              && push.Parameters[3] is byte[] bytes && MistsLayout.Read(bytes).Level == 1 && s.Status == SessionStatus.Transferring,
              $"the push {{0 @MISTS@<guid>, 3 a9i, 4 MISTS_SOLO_YELLOW, 5 T{zone?.Tier}}} with no request id (got {string.Join(",", push.Parameters.Keys)})");

        // A6: phase A: 537 at the border tiles, one 523 (rarity 1), at most one 527, the 520 ending ≤ now + 30 s; after JoinFinished 521; camps; mobs of the tier.
        var (s2, peer2, join) = Join(world, hero.Id);
        var me = s2.Player!;
        var map = me.Map!;
        var fin = peer2.Events.FindIndex(e => Code(e) == (int)EventCodes.JoinFinished);
        var phaseA = peer2.Events.Take(fin).ToList();
        var borders = phaseA.Where(e => Code(e) == (int)EventCodes.NewMistsBorderExit).ToList();
        var tiles = zone!.Template.Critical.Where(m => m.Type == "mistsborderexit").Select(m => m.Position).ToList();
        var ret = phaseA.SingleOrDefault(e => Code(e) == (int)EventCodes.NewMistsImmediateReturnExit);
        var info = peer2.Events.Skip(fin).SingleOrDefault(e => Code(e) == (int)EventCodes.MistsPlayerJoinedInfo);
        var mobs = map.Objects.Values.OfType<Mob>().ToList();
        Check(join.a8i == zone.Id && borders.Count >= 1 && borders.All(b => b.Parameters[1] is float[] p && tiles.Contains(new Vector2(p[0], p[1])))
              && phaseA.Count(e => Code(e) == (int)EventCodes.NewMistsOpenWorldExit) <= 1 && phaseA.Count(e => Code(e) == (int)EventCodes.NewMistsCityEntrance) <= 1
              && map.Objects.Values.OfType<MistsOpenWorldExit>().Count() == 1 && map.Objects.Values.OfType<MistsCityEntrance>().Count() <= 1
              && ret is not null && Convert.ToInt64(ret.Parameters[3]) <= world.GameNow + TimeSpan.FromSeconds(30).Ticks
              && info is not null && Convert.ToInt64(info.Parameters[0]) == Convert.ToInt64(ret.Parameters[0]) && Convert.ToInt64(info.Parameters[1]) == Convert.ToInt64(ret.Parameters[3])
              && info.Parameters[2] is string back && back == t5.Id && info.Parameters[4] is string target && target == t5.Id && Convert.ToInt64(info.Parameters[5]) == zone.ShutdownTicks
              && world.WorldChests.AreasOf(map).Count >= 1 && mobs.Where(m => m.Template.Name.Contains("_MOB_CAMP_MISTS_", StringComparison.Ordinal)).ToList() is { Count: > 5 } camp && camp.All(m => m.Template.Tier == zone.Tier),
              $"Join phase A: {borders.Count} border exits in view at their tiles, the 520 (one 523 in the zone, rarity 1); 521 {{0 the 520, 1 its end, 2/4 {t5.Id}, 5 the shutdown}}; {world.WorldChests.AreasOf(map).Count} camp(s), the camps' mobs of T{zone.Tier} (Mists spawn behaviours)");

        // A8: op 476 after its 30 s refused; then the open-world exit (Entrance) back to the portal's front.
        Tick(world, clock, 31);
        lock (world.Sync) s2.OnRequest(Request(new MistsUseImmediateReturnExitRequest { wf = world.GameNow, vw = Convert.ToInt64(ret!.Parameters[0]) }, 7), encrypted: false);
        var late = peer2.Responses[^1];
        var owe = map.Objects.Values.OfType<MistsOpenWorldExit>().Single();
        Move(world, s2, owe.Position);
        lock (world.Sync) s2.OnRequest(Request(new ChangeClusterRequest { ee = owe.Id }, 8), encrypted: false);
        var outward = peer2.Responses[^1];
        var (s3, _, home) = Join(world, hero.Id);
        var front = exit.ArrivalOut(t5.Id);
        Check(late.ReturnCode != 0 && outward is { ReturnCode: 0 } && outward.Parameters[0] is string o && o == t5.Id && home.a8i == t5.Id
              && Vector2.Distance(home.a8j, front.Position) < 0.01f && s3.Player!.Character.DungeonReturn is null,
              $"476 after 30 s refused ({late.ReturnCode}); the open-world exit {{0: {t5.Id}}}, the Join in front of the portal (got {outward.ReturnCode} {home.a8j})");

        // A8, A10: a second zone: op 476 within 30 s (RC 0, then the push out); a border (a new zone {0, 3, 4, 5} with 255, 521 again); the relog's keys 1, 4, 5, 6.
        var exit2 = Portal(world, t5.Id, s3.Player!.PositionAt(DateTime.UtcNow), "MISTS_SOLO_YELLOW", 0);
        Move(world, s3, exit2.Position + new Vector2(2, 0));
        Enter(world, clock, s3, exit2, 4);
        var zone2 = world.Mists.Zones.Single(z => z != zone && !z.Closed);
        var (s4, peer4, _) = Join(world, hero.Id);
        var portal = zone2.Returns[hero.Id];
        var n4 = peer4.Responses.Count;
        lock (world.Sync) s4.OnRequest(Request(new MistsUseImmediateReturnExitRequest { wf = world.GameNow, vw = portal.Id }, 9), encrypted: false);
        var quick = peer4.Responses.Skip(n4).ToList();
        Check(quick is [{ ReturnCode: 0 } ok, var pushOut] && Convert.ToInt32(ok.Parameters[ReservedKeys.OperationCode]) == (int)OperationCodes.MistsUseImmediateReturnExit
              && pushOut.Parameters[0] is string pt && pt == t5.Id && !pushOut.Parameters.ContainsKey(ReservedKeys.RequestId),
              $"476 within 30 s: RC 0, then the push {{0: {t5.Id}}} (got {quick.Count} responses)");
        var (s5, _, _) = Join(world, hero.Id);
        var exit3 = Portal(world, t5.Id, s5.Player!.PositionAt(DateTime.UtcNow), "MISTS_SOLO_YELLOW", 0);
        Move(world, s5, exit3.Position + new Vector2(2, 0));
        Enter(world, clock, s5, exit3, 5);
        var zone3 = world.Mists.Zones.Single(z => z != zone && z != zone2 && !z.Closed);
        var (s6, peer6, _) = Join(world, hero.Id);
        Tick(world, clock, 11); // the exit cooldown
        var border = s6.Player!.Map!.Objects.Values.OfType<MistsBorderExit>().First();
        Move(world, s6, border.Position);
        var n6 = peer6.Responses.Count;
        lock (world.Sync) s6.OnRequest(Request(new ChangeClusterRequest { ee = border.Id }, 10), encrypted: false);
        world.Mists.Building().Wait(TimeSpan.FromSeconds(60));
        for (var i = 0; i < 200 && peer6.Responses.Count == n6; i++) // the build's answer comes through World.Post
        {
            Thread.Sleep(10);
            Tick(world, clock, 0.01);
        }
        var crossed = peer6.Responses[^1];
        var zone4 = world.Mists.Zones.SingleOrDefault(z => z != zone && z != zone2 && z != zone3 && !z.Closed);
        var (s7, peer7, join7) = Join(world, hero.Id);
        lock (world.Sync) s7.OnDisconnected("logout");
        var loginPeer = new FakePeer();
        var login = new LoginSession(loginPeer, world.Config, characters, StarterKit.Default) { World = world };
        lock (world.Sync)
        {
            login.OnRequest(Request(TestLogin(), 1), encrypted: true);
            login.OnRequest(Request(new SelectCharacterRequest { abc = hero.Name }, null), encrypted: true);
        }
        var select = new SelectCharacterResponse();
        AlbionSerializer.FillFields(select, loginPeer.Responses[^1].Parameters);
        Check(zone4 is not null && crossed is { ReturnCode: 0 } && crossed.Parameters[0] is string cid && cid == zone4.Id && crossed.Parameters.ContainsKey(ReservedKeys.RequestId)
              && crossed.Parameters.Keys.Where(k => k < 250).Order().SequenceEqual(new byte[] { 0, 3, 4, 5 }) && join7.a8i == zone4.Id
              && peer7.Events.Count(e => Code(e) == (int)EventCodes.MistsPlayerJoinedInfo) == 1
              && select.blo == zone4.Id && select.blr is { Length: > 0 } && select.bls == "MISTS_SOLO_YELLOW" && select.blt == zone4.Tier,
              $"the border: {{0 a new @MISTS@, 3, 4, 5}} with the request id, 521 after its Join; the relog: SelectCharacter {{1 the id, 4 a9i, 5 MISTS_SOLO_YELLOW, 6 T{zone4?.Tier}}}");

        // A10: after a restart (no zone) the character goes back to its way back; A9: the zones left empty close after EmptySeconds.
        var gone = world.LoadCharacter(hero.Id)!.Clone();
        bool returned;
        lock (world.Sync)
        {
            world.Mists.Close(zone4!, "test");
            returned = world.Mists.ReturnIfClosed(gone);
        }
        Tick(world, clock, ServerConfig.PveDefaults.Mists.EmptySeconds + 1);
        Check(returned && gone.Cluster == t5.Id && gone.DungeonReturn is null && zone.Closed && zone2.Closed && world.Maps.Find(zone.Id) is null,
              $"a zone that is gone: back at the way back {t5.Id} (got {gone.Cluster}); the zones left empty closed after {ServerConfig.PveDefaults.Mists.EmptySeconds} s");
        world.Dispose();
        characters.Dispose();
        try { Directory.Delete(dir, true); } catch (IOException) { }

        // A4: with Mists:Enabled false the same markers draw no mists portal.
        var (off, _, offDir, offChars) = NewWorld(ServerConfig.PveDefaults.Mists with { Enabled = false }, 21);
        var (s8, _, _) = Join(off, Hero(offChars, t5.Id, spot).Id);
        Check(off.Dungeons.ChoicesOn(s8.Player!.Map!, "SOLO") is { Count: > 0 } choices && choices.All(c => !c.Exit.StartsWith("MISTS_", StringComparison.Ordinal)),
              "Mists:Enabled false: the T5 SOLO markers draw no MISTS_* exit");
        off.Dispose();
        offChars.Dispose();
        try { Directory.Delete(offDir, true); } catch (IOException) { }
    }

    private static void Teams()
    {
        var (world, clock, dir, characters) = NewWorld(ServerConfig.PveDefaults.Mists with { LifetimeFactor = 0.05 }, 23);
        var t5 = RoyalT5();
        var spot = t5.DungeonMarkers.First(m => m.Type == "SOLO").Position + new Vector2(3, 0);
        var heroes = Enumerable.Range(0, 3).Select(i => Hero(characters, t5.Id, spot + new Vector2(0, i))).ToList();
        var sessions = heroes.Select(h => Join(world, h.Id).Session).ToList();
        lock (world.Sync)
        {
            var (a, b, c) = (sessions[0].Player!, sessions[1].Player!, sessions[2].Player!);
            Check(world.Parties.Invite(a, b.Character.Id, null, out _) == 0 && world.Parties.AnswerInvitation(b, true, false, Parties.NoReason) == 0
                  && world.Parties.Invite(a, c.Character.Id, null, out _) == 0 && world.Parties.AnswerInvitation(c, true, false, Parties.NoReason) == 0, "a party of three");
        }
        MistsZone? ZoneOf(Character h) => world.Mists.Zones.FirstOrDefault(z => !z.Closed && z.Entered.Contains(h.Id));

        // A7: two party members at one SOLO portal: two zones; three at one DUO portal: the first two share one, the third has its own.
        var solo = Portal(world, t5.Id, spot, "MISTS_SOLO_YELLOW", 0);
        Enter(world, clock, sessions[0], solo, 1);
        Enter(world, clock, sessions[1], solo, 1);
        var (z0, z1) = (ZoneOf(heroes[0]), ZoneOf(heroes[1]));
        foreach (var h in heroes.Take(2)) _ = Join(world, h.Id); // into their zones, then out again by the close
        lock (world.Sync) { world.Mists.Close(z0!, "test"); world.Mists.Close(z1!, "test"); }
        sessions = [.. heroes.Select(h => Join(world, h.Id).Session)];
        foreach (var x in sessions) Move(world, x, spot);
        var duo = Portal(world, t5.Id, spot, "MISTS_DUO_BLACK", 1);
        lock (world.Sync) // the first two cast together: both casts under way before either ends
            foreach (var x in sessions.Take(2)) x.OnRequest(Request(Cast(world, duo.Position, 2), null), encrypted: false);
        world.Mists.Building().Wait(TimeSpan.FromSeconds(60));
        Tick(world, clock, 3.1);
        Enter(world, clock, sessions[2], duo, 2);
        var (d0, d1, d2) = (ZoneOf(heroes[0]), ZoneOf(heroes[1]), ZoneOf(heroes[2]));
        var (j0, j1) = (Join(world, heroes[0].Id).Join, Join(world, heroes[1].Id).Join);
        Check(z0 is not null && z1 is not null && z0 != z1 && d0 is not null && d0 == d1 && !d0.Scaled && d2 is not null && d2 != d0 && d0.Mode.Name == "MISTS_DUO_BLACK"
              && j0.a8i == d0.Id && j1.a8i == d0.Id && Vector2.Distance(j0.a8j, j1.a8j) < 0.01f,
              "a SOLO portal: two party members in two zones; a DUO portal cast by two at once: one zone, not scaled, both at one border; the third goes on its own");

        // A7: the third, alone in a duo zone: its mobs at 0.6; A6: the mist boss of a rarity 1 zone and its reward chest (pve) at its death.
        var alone = Join(world, heroes[2].Id);
        var map = alone.Session.Player!.Map!;
        var me = alone.Session.Player!;
        var planned = d2!.Treasures.Count(t => t.Subtype == "MYTHICALMOBS");
        Check(d2.Scaled && map.MobHealthFactor == 0.6 && d2.Level == 1, $"alone in a duo zone: Scaled, mobs at {map.MobHealthFactor}; {planned} mist boss(es) planned at rarity 1");
        // The planned ones come later than this short-lived zone lasts: one is due now, at the player.
        d2.Treasures.RemoveAll(t => t.Subtype == "MYTHICALMOBS");
        d2.Treasures.Add(new MistsTreasureSpot("MYTHICALMOBS", $"T{d2.Tier}_MOB_MISTS_GRIFFIN", me.PositionAt(DateTime.UtcNow) + new Vector2(4, 0), TimeSpan.Zero));
        Tick(world, clock, 0.1);
        var boss = map.Objects.Values.OfType<Mob>().FirstOrDefault(m => d2.Bosses.Contains(m.Id));
        Check(boss is not null && boss.Template.Name.StartsWith($"T{d2.Tier}_MOB_MISTS_", StringComparison.Ordinal), $"the mist boss of T{d2.Tier} unlocked: {boss?.Template.Name}");
        lock (world.Sync) Combat.Damage(me, boss!, 1e9, EffectType.Physical, HitOrigin.Direct, 0, world.GameNow);
        var chest = map.Objects.Values.OfType<LootChest>().FirstOrDefault(c => c.Personal && c.SpawnpointName == "MISTS_TREASURE_SOLO");
        Check(chest is not null && chest.ViewerState(me).State == 6 && d2.Bosses.Count == 0, "the mist boss slain: a personal reward chest, unlocked for its damager");

        // A9: LifetimeFactor 0.05 (60 s): the Closing line (within the countdown already at the Join), then at the shutdown the push out to the way back, the map gone.
        var peer = alone.Peer;
        Tick(world, clock, Math.Max(0.1, (d2.ShutdownAt - world.Now).TotalSeconds + 0.5));
        var warned = peer.Events.Any(e => e.Parameters.Values.OfType<string>().Any(t => t.Contains("mist thickens", StringComparison.Ordinal)));
        var pushed = peer.Responses[^1];
        Check(warned && d2.Closed && pushed.Parameters[0] is string to && to == t5.Id && world.Maps.Find(d2.Id) is null,
              $"the Closing line, then at the shutdown the push {{0: {t5.Id}}} and the map unloaded (got {pushed.Parameters.GetValueOrDefault((byte)0)})");
        world.Dispose();
        characters.Dispose();
        try { Directory.Delete(dir, true); } catch (IOException) { }
    }

    private static void Shared()
    {
        var (world, clock, dir, characters) = NewWorld(new MistsOptions(), 25);
        var info = Store.World; // read: the Random exit draws from world.xml
        var t5 = RoyalT5();
        var spot = t5.DungeonMarkers.First(m => m.Type == "SOLO").Position + new Vector2(3, 0);
        var hero = Hero(characters, t5.Id, spot);
        var (s, _, _) = Join(world, hero.Id);
        Enter(world, clock, s, Portal(world, t5.Id, spot, "MISTS_SOLO_YELLOW", 1), 1);
        var zone = world.Mists.Zones.Single();
        var (s2, _, _) = Join(world, hero.Id);
        MistsZone? next;
        lock (world.Sync) next = world.Mists.NextZone(s2.Player!, zone, $"border:{zone.Id}:1", out _);
        var target = zone.OpenWorldTarget is { } t ? info.Clusters.First(c => c.Id == t) : null;
        var continent = target is null ? 0 : ClusterNameInfo.Parse(target.File)?.Continent;
        Check(zone.TeamKey.Length == 0 && next is null && zone.Entrance == t5.Id
              && (target is null || continent == 1 || Store.Archetypes.Of(target.Type).PvpWarning == 4),
              $"Shared: the zone's own border finds no zone to join (not itself); the open-world exit {target?.Id} on the royal entrance's continent (got continent {continent})");
        world.Dispose();
        characters.Dispose();
        try { Directory.Delete(dir, true); } catch (IOException) { }
    }

    internal static (WorldSession Session, FakePeer Peer, JoinResponse Join) Join(World world, Guid key)
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
