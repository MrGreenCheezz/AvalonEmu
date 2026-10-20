using System.IO.Compression;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using System.Xml.Linq;

namespace AlbionLocal.Server.Web.Studio;

/// <summary>
/// The studio's files inside one pack (studio SPEC §3): the drafts mods/&lt;pack&gt;/studio/&lt;kind&gt;/&lt;ID&gt;.json (the source of
/// truth), their history studio/.history/&lt;kind&gt;/&lt;ID&gt;/&lt;stamp&gt;.json (the last <see cref="KeepVersions"/>), and
/// studio/generated.json: what the last Generate wrote into the patch files (so a deleted draft's elements are taken out again).
/// Only files below the pack folder are written, each as a temp file moved over the old one.
/// </summary>
public sealed partial class StudioStore(PackFiles pack)
{
    public const int KeepVersions = 20;
    public const int MaxDraft = 512 * 1024;
    public const int MaxDrafts = 2000;

    [GeneratedRegex("^[a-z][a-z0-9-]{0,19}$")]
    private static partial Regex KindName();

    public PackFiles Pack { get; } = pack;
    public string Root => Path.Combine(Pack.Dir, "studio");

    private string DraftPath(string kind, string id) => Path.Combine(Root, kind, id + ".json");

    private static void Check(string kind, string id)
    {
        if (!KindName().IsMatch(kind) || StudioKinds.Get(kind) is null) throw new InvalidDataException($"no such kind '{kind}'");
        if (!PackFiles.NewName().IsMatch(id)) throw new InvalidDataException($"the id '{id}': 3 to 80 of A-Z, 0-9 and _");
    }

    internal static void WriteText(string path, string text)
    {
        var bytes = new UTF8Encoding(false).GetBytes(text);
        if (bytes.Length > PackFiles.MaxFile) throw new InvalidDataException($"{Path.GetFileName(path)} would be larger than {PackFiles.MaxFile / 1024 / 1024} MB");
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var temp = path + ".studio-tmp";
        File.WriteAllBytes(temp, bytes);
        File.Move(temp, path, overwrite: true);
    }

    private static JsonObject? ReadJson(string path)
    {
        if (!File.Exists(path) || new FileInfo(path).Length > MaxDraft) return null;
        try { return JsonNode.Parse(File.ReadAllText(path), null, J.Read) as JsonObject; }
        catch (JsonException) { return null; }
    }

    /// <summary>Every draft of the pack: (kind, id) → draft, plus the files that do not parse.</summary>
    public (Dictionary<(string Kind, string Id), JsonObject> Drafts, List<string> Broken) All()
    {
        var drafts = new Dictionary<(string, string), JsonObject>();
        var broken = new List<string>();
        foreach (var kind in StudioKinds.All.Select(k => k.Kind))
        {
            var dir = Path.Combine(Root, kind);
            if (!Directory.Exists(dir)) continue;
            foreach (var file in Directory.EnumerateFiles(dir, "*.json").Take(MaxDrafts))
            {
                var id = Path.GetFileNameWithoutExtension(file);
                if (!PackFiles.NewName().IsMatch(id)) continue;
                if (ReadJson(file) is { } d) { d["kind"] = kind; d["id"] = id; drafts[(kind, id)] = d; }
                else broken.Add($"studio/{kind}/{id}.json");
            }
        }
        return (drafts, broken);
    }

    public JsonObject? Get(string kind, string id)
    {
        Check(kind, id);
        var d = ReadJson(DraftPath(kind, id));
        if (d is not null) { d["kind"] = kind; d["id"] = id; }
        return d;
    }

    public bool Exists(string kind, string id) => File.Exists(DraftPath(kind, id));

    public DateTime? Updated(string kind, string id) => File.Exists(DraftPath(kind, id)) ? File.GetLastWriteTimeUtc(DraftPath(kind, id)) : null;

    /// <summary>Writes the draft; the previous version goes into the history first.</summary>
    public void Save(JsonObject draft)
    {
        var kind = J.Str(draft["kind"]);
        var id = J.Str(draft["id"]);
        Check(kind, id);
        var path = DraftPath(kind, id);
        if (!File.Exists(path) && Directory.Exists(Path.Combine(Root, kind)) && Directory.GetFiles(Path.Combine(Root, kind), "*.json").Length >= MaxDrafts)
            throw new InvalidDataException($"at most {MaxDrafts} drafts of a kind");
        Snapshot(kind, id);
        draft["schema"] ??= 1;
        draft["updated"] = DateTime.UtcNow.ToString("yyyy-MM-ddTHH:mm:ssZ");
        var text = draft.ToJsonString(J.Indented);
        if (Encoding.UTF8.GetByteCount(text) > MaxDraft) throw new InvalidDataException($"the draft is larger than {MaxDraft / 1024} KB");
        WriteText(path, text + "\n");
    }

    private string HistoryDir(string kind, string id) => Path.Combine(Root, ".history", kind, id);

    private void Snapshot(string kind, string id)
    {
        var path = DraftPath(kind, id);
        if (!File.Exists(path)) return;
        var dir = HistoryDir(kind, id);
        Directory.CreateDirectory(dir);
        var stamp = DateTime.UtcNow.ToString("yyyyMMdd-HHmmss-fff");
        File.Copy(path, Path.Combine(dir, stamp + ".json"), overwrite: true);
        foreach (var old in Directory.GetFiles(dir, "*.json").Order(StringComparer.Ordinal).SkipLast(KeepVersions)) File.Delete(old);
    }

    public List<(string Stamp, long Size)> History(string kind, string id)
    {
        Check(kind, id);
        var dir = HistoryDir(kind, id);
        return !Directory.Exists(dir) ? [] :
            [.. Directory.GetFiles(dir, "*.json").Order(StringComparer.Ordinal).Reverse().Select(f => (Path.GetFileNameWithoutExtension(f), new FileInfo(f).Length))];
    }

    public JsonObject? Version(string kind, string id, string stamp)
    {
        Check(kind, id);
        if (!Regex.IsMatch(stamp, "^[0-9]{8}-[0-9]{6}-[0-9]{3}$")) return null;
        return ReadJson(Path.Combine(HistoryDir(kind, id), stamp + ".json"));
    }

    /// <summary>Deletes the draft (its last version stays in the history).</summary>
    public bool Delete(string kind, string id)
    {
        Check(kind, id);
        var path = DraftPath(kind, id);
        if (!File.Exists(path)) return false;
        Snapshot(kind, id);
        File.Delete(path);
        return true;
    }

    /// <summary>Moves the draft and its history to <paramref name="to"/> (its id changes; references elsewhere are the caller's).</summary>
    public void Rename(string kind, string id, string to, JsonObject draft)
    {
        Check(kind, id);
        Check(kind, to);
        if (Exists(kind, to)) throw new InvalidDataException($"a {kind} draft {to} exists already");
        draft["id"] = to;
        Save(draft);
        File.Delete(DraftPath(kind, id));
        var from = HistoryDir(kind, id);
        if (Directory.Exists(from) && !Directory.Exists(HistoryDir(kind, to))) Directory.Move(from, HistoryDir(kind, to));
    }

    // ---------------------------------------------------------------- generate

    private string ManifestPath => Path.Combine(Root, "generated.json");

    /// <summary>The server-side data files Generate writes (studio phase 3) and their array's key.</summary>
    public static readonly (string File, string Root)[] ServerFiles = [("server/mobs.json", "Mobs"), ("server/loot.json", "Lists")];

    /// <summary>The names the last Generate wrote (file → names), and when.</summary>
    public (Dictionary<string, List<string>> Names, DateTime? At) Manifest()
    {
        var names = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
        DateTime? at = null;
        if (ReadJson(ManifestPath) is { } m)
        {
            foreach (var (file, list) in J.Obj(m["files"])) names[file] = J.StringList(list);
            if (DateTime.TryParse(J.Str(m["at"]), null, System.Globalization.DateTimeStyles.AdjustToUniversal | System.Globalization.DateTimeStyles.AssumeUniversal, out var t)) at = t;
        }
        return (names, at);
    }

    /// <summary>True when a draft changed (or was deleted) after the last Generate.</summary>
    public bool Dirty()
    {
        var (names, at) = Manifest();
        var (drafts, _) = All();
        if (at is null) return drafts.Count > 0;
        if (drafts.Keys.Any(k => (Updated(k.Kind, k.Id) ?? DateTime.MinValue) > at.Value)) return true;
        var seen = (names.GetValueOrDefault("drafts") ?? []).ToHashSet(StringComparer.Ordinal);
        return !seen.SetEquals(drafts.Keys.Select(k => $"{k.Kind}/{k.Id}"));
    }

    /// <summary>
    /// Compiles every draft into the pack's patch files: the elements the previous Generate made and no draft makes any more are
    /// taken out; other content of the files (hand-written, or the old editor's) stays. Drafts with a blocker are skipped (reported).
    /// </summary>
    public (List<string> Written, List<StudioDiag> Problems) Generate(StudioContext ctx)
    {
        var problems = new List<StudioDiag>();
        var (drafts, broken) = All();
        problems.AddRange(broken.Select(b => new StudioDiag(StudioDiag.Blocker, "BROKEN_DRAFT", $"{b} is no JSON object: fix or delete it")));
        var compiled = new List<StudioCompiled>();
        var ids = new List<string>();
        foreach (var ((kind, id), draft) in drafts.OrderBy(d => d.Key.Kind, StringComparer.Ordinal).ThenBy(d => d.Key.Id, StringComparer.Ordinal))
        {
            var k = StudioKinds.Get(kind)!;
            var diags = k.Validate(ctx, draft);
            problems.AddRange(diags);
            if (diags.Any(x => x.Severity == StudioDiag.Blocker)) continue;
            try
            {
                var c = k.Compile(ctx, draft);
                problems.AddRange(c.Problems);
                compiled.Add(c);
                ids.Add(id);
            }
            catch (Exception e) when (e is InvalidDataException or FormatException or InvalidOperationException or ArgumentException)
            {
                problems.Add(new StudioDiag(StudioDiag.Blocker, "COMPILE", e.Message, "", null, kind, id));
            }
        }
        var (old, _) = Manifest();
        var newNames = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
        foreach (var c in compiled)
            foreach (var (file, els) in c.Added)
            {
                if (!newNames.TryGetValue(file, out var l)) newNames[file] = l = [];
                l.AddRange(els.Select(e => e.Attribute("uniquename")?.Value).OfType<string>());
            }
        var written = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        // The added elements, file by file.
        foreach (var file in old.Keys.Concat(newNames.Keys).Where(f => PackFiles.PatchFiles.Contains(f) && f != "localization.xml" && f != "buildings.xml").Distinct(StringComparer.OrdinalIgnoreCase).ToList())
        {
            var doc = Pack.Patch(file);
            var keep = newNames.GetValueOrDefault(file) ?? [];
            foreach (var gone in (old.GetValueOrDefault(file) ?? []).Where(n => !keep.Contains(n, StringComparer.OrdinalIgnoreCase))) PackFiles.RemoveAdded(doc, gone);
            foreach (var c in compiled)
                foreach (var e in c.Added.GetValueOrDefault(file) ?? []) PackFiles.PutAdded(doc, e);
            Pack.Save(file, doc);
            written.Add(file);
        }
        // Texts: the old entries of every generated name out, the new ones in.
        var loc = Pack.Patch("localization.xml");
        var oldTuids = (old.GetValueOrDefault("localization.xml") ?? []).ToList();
        foreach (var t in oldTuids) PackFiles.SetLoc(loc, t, "", "");
        foreach (var c in compiled)
            foreach (var (tuid, en, ru) in c.Loc) PackFiles.SetLoc(loc, tuid, en, ru);
        newNames["localization.xml"] = [.. compiled.SelectMany(c => c.Loc).Where(l => l.En.Length > 0 || l.Ru.Length > 0).Select(l => l.Tuid)];
        if (oldTuids.Count > 0 || newNames["localization.xml"].Count > 0) { Pack.Save("localization.xml", loc); written.Add("localization.xml"); }
        // Stations.
        var stationItems = compiled.SelectMany(c => c.Stations).ToList();
        var oldStationItems = old.GetValueOrDefault("buildings.xml") ?? [];
        if (stationItems.Count > 0 || oldStationItems.Count > 0)
        {
            var buildings = Pack.Patch("buildings.xml");
            foreach (var item in oldStationItems) PackFiles.SetStations(buildings, item, [], _ => new XElement("craftitem"));
            foreach (var (item, list) in stationItems)
            {
                var models = list.ToDictionary(x => x.Station, x => x.CraftItem, StringComparer.OrdinalIgnoreCase);
                PackFiles.SetStations(buildings, item, list.Select(x => x.Station), st => models[st]);
            }
            Pack.Save("buildings.xml", buildings);
            written.Add("buildings.xml");
            newNames["buildings.xml"] = [.. stationItems.Where(s => s.Value.Count > 0).Select(s => s.Key)];
        }
        // Drops.
        var dropItems = compiled.SelectMany(c => c.Drops).ToList();
        var oldDropItems = old.GetValueOrDefault("server/drops.json") ?? [];
        foreach (var item in oldDropItems.Where(i => !dropItems.Any(d => string.Equals(d.Key, i, StringComparison.OrdinalIgnoreCase))))
            if (Pack.Drops().Any(d => string.Equals(d.Item, item, StringComparison.OrdinalIgnoreCase))) { Pack.SetDrops(item, []); written.Add("server/drops.json"); }
        foreach (var (item, rows) in dropItems)
            if (rows.Count > 0 || Pack.Drops().Any(d => string.Equals(d.Item, item, StringComparison.OrdinalIgnoreCase))) { Pack.SetDrops(item, rows); written.Add("server/drops.json"); }
        newNames["server/drops.json"] = [.. dropItems.Where(d => d.Value.Count > 0).Select(d => d.Key)];
        // The extra action bars (server/extraspells.json, an item → spells map): the drafts' items replaced, hand-written entries kept.
        {
            const string file = "server/extraspells.json";
            var made = compiled.SelectMany(c => c.ExtraSpells).Where(e => e.Value.Count > 0).ToList();
            var before = old.GetValueOrDefault(file) ?? [];
            var path = Path.Combine(Pack.Dir, "server", "extraspells.json");
            if (made.Count > 0 || before.Count > 0)
            {
                var root = File.Exists(path) ? ReadJson(path) : new JsonObject();
                if (root is null) problems.Add(new StudioDiag(StudioDiag.Blocker, "BAD_FILE", $"{file} does not parse (or is too large): fix it by hand, the extra abilities were not written", "", null, "item", ""));
                else
                {
                    var items = root["Items"] as JsonObject ?? root; // the flat map, or {"Items": {…}}
                    foreach (var gone in before) items.Remove(gone);
                    foreach (var (item, spells) in made) items[item] = J.Strings(spells);
                    root["note"] ??= "extra action bars (studio + hand-written): item or pattern -> activespells; read at the start / .extrabar reload for the packs in Mods:Packs";
                    WriteText(path, root.ToJsonString(J.Indented) + "\n");
                    written.Add(file);
                    newNames[file] = [.. made.Select(m => m.Key)];
                }
            }
        }
        // Server-side pack data (mob variants, loot lists): the generated entries replaced, hand-written ones kept.
        foreach (var (file, rootKey) in ServerFiles)
        {
            var made = compiled.SelectMany(c => c.Server.GetValueOrDefault(file) ?? []).ToList();
            var before = old.GetValueOrDefault(file) ?? [];
            if (made.Count == 0 && before.Count == 0) continue;
            var path = Path.Combine(Pack.Dir, file.Replace('/', Path.DirectorySeparatorChar));
            var root = File.Exists(path) ? ReadJson(path) ?? [] : [];
            var madeIds = made.Select(m => J.Str(m["Id"])).ToHashSet(StringComparer.OrdinalIgnoreCase);
            var kept = J.Arr(root[rootKey]).OfType<JsonObject>().Where(e => !madeIds.Contains(J.Str(e["Id"])) && !before.Contains(J.Str(e["Id"]), StringComparer.OrdinalIgnoreCase))
                .Select(e => (JsonNode)e.DeepClone()).ToList();
            root[rootKey] = new JsonArray([.. kept, .. made.Select(m => (JsonNode)m.DeepClone())]);
            root["note"] ??= "server-side pack data (studio): read at the start for the packs in Mods:Packs";
            WriteText(path, root.ToJsonString(J.Indented) + "\n");
            written.Add(file);
            newNames[file] = [.. madeIds];
        }
        // editor.json: which official element each new one was cloned from (the old editor's preview reads it).
        foreach (var gone in old.Where(o => o.Key is "items.xml" or "spells.xml").SelectMany(o => o.Value)) Pack.SetTemplate(gone, null);
        foreach (var (name, template) in compiled.SelectMany(c => c.Templates)) Pack.SetTemplate(name, template);
        newNames["drafts"] = [.. drafts.Keys.Select(k => $"{k.Kind}/{k.Id}")];
        var files = new JsonObject();
        foreach (var (file, names) in newNames) files[file] = J.Strings(names);
        WriteText(ManifestPath, new JsonObject
        {
            ["note"] = "written by the studio's Generate: the names it put into the patch files (taken out again when their draft goes)",
            ["at"] = DateTime.UtcNow.ToString("o"), ["files"] = files,
        }.ToJsonString(J.Indented));
        return ([.. written.Order(StringComparer.Ordinal)], problems);
    }

    // ---------------------------------------------------------------- export / import

    /// <summary>The files a pack export carries: pack.json, the patch files, editor.json, server/drops.json and the drafts (not the history).</summary>
    public IEnumerable<string> ExportFiles()
    {
        foreach (var f in new[] { "pack.json", "editor.json" }.Concat(PackFiles.PatchFiles))
            if (File.Exists(Path.Combine(Pack.Dir, f))) yield return f;
        foreach (var f in ServerFiles.Select(s => s.File).Prepend("server/drops.json").Append("server/extraspells.json"))
            if (File.Exists(Path.Combine(Pack.Dir, f.Replace('/', Path.DirectorySeparatorChar)))) yield return f;
        foreach (var kind in StudioKinds.All.Select(k => k.Kind))
            if (Directory.Exists(Path.Combine(Root, kind)))
                foreach (var f in Directory.GetFiles(Path.Combine(Root, kind), "*.json").Order(StringComparer.Ordinal))
                    if (PackFiles.NewName().IsMatch(Path.GetFileNameWithoutExtension(f))) yield return $"studio/{kind}/{Path.GetFileName(f)}";
    }

    public byte[] ExportZip()
    {
        using var ms = new MemoryStream();
        using (var zip = new ZipArchive(ms, ZipArchiveMode.Create, true))
            foreach (var rel in ExportFiles())
            {
                var entry = zip.CreateEntry(rel, CompressionLevel.Optimal);
                using var w = entry.Open();
                w.Write(File.ReadAllBytes(Path.Combine(Pack.Dir, rel.Replace('/', Path.DirectorySeparatorChar))));
            }
        return ms.ToArray();
    }

    public JsonObject ExportJson()
    {
        var info = Pack.Info();
        var (drafts, _) = All();
        return new JsonObject
        {
            ["format"] = "albion-studio-pack", ["version"] = 1, ["exported"] = DateTime.UtcNow.ToString("o"),
            ["pack"] = new JsonObject
            {
                ["Name"] = Pack.Name, ["Version"] = info.GetValueOrDefault("Version"), ["Description"] = info.GetValueOrDefault("Description"), ["Client"] = info.GetValueOrDefault("Client"),
            },
            ["entities"] = new JsonArray([.. drafts.OrderBy(d => d.Key.Kind).ThenBy(d => d.Key.Id).Select(d => (JsonNode)d.Value.DeepClone())]),
        };
    }

    [GeneratedRegex(@"^(pack\.json|editor\.json|items\.xml|spells\.xml|buildings\.xml|localization\.xml|server/drops\.json|server/mobs\.json|server/loot\.json|server/extraspells\.json|studio/[a-z][a-z0-9-]{0,19}/[A-Z0-9][A-Z0-9_]{2,79}\.json)$")]
    private static partial Regex ImportName();

    /// <summary>
    /// A new pack <paramref name="name"/> from an exported zip: only the known file names (no paths of its own, no ..), each at most
    /// 2 MB, at most 4000 entries and 16 MB unpacked; pack.json's Name becomes the new name. An error text, or null.
    /// </summary>
    public static string? ImportZip(string modsDir, string name, byte[] zipBytes, string? client)
    {
        if (!PackFiles.PackName().IsMatch(name)) return "the pack name: 1 to 40 of a-z, 0-9 and -";
        if (Directory.Exists(Path.Combine(modsDir, name))) return $"mods/{name} exists already";
        var files = new Dictionary<string, byte[]>(StringComparer.Ordinal);
        try
        {
            using var zip = new ZipArchive(new MemoryStream(zipBytes), ZipArchiveMode.Read);
            if (zip.Entries.Count > 4000) return "too many files in the zip";
            long total = 0;
            foreach (var e in zip.Entries)
            {
                if (e.FullName.EndsWith('/')) continue;
                var rel = e.FullName.Replace('\\', '/');
                if (!ImportName().IsMatch(rel)) return $"{e.FullName}: not a file a pack export has";
                if (e.Length > PackFiles.MaxFile || (total += e.Length) > 16 * 1024 * 1024) return $"{e.FullName}: too large";
                using var s = e.Open();
                using var ms = new MemoryStream();
                s.CopyTo(ms);
                if (ms.Length > PackFiles.MaxFile) return $"{e.FullName}: too large";
                files[rel] = ms.ToArray();
            }
        }
        catch (InvalidDataException e) { return "not a zip file: " + e.Message; }
        if (!files.ContainsKey("pack.json")) return "no pack.json in the zip";
        foreach (var (rel, bytes) in files.Where(f => f.Key.EndsWith(".xml", StringComparison.Ordinal)))
            try { System.Xml.Linq.XDocument.Load(System.Xml.XmlReader.Create(new MemoryStream(bytes), new System.Xml.XmlReaderSettings { DtdProcessing = System.Xml.DtdProcessing.Prohibit })); }
            catch (System.Xml.XmlException e) { return $"{rel}: {e.Message}"; }
        JsonObject info;
        try { info = JsonNode.Parse(files["pack.json"], null, J.Read) as JsonObject ?? throw new JsonException("not an object"); }
        catch (JsonException e) { return "pack.json: " + e.Message; }
        var error = PackFiles.Create(modsDir, name, J.Str(info["Version"], "1.0.0") is { Length: > 0 } v && PackFiles.Version().IsMatch(v) ? v : "1.0.0",
            WebPanel.Clean(J.Str(info["Description"]), 300), client);
        if (error is not null) return error;
        var dir = Path.Combine(Path.GetFullPath(modsDir), name);
        foreach (var (rel, bytes) in files.Where(f => f.Key != "pack.json"))
        {
            var path = Path.GetFullPath(Path.Combine(dir, rel.Replace('/', Path.DirectorySeparatorChar)));
            if (!path.StartsWith(dir + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)) continue;
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllBytes(path, bytes);
        }
        return null;
    }

    /// <summary>A new pack from a JSON export (albion-studio-pack): pack.json and the drafts; the patch files come from Generate. An error text, or null.</summary>
    public static string? ImportJson(string modsDir, string name, JsonObject bundle, string? client)
    {
        if (J.Str(bundle["format"]) != "albion-studio-pack") return "not a studio pack export (format albion-studio-pack)";
        var entities = J.Arr(bundle["entities"]).OfType<JsonObject>().ToList();
        if (entities.Count > MaxDrafts) return "too many entities";
        foreach (var e in entities)
            if (StudioKinds.Get(J.Str(e["kind"])) is null || !PackFiles.NewName().IsMatch(J.Str(e["id"]))) return $"entity {J.Str(e["kind"])}/{J.Str(e["id"])}: bad kind or id";
        var info = J.Obj(bundle["pack"]);
        var error = PackFiles.Create(modsDir, name, J.Str(info["Version"], "1.0.0") is { Length: > 0 } v && PackFiles.Version().IsMatch(v) ? v : "1.0.0",
            WebPanel.Clean(J.Str(info["Description"]), 300), client);
        if (error is not null) return error;
        var store = new StudioStore(PackFiles.Open(modsDir, name)!);
        foreach (var e in entities) store.Save((JsonObject)e.DeepClone());
        return null;
    }
}
