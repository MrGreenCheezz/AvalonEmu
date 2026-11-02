using AlbionLocal.Photon;
using AlbionLocal.Server.Protocol;
using AlbionLocal.Server.Systems;

namespace AlbionLocal.Server.Network;

public sealed partial class WorldSession
{
    [Handles(OperationCodes.GetPvpChallengeData, Status = SessionStatus.InWorld)]
    private GetPvpChallengeDataResponse GetPvpChallengeData(GetPvpChallengeDataRequest req) =>
        Player is { } p ? PvpChallenge.Response(p) : new() { ReturnCode = 1 };

    [Handles(OperationCodes.ClaimPvpChallengeWeeklyReward, Status = SessionStatus.InWorld)]
    private GenericResponse ClaimPvpChallengeWeeklyReward(ClaimPvpChallengeWeeklyRewardRequest req) =>
        new() { ReturnCode = Player is { } p ? PvpChallenge.Claim(p) : (short)1 };

    [Handles(OperationCodes.GetPersonalMightStats, Status = SessionStatus.InWorld)]
    private GetPersonalMightStatsResponse GetPersonalMightStats(GetPersonalMightStatsRequest req) =>
        Player is { } p ? PvpChallenge.Stats(p, req.od) : new() { ReturnCode = 1, a2y = req.od, a2z = [], a20 = [] };

    [Handles(OperationCodes.GetPvpChallengeSeasonRewards, Status = SessionStatus.InWorld)]
    private GetPvpChallengeSeasonRewardsResponse GetPvpChallengeSeasonRewards(GetPvpChallengeSeasonRewardsRequest req) =>
        Player is { } p ? PvpChallenge.SeasonRewards(p) : PvpChallenge.EmptyRewards(1);

    [Handles(OperationCodes.GetPvpChallengeSeasonRewardItems, Status = SessionStatus.InWorld)]
    private GetPvpChallengeSeasonRewardItemsResponse GetPvpChallengeSeasonRewardItems(GetPvpChallengeSeasonRewardItemsRequest req) =>
        Player is { } p ? PvpChallenge.SeasonItems(p, req.oj) : new() { ReturnCode = 1, a3i = req.oj, a3j = [], a3k = [] };

    [Handles(OperationCodes.ClaimPvpChallengeSeasonRewards, Status = SessionStatus.InWorld)]
    private GenericResponse ClaimPvpChallengeSeasonRewards(ClaimPvpChallengeSeasonRewardsRequest req) =>
        new() { ReturnCode = Player is { } p ? PvpChallenge.ClaimSeason(p, req.fw, req.fx) : (short)1 };

    [Handles(OperationCodes.ClaimPvpChallengeSeasonRewardItems, Status = SessionStatus.InWorld)]
    private GenericResponse ClaimPvpChallengeSeasonRewardItems(ClaimPvpChallengeSeasonRewardItemsRequest req) =>
        new() { ReturnCode = Player is { } p ? PvpChallenge.ClaimSeasonItems(p, req.ft, req.fu, req.fv,
            ShopRequest(OperationCodes.ClaimPvpChallengeSeasonRewardItems)) : (short)1 };
}
