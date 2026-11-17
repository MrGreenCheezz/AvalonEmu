using System.Numerics;
using AlbionLocal.Photon;
using AlbionLocal.Photon.Serialization;
using AlbionLocal.Server;
using AlbionLocal.Server.Content;
using AlbionLocal.Server.Data;
using AlbionLocal.Server.Entities;
using AlbionLocal.Server.Network;
using AlbionLocal.Server.Protocol;
using static AlbionLocal.SelfTest.AlbionTests;

namespace AlbionLocal.SelfTest;

/// <summary>
/// Dragon areas (analysis/work/stageR/dragon-areas/SPEC.md): dragonarea.xml, an entrance in an Outlands cluster (694) and its life cycle; part two:
/// the a88 layout of DRAGON_001 and its cluster, the use (699) and the entry (550 → the push {0, 3, 4, 5}), the instance's exits (695) and mobs,
/// the way out (237 → in front of the entrance), and the entrances drawn by treasuredistribution.xml on a royal yellow map.
/// </summary>
public static class DragonAreaTest
{
    public static IEnumerable<(string, Action)> All =>
    [
        ("dragon areas: dragonarea.xml, an entrance (694 keys), closed after its lifetime, gone after despawn", Entrance),
        ("dragon areas: the a88 layout of DRAGON_001 (type 4 tail, name, drake islands, client reader) and its cluster (exits, mobs)", Layout),
        ("dragon areas: use 549 (699 Channeling → WaitingToEnter), enter 550, the push into the instance, its exits and mobs, out by an exit", Enter),
        ("dragon areas: entrances drawn by treasuredistribution.xml on a royal yellow map's tracking spots", Natural),
    ];

    private const string Cluster = "2325";

    private static ClusterStore Store => Client.Clusters!;

    private static void Entrance()
    {
        var store = Store;
        _ = store.Get(Cluster);
        var dir = NewDir();
        var characters = Repository(dir);
        var clock = new ManualClock();
        var world = new World(new ServerConfig { JoinByCharacterId = true }, characters, Client.Items) { Sync = new object(), Clock = clock, Clusters = store };
        var data = store.DragonAreas;
        Check(data.EntranceTypes.Count == 6 && data.Entrance("DRAGON_AREA_ORANGE_SMALL") is ({ Tile: "DRAGON_AREA_ENTRANCE_SMALL", LifeSeconds: 600, DespawnSeconds: 300 }, 0)
              && data.Modes.TryGetValue("DRAGON_AREA_1_TO_1_ORANGE", out var mode) && mode.MapSet == "MAPSET_1_TO_1_PLAYERS_ORANGE"
              && data.MapSets[mode.MapSet] is [{ Master: "DRAGON_001", Pool: "ORANGE_SOLO_TEMPLATEPOOL", Tier: 6, RestrictRotation: true }],
              "dragonarea.xml: 6 entrance types, ORANGE_SMALL first (DRAGON_AREA_ENTRANCE_SMALL, 600 s open, 300 s closed), the 1-to-1 mode on DRAGON_001");

        var hero = StarterKit.Default.NewCharacter("D" + Guid.NewGuid().ToString("N")[..10]);
        (hero.Cluster, hero.PositionX, hero.PositionY) = (Cluster, 0, 0);
        Check(characters.Create(hero), "character created");
        var peer = new FakePeer();
        var s = new WorldSession(peer, world);
        s.OnRequest(Request(new JoinRequest { ss = hero.Id }, null), encrypted: true);
        var map = world.Maps.Find(Cluster)!;
        var start = s.Player!.PositionAt(DateTime.UtcNow);
        var (entrance, why) = world.DragonAreas.Spawn(map, start + new Vector2(0, 4), 0, "DRAGON_AREA_ORANGE_SMALL");
        Check(entrance is not null && s.Player.Visibility.Known.Contains(entrance) && entrance.Event() is { edv: 0, edy: "DRAGON_AREA_ENTRANCE_SMALL", edz: 1 } e && e.ed0 > world.GameNow,
              $"an open entrance next to the player, in view: 694 {{1 type 0, 4 its tile, 5 Open, 6 the state's end}} (got {why})");

        clock.Advance(TimeSpan.FromSeconds(601));
        world.DragonAreas.Update();
        Check(entrance!.State == DragonAreaEntranceState.Closed && map.Objects.ContainsKey(entrance.Id), "after 600 s: Closed, still standing");
        clock.Advance(TimeSpan.FromSeconds(300));
        world.DragonAreas.Update();
        Check(!map.Objects.ContainsKey(entrance.Id) && world.DragonAreas.Entrances.Count == 0, "after 300 s more: gone");
        world.Dispose();
        characters.Dispose();
        try { Directory.Delete(dir, true); } catch (IOException) { }
    }

    private static void Layout()
    {
        var data = Store.DragonAreas;
        var mode = data.Modes["DRAGON_AREA_1_TO_1_ORANGE"];
        var map = DragonAreaGenerator.PickMap(data, mode, new Random(1))!;
        var master = data.Masters.GetValueOrDefault("DRAGON_001");
        Check(master is { Slots.Count: 28 } && master.Slots.Count(s => s.Tags.Contains("DRAKE_ISLAND")) == 4 && data.Pools.ContainsKey(map.Pool)
              && data.Templates.ContainsKey("M_DR_Drake_01") && map.RollTags is [("orangesolo", _)],
              $"dragonareatemplatemetadata.xml: DRAGON_001 with 28 slots, 4 of them DRAKE_ISLAND; the pool and templates (got {master?.Slots.Count})");
        var id = DragonAreaIds.InstanceId(Guid.NewGuid());
        var layout = DragonAreaGenerator.Build(Store, data, mode, map, "KEEPER", id, new Random(7));
        var bytes = layout.Write();
        var read = DragonAreaLayout.Read(bytes);
        var drakeSlots = master!.Slots.Select((s, i) => (s, i)).Where(x => x.s.Tags.Contains("DRAKE_ISLAND")).Select(x => $"instanceslot_{x.i + 1:00}").ToHashSet();
        Check(bytes[0] == 4 && read.Id == id && read.Name == "DRAGON-AREA-1-TO-1-ORANGE_DRA_DR_NON_T6_KPR_DRL" && read.Mode == mode.Name && read.Map == map.Id && read.Variant == "default"
              && read.MiniMap.Preset == "Dragon" && read.Templates.Count == layout.Templates.Count && layout.Templates.Count == 29
              && read.Templates[0] is { Template: "DRAGON_001", Layers: ["Layer_01"], Rotation: 0 }
              && layout.Templates.Where(t => drakeSlots.Contains(t.Slot)).All(t => t.Template == "M_DR_Drake_01")
              && layout.Templates.Where(t => !drakeSlots.Contains(t.Slot)).All(t => t.Template != "M_DR_Drake_01") && layout.Templates.Skip(1).All(t => t.Layers.Count == 1)
              && ClusterNameInfo.Parse(read.Name) is { Type: 25, Biome: 9, Tier: 6, Continent: 5 },
              $"a88: type 4, the name, tail {{k mode, l map, n 0, m default}}, preset Dragon, DRAGON_001 + 28 slot templates (drake islands on DRAKE_ISLAND), one rolled layer each ({bytes.Length} bytes)");
        var template = Store.FromLayout(layout);
        var exits = template.Critical.Where(m => m.Type == "dragonareareexit").ToList();
        Check(template.Problems.Count == 0 && exits.Count == 9 && template.MobSpawnPoints.Count > 30,
              $"its cluster: {exits.Count} dragonareareexit members (Layer_01's and the unlayered), {template.MobSpawnPoints.Count} mob spawn points, problems: {string.Join("; ", template.Problems.Take(2))}");
    }

    private static void Enter()
    {
        _ = Store.Get(Cluster); // resolved before the Join: its map gets the cluster at once
        var (world, clock, dir, characters) = MistsTest.NewWorld(new MistsOptions(), 11);
        var hero = MistsTest.Hero(characters, Cluster, Vector2.Zero);
        var (s, peer, _) = MistsTest.Join(world, hero.Id);
        var map = world.Maps.Find(Cluster)!;
        var at = s.Player!.PositionAt(DateTime.UtcNow);
        DragonAreaEntrance entrance;
        lock (world.Sync) entrance = world.DragonAreas.Spawn(map, at + new Vector2(0, 4), 0, "DRAGON_AREA_ORANGE_SMALL").Entrance!;
        List<EventData> Of(FakePeer on, EventCodes code) => [.. on.Events.Where(e => MistsTest.Code(e) == (int)code)];

        lock (world.Sync) s.OnRequest(Request(new UseDragonAreaEntranceRequest { wf = world.GameNow, j5 = entrance.Id }, 2), encrypted: false);
        var used = peer.Responses[^1];
        var channel = Of(peer, EventCodes.UsingDragonAreaEntranceStateUpdate).LastOrDefault();
        MistsTest.Tick(world, clock, 3.1);
        var waiting = Of(peer, EventCodes.UsingDragonAreaEntranceStateUpdate).LastOrDefault();
        Check(used.ReturnCode == 0 && Convert.ToInt32(channel?.Parameters[2]) == 1 && Convert.ToInt64(channel!.Parameters[3]) == entrance.Id
              && Convert.ToInt32(waiting?.Parameters[2]) == 2 && world.DragonAreas.UsingOf(s.Player) is { State: 2 },
              $"549: RC 0, 699 {{2 Channeling, 3 the entrance}}, after the channel 699 WaitingToEnter (got {used.ReturnCode})");

        lock (world.Sync) s.OnRequest(Request(new EnterDragonAreaEntranceRequest { wf = world.GameNow, j2 = entrance.Id }, 3), encrypted: false);
        var entered = peer.Responses[^1];
        world.DragonAreas.Building().Wait(TimeSpan.FromSeconds(120));
        MistsTest.Tick(world, clock, 0.1);
        var push = peer.Responses[^1];
        var instance = world.DragonAreas.Instances.SingleOrDefault();
        Check(entered.ReturnCode == 0 && instance is not null && Convert.ToInt32(push.Parameters[ReservedKeys.OperationCode]) == (int)OperationCodes.ChangeCluster
              && push.Parameters.Keys.Where(k => k < 250).Order().SequenceEqual(new byte[] { 0, 3, 4, 5 }) && push.Parameters[0] is string pid && pid == instance.Id
              && pid.StartsWith(DragonAreaIds.Prefix, StringComparison.Ordinal) && push.Parameters[4] is "DRAGON_AREA_1_TO_1_ORANGE" && Convert.ToInt32(push.Parameters[5]) == 6
              && push.Parameters[3] is byte[] bytes && DragonAreaLayout.Read(bytes).Map == instance.Map.Id && s.Status == SessionStatus.Transferring,
              $"550: RC 0; the push {{0 @DRAGONAREA@<guid>, 3 a88, 4 DRAGON_AREA_1_TO_1_ORANGE, 5 6}} (got {entered.ReturnCode}, keys {string.Join(",", push.Parameters.Keys)})");

        var (s2, peer2, join) = MistsTest.Join(world, hero.Id);
        var inside = s2.Player!.Map!;
        var exits = Of(peer2, EventCodes.NewDragonAreaExit);
        var mobs = inside.Objects.Values.OfType<Mob>().Count();
        Check(join.a8i == instance!.Id && exits.Count >= 1 && exits.All(e => e.Parameters[2] is Cluster) && mobs > 10,
              $"the Join in the instance: {exits.Count} exit(s) in view leading to {Cluster} (695 key 2), {mobs} mobs");

        var exit = inside.Objects.Values.OfType<DragonAreaExit>().Where(x => x.State == DragonAreaExitState.Active).MinBy(x => Vector2.Distance(x.Position, s2.Player.PositionAt(DateTime.UtcNow)))!;
        MistsTest.Move(world, s2, exit.Position);
        lock (world.Sync) s2.OnRequest(Request(new ExitEnterStartRequest { wf = world.GameNow, kq = exit.Id }, 4), encrypted: false);
        var started = !Of(peer2, EventCodes.ExitEnterCancel).Any(); // accepted: no answer, no 254 (237 goes to the observers; the client runs its own bar)
        MistsTest.Tick(world, clock, 2.1);
        var outward = peer2.Responses[^1];
        var (s3, _, home) = MistsTest.Join(world, hero.Id);
        Check(started && outward.Parameters.GetValueOrDefault((byte)0) is Cluster && home.a8i == Cluster && Vector2.Distance(home.a8j, entrance.Position) < 6 && s3.Player!.Character.DungeonReturn is null,
              $"237 at an exit: the channel, then the push {{0 {Cluster}}}, the Join in front of the entrance (got {outward.Parameters.GetValueOrDefault((byte)0)} {home.a8j}, started {started}, entrance {entrance.Position}, status {s3.Status} return {s3.Player?.Character.DungeonReturn})");

        Check(world.DragonAreas.Find(instance.Id) is not null, "the instance stays while empty");
        MistsTest.Tick(world, clock, 301);
        Check(world.DragonAreas.Find(instance.Id) is null && world.Maps.Find(instance.Id) is null, "empty for 300 s: closed, its map gone");
        world.Dispose();
        characters.Dispose();
        try { Directory.Delete(dir, true); } catch (IOException) { }
    }

    private static void Natural()
    {
        var info = Store.World;
        var yellow = info.Clusters.Where(c => c.Type == "OPENPVP_YELLOW" && ClusterNameInfo.Parse(c.File) is { Type: 0, Continent: 1, Tier: 5 or 6 })
            .Select(c => Store.Get(c.Id)).FirstOrDefault(t => t.Resources?.Placements.Any(p => p.Name == ClusterTemplate.TrackingPrefab) == true);
        var treasures = Store.Treasures;
        _ = Store.DragonAreas;
        Check(yellow is not null && treasures.DragonEntranceEventsFor("Royal", "OPENPVP_YELLOW", yellow.Name.Tier ?? 5).Count == 3,
              $"a royal yellow T5/T6 map with tracking spots ({yellow?.Id}), 3 DRAGONAREA_ENTRANCE events there");
        var (world, _, dir, characters) = MistsTest.NewWorld(new MistsOptions(), 5);
        DragonAreaEntrance? drawn = null;
        for (var i = 0; i < 40 && drawn is null; i++)
            drawn = world.DragonAreas.Populate(new Map(yellow!.Id), yellow).OfType<DragonAreaEntrance>().FirstOrDefault();
        var spots = yellow!.Resources!.Placements.Where(p => p.Name == ClusterTemplate.TrackingPrefab).Select(p => p.Position).ToList();
        Check(drawn is not null && drawn.Type.Name.StartsWith("DRAGON_AREA_ORANGE_", StringComparison.Ordinal) && spots.Contains(drawn.Position) && drawn.Origin is not null,
              $"an orange entrance drawn on one of the {spots.Count} tracking spots (got {drawn?.Type.Name})");
        var off = new World(new ServerConfig { JoinByCharacterId = true, DragonAreaNaturalSpawn = false }, characters, Client.Items) { Sync = new object(), Clusters = Store };
        Check(Enumerable.Range(0, 20).All(_ => !off.DragonAreas.Populate(new Map(yellow.Id), yellow).Any()), "DragonAreas:NaturalSpawn false: none");
        off.Dispose();
        world.Dispose();
        characters.Dispose();
        try { Directory.Delete(dir, true); } catch (IOException) { }
    }

    private static void Check(bool cond, string what) => AlbionTests.Check(cond, what);
}
