using System.Numerics;
using System.Xml.Linq;
using AlbionLocal.Server.Data;
using AlbionLocal.Server.Entities;


namespace AlbionLocal.Server.Systems;

/// <summary>
/// The timed effects that watch their unit while they last (analysis/work/stageR/spell-effects/SPEC.md §3). The client only parses them
/// (bj7, blc, bk4 in Albion.Common: attributes, no logic), so what they do is read from their attributes [infer]:
/// <list type="bullet">
/// <item><b>castspellafterdistancemoved</b> spell castafterdistancemoved castspellintervalnotenoughmovement firstcastonapply (HUMAN_TORCH on
/// the infernal staff, FROSTWALK and DEMONWALK on the keeper and Morgana cloth shoes, AFTER_IMAGE on the fey leather shoes): each time the
/// unit that carries it has moved castafterdistancemoved metres since the last cast, the spell is cast at it by the effect's caster; with
/// castspellintervalnotenoughmovement &gt; 0 it is cast also when the unit stood that long without moving far enough (0: only by
/// distance, AFTER_IMAGE's 32 m); firstcastonapply casts it once right after the application. Checked every <see cref="Step"/> [assume].</item>
/// <item><b>targetsinarea</b> areatargettype radius radiusleavethreshold applyspellwhentargetsinarea applyspellwhennotargetsinarea
/// rechecktargetsinintervals (SPEEDARCHER_KITE_BUFF of the bow, PASSIVE_PANTHER_INVIS_TRIGGER_EFFECT of the panther form): whether units of
/// areatargettype stand within radius of the carrier (radius + radiusleavethreshold once they did); at the application and at each change
/// of that answer the matching spell runs on the carrier (SPEEDARCHER_KITE_ENEMY_DEBUFF and PASSIVE_PANTHER_INVIS_BREAK are self spells).
/// Rechecked every <see cref="RecheckStep"/> [assume] while rechecktargetsinintervals. A player carrier only (the class is read for it,
/// <see cref="SpellTargets.Of"/>); onlycountvisibleplayers is not read (every unit counts) [assume].</item>
/// <item><b>removeeffectoneventactive</b> event="EffectRemoved_TerminatedPrematurely" eventtriggereffectname removespelleffectnames
/// (SHIELDFRIENDLY of the arcane staff, the ent and territory guard shields, SPAWN_INVULNERABILITY_HIDEOUT): when a sub-effect of the same
/// entry whose effectname is eventtriggereffectname ends early (a damage shield used up, an effect broken by attacking, casting, damage or
/// a move), the sub-effects of that entry named in removespelleffectnames end too (<see cref="Terminated"/>).</item>
/// </list>
/// Every cast goes through <see cref="SpellCasts.Execute"/> (its targeting rules, damage and heal paths). Under <see cref="World.Sync"/>.
/// </summary>
public static class SpellWatchers
{
    /// <summary>How often castspellafterdistancemoved looks at its unit [assume].</summary>
    public const double Step = 0.2;

    /// <summary>How often targetsinarea looks again with rechecktargetsinintervals [assume].</summary>
    public const double RecheckStep = 0.5;

    /// <summary>The tags this class starts at an application.</summary>
    public static bool Watches(string tag) => tag is "castspellafterdistancemoved" or "targetsinarea";

    /// <summary>
    /// <paramref name="entry"/> was just applied (or restarted) on <paramref name="unit"/>: its Active watching sub-effects start. Their first
    /// look is a timer due now, so nothing runs inside the Execute that applied it.
    /// </summary>
    public static void Started(World world, IUnit unit, ActiveSpell entry)
    {
        if (unit.Map is not { } map) return;
        foreach (var sub in entry.Subs)
        {
            if (!sub.Active || !Watches(sub.Tag)) continue;
            var generation = ++sub.Generation; // a re-application starts a new watch: the older one stops
            if (sub.Tag == "castspellafterdistancemoved") Distance(world, map, unit, entry, sub, generation);
            else Area(world, map, unit, entry, sub, generation);
        }
    }

    /// <summary>Whether the watch of <paramref name="sub"/> still runs: same generation, its entry on the unit and in force, the unit on <paramref name="map"/>.</summary>
    private static bool Alive(World world, Map map, IUnit unit, ActiveSpell entry, SubEffect sub, int generation) =>
        sub.Generation == generation && !entry.Cancelled && unit.Map == map && unit.Effects.Contains(entry) && entry.On(sub, world.GameNow)
        && unit is not Mob { Dead: true };

    // ---- castspellafterdistancemoved ----

    private static void Distance(World world, Map map, IUnit unit, ActiveSpell entry, SubEffect sub, int generation)
    {
        var node = sub.Node;
        if (world.Spells?.Template((string?)node.Attribute("spell")) is not { } spell) return;
        var distance = SpellTemplate.Float(node, "castafterdistancemoved");
        var idle = SpellCasts.Seconds(SpellTemplate.Float(node, "castspellintervalnotenoughmovement"));
        var anchor = SpellMovement.PositionOf(unit, world.GameNow);
        var last = world.GameNow;
        void Look()
        {
            if (!Alive(world, map, unit, entry, sub, generation)) return;
            var now = world.GameNow;
            var at = SpellMovement.PositionOf(unit, now);
            if (distance > 0 && Vector2.Distance(at, anchor) >= distance || idle > 0 && now - last >= idle)
            {
                anchor = at;
                last = now;
                Cast(world, map, unit, entry, spell, now);
            }
            map.Timers.At(world.ClockAt(now + SpellCasts.Seconds(Step)), Look);
        }
        if (SpellTemplate.Bool(node, "firstcastonapply"))
            map.Timers.At(world.Clock.Now, () =>
            {
                if (!Alive(world, map, unit, entry, sub, generation)) return;
                anchor = SpellMovement.PositionOf(unit, world.GameNow);
                last = world.GameNow;
                Cast(world, map, unit, entry, spell, world.GameNow);
            });
        map.Timers.At(world.ClockAt(world.GameNow + SpellCasts.Seconds(Step)), Look);
    }

    /// <summary>
    /// <paramref name="spell"/> by the entry's caster (a player on the map; a mob's entry casts nothing [assume]) at <paramref name="unit"/>: the
    /// unit as the target, or no target when it is the caster itself, with the entry's ability powers.
    /// </summary>
    private static void Cast(World world, Map map, IUnit unit, ActiveSpell entry, SpellTemplate spell, long now)
    {
        if (map.FindPlayer(entry.Caster) is not { } caster || caster.Map != map) return;
        var target = unit == caster ? null : unit;
        SpellCasts.Execute(new SpellCasts.SpellContext(caster, map, entry.Spell, 0, entry.Ap, target, SpellMovement.PositionOf(unit, now), now)
            { CcAbilityPower = entry.CcAp, Depth = 1 }, spell);
    }

    // ---- targetsinarea ----

    private static void Area(World world, Map map, IUnit unit, ActiveSpell entry, SubEffect sub, int generation)
    {
        if (unit is not Player carrier) return;
        var node = sub.Node;
        var cls = SpellTargets.Parse((string?)node.Attribute("areatargettype"));
        var radius = SpellTemplate.Float(node, "radius");
        var leave = SpellTemplate.Float(node, "radiusleavethreshold");
        var recheck = SpellTemplate.Bool(node, "rechecktargetsinintervals");
        var whenIn = world.Spells?.Template((string?)node.Attribute("applyspellwhentargetsinarea"));
        var whenOut = world.Spells?.Template((string?)node.Attribute("applyspellwhennotargetsinarea"));
        bool? inside = null;
        void Look()
        {
            if (!Alive(world, map, unit, entry, sub, generation)) return;
            var now = world.GameNow;
            var any = AnyIn(map, carrier, cls, radius + (inside == true ? leave : 0), now);
            if (any != inside)
            {
                inside = any;
                if ((any ? whenIn : whenOut) is { } spell)
                    SpellCasts.Execute(new SpellCasts.SpellContext(carrier, map, entry.Spell, 0, entry.Ap, null, SpellMovement.PositionOf(carrier, now), now)
                        { CcAbilityPower = entry.CcAp, Depth = 1 }, spell);
            }
            if (recheck) map.Timers.At(world.ClockAt(now + SpellCasts.Seconds(RecheckStep)), Look);
        }
        map.Timers.At(world.Clock.Now, Look);
    }

    /// <summary>Whether a unit other than <paramref name="carrier"/> that matches <paramref name="cls"/> for it stands within <paramref name="radius"/> of it.</summary>
    public static bool AnyIn(Map map, Player carrier, SpellTargets.Cls cls, float radius, long now)
    {
        var at = SpellMovement.PositionOf(carrier, now);
        foreach (var m in map.Objects.Values.OfType<Mob>())
            if (!m.Dead && Vector2.Distance(m.Position, at) <= radius + m.Template.CollisionRadius && SpellTargets.Effect(cls, SpellTargets.Of(carrier, m, now)))
                return true;
        foreach (var q in map.Characters)
            if (q != carrier && Vector2.Distance(SpellMovement.PositionOf(q, now), at) <= radius && SpellTargets.Effect(cls, SpellTargets.Of(carrier, q, now)))
                return true;
        return false;
    }

    // ---- removeeffectoneventactive ----

    /// <summary>
    /// <paramref name="ended"/> of <paramref name="entry"/> on a unit ended before its time: the removeeffectoneventactive sub-effects of the
    /// entry in force for EffectRemoved_TerminatedPrematurely of its effectname switch off the sub-effects named in their removespelleffectnames,
    /// which may set off the next ones (MOB_TRACKING_ENT_CHANNEL_SHIELD: CCIMMUNITY ↔ SHIELD). Returns whether any other sub-effect ended; the
    /// caller sends the list and works the stats out.
    /// </summary>
    public static bool Terminated(ActiveSpell entry, SubEffect ended, long now)
    {
        var changed = false;
        var queue = new Queue<string>();
        if ((string?)ended.Node.Attribute("effectname") is { Length: > 0 } first) queue.Enqueue(first);
        var seen = new HashSet<string>(StringComparer.Ordinal);
        while (queue.Count > 0)
        {
            var name = queue.Dequeue();
            if (!seen.Add(name)) continue;
            foreach (var rule in entry.Subs)
            {
                if (rule.Tag != "removeeffectoneventactive" || !entry.On(rule, now)) continue;
                var node = rule.Node;
                if ((string?)node.Attribute("event") != "EffectRemoved_TerminatedPrematurely" || (string?)node.Attribute("eventtriggereffectname") != name) continue;
                foreach (var target in Names(node))
                    foreach (var sub in entry.Subs)
                        if (entry.On(sub, now) && (string?)sub.Node.Attribute("effectname") == target)
                        {
                            sub.Active = false;
                            changed = true;
                            queue.Enqueue(target);
                        }
            }
        }
        return changed;
    }

    private static IEnumerable<string> Names(XElement node) =>
        ((string?)node.Attribute("removespelleffectnames") ?? "").Split([',', ' ', ';'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
}
