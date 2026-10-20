using System.Globalization;
using System.Reflection;
using AlbionLocal.Photon;
using AlbionLocal.Server.Data;
using AlbionLocal.Server.Entities;
using AlbionLocal.Server.Persistence;

namespace AlbionLocal.Server.Commands;

/// <summary>
/// The GM commands (TC ChatHandler + CommandScript, ARCHITECTURE.md §6.6): the static [Command] methods, found once by
/// reflection. Building checks them and throws <see cref="InvalidOperationException"/> on a method that cannot be bound
/// (not static, no <see cref="CommandContext"/> first, a parameter type the parser does not know, Tail not last) or a
/// path used twice; <see cref="ServerHost"/> builds the default registry before it starts, so such a mistake stops the
/// server there (principle 6).
/// A line is "path args…" (a leading '.' is optional here): the longest command path that starts the line wins, then
/// each argument is parsed by its parameter type: string (one word, or "several words" in quotes), int, long, float,
/// bool (true/false, yes/no, on/off, 1/0), any enum (by name), <see cref="ItemTemplate"/> (by uniquename; a miss names the
/// closest one), <see cref="Tail"/> (the rest of the line), and the nullable forms of the value types (int?, float?, …:
/// with a default of null, "not given"). A parameter with a default value may be left out.
/// </summary>
public sealed class CommandRegistry
{
    /// <summary>A bound command; <see cref="Usage"/> is its .help line ".item add &lt;item&gt; [count=1] [quality=1]".</summary>
    public sealed record Command(string Path, string[] Words, AccountLevel Level, string Help, MethodInfo Method, ParameterInfo[] Arguments)
    {
        /// <summary>Runs only from the server console (<see cref="CommandAttribute.ConsoleOnly"/>).</summary>
        public bool ConsoleOnly { get; init; }

        /// <summary>Whether an argument is a <see cref="SecretAttribute"/> one.</summary>
        public bool HasSecrets => Arguments.Any(IsSecret);

        public string Usage => "." + string.Join(" ", [Path, .. Arguments.Select(Describe)]);

        private static string Describe(ParameterInfo p)
        {
            var name = p.ParameterType == typeof(Tail) ? $"{p.Name}..." : p.Name!;
            if (!p.HasDefaultValue) return $"<{name}>";
            return p.DefaultValue is null or DBNull || p.ParameterType == typeof(Tail)
                ? $"[{name}]"
                : $"[{name}={(p.DefaultValue is bool b ? b ? "true" : "false" : Convert.ToString(p.DefaultValue, CultureInfo.InvariantCulture))}]";
        }
    }

    private delegate object Parser(CommandContext ctx, string text, string name);

    private static readonly Dictionary<Type, Parser> Parsers = new()
    {
        [typeof(string)] = (_, text, _) => text,
        [typeof(int)] = (_, text, name) => int.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out var v) ? v : throw Bad(name, text, "is not a whole number"),
        [typeof(long)] = (_, text, name) => long.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out var v) ? v : throw Bad(name, text, "is not a whole number"),
        [typeof(float)] = (_, text, name) => float.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out var v) && float.IsFinite(v) ? v : throw Bad(name, text, "is not a number"),
        [typeof(bool)] = (_, text, name) => text.ToLowerInvariant() switch
        {
            "true" or "yes" or "on" or "1" => true,
            "false" or "no" or "off" or "0" => false,
            _ => throw Bad(name, text, "is not true or false"),
        },
        [typeof(ItemTemplate)] = (ctx, text, _) => ctx.World.Items.Find(text) ?? throw new CommandError(
            $"unknown item {text}{(ctx.World.Items.Closest(text) is { } closest ? $" (closest: {closest})" : "")}"),
        // mobs.xml comes with the first cluster resolved (Data/ClusterStore); the lock never waits for the file.
        [typeof(MobTemplate)] = (ctx, text, _) => ctx.World.Clusters?.MobsIfLoaded is not { } mobs
            ? throw new CommandError("mobs.xml is not loaded yet (it comes with the first cluster's data)")
            : mobs.Find(text) ?? throw new CommandError($"unknown mob {text}{(mobs.Like(text).ToList() is { Count: > 0 } like ? $" (like: {string.Join(", ", like)})" : "")}"),
    };

    private static readonly Lazy<CommandRegistry> Built = new(() => new CommandRegistry(typeof(CommandRegistry).Assembly.GetTypes()));

    private readonly List<Command> _commands = [];

    /// <summary>The commands of the server assembly (Commands/Builtin; later also Scripts/), built and checked on first use.</summary>
    public static CommandRegistry Default => Built.Value;

    /// <summary>Binds the [Command] methods of <paramref name="types"/>; throws on a method that cannot be bound or a path used twice.</summary>
    public CommandRegistry(IEnumerable<Type> types)
    {
        foreach (var type in types)
        foreach (var m in type.GetMethods(BindingFlags.DeclaredOnly | BindingFlags.Static | BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic))
        {
            if (m.GetCustomAttribute<CommandAttribute>() is not { } a) continue;
            var method = $"{type.Name}.{m.Name}";
            var words = a.Path.Split(' ', StringSplitOptions.RemoveEmptyEntries).Select(w => w.ToLowerInvariant()).ToArray();
            if (words.Length == 0) throw new InvalidOperationException($"{method}: [Command] with an empty path");
            if (!m.IsStatic) throw new InvalidOperationException($"{method}: [Command(\"{a.Path}\")] must be on a static method");
            var parameters = m.GetParameters();
            if (parameters is not [{ ParameterType: var first }, ..] || first != typeof(CommandContext))
                throw new InvalidOperationException($"{method}: [Command(\"{a.Path}\")] must take a CommandContext first");
            var arguments = parameters[1..];
            foreach (var p in arguments)
            {
                var parsed = Nullable.GetUnderlyingType(p.ParameterType) ?? p.ParameterType;
                if (!Parsers.ContainsKey(parsed) && !parsed.IsEnum && p.ParameterType != typeof(Tail))
                    throw new InvalidOperationException($"{method}: parameter {p.Name} of type {p.ParameterType.Name} cannot be parsed " +
                        "(string, int, long, float, bool, an enum, ItemTemplate, Tail, or a nullable one of them)");
                if (p.ParameterType == typeof(Tail) && p != arguments[^1])
                    throw new InvalidOperationException($"{method}: Tail parameter {p.Name} must be the last one");
            }
            var path = string.Join(" ", words);
            if (_commands.Find(c => c.Path == path) is { } other)
                throw new InvalidOperationException($"command .{path} is bound twice: {other.Method.DeclaringType?.Name}.{other.Method.Name} and {method}");
            _commands.Add(new Command(path, words, a.Level, a.Help, m, arguments) { ConsoleOnly = a.ConsoleOnly });
        }
        _commands.Sort((x, y) => string.CompareOrdinal(x.Path, y.Path));
    }

    /// <summary>All commands, by path.</summary>
    public IReadOnlyList<Command> Commands => _commands;

    /// <summary>
    /// The command a line asks for and its parsed arguments (defaults filled in), checked against the caller's level;
    /// a <see cref="CommandError"/> says what is wrong (unknown command, level, missing or bad argument).
    /// </summary>
    public (Command Command, object?[] Args) Parse(CommandContext ctx, string line)
    {
        line = StripDot(line);
        var words = Split(line);
        if (words.Count == 0) throw new CommandError("type .help for the commands");
        var command = Find(words);
        if (command is null)
        {
            // ".item" alone: the commands of that group.
            var group = _commands.Where(c => c.Words[0].Equals(words[0].Text, StringComparison.OrdinalIgnoreCase) && c.Level <= ctx.Level
                && (ctx.Console || !c.ConsoleOnly)).ToList();
            throw new CommandError(group.Count > 0
                ? $"usage: {string.Join(" | ", group.Select(c => c.Usage))}"
                : $"unknown command .{words[0].Text}; .help lists the commands");
        }
        if (command.Level > ctx.Level) throw new CommandError($".{command.Path} needs level {command.Level} (yours: {ctx.Level})");
        if (command.ConsoleOnly && !ctx.Console) throw new CommandError($".{command.Path} runs only on the server console");

        var args = new object?[command.Arguments.Length + 1];
        args[0] = ctx;
        var next = command.Words.Length;
        for (var i = 0; i < command.Arguments.Length; i++)
        {
            var p = command.Arguments[i];
            if (p.ParameterType == typeof(Tail))
            {
                args[i + 1] = new Tail(next < words.Count ? line[words[next].Start..].Trim() : "");
                next = words.Count;
            }
            else if (next < words.Count)
            {
                try { args[i + 1] = ParseArgument(ctx, p, words[next++].Text); }
                catch (CommandError) when (command.HasSecrets) { throw new CommandError($"bad <{p.Name}>; usage: {command.Usage}"); }
            }
            else if (p.HasDefaultValue)
                args[i + 1] = p.DefaultValue is null or DBNull && p.ParameterType.IsValueType ? Activator.CreateInstance(p.ParameterType) : p.DefaultValue;
            else
                throw new CommandError($"missing <{p.Name}>; usage: {command.Usage}");
        }
        if (next < words.Count)
            throw new CommandError(command.HasSecrets ? $"too many arguments (a word with spaces goes in \"quotes\"); usage: {command.Usage}"
                : $"too many arguments ('{words[next].Text}'); usage: {command.Usage}");
        return (command, args);
    }

    /// <summary>
    /// Runs one line for <paramref name="ctx"/>: parses it, runs the command and turns a <see cref="CommandError"/> into an
    /// error reply. Any other exception is logged and answered as a failure. Never throws. Under <see cref="World.Sync"/>.
    /// </summary>
    public void Execute(CommandContext ctx, string line)
    {
        // A plugin's command (server/plugins/README.md) when its path is longer than any built-in one the line starts with.
        if (ctx.World.Plugins.TryRun(ctx, line)) return;
        try
        {
            var (command, args) = Parse(ctx, line);
            command.Method.Invoke(null, BindingFlags.DoNotWrapExceptions, null, args, CultureInfo.InvariantCulture);
        }
        catch (CommandError e)
        {
            ctx.Error(e.Message);
        }
        catch (Exception e)
        {
            Log.Error("cmd", $"'{Redact(line)}' failed: {e}");
            ctx.Error($"failed: {e.Message}");
        }
    }

    /// <summary>
    /// A line from the server console (<see cref="ConsoleInput"/>, under <see cref="World.Sync"/>), run as Admin. It acts on
    /// the only joined player; with several, the line starts with the player's name ("Hero .gps"), which also works with one.
    /// </summary>
    public void ExecuteConsole(World world, string line, Action<string, bool> reply)
    {
        var players = Players(world);
        var words = Split(StripDot(line));
        Player? target = null;
        var noPlayer = "no player in game";
        if (words.Count > 1 && players.Find(p => p.Character.Name.Equals(words[0].Text, StringComparison.OrdinalIgnoreCase)) is { } named)
        {
            target = named;
            line = StripDot(line)[words[1].Start..];
        }
        else if (players.Count == 1)
            target = players[0];
        else if (players.Count > 1)
            noPlayer = $"{players.Count} players in game ({string.Join(", ", players.Select(p => p.Character.Name))}): "
                + $"start the line with the name, e.g. '{players[0].Character.Name} .gps'";
        Execute(new CommandContext(world, target, AccountLevel.Admin, reply) { NoPlayer = noPlayer, Console = true }, line);
    }

    /// <summary>
    /// The line as it may be logged: the <see cref="SecretAttribute"/> arguments of its command (a password) replaced by
    /// "&lt;redacted&gt;", every word after them too (a mistyped line may have shifted them). Other lines stay as they are.
    /// </summary>
    public string Redact(string line)
    {
        var stripped = StripDot(line);
        var words = Split(stripped);
        if (Find(words) is not { HasSecrets: true } command) return line;
        var first = command.Arguments.Select((p, i) => (p, i)).First(x => IsSecret(x.p)).i + command.Words.Length;
        if (first >= words.Count) return line;
        return (line.TrimStart().StartsWith('.') ? "." : "") + stripped[..words[first].Start] + "<redacted>";
    }

    /// <summary>The command with the longest path that starts <paramref name="words"/>; null = none.</summary>
    private Command? Find(List<(string Text, int Start)> words) => _commands
        .Where(c => c.Words.Length <= words.Count && c.Words.Select((w, i) => w.Equals(words[i].Text, StringComparison.OrdinalIgnoreCase)).All(x => x))
        .MaxBy(c => c.Words.Length);

    private static bool IsSecret(ParameterInfo p) => p.GetCustomAttribute<SecretAttribute>() is not null;

    /// <summary>
    /// The joined players of every map, one per character: a character joined twice (<see cref="World.LoadCharacter"/>,
    /// a reconnect while the old peer waits out its timeout) counts by its newest Join (highest object id), the live
    /// session, so the console does not act on the dead one.
    /// </summary>
    public static List<Player> Players(World world) =>
        [.. world.Maps.Maps.SelectMany(m => m.Players.Values).GroupBy(p => p.Character.Id).Select(g => g.MaxBy(p => p.Id)!)];

    private static object ParseArgument(CommandContext ctx, ParameterInfo p, string text)
    {
        var type = Nullable.GetUnderlyingType(p.ParameterType) ?? p.ParameterType; // a given int? is an int
        if (Parsers.TryGetValue(type, out var parse)) return parse(ctx, text, p.Name!);
        // Enums by name only: a number would pass Enum.TryParse even when no member has it.
        if (type.IsEnum && text is [not ('-' or (>= '0' and <= '9')), ..]
            && Enum.TryParse(type, text, ignoreCase: true, out var value))
            return value;
        throw Bad(p.Name!, text, $"is not one of {string.Join(", ", Enum.GetNames(type))}");
    }

    private static CommandError Bad(string name, string text, string problem) => new($"{name}: '{text}' {problem}");

    private static string StripDot(string line)
    {
        line = line.Trim();
        return line.StartsWith('.') ? line[1..] : line;
    }

    /// <summary>The words of a line with where each starts (for <see cref="Tail"/>); "a b" in double quotes is one word.</summary>
    private static List<(string Text, int Start)> Split(string line)
    {
        var words = new List<(string, int)>();
        for (var i = 0; i < line.Length;)
        {
            if (char.IsWhiteSpace(line[i]))
            {
                i++;
                continue;
            }
            var start = i;
            if (line[i] == '"')
            {
                var end = line.IndexOf('"', i + 1);
                if (end < 0) end = line.Length;
                words.Add((line[(i + 1)..end], start));
                i = end + 1;
                continue;
            }
            while (i < line.Length && !char.IsWhiteSpace(line[i])) i++;
            words.Add((line[start..i], start));
        }
        return words;
    }
}
