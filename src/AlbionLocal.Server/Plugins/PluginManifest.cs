using System.Text.Json;
using System.Text.Json.Serialization;

namespace AlbionLocal.Server.Plugins;

/// <summary>
/// A plugin folder's plugin.json (server/plugins/README.md): name, version, author, description, apiVersion, enabled, dependencies and,
/// for a prebuilt plugin, the DLL ("assembly"). Plus where it was found (<see cref="Directory"/>, <see cref="Pack"/>).
/// </summary>
public sealed record PluginManifest
{
    public string Name { get; init; } = "";
    public string Version { get; init; } = "1.0.0";
    public string Author { get; init; } = "";
    public string Description { get; init; } = "";

    /// <summary>The API version the plugin was written for (<see cref="AlbionLocal.Plugin.Api.PluginApi.Version"/>).</summary>
    public int ApiVersion { get; init; } = 1;

    /// <summary>Whether it loads without being named in Plugins:List (default true; the examples ship false).</summary>
    public bool Enabled { get; init; } = true;

    /// <summary>The plugins that must be loaded first (by name).</summary>
    public IReadOnlyList<string> Dependencies { get; init; } = [];

    /// <summary>A prebuilt DLL in the folder (relative path); empty = build the folder's *.cs (or the one DLL of the folder when it has no sources).</summary>
    public string Assembly { get; init; } = "";

    /// <summary>The folder (absolute).</summary>
    [JsonIgnore] public string Directory { get; init; } = "";

    /// <summary>The mod pack it came with (mods/&lt;pack&gt;/plugins/), null for Plugins:Path's.</summary>
    [JsonIgnore] public string? Pack { get; init; }

    private static readonly JsonSerializerOptions Json = new()
    {
        PropertyNameCaseInsensitive = true, ReadCommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true,
    };

    /// <summary>Reads <paramref name="dir"/>/plugin.json; the name defaults to the folder's. Throws <see cref="InvalidDataException"/> on a bad file.</summary>
    public static PluginManifest Read(string dir, string? pack)
    {
        var file = Path.Combine(dir, "plugin.json");
        PluginManifest m;
        try { m = JsonSerializer.Deserialize<PluginManifest>(File.ReadAllText(file), Json) ?? new PluginManifest(); }
        catch (JsonException e) { throw new InvalidDataException($"{file}: {e.Message}"); }
        var name = string.IsNullOrWhiteSpace(m.Name) ? Path.GetFileName(dir) : m.Name.Trim();
        if (name.IndexOfAny(['/', '\\', ':', ' ']) >= 0) throw new InvalidDataException($"{file}: name '{name}' may not hold / \\ : or spaces");
        return m with { Name = name, Directory = Path.GetFullPath(dir), Pack = pack, Dependencies = m.Dependencies ?? [] };
    }

    /// <summary>
    /// The plugin folders of <paramref name="pluginsPath"/> and of the packs <paramref name="packs"/> under <paramref name="modsPath"/>
    /// (mods/&lt;pack&gt;/plugins/&lt;name&gt;/), each with a plugin.json. A bad plugin.json is logged and skipped; a name found twice keeps the first.
    /// </summary>
    public static List<PluginManifest> Discover(string pluginsPath, string modsPath, IEnumerable<string> packs)
    {
        var found = new List<PluginManifest>();
        void Scan(string root, string? pack)
        {
            if (!System.IO.Directory.Exists(root)) return;
            foreach (var dir in System.IO.Directory.GetDirectories(root).Order(StringComparer.OrdinalIgnoreCase))
            {
                if (!File.Exists(Path.Combine(dir, "plugin.json"))) continue;
                try
                {
                    var m = Read(dir, pack);
                    if (found.Find(f => f.Name.Equals(m.Name, StringComparison.OrdinalIgnoreCase)) is { } other)
                        Photon.Log.Warn("plugins", $"{dir}: a plugin named {m.Name} is already at {other.Directory}; skipped");
                    else found.Add(m);
                }
                catch (Exception e) when (e is InvalidDataException or IOException) { Photon.Log.Error("plugins", e.Message); }
            }
        }
        Scan(pluginsPath, null);
        foreach (var pack in packs) Scan(Path.Combine(modsPath, pack, "plugins"), pack);
        return found;
    }

    /// <summary>The C# sources of the folder (bin/ and obj/ left out).</summary>
    public IEnumerable<string> Sources() =>
        System.IO.Directory.EnumerateFiles(Directory, "*.cs", SearchOption.AllDirectories)
            .Where(f => !IsBuildFolder(Path.GetRelativePath(Directory, f)));

    private static bool IsBuildFolder(string relative) =>
        relative.Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) is [var first, _, ..]
        && (first.Equals("bin", StringComparison.OrdinalIgnoreCase) || first.Equals("obj", StringComparison.OrdinalIgnoreCase));
}
