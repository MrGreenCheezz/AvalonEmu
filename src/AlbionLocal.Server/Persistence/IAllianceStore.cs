using AlbionLocal.Server.Game;

namespace AlbionLocal.Server.Persistence;

/// <summary>
/// The alliances (analysis/work/stageP/hideouts/SPEC.md §7 A1), saved at once like the guilds (<see cref="IGuildStore"/>): the alliances with
/// their guilds, the open invitations and each guild's alliance history (the cooldowns). A guild's rows go with the guild (ON DELETE CASCADE).
/// </summary>
public interface IAllianceStore
{
    /// <summary>Every alliance with its guilds in join order.</summary>
    IReadOnlyList<Alliance> LoadAlliances();

    /// <summary>Stores <paramref name="alliance"/> (new or changed) with all its guild rows, in one transaction.</summary>
    void SaveAlliance(Alliance alliance);

    /// <summary>Deletes the alliance, its guild rows and its invitations.</summary>
    void DeleteAlliance(Guid id);

    IReadOnlyList<AllianceInvite> LoadAllianceInvites();

    void SaveAllianceInvite(AllianceInvite invite);

    void DeleteAllianceInvite(Guid alliance, Guid guild);

    IReadOnlyList<AllianceHistory> LoadAllianceHistory();

    void SaveAllianceHistory(AllianceHistory history);
}
