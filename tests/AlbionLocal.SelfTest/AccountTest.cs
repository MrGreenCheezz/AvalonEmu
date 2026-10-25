using AlbionLocal.Photon.Serialization;
using AlbionLocal.Server;
using AlbionLocal.Server.Commands;
using AlbionLocal.Server.Content;
using AlbionLocal.Server.Network;
using AlbionLocal.Server.Persistence;
using AlbionLocal.Server.Protocol;
using static AlbionLocal.SelfTest.AlbionTests;

namespace AlbionLocal.SelfTest;

/// <summary>
/// Real accounts (analysis/work/accounts): Login's codes and keys, the lock after wrong passwords, bans, registration
/// through CreateAccount, the first login of an account of before, the redaction of secrets in logs.
/// </summary>
public static class AccountTest
{
    public static IEnumerable<(string, Action)> All =>
    [
        ("accounts: login ok, wrong password and unknown name 201, lock after 5 (210), ban 205 with key 64, second login closes the first", Logins),
        ("accounts: CreateAccount then login; name taken 161, bad password 163, bad email 167", Register),
        ("accounts: an account of before has no password: its first login sets the one typed", LegacyFirstLogin),
        ("accounts: empty database cannot claim the legacy account or bypass disabled auto-registration", EmptyDatabaseLogin),
        ("accounts: passwords redacted in MessageDump and in logged command lines, account commands console-only", Redaction),
    ];

    /// <summary>A Login on its own connection; the last response.</summary>
    private static OperationResponse LoginAs(ServerConfig config, ICharacterRepository repo, string name, string password,
        Func<DateTime>? now = null, World? world = null, FakePeer? peer = null)
    {
        peer ??= new FakePeer();
        var login = new LoginSession(peer, config, repo, StarterKit.Default) { World = world, UtcNow = now ?? (() => DateTime.UtcNow) };
        login.OnRequest(Request(TestLogin(name, password), 1), encrypted: true);
        return peer.Responses[^1];
    }

    private static void Logins()
    {
        var dir = NewDir();
        using var repo = Repository(dir);
        var config = new ServerConfig { LegacyAccountName = "nobody-claims-it" };
        Check(repo.CreateAccount("Alice", Passwords.Hash("correct horse"), AccountLevel.Player) is { Name: "alice", DisplayName: "Alice" },
              "an account: login key lower case, the name as typed kept");
        var ok = LoginAs(config, repo, "  ALICE ", " correct horse ");
        Check(ok.ReturnCode == 0 && ok.Parameters[51] is byte[] { Length: 16 }, "the right password (name in any case, both trimmed): RC 0 with the account id");
        var wrong = LoginAs(config, repo, "alice", "wrong horse");
        Check(wrong.ReturnCode == LoginCodes.InvalidAccountNameOrPassword && !wrong.Parameters.ContainsKey(51) && !wrong.Parameters.ContainsKey(9)
              && wrong.Parameters[ReservedKeys.OperationCode] is (short)OperationCodes.Login && wrong.Parameters[ReservedKeys.RequestId] is 1,
              "a wrong password: 201 with the op code and request id, nothing of the success payload");
        Check(LoginAs(config, repo, "mallory", "whatever1").ReturnCode == LoginCodes.InvalidAccountNameOrPassword, "an unknown name: 201 as well");
        Check(LoginAs(config, repo, "alice", "").ReturnCode == LoginCodes.InvalidAccountNameOrPassword, "no password: 201");
        Check(LoginAs(config with { AutoRegister = true }, repo, "mallory", "whatever1").ReturnCode == 0 && repo.FindAccount("Mallory") is { PasswordHash: not null },
              "Accounts:AutoRegister: an unknown name with a password makes the account");

        // Wrong passwords: one more each time (the right one in between resets the count), the 5th in a row locks.
        var clock = DateTime.UtcNow;
        DateTime Now() => clock;
        Check(LoginAs(config, repo, "alice", "correct horse", Now).ReturnCode == 0 && repo.FindAccount("alice") is { FailedLogins: 0 },
              "the right password resets the count of the wrong one before");
        for (var i = 1; i <= 4; i++)
            Check(LoginAs(config, repo, "alice", "nope", Now).ReturnCode == LoginCodes.InvalidAccountNameOrPassword, $"wrong password {i}: 201");
        var locked = LoginAs(config, repo, "alice", "nope", Now);
        Check(locked.ReturnCode == LoginCodes.BlockedByEnteringWrongPassword && locked.Parameters[56] is long left && left == TimeSpan.FromMinutes(15).Ticks
              && locked.Parameters[64] is "Alice", "the 5th wrong password: 210 with key 56 = 15 min and key 64 = the name (never null)");
        clock += TimeSpan.FromMinutes(5);
        var still = LoginAs(config, repo, "alice", "correct horse", Now);
        Check(still.ReturnCode == LoginCodes.BlockedByEnteringWrongPassword && still.Parameters[56] is long left2 && left2 == TimeSpan.FromMinutes(10).Ticks
              && still.Parameters[64] is "Alice", "while locked even the right password gets 210, with the time left");
        clock += TimeSpan.FromMinutes(11);
        Check(LoginAs(config, repo, "alice", "correct horse", Now).ReturnCode == 0 && repo.FindAccount("alice") is { FailedLogins: 0, LockedUntil: null },
              "after the lock the right password logs in again and clears it");

        // A ban: told only with the right password, with its end, reason and key 64.
        var until = DateTime.UtcNow.AddHours(1);
        repo.UpdateAccount(repo.FindAccount("alice")! with { BannedUntil = until, BanReason = "botting" });
        Check(LoginAs(config, repo, "alice", "nope").ReturnCode == LoginCodes.InvalidAccountNameOrPassword, "banned, wrong password: 201 (the ban is not told)");
        var banned = LoginAs(config, repo, "alice", "correct horse");
        Check(banned.ReturnCode == LoginCodes.AccountIsBanned && banned.Parameters[52] is long end && end == until.Ticks && banned.Parameters[54] is "botting"
              && banned.Parameters[64] is "Alice" && !banned.Parameters.ContainsKey(51), "banned: 205 with keys 52 (UTC ticks), 54, 64 and no account id");
        repo.UpdateAccount(repo.FindAccount("alice")! with { BannedUntil = null, BanReason = "" });
        Check(LoginAs(config with { Maintenance = true }, repo, "alice", "correct horse").ReturnCode == LoginCodes.InsufficientAccessLevel,
              "Accounts:Maintenance: 204 (the client's maintenance message)");
        Check(LoginAs(config, repo, "alice", "correct horse") is { ReturnCode: 0 }, "unbanned: in again");
        var steamLogin = TestLogin("alice", "correct horse");
        steamLogin.u0 = "steam-ticket";
        var steam = new FakePeer();
        new LoginSession(steam, config, repo, StarterKit.Default).OnRequest(Request(steamLogin, 1), encrypted: true);
        Check(steam.Responses[^1].ReturnCode == LoginCodes.InvalidAccountNameOrPassword, "a Steam login (key 8 set): 201");

        // A second Login of the account closes the first one's connections.
        var world = new World(config, repo, Client.Items) { Sync = new object(), Clock = new ManualClock() };
        var first = new FakePeer();
        Check(LoginAs(config, repo, "alice", "correct horse", world: world, peer: first).ReturnCode == 0 && first.DisconnectReason is null, "the first login");
        var second = new FakePeer();
        Check(LoginAs(config, repo, "alice", "correct horse", world: world, peer: second).ReturnCode == 0
              && first.DisconnectReason == AlbionSession.DisconnectByServerLogic && second.DisconnectReason is null,
              "the second login closes the first connection (DisconnectByServerLogic), not itself");
        world.Dispose();
    }

    private static void Register()
    {
        var dir = NewDir();
        using var repo = Repository(dir);
        var config = new ServerConfig { LegacyAccountName = "nobody-claims-it" };
        OperationResponse Create(string? email, string? password, ServerConfig? with = null)
        {
            var peer = new FakePeer();
            new LoginSession(peer, with ?? config, repo, StarterKit.Default)
                .OnRequest(Request(new CreateAccountRequest { aiu = 1, aiv = 32, aiw = 455, aiy = 345319, hp = email, hq = password, hs = "device" }, 3), encrypted: true);
            return peer.Responses[^1];
        }
        var made = Create(" New.Player@example.com ", "s3cret-pass");
        Check(made.ReturnCode == 0 && made.Parameters[ReservedKeys.OperationCode] is (short)OperationCodes.CreateAccount && made.Parameters[ReservedKeys.RequestId] is 3
              && made.Parameters[1] is (short)32, "CreateAccount: RC 0, the version echoed");
        Check(repo.FindAccount("new.player@example.com") is { DisplayName: "New.Player@example.com", PasswordHash: not null } a && repo.List(a.Id).Count == 0,
              "the account exists at once (email trimmed), empty");
        var login = LoginAs(config, repo, "new.player@example.com", "s3cret-pass");
        Check(login.ReturnCode == 0 && login.Parameters[9] is string[] { Length: 0 }, "then the login with it works and lists no characters");
        Check(Create("NEW.PLAYER@example.com", "another-pass").ReturnCode == LoginCodes.NameAlreadyTaken, "the same name again (any case): 161");
        var shortPw = Create("short@example.com", "abc");
        Check(shortPw.ReturnCode == LoginCodes.ValidationErrors && shortPw.Parameters[9] is (byte)PasswordProblem.Length, "a short password: 163 with key 9 = length");
        var spaced = Create("spaced@example.com", " leading-space");
        Check(spaced.ReturnCode == LoginCodes.ValidationErrors && spaced.Parameters[9] is (byte)PasswordProblem.InvalidCharacters,
              "a password with a space at an end (login trims it): 163, invalid characters");
        var same = Create("Same@Example.com", "same@example.com");
        Check(same.ReturnCode == LoginCodes.ValidationErrors && ((byte)same.Parameters[9]! & (byte)PasswordProblem.MatchesName) != 0, "the password = the email: 163 with flag 4");
        Check(Create("none@example.com", null).Parameters[9] is (byte)PasswordProblem.NotProvided, "no password: flag 16");
        Check(Create("not-an-email", "s3cret-pass").ReturnCode == LoginCodes.InvalidEmail, "not an email: 167");
        Check(Create("off@example.com", "s3cret-pass", config with { Registration = false }).ReturnCode == LoginCodes.CreationDisabled, "Accounts:Registration off: 165");
        Check(repo.Accounts().Count == 2, "nothing else stored (the local account and the new one)");
    }

    private static void LegacyFirstLogin()
    {
        var dir = NewDir();
        using var repo = Repository(dir); // the local account with LocalHero, no name and no password
        var config = new ServerConfig();
        var first = LoginAs(config, repo, "Hero", "first-pass");
        Check(first.ReturnCode == 0 && new Guid((byte[])first.Parameters[51]!) == SqliteCharacterRepository.LocalAccountId && first.Parameters[9] is string[] and ["LocalHero"],
              "the first name takes the local account with its characters");
        Check(repo.Account.Id == SqliteCharacterRepository.LocalAccountId && repo.FindAccount("hero") is { PasswordHash: not null, DisplayName: "Hero" },
              "and the password typed is its password now");
        Check(LoginAs(config, repo, "hero", "other-pass").ReturnCode == LoginCodes.InvalidAccountNameOrPassword, "another password is wrong from then on");
        Check(LoginAs(config, repo, "hero", "first-pass").ReturnCode == 0, "the first one works");
        // A stage-7 account of a name (made without a password): the same.
        Check(repo.CreateAccount("Old", null, AccountLevel.Admin) is not null && LoginAs(config, repo, "old", "old-pass").ReturnCode == 0
              && LoginAs(config, repo, "old", "nope-pass").ReturnCode == LoginCodes.InvalidAccountNameOrPassword, "a name's account without a password: the first login sets it");
        Check(LoginAs(config, repo, "stranger", "x-pass").ReturnCode == LoginCodes.InvalidAccountNameOrPassword,
              "the local account is taken: an unknown name is refused (Accounts:AutoRegister off)");
    }

    private static void EmptyDatabaseLogin()
    {
        var dir = NewDir();
        var path = Path.Combine(dir, "accounts.db");
        var config = new ServerConfig { AutoRegister = false };
        using (var repo = new SqliteCharacterRepository(path))
        {
            foreach (var legacyName in new[] { "", "fresh@example.com" })
            foreach (var registration in new[] { true, false })
            {
                var response = LoginAs(config with { LegacyAccountName = legacyName, Registration = registration },
                    repo, "fresh@example.com", "s3cret-pass");
                Check(response.ReturnCode == LoginCodes.InvalidAccountNameOrPassword && !response.Parameters.ContainsKey(51),
                    "an empty database refuses unknown logins regardless of registration or explicit legacy name");
                Check(repo.Accounts().Count == 1 && repo.FindAccount("fresh@example.com") is null
                    && repo.FindAccount(SqliteCharacterRepository.LocalAccountId) is { Name: null, PasswordHash: null },
                    "refused login leaves the unnamed local account and its password untouched");
            }
            var peer = new FakePeer();
            new LoginSession(peer, config, repo, StarterKit.Default).OnRequest(Request(new CreateAccountRequest
                { aiu = 1, aiv = 32, aiw = 455, aiy = 345319, hp = "registered@example.com", hq = "s3cret-pass" }, 3), encrypted: true);
            Check(peer.Responses[^1].ReturnCode == 0 && repo.FindAccount("registered@example.com") is { Level: AccountLevel.Player } registered
                && registered.Id != SqliteCharacterRepository.LocalAccountId,
                "explicit registration still creates a normal player account with auto-registration disabled");
            Check(LoginAs(config, repo, "registered@example.com", "s3cret-pass").ReturnCode == 0,
                "explicitly registered account can log in");
        }
        using (var repo = new SqliteCharacterRepository(path))
        {
            Check(LoginAs(config, repo, "fresh@example.com", "s3cret-pass").ReturnCode == LoginCodes.InvalidAccountNameOrPassword,
                "reopening the database does not restore the first-login bypass");
            Check(LoginAs(config with { AutoRegister = true }, repo, "fresh@example.com", "s3cret-pass").ReturnCode == 0
                && repo.FindAccount("fresh@example.com") is { Level: AccountLevel.Player } made
                && made.Id != SqliteCharacterRepository.LocalAccountId,
                "enabled auto-registration creates an ordinary player account instead of claiming the local admin");
            Check(repo.FindAccount(SqliteCharacterRepository.LocalAccountId) is { Name: null, PasswordHash: null },
                "the empty legacy account remains unclaimed");
        }
        Directory.Delete(dir, true);
    }

    private static void Redaction()
    {
        var login = TestLogin("alice", "hunter2-secret");
        login.aiz = "203.0.113.7";
        login.u1 = "device-guid";
        var text = MessageDump.Describe(login);
        Check(!text.Contains("hunter2") && text.Contains("[7] uz = <redacted len 14>") && !text.Contains("203.0.113.7") && !text.Contains("device-guid")
              && text.Contains("\"alice\""), "Describe(message): keys 5, 7 and 9 redacted, the name shown");
        var raw = MessageDump.Describe(Request(login, 1).Parameters);
        Check(!raw.Contains("hunter2") && raw.Contains("[7] = <redacted len 14>"), "Describe(parameters): key 7 redacted by the op code in key 253");
        var create = MessageDump.Describe(new CreateAccountRequest { hp = "a@b.cc", hq = "hunter2-secret", hu = "{\"gpu\":1}" });
        Check(!create.Contains("hunter2") && !create.Contains("gpu") && create.Contains("a@b.cc"), "CreateAccount: keys 7 and 11 redacted");

        var commands = CommandRegistry.Default;
        Check(commands.Redact(".account create bob hunter2-secret") == ".account create bob <redacted>"
              && commands.Redact("account password bob \"two words\"") == "account password bob <redacted>"
              && commands.Redact(".item add T4_ORE 5") == ".item add T4_ORE 5", "logged command lines hide the password arguments");
        var dir = NewDir();
        using var repo = Repository(dir);
        var world = new World(new ServerConfig(), repo, Client.Items) { Sync = new object(), Clock = new ManualClock() };
        var replies = new List<(string Text, bool Error)>();
        commands.Execute(new CommandContext(world, null, AccountLevel.Admin, (t, e) => replies.Add((t, e))), ".account create bob hunter2-secret");
        Check(replies is [(var chat, true)] && chat.Contains("console") && repo.FindAccount("bob") is null, "from chat (even as Admin) the account commands are refused");
        replies.Clear();
        commands.ExecuteConsole(world, ".account create bob hunter2-secret", (t, e) => replies.Add((t, e)));
        commands.ExecuteConsole(world, ".account create carl hunter2-secret extra", (t, e) => replies.Add((t, e)));
        Check(replies.Count == 2 && !replies[0].Error && replies[1].Error && replies.All(r => !r.Text.Contains("hunter2") && !r.Text.Contains("extra"))
              && repo.FindAccount("bob") is { PasswordHash: not null }, "on the console it runs; no reply echoes the password");
        replies.Clear();
        commands.ExecuteConsole(world, ".account list", (t, e) => replies.Add((t, e)));
        Check(replies is [(var list, false)] && list.Contains("'bob'") && !list.Contains("pbkdf2"), "the list never shows a hash");
        world.Dispose();
    }

    private static void Check(bool cond, string what) => AlbionTests.Check(cond, what);
}
