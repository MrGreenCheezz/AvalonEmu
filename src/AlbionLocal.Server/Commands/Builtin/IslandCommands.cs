using AlbionLocal.Server.Persistence;
using AlbionLocal.Server.Systems;

namespace AlbionLocal.Server.Commands.Builtin;

/// <summary>
/// The personal islands (stageH islands SPEC §1.1, §3): .island give, .island level, .island list, .island go; the farms of phase 3 (until
/// building exists, phase 5): .island build, .island unbuild, .island farms, .island ripen.
/// </summary>
public static class IslandCommands
{
    private static Islands Islands(CommandContext ctx) =>
        ctx.World.Islands.Enabled ? ctx.World.Islands : throw new CommandError("islands are off on this server (Islands:Enabled)");

    /// <summary>
    /// .island give [city] [level]: the personal island of <paramref name="city"/> (a world.xml city id; default: the city the player stands in,
    /// else its last city, Join key 118, an interior resolved to its city) at <paramref name="level"/> (0-5) for the player; one per character and city.
    /// </summary>
    [Command("island give", Level = AccountLevel.GameMaster, Help = "gives the player the personal island of a city [city id, default the current/last city] [level 0-5]")]
    public static void Give(CommandContext ctx, string city = "", int level = 0)
    {
        var player = ctx.RequirePlayer();
        var islands = Islands(ctx);
        if (islands.Data is not { } data) throw new CommandError("no island data (the client's worldsettings.xml)");
        if (city.Length == 0)
            city = player.Map is { } map && data.PersonalOf(map.Cluster) is not null ? map.Cluster : islands.CityOf(player.Character.LastCity); // an interior (1001 the bank) → its city
        if (data.PersonalOf(city) is not { } type)
            throw new CommandError($"'{city}' has no personal island; the island cities are {string.Join(", ", data.Types.Where(t => t.Kind == 0).Select(t => t.City))}");
        if (islands.Give(player.Character, type, level, out var why) is not { } island) throw new CommandError(why);
        ctx.Reply($"{island} given: the island merchant of {type.City} offers the visit");
    }

    /// <summary>.island level &lt;0-5&gt; [city]: the level of the player's island (of the city it stands on or in, else its only one); at once, players on it go to its city.</summary>
    [Command("island level", Level = AccountLevel.GameMaster, Help = "sets the level (0-5) of the player's island [city id: which one, when it has several]")]
    public static void Level(CommandContext ctx, int level, string city = "")
    {
        var player = ctx.RequirePlayer();
        var islands = Islands(ctx);
        var island = Pick(ctx, player, city);
        if (islands.Data?.TypeAt(island.Type)?.LevelAt(level) is null) throw new CommandError($"the island has no level {level} (0-5)");
        // a journey onto it already committed: the client cloned the old level's template (ChangeCluster key 1), the Join would build the new one
        if (player.Character.PendingTransfer?.To == island.Cluster) throw new CommandError("on the way to the island: try again once there");
        islands.SetLevel(island, level);
        ctx.Reply($"{island}: level {level} ({islands.WorldOf(island.Cluster)})");
    }

    /// <summary>.island list: the player's islands (with an argument "all": every island of the server).</summary>
    [Command("island list", Level = AccountLevel.GameMaster, Help = "the player's islands, or every island with 'all'")]
    public static void List(CommandContext ctx, string all = "")
    {
        var islands = Islands(ctx);
        var lines = islands.Describe(all == "all" ? null : ctx.RequirePlayer().Character.Id).ToList();
        ctx.Reply(lines.Count == 0 ? "no islands" : string.Join("\n", lines));
    }

    /// <summary>
    /// .labourer finish: on the island the player stands on (its own or another's), every labourer has settled in at once and every running
    /// job ends now, so the job's loot can be taken (LaborerTakeJobLoot); the count of labourers changed.
    /// </summary>
    [Command("labourer finish", Level = AccountLevel.GameMaster, Help = "on this island: every labourer settled in and every running job done now")]
    public static void LabourerFinish(CommandContext ctx)
    {
        var player = ctx.RequirePlayer();
        var islands = Islands(ctx);
        if (player.Map is not { } map || islands.Find(map.Cluster) is not { } island) throw new CommandError("the player is not on an island (.island go)");
        var n = islands.FinishAll(map);
        ctx.Reply($"{island}: {n} labourer(s) settled in or done with their job");
    }

    /// <summary>.island go [city]: onto the player's island as after a journey, without the merchant's window.</summary>
    [Command("island go", Level = AccountLevel.GameMaster, Help = "takes the player onto its island (as a journey) [city id: which one]")]
    public static void Go(CommandContext ctx, string city = "")
    {
        var player = ctx.RequirePlayer();
        var islands = Islands(ctx);
        var island = Pick(ctx, player, city);
        if (player.Map?.Cluster == island.Cluster) throw new CommandError("the player is on that island already");
        if (!islands.Send(player, island, $"island:{island.Id}:gm")) throw new CommandError($"the template of {island} is being read; try again in a moment");
    }

    /// <summary>The player's own island it stands on (the farm commands).</summary>
    private static Game.Island Here(CommandContext ctx, Entities.Player player) =>
        Islands(ctx).Find(player.Map?.Cluster) is { } on && on.Owner == player.Character.Id ? on : throw new CommandError("the player is not on its own island");

    /// <summary>
    /// .island build &lt;building&gt; [x z]: a player building (buildings.xml uniquename: T1_FARMHOUSE, T1_HERBGARDEN, T1_PASTURE, T1_KENNEL) on the
    /// island the player stands on, its centre at (x, z) or where the player stands. Its 3 × 3 slots lie 6 m apart around the centre.
    /// </summary>
    [Command("island build", Level = AccountLevel.GameMaster, Help = "builds a farm building on the player's island (T1_FARMHOUSE, T1_HERBGARDEN, …) [x z, default here]")]
    public static void Build(CommandContext ctx, string building, float? x = null, float? z = null)
    {
        var player = ctx.RequirePlayer();
        var island = Here(ctx, player);
        var at = x is { } bx && z is { } bz ? new System.Numerics.Vector2(bx, bz) : player.PositionAt(DateTime.UtcNow);
        if (Islands(ctx).Build(island, building.ToUpperInvariant(), at, 0, out var why) is not { } b) throw new CommandError(why);
        ctx.Reply(string.Create(System.Globalization.CultureInfo.InvariantCulture, $"{b.Name} built at ({b.X:0.#}, {b.Z:0.#})"));
    }

    /// <summary>.island unbuild: the player building nearest the player (within 15 m) on its island goes, with its crops.</summary>
    [Command("island unbuild", Level = AccountLevel.GameMaster, Help = "removes the player building nearest the player (within 15 m) and its crops")]
    public static void Unbuild(CommandContext ctx)
    {
        var player = ctx.RequirePlayer();
        var islands = Islands(ctx);
        var here = player.PositionAt(DateTime.UtcNow);
        var b = islands.BuildingsOf(Here(ctx, player).Id).Select(b => (b, d: System.Numerics.Vector2.Distance(here, new(b.X, b.Z)))).Where(t => t.d <= 15)
            .OrderBy(t => t.d).Select(t => t.b).FirstOrDefault() ?? throw new CommandError("no player building within 15 m");
        islands.Demolish(b);
        ctx.Reply($"{b.Name} removed");
    }

    /// <summary>.island farms: the buildings of the player's island (the one it stands on, else its only one) and their crops' growth.</summary>
    [Command("island farms", Level = AccountLevel.GameMaster, Help = "the farm buildings of the player's island and the growth of their crops")]
    public static void Farms(CommandContext ctx, string city = "")
    {
        var player = ctx.RequirePlayer();
        var lines = Islands(ctx).DescribeFarms(Pick(ctx, player, city)).ToList();
        ctx.Reply(lines.Count == 0 ? "no buildings (.island build)" : string.Join("\n", lines));
    }

    /// <summary>.island ripen: every crop of the player's island grown at once (to try the harvest).</summary>
    [Command("island ripen", Level = AccountLevel.GameMaster, Help = "lets every crop of the player's island finish growing now")]
    public static void Ripen(CommandContext ctx, string city = "")
    {
        var player = ctx.RequirePlayer();
        ctx.Reply($"{Islands(ctx).Ripen(Pick(ctx, player, city))} crop(s) grown");
    }

    /// <summary>The player's island of <paramref name="city"/>; without one: the island it stands on, else that of its city, else its only one.</summary>
    private static Game.Island Pick(CommandContext ctx, Entities.Player player, string city)
    {
        var islands = ctx.World.Islands;
        var mine = islands.Of(player.Character.Id);
        if (mine.Count == 0) throw new CommandError("the player has no island (.island give)");
        if (city.Length > 0) return mine.FirstOrDefault(i => i.City == city) ?? throw new CommandError($"the player has no island of {city}");
        return islands.Find(player.Map?.Cluster) is { } on && on.Owner == player.Character.Id ? on
            : mine.FirstOrDefault(i => i.City == player.Map?.Cluster) ?? (mine.Count == 1 ? mine[0] : throw new CommandError("the player has several islands: name the city"));
    }
}
