using System.Globalization;
using System.Numerics;
using AlbionLocal.Server.Data;
using AlbionLocal.Server.Entities;
using AlbionLocal.Server.Persistence;

namespace AlbionLocal.Server.Commands.Builtin;

/// <summary>The open-world chests and camp quests (analysis/work/stageH/bosses/SPEC.md §6.5): .chest, .quest.</summary>
public static class ChestCommands
{
    /// <summary>
    /// .chest [list]: the chest spots of the cluster, nearest first (kind, chest, ChestState). .chest spawn &lt;spawnpoint&gt;: a chest
    /// of that spawnpoint 3 m in front of the player (as .rd chest; it never respawns). .chest unlock: the nearest chest within 20 m
    /// unlocks (a camp chest for the player alone).
    /// </summary>
    [Command("chest", Level = AccountLevel.GameMaster,
        Help = ".chest lists the cluster's chest spots; .chest spawn <spawnpoint> puts a chest in front of the player; .chest unlock unlocks the nearest one")]
    public static void Chest(CommandContext ctx, string what = "list", string spawnpoint = "STATIC_UNDEAD_VETERAN_POI")
    {
        var player = ctx.RequirePlayer();
        var world = ctx.World;
        if (player.Map is not { } map) throw new CommandError("the player is on no map");
        var at = player.PositionAt(DateTime.UtcNow);
        switch (what.ToLowerInvariant())
        {
            case "spawn":
                var (dx, dz) = TileMath.Rotate(0, 3, player.Facing);
                try
                {
                    var chest = world.Dungeons.SpawnChest(map, spawnpoint.ToUpperInvariant(), at + new Vector2((float)dx, (float)dz), player.Facing + 180);
                    ctx.Reply($"{chest}: tile {chest.Tile}, rarity {chest.State.Rarity}");
                }
                catch (InvalidOperationException e) { throw new CommandError(e.Message); }
                return;
            case "unlock":
                if (map.Objects.Values.OfType<LootChest>().Where(c => Vector2.Distance(c.Position, at) <= 20).MinBy(c => Vector2.Distance(c.Position, at)) is not { } near)
                    throw new CommandError("no loot chest within 20 m");
                world.WorldChests.Unlock(near, player);
                ctx.Reply($"{near} unlocked{(near.Personal ? " for you" : "")}");
                return;
            case "list":
                var lines = world.WorldChests.Describe(map, at).ToList();
                ctx.Reply(lines.Count == 0 ? $"{map.Cluster}: no open-world chest spots" : $"{map.Cluster}:\n{string.Join("\n", lines)}");
                return;
            default:
                throw new CommandError(".chest [list] | spawn <spawnpoint> | unlock");
        }
    }

    /// <summary>
    /// Public issue #19: .coffer [list]: the cluster's treasure slots (their draw) and spots, nearest first. .coffer spawn [coffer|solo|veteran|elite]:
    /// a treasure of that size 3 m in front of the player with its protection countdown and PvP circle (it never comes back). .chest unlock ends
    /// the countdown.
    /// </summary>
    [Command("coffer", Level = AccountLevel.GameMaster,
        Help = ".coffer lists the cluster's treasure slots and spots; .coffer spawn [coffer|solo|veteran|elite] puts a contested treasure in front of the player")]
    public static void Coffer(CommandContext ctx, string what = "list", string size = "coffer")
    {
        var player = ctx.RequirePlayer();
        var world = ctx.World;
        if (player.Map is not { } map) throw new CommandError("the player is on no map");
        var at = player.PositionAt(DateTime.UtcNow);
        switch (what.ToLowerInvariant())
        {
            case "spawn":
                var (dx, dz) = TileMath.Rotate(0, 3, player.Facing);
                try
                {
                    var chest = world.TreasureCoffers.SpawnAt(map, size, at + new Vector2((float)dx, (float)dz), (float)TileMath.Mod360(player.Facing + 180));
                    ctx.Reply(string.Create(CultureInfo.InvariantCulture,
                        $"{chest}: tile {chest.Tile}, ChestState {chest.LockState} for {chest.Template.ProtectionSeconds:0} s, PvP circle {(chest.PvpZone ? $"{chest.Template.PvpZoneRadius:0} m" : "off (Pvp:Mode)")}"));
                }
                catch (InvalidOperationException e) { throw new CommandError(e.Message); }
                return;
            case "list":
                var lines = world.TreasureCoffers.Describe(map, at).ToList();
                ctx.Reply(lines.Count == 0 ? $"{map.Cluster}: no treasure slots (World:TreasureCoffers, no spot or no distribution here)" : $"{map.Cluster}:\n{string.Join("\n", lines)}");
                return;
            default:
                throw new CommandError(".coffer [list] | spawn [coffer|solo|veteran|elite]");
        }
    }

    /// <summary>.quest [local]: the player's camp quests on this cluster (fame so far of the target, done, inside).</summary>
    [Command("quest", Level = AccountLevel.GameMaster, Help = ".quest local shows the player's camp quest progress on this cluster")]
    public static void Quest(CommandContext ctx, string what = "local")
    {
        var player = ctx.RequirePlayer();
        if (player.Map is not { } map) throw new CommandError("the player is on no map");
        if (!what.Equals("local", StringComparison.OrdinalIgnoreCase)) throw new CommandError(".quest local");
        var at = player.PositionAt(DateTime.UtcNow);
        var lines = ctx.World.WorldChests.AreasOf(map).OrderBy(a => Vector2.Distance(a.Spawn.Position, at)).Select(a =>
        {
            var state = a.Progress.TryGetValue(player.Character.Id, out var st)
                ? string.Create(CultureInfo.InvariantCulture, $"{st.Fame:0} / {a.Target:0} fame{(st.Done ? ", done" : "")}{(st.Inside ? ", inside" : "")}") : "not started";
            return string.Create(CultureInfo.InvariantCulture,
                $"  {a.Quest.Name} at ({a.Spawn.Position.X:0.##}, {a.Spawn.Position.Y:0.##}) r {a.Spawn.Radius:0}, {Vector2.Distance(a.Spawn.Position, at):0} m: {state}");
        }).ToList();
        ctx.Reply(lines.Count == 0 ? $"{map.Cluster}: no camp quests" : $"{map.Cluster}:\n{string.Join("\n", lines)}");
    }
}
