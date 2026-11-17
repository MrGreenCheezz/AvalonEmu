using AlbionLocal.Photon;
using AlbionLocal.Server.Protocol;
using AlbionLocal.Server.Systems;

namespace AlbionLocal.Server.Network;

/// <summary>
/// StageR stats (analysis/work/stageR/stats/SPEC.md): the stats window's kill history, the personal might and the Conqueror's Challenge, and the
/// smaller statistics windows. The rules in <see cref="KillHistory"/> and <see cref="Conqueror"/>.
/// </summary>
public sealed partial class WorldSession
{
    /// <summary>A return code the client's handlers read as a failure (cte.a 0x180c05210 "not available", c2y's claims a generic error) [assume].</summary>
    private const short StatsRefused = 1;

    // ---- the kill history (SPEC §1)

    /// <summary>
    /// GetCharacterStats (145 {0 a name, 1 RankingEntityTypes}; no response class): the bare rc-0 answer as captured (it was an "ack" stub) and, for a
    /// player (entity type 0), the four kill history lists (143-146). The stats event itself (142) is not made (SPEC Not done); a guild (type 1)
    /// gets GuildStats officially, not made either.
    /// </summary>
    [Handles(OperationCodes.GetCharacterStats, Status = SessionStatus.InWorld)]
    private GenericResponse GetCharacterStats(GetCharacterStatsRequest req)
    {
        if (Player is { } player && req.mg == 0)
            foreach (var e in world.KillHistory.StatsEvents(player, req.mf)) SendEvent(e);
        return new GenericResponse();
    }

    /// <summary>
    /// GetKillHistoryDetails (146 {0 the kill's Guid}; cte.a.q 0x180bf4ef0): KillHistoryDetails (148) then the answer {0 the Guid's bytes}; an unknown
    /// kill only the answer, refused (the window's "@STATS_KILLHISTORY_ERROR_NOT_AVAILABLE").
    /// </summary>
    [Handles(OperationCodes.GetKillHistoryDetails, Status = SessionStatus.InWorld)]
    private GetKillHistoryDetailsResponse GetKillHistoryDetails(GetKillHistoryDetailsRequest req)
    {
        if (Player is { } player && world.KillHistory.DetailsEvent(player, req.ny) is { } details)
        {
            SendEvent(details);
            return new GetKillHistoryDetailsResponse { a0e = req.ny.ToByteArray() };
        }
        return new GetKillHistoryDetailsResponse { a0e = req.ny.ToByteArray(), ReturnCode = StatsRefused };
    }

    /// <summary>
    /// GetItemKillHistory (466 {0 an item's id}; cte.a.q for an entry made with an item id: a trophy or a hideout item's kill): this server keeps no
    /// kills per item, so the answer {0 the id} is refused and the window says "not available" (SPEC Not done).
    /// </summary>
    [Handles(OperationCodes.GetItemKillHistory, Status = SessionStatus.InWorld)]
    private GetItemKillHistoryResponse GetItemKillHistory(GetItemKillHistoryRequest req) => new() { a0d = req.nx, ReturnCode = StatsRefused };

    // ---- the personal might and the Conqueror's Challenge (SPEC §2, §3)

    /// <summary>GetPvpChallengeData (451; was an empty stub): {0 the season might, 1 this week's, 2 the weekly chest's state}.</summary>
    [Handles(OperationCodes.GetPvpChallengeData, Status = SessionStatus.InWorld)]
    private GetPvpChallengeDataResponse GetPvpChallengeData(GetPvpChallengeDataRequest req) =>
        Player is { } player ? Conqueror.Data(player) : new GetPvpChallengeDataResponse();

    /// <summary>ClaimPvpChallengeWeeklyReward (452, no keys, no response class): the return code of <see cref="Conqueror.ClaimWeekly"/>.</summary>
    [Handles(OperationCodes.ClaimPvpChallengeWeeklyReward, Status = SessionStatus.InWorld)]
    private GenericResponse ClaimPvpChallengeWeeklyReward(ClaimPvpChallengeWeeklyRewardRequest req) =>
        new() { ReturnCode = Player is { } player ? Conqueror.ClaimWeekly(player) : Conqueror.RcGeneric };

    /// <summary>GetPersonalMightStats (453 {0 the season}): <see cref="Conqueror.Stats"/>.</summary>
    [Handles(OperationCodes.GetPersonalMightStats, Status = SessionStatus.InWorld)]
    private GetPersonalMightStatsResponse GetPersonalMightStats(GetPersonalMightStatsRequest req) =>
        Player is { } player ? Conqueror.Stats(player, req.od) : new GetPersonalMightStatsResponse { a2y = req.od, a2z = [], a20 = [] };

    /// <summary>
    /// DailyMightBonus (505; gkr.l4 0x180c92580: 0/1 the guild's territories and their might, 2 CanClaim, 3 the bonus, 4 claimable till): the
    /// territories' daily might is not known (SPEC Not done), so no territories, nothing to claim; the window shows its no-territory text.
    /// </summary>
    [Handles(OperationCodes.DailyMightBonus, Status = SessionStatus.InWorld)]
    private DailyMightBonusResponse DailyMightBonus(DailyMightBonusRequest req) => new() { av5 = [], av6 = [] };

    /// <summary>ClaimDailyMightBonus (506; MightBonusModule.ClaimBonus, only while CanClaim): never claimable here, refused.</summary>
    [Handles(OperationCodes.ClaimDailyMightBonus, Status = SessionStatus.InWorld)]
    private GenericResponse ClaimDailyMightBonus(ClaimDailyMightBonusRequest req)
    {
        Log.Info(Name, "ClaimDailyMightBonus: no daily might bonus is made here");
        return new GenericResponse { ReturnCode = StatsRefused };
    }

    /// <summary>
    /// GetPvpChallengeSeasonRewards (454; c2y.h 0x180a53480: per season 0 its uniquename, 1 the might, 2/3 the guild, 4 its bracket, 5 its rank,
    /// 6/7 two lists of reward ids): the client seasons the character has personal might in, with its guild of now and that guild's bracket in the
    /// season; the reward lists empty and the rank 0 (SPEC Not done: the season rewards are not claimable here). None = the empty history view.
    /// </summary>
    [Handles(OperationCodes.GetPvpChallengeSeasonRewards, Status = SessionStatus.InWorld)]
    private GetPvpChallengeSeasonRewardsResponse GetPvpChallengeSeasonRewards(GetPvpChallengeSeasonRewardsRequest req)
    {
        var rows = new List<(string Season, long Might, byte Bracket)>();
        var guild = Player is { } p ? world.Guilds.Of(p.Character.Id) : null;
        if (Player is { } player && world.GvgSeasons is { } data)
            foreach (var (index, stats) in Conqueror.LogOf(player).Seasons.OrderBy(s => s.Key))
                if (index >= 0 && index < data.Seasons.Count && stats.Values.Sum() is var might and > 0)
                {
                    var season = data.Seasons[index];
                    var bracket = guild is not null && GuildGoals.Peek(guild, season) is { } st ? season.BracketOf(st.Points)?.Name : null;
                    rows.Add((season.Key, might, BracketLevel(bracket)));
                }
        return new GetPvpChallengeSeasonRewardsResponse
        {
            a3l = [.. rows.Select(r => r.Season)], a3m = [.. rows.Select(r => r.Might)], a3n = [.. rows.Select(_ => guild?.Id ?? Guid.Empty)],
            a3o = [.. rows.Select(_ => guild?.Name ?? "")], a3p = [.. rows.Select(r => r.Bracket)], a3q = new int[rows.Count],
            a3r = [.. rows.Select(_ => Array.Empty<string>())], a3s = [.. rows.Select(_ => Array.Empty<string>())],
        };
    }

    /// <summary>but.BracketLevel: None, Iron, Bronze, Silver, Gold, Crystal (the bracket names of gvgseasons, lowercase).</summary>
    private static byte BracketLevel(string? bracket) => bracket switch
    {
        "iron" => 1, "bronze" => 2, "silver" => 3, "gold" => 4, "crystal" => 5, _ => 0,
    };

    /// <summary>GetPvpChallengeSeasonRewardItems (455 {0 the season}): {0 the season, 1/2 the items and counts waiting}: none here.</summary>
    [Handles(OperationCodes.GetPvpChallengeSeasonRewardItems, Status = SessionStatus.InWorld)]
    private GetPvpChallengeSeasonRewardItemsResponse GetPvpChallengeSeasonRewardItems(GetPvpChallengeSeasonRewardItemsRequest req) =>
        new() { a3i = req.oj, a3j = [], a3k = [] };

    /// <summary>ClaimPvpChallengeSeasonRewards (456 {0 the season, 1 a reward}): refused (SPEC Not done: the season rewards).</summary>
    [Handles(OperationCodes.ClaimPvpChallengeSeasonRewards, Status = SessionStatus.InWorld)]
    private GenericResponse ClaimPvpChallengeSeasonRewards(ClaimPvpChallengeSeasonRewardsRequest req)
    {
        Log.Info(Name, $"ClaimPvpChallengeSeasonRewards {req.fw} {req.fx}: the season rewards are not made here");
        return new GenericResponse { ReturnCode = StatsRefused };
    }

    /// <summary>ClaimPvpChallengeSeasonRewardItems (457 {0 the season, 1 items, 2 counts}): refused, nothing waits (see 455).</summary>
    [Handles(OperationCodes.ClaimPvpChallengeSeasonRewardItems, Status = SessionStatus.InWorld)]
    private GenericResponse ClaimPvpChallengeSeasonRewardItems(ClaimPvpChallengeSeasonRewardItemsRequest req)
    {
        Log.Info(Name, $"ClaimPvpChallengeSeasonRewardItems {req.ft}: no season reward items wait here");
        return new GenericResponse { ReturnCode = StatsRefused };
    }

    // ---- the smaller windows (SPEC §4)

    /// <summary>
    /// ArmoryActivityUsageStatistics (531 {0 the Activity}): {0 the Activity echoed, 1-3 the builds' usage}: the official population's statistics
    /// do not exist here, so none.
    /// </summary>
    [Handles(OperationCodes.ArmoryActivityUsageStatistics, Status = SessionStatus.InWorld)]
    private ArmoryActivityUsageStatisticsResponse ArmoryActivityUsageStatistics(ArmoryActivityUsageStatisticsRequest req) =>
        new() { ak1 = req.bv, ak2 = [], ak3 = [], ak4 = [] };

    /// <summary>RequestLegendaryEvenHistory (495 {0 a legendary item's Guid}): {0 the Guid echoed, 1-4 its events}: no history is kept here, none.</summary>
    [Handles(OperationCodes.RequestLegendaryEvenHistory, Status = SessionStatus.InWorld)]
    private RequestLegendaryEvenHistoryResponse RequestLegendaryEvenHistory(RequestLegendaryEvenHistoryRequest req) =>
        new() { bb1 = req.tj, bb2 = [], bb3 = [], bb4 = [], bb5 = [] };

    /// <summary>GetPlayerInfoForStalk (486 {0 a name}): {0 the character's Guid}; an unknown name Guid.Empty [infer].</summary>
    [Handles(OperationCodes.GetPlayerInfoForStalk, Status = SessionStatus.InWorld)]
    private GetPlayerInfoForStalkResponse GetPlayerInfoForStalk(GetPlayerInfoForStalkRequest req) =>
        new() { a3e = string.IsNullOrWhiteSpace(req.oi) ? Guid.Empty : world.Characters.Find(req.oi)?.Id ?? Guid.Empty };
}
