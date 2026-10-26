using System.Diagnostics;
using System.Globalization;
using System.Numerics;
using System.Xml.Linq;
using AlbionLocal.Photon;

namespace AlbionLocal.Server.Data;

/// <summary>
/// cluster/world.bin (world.xml; SPEC stage 2 §2 "Inputs"): every cluster with its .cluster file, type and bounds and the
/// server objects world.xml lists for it (exits with their targets, travelpoints, island access points, outposts, quest
/// givers, minimap markers, the number of territories, the static resource distribution and the mob counts), plus criticalObjects, the persistent
/// Guid of every critical scene member by path "&lt;templateinstance id&gt;/&lt;member id&gt;" (unused ones flagged). The
/// file is streamed one cluster element at a time; staticdungeons and the road networks are not read. Loaded once, at
/// the first Join that needs a cluster (<see cref="ClusterStore"/>).
/// </summary>
public sealed class WorldInfo
{
    private readonly Dictionary<string, ClusterInfo> _clusters;
    private readonly Dictionary<string, Dictionary<string, CriticalObject>> _critical;

    private WorldInfo(Dictionary<string, ClusterInfo> clusters, Dictionary<string, Dictionary<string, CriticalObject>> critical, TimeSpan loadTime,
        IReadOnlyList<string> problems)
    {
        _clusters = clusters;
        _critical = critical;
        LoadTime = loadTime;
        Problems = problems;
    }

    /// <summary>Every world.xml cluster by id (case-sensitive, as Join key 8 compares it), in file order.</summary>
    public IReadOnlyCollection<ClusterInfo> Clusters => _clusters.Values;

    /// <summary>The cluster <paramref name="id"/>; null = not in world.xml.</summary>
    public ClusterInfo? Find(string id) => _clusters.GetValueOrDefault(id);

    /// <summary>
    /// criticalObjects/cluster[@id] by path; null when world.xml has none for <paramref name="id"/>. Also for ids that
    /// are no cluster of their own but own a .cluster file other clusters share (<see cref="ClusterTemplate.CriticalObjects"/>).
    /// </summary>
    public IReadOnlyDictionary<string, CriticalObject>? CriticalOf(string id) => _critical.GetValueOrDefault(id);

    public TimeSpan LoadTime { get; }

    /// <summary>Values that did not parse (a Guid, a count, a tier), read as their default (Guid.Empty, 0); --check-content fails on any.</summary>
    public IReadOnlyList<string> Problems { get; }

    public static WorldInfo Load(GameDataFile file)
    {
        var watch = Stopwatch.StartNew();
        var clusters = new Dictionary<string, ClusterInfo>(StringComparer.Ordinal);
        var critical = new Dictionary<string, Dictionary<string, CriticalObject>>(StringComparer.Ordinal);
        var problems = new List<string>();
        foreach (var (container, el) in file.ElementsIn("clusters", "criticalObjects"))
        {
            if (el.Name.LocalName != "cluster" || el.Attribute("id")?.Value is not { } id) continue;
            if (container == "clusters") clusters[id] = Cluster(id, el, problems);
            else
            {
                var objects = new Dictionary<string, CriticalObject>(StringComparer.Ordinal);
                foreach (var o in el.Elements())
                    if (o.Attribute("path")?.Value is { } path)
                        objects[path] = new CriticalObject(o.Name.LocalName, path, Id(o, "guid", $"{id} {path}", problems), (string?)o.Attribute("unused") == "true");
                critical[id] = objects;
            }
        }
        foreach (var (id, objects) in critical)
            if (clusters.TryGetValue(id, out var c)) clusters[id] = c with { Critical = objects };
        watch.Stop();
        if (problems.Count > 0)
            Log.Warn("data", $"{file.Name}: {problems.Count} value(s) unreadable, read as empty or 0 (first: {problems[0]})");
        return new WorldInfo(clusters, critical, watch.Elapsed, problems);
    }

    private static ClusterInfo Cluster(string id, XElement el, List<string> problems)
    {
        Vector2 Pos(XElement e) => TileMath.V2((string?)e.Attribute("pos")) ?? Vector2.Zero;
        IEnumerable<XElement> List(string container, string item) => el.Element(container)?.Elements(item) ?? [];
        var distribution = el.Element("distribution");
        return new ClusterInfo
        {
            Id = id,
            File = (string?)el.Attribute("file") ?? "",
            Type = (string?)el.Attribute("type") ?? "",
            DisplayName = (string?)el.Attribute("displayname") ?? "",
            Origin = TileMath.V2((string?)el.Attribute("origin")),
            Size = TileMath.V2((string?)el.Attribute("size")),
            Exits = [.. List("exits", "exit").Select(e =>
            {
                var (target, cluster) = Target((string?)e.Attribute("targetid"), $"{id} exit", problems);
                return new WorldExit(Id(e, "id", $"{id} exit", problems), Pos(e), target, cluster, (string?)e.Attribute("targettype") ?? "", Attrs(e));
            })],
            Travelpoints = [.. List("travelpoints", "travelpoint").Select(t => new WorldTravelpoint(Id(t, "id", $"{id} travelpoint", problems), Pos(t),
                [.. (t.Element("connections")?.Elements("connection") ?? []).Select(c =>
                {
                    var (target, cluster) = Target((string?)c.Attribute("to"), $"{id} travel connection", problems);
                    return new TravelConnection(target, cluster, TileMath.Float((string?)c.Attribute("costmodifier")), (string?)c.Attribute("noluggage") == "true");
                })]))],
            IslandAccessPoints = [.. List("islandaccesspoints", "islandaccesspoint")
                .Select(a => new WorldIslandAccessPoint(Id(a, "id", $"{id} islandaccesspoint", problems), Id(a, "travelpointid", $"{id} islandaccesspoint", problems)))],
            Outposts = [.. List("outposts", "outpost").Select(o => new WorldOutpost(Id(o, "id", $"{id} outpost", problems), (string?)o.Attribute("type") ?? "", Pos(o)))],
            TerritoryList = [.. List("territories", "territory").Select(t => new WorldTerritory(Id(t, "id", $"{id} territory", problems),
                (string?)t.Attribute("name") ?? "", (string?)t.Attribute("territorytype") ?? "", (string?)t.Attribute("resourcetype") ?? "",
                (string?)t.Attribute("castletype") ?? "", TileMath.V2((string?)t.Attribute("center")) ?? Vector2.Zero,
                TileMath.V2((string?)t.Attribute("size")) ?? Vector2.Zero, TileMath.V2((string?)t.Attribute("monolith")) ?? Vector2.Zero))],
            QuestGivers = [.. List("questgivers", "questgiver").Select(q => new WorldQuestGiver(Id(q, "id", $"{id} questgiver", problems), (string?)q.Attribute("type") ?? "", Pos(q)))],
            MinimapMarkers = [.. List("minimapmarkers", "marker").Select(m => new WorldMarker((string?)m.Attribute("type") ?? "", Pos(m)))],
            RealEstates = Number(distribution, "realestates", $"{id} distribution", problems),
            Distribution = [.. (distribution?.Elements("resource") ?? []).Select(r => new ResourceCount((string?)r.Attribute("name") ?? "",
                Number(r, "tier", $"{id} distribution resource", problems), Number(r, "count", $"{id} distribution resource", problems)))],
            MobCounts = [.. List("mobcounts", "mob").Select(m => new MobCount((string?)m.Attribute("name") ?? "", Number(m, "count", $"{id} mobcounts mob", problems)))],
            Attrs = Attrs(el),
        };
    }

    private static Dictionary<string, string> Attrs(XElement e) => e.Attributes().ToDictionary(a => a.Name.LocalName, a => a.Value);

    /// <summary>An int attribute; missing = 0, not an int = 0 and a problem.</summary>
    private static int Number(XElement? e, string attribute, string where, List<string> problems)
    {
        var text = (string?)e?.Attribute(attribute);
        if (string.IsNullOrWhiteSpace(text)) return 0;
        if (int.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out var i)) return i;
        problems.Add($"{where} {attribute} '{text}'");
        return 0;
    }

    private static Guid Id(XElement e, string attribute, string where, List<string> problems)
    {
        var text = (string?)e.Attribute(attribute);
        if (Guid.TryParse(text, out var g)) return g;
        problems.Add($"{where} {attribute} '{text}'");
        return Guid.Empty;
    }

    /// <summary>"&lt;guid&gt;@&lt;cluster&gt;" (exit targetid, travel connection to).</summary>
    private static (Guid Target, string Cluster) Target(string? text, string where, List<string> problems)
    {
        if (string.IsNullOrEmpty(text)) return (Guid.Empty, "");
        var at = text.IndexOf('@');
        var guidText = at < 0 ? text : text[..at];
        if (!Guid.TryParse(guidText, out var g)) problems.Add($"{where} target '{text}'");
        return (g, at < 0 ? "" : text[(at + 1)..]);
    }
}

/// <summary>One world.xml cluster (clusters/cluster) with its critical objects.</summary>
public sealed record ClusterInfo
{
    public required string Id { get; init; }

    /// <summary>The .cluster file as world.xml names it ("0000_CTY_SW_AUTO_T1_NON.cluster.xml"); the client ships it as cluster/*.cluster.bin.</summary>
    public required string File { get; init; }

    /// <summary>worldsettings clustertype (0000 = PLAYERCITY_SAFEAREA_01).</summary>
    public string Type { get; init; } = "";

    public string DisplayName { get; init; } = "";

    /// <summary>world.xml origin and size; the .cluster file's own (<see cref="ClusterTemplate.Origin"/>) is what the client uses.</summary>
    public Vector2? Origin { get; init; }

    public Vector2? Size { get; init; }

    public IReadOnlyList<WorldExit> Exits { get; init; } = [];
    public IReadOnlyList<WorldTravelpoint> Travelpoints { get; init; } = [];
    public IReadOnlyList<WorldIslandAccessPoint> IslandAccessPoints { get; init; } = [];
    public IReadOnlyList<WorldOutpost> Outposts { get; init; } = [];

    /// <summary>
    /// territories/territory in file order (stageP territories SPEC §1): the guild territories of the cluster (269 clusters have some), in the
    /// order the client's world map iterates them (WorldmapCluster.UpdateTerritories 0x181be2e30 indexes ClusterInfoUpdate 4-18 by it).
    /// </summary>
    public IReadOnlyList<WorldTerritory> TerritoryList { get; init; } = [];

    /// <summary>The number of <see cref="TerritoryList"/>.</summary>
    public int Territories => TerritoryList.Count;
    public IReadOnlyList<WorldQuestGiver> QuestGivers { get; init; } = [];
    public IReadOnlyList<WorldMarker> MinimapMarkers { get; init; } = [];

    /// <summary>distribution@realestates: the real-estate plots of the cluster.</summary>
    public int RealEstates { get; init; }

    /// <summary>distribution/resource: static harvestables per resource and tier, including those carried by the mobs of <see cref="MobCounts"/> (HIDE).</summary>
    public IReadOnlyList<ResourceCount> Distribution { get; init; } = [];

    /// <summary>mobcounts/mob: the spawn points that name their mob.</summary>
    public IReadOnlyList<MobCount> MobCounts { get; init; } = [];

    /// <summary>criticalObjects/cluster[@id]: by path "&lt;templateinstance id&gt;/&lt;member id&gt;"; empty when world.xml has none.</summary>
    public IReadOnlyDictionary<string, CriticalObject> Critical { get; init; } = new Dictionary<string, CriticalObject>();

    /// <summary>All attributes of the cluster element.</summary>
    public IReadOnlyDictionary<string, string> Attrs { get; init; } = new Dictionary<string, string>();
}

/// <summary>exits/exit: the Guid ddt.al1 registers (NewExit key 1), the position the exit tile sits at, and where it leads.</summary>
public sealed record WorldExit(Guid Id, Vector2 Position, Guid Target, string TargetCluster, string TargetType, IReadOnlyDictionary<string, string> Attrs);

public sealed record WorldTravelpoint(Guid Id, Vector2 Position, IReadOnlyList<TravelConnection> Connections);

public sealed record TravelConnection(Guid Target, string TargetCluster, float CostModifier, bool NoLuggage);

public sealed record WorldIslandAccessPoint(Guid Id, Guid Travelpoint);

public sealed record WorldOutpost(Guid Id, string Type, Vector2 Position);

/// <summary>
/// territories/territory (stageP territories SPEC §1): its Guid (also the Guid of the cluster's monolith or castle member, NewMonolithObject key 7,
/// NewCastleObject key 3), name, territorytype WATCHTOWER or CASTLE, resourcetype (Resource, Farm; "" for a castle), castletype (SMALLCASTLE = castle
/// outpost, MEDIUMCASTLE = castle; "" for a watchtower), the center and size of its area and the position of its monolith (a castle's own position).
/// </summary>
public sealed record WorldTerritory(Guid Id, string Name, string Type, string ResourceType, string CastleType, Vector2 Center, Vector2 Size, Vector2 Monolith)
{
    public bool IsCastle => Type == "CASTLE";
}

public sealed record WorldQuestGiver(Guid Id, string Type, Vector2 Position);

public sealed record WorldMarker(string Type, Vector2 Position);

public sealed record ResourceCount(string Resource, int Tier, int Count);

public sealed record MobCount(string Mob, int Count);

/// <summary>criticalObjects/cluster/&lt;type path guid unused?&gt;.</summary>
public sealed record CriticalObject(string Type, string Path, Guid Guid, bool Unused);
