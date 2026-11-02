using AlbionLocal.Photon;
using AlbionLocal.Server.Protocol;

namespace AlbionLocal.Server.Network;

public sealed partial class WorldSession
{
    /// <summary>
    /// QueryAllianceGuildStats (310, current 345319 zi): key 0 TimeFrame, key 1 bex (SeasonPoints = 0); response aib has
    /// key 0 Guid[] guilds and key 1 long[] values (a7m, whole season points multiplied by 10000). Empty parallel arrays finish
    /// the request when the character has no alliance.
    /// </summary>
    [Handles(OperationCodes.QueryAllianceGuildStats, Status = SessionStatus.InWorld)]
    private AlbionResponse? QueryAllianceGuildStats(QueryAllianceGuildStatsRequest req) =>
        Player is { } player ? world.GuildGoals.AllianceStats(player, req.yv, req.yw) : null;
}
