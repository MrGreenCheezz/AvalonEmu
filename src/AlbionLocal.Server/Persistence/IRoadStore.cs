namespace AlbionLocal.Server.Persistence;

/// <summary>A link of the Roads of Avalon network as saved (stageL roads SPEC §5.7): both spots by cluster and criticalObjects path, its type and times.</summary>
public sealed record RoadLinkRow(long Id, string OutCluster, string OutPath, string InCluster, string InPath, int Type, long Opened, long Closes, int Charges,
    long NextCharge, long RecentUntil);

/// <summary>An outgoing spot without a link as saved: searching (1, with its keyword) or idle (2), and the end of its lifetime.</summary>
public sealed record RoadSlotRow(string Cluster, string Path, int State, string? Keyword, long Closes);

/// <summary>
/// The Roads of Avalon network (stageL roads SPEC §5.7), kept in memory by <see cref="Systems.AvalonRoads"/>, which writes its changes in batches
/// (one transaction per batch, every few seconds and at the end), not through <see cref="SaveScheduler"/>: a link is no character's.
/// </summary>
public interface IRoadStore
{
    /// <summary>Every saved link and outgoing spot.</summary>
    (IReadOnlyList<RoadLinkRow> Links, IReadOnlyList<RoadSlotRow> Slots) LoadRoads();

    /// <summary>
    /// One batch: the links <paramref name="links"/> stored (new or changed), those of <paramref name="droppedLinks"/> removed, the spots of
    /// <paramref name="slots"/> stored and those of <paramref name="droppedSlots"/> (linked or incoming now) removed.
    /// </summary>
    void SaveRoads(IReadOnlyList<RoadLinkRow> links, IReadOnlyList<long> droppedLinks, IReadOnlyList<RoadSlotRow> slots, IReadOnlyList<(string Cluster, string Path)> droppedSlots);

    /// <summary>Removes the whole network (.roads regen).</summary>
    void ClearRoads();
}
