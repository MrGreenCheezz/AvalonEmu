using System.Globalization;
using System.Numerics;
using System.Text.Json;
using AlbionLocal.Photon;
using AlbionLocal.Photon.Serialization;
using AlbionLocal.Server;
using AlbionLocal.Server.Data;
using AlbionLocal.Server.Network;
using AlbionLocal.Server.Protocol;
using static AlbionLocal.SelfTest.AlbionTests;

namespace AlbionLocal.SelfTest;

/// <summary>
/// The cluster data (step 11; analysis/work/stage2/SPEC.md §2 and §4) on the client's own files: the building and harvestable ids, the
/// offline golden checks of the clusters the data track verified, the Thetford slice the spec was written from, and the
/// lazy load at the first Join. All share the one <see cref="ClusterStore"/> of <see cref="AlbionTests.Client"/>.
/// </summary>
public static class ClusterDataTest
{
    public static IEnumerable<(string, Action)> All =>
    [
        ("cluster data: read at the first Join into a cluster, in the background, not at startup; a failed load is retried; config keys", Lazy),
        ("cluster data: building, harvestable, expedition agent and quest giver type ids equal the captured values (ClientNumbering)", Numbering),
        ("cluster data: golden checks of 0000, 0006, 0007, 1000, 1001, 1002, 1207 (critical objects, exits, distribution, mob counts, bounds)", Golden),
        ("cluster data: Thetford, its bank and its market equal spec/slice_000x.json (types, positions, guids)", Slices),
    ];

    private static ClusterStore Store => Client.Clusters!;

    /// <summary>
    /// A cluster is read at the first Join into it, not before (ServerHost only hands the store on), and the Join does not
    /// wait for it: here the load is a task the test completes itself, so the Join into 0008 (no other test uses it) must
    /// be answered while it still runs; the map gets the template in a later tick, and the player its world objects then
    /// (phases A, B and C in one late pass, VisibilityTracker.Refresh). A failed load is tried again at the next Join.
    /// Without a loader of its own the world reads through the store. The config keys reach the store's options.
    /// </summary>
    private static void Lazy()
    {
        var dir = NewDir();
        var characters = Repository(dir);
        var hero = characters.List()[0];
        hero.Cluster = "0008";
        characters.Save(hero);
        var asked = new List<string>();
        var load = new TaskCompletionSource<ClusterTemplate>();
        var world = new World(new ServerConfig { JoinByCharacterId = true }, characters, Client.Items)
        {
            Sync = new object(), Clock = new ManualClock(), Clusters = Store,
            LoadCluster = id =>
            {
                asked.Add(id);
                load = new TaskCompletionSource<ClusterTemplate>(TaskCreationOptions.RunContinuationsAsynchronously);
                return load.Task;
            },
        };
        void TickUntil(World w, Func<bool> done)
        {
            for (var waited = 0; !done() && waited < 60000; waited += 20)
            {
                Thread.Sleep(20);
                lock (w.Sync) w.Update();
            }
        }
        Check(!Store.IsLoaded("0008") && world.Maps.Find("0008") is null, "0008 is not loaded before anyone enters it");
        var peer = new FakePeer();
        var session = new WorldSession(peer, world);
        session.OnRequest(Request(new JoinRequest { ss = hero.Id }, 1), encrypted: false);
        var map = world.Maps.Find("0008")!;
        Check(peer.Responses is [{ ReturnCode: 0 }] && session.Player?.Map == map && asked is ["0008"] && map is { TemplateRequested: true, Template: null },
              "the Join is answered while the load of its cluster still runs");
        var level = Log.Level;
        Log.Level = LogLevel.Error; // the warning about the failed load
        load.SetException(new IOException("cluster/world.bin is being rewritten"));
        TickUntil(world, () => !map.TemplateRequested);
        Log.Level = level;
        world.LoadTemplate(map); // as the next Join does
        Check(asked is ["0008", "0008"] && map.TemplateRequested, "a failed load is tried again at the next Join");
        load.SetResult(Store.Get("0008"));
        TickUntil(world, () => map.Template is not null);
        Check(map.Template is { Id: "0008", Buildings.Count: > 0 } t && t.Exits.Count > 0 && Store.IsLoaded("0008"), "the map has its cluster after a later tick");
        var finished = peer.Sequence.FindIndex(x => x is EventData e && WorldObjectTest.Code(e) == (int)EventCodes.JoinFinished);
        var late = peer.Sequence.Skip(finished + 1).OfType<EventData>().Where(e => WorldObjectTest.WorldCodes.Contains(WorldObjectTest.Code(e))).ToList();
        var sent = late.SelectMany(e => e.Parameters[0] switch { long[] ids => ids, var id when WorldObjectTest.Code(e) != (int)EventCodes.QuestGiverInfoForPlayer => [Convert.ToInt64(id)], _ => [] });
        Check(finished > 0 && peer.Sequence.Take(finished).OfType<EventData>().All(e => !WorldObjectTest.WorldCodes.Contains(WorldObjectTest.Code(e)))
              && late.Any(e => WorldObjectTest.Code(e) is (int)EventCodes.NewBuilding or (int)EventCodes.NewExit)
              && sent.Order().SequenceEqual(session.Player!.Visibility.Known.Select(o => o.Id).Order()),
              $"the player who joined early gets the window's objects on that tick, each once (got {late.Count} events, {session.Player!.Visibility.Known.Count} known)");
        var count = peer.Events.Count;
        lock (world.Sync) world.Update();
        Check(peer.Events.Skip(count).All(e => !WorldObjectTest.WorldCodes.Contains(WorldObjectTest.Code(e))), "the next tick sends no world object again");
        var plain = new World(new ServerConfig { JoinByCharacterId = true }, characters, Client.Items) { Sync = new object(), Clock = new ManualClock(), Clusters = Store };
        var other = new Map("0008");
        plain.LoadTemplate(other);
        TickUntil(plain, () => other.Template is not null);
        Check(other.Template == map.Template, "without a loader of its own the world reads through the store (cached: the same template)");
        plain.Dispose();
        world.Dispose();
        characters.Dispose();
        try { Directory.Delete(dir, true); } catch (IOException) { }

        var config = LoadTestConfig(Path.Combine(dir, "none.json"), ["--World:EventLayers=halloween, easter", "--ClientNumbering:BuildingIdOffset=0"]);
        Check(config.Clusters is { BuildingIdOffset: 0 } o && o.EventLayers.SetEquals(["halloween", "easter"])
              && new ServerConfig().Clusters is { BuildingIdOffset: 1, EventLayers.Count: 0 }, "World:EventLayers and ClientNumbering:BuildingIdOffset, default none and 1");
        try { LoadTestConfig(Path.Combine(dir, "none.json"), ["--World:EventLayers=halloweem"]); Check(false, "an unknown event layer was accepted"); }
        catch (InvalidDataException e) { Check(e.Message.Contains("halloweem"), $"an unknown event layer is named (got {e.Message})"); }
    }

    /// <summary>SPEC §4: building id = buildings.xml index + 1 (13 captured buildings), harvestable id = &lt;Harvestable&gt; index.</summary>
    private static void Numbering()
    {
        var buildings = Store.Buildings;
        foreach (var (name, id) in new (string, short)[]
                 {
                     ("SWAMP_GREEN_REPAIRSHOP", 495), ("SWAMP_GREEN_VANITY_STORE", 371), ("SWAMP_GREEN_T8_MELDBUILDING", 27),
                     ("SWAMP_GREEN_FACTION_TRADER", 21), ("ARENA_AGENCY", 349), ("SWAMP_GREEN_T1_BANK", 52), ("SWAMP_GREEN_MARKETPLACE_CENTERCITY", 125),
                     ("FOREST_GREEN_REPAIRSHOP", 497), ("T8_HUNTERSLODGE", 417), ("T1_HERBGARDEN", 344), ("FARMING_MERCHANT_HIDEOUT", 348),
                 })
            Check(buildings.Find(name)?.Id == id && buildings[id]?.Name == name, $"{name} = {id} (got {buildings.Find(name)?.Id})");
        Check(buildings.All[0].Id == 1 && buildings.Find("CASTLE_WALLS") is null && buildings.Find("TERRITORY_CASTLE_WALLS") is null,
              "the first element is 1; durabilitythresholddefinition and fortificationtype are no buildings");
        Check(buildings.Find("SWAMP_GREEN_REPAIRSHOP") is { Kind: "repairbuilding", Reference: "T8_REPAIRSHOP", Durability: 1749600, NutritionStorage: 795000, CraftCapacity: 794993 }
              && buildings.Find("SWAMP_GREEN_T1_BANK") is { Durability: 100, NutritionStorage: 1, CraftCapacity: 0 }
              && buildings.Find("ARENA_AGENCY") is { Durability: 1, CraftCapacity: 0 },
              "a reference takes its template's numbers; absent durability 100, nutrition 1, craft capacity 0 (NewBuilding keys 7/18/21)");
        Check(new ClusterStore(Store.Folder, new ClusterOptions { BuildingIdOffset = 0 }).Buildings.Find("ARENA_AGENCY")?.Id == 348,
              "ClientNumbering:BuildingIdOffset moves every id");

        var harvestables = Store.Harvestables;
        foreach (var (name, id) in new (string, byte)[] { ("WOOD", 0), ("WOOD_GIANTTREE", 2), ("WOOD_CRITTER", 3), ("ROCK", 7), ("FIBER", 14), ("HIDE", 20), ("ORE", 27) })
            Check(harvestables.Find(name)?.Id == id && harvestables[id]?.Name == name, $"harvestable {name} = {id} (got {harvestables.Find(name)?.Id})");
        Check(harvestables.Count == 89 && harvestables.Find("WOOD")!.Tiers[1].Charges == 2 && harvestables.Find("ROCK")!.Tiers[1].Charges == 2
              && harvestables.Find("WOOD_GIANTTREE") is { Resource: "WOOD" } giant && giant.Tiers[1].Charges == 5
              && new[] { 2, 3, 4 }.All(t => harvestables.Find("WOOD")!.Tiers[t].Charges == 3) && harvestables.Find("FIBER")!.Tiers.Keys.First() == 2,
              "full charges: WOOD and ROCK T1 2, WOOD_GIANTTREE T1 5, T2-T4 3; FIBER starts at T2");
        Check(Store.ExpeditionAgents.IdOf("T6_EXPAGENT_ROYAL_CITY") == 4 && Store.ExpeditionAgents.Names[0] == "TEST"
              && Store.QuestGiverTypes is { Count: 26 } q && q.IdOf("FACTIONSTORE_SWAMP") == 20 && q.IdOf("FACTIONSTORE_FOREST") == 21 && q.IdOf("GOTO_A") == 0,
              "expedition agent T6_EXPAGENT_ROYAL_CITY = 4 (captured), quest giver types FACTIONSTORE_SWAMP 20 / FOREST 21 (captured) of 26");
    }

    /// <summary>The <see cref="ContentCheck"/> of each cluster the data track verified, plus the numbers SPEC §2, §5 and §7 give.</summary>
    private static void Golden()
    {
        var mobs = ContentCheck.Mobs(GameDataFile.Open(Store.Folder, "mobs.bin"));
        // world.xml counts the default templates: the checks resolve without a layout session (no dyn roll, as --check-content).
        foreach (var id in new[] { "0000", "0006", "0007", "1000", "1001", "1002", "1207" })
        {
            var failures = ContentCheck.Check(Store.Resolve(id), Store.Harvestables, mobs);
            Check(failures.Count == 0, $"{id}: {string.Join(" | ", failures)}");
        }
        Check(Store.Tiles.LoadTime < TimeSpan.FromSeconds(30) && Store.World.LoadTime < TimeSpan.FromSeconds(30),
              $"indexes in seconds (world.bin {Store.World.LoadTime.TotalSeconds:0.0} s, tiles.bin {Store.Tiles.LoadTime.TotalSeconds:0.0} s)");

        var thetford = Store.Get("0000");
        Check(thetford is { Origin: { X: -305, Y: -305 }, Size: { X: 600, Y: 600 }, Name.TemplateFolder: "GREEN", Critical.Count: 104, Exits.Count: 9, RealEstates.Count: 80 }
              && thetford.Instances is [{ Slot: "instanceslot_00", Folder: "GREEN" } instance] && instance.ActiveLayers.SetEquals(["Layer_01"])
              && thetford.Harvestables.Count == 271 && thetford.Harvestables.All(h => h is { Type.Name: "WOOD", Tier.Tier: 1, Charges: 2 })
              && thetford.MobSpawnPoints.Count == 0 && thetford.CriticalObjects.Count == 105,
              "0000: origin, 104 of 105 critical objects, 9 exits, 80 real estates, 271 WOOD T1 with 2 charges, no spawn points, only Layer_01");
        Check(thetford.Buildings.All(b => b.Key.ToString() == $"0000:instanceslot_00:{b.Key.Index}") && thetford.Objects.Select(o => o.Key).Distinct().Count() == thetford.Objects.Count(),
              "spawn keys are <cluster>:<slot>:<n>, one per object");
        // n counts the template's structure, not the kinds placed (TemplateLayout); the values are an independent count
        // (Python over the decrypted files): a member, a top-level tree, a compound's tree, a marker, a compound's spawn point.
        static string KeyAt<T>(ClusterTemplate c, float x, float z) where T : ClusterObject =>
            string.Join(" ", c.Objects.OfType<T>().Where(o => Math.Abs(o.Position.X - x) < 0.01 && Math.Abs(o.Position.Y - z) < 0.01).Select(o => o.Key));
        foreach (var (key, got) in new[]
                 {
                     ("0000:instanceslot_00:18", KeyAt<BuildingSpawn>(thetford, 16, 8)), ("0000:instanceslot_00:1039", KeyAt<HarvestableSpawn>(thetford, 44.5f, 3.5f)),
                     ("0000:instanceslot_00:3055", KeyAt<HarvestableSpawn>(thetford, 116.5f, -86.5f)), ("0000:instanceslot_00:2082", KeyAt<LocationMarkerSpawn>(thetford, 7.5f, 0.5f)),
                     ("1000:instanceslot_00:3457", KeyAt<MobSpawnPoint>(Store.Get("1000"), 226.8f, 165.33f)),
                 })
            Check(got == key, $"spawn key {key} (got '{got}')");
        foreach (var (id, x, z) in new[] { ("1000", -335f, -305f), ("1207", -466f, -466f), ("1001", -65f, -55f), ("1002", -145f, -115f) })
            Check(Store.Get(id).Origin == new Vector2(x, z), $"{id} origin ({x}, {z}) (SPEC §5)");
        var birchcopse = Store.Resolve("1207");
        Check(Store.Get("1000").MobSpawnPoints.Count(m => m.Mob == "MOB_RABBIT") == 6 && birchcopse.MobSpawnPoints.Count(m => m.Mob == "MOB_RABBIT") == 231
              && birchcopse.Harvestables.Count == 1573 && birchcopse.Resources is null, "1000: 6 rabbit spawn points; 1207 (default templates): 231, and 1572 WOOD + 1 ROCK");
    }

    /// <summary>
    /// spec/slice_0000.json (and the bank and market, 0006 and 0007): the objects of the spec's first slice, made by the
    /// reference resolver (clusterdump.py) and checked against the captures. Every record must match one object of the
    /// same kind and fields within 0.002 per number; the counts per kind must be equal. Minimap markers are data only
    /// and not resolved.
    /// </summary>
    private static void Slices()
    {
        foreach (var id in new[] { "0000", "0006", "0007" })
        {
            using var doc = JsonDocument.Parse(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Fixtures", $"slice_{id}.json")));
            var expected = new List<(string Kind, string Fields, double[] Numbers)>();
            foreach (var e in doc.RootElement.EnumerateArray())
            {
                string S(string key) => e.TryGetProperty(key, out var v) ? v.ValueKind == JsonValueKind.String ? v.GetString()! : v.GetRawText() : "";
                double[] N(params string[] keys) => [.. keys.SelectMany(k => e.TryGetProperty(k, out var v)
                    ? v.ValueKind == JsonValueKind.Array ? v.EnumerateArray().Select(x => x.GetDouble())
                    : v.ValueKind == JsonValueKind.String ? v.GetString()!.Split(' ').Select(x => double.Parse(x, CultureInfo.InvariantCulture)) : [v.GetDouble()] : [])];
                var record = S("event") switch
                {
                    "NewBuilding" => ("building", $"{S("tile")}|{S("guid")}|{S("type_1based_datatrack")}", N("pos", "rot")),
                    "NewExit" => ("exit", $"{S("tile")}|{S("guid")}|{S("target")}", N("pos", "worldxml_pos", "spawn")),
                    "NewTravelpoint" => ("travelpoint", $"{S("tile")}|{S("guid")}", N("pos")),
                    "NewFactionWarfarePortal" => ("portal", $"{S("tile")}|{S("guid")}", N("pos")),
                    "NewIslandAccessPoint" => ("island", S("guid"), N("pos", "rot")),
                    "NewExpeditionAgent" => ("agent", $"{S("guid")}|{S("agent")}", N("pos")),
                    "NewLocationMarker" => ("marker", S("locationid"), N("pos", "radius")),
                    "Harvestable" => ("harvestable", $"{S("tile")}|{S("type")}|{S("tier")}|{S("charges")}", N("pos")),
                    "NewRealEstate(later)" => ("realestate", $"{S("guid")}|{S("size")}", N("pos")),
                    "NewFishingZoneObject(later)" => ("fishing", S("zone"), N("pos")),
                    "none:placeholderspot" => ("placeholder", S("tile"), N("pos")),
                    "none:respawnpoint" => ("respawn", S("tile"), N("pos")),
                    "none:minimapmarker" => (null, "", []),
                    var other => throw new Exception($"slice_{id}.json: unknown event {other}"),
                };
                if (record.Item1 is { } kind) expected.Add((kind, record.Item2, record.Item3));
            }

            var t = Store.Get(id);
            static double[] P(ClusterObject o, params double[] more) => [o.Position.X, o.Position.Y, .. more];
            static double[] V(Vector2? v) => v is { } p ? [p.X, p.Y] : [];
            static string G(Guid g) => g.ToString();
            var actual = new List<(string Kind, string Fields, double[] Numbers)>();
            actual.AddRange(t.Buildings.Select(b => ("building", $"{b.Tile}|{G(b.Guid)}|{b.TypeId}", P(b, b.Direction))));
            actual.AddRange(t.Exits.Select(e => ("exit", $"{e.Tile}|{G(e.Guid)}|{e.World?.Target}@{e.World?.TargetCluster}", P(e).Concat(V(e.World?.Position)).Concat(V(e.SpawnPosition)).ToArray())));
            actual.AddRange(t.Travelpoints.Select(p => ("travelpoint", $"{p.Tile}|{G(p.Guid)}", P(p))));
            actual.AddRange(t.FactionWarfarePortals.Select(p => ("portal", $"{p.Tile}|{G(p.Guid)}", P(p))));
            actual.AddRange(t.IslandAccessPoints.Select(a => ("island", G(a.Guid), P(a, a.Direction))));
            actual.AddRange(t.ExpeditionAgents.Select(a => ("agent", $"{G(a.Guid)}|{a.AgentType}", P(a))));
            actual.AddRange(t.LocationMarkers.Select(m => ("marker", m.LocationId, P(m, m.Radius))));
            actual.AddRange(t.Harvestables.Select(h => ("harvestable", $"{h.Tile}|{h.Type.Id}|{h.Tier.Tier}|{h.Charges}", P(h))));
            actual.AddRange(t.RealEstates.Select(r => ("realestate", $"{G(r.Guid)}|{r.Attr("size")}", P(r))));
            actual.AddRange(t.FishingZones.Select(f => ("fishing", f.Zone ?? "", P(f))));
            actual.AddRange(t.Critical.Where(m => m.Type == "placeholderspot").Select(m => ("placeholder", m.Tile, P(m))));
            actual.AddRange(t.RespawnPoints.Select(r => ("respawn", r.Tile, P(r))));

            var kinds = expected.Select(e => e.Kind).Concat(actual.Select(a => a.Kind)).Distinct().Order().ToList();
            var counts = kinds.Select(k => (k, Want: expected.Count(e => e.Kind == k), Got: actual.Count(a => a.Kind == k))).ToList();
            Check(counts.All(c => c.Want == c.Got), $"{id}: counts per kind {string.Join(", ", counts.Select(c => $"{c.k} {c.Got}/{c.Want}"))}");
            var unmatched = new List<string>();
            foreach (var e in expected)
            {
                var hit = actual.FindIndex(a => a.Kind == e.Kind && a.Fields == e.Fields && a.Numbers.Length == e.Numbers.Length
                                                && a.Numbers.Zip(e.Numbers).All(p => Math.Abs(p.First - p.Second) <= 0.002));
                if (hit < 0) unmatched.Add($"{e.Kind} {e.Fields} [{string.Join(", ", e.Numbers)}]");
                else actual.RemoveAt(hit);
            }
            Check(unmatched.Count == 0, $"{id}: {unmatched.Count} of {expected.Count} slice records unmatched, first {unmatched.FirstOrDefault()}");
        }
    }

    private static void Check(bool cond, string what) => AlbionTests.Check(cond, what);
}
