using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace AlbionLocal.Server.Rules;

/// <summary>A problem of a rule: blocker (the rule is not loaded) or warning, a JSON pointer into the rule, a message.</summary>
public sealed record RuleProblem(string Severity, string Path, string Message, string Pack = "", string Rule = "")
{
    public bool Blocker => Severity == "blocker";

    public JsonObject ToJson()
    {
        var (ru, en) = Web.Studio.StudioDiagText.Rule(Message);
        return new()
        {
            ["severity"] = Severity, ["code"] = "rule", ["path"] = Path, ["message"] = Message, ["pack"] = Pack, ["rule"] = Rule,
            ["text"] = new JsonObject { ["ru"] = ru, ["en"] = en },
        };
    }

    public override string ToString() => $"{(Pack.Length > 0 ? $"{Pack}/{Rule}" : Rule)}{(Path.Length > 0 ? " " + Path : "")}: {Message}";
}

/// <summary>Whether a name exists in the game data; null when it cannot be told (the data are not loaded).</summary>
public interface IRuleRefs
{
    bool? Item(string name);
    bool? Spell(string name);
    bool? Mob(string name);
}

/// <summary>One condition or action of a rule (parameters as given, read through the typed helpers).</summary>
public sealed class RuleStep
{
    public required string Type { get; init; }
    public required int Index { get; init; }
    public JsonObject Params { get; init; } = [];

    /// <summary>Actions: whom (a <see cref="RuleSchema.AllTargets"/> value; null = the rule's default). Conditions: the unit looked at (null = self).</summary>
    public string? Target { get; init; }

    /// <summary>Conditions: the result inverted.</summary>
    public bool Not { get; init; }

    /// <summary>Target "radius": the radius in metres.</summary>
    public float Radius { get; init; } = 20;

    public string Str(string name, string fallback = "") => Params[name] is JsonValue v && v.TryGetValue<string>(out var s) ? s.Trim()
        : Params[name] is JsonValue n && n.TryGetValue<double>(out var d) ? d.ToString(CultureInfo.InvariantCulture) : fallback;

    public double Num(string name, double fallback = 0) => RuleParser.Number(Params[name]) ?? fallback;

    public bool Bool(string name, bool fallback = false) => Params[name] is JsonValue v ? v.TryGetValue<bool>(out var b) ? b : v.TryGetValue<string>(out var s) ? s == "true" : fallback : fallback;

    public IReadOnlyList<string> List(string name) => Params[name] switch
    {
        JsonArray a => [.. a.Select(x => x?.ToString().Trim() ?? "").Where(x => x.Length > 0)],
        JsonValue v when v.TryGetValue<string>(out var s) => [.. s.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)],
        _ => [],
    };
}

/// <summary>A loaded rule (server/plugins/RULES.md): trigger → conditions → actions, with its cooldown, limits and runtime counters.</summary>
public sealed class Rule
{
    public required string Pack { get; init; }
    public required string Id { get; init; }
    public string Name { get; init; } = "";
    public bool Enabled { get; init; } = true;
    public required string Trigger { get; init; }

    /// <summary>The trigger's id filter (a uniquename, a cluster, a chain name; '*' matches any text); null = every one.</summary>
    public string? Filter { get; init; }

    public JsonObject TriggerParams { get; init; } = [];

    /// <summary>"any": one condition is enough; else all must hold.</summary>
    public bool Any { get; init; }

    public List<RuleStep> Conditions { get; init; } = [];
    public List<RuleStep> Actions { get; init; } = [];

    /// <summary>The actions' target when an action names none.</summary>
    public string Target { get; init; } = "self";

    public double CooldownSeconds { get; init; }
    public bool CooldownGlobal { get; init; }
    public int MaxFires { get; init; }
    public bool LimitGlobal { get; init; }
    public int PerSecond { get; init; } = 20;

    /// <summary>Said to the player of a command rule whose conditions fail (empty: nothing).</summary>
    public string FailMessage { get; init; } = "";

    public string Key => Pack + "/" + Id;

    // runtime (kept by the engine instance)
    public int Errors { get; set; }
    public bool Disabled { get; set; }
    public string? DisabledWhy { get; set; }
    public long Fires { get; set; }
    public long SecondStamp { get; set; }
    public int SecondCount { get; set; }

    public double TriggerNum(string name, double fallback) => RuleParser.Number(TriggerParams[name]) ?? fallback;
    public string TriggerStr(string name, string fallback = "") => TriggerParams[name] is JsonValue v && v.TryGetValue<string>(out var s) ? s.Trim() : fallback;
    public bool TriggerBool(string name) => TriggerParams[name] is JsonValue v && (v.TryGetValue<bool>(out var b) ? b : v.TryGetValue<string>(out var s) && s == "true");

    /// <summary>Whether the trigger's id filter takes <paramref name="id"/> ('*' = any text, case-insensitive; a comma list = any of them).</summary>
    public bool Matches(string? id)
    {
        if (string.IsNullOrEmpty(Filter)) return true;
        if (string.IsNullOrEmpty(id)) return false;
        foreach (var part in Filter.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            if (RuleParser.Glob(part, id)) return true;
        return false;
    }

    /// <summary>An exact single id (no '*' and no list): the hook can be registered filtered by it.</summary>
    public string? ExactFilter => string.IsNullOrEmpty(Filter) || Filter.Contains('*') || Filter.Contains(',') ? null : Filter;
}

/// <summary>Reads and checks a rule's JSON: a <see cref="Rule"/> when it has no blocker, and every problem with a JSON pointer.</summary>
public static class RuleParser
{
    public static readonly Regex IdPattern = new("^[a-z0-9][a-z0-9_-]{0,63}$", RegexOptions.CultureInvariant);
    private static readonly Regex TimePattern = new("^([01]?[0-9]|2[0-3]):[0-5][0-9]$", RegexOptions.CultureInvariant);
    private static readonly Regex CommandPattern = new("^[a-z0-9_-]{1,24}( [a-z0-9_-]{1,24})?$", RegexOptions.CultureInvariant);
    private static readonly string[] UnitSelectors = ["self", "killer", "victim", "caster", "target", "mob"];

    public static readonly JsonSerializerOptions Read = new() { ReadCommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true };

    public static double? Number(JsonNode? node) => node switch
    {
        JsonValue v when v.TryGetValue<double>(out var d) && double.IsFinite(d) => d,
        JsonValue v when v.TryGetValue<string>(out var s) && double.TryParse(s.Trim().Replace(',', '.'), NumberStyles.Float, CultureInfo.InvariantCulture, out var d) && double.IsFinite(d) => d,
        _ => null,
    };

    /// <summary>'*' matches any text (case-insensitive).</summary>
    public static bool Glob(string pattern, string text)
    {
        if (!pattern.Contains('*')) return pattern.Equals(text, StringComparison.OrdinalIgnoreCase);
        var rx = "^" + string.Join(".*", pattern.Split('*').Select(Regex.Escape)) + "$";
        return Regex.IsMatch(text, rx, RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
    }

    /// <summary>"HH:MM" → minutes after midnight (null when not a time).</summary>
    public static int? Minutes(string text) => TimePattern.IsMatch(text.Trim()) ? int.Parse(text.Trim().Split(':')[0], CultureInfo.InvariantCulture) * 60 + int.Parse(text.Trim().Split(':')[1], CultureInfo.InvariantCulture) : null;

    /// <summary>
    /// Parses <paramref name="json"/> (a rule of pack <paramref name="pack"/>; <paramref name="fileId"/> the file's name, which must equal the id when given).
    /// </summary>
    public static (Rule? Rule, List<RuleProblem> Problems) Parse(JsonObject json, string pack, string? fileId, IRuleRefs? refs)
    {
        var problems = new List<RuleProblem>();
        var id = (json["id"] as JsonValue)?.TryGetValue<string>(out var s) == true ? s.Trim() : fileId ?? "";
        void Add(string severity, string path, string message) => problems.Add(new RuleProblem(severity, path, message, pack, id));
        void Block(string path, string message) => Add("blocker", path, message);

        if (!IdPattern.IsMatch(id)) Block("/id", $"the id '{id}' must be 1-64 of a-z, 0-9, _ and - (it is the file name)");
        else if (fileId is not null && !fileId.Equals(id, StringComparison.Ordinal)) Block("/id", $"the id '{id}' differs from the file name {fileId}.json");
        if (json["schema"] is { } schema && Number(schema) is not 1) Add("warning", "/schema", "schema should be 1");

        var trigger = json["trigger"] as JsonObject;
        var type = (trigger?["type"] as JsonValue)?.TryGetValue<string>(out var tt) == true ? tt.Trim() : "";
        var def = RuleSchema.Trigger(type);
        if (trigger is null) Block("/trigger", "no trigger: say when the rule fires");
        else if (def is null) Block("/trigger/type", type.Length == 0 ? "choose a trigger" : $"unknown trigger '{type}'");
        var filter = (trigger?["id"] as JsonValue)?.TryGetValue<string>(out var fid) == true ? fid.Trim() : "";
        var triggerParams = trigger?["params"] as JsonObject ?? [];
        if (def is not null)
        {
            if (filter.Length > 0 && def.IdKind is null) Add("warning", "/trigger/id", $"the trigger '{type}' takes no id filter: it is ignored");
            else if (filter.Length > 0) CheckRef(def.IdKind!, filter, "/trigger/id", refs, Add, allowPattern: true);
            if (def.IdKind == "chain" && filter.Length == 0) Block("/trigger/id", "name the chain (the run.chain action calls it by this name)");
            CheckParams(def.Params, triggerParams, "/trigger/params", refs, Add);
            if (type == "command" && triggerParams["command"] is JsonValue cv && cv.TryGetValue<string>(out var cmd) && !CommandPattern.IsMatch(cmd.Trim().TrimStart('.').ToLowerInvariant()))
                Block("/trigger/params/command", "a command is one or two words of a-z, 0-9, _ and - (without the dot)");
        }

        var available = def?.Targets ?? RuleSchema.AllTargets;
        var features = def?.Features ?? RuleSchema.Feature.None;
        var defaultTarget = (json["target"] as JsonValue)?.TryGetValue<string>(out var dt) == true && dt.Length > 0 ? dt : available.Contains("self") ? "self" : available.FirstOrDefault() ?? "self";
        if (!RuleSchema.AllTargets.Contains(defaultTarget)) Block("/target", $"unknown target '{defaultTarget}'");
        else if (!available.Contains(defaultTarget)) Block("/target", $"the trigger '{type}' has no '{defaultTarget}'");

        List<RuleStep> Steps(string key, bool actions)
        {
            var list = new List<RuleStep>();
            if (json[key] is null) return list;
            if (json[key] is not JsonArray array)
            {
                Block("/" + key, $"{key} must be a list");
                return list;
            }
            for (var i = 0; i < array.Count; i++)
            {
                var path = $"/{key}/{i}";
                if (array[i] is not JsonObject o)
                {
                    Block(path, "not an object");
                    continue;
                }
                var stype = (o["type"] as JsonValue)?.TryGetValue<string>(out var st) == true ? st.Trim() : "";
                var sdef = actions ? RuleSchema.Action(stype) : RuleSchema.Condition(stype);
                if (sdef is null)
                {
                    Block(path + "/type", stype.Length == 0 ? "choose a type" : $"unknown {(actions ? "action" : "condition")} '{stype}'");
                    continue;
                }
                var p = o["params"] as JsonObject ?? [];
                CheckParams(sdef.Params, p, path + "/params", refs, Add);
                if (sdef.Needs != RuleSchema.Feature.None && def is not null && !features.HasFlag(sdef.Needs) && def.Type != "chain")
                    Block(path + "/type", $"'{stype}' needs a trigger that offers {sdef.Needs.ToString().ToLowerInvariant()}; '{type}' does not");
                var target = (o["target"] as JsonValue)?.TryGetValue<string>(out var tg) == true && tg.Length > 0 ? tg.Trim() : null;
                if (target is not null)
                {
                    if (!RuleSchema.AllTargets.Contains(target)) Block(path + "/target", $"unknown target '{target}'");
                    else if (!actions && !UnitSelectors.Contains(target)) Block(path + "/target", $"a condition looks at one unit: {string.Join(", ", UnitSelectors)}");
                    else if (!available.Contains(target) && def?.Type != "chain") Block(path + "/target", $"the trigger '{type}' has no '{target}'");
                }
                var radius = Number(o["radius"]) ?? 20;
                if ((target ?? defaultTarget) == "radius" && actions && (radius < 1 || radius > 200)) Block(path + "/radius", "the radius is 1-200 m");
                list.Add(new RuleStep
                {
                    Type = stype, Index = i, Params = (JsonObject)p.DeepClone(), Target = target, Not = o["not"] is JsonValue nv && nv.TryGetValue<bool>(out var not) && not,
                    Radius = (float)radius,
                });
            }
            return list;
        }

        var conditions = Steps("conditions", false);
        var actionList = Steps("actions", true);
        if (actionList.Count == 0 && !problems.Any(p => p.Path.StartsWith("/actions/", StringComparison.Ordinal))) Add("warning", "/actions", "no actions: the rule does nothing");

        var match = (json["match"] as JsonValue)?.TryGetValue<string>(out var m) == true ? m : "all";
        if (match is not ("all" or "any")) Block("/match", "match is all or any");

        double cooldown = 0, perSecond = 20, max = 0;
        bool cdGlobal = false, limitGlobal = false;
        if (json["cooldown"] is JsonObject cd)
        {
            cooldown = Number(cd["seconds"]) ?? 0;
            if (cooldown is < 0 or > 30 * 86400) Block("/cooldown/seconds", "the cooldown is 0-2592000 s");
            cdGlobal = (cd["per"] as JsonValue)?.ToString() == "global";
        }
        if (json["limits"] is JsonObject lim)
        {
            max = Number(lim["maxFires"]) ?? 0;
            if (max < 0 || max != Math.Floor(max)) Block("/limits/maxFires", "max fires is a whole number ≥ 0 (0 = no limit)");
            limitGlobal = (lim["per"] as JsonValue)?.ToString() == "global";
            perSecond = Number(lim["perSecond"]) ?? 20;
            if (perSecond is < 1 or > 1000) Block("/limits/perSecond", "fires per second is 1-1000");
        }

        if (problems.Any(p => p.Blocker) || def is null) return (null, problems);
        var name = (json["name"] as JsonValue)?.TryGetValue<string>(out var n) == true ? n.Trim() : "";
        return (new Rule
        {
            Pack = pack, Id = id, Name = name.Length > 0 ? name : id, Enabled = json["enabled"] is not JsonValue ev || !ev.TryGetValue<bool>(out var en) || en,
            Trigger = type, Filter = filter.Length > 0 && def.IdKind is not null ? filter : null, TriggerParams = (JsonObject)triggerParams.DeepClone(),
            Any = match == "any", Conditions = conditions, Actions = actionList, Target = defaultTarget,
            CooldownSeconds = cooldown, CooldownGlobal = cdGlobal, MaxFires = (int)max, LimitGlobal = limitGlobal, PerSecond = (int)perSecond,
            FailMessage = (json["failMessage"] as JsonValue)?.TryGetValue<string>(out var fm) == true ? fm : "",
        }, problems);
    }

    private static void CheckParams(RuleSchema.Param[] defs, JsonObject given, string path, IRuleRefs? refs, Action<string, string, string> add)
    {
        foreach (var (key, _) in given)
            if (!defs.Any(d => d.Name == key)) add("warning", $"{path}/{key}", $"unknown parameter '{key}' (ignored)");
        foreach (var d in defs)
        {
            var p = $"{path}/{d.Name}";
            var node = given[d.Name];
            var empty = node is null || (node is JsonValue v0 && v0.TryGetValue<string>(out var s0) && s0.Trim().Length == 0) || (node is JsonArray a0 && a0.Count == 0);
            if (empty)
            {
                if (d.Required && d.Default is null) add("blocker", p, $"'{d.Name}' is required");
                else if (d.Required && node is not null) add("blocker", p, $"'{d.Name}' is required");
                continue;
            }
            switch (d.Type)
            {
                case "int" or "number" or "seconds":
                {
                    var n = Number(node);
                    if (n is null) add("blocker", p, $"'{d.Name}' must be a number");
                    else if (d.Type == "int" && n != Math.Floor(n.Value)) add("blocker", p, $"'{d.Name}' must be a whole number");
                    else if ((d.Min is { } min && n < min) || (d.Max is { } max && n > max))
                        add("blocker", p, $"'{d.Name}' is {(d.Min is { } a ? a.ToString(CultureInfo.InvariantCulture) : "…")}-{(d.Max is { } b ? b.ToString(CultureInfo.InvariantCulture) : "…")}");
                    break;
                }
                case "bool":
                    if (node is not JsonValue bv || !(bv.TryGetValue<bool>(out _) || (bv.TryGetValue<string>(out var bs) && bs is "true" or "false"))) add("blocker", p, $"'{d.Name}' is true or false");
                    break;
                case "select":
                    if (node!.ToString() is var sv && !d.Options!.Contains(sv)) add("blocker", p, $"'{d.Name}' is one of {string.Join(", ", d.Options!)}");
                    break;
                case "flags":
                    if (node is not JsonArray fa || fa.Any(x => !d.Options!.Contains(x?.ToString() ?? ""))) add("blocker", p, $"'{d.Name}' is a list of {string.Join(", ", d.Options!)}");
                    break;
                case "time":
                    if (Minutes(node!.ToString()) is null) add("blocker", p, $"'{d.Name}' is a time HH:MM");
                    break;
                case "item" or "spell" or "buff" or "mob" or "mount" or "cluster":
                    CheckRef(d.Type, node!.ToString().Trim(), p, refs, add, allowPattern: d.Type is "item" or "mob");
                    break;
            }
        }
    }

    private static void CheckRef(string kind, string name, string path, IRuleRefs? refs, Action<string, string, string> add, bool allowPattern)
    {
        if (refs is null || kind is "text" or "chain" or "cluster") return;
        foreach (var one in name.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            if (one.Contains('*'))
            {
                if (!allowPattern && kind is not ("item" or "mob" or "spell" or "buff" or "mount")) add("blocker", path, $"'{one}': no pattern here");
                continue;
            }
            var bare = kind is "item" or "mount" && one.Contains('@') ? one[..one.IndexOf('@')] : one;
            var known = kind switch
            {
                "item" or "mount" => refs.Item(bare),
                "spell" or "buff" => refs.Spell(bare),
                "mob" => refs.Mob(bare),
                _ => null,
            };
            if (known == false) add("blocker", path, $"no {kind} '{one}' in the game data");
        }
    }
}
