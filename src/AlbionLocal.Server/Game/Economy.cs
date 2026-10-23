using System.Globalization;
using System.Xml.Linq;
using AlbionLocal.Server.Data;

namespace AlbionLocal.Server.Game;

/// <summary>
/// The economy numbers of gamedata.xml (stage 6 SPEC §6a.3, §6b, §6c): repair cost and time factors
/// (DurabilityData/Repair/Items), ItemValueToNutrition, the crafting quality weights (CraftingQualityChances), the focus
/// efficiency bonus (ActionFocus/CraftingEfficiency), FreeCrafting maxTier, the crafting fame (FameGainFactors), the
/// city bank (CityBank size and weight limit) and the marketplace (MarketPlace TaxValues and Settings, stage 6d). The
/// defaults are 345319's values, used when a section is missing.
/// </summary>
public sealed record EconomySettings
{
    public static EconomySettings Default { get; } = new();

    /// <summary>CostFactors lower/upper: cf = f × lower + (1 − f) × upper (= 1 − f).</summary>
    public double RepairLower { get; init; }
    public double RepairUpper { get; init; } = 1;

    /// <summary>Modifier silver: the repair silver per item value point at 0 durability.</summary>
    public double RepairSilver { get; init; } = 16.5;

    /// <summary>Time crafttimefactor (the repair duration formula is open, Q-A3; not used: a fixed duration).</summary>
    public double RepairTimeFactor { get; init; } = 0.1;

    public double ItemValueToNutrition { get; init; } = 0.1125;

    /// <summary>
    /// DurabilityData/Salvage/Items RewardFactors lower/upper (bca+0x128 / +0x130): the salvage's durability factor
    /// f × upper + (1 − f) × lower (bca.j 0x180599D90; = f).
    /// </summary>
    public double SalvageLower { get; init; }
    public double SalvageUpper { get; init; } = 1;

    /// <summary>Salvage GainFactors per tier 1-8 (bca+0x118 resource, +0x120 silver): 0.2 of the resources, 0.75 of the item value.</summary>
    public IReadOnlyList<double> SalvageResourceGain { get; init; } = [0.2, 0.2, 0.2, 0.2, 0.2, 0.2, 0.2, 0.2];
    public IReadOnlyList<double> SalvageSilverGain { get; init; } = [0.75, 0.75, 0.75, 0.75, 0.75, 0.75, 0.75, 0.75];

    /// <summary>Weight per quality level 1-5 (689/250/50/10/1).</summary>
    public IReadOnlyList<int> QualityWeights { get; init; } = [689, 250, 50, 10, 1];

    /// <summary>
    /// RerollQualityChances (enchanting SPEC §2): per initial quality 1-4 the weights of the result quality 1-5 (q1: 0/1600/300/99/1;
    /// q2: 0/1200/2400/397/3; q3: 0/0/500/499/1; q4: 0/0/0/199/1). A reroll never lowers the quality; a 1 always improves.
    /// </summary>
    public IReadOnlyList<IReadOnlyList<int>> RerollWeights { get; init; } =
        [[0, 1600, 300, 99, 1], [0, 1200, 2400, 397, 3], [0, 0, 500, 499, 1], [0, 0, 0, 199, 1]];

    /// <summary>DurabilityData/RerollQuality/Items CostModifier silver per quality 1-4 (4.4/5.5/6.6/27.5): the reroll price per item value point (bca+0x140).</summary>
    public IReadOnlyList<double> RerollCost { get; init; } = [4.4, 5.5, 6.6, 27.5];

    /// <summary>ActionFocus CraftingQuality bonus (50): the quality bonus a craft with focus gets (enchanting SPEC §3).</summary>
    public double FocusQuality { get; init; } = 50;

    /// <summary>ActionFocus CraftingEfficiency bonus (0.59): what focus adds to the return bonus.</summary>
    public double FocusEfficiency { get; init; } = 0.59;

    /// <summary>
    /// ActionFocus costreductionconstant (1.00695555005672; bgf+0x668): a craft's focus is divided by it to the power of the
    /// destiny board's reduction units (stageJ focus-lp SPEC §2.4: ln 2 / ln c = 100, so 100 units halve the cost).
    /// </summary>
    public double FocusCostConstant { get; init; } = 1.00695555005672;

    /// <summary>FreeCrafting maxTier: no nutrition fee up to this tier.</summary>
    public int FreeCraftingMaxTier { get; init; } = 2;

    /// <summary>FameGainFactors itemvaluetofameprogressionfactor (1.5): bci+0x30, the base of the crafting fame's tier power.</summary>
    public double FameProgressionFactor { get; init; } = 1.5;

    /// <summary>FameGainFactors progressionstarttier (3): bci+0x38, the tier the power counts from.</summary>
    public int FameProgressionStartTier { get; init; } = 3;

    /// <summary>FameGainFactors/CraftItem factor (1).</summary>
    public double CraftFameFactor { get; init; } = 1;

    public int BankSlots { get; init; } = 128;
    public double BankWeightLimit { get; init; } = 100000;

    /// <summary>MarketPlace TaxFactor setupfee (0.025): of an order's total, paid when it is created and kept (cfh.a 0x1808A9090).</summary>
    public double MarketSetupFee { get; init; } = 0.025;

    /// <summary>MarketPlace TaxFactor transactiontax (0.08): of a sale's total, kept from the seller (cfh.b 0x1808A9250).</summary>
    public double MarketTax { get; init; } = 0.08;

    /// <summary>MarketPlace AbsoluteTax minimumtax (1 silver): the least setup fee (cfh.a; the client's tax has no minimum).</summary>
    public double MarketMinimumTax { get; init; } = 1;

    /// <summary>MarketPlace Settings defaultexpiration in hours (720): MarketPlacePopUpGui.DurationInHrs, the key 4 of both Create ops.</summary>
    public int MarketOrderHours { get; init; } = 720;

    /// <summary>MarketPlace OrderDurations durationInHrs (12, 24, 168, 720): the durations the client's dropdown offers.</summary>
    public IReadOnlyList<int> MarketOrderDurations { get; init; } = [12, 24, 168, 720];

    /// <summary>The settings from gamedata.xml's root; a missing part keeps its default.</summary>
    public static EconomySettings From(XElement gamedata)
    {
        static double Num(XElement? e, string attr, double fallback) =>
            double.TryParse((string?)e?.Attribute(attr), NumberStyles.Float, CultureInfo.InvariantCulture, out var d) ? d : fallback;
        var d = Default;
        var repair = gamedata.Descendants("Repair").FirstOrDefault()?.Element("Items");
        var salvage = gamedata.Descendants("Salvage").FirstOrDefault()?.Element("Items");
        IReadOnlyList<double> Gains(string attr, IReadOnlyList<double> fallback)
        {
            var byTier = salvage?.Element("GainFactors")?.Elements("Item").ToDictionary(i => (int)Num(i, "tier", 0), i => Num(i, attr, double.NaN));
            if (byTier is null) return fallback;
            return [.. Enumerable.Range(1, 8).Select(t => byTier.TryGetValue(t, out var v) && !double.IsNaN(v) ? v : fallback[t - 1])];
        }
        var weights = gamedata.Descendants("CraftingQualityChances").FirstOrDefault()?.Elements("QualityLevel")
            .OrderBy(q => Num(q, "level", 0)).Select(q => (int)Num(q, "weight", 0)).ToList();
        var reroll = gamedata.Descendants("RerollQualityChances").FirstOrDefault()?.Elements("InitialQuality")
            .OrderBy(q => Num(q, "level", 0))
            .Select(q => (IReadOnlyList<int>)[.. q.Elements("ResultQuality").OrderBy(r => Num(r, "level", 0)).Select(r => (int)Num(r, "weight", 0))]).ToList();
        var rerollCost = gamedata.Descendants("RerollQuality").FirstOrDefault()?.Element("Items")?.Elements("CostModifier")
            .OrderBy(c => Num(c, "level", 0)).Select(c => Num(c, "silver", 0)).ToList();
        var bank = gamedata.Descendants("CityBank").FirstOrDefault();
        var fame = gamedata.Descendants("FameGainFactors").FirstOrDefault();
        // gamedata.xml has an empty <MarketPlace/> elsewhere too: the one with TaxValues counts.
        var market = gamedata.Descendants("MarketPlace").FirstOrDefault(m => m.Element("TaxValues") is not null);
        XElement? Tax(string name) => market?.Element("TaxValues")?.Elements().FirstOrDefault(e => (string?)e.Attribute("name") == name);
        var expiration = market?.Element("Settings")?.Elements("Entry").FirstOrDefault(e => (string?)e.Attribute("name") == "defaultexpiration");
        var durations = market?.Element("OrderDurations")?.Elements("Entry").Select(e => (int)Num(e, "durationInHrs", 0)).Where(h => h > 0).ToList();
        return new EconomySettings
        {
            RepairLower = Num(repair?.Element("CostFactors"), "lower", d.RepairLower),
            RepairUpper = Num(repair?.Element("CostFactors"), "upper", d.RepairUpper),
            RepairSilver = Num(repair?.Element("Modifier"), "silver", d.RepairSilver),
            RepairTimeFactor = Num(repair?.Element("Time"), "crafttimefactor", d.RepairTimeFactor),
            SalvageLower = Num(salvage?.Element("RewardFactors"), "lower", d.SalvageLower),
            SalvageUpper = Num(salvage?.Element("RewardFactors"), "upper", d.SalvageUpper),
            SalvageResourceGain = Gains("resource", d.SalvageResourceGain),
            SalvageSilverGain = Gains("silver", d.SalvageSilverGain),
            ItemValueToNutrition = Num(gamedata.Descendants("ItemValueToNutrition").FirstOrDefault(), "factor", d.ItemValueToNutrition),
            QualityWeights = weights is { Count: 5 } ? weights : d.QualityWeights,
            RerollWeights = reroll is { Count: > 0 } && reroll.All(r => r.Count == 5) ? reroll : d.RerollWeights,
            RerollCost = rerollCost is { Count: > 0 } ? rerollCost : d.RerollCost,
            FocusQuality = Num(gamedata.Descendants("ActionFocus").FirstOrDefault()?.Element("CraftingQuality"), "bonus", d.FocusQuality),
            FocusEfficiency = Num(gamedata.Descendants("ActionFocus").FirstOrDefault()?.Element("CraftingEfficiency"), "bonus", d.FocusEfficiency),
            FocusCostConstant = Num(gamedata.Descendants("ActionFocus").FirstOrDefault(), "costreductionconstant", d.FocusCostConstant),
            FreeCraftingMaxTier = (int)Num(gamedata.Descendants("FreeCrafting").FirstOrDefault(), "maxTier", d.FreeCraftingMaxTier),
            FameProgressionFactor = Num(fame, "itemvaluetofameprogressionfactor", d.FameProgressionFactor),
            FameProgressionStartTier = (int)Num(fame, "progressionstarttier", d.FameProgressionStartTier),
            CraftFameFactor = Num(fame?.Element("CraftItem"), "factor", d.CraftFameFactor),
            BankSlots = (int)Num(bank, "size", d.BankSlots),
            BankWeightLimit = Num(bank, "weightlimit", d.BankWeightLimit),
            MarketSetupFee = Num(Tax("setupfee"), "value", d.MarketSetupFee),
            MarketTax = Num(Tax("transactiontax"), "value", d.MarketTax),
            MarketMinimumTax = Num(Tax("minimumtax"), "value", d.MarketMinimumTax),
            MarketOrderHours = (int)Num(expiration, "value", d.MarketOrderHours),
            MarketOrderDurations = durations is { Count: > 0 } ? durations : d.MarketOrderDurations,
        };
    }
}

/// <summary>
/// Item values (bdi.d 0x1805F23E0, stage 6 SPEC §6a.3): the variant's own itemvalue attribute, else the first recipe's
/// resources (each at its enchantment level) × their counts, divided by amountcrafted when that is not 1; 0 without
/// either. A cycle counts 0 for the item that closes it. Cached per item; safe to share (a lock guards the cache).
/// </summary>
public sealed class ItemValues(ItemStore items)
{
    private readonly Dictionary<string, double> _cache = new(StringComparer.OrdinalIgnoreCase);
    private readonly object _gate = new();

    public double Of(ItemTemplate item)
    {
        lock (_gate) return Value(item, []);
    }

    /// <summary>The value of resource <paramref name="name"/> at <paramref name="enchantment"/> (NAME@N when it exists, else NAME).</summary>
    public double Of(string name, int enchantment)
    {
        lock (_gate) return Resolve(name, enchantment) is { } t ? Value(t, []) : 0;
    }

    private ItemTemplate? Resolve(string name, int enchantment) =>
        (enchantment > 0 ? items.Find($"{name}@{enchantment}") : null) ?? items.Find(name);

    private double Value(ItemTemplate item, HashSet<string> path)
    {
        if (_cache.TryGetValue(item.Name, out var cached)) return cached;
        if (item.OwnItemValue is { } own) return _cache[item.Name] = own;
        if (item.Recipes is not [var recipe, ..] || !path.Add(item.Name)) return 0;
        var sum = 0.0;
        foreach (var r in recipe.Resources)
            if (Resolve(r.Name, r.Enchantment) is { } resource) sum += Value(resource, path) * r.Count;
        path.Remove(item.Name);
        if (recipe.AmountCrafted != 1) sum /= recipe.AmountCrafted;
        return _cache[item.Name] = sum;
    }
}

/// <summary>The price and repair formulas of the client (stage 6 SPEC §0.1, §6a.3, §6c), kept in one place.</summary>
public static class Pricing
{
    /// <summary>a7m raw per silver.</summary>
    public const long A7m = 10_000;

    /// <summary>
    /// c3d.c5 (bgf.abo 0x1805CAE10): x × ((k84 − 1) × k85 + 1) with Join keys 84/85. This server sends neither, so both are
    /// 0 and the factor is 1 (SPEC §0.1 decision); the official 1.156 / 1.0 give c5(31940) = 36922.64.
    /// </summary>
    public static decimal CityFactor(decimal x, decimal k84 = 0, decimal k85 = 0) => x * ((k84 - 1) * k85 + 1);

    /// <summary>
    /// The durability fraction as the client works it out: cur / max as an a7m value, truncated to 4 decimals (the verifier:
    /// a plain float gives 6111 instead of the official 6116 for the 103829 repair). <paramref name="curRaw"/> is a7m raw,
    /// <paramref name="maxDurability"/> in items.xml units.
    /// </summary>
    public static decimal DurabilityFraction(long curRaw, double maxDurability) =>
        maxDurability <= 0 ? 1 : Math.Min(1m, Math.Floor(curRaw / (decimal)maxDurability) / A7m);

    /// <summary>
    /// The repair base of one item in whole silver (bca.i_4, cmj.al 0x180B67670): ceil(itemvalue × (f·lower + (1 − f)·upper)
    /// × silver), at least 1 when not full, × <paramref name="count"/>; 0 when full (ItemHasFullDurability).
    /// </summary>
    public static long RepairBase(double itemValue, long curRaw, double maxDurability, int count, EconomySettings? settings = null)
    {
        var s = settings ?? EconomySettings.Default;
        var f = DurabilityFraction(curRaw, maxDurability);
        if (f >= 1) return 0;
        var cf = f * (decimal)s.RepairLower + (1 - f) * (decimal)s.RepairUpper;
        var silver = (long)Math.Ceiling((decimal)itemValue * cf * (decimal)s.RepairSilver);
        return Math.Max(1, silver) * Math.Max(1, count);
    }

    /// <summary>The repair nutrition (bca.i_5): max(1, ceil(itemvalue × ItemValueToNutrition × 1 × (1 − f))) × count.</summary>
    public static long RepairNutrition(double itemValue, long curRaw, double maxDurability, int count, EconomySettings? settings = null)
    {
        var s = settings ?? EconomySettings.Default;
        var f = DurabilityFraction(curRaw, maxDurability);
        return Math.Max(1, (long)Math.Ceiling((decimal)itemValue * (decimal)s.ItemValueToNutrition * (1 - f))) * Math.Max(1, count);
    }

    /// <summary>
    /// The salvage's durability factor (bca.j 0x180599D90): f × upper + (1 − f) × lower with f the a7m durability fraction
    /// (<see cref="DurabilityFraction"/>); 1 for an item without durability (bca.i / bca.l take the factor only for
    /// equipment, bc4).
    /// </summary>
    public static decimal SalvageDurabilityFactor(long curRaw, double maxDurability, EconomySettings? settings = null)
    {
        var s = settings ?? EconomySettings.Default;
        if (maxDurability <= 0) return 1;
        var f = DurabilityFraction(curRaw, maxDurability);
        return f * (decimal)s.SalvageUpper + (1 - f) * (decimal)s.SalvageLower;
    }

    /// <summary>A tier's entry of a salvage gain table (tiers 1-8; 0 outside, as bca.i / bca.l).</summary>
    private static decimal Gain(IReadOnlyList<double> table, int tier) => tier is >= 1 and <= 8 && tier <= table.Count ? (decimal)table[tier - 1] : 0;

    /// <summary>
    /// The resource factor of salvaging one item (cml.al(c11, bca) 0x180B6EB60 → bca.l 0x18059AD40): the recipe's
    /// salvageitemfactor when given, else the durability factor × the tier's resource gain (0.2).
    /// </summary>
    public static decimal SalvageResourceFactor(CraftRecipe? recipe, int tier, long curRaw, double maxDurability, EconomySettings? settings = null)
    {
        var s = settings ?? EconomySettings.Default;
        if (recipe?.SalvageItemFactor is { } own) return (decimal)own;
        return SalvageDurabilityFactor(curRaw, maxDurability, s) * Gain(s.SalvageResourceGain, tier);
    }

    /// <summary>
    /// The silver of salvaging <paramref name="count"/> items, a7m raw (bca.i(bdi, a7m, a7m) 0x180599BD0 as SalvageItemView
    /// shows it): the salvage value (salvagevalueoverride, else the item value) × (the recipe's salvagesilverfactor when
    /// given, else the durability factor × the tier's silver gain, 0.75) per item, divided by the city factor (1 here: no
    /// Join keys 84/85, <see cref="CityFactor"/>), truncated to whole a7m, × count.
    /// </summary>
    public static long SalvageSilver(double salvageValue, CraftRecipe? recipe, int tier, long curRaw, double maxDurability, int count, EconomySettings? settings = null)
    {
        var s = settings ?? EconomySettings.Default;
        if (salvageValue <= 0 || count <= 0) return 0;
        var factor = recipe?.SalvageSilverFactor is { } own ? (decimal)own : SalvageDurabilityFactor(curRaw, maxDurability, s) * Gain(s.SalvageSilverGain, tier);
        return (long)Math.Floor((decimal)salvageValue * factor * A7m) * count;
    }

    /// <summary>
    /// The count of one resource a salvage gives (cml.al(List, a7o) 0x180B6EC80): count × the item's stack × the resource
    /// factor, rounded at random (a7p.a 0x180944610: up with the probability of the fraction, down otherwise; the client's
    /// preview without a random rounds up).
    /// </summary>
    public static int SalvageResourceCount(int perCraft, int stack, decimal factor, Random? random)
    {
        var exact = (double)(perCraft * stack * factor);
        if (exact <= 0) return 0;
        var floor = Math.Floor(exact);
        return (int)(random is null ? Math.Ceiling(exact) : random.NextDouble() <= exact - floor ? Math.Ceiling(exact) : floor);
    }

    /// <summary>
    /// a7m × a7m as the client multiplies (a7m.p 0x1809442A0): raw × raw / 10⁴, truncated toward zero; an overflow gives
    /// long.MaxValue (the client's a7m.MaxValue).
    /// </summary>
    public static long A7mTimes(long raw, double factor)
    {
        var product = (Int128)raw * (long)Math.Round(factor * A7m) / A7m;
        return product > long.MaxValue ? long.MaxValue : product < long.MinValue ? long.MinValue : (long)product;
    }

    /// <summary>
    /// The setup fee of a market order of <paramref name="totalRaw"/> (unit × amount, a7m raw), as the client shows and checks
    /// it (cfh.a 0x1808A9090): max(minimumtax, total × setupfee), a7m truncated. 2.5 % of 500 silver = 12.5 silver.
    /// </summary>
    public static long MarketSetupFee(long totalRaw, EconomySettings? settings = null)
    {
        var s = settings ?? EconomySettings.Default;
        return Math.Max((long)Math.Round(s.MarketMinimumTax * A7m), A7mTimes(totalRaw, s.MarketSetupFee));
    }

    /// <summary>
    /// The sales tax kept from a sale of <paramref name="totalRaw"/> (MarketPlacePopUpGui.CalcSellTax 0x180FD2710, cfh.b
    /// 0x1808A9250): max(0, total × transactiontax) × <paramref name="premiumFactor"/> (ctp.asj: characters.xml
    /// premiummarkettransactionfeefactor 0.5 while the seller is premium, else 1; <see cref="Systems.Premium.TaxFactor"/>), a7m truncated.
    /// 8 % of 300 silver = 24 silver, 12 with premium.
    /// </summary>
    public static long MarketTax(long totalRaw, EconomySettings? settings = null, double premiumFactor = 1) =>
        A7mTimes(Math.Max(0, A7mTimes(totalRaw, (settings ?? EconomySettings.Default).MarketTax)), premiumFactor);

    /// <summary>Crafts of a request: ceil(amount / amountcrafted).</summary>
    public static int Crafts(int amount, int amountCrafted) => amount <= 0 ? 0 : (amount + Math.Max(1, amountCrafted) - 1) / Math.Max(1, amountCrafted);

    /// <summary>The silver price of <paramref name="crafts"/> crafts of <paramref name="recipe"/> (cl3.al_6), a7m raw: silver × crafts.</summary>
    public static long CraftPrice(CraftRecipe recipe, int crafts) => (long)Math.Round((decimal)recipe.Silver * A7m) * crafts;

    /// <summary>Client cl3.aap → ctp.af: recipe seconds × completed crafts × CraftTime factor.
    /// a4f.fg clamps the factor to max(0.1, 1 − reduction). Keep its single-precision arithmetic
    /// and calculate each cumulative deadline from the batch start rather than adding rounded intervals.</summary>
    public static TimeSpan CraftDuration(CraftRecipe recipe, int crafts, double reduction = 0)
        => crafts <= 0 || recipe.Time <= 0 ? TimeSpan.Zero
            : TimeSpan.FromSeconds((float)recipe.Time * crafts * MathF.Max(0.1f, 1 - (float)reduction));

    /// <summary>
    /// SPEC §0.1's price rule: the client's price <paramref name="client"/> is taken when 0 ≤ client ≤ expected + 1 silver
    /// (the unknown abp discount may lower it); anything else is PriceChanged.
    /// </summary>
    public static bool Accepts(long client, long expected) => client >= 0 && client <= expected + A7m;

    /// <summary>
    /// The share of the resources a craft gives back (SPEC §6c, Q-C7 assumed): 1 − 1 / (1 + bonus), bonus = the location's
    /// refining or crafting bonus + the category's modifier (+ the focus efficiency when focus is used).
    /// </summary>
    public static double ReturnFraction(double bonus) => bonus <= 0 ? 0 : 1 - 1 / (1 + bonus);

    /// <summary>
    /// The crafting fame of <paramref name="count"/> items of value <paramref name="itemValue"/> (bci.g 0x1805B13A0, stage 6
    /// SPEC §6c): itemvalue × count × CraftItem factor × FameProgressionFactor^(enchantment + 1 + tier − progressionstarttier),
    /// a7m raw, truncated (T2 planks 4 × 1.5⁰ = 4, T3 planks 8 × 1.5 = 12, T4_MAIN_SWORD 384 × 1.5² = 864). bci.g's exponent
    /// is p4 + 1 + p3 − start, so which of its two ints is the tier and which the enchantment (Q-C9) does not change it.
    /// bci.g turns the power into an a7m (truncated to 4 decimals) before the product, which matters from exponent 5 on
    /// (1.5⁶ = 11.390625 → 11.3906: T8_MAIN_SWORD 6144 × 11.3906 = 69983.8464, not 69984).
    /// </summary>
    public static long CraftFame(double itemValue, int tier, int enchantment, int count, EconomySettings? settings = null)
    {
        var s = settings ?? EconomySettings.Default;
        if (itemValue <= 0 || count <= 0) return 0;
        var power = Math.Pow(s.FameProgressionFactor, enchantment + 1 + tier - s.FameProgressionStartTier);
        var powerA7m = Math.Floor((decimal)power * A7m) / A7m;
        return (long)Math.Floor((decimal)itemValue * count * (decimal)s.CraftFameFactor * powerA7m * A7m);
    }

    /// <summary>
    /// The returned count of resource <paramref name="r"/> for <paramref name="crafts"/> crafts: count × crafts × fraction plus
    /// the carried remainder, floored, at most maxreturnamount per craft (0 = never returned); and the new remainder.
    /// </summary>
    public static (int Count, double Carry) Returned(CraftResource r, int crafts, double fraction, double carry)
    {
        if (r.MaxReturn <= 0 || fraction <= 0) return (0, carry);
        var exact = r.Count * crafts * fraction + carry;
        var whole = Math.Floor(exact);
        return ((int)Math.Min(whole, (long)r.MaxReturn * crafts), exact - whole);
    }
}
