using System.Globalization;
using System.Text.Json.Nodes;
using System.Xml.Linq;
using AlbionLocal.Server.Mods;

namespace AlbionLocal.Server.Web.Studio;

/// <summary>
/// An item draft (studio SPEC §4.1): a copy of a template item with attribute overrides, texts, recipe, stations, spell slots and
/// server-side drops. Compiles as the old editor's "clone &amp; edit" did: the template's model and icon, no enchantments,
/// unlockedtocraft/unlockedtoequip true (a new item has no destiny-board node).
/// </summary>
public sealed class ItemKind : IStudioKind
{
    public string Kind => "item";
    public string PatchFile => "items.xml";

    /// <summary>The attributes a draft cannot change (assets: only official ones exist).</summary>
    public static bool Fixed(XElement e, XAttribute a) => a.Name.LocalName is "uniquename" or "mesh" or "uisprite" || WebPanel.IsAsset(e, a);

    /// <summary>Whether items like this show a model on the character (weapons and equipment; the client builds "&lt;mesh&gt;_01").</summary>
    public static bool HasModel(XElement src) => src.Name.LocalName is "weapon" or "equipmentitem" || src.Attribute("mesh") is not null;

    public XElement? TemplateElement(StudioContext ctx, JsonObject draft) =>
        ctx.FindItem(J.Str(draft["template"]))?.El ?? ctx.DraftElement("item", J.Str(draft["template"]));

    /// <summary>The spells an item offers: its craftspell elements, or those of the item its craftingspelllist references.</summary>
    public static List<(string Spell, string? Slot, string? Tag)> Spells(StudioContext ctx, XElement item)
    {
        for (var depth = 0; depth < 5; depth++)
        {
            var list = item.Element("craftingspelllist");
            if (list is null) return [];
            if (list.Attribute("reference")?.Value is { Length: > 0 } reference && !list.Elements("craftspell").Any())
            {
                if (ctx.FindItem(reference)?.El is not { } next) return [];
                item = next;
                continue;
            }
            return [.. list.Elements("craftspell").Select(c => (c.Attribute("uniquename")?.Value ?? "", c.Attribute("slots")?.Value, c.Attribute("tag")?.Value))];
        }
        return [];
    }

    public JsonObject NewFrom(StudioContext ctx, string template, string id)
    {
        var t = ctx.FindItem(template) ?? throw new InvalidDataException($"no item {template}");
        var src = t.El;
        var (en, ru) = ctx.Names("@ITEMS_" + t.Name);
        var (den, dru) = ctx.Names(src.Attribute("descriptionlocatag")?.Value is { Length: > 1 } dk ? dk : "@ITEMS_" + t.Name + "_DESC");
        var req = src.Elements("craftingrequirements").FirstOrDefault();
        var draft = new JsonObject
        {
            ["schema"] = 1, ["kind"] = "item", ["id"] = id, ["template"] = t.Name,
            ["name"] = J.LocNode(en is null ? id : en + " (copy)", ru is null ? "" : ru + " (копия)"),
            ["description"] = J.LocNode(den, dru),
            ["attributes"] = new JsonObject(),
            ["recipe"] = req is null ? null : new JsonObject
            {
                ["silver"] = req.Attribute("silver")?.Value ?? "0", ["time"] = req.Attribute("time")?.Value ?? "1",
                ["focus"] = req.Attribute("craftingfocus")?.Value ?? "0", ["amount"] = req.Attribute("amountcrafted")?.Value,
                ["resources"] = new JsonArray([.. req.Elements("craftresource").Select(c => (JsonNode)new JsonObject
                {
                    ["item"] = c.Attribute("uniquename")?.Value ?? "", ["count"] = int.TryParse(c.Attribute("count")?.Value, out var n) ? n : 1,
                })]),
            },
            ["stations"] = J.Strings((ctx.Catalog.StationsOf.GetValueOrDefault(t.Name) ?? []).Select(x => x.Station).Where(x => !x.Contains("TEST", StringComparison.OrdinalIgnoreCase)).Distinct()),
            ["spells"] = new JsonArray([.. Spells(ctx, src).Select(s => (JsonNode)new JsonObject
            {
                ["spell"] = s.Spell, ["slot"] = int.TryParse(s.Slot, out var sl) ? sl : null, ["tag"] = s.Tag,
            })]),
            ["drops"] = new JsonArray(),
        };
        return draft;
    }

    public IEnumerable<StudioRef> References(JsonObject d)
    {
        var refs = new List<StudioRef>();
        if (J.Str(d["template"]) is { Length: > 0 } t) refs.Add(new("item", t, "/template"));
        var res = J.Arr(J.Obj(d["recipe"])["resources"]);
        for (var i = 0; i < res.Count; i++) refs.Add(new("item", J.Str(res[i]?["item"]), $"/recipe/resources/{i}/item"));
        var st = J.Arr(d["stations"]);
        for (var i = 0; i < st.Count; i++) refs.Add(new("station", J.Str(st[i]), $"/stations/{i}"));
        var sp = J.Arr(d["spells"]);
        for (var i = 0; i < sp.Count; i++) refs.Add(new("spell", J.Str(sp[i]?["spell"]), $"/spells/{i}/spell"));
        var extra = J.Arr(d["extraSpells"]);
        for (var i = 0; i < extra.Count; i++) refs.Add(new("spell", J.Str(extra[i]), $"/extraSpells/{i}"));
        foreach (var (name, value) in J.Obj(d["attributes"]))
            if (name is "consumespell" or "spell" or "equipspell" or "mountedbuff" or "halfmountedbuff" && J.Str(value) is { Length: > 0 } v) refs.Add(new("spell", v, "/attributes/" + StudioDiag.Seg(name)));
        var drops = J.Arr(d["drops"]);
        for (var i = 0; i < drops.Count; i++)
        {
            var mobs = J.StringList(drops[i]?["mobs"]);
            for (var k = 0; k < mobs.Count; k++) refs.Add(new("mob", mobs[k], $"/drops/{i}/mobs/{k}"));
            var chests = J.StringList(drops[i]?["chests"]);
            for (var k = 0; k < chests.Count; k++) refs.Add(new("chest", chests[k], $"/drops/{i}/chests/{k}"));
        }
        return refs.Where(r => r.Name.Length > 0);
    }

    public int RenameRefs(JsonObject d, string kind, string from, string to)
    {
        var n = 0;
        bool Eq(JsonNode? x) => string.Equals(J.Str(x), from, StringComparison.OrdinalIgnoreCase);
        if (kind == "item")
        {
            if (Eq(d["template"])) { d["template"] = to; n++; }
            foreach (var r in J.Arr(J.Obj(d["recipe"])["resources"]).OfType<JsonObject>())
                if (Eq(r["item"])) { r["item"] = to; n++; }
        }
        if (kind == "spell")
        {
            foreach (var s in J.Arr(d["spells"]).OfType<JsonObject>())
                if (Eq(s["spell"])) { s["spell"] = to; n++; }
            if (d["extraSpells"] is JsonArray extra)
                for (var i = 0; i < extra.Count; i++)
                    if (Eq(extra[i])) { extra[i] = to; n++; }
            var attrs = J.Obj(d["attributes"]);
            foreach (var key in attrs.Select(p => p.Key).ToList())
                if (key is "consumespell" or "spell" or "equipspell" or "mountedbuff" or "halfmountedbuff" && Eq(attrs[key])) { attrs[key] = to; n++; }
        }
        if (kind == "station" && d["stations"] is JsonArray st)
            for (var i = 0; i < st.Count; i++)
                if (Eq(st[i])) { st[i] = to; n++; }
        return n;
    }

    public List<StudioDiag> Validate(StudioContext ctx, JsonObject d)
    {
        var list = new List<StudioDiag>();
        var id = J.Str(d["id"]);
        void Add(string sev, string code, string msg, string path, string? hint = null) => list.Add(new(sev, code, msg, path, hint, "item", id));
        if (!PackFiles.NewName().IsMatch(id)) Add(StudioDiag.Blocker, "BAD_ID", "the unique name: 3 to 80 of A-Z, 0-9 and _ (e.g. T6_MAIN_MYSWORD)", "/id");
        else if (ctx.FindItem(id) is { } taken && taken.Pack != ctx.Pack)
            Add(StudioDiag.Blocker, "ID_TAKEN", $"{id} exists already ({(taken.Pack is { } tp ? "in pack " + tp : "an official item")})", "/id", "choose another unique name");
        var src = TemplateElement(ctx, d);
        if (src is null) { Add(StudioDiag.Blocker, "NO_TEMPLATE", $"no template item {J.Str(d["template"])}", "/template"); return list; }
        var (en, ru) = J.Loc(d["name"]);
        if (en.Length == 0) Add(StudioDiag.Warning, "NO_NAME_EN", "no English name: the client shows the raw key", "/name/en");
        if (ru.Length == 0) Add(StudioDiag.Warning, "NO_NAME_RU", "no Russian name: the Russian client shows the raw key", "/name/ru");
        if (en.Length > 200 || ru.Length > 200) Add(StudioDiag.Blocker, "NAME_LONG", "a name: at most 200 characters", "/name");
        foreach (var (name, value) in J.Obj(d["attributes"]))
        {
            var path = "/attributes/" + StudioDiag.Seg(name);
            var a = src.Attribute(name);
            if (a is null) { Add(StudioDiag.Warning, "NEW_ATTR", $"{name}: the template has no such attribute (it is added as text)", path); continue; }
            if (Fixed(src, a)) { Add(StudioDiag.Warning, "FIXED_ATTR", $"{name} is the template's asset and stays as it is", path); continue; }
            var v = J.Str(value);
            if (v.Length == 0) continue;
            if (WebPanel.CheckValue(name, a.Value, v, out var clean) is { } bad) Add(StudioDiag.Blocker, "BAD_VALUE", bad, path);
            else if (name == "tier" && (!int.TryParse(clean, out var tier) || tier is < 0 or > 8)) Add(StudioDiag.Blocker, "BAD_TIER", "tier: 0..8", path);
            else if (name is "consumespell" && !ctx.SpellExists(clean)) Add(StudioDiag.Blocker, "NO_SPELL", $"no spell {clean}", path);
        }
        if (d["recipe"] is JsonObject recipe)
        {
            foreach (var (field, label) in new[] { ("silver", "silver"), ("time", "time"), ("focus", "focus") })
                if (J.Str(recipe[field]) is { Length: > 0 } v && (J.Num(recipe[field]) is not { } x || x < 0 || !double.IsFinite(x)))
                    Add(StudioDiag.Blocker, "BAD_NUMBER", $"recipe {label}: '{v}' is not a number ≥ 0", "/recipe/" + field);
            var res = J.Arr(recipe["resources"]);
            for (var i = 0; i < res.Count; i++)
            {
                var item = J.Str(res[i]?["item"]).Trim();
                var count = J.Num(res[i]?["count"]);
                if (item.Length == 0) { Add(StudioDiag.Blocker, "EMPTY_REF", $"ingredient {i + 1}: choose an item", $"/recipe/resources/{i}/item"); continue; }
                if (!ctx.ItemExists(item)) Add(StudioDiag.Blocker, "NO_ITEM", $"ingredient {item}: no such item", $"/recipe/resources/{i}/item", "pick it with the item picker");
                if (count is not { } c || c < 1 || c > 9999 || c != Math.Floor(c)) Add(StudioDiag.Blocker, "BAD_COUNT", $"ingredient {item}: a count of 1..9999", $"/recipe/resources/{i}/count");
            }
            if (res.Count == 0) Add(StudioDiag.Info, "NOT_CRAFTABLE", "no ingredients: the item is not craftable", "/recipe");
        }
        var stations = J.Arr(d["stations"]);
        for (var i = 0; i < stations.Count; i++)
            if (J.Str(stations[i]) is var st && !ctx.Catalog.StationEls.ContainsKey(st)) Add(StudioDiag.Blocker, "NO_STATION", $"no crafting station {st}", $"/stations/{i}");
        if (stations.Count > 0 && J.Arr(J.Obj(d["recipe"])["resources"]).Count == 0) Add(StudioDiag.Warning, "STATION_NO_RECIPE", "stations without ingredients: nothing to craft there", "/stations");
        var spells = J.Arr(d["spells"]);
        var slotsUsed = 0;
        for (var i = 0; i < spells.Count; i++)
        {
            var sp = J.Str(spells[i]?["spell"]).Trim();
            if (sp.Length == 0) { Add(StudioDiag.Blocker, "EMPTY_REF", $"spell {i + 1}: choose a spell", $"/spells/{i}/spell"); continue; }
            if (!ctx.SpellExists(sp)) Add(StudioDiag.Blocker, "NO_SPELL", $"no spell {sp}", $"/spells/{i}/spell");
            if (spells[i]?["slot"] is { } slotNode && J.Str(slotNode).Length > 0)
            {
                if (J.Num(slotNode) is not { } slot || slot < 1 || slot > 6 || slot != Math.Floor(slot)) Add(StudioDiag.Blocker, "BAD_SLOT", $"spell {sp}: slot 1..6 (empty = passive)", $"/spells/{i}/slot");
                else slotsUsed++;
            }
        }
        if (spells.Count > 30) Add(StudioDiag.Blocker, "TOO_MANY", "at most 30 spells", "/spells");
        if (src.Name.LocalName is "weapon" && d["spells"] is JsonArray && slotsUsed == 0) Add(StudioDiag.Warning, "NO_ACTIVE", "a weapon without active spells (slot 1-3)", "/spells");
        // The extra action bars (server/extraspells.json): active spells only, each once, at most 12.
        var extraSpells = J.Arr(d["extraSpells"]);
        for (var i = 0; i < extraSpells.Count; i++)
        {
            var sp = J.Str(extraSpells[i]).Trim();
            if (sp.Length == 0) { Add(StudioDiag.Blocker, "EMPTY_REF", $"extra ability {i + 1}: choose a spell", $"/extraSpells/{i}"); continue; }
            if (!ctx.SpellExists(sp)) Add(StudioDiag.Blocker, "NO_SPELL", $"no spell {sp}", $"/extraSpells/{i}");
            else if (ctx.FindSpell(sp) is { } found && found.El.Name.LocalName != "activespell")
                Add(StudioDiag.Blocker, "NOT_ACTIVE", $"{sp} is a {found.El.Name.LocalName}: only an activespell goes into the extra bars", $"/extraSpells/{i}");
            for (var k = 0; k < i; k++)
                if (string.Equals(J.Str(extraSpells[k]).Trim(), sp, StringComparison.OrdinalIgnoreCase)) Add(StudioDiag.Warning, "DUPLICATE", $"{sp} is in the list twice", $"/extraSpells/{i}");
        }
        if (extraSpells.Count > 12) Add(StudioDiag.Blocker, "TOO_MANY", "at most 12 extra abilities (two bars of six)", "/extraSpells");
        var drops = J.Arr(d["drops"]);
        for (var i = 0; i < drops.Count; i++)
        {
            var o = J.Obj(drops[i]);
            var mobs = J.StringList(o["mobs"]);
            var chests = J.StringList(o["chests"]);
            var cat = J.Str(o["category"]).Trim();
            if (mobs.Count == 0 && chests.Count == 0 && cat.Length == 0) Add(StudioDiag.Blocker, "DROP_NOWHERE", $"drop {i + 1}: give mobs, chests or a mob category", $"/drops/{i}");
            foreach (var m in mobs.Concat(chests).Append(cat).Where(m => m.Length > 0))
                if (!PackFiles.RefName().IsMatch(m)) Add(StudioDiag.Blocker, "BAD_PATTERN", $"drop {i + 1}: '{m}' is no name or pattern", $"/drops/{i}");
            for (var k = 0; k < mobs.Count; k++)
                if (!mobs[k].Contains('*') && !mobs[k].Contains('?') && !ctx.Catalog.Mobs.ContainsKey(mobs[k]) && !ctx.DraftExists("mob", mobs[k])) Add(StudioDiag.Warning, "NO_MOB", $"no mob {mobs[k]} in mobs.bin", $"/drops/{i}/mobs/{k}");
            if (J.Num(o["chance"]) is not { } ch || ch < 0 || ch > 1) Add(StudioDiag.Blocker, "BAD_CHANCE", $"drop {i + 1}: a chance of 0..1 (0.03 = 3 %)", $"/drops/{i}/chance");
            if (J.Str(o["amount"]).Length > 0 && (J.Num(o["amount"]) is not { } am || am < 1 || am > 999 || am != Math.Floor(am))) Add(StudioDiag.Blocker, "BAD_AMOUNT", $"drop {i + 1}: an amount of 1..999", $"/drops/{i}/amount");
        }
        if (d["enchantments"] is JsonNode ench && J.Str(ench) is not ("true" or "false")) Add(StudioDiag.Blocker, "BAD_VALUE", "enchantments: true or false", "/enchantments");
        else if (J.Str(d["enchantments"]) == "true" && src.Element("enchantments") is null) Add(StudioDiag.Info, "NO_ENCHANTMENTS", "the template has no enchantment levels (.1-.3): nothing to keep", "/enchantments");
        foreach (var (name, value) in J.Obj(d["attributes"]))
            if (name is "mountedbuff" or "halfmountedbuff" && J.Str(value) is { Length: > 0 } mb && !ctx.SpellExists(mb))
                Add(StudioDiag.Blocker, "NO_SPELL", $"{name}: no spell {mb}", "/attributes/" + name);
        // The look (SPEC §9.2): the template's model and icon unless the draft picks another official one.
        var hasModel = HasModel(src);
        if (J.Str(d["mesh"]).Trim() is { Length: > 0 } mesh)
        {
            if (!hasModel) Add(StudioDiag.Warning, "MODEL_NONE", $"mesh=\"{mesh}\": a {src.Name.LocalName} has no character model (ignored)", "/mesh");
            else if (!PackFiles.RefName().IsMatch(mesh) || !ctx.Catalog.ModelKnown(mesh))
                Add(StudioDiag.Blocker, "MODEL_UNKNOWN", $"mesh=\"{mesh}\" is no model of the client (template's: {ModBuilder.MeshOf(src)})", "/mesh");
        }
        if (J.Str(d["uisprite"]).Trim() is { Length: > 0 } sprite && !ctx.Catalog.Sprites.Contains(sprite))
            Add(StudioDiag.Blocker, "ICON_UNKNOWN", $"uisprite=\"{sprite}\" is no icon of an official item (template's: {ModBuilder.SpriteOf(src)})", "/uisprite");
        list.AddRange(StudioRecipes.ValidateGoal(ctx, d));
        if (!ctx.Icons.Has(ModBuilder.SpriteOf(src))) Add(StudioDiag.Info, "NO_ICON_CACHE", $"no cached icon for {ModBuilder.SpriteOf(src)} (the game shows it; run the modkit's icons once for the studio)", "");
        return list;
    }

    public StudioCompiled Compile(StudioContext ctx, JsonObject d)
    {
        var result = new StudioCompiled();
        var id = J.Str(d["id"]);
        var template = J.Str(d["template"]);
        var src = TemplateElement(ctx, d) ?? throw new InvalidDataException($"{id}: no template item {template}");
        var el = new XElement(src);
        el.SetAttributeValue("uniquename", id);
        el.SetAttributeValue("unlockedtocraft", "true");
        el.SetAttributeValue("unlockedtoequip", "true");
        foreach (var (name, value) in J.Obj(d["attributes"]))
        {
            var v = J.Str(value).Trim();
            if (v.Length == 0 || name.Length == 0 || !System.Xml.XmlReader.IsName(name)) continue;
            if (src.Attribute(name) is { } a && Fixed(src, a)) continue;
            if (src.Attribute(name) is { } old && WebPanel.CheckValue(name, old.Value, v, out _) is not null) continue;
            if (v.Length > 200 || v.Any(char.IsControl)) continue;
            el.SetAttributeValue(name, v);
        }
        el.Attribute("descriptionlocatag")?.Remove(); // the new item's own text
        var sprite = J.Str(d["uisprite"]).Trim();
        el.SetAttributeValue("uisprite", sprite.Length > 0 && ctx.Catalog.Sprites.Contains(sprite) ? sprite : ModBuilder.SpriteOf(src));
        if (HasModel(src))
        {
            var mesh = J.Str(d["mesh"]).Trim();
            el.SetAttributeValue("mesh", mesh.Length > 0 && PackFiles.RefName().IsMatch(mesh) && ctx.Catalog.ModelKnown(mesh) ? mesh : ModBuilder.MeshOf(src));
        }
        // Enchantment levels: none (the old editor's clone), or the template's .1-.3 kept with the new item (the levels above go).
        if (J.Str(d["enchantments"]) == "true")
            el.Elements("enchantments").Elements("enchantment").Where(e => !int.TryParse(e.Attribute("enchantmentlevel")?.Value, out var lv) || lv > 3).Remove();
        else el.Elements("enchantments").Remove();
        if (d["recipe"] is JsonObject recipe)
        {
            var req = new XElement("craftingrequirements");
            if (src.Element("craftingrequirements") is { } oldReq) req.Add(oldReq.Attributes());
            foreach (var (field, attr) in new[] { ("silver", "silver"), ("time", "time"), ("focus", "craftingfocus"), ("amount", "amountcrafted") })
                if (J.Num(recipe[field]) is { } x && x >= 0 && double.IsFinite(x)) req.SetAttributeValue(attr, J.Fmt(x));
            foreach (var r in J.Arr(recipe["resources"]).OfType<JsonObject>())
            {
                var item = J.Str(r["item"]).Trim();
                if (item.Length == 0 || !PackFiles.RefName().IsMatch(item)) continue;
                var count = (int)Math.Clamp(J.Num(r["count"]) ?? 1, 1, 9999);
                req.Add(new XElement("craftresource", new XAttribute("uniquename", item), new XAttribute("count", count)));
            }
            var olds = el.Elements("craftingrequirements").ToList();
            if (!req.HasElements) olds.ForEach(x => x.Remove());
            else if (olds.Count > 0)
            {
                olds[0].ReplaceWith(req);
                olds.Skip(1).ToList().ForEach(x => x.Remove());
            }
            else el.AddFirst(req);
        }
        if (d["spells"] is JsonArray spells)
        {
            var list = new XElement("craftingspelllist");
            foreach (var s in spells.OfType<JsonObject>().Take(30))
            {
                var sp = J.Str(s["spell"]).Trim();
                if (sp.Length == 0 || !PackFiles.RefName().IsMatch(sp)) continue;
                var cs = new XElement("craftspell", new XAttribute("uniquename", sp));
                if (J.Num(s["slot"]) is { } slot && slot is >= 1 and <= 6) cs.SetAttributeValue("slots", (int)slot);
                if (J.Str(s["tag"]) is { Length: > 0 } tag && System.Xml.XmlReader.IsName(tag)) cs.SetAttributeValue("tag", tag);
                list.Add(cs);
            }
            var old = el.Element("craftingspelllist");
            if (old is not null) old.ReplaceWith(list);
            else if (list.HasElements)
            {
                if (el.Elements("craftingrequirements").LastOrDefault() is { } after) after.AddAfterSelf(list);
                else el.AddFirst(list);
            }
            if (!list.HasElements && list.Parent is not null) list.Remove();
        }
        result.Add(PatchFile, el);
        var (en, ru) = J.Loc(d["name"]);
        var (den, dru) = J.Loc(d["description"]);
        result.Loc.Add(("@ITEMS_" + id, WebPanel.Clean(en, 200), WebPanel.Clean(ru, 200)));
        result.Loc.Add(("@ITEMS_" + id + "_DESC", WebPanel.Clean(den, 1000), WebPanel.Clean(dru, 1000)));
        if (d["stations"] is JsonArray stations)
        {
            var known = ctx.Catalog.StationsOf.GetValueOrDefault(template) ?? [];
            var list = new List<(string, XElement)>();
            foreach (var st in J.StringList(stations).Distinct(StringComparer.OrdinalIgnoreCase).Where(s => PackFiles.RefName().IsMatch(s)))
            {
                var model = known.FirstOrDefault(k => string.Equals(k.Station, st, StringComparison.OrdinalIgnoreCase)).CraftItem ?? known.FirstOrDefault().CraftItem;
                var ci = model is null ? new XElement("craftitem") : new XElement(model);
                ci.SetAttributeValue("uniquename", id);
                list.Add((st, ci));
            }
            result.Stations[id] = list;
        }
        var drops = new List<DropRow>();
        foreach (var o in J.Arr(d["drops"]).OfType<JsonObject>())
        {
            var mobs = J.StringList(o["mobs"]).Where(m => PackFiles.RefName().IsMatch(m)).ToArray();
            var chests = J.StringList(o["chests"]).Where(m => PackFiles.RefName().IsMatch(m)).ToArray();
            var cat = J.Str(o["category"]).Trim();
            if (mobs.Length == 0 && chests.Length == 0 && cat.Length == 0) continue;
            drops.Add(new DropRow(id, mobs, chests, cat.Length > 0 && PackFiles.RefName().IsMatch(cat) ? cat : null,
                Math.Clamp(J.Num(o["chance"]) ?? 0, 0, 1), (int)Math.Clamp(J.Num(o["amount"]) ?? 1, 1, 999)));
        }
        result.Drops[id] = drops;
        result.ExtraSpells[id] = [.. J.Arr(d["extraSpells"]).Select(x => J.Str(x).Trim().ToUpperInvariant()).Where(x => PackFiles.RefName().IsMatch(x)).Distinct().Take(12)];
        if (ctx.FindItem(template) is { Pack: null }) result.Templates[id] = template;
        return result;
    }

    private static readonly (string Attr, string Label)[] MainStats =
    [
        ("attackdamage", "attackdamage"), ("attackspeed", "attackspeed"), ("attackrange", "attackrange"), ("hitpointsmax", "hitpointsmax"),
        ("energymax", "energymax"), ("physicalarmor", "physicalarmor"), ("magicresistance", "magicresistance"), ("abilitypower", "abilitypower"),
        ("movespeedbonus", "movespeedbonus"), ("maxload", "maxload"), ("healbonus", "healbonus"), ("durability", "durability"), ("weight", "weight"),
        ("maxstacksize", "maxstacksize"),
    ];

    public JsonObject Tooltip(StudioContext ctx, JsonObject d, StudioCompiled compiled)
    {
        var el = compiled.Added.GetValueOrDefault(PatchFile)?.FirstOrDefault();
        var src = TemplateElement(ctx, d);
        var (en, ru) = J.Loc(d["name"]);
        var (den, dru) = J.Loc(d["description"]);
        var stats = new JsonArray();
        if (el is not null)
            foreach (var (attr, label) in MainStats)
                if (el.Attribute(attr)?.Value is { } v && v != "0" && v.Length > 0)
                {
                    var t = src?.Attribute(attr)?.Value;
                    stats.Add(new JsonObject { ["key"] = label, ["value"] = v, ["template"] = t, ["changed"] = t is not null && t != v });
                }
        var spells = new JsonArray();
        if (el is not null)
            foreach (var (sp, slot, _) in Spells(ctx, el).Take(30))
                spells.Add(SpellText.Describe(ctx, sp, slot));
        var sprite = el is null ? null : ModBuilder.SpriteOf(el);
        return new JsonObject
        {
            ["kind"] = "item", ["id"] = J.Str(d["id"]), ["name"] = J.LocNode(en, ru), ["description"] = J.LocNode(den, dru),
            ["icon"] = ctx.Icons.Has(sprite) ? StudioApi.IconUrl(sprite!) : null,
            ["tier"] = el?.Attribute("tier")?.Value, ["itemPower"] = el?.Attribute("itempower")?.Value ?? el?.Attribute("dummyitempower")?.Value,
            ["type"] = el?.Name.LocalName, ["slot"] = el?.Attribute("slottype")?.Value, ["category"] = el?.Attribute("shopsubcategory1")?.Value ?? el?.Attribute("shopcategory")?.Value,
            ["stats"] = stats, ["spells"] = spells,
            ["recipe"] = el?.Element("craftingrequirements") is { } req ? new JsonArray([.. req.Elements("craftresource").Select(c => (JsonNode)new JsonObject
            {
                ["item"] = c.Attribute("uniquename")?.Value, ["count"] = c.Attribute("count")?.Value,
                ["name"] = ctx.Names("@ITEMS_" + c.Attribute("uniquename")?.Value) is var (ien, iru) ? J.LocNode(ien, iru) : null,
                ["icon"] = ctx.FindItem(c.Attribute("uniquename")?.Value ?? "") is { } ci && ctx.Icons.ForItem(ci.El) is { } s ? StudioApi.IconUrl(s) : null,
            })]) : null,
        };
    }
}

/// <summary>A spell's player-facing text: its name and the official description with the {0} placeholders filled from its locareferences.</summary>
public static class SpellText
{
    private static XElement? Find(StudioContext ctx, string name) => ctx.DraftElement("spell", name) ?? ctx.FindSpell(name)?.El;

    public static JsonObject Describe(StudioContext ctx, string spell, string? slot)
    {
        var el = Find(ctx, spell);
        var dj = ctx.DraftElement("spell", spell) is null ? null
            : ctx.Drafts.FirstOrDefault(p => p.Key.Kind == "spell" && string.Equals(p.Key.Id, spell, StringComparison.OrdinalIgnoreCase)).Value;
        var draft = dj is not null;
        var nameKey = el is null ? "@SPELLS_" + spell : ModCatalog.SpellNameKey(el);
        var descKey = el is null ? "" : ModCatalog.SpellDescKey(el);
        var (en, ru) = dj is not null ? J.Loc(dj["name"]) : ctx.Names(nameKey);
        var (den, dru) = dj is not null ? J.Loc(dj["description"]) : descKey.Length > 0 ? ctx.Names(descKey) : (null, null);
        // A draft has no locareferences (they name the template's values): those of its template, read against the draft's own numbers.
        var refsFrom = el;
        var templateName = dj is null ? "" : J.Str(dj["template"]);
        if (dj is not null && ctx.FindSpell(templateName)?.El is { } template) refsFrom = template;
        var values = refsFrom?.Element("locareferences")?.Element("description")?.Elements("locareference")
            .Select(r => Resolve(ctx, r.Attribute("tag")?.Value ?? "", draft ? (templateName, spell) : null)).ToList() ?? [];
        return new JsonObject
        {
            ["spell"] = spell, ["slot"] = slot, ["found"] = el is not null, ["own"] = draft,
            ["kind"] = el?.Name.LocalName,
            ["name"] = J.LocNode(en ?? spell, ru),
            ["icon"] = el is not null && ctx.Icons.ForSpell(el) is { } s ? StudioApi.IconUrl(s) : null,
            ["cooldown"] = el?.Attribute("recastdelay")?.Value, ["energy"] = el?.Attribute("energyusage")?.Value,
            ["castTime"] = el?.Attribute("castingtime")?.Value, ["range"] = el?.Attribute("castrange")?.Value,
            ["description"] = J.LocNode(Fill(den, values), Fill(dru, values)),
        };
    }

    /// <summary>"$$HAMSTRINGSWORD_EFFECT.directattributechange[0].change$" → the value, formatted as the client would (abs, % for factors).</summary>
    private static string Resolve(StudioContext ctx, string tag, (string Template, string Draft)? rename)
    {
        var m = System.Text.RegularExpressions.Regex.Match(tag, @"^\$\$([A-Za-z0-9_@\-]+)((?:\.[A-Za-z0-9_]+\[\d+\])*)\.([A-Za-z0-9_]+)\$$");
        if (!m.Success) return "?";
        var spellName = m.Groups[1].Value;
        if (rename is { } rn && spellName.StartsWith(rn.Template, StringComparison.OrdinalIgnoreCase))
        {
            var mapped = rn.Draft + spellName[rn.Template.Length..];
            if (Find(ctx, mapped) is not null) spellName = mapped;
        }
        var at = Find(ctx, spellName);
        foreach (System.Text.RegularExpressions.Match step in System.Text.RegularExpressions.Regex.Matches(m.Groups[2].Value, @"\.([A-Za-z0-9_]+)\[(\d+)\]"))
            at = at?.Elements(step.Groups[1].Value).ElementAtOrDefault(int.Parse(step.Groups[2].Value, CultureInfo.InvariantCulture));
        var raw = at?.Attribute(m.Groups[3].Value)?.Value;
        if (raw is null) return "?";
        if (!double.TryParse(raw, NumberStyles.Float, CultureInfo.InvariantCulture, out var v)) return raw;
        var attr = m.Groups[3].Value;
        v = Math.Abs(v);
        return attr is "value" or "factor" or "percent" && v < 1 && v > 0 ? J.Fmt(v * 100) + "%" : J.Fmt(v);
    }

    private static string? Fill(string? text, List<string> values)
    {
        if (text is null) return null;
        var filled = System.Text.RegularExpressions.Regex.Replace(text, @"\{(\d+)\}", m => int.TryParse(m.Groups[1].Value, out var i) && i < values.Count ? values[i] : m.Value);
        return filled.Length > 2000 ? filled[..2000] : filled;
    }
}
