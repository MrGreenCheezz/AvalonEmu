using System.Numerics;
using AlbionLocal.Server.Entities;
using AlbionLocal.Server.Network;
using AlbionLocal.Server.Persistence;
using AlbionLocal.Server.Protocol;

namespace AlbionLocal.Server.Commands.Builtin;

/// <summary>The first built-in commands (ARCHITECTURE.md §6.6): help, save, who, gps, go, ops unhandled (with the opcode stubs apart).</summary>
public static class GeneralCommands
{
    [Command("help", Level = AccountLevel.Player, Help = "the commands you may use, or those starting with <command>")]
    public static void Help(CommandContext ctx, Tail command = default)
    {
        var words = command.IsEmpty ? [] : command.Text!.TrimStart('.').Split(' ', StringSplitOptions.RemoveEmptyEntries);
        var shown = ctx.World.Commands.Commands
            .Where(c => c.Level <= ctx.Level && (ctx.Console || !c.ConsoleOnly) && words.Select((w, i) => i < c.Words.Length && c.Words[i].Equals(w, StringComparison.OrdinalIgnoreCase)).All(x => x))
            .Select(c => c.Help.Length > 0 ? $"{c.Usage} - {c.Help}" : c.Usage)
            .Concat(ctx.World.Plugins.HelpLines(ctx.Level, words)) // the plugins' commands
            .ToList();
        if (shown.Count == 0) throw new CommandError($"no command .{string.Join(" ", words)}; .help lists them");
        ctx.Reply(string.Join("\n", shown));
    }

    [Command("save", Level = AccountLevel.Player, Help = "saves every player in game now")]
    public static void Save(CommandContext ctx)
    {
        var players = CommandRegistry.Players(ctx.World);
        foreach (var p in players) ctx.World.Saves.SaveNow(p.Character);
        ctx.Reply(players.Count == 0 ? "nobody in game, nothing to save" : $"saving {players.Count} character(s)");
    }

    /// <summary>The players in game with object id, cluster and position (where their walks have got to), one per line after the count.</summary>
    [Command("who", Level = AccountLevel.Player, Help = "the players in game: cluster and position")]
    public static void Who(CommandContext ctx)
    {
        var players = CommandRegistry.Players(ctx.World);
        var now = DateTime.UtcNow;
        string Line(Player p)
        {
            var at = p.PositionAt(now);
            return $"'{p.Character.Name}' (object {p.Id}): cluster {p.Map?.Cluster ?? p.Character.Cluster}, position ({at.X:0.##}, {at.Y:0.##})";
        }
        ctx.Reply(players.Count == 0 ? "nobody in game" : $"{players.Count} in game:\n" + string.Join("\n", players.Select(Line)));
    }

    /// <summary>
    /// Moves the player within its cluster to (<paramref name="x"/>, <paramref name="z"/>), facing <paramref name="facing"/>
    /// degrees: a MoveResponse with the position to its own client (a correction, cyl.ai7 0x180a02e90: teleport, facing,
    /// stop; pushed without a request id, as Move has none; not yet tried in the client) and Teleport (event 4) to the
    /// players who know it (stage 7 SPEC §2.2), then the visibility of the new cell.
    /// </summary>
    [Command("go", Level = AccountLevel.GameMaster, Help = "moves the player within its cluster to (x, z), facing <facing> degrees")]
    public static void Go(CommandContext ctx, float x, float z, float facing = 0)
    {
        var player = ctx.RequirePlayer();
        if (player.Map is null || player.Session.Player != player) throw new CommandError("the player is not in the world");
        player.Session.Relocate(new Vector2(x, z), facing);
        ctx.Reply($"moved to ({x:0.##}, {z:0.##}) in {player.Map.Cluster}; {player.Observers.Count} other player(s) see it");
    }

    [Command("gps", Level = AccountLevel.Player, Help = "cluster and position of the player (where its walk has got to)")]
    public static void Gps(CommandContext ctx)
    {
        var p = ctx.RequirePlayer();
        var at = p.Movement?.PositionAt(DateTime.UtcNow) ?? new Vector2(p.Character.PositionX, p.Character.PositionY);
        ctx.Reply($"'{p.Character.Name}': cluster {p.Map?.Cluster ?? p.Character.Cluster}, position ({at.X:0.##}, {at.Y:0.##})");
    }

    [Command("ops unhandled", Level = AccountLevel.GameMaster,
        Help = "operations that came without a handler since the start, most frequent first; those answered from opcode-stubs.json apart")]
    public static void OpsUnhandled(CommandContext ctx)
    {
        static string? Line(string what, IReadOnlyDictionary<OperationCodes, long> counts) =>
            counts.OrderByDescending(u => u.Value).ThenBy(u => u.Key).ToList() is { Count: > 0 } ops
                ? $"{what}: " + string.Join(", ", ops.Select(u => $"{u.Key} x{u.Value}"))
                : null;
        var login = OpcodeTable.For(typeof(LoginSession));
        var game = OpcodeTable.For(typeof(WorldSession));
        var lines = new[] { Line("login", login.Unhandled), Line("game", game.Unhandled) }.OfType<string>().ToList();
        if (lines.Count == 0) lines.Add("no unhandled operations");
        lines.AddRange(new[] { Line("login stubbed", login.Stubbed), Line("game stubbed", game.Stubbed) }.OfType<string>());
        ctx.Reply(string.Join("\n", lines));
    }
}
