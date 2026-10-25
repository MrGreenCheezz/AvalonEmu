using System.Collections.ObjectModel;
using System.Globalization;

namespace AlbionLocal.Server.Configuration;

public sealed record ConfigurationLoadResult(ServerConfig Config, string Directory, IReadOnlyDictionary<string, string> Sources, IReadOnlyDictionary<string, string> Overrides)
{
    public void WriteSummary(TextWriter writer)
    {
        writer.WriteLine($"Configuration directory: {Directory}");
        writer.WriteLine($"Files: {string.Join(", ", ConfigurationOptions.FileNames)}");
        writer.WriteLine($"Preset: {Config.Preset}; tutorial: {Config.TutorialOpening}; persistence: {Config.PersistenceProvider}");
        writer.WriteLine($"Login: {Config.LoginEndPoint}; world: {Config.GameEndPoint}; chat: {Config.ChatEndPoint} (enabled: {Config.ChatEnabled}); public: {Config.PublicAddress}");
        writer.WriteLine($"GameData: {Config.ClientGameDataPath}\nContent: {Config.ContentPath}\nSaves: {Config.SavesPath}\nLog: {Config.LogFile ?? "console only"}");
        writer.WriteLine($"Rates: {Config.Rates.Describe()}");
        writer.WriteLine(string.Create(CultureInfo.InvariantCulture, $"Effective fame: combat x{Config.Rates.Fame * Config.Rates.CombatFame}, gathering x{Config.Rates.Fame * Config.Rates.GatheringFame}, crafting x{Config.Rates.Fame * Config.Rates.CraftingFame}; existing game bonuses still apply"));
        foreach (var (key, value) in Overrides) writer.WriteLine($"Argument override: {key}={value} (file: {ConfigurationOptions.Owner(key)})");
    }
}

public static class ServerConfiguration
{
    public static (string Directory, string[] Arguments) ParseArguments(IEnumerable<string> arguments)
    {
        var args = arguments.ToArray();
        var directory = "config";
        var remaining = new List<string>();
        bool selected = false;
        for (var i = 0; i < args.Length; i++)
        {
            if (args[i] == "--config-dir" || args[i].StartsWith("--config-dir=", StringComparison.Ordinal))
            {
                if (selected) throw new InvalidDataException("--config-dir may only be specified once");
                selected = true;
                directory = args[i] == "--config-dir" ? ++i < args.Length && !args[i].StartsWith('-') ? args[i] : throw new InvalidDataException("--config-dir requires a directory") : args[i][13..];
                if (string.IsNullOrWhiteSpace(directory)) throw new InvalidDataException("--config-dir requires a directory");
            }
            else if (args[i] == "--config" || args[i].StartsWith("--config=", StringComparison.Ordinal))
                throw new InvalidDataException("Use --config-dir=<directory>; there is no main configuration file");
            else remaining.Add(args[i]);
        }
        return (Path.GetFullPath(directory), remaining.ToArray());
    }

    public static ConfigurationLoadResult Load(string directory, IEnumerable<string> arguments)
    {
        directory = Path.GetFullPath(directory);
        var values = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var sources = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var name in ConfigurationOptions.FileNames)
        {
            var path = Path.Combine(directory, name);
            if (!File.Exists(path)) throw new InvalidDataException($"Required configuration file missing: {path}");
            var editable = EditableConfiguration.Parse(File.ReadAllText(path), path);
            foreach (var (key, value) in editable.Values)
            {
                var owner = ConfigurationOptions.Owner(key);
                if (owner != name) throw new InvalidDataException($"{path}: {key} belongs in {owner}");
                if (!values.TryAdd(key, value)) throw new InvalidDataException($"duplicate config key: {key}");
                sources.Add(key, path);
            }
        }
        var args = arguments.ToArray();
        var overrides = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var arg in args)
            if (arg.StartsWith("--") && arg.IndexOf('=') is > 2 and var eq) overrides[arg[2..eq]] = arg[(eq + 1)..];
        var baseDirectory = Path.GetDirectoryName(Path.TrimEndingDirectorySeparator(directory)) ?? directory;
        var editableValues = new EditableConfiguration(values);
        // Full composition validates cross-option constraints after applying explicit arguments.
        var config = editableValues.Validate(baseDirectory, args);
        return new(config, directory, new ReadOnlyDictionary<string, string>(sources), new ReadOnlyDictionary<string, string>(overrides));
    }
}
