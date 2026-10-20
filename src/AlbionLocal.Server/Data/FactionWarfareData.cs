using System.Globalization;
using System.Xml.Linq;

namespace AlbionLocal.Server.Data;

/// <summary>
/// factionwarfare.xml and currencies.xml, the PvE side (analysis/work/stageN/faction/SPEC.md, "SPEC" here, §1): the six factions (flagging
/// name, home city, currency), the enlistment cooldown, the faction point multipliers of PvE / gathering / fishing fame, the standing's
/// ranks and the clusters of the faction warfare duchies with their cluster types. Read lazily by <see cref="Systems.Factions"/>; immutable.
/// </summary>
public sealed class FactionWarfareData
{
    /// <summary>A &lt;faction&gt;: its FlaggingStatus (1 Highland … 6 Caerleon), home cluster and currency (FACTION_HIGHLAND …).</summary>
    public sealed record Faction(byte Flag, string Name, string City, string Currency);

    /// <summary>
    /// A &lt;rank&gt; of &lt;factionstanding&gt;: the standing it starts at (whole points), its pointmultiplier (× the points earned) and
    /// standingmultiplier (× the standing they give: the two cancel, so the standing grows with the base points).
    /// </summary>
    public sealed record Rank(long MinStanding, double PointMultiplier, double StandingMultiplier);

    /// <summary>The factions by FlaggingStatus 1-6 (index 0 unused).</summary>
    public IReadOnlyList<Faction?> Factions { get; }

    /// <summary>The ranks, ascending; rank 1 = index 0.</summary>
    public IReadOnlyList<Rank> Ranks { get; }

    /// <summary>factionstanding max (whole points).</summary>
    public long MaxStanding { get; }

    /// <summary>global factionenlistmentcooldowninmins (1440).</summary>
    public double EnlistCooldownMinutes { get; }

    /// <summary>factionpoints scalingmultiplier: pve 0.007, gathering 0.1, fishing 0.1 (× the fame).</summary>
    public double PvePoints { get; }
    public double GatheringPoints { get; }
    public double FishingPoints { get; }

    /// <summary>The cluster type of every cluster factionwarfare.xml names (clustertypeoverride).</summary>
    public IReadOnlyDictionary<string, string> ClusterTypes { get; }

    /// <summary>The duchy's defaultfaction of every cluster a duchy includes (the warfare zones, the cities too).</summary>
    public IReadOnlyDictionary<string, byte> ClusterFaction { get; }

    /// <summary>currencies.xml's index of each currency (UpdateCurrency key 2, Join key 104): FACTION_FOREST 1 … FACTION_CAERLEON 6, FAVOR 7.</summary>
    public IReadOnlyDictionary<string, int> CurrencyIndex { get; }

    private FactionWarfareData(IReadOnlyList<Faction?> factions, IReadOnlyList<Rank> ranks, long max, double cooldown, double pve, double gathering, double fishing,
        IReadOnlyDictionary<string, string> types, IReadOnlyDictionary<string, byte> clusterFaction, IReadOnlyDictionary<string, int> currencies)
    {
        (Factions, Ranks, MaxStanding, EnlistCooldownMinutes, PvePoints, GatheringPoints, FishingPoints) = (factions, ranks, max, cooldown, pve, gathering, fishing);
        (ClusterTypes, ClusterFaction, CurrencyIndex) = (types, clusterFaction, currencies);
    }

    /// <summary>The FlaggingStatus of a faction name (Highland, forest, …; case ignored); 0 = none.</summary>
    public static byte FlagOf(string? name) => name?.Trim().ToLowerInvariant() switch
    {
        "highland" => 1, "forest" => 2, "steppe" => 3, "mountain" => 4, "swamp" => 5, "caerleon" => 6, _ => 0,
    };

    /// <summary>The faction of FlaggingStatus <paramref name="flag"/>; null for 0, Red or one the file lacks.</summary>
    public Faction? Of(byte flag) => flag is >= 1 and <= 6 ? Factions[flag] : null;

    /// <summary>The rank (1-based) of a standing of <paramref name="raw"/> (a7m).</summary>
    public int RankOf(long raw)
    {
        var rank = 0;
        for (var i = 0; i < Ranks.Count && raw / 10000 >= Ranks[i].MinStanding; i++) rank = i + 1;
        return Math.Max(1, rank);
    }

    /// <summary>
    /// Whether PvE in <paramref name="cluster"/> earns faction points (SPEC §2): a cluster of a warfare duchy whose type has a value (FW_*_SAFE_*,
    /// FW_LETHAL_*), not FW_NO_VALUE, a city or Caerleon.
    /// </summary>
    public bool IsWarzone(string cluster) => ClusterTypes.TryGetValue(cluster, out var type) && ClusterFaction.ContainsKey(cluster)
        && type.StartsWith("FW_", StringComparison.Ordinal) && !type.StartsWith("FW_CITY", StringComparison.Ordinal) && type is not ("FW_NO_VALUE" or "FW_CAERLEON");

    public static FactionWarfareData Load(XElement root, XElement currencies)
    {
        static string S(XElement e, string a) => ((string?)e.Attribute(a) ?? "").Trim();
        static double D(XElement e, string a, double fallback = 0) => double.TryParse(S(e, a), NumberStyles.Float, CultureInfo.InvariantCulture, out var d) ? d : fallback;
        var factions = new Faction?[7];
        foreach (var f in root.Element("factions")?.Elements("faction") ?? [])
            if (FlagOf(S(f, "flagging")) is var flag and > 0) factions[flag] = new Faction(flag, S(f, "flagging"), S(f, "city"), S(f, "currency"));
        var global = root.Element("global");
        var points = root.Element("factionpoints")?.Element("scalingmultiplier");
        var standing = root.Element("factionstanding");
        var ranks = (standing?.Elements("rank") ?? []).Select(r => new Rank((long)D(r, "minstanding"), D(r, "pointmultiplier", 1), D(r, "standingmultiplier", 1)))
            .OrderBy(r => r.MinStanding).ToList();
        if (ranks.Count == 0) ranks.Add(new Rank(0, 1, 1));
        var types = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var o in root.Elements("clustertypeoverride")) types[S(o, "clusterid")] = S(o, "type");
        var clusterFaction = new Dictionary<string, byte>(StringComparer.Ordinal);
        foreach (var d in root.Elements("duchy"))
            foreach (var i in d.Elements("include")) clusterFaction[S(i, "clusterid")] = FlagOf(S(d, "defaultfaction"));
        var index = new Dictionary<string, int>(StringComparer.Ordinal);
        var n = 0;
        foreach (var c in currencies.Elements("currency")) index[S(c, "uniquename")] = n++;
        return new FactionWarfareData(factions, ranks, standing is null ? 7_500_000 : (long)D(standing, "max", 7_500_000),
            global is null ? 1440 : D(global, "factionenlistmentcooldowninmins", 1440),
            points is null ? 0.007 : D(points, "pve", 0.007), points is null ? 0.1 : D(points, "gathering", 0.1), points is null ? 0.1 : D(points, "fishing", 0.1),
            types, clusterFaction, index);
    }
}
