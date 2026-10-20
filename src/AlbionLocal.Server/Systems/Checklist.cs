using System.Globalization;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;
using AlbionLocal.Photon;
using AlbionLocal.Server.Commands;
using AlbionLocal.Server.Entities;
using AlbionLocal.Server.Persistence;

namespace AlbionLocal.Server.Systems;

/// <summary>
/// The client checker (analysis/work/stageN/checker/SPEC.md): the checks of content/checks.json (what still has to be seen in the real client,
/// Reverse/CHECKLIST.md is made from the same file), the GM's .check commands and the web page /admin/checks. .check &lt;id&gt; runs the
/// check's setup lines as the player through the command dispatcher, one by one, waiting for a cluster change to end with the next Join
/// (<see cref="Update"/>), then shows its steps and expected results in chat. The results (status, note, who, when, the server build, a
/// failure's last log lines) go to data/checks-results.json, written at once; checks.json is read again when it changes on disk.
/// Touched only under <see cref="World.Sync"/>.
/// </summary>
public sealed class Checklist(World world)
{
    public enum CheckStatus { Todo, InProgress, Ok, Fail, Skip }

    /// <summary>One check of content/checks.json (the schema is in its header).</summary>
    public sealed record Check
    {
        public string Id { get; init; } = "";
        public int Order { get; init; }
        public string Area { get; init; } = "";
        public string Title { get; init; } = "";
        public int Priority { get; init; } = 2;
        public int Players { get; init; } = 1;
        public int Minutes { get; init; }
        public string[] Setup { get; init; } = [];
        public string[] Steps { get; init; } = [];
        public string[] Expect { get; init; } = [];
        public string IfBroken { get; init; } = "";
        public string Source { get; init; } = "";
        public string[] Needs { get; init; } = [];
    }

    /// <summary>A saved result: who set it and when, on which build; a failure keeps the log lines of that moment.</summary>
    public sealed class Result
    {
        public CheckStatus Status { get; set; }
        public string? Note { get; set; }
        public string? Account { get; set; }
        public string? Character { get; set; }
        public string? Cluster { get; set; }
        public DateTime Time { get; set; }
        public string? Build { get; set; }
        public string[]? Log { get; set; }
    }

    private sealed class ResultsFile
    {
        public DateTime Updated { get; set; }
        public SortedDictionary<string, Result> Results { get; set; } = new(StringComparer.Ordinal);
    }

    /// <summary>How many log lines a failure keeps.</summary>
    public const int LogLines = 50;

    /// <summary>The pause between two setup lines.</summary>
    public static readonly TimeSpan Step = TimeSpan.FromSeconds(0.4);

    /// <summary>The pause after the Join that ends a cluster change, before the next line (the client is still loading the map).</summary>
    public static readonly TimeSpan Settle = TimeSpan.FromSeconds(2);

    /// <summary>How long a cluster change of a setup line may take before the rest of the setup is dropped.</summary>
    public static readonly TimeSpan MoveTimeout = TimeSpan.FromSeconds(90);

    /// <summary>The commands that change the cluster: the next line waits for the player's new Join.</summary>
    public static readonly string[] Movers = ["tele", "island go", "hg enter", "cd enter", "exp start"];

    private static readonly JsonSerializerOptions ReadOptions = new()
    {
        ReadCommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true, PropertyNameCaseInsensitive = true,
    };

    private static readonly JsonSerializerOptions WriteOptions = new()
    {
        WriteIndented = true, PropertyNamingPolicy = JsonNamingPolicy.CamelCase, DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping, PropertyNameCaseInsensitive = true,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.KebabCaseLower) },
    };

    private List<Check> _checks = [];
    private DateTime _checksStamp;
    private string? _loadError;
    private ResultsFile? _results;
    private readonly Dictionary<Guid, Run> _runs = [];
    private readonly Dictionary<Guid, string> _current = [];

    /// <summary>content/checks.json (Paths:Content).</summary>
    public string ChecksPath { get; init; } = Path.Combine(world.Config.ContentPath, "checks.json");

    /// <summary>data/checks-results.json (Paths:Saves).</summary>
    public string ResultsPath { get; init; } = Path.Combine(world.Config.SavesPath, "checks-results.json");

    /// <summary>The server build a result records (the web panel's).</summary>
    public string Build { get; init; } = Web.WebPanel.ServerBuild;

    /// <summary>The checks in route order (read again when the file has changed); empty when it is missing or broken (<see cref="LoadError"/>).</summary>
    public IReadOnlyList<Check> Checks
    {
        get
        {
            var stamp = File.Exists(ChecksPath) ? File.GetLastWriteTimeUtc(ChecksPath) : DateTime.MinValue;
            if (stamp == _checksStamp && (_checks.Count > 0 || _loadError is not null)) return _checks;
            _checksStamp = stamp;
            try
            {
                _checks = stamp == DateTime.MinValue ? [] : Load(File.ReadAllText(ChecksPath));
                _loadError = stamp == DateTime.MinValue ? $"{ChecksPath} is missing" : null;
            }
            catch (Exception e) when (e is JsonException or IOException or UnauthorizedAccessException)
            {
                _checks = [];
                _loadError = $"{ChecksPath}: {e.Message}";
                Log.Warn("check", _loadError);
            }
            return _checks;
        }
    }

    /// <summary>Why <see cref="Checks"/> is empty; null when it loaded.</summary>
    public string? LoadError
    {
        get
        {
            _ = Checks;
            return _loadError;
        }
    }

    /// <summary>The checks of a checks.json text (comments and trailing commas allowed), by <see cref="Check.Order"/>, then id.</summary>
    public static List<Check> Load(string json) =>
        [.. (JsonSerializer.Deserialize<List<Check>>(json, ReadOptions) ?? []).OrderBy(c => c.Order == 0 ? int.MaxValue : c.Order).ThenBy(c => c.Id, StringComparer.Ordinal)];

    public Check? Find(string id) => Checks.FirstOrDefault(c => c.Id.Equals(id.Trim(), StringComparison.OrdinalIgnoreCase));

    private ResultsFile Results
    {
        get
        {
            if (_results is not null) return _results;
            try
            {
                _results = File.Exists(ResultsPath) ? JsonSerializer.Deserialize<ResultsFile>(File.ReadAllText(ResultsPath), WriteOptions) : null;
            }
            catch (Exception e) when (e is JsonException or IOException or UnauthorizedAccessException)
            {
                Log.Warn("check", $"{ResultsPath} not read ({e.Message}): starting with no results; the old file is kept as .bad");
                try { File.Copy(ResultsPath, ResultsPath + ".bad", overwrite: true); } catch (IOException) { }
            }
            _results ??= new ResultsFile();
            if (_results.Results.Comparer != StringComparer.Ordinal) _results.Results = new(_results.Results, StringComparer.Ordinal);
            return _results;
        }
    }

    /// <summary>The saved result of <paramref name="id"/>; null = never touched (todo).</summary>
    public Result? ResultOf(string id) => Results.Results.GetValueOrDefault(id);

    public CheckStatus StatusOf(string id) => ResultOf(id)?.Status ?? CheckStatus.Todo;

    /// <summary>
    /// Sets <paramref name="id"/>'s status with <paramref name="note"/>, who and where, the time and build; a failure keeps the last
    /// <see cref="LogLines"/> log lines. Written at once. <see cref="CheckStatus.Todo"/> removes the result.
    /// </summary>
    public Result? Set(string id, CheckStatus status, string? note, string? account, string? character, string? cluster = null)
    {
        var results = Results.Results;
        Result? result = null;
        if (status == CheckStatus.Todo) results.Remove(id);
        else
        {
            result = new Result
            {
                Status = status, Note = string.IsNullOrWhiteSpace(note) ? null : note.Trim(), Account = account, Character = character, Cluster = cluster,
                Time = DateTime.UtcNow, Build = Build, Log = status == CheckStatus.Fail ? Log.Recent(LogLines) : null,
            };
            results[id] = result;
        }
        Save();
        Log.Info("check", $"{id}: {Name(status)}{(result?.Note is { } n ? $" ({n})" : "")} by {account ?? "?"}/{character ?? "?"}");
        return result;
    }

    /// <summary>Every result back to todo.</summary>
    public int ResetAll()
    {
        var n = Results.Results.Count;
        Results.Results.Clear();
        Save();
        return n;
    }

    private void Save()
    {
        var file = Results;
        file.Updated = DateTime.UtcNow;
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(ResultsPath))!);
        var tmp = ResultsPath + ".tmp";
        File.WriteAllText(tmp, JsonSerializer.Serialize(file, WriteOptions), new UTF8Encoding(false));
        File.Move(tmp, ResultsPath, overwrite: true);
    }

    /// <summary>The first check in route order still to do; else the first one in progress; null when every check has a result.</summary>
    public Check? Next(Func<Check, bool>? filter = null)
    {
        var list = Checks.Where(c => filter?.Invoke(c) != false).ToList();
        return list.FirstOrDefault(c => StatusOf(c.Id) == CheckStatus.Todo) ?? list.FirstOrDefault(c => StatusOf(c.Id) == CheckStatus.InProgress);
    }

    /// <summary>The check <paramref name="character"/> started last (.check ok without an id).</summary>
    public Check? CurrentOf(Guid character) => _current.TryGetValue(character, out var id) ? Find(id) : null;

    /// <summary>The Russian word of a status, as chat and the web page show it.</summary>
    public static string Name(CheckStatus s) => s switch
    {
        CheckStatus.InProgress => "идёт",
        CheckStatus.Ok => "ок",
        CheckStatus.Fail => "СБОЙ",
        CheckStatus.Skip => "пропуск",
        _ => "ждёт",
    };

    // ---- the setup runner

    private sealed class Run
    {
        public required Check Check { get; init; }
        public required Guid Character { get; init; }
        public required AccountLevel Level { get; init; }
        public required Queue<string> Lines { get; init; }
        public Player? Before { get; set; }
        public bool WaitMove { get; set; }
        public bool LastError { get; set; }
        public int Errors { get; set; }
        public TimeSpan NextAt { get; set; }
        public TimeSpan Deadline { get; set; }
    }

    /// <summary>Whether a setup run of <paramref name="character"/> is under way.</summary>
    public bool Running(Guid character) => _runs.ContainsKey(character);

    /// <summary>
    /// .check &lt;id&gt;: <paramref name="c"/> is in progress for <paramref name="p"/>; its setup lines ({me} = the character's name) run as the
    /// player at <paramref name="level"/> from the next tick (<see cref="Update"/>), then the text follows. <paramref name="showOnly"/>: no setup
    /// (the second player of a two-player check, or a look), the text at once.
    /// </summary>
    public void Start(Player p, AccountLevel level, Check c, bool showOnly)
    {
        var who = p.Character;
        _current[who.Id] = c.Id;
        if (_runs.Remove(who.Id, out var old)) Tell(p, $"подготовка {old.Check.Id} прервана", true);
        if (!showOnly && StatusOf(c.Id) is CheckStatus.Todo or CheckStatus.InProgress)
            Set(c.Id, CheckStatus.InProgress, ResultOf(c.Id)?.Note, AccountOf(p), who.Name, p.Map?.Cluster);
        if (showOnly || c.Setup.Length == 0)
        {
            Tell(p, Describe(c, showOnly && c.Setup.Length > 0 ? "подготовка не выполнялась (только текст)" : null), false);
            return;
        }
        Tell(p, $"{c.Id}: подготовка, {c.Setup.Length} команд(ы)…", false);
        _runs[who.Id] = new Run
        {
            Check = c, Character = who.Id, Level = level, NextAt = world.Now,
            Lines = new Queue<string>(c.Setup.Select(l => l.Replace("{me}", who.Name, StringComparison.Ordinal))),
        };
    }

    /// <summary>The account name a result records for <paramref name="p"/> (the login name, never anything secret).</summary>
    public string AccountOf(Player p) => world.Characters.FindAccount(p.Character.Account)?.Shown ?? "?";

    /// <summary>Every tick: the next setup line of each run that is due; a run waiting for a cluster change goes on after the new Join.</summary>
    public void Update()
    {
        if (_runs.Count == 0) return;
        var now = world.Now;
        foreach (var run in _runs.Values.ToList())
        {
            if (now < run.NextAt) continue;
            var p = Live(run.Character);
            if (run.WaitMove && !run.LastError)
            {
                if (p is null || p == run.Before || p.Map is null)
                {
                    if (now > run.Deadline) Abort(run, p, "переход на другой кластер не закончился за 90 с");
                    continue;
                }
                run.WaitMove = false;
                run.NextAt = now + Settle; // joined: the client is still loading
                continue;
            }
            run.WaitMove = false;
            if (p is null)
            {
                if (now > run.Deadline) Abort(run, null, "игрок вышел из игры");
                continue;
            }
            if (run.Lines.Count == 0)
            {
                _runs.Remove(run.Character);
                Tell(p, Describe(run.Check, run.Errors > 0 ? $"подготовка: {run.Errors} команд(ы) ответили ошибкой (выше)" : null), false);
                continue;
            }
            var line = run.Lines.Dequeue();
            run.Before = p;
            run.LastError = false;
            Tell(p, $"» {line}", false);
            Log.Info("check", $"{run.Check.Id} setup for '{p.Character.Name}': {world.Commands.Redact(line)}");
            world.Commands.Execute(new CommandContext(world, p, run.Level, (text, error) =>
            {
                if (error)
                {
                    run.LastError = true;
                    run.Errors++;
                }
                Tell(Live(run.Character) ?? p, text, error);
            }), line);
            run.NextAt = now + Step;
            run.Deadline = now + MoveTimeout;
            var moved = p.Session.Player != p || p.Map is null;
            run.WaitMove = !run.LastError && (moved || IsMover(line));
        }
    }

    private void Abort(Run run, Player? p, string why)
    {
        _runs.Remove(run.Character);
        Log.Warn("check", $"{run.Check.Id}: setup stopped: {why} ({run.Lines.Count} line(s) left)");
        if (p is not null) Tell(p, $"{run.Check.Id}: подготовка остановлена — {why}; осталось команд: {run.Lines.Count}", true);
    }

    /// <summary>Whether <paramref name="line"/> runs one of the <see cref="Movers"/>.</summary>
    public static bool IsMover(string line)
    {
        var words = string.Join(' ', line.Trim().TrimStart('.').Split(' ', StringSplitOptions.RemoveEmptyEntries)).ToLowerInvariant();
        return Movers.Any(m => words == m || words.StartsWith(m + " ", StringComparison.Ordinal));
    }

    /// <summary>The live player of <paramref name="character"/> on a map (the newest Join); null while it changes cluster or is offline.</summary>
    private Player? Live(Guid character) => world.Maps.Maps.SelectMany(m => m.Players.Values)
        .Where(p => !p.IsBot && p.Character.Id == character).MaxBy(p => p.Id);

    private static void Tell(Player p, string text, bool error) => p.Session.CommandReply(text, error);

    /// <summary>The check as chat lines (Russian): title, steps, expected, what to send when broken, how to answer.</summary>
    public string Describe(Check c, string? remark)
    {
        var s = new StringBuilder();
        s.Append(CultureInfo.InvariantCulture, $"== {c.Id}: {c.Title} ==\n");
        s.Append(CultureInfo.InvariantCulture, $"{c.Area}; приоритет {c.Priority}; ~{c.Minutes} мин; статус: {Name(StatusOf(c.Id))}\n");
        if (remark is not null) s.Append(remark).Append('\n');
        if (c.Players >= 2)
            s.Append("Вдвоём: подготовка выполняется у первого игрока (A); второй игрок (Б) делает в шагах свою часть — ему можно показать текст: .check ")
                .Append(c.Id).Append(" as2\n");
        s.Append("Шаги:\n");
        for (var i = 0; i < c.Steps.Length; i++) s.Append(CultureInfo.InvariantCulture, $"{i + 1}. {c.Steps[i]}\n");
        s.Append("Ожидается:\n");
        foreach (var e in c.Expect) s.Append("- ").Append(e).Append('\n');
        if (c.IfBroken.Length > 0) s.Append("Если сломано: ").Append(c.IfBroken).Append('\n');
        s.Append(CultureInfo.InvariantCulture, $"Итог: .check ok {c.Id} [заметка] | .check fail {c.Id} <что не так> | .check skip {c.Id}");
        return s.ToString();
    }

    // ---- validation (the self-test)

    /// <summary>
    /// What is wrong with <paramref name="checks"/>: missing or repeated ids, an order that is not 1..n each once, and every setup line ({me} as
    /// "Tester") that the dispatcher of <paramref name="ctx"/> cannot parse (unknown command, bad or missing argument, an unknown item or mob).
    /// Parsed only, nothing runs.
    /// </summary>
    public static List<string> Validate(IReadOnlyList<Check> checks, CommandContext ctx)
    {
        var errors = new List<string>();
        foreach (var dup in checks.GroupBy(c => c.Id, StringComparer.OrdinalIgnoreCase).Where(g => g.Key.Length == 0 || g.Count() > 1))
            errors.Add($"id '{dup.Key}' used {dup.Count()} times");
        var orders = checks.Select(c => c.Order).Order().ToList();
        if (!orders.SequenceEqual(Enumerable.Range(1, checks.Count)))
            errors.Add($"order is not 1..{checks.Count} each once (missing: {string.Join(", ", Enumerable.Range(1, checks.Count).Except(orders).Take(10))}; "
                + $"repeated: {string.Join(", ", orders.GroupBy(o => o).Where(g => g.Count() > 1).Select(g => g.Key).Take(10))})");
        foreach (var c in checks)
        {
            if (c.Title.Length == 0 || c.Area.Length == 0 || c.Steps.Length == 0 || c.Expect.Length == 0) errors.Add($"{c.Id}: title, area, steps or expect missing");
            if (c.Players is < 1 or > 2 || c.Priority is < 1 or > 3) errors.Add($"{c.Id}: players {c.Players} / priority {c.Priority} out of range");
            foreach (var line in c.Setup)
            {
                if (!line.StartsWith('.')) errors.Add($"{c.Id}: '{line}' does not start with '.'");
                try { ctx.World.Commands.Parse(ctx, line.Replace("{me}", "Tester", StringComparison.Ordinal)); }
                catch (CommandError e) { errors.Add($"{c.Id}: '{line}': {e.Message}"); }
            }
        }
        return errors;
    }
}
