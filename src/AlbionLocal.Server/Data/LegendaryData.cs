using System.Globalization;
using System.Text.RegularExpressions;
using System.Xml.Linq;

namespace AlbionLocal.Server.Data;

/// <summary>A trait of the &lt;traitpool&gt;: its passive spell (PASSIVE_TRAIT_*), rarity weight and the item keywords it needs.</summary>
public sealed record TraitDef(string Name, string Spell, int Weight, IReadOnlySet<string> RequiresOneOf, IReadOnlySet<string> RequiresAll)
{
    /// <summary>Whether an item group with <paramref name="keywords"/> may roll it (requiresOneOfKeywords / requiresKeywords).</summary>
    public bool Fits(IReadOnlySet<string> keywords) =>
        (RequiresOneOf.Count == 0 || RequiresOneOf.Any(keywords.Contains)) && RequiresAll.All(keywords.Contains);
}

/// <summary>An &lt;itemgroup&gt;: the weapons (T?_ patterns) that can be awakened, their trait count and keywords.</summary>
public sealed record LegendaryGroup(IReadOnlyList<Regex> Items, int BaseTraits, IReadOnlySet<string> Keywords);

/// <summary>
/// legendaryitems.bin (awakened weapons, analysis/work/stageN/awakening/SPEC.md §1): the awakening requirements, the tier scaling of
/// every cost, the attunement settings, the roll settings of the traits, which weapons can be awakened (item groups) and the trait pool.
/// Field names follow the XML; the client keeps them in bs2 (globalsettings), bs3/bs4 (item groups) and bs8/btd (traits).
/// </summary>
public sealed class LegendaryData
{
    public int MinTier { get; init; } = 4;
    public int MinEnchant { get; init; } = 4;

    /// <summary>awakeningrequirements silver (whole silver, × the tier scaling: bs2.c × bs5.abo).</summary>
    public double AwakenSilver { get; init; }

    /// <summary>awakeningrequirements craftresource (count × the tier scaling, rounded).</summary>
    public IReadOnlyList<(string Name, int Count)> AwakenResources { get; init; } = [];

    /// <summary>basecosts tierscaling: the factor per item tier (bs2.r, bs5.abo; 1 for a tier it lacks).</summary>
    public IReadOnlyDictionary<int, double> TierScaling { get; init; } = new Dictionary<int, double>();

    /// <summary>basecosts reroll / addreplace: the attunement a trait value reroll / a trait generation costs at scaling 1 and strain 1.</summary>
    public double RerollBase { get; init; } = 6667;
    public double AddReplaceBase { get; init; } = 10000;

    public double FameFactor { get; init; } = 0.037;
    public double MaxBaseAttunement { get; init; } = 100000;
    public double AttunementToSilver { get; init; } = 33;
    public double AttunementToFocus { get; init; } = 0.097511233;
    public double ReattuneSilver { get; init; } = 1000000;

    public double InitialRollMin { get; init; } = 0.01;
    public double InitialRollMax { get; init; } = 0.03;
    public double FailureBonusMin { get; init; } = 0.02;
    public double FailureBonusMax { get; init; } = 0.06;
    public double CriticalChance { get; init; } = 0.3375;
    public double CriticalBonusMin { get; init; } = 0.1;
    public double CriticalBonusMax { get; init; } = 0.3;

    /// <summary>interface traitsavailablepermodify: the options a trait generation offers.</summary>
    public int OptionsPerModify { get; init; } = 3;

    /// <summary>traitvariableconfig "default": traitvar1 = min + (max − min) × roll^scaler (the client's btb.k 0x18077F940).</summary>
    public double VarMin { get; init; } = 0.01;
    public double VarMax { get; init; } = 1;
    public double VarScaler { get; init; } = 1;

    /// <summary>actionduration awakening / reattuning / disenchanting (seconds).</summary>
    public double AwakenSeconds { get; init; } = 1.5;
    public double ReattuneSeconds { get; init; } = 1.5;
    public double DisenchantSeconds { get; init; } = 1.5;

    public IReadOnlyList<LegendaryGroup> Groups { get; init; } = [];
    public IReadOnlyList<TraitDef> Traits { get; init; } = [];

    /// <summary>The trait of <paramref name="name"/>; null = not in the pool.</summary>
    public TraitDef? Trait(string name) => Traits.FirstOrDefault(t => t.Name == name);

    /// <summary>The scaling factor of <paramref name="tier"/> (1 when the table lacks it, as bs5.abo).</summary>
    public double Scale(int tier) => TierScaling.TryGetValue(tier, out var f) ? f : 1;

    /// <summary>traitvar1 of a roll (0-1): the factor the trait's spell values are multiplied with.</summary>
    public double Factor(double roll) => VarMin + (VarMax - VarMin) * Math.Pow(Math.Clamp(roll, 0, 1), VarScaler);

    /// <summary>The item group of <paramref name="item"/> (its base name without the enchantment against the T?_ patterns); null = cannot be awakened.</summary>
    public LegendaryGroup? GroupOf(ItemTemplate item) => Groups.FirstOrDefault(g => g.Items.Any(r => r.IsMatch(item.BaseName)));

    /// <summary>Reads legendaryitems.bin.</summary>
    public static LegendaryData Load(GameDataFile file) => Parse(file.Root());

    public static LegendaryData Parse(XElement root)
    {
        static double D(XElement? e, string attr, double fallback) =>
            e?.Attribute(attr) is { } a && double.TryParse(a.Value, NumberStyles.Float, CultureInfo.InvariantCulture, out var v) ? v : fallback;
        static int I(XElement? e, string attr, int fallback) => (int)D(e, attr, fallback);
        static HashSet<string> Set(string? csv) =>
            new((csv ?? "").Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries), StringComparer.OrdinalIgnoreCase);
        var g = root.Element("globalsettings");
        var req = g?.Element("awakeningrequirements");
        var att = g?.Element("attunement");
        var costs = g?.Element("basecosts");
        var traits = g?.Element("traits");
        var reroll = g?.Element("reroll");
        var duration = g?.Element("actionduration");
        var config = root.Element("traitconfiguration");
        var variable = config?.Element("traitvariables")?.Elements("traitvariableconfig").FirstOrDefault(e => (string?)e.Attribute("id") == "default");
        var rarity = config?.Element("raritygroups")?.Elements("rarity")
            .ToDictionary(e => (string?)e.Attribute("name") ?? "", e => I(e, "weighting", 1), StringComparer.OrdinalIgnoreCase) ?? [];
        return new LegendaryData
        {
            MinTier = I(req, "mintier", 4), MinEnchant = I(req, "minenchant", 4), AwakenSilver = D(req, "silver", 0),
            AwakenResources = req?.Elements("craftresource").Select(e => ((string?)e.Attribute("uniquename") ?? "", I(e, "count", 0))).ToList() ?? [],
            TierScaling = costs?.Element("tierscaling")?.Elements("tier").ToDictionary(e => I(e, "tier", 0), e => D(e, "scalingfactor", 1)) ?? [],
            RerollBase = D(costs, "reroll", 6667), AddReplaceBase = D(costs, "addreplace", 10000),
            FameFactor = D(att, "famefactor", 0.037), MaxBaseAttunement = D(att, "maxbaseattunement", 100000),
            AttunementToSilver = D(att, "attunementtosilverfactor", 33), AttunementToFocus = D(att, "attunementtofocusfactor", 0.097511233),
            ReattuneSilver = D(att, "reattunementsilvercost", 1000000),
            InitialRollMin = D(traits, "initialrollmin", 0.01), InitialRollMax = D(traits, "initialrollmax", 0.03),
            FailureBonusMin = D(reroll, "failurebonusmin", 0.02), FailureBonusMax = D(reroll, "failurebonusmax", 0.06),
            CriticalChance = D(reroll, "criticalchance", 0.3375), CriticalBonusMin = D(reroll, "criticalbonusmin", 0.1), CriticalBonusMax = D(reroll, "criticalbonusmax", 0.3),
            OptionsPerModify = Math.Max(1, I(g?.Element("interface"), "traitsavailablepermodify", 3)),
            VarMin = D(variable, "minfactor", 0.01), VarMax = D(variable, "maxfactor", 1), VarScaler = D(variable, "rollscaler", 1),
            AwakenSeconds = D(duration, "awakening", 1.5), ReattuneSeconds = D(duration, "reattuning", 1.5), DisenchantSeconds = D(duration, "disenchanting", 1.5),
            Groups = root.Element("itemconfiguration")?.Elements("itemgroup").Select(e => new LegendaryGroup(
                e.Elements("item").Select(i => new Regex("^" + Regex.Escape((string?)i.Attribute("id") ?? "").Replace(@"\?", "[0-9]") + "$", RegexOptions.CultureInvariant)).ToList(),
                I(e, "basetraits", 3), Set((string?)e.Attribute("keywords")))).ToList() ?? [],
            Traits = config?.Element("traitpool")?.Elements("trait").Select(e => new TraitDef(
                (string?)e.Attribute("uniquename") ?? "", (string?)e.Attribute("spell") ?? "", rarity.GetValueOrDefault((string?)e.Attribute("rarity") ?? "", 1),
                Set((string?)e.Attribute("requiresOneOfKeywords")), Set((string?)e.Attribute("requiresKeywords")))).Where(t => t.Name.Length > 0).ToList() ?? [],
        };
    }
}
