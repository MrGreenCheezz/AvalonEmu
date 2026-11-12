using System.Globalization;
using System.Xml.Linq;

namespace AlbionLocal.Server.Data;

/// <summary>
/// treasuredistribution.xml as the open-world treasure chests use it (public issue #19, analysis/work/coffers/SPEC.md §2): the
/// &lt;TreasureLootchest&gt; definitions (uniquename → the lootchests.xml LootChestSpawnpoint it spawns, maxlifetime, the world-map
/// treasure) and the &lt;Distribute continent subtype [clustertype]&gt; entries of the subtypes COFFER and CHEST, per &lt;ClusterTier&gt;
/// one &lt;Random&gt; draw: maxslotsused (treasures at once in the cluster), activationtimemin/max (seconds until a free slot's next
/// draw), positioncooldown (seconds a spot stays unused after its treasure), activationchance and the weighted entries. Read with the
/// first open-world cluster that has a treasure spot (<see cref="ClusterStore.Treasures"/>); immutable.
/// </summary>
public sealed class TreasureDistributionData
{
    /// <summary>The subtypes this server places (the tiles' treasurespawnpoint subtype): the coffers and the treasure chests.</summary>
    public static readonly IReadOnlyList<string> Subtypes = ["COFFER", "CHEST"];

    /// <summary>&lt;TreasureLootchest&gt; by uniquename.</summary>
    public IReadOnlyDictionary<string, TreasureLootchest> Lootchests { get; private init; } = new Dictionary<string, TreasureLootchest>();

    /// <summary>The &lt;Distribute&gt; entries of <see cref="Subtypes"/>, in file order.</summary>
    public IReadOnlyList<TreasureDistribute> Distributes { get; private init; } = [];

    /// <summary>
    /// The treasure kinds of the client's list (bfz.jo; the element names its entry parser bfz.abo(XmlElement, bfv) and the definition
    /// sections create one per name of).
    /// </summary>
    public static readonly IReadOnlyList<string> ClientKinds =
    [
        "TreasureMob", "TreasurePile", "TreasureDragon", "TreasureShrine", "TreasureHuntTrack", "TreasureLootchest", "TreasureCagedObject",
        "TreasureDestination", "TreasureHarvestable", "TreasureDragonAreaEntrance",
    ];

    private IReadOnlyDictionary<(string Kind, string Name), int> _clientIndex = new Dictionary<(string, string), int>();

    /// <summary>
    /// Public issue #23: the index of a treasure definition in the client's list bfz.jo, which LocalTreasuresUpdate key 2 and ClusterInfoUpdate
    /// key 52 carry (bfz.abo(int) 0x18066D750, a plain list index). bfz.c8 0x1806763F0 adds bf1.NONE (0) and HUNTSPOTGENERIC (1), then reads the
    /// sections in file order, GameobjectDistribution first; every entry or definition of a kind adds its name the first time it comes
    /// (per kind, bfz.abo(bf1) 0x18066E000): document order. 0 (NONE) for a name the file lacks, which the client drops.
    /// </summary>
    public int ClientIndex(string kind, string name) => _clientIndex.GetValueOrDefault((kind, name));

    /// <summary>
    /// The draw of <paramref name="subtype"/> for a cluster of <paramref name="continent"/> (Royal, Outlands, Avalon), world.xml cluster type
    /// <paramref name="clusterType"/> and <paramref name="tier"/>: the first &lt;Distribute&gt; of that continent and subtype whose clustertype is
    /// the cluster's (one without a clustertype takes every type) and that has the tier; null = none (no treasure of that subtype there).
    /// </summary>
    public TreasureDraw? DrawFor(string subtype, string continent, string clusterType, int tier) =>
        Distributes.Where(d => d.Subtype == subtype && string.Equals(d.Continent, continent, StringComparison.OrdinalIgnoreCase)
                && (d.ClusterType is null || d.ClusterType == clusterType))
            .OrderBy(d => d.ClusterType is null) // a clustertype's own entry before a continent-wide one
            .Select(d => d.Tiers.GetValueOrDefault(tier)).FirstOrDefault(t => t is not null);

    public static TreasureDistributionData Load(XElement root)
    {
        static double D(XElement e, string a, double fallback) =>
            double.TryParse((string?)e.Attribute(a), NumberStyles.Float, CultureInfo.InvariantCulture, out var v) ? v : fallback;
        var chests = new Dictionary<string, TreasureLootchest>(StringComparer.Ordinal);
        foreach (var c in root.Element("TreasureLootchests")?.Elements("TreasureLootchest") ?? [])
            if ((string?)c.Attribute("uniquename") is { Length: > 0 } name && (string?)c.Attribute("lootchest") is { Length: > 0 } spawnpoint)
                chests.TryAdd(name, new TreasureLootchest(name, spawnpoint, (string?)c.Attribute("chestcategory") ?? "", D(c, "maxlifetime", 86400))
                {
                    WorldMapTreasure = (string?)c.Attribute("worldmaptreasure"), WorldMapRange = (int)D(c, "worldmaprange", -1),
                    NotInConflictZone = (string?)c.Attribute("notinconflictzone") == "true",
                });
        var distributes = new List<TreasureDistribute>();
        foreach (var d in root.Element("GameobjectDistribution")?.Elements("Distribute") ?? [])
        {
            var subtype = ((string?)d.Attribute("subtype") ?? "").ToUpperInvariant();
            if (!Subtypes.Contains(subtype)) continue;
            var tiers = new Dictionary<int, TreasureDraw>();
            foreach (var t in d.Elements("ClusterTier"))
            {
                if (t.Element("Random") is not { } r) continue; // the coffers and chests draw at random (the castle chests are Events, TerritoryData)
                var entries = r.Elements("TreasureLootchest").Select(x => ((string?)x.Attribute("name") ?? "", D(x, "weight", 1)))
                    .Where(x => x.Item1.Length > 0 && x.Item2 > 0).ToList();
                tiers.TryAdd((int)D(t, "tier", 0), new TreasureDraw(
                    (int)D(r, "maxslotsused", 1), D(r, "activationtimemin", 0), D(r, "activationtimemax", 0), D(r, "positioncooldown", 0),
                    D(r, "activationchance", 1), entries, r.Elements("None").Sum(x => D(x, "weight", 0))));
            }
            distributes.Add(new TreasureDistribute((string?)d.Attribute("continent") ?? "", subtype, (string?)d.Attribute("clustertype"), tiers));
        }
        if (chests.Count == 0) throw new InvalidDataException("treasuredistribution.bin: no <TreasureLootchest>");
        var index = new Dictionary<(string, string), int>();
        foreach (var e in root.Descendants().Where(e => ClientKinds.Contains(e.Name.LocalName)))
            if (((string?)e.Attribute("uniquename") ?? (string?)e.Attribute("name")) is { Length: > 0 } name)
                index.TryAdd((e.Name.LocalName, name), index.Count + 2); // after NONE and HUNTSPOTGENERIC
        return new TreasureDistributionData { Lootchests = chests, Distributes = distributes, _clientIndex = index };
    }
}

/// <summary>
/// A &lt;TreasureLootchest&gt;: its name, the LootChestSpawnpoint of lootchests.xml it spawns, its category (Coffer, Chest) and maxlifetime (seconds an
/// unopened one stands, 86 400); the veteran and elite chests also name a world-map treasure (worldmaptreasure, worldmaprange: issue #23, TreasureCoffers.FillWorldMap).
/// </summary>
public sealed record TreasureLootchest(string Name, string Spawnpoint, string Category, double MaxLifetimeSeconds)
{
    public string? WorldMapTreasure { get; init; }

    public int WorldMapRange { get; init; } = -1;

    /// <summary>notinconflictzone (the coffers and solo chests; a faction warfare conflict zone is not run here).</summary>
    public bool NotInConflictZone { get; init; }
}

/// <summary>One &lt;Distribute&gt;: continent, subtype, the clustertype it is limited to (null = every type of the continent) and its draws by cluster tier.</summary>
public sealed record TreasureDistribute(string Continent, string Subtype, string? ClusterType, IReadOnlyDictionary<int, TreasureDraw> Tiers);

/// <summary>
/// A &lt;Random&gt; draw of a cluster tier: <paramref name="Slots"/> treasures at once (maxslotsused), a free slot's next draw uniform(<paramref name="MinSeconds"/>,
/// <paramref name="MaxSeconds"/>) later, a spot unused for <paramref name="CooldownSeconds"/> after its treasure, <paramref name="Chance"/> that a draw places one,
/// and the weighted &lt;TreasureLootchest&gt; names (with the &lt;None&gt; weight, a draw of nothing).
/// </summary>
public sealed record TreasureDraw(int Slots, double MinSeconds, double MaxSeconds, double CooldownSeconds, double Chance,
    IReadOnlyList<(string Name, double Weight)> Entries, double NoneWeight);
