using System.Net;
using System.Text;
using AlbionLocal.Server.Commands.Builtin;
using AlbionLocal.Server.Systems;
using static AlbionLocal.Server.Systems.Checklist;

namespace AlbionLocal.Server.Web;

/// <summary>
/// The client checker's page (analysis/work/stageN/checker/SPEC.md): /admin/checks, every check of content/checks.json by area with its
/// status (filters: area, priority, status, players), the progress at the top; /admin/check?id= one check with its setup, steps, expected
/// results, what to send when broken, the saved note and a failure's log lines, and a form that sets its status (CSRF). Read and written
/// on the world thread like the game's .check.
/// </summary>
public sealed partial class WebPanel
{
    private sealed record CheckRow(Check Check, CheckStatus Status, Result? Result);

    private static readonly CheckStatus[] StatusOrder = [CheckStatus.Ok, CheckStatus.Fail, CheckStatus.Skip, CheckStatus.InProgress, CheckStatus.Todo];

    private static string StatusClass(CheckStatus s) => s switch
    {
        CheckStatus.Ok => "st-ok",
        CheckStatus.Fail => "st-fail",
        CheckStatus.Skip => "st-skip",
        CheckStatus.InProgress => "st-run",
        _ => "st-todo",
    };

    private static string StatusKey(CheckStatus s) => s switch
    {
        CheckStatus.InProgress => "run",
        _ => s.ToString().ToLowerInvariant(),
    };

    private const string ChecksCss = """
        .st{display:inline-block;min-width:4.5em;text-align:center;font-size:12px;padding:1px 6px;border-radius:4px;border:1px solid var(--line)}
        .st-ok{background:#1e8449;border-color:#1e8449;color:#fff}.st-fail{background:#c0392b;border-color:#c0392b;color:#fff}
        .st-skip{background:#7f8c8d;border-color:#7f8c8d;color:#fff}.st-run{background:#d68910;border-color:#d68910;color:#fff}.st-todo{color:var(--muted)}
        .bar{display:flex;height:14px;border-radius:7px;overflow:hidden;background:var(--line);margin:8px 0}.bar span{display:block;height:100%}
        .bar .st-ok,.bar .st-fail,.bar .st-skip,.bar .st-run{border:0;border-radius:0;padding:0}
        ol.steps li,ul.expect li{margin:3px 0}pre.setup{font:13px/1.4 ui-monospace,Consolas,monospace;background:var(--bg);padding:8px;border-radius:6px;margin:6px 0;white-space:pre-wrap}
        """;

    private async Task<WebResponse> ChecksPage(WebRequest r, AdminSession s)
    {
        var (rows, error) = await OnWorld(() =>
        {
            var c = _world.Checks;
            return (c.Checks.Select(x => new CheckRow(x, c.StatusOf(x.Id), c.ResultOf(x.Id))).ToList(), c.LoadError);
        });
        var body = new StringBuilder();
        if (rows.Count == 0)
            return Page("Checks", $"<div class=card><h2>Client checks</h2>{Note(error ?? "content/checks.json has no checks", true)}</div>", s);
        // The progress over all checks.
        var total = rows.Count;
        var counts = StatusOrder.ToDictionary(x => x, x => rows.Count(row => row.Status == x));
        var done = counts[CheckStatus.Ok] + counts[CheckStatus.Fail] + counts[CheckStatus.Skip];
        body.Append($"<div class=card><h2>Client checks: {done} of {total} done</h2><div class=bar title=\"progress\">");
        foreach (var st in StatusOrder.Where(x => x != CheckStatus.Todo && counts[x] > 0))
            body.Append($"<span class={StatusClass(st)} style=\"width:{100.0 * counts[st] / total:0.##}%\" title=\"{H(Name(st))}: {counts[st]}\"></span>");
        body.Append("</div><p>");
        foreach (var st in StatusOrder) body.Append($"<span class=\"st {StatusClass(st)}\">{H(Name(st))}</span> {counts[st]} &nbsp; ");
        body.Append($"&nbsp; ~{rows.Where(x => x.Status is CheckStatus.Todo or CheckStatus.InProgress).Sum(x => x.Check.Minutes)} min left</p>")
            .Append("<p class=muted>In the game (GameMaster): <code>.check next</code> runs the next check of the route, <code>.check &lt;id&gt;</code> a given one "
                + "(its setup as you, then its steps in chat), <code>.check ok|fail|skip &lt;id&gt; [note]</code> saves the result. Results: "
                + $"{H(_world.Checks.ResultsPath)}.</p></div>");
        // Filters (a plain GET form).
        var area = r.Q("area");
        var prio = r.Q("prio");
        var status = r.Q("status");
        var players = r.Q("players");
        var areas = rows.Select(x => x.Check.Area).Distinct().ToList();
        string Options(IEnumerable<(string Value, string Label)> options, string selected) =>
            string.Concat(options.Select(o => $"<option value=\"{H(o.Value)}\"{(o.Value == selected ? " selected" : "")}>{H(o.Label)}</option>"));
        body.Append("<div class=card><form method=get action=\"/admin/checks\" class=inline>")
            .Append("<label>Area <select name=area>").Append(Options([("", "all"), .. areas.Select(a => (a, a))], area)).Append("</select></label>")
            .Append("<label>Priority <select name=prio>").Append(Options([("", "all"), ("1", "1"), ("2", "2"), ("3", "3")], prio)).Append("</select></label>")
            .Append("<label>Status <select name=status>").Append(Options([("", "all"), .. StatusOrder.Select(x => (StatusKey(x), Name(x)))], status)).Append("</select></label>")
            .Append("<label>Players <select name=players>").Append(Options([("", "all"), ("1", "1"), ("2", "2")], players)).Append("</select></label>")
            .Append("<button>Filter</button></form>");
        var shown = rows.Where(x => (area.Length == 0 || x.Check.Area == area) && (prio.Length == 0 || x.Check.Priority.ToString() == prio)
            && (status.Length == 0 || StatusKey(x.Status) == status) && (players.Length == 0 || x.Check.Players.ToString() == players)).ToList();
        body.Append($"<p class=muted>{shown.Count} of {total} shown, in the order of the recommended route within each area.</p></div>");
        foreach (var group in shown.GroupBy(x => x.Check.Area))
        {
            var ok = group.Count(x => x.Status == CheckStatus.Ok);
            var fail = group.Count(x => x.Status == CheckStatus.Fail);
            body.Append($"<div class=card><h3>{H(group.Key)} <span class=muted>({ok} ok{(fail > 0 ? $", {fail} failed" : "")} of {group.Count()})</span></h3>")
                .Append("<div class=scroll><table class=small><tr><th>Id</th><th>Status</th><th>Title</th><th>Prio</th><th>Pl.</th><th>Min</th><th>Note</th></tr>");
            foreach (var x in group)
                body.Append($"<tr><td><a href=\"/admin/check?id={WebUtility.UrlEncode(x.Check.Id)}\">{H(x.Check.Id)}</a></td>")
                    .Append($"<td><span class=\"st {StatusClass(x.Status)}\">{H(Name(x.Status))}</span></td><td>{H(x.Check.Title)}</td>")
                    .Append($"<td>{x.Check.Priority}</td><td>{x.Check.Players}</td><td class=num>{x.Check.Minutes}</td><td>{H(Short(x.Result?.Note, 80))}</td></tr>");
            body.Append("</table></div></div>");
        }
        return Page("Checks", body.ToString(), s, head: $"<style>{ChecksCss}</style>");
    }

    private static string Short(string? text, int max) => text is null ? "" : text.Length <= max ? text : text[..max] + "…";

    private async Task<WebResponse> CheckPage(WebRequest r, AdminSession s)
    {
        var id = r.Q("id");
        var data = await OnWorld(() =>
        {
            var c = _world.Checks;
            if (c.Find(id) is not { } check) return null;
            var list = c.Checks;
            var at = list.ToList().IndexOf(check);
            return new
            {
                Row = new CheckRow(check, c.StatusOf(check.Id), c.ResultOf(check.Id)),
                Prev = at > 0 ? list[at - 1].Id : null,
                Next = at + 1 < list.Count ? list[at + 1].Id : null,
            };
        });
        if (data is null) return Text(404, $"no check '{id}'");
        var (check, status, result) = data.Row;
        var body = new StringBuilder($"<div class=card><h2>{H(check.Id)} — {H(check.Title)} <span class=\"st {StatusClass(status)}\">{H(Name(status))}</span></h2>")
            .Append("<p>")
            .Append(data.Prev is { } prev ? $"<a href=\"/admin/check?id={WebUtility.UrlEncode(prev)}\">&larr; {H(prev)}</a> &nbsp; " : "")
            .Append("<a href=\"/admin/checks\">all checks</a>")
            .Append(data.Next is { } next ? $" &nbsp; <a href=\"/admin/check?id={WebUtility.UrlEncode(next)}\">{H(next)} &rarr;</a>" : "")
            .Append("</p><table>")
            .Append(Row("Area", check.Area)).Append(Row("Priority", check.Priority.ToString())).Append(Row("Players", check.Players.ToString()))
            .Append(Row("Minutes", check.Minutes.ToString())).Append(Row("Route order", check.Order.ToString())).Append(Row("Source", check.Source))
            .Append("</table>");
        if (check.Setup.Length > 0)
            body.Append("<h3>Setup</h3><p class=muted>In the game: <code>.check ").Append(H(check.Id)).Append("</code> runs these as you, then shows the steps.</p><pre class=setup>")
                .Append(H(string.Join("\n", check.Setup))).Append("</pre>");
        if (check.Players > 1) body.Append("<p class=muted>Two players: the setup runs for the first one (A); the second (B) sees the text with <code>.check ").Append(H(check.Id)).Append(" as2</code>.</p>");
        body.Append("<h3>Steps</h3><ol class=steps>").Append(string.Concat(check.Steps.Select(x => $"<li>{H(x)}</li>"))).Append("</ol>")
            .Append("<h3>Expected</h3><ul class=expect>").Append(string.Concat(check.Expect.Select(x => $"<li>{H(x)}</li>"))).Append("</ul>");
        if (check.IfBroken.Length > 0) body.Append("<h3>If broken</h3><p>").Append(H(check.IfBroken)).Append("</p>");
        body.Append("</div><div class=card><h3>Result</h3>");
        if (result is null) body.Append("<p class=muted>Not checked yet.</p>");
        else
        {
            body.Append("<table>").Append(Row("Status", Name(result.Status))).Append(result.Note is { } note ? Row("Note", note) : "")
                .Append(Row("By", $"{result.Account ?? "?"} / {result.Character ?? "?"}{(result.Cluster is { } cl ? $" on {cl}" : "")}"))
                .Append(Row("When", $"{result.Time:yyyy-MM-dd HH:mm:ss} UTC")).Append(Row("Server build", result.Build ?? "?")).Append("</table>");
            if (result.Log is { Length: > 0 } log)
                body.Append($"<details{(result.Status == CheckStatus.Fail ? " open" : "")}><summary>Log at that moment ({log.Length} lines)</summary><pre class=log>")
                    .Append(H(string.Join("\n", log))).Append("</pre></details>");
        }
        body.Append("<h3>Set the status</h3>")
            .Append(Form(s, "/admin/check", "Save", "", ("id", check.Id),
                "<select name=status aria-label=status>" + string.Concat(new[] { CheckStatus.Ok, CheckStatus.Fail, CheckStatus.Skip, CheckStatus.InProgress, CheckStatus.Todo }
                    .Select(x => $"<option value={StatusKey(x)}{(x == status ? " selected" : "")}>{H(Name(x))}</option>"))
                + "</select><input name=note maxlength=1000 style=\"min-width:22em\" placeholder=\"note (what you saw)\" aria-label=note value=\"" + H(result?.Note) + "\">"))
            .Append("<p class=muted>A failure keeps the last 50 server log lines of the moment it is saved.</p></div>");
        return Page(check.Id, body.ToString(), s, head: $"<style>{ChecksCss}</style>");
    }

    private async Task<WebResponse> CheckSet(WebRequest r, AdminSession s)
    {
        var id = r.F("id");
        CheckStatus? status = r.F("status") switch
        {
            "ok" => CheckStatus.Ok,
            "fail" => CheckStatus.Fail,
            "skip" => CheckStatus.Skip,
            "run" => CheckStatus.InProgress,
            "todo" => CheckStatus.Todo,
            _ => null,
        };
        if (status is not { } st) return Text(400, "no such status");
        var note = new string([.. r.F("note").Where(ch => ch >= ' ')]).Trim();
        if (note.Length > 1000) note = note[..1000];
        var found = await OnWorld(() =>
        {
            if (_world.Checks.Find(id) is not { } check) return null;
            _world.Checks.Set(check.Id, st, note, $"web:{s.Name}", null);
            return check.Id;
        });
        if (found is null) return Text(404, $"no check '{id}'");
        Audit($"admin '{s.Name}'", r.Remote, $"check {found}: {StatusKey(st)}");
        Flash(s, $"{found}: {Name(st)}", false);
        return WebResponse.Redirect($"/admin/check?id={WebUtility.UrlEncode(found)}");
    }
}
