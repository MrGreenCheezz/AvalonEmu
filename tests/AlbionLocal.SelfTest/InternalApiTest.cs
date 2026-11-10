using System.Net;
using System.Text;
using System.Text.Json;
using AlbionLocal.Server;
using AlbionLocal.Server.Web;
using static AlbionLocal.SelfTest.AlbionTests;

namespace AlbionLocal.SelfTest;

/// <summary>The website's loopback JSON API over real HTTP.</summary>
public static class InternalApiTest
{
    private const string Token = "test-internal-api-token-0123456789abcdef";

    public static IEnumerable<(string, Action)> All =>
    [
        ("internal API: token, account lifecycle, status, faction summary", Lifecycle),
    ];

    private static (ServerHost Host, InternalApi Api, HttpClient Http, string Dir) Start()
    {
        var dir = NewDir();
        var host = Host(dir);
        var api = new InternalApi(host.World, new WebOptions
        {
            ApiEnabled = true,
            ApiPort = 0,
            ApiToken = Token,
        }, "test-client");
        var http = new HttpClient
        {
            BaseAddress = new Uri($"http://127.0.0.1:{api.EndPoint.Port}/"),
            Timeout = TimeSpan.FromSeconds(15),
        };
        http.DefaultRequestHeaders.TryAddWithoutValidation("X-Internal-Token", Token);
        return (host, api, http, dir);
    }

    private static void Stop((ServerHost Host, InternalApi Api, HttpClient Http, string Dir) s)
    {
        s.Http.Dispose();
        s.Api.Dispose();
        s.Host.Dispose();
        try { Directory.Delete(s.Dir, true); } catch (IOException) { }
    }

    private static HttpResponseMessage Post(HttpClient http, string path, object body)
    {
        var req = new HttpRequestMessage(HttpMethod.Post, path)
        {
            Content = new StringContent(JsonSerializer.Serialize(body), Encoding.UTF8, "application/json"),
        };
        return http.Send(req);
    }

    private static JsonElement Json(HttpResponseMessage response)
    {
        var text = response.Content.ReadAsStringAsync().Result;
        return JsonDocument.Parse(text).RootElement.Clone();
    }

    private static void Check(bool condition, string what) => AlbionTests.Check(condition, what);

    private static void Lifecycle()
    {
        var s = Start();
        try
        {
            using (var bare = new HttpClient { BaseAddress = s.Http.BaseAddress, Timeout = TimeSpan.FromSeconds(15) })
            {
                var refused = bare.GetAsync("internal/v1/status").Result;
                Check(refused.StatusCode == HttpStatusCode.Unauthorized, "missing token: 401");
                bare.DefaultRequestHeaders.TryAddWithoutValidation("X-Internal-Token", "wrong-token-wrong-token-wrong-token");
                Check(bare.GetAsync("internal/v1/status").Result.StatusCode == HttpStatusCode.Unauthorized, "wrong token: 401");
            }

            var registered = Post(s.Http, "internal/v1/accounts", new { name = "WebApiUser", password = "web secret 1" });
            var registeredJson = Json(registered);
            var accountIdText = registeredJson.GetProperty("data").GetProperty("accountId").GetString();
            Check(registered.StatusCode == HttpStatusCode.OK && registeredJson.GetProperty("ok").GetBoolean()
                  && Guid.TryParse(accountIdText, out _),
                  "register: account id returned");
            var accountId = Guid.Parse(accountIdText!);
            Check(Post(s.Http, "internal/v1/accounts", new { name = "webapiuser", password = "web secret 1" }).StatusCode == HttpStatusCode.Conflict,
                  "duplicate normalized name: 409");

            var hero = s.Host.Starter.NewCharacter("WebApiHero");
            hero.Account = accountId;
            s.Host.Characters.Create(hero);
            var login = Post(s.Http, "internal/v1/auth/login", new { name = "webapiuser", password = "web secret 1" });
            var loginJson = Json(login);
            Check(login.StatusCode == HttpStatusCode.OK
                  && loginJson.GetProperty("data").GetProperty("characters").GetArrayLength() == 1,
                  "login returns the account's character");
            Check(Post(s.Http, "internal/v1/auth/login", new { name = "webapiuser", password = "wrong pass" }).StatusCode == HttpStatusCode.Unauthorized,
                  "wrong password: 401");

            var changed = Post(s.Http, $"internal/v1/accounts/{accountId}/password/change",
                new { currentPassword = "web secret 1", newPassword = "web secret 2" });
            Check(changed.StatusCode == HttpStatusCode.OK, "change password: 200");
            Check(Post(s.Http, "internal/v1/auth/login", new { name = "webapiuser", password = "web secret 1" }).StatusCode == HttpStatusCode.Unauthorized,
                  "old password refused after change");
            Check(Post(s.Http, "internal/v1/auth/login", new { name = "webapiuser", password = "web secret 2" }).StatusCode == HttpStatusCode.OK,
                  "new password accepted");

            Check(Post(s.Http, $"internal/v1/accounts/{accountId}/password/reset", new { newPassword = "web secret 3" }).StatusCode == HttpStatusCode.OK,
                  "reset password: 200");
            Check(Post(s.Http, "internal/v1/auth/login", new { name = "webapiuser", password = "web secret 3" }).StatusCode == HttpStatusCode.OK,
                  "reset password can log in");
            Check(Post(s.Http, $"internal/v1/accounts/{accountId}/sessions/revoke", new { }).StatusCode == HttpStatusCode.OK,
                  "revoke sessions: 200");

            var status = s.Http.GetAsync("internal/v1/status").Result;
            var statusJson = Json(status);
            Check(status.StatusCode == HttpStatusCode.OK
                  && statusJson.GetProperty("data").GetProperty("playersOnline").GetInt32() == 0
                  && statusJson.GetProperty("data").GetProperty("registrationOpen").GetBoolean(),
                  "status reports zero online players and registration open");

            var factions = s.Http.GetAsync("internal/v1/factions").Result;
            var factionJson = Json(factions);
            Check(factions.StatusCode == HttpStatusCode.OK
                  && factionJson.GetProperty("data").GetProperty("factions").GetArrayLength() == 6,
                  "faction summary always has six rows");
            Check(s.Http.GetAsync("internal/v1/guilds").Result.StatusCode == HttpStatusCode.OK, "guild directory: 200");
            Check(s.Http.GetAsync("internal/v1/online?page=1&limit=10").Result.StatusCode == HttpStatusCode.OK, "online players: 200");
        }
        finally { Stop(s); }
    }
}
