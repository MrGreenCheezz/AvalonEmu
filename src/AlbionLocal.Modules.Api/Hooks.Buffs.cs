namespace AlbionLocal.Modules;

/// <summary>The life of a spell on a unit (a buff, debuff, crowd control, mount buff), as TrinityCore's AuraScript.</summary>
public interface IBuffHooks
{
    /// <summary>Before a spell's timed effects go on a unit (cast spells, area effects, procs): Cancel keeps it off.</summary>
    IHookRegistration OnBeforeApply(Action<BeforeBuffArgs> handler, HookPriority priority = HookPriority.Normal);
    /// <summary>As OnBeforeApply, for one buff (the spell's spells.xml uniquename, or a mask with * and ?).</summary>
    IHookRegistration OnBeforeApply(string buff, Action<BeforeBuffArgs> handler, HookPriority priority = HookPriority.Normal);

    /// <summary>The buff went on (new, or re-applied: a stack more).</summary>
    IHookRegistration OnApply(Action<BuffArgs> handler, HookPriority priority = HookPriority.Normal);
    /// <summary>As OnApply, for one buff (the spell's spells.xml uniquename, or a mask with * and ?).</summary>
    IHookRegistration OnApply(string buff, Action<BuffArgs> handler, HookPriority priority = HookPriority.Normal);

    /// <summary>A buff's stacks changed (re-applied, charges consumed).</summary>
    IHookRegistration OnStackChange(Action<BuffStackArgs> handler, HookPriority priority = HookPriority.Normal);
    /// <summary>As OnStackChange, for one buff (the spell's spells.xml uniquename, or a mask with * and ?).</summary>
    IHookRegistration OnStackChange(string buff, Action<BuffStackArgs> handler, HookPriority priority = HookPriority.Normal);

    /// <summary>A tick of a buff's over-time effect (damage/heal over time, a pulse).</summary>
    IHookRegistration OnTick(Action<BuffArgs> handler, HookPriority priority = HookPriority.Normal);
    /// <summary>As OnTick, for one buff (the spell's spells.xml uniquename, or a mask with * and ?).</summary>
    IHookRegistration OnTick(string buff, Action<BuffArgs> handler, HookPriority priority = HookPriority.Normal);

    /// <summary>The buff went off: expired, removed (a cleanse, a dispel, a module), or cleared (death, reset, knockdown).</summary>
    IHookRegistration OnRemove(Action<BuffRemoveArgs> handler, HookPriority priority = HookPriority.Normal);
    /// <summary>As OnRemove, for one buff (the spell's spells.xml uniquename, or a mask with * and ?).</summary>
    IHookRegistration OnRemove(string buff, Action<BuffRemoveArgs> handler, HookPriority priority = HookPriority.Normal);
}

/// <summary>A spell's timed effects are about to go on a unit (OnBeforeApply): Cancel keeps them off.</summary>
public sealed class BeforeBuffArgs(IUnit unit, SpellInfo buff, IUnit? caster) : CancellableArgs
{
    /// <summary>The unit it would go on.</summary>
    public IUnit Unit { get; } = unit;
    /// <summary>The spell's uniquename.</summary>
    public SpellInfo Buff { get; } = buff;

    /// <summary>Who applies it (null: the world, or a caster no longer on the map).</summary>
    public IUnit? Caster { get; } = caster;
}

/// <summary>A spell entry on a unit: what OnApply and OnTick get, and the base of the other buff args.</summary>
public class BuffArgs(IUnit unit, IBuff buff, bool isNew)
{
    /// <summary>The unit it is on.</summary>
    public IUnit Unit { get; } = unit;
    /// <summary>The entry: stacks, time left, caster and this module's data on it. Valid only during the call; keep the unit, not the buff.</summary>
    public IBuff Buff { get; } = buff;

    /// <summary>OnApply: a new entry (else a re-application: a stack more and a restart).</summary>
    public bool IsNew { get; } = isNew;
}

/// <summary>The stacks of a spell entry changed (<see cref="IBuffHooks.OnStackChange(Action{BuffStackArgs}, HookPriority)"/>).</summary>
public sealed class BuffStackArgs(IUnit unit, IBuff buff, int before) : BuffArgs(unit, buff, false)
{
    /// <summary>The stacks before the change.</summary>
    public int Before { get; } = before;
    /// <summary>The stacks now.</summary>
    public int After => Buff.Stacks;
}

/// <summary>How a spell entry left its unit (<see cref="BuffRemoveArgs.How"/>).</summary>
public enum BuffEnd
{
    /// <summary>Its time ran out.</summary>
    Expired,
    /// <summary>It was taken off: a cleanse, a dispel, a module.</summary>
    Removed,
    /// <summary>The unit's whole list was cleared: death, reset, knockdown.</summary>
    Cleared,
}

/// <summary>A spell entry left its unit (<see cref="IBuffHooks.OnRemove(Action{BuffRemoveArgs}, HookPriority)"/>).</summary>
public sealed class BuffRemoveArgs(IUnit unit, IBuff buff, BuffEnd how) : BuffArgs(unit, buff, false)
{
    /// <summary>How it ended.</summary>
    public BuffEnd How { get; } = how;
}
