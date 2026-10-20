using System.Xml.Linq;
using AlbionLocal.Server.Data;
using AlbionLocal.Server.Entities;
using AlbionLocal.Server.Game;
using AlbionLocal.Server.Protocol;

namespace AlbionLocal.Server.Systems;

/// <summary>
/// One client effect index of an <see cref="ActiveSpell"/> (stageD effects SPEC §1): whether it is Active (a timed class
/// whose conditions held when the spell was applied), how long it lasts (DurationMs, 0 = until removed) and what it does
/// while it lasts (a buffovertime's attribute and its scaled value per stack, a crowd control type).
/// </summary>
public sealed class SubEffect(XElement node, int index)
{
    public XElement Node { get; } = node;

    /// <summary>Its client effect index in the spell (<see cref="SpellTemplate.Effects"/>).</summary>
    public int Index { get; } = index;

    public string Tag => Node.Name.LocalName;

    public bool Active { get; internal set; }

    /// <summary>ActiveSpellEffectsUpdate key 8 (sent when &gt; 0): the duration from the spell's Start in ms; 0 = until removed.</summary>
    public int DurationMs { get; internal set; }

    /// <summary>buffovertime: the attribute (its type, e.g. movespeedbonus); null for every other tag.</summary>
    public string? Attribute { get; internal set; }

    /// <summary>buffovertime: its value per stack after the ability power scaling (bej.a_4, <see cref="SpellEffects.Scale"/>).</summary>
    public double Value { get; internal set; }

    /// <summary>The crowd control type (stun, root, silence, slow, …; <see cref="SpellEffects.CcTypeOf"/>); null = none.</summary>
    public string? CcType { get; internal set; }

    /// <summary>attributechangeovertime and aura: counts its schedules, so a re-application (a new stack) drops the older one.</summary>
    internal int Generation { get; set; }

    /// <summary>damageshield: what is left of the shield (damageabsorbed × Ap / 100 when applied, stageD effects SPEC §3.4); 0 for every other tag.</summary>
    public double Shield { get; internal set; }

    /// <summary>applyeffectoneventactive: how often it has applied its spell (at most its count, SPEC §3.5).</summary>
    internal int Uses { get; set; }

    internal void Take(SubEffect from)
    {
        Active = from.Active;
        DurationMs = from.DurationMs;
        Attribute = from.Attribute;
        Value = from.Value;
        CcType = from.CcType;
        Shield = from.Shield;
        Uses = 0;
    }
}

/// <summary>
/// A spell applied to a unit by one caster (stageD effects SPEC §1): ActiveSpellEffectsUpdate keys 1-5 and 9/10, one
/// <see cref="SubEffect"/> per client effect index. Re-applying it from the same caster adds a stack (up to
/// <see cref="SpellTemplate.StackLimit"/>) and restarts it (Start = the new hit).
/// </summary>
public sealed class ActiveSpell
{
    public required SpellTemplate Spell { get; init; }

    /// <summary>The caster's object id; 0 = the world (MOB_RETURNHOME, portal buffs).</summary>
    public required long Caster { get; init; }

    /// <summary>Key 2: the caster's AbilityPower for the source of the spell.</summary>
    public float Ap { get; internal set; }

    /// <summary>Key 3: the caster's CCAbilityPower.</summary>
    public float CcAp { get; internal set; }

    /// <summary>Key 4: the GameTimeStamp of the (latest) application, the hit time.</summary>
    public long Start { get; internal set; }

    /// <summary>Key 5: the stacks (1 to <see cref="SpellTemplate.StackLimit"/>).</summary>
    public byte Stacks { get; internal set; } = 1;

    public required SubEffect[] Subs { get; init; }

    /// <summary>
    /// Listed already but not in force yet: a spell applied in this hit acts only once the hit's effects before it in document
    /// order have run (AXESMASH's damage before its AXESMASH_EFFECT debuff), while the list goes out before the CastHit.
    /// </summary>
    internal bool Pending { get; set; }

    /// <summary>Taken off the list before its end (a removal, a death, a reset): its over-time ticks stop. A plain end does not set it.</summary>
    internal bool Cancelled { get; set; }

    /// <summary>GameTimeStamp a sub-effect ends at; long.MaxValue for one without a duration.</summary>
    public long EndOf(SubEffect s) => s.DurationMs <= 0 ? long.MaxValue : Start + s.DurationMs * TimeSpan.TicksPerMillisecond;

    /// <summary>Whether <paramref name="s"/> is in force at <paramref name="now"/>: Active and not ended.</summary>
    public bool On(SubEffect s, long now) => s.Active && EndOf(s) > now;

    /// <summary>A spell whose Active sub-effects have all ended is dropped from the list.</summary>
    public bool Live(long now) => Subs.Any(s => On(s, now));

    /// <summary>
    /// An entry with known sub-effect states, outside any list (the captured examples of SPEC §2 for the layout, which list
    /// effects that do not run yet, such as FIREWALL_FEAR's forcedmovement): <paramref name="active"/> = the Active client
    /// indices with their DurationMs (0 = until removed).
    /// </summary>
    public static ActiveSpell Of(SpellTemplate spell, long caster, double ap, double ccAp, long start, byte stacks, params (int Index, int DurationMs)[] active)
    {
        var entry = new ActiveSpell
        {
            Spell = spell, Caster = caster, Ap = (float)ap, CcAp = (float)ccAp, Start = start, Stacks = stacks,
            Subs = [.. spell.Effects.Select((node, i) => new SubEffect(node, i))],
        };
        foreach (var (index, ms) in active)
        {
            entry.Subs[index].Active = true;
            entry.Subs[index].DurationMs = ms;
        }
        return entry;
    }

    public override string ToString() => $"{Spell.Name} ×{Stacks} by #{Caster}";
}

/// <summary>
/// The active spells on a unit in insertion order (stageD effects SPEC §1; the client rebuilds its own list from every
/// ActiveSpellEffectsUpdate), and its crowd control diminishing returns. Ended spells are pruned at their end without an
/// update (the client drops them itself, SPEC §2). Touched only under <see cref="World.Sync"/>.
/// </summary>
public sealed class EffectList
{
    private readonly List<ActiveSpell> _spells = [];

    public IReadOnlyList<ActiveSpell> Spells => _spells;

    /// <summary>Per crowd control type: the diminishing return level at the time of the last application.</summary>
    internal Dictionary<string, (double Level, long At)> Diminishing { get; } = new(StringComparer.Ordinal);

    /// <summary>The forced movement under way (a dash, knockback, pull or fear, <see cref="SpellMovement"/>); null when none.</summary>
    public ForcedMove? Forced { get; internal set; }

    /// <summary>A DamageShieldUpdate with a value went out and no bare one since (SPEC §3.4: the bare {0} at the break or end).</summary>
    internal bool ShieldSent { get; set; }

    public int Count => _spells.Count;

    /// <summary>
    /// GameTimeStamp and type of the last damage on its health (stageN spells2: the &lt;AI&gt; IfTargetTookDamage); 0 = none yet. Set by
    /// <see cref="SpellEffects.Damaged"/> from the three damage paths (player → mob, mob → player, player → player).
    /// </summary>
    public long DamagedAt { get; internal set; }

    public EffectType DamagedType { get; internal set; }

    /// <summary>A player's worn passives as last worked out (<see cref="Passives.Of"/>); null until then and for mobs.</summary>
    internal PassiveSet? Passive { get; set; }

    /// <summary>The buff sums of a player's worn passives by attribute (<see cref="Passives"/>: a passive buff counts as a buffovertime); null for mobs.</summary>
    internal Func<IReadOnlyDictionary<string, double>>? PassiveBuffs { get; init; }

    /// <summary>Nothing to add up: no spells, no passive buffs and no plugin modifiers (the fast path of the sums and factors).</summary>
    public bool Idle => _spells.Count == 0 && _modifierSums is null && (PassiveBuffs?.Invoke().Count ?? 0) == 0;

    /// <summary>The unit whose list this is (the plugins' buff hooks); null for a list outside any unit.</summary>
    internal IUnit? Owner { get; init; }

    /// <summary>Whether the plugins listen to entries leaving this list (Buffs.OnRemove).</summary>
    private bool Watched => Owner switch { Player p => p.World.Plugins.WantsBuffRemove, Mob { World: { } w } => w.Plugins.WantsBuffRemove, _ => false };

    /// <summary>Drops the spells that have ended by <paramref name="now"/>; returns how many (the plugins' OnRemove "expired" for each).</summary>
    public int Prune(long now)
    {
        if (_spells.Count == 0) return 0;
        if (!Watched) return _spells.RemoveAll(s => !s.Live(now));
        var gone = _spells.Where(s => !s.Live(now)).ToList();
        if (gone.Count == 0) return 0;
        _spells.RemoveAll(gone.Contains);
        Plugins.PluginHost.BuffsEnded(Owner!, gone, AlbionLocal.Plugin.Api.BuffEnd.Expired);
        return gone.Count;
    }

    // ---- the plugins' stat modifiers (IGameplay.SetModifier)

    private Dictionary<(string Plugin, string Source, string Attribute), double>? _modifiers;
    private Dictionary<string, double>? _modifierSums;

    internal void SetModifier(string plugin, string source, string attribute, double value)
    {
        (_modifiers ??= [])[(plugin, source, attribute)] = value;
        Resum();
    }

    /// <summary>Clears the modifiers of <paramref name="plugin"/> (of <paramref name="source"/> and <paramref name="attribute"/> when given); whether any went.</summary>
    internal bool ClearModifiers(string plugin, string? source, string? attribute)
    {
        if (_modifiers is null) return false;
        var keys = _modifiers.Keys.Where(k => k.Plugin == plugin && (source is null || k.Source == source) && (attribute is null || k.Attribute == attribute)).ToList();
        foreach (var k in keys) _modifiers.Remove(k);
        if (keys.Count > 0) Resum();
        return keys.Count > 0;
    }

    private void Resum()
    {
        _modifierSums = _modifiers is { Count: > 0 } ? _modifiers.GroupBy(k => k.Key.Attribute).ToDictionary(g => g.Key, g => g.Sum(x => x.Value)) : null;
    }

    /// <summary>The sub-effects in force at <paramref name="now"/>, with their spell.</summary>
    public IEnumerable<(ActiveSpell Spell, SubEffect Sub)> On(long now)
    {
        foreach (var s in _spells)
            if (!s.Pending)
                foreach (var sub in s.Subs)
                    if (s.On(sub, now)) yield return (s, sub);
    }

    /// <summary>Whether a sub-effect with <paramref name="tag"/> is in force (stun, root, silence, invincibility, …).</summary>
    public bool Has(string tag, long now) => On(now).Any(x => x.Sub.Tag == tag);

    /// <summary>
    /// The sum of the buffovertime values on <paramref name="attribute"/> in force, each × its spell's stacks (SPEC §3.1), and of a
    /// player's passive buffs on it (stageD effects batch 2, <see cref="Passives"/>).
    /// </summary>
    public double Sum(string attribute, long now) =>
        On(now).Where(x => x.Sub.Attribute == attribute).Sum(x => x.Sub.Value * x.Spell.Stacks) + (PassiveBuffs?.Invoke().GetValueOrDefault(attribute) ?? 0)
        + (_modifierSums?.GetValueOrDefault(attribute) ?? 0);

    /// <summary>The entry of <paramref name="spell"/> by <paramref name="caster"/>; null = none.</summary>
    public ActiveSpell? Find(short spell, long caster) => _spells.FirstOrDefault(s => s.Spell.Id == spell && s.Caster == caster);

    public bool Contains(ActiveSpell s) => _spells.Contains(s);

    internal void Add(ActiveSpell s) => _spells.Add(s);

    internal bool Remove(ActiveSpell s)
    {
        s.Cancelled = true;
        if (!_spells.Remove(s)) return false;
        if (Watched) Plugins.PluginHost.BuffsEnded(Owner!, [s], AlbionLocal.Plugin.Api.BuffEnd.Removed);
        return true;
    }

    /// <summary>Every spell gone (death, reset, knockdown), a forced movement under way with them; the diminishing returns stay.</summary>
    public void Clear()
    {
        foreach (var s in _spells) s.Cancelled = true;
        var gone = Watched && _spells.Count > 0 ? _spells.ToList() : null;
        _spells.Clear();
        Forced = null;
        if (gone is not null) Plugins.PluginHost.BuffsEnded(Owner!, gone, AlbionLocal.Plugin.Api.BuffEnd.Cleared);
    }
}

/// <summary>
/// The spell effects that stay on a unit (analysis/work/stageD/effects/SPEC.md §1-§3, batch 1 parts A and B): the list
/// (<see cref="EffectList"/>) and its ActiveSpellEffectsUpdate (11, <see cref="Update"/>), the application of a spell's
/// timed sub-effects (<see cref="Make"/>, <see cref="Apply"/>), buffovertime with the bej.a_4 scaling (<see cref="Scale"/>)
/// and what the sums change (bars, speed, resistances, damage), the crowd control duration with diminishing returns
/// (<see cref="CcDuration"/>), spell and crowd control immunity, and the state flags the server enforces (stun, root, silence,
/// cannotattack, invincibility). Part B: damage shields (<see cref="Absorb"/>, DamageShieldUpdate 9), the event triggers of
/// applyeffectoneventactive (<see cref="Trigger"/>) and the untargetable flags. Which tags are listed as Active is
/// <see cref="Listed"/>; every other timed tag still takes its 3 bits as 000, so the key-7 groups stay aligned. Touched only
/// under <see cref="World.Sync"/>.
/// </summary>
public static class SpellEffects
{
    /// <summary>
    /// The timed tags that run and are listed Active (SPEC §3); dash stays inactive (the move goes out as ForcedMovement, SPEC
    /// §5.1: 0 in all captured lists), knockback, pull and forcedmovement are Active for their movement time (FIREWALL_FEAR 500),
    /// the other timed tags stay 000 until they run.
    /// </summary>
    public static readonly IReadOnlySet<string> Listed = new HashSet<string>(StringComparer.Ordinal)
    {
        "buffovertime", "attributechangeovertime", "pulsingspell", "spellimmunity", "cceffectimmunity", "dummy", "invincibility",
        "stun", "root", "silence", "cannotattack", "portalrestriction",
        // Part B: the movement effects (their path goes out as ForcedMovement), damage shields, auras, event triggers, untargetable.
        "knockback", "pull", "forcedmovement", "damageshield", "aura", "applyeffectoneventactive", "nothittablebyothers", "nothittable",
        "notselectable",
        // StageD mounts (SPEC §2): the mounted buff's speed-buff trigger (its own bit set in every captured list of 1252; what it does is
        // Systems.Mounts.Update; in the other spells that have it the bit is listed too but not verified, and nothing fires) and
        // DISMOUNTED's PvP flag (listed with its 5 s, captured {0xc9} [5000]).
        "castspellaftermoving", "cannotattackplayers",
        // Batch 2/3: heal and energy on hit, reflected damage, the health floor, invisibility to mobs, no healing, the gathering food.
        "healonhit", "reflectdamageactive", "clamphitpoints", "invisibility", "cannotreceivehealing", "resourcegatheringbuffovertime",
        // Batch 3: the shapeshifter forms (listed only, TODO the Transformation event 547), casts that nothing interrupts, mobs that do not
        // aggro, energy per hit taken, the mob strength buffs, the timed carry load.
        "transformation", "notinterruptible", "cannotaggro", "gainenergyondamage", "mobdamagemodifier", "mobhealthmodifier", "weightbonusovertime",
        // StageN spells-bosses: the fame buffs by mob category (Combat.FameBuff reads them at a kill).
        "mobfamebycategoryovertime",
    };

    private static EffectSettings Settings(World world) => world.GameData?.Effects ?? EffectSettings.Default;

    // ---- ActiveSpellEffectsUpdate (SPEC §2) ----

    /// <summary>
    /// ActiveSpellEffectsUpdate (11, akp) with the whole list <paramref name="spells"/> of unit <paramref name="unit"/> at
    /// <paramref name="now"/> (the client clears and rebuilds its list from it): 1 spell ids, 2 Ap, 3 CcAp, 4 Start, 5 stacks,
    /// 7 3 bits per client effect index over all spells in order (bit 0 Active, bit 1 a duration in key 8; non-timed and
    /// inactive groups 000, bit 2 never), 8 the durations in ms (left out when none), 9 one bit per spell whose caster is
    /// not the unit, 10 those casters (long[]; byte[] zeros when all are the world, as the captured {0} of MOB_RETURNHOME).
    /// No key 6. An empty list is the captured death clear (<see cref="Combat.EffectsCleared"/>).
    /// </summary>
    public static ActiveSpellEffectsUpdateEvent Update(long unit, IReadOnlyList<ActiveSpell> spells, long now)
    {
        if (spells.Count == 0) return Combat.EffectsCleared(unit);
        var groups = spells.Sum(s => s.Subs.Length);
        var bits = new byte[(3 * groups + 7) / 8];
        void Set(int bit) => bits[bit / 8] |= (byte)(1 << (bit % 8));
        var durations = new List<int>();
        var g = 0;
        foreach (var s in spells)
            foreach (var sub in s.Subs)
            {
                if (s.On(sub, now) && SpellTemplate.Timed.Contains(sub.Tag))
                {
                    Set(3 * g);
                    if (sub.DurationMs > 0)
                    {
                        Set(3 * g + 1);
                        durations.Add(sub.DurationMs);
                    }
                }
                g++;
            }
        var others = new byte[(spells.Count + 7) / 8];
        var casters = new List<long>();
        for (var i = 0; i < spells.Count; i++)
        {
            if (spells[i].Caster == unit) continue;
            others[i / 8] |= (byte)(1 << (i % 8));
            casters.Add(spells[i].Caster);
        }
        var e = new ActiveSpellEffectsUpdateEvent
        {
            ddn = unit,
            brs = [.. spells.Select(s => s.Spell.Id)],                 // [1]
            brt = [.. spells.Select(s => s.Ap)],                       // [2]
            bru = [.. spells.Select(s => s.CcAp)],                     // [3]
            brv = [.. spells.Select(s => s.Start)],                    // [4]
            brw = [.. spells.Select(s => s.Stacks)],                   // [5]
            bry = bits,                                                // [7]
            brz = durations.Count > 0 ? [.. durations] : null,         // [8]
            br0 = others,                                              // [9]
        };
        if (casters.Count > 0)
        {
            if (casters.All(c => c == 0)) e.Extra = new() { [10] = new byte[casters.Count] };
            else e.br1 = [.. casters];                                 // [10]
        }
        return e;
    }

    /// <summary>The unit's list now (ended spells pruned) to every client that knows it, its own included.</summary>
    public static void Send(World world, IUnit unit)
    {
        var now = world.GameNow;
        unit.Effects.Prune(now);
        unit.SendAround(Update(unit.Id, unit.Effects.Spells, now));
    }

    /// <summary>The list of <paramref name="unit"/> for a client that has just got its NewCharacter / NewMob; null when it is empty.</summary>
    public static ActiveSpellEffectsUpdateEvent? ForNewObserver(IUnit unit, long now)
    {
        unit.Effects.Prune(now);
        return unit.Effects.Count == 0 ? null : Update(unit.Id, unit.Effects.Spells, now);
    }

    // ---- application (SPEC §1, §3) ----

    /// <summary>
    /// The sub-effect of <paramref name="node"/> (client index <paramref name="index"/>) as the spell is applied by
    /// <paramref name="caster"/> (ability power <paramref name="ap"/>, crowd control power <paramref name="ccAp"/>) to
    /// <paramref name="target"/> at <paramref name="now"/>; null = not Active (not a <see cref="Listed"/> tag, or crowd control the
    /// target is immune to: cceffectimmunity of its type, crowdcontrolresistance 99999, immunetoforcedmovement). Durations:
    /// attributechangeovertime and pulsingspell (initialinterval + (count − 1) × interval) s (0 s = instant, not Active), knockback
    /// and pull the time of their path (<see cref="SpellMovement.Seconds"/>), crowd control <see cref="CcDuration"/> (its
    /// diminishing return raised), every other time × 1000 (0 = until removed). damageshield: its <see cref="SubEffect.Shield"/>.
    /// </summary>
    public static SubEffect? Make(World world, IUnit caster, double ap, double ccAp, XElement node, int index, IUnit target, long now)
    {
        var tag = node.Name.LocalName;
        if (!Listed.Contains(tag)) return null;
        double seconds = tag is "attributechangeovertime" or "pulsingspell"
            ? SpellTemplate.Float(node, "initialinterval") + (Math.Max(1, (int)SpellTemplate.Float(node, "count", 1)) - 1) * SpellTemplate.Float(node, "interval", 1)
            : SpellTemplate.Float(node, "time");
        if (seconds <= 0 && tag is "attributechangeovertime" or "pulsingspell") return null; // a 0 s span (count 1, no initialinterval) is instant, not "until removed"
        var moved = tag is "knockback" or "pull";
        if (moved) seconds = SpellMovement.Seconds(node, caster, target, now); // the path's time, not scaled
        var sub = new SubEffect(node, index) { Active = true };
        if (tag == "damageshield")
        {
            var absorbed = SpellTemplate.Float(node, "damageabsorbed");
            sub.Shield = SpellTemplate.Bool(node, "ignoreabilitypowerscaling") ? absorbed : absorbed * ap / Settings(world).BaseDamage;
        }
        if (tag == "buffovertime")
        {
            sub.Attribute = (string?)node.Attribute("type") ?? "";
            var value = SpellTemplate.Float(node, "value");
            sub.Value = SpellTemplate.Bool(node, "ignoreabilitypowerscaling") ? value : Scale(sub.Attribute, value, ap, Settings(world));
        }
        if (CcTypeOf(node) is { } cc)
        {
            if (CcImmune(target, cc, now) || target is Mob { Template.CrowdControlResistance: >= ImmuneResistance }) return null;
            if (cc is "knockback" or "forcedmovement" && target is Mob { Template.ImmuneToForcedMovement: true }) return null;
            if ((moved || tag == "forcedmovement") && seconds <= 0) return null; // nowhere to go
            sub.CcType = cc;
            if (seconds > 0 && !moved) seconds = CcSeconds(world, caster, ccAp, node, cc, target, seconds, now);
        }
        sub.DurationMs = seconds > 0 ? Math.Max(1, (int)Math.Round(seconds * 1000)) : 0;
        return sub;
    }

    /// <summary>
    /// Applies <paramref name="spell"/> by <paramref name="caster"/> to <paramref name="unit"/> with the sub-effects of
    /// <paramref name="planned"/> (by client index; null = inactive) at hit time <paramref name="start"/>: a new entry at the
    /// end of the list, or (the same spell and caster on it already) one stack more and a restart. Each Active sub-effect
    /// with a duration is pruned at its end (a map timer, no update, the stats worked out again). The caller sends the list.
    /// <paramref name="pending"/>: a new entry is not in force until <see cref="ActiveSpell.Pending"/> is cleared (the hit's
    /// effects before it have run, <see cref="SpellCasts.Execute"/>).
    /// </summary>
    public static ActiveSpell Apply(World world, IUnit unit, SpellTemplate spell, long caster, double ap, double ccAp, long start, SubEffect?[] planned,
        bool pending = false)
    {
        var list = unit.Effects;
        list.Prune(world.GameNow);
        var entry = list.Find(spell.Id, caster);
        var isNew = entry is null;
        var before = entry?.Stacks ?? 0;
        if (entry is null)
        {
            entry = new ActiveSpell
            {
                Spell = spell, Caster = caster, Subs = [.. spell.Effects.Select((node, i) => new SubEffect(node, i))], Pending = pending,
                Stacks = (byte)Math.Clamp(Math.Min(spell.StartCharges, spell.StackLimit), 1, byte.MaxValue), // startcharges (the bait's 10 charges)
            };
            list.Add(entry);
        }
        else entry.Stacks = (byte)Math.Min(Math.Min(spell.StackLimit, byte.MaxValue), entry.Stacks + 1);
        entry.Ap = (float)ap;
        entry.CcAp = (float)ccAp;
        entry.Start = start;
        for (var i = 0; i < entry.Subs.Length; i++)
        {
            if (i < planned.Length && planned[i] is { } plannedSub) entry.Subs[i].Take(plannedSub);
            else entry.Subs[i].Active = false;
        }
        if (unit.Map is { } map)
            foreach (var end in entry.Subs.Where(s => s.Active && s.DurationMs > 0).Select(entry.EndOf).Distinct())
                SpellCasts.At(world, map, end, () => Expired(world, unit));
        if (!entry.Pending) Interrupt(unit, world.GameNow);
        world.Plugins.BuffApplied(unit, entry, isNew, before); // the plugins' Buffs.OnApply / OnStackChange
        return entry;
    }

    /// <summary>
    /// A stunned player's cast under way is cancelled (CastCancel {1 true}, SPEC §3.2), and its channel (ChannelingEnded {1 2}); since stageJ
    /// pvp-spells (D7, phase 2) its flag cast and execute too (<see cref="SpellCasts.InterruptPlayer"/>), a mob's stun as a player's.
    /// </summary>
    internal static void Interrupt(IUnit unit, long now)
    {
        if (unit is Player p && Stunned(unit, now)) SpellCasts.InterruptPlayer(p, now, "stunned");
    }

    /// <summary>
    /// Takes the entries <paramref name="match"/> selects off <paramref name="unit"/>: all of each, or <paramref name="charges"/>
    /// stacks (the entry goes at 0). Returns whether the list changed; the caller sends it and works the stats out again. A player's
    /// toggle whose own entry this took off ends (<see cref="SpellCasts.ToggleEntriesGone"/>, which sends the list itself).
    /// </summary>
    public static bool Remove(World world, IUnit unit, Func<ActiveSpell, bool> match, int charges = 0)
    {
        var list = unit.Effects;
        list.Prune(world.GameNow);
        var changed = false;
        foreach (var s in list.Spells.Where(match).ToList())
        {
            changed = true;
            if (charges > 0 && s.Stacks > charges)
            {
                var before = s.Stacks;
                s.Stacks -= (byte)charges;
                world.Plugins.BuffStacks(unit, s, before); // the plugins' Buffs.OnStackChange
            }
            else list.Remove(s);
        }
        if (changed && unit is Player { Combat.Toggles.Count: > 0 } owner) SpellCasts.ToggleEntriesGone(owner);
        return changed;
    }

    /// <summary>
    /// A sub-effect's end on <paramref name="unit"/>: ended spells pruned without an update (SPEC §2: no update at a plain
    /// timeout), the stats always worked out again (one sub-effect of a spell may end before the others, and another path may
    /// have pruned it already; <see cref="Refresh"/> sends only what changed).
    /// </summary>
    private static void Expired(World world, IUnit unit)
    {
        if (unit.Map is null) return;
        unit.Effects.Prune(world.GameNow);
        Refresh(world, unit);
    }

    /// <summary>
    /// What a change of the list changes on the unit (SPEC §3.1): a player's bars (<see cref="Player.RecalculateStats"/> with
    /// the buffs; RegenerationPlayerComboChanged to it and its observers when they changed, RegenerationHealthChanged (91) when
    /// only the health regeneration did, stageD consumables SPEC §4.2: the soup's 51.692886 → 103.38577), a mob's maximum health
    /// (RegenerationHealthEnergyComboChanged) and its walk speed (the walk sent again at the new speed, a stop while stunned or
    /// rooted). A damage shield that has ended or was taken off: the bare DamageShieldUpdate (SPEC §3.4).
    /// </summary>
    public static void Refresh(World world, IUnit unit)
    {
        var now = world.GameNow;
        ShieldCheck(unit, now);
        switch (unit)
        {
            case Player p:
                if (p.Map is null) break;
                var (healthMax, energyMax, energyRegen) = (p.Health.Max, p.Energy.Max, p.Energy.RegenPerSecond);
                if (!p.RecalculateStats(now, keep: true)) break;
                // Only the health regeneration changed (a food buff, stageD consumables SPEC §4.2): RegenerationHealthChanged.
                var regenOnly = healthMax == p.Health.Max && energyMax == p.Energy.Max && energyRegen == p.Energy.RegenPerSecond;
                p.SendAround(regenOnly ? Combat.RegenerationChanged(p, now) : Combat.PlayerCombo(p, now));
                break;
            case Mob m when m.Map is { } map && !m.Dead:
                var max = MobMaxHealth(m, now);
                if (max > 0 && max != m.Health.Max)
                {
                    var (value, old) = (m.Health.At(now), m.Health.Max);
                    m.Health.Max = max;
                    // A larger max keeps the health share (MOB_OPENWORLD_BUFF_Q* from spawn: a stronger mob, not a wounded one); a smaller one cuts.
                    m.Health.Set(max > old && old > 0 ? value * max / old : Math.Min(value, max), Math.Max(now, m.Health.Stamp));
                    map.SendToObservers(m, AI.AggressiveAI.RegenerationCombo(m, now));
                }
                m.Motion.Rescale();
                break;
        }
    }

    /// <summary>
    /// A mob's maximum health with its buffs: (hitpointsmax + Σ hitpointsmax) × (1 + Σ hitpointsmaxbonus) × mobhealthmodifier, rounded;
    /// × a world boss's HealthFactor (stageH, content/bosses.json).
    /// </summary>
    public static float MobMaxHealth(Mob m, long now) =>
        MathF.Round((float)((m.Template.HitPointsMax + m.Effects.Sum("hitpointsmax", now)) * (1 + m.Effects.Sum("hitpointsmaxbonus", now))
            * MobModifier(m, "mobhealthmodifier", now) * m.HealthFactor));

    // ---- buffs (SPEC §3.1) ----

    /// <summary>
    /// bej.a_4 0x180623ea0: a buffovertime value on <paramref name="attribute"/> at ability power <paramref name="ap"/>:
    /// hitpointsmax / hitpointsregeneration × (AP/100)^hitpointprogression; energymax, energyregeneration, attackdamage,
    /// abilitypower, ccabilitypower × AP/100; physicalarmor / magicresistance × 200 × (AP/100)^resistanceprogression
    /// (resistance points); crowdcontrolresistance × AP/100 × 100; maxload ((AP/100)^loadprogression × baseload − baseload) ×
    /// value; silvertobonuspvefame × AP; every other (the *bonus, reductions, healmodifier, …) unscaled.
    /// </summary>
    public static double Scale(string attribute, double value, double ap, EffectSettings s)
    {
        var r = ap / s.BaseDamage;
        return attribute switch
        {
            "hitpointsmax" or "hitpointsregeneration" => value * Math.Pow(r, s.HitPointProgression),
            "energymax" or "energyregeneration" or "attackdamage" or "abilitypower" or "ccabilitypower" => value * r,
            "physicalarmor" or "magicresistance" => value * 200 * Math.Pow(r, s.ResistanceProgression),
            "crowdcontrolresistance" => value * r * 100,
            "maxload" => (Math.Pow(r, s.LoadProgression) * s.BaseLoad - s.BaseLoad) * value,
            "silvertobonuspvefame" => value * ap,
            _ => value,
        };
    }

    /// <summary>
    /// The damage <paramref name="attacker"/> deals × (1 + Σ physical|magic attack|spell damagebonus) × (1 + bonusdamagevsmobs
    /// or vsplayers) (SPEC §3.1); × a world boss's DamageFactor (stageH, content/bosses.json). A player's spells add its worn items'
    /// physical|magic spelldamagebonus to the sum (<see cref="Combat.Worn"/>, Combat:GearDamageBonus; stageM pvp-damage NOTES §3: T4 cloth
    /// set 2's 0.45 with two passives' 0.095 gives the captured FIRESTAFFBOLT2 109, not 77). Its auto-attacks do not (the captured
    /// fire-staff autos need about a third of the robe's attackdamagebonus; that share is open, NOTES §6 Q2). A bot's spells add it only
    /// with Bots:Gear:DamageBonus (true; the pve preset false, so cloth and leather bots do not hit harder than before).
    /// </summary>
    public static double DealtFactor(IUnit attacker, bool spell, EffectType type, bool vsPlayer, long now)
    {
        var e = attacker.Effects;
        var boss = attacker is Mob { DamageFactor: var f } ? f : 1;
        var name = $"{(type == EffectType.Magic ? "magic" : "physical")}{(spell ? "spell" : "attack")}damagebonus";
        var worn = spell && attacker is Player { World.Config.GearDamageBonus: true } p && (p.Bot is null || (p.IsCompanion ? p.World.Config.Companions.GearDamageBonus : p.World.Config.Bots.GearDamageBonus))
            ? Combat.Worn(p, name) : 0;
        if (e.Idle) return Math.Max(0, 1 + worn) * boss;
        var bonus = worn + e.Sum(name, now);
        var mob = attacker is Mob ? MobModifier(attacker, "mobdamagemodifier", now) : 1;
        return Math.Max(0, 1 + bonus) * Math.Max(0, 1 + e.Sum(vsPlayer ? "bonusdamagevsplayers" : "bonusdamagevsmobs", now)) * mob * boss;
    }

    /// <summary>
    /// mobdamagemodifier / mobhealthmodifier value (stageD effects batch 3; MOB_OPENWORLD_BUFF_Q2-Q6 1.05-1.25, time 0 on the mob): the
    /// product of the values of <paramref name="tag"/> in force on <paramref name="unit"/>, × its stacks; 1 without one.
    /// </summary>
    public static double MobModifier(IUnit unit, string tag, long now)
    {
        if (unit.Effects.Count == 0) return 1;
        var f = 1.0;
        foreach (var (entry, sub) in unit.Effects.On(now))
            if (sub.Tag == tag) f *= Math.Pow(Math.Max(0, SpellTemplate.Float(sub.Node, "value", 1)), entry.Stacks);
        return f;
    }

    /// <summary>The damage <paramref name="target"/> takes × 1 / (1 + bonusdefensevsplayers or vsmobs) (SPEC §3.1, §9 Q5); <paramref name="extra"/> joins the sum (the worn items' value, stageE pvp).</summary>
    public static double TakenFactor(IUnit target, bool fromPlayer, long now, double extra = 0)
    {
        if (target.Effects.Idle && extra == 0) return 1;
        var sum = extra + (target.Effects.Idle ? 0 : target.Effects.Sum(fromPlayer ? "bonusdefensevsplayers" : "bonusdefensevsmobs", now));
        return 1 / Math.Max(0.01, 1 + sum);
    }

    /// <summary>Heals received × (1 + Σ healmodifier) (MOB_UNDEAD_DECAY); not below 0.</summary>
    public static double HealFactor(IUnit target, long now) => target.Effects.Idle ? 1 : Math.Max(0, 1 + target.Effects.Sum("healmodifier", now));

    /// <summary>A mob's walk speed factor: 0 while stunned or rooted, else max(0, 1 + Σ movespeedbonus) (SPEC §3.1).</summary>
    public static float SpeedFactor(IUnit unit, long now)
    {
        var e = unit.Effects;
        if (e.Idle) return 1;
        if (Stunned(unit, now) || Rooted(unit, now)) return 0;
        return (float)Math.Max(0, 1 + e.Sum("movespeedbonus", now));
    }

    // ---- crowd control (SPEC §3.2) ----

    /// <summary>crowdcontrolresistance from which a unit is immune (688 bosses have 99999).</summary>
    public const float ImmuneResistance = 99999;

    /// <summary>
    /// The crowd control type of an effect node (cceffectimmunity type, diminishing return type): stun, root, silence;
    /// knockback; forcedmovement and pull → forcedmovement; buffovertime movespeedbonus &lt; 0 → slow; transformation cceffect="true" →
    /// transformationcc; null = no crowd control.
    /// </summary>
    public static string? CcTypeOf(XElement node) => node.Name.LocalName switch
    {
        "stun" => "stun",
        "root" => "root",
        "silence" => "silence",
        "knockback" => "knockback",
        "forcedmovement" or "pull" => "forcedmovement",
        "transformation" when SpellTemplate.Bool(node, "cceffect") => "transformationcc", // a polymorph; the own shapeshift forms are no CC
        "buffovertime" when (string?)node.Attribute("type") == "movespeedbonus" && SpellTemplate.Float(node, "value") < 0 => "slow",
        _ => null,
    };

    /// <summary>
    /// bej.a_3 and the resistance and diminishing returns after it (SPEC §3.2): seconds × (CcAp / 100) × factor × (1 + bonus) ×
    /// 100 / (100 + crowd control resistance) × (1 − diminishing). factor = ccdurationfactorplayers 0.7 (mobs 0.5), bonus =
    /// the caster's bonusccdurationvs* (the captured fit K = 0.84 = 0.7 × 1.2, §9 Q4). GROUNDARROW_ROOT 1.8 s with CcAp 157.9
    /// and K 0.84 on a ghoul (38): 1730 ms (captured 1729).
    /// </summary>
    public static double CcDuration(double seconds, double ccAp, double factor, double bonus, double resistance, double diminishing) =>
        seconds * ccAp / 100 * factor * (1 + bonus) * 100 / (100 + Math.Max(0, resistance)) * (1 - diminishing);

    /// <summary>
    /// The duration of a crowd control of type <paramref name="cc"/> from <paramref name="node"/>: <see cref="CcDuration"/> with
    /// the ability power part left out for ignoreabilitypowerscaling, the bonus for ignorebonusccdurationscaling, the
    /// resistance for ignorecrowdcontrolresistance and the diminishing returns for ignorediminishingreturns; the bonus is the caster's
    /// bonusccdurationvs{players|mobs} buffs and, for a player caster, its worn items' value (T4 plate armour 0.4, raw; stageJ
    /// pvp-spells phase 0), chosen by the target's kind; the target's
    /// level of that type then rises by its typefactor (up to diminishingreturnmax) unless donttriggerdiminishingreturns.
    /// </summary>
    private static double CcSeconds(World world, IUnit caster, double ccAp, XElement node, string cc, IUnit target, double seconds, long now)
    {
        var s = Settings(world);
        var vsPlayer = target is Player;
        var power = !SpellTemplate.Bool(node, "ignoreabilitypowerscaling");
        var factor = power ? (caster is Player ? s.CcFactorPlayers : s.CcFactorMobs) : 1;
        var bonusName = vsPlayer ? "bonusccdurationvsplayers" : "bonusccdurationvsmobs";
        var bonus = power && !SpellTemplate.Bool(node, "ignorebonusccdurationscaling")
            ? caster.Effects.Sum(bonusName, now) + (caster is Player wearer ? Combat.Worn(wearer, bonusName) : 0) : 0;
        var resistance = SpellTemplate.Bool(node, "ignorecrowdcontrolresistance") ? 0 : ResistanceOf(target, now);
        var diminishing = SpellTemplate.Bool(node, "ignorediminishingreturns") ? 0 : Diminishing(world, target, cc, now);
        if (!SpellTemplate.Bool(node, "donttriggerdiminishingreturns") && s.Diminishing.TryGetValue(cc, out var type))
            target.Effects.Diminishing[cc] = (Math.Min(s.DiminishingMax, Diminishing(world, target, cc, now) + type.Factor), now);
        return CcDuration(seconds, power ? ccAp : 100, factor, bonus, resistance, diminishing);
    }

    /// <summary>
    /// A unit's crowd control resistance (a4f.fc, stageJ armour SPEC §2.3, §5.4): its mobs.xml crowdcontrolresistance (mobs) or its
    /// worn items' (players, <see cref="Player.Defense"/>: T4 plate set 1 at IP 700 105.55, a T4 shield 28.09) + the
    /// crowdcontrolresistance buff points, clamped by &lt;ActiveBuffScaling&gt; (<see cref="Combat.Buffed"/>), then max(0, (1 + Bal) × t +
    /// 100 × Bal), Bal = Σ balancemodifier (only the enraged bosses' BOSS_ENRAGE_BUFF_1/2/3: 0.2 / 0.5 / 10).
    /// </summary>
    public static double ResistanceOf(IUnit unit, long now)
    {
        var (value, data) = unit switch
        {
            Mob m => ((double)m.Template.CrowdControlResistance, m.World?.GameData),
            Player p => (p.Defense.Ccr, p.World.GameData),
            _ => (0.0, null),
        };
        var effects = unit.Effects;
        var t = Combat.Buffed(value, effects.Idle ? 0 : effects.Sum("crowdcontrolresistance", now), Combat.Limit(data, "crowdcontrolresistance", mob: unit is Mob));
        var balance = effects.Idle ? 0 : effects.Sum("balancemodifier", now);
        return Math.Max(0, (1 + balance) * t + 100 * balance);
    }

    /// <summary>
    /// The diminishing return level of <paramref name="cc"/> on <paramref name="unit"/> at <paramref name="now"/>: the level of
    /// its last application decaying linearly to 0 over the type's decreasetime (the data fit 0.3 × (1 − Δt / 10 s) for slows;
    /// the exact curve is open, SPEC §9 Q4).
    /// </summary>
    public static double Diminishing(World world, IUnit unit, string cc, long now)
    {
        if (!unit.Effects.Diminishing.TryGetValue(cc, out var d) || !Settings(world).Diminishing.TryGetValue(cc, out var type) || type.DecreaseSeconds <= 0) return 0;
        var elapsed = (now - d.At) / (double)TimeSpan.TicksPerSecond;
        return Math.Max(0, d.Level * (1 - elapsed / type.DecreaseSeconds));
    }

    // ---- immunity and state (SPEC §3.2-§3.4) ----

    /// <summary>
    /// Whether a spellimmunity on <paramref name="unit"/> blocks <paramref name="spell"/>: its spellname is the spell's name or
    /// "*", or its category the spell's category (SPEC §3.3). A blocked spell does nothing and its CastHit has hit type 2.
    /// </summary>
    public static bool Immune(IUnit unit, SpellTemplate spell, long now)
    {
        if (unit.Effects.Count == 0) return false;
        foreach (var (_, sub) in unit.Effects.On(now))
        {
            if (sub.Tag != "spellimmunity") continue;
            var name = (string?)sub.Node.Attribute("spellname");
            if (name is "*" || name == spell.Name) return true;
            if ((string?)sub.Node.Attribute("category") is { Length: > 0 } category && category == spell.Category) return true;
        }
        return false;
    }

    /// <summary>Whether a cceffectimmunity of type <paramref name="cc"/> is on <paramref name="unit"/> (forcedmovement also covers knockback).</summary>
    public static bool CcImmune(IUnit unit, string cc, long now) =>
        unit.Effects.Count > 0 && unit.Effects.On(now).Any(x => x.Sub.Tag == "cceffectimmunity"
            && (string?)x.Sub.Node.Attribute("type") is var t && (t == cc || (cc == "knockback" && t == "forcedmovement")));

    /// <summary>Stunned: no move, cast or auto-attack; a cast under way is cancelled.</summary>
    public static bool Stunned(IUnit unit, long now) => unit.Effects.Count > 0 && unit.Effects.Has("stun", now);

    /// <summary>Rooted: no movement; casts and auto-attacks allowed.</summary>
    public static bool Rooted(IUnit unit, long now) => unit.Effects.Count > 0 && unit.Effects.Has("root", now);

    /// <summary>Silenced: no CastStart unless the spell is castablewhensilenced; auto-attacks allowed.</summary>
    public static bool Silenced(IUnit unit, long now) => unit.Effects.Count > 0 && unit.Effects.Has("silence", now);

    /// <summary>No auto-attack: stunned or cannotattack.</summary>
    public static bool CannotAttack(IUnit unit, long now) => unit.Effects.Count > 0 && (unit.Effects.Has("cannotattack", now) || unit.Effects.Has("stun", now));

    /// <summary>Damage of <paramref name="type"/> came off <paramref name="unit"/>'s health at <paramref name="ticks"/> (IfTargetTookDamage).</summary>
    public static void Damaged(IUnit unit, EffectType type, long ticks)
    {
        if (ticks < unit.Effects.DamagedAt) return;
        unit.Effects.DamagedAt = ticks;
        unit.Effects.DamagedType = type;
    }

    /// <summary>invincibility: damage on the unit is ignored (no HealthUpdate).</summary>
    public static bool Invincible(IUnit unit, long now) => unit.Effects.Count > 0 && unit.Effects.Has("invincibility", now);

    /// <summary>nothittablebyothers / nothittable: others' attacks and spells do not hit the unit (FLAMEDASH, SPEC §3.4).</summary>
    public static bool NotHittable(IUnit unit, long now) => unit.Effects.Count > 0 && (unit.Effects.Has("nothittablebyothers", now) || unit.Effects.Has("nothittable", now));

    /// <summary>
    /// invisibility (to everyone, mobs, mobsexceptplayerguardmobs or enemies; not "players"): mobs do not aggro the unit (stageD effects
    /// batch 2; PASSIVE_CAPE_UNDEAD_EFFECT, the ambush and stealth spells).
    /// </summary>
    public static bool InvisibleToMobs(IUnit unit, long now) => unit.Effects.Count > 0
        && unit.Effects.On(now).Any(x => x.Sub.Tag == "invisibility" && (string?)x.Sub.Node.Attribute("to") is null or "everyone" or "mobs" or "mobsexceptplayerguardmobs" or "enemies");

    /// <summary>notinterruptible (HYPER_FOCUS, PASSIVE_HOLY_ASCENDED_EFFECT, the territory guards' INTERRUPT_PROTECTION): no stun, interruptcast or damagecancelscasting hit cancels its cast or channel.</summary>
    public static bool NotInterruptible(IUnit unit, long now) => unit.Effects.Count > 0 && unit.Effects.Has("notinterruptible", now);

    /// <summary>cannotaggro on a mob (MOB_WATCHTOWER_COLLECTER_PROTECTION): it starts no fight by itself (stageD effects batch 3).</summary>
    public static bool CannotAggro(IUnit unit, long now) => unit.Effects.Count > 0 && unit.Effects.Has("cannotaggro", now);

    /// <summary>
    /// gainenergyondamage energyperhit (ENERGYSHIELD2, stageD effects batch 3): every hit <paramref name="p"/> takes while it is in force
    /// gives it energyperhit energy (× the stacks), EnergyUpdate {4: 2} with the spell.
    /// </summary>
    public static void EnergyOnDamage(Player p, long ticks)
    {
        if (p.Effects.Count == 0) return;
        foreach (var (entry, sub) in p.Effects.On(ticks).Where(x => x.Sub.Tag == "gainenergyondamage").ToList())
            SpellCasts.GainEnergy(p, SpellTemplate.Float(sub.Node, "energyperhit") * entry.Stacks, entry.Spell, ticks, HitOrigin.Direct);
    }

    /// <summary>
    /// setcrowdcontroldiminishingreturns type value (CLEANSE: DRStun 0.3 when the target is stunned, …; stageD effects batch 3): the
    /// unit's diminishing return level of that crowd control type is set to value now. Returns false for a type not known.
    /// </summary>
    public static bool SetDiminishing(IUnit unit, XElement node, long now)
    {
        var type = (string?)node.Attribute("type") ?? "";
        if (!type.StartsWith("DR", StringComparison.Ordinal) || type.Length < 3) return false;
        var cc = type[2..].ToLowerInvariant(); // DRStun → stun, DRTransformationCC → transformationcc
        unit.Effects.Diminishing[cc] = (Math.Max(0, SpellTemplate.Float(node, "value")), now);
        return true;
    }

    /// <summary>cannotreceivehealing: heals on the unit do nothing.</summary>
    public static bool CannotBeHealed(IUnit unit, long now) => unit.Effects.Count > 0 && unit.Effects.Has("cannotreceivehealing", now);

    /// <summary>
    /// What <paramref name="unit"/> just did, <paramref name="what"/> = attacking, casting, damage (it took damage: a player or a mob),
    /// mounting or worldinteraction: every sub-effect in force with <c>{what}breakseffect="true"</c> is switched off (an invisibility
    /// broken by an attack, a buff by a cast, a stun on a mob by a hit), the list goes out at once and the stats are worked out again
    /// (stageD effects batch 2). A missing attribute counts as false (the client's default is not verified).
    /// </summary>
    public static void Break(World world, IUnit unit, string what, long now)
    {
        if (unit.Effects.Count == 0) return;
        var attribute = what + "breakseffect";
        var changed = false;
        foreach (var (_, sub) in unit.Effects.On(now).ToList())
            if (SpellTemplate.Bool(sub.Node, attribute))
            {
                sub.Active = false;
                changed = true;
            }
        if (!changed) return;
        Send(world, unit);
        Refresh(world, unit);
    }

    /// <summary>
    /// clamphitpoints min spellontrigger maxnumoftriggeredcasts castspellonlyifhpwereabovelimitbefore (stageD effects batch 2): the health
    /// <paramref name="after"/> a hit on <paramref name="p"/> does not go below min (1 without one: SHAPE_IMP_REBIRTH's "not dead");
    /// when it would, spellontrigger runs on it (at most maxnumoftriggeredcasts times; with castspellonlyifhpwereabovelimitbefore only when
    /// <paramref name="before"/> was above the limit). Returns the health to set.
    /// </summary>
    public static double Clamp(World world, Player p, double before, double after, long now)
    {
        if (p.Effects.Count == 0) return after;
        foreach (var (entry, sub) in p.Effects.On(now).Where(x => x.Sub.Tag == "clamphitpoints").ToList())
        {
            var node = sub.Node;
            var min = node.Attribute("min") is not null ? SpellTemplate.Float(node, "min") : 1;
            if (after >= min) continue;
            after = Math.Min(before, min);
            if ((string?)node.Attribute("spellontrigger") is not { } name || world.Spells?.Template(name) is not { } spell || p.Map is not { } map) continue;
            if (SpellTemplate.Bool(node, "castspellonlyifhpwereabovelimitbefore") && before <= min) continue;
            if (sub.Uses >= Math.Max(1, (int)SpellTemplate.Float(node, "maxnumoftriggeredcasts", 1))) continue;
            sub.Uses++;
            SpellCasts.Execute(new SpellCasts.SpellContext(p, map, entry.Spell, 0, entry.Ap, null, SpellMovement.PositionOf(p, now), now) { CcAbilityPower = entry.CcAp, Depth = 1 }, spell);
        }
        return after;
    }

    /// <summary>
    /// reflectdamageactive amountpercent amountabsolute astype (stageD effects batch 2): <paramref name="damage"/> of
    /// <paramref name="attacker"/> on <paramref name="p"/> goes back to it, amountpercent × damage + amountabsolute as astype damage
    /// (through its resistance), HealthUpdate origin 6 with the reflecting spell. A player attacker (stageE pvp) takes it through
    /// <see cref="Pvp.Damage"/>, which reflects no reflected hit back. All the reflects of one hit together send back at most
    /// maxReflectCapacityFactorOfMaxHp (2) × the reflector's maximum health (stageJ pvp-spells SPEC §2.5, phase 3; per hit
    /// [inference]: the channelled 1.25 and 1.5 reflects × a boss's blow), before the attacker's resistance.
    /// </summary>
    public static void Reflect(Player p, IUnit attacker, double damage, long now)
    {
        if (p.Effects.Count == 0 || damage <= 0) return;
        var left = (p.World.GameData?.Effects ?? EffectSettings.Default).ReflectCap * p.Health.Max;
        foreach (var (entry, sub) in p.Effects.On(now).Where(x => x.Sub.Tag == "reflectdamageactive").ToList())
        {
            if (attacker is Mob { Attackable: false }) return;
            var node = sub.Node;
            var amount = Math.Min(left, damage * SpellTemplate.Float(node, "amountpercent") + SpellTemplate.Float(node, "amountabsolute"));
            var type = (string?)node.Attribute("astype") == "physical" ? EffectType.Physical : EffectType.Magic;
            if (amount <= 0) continue;
            left -= amount;
            if (attacker is Mob mob) Combat.Damage(p, mob, amount * Combat.ResistanceOf(mob, type), type, HitOrigin.Reflected, entry.Spell.Id, now);
            else if (attacker is Player player) Pvp.Damage(p, player, amount * Combat.ResistanceOf(player, type), type, HitOrigin.Reflected, entry.Spell.Id, now);
        }
    }

    /// <summary>notselectable / nothittable: not a valid target (the target scans of areas and auras skip it).</summary>
    public static bool NotTargetable(IUnit unit, long now) => unit.Effects.Count > 0 && (unit.Effects.Has("notselectable", now) || NotHittable(unit, now));

    // ---- damage shields (SPEC §3.4) ----

    /// <summary>What is left of the damage shields in force on <paramref name="unit"/>.</summary>
    public static double ShieldOf(IUnit unit, long now) =>
        unit.Effects.Count == 0 ? 0 : unit.Effects.On(now).Where(x => x.Sub.Tag == "damageshield").Sum(x => x.Sub.Shield);

    /// <summary>DamageShieldUpdate (9, amo): {0 unit, 1 the shield left}, or {0} alone (key 1 absent) when it broke or ended.</summary>
    public static DamageShieldUpdateEvent ShieldUpdate(long unit, double left) => new() { ddn = unit, b1p = left > 0 ? (float)left : 0 };

    /// <summary>A shield was applied or changed: DamageShieldUpdate {1 left} to the unit and its observers (ENERGY_BARRIER {1: 804} in the cast batch).</summary>
    internal static void ShieldChanged(IUnit unit, long now)
    {
        var left = ShieldOf(unit, now);
        if (left <= 0)
        {
            ShieldCheck(unit, now);
            return;
        }
        unit.Effects.ShieldSent = true;
        unit.SendAround(ShieldUpdate(unit.Id, left));
    }

    /// <summary>The bare DamageShieldUpdate {0} once no shield is left of the one announced (a break, its end, a removal or clear).</summary>
    internal static void ShieldCheck(IUnit unit, long now)
    {
        if (!unit.Effects.ShieldSent || ShieldOf(unit, now) > 0) return;
        unit.Effects.ShieldSent = false;
        unit.SendAround(ShieldUpdate(unit.Id, 0));
    }

    /// <summary>
    /// <paramref name="damage"/> on <paramref name="unit"/> goes to its damage shields first, in list order (SPEC §3.4); returns what
    /// is left for its health. A shield used up is switched off (the list goes out at once, a sub-effect ended early); the
    /// DamageShieldUpdate carries what is left, or is bare at the break.
    /// </summary>
    public static double Absorb(World world, IUnit unit, double damage, long now)
    {
        if (damage <= 0 || unit.Effects.Count == 0) return damage;
        var broke = false;
        var absorbed = false;
        foreach (var (entry, sub) in unit.Effects.On(now).Where(x => x.Sub.Tag == "damageshield").ToList())
        {
            if (damage <= 0) break;
            var take = Math.Min(sub.Shield, damage);
            sub.Shield -= take;
            damage -= take;
            absorbed = true;
            if (sub.Shield > 0.0001) continue;
            sub.Shield = 0;
            sub.Active = false;
            broke = true;
        }
        if (!absorbed) return damage;
        if (broke)
        {
            Send(world, unit);
            Refresh(world, unit); // the bare update when nothing is left
        }
        if (ShieldOf(unit, now) > 0) ShieldChanged(unit, now);
        return Math.Max(0, damage);
    }

    // ---- applyeffectoneventactive (SPEC §3.5) ----

    /// <summary>Set while a triggered spell runs: what it does triggers nothing further (no loops).</summary>
    [ThreadStatic] private static bool _triggering;

    /// <summary>
    /// Event <paramref name="e"/> of <paramref name="owner"/> (ReceiveDamage, ReceiveDirectDamage, DoDamage, DoDirectDamage,
    /// AutoAttack, CastFinished; <paramref name="other"/> = the other party): every applyeffectoneventactive in force on it for that
    /// event applies its spell, at most its count times (ENRAGE: ReceiveDamage → ENRAGE_BUFF, up to 7 stacks), with the entry's
    /// Ap and CcAp, on the owner (applyeffecttarget self) or on the other party (enemy: an attackable mob, since stageJ pvp-spells phase 2
    /// also a player or bot Hostile to the owner, <see cref="Hostile"/>); applychance is rolled. Then the worn passives'
    /// applyeffectoneventpassive for the same event (batch 2, <see cref="Passives.Trigger"/>; CastFinished with the cast's
    /// <paramref name="slot"/>).
    /// </summary>
    public static void Trigger(Player owner, string e, IUnit? other, long ticks, byte? slot = null)
    {
        if (_triggering || owner.Map is not { } map || owner.World.Spells is not { } spells) return;
        var now = owner.World.GameNow;
        var due = owner.Effects.Count == 0 ? []
            : owner.Effects.On(now).Where(x => x.Sub.Tag == "applyeffectoneventactive" && (string?)x.Sub.Node.Attribute("event") == e).ToList();
        _triggering = true;
        try
        {
            Passives.Trigger(owner, e, other, ticks, slot);
            foreach (var (entry, sub) in due)
            {
                var node = sub.Node;
                var count = (int)SpellTemplate.Float(node, "count");
                if (count > 0 && sub.Uses >= count) continue;
                if (spells.Template((string?)node.Attribute("spell")) is not { } spell) continue;
                var enemy = (string?)node.Attribute("applyeffecttarget") is "enemy" or "groundatenemy";
                if (enemy && !Hostile(owner, other, now)) continue;
                if (node.Attribute("applychance") is not null && map.Random.NextDouble() >= SpellTemplate.Float(node, "applychance", 1)) continue;
                sub.Uses++;
                var target = enemy ? other : null;
                SpellCasts.Execute(new SpellCasts.SpellContext(owner, map, entry.Spell, 0, entry.Ap, target, SpellMovement.PositionOf(target ?? owner, now), ticks)
                    { CcAbilityPower = entry.CcAp, Depth = 1 }, spell);
            }
        }
        finally { _triggering = false; }
    }

    /// <summary>
    /// Whether a trigger's other party <paramref name="other"/> takes an applyeffecttarget enemy of <paramref name="owner"/> at
    /// <paramref name="now"/>: an attackable mob, or a player or bot Hostile to it (stageJ pvp-spells SPEC §2.5, phase 2:
    /// <see cref="SpellTargets.Of"/>, so not under the consensual mode's narrowing and not with Pvp:Spells false).
    /// </summary>
    internal static bool Hostile(Player owner, IUnit? other, long now) => other switch
    {
        Mob m => m.Attackable,
        Player q => q != owner && SpellTargets.Of(owner, q, now).Hostile,
        _ => false,
    };
}
