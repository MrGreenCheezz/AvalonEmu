using AlbionLocal.Server.Persistence;

namespace AlbionLocal.Server.Commands.Builtin;

/// <summary>Dragon areas (analysis/work/stageR/dragon-areas/SPEC.md): .dragon entrance places an entrance next to the GM, .dragon list and .dragon close the instances.</summary>
public static class DragonAreaCommands
{
    /// <summary>.dragon entrance [type]: a dragon area entrance of that dragonarea.xml entrance type (default DRAGON_AREA_ORANGE_SMALL) 4 m in front of the player.</summary>
    [Command("dragon entrance", Level = AccountLevel.GameMaster, Help = "places a dragon area entrance next to you: [type, default DRAGON_AREA_ORANGE_SMALL]")]
    public static void Entrance(CommandContext ctx, string type = "DRAGON_AREA_ORANGE_SMALL")
    {
        var player = ctx.RequirePlayer();
        if (player.Map is not { } map) throw new CommandError("not on a map");
        var at = player.PositionAt(DateTime.UtcNow) + new System.Numerics.Vector2(0, 4);
        var (entrance, why) = ctx.World.DragonAreas.Spawn(map, at, 0, type);
        if (entrance is null) throw new CommandError(why ?? "no entrance");
        ctx.Reply($"{entrance} placed, open for {entrance.Type.LifeSeconds:0} s");
    }

    /// <summary>.dragon list: the live dragon area instances.</summary>
    [Command("dragon list", Level = AccountLevel.GameMaster, Help = "lists the live dragon area instances")]
    public static void List(CommandContext ctx)
    {
        var lines = ctx.World.DragonAreas.Describe().ToList();
        ctx.Reply(lines.Count == 0 ? "no dragon area instance" : $"{lines.Count} dragon area instance(s):\n{string.Join("\n", lines)}");
    }

    /// <summary>.dragon close: closes the dragon area instance the GM stands in (everyone out in front of its entrance).</summary>
    [Command("dragon close", Level = AccountLevel.GameMaster, Help = "closes the dragon area instance you are in")]
    public static void Close(CommandContext ctx)
    {
        var player = ctx.RequirePlayer();
        if (ctx.World.DragonAreas.Find(player.Map?.Cluster) is not { } instance) throw new CommandError("you are in no dragon area instance");
        ctx.World.DragonAreas.Close(instance, "closed by a GM");
        ctx.Reply($"{instance} closed");
    }
}
