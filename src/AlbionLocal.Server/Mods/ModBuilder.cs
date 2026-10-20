using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using System.Xml;
using System.Xml.Linq;
using AlbionLocal.Server.Data;

namespace AlbionLocal.Server.Mods;

/// <summary>The ids a build gives: how many the official file had (unchanged), a fingerprint of their names, and the new ones.</summary>
public sealed record ModNumbering(int PristineCount, string PristineNamesSha, int TotalCount, Dictionary<string, int> New);

/// <summary>What <see cref="ModBuilder.Build"/> made: the new .bin files (not installed), what was checked, and the new ids.</summary>
public sealed class ModBuildResult
{
    public List<string> Errors { get; } = [];
    public List<string> Warnings { get; } = [];
    public List<string> Notes { get; } = [];

    /// <summary>The built files, by GameData file name ("items.bin"): encrypted like the client's, ready to install.</summary>
    public Dictionary<string, byte[]> Files { get; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>The SHA-256 of every official file the build started from.</summary>
    public Dictionary<string, string> PristineHashes { get; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Which packs changed which file.</summary>
    public Dictionary<string, List<string>> PacksByFile { get; } = new(StringComparer.OrdinalIgnoreCase);

    public ModNumbering? Items { get; set; }
    public ModNumbering? Spells { get; set; }

    /// <summary>The official client build (version.txt), null = unknown.</summary>
    public string? ClientBuild { get; set; }

    public bool Ok => Errors.Count == 0;
}

/// <summary>
/// Applies content packs to the pristine official GameData (modkit SPEC §2): reads every file it patches from the official
/// folder (only reads), applies the packs' operations in order, validates them against the official data and checks that no
/// official id moves, then encodes the changed files like the client's (<see cref="GameDataFile.Encode"/>). Nothing is written
/// to disk here; <see cref="ModKit"/> installs the result.
/// </summary>
public sealed partial class ModBuilder(string officialGameData)
{
    public string Official { get; } = Path.GetFullPath(officialGameData);

    /// <summary>A GameData file as an XML tree, with what is needed to write it back the way it was.</summary>
    private sealed class Doc
    {
        public required string Name { get; init; }
        public required XDocument X { get; init; }
        public required byte[] Header { get; init; }
        public required bool Bom { get; init; }
        public required XElement Container { get; init; }
        public required string KeyAttr { get; init; }

        /// <summary>Whether the order of the top-level elements gives client ids (every file but localization).</summary>
        public bool Ordered => !Name.Equals("localization.bin", StringComparison.OrdinalIgnoreCase);

        public HashSet<XElement> Touched { get; } = [];
        public List<XElement> Added { get; } = [];
        private Dictionary<string, XElement>? _byKey;

        public Dictionary<string, XElement> ByKey
        {
            get
            {
                if (_byKey is not null) return _byKey;
                _byKey = new Dictionary<string, XElement>(StringComparer.OrdinalIgnoreCase);
                foreach (var e in Container.Elements())
                    if (e.Attribute(KeyAttr)?.Value is { Length: > 0 } k) _byKey.TryAdd(k, e);
                return _byKey;
            }
        }
    }

    private readonly Dictionary<string, Doc> _pristine = new(StringComparer.OrdinalIgnoreCase);

    private Doc Open(string name, bool cache)
    {
        if (cache && _pristine.TryGetValue(name, out var cached)) return cached;
        var file = GameDataFile.Open(Official, name);
        var xml = file.DecodeXml(out var header);
        var big = xml.Length > 20_000_000; // localization.bin (78 MB of XML): no whitespace nodes, or the tree takes gigabytes
        XDocument x;
        using (var reader = XmlReader.Create(new MemoryStream(xml), new XmlReaderSettings { DtdProcessing = DtdProcessing.Prohibit, IgnoreWhitespace = big }))
            x = XDocument.Load(reader, big ? LoadOptions.None : LoadOptions.PreserveWhitespace);
        var root = x.Root ?? throw new InvalidDataException($"{name}: no root element");
        var localization = name.Equals("localization.bin", StringComparison.OrdinalIgnoreCase);
        var doc = new Doc
        {
            Name = name, X = x, Header = header, Bom = xml is [0xEF, 0xBB, 0xBF, ..],
            Container = localization ? root.Element("body") ?? throw new InvalidDataException($"{name}: no <body>") : root,
            KeyAttr = localization ? "tuid" : "uniquename",
        };
        if (cache) _pristine[name] = doc;
        return doc;
    }

    /// <summary>Builds <paramref name="packs"/> (in that order) over the official data. Errors leave <see cref="ModBuildResult.Files"/> empty.</summary>
    public ModBuildResult Build(IReadOnlyList<ModPack> packs)
    {
        var result = new ModBuildResult { ClientBuild = ReadBuild(Official) };
        if (!Directory.Exists(Official)) { result.Errors.Add($"official GameData folder {Official} not found"); return result; }
        foreach (var pack in packs)
            if (pack.Info.Client is { Length: > 0 } wanted && result.ClientBuild is { } have && wanted != have)
                result.Warnings.Add($"{pack.Name}: written for client {wanted}, the official client is {have}: check the pack against the new data");

        // 1. The patched files, from the official ones (a fresh tree each: the pristine trees stay for the checks).
        var docs = new Dictionary<string, Doc>(StringComparer.OrdinalIgnoreCase);
        foreach (var patch in packs.SelectMany(p => p.Patches))
        {
            if (!docs.TryGetValue(patch.Target, out var doc))
            {
                if (!File.Exists(Path.Combine(Official, patch.Target)))
                {
                    result.Errors.Add($"{Where(patch, patch.Root)}: no GameData file {patch.Target} for {Path.GetFileName(patch.Path)}");
                    continue;
                }
                try { docs[patch.Target] = doc = Open(patch.Target, cache: false); }
                catch (Exception e) when (e is InvalidDataException or XmlException or IOException or CryptographicException)
                {
                    result.Errors.Add($"{patch.Target}: cannot read the official file ({e.Message})");
                    continue;
                }
                result.PristineHashes[patch.Target] = SafeFiles.Sha256(Path.Combine(Official, patch.Target));
            }
            if (!result.PacksByFile.TryGetValue(patch.Target, out var list)) result.PacksByFile[patch.Target] = list = [];
            if (!list.Contains(patch.Pack)) list.Add(patch.Pack);
            Apply(patch, doc, result.Errors);
        }
        if (!result.Ok) return result;

        // 2. The checks against the official data.
        var pristineItems = Open("items.bin", cache: true);
        var pristineSpells = Open("spells.bin", cache: true);
        var items = docs.GetValueOrDefault("items.bin") ?? pristineItems;
        var spells = docs.GetValueOrDefault("spells.bin") ?? pristineSpells;
        var itemNames = ItemNames(items);
        var spellNames = SpellNames(spells);
        var pristineItemNames = ItemNames(pristineItems);
        var pristineSpellNames = SpellNames(pristineSpells);
        foreach (var doc in docs.Values.Where(d => d.Ordered))
            Validate(doc, docs.ContainsKey(doc.Name) ? Open(doc.Name, cache: true) : doc, pristineItemNames, pristineSpellNames, itemNames, spellNames, result);
        CheckNames(docs, items, spells, result);

        // 3. No official id may move: the top-level order of every ordered file, the item and spell ids exactly.
        foreach (var doc in docs.Values.Where(d => d.Ordered))
            CheckOrder(doc, Open(doc.Name, cache: true), result);
        if (docs.ContainsKey("items.bin")) result.Items = CompareItems(pristineItems, items, result);
        if (docs.ContainsKey("spells.bin")) result.Spells = CompareSpells(pristineSpells, spells, result);
        if (!result.Ok) return result;

        // 4. The new files, and a read-back of each with the server's own reader.
        foreach (var doc in docs.Values)
        {
            var bin = GameDataFile.Encode(Serialize(doc), doc.Header);
            try
            {
                var back = GameDataFile.FromBytes(Path.Combine(Official, doc.Name), bin);
                var count = doc.Ordered ? back.Elements().Count() : back.Read(r => { var n = 0; while (r.ReadToFollowing("tu")) n++; return n; });
                var want = doc.Container.Elements().Count();
                if (count != want) result.Errors.Add($"{doc.Name}: the built file reads back {count} elements, not {want}");
            }
            catch (InvalidDataException e) { result.Errors.Add($"{doc.Name}: the built file does not read back ({e.Message})"); }
            result.Files[doc.Name] = bin;
            result.Notes.Add($"{doc.Name}: {doc.Added.Count} added, {doc.Touched.Count - doc.Added.Count} changed ({string.Join(", ", result.PacksByFile[doc.Name])})");
        }
        if (!result.Ok) result.Files.Clear();
        return result;
    }

    /// <summary>The first line of version.txt, searched from the GameData folder upwards (it sits next to Albion-Online.exe).</summary>
    public static string? ReadBuild(string gameData)
    {
        var dir = new DirectoryInfo(gameData);
        for (var up = 0; dir is not null && up < 5; up++, dir = dir.Parent)
        {
            var path = Path.Combine(dir.FullName, "version.txt");
            if (File.Exists(path)) return File.ReadLines(path).FirstOrDefault()?.Trim();
        }
        return null;
    }

    private static string Where(PatchFile patch, XElement op) =>
        $"{patch.Pack}/{Path.GetFileName(patch.Path)}:{((IXmlLineInfo)op).LineNumber}";

    // ---------------------------------------------------------------- the operations

    private static void Apply(PatchFile patch, Doc doc, List<string> errors)
    {
        foreach (var op in patch.Root.Elements())
        {
            var where = Where(patch, op);
            var key = op.Attribute(doc.KeyAttr)?.Value ?? op.Attribute("key")?.Value ?? op.Attribute("uniquename")?.Value ?? op.Attribute("tuid")?.Value;
            var path = op.Attribute("path")?.Value;
            XElement? Target()
            {
                if (key is null) { errors.Add($"{where}: <{op.Name.LocalName}> needs {doc.KeyAttr}=\"...\""); return null; }
                if (doc.ByKey.GetValueOrDefault(key) is not { } top) { errors.Add($"{where}: no element {doc.KeyAttr}=\"{key}\" in {doc.Name}"); return null; }
                doc.Touched.Add(top);
                if (path is null) return top;
                var at = Resolve(top, path);
                if (at is null) errors.Add($"{where}: {key} has no {path}");
                return at;
            }
            switch (op.Name.LocalName)
            {
                case "add" when key is null:
                    foreach (var child in op.Elements())
                    {
                        var k = child.Attribute(doc.KeyAttr)?.Value;
                        if (k is not { Length: > 0 }) { errors.Add($"{where}: an added <{child.Name.LocalName}> needs {doc.KeyAttr}"); continue; }
                        if (doc.ByKey.ContainsKey(k)) { errors.Add($"{where}: {k} exists already in {doc.Name}: use <replace> or <set>"); continue; }
                        var added = Clone(child);
                        doc.Container.Add(added);
                        doc.ByKey[k] = added;
                        doc.Added.Add(added);
                        doc.Touched.Add(added);
                    }
                    break;
                case "add":
                    if (Target() is { } parent)
                        foreach (var child in op.Elements()) parent.Add(Clone(child));
                    break;
                case "replace":
                    if (op.Elements().Count() != 1) { errors.Add($"{where}: <replace> holds exactly one element"); break; }
                    if (Target() is not { } old) break;
                    var with = Clone(op.Elements().Single());
                    if (path is null)
                    {
                        if (!string.Equals(with.Attribute(doc.KeyAttr)?.Value, key, StringComparison.OrdinalIgnoreCase))
                        {
                            errors.Add($"{where}: the replacement of {key} must keep {doc.KeyAttr}=\"{key}\"");
                            break;
                        }
                        doc.Touched.Remove(old);
                        doc.Touched.Add(with);
                        doc.ByKey[key!] = with;
                    }
                    old.ReplaceWith(with);
                    break;
                case "set":
                    var attr = op.Attribute("attr")?.Value;
                    var value = op.Attribute("value")?.Value;
                    var unset = op.Attribute("unset")?.Value == "true";
                    if (attr is not { Length: > 0 } || (value is null && !unset)) { errors.Add($"{where}: <set> needs attr=\"...\" and value=\"...\" (or unset=\"true\")"); break; }
                    if (path is null && attr == doc.KeyAttr) { errors.Add($"{where}: {doc.KeyAttr} cannot be set; add a new element instead"); break; }
                    Target()?.SetAttributeValue(attr, unset ? null : value);
                    break;
                case "remove" when path is null && doc.Ordered:
                    errors.Add($"{where}: removing {key} from {doc.Name} would move the client ids of everything after it; "
                        + "change it with <set> instead (e.g. unlockedtocraft=\"false\"), or remove what uses it (a craftitem, a loot entry)");
                    break;
                case "remove":
                    if (Target() is not { } gone) break;
                    if (path is null) { doc.ByKey.Remove(key!); doc.Touched.Remove(gone); }
                    gone.Remove();
                    break;
                case "entry" when !doc.Ordered:
                    Entry(op, doc, where, errors);
                    break;
                default:
                    errors.Add($"{where}: unknown operation <{op.Name.LocalName}> for {doc.Name} (add, replace, set, remove{(doc.Ordered ? "" : ", entry")})");
                    break;
            }
        }
    }

    /// <summary>localization.xml: <c>&lt;entry tuid="@ITEMS_X"&gt;&lt;text lang="EN-US"&gt;…&lt;/text&gt;…&lt;/entry&gt;</c> adds the &lt;tu&gt; or sets its languages.</summary>
    private static void Entry(XElement op, Doc doc, string where, List<string> errors)
    {
        var tuid = op.Attribute("tuid")?.Value;
        if (tuid is not { Length: > 1 } || tuid[0] != '@') { errors.Add($"{where}: <entry> needs tuid=\"@...\""); return; }
        if (!doc.ByKey.TryGetValue(tuid, out var tu))
        {
            tu = new XElement("tu", new XAttribute("tuid", tuid));
            doc.Container.Add(tu);
            doc.ByKey[tuid] = tu;
            doc.Added.Add(tu);
        }
        doc.Touched.Add(tu);
        foreach (var text in op.Elements("text"))
        {
            var lang = text.Attribute("lang")?.Value;
            if (lang is not { Length: > 0 }) { errors.Add($"{where}: <text> needs lang=\"EN-US\" (or RU-RU, DE-DE, …)"); continue; }
            var tuv = tu.Elements("tuv").FirstOrDefault(t => string.Equals((string?)t.Attribute(XNamespace.Xml + "lang"), lang, StringComparison.OrdinalIgnoreCase));
            if (tuv is null) tu.Add(tuv = new XElement("tuv", new XAttribute(XNamespace.Xml + "lang", lang), new XElement("seg")));
            var seg = tuv.Element("seg");
            if (seg is null) tuv.Add(seg = new XElement("seg"));
            seg.Value = text.Value.Trim();
        }
    }

    /// <summary>A copy of a patch element without its file's line information.</summary>
    private static XElement Clone(XElement e) => XElement.Parse(e.ToString(SaveOptions.DisableFormatting), LoadOptions.None);

    /// <summary>"a/b[attr=value]/c": the first child of each step (a [attr=value] filter optional, the value quoted or not).</summary>
    internal static XElement? Resolve(XElement from, string path)
    {
        var at = from;
        foreach (var step in path.Split('/', StringSplitOptions.RemoveEmptyEntries))
        {
            var m = StepPattern().Match(step.Trim());
            if (!m.Success) return null;
            var name = m.Groups[1].Value;
            var filterAttr = m.Groups[2].Success ? m.Groups[2].Value : null;
            var filterValue = m.Groups[3].Success ? m.Groups[3].Value.Trim('"', '\'') : null;
            at = at.Elements(name).FirstOrDefault(e => filterAttr is null || string.Equals(e.Attribute(filterAttr)?.Value, filterValue, StringComparison.OrdinalIgnoreCase));
            if (at is null) return null;
        }
        return at;
    }

    [GeneratedRegex(@"^([A-Za-z_][\w\-.]*)(?:\[\s*@?([\w\-.:]+)\s*=\s*([^\]]*)\])?$")]
    private static partial Regex StepPattern();

    private static byte[] Serialize(Doc doc)
    {
        var settings = new XmlWriterSettings { Encoding = new UTF8Encoding(doc.Bom), Indent = false, OmitXmlDeclaration = doc.X.Declaration is null };
        using var stream = new MemoryStream();
        using (var writer = XmlWriter.Create(stream, settings)) doc.X.Save(writer);
        return stream.ToArray();
    }

    // ---------------------------------------------------------------- names and numbering

    private static HashSet<string> ItemNames(Doc items)
    {
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var (_, item, enchantment) in ClientNumbering.Items(items.Container.Elements()))
        {
            var name = item.Attribute("uniquename")!.Value;
            names.Add(enchantment is null ? name : $"{name}@{enchantment.Attribute("enchantmentlevel")?.Value}");
            if (item.Name.LocalName == "journalitem" && enchantment is null) { names.Add(name + "_EMPTY"); names.Add(name + "_FULL"); }
        }
        return names;
    }

    private static HashSet<string> SpellNames(Doc spells) =>
        new(ClientNumbering.Spells(spells.Container.Elements()).Select(s => s.Name), StringComparer.OrdinalIgnoreCase);

    private static string NamesSha(IEnumerable<string> names) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(string.Join("\n", names)))).ToLowerInvariant();

    /// <summary>The fingerprint of the first <paramref name="count"/> names in id order (the server compares it at the start, <see cref="ModState.Verify"/>).</summary>
    public static string NamesFingerprint(IEnumerable<string> namesInIdOrder, int count) => NamesSha(namesInIdOrder.Take(count));

    private static List<(int Id, string Name)> ItemIds(Doc doc) =>
        [.. ClientNumbering.Items(doc.Container.Elements()).Select(t => ((int)t.Index,
            t.Enchantment is null ? t.Item.Attribute("uniquename")!.Value : $"{t.Item.Attribute("uniquename")!.Value}@{t.Enchantment.Attribute("enchantmentlevel")?.Value ?? "?"}"))];

    private static ModNumbering CompareItems(Doc pristine, Doc modded, ModBuildResult result)
    {
        var before = ItemIds(pristine);
        var after = ItemIds(modded);
        var afterByName = after.GroupBy(a => a.Name, StringComparer.OrdinalIgnoreCase).ToDictionary(g => g.Key, g => g.First().Id, StringComparer.OrdinalIgnoreCase);
        var moved = before.Where(b => afterByName.GetValueOrDefault(b.Name) != b.Id).ToList();
        if (moved.Count > 0)
            result.Errors.Add($"items.bin: {moved.Count} official item ids would move (first {moved[0].Name} {moved[0].Id} -> {afterByName.GetValueOrDefault(moved[0].Name)}): "
                + "only append new items and keep the enchantments of the official ones");
        var known = before.Select(b => b.Name).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var added = after.Where(a => !known.Contains(a.Name)).ToDictionary(a => a.Name, a => a.Id, StringComparer.OrdinalIgnoreCase);
        var journals = modded.Container.Elements("journalitem").Count(j => j.Attribute("uniquename") is not null);
        if (added.Count > 0)
        {
            result.Notes.Add($"items.bin: new item ids {string.Join(", ", added.Select(a => $"{a.Key} {a.Value}"))}; the {journals * 2} journal "
                + $"_EMPTY/_FULL variants the client makes after the file move by +{after.Count - before.Count} on client and server alike (saves keep item names, not ids)");
        }
        return new ModNumbering(before.Count, NamesSha(before.Select(b => b.Name)), after.Count, added);
    }

    private static ModNumbering CompareSpells(Doc pristine, Doc modded, ModBuildResult result)
    {
        var before = ClientNumbering.Spells(pristine.Container.Elements()).Select(s => ((int)s.Id, s.Name)).ToList();
        var after = ClientNumbering.Spells(modded.Container.Elements()).Select(s => ((int)s.Id, s.Name)).ToList();
        var moved = before.Where((b, i) => i >= after.Count || !string.Equals(after[i].Name, b.Name, StringComparison.OrdinalIgnoreCase)).ToList();
        if (moved.Count > 0)
            result.Errors.Add($"spells.bin: {moved.Count} official spell ids would move (first {moved[0].Name} {moved[0].Item1}): only append new spells, and keep the channeling parts of the official ones");
        var known = before.Select(b => b.Name).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var added = after.Where(a => !known.Contains(a.Name)).GroupBy(a => a.Name, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => g.First().Item1, StringComparer.OrdinalIgnoreCase);
        if (added.Count > 0) result.Notes.Add($"spells.bin: new spell ids {string.Join(", ", added.Select(a => $"{a.Key} {a.Value}"))}");
        return new ModNumbering(before.Count, NamesSha(before.Select(b => b.Name)), after.Count, added);
    }

    /// <summary>The top-level elements of the official file must come first, in their order (buildings, mobs, … are numbered by it).</summary>
    private static void CheckOrder(Doc modded, Doc pristine, ModBuildResult result)
    {
        static string Id(XElement e, string key) => e.Attribute(key)?.Value ?? "<" + e.Name.LocalName + ">";
        var before = pristine.Container.Elements().Select(e => Id(e, pristine.KeyAttr)).ToList();
        var after = modded.Container.Elements().Select(e => Id(e, modded.KeyAttr)).ToList();
        for (var i = 0; i < before.Count; i++)
            if (i >= after.Count || !string.Equals(before[i], after[i], StringComparison.OrdinalIgnoreCase))
            {
                result.Errors.Add($"{modded.Name}: element {i} is {(i < after.Count ? after[i] : "gone")}, the official file has {before[i]} there: "
                    + "official elements keep their place (new ones are appended)");
                return;
            }
    }

    // ---------------------------------------------------------------- validation

    /// <summary>What an attribute (element/attribute pair) holds in the official data.</summary>
    private enum RefKind { None, Item, Spell }

    [GeneratedRegex("prefab|sprite|mesh|anim|audio|sound|vfx|icon|atlas|texture|material|model|clip", RegexOptions.IgnoreCase)]
    private static partial Regex AssetName();

    private static readonly HashSet<string> AssetElements = new(StringComparer.Ordinal) { "AudioInfo", "SocketPreset", "AssetVfxPreset", "FootStepVfxPreset" };

    private static bool Plain(string v) =>
        v.Length == 0 || v.Equals("none", StringComparison.OrdinalIgnoreCase) || v is "true" or "false"
        || double.TryParse(v, NumberStyles.Float, CultureInfo.InvariantCulture, out _);

    private static bool IsAsset(XElement e, XAttribute a) => AssetName().IsMatch(a.Name.LocalName) || (AssetElements.Contains(e.Name.LocalName) && a.Name.LocalName == "name");

    private readonly Dictionary<string, (Dictionary<string, RefKind> Kinds, Dictionary<string, HashSet<string>> Assets)> _profiles = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// The official file's profile: which element/attribute pairs name items or spells (90 % of at least 3 values are such names:
    /// craftresource/uniquename, craftspell/uniquename, applyspell/spell, dash/endeffect, craftitem/uniquename, …) and the values of
    /// every asset attribute (prefab, uisprite, AudioInfo name, …): a pack may only use assets the official data uses.
    /// </summary>
    private (Dictionary<string, RefKind> Kinds, Dictionary<string, HashSet<string>> Assets) Profile(Doc pristine, HashSet<string> items, HashSet<string> spells)
    {
        if (_profiles.TryGetValue(pristine.Name, out var cached)) return cached;
        var tally = new Dictionary<string, (int Total, int Items, int Spells)>(StringComparer.Ordinal);
        var assets = new Dictionary<string, HashSet<string>>(StringComparer.Ordinal);
        foreach (var top in pristine.Container.Elements())
            foreach (var e in top.DescendantsAndSelf())
                foreach (var a in e.Attributes())
                {
                    if (a.IsNamespaceDeclaration || (e == top && a.Name.LocalName == pristine.KeyAttr)) continue;
                    if (IsAsset(e, a))
                    {
                        if (!assets.TryGetValue(a.Name.LocalName, out var set)) assets[a.Name.LocalName] = set = new(StringComparer.OrdinalIgnoreCase);
                        set.Add(a.Value);
                    }
                    if (Plain(a.Value)) continue;
                    var pair = e.Name.LocalName + "/" + a.Name.LocalName;
                    var t = tally.GetValueOrDefault(pair);
                    tally[pair] = (t.Total + 1, t.Items + (items.Contains(a.Value) ? 1 : 0), t.Spells + (spells.Contains(a.Value) ? 1 : 0));
                }
        var kinds = tally.Where(t => t.Value.Total >= 3).ToDictionary(t => t.Key,
            t => t.Value.Items >= 0.9 * t.Value.Total ? RefKind.Item : t.Value.Spells >= 0.9 * t.Value.Total ? RefKind.Spell : RefKind.None, StringComparer.Ordinal);
        return _profiles[pristine.Name] = (kinds, assets);
    }

    /// <summary>The icon name the client uses for an item: uisprite, else the uniquename without its "Tn_" (bdi.hu 0x1805f3230).</summary>
    public static string SpriteOf(XElement item)
    {
        if (item.Attribute("uisprite")?.Value is { Length: > 0 } sprite) return sprite;
        var name = item.Attribute("uniquename")?.Value ?? "";
        return name.Length > 3 && name[0] == 'T' && name[2] == '_' ? name[3..] : name;
    }

    /// <summary>The model name the client builds for equipment: mesh, else the uniquename; + "_01" (bc9.l9 0x1805ff0f0, dvu.a).</summary>
    public static string MeshOf(XElement item) => item.Attribute("mesh")?.Value is { Length: > 0 } mesh ? mesh : item.Attribute("uniquename")?.Value ?? "";

    private HashSet<string>? _meshes;

    /// <summary>
    /// The equipment models the client can show: every model of CustomMeshes/meshes.toc (CharacterBuilder/EQUIPMENT/&lt;slot&gt;/[HUMAN_MALE/|HUMAN_FEMALE/]
    /// &lt;name&gt;_01[_HM|_HF]: capes, armour, heads and shoes sit in a gender folder) and every model an official item uses (bags have
    /// no character model at all: the client handles theirs as for the official bag).
    /// </summary>
    private HashSet<string> Meshes(Doc pristineItems) =>
        _meshes ??= new HashSet<string>(TocModels(Official).Concat(pristineItems.Container.Elements().Where(e => e.Name.LocalName is "weapon" or "equipmentitem").Select(MeshOf)),
            StringComparer.OrdinalIgnoreCase);

    private static readonly Dictionary<string, HashSet<string>> TocCache = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>The model names of the client's CustomMeshes/meshes.toc beside the GameData folder (empty without it); cached per folder.</summary>
    public static HashSet<string> TocModels(string gameDataFolder)
    {
        var toc = Path.GetFullPath(Path.Combine(gameDataFolder, "..", "CustomMeshes", "meshes.toc"));
        lock (TocCache)
        {
            if (TocCache.TryGetValue(toc, out var known)) return known;
            var set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            try
            {
                if (File.Exists(toc)) set.UnionWith(TocPattern().Matches(Encoding.Latin1.GetString(File.ReadAllBytes(toc))).Select(m => m.Groups[1].Value));
            }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
            return TocCache[toc] = set;
        }
    }

    [GeneratedRegex(@"CharacterBuilder/EQUIPMENT/[A-Za-z0-9_]+/(?:HUMAN_(?:MALE|FEMALE)/)?([A-Za-z0-9_@\-]+)_\d\d(?![A-Za-z0-9])")]
    private static partial Regex TocPattern();

    private void Validate(Doc doc, Doc pristine, HashSet<string> pristineItems, HashSet<string> pristineSpells, HashSet<string> items, HashSet<string> spells, ModBuildResult result)
    {
        var (kinds, assets) = Profile(pristine, pristineItems, pristineSpells);
        var isItems = doc.Name.Equals("items.bin", StringComparison.OrdinalIgnoreCase);
        HashSet<string>? sprites = null;
        if (isItems)
            sprites = new HashSet<string>(Open("items.bin", cache: true).Container.Elements().Where(e => e.Attribute("uniquename") is not null).Select(SpriteOf),
                StringComparer.OrdinalIgnoreCase);
        foreach (var top in doc.Touched.Where(t => t.Parent is not null))
        {
            var key = top.Attribute(doc.KeyAttr)?.Value ?? top.Name.LocalName;
            foreach (var e in top.DescendantsAndSelf())
                foreach (var a in e.Attributes())
                {
                    if (a.IsNamespaceDeclaration || (e == top && a.Name.LocalName == doc.KeyAttr)) continue;
                    var name = a.Name.LocalName;
                    if (isItems && name == "uisprite")
                    {
                        if (!sprites!.Contains(a.Value)) result.Errors.Add($"{doc.Name} {key}: uisprite=\"{a.Value}\" is no icon of an official item (e.g. MAIN_SWORD, the icon of T*_MAIN_SWORD)");
                        continue;
                    }
                    if (isItems && name == "mesh")
                    {
                        if (!Meshes(pristine).Contains(a.Value)) result.Errors.Add($"{doc.Name} {key}: mesh=\"{a.Value}\" is no model of the client (e.g. T6_MAIN_SWORD)");
                        continue;
                    }
                    if (IsAsset(e, a) && !Plain(a.Value) && !(assets.GetValueOrDefault(name)?.Contains(a.Value) ?? false))
                    {
                        result.Errors.Add($"{doc.Name} {key}: <{e.Name.LocalName} {name}=\"{a.Value}\"> is not used by any official element: only existing assets (models, icons, effects, sounds) can be used");
                        continue;
                    }
                    if (Plain(a.Value)) continue;
                    switch (kinds.GetValueOrDefault(e.Name.LocalName + "/" + name))
                    {
                        case RefKind.Item when !items.Contains(a.Value):
                            result.Errors.Add($"{doc.Name} {key}: <{e.Name.LocalName} {name}=\"{a.Value}\"> names no item");
                            break;
                        case RefKind.Spell when !spells.Contains(a.Value):
                            result.Errors.Add($"{doc.Name} {key}: <{e.Name.LocalName} {name}=\"{a.Value}\"> names no spell");
                            break;
                    }
                }
        }
        if (!isItems) return;
        foreach (var added in doc.Added)
        {
            var name = added.Attribute("uniquename")!.Value;
            if (added.Attribute("uisprite") is null && !sprites!.Contains(SpriteOf(added)))
                result.Errors.Add($"items.bin {name}: a new item has no icon of its own: give it uisprite=\"...\" of an official item (e.g. uisprite=\"MAIN_SWORD\")");
            if (added.Name.LocalName is "weapon" or "equipmentitem" && added.Attribute("mesh") is null && !Meshes(Open("items.bin", cache: true)).Contains(name))
                result.Errors.Add($"items.bin {name}: new equipment has no model of its own: give it mesh=\"...\" of an official item (e.g. mesh=\"T6_MAIN_SWORD\")");
            if (added.Attribute("tier")?.Value is { } tier && !name.StartsWith($"T{tier}_", StringComparison.Ordinal))
                result.Warnings.Add($"items.bin {name}: tier {tier} but the name does not start with T{tier}_ (the client shows the tier from the attribute)");
        }
    }

    /// <summary>Every new item and every new spell an item offers should have a name (and a description) in EN-US and RU-RU.</summary>
    private void CheckNames(Dictionary<string, Doc> docs, Doc items, Doc spells, ModBuildResult result)
    {
        var newItems = docs.GetValueOrDefault("items.bin")?.Added ?? [];
        var newSpells = docs.GetValueOrDefault("spells.bin")?.Added ?? [];
        if (newItems.Count == 0 && newSpells.Count == 0) return;
        var offered = items.Container.Descendants("craftspell").Select(c => c.Attribute("uniquename")?.Value).OfType<string>().ToHashSet(StringComparer.OrdinalIgnoreCase);
        var wanted = new List<(string Key, string What)>();
        foreach (var item in newItems)
        {
            var name = item.Attribute("uniquename")!.Value;
            wanted.Add(($"@ITEMS_{name}", $"the name of item {name}"));
            wanted.Add(($"@ITEMS_{name}_DESC", $"the description of item {name}"));
        }
        foreach (var spell in newSpells.Where(s => offered.Contains(s.Attribute("uniquename")!.Value)))
        {
            var name = spell.Attribute("uniquename")!.Value;
            var nameKey = spell.Attribute("namelocatag")?.Value ?? $"@SPELLS_{name}";
            wanted.Add((nameKey, $"the name of spell {name}"));
            wanted.Add((spell.Attribute("descriptionlocatag")?.Value ?? $"{nameKey}_DESC", $"the description of spell {name}"));
        }
        var loc = docs.GetValueOrDefault("localization.bin") ?? Open("localization.bin", cache: true);
        foreach (var (key, what) in wanted)
        {
            var tu = loc.ByKey.GetValueOrDefault(key);
            foreach (var lang in new[] { "EN-US", "RU-RU" })
                if (tu?.Elements("tuv").Any(t => string.Equals((string?)t.Attribute(XNamespace.Xml + "lang"), lang, StringComparison.OrdinalIgnoreCase)) != true)
                    result.Warnings.Add($"localization: {what} has no {lang} text ({key}); the client shows the key instead");
        }
    }
}
