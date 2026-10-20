using System.Globalization;
using System.Xml.Linq;
using AlbionLocal.Photon;
using AlbionLocal.Server.Data;
using AlbionLocal.Server.Entities;

namespace AlbionLocal.Server.Systems;

/// <summary>A passive spell a worn item gives (its chosen passive slot): the item and the ability power its values scale with.</summary>
/// <remarks><see cref="Factor"/>: an awakened weapon's trait spell runs with its traitvar1 (variablefactor="traitvar1"); 1 for the others.</remarks>
public sealed record PassiveSpell(SpellTemplate Spell, Item Source, double Ap, double CcAp, double Factor = 1);

/// <summary>
/// A resourcegatheringbuff (passive, a tool's or gatherer gear's) or resourcegatheringbuffovertime (a food's or potion's): the yield
/// bonus <see cref="Value"/> of <see cref="BuffType"/> (gatheringyield, fishingyield) on resourcetype <see cref="Resource"/> ("" = any)
/// of the tiers <see cref="MinTier"/>..<see cref="MaxTier"/>.
/// </summary>
public sealed record GatheringBuff(string BuffType, string Resource, int MinTier, int MaxTier, double Value)
{
    public bool Covers(string bufftype, string? resource, int tier) =>
        BuffType == bufftype && (Resource.Length == 0 || string.Equals(Resource, resource, StringComparison.OrdinalIgnoreCase)) && tier >= MinTier && tier <= MaxTier;
}

/// <summary>
/// What a player's worn passives add up to (<see cref="Passives.Of"/>), worked out again when the equipment changes: the spells,
/// the buff sums by attribute (scaled as buffovertime is, bej.a_4, with the item's ability power), the weight bonuses by item name,
/// the gathering yield buffs, and the next pulse of each pulsingspellpassive.
/// </summary>
public sealed class PassiveSet
{
    public static readonly PassiveSet None = new() { Key = 0, Spells = [], Buffs = new Dictionary<string, double>(), Weight = new Dictionary<string, double>(), Gathering = [] };

    /// <summary>The equipment signature the set was made for (the worn items and their passive choices).</summary>
    public required int Key { get; init; }

    public required IReadOnlyList<PassiveSpell> Spells { get; init; }

    public required IReadOnlyDictionary<string, double> Buffs { get; init; }

    /// <summary>weightbonus: per item uniquename the share of its weight that is not carried (PASSIVE_BACKPACK_FIBER_T4: fiber 0.3).</summary>
    public required IReadOnlyDictionary<string, double> Weight { get; init; }

    public required IReadOnlyList<GatheringBuff> Gathering { get; init; }

    /// <summary>pulsingspellpassive: the GameTimeStamp of its next pulse and how many are left (−1 = count 0, endless).</summary>
    internal Dictionary<XElement, (long Next, int Left)> Pulses { get; } = [];

    /// <summary>The spells its pulses applied (PASSIVE_BAG_FAME_T4_EFFECT, bindtoitem): they come off with the item.</summary>
    internal HashSet<short> Applied { get; } = [];
}

/// <summary>
/// The passive spells of a player's worn items (stageD effects SPEC §7 batch 2: capes, weapon and armor passives, bags, tools;
/// batch 3: weightbonus, resourcegatheringbuff), the passivespell tags of the client's bja classes, none of them listed in
/// ActiveSpellEffectsUpdate (non-timed: the client works them out from the equipment itself):
/// <list type="bullet">
/// <item><b>buff</b> type value: added to the unit's sums (<see cref="EffectList.Sum"/>) like a buffovertime, scaled with the item's
/// ability power unless ignoreabilitypowerscaling (variablefactor is not modelled).</item>
/// <item><b>applyeffectoneventpassive</b> event spell applyeffecttarget chance castfinishedslot: the owner's events
/// (<see cref="Trigger"/>) apply the spell as applyeffectoneventactive does, while its conditions hold.</item>
/// <item><b>pulsingspellpassive</b> spell count interval initialinterval: the spell on the owner every interval (count 0 = while worn;
/// <see cref="Update"/>: PASSIVE_BAG_FAME_T4 a destiny board bonus stack every 30 s).</item>
/// <item><b>healonhitpassive</b> damagetype percentage chance attribute (and the timed healonhit of the list): a share of the damage
/// dealt comes back as a heal (<see cref="OnHit"/>, HealthUpdate origin 4); updateinterval / maxhealpercentageperinterval are not modelled.</item>
/// <item><b>weightbonus</b> item value: that item weighs (1 − value) of its weight in the bag (<see cref="Load"/>). The client sums the
/// weights itself (NOTES-protocol: no load event); the server needs the load only for IfOwnLoadPercent (dashes and blinks need
/// &lt; 150 %), with the client's formula (<see cref="Load"/>, <see cref="MaxLoad"/>; stageJ carry-load SPEC).</item>
/// <item><b>resourcegatheringbuff</b> bufftype resourcetype tier value: the harvest's yield bonus (<see cref="YieldBonus"/>, HarvestFinished key 6).</item>
/// <item><b>dummypassive</b>: a marker (the backpack's tier), nothing to do.</item>
/// </list>
/// Touched only under <see cref="World.Sync"/>.
/// </summary>
public static class Passives
{
    /// <summary>
    /// The equipment slots whose items' passive spells count: main hand to cape and the mount (slot 7), as the client's equipment
    /// stats (c2e.d4 0x180A39F60: every slot while its item is valid there, with no mount-state test; stageJ carry-load SPEC §1.2).
    /// The mount's item passive (PASSIVE_MAXLOAD_DRAKE 5.7, _DIREBOAR, _SWAMPDRAGON, _GIANTSTAG, _HORSE, _COMBAT_MULE, the
    /// negative _OWL) therefore counts whenever the mount is equipped, ridden or not; its halfmountedbuff / mountedbuff load is
    /// timed and switched through ActiveSpellEffectsUpdate (<see cref="Mounts"/>).
    /// </summary>
    public const int Slots = 8;

    /// <summary>
    /// CharacterSpellSlot of a castfinishedslot / multispell slot name: mainhand1 0, mainhand2 1, offhandormainhand3 2, armor 3, head 4,
    /// shoes 5, potion 12, food 13; null for the others (the transformation slots).
    /// </summary>
    public static byte? SlotOf(string? name) => name switch
    {
        "mainhand1" => 0, "mainhand2" => 1, "offhandormainhand3" => 2, "armor" => 3, "head" => 4, "shoes" => 5, "potion" => 12, "food" => 13, _ => null,
    };

    /// <summary>The passives of <paramref name="p"/>'s worn items, made anew when the equipment (or a passive choice) changed.</summary>
    public static PassiveSet Of(Player p)
    {
        var key = Signature(p);
        if (p.Effects.Passive is { } set && set.Key == key) return set;
        var old = p.Effects.Passive;
        set = key == 0 ? PassiveSet.None : Build(p, key);
        p.Effects.Passive = set;
        // What the old items' pulses applied comes off with them (bindtoitem): the list goes out at once.
        if (old is { Applied.Count: > 0 } && SpellEffects.Remove(p.World, p, s => s.Caster == p.Id && old.Applied.Contains(s.Spell.Id)) && p.Map is not null)
        {
            SpellEffects.Send(p.World, p);
            SpellEffects.Refresh(p.World, p);
        }
        if (set.Spells.Count > 0) Log.Debug("combat", $"'{p.Character.Name}' passives: {string.Join(", ", set.Spells.Select(s => s.Spell.Name))}");
        return set;
    }

    /// <summary>A hash of the worn items' object ids and passive choices; 0 = nothing that could carry a passive.</summary>
    private static int Signature(Player p)
    {
        var hash = new HashCode();
        var any = false;
        var e = p.Inventory.Equipment;
        var plugins = p.World.Plugins.WantsPassives; // an OnCalcPassives listens: every worn item counts
        for (var s = 0; s < Slots; s++)
        {
            if (e[s] is not { } item || item.Passive.Length == 0 && item.Soul is null && !plugins) continue;
            any = true;
            hash.Add(s);
            hash.Add(item.Id);
            hash.Add(Broken(item));
            foreach (var id in item.Passive) hash.Add(id);
            if (item.Soul is { } soul)
            {
                hash.Add(soul.AttunedTo(p.Character.Id));
                foreach (var t in soul.Traits) hash.Add((t.Name, t.Roll));
            }
        }
        return any ? hash.ToHashCode() | 1 : 0;
    }

    /// <summary>A broken item (durability 0) gives no stats (the client's c1q.b5 0x180A1F1E0 in c2e.m_6).</summary>
    private static bool Broken(Item item) => item.DurabilityRaw <= 0;

    private static PassiveSet Build(Player p, int key)
    {
        var spells = new List<PassiveSpell>();
        var buffs = new Dictionary<string, double>(StringComparer.Ordinal);
        var weight = new Dictionary<string, double>(StringComparer.Ordinal);
        var gathering = new List<GatheringBuff>();
        var settings = p.World.GameData?.Effects ?? Game.EffectSettings.Default;
        var e = p.Inventory.Equipment;
        for (var s = 0; s < Slots; s++)
        {
            if (e[s] is not { } item || Broken(item)) continue;
            foreach (var id in item.Passive)
            {
                if (id < 0 || p.World.Spells?.Template(id) is not { Kind: "passivespell" } spell) continue;
                var ap = Combat.AbilityPowerOf(p, item);
                spells.Add(new PassiveSpell(spell, item, ap, Combat.CcAbilityPowerOf(p, item)));
                foreach (var node in spell.Effects)
                    switch (node.Name.LocalName)
                    {
                        case "buff":
                            var type = (string?)node.Attribute("type") ?? "";
                            var value = SpellTemplate.Float(node, "value");
                            buffs[type] = buffs.GetValueOrDefault(type) + (SpellTemplate.Bool(node, "ignoreabilitypowerscaling") ? value : SpellEffects.Scale(type, value, ap, settings));
                            break;
                        case "weightbonus":
                            if ((string?)node.Attribute("item") is { Length: > 0 } name) weight[name] = weight.GetValueOrDefault(name) + SpellTemplate.Float(node, "value");
                            break;
                        case "resourcegatheringbuff":
                            gathering.Add(GatheringOf(node));
                            break;
                    }
            }
            Traits(p, item, spells, buffs, settings);
        }
        p.World.Plugins.CalcPassives(p, buffs); // the plugins' Items.OnCalcPassives
        var set = new PassiveSet { Key = key, Spells = spells, Buffs = buffs, Weight = weight, Gathering = gathering };
        var now = p.World.GameNow;
        foreach (var ps in spells)
            foreach (var node in ps.Spell.Effects.Where(n => n.Name.LocalName == "pulsingspellpassive"))
            {
                var count = (int)SpellTemplate.Float(node, "count");
                set.Pulses[node] = (now + SpellCasts.Seconds(SpellTemplate.Float(node, "initialinterval")), count > 0 ? count : -1);
            }
        return set;
    }

    /// <summary>
    /// The traits of an awakened weapon attuned to <paramref name="p"/> (stageN awakening SPEC §4): each trait's PASSIVE_TRAIT_* spell as a
    /// passive with its traitvar1 (<see cref="LegendaryData.Factor"/> of the roll): its buff values × traitvar1, scaled with the item's
    /// ability power as the other passive buffs (the legendaryitemtraits progressions of gamedata are not used), and its healonhitpassive
    /// (TRAIT_LIFESTEAL) × traitvar1 in <see cref="OnHit"/>. TRAIT_ITEM_POWER's itempower goes to the weapon's item power instead
    /// (<see cref="Combat.ItemPower"/>). A soul attuned to someone else gives nothing.
    /// </summary>
    private static void Traits(Player p, Item item, List<PassiveSpell> spells, Dictionary<string, double> buffs, Game.EffectSettings settings)
    {
        if (item.Soul is not { } soul || !soul.AttunedTo(p.Character.Id) || p.World.Legendary is not { } data || p.World.Spells is not { } table) return;
        var ap = Combat.AbilityPowerOf(p, item);
        foreach (var trait in soul.Traits)
        {
            if (data.Trait(trait.Name) is not { } def || table.Template(def.Spell) is not { } spell) continue;
            var factor = data.Factor(trait.Roll);
            spells.Add(new PassiveSpell(spell, item, ap, Combat.CcAbilityPowerOf(p, item), factor));
            foreach (var node in spell.Effects)
            {
                if (node.Name.LocalName != "buff") continue;
                var type = (string?)node.Attribute("type") ?? "";
                if (type == "itempower") continue;
                var value = SpellTemplate.Float(node, "value") * factor;
                buffs[type] = buffs.GetValueOrDefault(type) + (SpellTemplate.Bool(node, "ignoreabilitypowerscaling") ? value : SpellEffects.Scale(type, value, ap, settings));
            }
        }
    }

    /// <summary>The item power TRAIT_ITEM_POWER adds to <paramref name="item"/> worn by <paramref name="p"/> (its buff value × traitvar1); 0 without one.</summary>
    public static double TraitItemPower(Player p, Item item)
    {
        if (item.Soul is not { } soul || !soul.AttunedTo(p.Character.Id) || p.World.Legendary is not { } data || p.World.Spells is not { } table) return 0;
        var sum = 0.0;
        foreach (var trait in soul.Traits)
            if (data.Trait(trait.Name) is { } def && table.Template(def.Spell) is { } spell)
                sum += spell.Effects.Where(n => n.Name.LocalName == "buff" && (string?)n.Attribute("type") == "itempower")
                    .Sum(n => SpellTemplate.Float(n, "value")) * data.Factor(trait.Roll);
        return sum;
    }

    /// <summary>A gathering buff node: tier (exact) or mintier..maxtier (the over-time form), resourcetype, bufftype, value.</summary>
    public static GatheringBuff GatheringOf(XElement node)
    {
        var tier = (int)SpellTemplate.Float(node, "tier");
        var min = node.Attribute("mintier") is not null ? (int)SpellTemplate.Float(node, "mintier") : tier > 0 ? tier : 0;
        var max = node.Attribute("maxtier") is not null ? (int)SpellTemplate.Float(node, "maxtier") : tier > 0 ? tier : 99;
        return new GatheringBuff((string?)node.Attribute("bufftype") ?? "", (string?)node.Attribute("resourcetype") ?? "", min, max, SpellTemplate.Float(node, "value"));
    }

    // ---- triggers and pulses ----

    /// <summary>
    /// Event <paramref name="e"/> of <paramref name="owner"/> for its passives' applyeffectoneventpassive (AutoAttack, CastFinished with
    /// castfinishedslot = the cast's <paramref name="slot"/>, DoDamage, ReceiveDamage, …): while the node's conditions hold and its
    /// chance comes up, its spell runs on the owner (self) or on <paramref name="other"/> (enemy: an attackable mob or a Hostile player,
    /// stageJ pvp-spells phase 2) with the item's ability power
    /// (PASSIVE_CAPE_CAERLEON: CastFinished of mainhand1 → its cooldown reset, then its cape_cooldown).
    /// </summary>
    internal static void Trigger(Player owner, string e, IUnit? other, long ticks, byte? slot)
    {
        if (owner.Map is not { } map || owner.World.Spells is not { } spells) return;
        foreach (var ps in Of(owner).Spells.ToList())
            foreach (var node in ps.Spell.Effects)
            {
                if (node.Name.LocalName != "applyeffectoneventpassive" || (string?)node.Attribute("event") != e) continue;
                if (node.Attribute("castfinishedslot") is { } wanted && SlotOf(wanted.Value) != slot) continue;
                if (spells.Template((string?)node.Attribute("spell")) is not { } spell) continue;
                var enemy = (string?)node.Attribute("applyeffecttarget") is "enemy" or "groundatenemy";
                if (enemy && !SpellEffects.Hostile(owner, other, ticks)) continue;
                var target = enemy ? other : null;
                var c = new SpellCasts.SpellContext(owner, map, ps.Spell, 0, ps.Ap, target, SpellMovement.PositionOf(target ?? owner, ticks), ticks)
                    { CcAbilityPower = ps.CcAp, Depth = 1 };
                if (!SpellCasts.ConditionsHold(c, node)) continue;
                if (node.Attribute("chance") is not null && map.Random.NextDouble() >= SpellTemplate.Float(node, "chance", 1)) continue;
                SpellCasts.Execute(c, spell);
            }
    }

    /// <summary>The world tick of <paramref name="p"/>: the pulsingspellpassive pulses that are due run their spell on it.</summary>
    public static void Update(Player p)
    {
        if (p.Map is not { } map || p.Combat.Down is not null || p.World.Spells is not { } spells) return;
        var set = Of(p);
        if (set.Pulses.Count == 0) return;
        var now = p.World.GameNow;
        foreach (var (node, (next, left)) in set.Pulses.ToList())
        {
            if (next > now || left == 0) continue;
            var interval = Math.Max(0.1, SpellTemplate.Float(node, "interval", 1));
            set.Pulses[node] = (next + SpellCasts.Seconds(interval) is var after && after > now ? after : now + SpellCasts.Seconds(interval), left > 0 ? left - 1 : left);
            if (spells.Template((string?)node.Attribute("spell")) is not { } spell || set.Spells.FirstOrDefault(s => s.Spell.Effects.Contains(node)) is not { } ps) continue;
            set.Applied.Add(spell.Id);
            SpellCasts.Execute(new SpellCasts.SpellContext(p, map, ps.Spell, 0, ps.Ap, null, SpellMovement.PositionOf(p, now), now) { CcAbilityPower = ps.CcAp, Depth = 1 }, spell);
        }
    }

    // ---- heal on hit ----

    /// <summary>
    /// <paramref name="damage"/> dealt by <paramref name="p"/> (origin <paramref name="origin"/>): every healonhitpassive of its passives
    /// and healonhit in force on it whose damagetype fits (autoattack: the auto-attacks only; any) heals it by percentage × damage
    /// (attribute health, or energy), chance rolled, × the healmodifier sum unless usehealingmodifiers="false"; HealthUpdate origin 4.
    /// </summary>
    public static void OnHit(Player p, double damage, HitOrigin origin, long ticks)
    {
        if (damage <= 0 || p.Map is not { } map) return;
        var auto = origin is HitOrigin.MeleeAutoAttack or HitOrigin.RangedAutoAttack;
        var nodes = Of(p).Spells.SelectMany(s => s.Spell.Effects.Where(n => n.Name.LocalName == "healonhitpassive").Select(n => (s.Spell, Node: n, s.Factor)))
            .Concat(p.Effects.On(p.World.GameNow).Where(x => x.Sub.Tag == "healonhit").Select(x => (x.Spell.Spell, x.Sub.Node, Factor: 1.0))).ToList();
        foreach (var (spell, node, factor) in nodes)
        {
            if ((string?)node.Attribute("damagetype") == "autoattack" && !auto) continue;
            if (node.Attribute("chance") is not null && map.Random.NextDouble() >= SpellTemplate.Float(node, "chance", 1)) continue;
            var amount = damage * SpellTemplate.Float(node, "percentage") * factor;
            if (SpellTemplate.Bool(node, "usehealingmodifiers", true)) amount *= SpellEffects.HealFactor(p, ticks);
            if (amount <= 0) continue;
            if ((string?)node.Attribute("attribute") == "energy") SpellCasts.GainEnergy(p, amount, spell, ticks, HitOrigin.OnHitPassive);
            else Combat.HealPlayer(p, p, amount, HitOrigin.OnHitPassive, spell.Id, ticks);
        }
    }

    // ---- carry load (weightbonus) ----

    /// <summary>items.xml weight of <paramref name="item"/>'s type (0 without one).</summary>
    public static double WeightOf(ItemTemplate item) =>
        item.Attrs.GetValueOrDefault("weight") is { Length: > 0 } w && double.TryParse(w, NumberStyles.Float, CultureInfo.InvariantCulture, out var d) ? d : 0;

    /// <summary>
    /// What <paramref name="p"/> carries, in kg, as the client's ctp.ag1 0x18099CD60 (shared Albion.Common code, so the official
    /// server's IfOwnLoadPercent sees the same; stageJ carry-load SPEC §1.1): the bag's items, items.xml weight × count × (1 − the
    /// weightbonus of its type, at most 1) (a6d.jz 0x180933C50; PASSIVE_BACKPACK_FIBER_T4: 100 T4_FIBER 0.51 kg → 35.7 kg). The
    /// equipment weighs nothing (a6c.jm is a `return 0`): the worn gear, the mount, the potion and the food stacks.
    /// </summary>
    public static double Load(Player p)
    {
        var bonus = TimedWeight(p, Of(p).Weight);
        return p.Inventory.Bag.Items.Sum(i => WeightOf(i.Template) * i.Count * (1 - Math.Clamp(bonus.GetValueOrDefault(i.Template.BaseName), 0, 1)));
    }

    /// <summary>
    /// The worn weightbonus shares plus the weightbonusovertime in force (stageD effects batch 3; ASIA_BETA_BUFF: item T1_FIBER and its
    /// &lt;item name&gt; children, 0.66 each), × the entry's stacks; the worn table itself when there is none.
    /// </summary>
    private static IReadOnlyDictionary<string, double> TimedWeight(Player p, IReadOnlyDictionary<string, double> worn)
    {
        if (p.Effects.Count == 0) return worn;
        Dictionary<string, double>? sum = null;
        foreach (var (entry, sub) in p.Effects.On(p.World.GameNow))
        {
            if (sub.Tag != "weightbonusovertime") continue;
            sum ??= new Dictionary<string, double>(worn, StringComparer.Ordinal);
            var value = SpellTemplate.Float(sub.Node, "value") * entry.Stacks;
            var names = sub.Node.Elements("item").Select(i => (string?)i.Attribute("name")).Prepend((string?)sub.Node.Attribute("item"));
            foreach (var name in names.OfType<string>().Where(n => n.Length > 0).Distinct())
                sum[name] = sum.GetValueOrDefault(name) + value;
        }
        return sum ?? worn;
    }

    /// <summary>
    /// The maximum load as the client's cpo.ax5 0x180BC4480 (a4h.go 0x1808FFDF0 case 7, a4f.bp 0x1808F75E0; stageJ carry-load SPEC
    /// §1.2): max(1, (1 + D15) × ((1 + F15) × (50 + F14) + D14)), 14 maxload and 15 maxloadbonus, F the worn items' passive buffs
    /// (the bag, the mount's item passive; each scaled once with its item's ability power), D the timed effects of the list (the
    /// mountedbuff and halfmountedbuff load, potions), 50 characters.xml maxload. Its ActiveBuffScaling clamps are unrestricted.
    /// T4_BAG IP 700 (75.57) + T8_MOUNT_DRAKE_FIRE IP 1100 (1127.41) → 1252.98 kg.
    /// </summary>
    public static double MaxLoad(Player p)
    {
        var now = p.World.GameNow;
        var worn = Of(p).Buffs;
        var f14 = worn.GetValueOrDefault("maxload");
        var f15 = worn.GetValueOrDefault("maxloadbonus");
        var d14 = p.Effects.Sum("maxload", now) - f14;
        var d15 = p.Effects.Sum("maxloadbonus", now) - f15;
        return Math.Max(1, (1 + d15) * ((1 + f15) * ((p.World.GameData?.MaxLoad ?? 50) + f14) + d14));
    }

    /// <summary>IfOwnLoadPercent's value: the load in percent of the maximum (150 = overloaded: no dash, no blink).</summary>
    public static double LoadPercent(Player p) => Load(p) / MaxLoad(p) * 100;

    // ---- gathering (resourcegatheringbuff) ----

    /// <summary>
    /// The yield bonus share of <paramref name="p"/> on a <paramref name="resource"/> node of tier <paramref name="tier"/>
    /// (<paramref name="bufftype"/> gatheringyield, fishingyield): its worn resourcegatheringbuff values and its resourcegatheringbuffovertime
    /// in force, summed (PASSIVE_AVALON_YIELD_WOOD_T4: +0.1 on WOOD T2-T4).
    /// </summary>
    public static double YieldBonus(Player p, string bufftype, string? resource, int tier)
    {
        var sum = Of(p).Gathering.Where(g => g.Covers(bufftype, resource, tier)).Sum(g => g.Value);
        foreach (var (entry, sub) in p.Effects.On(p.World.GameNow))
            if (sub.Tag == "resourcegatheringbuffovertime" && GatheringOf(sub.Node) is var g && g.Covers(bufftype, resource, tier)) sum += g.Value * entry.Stacks;
        return sum;
    }

    /// <summary>HarvestFinished key 6: <paramref name="amount"/> × <paramref name="share"/>, the fraction rounded up at random (as the premium bonus, key 7).</summary>
    public static int Bonus(int amount, double share, Random random)
    {
        if (share <= 0 || amount <= 0) return 0;
        var x = amount * share;
        var whole = (int)Math.Floor(x);
        return whole + (random.NextDouble() < x - whole ? 1 : 0);
    }
}
