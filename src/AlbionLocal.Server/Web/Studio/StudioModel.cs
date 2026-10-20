using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Xml.Linq;

namespace AlbionLocal.Server.Web.Studio;

/// <summary>
/// One problem of a draft or a build (studio SPEC §5): blocker (Generate/Install refused), warning or info, with the JSON pointer of
/// the draft field it is about ("/recipe/resources/0/item"), so the editor can show it inline next to the field.
/// </summary>
public sealed record StudioDiag(string Severity, string Code, string Message, string Path = "", string? Hint = null, string? Kind = null, string? Id = null)
{
    public const string Blocker = "blocker", Warning = "warning", Info = "info";

    /// <summary>The field's own label (RU, EN) when the producer knows it (a recipe's field); else <see cref="StudioDiagText"/> names it by the path.</summary>
    public (string Ru, string En)? Label { get; init; }

    /// <summary>
    /// The English detail stays in "message"; "text" and "advice" are the catalogue's RU/EN sentences (what is wrong, how to fix it),
    /// "field" the field's name, "fix" a one-click change {label, path, value} (value null: back to the template's).
    /// </summary>
    public JsonObject ToJson()
    {
        var o = new JsonObject
        {
            ["severity"] = Severity, ["code"] = Code, ["message"] = Message, ["path"] = Path, ["hint"] = Hint,
            ["entity"] = Kind is null && Id is null ? null : new JsonObject { ["kind"] = Kind, ["id"] = Id },
        };
        var d = StudioDiagText.Describe(this);
        o["text"] = new JsonObject { ["ru"] = d.Ru, ["en"] = d.En };
        if (d.AdviceRu is not null) o["advice"] = new JsonObject { ["ru"] = d.AdviceRu, ["en"] = d.AdviceEn };
        if (d.Field is { } f) o["field"] = new JsonObject { ["ru"] = f.Ru, ["en"] = f.En };
        if (d.Fix is { } fix) o["fix"] = new JsonObject { ["label"] = new JsonObject { ["ru"] = fix.Ru, ["en"] = fix.En }, ["path"] = fix.Path, ["value"] = null };
        return o;
    }

    /// <summary>A JSON-pointer segment: ~ → ~0, / → ~1.</summary>
    public static string Seg(string s) => s.Replace("~", "~0").Replace("/", "~1");
}

/// <summary>A reference a draft makes to another entity (kind: item, spell, station, mob, chest, lootlist), at a JSON pointer.</summary>
public sealed record StudioRef(string Kind, string Name, string Path);

/// <summary>What one draft compiles to: the elements it adds to the pack's patch files and the server-side extras.</summary>
public sealed class StudioCompiled
{
    /// <summary>New top-level elements by patch file ("items.xml" → the new &lt;weapon&gt;, "spells.xml" → the spell and its cloned sub-spells).</summary>
    public Dictionary<string, List<XElement>> Added { get; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Localization entries (tuid → EN, RU).</summary>
    public List<(string Tuid, string En, string Ru)> Loc { get; } = [];

    /// <summary>item → the stations that craft it, each with its craftitem element; null = the draft does not say (no stations written).</summary>
    public Dictionary<string, List<(string Station, XElement CraftItem)>> Stations { get; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>item → its drops in server/drops.json.</summary>
    public Dictionary<string, List<DropRow>> Drops { get; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>item → its extra spells for the extra action bars (server/extraspells.json; analysis/work/stageN/extrabars/SPEC.md §2).</summary>
    public Dictionary<string, List<string>> ExtraSpells { get; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>new name → the official element it was cloned from (editor.json, the old editor's preview).</summary>
    public Dictionary<string, string> Templates { get; } = new(StringComparer.OrdinalIgnoreCase);

    public List<StudioDiag> Problems { get; } = [];

    /// <summary>Server-side pack data (studio phase 3): "server/mobs.json" → mob variants, "server/loot.json" → loot lists; each entry has an "Id".</summary>
    public Dictionary<string, List<JsonObject>> Server { get; } = new(StringComparer.OrdinalIgnoreCase);

    public void AddServer(string file, JsonObject entry)
    {
        if (!Server.TryGetValue(file, out var list)) Server[file] = list = [];
        list.Add(entry);
    }

    public void Add(string file, XElement e)
    {
        if (!Added.TryGetValue(file, out var list)) Added[file] = list = [];
        list.Add(e);
    }
}

/// <summary>
/// What a kind of draft can do (studio SPEC §6, the backend extension point): make a draft from a catalogue entry, check it,
/// compile it into patch elements, describe it for the preview's tooltip, and list or rename its references. Registered in
/// <see cref="StudioKinds"/>; a later "recipe" editor adds fields to a kind's draft and handles them here.
/// </summary>
public interface IStudioKind
{
    /// <summary>"item", "spell": the folder under mods/&lt;pack&gt;/studio/ and the "kind" of its drafts.</summary>
    string Kind { get; }

    /// <summary>The patch file its main element goes into (items.xml).</summary>
    string PatchFile { get; }

    /// <summary>A new draft: a copy of the catalogue entry <paramref name="template"/> named <paramref name="id"/>, its texts and links pre-filled.</summary>
    JsonObject NewFrom(StudioContext ctx, string template, string id);

    List<StudioDiag> Validate(StudioContext ctx, JsonObject draft);

    /// <summary>The draft as patch elements; problems that stop the compile are in <see cref="StudioCompiled.Problems"/>.</summary>
    StudioCompiled Compile(StudioContext ctx, JsonObject draft);

    /// <summary>The right pane's tooltip: name, tier, item power, main stats, spells with their descriptions.</summary>
    JsonObject Tooltip(StudioContext ctx, JsonObject draft, StudioCompiled compiled);

    /// <summary>The official (or pack) element the draft starts from, or null.</summary>
    XElement? TemplateElement(StudioContext ctx, JsonObject draft);

    IEnumerable<StudioRef> References(JsonObject draft);

    /// <summary>Rewrites references to <paramref name="kind"/> <paramref name="from"/> into <paramref name="to"/>; how many changed.</summary>
    int RenameRefs(JsonObject draft, string kind, string from, string to);
}

/// <summary>The registered kinds (item, spell). Plugins or recipe editors add theirs with <see cref="Register"/>.</summary>
public static class StudioKinds
{
    private static readonly Dictionary<string, IStudioKind> ByName = new(StringComparer.Ordinal)
    {
        ["item"] = new ItemKind(),
        ["spell"] = new SpellKind(),
        ["mob"] = new MobKind(),
        ["lootlist"] = new LootListKind(),
    };

    public static IEnumerable<IStudioKind> All => ByName.Values;

    public static IStudioKind? Get(string? kind) => kind is null ? null : ByName.GetValueOrDefault(kind);

    public static void Register(IStudioKind kind)
    {
        lock (ByName) ByName[kind.Kind] = kind;
    }
}

/// <summary>What a kind sees while it works: the catalogue, the packs' new elements, the drafts of the pack, the icons.</summary>
public sealed class StudioContext
{
    public required ModCatalog Catalog { get; init; }
    public required IconCache Icons { get; init; }
    public required string Pack { get; init; }

    /// <summary>An item by name: official, or added by a pack's patch files.</summary>
    public required Func<string, CatItem?> FindItem { get; init; }

    public required Func<string, CatSpell?> FindSpell { get; init; }

    /// <summary>EN/RU of a tuid: a pack's localization.xml first, else the official text.</summary>
    public required Func<string, (string? En, string? Ru)> Names { get; init; }

    /// <summary>The drafts of this pack by kind and id (they count as existing for references).</summary>
    public required Dictionary<(string Kind, string Id), JsonObject> Drafts { get; init; }

    public bool DraftExists(string kind, string id) => Drafts.ContainsKey((kind, id.ToUpperInvariant())) || Drafts.Keys.Any(k => k.Kind == kind && string.Equals(k.Id, id, StringComparison.OrdinalIgnoreCase));

    public bool ItemExists(string name) => FindItem(name) is not null || DraftExists("item", name);

    public bool SpellExists(string name) => FindSpell(name) is not null || DraftExists("spell", name);

    /// <summary>The compiled element of a draft of this pack (cached per context), or null.</summary>
    public XElement? DraftElement(string kind, string id)
    {
        var key = (kind, id.ToUpperInvariant());
        if (_compiled.TryGetValue(key, out var done)) return done;
        _compiled[key] = null; // a cycle stops here
        var draft = Drafts.FirstOrDefault(d => d.Key.Kind == kind && string.Equals(d.Key.Id, id, StringComparison.OrdinalIgnoreCase)).Value;
        if (draft is null || StudioKinds.Get(kind) is not { } k) return null;
        try
        {
            var c = k.Compile(this, draft);
            return _compiled[key] = c.Added.GetValueOrDefault(k.PatchFile)?.FirstOrDefault();
        }
        catch (Exception e) when (e is InvalidDataException or FormatException or InvalidOperationException) { return null; }
    }

    private readonly Dictionary<(string, string), XElement?> _compiled = [];
}

/// <summary>Small JSON helpers for drafts (string values, invariant numbers).</summary>
public static class J
{
    public static readonly JsonSerializerOptions Indented = new() { WriteIndented = true, Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping };
    public static readonly JsonDocumentOptions Read = new() { CommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true, MaxDepth = 64 };

    public static string Str(JsonNode? n, string fallback = "")
    {
        if (n is not JsonValue v) return fallback;
        if (v.TryGetValue<string>(out var s)) return s;
        if (Number(v) is { } d) return d.ToString("0.######", CultureInfo.InvariantCulture);
        if (v.TryGetValue<bool>(out var b)) return b ? "true" : "false";
        return fallback;
    }

    /// <summary>A number value of any CLR type (a parsed draft holds JsonElements, a built one ints and doubles).</summary>
    private static double? Number(JsonValue v)
    {
        if (v.TryGetValue<double>(out var d)) return d;
        if (v.TryGetValue<int>(out var i)) return i;
        if (v.TryGetValue<long>(out var l)) return l;
        if (v.TryGetValue<float>(out var f)) return f;
        if (v.TryGetValue<decimal>(out var m)) return (double)m;
        return null;
    }

    public static double? Num(JsonNode? n)
    {
        if (n is not JsonValue v) return null;
        if (Number(v) is { } d) return d;
        if (v.TryGetValue<string>(out var s) && double.TryParse(s.Trim().TrimEnd('%'), NumberStyles.Float, CultureInfo.InvariantCulture, out d)) return d;
        return null;
    }

    public static JsonObject Obj(JsonNode? n) => n as JsonObject ?? [];

    public static JsonArray Arr(JsonNode? n) => n as JsonArray ?? [];

    public static (string En, string Ru) Loc(JsonNode? n) => (Str(n?["en"]).Trim(), Str(n?["ru"]).Trim());

    public static JsonObject LocNode(string? en, string? ru) => new() { ["en"] = en ?? "", ["ru"] = ru ?? "" };

    public static JsonArray Strings(IEnumerable<string> values) => new([.. values.Select(v => (JsonNode?)JsonValue.Create(v))]);

    public static List<string> StringList(JsonNode? n) => [.. Arr(n).Select(x => Str(x).Trim()).Where(x => x.Length > 0)];

    public static string Fmt(double v) => v.ToString("0.###", CultureInfo.InvariantCulture);
}
