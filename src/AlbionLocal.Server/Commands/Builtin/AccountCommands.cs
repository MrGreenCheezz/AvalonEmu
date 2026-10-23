using AlbionLocal.Server.Network;
using AlbionLocal.Server.Persistence;

namespace AlbionLocal.Server.Commands.Builtin;

/// <summary>
/// The accounts (analysis/work/accounts), from the server console only (<see cref="CommandAttribute.ConsoleOnly"/>, Admin):
/// list, create, set a password or the level, ban and unban, delete. Passwords are [Secret]: never echoed, never in a logged line.
/// Names are login names, ignoring case (<see cref="Account.KeyOf"/>).
/// </summary>
public static class AccountCommands
{
    [Command("account list", ConsoleOnly = true, Help = "the accounts: characters, online, password set, lock and ban")]
    public static void List(CommandContext ctx)
    {
        var repo = ctx.World.Characters;
        var now = DateTime.UtcNow;
        string Line(Account a)
        {
            var notes = new List<string> { $"{repo.List(a.Id).Count} character(s)", a.Level.ToString() };
            if (Online(ctx.World, a.Id)) notes.Add("online");
            if (a.PasswordHash is null) notes.Add("no password yet (the next login sets it)");
            if (a.LockedUntil is { } locked && locked > now) notes.Add($"locked {(locked - now).TotalMinutes:0.#} more min");
            if (a.BannedUntil is { } banned && banned > now)
                notes.Add($"banned {(banned == DateTime.MaxValue ? "for good" : $"until {banned:u}")}{(a.BanReason.Length > 0 ? $" ({a.BanReason})" : "")}");
            return $"'{a.Shown}' {a.Id}: {string.Join(", ", notes)}";
        }
        var accounts = repo.Accounts();
        ctx.Reply($"{accounts.Count} account(s):\n" + string.Join("\n", accounts.Select(Line)));
    }

    [Command("account create", ConsoleOnly = true, Help = "a new empty account (password: 8-72 ASCII characters, no space at either end)")]
    public static void Create(CommandContext ctx, string name, [Secret] string password)
    {
        if (Account.KeyOf(name).Length == 0) throw new CommandError("the name is empty");
        CheckPassword(password, name);
        var made = ctx.World.Characters.CreateAccount(name, Passwords.Hash(password), ctx.World.Config.DefaultAccountLevel)
            ?? throw new CommandError($"'{name.Trim()}' has an account already");
        ctx.Reply($"{made} created at level {made.Level} (Accounts:DefaultLevel)");
    }

    [Command("account password", ConsoleOnly = true, Help = "sets the account's password (and ends a lock after wrong passwords)")]
    public static void Password(CommandContext ctx, string name, [Secret] string password)
    {
        var account = Named(ctx, name);
        CheckPassword(password, name);
        ctx.World.Characters.UpdateAccount(account with { PasswordHash = Passwords.Hash(password), FailedLogins = 0, LockedUntil = null });
        ctx.Reply($"{account}: new password set (a client that remembers the old one will be refused)");
    }

    [Command("account level", ConsoleOnly = true, Help = "sets the account's level (Player, GameMaster, Admin): which GM commands its chat may run")]
    public static void SetLevel(CommandContext ctx, string name, AccountLevel level)
    {
        var account = Named(ctx, name);
        ctx.World.Characters.SetAccountLevel(account.Id, level); // only the level: a login meanwhile keeps its wrong-password count and lock
        ctx.Reply($"{account}: {account.Level} -> {level} (its next chat command runs at it)");
    }

    [Command("account ban", ConsoleOnly = true, Help = "bans the account for <minutes> (0 = for good) and closes its connections; the reason is shown at login")]
    public static void Ban(CommandContext ctx, string name, int minutes, Tail reason = default)
    {
        var account = Named(ctx, name);
        if (minutes < 0) throw new CommandError("minutes: 0 (for good) or more");
        var until = minutes == 0 ? DateTime.MaxValue : DateTime.UtcNow.AddMinutes(minutes);
        ctx.World.Characters.UpdateAccount(account with { BannedUntil = until, BanReason = reason.IsEmpty ? "" : reason.Text!.Trim() });
        var closed = Kick(ctx.World, account.Id, "the account is banned");
        ctx.Reply($"{account} banned {(minutes == 0 ? "for good" : $"for {minutes} min")}; {closed} connection(s) closed");
    }

    [Command("account unban", ConsoleOnly = true, Help = "lifts the account's ban and its lock after wrong passwords")]
    public static void Unban(CommandContext ctx, string name)
    {
        var account = Named(ctx, name);
        ctx.World.Characters.UpdateAccount(account with { BannedUntil = null, BanReason = "", FailedLogins = 0, LockedUntil = null });
        ctx.Reply($"{account}: no ban, no lock");
    }

    [Command("account delete", ConsoleOnly = true, Help = "deletes the account with all its characters (not while it is online)")]
    public static void Delete(CommandContext ctx, string name)
    {
        var account = Named(ctx, name);
        if (Online(ctx.World, account.Id)) throw new CommandError($"{account} is online: ban it first (closes its connections), then delete");
        var characters = ctx.World.Characters.List(account.Id);
        var count = characters.Count;
        ctx.World.Characters.DeleteAccount(account.Id); // one transaction: the characters are gone (or none is) when it returns
        foreach (var c in characters) ctx.World.Social.CharacterDeleted(c.Id); // off its friends' lists
        foreach (var c in characters) ctx.World.Guilds.CharacterDeleted(c.Id); // then out of its guild (a master's role passes on)
        ctx.Reply(account.Id == Account.LocalId
            ? $"{account}: its {count} character(s) deleted; it is the local account, so it stays, empty and without a name"
            : $"{account} deleted with its {count} character(s)");
    }

    private static Account Named(CommandContext ctx, string name) =>
        ctx.World.Characters.FindAccount(name) ?? throw new CommandError($"no account '{name.Trim()}' (.account list)");

    private static void CheckPassword(string password, string name)
    {
        if (Passwords.Check(password, name) is not PasswordProblem.None and var problems)
            throw new CommandError($"password not accepted: {problems} (8-72 printable ASCII characters, no space at either end, not the name)");
    }

    /// <summary>Whether the account has an open connection or a player in the world.</summary>
    internal static bool Online(World world, Guid account) =>
        world.Accounts.Sessions(account).Count > 0 || world.Maps.Maps.Any(m => m.Players.Values.Any(p => p.Character.Account == account));

    /// <summary>Closes every connection of the account (its login and game sessions, players in the world); how many.</summary>
    internal static int Kick(World world, Guid account, string why)
    {
        var sessions = world.Accounts.Sessions(account).ToHashSet();
        foreach (var p in world.Maps.Maps.SelectMany(m => m.Players.Values).Where(p => p.Character.Account == account).ToList())
            sessions.Add(p.Session);
        foreach (var s in sessions) s.Close(why);
        return sessions.Count;
    }
}
