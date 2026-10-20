using System.Globalization;
using System.Numerics;
using System.Xml.Linq;
using AlbionLocal.Server.AI;
using AlbionLocal.Server.Data;
using AlbionLocal.Server.Entities;

namespace AlbionLocal.Server.Systems;

/// <summary>
/// The If* conditions of spells.xml that the players' runner (<see cref="SpellCasts"/>) and the mobs' runner (<see cref="MobCasts"/>)
/// share (stageN spells-bosses, audit item 10; analysis/work/stageN/spells-bosses/SPEC.md §1): the target's health and energy share
/// (the executes), its mob category, tier, faction and danger state (the boss scripts and the hide and territory spells), the item
/// type and weapon type it wears (the anti-armour spells), the channel tick (the boss channels), the caster mob's threat list, the
/// channeling and transformation of the target, the summons, the AI variables, the duel, the potion slot, the faction flags and the
/// cluster biome; and the value overrides (&lt;valueoverride&gt;&lt;IfCharge&gt;, the stacking damage of AXE_BLEED, FLAME_ORB, …).
/// <c>who</c>: Caster the caster, SpellTarget the spell's target, EffectTarget (and missing) the unit the effect acts on. A condition
/// this does not know returns null (the caller logs it once and reads it as false). Touched only under <see cref="World.Sync"/>.
/// </summary>
public static class SpellConditions
{
    /// <summary>What a condition is asked with: the caster, the spell's target, the unit the effect acts on, the map, the time, the channel tick (1-based; 0 = no channel).</summary>
    public readonly record struct Facts(IUnit Caster, IUnit SpellTarget, IUnit EffectTarget, Map Map, long Ticks, int ChannelTick);

    /// <summary>The conditions <see cref="Holds"/> decides (the start-up report and the SPEC count them).</summary>
    public static readonly IReadOnlySet<string> Known = new HashSet<string>(StringComparer.Ordinal)
    {
        "IfTargetHealthPercentage", "IfTargetEnergyPercentage", "IfTargetMobCategory", "IfTargetMobTier", "IfTargetMobFaction",
        "IfTargetMobDangerState", "IfTargetItemTypeEquipped", "IfTargetWeaponTypeEquipped", "IfChannelTick", "IfTotalNumberOfTargetsOnThreatList",
        "IfIsTargetTopOfThreatList", "IfIsTargetChanneling", "IfTargetIsTransformed", "IfSummonedMobCount", "IfVariableOnTarget", "IfInDuel",
        "IfEquipmentSlotEmpty", "IfTargetIsInFaction", "IfTargetIsInAnyFaction", "IfCasterIsInAnyFaction", "IfTargetIsInOpposingFaction",
        "IfClusterBiome", "IfIsTargetCarryingObject", "IfIsAnyMobWithVariableInRange", "IfTargetCCEffect", "IfTargetPositionIsInPursuitRange",
        "IfTargetMobIsMoving", "IfTargetHasPowerCrystal",
    };

    /// <summary>The unit <c>who</c> names (EffectTarget when missing).</summary>
    public static IUnit Who(XElement e, Facts f) => (string?)e.Attribute("who") switch
    {
        "Caster" => f.Caster,
        "SpellTarget" => f.SpellTarget,
        _ => f.EffectTarget,
    };

    /// <summary>Whether condition <paramref name="e"/> holds; null = not a condition this knows.</summary>
    public static bool? Holds(XElement e, Facts f)
    {
        var world = f.Caster switch { Player p => p.World, Mob m => m.World, _ => null };
        var unit = Who(e, f);
        switch (e.Name.LocalName)
        {
            case "IfTargetHealthPercentage": // the executes (… below 40 % health) and the boss phases
                return Compare(e, Share(unit, f.Ticks, energy: false));
            case "IfTargetEnergyPercentage":
                return Compare(e, Share(unit, f.Ticks, energy: true));
            case "IfTargetMobCategory": // mobs.xml category (hidemob, territoryguards, rd_elite, avalon)
                return unit is Mob mc && Text(e, "category").Equals(mc.Template.FameCategory, StringComparison.OrdinalIgnoreCase);
            case "IfTargetMobTier":
                return unit is Mob mt && Compare(e, mt.Template.Tier);
            case "IfTargetMobFaction": // op equals / notequals (not a CRITTER)
                if (unit is not Mob mf) return false;
                var same = Text(e, "value").Equals(mf.Template.Faction, StringComparison.OrdinalIgnoreCase);
                return Op(e) is "notequals" or "notequal" ? !same : same;
            case "IfTargetMobDangerState":
                return unit is Mob md && Text(e, "state").Equals(md.Template.DangerState, StringComparison.OrdinalIgnoreCase);
            case "IfTargetItemTypeEquipped": // slottype + shopsubcategory1 of the worn item (plate_armor, arcanestaff, …)
            {
                if (unit is not Player p) return false;
                var slot = Array.IndexOf(Inventory.SlotNames, Text(e, "slottype"));
                if (slot < 0 || p.Inventory.Equipment[slot] is not { } item) return false;
                var wanted = Text(e, "shopsubcategory1");
                return wanted.Length == 0 || wanted.Equals(item.Template.Attrs.GetValueOrDefault("shopsubcategory1"), StringComparison.OrdinalIgnoreCase);
            }
            case "IfTargetWeaponTypeEquipped": // type Magic / Melee / Ranged: the main hand's attacktype
                return unit is Player pw && pw.Inventory.Equipment[0] is { } weapon
                    && Text(e, "type").Equals(weapon.Template.Attrs.GetValueOrDefault("attacktype"), StringComparison.OrdinalIgnoreCase);
            case "IfChannelTick": // 1-based tick of the channel the effect runs in (ROOTFIELD: tick 1 roots everyone); modulo first
            {
                if (f.ChannelTick <= 0) return false;
                var modulo = (int)SpellTemplate.Float(e, "modulo");
                return Compare(e, modulo > 0 ? f.ChannelTick % modulo : f.ChannelTick);
            }
            case "IfTotalNumberOfTargetsOnThreatList": // the caster mob's live threat entries (the boss spells that scale with the group)
                return Compare(e, f.Caster is Mob tm ? tm.Threat.Keys.Count(q => MobCasts.Valid(q, f.Map, f.Ticks)) : 0);
            case "IfIsTargetTopOfThreatList":
                return f.Caster is Mob top && top.Threat.Count > 0 && top.Threat.MaxBy(t => t.Value).Key == unit;
            case "IfIsTargetChanneling": // category: of the channelled spell (invisibility: the stealth channels)
            {
                var category = (string?)e.Attribute("category");
                SpellTemplate? channelled = unit switch
                {
                    Player { Combat.Channel: { Over: false } ch } => ch.Parent,
                    Mob { AI: BehaviourAI { Cast: { Finished: true, Over: false, Channel: not null } cast } } => cast.Spell,
                    _ => null,
                };
                return channelled is not null && (category is null || category.Equals(channelled.Category, StringComparison.OrdinalIgnoreCase));
            }
            case "IfTargetIsTransformed":
                return world is not null && unit.Effects.Has("transformation", world.GameNow);
            case "IfSummonedMobCount": // the caster's living summons
                return Compare(e, Summons.Of(f.Map, f.Caster).Count());
            case "IfVariableOnTarget": // a mob's AI variable (frostcall, TRDemonPrinceSpawn); no op: set and not false
            {
                if (unit is not Mob { AI: BehaviourAI ai } || world is null) return false;
                var value = ai.Var(world, Text(e, "name"));
                return Variable(e, value);
            }
            case "IfIsAnyMobWithVariableInRange":
            {
                if (world is null) return false;
                var range = SpellTemplate.Float(e, "range");
                var at = Position(f.Caster);
                return f.Map.Objects.Values.OfType<Mob>().Any(m => !m.Dead && m.AI is BehaviourAI ai && Vector2.Distance(m.Position, at) <= range
                    && Variable(e, ai.Var(world, Text(e, "name"))));
            }
            case "IfInDuel":
                return unit is Player pd && pd.World.Duels.Of(pd) is not null;
            case "IfEquipmentSlotEmpty":
            {
                if (unit is not Player ps) return false;
                var slot = Array.IndexOf(Inventory.SlotNames, Text(e, "slot"));
                return slot >= 0 && ps.Inventory.Equipment[slot] is null;
            }
            case "IfTargetIsInFaction": // the faction flag by its city's biome (highland 1, forest 2, steppe 3, mountain 4, swamp 5, caerleon 6)
                return unit is Player pf && pf.Flag == FactionFlag(Text(e, "faction"));
            case "IfTargetIsInAnyFaction":
                return unit is Player pa && Pvp.IsFaction(pa.Flag);
            case "IfCasterIsInAnyFaction":
                return f.Caster is Player pc && Pvp.IsFaction(pc.Flag);
            case "IfTargetIsInOpposingFaction":
                return f.Caster is Player me && unit is Player them && Pvp.IsFaction(me.Flag) && Pvp.IsFaction(them.Flag) && me.Flag != them.Flag;
            case "IfClusterBiome":
                return Text(e, "biome").Equals(f.Map.Template?.Name.Biome, StringComparison.OrdinalIgnoreCase);
            case "IfTargetCCEffect": // under that crowd control (stun, root, silence, …)
                return world is not null && unit.Effects.On(world.GameNow).Any(x => x.Sub.CcType == (string?)e.Attribute("type"));
            case "IfTargetPositionIsInPursuitRange": // the caster mob would follow the target there (its pursuitradius around home)
                return f.Caster is Mob pm && Vector2.Distance(Position(unit), pm.Home) <= Math.Max(pm.Template.PursuitRadius, pm.Template.AggroRadius);
            case "IfTargetMobIsMoving":
                return unit is Mob mm && mm.Motion.Moving;
            case "IfIsTargetCarryingObject" or "IfTargetHasPowerCrystal": // no carriable objects (battle standards, power cores, crystals) on this server
                return false;
            default:
                return null;
        }
    }

    /// <summary>
    /// The value of attribute <paramref name="name"/> of effect node <paramref name="node"/> with its &lt;valueoverride&gt; applied:
    /// IfCharge chargespell (who, casterchargesonly) picks the &lt;change&gt; with the highest mincharges the unit's stacks of that
    /// spell reach (AXETHROW_EFFECT −160 → −190/−220/−250 with 1-3 AXE_BLEED stacks); none reached, no override: the node's own value.
    /// </summary>
    public static float Value(XElement node, string name, Facts f, float fallback = 0)
    {
        var value = SpellTemplate.Float(node, name, fallback);
        if (node.Element("valueoverride") is not { } over) return value;
        foreach (var charge in over.Elements("IfCharge"))
        {
            var unit = Who(charge, f);
            var spell = (string?)charge.Attribute("chargespell");
            var mine = SpellTemplate.Bool(charge, "casterchargesonly");
            var now = f.Ticks;
            var stacks = unit.Effects.Spells.Where(s => s.Spell.Name == spell && (!mine || s.Caster == f.Caster.Id) && s.Live(now)).Sum(s => s.Stacks);
            // The node's main number (change, value) is written as value="…"; any other attribute (time) under its own name.
            var key = name is "change" ? "value" : name;
            var best = charge.Elements("change").Where(c => c.Attribute(key) is not null && SpellTemplate.Float(c, "mincharges", 1) <= stacks)
                .MaxBy(c => SpellTemplate.Float(c, "mincharges", 1));
            if (best is not null) value = SpellTemplate.Float(best, key, value);
        }
        return value;
    }

    private static string Text(XElement e, string name) => (string?)e.Attribute(name) ?? "";

    private static string Op(XElement e) => ((string?)e.Attribute("op") ?? "").ToLowerInvariant();

    private static bool Variable(XElement e, string value)
    {
        var wanted = (string?)e.Attribute("value");
        return Op(e) switch
        {
            "" => value.Length > 0 && !value.Equals("false", StringComparison.OrdinalIgnoreCase) && value != "0",
            "contains" => wanted is not null && value.Contains(wanted, StringComparison.OrdinalIgnoreCase),
            "notequals" or "notequal" => !value.Equals(wanted ?? "", StringComparison.OrdinalIgnoreCase),
            _ => value.Equals(wanted ?? "", StringComparison.OrdinalIgnoreCase),
        };
    }

    private static Vector2 Position(IUnit unit) => unit switch
    {
        Player p => p.PositionAt(DateTime.UtcNow),
        Mob m => m.Position,
        _ => default,
    };

    private static double Share(IUnit unit, long ticks, bool energy) => unit switch
    {
        Player p => energy ? Ratio(p.Energy.At(ticks), p.Energy.Max) : Ratio(p.Health.At(ticks), p.Health.Max),
        Mob m => energy ? Ratio(m.Energy.At(ticks), m.Energy.Max) : Ratio(m.Health.At(ticks), m.Health.Max),
        _ => 0,
    };

    private static double Ratio(double v, double max) => max > 0 ? v / max : 0;

    /// <summary>The FlaggingStatus of a faction name (MapSpawner.CityFaction's numbering); 0 = none.</summary>
    public static byte FactionFlag(string faction) => faction.ToLowerInvariant() switch
    {
        "highland" => 1, "forest" => 2, "steppe" => 3, "mountain" => 4, "swamp" => 5, "caerleon" => 6, _ => 0,
    };

    /// <summary><paramref name="v"/> against the condition's value with its op (less, lessOrEqual, greater, greaterOrEqual, equal(s), notequal(s)).</summary>
    public static bool Compare(XElement e, double v)
    {
        var value = SpellTemplate.Float(e, "value");
        return Op(e) switch
        {
            "less" => v < value, "lessorequal" => v <= value, "greater" => v > value, "greaterorequal" => v >= value,
            "equal" or "equals" => Math.Abs(v - value) < 0.01, "notequal" or "notequals" => Math.Abs(v - value) >= 0.01, _ => false,
        };
    }
}
