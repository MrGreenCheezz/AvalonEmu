using System.Net;
using AlbionLocal.Photon;
using AlbionLocal.Server.Game;
using AlbionLocal.Server.Persistence;
using AlbionLocal.Server.Protocol;

namespace AlbionLocal.Server.Network;

public sealed partial class LoginSession
{
    /// <summary>
    /// Status/ping peers (c35/c36, AppId "Albion.StatusServer") connect to the status address of
    /// each region and send one empty request. c35.xk 0x180a95d90 unboxes event param 1 as a Byte (another type
    /// throws) and stores 1, 2 or 3 as the server state (c36+0x148, enum c36.a; other values keep the old one).
    /// ServerSelectionButton.UpdateFromCurrentServerAddress 0x180eaf290 (the SetStatus logic, 0x180eaf0d0) shows the
    /// region normally only for 3: any other value turns on MaintenanceFlagParent and the GreyTextureShader on the
    /// ContinentImage, the StatusDot takes its colour from dr2.a(state), SetSelected 0x180eaef30 uses the selected colours
    /// only for 3. So 3 = online; the 1 sent before showed the region greyed out as in maintenance. Official answer
    /// (capture 002835, all three regions, both logins): event 1 {1: byte 3, 252: short 1}, 11 bytes,
    /// the same keys and wire types as this one. Ping itself is the Photon round-trip time.
    /// These peers never log in (captures: their only operation is that request, op 1 without key 253 = Unused).
    /// </summary>
    [Handles(OperationCodes.Unused)]
    private AlbionResponse? StatusQuery(UnusedRequest req)
    {
        Log.Info(Name, $"status query from {Peer.AppId} peer {Peer.PeerId}");
        Peer.SendEvent(new Photon.Serialization.EventData(AlbionSerializer.PhotonEventCode, new()
        {
            [ReservedKeys.EventCode] = (short)1,
            [1] = OnlineStatus,
        }));
        return null;
    }

    /// <summary>The status event's key 1 for a server that is up (c36.a; 1 and 2 both get the maintenance flag and the grey continent).</summary>
    public const byte OnlineStatus = 3;

    /// <summary>
    /// First operation of every login connection; the session is Authed afterwards. Key 6 is the login name (the first
    /// field; c33.i 0x180a90a40 stores its first string argument at xn+0x28), key 7 the password as plain text (the second
    /// field; the client never hashes it, LoginDialog.LoginFromInputFields trims both), and both are trimmed here too. The
    /// checks (analysis/work/accounts, server spec §2 with its verifier's corrections, <see cref="Authenticate"/>) refuse
    /// with the client's RcOpLogin codes (<see cref="LoginCodes"/>); a successful Login closes the account's older
    /// connections (a client that lost its game connection logs in again from scratch) and sends the account's characters.
    /// </summary>
    [Handles(OperationCodes.Login)]
    private LoginResponse Login(LoginRequest req)
    {
        Log.Info(Name, $"login from {Peer.EndPoint}: client {req.aiu}.{req.aiv}.{req.aiw} build {req.aiy}");
        Account account;
        try
        {
            if (Authenticate(req) is not { } ok)
            {
                // A refused Login on an Authed connection ends its character list access and its place among the account's
                // connections (online, closed by the account's next Login, the Join's login check).
                Status = SessionStatus.Connected;
                World?.Accounts.Detach(_account.Id, this);
                _account = characters.Account;
                return _refusal!;
            }
            account = ok;
        }
        catch (Exception e)
        {
            // A database error and the like: the generic error (206 has no case of its own in HandleResponseLogin, so the
            // client shows "@LOGINGUI_GENERIC_ERROR (206)" and clears its auto-login).
            Log.Error(Name, $"login failed: {e.Message}");
            return Refuse(LoginCodes.LoginServiceUnavailable, "login service unavailable");
        }
        World?.Accounts.Detach(_account.Id, this); // a second Login on this connection (another name)
        _account = account;
        if (World is { } world)
        {
            // Not the chat connection: it belongs to the Select's token, and a re-Login of the same client keeps it (stageJ chat SPEC §1.1;
            // the next Select moves it to its character, ChatHub.Selected, and another client's register replaces it, ChatHub.Registered).
            var older = world.Accounts.Sessions(account.Id).Where(s => s != this && s is not ChatSession).ToList();
            if (older.Count > 0) Log.Info(Name, $"{account} logs in again: closing its {older.Count} older connection(s)");
            foreach (var s in older) s.Close($"{account} logged in on another connection");
            world.Accounts.Attach(account.Id, this);
        }
        var r = new LoginResponse
        {
            // ajb (VersionedOperation response): echo the client's version.
            bm1 = req.aiu, bm2 = req.aiv, bm3 = req.aiw, bm4 = req.aix, bm5 = req.aiy,
            bej = _account.Id,          // [51] stored in PhotonClient (account id; Join key 0)
            bep = LoginWorld.Key57,     // [57] server session; compared on reconnect ("server was restarted"); seeds the open-world layout
            bfb = LoginWorld.Flags,     // [79] event flags of the layout (none: an empty array, as FillEmpty sent it before)
            bfh = true,                 // [85] when false during a reconnect the client auto-selects a character
            bfv = LoginWorld.Features(), // [99] dew.a feature table, one entry per Features value
        };
        FillCharacterList(r, characters.List(_account.Id), World?.Guilds, Systems.Premium.ListDays(config), World);
        r.FillEmpty();
        // [78] GameTimeStamp time scaling, same as Join key 70: c3d.cl(ahw) 0x180a5bae0 passes it to
        // GameTimeStamp.a(long[]), which needs exactly 5 values. Left out = unscaled real time.
        r.bfa = null;
        // Must be a long or the client throws. Like the official server: the server start (= key 57),
        // which the client takes as the base of compressed GameTimeStamps (ce.e_1).
        r.Extra[ReservedKeys.ServerTime] = GameTime.StartTicks;
        // The login session starts: Join key 102 for each of its Joins, and their count (key 103) from 1 again.
        World?.Accounts.Login(_account.Id, DateTime.UtcNow.Ticks);
        Status = SessionStatus.Authed;
        return r;
    }

    /// <summary>The refusal <see cref="Authenticate"/> decided on (null after a success).</summary>
    private LoginResponse? _refusal;

    /// <summary>The clock of the lock and ban checks (UTC); the self-test moves it.</summary>
    public Func<DateTime> UtcNow { get; init; } = () => DateTime.UtcNow;

    /// <summary>
    /// The account a Login opens, or null with <see cref="_refusal"/> set. In order:
    /// <list type="number">
    /// <item>Accounts:Maintenance → 204 (the client's maintenance message).</item>
    /// <item>A Steam or console login (key 8, 28, 30 or 31 set) → 201: only name and password are supported.</item>
    /// <item>More than <see cref="LoginThrottle.Limit"/> password checks a minute from the address → 206 without one (each
    /// hash runs under the world's lock).</item>
    /// <item>An empty name or password → 201.</item>
    /// <item>A name without an account: the local account of before stage 7 when it has no name yet and Accounts:LegacyName
    /// is this name, or is empty and the Login comes from this PC (loopback: a stranger on the network never takes it); else with Accounts:AutoRegister a new account with this password; else 201 after a dummy
    /// hash (the same answer and about the same time as a wrong password: the client has no "no such account" message).</item>
    /// <item>Locked (too many wrong passwords) → 210 with key 56 = the lock's remaining TimeSpan ticks and key 64.</item>
    /// <item>No password yet (an account of before the real accounts): the one typed becomes its password. Else a wrong
    /// password → 201, and the Accounts:MaxFailedLogins-th in a row locks the account for Accounts:LockMinutes → 210.</item>
    /// <item>Banned → 205 with key 52 = the ban's end (UTC DateTime ticks), 54 = the reason, 64. Checked after the password,
    /// so a ban is told only to whoever knows it.</item>
    /// </list>
    /// Keys 64 must be non-null on 205 and 210 (the client's handlers throw otherwise): the name as typed at registration.
    /// </summary>
    private Account? Authenticate(LoginRequest req)
    {
        _refusal = null;
        var name = (req.uy ?? "").Trim();
        var password = (req.uz ?? "").Trim();
        if (config.Maintenance)
            return Refused(LoginCodes.InsufficientAccessLevel, "server in maintenance", "maintenance (Accounts:Maintenance)");
        if (!string.IsNullOrEmpty(req.u0) || !string.IsNullOrEmpty(req.vk) || !string.IsNullOrEmpty(req.vm) || !string.IsNullOrEmpty(req.vn))
            return Refused(LoginCodes.InvalidAccountNameOrPassword, "only name and password logins", "a Steam or console login (not supported)");
        if (name.Length == 0 || password.Length == 0)
            return Refused(LoginCodes.InvalidAccountNameOrPassword, "name and password needed", "no name or no password");
        if (!Throttle.Allow(Peer.EndPoint.Address, UtcNow()))
            return Refused(LoginCodes.LoginServiceUnavailable, "too many attempts", $"too many password checks from {Peer.EndPoint.Address} (a minute's limit)");

        var now = UtcNow();
        var account = characters.FindAccount(name);
        if (account is null)
        {
            if (characters.Account.Name is null
                && (config.LegacyAccountName.Length == 0 ? IPAddress.IsLoopback(Peer.EndPoint.Address)
                    : Persistence.Account.KeyOf(config.LegacyAccountName) == Persistence.Account.KeyOf(name))
                && characters.ClaimLocalAccount(name) is { } claimed)
            {
                Log.Info(Name, $"{claimed} takes over the local account with its {characters.List(claimed.Id).Count} character(s) of before the upgrade ("
                    + (config.LegacyAccountName.Length == 0 ? "the first name to log in on this PC; Accounts:LegacyName is empty)" : "Accounts:LegacyName)"));
                account = claimed;
            }
            else if (config.AutoRegister)
            {
                if (characters.CreateAccount(name, Passwords.Hash(password), config.DefaultAccountLevel) is not { } made)
                    return Refused(LoginCodes.LoginServiceUnavailable, "account not created", "the new account could not be stored");
                Log.Info(Name, $"{made} is new: an empty account with the password typed (Accounts:AutoRegister)");
                return made;
            }
            else
            {
                Passwords.Dummy(password);
                return Refused(LoginCodes.InvalidAccountNameOrPassword, "invalid name or password", "no account of that name (Accounts:AutoRegister is off)");
            }
        }
        var shown = account.DisplayName ?? name;
        if (account.LockedUntil is { } locked && locked > now)
            return Locked(account, locked - now);

        if (account.PasswordHash is null)
        {
            account = account with { PasswordHash = Passwords.Hash(password), FailedLogins = 0, LockedUntil = null };
            characters.UpdateAccount(account);
            Log.Info(Name, $"{account} had no password: the one typed at this login is its password now");
        }
        else if (!Passwords.Verify(password, account.PasswordHash))
        {
            var failed = account.FailedLogins + 1;
            if (config.MaxFailedLogins > 0 && failed >= config.MaxFailedLogins)
            {
                account = account with { FailedLogins = 0, LockedUntil = now + config.LockTime };
                characters.UpdateAccount(account);
                Log.Warn(Name, $"{account}: {failed} wrong passwords in a row, locked for {config.LockTime.TotalMinutes:0.#} min");
                return Locked(account, config.LockTime);
            }
            characters.UpdateAccount(account with { FailedLogins = failed, LockedUntil = null });
            return Refused(LoginCodes.InvalidAccountNameOrPassword, "invalid name or password", $"{account}: wrong password ({failed} in a row)");
        }
        else if (account.FailedLogins != 0 || account.LockedUntil is not null)
        {
            account = account with { FailedLogins = 0, LockedUntil = null };
            characters.UpdateAccount(account);
        }

        if (account.BannedUntil is { } banned && banned > now)
        {
            _refusal = Refuse(LoginCodes.AccountIsBanned, "account banned");
            _refusal.bek = banned.Ticks;        // [52] ban end, UTC DateTime ticks
            _refusal.bem = account.BanReason;   // [54] reason (dr2.k), "" = none
            _refusal.bew = shown;               // [64] must be non-null
            Log.Info(Name, $"{account}: login refused (205): banned until {(banned == DateTime.MaxValue ? "for good" : $"{banned:u}")}");
            return null;
        }
        Log.Info(Name, $"{account}: {characters.List(account.Id).Count} character(s)");
        return account;

        Account? Locked(Account a, TimeSpan left)
        {
            _refusal = Refuse(LoginCodes.BlockedByEnteringWrongPassword, "too many wrong passwords");
            _refusal.beo = left.Ticks;          // [56] how long the lock lasts, TimeSpan ticks
            _refusal.bew = a.DisplayName ?? name; // [64] must be non-null
            Log.Info(Name, $"{a}: login refused (210): locked for {left.TotalMinutes:0.#} more min");
            return null;
        }

        Account? Refused(short code, string debug, string why)
        {
            _refusal = Refuse(code, debug);
            Log.Info(Name, $"login refused ({code}): {why}");
            return null;
        }
    }

    /// <summary>
    /// A refused Login: the ReturnCode and DebugMessage with the op code (and request id) only, none of the success
    /// payload; <see cref="Authenticate"/> adds the keys a code needs.
    /// </summary>
    private static LoginResponse Refuse(short code, string debug) => new() { ReturnCode = code, DebugMessage = debug };
}

/// <summary>
/// ReturnCodes of Login and CreateAccount as the client handles them (Albion/Common/Photon/RcOpLogin.cs;
/// LoginInProcessDialog.HandleResponseLogin 0x1816d63c0, CreatingAccountDialog.HandleResponseCreateAccount 0x1816cae10;
/// analysis/work/accounts with its verifier's corrections). Not used on purpose: 203 ACCOUNT_IN_USE (a second Login closes
/// the first one's connections instead), 211 (opens the verification-code screen), 213 ("email sent"), 230 (no message at
/// all), 171 (its message is never shown), 162 (ambiguous label).
/// </summary>
public static class LoginCodes
{
    /// <summary>Login 201: "@LOGINGUI_LOGIN_ERROR_INCORRECT_CREDENTIALS": unknown name and wrong password alike; auto-login stays on.</summary>
    public const short InvalidAccountNameOrPassword = 201;

    /// <summary>Login 204 (InsufficientAccessLevel): the maintenance message, auto-login cleared.</summary>
    public const short InsufficientAccessLevel = 204;

    /// <summary>Login 205: HandleAccountIsBanned: "@LOGINGUI_LOGIN_ERROR_BANNED" with key 52 (end) and 54 (reason); key 64 required.</summary>
    public const short AccountIsBanned = 205;

    /// <summary>Login 206 (LoginServiceUnavailable): no case of its own, "@LOGINGUI_GENERIC_ERROR (206)", auto-login cleared.</summary>
    public const short LoginServiceUnavailable = 206;

    /// <summary>Login 210: "@LOGINGUI_LOGIN_ERROR_BLOCKED_BY_ENTERING_WRONG_PASSWORD" for key 56 (minutes, seconds); key 64 required.</summary>
    public const short BlockedByEnteringWrongPassword = 210;

    /// <summary>CreateAccount 161: "@LOGINGUI_CREATEACCOUNT_ERROR_NAME_ALREADY_TAKEN".</summary>
    public const short NameAlreadyTaken = 161;

    /// <summary>CreateAccount 163: the reasons of key 9 (<see cref="Persistence.PasswordProblem"/> flags).</summary>
    public const short ValidationErrors = 163;

    /// <summary>CreateAccount 165: account creation disabled (Accounts:Registration false).</summary>
    public const short CreationDisabled = 165;

    /// <summary>CreateAccount 167: invalid email, formatted with the email typed.</summary>
    public const short InvalidEmail = 167;
}
