using System.Globalization;
using System.Numerics;
using AlbionLocal.Server.Data;
using AlbionLocal.Server.Entities;
using AlbionLocal.Server.Persistence;
using AlbionLocal.Server.Systems;

namespace AlbionLocal.Server.Commands.Builtin;

/// <summary>The smugglers for a GM (analysis/work/stageR/smugglers/SPEC.md): .smuggler pile, .smuggler info.</summary>
public static class SmugglerCommands
{
    /// <summary>A crate pile of treasuredistribution.xml TreasurePile SMUGGLER_CRATE_PILE_<paramref name="size"/> 3 m in front of the player (protected as its data says).</summary>
    [Command("smuggler pile", Level = AccountLevel.GameMaster, Help = "puts a smuggler crate pile 3 m in front of you: SMALL (default), MEDIUM1-4 or LARGE1-4")]
    public static void Pile(CommandContext ctx, string size = "SMALL")
    {
        var player = ctx.RequirePlayer();
        if (player.Map is not { Grid: not null } map) throw new CommandError("the player is on no loaded map");
        var at = player.PositionAt(DateTime.UtcNow);
        var (dx, dz) = TileMath.Rotate(0, 3, player.Facing);
        var name = "SMUGGLER_CRATE_PILE_" + size.ToUpperInvariant();
        var pile = ctx.World.Smugglers.Place(map, name, at + new Vector2((float)dx, (float)dz))
            ?? throw new CommandError($"no pile {name} (piledobjects.xml, or no wagon tile for this cluster's biome)");
        ctx.Reply(string.Create(CultureInfo.InvariantCulture, $"{pile.Definition.Name} (object {pile.Id}): {pile.Crates.Count} crate(s), state {pile.State}"));
    }

    /// <summary>The player's smugglers standing, what it carries and the stations near it.</summary>
    [Command("smuggler info", Level = AccountLevel.GameMaster, Help = "your smugglers standing, what you carry, the smuggler stashes of the cluster")]
    public static void Info(CommandContext ctx)
    {
        var player = ctx.RequirePlayer();
        var at = player.PositionAt(DateTime.UtcNow);
        var stations = player.Map?.Objects.Values.OfType<SmugglerDeliveryStation>().OrderBy(s => Vector2.Distance(s.Position, at)).ToList() ?? [];
        var carried = ctx.World.Carriables.Of(player);
        ctx.Reply(string.Create(CultureInfo.InvariantCulture,
            $"smugglers standing {player.Character.SmugglerStanding / 10000:N0} (rank {Smugglers.RankOf(player.Character.SmugglerStanding)}); carrying {(carried is null ? "nothing" : $"{carried.Type.Name} rarity {carried.Rarity}")}; "
            + $"{stations.Count} stash(es){(stations.Count > 0 ? $", the nearest {Vector2.Distance(stations[0].Position, at):0} m away at ({stations[0].Position.X:0}, {stations[0].Position.Y:0})" : "")}"));
    }
}
