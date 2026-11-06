using System.Globalization;
using AlbionLocal.Server.Persistence;

namespace AlbionLocal.Server.Commands.Builtin;

/// <summary>.tick (GameMaster): where the world tick's time goes, by section of World.Update (<see cref="TickProfile"/>).</summary>
public static class TickCommands
{
    [Command("tick", Level = AccountLevel.GameMaster,
        Help = "the world tick's average and peak (ms) by section since the start or the last .tick reset; .tick reset starts anew")]
    public static void Show(CommandContext ctx, string reset = "")
    {
        var profile = ctx.World.Profile;
        if (reset.Equals("reset", StringComparison.OrdinalIgnoreCase))
        {
            profile.Reset();
            ctx.Reply("tick profile reset");
            return;
        }
        if (reset.Length > 0) throw new CommandError($"'{reset}' is not reset");
        var (average, max, sections) = profile.Summary();
        ctx.Reply(string.Create(CultureInfo.InvariantCulture,
            $"{profile.Ticks} ticks of {ctx.World.Config.TickTime.TotalMilliseconds:0} ms: average {average:0.00} ms, peak {max:0.0} ms"));
        foreach (var (name, avg, peak) in sections.Take(6))
            ctx.Reply(string.Create(CultureInfo.InvariantCulture, $"  {name}: {avg:0.000} ms, peak {peak:0.0} ms"));
    }
}
