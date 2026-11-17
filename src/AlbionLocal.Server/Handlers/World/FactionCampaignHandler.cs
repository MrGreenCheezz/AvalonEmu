using AlbionLocal.Photon;
using AlbionLocal.Server.Protocol;

namespace AlbionLocal.Server.Network;

/// <summary>StageR fw-campaign (analysis/work/stageR/fw-campaign/SPEC.md): the campaign chests and the weekly report; the rules in <see cref="Systems.FactionCampaign"/>.</summary>
public sealed partial class WorldSession
{
    /// <summary>
    /// ClaimFactionWarfareItemReward (op 437, qv {0 the reward id}; FactionWarfareRewardWeeklyItemReward.OnClickClaim 0x181175320): no response
    /// class, the client reads the return code (0: CLAIMED and the campaign data asked again; 17480 / 17481 inventory lines; else a generic error).
    /// </summary>
    [Handles(OperationCodes.ClaimFactionWarfareItemReward, Status = SessionStatus.InWorld)]
    private GenericResponse ClaimFactionWarfareItemReward(ClaimFactionWarfareItemRewardRequest req) =>
        new() { ReturnCode = Player is { } player ? Systems.FactionCampaign.Claim(player, req.fn) : Systems.FactionCampaign.RcGeneric };

    /// <summary>GetFactionWarfareWeeklyReport (op 434, tn {}; dex.as 0x180aea540 when its cached report is of another week or faction): aff.</summary>
    [Handles(OperationCodes.GetFactionWarfareWeeklyReport, Status = SessionStatus.InWorld)]
    private GetFactionWarfareWeeklyReportResponse GetFactionWarfareWeeklyReport(GetFactionWarfareWeeklyReportRequest req) =>
        Player is { } player ? Systems.FactionCampaign.WeeklyReport(player) : new GetFactionWarfareWeeklyReportResponse();

    /// <summary>
    /// ClaimFactionWarfareWeeklyReport (op 435, qw {}; dex.au 0x180aea8b0, only while the report's key 14 is above 0): the report's bonus is not
    /// made here (key 14 stays 0, SPEC §4), so a request is refused with RC 1 [assume].
    /// </summary>
    [Handles(OperationCodes.ClaimFactionWarfareWeeklyReport, Status = SessionStatus.InWorld)]
    private GenericResponse ClaimFactionWarfareWeeklyReport(ClaimFactionWarfareWeeklyReportRequest req)
    {
        Log.Info(Name, "ClaimFactionWarfareWeeklyReport: no weekly bonus is made here");
        return new GenericResponse { ReturnCode = 1 };
    }
}
