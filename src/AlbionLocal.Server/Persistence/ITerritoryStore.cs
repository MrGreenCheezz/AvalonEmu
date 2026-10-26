namespace AlbionLocal.Server.Persistence;

/// <summary>
/// One saved owner of a territory or castle (schema v39 territory_owners): the world.xml territory, the guild and since when (UTC); for a watchtower
/// also its food at a time, its access byte and its tower power points at a time (v41 territory_state, phases P3/P4; 0 for a castle).
/// </summary>
public sealed record TerritoryOwner(Guid Territory, Guid Guild, DateTime Since)
{
    public double Nutrition { get; init; }

    public DateTime FedAt { get; init; }

    public byte Access { get; init; }

    public double Power { get; init; }

    public DateTime PowerAt { get; init; }
}

/// <summary>
/// The owners of the guild territories and castles (analysis/work/stageP/territories/SPEC.md phases P3-P5), saved at once on each change. A guild's
/// rows go with the guild (ON DELETE CASCADE), a territory's state row with its owner row.
/// </summary>
public interface ITerritoryStore
{
    IReadOnlyList<TerritoryOwner> LoadTerritoryOwners();

    /// <summary>Stores the owner of <paramref name="territory"/>; Guid.Empty <paramref name="guild"/> deletes the row (unowned).</summary>
    void SaveTerritoryOwner(Guid territory, Guid guild, DateTime since) => SaveTerritoryOwner(new TerritoryOwner(territory, guild, since));

    /// <summary>Stores the owner row and its state; Guid.Empty guild deletes both (unowned).</summary>
    void SaveTerritoryOwner(TerritoryOwner owner);
}
