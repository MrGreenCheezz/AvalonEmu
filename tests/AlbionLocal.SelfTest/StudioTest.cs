using System.Net;
using System.Text;
using System.Text.Json.Nodes;
using AlbionLocal.Server.Data;
using AlbionLocal.Server.Mods;
using AlbionLocal.Server.Persistence;
using AlbionLocal.Server.Web;
using static AlbionLocal.SelfTest.AlbionTests;

namespace AlbionLocal.SelfTest;

/// <summary>
/// The Studio (analysis/work/stageN/studio/SPEC.md) over real HTTP, on TEMP copies only (as <see cref="ModEditorTest"/>): a stand-in
/// official install, a stand-in game-local of hard links to it, a temp mods folder and a temp icon cache.
/// </summary>
public static class StudioTest
{
    public static IEnumerable<(string, Action)> All =>
    [
        ("studio: /studio is static ES modules under script-src 'self' (ETag, 304); the API needs the admin session, writes the CSRF header", Security),
        ("studio: catalogue search is paged server-side, rows carry icon URLs, filters by tier", Search),
        ("studio recipes: weapon + area spell + field, an item line T4-T8 (5 drafts) and the boss kit → Generate → the modkit's check passes", RecipesToCheck),
        ("studio recipes: the boss kit's server data (mobs.json, loot.json) loads; a variant kill rolls its list and its drop rule", RecipeServerData),
        ("studio: a copy of an official cape, bag, mount, off-hand, armour, tool and consumable passes the modkit's check (gendered models of meshes.toc); problems read in Russian with a fix", KindsToCheck),
        ("studio: Make a copy of T6_MAIN_SWORD and of a spell → save → preview (tooltip, diff) → Generate → the modkit's check passes", DraftToCheck),
    ];

    private static void Check(bool cond, string what) => AlbionTests.Check(cond, what);

    private static readonly string[] Files = ["items.bin", "spells.bin", "buildings.bin", "localization.bin", "mobs.bin", "loot.bin", "lootchests.bin"];

    private sealed record Setup(string Root, ModPaths Paths, string Mods, Server.ServerHost Host, WebPanel Panel, HttpClient Http, string Cookie, string Csrf);

    private static readonly Lazy<Setup> Started = new(() =>
    {
        var root = NewDir();
        var official = Directory.CreateDirectory(Path.Combine(root, "game", "Albion-Online_Data", "StreamingAssets", "GameData")).FullName;
        var local = Directory.CreateDirectory(Path.Combine(root, "game-local", "Albion-Online_Data", "StreamingAssets", "GameData")).FullName;
        var source = ClientGameDataDir();
        foreach (var f in Files)
        {
            File.Copy(Path.Combine(source, f), Path.Combine(official, f));
            SafeFiles.HardLink(Path.Combine(local, f), Path.Combine(official, f));
        }
        // The client's model list (capes, armour, heads and shoes sit in HUMAN_MALE/HUMAN_FEMALE folders there): the check uses it.
        var toc = Path.GetFullPath(Path.Combine(source, "..", "CustomMeshes", "meshes.toc"));
        if (File.Exists(toc))
        {
            File.Copy(toc, Path.Combine(Directory.CreateDirectory(Path.Combine(official, "..", "CustomMeshes")).FullName, "meshes.toc"));
            SafeFiles.HardLink(Path.Combine(Directory.CreateDirectory(Path.Combine(local, "..", "CustomMeshes")).FullName, "meshes.toc"), Path.Combine(official, "..", "CustomMeshes", "meshes.toc"));
        }
        var build = ModBuilder.ReadBuild(source) ?? "unknown";
        File.WriteAllText(Path.Combine(root, "game", "version.txt"), build);
        File.WriteAllText(Path.Combine(root, "game-local", "version.txt"), build);
        var paths = ModPaths.For(local) ?? throw new Exception("no temp official folder beside the temp game-local");
        var mods = Directory.CreateDirectory(Path.Combine(root, "mods")).FullName;
        var icons = Directory.CreateDirectory(Path.Combine(root, "icons", "ItemAtlas")).FullName;
        File.WriteAllBytes(Path.Combine(icons, "MAIN_SWORD.png"), Png.Encode(1, 1, [200, 50, 50, 255]));
        File.WriteAllText(Path.Combine(root, "icons", "index.json"), """{"version":1,"sprites":{"MAIN_SWORD":"ItemAtlas/MAIN_SWORD.png"}}""");
        var host = Host(Directory.CreateDirectory(Path.Combine(root, "saves")).FullName);
        var panel = new WebPanel(host.World, new WebOptions { Enabled = true, Port = 0 }, new DbBackup(host.Config.DatabasePath, Path.Combine(root, "backups"), new BackupOptions()), null, paths, mods, Path.Combine(root, "icons"));
        var http = new HttpClient(new HttpClientHandler { UseCookies = false, AllowAutoRedirect = false })
        {
            BaseAddress = new Uri($"http://127.0.0.1:{panel.EndPoint.Port}/"), Timeout = TimeSpan.FromSeconds(60),
        };
        host.Characters.CreateAccount("Studio", Passwords.Hash("studio password"), AccountLevel.Admin);
        var login = http.Send(new HttpRequestMessage(HttpMethod.Post, "login")
        {
            Content = new FormUrlEncodedContent([KeyValuePair.Create("name", "studio"), KeyValuePair.Create("password", "studio password"), KeyValuePair.Create("next", "/studio")]),
        });
        Check(login.StatusCode == HttpStatusCode.SeeOther && login.Headers.Location?.ToString() == "/studio", $"the login goes back to /studio ({login.Headers.Location})");
        var cookie = login.Headers.GetValues("Set-Cookie").Single().Split(';')[0];
        var session = Json(Send(http, HttpMethod.Get, "studio/api/session", cookie));
        return new Setup(root, paths, mods, host, panel, http, cookie, (string)session["csrf"]!);
    });

    private static HttpResponseMessage Send(HttpClient http, HttpMethod method, string path, string? cookie, string? csrf = null, JsonNode? body = null, (string, string)? header = null)
    {
        var request = new HttpRequestMessage(method, path);
        if (cookie is not null) request.Headers.Add("Cookie", cookie);
        if (csrf is not null) request.Headers.Add("X-CSRF-Token", csrf);
        if (header is { } h) request.Headers.TryAddWithoutValidation(h.Item1, h.Item2);
        if (body is not null) request.Content = new StringContent(body.ToJsonString(), Encoding.UTF8, "application/json");
        return http.Send(request);
    }

    private static JsonObject Json(HttpResponseMessage r) => JsonNode.Parse(r.Content.ReadAsStringAsync().Result) as JsonObject ?? throw new Exception("no JSON object");

    private static JsonObject Get(Setup s, string path) => Json(Send(s.Http, HttpMethod.Get, "studio/api/" + path, s.Cookie));

    private static JsonObject Post(Setup s, string path, JsonNode body, out HttpStatusCode status)
    {
        var r = Send(s.Http, HttpMethod.Post, "studio/api/" + path, s.Cookie, s.Csrf, body);
        status = r.StatusCode;
        return Json(r);
    }

    /// <summary>The catalogue loads in the background: polls the session until it is ready (at most 3 minutes).</summary>
    private static void WaitCatalog(Setup s)
    {
        var until = DateTime.UtcNow.AddMinutes(3);
        while ((bool?)Get(s, "session")["catalog"]?["ready"] != true)
        {
            Check(DateTime.UtcNow < until, "the catalogue loads within 3 minutes");
            Thread.Sleep(500);
        }
    }

    private static void Security()
    {
        var s = Started.Value;
        var anonymous = Send(s.Http, HttpMethod.Get, "studio", null);
        Check(anonymous.StatusCode == HttpStatusCode.SeeOther && anonymous.Headers.Location?.ToString() == "/login?next=/studio", "/studio without a session: to the login, back afterwards");
        Check(Send(s.Http, HttpMethod.Get, "studio/api/session", null).StatusCode == HttpStatusCode.Unauthorized, "the API without a session: 401");
        var page = Send(s.Http, HttpMethod.Get, "studio", s.Cookie);
        var html = page.Content.ReadAsStringAsync().Result;
        var csp = page.Headers.GetValues("Content-Security-Policy").Single();
        Check(page.StatusCode == HttpStatusCode.OK && page.Content.Headers.ContentType?.MediaType == "text/html" && html.Contains("<script type=\"module\" src=\"/studio/app.mjs\">")
              && !System.Text.RegularExpressions.Regex.IsMatch(html, "<script(?![^>]*\\bsrc=)"), "/studio: the page, its only script a module file");
        Check(csp.Contains("script-src 'self'") && !csp.Contains("unsafe-eval") && !csp.Contains("script-src 'self' 'unsafe-inline'") && csp.Contains("frame-ancestors 'none'"),
              $"/studio's CSP allows only its own scripts ({csp})");
        var app = Send(s.Http, HttpMethod.Get, "studio/app.mjs", s.Cookie);
        var etag = app.Headers.ETag?.Tag;
        Check(app.StatusCode == HttpStatusCode.OK && app.Content.Headers.ContentType?.MediaType == "text/javascript" && etag is not null
              && app.Content.ReadAsStringAsync().Result.Contains("import "), "app.mjs: text/javascript with an ETag");
        Check(Send(s.Http, HttpMethod.Get, "studio/app.mjs", s.Cookie, header: ("If-None-Match", etag!)).StatusCode == HttpStatusCode.NotModified, "the same ETag: 304");
        Check(Send(s.Http, HttpMethod.Get, "studio/nope.mjs", s.Cookie).StatusCode == HttpStatusCode.NotFound, "an unknown file: 404");
        var admin = Send(s.Http, HttpMethod.Get, "admin/mods", s.Cookie);
        Check(admin.Headers.GetValues("Content-Security-Policy").Single() == MiniHttp.DefaultCsp && admin.Content.ReadAsStringAsync().Result.Contains("href=\"/studio\""),
              "/admin keeps the strict CSP (no scripts) and links the studio");
        // Writes: the CSRF header is needed.
        var noToken = Send(s.Http, HttpMethod.Post, "studio/api/packs/create", s.Cookie, null, new JsonObject { ["name"] = "no-token" });
        Check(noToken.StatusCode == HttpStatusCode.Forbidden && !Directory.Exists(Path.Combine(s.Mods, "no-token")), "create without X-CSRF-Token: 403, nothing made");
        var wrong = Send(s.Http, HttpMethod.Post, "studio/api/packs/create", s.Cookie, "wrong", new JsonObject { ["name"] = "no-token" });
        Check(wrong.StatusCode == HttpStatusCode.Forbidden, "a wrong token: 403");
        var foreign = new HttpRequestMessage(HttpMethod.Post, "studio/api/packs/create") { Content = new StringContent("{\"name\":\"foreign\"}", Encoding.UTF8, "application/json") };
        foreign.Headers.Add("Cookie", s.Cookie);
        foreign.Headers.Add("X-CSRF-Token", s.Csrf);
        foreign.Headers.Add("Origin", "http://evil.example");
        Check(s.Http.Send(foreign).StatusCode == HttpStatusCode.Forbidden && !Directory.Exists(Path.Combine(s.Mods, "foreign")), "a cross-site Origin: 403");
        Post(s, "packs/create", new JsonObject { ["name"] = "../evil" }, out var evil);
        Check(evil == HttpStatusCode.BadRequest && !Directory.Exists(Path.Combine(s.Root, "evil")), "a pack name with ../: refused");
        var made = Post(s, "packs/create", new JsonObject { ["name"] = "studio-pack", ["version"] = "1.0.0", ["description"] = "studio test" }, out var ok);
        Check(ok == HttpStatusCode.OK && (bool?)made["ok"] == true && File.Exists(Path.Combine(s.Mods, "studio-pack", "pack.json")), "with the token: the pack is made");
    }

    private static void Search()
    {
        var s = Started.Value;
        WaitCatalog(s);
        var found = Get(s, "search/item?q=broadsword&f.tier=6&limit=5");
        var first = found["rows"]!.AsArray().FirstOrDefault();
        Check((int)found["total"]! >= 1 && (string?)first?["name"] == "T6_MAIN_SWORD" && (string?)first?["icon"] == "/studio/icons/MAIN_SWORD.png"
              && (string?)first?["facets"]?["tier"] == "6", $"'broadsword' at tier 6: T6_MAIN_SWORD first, with its icon URL ({first?.ToJsonString()})");
        var page2 = Get(s, "search/item?q=t6&limit=10&offset=10");
        Check(page2["rows"]!.AsArray().Count == 10 && (int)page2["offset"]! == 10 && (int)page2["total"]! > 100, "paging: 10 rows from offset 10 of many");
        var spells = Get(s, "search/spell?q=HAMSTRING&f.offered=1&limit=10");
        Check(spells["rows"]!.AsArray().Any(r => (string?)r!["name"] == "HAMSTRINGSWORD"), "spells: the offered HAMSTRINGSWORD is found");
        Check(Get(s, "search/station?q=forge&limit=50")["rows"]!.AsArray().Any(r => (string?)r!["name"] == "T6_FORGE")
              && (int)Get(s, "search/mob?limit=1")["total"]! > 100, "stations and mobs are searchable");
        var icon = Send(s.Http, HttpMethod.Get, "studio/icons/MAIN_SWORD.png", s.Cookie);
        Check(icon.StatusCode == HttpStatusCode.OK && icon.Content.Headers.ContentType?.MediaType == "image/png" && (icon.Headers.CacheControl?.MaxAge ?? TimeSpan.Zero) > TimeSpan.Zero,
              "/studio/icons/MAIN_SWORD.png: the PNG, cacheable");
        var lookup = Get(s, "lookup?refs=item:T6_METALBAR,spell:NOPE_SPELL");
        Check((bool?)lookup["item:T6_METALBAR"]?["found"] == true && (bool?)lookup["spell:NOPE_SPELL"]?["found"] == false, "lookup resolves references in a batch");
    }

    private static JsonObject WaitJob(Setup s)
    {
        JsonObject job;
        var until = DateTime.UtcNow.AddMinutes(3);
        do
        {
            Thread.Sleep(500);
            job = (JsonObject)Get(s, "jobs")["jobs"]!.AsArray()[0]!;
        } while ((bool?)job["done"] != true && DateTime.UtcNow < until);
        return job;
    }

    private static JsonObject Recipe(Setup s, string recipe, JsonObject values, out HttpStatusCode status) =>
        Post(s, "packs/recipe-pack/recipe/create", new JsonObject { ["recipe"] = recipe, ["values"] = values }, out status);

    private static void RecipesToCheck()
    {
        var s = Started.Value;
        WaitCatalog(s);
        Post(s, "packs/create", new JsonObject { ["name"] = "recipe-pack" }, out _);
        var list = Get(s, "recipes")["recipes"]!.AsArray();
        Check(list.Count(r => (string?)r!["kind"] == "spell") >= 14 && list.Count(r => (string?)r!["kind"] == "item") >= 10 && list.Count(r => (string?)r!["kind"] == "kit") >= 4
              && list.All(r => r!["fields"]!.AsArray().Count > 0), $"recipes: 14 abilities, 10 items, a line and 4 kits, each a field list ({list.Count})");
        // A required field left empty: a blocker at the field, nothing made.
        var empty = Recipe(s, "spell.area", new JsonObject { ["base"] = "" }, out var st);
        Check(st == HttpStatusCode.BadRequest && empty["diagnostics"]!.AsArray().Any(d => (string?)d!["path"] == "/values/base" && (string?)d["severity"] == "blocker"),
              "an empty required field: refused, the blocker at /values/base");
        var built = Post(s, "recipes/build", new JsonObject { ["pack"] = "recipe-pack", ["recipe"] = "spell.area", ["values"] = new JsonObject { ["base"] = "RECIPE_BLAST", ["damage"] = 321 } }, out _);
        Check(((string?)built["drafts"]?[0]?["description"]?["en"] ?? "").Contains("321") && ((string?)built["drafts"]?[0]?["description"]?["ru"] ?? "").Contains("321")
              && built["ranges"]?["damage"]?["median"] is not null && built["tooltip"] is not null,
              $"build (not saved): the RU/EN description from the numbers, the official range of the damage ({built["ranges"]?.ToJsonString()})");
        Recipe(s, "spell.area", new JsonObject { ["base"] = "RECIPE_BLAST", ["damage"] = 321, ["radius"] = 5 }, out st);
        Check(st == HttpStatusCode.OK, "spell.area made");
        Recipe(s, "spell.field", new JsonObject { ["base"] = "RECIPE_FIELD" }, out st);
        Check(st == HttpStatusCode.OK && Get(s, "packs/recipe-pack/entities")["entities"]!.AsArray().Any(e => (string?)e!["id"] == "RECIPE_FIELD_TICK"), "spell.field: the field and its tick spell");
        var weapon = Recipe(s, "item.weapon1h", new JsonObject { ["base"] = "MAIN_RECIPEBLADE", ["tier"] = 6, ["damage"] = 47, ["active"] = "RECIPE_BLAST", ["family"] = "MAIN_AXE" }, out st);
        var blade = Get(s, "packs/recipe-pack/entity?kind=item&id=T6_MAIN_RECIPEBLADE")["draft"];
        Check(st == HttpStatusCode.OK && (string?)blade?["template"] == "T6_MAIN_AXE" && (string?)blade?["attributes"]?["attackdamage"] == "47"
              && blade!["spells"]!.AsArray().Any(x => (string?)x!["spell"] == "RECIPE_BLAST") && (string?)blade["goal"]?["recipe"] == "item.weapon1h",
              $"item.weapon1h: the axe template, the damage, the new ability in slot 1, the goal kept ({blade?.ToJsonString()})");
        var line = Recipe(s, "line.items", new JsonObject { ["of"] = "item.armor", ["base"] = "ARMOR_RECIPELINE", ["slot"] = "HEAD", ["material"] = "LEATHER", ["ip"] = 1000, ["tier"] = 6 }, out st);
        var tiers = line["created"]?.AsArray().Select(c => (string)c!["id"]!).ToList() ?? [];
        var t4 = Get(s, "packs/recipe-pack/entity?kind=item&id=T4_ARMOR_RECIPELINE")["draft"];
        Check(st == HttpStatusCode.OK && tiers.Count == 5 && tiers.Contains("T8_ARMOR_RECIPELINE") && (string?)t4?["template"] == "T4_HEAD_LEATHER_SET1"
              && (string?)t4?["attributes"]?["itempower"] == "778", $"the line: 5 drafts T4-T8, IP scaled on the official curve (1000 × 700/900 = 778 at T4: {t4?["attributes"]?.ToJsonString()}; {line["error"]})");
        var kit = Recipe(s, "kit.boss", new JsonObject { ["prefix"] = "RCP" }, out st);
        var made = kit["created"]?.AsArray().Select(c => $"{c!["kind"]}/{c["id"]}").ToList() ?? [];
        Check(st == HttpStatusCode.OK && made.Contains("mob/RCP_BOSS") && made.Contains("lootlist/RCP_BOSS_LOOT") && made.Contains("spell/RCP_POWER") && made.Contains("item/T6_MAIN_RCP_BLADE"),
              $"the boss kit: mob variant, loot list, ability, weapon ({string.Join(", ", made)}; {kit["error"]})");
        // Every other recipe with its defaults: valid drafts too (one modkit check below covers them all).
        foreach (var r in list.Where(r => (string?)r!["id"] is not ("line.items" or "kit.boss")))
        {
            var res = Recipe(s, (string)r!["id"]!, new JsonObject(), out st);
            Check(st == HttpStatusCode.OK && !res["diagnostics"]!.AsArray().Any(d => (string?)d!["severity"] == "blocker"),
                  $"recipe {r["id"]} with its defaults: made without blockers ({res["error"]} {res["diagnostics"]?.AsArray().FirstOrDefault(d => (string?)d!["severity"] == "blocker")?.ToJsonString()})");
        }
        var gen = Post(s, "packs/recipe-pack/generate", new JsonObject(), out st);
        Check(st == HttpStatusCode.OK && (bool?)gen["ok"] == true && gen["written"]!.AsArray().Any(x => (string?)x == "server/mobs.json"),
              $"Generate: no blockers, the server data written ({gen["diagnostics"]?.AsArray().Where(d => (string?)d!["severity"] == "blocker").Select(d => d!.ToJsonString()).FirstOrDefault()})");
        Post(s, "actions/validate", new JsonObject { ["pack"] = "recipe-pack" }, out st);
        var job = WaitJob(s);
        Check(st == HttpStatusCode.OK && (bool?)job["ok"] == true, $"the modkit's check passes for the recipe pack ({job["diagnostics"]?.ToJsonString()[..Math.Min(600, job["diagnostics"]!.ToJsonString().Length)]})");
    }

    private static void RecipeServerData()
    {
        var s = Started.Value;
        var drops = ModDrops.Load(s.Mods, ["recipe-pack"], null);
        Check(drops.Variants.TryGetValue("RCP_BOSS", out var boss) && boss.HealthFactor == 5 && drops.Lists.ContainsKey("RCP_BOSS_LOOT"),
              "the server reads the kit's mob variant and loot list");
        drops.Random = new Random(7);
        var rolled = drops.ForMob(boss!.Template, "boss", 1000, [], "RCP_BOSS");
        Check(rolled.Any(r => r.Item.EndsWith("_METALBAR", StringComparison.Ordinal)) && rolled.Any(r => r.Item == "T6_MAIN_RCP_BLADE"),
              $"a variant kill: its loot list and the weapon's drop rule from it ({string.Join(", ", rolled)})");
    }

    private static bool Cyrillic(string? s) => s is not null && s.Any(ch => ch is >= '\u0400' and <= '\u04FF');

    private static void KindsToCheck()
    {
        var s = Started.Value;
        WaitCatalog(s);
        Post(s, "packs/create", new JsonObject { ["name"] = "kinds-pack" }, out _);
        // The user's report: a cape made by the recipe failed the check with mesh="T6_CAPE" (its model is CAPE/HUMAN_MALE/T6_CAPE_01_HM).
        var (code, path) = Server.Web.Studio.StudioDiagText.Modkit("items.bin T6_MY_CAPE_LOL: mesh=\"T6_CAPE\" is no model of the client (e.g. T6_MAIN_SWORD)");
        var mk = new Server.Web.Studio.StudioDiag("blocker", code, "items.bin T6_MY_CAPE_LOL: mesh=\"T6_CAPE\" is no model of the client (e.g. T6_MAIN_SWORD)", path, null, "item", "T6_MY_CAPE_LOL").ToJson();
        Check(code == "MK_MODEL" && path == "/mesh" && (string?)mk["text"]?["ru"] == "Модель «T6_CAPE» не найдена в клиенте."
              && ((string?)mk["advice"]?["ru"] ?? "").Contains("Выбрать модель") && (string?)mk["fix"]?["path"] == "/mesh" && (string?)mk["field"]?["ru"] == "модель",
              $"the modkit's model error reads in Russian, names the field, offers the template's model ({mk.ToJsonString()})");
        var note = Server.Web.Studio.StudioDiagText.Note("items.bin: new item ids T6_X 11950; the 54 journal _EMPTY/_FULL variants the client makes after the file move by +1 on client and server alike (saves keep item names, not ids)");
        Check(note.Ru.StartsWith("Новые ID предметов: T6_X 11950") && note.Ru.Contains("+1"), $"the notes read in Russian ({note.Ru})");
        string[] templates = ["T6_CAPE", "T6_BAG", "T5_MOUNT_ARMORED_HORSE", "T6_OFF_SHIELD", "T6_ARMOR_PLATE_SET1", "T6_HEAD_LEATHER_SET1", "T6_SHOES_CLOTH_SET1", "T6_2H_TOOL_PICK", "T6_POTION_HEAL", "T6_MEAL_STEW"];
        foreach (var template in templates)
        {
            var id = template[..3] + "KIND_" + template[3..];
            var made = Post(s, "packs/kinds-pack/entity/new", new JsonObject { ["kind"] = "item", ["from"] = template, ["id"] = id }, out var st);
            var saved = Post(s, "packs/kinds-pack/entity/save", new JsonObject { ["draft"] = made["draft"]?.DeepClone() }, out var st2);
            Check(st == HttpStatusCode.OK && st2 == HttpStatusCode.OK && !saved["diagnostics"]!.AsArray().Any(d => (string?)d!["severity"] == "blocker"),
                  $"a copy of {template}: saved without blockers ({saved["diagnostics"]?.ToJsonString()})");
        }
        // Another official cape's model by the picker's list; an unknown one is a blocker in Russian with the one-click fix.
        var models = Get(s, "models?slot=cape&q=MARTLOCK&limit=5");
        var martlock = (string?)models["rows"]?[0]?["name"];
        Check(martlock is not null && (string?)models["rows"]![0]!["slot"] == "cape" && models["rows"]![0]!["item"] is not null, $"the model picker lists cape models with an item that uses them ({models.ToJsonString()[..Math.Min(300, models.ToJsonString().Length)]})");
        var cape = (JsonObject)Get(s, "packs/kinds-pack/entity?kind=item&id=T6_KIND_CAPE")["draft"]!;
        cape["mesh"] = martlock;
        cape["uisprite"] = "CAPEITEM_FW_MARTLOCK";
        Post(s, "packs/kinds-pack/entity/save", new JsonObject { ["draft"] = cape.DeepClone() }, out _);
        var bad = (JsonObject)cape.DeepClone();
        bad["mesh"] = "T6_CAPE_NOPE";
        bad["uisprite"] = "NOPE_SPRITE";
        bad["name"] = new JsonObject { ["en"] = "", ["ru"] = "" };
        bad["recipe"] = new JsonObject { ["silver"] = "x", ["resources"] = new JsonArray(new JsonObject { ["item"] = "T6_NOPE_BAR", ["count"] = 0 }) };
        bad["spells"] = new JsonArray(new JsonObject { ["spell"] = "NOPE_SPELL", ["slot"] = 9 });
        bad["drops"] = new JsonArray(new JsonObject { ["mobs"] = new JsonArray(), ["chance"] = 2 });
        var diags = Post(s, "preview", new JsonObject { ["pack"] = "kinds-pack", ["draft"] = bad }, out _)["diagnostics"]!.AsArray();
        var model = diags.FirstOrDefault(d => (string?)d!["code"] == "MODEL_UNKNOWN");
        Check((string?)model?["path"] == "/mesh" && (string?)model?["text"]?["ru"] == "Модель «T6_CAPE_NOPE» не найдена в клиенте." && (string?)model?["fix"]?["path"] == "/mesh"
              && diags.Any(d => (string?)d!["code"] == "ICON_UNKNOWN" && (string?)d["fix"]?["path"] == "/uisprite"),
              $"an unknown model / icon: blockers at /mesh and /uisprite in Russian, with the template's as the fix ({model?.ToJsonString()})");
        var untranslated = diags.Where(d => !Cyrillic((string?)d!["text"]?["ru"]) || string.IsNullOrEmpty((string?)d["text"]?["en"])).Select(d => d!.ToJsonString()).ToList();
        Check(diags.Count >= 8 && untranslated.Count == 0, $"every problem has its Russian and English text ({diags.Count}; untranslated: {string.Join("; ", untranslated)})");
        var gen = Post(s, "packs/kinds-pack/generate", new JsonObject(), out var gst);
        var items = ModPack.Load(Path.Combine(s.Mods, "kinds-pack")).Patches.Single(p => p.Target == "items.bin").Root;
        var capeEl = items.Descendants().SingleOrDefault(e => (string?)e.Attribute("uniquename") == "T6_KIND_CAPE");
        Check(gst == HttpStatusCode.OK && (bool?)gen["ok"] == true && (string?)capeEl?.Attribute("mesh") == martlock && (string?)capeEl?.Attribute("uisprite") == "CAPEITEM_FW_MARTLOCK"
              && (string?)items.Descendants().SingleOrDefault(e => (string?)e.Attribute("uniquename") == "T6_KIND_BAG")?.Attribute("mesh") == "T6_BAG",
              $"Generate: the cape with the chosen model and icon, the bag with its template's ({capeEl})");
        Post(s, "actions/validate", new JsonObject { ["pack"] = "kinds-pack" }, out var vst);
        var job = WaitJob(s);
        Check(vst == HttpStatusCode.OK && (bool?)job["ok"] == true && job["notesText"]!.AsArray().Any(n => Cyrillic((string?)n!["ru"])),
              $"the modkit's check passes for every kind, its notes in Russian ({job["diagnostics"]?.ToJsonString()[..Math.Min(600, job["diagnostics"]!.ToJsonString().Length)]})");
    }

    private static void DraftToCheck()
    {
        var s = Started.Value;
        try
        {
            WaitCatalog(s);
            if (!Directory.Exists(Path.Combine(s.Mods, "studio-pack"))) Post(s, "packs/create", new JsonObject { ["name"] = "studio-pack" }, out _);
            var made = Post(s, "packs/studio-pack/entity/new", new JsonObject { ["kind"] = "item", ["from"] = "T6_MAIN_SWORD", ["id"] = "T6_MAIN_STUDIOBLADE" }, out var st);
            var draft = made["draft"] as JsonObject;
            Check(st == HttpStatusCode.OK && draft is not null && (string?)draft["template"] == "T6_MAIN_SWORD" && draft["recipe"]?["resources"]?.AsArray().Count > 0
                  && draft["spells"]!.AsArray().Any(x => (string?)x!["spell"] == "HEROICSTRIKE2") && draft["stations"]!.AsArray().Any(x => (string?)x == "T6_FORGE"),
                  "Make a copy: the draft carries the template's recipe, stations and spells (resolved from its reference list)");
            // A spell of its own: the hamstring with its sub-spells cloned and a new cooldown, offered in slot 2.
            var spell = Post(s, "packs/studio-pack/entity/new", new JsonObject { ["kind"] = "spell", ["from"] = "HAMSTRINGSWORD", ["id"] = "STUDIO_DASH" }, out st)["draft"] as JsonObject;
            Check(st == HttpStatusCode.OK && spell is not null, "Make a copy of a spell");
            spell!["attributes"] = new JsonObject { ["recastdelay"] = "9" };
            spell["subSpells"] = "clone";
            spell["name"] = new JsonObject { ["en"] = "Studio Dash", ["ru"] = "Студийный рывок" };
            Post(s, "packs/studio-pack/entity/save", new JsonObject { ["draft"] = spell.DeepClone() }, out st);
            Check(st == HttpStatusCode.OK, "the spell draft is saved");
            draft!["attributes"] = new JsonObject { ["attackdamage"] = "44" };
            draft["name"] = new JsonObject { ["en"] = "Studio Blade", ["ru"] = "Студийный клинок" };
            draft["spells"]!.AsArray().Add(new JsonObject { ["spell"] = "STUDIO_DASH", ["slot"] = 2 });
            draft["drops"] = new JsonArray(new JsonObject { ["mobs"] = new JsonArray("WORLD_BOSS_*"), ["chance"] = 0.02, ["amount"] = 1 });
            draft["extraSpells"] = new JsonArray("SPRINT"); // the extra action bars (server/extraspells.json)
            var saved = Post(s, "packs/studio-pack/entity/save", new JsonObject { ["draft"] = draft.DeepClone() }, out st);
            Check(st == HttpStatusCode.OK && !saved["diagnostics"]!.AsArray().Any(d => (string?)d!["severity"] == "blocker"),
                  $"saved without blockers ({saved["diagnostics"]?.ToJsonString()})");
            Check(File.Exists(Path.Combine(s.Mods, "studio-pack", "studio", "item", "T6_MAIN_STUDIOBLADE.json")), "the draft is a JSON file in mods/<pack>/studio/item/");
            // A bad reference shows as a blocker at its field.
            var bad = (JsonObject)draft.DeepClone();
            bad["recipe"]!["resources"]!.AsArray().Add(new JsonObject { ["item"] = "T6_NOPE_BAR", ["count"] = 3 });
            var badPreview = Post(s, "preview", new JsonObject { ["pack"] = "studio-pack", ["draft"] = bad.DeepClone() }, out _);
            Check(badPreview["diagnostics"]!.AsArray().Any(d => (string?)d!["severity"] == "blocker" && ((string?)d["path"] ?? "").StartsWith("/recipe/resources/2/item")),
                  "an unknown ingredient: a blocker at /recipe/resources/2/item");
            var preview = Post(s, "preview", new JsonObject { ["pack"] = "studio-pack", ["draft"] = draft.DeepClone() }, out _);
            var tt = preview["tooltip"];
            Check((string?)tt?["name"]?["en"] == "Studio Blade" && (string?)tt?["tier"] == "6" && (string?)tt?["icon"] == "/studio/icons/MAIN_SWORD.png"
                  && tt!["stats"]!.AsArray().Any(x => (string?)x!["key"] == "attackdamage" && (string?)x["value"] == "44" && (bool?)x["changed"] == true)
                  && tt["spells"]!.AsArray().Any(x => (string?)x!["spell"] == "STUDIO_DASH" && (bool?)x["own"] == true && ((string?)x["name"]?["en"]) == "Studio Dash"),
                  $"the tooltip: name, tier, icon, the changed damage, the pack's own spell ({tt?.ToJsonString()[..Math.Min(400, tt?.ToJsonString().Length ?? 0)]})");
            Check(preview["diff"]!.AsArray().Any(d => (string?)d!["op"] == "+" && ((string?)d["line"] ?? "").Contains("attackdamage=\"44\"")), "the diff against the template shows the new damage");
            var spellPreview = Post(s, "preview", new JsonObject { ["pack"] = "studio-pack", ["draft"] = spell.DeepClone() }, out _);
            Check(((string?)spellPreview["tooltip"]?["description"]?["en"] ?? "").Contains("physical damage") && spellPreview["tooltip"]!["subSpells"]!.AsArray().Count > 0,
                  "the spell's tooltip: the official description with its numbers filled, the cloned sub-spells listed");
            var gen = Post(s, "packs/studio-pack/generate", new JsonObject(), out st);
            Check(st == HttpStatusCode.OK && (bool?)gen["ok"] == true && gen["written"]!.AsArray().Any(x => (string?)x == "items.xml"), $"Generate writes the patch files ({gen.ToJsonString()[..Math.Min(300, gen.ToJsonString().Length)]})");
            var pack = ModPack.Load(Path.Combine(s.Mods, "studio-pack"));
            var blade = pack.Patches.Single(p => p.Target == "items.bin").Root.Descendants("weapon").SingleOrDefault();
            Check((string?)blade?.Attribute("uniquename") == "T6_MAIN_STUDIOBLADE" && (string?)blade?.Attribute("attackdamage") == "44" && blade?.Element("enchantments") is null
                  && File.ReadAllText(Path.Combine(s.Mods, "studio-pack", "localization.xml")).Contains("Студийный клинок")
                  && File.ReadAllText(Path.Combine(s.Mods, "studio-pack", "server", "drops.json")).Contains("WORLD_BOSS_*")
                  && File.ReadAllText(Path.Combine(s.Mods, "studio-pack", "server", "extraspells.json")) is var extra && extra.Contains("T6_MAIN_STUDIOBLADE") && extra.Contains("SPRINT"),
                  "items.xml, localization.xml, drops.json and extraspells.json hold the draft");
            Post(s, "actions/validate", new JsonObject { ["pack"] = "studio-pack" }, out st);
            Check(st == HttpStatusCode.OK, "Validate starts");
            JsonObject job;
            var until = DateTime.UtcNow.AddMinutes(3);
            do
            {
                Thread.Sleep(500);
                job = (JsonObject)Get(s, "jobs")["jobs"]!.AsArray()[0]!;
            } while ((bool?)job["done"] != true && DateTime.UtcNow < until);
            Check((bool?)job["ok"] == true && (int)job["progress"]! == 100, $"the modkit's check passes ({job["diagnostics"]?.ToJsonString()})");
            // Export and import as a new pack; rename with the reference updated; delete is refused while used.
            var zip = Send(s.Http, HttpMethod.Get, "studio/api/packs/studio-pack/export?format=zip", s.Cookie).Content.ReadAsByteArrayAsync().Result;
            var import = new HttpRequestMessage(HttpMethod.Post, "studio/api/packs/import?name=studio-copy") { Content = new ByteArrayContent(zip) };
            import.Headers.Add("Cookie", s.Cookie);
            import.Headers.Add("X-CSRF-Token", s.Csrf);
            Check(s.Http.Send(import).StatusCode == HttpStatusCode.OK && Get(s, "packs/studio-copy/entities")["entities"]!.AsArray().Count == 2, "export zip → import as a new pack: both drafts");
            Post(s, "packs/studio-pack/entity/delete", new JsonObject { ["kind"] = "spell", ["id"] = "STUDIO_DASH" }, out st);
            Check(st == HttpStatusCode.Conflict, "deleting a spell an item uses: 409 with the dependents");
            var renamed = Post(s, "packs/studio-pack/entity/rename", new JsonObject { ["kind"] = "spell", ["id"] = "STUDIO_DASH", ["to"] = "STUDIO_LEAP" }, out st);
            var after = Get(s, "packs/studio-pack/entity?kind=item&id=T6_MAIN_STUDIOBLADE")["draft"];
            Check(st == HttpStatusCode.OK && (int)renamed["references"]! == 1 && after!["spells"]!.AsArray().Any(x => (string?)x!["spell"] == "STUDIO_LEAP")
                  && Get(s, "packs/studio-pack/history?kind=item&id=T6_MAIN_STUDIOBLADE")["versions"]!.AsArray().Count >= 1,
                  "rename: the item's reference follows; the history keeps the earlier versions");
        }
        finally
        {
            s.Http.Dispose();
            s.Panel.Dispose();
            s.Host.Dispose();
            try { Directory.Delete(s.Root, true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
        }
    }
}
