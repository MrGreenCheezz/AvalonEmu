using System.Globalization;
using AlbionLocal.Server.Persistence;
using AlbionLocal.Server.Systems;
using static AlbionLocal.Server.Systems.Checklist;

namespace AlbionLocal.Server.Commands.Builtin;

/// <summary>
/// The client checker (analysis/work/stageN/checker/SPEC.md, <see cref="Checklist"/>): .check &lt;id&gt; runs a check of content/checks.json
/// (its setup as the player, then its text in chat), .check next, .check list, .check ok / fail / skip, .check reset, .check status. The
/// results go to data/checks-results.json; the web page /admin/checks shows and sets them too.
/// </summary>
public static class CheckCommands
{
    /// <summary>Lines per page of .check list.</summary>
    public const int PageSize = 15;

    private static Checklist Checks(CommandContext ctx) =>
        ctx.World.Checks.Checks.Count > 0 ? ctx.World.Checks : throw new CommandError($"no checks: {ctx.World.Checks.LoadError ?? "content/checks.json is empty"}");

    /// <summary>
    /// .check &lt;id&gt; [as2]: the check in progress, its setup lines run as the player one by one (a cluster change waited out), then its
    /// steps and expected results in chat. as2 (the second player of a two-player check) or show: only the text, no setup.
    /// </summary>
    [Command("check", Level = AccountLevel.GameMaster, Help = "runs a check of content/checks.json: its setup as you, then its steps in chat ('as2' or 'show': the text only)")]
    public static void Run(CommandContext ctx, string id, string mode = "")
    {
        var player = ctx.RequirePlayer();
        var checks = Checks(ctx);
        var check = checks.Find(id) ?? throw new CommandError($"no check {id} (.check list, .check next)");
        var showOnly = mode.ToLowerInvariant() switch
        {
            "" => false,
            "as2" or "show" => true,
            _ => throw new CommandError($"'{mode}': only as2 or show may follow the id"),
        };
        checks.Start(player, ctx.Level, check, showOnly);
    }

    /// <summary>.check next: the next check of the route still to do (content/checks.json "order") runs as .check &lt;id&gt; does.</summary>
    [Command("check next", Level = AccountLevel.GameMaster, Help = "runs the next check of the recommended route that is still to do")]
    public static void Next(CommandContext ctx)
    {
        var player = ctx.RequirePlayer();
        var checks = Checks(ctx);
        var next = checks.Next() ?? throw new CommandError("every check has a result (.check status; .check reset <id> to do one again)");
        checks.Start(player, ctx.Level, next, showOnly: false);
    }

    /// <summary>
    /// .check list [filter] [page]: id, status and title of the checks in route order, <see cref="PageSize"/> a page. The filter: todo, run, ok,
    /// fail, skip (a status), p1-p3 (a priority), 2p (two players), an id prefix (MOB) or a part of an area's name ("Мобы").
    /// </summary>
    [Command("check list", Level = AccountLevel.GameMaster, Help = "the checks with their status [todo|run|ok|fail|skip|p1|p2|p3|2p|id prefix|area] [page]")]
    public static void List(CommandContext ctx, string filter = "", int page = 1)
    {
        var checks = Checks(ctx);
        if (int.TryParse(filter, NumberStyles.Integer, CultureInfo.InvariantCulture, out var asPage))
            (filter, page) = ("", asPage);
        var match = Filter(checks, filter);
        var list = checks.Checks.Where(match).ToList();
        if (list.Count == 0) throw new CommandError($"no check matches '{filter}'");
        var pages = (list.Count + PageSize - 1) / PageSize;
        page = Math.Clamp(page, 1, pages);
        var lines = list.Skip((page - 1) * PageSize).Take(PageSize)
            .Select(c => $"{c.Id} [{Name(checks.StatusOf(c.Id))}] {c.Title}{(c.Players > 1 ? " (вдвоём)" : "")}");
        ctx.Reply($"проверки{(filter.Length > 0 ? $" «{filter}»" : "")}: {list.Count}, страница {page}/{pages}\n" + string.Join("\n", lines)
            + (page < pages ? $"\nдальше: .check list {(filter.Length > 0 ? QuoteIfSpaced(filter) + " " : "")}{page + 1}" : ""));
    }

    private static string QuoteIfSpaced(string s) => s.Contains(' ') ? $"\"{s}\"" : s;

    /// <summary>The checks a .check list / web filter word keeps.</summary>
    public static Func<Check, bool> Filter(Checklist checks, string filter)
    {
        var f = filter.Trim();
        return f.ToLowerInvariant() switch
        {
            "" or "all" => _ => true,
            "todo" => c => checks.StatusOf(c.Id) == CheckStatus.Todo,
            "run" or "progress" => c => checks.StatusOf(c.Id) == CheckStatus.InProgress,
            "ok" => c => checks.StatusOf(c.Id) == CheckStatus.Ok,
            "fail" => c => checks.StatusOf(c.Id) == CheckStatus.Fail,
            "skip" => c => checks.StatusOf(c.Id) == CheckStatus.Skip,
            "p1" or "p2" or "p3" => c => c.Priority == f[1] - '0',
            "2p" => c => c.Players > 1,
            _ => c => c.Id.StartsWith(f, StringComparison.OrdinalIgnoreCase) || c.Area.Contains(f, StringComparison.OrdinalIgnoreCase),
        };
    }

    [Command("check ok", Level = AccountLevel.GameMaster, Help = "marks a check passed: [id] [note] (no id: the one you ran last)")]
    public static void Ok(CommandContext ctx, Tail text = default) => SetStatus(ctx, CheckStatus.Ok, text, noteNeeded: false);

    [Command("check fail", Level = AccountLevel.GameMaster, Help = "marks a check failed: [id] <what is wrong> (the last 50 log lines are kept with it)")]
    public static void Fail(CommandContext ctx, Tail text) => SetStatus(ctx, CheckStatus.Fail, text, noteNeeded: true);

    [Command("check skip", Level = AccountLevel.GameMaster, Help = "marks a check skipped: [id] [note]")]
    public static void Skip(CommandContext ctx, Tail text = default) => SetStatus(ctx, CheckStatus.Skip, text, noteNeeded: false);

    /// <summary>The first word is a check id, else the check the player ran last; the rest is the note.</summary>
    private static void SetStatus(CommandContext ctx, CheckStatus status, Tail text, bool noteNeeded)
    {
        var checks = Checks(ctx);
        var rest = text.ToString().Trim();
        var first = rest.Split(' ', 2, StringSplitOptions.RemoveEmptyEntries).FirstOrDefault() ?? "";
        Check? check = first.Length > 0 ? checks.Find(first) : null;
        if (check is not null) rest = rest[first.Length..].Trim();
        else check = ctx.Player is { } p ? checks.CurrentOf(p.Character.Id) : null;
        if (check is null) throw new CommandError(first.Length > 0 ? $"no check {first}" : "which check? .check ok|fail|skip <id> [note]");
        if (noteNeeded && rest.Length == 0) throw new CommandError($"say what is wrong: .check fail {check.Id} <what you see>");
        var player = ctx.Player;
        var result = checks.Set(check.Id, status, rest, player is null ? "console" : checks.AccountOf(player), player?.Character.Name, player?.Map?.Cluster);
        var next = checks.Next();
        ctx.Reply($"{check.Id}: {Name(status)}{(result?.Note is { } n ? $" — {n}" : "")}{(status == CheckStatus.Fail ? $" (сохранено {result?.Log?.Length ?? 0} строк журнала)" : "")}"
            + (next is not null ? $"; дальше: {next.Id} {next.Title} (.check next)" : "; все проверки пройдены"));
    }

    /// <summary>.check reset &lt;id|all&gt;: back to todo (the result removed).</summary>
    [Command("check reset", Level = AccountLevel.GameMaster, Help = "sets a check (or all) back to todo")]
    public static void Reset(CommandContext ctx, string id)
    {
        var checks = Checks(ctx);
        if (id.Equals("all", StringComparison.OrdinalIgnoreCase))
        {
            ctx.Reply($"{checks.ResetAll()} result(s) removed: every check is todo again");
            return;
        }
        var check = checks.Find(id) ?? throw new CommandError($"no check {id}");
        checks.Set(check.Id, CheckStatus.Todo, null, ctx.Player is { } p ? checks.AccountOf(p) : "console", ctx.Player?.Character.Name);
        ctx.Reply($"{check.Id}: {Name(CheckStatus.Todo)}");
    }

    /// <summary>.check status: the counts by status, then per area.</summary>
    [Command("check status", Level = AccountLevel.GameMaster, Help = "how many checks are done, failed, skipped and left, overall and per area")]
    public static void Status(CommandContext ctx)
    {
        var checks = Checks(ctx);
        string Counts(IEnumerable<Check> list)
        {
            var by = list.GroupBy(c => checks.StatusOf(c.Id)).ToDictionary(g => g.Key, g => g.Count());
            int N(CheckStatus s) => by.GetValueOrDefault(s);
            return $"ок {N(CheckStatus.Ok)}, сбой {N(CheckStatus.Fail)}, пропуск {N(CheckStatus.Skip)}, идёт {N(CheckStatus.InProgress)}, ждёт {N(CheckStatus.Todo)}";
        }
        var all = checks.Checks;
        var lines = new List<string> { $"всего {all.Count}: {Counts(all)}" };
        lines.AddRange(all.GroupBy(c => c.Area).Select(g => $"{g.Key} ({g.Count()}): {Counts(g)}"));
        ctx.Reply(string.Join("\n", lines));
    }
}
