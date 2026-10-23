using AlbionLocal.Server.Game;

namespace AlbionLocal.Server.Persistence;

/// <summary>
/// The owned islands (stageH islands SPEC §0.5), saved at once like the guilds (a gift or a level change is rare), not through
/// <see cref="SaveScheduler"/>. The game keeps them in memory (<see cref="Systems.Islands"/>) and writes each change here.
/// </summary>
public interface IIslandStore
{
    /// <summary>Every island with its owner's name (from the character table), in purchase order.</summary>
    IReadOnlyList<Island> LoadIslands();

    /// <summary>Stores <paramref name="island"/> (new or changed).</summary>
    void SaveIsland(Island island);

    void DeleteIsland(Guid id);

    /// <summary>Every player building of every island (phase 3: farms and herb gardens).</summary>
    IReadOnlyList<IslandBuilding> LoadIslandBuildings();

    void SaveIslandBuilding(IslandBuilding building);

    /// <summary>Removes a building and the crops in it.</summary>
    void DeleteIslandBuilding(Guid id);

    /// <summary>Every crop of every island.</summary>
    IReadOnlyList<Farmable> LoadFarmables();

    /// <summary>Stores <paramref name="farmable"/> (planted or watered).</summary>
    void SaveFarmable(Farmable farmable);

    /// <summary>Removes a crop (harvested or destroyed).</summary>
    void DeleteFarmable(Guid id);

    /// <summary>Every labourer of every island's houses (schema v33).</summary>
    IReadOnlyList<IslandLabourer> LoadLabourers();

    /// <summary>Stores <paramref name="labourer"/> (hired or changed).</summary>
    void SaveLabourer(IslandLabourer labourer);

    /// <summary>Removes a dismissed labourer.</summary>
    void DeleteLabourer(Guid id);
}
