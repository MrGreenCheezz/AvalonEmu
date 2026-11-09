using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using AlbionLocal.Photon;
using AlbionLocal.Server.Commands;
using AlbionLocal.Server.Commands.Builtin;
using AlbionLocal.Server.Entities;
using AlbionLocal.Server.Game;
using AlbionLocal.Server.Persistence;

namespace AlbionLocal.Server.Web;

/// <summary>
/// The local JSON API used by the website. It is bound to loopback by default, requires a shared token and never exposes
/// password hashes or internal exceptions. Live reads run on the world thread; account changes use the existing account
/// commands so the game client and website share the same password and lock rules.
/// </summary>
public sealed class InternalApi : IDisposable
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        PropertyNameCaseInsensitive = true,
        WriteIndented = false,
    };

    private static readonly TimeSpan WorldTimeout = TimeSpan.FromSeconds(10);
    private static readonly (byte Id, string Key, string Name, string City)[] FactionDefaults =
    [
        (1, "highland", "Highland", "Martlock"),
        (2, "forest", "Forest", "Lymhurst"),
        (3, "steppe", "Steppe", "Bridgewatch"),
        (4, "mountain", "Mountain", "Fort Sterling"),
        (5, "swamp", "Swamp", "Thetford"),
        (6, "caerleon", "Caerleon", "Caerleon"),
    ];

    private readonly World _world;
    private readonly WebOptions _options;
    private readonly string? _clientBuild;
    private readonly DateTime _started = DateTime.UtcNow;
    private readonly byte[] _token;
    private readonly MiniHttp _http;

    public InternalApi(World world, WebOptions options, string? clientBuild)
    {
        _world = world;
        _options = options;
        _clientBuild = clientBuild;
        _token = Encoding.UTF8.GetBytes(options.ApiToken);
        _http = new MiniHttp(new IPEndPoint(options.ApiBindAddress, options.ApiPort), Handle);
        Log.Info("api", $"internal API on http://{options.ApiBindAddress}:{EndPoint.Port}/internal/v1/ (local token required)");
    }

    public IPEndPoint EndPoint => _http.EndPoint;

    public void Dispose() => _http.Dispose();

    private async Task<WebResponse> Handle(WebRequest r)
    {
        try
        {
            if (!Authorized(r)) return Error(401, "UNAUTHORIZED", "A valid internal API token is required.");
            if (r.Method is not ("GET" or "POST")) return Error(405, "METHOD_NOT_ALLOWED", "GET and POST only.");

            var path = r.Path.Trim('/');
            if (path == "internal/v1/status" && r.Method == "GET") return await Status();
            if (path == "internal/v1/online" && r.Method == "GET") return await Online(r);
            if (path == "internal/v1/auth/login" && r.Method == "POST") return await Login(r);
            if (path == "internal/v1/accounts" && r.Method == "POST") return await Register(r);
            if (path == "internal/v1/guilds" && r.Method == "GET") return await Guilds(r);
            if (path == "internal/v1/factions" && r.Method == "GET") return await Factions();

            if (TryTail(path, "internal/v1/accounts/", out var accountTail)) return await AccountRoutes(r, accountTail);
            if (TryTail(path, "internal/v1/guilds/", out var guildTail)) return await GuildRoutes(r, guildTail);
            if (TryTail(path, "internal/v1/factions/", out var factionTail)) return await FactionRoutes(r, factionTail);
            return Error(404, "NOT_FOUND", "No such internal API route.");
        }
        catch (ApiError e)
        {
            return Error(e.Status, e.Code, e.Message);
        }
        catch (TimeoutException)
        {
            return Error(503, "WORLD_TIMEOUT", "The game world did not answer in time.");
        }
        catch (JsonException)
        {
            return Error(400, "INVALID_JSON", "The request body is not valid JSON.");
        }
        catch (Exception e)
        {
            Log.Error("api", $"request {r.Method} {r.Path} failed: {e.Message}");
            return Error(500, "INTERNAL_ERROR", "The internal API could not complete the request.");
        }
    }

    private bool Authorized(WebRequest r)
    {
        var supplied = r.Headers.TryGetValue("X-Internal-Token", out var value) ? value : "";
        if (supplied.Length == 0 && r.Headers.TryGetValue("Authorization", out var auth)
            && auth.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase))
            supplied = auth[7..].Trim();
        if (supplied.Length == 0) return false;
        var bytes = Encoding.UTF8.GetBytes(supplied);
        return bytes.Length == _token.Length && CryptographicOperations.FixedTimeEquals(bytes, _token);
    }

    private static bool TryTail(string path, string prefix, out string tail)
    {
        if (!path.StartsWith(prefix, StringComparison.Ordinal))
        {
            tail = "";
            return false;
        }
        tail = path[prefix.Length..];
        return tail.Length > 0;
    }

    private async Task<WebResponse> AccountRoutes(WebRequest r, string tail)
    {
        var parts = tail.Split('/', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length == 0 || !Guid.TryParse(parts[0], out var id)) return Error(404, "ACCOUNT_NOT_FOUND", "No such account.");
        if (r.Method == "GET" && parts.Length == 1) return await Account(id);
        if (r.Method == "GET" && parts is [_, "characters"]) return await Characters(id);
        if (r.Method == "POST" && parts is [_, "password", "change"]) return await ChangePassword(r, id);
        if (r.Method == "POST" && parts is [_, "password", "reset"]) return await ResetPassword(r, id);
        if (r.Method == "POST" && parts is [_, "sessions", "revoke"]) return await RevokeSessions(id);
        return Error(404, "NOT_FOUND", "No such account route.");
    }

    private async Task<WebResponse> GuildRoutes(WebRequest r, string tail)
    {
        var parts = tail.Split('/', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length == 0 || !Guid.TryParse(parts[0], out var id)) return Error(404, "GUILD_NOT_FOUND", "No such guild.");
        if (r.Method == "GET" && parts.Length == 1) return await Guild(id);
        if (r.Method == "GET" && parts is [_, "members"]) return await GuildMembers(id);
        return Error(404, "NOT_FOUND", "No such guild route.");
    }

    private async Task<WebResponse> FactionRoutes(WebRequest r, string tail)
    {
        var parts = tail.Split('/', StringSplitOptions.RemoveEmptyEntries);
        if (r.Method != "GET" || parts.Length != 2 || !byte.TryParse(parts[0], out var id) || id is < 1 or > 6
            || parts[1] != "ranking")
            return Error(404, "NOT_FOUND", "No such faction route.");
        return await FactionRanking(r, id);
    }

    private async Task<WebResponse> Status()
    {
        var data = await OnWorld(() =>
        {
            var players = OnlinePlayers();
            var uptime = DateTime.UtcNow - _started;
            return new
            {
                online = true,
                playersOnline = players.Count,
                botsOnline = _world.Maps.Maps.SelectMany(m => m.Players.Values).Count(p => p.IsBot),
                mapsLoaded = _world.Maps.Maps.Count(),
                uptimeSeconds = (long)Math.Max(0, uptime.TotalSeconds),
                serverBuild = WebPanel.ServerBuild,
                clientBuild = _clientBuild,
                rules = _world.Config.Preset.ToString(),
                registrationOpen = _world.Config.Registration,
            };
        });
        return Ok(data);
    }

    private async Task<WebResponse> Online(WebRequest r)
    {
        var guildFilter = Guid.TryParse(r.Q("guildId"), out var guildId) ? guildId : Guid.Empty;
        var factionFilter = byte.TryParse(r.Q("factionId"), out var factionId) && factionId is >= 1 and <= 6 ? factionId : (byte)0;
        var query = r.Q("q").Trim();
        var page = ParseInt(r.Q("page"), 1, 1, 100_000);
        var limit = ParseInt(r.Q("limit"), 50, 1, 100);

        var data = await OnWorld(() =>
        {
            var rows = OnlinePlayers()
                .Select(x =>
                {
                    var guild = _world.Guilds.Of(x.Player.Character.Id);
                    var alliance = guild is null ? null : _world.Alliances.OfGuild(guild.Id);
                    var faction = x.Player.Character.Faction;
                    return new
                    {
                        characterId = x.Player.Character.Id,
                        name = x.Player.Character.Name,
                        cluster = x.Cluster,
                        guildId = guild?.Id,
                        guildName = guild?.Name,
                        allianceId = alliance?.Id,
                        allianceName = alliance?.Name,
                        allianceTag = alliance?.Tag,
                        factionId = faction == 0 ? (byte?)null : faction,
                        factionName = faction == 0 ? null : FactionName(faction),
                    };
                })
                .Where(x => guildFilter == Guid.Empty || x.guildId == guildFilter)
                .Where(x => factionFilter == 0 || x.factionId == factionFilter)
                .Where(x => query.Length == 0
                    || x.name.Contains(query, StringComparison.OrdinalIgnoreCase)
                    || (x.guildName?.Contains(query, StringComparison.OrdinalIgnoreCase) ?? false))
                .OrderBy(x => x.name, StringComparer.OrdinalIgnoreCase)
                .ToList();
            return new
            {
                total = rows.Count,
                page,
                limit,
                players = rows.Skip((page - 1) * limit).Take(limit).ToList(),
            };
        });
        return Ok(data);
    }

    private async Task<WebResponse> Login(WebRequest r)
    {
        var body = Body<LoginBody>(r);
        var name = (body.Name ?? "").Trim();
        var password = body.Password ?? "";
        if (name.Length == 0 || password.Length == 0) throw new ApiError(400, "CREDENTIALS_REQUIRED", "Account name and password are required.");

        var account = _world.Characters.FindAccount(name);
        var now = DateTime.UtcNow;
        if (account is null)
        {
            Passwords.Dummy(password);
            throw new ApiError(401, "INVALID_CREDENTIALS", "Invalid account name or password.");
        }
        if (account.LockedUntil is { } locked && locked > now)
            throw new ApiError(423, "ACCOUNT_LOCKED", $"The account is locked until {locked:u}.");
        if (account.PasswordHash is null || !Passwords.Verify(password, account.PasswordHash))
        {
            var failed = account.FailedLogins + 1;
            if (_world.Config.MaxFailedLogins > 0 && failed >= _world.Config.MaxFailedLogins)
            {
                var until = now + _world.Config.LockTime;
                _world.Characters.UpdateAccount(account with { FailedLogins = 0, LockedUntil = until });
                throw new ApiError(423, "ACCOUNT_LOCKED", $"Too many wrong passwords. Try again after {until:u}.");
            }
            _world.Characters.UpdateAccount(account with { FailedLogins = failed, LockedUntil = null });
            throw new ApiError(401, "INVALID_CREDENTIALS", "Invalid account name or password.");
        }
        if (account.FailedLogins != 0 || account.LockedUntil is not null)
            _world.Characters.UpdateAccount(account with { FailedLogins = 0, LockedUntil = null });
        if (account.BannedUntil is { } banned && banned > now)
            throw new ApiError(403, "ACCOUNT_BANNED", banned == DateTime.MaxValue
                ? $"The account is banned. {account.BanReason}".Trim()
                : $"The account is banned until {banned:u}. {account.BanReason}".Trim());

        var data = await OnWorld(() => AccountBundle(account.Id));
        if (data is null) throw new ApiError(404, "ACCOUNT_NOT_FOUND", "No such account.");
        return Ok(data);
    }

    private async Task<WebResponse> Register(WebRequest r)
    {
        var body = Body<RegisterBody>(r);
        var name = (body.Name ?? "").Trim();
        var password = body.Password ?? "";
        if (name.Length is < 3 or > 254 || name.Any(ch => ch is <= ' ' or > '~'))
            throw new ApiError(400, "INVALID_NAME", "The name must be 3 to 254 printable ASCII characters without spaces.");
        if (_world.Characters.FindAccount(name) is not null)
            throw new ApiError(409, "ACCOUNT_EXISTS", "That account name is already in use.");
        var (ok, reply) = await Command(ctx => AccountCommands.Create(ctx, name, password));
        if (!ok) throw new ApiError(400, "REGISTRATION_REFUSED", reply);
        var made = _world.Characters.FindAccount(name)
            ?? throw new ApiError(500, "ACCOUNT_NOT_READABLE", "The account was created but could not be read back.");
        return Ok(new { accountId = made.Id, displayName = made.Shown });
    }

    private async Task<WebResponse> Account(Guid id)
    {
        var data = await OnWorld(() => AccountBundle(id, includeCharacters: false));
        return data is null ? Error(404, "ACCOUNT_NOT_FOUND", "No such account.") : Ok(data);
    }

    private async Task<WebResponse> Characters(Guid id)
    {
        var data = await OnWorld(() => AccountBundle(id));
        return data is null ? Error(404, "ACCOUNT_NOT_FOUND", "No such account.") : Ok(data);
    }

    private async Task<WebResponse> ChangePassword(WebRequest r, Guid id)
    {
        var body = Body<ChangePasswordBody>(r);
        var account = _world.Characters.FindAccount(id)
            ?? throw new ApiError(404, "ACCOUNT_NOT_FOUND", "No such account.");
        if (account.Name is null) throw new ApiError(409, "ACCOUNT_NOT_NAMED", "This local account has no login name.");
        if (account.PasswordHash is null || !Passwords.Verify(body.CurrentPassword ?? "", account.PasswordHash))
            throw new ApiError(401, "INVALID_CURRENT_PASSWORD", "The current password is incorrect.");
        if (string.IsNullOrEmpty(body.NewPassword)) throw new ApiError(400, "PASSWORD_REQUIRED", "A new password is required.");
        var (ok, reply) = await Command(ctx => AccountCommands.Password(ctx, account.Name, body.NewPassword));
        if (!ok) throw new ApiError(400, "PASSWORD_REFUSED", reply);
        await OnWorld(() => AccountCommands.Kick(_world, id, "the web account password changed"));
        return Ok(new { changed = true });
    }

    private async Task<WebResponse> ResetPassword(WebRequest r, Guid id)
    {
        var body = Body<ResetPasswordBody>(r);
        var account = _world.Characters.FindAccount(id)
            ?? throw new ApiError(404, "ACCOUNT_NOT_FOUND", "No such account.");
        if (account.Name is null) throw new ApiError(409, "ACCOUNT_NOT_NAMED", "This local account has no login name.");
        if (string.IsNullOrEmpty(body.NewPassword)) throw new ApiError(400, "PASSWORD_REQUIRED", "A new password is required.");
        var (ok, reply) = await Command(ctx => AccountCommands.Password(ctx, account.Name, body.NewPassword));
        if (!ok) throw new ApiError(400, "PASSWORD_REFUSED", reply);
        await OnWorld(() => AccountCommands.Kick(_world, id, "the web account password was reset"));
        return Ok(new { reset = true });
    }

    private async Task<WebResponse> RevokeSessions(Guid id)
    {
        if (_world.Characters.FindAccount(id) is null) return Error(404, "ACCOUNT_NOT_FOUND", "No such account.");
        var count = await OnWorld(() => AccountCommands.Kick(_world, id, "ended from the website"));
        return Ok(new { revokedSessions = count });
    }

    private async Task<WebResponse> Guilds(WebRequest r)
    {
        var query = r.Q("q").Trim();
        var page = ParseInt(r.Q("page"), 1, 1, 100_000);
        var limit = ParseInt(r.Q("limit"), 20, 1, 100);
        var data = await OnWorld(() =>
        {
            var online = OnlineCharacterIds();
            var rows = _world.Guilds.All
                .Where(g => query.Length == 0 || g.Name.Contains(query, StringComparison.OrdinalIgnoreCase))
                .OrderBy(g => g.Name, StringComparer.OrdinalIgnoreCase)
                .Select(g => GuildSummary(g, online))
                .ToList();
            return new { total = rows.Count, page, limit, guilds = rows.Skip((page - 1) * limit).Take(limit).ToList() };
        });
        return Ok(data);
    }

    private async Task<WebResponse> Guild(Guid id)
    {
        var data = await OnWorld(() =>
        {
            var guild = _world.Guilds.ById(id);
            if (guild is null) return null;
            var alliance = _world.Alliances.OfGuild(id);
            return new
            {
                id = guild.Id,
                name = guild.Name,
                createdAt = guild.Created,
                logo = guild.Logo,
                xp = guild.Xp,
                memberCount = guild.Members.Count,
                onlineCount = onlineCount(guild),
                alliance = AllianceData(alliance),
                territoryCount = _world.Territories.OwnedBy(guild.Id),
                hideoutCount = _world.Hideouts.OfGuild(guild.Id).Count,
                roles = Enum.GetNames<GuildRole>(),
            };

            int onlineCount(Guild g)
            {
                var online = OnlineCharacterIds();
                return g.Members.Count(m => online.Contains(m.Id));
            }
        });
        return data is null ? Error(404, "GUILD_NOT_FOUND", "No such guild.") : Ok(data);
    }

    private async Task<WebResponse> GuildMembers(Guid id)
    {
        var data = await OnWorld(() =>
        {
            var guild = _world.Guilds.ById(id);
            if (guild is null) return null;
            var online = OnlineCharacterIds();
            return new
            {
                guildId = guild.Id,
                guildName = guild.Name,
                members = guild.Members
                    .OrderByDescending(m => m.Role)
                    .ThenBy(m => m.Name, StringComparer.OrdinalIgnoreCase)
                    .Select(m => new
                    {
                        characterId = m.Id,
                        name = m.Name,
                        role = m.Role.ToString(),
                        joinedAt = m.Joined,
                        lastSeenAt = m.LastSeen,
                        online = online.Contains(m.Id),
                    }).ToList(),
            };
        });
        return data is null ? Error(404, "GUILD_NOT_FOUND", "No such guild.") : Ok(data);
    }

    private async Task<WebResponse> Factions()
    {
        var data = await OnWorld(() =>
        {
            var characters = _world.Characters.List();
            var rows = FactionDefaults.Select(f =>
            {
                var enlisted = characters.Where(c => c.Faction == f.Id).ToList();
                var totalPointsRaw = enlisted.Sum(c => Value(c.FactionPoints, f.Id));
                var totalStandingRaw = enlisted.Sum(c => Value(c.FactionStanding, f.Id));
                return new
                {
                    id = f.Id,
                    key = f.Key,
                    name = f.Name,
                    city = f.City,
                    enlistedCount = enlisted.Count,
                    totalPointsRaw,
                    totalPoints = totalPointsRaw / 10000d,
                    averageStandingRaw = enlisted.Count == 0 ? 0 : totalStandingRaw / enlisted.Count,
                    averageStanding = enlisted.Count == 0 ? 0 : totalStandingRaw / 10000d / enlisted.Count,
                };
            }).ToList();
            return new { factions = rows, totalCharacters = characters.Count };
        });
        return Ok(data);
    }

    private async Task<WebResponse> FactionRanking(WebRequest r, byte faction)
    {
        var metric = r.Q("metric").Trim().Equals("points", StringComparison.OrdinalIgnoreCase) ? "points" : "standing";
        var limit = ParseInt(r.Q("limit"), 20, 1, 100);
        var data = await OnWorld(() =>
        {
            var rows = _world.Characters.List()
                .Select(c => new
                {
                    characterId = c.Id,
                    name = c.Name,
                    pointsRaw = Value(c.FactionPoints, faction),
                    standingRaw = Value(c.FactionStanding, faction),
                })
                .Select(x => new
                {
                    x.characterId,
                    x.name,
                    x.pointsRaw,
                    x.standingRaw,
                    points = x.pointsRaw / 10000d,
                    standing = x.standingRaw / 10000d,
                    metricValue = metric == "points" ? x.pointsRaw / 10000d : x.standingRaw / 10000d,
                })
                .Where(x => x.metricValue > 0)
                .OrderByDescending(x => x.metricValue)
                .ThenBy(x => x.name, StringComparer.OrdinalIgnoreCase)
                .Take(limit)
                .Select((x, i) => new { rank = i + 1, x.characterId, x.name, x.points, x.standing, x.metricValue })
                .ToList();
            var f = FactionDefaults[faction - 1];
            return new { factionId = faction, factionName = f.Name, metric, entries = rows };
        });
        return Ok(data);
    }

    private object? AccountBundle(Guid id, bool includeCharacters = true)
    {
        var account = _world.Characters.FindAccount(id);
        if (account is null) return null;
        var chars = includeCharacters ? CharacterList(account.Id) : null;
        return new
        {
            accountId = account.Id,
            displayName = account.Shown,
            level = account.Level.ToString(),
            createdAt = account.Created,
            banned = account.BannedUntil is { } banned && banned > DateTime.UtcNow,
            bannedUntil = account.BannedUntil is { } until && until > DateTime.UtcNow ? (DateTime?)until : null,
            banReason = account.BanReason,
            locked = account.LockedUntil is { } locked && locked > DateTime.UtcNow,
            lockedUntil = account.LockedUntil is { } lockUntil && lockUntil > DateTime.UtcNow ? (DateTime?)lockUntil : null,
            characters = chars,
        };
    }

    private List<object> CharacterList(Guid accountId)
    {
        var online = OnlineCharacterIds();
        return _world.Characters.List(accountId)
            .Select(c => _world.LoadCharacter(c.Id) ?? c)
            .Select(c => CharacterData(c, online.Contains(c.Id)))
            .ToList();
    }

    private object CharacterData(Character c, bool online)
    {
        var guild = _world.Guilds.Of(c.Id);
        var member = guild?.Find(c.Id);
        var alliance = guild is null ? null : _world.Alliances.OfGuild(guild.Id);
        return new
        {
            characterId = c.Id,
            name = c.Name,
            cluster = c.Cluster,
            fame = c.Fame / 10000d,
            silver = c.Silver / 10000d,
            gold = c.Gold / 10000d,
            createdAt = c.Created,
            playedSeconds = c.PlayedMilliseconds / 1000d,
            lastSeenAt = c.LastLeftAt > 0 ? new DateTime(c.LastLeftAt, DateTimeKind.Utc) : (DateTime?)null,
            online,
            guildId = guild?.Id,
            guildName = guild?.Name,
            guildRole = member?.Role.ToString(),
            allianceId = alliance?.Id,
            allianceName = alliance?.Name,
            allianceTag = alliance?.Tag,
            factionId = c.Faction == 0 ? (byte?)null : c.Faction,
            factionName = c.Faction == 0 ? null : FactionName(c.Faction),
            flag = c.Flag,
            reputation = c.Reputation,
            factionPointsRaw = c.FactionPoints,
            factionPoints = c.FactionPoints.Select(v => v / 10000d).ToArray(),
            factionStandingRaw = c.FactionStanding,
            factionStanding = c.FactionStanding.Select(v => v / 10000d).ToArray(),
        };
    }

    private object GuildSummary(Guild g, HashSet<Guid> online)
    {
        var alliance = _world.Alliances.OfGuild(g.Id);
        return new
        {
            id = g.Id,
            name = g.Name,
            createdAt = g.Created,
            logo = g.Logo,
            xp = g.Xp,
            memberCount = g.Members.Count,
            onlineCount = g.Members.Count(m => online.Contains(m.Id)),
            alliance = AllianceData(alliance),
            territoryCount = _world.Territories.OwnedBy(g.Id),
            hideoutCount = _world.Hideouts.OfGuild(g.Id).Count,
        };
    }

    private static object? AllianceData(Alliance? alliance) => alliance is null ? null : new
    {
        id = alliance.Id,
        name = alliance.Name,
        tag = alliance.Tag,
        createdAt = alliance.Created,
        guildCount = alliance.Guilds.Count,
    };

    private List<(Player Player, string Cluster)> OnlinePlayers() =>
        _world.Maps.Maps
            .SelectMany(m => m.Players.Values.Where(p => !p.IsBot).Select(p => (Player: p, Cluster: m.Cluster)))
            .ToList();

    private HashSet<Guid> OnlineCharacterIds() =>
        _world.Maps.Maps.SelectMany(m => m.Players.Values).Select(p => p.Character.Id).ToHashSet();

    private string FactionName(byte faction)
    {
        var data = _world.Factions.Data?.Of(faction);
        return data is { Name.Length: > 0 } ? data.Name : FactionDefaults[faction - 1].Name;
    }

    private static long Value(long[] values, byte faction) =>
        faction is >= 1 and <= 6 && values.Length >= faction ? values[faction - 1] : 0;

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

    private Task<(bool Ok, string Reply)> Command(Action<CommandContext> run) => OnWorld(() =>
    {
        var lines = new List<string>();
        var error = false;
        var ctx = new CommandContext(_world, null, AccountLevel.Admin, (text, isError) => { lines.Add(text); error |= isError; }) { Console = true };
        try { run(ctx); }
        catch (CommandError e) { return (false, e.Message); }
        return (!error, string.Join("\n", lines));
    });

    private T Body<T>(WebRequest r) where T : class
    {
        if (!r.ContentType.StartsWith("application/json", StringComparison.OrdinalIgnoreCase))
            throw new ApiError(400, "JSON_REQUIRED", "The request body must be application/json.");
        return JsonSerializer.Deserialize<T>(r.Body, JsonOptions) ?? throw new ApiError(400, "JSON_REQUIRED", "A JSON object is required.");
    }

    private static int ParseInt(string text, int fallback, int min, int max) =>
        int.TryParse(text, out var value) ? Math.Clamp(value, min, max) : fallback;

    private static WebResponse Ok(object data) =>
        Json(200, new { ok = true, data });

    private static WebResponse Error(int status, string code, string message) =>
        Json(status, new { ok = false, error = new { code, message } });

    private static WebResponse Json(int status, object data) =>
        new(status, JsonSerializer.Serialize(data, JsonOptions), "application/json; charset=utf-8");

    private sealed record LoginBody(string? Name, string? Password);
    private sealed record RegisterBody(string? Name, string? Password);
    private sealed record ChangePasswordBody(string? CurrentPassword, string? NewPassword);
    private sealed record ResetPasswordBody(string? NewPassword);

    private sealed class ApiError(int status, string code, string message) : Exception(message)
    {
        public int Status { get; } = status;
        public string Code { get; } = code;
    }
}
