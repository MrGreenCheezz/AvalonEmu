using System.Globalization;
using System.Numerics;
using System.Xml.Linq;
using AlbionLocal.Photon;
using AlbionLocal.Server.Data;
using AlbionLocal.Server.Entities;
using AlbionLocal.Server.Protocol;

namespace AlbionLocal.Server.Systems;

/// <summary>What a mob's cast tells its AI (<see cref="AI.BehaviourAI"/>): the node events and the end of the cast or its channel.</summary>
public interface IMobCaster
{
    /// <summary>Spell event <paramref name="name"/> of the cast's node (onCastStart, onCastSuccessful, onCastInterrupted, onChannelingHit; SPEC R9).</summary>
    void OnCastEvent(MobCast cast, string name);

    /// <summary>The cast has finished (CastFinished sent, its channel started if it has one): cooldowns and the stand time.</summary>
    void OnCastFinished(MobCast cast);

    /// <summary>The cast is over: finished without a channel, its channel ended, or interrupted.</summary>
    void OnCastOver(MobCast cast);
}

/// <summary>
/// A mob's cast (stageD mobspells SPEC §3, §4.3): the spell, its AI node and slot, the cast's counter, its target (a player, the
/// mob itself, or none for a ground spell) and point, start and end, the energy it costs, and once it has finished its channel.
/// Touched only under <see cref="World.Sync"/>.
/// </summary>
public sealed class MobCast
{
    public required Mob Caster { get; init; }

    public required SpellTemplate Spell { get; init; }

    public AiSpell? Node { get; init; }

    public byte Slot { get; init; }

    public byte Counter { get; init; }

    /// <summary>The unit it is cast on: a player (enemy spells), the mob (self spells); null for a ground spell.</summary>
    public IUnit? Target { get; init; }

    /// <summary>CastStart key 2: the ground point, else the mob's position.</summary>
    public Vector2 Point { get; init; }

    public bool Ground => Spell.Target == "ground";

    /// <summary>
    /// CastStart / CastSpell key 2: the ground point of a ground spell; left out for a spell at a unit (the captured ghoul bite, HOWL:
    /// key 7 only). Sending the mob's own position there made the client draw the telegraph from the mob towards itself, i.e. along
    /// its stale facing, while the shot flew at the player (stageN spells-bosses, the user's report).
    /// </summary>
    public System.Numerics.Vector2 WirePoint => Ground ? Point : default;

    public long Start { get; init; }

    public long End { get; init; }

    public int Cost { get; init; }

    /// <summary>The mob's abilitypower: the damage and energy scaling, ActiveSpellEffectsUpdate keys 2 and 3.</summary>
    public double Ap { get; init; }

    public IMobCaster? Owner { get; init; }

    /// <summary>CastFinished went out.</summary>
    public bool Finished { get; internal set; }

    /// <summary>Over: no tick, hit or end of it runs any more (it ended, was cancelled, or the mob died or reset).</summary>
    public bool Over { get; internal set; }

    /// <summary>Its channel spell (the next id) and when the channel ends; null without one.</summary>
    public SpellTemplate? Channel { get; internal set; }

    public long? ChannelEnd { get; internal set; }

    /// <summary>The areas linked to its channel (linktochanneling): they leave with it.</summary>
    internal List<MobArea> Areas { get; } = [];

    public override string ToString() => $"{Spell.Name} #{Counter}";
}

/// <summary>
/// The area a mob's spelleffectarea makes (stageD mobspells SPEC §3 NewSpellEffectArea row, §4.4 item 6): as the players'
/// <see cref="SpellArea"/>, with the mob as owner and the players as its targets. Touched only under <see cref="World.Sync"/>.
/// </summary>
public sealed class MobArea(long id, ClusterObject spawn) : WorldObject(id, spawn)
{
    public required Mob Owner { get; init; }

    /// <summary>Key 4: the spell that holds the node (a channel's area: the _CHANNELING spell).</summary>
    public required SpellTemplate Holder { get; init; }

    public required byte Index { get; init; }

    public required XElement Node { get; init; }

    public SpellTemplate? Effect { get; init; }

    public required Vector2 Origin { get; init; }

    public required IReadOnlyList<SpellAreas.ShapeNode> Shapes { get; init; }

    public required float Reach { get; init; }

    public required float Yaw { get; init; }

    public required long Start { get; init; }

    /// <summary>Key 7: start + time; a channel's area: its planned end (it leaves with the channel).</summary>
    public required long End { get; init; }

    public required byte Counter { get; init; }

    public required double Ap { get; init; }

    /// <summary>The cast whose channel it is linked to; null = not linked.</summary>
    public MobCast? Channel { get; init; }

    internal HashSet<long> InsideLast { get; } = [];

    internal HashSet<long> Applied { get; } = [];

    internal int Hits { get; set; }

    public bool Gone { get; internal set; }

    public override Vector2 Position => Origin;

    public override float Direction => Yaw;

    public override string Kind => "mob spell area";

    public override string TypeName => $"{Holder.Name}[{Index}]";

    public override bool LeaveOnRange => true;

    /// <summary>NewSpellEffectArea (113): 0 id, 1 the mob, 2 centre, 3 direction (left out at 0), 4 holder, 5 index (also 0), 6 start, 7 end, 8 the cast's counter, 9 byte 0.</summary>
    public override IEnumerable<AlbionEvent> BuildCreate(Player viewer)
    {
        yield return new NewSpellEffectAreaEvent
        {
            da6 = Id, da7 = Owner.Id, da8 = Origin, da9 = Yaw, dba = Holder.Id, dbb = Index, dbc = Start, dbd = End, dbe = Counter,
            Extra = new() { [5] = Index, [9] = (byte)0 },
        };
    }
}

/// <summary>
/// The cast pipeline and effect runner of mob spells (analysis/work/stageD/mobspells/SPEC.md §3, §4.4; ARCHITECTURE.md §6.4),
/// in the captured wire format, the same for every observer: CastStart {0 mob, 1 start, 2 point, 4 end, 5 spell, 6 -1, 7 target
/// (absent for ground spells; the mob for self spells), 8 counter, 9 0}; at the end EnergyUpdate {2 -cost (left out at 0), 3, 4 7,
/// 5 mob, 6 spell}, CastFinished {1 target, 2 spell, 3 slot, 4 counter, 5 start + castingtime; no key 6} and CastSpell {1 target,
/// 2 point, 3 spell, 4 end, 5 hit, 6 counter, 7 0}; the hit hitdelay (+ the projectile's flight) later; a channel's ticks (CastSpell
/// of the _CHANNELING spell, its effects, the self CastHits) while before its end, then ChannelingEnded {1 3, 2 counter}.
/// The effects run as the players' runner does (<see cref="SpellCasts.Execute"/>, stageD effects batch 1, whose list, crowd
/// control, buff scaling and area shapes this shares: <see cref="SpellEffects"/>, <see cref="SpellAreas.Inside(IReadOnlyList{SpellAreas.ShapeNode}, float, Vector2, float, long, Vector2, float, long)"/>),
/// in the mob → player direction: first the timed sub-effects into the lists (ActiveSpellEffectsUpdate), then the CastHits
/// (target, then self), then the instant effects in document order: directattributechange (damage on players through
/// <see cref="Combat.DamagePlayer"/>, heals on the mob; effectarearadius: every player within it, origin 3),
/// attributechangeovertime, applyspell, pulsingspell, removeactivespell / removeactiveeffects, spelleffectarea and aura.
/// Every other tag is a no-op logged once. Touched only under <see cref="World.Sync"/>.
/// </summary>
public static class MobCasts
{
    // ---- the cast ----

    /// <summary>The players' and mobs' valid targets: on the map, alive, not knocked down or invulnerable, selectable.</summary>
    public static bool Valid(Player p, Map map, long now) =>
        p.Map == map && !p.Combat.Untouchable && p.Health.At(now) > 0 && !SpellEffects.NotTargetable(p, now);

    /// <summary>
    /// Starts <paramref name="cast"/>: CastStart to the mob's observers; an instant spell finishes at once (the four events in one
    /// batch, SPEC §3), one with a cast time at its end (a map timer).
    /// </summary>
    public static void Start(World world, Map map, MobCast cast)
    {
        var mob = cast.Caster;
        mob.SendAround(new CastStartEvent
        {
            ddn = mob.Id, bwb = cast.Start, bwc = cast.WirePoint, bwe = cast.End, bwf = cast.Spell.Id, bwg = -1, bwh = cast.Ground ? 0 : cast.Target?.Id ?? mob.Id,
            Extra = new() { [8] = cast.Counter, [9] = (byte)0 },
        });
        Log.Debug("ai", $"{mob} casts {cast.Spell} #{cast.Counter}{(cast.Target is { } t && t != mob ? $" on #{t.Id}" : "")}");
        cast.Owner?.OnCastEvent(cast, "onCastStart");
        if (cast.End <= cast.Start)
        {
            Finish(world, map, cast);
            return;
        }
        SpellCasts.At(world, map, cast.End, () =>
        {
            if (!cast.Over && mob.Map == map && !mob.Dead && !mob.Removed) Finish(world, map, cast); // a despawned mob (.mob kill) casts no more
        });
    }

    /// <summary>
    /// The end of <paramref name="cast"/> (SPEC §3): an enemy spell whose target is gone is interrupted (CastCancel {1 true});
    /// otherwise the energy is charged and EnergyUpdate, CastFinished and CastSpell go out, the hit is scheduled and the channel starts.
    /// </summary>
    private static void Finish(World world, Map map, MobCast cast)
    {
        var mob = cast.Caster;
        if (!cast.Ground && cast.Target is Player p && !Valid(p, map, cast.End))
        {
            Cancel(cast, interrupted: true);
            cast.Owner?.OnCastEvent(cast, "onCastInterrupted");
            cast.Owner?.OnCastOver(cast);
            return;
        }
        cast.Finished = true;
        var spell = cast.Spell;
        var energy = Math.Max(0, mob.Energy.At(cast.End) - cast.Cost);
        mob.Energy.Set(energy, Math.Max(cast.End, mob.Energy.Stamp));
        mob.SendAround(new EnergyUpdateEvent { ddn = mob.Id, b5b = cast.End, b5c = -cast.Cost, b5d = energy, b5e = Combat.EnergyCast, b5f = mob.Id, b5g = spell.Id });
        var targetId = cast.Ground ? 0 : cast.Target?.Id ?? mob.Id;
        var aim = cast.Ground ? cast.Point : PositionOf(cast.Target) ?? mob.Position;
        var flight = spell.ProjectileSpeed is { } speed ? Vector2.Distance(mob.Position, aim) / speed : 0;
        var hit = cast.End + SpellCasts.Seconds(spell.HitDelay + flight);
        mob.SendAround(new CastFinishedEvent { ddn = mob.Id, bux = targetId, buy = spell.Id, bu1 = cast.End, Extra = new() { [3] = cast.Slot, [4] = cast.Counter } });
        mob.SendAround(new CastSpellEvent
        {
            ddn = mob.Id, bvw = targetId, bvx = cast.WirePoint, bvy = spell.Id, bvz = cast.End, bv0 = hit, Extra = new() { [6] = cast.Counter, [7] = (short)0 },
        });
        cast.Owner?.OnCastEvent(cast, "onCastSuccessful");
        SpellCasts.At(world, map, hit, () =>
        {
            if (mob.Map != map || mob.Dead || mob.Removed) return;
            var target = cast.Ground ? null : cast.Target;
            Execute(new MobHit(mob, map, spell, cast.Counter, cast.Ap, target, cast.Ground ? cast.Point : PositionOf(target) ?? mob.Position, hit) { Cast = cast }, spell);
        });
        StartChannel(world, map, cast);
        cast.Owner?.OnCastFinished(cast);
        if (cast.Channel is null)
        {
            cast.Over = true;
            cast.Owner?.OnCastOver(cast);
        }
    }

    /// <summary>
    /// Drops <paramref name="cast"/>: before its end CastCancel {0 mob, 1 true when <paramref name="interrupted"/>, 2 counter, 9 0}
    /// (a reset or death: without key 1, as captured), during its channel ChannelingEnded {1 2} (SPEC §9 Q7: not captured); its
    /// channel's areas leave.
    /// </summary>
    public static void Cancel(MobCast cast, bool interrupted)
    {
        if (cast.Over) return;
        cast.Over = true;
        var mob = cast.Caster;
        if (!cast.Finished) mob.SendAround(new CastCancelEvent { ddn = mob.Id, buo = interrupted, bup = cast.Counter, Extra = new() { [9] = (byte)0 } });
        else if (cast.Channel is not null) mob.SendAround(new ChannelingEndedEvent { ddn = mob.Id, bwu = 2, bwv = cast.Counter });
        foreach (var area in cast.Areas.ToList()) Remove(area);
    }

    /// <summary>
    /// The channel of a finished cast whose spell has one (MOB_UNDEAD_SOLDIER_CHARGED_FRENZY): channelEnd = end +
    /// initialeffectinterval + (effectcount − 1) × effectinterval; ticks at end + initialeffectinterval + k × effectinterval while
    /// before channelEnd (all 3 captured frenzies had 3 ticks, SPEC §3, Q14), each a CastSpell of the channel spell and its effects;
    /// at channelEnd ChannelingEnded {0 mob, 1 3, 2 counter} with no tick, and its linked areas leave in the same batch.
    /// </summary>
    private static void StartChannel(World world, Map map, MobCast cast)
    {
        if (!cast.Spell.HasChannel || world.Spells?.Template(cast.Spell.Id + 1) is not { Channel: { } info } channel) return;
        var end = cast.End + SpellCasts.Seconds(info.Seconds);
        cast.Channel = channel;
        cast.ChannelEnd = end;
        var mob = cast.Caster;
        for (var k = 0; k < info.Count; k++)
        {
            var ticks = cast.End + SpellCasts.Seconds(info.Initial + k * info.Interval);
            if (ticks >= end && info.Count > 1) break;
            var first = k == 0;
            var tick = k + 1; // IfChannelTick counts from 1
            SpellCasts.At(world, map, ticks, () =>
            {
                if (cast.Over || mob.Map != map || mob.Dead || mob.Removed) return;
                mob.SendAround(new CastSpellEvent { ddn = mob.Id, bvx = cast.WirePoint, bvy = channel.Id, bvz = ticks, bv0 = ticks, Extra = new() { [6] = cast.Counter, [7] = (short)0 } });
                var target = cast.Ground ? null : cast.Target;
                Execute(new MobHit(mob, map, channel, cast.Counter, cast.Ap, target, cast.Ground ? cast.Point : PositionOf(target) ?? mob.Position, ticks)
                    { Cast = cast, ListHits = true, FirstTick = first, ChannelTick = tick }, channel);
                cast.Owner?.OnCastEvent(cast, "onChannelingHit");
            });
        }
        SpellCasts.At(world, map, end, () =>
        {
            if (cast.Over || mob.Map != map || mob.Dead || mob.Removed) return;
            cast.Over = true;
            mob.SendAround(new ChannelingEndedEvent { ddn = mob.Id, bwu = 3, bwv = cast.Counter });
            foreach (var area in cast.Areas.ToList()) Remove(area);
            cast.Owner?.OnCastOver(cast);
        });
    }

    /// <summary>The current position of a unit; null for none.</summary>
    private static Vector2? PositionOf(IUnit? unit) => unit switch
    {
        Player p => p.PositionAt(DateTime.UtcNow),
        Mob m => m.Position,
        _ => null,
    };

    // ---- the effect tree ----

    /// <summary>
    /// What a mob spell's effects run with: the mob, the root spell (whose own CastHits carry <see cref="Counter"/>; every spell it
    /// triggers carries 0), the ability power, the unit it acts on (null: the ground) and point, and the effect time.
    /// </summary>
    public sealed record MobHit(Mob Caster, Map Map, SpellTemplate Root, byte Counter, double Ap, IUnit? Target, Vector2 Point, long Ticks)
    {
        public int Depth { get; init; }

        /// <summary>The cast (its channel links the areas); null for an area's or aura's hit.</summary>
        public MobCast? Cast { get; init; }

        /// <summary>
        /// A channel tick or an area's hit: one CastHits per unit listing every spell that reached it ({2 [channel, sub-spell…],
        /// 4 [counter, 0…]}); otherwise one CastHit per unit with the executed spell.
        /// </summary>
        public bool ListHits { get; init; }

        /// <summary>The first tick of a channel: its linked areas are made once.</summary>
        public bool FirstTick { get; init; }

        /// <summary>The channel tick, from 1 (IfChannelTick); 0 = not a tick.</summary>
        public int ChannelTick { get; init; }

        /// <summary>Where a knockback pushes away from: an area's origin; null = the mob.</summary>
        public Vector2? Source { get; init; }

        public byte CounterOf(SpellTemplate spell) => spell == Root ? Counter : (byte)0;

        public World World => Caster.World!;
    }

    /// <summary>The decisions of one <see cref="Execute"/>, as the players' runner keeps them: conditions once per node and unit, the listings, who was reached.</summary>
    private sealed class Plan
    {
        public readonly Dictionary<(XElement Node, long Unit), bool> Holds = [];
        public readonly List<(MobHit Context, SpellTemplate Spell, IUnit Unit, SubEffect?[] Subs)> Applications = [];
        public readonly List<(IUnit Unit, SpellTemplate Spell, int Order)> Reached = [];
        public readonly HashSet<(short Spell, long Unit)> Blocked = [];
        public int Order;
    }

    /// <summary>
    /// Runs <paramref name="spell"/> with <paramref name="c"/>: the listings (one ActiveSpellEffectsUpdate per changed unit), the
    /// CastHit(s) per reached unit in the order reached (the target before the mob itself, SPEC §3), then the instant effects.
    /// </summary>
    public static void Execute(MobHit c, SpellTemplate spell)
    {
        if (c.Depth > 8 || c.Caster.World is not { } world) return;
        var plan = new Plan();
        Collect(c, spell, plan);
        var changed = new List<IUnit>();
        var entries = new List<(MobHit Context, ActiveSpell Entry, SubEffect?[] Subs, IUnit Unit)>();
        foreach (var (ctx, s, unit, subs) in plan.Applications)
        {
            entries.Add((ctx, SpellEffects.Apply(world, unit, s, c.Caster.Id, ctx.Ap, ctx.Ap, ctx.Ticks, subs), subs, unit));
            if (!changed.Contains(unit)) changed.Add(unit);
        }
        // StageN spells-bosses: the knockbacks, pulls and fears on the players go out as ForcedMovement before the lists, as a player's.
        foreach (var (ctx, entry, subs, unit) in entries)
            for (var i = 0; i < subs.Length; i++)
                if (subs[i] is { Tag: "knockback" or "pull" or "forcedmovement" } && unit != ctx.Caster)
                    SpellMovement.Push(world, ctx.Caster, entry.Spell, entry.Subs[i], unit, ctx.Source ?? ctx.Caster.Position, ctx.Ticks, ctx.ListHits);
        foreach (var u in changed)
        {
            SpellEffects.Send(world, u);
            SpellEffects.Refresh(world, u);
        }
        foreach (var (ctx, entry, subs, unit) in entries)
            for (var i = 0; i < subs.Length; i++)
                if (subs[i]?.Tag == "aura") Aura(ctx, entry, entry.Subs[i], unit);
        foreach (var group in plan.Reached.GroupBy(r => r.Unit))
        {
            var unit = group.Key;
            if (!c.ListHits)
            {
                unit.SendAround(Combat.CastHit(unit.Id, c.Caster.Id, spell.Id, c.CounterOf(spell)));
                continue;
            }
            var spells = group.OrderBy(r => r.Order).Select(r => r.Spell).Distinct().ToArray();
            unit.SendAround(spells.Length == 1
                ? Combat.CastHit(unit.Id, c.Caster.Id, spells[0].Id, c.CounterOf(spells[0]))
                : new CastHitsEvent
                {
                    ddn = unit.Id, bu7 = [.. spells.Select(_ => c.Caster.Id)], bu8 = [.. spells.Select(s => s.Id)], bu9 = [.. spells.Select(_ => (byte)1)],
                    bva = [.. spells.Select(c.CounterOf)],
                });
        }
        foreach (var (id, unitId) in plan.Blocked)
            if (UnitById(c, unitId) is { } unit)
                unit.SendAround(new CastHitEvent { ddn = unit.Id, bu3 = c.Caster.Id, bu4 = id, bu5 = 2, Extra = new() { [4] = (byte)0 } });
        Run(c, spell, plan);
    }

    private static IUnit? UnitById(MobHit c, long id) =>
        id == c.Caster.Id ? c.Caster : c.Map.Fighters.FirstOrDefault(p => p.Id == id) as IUnit ?? c.Map.Objects.GetValueOrDefault(id) as Mob;

    /// <summary>
    /// The first pass over <paramref name="spell"/>: the conditions, the timed sub-effects to list and the units reached (any effect
    /// on a unit but pulses and areas; an applyspell reaches what its spell reaches). Returns whether it reached anything.
    /// </summary>
    private static HashSet<long> Collect(MobHit c, SpellTemplate spell, Plan plan)
    {
        var reached = new HashSet<long>();
        if (c.Depth > 8) return reached;
        var order = plan.Order++;
        var world = c.World;
        var own = new Dictionary<long, SubEffect?[]>();
        for (var i = 0; i < spell.Effects.Count; i++)
        {
            var node = spell.Effects[i];
            var tag = node.Name.LocalName;
            if (tag is "spelleffectarea" or "nop" or "spawnmob" or "dash") continue;
            foreach (var unit in Targets(c, node))
            {
                if (!Holds(plan, c, node, unit)) continue;
                if (unit is Player && SpellEffects.Immune(unit, spell, c.Ticks))
                {
                    plan.Blocked.Add((spell.Id, unit.Id));
                    continue;
                }
                if (tag == "applyspell")
                {
                    if (Sub(c, node) is { } applied && Collect(Child(c, unit), applied, plan).Contains(unit.Id)) reached.Add(unit.Id);
                    continue;
                }
                if (tag != "pulsingspell") reached.Add(unit.Id);
                if (SpellEffects.Make(world, c.Caster, c.Ap, c.Ap, node, i, unit, c.Ticks) is not { } sub) continue;
                if (!own.TryGetValue(unit.Id, out var subs))
                {
                    own[unit.Id] = subs = new SubEffect?[spell.Effects.Count];
                    plan.Applications.Add((c, spell, unit, subs));
                }
                subs[i] = sub;
            }
        }
        foreach (var id in reached)
            if (UnitById(c, id) is { } unit) plan.Reached.Add((unit, spell, order));
        return reached;
    }

    /// <summary>The second pass: the instant effects in document order.</summary>
    private static void Run(MobHit c, SpellTemplate spell, Plan plan)
    {
        if (c.Depth > 8) return;
        for (var i = 0; i < spell.Effects.Count; i++)
        {
            var node = spell.Effects[i];
            var tag = node.Name.LocalName;
            if (tag == "spelleffectarea")
            {
                CreateArea(c, spell, node, i);
                continue;
            }
            if (tag == "dash") // stageN spells-bosses: the mob's own charge or leap, at its target or the ground point
            {
                if (Holds(plan, c, node, c.Caster)) Dash(c, spell, node, i);
                continue;
            }
            if (tag == "changeaggro") // stageN spells2: the mob's own threat (and its friends'), not per target unit
            {
                if (Holds(plan, c, node, c.Caster)) ChangeAggro(c, node);
                continue;
            }
            foreach (var unit in Targets(c, node))
            {
                if (!Holds(plan, c, node, unit) || plan.Blocked.Contains((spell.Id, unit.Id))) continue;
                switch (tag)
                {
                    case "directattributechange":
                        Change(c, spell, node, unit, SpellTemplate.Float(node, "effectarearadius") > 0 ? HitOrigin.Area : HitOrigin.Direct);
                        break;
                    case "attributechangeovertime":
                        OverTime(c, spell, node, i, unit);
                        break;
                    case "applyspell":
                        if (Sub(c, node) is { } applied) Run(Child(c, unit), applied, plan);
                        break;
                    case "pulsingspell":
                        if (Sub(c, node) is { } pulsed) Pulses(c, spell, node, pulsed, unit);
                        break;
                    case "removeactivespell" or "removeactiveeffects":
                        RemoveEffects(c, node, unit);
                        break;
                    case "nop":
                        break;
                    case "spawnmob": // stageD effects batch 3: the adds (Systems.Summons)
                        Summons.Spawn(c.World, c.Map, c.Caster, node, unit, c.Point, c.Ticks);
                        break;
                    // Batch 3: threat, chains, random picks and the mob's own areas.
                    case "removefromthreatlist":
                        if ((string?)node.Attribute("target") == "allplayers")
                            foreach (var p in c.Caster.Threat.Keys.ToList()) c.Caster.AI.Forget(p);
                        else if (unit is Player p && c.Caster.Threat.ContainsKey(p)) c.Caster.AI.Forget(p);
                        break;
                    case "chainspell":
                        Chain(c, node, unit);
                        break;
                    case "maketargettopthreat" when unit is Player top: // stageN spells-bosses: the boss turns on its target (× factor of the top threat)
                    {
                        var most = c.Caster.Threat.Count == 0 ? 0 : c.Caster.Threat.Values.Max();
                        c.Caster.AI.AddThreat(top, Math.Max(0, most * SpellTemplate.Float(node, "factor", 1) + 1 - c.Caster.Threat.GetValueOrDefault(top)));
                        if (c.Caster.Threat.ContainsKey(top)) c.Caster.TargetId = top.Id;
                        break;
                    }
                    case "interruptcast" when unit is Player cut: // stageN spells-bosses: a mob's interrupt ends the player's cast or channel
                        SpellCasts.InterruptPlayer(cut, c.Ticks, "interruptcast");
                        break;
                    case "applyrandomspell":
                        var options = node.Elements("applyspell").Where(e => e.Elements().Where(IsCondition).All(x => Holds(c, x, unit))).ToList();
                        if (options.Count > 0 && Sub(c, options[c.Map.Random.Next(options.Count)]) is { } picked) Execute(Child(c, unit), picked);
                        break;
                    case "removespelleffectareasoftarget":
                        if (unit == c.Caster) RemoveAreas(c.Caster, a => SpellAreas.Fits(a.Holder, a.Node, (string?)node.Attribute("spell"), (string?)node.Attribute("areatags")));
                        break;
                    case "removespelleffectareasaffectingtarget":
                        RemoveAffecting(c, node, unit);
                        break;
                    default:
                        if (SpellEffects.Listed.Contains(tag)) break; // listed in the first pass
                        Combat.LogOnce("mobtag:" + tag, $"mob spell effect <{tag}> is not supported yet: {spell.Name} skips it");
                        break;
                }
            }
        }
    }

    /// <summary>
    /// changeaggro of a mob's spell (stageN spells2, 42 uses): <c>value</c> threat (negative: taken off, never below 0) for players on mobs.
    /// The players: with effectarearadius the valid ones within it of the target (else the point), the nearest maxeffectareatargets of them;
    /// else the context's player. The mobs: the caster, and with aggroradius (not who="onlycaster") every living mob of its faction within
    /// it (the heretics' alarm traps call their camp). target friendother / self (the hellhounds' and the chests' alerts): the mobs of
    /// <c>faction</c> (self or missing: the caster's) within effectarearadius (else aggroradius), the caster too for self, take the
    /// value on the caster's threat list (else its target). An idle mob starts to fight (<see cref="AI.MobAI.AddThreat"/>) [inference: the
    /// tag's client class bjs has no server half].
    /// </summary>
    private static void ChangeAggro(MobHit c, XElement node)
    {
        var value = SpellTemplate.Float(node, "value");
        var target = ((string?)node.Attribute("target") ?? "enemy").ToLowerInvariant();
        var radius = SpellTemplate.Float(node, "effectarearadius");
        var aggro = SpellTemplate.Float(node, "aggroradius");
        var caster = c.Caster;
        var now = c.Ticks;
        var faction = (string?)node.Attribute("faction") is { Length: > 0 } f && !f.Equals("self", StringComparison.OrdinalIgnoreCase) ? f : caster.Template.Faction;
        bool Kin(Mob m) => !m.Dead && !m.Removed && m.Attackable && string.Equals(m.Template.Faction, faction, StringComparison.OrdinalIgnoreCase);
        List<Player> players;
        List<Mob> mobs;
        if (target is "friendother" or "self")
        {
            players = [.. caster.Threat.Keys.Where(p => Valid(p, c.Map, now))];
            if (players.Count == 0 && c.Target is Player t && Valid(t, c.Map, now)) players.Add(t);
            var reach = radius > 0 ? radius : aggro;
            mobs = [.. c.Map.Objects.Values.OfType<Mob>().Where(m => (m != caster || target == "self") && Kin(m)
                && Vector2.Distance(m.Position, caster.Position) <= reach + m.Template.CollisionRadius)];
        }
        else
        {
            if (radius > 0)
            {
                var center = PositionOf(c.Target) ?? c.Point;
                players = [.. c.Map.Fighters.Where(p => Valid(p, c.Map, now) && Vector2.Distance(p.PositionAt(DateTime.UtcNow), center) <= radius + c.World.CollisionRadius)
                    .OrderBy(p => Vector2.Distance(p.PositionAt(DateTime.UtcNow), center))];
                if (SpellTemplate.Float(node, "maxeffectareatargets") is > 0 and var most) players = [.. players.Take((int)most)];
            }
            else players = c.Target is Player p && Valid(p, c.Map, now) ? [p] : [];
            mobs = [caster];
            if (aggro > 0 && !string.Equals((string?)node.Attribute("who"), "onlycaster", StringComparison.OrdinalIgnoreCase))
                mobs.AddRange(c.Map.Objects.Values.OfType<Mob>().Where(m => m != caster && Kin(m) && Vector2.Distance(m.Position, caster.Position) <= aggro + m.Template.CollisionRadius));
        }
        foreach (var m in mobs)
            foreach (var p in players)
            {
                if (value < 0 && !m.Threat.ContainsKey(p)) continue; // nothing to take off: an idle mob is not pulled by it
                if (m.Attackable || m == caster) m.AI.AddThreat(p, value);
            }
    }

    /// <summary>A dash of the mob (<see cref="SpellMovement.MobDash"/>): to its target (not itself) or the point; the endeffect runs at the end point.</summary>
    private static void Dash(MobHit c, SpellTemplate spell, XElement node, int index)
    {
        var end = (string?)node.Attribute("endeffect") is { Length: > 0 } name ? c.World.Spells?.Template(name) : null;
        var target = c.Target is { } t && t != c.Caster ? t : null;
        SpellMovement.MobDash(c.World, c.Caster, spell, node, index, PositionOf(target) ?? c.Point, target is not null, c.Ticks,
            end is null ? null : (at, ticks) =>
            {
                if (!c.Caster.Dead && c.Caster.Map == c.Map) Execute(c with { Target = null, Point = at, Ticks = ticks, Depth = c.Depth + 1, Cast = null }, end);
            });
    }

    private static MobHit Child(MobHit c, IUnit unit) => c with { Target = unit, Point = PositionOf(unit) ?? c.Point, Depth = c.Depth + 1 };

    /// <summary>
    /// Who an effect node acts on: target "self" the mob; with effectarearadius every valid player within it (+ the character's
    /// collision radius) of the centre (the target, else the point: HOWL around the mob); "enemy" the context's player while it is
    /// valid; every other target (all, friend, …) the context's unit, else the mob.
    /// </summary>
    private static List<IUnit> Targets(MobHit c, XElement node)
    {
        var target = (string?)node.Attribute("target") ?? "";
        if (target == "self") return [c.Caster];
        var radius = SpellTemplate.Float(node, "effectarearadius");
        var now = c.Ticks;
        if (radius > 0 && target is "enemy" or "")
        {
            var center = PositionOf(c.Target) ?? c.Point;
            return [.. c.Map.Fighters.Where(p => Valid(p, c.Map, now)
                && Vector2.Distance(p.PositionAt(DateTime.UtcNow), center) <= radius + c.World.CollisionRadius)];
        }
        if (target == "enemy") return c.Target is Player p && Valid(p, c.Map, now) ? [p] : [];
        return [c.Target ?? c.Caster];
    }

    /// <summary>
    /// One directattributechange on <paramref name="unit"/>, × <paramref name="stacks"/>: damage on a player = change × AP / 100 ×
    /// the mob's damage bonuses (relativemax/current/missing: × that part of the player's health; absolute and
    /// ignoreabilitypowerscaling: the raw change) × its resistance against effecttype (magic when missing: FREEZE's HealthUpdate key 4
    /// is 2; ignorearmor: none); a relative energy drain on a player (relativemax: × its maximum energy); a heal on the mob = change × (AP / 100)^0.6634
    /// (bite: 0.05 relativemax, 19 on 385). Others are logged.
    /// </summary>
    private static void Change(MobHit c, SpellTemplate spell, XElement node, IUnit unit, HitOrigin origin, int stacks = 1)
    {
        var change = SpellConditions.Value(node, "change", Facts(c, unit)) * stacks;
        if (change == 0) return;
        var attribute = (string?)node.Attribute("attribute") ?? "";
        var changeType = (string?)node.Attribute("changetype") ?? "";
        var raw = SpellTemplate.Bool(node, "ignoreabilitypowerscaling") || changeType == "absolute";
        double Relative(double scaled, float current, float max) => changeType switch
        {
            "relativemax" => change * max,
            "relativecurrent" => change * current,
            "relativemissing" => change * Math.Max(0, max - current),
            _ => raw ? change : scaled,
        };
        if (attribute == "energy" && change < 0 && unit is Player drained && changeType.StartsWith("relative", StringComparison.Ordinal))
        {
            // An energy drain on a player (stageJ pvp-spells phase 3: UNDEAD_HARVESTER_ENERGY_DRAIN, SHADE_SPECTRALTOUCH_CHANNEL, the Avalon
            // drone's instability; −10 % of the maximum): EnergyUpdate {5 the mob} to it and its observers. The same guards as a mob's
            // hit (Combat.DamagePlayer: no bot, same map, not untouchable, invincible or nothittable, alive) and the player is in combat.
            // Flat drains (MOB_HERETIC_SHADOWMASK_INSANITY_EFFECT −5/−25/−125, whose health punishments wait for IfTargetEnergyPercentage)
            // stay logged below: whether their raw values scale with the mob's AP is not settled.
            if (drained.IsBot && !drained.IsCompanion || drained.Map is null || drained.Map != c.Caster.Map || drained.Combat.Untouchable || SpellEffects.Invincible(drained, c.Ticks)
                || SpellEffects.NotHittable(drained, c.Ticks) || drained.Health.At(c.Ticks) <= 0) return;
            Combat.EnterCombat(drained, c.Caster, hit: true);
            SpellCasts.DrainEnergy(drained, -Relative(change * c.Ap / 100, drained.Energy.At(c.Ticks), drained.Energy.Max), spell, c.Caster.Id, c.Ticks, origin);
            return;
        }
        if (attribute != "health")
        {
            Combat.LogOnce("mobattr:" + attribute, $"mob spell effect on attribute '{attribute}' is not supported yet: {spell.Name} skips it");
            return;
        }
        if (change < 0 && unit is Player p)
        {
            var type = (string?)node.Attribute("effecttype") == "physical" ? EffectType.Physical : EffectType.Magic;
            var value = Relative(Combat.SpellChange(change, c.Ap) * SpellEffects.DealtFactor(c.Caster, spell: true, type, vsPlayer: true, c.Ticks),
                p.Health.At(c.Ticks), p.Health.Max);
            var resistance = SpellTemplate.Bool(node, "ignorearmor") ? 1 : Combat.ResistanceOf(p, type);
            // reflectdamage="never" (321 nodes; stageJ pvp-spells phase 0): the player's reflectdamageactive does not answer this hit.
            Combat.DamagePlayer(c.Caster, p, -value * resistance, type, origin, spell.Id, c.Ticks, reflectable: (string?)node.Attribute("reflectdamage") != "never");
            return;
        }
        if (change > 0 && unit is Mob m)
        {
            var amount = Relative(Combat.Heal(change, c.Ap), m.Health.At(c.Ticks), m.Health.Max);
            if (SpellTemplate.Bool(node, "usehealingmodifiers", true)) amount *= SpellEffects.HealFactor(m, c.Ticks);
            Heal(m, amount, origin, spell.Id, c.Ticks);
            return;
        }
        Combat.LogOnce("mobchange:" + spell.Name, $"{spell.Name}: a mob's {(change < 0 ? "damage on a mob" : "heal on a player")} is not supported yet");
    }

    /// <summary>
    /// A heal of <paramref name="mob"/> by itself: up to the maximum, rounded; HealthUpdate {0 mob, 1 ticks, 2 +gain, 3 health,
    /// 4 2, 5 origin, 6 mob, 7 spell} to its observers (the bite's +19, SPEC §3).
    /// </summary>
    public static void Heal(Mob mob, double amount, HitOrigin origin, short spell, long ticks)
    {
        if (!mob.Attackable || mob.Map is not { } map) return;
        var before = mob.Health.At(ticks);
        var after = Math.Min(mob.Health.Max, before + Math.Max(0, origin == HitOrigin.OverTime ? (float)amount : Combat.Rounded(amount)));
        mob.Health.Set(after, Math.Max(ticks, mob.Health.Stamp));
        map.SendToObservers(mob, Combat.HealthUpdate(mob.Id, ticks, after - before, after, EffectType.Magic, origin, mob.Id, spell));
    }

    /// <summary>attributechangeovertime: count ticks from initialinterval after the hit, every interval, origin 5, while its entry on the unit stays (removeonleave, a reset, a death end it).</summary>
    private static void OverTime(MobHit c, SpellTemplate spell, XElement node, int index, IUnit unit)
    {
        var count = (int)SpellTemplate.Float(node, "count", 1);
        var first = SpellTemplate.Float(node, "initialinterval");
        var interval = SpellTemplate.Float(node, "interval", 1);
        var entry = unit.Effects.Find(spell.Id, c.Caster.Id);
        var sub = entry is not null && index < entry.Subs.Length && entry.Subs[index].Active ? entry.Subs[index] : null;
        var generation = sub is null ? 0 : ++sub.Generation;
        for (var k = 0; k < count; k++)
        {
            var ticks = c.Ticks + SpellCasts.Seconds(first + k * interval);
            SpellCasts.At(c.World, c.Map, ticks, () =>
            {
                if (c.Caster.Removed || unit.Map != c.Map) return;
                if (sub is not null && (entry!.Cancelled || !unit.Effects.Contains(entry) || sub.Generation != generation)) return;
                Change(c with { Ticks = ticks }, spell, node, unit, HitOrigin.OverTime, entry?.Stacks ?? 1);
            });
        }
    }

    /// <summary>
    /// pulsingspell: the named spell count times, initialinterval after the hit and then every interval, on the node's unit, while
    /// the entry it listed stays (FRENZY_PULSE stops when the player leaves the arc, SPEC §4.4 item 6). A pulse on the mob itself
    /// goes out as a CastSpell {0 mob, 1 mob, 3 spell, 4 = 5 now, 6 0} first (HEROIC_STACK every 10 s, SPEC §7 item 5).
    /// </summary>
    private static void Pulses(MobHit c, SpellTemplate holder, XElement node, SpellTemplate pulsed, IUnit unit)
    {
        var count = (int)SpellTemplate.Float(node, "count", 1);
        var first = SpellTemplate.Float(node, "initialinterval");
        var interval = SpellTemplate.Float(node, "interval", 1);
        var entry = unit.Effects.Find(holder.Id, c.Caster.Id);
        var mob = c.Caster;
        // Only the next pulse is waiting at a time: 999 pulses of HEROIC_STACK are not 999 timers.
        void Pulse(int k)
        {
            if (k >= count) return;
            var ticks = c.Ticks + SpellCasts.Seconds(first + k * interval);
            SpellCasts.At(c.World, c.Map, ticks, () =>
            {
                if (mob.Dead || mob.Removed || mob.Map != c.Map || unit.Map != c.Map) return;
                if (entry is not null && (entry.Cancelled || !unit.Effects.Contains(entry))) return;
                if (unit is Player p && !Valid(p, c.Map, ticks)) return;
                if (unit == mob)
                    mob.SendAround(new CastSpellEvent { ddn = mob.Id, bvw = mob.Id, bvx = mob.Position, bvy = pulsed.Id, bvz = ticks, bv0 = ticks, Extra = new() { [6] = (byte)0, [7] = (short)0 } });
                Execute(c with { Target = unit, Point = PositionOf(unit) ?? c.Point, Ticks = ticks, Depth = c.Depth + 1, ListHits = false, Cast = null }, pulsed);
                Pulse(k + 1);
            });
        }
        Pulse(0);
    }

    /// <summary>removeactivespell spell (charges, casterchargesonly) and removeactiveeffects category on <paramref name="unit"/>: the list goes out at once.</summary>
    private static void RemoveEffects(MobHit c, XElement node, IUnit unit)
    {
        var charges = (int)SpellTemplate.Float(node, "charges");
        var mine = SpellTemplate.Bool(node, "casterchargesonly");
        Func<ActiveSpell, bool> match = node.Name.LocalName == "removeactivespell"
            ? (string?)node.Attribute("spell") is { } name ? s => s.Spell.Name == name && (!mine || s.Caster == c.Caster.Id) : _ => false
            : (string?)node.Attribute("category") is { Length: > 0 } category ? s => s.Spell.Category == category : _ => false;
        if (!SpellEffects.Remove(c.World, unit, match, charges)) return;
        SpellEffects.Send(c.World, unit);
        SpellEffects.Refresh(c.World, unit);
    }

    /// <summary>The spell an applyspell/pulsingspell/aura names; logged once and null when spells.xml has none.</summary>
    private static SpellTemplate? Sub(MobHit c, XElement node)
    {
        var name = (string?)node.Attribute("spell");
        if (c.World.Spells?.Template(name) is { } sub) return sub;
        Combat.LogOnce("spell:" + name, $"spell {name} named by {c.Root.Name} is not in spells.xml");
        return null;
    }

    // ---- conditions of the effect nodes ----

    /// <summary>Whether <paramref name="node"/> runs on <paramref name="unit"/> in this Execute: its conditions and its chance, decided once.</summary>
    private static bool Holds(Plan plan, MobHit c, XElement node, IUnit unit)
    {
        var key = (node, unit.Id);
        if (plan.Holds.TryGetValue(key, out var holds)) return holds;
        holds = node.Elements().Where(IsCondition).All(e => Holds(c, e, unit))
            && (node.Attribute("chance") is null || c.Map.Random.NextDouble() < SpellTemplate.Float(node, "chance", 1));
        plan.Holds[key] = holds;
        return holds;
    }

    private static bool IsCondition(XElement e) => e.Name.LocalName is "not" or "and" or "or" or "Event" || e.Name.LocalName.StartsWith("If", StringComparison.Ordinal);

    /// <summary>
    /// One condition of a spell effect node (the players' runner's set, SpellCasts.Holds, for a mob caster): not/and/or, Event (a
    /// trigger: true), IfSpellActive / IfSpellCategoryActive (who="Caster" the mob, SpellTarget and EffectTarget the unit), IfChance,
    /// IfTargetRange, IfTargetType, IfTargetCCEffect; every other one is false, logged once.
    /// </summary>
    private static bool Holds(MobHit c, XElement e, IUnit unit)
    {
        var now = c.Ticks;
        IUnit Who() => (string?)e.Attribute("who") == "Caster" ? c.Caster : unit;
        switch (e.Name.LocalName)
        {
            case "not": return !e.Elements().Where(IsCondition).Any(x => Holds(c, x, unit)); // by3.tf: none of its children holds
            case "and": return e.Elements().Where(IsCondition).All(x => Holds(c, x, unit));
            case "or": return e.Elements().Where(IsCondition).Any(x => Holds(c, x, unit));
            case "Event": return true;
            case "IfChance": return c.Map.Random.NextDouble() < SpellTemplate.Float(e, "value", 1);
            case "IfTargetType": return ((string?)e.Attribute("type") ?? "") == "mob" ? unit is Mob : unit is Player;
            case "IfTargetRange":
                return Compare(e, Vector2.Distance(c.Caster.Position, PositionOf(unit) ?? c.Caster.Position));
            case "IfSpellActive":
            {
                var who = Who();
                who.Effects.Prune(c.World.GameNow);
                var name = (string?)e.Attribute("spell");
                // MOB_IS_TIER_n: a hidden marker (dummy, time 0) of the mob's tier, never cast on our mobs: read from mobs.xml tier
                // (UNDEAD_NECROMANCER_SUMMON_*'s spawnmob nodes per tier, stageD effects batch 3).
                if (who is Mob tiered && name is { } marker && marker.StartsWith("MOB_IS_TIER_", StringComparison.Ordinal)
                    && int.TryParse(marker["MOB_IS_TIER_".Length..], out var wanted) && !tiered.Effects.Spells.Any(s => s.Spell.Name == marker))
                    return tiered.Template.Tier == wanted;
                var mine = SpellTemplate.Bool(e, "casterchargesonly");
                var charges = Math.Max(1, (int)SpellTemplate.Float(e, "atleastcharges", 1));
                return who.Effects.Spells.Any(s => s.Spell.Name == name && (!mine || s.Caster == c.Caster.Id) && s.Live(now) && s.Stacks >= charges);
            }
            case "IfSpellCategoryActive":
            {
                var category = (string?)e.Attribute("category");
                return Who().Effects.Spells.Any(s => s.Spell.Category == category && s.Live(now));
            }
            case "IfTargetCCEffect": // the unit is under that crowd control (stageD effects batch 3)
                return Who().Effects.On(c.World.GameNow).Any(x => x.Sub.CcType == (string?)e.Attribute("type"));
            default:
                if (SpellConditions.Holds(e, Facts(c, unit)) is { } known) return known;
                Combat.LogOnce("mobcond:" + e.Name.LocalName, $"mob spell condition <{e.Name.LocalName}> is not supported yet: read as false");
                return false;
        }
    }

    private static bool Compare(XElement e, float v) => SpellConditions.Compare(e, v);

    /// <summary>What the shared conditions are asked with: the hit's target as the spell's target, <paramref name="unit"/> as the effect's.</summary>
    private static SpellConditions.Facts Facts(MobHit c, IUnit unit) => new(c.Caster, c.Target ?? unit, unit, c.Map, c.Ticks, c.ChannelTick);

    // ---- areas (SPEC §4.4 item 6) ----

    private sealed record AreaSpawn : ClusterObject;

    private static int _spawns;

    /// <summary>
    /// The area of <paramref name="node"/> in the hit <paramref name="c"/> (SPEC §3 NewSpellEffectArea): origin the mob
    /// (position="atcaster") or the target point; direction castdirection = atan2(dx, dy) of mob → point in [0, 360) (also the default
    /// at the caster), casterrotation the mob's facing, else 0; from the hit (a channel tick) for time s, a linktochanneling area for the
    /// channel's planned length and only on its first tick. Then a check every world tick and at its shapes' keyframes.
    /// </summary>
    private static void CreateArea(MobHit c, SpellTemplate spell, XElement node, int index)
    {
        var map = c.Map;
        var mob = c.Caster;
        var world = c.World;
        if (map.Grid is null || mob.Map != map) return;
        var linked = SpellTemplate.Bool(node, "linktochanneling") && c.Cast is { ChannelEnd: not null } && spell == c.Cast.Channel;
        if (linked && !c.FirstTick) return;
        var here = mob.Position;
        var atCaster = (string?)node.Attribute("position") == "atcaster";
        var aim = PositionOf(c.Target) ?? c.Point;
        var origin = atCaster ? here : aim;
        var yaw = ((string?)node.Attribute("rotation") ?? (atCaster ? "castdirection" : "")) switch
        {
            "castdirection" => (aim - here).LengthSquared() > 1e-4f ? AI.MotionMaster.Facing(here, aim) : mob.Direction,
            "casterrotation" => mob.Direction,
            "targetrotation" => c.Target is Player tp ? tp.Facing : 0,
            _ => 0f,
        };
        var effectName = (string?)node.Attribute("effect");
        var effect = effectName is null || effectName.Equals("NONE", StringComparison.OrdinalIgnoreCase) ? null : world.Spells?.Template(effectName);
        if (effect is null && effectName is not null && !effectName.Equals("NONE", StringComparison.OrdinalIgnoreCase))
            Combat.LogOnce("spell:" + effectName, $"spell {effectName} of {spell.Name}'s area is not in spells.xml: the area applies nothing");
        var shapes = SpellAreas.Nodes(node).ToList();
        var seconds = linked ? spell.Channel!.Seconds : SpellTemplate.Float(node, "time");
        var area = new MobArea(world.ObjectIds.Next(), new AreaSpawn
        {
            Key = new SpawnKey(map.Cluster, "mobarea", _spawns++), Tile = "", Position = origin, Component = new XElement("spelleffectarea"),
        })
        {
            Owner = mob, Holder = spell, Index = (byte)index, Node = node, Effect = effect, Origin = origin, Shapes = shapes,
            Reach = shapes.Count == 0 ? SpellTemplate.Float(node, "effectarearadius") : shapes.Max(n => n.Reach),
            Yaw = (float)Math.Round(yaw, 3), Start = c.Ticks, End = c.Ticks + SpellCasts.Seconds(seconds), Counter = c.Counter, Ap = c.Ap,
            Channel = linked ? c.Cast : null,
        };
        map.AddObject(area);
        if (linked) c.Cast!.Areas.Add(area);
        Log.Debug("ai", string.Create(CultureInfo.InvariantCulture,
            $"{mob} {spell}: area #{area.Id} [{index}] at ({origin.X:0.##}, {origin.Y:0.##}) facing {area.Yaw:0.#} for {seconds:0.##} s"));
        // The shapes' keyframes (AOE_SLOW_EFFECT's radius 0 → 4 at 1.7 s hits exactly then, SPEC §4.4 item 6).
        foreach (var t in shapes.SelectMany(s => s.Radius.Keys.Concat(s.Outer.Keys).Concat(s.AnimatedOpening?.Keys ?? []))
            .Select(k => k.T).Where(t => t > 0 && t <= seconds).Distinct())
        {
            var at = area.Start + SpellCasts.Seconds(t);
            map.Timers.At(world.ClockAt(at), () => Tick(world, area, at, chain: false));
        }
        if (!linked && area.End > area.Start) map.Timers.At(world.ClockAt(area.End), () => Remove(area));
        Tick(world, area, c.Ticks, chain: true);
    }

    /// <summary>
    /// One check of <paramref name="area"/> at <paramref name="at"/>: a valid player inside (its collision radius counted) that was not
    /// at the last check gets the effect spell (counter 0, the cast's AP; once per target with applyeffectonlyoncepertarget, at most
    /// maxeffectareatargets), its hit's CastHits listing the spells that reached it; removeonleave takes the effect off a player that
    /// left. <paramref name="chain"/>: the per-tick check, which sets the next one.
    /// </summary>
    private static void Tick(World world, MobArea area, long at, bool chain)
    {
        if (area.Gone || area.Map is not { } map) return;
        var mob = area.Owner;
        if (mob.Map != map || mob.Removed || (area.Channel is null && area.End > area.Start && at >= area.End) || area.Channel is { Over: true })
        {
            Remove(area);
            return;
        }
        if (chain) map.Timers.At(world.Clock.Now + world.Config.TickTime, () => Tick(world, area, world.GameNow, chain: true));
        var node = area.Node;
        var once = SpellTemplate.Bool(node, "applyeffectonlyoncepertarget");
        var max = (int)SpellTemplate.Float(node, "maxeffectareatargets");
        var radius = world.CollisionRadius;
        var inside = new HashSet<long>();
        foreach (var p in map.Fighters.ToList())
        {
            if (!Valid(p, map, at) || !SpellAreas.Inside(area.Shapes, area.Reach, area.Origin, area.Yaw, area.Start, p.PositionAt(DateTime.UtcNow), radius, at, zeroOff: true)) continue;
            inside.Add(p.Id);
            if (area.InsideLast.Contains(p.Id) || (once && area.Applied.Contains(p.Id)) || (max > 0 && area.Hits >= max)) continue;
            area.Applied.Add(p.Id);
            if (area.Effect is not { } effect) continue;
            area.Hits++;
            Execute(new MobHit(mob, map, area.Holder, 0, area.Ap, p, p.PositionAt(DateTime.UtcNow), at) { ListHits = true, Depth = 1, Source = area.Origin }, effect);
            if (area.Gone) return;
        }
        if (SpellTemplate.Bool(node, "removeonleave") && area.Effect is { } left)
            foreach (var id in area.InsideLast.Where(id => !inside.Contains(id)))
                TakeOff(world, map.Fighters.FirstOrDefault(p => p.Id == id), left, mob);
        area.InsideLast.Clear();
        area.InsideLast.UnionWith(inside);
        if (max > 0 && area.Hits >= max && SpellTemplate.Bool(node, "deletewhenmaxtargetshit")) Remove(area);
    }

    /// <summary>Takes <paramref name="spell"/> by <paramref name="mob"/> off <paramref name="unit"/> (removeonleave): its list goes out.</summary>
    private static void TakeOff(World world, IUnit? unit, SpellTemplate spell, Mob mob)
    {
        if (unit is null || !SpellEffects.Remove(world, unit, s => s.Spell == spell && s.Caster == mob.Id)) return;
        SpellEffects.Send(world, unit);
        SpellEffects.Refresh(world, unit);
    }

    /// <summary>The areas of <paramref name="mob"/> that <paramref name="match"/> selects leave (removespelleffectareasoftarget, stageD effects batch 3).</summary>
    private static void RemoveAreas(Mob mob, Func<MobArea, bool> match)
    {
        if (mob.Map is not { } map) return;
        foreach (var area in map.Objects.Values.OfType<MobArea>().Where(a => a.Owner == mob && !a.Gone && match(a)).ToList()) Remove(area);
    }

    /// <summary>
    /// removespelleffectareasaffectingtarget ownedby="caster" spell effectarearadius maxareasremoved (stageD effects batch 3; the heretic
    /// crossbows and the ballista's mines): the mob's areas that touch <paramref name="unit"/> (its position + effectarearadius + the character's
    /// collision radius) leave, at most maxareasremoved (0 = all).
    /// </summary>
    private static void RemoveAffecting(MobHit c, XElement node, IUnit unit)
    {
        if (PositionOf(unit) is not { } at) return;
        var now = c.World.GameNow;
        var radius = SpellTemplate.Float(node, "effectarearadius") + c.World.CollisionRadius;
        var max = (int)SpellTemplate.Float(node, "maxareasremoved");
        var n = 0;
        RemoveAreas(c.Caster, a => (max <= 0 || n < max) && SpellAreas.Fits(a.Holder, a.Node, (string?)node.Attribute("spell"), null)
            && SpellAreas.Inside(a.Shapes, a.Reach, a.Origin, a.Yaw, a.Start, at, radius, now) && ++n > 0);
    }

    /// <summary>
    /// chainspell (stageD effects batch 3; the chain lightnings of the undead archer boss, the keepers' axes and wisps): <c>spell</c> runs on
    /// <paramref name="first"/> (target self: the valid player nearest the mob within maxdistance), then every interval on the valid player
    /// nearest the last within maxdistance (not the last one; with onlyuniquetargets none hit before), maxcount times in all while one is found.
    /// As the players' chain (SpellCasts.Chain) without NewChainSpell (115) / UpdateChainSpell (116): TODO, not captured.
    /// </summary>
    private static void Chain(MobHit c, XElement node, IUnit first)
    {
        if (Sub(c, node) is not { } spell) return;
        var max = Math.Max(1, (int)SpellTemplate.Float(node, "maxcount", 1));
        var reach = SpellTemplate.Float(node, "maxdistance");
        var interval = SpellTemplate.Float(node, "interval");
        var unique = SpellTemplate.Bool(node, "onlyuniquetargets");
        var hit = new HashSet<long>();
        Player? Nearest(Vector2 from, IUnit? last, long now) => c.Map.Fighters
            .Where(p => p != last && Valid(p, c.Map, now) && !(unique && hit.Contains(p.Id))
                && Vector2.Distance(p.PositionAt(DateTime.UtcNow), from) <= reach + c.World.CollisionRadius)
            .OrderBy(p => Vector2.DistanceSquared(p.PositionAt(DateTime.UtcNow), from)).FirstOrDefault();
        var start = first as Player ?? Nearest(c.Caster.Position, null, c.Ticks);
        if (start is null) return;
        void Hop(int k, Player target, long ticks)
        {
            if (c.Caster.Dead || c.Caster.Removed || c.Caster.Map != c.Map || !Valid(target, c.Map, ticks)) return;
            hit.Add(target.Id);
            Execute(c with { Target = target, Point = target.PositionAt(DateTime.UtcNow), Ticks = ticks, Depth = c.Depth + 1, ListHits = false, Cast = null }, spell);
            if (k + 1 >= max || Nearest(target.PositionAt(DateTime.UtcNow), target, ticks) is not { } next) return;
            var at = ticks + SpellCasts.Seconds(interval);
            SpellCasts.At(c.World, c.Map, at, () => Hop(k + 1, next, at));
        }
        Hop(0, start, c.Ticks);
    }

    /// <summary>Takes <paramref name="area"/> off its map (a Leave to every client that knows it); a removeonleave area takes its effect off the players inside.</summary>
    public static void Remove(MobArea area)
    {
        if (area.Gone) return;
        area.Gone = true;
        if (area.Map is not { } map) return;
        if (SpellTemplate.Bool(area.Node, "removeonleave") && area.Effect is { } effect && area.Owner.World is { } world)
            foreach (var id in area.InsideLast)
                TakeOff(world, map.Fighters.FirstOrDefault(p => p.Id == id), effect, area.Owner);
        map.RemoveObject(area);
        Log.Debug("ai", $"area #{area.Id} of {area.Holder} left");
    }

    /// <summary>The areas of <paramref name="mob"/> on its map: all of them at its death, those with removeoncasterreset or removeoncasterdeath at a reset.</summary>
    public static void OwnerGone(Mob mob, bool death)
    {
        if (mob.Map is not { } map) return;
        foreach (var area in map.Objects.Values.OfType<MobArea>().Where(a => a.Owner == mob).ToList())
            if (death || area.Channel is not null || SpellTemplate.Bool(area.Node, "removeoncasterreset") || SpellTemplate.Bool(area.Node, "removeoncasterdeath"))
                Remove(area);
    }

    // ---- aura ----

    /// <summary>
    /// An aura sub-effect on the mob itself (BURNAURA): while it is in force, every world tick each valid player within auraradius
    /// that has just come in gets the aura's spell (counter 0); removeonleave takes it off a player that leaves, and off all at its end.
    /// </summary>
    private static void Aura(MobHit c, ActiveSpell entry, SubEffect sub, IUnit unit)
    {
        var node = sub.Node;
        var world = c.World;
        if (unit != c.Caster || Sub(c, node) is not { } spell)
        {
            if (unit != c.Caster) Combat.LogOnce("mobaura:" + entry.Spell.Name, $"the aura of {entry.Spell.Name} on another unit than its mob is not supported yet");
            return;
        }
        var mob = c.Caster;
        var map = c.Map;
        var generation = ++sub.Generation;
        var radius = SpellTemplate.Float(node, "auraradius");
        var leave = SpellTemplate.Bool(node, "removeonleave");
        var last = new HashSet<long>();
        void Tick()
        {
            var now = world.GameNow;
            var on = mob.Map == map && !mob.Dead && sub.Generation == generation && mob.Effects.Contains(entry) && entry.On(sub, now);
            var inside = new HashSet<long>();
            if (on)
                foreach (var p in map.Fighters.ToList())
                {
                    if (!Valid(p, map, now) || Vector2.Distance(mob.Position, p.PositionAt(DateTime.UtcNow)) > radius + world.CollisionRadius) continue;
                    inside.Add(p.Id);
                    if (last.Contains(p.Id)) continue;
                    Execute(new MobHit(mob, map, entry.Spell, 0, entry.Ap, p, p.PositionAt(DateTime.UtcNow), now) { Depth = c.Depth + 1 }, spell);
                }
            if (leave)
                foreach (var id in last.Where(id => !inside.Contains(id)))
                    TakeOff(world, map.Fighters.FirstOrDefault(p => p.Id == id), spell, mob);
            last = inside;
            if (on) map.Timers.At(world.Clock.Now + world.Config.TickTime, Tick);
        }
        Tick();
    }
}
