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
    ];

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
