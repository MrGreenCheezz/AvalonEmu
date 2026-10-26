using AlbionLocal.Server.Game;

namespace AlbionLocal.Server.Persistence;

/// <summary>
/// The guild hideouts (analysis/work/stageP/hideouts/SPEC.md phases H1-H4; schema v40: hideouts, hideout_homes), each change saved at once. A
/// hideout's row goes with its guild, a home with its character or its hideout (ON DELETE CASCADE).
/// </summary>
public interface IHideoutStore
{
    IReadOnlyList<Hideout> LoadHideouts();

    void SaveHideout(Hideout h);

    void DeleteHideout(Guid id);

    /// <summary>The characters whose home is a hideout (Make Home), with that hideout.</summary>
    IReadOnlyList<(Guid Character, Guid Hideout)> LoadHideoutHomes();

    /// <summary>Stores <paramref name="character"/>'s home hideout; null deletes the row (Leave Home).</summary>
    void SaveHideoutHome(Guid character, Guid? hideout);
}
