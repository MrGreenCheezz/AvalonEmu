using AlbionLocal.Photon;
using AlbionLocal.Server.Protocol;

namespace AlbionLocal.Server.Network;

/// <summary>StageR fw-zone (analysis/work/stageR/fw-zone/SPEC.md §2-3): the bandit assault's standings and the zerg debuff's debug info.</summary>
public sealed partial class WorldSession
{
    /// <summary>
    /// RequestRedZoneEventStandings (op 540, aac {}): aiu {0 a7m[] standings by faction − 1}; de3.h 0x180ad9eb0 copies each entry into its own
    /// array of the same length where it grew (<see cref="Systems.RedZoneEvents.Standings"/>, all 0 while no event ran).
    /// </summary>
    [Handles(OperationCodes.RequestRedZoneEventStandings, Status = SessionStatus.InWorld)]
    private RequestRedZoneEventStandingsResponse RequestRedZoneEventStandings(RequestRedZoneEventStandingsRequest req) =>
        new() { ble = world.RedZone.Standings() };

    /// <summary>
    /// GetZergDebuffInfo (op 541, acy {0 long, 1 Guid group, 2 int}): agt {0 int debuff level, 1 Guid group id, 2 long mount disarray level},
    /// which gx7 0x180da8b20 only prints as "Zerg Debuff Info" debug text. This server has no zerg debuff: level 0, the group echoed, 0 [assume].
    /// </summary>
    [Handles(OperationCodes.GetZergDebuffInfo, Status = SessionStatus.InWorld)]
    private GetZergDebuffInfoResponse GetZergDebuffInfo(GetZergDebuffInfoRequest req) =>
        new() { a5a = 0, a5b = req.ajd, a5c = 0 };
}
