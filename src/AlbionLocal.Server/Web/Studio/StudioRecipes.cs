using System.Globalization;
using System.Runtime.CompilerServices;
using System.Text.Json.Nodes;
using System.Xml.Linq;

namespace AlbionLocal.Server.Web.Studio;

/// <summary>One choice of a select field (its value and RU/EN label).</summary>
public sealed record RecipeOption(string Value, string En, string Ru);

/// <summary>
/// A declarative field of a recipe (studio SPEC phase 3): what the form shows (label, hint RU/EN, type, default, min/max,
/// required, options), when it shows (<see cref="Show"/> "key=a|b"), and where its official range comes from (<see cref="Range"/>,
/// the value helper's min/median/max among official items or spells of the same kind).
/// Types: text, id (A-Z 0-9 _), loc, int, number, percent (0..1 shown as %), seconds, select, bool, item, spell, mob.
/// </summary>
public sealed class RecipeField
{
    public required string Key { get; init; }
    public required string Type { get; init; }
    public required string En { get; init; }
    public required string Ru { get; init; }
    public string? HintEn { get; init; }
    public string? HintRu { get; init; }
    public JsonNode? Default { get; init; }

    /// <summary>A default that depends on the catalogue (a boss mob of the right tier), evaluated when the recipes are listed.</summary>
    public Func<StudioContext, JsonNode?>? DefaultFrom { get; init; }

    public double? Min { get; init; }
    public double? Max { get; init; }
    public bool Required { get; init; }
    public IReadOnlyList<RecipeOption>? Options { get; init; }
    public string? Show { get; init; }
    public string? Suffix { get; init; }

    /// <summary>The official values of this number for the values chosen so far (the value helper), or null.</summary>
    public Func<RecipeRun, IEnumerable<double>>? Range { get; init; }

    public bool Numeric => Type is "int" or "number" or "percent" or "seconds";

    /// <summary>True when the field applies to <paramref name="values"/> (its Show condition holds).</summary>
    public bool Shown(JsonObject values)
    {
        if (Show is not { Length: > 0 } show || show.IndexOf('=') is not (> 0 and var eq)) return true;
        var current = J.Str(values[show[..eq]]);
        return show[(eq + 1)..].Split('|').Contains(current, StringComparer.OrdinalIgnoreCase);
    }

    public JsonObject ToJson(StudioContext? ctx) => new()
    {
        ["key"] = Key, ["type"] = Type, ["label"] = J.LocNode(En, Ru), ["hint"] = HintEn is null && HintRu is null ? null : J.LocNode(HintEn, HintRu),
        ["default"] = (ctx is not null && DefaultFrom is not null ? DefaultFrom(ctx) : Default)?.DeepClone(), ["min"] = Min, ["max"] = Max,
        ["required"] = Required, ["show"] = Show, ["suffix"] = Suffix, ["range"] = Range is not null,
        ["options"] = Options is null ? null : new JsonArray([.. Options.Select(o => (JsonNode)new JsonObject { ["value"] = o.Value, ["label"] = J.LocNode(o.En, o.Ru) })]),
    };
}

/// <summary>
/// A recipe: a goal the user picks ("one-handed weapon", "area damage", "legendary boss kit") instead of raw fields. Its fields
/// drive the form, the defaults and the required-field blockers; <see cref="Build"/> turns the values into ordinary drafts
/// (item, spell, mob, lootlist) that carry the recipe and its values as their "goal", so the editor can show the recipe's form
/// again and rebuild the draft from it.
/// </summary>
public sealed class StudioRecipe
{
    public required string Id { get; init; }

    /// <summary>"item", "spell" (one main draft), "line" (one item per tier), "kit" (several entities).</summary>
    public required string Kind { get; init; }

    public required string Icon { get; init; }
    public required string TitleEn { get; init; }
    public required string TitleRu { get; init; }
    public required string SummaryEn { get; init; }
    public required string SummaryRu { get; init; }
    public required IReadOnlyList<RecipeField> Fields { get; init; }
    public required Action<RecipeRun> Build { get; init; }
    public int Order { get; init; }

    /// <summary>The official spells or items this recipe is modelled on (shown on its card).</summary>
    public string[] Examples { get; init; } = [];

    /// <summary>A recipe whose fields this one shows too (the item line: the chosen item recipe's), by the value of this field.</summary>
    public string? InheritFrom { get; init; }

    public RecipeField? Field(string key) => Fields.FirstOrDefault(f => f.Key == key);

    public JsonObject ToJson(StudioContext? ctx) => new()
    {
        ["id"] = Id, ["kind"] = Kind, ["icon"] = Icon, ["title"] = J.LocNode(TitleEn, TitleRu), ["summary"] = J.LocNode(SummaryEn, SummaryRu),
        ["order"] = Order, ["examples"] = J.Strings(Examples), ["inheritFrom"] = InheritFrom,
        ["fields"] = new JsonArray([.. Fields.Select(f => (JsonNode)f.ToJson(ctx))]),
    };
}

/// <summary>One build of a recipe: the context, the values (defaults filled), the drafts made and the problems met.</summary>
public sealed class RecipeRun(StudioContext ctx, StudioRecipe recipe, JsonObject values)
{
    public StudioContext Ctx { get; } = ctx;
    public StudioRecipe Recipe { get; } = recipe;
    public JsonObject Values { get; } = values;
    public List<JsonObject> Drafts { get; } = [];
    public List<StudioDiag> Problems { get; } = [];

    public string S(string key) => J.Str(Values[key]).Trim();
    public double N(string key, double fallback = 0) => J.Num(Values[key]) ?? fallback;
    public double? Opt(string key) => J.Str(Values[key]).Length == 0 ? null : J.Num(Values[key]);
    public int I(string key, int fallback = 0) => (int)Math.Round(J.Num(Values[key]) ?? fallback);
    public bool B(string key) => J.Str(Values[key]) is "true" or "1";
    public (string En, string Ru) L(string key) => J.Loc(Values[key]);

    /// <summary>The goal a draft of this run carries: {recipe, values}.</summary>
    public JsonObject Goal(string? recipeId = null, JsonObject? values = null) => new()
    {
        ["recipe"] = recipeId ?? Recipe.Id, ["values"] = (values ?? Values).DeepClone(),
    };

    public JsonObject Add(JsonObject draft)
    {
        var id = J.Str(draft["id"]);
        var kind = J.Str(draft["kind"]);
        Drafts.RemoveAll(d => J.Str(d["kind"]) == kind && string.Equals(J.Str(d["id"]), id, StringComparison.OrdinalIgnoreCase));
        Drafts.Add(draft);
        return draft;
    }

    public void Problem(string severity, string code, string message, string key = "") =>
        Problems.Add(new StudioDiag(severity, code, message, key.Length > 0 ? "/values/" + StudioDiag.Seg(key) : ""));

    /// <summary>A run of another recipe with these values (the item line's per tier, a kit's parts); its drafts and problems come back here.</summary>
    public RecipeRun Sub(string recipeId, JsonObject values)
    {
        var recipe = StudioRecipes.Get(recipeId) ?? throw new InvalidDataException($"no recipe {recipeId}");
        var run = new RecipeRun(Ctx, recipe, StudioRecipes.Fill(Ctx, recipe, values));
        recipe.Build(run);
        foreach (var d in run.Drafts) Add(d);
        Problems.AddRange(run.Problems);
        return run;
    }
}

/// <summary>The recipe registry, the value checks, the build and the value helper's official ranges.</summary>
public static partial class StudioRecipes
{
    private static readonly List<StudioRecipe> Registry = [];

    static StudioRecipes()
    {
        RegisterItems();
        RegisterSpells();
        RegisterKits();
    }

    public static IReadOnlyList<StudioRecipe> All
    {
        get { lock (Registry) return [.. Registry.OrderBy(r => r.Order)]; }
    }

    public static StudioRecipe? Get(string? id)
    {
        lock (Registry) return id is null ? null : Registry.FirstOrDefault(r => r.Id == id);
    }

    /// <summary>Adds (or replaces) a recipe; plugins may register their own the same way.</summary>
    public static void Register(StudioRecipe recipe)
    {
        lock (Registry)
        {
            Registry.RemoveAll(r => r.Id == recipe.Id);
            Registry.Add(recipe);
        }
    }

    /// <summary>The fields of a recipe plus those it inherits (the item line's chosen recipe's, but its tier).</summary>
    public static List<RecipeField> FieldsOf(StudioRecipe recipe, JsonObject values)
    {
        var list = recipe.Fields.ToList();
        if (recipe.InheritFrom is { } key && Get(J.Str(values[key])) is { } inner)
            list.AddRange(inner.Fields.Where(f => f.Key != "tier" && list.All(x => x.Key != f.Key)));
        return list;
    }

    /// <summary>The values with the defaults of the fields not given.</summary>
    public static JsonObject Fill(StudioContext ctx, StudioRecipe recipe, JsonObject? values)
    {
        var v = (JsonObject?)values?.DeepClone() ?? [];
        foreach (var f in recipe.Fields)
            if (v[f.Key] is null && (f.DefaultFrom is not null ? f.DefaultFrom(ctx) : f.Default) is { } d) v[f.Key] = d.DeepClone();
        if (recipe.InheritFrom is { } key && Get(J.Str(v[key])) is { } inner)
            foreach (var f in inner.Fields)
                if (v[f.Key] is null && (f.DefaultFrom is not null ? f.DefaultFrom(ctx) : f.Default) is { } d) v[f.Key] = d.DeepClone();
        return v;
    }

    /// <summary>The values checked against the fields: required, numbers and their bounds, options, references.</summary>
    public static List<StudioDiag> Check(StudioContext ctx, StudioRecipe recipe, JsonObject values, string prefix, string? kind = null, string? id = null)
    {
        var list = new List<StudioDiag>();
        RecipeField? at = null;
        void Add(string sev, string code, string msg, string key) => list.Add(new(sev, code, msg, prefix + StudioDiag.Seg(key), null, kind, id) { Label = at is null ? null : (at.Ru, at.En) });
        foreach (var f in FieldsOf(recipe, values))
        {
            at = f;
            if (!f.Shown(values)) continue;
            var node = values[f.Key];
            var text = f.Type == "loc" ? string.Join("", J.Loc(node).En, J.Loc(node).Ru) : J.Str(node).Trim();
            var label = f.En;
            if (text.Length == 0)
            {
                if (f.Required) Add(StudioDiag.Blocker, "REQUIRED", $"{label}: required", f.Key);
                continue;
            }
            if (f.Numeric)
            {
                if (J.Num(node) is not { } n || !double.IsFinite(n)) { Add(StudioDiag.Blocker, "BAD_NUMBER", $"{label}: '{text}' is not a number", f.Key); continue; }
                if (f.Type == "int" && n != Math.Floor(n)) Add(StudioDiag.Blocker, "BAD_NUMBER", $"{label}: a whole number", f.Key);
                if (f.Min is { } min && n < min || f.Max is { } max && n > max)
                    Add(StudioDiag.Blocker, "OUT_OF_RANGE", $"{label}: {Show(f, f.Min)}..{Show(f, f.Max)} (now {Show(f, n)})", f.Key);
            }
            else switch (f.Type)
            {
                case "select" when f.Options is { } options && !options.Any(o => o.Value == text):
                    Add(StudioDiag.Blocker, "BAD_VALUE", $"{label}: '{text}' is not one of {string.Join(", ", options.Select(o => o.Value))}", f.Key);
                    break;
                case "id" when !System.Text.RegularExpressions.Regex.IsMatch(text, "^[A-Z0-9][A-Z0-9_]{1,70}$"):
                    Add(StudioDiag.Blocker, "BAD_ID", $"{label}: A-Z, 0-9 and _ (2 to 70)", f.Key);
                    break;
                case "item" when !ctx.ItemExists(text):
                    Add(StudioDiag.Blocker, "NO_ITEM", $"{label}: no item {text}", f.Key);
                    break;
                case "spell" when !ctx.SpellExists(text):
                    Add(StudioDiag.Blocker, "NO_SPELL", $"{label}: no spell {text}", f.Key);
                    break;
                case "mob" when !ctx.Catalog.MobEls.ContainsKey(text):
                    Add(StudioDiag.Blocker, "NO_MOB", $"{label}: no mob {text} in mobs.bin", f.Key);
                    break;
            }
        }
        return list;

        static string Show(RecipeField f, double? v) => v is not { } x ? "…" : f.Type == "percent" ? J.Fmt(x * 100) + "%" : J.Fmt(x);
    }

    /// <summary>A draft's goal checked against its recipe (blockers at /goal/values/&lt;key&gt;); nothing for a draft without one.</summary>
    public static List<StudioDiag> ValidateGoal(StudioContext ctx, JsonObject draft)
    {
        if (draft["goal"] is not JsonObject goal) return [];
        var kind = J.Str(draft["kind"]);
        var id = J.Str(draft["id"]);
        if (Get(J.Str(goal["recipe"])) is not { } recipe)
            return [new StudioDiag(StudioDiag.Info, "NO_RECIPE", $"recipe {J.Str(goal["recipe"])} is not known (the draft is edited by its fields only)", "/goal", null, kind, id)];
        return Check(ctx, recipe, J.Obj(goal["values"]), "/goal/values/", kind, id);
    }

    /// <summary>
    /// Builds <paramref name="recipe"/> with <paramref name="values"/>: the drafts (not saved) and the problems (the value checks first:
    /// a blocker there stops the build).
    /// </summary>
    public static RecipeRun Run(StudioContext ctx, StudioRecipe recipe, JsonObject? values)
    {
        var run = new RecipeRun(ctx, recipe, Fill(ctx, recipe, values));
        run.Problems.AddRange(Check(ctx, recipe, run.Values, "/values/"));
        if (run.Problems.Any(p => p.Severity == StudioDiag.Blocker)) return run;
        try { recipe.Build(run); }
        catch (Exception e) when (e is InvalidDataException or FormatException or InvalidOperationException or ArgumentException)
        {
            run.Problems.Add(new StudioDiag(StudioDiag.Blocker, "RECIPE", e.Message));
        }
        return run;
    }

    /// <summary>The value helper: per numeric field with a range, min/median/max/n of the official values for the values chosen.</summary>
    public static JsonObject Ranges(StudioContext ctx, StudioRecipe recipe, JsonObject values)
    {
        var run = new RecipeRun(ctx, recipe, Fill(ctx, recipe, values));
        var result = new JsonObject();
        foreach (var f in FieldsOf(recipe, run.Values).Where(f => f.Range is not null))
        {
            List<double> list;
            try { list = [.. f.Range!(run).Where(double.IsFinite).Order()]; }
            catch (Exception e) when (e is InvalidDataException or FormatException or InvalidOperationException or ArgumentException) { continue; }
            if (list.Count == 0) continue;
            result[f.Key] = new JsonObject
            {
                ["min"] = Round(list[0]), ["median"] = Round(list[list.Count / 2]), ["max"] = Round(list[^1]), ["n"] = list.Count,
            };
        }
        return result;

        static double Round(double v) => Math.Abs(v) >= 100 ? Math.Round(v, 1) : Math.Round(v, 3);
    }

    // ---------------------------------------------------------------- building blocks for the recipes

    internal static JsonNode Num(double v) => JsonValue.Create(v)!;

    internal static RecipeOption Opt(string value, string en, string ru) => new(value, en, ru);

    internal static RecipeField Fld(string key, string type, string en, string ru, JsonNode? def = null, double? min = null, double? max = null,
        bool required = false, string? hintEn = null, string? hintRu = null, IReadOnlyList<RecipeOption>? options = null, string? show = null,
        Func<RecipeRun, IEnumerable<double>>? range = null, string? suffix = null, Func<StudioContext, JsonNode?>? defaultFrom = null) => new()
    {
        Key = key, Type = type, En = en, Ru = ru, Default = def, Min = min, Max = max, Required = required, HintEn = hintEn, HintRu = hintRu,
        Options = options, Show = show, Range = range, Suffix = suffix, DefaultFrom = defaultFrom,
    };

    /// <summary>The unique-name field (the game's key of the new entry; items get T&lt;tier&gt;_ in front).</summary>
    internal static RecipeField IdField(string def, bool tiered) => Fld("base", "id", tiered ? "Unique name (after T<tier>_)" : "Unique name", tiered ? "Уникальное имя (после T<тир>_)" : "Уникальное имя",
        JsonValue.Create(def), required: true, hintEn: "A-Z, 0-9 and _: the game's key of the new entry", hintRu: "A-Z, 0-9 и _: ключ новой записи в игре");

    internal static RecipeField NameField(string en, string ru) => Fld("name", "loc", "Name", "Название", J.LocNode(en, ru), required: true);

    /// <summary>"T6_MAIN_SWORD" for family MAIN_SWORD at tier 6, or the nearest tier the family has (consumables exist at some tiers only).</summary>
    internal static CatItem? TemplateAt(StudioContext ctx, string family, int tier)
    {
        if (ctx.FindItem($"T{tier}_{family}") is { Pack: null } exact) return exact;
        for (var d = 1; d <= 7; d++)
            foreach (var t in new[] { tier - d, tier + d })
                if (t is >= 1 and <= 8 && ctx.FindItem($"T{t}_{family}") is { Pack: null } near) return near;
        return ctx.FindItem(family) is { Pack: null } plain ? plain : null;
    }

    internal static string ItemId(int tier, string baseName, CatItem template) =>
        System.Text.RegularExpressions.Regex.IsMatch(template.Name, "^T[0-9]_") ? $"T{tier}_{baseName}" : baseName;

    /// <summary>A number attribute of an element (invariant), or null.</summary>
    internal static double? Attr(XElement? e, string attr) => e is null ? null : ModCatalog.Num(e, attr);

    /// <summary>Sets an attribute override when it differs from the template's value.</summary>
    internal static void SetAttr(JsonObject draft, XElement template, string attr, double value)
    {
        var attrs = J.Obj(draft["attributes"]);
        draft["attributes"] = attrs;
        if (Attr(template, attr) is { } old && Math.Abs(old - value) < 1e-9) { attrs.Remove(attr); return; }
        attrs[attr] = Fmt(value);
    }

    internal static void SetAttr(JsonObject draft, string attr, string value)
    {
        var attrs = J.Obj(draft["attributes"]);
        draft["attributes"] = attrs;
        attrs[attr] = value;
    }

    internal static string Fmt(double v) => v == 0 ? "0" : v.ToString("0.####", CultureInfo.InvariantCulture);

    /// <summary>A nested edit of a spell draft: "2/0@value".</summary>
    internal static void Edit(JsonObject draft, string key, string value)
    {
        var edits = J.Obj(draft["edits"]);
        draft["edits"] = edits;
        edits[key] = value;
    }

    internal static void Edit(JsonObject draft, string key, double value) => Edit(draft, key, Fmt(value));

    internal static void RemoveBlock(JsonObject draft, params string[] paths)
    {
        var blocks = J.Obj(draft["blocks"]);
        draft["blocks"] = blocks;
        var list = J.Arr(blocks["remove"]);
        blocks["remove"] = list;
        foreach (var p in paths) list.Add(p);
    }

    internal static void AddBlock(JsonObject draft, string from, string path, params (string Attr, string Value)[] attrs)
    {
        var blocks = J.Obj(draft["blocks"]);
        draft["blocks"] = blocks;
        var list = J.Arr(blocks["add"]);
        blocks["add"] = list;
        var a = new JsonObject();
        foreach (var (k, v) in attrs) a[k] = v;
        list.Add(new JsonObject { ["from"] = from, ["path"] = path, ["attributes"] = a });
    }

    // ---------------------------------------------------------------- official ranges (the value helper)

    private sealed class StatCache
    {
        public readonly Dictionary<string, List<double>> Memo = [];
        public List<XElement>? OfferedTrees;
    }

    private static readonly ConditionalWeakTable<ModCatalog, StatCache> Stats = [];

    private static List<double> Memo(StudioContext ctx, string key, Func<List<double>> make)
    {
        var cache = Stats.GetValue(ctx.Catalog, _ => new StatCache());
        lock (cache)
        {
            if (cache.Memo.TryGetValue(key, out var v)) return v;
            return cache.Memo[key] = make();
        }
    }

    /// <summary>An item attribute among the official items of the same shop subcategory (and tier, when enough have it).</summary>
    internal static List<double> ItemValues(StudioContext ctx, string attr, string? subcategory, int tier) =>
        Memo(ctx, $"item|{attr}|{subcategory}|{tier}", () =>
        {
            var same = ctx.Catalog.Items.Where(i => i.Pack is null && (subcategory is null || i.El.Attribute("shopsubcategory1")?.Value == subcategory)).ToList();
            var atTier = same.Where(i => i.Tier == tier).Select(i => Attr(i.El, attr)).OfType<double>().ToList();
            return atTier.Count >= 3 ? atTier : [.. same.Select(i => Attr(i.El, attr)).OfType<double>()];
        });

    /// <summary>The offered spells (those items give) and the spells they use, two levels deep.</summary>
    private static List<XElement> OfferedTrees(StudioContext ctx)
    {
        var cache = Stats.GetValue(ctx.Catalog, _ => new StatCache());
        lock (cache)
        {
            if (cache.OfferedTrees is { } done) return done;
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var list = new List<XElement>();
            var queue = new Queue<(string, int)>(ctx.Catalog.OfferedBy.Keys.Select(k => (k, 0)));
            while (queue.Count > 0)
            {
                var (name, depth) = queue.Dequeue();
                if (!seen.Add(name) || !ctx.Catalog.SpellByName.TryGetValue(name, out var s)) continue;
                list.Add(s.El);
                if (depth < 2)
                    foreach (var a in s.El.Descendants().Attributes().Where(a => a.Name.LocalName is "spell" or "effect" or "endeffect"))
                        queue.Enqueue((a.Value, depth + 1));
            }
            return cache.OfferedTrees = list;
        }
    }

    /// <summary>A root attribute of the offered spells (of <paramref name="category"/> when given).</summary>
    internal static List<double> SpellRoot(StudioContext ctx, string attr, string? category = null) =>
        Memo(ctx, $"root|{attr}|{category}", () =>
            [.. ctx.Catalog.OfferedBy.Keys.Select(k => ctx.Catalog.SpellByName.GetValueOrDefault(k)?.El)
                .Where(e => e is not null && e.Name.LocalName == "activespell" && (category is null || e.Attribute("category")?.Value == category))
                .Select(e => Attr(e, attr)).OfType<double>()]);

    /// <summary>An attribute of the blocks named <paramref name="element"/> in the offered spells' trees (absolute values), filtered.</summary>
    internal static List<double> BlockValues(StudioContext ctx, string element, string attr, string key, Func<XElement, bool>? where = null) =>
        Memo(ctx, $"block|{element}|{attr}|{key}", () =>
            [.. OfferedTrees(ctx).SelectMany(s => s.Descendants(element)).Where(e => where is null || where(e))
                .Select(e => Attr(e, attr)).OfType<double>().Select(Math.Abs).Where(v => v > 0)]);

    internal static bool Is(XElement e, string attr, string value) => string.Equals(e.Attribute(attr)?.Value, value, StringComparison.OrdinalIgnoreCase);
}
