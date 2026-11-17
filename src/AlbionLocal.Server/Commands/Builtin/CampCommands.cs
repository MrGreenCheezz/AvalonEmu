using AlbionLocal.Server.Persistence;

namespace AlbionLocal.Server.Commands.Builtin;

/// <summary>StageQ camps (<see cref="Systems.FactionCamps"/>): .camp lists the map's dynamic templates (Default or faction camp, Active or Inactive).</summary>
public static class CampCommands
{
    /// <summary>.camp: each rolled dyn slot with VAR layers, nearest first: its template, feature layer, state, a faction camp's guards and next state.</summary>
    [Command("camp", Level = AccountLevel.Player, Help = "the dynamic camps of this map: Default or faction camp, Active or Inactive, guards up, next state")]
    public static void List(CommandContext ctx)
    {
        var player = ctx.RequirePlayer();
        if (player.Map is not { } map) throw new CommandError("not on a map");
        foreach (var line in ctx.World.FactionCamps.Describe(map, player.PositionAt(DateTime.UtcNow))) ctx.Reply(line);
    }
}
