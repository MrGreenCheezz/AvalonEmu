using AlbionLocal.Server.Protocol;

namespace AlbionLocal.Server.Network;

/// <summary>
/// The guild's progress windows (stage I, analysis/work/stageI/guild-goals/SPEC.md §1.3, §1.4, §1.6-§1.8; the rules in <see cref="Systems.GuildGoals"/>):
/// the Guild Season window's Guild Challenge tab (390), header (152), Rankings tab (151), Overview breakdown (447) and Guild Might tab (450,
/// 449), and Guild Logs → Member Logs (309).
/// </summary>
public sealed partial class WorldSession
{
    /// <summary>
    /// GetGuildChallengePoints (op 390, ts: 0 guild, 1 first row, 2 count, 3 a member name, its row's window then): afk {0 guild, 1 the time of the guild's last
    /// change, 2 the total, 3 the members, 4 the first row, 5 names, 6 points}; no keys outside the guild (or with Guild:Enabled off, the
    /// stub's empty answer of before).
    /// </summary>
    [Handles(OperationCodes.GetGuildChallengePoints, Status = SessionStatus.InWorld)]
    private AlbionResponse? GetGuildChallengePoints(GetGuildChallengePointsRequest req) =>
        Player is { } player ? world.GuildGoals.ChallengePoints(player, req.nb, req.nc, req.nd, req.ne) : null;

    /// <summary>GetGvgSeasonRank (op 152, t1: 0 season index, 1 guild name): afu {0 rank, 1 points}, or {0: -1} as officially for a guild without a rank.</summary>
    [Handles(OperationCodes.GetGvgSeasonRank, Status = SessionStatus.InWorld)]
    private AlbionResponse? GetGvgSeasonRank(GetGvgSeasonRankRequest req) =>
        Player is { } player ? world.GuildGoals.Rank(player, req.ns, req.nt) : null;

    /// <summary>GetGvgSeasonRankings (op 151, t2: 0 season index, 1 first row, 2 count): aft, the 18 keys of the local guilds by season points.</summary>
    [Handles(OperationCodes.GetGvgSeasonRankings, Status = SessionStatus.InWorld)]
    private AlbionResponse? GetGvgSeasonRankings(GetGvgSeasonRankingsRequest req) =>
        Player is not null ? world.GuildGoals.Rankings(req.nu, req.nv, req.nw) : null;

    /// <summary>GetGvgSeasonContributionByActivity (op 447, tx: 0 guild): afp {0 season, 1 guild, 2 time, 3 total, 4 activities, 5 their points}.</summary>
    [Handles(OperationCodes.GetGvgSeasonContributionByActivity, Status = SessionStatus.InWorld)]
    private AlbionResponse? GetGvgSeasonContributionByActivity(GetGvgSeasonContributionByActivityRequest req) =>
        Player is { } player ? world.GuildGoals.Contribution(player, req.nl) : null;

    /// <summary>GetGuildMightCategoryOverview (op 450, tw: 0 guild): afo {0 guild, 1 time, 2 categories, 3 their might}, both arrays always.</summary>
    [Handles(OperationCodes.GetGuildMightCategoryOverview, Status = SessionStatus.InWorld)]
    private AlbionResponse? GetGuildMightCategoryOverview(GetGuildMightCategoryOverviewRequest req) =>
        Player is { } player ? world.GuildGoals.MightOverview(player, req.nk) : null;

    /// <summary>
    /// GetGuildMightCategoryContribution (op 449, tv: 0 guild, 1 category, 2 first row, 3 count, 4 a member name: its row's window then): afn {0 guild,
    /// 1 category, 2 time, 3 the guild's might, 4 members, 5 first row, 6 names, 7 might}.
    /// </summary>
    [Handles(OperationCodes.GetGuildMightCategoryContribution, Status = SessionStatus.InWorld)]
    private AlbionResponse? GetGuildMightCategoryContribution(GetGuildMightCategoryContributionRequest req) =>
        Player is { } player ? world.GuildGoals.MightContribution(player, req.nf, req.ng, req.nh, req.ni, req.nj) : null;

    /// <summary>GetGvgSeasonHistoryRankings (op 153, t0: 0 guild name): afs {0 time, 1 past client seasons, 2 ranks, 3 points} (stageP territories SPEC P2).</summary>
    [Handles(OperationCodes.GetGvgSeasonHistoryRankings, Status = SessionStatus.InWorld)]
    private AlbionResponse? GetGvgSeasonHistoryRankings(GetGvgSeasonHistoryRankingsRequest req) =>
        Player is { } player ? world.GuildGoals.HistoryRankings(player, req.nr) : null;

    /// <summary>GetGvgSeasonGuildMemberHistory (op 154, tz: 0 season index): afr, the members' challenge points and might of that season, 12 parallel arrays.</summary>
    [Handles(OperationCodes.GetGvgSeasonGuildMemberHistory, Status = SessionStatus.InWorld)]
    private AlbionResponse? GetGvgSeasonGuildMemberHistory(GetGvgSeasonGuildMemberHistoryRequest req) =>
        Player is { } player ? world.GuildGoals.MemberHistory(player, req.nq) : null;

    /// <summary>
    /// GvGSeasonPlayerGuildParticipationDetails (op 504, vn: 0 season index, 1 the requester's Guid): ag5, the guild, its points and rank and the
    /// member's participation; no answer for a season the client lacks (stageP territories SPEC P2).
    /// </summary>
    [Handles(OperationCodes.GvGSeasonPlayerGuildParticipationDetails, Status = SessionStatus.InWorld)]
    private AlbionResponse? GvGSeasonPlayerGuildParticipationDetails(GvGSeasonPlayerGuildParticipationDetailsRequest req) =>
        Player is { } player ? world.GuildGoals.Participation(player, req.pu, req.pv) : null;

    /// <summary>
    /// QueryGuildPlayerStats (op 309, zj: 0 TimeFrame, 1 the stat; no request id): aic {0 the member Guids, 1 their values (a7m)}, never
    /// null arrays (the client passes both to its GUI unchecked); empty arrays outside a guild, zeros with Guild:Enabled off.
    /// </summary>
    [Handles(OperationCodes.QueryGuildPlayerStats, Status = SessionStatus.InWorld)]
    private AlbionResponse? QueryGuildPlayerStats(QueryGuildPlayerStatsRequest req) =>
        Player is { } player ? world.GuildGoals.MemberStats(player, req.yx, req.yy) : null;
}
