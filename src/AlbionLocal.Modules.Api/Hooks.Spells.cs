namespace AlbionLocal.Modules;

/// <summary>
/// The spell lifecycle (as TrinityCore's SpellScript): OnBeforeCast (cancel) → OnCast (the cast went through) → OnBeforeHit (its hit lands;
/// Cancel skips the spells.xml effects) → OnBeforeEffect per unit it reaches (Cancel: not on this unit) → OnBeforeDamage /
/// OnAfterDamage per health change → OnAfterHit; OnCancel when it is cancelled or interrupted; OnTick per channel tick; OnAreaCreate /
/// OnAreaTick for its spelleffectarea. Players' casts; OnCast, OnCancel, OnBeforeHit, OnBeforeDamage, OnAfterDamage, OnAfterHit, OnTick and scripted spells (Register) also for mobs (Caster is an IMob).
/// </summary>
public interface ISpellHooks
{
    /// <summary>A player's cast passed the engine's checks and is about to start: Cancel refuses it. Not fired for mobs or bots.</summary>
    IHookRegistration OnBeforeCast(Action<BeforeCastArgs> handler, HookPriority priority = HookPriority.Normal);
    /// <summary>As OnBeforeCast, for one spell (its spells.xml uniquename, or a mask with * and ?).</summary>
    IHookRegistration OnBeforeCast(string spell, Action<BeforeCastArgs> handler, HookPriority priority = HookPriority.Normal);
    /// <summary>A cast went through: energy paid, cooldown started, the hit still to come. Also fired for mobs (the caster is an <see cref="IMob"/>).</summary>
    IHookRegistration OnCast(Action<SpellCastArgs> handler, HookPriority priority = HookPriority.Normal);
    /// <summary>As OnCast, for one spell (its spells.xml uniquename, or a mask with * and ?).</summary>
    IHookRegistration OnCast(string spell, Action<SpellCastArgs> handler, HookPriority priority = HookPriority.Normal);
    /// <summary>A cast under way was dropped before it finished: see <see cref="SpellCancelArgs.Interrupted"/>. Also fired for mobs. Read only.</summary>
    IHookRegistration OnCancel(Action<SpellCancelArgs> handler, HookPriority priority = HookPriority.Normal);
    /// <summary>As OnCancel, for one spell (its spells.xml uniquename, or a mask with * and ?).</summary>
    IHookRegistration OnCancel(string spell, Action<SpellCancelArgs> handler, HookPriority priority = HookPriority.Normal);
    /// <summary>A player's cast's hit lands: <see cref="CancellableSpellArgs.Cancel"/> skips the spells.xml effects (a scripted spell's OnHit still runs).</summary>
    IHookRegistration OnBeforeHit(Action<SpellHitArgs> handler, HookPriority priority = HookPriority.Normal);
    /// <summary>As OnBeforeHit, for one spell (its spells.xml uniquename, or a mask with * and ?).</summary>
    IHookRegistration OnBeforeHit(string spell, Action<SpellHitArgs> handler, HookPriority priority = HookPriority.Normal);
    /// <summary>A player's cast's hit has been handled and its effects have run (also when OnBeforeHit cancelled them). Read only: Cancel does nothing here.</summary>
    IHookRegistration OnAfterHit(Action<SpellHitArgs> handler, HookPriority priority = HookPriority.Normal);
    /// <summary>As OnAfterHit, for one spell (its spells.xml uniquename, or a mask with * and ?).</summary>
    IHookRegistration OnAfterHit(string spell, Action<SpellHitArgs> handler, HookPriority priority = HookPriority.Normal);
    /// <summary>A spell is about to act on one unit: <see cref="CancellableArgs.Cancel"/> keeps all of it off that unit. Runs once per unit reached.</summary>
    IHookRegistration OnBeforeEffect(Action<SpellEffectArgs> handler, HookPriority priority = HookPriority.Normal);
    /// <summary>As OnBeforeEffect, for one spell (its spells.xml uniquename, or a mask with * and ?).</summary>
    IHookRegistration OnBeforeEffect(string spell, Action<SpellEffectArgs> handler, HookPriority priority = HookPriority.Normal);
    /// <summary>One health or energy change of a spell is about to happen, before defences and shields: change Amount, or Cancel to drop it. Also for mobs' spells.</summary>
    IHookRegistration OnBeforeDamage(Action<SpellDamageArgs> handler, HookPriority priority = HookPriority.Normal);
    /// <summary>As OnBeforeDamage, for one spell (its spells.xml uniquename, or a mask with * and ?).</summary>
    IHookRegistration OnBeforeDamage(string spell, Action<SpellDamageArgs> handler, HookPriority priority = HookPriority.Normal);
    /// <summary>One health or energy change of a spell has happened: Amount is what was lost or gained. Also for mobs' spells. Read only.</summary>
    IHookRegistration OnAfterDamage(Action<SpellDamageArgs> handler, HookPriority priority = HookPriority.Normal);
    /// <summary>As OnAfterDamage, for one spell (its spells.xml uniquename, or a mask with * and ?).</summary>
    IHookRegistration OnAfterDamage(string spell, Action<SpellDamageArgs> handler, HookPriority priority = HookPriority.Normal);

    /// <summary>
    /// A hit or a cast is about to set off a player's reaction, a spell's or a worn passive's applyeffectonevent (ENRAGE on a hit taken, a parry's riposte, a proc): it has passed its
    /// chance and <see cref="CancellableArgs.Cancel"/> stops it.
    /// </summary>
    IHookRegistration OnBeforeTrigger(Action<SpellTriggerArgs> handler, HookPriority priority = HookPriority.Normal);
    /// <summary>As OnBeforeTrigger, for one spell set off (its spells.xml uniquename, or a mask with * and ?).</summary>
    IHookRegistration OnBeforeTrigger(string spell, Action<SpellTriggerArgs> handler, HookPriority priority = HookPriority.Normal);
    /// <summary>One channel tick of a player's channelled spell. Not fired for bots.</summary>
    IHookRegistration OnTick(Action<SpellTickArgs> handler, HookPriority priority = HookPriority.Normal);
    /// <summary>As OnTick, for one spell (its spells.xml uniquename, or a mask with * and ?).</summary>
    IHookRegistration OnTick(string spell, Action<SpellTickArgs> handler, HookPriority priority = HookPriority.Normal);
    /// <summary>A spell area (spelleffectarea, a field on the ground) was created. Id = the spell that holds the area node.</summary>
    IHookRegistration OnAreaCreate(Action<SpellAreaArgs> handler, HookPriority priority = HookPriority.Normal);
    /// <summary>As OnAreaCreate, for one spell (its spells.xml uniquename, or a mask with * and ?).</summary>
    IHookRegistration OnAreaCreate(string spell, Action<SpellAreaArgs> handler, HookPriority priority = HookPriority.Normal);
    /// <summary>A tick of a spell area, with the units inside. Id = the spell that holds the area node.</summary>
    IHookRegistration OnAreaTick(Action<SpellAreaArgs> handler, HookPriority priority = HookPriority.Normal);
    /// <summary>As OnAreaTick, for one spell (its spells.xml uniquename, or a mask with * and ?).</summary>
    IHookRegistration OnAreaTick(string spell, Action<SpellAreaArgs> handler, HookPriority priority = HookPriority.Normal);

    /// <summary>
    /// A scripted spell: <paramref name="script"/>'s callbacks implement (or, with <see cref="CustomSpell.ReplaceEffects"/> false, add to)
    /// spell <paramref name="spell"/> server-side. The spell must exist in spells.xml (the client casts it by its id: a ModKit pack's new spell
    /// or an official one). One script per spell; a second registration of the same spell is refused.
    /// </summary>
    IHookRegistration Register(string spell, CustomSpell script);
}

/// <summary>What a cast is about (the caster, the spell, where it came from, its target).</summary>
public abstract class SpellArgsBase(IUnit caster, SpellInfo spell, byte slotIndex, ItemType? item, bool mountSpell, IUnit? target, float x, float y)
{
    /// <summary>The unit casting: a player, or an <see cref="IMob"/> where the hook says so.</summary>
    public IUnit Caster { get; } = caster;

    /// <summary>The caster when it is a player.</summary>
    public IPlayer? Player => Caster as IPlayer;

    /// <summary>The spell.</summary>
    public SpellInfo Spell { get; } = spell;

    /// <summary>The raw CharacterSpellSlot (0-13; 200-211 an extra slot, cast as the wire's slot 25; 255 for a mob).</summary>
    public byte SlotIndex { get; } = slotIndex;

    /// <summary><see cref="SlotIndex"/> as a <see cref="SpellSlot"/> (<see cref="SpellSlot.Other"/> for an extra slot or a mob).</summary>
    public SpellSlot Slot => SpellSlots.FromIndex(SlotIndex);

    /// <summary>The item that granted the spell (the weapon, armour, helmet, shoes, potion, food or the ridden mount); null when none.</summary>
    public ItemType? Item { get; } = item;

    /// <summary>A spell of the ridden mount (its slots replace the equipment's while mounted).</summary>
    public bool MountSpell { get; } = mountSpell;

    /// <summary>The unit it is cast at; null for a ground or self cast.</summary>
    public IUnit? Target { get; } = target;

    /// <summary>The aim point (a ground cast) or the target's position.</summary>
    public float X { get; } = x;
    /// <summary>The Y of <see cref="X"/>'s point.</summary>
    public float Y { get; } = y;
}

/// <summary>A player's cast passed the engine's checks and is about to start: <see cref="CancellableArgs.Cancel"/> refuses it (the client gets the cast refusal).</summary>
public sealed class BeforeCastArgs(IUnit caster, SpellInfo spell, byte slot, ItemType? item, bool mount, IUnit? target, float x, float y)
    : CancellableSpellArgs(caster, spell, slot, item, mount, target, x, y);

/// <summary>
/// A cast went through (energy paid, CastFinished sent, the cooldown started; the hit follows). Its cooldown is
/// <see cref="IFormulaHooks.OnCalcCooldown(Action{SpellFormulaArgs}, HookPriority)"/>'s (so the client shows the same).
/// </summary>
public sealed class SpellCastArgs(IUnit caster, SpellInfo spell, byte slot, ItemType? item, bool mount, IUnit? target, float x, float y)
    : SpellArgsBase(caster, spell, slot, item, mount, target, x, y);

/// <summary>A cast under way was dropped: <see cref="Interrupted"/> by the game (a stun, damage, a dead target) or else cancelled (the player, a new cast, a refusal).</summary>
public sealed class SpellCancelArgs(IUnit caster, SpellInfo spell, byte slot, ItemType? item, bool mount, IUnit? target, float x, float y, bool interrupted)
    : SpellArgsBase(caster, spell, slot, item, mount, target, x, y)
{
    /// <summary>True: the game interrupted it (a stun, damage, a dead target). False: it was cancelled (the player, a new cast, a refusal).</summary>
    public bool Interrupted { get; } = interrupted;
}

/// <summary>A player's cast's hit lands (after hitdelay and a projectile's flight). OnBeforeHit: Cancel skips the spells.xml effects.</summary>
public sealed class SpellHitArgs(IUnit caster, SpellInfo spell, byte slot, ItemType? item, bool mount, IUnit? target, float x, float y, bool projectile, int unitsHit = 0)
    : CancellableSpellArgs(caster, spell, slot, item, mount, target, x, y)
{
    /// <summary>The spell flies (projectilespeed): this is its impact.</summary>
    public bool Projectile { get; } = projectile;

    /// <summary>
    /// OnAfterHit: how many units other than the caster its effects acted on at this hit. 0 on a spell aimed at someone or the ground means everybody stepped out of it or was immune
    /// (<see cref="ICombatHooks.OnHitAvoided(Action{HitAvoidedArgs}, HookPriority)"/> tells of the immune); it is always 0 on a spell only for the caster, and it does not count what a
    /// lingering area does later or a scripted spell does itself. OnBeforeHit: 0.
    /// </summary>
    public int UnitsHit { get; } = unitsHit;
}

/// <summary>
/// A spell (the cast one, one it applies, an area's effect) is about to act on one unit: <see cref="CancellableArgs.Cancel"/> keeps
/// all of it off that unit (the client sees the hit as blocked, as by a spell immunity). <see cref="Spell"/> is the acting spell,
/// <see cref="RootSpell"/> the cast one.
/// </summary>
public sealed class SpellEffectArgs(IUnit caster, SpellInfo spell, SpellInfo root, IUnit unit, bool fromArea) : CancellableArgs
{
    /// <summary>The player whose cast it is.</summary>
    public IUnit Caster { get; } = caster;
    /// <summary>The spell about to act.</summary>
    public SpellInfo Spell { get; } = spell;
    /// <summary>The spell that was cast.</summary>
    public SpellInfo RootSpell { get; } = root;
    /// <summary>The unit it is about to act on.</summary>
    public IUnit Unit { get; } = unit;

    /// <summary>Applied by a spell area (a field the unit stands in).</summary>
    public bool FromArea { get; } = fromArea;
}

/// <summary>
/// One health (or energy) change of a spell on one unit. OnBeforeDamage: before the target's defences and damage shields; <see cref="Amount"/>
/// may be changed, Cancel drops it. OnAfterDamage: after it (Amount is what the health lost or gained, <see cref="HealthLeft"/> after).
/// Heals come through too (<see cref="IsHeal"/>), and energy drains/gains (<see cref="IsEnergy"/>).
/// </summary>
public sealed class SpellDamageArgs(IUnit caster, SpellInfo spell, IUnit target, double amount, bool heal, bool energy, bool overTime, bool area, bool magic) : CancellableArgs
{
    /// <summary>The unit casting: a player or a mob.</summary>
    public IUnit Caster { get; } = caster;
    /// <summary>The spell.</summary>
    public SpellInfo Spell { get; } = spell;
    /// <summary>The unit whose health or energy changes.</summary>
    public IUnit Target { get; } = target;

    /// <summary>The amount (positive for damage and heals alike).</summary>
    public double Amount { get; set; } = amount;

    /// <summary>The amount before any handler changed it.</summary>
    public double OriginalAmount { get; } = amount;

    /// <summary>A gain (a heal, an energy gain) and not a loss.</summary>
    public bool IsHeal { get; } = heal;
    /// <summary>It changes energy, not health.</summary>
    public bool IsEnergy { get; } = energy;

    /// <summary>A damage- or heal-over-time tick.</summary>
    public bool OverTime { get; } = overTime;

    /// <summary>An area node's hit (effectarearadius) or a spell area's.</summary>
    public bool Area { get; } = area;

    /// <summary>Magic (else physical) damage.</summary>
    public bool Magic { get; } = magic;

    /// <summary>OnAfterDamage: the target's health after the hit.</summary>
    public float HealthLeft { get; internal set; }
}

/// <summary>What sets off a reaction (<see cref="SpellTriggerArgs.Event"/>).</summary>
public enum SpellTriggerEvent
{
    /// <summary>The owner took damage, a tick over time too.</summary>
    ReceiveDamage,
    /// <summary>The owner took a direct hit.</summary>
    ReceiveDirectDamage,
    /// <summary>The owner dealt damage, a tick over time too.</summary>
    DoDamage,
    /// <summary>The owner dealt a direct hit.</summary>
    DoDirectDamage,
    /// <summary>The owner's auto-attack.</summary>
    AutoAttack,
    /// <summary>The owner finished a cast.</summary>
    CastFinished,
    /// <summary>Another event of the game's data.</summary>
    Other,
}

/// <summary>A reaction about to run (<see cref="ISpellHooks.OnBeforeTrigger(Action{SpellTriggerArgs}, HookPriority)"/>): Cancel stops it.</summary>
public sealed class SpellTriggerArgs(IPlayer owner, SpellTriggerEvent on, IUnit? other, SpellInfo effect, SpellInfo spell, bool passive) : CancellableArgs
{
    /// <summary>The player it belongs to.</summary>
    public IPlayer Owner { get; } = owner;
    /// <summary>What set it off.</summary>
    public SpellTriggerEvent Event { get; } = on;
    /// <summary>The other party: who hit the owner or was hit by it; null when there is none.</summary>
    public IUnit? Other { get; } = other;
    /// <summary>The spell whose effect this is: ENRAGE, or the worn passive.</summary>
    public SpellInfo Effect { get; } = effect;
    /// <summary>The spell it is about to run: ENRAGE_BUFF.</summary>
    public SpellInfo Spell { get; } = spell;
    /// <summary>It comes from a worn passive, not from a spell on the player.</summary>
    public bool IsPassive { get; } = passive;
}

/// <summary>A channel tick of a player's channelled spell (PARRY, BLOCK, PYROBLAST…): <see cref="Tick"/> from 1 of <see cref="Count"/>.</summary>
public sealed class SpellTickArgs(IUnit caster, SpellInfo spell, SpellInfo channel, int tick, int count, IUnit? target, float x, float y)
{
    /// <summary>The player channelling.</summary>
    public IUnit Caster { get; } = caster;

    /// <summary>The cast spell.</summary>
    public SpellInfo Spell { get; } = spell;

    /// <summary>The channel spell (the next spell id, "…_CHANNEL").</summary>
    public SpellInfo Channel { get; } = channel;

    /// <summary>This tick's number, from 1.</summary>
    public int Tick { get; } = tick;
    /// <summary>How many ticks the channel has.</summary>
    public int Count { get; } = count;
    /// <summary>The unit it is aimed at; null for a ground or self cast.</summary>
    public IUnit? Target { get; } = target;
    /// <summary>The aim point's X.</summary>
    public float X { get; } = x;
    /// <summary>The aim point's Y.</summary>
    public float Y { get; } = y;
}

/// <summary>A spell area (spelleffectarea, a field on the ground): created, or one of its ticks with the units inside.</summary>
public sealed class SpellAreaArgs(IUnit owner, SpellInfo spell, SpellInfo? effect, long areaId, float x, float y, float radius, double seconds, IReadOnlyList<IUnit> inside)
{
    /// <summary>The unit that made the area.</summary>
    public IUnit Owner { get; } = owner;

    /// <summary>The spell that holds the area node.</summary>
    public SpellInfo Spell { get; } = spell;

    /// <summary>The spell it applies to who enters; null for a visual one.</summary>
    public SpellInfo? Effect { get; } = effect;

    /// <summary>The area's id.</summary>
    public long AreaId { get; } = areaId;
    /// <summary>The centre's X.</summary>
    public float X { get; } = x;
    /// <summary>The centre's Y.</summary>
    public float Y { get; } = y;
    /// <summary>The radius in metres.</summary>
    public float Radius { get; } = radius;

    /// <summary>Its lifetime in seconds (0 = until its owner removes it).</summary>
    public double Seconds { get; } = seconds;

    /// <summary>OnAreaTick: the units inside now (empty at OnAreaCreate).</summary>
    public IReadOnlyList<IUnit> Inside { get; } = inside;
}
