using AlbionLocal.Server.Persistence;

namespace AlbionLocal.Server.Commands.Builtin;

/// <summary>The no-code rules (server/plugins/RULES.md): .rules, .rules reload, .rules problems.</summary>
public static class RulesCommands
{
    /// <summary>.rules [list]: the rules running, by pack, with their fires and errors.</summary>
    [Command("rules", Level = AccountLevel.GameMaster, Help = "the no-code rules running (.rules reload|problems)")]
    public static void List(CommandContext ctx)
    {
        var host = ctx.World.Plugins.Rules;
        var lines = new List<string> { host.Summary() };
        foreach (var r in host.Rules.OrderBy(r => r.Key, StringComparer.Ordinal))
            lines.Add($"{r.Key} [{r.Trigger}{(r.Filter is null ? "" : " " + r.Filter)}] {(!r.Enabled ? "off" : r.Disabled ? "SWITCHED OFF: " + r.DisabledWhy : "on")}"
                + $", fired {r.Fires}{(r.Errors > 0 ? $", {r.Errors} error(s)" : "")} - {r.Name}");
        ctx.Reply(string.Join("\n", lines));
    }

    /// <summary>.rules reload: reads mods/&lt;pack&gt;/rules/ of the packs in Mods:Packs again and restarts the rules (no server restart).</summary>
    [Command("rules reload", Level = AccountLevel.Admin, Help = "reads the packs' rules again and applies them now")]
    public static void Reload(CommandContext ctx)
    {
        var host = ctx.World.Plugins.Rules;
        ctx.Reply(host.Reload());
        foreach (var p in host.Problems.Where(p => p.Blocker).Take(10)) ctx.Error("not loaded: " + p);
    }

    /// <summary>.rules problems: why rules were left out or switched off, and their warnings.</summary>
    [Command("rules problems", Level = AccountLevel.GameMaster, Help = "why rules were not loaded or were switched off")]
    public static void Problems(CommandContext ctx)
    {
        var problems = ctx.World.Plugins.Rules.Problems;
        ctx.Reply(problems.Count == 0 ? "no problems" : string.Join("\n", problems.Take(30).Select(p => $"{(p.Blocker ? "⛔" : "⚠")} {p}")));
    }
}
