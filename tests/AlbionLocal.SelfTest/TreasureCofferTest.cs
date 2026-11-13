using System.Numerics;
using AlbionLocal.Photon.Serialization;
using AlbionLocal.Server;
using AlbionLocal.Server.Data;
using AlbionLocal.Server.Entities;
using AlbionLocal.Server.Network;
using AlbionLocal.Server.Protocol;
using AlbionLocal.Server.Systems;
using static AlbionLocal.SelfTest.AlbionTests;

namespace AlbionLocal.SelfTest;

/// <summary>
/// Public issue #19 (analysis/work/coffers/SPEC.md): the open world's contested treasures. The data (treasuredistribution.xml draws, the
/// lootchests.xml protection, circle and size), and on FakePeer sessions with a manual clock in a yellow-zone cluster with coffer tiles: the
/// slots' draws, a coffer LockedByTimer with key 27, refused until its 90 s are over, the PvP circle (308 to both players, Hostile), the
/// opening and the despawn 300 s later, then the slot's next draw.
/// </summary>
public static class TreasureCofferTest
{
    public static IEnumerable<(string, Action)> All =>
    [
        ("treasure coffers: treasuredistribution draws, lootchests protection/circle/size; a yellow-zone coffer's countdown, PvP circle (308, Hostile), opening, despawn and next draw", Run),
        ("treasure map markers: the client's treasure list index, LocalTreasuresUpdate 284 at Join and on a spawn/despawn, the world map's ClusterInfoUpdate 51-56 (issue #23)", Markers),
    ];

    /// <summary>The 284 entries of <paramref name="e"/>: (Guid, index, id, subtype).</summary>
    private static List<(Guid Guid, int Index, long Id, string Subtype)> Entries(EventData e)
    {
        var guids = Guids(e.Parameters[1]);
        var index = (int[])e.Parameters[2];
        var ids = (long[])e.Parameters[4];
        var kinds = (string[])e.Parameters[8];
        return [.. guids.Select((g, i) => (g, index[i], ids[i], kinds[i]))];
    }

    /// <summary>A Guid[] as sent (AlbionSerializer: 16 bytes each, concatenated).</summary>
    private static Guid[] Guids(object? o) => o is byte[] b ? [.. b.Chunk(16).Select(c => new Guid(c))] : [];

    private static void Markers()
    {
        var store = Day15.Value;
        var data = store.Treasures;
        int Ix(string name) => data.ClientIndex("TreasureLootchest", name);
        // bfz.jo of client 345319: NONE, HUNTSPOTGENERIC, then names in document order (106-108 and 152 in this data)
        Check(Ix("DEFAULT_CHEST_SOLO") > 1 && Ix("DEFAULT_CHEST_VETERAN") == Ix("DEFAULT_CHEST_SOLO") + 1 && Ix("DEFAULT_CHEST_ELITE") == Ix("DEFAULT_CHEST_SOLO") + 2
              && Ix("COFFER_SOLO") > 1 && Ix("NO_SUCH_TREASURE") == 0,
              $"client treasure list: DEFAULT_CHEST_SOLO/VETERAN/ELITE {Ix("DEFAULT_CHEST_SOLO")}/{Ix("DEFAULT_CHEST_VETERAN")}/{Ix("DEFAULT_CHEST_ELITE")}, COFFER_SOLO {Ix("COFFER_SOLO")}, unknown 0");

        var found = store.World.Clusters.Where(c => c.Type == "OPENPVP_YELLOW" && ClusterNameInfo.Parse(c.File) is { Type: 0, Continent: 1, Tier: 5 })
            .OrderBy(c => c.Id, StringComparer.Ordinal).Select(c => c.Id).First();
        var spot = store.Get(found).MobSpawnPoints[0].Position;
        var dir = NewDir();
        var characters = Repository(dir);
        var clock = new ManualClock();
        var anchor = DateTime.UtcNow.Ticks;
        var world = new World(new ServerConfig { JoinByCharacterId = true, TreasureRespawnMinutes = 600 }, characters, Client.Items, Client.GameData, Client.Spells)
        {
            Sync = new object(), Clock = clock, Clusters = store, MapSeed = 23, GameTicks = () => anchor + clock.Now.Ticks,
        };
        world.MobAi = false;
        void Tick(double seconds)
        {
            clock.Advance(TimeSpan.FromSeconds(seconds));
            lock (world.Sync) world.Update();
        }
        var hero = EventTest.NewHero(characters, spot);
        hero.Cluster = found;
        characters.Save(hero);
        var (a, peerA) = EventTest.Join(world, hero.Id);
        var map = world.Maps.Find(found)!;
        Tick(0.1);

        // A GM veteran chest 300 m away: the player in the cluster gets the new list at once (no NewLootChest that far).
        var mark = peerA.Events.Count;
        LootChest chest;
        lock (world.Sync) chest = world.TreasureCoffers.SpawnAt(map, "veteran", spot + new Vector2(300, 0), 0);
        Tick(0.1);
        var spawned = peerA.Events.Skip(mark).LastOrDefault(e => Code(e) == (int)EventCodes.LocalTreasuresUpdate);
        var entry = spawned is null ? default : Entries(spawned).FirstOrDefault(x => x.Id == chest.Id);
        Check(entry.Index == Ix("DEFAULT_CHEST_VETERAN") && entry.Index > 1 && entry.Subtype == "CHEST" && chest.TreasureGuid == entry.Guid,
              $"a spawn: 284 to the player 300 m off with {{2 DEFAULT_CHEST_VETERAN's index, 8 CHEST, 1 the chest's Guid}} (got {entry})");

        // Keys 6 and 7 are the two stamps the minimap countdown reads (TreasureSpawnMapLabel.UpdateTime: key 6 for a lootchest, key 7 for
        // the mob types), and MapLabelTimer hides the text as soon as now >= the stamp. They used to be GameNow and 0, i.e. expired.
        var k6 = spawned!.Parameters[6] as long[];
        var k7 = spawned.Parameters[7] as long[];
        var openAt = chest.LockState == TreasureCoffers.LockedByTimer
            ? chest.StateTicks + TimeSpan.FromSeconds(chest.Template.ProtectionSeconds).Ticks
            : chest.StateTicks;
        Check(k6 is { Length: 1 } && k6[0] == openAt && k6[0] != world.GameNow && k7 is { Length: 1 } && k7[0] >= 0,
              $"284 keys 6/7: the openable-at stamp {openAt} and the despawn {k7?[0]}, not now {world.GameNow} / 0 (got {k6?[0]} / {k7?[0]})");

        // A second player joining far off: 284 at Join, before JoinFinished, with the entry.
        var rival = EventTest.NewHero(characters, spot);
        rival.Cluster = found;
        characters.Save(rival);
        var (b, peerB) = EventTest.Join(world, rival.Id);
        var codes = peerB.Events.Select(Code).ToList();
        var at = codes.LastIndexOf((int)EventCodes.LocalTreasuresUpdate);
        Check(at >= 0 && at < codes.IndexOf((int)EventCodes.JoinFinished) && Entries(peerB.Events[at]).Any(x => x.Id == chest.Id && x.Index > 1),
              "Join: 284 before JoinFinished with the veteran chest");

        // The world map: ClusterInfoUpdate keys 51-56 carry it (worldmaptreasure, range 1) for a viewer in the cluster.
        mark = peerB.Events.Count;
        lock (world.Sync) b.OnRequest(Request(new SubscribeToClusterRequest { agl = [found] }, 80), encrypted: false);
        var info = peerB.Events.Skip(mark).FirstOrDefault(e => Code(e) == (int)EventCodes.ClusterInfoUpdate)?.Parameters;
        Check(info is not null && Guids(info.GetValueOrDefault((byte)51)).SequenceEqual([chest.TreasureGuid!.Value]) && info[52] is int[] wi && wi.SequenceEqual([Ix("DEFAULT_CHEST_VETERAN")]),
              "world map: ClusterInfoUpdate {51 the Guid, 52 the index}");

        // Gone (despawned): the next list is empty.
        mark = peerA.Events.Count;
        lock (world.Sync) Combat.RemoveBag(chest, "test");
        Tick(0.1);
        var gone = peerA.Events.Skip(mark).LastOrDefault(e => Code(e) == (int)EventCodes.LocalTreasuresUpdate);
        Check(gone is not null && Entries(gone).Count == 0, "a despawn: 284 with an empty list");
        world.Dispose();
        characters.Dispose();
        try { Directory.Delete(dir, true); } catch (IOException) { }
    }

    private static void Check(bool cond, string what) => AlbionTests.Check(cond, what);

    private static int Code(EventData e) => Convert.ToInt32(e.Parameters.GetValueOrDefault(ReservedKeys.EventCode));

    private static readonly Lazy<ClusterStore> Day15 = new(() => ResourceNodeTest.Day(Capture002835Key57));

    private static void Run()
    {
        var store = Day15.Value;
        var data = store.Treasures;
        var chests = store.LootChests;
        var coffer = data.DrawFor("COFFER", "Royal", "OPENPVP_YELLOW", 5);
        var outlands = data.DrawFor("CHEST", "Outlands", "OPENPVP_BLACK_1", 6);
        Check(coffer is { Slots: 5, MinSeconds: 220, MaxSeconds: 650, CooldownSeconds: 300, Entries: [("COFFER_SOLO", 1)] } && Math.Abs(coffer.Chance - 0.205094966) < 1e-9
              && data.Lootchests["COFFER_SOLO"] is { Spawnpoint: "TREASURE_COFFER_SOLO", MaxLifetimeSeconds: 86400 }
              && outlands is { Slots: 1, Entries: [("DEFAULT_CHEST_SOLO", 755), ("DEFAULT_CHEST_VETERAN", 225), ("DEFAULT_CHEST_ELITE", 20)] }
              && data.DrawFor("COFFER", "Royal", "OPENPVP_YELLOW", 4) is null && data.DrawFor("COFFER", "Royal", "SAFEAREA", 5) is null,
              "treasuredistribution: Royal yellow T5 COFFER = 5 slots, 220-650 s, cooldown 300 s, chance 0.205, COFFER_SOLO -> TREASURE_COFFER_SOLO (24 h); "
              + "Outlands T6 CHEST = solo 755 / veteran 225 / elite 20; no coffer at T4 or in a blue zone");
        Check(chests.Chests["TREASURE_COFFER_SOLO"] is { ProtectionSeconds: 90, PvpZoneRadius: 30, AlwaysChannel: true, ChestType: "treasurecoffer", Spell: "OPEN_TREASURECHEST" }
              && chests.Chests["TREASURE_ELITE_RARE"] is { ProtectionSeconds: 2400, PvpZoneRadius: 40, ChestType: "treasurechestlarge" }
              && chests.Spawnpoints["TREASURE_COFFER_SOLO"] is { DespawnSeconds: 300, MinimapDisplay: true }
              && TreasureCoffers.SizeOf("treasurechestmedium") == "veteran" && TreasureCoffers.SizeOf("factionchestsmall") is null,
              "lootchests: TREASURE_COFFER_SOLO 90 s, 30 m, channelled, treasurecoffer; the elite chests 2400 s, 40 m; despawn 300 s; the sizes");

        // A Royal yellow T5 cluster whose rolled mob camps hold coffer tiles (the dynamic templates of 2026-10-15).
        string? found = null;
        foreach (var c in store.World.Clusters.Where(c => c.Type == "OPENPVP_YELLOW" && ClusterNameInfo.Parse(c.File) is { Type: 0, Continent: 1, Tier: 5 }).OrderBy(c => c.Id, StringComparer.Ordinal))
        {
            if (store.Get(c.Id).MistsSpots.Count(s => s.Kind == "treasure:COFFER") < 2) continue;
            found = c.Id;
            break;
        }
        Check(found is not null, $"a Royal OPENPVP_YELLOW T5 cluster with coffer tiles ({found})");
        if (found is null) return;
        var spots = store.Get(found).MistsSpots.Where(s => s.Kind == "treasure:COFFER").ToList();

        var dir = NewDir();
        var characters = Repository(dir);
        var clock = new ManualClock();
        var anchor = DateTime.UtcNow.Ticks;
        var world = new World(new ServerConfig { JoinByCharacterId = true }, characters, Client.Items, Client.GameData, Client.Spells)
        {
            Sync = new object(), Clock = clock, Clusters = store, MapSeed = 19, GameTicks = () => anchor + clock.Now.Ticks,
        };
        world.Rates = world.Rates with { ChestRespawn = 100 };
        world.MobAi = false;
        void Tick(double seconds)
        {
            clock.Advance(TimeSpan.FromSeconds(seconds));
            lock (world.Sync) world.Update();
        }
        var hero = EventTest.NewHero(characters, spots[0].Position);
        var rival = EventTest.NewHero(characters, spots[0].Position + new Vector2(60, 0));
        foreach (var c in new[] { hero, rival })
        {
            c.Cluster = found;
            characters.Save(c);
        }
        var (a, peerA) = EventTest.Join(world, hero.Id);
        var (b, peerB) = EventTest.Join(world, rival.Id);
        var map = world.Maps.Find(found)!;
        var slots = world.TreasureCoffers.SlotsOf(map);
        Check(slots.Count(s => s.Subtype == "COFFER") == 5 && world.TreasureCoffers.SpotsOf(map).Count(s => s.Subtype == "COFFER") == spots.Count,
              $"{found}: 5 coffer slots over its {spots.Count} coffer tiles");
        for (var i = 0; i < 400 && !slots.Any(s => s.Chest is not null); i++) Tick(1); // 2.2-6.5 s per draw at Rates:ChestRespawn 100, chance 0.205
        var chest = slots.Select(s => s.Chest).FirstOrDefault(c => c is not null);
        Check(chest is { Template.Name: "TREASURE_COFFER_SOLO", LockState: TreasureCoffers.LockedByTimer, PvpZone: true } && spots.Any(s => s.Position == chest.Position)
              && slots.Count(s => s.Chest is not null) <= 5,
              $"a draw puts TREASURE_COFFER_SOLO on a coffer tile, LockedByTimer, its circle up (got {chest})");
        if (chest is null) return;

        // Both players at the coffer: its create (key 5 = 1, key 7 = the spawn, key 27 true), locked until its 90 s are over.
        foreach (var (s, id) in new[] { (a, 70), (b, 71) })
        {
            var to = chest.Position + new Vector2(id == 70 ? 3 : -3, 0);
            lock (world.Sync) s.OnRequest(Request(new MoveRequest { wf = DateTime.UtcNow.Ticks, v6 = [to.X, to.Y], v8 = [to.X, to.Y], v9 = 0f }, id), encrypted: false);
        }
        Tick(0.1);
        var created = peerA.Events.LastOrDefault(e => Code(e) == (int)EventCodes.NewLootChest && Convert.ToInt64(e.Parameters[0]) == chest.Id)?.Parameters;
        var count = peerA.Responses.Count;
        lock (world.Sync) a.OnRequest(Request(new UseLootChestRequest { wf = world.GameNow, aif = chest.Id }, 72), encrypted: false);
        Check(created is not null && created[5] is (byte)TreasureCoffers.LockedByTimer && Convert.ToInt64(created[7]) == chest.SpawnTicks && created[27] is true
              && created[3] is "TREASURE_COFFER_SOLO" && created[18] is "TREASURE_COFFER_SOLO" && created[4] is string tile && tile != chest.Template.Prefab
              && tile == chest.Template.TileFor(store.Get(found).Name.Biome, store.Get(found).Name.TemplateFolder) && peerA.Responses.Count > count && peerA.Responses[^1].ReturnCode == WorldSession.ChestLocked,
              "NewLootChest {3 TREASURE_COFFER_SOLO, 4 the cluster biome's tile, 5 LockedByTimer, 7 its spawn, 18 the spawnpoint, 27 the circle}; UseLootChest in the countdown: rc 10126"
              + $" (got {(created is null ? $"no create, the player {Vector2.Distance(a.Player!.PositionAt(DateTime.UtcNow), chest.Position):0} m off" : string.Join(" ", created.Select(kv => $"{kv.Key}={kv.Value}")))}; rc {peerA.Responses[^1].ReturnCode})");

        // The circle: both players unrestricted (308 {0 player, 1 1} to each about both), Hostile although both are unflagged in a yellow zone.
        bool Told(FakePeer peer, Player about, byte status) => peer.Events.Any(e => Code(e) == (int)EventCodes.UnrestrictedPvpZoneUpdate
            && Convert.ToInt64(e.Parameters[0]) == about.Id && Convert.ToByte(e.Parameters.GetValueOrDefault((byte)1) ?? (byte)0) == status);
        var (pa, pb) = (a.Player!, b.Player!);
        Check(Told(peerA, pa, 1) && Told(peerA, pb, 1) && Told(peerB, pa, 1) && Told(peerB, pb, 1)
              && world.TreasureCoffers.IsInside(pa) && Pvp.Of(pa, pb) == Relation.Hostile,
              $"in the 30 m circle: 308 status 1 about both to both, Hostile ({Pvp.Of(pa, pb)})");
        var away = chest.Position + new Vector2(45, 0);
        lock (world.Sync) b.OnRequest(Request(new MoveRequest { wf = DateTime.UtcNow.Ticks, v6 = [away.X, away.Y], v8 = [away.X, away.Y], v9 = 0f }, 73), encrypted: false);
        Tick(0.1);
        Check(Told(peerB, pb, 0) && Told(peerA, pb, 0) && !world.TreasureCoffers.IsInside(pb) && Pvp.Of(pa, pb) != Relation.Hostile,
              "out of the circle (45 m): 308 status 0 to both, no longer Hostile");

        var mark = peerA.Events.Count;
        Tick(90);
        var unlocked = peerA.Events.Skip(mark).Any(e => Code(e) == (int)EventCodes.UpdateLootChest && Convert.ToInt64(e.Parameters[0]) == chest.Id && e.Parameters[1] is (byte)6);
        count = peerA.Responses.Count;
        lock (world.Sync) a.OnRequest(Request(new UseLootChestRequest { wf = world.GameNow, aif = chest.Id }, 74), encrypted: false);
        Check(unlocked && chest.Rolled && chest.LockState is 7 or 8 && peerA.Responses.Count > count && peerA.Responses[^1].ReturnCode == 0,
              "after 90 s: UpdateLootChest {1 Unlocked}; opened, its loot rolled");
        var slot = slots.First(s => s.Chest == chest);
        Tick(299);
        var stays = map.Objects.ContainsKey(chest.Id);
        Tick(2);
        Check(stays && !map.Objects.ContainsKey(chest.Id) && slot.Chest != chest && !world.TreasureCoffers.SpotsOf(map).Any(s => s.Chest == chest),
              "the opened coffer goes 300 s after its opening and frees its slot and tile");
        for (var i = 0; i < 400 && slot.Chest is null; i++) Tick(1);
        Check(slot.Chest is { } next && next != chest, "the freed slot draws its next coffer");
        world.Dispose();
        characters.Dispose();
        try { Directory.Delete(dir, true); } catch (IOException) { }
    }
}
