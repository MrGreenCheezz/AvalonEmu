using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using System.Xml;
using System.Xml.Linq;

namespace AlbionLocal.Server.Web;

/// <summary>A drop row of server/drops.json as the editor shows it.</summary>
public sealed record DropRow(string Item, string[] Mobs, string[] Chests, string? Category, double Chance, int Amount);

/// <summary>
/// The files of one content pack as the web panel's mod editor writes them (modeditor SPEC §3): only mods/&lt;pack&gt;/ with a name of
/// [a-z0-9-], only the fixed files pack.json, items.xml, spells.xml, buildings.xml, localization.xml, server/drops.json and editor.json
/// (the editor's own notes: which template each new element was cloned from; the modkit ignores it). Every file is written as a new
/// temp file moved over the old one, and is at most <see cref="MaxFile"/> bytes. Nothing outside the pack folder is touched; the
/// game's files are never written here (installing is the modkit's build).
/// </summary>
public sealed partial class PackFiles
{
    public const int MaxFile = 2 * 1024 * 1024;
    public const int MaxPacks = 200;

    [GeneratedRegex("^[a-z0-9][a-z0-9-]{0,39}$")]
    public static partial Regex PackName();

    /// <summary>A new item or spell name: upper case letters, digits and _ (as the official ones), 3..80.</summary>
    [GeneratedRegex("^[A-Z0-9][A-Z0-9_]{2,79}$")]
    public static partial Regex NewName();

    /// <summary>A reference typed into a form (an item, spell, station, mob pattern): letters, digits, _ @ - . * and ?.</summary>
    [GeneratedRegex(@"^[A-Za-z0-9_@\-.*?]{1,100}$")]
    public static partial Regex RefName();

    [GeneratedRegex(@"^[0-9A-Za-z.+\-]{1,20}$")]
    public static partial Regex Version();

    public static readonly string[] PatchFiles = ["items.xml", "spells.xml", "buildings.xml", "localization.xml"];

    public string Name { get; }
    public string Dir { get; }

    private PackFiles(string name, string dir) => (Name, Dir) = (name, dir);

    /// <summary>The pack folders under <paramref name="modsDir"/> with a valid name and a pack.json.</summary>
    public static List<string> List(string modsDir) => !Directory.Exists(modsDir) ? [] :
        [.. Directory.GetDirectories(modsDir).Select(Path.GetFileName).OfType<string>()
            .Where(n => PackName().IsMatch(n) && File.Exists(Path.Combine(modsDir, n, "pack.json"))).Order(StringComparer.Ordinal).Take(MaxPacks)];

    /// <summary>The existing pack <paramref name="name"/>, or null (a bad name, no folder or no pack.json).</summary>
    public static PackFiles? Open(string modsDir, string? name)
    {
        if (name is null || !PackName().IsMatch(name)) return null;
        var root = Path.GetFullPath(modsDir);
        var dir = Path.GetFullPath(Path.Combine(root, name));
        if (!dir.StartsWith(root.TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)) return null;
        if (!File.Exists(Path.Combine(dir, "pack.json"))) return null;
        if (new DirectoryInfo(dir).LinkTarget is not null) return null; // no junction out of mods/
        return new PackFiles(name, dir);
    }

    /// <summary>Creates mods/&lt;name&gt;/pack.json; an error text, or null when made.</summary>
    public static string? Create(string modsDir, string name, string version, string description, string? client)
    {
        if (!PackName().IsMatch(name)) return "the pack name: 1 to 40 of a-z, 0-9 and - (starting with a letter or digit)";
        if (!Version().IsMatch(version)) return "the version: 1 to 20 of 0-9 A-Z a-z . + - (e.g. 1.0.0)";
        if (description.Length > 300) return "the description: at most 300 characters";
        if (List(modsDir).Count >= MaxPacks) return $"at most {MaxPacks} packs";
        var dir = Path.Combine(Path.GetFullPath(modsDir), name);
        if (Directory.Exists(dir)) return $"mods/{name} exists already";
        Directory.CreateDirectory(dir);
        var info = new JsonObject { ["Name"] = name, ["Version"] = version, ["Client"] = client, ["Description"] = description };
        WriteText(Path.Combine(dir, "pack.json"), info.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
        return null;
    }

    /// <summary>pack.json as text fields (Name, Version, Client, Description).</summary>
    public Dictionary<string, string> Info()
    {
        var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        try
        {
            if (JsonNode.Parse(ReadText("pack.json"), null, new JsonDocumentOptions { CommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true }) is JsonObject o)
                foreach (var (k, v) in o)
                    if (v is JsonValue jv && jv.TryGetValue<string>(out var s)) result[k] = s;
        }
        catch (JsonException) { }
        return result;
    }

    private string PathOf(string file) => Path.Combine(Dir, file);

    private string ReadText(string file)
    {
        var path = PathOf(file);
        if (!File.Exists(path)) return "";
        if (new FileInfo(path).Length > MaxFile) throw new InvalidDataException($"{Name}/{file} is larger than {MaxFile / 1024 / 1024} MB: edit it by hand");
        return File.ReadAllText(path);
    }

    private static void WriteText(string path, string text)
    {
        var bytes = new UTF8Encoding(false).GetBytes(text);
        if (bytes.Length > MaxFile) throw new InvalidDataException($"{Path.GetFileName(path)} would be larger than {MaxFile / 1024 / 1024} MB");
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var temp = path + ".editor-tmp";
        File.WriteAllBytes(temp, bytes);
        File.Move(temp, path, overwrite: true);
    }

    /// <summary>A patch file (items.xml, …) as XML; a new &lt;patch/&gt; when the pack has none.</summary>
    public XDocument Patch(string file)
    {
        if (!PatchFiles.Contains(file)) throw new ArgumentException(file);
        var text = ReadText(file);
        if (text.Length == 0) return new XDocument(new XDeclaration("1.0", "utf-8", null), new XElement("patch"));
        try
        {
            using var reader = XmlReader.Create(new StringReader(text), new XmlReaderSettings { DtdProcessing = DtdProcessing.Prohibit });
            return XDocument.Load(reader);
        }
        catch (XmlException e) { throw new InvalidDataException($"{Name}/{file}: {e.Message}", e); }
    }

    public bool Has(string file) => File.Exists(PathOf(file));

    public string Raw(string file) => ReadText(file);

    public void Save(string file, XDocument doc)
    {
        if (!PatchFiles.Contains(file)) throw new ArgumentException(file);
        var path = PathOf(file);
        if (doc.Root is null || !doc.Root.HasElements)
        {
            if (File.Exists(path)) File.Delete(path); // an empty patch file: none
            return;
        }
        var sb = new StringBuilder();
        using (var w = XmlWriter.Create(sb, new XmlWriterSettings { Indent = true, IndentChars = "  ", OmitXmlDeclaration = true, NewLineHandling = NewLineHandling.Replace }))
            doc.Root.Save(w);
        WriteText(path, "<?xml version=\"1.0\" encoding=\"utf-8\"?>\n" + sb + "\n");
    }

    // ---------------------------------------------------------------- the added elements (<add> without uniquename)

    private static IEnumerable<XElement> AddOps(XDocument doc) =>
        doc.Root!.Elements("add").Where(a => a.Attribute("uniquename") is null && a.Attribute("path") is null);

    /// <summary>The new top-level elements the pack adds to this file.</summary>
    public static IEnumerable<XElement> Added(XDocument doc) => AddOps(doc).SelectMany(a => a.Elements());

    public static XElement? Added(XDocument doc, string name) =>
        Added(doc).FirstOrDefault(e => string.Equals(e.Attribute("uniquename")?.Value, name, StringComparison.OrdinalIgnoreCase));

    /// <summary>Puts <paramref name="element"/> in place of the added element of the same name, or appends it to the last &lt;add&gt;.</summary>
    public static void PutAdded(XDocument doc, XElement element)
    {
        var name = element.Attribute("uniquename")!.Value;
        if (Added(doc, name) is { } old) { old.ReplaceWith(element); return; }
        var add = AddOps(doc).LastOrDefault();
        if (add is null) doc.Root!.Add(add = new XElement("add"));
        add.Add(element);
    }

    public static bool RemoveAdded(XDocument doc, string name)
    {
        if (Added(doc, name) is not { } old) return false;
        var parent = old.Parent!;
        old.Remove();
        if (!parent.HasElements) parent.Remove();
        return true;
    }

    // ---------------------------------------------------------------- localization.xml

    public static (string? En, string? Ru) Loc(XDocument loc, string tuid)
    {
        var entry = loc.Root!.Elements("entry").FirstOrDefault(e => (string?)e.Attribute("tuid") == tuid);
        string? Text(string lang) => entry?.Elements("text").FirstOrDefault(t => string.Equals((string?)t.Attribute("lang"), lang, StringComparison.OrdinalIgnoreCase))?.Value;
        return (Text("EN-US"), Text("RU-RU"));
    }

    /// <summary>Sets the EN-US and RU-RU text of <paramref name="tuid"/> (both empty: the entry is removed).</summary>
    public static void SetLoc(XDocument loc, string tuid, string en, string ru)
    {
        foreach (var old in loc.Root!.Elements("entry").Where(e => (string?)e.Attribute("tuid") == tuid).ToList()) old.Remove();
        if (en.Length == 0 && ru.Length == 0) return;
        var entry = new XElement("entry", new XAttribute("tuid", tuid));
        if (en.Length > 0) entry.Add(new XElement("text", new XAttribute("lang", "EN-US"), en));
        if (ru.Length > 0) entry.Add(new XElement("text", new XAttribute("lang", "RU-RU"), ru));
        loc.Root.Add(entry);
    }

    // ---------------------------------------------------------------- buildings.xml: the stations that craft an item

    /// <summary>The stations whose craftingitemlist the pack adds <paramref name="item"/> to.</summary>
    public static List<string> StationsOf(XDocument buildings, string item) =>
        [.. buildings.Root!.Elements("add").Where(a => a.Attribute("uniquename") is not null && (string?)a.Attribute("path") == "craftingitemlist"
            && a.Elements("craftitem").Any(c => string.Equals((string?)c.Attribute("uniquename"), item, StringComparison.OrdinalIgnoreCase)))
            .Select(a => a.Attribute("uniquename")!.Value)];

    /// <summary>Makes exactly <paramref name="stations"/> craft <paramref name="item"/> (craftitem elements made by <paramref name="craftItem"/>).</summary>
    public static void SetStations(XDocument buildings, string item, IEnumerable<string> stations, Func<string, XElement> craftItem)
    {
        foreach (var add in buildings.Root!.Elements("add").Where(a => a.Attribute("uniquename") is not null && (string?)a.Attribute("path") == "craftingitemlist").ToList())
        {
            foreach (var c in add.Elements("craftitem").Where(c => string.Equals((string?)c.Attribute("uniquename"), item, StringComparison.OrdinalIgnoreCase)).ToList()) c.Remove();
            if (!add.HasElements) add.Remove();
        }
        foreach (var station in stations)
            buildings.Root.Add(new XElement("add", new XAttribute("uniquename", station), new XAttribute("path", "craftingitemlist"), craftItem(station)));
    }

    // ---------------------------------------------------------------- server/drops.json

    private static readonly JsonDocumentOptions JsonRead = new() { CommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true };

    private JsonArray DropsArray(out JsonObject root)
    {
        var text = ReadText(Path.Combine("server", "drops.json"));
        root = text.Length > 0 && JsonNode.Parse(text, null, JsonRead) is JsonObject o ? o : [];
        if (root["Drops"] is not JsonArray drops) root["Drops"] = drops = [];
        return drops;
    }

    public List<DropRow> Drops()
    {
        try
        {
            return [.. DropsArray(out _).OfType<JsonObject>().Select(o => new DropRow(
                (string?)o["Item"] ?? "",
                [.. (o["Mobs"] as JsonArray ?? []).Select(m => (string?)m ?? "")],
                [.. (o["Chests"] as JsonArray ?? []).Select(m => (string?)m ?? "")],
                (string?)o["Category"],
                o["Chance"]?.GetValue<double>() ?? 0,
                o["Amount"]?.GetValue<int>() ?? 1))];
        }
        catch (Exception e) when (e is JsonException or InvalidOperationException or FormatException)
        {
            throw new InvalidDataException($"{Name}/server/drops.json: {e.Message}", e);
        }
    }

    /// <summary>Replaces the drops of <paramref name="item"/> with <paramref name="rows"/>; the other items' drops stay.</summary>
    public void SetDrops(string item, IEnumerable<DropRow> rows)
    {
        var drops = DropsArray(out var root);
        foreach (var old in drops.OfType<JsonObject>().Where(o => string.Equals((string?)o["Item"], item, StringComparison.OrdinalIgnoreCase)).ToList()) drops.Remove(old);
        foreach (var r in rows)
        {
            var o = new JsonObject { ["Item"] = r.Item };
            if (r.Mobs.Length > 0) o["Mobs"] = new JsonArray([.. r.Mobs.Select(m => (JsonNode)JsonValue.Create(m))]);
            if (r.Chests.Length > 0) o["Chests"] = new JsonArray([.. r.Chests.Select(m => (JsonNode)JsonValue.Create(m))]);
            if (r.Category is { Length: > 0 }) o["Category"] = r.Category;
            o["Chance"] = r.Chance;
            o["Amount"] = r.Amount;
            drops.Add(o);
        }
        var path = PathOf(Path.Combine("server", "drops.json"));
        if (drops.Count == 0 && root.Count == 1) { if (File.Exists(path)) File.Delete(path); return; }
        WriteText(path, "// Server-only extras of the pack (written by the web panel's mod editor; see tools/AlbionLocal.ModKit/README.md).\n"
            + root.ToJsonString(new JsonSerializerOptions { WriteIndented = true }) + "\n");
    }

    // ---------------------------------------------------------------- editor.json: the templates

    public Dictionary<string, string> Templates()
    {
        try
        {
            var text = ReadText("editor.json");
            if (text.Length > 0 && JsonNode.Parse(text, null, JsonRead) is JsonObject o && o["Templates"] is JsonObject t)
                return t.Where(p => p.Value is JsonValue).ToDictionary(p => p.Key, p => (string?)p.Value ?? "", StringComparer.OrdinalIgnoreCase);
        }
        catch (Exception e) when (e is JsonException or InvalidOperationException) { }
        return new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
    }

    public void SetTemplate(string name, string? template)
    {
        var all = Templates();
        if (template is null) all.Remove(name); else all[name] = template;
        var t = new JsonObject();
        foreach (var (k, v) in all.OrderBy(p => p.Key, StringComparer.Ordinal)) t[k] = v;
        WriteText(PathOf("editor.json"), new JsonObject { ["Note"] = "the web panel's mod editor: which official element each new one was cloned from (the modkit ignores this file)", ["Templates"] = t }
            .ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
    }

    public static string Num(double v) => v.ToString("0.######", CultureInfo.InvariantCulture);
}
