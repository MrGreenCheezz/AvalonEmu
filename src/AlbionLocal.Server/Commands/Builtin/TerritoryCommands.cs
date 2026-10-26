using System.Numerics;
using AlbionLocal.Server.Entities;
using AlbionLocal.Server.Persistence;

namespace AlbionLocal.Server.Commands.Builtin;

/// <summary>The guild territories (analysis/work/stageP/territories/SPEC.md): .territory, .castle, .tower.</summary>
public static class TerritoryCommands
{
    /// <summary>.territory [id or name]: the territory counts and claim rules, or one territory's cluster, owner, prime time and season window.</summary>
    [Command("territory", Level = AccountLevel.Player, Help = "the guild territories and castles (counts, claim rules), or one by <id or name>")]
    public static void Describe(CommandContext ctx, Tail key = default)
    {
        foreach (var line in ctx.World.Territories.Describe(key.Text)) ctx.Reply(line);
    }

    /// <summary>
    /// .castle &lt;action&gt; [guild]: on the castle of the player's map nearest to it: capture (the defenders go, the capture zone opens), reset
    /// (the fight fails), chest (a castle chest now), owner [guild] (the player's guild without a name), free (unowned).
    /// </summary>
    [Command("castle", Level = AccountLevel.GameMaster, Help = ".castle capture|reset|chest|free|owner [guild]: on the nearest castle of your map")]
    public static void Castle(CommandContext ctx, string action = "", Tail guild = default)
    {
        var player = ctx.RequirePlayer();
        var world = ctx.World;
        if (player.Map is not { } map) throw new CommandError("the player is on no map");
        var at = player.PositionAt(DateTime.UtcNow);
        if (map.Objects.Values.OfType<CastleObject>().MinBy(c => Vector2.Distance(c.Position, at)) is not { } castle)
            throw new CommandError("no castle on this map");
        var g = string.IsNullOrWhiteSpace(guild.Text) ? world.Guilds.Of(player.Character.Id) : world.Guilds.Find(guild.Text);
        ctx.Reply(world.Territories.Gm(castle.State, action.ToLowerInvariant(), g));
    }

    /// <summary>
    /// .tower &lt;action&gt; [value]: on the watchtower territory of the player's map nearest to it: owner [guild] (the player's guild without a
    /// name), free (unowned, or the NPC holder's), food &lt;n&gt;, power &lt;n&gt; (tower power points), beat (its NPC guards and Tower Guardian go).
    /// </summary>
    [Command("tower", Level = AccountLevel.GameMaster, Help = ".tower owner [guild]|free|food <n>|power <n>|beat: on the nearest territory tower of your map")]
    public static void Tower(CommandContext ctx, string action = "", Tail value = default)
    {
        var player = ctx.RequirePlayer();
        var world = ctx.World;
        if (player.Map is not { } map) throw new CommandError("the player is on no map");
        var at = player.PositionAt(DateTime.UtcNow);
        if (map.Objects.Values.OfType<MonolithObject>().MinBy(m => Vector2.Distance(m.Position, at)) is not { } tower)
            throw new CommandError("no territory tower on this map");
        var text = value.Text?.Trim() ?? "";
        var g = action.Equals("owner", StringComparison.OrdinalIgnoreCase)
            ? string.IsNullOrWhiteSpace(text) ? world.Guilds.Of(player.Character.Id) : world.Guilds.Find(text) : null;
        var amount = double.TryParse(text, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var n) ? n : 0;
        ctx.Reply(world.Territories.GmWatchtower(tower.State, action.ToLowerInvariant(), g, amount));
    }
}
