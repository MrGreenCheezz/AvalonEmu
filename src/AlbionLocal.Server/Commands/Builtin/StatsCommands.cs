using AlbionLocal.Server.Persistence;
using AlbionLocal.Server.Systems;

namespace AlbionLocal.Server.Commands.Builtin;

/// <summary>StageR stats (analysis/work/stageR/stats/SPEC.md): .might and .kills for every player, .might add for GMs.</summary>
public static class StatsCommands
{
    /// <summary>.might: this week's might, the weekly chest and the season's personal might (<see cref="Conqueror"/>).</summary>
    [Command("might", Level = AccountLevel.Player, Help = "your Conqueror's Challenge might this week, the weekly chest and your season might")]
    public static void Might(CommandContext ctx) => ctx.Reply(Conqueror.Describe(ctx.RequirePlayer()));

    /// <summary>.might add &lt;might&gt;: whole might for the player as a PvE kill's (PVE), with its weekly rewards and chest (a test of the window).</summary>
    [Command("might add", Level = AccountLevel.GameMaster, Help = "gives you <might> personal might (PVE) with its weekly rewards")]
    public static void Add(CommandContext ctx, long might)
    {
        if (might <= 0) throw new CommandError("might > 0");
        var p = ctx.RequirePlayer();
        var type = ctx.World.GvgSeasons?.Might.Fame.GetValueOrDefault("pve")?.Objective ?? "PVPCHALLENGE_PVE";
        Conqueror.Add(p, type, null, might * 10000.0);
        ctx.Reply(Conqueror.Describe(p));
    }

    /// <summary>.kills: the counts of the player's kill history lists (<see cref="KillHistory"/>).</summary>
    [Command("kills", Level = AccountLevel.Player, Help = "the counts of your kill history (kills, deaths, knockdowns)")]
    public static void Kills(CommandContext ctx) => ctx.Reply(ctx.World.KillHistory.Describe(ctx.RequirePlayer()));
}
