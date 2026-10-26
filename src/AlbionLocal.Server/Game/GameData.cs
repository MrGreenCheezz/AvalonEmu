using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Xml.Linq;
using AlbionLocal.Photon;
using AlbionLocal.Server.Data;

namespace AlbionLocal.Server.Game;

/// <summary>
/// One items.xml variant as the stat calculator needs it, indexed by the client's item type id: the stat fields of its
/// <see cref="ItemTemplate"/> (<see cref="From"/>), or a data/gamedata.json "items" entry (analysis/tools/gen_gamedata.py;
/// zero fields are left out of the file), which the self-test compares with the template's.
/// </summary>
public sealed class ItemData
{
    [JsonPropertyName("n")] public string Name { get; init; } = "";
    [JsonPropertyName("t")] public int Tier { get; init; }
    /// <summary>items.xml slottype (mainhand, offhand, head, armor, shoes, bag, cape, mount, potion, food).</summary>
    [JsonPropertyName("s")] public string Slot { get; init; } = "";
    /// <summary>itempowerprogressiontype: key into the hitpoints/energy shares.</summary>
    [JsonPropertyName("p")] public string ProgressionType { get; init; } = "";
    [JsonPropertyName("two")] public bool TwoHanded { get; init; }
    /// <summary>itempower of this variant (the enchantment's value for @N).</summary>
    [JsonPropertyName("ip")] public double ItemPower { get; init; }
    [JsonPropertyName("mm")] public double MasteryModifier { get; init; }
    [JsonPropertyName("hp")] public double HitPointsMax { get; init; }
    [JsonPropertyName("hpr")] public double HitPointsRegeneration { get; init; }
    [JsonPropertyName("en")] public double EnergyMax { get; init; }
    [JsonPropertyName("hrb")] public double HitPointsRegenerationBonus { get; init; }
    [JsonPropertyName("erb")] public double EnergyRegenerationBonus { get; init; }
    [JsonPropertyName("mhp")] public double MountHitPointsMax { get; init; }
    [JsonPropertyName("mhr")] public double MountHitPointsRegeneration { get; init; }
    /// <summary>Max durability in items.xml units; NewEquipmentItem key 8 carries it as a7m raw (x10000).</summary>
    [JsonPropertyName("d")] public double Durability { get; init; }

    /// <summary>
    /// items.xml physicalarmor / magicresistance / crowdcontrolresistance (stageJ armour SPEC §2.6: only on slottype armor, e.g.
    /// T4_ARMOR_PLATE_SET1 143 / 112 / 40) and bonusdefensevsmobs / bonusdefensevsplayers (only on off hands: shield 0.0108);
    /// 0 when absent. Not in data/gamedata.json.
    /// </summary>
    [JsonIgnore] public double PhysicalArmor { get; init; }

    [JsonIgnore] public double MagicResistance { get; init; }

    [JsonIgnore] public double CrowdControlResistance { get; init; }

    [JsonIgnore] public double BonusDefenseVsMobs { get; init; }

    [JsonIgnore] public double BonusDefenseVsPlayers { get; init; }

    /// <summary>Unique name without the @N enchantment suffix (what destiny board patterns match).</summary>
    [JsonIgnore] public string BaseName => Name.IndexOf('@') is var i and >= 0 ? Name[..i] : Name;

    /// <summary>The stat fields of a client item template (the enchantment's itempower and durability for @N).</summary>
    public static ItemData From(ItemTemplate t) => new()
    {
        Name = t.Name, Tier = t.Tier, Slot = t.Slot, ProgressionType = t.ProgressionType, TwoHanded = t.TwoHanded,
        ItemPower = t.ItemPower, MasteryModifier = t.MasteryModifier, HitPointsMax = t.HitPointsMax,
        HitPointsRegeneration = t.HitPointsRegeneration, EnergyMax = t.EnergyMax,
        HitPointsRegenerationBonus = t.HitPointsRegenerationBonus, EnergyRegenerationBonus = t.EnergyRegenerationBonus,
        MountHitPointsMax = t.MountHitPointsMax, MountHitPointsRegeneration = t.MountHitPointsRegeneration,
        Durability = t.MaxDurability,
        PhysicalArmor = Raw(t, "physicalarmor"), MagicResistance = Raw(t, "magicresistance"),
        CrowdControlResistance = Raw(t, "crowdcontrolresistance"), BonusDefenseVsMobs = Raw(t, "bonusdefensevsmobs"),
        BonusDefenseVsPlayers = Raw(t, "bonusdefensevsplayers"),
    };

    /// <summary>A raw items.xml attribute of <paramref name="t"/> as a number (invariant culture); 0 when absent or not a number.</summary>
    private static double Raw(ItemTemplate t, string name) =>
        t.Attrs.GetValueOrDefault(name) is { Length: > 0 } v && double.TryParse(v, NumberStyles.Float, CultureInfo.InvariantCulture, out var d) ? d : 0;
}

/// <summary>hitpointsprogression / energyprogression of one gamedata.xml ItemPowerProgression section.</summary>
public sealed record ItemPowerProgression(
    [property: JsonPropertyName("hp")] double HitPoints,
    [property: JsonPropertyName("en")] double Energy);

/// <summary>
/// The combat numbers of one gamedata.xml ItemPowerProgression section (stage 5b, SPEC §5b.2/§5b.3): an item's auto-attack
/// damage scales with attackdamageprogression^(IP/100), its spells' ability power with abilitypowerprogression^(IP/100), the
/// energy cost of the spells it grants with energyprogression^(IP/100) (mainhand_1h 1.0825 / 1.0825 / 1.0918), and the crowd
/// control power (ActiveSpellEffectsUpdate key 3, stageD effects SPEC §1) with ccpowerprogression^(IP/100) (mainhand_1h 1.0474).
/// </summary>
public sealed record CombatProgression(double AttackDamage, double AbilityPower, double Energy, double CcPower = 1);

/// <summary>
/// The defence numbers of one gamedata.xml ItemPowerProgression section (stageJ armour SPEC §2.2, bej.a_5 cases 0x20-0x22,
/// 0x2d, 0x31): armorbase / ccrbase (the share base of armour and crowd control resistance, 100 in every section), armorprogression
/// (1.03, armour and magic resistance), crowdcontrolresistanceprogression (1.0564), bonusdefensevsmobs / bonusdefensevsplayers
/// (1.16 for offhand and transformation, else 1). <see cref="Default"/> for an absent attribute.
/// </summary>
public sealed record DefenseProgression(double ArmorBase, double CcrBase, double Armor, double Ccr, double DefenseVsMobs, double DefenseVsPlayers)
{
    public static DefenseProgression Default { get; } = new(100, 100, 1, 1, 1, 1);
}

/// <summary>
/// One gamedata.xml &lt;ActiveBuffScaling&gt; row (stageJ armour SPEC §2.3, bgf.acz_1): the buff points on an attribute count from
/// <see cref="Min"/> to <see cref="Max"/> × S, S = max(base, <see cref="AbsoluteMin"/>) (PhysicalArmor, MagicResistance,
/// CrowdControlResistance: −0.6, unrestricted, 100). An absent or unparsable value ("unrestricted") is the client's default:
/// −∞ / +∞ / −∞ (S = base) in a listed row; an attribute without a row is <see cref="Unlisted"/>. <see cref="Mobs"/>: the
/// &lt;mobs&gt; child, its absent attributes the row's; null = the row itself.
/// </summary>
public sealed record BuffLimit(double Min, double Max, double AbsoluteMin)
{
    /// <summary>An attribute without a row: absolutemin 0, no min, no max.</summary>
    public static BuffLimit Unlisted { get; } = new(double.NegativeInfinity, double.PositiveInfinity, 0);

    /// <summary>The armour, magic resistance and crowd control resistance rows of 345319 (no &lt;mobs&gt; child).</summary>
    public static BuffLimit Resistance { get; } = new(-0.6, double.PositiveInfinity, 100);

    public BuffLimit? Mobs { get; init; }

    /// <summary>The row a mob (<paramref name="mob"/>) or a player uses.</summary>
    public BuffLimit For(bool mob) => mob ? Mobs ?? this : this;

    /// <summary>The rows of &lt;ActiveBuffScaling&gt; by attribute name (any case); null = no section.</summary>
    internal static Dictionary<string, BuffLimit>? Read(XElement? scaling)
    {
        if (scaling is null) return null;
        // Single.TryParse in the client (s.a_t1e8): what does not parse keeps the default.
        static double Value(XElement e, string name, double fallback) =>
            double.TryParse(e.Attribute(name)?.Value, NumberStyles.Float, CultureInfo.InvariantCulture, out var v) ? v : fallback;
        var rows = new Dictionary<string, BuffLimit>(StringComparer.OrdinalIgnoreCase);
        foreach (var a in scaling.Elements("attribute"))
        {
            if (a.Attribute("name")?.Value is not { Length: > 0 } name) continue;
            var row = new BuffLimit(Value(a, "min", double.NegativeInfinity), Value(a, "max", double.PositiveInfinity), Value(a, "absolutemin", double.NegativeInfinity));
            if (a.Element("mobs") is { } mobs)
                row = row with { Mobs = new BuffLimit(Value(mobs, "min", row.Min), Value(mobs, "max", row.Max), Value(mobs, "absolutemin", row.AbsoluteMin)) };
            rows.TryAdd(name, row);
        }
        return rows;
    }
}

/// <summary>
/// gamedata.xml &lt;ClusterDangerBonus type&gt; (stage 5c, SPEC §5c.2/§5c.4): the kill fame factor, the mob loot factor (NewLoot
/// key 15) and the mob silver factor of a cluster danger type (safe 2.15 / 1.25 / 1.45, yellow 2.4, …), and the harvest fame
/// factor (gatheringfamefactor, 1 in every type of 345319; HARVEST.md §4) and the fishing fame factor (fishingfamefactor: 1 in
/// safe..black, 1.25-1.5 in black1-6; stageG fishing SPEC §9).
/// </summary>
public sealed record DangerBonus(double FameFactor, double MobLootFactor, double MobSilverLootFactor, double GatheringFameFactor = 1, double FishingFameFactor = 1);

/// <summary>gamedata.xml DurabilityIndicator: applies from <see cref="Min"/> percent durability up to the next row.</summary>
public sealed record DurabilityIndicator(
    [property: JsonPropertyName("min")] double Min,
    [property: JsonPropertyName("ipr")] double ItemPowerReduction);

/// <summary>Character DefaultValues from characters.xml.</summary>
public sealed record CharacterBases(
    [property: JsonPropertyName("hp")] double HitPoints,
    [property: JsonPropertyName("hpr")] double HitPointsRegeneration,
    [property: JsonPropertyName("en")] double Energy,
    [property: JsonPropertyName("enr")] double EnergyRegeneration);

/// <summary>
/// A direct baserewards bonus type="itemwearbonus" attribute="itempower": per achievement level it adds
/// <see cref="Bonus"/> item power to worn items whose base name matches a pattern and whose tier lies in
/// [<see cref="MinTier"/>, <see cref="MaxTier"/>] (4-8 in this build).
/// </summary>
public sealed record WearBonus(
    [property: JsonPropertyName("p")] string[] Patterns,
    [property: JsonPropertyName("lo")] int MinTier,
    [property: JsonPropertyName("hi")] int MaxTier,
    [property: JsonPropertyName("b")] double Bonus);

/// <summary>
/// One destiny board node. Id = 0-based order of top-level &lt;achievement&gt;/&lt;templateachievement&gt;
/// in achievements.xml (b74.v 0x180822ad0 numbers them while loading).
/// </summary>
public sealed record AchievementData(
    [property: JsonPropertyName("id")] int Id,
    [property: JsonPropertyName("name")] string Name,
    [property: JsonPropertyName("tpl")] bool IsTemplate,
    [property: JsonPropertyName("lv")] int BaseLevels,
    [property: JsonPropertyName("wear")] WearBonus[]? Wear);

/// <summary>The FullAchievementInfo ids that unlock everything (analysis/work/unlocks/payload_unlock_all.json).</summary>
public sealed record UnlockAllIds(
    [property: JsonPropertyName("completed")] int[] Completed,
    [property: JsonPropertyName("templates")] int[] Templates);

/// <summary>
/// The GameData subset the server needs for stats and the destiny board, built at startup from the client's items.xml,
/// gamedata.xml, characters.xml and achievements.xml (<see cref="FromClient"/>). data/gamedata.json, which
/// analysis/tools/gen_gamedata.py made from the same files, is the golden reference of the self-test (<see cref="Load"/>).
/// </summary>
public sealed class GameData
{
    [JsonPropertyName("bases")] public required CharacterBases Bases { get; init; }

    /// <summary>ItemPowerProgression hitpointsshare / energyshare by itempowerprogressiontype ("hp", "en").</summary>
    [JsonPropertyName("shares")] public required Dictionary<string, Dictionary<string, double>> Shares { get; init; }

    [JsonPropertyName("progressions")] public required Dictionary<string, ItemPowerProgression> Progressions { get; init; }

    /// <summary>QualityLevels itempowerbonus by quality (1 = normal is not listed: 0).</summary>
    [JsonPropertyName("quality")] public required Dictionary<int, double> QualityItemPower { get; init; }

    /// <summary>DurabilityIndicators, highest threshold first.</summary>
    [JsonPropertyName("durability")] public required DurabilityIndicator[] Durability { get; init; }

    [JsonPropertyName("unlockAll")] public required UnlockAllIds UnlockAll { get; init; }

    /// <summary>Indexed by achievement id (checked on load).</summary>
    [JsonPropertyName("achievements")] public required AchievementData[] Achievements { get; init; }

    /// <summary>Indexed by item type id (bdh.da 0x18060a3f0 numbering, <see cref="ClientNumbering.Items"/>); [0] is null.</summary>
    [JsonPropertyName("items")] public required ItemData?[] Items { get; init; }

    /// <summary>
    /// gamedata.xml &lt;ClusterChange cooldownseconds="10"/&gt;: how long after a Join an exit may be used outside a city
    /// (bgf+0x70, read by ctp.auf 0x1809a8bc0; a city has 1 s, SPEC stage 3 §2.1). Not in data/gamedata.json, whose
    /// golden check leaves it out; 10 when the element is missing.
    /// </summary>
    [JsonIgnore] public double ClusterChangeCooldownSeconds { get; init; } = 10;

    /// <summary>characters.xml's value when it has no collisionradius.</summary>
    public const double DefaultCollisionRadius = 0.75;

    /// <summary>
    /// characters.xml &lt;DefaultValues collisionradius="0.75"/&gt;: the radius of a character's collision circle. The
    /// arrival of a cluster change is pushed by it out of the arrival tile's walk-blocking cells (Transfers.Clear; the
    /// bank doors' 0.75 m). Not in data/gamedata.json; <see cref="DefaultCollisionRadius"/> when the attribute is missing.
    /// </summary>
    [JsonIgnore] public double CollisionRadius { get; init; } = DefaultCollisionRadius;

    /// <summary>
    /// Stage I bots: characters.xml's AvatarToCustomizationPresets and customization items, the looks a bot is given (<see cref="CharacterLooks"/>).
    /// Not in data/gamedata.json; <see cref="CharacterLooks.None"/> (the default look) when read from it.
    /// </summary>
    [JsonIgnore] public CharacterLooks Looks { get; init; } = CharacterLooks.None;

    /// <summary>characters.xml's value when it has no harvestrange.</summary>
    public const double DefaultHarvestRange = 2.0;

    /// <summary>
    /// characters.xml &lt;DefaultValues harvestrange="2.0"/&gt;: how far a character reaches a harvestable, from the edge of
    /// its circle (the client's own check, cn2.af 0x180b87bf0: cn2.abq(position) − radius &gt; cpx.axd → its code 5). The
    /// captured stops were 2.66-2.67 m from the centre of a T1 bush (HARVEST.md §3: 1.25-3.11 m for every node). Not in
    /// data/gamedata.json; <see cref="DefaultHarvestRange"/> when the attribute is missing.
    /// </summary>
    [JsonIgnore] public double HarvestRange { get; init; } = DefaultHarvestRange;

    /// <summary>gamedata.xml's value when it has no &lt;Fishing safety&gt; (the client's own default, bgf.acj 0x1805d5f80).</summary>
    public const double DefaultFishingSafety = 3;

    /// <summary>
    /// gamedata.xml &lt;Fishing safety="3"/&gt;: the seconds the client's minigame waits before it reports a success (dlm.c
    /// 0x180ef00e0; stageG fishing SPEC §1, §5.2). The server refuses an earlier success (Systems/Fishing). Not in data/gamedata.json.
    /// </summary>
    [JsonIgnore] public double FishingSafety { get; init; } = DefaultFishingSafety;

    /// <summary>
    /// gamedata.xml &lt;DurabilityLoss&gt;&lt;ToolUse&gt;&lt;Resource tier="n" durabilityloss="x"/&gt;: the share of the resource's
    /// itemvalue a gathering tool loses per item harvested (T1 0, T2-T8 0.5; HARVEST.md §4: 1200 → 1194 for 3 T2_FIBER =
    /// 0.5 × 4 × 3). By resource tier; not in data/gamedata.json; <see cref="DefaultToolUseLoss"/> without game data.
    /// </summary>
    [JsonIgnore] public IReadOnlyDictionary<int, double> ToolUseLoss { get; init; } = DefaultToolUseLoss;

    /// <summary>The ToolUse table of 345319, for hand-made tables and a gamedata.xml without the section.</summary>
    public static readonly IReadOnlyDictionary<int, double> DefaultToolUseLoss =
        new Dictionary<int, double> { [1] = 0, [2] = 0.5, [3] = 0.5, [4] = 0.5, [5] = 0.5, [6] = 0.5, [7] = 0.5, [8] = 0.5 };

    /// <summary>
    /// The combat progressions of the gamedata.xml ItemPowerProgression sections by name (mainhand_1h, mainhand_2h, offhand,
    /// head, …; <see cref="ProgressionKey"/> picks an item's). Not in data/gamedata.json; empty without the section.
    /// </summary>
    [JsonIgnore] public IReadOnlyDictionary<string, CombatProgression> CombatProgressions { get; init; } = new Dictionary<string, CombatProgression>();

    /// <summary>
    /// The defence numbers of the ItemPowerProgression sections by name (stageJ armour SPEC §5.1; <see cref="DefenseProgressionOf"/>).
    /// Not in data/gamedata.json; empty without the section (no defence from items then).
    /// </summary>
    [JsonIgnore] public IReadOnlyDictionary<string, DefenseProgression> DefenseProgressions { get; init; } = new Dictionary<string, DefenseProgression>();

    /// <summary>The worn-item bonus attributes whose ItemPowerProgression scaling <see cref="BonusProgressions"/> reads (stageM pvp-damage NOTES §2).</summary>
    public static readonly string[] BonusAttributes =
    [
        "physicalspelldamagebonus", "magicspelldamagebonus", "physicalattackdamagebonus", "magicattackdamagebonus",
        "healbonus", "bonusccdurationvsmobs", "bonusccdurationvsplayers",
    ];

    /// <summary>
    /// The ItemPowerProgression sections' same-named progressions of <see cref="BonusAttributes"/> by section and attribute (offhand 1.16,
    /// every other section 1): a worn item's bonus is its items.xml value × progression^(IP/100) (<see cref="BonusProgression"/>,
    /// Systems.Combat.Worn). Not in data/gamedata.json; empty without the section (the raw values then).
    /// </summary>
    [JsonIgnore] public IReadOnlyDictionary<string, IReadOnlyDictionary<string, double>> BonusProgressions { get; init; } = new Dictionary<string, IReadOnlyDictionary<string, double>>();

    /// <summary>
    /// ItemPowerProgression &lt;armorshare&gt; / &lt;ccrshare&gt; by itempowerprogressiontype (armor 1.0 / 1.0, shield 0 / 0.60, talisman
    /// 0 / 1.8, crystaltorch 0 / −1.2, …; stageJ armour SPEC §2.6). Not in data/gamedata.json; empty without them.
    /// </summary>
    [JsonIgnore] public IReadOnlyDictionary<string, double> ArmorShares { get; init; } = new Dictionary<string, double>();

    [JsonIgnore] public IReadOnlyDictionary<string, double> CcrShares { get; init; } = new Dictionary<string, double>();

    /// <summary>
    /// gamedata.xml &lt;ActiveBuffScaling&gt; by attribute name, any case (stageJ armour SPEC §2.3; <see cref="BuffLimitOf"/>). Not in
    /// data/gamedata.json; <see cref="DefaultBuffLimits"/> without the section.
    /// </summary>
    [JsonIgnore] public IReadOnlyDictionary<string, BuffLimit> BuffLimits { get; init; } = DefaultBuffLimits;

    /// <summary>The armour rows of 345319, for hand-made tables and a gamedata.xml without the section.</summary>
    public static readonly IReadOnlyDictionary<string, BuffLimit> DefaultBuffLimits = new Dictionary<string, BuffLimit>(StringComparer.OrdinalIgnoreCase)
    {
        ["physicalarmor"] = BuffLimit.Resistance, ["magicresistance"] = BuffLimit.Resistance, ["crowdcontrolresistance"] = BuffLimit.Resistance,
    };

    /// <summary>The &lt;ActiveBuffScaling&gt; row of <paramref name="attribute"/> (any case); <see cref="BuffLimit.Unlisted"/> without one.</summary>
    public BuffLimit BuffLimitOf(string attribute) => BuffLimits.GetValueOrDefault(attribute) ?? BuffLimit.Unlisted;

    /// <summary>gamedata.xml ClusterDangerBonus by type (safe, yellow, orange, red, black, black1-6, …). Not in data/gamedata.json.</summary>
    [JsonIgnore] public IReadOnlyDictionary<string, DangerBonus> DangerBonuses { get; init; } = new Dictionary<string, DangerBonus>();

    /// <summary>
    /// &lt;ClusterDangerBonusTypes globalmobsilverlootfactor="0.9"&gt;: × the type's mobsilverlootfactor = TakeSilver key 8 (safe
    /// 1.45 × 0.9 = 1.305, stage 5c SPEC §5c.3). 1 when missing.
    /// </summary>
    [JsonIgnore] public double GlobalMobSilverLootFactor { get; init; } = 1;

    /// <summary>
    /// &lt;ClusterDangerBonusTypes globalmoblootfactor="0.9"&gt;: × the type's moblootfactor = the total loot factor of a mob's
    /// roll (safe 1.25 × 0.9 = 1.125, <see cref="Data.LootTables.RollMob"/>). 1 when missing.
    /// </summary>
    [JsonIgnore] public double GlobalMobLootFactor { get; init; } = 1;

    /// <summary>gamedata.xml's value when it has no &lt;Loot mobprotection&gt;.</summary>
    public const double DefaultMobLootProtection = 120;

    /// <summary>
    /// gamedata.xml &lt;Loot vanishtime="1800" mobprotection="120" …/&gt;: how long a killed mob's corpse harvestable is the
    /// killer's alone (NewHarvestableObject key 2 = the kill + 120.0 s on both own captured kills, HARVEST.md §2); a mobs.xml
    /// &lt;Harvestable lootprotectiontimeoverride&gt; wins (<see cref="Corpses"/>). <see cref="DefaultMobLootProtection"/> when missing.
    /// </summary>
    [JsonIgnore] public double MobLootProtectionSeconds { get; init; } = DefaultMobLootProtection;

    /// <summary>
    /// gamedata.xml &lt;InCombat activetimeoutseconds="20" passivetimeoutseconds="20"/&gt;: a player's combat ends this long after
    /// its last own hit (active) or the last damage taken (passive) (SPEC §5b.5). 20 when missing.
    /// </summary>
    [JsonIgnore] public double InCombatActiveSeconds { get; init; } = 20;

    [JsonIgnore] public double InCombatPassiveSeconds { get; init; } = 20;

    /// <summary>gamedata.xml &lt;StuckCommand cooldownseconds="1800" delayseconds="180"/&gt; (stageN qol SPEC §3): /stuck's wait and its cooldown.</summary>
    [JsonIgnore] public double StuckDelaySeconds { get; init; } = 180;

    [JsonIgnore] public double StuckCooldownSeconds { get; init; } = 1800;

    /// <summary>gamedata.xml &lt;DeathSettings&gt;: the PvE knockdown's timings (stage 5e, SPEC §5e). The 345319 values when missing.</summary>
    [JsonIgnore] public DeathSettings Death { get; init; } = DeathSettings.Default;

    /// <summary>gamedata.xml &lt;DuelSettings&gt; (stageE pvp SPEC §2.1). The 345319 values when missing.</summary>
    [JsonIgnore] public DuelSettings Duel { get; init; } = DuelSettings.Default;

    /// <summary>gamedata.xml &lt;BleedOutSettings&gt;&lt;BleedOutSetting name="DEFAULT"&gt; (stageN revive SPEC §2). The 345319 values when missing.</summary>
    [JsonIgnore] public BleedOutSettings BleedOut { get; init; } = BleedOutSettings.Default;

    /// <summary>gamedata.xml &lt;Reputation&gt; (stageE pvp SPEC §3.4). The 345319 values when missing.</summary>
    [JsonIgnore] public ReputationSettings Reputation { get; init; } = ReputationSettings.Default;

    /// <summary>gamedata.xml &lt;ItemLoss&gt;&lt;ItemLossEvent&gt;&lt;AliveToKnockedOut&gt; (stageE pvp SPEC §3.5, §4.4): a PvP knockdown's durability loss.</summary>
    [JsonIgnore] public DurabilityLossRule KnockedOutLoss { get; init; } = DurabilityLossRule.KnockedOut;

    /// <summary>The death of a lethal cluster (stageE pvp SPEC §4): its item loss events, loot bag times, respawn and kill fame numbers.</summary>
    [JsonIgnore] public PvpDeathSettings PvpDeath { get; init; } = PvpDeathSettings.Default;

    /// <summary>The PvP details of stageP (SPEC §2): the death recap window, healing sickness and resilience tables.</summary>
    [JsonIgnore] public PvpDetailSettings PvpDetails { get; init; } = PvpDetailSettings.Default;

    /// <summary>
    /// gamedata.xml &lt;AbilityPowerProgression&gt; and &lt;CrowdControlDiminishingReturns&gt;: the buff scaling and crowd
    /// control numbers of the spell effects (stageD effects SPEC §3.1, §3.2). The 345319 values when missing.
    /// </summary>
    [JsonIgnore] public EffectSettings Effects { get; init; } = EffectSettings.Default;

    /// <summary>
    /// The destiny board's fame and unlock rules (achievements.xml rows, mission types, item lists, neighbours, itemuse
    /// rewards; <see cref="DestinyBoard"/>). Not in data/gamedata.json; null there (no fame goes to the board then).
    /// </summary>
    [JsonIgnore] public DestinyBoard? Board { get; init; }

    /// <summary>
    /// characters.xml DefaultValues melee/ranged/magicattackdamagetime (0.25 / 0.32 / 0.36 s): from a player's auto-attack
    /// request to its damage, times the weapon's attackdamagetimefactor (1 when missing); ranged and magic add the
    /// projectile's flight. By items.xml attacktype.
    /// </summary>
    [JsonIgnore] public IReadOnlyDictionary<string, double> AttackDamageTimes { get; init; } =
        new Dictionary<string, double>(StringComparer.Ordinal) { ["melee"] = 0.25, ["ranged"] = 0.32, ["magic"] = 0.36 };

    /// <summary>characters.xml DefaultValues barehandrange / barehandphysicaldamage / barehandattackspeed (2 m, 20, 1/s): the auto-attack without a weapon.</summary>
    [JsonIgnore] public (double Range, double Damage, double Speed) Barehand { get; init; } = (2, 20, 1);

    /// <summary>
    /// characters.xml DefaultValues maxload (50 kg): a character's own carry load, before the maxload buffs of its bag, shoes and
    /// mount (stageD effects batch 2, <see cref="Systems.Passives.MaxLoad"/>).
    /// </summary>
    [JsonIgnore] public double MaxLoad { get; init; } = 50;

    /// <summary>characters.xml DefaultValues useobjectrange (2 m): how near a player must be to use an object, e.g. a loot bag.</summary>
    [JsonIgnore] public double UseObjectRange { get; init; } = 2;

    /// <summary>
    /// gamedata.xml &lt;Fame pvefamesharingradius="50"&gt;: the party members within this many metres of a killed mob share its fame
    /// (stageD party SPEC §5). 50 when missing.
    /// </summary>
    [JsonIgnore] public double PveFameSharingRadius { get; init; } = 50;

    /// <summary>
    /// gamedata.xml &lt;Fame&gt;&lt;GroupBonus&gt;&lt;Group size bonusfactor/&gt;: the fame factor of a group of that size (2: 1.1, 3: 1.17,
    /// 4: 1.33, 5-20: 1.4; stageD party SPEC §5). Empty when missing; <see cref="GroupBonus"/> reads it.
    /// </summary>
    [JsonIgnore] public IReadOnlyDictionary<int, double> GroupBonuses { get; init; } = new Dictionary<int, double>();

    /// <summary>The group bonus of <paramref name="size"/> players: 1 alone; else the factor of the largest listed size up to it (1.4 above 20), 1 without one.</summary>
    public double GroupBonus(int size) =>
        size <= 1 ? 1 : GroupBonuses.Where(g => g.Key <= size).OrderByDescending(g => g.Key).Select(g => g.Value).DefaultIfEmpty(1).First();

    /// <summary>The economy numbers of gamedata.xml (repair, crafting quality, focus, city bank; stage 6).</summary>
    [JsonIgnore] public EconomySettings Economy { get; init; } = EconomySettings.Default;

    /// <summary>
    /// characters.xml &lt;DefaultValues&gt;'s focus, learning point and premium values (stageJ focus-lp SPEC §1.1). Not in
    /// data/gamedata.json; <see cref="CharacterDefaults.Default"/> without the file.
    /// </summary>
    [JsonIgnore] public CharacterDefaults Defaults { get; init; } = CharacterDefaults.Default;

    /// <summary>
    /// Per achievement id its direct baserewards bonuses type="craftingfocuscostreduction" (focus-lp SPEC §1.3: units per level, the
    /// patterns and the tier gate, as <see cref="WearBonus"/>); null = none. Not in data/gamedata.json.
    /// </summary>
    [JsonIgnore] public WearBonus[]?[] FocusReductions { get; init; } = [];

    /// <summary>
    /// Per achievement id its direct baserewards bonuses type="itemcraftquality" (enchanting SPEC §3: quality points per level, the
    /// patterns and the tier gate, as <see cref="FocusReductions"/>); null = none. Not in data/gamedata.json.
    /// </summary>
    [JsonIgnore] public WearBonus[]?[] QualityBonusTable { get; init; } = [];

    private Dictionary<string, short>? _byName;
    private readonly Dictionary<int, (int Achievement, double Bonus)[]> _wear = new();
    private readonly Dictionary<int, (int Achievement, double Bonus)[]> _focus = new();
    private readonly Dictionary<int, (int Achievement, double Bonus)[]> _quality = new();

    /// <summary>
    /// The tables from the client's files, read as analysis/tools/gen_gamedata.py reads them: bases = characters.xml
    /// DefaultValues; shares = the attributes of ItemPowerProgression hitpointsshare / energyshare; progressions = its
    /// children with a hitpointsprogression; quality = QualityLevels itempowerbonus by level; durability =
    /// DurabilityIndicators, highest percentageaboveorequal first; achievements numbered by
    /// <see cref="ClientNumbering.Achievements"/>, a template achievement with the base level count of its template (the
    /// non-empty lines of &lt;baselevels&gt;), each with its direct baserewards bonus type="itemwearbonus"
    /// attribute="itempower"; unlock-all = the plain achievements finished and the template ones at their base level count.
    /// A missing section throws <see cref="InvalidDataException"/>; a number that does not parse reads as the default and
    /// is warned once per file and attribute (<see cref="NumberReader"/>), as is a quality level without a usable level.
    /// </summary>
    public static GameData FromClient(ItemStore items, XElement gamedata, XElement characters, IEnumerable<XElement> achievements)
    {
        static string? Attr(XElement e, string name) => e.Attribute(name)?.Value;
        static XElement Need(XElement root, string name, string file) =>
            root.Descendants(name).FirstOrDefault() ?? throw new InvalidDataException($"{file}: no <{name}>");
        NumberReader gd = new("gamedata.xml"), chars = new("characters.xml"), ach = new("achievements.xml");

        var ipp = Need(gamedata, "ItemPowerProgression", "gamedata.xml");
        Dictionary<string, double> Shares(string element) =>
            (ipp.Element(element) ?? throw new InvalidDataException($"gamedata.xml: no <{element}> in <ItemPowerProgression>"))
            .Attributes().Where(a => !a.IsNamespaceDeclaration).ToDictionary(a => a.Name.LocalName, a => gd.Num(a.Value, a.Name.LocalName, element));
        var progressions = new Dictionary<string, ItemPowerProgression>();
        foreach (var e in ipp.Elements())
            if (Attr(e, "hitpointsprogression") is { Length: > 0 } hp)
                progressions[e.Name.LocalName] = new(gd.Num(hp, "hitpointsprogression", e.Name.LocalName),
                    gd.Num(Attr(e, "energyprogression"), "energyprogression", e.Name.LocalName));
        var quality = new Dictionary<int, double>();
        foreach (var (e, i) in Need(gamedata, "QualityLevels", "gamedata.xml").Elements().Select((e, i) => (e, i)))
        {
            var where = $"<QualityLevels> entry {i + 1}";
            if (int.TryParse(Attr(e, "level"), NumberStyles.Integer, CultureInfo.InvariantCulture, out var level))
                quality[level] = gd.Num(Attr(e, "itempowerbonus"), "itempowerbonus", where);
            else gd.Note("level", where, Attr(e, "level") ?? "(none)", "entry skipped");
        }
        var durability = Need(gamedata, "DurabilityIndicators", "gamedata.xml").Elements("DurabilityIndicator")
            .Select((e, i) => (e, Where: $"<DurabilityIndicator> {i + 1}"))
            .Select(x => new DurabilityIndicator(gd.Num(Attr(x.e, "percentageaboveorequal"), "percentageaboveorequal", x.Where),
                gd.Num(Attr(x.e, "itempowerreduction"), "itempowerreduction", x.Where)))
            .OrderByDescending(d => d.Min).ToArray();
        var dv = Need(characters, "DefaultValues", "characters.xml");
        var combat = new Dictionary<string, CombatProgression>(StringComparer.Ordinal);
        foreach (var e in ipp.Elements())
            if (Attr(e, "abilitypowerprogression") is { Length: > 0 } ap)
                combat[e.Name.LocalName] = new(gd.Num(Attr(e, "attackdamageprogression"), "attackdamageprogression", e.Name.LocalName, 1),
                    gd.Num(ap, "abilitypowerprogression", e.Name.LocalName, 1), gd.Num(Attr(e, "energyprogression"), "energyprogression", e.Name.LocalName, 1),
                    gd.Num(Attr(e, "ccpowerprogression"), "ccpowerprogression", e.Name.LocalName, 1));
        var defense = new Dictionary<string, DefenseProgression>(StringComparer.Ordinal);
        foreach (var e in ipp.Elements())
            if (Attr(e, "armorprogression") is { Length: > 0 } armor)
            {
                var dp = DefenseProgression.Default;
                double Num(string name, double fallback) => Attr(e, name) is { Length: > 0 } v ? gd.Num(v, name, e.Name.LocalName, fallback) : fallback;
                defense[e.Name.LocalName] = new(Num("armorbase", dp.ArmorBase), Num("ccrbase", dp.CcrBase), gd.Num(armor, "armorprogression", e.Name.LocalName, 1),
                    Num("crowdcontrolresistanceprogression", dp.Ccr), Num("bonusdefensevsmobs", dp.DefenseVsMobs), Num("bonusdefensevsplayers", dp.DefenseVsPlayers));
            }
        var bonus = new Dictionary<string, IReadOnlyDictionary<string, double>>(StringComparer.Ordinal);
        foreach (var e in ipp.Elements())
        {
            var row = new Dictionary<string, double>(StringComparer.Ordinal);
            foreach (var name in BonusAttributes)
                if (Attr(e, name) is { Length: > 0 } v) row[name] = gd.Num(v, name, e.Name.LocalName, 1);
            if (row.Count > 0) bonus[e.Name.LocalName] = row;
        }
        Dictionary<string, double> OptionalShares(string element) => ipp.Element(element) is null ? [] : Shares(element);
        var danger = new Dictionary<string, DangerBonus>(StringComparer.Ordinal);
        foreach (var e in gamedata.Descendants("ClusterDangerBonus"))
            if (Attr(e, "type") is { Length: > 0 } type)
                danger.TryAdd(type, new(gd.Num(Attr(e, "famefactor"), "famefactor", type, 1), gd.Num(Attr(e, "moblootfactor"), "moblootfactor", type, 1),
                    gd.Num(Attr(e, "mobsilverlootfactor"), "mobsilverlootfactor", type, 1), gd.Num(Attr(e, "gatheringfamefactor"), "gatheringfamefactor", type, 1),
                    gd.Num(Attr(e, "fishingfamefactor"), "fishingfamefactor", type, 1)));
        var inCombat = gamedata.Descendants("InCombat").FirstOrDefault();
        var stuck = gamedata.Descendants("StuckCommand").FirstOrDefault();

        var top = achievements.ToList();
        var templates = new Dictionary<string, XElement>();
        foreach (var t in top)
            if (t.Name.LocalName == "template") templates[Attr(t, "name") ?? ""] = t;
        var list = new List<AchievementData>();
        var focusReductions = new List<WearBonus[]?>();
        var qualityBonuses = new List<WearBonus[]?>();
        foreach (var (id, el) in ClientNumbering.Achievements(top))
        {
            var isTemplate = el.Name.LocalName == "templateachievement";
            var levels = 0;
            if (isTemplate)
            {
                if (templates.GetValueOrDefault(Attr(el, "usetemplate") ?? "")?.Element("baselevels") is { } bl)
                    levels = string.Concat(bl.Nodes().TakeWhile(n => n is XText).Cast<XText>().Select(t => t.Value))
                        .Split('\n').Count(line => !string.IsNullOrWhiteSpace(line));
                else
                    Log.Warn("data", $"achievements.xml: {Attr(el, "id")} uses template {Attr(el, "usetemplate")} without <baselevels>: 0 levels");
            }
            var name = Attr(el, "id") ?? "";
            var wear = (el.Element("baserewards")?.Elements("bonus") ?? [])
                .Where(b => Attr(b, "type") == "itemwearbonus" && Attr(b, "attribute") == "itempower")
                .Select(b => new WearBonus([.. b.Elements("itempattern").Select(x => Attr(x, "pattern") ?? "")],
                    ach.Int(Attr(b, "mintier"), "mintier", name, 0), ach.Int(Attr(b, "maxtier"), "maxtier", name, 99),
                    ach.Num(Attr(b, "bonus"), "bonus", name)))
                .ToArray();
            list.Add(new AchievementData(id, name, isTemplate, levels, wear.Length > 0 ? wear : null));
            var focus = (el.Element("baserewards")?.Elements("bonus") ?? [])
                .Where(b => Attr(b, "type") == "craftingfocuscostreduction")
                .Select(b => new WearBonus([.. b.Elements("itempattern").Select(x => Attr(x, "pattern") ?? "")],
                    ach.Int(Attr(b, "mintier"), "mintier", name, 0), ach.Int(Attr(b, "maxtier"), "maxtier", name, 99),
                    ach.Num(Attr(b, "bonus"), "bonus", name)))
                .ToArray();
            focusReductions.Add(focus.Length > 0 ? focus : null);
            var craftQuality = (el.Element("baserewards")?.Elements("bonus") ?? [])
                .Where(b => Attr(b, "type") == "itemcraftquality")
                .Select(b => new WearBonus([.. b.Elements("itempattern").Select(x => Attr(x, "pattern") ?? "")],
                    ach.Int(Attr(b, "mintier"), "mintier", name, 0), ach.Int(Attr(b, "maxtier"), "maxtier", name, 99),
                    ach.Num(Attr(b, "bonus"), "bonus", name)))
                .ToArray();
            qualityBonuses.Add(craftQuality.Length > 0 ? craftQuality : null);
        }

        var itemData = new ItemData?[items.Count == 0 ? 1 : items.All.Max(t => t.Index) + 1];
        foreach (var t in items.All) itemData[t.Index] = ItemData.From(t);
        double Base(string attribute) => chars.Num(Attr(dv, attribute), attribute, "<DefaultValues>");
        var data = new GameData
        {
            Bases = new CharacterBases(Base("hitpointsmax"), Base("hitpointsregeneration"), Base("energymax"), Base("energyregeneration")),
            Shares = new() { ["hp"] = Shares("hitpointsshare"), ["en"] = Shares("energyshare") },
            Progressions = progressions,
            QualityItemPower = quality,
            Durability = durability,
            UnlockAll = new UnlockAllIds([.. list.Where(a => !a.IsTemplate).Select(a => a.Id)], [.. list.Where(a => a.IsTemplate).Select(a => a.Id)]),
            Achievements = [.. list],
            FocusReductions = [.. focusReductions],
            QualityBonusTable = [.. qualityBonuses],
            Defaults = CharacterDefaults.From(dv),
            Items = itemData,
            ClusterChangeCooldownSeconds = gd.Num(Attr(gamedata.Descendants("ClusterChange").FirstOrDefault() ?? new XElement("none"), "cooldownseconds"),
                "cooldownseconds", "<ClusterChange>", 10),
            CollisionRadius = chars.Num(Attr(dv, "collisionradius"), "collisionradius", "<DefaultValues>", DefaultCollisionRadius),
            Looks = CharacterLooks.Read(characters),
            HarvestRange = chars.Num(Attr(dv, "harvestrange"), "harvestrange", "<DefaultValues>", DefaultHarvestRange),
            FishingSafety = gd.Num(Attr(gamedata.Descendants("Fishing").FirstOrDefault() ?? new XElement("none"), "safety"), "safety", "<Fishing>", DefaultFishingSafety),
            CombatProgressions = combat,
            DefenseProgressions = defense,
            BonusProgressions = bonus,
            ArmorShares = OptionalShares("armorshare"),
            CcrShares = OptionalShares("ccrshare"),
            BuffLimits = BuffLimit.Read(gamedata.Descendants("ActiveBuffScaling").FirstOrDefault()) ?? DefaultBuffLimits,
            DangerBonuses = danger,
            GlobalMobSilverLootFactor = gamedata.Descendants("ClusterDangerBonusTypes").FirstOrDefault() is { } types && Attr(types, "globalmobsilverlootfactor") is { Length: > 0 } g
                ? gd.Num(g, "globalmobsilverlootfactor", "<ClusterDangerBonusTypes>", 1) : 1,
            GlobalMobLootFactor = gamedata.Descendants("ClusterDangerBonusTypes").FirstOrDefault() is { } lootTypes && Attr(lootTypes, "globalmoblootfactor") is { Length: > 0 } gl
                ? gd.Num(gl, "globalmoblootfactor", "<ClusterDangerBonusTypes>", 1) : 1,
            MobLootProtectionSeconds = gamedata.Descendants("Loot").FirstOrDefault(l => Attr(l, "mobprotection") is not null) is { } lootSettings
                ? gd.Num(Attr(lootSettings, "mobprotection"), "mobprotection", "<Loot>", DefaultMobLootProtection) : DefaultMobLootProtection,
            InCombatActiveSeconds = inCombat is null ? 20 : gd.Num(Attr(inCombat, "activetimeoutseconds"), "activetimeoutseconds", "<InCombat>", 20),
            InCombatPassiveSeconds = inCombat is null ? 20 : gd.Num(Attr(inCombat, "passivetimeoutseconds"), "passivetimeoutseconds", "<InCombat>", 20),
            StuckDelaySeconds = stuck is null ? 180 : gd.Num(Attr(stuck, "delayseconds"), "delayseconds", "<StuckCommand>", 180),
            StuckCooldownSeconds = stuck is null ? 1800 : gd.Num(Attr(stuck, "cooldownseconds"), "cooldownseconds", "<StuckCommand>", 1800),
            Death = gamedata.Descendants("DeathSettings").FirstOrDefault() is { } death
                ? new DeathSettings(
                    gd.Num(Attr(death, "getuptimeseconds"), "getuptimeseconds", "<DeathSettings>", DeathSettings.Default.GetUpSeconds),
                    gd.Num(Attr(death, "knockdownhealthregentopercent"), "knockdownhealthregentopercent", "<DeathSettings>", DeathSettings.Default.HealthRegenToPercent),
                    gd.Num(Attr(death, "knockdowngetuptimeseconds_pve"), "knockdowngetuptimeseconds_pve", "<DeathSettings>", DeathSettings.Default.PveSeconds),
                    gd.Num(Attr(death, "knockdowngetuptimeseconds_combatover"), "knockdowngetuptimeseconds_combatover", "<DeathSettings>", DeathSettings.Default.CombatOverSeconds))
                {
                    DuelSeconds = gd.Num(Attr(death, "knockdowngetuptimeseconds_duel"), "knockdowngetuptimeseconds_duel", "<DeathSettings>", DeathSettings.Default.DuelSeconds),
                    PvpSeconds = gd.Num(Attr(death, "knockdowngetuptimeseconds_pvp"), "knockdowngetuptimeseconds_pvp", "<DeathSettings>", DeathSettings.Default.PvpSeconds),
                    BuffAfterGetUp = Attr(death, "knockdownbuffaftergetup") is { Length: > 0 } buff ? buff : DeathSettings.Default.BuffAfterGetUp,
                }
                : DeathSettings.Default,
            Duel = gamedata.Descendants("DuelSettings").FirstOrDefault() is { } duel ? DuelSettings.Read(duel) : DuelSettings.Default,
            BleedOut = BleedOutSettings.Read(gamedata.Descendants("BleedOutSetting").FirstOrDefault(b => Attr(b, "name") == "DEFAULT")),
            Reputation = gamedata.Descendants("Reputation").FirstOrDefault(r => r.Elements("level").Any()) is { } reputation
                ? ReputationSettings.Read(reputation) : ReputationSettings.Default,
            KnockedOutLoss = DurabilityLossRule.Read(gamedata.Descendants("AliveToKnockedOut").FirstOrDefault()) ?? DurabilityLossRule.KnockedOut,
            PvpDeath = PvpDeathSettings.Read(gamedata),
            PvpDetails = PvpDetailSettings.Read(gamedata),
            AttackDamageTimes = new Dictionary<string, double>(StringComparer.Ordinal)
            {
                ["melee"] = chars.Num(Attr(dv, "meleeattackdamagetime"), "meleeattackdamagetime", "<DefaultValues>", 0.25),
                ["ranged"] = chars.Num(Attr(dv, "rangedattackdamagetime"), "rangedattackdamagetime", "<DefaultValues>", 0.32),
                ["magic"] = chars.Num(Attr(dv, "magicattackdamagetime"), "magicattackdamagetime", "<DefaultValues>", 0.36),
            },
            Barehand = (chars.Num(Attr(dv, "barehandrange"), "barehandrange", "<DefaultValues>", 2),
                chars.Num(Attr(dv, "barehandphysicaldamage"), "barehandphysicaldamage", "<DefaultValues>", 20),
                chars.Num(Attr(dv, "barehandattackspeed"), "barehandattackspeed", "<DefaultValues>", 1)),
            UseObjectRange = chars.Num(Attr(dv, "useobjectrange"), "useobjectrange", "<DefaultValues>", 2),
            MaxLoad = chars.Num(Attr(dv, "maxload"), "maxload", "<DefaultValues>", 50),
            PveFameSharingRadius = gamedata.Descendants("Fame").FirstOrDefault() is { } fame
                ? gd.Num(Attr(fame, "pvefamesharingradius"), "pvefamesharingradius", "<Fame>", 50) : 50,
            GroupBonuses = gamedata.Descendants("GroupBonus").Elements("Group")
                .Select((g, i) => (Size: gd.Int(Attr(g, "size"), "size", $"<GroupBonus> {i + 1}"), Factor: gd.Num(Attr(g, "bonusfactor"), "bonusfactor", $"<GroupBonus> {i + 1}", 1)))
                .GroupBy(g => g.Size).ToDictionary(g => g.Key, g => g.First().Factor),
            Economy = EconomySettings.From(gamedata),
            Effects = EffectSettings.From(gamedata, gd),
            Board = DestinyBoard.FromClient(top, items),
            ToolUseLoss = gamedata.Descendants("ToolUse").FirstOrDefault() is { } toolUse
                ? toolUse.Elements("Resource").ToDictionary(r => gd.Int(Attr(r, "tier"), "tier", "<ToolUse>"), r => gd.Num(Attr(r, "durabilityloss"), "durabilityloss", "<ToolUse>"))
                : DefaultToolUseLoss,
        };
        gd.Report();
        chars.Report();
        ach.Report();
        return data;
    }

    /// <summary>data/gamedata.json: the golden reference of the self-test (the server builds its tables with <see cref="FromClient"/>).</summary>
    public static GameData Load(string path)
    {
        using var s = File.OpenRead(path);
        var data = JsonSerializer.Deserialize<GameData>(s) ?? throw new InvalidDataException($"{path}: empty");
        for (var i = 0; i < data.Achievements.Length; i++)
            if (data.Achievements[i].Id != i) throw new InvalidDataException($"{path}: achievement {i} has id {data.Achievements[i].Id}");
        return data;
    }

    public ItemData? Item(int typeIndex) => typeIndex > 0 && typeIndex < Items.Length ? Items[typeIndex] : null;

    /// <summary>Item type id by unique name (with @N for enchantments), 0 = unknown.</summary>
    public short Find(string name)
    {
        _byName ??= Enumerable.Range(1, Items.Length - 1).Where(i => Items[i] is not null)
            .ToDictionary(i => Items[i]!.Name, i => (short)i, StringComparer.OrdinalIgnoreCase);
        return _byName.GetValueOrDefault(name);
    }

    public double HitPointsShare(string progressionType) => Share("hp", progressionType);
    public double EnergyShare(string progressionType) => Share("en", progressionType);

    private double Share(string kind, string type) =>
        Shares.TryGetValue(kind, out var m) && m.TryGetValue(type, out var v) ? v : 0;

    /// <summary>
    /// ItemPowerProgression section of an item (bgf.acg): mainhand_1h / mainhand_2h by twohanded,
    /// offhand for the offhand slot, otherwise the progression type or the slot; null = none
    /// (bej.a 0x180624200 then adds nothing).
    /// </summary>
    public ItemPowerProgression? ProgressionOf(ItemData item) => Progressions.GetValueOrDefault(ProgressionKey(item));

    /// <summary>The ItemPowerProgression section of an item (bgf.acg, as <see cref="ProgressionOf"/>): mainhand_1h/_2h, offhand, else the progression type or the slot.</summary>
    public string ProgressionKey(ItemData item) =>
        item.ProgressionType == "mainhand" ? (item.TwoHanded ? "mainhand_2h" : "mainhand_1h")
            : item.Slot == "offhand" ? "offhand"
            : Progressions.ContainsKey(item.ProgressionType) ? item.ProgressionType : item.Slot;

    /// <summary>The defence numbers of an item's section (<see cref="ProgressionKey"/>); null = none (the item adds no defence).</summary>
    public DefenseProgression? DefenseProgressionOf(ItemData item) => DefenseProgressions.GetValueOrDefault(ProgressionKey(item));

    /// <summary>The progression of a bonus <paramref name="attribute"/> in an item's section (<see cref="BonusProgressions"/>); 1 without one.</summary>
    public double BonusProgression(ItemData item, string attribute) =>
        BonusProgressions.GetValueOrDefault(ProgressionKey(item))?.GetValueOrDefault(attribute) is double v and > 0 ? v : 1;

    /// <summary>The combat progressions of an item's section (<see cref="ProgressionKey"/>); null = none (no scaling).</summary>
    public CombatProgression? CombatProgressionOf(ItemData item) => CombatProgressions.GetValueOrDefault(ProgressionKey(item));

    /// <summary>
    /// Item power lost to wear (bc9.a_18 0x1805fd9a0): itempowerreduction of the DurabilityIndicator with the
    /// highest percentageaboveorequal &lt;= (float)(current / max) * 100 (the client sorts the list ascending,
    /// bca.z / bca.a.CompareTo 0x18059ed20, and stops at the first higher threshold). Items without durability
    /// and ratios below every threshold lose nothing, as in the client. Not verified: gamedata ItemBrokenState
    /// (percentunder 0.1, bca.aa 0x180597a30), i.e. whether a broken item still adds stats.
    /// </summary>
    public double DurabilityPenalty(ItemData item, long durabilityRaw)
    {
        if (item.Durability <= 0) return 0;
        var percent = (float)(durabilityRaw / (item.Durability * 10000)) * 100f;
        foreach (var d in Durability)
            if (percent >= d.Min) return d.ItemPowerReduction;
        return 0;
    }

    /// <summary>
    /// Destiny board wear bonuses that apply to an item type: (achievement id, item power per level) for
    /// every itemwearbonus/itempower entry whose tier range holds the item's tier and whose patterns match
    /// its base name. Cached per type.
    /// </summary>
    public (int Achievement, double Bonus)[] WearBonuses(int typeIndex)
    {
        lock (_wear)
        {
            if (_wear.TryGetValue(typeIndex, out var cached)) return cached;
            var list = new List<(int, double)>();
            if (Item(typeIndex) is { } item)
            {
                var name = item.BaseName;
                foreach (var a in Achievements)
                    foreach (var w in a.Wear ?? [])
                        if (item.Tier >= w.MinTier && item.Tier <= w.MaxTier && w.Patterns.Any(p => Glob(p, name)))
                            list.Add((a.Id, w.Bonus));
            }
            return _wear[typeIndex] = list.ToArray();
        }
    }

    /// <summary>
    /// The destiny board's focus cost reductions for crafting an item type (stageJ focus-lp SPEC §1.3, §5.2): (achievement id, units per
    /// level) for every craftingfocuscostreduction entry whose tier range holds the item's tier and whose patterns match its base name,
    /// as <see cref="WearBonuses"/>. Cached per type.
    /// </summary>
    public (int Achievement, double Bonus)[] FocusBonuses(int typeIndex) => Matching(_focus, FocusReductions, typeIndex);

    /// <summary>
    /// The destiny board's crafting quality bonuses for an item type (enchanting SPEC §3): (achievement id, quality points per level) for
    /// every itemcraftquality entry whose tier range holds the item's tier and whose patterns match its base name. Cached per type.
    /// </summary>
    public (int Achievement, double Bonus)[] QualityBonuses(int typeIndex) => Matching(_quality, QualityBonusTable, typeIndex);

    private (int Achievement, double Bonus)[] Matching(Dictionary<int, (int Achievement, double Bonus)[]> cache, WearBonus[]?[] table, int typeIndex)
    {
        lock (cache)
        {
            if (cache.TryGetValue(typeIndex, out var cached)) return cached;
            var list = new List<(int, double)>();
            if (Item(typeIndex) is { } item)
            {
                var name = item.BaseName;
                for (var a = 0; a < table.Length; a++)
                    foreach (var w in table[a] ?? [])
                        if (item.Tier >= w.MinTier && item.Tier <= w.MaxTier && w.Patterns.Any(p => Glob(p, name)))
                            list.Add((a, w.Bonus));
            }
            return cache[typeIndex] = list.ToArray();
        }
    }

    /// <summary>achievements.xml itempattern match: '?' = one character, '*' = any run, case-sensitive.</summary>
    public static bool Glob(string pattern, string name)
    {
        int p = 0, n = 0, star = -1, mark = 0;
        while (n < name.Length)
        {
            if (p < pattern.Length && (pattern[p] == '?' || pattern[p] == name[n])) { p++; n++; }
            else if (p < pattern.Length && pattern[p] == '*') { star = p++; mark = n; }
            else if (star >= 0) { p = star + 1; n = ++mark; }
            else return false;
        }
        while (p < pattern.Length && pattern[p] == '*') p++;
        return p == pattern.Length;
    }
}

/// <summary>
/// gamedata.xml &lt;DeathSettings&gt; as the PvE knockdown uses it (stage 5e, SPEC §5e.1-§5e.2): the get-up animation
/// (getuptimeseconds, 3 s, before the invulnerability buff), the share of the maximum the knocked-down health regenerates to
/// (knockdownhealthregentopercent, 1 = all of it) over knockdowngetuptimeseconds_pve (600 s, the latest get-up), or over
/// knockdowngetuptimeseconds_combatover (18 s) once no mob fights the player any more.
/// </summary>
public sealed record DeathSettings(double GetUpSeconds, double HealthRegenToPercent, double PveSeconds, double CombatOverSeconds)
{
    public static DeathSettings Default { get; } = new(3, 1, 600, 18);

    /// <summary>knockdowngetuptimeseconds_duel: a duel's knockdown (6 s, stageE pvp SPEC §2.5; 6.000 s in 3 of 3 captured).</summary>
    public double DuelSeconds { get; init; } = 6;

    /// <summary>knockdowngetuptimeseconds_pvp: a PvP knockdown where the archetype names none (180 s, stageE pvp SPEC §3.5).</summary>
    public double PvpSeconds { get; init; } = 180;

    /// <summary>knockdownbuffaftergetup: the PvP get-up buff where the archetype names none (SPAWN_KNOCKDOWN, SPEC §3.5).</summary>
    public string BuffAfterGetUp { get; init; } = "SPAWN_KNOCKDOWN";
}

/// <summary>
/// gamedata.xml &lt;BleedOutSetting name="DEFAULT"&gt; (client bgt; stageN revive SPEC §2): the bleed-out points (totalpoints 120) and their loss
/// per second with a standing ally within closerange (43 m: 1), without one (4) or with every ally down (30), the health of a revive
/// (revivehitpointspercentage 30), its debuff (onrevivebuff BLEEDOUT_REVIVE_DEBUFF) and the reviver's spell (playerrevivespell
/// PLAYER_BLEEDOUT_REVIVE, CharacterSpellSlot 27).
/// </summary>
public sealed record BleedOutSettings(double TotalPoints, double LossPerSecond, double LossNoAllyClose, double CloseRange, double LossAllDown,
    double ReviveHealthPercent, string ReviveBuff, string ReviveSpell)
{
    public static BleedOutSettings Default { get; } = new(120, 1, 4, 43, 30, 30, "BLEEDOUT_REVIVE_DEBUFF", "PLAYER_BLEEDOUT_REVIVE");

    public static BleedOutSettings Read(System.Xml.Linq.XElement? e)
    {
        if (e is null) return Default;
        var d = Default;
        double N(string name, double fallback) =>
            double.TryParse(e.Attribute(name)?.Value, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var v) ? v : fallback;
        string S(string name, string fallback) => e.Attribute(name)?.Value is { Length: > 0 } v ? v : fallback;
        return new(N("totalpoints", d.TotalPoints), N("pointlosspersecond", d.LossPerSecond), N("pointlosspersecondnoallyclose", d.LossNoAllyClose),
            N("closerange", d.CloseRange), N("pointlosspersecondallalliesdeadorknockeddown", d.LossAllDown), N("revivehitpointspercentage", d.ReviveHealthPercent),
            S("onrevivebuff", d.ReviveBuff), S("playerrevivespell", d.ReviveSpell));
    }
}

/// <summary>
/// gamedata.xml &lt;DuelSettings&gt; (bgf.acd 0x1805D3060, stageE pvp SPEC §2.1), each a child &lt;Name value="…"/&gt;: the area
/// radius (25 m, also the challenge range) and the time a duelist may stay out of it (10 s), the challenge's height limit (5 m),
/// the accept countdown (13 s), the preparation (10 s), the longest duel (5 min), the get-up buff (DUEL_SPAWN_KNOCKDOWN) and
/// the time between two challenges of one player (10 s).
/// </summary>
public sealed record DuelSettings(double AreaRadius, double LeftAreaSeconds, double MaxHeightDifference, double AcceptSeconds, double PreparationSeconds,
    double MaxMinutes, string GetUpBuff, double IssueIntervalSeconds)
{
    public static DuelSettings Default { get; } = new(25, 10, 5, 13, 10, 5, "DUEL_SPAWN_KNOCKDOWN", 10);

    internal static DuelSettings Read(XElement e)
    {
        double V(string name, double fallback) =>
            e.Element(name)?.Attribute("value")?.Value is { } v && double.TryParse(v, NumberStyles.Float, CultureInfo.InvariantCulture, out var x) ? x : fallback;
        var d = Default;
        return new DuelSettings(V("DuelAreaRadius", d.AreaRadius), V("DuelAreaLeftTimerInSeconds", d.LeftAreaSeconds),
            V("DuelMaxAllowedHeightDifference", d.MaxHeightDifference), V("AcceptDuelCountDownInSeconds", d.AcceptSeconds),
            V("DuelPreparationTimeInSeconds", d.PreparationSeconds), V("DuelMaxDurationInMins", d.MaxMinutes),
            e.Element("DuelKnockDownGetUpBuff")?.Attribute("name")?.Value is { Length: > 0 } buff ? buff : d.GetUpBuff,
            V("IssueDuelIntervalTimeInSeconds", d.IssueIntervalSeconds));
    }
}

/// <summary>
/// The spell effect numbers of gamedata.xml (stageD effects SPEC §3.1, §3.2): &lt;AbilityPowerProgression&gt; basedamage 100,
/// baseload 25, hitpointprogression 0.6634, resistanceprogression 0.3366, loadprogression 2.2641 (bej.a_4's buff scaling),
/// ccdurationfactorplayers 0.7 / mobs 0.5 (bej.a_3); &lt;CrowdControlDiminishingReturns&gt; diminishingreturnmax 0.8 and per
/// crowd control type (stun, root, knockback, silence, slow, forcedmovement, transformationcc) its typefactor and
/// decreasetime (seconds); &lt;ReflectedDamage maxReflectCapacityFactorOfMaxHp 2&gt; (<see cref="ReflectCap"/>).
/// </summary>
public sealed record EffectSettings(double BaseDamage, double BaseLoad, double HitPointProgression, double ResistanceProgression,
    double LoadProgression, double CcFactorPlayers, double CcFactorMobs, double DiminishingMax,
    IReadOnlyDictionary<string, (double Factor, double DecreaseSeconds)> Diminishing)
{
    /// <summary>
    /// maxReflectCapacityFactorOfMaxHp: the damage a reflectdamageactive sends back from one hit is at most this × the reflector's
    /// maximum health (stageJ pvp-spells SPEC §2.5, phase 3; per hit [inference]).
    /// </summary>
    public double ReflectCap { get; init; } = 2;

    public static EffectSettings Default { get; } = new(100, 25, 0.663445601, 0.336554399, 2.264101591, 0.7, 0.5, 0.8,
        new Dictionary<string, (double, double)>(StringComparer.Ordinal)
        {
            ["stun"] = (0.3, 5), ["root"] = (0.3, 5), ["knockback"] = (0.08, 10), ["silence"] = (0.3, 5), ["slow"] = (0.3, 10),
            ["forcedmovement"] = (0.3, 10), ["transformationcc"] = (0.3, 5),
        });

    /// <summary>The three sections of gamedata.xml; a missing one or attribute keeps the 345319 value.</summary>
    internal static EffectSettings From(XElement gamedata, NumberReader gd)
    {
        var d = Default;
        var ap = gamedata.Descendants("AbilityPowerProgression").FirstOrDefault();
        var dr = gamedata.Descendants("CrowdControlDiminishingReturns").FirstOrDefault();
        var reflected = gamedata.Descendants("ReflectedDamage").FirstOrDefault();
        double Num(XElement? e, string name, double fallback) =>
            e?.Attribute(name)?.Value is { Length: > 0 } v ? gd.Num(v, name, "<" + e.Name.LocalName + ">", fallback) : fallback;
        var types = new Dictionary<string, (double, double)>(StringComparer.Ordinal);
        foreach (var (type, (factor, seconds)) in d.Diminishing)
            types[type] = (Num(dr, "typefactor" + type, factor), Num(dr, "decreasetime" + type, seconds));
        return new EffectSettings(Num(ap, "basedamage", d.BaseDamage), Num(ap, "baseload", d.BaseLoad),
            Num(ap, "hitpointprogression", d.HitPointProgression), Num(ap, "resistanceprogression", d.ResistanceProgression),
            Num(ap, "loadprogression", d.LoadProgression), Num(ap, "ccdurationfactorplayers", d.CcFactorPlayers),
            Num(ap, "ccdurationfactormobs", d.CcFactorMobs), Num(dr, "diminishingreturnmax", d.DiminishingMax), types)
        {
            ReflectCap = Num(reflected, "maxReflectCapacityFactorOfMaxHp", d.ReflectCap),
        };
    }
}
