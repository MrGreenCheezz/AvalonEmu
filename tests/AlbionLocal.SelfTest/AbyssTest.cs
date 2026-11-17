using System.Numerics;
using AlbionLocal.Photon;
using AlbionLocal.Photon.Serialization;
using AlbionLocal.Server;
using AlbionLocal.Server.Data;
using AlbionLocal.Server.Entities;
using AlbionLocal.Server.Network;
using AlbionLocal.Server.Persistence;
using AlbionLocal.Server.Protocol;
using AlbionLocal.Server.Systems;
using static AlbionLocal.SelfTest.AlbionTests;

namespace AlbionLocal.SelfTest;

/// <summary>
/// The Abyssal Depths (analysis/work/stageR/abyss/SPEC.md): helldungeons.xml and the a9c layout of a floor and its cluster; a Hell Den's entrance (638)
/// and its Open/Closed cycle (639); op 532 refused and accepted, the push into floor 1 (622, the exits), a downward exit into floor 2 and an upward
/// exit back to the Hell Den.
/// </summary>
public static class AbyssTest
{
    public static IEnumerable<(string, Action)> All =>
    [
        ("abyss: helldungeons.xml, the a9c layout of floor 1 (tail, name, client reader) and its cluster (spawns, exits, shrines, mobs)", Layout),
        ("abyss: a Hell Den's entrance (638 keys) and its Open/Closed cycle (639)", Entrance),
        ("abyss: op 532 refused (closed, mode) and accepted, the push into floor 1 (622, exits), down to floor 2, up to the Hell Den", Enter),
    ];

    private const string Den = "0000-HellDen";

    private static ClusterStore Store => Client.Clusters!;

    private static (World World, ManualClock Clock, string Dir, SqliteCharacterRepository Characters) NewWorld(int seed)
    {
        var dir = NewDir();
        var characters = Repository(dir);
        var clock = new ManualClock();
        var anchor = DateTime.UtcNow.Ticks;
        var world = new World(new ServerConfig { JoinByCharacterId = true, AbyssalDepthsCheckItemPower = false }, characters, Client.Items, Client.GameData, Client.Spells)
        {
            Sync = new object(), Clock = clock, Clusters = Store, MapSeed = seed, GameTicks = () => anchor + clock.Now.Ticks,
        };
        world.MobAi = false;
        return (world, clock, dir, characters);
    }

    private static void Layout()
    {
        var data = Store.HellDungeons;
        var mode = data.ModeById(0);
        Check(data.Modes.Count == 2 && mode is { Name: "HELL_DUNGEON_DUO_ORANGE", MaxTeam: 2, ItemPower: 900 } && data.ModeById(1)?.Name == "HELL_DUNGEON_TRIO_ORANGE"
              && data.MapSets["MAPSET_DUO_ORANGE_DUNGEON"].Select(m => m.Level).SequenceEqual([0, 1, 2]) && data.StaticOpenSeconds == 300 && data.StaticCloseSeconds == 300
              && data.Masters.TryGetValue("HELLDNG_MASTER_01_LVL1", out var master) && master.Slots.Count == 6,
              "helldungeons.xml: the duo (id 0) and trio (id 1) modes, 3 floors, the 300/300 s entrance cycle; the LVL1 master with 6 slots");
        var map = data.MapAt(mode!, 0, new Random(1))!;
        var id = HellDungeonIds.InstanceId(Guid.NewGuid());
        var layout = HellDungeonGenerator.Build(Store, data, mode!, map, "HERETIC", id, new Random(3));
        var bytes = layout.Write();
        var read = HellDungeonLayout.Read(bytes);
        Check(bytes[0] == 3 && read.Id == id && read.Name == "DUO-ORANGE-HELL-DUNGEON-01_HED_HL_DED_T6_HER_ROY" && read.Mode == mode!.Name && read.Map == map.Id && read.Level == 0
              && read.Variant == "default" && read.Offset == 0 && read.MiniMap.Preset == "HELL_DUNGEON_LEVEL_ALL" && layout.Templates.Count == 7
              && read.Templates[0].Template == "HELLDNG_MASTER_01_LVL1" && layout.Templates.Skip(1).All(t => t.Folder == "DEAD" && t.Layers.Count == 1)
              && ClusterNameInfo.Parse(read.Name) is { Type: 24, Biome: 2, SubBiome: 2, Tier: 6, Continent: 1 },
              $"a9c: type 3, the name, tail {{o mode, p map, r 0, q default, s 0}}, preset HELL_DUNGEON_LEVEL_ALL, master + 6 slot templates with their rolled layer ({bytes.Length} bytes)");
        var template = Store.FromLayout(layout);
        int Count(string kind) => template.MistsSpots.Count(s => s.Kind == kind);
        Check(template.Problems.Count == 0 && Count("HellDungeonPlayerSpawnpoint") >= 2 && Count("helldungeonupwardexit") >= 2 && Count("helldungeondownwardexit") >= 5
              && Count("resurrectionshrine") >= 2 && template.MobSpawnPoints.Count > 50,
              $"its cluster: {Count("HellDungeonPlayerSpawnpoint")} spawn points, {Count("helldungeonupwardexit")} up, {Count("helldungeondownwardexit")} down, "
              + $"{Count("resurrectionshrine")} shrines, {template.MobSpawnPoints.Count} mob spawn points; problems: {string.Join("; ", template.Problems.Take(2))}");
    }

    private static Vector2 EntranceSpot()
    {
        var den = Store.Get(Den);
        var member = den.Critical.Single(m => m.Type == "helldungeonstaticentrance");
        return member.Position;
    }

    private static void Entrance()
    {
        var spot = EntranceSpot();
        var (world, clock, dir, characters) = NewWorld(5);
        var hero = MistsTest.Hero(characters, Den, spot + new Vector2(0, 3));
        var (s, peer, _) = MistsTest.Join(world, hero.Id);
        var created = peer.Events.FirstOrDefault(e => MistsTest.Code(e) == (int)EventCodes.NewHellDungeonStaticEntrance);
        var entrance = world.Abyss.Entrances.SingleOrDefault();
        Check(created is not null && entrance is { State: AbyssEntranceState.Open } && Convert.ToInt64(created.Parameters[0]) == entrance.Id
              && created.Parameters[1] is byte[] { Length: 16 } g && g.Any(x => x != 0) && created.Parameters[2] is float[] && !created.Parameters.ContainsKey(4)
              && Convert.ToInt64(created.Parameters[5]) > world.GameNow,
              $"the Join in {Den}: 638 {{0 id, 1 the member's Guid, 2 position, 4 Open (left out), 5 the next change}} (got {string.Join(", ", created?.Parameters.Select(kv => $"{kv.Key}={kv.Value?.GetType().Name}:{kv.Value}") ?? [])}; {entrance?.State})");
        MistsTest.Tick(world, clock, 301);
        var update = peer.Events.LastOrDefault(e => MistsTest.Code(e) == (int)EventCodes.UpdateHellDungeonStaticEntranceState);
        Check(entrance!.State == AbyssEntranceState.Closed && Convert.ToInt32(update?.Parameters[1]) == 1 && Convert.ToInt64(update!.Parameters[2]) == entrance.NextChange,
              "after 300 s: Closed, 639 {1 Closed, 2 the next change}");
        MistsTest.Tick(world, clock, 300);
        Check(entrance.State == AbyssEntranceState.Open, "300 s later: Open again");
        world.Dispose();
        characters.Dispose();
        try { Directory.Delete(dir, true); } catch (IOException) { }
    }

    private static void Enter()
    {
        var spot = EntranceSpot();
        var (world, clock, dir, characters) = NewWorld(7);
        var hero = MistsTest.Hero(characters, Den, spot + new Vector2(0, 3));
        var (s, peer, _) = MistsTest.Join(world, hero.Id);
        var entrance = world.Abyss.Entrances.Single();
        List<EventData> Of(FakePeer on, EventCodes code) => [.. on.Events.Where(e => MistsTest.Code(e) == (int)code)];

        lock (world.Sync) world.Abyss.Force(entrance, AbyssEntranceState.Closed, 60);
        lock (world.Sync) s.OnRequest(Request(new HellDungeonUseStaticEntranceRequest { wf = world.GameNow, pz = entrance.Id, p0 = 0 }, 2), encrypted: false);
        var closed = peer.Responses[^1];
        lock (world.Sync) world.Abyss.Force(entrance, AbyssEntranceState.Open, 600);
        lock (world.Sync) s.OnRequest(Request(new HellDungeonUseStaticEntranceRequest { wf = world.GameNow, pz = entrance.Id, p0 = 7 }, 3), encrypted: false);
        var badMode = peer.Responses[^1];
        lock (world.Sync) s.OnRequest(Request(new HellDungeonUseStaticEntranceRequest { wf = world.GameNow, pz = entrance.Id, p0 = 0 }, 4), encrypted: false);
        var used = peer.Responses[^1];
        Check(closed.ReturnCode == AbyssalDepths.EntranceClosed && Convert.ToInt64(closed.Parameters[1]) > 0 && badMode.ReturnCode == AbyssalDepths.ModeNotAllowed
              && used.ReturnCode == 0 && world.Abyss.Waiting(s.Player!),
              $"532: closed → 21282 with key 1, mode 7 → 21286, then RC 0 and the player waits (got {closed.ReturnCode}, {badMode.ReturnCode}, {used.ReturnCode})");

        MistsTest.Tick(world, clock, 3.1);
        world.Abyss.Building().Wait(TimeSpan.FromSeconds(120));
        MistsTest.Tick(world, clock, 0.1);
        var push = peer.Responses[^1];
        var floor = world.Abyss.Instances.SingleOrDefault();
        Check(floor is { Level: 0 } && Convert.ToInt32(push.Parameters[ReservedKeys.OperationCode]) == (int)OperationCodes.ChangeCluster
              && push.Parameters.Keys.Where(k => k < 250).Order().SequenceEqual(new byte[] { 0, 3, 4, 5 }) && push.Parameters[0] is string pid && pid == floor.Id
              && pid.StartsWith(HellDungeonIds.Prefix, StringComparison.Ordinal) && push.Parameters[4] is "HELL_DUNGEON_DUO_ORANGE" && Convert.ToInt32(push.Parameters[5]) == 6
              && push.Parameters[3] is byte[] bytes && HellDungeonLayout.Read(bytes).Level == 0 && s.Status == SessionStatus.Transferring,
              $"after the delay: the push {{0 @HELLDUNGEON@<guid>, 3 a9c, 4 HELL_DUNGEON_DUO_ORANGE, 5 6}} (got keys {string.Join(",", push.Parameters.Keys)})");

        var (s2, peer2, join) = MistsTest.Join(world, hero.Id);
        var inside = s2.Player!.Map!;
        var info = Of(peer2, EventCodes.HellDungeonsPlayerJoinedInfo).SingleOrDefault();
        var ups = Of(peer2, EventCodes.NewHellDungeonUpwardExit);
        var downs = inside.Objects.Values.OfType<AbyssExit>().Where(e => e.Downward).ToList();
        var mobs = inside.Objects.Values.OfType<Mob>().Count();
        Check(join.a8i == floor!.Id && info is not null && Convert.ToInt64(info.Parameters[0]) > Convert.ToInt64(info.Parameters[1]) && Convert.ToInt64(info.Parameters[1]) > world.GameNow
              && inside.Objects.Values.OfType<AbyssExit>().Count(e => e.Upward) >= 2 && downs.Count(d => d.State == 0) is >= 5 and <= 8 && mobs > 20
              && s2.Player.Character.DungeonReturn?.Cluster == Den,
              $"the Join in floor 1: 622 {{0 end, 1 start of the shutdown}}, {ups.Count} upward exit(s) in view, {downs.Count(d => d.State == 0)}/{downs.Count} downward open, {mobs} mobs, the way back {Den}");

        var down = downs.First(d => d.State == 0);
        MistsTest.Move(world, s2, down.Position);
        clock.Advance(TimeSpan.FromSeconds(30)); // past the exit cooldown
        var before = peer2.Responses.Count;
        lock (world.Sync) s2.OnRequest(Request(new ChangeClusterRequest { ee = down.Id }, 5), encrypted: false);
        world.Abyss.Building().Wait(TimeSpan.FromSeconds(120));
        for (var i = 0; i < 200 && !peer2.Responses.Skip(before).Any(r => Convert.ToInt32(r.Parameters.GetValueOrDefault(ReservedKeys.OperationCode)) == (int)OperationCodes.ChangeCluster); i++)
        {
            Thread.Sleep(10);
            MistsTest.Tick(world, clock, 0.01);
        }
        var deeper = peer2.Responses.Skip(before).LastOrDefault(r => Convert.ToInt32(r.Parameters.GetValueOrDefault(ReservedKeys.OperationCode)) == (int)OperationCodes.ChangeCluster) ?? peer2.Responses[^1];
        var floor2 = world.Abyss.Instances.SingleOrDefault(i => i.Level == 1);
        Check(floor2 is not null && deeper.ReturnCode == 0 && deeper.Parameters[0] is string id2 && id2 == floor2.Id && deeper.Parameters[3] is byte[] b2 && HellDungeonLayout.Read(b2).Level == 1
              && floor2.Teams.ContainsKey(hero.Id.ToString()),
              $"a downward exit: built floor 2 ({floor2?.Map.Id}) and the answer {{0 its id, 3 a9c of level 1}} (got RC {deeper.ReturnCode}, {deeper.Parameters.GetValueOrDefault((byte)0)})");

        var (s3, _, join3) = MistsTest.Join(world, hero.Id);
        var up = s3.Player!.Map!.Objects.Values.OfType<AbyssExit>().Where(e => e.Upward).MinBy(e => Vector2.Distance(e.Position, s3.Player.PositionAt(DateTime.UtcNow)))!;
        MistsTest.Move(world, s3, up.Position);
        clock.Advance(TimeSpan.FromSeconds(30));
        lock (world.Sync) s3.OnRequest(Request(new ChangeClusterRequest { ee = up.Id }, 6), encrypted: false);
        var (s4, _, home) = MistsTest.Join(world, hero.Id);
        Check(join3.a8i == floor2!.Id && home.a8i == Den && Vector2.Distance(home.a8j, spot) < 8 && s4.Player!.Character.DungeonReturn is null,
              $"an upward exit of floor 2: back in {Den} where the entrance was used (got {home.a8i} {home.a8j})");

        MistsTest.Tick(world, clock, 1100);
        Check(world.Abyss.Find(floor.Id) is null && world.Abyss.Find(floor2.Id) is null, "the floors close at their shutdown time (1020 s) or empty");
        world.Dispose();
        characters.Dispose();
        try { Directory.Delete(dir, true); } catch (IOException) { }
    }

    private static void Check(bool cond, string what) => AlbionTests.Check(cond, what);
}
