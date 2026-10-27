using System.Globalization;
using System.Numerics;
using AlbionLocal.Photon;
using AlbionLocal.Server.Data;
using AlbionLocal.Server.Entities;
using AlbionLocal.Server.Game;
using AlbionLocal.Server.Protocol;

namespace AlbionLocal.Server.Systems;

/// <summary>HealthUpdate key 4 (EffectType): 1 physical, 2 magic; heals are always 2 (SPEC §5b.5).</summary>
public enum EffectType : byte
{
    Physical = 1,
    Magic = 2,
}

/// <summary>HealthUpdate key 5, what caused the change (SPEC §5b.5, inferred from both captures and spells.xml).</summary>
public enum HitOrigin : byte
{
    MeleeAutoAttack = 0,
    RangedAutoAttack = 1,
    Direct = 2,
    Area = 3,
    OnHitPassive = 4,
    OverTime = 5,
    Reflected = 6,
}

/// <summary>
/// Damage, death and the combat state of stage 5b/5c (analysis/work/stage5/SPEC.md §5b.2, §5b.5, §5c.1-§5c.2; ARCHITECTURE.md
/// §7 "Systems/Combat"): the formulas (each reproduces the captures to the integer), the auto-attack and ability power of
/// a player's items, HealthUpdate on a mob, the player's combat state with its regeneration (InCombatStateUpdate 277,
/// RegenerationHealthChanged 91), and a mob's death in the SPEC's corrected order: rewards (UpdateFame, the energy reward),
/// the killer's combat end, NewLoot, the death clear, the killing hit, Leave after postDeathLifetime, the respawn. Since 5d
/// mobs hit back (<see cref="DamagePlayer"/>, swung by <see cref="AI.AggressiveAI"/>), and damage and heals feed their threat.
/// The silver pile (NewSilverObject 44, taken with TakeSilver) since stage 6a, the skinnable corpse (NewHarvestableObject 40 of
/// hide and fiber critters, <see cref="Corpses"/>) in its place. Not modelled (TODO): ReputationUpdate 304, AchievementProgressInfo 152 / FinishedAchievement 151 (destiny board
/// progression comes in a later chain), UpdateLearningPoints 83. Armour and resistances: the worn items with their item power
/// (<see cref="Player.Defense"/>) and the buff points clamped by &lt;ActiveBuffScaling&gt; (<see cref="Buffed"/>, stageJ armour SPEC). The player's knockdown at 0 health is <see cref="Knockdown"/> (5e). Touched only under <see cref="World.Sync"/>.
/// </summary>
public static class Combat
{
    /// <summary>bej.d: a heal scales with (ability power / 100) to this power (hitpointprogression).</summary>
    public const double HealExponent = 0.6634;

    /// <summary>EnergyUpdate key 4 of a cast's energy cost.</summary>
    public const byte EnergyCast = 7;

    /// <summary>EnergyUpdate key 4 of a kill's energy reward.</summary>
    public const byte EnergyReward = 8;

    // ---- formulas (SPEC §5b.2, §5b.3, §5c.2) ----

    /// <summary>cpo.ayk/aym: the share of damage that gets through physicalarmor or magicresistance <paramref name="value"/>, 100 / (100 + value).</summary>
    public static double Resistance(double value) => 100 / (100 + value);

    /// <summary>An item power scaling: <paramref name="progression"/>^(IP / 100).</summary>
    public static double Scale(double progression, double itemPower) => Math.Pow(progression, itemPower / 100);

    /// <summary>A player's auto-attack before resistance: weapon attackdamage × attackdamageprogression^(IP/100) (T4_MAIN_AXE IP 945: 41 × 2.116).</summary>
    public static double AutoAttack(double attackDamage, double progression, double itemPower) => attackDamage * Scale(progression, itemPower);

    /// <summary>bej.b: an item's ability power, abilitypower × abilitypowerprogression^(IP/100) (138 × 2.116 = 292 for the axe at IP 945).</summary>
    public static double AbilityPower(double abilityPower, double progression, double itemPower) => abilityPower * Scale(progression, itemPower);

    /// <summary>A spell's damage or change before resistance: change × AP / 100 (basedamage 100). Mob spells use the mob's abilitypower.</summary>
    public static double SpellChange(double change, double abilityPower) => change * abilityPower / 100;

    /// <summary>bej.d: a heal, change × (AP / 100)^0.6634.</summary>
    public static double Heal(double change, double abilityPower) => change * Math.Pow(abilityPower / 100, HealExponent);

    /// <summary>
    /// The healer's share on its heals (stageJ pvp-spells SPEC §2.5, U6 [inference: healbonus is the healer's, healmodifier the receiver's]):
    /// 1 + Σ healbonus of its buffs and worn passives + its worn items' healbonus (T2_ARMOR_PLATE_SET1 0.1, the raw items.xml value); not below 0.
    /// </summary>
    public static double HealBonusFactor(Player healer, long now) =>
        Math.Max(0, 1 + (healer.Effects.Idle ? 0 : healer.Effects.Sum("healbonus", now)) + Worn(healer, "healbonus"));

    /// <summary>
    /// The sum of an items.xml bonus attribute over <paramref name="p"/>'s worn items, main hand to cape (healbonus, bonusccdurationvsplayers /
    /// vsmobs, the physical|magic spell|attack damagebonus; stageM pvp-damage NOTES §2): each item's value × its ItemPowerProgression
    /// section's same-named progression^(IP/100) (<see cref="GameData.BonusProgression"/>: offhand 1.16, every other section 1, so a
    /// chest's value counts raw); the raw values without game data; 0 without any.
    /// </summary>
    public static double Worn(Player p, string attribute)
    {
        var sum = 0.0;
        var e = p.Inventory.Equipment;
        var data = p.World.GameData;
        for (var s = 0; s < Math.Min(Passives.Slots, e.Size); s++)
            if (e[s] is { } item && item.Template.Attrs.GetValueOrDefault(attribute) is { Length: > 0 } v
                && double.TryParse(v, NumberStyles.Float, CultureInfo.InvariantCulture, out var d) && d != 0)
                sum += data?.Item(item.Template.Index) is { } stats && data.BonusProgression(stats, attribute) is var prog && prog != 1
                    ? d * Scale(prog, ItemPower(p, item)) : d;
        return sum;
    }

    /// <summary>What goes on the wire: rounded to an integer (Math.Round) and sent as a float (SPEC §5b.2).</summary>
    public static float Rounded(double value) => (float)Math.Round(value);

    /// <summary>
    /// A player's energy cost: round(energyusage × energyprogression^(IP/100)) with the IP of the item that grants the spell
    /// (FIRESTAFFBOLT2 3 → 8 at IP 1050, AXESMASH 8 → 18 at IP 945; SPEC §5b.3, the rounding mode is open, §9 Q8).
    /// </summary>
    public static int PlayerEnergyCost(double energyUsage, double energyProgression, double itemPower) =>
        (int)Math.Round(energyUsage * Scale(energyProgression, itemPower));

    /// <summary>A mob's energy cost: round(energyusage × abilitypower / 100), half to even (all 11 captured pairs; 5f).</summary>
    public static int MobEnergyCost(double energyUsage, double abilityPower) =>
        (int)Math.Round(energyUsage * abilityPower / 100, MidpointRounding.ToEven);

    /// <summary>
    /// UpdateFame keys 2 and 3 (a7m, ×10000) of a kill (SPEC §5c.2): fame × famefactor / divisor and fame × (famefactor − 1) /
    /// divisor, divisor = mindivisor when the mob has one, else 1. Computed in floats and truncated, which gives every
    /// captured value (94 × 2.15 = 202.1 → 2021000 / 1081000; 66 × 2.15 / 3.2 → 443437 / 237187).
    /// In a party (stageD party SPEC §5 [H], no party capture): each of the <paramref name="members"/> who share the kill gets
    /// fame × famefactor × g / d, d = max(members, the divisor), g = the gamedata GroupBonus of the group size
    /// (<paramref name="groupBonus"/>); one member with g = 1 gives the solo numbers above unchanged.
    /// </summary>
    public static (long Gain, long Bonus) Fame(double fame, double fameFactor, double minDivisor, int members = 1, double groupBonus = 1)
    {
        float f = (float)fame, k = (float)fameFactor, g = (float)groupBonus, divisor = minDivisor > 0 ? (float)minDivisor : 1f;
        var d = members > 1 ? Math.Max(members, divisor) : divisor;
        float gain = f * k * g / d * 10000f, bonus = f * (k - 1f) * g / d * 10000f;
        return ((long)gain, (long)bonus);
    }

    // ---- a player's items ----

    /// <summary>
    /// The auto-attack of a player: its main-hand weapon's items.xml attackdamage, attackspeed, attackrange, attacktype and
    /// attackdamagetimefactor, the weapon's item power and combat progression; bare hands: characters.xml barehand values
    /// (melee, no scaling).
    /// </summary>
    public readonly record struct Weapon(Item? Item, double Damage, double Speed, double Range, string Type, double DamageTimeFactor,
        double ItemPower, CombatProgression? Progression)
    {
        /// <summary>Attack 13 key 3: 1 melee, 2 ranged, 4 magic.</summary>
        public byte AttackType => Type switch { "ranged" => 2, "magic" => 4, _ => 1 };

        /// <summary>HealthUpdate key 4: magic attacks are magic damage, melee and ranged physical.</summary>
        public EffectType Effect => Type == "magic" ? EffectType.Magic : EffectType.Physical;

        /// <summary>HealthUpdate key 5: 0 melee, 1 ranged or magic.</summary>
        public HitOrigin Origin => Type == "melee" ? HitOrigin.MeleeAutoAttack : HitOrigin.RangedAutoAttack;
    }

    public static Weapon WeaponOf(Player p)
    {
        var data = p.World.GameData;
        if (p.Inventory.Equipment[0] is not { } item)
        {
            var bare = data?.Barehand ?? (2, 20, 1);
            return new Weapon(null, bare.Damage, bare.Speed, bare.Range, "melee", 1, 0, null);
        }
        var t = item.Template;
        var type = Attr(t, "attacktype") is { Length: > 0 } a ? a : "melee";
        return new Weapon(item, Number(t, "attackdamage", 0), Number(t, "attackspeed", 1), Number(t, "attackrange", 2), type,
            Number(t, "attackdamagetimefactor", 1), ItemPower(p, item), ProgressionOf(p.World, item));
    }

    /// <summary>The item power of <paramref name="item"/> as worn by <paramref name="p"/> (<see cref="StatCalculator.ItemPower"/>: quality, destiny board, wear); items.xml itempower without game data.</summary>
    /// <remarks>An awakened weapon attuned to <paramref name="p"/> adds its TRAIT_ITEM_POWER (<see cref="Passives.TraitItemPower"/>).</remarks>
    /// <remarks>StageP: under an arena's item power cap (<see cref="Player.ItemPowerCap"/>) the capped figure.</remarks>
    public static double ItemPower(Player p, Item item)
    {
        var ip = (p.World.StatCalculator is { } calc ? calc.ItemPower(item.ForStats(), p.Progression?.Levels ?? new SortedDictionary<int, int>())
                : double.TryParse(Attr(item.Template, "itempower"), NumberStyles.Float, CultureInfo.InvariantCulture, out var raw) ? raw : 0)
            + (item.Soul is null ? 0 : Passives.TraitItemPower(p, item));
        return p.ItemPowerCap is { } cap ? cap.Apply(ip) : ip;
    }

    /// <summary>The ItemPowerProgression section's combat progressions of <paramref name="item"/>; null = no scaling.</summary>
    public static CombatProgression? ProgressionOf(World world, Item item) =>
        world.GameData is { } data && data.Item(item.Template.Index) is { } d ? data.CombatProgressionOf(d) : null;

    /// <summary>
    /// The ability power a spell of <paramref name="item"/> runs with (bej.b); 100 (no scaling) without an item. The captured fire-staff
    /// player's "1.408 × this" (SPEC §9 Q18a) is not the ability power but its worn robe's spell damage bonus (SpellEffects.DealtFactor,
    /// stageM pvp-damage NOTES §3).
    /// </summary>
    public static double AbilityPowerOf(Player p, Item? item)
    {
        if (item is null) return 100;
        var ap = Number(item.Template, "abilitypower", 100);
        return ProgressionOf(p.World, item) is { } prog ? AbilityPower(ap, prog.AbilityPower, ConsumableIp(item) ?? ItemPower(p, item)) : ap;
    }

    /// <summary>
    /// The item power a consumable's ability power runs with: 100, i.e. abilitypower × the potion/food progression once
    /// (stageD consumables SPEC §5: soup AP 186 → 203.0748 and 196.49039, fish 100 → 109.18; the IP of T1 soup, T7 roast and T8
    /// fish does not enter); null for every other item.
    /// </summary>
    private static double? ConsumableIp(Item item) => item.Template.Kind is "consumableitem" or "consumablefrominventoryitem" ? 100 : null;

    /// <summary>
    /// The crowd control power a spell of <paramref name="item"/> runs with (ActiveSpellEffectsUpdate key 3, stageD effects SPEC
    /// §1): abilitypower × ccpowerprogression^(IP/100) (T5_MOUNT_OX IP 800: 100 × 1.0564^8 = 155.10); 100 without an item.
    /// </summary>
    public static double CcAbilityPowerOf(Player p, Item? item)
    {
        if (item is null) return 100;
        var ap = Number(item.Template, "abilitypower", 100);
        return ProgressionOf(p.World, item) is { } prog ? AbilityPower(ap, prog.CcPower, ConsumableIp(item) ?? ItemPower(p, item)) : ap;
    }

    /// <summary>
    /// The energy a cast of <paramref name="spell"/> granted by <paramref name="item"/> costs <paramref name="p"/>: a share of
    /// its max energy for energyusageisrelative (LIFESAVIOR 0.15 → 44 of 293), else <see cref="PlayerEnergyCost"/>.
    /// </summary>
    public static int EnergyCost(Player p, SpellTemplate spell, Item? item)
    {
        if (spell.EnergyUsage <= 0) return 0;
        if (spell.EnergyUsageIsRelative) return (int)Math.Round(spell.EnergyUsage * p.Energy.Max);
        return item is not null && ProgressionOf(p.World, item) is { } prog
            ? PlayerEnergyCost(spell.EnergyUsage, prog.Energy, ItemPower(p, item))
            : (int)Math.Round(spell.EnergyUsage);
    }

    /// <summary>
    /// The item that grants the spell in CharacterSpellSlot <paramref name="slot"/> (<see cref="Network.WorldSession.SpellSlots"/>):
    /// 0-2 the main hand (2 the off hand when the main hand has fewer than 3 active slots), 3 the armor, 4 the head, 5 the
    /// shoes, 12 the potion, 13 the food.
    /// </summary>
    public static Item? SpellSource(Inventory inventory, int slot)
    {
        var e = inventory.Equipment;
        return slot switch
        {
            0 or 1 => e[0],
            2 => e[0] is { Active.Length: >= 3 } main ? main : e[1],
            3 => e[3],
            4 => e[2],
            5 => e[4],
            12 => e[8],
            13 => e[9],
            _ => null,
        };
    }

    /// <summary>
    /// The physicalarmor or magicresistance share against <paramref name="type"/> damage on <paramref name="mob"/>: its mobs.xml
    /// value + the resistance points of its buffs and debuffs, clamped by the attribute's &lt;ActiveBuffScaling&gt; mob row
    /// (<see cref="Buffed"/>; the "a71.b curve" of stage 5 Q10 is this clamp, stageJ armour SPEC §1).
    /// </summary>
    public static double ResistanceOf(Mob mob, EffectType type) =>
        Resistance(Math.Max(0, Buffed(type == EffectType.Magic ? mob.Template.MagicResistance : mob.Template.Armor, BuffResistance(mob, type, mob.GameNow),
            Limit(mob.World?.GameData, ResistanceKey(type), mob: true))));

    /// <summary>
    /// a4f.e8 / a4f.fa / a4f.fc before their floor (stageJ armour SPEC §2.3): <paramref name="value"/> + S × clamp(points / S, min,
    /// max), S = max(value, absolutemin) (0 when S is 0). Armour, magic and crowd control resistance: S at least 100 and the
    /// debuffs at most −0.6 × S (a soldier's 121 armour with −200 points: 48.4; a naked player's −50: −50, floored to 0 by the caller).
    /// </summary>
    public static double Buffed(double value, double points, BuffLimit limit)
    {
        var s = Math.Max(value, limit.AbsoluteMin);
        return s == 0 ? 0 : value + s * Math.Min(limit.Max, Math.Max(limit.Min, points / s));
    }

    /// <summary>The &lt;ActiveBuffScaling&gt; row of <paramref name="attribute"/> for a mob or a player; the 345319 rows without game data.</summary>
    public static BuffLimit Limit(GameData? data, string attribute, bool mob) =>
        (data?.BuffLimitOf(attribute) ?? GameData.DefaultBuffLimits.GetValueOrDefault(attribute) ?? BuffLimit.Unlisted).For(mob);

    /// <summary>The attribute that resists <paramref name="type"/> damage: magicresistance or physicalarmor.</summary>
    private static string ResistanceKey(EffectType type) => type == EffectType.Magic ? "magicresistance" : "physicalarmor";

    /// <summary>The physicalarmor / magicresistance points of <paramref name="unit"/>'s buffs (SpellEffects.Scale: value × 200 × (AP/100)^0.3366).</summary>
    private static double BuffResistance(IUnit unit, EffectType type, long now) =>
        unit.Effects.Idle ? 0 : unit.Effects.Sum(ResistanceKey(type), now);

    /// <summary>
    /// The physicalarmor or magicresistance share against <paramref name="type"/> damage on <paramref name="p"/> (a mob's hit, a
    /// player's in PvP; stageJ armour SPEC §5.4, cpo.ayk / cpo.aym): 100 / (100 + max(0, <see cref="Buffed"/>)) of its worn items'
    /// armour or magic resistance (<see cref="Player.Defense"/>, with their item power) and its buff points. T4 cloth set 2 at IP
    /// 865 (107.9 armour): a 431 hit lands for 207, 200 with the head and shoe passives' defence (captured 200). Without game data,
    /// or naked, 1 (no reduction).
    /// </summary>
    public static double ResistanceOf(Player p, EffectType type)
    {
        if (p.IsMounted) return Mounts.Resistance(p, type); // the mount takes the hit (stageD mounts SPEC §4.5)
        var d = p.Defense;
        return Resistance(Math.Max(0, Buffed(type == EffectType.Magic ? d.MagicResistance : d.Armor, BuffResistance(p, type, p.World.GameNow),
            Limit(p.World.GameData, ResistanceKey(type), mob: false))));
    }

    /// <summary>Distance between the edges of a player and a mob (centre distance less both collision radii, not below 0).</summary>
    public static float EdgeDistance(Player p, Vector2 at, Mob mob) =>
        Math.Max(0, Vector2.Distance(at, mob.Position) - mob.Template.CollisionRadius - p.World.CollisionRadius);

    // ---- events ----

    /// <summary>
    /// HealthUpdate (6, ap9; SPEC §5b.5): 0 target, 1 the hit's GameTimeStamp, 2 the change (negative = damage; left out at 0),
    /// 3 the value after it (left out at 0: death), 4 EffectType, 5 origin (always, also 0), 6 the causer, 7 the spell (-1 =
    /// auto-attack). Never key 8.
    /// </summary>
    public static HealthUpdateEvent HealthUpdate(long target, long ticks, float change, float value, EffectType type, HitOrigin origin, long causer, short spell) =>
        new() { ddn = target, ch5 = ticks, ch6 = change, ch7 = value, ch8 = (byte)type, cia = causer, cib = spell, Extra = new() { [5] = (byte)origin } };

    /// <summary>CastHit (21, alf): 0 target, 1 caster, 2 spell, 3 hit type 1, 4 the cast counter (always; 0 for a spell the server triggers).</summary>
    public static CastHitEvent CastHit(long target, long caster, short spell, byte counter) =>
        new() { ddn = target, bu3 = caster, bu4 = spell, bu5 = 1, Extra = new() { [4] = counter } };

    /// <summary>
    /// The death clear of SPEC §5c.1 (the same in all 18 captured kills): ActiveSpellEffectsUpdate (11) {0 mob, 3 [], 4 [],
    /// 5 empty byte[], 7 {0x00}, 9 {0x00}}, keys 1, 2, 6, 8 and 10 left out.
    /// </summary>
    public static ActiveSpellEffectsUpdateEvent EffectsCleared(long id) =>
        new() { ddn = id, bru = [], brv = [], brw = [], bry = [0], br0 = [0] };

    /// <summary>
    /// ActiveSpellEffectsUpdate (11) with one buff, <paramref name="spell"/>, on object <paramref name="id"/> (the mob reset's
    /// layout, 21 captured, SPEC §5d.3): {0 id, 1 [spell], 2 [100], 3 [100], 4 [<paramref name="stamp"/>], 5 {1} (the stack
    /// count, most likely), 7 3 bits per timed sub-effect, value 1 each, little endian, 9 {1} (one bit per buff), 10 {0}};
    /// keys 6 and 8 left out. Keys 2/3/5/10 are copied from the reset buff without knowing what they mean (SPEC §9 Q15).
    /// </summary>
    public static ActiveSpellEffectsUpdateEvent BuffEffects(long id, short spell, int subEffects, long stamp)
    {
        var bits = new byte[(3 * subEffects + 7) / 8];
        for (var i = 0; i < subEffects; i++) bits[3 * i / 8] |= (byte)(1 << (3 * i % 8));
        return new ActiveSpellEffectsUpdateEvent
        {
            ddn = id, brs = [spell], brt = [100f], bru = [100f], brv = [stamp], brw = [1], bry = bits, br0 = [1],
            Extra = new() { [10] = new byte[] { 0 } },
        };
    }

    // ---- damage and healing ----

    /// <summary>
    /// <paramref name="amount"/> damage (already resisted; × the target's bonusdefensevsplayers factor and rounded here) of
    /// <paramref name="attacker"/> on <paramref name="target"/> at GameTimeStamp <paramref name="ticks"/>, none while it is
    /// invincible (stageD SPEC §3.1, §3.4): the health is regenerated up to then and lowered, the mob takes the attacker
    /// as its target (NewMob key 12 for later observers) and the attacker is in combat with it; every client that knows the
    /// mob gets <paramref name="hit"/> (a CastHit, null for auto-attacks and over-time ticks) and the HealthUpdate. A lethal
    /// hit reports the full damage and is sent last in the death batch (<see cref="Kill"/>). Nothing on a dead or removed mob,
    /// nor on one running home with its return buff's spell immunity (<see cref="Mob.Immune"/>, SPEC §5d.3). With
    /// <see cref="World.MobAi"/> the damage is threat for the attacker (<see cref="AI.MobAI.OnDamaged"/>). Nothing either from
    /// an attacker knocked down meanwhile (a cast hit or projectile in flight, a pulse: SPEC §5e.1, no combat while down).
    /// StageD part B: nothing on a nothittablebyothers mob, damage shields first, then the attacker's DoDamage / DoDirectDamage triggers.
    /// Never from a bot (bots never fight mobs; its spells' scans skip them already, stageJ pvp-spells SPEC §2.9) but a hired companion (stageN).
    /// </summary>
    public static void Damage(Player attacker, Mob target, double amount, EffectType type, HitOrigin origin, short spell, long ticks, AlbionEvent? hit = null)
    {
        // StageN companions: a hired companion fights mobs (Companions:DamageFactor); the other bots never do.
        if (!target.Attackable || target.Map is not { } map || target.Immune || attacker.Combat.Down is not null || attacker.IsBot && !attacker.IsCompanion) return;
        if (SpellEffects.Invincible(target, ticks) || SpellEffects.NotHittable(target, ticks)) return; // stageD SPEC §3.4: no HealthUpdate
        var world = attacker.World;
        // A companion whose owner is not in game deals nothing: every kill is credited to a human (fame, loot, journal).
        var credit = attacker.IsCompanion ? world.Companions.CreditOf(attacker) : attacker;
        if (credit is null) return;
        if (attacker.IsCompanion) amount *= world.Config.Companions.DamageFactor;
        var damage = Math.Max(0, Rounded(amount * SpellEffects.TakenFactor(target, fromPlayer: true, ticks)));
        if (target.TargetId == 0) target.TargetId = attacker.Id;
        if (attacker.Map == map) EnterCombat(attacker, target);
        if (origin is not (HitOrigin.Reflected or HitOrigin.OverTime)) SpellEffects.Break(world, attacker, "attacking", ticks); // an invisibility ends with the attack
        var raw = damage;
        damage = Rounded(SpellEffects.Absorb(world, target, damage, ticks));
        if (damage <= 0 && raw > 0)
        {
            if (hit is not null) map.SendToObservers(target, hit); // all on a damage shield: no HealthUpdate (stageD SPEC §9 Q7)
        }
        else
        {
            var stamp = Math.Max(ticks, target.Health.Stamp);
            var had = target.Health.At(ticks);
            var left = had - damage;
            if (target.Event is { } run && !attacker.IsBot) WorldEvents.Damaged(run, attacker, Math.Min(damage, had)); // a world event's reward share (never a bot's, stage I)
            var update = HealthUpdate(target.Id, ticks, -damage, Math.Max(0, left), type, origin, attacker.Id, spell);
            if (damage > 0) SpellEffects.Damaged(target, type, ticks);
            if (left <= 0)
            {
                target.Health.Set(0, stamp);
                // StageN companions: a companion's kill is its owner's (fame, silver, the loot bag's rights), the owner on the map.
                Kill(world, credit, target, ticks, hit is null ? [update] : [hit, update]);
            }
            else
            {
                target.Health.Set(left, stamp);
                if (hit is not null) map.SendToObservers(target, hit);
                map.SendToObservers(target, update);
                if (damage > 0) SpellEffects.Break(world, target, "damage", ticks); // a stun or root with damagebreakseffect ends with the hit
                if (world.MobAi && attacker.Map == map)
                {
                    target.AI.OnDamaged(attacker, damage);
                    if (attacker.IsCompanion) world.Companions.Threat(attacker, target, damage); // a tank's Companions:TankThreatFactor
                }
            }
        }
        // The attacker's triggers (applyeffectoneventactive, stageD SPEC §3.5), after the hit, the killing one too.
        SpellEffects.Trigger(attacker, "DoDamage", target, ticks);
        if (origin != HitOrigin.OverTime) SpellEffects.Trigger(attacker, "DoDirectDamage", target, ticks);
        Passives.OnHit(attacker, raw, origin, ticks); // healonhit(passive), stageD effects batch 2
    }

    /// <summary>
    /// <paramref name="amount"/> damage (already resisted, rounded here) of mob <paramref name="attacker"/> on player
    /// <paramref name="target"/> at GameTimeStamp <paramref name="ticks"/> (stage 5d, SPEC §5d.1: a mob's swing at its
    /// impact, stamped with the Attack's key 4): the player is in combat with the mob, hit (<see cref="EnterCombat"/>:
    /// InCombatStateUpdate {1 true, 2 true}), its health lowered (no regeneration in combat), and the HealthUpdate {1 ticks,
    /// 2 -damage, 3 left, 4 type, 5 origin, 6 the mob, 7 spell} goes to it and the players who see it. At 0 the HealthUpdate
    /// has no key 3 and the player is knocked down (stage 5e, <see cref="Knockdown.Start"/>: KnockedDown 165 with the mob and
    /// its "@" + namelocatag, every mob drops it). Nothing on a player knocked down or invulnerable after its get-up or by an
    /// invincibility or nothittablebyothers, nor off the mob's map; × its bonusdefensevsmobs factor of buffs and worn off hand (stageD SPEC §3.1, stageJ armour SPEC §5.4). StageD part
    /// B: the damage goes to its damage shields first (none on the health: no HealthUpdate, SPEC §3.4, §9 Q7), a channel with
    /// damagecancelscasting ends, and its ReceiveDamage / ReceiveDirectDamage triggers run after the hit. StageD mounts (SPEC §4.5): on
    /// a mounted player the hit goes to the mount bar (<see cref="Mounts.Damage"/>), never to the health. <paramref name="reflectable"/> false: a
    /// spell node's reflectdamage="never", which the player's reflectdamageactive does not answer (stageJ pvp-spells phase 0).
    /// </summary>
    public static void DamagePlayer(Mob attacker, Player target, double amount, EffectType type, HitOrigin origin, short spell, long ticks, bool reflectable = true)
    {
        if (target.IsBot && !target.IsCompanion) return; // stage I: mobs never fight bots (they scan the humans and companions only; a guard)
        if (target.Map is not { } map || attacker.Map != map || target.Combat.Untouchable || SpellEffects.Invincible(target, ticks)
            || SpellEffects.NotHittable(target, ticks)) return;
        var before = target.Health.At(ticks);
        if (before <= 0) return;
        var world = target.World;
        var damage = Math.Max(0, Rounded(amount * SpellEffects.TakenFactor(target, fromPlayer: false, ticks, target.Defense.DefenseVsMobs)));
        EnterCombat(target, attacker, hit: true);
        // notinterruptible (HYPER_FOCUS) keeps a damagecancelscasting channel too, as it does for stuns (the client side not captured).
        if (target.Combat.Channel is { Info.DamageCancels: true } && !SpellEffects.NotInterruptible(target, ticks))
            SpellCasts.EndChannel(target, SpellCasts.ChannelEnd.Interrupted);
        Flagging.Interrupt(target, "damage"); // stageE: a flag cast ends (damagecancelscasting)
        if (target.IsMounted)
        {
            Mounts.Damage(target, attacker.Id, damage, type, origin, spell, ticks);
            return;
        }
        var raw = damage;
        damage = Rounded(SpellEffects.Absorb(world, target, damage, ticks));
        var health = target.Health.At(ticks);
        var left = (float)SpellEffects.Clamp(world, target, health, Math.Max(0, health - damage), ticks); // clamphitpoints: not below its min
        if (left > Math.Max(0, health - damage)) damage = health - left;
        if (damage > 0) Mounts.Cancel(target, "damage"); // absorbed or immune hits do not interrupt mounting
        if (damage > 0 || raw <= 0) // a hit of 0 still goes out; one all on a shield does not
        {
            target.Health.Set(left, Math.Max(ticks, target.Health.Stamp));
            if (damage > 0) SpellEffects.Damaged(target, type, ticks);
            var e = HealthUpdate(target.Id, ticks, -damage, left, type, origin, attacker.Id, spell);
            target.Session.Send(e);
            target.SendToObservers(e);
            DeathRecaps.Hit(target, attacker, spell, origin, -damage, ticks); // stageP: the death recap's mob rows
        }
        if (reflectable) SpellEffects.Reflect(target, attacker, raw, ticks); // reflectdamageactive (batch 2); not a reflectdamage="never" hit
        if (raw > 0) SpellEffects.EnergyOnDamage(target, ticks); // gainenergyondamage (batch 3), a hit on a shield too
        SpellEffects.Break(world, target, "damage", ticks);
        if (left > 0)
        {
            // The player's triggers (applyeffectoneventactive: ENRAGE, stageD SPEC §3.5), after the hit.
            SpellEffects.Trigger(target, "ReceiveDamage", attacker, ticks);
            if (origin != HitOrigin.OverTime) SpellEffects.Trigger(target, "ReceiveDirectDamage", attacker, ticks);
            return;
        }
        var tag = attacker.Template.NameLocaTag ?? attacker.Template.Name;
        Knockdown.Start(target, attacker.Id, tag.StartsWith('@') ? tag : "@" + tag, ticks);
        if (world.Config.PveKnockdownWear is > 0 and var share && target.Combat.Down is { Kind: KnockdownKind.Pve })
            Pvp.WearAll(target, (world.GameData?.KnockedOutLoss ?? DurabilityLossRule.KnockedOut) with { MinFactor = share, MaxFactor = share }, ticks); // Pve:KnockdownWear
    }

    /// <summary>
    /// <paramref name="mob"/> stops fighting (its reset, SPEC §5d.3): it leaves the enemies of every player on its map, and
    /// the players whose last enemy it was leave combat at once (5e.1 step 6: "combat is over at once"), but for one that fights a
    /// player (<see cref="FightsPlayer"/>, stageE).
    /// </summary>
    public static void EnemyGone(Map map, Mob mob)
    {
        foreach (var p in map.Fighters.ToList())
            if (p.Combat.Enemies.Remove(mob) && p.Combat.Enemies.Count == 0 && p.Combat.InCombat && !FightsPlayer(p)) EndCombat(p);
    }

    /// <summary>
    /// Whether <paramref name="p"/> swung at or was hit by a player within the combat timeout (<see cref="PlayerCombat.LastPvp"/>):
    /// its combat then outlives its last mob enemy and ends by the timeout (<see cref="Update"/>).
    /// </summary>
    public static bool FightsPlayer(Player p) =>
        p.Combat.LastPvp is { } at && p.World.Clock.Now - at < TimeSpan.FromSeconds(p.World.GameData?.InCombatActiveSeconds ?? 20);

    /// <summary>
    /// A heal of <paramref name="amount"/> (scaled already) by <paramref name="caster"/> on <paramref name="target"/> at
    /// <paramref name="ticks"/>: up to the maximum; HealthUpdate (EffectType 2) to the target and the players who see it.
    /// Nothing on a knocked-down player (it gets up by itself, SPEC §5e.2). An over-time tick is not rounded (stageD consumables
    /// SPEC §8.1, as the captured over-time energy 18.126; a heal potion's 0.013 × max), a direct heal is.
    /// </summary>
    public static void HealPlayer(Player caster, Player target, double amount, HitOrigin origin, short spell, long ticks)
    {
        if (target.Map is null || target.Combat.Down is not null || SpellEffects.CannotBeHealed(target, ticks)) return;
        var before = target.Health.At(ticks);
        var after = Math.Min(target.Health.Max, before + Math.Max(0, origin == HitOrigin.OverTime ? (float)amount : Rounded(amount)));
        target.Health.Set(after, Math.Max(ticks, target.Health.Stamp));
        var e = HealthUpdate(target.Id, ticks, after - before, after, EffectType.Magic, origin, caster.Id, spell);
        target.Session.Send(e);
        target.SendToObservers(e);
        DeathRecaps.Hit(target, caster, spell, origin, after - before, ticks); // stageP: heals in the death recap
        if (target.World.MobAi && target.Map is { } map) AI.MobAI.Healed(map, caster, target, after - before);
    }

    // ---- death (SPEC §5c.1) ----

    /// <summary>
    /// The fame factor of <paramref name="p"/>'s fame buffs for a kill of <paramref name="mob"/> (mobfamebycategoryovertime, stageN
    /// spells-bosses): 1 + Σ value × stacks of the buffs in force whose category is the mob's mobs.xml category (roaming, static,
    /// rd_veteran, avalon, …) and whose bindtoclusterarchetype, when set, is the map's archetype.
    /// </summary>
    public static double FameBuff(World world, Player p, Mob mob, Map map)
    {
        if (p.Effects.Idle) return 1;
        var category = mob.Template.FameCategory;
        string? archetype = null;
        var sum = 0.0;
        foreach (var (spell, sub) in p.Effects.On(world.GameNow))
        {
            if (sub.Tag != "mobfamebycategoryovertime" || !category.Equals((string?)sub.Node.Attribute("category"), StringComparison.OrdinalIgnoreCase)) continue;
            if ((string?)sub.Node.Attribute("bindtoclusterarchetype") is { } bound)
            {
                try { archetype ??= map.Template?.Info.Type is { } type ? world.Clusters?.Archetypes.Of(type).Archetype ?? "" : ""; }
                catch (Exception x) when (x is InvalidDataException or IOException) { archetype = ""; }
                if (!bound.Equals(archetype, StringComparison.OrdinalIgnoreCase)) continue;
            }
            sum += Data.SpellTemplate.Float(sub.Node, "value") * spell.Stacks;
        }
        // The elites' death spell MOB_ELITE_FAME_BUFF (buffovertime mobfamemodifier 0.005 a stack, up to 50, in the black zones): any mob.
        return 1 + sum + p.Effects.Sum("mobfamemodifier", world.GameNow);
    }

    /// <summary>
    /// <paramref name="mob"/> dies by <paramref name="killer"/>'s hit at <paramref name="ticks"/>. The batch, in the SPEC's
    /// corrected order (18 captured kills; official sends the rewards 8-217 ms earlier, only the order matters): 1. the
    /// killer's rewards: UpdateFame (82) and, when the mob has an energyreward, EnergyUpdate (8) {0 killer, 1, 2 +reward, 3,
    /// 4 byte 8, 5 the mob's object id; no key 6} and CastSpell (19) of its energyrewardspell to the mob's observers {0 mob, 1
    /// killer, 2 the killer's position, 3 spell, 4 = 5 the kill, 6 the mob's cast counter, 7 0}, with the silver pile between
    /// UpdateFame and EnergyUpdate (<see cref="DropSilver"/>, SPEC §5c.1) and in the same place the corpse of a skinnable mob
    /// (<see cref="Corpses.Drop"/>: NewHarvestableObject 40, captured between UpdateFame and EnergyUpdate); 2. the combat end of every
    /// player whose last enemy it was, the killer's in this batch; 3. NewLoot (98) when items drop (<see cref="DropLoot"/>);
    /// 4. the death clear (<see cref="EffectsCleared"/>) and the killing hit (<paramref name="killingHit"/>: its CastHit and
    /// the HealthUpdate without key 3). No Died (164): that is the player kill feed. The corpse stays postDeathLifetime
    /// (min(5, attr), 5 s), then a Leave to every observer and the spawn point's respawn (<see cref="SpawnManager.Despawn"/>).
    /// The fame goes to the killer's destiny board first (<see cref="Destiny.Award"/>: FinishedAchievement and
    /// AchievementProgressInfo of the nodes it feeds, before the UpdateFame).
    /// Before each fame the reputation gain (ReputationUpdate reason 1, stageE <see cref="Reputation.Gain"/>). Not sent (TODO):
    /// UpdateLearningPoints (no learning points). A pending mob cast is cancelled by its AI's <see cref="AI.MobAI.OnDeath"/> (stageD mobspells §4.3).
    /// </summary>
    internal static void Kill(World world, Player killer, Mob mob, long ticks, IReadOnlyList<AlbionEvent> killingHit)
    {
        var map = mob.Map!;
        var bossLooters = world.Bosses.Looters(mob, killer); // stageH: a world boss's contributors, from its threat before the death clears it
        var mistsLooters = world.Mists.Looters(mob, killer); // stageL mists: a mist boss's reward chest takers, the same way
        mob.Dead = true;
        var threat = mob.Threat.Keys.ToList(); // stageN spells-bosses: its death spells and kill reward buffs, after the AI's death
        mob.AI.OnDeath(); // 5d: no threat, its swings and return dropped, it stops where it is
        mob.TargetId = 0;
        mob.Health.RegenPerSecond = 0;
        mob.Effects.Clear(); // its debuffs and the damage over time end (the death clear below tells the clients)
        Summons.SummonerGone(world, map, mob); // its adds go (ttlaftercasterdeath, stageD effects batch 3)
        var danger = DangerOf(world, map);
        // 1. The rewards of the killer (5c: the last hitter only); its party members near the mob share the fame (stageD party SPEC §5).
        var t = mob.Template;
        var group = world.Parties.FameGroup(killer, mob);
        var groupBonus = world.GameData?.GroupBonus(group.Count) ?? 1;
        // Rates:Fame × Rates:CombatFame on both keys 2 and 3 (the bonus is a part of the gain).
        var fameRate = world.Rates.Fame * world.Rates.CombatFame;
        // StageG dungeons: the floor's level adds its mobfamefactor to the danger factor (SPEC §7); stage I: a T9/T10 invasion mob's FameFactor.
        var (gain, bonus) = t.Fame > 0 ? Fame(t.Fame, (danger?.FameFactor ?? 1) * (map.Difficulty?.MobFame ?? 1) * mob.FameFactor, t.MinDivisor, group.Count, groupBonus) : (0, 0);
        (gain, bonus) = (Rates.ScaleFame(gain, fameRate), Rates.ScaleFame(bonus, fameRate));
        if (gain > 0) // the scaled gain: Rates 0 sends no +0 popup
            foreach (var p in group.Where(p => !p.IsBot)) // bot-party: a recruited bot's share of the split goes nowhere
            {
                // The destiny board's 151/152 of the equipped gear's nodes, then UpdateFame {1 total, 2 fame × famefactor /
                // divisor, 3 the danger bonus part, 5 PvE, 15} (PROGRESSION.md §6.6: all before the lethal HealthUpdate), to each alone.
                // StageE (pvp SPEC §3.4 [infer]): first the reputation gain, pvegainfactor × fame (stage 5 SPEC §5c.1: ReputationUpdate
                // reason 1 leads the rewards).
                Reputation.Gain(p, gain / 10000.0, Reputation.MobKill, (short)t.Index);
                // StageN spells-bosses: the fame buffs of the mob's category (the dungeon and roaming fame events, the world boss's death spell).
                var buff = FameBuff(world, p, mob, map);
                Destiny.Award(p, buff == 1 ? gain : Rates.ScaleFame(gain, buff), Destiny.Kill(p, t.Tier), buff == 1 ? bonus : Rates.ScaleFame(bonus, buff));
            }
        DropSilver(world, killer, mob, ticks, danger);
        Corpses.Drop(world, killer, mob, ticks);
        if (t.EnergyReward > 0 && killer.Map == map)
        {
            var before = killer.Energy.At(ticks);
            var after = Math.Min(killer.Energy.Max, before + t.EnergyReward);
            killer.Energy.Set(after, Math.Max(ticks, killer.Energy.Stamp));
            killer.Session.Send(new EnergyUpdateEvent
            {
                ddn = killer.Id, b5b = ticks, b5c = t.EnergyReward, b5d = after, b5e = EnergyReward, b5f = mob.Id, // keys 0-5, no key 6
            });
            if (world.Spells?.IdOf(t.EnergyRewardSpell) is { } rewardSpell and >= 0)
                map.SendToObservers(mob, new CastSpellEvent
                {
                    ddn = mob.Id, bvw = killer.Id, bvx = killer.PositionAt(DateTime.UtcNow), bvy = rewardSpell, bvz = ticks, bv0 = ticks,
                    Extra = new() { [6] = mob.CastCounter, [7] = (short)0 },
                });
        }
        // 2. The combat end of the players whose last enemy it was: the killer's in this batch.
        var ended = new List<Player>();
        foreach (var p in map.Fighters)
            if (p.Combat.Enemies.Remove(mob) && p.Combat.Enemies.Count == 0 && p.Combat.InCombat && !FightsPlayer(p)) ended.Add(p);
        if (ended.Remove(killer)) EndCombat(killer);
        // 3. The loot bag, 4. the death clear and the killing hit.
        // StageH: a world boss's bag for each contributor (its own roll, Loot Personal) or one bag for all of them (Shared).
        if (bossLooters is null) DropLoot(world, killer, mob, ticks, danger);
        else if (world.Bosses.PersonalLoot)
            foreach (var p in bossLooters) DropLoot(world, p, mob, ticks, danger, [p.Character.Id], party: false);
        else DropLoot(world, killer, mob, ticks, danger, [.. bossLooters.Select(p => p.Character.Id)]);
        map.SendToObservers(mob, EffectsCleared(mob.Id));
        foreach (var e in killingHit) map.SendToObservers(mob, e);
        foreach (var p in ended) EndCombat(p);
        MobEvents.Died(world, map, mob, killer, threat); // stageN spells-bosses: <DeathSpells>, <KillRewardBuff>
        // StageH bosses SPEC §4.2-4.4, after the kill batch: the open-world chests it guarded unlock, a boss point's reward chest comes,
        // a camp quest counts its raw fame (× the zone's famefactor / mindivisor, no rates).
        world.WorldChests.Killed(map, killer, mob, t.Fame * (danger?.FameFactor ?? 1) / (t.MinDivisor > 0 ? t.MinDivisor : 1));
        world.Bosses.Killed(mob); // stageH: a world boss's slot waits for its next time, the death is announced
        world.Hunts.Killed(mob, killer); // stageN hunts: a hunt's target slain wins the hunt (fame, HuntFinished)
        world.Vanity.Killed(killer, mob, bossLooters); // stageN vanity: a boss, miniboss or champion may drop a cosmetic (very rarely)
        world.Expeditions.Killed(map, mob); // stageH: an expedition's task counts it (261 to the members inside)
        world.Dungeons.Killed(map, mob); // stageH: a dungeon boss frees the boss chests it guarded
        world.Corrupted.Killed(map, killer, mob); // stageL: a corrupted dungeon's points (451), the demon, its completion, the chest guards
        world.Hellgates.Killed(map, mob); // stageL: a hellgate's points (453), objective 2, the guardian, the minibosses' chest
        world.Mists.Killed(map, mob, mistsLooters); // stageL: a mist boss's death line and its reward chest; phase B: a wisp's portal, a shrine room
        world.Mists.Standing(map, group, gain); // stageL mists phase B: the Brecilien standing of a mists kill's fame

        // PVE-DESIGN step 11: the journal's killmob missions, the bounties and a boss's season bonus, for the killer and its fame group;
        // a world boss's for every contributor too (its looters: in an open-world fight most of them are not in the killer's party).
        IReadOnlyList<Player> credited = bossLooters is null ? group : [.. group.Union(bossLooters)];
        AlbionJournal.Killed(map, killer, mob, credited);
        TutorialJourney.MobKilled(killer, mob);
        world.Bounties.Killed(map, killer, mob, credited);
        world.Guilds.Killed(map, mob, credited); // guilds SPEC §10.3/§10.6: the clear experience of a dungeon boss, the contracts
        world.GuildGoals.OnKill(map, killer, mob, credited); // stage I: the weekly guild goals (killmobs, bosses, dungeons)
        foreach (var p in credited.Contains(killer) ? credited : [killer, .. credited]) Seasons.OnBossKill(p, mob);
        Log.Info("combat", string.Create(CultureInfo.InvariantCulture,
            $"'{killer.Character.Name}' killed {mob}: fame {gain / 10000.0:0.####}{(group.Count > 1 ? $" each for {group.Count} party members" : "")} (total {killer.Character.Fame / 10000.0:0.####}), energy +{t.EnergyReward}"));
        // 5./6. A corpse until postDeathLifetime, then off the map; its spawn point respawns.
        map.Timers.At(world.Clock.Now + TimeSpan.FromSeconds(t.PostDeathLifetime), () => SpawnManager.Despawn(mob, world));
    }

    /// <summary>The gamedata ClusterDangerBonus of the map's cluster (SAFEAREA → safe); null without one (cities, no data).</summary>
    public static DangerBonus? DangerOf(World world, Map map) =>
        map.Template?.DangerType is { } type ? world.GameData?.DangerBonuses.GetValueOrDefault(type) : null;

    /// <summary>
    /// The items <paramref name="mob"/> drops (its &lt;Loot&gt; rolled with loot.xml on the map's random, with the cluster's
    /// moblootfactor × globalmoblootfactor × Rates:Loot as the total loot factor, Loot:BlackMarketChance (not × Rates:Loot: it is on the
    /// factor already) and Loot:BlackMarketSoftCap for the useblackmarket items and the mob's tier as the content tier (loot-balance SPEC):
    /// <see cref="Data.LootTables.RollMob"/>; or <see cref="World.LootOverride"/>) into a <see cref="LootBag"/> for <paramref name="killer"/> at the mob's position,
    /// put on the map (NewLoot to the players in view) and taken off again after the bag time. Nothing when nothing drops;
    /// items the server cannot create are logged and left out. A stackable item is one stack of its amount, equipment one
    /// item per piece. <paramref name="looters"/>: who may open the bag (the killer by default; a world boss's contributors, stageH),
    /// <paramref name="party"/> false: not the killer's party either (a personal boss bag).
    /// </summary>
    private static void DropLoot(World world, Player killer, Mob mob, long ticks, DangerBonus? danger, Guid[]? looters = null, bool party = true)
    {
        var map = mob.Map!;
        IReadOnlyList<(string Item, int Amount)> drops = world.LootOverride?.Invoke(mob)
            ?? (mob.Template.Loot is { } loot && world.Clusters?.LootIfLoaded is { } tables
                ? tables.RollMob(loot, map.Random, (danger?.MobLootFactor ?? 1) * (world.GameData?.GlobalMobLootFactor ?? 1) * (map.Difficulty?.Loot ?? 1) * world.Rates.Loot,
                    world.Config.LootBlackMarketChance,
                    name => LogOnce("loot:" + name, $"loot list {name} of {mob.Template.Name} is not in loot.xml"),
                    world.Config.LootTierCap ? mob.Template.Tier : 0, world.Config.LootBlackMarketSoftCap)
                : []);
        if (world.LootOverride is null) drops = WithTicket(world, mob, map, drops);
        var items = new List<Item>();
        foreach (var (name, amount) in drops)
        {
            if (world.Items.Find(name) is not { CanCreate: true } type)
            {
                LogOnce("item:" + name, $"loot item {name} is not in items.xml or has no New*Item event yet: not dropped");
                continue;
            }
            if (type.IsEquipment)
                for (var i = 0; i < amount; i++) items.Add(killer.Session.NewItem(type, 1));
            else items.Add(killer.Session.NewItem(type, amount));
        }
        if (items.Count == 0) return;
        var container = new Container(Guid.NewGuid(), items.Count);
        for (var i = 0; i < items.Count; i++) container.Put(i, items[i]);
        var bag = new LootBag(world.ObjectIds.Next(), mob, container, looters ?? [killer.Character.Id], (float)(danger?.MobLootFactor ?? 1), ticks,
            party && world.Parties.Of(killer.Character.Id) is { } own ? [own.Id] : null); // NewLoot key 11 (stageD party SPEC §6)
        map.AddObject(bag);
        map.Timers.At(world.Clock.Now + world.Combat.LootBagTime, () => RemoveBag(bag, "its time is up"));
        Log.Info("combat", $"{mob} dropped {string.Join(", ", items.Select(i => $"{i.Template.Name} x{i.Count}"))}");
    }

    /// <summary>
    /// Stage I (invasions SPEC §3.4): an open-world boss (miniboss) kill drops one invasion ticket (Drops.Item) with chance Drops.Boss
    /// (Drops.Miniboss) × Rates:Loot, at most 1; never off the open world (dungeon floors, expeditions) or from an event's own mobs.
    /// </summary>
    private static IReadOnlyList<(string Item, int Amount)> WithTicket(World world, Mob mob, Map map, IReadOnlyList<(string Item, int Amount)> drops)
    {
        var d = world.EventSettings.Drops;
        var chance = mob.Template.Category switch { "boss" => d.Boss, "miniboss" => d.Miniboss, _ => 0 };
        if (chance <= 0 || mob.Event is not null || map.Difficulty is not null || !WorldEvents.Fits(map)) return drops;
        if (world.Events.Random.NextDouble() >= Math.Min(1, chance * world.Rates.Loot)) return drops; // its own random: the map's rolls stay as they were
        return [.. drops, (d.Item, 1)];
    }

    /// <summary>
    /// The guild tax of every captured own pickup (TakeSilver key 5: 15 % of the gross, floored). The official player was in a
    /// guild; here it is <see cref="ServerConfig.SilverPickupTax"/> (0), for a guild member Guilds:SilverTax into its guild's account.
    /// </summary>
    public const double CapturedSilverTax = 0.15;

    /// <summary>
    /// The premium factor of every captured own pickup (TakeSilver key 7 true: gross = amount × key 8 × 1.5). The official
    /// player had premium; here it is <see cref="ServerConfig.SilverPickupFactor"/> (1).
    /// </summary>
    public const double CapturedSilverBonus = 1.5;

    /// <summary>
    /// The silver pile of <paramref name="mob"/> (SPEC §5c.3) for each SILVERCOINS_* &lt;Loot&gt;&lt;Harvestable type tier&gt; of
    /// its mobs.xml entry: amount = round(the type's level-1 yield at that tier (SILVERCOINS_LOOT_ROAMING T4 22.5) ×
    /// silvermodifier × r × Rates:Silver × (1 + the killer's guild silver perk)), r uniform 0.84..0.88 on the map's random (ghoul 2.494 → 47-49); none when that is 0. The factor
    /// (TakeSilver key 8) is the cluster's mobsilverlootfactor × globalmobsilverlootfactor. On the map (NewSilverObject to the
    /// players in view), off again after the tier's decaytimeseconds (300 s).
    /// </summary>
    private static void DropSilver(World world, Player killer, Mob mob, long ticks, DangerBonus? danger)
    {
        var map = mob.Map!;
        foreach (var h in mob.Template.Loot?.Elements("Harvestable") ?? [])
        {
            var type = (string?)h.Attribute("type") ?? "";
            if (!type.StartsWith("SILVERCOINS", StringComparison.Ordinal)) continue;
            int.TryParse((string?)h.Attribute("tier"), out var tier);
            if (world.Clusters?.Harvestables.Find(type)?.Tiers.GetValueOrDefault(tier) is not { } data
                || data.Levels.FirstOrDefault(l => l.From <= 1 && l.To >= 1) is not { ExactYield: > 0 } level)
            {
                LogOnce("silver:" + type + tier, $"silver {type} T{tier} of {mob.Template.Name} is not in harvestables.xml: none dropped");
                continue;
            }
            // Guilds SPEC §10.3: the silver perk of the killer's guild level, a Rates-like factor.
            var amount = (int)Math.Round(level.ExactYield * mob.Template.SilverModifier * (0.84 + 0.04 * map.Random.NextDouble()) * (map.Difficulty?.Silver ?? 1) * world.Rates.Silver
                * (1 + world.Guilds.SilverBonus(killer)), MidpointRounding.AwayFromZero);
            if (amount <= 0) continue;
            var factor = (danger?.MobSilverLootFactor ?? 1) * (world.GameData?.GlobalMobSilverLootFactor ?? 1);
            var pile = new SilverPile(world.ObjectIds.Next(), mob, amount, factor, killer.Character.Id, ticks, world.Parties.Of(killer.Character.Id)?.Id ?? 0)
            {
                GuildId = world.Guilds.SharingGuild(killer)?.Id ?? Guid.Empty, // Guilds:ShareWithoutParty (§10.4)
            };
            map.AddObject(pile);
            var decay = double.TryParse(data.Attrs.GetValueOrDefault("decaytimeseconds"), NumberStyles.Float, CultureInfo.InvariantCulture, out var s) && s > 0 ? s : 300;
            map.Timers.At(world.Clock.Now + TimeSpan.FromSeconds(decay), () => RemoveSilver(pile, "it decayed"));
            Log.Info("combat", $"{mob} dropped {amount} silver (factor {factor.ToString("0.####", CultureInfo.InvariantCulture)})");
        }
    }

    /// <summary>
    /// What taking a pile of <paramref name="amount"/> with <paramref name="factor"/> (TakeSilver 62 key 8) pays, a7m raw:
    /// gross = amount × factor × <paramref name="bonus"/>, tax = <paramref name="tax"/> of it floored, net = gross − tax.
    /// The local default (bonus 1, tax 0) is the official pickup without premium and guild (stage 5 SPEC §5c.3: 173 → 2257650,
    /// no tax). With the captured 1.5 and 15 % (103829): 49 → 959175 / 143876, 397 → 7771275 / 1165691, 3 → 58725 / 8808 (safe 1.305).
    /// </summary>
    public static (long Gross, long Tax, long Net) SilverValue(int amount, double factor, double bonus = 1, double tax = 0)
    {
        var gross = (long)Math.Round((decimal)amount * (decimal)Math.Round(factor * 10000) * (decimal)bonus, MidpointRounding.AwayFromZero);
        var kept = (long)Math.Floor(gross * (decimal)tax);
        return (gross, kept, gross - kept);
    }

    /// <summary>Takes <paramref name="pile"/> off its map (a Leave to every client that knows it).</summary>
    public static void RemoveSilver(SilverPile pile, string why)
    {
        if (pile.Map is not { } map || !map.Objects.ContainsKey(pile.Id)) return;
        map.RemoveObject(pile);
        Log.Debug("combat", $"{pile} removed: {why}");
    }

    /// <summary>Takes <paramref name="bag"/> off its map (a Leave to every client that knows it) and closes it for whoever has it open.</summary>
    public static void RemoveBag(LootObject bag, string why)
    {
        if (bag.Map is not { } map || !map.Objects.ContainsKey(bag.Id)) return;
        foreach (var p in map.Players.Values)
            if (p.OpenBag == bag)
            {
                p.OpenBag = null;
                p.Session.Send(new DetachItemContainerEvent { b20 = bag.ItemsFor(p).Id });
            }
        map.RemoveObject(bag);
        Log.Debug("combat", $"{bag} removed: {why}");
    }

    // ---- the player's combat state (SPEC §5b.5) ----

    /// <summary>
    /// <paramref name="p"/> fights <paramref name="enemy"/> (an accepted auto-attack or own damage): it joins the enemies and
    /// the combat timer restarts. The first time: InCombatStateUpdate (277) {0 me, 1 true} and RegenerationHealthChanged (91)
    /// {0 me, 1 now, 2 current, 3 max, 6 now} without key 5: the health regeneration stops in combat (captured 115 ms after
    /// the first AttackStart, before its hit). <paramref name="hit"/> (stage 5d: the player took a mob's damage): {1 true,
    /// 2 true}, once per combat, also when the player was in combat already by its own attacks (SPEC §5b.5, §5d.1). Mounted
    /// (stageD mounts SPEC §4.5, 566.18 / 587.12): RegenerationPlayerComboChanged (96) without keys 5 and 15 instead, before the
    /// InCombatStateUpdate: the mount bar stops regenerating too. <paramref name="enemy"/> null: a fight with a player (stageE pvp),
    /// which ends only by the timeout.
    /// </summary>
    public static void EnterCombat(Player p, Mob? enemy, bool hit = false)
    {
        var c = p.Combat;
        if (enemy is not null) c.Enemies.Add(enemy);
        else c.LastPvp = p.World.Clock.Now;
        c.LastAction = p.World.Clock.Now;
        if (c.InCombat && (!hit || c.Hit)) return;
        var first = !c.InCombat;
        c.InCombat = true;
        c.Hit |= hit;
        var now = p.World.GameNow;
        if (first)
        {
            p.Health.Set(p.Health.At(now), Math.Max(now, p.Health.Stamp));
            p.Health.RegenPerSecond = 0;
        }
        if (first) Mounts.SyncRegeneration(p, now); // the mount bar stops too, mounted or not
        if (first && p.IsMounted) p.Session.Send(PlayerCombo(p, now));
        p.Session.Send(new InCombatStateUpdateEvent { ddn = p.Id, cj3 = true, cj4 = c.Hit });
        if (first && !p.IsMounted) p.Session.Send(RegenerationChanged(p, now));
    }

    /// <summary>
    /// The end of <paramref name="p"/>'s combat (its last enemy died, or 20 s without a hit), in the captured order:
    /// InCombatStateUpdate {0 me, 2 true}, RegenerationHealthChanged with key 5 = the regeneration out of combat, then
    /// InCombatStateUpdate {0 me}. Mounted: RegenerationPlayerComboChanged with both regenerations back instead (stageD mounts SPEC
    /// §4.5: 569.57 / 590.45, 5 = 51.79, 15 = 19.13). The mobs that still had it as their target forget it.
    /// </summary>
    public static void EndCombat(Player p)
    {
        var c = p.Combat;
        if (!c.InCombat) return;
        c.InCombat = false;
        c.Hit = false;
        c.Enemies.Clear();
        c.LastPvp = null;
        var now = p.World.GameNow;
        p.Health.Set(p.Health.At(now), Math.Max(now, p.Health.Stamp));
        p.Health.RegenPerSecond = p.ComputeStats().HealthRegen;
        Mounts.SyncRegeneration(p, now);
        p.Session.Send(new InCombatStateUpdateEvent { ddn = p.Id, cj4 = true });
        p.Session.Send(p.IsMounted ? PlayerCombo(p, now) : RegenerationChanged(p, now));
        p.Session.Send(new InCombatStateUpdateEvent { ddn = p.Id });
        if (p.Map is { } map)
            foreach (var mob in map.Objects.Values.OfType<Mob>())
                if (mob.TargetId == p.Id) mob.TargetId = 0;
    }

    /// <summary>RegenerationHealthChanged (91, ayl): 0 player, 1 now, 2 current (left out at 0: knocked down), 3 max, 5 regeneration per second (left out at 0: in combat), 6 now (Int64 ticks).</summary>
    internal static RegenerationHealthChangedEvent RegenerationChanged(Player p, long now) => new()
    {
        ddn = p.Id, dtt = now, dtu = p.Health.At(now), dtv = p.Health.Max, dtx = p.Health.RegenPerSecond, dty = now,
    };

    /// <summary>
    /// RegenerationPlayerComboChanged (96, ayo) with the player's bars as they are stored: 0 player, 1 now, 2-6 health (value,
    /// max, regeneration cap, regeneration per second, stamp), 7-11 energy, 12-16 the mount (16, its stamp, always sent, as official).
    /// </summary>
    public static RegenerationPlayerComboChangedEvent PlayerCombo(Player p, long now)
    {
        ResourceBar hp = p.Health, en = p.Energy, mount = p.MountHealth;
        return new RegenerationPlayerComboChangedEvent
        {
            ddn = p.Id, dug = now,                                                                     // [0] player, [1] time (ticks)
            duh = hp.Value, dui = hp.Max, duj = hp.RegenCap, duk = hp.RegenPerSecond, dul = hp.Stamp, // [2-6] health
            dum = en.Value, dun = en.Max, duo = en.RegenCap, dup = en.RegenPerSecond, duq = en.Stamp, // [7-11] energy
            dur = mount.Value, dus = mount.Max, dut = mount.RegenCap, duu = mount.RegenPerSecond,     // [12-15] mount
            duv = mount.Stamp,                                                                         // [16] always sent, as official
        };
    }

    /// <summary>
    /// The world tick: the knockdown and get-up of stage 5e (<see cref="Knockdown.Update"/>), then a player's combat ends
    /// gamedata InCombat activetimeoutseconds (20 s) after its last hit.
    /// </summary>
    public static void Update(Player p, TimeSpan now)
    {
        SpellEffects.ExpireAfterMovement(p);
        Knockdown.Update(p, now);
        Passives.Update(p); // the pulsingspellpassive pulses (stageD effects batch 2)
        var c = p.Combat;
        if (!c.InCombat) return;
        var timeout = TimeSpan.FromSeconds(p.World.GameData?.InCombatActiveSeconds ?? 20);
        if (now - c.LastAction >= timeout) EndCombat(p);
    }

    /// <summary>
    /// A player leaves its map: its pending cast is dropped and the mobs forget it as their target and drop it from their
    /// threat lists (no events: its client is gone or changes cluster; a mob left without threat resets in its next tick).
    /// </summary>
    public static void Leave(Player p)
    {
        p.PvpState.FlagCast = null; // stageE: a flag cast under way is dropped (no events, as the cast's)
        p.PvpState.Execute = null; // stageE E3: so is an execute
        p.PvpState.Revive = null; // stageN: and a revive
        p.PvpState.HitBy.Clear();
        p.PvpState.DownCredits = null; // stage I: a knockdown's credits end with it
        p.World.Duels.Left(p); // stageE: its duel ends (destroyed), its challenges lapse; the opponent is told while p is still on the map
        Knockdown.Leave(p);
        SpellCasts.EndToggles(p, SpellCasts.ToggleEnd.Left); // quietly (WorldSession.Leave has ended them already before saving the buffs)
        p.Combat.Cast = null;
        // A channel or forced movement under way stops quietly: no ChannelingEnded, no endeffect on the map left (stageD SPEC §7 step 9).
        if (p.Combat.Channel is { } channel)
        {
            channel.Over = true;
            p.Combat.Channel = null;
        }
        p.Effects.Forced = null;
        p.Combat.Enemies.Clear();
        p.OpenBag = null;
        if (p.Map is not { } map) return;
        Summons.SummonerGone(p.World, map, p); // its summons (a vanity pet) go with it
        AI.MobAI.Forget(map, p);
        foreach (var mob in map.Objects.Values.OfType<Mob>())
            if (mob.TargetId == p.Id) mob.TargetId = 0;
    }

    // ---- helpers ----

    private static readonly HashSet<string> Logged = [];

    /// <summary>Logs <paramref name="text"/> once per <paramref name="key"/> while the server runs.</summary>
    internal static void LogOnce(string key, string text)
    {
        lock (Logged)
            if (!Logged.Add(key)) return;
        Log.Info("combat", text);
    }

    /// <summary>
    /// Like <see cref="LogOnce(string, string)"/>, but builds the text only the first time (for texts that are costly to
    /// make, such as the spells.xml tag count of the no-op effect tags).
    /// </summary>
    internal static void LogOnce(string key, Func<string> text)
    {
        lock (Logged)
            if (!Logged.Add(key)) return;
        Log.Info("combat", text());
    }

    private static string? Attr(ItemTemplate t, string name) => t.Attrs.GetValueOrDefault(name);

    private static double Number(ItemTemplate t, string name, double fallback) =>
        Attr(t, name) is { Length: > 0 } v && double.TryParse(v, NumberStyles.Float, CultureInfo.InvariantCulture, out var d) ? d : fallback;
}
