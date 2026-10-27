using System.Numerics;
using AlbionLocal.Photon;
using AlbionLocal.Server.Data;
using AlbionLocal.Server.Entities;

namespace AlbionLocal.Server;

/// <summary>
/// Fills a map with the world objects of its resolved cluster (ARCHITECTURE.md §4.2; SPEC stage 2 §1, §3): one object per
/// template object of a sendable kind, its object id taken now, so it stays the same while the map lives. Before an
/// object is made, what the client needs of its event is checked (SPEC §3 "Required keys and silent failures": the client
/// throws or drops the object without telling the server), and what fails is left out with a warning. Content patches
/// (content/spawns) come with the stages that need them; real estates, hall-of-fame spots and outposts are not spawned
/// yet (the fishing zones are, since stageG). The resource nodes (stage 4b) come from the cluster's <see cref="ResourceLayout"/>, the
/// layout the client computes for the same Login key 57. The mobs (stage 5a) stand at the spawn points of the template
/// and of that layout's prefabs (mob camps, roaming mobs, critters; <see cref="SpawnManager"/>).
/// </summary>
public static class MapSpawner
{
    /// <summary>
    /// The buildings content/spawns adds to <paramref name="cluster"/> (<see cref="Content.StationSpawns"/>): each a buildings.xml
    /// building placed by its own tile (the tile named like it), with a Guid made from the cluster, the entry and the name
    /// (stable over restarts) and the key &lt;cluster&gt;:content:&lt;n&gt;. An unknown building is warned about and left out.
    /// </summary>
    public static IEnumerable<BuildingSpawn> AddedBuildings(string cluster, World world)
    {
        var entries = world.Stations.For(cluster);
        if (entries.Count == 0 || world.Clusters is not { } store) yield break;
        for (var i = 0; i < entries.Count; i++)
        {
            var e = entries[i];
            if (store.Buildings.Find(e.Building) is not { } template)
            {
                Log.Warn("world", $"content/spawns/{cluster}.json: unknown building {e.Building} (closest: "
                    + $"{store.Buildings.All.MinBy(b => ItemStore.Edits(b.Name.ToUpperInvariant(), e.Building.ToUpperInvariant()))?.Name}), left out");
                continue;
            }
            var guid = new Guid(System.Security.Cryptography.MD5.HashData(System.Text.Encoding.UTF8.GetBytes($"albionlocal:{cluster}:content:{i}:{template.Name}")));
            yield return new BuildingSpawn
            {
                Key = new SpawnKey(cluster, "content", i), Tile = template.Name, Position = e.Position, Direction = e.Direction,
                Component = new System.Xml.Linq.XElement("building", new System.Xml.Linq.XAttribute("name", template.Name)), Guid = guid, Template = template,
            };
        }
    }

    /// <summary>|d| per axis below which the client binds an object to its landscape tile (cjb.a 0x180aa1b10).</summary>
    public const float BindTolerance = 0.001f;

    /// <summary>
    /// Makes the objects of <paramref name="template"/> and puts them on <paramref name="map"/> (<see cref="Map.Populate"/>);
    /// logs one line with the counts and one warning per kind of object left out. Under <see cref="World.Sync"/>.
    /// </summary>
    public static void Populate(Map map, ClusterTemplate template, World world)
    {
        var now = DateTime.UtcNow.Ticks;
        if (world.MapSeed is { } seed) map.Random = new Random(seed);
        var objects = new List<WorldObject>();
        var skipped = new Dictionary<string, int>(StringComparer.Ordinal);
        void Skip(string why) => skipped[why] = skipped.GetValueOrDefault(why) + 1;
        long Next() => world.ObjectIds.Next();

        // NewBuilding: key 2 resolves and key 3 is a known tile by construction (the template's actionbuilding tile is a
        // buildings.xml uniquename, BuildingSpawn); the stamps are the map's creation time (keys 8, 20, 22).
        foreach (var b in template.Buildings) objects.Add(new Building(Next(), b, now, now));
        // Stage 6c: the buildings content/spawns adds (ownerless crafting stations), the same NewBuilding without owner keys.
        foreach (var b in AddedBuildings(map.Cluster, world)) objects.Add(new Building(Next(), b, now, now));
        // NewExit: key 1 must be the world.xml exit Guid ddt.al1 registered, and the position the tile's.
        foreach (var e in template.Exits)
        {
            if (e.World is not { } exit) Skip("exit without its world.xml exit");
            else if (!Binds(e.Position, exit.Position)) Skip("exit not at its world.xml position");
            else objects.Add(new Exit(Next(), e, exit));
        }
        foreach (var t in template.Travelpoints) objects.Add(new Travelpoint(Next(), t));
        foreach (var p in template.FactionWarfarePortals)
        {
            if (CityFaction(p.Tile, template.Name) is { } faction) objects.Add(new FactionWarfarePortal(Next(), p, faction));
            else Skip("faction warfare portal of an unknown faction");
        }
        foreach (var a in template.IslandAccessPoints) objects.Add(new IslandAccessPoint(Next(), a));
        // NewExpeditionAgent: key 3 within expeditionagents.xml (else a null agent template).
        foreach (var a in template.ExpeditionAgents)
        {
            if (a.AgentId is { } id) objects.Add(new ExpeditionAgent(Next(), a, id));
            else Skip("expedition agent not in expeditionagents.xml");
        }
        foreach (var m in template.LocationMarkers) objects.Add(new LocationMarker(Next(), m));
        if (world.OpeningTutorial is not null && TutorialInstances.IsTutorialCluster(map.Cluster))
            foreach (var b in template.TutorialBlockers) objects.Add(new TutorialBlocker(Next(), b));
        if (world.OpeningTutorial is { } opening && TutorialInstances.IsTutorialCluster(map.Cluster))
            foreach (var npc in template.Critical.Where(m => m.Type == "questgiverobject" && m.Guid is not null))
                if ((string?)npc.Component.Attribute("reference") is { } reference
                    && world.Clusters?.QuestGiverTypes.IdOf(reference) is { } questType)
                {
                    // NewQuestGiverObject indexes questgiverobjects.bin; its information event indexes questgivertypes.bin.
                    var objectType = opening.Journey?.QuestGivers.FirstOrDefault(g => g.Cluster == map.Cluster && g.Guid == npc.Guid)?.Type
                        ?? (reference == "WOUNDEDCRAFTER" ? opening.CrafterType : (int?)null);
                    if (objectType is { } type) objects.Add(new TutorialQuestGiver(Next(), npc, type, questType));
                }
        // StageG: the random dungeon exits on the cluster's markers (SPEC §8), and a dungeon floor's level factors (SPEC §7), with stageH's
        // dungeon of the day and group scaling.
        objects.AddRange(world.Dungeons.Populate(map, template));
        world.Dungeons.FillFactors(map);
        // StageG: the loot chests of a dungeon floor's reward layers (lootchests.xml; SPEC §7).
        objects.AddRange(world.Dungeons.ChestsOf(map, template));
        // StageL roads (SPEC §5.3): the Roads of Avalon portals, every spot of a road, the linked ones of an open-world zone.
        objects.AddRange(world.Roads.Populate(map, template));
        // StageH expeditions (SPEC §2.7): an instance's exits, narrators and checkpoints (cluster-wide); its mobs spawn once.
        objects.AddRange(world.Expeditions.Populate(map, template));
        // StageL corrupted (SPEC §4.2): a Hell Den's entrance (637); an instance's shrine, exit portal and chests, its scripted spawns left out.
        objects.AddRange(world.Corrupted.Populate(map, template));
        // StageL hellgates (SPEC §5.3, §5.4): a Hell Den's shrines (400); an instance's REWARD chests, its guardian's points held back, its scaling.
        objects.AddRange(world.Hellgates.Populate(map, template));
        // StageN shrines (SPEC §3): the buff shrines of the active layers (396): a solo dungeon room's, the faction and invisibility shrines.
        objects.AddRange(world.Shrines.Populate(map, template));
        // StageP territories (SPEC §6 P1): a watchtower territory's monolith (109) and a castle territory's castle (112), unowned.
        objects.AddRange(world.Territories.Populate(map, template));
        // StageL mists (SPEC §6.4): a mists world cluster's city entrances (527); a zone's border exits, city entrance and open-world exit, its mob level and treasures.
        objects.AddRange(world.Mists.Populate(map, template));
        // StageH islands (SPEC §1.5): an island instance's island management, owned by the island's owner.
        objects.AddRange(world.Islands.Populate(map, template));
        // StageP hideouts (SPEC §2.5): the hideouts standing in a black zone (419); a hideout instance's management (420) and exit (421).
        objects.AddRange(world.Hideouts.Populate(map, template));
        // StageG: NewFishingZoneObject, one per fishingzone tile whose zone content/fishing.json knows, full charges (all of
        // them at their template position, as the 15 seen in 0204 and the 16 in Thetford; SPEC §7.1, §12.1).
        foreach (var f in template.FishingZones)
        {
            if (f.Zone is { } zone && world.Fishing.Zones.TryGetValue(zone, out var settings)) objects.Add(new FishingZone(Next(), f, settings));
            else Skip($"fishing zone '{f.Zone}' not in content/fishing.json");
        }
        // List 39: the type is a harvestables.xml one by construction (else bng.abo null, a throw); the client indexes the
        // type's tier array (bnh+0x28) with tier - 1 behind a bounds check that throws (cjb.a 0x180aa10d0) and skips a null
        // entry. That array goes by tier number, not by Tier element (FIBER and ORE have no T1 element, and their T2 is not
        // read as T3), so the tier must be one of the type's Tier elements, from 1 up (SPEC §3 says "1 to the number of
        // tiers", which for FIBER and ORE would refuse their T8).
        foreach (var h in template.Harvestables)
        {
            if (h.Tier.Tier >= 1 && h.Type.Tiers.ContainsKey(h.Tier.Tier)) objects.Add(new Harvestable(Next(), h));
            else Skip($"harvestable {h.Type.Name} with a tier it does not have ({h.Tier.Tier})");
        }
        // Stage 4b: the resource nodes the client places itself (ResourceLayout), after the static ones (the official server
        // allocates them after the statics too; the client does not care), with the same checks; full charges.
        foreach (var h in template.Resources?.Harvestables ?? [])
        {
            if (h.Tier.Tier >= 1 && h.Type.Tiers.ContainsKey(h.Tier.Tier)) objects.Add(new Harvestable(Next(), h));
            else Skip($"resource node {h.Type.Name} with a tier it does not have ({h.Tier.Tier})");
        }

        // Stage 5a: a mob at every spawn point of the template and of the resource layout's prefabs (SpawnManager), after the
        // static objects; stageL roads (SPEC §5.5): the road chests drawn first, whose level the camp points spawn at.
        world.WorldChests.Plan(map, template);
        objects.AddRange(SpawnManager.Start(map, template, world));
        // StageH bosses (SPEC §4.1, §6.1): the world bosses of the WORLDBOSS points and content/bosses.json that stand now.
        objects.AddRange(world.Bosses.Populate(map, template));
        // StageH bosses (SPEC §4.2-4.4): the open world's POI and camp chests, after the mobs that lock them.
        objects.AddRange(world.WorldChests.Populate(map, template));
        map.Populate(template, objects, world.Config.Visibility.CellSize, now);
        world.Dungeons.BossChecks(map); // stageH: a dungeon floor's boss chests with no boss to guard them unlock
        world.Corrupted.Filled(map); // stageL: a corrupted instance's mob fame for the points, its chests with no guard near unlock
        world.Hellgates.Filled(map); // stageL: a hellgate's minibosses (its CHEST's guards)
        ResourceNodes.Start(map, template, world.Clusters, world);
        var counts = objects.GroupBy(o => o.Kind).Select(g => $"{g.Count()} {g.Key}{(g.Count() == 1 ? "" : "s")}");
        var later = new (int Count, string What)[]
        {
            (template.RealEstates.Count, "real estates"),
            (template.Critical.Count(m => m.Type == "placeholderspot"), "hall-of-fame spots"), (template.Info.Outposts.Count, "outposts"),
        }.Where(l => l.Count > 0).Select(l => $"{l.Count} {l.What}").ToList();
        var enchanted = map.ResourceNodes.Count(n => n.RareState > 0);
        Log.Info("world", $"map {map.Cluster}: {objects.Count} world objects ({string.Join(", ", counts)})"
            + (enchanted > 0 ? $", {enchanted} resource nodes enchanted" : "")
            + (map.MobSlots.Count > 0 ? $"; {SpawnManager.Summary(map)}" : "")
            + (later.Count > 0 ? $"; not spawned yet: {string.Join(", ", later)}" : ""));
        foreach (var (why, n) in skipped) Log.Warn("world", $"map {map.Cluster}: {n} x {why}, left out (the client would not show it)");
    }

    /// <summary>Whether the client binds <paramref name="sent"/> to a tile at <paramref name="tile"/> (|d| &lt; 0.001 per axis, SPEC §1).</summary>
    public static bool Binds(Vector2 sent, Vector2 tile) => Math.Abs(sent.X - tile.X) < BindTolerance && Math.Abs(sent.Y - tile.Y) < BindTolerance;

    /// <summary>
    /// NewFactionWarfarePortal key 4, the city's FlaggingStatus (SPEC §4: Highland 1, Forest 2, Steppe 3, Mountain 4, Swamp
    /// 5, Caerleon 6): from the wagon tile's biome prefix (SWAMP_GREEN_CITY_FWARFARE_PORTAL_WAGON), CAERLEON in the tile
    /// name first, else the cluster file's biome; null = not a faction city.
    /// </summary>
    public static byte? CityFaction(string tile, ClusterName cluster)
    {
        if (tile.Contains("CAERLEON", StringComparison.OrdinalIgnoreCase)) return 6;
        var biome = tile.Split('_')[0].ToUpperInvariant() switch
        {
            "HIGHLAND" => "Highland", "FOREST" => "Forest", "STEPPE" => "Steppe", "MOUNTAIN" => "Mountain", "SWAMP" => "Swamp",
            _ => cluster.Biome,
        };
        return biome switch { "Highland" => 1, "Forest" => 2, "Steppe" => 3, "Mountain" => 4, "Swamp" => 5, _ => null };
    }
}
