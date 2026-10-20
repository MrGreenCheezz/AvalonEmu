using System.Globalization;
using System.Net;
using System.Text;
using System.Text.RegularExpressions;
using System.Xml.Linq;
using AlbionLocal.Photon;
using AlbionLocal.Server.Mods;

namespace AlbionLocal.Server.Web;

/// <summary>
/// The panel's Mods section (analysis/work/stageN/modeditor/SPEC.md): a catalogue of the official items and spells, the content
/// packs in Mods:Path, "clone &amp; edit item", the ability builder, and the modkit's check, build (install into game-local) and
/// restore. Pack files are written by <see cref="PackFiles"/> (only mods/&lt;pack&gt;/); game-local is written only by the modkit's
/// own safe install (<see cref="ModKit.Build"/>, <see cref="ModKit.Restore"/>); the official install is only read. Long modkit runs
/// are background jobs (one at a time) whose page refreshes itself. No scripts: plain forms and links.
/// </summary>
public sealed partial class WebPanel
{
    private readonly ModPaths? _modPaths;
    private readonly string _modsDir;
    private readonly string? _modsProblem;
    private readonly object _catalogLock = new();
    private Task<ModCatalog>? _catalog;
    private readonly object _jobLock = new();
    private ModJob? _job;
    private const int PageSize = 50;

    private sealed class ModJob
    {
        public required string What { get; init; }
        public string? Pack { get; init; }
        public bool Install { get; init; }
        public DateTime Started { get; } = DateTime.UtcNow;
        public volatile bool Done;
        public bool Ok { get; set; }
        public List<string> Errors { get; } = [];
        public List<string> Warnings { get; } = [];
        public List<string> Notes { get; } = [];
        public string Output { get; set; } = "";
        public List<string> Packs { get; set; } = [];
    }

    // ---- set-up

    private static (ModPaths? Paths, string? Problem) ModSetup(ModPaths? given, string clientGameData, string modsDir)
    {
        ModPaths? paths = given;
        if (paths is null)
            try { paths = Directory.Exists(clientGameData) ? ModPaths.For(clientGameData) : null; }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException or ArgumentException) { paths = null; }
        var mods = Path.GetFullPath(modsDir);
        if (paths is not null)
            foreach (var guarded in new[] { paths.OfficialRoot, Path.GetFullPath(Path.Combine(paths.Local, "..", "..", "..")) })
                try { SafeFiles.GuardNotOfficial(Path.Combine(mods, "x"), guarded); }
                catch (InvalidOperationException) { return (paths, $"Mods:Path {mods} lies inside the game folder {guarded}: the editor does not write there"); }
        return (paths, null);
    }

    /// <summary>The catalogue: null while it loads (in the background, once), or the error that stopped it.</summary>
    private (ModCatalog? Catalog, string? Problem) Catalog()
    {
        var folder = _modPaths?.Official ?? _world.Config.ClientGameDataPath;
        Task<ModCatalog> task;
        lock (_catalogLock) task = _catalog ??= Task.Run(() => ModCatalog.Load(folder));
        if (!task.IsCompleted) return (null, null);
        if (task.IsFaulted)
        {
            lock (_catalogLock) _catalog = null; // the next request tries again
            var e = task.Exception!.GetBaseException();
            Log.Error("web", $"mod catalogue from {folder}: {e.Message}");
            return (null, $"the catalogue could not be read from {folder}: {e.Message}");
        }
        return (task.Result, null);
    }

    private WebResponse CatalogWait(AdminSession s, string? problem) => problem is not null
        ? Page("Mods", Note(problem, true), s, 503)
        : Page("Mods", "<div class=card><h2>Loading the catalogue…</h2><p class=muted>The official items, spells and their names are being read "
            + "(items.bin, spells.bin, the 78 MB localization, buildings, mobs, loot): about 10-30 s, once. This page reloads itself.</p></div>", s, 200,
            "<meta http-equiv=refresh content=3>");

    /// <summary>What every pack adds: its new items and spells (tagged with the pack) and its texts.</summary>
    private sealed record Overlay(List<CatItem> Items, List<CatSpell> Spells, Dictionary<string, (string? En, string? Ru)> Loc, Dictionary<string, List<DropRow>> Drops);

    private Overlay PackOverlay()
    {
        var o = new Overlay([], [], new(StringComparer.OrdinalIgnoreCase), new(StringComparer.OrdinalIgnoreCase));
        foreach (var name in PackFiles.List(_modsDir))
        {
            if (PackFiles.Open(_modsDir, name) is not { } p) continue;
            try
            {
                foreach (var e in PackFiles.Added(p.Patch("items.xml")))
                    if (e.Attribute("uniquename")?.Value is { Length: > 0 } n)
                        o.Items.Add(new CatItem(n, e.Name.LocalName, int.TryParse(e.Attribute("tier")?.Value, out var t) ? t : 0, e.Attribute("slottype")?.Value ?? "", e, name));
                foreach (var e in PackFiles.Added(p.Patch("spells.xml")))
                    if (e.Attribute("uniquename")?.Value is { Length: > 0 } n) o.Spells.Add(new CatSpell(n, e.Name.LocalName, e, name));
                foreach (var entry in p.Patch("localization.xml").Root!.Elements("entry"))
                    if (entry.Attribute("tuid")?.Value is { } tuid) o.Loc[tuid] = PackFiles.Loc(entry.Document!, tuid);
                foreach (var d in p.Drops())
                {
                    if (!o.Drops.TryGetValue(d.Item, out var list)) o.Drops[d.Item] = list = [];
                    list.Add(d with { Item = name }); // Item = the pack, for the listing
                }
            }
            catch (Exception e) when (e is InvalidDataException or IOException) { Log.Warn("web", $"mods/{name}: {e.Message}"); }
        }
        return o;
    }

    private static (string? En, string? Ru) Names(ModCatalog c, Overlay o, string tuid) =>
        o.Loc.TryGetValue(tuid, out var p) ? p : c.Loc.GetValueOrDefault(tuid);

    private static CatItem? FindItem(ModCatalog c, Overlay o, string name) =>
        c.ItemByName.GetValueOrDefault(name) ?? o.Items.FirstOrDefault(i => string.Equals(i.Name, name, StringComparison.OrdinalIgnoreCase));

    private static CatSpell? FindSpell(ModCatalog c, Overlay o, string name) =>
        c.SpellByName.GetValueOrDefault(name) ?? o.Spells.FirstOrDefault(i => string.Equals(i.Name, name, StringComparison.OrdinalIgnoreCase));

    // ---- dispatch

    private async Task<WebResponse> Mods(WebRequest r, AdminSession s)
    {
        try
        {
            return (r.Method, r.Path) switch
            {
                ("GET", "/admin/mods") => await ModsHome(s),
                ("GET", "/admin/mods/items") => ItemsPage(r, s),
                ("GET", "/admin/mods/item") => ItemPage(r, s),
                ("GET", "/admin/mods/spells") => SpellsPage(r, s),
                ("GET", "/admin/mods/spell") => SpellPage(r, s),
                ("GET", "/admin/mods/pack") => PackPage(r, s),
                ("GET", "/admin/mods/preview") => PreviewPage(r, s),
                ("GET", "/admin/mods/job") => JobPage(s),
                ("GET", "/admin/mods/item/edit") => ItemEdit(r, s, null, null),
                ("GET", "/admin/mods/spell/edit") => SpellEdit(r, s, null, null),
                ("POST", "/admin/mods/create") => CreatePack(r, s),
                ("POST", "/admin/mods/item/save") => ItemSave(r, s),
                ("POST", "/admin/mods/spell/save") => SpellSave(r, s),
                ("POST", "/admin/mods/spell/block") => SpellBlock(r, s),
                ("POST", "/admin/mods/remove") => RemoveFromPack(r, s),
                ("POST", "/admin/mods/check" or "/admin/mods/install" or "/admin/mods/restore") => StartModJob(r, s),
                _ => Text(404, "not found"),
            };
        }
        catch (Exception e) when (e is InvalidDataException or IOException or UnauthorizedAccessException)
        {
            Log.Warn("web", $"mods: {r.Method} {r.Path}: {e.Message}");
            return Page("Mods", Note(e.Message, true), s, 400);
        }
    }

    private WebResponse Back(AdminSession s, string to, string text, bool error)
    {
        Flash(s, text, error);
        return WebResponse.Redirect(to);
    }

    private static string Enc(string v) => Uri.EscapeDataString(v);

    // ---- overview

    private async Task<WebResponse> ModsHome(AdminSession s)
    {
        var packs = PackFiles.List(_modsDir);
        var body = new StringBuilder("<div class=card><h2>Mods</h2><p>Own items and abilities made of the client's official assets "
            + "(models, icons, effects are reused). Packs live in <code>").Append(H(_modsDir)).Append("</code>; installing builds them into game-local with the modkit "
            + "(<code>tools/AlbionLocal.ModKit</code>, MODDING.md).</p><p class=\"note ok\"><b>New editor:</b> <a href=\"/studio\">open the Studio</a> — pickers with icons, \"make a copy\" from the catalogue, live preview and problems. These pages stay as the classic editor.</p><p><a href=\"/admin/mods/items\">Item catalogue</a> · <a href=\"/admin/mods/spells\">Spell catalogue</a>"
            + (_job is not null ? " · <a href=\"/admin/mods/job\">Last modkit run</a>" : "") + "</p>");
        if (_modsProblem is not null) body.Append(Note(_modsProblem, true));
        if (_modPaths is null) body.Append(Note("No official GameData found next to Paths:ClientGameData (expected Reverse/game beside Reverse/game-local): browsing and editing work, check and install do not.", true));
        body.Append("</div><div class=card><h3>Packs (").Append(packs.Count).Append(")</h3>");
        if (packs.Count > 0)
        {
            body.Append("<div class=scroll><table><tr><th>Pack</th><th>Version</th><th>Description</th><th>Items</th><th>Spells</th></tr>");
            foreach (var name in packs)
            {
                if (PackFiles.Open(_modsDir, name) is not { } p) continue;
                var info = p.Info();
                int items = 0, spells = 0;
                try { items = PackFiles.Added(p.Patch("items.xml")).Count(); spells = PackFiles.Added(p.Patch("spells.xml")).Count(); }
                catch (InvalidDataException) { }
                body.Append($"<tr><td><a href=\"/admin/mods/pack?name={name}\">{H(name)}</a></td><td>{H(info.GetValueOrDefault("Version"))}</td>")
                    .Append($"<td>{H(info.GetValueOrDefault("Description"))}</td><td class=num>{items}</td><td class=num>{spells}</td></tr>");
            }
            body.Append("</table></div>");
        }
        body.Append("<h3>New pack</h3>").Append(Form(s, "/admin/mods/create", "Create pack", "", null,
            "<input name=name required pattern=\"[a-z0-9][a-z0-9\\-]{0,39}\" maxlength=40 placeholder=\"name: a-z 0-9 -\" aria-label=\"pack name\">"
            + "<input name=version required maxlength=20 value=\"1.0.0\" style=\"width:7em\" aria-label=version>"
            + "<input name=description maxlength=300 placeholder=\"description\" aria-label=description>"));
        body.Append("</div>");
        body.Append("<div class=card><h3>Installed in game-local</h3>");
        if (_modPaths is null) body.Append("<p class=muted>Unknown (no official GameData found).</p>");
        else
        {
            var status = await Task.Run(() =>
            {
                var w = new StringWriter();
                try { new ModKit(_modPaths, w).Status(); }
                catch (Exception e) when (e is IOException or InvalidDataException or UnauthorizedAccessException) { w.WriteLine("status failed: " + e.Message); }
                return w.ToString();
            });
            body.Append("<pre class=log>").Append(H(status)).Append("</pre>")
                .Append("<p class=muted>Restore puts the official files back into game-local (hard links again). Restart the server and the game client afterwards.</p>")
                .Append(Form(s, "/admin/mods/restore", "Restore official files", "danger", null,
                    "<label><input type=checkbox name=confirm value=1 required> I will restart the server and the client</label>"));
        }
        return Page("Mods", body.Append("</div>").ToString(), s);
    }

    private WebResponse CreatePack(WebRequest r, AdminSession s)
    {
        if (_modsProblem is not null) return Back(s, "/admin/mods", _modsProblem, true);
        var name = r.F("name").Trim();
        var error = PackFiles.Create(_modsDir, name, r.F("version").Trim(), Clean(r.F("description"), 300), _modPaths is null ? null : ModBuilder.ReadBuild(_modPaths.Official));
        if (error is not null) return Back(s, "/admin/mods", error, true);
        Audit($"admin '{s.Name}'", r.Remote, $"mods: created pack {name}");
        return Back(s, $"/admin/mods/pack?name={name}", $"pack {name} created", false);
    }

    /// <summary>A text field: control characters (newlines too) become spaces, trimmed, cut at <paramref name="max"/>.</summary>
    internal static string Clean(string text, int max)
    {
        var t = new string([.. text.Select(ch => char.IsControl(ch) ? ' ' : ch)]).Trim();
        return t.Length > max ? t[..max] : t;
    }

    // ---- catalogue

    private string PackSelect(string? selected) =>
        "<select name=pack aria-label=pack>" + string.Concat(PackFiles.List(_modsDir).Select(p => $"<option{(p == selected ? " selected" : "")}>{H(p)}</option>")) + "</select>";

    private static string Pager(string basePath, IReadOnlyDictionary<string, string> query, int page, int pages)
    {
        if (pages <= 1) return "";
        string Link(int p) => basePath + "?" + string.Join("&", query.Where(q => q.Key != "page" && q.Value.Length > 0).Select(q => $"{Enc(q.Key)}={Enc(q.Value)}").Append($"page={p}"));
        var sb = new StringBuilder("<p class=pager>");
        if (page > 1) sb.Append($"<a href=\"{H(Link(page - 1))}\">‹ previous</a> ");
        sb.Append($"page {page} of {pages}");
        if (page < pages) sb.Append($" <a href=\"{H(Link(page + 1))}\">next ›</a>");
        return sb.Append("</p>").ToString();
    }

    private static string Options(IEnumerable<string> values, string selected, string all = "all") =>
        $"<option value=\"\">{H(all)}</option>" + string.Concat(values.Select(v => $"<option{(string.Equals(v, selected, StringComparison.OrdinalIgnoreCase) ? " selected" : "")}>{H(v)}</option>"));

    private static string Stats(XElement e)
    {
        var parts = new List<string>();
        foreach (var (attr, label) in new[] { ("itempower", "IP"), ("attackdamage", "dmg"), ("attackspeed", "spd"), ("attackrange", "range"), ("durability", "dur"), ("weight", "kg") })
            if (e.Attribute(attr)?.Value is { } v) parts.Add($"{label} {v}");
        return string.Join(" · ", parts);
    }

    private static string Recipe(XElement e) => e.Element("craftingrequirements") is { } req
        ? string.Join(", ", req.Elements("craftresource").Select(c => $"{c.Attribute("count")?.Value}× {c.Attribute("uniquename")?.Value}"))
        : "";

    private WebResponse ItemsPage(WebRequest r, AdminSession s)
    {
        var (c, problem) = Catalog();
        if (c is null) return CatalogWait(s, problem);
        var o = PackOverlay();
        var q = r.Q("q").Trim();
        var tier = r.Q("tier");
        var kind = r.Q("kind");
        var slot = r.Q("slot");
        IEnumerable<CatItem> all = c.Items.Concat(o.Items);
        if (int.TryParse(tier, out var t)) all = all.Where(i => i.Tier == t);
        if (kind.Length > 0) all = all.Where(i => i.Kind == kind);
        if (slot.Length > 0) all = all.Where(i => i.Slot == slot);
        if (q.Length > 0)
            all = all.Where(i => i.Name.Contains(q, StringComparison.OrdinalIgnoreCase)
                || Names(c, o, "@ITEMS_" + i.Name) is var (en, ru) && ((en?.Contains(q, StringComparison.OrdinalIgnoreCase) ?? false) || (ru?.Contains(q, StringComparison.OrdinalIgnoreCase) ?? false)));
        var list = all.ToList();
        var pages = Math.Max(1, (list.Count + PageSize - 1) / PageSize);
        var page = int.TryParse(r.Q("page"), out var pg) ? Math.Clamp(pg, 1, pages) : 1;
        var body = new StringBuilder("<div class=card><h2>Item catalogue</h2><form method=get action=\"/admin/mods/items\" class=inline>")
            .Append($"<input name=q value=\"{H(q)}\" placeholder=\"name or text (EN/RU)\" aria-label=search>")
            .Append("<select name=tier aria-label=tier>").Append(Options(Enumerable.Range(0, 9).Select(i => i.ToString()), tier, "any tier")).Append("</select>")
            .Append("<select name=kind aria-label=kind>").Append(Options(c.Items.Select(i => i.Kind).Distinct().Order(), kind, "any kind")).Append("</select>")
            .Append("<select name=slot aria-label=slot>").Append(Options(c.Items.Select(i => i.Slot).Where(x => x.Length > 0).Distinct().Order(), slot, "any slot")).Append("</select>")
            .Append("<button>Search</button></form>")
            .Append($"<p class=muted>{list.Count} of {c.Items.Count + o.Items.Count} items (official data{(c.Build is { } b ? " " + H(b) : "")}{(o.Items.Count > 0 ? $" + {o.Items.Count} from packs" : "")}).</p>")
            .Append(IconHint())
            .Append("<div class=scroll><table class=small><tr><th>Item</th><th>Name EN / RU</th><th>T</th><th>Kind · slot</th><th>Stats</th><th>Spells</th><th>Recipe</th><th>Drops</th></tr>");
        foreach (var i in list.Skip((page - 1) * PageSize).Take(PageSize))
        {
            var (en, ru) = Names(c, o, "@ITEMS_" + i.Name);
            var spells = i.El.Descendants("craftspell").Count();
            var drops = c.DropSources(i.Name, 1000).Count + (o.Drops.GetValueOrDefault(i.Name)?.Count ?? 0);
            body.Append($"<tr><td>{ItemIcon(i.El)}<a href=\"/admin/mods/item?name={Enc(i.Name)}\">{H(i.Name)}</a>{(i.Pack is { } pk ? $" <span class=tag>{H(pk)}</span>" : "")}</td>")
                .Append($"<td>{H(en)}<br><span class=muted>{H(ru)}</span></td><td>{i.Tier}</td><td>{H(i.Kind)}{(i.Slot.Length > 0 ? " · " + H(i.Slot) : "")}</td>")
                .Append($"<td>{H(Stats(i.El))}</td><td class=num>{(spells > 0 ? spells : "")}</td><td>{H(Recipe(i.El))}</td><td class=num>{(drops > 0 ? drops : "")}</td></tr>");
        }
        body.Append("</table></div>").Append(Pager("/admin/mods/items", r.Query, page, pages));
        return Page("Items", body.Append("</div>").ToString(), s);
    }

    private static string XmlBlock(XElement e, string summary = "XML")
    {
        var text = e.ToString();
        if (text.Length > 200_000) text = text[..200_000] + "\n…";
        return $"<details><summary>{H(summary)}</summary><pre class=xml>{H(text)}</pre></details>";
    }

    private WebResponse ItemPage(WebRequest r, AdminSession s)
    {
        var (c, problem) = Catalog();
        if (c is null) return CatalogWait(s, problem);
        var o = PackOverlay();
        if (FindItem(c, o, r.Q("name")) is not { } i) return Text(404, "no such item");
        var (en, ru) = Names(c, o, "@ITEMS_" + i.Name);
        var (den, dru) = Names(c, o, "@ITEMS_" + i.Name + "_DESC");
        var body = new StringBuilder($"<div class=card><h2>{ItemIcon(i.El, 64)}{H(en ?? i.Name)}</h2>{IconHint()}<table>")
            .Append(Row("Unique name", i.Name)).Append(Row("Name RU", ru ?? "")).Append(Row("Description", den ?? "")).Append(Row("Описание", dru ?? ""))
            .Append(Row("Kind · slot", $"{i.Kind} · {i.Slot}")).Append(Row("Tier", i.Tier.ToString()))
            .Append(Row("Model · icon", $"{(i.Kind is "weapon" or "equipmentitem" ? ModBuilder.MeshOf(i.El) + "_01" : "-")} · {ModBuilder.SpriteOf(i.El)}"))
            .Append(i.Pack is { } pk ? Row("From pack", $"<a href=\"/admin/mods/pack?name={pk}\">{H(pk)}</a>", raw: true) : "")
            .Append("</table>");
        if (PackFiles.List(_modsDir).Count > 0)
            body.Append("<form method=get action=\"/admin/mods/item/edit\" class=inline><input type=hidden name=template value=\"").Append(H(i.Name)).Append("\">")
                .Append(PackSelect(null)).Append("<button>Clone &amp; edit into pack</button></form>");
        else body.Append("<p><a href=\"/admin/mods\">Create a pack</a> to clone this item.</p>");
        body.Append("</div><div class=card><h3>Attributes</h3><div class=scroll><table class=small>");
        foreach (var a in i.El.Attributes().Where(a => !a.IsNamespaceDeclaration)) body.Append(Row(a.Name.LocalName, a.Value));
        body.Append("</table></div></div><div class=card><h3>Spells it offers</h3>");
        var spells = i.El.Descendants("craftspell").ToList();
        if (spells.Count == 0) body.Append("<p class=muted>None.</p>");
        else
        {
            body.Append("<table class=small><tr><th>Slot</th><th>Spell</th><th>Name</th></tr>");
            foreach (var cs in spells)
            {
                var n = cs.Attribute("uniquename")?.Value ?? "";
                var sp = FindSpell(c, o, n);
                body.Append($"<tr><td>{H(cs.Attribute("slots")?.Value ?? "passive")}</td><td>{(sp is null ? "" : SpellIcon(sp.El, 24))}<a href=\"/admin/mods/spell?name={Enc(n)}\">{H(n)}</a></td>")
                    .Append($"<td>{H(sp is null ? "(unknown spell)" : Names(c, o, ModCatalog.SpellNameKey(sp.El)).En)}</td></tr>");
            }
            body.Append("</table>");
        }
        body.Append("</div><div class=card><h3>Crafting</h3>");
        var reqs = i.El.Elements("craftingrequirements").ToList();
        if (reqs.Count == 0) body.Append("<p class=muted>Not craftable.</p>");
        foreach (var req in reqs)
            body.Append($"<p>silver {H(req.Attribute("silver")?.Value)}, time {H(req.Attribute("time")?.Value)}, focus {H(req.Attribute("craftingfocus")?.Value)}: ")
                .Append(string.Join(", ", req.Elements("craftresource").Select(cr => $"{H(cr.Attribute("count")?.Value)}× <a href=\"/admin/mods/item?name={Enc(cr.Attribute("uniquename")?.Value ?? "")}\">{H(cr.Attribute("uniquename")?.Value)}</a>")))
                .Append("</p>");
        var stations = c.StationsOf.GetValueOrDefault(i.Name)?.Select(x => x.Station).ToList() ?? [];
        body.Append($"<p>Stations: {(stations.Count > 0 ? H(string.Join(", ", stations)) : "<span class=muted>none in buildings.bin</span>")}</p></div>");
        body.Append("<div class=card><h3>Where it drops on the server</h3>");
        var sources = c.DropSources(i.Name);
        var lists = c.LootLists(i.Name);
        body.Append(sources.Count == 0 ? "<p class=muted>No official mob or chest loot names it.</p>"
            : $"<p>Official loot ({sources.Count}{(sources.Count >= 60 ? "+" : "")} mobs/chests, through {lists.Count} loot list(s)): {H(string.Join(", ", sources))}</p>");
        if (o.Drops.GetValueOrDefault(i.Name) is { Count: > 0 } pd)
            body.Append("<p>Pack drops (server/drops.json, when the pack is in Mods:Packs): ")
                .Append(H(string.Join("; ", pd.Select(d => $"{d.Item}: {DropText(d)}")))).Append("</p>");
        body.Append("</div><div class=card>").Append(XmlBlock(i.El)).Append("</div>");
        return Page(i.Name, body.ToString(), s);
    }

    private static string DropText(DropRow d) =>
        string.Join(", ", [.. d.Mobs, .. d.Chests.Select(x => "chest " + x), .. d.Category is { Length: > 0 } cat ? new[] { "category " + cat } : []])
        + $" at {d.Chance * 100:0.###} % ×{d.Amount}";

    private WebResponse SpellsPage(WebRequest r, AdminSession s)
    {
        var (c, problem) = Catalog();
        if (c is null) return CatalogWait(s, problem);
        var o = PackOverlay();
        var q = r.Q("q").Trim();
        var kind = r.Q("kind");
        var cat = r.Q("category");
        var offered = r.Q("offered") == "1";
        IEnumerable<CatSpell> all = c.Spells.Concat(o.Spells);
        if (kind.Length > 0) all = all.Where(x => x.Kind == kind);
        if (cat.Length > 0) all = all.Where(x => x.El.Attribute("category")?.Value == cat);
        if (offered) all = all.Where(x => c.OfferedBy.ContainsKey(x.Name) || x.Pack is not null);
        if (q.Length > 0)
            all = all.Where(x => x.Name.Contains(q, StringComparison.OrdinalIgnoreCase)
                || Names(c, o, ModCatalog.SpellNameKey(x.El)) is var (en, ru) && ((en?.Contains(q, StringComparison.OrdinalIgnoreCase) ?? false) || (ru?.Contains(q, StringComparison.OrdinalIgnoreCase) ?? false)));
        var list = all.ToList();
        var pages = Math.Max(1, (list.Count + PageSize - 1) / PageSize);
        var page = int.TryParse(r.Q("page"), out var pg) ? Math.Clamp(pg, 1, pages) : 1;
        var body = new StringBuilder("<div class=card><h2>Spell catalogue</h2><form method=get action=\"/admin/mods/spells\" class=inline>")
            .Append($"<input name=q value=\"{H(q)}\" placeholder=\"name or text (EN/RU)\" aria-label=search>")
            .Append("<select name=kind aria-label=kind>").Append(Options(["activespell", "passivespell", "togglespell"], kind, "any kind")).Append("</select>")
            .Append("<select name=category aria-label=category>").Append(Options(c.Spells.Select(x => x.El.Attribute("category")?.Value ?? "").Where(x => x.Length > 0).Distinct().Order(), cat, "any category")).Append("</select>")
            .Append($"<label><input type=checkbox name=offered value=1{(offered ? " checked" : "")}> offered by an item</label><button>Search</button></form>")
            .Append($"<p class=muted>{list.Count} of {c.Spells.Count + o.Spells.Count} spells.</p>").Append(IconHint())
            .Append("<div class=scroll><table class=small><tr><th>Spell</th><th>Name EN / RU</th><th>Kind · category</th><th>Cooldown</th><th>Energy</th><th>Cast</th><th>Range</th><th>Items</th></tr>");
        foreach (var x in list.Skip((page - 1) * PageSize).Take(PageSize))
        {
            var (en, ru) = Names(c, o, ModCatalog.SpellNameKey(x.El));
            body.Append($"<tr><td>{SpellIcon(x.El)}<a href=\"/admin/mods/spell?name={Enc(x.Name)}\">{H(x.Name)}</a>{(x.Pack is { } pk ? $" <span class=tag>{H(pk)}</span>" : "")}</td>")
                .Append($"<td>{H(en)}<br><span class=muted>{H(ru)}</span></td><td>{H(x.Kind)} · {H(x.El.Attribute("category")?.Value)}</td>")
                .Append($"<td class=num>{H(x.El.Attribute("recastdelay")?.Value)}</td><td class=num>{H(x.El.Attribute("energyusage")?.Value)}</td>")
                .Append($"<td class=num>{H(x.El.Attribute("castingtime")?.Value)}</td><td class=num>{H(x.El.Attribute("castrange")?.Value)}</td>")
                .Append($"<td class=num>{(c.OfferedBy.GetValueOrDefault(x.Name)?.Count is { } n and > 0 ? n : "")}</td></tr>");
        }
        body.Append("</table></div>").Append(Pager("/admin/mods/spells", r.Query, page, pages));
        return Page("Spells", body.Append("</div>").ToString(), s);
    }

    /// <summary>The spells an element names (applyspell spell, effect, endeffect, …): attribute values that are spell names.</summary>
    private static List<string> SpellRefs(ModCatalog c, Overlay o, XElement e) =>
        [.. e.Descendants().SelectMany(d => d.Attributes()).Where(a => a.Name.LocalName is not ("uniquename" or "namelocatag" or "descriptionlocatag")
            && a.Value.Length > 2 && !double.TryParse(a.Value, NumberStyles.Float, CultureInfo.InvariantCulture, out _) && FindSpell(c, o, a.Value) is not null)
            .Select(a => a.Value).Distinct(StringComparer.OrdinalIgnoreCase)];

    private static string BlockSummary(XElement b)
    {
        var attrs = b.Attributes().Take(8).Select(a => $"{a.Name.LocalName}={a.Value}");
        var inner = b.Descendants().Count();
        return $"<{b.Name.LocalName}> {string.Join(" ", attrs)}{(b.Attributes().Count() > 8 ? " …" : "")}{(inner > 0 ? $" (+{inner} inner)" : "")}";
    }

    private WebResponse SpellPage(WebRequest r, AdminSession s)
    {
        var (c, problem) = Catalog();
        if (c is null) return CatalogWait(s, problem);
        var o = PackOverlay();
        if (FindSpell(c, o, r.Q("name")) is not { } x) return Text(404, "no such spell");
        var (en, ru) = Names(c, o, ModCatalog.SpellNameKey(x.El));
        var (den, dru) = Names(c, o, ModCatalog.SpellDescKey(x.El));
        var body = new StringBuilder($"<div class=card><h2>{SpellIcon(x.El, 64)}{H(en ?? x.Name)}</h2>{IconHint()}<table>")
            .Append(Row("Unique name", x.Name)).Append(Row("Name RU", ru ?? "")).Append(Row("Description", den ?? "")).Append(Row("Описание", dru ?? ""))
            .Append(Row("Kind", x.Kind)).Append(Row("Icon", x.El.Attribute("uisprite")?.Value ?? "-"))
            .Append(Row("Offered by", string.Join(", ", c.OfferedBy.GetValueOrDefault(x.Name) ?? [])))
            .Append(x.Pack is { } pk ? Row("From pack", $"<a href=\"/admin/mods/pack?name={pk}\">{H(pk)}</a>", raw: true) : "")
            .Append("</table>");
        if (PackFiles.List(_modsDir).Count > 0)
            body.Append("<form method=get action=\"/admin/mods/spell/edit\" class=inline><input type=hidden name=template value=\"").Append(H(x.Name)).Append("\">")
                .Append(PackSelect(null)).Append("<button>New ability from this spell</button></form>");
        body.Append("</div><div class=card><h3>Attributes</h3><div class=scroll><table class=small>");
        foreach (var a in x.El.Attributes()) body.Append(Row(a.Name.LocalName, a.Value));
        body.Append("</table></div><h3>Effect blocks</h3><ol class=small>");
        foreach (var b in x.El.Elements().Where(b => b.Name.LocalName != "locareferences")) body.Append("<li><code>").Append(H(BlockSummary(b))).Append("</code></li>");
        body.Append("</ol><p>Spells it uses: ").Append(string.Join(", ", SpellRefs(c, o, x.El).Select(n => $"<a href=\"/admin/mods/spell?name={Enc(n)}\">{H(n)}</a>"))).Append("</p></div>");
        body.Append("<div class=card>").Append(XmlBlock(x.El)).Append("</div>");
        return Page(x.Name, body.ToString(), s);
    }

    // ---- pack page and preview

    private WebResponse PackPage(WebRequest r, AdminSession s)
    {
        if (PackFiles.Open(_modsDir, r.Q("name")) is not { } p) return Text(404, "no such pack");
        var info = p.Info();
        var items = p.Patch("items.xml");
        var spells = p.Patch("spells.xml");
        var loc = p.Patch("localization.xml");
        var templates = p.Templates();
        var body = new StringBuilder($"<div class=card><h2>Pack {H(p.Name)}</h2><table>")
            .Append(Row("Version", info.GetValueOrDefault("Version") ?? "")).Append(Row("Client build", info.GetValueOrDefault("Client") ?? ""))
            .Append(Row("Description", info.GetValueOrDefault("Description") ?? "")).Append(Row("Folder", p.Dir)).Append("</table>")
            .Append($"<form method=get action=\"/admin/mods/item/edit\" class=inline><input type=hidden name=pack value=\"{p.Name}\">")
            .Append("<input name=template required maxlength=100 placeholder=\"template item, e.g. T6_MAIN_SWORD\" aria-label=\"template item\"><button>Clone &amp; edit item</button></form>")
            .Append($"<form method=get action=\"/admin/mods/spell/edit\" class=inline><input type=hidden name=pack value=\"{p.Name}\">")
            .Append("<input name=template required maxlength=100 placeholder=\"template spell, e.g. HAMSTRINGSWORD\" aria-label=\"template spell\"><button>New ability from spell</button></form>")
            .Append("<p class=muted>Find templates in the <a href=\"/admin/mods/items\">item</a> and <a href=\"/admin/mods/spells\">spell</a> catalogues.</p></div>");
        body.Append("<div class=card><h3>New items</h3>");
        var newItems = PackFiles.Added(items).ToList();
        if (newItems.Count == 0) body.Append("<p class=muted>None.</p>");
        else
        {
            body.Append("<div class=scroll><table class=small><tr><th>Item</th><th>Name EN / RU</th><th>Kind</th><th>Template</th><th></th></tr>");
            foreach (var e in newItems)
            {
                var n = e.Attribute("uniquename")?.Value ?? "";
                var (en, ru) = PackFiles.Loc(loc, "@ITEMS_" + n);
                body.Append($"<tr><td>{ItemIcon(e, 24)}{H(n)}</td><td>{H(en)}<br><span class=muted>{H(ru)}</span></td><td>{H(e.Name.LocalName)}</td><td>{H(templates.GetValueOrDefault(n))}</td><td>")
                    .Append($"<a href=\"/admin/mods/item/edit?pack={p.Name}&amp;name={Enc(n)}\">Edit</a> ")
                    .Append(Form(s, "/admin/mods/remove", "Remove", "danger", ("pack", p.Name), $"<input type=hidden name=kind value=item><input type=hidden name=name value=\"{H(n)}\">"))
                    .Append("</td></tr>");
            }
            body.Append("</table></div>");
        }
        body.Append("<h3>New spells</h3>");
        var newSpells = PackFiles.Added(spells).ToList();
        if (newSpells.Count == 0) body.Append("<p class=muted>None.</p>");
        else
        {
            body.Append("<div class=scroll><table class=small><tr><th>Spell</th><th>Name EN / RU</th><th>Template</th><th></th></tr>");
            foreach (var e in newSpells)
            {
                var n = e.Attribute("uniquename")?.Value ?? "";
                var (en, ru) = PackFiles.Loc(loc, ModCatalog.SpellNameKey(e));
                body.Append($"<tr><td>{SpellIcon(e, 24)}{H(n)}</td><td>{H(en)}<br><span class=muted>{H(ru)}</span></td><td>{H(templates.GetValueOrDefault(n))}</td><td>")
                    .Append($"<a href=\"/admin/mods/spell/edit?pack={p.Name}&amp;name={Enc(n)}\">Edit</a> ")
                    .Append(Form(s, "/admin/mods/remove", "Remove", "danger", ("pack", p.Name), $"<input type=hidden name=kind value=spell><input type=hidden name=name value=\"{H(n)}\">"))
                    .Append("</td></tr>");
            }
            body.Append("</table></div>");
        }
        var other = OtherChanges(p);
        body.Append("<h3>Other changes</h3>").Append(other.Count == 0 ? "<p class=muted>None.</p>" : "<ul class=small>" + string.Concat(other.Select(x => $"<li>{H(x)}</li>")) + "</ul>");
        var drops = p.Drops();
        body.Append("<h3>Drops (server/drops.json)</h3>").Append(drops.Count == 0 ? "<p class=muted>None.</p>"
            : "<ul class=small>" + string.Concat(drops.Select(d => $"<li>{H(d.Item)}: {H(DropText(d))}</li>")) + "</ul>");
        body.Append("</div>");
        // Actions
        body.Append("<div class=card><h3>Validate, preview, install</h3>")
            .Append(Form(s, "/admin/mods/check", "Validate (modkit check)", "", ("pack", p.Name), ""))
            .Append($" <a href=\"/admin/mods/preview?pack={p.Name}\">Preview the patch files and the differences to the templates</a>");
        if (_modPaths is null) body.Append(Note("No official GameData found: check and install are not available.", true));
        else
        {
            var state = SafeState();
            var installed = state?.Packs.Select(x => x.Split(' ')[0]).ToHashSet(StringComparer.Ordinal) ?? [];
            var boxes = string.Concat(PackFiles.List(_modsDir).Select(n =>
                $"<label><input type=checkbox name=\"pk_{n}\" value=1{(n == p.Name || installed.Contains(n) ? " checked" : "")}> {H(n)}</label>"));
            body.Append("<div class=warnbox><b>Install</b> builds the checked packs (a build replaces the previous one: check every pack you want) from the official "
                + "files into game-local with the modkit. <b>Afterwards restart the server and the game client</b>: both read the game data only at their start. "
                + "The panel does not restart the server. For a pack's drops add it to <code>Mods:Packs</code> before the restart.</div>")
                .Append(Form(s, "/admin/mods/install", "Install into game-local", "danger", ("pack", p.Name),
                    "<span class=boxes>" + boxes + "</span><label><input type=checkbox name=confirm value=1 required> I will restart the server and the client</label>"));
        }
        return Page($"Pack {p.Name}", body.Append("</div>").ToString(), s);
    }

    private ModState? SafeState()
    {
        if (_modPaths is null) return null;
        try { return ModState.Load(_modPaths.Local); }
        catch (InvalidDataException) { return null; }
    }

    /// <summary>The pack's operations besides the new items and spells, one line each.</summary>
    private static List<string> OtherChanges(PackFiles p)
    {
        var lines = new List<string>();
        foreach (var file in Directory.GetFiles(p.Dir, "*.xml").Select(Path.GetFileName).OfType<string>().Order())
        {
            XDocument doc;
            try { doc = PackFiles.PatchFiles.Contains(file) ? p.Patch(file) : XDocument.Parse(File.ReadAllText(Path.Combine(p.Dir, file))); }
            catch (Exception e) when (e is System.Xml.XmlException or InvalidDataException) { lines.Add($"{file}: not readable ({e.Message})"); continue; }
            if (doc.Root is null) continue;
            if (file == "localization.xml") { lines.Add($"localization.xml: {doc.Root.Elements("entry").Count()} text entries"); continue; }
            foreach (var op in doc.Root.Elements())
            {
                var key = op.Attribute("uniquename")?.Value;
                var path = op.Attribute("path")?.Value;
                var at = key is null ? "" : key + (path is null ? "" : "/" + path);
                switch (op.Name.LocalName)
                {
                    case "add" when key is null:
                        if (file is not ("items.xml" or "spells.xml")) lines.Add($"{file}: new {string.Join(", ", op.Elements().Select(e => $"<{e.Name.LocalName}> {e.Attribute("uniquename")?.Value}"))}");
                        break;
                    case "add":
                        lines.Add($"{file}: into {at}: {string.Join(", ", op.Elements().Select(e => $"<{e.Name.LocalName} {e.Attribute("uniquename")?.Value}>"))}");
                        break;
                    case "set":
                        lines.Add($"{file}: {at} {op.Attribute("attr")?.Value} = {(op.Attribute("unset")?.Value == "true" ? "(removed)" : op.Attribute("value")?.Value)}");
                        break;
                    default:
                        lines.Add($"{file}: {op.Name.LocalName} {at}");
                        break;
                }
            }
        }
        return lines;
    }

    private WebResponse PreviewPage(WebRequest r, AdminSession s)
    {
        if (PackFiles.Open(_modsDir, r.Q("pack")) is not { } p) return Text(404, "no such pack");
        var (c, problem) = Catalog();
        var body = new StringBuilder($"<div class=card><h2>Preview of {H(p.Name)}</h2><p class=muted>What the modkit reads: the patch files as they are on disk, "
            + "and for every element made from a template the lines that differ from it (- template, + new).</p></div>");
        if (c is not null)
        {
            var o = PackOverlay();
            var templates = p.Templates();
            foreach (var file in new[] { "items.xml", "spells.xml" })
                foreach (var e in PackFiles.Added(p.Patch(file)))
                {
                    var n = e.Attribute("uniquename")?.Value ?? "";
                    if (templates.GetValueOrDefault(n) is not { } t) continue;
                    XElement? te = file == "items.xml" ? c.ItemByName.GetValueOrDefault(t)?.El : c.SpellByName.GetValueOrDefault(t)?.El;
                    if (te is null) continue;
                    body.Append($"<div class=card><h3>{H(n)} <span class=muted>from {H(t)}</span></h3>").Append(Diff(te.ToString(), e.ToString())).Append("</div>");
                }
        }
        else if (problem is not null) body.Append(Note(problem, true));
        else body.Append(Note("the catalogue is still loading: the differences show when it is ready (reload)", false));
        foreach (var file in PackFiles.PatchFiles.Append(Path.Combine("server", "drops.json")).Append("pack.json"))
        {
            var text = file.EndsWith(".xml") ? (p.Has(file) ? p.Raw(file) : "") : File.Exists(Path.Combine(p.Dir, file)) ? File.ReadAllText(Path.Combine(p.Dir, file)) : "";
            if (text.Length == 0) continue;
            body.Append($"<div class=card><details open><summary>{H(file.Replace('\\', '/'))}</summary><pre class=xml>{H(text.Length > 300_000 ? text[..300_000] + "\n…" : text)}</pre></details></div>");
        }
        return Page($"Preview {p.Name}", body.ToString(), s);
    }

    /// <summary>A line diff (longest common subsequence) of two texts, only the changed lines and two lines around them.</summary>
    internal static string Diff(string a, string b)
    {
        var x = a.Split('\n').Select(l => l.TrimEnd('\r')).ToArray();
        var y = b.Split('\n').Select(l => l.TrimEnd('\r')).ToArray();
        if ((long)x.Length * y.Length > 4_000_000) return "<p class=muted>too large to compare here</p>";
        var lcs = new int[x.Length + 1, y.Length + 1];
        for (var i = x.Length - 1; i >= 0; i--)
            for (var j = y.Length - 1; j >= 0; j--)
                lcs[i, j] = x[i] == y[j] ? lcs[i + 1, j + 1] + 1 : Math.Max(lcs[i + 1, j], lcs[i, j + 1]);
        var ops = new List<(char Op, string Line)>();
        int p = 0, q = 0;
        while (p < x.Length || q < y.Length)
        {
            if (p < x.Length && q < y.Length && x[p] == y[q]) { ops.Add((' ', x[p])); p++; q++; }
            else if (q < y.Length && (p >= x.Length || lcs[p, q + 1] >= lcs[p + 1, q])) ops.Add(('+', y[q++]));
            else ops.Add(('-', x[p++]));
        }
        var keep = new bool[ops.Count];
        for (var i = 0; i < ops.Count; i++)
            if (ops[i].Op != ' ')
                for (var k = Math.Max(0, i - 2); k <= Math.Min(ops.Count - 1, i + 2); k++) keep[k] = true;
        if (!keep.Contains(true)) return "<p class=muted>no difference</p>";
        var sb = new StringBuilder("<pre class=diff>");
        var gap = false;
        for (var i = 0; i < ops.Count; i++)
        {
            if (!keep[i]) { if (!gap) sb.Append("<span class=muted>…</span>\n"); gap = true; continue; }
            gap = false;
            var cls = ops[i].Op switch { '+' => "add", '-' => "del", _ => "" };
            sb.Append(cls.Length > 0 ? $"<span class={cls}>" : "<span>").Append(ops[i].Op).Append(' ').Append(H(ops[i].Line)).Append("</span>\n");
        }
        return sb.Append("</pre>").ToString();
    }

    // ---- generic attribute fields

    [GeneratedRegex("prefab|sprite|mesh|anim|audio|sound|vfx|icon|atlas|texture|material|model|clip", RegexOptions.IgnoreCase)]
    private static partial Regex AssetAttr();

    internal static bool IsAsset(XElement e, XAttribute a) =>
        AssetAttr().IsMatch(a.Name.LocalName) || (e.Name.LocalName is "AudioInfo" or "SocketPreset" or "AssetVfxPreset" or "FootStepVfxPreset" && a.Name.LocalName == "name");

    internal static bool IsNumber(string v) => double.TryParse(v, NumberStyles.Float, CultureInfo.InvariantCulture, out _);

    /// <summary>Attributes the client reads as decimals although a template may hold a whole number ("recastdelay=2" → 2.5 is fine).</summary>
    internal static readonly HashSet<string> FloatAttrs = new(StringComparer.OrdinalIgnoreCase)
    {
        "recastdelay", "castingtime", "castrange", "hitdelay", "standtime", "energyusage", "change", "value", "time", "radius", "effectarearadius",
        "movespeed", "distance", "interval", "initialinterval", "damageabsorbed", "factor", "attackdamage", "attackspeed", "attackrange", "weight",
        "abilitypower", "physicalarmor", "magicresistance", "hitpointsmax", "hitpointsregenerationbonus", "energymax", "mounthitpointsmax", "maxload",
        "ttl", "timewindow", "healbonus", "threatbonus", "movespeedbonus", "crowdcontrolresistance", "mounttime",
    };

    /// <summary>A new value for an attribute whose template value is <paramref name="old"/>: same type (number, true/false, text), at most 200 characters.</summary>
    internal static string? CheckValue(string attr, string old, string value, out string clean)
    {
        clean = value.Trim();
        if (clean.Length > 200 || clean.Any(char.IsControl)) return $"{attr}: at most 200 characters, no control characters";
        if (IsNumber(old))
        {
            if (!double.TryParse(clean, NumberStyles.Float, CultureInfo.InvariantCulture, out var d) || !double.IsFinite(d)) return $"{attr}: '{clean}' is not a number (use . for decimals)";
            if (!old.Contains('.') && !old.Contains('E') && !old.Contains('e') && clean.Contains('.') && d != Math.Floor(d) && !FloatAttrs.Contains(attr.Split(' ')[^1]))
                return $"{attr}: a whole number (the template has {old})";
        }
        else if (old is "true" or "false" && clean is not ("true" or "false")) return $"{attr}: true or false";
        return null;
    }

    private static string Input(string name, string value, string label, bool readOnly = false, string extra = "") =>
        $"<label><span>{H(label)}</span><input name=\"{H(name)}\" value=\"{H(value)}\"{(readOnly ? " readonly class=ro" : "")}{extra}></label>";

    private static string Datalist(string id, IEnumerable<string> values) =>
        $"<datalist id={id}>" + string.Concat(values.Distinct(StringComparer.OrdinalIgnoreCase).Take(5000).Select(v => $"<option value=\"{H(v)}\">")) + "</datalist>";

    // ---- items: clone & edit

    private WebResponse ItemEdit(WebRequest r, AdminSession s, IReadOnlyDictionary<string, string>? posted, string? error)
    {
        var (c, problem) = Catalog();
        if (c is null) return CatalogWait(s, problem);
        var o = PackOverlay();
        var get = posted ?? r.Query;
        string V(string key, string dflt) => posted is not null ? posted.GetValueOrDefault(key, dflt) : dflt;
        var packName = get.GetValueOrDefault("pack") ?? "";
        var editName = get.GetValueOrDefault("name");
        PackFiles? pack = PackFiles.Open(_modsDir, packName);
        XElement src;
        string template;
        bool clone;
        if (editName is { Length: > 0 } && posted?.GetValueOrDefault("mode") != "clone")
        {
            if (pack is null) return Text(404, "no such pack");
            src = PackFiles.Added(pack.Patch("items.xml"), editName) ?? throw new InvalidDataException($"{pack.Name} adds no item {editName}");
            template = pack.Templates().GetValueOrDefault(editName) ?? "";
            clone = false;
        }
        else
        {
            template = (get.GetValueOrDefault("template") ?? "").Trim();
            if (FindItem(c, o, template) is not { } t) return Text(404, $"no item '{template}' to clone (search the item catalogue)");
            src = t.El;
            template = t.Name;
            clone = true;
        }
        var name = clone ? V("uniquename", "") : editName!;
        var locPack = pack?.Patch("localization.xml");
        var (en, ru) = clone ? Names(c, o, "@ITEMS_" + template) : PackFiles.Loc(locPack!, "@ITEMS_" + name);
        var (den, dru) = clone ? Names(c, o, "@ITEMS_" + template + "_DESC") : PackFiles.Loc(locPack!, "@ITEMS_" + name + "_DESC");
        var body = new StringBuilder($"<div class=card><h2>{(clone ? "Clone &amp; edit item" : "Edit item " + H(name))}</h2>");
        if (error is not null) body.Append(Note(error, true));
        body.Append($"<p class=muted>{ItemIcon(src, 48)}{(clone ? $"Template: <a href=\"/admin/mods/item?name={Enc(template)}\">{H(template)}</a> ({H(src.Name.LocalName)}). " : template.Length > 0 ? $"Cloned from {H(template)}. " : "")}")
            .Append($"Model {H(src.Name.LocalName is "weapon" or "equipmentitem" || src.Attribute("mesh") is not null ? ModBuilder.MeshOf(src) + "_01" : "-")} and icon {H(ModBuilder.SpriteOf(src))} are the template's (reused: a new item cannot have its own). ")
            .Append("Enchantment levels of the template are not copied (the new item has the base level only).</p>")
            .Append("<form method=post action=\"/admin/mods/item/save\" class=edit>")
            .Append($"<input type=hidden name=csrf value=\"{s.Csrf}\"><input type=hidden name=mode value=\"{(clone ? "clone" : "edit")}\"><input type=hidden name=template value=\"{H(template)}\">");
        body.Append("<fieldset><legend>Identity</legend><div class=grid>");
        body.Append(clone ? "<label><span>Pack</span>" + PackSelect(pack?.Name ?? V("pack", "")) + "</label>" : $"<input type=hidden name=pack value=\"{pack!.Name}\">");
        body.Append(clone
            ? Input("uniquename", name, "Unique name (new, A-Z 0-9 _)", extra: $" required pattern=\"[A-Z0-9][A-Z0-9_]{{2,79}}\" placeholder=\"{H(Suggest(template))}\"")
            : Input("name", name, "Unique name", readOnly: true));
        body.Append(Input("name_en", V("name_en", en ?? ""), "Name EN", extra: " maxlength=200"))
            .Append(Input("name_ru", V("name_ru", ru ?? ""), "Name RU", extra: " maxlength=200"))
            .Append("</div><div class=grid2>")
            .Append($"<label><span>Description EN</span><textarea name=desc_en maxlength=1000 rows=3>{H(V("desc_en", den ?? ""))}</textarea></label>")
            .Append($"<label><span>Description RU</span><textarea name=desc_ru maxlength=1000 rows=3>{H(V("desc_ru", dru ?? ""))}</textarea></label>")
            .Append("</div></fieldset>");
        // Attributes of the template, generically.
        body.Append("<fieldset><legend>Attributes (the template's, with its values)</legend><div class=grid>");
        foreach (var a in src.Attributes().Where(a => !a.IsNamespaceDeclaration && a.Name.LocalName != "uniquename"))
        {
            var n = a.Name.LocalName;
            var fixedValue = n is "mesh" or "uisprite" || IsAsset(src, a);
            var dflt = clone && n is "unlockedtocraft" or "unlockedtoequip" ? "true" : a.Value;
            body.Append(Input(fixedValue ? "" : "a_" + n, fixedValue ? a.Value + " (reused)" : V("a_" + n, dflt), n, readOnly: fixedValue));
        }
        body.Append("</div><p class=muted>unlockedtocraft / unlockedtoequip: a new item has no destiny-board node, so a clone sets them to true.</p></fieldset>");
        // Recipe.
        var req = src.Elements("craftingrequirements").FirstOrDefault();
        var res = req?.Elements("craftresource").ToList() ?? [];
        var stations = clone
            ? (c.StationsOf.GetValueOrDefault(template) ?? []).Select(x => x.Station).Where(x => !x.Contains("TEST", StringComparison.OrdinalIgnoreCase)).ToList()
            : PackFiles.StationsOf(pack!.Patch("buildings.xml"), name);
        body.Append("<fieldset><legend>Crafting recipe</legend><input type=hidden name=r_present value=1><div class=grid>")
            .Append(Input("r_silver", V("r_silver", req?.Attribute("silver")?.Value ?? "0"), "silver"))
            .Append(Input("r_time", V("r_time", req?.Attribute("time")?.Value ?? "1"), "time"))
            .Append(Input("r_focus", V("r_focus", req?.Attribute("craftingfocus")?.Value ?? "0"), "craftingfocus"))
            .Append("</div><table class=small><tr><th>Ingredient</th><th>Count</th></tr>");
        for (var i = 0; i < 8; i++)
            body.Append($"<tr><td><input name=ing{i}_name list=dl-res maxlength=100 value=\"{H(V($"ing{i}_name", i < res.Count ? res[i].Attribute("uniquename")?.Value ?? "" : ""))}\" aria-label=\"ingredient {i + 1}\"></td>")
                .Append($"<td><input name=ing{i}_count inputmode=numeric style=\"width:6em\" value=\"{H(V($"ing{i}_count", i < res.Count ? res[i].Attribute("count")?.Value ?? "" : ""))}\" aria-label=\"count {i + 1}\"></td></tr>");
        body.Append("</table>").Append($"<label><span>Stations (comma-separated; none = not craftable at a station)</span><input name=stations list=dl-stations maxlength=600 value=\"{H(V("stations", string.Join(", ", stations)))}\"></label>")
            .Append("<p class=muted>No ingredients = not craftable.</p></fieldset>");
        // Spells.
        var cs = src.Element("craftingspelllist")?.Elements("craftspell").ToList() ?? [];
        var rows = posted is not null ? Enumerable.Range(0, 30).Count(i => posted.ContainsKey($"sp{i}_name")) : Math.Clamp(cs.Count + 4, 8, 30);
        body.Append("<fieldset><legend>Spell slots</legend><input type=hidden name=s_present value=1><table class=small><tr><th>Spell (existing, or a new spell of the pack)</th><th>Slot (1-3; empty = passive)</th></tr>");
        for (var i = 0; i < rows; i++)
            body.Append($"<tr><td><input name=sp{i}_name list=dl-spells maxlength=100 value=\"{H(V($"sp{i}_name", i < cs.Count ? cs[i].Attribute("uniquename")?.Value ?? "" : ""))}\" aria-label=\"spell {i + 1}\"></td>")
                .Append($"<td><input name=sp{i}_slots inputmode=numeric style=\"width:5em\" value=\"{H(V($"sp{i}_slots", i < cs.Count ? cs[i].Attribute("slots")?.Value ?? "" : ""))}\" aria-label=\"slot {i + 1}\"></td></tr>");
        body.Append("</table></fieldset>");
        // Drops.
        var drops = clone || pack is null ? new List<DropRow>() : pack.Drops().Where(d => string.Equals(d.Item, name, StringComparison.OrdinalIgnoreCase)).ToList();
        body.Append("<fieldset><legend>Drops on the server (server/drops.json; needs the pack in Mods:Packs)</legend><input type=hidden name=d_present value=1>")
            .Append("<table class=small><tr><th>Mobs (names, * wildcards, comma-separated)</th><th>Chests</th><th>Mob category</th><th>Chance 0..1</th><th>Amount</th></tr>");
        for (var i = 0; i < Math.Min(drops.Count + 2, 8); i++)
        {
            var d = i < drops.Count ? drops[i] : null;
            body.Append($"<tr><td><input name=dr{i}_mobs list=dl-mobs maxlength=600 value=\"{H(V($"dr{i}_mobs", d is null ? "" : string.Join(", ", d.Mobs)))}\" aria-label=\"mobs {i + 1}\"></td>")
                .Append($"<td><input name=dr{i}_chests list=dl-chests maxlength=600 value=\"{H(V($"dr{i}_chests", d is null ? "" : string.Join(", ", d.Chests)))}\" aria-label=\"chests {i + 1}\"></td>")
                .Append($"<td><select name=dr{i}_cat aria-label=\"category {i + 1}\">{Options(c.Mobs.Values.Where(v => v.Length > 0).Distinct().Order(), V($"dr{i}_cat", d?.Category ?? ""), "-")}</select></td>")
                .Append($"<td><input name=dr{i}_chance inputmode=decimal style=\"width:6em\" value=\"{H(V($"dr{i}_chance", d is null ? "" : PackFiles.Num(d.Chance)))}\" aria-label=\"chance {i + 1}\"></td>")
                .Append($"<td><input name=dr{i}_amount inputmode=numeric style=\"width:5em\" value=\"{H(V($"dr{i}_amount", d is null ? "1" : d.Amount.ToString()))}\" aria-label=\"amount {i + 1}\"></td></tr>");
        }
        body.Append("</table><p class=muted>Mobs and a category together: both must match. The chance is per kill (or per chest's first roll) × Rates:Loot.</p></fieldset>");
        body.Append("<button>Save into the pack</button></form>");
        if (!clone) body.Append($"<p><a href=\"/admin/mods/pack?name={pack!.Name}\">Back to the pack</a> · <a href=\"/admin/mods/preview?pack={pack.Name}\">Preview</a></p>");
        body.Append(Datalist("dl-res", c.Resources.Concat(o.Items.Select(i => i.Name))))
            .Append(Datalist("dl-spells", c.OfferedBy.Keys.Order().Concat(o.Spells.Select(x => x.Name))))
            .Append(Datalist("dl-stations", c.Stations.Where(x => !x.Contains("TEST", StringComparison.OrdinalIgnoreCase))))
            .Append(Datalist("dl-mobs", new[] { "WORLD_BOSS_*" }.Concat(c.Mobs.Where(m => m.Value is "boss" or "miniboss").Select(m => m.Key).Order())))
            .Append(Datalist("dl-chests", c.Chests.Order()));
        return Page(clone ? "Clone item" : $"Edit {name}", body.Append("</div>").ToString(), s, error is null ? 200 : 400);
    }

    /// <summary>A name suggestion for a clone of <paramref name="template"/>: T6_MAIN_SWORD → T6_MAIN_SWORD_MOD.</summary>
    private static string Suggest(string template) => (template.Length > 70 ? template[..70] : template) + "_MOD";

    private static List<string> SplitList(string text) =>
        [.. text.Split([',', ';', ' '], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).Take(30)];

    private WebResponse ItemSave(WebRequest r, AdminSession s)
    {
        var (c, problem) = Catalog();
        if (c is null) return CatalogWait(s, problem);
        if (_modsProblem is not null) return Text(403, _modsProblem);
        var o = PackOverlay();
        var f = r.Form;
        WebResponse Fail(string why) => ItemEdit(r, s, f, why);
        if (PackFiles.Open(_modsDir, r.F("pack")) is not { } pack) return Fail("choose an existing pack");
        var clone = r.F("mode") == "clone";
        var items = pack.Patch("items.xml");
        XElement src;
        string name;
        var template = r.F("template").Trim();
        if (clone)
        {
            name = r.F("uniquename").Trim();
            if (!PackFiles.NewName().IsMatch(name)) return Fail("the unique name: 3 to 80 of A-Z, 0-9 and _ (e.g. T6_MAIN_MYSWORD)");
            if (FindItem(c, o, name) is { } taken) return Fail($"{name} exists already ({(taken.Pack is { } tp ? "in pack " + tp : "an official item")}): choose a new name");
            if (FindItem(c, o, template) is not { } t) return Fail($"no template item {template}");
            src = t.El;
        }
        else
        {
            name = r.F("name").Trim();
            src = PackFiles.Added(items, name) ?? throw new InvalidDataException($"{pack.Name} adds no item {name}");
        }
        var el = new XElement(src);
        el.SetAttributeValue("uniquename", name);
        // Attributes: the template's, with the posted values.
        foreach (var a in src.Attributes().Where(a => !a.IsNamespaceDeclaration && a.Name.LocalName != "uniquename").ToList())
        {
            var n = a.Name.LocalName;
            if (n is "mesh" or "uisprite" || IsAsset(src, a)) continue;
            var posted = f.GetValueOrDefault("a_" + n);
            if (posted is null or "")
            {
                if (clone && n is "unlockedtocraft" or "unlockedtoequip") el.SetAttributeValue(n, "true");
                continue;
            }
            if (CheckValue(n, a.Value, posted, out var value) is { } bad) return Fail(bad);
            el.SetAttributeValue(n, value);
        }
        if (int.TryParse(el.Attribute("tier")?.Value, out var tier) && tier is < 0 or > 8) return Fail("tier: 0..8");
        // Model and icon: always the template's.
        el.SetAttributeValue("uisprite", ModBuilder.SpriteOf(src));
        if (src.Name.LocalName is "weapon" or "equipmentitem" || src.Attribute("mesh") is not null) el.SetAttributeValue("mesh", ModBuilder.MeshOf(src));
        el.Elements("enchantments").Remove();
        // Recipe.
        if (f.ContainsKey("r_present"))
        {
            var req = new XElement("craftingrequirements");
            if (src.Element("craftingrequirements") is { } oldReq) req.Add(oldReq.Attributes());
            foreach (var (field, attr, old) in new[] { ("r_silver", "silver", "0"), ("r_time", "time", "1.5"), ("r_focus", "craftingfocus", "0") })
                if (r.F(field).Trim() is { Length: > 0 } v)
                {
                    if (CheckValue(attr, old, v, out var clean) is { } bad) return Fail(bad);
                    req.SetAttributeValue(attr, clean);
                }
            for (var i = 0; i < 8; i++)
            {
                var res = r.F($"ing{i}_name").Trim();
                if (res.Length == 0) continue;
                if (!PackFiles.RefName().IsMatch(res)) return Fail($"ingredient {i + 1}: '{res}' is no item name");
                if (!int.TryParse(r.F($"ing{i}_count").Trim(), out var count) || count is < 1 or > 9999) return Fail($"ingredient {res}: a count of 1..9999");
                req.Add(new XElement("craftresource", new XAttribute("uniquename", res), new XAttribute("count", count)));
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
        // Spells.
        if (f.ContainsKey("s_present"))
        {
            var list = new XElement("craftingspelllist");
            for (var i = 0; i < 30; i++)
            {
                var sp = r.F($"sp{i}_name").Trim();
                if (sp.Length == 0) continue;
                if (!PackFiles.RefName().IsMatch(sp)) return Fail($"spell {i + 1}: '{sp}' is no spell name");
                var slot = r.F($"sp{i}_slots").Trim();
                var cs = new XElement("craftspell", new XAttribute("uniquename", sp));
                if (slot.Length > 0)
                {
                    if (!int.TryParse(slot, out var sl) || sl is < 1 or > 6) return Fail($"spell {sp}: slot 1..6 (or empty for a passive)");
                    cs.SetAttributeValue("slots", sl);
                }
                list.Add(cs);
            }
            var old = el.Element("craftingspelllist");
            if (old is not null) old.ReplaceWith(list);
            else if (list.HasElements)
            {
                if (el.Elements("craftingrequirements").LastOrDefault() is { } after) after.AddAfterSelf(list);
                else el.AddFirst(list);
            }
            if (!list.HasElements) list.Remove();
        }
        // Stations.
        List<string>? stations = null;
        if (f.ContainsKey("stations"))
        {
            stations = SplitList(r.F("stations")).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
            foreach (var st in stations)
                if (!PackFiles.RefName().IsMatch(st)) return Fail($"station '{st}' is no building name");
        }
        // Drops.
        List<DropRow>? drops = null;
        if (f.ContainsKey("d_present"))
        {
            drops = [];
            for (var i = 0; i < 8; i++)
            {
                var mobs = SplitList(r.F($"dr{i}_mobs"));
                var chests = SplitList(r.F($"dr{i}_chests"));
                var cat = r.F($"dr{i}_cat").Trim();
                if (mobs.Count == 0 && chests.Count == 0 && cat.Length == 0) continue;
                foreach (var m in mobs.Concat(chests).Append(cat).Where(m => m.Length > 0))
                    if (!PackFiles.RefName().IsMatch(m)) return Fail($"drop {i + 1}: '{m}' is no name or pattern");
                if (!double.TryParse(r.F($"dr{i}_chance").Trim().TrimEnd('%'), NumberStyles.Float, CultureInfo.InvariantCulture, out var chance) || chance is < 0 or > 1)
                    return Fail($"drop {i + 1}: a chance of 0..1 (0.03 = 3 %)");
                var amountText = r.F($"dr{i}_amount").Trim();
                var amount = 1;
                if (amountText.Length > 0 && (!int.TryParse(amountText, out amount) || amount is < 1 or > 999)) return Fail($"drop {i + 1}: an amount of 1..999");
                drops.Add(new DropRow(name, [.. mobs], [.. chests], cat.Length > 0 ? cat : null, chance, amount));
            }
        }
        // Write: items.xml, localization.xml, buildings.xml, drops.json, editor.json.
        PackFiles.PutAdded(items, el);
        pack.Save("items.xml", items);
        var loc = pack.Patch("localization.xml");
        PackFiles.SetLoc(loc, "@ITEMS_" + name, Clean(r.F("name_en"), 200), Clean(r.F("name_ru"), 200));
        PackFiles.SetLoc(loc, "@ITEMS_" + name + "_DESC", Clean(r.F("desc_en"), 1000), Clean(r.F("desc_ru"), 1000));
        pack.Save("localization.xml", loc);
        if (stations is not null)
        {
            var buildings = pack.Patch("buildings.xml");
            var origin = clone ? template : pack.Templates().GetValueOrDefault(name) ?? "";
            var known = c.StationsOf.GetValueOrDefault(origin) ?? [];
            PackFiles.SetStations(buildings, name, stations, st =>
            {
                var model = known.FirstOrDefault(k => string.Equals(k.Station, st, StringComparison.OrdinalIgnoreCase)).CraftItem ?? known.FirstOrDefault().CraftItem;
                var ci = model is null ? new XElement("craftitem") : new XElement(model);
                ci.SetAttributeValue("uniquename", name);
                return ci;
            });
            pack.Save("buildings.xml", buildings);
        }
        if (drops is not null) pack.SetDrops(name, drops);
        if (clone) pack.SetTemplate(name, template);
        Audit($"admin '{s.Name}'", r.Remote, $"mods: {(clone ? $"cloned item {template} as" : "edited item")} {name} in pack {pack.Name}");
        return Back(s, $"/admin/mods/item/edit?pack={pack.Name}&name={Enc(name)}", $"{name} saved into {pack.Name} (not installed yet: Validate, then Install on the pack page)", false);
    }

    // ---- spells: the ability builder

    /// <summary>The spell as the editor works on it: a copy without &lt;locareferences&gt; (they name the template's values).</summary>
    internal static XElement PrepareSpell(XElement src)
    {
        var e = new XElement(src);
        e.Elements("locareferences").Remove();
        return e;
    }

    private static string ElementPath(XElement root, XElement e)
    {
        var parts = new List<string>();
        for (var at = e; at is not null && at != root; at = at.Parent)
        {
            var same = at.Parent?.Elements(at.Name).ToList() ?? [];
            parts.Add(same.Count > 1 ? $"{at.Name.LocalName}[{same.IndexOf(at) + 1}]" : at.Name.LocalName);
        }
        parts.Add(root.Name.LocalName);
        parts.Reverse();
        return string.Join(" / ", parts);
    }

    private const int MaxSpellFields = 400;

    private WebResponse SpellEdit(WebRequest r, AdminSession s, IReadOnlyDictionary<string, string>? posted, string? error)
    {
        var (c, problem) = Catalog();
        if (c is null) return CatalogWait(s, problem);
        var o = PackOverlay();
        var get = posted ?? r.Query;
        string V(string key, string dflt) => posted is not null ? posted.GetValueOrDefault(key, dflt) : dflt;
        var pack = PackFiles.Open(_modsDir, get.GetValueOrDefault("pack"));
        var editName = get.GetValueOrDefault("name");
        XElement src;
        string template;
        bool clone;
        if (editName is { Length: > 0 } && posted?.GetValueOrDefault("mode") != "clone")
        {
            if (pack is null) return Text(404, "no such pack");
            src = PackFiles.Added(pack.Patch("spells.xml"), editName) ?? throw new InvalidDataException($"{pack.Name} adds no spell {editName}");
            template = pack.Templates().GetValueOrDefault(editName) ?? "";
            clone = false;
        }
        else
        {
            template = (get.GetValueOrDefault("template") ?? "").Trim();
            if (FindSpell(c, o, template) is not { } t) return Text(404, $"no spell '{template}' to start from (search the spell catalogue)");
            src = t.El;
            template = t.Name;
            clone = true;
        }
        var el = PrepareSpell(src);
        var name = clone ? V("uniquename", "") : editName!;
        var loc = pack?.Patch("localization.xml");
        var nameKey = clone ? "" : ModCatalog.SpellNameKey(el);
        var descKey = clone ? "" : ModCatalog.SpellDescKey(el);
        var ownText = clone || nameKey.StartsWith("@SPELLS_" + name, StringComparison.OrdinalIgnoreCase);
        var (en, ru) = clone ? Names(c, o, ModCatalog.SpellNameKey(src)) : ownText ? PackFiles.Loc(loc!, nameKey) : Names(c, o, nameKey);
        var (den, dru) = clone ? Names(c, o, ModCatalog.SpellDescKey(src)) : ownText ? PackFiles.Loc(loc!, descKey) : Names(c, o, descKey);
        var body = new StringBuilder($"<div class=card><h2>{(clone ? "New ability" : "Edit ability " + H(name))}</h2>");
        if (error is not null) body.Append(Note(error, true));
        body.Append($"<p class=muted>{SpellIcon(src, 48)}{(clone ? $"Template: <a href=\"/admin/mods/spell?name={Enc(template)}\">{H(template)}</a>. " : template.Length > 0 ? $"Made from {H(template)}. " : "")}")
            .Append("Icons, animations, effects and sounds stay the template's (only official assets exist). The spells it uses (sub-spells) stay the official ones "
                + "unless you clone them with it; their numbers are then edited as spells of their own in this pack.</p>")
            .Append("<form method=post action=\"/admin/mods/spell/save\" class=edit>")
            .Append($"<input type=hidden name=csrf value=\"{s.Csrf}\"><input type=hidden name=mode value=\"{(clone ? "clone" : "edit")}\"><input type=hidden name=template value=\"{H(template)}\">")
            .Append("<fieldset><legend>Identity</legend><div class=grid>")
            .Append(clone ? "<label><span>Pack</span>" + PackSelect(pack?.Name ?? V("pack", "")) + "</label>" : $"<input type=hidden name=pack value=\"{pack!.Name}\">")
            .Append(clone
                ? Input("uniquename", name, "Unique name (new, A-Z 0-9 _)", extra: $" required pattern=\"[A-Z0-9][A-Z0-9_]{{2,79}}\" placeholder=\"{H(Suggest(template))}\"")
                : Input("name", name, "Unique name", readOnly: true));
        if (ownText)
            body.Append(Input("name_en", V("name_en", en ?? ""), "Name EN", extra: " maxlength=200")).Append(Input("name_ru", V("name_ru", ru ?? ""), "Name RU", extra: " maxlength=200"))
                .Append("</div><div class=grid2>")
                .Append($"<label><span>Description EN</span><textarea name=desc_en maxlength=1000 rows=3>{H(V("desc_en", den ?? ""))}</textarea></label>")
                .Append($"<label><span>Description RU</span><textarea name=desc_ru maxlength=1000 rows=3>{H(V("desc_ru", dru ?? ""))}</textarea></label>");
        else body.Append($"</div><p class=muted>Texts: the official {H(nameKey)} ({H(en)}) — a sub-spell keeps the official name.</p><div>");
        body.Append("</div>");
        if (clone)
        {
            var refs = SpellRefs(c, o, el).Where(n => !string.Equals(n, template, StringComparison.OrdinalIgnoreCase)).ToList();
            if (refs.Count > 0)
                body.Append($"<label class=check><input type=checkbox name=deep value=1{(V("deep", "") == "1" ? " checked" : "")}> also clone the spells it uses ({H(string.Join(", ", refs.Take(12)))}{(refs.Count > 12 ? ", …" : "")}) as new spells of the pack, so that their numbers can be changed</label>");
        }
        body.Append("</fieldset>");
        // Every attribute of every element, generically.
        var all = el.DescendantsAndSelf().ToList();
        var count = 0;
        for (var i = 0; i < all.Count && count < MaxSpellFields; i++)
        {
            var e = all[i];
            var attrs = e.Attributes().Where(a => !a.IsNamespaceDeclaration && !(e == el && a.Name.LocalName is "uniquename" or "namelocatag" or "descriptionlocatag")).ToList();
            if (attrs.Count == 0) continue;
            body.Append($"<fieldset><legend>{H(ElementPath(el, e))}</legend><div class=grid>");
            foreach (var a in attrs)
            {
                var asset = IsAsset(e, a);
                body.Append(Input(asset ? "" : $"v{i}_{a.Name.LocalName}", asset ? a.Value + " (reused)" : V($"v{i}_{a.Name.LocalName}", a.Value), a.Name.LocalName, readOnly: asset));
                count++;
            }
            body.Append("</div></fieldset>");
        }
        if (count >= MaxSpellFields) body.Append(Note($"only the first {MaxSpellFields} attributes are shown: edit the rest in spells.xml by hand", false));
        body.Append("<button>Save into the pack</button></form>");
        if (!clone)
        {
            // Composition: the blocks of this spell, and blocks to add from another spell.
            body.Append("<h3>Effect blocks</h3><ol class=small>");
            var blocks = el.Elements().ToList();
            for (var i = 0; i < blocks.Count; i++)
                body.Append("<li><code>").Append(H(BlockSummary(blocks[i]))).Append("</code> ")
                    .Append(Form(s, "/admin/mods/spell/block", "Remove", "danger", ("pack", pack!.Name),
                        $"<input type=hidden name=name value=\"{H(name)}\"><input type=hidden name=op value=remove><input type=hidden name=index value={i}>"))
                    .Append("</li>");
            body.Append("</ol><form method=get action=\"/admin/mods/spell/edit\" class=inline>")
                .Append($"<input type=hidden name=pack value=\"{pack!.Name}\"><input type=hidden name=name value=\"{H(name)}\">")
                .Append($"<input name=from list=dl-allspells maxlength=100 value=\"{H(r.Q("from"))}\" placeholder=\"spell to take a block from, e.g. ICEROCK_EXPLODE_FROST_PATH\" aria-label=\"source spell\"><button>Show its blocks</button></form>");
            if (r.Q("from") is { Length: > 0 } from)
            {
                if (FindSpell(c, o, from) is not { } source) body.Append(Note($"no spell {from}", true));
                else
                {
                    body.Append($"<p>Blocks of <a href=\"/admin/mods/spell?name={Enc(source.Name)}\">{H(source.Name)}</a> ({H(Names(c, o, ModCatalog.SpellNameKey(source.El)).En)}):</p><ol class=small>");
                    var sb = source.El.Elements().ToList();
                    for (var i = 0; i < sb.Count; i++)
                    {
                        if (sb[i].Name.LocalName == "locareferences") continue;
                        body.Append("<li><code>").Append(H(BlockSummary(sb[i]))).Append("</code> ")
                            .Append(Form(s, "/admin/mods/spell/block", "Add to this spell", "", ("pack", pack.Name),
                                $"<input type=hidden name=name value=\"{H(name)}\"><input type=hidden name=op value=add><input type=hidden name=from value=\"{H(source.Name)}\"><input type=hidden name=index value={i}>"))
                            .Append("</li>");
                    }
                    body.Append("</ol>");
                }
            }
            body.Append(Datalist("dl-allspells", c.Spells.Where(x => x.Kind == "activespell").Select(x => x.Name).Where(n => c.OfferedBy.ContainsKey(n) || n.Contains("_EFFECT") || n.Contains("_AREA") || n.Contains("_SLOW"))
                .Take(3000).Concat(o.Spells.Select(x => x.Name))));
            body.Append($"<p><a href=\"/admin/mods/pack?name={pack.Name}\">Back to the pack</a> · <a href=\"/admin/mods/preview?pack={pack.Name}\">Preview</a></p>");
        }
        return Page(clone ? "New ability" : $"Edit {name}", body.Append("</div>").ToString(), s, error is null ? 200 : 400);
    }

    private WebResponse SpellSave(WebRequest r, AdminSession s)
    {
        var (c, problem) = Catalog();
        if (c is null) return CatalogWait(s, problem);
        if (_modsProblem is not null) return Text(403, _modsProblem);
        var o = PackOverlay();
        var f = r.Form;
        WebResponse Fail(string why) => SpellEdit(r, s, f, why);
        if (PackFiles.Open(_modsDir, r.F("pack")) is not { } pack) return Fail("choose an existing pack");
        var clone = r.F("mode") == "clone";
        var spells = pack.Patch("spells.xml");
        var template = r.F("template").Trim();
        XElement src;
        string name;
        if (clone)
        {
            name = r.F("uniquename").Trim();
            if (!PackFiles.NewName().IsMatch(name)) return Fail("the unique name: 3 to 80 of A-Z, 0-9 and _ (e.g. MY_DASH)");
            if (FindSpell(c, o, name) is not null) return Fail($"a spell {name} exists already: choose a new name");
            if (FindSpell(c, o, template) is not { } t) return Fail($"no template spell {template}");
            src = t.El;
        }
        else
        {
            name = r.F("name").Trim();
            src = PackFiles.Added(spells, name) ?? throw new InvalidDataException($"{pack.Name} adds no spell {name}");
        }
        var el = PrepareSpell(src);
        var all = el.DescendantsAndSelf().ToList();
        var fields = 0;
        for (var i = 0; i < all.Count && fields < MaxSpellFields; i++)
        {
            var e = all[i];
            foreach (var a in e.Attributes().Where(a => !a.IsNamespaceDeclaration && !(e == el && a.Name.LocalName is "uniquename" or "namelocatag" or "descriptionlocatag")).ToList())
            {
                fields++;
                if (IsAsset(e, a)) continue;
                if (f.GetValueOrDefault($"v{i}_{a.Name.LocalName}") is not { Length: > 0 } posted) continue;
                if (CheckValue($"{ElementPath(el, e)} {a.Name.LocalName}", a.Value, posted, out var value) is { } bad) return Fail(bad);
                a.Value = value;
            }
        }
        el.SetAttributeValue("uniquename", name);
        var made = new List<(XElement El, string Template)>();
        if (clone)
        {
            el.SetAttributeValue("namelocatag", "@SPELLS_" + name);
            el.SetAttributeValue("descriptionlocatag", "@SPELLS_" + name + "_DESC");
            // Deep: the spells it uses become new spells of the pack (named after the new one), the references rewired.
            var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase) { [template] = name };
            if (r.F("deep") == "1")
            {
                var queue = new Queue<(string Name, int Depth)>(SpellRefs(c, o, el).Select(n => (n, 1)));
                while (queue.Count > 0 && map.Count < 13)
                {
                    var (sub, depth) = queue.Dequeue();
                    if (map.ContainsKey(sub) || FindSpell(c, o, sub) is not { } ss) continue;
                    var newName = sub.StartsWith(template + "_", StringComparison.OrdinalIgnoreCase) ? name + sub[template.Length..] : name + "_" + sub;
                    if (newName.Length > 80) newName = newName[..80];
                    if (FindSpell(c, o, newName) is not null || map.ContainsValue(newName) || !PackFiles.NewName().IsMatch(newName)) continue;
                    map[sub] = newName;
                    var copy = PrepareSpell(ss.El);
                    copy.SetAttributeValue("uniquename", newName);
                    made.Add((copy, ss.Name));
                    if (depth < 2) foreach (var n in SpellRefs(c, o, copy)) queue.Enqueue((n, depth + 1));
                }
            }
            foreach (var e in made.Select(m => m.El).Prepend(el))
                foreach (var a in e.DescendantsAndSelf().SelectMany(d => d.Attributes()).ToList())
                    if (!(a.Parent == e && a.Name.LocalName is "uniquename" or "namelocatag" or "descriptionlocatag") && !IsAsset(a.Parent!, a) && map.TryGetValue(a.Value, out var to)) a.Value = to;
        }
        PackFiles.PutAdded(spells, el);
        foreach (var (sub, _) in made) PackFiles.PutAdded(spells, sub);
        pack.Save("spells.xml", spells);
        var nameKey = ModCatalog.SpellNameKey(el);
        if (nameKey.StartsWith("@SPELLS_" + name, StringComparison.OrdinalIgnoreCase))
        {
            var loc = pack.Patch("localization.xml");
            PackFiles.SetLoc(loc, nameKey, Clean(r.F("name_en"), 200), Clean(r.F("name_ru"), 200));
            PackFiles.SetLoc(loc, ModCatalog.SpellDescKey(el), Clean(r.F("desc_en"), 1000), Clean(r.F("desc_ru"), 1000));
            pack.Save("localization.xml", loc);
        }
        if (clone)
        {
            pack.SetTemplate(name, template);
            foreach (var (sub, t) in made) pack.SetTemplate(sub.Attribute("uniquename")!.Value, t);
        }
        Audit($"admin '{s.Name}'", r.Remote, $"mods: {(clone ? $"new spell {name} from {template}" : $"edited spell {name}")}{(made.Count > 0 ? $" (+{made.Count} sub-spells)" : "")} in pack {pack.Name}");
        return Back(s, $"/admin/mods/spell/edit?pack={pack.Name}&name={Enc(name)}",
            $"{name} saved into {pack.Name}{(made.Count > 0 ? $" with {string.Join(", ", made.Select(m => m.El.Attribute("uniquename")!.Value))}" : "")}. Offer it in an item's spell slots to use it.", false);
    }

    private WebResponse SpellBlock(WebRequest r, AdminSession s)
    {
        var (c, problem) = Catalog();
        if (c is null) return CatalogWait(s, problem);
        if (_modsProblem is not null) return Text(403, _modsProblem);
        if (PackFiles.Open(_modsDir, r.F("pack")) is not { } pack) return Text(404, "no such pack");
        var name = r.F("name");
        var spells = pack.Patch("spells.xml");
        if (PackFiles.Added(spells, name) is not { } el) return Text(404, $"{pack.Name} adds no spell {name}");
        var back = $"/admin/mods/spell/edit?pack={pack.Name}&name={Enc(name)}";
        if (!int.TryParse(r.F("index"), out var index) || index < 0) return Back(s, back, "no block index", true);
        string what;
        if (r.F("op") == "add")
        {
            if (FindSpell(c, PackOverlay(), r.F("from")) is not { } source) return Back(s, back, $"no spell {r.F("from")}", true);
            var blocks = source.El.Elements().ToList();
            if (index >= blocks.Count || blocks[index].Name.LocalName == "locareferences") return Back(s, back, "no such block", true);
            if (el.Elements().Count() >= 60) return Back(s, back, "at most 60 blocks in a spell", true);
            el.Add(new XElement(blocks[index]));
            what = $"added <{blocks[index].Name.LocalName}> of {source.Name} to {name}";
        }
        else
        {
            var blocks = el.Elements().ToList();
            if (index >= blocks.Count) return Back(s, back, "no such block", true);
            blocks[index].Remove();
            what = $"removed <{blocks[index].Name.LocalName}> from {name}";
        }
        pack.Save("spells.xml", spells);
        Audit($"admin '{s.Name}'", r.Remote, $"mods: {what} in pack {pack.Name}");
        return Back(s, back, what, false);
    }

    private WebResponse RemoveFromPack(WebRequest r, AdminSession s)
    {
        if (_modsProblem is not null) return Text(403, _modsProblem);
        if (PackFiles.Open(_modsDir, r.F("pack")) is not { } pack) return Text(404, "no such pack");
        var name = r.F("name");
        var back = $"/admin/mods/pack?name={pack.Name}";
        var item = r.F("kind") == "item";
        var doc = pack.Patch(item ? "items.xml" : "spells.xml");
        var el = PackFiles.Added(doc, name);
        if (el is null) return Back(s, back, $"{pack.Name} adds no {(item ? "item" : "spell")} {name}", true);
        var keys = item ? new[] { "@ITEMS_" + name, "@ITEMS_" + name + "_DESC" } : new[] { ModCatalog.SpellNameKey(el), ModCatalog.SpellDescKey(el) };
        PackFiles.RemoveAdded(doc, name);
        pack.Save(item ? "items.xml" : "spells.xml", doc);
        var loc = pack.Patch("localization.xml");
        foreach (var k in keys.Where(k => k.StartsWith((item ? "@ITEMS_" : "@SPELLS_") + name, StringComparison.OrdinalIgnoreCase))) PackFiles.SetLoc(loc, k, "", "");
        pack.Save("localization.xml", loc);
        if (item)
        {
            var buildings = pack.Patch("buildings.xml");
            PackFiles.SetStations(buildings, name, [], _ => new XElement("craftitem"));
            pack.Save("buildings.xml", buildings);
            if (pack.Drops().Any(d => string.Equals(d.Item, name, StringComparison.OrdinalIgnoreCase))) pack.SetDrops(name, []);
        }
        pack.SetTemplate(name, null);
        Audit($"admin '{s.Name}'", r.Remote, $"mods: removed {(item ? "item" : "spell")} {name} from pack {pack.Name}");
        return Back(s, back, $"{name} removed from {pack.Name}{(item ? "" : " (items that offer it now name a missing spell: Validate shows them)")}", false);
    }

    // ---- modkit jobs

    private WebResponse StartModJob(WebRequest r, AdminSession s)
    {
        var verb = r.Path[(r.Path.LastIndexOf('/') + 1)..];
        var pack = verb == "restore" ? null : PackFiles.Open(_modsDir, r.F("pack"));
        var back = pack is null ? "/admin/mods" : $"/admin/mods/pack?name={pack.Name}";
        if (verb != "restore" && pack is null) return Back(s, "/admin/mods", "no such pack", true);
        if (_modPaths is not { } paths) return Back(s, back, "no official GameData found next to Paths:ClientGameData: check, install and restore need it", true);
        if (verb != "check" && r.F("confirm") != "1") return Back(s, back, "tick the confirmation (the server and the client must be restarted afterwards)", true);
        if (verb != "check" && !Directory.Exists(paths.Local)) return Back(s, back, $"game-local GameData {paths.Local} not found", true);
        var packs = verb switch
        {
            "check" => new List<string> { pack!.Name },
            "install" => PackFiles.List(_modsDir).Where(n => r.F("pk_" + n) == "1").ToList(),
            _ => new List<string>(),
        };
        if (verb == "install" && packs.Count == 0) return Back(s, back, "check at least one pack to install", true);
        var job = new ModJob { What = verb, Pack = pack?.Name, Install = verb == "install", Packs = packs };
        lock (_jobLock)
        {
            if (_job is { Done: false } running) return Back(s, back, $"the modkit is still running ({running.What}): wait for it", true);
            lock (_studioLock)
                if (_studioJobs.LastOrDefault() is { Done: false } studio) return Back(s, back, $"the studio's modkit run is going ({studio.What}): wait for it", true);
            _job = job;
        }
        var who = $"admin '{s.Name}'";
        var from = r.Remote;
        Audit(who, from, $"mods: {verb} {string.Join(", ", packs)}".TrimEnd());
        _ = Task.Run(() =>
        {
            var output = new StringWriter();
            try
            {
                switch (verb)
                {
                    case "check":
                        var result = new ModBuilder(paths.Official).Build(ModPack.LoadAll(_modsDir, packs));
                        job.Errors.AddRange(result.Errors);
                        job.Warnings.AddRange(result.Warnings);
                        job.Notes.AddRange(result.Notes);
                        job.Ok = result.Ok;
                        break;
                    case "install":
                        var built = new ModKit(paths, output).Build(ModPack.LoadAll(_modsDir, packs), force: false);
                        if (built is not null)
                        {
                            job.Errors.AddRange(built.Errors);
                            job.Warnings.AddRange(built.Warnings); // its notes are in the output
                        }
                        else job.Errors.Add("refused: see the output (an official client update since the first build needs the modkit's build --force on the console)");
                        job.Ok = built is { Ok: true };
                        break;
                    default:
                        new ModKit(paths, output).Restore();
                        job.Ok = true;
                        break;
                }
            }
            catch (Exception e)
            {
                job.Errors.Add($"{e.GetType().Name}: {e.Message}");
                job.Ok = false;
            }
            finally
            {
                job.Output = output.ToString();
                job.Done = true;
                Audit(who, from, $"mods: {verb} {string.Join(", ", packs)}".TrimEnd() + $" finished: {(job.Ok ? "ok" : $"{job.Errors.Count} error(s)")}");
            }
        });
        return WebResponse.Redirect("/admin/mods/job");
    }

    private WebResponse JobPage(AdminSession s)
    {
        var job = _job;
        if (job is null) return Page("Modkit", "<div class=card><p class=muted>No modkit run since the server started.</p></div>", s);
        var title = job.What switch { "check" => "Validate", "install" => "Install", _ => "Restore" };
        var body = new StringBuilder($"<div class=card><h2>{title}{(job.Packs.Count > 0 ? ": " + H(string.Join(", ", job.Packs)) : "")}</h2>");
        if (!job.Done)
        {
            body.Append($"<p>Running since {job.Started:HH:mm:ss} UTC… (reading the official files; with names this takes 10-60 s). This page reloads itself.</p></div>");
            return Page(title, body.ToString(), s, 200, "<meta http-equiv=refresh content=2>");
        }
        body.Append(job.Ok ? $"<p class=\"note ok\">{(job.What == "check" ? "No errors: the pack builds." : job.What == "install" ? "Installed into game-local." : "Restored: game-local has the official files.")}</p>"
            : $"<p class=\"note err\">{job.Errors.Count} error(s){(job.What == "install" ? ": nothing was installed" : "")}.</p>");
        if (job.Ok && job.What is "install" or "restore")
            body.Append("<div class=warnbox><b>Restart the server and the game client now.</b> Both read the game data only when they start; the panel does not restart the server. "
                + (job.What == "install" ? $"For the packs' drops the server needs them in <code>Mods:Packs</code> (e.g. <code>--Mods:Packs={H(string.Join(",", job.Packs))}</code>)." : "") + "</div>");
        if (job.Errors.Count > 0) body.Append("<h3>Errors</h3><ul class=errs>").Append(string.Concat(job.Errors.Select(e => $"<li>{H(e)}</li>"))).Append("</ul>");
        if (job.Warnings.Count > 0) body.Append("<h3>Warnings</h3><ul class=warns>").Append(string.Concat(job.Warnings.Select(e => $"<li>{H(e)}</li>"))).Append("</ul>");
        if (job.Notes.Count > 0) body.Append("<h3>Notes</h3><ul class=small>").Append(string.Concat(job.Notes.Select(e => $"<li>{H(e)}</li>"))).Append("</ul>");
        if (job.Output.Length > 0) body.Append("<h3>Modkit output</h3><pre class=log>").Append(H(job.Output)).Append("</pre>");
        body.Append($"<p>{(job.Pack is { } p ? $"<a href=\"/admin/mods/pack?name={p}\">Back to {H(p)}</a> · " : "")}<a href=\"/admin/mods\">Mods</a></p>");
        return Page(title, body.Append("</div>").ToString(), s);
    }
}
