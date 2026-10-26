using System.Globalization;
using System.Xml.Linq;

namespace AlbionLocal.Server.Game;

/// <summary>
/// One gamedata.xml &lt;Reputation&gt;&lt;level&gt; (stageE pvp SPEC §3.4, bgf.acj 0x1805D5F80): the lowest points of the level, its daily
/// recovery (negative levels), the gain factors of PvE kills and gathering (× fame), the penalties the level costs whoever commits
/// a crime against a player of it (attack, knockdown, kill), whether the flag may go back to Blue and its crime protection buff.
/// </summary>
public sealed record ReputationLevel(double RequiredPoints, double RecoveryPerDay, double PveGainFactor, double GatherGainFactor,
    double AttackPenalty, double KnockdownPenalty, double KillPenalty, bool CanDisablePvp = true, string? CrimeProtectionBuff = null);

/// <summary>
/// gamedata.xml &lt;Reputation&gt; (stageE pvp SPEC §3.4): attacktimeoutseconds (120: one attack penalty per victim in it),
/// supporttimeoutseconds (10), startreputation, min/maxreputation (±50000), flaggedplayersgainreputationfromgatheringandpve and the
/// levels in file order (the order is the level byte of Join key 44, NewCharacter key 39 and ReputationUpdate keys 4/5: 0
/// POSITIVE04 … 4 NEUTRAL … 9 NEGATIVE04).
/// </summary>
public sealed record ReputationSettings(double AttackTimeoutSeconds, double SupportTimeoutSeconds, double Start, double Min, double Max,
    bool FlaggedGain, IReadOnlyList<ReputationLevel> Levels)
{
    /// <summary>The 345319 values.</summary>
    public static ReputationSettings Default { get; } = new(120, 10, 0, -50000, 50000, true,
    [
        new(10000, 0, 0.0014, 0.01, 10, 50, 1000, CrimeProtectionBuff: "CRIMEPROTECTION_POSITIVE04"),
        new(5000, 0, 0.0028, 0.02, 7.5, 37.5, 750, CrimeProtectionBuff: "CRIMEPROTECTION_POSITIVE03"),
        new(1500, 0, 0.0042, 0.03, 5, 25, 500, CrimeProtectionBuff: "CRIMEPROTECTION_POSITIVE02"),
        new(500, 0, 0.0056, 0.04, 2.5, 12.5, 250, CrimeProtectionBuff: "CRIMEPROTECTION_POSITIVE01"),
        new(0, 0, 0.007, 0.05, 1, 5, 100),
        new(-1000, 0, 0.0084, 0.04, 0, 0, 0),
        new(-3000, 1000, 0.014, 0.1, 0, 0, 0),
        new(-10000, 750, 0.028, 0.2, 0, 0, 0),
        new(-20000, 500, 0.042, 0.3, 0, 0, 0),
        new(-50000, 2000, 0.063, 0.45, 0, 0, 0),
    ]);

    /// <summary>
    /// The level byte of <paramref name="points"/>: the first level, top to bottom, whose requiredpoints are not above them (SPEC
    /// §3.4); below every level the last one.
    /// </summary>
    public byte LevelOf(double points)
    {
        for (var i = 0; i < Levels.Count; i++)
            if (Levels[i].RequiredPoints <= points) return (byte)i;
        return (byte)Math.Max(0, Levels.Count - 1);
    }

    /// <summary>Reads the element; a missing attribute keeps the default's value, no &lt;level&gt; keeps its levels.</summary>
    internal static ReputationSettings Read(XElement e)
    {
        static double N(XElement e, string name, double fallback) =>
            double.TryParse((string?)e.Attribute(name), NumberStyles.Float, CultureInfo.InvariantCulture, out var v) ? v : fallback;
        var d = Default;
        var levels = e.Elements("level").Select(l => new ReputationLevel(N(l, "requiredpoints", 0), N(l, "recoveryperday", 0),
            N(l, "pvegainfactor", 0), N(l, "gathergainfactor", 0), N(l, "attackpenalty", 0), N(l, "knockdownpenalty", 0), N(l, "killpenalty", 0),
            (string?)l.Attribute("candisablepvp") != "false", (string?)l.Attribute("crimeprotectionbuff") is { Length: > 0 } buff ? buff : null)).ToList();
        return new ReputationSettings(N(e, "attacktimeoutseconds", d.AttackTimeoutSeconds), N(e, "supporttimeoutseconds", d.SupportTimeoutSeconds),
            N(e, "startreputation", d.Start), N(e, "minreputation", d.Min), N(e, "maxreputation", d.Max),
            (string?)e.Attribute("flaggedplayersgainreputationfromgatheringandpve") != "false", levels.Count > 0 ? levels : d.Levels);
    }
}

/// <summary>
/// One &lt;DurabilityLoss&gt; of a gamedata.xml &lt;ItemLossEvent&gt; (stageE pvp SPEC §4.4): the share of the maximum durability an item
/// loses (uniform in [min, max]), what at least stays (minimumremainingfactor of the maximum), per item kind (items.xml element name:
/// rewardtoken, …) an override of the three.
/// </summary>
public sealed record DurabilityLossRule(double MinFactor, double MaxFactor, double MinimumRemaining,
    IReadOnlyDictionary<string, (double Min, double Max, double Remaining)> Overrides)
{
    /// <summary>AliveToKnockedOut of 345319: 5 % of every item, at least 1 % left; rewardtoken nothing.</summary>
    public static DurabilityLossRule KnockedOut { get; } = new(0.05, 0.05, 0.01,
        new Dictionary<string, (double, double, double)>(StringComparer.Ordinal) { ["rewardtoken"] = (0, 0, 0.01) });

    /// <summary>The factors for an item of kind <paramref name="kind"/>.</summary>
    public (double Min, double Max, double Remaining) For(string kind) =>
        Overrides.TryGetValue(kind, out var o) ? o : (MinFactor, MaxFactor, MinimumRemaining);

    /// <summary>The &lt;DurabilityLoss&gt; child of <paramref name="lossEvent"/> (the first without a target); null = none.</summary>
    internal static DurabilityLossRule? Read(XElement? lossEvent)
    {
        return lossEvent?.Elements("DurabilityLoss").FirstOrDefault(d => d.Attribute("target") is null) is { } d ? Of(d) : null;
    }

    /// <summary>Reads one &lt;DurabilityLoss&gt; element (its target attribute is the caller's business).</summary>
    internal static DurabilityLossRule Of(XElement d)
    {
        static double N(XElement e, string name, double fallback) =>
            double.TryParse((string?)e.Attribute(name), NumberStyles.Float, CultureInfo.InvariantCulture, out var v) ? v : fallback;
        var min = N(d, "minfactor", 0);
        var max = N(d, "maxfactor", min);
        var rest = N(d, "minimumremainingfactor", 0);
        return new DurabilityLossRule(min, max, rest, d.Elements("Override").Where(o => o.Attribute("itemtype") is not null)
            .GroupBy(o => (string)o.Attribute("itemtype")!, StringComparer.Ordinal)
            .ToDictionary(g => g.Key, g => (N(g.First(), "minfactor", min), N(g.First(), "maxfactor", max), N(g.First(), "minimumremainingfactor", rest)),
                StringComparer.Ordinal));
    }
}

/// <summary>
/// One &lt;StackLoss&gt; of a gamedata.xml &lt;ItemLossEvent&gt; (stageE pvp SPEC §4.4): the share of each item's count destroyed on a
/// death (0.33), per item kind (items.xml element name: trashitem 1, rewardtoken 1) an override.
/// </summary>
public sealed record StackLossRule(double Factor, IReadOnlyDictionary<string, double> Overrides)
{
    /// <summary>The share of an item of kind <paramref name="kind"/>.</summary>
    public double For(string kind) => Overrides.TryGetValue(kind, out var f) ? f : Factor;

    internal static StackLossRule Of(XElement e)
    {
        var factor = PvpDeathSettings.N(e, "factor", 0);
        return new StackLossRule(factor, e.Elements("Override").Where(o => o.Attribute("itemtype") is not null)
            .GroupBy(o => (string)o.Attribute("itemtype")!, StringComparer.Ordinal)
            .ToDictionary(g => g.Key, g => PvpDeathSettings.N(g.First(), "factor", factor), StringComparer.Ordinal));
    }
}

/// <summary>
/// What one gamedata.xml &lt;ItemLossEvent&gt; of a death does to the items (stageE pvp SPEC §4.4): the durability loss and the stack
/// loss of the equipment and of the inventory (a &lt;DurabilityLoss&gt; / &lt;StackLoss&gt; without target is both's, target="equipment"
/// or "inventory" one's; null = none). A part drops into the loot bag when the event has a stack loss for it [infer: the SPEC's
/// table; AliveToDeadOrange and KnockedOutToDeadOrange have none for the equipment, which stays on the victim].
/// </summary>
public sealed record ItemLossRule(DurabilityLossRule? EquipmentWear, DurabilityLossRule? InventoryWear, StackLossRule? EquipmentStack, StackLossRule? InventoryStack)
{
    public bool EquipmentDrops => EquipmentStack is not null;

    public bool InventoryDrops => InventoryStack is not null;

    private static DurabilityLossRule Wear(double min, double max, bool rewardtoken) => new(min, max, 0.01,
        rewardtoken ? new Dictionary<string, (double, double, double)>(StringComparer.Ordinal) { ["rewardtoken"] = (1, 1, 0.01) }
            : new Dictionary<string, (double, double, double)>());

    private static StackLossRule Stack { get; } = new(0.33, new Dictionary<string, double>(StringComparer.Ordinal) { ["rewardtoken"] = 1, ["trashitem"] = 1 });

    /// <summary>AliveToDead of 345319: everything drops, 20-40 % durability (rewardtoken all), a third of each stack destroyed.</summary>
    public static ItemLossRule AliveToDead { get; } = new(Wear(0.2, 0.4, true), Wear(0.2, 0.4, true), Stack, Stack);

    /// <summary>KnockedOutToDead: everything drops, 15-35 % durability, a third of each stack.</summary>
    public static ItemLossRule KnockedOutToDead { get; } = new(Wear(0.15, 0.35, true), Wear(0.15, 0.35, true), Stack, Stack);

    /// <summary>AliveToDeadOrange: the equipment stays with 5 % durability lost, the inventory drops as AliveToDead's.</summary>
    public static ItemLossRule AliveToDeadOrange { get; } = new(Wear(0.05, 0.05, false), Wear(0.2, 0.4, false), null, Stack);

    /// <summary>KnockedOutToDeadOrange: the equipment stays untouched, the inventory drops (15-35 %).</summary>
    public static ItemLossRule KnockedOutToDeadOrange { get; } = new(null, Wear(0.15, 0.35, false), null, Stack);

    private static StackLossRule ThrownOut { get; } = new(0.33, new Dictionary<string, double>(StringComparer.Ordinal)
    {
        ["equipmentitem"] = 0, ["mount"] = 0, ["weapon"] = 0, ["transformationweapon"] = 0, ["trashitem"] = 1,
    });

    /// <summary>
    /// StageL roads phase B: ThrownOutAvalonRoads, nothing drops: 20-40 % durability of everything, a third of each stack destroyed (trash all of
    /// it), but no equipment item, weapon, transformation weapon or mount (factor 0).
    /// </summary>
    public static ItemLossRule ThrownOutAvalonRoads { get; } = new(Wear(0.2, 0.4, false), Wear(0.2, 0.4, false), ThrownOut, ThrownOut);

    /// <summary>Reads one event element (&lt;AliveToDead&gt; …); null = no such element.</summary>
    internal static ItemLossRule? Of(XElement? e)
    {
        if (e is null) return null;
        XElement? Pick(string name, string target) =>
            e.Elements(name).FirstOrDefault(x => (string?)x.Attribute("target") == target) ?? e.Elements(name).FirstOrDefault(x => x.Attribute("target") is null);
        DurabilityLossRule? Wear(string target) => Pick("DurabilityLoss", target) is { } d ? DurabilityLossRule.Of(d) : null;
        StackLossRule? Stack(string target) => Pick("StackLoss", target) is { } s ? StackLossRule.Of(s) : null;
        return new ItemLossRule(Wear("equipment"), Wear("inventory"), Stack("equipment"), Stack("inventory"));
    }
}

/// <summary>
/// The gamedata.xml numbers of a death in a lethal cluster (stageE pvp SPEC §4, sub-step E3): the four &lt;ItemLossEvent&gt; rules of a death and ThrownOutAvalonRoads (stageL roads),
/// the &lt;MultipleAttackerMultiplier&gt; durability factors (by the number of attackers: 2 → 1.15 … 11 and more → 2.50), &lt;Loot&gt;
/// pvpallowedprotection (120 s) / pvpforcedprotection (90 s, PvpForced) / vanishtime (1800 s) of the loot bag, &lt;DeathSettings&gt;
/// playervisibilityafterdeath (3 s), &lt;Respawn autorespawntimerinsecs&gt; (600 s), &lt;FameGainFactors&gt;&lt;KillPlayerIv factor&gt; (1),
/// &lt;SilverDropFromPvp factor&gt; (0.75) and &lt;Fame pvpfamesharingradius&gt; (50 m).
/// </summary>
public sealed record PvpDeathSettings(ItemLossRule AliveToDead, ItemLossRule KnockedOutToDead, ItemLossRule AliveToDeadOrange, ItemLossRule KnockedOutToDeadOrange,
    ItemLossRule ThrownOutAvalonRoads, IReadOnlyList<(int Attackers, double Durability)> AttackerMultipliers, double AllowedProtectionSeconds, double ForcedProtectionSeconds, double VanishSeconds,
    double VisibilityAfterDeathSeconds, double AutoRespawnSeconds, double KillPlayerIvFactor, double SilverDropFactor, double FameSharingRadius)
{
    /// <summary>The 345319 values.</summary>
    public static PvpDeathSettings Default { get; } = new(ItemLossRule.AliveToDead, ItemLossRule.KnockedOutToDead, ItemLossRule.AliveToDeadOrange,
        ItemLossRule.KnockedOutToDeadOrange, ItemLossRule.ThrownOutAvalonRoads, [(2, 1.15), (3, 1.30), (4, 1.45), (5, 1.60), (6, 1.75), (7, 1.90), (8, 2.05), (9, 2.20), (10, 2.35), (11, 2.50)],
        120, 90, 1800, 3, 600, 1, 0.75, 50);

    /// <summary>
    /// The durability factor for <paramref name="attackers"/> attackers: the entry of the highest count listed that is not above it
    /// (gamedata: "for more attackers count not listed the next lowest will be taken"), 1 below the first.
    /// </summary>
    public double DurabilityMultiplier(int attackers) =>
        AttackerMultipliers.Where(m => m.Attackers <= attackers).Select(m => m.Durability).DefaultIfEmpty(1).Last();

    internal static double N(XElement e, string name, double fallback) =>
        double.TryParse((string?)e.Attribute(name), NumberStyles.Float, CultureInfo.InvariantCulture, out var v) ? v : fallback;

    /// <summary>Reads gamedata.xml's root; what is missing keeps <see cref="Default"/>'s value.</summary>
    internal static PvpDeathSettings Read(XElement gamedata)
    {
        var d = Default;
        var events = gamedata.Descendants("ItemLossEvent").FirstOrDefault();
        var multipliers = gamedata.Descendants("MultipleAttackerMultiplier").FirstOrDefault()?.Elements("Multiplier")
            .Select(m => ((int)N(m, "attackers", 0), N(m, "durabilityloss", 1))).Where(m => m.Item1 > 0).OrderBy(m => m.Item1).ToList();
        var loot = gamedata.Descendants("Loot").FirstOrDefault(l => l.Attribute("pvpallowedprotection") is not null);
        var death = gamedata.Descendants("DeathSettings").FirstOrDefault();
        var respawn = gamedata.Descendants("Respawn").FirstOrDefault(r => r.Attribute("autorespawntimerinsecs") is not null);
        var kill = gamedata.Descendants("KillPlayerIv").FirstOrDefault();
        var silver = gamedata.Descendants("SilverDropFromPvp").FirstOrDefault();
        var fame = gamedata.Descendants("Fame").FirstOrDefault(f => f.Attribute("pvpfamesharingradius") is not null);
        return new PvpDeathSettings(
            ItemLossRule.Of(events?.Element("AliveToDead")) ?? d.AliveToDead,
            ItemLossRule.Of(events?.Element("KnockedOutToDead")) ?? d.KnockedOutToDead,
            ItemLossRule.Of(events?.Element("AliveToDeadOrange")) ?? d.AliveToDeadOrange,
            ItemLossRule.Of(events?.Element("KnockedOutToDeadOrange")) ?? d.KnockedOutToDeadOrange,
            ItemLossRule.Of(events?.Element("ThrownOutAvalonRoads")) ?? d.ThrownOutAvalonRoads,
            multipliers is { Count: > 0 } ? multipliers : d.AttackerMultipliers,
            loot is null ? d.AllowedProtectionSeconds : N(loot, "pvpallowedprotection", d.AllowedProtectionSeconds),
            loot is null ? d.ForcedProtectionSeconds : N(loot, "pvpforcedprotection", d.ForcedProtectionSeconds),
            loot is null ? d.VanishSeconds : N(loot, "vanishtime", d.VanishSeconds),
            death is null ? d.VisibilityAfterDeathSeconds : N(death, "playervisibilityafterdeath", d.VisibilityAfterDeathSeconds),
            respawn is null ? d.AutoRespawnSeconds : N(respawn, "autorespawntimerinsecs", d.AutoRespawnSeconds),
            kill is null ? d.KillPlayerIvFactor : N(kill, "factor", d.KillPlayerIvFactor),
            silver is null ? d.SilverDropFactor : N(silver, "factor", d.SilverDropFactor),
            fame is null ? d.FameSharingRadius : N(fame, "pvpfamesharingradius", d.FameSharingRadius));
    }
}

/// <summary>
/// One &lt;PlayerFocusFire&gt;&lt;attackers&gt; row (stageP SPEC §2.3): from <see cref="Attackers"/> distinct attacking players on, the
/// share of a player hit's damage the target's resilience takes away, by the attacker's weapon (melee, ranged) or on a mounted target.
/// </summary>
public sealed record FocusFireRow(int Attackers, double Melee, double Ranged, double Mounted);

/// <summary>
/// The gamedata.xml numbers of the PvP details of stageP (analysis/work/stageP/league-pvp/SPEC.md §2): &lt;DeathRecap timeframe&gt;
/// (180 s, the death recap's window), &lt;PlayerHealingSickness lookbackseconds="10"&gt; with its &lt;attackers numbergreaterorequal
/// buff&gt; rows (2 → HEALINGSICKNESS_2 … 20 → HEALINGSICKNESS_20; the counted units are the distinct healers, SPEC §2.2) and
/// &lt;PlayerFocusFire lookbackseconds="10" effectdurationinsec="5"&gt; with its rows (2 → 0.143 … 26+ → 0.75, mounted up to 0.80).
/// </summary>
public sealed record PvpDetailSettings(double DeathRecapSeconds, double SicknessLookbackSeconds, IReadOnlyList<(int Healers, string Buff)> Sickness,
    double FocusFireLookbackSeconds, double FocusFireSeconds, IReadOnlyList<FocusFireRow> FocusFire)
{
    private static readonly double[] MeleeDefault =
        [0.143, 0.259, 0.353, 0.420, 0.466, 0.500, 0.524, 0.546, 0.566, 0.585, 0.602, 0.618, 0.633, 0.646, 0.659, 0.671, 0.682, 0.692, 0.702, 0.711, 0.720, 0.728, 0.736, 0.743, 0.750];

    private static readonly double[] MountedDefault =
        [0.143, 0.259, 0.353, 0.430, 0.494, 0.547, 0.592, 0.630, 0.662, 0.690, 0.713, 0.734, 0.752, 0.768, 0.782, 0.795, 0.800];

    /// <summary>The 345319 values.</summary>
    public static PvpDetailSettings Default { get; } = new(180, 10, [.. Enumerable.Range(2, 19).Select(n => (n, $"HEALINGSICKNESS_{n}"))], 10, 5,
        [.. Enumerable.Range(2, 29).Select(n => new FocusFireRow(n, MeleeDefault[Math.Min(n - 2, MeleeDefault.Length - 1)],
            MeleeDefault[Math.Min(n - 2, MeleeDefault.Length - 1)], MountedDefault[Math.Min(n - 2, MountedDefault.Length - 1)]))]);

    /// <summary>The buff of <paramref name="healers"/> distinct healers: the row with the highest count not above it; null below the first.</summary>
    public string? SicknessBuff(int healers) => Sickness.Where(r => r.Healers <= healers).Select(r => r.Buff).LastOrDefault();

    /// <summary>The row of <paramref name="attackers"/> distinct attackers: the highest count not above it; null below the first.</summary>
    public FocusFireRow? FocusFireRowOf(int attackers) => FocusFire.LastOrDefault(r => r.Attackers <= attackers);

    /// <summary>Reads gamedata.xml's root; what is missing keeps <see cref="Default"/>'s value.</summary>
    internal static PvpDetailSettings Read(XElement gamedata)
    {
        var d = Default;
        var recap = gamedata.Descendants("DeathRecap").FirstOrDefault();
        var sick = gamedata.Descendants("PlayerHealingSickness").FirstOrDefault();
        var sickRows = sick?.Elements("attackers").Select(a => ((int)PvpDeathSettings.N(a, "numbergreaterorequal", 0), (string?)a.Attribute("buff") ?? ""))
            .Where(r => r.Item1 > 0 && r.Item2.Length > 0).OrderBy(r => r.Item1).ToList();
        var focus = gamedata.Descendants("PlayerFocusFire").FirstOrDefault();
        var focusRows = focus?.Elements("attackers").Select(a => new FocusFireRow((int)PvpDeathSettings.N(a, "numbergreaterorequal", 0),
                PvpDeathSettings.N(a, "meleemodifier", 0), PvpDeathSettings.N(a, "rangedmodifier", 0), PvpDeathSettings.N(a, "mountedmodifier", 0)))
            .Where(r => r.Attackers > 0).OrderBy(r => r.Attackers).ToList();
        return new PvpDetailSettings(
            recap is null ? d.DeathRecapSeconds : PvpDeathSettings.N(recap, "timeframe", d.DeathRecapSeconds),
            sick is null ? d.SicknessLookbackSeconds : PvpDeathSettings.N(sick, "lookbackseconds", d.SicknessLookbackSeconds),
            sickRows is { Count: > 0 } ? sickRows : d.Sickness,
            focus is null ? d.FocusFireLookbackSeconds : PvpDeathSettings.N(focus, "lookbackseconds", d.FocusFireLookbackSeconds),
            focus is null ? d.FocusFireSeconds : PvpDeathSettings.N(focus, "effectdurationinsec", d.FocusFireSeconds),
            focusRows is { Count: > 0 } ? focusRows : d.FocusFire);
    }
}
