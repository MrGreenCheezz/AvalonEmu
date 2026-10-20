namespace AlbionLocal.Server.Plugins;

/// <summary>The no-code rules (server/plugins/RULES.md) ride on the plugin host: one built-in plugin, "rules".</summary>
public sealed partial class PluginHost
{
    private AlbionLocal.Server.Rules.RulesHost? _rulesHost;

    /// <summary>The rules of the packs in Mods:Packs (<see cref="AlbionLocal.Server.Rules.RulesHost"/>); <c>.rules reload</c>, the Studio's saves.</summary>
    public AlbionLocal.Server.Rules.RulesHost Rules => _rulesHost ??= new AlbionLocal.Server.Rules.RulesHost(this);

    /// <summary>Runs <paramref name="action"/> as loaded plugin <paramref name="name"/> (its data bags and timers), guarded; plainly when it is not loaded.</summary>
    internal void RunAs(string name, Action action)
    {
        if (_loaded.FirstOrDefault(c => !c.Dropped && c.Name.Equals(name, StringComparison.OrdinalIgnoreCase)) is { } ctx)
        {
            var outer = _current;
            _current = ctx;
            try { action(); }
            finally { _current = outer; }
        }
        else action();
    }
}
