using System.Globalization;
using System.Xml.Linq;
using AlbionLocal.Photon;

namespace AlbionLocal.Server.Data;

/// <summary>
/// The harvestable types of harvestables.xml (<see cref="HarvestableTemplate"/>), by type id
/// (<see cref="ClientNumbering.Harvestables"/>) and by name ignoring case: what a static tree or rock tile of a cluster
/// template is (its harvestable component names type and tier), and what NewSimpleHarvestableObjectList keys 1, 2 and 4
/// carry. With the numbers gathering runs on (stage 4, World/Harvesting.cs): the tool time factors, per tier the harvest
/// time, the yield and regrowth of each charge level.
/// </summary>
public sealed class HarvestableStore
{
    private readonly HarvestableTemplate[] _byId;
    private readonly Dictionary<string, HarvestableTemplate> _byName = new(StringComparer.OrdinalIgnoreCase);

    public HarvestableStore(IEnumerable<HarvestableTemplate> templates)
    {
        _byId = [.. templates];
        foreach (var t in _byId)
            if (!_byName.TryAdd(t.Name, t)) Log.Warn("data", $"harvestable {t.Name} twice (ids {_byName[t.Name].Id} and {t.Id}); the name finds the first");
    }

    public IReadOnlyList<HarvestableTemplate> All => _byId;

    public int Count => _byId.Length;

    public HarvestableTemplate? this[int id] => id >= 0 && id < _byId.Length ? _byId[id] : null;

    public HarvestableTemplate? Find(string name) => _byName.GetValueOrDefault(name);

    /// <summary>
    /// harvestables.xml, numbered by <see cref="ClientNumbering.Harvestables"/>. A number that does not parse reads as its
    /// default and is warned once per attribute (<see cref="NumberReader"/>).
    /// </summary>
    public static HarvestableStore Load(GameDataFile file)
    {
        var numbers = new NumberReader(file.Name);
        var store = new HarvestableStore(ClientNumbering.Harvestables(file.Elements()).Select(h =>
        {
            var name = (string?)h.Element.Attribute("name") ?? "";
            // <ToolModifier><Modifier tierdifference="-1" timefactor="1.5"/>…: "harvesting is only possible if a tool modifier
            // is given" (the file's own comment), so a tier difference without a Modifier refuses that tool.
            var factors = new SortedDictionary<int, double>();
            foreach (var m in h.Element.Elements("ToolModifier").Elements("Modifier"))
                factors[numbers.Int((string?)m.Attribute("tierdifference"), "tierdifference", name)] =
                    numbers.Num((string?)m.Attribute("timefactor"), "timefactor", name, 1);
            var tiers = new SortedDictionary<int, HarvestableTier>();
            foreach (var t in h.Element.Elements("Tier"))
            {
                var attrs = t.Attributes().ToDictionary(a => a.Name.LocalName, a => a.Value);
                var tier = TileMath.Int(attrs.GetValueOrDefault("tier"));
                var where = $"{name} T{tier}";
                // <Charge level="1-2" yield="1" respawnfactormin="0.5" respawnfactormax="1.5" respawncharges="1"/>: one entry per
                // level range; level 0 is the empty state. A level without respawn factors regrows at factor 1 (WOOD_GIANTTREE
                // T1 levels 1-5), without respawncharges by 1.
                var levels = t.Elements("Charge").Select(c =>
                {
                    var level = (string?)c.Attribute("level") ?? "";
                    var dash = level.IndexOf('-');
                    int from = TileMath.Int(dash < 0 ? level : level[..dash]), to = TileMath.Int(level[(dash + 1)..]);
                    double Num(string attribute, double fallback) => numbers.Num((string?)c.Attribute(attribute), attribute, where, fallback);
                    return new HarvestableCharge(from, to, (int)Num("yield", 0), Num("respawnfactormin", 1), Num("respawnfactormax", 1),
                        (int)Num("respawncharges", 1)) { ExactYield = Num("yield", 0) };
                }).ToArray();
                // Full charges: the upper bound of the highest Charge level ("1-2" → 2); WOOD T1 2, WOOD_GIANTTREE T1 5 (SPEC §3).
                var charges = levels.Select(l => l.To).DefaultIfEmpty(0).Max();
                tiers[tier] = new HarvestableTier(tier, attrs.GetValueOrDefault("item"), charges, TileMath.Int(attrs.GetValueOrDefault("startcharges")),
                    double.TryParse(attrs.GetValueOrDefault("respawntimeseconds"), NumberStyles.Float, CultureInfo.InvariantCulture, out var s) ? s : 0, attrs)
                {
                    HarvestSeconds = numbers.Num(attrs.GetValueOrDefault("harvesttimeseconds"), "harvesttimeseconds", where),
                    // Absent on the tiers of 5 and up and on ROCK T1, which needs no tool: by hand at the base time there.
                    NoToolTimeFactor = numbers.Num(attrs.GetValueOrDefault("notooltimefactor"), "notooltimefactor", where, 1),
                    RequiresTool = attrs.GetValueOrDefault("requirestool") == "true",
                    MaxChargesPerHarvest = Math.Max(1, numbers.Int(attrs.GetValueOrDefault("maxchargesperharvest"), "maxchargesperharvest", where, 1)),
                    Levels = levels,
                    // <RareState state="1" item="T4_WOOD_LEVEL1">: what an enchanted node of the tier gives (T4 and up).
                    RareItems = t.Elements("RareState").Where(r => (string?)r.Attribute("item") is not null)
                        .GroupBy(r => TileMath.Int((string?)r.Attribute("state"))).ToDictionary(g => g.Key, g => (string)g.First().Attribute("item")!),
                    // <Loot><LootListReference name="T4_WOOD_SKILLBOOK" chance="0.002"/></Loot>: the bonus loot (the gathering tomes, econ SPEC §2.1).
                    Loot = t.Element("Loot"),
                    RareLoot = t.Elements("RareState").Where(r => r.Element("Loot") is not null)
                        .GroupBy(r => TileMath.Int((string?)r.Attribute("state"))).ToDictionary(g => g.Key, g => g.First().Element("Loot")!),
                };
            }
            return new HarvestableTemplate
            {
                Id = h.Id,
                Name = name,
                Resource = (string?)h.Element.Attribute("resource"),
                ToolTimeFactors = factors,
                Tiers = tiers,
                Attrs = h.Element.Attributes().ToDictionary(a => a.Name.LocalName, a => a.Value),
            };
        }).ToList());
        numbers.Report();
        return store;
    }
}

/// <summary>One &lt;Harvestable&gt; of harvestables.xml.</summary>
public sealed record HarvestableTemplate
{
    /// <summary>NewSimpleHarvestableObjectList key 1, NewHarvestableObject key 5, HarvestStart key 4.</summary>
    public required byte Id { get; init; }

    public required string Name { get; init; }

    /// <summary>
    /// resource: what world.xml distribution counts it as (WOOD_GIANTTREE is WOOD), and what a tool's items.xml
    /// &lt;canharvest resourcetype&gt; must name; null for the silver nodes.
    /// </summary>
    public string? Resource { get; init; }

    /// <summary>
    /// &lt;ToolModifier&gt;: harvest time factor by tier difference (tool tier − resource tier; -1 1.5, 0 1, +1 0.7, +2 0.5, +3
    /// 0.35, +4 0.25, then 0.15). A difference not listed cannot harvest (below -1 for every type).
    /// </summary>
    public IReadOnlyDictionary<int, double> ToolTimeFactors { get; init; } = new Dictionary<int, double>();

    /// <summary>By Tier@tier (key 2); WOOD has 1-8, FIBER and ORE 2-8.</summary>
    public IReadOnlyDictionary<int, HarvestableTier> Tiers { get; init; } = new Dictionary<int, HarvestableTier>();

    public IReadOnlyDictionary<string, string> Attrs { get; init; } = new Dictionary<string, string>();
}

/// <summary>
/// One &lt;Tier&gt; of a harvestable type; <see cref="Charges"/> = full charges (key 4 of a fresh one). The harvest numbers
/// (World/Harvesting.cs; HARVEST.md §4, every captured value fits): duration = <see cref="HarvestSeconds"/> × the tool's
/// time factor, by hand × <see cref="NoToolTimeFactor"/>; one harvest takes up to <see cref="MaxChargesPerHarvest"/>
/// charges and gives the yield of the current <see cref="Levels"/> entry per charge taken.
/// </summary>
public sealed record HarvestableTier(int Tier, string? Item, int Charges, int StartCharges, double RespawnSeconds, IReadOnlyDictionary<string, string> Attrs)
{
    /// <summary>harvesttimeseconds (WOOD T1 1, T2 2, T3 3, T4 4; FIBER the same).</summary>
    public double HarvestSeconds { get; init; }

    /// <summary>notooltimefactor: the time factor by hand (2 where given; 1 when absent).</summary>
    public double NoToolTimeFactor { get; init; } = 1;

    /// <summary>requirestool: false for the T1 trees and rocks, true from T2.</summary>
    public bool RequiresTool { get; init; }

    /// <summary>maxchargesperharvest (WOOD T1 2: one harvest takes both charges; 1 for most others).</summary>
    public int MaxChargesPerHarvest { get; init; } = 1;

    /// <summary>The &lt;Charge&gt; levels in file order (level 0 = empty first).</summary>
    public IReadOnlyList<HarvestableCharge> Levels { get; init; } = [];

    /// <summary>
    /// The item of each rare (enchantment) state 1-3 (&lt;RareState state item&gt;; T4_WOOD_LEVEL1, …): only tiers that have
    /// them can be enchanted (stage 4b SPEC §5.4). Empty below T4.
    /// </summary>
    public IReadOnlyDictionary<int, string> RareItems { get; init; } = new Dictionary<int, string>();

    /// <summary>
    /// The tier's &lt;Loot&gt; (a loot.xml roll, <see cref="LootTables.RollMob"/>): the bonus loot of one harvest. Every one names a
    /// Tn_&lt;RESOURCE&gt;_SKILLBOOK list (a gathering tome) with chance 0.001 × yield × maxchargesperharvest (loot.xml's own comment), so
    /// one roll per harvest; null = none (T1-T3).
    /// </summary>
    public System.Xml.Linq.XElement? Loot { get; init; }

    /// <summary>The &lt;Loot&gt; of each rare state (the same lists, at the enchanted yields' chances).</summary>
    public IReadOnlyDictionary<int, System.Xml.Linq.XElement> RareLoot { get; init; } = new Dictionary<int, System.Xml.Linq.XElement>();

    /// <summary>The bonus loot of a node in <paramref name="rareState"/>: its RareState's, else the tier's.</summary>
    public System.Xml.Linq.XElement? LootOf(int rareState) => rareState > 0 && RareLoot.TryGetValue(rareState, out var rare) ? rare : Loot;

    /// <summary>The item a harvest of a node in <paramref name="rareState"/> gives: its RareState item, else <see cref="Item"/>.</summary>
    public string? ItemOf(int rareState) => rareState > 0 && RareItems.TryGetValue(rareState, out var rare) ? rare : Item;

    /// <summary>The level entry holding <paramref name="charges"/>; null when none does.</summary>
    public HarvestableCharge? Level(int charges) => Levels.FirstOrDefault(l => charges >= l.From && charges <= l.To);
}

/// <summary>
/// One &lt;Charge level="From-To"&gt; of a tier: <see cref="Yield"/> items per charge harvested at these levels; the time to
/// the next charge is the tier's respawntimeseconds × a factor between <see cref="RespawnFactorMin"/> and
/// <see cref="RespawnFactorMax"/> (the file: "RespawnFactor of charge level n affects time to reach charge level n+1"),
/// which adds <see cref="RespawnCharges"/>.
/// </summary>
public sealed record HarvestableCharge(int From, int To, int Yield, double RespawnFactorMin, double RespawnFactorMax, int RespawnCharges)
{
    /// <summary>The yield as written (SILVERCOINS_LOOT_ROAMING T4 level 1: 22.5, the base of a mob's silver pile, stage 5c SPEC §5c.3).</summary>
    public double ExactYield { get; init; }
}
