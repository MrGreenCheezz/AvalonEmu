using System.Net;
using System.Text.RegularExpressions;
using AlbionLocal.Server.Data;
using AlbionLocal.Server.Mods;
using AlbionLocal.Server.Persistence;
using AlbionLocal.Server.Web;
using static AlbionLocal.SelfTest.AlbionTests;

namespace AlbionLocal.SelfTest;

/// <summary>
/// The web panel's mod editor (analysis/work/stageN/modeditor/SPEC.md) over real HTTP, on TEMP copies only: a stand-in official
/// install (copies of the files the editor and the modkit read) and a stand-in game-local of hard links to it, a temp mods folder.
/// The real game-local, the official install and server/mods are never touched.
/// </summary>
public static class ModEditorTest
{
    public static IEnumerable<(string, Action)> All =>
    [
        ("mod editor: clone an item over HTTP into a new pack, the files are written, Validate passes, Install builds into the temp game-local", CloneInstall),
        ("mod editor icons: DXT1/DXT5 blocks and an atlas crop decode as expected, PNG round-trips; /admin/icons serves a cached PNG, else a blank", Icons),
        ("mod editor: Validate shows a bad ingredient inline; no CSRF token or a bad pack name is refused; Restore links back", BadReference),
    ];

    private static void Check(bool cond, string what) => AlbionTests.Check(cond, what);

    private static readonly string[] Files = ["items.bin", "spells.bin", "buildings.bin", "localization.bin", "mobs.bin", "loot.bin", "lootchests.bin"];

    private sealed record Setup(string Root, ModPaths Paths, string Mods, Server.ServerHost Host, WebPanel Panel, HttpClient Http, string Cookie, string Csrf, Dictionary<string, string> OfficialBefore);

    private static readonly Lazy<Setup> Started = new(() =>
    {
        var root = NewDir();
        var official = Directory.CreateDirectory(Path.Combine(root, "game", "Albion-Online_Data", "StreamingAssets", "GameData")).FullName;
        var local = Directory.CreateDirectory(Path.Combine(root, "game-local", "Albion-Online_Data", "StreamingAssets", "GameData")).FullName;
        var source = ClientGameDataDir(); // the pristine data (only read)
        foreach (var f in Files)
        {
            File.Copy(Path.Combine(source, f), Path.Combine(official, f));
            SafeFiles.HardLink(Path.Combine(local, f), Path.Combine(official, f));
        }
        var build = ModBuilder.ReadBuild(source) ?? "unknown";
        File.WriteAllText(Path.Combine(root, "game", "version.txt"), build);
        File.WriteAllText(Path.Combine(root, "game-local", "version.txt"), build);
        var paths = ModPaths.For(local) ?? throw new Exception("no temp official folder beside the temp game-local");
        var mods = Directory.CreateDirectory(Path.Combine(root, "mods")).FullName;
        var host = Host(Directory.CreateDirectory(Path.Combine(root, "saves")).FullName);
        var panel = new WebPanel(host.World, new WebOptions { Enabled = true, Port = 0 }, new DbBackup(host.Config.DatabasePath, Path.Combine(root, "backups"), new BackupOptions()), null, paths, mods, Path.Combine(root, "icons"));
        var http = new HttpClient(new HttpClientHandler { UseCookies = false, AllowAutoRedirect = false })
        {
            BaseAddress = new Uri($"http://127.0.0.1:{panel.EndPoint.Port}/"), Timeout = TimeSpan.FromSeconds(30),
        };
        host.Characters.CreateAccount("Modder", Passwords.Hash("modder password"), AccountLevel.Admin);
        var login = Post(http, "login", null, ("name", "modder"), ("password", "modder password"));
        var cookie = login.Headers.GetValues("Set-Cookie").Single().Split(';')[0];
        var csrf = Regex.Match(Get(http, "admin/mods", cookie), "name=csrf value=\"([^\"]+)\"").Groups[1].Value;
        var before = Files.ToDictionary(f => f, f => SafeFiles.Sha256(Path.Combine(official, f)));
        return new Setup(root, paths, mods, host, panel, http, cookie, csrf, before);
    });

    private static HttpResponseMessage Post(HttpClient http, string path, string? cookie, params (string, string)[] form)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, path) { Content = new FormUrlEncodedContent(form.Select(f => KeyValuePair.Create(f.Item1, f.Item2))) };
        if (cookie is not null) request.Headers.Add("Cookie", cookie);
        return http.Send(request);
    }

    private static string Get(HttpClient http, string path, string cookie)
    {
        var request = new HttpRequestMessage(HttpMethod.Get, path);
        request.Headers.Add("Cookie", cookie);
        return http.Send(request).Content.ReadAsStringAsync().Result;
    }

    /// <summary>GETs <paramref name="path"/> until the page no longer says it is loading or running (at most 3 minutes).</summary>
    private static string Wait(Setup s, string path)
    {
        var until = DateTime.UtcNow.AddMinutes(3);
        while (true)
        {
            var page = Get(s.Http, path, s.Cookie);
            if (!page.Contains("http-equiv=refresh") || DateTime.UtcNow > until) return page;
            Thread.Sleep(500);
        }
    }

    private static HttpResponseMessage Save(Setup s, string pack, string name, string ingredient) => Post(s.Http, "admin/mods/item/save", s.Cookie,
        ("csrf", s.Csrf), ("mode", "clone"), ("template", "T6_MAIN_SWORD"), ("pack", pack), ("uniquename", name),
        ("name_en", "Test Blade"), ("name_ru", "Тестовый клинок"), ("desc_en", "A test."), ("desc_ru", "Тест."), ("a_attackdamage", "44"),
        ("r_present", "1"), ("ing0_name", ingredient), ("ing0_count", "20"), ("stations", "T6_FORGE"),
        ("s_present", "1"), ("sp0_name", "HEROICSTRIKE2"), ("sp0_slots", "1"), ("sp1_name", "PASSIVE_BLEEDCHANCE"),
        ("d_present", "1"), ("dr0_mobs", "WORLD_BOSS_*"), ("dr0_chance", "0.02"));

    private static void CloneInstall()
    {
        var s = Started.Value;
        Check(s.Csrf.Length > 20, "the Mods page carries the session's CSRF token");
        Check(Post(s.Http, "admin/mods/create", s.Cookie, ("csrf", s.Csrf), ("name", "test-pack"), ("version", "1.0.0"), ("description", "editor test")).StatusCode == HttpStatusCode.SeeOther
              && File.Exists(Path.Combine(s.Mods, "test-pack", "pack.json")), "Create pack: mods/test-pack/pack.json");
        var items = Wait(s, "admin/mods/items?q=T6_MAIN_SWORD&tier=6");
        Check(items.Contains("item?name=T6_MAIN_SWORD\"") && items.Contains("Broadsword"), "the catalogue finds the Broadsword (T6_MAIN_SWORD) with its English name");
        var form = Get(s.Http, "admin/mods/item/edit?pack=test-pack&template=T6_MAIN_SWORD", s.Cookie);
        Check(form.Contains("name=\"a_attackdamage\"") && form.Contains("T6_FORGE") && form.Contains("HEROICSTRIKE2"), "the clone form shows the template's attributes, stations and spells");
        var saved = Save(s, "test-pack", "T6_MAIN_TESTBLADE", "T6_METALBAR");
        Check(saved.StatusCode == HttpStatusCode.SeeOther, $"Save: a redirect ({saved.StatusCode})");
        var dir = Path.Combine(s.Mods, "test-pack");
        var pack = ModPack.Load(dir);
        var blade = pack.Patches.Single(p => p.Target == "items.bin").Root.Descendants("weapon").SingleOrDefault();
        Check(blade is not null && (string?)blade.Attribute("uniquename") == "T6_MAIN_TESTBLADE" && (string?)blade.Attribute("attackdamage") == "44"
              && (string?)blade.Attribute("mesh") == "T6_MAIN_SWORD" && (string?)blade.Attribute("uisprite") == "MAIN_SWORD" && blade.Element("enchantments") is null,
              "items.xml: the clone with the new damage, the template's model and icon, no enchantments");
        Check(File.ReadAllText(Path.Combine(dir, "localization.xml")).Contains("Тестовый клинок") && File.ReadAllText(Path.Combine(dir, "buildings.xml")).Contains("T6_FORGE")
              && File.ReadAllText(Path.Combine(dir, "server", "drops.json")).Contains("WORLD_BOSS_*") && File.Exists(Path.Combine(dir, "editor.json")),
              "localization.xml, buildings.xml, server/drops.json and editor.json written");
        // The ability builder: the hamstring sword with its sub-spells cloned, a new cooldown, plus the frost path's slowing field.
        var ability = Post(s.Http, "admin/mods/spell/save", s.Cookie, ("csrf", s.Csrf), ("mode", "clone"), ("template", "HAMSTRINGSWORD"), ("pack", "test-pack"),
              ("uniquename", "MODTEST_DASH"), ("deep", "1"), ("name_en", "Test Dash"), ("name_ru", "Тестовый рывок"), ("v0_recastdelay", "9"));
        Check(ability.StatusCode == HttpStatusCode.SeeOther,
              $"the ability is saved ({ability.StatusCode} {Regex.Match(ability.Content.ReadAsStringAsync().Result, "class=\"note err\">([^<]*)").Groups[1].Value})");
        var blocks = Get(s.Http, "admin/mods/spell/edit?pack=test-pack&name=MODTEST_DASH&from=ICEROCK_EXPLODE_FROST_PATH", s.Cookie);
        Check(blocks.Contains("Add to this spell") && blocks.Contains("spelleffectarea"), "the block picker lists the frost path's blocks");
        Post(s.Http, "admin/mods/spell/block", s.Cookie, ("csrf", s.Csrf), ("pack", "test-pack"), ("name", "MODTEST_DASH"), ("op", "add"), ("from", "ICEROCK_EXPLODE_FROST_PATH"), ("index", "0"));
        var dash = ModPack.Load(dir).Patches.Single(p => p.Target == "spells.bin").Root.Descendants("activespell").ToList();
        var main = dash.FirstOrDefault(e => (string?)e.Attribute("uniquename") == "MODTEST_DASH");
        Check(main is not null && (string?)main.Attribute("recastdelay") == "9" && main.Element("spelleffectarea") is not null && dash.Count > 1
              && main.Descendants().All(e => e.Attributes().All(a => !a.Value.StartsWith("HAMSTRINGSWORD", StringComparison.Ordinal))),
              $"spells.xml: MODTEST_DASH (cooldown 9, the slowing field) and its {dash.Count - 1} cloned sub-spells, the references rewired");
        Check(Post(s.Http, "admin/mods/check", s.Cookie, ("csrf", s.Csrf), ("pack", "test-pack")).StatusCode == HttpStatusCode.SeeOther, "Validate starts");
        var check = Wait(s, "admin/mods/job");
        Check(check.Contains("No errors: the pack builds."), "Validate: no errors" + (check.Contains("<ul class=errs>") ? " — " + Regex.Match(check, "<ul class=errs>(.*?)</ul>").Groups[1].Value : ""));
        Check(Get(s.Http, "admin/mods/preview?pack=test-pack", s.Cookie).Contains("attackdamage=&quot;44&quot;"), "the preview shows the changed damage against the template");
        Check(Post(s.Http, "admin/mods/install", s.Cookie, ("csrf", s.Csrf), ("pack", "test-pack"), ("pk_test-pack", "1")).StatusCode == HttpStatusCode.SeeOther
              && !Get(s.Http, "admin/mods/job", s.Cookie).Contains("Install"), "Install without the restart confirmation: refused, no job");
        Post(s.Http, "admin/mods/install", s.Cookie, ("csrf", s.Csrf), ("pack", "test-pack"), ("pk_test-pack", "1"), ("confirm", "1"));
        var install = Wait(s, "admin/mods/job");
        Check(install.Contains("Installed into game-local.") && install.Contains("Restart the server and the game client"), "Install: built, with the restart warning");
        var state = ModState.Load(s.Paths.Local);
        Check(state is { Installed.Count: > 0 } && state.Items!.New.ContainsKey("T6_MAIN_TESTBLADE")
              && GameDataFile.Open(s.Paths.Local, "items.bin").Elements().Any(e => (string?)e.Attribute("uniquename") == "T6_MAIN_TESTBLADE"),
              "the temp game-local items.bin holds the new item; the modkit state names its id");
        var changed = Files.Where(f => SafeFiles.Sha256(Path.Combine(s.Paths.Official, f)) != s.OfficialBefore[f]).ToList();
        Check(changed.Count == 0 && SafeFiles.LinkCount(Path.Combine(s.Paths.Local, "items.bin")) == 1, $"the temp 'official' files are unchanged ({string.Join(", ", changed)}), the modded one unlinked");
    }

    private static void BadReference()
    {
        var s = Started.Value;
        try
        {
            Check(Post(s.Http, "admin/mods/create", s.Cookie, ("name", "no-token"), ("version", "1.0.0")).StatusCode == HttpStatusCode.Forbidden
                  && !Directory.Exists(Path.Combine(s.Mods, "no-token")), "Create without the CSRF token: 403, nothing made");
            Post(s.Http, "admin/mods/create", s.Cookie, ("csrf", s.Csrf), ("name", "../evil"), ("version", "1.0.0"));
            Check(!Directory.Exists(Path.Combine(s.Root, "evil")) && Directory.GetDirectories(s.Mods).Length == 1, "a pack name with ../ is refused");
            Post(s.Http, "admin/mods/create", s.Cookie, ("csrf", s.Csrf), ("name", "bad-pack"), ("version", "1.0.0"));
            Check(Save(s, "bad-pack", "T6_MAIN_BADBLADE", "T6_NOPE_BAR").StatusCode == HttpStatusCode.SeeOther, "an unknown ingredient is saved (the check judges references)");
            Post(s.Http, "admin/mods/check", s.Cookie, ("csrf", s.Csrf), ("pack", "bad-pack"));
            var check = Wait(s, "admin/mods/job");
            Check(check.Contains("<ul class=errs>") && check.Contains("T6_NOPE_BAR") && check.Contains("names no item"), "Validate lists the bad ingredient as an error");
            Post(s.Http, "admin/mods/restore", s.Cookie, ("csrf", s.Csrf), ("confirm", "1"));
            Check(Wait(s, "admin/mods/job").Contains("Restored") && SafeFiles.SameFile(Path.Combine(s.Paths.Local, "items.bin"), Path.Combine(s.Paths.Official, "items.bin")),
                  "Restore: the temp game-local items.bin is a link to the 'official' one again");
        }
        finally
        {
            s.Http.Dispose();
            s.Panel.Dispose();
            s.Host.Dispose();
            try { Directory.Delete(s.Root, true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
        }
    }

    private static void Icons()
    {
        // DXT1: red/blue end points (4-colour mode), pixels 0..3 take indices 0, 1, 2, 3.
        var px = new byte[64];
        TextureDecode.DecodeDxt1([0x00, 0xF8, 0x1F, 0x00, 0xE4, 0, 0, 0], px);
        Check(px.AsSpan(0, 16).SequenceEqual(new byte[] { 255, 0, 0, 255, 0, 0, 255, 255, 170, 0, 85, 255, 85, 0, 170, 255 }) && px[4 * 15] == 255,
              "DXT1 block: red, blue and the two thirds between");
        // DXT5: alpha 255/0 (8-value mode), pixel 0 → 255, 1 → 0, 2 → 6/7 of 255.
        TextureDecode.DecodeDxt5([255, 0, 136, 0, 0, 0, 0, 0, 0x00, 0xF8, 0x1F, 0x00, 0, 0, 0, 0], px);
        Check(px[3] == 255 && px[7] == 0 && px[11] == 218 && px[15] == 255 && px[0] == 255, "DXT5 block: the interpolated alpha");
        // RGBA32 atlas 4×4, Unity rows bottom-up: the sprite at the top-left (x 1, y 0, 2×1) is in data row 3; padding 1 left.
        var atlas = new byte[64];
        for (var i = 0; i < 16; i++) { atlas[i * 4] = (byte)i; atlas[i * 4 + 3] = 255; }
        var crop = TextureDecode.Crop(atlas, 4, 4, 1, 0, 2, 1, padL: 1);
        Check(crop.Length == 12 && crop[3] == 0 && crop[4] == 13 && crop[8] == 14, "atlas crop: top-left origin, Unity rows flipped, padding transparent");
        var img = new byte[7 * 5 * 4];
        for (var i = 0; i < img.Length; i++) img[i] = (byte)(i * 37 % 251);
        var (w, h, back) = Png.Decode(Png.Encode(7, 5, img));
        Check(w == 7 && h == 5 && back.AsSpan().SequenceEqual(img), "PNG encode → decode gives the same pixels");

        var s = Started.Value;
        var icons = Path.Combine(s.Root, "icons");
        Check(Get(s.Http, "admin/mods/item?name=T6_MAIN_SWORD", s.Cookie) is var before && before.Contains("-- icons</code>") && !before.Contains("<img"),
              "no cache: the item page shows the hint and no image");
        var png = Png.Encode(2, 1, crop.AsSpan(4).ToArray());
        Directory.CreateDirectory(Path.Combine(icons, "ItemAtlas"));
        File.WriteAllBytes(Path.Combine(icons, "ItemAtlas", "MAIN_SWORD.png"), png);
        File.WriteAllText(Path.Combine(icons, "index.json"), """{"version":1,"sprites":{"MAIN_SWORD":"ItemAtlas/MAIN_SWORD.png","EVIL":"../index.json"}}""");
        var request = new HttpRequestMessage(HttpMethod.Get, "admin/icons/MAIN_SWORD.png");
        request.Headers.Add("Cookie", s.Cookie);
        var ok = s.Http.Send(request);
        Check(ok.StatusCode == HttpStatusCode.OK && ok.Content.Headers.ContentType?.MediaType == "image/png" && ok.Content.ReadAsByteArrayAsync().Result.SequenceEqual(png)
              && ok.Headers.GetValues("Content-Security-Policy").Single().Contains("img-src 'self'"), "/admin/icons/MAIN_SWORD.png: 200, image/png, the cached file");
        foreach (var bad in new[] { "admin/icons/NOPE.png", "admin/icons/EVIL.png" })
        {
            request = new HttpRequestMessage(HttpMethod.Get, bad);
            request.Headers.Add("Cookie", s.Cookie);
            var miss = s.Http.Send(request);
            Check(miss.StatusCode == HttpStatusCode.NotFound && Png.Decode(miss.Content.ReadAsByteArrayAsync().Result).Width == 1, $"{bad}: 404 with a blank 1×1 PNG");
        }
        Check(s.Http.Send(new HttpRequestMessage(HttpMethod.Get, "admin/icons/MAIN_SWORD.png")).StatusCode == HttpStatusCode.SeeOther, "icons need the admin session");
        var page = Get(s.Http, "admin/mods/item?name=T6_MAIN_SWORD", s.Cookie);
        var list = Get(s.Http, "admin/mods/items?q=T6_MAIN_SWORD", s.Cookie);
        var clone = Get(s.Http, "admin/mods/item/edit?pack=test-pack&template=T6_MAIN_SWORD", s.Cookie);
        Check(page.Contains("src=\"/admin/icons/MAIN_SWORD.png\"") && list.Contains("src=\"/admin/icons/MAIN_SWORD.png\"") && clone.Contains("src=\"/admin/icons/MAIN_SWORD.png\"")
              && !page.Contains("-- icons</code>"), "with the cache: the icon on the item page, in the catalogue and in the clone form");
    }
}
