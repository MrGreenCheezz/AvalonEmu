using System.Text.Json;
using System.Text.Json.Serialization;
using AlbionLocal.Modules;

namespace AlbionLocal.Server.Modules;

/// <summary>
/// A module folder's module.jsonc (docs/modules/README.md): id, version, author, description, api, enabled, requires, loadAfter, coreAccess and, for a prebuilt
/// module, the DLL ("assembly"). Plus where it was found (<see cref="Directory"/>). Only "id" is needed, and it defaults to the folder's name.
/// </summary>
public sealed record ModuleManifest
{
    /// <summary>The file that makes a folder a module.</summary>
    public const string FileName = "module.jsonc";

    /// <summary>The module's id: the folder's name by default.</summary>
    public string Id { get; init; } = "";

    /// <summary>The same as <see cref="Id"/>.</summary>
    [JsonIgnore] public string Name => Id;

    public string Version { get; init; } = "1.0.0";
    public string Author { get; init; } = "";
    public string Description { get; init; } = "";

    /// <summary>The module API version it was written for ("1.0"; <see cref="ModuleApi.Version"/>).</summary>
    public string Api { get; init; } = ModuleApi.Version;

    /// <summary>Whether it loads without being named in Modules:List (default true; the examples ship false).</summary>
    public bool Enabled { get; init; } = true;

    /// <summary>The modules that must be loaded before this one, or it does not load.</summary>
    public IReadOnlyList<string> Requires { get; init; } = [];

    /// <summary>The modules that load before this one when they are present (its handlers run after theirs at equal priority).</summary>
    public IReadOnlyList<string> LoadAfter { get; init; } = [];

    /// <summary>The module uses the server's own assemblies (AlbionLocal.Server, Protocol, Photon) directly: everything is reachable, nothing is promised to stay.</summary>
    public bool CoreAccess { get; init; }

    /// <summary>A prebuilt DLL in the folder (relative path); empty = build the folder's *.cs (or the one DLL of the folder when it has no sources).</summary>
    public string Assembly { get; init; } = "";

    /// <summary>The folder (absolute).</summary>
    [JsonIgnore] public string Directory { get; init; } = "";

    /// <summary>What module.jsonc holds beyond the known keys (a typo gets a hint).</summary>
    [JsonExtensionData] public Dictionary<string, JsonElement>? Unknown { get; init; }

    private static readonly JsonSerializerOptions Json = new()
    {
        PropertyNameCaseInsensitive = true, ReadCommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true,
    };

    private static readonly string[] KnownKeys = ["id", "version", "author", "description", "api", "enabled", "requires", "loadAfter", "coreAccess", "assembly"];

    /// <summary>Reads <paramref name="dir"/>/module.jsonc; the id defaults to the folder's. Throws <see cref="InvalidDataException"/> with what to fix on a bad file.</summary>
    public static ModuleManifest Read(string dir)
    {
        var file = Path.Combine(dir, FileName);
        ModuleManifest m;
        try { m = JsonSerializer.Deserialize<ModuleManifest>(File.ReadAllText(file), Json) ?? new ModuleManifest(); }
        catch (JsonException e) { throw new InvalidDataException($"{file}: {e.Message} (a module.jsonc looks like {{ \"id\": \"mod-x\", \"version\": \"1.0.0\", \"api\": \"{ModuleApi.Version}\" }})"); }
        var id = string.IsNullOrWhiteSpace(m.Id) ? Path.GetFileName(Path.TrimEndingDirectorySeparator(dir)) : m.Id.Trim();
        if (id.IndexOfAny(['/', '\\', ':', ' ']) >= 0) throw new InvalidDataException($"{file}: id '{id}' may not hold / \\ : or spaces");
        return m with { Id = id, Directory = Path.GetFullPath(dir), Requires = m.Requires ?? [], LoadAfter = m.LoadAfter ?? [] };
    }

    /// <summary>What is wrong with this manifest that does not stop it from being read: unknown keys, each with the closest known one.</summary>
    public IEnumerable<string> Warnings() =>
        (Unknown ?? []).Keys.Select(k => $"{FileName}: unknown key \"{k}\"{(ModuleHost.Nearest(k, KnownKeys) is { } near ? $" (did you mean \"{near}\"?)" : "")}");

    /// <summary>The module folders of <paramref name="modulesPath"/>, each with a module.jsonc (also one level deeper: modules/examples/mod-x). A bad file goes to <paramref name="problem"/> (the log) and is skipped; an id found twice keeps the first.</summary>
    public static List<ModuleManifest> Discover(string modulesPath, Action<string>? problem = null)
    {
        problem ??= m => Photon.Log.Error("modules", m);
        var found = new List<ModuleManifest>();
        if (!System.IO.Directory.Exists(modulesPath)) return found;
        var folders = System.IO.Directory.GetDirectories(modulesPath).Order(StringComparer.OrdinalIgnoreCase)
            .SelectMany(d => File.Exists(Path.Combine(d, FileName)) ? new[] { d } : System.IO.Directory.GetDirectories(d).Where(n => File.Exists(Path.Combine(n, FileName))).Order(StringComparer.OrdinalIgnoreCase).ToArray());
        foreach (var dir in folders)
        {
            try
            {
                var m = Read(dir);
                if (found.Find(f => f.Id.Equals(m.Id, StringComparison.OrdinalIgnoreCase)) is { } other) problem($"{dir}: a module named {m.Id} is already at {other.Directory}; skipped");
                else found.Add(m);
            }
            catch (Exception e) when (e is InvalidDataException or IOException) { problem(e.Message); }
        }
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
