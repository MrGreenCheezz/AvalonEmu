using AlbionLocal.Photon;
using AlbionLocal.Server.Protocol;

namespace AlbionLocal.Server.Network;

public sealed partial class WorldSession
{
    /// <summary>The real regional/seasonal potential attack time; no match is created by this read-only query.</summary>
    [Handles(OperationCodes.GetNextOpenWorldAttackScheduleTime, Status = SessionStatus.InWorld)]
    private AlbionResponse? GetNextOpenWorldAttackScheduleTime(GetNextOpenWorldAttackScheduleTimeRequest request) =>
        Player is { } player ? world.Territories.NextAttackSchedule(player, request.n9) : null;
}
