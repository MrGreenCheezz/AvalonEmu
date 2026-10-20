using System.Globalization;
using System.Numerics;
using AlbionLocal.Server.Data;
using AlbionLocal.Server.Persistence;
using AlbionLocal.Server.Systems;

namespace AlbionLocal.Server.Commands.Builtin;

/// <summary>Random dungeons (analysis/work/stageG/dungeons/SPEC.md §13 item 1): .rd; static dungeons: .sd event.</summary>
public static class DungeonCommands
{
    /// <summary>
    /// .rd &lt;exit&gt;: a random dungeon exit of that RandomExit or FixedExit name (T4_PORTAL_ROYAL_SOLO, T4_HERETIC, …) 3 m in front
    /// of the player, facing them; it goes after Dungeons:GmExitSeconds unused. .rd close: the player's dungeon closes now (everyone
    /// inside is pushed out). .rd chest [spawnpoint]: a loot chest of that LootChestSpawnpoint (HERETIC_SOLO_LOOTCHEST_STANDARD by
    /// default) 3 m in front of the player. .rd: the exits of the cluster and the open dungeons.
    /// </summary>
    [Command("rd", Level = AccountLevel.GameMaster,
        Help = ".rd <exit name> puts a random dungeon exit in front of the player; .rd chest [spawnpoint] a loot chest; .rd close closes the player's dungeon; .rd lists exits and dungeons")]
    public static void Rd(CommandContext ctx, string exit = "", string spawnpoint = "HERETIC_SOLO_LOOTCHEST_STANDARD")
    {
        var player = ctx.RequirePlayer();
        var world = ctx.World;
        if (player.Map is not { } map) throw new CommandError("the player is on no map");
        if (exit.Equals("chest", StringComparison.OrdinalIgnoreCase))
        {
            var (cx, cz) = TileMath.Rotate(0, 3, player.Facing);
            try
            {
                var chest = world.Dungeons.SpawnChest(map, spawnpoint.ToUpperInvariant(), player.PositionAt(DateTime.UtcNow) + new Vector2((float)cx, (float)cz), player.Facing + 180);
                ctx.Reply($"{chest}: prefab {chest.Template.Prefab}, rarity {chest.State.Rarity}");
            }
            catch (InvalidOperationException e) { throw new CommandError(e.Message); }
            return;
        }
        if (exit.Equals("close", StringComparison.OrdinalIgnoreCase))
        {
            if (world.Dungeons.Find(map.Cluster) is not { } here) throw new CommandError("the player is in no random dungeon");
            ctx.Reply($"closing dungeon {here.Group.Ids[0]}");
            world.Dungeons.Close(here.Group, "closed by a GM");
            return;
        }
        if (exit.Length > 0)
        {
            var at = player.PositionAt(DateTime.UtcNow);
            var facing = player.Facing;
            var (dx, dz) = TileMath.Rotate(0, 3, facing);
            try
            {
                var made = world.Dungeons.Spawn(map, exit.ToUpperInvariant(), at + new Vector2((float)dx, (float)dz), facing + 180);
                ctx.Reply($"{made}: prefab {made.Prefab} (visualization {made.Visualization}), clustertype {made.ClusterType}");
            }
            catch (InvalidOperationException e) { throw new CommandError(e.Message); }
            return;
        }
        var here2 = player.PositionAt(DateTime.UtcNow);
        var lines = new List<string>();
        foreach (var e in world.Dungeons.ExitsOf(map).OrderBy(e => Vector2.Distance(e.Position, here2)))
            lines.Add(string.Create(CultureInfo.InvariantCulture, $"  #{e.Id} {e.Name} at ({e.Position.X:0.##}, {e.Position.Y:0.##}), {Vector2.Distance(e.Position, here2):0} m, {e.SubtypeName} level {e.Level}"));
        foreach (var g in world.Dungeons.Groups)
            lines.Add($"  dungeon {g.Ids[0]}: {g.Plan.Generator.Name} T{g.Tier} from {g.Entrance}, {g.Floors.Count(f => f is not null)}/{g.FloorCount} floor(s) made"
                      + (g.EmptySince is null ? ", players inside" : ", empty")
                      + string.Create(CultureInfo.InvariantCulture, $", {g.Players} player(s){(g.Bonus != 1 ? $", dungeon of the day x{g.Bonus:0.##}" : "")}"));
        // StageH PvE: the dungeon of the day (Dungeons:DailyBonus).
        var daily = world.DungeonSettings.DailyFaction(new DateTime(world.GameNow, DateTimeKind.Utc));
        lines.Add(daily is null ? "  no dungeon of the day" : string.Create(CultureInfo.InvariantCulture,
            $"  dungeon of the day: {RandomDungeons.DailyName(daily)} (x{world.DungeonSettings.DailyBonus:0.##} fame, silver and loot until {world.DungeonSettings.DailyResetHour:00}:00 UTC)"));
        ctx.Reply($"{map.Cluster}:\n{string.Join("\n", lines)}");
    }

    /// <summary>How far .sd event looks for a static dungeon entrance on the player's map (m).</summary>
    public const float EventRange = 200;

    /// <summary>
    /// .sd event [minutes]: the staticdungeons.xml event of the static dungeon the player is in, or whose entrance is nearest within
    /// <see cref="EventRange"/> m, starts now for <paramref name="minutes"/> (over the schedule, not saved; <see cref="StaticDungeons.Force"/>);
    /// the players on the dungeon's clusters and on its entrances' clusters get 499 and the entrance clusters' ClusterInfoUpdate (keys 65-68).
    /// </summary>
    [Command("sd event", Level = AccountLevel.GameMaster, Help = "starts now the event of the static dungeon you are in or whose entrance is nearest (200 m) [minutes=60]")]
    public static void SdEvent(CommandContext ctx, float minutes = 60)
    {
        var player = ctx.RequirePlayer();
        var world = ctx.World;
        if (minutes is <= 0 or > 24 * 60) throw new CommandError($"minutes: {minutes} is not 1..1440");
        if (player.Map is not { } map || world.Clusters?.WorldIfLoaded is not { } info || info.Find(map.Cluster) is not { } cluster)
            throw new CommandError("the player is on no world.xml cluster");
        var sd = world.StaticDungeons;
        var at = player.PositionAt(DateTime.UtcNow);
        var dungeon = sd.Of(map.Cluster) ?? cluster.Exits
            .Where(e => e.Attrs.TryGetValue("staticdungeonid", out var s) && Guid.TryParse(s, out var g) && sd.Dungeons.ContainsKey(g) && Vector2.Distance(e.Position, at) <= EventRange)
            .OrderBy(e => Vector2.Distance(e.Position, at)).Select(e => sd.Dungeons[Guid.Parse(e.Attrs["staticdungeonid"])]).FirstOrDefault()
            ?? throw new CommandError($"no static dungeon here or with an entrance within {EventRange:0} m (.exits lists the exits)");
        var ev = sd.Force(dungeon, minutes);
        var clusters = dungeon.Clusters.Concat(dungeon.Entrances.Select(e => e.Cluster)).ToHashSet(StringComparer.Ordinal);
        var told = 0;
        foreach (var p in CommandRegistry.Players(world).Where(p => !p.IsBot && p.Map is { } m && clusters.Contains(m.Cluster)))
        {
            sd.Joined(p); // 499 for the dungeon here and those entered from here
            foreach (var id in dungeon.Entrances.Select(e => e.Cluster).Distinct())
                if (Network.WorldSession.ClusterInfo(info, id, sd) is { } update) p.Session.Send(update);
            told++;
        }
        ctx.Reply(string.Create(CultureInfo.InvariantCulture,
            $"static dungeon {dungeon.Type} T{dungeon.Tier} ({string.Join(", ", dungeon.Clusters)}): event {ev.Def.Name} for {minutes:0.#} min, {told} player(s) told"));
    }
}
