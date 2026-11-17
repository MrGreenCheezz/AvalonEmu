using AlbionLocal.Server.Protocol;

namespace AlbionLocal.Server.Network;

/// <summary>The season windows: personal season (Adventurer's Challenge) and faction warfare campaign (ops-audit SPEC §3.2, §3.3).</summary>
public sealed partial class WorldSession
{
    /// <summary>
    /// GetPersonalSeasonPastRewardData (op 373, ul {}): sent once per connection by cts.i 0x1809b6c30 when the Season window
    /// first shows its past seasons. No response class; officially event PersonalSeasonPastSeasonDataEvent (377, aw3) 0.10 s
    /// later, keys 2-8 parallel arrays with one entry per past season (2 string, 3 short year, 4 byte, 5/6 string, 7 int,
    /// 8 string; cts.d). Here no past seasons: keys 0/1 = 0 (left out) and seven empty arrays, so the dropdown is empty and
    /// PersonalSeasonPastView.QueryData (a per-season request whose op is not known) never runs.
    /// </summary>
    [Handles(OperationCodes.GetPersonalSeasonPastRewardData, Status = SessionStatus.InWorld)]
    private AlbionResponse? GetPersonalSeasonPastRewardData(GetPersonalSeasonPastRewardDataRequest req)
    {
        SendEvent(new PersonalSeasonPastSeasonDataEventEvent { dlr = [], dls = [], dlt = [], dlu = [], dlv = [], dlw = [], dlx = [] });
        return null;
    }

    /// <summary>
    /// GetPersonalSeasonTrackerData (op 372, um / agb; stageH journals SPEC §C.3): asked after JoinFinished, at every ChangeCluster and when
    /// the Adventurer's Challenge window's cache runs out, with the season id (ADCSEASON_10@2026). The answer: the trackers and their
    /// points, the rewards that are not LOCKED with their states (<see cref="Systems.Seasons.Response"/>); the empty one (as the stub before)
    /// without personalseasons.bin or with Season:Enabled off.
    /// </summary>
    [Handles(OperationCodes.GetPersonalSeasonTrackerData, Status = SessionStatus.InWorld)]
    private GetPersonalSeasonTrackerDataResponse GetPersonalSeasonTrackerData(GetPersonalSeasonTrackerDataRequest req) =>
        Player is { } player ? Systems.Seasons.Response(player, req.oe) : new GetPersonalSeasonTrackerDataResponse { a21 = req.oe ?? "" }.FillEmpty();

    /// <summary>
    /// ClaimPersonalSeasonReward (op 375, q0 {0 season id, 1 reward id}; PersonalSeasonItemReward.OnClickClaim 0x1815e4b70): no response
    /// class, the client reads the return code only (0, 15001 claimed, 15002 not enough points, 15004 not claimable, 15005 no inventory
    /// space; <see cref="Systems.Seasons.Claim"/>) and asks 372 again after a 0.
    /// </summary>
    [Handles(OperationCodes.ClaimPersonalSeasonReward, Status = SessionStatus.InWorld)]
    private GenericResponse ClaimPersonalSeasonReward(ClaimPersonalSeasonRewardRequest req) =>
        new() { ReturnCode = Player is { } player ? Systems.Seasons.Claim(player, req.fr, req.fs) : Systems.Seasons.RcNotClaimable };

    /// <summary>
    /// GetFactionWarfareCampaignData (op 436, tm {}; asked by dex.av 0x180aeaaa0, read by dex.z 0x180aeb3b0). Key 4 is the
    /// campaign month, months since 0001-01 (bcg.abu 0x1805aca60 = baseDate.AddMonths(key 4); official 24309 on 2026-10-16):
    /// the empty answer's 0 was "January of year 1". Keys 5-8 had six entries officially (one per faction, presumably): six
    /// zeros, in case a campaign view indexes them per faction (Q4); keys 0-3 empty. Since stageR fw-campaign the character's campaign
    /// (<see cref="Systems.FactionCampaign.Response"/>); this empty one only without a player.
    /// </summary>
    [Handles(OperationCodes.GetFactionWarfareCampaignData, Status = SessionStatus.InWorld)]
    private GetFactionWarfareCampaignDataResponse GetFactionWarfareCampaignData(GetFactionWarfareCampaignDataRequest req) =>
        Player is { } player ? Systems.FactionCampaign.Response(player) : new GetFactionWarfareCampaignDataResponse
        {
            awe = CampaignMonth(DateTime.UtcNow),
            awf = new long[Factions], awg = new int[Factions], awh = new int[Factions], awi = new int[Factions],
        }.FillEmpty();

    /// <summary>The six factions of keys 5-8 of the campaign data (the six royal cities' factions).</summary>
    private const int Factions = 6;

    /// <summary>The faction warfare campaign month of <paramref name="utc"/>: (year - 1) × 12 + month - 1 (2026-10 = 24309).</summary>
    public static int CampaignMonth(DateTime utc) => (utc.Year - 1) * 12 + utc.Month - 1;
}
