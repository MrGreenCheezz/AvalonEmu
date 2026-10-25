using System.Globalization;
using System.Numerics;
using AlbionLocal.Server.Bots;
using AlbionLocal.Server.Entities;
using AlbionLocal.Server.Persistence;

namespace AlbionLocal.Server.Commands.Builtin;

/// <summary>
/// Stage I bot commands (analysis/work/stageI/bots/SPEC.md §4), GameMaster: .bot (the population of every loaded map), .bot list, .bot spawn,
/// .bot clear, .bot on / off, .bot info, .bot grid, .bot path.
/// </summary>
public static class BotCommands
{
    /// <summary>Most bots one .bot spawn makes.</summary>
    public const int MaxCount = 20;

    private static string Inv(FormattableString s) => s.ToString(CultureInfo.InvariantCulture);

    /// <summary>Per loaded map with humans or bots: its zone class, humans, bots by role, slots filled, the walk grid; the switch and the totals.</summary>
    [Command("bot", Level = AccountLevel.GameMaster, Help = "the bot population of every loaded map, the walk grids and whether bots are on")]
    public static void Population(CommandContext ctx)
    {
        var bots = ctx.World.Bots;
        var lines = new List<string>
        {
            $"bots {(bots.Enabled ? "on" : "off")}: {bots.Count} in the world (cap {bots.Options.MaxTotal}), {bots.Nav.Count} walk grid(s) kept, {bots.Nav.Building} being built",
            Switches(bots.Options),
        };
        foreach (var map in ctx.World.Maps.Maps.Where(m => m.HumanCount > 0 || m.Bots.Count > 0))
        {
            var roles = string.Join(", ", map.Bots.Values.GroupBy(b => b.Bot!.Role).Select(g => $"{g.Count()} {g.Key.ToString().ToLowerInvariant()}"));
            var slots = bots.SlotsOf(map);
            var nav = map.Template is { } t && bots.Nav.Find(t.Id) is { } n ? n.Summary() : "walk grid not built";
            lines.Add($"{map.Cluster} ({bots.ZoneOf(map) ?? "no bots"}): {map.HumanCount} human(s), {map.Bots.Count} bot(s){(roles.Length > 0 ? $" ({roles})" : "")}, "
                + $"slots {slots.Count(s => s.Bot is not null)}/{slots.Count}; {nav}{(bots.UnreachableOf(map) is > 0 and var u ? $"; {u} POI(s) without a path" : "")}");
        }
        ctx.Reply(string.Join("\n", lines));
    }

    /// <summary>The bots of the player's map, nearest first: name, role, tier, persona, distance, what it does.</summary>
    [Command("bot list", Level = AccountLevel.GameMaster, Help = "the bots of this map: name, role, tier, persona, distance, what they do")]
    public static void List(CommandContext ctx)
    {
        var player = ctx.RequirePlayer();
        var map = MapOf(player);
        var at = player.PositionAt(DateTime.UtcNow);
        var time = ctx.World.Bots.Time;
        var bots = map.Bots.Values.OrderBy(b => Vector2.Distance(b.PositionAt(time), at)).ToList();
        if (bots.Count == 0)
        {
            ctx.Reply($"no bots on {map.Cluster}");
            return;
        }
        ctx.Reply(string.Join("\n", bots.Select(b => Inv($"'{b.Character.Name}' {b.Bot!.Role.ToString().ToLowerInvariant()} T{b.Bot.Tier} {b.Bot.PersonaId} (object {b.Id}) {Vector2.Distance(b.PositionAt(time), at):0} m: {b.Bot.Brain.State}"))));
    }

    /// <summary>
    /// <paramref name="count"/> GM bots of <paramref name="role"/> 3-8 m around the walkable cell nearest to the player (within 6 m; on that
    /// cell when no spot is found), gear tier <paramref name="tier"/> (0: by the zone): NewCharacter to everyone in view at once. Refused until
    /// the walk grid is built, and where nothing within 6 m is walkable: a bot is never put in a wall. Extra to the population, they never
    /// respawn; .bot clear takes them. <paramref name="elite"/> "elite": every one an elite (BotInfo.Elite: Bots:Gear:EliteEnchant and EliteQuality)
    /// instead of the Bots:Gear:EliteChance roll of the population's patrols. City and gatherer bots (a gatherer gathers what the map has of its tier, which may move its tier to
    /// the nodes there; refused on a map without harvestables) and patrol bots (in groups of up to Bots:Patrol:GroupSize's most, on the
    /// route nearest to the player; refused where the rule keeps no Red player: a city, a green zone).
    /// </summary>
    [Command("bot spawn", Level = AccountLevel.GameMaster,
        Help = "puts <count> GM bots of a role (city, gatherer, patrol) 3-8 m around the player, gear tier [tier] (0 = by the zone); 'elite' last: elites (enchanted gear)")]
    public static void Spawn(CommandContext ctx, string role, int count = 1, int tier = 0, string elite = "")
    {
        if (elite.Length > 0 && !elite.Equals("elite", StringComparison.OrdinalIgnoreCase)) throw new CommandError($"'{elite}': only the word elite may follow the tier");
        var forceElite = elite.Length > 0 ? true : (bool?)null;
        var player = ctx.RequirePlayer();
        var bots = ctx.World.Bots;
        if (!bots.Enabled) throw new CommandError("bots are off (.bot on)");
        if (!Enum.TryParse<BotRole>(role, ignoreCase: true, out var r) || !Enum.IsDefined(r)) throw new CommandError($"role: '{role}' is not city, gatherer or patrol");
        if (count is < 1 or > MaxCount) throw new CommandError($"count: {count} is not 1..{MaxCount}");
        if (tier is < 0 or > 8) throw new CommandError($"tier: {tier} is not 0..8");
        var map = MapOf(player);
        if (BotManager.Patrols(r) && !bots.PatrolsAllowed(map))
            throw new CommandError($"no patrols on {map.Cluster}: its PvP rule keeps no Red player (a Red bot would turn Blue)");
        var at = player.PositionAt(DateTime.UtcNow);
        var nav = bots.NavOf(map) ?? throw new CommandError($"the walk grid of {map.Cluster} is being built; try again in a moment");
        var centre = nav.Grid.Nearest(at, 6) ?? throw new CommandError("nothing walkable within 6 m of you");
        var names = new List<string>();
        var size = Math.Max(1, (int)Math.Round(bots.Options.GroupSize.Max));
        BotGroup? group = null;
        for (var i = 0; i < count; i++)
        {
            var spot = nav.Grid.RandomNear(bots.Random, centre, 3, 8) ?? centre;
            var facing = AI.MotionMaster.Facing(spot, at);
            var t = tier > 0 ? tier : bots.Factory.TierFor(bots.ZoneOf(map) ?? "Safe", map.Template?.Name.Tier ?? 0, bots.Random);
            if (group is { } g && g.Members.Count >= size) group = null; // the next group
            if (bots.Spawn(map, r, t, spot, facing, group: group, elite: forceElite) is not { } bot) continue;
            group = bot.Bot!.Group;
            names.Add(Inv($"'{bot.Character.Name}' T{t}{(bot.Bot!.Elite ? " elite" : "")} (object {bot.Id})"));
        }
        if (names.Count == 0)
            throw new CommandError(r == BotRole.Gatherer && bots.NodesOf(map).Count == 0 ? $"nothing to gather on {map.Cluster}"
                : r == BotRole.Patrol ? $"no patrol route on {map.Cluster} (or no free name)" : "no bot spawned (no free name)");
        ctx.Reply($"{names.Count} {r.ToString().ToLowerInvariant()} bot(s): {string.Join(", ", names)}");
    }

    /// <summary>Every bot of the player's map (<paramref name="where"/> here) or of every map (all) goes (Leave); the slots refill after Bots:RespawnSeconds.</summary>
    [Command("bot clear", Level = AccountLevel.GameMaster, Help = "despawns the bots of this map (here) or of every map (all); the population refills later")]
    public static void Clear(CommandContext ctx, string where = "here")
    {
        var bots = ctx.World.Bots;
        var n = where.ToLowerInvariant() switch
        {
            "here" => bots.Clear(MapOf(ctx.RequirePlayer()), "a GM's .bot clear", BotRefill.Respawn),
            "all" => bots.ClearAll("a GM's .bot clear", BotRefill.Respawn),
            _ => throw new CommandError($"where: '{where}' is not here or all"),
        };
        ctx.Reply($"{n} bot(s) despawned");
    }

    /// <summary>The switch matrix of world.jsonc (bot-switches): the kinds and behaviours on or off under the master.</summary>
    private static string Switches(BotOptions o)
    {
        static string On(bool on) => on ? "on" : "off";
        return $"switches: world {On(o.WorldEnabled)} (city {On(o.CityEnabled)}, gatherer {On(o.GathererEnabled)}, patrol {On(o.PatrolEnabled)}, drifter {On(o.DrifterEnabled)}; "
            + $"zones {(o.Zones.Count == 0 ? "all" : string.Join("/", o.Zones))}), arena {On(o.ArenaEnabled)}, hellgate {On(o.HellgateEnabled)}, invader {On(o.InvaderEnabled)}, "
            + $"pvp {On(o.PvpEnabled)}, rewards {On(o.RewardsEnabled)}";
    }

    /// <summary>
    /// Bots on for this run only (the master, Bots:Enabled): not saved, the next start reads world.jsonc again. The other switches
    /// (Bots:World:Enabled, the roles, Bots:Zones, ...) are not changed at run time: there is no .bot reload, edit world.jsonc and restart.
    /// </summary>
    [Command("bot on", Level = AccountLevel.GameMaster, Help = "switches the bots on for this run only (Bots:Enabled; not saved, world.jsonc decides at the next start)")]
    public static void On(CommandContext ctx)
    {
        ctx.World.Bots.Enabled = true;
        ctx.Reply("bots on for this run (not saved)");
    }

    /// <summary>Bots off for this run only (not saved): every bot goes, no population pass spawns, .bot spawn is refused.</summary>
    [Command("bot off", Level = AccountLevel.GameMaster, Help = "switches the bots off for this run only: every bot goes (not saved, world.jsonc decides at the next start)")]
    public static void Off(CommandContext ctx)
    {
        var n = ctx.World.Bots.Count;
        ctx.World.Bots.Enabled = false;
        ctx.Reply($"bots off for this run, not saved ({n} despawned)");
    }

    /// <summary>The bot nearest to the player on its map: role, tier, persona, flag, health, what it does, its path and stay; a patrol's group and route.</summary>
    [Command("bot info", Level = AccountLevel.GameMaster, Help = "the nearest bot: role, tier, persona, flag, health, state, path")]
    public static void Info(CommandContext ctx)
    {
        var player = ctx.RequirePlayer();
        var map = MapOf(player);
        var at = player.PositionAt(DateTime.UtcNow);
        var time = ctx.World.Bots.Time;
        var bot = map.Bots.Values.MinBy(b => Vector2.Distance(b.PositionAt(time), at)) ?? throw new CommandError($"no bots on {map.Cluster}");
        var info = bot.Bot!;
        var p = bot.PositionAt(time);
        var stay = info.StayUntil == TimeSpan.MaxValue ? "a GM's bot" : Inv($"stays {(info.StayUntil - ctx.World.Now).TotalMinutes:0.#} min more");
        var fight = info.Fight;
        var fighting = fight.Defeated ? "; defeated" : fight.Active ? Inv($"; fights object {fight.TargetId}, {fight.Engaged.Count} attacker(s) engaged{(fight.FleeUntil is not null ? ", running off" : "")}") : "";
        if (info.Group is { } group) fighting += Inv($"; group of {group.Members.Count}, route of {group.Route.Stops.Count} stops ({(group.Route.Loop ? "a loop" : "a road")}), {fight.Spared.Count} spared");
        ctx.Reply(Inv($"'{bot.Character.Name}' (object {bot.Id}) {info.Role.ToString().ToLowerInvariant()} T{info.Tier} {info.PersonaId}, flag {bot.Flag}, ")
            + Inv($"health {bot.Health.At(ctx.World.GameNow):0}/{bot.Health.Max:0}, at ({p.X:0.#}, {p.Y:0.#}) {Vector2.Distance(p, at):0} m away, {info.Brain.State}")
            + Inv($"{(info.Motion.Goal is { } g ? $", walking to ({g.X:0.#}, {g.Y:0.#}), {info.Motion.Ahead.Count()} leg(s) left" : "")}{(info.Emote is { } e ? $", emote {e.Emote}" : "")}{fighting}{(fight.CarriedTotal > 0 ? $"; carries {string.Join(", ", fight.Carried.Select(c => $"{c.Value} x {c.Key}"))}" : "")}; {stay}; seen by {bot.Observers.Count}"));
    }

    /// <summary>The walk grid at the player: its cell blocked or walkable, the nearest walkable point, the lattice nodes within 20 m.</summary>
    [Command("bot grid", Level = AccountLevel.GameMaster, Help = "the bots' walk grid at the player: blocked, walkable, nearest walkable point")]
    public static void Grid(CommandContext ctx)
    {
        var player = ctx.RequirePlayer();
        var map = MapOf(player);
        var nav = ctx.World.Bots.NavOf(map) ?? throw new CommandError($"the walk grid of {map.Cluster} is being built; try again in a moment");
        var at = player.PositionAt(DateTime.UtcNow);
        var (x, z) = ((int)Math.Floor(at.X), (int)Math.Floor(at.Y));
        var nearest = nav.Grid.Nearest(at, 10);
        ctx.Reply(Inv($"cell ({x}, {z}): {(nav.Grid.Blocked(x, z) ? "blocked" : "not blocked")}, {(nav.Grid.Walkable(x, z) ? "walkable" : "not walkable")}")
            + (nearest is { } n ? Inv($"; nearest walkable ({n.X:0.#}, {n.Y:0.#}) {Vector2.Distance(n, at):0.#} m") : "; nothing walkable within 10 m")
            + $"; {nav.Summary()}");
    }

    /// <summary>
    /// The bots' path from the player to (<paramref name="x"/>, <paramref name="z"/>): its legs and length; a GM city bot walks it (a quick
    /// look at whether bots go around the walls).
    /// </summary>
    [Command("bot path", Level = AccountLevel.GameMaster, Help = "the bots' path from the player to <x> <z>, walked by a new GM city bot")]
    public static void Path(CommandContext ctx, float x, float z)
    {
        var player = ctx.RequirePlayer();
        var bots = ctx.World.Bots;
        if (!bots.Enabled) throw new CommandError("bots are off (.bot on)");
        var map = MapOf(player);
        var nav = bots.NavOf(map) ?? throw new CommandError($"the walk grid of {map.Cluster} is being built; try again in a moment");
        var at = player.PositionAt(DateTime.UtcNow);
        var from = nav.Grid.Nearest(at, 10) ?? throw new CommandError("nothing walkable within 10 m of you");
        var to = nav.Grid.Nearest(new Vector2(x, z), 10) ?? throw new CommandError(Inv($"nothing walkable within 10 m of ({x}, {z})"));
        var path = bots.FindPath(nav, from, to) ?? throw new CommandError(Inv($"no path from ({from.X:0.#}, {from.Y:0.#}) to ({to.X:0.#}, {to.Y:0.#})"));
        var length = path.Zip(path.Skip(1), Vector2.Distance).Sum();
        var tier = bots.Factory.TierFor(bots.ZoneOf(map) ?? "Safe", map.Template?.Name.Tier ?? 0, bots.Random);
        var bot = bots.Spawn(map, BotRole.City, tier, from, AI.MotionMaster.Facing(from, to)) ?? throw new CommandError("no bot spawned (no free name)");
        bots.Walk(bot, path, null);
        ctx.Reply(Inv($"{path.Count - 1} leg(s), {length:0} m (straight {Vector2.Distance(from, to):0} m): '{bot.Character.Name}' walks it"));
    }

    private static Map MapOf(Player player) => player.Map is { Grid: not null } map ? map
        : throw new CommandError($"cluster {player.Map?.Cluster ?? player.Character.Cluster} has no world objects (yet)");
}
