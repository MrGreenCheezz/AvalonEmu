using System.Globalization;
using AlbionLocal.Server.Persistence;

namespace AlbionLocal.Server.Commands.Builtin;

/// <summary>StageN faction (analysis/work/stageN/faction/SPEC.md): .faction shows the enlistment, standing, rank and points; .faction add gives points.</summary>
public static class FactionCommands
{
    /// <summary>.faction: the enlisted faction, when a switch is allowed again, and per faction with any the standing, rank and points.</summary>
    [Command("faction", Level = AccountLevel.Player, Help = "your faction enlistment, standing, rank and faction points")]
    public static void Show(CommandContext ctx)
    {
        var player = ctx.RequirePlayer();
        var factions = ctx.World.Factions;
        if (factions.Data is not { } data) throw new CommandError("faction warfare is off (factionwarfare.bin not read)");
        var c = player.Character;
        var next = c.FactionChangedAt + factions.Cooldown.Ticks - ctx.World.GameNow;
        ctx.Reply($"enlisted: {factions.Name(player.Faction)}{(player.Faction != 0 && next > 0 ? string.Create(CultureInfo.InvariantCulture, $" (switch again in {TimeSpan.FromTicks(next).TotalMinutes:0} min)") : "")}"
            + $"; points are earned flagged to it in a faction warfare cluster{(player.Map is { } map ? $" (here: {(data.IsWarzone(map.Cluster) ? "yes" : "no")})" : "")}");
        for (byte f = 1; f <= 6; f++)
            if (c.FactionStanding[f - 1] > 0 || c.FactionPoints[f - 1] > 0 || f == player.Faction)
                ctx.Reply(string.Create(CultureInfo.InvariantCulture,
                    $"  {factions.Name(f)}: standing {c.FactionStanding[f - 1] / 10000:N0} (rank {data.RankOf(c.FactionStanding[f - 1])} of {data.Ranks.Count}), points {c.FactionPoints[f - 1] / 10000:N0}"));
    }

    /// <summary>.faction add &lt;points&gt;: the enlisted faction gets that many base points (× the rank's multiplier) and the standing they give.</summary>
    [Command("faction add", Level = AccountLevel.GameMaster, Help = "gives your enlisted faction <points> base faction points and their standing")]
    public static void Add(CommandContext ctx, long points)
    {
        var player = ctx.RequirePlayer();
        if (player.Faction == 0) throw new CommandError("not enlisted (.pvp faction or a faction trader)");
        if (points <= 0) throw new CommandError("points above 0");
        ctx.World.Factions.Earn(player, checked(points * 10000), null);
        Show(ctx);
    }
}
