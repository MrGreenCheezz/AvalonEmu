using AlbionLocal.Server.Persistence;

namespace AlbionLocal.Server.Commands.Builtin;

/// <summary>The C# plugins (server/plugins/README.md): .plugins list, .plugins reload, .plugins enable, .plugins disable.</summary>
public static class PluginCommands
{
    /// <summary>.plugins [list]: every plugin found (Plugins:Path, the packs' plugins/) with its version, state and errors.</summary>
    [Command("plugins", Level = AccountLevel.GameMaster, Help = "the plugins found with their state (.plugins list|reload|enable|disable)")]
    public static void Status(CommandContext ctx) => List(ctx);

    [Command("plugins list", Level = AccountLevel.GameMaster, Help = "the plugins found with their version, state and errors")]
    public static void List(CommandContext ctx)
    {
        var host = ctx.World.Plugins;
        host.Discover();
        var lines = host.Describe().ToList();
        ctx.Reply((ctx.World.Config.Plugins.Enabled ? "" : "Plugins:Enabled is false: nothing loads at the start; .plugins enable still works\n")
            + (lines.Count == 0 ? $"no plugins in {host.Options.Path}" : string.Join("\n", lines)));
    }

    /// <summary>.plugins reload &lt;name&gt;: unload (handlers, commands, timers dropped), build when its sources changed (in the background), load again.</summary>
    [Command("plugins reload", Level = AccountLevel.Admin, Help = "unloads a plugin, builds it again when its sources changed, and loads it")]
    public static void Reload(CommandContext ctx, string name)
    {
        var was = ctx.World.Plugins.IsLoaded(name);
        ctx.Reply($"{(was ? "reloading" : "loading")} {name} (a build may take a few seconds)");
        ctx.World.Plugins.Reload(name, why =>
        {
            if (why is null) ctx.Reply($"{name} loaded");
            else ctx.Error($"{name} not loaded: {why}");
        });
    }

    /// <summary>.plugins enable &lt;name&gt;: loads a plugin that is not loaded (until the next start: plugin.json "enabled" or Plugins:List keep it).</summary>
    [Command("plugins enable", Level = AccountLevel.Admin, Help = "loads a plugin now (until the restart; plugin.json \"enabled\" or Plugins:List make it stay)")]
    public static void Enable(CommandContext ctx, string name)
    {
        if (ctx.World.Plugins.IsLoaded(name)) throw new CommandError($"{name} is loaded already (.plugins reload {name} loads it anew)");
        Reload(ctx, name);
    }

    /// <summary>.plugins disable &lt;name&gt;: unloads a plugin (until the next start).</summary>
    [Command("plugins disable", Level = AccountLevel.Admin, Help = "unloads a plugin now (until the restart)")]
    public static void Disable(CommandContext ctx, string name)
    {
        if (!ctx.World.Plugins.Unload(name, $"disabled by {(ctx.Console ? "the console" : ctx.Player?.Character.Name ?? "a GM")}"))
            throw new CommandError($"{name} is not loaded");
        ctx.Reply($"{name} unloaded");
    }
}
