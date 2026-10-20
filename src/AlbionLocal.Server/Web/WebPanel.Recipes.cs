using System.Text.Json.Nodes;
using System.Xml.Linq;
using AlbionLocal.Server.Mods;
using AlbionLocal.Server.Web.Studio;

namespace AlbionLocal.Server.Web;

/// <summary>
/// The Studio's recipes, kits, value helper and the adoption of the classic editor's elements (studio SPEC, phase 3):
/// GET recipes · POST recipes/build {pack, recipe, values} (the drafts, not saved, with their problems, ranges and the main
/// draft's tooltip) · POST recipes/ranges {recipe, values} · GET ranges/&lt;item|spell&gt;?template= (official min/median/max per
/// numeric attribute) · POST packs/&lt;p&gt;/recipe/create {recipe, values, overwrite} · POST packs/&lt;p&gt;/adopt {kind, id}.
/// </summary>
public sealed partial class WebPanel
{
    private JsonObject RecipesJson(ModCatalog c, Overlay o)
    {
        var ctx = Context(c, o, "", []);
        return new JsonObject { ["recipes"] = new JsonArray([.. StudioRecipes.All.Select(r => (JsonNode)r.ToJson(ctx))]) };
    }

    /// <summary>A mob variant or loot list draft of some pack (pickers and badges show them as found).</summary>
    private JsonObject? PackDraft(string kind, string name)
    {
        if (!PackFiles.NewName().IsMatch(name)) return null;
        foreach (var pack in PackFiles.List(_modsDir))
        {
            if (PackFiles.Open(_modsDir, pack) is not { } p) continue;
            var store = new StudioStore(p);
            if (!store.Exists(kind, name.ToUpperInvariant()) && !store.Exists(kind, name)) continue;
            var d = store.Get(kind, store.Exists(kind, name) ? name : name.ToUpperInvariant());
            return new JsonObject { ["kind"] = kind, ["name"] = name, ["found"] = true, ["pack"] = pack, ["en"] = J.Loc(d?["name"]).En, ["ru"] = J.Loc(d?["name"]).Ru };
        }
        return null;
    }

    private (StudioContext Ctx, StudioRecipe Recipe, JsonObject Values)? RecipeInput(WebRequest r, ModCatalog c, Overlay o, out WebResponse? error)
    {
        var b = Body(r);
        error = null;
        if (StudioRecipes.Get(J.Str(b["recipe"])) is not { } recipe)
        {
            error = Json(400, Error($"no recipe '{J.Str(b["recipe"])}'"));
            return null;
        }
        var packName = J.Str(b["pack"]);
        var drafts = packName.Length > 0 && PackFiles.Open(_modsDir, packName) is { } p ? new StudioStore(p).All().Drafts : [];
        return (Context(c, o, packName, drafts), recipe, J.Obj(b["values"]));
    }

    /// <summary>The run's drafts checked as they would be saved next to the pack's (their ids counted as existing).</summary>
    private static List<StudioDiag> CheckRun(StudioContext ctx, RecipeRun run)
    {
        var problems = new List<StudioDiag>(run.Problems);
        foreach (var d in run.Drafts) ctx.Drafts[(J.Str(d["kind"]), J.Str(d["id"]))] = d;
        foreach (var d in run.Drafts)
            if (StudioKinds.Get(J.Str(d["kind"])) is { } k) problems.AddRange(k.Validate(ctx, d).Where(x => x.Code is not ("NO_ICON_CACHE")));
        return problems;
    }

    private WebResponse RecipeBuild(WebRequest r, ModCatalog c, Overlay o)
    {
        if (RecipeInput(r, c, o, out var error) is not { } input) return error!;
        var (ctx, recipe, values) = input;
        var run = StudioRecipes.Run(ctx, recipe, values);
        var problems = CheckRun(ctx, run);
        JsonObject? tooltip = null;
        if (run.Drafts.FirstOrDefault() is { } main && StudioKinds.Get(J.Str(main["kind"])) is { } k)
            try { tooltip = k.Tooltip(ctx, main, k.Compile(ctx, main)); }
            catch (Exception e) when (e is InvalidDataException or FormatException or InvalidOperationException or ArgumentException) { tooltip = null; }
        return Json(200, new JsonObject
        {
            ["recipe"] = recipe.Id, ["values"] = run.Values.DeepClone(), ["drafts"] = new JsonArray([.. run.Drafts.Select(d => (JsonNode)d.DeepClone())]),
            ["diagnostics"] = Diags(problems), ["ranges"] = StudioRecipes.Ranges(ctx, recipe, values), ["tooltip"] = tooltip,
            ["fields"] = new JsonArray([.. StudioRecipes.FieldsOf(recipe, run.Values).Select(f => (JsonNode)f.ToJson(ctx))]),
        });
    }

    private WebResponse RecipeRanges(WebRequest r, ModCatalog c, Overlay o)
    {
        if (RecipeInput(r, c, o, out var error) is not { } input) return error!;
        return Json(200, new JsonObject { ["ranges"] = StudioRecipes.Ranges(input.Ctx, input.Recipe, input.Values) });
    }

    /// <summary>
    /// The value helper of the generic editors: per numeric attribute of <paramref name="template"/> the official min/median/max —
    /// items of the same shop subcategory and tier, spells offered by items of the same category.
    /// </summary>
    private JsonObject AttrRanges(string kind, string template, ModCatalog c, Overlay o)
    {
        var result = new JsonObject();
        var ctx = Context(c, o, "", []);
        XElement? el = kind == "item" ? FindItem(c, o, template)?.El : kind == "spell" ? FindSpell(c, o, template)?.El : null;
        if (el is null) return result;
        var sub = el.Attribute("shopsubcategory1")?.Value;
        var tier = (int)(ModCatalog.Num(el, "tier") ?? 0);
        foreach (var a in el.Attributes().Where(a => IsNumber(a.Value) && !IsAsset(el, a)))
        {
            var name = a.Name.LocalName;
            var list = (kind == "item" ? StudioRecipes.ItemValues(ctx, name, sub, tier) : StudioRecipes.SpellRoot(ctx, name, el.Attribute("category")?.Value)).Order().ToList();
            if (list.Count < 2 || list[0] == list[^1]) continue;
            result[name] = new JsonObject { ["min"] = list[0], ["median"] = list[list.Count / 2], ["max"] = list[^1], ["n"] = list.Count };
        }
        return result;
    }

    private WebResponse RecipeCreate(WebRequest r, StudioStore store, StudioContext ctx, Dictionary<(string Kind, string Id), JsonObject> drafts, string who)
    {
        var b = Body(r);
        if (StudioRecipes.Get(J.Str(b["recipe"])) is not { } recipe) return Json(400, Error($"no recipe '{J.Str(b["recipe"])}'"));
        var run = StudioRecipes.Run(ctx, recipe, J.Obj(b["values"]));
        var problems = CheckRun(ctx, run);
        // The values' own blockers (a required field empty, a number out of range) and taken names stop the create; the rest is the drafts' to fix.
        var stop = problems.Where(p => p.Severity == StudioDiag.Blocker && (p.Path.StartsWith("/values/", StringComparison.Ordinal) || p.Code is "RECIPE" or "BAD_ID" or "ID_TAKEN")).ToList();
        if (run.Drafts.Count == 0 && stop.Count == 0) stop.Add(new StudioDiag(StudioDiag.Blocker, "RECIPE", "the recipe made nothing"));
        if (stop.Count > 0) return Json(400, Error(stop[0].Message, new JsonObject { ["diagnostics"] = Diags(problems) }));
        var overwrite = J.Str(b["overwrite"]) == "true";
        var existing = run.Drafts.Where(d => store.Exists(J.Str(d["kind"]), J.Str(d["id"]))).Select(d => $"{J.Str(d["kind"])}/{J.Str(d["id"])}").ToList();
        if (existing.Count > 0 && !overwrite)
            return Json(409, Error($"this pack has {string.Join(", ", existing)} already: change the name (prefix) or overwrite", new JsonObject { ["existing"] = J.Strings(existing) }));
        foreach (var d in run.Drafts) store.Save(d);
        Audit(who, r.Remote, $"studio: recipe {recipe.Id} made {string.Join(", ", run.Drafts.Select(d => $"{J.Str(d["kind"])}/{J.Str(d["id"])}"))} in pack {store.Pack.Name}");
        return Json(200, new JsonObject
        {
            ["ok"] = true, ["created"] = new JsonArray([.. run.Drafts.Select(d => (JsonNode)new JsonObject { ["kind"] = J.Str(d["kind"]), ["id"] = J.Str(d["id"]) })]),
            ["diagnostics"] = Diags(problems),
        });
    }

    /// <summary>
    /// A draft from an element the classic editor (/admin/mods) made: the template from editor.json, the attributes that differ,
    /// the recipe, spells, stations, texts and drops (items), the nested attribute edits when the structure is the template's (spells).
    /// Generate then writes the same element from the draft.
    /// </summary>
    private WebResponse Adopt(WebRequest r, StudioStore store, StudioContext ctx, string who)
    {
        var b = Body(r);
        var kind = J.Str(b["kind"]);
        var id = J.Str(b["id"]);
        if (kind is not ("item" or "spell")) return Json(400, Error("adopt: kind item or spell"));
        var el = PackFiles.Added(store.Pack.Patch(kind == "item" ? "items.xml" : "spells.xml"), id);
        if (el is null) return Json(404, Error($"no new {kind} {id} in the pack's patch files"));
        if (store.Exists(kind, id)) return Json(409, Error($"this pack has a {kind} draft {id} already"));
        var template = store.Pack.Templates().GetValueOrDefault(id);
        if (template is null || (kind == "item" ? ctx.FindItem(template) : (object?)ctx.FindSpell(template)) is null)
            return Json(400, Error($"{id}: no template recorded in editor.json (hand-written?): make a studio copy from the catalogue instead"));
        var notes = new List<string>();
        JsonObject draft;
        var loc = store.Pack.Patch("localization.xml");
        if (kind == "item")
        {
            var tpl = ctx.FindItem(template)!.El;
            draft = new ItemKind().NewFrom(ctx, template, id);
            var attrs = new JsonObject();
            foreach (var a in el.Attributes())
            {
                var n = a.Name.LocalName;
                if (n is "uniquename" or "unlockedtocraft" or "unlockedtoequip" or "descriptionlocatag" or "mesh" or "uisprite" || IsAsset(el, a)) continue;
                if (tpl.Attribute(n)?.Value != a.Value) attrs[n] = a.Value;
            }
            draft["attributes"] = attrs;
            if (el.Element("craftingrequirements") is { } req)
                draft["recipe"] = new JsonObject
                {
                    ["silver"] = req.Attribute("silver")?.Value ?? "0", ["time"] = req.Attribute("time")?.Value ?? "1", ["focus"] = req.Attribute("craftingfocus")?.Value ?? "0",
                    ["amount"] = req.Attribute("amountcrafted")?.Value,
                    ["resources"] = new JsonArray([.. req.Elements("craftresource").Select(cr => (JsonNode)new JsonObject
                    {
                        ["item"] = cr.Attribute("uniquename")?.Value ?? "", ["count"] = int.TryParse(cr.Attribute("count")?.Value, out var cnt) ? cnt : 1,
                    })]),
                };
            else draft["recipe"] = new JsonObject { ["silver"] = "0", ["time"] = "1", ["focus"] = "0", ["amount"] = null, ["resources"] = new JsonArray() };
            draft["spells"] = new JsonArray([.. ItemKind.Spells(ctx, el).Select(s => (JsonNode)new JsonObject { ["spell"] = s.Spell, ["slot"] = int.TryParse(s.Slot, out var sl) ? sl : null, ["tag"] = s.Tag })]);
            draft["enchantments"] = el.Element("enchantments") is not null;
            if (store.Pack.Has("buildings.xml")) draft["stations"] = J.Strings(PackFiles.StationsOf(store.Pack.Patch("buildings.xml"), id));
            draft["drops"] = new JsonArray([.. store.Pack.Drops().Where(d => string.Equals(d.Item, id, StringComparison.OrdinalIgnoreCase)).Select(d => (JsonNode)new JsonObject
            {
                ["mobs"] = J.Strings(d.Mobs), ["chests"] = J.Strings(d.Chests), ["category"] = d.Category ?? "", ["chance"] = d.Chance, ["amount"] = d.Amount,
            })]);
            var (en, ru) = PackFiles.Loc(loc, "@ITEMS_" + id);
            var (den, dru) = PackFiles.Loc(loc, "@ITEMS_" + id + "_DESC");
            if (en is not null || ru is not null) draft["name"] = J.LocNode(en, ru);
            if (den is not null || dru is not null) draft["description"] = J.LocNode(den, dru);
        }
        else
        {
            var tpl = PrepareSpell(ctx.FindSpell(template)!.El);
            var mine = PrepareSpell(el);
            draft = new SpellKind().NewFrom(ctx, template, id);
            var attrs = new JsonObject();
            foreach (var a in mine.Attributes())
                if (a.Name.LocalName is not ("uniquename" or "namelocatag" or "descriptionlocatag") && !IsAsset(mine, a) && tpl.Attribute(a.Name)?.Value != a.Value) attrs[a.Name.LocalName] = a.Value;
            draft["attributes"] = attrs;
            var a1 = tpl.Descendants().ToList();
            var a2 = mine.Descendants().ToList();
            var edits = new JsonObject();
            if (a1.Count == a2.Count && a1.Zip(a2).All(p => p.First.Name == p.Second.Name))
            {
                foreach (var (t, m) in a1.Zip(a2))
                    foreach (var a in m.Attributes().Where(a => !IsAsset(m, a) && t.Attribute(a.Name)?.Value != a.Value))
                        edits[$"{SpellKind.PathOf(mine, m)}@{a.Name.LocalName}"] = a.Value;
            }
            else notes.Add("the effect blocks differ from the template's: only the root attributes were adopted (check the blocks)");
            draft["edits"] = edits;
            var subs = PackFiles.Added(store.Pack.Patch("spells.xml")).Select(e => e.Attribute("uniquename")?.Value ?? "").Where(n => n.StartsWith(id + "_", StringComparison.OrdinalIgnoreCase));
            if (subs.Any()) draft["subSpells"] = "clone";
            var (en, ru) = PackFiles.Loc(loc, "@SPELLS_" + id);
            var (den, dru) = PackFiles.Loc(loc, "@SPELLS_" + id + "_DESC");
            if (en is not null || ru is not null) draft["name"] = J.LocNode(en, ru);
            if (den is not null || dru is not null) draft["description"] = J.LocNode(den, dru);
        }
        store.Save(draft);
        Audit(who, r.Remote, $"studio: adopted the classic editor's {kind} {id} into a draft in pack {store.Pack.Name}");
        ctx.Drafts[(kind, id)] = draft;
        return Json(200, new JsonObject
        {
            ["ok"] = true, ["kind"] = kind, ["id"] = id, ["notes"] = J.Strings(notes),
            ["diagnostics"] = Diags(StudioKinds.Get(kind)!.Validate(ctx, draft)),
        });
    }
}
