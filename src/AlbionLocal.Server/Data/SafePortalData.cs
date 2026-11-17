using System.Globalization;
using System.Xml.Linq;

namespace AlbionLocal.Server.Data;

/// <summary>
/// outlandsteleportationportals.xml, the S.A.F.E. portals of the Outlands portal towns (analysis/work/stageR/safe-portal/SPEC.md, "SPEC"
/// here, §1): the client's reader brn.da 0x18073C370 (root "outlandsteleportationportals", &lt;settings&gt; read by brn.abo 0x18073C190 with
/// the defaults kept below, &lt;destinationselection&gt; by brm) and the cost formula of PortalTravelCostDataComponent (bel.m 0x180625E80,
/// shared client/server code). Immutable.
/// </summary>
public sealed class SafePortalData
{
    /// <summary>returnportaltile: the tile NewOutlandsTeleportationReturnPortal key 4 names (cng ctor: a9f.u(name), placed at keys 1/2).</summary>
    public string ReturnPortalTile { get; private init; } = "OUTLANDS_TELEPORTATION_RETURN_PORTAL";

    /// <summary>returndurationinsec (5400): how long the return portal stands and the binding lasts.</summary>
    public double ReturnSeconds { get; private init; } = 5400;

    /// <summary>portalusageblockerdebuff (OUTLANDS_TELEPORTATION_PORTAL_BLOCKER_DEBUFF: cannotuseobject outlandsteleportationportal 30 s).</summary>
    public string BlockerDebuff { get; private init; } = "OUTLANDS_TELEPORTATION_PORTAL_BLOCKER_DEBUFF";

    /// <summary>portalusagebindingbuff (OUTLANDS_TELEPORTATION_PORTAL_BINDING_BUFF: the SAFE_PORTAL icon for 5400 s).</summary>
    public string BindingBuff { get; private init; } = "OUTLANDS_TELEPORTATION_PORTAL_BINDING_BUFF";

    /// <summary>mintravelcosts (10000 silver): the lowest baggage cost; a7m raw.</summary>
    public long MinCost { get; private init; } = 10_000 * 10_000L;

    /// <summary>emvcostfactor (0.05): the share of an item's estimated market value.</summary>
    public double EmvFactor { get; private init; } = 0.05;

    /// <summary>toolscostscalingfactor (0.5): the factor on a tool's cost.</summary>
    public double ToolsFactor { get; private init; } = 0.5;

    /// <summary>The item value curve (bel.m(double, int) 0x180625780): ivbase, ivcoefficient, ivpower, ivboundconstant, ivmin, ivmax.</summary>
    public double IvBase { get; private init; } = 0.98224197;
    public double IvCoefficient { get; private init; } = 102.43388085;
    public double IvPower { get; private init; } = 0.16148053;
    public double IvBound { get; private init; } = 100;
    public double IvMin { get; private init; }
    public double IvMax { get; private init; } = 121344;

    /// <summary>globalcostfactor (1 when absent): the factor on the extra cost the caller adds.</summary>
    public double GlobalFactor { get; private init; } = 1;

    /// <summary>costresettime (1800 s): a use counts for the repeat-use tax this long.</summary>
    public double CostResetSeconds { get; private init; } = 1800;

    /// <summary>reusecostfactor (1.05): the cost × this per use within <see cref="CostResetSeconds"/>.</summary>
    public double ReuseFactor { get; private init; } = 1.05;

    /// <summary>returnportalvisiblityrange (16).</summary>
    public double ReturnVisibilityRange { get; private init; } = 16;

    /// <summary>&lt;destinationselection minimumportalspawndistance&gt; (90 m).</summary>
    public double MinSpawnDistance { get; private init; } = 90;

    /// <summary>&lt;portal cluster&gt; → its &lt;destination cluster&gt;s, in file order (the clusters the client highlights, cnf.aqs).</summary>
    public IReadOnlyDictionary<string, IReadOnlyList<string>> Destinations { get; private init; } = new Dictionary<string, IReadOnlyList<string>>();

    public static SafePortalData Load(XElement root)
    {
        var s = root.Element("settings");
        double D(string name, double fallback) =>
            double.TryParse((string?)s?.Attribute(name), NumberStyles.Float, CultureInfo.InvariantCulture, out var v) ? v : fallback;
        string S(string name, string fallback) => (string?)s?.Attribute(name) is { Length: > 0 } v ? v : fallback;
        var d = new SafePortalData();
        var selection = root.Element("destinationselection");
        var destinations = new Dictionary<string, IReadOnlyList<string>>(StringComparer.Ordinal);
        foreach (var p in selection?.Elements("portal") ?? [])
            if ((string?)p.Attribute("cluster") is { Length: > 0 } town)
                destinations[town] = [.. p.Elements("destination").Select(x => (string?)x.Attribute("cluster")).OfType<string>().Where(x => x.Length > 0)];
        return new SafePortalData
        {
            ReturnPortalTile = S("returnportaltile", d.ReturnPortalTile),
            ReturnSeconds = D("returndurationinsec", d.ReturnSeconds),
            BlockerDebuff = S("portalusageblockerdebuff", d.BlockerDebuff),
            BindingBuff = S("portalusagebindingbuff", d.BindingBuff),
            MinCost = (long)Math.Round(D("mintravelcosts", 10_000) * 10_000),
            EmvFactor = D("emvcostfactor", 0.1),
            ToolsFactor = D("toolscostscalingfactor", 1),
            IvBase = D("ivbase", d.IvBase), IvCoefficient = D("ivcoefficient", d.IvCoefficient), IvPower = D("ivpower", d.IvPower),
            IvBound = D("ivboundconstant", d.IvBound), IvMin = D("ivmin", 0), IvMax = D("ivmax", d.IvMax),
            GlobalFactor = D("globalcostfactor", 1),
            CostResetSeconds = D("costresettime", d.CostResetSeconds),
            ReuseFactor = D("reusecostfactor", 1),
            ReturnVisibilityRange = D("returnportalvisiblityrange", 15),
            MinSpawnDistance = double.TryParse((string?)selection?.Attribute("minimumportalspawndistance"), NumberStyles.Float, CultureInfo.InvariantCulture, out var min) ? min : 90,
            Destinations = destinations,
        };
    }

    /// <summary>
    /// The item value curve (bel.m(double, int) 0x180625780): v = clamp(value − ivmin, 0, ivmax); cost = (1 / ivbase^(ivcoefficient · v^ivpower
    /// + ivboundconstant) − 1) × count, in silver (a7m raw here).
    /// </summary>
    public long CurveCost(double itemValue, int count)
    {
        var v = Math.Min(Math.Max(itemValue - IvMin, 0), IvMax);
        var p = Math.Pow(IvBase, IvCoefficient * Math.Pow(v, IvPower) + IvBound);
        if (p <= 0) return 0;
        var silver = 1 / p - 1;
        return silver <= 0 ? 0 : (long)Math.Round(silver * 10_000) * Math.Max(1, count);
    }

    /// <summary>
    /// The baggage cost (bel.m_t338a 0x1806262F0): per item max(emv × emvcostfactor × count, curve(itemvalue, count)), a tool's × toolscostscalingfactor;
    /// summed, + <paramref name="extra"/> × globalcostfactor, at least mintravelcosts. <paramref name="items"/>: (item value, estimated market value
    /// a7m raw, count, is a tool).
    /// </summary>
    public long InitialCost(IEnumerable<(double ItemValue, long Emv, int Count, bool Tool)> items, long extra = 0)
    {
        long sum = 0;
        foreach (var (value, emv, count, tool) in items)
        {
            var byEmv = (long)Math.Round(emv * EmvFactor) * Math.Max(1, count);
            var byCurve = CurveCost(value, count);
            if (tool)
            {
                byEmv = (long)Math.Round(byEmv * ToolsFactor);
                byCurve = (long)Math.Round(byCurve * ToolsFactor);
            }
            sum = checked(sum + Math.Max(byEmv, byCurve));
        }
        sum = checked(sum + (long)Math.Round(extra * GlobalFactor));
        return Math.Max(sum, MinCost);
    }

    /// <summary>
    /// The repeat-use tax (bel.m 0x180625E80): the uses of <paramref name="history"/> (GameTimeStamps) less than costresettime before
    /// <paramref name="now"/> count n; the travel cost = <paramref name="initial"/> × reusecostfactor^n, and the next price change = the oldest
    /// counted use + costresettime (null without one).
    /// </summary>
    public (long Cost, int Uses, long? NextChange) TravelCost(long initial, IEnumerable<long> history, long now)
    {
        var window = TimeSpan.FromSeconds(CostResetSeconds).Ticks;
        var counted = history.Where(t => now - t < window).ToList();
        if (counted.Count == 0 || ReuseFactor == 1) return (initial, counted.Count, counted.Count == 0 ? null : counted.Min() + window);
        return ((long)Math.Round(initial * Math.Pow(ReuseFactor, counted.Count)), counted.Count, counted.Min() + window);
    }
}
