using System.Text.Json;
using System.Xml;
using System.Xml.Linq;

namespace AlbionLocal.Server.Mods;

/// <summary>pack.json of a content pack: its name, version and the client build it was written for.</summary>
public sealed record PackInfo
{
    public string Name { get; init; } = "";
    public string Version { get; init; } = "";

    /// <summary>The client build the pack targets (the first line of the client's version.txt, e.g. albiononline-win32-full-1.32.030.345319).</summary>
    public string? Client { get; init; }

    public string? Description { get; init; }
}

/// <summary>One XML patch file of a pack: <c>items.xml</c> patches items.bin, <c>spells.xml</c> spells.bin, and so on.</summary>
public sealed record PatchFile(string Pack, string Path, string Target, XElement Root);

/// <summary>
/// A content pack (modkit SPEC §2, tools/AlbionLocal.ModKit/README.md): a folder mods/&lt;pack&gt;/ with pack.json, one XML
/// patch file per GameData file it changes (named after it: items.xml → items.bin) and, optionally, server/drops.json (the
/// server-only extras, <see cref="ModDrops"/>). Loading only reads and parses; what a patch means is checked by <see cref="ModBuilder"/>.
/// </summary>
public sealed class ModPack
{
    public required string Dir { get; init; }
    public required PackInfo Info { get; init; }
    public required IReadOnlyList<PatchFile> Patches { get; init; }

    /// <summary>server/drops.json, or null.</summary>
    public string? DropsPath { get; init; }

    public string Name => Info.Name;

    private static readonly JsonSerializerOptions Json = new()
    {
        PropertyNameCaseInsensitive = true, ReadCommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true,
    };

    /// <summary>Reads the pack in <paramref name="dir"/>; a missing pack.json or a patch that is no XML throws <see cref="InvalidDataException"/>.</summary>
    public static ModPack Load(string dir)
    {
        dir = Path.GetFullPath(dir);
        var infoPath = Path.Combine(dir, "pack.json");
        if (!File.Exists(infoPath)) throw new InvalidDataException($"{dir}: no pack.json (is this a pack folder?)");
        PackInfo info;
        try { info = JsonSerializer.Deserialize<PackInfo>(File.ReadAllText(infoPath), Json) ?? new PackInfo(); }
        catch (JsonException e) { throw new InvalidDataException($"{infoPath}: {e.Message}", e); }
        if (info.Name.Length == 0) info = info with { Name = Path.GetFileName(dir) };
        var patches = new List<PatchFile>();
        foreach (var path in Directory.GetFiles(dir, "*.xml").Order(StringComparer.OrdinalIgnoreCase))
        {
            XElement root;
            try
            {
                using var reader = XmlReader.Create(path, new XmlReaderSettings { DtdProcessing = DtdProcessing.Prohibit, IgnoreComments = true });
                root = XElement.Load(reader, LoadOptions.SetLineInfo);
            }
            catch (XmlException e) { throw new InvalidDataException($"{path}: {e.Message}", e); }
            patches.Add(new PatchFile(info.Name, path, Path.GetFileNameWithoutExtension(path) + ".bin", root));
        }
        var drops = Path.Combine(dir, "server", "drops.json");
        return new ModPack { Dir = dir, Info = info, Patches = patches, DropsPath = File.Exists(drops) ? drops : null };
    }

    /// <summary>The packs <paramref name="names"/> (folder names or paths) under <paramref name="modsDir"/>, in that order.</summary>
    public static List<ModPack> LoadAll(string modsDir, IEnumerable<string> names) =>
        [.. names.Select(n => Load(Path.IsPathRooted(n) || n.Contains('/') || n.Contains('\\') ? n : Path.Combine(modsDir, n)))];
}
