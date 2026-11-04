using System.Text.Json;

namespace AlbionLocal.Modules;

/// <summary>
/// What the server gives a module at <see cref="ModuleScript.OnLoad"/>. Every member is to be used on the world thread only (inside a hook
/// handler, a timer, a command or OnLoad/OnUnload): the world is single-threaded and takes no locks for modules. Work on another thread
/// (a file read, a web request of the module's own) must come back with <see cref="Post"/>.
/// </summary>
public interface IModuleContext
{
    /// <summary>The module's id (module.jsonc "id").</summary>
    string Name { get; }

    /// <summary>The module's own folder (module.jsonc, sources, conf/; read-only by convention).</summary>
    string ModuleDirectory { get; }

    /// <summary>The module's data folder (data/modules/&lt;name&gt;/, created on first use): storage.json, and any file of its own.</summary>
    string DataDirectory { get; }

    /// <summary>The game: players, mobs, messages, items, silver, fame, teleports, buffs, spawns, world events.</summary>
    IServer Server { get; }

    /// <summary>The log, every line prefixed "module:&lt;name&gt;".</summary>
    IModuleLog Log { get; }

    /// <summary>The module's settings: conf/&lt;id&gt;.jsonc.dist with config/modules/&lt;id&gt;.jsonc over it.</summary>
    IModuleConfig Config { get; }

    /// <summary>A small persisted key-value store (data/modules/&lt;name&gt;/storage.json).</summary>
    IModuleStorage Storage { get; }

    /// <summary>Timers on the world clock: every N seconds, once after N seconds.</summary>
    IModuleTimers Timers { get; }

    /// <summary>The module's chat / console commands (".motd").</summary>
    IModuleCommands Commands { get; }

    /// <summary>The hooks as lambdas, each for everything or for one id. The same hooks are the methods of the *Script classes.</summary>
    IHooks Hooks { get; }

    /// <summary>The gameplay actions (damage, heal, buffs, movement, areas, summons, modifiers, cooldowns).</summary>
    IGameplay Game { get; }

    /// <summary>Adds a script made by the module itself (one with constructor arguments): its hooks are connected at once, its OnLoad runs. Goes with the module.</summary>
    void AddScript(ModuleScript script);

    /// <summary>Runs <paramref name="action"/> on the world thread at the next tick: the one call that is safe from any thread.</summary>
    void Post(Action action);
}

/// <summary>Log lines with the module's prefix (server log and data/server.log).</summary>
public interface IModuleLog
{
    /// <summary>A line at debug level, for what is only useful when looking into a problem.</summary>
    void Debug(string message);
    /// <summary>A line at information level.</summary>
    void Info(string message);
    /// <summary>A warning: something is wrong but the module goes on.</summary>
    void Warn(string message);
    /// <summary>An error line. It does not count towards the module's error limit: only exceptions do.</summary>
    void Error(string message);
}

/// <summary>
/// The module's settings: conf/&lt;id&gt;.jsonc.dist in its folder (the defaults it ships with, copied once to config/modules/&lt;id&gt;.jsonc for the
/// server owner to edit; a module update does not overwrite that copy), overlaid key by key by that copy. Keys are the top-level properties; nested objects are read
/// with <see cref="Get{T}"/> into a class or with <see cref="Root"/>.
/// </summary>
public interface IModuleConfig
{
    /// <summary>The merged JSON object (an empty object when there is no config).</summary>
    JsonElement Root { get; }

    /// <summary>Key <paramref name="key"/> as <typeparamref name="T"/> (System.Text.Json, case-insensitive); <paramref name="fallback"/> when missing or not convertible.</summary>
    T Get<T>(string key, T fallback);

    /// <summary>The whole config as <typeparamref name="T"/> (properties by name, case-insensitive; missing ones keep their defaults).</summary>
    /// <example>
    /// A settings class with its defaults:
    /// <code><![CDATA[
    /// public sealed class BountySettings
    /// {
    ///     public int Reward { get; set; } = 100;
    ///     public bool Announce { get; set; } = true;
    /// }
    ///
    /// protected override void OnLoad()
    /// {
    ///     var settings = Config.Bind<BountySettings>();
    ///     Log.Info($"bounty reward: {settings.Reward}");
    /// }
    /// ]]></code>
    /// </example>
    T Bind<T>() where T : new();

    /// <summary>Reads the files again (also done by <c>.modules reload</c>).</summary>
    void Reload();
}

/// <summary>
/// A per-module key-value store persisted as JSON (data/modules/&lt;name&gt;/storage.json): written a few seconds after a change, at
/// unload and at the server's stop. Meant for small state (a leaderboard, a counter, settings changed in game), not bulk data.
/// </summary>
public interface IModuleStorage
{
    /// <summary>The keys stored.</summary>
    IReadOnlyCollection<string> Keys { get; }

    /// <summary>Whether <paramref name="key"/> is stored.</summary>
    bool Contains(string key);

    /// <summary>The value of <paramref name="key"/> as <typeparamref name="T"/>; <paramref name="fallback"/> when missing or not convertible.</summary>
    T Get<T>(string key, T fallback);

    /// <summary>Stores <paramref name="value"/> (any JSON-serialisable value) under <paramref name="key"/>.</summary>
    void Set<T>(string key, T value);

    /// <summary>Removes <paramref name="key"/>; false when it was not there.</summary>
    bool Remove(string key);

    /// <summary>Writes the file now (otherwise done a few seconds after a change).</summary>
    void Save();
}

/// <summary>Timers on the world clock (fired in the world tick, so at most one tick late). Cancel with Dispose; all are cancelled at unload.</summary>
public interface IModuleTimers
{
    /// <summary>Runs <paramref name="action"/> every <paramref name="interval"/> (first after one interval; at least 10 ms, a shorter one throws).</summary>
    IDisposable Every(TimeSpan interval, Action action);

    /// <summary>Runs <paramref name="action"/> once after <paramref name="delay"/>.</summary>
    IDisposable After(TimeSpan delay, Action action);
}

/// <summary>Who may run a command (the server's account levels).</summary>
public enum PlayerLevel
{
    /// <summary>An ordinary account. As a command's level: anyone may run it.</summary>
    Player = 0,
    /// <summary>A game master. As a command's level: game masters and admins may run it.</summary>
    GameMaster = 1,
    /// <summary>An admin. As a command's level: only admins may run it.</summary>
    Admin = 2,
}

/// <summary>The module's commands, typed in the game's Say chat ('.' first) or on the server console. Dropped at unload.</summary>
public interface IModuleCommands
{
    /// <summary>
    /// Registers <paramref name="path"/> ("motd", or two words "bounty top"); a line runs the module command with the longest matching path, unless a built-in
    /// command matches at least as much of the line. A path a built-in command or another module already has is refused (an <see cref="InvalidOperationException"/>). <paramref name="help"/> shows in .help.
    /// </summary>
    /// <example>
    /// A command with an optional argument:
    /// <code><![CDATA[
    /// Commands.Register("bounty top", PlayerLevel.Player, "the highest bounties", call =>
    /// {
    ///     if (call.Words.Count > 1)
    ///     {
    ///         call.Error("usage: .bounty top [count]");
    ///         return;
    ///     }
    ///     call.Reply("No bounties yet.");
    /// });
    /// ]]></code>
    /// </example>
    void Register(string path, PlayerLevel level, string help, Action<ICommandCall> handler);
}

/// <summary>One run of a module command.</summary>
public interface ICommandCall
{
    /// <summary>The player who typed it (in chat), or the console's player (the only one in game, or the one named first); null when none.</summary>
    IPlayer? Player { get; }

    /// <summary>The caller's level.</summary>
    PlayerLevel Level { get; }

    /// <summary>Whether it came from the server console.</summary>
    bool FromConsole { get; }

    /// <summary>The text after the command's path (trimmed; "" when none).</summary>
    string Args { get; }

    /// <summary><see cref="Args"/> split on spaces ("two words" in double quotes stay one).</summary>
    IReadOnlyList<string> Words { get; }

    /// <summary>Word <paramref name="index"/> of <see cref="Words"/> (from 0); null when the line has fewer.</summary>
    string? Word(int index) => index >= 0 && index < Words.Count ? Words[index] : null;

    /// <summary>Word <paramref name="index"/> as a whole number; <paramref name="fallback"/> when it is missing or no number.</summary>
    int IntArg(int index, int fallback = 0) => int.TryParse(Word(index), System.Globalization.NumberStyles.Integer, System.Globalization.CultureInfo.InvariantCulture, out var n) ? n : fallback;

    /// <summary>Word <paramref name="index"/> as a number; <paramref name="fallback"/> when it is missing or no number.</summary>
    double DoubleArg(int index, double fallback = 0) => double.TryParse(Word(index), System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var n) ? n : fallback;

    /// <summary>The online player that word <paramref name="index"/> names: <c>if (call.PlayerArg(0) is not { } target) return;</c>. Null when there is none, with the caller told so.</summary>
    IPlayer? PlayerArg(int index);

    /// <summary>The kind of item that word <paramref name="index"/> names. Null when there is none, with the caller told so and the closest name offered.</summary>
    ItemType? ItemArg(int index);

    /// <summary>The spell that word <paramref name="index"/> names. Null when there is none, with the caller told so.</summary>
    SpellInfo? SpellArg(int index);

    /// <summary>An answer to the caller (a SystemMessage in chat; the log on the console). Several lines may be joined with '\n'.</summary>
    void Reply(string text);

    /// <summary>An error answer.</summary>
    void Error(string text);
}
