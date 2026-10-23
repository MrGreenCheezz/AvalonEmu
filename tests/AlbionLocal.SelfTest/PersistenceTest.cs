using System.Text.Json;
using AlbionLocal.Server.Content;
using AlbionLocal.Server.Game;
using AlbionLocal.Server.Persistence;
using Microsoft.Data.Sqlite;
using static AlbionLocal.SelfTest.AlbionTests;

namespace AlbionLocal.SelfTest;

/// <summary>The SQLite character repository and the one-time import of characters.json, on temporary files.</summary>
public static class PersistenceTest
{
    public static IEnumerable<(string, Action)> All =>
    [
        ("persistence: native SQLite is at least 3.50.2 (CVE-2025-6965)", NativeVersion),
        ("persistence: characters round trip through a new repository, names unique, deleted stays deleted", RoundTrip),
        ("persistence: characters.json imported once (bare item names too) and renamed", Import),
        ("persistence: a save that fails halfway leaves the previous record", AbortedSave),
        ("persistence: a failed write stays queued for a join and is retried", FailedWrite),
        ("persistence: a link merged while its group is being written stays for the member not yet written", LinkDuringWrite),
    ];

    private static void NativeVersion()
    {
        using var connection = new SqliteConnection("Data Source=:memory:");
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT sqlite_version()";
        var actual = (string)command.ExecuteScalar()!;
        Check(Version.TryParse(actual, out var version) && version >= new Version(3, 50, 2),
            $"loaded SQLite {actual}; CVE-2025-6965 requires SQLite >= 3.50.2");
        Console.WriteLine($"      native SQLite {actual}");
    }

    /// <summary>Every saved field, compared as JSON.</summary>
    private static string Json(Character c) => JsonSerializer.Serialize(c);

    private static void RoundTrip()
    {
        var dir = NewDir();
        var path = Path.Combine(dir, "albionlocal.db");
        var a = StarterKit.Default.NewCharacter("Alpha");
        a.Template = 3;
        a.PlayedMilliseconds = 123456789;
        a.Appearance = 2;
        a.Customization = [1, 2, 3];
        a.Clothes = [7];
        a.Cluster = "1234";
        a.PositionX = 1.5f;
        a.PositionY = -2.25f;
        a.Equipment[0] = new InventoryItem("T4_MAIN_SWORD") { Quality = 3, Durability = 12345, Active = ["HEROICSTRIKE2", null, "MIGHTYBLOW"], Passive = [] };
        a.Inventory = [null, new("T4_ORE", 7), null, new("T4_WOOD")];
        a.Progression = new Progression { Completed = [5, 1], Levels = new() { [9] = 2, [3] = 100 } };
        // The schema v2 columns (step 13): facing, cluster history, a pending transfer.
        a.Direction = 88.5679f;
        a.PreviousCluster = "0000";
        a.LastLeftAt = 639276397632025170;
        a.LastCity = "0007";
        a.PendingTransfer = new PendingTransfer("0000", "1234", 1.5f, -2.25f, 88.5679f, 639276397632025170, "exit:399399b1-0000-0000-0000-000000000000");
        var b = StarterKit.Default.NewCharacter("Beta"); // no progression: everything unlocked
        using (var repo = new SqliteCharacterRepository(path))
        {
            Check(repo.Create(a) && repo.Create(b) && !repo.Create(StarterKit.Default.NewCharacter("ALPHA")), "names are unique ignoring case");
            Check(repo.Account is { Level: AccountLevel.Admin, Name: null, PasswordHash: null } local && local.Id == SqliteCharacterRepository.LocalAccountId, "the local account is an admin, unclaimed, without a password");
        }
        using (var repo = new SqliteCharacterRepository(path))
        {
            Check(repo.List() is [{ Name: "Alpha" }, { Name: "Beta" }], "creation order");
            Check(repo.Find("alpha") is { } a2 && Json(a2) == Json(a), "every field comes back: items, durability, spells, progression");
            Check(repo.Find(b.Id) is { Progression: null } b2 && Json(b2) == Json(b), "no progression stays null (unlock-all)");
            Check(repo.Delete("Alpha") && repo.Find(a.Id) is null && !repo.Save(a) && repo.List() is [{ Name: "Beta" }],
                  "a deleted character is not brought back by a later save");
        }

        // A file of schema v1 (step 7, as data/albionlocal.db is before step 13) gets the v2 columns with their defaults.
        var v1 = Path.Combine(dir, "v1.db");
        using (var db = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = v1, Pooling = false }.ToString()))
        {
            db.Open();
            using var create = db.CreateCommand();
            create.CommandText = """
                CREATE TABLE accounts (id TEXT PRIMARY KEY, level INTEGER NOT NULL);
                CREATE TABLE characters (id TEXT PRIMARY KEY, account TEXT NOT NULL REFERENCES accounts (id), name TEXT NOT NULL UNIQUE COLLATE NOCASE,
                    template INTEGER NOT NULL, appearance INTEGER NOT NULL, customization TEXT NOT NULL, clothes TEXT NOT NULL, cluster TEXT NOT NULL,
                    position_x REAL NOT NULL, position_y REAL NOT NULL, created INTEGER NOT NULL, equipment_container TEXT NOT NULL, inventory_container TEXT NOT NULL);
                CREATE TABLE items (character TEXT NOT NULL, container INTEGER NOT NULL, slot INTEGER NOT NULL, uniquename TEXT NOT NULL, count INTEGER NOT NULL,
                    quality INTEGER NOT NULL, durability INTEGER, active TEXT, passive TEXT, PRIMARY KEY (character, container, slot)) WITHOUT ROWID;
                CREATE TABLE progression (character TEXT PRIMARY KEY, completed TEXT NOT NULL, levels TEXT NOT NULL);
                INSERT INTO accounts VALUES ('6c6f6361-6c00-0000-0000-000000000001', 2);
                INSERT INTO characters VALUES ('11111111-2222-3333-4444-555555555555', '6c6f6361-6c00-0000-0000-000000000001', 'Old', 0, 0, '[]', '[]',
                    '1000', 3.5, -1, 0, '11111111-2222-3333-4444-000000000001', '11111111-2222-3333-4444-000000000002');
                PRAGMA user_version = 1;
                """;
            create.ExecuteNonQuery();
        }
        using (var repo = new SqliteCharacterRepository(v1))
            Check(repo.Find("Old") is { Cluster: "1000", PositionX: 3.5f, Direction: 0, PreviousCluster: "", LastLeftAt: 0, LastCity: "", PendingTransfer: null },
                  "a v1 file is migrated: its characters load, the new columns at their defaults");
        try { Directory.Delete(dir, true); } catch (IOException) { }
    }

    private static void Import()
    {
        var dir = NewDir();
        var json = Path.Combine(dir, "characters.json");
        // As the server before step 7 wrote it, plus the oldest forms: equipment as bare names, an entry without items.
        File.WriteAllText(json, """
            [
              { "Id": "11111111-2222-3333-4444-555555555555", "Name": "Old", "Cluster": "1000", "PositionX": 3.5, "PositionY": -1,
                "Created": "2026-10-15T01:19:28.5246623Z",
                "Equipment": ["T4_MAIN_SWORD", null, { "Name": "T4_HEAD_PLATE_SET1", "Count": 1, "Quality": 2, "Durability": 500, "Active": ["X", null], "Passive": null }],
                "Inventory": [{ "Name": "T4_ORE", "Count": 10 }, null, "T4_WOOD"],
                "Progression": { "Completed": [1, 2], "Levels": { "4": 100 } } },
              { "Name": "Bare" }
            ]
            """);
        using var repo = new SqliteCharacterRepository(Path.Combine(dir, "albionlocal.db"));
        Check(JsonSaveImport.Run(repo, json, StarterKit.Default) == 2, "both imported");
        Check(!File.Exists(json) && File.Exists(json + ".migrated"), "the file is renamed, not deleted");
        var old = repo.Find("Old")!;
        Check(old is { Cluster: "1000", PositionX: 3.5f, PositionY: -1 } && old.Id == Guid.Parse("11111111-2222-3333-4444-555555555555")
              && old.Created == new DateTime(2026, 10, 15, 1, 19, 28, DateTimeKind.Utc).AddTicks(5246623), "character fields kept");
        Check(old.Equipment is [{ Name: "T4_MAIN_SWORD", Count: 1, Quality: 1, Durability: null, Active: null }, null,
                  { Name: "T4_HEAD_PLATE_SET1", Quality: 2, Durability: 500, Active: ["X", null], Passive: null }, ..]
              && old.Inventory is [{ Name: "T4_ORE", Count: 10 }, null, { Name: "T4_WOOD", Count: 1 }], "items kept, bare names too");
        Check(old.Progression is { Completed: [1, 2] } p && p.Levels.Count == 1 && p.Levels[4] == 100, "progression kept");
        Check(repo.Find("Bare") is { Progression: null } bare && bare.Equipment[0]?.Name == "T4_MAIN_SWORD"
              && bare.Inventory is [{ Name: "T4_ORE", Count: 10 }, { Name: "T4_WOOD", Count: 10 }],
              "an entry without items gets the starter kit, one without progression stays unlock-all");
        File.WriteAllText(json, "[]");
        Check(JsonSaveImport.Run(repo, json, StarterKit.Default) == 0 && File.Exists(json), "only once: a repository with characters imports nothing");
        repo.Dispose();
        try { Directory.Delete(dir, true); } catch (IOException) { }
    }

    private static void AbortedSave()
    {
        var dir = NewDir();
        var path = Path.Combine(dir, "albionlocal.db");
        var c = StarterKit.Default.NewCharacter("Gamma");
        using (var repo = new SqliteCharacterRepository(path))
        {
            repo.Create(c);
            c.PositionX = 5;
            c.Inventory = [new("T4_ORE", 1)];
            Check(repo.Save(c), "first save");
        }
        var before = Json(c);
        using (var repo = new SqliteCharacterRepository(path))
        {
            c.PositionX = 99;
            c.Cluster = "9999";
            c.Progression = new Progression();
            // The character row is updated and the first item written before the nameless one breaks NOT NULL.
            c.Inventory = [new("T4_ORE", 2), new(null!, 1), new("T4_WOOD", 3)];
            try { repo.Save(c); Check(false, "a nameless item was saved"); }
            catch (SqliteException) { }
            Check(Json(repo.Find(c.Id)!) == before, "the previous record is intact");
        }
        using (var repo = new SqliteCharacterRepository(path))
            Check(Json(repo.Find(c.Id)!) == before, "also for a new connection to the file");
        try { Directory.Delete(dir, true); } catch (IOException) { }
    }

    /// <summary>A disconnect save whose write fails (database locked, disk full): a rejoin still gets it, a later try writes it.</summary>
    private static void FailedWrite()
    {
        var dir = NewDir();
        var db = new SqliteCharacterRepository(Path.Combine(dir, "albionlocal.db"));
        var c = StarterKit.Default.NewCharacter("Delta");
        db.Create(c);
        var repo = new FlakyRepository(db) { Fail = true };
        var saves = new SaveScheduler(repo);
        c.PositionX = 42;
        saves.SaveNow(c);
        saves.Flush(); // back after the failed try
        Check(db.Find(c.Id)!.PositionX == 0 && saves.Load(c.Id)!.PositionX == 42, "the failed snapshot is kept: a join loads it");
        repo.Fail = false;
        saves.Dispose(); // the retry, at the latest when stopping
        Check(db.Find(c.Id)!.PositionX == 42, "and a retry writes it");
        db.Dispose();
        try { Directory.Delete(dir, true); } catch (IOException) { }
    }

    /// <summary>
    /// X queued, then SaveTogether(A, X): the writer stores [X, A]; while it does, SaveTogether(A, B) merges the link and replaces A's
    /// snapshot. Only X was written, so A keeps its link: the next write stores A and B in one transaction.
    /// </summary>
    private static void LinkDuringWrite()
    {
        var dir = NewDir();
        var db = new SqliteCharacterRepository(Path.Combine(dir, "albionlocal.db"));
        var (z, x, a, b) = (StarterKit.Default.NewCharacter("Zeta"), StarterKit.Default.NewCharacter("Xi"),
            StarterKit.Default.NewCharacter("Alpha"), StarterKit.Default.NewCharacter("Beta"));
        foreach (var c in new[] { z, x, a, b }) db.Create(c);
        var gate = new SemaphoreSlim(0);
        var repo = new FlakyRepository(db) { Gate = gate };
        var saves = new SaveScheduler(repo);
        saves.SaveNow(z);
        Check(repo.Entered.Wait(10_000), "the writer holds Z");
        saves.SaveNow(x);
        saves.SaveTogether(a, x);
        gate.Release();
        Check(repo.Entered.Wait(10_000), "the writer holds [X, A]");
        a.Silver = 7;
        saves.SaveTogether(a, b);
        gate.Release();
        Check(repo.Entered.Wait(10_000), "the next write");
        repo.Gate = null;
        gate.Release(10);
        saves.Dispose();
        Guid[] last;
        lock (repo.Joint) last = repo.Joint[^1];
        Check(last.Length == 2 && last.Contains(a.Id) && last.Contains(b.Id), "A's newer snapshot is written with B");
        Check(db.Find(a.Id)!.Silver == 7, "and stored");
        db.Dispose();
        try { Directory.Delete(dir, true); } catch (IOException) { }
    }

    /// <summary>A repository whose saves throw while <see cref="Fail"/> is set; it records the ids of each joint save (<see cref="SaveMany"/>).</summary>
    internal sealed class FlakyRepository(ICharacterRepository inner) : ICharacterRepository
    {
        public volatile bool Fail;
        public List<Guid[]> Joint { get; } = [];
        public Account Account => inner.Account;
        public Account? FindAccount(Guid id) => inner.FindAccount(id);
        public Account? FindAccount(string name) => inner.FindAccount(name);
        public IReadOnlyList<Account> Accounts() => inner.Accounts();
        public Account? CreateAccount(string name, string? passwordHash, AccountLevel level) => inner.CreateAccount(name, passwordHash, level);
        public Account? ClaimLocalAccount(string name) => inner.ClaimLocalAccount(name);
        public bool UpdateAccount(Account account) => inner.UpdateAccount(account);
        public bool SetAccountLevel(Guid id, AccountLevel level) => inner.SetAccountLevel(id, level);
        public bool DeleteAccount(Guid id) => inner.DeleteAccount(id);
        public IReadOnlyList<Character> List() => inner.List();
        public IReadOnlyList<Character> List(Guid account) => inner.List(account);
        public Character? Find(Guid id) => inner.Find(id);
        public Character? Find(string name) => inner.Find(name);
        public bool Create(Character c) => inner.Create(c);
        public int Import(IEnumerable<Character> characters) => inner.Import(characters);
        /// <summary>When set, each save signals <see cref="Entered"/> and then waits for one release of <see cref="Gate"/>.</summary>
        public SemaphoreSlim? Gate;
        public readonly SemaphoreSlim Entered = new(0);
        private void Hold()
        {
            if (Gate is not { } gate) return;
            Entered.Release();
            gate.Wait();
        }
        public bool Save(Character c)
        {
            Hold();
            return Fail ? throw new IOException("disk full (test)") : inner.Save(c);
        }
        public int SaveMany(IReadOnlyList<Character> cs)
        {
            Hold();
            if (Fail) throw new IOException("disk full (test)");
            lock (Joint) Joint.Add([.. cs.Select(c => c.Id)]);
            return inner.SaveMany(cs);
        }
        public bool Delete(string name) => inner.Delete(name);
        public void Dispose() { }
    }

    private static void Check(bool cond, string what) => AlbionTests.Check(cond, what);
}
