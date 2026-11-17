namespace AlbionLocal.Modules;

/// <summary>Health and energy changes, combat state, revives.</summary>
public interface IUnitHooks
{
    /// <summary>A unit's health changed by a hit or a heal (not by regeneration; not for a bot). Id = the mob; a player only reaches the handlers for everything.</summary>
    IHookRegistration OnHealthChange(Action<UnitValueArgs> handler, HookPriority priority = HookPriority.Normal);
    /// <summary>As OnHealthChange, for one mob (its mobs.xml uniquename, or a mask with * and ?).</summary>
    IHookRegistration OnHealthChange(string mob, Action<UnitValueArgs> handler, HookPriority priority = HookPriority.Normal);
    /// <summary>A player's energy changed by a cast's cost or a spell's drain or gain (not by regeneration; not for a bot).</summary>
    IHookRegistration OnEnergyChange(Action<UnitValueArgs> handler, HookPriority priority = HookPriority.Normal);

    /// <summary>A player entered combat (with a mob or a player).</summary>
    IHookRegistration OnEnterCombat(Action<UnitArgs> handler, HookPriority priority = HookPriority.Normal);

    /// <summary>A player's combat ended.</summary>
    IHookRegistration OnLeaveCombat(Action<UnitArgs> handler, HookPriority priority = HookPriority.Normal);

    /// <summary>A knocked-down player got up (the timer, a revive by an ally, a resurrection, a GM).</summary>
    IHookRegistration OnRevive(Action<UnitArgs> handler, HookPriority priority = HookPriority.Normal);

    /// <summary>
    /// Replayed for every joined player and living mob right after this module was (re)loaded, so it can rebuild its per-entity state
    /// (data bags and entity timers are cleared at an unload).
    /// </summary>
    IHookRegistration OnReload(Action<UnitArgs> handler, HookPriority priority = HookPriority.Normal);
    /// <summary>As OnReload, for one mob (its mobs.xml uniquename, or a mask with * and ?).</summary>
    IHookRegistration OnReload(string mob, Action<UnitArgs> handler, HookPriority priority = HookPriority.Normal);
}

/// <summary>A unit: a player for OnEnterCombat, OnLeaveCombat and OnRevive, a player or a mob for OnReload.</summary>
public class UnitArgs(IUnit unit)
{
    /// <summary>The unit.</summary>
    public IUnit Unit { get; } = unit;
}

/// <summary>A unit's health or energy changed (OnHealthChange, OnEnergyChange).</summary>
public sealed class UnitValueArgs(IUnit unit, float before, float after, float max, IUnit? source, SpellInfo? spell) : UnitArgs(unit)
{
    /// <summary>The value before the change.</summary>
    public float Before { get; } = before;
    /// <summary>The value after the change.</summary>
    public float After { get; } = after;
    /// <summary>The maximum of that value.</summary>
    public float Max { get; } = max;

    /// <summary>Who caused it (the attacker or healer); null when it is unknown or no longer on the map.</summary>
    public IUnit? Source { get; } = source;

    /// <summary>The spell, when a spell did it.</summary>
    public SpellInfo? Spell { get; } = spell;

    /// <summary><see cref="After"/> as a share of <see cref="Max"/>, 0 to 1 (0 when Max is 0).</summary>
    public float Share => Max > 0 ? After / Max : 0;
}
