using AlbionLocal.Server.Game;

namespace AlbionLocal.Server.Persistence;

/// <summary>What an account may do (TC RBAC reduced to three levels, ARCHITECTURE.md §6.6); GM commands check it.</summary>
public enum AccountLevel { Player, GameMaster, Admin }

/// <summary>
/// A saved account (Login key 51 is its id). Since stage 7 one per login name (Login key 6, the first text field of the
/// login screen, c33.i 0x180a90a40); <see cref="Name"/> null = the local account of the single-account server before,
/// not claimed by a login name yet. Since the real accounts (analysis/work/accounts) it has a password and the state of
/// the login checks: wrong passwords in a row, a lock after too many, a ban.
/// </summary>
public sealed record Account(Guid Id, AccountLevel Level)
{
    /// <summary>Stable legacy account identity, shared by all persistence backends and the login protocol.</summary>
    public static readonly Guid LocalId = new("6c6f6361-6c00-0000-0000-000000000001");

    /// <summary>The login key: the name as typed at registration or first login, trimmed and lower case (<see cref="KeyOf"/>).</summary>
    public string? Name { get; init; }

    /// <summary>The name as it was typed (trimmed): Login key 64 of the refusals, the console's list.</summary>
    public string? DisplayName { get; init; }

    /// <summary>The password's stored form (<see cref="Passwords.Hash"/>); null = none yet: the next successful login sets the one typed.</summary>
    public string? PasswordHash { get; init; }

    /// <summary>Wrong passwords in a row since the last successful login or lock.</summary>
    public int FailedLogins { get; init; }

    /// <summary>Until when (UTC) logins are refused after too many wrong passwords (RC 210); null = not locked.</summary>
    public DateTime? LockedUntil { get; init; }

    /// <summary>Until when (UTC) the account is banned (RC 205); null = not banned, <see cref="DateTime.MaxValue"/> = for good.</summary>
    public DateTime? BannedUntil { get; init; }

    /// <summary>The ban's reason as the client shows it (Login key 54); "" = none given.</summary>
    public string BanReason { get; init; } = "";

    /// <summary>When the account was made (UTC; for accounts of before the real accounts, when that schema step ran).</summary>
    public DateTime Created { get; init; }

    /// <summary>The name to show: as typed, else the login key, else "(local)".</summary>
    public string Shown => DisplayName ?? Name ?? "(local)";

    /// <summary>The login key of a typed name: trimmed, lower case (invariant culture), so names compare ignoring case.</summary>
    public static string KeyOf(string name) => name.Trim().ToLowerInvariant();

    /// <summary>Never the password hash: an account in a log line is its id and name.</summary>
    public override string ToString() => $"account {Id} '{Shown}'";
}

/// <summary>
/// The saved accounts and their characters (TC CharacterDatabase, ARCHITECTURE.md §5.3). Thread-safe: the repository
/// has its own lock, and characters go in and out as copies (<see cref="Character.Clone"/>), so the login reading
/// the character list never sees a character the game is changing. The game saves through <see cref="SaveScheduler"/>
/// (snapshot under the world lock, write on its writer thread); the login creates and deletes directly.
/// </summary>
public interface ICharacterRepository : IDisposable
{
    /// <summary>
    /// The local account: the only one until stage 7, holding every character saved before it (the default character
    /// too); its name and level as when the repository was opened. A character created without an account goes into it.
    /// </summary>
    Account Account { get; }

    /// <summary>The account with id <paramref name="id"/>; null = none.</summary>
    Account? FindAccount(Guid id);

    /// <summary>The account of login name <paramref name="name"/> (<see cref="Account.KeyOf"/>: trimmed, ignoring case); null = none.</summary>
    Account? FindAccount(string name);

    /// <summary>All accounts in creation order (the local one first).</summary>
    IReadOnlyList<Account> Accounts();

    /// <summary>
    /// A new, empty account for login name <paramref name="name"/> (key <see cref="Account.KeyOf"/>, shown as typed) with
    /// <paramref name="passwordHash"/> (null = none: the first login sets it) at <paramref name="level"/>; null (nothing
    /// stored) when the name is taken.
    /// </summary>
    Account? CreateAccount(string name, string? passwordHash, AccountLevel level);

    /// <summary>
    /// Gives the local account (the characters of before stage 7) the login name <paramref name="name"/> when it has none
    /// yet; null when it has one already or another account has that name.
    /// </summary>
    Account? ClaimLocalAccount(string name);

    /// <summary>
    /// Stores the account's password, login check state, ban and level (id, name and creation time stay); false when it no
    /// longer exists.
    /// </summary>
    bool UpdateAccount(Account account);

    /// <summary>
    /// Stores only the account's level (the console's .account level), so a login check written meanwhile (wrong passwords,
    /// a lock) is not put back; false when it no longer exists.
    /// </summary>
    bool SetAccountLevel(Guid id, AccountLevel level);

    /// <summary>
    /// Deletes the account with its characters (their items and progression with them) in one transaction. The local
    /// account is not removed but emptied: no characters, no name, no password, so a later first login takes it as new.
    /// False when there is no such account.
    /// </summary>
    bool DeleteAccount(Guid id);

    /// <summary>All characters of all accounts in creation order.</summary>
    IReadOnlyList<Character> List();

    /// <summary>The characters of <paramref name="account"/> in creation order (the login's character list).</summary>
    IReadOnlyList<Character> List(Guid account);

    Character? Find(Guid id);

    /// <summary>By name, ignoring case (names are unique that way, over all accounts).</summary>
    Character? Find(string name);

    /// <summary>Stores a new character in its <see cref="Character.Account"/>; false (nothing stored) when its id or name is taken.</summary>
    bool Create(Character c);

    /// <summary>
    /// <see cref="Create"/> for many in one transaction (the one-time import of characters.json): all or nothing,
    /// except that a character whose id or name is taken is skipped. Returns how many were stored.
    /// </summary>
    int Import(IEnumerable<Character> characters);

    /// <summary>
    /// Replaces the saved record (character, items, progression) in one transaction; an exception leaves the previous
    /// record as it was. False (nothing stored) when the character no longer exists: a deleted one is not brought back.
    /// </summary>
    bool Save(Character c);

    /// <summary>
    /// <see cref="Save"/> for all of <paramref name="cs"/> in one transaction (a trade: both characters or neither, stageJ trade SPEC
    /// §2.5); an exception leaves every previous record as it was. A deleted character is skipped. Returns how many were stored.
    /// </summary>
    int SaveMany(IReadOnlyList<Character> cs);

    /// <summary>Deletes the character with its items and progression; false when there is none of that name.</summary>
    bool Delete(string name);
}
