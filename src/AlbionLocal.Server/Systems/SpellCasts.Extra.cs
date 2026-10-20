using System.Numerics;
using System.Xml.Linq;
using AlbionLocal.Photon;
using AlbionLocal.Server.Data;
using AlbionLocal.Server.Entities;
using AlbionLocal.Server.Protocol;

namespace AlbionLocal.Server.Systems;

/// <summary>
/// A multispell in force on a CharacterSpellSlot (stageD effects batch 2): the follow-up <see cref="Spell"/> the slot casts, the spell
/// whose node set it (<see cref="Holder"/>, ReplaceSpellSlotWithMultiSpell key 0), until <see cref="End"/>; castableonlyonce ends it
/// with its cast.
/// </summary>
public sealed record MultiSlot(SpellTemplate Spell, SpellTemplate Holder, long End, bool Once, byte Index = 0);

/// <summary>A stored target of storetargets (stageN spells2): its tag (null: none), the unit (null: a point) and where, until a GameTimeStamp.</summary>
public sealed record StoredTarget(string? Tag, IUnit? Unit, Vector2 Point, long Until);

/// <summary>
/// The instant effects of stageD effects batch 2 and 3 (SPEC §7) that the effect tree runs (<see cref="Run"/>): multispell and
/// cancelmultispell, teleport, resetcooldowns and setcooldowns, the threat effects (changeaggro, maketargettopthreat, matchhighestthreatlevel),
/// interruptcast, chainspell, applyrandomspell, endtransformation, terminatetogglespells, setcrowdcontroldiminishingreturns, spawnmob
/// (<see cref="Summons"/>), removespelleffectareasinarea (<see cref="SpellAreas.RemoveInArea"/>); generateloot runs with the other
/// inventory item tags (<see cref="Consumables.Award"/>). The passive tags (buff, weightbonus, resourcegatheringbuff,
/// applyeffectoneventpassive, pulsingspellpassive, healonhitpassive, dummypassive) run through <see cref="Passives"/>.
/// </summary>
public static partial class SpellCasts
{
    /// <summary>The passivespell tags: they act through the worn items (<see cref="Passives"/>), never in an effect tree.</summary>
    public static readonly IReadOnlySet<string> PassiveTags = new HashSet<string>(StringComparer.Ordinal)
    {
        "buff", "weightbonus", "resourcegatheringbuff", "applyeffectoneventpassive", "pulsingspellpassive", "healonhitpassive", "dummypassive",
    };

    // ---- stored targets (stageN spells2: storetargets, removestoredtargets, target="storedtarget") ----

    /// <summary>
    /// storetargets target maxtargets tag time targettype: the node's units (enemy, friendall, enemyunmounted: the context's unit) or, for
    /// target="ground" (targettype spelleffectarea), the centre of the caster's newest live area of this spell (AFTER_IMAGE's image where it
    /// was cast, ICE_SCULPTURE's), else the cast's point; kept under the tag for <c>time</c> seconds, at most maxtargets per tag (the oldest go).
    /// </summary>
    private static void Store(SpellContext c, SpellTemplate spell, XElement node)
    {
        var p = c.Caster;
        var tag = (string?)node.Attribute("tag") is { Length: > 0 } t ? t : null;
        var until = c.Ticks + Seconds(SpellTemplate.Float(node, "time", 1));
        var most = Math.Max(1, (int)SpellTemplate.Float(node, "maxtargets", 1));
        p.Combat.Stored.RemoveAll(s => s.Until <= c.Ticks);
        if ((string?)node.Attribute("target") == "ground")
        {
            var area = c.Map.Objects.Values.OfType<SpellArea>().Where(a => a.Owner == p && a.Holder == spell && !a.Gone).MaxBy(a => a.Start);
            p.Combat.Stored.Add(new StoredTarget(tag, null, area?.Origin ?? c.Point, until));
        }
        else
            foreach (var target in Targets(c, node))
                p.Combat.Stored.Add(new StoredTarget(tag, target ?? p, SpellMovement.PositionOf(target ?? p, c.Ticks), until));
        var mine = p.Combat.Stored.Where(s => s.Tag == tag).ToList();
        foreach (var old in mine.Take(Math.Max(0, mine.Count - most))) p.Combat.Stored.Remove(old);
    }

    /// <summary>removestoredtargets numberoftargets tag: the oldest stored targets of the tag (none: of any tag) go.</summary>
    private static void RemoveStored(SpellContext c, XElement node)
    {
        var tag = (string?)node.Attribute("tag") is { Length: > 0 } t ? t : null;
        var count = Math.Max(1, (int)SpellTemplate.Float(node, "numberoftargets", 1));
        foreach (var s in c.Caster.Combat.Stored.Where(s => tag is null || s.Tag == tag).Take(count).ToList()) c.Caster.Combat.Stored.Remove(s);
    }

    /// <summary>
    /// The newest live stored target of <paramref name="p"/> for <paramref name="spell"/>'s storedtargettag (none: any tag): a unit still on
    /// its map and alive (a mob not dead, a player not knocked down), else a point; null when there is none.
    /// </summary>
    public static StoredTarget? Stored(Player p, SpellTemplate spell, long now)
    {
        p.Combat.Stored.RemoveAll(s => s.Until <= now);
        return p.Combat.Stored.LastOrDefault(s => (spell.StoredTargetTag is null || s.Tag == spell.StoredTargetTag)
            && s.Unit switch { null => true, Mob m => !m.Dead && !m.Removed && m.Map == p.Map, Player q => q.Map == p.Map && q.Combat.Down is null, _ => false });
    }

    // ---- multispell (ReplaceSpellSlotWithMultiSpell 457) ----

    /// <summary>
    /// multispell spell slot timewindow castableonlyonce (bow E SPEEDARCHER_KITE → SPEEDARCHER_KITE_MULTI_DASH): the slot casts
    /// <c>spell</c> until hit + timewindow; the caster gets ReplaceSpellSlotWithMultiSpell (457) {0 the holding spell, 1 the node's client
    /// effect index, 2 Int64 the window's end} (114814: 5 of 5 captured {0 SPEEDARCHER_KITE, 1 0, 2 = CastFinished key 5 + 8.08 s}; key 1
    /// read as the index, as NewSpellEffectArea key 5: the captured node is index 0). The client swaps the slot itself and turns it back
    /// at the end; timeoutcooldown and the extended windows are its own display.
    /// </summary>
    private static void Multi(SpellContext c, SpellTemplate spell, XElement node, int index)
    {
        var p = c.Caster;
        if (Passives.SlotOf((string?)node.Attribute("slot")) is not { } slot || p.World.Spells?.Template((string?)node.Attribute("spell")) is not { } next)
        {
            Combat.LogOnce("multi:" + spell.Name, $"multispell of {spell.Name} on slot {(string?)node.Attribute("slot")} is not supported (a transformation slot or unknown spell)");
            return;
        }
        var end = c.Ticks + Seconds(SpellTemplate.Float(node, "timewindow"));
        p.Combat.Multi[slot] = new MultiSlot(next, spell, end, SpellTemplate.Bool(node, "castableonlyonce", true), (byte)index);
        p.Session.Send(new ReplaceSpellSlotWithMultiSpellEvent { dva = spell.Id, dvb = (byte)index, dvc = end, Extra = new() { [1] = (byte)index } }); // key 1 sent at 0 too, as captured
        Log.Debug("combat", $"'{p.Character.Name}' slot {slot}: {next} until its window ends");
    }

    /// <summary>
    /// cancelmultispell slot (stageD effects batch 3; DASHKICK_FOLLOW_UP mainhand1, AFTER_IMAGE_REMOVE shoes, slot="all"): the multispell
    /// window on that slot of the caster ends now; ReplaceSpellSlotWithMultiSpell {0 the holding spell, 1 its index, 2 now} so its client turns
    /// the slot back (not captured: the client's own end of the window is assumed to work for an end now too).
    /// </summary>
    private static void CancelMulti(SpellContext c, XElement node)
    {
        var p = c.Caster;
        var name = (string?)node.Attribute("slot");
        List<byte> slots = name == "all" ? [.. p.Combat.Multi.Keys] : Passives.SlotOf(name) is { } one ? [one] : [];
        var now = p.World.GameNow;
        foreach (var slot in slots)
        {
            if (!p.Combat.Multi.Remove(slot, out var m)) continue;
            p.Session.Send(new ReplaceSpellSlotWithMultiSpellEvent { dva = m.Holder.Id, dvb = m.Index, dvc = now, Extra = new() { [1] = m.Index } });
        }
    }

    /// <summary>Whether CharacterSpellSlot <paramref name="slot"/> of <paramref name="p"/> casts <paramref name="spellId"/> through a multispell window at <paramref name="start"/>.</summary>
    internal static bool MultiAllows(Player p, byte slot, int spellId, long start) =>
        p.Combat.Multi.TryGetValue(slot, out var m) && m.Spell.Id == spellId && start <= m.End + Early.Ticks;

    /// <summary>A finished cast of a multispell's follow-up ends its window when it is castableonlyonce (its own multispell, if any, sets the next one at its hit).</summary>
    private static void MultiCast(Player p, PendingCast cast)
    {
        if (p.Combat.Multi.TryGetValue(cast.Slot, out var m) && m.Spell == cast.Spell && m.Once) p.Combat.Multi.Remove(cast.Slot);
    }

    // ---- teleport (blink, SPEC §5.2) ----

    /// <summary>
    /// teleport (MOUNTSPELL_BAT_BLINK, the blink shoes) at client index <paramref name="index"/>: the caster stands at the aim point (a
    /// ground spell) or its target, pulled in to castrange; ForcedMovement kind 1 to the caster and its observers
    /// (<see cref="SpellMovement.Teleport"/>; stageM movement NOTES: the client moves its own player only so), then the node's endeffect
    /// runs there. Walkability is not checked (no collision data on the server).
    /// </summary>
    private static void Blink(SpellContext c, SpellTemplate spell, XElement node, int index)
    {
        var p = c.Caster;
        var from = SpellMovement.PositionOf(p, c.Ticks);
        var to = spell.Target == "ground" || c.Target is null ? c.Point : SpellMovement.PositionOf(c.Target, c.Ticks);
        var d = Vector2.Distance(from, to);
        if (spell.CastRange > 0 && d > spell.CastRange) to = from + (to - from) * (spell.CastRange / d);
        if (Vector2.Distance(from, to) < 0.01f) return;
        SpellMovement.Teleport(c, spell, to, index);
        if ((string?)node.Attribute("endeffect") is { } name && p.World.Spells?.Template(name) is { } end)
            Execute(c with { Point = to, Target = null, Depth = c.Depth + 1 }, end);
    }

    // ---- resetcooldowns ----

    /// <summary>
    /// resetcooldowns slot amount relative (PASSIVE_CAPE_CAERLEON_EFFECT: mainhand1 fully; slot="all" every spell slot): the cooldown
    /// left of the slot's spell × (1 − amount) when relative, else − amount s; SpellCooldownUpdate (249) {0 [slot], 1 [the new end], 2
    /// [new end − its recast], 3 [its recast, ticks], 4 [0]} to the caster (the layout of the captured 249 and of
    /// <see cref="Consumables.Equipped"/>), one entry per changed slot. <paramref name="set"/>: setcooldowns (batch 3; PANTHER_CLAWS_COOLDOWN_PENALTY
    /// 15 s on offhandormainhand3, SHAPE_IMP_REBIRTH_EFFECT 60 s): the slot's cooldown is amount s from now (relative: × its recast), running or not.
    /// Then key 2 is now and key 3 the larger of the recast and the time set (not captured).
    /// </summary>
    private static void ResetCooldowns(SpellContext c, XElement node, bool set = false)
    {
        var p = c.Caster;
        var name = (string?)node.Attribute("slot");
        var slots = p.Session.SpellSlots(p.Inventory);
        IEnumerable<byte> wanted = name == "all" ? Enumerable.Range(0, slots.Length).Select(i => (byte)i)
            : Passives.SlotOf(name) is { } one ? [one] : [];
        var now = p.World.GameNow;
        var amount = SpellTemplate.Float(node, "amount");
        var relative = SpellTemplate.Bool(node, "relative");
        List<byte> changed = [];
        List<long> ends = [], starts = [], spans = [];
        foreach (var slot in wanted)
        {
            if (slot >= slots.Length || p.World.Spells?.Template(slots[slot]) is not { } spell) continue;
            var span = Seconds(spell.RecastDelay);
            long left;
            if (set) left = relative ? (long)(span * Math.Max(0, amount)) : Seconds(Math.Max(0, amount)); // setcooldowns: running or not
            else
            {
                if (!p.Combat.Cooldowns.TryGetValue(spell.Id, out var end) || end <= now) continue;
                left = end - now;
                left = relative ? (long)(left * Math.Max(0, 1 - amount)) : Math.Max(0, left - Seconds(amount));
            }
            p.Combat.Cooldowns[spell.Id] = now + left;
            // setcooldowns may set more than the recast (the panther claws: 15 s on a 5 s recast) or hit a slot without one: the span
            // is then the cooldown set, so that the client's fill (elapsed / span) stays within 0..1 (not captured); nothing to show at 0.
            if (set) span = Math.Max(span, left);
            if (span <= 0) continue;
            changed.Add(slot);
            ends.Add(now + left);
            starts.Add(now + left - span);
            spans.Add(span);
        }
        if (changed.Count == 0) return;
        p.Session.Send(new SpellCooldownUpdateEvent { dy7 = [.. changed], dy8 = [.. ends], dy9 = [.. starts], dza = [.. spans], dzb = new byte[changed.Count] });
    }

    // ---- threat ----

    /// <summary>
    /// The mobs a threat tag acts on (changeaggro, maketargettopthreat, matchhighestthreatlevel): target "self" means the caster's
    /// threat, on the context's mob (HAMMER_SHOVE_SWING_EFFECT, an enemy effect of an area: the hit mob) or, with aggroradius, on every
    /// attackable mob within it of the caster (PBAOE_PULL_EFFECT 7 m, TAUNT); "allmobs" with aggroradius the same (the
    /// POTION_MOB_RESET_P* value −999: off every threat list near); any other target as <see cref="Targets"/>. None for a bot caster.
    /// </summary>
    private static List<IUnit?> ThreatTargets(SpellContext c, XElement node)
    {
        if (c.Caster.IsBot && !c.Caster.IsCompanion) return []; // bots never fight mobs (a companion's TAUNT does pull) (stageJ pvp-spells SPEC §2.9): a bot's TAUNT pulls none onto it
        var target = (string?)node.Attribute("target") ?? "";
        if (target is not ("self" or "allmobs")) return Targets(c, node);
        var radius = SpellTemplate.Float(node, "aggroradius");
        if (radius <= 0)
            return target == "self" && c.Target is Mob { Attackable: true } mob ? [mob] : [];
        var at = SpellMovement.PositionOf(c.Caster, c.Ticks);
        return [.. c.Map.Objects.Values.OfType<Mob>().Where(m => m.Attackable && Vector2.Distance(m.Position, at) <= radius + m.Template.CollisionRadius)];
    }

    /// <summary>
    /// The threat effects on <paramref name="mob"/> (stageD effects batch 2): changeaggro value (+ value threat for the caster, the hammer
    /// and bear smashes; a negative value takes it off), maketargettopthreat factor (the caster's threat to the highest × factor, default
    /// 1, plus one: it takes over the target), matchhighestthreatlevel (the caster's threat up to the highest). An idle mob starts to
    /// fight the caster (<see cref="AI.MobAI.AddThreat"/>).
    /// </summary>
    private static void Threat(SpellContext c, XElement node, Mob mob)
    {
        var p = c.Caster;
        var top = mob.Threat.Count == 0 ? 0 : mob.Threat.Values.Max();
        var mine = mob.Threat.GetValueOrDefault(p);
        var add = node.Name.LocalName switch
        {
            "changeaggro" => SpellTemplate.Float(node, "value"),
            "maketargettopthreat" => Math.Max(0, top * SpellTemplate.Float(node, "factor", 1) + 1 - mine),
            _ => Math.Max(0, top - mine),
        };
        if (add < 0 && !mob.Threat.ContainsKey(p)) return; // nothing to take off: an idle mob is not pulled by it
        mob.AI.AddThreat(p, add);
        if (node.Name.LocalName == "maketargettopthreat" && mob.Threat.ContainsKey(p)) mob.TargetId = p.Id;
    }

    /// <summary>interruptcast on a mob: its cast under way (not finished yet) is cancelled, CastCancel {1 true} (unless interruptiblebyspell="false").</summary>
    internal static void InterruptMob(Mob mob)
    {
        if (mob.AI is AI.BehaviourAI ai) ai.Interrupt();
    }

    /// <summary>
    /// interruptcast or an interruptscasting hit on player <paramref name="q"/> (stageJ pvp-spells SPEC §2.5, phase 2): unless notinterruptible,
    /// its cast under way is cancelled (CastCancel {1 true} to it, {1 true, 9 0} to its observers), its channel ends (ChannelingEnded {1 2}),
    /// and its flag cast and execute end (<see cref="Flagging.Interrupt"/>). An interruptcast spares a cast or channel whose spell (a
    /// channel's parent) has interruptiblebyspell="false" (MOUNTSPELL_BASTION_SHIELD_CHANNEL, the BEAR_ spells), as on a mob (phase 3);
    /// an interruptscasting hit and a stun still end them [inference].
    /// </summary>
    internal static void InterruptPlayer(Player q, long now, string why)
    {
        if (SpellEffects.NotInterruptible(q, now)) return;
        var bySpell = why == "interruptcast";
        if (q.Combat.Cast is { } cast && !(bySpell && !cast.Spell.InterruptibleBySpell)) Cancel(q, cast, interrupted: true);
        if (q.Combat.Channel is { } channel && !(bySpell && !channel.Parent.InterruptibleBySpell)) EndChannel(q, ChannelEnd.Interrupted);
        Flagging.Interrupt(q, why);
    }

    // ---- batch 3: effects on a unit, chains, random picks ----

    /// <summary>
    /// The batch-3 effects on one <paramref name="unit"/> of the node's targets: endtransformation (SHAPESHIFT_HUMAN: every entry with a
    /// transformation in force goes, the list at once), terminatetogglespells (a player's toggles end, as TerminateToggleSpell) and
    /// setcrowdcontroldiminishingreturns (<see cref="SpellEffects.SetDiminishing"/>).
    /// </summary>
    private static void OnUnit(SpellContext c, XElement node, IUnit unit)
    {
        var world = c.Caster.World;
        var now = world.GameNow;
        switch (node.Name.LocalName)
        {
            case "endtransformation":
                if (!SpellEffects.Remove(world, unit, s => s.Subs.Any(sub => s.On(sub, now) && sub.Tag == "transformation"))) break;
                SpellEffects.Send(world, unit);
                SpellEffects.Refresh(world, unit);
                break;
            case "terminatetogglespells":
                if (unit is Player p) EndToggles(p, ToggleEnd.Terminated);
                break;
            case "setcrowdcontroldiminishingreturns":
                SpellEffects.SetDiminishing(unit, node, now);
                break;
        }
    }

    /// <summary>applyrandomspell: one of its applyspell children whose conditions hold, picked at random (the dice, the shrine buffs); null = none.</summary>
    private static SpellTemplate? PickRandom(SpellContext c, XElement node)
    {
        var options = node.Elements("applyspell").Where(e => Conditions(c, e)).ToList();
        return options.Count == 0 ? null : Sub(c, options[c.Map.Random.Next(options.Count)]);
    }

    /// <summary>
    /// chainspell spell chaintarget maxcount maxdistance interval onlyuniquetargets (stageD effects batch 3; DARKMATTER, ARCANE_CHAIN_MISSILE,
    /// CHAINDASH, PASSIVE_CAPE_THETFORD_CHAIN): <c>spell</c> runs on the first target (the context's unit when it matches chaintarget,
    /// else the unit nearest the caster within maxdistance that does), then every interval on the next, the matching unit nearest the last
    /// within maxdistance (not the last one; with onlyuniquetargets none hit before), maxcount times in all while one is found. chaintarget
    /// is a class (<see cref="SpellTargets.Effect"/>; absent = enemy; stageJ pvp-spells SPEC §2.6): enemy and all hop over the mobs, friendall
    /// (CELESTIAL_SPHERE) over the caster and its Friends; the chained spell's own class narrows by kind. Server-side only: TODO
    /// NewChainSpell (115) / UpdateChainSpell (116), the client's beam, are not captured, so the hops show as their spell's CastHit and
    /// damage or heal alone. <paramref name="allowed"/>: whether the parent spell reaches the first target (its spellimmunity, as
    /// <see cref="Blocked"/>); the hops after it check only their own spell.
    /// </summary>
    private static void Chain(SpellContext c, XElement node, Func<IUnit, bool> allowed)
    {
        if (Sub(c, node) is not { } spell) return;
        var cls = SpellTargets.Parse((string?)node.Attribute("chaintarget"));
        if (cls == SpellTargets.Cls.None) cls = SpellTargets.Cls.Enemy;
        bool Fits(IUnit u, long now) => SpellTargets.Effect(cls, SpellTargets.Of(c.Caster, u, now)) && SpellTargets.Kind(spell, u);
        var max = Math.Max(1, (int)SpellTemplate.Float(node, "maxcount", 1));
        var reach = SpellTemplate.Float(node, "maxdistance");
        var interval = SpellTemplate.Float(node, "interval");
        var unique = SpellTemplate.Bool(node, "onlyuniquetargets");
        var first = Fits(c.Unit, c.Ticks) ? c.Unit : Nearest(c, SpellMovement.PositionOf(c.Caster, c.Ticks), reach, null, [], Fits);
        if (first is null || !allowed(first)) return;
        var hit = new HashSet<long>();
        void Hop(int k, IUnit target, long ticks)
        {
            if (c.Caster.Map != c.Map || c.Caster.Combat.Down is not null || target.Map != c.Map || !Fits(target, ticks)) return;
            hit.Add(target.Id);
            var at = SpellMovement.PositionOf(target, ticks);
            Execute(c with { Target = target == c.Caster ? null : target, Point = at, Ticks = ticks, Depth = c.Depth + 1 }, spell);
            if (k + 1 >= max || Nearest(c, at, reach, target, unique ? hit : [], Fits) is not { } next) return;
            var later = ticks + Seconds(interval);
            At(c.Caster.World, c.Map, later, () => Hop(k + 1, next, later));
        }
        Hop(0, first, c.Ticks);
    }

    /// <summary>
    /// The unit nearest <paramref name="from"/> within <paramref name="reach"/> (+ its collision radius) that <paramref name="fits"/> the chain:
    /// a mob or a player or bot, not <paramref name="last"/> nor one of <paramref name="skip"/>.
    /// </summary>
    private static IUnit? Nearest(SpellContext c, Vector2 from, float reach, IUnit? last, HashSet<long> skip, Func<IUnit, long, bool> fits)
    {
        IUnit? best = null;
        var bestDistance = float.MaxValue;
        void Consider(IUnit u, Vector2 at, float radius)
        {
            var d = Vector2.DistanceSquared(at, from);
            if (u == last || skip.Contains(u.Id) || d >= bestDistance || Vector2.Distance(at, from) > reach + radius || !fits(u, c.Ticks)) return;
            best = u;
            bestDistance = d;
        }
        foreach (var m in c.Map.Objects.Values.OfType<Mob>()) Consider(m, m.Position, m.Template.CollisionRadius);
        foreach (var q in c.Map.Characters) Consider(q, SpellMovement.PositionOf(q, c.Ticks), c.Caster.World.CollisionRadius);
        return best;
    }
}
