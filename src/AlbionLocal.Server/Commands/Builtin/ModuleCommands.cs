using System.Globalization;
using AlbionLocal.Server.Modules;
using AlbionLocal.Server.Persistence;

namespace AlbionLocal.Server.Commands.Builtin;

/// <summary>The server modules (docs/modules/README.md): .modules list, info, reload, enable, disable, doctor, hooks, trace, timings, ops.</summary>
public static class ModuleCommands
{
    /// <summary>.modules [list]: every module found (Modules:Path) with its version, state and errors.</summary>
    [Command("modules", Level = AccountLevel.GameMaster, Help = "the modules found with their state (.modules list|info|reload|enable|disable|doctor|hooks|trace|timings|ops)")]
    public static void Status(CommandContext ctx) => List(ctx);

    [Command("modules list", Level = AccountLevel.GameMaster, Help = "the modules found with their version, state and errors")]
    public static void List(CommandContext ctx)
    {
        var host = ctx.World.Modules;
        host.Discover();
        var lines = host.Describe().ToList();
        ctx.Reply((ctx.World.Config.Modules.Enabled ? "" : "Modules:Enabled is false: nothing loads at the start; .modules enable still works\n")
            + (lines.Count == 0 ? $"no modules in {host.Options.Path} (AlbionLocal.Server --new-module mod-x makes one)" : string.Join("\n", lines)));
    }

    /// <summary>.modules info &lt;id&gt;: what one module is and what it has connected.</summary>
    [Command("modules info", Level = AccountLevel.GameMaster, Help = "one module: its manifest, state, hooks, commands and timers")]
    public static void Info(CommandContext ctx, string id) => ctx.Reply(ctx.World.Modules.Info(id));

    /// <summary>.modules reload &lt;id&gt;: unload (hooks, commands, timers dropped), build when its sources changed (in the background), load again.</summary>
    [Command("modules reload", Level = AccountLevel.Admin, Help = "unloads a module, builds it again when its sources changed, and loads it")]
    public static void Reload(CommandContext ctx, string id)
    {
        var was = ctx.World.Modules.IsLoaded(id);
        ctx.Reply($"{(was ? "reloading" : "loading")} {id} (a build may take a few seconds)");
        ctx.World.Modules.Reload(id, why =>
        {
            if (why is null) ctx.Reply($"{id} loaded");
            else ctx.Error($"{id} not loaded: {why}");
        });
    }

    /// <summary>.modules enable &lt;id&gt;: loads a module that is not loaded (until the next start: "Enabled" in its settings or Modules:List keep it).</summary>
    [Command("modules enable", Level = AccountLevel.Admin, Help = "loads a module now (until the restart; its settings' \"Enabled\" or Modules:List make it stay)")]
    public static void Enable(CommandContext ctx, string id)
    {
        if (ctx.World.Modules.IsLoaded(id)) throw new CommandError($"{id} is loaded already (.modules reload {id} loads it anew)");
        Reload(ctx, id);
    }

    /// <summary>.modules disable &lt;id&gt;: unloads a module (until the next start).</summary>
    [Command("modules disable", Level = AccountLevel.Admin, Help = "unloads a module now (until the restart)")]
    public static void Disable(CommandContext ctx, string id)
    {
        if (!ctx.World.Modules.Unload(id, $"disabled by {(ctx.Console ? "the console" : ctx.Player?.Character.Name ?? "a GM")}"))
            throw new CommandError($"{id} is not loaded");
        ctx.Reply($"{id} unloaded");
    }

    /// <summary>.modules doctor: the checks of --check-modules: manifests, versions, dependencies, settings.</summary>
    [Command("modules doctor", Level = AccountLevel.GameMaster, Help = "checks every module's manifest, API version, dependencies and settings, and says what to fix")]
    public static void Doctor(CommandContext ctx)
    {
        using var text = new StringWriter(CultureInfo.InvariantCulture);
        ModuleDoctor.Run(ctx.World.Config, text);
        ctx.Reply(text.ToString().TrimEnd());
    }

    /// <summary>.modules hooks [filter]: the server's hooks with who listens to each.</summary>
    [Command("modules hooks", Level = AccountLevel.GameMaster, Help = "the hooks of the server with who listens (.modules hooks spells, .modules hooks damage)")]
    public static void Hooks(CommandContext ctx, string? filter = null)
    {
        var lines = ctx.World.Modules.Hooks.All.Where(p => filter is null || p.Name.Contains(filter, StringComparison.OrdinalIgnoreCase)).Select(p =>
        {
            var listeners = p.Entries().Select(e => e.Module + (e.Id is null ? "" : $"[{e.Id}]")).Distinct().ToList();
            return $"{p.Name}{(listeners.Count > 0 ? ": " + string.Join(", ", listeners) : "")}";
        }).ToList();
        if (lines.Count == 0) throw new CommandError($"no hook has {filter} in its name");
        ctx.Reply(string.Join("\n", lines));
    }

    /// <summary>.modules trace &lt;hook&gt;: logs every handler run of a hook with its module, time and whether it cancelled; again to stop, "off" for all.</summary>
    [Command("modules trace", Level = AccountLevel.GameMaster, Help = "logs every handler run of a hook (.modules trace Combat.OnBeforeDamage; again to stop; off stops all)")]
    public static void Trace(CommandContext ctx, string hook)
    {
        var host = ctx.World.Modules;
        if (hook.Equals("off", StringComparison.OrdinalIgnoreCase))
        {
            foreach (var p in host.Hooks.All) p.Traced = false;
            ctx.Reply("no hook is traced now");
            return;
        }
        if (host.Hooks.Find(hook) is not { } point)
            throw new CommandError($"no hook {hook}{(ModuleHost.Nearest(hook, host.Hooks.All.Select(p => p.Name)) is { } near ? $" (did you mean {near}?)" : "")}; .modules hooks lists them");
        point.Traced = !point.Traced;
        ctx.Reply($"{point.Name}: tracing {(point.Traced ? "on: see the server log (trace)" : "off")}");
    }

    /// <summary>.modules timings: the handlers by the time they have taken so far.</summary>
    [Command("modules timings", Level = AccountLevel.GameMaster, Help = "the module handlers by the time they have taken so far")]
    public static void Timings(CommandContext ctx)
    {
        var lines = ctx.World.Modules.Hooks.All.SelectMany(p => p.Entries()).Where(e => e.Calls > 0).OrderByDescending(e => e.Milliseconds).Take(30)
            .Select(e => string.Create(CultureInfo.InvariantCulture, $"{e.Milliseconds,9:0.0} ms {e.Calls,8} calls  {e.Module}: {e.Hook}{(e.Id is null ? "" : $"[{e.Id}]")}")).ToList();
        ctx.Reply(lines.Count == 0 ? "no module handler has run yet" : string.Join("\n", lines));
    }

    /// <summary>.modules ops [filter]: the operation and event names of the network hooks.</summary>
    [Command("modules ops", Level = AccountLevel.GameMaster, Help = "the operation and event names the network hooks take (.modules ops cast)")]
    public static void Ops(CommandContext ctx, string? filter = null)
    {
        IEnumerable<string> Pick(IEnumerable<string> names) => names.Where(n => filter is null || n.Contains(filter, StringComparison.OrdinalIgnoreCase)).Order(StringComparer.Ordinal);
        ctx.Reply($"operations: {string.Join(", ", Pick(ModuleNetwork.OperationNames))}\nevents: {string.Join(", ", Pick(ModuleNetwork.EventNames))}");
    }
}
