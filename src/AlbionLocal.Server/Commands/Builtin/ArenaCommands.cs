using System.Globalization;
using AlbionLocal.Server.Persistence;
using AlbionLocal.Server.Systems;

namespace AlbionLocal.Server.Commands.Builtin;

/// <summary>
/// The arena, the ladders and the tournament (analysis/work/stageN/arena/SPEC.md §7): .arena join/leave/accept/status for every player
/// (the arena agency does the same through the client), .arena end for GMs; .ladder and .rating; .tournament for everyone, its open, start,
/// win and cancel for GMs.
/// </summary>
public static class ArenaCommands
{
    /// <summary>.arena join &lt;1v1|2v2|5v5&gt; [group]: into the queue (with the party's online members when "group").</summary>
    [Command("arena join", Level = AccountLevel.Player, Help = "joins the arena queue: 1v1, 2v2 or 5v5; add 'group' to queue with your party")]
    public static void Join(CommandContext ctx, string mode, string group = "")
    {
        var player = ctx.RequirePlayer();
        if (Arenas.ModeOf(mode) is not { } m) throw new CommandError($"{mode}: one of {string.Join(", ", Arenas.Modes.Select(x => x.Name))}");
        if (ctx.World.Arenas.Register(player, m, group.Equals("group", StringComparison.OrdinalIgnoreCase)) is { } why) throw new CommandError(why);
    }

    /// <summary>.arena leave: out of the queue, the ready check or the match (a forfeit while it runs).</summary>
    [Command("arena leave", Level = AccountLevel.Player, Help = "leaves the arena queue, or the arena match you are in (a forfeit)")]
    public static void Leave(CommandContext ctx)
    {
        var player = ctx.RequirePlayer();
        var arenas = ctx.World.Arenas;
        if (arenas.Find(player.Map?.Cluster) is { } m) arenas.Leave(player, m, "command");
        else if (!arenas.Unregister(player, "left")) throw new CommandError("you are in no arena queue or match");
    }

    /// <summary>.arena accept: accepts the ready check (as the client's popup).</summary>
    [Command("arena accept", Level = AccountLevel.Player, Help = "accepts the arena match that is ready")]
    public static void Accept(CommandContext ctx)
    {
        if (!ctx.World.Arenas.Accept(ctx.RequirePlayer())) throw new CommandError("no arena match waits for you");
        ctx.Reply("accepted");
    }

    /// <summary>.arena status: the queues and the matches.</summary>
    [Command("arena status", Level = AccountLevel.Player, Help = "shows the arena queues and matches")]
    public static void Status(CommandContext ctx)
    {
        var lines = ctx.World.Arenas.Describe().ToList();
        ctx.Reply(lines.Count == 0 ? "the arena is empty (.arena join 1v1|2v2|5v5)" : "arena:\n" + string.Join("\n", lines));
    }

    /// <summary>.arena end &lt;blue|red|draw&gt; [player]: a GM ends the match of the player (or the one here) with that result.</summary>
    [Command("arena end", Level = AccountLevel.GameMaster, Help = "ends the arena match here: blue, red or draw")]
    public static void End(CommandContext ctx, string result)
    {
        var team = result.ToLowerInvariant() switch { "blue" or "0" => 0, "red" or "1" => 1, "draw" => -1, _ => throw new CommandError("blue, red or draw") };
        if (ctx.World.Arenas.Finish(ctx.RequirePlayer(), team) is { } why) throw new CommandError(why);
    }

    /// <summary>.ladder [duel|1v1|2v2|5v5] [n]: the best n (10) of a ladder (duel by default).</summary>
    [Command("ladder", Level = AccountLevel.Player, Help = "shows the best players of a ladder: duel, 1v1, 2v2 or 5v5 [count]")]
    public static void Ladder(CommandContext ctx, string ladder = "duel", int count = 10)
    {
        if (Ratings.LadderOf(ladder) is not { } l) throw new CommandError($"{ladder}: one of {string.Join(", ", Ratings.Ladders)}");
        var top = ctx.World.Ratings.Top(l, Math.Clamp(count, 1, 50));
        if (top.Count == 0)
        {
            ctx.Reply($"the {l} ladder is empty");
            return;
        }
        ctx.Reply($"{l} ladder:\n" + string.Join("\n", top.Select((r, i) => string.Create(CultureInfo.InvariantCulture, $"  {i + 1}. {r.Name} {r.Rating} ({r.Wins}-{r.Losses}{(r.Draws > 0 ? $"-{r.Draws}" : "")})"))));
    }

    /// <summary>.rating [name]: the ratings and places of a character (yourself by default) on every ladder.</summary>
    [Command("rating", Level = AccountLevel.Player, Help = "shows your ratings (or a player's) on the duel and arena ladders")]
    public static void Rating(CommandContext ctx, string name = "")
    {
        var ratings = ctx.World.Ratings;
        Guid id;
        string who;
        if (name.Length == 0)
        {
            var player = ctx.RequirePlayer();
            (id, who) = (player.Character.Id, player.Character.Name);
        }
        else if (ratings.FindByName(name) is { } found) (id, who) = (found, name);
        else throw new CommandError($"{name} has no rating yet");
        var parts = Ratings.Ladders.Select(l => ratings.Find(id, l) is { } r
            ? string.Create(CultureInfo.InvariantCulture, $"{l} {r.Rating} (#{ratings.RankOf(id, l)}, {r.Wins}-{r.Losses}{(r.Draws > 0 ? $"-{r.Draws}" : "")})")
            : string.Create(CultureInfo.InvariantCulture, $"{l} {ratings.RatingOf(id, l)} (unrated)"));
        ctx.Reply($"{who}: {string.Join(", ", parts)}");
    }

    /// <summary>.tournament [join|leave]: the tournament's state, or signing up / off.</summary>
    [Command("tournament", Level = AccountLevel.Player, Help = "the duel tournament: status, or join / leave its sign-up")]
    public static void Tournament(CommandContext ctx, string action = "")
    {
        var t = ctx.World.Tournament;
        switch (action.ToLowerInvariant())
        {
            case "":
            case "status":
                ctx.Reply(t.Describe());
                break;
            case "join":
                if (t.Join(ctx.RequirePlayer()) is { } no) throw new CommandError(no);
                break;
            case "leave":
                if (t.Leave(ctx.RequirePlayer()) is { } notIn) throw new CommandError(notIn);
                ctx.Reply("signed off");
                break;
            default:
                throw new CommandError("status, join or leave (GMs: .tournament open|start|win|cancel)");
        }
    }

    [Command("tournament open", Level = AccountLevel.GameMaster, Help = "opens a duel tournament's sign-up")]
    public static void Open(CommandContext ctx) => ctx.World.Tournament.Open();

    [Command("tournament start", Level = AccountLevel.GameMaster, Help = "closes the sign-up and pairs the first round (single elimination)")]
    public static void Start(CommandContext ctx)
    {
        if (ctx.World.Tournament.Start() is { } why) throw new CommandError(why);
    }

    [Command("tournament win", Level = AccountLevel.GameMaster, Help = "decides the open match of a player for that player")]
    public static void Win(CommandContext ctx, string name)
    {
        if (ctx.World.Tournament.SetWinner(name) is { } why) throw new CommandError(why);
    }

    [Command("tournament cancel", Level = AccountLevel.GameMaster, Help = "ends the tournament")]
    public static void Cancel(CommandContext ctx) => ctx.World.Tournament.Cancel();
}
