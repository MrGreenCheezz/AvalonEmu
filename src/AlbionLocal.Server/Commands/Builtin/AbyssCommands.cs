using AlbionLocal.Server.Entities;
using AlbionLocal.Server.Persistence;

namespace AlbionLocal.Server.Commands.Builtin;

/// <summary>The Abyssal Depths (analysis/work/stageR/abyss/SPEC.md): .abyss list, .abyss open / close (the Hell Den's entrance), .abyss shut (a floor).</summary>
public static class AbyssCommands
{
    /// <summary>.abyss list: the Hell Dens' entrances and the live floors.</summary>
    [Command("abyss list", Level = AccountLevel.GameMaster, Help = "lists the Abyssal Depths entrances and the live floors")]
    public static void List(CommandContext ctx)
    {
        var lines = ctx.World.Abyss.Describe().ToList();
        ctx.Reply(lines.Count == 0 ? "no Abyssal Depths entrance loaded and no floor (stand in a Hell Den: Antiquarian's Den)" : string.Join("\n", lines));
    }

    /// <summary>.abyss open [seconds]: the entrance of the Hell Den the GM stands in Open for that long (default 300), then its cycle goes on.</summary>
    [Command("abyss open", Level = AccountLevel.GameMaster, Help = "opens the Abyssal Depths entrance of this Hell Den: [seconds, default 300]")]
    public static void Open(CommandContext ctx, int seconds = 300) => Set(ctx, AbyssEntranceState.Open, seconds);

    /// <summary>.abyss close [seconds]: the same, Closed.</summary>
    [Command("abyss close", Level = AccountLevel.GameMaster, Help = "closes the Abyssal Depths entrance of this Hell Den: [seconds, default 300]")]
    public static void Close(CommandContext ctx, int seconds = 300) => Set(ctx, AbyssEntranceState.Closed, seconds);

    private static void Set(CommandContext ctx, AbyssEntranceState state, int seconds)
    {
        var player = ctx.RequirePlayer();
        var entrance = ctx.World.Abyss.Entrances.FirstOrDefault(e => e.Map == player.Map) ?? throw new CommandError("no Abyssal Depths entrance on this map (a Hell Den has one)");
        ctx.World.Abyss.Force(entrance, state, Math.Clamp(seconds, 1, 86400));
        ctx.Reply($"{entrance} for {seconds:0} s");
    }

    /// <summary>.abyss shut: closes the floor the GM stands in (everyone out to the Hell Den).</summary>
    [Command("abyss shut", Level = AccountLevel.GameMaster, Help = "closes the Abyssal Depths floor you are in")]
    public static void Shut(CommandContext ctx)
    {
        var player = ctx.RequirePlayer();
        if (ctx.World.Abyss.Find(player.Map?.Cluster) is not { } instance) throw new CommandError("you are in no Abyssal Depths floor");
        ctx.World.Abyss.Close(instance, "closed by a GM");
        ctx.Reply($"{instance} closed");
    }
}
