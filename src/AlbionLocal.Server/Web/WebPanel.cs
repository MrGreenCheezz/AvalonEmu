using System.Collections.Concurrent;
using System.Net;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using AlbionLocal.Photon;
using AlbionLocal.Server.Commands;
using AlbionLocal.Server.Commands.Builtin;
using AlbionLocal.Server.Mods;
using AlbionLocal.Server.Persistence;

namespace AlbionLocal.Server.Web;

/// <summary>
/// The built-in web panel (analysis/work/stageN/webpanel/SPEC.md), on <see cref="MiniHttp"/>. Public: the status page and the
/// registration form. Admin (an account of level Admin, cookie session): online players with kick, accounts with ban, unban,
/// level, password and delete, the characters of an account, the log's tail, database backups. Every change runs on the world
/// thread (<see cref="World.Post"/>) through the console's own account commands (<see cref="AccountCommands"/>), and is written
/// to the log as an audit line; passwords and hashes never are.
/// </summary>
public sealed partial class WebPanel : IDisposable
{
    private const string Cookie = "alp_session";
    private static readonly TimeSpan WorldTimeout = TimeSpan.FromSeconds(10);
    private readonly World _world;
    private readonly WebOptions _options;
    private readonly DbBackup _backup;
    private readonly string? _clientBuild;
    private readonly DateTime _started = DateTime.UtcNow;
    private readonly MiniHttp _http;
    private readonly ConcurrentDictionary<string, AdminSession> _sessions = new(StringComparer.Ordinal);
    private readonly RateLimit _registrations = new(5, TimeSpan.FromMinutes(10));
    private readonly RateLimit _logins = new(10, TimeSpan.FromMinutes(5));

    private sealed class AdminSession(Guid account, string name)
    {
        public Guid Account { get; } = account;
        public string Name { get; } = name;
        public string Csrf { get; } = Token();
        public DateTime Expires { get; set; }
        public string? Flash { get; set; }
        public bool FlashError { get; set; }
    }

    /// <summary>Opens the listener (a port in use throws <see cref="System.Net.Sockets.SocketException"/>).</summary>
    /// <param name="modPaths">The modkit's folders for the Mods section (null: found from Paths:ClientGameData).</param>
    /// <param name="modsDir">The content packs (null: Mods:Path).</param>
    /// <param name="iconDir">The icons of <c>modkit icons</c> (null: data/icon-cache beside the saves).</param>
    public WebPanel(World world, WebOptions options, DbBackup backup, string? clientBuild, ModPaths? modPaths = null, string? modsDir = null, string? iconDir = null)
    {
        _icons = new IconCache(iconDir ?? Path.Combine(world.Config.SavesPath, "icon-cache"));
        _world = world;
        _options = options;
        _backup = backup;
        _clientBuild = clientBuild;
        _modsDir = Path.GetFullPath(modsDir ?? world.Config.ModsPath);
        (_modPaths, _modsProblem) = ModSetup(modPaths, world.Config.ClientGameDataPath, _modsDir);
        _http = new MiniHttp(new IPEndPoint(options.BindAddress, options.Port), Handle);
        Log.Info("web", $"web panel on http://{(options.BindAddress.Equals(IPAddress.Any) ? "127.0.0.1" : options.BindAddress.ToString())}:{EndPoint.Port}/"
            + (IPAddress.IsLoopback(options.BindAddress) ? " (this PC only, Web:BindAddress)" : $" (bound to {options.BindAddress}: reachable from other PCs, plain HTTP)"));
    }

    public IPEndPoint EndPoint => _http.EndPoint;

    public void Dispose() => _http.Dispose();

    /// <summary>The server build: the assembly's informational version with its commit, shortened ("1.0.0+cd88d2a").</summary>
    public static string ServerBuild
    {
        get
        {
            var v = typeof(WebPanel).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion ?? "?";
            return v.IndexOf('+') is > 0 and var plus && v.Length > plus + 8 ? v[..(plus + 8)] : v;
        }
    }

    // ---- dispatch

    private async Task<WebResponse> Handle(WebRequest r)
    {
        try
        {
            if (r.Method is not ("GET" or "POST")) return Text(405, "GET and POST only");
            if (r.Method == "POST" && !SameOrigin(r)) return Text(403, "cross-site request refused");
            switch (r.Method, r.Path)
            {
                case ("GET", "/"): return await Status(r);
                case ("GET", "/register"): return RegisterPage(r, null, false);
                case ("POST", "/register"): return await Register(r);
                case ("GET", "/login"): return LoginPage(null, r.Q("next"));
                case ("POST", "/login"): return await Login(r);
            }
            if (r.Path == "/studio" || r.Path.StartsWith("/studio/", StringComparison.Ordinal)) return await Studio(r);
            if (!r.Path.StartsWith("/admin", StringComparison.Ordinal) && r.Path != "/logout") return Text(404, "not found");
            if (Authenticate(r) is not { } s) return r.Method == "GET" ? WebResponse.Redirect("/login") : Text(403, "not logged in (or the session ended)");
            if (r.Method == "POST" && !CsrfOk(s, r.F("csrf"))) return Text(403, "the form's token is not this session's: reload the page");
            if (r.Method == "GET" && r.Path.StartsWith("/admin/icons/", StringComparison.Ordinal)) return IconFile(r.Path["/admin/icons/".Length..]);
            if (r.Path == "/admin/mods" || r.Path.StartsWith("/admin/mods/", StringComparison.Ordinal)) return await Mods(r, s);
            return (r.Method, r.Path) switch
            {
                ("POST", "/logout") => Logout(r, s),
                ("GET", "/admin") or ("GET", "/admin/players") => await Players(s),
                ("POST", "/admin/kick") => await Kick(r, s),
                ("GET", "/admin/accounts") => await Accounts(s),
                ("GET", "/admin/account") => await AccountPage(r, s),
                ("GET", "/admin/delete") => await DeletePage(r, s),
                ("POST", "/admin/ban" or "/admin/unban" or "/admin/level" or "/admin/password" or "/admin/delete") => await AccountAction(r, s),
                ("GET", "/admin/log") => LogPage(r, s),
                ("GET", "/admin/backups") => BackupPage(s),
                ("GET", "/admin/checks") => await ChecksPage(r, s),
                ("GET", "/admin/check") => await CheckPage(r, s),
                ("POST", "/admin/check") => await CheckSet(r, s),
                ("POST", "/admin/backup") => await Backup(r, s),
                _ => Text(404, "not found"),
            };
        }
        catch (TimeoutException)
        {
            return Text(503, "the world did not answer in time (is the server stopping?)");
        }
    }

    /// <summary>A POST's Origin (when sent) is this page's own host: a form from another site is refused, besides SameSite=Strict. The pages send
    /// Referrer-Policy same-origin: under no-referrer a browser posts its own forms with "Origin: null", which this refuses.</summary>
    private static bool SameOrigin(WebRequest r)
    {
        if (!r.Headers.TryGetValue("Origin", out var origin)) return true;
        return Uri.TryCreate(origin, UriKind.Absolute, out var o) && r.Headers.TryGetValue("Host", out var host)
            && string.Equals(o.Authority, host, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>Runs <paramref name="work"/> under the world lock at the next tick; its result, or a TimeoutException after 10 s.</summary>
    private Task<T> OnWorld<T>(Func<T> work)
    {
        var done = new TaskCompletionSource<T>(TaskCreationOptions.RunContinuationsAsynchronously);
        _world.Post(() =>
        {
            try { done.SetResult(work()); }
            catch (Exception e) { done.SetException(e); }
        });
        return done.Task.WaitAsync(WorldTimeout);
    }

    /// <summary>One of the console's account commands, on the world thread as the console runs it; (ok, its reply).</summary>
    private Task<(bool Ok, string Reply)> Command(Action<CommandContext> run) => OnWorld(() =>
    {
        var lines = new List<string>();
        var error = false;
        var ctx = new CommandContext(_world, null, AccountLevel.Admin, (text, isError) => { lines.Add(text); error |= isError; }) { Console = true };
        try { run(ctx); }
        catch (CommandError e) { return (false, e.Message); }
        return (!error, string.Join("\n", lines));
    });

    private static void Audit(string who, IPAddress from, string what) => Log.Always("web", $"audit: {who} from {from}: {what}");

    // ---- sessions

    private static string Token() => Convert.ToBase64String(RandomNumberGenerator.GetBytes(32)).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    private static bool CsrfOk(AdminSession s, string token) =>
        token.Length > 0 && CryptographicOperations.FixedTimeEquals(Encoding.ASCII.GetBytes(token), Encoding.ASCII.GetBytes(s.Csrf));

    /// <summary>The session of the request's cookie, while it has not timed out and its account is still an Admin without a ban; it is extended.</summary>
    private AdminSession? Authenticate(WebRequest r)
    {
        var now = DateTime.UtcNow;
        foreach (var (key, old) in _sessions)
            if (old.Expires <= now) _sessions.TryRemove(key, out _);
        if (!r.Cookies.TryGetValue(Cookie, out var token) || !_sessions.TryGetValue(token, out var s)) return null;
        if (_world.Characters.FindAccount(s.Account) is not { Level: AccountLevel.Admin } a || a.BannedUntil is { } b && b > now)
        {
            _sessions.TryRemove(token, out _);
            return null;
        }
        s.Expires = now.AddMinutes(_options.SessionMinutes);
        return s;
    }

    private async Task<WebResponse> Login(WebRequest r)
    {
        var next = SafeNext(r.F("next"));
        if (!_logins.Allow(r.Remote)) return LoginPage("too many attempts from this address: wait a few minutes", next);
        var name = r.F("name").Trim();
        var password = r.F("password");
        // The password check (PBKDF2) runs here, off the world thread; the repository has its own lock.
        var account = name.Length == 0 ? null : _world.Characters.FindAccount(name);
        var ok = account?.PasswordHash is { } hash ? Passwords.Verify(password, hash) : DummyFalse(password);
        var now = DateTime.UtcNow;
        if (!ok || account is not { Level: AccountLevel.Admin } || account.BannedUntil is { } b && b > now)
        {
            Log.Warn("web", $"admin login refused for '{name}' from {r.Remote}" + (ok ? " (not an Admin account, or banned)" : ""));
            await Task.Delay(300);
            return LoginPage("refused: wrong name or password, or not an Admin account", next);
        }
        var s = new AdminSession(account.Id, account.Shown) { Expires = now.AddMinutes(_options.SessionMinutes) };
        var token = Token();
        _sessions[token] = s;
        Audit(account.ToString(), r.Remote, "admin login");
        var response = WebResponse.Redirect(next ?? "/admin/players");
        response.Headers.Add(("Set-Cookie", $"{Cookie}={token}; Path=/; HttpOnly; SameSite=Strict; Max-Age={_options.SessionMinutes * 60}"));
        return response;

        static bool DummyFalse(string password)
        {
            Passwords.Dummy(password); // the same time as a wrong password: the answer does not tell which names exist
            return false;
        }
    }

    private WebResponse Logout(WebRequest r, AdminSession s)
    {
        if (r.Cookies.TryGetValue(Cookie, out var token)) _sessions.TryRemove(token, out _);
        Audit($"'{s.Name}'", r.Remote, "logout");
        var response = WebResponse.Redirect("/");
        response.Headers.Add(("Set-Cookie", $"{Cookie}=; Path=/; HttpOnly; SameSite=Strict; Max-Age=0"));
        return response;
    }

    // ---- public pages

    private async Task<WebResponse> Status(WebRequest r)
    {
        var (online, bots, maps) = await OnWorld(() =>
        {
            var players = _world.Maps.Maps.SelectMany(m => m.Players.Values).ToList();
            return (players.Count(p => !p.IsBot), players.Count(p => p.IsBot), _world.Maps.Maps.Count());
        });
        var up = DateTime.UtcNow - _started;
        var body = new StringBuilder("<div class=card><h2>Server status</h2><table>")
            .Append(Row("Players online", online.ToString()))
            .Append(bots > 0 ? Row("Bots in the world", bots.ToString()) : "")
            .Append(Row("Maps loaded", maps.ToString()))
            .Append(Row("Uptime", $"{(int)up.TotalDays}d {up.Hours}h {up.Minutes}m"))
            .Append(Row("Server build", ServerBuild))
            .Append(Row("Client build", _clientBuild ?? "unknown"))
            .Append(Row("Rules", _world.Config.Preset.ToString()))
            .Append(Row("Registration", RegistrationOpen ? "<a href=\"/register\">open</a>" : "closed", raw: true))
            .Append("</table></div>");
        return Page("Status", body.ToString(), null);
    }

    private bool RegistrationOpen => _options.Registration && _world.Config.Registration;

    private WebResponse RegisterPage(WebRequest r, string? message, bool error, int status = 200)
    {
        var body = new StringBuilder("<div class=card><h2>Create an account</h2>");
        if (message is not null) body.Append(Note(message, error));
        if (!RegistrationOpen) body.Append("<p>Registration is closed on this server (Web:Registration or Accounts:Registration). Ask the admin for an account.</p>");
        else
            body.Append("<form method=post action=\"/register\" class=stack>")
                .Append("<label>Login name<input name=name required minlength=3 maxlength=254 autocomplete=username value=\"").Append(H(error ? r.F("name") : "")).Append("\"></label>")
                .Append("<label>Password<input name=password type=password required minlength=8 maxlength=72 autocomplete=new-password></label>")
                .Append("<label>Password again<input name=password2 type=password required minlength=8 maxlength=72 autocomplete=new-password></label>")
                .Append("<p class=muted>The name is what you type in the game's login screen (an e-mail address works). Password: 8 to 72 printable ASCII characters, no space at either end, not the name.</p>")
                .Append("<button>Create account</button></form>");
        body.Append("</div>");
        return Page("Register", body.ToString(), null, status);
    }

    private async Task<WebResponse> Register(WebRequest r)
    {
        if (!RegistrationOpen) return RegisterPage(r, "registration is closed", true, 403);
        if (!_registrations.Allow(r.Remote)) return RegisterPage(r, "too many registrations from this address: try again later", true, 429);
        var name = r.F("name").Trim();
        var password = r.F("password");
        if (name.Length is < 3 or > 254 || name.Any(ch => ch is <= ' ' or > '~'))
            return RegisterPage(r, "the name: 3 to 254 printable ASCII characters without spaces", true, 400);
        if (password != r.F("password2")) return RegisterPage(r, "the two passwords differ", true, 400);
        // The console's .account create: the same password rules, the account at Accounts:DefaultLevel.
        var (ok, reply) = await Command(ctx => AccountCommands.Create(ctx, name, password));
        if (!ok) return RegisterPage(r, reply, true, 400);
        Audit("registration", r.Remote, $"account '{name}' created");
        return RegisterPage(r, $"Account '{name}' created: log in with it in the game.", false);
    }

    /// <summary>Where a login goes afterwards: only a local /studio or /admin path (no other host, no //).</summary>
    private static string? SafeNext(string? next) =>
        next is { Length: > 0 and < 200 } && (next == "/studio" || next.StartsWith("/studio/", StringComparison.Ordinal) || next.StartsWith("/admin", StringComparison.Ordinal))
        && !next.Contains("//") && !next.Contains('\\') && next.All(ch => ch > ' ' && ch < 127) ? next : null;

    private static WebResponse LoginPage(string? message, string? next = null) => Page("Admin login",
        "<div class=card><h2>Admin login</h2>" + (message is null ? "" : Note(message, true))
        + "<form method=post action=\"/login\" class=stack>" + (SafeNext(next) is { } n ? $"<input type=hidden name=next value=\"{H(n)}\">" : "")
        + "<label>Login name<input name=name required autocomplete=username></label>"
        + "<label>Password<input name=password type=password required autocomplete=current-password></label><button>Log in</button></form>"
        + "<p class=muted>Accounts of level Admin only (console: <code>.account level &lt;name&gt; Admin</code>).</p></div>", null, message is null ? 200 : 403);

    // ---- admin pages

    private sealed record OnlineRow(Guid Character, string Name, string Cluster, string Account, Guid AccountId);

    private async Task<WebResponse> Players(AdminSession s)
    {
        var rows = await OnWorld(() => _world.Maps.Maps
            .SelectMany(m => m.Players.Values.Where(p => !p.IsBot).Select(p => (Map: m, Player: p)))
            .Select(x => new OnlineRow(x.Player.Character.Id, x.Player.Character.Name, x.Map.Cluster,
                _world.Characters.FindAccount(x.Player.Character.Account)?.Shown ?? "?", x.Player.Character.Account))
            .OrderBy(x => x.Name, StringComparer.OrdinalIgnoreCase).ToList());
        var body = new StringBuilder($"<div class=card><h2>Online players ({rows.Count})</h2>");
        if (rows.Count == 0) body.Append("<p class=muted>Nobody is in the world.</p>");
        else
        {
            body.Append("<div class=scroll><table><tr><th>Character</th><th>Cluster</th><th>Account</th><th></th></tr>");
            foreach (var p in rows)
                body.Append($"<tr><td>{H(p.Name)}</td><td>{H(p.Cluster)}</td><td><a href=\"/admin/account?id={p.AccountId}\">{H(p.Account)}</a></td><td>")
                    .Append(PostButton(s, "/admin/kick", "Kick", ("id", p.Character.ToString())))
                    .Append("</td></tr>");
            body.Append("</table></div>");
        }
        return Page("Players", body.Append("</div>").ToString(), s);
    }

    private async Task<WebResponse> Kick(WebRequest r, AdminSession s)
    {
        if (!Guid.TryParse(r.F("id"), out var id)) return Text(400, "no character id");
        var name = await OnWorld(() =>
        {
            var player = _world.Maps.Maps.SelectMany(m => m.Players.Values).FirstOrDefault(p => !p.IsBot && p.Character.Id == id);
            player?.Session.Close("kicked by an admin");
            return player?.Character.Name;
        });
        if (name is not null) Audit($"admin '{s.Name}'", r.Remote, $"kicked character '{name}' ({id})");
        Flash(s, name is null ? "that character is not online any more" : $"'{name}' kicked", name is null);
        return WebResponse.Redirect("/admin/players");
    }

    private sealed record AccountRow(Account Account, int Characters, bool Online);

    private async Task<WebResponse> Accounts(AdminSession s)
    {
        var rows = await OnWorld(() => _world.Characters.Accounts()
            .Select(a => new AccountRow(a, _world.Characters.List(a.Id).Count, AccountCommands.Online(_world, a.Id))).ToList());
        var now = DateTime.UtcNow;
        var body = new StringBuilder($"<div class=card><h2>Accounts ({rows.Count})</h2><div class=scroll><table>")
            .Append("<tr><th>Name</th><th>Level</th><th>Chars</th><th>State</th><th>Created</th></tr>");
        foreach (var (a, chars, online) in rows)
            body.Append($"<tr><td><a href=\"/admin/account?id={a.Id}\">{H(a.Shown)}</a></td><td>{a.Level}</td><td>{chars}</td><td>{H(State(a, online, now))}</td>")
                .Append($"<td>{(a.Created == default ? "" : a.Created.ToString("yyyy-MM-dd"))}</td></tr>");
        return Page("Accounts", body.Append("</table></div></div>").ToString(), s);
    }

    private static string State(Account a, bool online, DateTime now)
    {
        var notes = new List<string>();
        if (online) notes.Add("online");
        if (a.BannedUntil is { } b && b > now) notes.Add(b == DateTime.MaxValue ? "banned for good" : $"banned until {b:yyyy-MM-dd HH:mm} UTC");
        if (a.LockedUntil is { } l && l > now) notes.Add("locked (wrong passwords)");
        if (a.PasswordHash is null) notes.Add("no password yet");
        return string.Join(", ", notes);
    }

    private sealed record CharacterRow(string Name, long Silver, long Fame, string Cluster, bool Online);

    private async Task<WebResponse> AccountPage(WebRequest r, AdminSession s)
    {
        if (!Guid.TryParse(r.Q("id"), out var id)) return Text(400, "no account id");
        var data = await OnWorld(() =>
        {
            if (_world.Characters.FindAccount(id) is not { } a) return null;
            var live = _world.Maps.Maps.SelectMany(m => m.Players.Values).Select(p => p.Character.Id).ToHashSet();
            // The newest state of each: a player's live character, else a snapshot waiting for the writer, else the saved one.
            var chars = _world.Characters.List(id).Select(c => _world.LoadCharacter(c.Id) ?? c)
                .Select(c => new CharacterRow(c.Name, c.Silver, c.Fame, c.Cluster, live.Contains(c.Id))).ToList();
            return new { Account = a, Characters = chars, Online = AccountCommands.Online(_world, id) };
        });
        if (data is null) return Text(404, "no such account");
        var a = data.Account;
        var self = a.Id == s.Account;
        var body = new StringBuilder($"<div class=card><h2>Account {H(a.Shown)}</h2><table>")
            .Append(Row("Id", a.Id.ToString())).Append(Row("Level", a.Level.ToString()))
            .Append(Row("State", State(a, data.Online, DateTime.UtcNow) is { Length: > 0 } st ? st : "ok"))
            .Append(a.BanReason.Length > 0 ? Row("Ban reason", a.BanReason) : "")
            .Append(Row("Created", a.Created == default ? "?" : $"{a.Created:yyyy-MM-dd HH:mm} UTC"))
            .Append("</table></div>");
        body.Append($"<div class=card><h3>Characters ({data.Characters.Count})</h3>");
        if (data.Characters.Count == 0) body.Append("<p class=muted>None.</p>");
        else
        {
            body.Append("<div class=scroll><table><tr><th>Name</th><th>Silver</th><th>Fame</th><th>Cluster</th><th></th></tr>");
            foreach (var c in data.Characters)
                body.Append($"<tr><td>{H(c.Name)}</td><td class=num>{c.Silver:N0}</td><td class=num>{c.Fame:N0}</td><td>{H(c.Cluster)}</td><td>{(c.Online ? "online" : "")}</td></tr>");
            body.Append("</table></div>");
        }
        body.Append("</div><div class=card><h3>Actions</h3>");
        if (a.Name is null) body.Append("<p class=muted>The local account has no login name yet: the first login names it. No actions until then.</p>");
        else
        {
            var idField = ("id", a.Id.ToString());
            if (self) body.Append("<p class=muted>Your own account: ban, delete and a lower level are not offered here (use the console).</p>");
            else
                body.Append(Form(s, "/admin/ban", "Ban", "danger", idField,
                    "<input name=minutes type=number min=0 value=0 title=\"minutes, 0 = for good\" style=\"width:7em\" aria-label=minutes> min "
                    + "<input name=reason maxlength=200 placeholder=\"reason (shown at login)\" aria-label=reason>"));
            body.Append(Form(s, "/admin/unban", "Unban / unlock", "", idField, ""));
            body.Append(Form(s, "/admin/level", "Set level", "", idField,
                "<select name=level aria-label=level>" + string.Concat(Enum.GetNames<AccountLevel>()
                    .Select(n => $"<option{(n == a.Level.ToString() ? " selected" : "")}>{n}</option>")) + "</select>"));
            body.Append(Form(s, "/admin/password", "Set password", "", idField,
                "<input name=password type=password minlength=8 maxlength=72 required autocomplete=new-password placeholder=\"new password\" aria-label=\"new password\">"));
            if (!self) body.Append($"<p><a class=danger href=\"/admin/delete?id={a.Id}\">Delete this account…</a></p>");
        }
        return Page($"Account {a.Shown}", body.Append("</div>").ToString(), s);
    }

    private async Task<WebResponse> DeletePage(WebRequest r, AdminSession s)
    {
        if (!Guid.TryParse(r.Q("id"), out var id) || _world.Characters.FindAccount(id) is not { Name: not null } a) return Text(404, "no such account");
        var count = await OnWorld(() => _world.Characters.List(id).Count);
        var body = $"<div class=card><h2>Delete account {H(a.Shown)}?</h2><p>This deletes the account and its {count} character(s) for good "
            + "(a backup first: <a href=\"/admin/backups\">Backups</a>). An online account must be banned first.</p>"
            + Form(s, "/admin/delete", "Delete for good", "danger", ("id", a.Id.ToString()),
                $"<input name=confirm required placeholder=\"type the name: {H(a.Shown)}\" aria-label=\"type the account name to confirm\">")
            + $"<p><a href=\"/admin/account?id={a.Id}\">Cancel</a></p></div>";
        return Page("Delete account", body, s);
    }

    private async Task<WebResponse> AccountAction(WebRequest r, AdminSession s)
    {
        if (!Guid.TryParse(r.F("id"), out var id) || _world.Characters.FindAccount(id) is not { } a) return Text(404, "no such account");
        var back = WebResponse.Redirect($"/admin/account?id={id}");
        if (a.Name is not { } name)
        {
            Flash(s, "the local account has no login name yet", true);
            return back;
        }
        var self = id == s.Account;
        string what;
        Action<CommandContext> run;
        switch (r.Path)
        {
            case "/admin/ban":
                if (self) return Refuse("you cannot ban your own account here");
                if (!int.TryParse(r.F("minutes"), out var minutes) || minutes < 0) return Refuse("minutes: 0 (for good) or more");
                var reason = new string([.. r.F("reason").Where(ch => ch >= ' ')]).Trim(); // one log line
                if (reason.Length > 200) reason = reason[..200];
                what = $"ban {a} {(minutes == 0 ? "for good" : $"for {minutes} min")}{(reason.Length > 0 ? $" ({reason})" : "")}";
                run = ctx => AccountCommands.Ban(ctx, name, minutes, new Tail(reason.Length == 0 ? null : reason));
                break;
            case "/admin/unban":
                what = $"unban {a}";
                run = ctx => AccountCommands.Unban(ctx, name);
                break;
            case "/admin/level":
                if (!Enum.TryParse<AccountLevel>(r.F("level"), out var level) || !Enum.IsDefined(level)) return Refuse("no such level");
                if (self && level != AccountLevel.Admin) return Refuse("you cannot lower your own level here (the console can)");
                what = $"level of {a}: {a.Level} -> {level}";
                run = ctx => AccountCommands.SetLevel(ctx, name, level);
                break;
            case "/admin/password":
                var password = r.F("password");
                what = $"new password for {a}"; // never the password itself
                run = ctx => AccountCommands.Password(ctx, name, password);
                break;
            default: // /admin/delete
                if (self) return Refuse("you cannot delete your own account here");
                if (!string.Equals(r.F("confirm").Trim(), a.Shown, StringComparison.OrdinalIgnoreCase))
                {
                    Flash(s, "not deleted: the name typed does not match", true);
                    return WebResponse.Redirect($"/admin/delete?id={id}");
                }
                what = $"delete {a}";
                run = ctx => AccountCommands.Delete(ctx, name);
                break;
        }
        var (ok, reply) = await Command(run);
        Audit($"admin '{s.Name}'", r.Remote, $"{what}: {(ok ? "done" : "refused: " + reply)}");
        Flash(s, reply, !ok);
        return ok && r.Path == "/admin/delete" ? WebResponse.Redirect("/admin/accounts") : back;

        WebResponse Refuse(string why)
        {
            Flash(s, why, true);
            return back;
        }
    }

    private WebResponse LogPage(WebRequest r, AdminSession s)
    {
        var min = Enum.TryParse<LogLevel>(r.Q("level"), true, out var l) && Enum.IsDefined(l) ? l : LogLevel.Info;
        var count = int.TryParse(r.Q("lines"), out var n) ? Math.Clamp(n, 10, 2000) : 200;
        var auto = r.Q("auto") == "1";
        var body = new StringBuilder("<div class=card><h2>Server log</h2><form method=get action=\"/admin/log\" class=inline>")
            .Append("<label>Level <select name=level>")
            .Append(string.Concat(Enum.GetValues<LogLevel>().Select(v => $"<option{(v == min ? " selected" : "")}>{v}</option>")))
            .Append($"</select></label><label>Lines <input name=lines type=number min=10 max=2000 value={count} style=\"width:6em\"></label>")
            .Append($"<label><input type=checkbox name=auto value=1{(auto ? " checked" : "")}> auto-refresh (5 s)</label><button>Show</button></form>");
        if (_world.Config.LogFile is not { } file) body.Append("<p class=muted>No log file (Log:File is \"\").</p>");
        else if (!File.Exists(file)) body.Append($"<p class=muted>{H(file)} does not exist yet.</p>");
        else
        {
            var lines = TailLog(file, min, count);
            body.Append($"<p class=muted>{H(file)}: the last {lines.Count} line(s) at {min} or above (newest last).</p><pre class=log>");
            foreach (var (level, text) in lines) body.Append($"<span class={level}>{H(text)}</span>\n");
            body.Append("</pre>");
        }
        return Page("Log", body.Append("</div>").ToString(), s, head: auto ? "<meta http-equiv=refresh content=5>" : "");
    }

    /// <summary>
    /// The last <paramref name="count"/> lines at <paramref name="min"/> or above of the log file's last MB (shared read: the
    /// server keeps writing it). A line without the "HH:mm:ss.fff Level [source]" head (a stack trace) has its entry's level.
    /// </summary>
    internal static List<(LogLevel Level, string Text)> TailLog(string file, LogLevel min, int count)
    {
        string text;
        bool cut;
        using (var fs = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
        {
            var take = (int)Math.Min(fs.Length, 1 << 20);
            cut = take < fs.Length;
            fs.Seek(-take, SeekOrigin.End);
            var bytes = new byte[take];
            fs.ReadExactly(bytes);
            text = Encoding.UTF8.GetString(bytes);
        }
        var all = text.Split('\n').Select(t => t.TrimEnd('\r')).Skip(cut ? 1 : 0).Where(t => t.Length > 0);
        var result = new List<(LogLevel, string)>();
        var current = LogLevel.Info;
        foreach (var line in all)
        {
            if (line.Length > 19 && line[2] == ':' && line[12] == ' ' && Enum.TryParse<LogLevel>(line.AsSpan(13, 5).Trim(), out var parsed)) current = parsed;
            if (current >= min) result.Add((current, line));
        }
        return result.Count > count ? result[^count..] : result;
    }

    private WebResponse BackupPage(AdminSession s)
    {
        var files = _backup.List();
        var body = new StringBuilder("<div class=card><h2>Database backups</h2>")
            .Append($"<p class=muted>A consistent copy of {H(_world.Config.DatabasePath)} (VACUUM INTO, the unsaved characters saved first) into {H(_backup.Folder)}. ")
            .Append(_world.Config.Backup.EveryHours > 0 ? $"Automatic: every {_world.Config.Backup.EveryHours:0.##} h" : "Automatic backups are off (Backup:EveryHours)")
            .Append($"; the newest {_world.Config.Backup.Keep} are kept (Backup:Keep).</p>")
            .Append(Form(s, "/admin/backup", "Back up now", "", null, ""));
        if (files.Count > 0)
        {
            body.Append("<div class=scroll><table><tr><th>File</th><th>Size</th><th>Made</th></tr>");
            foreach (var f in files) body.Append($"<tr><td>{H(f.Name)}</td><td class=num>{f.Length / 1024.0:0.#} KB</td><td>{f.LastWriteTime:yyyy-MM-dd HH:mm:ss}</td></tr>");
            body.Append("</table></div>");
        }
        return Page("Backups", body.Append("</div>").ToString(), s);
    }

    private async Task<WebResponse> Backup(WebRequest r, AdminSession s)
    {
        try
        {
            await OnWorld(() =>
            {
                _world.Saves.SaveAll(); // the characters' unsaved changes into the writer's queue
                return true;
            });
            var path = await Task.Run(() =>
            {
                _world.Saves.Flush(); // and written, off the world thread
                return _backup.Run($"manual (admin '{s.Name}')");
            });
            Audit($"admin '{s.Name}'", r.Remote, $"database backup {Path.GetFileName(path)}");
            Flash(s, $"backup written: {Path.GetFileName(path)}", false);
        }
        catch (Exception e) when (e is not TimeoutException)
        {
            Log.Error("web", $"backup failed: {e.Message}");
            Flash(s, $"backup failed: {e.Message}", true);
        }
        return WebResponse.Redirect("/admin/backups");
    }

    // ---- HTML

    private static void Flash(AdminSession s, string text, bool error) => (s.Flash, s.FlashError) = (text, error);

    private static string H(string? text) => WebUtility.HtmlEncode(text ?? "");

    private static string Row(string name, string value, bool raw = false) => $"<tr><th>{H(name)}</th><td>{(raw ? value : H(value))}</td></tr>";

    private static string Note(string text, bool error) => $"<p class=\"note{(error ? " err" : "")}\">{H(text).Replace("\n", "<br>")}</p>";

    private static string PostButton(AdminSession s, string action, string label, (string, string) field) => Form(s, action, label, "", field, "");

    private static string Form(AdminSession s, string action, string label, string cls, (string Name, string Value)? field, string inputs) =>
        $"<form method=post action=\"{action}\" class=inline><input type=hidden name=csrf value=\"{s.Csrf}\">"
        + (field is { } f ? $"<input type=hidden name=\"{f.Name}\" value=\"{H(f.Value)}\">" : "")
        + $"{inputs}<button{(cls.Length > 0 ? $" class={cls}" : "")}>{H(label)}</button></form>";

    private static WebResponse Text(int status, string text) => Page(status == 404 ? "Not found" : "Refused", Note(text, true), null, status);

    private static WebResponse Page(string title, string body, AdminSession? s, int status = 200, string head = "")
    {
        var nav = s is null
            ? "<a href=\"/\">Status</a><a href=\"/register\">Register</a><a href=\"/login\">Admin</a>"
            : "<a href=\"/\">Status</a><a href=\"/admin/players\">Players</a><a href=\"/admin/accounts\">Accounts</a><a href=\"/admin/log\">Log</a><a href=\"/admin/backups\">Backups</a><a href=\"/admin/checks\">Checks</a><a href=\"/admin/mods\">Mods</a><a href=\"/studio\">Studio</a>"
              + $"<form method=post action=\"/logout\" class=inline><input type=hidden name=csrf value=\"{s.Csrf}\"><button class=link>Log out {H(s.Name)}</button></form>";
        var flash = "";
        if (s?.Flash is { } f)
        {
            flash = Note(f, s.FlashError);
            s.Flash = null;
        }
        var html = $"<!doctype html><html lang=en><head><meta charset=utf-8><meta name=viewport content=\"width=device-width,initial-scale=1\">{head}"
            + $"<title>{H(title)} - AlbionLocal</title><style>{Css}</style></head><body><header><b>AlbionLocal</b><nav>{nav}</nav></header>"
            + $"<main>{flash}{body}</main></body></html>";
        return new WebResponse(status, html);
    }

    private const string Css = """
        :root{color-scheme:light dark;--bg:#f5f6f8;--fg:#1c2230;--muted:#5f6878;--card:#fff;--line:#d8dce3;--accent:#2f6fde;--bad:#c0392b;--warn:#a86b00;--ok:#1e8449}
        @media (prefers-color-scheme:dark){:root{--bg:#13161b;--fg:#e3e6eb;--muted:#99a2b1;--card:#1c2027;--line:#2d333d;--accent:#6c9eff;--bad:#ff6b5e;--warn:#e0b050;--ok:#58d68d}}
        *{box-sizing:border-box}body{margin:0;font:15px/1.45 system-ui,-apple-system,Segoe UI,sans-serif;background:var(--bg);color:var(--fg)}
        header{display:flex;flex-wrap:wrap;gap:8px 16px;align-items:center;padding:10px 16px;background:var(--card);border-bottom:1px solid var(--line)}
        nav{display:flex;flex-wrap:wrap;gap:4px 14px;align-items:center}a{color:var(--accent)}nav a{text-decoration:none}
        main{max-width:980px;margin:0 auto;padding:16px}.card{background:var(--card);border:1px solid var(--line);border-radius:8px;padding:12px 16px;margin:0 0 16px}
        h2,h3{margin:4px 0 12px}table{border-collapse:collapse;width:100%}th,td{text-align:left;padding:6px 8px;border-bottom:1px solid var(--line);vertical-align:middle}
        td.num{text-align:right;font-variant-numeric:tabular-nums}.scroll{overflow-x:auto}.muted{color:var(--muted)}
        input,select,button{font:inherit;padding:6px 9px;border:1px solid var(--line);border-radius:6px;background:var(--bg);color:var(--fg);max-width:100%}
        button{background:var(--accent);border-color:var(--accent);color:#fff;cursor:pointer}button.danger{background:var(--bad);border-color:var(--bad)}
        button.link{background:none;border:none;color:var(--accent);padding:0}a.danger{color:var(--bad)}
        form.inline{display:inline-flex;flex-wrap:wrap;gap:6px;align-items:center;margin:3px 0}.card>form.inline{display:flex}
        form.stack{display:grid;gap:10px;max-width:420px}form.stack label{display:grid;gap:4px}
        .note{padding:8px 12px;border-radius:6px;border:1px solid var(--line);background:var(--card)}.note.err{border-color:var(--bad);color:var(--bad)}
        pre.log{white-space:pre-wrap;word-break:break-word;font:12px/1.4 ui-monospace,Consolas,monospace;max-height:70vh;overflow:auto;margin:0}
        .Warn{color:var(--warn)}.Error{color:var(--bad)}.Debug,.Trace{color:var(--muted)}
        table.small th,table.small td{padding:4px 6px;font-size:13px}.pager{margin:10px 0}.tag{font-size:11px;padding:1px 5px;border-radius:4px;background:var(--line)}
        pre.xml,pre.diff{white-space:pre-wrap;word-break:break-word;font:12px/1.4 ui-monospace,Consolas,monospace;max-height:60vh;overflow:auto;margin:6px 0}
        pre.diff .add{color:var(--ok)}pre.diff .del{color:var(--bad)}details summary{cursor:pointer}code{font:12px ui-monospace,Consolas,monospace}
        form.edit fieldset{border:1px solid var(--line);border-radius:6px;margin:0 0 12px;padding:8px 12px}form.edit legend{font-weight:600;padding:0 4px}
        .grid{display:grid;grid-template-columns:repeat(auto-fill,minmax(200px,1fr));gap:8px}.grid2{display:grid;grid-template-columns:repeat(auto-fit,minmax(280px,1fr));gap:8px;margin-top:8px}
        .grid label,.grid2 label,form.edit>fieldset>label{display:grid;gap:2px;font-size:13px}.grid input,.grid select,.grid2 textarea,form.edit td input{width:100%}
        textarea{font:inherit;padding:6px 9px;border:1px solid var(--line);border-radius:6px;background:var(--bg);color:var(--fg);max-width:100%}
        input.ro{opacity:.65}label.check{display:block;margin-top:8px}.boxes{display:inline-flex;flex-wrap:wrap;gap:4px 12px}
        .warnbox{border:1px solid var(--warn);border-radius:6px;padding:8px 12px;margin:10px 0}.note.ok{border-color:var(--ok);color:var(--ok)}
        img.icon{vertical-align:middle;margin-right:6px;object-fit:contain}h2 img.icon{margin-right:10px}
        ul.errs li{color:var(--bad)}ul.warns li{color:var(--warn)}ul.small,ol.small{font-size:13px}
        """;

    /// <summary>At most <c>max</c> tries per address in a sliding window.</summary>
    private sealed class RateLimit(int max, TimeSpan window)
    {
        private readonly Dictionary<IPAddress, Queue<DateTime>> _tries = [];

        public bool Allow(IPAddress from)
        {
            var now = DateTime.UtcNow;
            lock (_tries)
            {
                if (_tries.Count > 10_000) _tries.Clear();
                var q = _tries.TryGetValue(from, out var found) ? found : _tries[from] = new Queue<DateTime>();
                while (q.Count > 0 && now - q.Peek() > window) q.Dequeue();
                if (q.Count >= max) return false;
                q.Enqueue(now);
                return true;
            }
        }
    }
}
