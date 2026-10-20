using AlbionLocal.Server.Bots;
using AlbionLocal.Server.Persistence;

namespace AlbionLocal.Server.Commands.Builtin;

/// <summary>StageN companions (analysis/work/stageN/companions/SPEC.md §4): .companion hire, .companion dismiss, .companion list, for every player.</summary>
public static class CompanionCommands
{
    /// <summary>.companion hire &lt;tank|healer|melee|ranged&gt; [tier]: a companion of that kind joins the player's party.</summary>
    [Command("companion hire", Level = AccountLevel.Player,
        Help = "hires a companion bot into your party: tank, healer, melee or ranged [gear tier 1-8; default from your item power]")]
    public static void Hire(CommandContext ctx, string role, int tier = 0)
    {
        var player = ctx.RequirePlayer();
        if (Companions.ParseKind(role) is not { } kind) throw new CommandError($"'{role}' is not a companion role: tank, healer, melee or ranged");
        if (tier is < 0 or > 8) throw new CommandError("the tier is 1-8 (or leave it out)");
        if (ctx.World.Companions.Hire(player, kind, tier == 0 ? null : tier, out var bot) is { } why) throw new CommandError($"no companion: {why}");
        ctx.Reply($"{bot!.Character.Name} ({kind}, T{bot.Bot!.Tier}) joins your party; .companion dismiss {bot.Character.Name} sends it away");
    }

    /// <summary>.companion dismiss [name|role|number|all]: the player's companions named go (all of them without an argument).</summary>
    [Command("companion dismiss", Level = AccountLevel.Player, Help = "sends companions away: a name, a role, a number of .companion list, or all (default)")]
    public static void Dismiss(CommandContext ctx, string which = "all")
    {
        var player = ctx.RequirePlayer();
        var n = ctx.World.Companions.Dismiss(player, which);
        if (n == 0) throw new CommandError(ctx.World.Companions.HasAny(player.Character.Id) ? $"no companion '{which}'" : "you have no companion");
        ctx.Reply($"{n} companion(s) dismissed");
    }

    /// <summary>.companion list: the player's companions (kind, tier, health, what each does).</summary>
    [Command("companion list", Level = AccountLevel.Player, Help = "your companions: role, tier, health, what each does")]
    public static void List(CommandContext ctx)
    {
        var player = ctx.RequirePlayer();
        var lines = ctx.World.Companions.Describe(player.Character.Id).ToList();
        var options = ctx.World.Config.Companions;
        ctx.Reply(lines.Count == 0
            ? $"no companion; .companion hire <tank|healer|melee|ranged> [tier] (up to {options.MaxPerPlayer}{(options.Enabled ? "" : ", off on this server")})"
            : $"companions:\n{string.Join("\n", lines)}");
    }
}
