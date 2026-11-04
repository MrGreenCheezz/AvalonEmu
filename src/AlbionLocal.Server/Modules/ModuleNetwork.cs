using System.Reflection;
using System.Runtime.CompilerServices;
using AlbionLocal.Server.Protocol;

namespace AlbionLocal.Server.Modules;

/// <summary>The protocol's names as the network hooks use them.</summary>
internal static class ModuleNetwork
{
    /// <summary>By the event's class, weakly: a module's own event class must not keep its code in memory.</summary>
    private static readonly ConditionalWeakTable<Type, Described> Events = new();

    private sealed record Described(string Name, int Code);

    /// <summary>Every operation's name (the enum OperationCodes).</summary>
    public static readonly IReadOnlyCollection<string> OperationNames = new HashSet<string>(Enum.GetNames<OperationCodes>(), StringComparer.OrdinalIgnoreCase);

    /// <summary>Every event's name (the enum EventCodes).</summary>
    public static readonly IReadOnlyCollection<string> EventNames = new HashSet<string>(Enum.GetNames<EventCodes>(), StringComparer.OrdinalIgnoreCase);

    /// <summary>The name and code of <paramref name="e"/>'s event: the protocol's, or for a class without a code its class name without "Event".</summary>
    public static (string Name, int Code) Describe(AlbionEvent e)
    {
        var d = Events.GetValue(e.GetType(), static t =>
        {
            if (t.GetCustomAttribute<AlbionEventAttribute>() is { } a) return new Described(a.Code.ToString(), (int)a.Code);
            var name = t.Name;
            return new Described(name.EndsWith("Event", StringComparison.Ordinal) ? name[..^5] : name, -1);
        });
        return (d.Name, d.Code);
    }
}
