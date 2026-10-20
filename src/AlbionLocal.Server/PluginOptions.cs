using System.Globalization;
using System.Text.Json;

namespace AlbionLocal.Server;

/// <summary>The C# plugins (Plugins:*, server/plugins/README.md, <see cref="Plugins.PluginHost"/>): off under Preset official, on under pve.</summary>
public sealed record PluginOptions
{
    /// <summary>Plugins:Enabled (false; the pve preset true): whether plugins load at all.</summary>
    public bool Enabled { get; init; }

    /// <summary>Plugins:Path (plugins): the plugin folders, one per plugin.</summary>
    public string Path { get; init; } = System.IO.Path.Combine(Environment.CurrentDirectory, "plugins");

    /// <summary>
    /// Plugins:List (none; a JSON array or a comma list): the plugins to load whatever their plugin.json "enabled" says; empty = every plugin
    /// whose plugin.json has "enabled": true (and every plugin of a pack in Mods:Packs unless it says false).
    /// </summary>
    public IReadOnlyList<string> List { get; init; } = [];

    /// <summary>Plugins:Build (true): build plugins from their sources (dotnet build, when they changed); false = prebuilt DLLs only.</summary>
    public bool Build { get; init; } = true;

    /// <summary>Plugins:MaxErrors (10, 1-10000): a plugin whose handlers have thrown this many times is disabled.</summary>
    public int MaxErrors { get; init; } = 10;

    /// <summary>Plugins:SlowMs (50, 1-60000): a handler that takes longer is logged as slow (once a minute per plugin).</summary>
    public int SlowMs { get; init; } = 50;

    internal static PluginOptions Load(Dictionary<string, string> values, PluginOptions d)
    {
        string? Take(string key) => values.Remove(key, out var v) ? v : null;
        bool Flag(string key, bool fallback) => Take(key) is not { } v ? fallback
            : bool.TryParse(v, out var f) ? f : throw new InvalidDataException($"{key}: '{v}' is not true or false");
        int Int(string key, int fallback, int min, int max) => Take(key) is not { } v ? fallback
            : int.TryParse(v, NumberStyles.Integer, CultureInfo.InvariantCulture, out var i) && i >= min && i <= max ? i
            : throw new InvalidDataException($"{key}: '{v}' is not {min}..{max}");
        IReadOnlyList<string> Names(string key, IReadOnlyList<string> fallback)
        {
            if (Take(key) is not { } v) return fallback;
            string[] names;
            try
            {
                names = v.TrimStart().StartsWith('[') ? JsonSerializer.Deserialize<string[]>(v) ?? []
                    : v.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            }
            catch (JsonException) { throw new InvalidDataException($"{key}: '{v}' is not a list of plugin names"); }
            foreach (var n in names)
                if (n.Length == 0 || n.IndexOfAny(['/', '\\', ':']) >= 0 || n is "." or "..")
                    throw new InvalidDataException($"{key}: '{n}' is not a plugin name");
            return names;
        }
        return new PluginOptions
        {
            Enabled = Flag("Plugins:Enabled", d.Enabled),
            Path = Take("Plugins:Path") is { } path ? System.IO.Path.GetFullPath(path, Environment.CurrentDirectory) : d.Path,
            List = Names("Plugins:List", d.List),
            Build = Flag("Plugins:Build", d.Build),
            MaxErrors = Int("Plugins:MaxErrors", d.MaxErrors, 1, 10_000),
            SlowMs = Int("Plugins:SlowMs", d.SlowMs, 1, 60_000),
        };
    }
}
