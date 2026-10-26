using System.Globalization;
using System.Numerics;
using System.Text.RegularExpressions;
using System.Xml.Linq;

namespace AlbionLocal.Server.Data;

/// <summary>
/// One cluster resolved from the client data (step 11; "SPEC stage 2" = analysis/work/stage2/SPEC.md, here §2): the
/// world.xml entry, the .cluster file's origin and size, its template instances and, in world coordinates, every object
/// the server has to know of, one list per kind. The landscape itself the client builds from the same files before
/// Join; these are the objects it needs events for (buildings, exits, travel points, …; SPEC §1) and the spawn data of
/// the server's own (mob spawn points, resource nodes). Made by <see cref="ClusterStore"/>, which caches it; immutable,
/// so any thread may read it. Reference implementation: analysis/work/stage2/data/tools/clusterdump.py.
/// </summary>
public sealed class ClusterTemplate
{
    public required ClusterInfo Info { get; init; }

    public string Id => Info.Id;

    /// <summary>The fields of the .cluster file name (biome, tier, template folder).</summary>
    public required ClusterName Name { get; init; }

    /// <summary>
    /// The .cluster file's origin and size (0000: (-305, -305) and (600, 600); world.xml's when the file has none): the
    /// bounds and the corner of the visibility cell grid (SPEC §5).
    /// </summary>
    public required Vector2 Origin { get; init; }

    public required Vector2 Size { get; init; }

    public IReadOnlyList<TemplateInstance> Instances { get; init; } = [];

    /// <summary>
    /// The world.xml criticalObjects the members take their Guids from, by path: the cluster's own, else (PSG-0039#2,
    /// TNL-201: 222 clusters that share the .cluster file of another and have none of their own) those of the cluster the
    /// file is named after (<see cref="ClusterName.Id"/>), whose Guids their world.xml exits carry too.
    /// </summary>
    public IReadOnlyDictionary<string, CriticalObject> CriticalObjects { get; init; } = new Dictionary<string, CriticalObject>();

    /// <summary>
    /// Every active critical scene member (layerId empty or active, its criticalObjects entry not unused), whatever its
    /// type: the typed lists below are made from these, and the other types (placeholder spots, outposts, tunnel exits,
    /// castles, …) wait here for the stages that need them.
    /// </summary>
    public IReadOnlyList<CriticalMember> Critical { get; init; } = [];

    /// <summary>
    /// actionbuilding members whose tile is a buildings.xml uniquename (NewBuilding 45). The others are fortification
    /// pieces (walls, gates and decorations of castles and faction forts, all with a fortificationtileswitch component),
    /// whose building comes from the fortification types: they stay in <see cref="Critical"/> for the territories.
    /// </summary>
    public IReadOnlyList<BuildingSpawn> Buildings { get; init; } = [];

    /// <summary>exit members (NewExit 215).</summary>
    public IReadOnlyList<ExitSpawn> Exits { get; init; } = [];

    /// <summary>fasttravelpoint members with a fasttravelpoint component (NewTravelpoint 213).</summary>
    public IReadOnlyList<TravelpointSpawn> Travelpoints { get; init; } = [];

    /// <summary>Members with a factionwarfareportal component, the city's war wagon (fasttravelpoint_02 in Thetford; NewFactionWarfarePortal 662).</summary>
    public IReadOnlyList<FactionWarfarePortalSpawn> FactionWarfarePortals { get; init; } = [];

    public IReadOnlyList<IslandAccessPointSpawn> IslandAccessPoints { get; init; } = [];

    public IReadOnlyList<ExpeditionAgentSpawn> ExpeditionAgents { get; init; } = [];

    /// <summary>locationmarker tiles (NewLocationMarker 383).</summary>
    public IReadOnlyList<LocationMarkerSpawn> LocationMarkers { get; init; } = [];

    public IReadOnlyList<TutorialBlockerSpawn> TutorialBlockers { get; init; } = [];

    /// <summary>Static trees and rocks: tiles with a harvestable component of a known type and tier (list 39).</summary>
    public IReadOnlyList<HarvestableSpawn> Harvestables { get; init; } = [];

    public IReadOnlyList<MobSpawnPoint> MobSpawnPoints { get; init; } = [];

    /// <summary>Open-world resource node tiles (stage 4): where the client's placement may put a resource prefab (<see cref="Resources"/>).</summary>
    public IReadOnlyList<ResourceNodeSpawn> ResourceNodes { get; init; } = [];

    /// <summary>
    /// The open-world layout of the <see cref="LayoutSession"/> the cluster was resolved with (stage 4b): the rolled dynamic
    /// templates (already in <see cref="Instances"/> and every list here) and the resource nodes the client places, which
    /// the server spawns (<see cref="ResourceLayout.Harvestables"/>); null when resolved without a session (default
    /// templates, no resource nodes: --check-content).
    /// </summary>
    public ResourceLayout? Resources { get; init; }

    public IReadOnlyList<RealEstateSpawn> RealEstates { get; init; } = [];

    public IReadOnlyList<FishingZoneSpawn> FishingZones { get; init; } = [];

    /// <summary>Where a dead player comes back (server data only).</summary>
    public IReadOnlyList<RespawnPoint> RespawnPoints { get; init; } = [];

    /// <summary>
    /// StageG dungeons (SPEC §8.1): the RandomExitPositionMarker tiles of the resolved layout (the day's rolled dynamic templates
    /// included), where a random dungeon exit may stand, and the fixed exit tiles (Type FIXED).
    /// </summary>
    public IReadOnlyList<DungeonExitMarker> DungeonMarkers { get; init; } = [];

    /// <summary>
    /// StageG dungeons (SPEC §7 "Chests"): the lootchestspawnpoint tiles of the active layers (the REWARD layers of a dungeon
    /// floor), each bound to its lootchests.xml LootChestSpawnpoint (null when the file does not have it).
    /// </summary>
    public IReadOnlyList<LootChestSpawn> LootChests { get; init; } = [];

    /// <summary>StageH bosses (SPEC §4.4): the localquestarea tiles of the active layers (the rolled camps), each bound to its localquests.xml quest.</summary>
    public IReadOnlyList<LocalQuestAreaSpawn> LocalQuestAreas { get; init; } = [];

    /// <summary>
    /// StageH expeditions (SPEC §2.7, §4.2): the ExpeditionExit, ExpeditionNarrator(Finish) and ExpeditionCheckPoint tiles of an expedition
    /// template, in template order; empty on every other cluster.
    /// </summary>
    public IReadOnlyList<ExpeditionObjectSpawn> ExpeditionObjects { get; init; } = [];

    /// <summary>
    /// Stage I bots (bots SPEC §1.6): every placed tile with walk-blocking cells (the templates' tiles, compound children and critical
    /// members of the active layers, the placed resource prefabs and mob camps), what <see cref="WalkGrid.Build"/> rasterises. Cheap
    /// and always built; the grid itself only for the maps that get bots.
    /// </summary>
    public IReadOnlyList<PlacedCollider> Colliders { get; init; } = [];

    /// <summary>Stage I bots: the road graphs of the active layers in world x/z (bots-world SPEC §1.2: 1207 has 7, cities none).</summary>
    public IReadOnlyList<RoadLine> Roads { get; init; } = [];

    /// <summary>StageH islands (SPEC §0.2, §1.5): the islandmanagement tiles of an island template (one, at (185.5, 64.5)); empty elsewhere.</summary>
    public IReadOnlyList<IslandManagementSpawn> IslandManagements { get; init; } = [];

    /// <summary>StageP hideouts (SPEC §1.1): the hideoutmanagement tiles of a hideout template (HIDEOUT_MANAGEMENT, one); empty elsewhere.</summary>
    public IReadOnlyList<HideoutManagementSpawn> HideoutManagements { get; init; } = [];

    /// <summary>Islands phase 5 (stageN islands5 SPEC §1): the centres (world x/z) of the buildable 10 × 10 m ground cells of the active layers; empty off islands.</summary>
    public IReadOnlyList<Vector2> BuildCells { get; init; } = [];

    /// <summary>
    /// StageL corrupted (SPEC §1.2, §4.4): the InvaderSpawnpoint tiles (tiles.xml namedmarkerpoint InvaderSpawnpoint) of the active layers,
    /// where an invader comes in; empty on every cluster but the corrupted dungeons.
    /// </summary>
    public IReadOnlyList<NamedMarkerSpawn> InvaderSpawns { get; init; } = [];

    /// <summary>
    /// StageL hellgates (SPEC §2.4, §2.5): the hellgateshrine tiles (a Hell Den's SHARED_HELLGATE_ENTRANCE), and the named markers of a
    /// Hellgate_NN template (<see cref="HellgateData.Markers"/>: the objective chest, the exit portal, the chain exit); empty elsewhere.
    /// </summary>
    public IReadOnlyList<HellgateShrineSpawn> HellgateShrines { get; init; } = [];

    /// <summary>
    /// StageN shrines (SPEC §2): the &lt;shrine shrinetype&gt; tiles of the active layers whose buffshrines.xml definition this server runs
    /// (<see cref="BuffShrineData.Definition.Supported"/>): a solo dungeon room's GENERAL_SHRINE_COMBAT_BUFF, the faction shrines, the
    /// invisibility shrines of the cities and portal clusters.
    /// </summary>
    public IReadOnlyList<BuffShrineSpawn> BuffShrines { get; init; } = [];

    public IReadOnlyList<NamedMarkerSpawn> HellgateMarkers { get; init; } = [];

    /// <summary>
    /// StageL hellgates phase B (SPEC §2.4, §7): the HellgateFxManager tile's &lt;arearestrictionsettings&gt; of a Hellgate_NN template (the
    /// restricted area's centre, its radius and width at the start and the end); empty elsewhere.
    /// </summary>
    public IReadOnlyList<HellgateAreaSpawn> HellgateAreas { get; init; } = [];

    /// <summary>
    /// Stage N arena (analysis/work/stageN/arena/SPEC.md §3): the WarCamp_Arena tiles' &lt;warcamp&gt; spawn positions of an arena template, in
    /// file order (the first camp team 1's, the second team 2's); empty elsewhere.
    /// </summary>
    public IReadOnlyList<WarcampSpawn> Warcamps { get; init; } = [];

    /// <summary>StageP league-pvp (SPEC §3.2): an arena template's &lt;orb type&gt; tiles (ARENA-STANDARD: three ARENA_ORB of ORB_ARENA); empty elsewhere.</summary>
    public IReadOnlyList<OrbSpawn> Orbs { get; init; } = [];

    /// <summary>
    /// StageL mists phase B (SPEC §2.5): the spots of a mists template's active layers by <see cref="MistsSpot.Kind"/>: the named markers
    /// MistsSpawnpoint (a resource zone's and the dungeon's arrivals) and MistsSpecialExitSpawnpoint, the mistswispspawn tiles (wispspawnpos), the
    /// treasurespawnpoint tiles ("treasure:CHEST", "treasure:COFFER": the dungeon's treasures) and the camps' unrestrictedpvpzone circles; empty elsewhere.
    /// </summary>
    public IReadOnlyList<MistsSpot> MistsSpots { get; init; } = [];

    /// <summary>
    /// The &lt;spawnbuff&gt; buffs of the cluster's worldsettings.xml cluster type with their conditions (<see cref="WorldArchetypes.SpawnBuffsOf"/>;
    /// stageL hellgates phase B: HELL_2V2_ENTER, SPAWN_INVULNERABILITY_HELL, applied under Hellgates:SpawnBuffs); empty without.
    /// </summary>
    public IReadOnlyList<SpawnBuff> SpawnBuffs { get; init; } = [];

    /// <summary>The worldsettings.xml poilootchestspawnlimit of the cluster's type (stageH bosses SPEC §4.2); null = none.</summary>
    public int? PoiChestLimit { get; init; }

    /// <summary>What did not resolve (a template file, a building or harvestable type, a Guid, a world.xml entry), with counts.</summary>
    public IReadOnlyList<string> Problems { get; init; } = [];

    /// <summary>Time to resolve it (templates parsed now included, the shared indexes not).</summary>
    public TimeSpan LoadTime { get; init; }

    /// <summary>
    /// The cluster's danger type for the kill rewards (stage 5c, SPEC §5c.2): world.xml type → worldsettings.xml
    /// &lt;clustertype archetype&gt; → &lt;archetype clusterdangerbonustype&gt; (SAFEAREA → GREEN → "safe"); the key of
    /// gamedata.xml ClusterDangerBonus. Null when the archetype has none (cities): no bonus, factor 1.
    /// </summary>
    public string? DangerType { get; init; }

    /// <summary>The archetype's PvP rules (stageE pvp SPEC §1.1): who may attack whom, duels, the PvP knockdown; fail safe (<see cref="ClusterPvp.Unresolved"/>) when unread.</summary>
    public ClusterPvp Pvp { get; init; } = ClusterPvp.Unresolved;

    /// <summary>Whether the mob spawn points were bound to mobs.xml and randomspawnbehaviors.xml (stage 5a); false when those failed to load ("mobs not bound" in <see cref="Problems"/>).</summary>
    public bool MobsBound { get; init; }

    /// <summary>
    /// The spawn points the map will leave out (SPEC §5a.2), one line per kind, for <see cref="ClusterStore.Get"/> to log once
    /// per cluster: each mob name mobs.xml does not have with its point count, and the RANDOM points without a spawn
    /// behaviour block for the cluster. Empty when the binding failed as a whole (that is in <see cref="Problems"/>).
    /// </summary>
    public IEnumerable<string> MobBindingReport()
    {
        if (!MobsBound) yield break;
        var points = MobSpawnPoints.Concat(Resources?.MobSpawnPoints ?? []).ToList();
        foreach (var group in points.Where(p => p.MobName is { } n && n != MobSpawnPoint.Random && n != MobSpawnPoint.WorldBossName && p.Template is null)
                     .GroupBy(p => p.MobName!, StringComparer.Ordinal).OrderBy(g => g.Key, StringComparer.Ordinal))
            yield return $"mob {group.Key} is not in mobs.xml, {group.Count()} spawn point(s) left out";
        var noBlock = points.Count(p => p.MobName == MobSpawnPoint.Random && p.Block is null);
        if (noBlock > 0) yield return $"{noBlock} RANDOM spawn point(s) without a spawn behaviour block for the cluster, left out";
    }

    /// <summary>The typed objects of every list.</summary>
    public IEnumerable<ClusterObject> Objects =>
        Enumerable.Empty<ClusterObject>().Concat(Buildings).Concat(Exits).Concat(Travelpoints).Concat(FactionWarfarePortals)
            .Concat(IslandAccessPoints).Concat(ExpeditionAgents).Concat(LocationMarkers).Concat(Harvestables).Concat(MobSpawnPoints)
            .Concat(ResourceNodes).Concat(RealEstates).Concat(FishingZones).Concat(RespawnPoints);

    /// <summary>"5 buildings, 9 exits, …" (the non-empty lists).</summary>
    public string Summary()
    {
        var parts = new (int Count, string What)[]
        {
            (Buildings.Count, "building"), (Exits.Count, "exit"), (Travelpoints.Count, "travel point"),
            (FactionWarfarePortals.Count, "faction warfare portal"), (IslandAccessPoints.Count, "island access point"),
            (ExpeditionAgents.Count, "expedition agent"), (LocationMarkers.Count, "location marker"), (Harvestables.Count, "harvestable"),
            (MobSpawnPoints.Count, "mob spawn point"), (ResourceNodes.Count, "resource node tile"), (RealEstates.Count, "real estate"),
            (FishingZones.Count, "fishing zone"), (RespawnPoints.Count, "respawn point"), (LootChests.Count, "loot chest point"),
        };
        var text = string.Join(", ", parts.Where(p => p.Count > 0).Select(p => $"{p.Count} {p.What}{(p.Count == 1 ? "" : "s")}"));
        return text.Length == 0 ? "no objects" : text;
    }

    /// <summary>
    /// Resolves <paramref name="info"/>: reads its .cluster file, then places each template instance's active critical
    /// members and component tiles (<see cref="TemplateLayout"/>) with the instance's position and rotation. With a
    /// <paramref name="session"/> (stage 4b) the dyn slots first get the dynamic templates the client rolls for that date
    /// (<see cref="DynamicTemplates.Roll"/>, feature 112 on): such a slot loads the rolled template instead of its default,
    /// with the layers its tags switch on (<see cref="DynamicRoll.LayerOn"/>), and everything of the cluster comes from it;
    /// then the resource nodes are placed (<see cref="Place"/>). A layer is
    /// active when the instance lists it in an activelayer without flags, or with flags that
    /// <see cref="ClusterOptions.EventLayers"/> enables (SPEC §2 "Layers"; 0000: only Layer_01, so the bank and market
    /// buildings of Layer_02 are not in the city but in 0006 and 0007). An activelayer with activeDuringEvent="false" is
    /// the opposite: on while its event is off (the hall-of-fame statues of Layer_09, anniversary_prelude, in the 46
    /// HALL clusters; world.xml lists their placeholder spots as critical objects). Missing pieces become
    /// <see cref="Problems"/>; a missing or broken .cluster file throws <see cref="InvalidDataException"/>. <paramref name="tier"/> (stageL
    /// corrupted, a <see cref="ClusterVariant"/>): the tier the mobs bind at and <see cref="Name"/> carries instead of the file name's.
    /// </summary>
    internal static ClusterTemplate Resolve(ClusterStore store, ClusterInfo info, LayoutSession? session, int? tier = null)
    {
        var watch = System.Diagnostics.Stopwatch.StartNew();
        var name = ClusterName.Parse(info.File);
        var root = GameDataFile.Open(store.Folder, Path.Combine("cluster", Path.ChangeExtension(info.File, ".bin"))).Root();
        var criticalObjects = info.Critical.Count > 0 ? info.Critical : store.World.CriticalOf(name.Id) ?? info.Critical;
        if (tier is { } own) name = name with { Tier = own };
        var b = new Builder(store, info, criticalObjects) { Tier = tier };
        var instances = new List<TemplateInstance>();

        // Stage 4b: the dynamic-template roll of the session's date (bal.a), before any template is placed.
        var cni = session is null ? null : ClusterNameInfo.Parse(info.File);
        var flags = session?.LowerFlags;
        var (pvpWarning, pvpRules) = (0, 2);
        IReadOnlyDictionary<string, DynamicRoll> rolls = new Dictionary<string, DynamicRoll>();
        var dynSeed = "";
        string? layoutError = null; // a layout file missing or broken: the cluster still resolves, with default templates and no nodes
        if (cni is not null)
        {
            try
            {
                (_, pvpWarning, pvpRules) = store.Archetypes.Of(info.Type);
                if (session is { DynamicTemplates: true }) rolls = store.Dynamic.Roll(cni, pvpWarning, flags, session.Key57, out dynSeed);
            }
            catch (Exception e) when (e is InvalidDataException or IOException)
            {
                layoutError = e.Message;
                b.Problem($"dynamic templates not rolled: {e.Message}");
            }
        }
        // The node sources of the placement (a81.h): per slot (first of an id) the template, its place and its layers as
        // the resourcenode walk counts them (an activelayer with flags only for a flag of Login key 79).
        var sources = new List<(string Slot, TemplateLayout Layout, Placement Place, HashSet<string> Layers)>();

        foreach (var ti in root.Elements("templateinstance"))
        {
            var slot = (string?)ti.Attribute("id") ?? "";
            var reference = (string?)ti.Attribute("ref") ?? "";
            var (px, py, pz) = TileMath.V3((string?)ti.Attribute("pos"));
            var place = new Placement(px, py, pz, TileMath.Float((string?)ti.Attribute("rot")));
            var active = new HashSet<string>(StringComparer.Ordinal);
            var nodeLayers = new HashSet<string>(StringComparer.Ordinal);
            foreach (var al in ti.Elements("activelayer"))
            {
                if ((string?)al.Attribute("id") is not { } layer) continue;
                var layerFlags = ((string?)al.Attribute("flags") ?? "").Split([' ', ','], StringSplitOptions.RemoveEmptyEntries);
                if (layerFlags.Length == 0 || layerFlags.Any(store.Options.EventLayers.Contains) != ((string?)al.Attribute("activeDuringEvent") == "false"))
                    active.Add(layer);
                var nodeFlags = ((string?)al.Attribute("flags") ?? "").ToLowerInvariant().Split([',', ';', ' ', '|'], StringSplitOptions.RemoveEmptyEntries);
                if (nodeFlags.Length == 0 || flags is not null && nodeFlags.Any(flags.Contains)) nodeLayers.Add(layer);
            }
            var roll = rolls.GetValueOrDefault(slot);
            var layout = roll is null ? store.Layout(name.TemplateFolder, reference) : DynamicLayout(store, name.TemplateFolder, roll.Template);
            // A rolled slot has no activelayer entries: its template's layers are on by the roll's tags.
            if (roll is not null && layout is not null)
                active = layout.LayerNames.Where(l => roll.LayerOn(l.Value)).Select(l => l.Key).ToHashSet(StringComparer.Ordinal);
            instances.Add(new TemplateInstance(slot, roll?.Template ?? reference, layout?.Folder, new Vector2((float)px, (float)pz), (float)py, (float)place.Yaw,
                (string?)ti.Attribute("dyn") == "true", active) { Default = reference, Roll = roll });
            if (layout is null)
            {
                b.Problem($"template {roll?.Template ?? reference} not in templates/{name.TemplateFolder}");
                continue;
            }
            // a81.kj, the dynamic templates' handler, has no resourcenode branch: a rolled slot gives no node tiles.
            if (roll is null && sources.All(x => x.Slot != slot)) sources.Add((slot, layout, place, nodeLayers));
            b.Place(slot, layout, place, active, guidless: roll is not null); // a dynamic template's members have no criticalObjects Guids
        }

        var origin = TileMath.V2((string?)root.Attribute("origin")) ?? info.Origin ?? Vector2.Zero;
        var size = TileMath.V2((string?)root.Attribute("size")) ?? info.Size ?? Vector2.Zero;
        ResourceLayout? resources = null;
        if (session is not null)
        {
            try
            {
                resources = cni is null ? new ResourceLayout { Session = session, Invalid = $"the cluster name {info.File} does not parse" }
                    : layoutError is not null ? new ResourceLayout { Session = session, Invalid = layoutError }
                    : Place(store, info, cni, pvpRules, session, sources, b.Problem) with { Rolls = rolls, DynamicSeedText = dynSeed };
            }
            catch (Exception e) when (e is InvalidDataException or IOException)
            {
                b.Problem($"resource placement: {e.Message}");
                resources = new ResourceLayout { Session = session, Invalid = e.Message, Rolls = rolls, DynamicSeedText = dynSeed };
            }
        }
        if (resources is { Placements.Count: > 0 }) PrefabColliders(store.Tiles, resources.Placements, b.Colliders);
        return b.Finish(name, origin, size, instances, resources, watch);
    }

    /// <summary>
    /// Stage I bots (bots-world SPEC §1.1, verification): the walk-blocking cells of the placed resource prefabs, mob camps and roaming
    /// mobs (a rock blocks up to 2.1 m around its node, a tree or ore 0.7 m), placed as <see cref="Place"/> places their harvestables: a
    /// plain tile at its snapped position turned by its fixed rotation (a9u.q, 90° steps), a compound's layer-0 children about its
    /// whole-metre position by the node's direction.
    /// </summary>
    private static void PrefabColliders(TileIndex tiles, IReadOnlyList<PlacedPrefab> placements, List<PlacedCollider> into)
    {
        foreach (var p in placements)
        {
            if (!p.Compound)
            {
                if (tiles.Collision(p.Name) is { } grid)
                    into.Add(new PlacedCollider(grid, p.Position.X, p.Position.Y, ResourcePlacement.RotationIndex(p.NodeDirection) * 90));
                continue;
            }
            foreach (var child in tiles.CompoundColliders(p.Name))
            {
                if (child.Layer is not null) continue;
                var (dx, dz) = TileMath.Rotate(child.X, child.Z, p.NodeDirection);
                into.Add(new PlacedCollider(tiles.Collision(child.Name)!, p.Position.X + dx, p.Position.Y + dz, TileMath.Mod360(p.NodeDirection + child.Yaw)));
            }
        }
    }

    /// <summary>
    /// A random dungeon floor resolved from its <paramref name="layout"/> (stageG dungeons SPEC §6.1) instead of a .cluster file:
    /// the a9r list as the template instances (no world.xml, no criticalObjects), every template from its folder with the
    /// layers the layout switches on, and the exits numbered in load order as the client does (a81.kl, SPEC §2.6): the first
    /// one Guid …0001 to the layout's up target, every later one …0002 to its down target, each a synthetic
    /// <see cref="WorldExit"/> with the target exit Guid (ad / ag). Its <see cref="Info"/> carries the instance id, the cluster
    /// name (a80.b) and the dungeon clustertype (a9p.x, for the PvP and danger rules); the mobs bind at the dungeon's tier.
    /// Missing templates become <see cref="Problems"/>. Reads template files: never under the world lock.
    /// </summary>
    internal static ClusterTemplate FromLayout(ClusterStore store, A80Layout layout)
    {
        var watch = System.Diagnostics.Stopwatch.StartNew();
        var info = new ClusterInfo { Id = layout.Id, File = layout.Name, Type = layout.ClusterType, DisplayName = layout.DisplayName, Origin = layout.Origin, Size = layout.Size };
        var name = ClusterName.Parse(info.File);
        // A random dungeon floor's two exits lead up and down; a mists zone (stageL mists SPEC §6.3) has no exit members, its ways out are its own objects.
        var b = new Builder(store, info, info.Critical)
        {
            DungeonExit = layout is not DungeonLayout floor ? null : (n, pos) => n == 1
                ? (DungeonExits.Up, new WorldExit(DungeonExits.Up, pos, floor.UpExit, floor.UpCluster, "", new Dictionary<string, string>()))
                : (DungeonExits.Down, new WorldExit(DungeonExits.Down, pos, floor.DownExit, floor.DownCluster, "", new Dictionary<string, string>())),
        };
        var instances = new List<TemplateInstance>();
        foreach (var t in layout.Templates)
        {
            var active = t.Layers.ToHashSet(StringComparer.Ordinal);
            var tl = store.Layout(t.Folder.Length > 0 ? t.Folder : name.TemplateFolder, t.Template);
            instances.Add(new TemplateInstance(t.Slot, t.Template, tl?.Folder, t.Position, t.Y, t.Rotation, false, active));
            if (tl is null)
            {
                b.Problem($"template {t.Template} not in templates/{t.Folder}");
                continue;
            }
            b.Place(t.Slot, tl, new Placement(t.Position.X, t.Y, t.Position.Y, t.Rotation), active, guidless: true);
        }
        return b.Finish(name, layout.Origin, layout.Size, instances, null, watch);
    }

    /// <summary>
    /// What <see cref="Resolve"/> and <see cref="FromLayout"/> share: the active critical members and component tiles of each
    /// placed template (<see cref="TemplateLayout"/>) turned into the typed lists in world coordinates, the problems with
    /// counts, and the end (<see cref="Finish"/>: the mobs bound, the danger type, the PvP rules, loot.xml read).
    /// </summary>
    private sealed class Builder(ClusterStore store, ClusterInfo info, IReadOnlyDictionary<string, CriticalObject> criticalObjects)
    {
        private readonly Dictionary<string, int> _problems = new(StringComparer.Ordinal);
        public readonly List<CriticalMember> Critical = [];
        public readonly List<BuildingSpawn> Buildings = [];
        public readonly List<ExitSpawn> Exits = [];
        public readonly List<TravelpointSpawn> Travelpoints = [];
        public readonly List<FactionWarfarePortalSpawn> Portals = [];
        public readonly List<IslandAccessPointSpawn> Islands = [];
        public readonly List<ExpeditionAgentSpawn> Agents = [];
        public readonly List<LocationMarkerSpawn> Markers = [];
        public readonly List<TutorialBlockerSpawn> TutorialBlockers = [];
        public readonly List<HarvestableSpawn> Harvestables = [];
        public List<MobSpawnPoint> Mobs = [];
        public readonly List<ResourceNodeSpawn> Nodes = [];
        public readonly List<RealEstateSpawn> Estates = [];
        public readonly List<FishingZoneSpawn> Fishing = [];
        public readonly List<RespawnPoint> Respawns = [];
        public readonly List<DungeonExitMarker> DungeonMarkers = [];
        public List<LootChestSpawn> ChestPoints = [];
        public List<LocalQuestAreaSpawn> QuestAreas = [];
        public readonly List<ExpeditionObjectSpawn> ExpeditionObjects = [];
        public readonly List<PlacedCollider> Colliders = [];
        public readonly List<RoadLine> Roads = [];
        public readonly List<IslandManagementSpawn> IslandManagements = [];
        public readonly List<HideoutManagementSpawn> HideoutManagements = [];
        public readonly List<Vector2> BuildCells = [];
        public readonly List<NamedMarkerSpawn> InvaderSpawns = [];
        public readonly List<HellgateShrineSpawn> HellgateShrines = [];
        public List<BuffShrineSpawn> BuffShrines = [];
        public readonly List<NamedMarkerSpawn> HellgateMarkers = [];
        public readonly List<HellgateAreaSpawn> HellgateAreas = [];
        public readonly List<WarcampSpawn> Warcamps = [];
        public readonly List<OrbSpawn> Orbs = [];
        public readonly List<MistsSpot> MistsSpots = [];

        /// <summary>A dungeon floor's exits (<see cref="FromLayout"/>): the Guid and world exit of the n-th exit member (1-based, load order) at its position.</summary>
        public Func<int, Vector2, (Guid Guid, WorldExit World)>? DungeonExit { get; init; }

        /// <summary>StageL corrupted (a <see cref="ClusterVariant"/>): the tier the mobs bind at instead of the file name's; null = the file's.</summary>
        public int? Tier { get; init; }

        private int _exitMembers;

        public void Problem(string text) => _problems[text] = _problems.GetValueOrDefault(text) + 1;

        /// <summary>
        /// The members and tiles of <paramref name="layout"/> placed at <paramref name="place"/> with the layers <paramref name="active"/>.
        /// <paramref name="guidless"/>: the template's members have no criticalObjects Guids (a rolled dynamic template, a dungeon floor), not a problem.
        /// </summary>
        public void Place(string slot, TemplateLayout layout, Placement place, IReadOnlySet<string> active, bool guidless)
        {
            foreach (var m in layout.Members)
            {
                if (m.Layer.Length > 0 && !active.Contains(m.Layer)) continue;
                if (store.Tiles.Collision(m.Tile) is { } memberGrid) // stage I bots: a building's walls block, its criticalObjects entry or not
                {
                    var (mx, _, mz) = place.Apply(m.X, m.Y, m.Z);
                    Colliders.Add(new PlacedCollider(memberGrid, mx, mz, TileMath.Mod360(place.Yaw + m.Yaw)));
                }
                var path = $"{slot}/{m.Id}";
                var entry = criticalObjects.GetValueOrDefault(path);
                if (entry is { Unused: true }) continue; // SPEC §2 "Guid": skip unused="true"
                if (entry is null && !guidless) Problem($"{m.Type} member without a criticalObjects Guid");
                var guid = entry?.Guid ?? Guid.Empty;
                var (x, y, z) = place.Apply(m.X, m.Y, m.Z);
                var key = new SpawnKey(info.Id, slot, m.Index);
                var pos = new Vector2((float)x, (float)z);
                var dir = (float)TileMath.Mod360(place.Yaw + m.Yaw);
                XElement? Of(string tag) => m.Components.FirstOrDefault(c => c.Name.LocalName == tag);
                var main = Of(m.Type) ?? m.Components.FirstOrDefault() ?? new XElement("none");
                Critical.Add(new CriticalMember
                {
                    Key = key, Tile = m.Tile, Position = pos, Height = (float)y, Direction = dir, Component = main,
                    Type = m.Type, Member = m.Id, Path = path, Guid = entry?.Guid, Layer = m.Layer, Components = m.Components,
                });
                // spawnpos: relative to the tile, turned with it (clusterdump.py spawnpos_world).
                Vector2? Spawn(XElement c)
                {
                    if (TileMath.V2((string?)c.Attribute("spawnpos")) is not { } sp) return null;
                    var (rx, rz) = TileMath.Rotate(sp.X, sp.Y, m.Yaw);
                    var (wx, _, wz) = place.Apply(m.X + rx, m.Y, m.Z + rz);
                    return new Vector2((float)wx, (float)wz);
                }
                switch (m.Type)
                {
                    case "actionbuilding" when Of("fortificationtileswitch") is null:
                        if (store.Buildings.Find(m.Tile) is not { } building)
                        {
                            Problem($"action building {m.Tile} not in buildings.xml");
                            break;
                        }
                        // The faction trader: its Guid is a world.xml questgiver id (SPEC §3 NewBuilding key 1).
                        var giver = info.QuestGivers.FirstOrDefault(q => q.Id == guid && guid != Guid.Empty);
                        var giverType = giver is null ? null : store.QuestGiverTypes.IdOf(giver.Type);
                        if (giver is not null && giverType is null) Problem($"quest giver type {giver.Type} not in questgivertypes.xml");
                        Buildings.Add(new BuildingSpawn
                        {
                            Key = key, Tile = m.Tile, Position = pos, Height = (float)y, Direction = dir, Component = main, Guid = guid, Template = building,
                            QuestGiver = giver, QuestGiverType = giverType,
                        });
                        break;
                    case "exit" when Of("exit") is { } exit:
                        WorldExit? world;
                        if (DungeonExit is { } numbered) (guid, world) = numbered(++_exitMembers, pos); // a81.kl: …0001, then …0002
                        else
                        {
                            world = info.Exits.FirstOrDefault(e => e.Id == guid);
                            if (world is null) Problem("exit member not in world.xml exits");
                        }
                        Exits.Add(new ExitSpawn
                        {
                            Key = key, Tile = m.Tile, Position = pos, Height = (float)y, Direction = dir, Component = exit, Guid = guid, World = world, SpawnPosition = Spawn(exit),
                            Collision = store.Tiles.Collision(m.Tile),
                        });
                        break;
                    case "fasttravelpoint" or "factionwarfareportal" when Of("factionwarfareportal") is { } portal:
                        Portals.Add(new FactionWarfarePortalSpawn { Key = key, Tile = m.Tile, Position = pos, Height = (float)y, Direction = dir, Component = portal, Guid = guid, SpawnPosition = Spawn(portal) });
                        break;
                    case "fasttravelpoint" when Of("fasttravelpoint") is { } travel:
                        Travelpoints.Add(new TravelpointSpawn
                        {
                            Key = key, Tile = m.Tile, Position = pos, Height = (float)y, Direction = dir, Component = travel, Guid = guid,
                            World = info.Travelpoints.FirstOrDefault(t => t.Id == guid), SpawnPosition = Spawn(travel),
                        });
                        break;
                    case "islandaccesspoint":
                        Islands.Add(new IslandAccessPointSpawn
                        {
                            Key = key, Tile = m.Tile, Position = pos, Height = (float)y, Direction = dir, Component = main, Guid = guid,
                            World = info.IslandAccessPoints.FirstOrDefault(a => a.Id == guid),
                        });
                        break;
                    case "expeditionagent":
                        var agentType = (string?)main.Attribute("agenttype") ?? "";
                        var agentId = store.ExpeditionAgents.IdOf(agentType);
                        if (agentId is null) Problem($"expedition agent {agentType} not in expeditionagents.xml");
                        Agents.Add(new ExpeditionAgentSpawn
                        {
                            Key = key, Tile = m.Tile, Position = pos, Height = (float)y, Direction = dir, Component = main, Guid = guid, AgentType = agentType, AgentId = agentId,
                        });
                        break;
                    case "realestate":
                        Estates.Add(new RealEstateSpawn
                        {
                            Key = key, Tile = m.Tile, Position = pos, Height = (float)y, Direction = dir, Component = main, Guid = guid,
                            Size = TileMath.V2((string?)main.Attribute("size")), Quality = (string?)main.Attribute("locationquality"),
                        });
                        break;
                    case "respawnpoint":
                        Respawns.Add(new RespawnPoint { Key = key, Tile = m.Tile, Position = pos, Height = (float)y, Direction = dir, Component = main, Guid = guid, Type = (string?)main.Attribute("type") });
                        break;
                }
            }

            // Stage I bots (bots SPEC §1.6): the walk-blocking tiles and the roads, placed like the rest.
            foreach (var c in layout.Colliders)
            {
                if (c.Layer is not null && !active.Contains(c.Layer)) continue;
                var (cx, _, cz) = place.Apply(c.X, 0, c.Z);
                Colliders.Add(new PlacedCollider(c.Grid, cx, cz, TileMath.Mod360(place.Yaw + c.Yaw)));
            }
            foreach (var cell in layout.BuildCells)
            {
                if (cell.Layer is not null && !active.Contains(cell.Layer)) continue;
                var (bx, _, bz) = place.Apply(cell.X, 0, cell.Z);
                BuildCells.Add(new Vector2((float)bx, (float)bz));
            }
            foreach (var r in layout.Roads)
            {
                if (r.Layer is not null && !active.Contains(r.Layer)) continue;
                Roads.Add(new RoadLine([.. r.Nodes.Select(n => place.Apply(n.X, 0, n.Z)).Select(w => new Vector2((float)w.X, (float)w.Z))], r.Links));
            }

            foreach (var t in layout.Tiles)
            {
                if (t.Layer is not null && !active.Contains(t.Layer)) continue;
                var (x, y, z) = place.Apply(t.X, t.Y, t.Z);
                var key = new SpawnKey(info.Id, slot, t.Index);
                var pos = new Vector2((float)x, (float)z);
                var dir = (float)TileMath.Mod360(place.Yaw + t.Yaw);
                var c = t.Component;
                string? A(string attribute) => (string?)c.Attribute(attribute);
                switch (c.Name.LocalName)
                {
                    case "harvestable":
                        var type = store.Harvestables.Find(A("type") ?? "");
                        var tierNumber = TileMath.Int(A("tier"));
                        if (type is null) Problem($"harvestable type {A("type")} not in harvestables.xml");
                        else if (!type.Tiers.TryGetValue(tierNumber, out var tier)) Problem($"harvestable {type.Name} has no tier {A("tier")}");
                        else Harvestables.Add(new HarvestableSpawn { Key = key, Tile = t.Tile, Position = pos, Height = (float)y, Direction = dir, Component = c, Type = type, Tier = tier });
                        break;
                    case "mobspawnpoint":
                        Mobs.Add(MobSpawnPoint.Of(c, key, t.Tile, pos, (float)y, dir, (wx, wz) =>
                        {
                            var (px2, _, pz2) = place.Apply(wx, t.Y, wz);
                            return new Vector2((float)px2, (float)pz2);
                        }));
                        break;
                    case "resourcenode":
                        Nodes.Add(new ResourceNodeSpawn { Key = key, Tile = t.Tile, Position = pos, Height = (float)y, Direction = dir, Component = c, ResourceType = A("resourcetype") ?? "", NodeType = A("nodetype") ?? "" });
                        break;
                    case "locationmarker":
                        Markers.Add(new LocationMarkerSpawn { Key = key, Tile = t.Tile, Position = pos, Height = (float)y, Direction = dir, Component = c, LocationId = A("locationid") ?? "", Radius = TileMath.Float(A("radius")) });
                        break;
                    case "tutorialblocker":
                        TutorialBlockers.Add(new TutorialBlockerSpawn { Key = key, Tile = t.Tile, Position = pos, Height = (float)y, Direction = dir, Component = c, BlockerId = A("id") ?? "" });
                        break;
                    case "fishingzone":
                        Fishing.Add(new FishingZoneSpawn { Key = key, Tile = t.Tile, Position = pos, Height = (float)y, Direction = dir, Component = c, Zone = A("zone"), Radius = TileMath.Float(A("radius")) });
                        break;
                    // StageG dungeons (SPEC §8.1): where a random dungeon exit may stand (its Type: SOLO, VETERAN, …), and the fixed exits.
                    case "randomdungeonrandomexitpositionmarker":
                        DungeonMarkers.Add(new DungeonExitMarker { Key = key, Tile = t.Tile, Position = pos, Height = (float)y, Direction = dir, Component = c, Type = (A("Type") ?? "SOLO").ToUpperInvariant() });
                        break;
                    case "randomdungeonfixedexit":
                        DungeonMarkers.Add(new DungeonExitMarker { Key = key, Tile = t.Tile, Position = pos, Height = (float)y, Direction = dir, Component = c, Type = "FIXED", FixedExit = A("type") });
                        break;
                    // StageG dungeons (SPEC §7): a chest of a reward layer; stageH bosses (SPEC §4.2-4.4): the open world's POI, mob-upgrade
                    // and camp chest spots. Bound to its spawnpoint in Finish.
                    case "lootchestspawnpoint":
                        ChestPoints.Add(new LootChestSpawn { Key = key, Tile = t.Tile, Position = pos, Height = (float)y, Direction = dir, Component = c, SpawnBehaviour = A("spawnbehaviour") ?? "" });
                        break;
                    // StageH expeditions (SPEC §2.7): the instance's exits, narrators and checkpoints (sent cluster-wide by Systems/Expeditions).
                    case "expeditionexit" or "expeditionnarrator" or "expeditioncheckpoint":
                        ExpeditionObjects.Add(new ExpeditionObjectSpawn
                        {
                            Key = key, Tile = t.Tile, Position = pos, Height = (float)y, Direction = dir, Component = c, Kind = c.Name.LocalName,
                            SpawnAtStartup = A("spawnatstartup") == "true", ColliderDistance = TileMath.Float(A("maxcolliderdist")),
                        });
                        break;
                    // StageH islands (SPEC §1.5): the island management spot (NewIslandManagement on an island instance).
                    case "islandmanagement":
                        IslandManagements.Add(new IslandManagementSpawn { Key = key, Tile = t.Tile, Position = pos, Height = (float)y, Direction = dir, Component = c });
                        break;
                    // StageP hideouts: the hideout management (NewHideoutManagement on a hideout instance).
                    case "hideoutmanagement":
                        HideoutManagements.Add(new HideoutManagementSpawn { Key = key, Tile = t.Tile, Position = pos, Height = (float)y, Direction = dir, Component = c });
                        break;
                    // StageL corrupted (SPEC §4.4): where an invader comes in (the other named marker points are not used).
                    case "namedmarkerpoint" when A("name") == CorruptedData.InvaderSpawnTile:
                        InvaderSpawns.Add(new NamedMarkerSpawn { Key = key, Tile = t.Tile, Position = pos, Height = (float)y, Direction = dir, Component = c });
                        break;
                    // StageL hellgates (SPEC §2.4): a Hellgate_NN template's objective chest, exit portal and chain exit markers.
                    case "namedmarkerpoint" when A("name") is { } marker && HellgateData.Markers.Contains(marker):
                        HellgateMarkers.Add(new NamedMarkerSpawn { Key = key, Tile = t.Tile, Position = pos, Height = (float)y, Direction = dir, Component = c, Name = marker });
                        break;
                    // StageL mists phase B (SPEC §2.5): the arrivals of a resource zone and of Knightfall Abbey, the special exits' spots, the wisp
                    // spawns, the dungeon's treasure spots and the camps' free-for-all circles.
                    case "namedmarkerpoint" when A("name") is "MistsSpawnpoint" or "MistsSpecialExitSpawnpoint":
                        MistsSpots.Add(new MistsSpot { Key = key, Tile = t.Tile, Position = pos, Height = (float)y, Direction = dir, Component = c, Kind = A("name")! });
                        break;
                    case "mistswispspawn" or "unrestrictedpvpzone":
                        MistsSpots.Add(new MistsSpot { Key = key, Tile = t.Tile, Position = pos, Height = (float)y, Direction = dir, Component = c, Kind = c.Name.LocalName });
                        break;
                    case "treasurespawnpoint":
                        MistsSpots.Add(new MistsSpot { Key = key, Tile = t.Tile, Position = pos, Height = (float)y, Direction = dir, Component = c, Kind = "treasure:" + (A("subtype") ?? "").ToUpperInvariant() });
                        break;
                    // StageL hellgates (SPEC §2.5, §3.1): a Hell Den's shrine (NewHellgateShrine 400 with this tile's name and position).
                    case "hellgateshrine":
                        HellgateShrines.Add(new HellgateShrineSpawn { Key = key, Tile = t.Tile, Position = pos, Height = (float)y, Direction = dir, Component = c });
                        break;
                    // StageN shrines (SPEC §2): a buff shrine (NewShrine 396), bound to its buffshrines.xml definition in Finish.
                    case "shrine":
                        BuffShrines.Add(new BuffShrineSpawn { Key = key, Tile = t.Tile, Position = pos, Height = (float)y, Direction = dir, Component = c, ShrineType = A("shrinetype") ?? "" });
                        break;
                    // StageL hellgates phase B (SPEC §2.4): the HellgateFxManager's restricted area; its centre attribute is in the template's frame.
                    // Stage N arena (SPEC §3): a team's camp and its spawn positions (template coordinates, placed as the tile).
                    case "warcamp":
                        var spawns = c.Descendants("position").Select(e => TileMath.V2((string?)e.Attribute("pos"))).OfType<Vector2>()
                            .Select(v => place.Apply(v.X, t.Y, v.Y)).Select(w => new Vector2((float)w.X, (float)w.Z)).ToList();
                        Warcamps.Add(new WarcampSpawn { Key = key, Tile = t.Tile, Position = pos, Height = (float)y, Direction = dir, Component = c, Spawns = spawns.Count > 0 ? spawns : [pos] });
                        break;
                    // StageP league-pvp (SPEC §3.2): an orb tile and its orbtypes.xml type.
                    case "orb":
                        Orbs.Add(new OrbSpawn { Key = key, Tile = t.Tile, Position = pos, Height = (float)y, Direction = dir, Component = c, OrbType = A("type") ?? "" });
                        break;
                    case "arearestrictionsettings":
                        var (acx, acz) = TileMath.V2((string?)c.Attribute("center")) is { } ac ? (ac.X, ac.Y) : ((float)t.X, (float)t.Z);
                        var (ax, _, az) = place.Apply(acx, t.Y, acz);
                        HellgateAreas.Add(new HellgateAreaSpawn
                        {
                            Key = key, Tile = t.Tile, Position = pos, Height = (float)y, Direction = dir, Component = c, Center = new Vector2((float)ax, (float)az),
                            StartRadius = TileMath.Float(A("startradius")), EndRadius = TileMath.Float(A("endradius")),
                            StartWidth = TileMath.Float(A("startwidth")), EndWidth = TileMath.Float(A("endwidth")),
                        });
                        break;
                    // StageH bosses (SPEC §4.4): a camp's quest circle (questid, radius), on the same rolled layer as its personal chest.
                    case "localquestarea":
                        QuestAreas.Add(new LocalQuestAreaSpawn { Key = key, Tile = t.Tile, Position = pos, Height = (float)y, Direction = dir, Component = c, QuestId = A("questid") ?? "", Radius = TileMath.Float(A("radius")) });
                        break;
                }
            }
        }

        /// <summary>
        /// The cluster from the lists: the mobs bound (stage 5, off the world thread; mobs.bin and randomspawnbehaviors.bin are read
        /// with the first cluster; without them the points stay unbound and the map spawns no mobs), the danger type and PvP rules
        /// of its archetype, and loot.xml read, so that a kill or a catch under the lock finds it loaded.
        /// </summary>
        public ClusterTemplate Finish(ClusterName name, Vector2 origin, Vector2 size, List<TemplateInstance> instances, ResourceLayout? resources,
            System.Diagnostics.Stopwatch watch)
        {
            var mobsBound = false;
            var corrupted = info.Type.StartsWith("CORRUPTED_DUNGEON", StringComparison.Ordinal);
            var hellgate = HellgateData.IsHellgateType(info.Type);
            try
            {
                var nameInfo = ClusterNameInfo.Parse(info.File);
                var (tier, continent) = (Tier ?? nameInfo?.Tier ?? name.Tier ?? 0, ClusterNameInfo.ContinentNames.GetValueOrDefault(nameInfo?.Continent ?? 0, "None"));
                var (mobStore, behaviors) = (store.Mobs, store.SpawnBehaviors);
                // StageL corrupted (SPEC §1.2 [V]): the sorcerer's teleport markers have no spawn block at all and are never a mob.
                // StageL hellgates (SPEC §2.4): the same markers in Hellgate_10 (the sorcerer boss's targets, fame 0) are no demons either.
                if (corrupted || hellgate) Mobs.RemoveAll(m => m.SpawnBehavior is { } sb && CorruptedData.MarkerBehaviours.Contains(sb));
                Mobs = [.. Mobs.Select(m => m.Bind(mobStore, behaviors, tier, continent, info.Type))];
                if (resources is { MobSpawnPoints.Count: > 0 })
                    resources = resources with { MobSpawnPoints = [.. resources.MobSpawnPoints.Select(m => m.Bind(mobStore, behaviors, tier, continent, info.Type))] };
                mobsBound = true;
            }
            catch (Exception e) when (e is InvalidDataException or IOException)
            {
                if (Mobs.Count > 0 || resources?.MobSpawnPoints.Count > 0) Problem($"mobs not bound: {e.Message}");
            }
            // Stage 5c: the kill rewards' danger type (worldsettings.xml archetype clusterdangerbonustype) and loot.xml.
            string? danger = null;
            try { danger = store.Archetypes.DangerTypeOf(info.Type); }
            catch (Exception e) when (e is InvalidDataException or IOException) { Problem($"cluster danger type unknown: {e.Message}"); }
            var pvp = ClusterPvp.Unresolved; // stageE: the archetype's PvP rules, read here with the danger type (fail safe without)
            try { pvp = store.Archetypes.PvpOf(info.Type); }
            catch (Exception e) when (e is InvalidDataException or IOException) { Problem($"cluster PvP rules unknown: {e.Message}"); }
            IReadOnlyList<SpawnBuff> spawnBuffs = []; // the cluster type's spawnbuff list, read with its archetype
            try { spawnBuffs = store.Archetypes.SpawnBuffsOf(info.Type); }
            catch (Exception e) when (e is InvalidDataException or IOException) { Problem($"cluster spawn buffs unknown: {e.Message}"); }
            if (mobsBound || Fishing.Count > 0) // the fishing catches roll loot.xml too (never read under the lock)
            {
                try { _ = store.Loot; }
                catch (Exception e) when (e is InvalidDataException or IOException) { Problem($"loot.xml not read, mobs drop no items and fishing catches nothing: {e.Message}"); }
            }
            // StageG dungeons: the markers of the placed prefabs after the template's own; randomdungeons.xml and templatemetadata.xml,
            // read now when the cluster has exit markers.
            if (resources is { DungeonMarkers.Count: > 0 }) DungeonMarkers.AddRange(resources.DungeonMarkers);
            if (DungeonMarkers.Count > 0)
            {
                try { _ = store.Dungeons; }
                catch (Exception e) when (e is InvalidDataException or IOException) { Problem($"randomdungeons.xml not read, no random dungeon exits: {e.Message}"); }
            }
            // StageL mists (SPEC §6.5): mists.xml with exit markers (the open world's mists portals), a mists city entrance (5100-5103), Brecilien's
            // static entrance (5001, phase B) or a mists zone, and lootchests.xml in a zone (its treasure chests); phase B: randomdungeons.xml in a
            // zone too (its Knightfall Abbey and wisp portals).
            var mists = info.Type.StartsWith("MISTS_", StringComparison.Ordinal);
            if (mists || DungeonMarkers.Count > 0 || Critical.Any(m => m.Type is "mistscityentrance" or "mistsstaticentrance"))
            {
                try { _ = store.Mists; }
                catch (Exception e) when (e is InvalidDataException or IOException) { Problem($"mists.xml not read, no mists: {e.Message}"); }
                if (mists)
                {
                    try { _ = store.LootChests; }
                    catch (Exception e) when (e is InvalidDataException or IOException) { Problem($"lootchests.xml not read, no mists treasures: {e.Message}"); }
                    try { _ = store.Dungeons; }
                    catch (Exception e) when (e is InvalidDataException or IOException) { Problem($"randomdungeons.xml not read, no portals inside the zone: {e.Message}"); }
                }
            }
            // StageG dungeons: the chest points bound to lootchests.xml, read now when the cluster has any.
            if (ChestPoints.Count > 0)
            {
                try
                {
                    var chests = store.LootChests;
                    ChestPoints = [.. ChestPoints.Select(c => c with { Behaviour = chests.Spawnpoints.GetValueOrDefault(c.SpawnBehaviour) })];
                    foreach (var c in ChestPoints.Where(c => c.Behaviour is null)) Problem($"loot chest spawnpoint {c.SpawnBehaviour} not in lootchests.xml");
                }
                catch (Exception e) when (e is InvalidDataException or IOException) { Problem($"lootchests.xml not read, no loot chests: {e.Message}"); }
            }
            // StageH bosses: the quest areas bound to localquests.xml, read now when the cluster has any; and the POI chest cap.
            if (QuestAreas.Count > 0)
            {
                try
                {
                    var quests = store.LocalQuests;
                    QuestAreas = [.. QuestAreas.Select(a => a with { Quest = quests.Find(a.QuestId) })];
                    foreach (var a in QuestAreas.Where(a => a.Quest is null)) Problem($"local quest {a.QuestId} not in localquests.xml");
                }
                catch (Exception e) when (e is InvalidDataException or IOException) { Problem($"localquests.xml not read, no camp quests: {e.Message}"); }
            }
            // StageH expeditions: the expedition tables, read now with the agent's city or an expedition cluster.
            if (Agents.Count > 0 || info.Type.Contains("EXPEDITION", StringComparison.Ordinal))
            {
                try { _ = store.Expeditions; }
                catch (Exception e) when (e is InvalidDataException or IOException) { Problem($"expedition tables not read, no expeditions: {e.Message}"); }
            }
            // StageL corrupted: corrupteddungeons.xml with a Hell Den's entrance, a corrupted dungeon or exit markers (phase B: the open world's
            // corrupted portals), and randomdungeons.xml (its exit portal's prefab).
            if (corrupted || DungeonMarkers.Count > 0 || Critical.Any(m => m.Type == "corrupteddungeonstaticentrance"))
            {
                try { _ = store.Corrupted; }
                catch (Exception e) when (e is InvalidDataException or IOException) { Problem($"corrupteddungeons.xml not read, no corrupted dungeons: {e.Message}"); }
                if (corrupted)
                {
                    try { _ = store.Dungeons; }
                    catch (Exception e) when (e is InvalidDataException or IOException) { Problem($"randomdungeons.xml not read, no corrupted exit portal: {e.Message}"); }
                }
            }
            // StageL hellgates: hellgate.xml with a Hell Den's shrine or a hellgate, and lootchests.xml (the objective chest of every map).
            if (hellgate || HellgateShrines.Count > 0)
            {
                try { _ = store.Hellgates; }
                catch (Exception e) when (e is InvalidDataException or IOException) { Problem($"hellgate.xml not read, no hellgates: {e.Message}"); }
                if (hellgate)
                {
                    try { _ = store.LootChests; }
                    catch (Exception e) when (e is InvalidDataException or IOException) { Problem($"lootchests.xml not read, no hellgate chests: {e.Message}"); }
                    try { _ = store.Dungeons; } // phase B: the chain exit's visualization (HELLGATE_CHAIN_EXIT)
                    catch (Exception e) when (e is InvalidDataException or IOException) { Problem($"randomdungeons.xml not read, no hellgate chain exit: {e.Message}"); }
                }
            }
            // StageN shrines: buffshrines.xml with the first cluster that places a shrine; the ones it does not run are left out.
            if (BuffShrines.Count > 0)
            {
                try
                {
                    var shrines = store.BuffShrines;
                    BuffShrines = [.. BuffShrines.Select(b => b with { Definition = shrines.Find(b.ShrineType) }).Where(b => b.Definition is { Supported: true })];
                }
                catch (Exception e) when (e is InvalidDataException or IOException)
                {
                    Problem($"buffshrines.xml not read, no shrines: {e.Message}");
                    BuffShrines = [];
                }
            }
            // StageH islands: the island types, read now with a city that has an island merchant or an island template.
            if (Islands.Count > 0 || info.Type.Contains("ISLAND", StringComparison.Ordinal))
            {
                try { _ = store.Islands; }
                catch (Exception e) when (e is InvalidDataException or IOException) { Problem($"island types not read, no islands: {e.Message}"); }
            }
            int? poiLimit = null;
            try { poiLimit = ChestPoints.Count > 0 && info.Type is { Length: > 0 } ct ? store.Archetypes.PoiChestLimitOf(ct) : null; }
            catch (Exception e) when (e is InvalidDataException or IOException) { Problem($"POI chest limit unknown: {e.Message}"); }
            watch.Stop();
            return new ClusterTemplate
            {
                Info = info, Name = name, Origin = origin, Size = size, Instances = instances, CriticalObjects = criticalObjects, Critical = Critical,
                Buildings = Buildings, Exits = Exits, Travelpoints = Travelpoints, FactionWarfarePortals = Portals, IslandAccessPoints = Islands,
                ExpeditionAgents = Agents, LocationMarkers = Markers, TutorialBlockers = TutorialBlockers, Harvestables = Harvestables, MobSpawnPoints = Mobs, ResourceNodes = Nodes,
                RealEstates = Estates, FishingZones = Fishing, RespawnPoints = Respawns, Resources = resources, DungeonMarkers = DungeonMarkers, LootChests = ChestPoints,
                LocalQuestAreas = QuestAreas, PoiChestLimit = poiLimit, ExpeditionObjects = ExpeditionObjects, Colliders = Colliders, Roads = Roads, IslandManagements = IslandManagements, HideoutManagements = HideoutManagements, BuildCells = BuildCells,
                InvaderSpawns = InvaderSpawns, HellgateShrines = HellgateShrines, BuffShrines = BuffShrines, HellgateMarkers = HellgateMarkers,
                HellgateAreas = HellgateAreas, Warcamps = Warcamps, Orbs = Orbs, SpawnBuffs = spawnBuffs, MistsSpots = MistsSpots,
                Problems = [.. _problems.Select(p => p.Value == 1 ? p.Key : $"{p.Key} (x{p.Value})")],
                LoadTime = watch.Elapsed, MobsBound = mobsBound, DangerType = danger, Pvp = pvp,
            };
        }
    }

    /// <summary>A rolled dynamic template: in the cluster's template folder, else (as the port) GREEN, NONE, RED, DEAD.</summary>
    private static TemplateLayout? DynamicLayout(ClusterStore store, string folder, string reference) =>
        new[] { folder, "GREEN", "NONE", "RED", "DEAD" }.Distinct().Select(f => store.Layout(f, reference)).FirstOrDefault(l => l is not null);

    /// <summary>
    /// The resource placement of the cluster (stage 4b SPEC §4.4-§4.8): the node tiles of <paramref name="sources"/> in
    /// slot order (List&lt;string&gt;.Sort of the slot ids) and template document order, the territory rectangles of their
    /// critical members, the preset (with the archetype's pvp rules) and the mapping, validation, d0.a with the seed
    /// d0.a(world.xml id, key 57), and the landscape step a81.p 0x180517EF0: every placed plain tile is snapped, every
    /// compound put at whole metres with its children turned about it (rounded to 4 decimals, a71.b), and each placed
    /// tile or compound child with a harvestable component becomes a live resource node.
    /// </summary>
    private static ResourceLayout Place(ClusterStore store, ClusterInfo info, ClusterNameInfo cni, int pvpRules, LayoutSession session,
        List<(string Slot, TemplateLayout Layout, Placement Place, HashSet<string> Layers)> sources, Action<string> problem)
    {
        var nodes = new Dictionary<(int, string), List<NodeTile>>();
        var areas = new NodeAreas();
        foreach (var (_, layout, place, layers) in sources.OrderBy(x => x.Slot, ClientCompare.Instance))
        {
            foreach (var t in layout.Tiles)
            {
                if (t.Component.Name.LocalName != "resourcenode" || t.Layer is not null && !layers.Contains(t.Layer)) continue;
                var nt = ((string?)t.Component.Attribute("nodetype") ?? "").ToLowerInvariant() switch { "medium" => 20, "high" => 30, _ => 10 };
                var res = ((string?)t.Component.Attribute("resourcetype") ?? "").ToUpperInvariant();
                var (x, y, z) = place.Apply(t.X, t.Y, t.Z);
                if (!nodes.TryGetValue((nt, res), out var list)) nodes[(nt, res)] = list = [];
                list.Add(new NodeTile((float)x, (float)y, (float)z, TileMath.Mod360(place.Yaw + t.Yaw)));
            }
            foreach (var m in layout.Members)
            {
                if (m.Layer.Length > 0 && !layers.Contains(m.Layer)) continue;
                var (x, _, z) = place.Apply(m.X, m.Y, m.Z);
                areas.Add(m.Components, (float)x, (float)z, TileMath.Mod360(place.Yaw + m.Yaw));
            }
        }
        var preset = store.Presets.Find(ResourcePresets.Key(cni, pvpRules));
        var mapping = store.Mappings.Find(cni);
        var seed = DaySeed.Of(info.Id, session.Key57);
        var layoutOf = new ResourceLayout
        {
            Session = session, SeedText = DaySeed.Text(info.Id, session.Key57), Seed = seed, Preset = preset?.Name, Mapping = mapping?.Name,
            NodeCounts = nodes.ToDictionary(n => n.Key, n => n.Value.Count),
        };
        // d0.a 0x1808B9E10: Test and Debug clusters and ALBIONTV_01 never get nodes.
        var invalid = cni.Type is 12 or 13 || info.Id == "ALBIONTV_01" ? "a test cluster" : ResourcePlacement.Validate(preset, mapping, nodes);
        if (invalid is not null) return layoutOf with { Invalid = invalid };
        List<(string Name, List<(NodeTile Node, PresetEntry Entry)> Nodes)> result;
        List<(PresetEntry, int)> counts;
        try { (result, counts) = ResourcePlacement.Place(preset!, mapping!.Value.Lists, cni.Continent, cni.Faction, session.LowerFlags, areas, seed, nodes); }
        catch (InvalidDataException e)
        {
            problem($"resource placement: {e.Message}");
            return layoutOf with { Invalid = e.Message };
        }

        var tiles = store.Tiles;
        var placements = new List<PlacedPrefab>();
        var harvestables = new List<HarvestableSpawn>();
        XElement? HarvestableOf(string tile) => tiles.Components(tile).FirstOrDefault(c => c.Name.LocalName == "harvestable");
        void Candidate(XElement c, string tile, float x, float z, float height, double direction, PlacedPrefab prefab)
        {
            var type = store.Harvestables.Find((string?)c.Attribute("type") ?? "");
            var tierNumber = TileMath.Int((string?)c.Attribute("tier"), 1);
            if (type is null) problem($"resource prefab {tile}: harvestable type {(string?)c.Attribute("type")} not in harvestables.xml");
            else if (!type.Tiers.TryGetValue(tierNumber, out var tier)) problem($"resource prefab {tile}: harvestable {type.Name} has no tier {tierNumber}");
            else harvestables.Add(new HarvestableSpawn
            {
                Key = new SpawnKey(info.Id, "resources", harvestables.Count), Tile = tile, Position = new Vector2(x, z), Height = height,
                Direction = (float)direction, Component = c, Type = type, Tier = tier, Prefab = prefab,
            });
        }
        // Stage 5 (SPEC §5a.1): the mob spawn points of the placed prefabs (mob camps, roaming mobs, the critters of hide and
        // fiber nodes), placed as their harvestables are; a waypoint turns with the prefab about its position.
        var mobPoints = new List<MobSpawnPoint>();
        var markers = new List<DungeonExitMarker>();
        void MobPoints(string tile, float x, float z, float height, double direction, double frameX, double frameZ, double frameYaw, PlacedPrefab prefab)
        {
            foreach (var c in tiles.Components(tile))
            {
                // StageG dungeons: a random dungeon exit marker of the prefab (SPEC §8.1), where the marker tile lands.
                if (c.Name.LocalName == "randomdungeonrandomexitpositionmarker")
                    markers.Add(new DungeonExitMarker
                    {
                        Key = new SpawnKey(info.Id, "resourcemarkers", markers.Count), Tile = tile, Position = new Vector2(x, z), Height = height,
                        Direction = (float)TileMath.Mod360(direction), Component = c, Type = ((string?)c.Attribute("Type") ?? "SOLO").ToUpperInvariant(),
                    });
                if (c.Name.LocalName != "mobspawnpoint") continue;
                mobPoints.Add(MobSpawnPoint.Of(c, new SpawnKey(info.Id, "resourcemobs", mobPoints.Count), tile, new Vector2(x, z), height, (float)direction, (wx, wz) =>
                {
                    var (dx, dz) = TileMath.Rotate(wx, wz, frameYaw);
                    return new Vector2((float)(frameX + dx), (float)(frameZ + dz));
                }, prefab));
            }
        }
        foreach (var (prefab, list) in result)
        {
            if (prefab.Length == 0) continue; // an empty filler places nothing
            var compound = tiles.IsCompound(prefab);
            foreach (var (node, entry) in list)
            {
                if (!compound)
                {
                    var pos = tiles.Footprint(prefab) is { } footprint
                        ? ResourcePlacement.Snap(footprint, node.X, node.Z, ResourcePlacement.RotationIndex((float)node.Direction)) : new Vector2(node.X, node.Z);
                    var placed = new PlacedPrefab(prefab, entry.Resource, entry.Tier, entry.NodeType, false, new Vector2(node.X, node.Z), (float)node.Direction, pos, node.Y);
                    placements.Add(placed);
                    if (HarvestableOf(prefab) is { } c) Candidate(c, prefab, pos.X, pos.Y, node.Y, node.Direction, placed);
                    MobPoints(prefab, pos.X, pos.Y, node.Y, node.Direction, pos.X, pos.Y, node.Direction, placed);
                }
                else
                {
                    // CVTTSD2SI(Math.Round(x)): the compound at whole metres; its layer-0 children turned about it by the node's direction.
                    float px = (int)Math.Round((double)node.X, MidpointRounding.ToEven), pz = (int)Math.Round((double)node.Z, MidpointRounding.ToEven);
                    var placed = new PlacedPrefab(prefab, entry.Resource, entry.Tier, entry.NodeType, true, new Vector2(node.X, node.Z), (float)node.Direction, new Vector2(px, pz), node.Y);
                    placements.Add(placed);
                    foreach (var child in tiles.Compound(prefab))
                    {
                        if (child.Layer is not null) continue;
                        var (dx, dz) = TileMath.Rotate(child.X, child.Z, node.Direction);
                        var (cx, cz) = ((float)Math.Round(px + dx, 4), (float)Math.Round(pz + dz, 4));
                        if (HarvestableOf(child.Name) is { } c) Candidate(c, child.Name, cx, cz, node.Y, TileMath.Mod360(node.Direction + child.Yaw), placed);
                        MobPoints(child.Name, cx, cz, node.Y, TileMath.Mod360(node.Direction + child.Yaw), px, pz, node.Direction, placed);
                    }
                }
            }
        }
        return layoutOf with { Counts = counts, Placements = placements, Harvestables = harvestables, MobSpawnPoints = mobPoints, DungeonMarkers = markers };
    }
}

/// <summary>
/// Stage I bots: a tile with walk-blocking cells placed in the cluster: <see cref="Grid"/> (tile frame) at world (x, z) turned by
/// <see cref="Yaw"/> degrees (<see cref="TileMath.Rotate"/>'s convention).
/// </summary>
public readonly record struct PlacedCollider(TileCollision Grid, double X, double Z, double Yaw);

/// <summary>Stage I bots: a road graph of the cluster: its nodes in world x/z and the links between them (node indices).</summary>
public sealed record RoadLine(IReadOnlyList<Vector2> Nodes, IReadOnlyList<(int A, int B)> Links);

/// <summary>
/// The fields of a .cluster file name, as the client reads them (bgv.a 0x18068e280 with the subbiome rule on):
/// "Id_Type_Biome_SubBiome_T&lt;tier&gt;_Faction_Continent", split at '_' and '.'. The template folder is the SubBiome: AUTO
/// is GREEN up to tier 4, RED for 5-6 and DEAD from 7; an unknown one is GREEN; Hell, Crystal, Roads and Dragon (and an
/// unknown biome) have none and use NONE (SPEC stage 2 §2; 0000, 1000 and 1207 all use GREEN).
/// </summary>
public sealed record ClusterName(string Id, string? Type, string? Biome, int? Tier, string? SubBiomeToken, string TemplateFolder, string? Faction, string? Continent)
{
    private static readonly Dictionary<string, string> Biomes = new()
    {
        ["ST"] = "Steppe", ["SW"] = "Swamp", ["HL"] = "Highland", ["MN"] = "Mountain", ["FR"] = "Forest",
        ["HE"] = "Hell", ["CR"] = "Crystal", ["RD"] = "Roads", ["RO"] = "Roads", ["MI"] = "Mists", ["DR"] = "Dragon",
    };

    public static ClusterName Parse(string file)
    {
        var name = Regex.Replace(Path.GetFileName(file), @"\.cluster(\.xml|\.bin)?$", "");
        var t = name.Split('_', '.');
        var biome = t.Length > 2 ? Biomes.GetValueOrDefault(t[2]) : null;
        int? tier = t.Length > 4 && Regex.IsMatch(t[4], @"^T\d+$") ? int.Parse(t[4][1..], CultureInfo.InvariantCulture) : null;
        var token = t.Length > 3 ? t[3] : null;
        // StageL mists (SPEC §3.1, V6): a generated zone's name carries the short aliases GRN, RED, DED.
        var folder = token == "AUTO" ? ((tier ?? 0) <= 4 ? "GREEN" : tier <= 6 ? "RED" : "DEAD")
            : token?.ToUpperInvariant() switch { "GREEN" or "GRN" => "GREEN", "RED" => "RED", "DEAD" or "DED" => "DEAD", _ => "GREEN" };
        if (biome is null or "Hell" or "Crystal" or "Roads" or "Dragon") folder = "NONE";
        return new ClusterName(t[0], t.Length > 1 ? t[1] : null, biome, tier, token, folder, t.Length > 5 ? t[5] : null, t.Length > 6 ? t[6] : null);
    }
}

/// <summary>
/// A templateinstance of the .cluster file; <see cref="Folder"/> = the folder its template was read from (null = not found).
/// <see cref="Reference"/> is the template placed: the rolled dynamic template of a dyn slot that got one (<see cref="Roll"/>,
/// stage 4b), else <see cref="Default"/>, the file's ref.
/// </summary>
public sealed record TemplateInstance(string Slot, string Reference, string? Folder, Vector2 Position, float Height, float Rotation, bool Dynamic,
    IReadOnlySet<string> ActiveLayers)
{
    public string Default { get; init; } = Reference;

    public DynamicRoll? Roll { get; init; }
}

/// <summary>
/// The stable key of a spawned object (ARCHITECTURE.md §4.1): "&lt;cluster&gt;:&lt;templateinstance id&gt;:&lt;n&gt;", n = the
/// object's number in its template, from the template's structure alone (<see cref="TemplateLayout"/>: critical members
/// first, then one per component of every tile in file order, compound children where their compound is), counted over
/// all layers and all components, so neither enabling an event layer nor placing one more kind of component moves a
/// key; only a client data update can. Content patches, GM commands and saved respawn timers refer to it.
/// </summary>
public readonly record struct SpawnKey(string Cluster, string Slot, int Index)
{
    public override string ToString() => $"{Cluster}:{Slot}:{Index}";
}

/// <summary>One resolved object of a cluster, in world coordinates.</summary>
public abstract record ClusterObject
{
    public required SpawnKey Key { get; init; }

    /// <summary>The tile the template places (the client's landscape tile; NewBuilding/NewTravelpoint key 3).</summary>
    public required string Tile { get; init; }

    /// <summary>World 2D (x, z), the exact tile position the client binds landscape objects to (|d| &lt; 0.001 per axis, SPEC §1).</summary>
    public required Vector2 Position { get; init; }

    /// <summary>The y of "x y z".</summary>
    public float Height { get; init; }

    /// <summary>Yaw in degrees, 0 ≤ d &lt; 360: tile roty + compound + instance rot.</summary>
    public float Direction { get; init; }

    /// <summary>
    /// The component this object comes from: the template's inline attributes over the tiles.xml ones (inline win), with
    /// its children. Shared with the tile index and other clusters: read it, never change it.
    /// </summary>
    public required XElement Component { get; init; }

    public string? Attr(string name) => (string?)Component.Attribute(name);
}

/// <summary>
/// A criticalscenemember of a template (the server objects of a cluster; SPEC §2 "Critical members"). <see cref="Guid"/>
/// = world.xml criticalObjects[path] (null when world.xml lists none); <see cref="Component"/> = the one named like the
/// type, else the first.
/// </summary>
public sealed record TutorialBlockerSpawn : ClusterObject
{
    public required string BlockerId { get; init; }
}

public sealed record CriticalMember : ClusterObject
{
    public required string Type { get; init; }
    public required string Member { get; init; }

    /// <summary>"&lt;templateinstance id&gt;/&lt;member id&gt;", the criticalObjects path.</summary>
    public required string Path { get; init; }

    public Guid? Guid { get; init; }

    /// <summary>layerId ("" = every layer).</summary>
    public string Layer { get; init; } = "";

    public IReadOnlyList<XElement> Components { get; init; } = [];
}

/// <summary>NewBuilding 45: key 1 <see cref="Guid"/>, key 2 Template.Id, key 3 <see cref="ClusterObject.Tile"/>, 4 position, 5 direction.</summary>
public sealed record BuildingSpawn : ClusterObject
{
    public required Guid Guid { get; init; }
    public required BuildingTemplate Template { get; init; }
    public short TypeId => Template.Id;

    /// <summary>
    /// The world.xml questgiver whose id is this building's Guid (the city's faction trader: FACTIONSTORE_SWAMP in 0000,
    /// FACTIONSTORE_FOREST in 1000); null for the other buildings. Such a building is cluster-wide and comes with
    /// QuestGiverInfoForPlayer 259 (SPEC §3, §5).
    /// </summary>
    public WorldQuestGiver? QuestGiver { get; init; }

    /// <summary>The questgivertypes.xml id of <see cref="QuestGiver"/>'s type (259 key 2); null without one or when the type is unknown.</summary>
    public int? QuestGiverType { get; init; }
}

/// <summary>NewExit 215: key 1 <see cref="Guid"/> = world.xml exit id; <see cref="World"/> = that entry (target, target type, its pos = the tile's).</summary>
public sealed record ExitSpawn : ClusterObject
{
    public required Guid Guid { get; init; }
    public WorldExit? World { get; init; }

    /// <summary>
    /// The exit's spawnpos turned with the tile, in world coordinates (stage 3), as clusterdump.py's spawnpos_world. A player
    /// coming through the exit arrives there, unless the tile's collision pushes the arrival further (Transfers.Through).
    /// </summary>
    public Vector2? SpawnPosition { get; init; }

    /// <summary>The walk-blocking cells of the exit's tile (the bank entrances' pillars); null for a tile without any.</summary>
    public TileCollision? Collision { get; init; }
}

public sealed record TravelpointSpawn : ClusterObject
{
    public required Guid Guid { get; init; }

    /// <summary>The world.xml travelpoint of the same id (connections), if any.</summary>
    public WorldTravelpoint? World { get; init; }

    public Vector2? SpawnPosition { get; init; }
}

public sealed record FactionWarfarePortalSpawn : ClusterObject
{
    public required Guid Guid { get; init; }
    public Vector2? SpawnPosition { get; init; }
}

public sealed record IslandAccessPointSpawn : ClusterObject
{
    public required Guid Guid { get; init; }
    public WorldIslandAccessPoint? World { get; init; }
}

/// <summary>An islandmanagement tile of an island template (&lt;BIOME&gt;_ISLAND_MANAGEMENT; stageH islands SPEC §0.2): NewIslandManagement 225 on an island instance.</summary>
public sealed record IslandManagementSpawn : ClusterObject;

/// <summary>A hideoutmanagement tile of a hideout template (HIDEOUT_MANAGEMENT; stageP hideouts): NewHideoutManagement 420 on a hideout instance.</summary>
public sealed record HideoutManagementSpawn : ClusterObject;

/// <summary>A namedmarkerpoint tile (stageL corrupted: InvaderSpawnpoint, where an invader comes in, SPEC §4.4; stageL hellgates: <see cref="Name"/> one of <see cref="HellgateData.Markers"/>).</summary>
public sealed record NamedMarkerSpawn : ClusterObject
{
    /// <summary>The component's name attribute (set for the hellgate markers).</summary>
    public string Name { get; init; } = "";
}

/// <summary>A hellgateshrine tile (stageL hellgates SPEC §2.5: SHARED_HELLGATE_ENTRANCE, size 7 × 7): a Hell Den's hellgate entrance.</summary>
public sealed record HellgateShrineSpawn : ClusterObject;

/// <summary>A &lt;shrine shrinetype&gt; tile (stageN shrines SPEC §2: SHRINE_NON_COMBAT_BUFF with GENERAL_SHRINE_COMBAT_BUFF, …) and its buffshrines.xml definition.</summary>
public sealed record BuffShrineSpawn : ClusterObject
{
    public required string ShrineType { get; init; }

    /// <summary>The definition (bound in Finish; null only before).</summary>
    public BuffShrineData.Definition? Definition { get; init; }
}

/// <summary>
/// A spot of a mists template (stageL mists SPEC §2.5, phase B): <see cref="Kind"/> MistsSpawnpoint, MistsSpecialExitSpawnpoint, mistswispspawn,
/// unrestrictedpvpzone (its radius attribute) or "treasure:&lt;SUBTYPE&gt;".
/// </summary>
public sealed record MistsSpot : ClusterObject
{
    public required string Kind { get; init; }
}


/// <summary>
/// A HellgateFxManager's &lt;arearestrictionsettings center startradius endradius startwidth endwidth&gt; (stageL hellgates SPEC §2.4; Hellgate_01:
/// (14, 41), 154.6 → 25 m, width 20): the restricted area of phase B (HellgateRestrictedAreaUpdate 452 keys 3-7).
/// </summary>
/// <summary>A WarCamp_Arena tile's &lt;warcamp isattacker&gt; with its &lt;spawnpositions&gt; (stage N arena SPEC §3: 1 in the 1v1 arena, 20 in the others).</summary>
public sealed record WarcampSpawn : ClusterObject
{
    public IReadOnlyList<Vector2> Spawns { get; init; } = [];
}

/// <summary>An arena template's &lt;orb type&gt; tile (stageP league-pvp SPEC §3.2): its orbtypes.xml uniquename.</summary>
public sealed record OrbSpawn : ClusterObject
{
    public string OrbType { get; init; } = "";
}

public sealed record HellgateAreaSpawn : ClusterObject
{
    public Vector2 Center { get; init; }
    public float StartRadius { get; init; }
    public float EndRadius { get; init; }
    public float StartWidth { get; init; }
    public float EndWidth { get; init; }
}

/// <summary>
/// A tile of an expedition template with an expeditionexit (<see cref="SpawnAtStartup"/>: the start exit), expeditionnarrator (the tile
/// name ExpeditionNarrator or ExpeditionNarratorFinish) or expeditioncheckpoint (<see cref="ColliderDistance"/>) component (SPEC §2.7).
/// </summary>
public sealed record ExpeditionObjectSpawn : ClusterObject
{
    /// <summary>The component: expeditionexit, expeditionnarrator or expeditioncheckpoint.</summary>
    public required string Kind { get; init; }

    public bool SpawnAtStartup { get; init; }

    /// <summary>maxcolliderdist of a checkpoint (m).</summary>
    public float ColliderDistance { get; init; }
}

/// <summary>NewExpeditionAgent 314; <see cref="AgentType"/> is an expeditionagents.xml uniquename (key 3 = its index).</summary>
public sealed record ExpeditionAgentSpawn : ClusterObject
{
    public required Guid Guid { get; init; }
    public required string AgentType { get; init; }

    /// <summary>The expeditionagents.xml id of <see cref="AgentType"/> (key 3; <see cref="ClientNumbering.ExpeditionAgents"/>); null = not in the file.</summary>
    public int? AgentId { get; init; }
}

public sealed record LocationMarkerSpawn : ClusterObject
{
    public required string LocationId { get; init; }
    public float Radius { get; init; }
}

/// <summary>
/// A static tree or rock, or (with <see cref="Prefab"/>) a resource node the client's placement put there (stage 4b);
/// <see cref="Charges"/> = full charges of its tier, the upper bound of its highest Charge level (list 39 key 4).
/// </summary>
public sealed record HarvestableSpawn : ClusterObject
{
    public required HarvestableTemplate Type { get; init; }
    public required HarvestableTier Tier { get; init; }
    public int Charges => Tier.Charges;

    /// <summary>The resource prefab placed on a node tile that this harvestable is part of; null for a template's static one.</summary>
    public PlacedPrefab? Prefab { get; init; }
}

/// <summary>
/// A mobspawnpoint tile: a named mob (<see cref="Mob"/>, a mobs.xml uniquename counted by world.xml mobcounts) or a
/// placeholder name: RANDOM (a spawn behaviour picks the mob) or WORLDBOSS (<see cref="WorldBoss"/> names the
/// worldbosses.xml boss whose mob it is). Stage 5 (SPEC §5a.1, §5a.2): <see cref="Template"/> is the named mob and
/// <see cref="Block"/> the spawn behaviour block a RANDOM point of this cluster uses, both bound when the cluster is
/// resolved (off the world thread); <see cref="Prefab"/> is set for the points of the resource layout's prefabs (mob
/// camps, roaming mobs, critters of hide and fiber nodes, stage 4b).
/// </summary>
public sealed record MobSpawnPoint : ClusterObject
{
    /// <summary>The placeholder mob names: a spawn behaviour or a world boss decides the mob.</summary>
    public const string Random = "RANDOM", WorldBossName = "WORLDBOSS";

    /// <summary>mobname; a point with a spawnbehavior and no mobname counts as RANDOM (<see cref="MobName"/>).</summary>
    public string? Mob { get; init; }
    public string? SpawnBehavior { get; init; }

    /// <summary>GUARD, PATROL or none (roaming): the idle behaviour of 5d (5a mobs stand).</summary>
    public string? Behavior { get; init; }
    public string? WorldBoss { get; init; }

    /// <summary>animswitch: the client's idle pose; nothing for the server.</summary>
    public string? AnimSwitch { get; init; }

    public string? TrackerId { get; init; }

    /// <summary>The PATROL path (&lt;path&gt;&lt;waypoint pos idle pace/&gt;), in world coordinates; empty for most points.</summary>
    public IReadOnlyList<Waypoint> Waypoints { get; init; } = [];

    /// <summary>The mobs.xml mob of a named point; null for RANDOM and WORLDBOSS, and for a name mobs.xml does not have.</summary>
    public MobTemplate? Template { get; init; }

    /// <summary>The spawn behaviour block of a RANDOM point in this cluster; null for a named point or an unknown behaviour.</summary>
    public SpawnBlock? Block { get; init; }

    /// <summary>The resource prefab (stage 4b) the point is part of; null for a template's own point.</summary>
    public PlacedPrefab? Prefab { get; init; }

    /// <summary>The mob name as the point gives it: mobname, else RANDOM when it has a spawn behaviour.</summary>
    public string? MobName => Mob ?? (SpawnBehavior is not null ? Random : null);

    /// <summary>
    /// The point read from <paramref name="c"/> (a mobspawnpoint component) at <paramref name="pos"/>; its waypoints, in
    /// the template's frame like the tile position, go through <paramref name="toWorld"/>.
    /// </summary>
    internal static MobSpawnPoint Of(XElement c, SpawnKey key, string tile, Vector2 pos, float height, float direction, Func<double, double, Vector2> toWorld,
        PlacedPrefab? prefab = null)
    {
        string? A(string attribute) => (string?)c.Attribute(attribute) is { Length: > 0 } v ? v : null;
        var waypoints = c.Element("path")?.Elements("waypoint").Select(w =>
        {
            var at = TileMath.V2((string?)w.Attribute("pos")) ?? Vector2.Zero;
            return new Waypoint(toWorld(at.X, at.Y), TileMath.Float((string?)w.Attribute("idle")), (string?)w.Attribute("pace") ?? "WALK");
        }).ToList();
        return new MobSpawnPoint
        {
            Key = key, Tile = tile, Position = pos, Height = height, Direction = direction, Component = c, Mob = A("mobname"), SpawnBehavior = A("spawnbehavior"),
            Behavior = A("behavior"), WorldBoss = A("worldboss"), AnimSwitch = A("animswitch"), TrackerId = A("trackerid"), Waypoints = waypoints ?? [], Prefab = prefab,
        };
    }

    /// <summary>
    /// The point with its mob bound (SPEC §5a.2): a named one to its mobs.xml mob; a RANDOM one to the first block of its
    /// spawn behaviour that fits a cluster of <paramref name="tier"/>, <paramref name="continent"/> and world.xml type
    /// <paramref name="clusterType"/>. WORLDBOSS and unknown names stay unbound (the spawner logs and skips them).
    /// </summary>
    public MobSpawnPoint Bind(MobStore mobs, SpawnBehaviors behaviors, int tier, string continent, string? clusterType) => MobName switch
    {
        null or WorldBossName => this,
        Random => this with { Block = SpawnBehavior is { } b ? behaviors.Select(b, tier, continent, clusterType) : null },
        // StageH expeditions (SPEC §4.3): an expedition cluster's spawn name mobs.xml lacks goes through the alias table and the token match.
        var name => this with
        {
            Template = mobs.Find(name) is { } m && m.Name == name ? m
                : clusterType?.Contains("EXPEDITION", StringComparison.Ordinal) == true ? ExpeditionData.ResolveSpawn(mobs, name) : null,
        },
    };
}

/// <summary>A waypoint of a PATROL path: where (world), how long to stand there (s), WALK (movespeed) or RUN (attackmovespeed).</summary>
public sealed record Waypoint(Vector2 Position, float Idle, string Pace);

public sealed record ResourceNodeSpawn : ClusterObject
{
    public required string ResourceType { get; init; }
    public required string NodeType { get; init; }
}

public sealed record RealEstateSpawn : ClusterObject
{
    public required Guid Guid { get; init; }
    public Vector2? Size { get; init; }

    /// <summary>locationquality (A-E).</summary>
    public string? Quality { get; init; }
}

public sealed record FishingZoneSpawn : ClusterObject
{
    public string? Zone { get; init; }
    public float Radius { get; init; }
}

public sealed record RespawnPoint : ClusterObject
{
    public required Guid Guid { get; init; }
    public string? Type { get; init; }
}

/// <summary>
/// A tile with a randomdungeonrandomexitpositionmarker component (<see cref="Type"/> SOLO, VETERAN, ELITE, EVENT_SOLO, …) or a
/// randomdungeonfixedexit one (Type FIXED, <see cref="FixedExit"/> = its FixedExit name): where the server puts a
/// NewRandomDungeonExit (stageG dungeons SPEC §8.1); <see cref="ClusterObject.Direction"/> is the marker's rotation.
/// </summary>
public sealed record DungeonExitMarker : ClusterObject
{
    public required string Type { get; init; }
    public string? FixedExit { get; init; }
}

/// <summary>
/// A tile with a lootchestspawnpoint component (stageG dungeons SPEC §7, the REWARD layers of the dungeon templates):
/// <see cref="SpawnBehaviour"/> names a lootchests.xml LootChestSpawnpoint, <see cref="Behaviour"/> is that entry once bound
/// (<see cref="ClusterTemplate"/>'s builder; null when lootchests.xml does not have it).
/// </summary>
public sealed record LootChestSpawn : ClusterObject
{
    public required string SpawnBehaviour { get; init; }
    public ChestSpawnBehaviour? Behaviour { get; init; }
}

/// <summary>
/// A tile with a localquestarea component (stageH bosses SPEC §4.4): the circle of radius <see cref="Radius"/> around its position in
/// which a player starts the camp's quest <see cref="QuestId"/>; <see cref="Quest"/> is that localquests.xml entry once bound.
/// </summary>
public sealed record LocalQuestAreaSpawn : ClusterObject
{
    public required string QuestId { get; init; }
    public float Radius { get; init; }
    public LocalQuest? Quest { get; init; }
}

/// <summary>The Guids the client gives the exits of a random dungeon floor (bn5.ue / bn5.uf, SPEC §2.6): the first exit loaded, every later one.</summary>
public static class DungeonExits
{
    public static readonly Guid Up = new("00000000-0000-0000-0000-000000000001");
    public static readonly Guid Down = new("00000000-0000-0000-0000-000000000002");
}
