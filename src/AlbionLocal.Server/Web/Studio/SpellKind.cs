using System.Globalization;
using System.Text.Json.Nodes;
using System.Xml.Linq;

namespace AlbionLocal.Server.Web.Studio;

/// <summary>
/// A spell draft (studio SPEC §4.2): a copy of a template spell with root attribute overrides, nested attribute edits addressed by
/// element path ("2/0@value": the first child of the third block), its texts, and optionally its sub-spells cloned with it (named
/// after the new spell, the references rewired), as the old ability builder's "deep" clone.
/// </summary>
public sealed class SpellKind : IStudioKind
{
    public string Kind => "spell";
    public string PatchFile => "spells.xml";

    private static readonly HashSet<string> RootOwn = ["uniquename", "namelocatag", "descriptionlocatag"];

    public XElement? TemplateElement(StudioContext ctx, JsonObject draft) =>
        ctx.FindSpell(J.Str(draft["template"]))?.El ?? ctx.DraftElement("spell", J.Str(draft["template"]));

    /// <summary>The element at "2/0" (element-child indices) below <paramref name="root"/>, or null.</summary>
    public static XElement? At(XElement root, string path)
    {
        var at = root;
        foreach (var part in path.Split('/', StringSplitOptions.RemoveEmptyEntries))
        {
            if (!int.TryParse(part, NumberStyles.None, CultureInfo.InvariantCulture, out var i)) return null;
            at = at.Elements().ElementAtOrDefault(i);
            if (at is null) return null;
        }
        return at;
    }

    /// <summary>The path of <paramref name="e"/> below <paramref name="root"/> as element-child indices.</summary>
    public static string PathOf(XElement root, XElement e)
    {
        var parts = new List<int>();
        for (var at = e; at is not null && at != root; at = at.Parent) parts.Add(at.Parent?.Elements().ToList().IndexOf(at) ?? 0);
        parts.Reverse();
        return string.Join("/", parts);
    }

    /// <summary>The spells an element names (applyspell spell=…, effect, …): attribute values that are spell names.</summary>
    public static List<string> SpellRefs(StudioContext ctx, XElement e) =>
        [.. e.Descendants().SelectMany(d => d.Attributes()).Where(a => a.Name.LocalName is not ("uniquename" or "namelocatag" or "descriptionlocatag")
            && a.Value.Length > 2 && !WebPanel.IsNumber(a.Value) && !WebPanel.IsAsset(a.Parent!, a) && ctx.FindSpell(a.Value) is not null)
            .Select(a => a.Value).Distinct(StringComparer.OrdinalIgnoreCase)];

    /// <summary>Most blocks a draft adds from other spells.</summary>
    public const int MaxAddedBlocks = 20;

    /// <summary>The source element of an added block {from: spell, path: "1"} (official or a pack's spell), or null.</summary>
    public static XElement? AddedBlock(StudioContext ctx, JsonObject add)
    {
        var from = J.Str(add["from"]).Trim();
        var path = J.Str(add["path"]).Trim();
        if (from.Length == 0 || path.Length == 0) return null;
        var src = ctx.FindSpell(from)?.El ?? ctx.DraftElement("spell", from);
        return src is null ? null : At(WebPanel.PrepareSpell(src), path);
    }

    public JsonObject NewFrom(StudioContext ctx, string template, string id)
    {
        var t = ctx.FindSpell(template) ?? throw new InvalidDataException($"no spell {template}");
        var (en, ru) = ctx.Names(ModCatalog.SpellNameKey(t.El));
        var (den, dru) = ctx.Names(ModCatalog.SpellDescKey(t.El));
        return new JsonObject
        {
            ["schema"] = 1, ["kind"] = "spell", ["id"] = id, ["template"] = t.Name,
            ["name"] = J.LocNode(en is null ? id : en + " (copy)", ru is null ? "" : ru + " (копия)"),
            ["description"] = J.LocNode(den, dru),
            ["attributes"] = new JsonObject(),
            ["edits"] = new JsonObject(),
            ["subSpells"] = "official",
        };
    }

    public IEnumerable<StudioRef> References(JsonObject d)
    {
        if (J.Str(d["template"]) is { Length: > 0 } t) yield return new("spell", t, "/template");
        foreach (var (key, value) in J.Obj(d["edits"]))
            if (key.EndsWith("@spell", StringComparison.Ordinal) || key.EndsWith("@effect", StringComparison.Ordinal) || key.EndsWith("@endeffect", StringComparison.Ordinal))
                if (J.Str(value) is { Length: > 2 } v && PackFiles.NewName().IsMatch(v)) yield return new("spell", v, "/edits/" + StudioDiag.Seg(key));
        var added = J.Arr(J.Obj(d["blocks"])["add"]);
        for (var i = 0; i < added.Count; i++)
            if (J.Str(added[i]?["from"]) is { Length: > 0 } f) yield return new("spell", f, $"/blocks/add/{i}/from");
    }

    public int RenameRefs(JsonObject d, string kind, string from, string to)
    {
        if (kind != "spell") return 0;
        var n = 0;
        if (string.Equals(J.Str(d["template"]), from, StringComparison.OrdinalIgnoreCase)) { d["template"] = to; n++; }
        foreach (var bag in new[] { J.Obj(d["attributes"]), J.Obj(d["edits"]) })
            foreach (var key in bag.Select(p => p.Key).ToList())
                if (string.Equals(J.Str(bag[key]), from, StringComparison.OrdinalIgnoreCase)) { bag[key] = to; n++; }
        foreach (var add in J.Arr(J.Obj(d["blocks"])["add"]).OfType<JsonObject>())
            if (string.Equals(J.Str(add["from"]), from, StringComparison.OrdinalIgnoreCase)) { add["from"] = to; n++; }
        return n;
    }

    public List<StudioDiag> Validate(StudioContext ctx, JsonObject d)
    {
        var list = new List<StudioDiag>();
        var id = J.Str(d["id"]);
        void Add(string sev, string code, string msg, string path, string? hint = null) => list.Add(new(sev, code, msg, path, hint, "spell", id));
        if (!PackFiles.NewName().IsMatch(id)) Add(StudioDiag.Blocker, "BAD_ID", "the unique name: 3 to 80 of A-Z, 0-9 and _ (e.g. MY_DASH)", "/id");
        else if (ctx.FindSpell(id) is { } taken && taken.Pack != ctx.Pack)
            Add(StudioDiag.Blocker, "ID_TAKEN", $"a spell {id} exists already ({(taken.Pack is { } tp ? "in pack " + tp : "official")})", "/id");
        var src = TemplateElement(ctx, d);
        if (src is null) { Add(StudioDiag.Blocker, "NO_TEMPLATE", $"no template spell {J.Str(d["template"])}", "/template"); return list; }
        var el = WebPanel.PrepareSpell(src);
        var (en, ru) = J.Loc(d["name"]);
        if (en.Length == 0) Add(StudioDiag.Warning, "NO_NAME_EN", "no English name", "/name/en");
        if (ru.Length == 0) Add(StudioDiag.Warning, "NO_NAME_RU", "no Russian name", "/name/ru");
        foreach (var (name, value) in J.Obj(d["attributes"]))
            CheckAttr(el, name, J.Str(value), "/attributes/" + StudioDiag.Seg(name));
        foreach (var (key, value) in J.Obj(d["edits"]))
        {
            var path = "/edits/" + StudioDiag.Seg(key);
            var at = key.LastIndexOf('@');
            if (at < 0 || SpellKind.At(el, key[..at]) is not { } target) { Add(StudioDiag.Warning, "NO_ELEMENT", $"{key}: the template has no such element any more", path); continue; }
            CheckAttr(target, key[(at + 1)..], J.Str(value), path);
        }
        if (J.Str(d["subSpells"]) is not ("" or "official" or "clone")) Add(StudioDiag.Blocker, "BAD_VALUE", "subSpells: official or clone", "/subSpells");
        // Blocks taken out (template paths) and blocks added from other spells (studio phase 3).
        var blocks = J.Obj(d["blocks"]);
        var removed = J.StringList(blocks["remove"]);
        for (var i = 0; i < removed.Count; i++)
            if (At(el, removed[i]) is null) Add(StudioDiag.Warning, "NO_ELEMENT", $"remove {removed[i]}: the template has no such block", $"/blocks/remove/{i}");
        var added = J.Arr(blocks["add"]);
        if (added.Count > MaxAddedBlocks) Add(StudioDiag.Blocker, "TOO_MANY", $"at most {MaxAddedBlocks} added blocks", "/blocks/add");
        for (var i = 0; i < added.Count; i++)
        {
            var path = $"/blocks/add/{i}";
            var from = J.Str(added[i]?["from"]).Trim();
            if (AddedBlock(ctx, J.Obj(added[i])) is not { } block) { Add(StudioDiag.Blocker, "NO_BLOCK", $"added block {i + 1}: no block {J.Str(added[i]?["path"])} in spell {from}", path, "pick it again with Add a block"); continue; }
            foreach (var (name, value) in J.Obj(added[i]?["attributes"]))
            {
                var v = J.Str(value).Trim();
                if (v.Length == 0) continue;
                var apath = $"{path}/attributes/{StudioDiag.Seg(name)}";
                if (!System.Xml.XmlReader.IsName(name)) { Add(StudioDiag.Blocker, "BAD_VALUE", $"{name}: no attribute name", apath); continue; }
                if (block.Attribute(name) is not null) CheckAttr(block, name, v, apath);
                else if (v.Length > 200 || v.Any(char.IsControl)) Add(StudioDiag.Blocker, "BAD_VALUE", $"{name}: at most 200 characters", apath);
            }
        }
        list.AddRange(StudioRecipes.ValidateGoal(ctx, d));
        if (!ctx.Icons.Has(src.Attribute("uisprite")?.Value)) Add(StudioDiag.Info, "NO_ICON_CACHE", "no cached icon for this spell", "");
        return list;

        void CheckAttr(XElement e, string name, string v, string path)
        {
            if (v.Length == 0) return;
            if (e == el && RootOwn.Contains(name)) { Add(StudioDiag.Warning, "FIXED_ATTR", $"{name} is set by the studio", path); return; }
            if (e.Attribute(name) is not { } a) { Add(StudioDiag.Warning, "NEW_ATTR", $"{name}: the template has no such attribute", path); return; }
            if (WebPanel.IsAsset(e, a)) { Add(StudioDiag.Warning, "FIXED_ATTR", $"{name} is an asset and stays the template's", path); return; }
            if (WebPanel.CheckValue(name, a.Value, v, out var clean) is { } bad) { Add(StudioDiag.Blocker, "BAD_VALUE", bad, path); return; }
            if (name is "spell" or "effect" && !WebPanel.IsNumber(clean) && !ctx.SpellExists(clean)) Add(StudioDiag.Blocker, "NO_SPELL", $"{name}: no spell {clean}", path);
        }
    }

    public StudioCompiled Compile(StudioContext ctx, JsonObject d)
    {
        var result = new StudioCompiled();
        var id = J.Str(d["id"]);
        var template = J.Str(d["template"]);
        var src = TemplateElement(ctx, d) ?? throw new InvalidDataException($"{id}: no template spell {template}");
        var el = WebPanel.PrepareSpell(src);
        // Edits address the template's elements: resolve them all before anything moves.
        var edits = new List<(XElement El, string Attr, string Value)>();
        foreach (var (name, value) in J.Obj(d["attributes"])) edits.Add((el, name, J.Str(value).Trim()));
        foreach (var (key, value) in J.Obj(d["edits"]))
            if (key.LastIndexOf('@') is > 0 and var at && At(el, key[..at]) is { } target) edits.Add((target, key[(at + 1)..], J.Str(value).Trim()));
        foreach (var (e, name, v) in edits)
        {
            if (v.Length == 0 || !System.Xml.XmlReader.IsName(name) || (e == el && RootOwn.Contains(name))) continue;
            if (e.Attribute(name) is not { } a || WebPanel.IsAsset(e, a) || WebPanel.CheckValue(name, a.Value, v, out var clean) is not null) continue;
            a.Value = clean;
        }
        // Blocks: the removed ones (template paths, resolved before anything goes), then the added copies at the end.
        var blocks = J.Obj(d["blocks"]);
        foreach (var gone in J.StringList(blocks["remove"]).Where(p => p.Trim('/').Length > 0).Select(p => At(el, p)).OfType<XElement>().ToList()) gone.Remove();
        foreach (var add in J.Arr(blocks["add"]).OfType<JsonObject>().Take(MaxAddedBlocks))
        {
            if (AddedBlock(ctx, add) is not { } source) continue;
            var copy = new XElement(source);
            foreach (var (name, value) in J.Obj(add["attributes"]))
            {
                var v = J.Str(value).Trim();
                if (v.Length == 0 || v.Length > 200 || v.Any(char.IsControl) || !System.Xml.XmlReader.IsName(name)) continue;
                if (copy.Attribute(name) is { } a)
                {
                    if (WebPanel.IsAsset(copy, a) || WebPanel.CheckValue(name, a.Value, v, out var clean) is not null) continue;
                    a.Value = clean;
                }
                else copy.SetAttributeValue(name, v);
            }
            if (el.Elements("AudioInfo").FirstOrDefault() is { } audio) audio.AddBeforeSelf(copy);
            else el.Add(copy);
        }
        el.SetAttributeValue("uniquename", id);
        el.SetAttributeValue("namelocatag", "@SPELLS_" + id);
        el.SetAttributeValue("descriptionlocatag", "@SPELLS_" + id + "_DESC");
        var made = new List<(XElement El, string Template)>();
        var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase) { [template] = id };
        if (J.Str(d["subSpells"]) == "clone")
        {
            var queue = new Queue<(string Name, int Depth)>(SpellRefs(ctx, el).Select(n => (n, 1)));
            while (queue.Count > 0 && map.Count < 13)
            {
                var (sub, depth) = queue.Dequeue();
                if (map.ContainsKey(sub) || ctx.FindSpell(sub) is not { } ss) continue;
                var newName = sub.StartsWith(template + "_", StringComparison.OrdinalIgnoreCase) ? id + sub[template.Length..] : id + "_" + sub;
                if (newName.Length > 80) newName = newName[..80];
                if ((ctx.FindSpell(newName) is { } other && other.Pack != ctx.Pack) || map.ContainsValue(newName) || !PackFiles.NewName().IsMatch(newName)) continue;
                map[sub] = newName;
                var copy = WebPanel.PrepareSpell(ss.El);
                copy.SetAttributeValue("uniquename", newName);
                made.Add((copy, ss.Name));
                if (depth < 2) foreach (var n in SpellRefs(ctx, copy)) queue.Enqueue((n, depth + 1));
            }
        }
        foreach (var e in made.Select(m => m.El).Prepend(el))
            foreach (var a in e.DescendantsAndSelf().SelectMany(x => x.Attributes()).ToList())
                if (!(a.Parent == e && RootOwn.Contains(a.Name.LocalName)) && !WebPanel.IsAsset(a.Parent!, a) && map.TryGetValue(a.Value, out var to)) a.Value = to;
        result.Add(PatchFile, el);
        foreach (var (sub, t) in made)
        {
            result.Add(PatchFile, sub);
            if (ctx.FindSpell(t) is { Pack: null }) result.Templates[sub.Attribute("uniquename")!.Value] = t;
        }
        var (en, ru) = J.Loc(d["name"]);
        var (den, dru) = J.Loc(d["description"]);
        result.Loc.Add(("@SPELLS_" + id, WebPanel.Clean(en, 200), WebPanel.Clean(ru, 200)));
        result.Loc.Add(("@SPELLS_" + id + "_DESC", WebPanel.Clean(den, 1000), WebPanel.Clean(dru, 1000)));
        if (ctx.FindSpell(template) is { Pack: null }) result.Templates[id] = template;
        return result;
    }

    public JsonObject Tooltip(StudioContext ctx, JsonObject d, StudioCompiled compiled)
    {
        var id = J.Str(d["id"]);
        var described = SpellText.Describe(ctx, id, null);
        var el = compiled.Added.GetValueOrDefault(PatchFile)?.FirstOrDefault();
        var src = TemplateElement(ctx, d);
        var (en, ru) = J.Loc(d["name"]);
        var stats = new JsonArray();
        if (el is not null)
            foreach (var attr in new[] { "recastdelay", "energyusage", "castingtime", "castrange", "hitdelay", "channelduration", "maxcharges", "target", "category" })
                if (el.Attribute(attr)?.Value is { Length: > 0 } v)
                {
                    var t = src?.Attribute(attr)?.Value;
                    stats.Add(new JsonObject { ["key"] = attr, ["value"] = v, ["template"] = t, ["changed"] = t is not null && t != v });
                }
        var blocks = new JsonArray();
        if (el is not null)
            foreach (var b in el.Elements().Take(40))
                blocks.Add(new JsonObject
                {
                    ["element"] = b.Name.LocalName,
                    ["summary"] = string.Join(" ", b.Attributes().Where(a => !WebPanel.IsAsset(b, a)).Take(6).Select(a => $"{a.Name.LocalName}={a.Value}")),
                });
        var subs = compiled.Added.GetValueOrDefault(PatchFile)?.Skip(1).Select(e => e.Attribute("uniquename")?.Value).OfType<string>().ToList() ?? [];
        return new JsonObject
        {
            ["kind"] = "spell", ["id"] = id, ["name"] = J.LocNode(en, ru),
            ["description"] = described["description"]?.DeepClone(), ["icon"] = described["icon"]?.DeepClone(),
            ["type"] = el?.Name.LocalName, ["stats"] = stats, ["blocks"] = blocks, ["subSpells"] = J.Strings(subs),
        };
    }
}
