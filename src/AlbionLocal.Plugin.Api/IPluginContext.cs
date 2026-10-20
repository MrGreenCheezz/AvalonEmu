using System.Text.Json;

namespace AlbionLocal.Plugin.Api;

/// <summary>
/// What the server gives a plugin at <see cref="IPlugin.Load"/>. Every member is to be used on the world thread only (inside an event
/// handler, a timer, a command or Load/Unload): the world is single-threaded and takes no locks for plugins. Work on another thread
/// (a file read, a web request of the plugin's own) must come back with <see cref="Post"/>.
/// </summary>
public interface IPluginContext
{
    /// <summary>The plugin's name as the server knows it (plugin.json "name").</summary>
    string Name { get; }

    /// <summary>The plugin's own folder (plugin.json, sources, config.json; read-only by convention).</summary>
    string PluginDirectory { get; }

    /// <summary>The plugin's data folder (data/plugins/&lt;name&gt;/, created on first use): storage.json, and any file of its own.</summary>
    string DataDirectory { get; }

    /// <summary>The game events (subscribe in Load; all handlers run on the world thread).</summary>
    IPluginEvents Events { get; }

    /// <summary>The game: players, mobs, messages, items, silver, fame, teleports, buffs, spawns, world events.</summary>
    IServer Server { get; }

    /// <summary>The log, every line prefixed "plugin:&lt;name&gt;".</summary>
    IPluginLog Log { get; }

    /// <summary>The plugin's config.json (its folder's, with data/plugins/&lt;name&gt;/config.json over it when that exists).</summary>
    IPluginConfig Config { get; }

    /// <summary>A small persisted key-value store (data/plugins/&lt;name&gt;/storage.json).</summary>
    IPluginStorage Storage { get; }

    /// <summary>Timers on the world clock: every N seconds, once after N seconds.</summary>
    IPluginTimers Timers { get; }

    /// <summary>The plugin's chat / console commands (".motd").</summary>
    IPluginCommands Commands { get; }

    /// <summary>API v2: the gameplay hooks (spells, buffs, items, mounts, units, formulas), each for every id or one uniquename.</summary>
    IHooks Hooks { get; }

    /// <summary>API v2: the gameplay actions (damage, heal, buffs, movement, areas, summons, modifiers, cooldowns).</summary>
    IGameplay Game { get; }

    /// <summary>Runs <paramref name="action"/> on the world thread at the next tick: the one call that is safe from any thread.</summary>
    void Post(Action action);
}

/// <summary>Log lines with the plugin's prefix (server log and data/server.log).</summary>
public interface IPluginLog
{
    void Debug(string message);
    void Info(string message);
    void Warn(string message);
    void Error(string message);
}

/// <summary>
/// The plugin's settings: config.json in its folder (the defaults it ships with), overlaid key by key by data/plugins/&lt;name&gt;/config.json
/// (the server owner's changes, which a plugin update does not overwrite). Keys are the top-level properties; nested objects are read
/// with <see cref="Get{T}"/> into a class or with <see cref="Root"/>.
/// </summary>
public interface IPluginConfig
{
    /// <summary>The merged JSON object (an empty object when there is no config).</summary>
    JsonElement Root { get; }

    /// <summary>Key <paramref name="key"/> as <typeparamref name="T"/> (System.Text.Json, case-insensitive); <paramref name="fallback"/> when missing or not convertible.</summary>
    T Get<T>(string key, T fallback);

    /// <summary>The whole config as <typeparamref name="T"/> (properties by name, case-insensitive; missing ones keep their defaults).</summary>
    T Bind<T>() where T : new();

    /// <summary>Reads the files again (also done by <c>.plugins reload</c>).</summary>
    void Reload();
}

/// <summary>
/// A per-plugin key-value store persisted as JSON (data/plugins/&lt;name&gt;/storage.json): written a few seconds after a change, at
/// unload and at the server's stop. Meant for small state (a leaderboard, a counter, settings changed in game), not bulk data.
/// </summary>
public interface IPluginStorage
{
    /// <summary>The keys stored.</summary>
    IReadOnlyCollection<string> Keys { get; }

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
public interface IPluginTimers
{
    /// <summary>Runs <paramref name="action"/> every <paramref name="interval"/> (first after one interval).</summary>
    IDisposable Every(TimeSpan interval, Action action);

    /// <summary>Runs <paramref name="action"/> once after <paramref name="delay"/>.</summary>
    IDisposable After(TimeSpan delay, Action action);
}

/// <summary>Who may run a command (the server's account levels).</summary>
public enum PlayerLevel
{
    Player = 0,
    GameMaster = 1,
    Admin = 2,
}

/// <summary>The plugin's commands, typed in the game's Say chat ('.' first) or on the server console. Dropped at unload.</summary>
public interface IPluginCommands
{
    /// <summary>
    /// Registers <paramref name="path"/> ("motd", or two words "bounty top"); the longest matching path wins. A path a built-in command or
    /// another plugin already has is refused (an <see cref="InvalidOperationException"/>). <paramref name="help"/> shows in .help.
    /// </summary>
    void Register(string path, PlayerLevel level, string help, Action<ICommandCall> handler);
}

/// <summary>One run of a plugin command.</summary>
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

    /// <summary>An answer to the caller (a SystemMessage in chat; the log on the console). Several lines may be joined with '\n'.</summary>
    void Reply(string text);

    /// <summary>An error answer.</summary>
    void Error(string text);
}
