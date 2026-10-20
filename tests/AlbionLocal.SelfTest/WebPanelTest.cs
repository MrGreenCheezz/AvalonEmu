using System.Net;
using System.Text.RegularExpressions;
using AlbionLocal.Server;
using AlbionLocal.Server.Content;
using AlbionLocal.Server.Network;
using AlbionLocal.Server.Persistence;
using AlbionLocal.Server.Web;
using static AlbionLocal.SelfTest.AlbionTests;

namespace AlbionLocal.SelfTest;

/// <summary>The web panel (analysis/work/stageN/webpanel/SPEC.md) over real HTTP on a free loopback port.</summary>
public static class WebPanelTest
{
    public static IEnumerable<(string, Action)> All =>
    [
        ("web panel: registration over HTTP makes an account that logs in to the game", Register),
        ("web panel: admin login, ban with the CSRF token (refused without), backup; a Player account is refused", AdminBan),
    ];

    private static (ServerHost Host, WebPanel Panel, HttpClient Http, string Dir) Start()
    {
        var dir = NewDir();
        var host = Host(dir);
        var backups = new DbBackup(host.Config.DatabasePath, Path.Combine(dir, "backups"), new BackupOptions());
        var panel = new WebPanel(host.World, new WebOptions { Enabled = true, Port = 0 }, backups, null);
        var http = new HttpClient(new HttpClientHandler { UseCookies = false, AllowAutoRedirect = false })
        {
            BaseAddress = new Uri($"http://127.0.0.1:{panel.EndPoint.Port}/"), Timeout = TimeSpan.FromSeconds(15),
        };
        return (host, panel, http, dir);
    }

    private static void Stop((ServerHost Host, WebPanel Panel, HttpClient Http, string Dir) s)
    {
        s.Http.Dispose();
        s.Panel.Dispose();
        s.Host.Dispose();
        try { Directory.Delete(s.Dir, true); } catch (IOException) { }
    }

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

    private static void Register()
    {
        var s = Start();
        try
        {
            var made = Post(s.Http, "register", null, ("name", "WebUser"), ("password", "secret pass 1"), ("password2", "secret pass 1"));
            AlbionTests.Check(made.StatusCode == HttpStatusCode.OK && s.Host.Characters.FindAccount("webuser") is { Level: AccountLevel.Player, PasswordHash: not null },
                  "a registration: 200 and the account at Accounts:DefaultLevel with a password");
            AlbionTests.Check(Post(s.Http, "register", null, ("name", "Other"), ("password", "short"), ("password2", "short")).StatusCode == HttpStatusCode.BadRequest
                  && s.Host.Characters.FindAccount("other") is null, "a password against the game's rules: 400, no account");
            var peer = new FakePeer();
            var login = new LoginSession(peer, s.Host.Config, s.Host.Characters, StarterKit.Default) { World = s.Host.World };
            login.OnRequest(Request(TestLogin("webuser", "secret pass 1"), 1), encrypted: true);
            AlbionTests.Check(peer.Responses[^1].ReturnCode == 0, "the new account logs in to the game with its password");
        }
        finally { Stop(s); }
    }

    private static void AdminBan()
    {
        var s = Start();
        try
        {
            var repo = s.Host.Characters;
            repo.CreateAccount("Boss", Passwords.Hash("boss password"), AccountLevel.Admin);
            var pleb = repo.CreateAccount("Pleb", Passwords.Hash("pleb password"), AccountLevel.Player)!;
            var refused = Post(s.Http, "login", null, ("name", "pleb"), ("password", "pleb password"));
            AlbionTests.Check(refused.StatusCode == HttpStatusCode.Forbidden && !refused.Headers.Contains("Set-Cookie"), "a Player account's right password: refused, no session");
            var ok = Post(s.Http, "login", null, ("name", "boss"), ("password", "boss password"));
            var cookie = ok.Headers.GetValues("Set-Cookie").Single();
            AlbionTests.Check(ok.StatusCode == HttpStatusCode.SeeOther && cookie.Contains("HttpOnly") && cookie.Contains("SameSite=Strict"), "the Admin: a session cookie, HttpOnly, SameSite=Strict");
            cookie = cookie.Split(';')[0];
            var page = Get(s.Http, $"admin/account?id={pleb.Id}", cookie);
            var csrf = Regex.Match(page, "name=csrf value=\"([^\"]+)\"").Groups[1].Value;
            AlbionTests.Check(csrf.Length > 20, "the account page carries the session's CSRF token");
            AlbionTests.Check(Post(s.Http, "admin/ban", cookie, ("id", pleb.Id.ToString()), ("minutes", "0")).StatusCode == HttpStatusCode.Forbidden
                  && repo.FindAccount("pleb")!.BannedUntil is null, "a ban without the token: 403, nothing changed");
            AlbionTests.Check(Post(s.Http, "admin/ban", null, ("csrf", csrf), ("id", pleb.Id.ToString()), ("minutes", "0")).StatusCode == HttpStatusCode.Forbidden,
                  "the token without the session cookie: 403");
            var ban = Post(s.Http, "admin/ban", cookie, ("csrf", csrf), ("id", pleb.Id.ToString()), ("minutes", "0"), ("reason", "test"));
            AlbionTests.Check(ban.StatusCode == HttpStatusCode.SeeOther && repo.FindAccount("pleb") is { BannedUntil: var until, BanReason: "test" } && until == DateTime.MaxValue,
                  "with the token: banned for good with the reason (through .account ban on the world thread)");
            AlbionTests.Check(Post(s.Http, "admin/backup", cookie, ("csrf", csrf)).StatusCode == HttpStatusCode.SeeOther
                  && Directory.GetFiles(Path.Combine(s.Dir, "backups"), "albionlocal-*.db").Length == 1, "Back up now: one copy in backups/");
            foreach (var path in new[] { "", "admin/players", "admin/accounts", $"admin/account?id={pleb.Id}", $"admin/delete?id={pleb.Id}", "admin/log?level=Warn&auto=1", "admin/backups" })
            {
                var request = new HttpRequestMessage(HttpMethod.Get, path);
                request.Headers.Add("Cookie", cookie);
                var answer = s.Http.Send(request);
                AlbionTests.Check(answer.StatusCode == HttpStatusCode.OK && answer.Content.ReadAsStringAsync().Result.Contains("</html>"), $"GET /{path}: a page");
            }
        }
        finally { Stop(s); }
    }
}
