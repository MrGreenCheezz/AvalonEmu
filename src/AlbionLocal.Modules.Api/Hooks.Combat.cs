namespace AlbionLocal.Modules;

/// <summary>
/// Every hit and heal on a player or a mob, whatever caused it: auto-attacks, spells, damage over time, reflected damage, a module's own
/// <see cref="IGameplay.Damage"/>. Fires for every hit: keep the handlers very cheap. Id = the target mob's uniquename (a hit on a player only
/// reaches the handlers registered for everything). Spell-only logic is easier in <see cref="ISpellHooks.OnBeforeDamage(Action{SpellDamageArgs}, HookPriority)"/>.
/// </summary>
public interface ICombatHooks
{
    /// <summary>
    /// A hit is about to land, before anything of it has happened: <see cref="CancellableArgs.Cancel"/> makes it as if it never landed (no
    /// damage, no combat, no threat, invisibility stays; a spell's hit animation still plays), <see cref="BeforeDamageArgs.Amount"/> changes it.
    /// </summary>
    /// <example>
    /// Keeps admins alive:
    /// <code><![CDATA[
    /// Hooks.Combat.OnBeforeDamage(e =>
    /// {
    ///     if (e.IsLethal && e.Target is IPlayer { Level: PlayerLevel.Admin }) e.Cancel();
    /// });
    /// ]]></code>
    /// </example>
    IHookRegistration OnBeforeDamage(Action<BeforeDamageArgs> handler, HookPriority priority = HookPriority.Normal);
    /// <summary>As OnBeforeDamage, for one target mob (its mobs.xml uniquename, or a mask with * and ?); a hit on a player does not reach it.</summary>
    IHookRegistration OnBeforeDamage(string mob, Action<BeforeDamageArgs> handler, HookPriority priority = HookPriority.Normal);

    /// <summary>Health was lost to a hit (damage shields already took their part). A hit all on shields or on a mount's bar lost no health and does not come here: see <see cref="OnAfterAbsorb(Action{AbsorbArgs}, HookPriority)"/>.</summary>
    IHookRegistration OnAfterDamage(Action<HealthChangeArgs> handler, HookPriority priority = HookPriority.Normal);
    /// <summary>As OnAfterDamage, for one target mob (its mobs.xml uniquename, or a mask with * and ?); a hit on a player does not reach it.</summary>
    IHookRegistration OnAfterDamage(string mob, Action<HealthChangeArgs> handler, HookPriority priority = HookPriority.Normal);

    /// <summary>
    /// A damage shield is about to take a hit, once per shield: <see cref="AbsorbArgs.Amount"/> changes what it takes (at most what it would),
    /// <see cref="CancellableArgs.Cancel"/> lets the hit through this shield, to the next one and then the health.
    /// </summary>
    /// <example>
    /// A bow's arrows pass through shields:
    /// <code><![CDATA[
    /// Hooks.Combat.OnBeforeAbsorb(e =>
    /// {
    ///     if (e.Attacker is IPlayer { MainHand.Weapon: WeaponType.Bow }) e.Cancel();
    /// });
    /// ]]></code>
    /// </example>
    IHookRegistration OnBeforeAbsorb(Action<AbsorbArgs> handler, HookPriority priority = HookPriority.Normal);
    /// <summary>As OnBeforeAbsorb, for one target mob (its mobs.xml uniquename, or a mask with * and ?); a hit on a player does not reach it.</summary>
    IHookRegistration OnBeforeAbsorb(string mob, Action<AbsorbArgs> handler, HookPriority priority = HookPriority.Normal);

    /// <summary>A damage shield took a hit, once per shield: how much, what is left of it, whether it broke. Read only.</summary>
    IHookRegistration OnAfterAbsorb(Action<AbsorbArgs> handler, HookPriority priority = HookPriority.Normal);
    /// <summary>As OnAfterAbsorb, for one target mob (its mobs.xml uniquename, or a mask with * and ?); a hit on a player does not reach it.</summary>
    IHookRegistration OnAfterAbsorb(string mob, Action<AbsorbArgs> handler, HookPriority priority = HookPriority.Normal);

    /// <summary>
    /// A hit or a spell did not land on its target: <see cref="HitAvoidedArgs.Reason"/> says why and <see cref="HitAvoidedArgs.ProtectedBy"/> which effect stopped it. The game has no dodge
    /// roll: its DODGE spell is an invincibility and a spell immunity, so a dodge comes here. Read only.
    /// </summary>
    /// <example>
    /// Tells a player that a dodge worked:
    /// <code><![CDATA[
    /// Hooks.Combat.OnHitAvoided(e =>
    /// {
    ///     if (e.ProtectedBy is { Spell.Name: "DODGE" } && e.Target is IPlayer p) p.SendMessage("Dodged!");
    /// });
    /// ]]></code>
    /// </example>
    IHookRegistration OnHitAvoided(Action<HitAvoidedArgs> handler, HookPriority priority = HookPriority.Normal);
    /// <summary>As OnHitAvoided, for one target mob (its mobs.xml uniquename, or a mask with * and ?); a hit on a player does not reach it.</summary>
    IHookRegistration OnHitAvoided(string mob, Action<HitAvoidedArgs> handler, HookPriority priority = HookPriority.Normal);

    /// <summary>A heal on a player is about to land: <see cref="CancellableArgs.Cancel"/> stops it, <see cref="BeforeHealArgs.Amount"/> changes it.</summary>
    IHookRegistration OnBeforeHeal(Action<BeforeHealArgs> handler, HookPriority priority = HookPriority.Normal);

    /// <summary>Health was gained from a heal.</summary>
    IHookRegistration OnAfterHeal(Action<HealthChangeArgs> handler, HookPriority priority = HookPriority.Normal);
    /// <summary>As OnAfterHeal, for one mob (its mobs.xml uniquename, or a mask with * and ?). A heal on a player does not reach it.</summary>
    IHookRegistration OnAfterHeal(string mob, Action<HealthChangeArgs> handler, HookPriority priority = HookPriority.Normal);
}

/// <summary>Who hit whom: a player on a mob, a mob on a player, a player on a player.</summary>
public enum DamageKind
{
    /// <summary>A player hit a mob.</summary>
    PlayerToMob,
    /// <summary>A mob hit a player.</summary>
    MobToPlayer,
    /// <summary>A player hit another player.</summary>
    PlayerToPlayer,
}

/// <summary>What caused a health change.</summary>
public enum DamageOrigin
{
    /// <summary>A melee auto-attack.</summary>
    MeleeAutoAttack = 0,
    /// <summary>A ranged auto-attack.</summary>
    RangedAutoAttack = 1,
    /// <summary>A spell's direct hit or heal, or a module's <see cref="IGameplay.Damage"/> or <see cref="IGameplay.Heal"/>.</summary>
    Direct = 2,
    /// <summary>An area effect.</summary>
    Area = 3,
    /// <summary>A passive that triggers on a hit.</summary>
    OnHitPassive = 4,
    /// <summary>A tick of damage or healing over time.</summary>
    OverTime = 5,
    /// <summary>Damage reflected back at its attacker.</summary>
    Reflected = 6,
}

internal static class DamageOrigins
{
    public static bool IsDirect(this DamageOrigin origin) => origin is DamageOrigin.MeleeAutoAttack or DamageOrigin.RangedAutoAttack or DamageOrigin.Direct or DamageOrigin.Area;
}

/// <summary>A hit about to land (<see cref="ICombatHooks.OnBeforeDamage(Action{BeforeDamageArgs}, HookPriority)"/>), after resistances and before damage shields.</summary>
public sealed class BeforeDamageArgs(IUnit attacker, IUnit target, DamageKind kind, DamageOrigin origin, bool magic, SpellInfo? spell, float targetHealth, double amount) : CancellableArgs
{
    /// <summary>The unit that hits.</summary>
    public IUnit Attacker { get; } = attacker;
    /// <summary>The unit that is hit.</summary>
    public IUnit Target { get; } = target;
    /// <summary>Who hit whom.</summary>
    public DamageKind Kind { get; } = kind;
    /// <summary>What caused the hit.</summary>
    public DamageOrigin Origin { get; } = origin;

    /// <summary>A damage-over-time tick (not a direct hit).</summary>
    public bool OverTime => Origin == DamageOrigin.OverTime;

    /// <summary>
    /// An attack or a spell's hit (an auto-attack, a spell's direct or area hit), not what follows from one (a tick over time, reflected damage,
    /// a passive's proc): what a "chance on hit" should ask. A module's own <see cref="IGameplay.Damage"/> counts as a hit too.
    /// </summary>
    /// <example>
    /// A chance to curse on a hit no module cancelled (Monitor: after every other handler):
    /// <code><![CDATA[
    /// [Priority(HookPriority.Monitor)]
    /// public override void OnBeforeDamage(BeforeDamageArgs e)
    /// {
    ///     if (!e.IsCancelled && e.IsDirectHit && Roll.Chance(0.15)) Game.ApplyBuff(e.Target, "AXE_BLEED_DEBUFF", e.Attacker);
    /// }
    /// ]]></code>
    /// </example>
    public bool IsDirectHit => Origin.IsDirect();

    /// <summary>Magic (else physical) damage.</summary>
    public bool Magic { get; } = magic;

    /// <summary>The spell when a spell did it; null for an auto-attack.</summary>
    public SpellInfo? Spell { get; } = spell;

    /// <summary>The target's health before the hit.</summary>
    public float TargetHealth { get; } = targetHealth;

    /// <summary>The hit would take all of the target's health (set <see cref="Amount"/> lower to keep it alive).</summary>
    public bool IsLethal => Amount >= TargetHealth;

    /// <summary>The damage about to be dealt; set it to change the hit (at least 0). Between players, focus fire protection that this very hit starts still comes off it.</summary>
    public double Amount { get; set; } = amount;

    /// <summary>The damage before any handler changed it.</summary>
    public double OriginalAmount { get; } = amount;
}

/// <summary>A heal about to land (<see cref="ICombatHooks.OnBeforeHeal"/>).</summary>
public sealed class BeforeHealArgs(IUnit healer, IPlayer target, DamageOrigin origin, SpellInfo? spell, double amount) : CancellableArgs
{
    /// <summary>The player that heals (the target itself when a module's heal names no player).</summary>
    public IUnit Healer { get; } = healer;
    /// <summary>The player being healed.</summary>
    public IPlayer Target { get; } = target;
    /// <summary>What caused the heal.</summary>
    public DamageOrigin Origin { get; } = origin;

    /// <summary>The spell's uniquename when a spell did it.</summary>
    public SpellInfo? Spell { get; } = spell;

    /// <summary>The health about to be gained; set it to change the heal (at least 0).</summary>
    public double Amount { get; set; } = amount;

    /// <summary>The heal before any handler changed it.</summary>
    public double OriginalAmount { get; } = amount;
}

/// <summary>Health changed by a hit or a heal.</summary>
public sealed class HealthChangeArgs(IUnit unit, float before, float after, float max, IUnit? source, SpellInfo? spell, DamageOrigin origin)
{
    /// <summary>The unit whose health changed.</summary>
    public IUnit Unit { get; } = unit;
    /// <summary>Its health before the change.</summary>
    public float Before { get; } = before;
    /// <summary>Its health after the change.</summary>
    public float After { get; } = after;
    /// <summary>Its maximum health.</summary>
    public float Max { get; } = max;

    /// <summary>The health lost or gained (positive).</summary>
    public float Amount => Math.Abs(After - Before);

    /// <summary>It took the last of the unit's health.</summary>
    public bool Lethal => After <= 0;

    /// <summary>Who caused it; null when it is unknown or gone.</summary>
    public IUnit? Source { get; } = source;

    /// <summary>The spell's uniquename when a spell did it.</summary>
    public SpellInfo? Spell { get; } = spell;

    /// <summary>What caused it.</summary>
    public DamageOrigin Origin { get; } = origin;

    /// <summary>An attack or a spell's hit or heal, not a tick over time, reflected damage or a passive's proc (<see cref="BeforeDamageArgs.IsDirectHit"/>).</summary>
    public bool IsDirectHit => Origin.IsDirect();
}

/// <summary>
/// A damage shield and a hit on it. <see cref="ICombatHooks.OnBeforeAbsorb(Action{AbsorbArgs}, HookPriority)"/>: <see cref="Amount"/> is what it is about to take and may be changed, Cancel lets the
/// hit through it. <see cref="ICombatHooks.OnAfterAbsorb(Action{AbsorbArgs}, HookPriority)"/>: what it took, read only.
/// </summary>
public sealed class AbsorbArgs(IUnit target, IBuff shield, IUnit? attacker, DamageOrigin origin, SpellInfo? spell, double amount, double shieldLeft, double damageLeft) : CancellableArgs
{
    /// <summary>The unit behind the shield.</summary>
    public IUnit Target { get; } = target;
    /// <summary>The shield: its spell, caster and time left. Valid only during the call.</summary>
    public IBuff Shield { get; } = shield;
    /// <summary>The unit that hits; null when it is gone.</summary>
    public IUnit? Attacker { get; } = attacker;
    /// <summary>What caused the hit.</summary>
    public DamageOrigin Origin { get; } = origin;

    /// <summary>An attack or a spell's hit, not a tick over time, reflected damage or a passive's proc (<see cref="BeforeDamageArgs.IsDirectHit"/>).</summary>
    public bool IsDirectHit => Origin.IsDirect();

    /// <summary>The spell when a spell did it; null for an auto-attack.</summary>
    public SpellInfo? Spell { get; } = spell;

    /// <summary>What the shield takes of the hit. OnBeforeAbsorb: set it to change that (from 0 to what it would take).</summary>
    public double Amount { get; set; } = amount;

    /// <summary>OnBeforeAbsorb: what the shield would take, before any handler changed it. OnAfterAbsorb: the same as <see cref="Amount"/>.</summary>
    public double OriginalAmount { get; } = amount;

    /// <summary>What the shield holds: before this hit in OnBeforeAbsorb, after it in OnAfterAbsorb.</summary>
    public double ShieldLeft { get; } = shieldLeft;

    /// <summary>OnAfterAbsorb: this hit used the shield up.</summary>
    public bool IsBroken => ShieldLeft <= 0;

    /// <summary>What there is of the hit: all that this shield could take in OnBeforeAbsorb, what goes on past it (to the next shield, then the health) in OnAfterAbsorb.</summary>
    public double DamageLeft { get; } = damageLeft;
}

/// <summary>Why a hit did not land (<see cref="HitAvoidedArgs.Reason"/>).</summary>
public enum HitAvoidReason
{
    /// <summary>The target has an invincibility: the DODGE spell, for one.</summary>
    Invincible,
    /// <summary>The target cannot be hit by others at all (nothittable, nothittablebyothers).</summary>
    NotHittable,
    /// <summary>A spell immunity on the target blocks this spell: none of it acts there.</summary>
    SpellImmunity,
    /// <summary>The target is knocked down or on its get-up protection.</summary>
    KnockedDown,
    /// <summary>The target is a mob running home, immune to everything until it is there.</summary>
    Evading,
    /// <summary>The PvP rules drop the hit (no longer hostile, a duel not started): <see cref="HitAvoidedArgs.Detail"/> says which.</summary>
    NotAllowed,
    /// <summary>A module cancelled it (Combat.OnBeforeDamage, Spells.OnBeforeDamage, Spells.OnBeforeEffect): a module's own dodge or parry.</summary>
    Cancelled,
}

/// <summary>A hit or a spell that did not land (<see cref="ICombatHooks.OnHitAvoided(Action{HitAvoidedArgs}, HookPriority)"/>).</summary>
public sealed class HitAvoidedArgs(IUnit attacker, IUnit target, HitAvoidReason reason, DamageOrigin origin, SpellInfo? spell, double amount, IBuff? protectedBy, string detail)
{
    /// <summary>The unit that tried to hit.</summary>
    public IUnit Attacker { get; } = attacker;
    /// <summary>The unit that was not hit.</summary>
    public IUnit Target { get; } = target;
    /// <summary>Why.</summary>
    public HitAvoidReason Reason { get; } = reason;
    /// <summary>What caused the hit.</summary>
    public DamageOrigin Origin { get; } = origin;

    /// <summary>An attack or a spell's hit, not a tick over time, reflected damage or a passive's proc (<see cref="BeforeDamageArgs.IsDirectHit"/>).</summary>
    public bool IsDirectHit => Origin.IsDirect();

    /// <summary>The spell when a spell did it; null for an auto-attack.</summary>
    public SpellInfo? Spell { get; } = spell;

    /// <summary>The damage it would have dealt; 0 when a whole spell was blocked, before any damage was worked out.</summary>
    public double Amount { get; } = amount;

    /// <summary>The effect on the target that stopped it, when one did: <c>e.ProtectedBy?.Spell</c> is DODGE, say. Valid only during the call.</summary>
    public IBuff? ProtectedBy { get; } = protectedBy;

    /// <summary>In words, for a log (the PvP rules' reason); do not read it in code, it may change.</summary>
    public string Detail { get; } = detail;
}
