namespace AlbionLocal.Modules;

// The model every hook follows (docs/modules/README.md "Hooks"):
//   OnBefore*  something is about to happen: Cancel() stops it, its values may be changed.
//   OnAfter*, On*  it happened: read only.
//   OnCalc*    the engine is about to use a number: set Value.
// A hook can be registered for everything or for one id (a spell, buff, item, mount or mob uniquename, or a mask with * and ?), in a
// *Script class (override the method) or with a lambda (Hooks.Combat.OnBeforeDamage(e => ...)). A hook nobody listens to costs one check.
// Handlers run on the world thread; an exception is logged and counted and the game goes on. Args are valid only during the call.

/// <summary>When a handler runs among the others of one hook. Equal priorities run in the order the modules were loaded.</summary>
public enum HookPriority
{
    /// <summary>Before everything else.</summary>
    First = -200,

    /// <summary>Before the normal handlers: a filter that others should see the result of.</summary>
    Early = -100,

    /// <summary>The default.</summary>
    Normal = 0,

    /// <summary>After the normal handlers: a final adjustment.</summary>
    Late = 100,

    /// <summary>The last handler that may still change the outcome.</summary>
    Last = 200,

    /// <summary>Only watches the final outcome (logging, statistics): always runs, also for a cancelled event, and its Cancel is ignored.</summary>
    Monitor = 1000,
}

/// <summary>A registered hook handler. Dispose removes it; everything a module registered goes at its unload anyway.</summary>
public interface IHookRegistration : IDisposable
{
    /// <summary>The id or mask it listens to; null = all.</summary>
    string? Id { get; }

    /// <summary>Also runs for an event another handler has cancelled (by default it is skipped), for example to undo that with <see cref="ICancellableArgs.Uncancel"/>.</summary>
    IHookRegistration ReceiveCancelled();
}

/// <summary>Args of a hook that can stop what is about to happen (every <c>OnBefore*</c>).</summary>
public interface ICancellableArgs
{
    /// <summary>A handler has stopped it.</summary>
    bool IsCancelled { get; }

    /// <summary>Why, as told to the player when it is not empty.</summary>
    string? CancelReason { get; }

    /// <summary>The module that stopped it (handy in logs and with <see cref="ReceiveCancelledAttribute"/>).</summary>
    string? CancelledBy { get; }

    /// <summary>Stops it. The engine still sends the client what a refusal needs. <paramref name="reason"/> goes to the player as an error line when the hook has one.</summary>
    void Cancel(string? reason = null);

    /// <summary>Takes back a cancel of an earlier handler.</summary>
    void Uncancel();

    internal void MarkCancelledBy(string module);
}

/// <summary>The cancel state the bases below share.</summary>
internal struct CancelState
{
    public bool Cancelled;
    public string? Reason;
    public string? By;

    public void Cancel(string? reason)
    {
        Cancelled = true;
        Reason ??= reason;
    }

    public void Uncancel() => this = default;
}

/// <summary>A base of hook args that can be cancelled (see <see cref="ICancellableArgs"/>).</summary>
public abstract class CancellableArgs : ICancellableArgs
{
    private CancelState _state;

    /// <summary>A handler has stopped it.</summary>
    public bool IsCancelled => _state.Cancelled;
    /// <summary>Why it was stopped, as told to the player when it is not empty; null when no reason was given.</summary>
    public string? CancelReason => _state.Reason;
    /// <summary>The id of the module that stopped it; null when none did.</summary>
    public string? CancelledBy => _state.By;
    /// <summary>Stops it. <paramref name="reason"/> goes to the player as an error line when the hook has one; the first reason given is kept.</summary>
    public void Cancel(string? reason = null) => _state.Cancel(reason);
    /// <summary>Takes back a cancel of an earlier handler, with its reason.</summary>
    public void Uncancel() => _state.Uncancel();
    void ICancellableArgs.MarkCancelledBy(string module) => _state.By ??= module;
}

/// <summary>The same for args about a cast.</summary>
public abstract class CancellableSpellArgs(IUnit caster, SpellInfo spell, byte slot, ItemType? item, bool mount, IUnit? target, float x, float y)
    : SpellArgsBase(caster, spell, slot, item, mount, target, x, y), ICancellableArgs
{
    private CancelState _state;

    /// <summary>A handler has stopped it.</summary>
    public bool IsCancelled => _state.Cancelled;
    /// <summary>Why it was stopped, as told to the player when it is not empty; null when no reason was given.</summary>
    public string? CancelReason => _state.Reason;
    /// <summary>The id of the module that stopped it; null when none did.</summary>
    public string? CancelledBy => _state.By;
    /// <summary>Stops it. <paramref name="reason"/> goes to the player as an error line when the hook has one; the first reason given is kept.</summary>
    public void Cancel(string? reason = null) => _state.Cancel(reason);
    /// <summary>Takes back a cancel of an earlier handler, with its reason.</summary>
    public void Uncancel() => _state.Uncancel();
    void ICancellableArgs.MarkCancelledBy(string module) => _state.By ??= module;
}

/// <summary>The same for args about a player.</summary>
public abstract class CancellablePlayerArgs(IPlayer player) : PlayerArgs(player), ICancellableArgs
{
    private CancelState _state;

    /// <summary>A handler has stopped it.</summary>
    public bool IsCancelled => _state.Cancelled;
    /// <summary>Why it was stopped, as told to the player when it is not empty; null when no reason was given.</summary>
    public string? CancelReason => _state.Reason;
    /// <summary>The id of the module that stopped it; null when none did.</summary>
    public string? CancelledBy => _state.By;
    /// <summary>Stops it. <paramref name="reason"/> goes to the player as an error line when the hook has one; the first reason given is kept.</summary>
    public void Cancel(string? reason = null) => _state.Cancel(reason);
    /// <summary>Takes back a cancel of an earlier handler, with its reason.</summary>
    public void Uncancel() => _state.Uncancel();
    void ICancellableArgs.MarkCancelledBy(string module) => _state.By ??= module;
}

/// <summary>Limits a *Script method (or all of its class) to these ids: exact uniquenames or masks with * and ?. Without it the method runs for everything.</summary>
[AttributeUsage(AttributeTargets.Class | AttributeTargets.Method, AllowMultiple = true, Inherited = false)]
public sealed class ForIdAttribute(string id) : Attribute
{
    /// <summary>The uniquename or mask given to the attribute.</summary>
    public string Id { get; } = id;
}

/// <summary>When this *Script method runs among the other handlers (the default is <see cref="HookPriority.Normal"/>).</summary>
[AttributeUsage(AttributeTargets.Method, Inherited = false)]
public sealed class PriorityAttribute(HookPriority priority) : Attribute
{
    /// <summary>The priority given to the attribute.</summary>
    public HookPriority Priority { get; } = priority;
}

/// <summary>This *Script method also runs for an event another handler has cancelled.</summary>
[AttributeUsage(AttributeTargets.Method, Inherited = false)]
public sealed class ReceiveCancelledAttribute : Attribute;

/// <summary>Marks a *Script method as the hook of that name. Used by the base classes; a module only overrides the method.</summary>
[AttributeUsage(AttributeTargets.Method, Inherited = false)]
public sealed class HookAttribute(string name) : Attribute
{
    /// <summary>The hook's name, "Group.Method" ("Combat.OnBeforeDamage").</summary>
    public string Name { get; } = name;
}

/// <summary>The spell slot of a cast (Albion's CharacterSpellSlot; the keys are the default key bindings).</summary>
public enum SpellSlot
{
    /// <summary>Q: the main hand's first spell.</summary>
    Q = 0,
    /// <summary>W: the main hand's second spell.</summary>
    W = 1,
    /// <summary>E: the off hand's or the two-handed weapon's third spell.</summary>
    E = 2,
    /// <summary>R: the armour's spell.</summary>
    Armor = 3,
    /// <summary>D: the helmet's spell.</summary>
    Head = 4,
    /// <summary>F: the shoes' spell.</summary>
    Shoes = 5,
    /// <summary>The potion slot.</summary>
    Potion = 12,
    /// <summary>The food slot.</summary>
    Food = 13,

    /// <summary>Any other slot (a mob's cast, a server-side cast).</summary>
    Other = 255,
}

/// <summary>Helpers for <see cref="SpellSlot"/>.</summary>
public static class SpellSlots
{
    /// <summary>A cast's raw slot index as a <see cref="SpellSlot"/>: 0-5, 12, 13 the CharacterSpellSlots, else Other.</summary>
    public static SpellSlot FromIndex(byte index) => index is <= 5 or 12 or 13 ? (SpellSlot)index : SpellSlot.Other;
}
