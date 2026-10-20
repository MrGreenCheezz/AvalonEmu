using System.Text.Json.Nodes;
using System.Xml.Linq;

namespace AlbionLocal.Server.Web.Studio;

/// <summary>
/// A mob variant (studio phase 3, SPEC "mob/loot decision"): the modkit cannot patch mobs.bin or loot.bin, so new mobs are
/// server-side pack data: an official mob (the client's model, name and spells) with factors on its health, damage and fame,
/// extra loot lists and a chance to replace the template where it spawns in the world. Compiles to mods/&lt;pack&gt;/server/mobs.json
/// (read by <see cref="Mods.ModDrops"/> for the packs in Mods:Packs); GMs spawn one with .mob variant &lt;id&gt;.
/// <code>{ "kind": "mob", "id": "FROST_BOSS", "template": "T6_MOB_…", "name": {en, ru}, "healthFactor": 4, "damageFactor": 1.5,
///   "fameFactor": 3, "loot": [{"list": "FROST_BOSS_LOOT", "chance": 1}], "replaceChance": 0.01 }</code>
/// </summary>
public sealed class MobKind : IStudioKind
{
    public string Kind => "mob";
    public string PatchFile => "server/mobs.json";

    public XElement? TemplateElement(StudioContext ctx, JsonObject draft) => ctx.Catalog.MobEls.GetValueOrDefault(J.Str(draft["template"]));

    public JsonObject NewFrom(StudioContext ctx, string template, string id)
    {
        if (!ctx.Catalog.MobEls.TryGetValue(template, out var mob)) throw new InvalidDataException($"no mob {template}");
        var (en, ru) = ctx.Catalog.Loc.GetValueOrDefault(ModCatalog.MobNameKey(mob));
        return new JsonObject
        {
            ["schema"] = 1, ["kind"] = "mob", ["id"] = id, ["template"] = mob.Attribute("uniquename")?.Value ?? template,
            ["name"] = J.LocNode(en is null ? id : en + " (variant)", ru is null ? "" : ru + " (вариант)"),
            ["healthFactor"] = 1, ["damageFactor"] = 1, ["fameFactor"] = 1, ["loot"] = new JsonArray(), ["replaceChance"] = 0,
        };
    }

    public IEnumerable<StudioRef> References(JsonObject d)
    {
        if (J.Str(d["template"]) is { Length: > 0 } t) yield return new("mob", t, "/template");
        var loot = J.Arr(d["loot"]);
        for (var i = 0; i < loot.Count; i++)
            if (J.Str(loot[i]?["list"]) is { Length: > 0 } l) yield return new("lootlist", l, $"/loot/{i}/list");
    }

    public int RenameRefs(JsonObject d, string kind, string from, string to)
    {
        var n = 0;
        if (kind == "lootlist")
            foreach (var l in J.Arr(d["loot"]).OfType<JsonObject>())
                if (string.Equals(J.Str(l["list"]), from, StringComparison.OrdinalIgnoreCase)) { l["list"] = to; n++; }
        return n;
    }

    public List<StudioDiag> Validate(StudioContext ctx, JsonObject d)
    {
        var list = new List<StudioDiag>();
        var id = J.Str(d["id"]);
        void Add(string sev, string code, string msg, string path, string? hint = null) => list.Add(new(sev, code, msg, path, hint, "mob", id));
        if (!PackFiles.NewName().IsMatch(id)) Add(StudioDiag.Blocker, "BAD_ID", "the unique name: 3 to 80 of A-Z, 0-9 and _ (e.g. FROST_BOSS)", "/id");
        else if (ctx.Catalog.MobEls.ContainsKey(id)) Add(StudioDiag.Blocker, "ID_TAKEN", $"{id} is an official mob: a variant needs a name of its own", "/id");
        if (TemplateElement(ctx, d) is null) Add(StudioDiag.Blocker, "NO_TEMPLATE", $"no mob {J.Str(d["template"])} in mobs.bin", "/template", "pick the mob whose model and spells it uses");
        var (en, ru) = J.Loc(d["name"]);
        if (en.Length == 0 && ru.Length == 0) Add(StudioDiag.Warning, "NO_NAME_EN", "no name (the server's logs and announcements use it; the client shows the template's)", "/name");
        foreach (var (field, min, max) in new[] { ("healthFactor", 0.05, 200.0), ("damageFactor", 0.05, 50.0), ("fameFactor", 0.0, 100.0) })
            if (J.Num(d[field]) is not { } v || v < min || v > max) Add(StudioDiag.Blocker, "OUT_OF_RANGE", $"{field}: {min}..{max}", "/" + field);
        if (J.Num(d["replaceChance"]) is not { } rc || rc < 0 || rc > 1) Add(StudioDiag.Blocker, "BAD_CHANCE", "replaceChance: 0..1 (0.01 = 1 % of the template's world spawns)", "/replaceChance");
        var loot = J.Arr(d["loot"]);
        for (var i = 0; i < loot.Count; i++)
        {
            var l = J.Str(loot[i]?["list"]).Trim();
            if (l.Length == 0) Add(StudioDiag.Blocker, "EMPTY_REF", $"loot {i + 1}: choose a loot list", $"/loot/{i}/list");
            else if (!ctx.DraftExists("lootlist", l) && !ctx.Catalog.LootListEls.ContainsKey(l)) Add(StudioDiag.Blocker, "NO_LIST", $"no loot list {l} (neither this pack's nor loot.xml's)", $"/loot/{i}/list");
            if (J.Num(loot[i]?["chance"]) is not { } c || c < 0 || c > 1) Add(StudioDiag.Blocker, "BAD_CHANCE", $"loot {i + 1}: a chance of 0..1", $"/loot/{i}/chance");
        }
        list.AddRange(StudioRecipes.ValidateGoal(ctx, d));
        return list;
    }

    public StudioCompiled Compile(StudioContext ctx, JsonObject d)
    {
        var result = new StudioCompiled();
        var id = J.Str(d["id"]);
        var (en, ru) = J.Loc(d["name"]);
        result.AddServer(PatchFile, new JsonObject
        {
            ["Id"] = id, ["Template"] = J.Str(d["template"]), ["Name"] = new JsonObject { ["En"] = WebPanel.Clean(en, 200), ["Ru"] = WebPanel.Clean(ru, 200) },
            ["HealthFactor"] = J.Num(d["healthFactor"]) ?? 1, ["DamageFactor"] = J.Num(d["damageFactor"]) ?? 1, ["FameFactor"] = J.Num(d["fameFactor"]) ?? 1,
            ["ReplaceChance"] = Math.Clamp(J.Num(d["replaceChance"]) ?? 0, 0, 1),
            ["Loot"] = new JsonArray([.. J.Arr(d["loot"]).OfType<JsonObject>().Where(l => J.Str(l["list"]).Length > 0)
                .Select(l => (JsonNode)new JsonObject { ["List"] = J.Str(l["list"]), ["Chance"] = Math.Clamp(J.Num(l["chance"]) ?? 1, 0, 1) })]),
        });
        return result;
    }

    /// <summary>The drop rules of this pack's item drafts that fire for the variant (its id or its template's name, patterns too).</summary>
    private static JsonArray DropRules(StudioContext ctx, string id, string template)
    {
        var rules = new JsonArray();
        foreach (var ((kind, item), draft) in ctx.Drafts.Where(x => x.Key.Kind == "item"))
            foreach (var drop in J.Arr(draft["drops"]).OfType<JsonObject>())
            {
                var mobs = J.StringList(drop["mobs"]);
                if (!mobs.Any(m => Glob(m, id) || Glob(m, template))) continue;
                var (en, ru) = J.Loc(draft["name"]);
                rules.Add(new JsonObject { ["item"] = item, ["name"] = J.LocNode(en, ru), ["chance"] = J.Num(drop["chance"]) ?? 0, ["amount"] = J.Num(drop["amount"]) ?? 1 });
            }
        return rules;

        static bool Glob(string pattern, string name) => System.Text.RegularExpressions.Regex.IsMatch(name,
            "^" + System.Text.RegularExpressions.Regex.Escape(pattern).Replace("\\*", ".*").Replace("\\?", ".") + "$", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
    }

    public JsonObject Tooltip(StudioContext ctx, JsonObject d, StudioCompiled compiled)
    {
        var mob = TemplateElement(ctx, d);
        var (en, ru) = J.Loc(d["name"]);
        double F(string a) => mob is null ? 0 : ModCatalog.Num(mob, a) ?? 0;
        var hf = J.Num(d["healthFactor"]) ?? 1;
        var stats = new JsonArray
        {
            new JsonObject { ["key"] = "hitpointsmax", ["value"] = J.Fmt(Math.Round(F("hitpointsmax") * hf)), ["template"] = J.Fmt(F("hitpointsmax")), ["changed"] = hf != 1 },
            new JsonObject { ["key"] = "damagefactor", ["value"] = "×" + J.Fmt(J.Num(d["damageFactor"]) ?? 1), ["template"] = "×1", ["changed"] = (J.Num(d["damageFactor"]) ?? 1) != 1 },
            new JsonObject { ["key"] = "fame", ["value"] = J.Fmt(Math.Round(F("fame") * (J.Num(d["fameFactor"]) ?? 1))), ["template"] = J.Fmt(F("fame")), ["changed"] = (J.Num(d["fameFactor"]) ?? 1) != 1 },
        };
        return new JsonObject
        {
            ["kind"] = "mob", ["id"] = J.Str(d["id"]), ["name"] = J.LocNode(en, ru), ["tier"] = mob?.Attribute("tier")?.Value, ["type"] = "mob variant",
            ["category"] = mob?.Attribute("mobtypecategory")?.Value, ["stats"] = stats,
            ["loot"] = new JsonArray([.. J.Arr(d["loot"]).OfType<JsonObject>().Select(l => (JsonNode)new JsonObject { ["list"] = J.Str(l["list"]), ["chance"] = J.Num(l["chance"]) ?? 1 })]),
            ["rules"] = DropRules(ctx, J.Str(d["id"]), J.Str(d["template"])),
            ["description"] = J.LocNode($"Server-side variant of {J.Str(d["template"])}: the client shows the template's model and name.",
                $"Серверный вариант {J.Str(d["template"])}: клиент показывает модель и имя шаблона."),
        };
    }
}

/// <summary>
/// A server loot list (studio phase 3): mods/&lt;pack&gt;/server/loot.json. Each roll of the list: every entry without a weight
/// rolls its own chance; the weighted entries share <c>rolls</c> picks by weight (an entry "nothing" is an empty weighted entry).
/// An entry gives an item (min..max) or rolls another list (a pack's or loot.xml's). Used by mob variants and drop rules.
/// <code>{ "kind": "lootlist", "id": "FROST_BOSS_LOOT", "name": {…}, "rolls": 1,
///   "entries": [{"item": "T6_MAIN_FROSTBLADE", "chance": 0.1}, {"item": "T6_METALBAR", "weight": 3, "min": 2, "max": 5}, {"list": "…", "chance": 1}] }</code>
/// </summary>
public sealed class LootListKind : IStudioKind
{
    public string Kind => "lootlist";
    public string PatchFile => "server/loot.json";

    public XElement? TemplateElement(StudioContext ctx, JsonObject draft) => ctx.Catalog.LootListEls.GetValueOrDefault(J.Str(draft["template"]));

    public JsonObject NewFrom(StudioContext ctx, string template, string id)
    {
        var entries = new JsonArray();
        if (template.Length > 0)
        {
            if (!ctx.Catalog.LootListEls.TryGetValue(template, out var list)) throw new InvalidDataException($"no loot list {template}");
            foreach (var it in list.Descendants("Item").Take(60))
            {
                var amount = it.Attribute("amount")?.Value ?? "1";
                var parts = amount.Split('-');
                entries.Add(new JsonObject
                {
                    ["item"] = it.Attribute("type")?.Value ?? "", ["chance"] = Math.Clamp(ModCatalog.Num(it, "chance") ?? 1, 0, 1),
                    ["min"] = int.TryParse(parts[0], out var mn) ? mn : 1, ["max"] = int.TryParse(parts[^1], out var mx) ? mx : 1,
                });
            }
        }
        return new JsonObject
        {
            ["schema"] = 1, ["kind"] = "lootlist", ["id"] = id, ["template"] = template.Length > 0 ? template : null,
            ["name"] = J.LocNode(id, ""), ["rolls"] = 1, ["entries"] = entries,
        };
    }

    public IEnumerable<StudioRef> References(JsonObject d)
    {
        var e = J.Arr(d["entries"]);
        for (var i = 0; i < e.Count; i++)
        {
            if (J.Str(e[i]?["item"]) is { Length: > 0 } item) yield return new("item", item, $"/entries/{i}/item");
            if (J.Str(e[i]?["list"]) is { Length: > 0 } list) yield return new("lootlist", list, $"/entries/{i}/list");
        }
    }

    public int RenameRefs(JsonObject d, string kind, string from, string to)
    {
        var n = 0;
        var key = kind == "item" ? "item" : kind == "lootlist" ? "list" : null;
        if (key is null) return 0;
        foreach (var e in J.Arr(d["entries"]).OfType<JsonObject>())
            if (string.Equals(J.Str(e[key]), from, StringComparison.OrdinalIgnoreCase)) { e[key] = to; n++; }
        return n;
    }

    public List<StudioDiag> Validate(StudioContext ctx, JsonObject d)
    {
        var list = new List<StudioDiag>();
        var id = J.Str(d["id"]);
        void Add(string sev, string code, string msg, string path) => list.Add(new(sev, code, msg, path, null, "lootlist", id));
        if (!PackFiles.NewName().IsMatch(id)) Add(StudioDiag.Blocker, "BAD_ID", "the unique name: 3 to 80 of A-Z, 0-9 and _", "/id");
        else if (ctx.Catalog.LootListEls.ContainsKey(id)) Add(StudioDiag.Blocker, "ID_TAKEN", $"{id} is a loot.xml list: choose a name of its own", "/id");
        if (J.Num(d["rolls"]) is not { } rolls || rolls < 0 || rolls > 50 || rolls != Math.Floor(rolls)) Add(StudioDiag.Blocker, "OUT_OF_RANGE", "rolls: 0..50 picks among the weighted entries", "/rolls");
        var e = J.Arr(d["entries"]);
        if (e.Count == 0) Add(StudioDiag.Warning, "EMPTY_LIST", "no entries: the list gives nothing", "/entries");
        if (e.Count > 200) Add(StudioDiag.Blocker, "TOO_MANY", "at most 200 entries", "/entries");
        for (var i = 0; i < e.Count; i++)
        {
            var o = J.Obj(e[i]);
            var item = J.Str(o["item"]).Trim();
            var sub = J.Str(o["list"]).Trim();
            if (item.Length == 0 && sub.Length == 0) { Add(StudioDiag.Blocker, "EMPTY_REF", $"entry {i + 1}: an item or a list", $"/entries/{i}"); continue; }
            if (item.Length > 0 && !ctx.ItemExists(item)) Add(StudioDiag.Blocker, "NO_ITEM", $"entry {i + 1}: no item {item}", $"/entries/{i}/item");
            if (sub.Length > 0 && !ctx.DraftExists("lootlist", sub) && !ctx.Catalog.LootListEls.ContainsKey(sub)) Add(StudioDiag.Blocker, "NO_LIST", $"entry {i + 1}: no loot list {sub}", $"/entries/{i}/list");
            if (string.Equals(sub, id, StringComparison.OrdinalIgnoreCase)) Add(StudioDiag.Blocker, "SELF_REF", "a list cannot roll itself", $"/entries/{i}/list");
            var weighted = (J.Num(o["weight"]) ?? 0) > 0;
            if (J.Str(o["weight"]).Length > 0 && (J.Num(o["weight"]) is not { } w || w < 0 || w > 1e6)) Add(StudioDiag.Blocker, "BAD_VALUE", $"entry {i + 1}: a weight ≥ 0", $"/entries/{i}/weight");
            if (!weighted && (J.Num(o["chance"]) is not { } c || c < 0 || c > 1)) Add(StudioDiag.Blocker, "BAD_CHANCE", $"entry {i + 1}: a chance of 0..1 (or give it a weight)", $"/entries/{i}/chance");
            var min = J.Num(o["min"]) ?? 1;
            var max = J.Num(o["max"]) ?? min;
            if (min < 1 || max < min || max > 9999 || min != Math.Floor(min) || max != Math.Floor(max)) Add(StudioDiag.Blocker, "BAD_AMOUNT", $"entry {i + 1}: amounts 1 ≤ min ≤ max ≤ 9999", $"/entries/{i}/min");
        }
        list.AddRange(StudioRecipes.ValidateGoal(ctx, d));
        return list;
    }

    public StudioCompiled Compile(StudioContext ctx, JsonObject d)
    {
        var result = new StudioCompiled();
        var (en, ru) = J.Loc(d["name"]);
        result.AddServer(PatchFile, new JsonObject
        {
            ["Id"] = J.Str(d["id"]), ["Name"] = new JsonObject { ["En"] = WebPanel.Clean(en, 200), ["Ru"] = WebPanel.Clean(ru, 200) },
            ["Rolls"] = (int)Math.Clamp(J.Num(d["rolls"]) ?? 1, 0, 50),
            ["Entries"] = new JsonArray([.. J.Arr(d["entries"]).OfType<JsonObject>().Take(200).Select(o =>
            {
                var min = (int)Math.Clamp(J.Num(o["min"]) ?? 1, 1, 9999);
                return (JsonNode)new JsonObject
                {
                    ["Item"] = J.Str(o["item"]).Trim() is { Length: > 0 } i ? i : null, ["List"] = J.Str(o["list"]).Trim() is { Length: > 0 } l ? l : null,
                    ["Chance"] = Math.Clamp(J.Num(o["chance"]) ?? 1, 0, 1), ["Weight"] = Math.Max(0, J.Num(o["weight"]) ?? 0),
                    ["Min"] = min, ["Max"] = (int)Math.Clamp(J.Num(o["max"]) ?? min, min, 9999),
                };
            })]),
        });
        return result;
    }

    public JsonObject Tooltip(StudioContext ctx, JsonObject d, StudioCompiled compiled)
    {
        var (en, ru) = J.Loc(d["name"]);
        var entries = new JsonArray();
        foreach (var o in J.Arr(d["entries"]).OfType<JsonObject>().Take(200))
        {
            var item = J.Str(o["item"]);
            var (ien, iru) = item.Length > 0 ? ctx.Names("@ITEMS_" + item) : (null, null);
            if (item.Length > 0 && ctx.Drafts.FirstOrDefault(p => p.Key.Kind == "item" && string.Equals(p.Key.Id, item, StringComparison.OrdinalIgnoreCase)).Value is { } own)
                (ien, iru) = J.Loc(own["name"]);
            entries.Add(new JsonObject
            {
                ["item"] = item.Length > 0 ? item : null, ["list"] = J.Str(o["list"]) is { Length: > 0 } l ? l : null, ["name"] = J.LocNode(ien, iru),
                ["icon"] = item.Length > 0 && ctx.FindItem(item) is { } ci && ctx.Icons.ForItem(ci.El) is { } s ? StudioApi.IconUrl(s) : null,
                ["chance"] = J.Num(o["chance"]) ?? 1, ["weight"] = J.Num(o["weight"]) ?? 0, ["min"] = J.Num(o["min"]) ?? 1, ["max"] = J.Num(o["max"]) ?? J.Num(o["min"]) ?? 1,
            });
        }
        return new JsonObject
        {
            ["kind"] = "lootlist", ["id"] = J.Str(d["id"]), ["name"] = J.LocNode(en, ru), ["type"] = "server loot list",
            ["rolls"] = J.Num(d["rolls"]) ?? 1, ["entries"] = entries,
        };
    }
}
