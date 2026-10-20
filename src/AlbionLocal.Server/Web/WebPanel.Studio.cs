using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Xml.Linq;
using AlbionLocal.Photon;
using AlbionLocal.Server.Mods;
using AlbionLocal.Server.Web.Studio;

namespace AlbionLocal.Server.Web;

/// <summary>URLs the studio's JSON uses.</summary>
public static class StudioApi
{
    public static string IconUrl(string sprite) => "/studio/icons/" + Uri.EscapeDataString(sprite) + ".png";

    /// <summary>The CSP of the studio's page and scripts: its own scripts and styles only (no inline script, no eval), images from itself.</summary>
    public const string PageCsp = "default-src 'none'; script-src 'self'; style-src 'self' 'unsafe-inline'; img-src 'self' data:; connect-src 'self'; font-src 'self'; form-action 'self'; frame-ancestors 'none'; base-uri 'none'";

    public const string JsonCsp = "default-src 'none'; frame-ancestors 'none'";
}

/// <summary>
/// The Studio (analysis/work/stageN/studio/SPEC.md): a single-page content editor at /studio for admins: the static front-end
/// (ES modules embedded in the server, web/studio/) and its JSON API under /studio/api/. Reads go to the catalogue; writes go
/// only into mods/&lt;pack&gt;/ (drafts in studio/, the patch files by Generate); game-local is written only by the modkit's own
/// safe install. Writes need the admin session and the X-CSRF-Token header (and a same-origin request).
/// </summary>
public sealed partial class WebPanel
{
    private readonly object _studioLock = new();
    private (ModCatalog Catalog, StudioIndex Index)? _studioIndex;
    private readonly List<StudioJob> _studioJobs = [];
    private int _studioJobSeq;

    /// <summary>A background modkit run of the studio: what, its stage and progress, and its result as diagnostics.</summary>
    private sealed class StudioJob
    {
        public required int Id { get; init; }
        public required string What { get; init; }
        public required List<string> Packs { get; init; }
        public DateTime Started { get; } = DateTime.UtcNow;
        public DateTime? Finished { get; set; }
        public volatile string Stage = "queued";
        public volatile int Progress;
        public volatile bool Done;
        public bool Ok { get; set; }
        public List<StudioDiag> Diagnostics { get; } = [];
        public List<string> Notes { get; } = [];
        public string Output { get; set; } = "";

        public JsonObject ToJson() => new()
        {
            ["id"] = Id, ["what"] = What, ["packs"] = J.Strings(Packs), ["started"] = Started.ToString("o"), ["finished"] = Finished?.ToString("o"),
            ["stage"] = Stage, ["progress"] = Progress, ["done"] = Done, ["ok"] = Ok,
            ["diagnostics"] = new JsonArray([.. Diagnostics.Select(d => (JsonNode)d.ToJson())]), ["notes"] = J.Strings(Notes),
            ["notesText"] = new JsonArray([.. Notes.Select(n => StudioDiagText.Note(n)).Select(n => (JsonNode)new JsonObject { ["ru"] = n.Ru, ["en"] = n.En })]), ["output"] = Output,
            ["restartNeeded"] = Done && Ok && What is "install" or "restore",
        };
    }

    // ---- dispatch

    private async Task<WebResponse> Studio(WebRequest r)
    {
        var api = r.Path.StartsWith("/studio/api/", StringComparison.Ordinal);
        if (Authenticate(r) is not { } s)
            return api || r.Method != "GET" ? Json(401, Error("not logged in (or the session ended): log in again at /login")) : WebResponse.Redirect("/login?next=/studio");
        if (r.Method == "POST")
        {
            if (!api) return Json(405, Error("POST only to /studio/api/"));
            if (!CsrfOk(s, r.Headers.GetValueOrDefault("X-CSRF-Token") ?? "")) return Json(403, Error("the X-CSRF-Token header is not this session's: reload the page"));
        }
        if (api)
        {
            try { return await StudioApiCall(r, s, r.Path["/studio/api/".Length..]); }
            catch (Exception e) when (e is InvalidDataException or IOException or UnauthorizedAccessException or JsonException or ArgumentException or FormatException or InvalidOperationException)
            {
                Log.Warn("web", $"studio: {r.Method} {r.Path}: {e.Message}");
                return Json(400, Error(e.Message));
            }
        }
        if (r.Method == "GET" && r.Path.StartsWith("/studio/icons/", StringComparison.Ordinal))
        {
            var icon = IconFile(r.Path["/studio/icons/".Length..]);
            return icon.Status == 200 ? icon with { CacheControl = "private, max-age=86400" } : icon;
        }
        return StudioStatic(r);
    }

    private static WebResponse Json(int status, JsonNode body) =>
        new(status, body.ToJsonString(J.Indented), "application/json; charset=utf-8") { Csp = StudioApi.JsonCsp };

    private static JsonObject Error(string message, JsonObject? more = null)
    {
        var o = more ?? [];
        o["error"] = message;
        return o;
    }

    private static JsonObject Body(WebRequest r)
    {
        if (r.Body.Length == 0) return [];
        return JsonNode.Parse(r.Body, null, J.Read) as JsonObject ?? throw new JsonException("the body is no JSON object");
    }

    // ---- static files (embedded: web/studio/**)

    private static readonly Dictionary<string, (byte[] Bytes, string Type, string ETag)> StaticFiles = LoadStatic();

    private static Dictionary<string, (byte[], string, string)> LoadStatic()
    {
        var map = new Dictionary<string, (byte[], string, string)>(StringComparer.Ordinal);
        var asm = typeof(WebPanel).Assembly;
        foreach (var name in asm.GetManifestResourceNames())
        {
            var rel = name.Replace('\\', '/');
            if (!rel.StartsWith("studio/", StringComparison.Ordinal)) continue;
            using var s = asm.GetManifestResourceStream(name)!;
            using var ms = new MemoryStream();
            s.CopyTo(ms);
            var bytes = ms.ToArray();
            var type = Path.GetExtension(rel).ToLowerInvariant() switch
            {
                ".html" => "text/html; charset=utf-8", ".mjs" or ".js" => "text/javascript; charset=utf-8", ".css" => "text/css; charset=utf-8",
                ".svg" => "image/svg+xml", ".png" => "image/png", ".json" => "application/json; charset=utf-8", _ => "application/octet-stream",
            };
            map[rel["studio/".Length..]] = (bytes, type, "\"" + Convert.ToHexString(SHA256.HashData(bytes))[..20] + "\"");
        }
        return map;
    }

    private static WebResponse StudioStatic(WebRequest r)
    {
        if (r.Method != "GET") return Json(405, Error("GET only"));
        var rel = r.Path is "/studio" or "/studio/" ? "index.html" : r.Path["/studio/".Length..];
        if (rel.Contains("..") || rel.Contains('\\') || rel.Contains(':') || !StaticFiles.TryGetValue(rel, out var f))
            return new WebResponse(404, "not found", "text/plain; charset=utf-8") { Csp = StudioApi.JsonCsp };
        // Working on the front-end: ALBION_STUDIO_WEB=<server>/web/studio serves the files from disk (only those the build embeds).
        if (Environment.GetEnvironmentVariable("ALBION_STUDIO_WEB") is { Length: > 0 } dev && File.Exists(Path.Combine(dev, rel)))
        {
            var bytes = File.ReadAllBytes(Path.Combine(dev, rel));
            f = (bytes, f.Type, "\"" + Convert.ToHexString(SHA256.HashData(bytes))[..20] + "\"");
        }
        if (r.Headers.GetValueOrDefault("If-None-Match") == f.ETag)
        {
            var nm = new WebResponse(304, "", f.Type) { Csp = StudioApi.PageCsp, CacheControl = "no-cache" };
            nm.Headers.Add(("ETag", f.ETag));
            return nm;
        }
        var response = new WebResponse(200, "", f.Type) { Bytes = f.Bytes, Csp = StudioApi.PageCsp, CacheControl = "no-cache" };
        response.Headers.Add(("ETag", f.ETag));
        return response;
    }

    // ---- the API

    private StudioIndex? Index(ModCatalog c)
    {
        lock (_studioLock)
        {
            if (_studioIndex is { } x && ReferenceEquals(x.Catalog, c)) return x.Index;
            var index = StudioIndex.Build(c, _icons);
            _studioIndex = (c, index);
            return index;
        }
    }

    private (ModCatalog Catalog, StudioIndex Index, Overlay Overlay)? Ready(out WebResponse? wait)
    {
        var (c, problem) = Catalog();
        if (c is null)
        {
            wait = Json(503, Error(problem ?? "the catalogue is loading (10-30 s, once)", new JsonObject { ["loading"] = problem is null }));
            return null;
        }
        wait = null;
        return (c, Index(c)!, PackOverlay());
    }

    private StudioContext Context(ModCatalog c, Overlay o, string pack, Dictionary<(string, string), JsonObject> drafts) => new()
    {
        Catalog = c, Icons = _icons, Pack = pack, Drafts = drafts,
        FindItem = n => FindItem(c, o, n), FindSpell = n => FindSpell(c, o, n), Names = t => Names(c, o, t),
    };

    private StudioStore Store(string? pack) =>
        new(PackFiles.Open(_modsDir, pack) ?? throw new InvalidDataException($"no pack '{pack}'"));

    private async Task<WebResponse> StudioApiCall(WebRequest r, AdminSession s, string path)
    {
        var parts = path.Split('/');
        if (parts is ["packs", var packName, ..] && parts.Length >= 3 && packName is not ("create" or "import"))
            return await PackCall(r, s, packName, string.Join('/', parts[2..]));
        if (parts is ["rules", _, ..]) return await RulesCall(r, s, string.Join('/', parts[1..])); // the no-code rules (WebPanel.Rules.cs)
        switch (r.Method, path)
        {
            case ("GET", "session"): return Json(200, SessionJson(s));
            case ("GET", "packs"): return Json(200, PacksJson());
            case ("GET", "jobs"):
                lock (_studioLock) return Json(200, new JsonObject { ["jobs"] = new JsonArray([.. _studioJobs.AsEnumerable().Reverse().Select(j => (JsonNode)j.ToJson())]) });
            case ("GET", "status"): return Json(200, await StatusJson());
            case ("GET", "icons"): return Json(200, IconSearch(r, r.Q("for") == "item" ? Catalog().Catalog?.Sprites : null));
            case ("POST", "packs/create"): return CreatePackApi(r, s);
            case ("POST", "packs/import"): return ImportPack(r, s);
            case ("POST", "preview"): return Preview(r);
            case ("POST", "actions/validate" or "actions/install" or "actions/restore"): return StartStudioJob(r, s, path["actions/".Length..]);
        }
        if (Ready(out var wait) is not { } ready) return wait!;
        var (c, index, o) = ready;
        return (r.Method, parts) switch
        {
            ("GET", ["facets", var kind]) => Json(200, index.Facets(kind)),
            ("GET", ["search", var kind]) => Json(200, Search(r, kind, index, c, o)),
            ("GET", ["detail", var kind]) => Detail(kind, r.Q("name"), c, o),
            ("GET", ["lookup"]) => Json(200, Lookup(r.Q("refs"), c, o)),
            ("GET", ["models"]) => Json(200, ModelSearch(r, c, o)),
            ("GET", ["recipes"]) => Json(200, RecipesJson(c, o)),
            ("POST", ["recipes", "build"]) => RecipeBuild(r, c, o),
            ("POST", ["recipes", "ranges"]) => RecipeRanges(r, c, o),
            ("GET", ["ranges", var kind]) => Json(200, AttrRanges(kind, r.Q("template"), c, o)),
            _ => Json(404, Error($"no API {r.Method} /studio/api/{path}")),
        };
    }

    private JsonObject SessionJson(AdminSession s)
    {
        var (c, problem) = Catalog();
        return new JsonObject
        {
            ["user"] = s.Name, ["csrf"] = s.Csrf, ["serverBuild"] = ServerBuild, ["clientBuild"] = _clientBuild ?? c?.Build,
            ["catalog"] = new JsonObject
            {
                ["ready"] = c is not null, ["problem"] = problem, ["items"] = c?.Items.Count, ["spells"] = c?.Spells.Count, ["mobs"] = c?.MobEls.Count,
                ["lootLists"] = c?.LootListEls.Count, ["stations"] = c?.StationEls.Count, ["build"] = c?.Build,
            },
            ["icons"] = _icons.Available, ["modsDir"] = _modsDir, ["modsProblem"] = _modsProblem, ["official"] = _modPaths is not null,
            ["kinds"] = J.Strings(StudioKinds.All.Select(k => k.Kind)),
        };
    }

    private JsonObject PacksJson()
    {
        var installed = SafeState()?.Packs.Select(x => x.Split(' ')[0]).ToHashSet(StringComparer.Ordinal) ?? [];
        var list = new JsonArray();
        foreach (var name in PackFiles.List(_modsDir))
        {
            if (PackFiles.Open(_modsDir, name) is not { } p) continue;
            var info = p.Info();
            var store = new StudioStore(p);
            var (drafts, broken) = store.All();
            var (_, at) = store.Manifest();
            list.Add(new JsonObject
            {
                ["name"] = name, ["version"] = info.GetValueOrDefault("Version"), ["description"] = info.GetValueOrDefault("Description"), ["client"] = info.GetValueOrDefault("Client"),
                ["drafts"] = new JsonObject([.. StudioKinds.All.Select(k => KeyValuePair.Create(k.Kind, (JsonNode?)drafts.Keys.Count(d => d.Kind == k.Kind)))]),
                ["broken"] = broken.Count, ["installed"] = installed.Contains(name), ["dirty"] = store.Dirty(), ["generated"] = at?.ToString("o"),
            });
        }
        return new JsonObject { ["packs"] = list, ["modsDir"] = _modsDir };
    }

    private async Task<JsonObject> StatusJson()
    {
        if (_modPaths is null) return new JsonObject { ["official"] = false, ["text"] = "No official GameData found next to Paths:ClientGameData: validate, install and restore are not available." };
        var text = await Task.Run(() =>
        {
            var w = new StringWriter();
            try { new ModKit(_modPaths, w).Status(); }
            catch (Exception e) when (e is IOException or InvalidDataException or UnauthorizedAccessException) { w.WriteLine("status failed: " + e.Message); }
            return w.ToString();
        });
        var state = SafeState();
        return new JsonObject
        {
            ["official"] = true, ["local"] = _modPaths.Local, ["officialDir"] = _modPaths.Official, ["text"] = text,
            ["installed"] = J.Strings(state?.Packs ?? []), ["built"] = state?.BuiltUtc?.ToString("o"),
        };
    }

    /// <summary>The icon cache's sprites (only the ones official items use when <paramref name="only"/> is given: an item's uisprite).</summary>
    private JsonObject IconSearch(WebRequest r, HashSet<string>? only)
    {
        var q = r.Q("q").Trim();
        var atlas = r.Q("atlas");
        var all = _icons.Entries().AsEnumerable();
        if (only is not null) all = all.Where(e => only.Contains(e.Sprite));
        if (atlas.Length > 0) all = all.Where(e => e.Atlas.StartsWith(atlas, StringComparison.OrdinalIgnoreCase));
        foreach (var w in q.Split(' ', StringSplitOptions.RemoveEmptyEntries)) all = all.Where(e => e.Sprite.Contains(w, StringComparison.OrdinalIgnoreCase));
        var list = all.ToList();
        var offset = Math.Max(0, int.TryParse(r.Q("offset"), out var off) ? off : 0);
        var limit = Math.Clamp(int.TryParse(r.Q("limit"), out var lim) ? lim : 120, 1, 400);
        return new JsonObject
        {
            ["total"] = list.Count, ["offset"] = offset, ["available"] = _icons.Available,
            ["atlases"] = J.Strings(_icons.Entries().Select(e => e.Atlas).Distinct().Order()),
            ["rows"] = new JsonArray([.. list.Skip(offset).Take(limit).Select(e => (JsonNode)new JsonObject { ["sprite"] = e.Sprite, ["atlas"] = e.Atlas, ["icon"] = StudioApi.IconUrl(e.Sprite) })]),
        };
    }

    // ---- search, detail, lookup

    private IEnumerable<StudioRow> OverlayRows(string kind, ModCatalog c, Overlay o) => kind switch
    {
        "item" => o.Items.Select(i => StudioIndex.ItemRow(Names(c, o, "@ITEMS_" + i.Name), i, _icons)),
        "spell" => o.Spells.Select(x => StudioIndex.SpellRow(Names(c, o, ModCatalog.SpellNameKey(x.El)), x, c, _icons)),
        _ => [],
    };

    private JsonObject Search(WebRequest r, string kind, StudioIndex index, ModCatalog c, Overlay o)
    {
        var filters = r.Query.Where(q => q.Key.StartsWith("f.", StringComparison.Ordinal)).ToDictionary(q => q.Key[2..], q => q.Value);
        var offset = int.TryParse(r.Q("offset"), out var off) ? off : 0;
        var limit = int.TryParse(r.Q("limit"), out var lim) ? lim : 40;
        var (total, rows) = index.Search(kind, r.Q("official") == "1" ? [] : OverlayRows(kind, c, o), r.Q("q").Trim(), filters, offset, limit);
        return new JsonObject { ["kind"] = kind, ["total"] = total, ["offset"] = offset, ["rows"] = new JsonArray([.. rows.Select(x => (JsonNode)x.ToJson(_icons))]) };
    }

    /// <summary>
    /// The model picker: the character models official equipment uses (q: words of the model or of its items' names; slot: the
    /// slottype, e.g. cape), each with the first item that uses it (its icon and name) and how many use it.
    /// </summary>
    private JsonObject ModelSearch(WebRequest r, ModCatalog c, Overlay o)
    {
        var slot = r.Q("slot").Trim();
        var words = r.Q("q").Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        var rows = c.Models.Where(m => slot.Length == 0 || m.Value.Any(i => string.Equals(i.Slot, slot, StringComparison.OrdinalIgnoreCase)))
            .Select(m => (Mesh: m.Key, Users: m.Value, Names: Names(c, o, "@ITEMS_" + m.Value[0].Name)))
            .Where(m => words.All(w => m.Mesh.Contains(w, StringComparison.OrdinalIgnoreCase) || (m.Names.En ?? "").Contains(w, StringComparison.OrdinalIgnoreCase)
                                       || (m.Names.Ru ?? "").Contains(w, StringComparison.OrdinalIgnoreCase)))
            .OrderBy(m => m.Users[0].Tier).ThenBy(m => m.Mesh, StringComparer.Ordinal).ToList();
        var offset = Math.Max(0, int.TryParse(r.Q("offset"), out var off) ? off : 0);
        var limit = Math.Clamp(int.TryParse(r.Q("limit"), out var lim) ? lim : 60, 1, 300);
        return new JsonObject
        {
            ["total"] = rows.Count, ["offset"] = offset,
            ["slots"] = J.Strings(c.Models.Values.Select(v => v[0].Slot).Where(x => x.Length > 0).Distinct(StringComparer.OrdinalIgnoreCase).Order(StringComparer.Ordinal)),
            ["rows"] = new JsonArray([.. rows.Skip(offset).Take(limit).Select(m => (JsonNode)new JsonObject
            {
                ["kind"] = "model", ["name"] = m.Mesh, ["en"] = m.Names.En, ["ru"] = m.Names.Ru, ["item"] = m.Users[0].Name, ["slot"] = m.Users[0].Slot, ["users"] = m.Users.Count,
                ["icon"] = _icons.ForItem(m.Users[0].El) is { } sp ? StudioApi.IconUrl(sp) : null,
            })]),
        };
    }

    private static string AttrType(XElement e, XAttribute a)
    {
        if (IsAsset(e, a) || a.Name.LocalName is "uniquename" or "mesh" or "uisprite") return "asset";
        var v = a.Value;
        if (v is "true" or "false") return "bool";
        if (IsNumber(v)) return v.Contains('.') || v.Contains('e') || v.Contains('E') ? "number" : "int";
        return a.Name.LocalName is "consumespell" or "spell" or "effect" ? "spell" : "text";
    }

    private static JsonArray Attrs(XElement e) => new([.. e.Attributes().Where(a => !a.IsNamespaceDeclaration)
        .Select(a => (JsonNode)new JsonObject { ["name"] = a.Name.LocalName, ["value"] = a.Value, ["type"] = AttrType(e, a) })]);

    private JsonObject Ref(string kind, string name, ModCatalog c, Overlay o)
    {
        switch (kind)
        {
            case "item":
                var i = FindItem(c, o, name);
                var (ien, iru) = Names(c, o, "@ITEMS_" + name);
                return new JsonObject { ["kind"] = kind, ["name"] = name, ["found"] = i is not null, ["en"] = ien, ["ru"] = iru, ["tier"] = i?.Tier, ["pack"] = i?.Pack,
                    ["icon"] = i is not null && _icons.ForItem(i.El) is { } isp ? StudioApi.IconUrl(isp) : null };
            case "spell":
                var sp = FindSpell(c, o, name);
                var (sen, sru) = sp is null ? (null, null) : Names(c, o, ModCatalog.SpellNameKey(sp.El));
                return new JsonObject { ["kind"] = kind, ["name"] = name, ["found"] = sp is not null, ["en"] = sen, ["ru"] = sru, ["pack"] = sp?.Pack,
                    ["icon"] = sp is not null && _icons.ForSpell(sp.El) is { } ssp ? StudioApi.IconUrl(ssp) : null };
            case "mob":
                var mob = c.MobEls.GetValueOrDefault(name);
                var (men, mru) = mob is null ? (null, null) : c.Loc.GetValueOrDefault(ModCatalog.MobNameKey(mob));
                if (mob is null && PackDraft("mob", name) is { } variant) return variant;
                return new JsonObject { ["kind"] = kind, ["name"] = name, ["found"] = mob is not null || name.Contains('*') || name.Contains('?'), ["pattern"] = name.Contains('*') || name.Contains('?'), ["en"] = men, ["ru"] = mru };
            case "station":
                var (ten, tru) = c.Loc.GetValueOrDefault("@BUILDINGS_" + name);
                return new JsonObject { ["kind"] = kind, ["name"] = name, ["found"] = c.StationEls.ContainsKey(name), ["en"] = ten, ["ru"] = tru };
            case "lootlist":
                if (!c.LootListEls.ContainsKey(name) && PackDraft("lootlist", name) is { } own) return own;
                return new JsonObject { ["kind"] = kind, ["name"] = name, ["found"] = c.LootListEls.ContainsKey(name) };
            case "icon":
                return new JsonObject { ["kind"] = kind, ["name"] = name, ["found"] = c.Sprites.Contains(name) || _icons.Has(name),
                    ["icon"] = _icons.Has(name) ? StudioApi.IconUrl(name) : null };
            case "model":
                var user = c.Models.GetValueOrDefault(name)?.FirstOrDefault();
                var (uen, uru) = user is null ? (null, null) : Names(c, o, "@ITEMS_" + user.Name);
                return new JsonObject { ["kind"] = kind, ["name"] = name, ["found"] = c.ModelKnown(name), ["en"] = uen, ["ru"] = uru, ["item"] = user?.Name,
                    ["icon"] = user is not null && _icons.ForItem(user.El) is { } usp ? StudioApi.IconUrl(usp) : null };
            case "chest":
                return new JsonObject { ["kind"] = kind, ["name"] = name, ["found"] = c.Chests.Contains(name, StringComparer.OrdinalIgnoreCase) || name.Contains('*') };
            default:
                return new JsonObject { ["kind"] = kind, ["name"] = name, ["found"] = false };
        }
    }

    /// <summary>refs=item:T6_METALBAR,spell:HEROICSTRIKE2 → {"item:T6_METALBAR": {found, en, ru, icon}, …} (at most 200).</summary>
    private JsonObject Lookup(string refs, ModCatalog c, Overlay o)
    {
        var result = new JsonObject();
        foreach (var r in refs.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).Distinct().Take(200))
            if (r.IndexOf(':') is > 0 and var colon) result[r] = Ref(r[..colon], r[(colon + 1)..], c, o);
        return result;
    }

    private WebResponse Detail(string kind, string name, ModCatalog c, Overlay o)
    {
        var used = DraftUsers(kind, name);
        switch (kind)
        {
            case "item":
            {
                if (FindItem(c, o, name) is not { } i) return Json(404, Error($"no item {name}"));
                var basics = Ref("item", i.Name, c, o);
                var (den, dru) = Names(c, o, i.El.Attribute("descriptionlocatag")?.Value is { Length: > 1 } dk ? dk : "@ITEMS_" + i.Name + "_DESC");
                var spells = ItemKind.Spells(Context(c, o, "", []), i.El);
                var usedIn = c.Items.Where(x => x.El.Elements("craftingrequirements").Elements("craftresource").Any(cr => string.Equals((string?)cr.Attribute("uniquename"), i.Name, StringComparison.OrdinalIgnoreCase)))
                    .Take(60).Select(x => x.Name).ToList();
                basics["descEn"] = den;
                basics["descRu"] = dru;
                basics["type"] = i.Kind;
                basics["slot"] = i.Slot;
                basics["sprite"] = ModBuilder.SpriteOf(i.El);
                basics["model"] = ItemKind.HasModel(i.El) ? ModBuilder.MeshOf(i.El) : null;
                basics["attributes"] = Attrs(i.El);
                basics["spells"] = new JsonArray([.. spells.Select(x => { var o2 = Ref("spell", x.Spell, c, o); o2["slot"] = x.Slot; o2["tag"] = x.Tag; return (JsonNode)o2; })]);
                basics["recipes"] = new JsonArray([.. i.El.Elements("craftingrequirements").Select(req => (JsonNode)new JsonObject
                {
                    ["silver"] = req.Attribute("silver")?.Value, ["time"] = req.Attribute("time")?.Value, ["focus"] = req.Attribute("craftingfocus")?.Value,
                    ["amount"] = req.Attribute("amountcrafted")?.Value,
                    ["resources"] = new JsonArray([.. req.Elements("craftresource").Select(cr => { var x = Ref("item", cr.Attribute("uniquename")?.Value ?? "", c, o); x["count"] = cr.Attribute("count")?.Value; return (JsonNode)x; })]),
                })]);
                basics["stations"] = new JsonArray([.. (c.StationsOf.GetValueOrDefault(i.Name) ?? []).Select(x => x.Station).Distinct().Select(st => (JsonNode)Ref("station", st, c, o))]);
                basics["enchantments"] = i.El.Element("enchantments")?.Elements("enchantment").Count() ?? 0;
                basics["drops"] = new JsonObject
                {
                    ["sources"] = J.Strings(c.DropSources(i.Name)), ["lists"] = J.Strings(c.LootLists(i.Name)),
                    ["packs"] = new JsonArray([.. (o.Drops.GetValueOrDefault(i.Name) ?? []).Select(d => (JsonNode)new JsonObject { ["pack"] = d.Item, ["text"] = DropText(d) })]),
                };
                basics["usedIn"] = new JsonObject { ["recipes"] = J.Strings(usedIn), ["drafts"] = used };
                basics["xml"] = Cut(i.El.ToString());
                return Json(200, basics);
            }
            case "spell":
            {
                if (FindSpell(c, o, name) is not { } x) return Json(404, Error($"no spell {name}"));
                var ctx = Context(c, o, "", []);
                var basics = Ref("spell", x.Name, c, o);
                var described = SpellText.Describe(ctx, x.Name, null);
                basics["description"] = described["description"]?.DeepClone();
                basics["type"] = x.Kind;
                basics["category"] = x.El.Attribute("category")?.Value;
                basics["attributes"] = Attrs(x.El);
                var prepared = PrepareSpell(x.El);
                var blocks = new JsonArray();
                var count = 0;
                foreach (var e in prepared.Descendants())
                {
                    if (count > 600) break;
                    var attrs = Attrs(e);
                    count += attrs.Count;
                    blocks.Add(new JsonObject { ["path"] = SpellKind.PathOf(prepared, e), ["element"] = e.Name.LocalName, ["depth"] = e.Ancestors().TakeWhile(a => a != prepared).Count(), ["attributes"] = attrs });
                }
                basics["blocks"] = blocks;
                basics["offeredBy"] = new JsonArray([.. (c.OfferedBy.GetValueOrDefault(x.Name) ?? []).Take(60).Select(n => (JsonNode)Ref("item", n, c, o))]);
                basics["uses"] = new JsonArray([.. SpellKind.SpellRefs(ctx, prepared).Take(40).Select(n => (JsonNode)Ref("spell", n, c, o))]);
                basics["usedIn"] = new JsonObject { ["drafts"] = used };
                basics["xml"] = Cut(x.El.ToString());
                return Json(200, basics);
            }
            case "mob":
            {
                if (!c.MobEls.TryGetValue(name, out var mob)) return Json(404, Error($"no mob {name}"));
                var basics = Ref("mob", name, c, o);
                basics["category"] = c.Mobs.GetValueOrDefault(name);
                basics["attributes"] = Attrs(mob);
                basics["loot"] = J.Strings(mob.Descendants("LootListReference").Select(l => l.Attribute("name")?.Value).OfType<string>().Distinct());
                basics["xml"] = Cut(mob.ToString());
                return Json(200, basics);
            }
            case "lootlist":
            {
                if (!c.LootListEls.TryGetValue(name, out var list)) return Json(404, Error($"no loot list {name}"));
                var basics = Ref("lootlist", name, c, o);
                basics["items"] = new JsonArray([.. list.Descendants("Item").Take(200).Select(it =>
                {
                    var x = Ref("item", it.Attribute("type")?.Value ?? "", c, o);
                    x["attributes"] = Attrs(it);
                    return (JsonNode)x;
                })]);
                basics["lists"] = J.Strings(list.Descendants("LootListReference").Select(l => l.Attribute("name")?.Value).OfType<string>().Distinct());
                basics["parents"] = J.Strings(c.ListParents(name));
                basics["users"] = J.Strings(c.ListUsers(name));
                basics["xml"] = Cut(list.ToString());
                return Json(200, basics);
            }
            case "station":
            {
                if (!c.StationEls.TryGetValue(name, out var b)) return Json(404, Error($"no station {name}"));
                var basics = Ref("station", name, c, o);
                basics["attributes"] = Attrs(b);
                var crafts = b.Element("craftingitemlist")?.Elements("craftitem").Select(ci => ci.Attribute("uniquename")?.Value).OfType<string>().ToList() ?? [];
                basics["crafts"] = new JsonArray([.. crafts.Take(300).Select(n => (JsonNode)Ref("item", n, c, o))]);
                basics["craftsTotal"] = crafts.Count;
                return Json(200, basics);
            }
        }
        return Json(404, Error($"no kind {kind}"));
    }

    private static string Cut(string text) => text.Length > 200_000 ? text[..200_000] + "\n…" : text;

    /// <summary>The drafts (in every pack) that reference <paramref name="kind"/> <paramref name="name"/>.</summary>
    private JsonArray DraftUsers(string kind, string name, string? skipPack = null, string? skipId = null)
    {
        var list = new JsonArray();
        foreach (var packName in PackFiles.List(_modsDir))
        {
            if (PackFiles.Open(_modsDir, packName) is not { } p) continue;
            foreach (var ((k, id), d) in new StudioStore(p).All().Drafts)
            {
                if (packName == skipPack && id == skipId && k == kind) continue;
                foreach (var rf in StudioKinds.Get(k)!.References(d))
                    if (rf.Kind == kind && string.Equals(rf.Name, name, StringComparison.OrdinalIgnoreCase))
                        list.Add(new JsonObject { ["pack"] = packName, ["kind"] = k, ["id"] = id, ["path"] = rf.Path });
            }
        }
        return list;
    }

    // ---- packs

    private WebResponse CreatePackApi(WebRequest r, AdminSession s)
    {
        if (_modsProblem is not null) return Json(403, Error(_modsProblem));
        var b = Body(r);
        var name = J.Str(b["name"]).Trim();
        var error = PackFiles.Create(_modsDir, name, J.Str(b["version"], "1.0.0").Trim() is { Length: > 0 } v ? v : "1.0.0", Clean(J.Str(b["description"]), 300),
            _modPaths is null ? null : ModBuilder.ReadBuild(_modPaths.Official));
        if (error is not null) return Json(400, Error(error));
        Directory.CreateDirectory(Path.Combine(_modsDir, name, "studio"));
        Audit($"admin '{s.Name}'", r.Remote, $"studio: created pack {name}");
        return Json(200, new JsonObject { ["ok"] = true, ["name"] = name });
    }

    private WebResponse ImportPack(WebRequest r, AdminSession s)
    {
        if (_modsProblem is not null) return Json(403, Error(_modsProblem));
        var name = r.Q("name").Trim();
        var client = _modPaths is null ? null : ModBuilder.ReadBuild(_modPaths.Official);
        var zip = r.Body is [0x50, 0x4B, ..];
        var error = zip ? StudioStore.ImportZip(_modsDir, name, r.Body, client) : StudioStore.ImportJson(_modsDir, name, Body(r), client);
        if (error is not null) return Json(400, Error(error));
        Audit($"admin '{s.Name}'", r.Remote, $"studio: imported pack {name} ({(zip ? "zip" : "json")}, {r.Body.Length} bytes)");
        return Json(200, new JsonObject { ["ok"] = true, ["name"] = name });
    }

    private async Task<WebResponse> PackCall(WebRequest r, AdminSession s, string packName, string sub)
    {
        var store = Store(packName);
        var who = $"admin '{s.Name}'";
        if (r.Method == "POST" && _modsProblem is not null) return Json(403, Error(_modsProblem));
        switch (r.Method, sub)
        {
            case ("GET", "export"):
            {
                var zip = r.Q("format") != "json";
                var response = zip
                    ? new WebResponse(200, "", "application/zip") { Bytes = store.ExportZip(), Csp = StudioApi.JsonCsp }
                    : Json(200, store.ExportJson());
                response.Headers.Add(("Content-Disposition", $"attachment; filename=\"{store.Pack.Name}{(zip ? ".zip" : ".studio.json")}\""));
                return response;
            }
            case ("GET", "files"):
            {
                var files = new JsonObject();
                foreach (var f in PackFiles.PatchFiles.Where(store.Pack.Has)) files[f] = Cut(store.Pack.Raw(f));
                var drops = Path.Combine(store.Pack.Dir, "server", "drops.json");
                if (File.Exists(drops)) files["server/drops.json"] = Cut(File.ReadAllText(drops));
                return Json(200, new JsonObject { ["files"] = files });
            }
            case ("GET", "history"):
                return Json(200, new JsonObject
                {
                    ["versions"] = new JsonArray([.. store.History(r.Q("kind"), r.Q("id")).Select(v => (JsonNode)new JsonObject { ["stamp"] = v.Stamp, ["size"] = v.Size })]),
                });
            case ("GET", "version"):
                return store.Version(r.Q("kind"), r.Q("id"), r.Q("stamp")) is { } ver ? Json(200, ver) : Json(404, Error("no such version"));
            case ("POST", "rename"):
            {
                var to = J.Str(Body(r)["to"]).Trim();
                if (!PackFiles.PackName().IsMatch(to)) return Json(400, Error("the pack name: 1 to 40 of a-z, 0-9 and -"));
                if (Directory.Exists(Path.Combine(_modsDir, to))) return Json(409, Error($"mods/{to} exists already"));
                if (StudioBusy() is { } busy) return Json(409, Error(busy));
                Directory.Move(store.Pack.Dir, Path.Combine(Path.GetFullPath(_modsDir), to));
                var info = JsonNode.Parse(File.ReadAllText(Path.Combine(_modsDir, to, "pack.json")), null, J.Read) as JsonObject ?? [];
                info["Name"] = to;
                StudioStore.WriteText(Path.Combine(_modsDir, to, "pack.json"), info.ToJsonString(J.Indented));
                Audit(who, r.Remote, $"studio: renamed pack {packName} to {to}");
                var installed = SafeState()?.Packs.Any(p => p.Split(' ')[0] == packName) ?? false;
                return Json(200, new JsonObject { ["ok"] = true, ["name"] = to, ["note"] = installed ? "this pack is installed under its old name: install it again (and fix Mods:Packs)" : null });
            }
        }
        if (Ready(out var wait) is not { } ready) return wait!;
        var (c, _, o) = ready;
        var (drafts, broken) = store.All();
        var ctx = Context(c, o, store.Pack.Name, drafts);
        switch (r.Method, sub)
        {
            case ("GET", "entities"):
                return Json(200, EntitiesJson(store, ctx, drafts, broken));
            case ("GET", "entity"):
            {
                var d = store.Get(r.Q("kind"), r.Q("id"));
                return d is null ? Json(404, Error($"no {r.Q("kind")} draft {r.Q("id")}")) : Json(200, new JsonObject { ["draft"] = d, ["diagnostics"] = Diags(StudioKinds.Get(r.Q("kind"))!.Validate(ctx, d)) });
            }
            case ("GET", "where-used"):
                return Json(200, new JsonObject { ["dependents"] = DraftUsers(r.Q("kind"), r.Q("id"), packName, r.Q("id")) });
            case ("POST", "entity/new"):
            {
                var b = Body(r);
                var kind = J.Str(b["kind"]);
                var id = J.Str(b["id"]).Trim().ToUpperInvariant();
                var from = J.Str(b["from"]).Trim();
                if (StudioKinds.Get(kind) is not { } k) return Json(400, Error($"no kind {kind}"));
                if (!PackFiles.NewName().IsMatch(id)) return Json(400, Error("the unique name: 3 to 80 of A-Z, 0-9 and _"));
                if (store.Exists(kind, id)) return Json(409, Error($"this pack has a {kind} draft {id} already"));
                if ((kind == "item" ? FindItem(c, o, id) is { } ti && ti.Pack != packName : kind == "spell" && FindSpell(c, o, id) is { } ts && ts.Pack != packName))
                    return Json(409, Error($"{id} exists already (official or in another pack): choose another name"));
                var draft = k.NewFrom(ctx, from, id);
                store.Save(draft);
                Audit(who, r.Remote, $"studio: new {kind} {id} from {from} in pack {packName}");
                drafts[(kind, id)] = draft;
                return Json(200, new JsonObject { ["draft"] = store.Get(kind, id), ["diagnostics"] = Diags(k.Validate(Context(c, o, packName, drafts), draft)) });
            }
            case ("POST", "entity/save"):
            {
                var draft = J.Obj(Body(r)["draft"]);
                var kind = J.Str(draft["kind"]);
                var id = J.Str(draft["id"]);
                if (StudioKinds.Get(kind) is not { } k) return Json(400, Error($"no kind {kind}"));
                store.Save(draft);
                drafts[(kind, id)] = draft;
                Audit(who, r.Remote, $"studio: saved {kind} {id} in pack {packName}");
                return Json(200, new JsonObject { ["draft"] = store.Get(kind, id), ["diagnostics"] = Diags(k.Validate(Context(c, o, packName, drafts), draft)) });
            }
            case ("POST", "entity/delete"):
            {
                var b = Body(r);
                var kind = J.Str(b["kind"]);
                var id = J.Str(b["id"]);
                var dependents = DraftUsers(kind, id, packName, id);
                if (dependents.Count > 0 && b["force"]?.GetValue<bool>() != true)
                    return Json(409, Error($"{dependents.Count} draft(s) use {id}: delete anyway (their references break) or change them first", new JsonObject { ["dependents"] = dependents }));
                if (!store.Delete(kind, id)) return Json(404, Error($"no {kind} draft {id}"));
                Audit(who, r.Remote, $"studio: deleted {kind} {id} in pack {packName}");
                return Json(200, new JsonObject { ["ok"] = true, ["note"] = "deleted (its last version stays in the history); Generate takes its elements out of the patch files" });
            }
            case ("POST", "entity/rename-preview" or "entity/rename"):
            {
                var b = Body(r);
                var kind = J.Str(b["kind"]);
                var id = J.Str(b["id"]);
                var to = J.Str(b["to"]).Trim().ToUpperInvariant();
                if (store.Get(kind, id) is not { } draft) return Json(404, Error($"no {kind} draft {id}"));
                var problems = new List<string>();
                if (!PackFiles.NewName().IsMatch(to)) problems.Add("the new name: 3 to 80 of A-Z, 0-9 and _");
                if (store.Exists(kind, to)) problems.Add($"a {kind} draft {to} exists already");
                if (kind == "item" && FindItem(c, o, to) is { } ti2 && ti2.Pack != packName || kind == "spell" && FindSpell(c, o, to) is { } ts2 && ts2.Pack != packName)
                    problems.Add($"{to} exists already (official or in another pack)");
                var dependents = DraftUsers(kind, id, packName, id);
                if (sub == "entity/rename-preview" || problems.Count > 0)
                    return Json(problems.Count > 0 && sub == "entity/rename" ? 400 : 200, new JsonObject
                    {
                        ["from"] = id, ["to"] = to, ["problems"] = J.Strings(problems), ["dependents"] = dependents,
                        ["note"] = "the references in this pack's drafts are renamed with it; other packs' drafts are listed and keep the old name",
                    });
                store.Rename(kind, id, to, draft);
                var changed = 0;
                foreach (var ((k2, id2), d2) in store.All().Drafts)
                    if (StudioKinds.Get(k2)!.RenameRefs(d2, kind, id, to) is > 0 and var n) { store.Save(d2); changed += n; }
                Audit(who, r.Remote, $"studio: renamed {kind} {id} to {to} in pack {packName} ({changed} reference(s))");
                return Json(200, new JsonObject { ["ok"] = true, ["id"] = to, ["references"] = changed });
            }
            case ("POST", "entity/restore"):
            {
                var b = Body(r);
                var kind = J.Str(b["kind"]);
                var id = J.Str(b["id"]);
                if (store.Version(kind, id, J.Str(b["stamp"])) is not { } old) return Json(404, Error("no such version"));
                old["kind"] = kind;
                old["id"] = id;
                store.Save(old);
                Audit(who, r.Remote, $"studio: restored {kind} {id} to {J.Str(b["stamp"])} in pack {packName}");
                return Json(200, new JsonObject { ["draft"] = store.Get(kind, id) });
            }
            case ("POST", "recipe/create"):
                return RecipeCreate(r, store, ctx, drafts, who);
            case ("POST", "adopt"):
                return Adopt(r, store, ctx, who);
            case ("POST", "generate"):
            {
                if (StudioBusy() is { } busy) return Json(409, Error(busy));
                var (written, problems) = store.Generate(ctx);
                Audit(who, r.Remote, $"studio: generated pack {packName} ({string.Join(", ", written)})");
                // The extra action bars read their files at the start: a running pack's change takes effect now (SPEC extrabars §2).
                if (written.Contains("server/extraspells.json") && _world.Config.ModPacks.Contains(packName, StringComparer.OrdinalIgnoreCase))
                    await OnWorld(() => _world.ExtraBars.Reload());
                return Json(200, new JsonObject { ["written"] = J.Strings(written), ["diagnostics"] = Diags(problems), ["ok"] = !problems.Any(p => p.Severity == StudioDiag.Blocker) });
            }
        }
        await Task.CompletedTask;
        return Json(404, Error($"no API {r.Method} packs/{packName}/{sub}"));
    }

    private static JsonArray Diags(IEnumerable<StudioDiag> list) => new([.. list.Select(d => (JsonNode)d.ToJson())]);

    private JsonObject EntitiesJson(StudioStore store, StudioContext ctx, Dictionary<(string Kind, string Id), JsonObject> drafts, List<string> broken)
    {
        var list = new JsonArray();
        foreach (var ((kind, id), d) in drafts.OrderBy(x => x.Key.Kind).ThenBy(x => x.Key.Id))
        {
            var k = StudioKinds.Get(kind)!;
            var diags = k.Validate(ctx, d);
            var template = k.TemplateElement(ctx, d);
            var sprite = template is null ? null : kind == "item" ? _icons.ForItem(template) : kind == "spell" ? _icons.ForSpell(template) : null;
            list.Add(new JsonObject
            {
                ["kind"] = kind, ["id"] = id, ["template"] = J.Str(d["template"]), ["name"] = J.Obj(d["name"]).DeepClone(),
                ["icon"] = sprite is null ? null : StudioApi.IconUrl(sprite), ["updated"] = J.Str(d["updated"]),
                ["problems"] = new JsonObject
                {
                    ["blocker"] = diags.Count(x => x.Severity == StudioDiag.Blocker), ["warning"] = diags.Count(x => x.Severity == StudioDiag.Warning), ["info"] = diags.Count(x => x.Severity == StudioDiag.Info),
                },
            });
        }
        // The pack's other new elements (the old editor's or hand-written): shown, not edited here.
        var (made, at) = store.Manifest();
        var generated = made.Values.SelectMany(v => v).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var legacy = new JsonArray();
        foreach (var (file, kind) in new[] { ("items.xml", "item"), ("spells.xml", "spell") })
            foreach (var e in PackFiles.Added(store.Pack.Patch(file)))
                if (e.Attribute("uniquename")?.Value is { } n && !generated.Contains(n) && !drafts.ContainsKey((kind, n)))
                    legacy.Add(new JsonObject { ["kind"] = kind, ["id"] = n, ["template"] = store.Pack.Templates().GetValueOrDefault(n) });
        return new JsonObject
        {
            ["pack"] = store.Pack.Name, ["entities"] = list, ["legacy"] = legacy, ["broken"] = J.Strings(broken),
            ["dirty"] = store.Dirty(), ["generated"] = at?.ToString("o"),
        };
    }

    // ---- preview

    private WebResponse Preview(WebRequest r)
    {
        if (Ready(out var wait) is not { } ready) return wait!;
        var (c, _, o) = ready;
        var b = Body(r);
        var draft = J.Obj(b["draft"]);
        var kind = J.Str(draft["kind"]);
        if (StudioKinds.Get(kind) is not { } k) return Json(400, Error($"no kind {kind}"));
        var packName = J.Str(b["pack"]);
        var drafts = PackFiles.Open(_modsDir, packName) is { } p ? new StudioStore(p).All().Drafts : [];
        drafts[(kind, J.Str(draft["id"]))] = draft;
        var ctx = Context(c, o, packName, drafts);
        var diags = k.Validate(ctx, draft);
        var result = new JsonObject { ["diagnostics"] = Diags(diags) };
        StudioCompiled compiled;
        try { compiled = k.Compile(ctx, draft); }
        catch (Exception e) when (e is InvalidDataException or FormatException or InvalidOperationException or ArgumentException)
        {
            result["error"] = e.Message;
            return Json(200, result);
        }
        var main = compiled.Added.GetValueOrDefault(k.PatchFile)?.FirstOrDefault();
        var template = k.TemplateElement(ctx, draft);
        var templateXml = template is null ? "" : (kind == "spell" ? PrepareSpell(template) : template).ToString();
        result["xml"] = new JsonObject([.. compiled.Added.Select(a => KeyValuePair.Create(a.Key, (JsonNode?)Cut(string.Join("\n", a.Value.Select(e => e.ToString())))))]);
        result["diff"] = main is null ? new JsonArray() : LineDiff(templateXml, main.ToString());
        result["loc"] = new JsonArray([.. compiled.Loc.Select(l => (JsonNode)new JsonObject { ["tuid"] = l.Tuid, ["en"] = l.En, ["ru"] = l.Ru })]);
        result["stations"] = new JsonObject([.. compiled.Stations.Select(s => KeyValuePair.Create(s.Key, (JsonNode?)J.Strings(s.Value.Select(v => v.Station))))]);
        result["drops"] = new JsonArray([.. compiled.Drops.SelectMany(d => d.Value).Select(d => (JsonNode)new JsonObject { ["text"] = DropText(d) })]);
        result["tooltip"] = k.Tooltip(ctx, draft, compiled);
        return Json(200, result);
    }

    /// <summary>A line diff (longest common subsequence): only changed lines and two around them, as [{op: " "|"+"|"-"|"…", line}].</summary>
    internal static JsonArray LineDiff(string a, string b)
    {
        var x = a.Split('\n').Select(l => l.TrimEnd('\r')).ToArray();
        var y = b.Split('\n').Select(l => l.TrimEnd('\r')).ToArray();
        var result = new JsonArray();
        if ((long)x.Length * y.Length > 4_000_000) { result.Add(new JsonObject { ["op"] = "…", ["line"] = "too large to compare" }); return result; }
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
        var gap = false;
        for (var i = 0; i < ops.Count; i++)
        {
            if (!keep[i]) { if (!gap) result.Add(new JsonObject { ["op"] = "…", ["line"] = "" }); gap = true; continue; }
            gap = false;
            result.Add(new JsonObject { ["op"] = ops[i].Op.ToString(), ["line"] = ops[i].Line });
        }
        return result;
    }

    // ---- jobs (validate, install, restore)

    /// <summary>Why the modkit cannot start now (a run of the studio or of the Mods pages is going), or null.</summary>
    private string? StudioBusy()
    {
        lock (_jobLock) if (_job is { Done: false } old) return $"the modkit is running ({old.What} from the Mods pages): wait for it";
        lock (_studioLock) if (_studioJobs.LastOrDefault() is { Done: false } j) return $"the modkit is running ({j.What}): wait for it";
        return null;
    }

    private WebResponse StartStudioJob(WebRequest r, AdminSession s, string verb)
    {
        if (_modPaths is not { } paths) return Json(409, Error("no official GameData found next to Paths:ClientGameData: validate, install and restore need it"));
        if (_modsProblem is not null) return Json(403, Error(_modsProblem));
        var b = Body(r);
        if (verb != "validate" && b["confirm"]?.GetValue<bool>() != true) return Json(400, Error("confirm: true — the server and the game client must be restarted afterwards"));
        if (verb != "validate" && !Directory.Exists(paths.Local)) return Json(409, Error($"game-local GameData {paths.Local} not found"));
        var packs = verb switch
        {
            "validate" => new List<string> { J.Str(b["pack"]) },
            "install" => J.StringList(b["packs"]),
            _ => new List<string>(),
        };
        foreach (var p in packs)
            if (PackFiles.Open(_modsDir, p) is null) return Json(400, Error($"no pack '{p}'"));
        if (verb == "install" && packs.Count == 0) return Json(400, Error("choose at least one pack to install"));
        if (StudioBusy() is { } busy) return Json(409, Error(busy));
        // Generate first (the drafts are the source of truth): the patch files the modkit reads.
        var pre = new List<StudioDiag>();
        if (packs.Count > 0)
        {
            if (Ready(out var wait) is not { } ready) return wait!;
            var (c, _, o) = ready;
            foreach (var name in packs)
            {
                var store = Store(name);
                if (!Directory.Exists(store.Root)) continue;
                var (drafts, _) = store.All();
                var (_, problems) = store.Generate(Context(c, o, name, drafts));
                pre.AddRange(problems);
            }
            if (verb == "install" && pre.Any(p => p.Severity == StudioDiag.Blocker))
                return Json(409, Error("the drafts have blockers: fix them first (nothing installed)", new JsonObject { ["diagnostics"] = Diags(pre) }));
        }
        StudioJob job;
        lock (_studioLock)
        {
            job = new StudioJob { Id = ++_studioJobSeq, What = verb, Packs = packs };
            job.Diagnostics.AddRange(pre);
            _studioJobs.Add(job);
            if (_studioJobs.Count > 10) _studioJobs.RemoveAt(0);
        }
        var who = $"admin '{s.Name}'";
        var from = r.Remote;
        Audit(who, from, $"studio: {verb} {string.Join(", ", packs)}".TrimEnd());
        _ = Task.Run(() => RunStudioJob(job, paths, who, from));
        return Json(200, job.ToJson());
    }

    private void RunStudioJob(StudioJob job, ModPaths paths, string who, System.Net.IPAddress from)
    {
        var output = new StringWriter();
        try
        {
            job.Stage = "loading the packs";
            job.Progress = 10;
            var packs = ModPack.LoadAll(_modsDir, job.Packs);
            switch (job.What)
            {
                case "validate":
                {
                    job.Stage = "building in memory (reading the official files)";
                    job.Progress = 30;
                    var result = new ModBuilder(paths.Official).Build(packs);
                    job.Progress = 90;
                    AddBuild(job, result);
                    job.Ok = result.Ok;
                    break;
                }
                case "install":
                {
                    job.Stage = "building and installing into game-local";
                    job.Progress = 30;
                    var built = new ModKit(paths, output).Build(packs, force: false);
                    job.Progress = 90;
                    if (built is not null) AddBuild(job, built);
                    else job.Diagnostics.Add(new StudioDiag(StudioDiag.Blocker, "REFUSED", "refused: see the output (an official client update since the first build needs the modkit's build --force on the console)"));
                    job.Ok = built is { Ok: true };
                    break;
                }
                default:
                    job.Stage = "restoring the official files";
                    job.Progress = 40;
                    new ModKit(paths, output).Restore();
                    job.Ok = true;
                    break;
            }
        }
        catch (Exception e)
        {
            job.Diagnostics.Add(new StudioDiag(StudioDiag.Blocker, "FAILED", $"{e.GetType().Name}: {e.Message}"));
            job.Ok = false;
        }
        finally
        {
            job.Output = output.ToString();
            job.Stage = job.Ok ? "done" : "failed";
            job.Progress = 100;
            job.Finished = DateTime.UtcNow;
            job.Done = true;
            Audit(who, from, $"studio: {job.What} {string.Join(", ", job.Packs)}".TrimEnd() + $" finished: {(job.Ok ? "ok" : "failed")}");
        }

        void AddBuild(StudioJob j, ModBuildResult result)
        {
            // The modkit's messages name the elements: attach each to the draft whose id it mentions (the longest first).
            var ids = new List<(string Pack, string Kind, string Id)>();
            foreach (var p in j.Packs)
                if (PackFiles.Open(_modsDir, p) is { } pf)
                    ids.AddRange(new StudioStore(pf).All().Drafts.Keys.Select(k => (p, k.Kind, k.Id)));
            ids.Sort((a, b) => b.Id.Length.CompareTo(a.Id.Length));
            StudioDiag Map(string severity, string text)
            {
                var hit = ids.FirstOrDefault(x => text.Contains(x.Id, StringComparison.OrdinalIgnoreCase));
                var (code, path) = StudioDiagText.Modkit(text);
                return new StudioDiag(severity, code, text, hit.Id is null ? "" : path, null, hit.Kind, hit.Id);
            }
            j.Diagnostics.AddRange(result.Errors.Select(e => Map(StudioDiag.Blocker, e)));
            j.Diagnostics.AddRange(result.Warnings.Select(e => Map(StudioDiag.Warning, e)));
            j.Notes.AddRange(result.Notes);
        }
    }
}
