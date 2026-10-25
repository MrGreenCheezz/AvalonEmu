using System.Text.Json;
using AlbionLocal.Photon;
using AlbionLocal.Server;
using AlbionLocal.Server.Commands;
using AlbionLocal.Server.Data;
using AlbionLocal.Server.Protocol;

ProtocolTrace.Enabled = args.Contains("--protocol-trace");

ServerConfig config;
AlbionLocal.Server.Configuration.ConfigurationLoadResult loaded;
try
{
    var selection = AlbionLocal.Server.Configuration.ServerConfiguration.ParseArguments(args);
    loaded = AlbionLocal.Server.Configuration.ServerConfiguration.Load(selection.Directory, selection.Arguments);
    config = loaded.Config;
    loaded.WriteSummary(Console.Out);
    if (args.Contains("--check-config")) return 0;
}
catch (Exception e) when (e is InvalidDataException or IOException or JsonException or ArgumentException or UnauthorizedAccessException)
{
    Log.Error("main", $"config: {e.Message}");
    return 1;
}
Log.Level = args.Contains("-v") ? LogLevel.Debug : args.Contains("-vv") ? LogLevel.Trace : config.LogLevel;
// Offline inspection deliberately runs before logging to a save folder or constructing ServerHost.
if (args.Contains("--check-tutorial"))
{
    try { return TutorialCheck.Run(config.ClientGameDataPath, Console.Out, details: args.Contains("-v")); }
    catch (Exception e) when (e is InvalidDataException or System.Xml.XmlException)
    {
        Log.Error("tutorial", e.Message);
        return 1;
    }
}
// --check-content: the offline golden checks of the cluster data over every world.xml cluster (Data/ContentCheck), a
// summary, then the exit code. No port, save or log file is touched, so a build of its own (README: a running server
// locks its bin folder) can run next to a running server.
if (args.Contains("--check-content"))
{
    try
    {
        return ContentCheck.Run(new ClusterStore(config.ClientGameDataPath, config.Clusters), Console.Out, all: args.Contains("-v"));
    }
    catch (InvalidDataException e)
    {
        Log.Error("main", e.Message);
        return 1;
    }
}
// --plugins-build [name...]: builds the plugins of Plugins:Path and of the Mods:Packs packs (all of them, enabled or not; or the named ones)
// into data/plugins-bin, prints each outcome and exits (server/plugins/README.md). No port is opened.
if (args.Contains("--plugins-build"))
{
    var names = args.SkipWhile(a => a != "--plugins-build").Skip(1).TakeWhile(a => !a.StartsWith('-')).ToHashSet(StringComparer.OrdinalIgnoreCase);
    var failed = 0;
    var found = AlbionLocal.Server.Plugins.PluginManifest.Discover(config.Plugins.Path, config.ModsPath, config.ModPacks);
    foreach (var m in found.Where(m => names.Count == 0 || names.Contains(m.Name)))
    {
        var (dll, error) = AlbionLocal.Server.Plugins.PluginBuilder.Resolve(m, Path.Combine(config.SavesPath, "plugins-bin"), build: true, force: args.Contains("--force"));
        Console.WriteLine(dll is not null ? $"OK    {m.Name}: {dll}" : $"FAIL  {m.Name}: {error}");
        if (dll is null) failed++;
    }
    if (found.Count == 0) Console.WriteLine($"no plugins in {config.Plugins.Path}");
    return failed == 0 ? 0 : 1;
}
// Here, not in ServerHost: the self-test builds its servers with the default config and must not write into data/.
if (config.LogFile is { } logFile) Log.SetFile(logFile);

try
{
    ServerHost built;
    try
    {
        built = new ServerHost(config);
    }
    catch (InvalidDataException e)
    {
        // The client data (Paths:ClientGameData) is missing or broken: no start without it.
        Log.Error("main", e.Message);
        return 1;
    }
    using var host = built;
    host.Start();
    // GM commands typed here (stdin); in a background shell stdin is empty and the reader just ends.
    ConsoleInput.Start(host.World);
    Log.Info("main", "press Ctrl+C to stop");
    if (!Console.IsInputRedirected) Log.Info("main", "commands: type .help here, or in the game's Say chat");

    var stop = new TaskCompletionSource();
    Console.CancelKeyPress += (_, e) =>
    {
        e.Cancel = true;
        stop.TrySetResult();
    };
    await stop.Task;
    Log.Info("main", "stopping");
    return 0;
}
finally
{
    Log.SetFile(null);
}
