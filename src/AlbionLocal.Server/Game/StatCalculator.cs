namespace AlbionLocal.Server.Game;

/// <summary>
/// An item in an equipment slot: type id, quality (NewEquipmentItem key 7; 0 = 1) and durability as
/// a7m raw (key 8, 10000 = 1 items.xml durability point).
/// </summary>
public sealed record EquippedItem(short TypeIndex, int Quality, long DurabilityRaw);

/// <summary>
/// Player bars as the server sends them: Join keys 12/14, 17/19, 22/24 and
/// RegenerationPlayerComboChanged keys 3/5, 8/10, 13/15 (max, regeneration per second); Entities.Player keeps them
/// as its ResourceBars.
/// </summary>
public sealed record CharacterStats(float MaxHealth, float HealthRegen, float MaxEnergy, float EnergyRegen,
    float MountMaxHealth, float MountHealthRegen)
{
    /// <summary>Without game data: a naked character's values (characters.xml defaults).</summary>
    public static CharacterStats Naked { get; } = new(1200, 12, 120, 1.5f, 0, 0);
}

/// <summary>
/// The defence of a player's worn items before buffs (stageJ armour SPEC §5.2; <see cref="StatCalculator.Defense(IReadOnlyList{EquippedItem?}, IReadOnlyDictionary{int, int}, bool)"/>):
/// physical armour, magic resistance and crowd control resistance points, and the bonusdefensevsmobs / vsplayers shares (damage
/// × 1 / (1 + Σ)). The client computes the same numbers for its character window; no message carries them.
/// </summary>
public sealed record DefenseStats(double Armor, double MagicResistance, double Ccr, double DefenseVsMobs, double DefenseVsPlayers)
{
    /// <summary>Naked, or without game data.</summary>
    public static DefenseStats None { get; } = new(0, 0, 0, 0, 0);
}

/// <summary>
/// An item power cap (stageP league-pvp SPEC §2.5, the arena's Arena:ItemPowerCap): each item's power above <see cref="Cap"/> counts
/// × <see cref="Factor"/> (0 = a hard cap; the 1v1's 0.2: 1000 → 920).
/// </summary>
public readonly record struct ItemPowerCap(double Cap, double Factor)
{
    public double Apply(double ip) => ip <= Cap ? ip : Cap + (ip - Cap) * Factor;

    /// <summary><paramref name="ip"/> under <paramref name="cap"/>, unchanged without one.</summary>
    public static float Apply(float ip, ItemPowerCap? cap) => cap is { } c ? (float)c.Apply(ip) : ip;
}

/// <summary>
/// Health/energy from the equipment, as the client computes item stats (bc9.a 0x1805fcfc0 item power,
/// bej.a 0x180624200 attribute contribution, float arithmetic). Verified against the official Join and
/// RegenerationPlayerComboChanged values (analysis/work/impl-stats/harness). Not modelled: buffs, food,
/// combat/rest regeneration states, overcharge and zone item power caps.
/// </summary>
public sealed class StatCalculator(GameData data)
{
    /// <summary>MainHand..Cape (EquipmentSlot 0-6) carry character stats.</summary>
    public const int StatSlots = 7;
    public const int MountSlot = 7;

    public GameData Data { get; } = data;

    /// <summary>
    /// slots10: EquipmentSlot order (null = empty; a shorter list counts as empty slots). levels: destiny
    /// board level by achievement id (FullAchievementInfo keys 2/3, see <see cref="Progression.Levels"/>; finished
    /// plain achievements carry no item power bonuses).
    /// Max health/energy are rounded (the official values are whole numbers; the midpoint rule is not
    /// known), regeneration is not.
    /// </summary>
    public CharacterStats Compute(IReadOnlyList<EquippedItem?> slots10, IReadOnlyDictionary<int, int> levels, ItemPowerCap? cap = null)
    {
        var b = Data.Bases;
        float baseHp = (float)b.HitPoints, baseHpRegen = (float)b.HitPointsRegeneration;
        float baseEn = (float)b.Energy, baseEnRegen = (float)b.EnergyRegeneration;
        float hp = baseHp, hpRegen = baseHpRegen, en = baseEn, enRegen = baseEnRegen;
        float hpRegenBonus = 0, enRegenBonus = 0;
        for (var s = 0; s < Math.Min(StatSlots, slots10.Count); s++)
        {
            if (slots10[s] is not { } e || Data.Item(e.TypeIndex) is not { } item) continue;
            // flat items.xml values, summed and applied as (1 + sum) to the regeneration totals
            hpRegenBonus += (float)item.HitPointsRegenerationBonus;
            enRegenBonus += (float)item.EnergyRegenerationBonus;
            if (Data.ProgressionOf(item) is not { } prog) continue;
            var f = ItemPowerCap.Apply(ItemPower(e, levels), cap) / 100f;
            var ph = (float)Math.Pow((float)prog.HitPoints, f);
            var pe = (float)Math.Pow((float)prog.Energy, f);
            var sh = (float)Data.HitPointsShare(item.ProgressionType);
            var se = (float)Data.EnergyShare(item.ProgressionType);
            hp += Contribution(sh * baseHp, (float)item.HitPointsMax, ph);
            hpRegen += Contribution(sh * baseHpRegen, (float)item.HitPointsRegeneration, ph);
            en += Contribution(se * baseEn, (float)item.EnergyMax, pe);
            enRegen += Contribution(se * baseEnRegen, 0, pe);
        }
        var mount = slots10.Count > MountSlot && slots10[MountSlot] is { } m ? Data.Item(m.TypeIndex) : null;
        return new CharacterStats(
            MathF.Round(hp, MidpointRounding.AwayFromZero), hpRegen * (1 + hpRegenBonus),
            MathF.Round(en, MidpointRounding.AwayFromZero), enRegen * (1 + enRegenBonus),
            (float)(mount?.MountHitPointsMax ?? 0), (float)(mount?.MountHitPointsRegeneration ?? 0));
    }

    /// <summary>
    /// The defence of the worn items (stageJ armour SPEC §5.2, bej.a_5 cases 0x20-0x22, 0x2d, 0x31): over slots 0..6 (main hand to
    /// cape) the physical armour and magic resistance (share_armor × armorbase + value) × armorprogression^(IP/100) − share_armor ×
    /// armorbase, the crowd control resistance the same with ccrshare / ccrbase / crowdcontrolresistanceprogression, the defence vs
    /// mobs / players value × bonusdefensevs*^(IP/100); IP = <see cref="ItemPower"/> (quality, destiny levels, durability). Only the
    /// chest has an armour share (1.0), so T4_ARMOR_PLATE_SET1 at IP 700 gives 198.86 / 160.73 / 105.55 and a T4 shield 28.09 crowd
    /// control resistance and 0.0305 defence. <paramref name="scaled"/> false (Combat:GearArmor false): the raw items.xml values
    /// summed, as before stageJ (no crowd control resistance, no defence vs mobs).
    /// </summary>
    public DefenseStats Defense(IReadOnlyList<EquippedItem?> slots10, IReadOnlyDictionary<int, int> levels, bool scaled = true, ItemPowerCap? cap = null)
    {
        var items = new List<(ItemData, double)>();
        for (var s = 0; s < Math.Min(StatSlots, slots10.Count); s++)
            if (slots10[s] is { } e && Data.Item(e.TypeIndex) is { } item)
                items.Add((item, scaled ? ItemPowerCap.Apply(ItemPower(e, levels), cap) : 0));
        return scaled ? Defense(items) : RawDefense(items);
    }

    /// <summary><see cref="Defense(IReadOnlyList{EquippedItem?}, IReadOnlyDictionary{int, int}, bool)"/> for items at explicit item powers (the self-test).</summary>
    public DefenseStats Defense(IReadOnlyList<(ItemData Item, double Ip)> items)
    {
        double armor = 0, mr = 0, ccr = 0, mobs = 0, players = 0;
        foreach (var (item, ip) in items)
        {
            if (Data.DefenseProgressionOf(item) is not { } p) continue;
            var f = ip / 100;
            var sa = Data.ArmorShares.GetValueOrDefault(item.ProgressionType) * p.ArmorBase;
            var sc = Data.CcrShares.GetValueOrDefault(item.ProgressionType) * p.CcrBase;
            var pa = Math.Pow(p.Armor, f);
            armor += (sa + item.PhysicalArmor) * pa - sa;
            mr += (sa + item.MagicResistance) * pa - sa;
            ccr += (sc + item.CrowdControlResistance) * Math.Pow(p.Ccr, f) - sc;
            mobs += item.BonusDefenseVsMobs * Math.Pow(p.DefenseVsMobs, f);
            players += item.BonusDefenseVsPlayers * Math.Pow(p.DefenseVsPlayers, f);
        }
        return new DefenseStats(armor, mr, ccr, mobs, players);
    }

    /// <summary>The legacy sum (Combat:GearArmor false): items.xml physicalarmor, magicresistance and bonusdefensevsplayers as they stand.</summary>
    private static DefenseStats RawDefense(IEnumerable<(ItemData Item, double Ip)> items)
    {
        double armor = 0, mr = 0, players = 0;
        foreach (var (item, _) in items)
        {
            armor += item.PhysicalArmor;
            mr += item.MagicResistance;
            players += item.BonusDefenseVsPlayers;
        }
        return new DefenseStats(armor, mr, 0, 0, players);
    }

    /// <summary>bej.a: (share * character base + item value) * progression^(IP/100) - share * character base.</summary>
    private static float Contribution(float shareBase, float itemValue, float scale) => (shareBase + itemValue) * scale - shareBase;

    /// <summary>
    /// bc9.a: items.xml itempower (the enchantment's for @N) + quality bonus
    /// + (1 + masterymodifier) * destiny board bonus - durability penalty.
    /// </summary>
    public float ItemPower(EquippedItem e, IReadOnlyDictionary<int, int> levels)
    {
        if (Data.Item(e.TypeIndex) is not { } item) return 0;
        var basePower = (float)item.ItemPower + (float)Data.QualityItemPower.GetValueOrDefault(e.Quality);
        var bonus = (1f + (float)item.MasteryModifier) * DestinyBonus(e.TypeIndex, levels);
        return basePower + (bonus - (float)Data.DurabilityPenalty(item, e.DurabilityRaw));
    }

    /// <summary>
    /// Sum of itemwearbonus/itempower * current level over the achievements that cover the item. The level
    /// is capped at the achievement's base level count: elite levels (101-120 on 238 templates) use separate
    /// elite rewards that are not modelled yet, and the captures only cover levels up to 71.
    /// </summary>
    public float DestinyBonus(short typeIndex, IReadOnlyDictionary<int, int> levels)
    {
        var sum = 0f;
        foreach (var (achievement, bonus) in Data.WearBonuses(typeIndex))
            if (levels.TryGetValue(achievement, out var level) && level > 0)
                sum += (float)bonus * Math.Min(level, Data.Achievements[achievement].BaseLevels);
        return sum;
    }
}
