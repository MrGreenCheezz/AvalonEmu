using System.Numerics;
using AlbionLocal.Photon.Serialization;
using AlbionLocal.Server;
using AlbionLocal.Server.Content;
using AlbionLocal.Server.Data;
using AlbionLocal.Server.Entities;
using AlbionLocal.Server.Game;
using AlbionLocal.Server.Network;
using AlbionLocal.Server.Protocol;
using AlbionLocal.Server.Systems;
using static AlbionLocal.SelfTest.AlbionTests;

namespace AlbionLocal.SelfTest;

/// <summary>
/// Personal islands, phase 1 (analysis/work/stageH/islands/SPEC.md §1.7, "SPEC" here): the island types and templates from the client data;
/// then, on FakePeer sessions in Lymhurst, the free starter island, GetIslandInfos, the journey onto the island and its Join, the way back,
/// a login on the island and the island store.
/// </summary>
public static class IslandTest
{
    public static IEnumerable<(string, Action)> All =>
    [
        ("islands: 13 types (2 Lymhurst personal, 9 guild), level templates, FOREST 0001c/0001d: travel point, management, bank 81 = 1001's, merchant 347, arrival (180, 56) / (179, 57)", Data),
        ("islands: starter island, GetIslandInfos rows (keys 0-8, 10-22, no 9), BuyJourney onto it (push {0, 1, 2}), Join 8/9/81/82 + NewIslandManagement and preset 3, back to 1000, login on it, gone island, store", Live),
        ("islands phase 2-3: Join 91, buy 20,000 / upgrade 500,000 (8046 for 130 s), flags, showroom and back; starter farm + herb garden, plant 665 (slot, keys, FOI), water, harvest at 79200 s not 79199, destroy, store, GrowthSpeed 4", Farming),
        ("islands: animals: a T3 horse baby in a pasture grows only while fed (10 wheat = 480 nutrition = 158400 s), collected as T3_FARM_HORSE_GROWN", Animals),
        ("islands phase 5: plots 1 (level 0) / 16 (level 5); a player builds a T1_PASTURE (op 68) for 100 silver + 30 wood/rock/planks/blocks, the plot then refused, gives it up (op 64)", Construction),
        ("islands phase 5: a placed animal is picked up again (op 71: the baby back, the object gone); a seed cannot be", Pickup),
        ("islands: labourers: a T2 house (op 68), a bed and a table in it (op 69), a wood labourer from its contract, a full T2 wood journal after settling in (op 257), its loot after 22 h (op 258)", Labourers),
    ];

    private static ClusterStore Store => Client.Clusters!;

    private static void Check(bool cond, string what) => AlbionTests.Check(cond, what);

    private static int Code(EventData e) => Convert.ToInt32(e.Parameters.GetValueOrDefault(ReservedKeys.EventCode));

    private static T Fill<T>(Dictionary<byte, object?> parameters) where T : new()
    {
        var t = new T();
        AlbionSerializer.FillFields(t, parameters);
        return t;
    }

    private static readonly Guid CityBank = new("0e860295-bb5c-4be1-89d0-9b1bbcba0b99");

    private static void Data()
    {
        var data = Store.Islands;
        var lymhurst = data.TypeAt(2);
        Check(data.Types.Count == 13 && lymhurst is { Name: "PLAYER_ISLAND_LYMHURST", City: "1000", Kind: 0, PremiumRequirement: 7, MaxLevel: 5 }
              && data.TypeAt(9) is { Name: "GUILD_ISLAND_LYMHURST", Kind: 1 } && data.PersonalOf("1000") == lymhurst
              && lymhurst.LevelAt(2)?.Cluster == "ISLAND-PLAYER-FOREST-0001c" && lymhurst.LevelAt(0) is { SilverPrice: 1_000_000, FirstSilverPrice: 20_000 },
              $"13 island types; 2 = PLAYER_ISLAND_LYMHURST/1000/kind 0, 9 = GUILD_ISLAND_LYMHURST/kind 1; level 2 = …0001c; level 0 20,000 first (got {data.Types.Count})");
        var bad = data.Types.SelectMany(t => t.Levels.Select(l => (t, l)))
            .Where(x => Store.World.Find(x.l.Cluster)?.Type != (x.t.Kind == 0 ? "PLAYERISLAND" : "GUILDISLAND")).Select(x => x.l.Cluster).ToList();
        Check(bad.Count == 0 && data.Types.All(t => t.Levels.Count == 6), $"every level template is a world.xml PLAYERISLAND / GUILDISLAND cluster, 6 per type (bad: {string.Join(", ", bad.Take(3))})");

        foreach (var (level, arrival) in new[] { (2, new Vector2(180, 56)), (3, new Vector2(179, 57)) })
        {
            var t = Store.Get(lymhurst!.LevelAt(level)!.Cluster);
            var bank = t.Buildings.SingleOrDefault(b => b.Template.Kind == "bankbuilding");
            var merchant = t.Buildings.SingleOrDefault(b => b.Tile == "FARMING_MERCHANT");
            Check(t.Travelpoints.Count == 1 && Islands.ArrivalOf(t) == arrival && t.IslandManagements is [{ Position: { X: 185.5f, Y: 64.5f } }]
                  && bank is { Guid: var g, Template.Id: 81 } && g == CityBank && merchant?.Template.Id == 347 && t.Pvp.Rule == PvpRule.PvpDisabled
                  && t.Info.Attrs.GetValueOrDefault("bankgroup") == $"{CityBank}@1001",
                  $"{t.Id}: one travel point, arrival {arrival}, the management at (185.5, 64.5), bank 81 = the 1001 bank's Guid, merchant 347, no PvP (got {Islands.ArrivalOf(t)})");
        }
        var id = IslandData.InstanceId(CityBank);
        Check(id.Length == 44 && id.StartsWith("@ISLAND@", StringComparison.Ordinal) && IslandData.GuidOf(id) == CityBank && IslandData.GuidOf("1000") is null
              && Islands.CityFlag("1000") == 2,
              "instance id: 44 characters (as captured; SPEC §0.3 says 45) \"@ISLAND@<guid>\", its Guid back; Lymhurst flag 2");
    }

    private static void Live()
    {
        var dir = NewDir();
        var characters = Repository(dir);
        var world = new World(new ServerConfig { JoinByCharacterId = true, IslandsStarterIsland = true }, characters, Client.Items, Client.GameData, Client.Spells)
        {
            Sync = new object(), Clusters = Store, MapSeed = 5,
        };
        world.MobAi = false;
        Check(!new ServerConfig().IslandsStarterIsland && ServerConfig.PveDefaults.IslandsStarterIsland, "Islands:StarterIsland: official false, pve true");
        var city = Store.Get("1000");
        _ = Store.Get("ISLAND-PLAYER-FOREST-0001c"); // resolved: the island's Join fills its map at once
        var apSpawn = city.IslandAccessPoints.Single();
        var hero = StarterKit.Default.NewCharacter("I" + Guid.NewGuid().ToString("N")[..10]);
        (hero.Cluster, hero.PositionX, hero.PositionY) = ("1000", apSpawn.Position.X + 1, apSpawn.Position.Y);
        Check(characters.Create(hero), "character created");
        var (s, peer, _) = Join(world, hero.Id);
        OperationResponse Call(WorldSession session, FakePeer on, AlbionRequest req, int id)
        {
            lock (world.Sync) session.OnRequest(Request(req, id), encrypted: false);
            return on.Responses[^1];
        }

        // The merchant's list (§1.2): the starter island of Lymhurst at level 0, then .island level 2; every row key, no key 9.
        var first = Fill<GetIslandInfosResponse>(Call(s, peer, new GetIslandInfosRequest(), 3).Parameters);
        var island = world.Islands.Of(hero.Id).SingleOrDefault();
        Check(island is { Type: 2, Level: 0, City: "1000" } && first.azq is [var g0] && g0 == island.Id && first.azw is [0],
              $"GetIslandInfos in Lymhurst: the free starter island, type 2 level 0 (got {island})");
        lock (world.Sync) world.Islands.SetLevel(island!, 2);
        var resp = Call(s, peer, new GetIslandInfosRequest(), 4);
        var infos = Fill<GetIslandInfosResponse>(resp.Parameters);
        var ap = $"{apSpawn.Guid:D}@1000";
        var rowKeys = Enumerable.Range(0, 23).Where(k => k != 9).Select(k => (byte)k).ToList();
        Check(rowKeys.All(k => resp.Parameters.ContainsKey(k)) && !resp.Parameters.ContainsKey(9)
              && infos is { azs: [2], azw: [2], az3: [2], azx: [0], az0: [0], az8: [0], a0a: [0f], a0b: [false], a0c: [false] }
              && infos.azr is [var row] && row == ap && ap.Length == 41 && infos.azu is [var owner] && owner == hero.Id
              && infos.azt is [var name] && name == hero.Name && infos.azv is [var lv] && lv == island!.Id,
              $"one row: keys 0-8 and 10-22 of length 1, no 9; type 2, level 2, flag 2, AP \"<guid>@1000\", owner = the character (got {string.Join(",", resp.Parameters.Keys.Order())})");

        // City → island (§1.3): BuyJourney {0 merchant, 1 its travel point@1000, 2 0, 3 island}: RC 0, push {0 id, 1 template, 2 owner}.
        var merchant = s.Player!.Map!.Objects.Values.OfType<IslandAccessPoint>().Single();
        var target = $"{apSpawn.World!.Travelpoint:D}@1000";
        var wrong = Call(s, peer, new BuyJourneyRequest { df = merchant.Id, dg = target, di = Guid.NewGuid() }, 5);
        var before = peer.Responses.Count;
        Call(s, peer, new BuyJourneyRequest { df = merchant.Id, dg = target, di = island!.Id }, 6);
        var answers = peer.Responses.Skip(before).ToList();
        Check(wrong.ReturnCode == 8040 && answers is [{ ReturnCode: 0 } ok, var push] && ok.Parameters.Keys.All(k => k >= 250)
              && push.Parameters.Keys.Where(k => k < 250).Order().SequenceEqual(new byte[] { 0, 1, 2 }) && !push.Parameters.ContainsKey(ReservedKeys.RequestId)
              && push.Parameters[0] is string pid && pid == island.Cluster && push.Parameters[1] is "ISLAND-PLAYER-FOREST-0001c" && push.Parameters[2] is string pn && pn == hero.Name
              && s.Status == SessionStatus.Transferring,
              $"an unknown island 8040; own island: RC 0, then the push {{0: @ISLAND@…, 1: …0001c, 2: owner}} without 255 (got {wrong.ReturnCode}, {answers.Count} answers)");

        // The island's Join (§1.5): key 8 the id at (180, 56), 81 the access point, 82 the island; the management and its preset 3.
        var (s2, peer2, join) = Join(world, hero.Id);
        var map = s2.Player!.Map!;
        var mgmt = peer2.Events.FirstOrDefault(e => Code(e) == (int)EventCodes.NewIslandManagement) is { } me ? Fill<NewIslandManagementEvent>(me.Parameters) : null;
        var access = peer2.Events.Where(e => Code(e) == (int)EventCodes.AccessStatus).Select(e => Fill<AccessStatusEvent>(e.Parameters)).FirstOrDefault(a => a.ddn == mgmt?.c20);
        Check(join.a8i == island.Cluster && join.a8j == new Vector2(180, 56) && join.baj == ap && join.bak == island.Id && join.a94 == "1000"
              && map.Template?.Id == "ISLAND-PLAYER-FOREST-0001c" && mgmt is { c21: { X: 185.5f, Y: 64.5f } } && mgmt.c22 == hero.Id && mgmt.c24 == hero.Name && mgmt.c23 == Guid.Empty
              && access is { bq5: 3, bq9: ["owner", "noaccess", "noaccess"] } && Islands.VaultLocation(map) == "1001",
              $"Join: 8 = the id at (180, 56), 81 = the AP, 82 = the island, 66 = 1000; NewIslandManagement {{1 (185.5, 64.5), 2 owner, 4 name}}, AccessStatus preset 3; the bank opens 1001's vault (got {join.a8i} {join.a8j})");

        // Island → city (§1.4): from the island's travel point to another city 8040; to the merchant's travel point RC 0, push {0: 1000}.
        var tp = map.Objects.Values.OfType<Travelpoint>().Single();
        var elsewhere = Call(s2, peer2, new BuyJourneyRequest { df = tp.Id, dg = $"{Store.Get("0000").Travelpoints.First().Guid:D}@0000" }, 7);
        before = peer2.Responses.Count;
        Call(s2, peer2, new BuyJourneyRequest { df = tp.Id, dg = target }, 8);
        var back = peer2.Responses.Skip(before).ToList();
        var (s3, _, home) = Join(world, hero.Id);
        var cityTp = city.Travelpoints.Single(t => t.Guid == apSpawn.World.Travelpoint).SpawnPosition!.Value;
        Check(elsewhere.ReturnCode == 8040 && back is [{ ReturnCode: 0 }, var homePush] && homePush.Parameters[0] is "1000" && !homePush.Parameters.ContainsKey(1)
              && home.a8i == "1000" && Vector2.Distance(home.a8j, cityTp) < 0.01f && home.a94 == island.Cluster && string.IsNullOrEmpty(home.baj),
              $"back: another city 8040; the island's city RC 0, push {{0: 1000}}, the Join at its travel point {cityTp}, 66 = the island, no 81 (got {elsewhere.ReturnCode}, {home.a8j})");

        // A login on the island (§1.6): SelectCharacter {1: the id, 2: the template}; an island that is gone → the last city.
        lock (world.Sync) Check(world.Islands.Send(s3.Player!, island, "test"), "onto the island again");
        var (s4, _, _) = Join(world, hero.Id);
        lock (world.Sync) s4.OnDisconnected("logout");
        var loginPeer = new FakePeer();
        var login = new LoginSession(loginPeer, world.Config, characters, StarterKit.Default) { World = world };
        lock (world.Sync)
        {
            login.OnRequest(Request(TestLogin(), 1), encrypted: true);
            login.OnRequest(Request(new SelectCharacterRequest { abc = hero.Name }, null), encrypted: true);
        }
        var select = Fill<SelectCharacterResponse>(loginPeer.Responses[^1].Parameters);
        var gone = world.LoadCharacter(hero.Id)!.Clone();
        (gone.Cluster, gone.LastCity) = (IslandData.InstanceId(Guid.NewGuid()), "1000");
        bool returned;
        lock (world.Sync) returned = world.Islands.ReturnIfGone(gone);
        var inBank = world.LoadCharacter(hero.Id)!.Clone(); // a last city that is an interior (1001 the Lymhurst bank) → its city
        (inBank.Cluster, inBank.LastCity) = (IslandData.InstanceId(Guid.NewGuid()), "1001");
        lock (world.Sync) world.Islands.ReturnIfGone(inBank);
        var stored = characters.LoadIslands().SingleOrDefault(i => i.Id == island.Id);
        Check(select.blo == island.Cluster && select.blp == "ISLAND-PLAYER-FOREST-0001c" && returned && gone.Cluster == "1000" && inBank.Cluster == "1000"
              && stored is { Level: 2, Type: 2 } && stored.OwnerName == hero.Name && stored.AccessPoint == ap,
              $"SelectCharacter on the island {{1: id, 2: …0001c}}; a gone island → 1000 (from 1001 too); the store keeps the island at level 2 (got {select.blo}/{select.blp}, {gone.Cluster}, {inBank.Cluster})");
        world.Dispose();
    }

    private static void Farming()
    {
        var dir = NewDir();
        var characters = Repository(dir);
        var clock = new ManualClock();
        var anchor = DateTime.UtcNow.Ticks;
        var config = new ServerConfig { JoinByCharacterId = true, IslandsStarterBuildings = true };
        var world = new World(config, characters, Client.Items, Client.GameData, Client.Spells)
        {
            Sync = new object(), Clusters = Store, MapSeed = 5, Clock = clock, GameTicks = () => anchor + clock.Now.Ticks,
        };
        world.MobAi = false;
        var city = Store.Get("1000");
        _ = Store.Get("ISLAND-PLAYER-FOREST-0001b");
        var showroom = Store.Get("ISLAND-PLAYER-FOREST-0001e-SHOWROOM");
        var apSpawn = city.IslandAccessPoints.Single();
        var seed = Client.Items.Find("T6_FARM_FOXGLOVE_SEED")!;
        Check(seed.Farm is { GrowTime: 79200, LootList: "T6_FOXGLOVE_LOOT", Fame: 100, MaxCycles: 1, FocusCost: 1000 } && world.Islands.ItemBuildingId(seed) == 665
              && Client.Items.Find("T1_FARM_CARROT_SEED")?.Farm is { SeedChance: 0, Bonus: 2 },
              $"T6_FARM_FOXGLOVE_SEED: growtime 79200, T6_FOXGLOVE_LOOT, fame 100, one watering for 1000 focus, building type 665 (got {world.Islands.ItemBuildingId(seed)})");
        var hero = StarterKit.Default.NewCharacter("F" + Guid.NewGuid().ToString("N")[..10]);
        (hero.Cluster, hero.PositionX, hero.PositionY, hero.Silver, hero.CraftingFocus) = ("1000", apSpawn.Position.X + 1, apSpawn.Position.Y, 3_000_000 * 10_000L, 5000);
        hero.Inventory.Add(new InventoryItem(seed.Name, 3));
        Check(characters.Create(hero), "character created");
        var (s, peer, join) = Join(world, hero.Id);
        OperationResponse Call(WorldSession session, FakePeer on, AlbionRequest req, int id)
        {
            lock (world.Sync) session.OnRequest(Request(req, id), encrypted: false);
            return on.Responses[^1];
        }
        void Tick(TimeSpan by)
        {
            clock.Advance(by);
            lock (world.Sync) world.Update();
        }

        // Phase 2 (§2.1-§2.5): key 91; the first island 20,000 (a price above it refused), its upgrade 500,000, 8046 while upgrading.
        var merchant = s.Player!.Map!.Objects.Values.OfType<IslandAccessPoint>().Single();
        var ap = $"{apSpawn.Guid:D}@1000";
        var cheap = Call(s, peer, new BuyMyIslandRequest { dk = merchant.Id, dl = ap, dm = 30_000 * 10_000L }, 3); // above the price + 1 silver
        var bought = Call(s, peer, new BuyMyIslandRequest { dk = merchant.Id, dl = ap, dm = 20_000 * 10_000L }, 4);
        var island = world.Islands.Of(hero.Id).SingleOrDefault();
        var thetford = world.Islands.Data!.PersonalOf("0000")!;
        Check(join.bat == 31 && cheap.ReturnCode == 1 && bought.ReturnCode == 0 && island is { Type: 2, Level: 0 } && Fill<BuyMyIslandResponse>(bought.Parameters).anf == island.Id
              && bought.Parameters[1] is string a && a == ap && bought.Parameters[2] is byte and 0 && s.Player.Character.Silver == 2_980_000 * 10_000L
              && world.Islands.PriceOf(hero.Id, thetford, 0) == 1_000_000,
              $"Join 91 = 31; buying refused at 30,000, at 20,000: {{0 island, 1 AP, 2 byte 0}}, silver -20,000; a second island 1,000,000 (got {cheap.ReturnCode}/{bought.ReturnCode}, {island})");
        var upgrade = Call(s, peer, new UpgradeMyIslandRequest { ah3 = island!.Id, ah4 = 500_000 * 10_000L }, 5);
        var target = $"{apSpawn.World!.Travelpoint:D}@1000";
        var busy = Call(s, peer, new BuyJourneyRequest { df = merchant.Id, dg = target, di = island.Id }, 6);
        var flags = Call(s, peer, new ChangePlayerIslandDataRequest { e0 = island.Id, e1 = true }, 7);
        var infos = Fill<GetIslandInfosResponse>(Call(s, peer, new GetIslandInfosRequest(), 8).Parameters);
        Check(upgrade.ReturnCode == 0 && upgrade.Parameters.Keys.All(k => k >= 250) && island is { Level: 1 } && island.LevelChanged == world.GameNow && busy.ReturnCode == 8046
              && flags.ReturnCode == 0 && infos is { a0b: [true], a0c: [false], azw: [1] } && infos.azx is [var changed] && changed == island.LevelChanged
              && world.Islands.PriceOf(hero.Id, world.Islands.Data.TypeAt(2)!, 2) == 1_125_000 && world.Islands.BuildingsOf(island.Id).Count == 2,
              $"upgrade 0 → 1: bare RC 0, level 1 stamped now (key 7), journeys 8046; favourite kept (key 21); level 2 costs 1,125,000; starter farm + herb garden (got {upgrade.ReturnCode}, {busy.ReturnCode})");

        // The showroom (§2.4): RC 0, push {0: …0001e-SHOWROOM}; from its travel point back to the merchant's travel point.
        var before = peer.Responses.Count;
        Call(s, peer, new TravelIslandShowroomRequest { aha = merchant.Id }, 9);
        var trip = peer.Responses.Skip(before).ToList();
        var (s2, peer2, there) = Join(world, hero.Id);
        var tp = s2.Player!.Map!.Objects.Values.OfType<Travelpoint>().Single();
        before = peer2.Responses.Count;
        Call(s2, peer2, new BuyJourneyRequest { df = tp.Id, dg = target }, 10);
        var back = peer2.Responses.Skip(before).ToList();
        Check(trip is [{ ReturnCode: 0 }, var push] && push.Parameters[0] is "ISLAND-PLAYER-FOREST-0001e-SHOWROOM" && there.a8i == showroom.Id
              && there.a8j == Islands.ArrivalOf(showroom) && back is [{ ReturnCode: 0 }, var home] && home.Parameters[0] is "1000",
              $"showroom: RC 0, push {{0: the showroom}}, Join at its travel point; back to 1000 (got {trip.Count} answers, {there.a8i})");

        // Phase 3: onto the island after the 130 s; its buildings (343 farm, 344 herb garden, AccessStatus preset 0).
        var (s3, peer3, _) = Join(world, hero.Id);
        Tick(TimeSpan.FromSeconds(131));
        lock (world.Sync) s3.Player!.Teleport(apSpawn.Position + new Vector2(1, 0), 0, DateTime.UtcNow); // back at the merchant (the harbour is 60 m off)
        var merchant3 = s3.Player!.Map!.Objects.Values.OfType<IslandAccessPoint>().Single();
        Check(Call(s3, peer3, new BuyJourneyRequest { df = merchant3.Id, dg = target, di = island.Id }, 11).ReturnCode == 0, "the journey after the upgrade time");
        var (s4, peer4, _) = Join(world, hero.Id);
        var me = s4.Player!;
        var map = me.Map!;
        var herb = map.Objects.Values.OfType<PlayerBuilding>().Single(b => b.Template.Name == "T1_HERBGARDEN");
        lock (world.Sync) me.Teleport(herb.Position + new Vector2(5, 0), 0, DateTime.UtcNow);
        var buildings = peer4.Events.Where(e => Code(e) == (int)EventCodes.NewBuilding).Select(e => Fill<NewBuildingEvent>(e.Parameters)).Where(b => b.ItemTypeId == -1 && b.cto == hero.Id).ToList();
        var access = peer4.Events.Where(e => Code(e) == (int)EventCodes.AccessStatus).Select(e => Fill<AccessStatusEvent>(e.Parameters)).FirstOrDefault(x => x.ddn == herb.Id);
        Check(map.Template?.Id == "ISLAND-PLAYER-FOREST-0001b" && buildings.Select(b => b.TypeId).Order().SequenceEqual(new short[] { 343, 344 })
              && buildings.All(b => b.Durability == 120_000_000 && b.Nutrition == 10_000 && b.ctq == hero.Name && b.ct6) && access is { bq5: 0, bq9: ["owner", "noaccess", "noaccess"] },
              $"the island (level 1, …0001b): NewBuilding 343 and 344 {{7 120000000, 9 owner, 11 name, 18 10000, 19 -1, 27}}, AccessStatus preset 0 (got {buildings.Count})");

        // Planting (§3.2): near (151, 135) of the herb garden at (145, 135) → that slot after 1 s: the stack 3 → 2, NewBuilding 665, FOI {0, 5, 12, 13}.
        var stack = me.Inventory.Bag.Items.Single(i => i.Template == seed);
        Call(s4, peer4, new PlaceableObjectPlaceRequest { x7 = stack.Id, x8 = 666, x9 = herb.Position + new Vector2(5.2f, -0.4f) }, 12);
        Call(s4, peer4, new PlaceableObjectPlaceRequest { x7 = stack.Id, x8 = 665, x9 = herb.Position + new Vector2(5.2f, -0.4f) }, 13);
        var events = peer4.Events.Count;
        Tick(TimeSpan.FromSeconds(1));
        var planted = peer4.Events.Skip(events).ToList();
        var crop = map.Objects.Values.OfType<FarmableObject>().SingleOrDefault();
        var nb = planted.FirstOrDefault(e => Code(e) == (int)EventCodes.NewBuilding);
        var foi = planted.FirstOrDefault(e => Code(e) == (int)EventCodes.FarmableObjectInfo);
        var foiKeys = foi?.Parameters.Keys.Where(k => k < 250).Order().ToList() ?? [];
        Check(stack.Count == 2 && crop is { Position: var slot } && slot == herb.Position + new Vector2(6, 0) && nb is not null && foi is not null
              && Convert.ToInt32(nb.Parameters[2]) == 665 && nb.Parameters[3] is "T6_FARM_FOXGLOVE_SEED" && nb.Parameters.ContainsKey(26) && nb.Parameters.ContainsKey(28)
              && foiKeys.Contains(0) && foiKeys.Contains(5) && foiKeys.Contains(12) && foiKeys.Contains(13) && !foiKeys.Contains(4) && !foiKeys.Contains(14)
              && foi.Parameters[12] is 1f && foi.Parameters[13] is long.MinValue && foi.Parameters[5] is long p5 && p5 == crop.Crop.Planted,
              $"planted after 1 s: stack 2, slot +6/0, NewBuilding {{2 665, 3 the seed, 26, 28}}, FOI keys {{0, 5, 12 1.0, 13 MinValue}} without 4/14 (got {string.Join(",", foiKeys)})");

        // Watering (§3.4) after 1 s: focus 5000 → 4000, FOI 14 = 1, event 320 {2 3, 4 665, 6 the stamp + 1 s}; a second one refused.
        events = peer4.Events.Count;
        var stamp = world.GameNow;
        Call(s4, peer4, new BoostFarmableRequest { wf = stamp, cr = 1, cs = 1, ct = crop!.Id }, 14);
        Tick(TimeSpan.FromSeconds(1));
        var boost = peer4.Events.Skip(events).FirstOrDefault(e => Code(e) == (int)EventCodes.BoostFarmable) is { } be ? Fill<BoostFarmableEvent>(be.Parameters) : null;
        Call(s4, peer4, new BoostFarmableRequest { wf = world.GameNow, cr = 2, cs = 1, ct = crop.Id }, 15);
        Tick(TimeSpan.FromSeconds(1));
        var info = world.Islands.Info(crop, world.GameNow);
        Check(me.Character.CraftingFocus == 4000 && crop.Crop.Boosts == 1 && boost is { bt0: 3, bt2: 665, btz: 1 } && boost.bt4 == stamp + TimeSpan.TicksPerSecond
              && info.b9m == 1 && info.b9c % 10000 == 0 && info.b9d - info.b9c * 1000 == crop.Crop.Planted,
              $"watered: focus 4000, one watering, event 320 {{2 3, 4 665, 6 +1 s}}, a second refused; FOI key 5 − key 4 × 1000 = the planting (got {me.Character.CraftingFocus}, {crop.Crop.Boosts})");

        // The harvest (§3.5): refused at 79199 s of growth, at 79200 s 3-6 T6_FOXGLOVE (+ the seed: watered), fame, the crop's Leave.
        Tick(TimeSpan.FromSeconds(79199) - TimeSpan.FromTicks(world.GameNow - crop.Crop.Planted));
        var early = Call(s4, peer4, new FarmableHarvestRequest { k9 = crop.Id }, 16);
        Tick(TimeSpan.FromSeconds(1));
        var fame = me.Character.Fame;
        var harvest = Call(s4, peer4, new FarmableHarvestRequest { k9 = crop.Id }, 17);
        var got = Fill<FarmableHarvestResponse>(harvest.Parameters);
        var names = got.la?.ToList() ?? [];
        var herbs = names.IndexOf("T6_FOXGLOVE") is var hi and >= 0 ? got.lb![hi] : 0;
        Check(early.ReturnCode == 1 && harvest.ReturnCode == 0 && herbs is >= 3 and <= 6 && names.Contains(seed.Name) && stack.Count == 3
              && me.Character.Fame > fame && !map.Objects.ContainsKey(crop.Id) && world.Islands.CropsOf(island.Id).Count == 0,
              $"harvest: 79199 s refused, 79200 s {herbs} T6_FOXGLOVE + the seed (watered), fame, crop gone (got {early.ReturnCode}/{harvest.ReturnCode}: {string.Join(",", names)})");

        // Destroy (op 75), then the store across a restart (a crop kept with its building), and Islands:GrowthSpeed 4 (19800 s).
        Call(s4, peer4, new PlaceableObjectPlaceRequest { x7 = stack.Id, x8 = 665, x9 = herb.Position }, 18);
        Call(s4, peer4, new PlaceableObjectPlaceRequest { x7 = stack.Id, x8 = 665, x9 = herb.Position + new Vector2(0, 6) }, 19);
        Tick(TimeSpan.FromSeconds(1));
        var crops = map.Objects.Values.OfType<FarmableObject>().ToList();
        var destroyed = crops.Count == 2 ? Call(s4, peer4, new FarmableDestroyRequest { lc = crops[0].Id }, 20) : null;
        var kept = crops[^1].Crop;
        var plantedAt = kept.Planted;
        world.Islands.Ripen(island); // .island ripen: the earlier stamp is stored too
        world.Dispose();
        long offset = 0;
        var again = new World(new ServerConfig { JoinByCharacterId = true, IslandsGrowthSpeed = 4 }, characters, Client.Items, Client.GameData, Client.Spells)
        {
            Sync = new object(), Clusters = Store, GameTicks = () => kept.Planted + offset,
        };
        var stored = again.Islands.CropsOf(island.Id);
        offset = TimeSpan.FromSeconds(19799).Ticks;
        var early4 = stored.Count == 1 && again.Islands.Ripe(stored[0], seed.Farm!, again.GameNow);
        offset = TimeSpan.FromSeconds(19800).Ticks;
        Check(destroyed?.ReturnCode == 0 && stored is [{ Item: "T6_FARM_FOXGLOVE_SEED" } c] && c.Id == kept.Id && c.Planted == kept.Planted && c.Planted < plantedAt
              && again.Islands.BuildingsOf(island.Id).Count == 2 && !early4 && again.Islands.Ripe(stored[0], seed.Farm!, again.GameNow),
              $"destroyed RC 0; after a restart the other crop (ripened) and both buildings are back; GrowthSpeed 4: grown at 19800 s, not 19799 (got {crops.Count} crops, {stored.Count} stored)");
        again.Dispose();
    }

    /// <summary>
    /// Stage N mounts (analysis/work/stageN/mounts/SPEC.md): a baby animal placed in a pasture like a seed, fed with FarmableFill (T3_WHEAT,
    /// 48 nutrition each, up to nutritionmax 480), grows while it has nutrition (330 s each: 480 × 330 = growtime 158400 s) and pauses
    /// without; FarmableFinishGrownItem gives the grown animal (the stable's mount resource) and takes it off the map.
    /// </summary>
    private static void Animals()
    {
        var dir = NewDir();
        var characters = Repository(dir);
        var clock = new ManualClock();
        var anchor = DateTime.UtcNow.Ticks;
        var world = new World(new ServerConfig { JoinByCharacterId = true }, characters, Client.Items, Client.GameData, Client.Spells)
        {
            Sync = new object(), Clusters = Store, MapSeed = 5, Clock = clock, GameTicks = () => anchor + clock.Now.Ticks,
        };
        world.MobAi = false;
        _ = Store.Get("ISLAND-PLAYER-FOREST-0001a"); // level 0: resolved, so that the Join fills the island's map at once
        var baby = Client.Items.Find("T3_FARM_HORSE_BABY")!;
        var hero = StarterKit.Default.NewCharacter("A" + Guid.NewGuid().ToString("N")[..10]);
        hero.Inventory.Add(new InventoryItem(baby.Name, 1));
        hero.Inventory.Add(new InventoryItem("T3_WHEAT", 12));
        var created = characters.Create(hero);
        var island = world.Islands.Give(hero, world.Islands.Data!.PersonalOf("1000")!, 0, out var why)!;
        var pasture = world.Islands.Build(island, "T1_PASTURE", new Vector2(145, 105), 0, out why);
        (hero.Cluster, hero.PositionX, hero.PositionY) = (island.Cluster, 150, 105);
        Check(created && pasture is not null && characters.Save(hero), $"island, pasture and character ({why})");
        var (s, peer, _) = Join(world, hero.Id);
        var me = s.Player!;
        OperationResponse Call(AlbionRequest req, int id)
        {
            lock (world.Sync) s.OnRequest(Request(req, id), encrypted: false);
            return peer.Responses[^1];
        }
        void Tick(TimeSpan by)
        {
            clock.Advance(by);
            lock (world.Sync) world.Update();
        }
        var stack = me.Inventory.Bag.Items.Single(i => i.Template == baby);
        Call(new PlaceableObjectPlaceRequest { x7 = stack.Id, x8 = world.Islands.ItemBuildingId(baby), x9 = new Vector2(151, 105) }, 3);
        Tick(TimeSpan.FromSeconds(1));
        var animal = me.Map!.Objects.Values.OfType<FarmableObject>().SingleOrDefault();
        Check(baby.Animal is { GrownItem: "T3_FARM_HORSE_GROWN", GrowTime: 158400, NutritionMax: 480, FoodCategory: "plants" } && animal is not null,
              $"T3_FARM_HORSE_BABY placed in the pasture (animal data {baby.Animal})");

        // Unfed it does not grow; 12 wheat offered, 10 eaten (480); half the time grown half; collecting refused until grown.
        Tick(TimeSpan.FromHours(10));
        var unfed = world.Islands.AnimalAt(animal!.Crop, baby.Animal!, world.GameNow).Growth;
        var wheat = me.Inventory.Bag.Items.Single(i => i.Template.Name == "T3_WHEAT");
        var fed = Fill<FarmableFillResponse>(Call(new FarmableFillRequest { ld = animal.Id, le = [wheat.Id], lf = [12] }, 4).Parameters);
        Tick(TimeSpan.FromSeconds(79200));
        var early = Call(new FarmableFinishGrownItemRequest { lg = animal.Id }, 5);
        var half = world.Islands.Info(animal, world.GameNow);
        Tick(TimeSpan.FromSeconds(79200 + 3600)); // an hour more than it needs: it stops at its growtime
        var done = Call(new FarmableFinishGrownItemRequest { lg = animal.Id }, 6);
        var got = Fill<FarmableFinishGrownItemResponse>(done.Parameters);
        Check(unfed == 0 && fed.lb is [10] && wheat.Count == 2 && early.ReturnCode == 1 && half.b9c == 79200 * 10000L && half.b9g is [var n] && n == 240 * 10000L
              && done.ReturnCode == 0 && got.la is ["T3_FARM_HORSE_GROWN", ..] && me.Inventory.Bag.Items.Any(i => i.Template.Name == "T3_FARM_HORSE_GROWN")
              && !me.Map.Objects.ContainsKey(animal.Id) && world.Islands.CropsOf(island.Id).Count == 0,
              $"unfed growth {unfed}; fed {string.Join(",", fed.lb ?? [])} wheat (left {wheat.Count}); at 79200 s RC {early.ReturnCode}, FOI 4 = {half.b9c}, 8 = {string.Join(",", half.b9g ?? [])}; collected RC {done.ReturnCode}: {string.Join(",", got.la ?? [])}");
        world.Dispose();
    }

    /// <summary>A level-0 Lymhurst island (…0001a) and its owner standing on it with <paramref name="items"/> and 1000 silver, joined.</summary>
    private static (World World, Island Island, WorldSession Session, FakePeer Peer, Func<AlbionRequest, int, OperationResponse> Call, Action<TimeSpan> Tick)
        OnIsland(params (string Item, int Count)[] items)
    {
        var characters = Repository(NewDir());
        var clock = new ManualClock();
        var anchor = DateTime.UtcNow.Ticks;
        var world = new World(new ServerConfig { JoinByCharacterId = true }, characters, Client.Items, Client.GameData, Client.Spells)
        {
            Sync = new object(), Clusters = Store, MapSeed = 5, Clock = clock, GameTicks = () => anchor + clock.Now.Ticks,
        };
        world.MobAi = false;
        _ = Store.Get("ISLAND-PLAYER-FOREST-0001a");
        var hero = StarterKit.Default.NewCharacter("A" + Guid.NewGuid().ToString("N")[..10]);
        hero.Silver = 1000 * Pricing.A7m;
        foreach (var (item, count) in items) hero.Inventory.Add(new InventoryItem(item, count));
        characters.Create(hero);
        var island = world.Islands.Give(hero, world.Islands.Data!.PersonalOf("1000")!, 0, out _)!;
        (hero.Cluster, hero.PositionX, hero.PositionY) = (island.Cluster, 180, 110);
        characters.Save(hero);
        var (s, peer, _) = Join(world, hero.Id);
        OperationResponse Call(AlbionRequest req, int id)
        {
            lock (world.Sync) s.OnRequest(Request(req, id), encrypted: false);
            return peer.Responses[^1];
        }
        void Tick(TimeSpan by)
        {
            clock.Advance(by);
            lock (world.Sync) world.Update();
        }
        return (world, island, s, peer, Call, Tick);
    }

    /// <summary>
    /// Islands phase 5 (analysis/work/stageN/islands5/SPEC.md §1): the plots of the templates (level 0 one, level 5 sixteen); ConstructionSiteCreate
    /// of a T1_PASTURE on the level-0 plot takes its placecost (100 silver) and its craftingrequirements at once and answers its object id; the
    /// same plot again and a spot off the buildable ground are refused; BuildingGiveup takes it away (nothing back).
    /// </summary>
    private static void Construction()
    {
        var lymhurst = Store.Islands.PersonalOf("1000")!;
        var plots = (Islands.PlotCount(Store.Get(lymhurst.LevelAt(0)!.Cluster)), Islands.PlotCount(Store.Get(lymhurst.LevelAt(5)!.Cluster)));
        var (world, island, s, _, call, _) = OnIsland(("T1_WOOD", 30), ("T1_ROCK", 30), ("T3_PLANKS", 30), ("T3_STONEBLOCK", 30));
        var me = s.Player!;
        var pasture = Store.Buildings.Find("T1_PASTURE")!;
        var cell = me.Map!.Template!.BuildCells.OrderBy(c => c.X).ThenBy(c => c.Y).First();
        var at = cell + new Vector2(5.4f, 4.6f);
        var built = call(new ConstructionSiteCreateRequest { g3 = pasture.Id, g4 = [at.X, at.Y], g5 = 0 }, 3);
        var building = me.Map.Objects.Values.OfType<PlayerBuilding>().SingleOrDefault();
        var again = call(new ConstructionSiteCreateRequest { g3 = pasture.Id, g4 = [at.X, at.Y], g5 = 0 }, 4);
        var off = call(new ConstructionSiteCreateRequest { g3 = pasture.Id, g4 = [20, 20], g5 = 0 }, 5);
        Check(plots == (1, 16) && built.ReturnCode == 0 && building is { Template.Name: "T1_PASTURE" } && building.Position == cell + new Vector2(5, 5)
              && Fill<ConstructionSiteCreateResponse>(built.Parameters).aoj == building.Id && me.Character.Silver == 900 * Pricing.A7m
              && me.Inventory.Bag.Items.Count(i => i.Template.Name is "T1_WOOD" or "T1_ROCK" or "T3_PLANKS" or "T3_STONEBLOCK") == 0
              && again.ReturnCode == 1 && off.ReturnCode == 1 && world.Islands.BuildingsOf(island.Id).Count == 1,
              $"plots {plots}; pasture built RC {built.ReturnCode} at {building?.Position} (cell {cell}), silver {me.Character.Silver / Pricing.A7m}, resources taken; again RC {again.ReturnCode}, off RC {off.ReturnCode}");
        var gone = building is null ? null : call(new BuildingGiveupRequest { c4 = building.Id }, 6);
        Check(gone?.ReturnCode == 0 && world.Islands.BuildingsOf(island.Id).Count == 0 && !me.Map.Objects.Values.OfType<PlayerBuilding>().Any(),
              $"given up: RC {gone?.ReturnCode}, no building left");
        world.Dispose();
    }

    /// <summary>
    /// analysis/work/stageN/labourers/SPEC.md: a T2_PLAYERHOUSE on the level-0 plot (op 68: 100 silver, its resources); a T2 bed and a T2 table
    /// placed in it (op 69: happiness target 200); a T2_LABOURER_WOOD hired by placing its contract (op 69, the contract used up); before it
    /// has settled in (1800 s) the job is refused; after, a T2_JOURNAL_WOOD_FULL starts the job at yield 1 (happiness 100 → 200 in 360 s);
    /// the loot is refused before the 79200 s and then gives the journal's baselootamount of T2 wood and the labourer fame.
    /// </summary>
    private static void Labourers()
    {
        var (world, island, s, _, call, tick) = OnIsland(("T1_WOOD", 30), ("T1_ROCK", 3), ("T2_STONEBLOCK", 180), ("T2_FURNITUREITEM_BED", 1),
            ("T2_FURNITUREITEM_TABLE", 1), ("T2_LABOURER_CONTRACT_WOOD", 1), ("T2_JOURNAL_WOOD_FULL", 1));
        var me = s.Player!;
        var house = Store.Buildings.Find("T2_PLAYERHOUSE")!;
        var cell = me.Map!.Template!.BuildCells.OrderBy(c => c.X).ThenBy(c => c.Y).First();
        var built = call(new ConstructionSiteCreateRequest { g3 = house.Id, g4 = [cell.X + 5, cell.Y + 5], g5 = 0 }, 3);
        var centre = cell + new Vector2(5, 5);
        long Stack(string name) => me.Inventory.Bag.Items.Single(i => i.Template.Name == name).Id;
        var bed = Client.Items.Find("T2_FURNITUREITEM_BED")!;
        var table = Client.Items.Find("T2_FURNITUREITEM_TABLE")!;
        call(new PlaceableObjectPlaceRequest { x7 = Stack(bed.Name), x8 = world.Islands.ItemBuildingId(bed), x9 = centre + new Vector2(4, 4) }, 4);
        call(new PlaceableObjectPlaceRequest { x7 = Stack(table.Name), x8 = world.Islands.ItemBuildingId(table), x9 = centre + new Vector2(-4, 4) }, 5);
        var type = world.Islands.LabourerTypes["T2_LABOURER_WOOD"];
        var hired = call(new PlaceableObjectPlaceRequest { x7 = Stack("T2_LABOURER_CONTRACT_WOOD"), x8 = type.Id, x9 = centre + new Vector2(0, -4) }, 6);
        var labourer = me.Map.Objects.Values.OfType<LabourerObject>().SingleOrDefault();
        var target = labourer is null ? -1 : world.Islands.HappinessTarget(labourer.Labourer);
        Check(built.ReturnCode == 0 && hired.ReturnCode == 0 && labourer is not null && me.Map.Objects.Values.OfType<FurnitureObject>().Count() == 2 && target == 200
              && me.Inventory.Bag.Items.All(i => i.Template.Name is not ("T2_STONEBLOCK" or "T2_FURNITUREITEM_BED" or "T2_LABOURER_CONTRACT_WOOD"))
              && me.Character.Silver == 900 * Pricing.A7m,
              $"house RC {built.ReturnCode}, hire RC {hired.ReturnCode}, labourer {labourer?.Labourer}, furniture {me.Map.Objects.Values.OfType<FurnitureObject>().Count()}, target {target}, silver {me.Character.Silver / Pricing.A7m}");
        if (labourer is null) { world.Dispose(); return; }
        var early = call(new LaborerStartJobRequest { tb = labourer.Id, tc = Stack("T2_JOURNAL_WOOD_FULL") }, 7);
        tick(TimeSpan.FromSeconds(1800));
        var started = call(new LaborerStartJobRequest { tb = labourer.Id, tc = Stack("T2_JOURNAL_WOOD_FULL") }, 8);
        tick(TimeSpan.FromSeconds(79199));
        var soon = call(new LaborerTakeJobLootRequest { td = labourer.Id }, 9);
        tick(TimeSpan.FromSeconds(1));
        var loot = call(new LaborerTakeJobLootRequest { td = labourer.Id }, 10);
        var journal = Client.Items.Find("T2_JOURNAL_WOOD_FULL")!.Journal!;
        var wood = me.Inventory.Bag.Items.Where(i => i.Template.BaseName.StartsWith("T2_WOOD", StringComparison.Ordinal)).Sum(i => i.Count);
        Check(early.ReturnCode == 1 && started.ReturnCode == 0 && soon.ReturnCode == 1 && loot.ReturnCode == 0 && wood == (int)Math.Round(journal.BaseLoot)
              && labourer.Labourer.Fame > 0 && labourer.Labourer.Journal is null && world.Islands.LabourersOf(labourer.Labourer.Building).Count == 1,
              $"job before settling RC {early.ReturnCode}, after RC {started.ReturnCode}; loot at 79199 s RC {soon.ReturnCode}, at 79200 s RC {loot.ReturnCode}: {wood} wood (baselootamount {journal.BaseLoot}), fame {labourer.Labourer.Fame}");
        world.Dispose();
    }

    /// <summary>
    /// Islands phase 5 / mounts SPEC "not done": PlaceableObjectPickup of a placed (fed) T3 horse baby gives the baby back and takes the animal
    /// off the map and out of the store; a planted seed (pickupable false) is refused.
    /// </summary>
    private static void Pickup()
    {
        var (world, island, s, _, call, tick) = OnIsland(("T3_FARM_HORSE_BABY", 1), ("T3_WHEAT", 5), ("T3_FARM_WHEAT_SEED", 1));
        var me = s.Player!;
        var baby = Client.Items.Find("T3_FARM_HORSE_BABY")!;
        var seed = Client.Items.Find("T3_FARM_WHEAT_SEED")!;
        world.Islands.Build(island, "T1_PASTURE", new Vector2(185, 115), 0, out _);
        world.Islands.Build(island, "T1_FARMHOUSE", new Vector2(165, 110), 0, out _);
        call(new PlaceableObjectPlaceRequest { x7 = me.Inventory.Bag.Items.Single(i => i.Template == baby).Id, x8 = world.Islands.ItemBuildingId(baby), x9 = new Vector2(185, 115) }, 3);
        call(new PlaceableObjectPlaceRequest { x7 = me.Inventory.Bag.Items.Single(i => i.Template == seed).Id, x8 = world.Islands.ItemBuildingId(seed), x9 = new Vector2(171, 110) }, 4);
        tick(TimeSpan.FromSeconds(1));
        var placed = me.Map!.Objects.Values.OfType<FarmableObject>().ToList();
        var animal = placed.SingleOrDefault(o => o.Seed == baby);
        var crop = placed.SingleOrDefault(o => o.Seed == seed);
        if (animal is not null) call(new FarmableFillRequest { ld = animal.Id, le = [me.Inventory.Bag.Items.Single(i => i.Template.Name == "T3_WHEAT").Id], lf = [5] }, 5);
        var up = animal is null ? null : call(new PlaceableObjectPickupRequest { x5 = animal.Id }, 6);
        var seedUp = crop is null ? null : call(new PlaceableObjectPickupRequest { x5 = crop.Id }, 7);
        Check(up?.ReturnCode == 0 && me.Inventory.Bag.Items.Count(i => i.Template == baby) == 1 && !me.Map.Objects.ContainsKey(animal!.Id)
              && world.Islands.CropsOf(island.Id).Count == 1 && seedUp?.ReturnCode == 1 && me.Map.Objects.ContainsKey(crop!.Id),
              $"picked up: RC {up?.ReturnCode}, baby back {me.Inventory.Bag.Items.Count(i => i.Template == baby)}, the seed's pick-up RC {seedUp?.ReturnCode} ({placed.Count} placed)");
        world.Dispose();
    }

    private static (WorldSession Session, FakePeer Peer, JoinResponse Join) Join(World world, Guid key)
    {
        var peer = new FakePeer();
        var session = new WorldSession(peer, world);
        lock (world.Sync) session.OnRequest(Request(new JoinRequest { ss = key }, null), encrypted: true);
        Check(peer.Responses is [{ ReturnCode: 0 }], "join answered");
        return (session, peer, Fill<JoinResponse>(peer.Responses[0].Parameters));
    }
}
