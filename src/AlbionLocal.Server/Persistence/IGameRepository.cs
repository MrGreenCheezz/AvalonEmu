namespace AlbionLocal.Server.Persistence;

/// <summary>
/// A complete persistence backend owned by the server host. Domain services still consume their narrow interfaces.
/// All stores share one lifetime; implementations must preserve each interface's transactional guarantees.
/// A backend cannot silently omit market, mail, guild, island, social or road persistence.
/// </summary>
public interface IGameRepository : ICharacterRepository, IMarketStore, IMailStore, IGuildStore,
    IIslandStore, ISocialStore, IRoadStore
{
}

/// <summary>Creates a complete backend. The caller owns and disposes the returned repository.</summary>
public interface IGameRepositoryFactory
{
    IGameRepository Open(ServerConfig config);
}
