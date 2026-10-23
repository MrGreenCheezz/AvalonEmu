using AlbionLocal.Photon;

namespace AlbionLocal.Server.Persistence;

/// <summary>The default backend; opening it keeps the existing database path and schema migrations.</summary>
public sealed class SqliteRepositoryFactory : IGameRepositoryFactory
{
    public IGameRepository Open(ServerConfig config)
    {
        var repository = new SqliteCharacterRepository(config.DatabasePath, config.LocalAccountLevel);
        Log.Info("db", $"SQLite: {config.DatabasePath}");
        return repository;
    }
}
