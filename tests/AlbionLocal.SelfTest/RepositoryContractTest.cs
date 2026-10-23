using System.Net;
using System.Text.Json;
using AlbionLocal.Server;
using AlbionLocal.Server.Content;
using AlbionLocal.Server.Persistence;
using static AlbionLocal.SelfTest.AlbionTests;

namespace AlbionLocal.SelfTest;

/// <summary>Backend-independent behavior; new providers must run these checks as well as their own integration tests.</summary>
public static class RepositoryContractTest
{
    private static void Check(bool condition, string message)
    {
        if (!condition) throw new Exception("check failed: " + message);
    }

    public static IEnumerable<(string, Action)> All =>
    [
        ("repository contract: SQLite accounts, snapshots and reopen", () => RoundTrip(new SqliteRepositoryFactory())),
        ("repository contract: SQLite joint saves and market batches roll back", () => AtomicWrites(new SqliteRepositoryFactory())),
        ("repository composition: host uses the injected backend and releases it", HostBackend),
        ("repository composition: failed legacy import releases the backend", FailedImport),
    ];

    private static void RoundTrip(IGameRepositoryFactory factory)
    {
        var dir = NewDir();
        try
        {
            var config = new ServerConfig { SavesPath = dir, LocalAccountLevel = AccountLevel.Player };
            Guid accountId, characterId;
            using (var repository = factory.Open(config))
            {
                Check(repository.Account is { Level: AccountLevel.Player } local && local.Id == Account.LocalId, "stable local identity and configured level");
                var account = repository.CreateAccount(" Student ", "synthetic-test-value", AccountLevel.Player)!;
                accountId = account.Id;
                Check(repository.FindAccount("STUDENT")?.Id == accountId && repository.CreateAccount("student", null, AccountLevel.Admin) is null,
                    "account keys are normalized and unique");
                var character = StarterKit.Default.NewCharacter("ContractHero");
                character.Account = accountId;
                character.PositionX = 12.5f;
                characterId = character.Id;
                Check(repository.Create(character), "character created in its account");
                Check(!repository.Create(StarterKit.Default.NewCharacter("CONTRACTHERO")), "character names unique ignoring case");
                character.PositionX = 999;
                var snapshot = repository.Find(characterId)!;
                Check(snapshot.PositionX == 12.5f, "writes copy caller-owned objects");
                snapshot.PositionX = -999;
                Check(repository.Find(characterId)!.PositionX == 12.5f, "reads return independent snapshots");
            }
            using (var repository = factory.Open(config))
            {
                Check(repository.List(accountId) is [{ PositionX: 12.5f }] && repository.FindAccount(accountId)?.Name == "student",
                    "account ownership and position survive reopen");
                var saved = repository.Find(characterId)!;
                Check(repository.DeleteAccount(accountId) && repository.Find(characterId) is null && !repository.Save(saved),
                    "deleting an account removes its character; stale saves cannot recreate it");
            }
        }
        finally { Directory.Delete(dir, true); }
    }

    private static void AtomicWrites(IGameRepositoryFactory factory)
    {
        var dir = NewDir();
        try
        {
            using var repository = factory.Open(new ServerConfig { SavesPath = dir });
            var a = StarterKit.Default.NewCharacter("AtomicA");
            var b = StarterKit.Default.NewCharacter("AtomicB");
            repository.Create(a);
            repository.Create(b);
            a.Silver = 10;
            b.Silver = 20;
            Check(repository.SaveMany([a, b]) == 2, "joint save persists both characters");
            var beforeA = JsonSerializer.Serialize(repository.Find(a.Id));
            var beforeB = JsonSerializer.Serialize(repository.Find(b.Id));
            a.Silver = 1000;
            // Invalid item after A's write: the whole batch, including A, must roll back.
            b.Inventory = [new(null!, 1)];
            var failed = false;
            try { repository.SaveMany([a, b]); }
            catch (Exception e) when (e is System.Data.Common.DbException or ArgumentException or InvalidDataException) { failed = true; }
            Check(failed && JsonSerializer.Serialize(repository.Find(a.Id)) == beforeA && JsonSerializer.Serialize(repository.Find(b.Id)) == beforeB,
                "failed joint save preserves both previous snapshots");
            try
            {
                repository.Batch(batch => { batch.Credit(a.Id, 77); throw new InvalidOperationException("synthetic rollback"); });
                Check(false, "market batch swallowed its failure");
            }
            catch (InvalidOperationException e) when (e.Message == "synthetic rollback") { }
            Check(repository.TakeCredits(a.Id) == 0, "failed market batch leaves no credit");
            repository.Batch(batch => batch.Credit(a.Id, 77));
            Check(repository.TakeCredits(a.Id) == 77 && repository.TakeCredits(a.Id) == 0, "successful credit can be collected once");
        }
        finally { Directory.Delete(dir, true); }
    }

    private static ServerConfig HostConfig(string dir) => new()
    {
        SavesPath = dir,
        ContentPath = Path.Combine(DataDir(), "..", "content"),
        LoginEndPoint = new IPEndPoint(IPAddress.Loopback, 0),
        GameEndPoint = new IPEndPoint(IPAddress.Loopback, 0),
        ChatEndPoint = new IPEndPoint(IPAddress.Loopback, 0),
    };

    // Redirects storage to demonstrate that the host uses the supplied factory, not a hidden default connection.
    private sealed class RedirectedFactory(string folder) : IGameRepositoryFactory
    {
        public IGameRepository? Opened { get; private set; }
        public IGameRepository Open(ServerConfig config) => Opened = new SqliteRepositoryFactory().Open(config with { SavesPath = folder });
    }

    private static void HostBackend()
    {
        var dir = NewDir();
        try
        {
            var factory = new RedirectedFactory(Path.Combine(dir, "backend"));
            var config = HostConfig(dir);
            using (var host = new ServerHost(config, Client with { Clusters = null }, factory))
            {
                Check(ReferenceEquals(host.Characters, factory.Opened) && ReferenceEquals(host.World.Market, factory.Opened)
                    && ReferenceEquals(host.World.Mail, factory.Opened), "host and services share the injected backend");
                Check(host.Characters.Find("LocalHero") is not null, "bootstrap created the default character through the backend");
                Check(!File.Exists(config.DatabasePath), "host did not open the default database");
            }
            using (var reopened = new SqliteRepositoryFactory().Open(config with { SavesPath = Path.Combine(dir, "backend") }))
                Check(reopened.Find("LocalHero") is not null, "injected database survives host disposal");
            using var exclusive = File.Open(Path.Combine(dir, "backend", "albionlocal.db"), FileMode.Open, FileAccess.Read, FileShare.None);
        }
        finally { Directory.Delete(dir, true); }
    }

    private static void FailedImport()
    {
        var dir = NewDir();
        try
        {
            var config = HostConfig(dir);
            File.WriteAllText(config.JsonSavePath, "{ invalid JSON");
            var factory = new RedirectedFactory(Path.Combine(dir, "backend"));
            try
            {
                using var unexpected = new ServerHost(config, Client with { Clusters = null }, factory);
                Check(false, "invalid import accepted");
            }
            catch (JsonException) { }
            using (File.Open(Path.Combine(dir, "backend", "albionlocal.db"), FileMode.Open, FileAccess.ReadWrite, FileShare.None)) { }
            File.Delete(config.JsonSavePath);
            using var retry = new ServerHost(config, Client with { Clusters = null }, factory);
            Check(retry.Characters.Find("LocalHero") is not null, "startup can retry after a failed import");
        }
        finally { Directory.Delete(dir, true); }
    }
}
