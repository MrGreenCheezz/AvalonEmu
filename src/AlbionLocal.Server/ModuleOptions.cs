using System.Globalization;
using System.Text.Json;

namespace AlbionLocal.Server;

/// <summary>The C# modules (Modules:*, docs/modules/README.md, <see cref="Modules.ModuleHost"/>): on by default, but only modules that are present and enabled load: an empty modules/ folder changes nothing.</summary>
public sealed record ModuleOptions
{
    /// <summary>Modules:Enabled (true): whether modules load at all.</summary>
    public bool Enabled { get; init; } = true;

    /// <summary>Modules:Path (modules): the module folders, one per module.</summary>
    public string Path { get; init; } = System.IO.Path.Combine(Environment.CurrentDirectory, "modules");

    /// <summary>
    /// Modules:List (none; a JSON array or a comma list): the modules to load whatever their module.jsonc "enabled" says; empty = every module
    /// whose module.jsonc has "enabled": true (the default). The module's own config/modules/&lt;id&gt;.jsonc can still say "Enabled": false.
    /// </summary>
    public IReadOnlyList<string> List { get; init; } = [];

    /// <summary>The folder of the server owner's module settings (&lt;config dir&gt;/modules, one &lt;id&gt;.jsonc each); empty = none (a bare test world).</summary>
    public string ConfigPath { get; init; } = "";

    /// <summary>Modules:Build (true): build modules from their sources (dotnet build, when they changed); false = prebuilt DLLs only.</summary>
    public bool Build { get; init; } = true;

    /// <summary>Modules:MaxErrors (10, 1-10000): a module whose handlers have thrown this many times is disabled.</summary>
    public int MaxErrors { get; init; } = 10;

    /// <summary>Modules:SlowMs (50, 1-60000): a handler that takes longer is logged as slow (once a minute per module).</summary>
    public int SlowMs { get; init; } = 50;

    internal static ModuleOptions Load(Dictionary<string, string> values, ModuleOptions d, string path)
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
            catch (JsonException) { throw new InvalidDataException($"{key}: '{v}' is not a list of module names"); }
            foreach (var n in names)
                if (n.Length == 0 || n.IndexOfAny(['/', '\\', ':']) >= 0 || n is "." or "..")
                    throw new InvalidDataException($"{key}: '{n}' is not a module name");
            return names;
        }
        return new ModuleOptions
        {
            Enabled = Flag("Modules:Enabled", d.Enabled),
            Path = path,
            List = Names("Modules:List", d.List),
            Build = Flag("Modules:Build", d.Build),
            MaxErrors = Int("Modules:MaxErrors", d.MaxErrors, 1, 10_000),
            SlowMs = Int("Modules:SlowMs", d.SlowMs, 1, 60_000),
        };
    }
}
