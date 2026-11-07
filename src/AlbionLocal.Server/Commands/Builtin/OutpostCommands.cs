using AlbionLocal.Server.Persistence;

namespace AlbionLocal.Server.Commands.Builtin;

/// <summary>StageQ outposts (<see cref="Systems.Outposts"/>): .outpost lists the faction warfare towers of the map; .outpost owner / clear for GMs.</summary>
public static class OutpostCommands
{
    /// <summary>.outpost: the zone's controller and front line, each tower's owner, points, claim, contest and guards, nearest first.</summary>
    [Command("outpost", Level = AccountLevel.Player, Help = "the faction warfare towers of this zone: owner, points, claim state, contest, guards")]
    public static void List(CommandContext ctx)
    {
        var player = ctx.RequirePlayer();
        if (player.Map is not { } map) throw new CommandError("not on a map");
        foreach (var line in ctx.World.Outposts.Describe(map, player.PositionAt(DateTime.UtcNow))) ctx.Reply(line);
    }

    /// <summary>.outpost owner &lt;faction 1-6&gt;: the nearest tower is that faction's (as a capture: 367, its guards anew).</summary>
    [Command("outpost owner", Level = AccountLevel.GameMaster, Help = "the nearest faction warfare tower becomes <faction 1-6>'s (Highland 1, Forest 2, Steppe 3, Mountain 4, Swamp 5, Caerleon 6)")]
    public static void Owner(CommandContext ctx, int faction)
    {
        var player = ctx.RequirePlayer();
        if (player.Map is not { } map) throw new CommandError("not on a map");
        if (faction is < 1 or > 6) throw new CommandError("a faction 1-6 (Highland 1, Forest 2, Steppe 3, Mountain 4, Swamp 5, Caerleon 6)");
        ctx.Reply(ctx.World.Outposts.Gm(map, player.PositionAt(DateTime.UtcNow), "owner", (byte)faction));
    }

    /// <summary>.outpost clear: the nearest tower's guards go (it is Claimable until they come back).</summary>
    [Command("outpost clear", Level = AccountLevel.GameMaster, Help = "the guards of the nearest faction warfare tower go: it can be claimed until they are back")]
    public static void Clear(CommandContext ctx)
    {
        var player = ctx.RequirePlayer();
        if (player.Map is not { } map) throw new CommandError("not on a map");
        ctx.Reply(ctx.World.Outposts.Gm(map, player.PositionAt(DateTime.UtcNow), "clear", 0));
    }
}
