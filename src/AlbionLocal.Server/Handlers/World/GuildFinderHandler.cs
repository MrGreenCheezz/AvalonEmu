using AlbionLocal.Photon;
using AlbionLocal.Server.Game;
using AlbionLocal.Server.Protocol;

namespace AlbionLocal.Server.Network;

/// <summary>
/// The guild finder and applications (analysis/work/stageR/guild/SPEC.md §1; the rules in Systems/Guilds.Finder.cs): the recruitment info,
/// the finder's lists, "Join" of an open guild, "Apply" and the answer to an application mail. Each is answered with the request's id (the
/// client's callbacks in gev, dee, deg, deh, deb and MailInvitationControls).
/// </summary>
public sealed partial class WorldSession
{
    /// <summary>UpdateGuildRecruitmentInfo (op 184, ab7 keys 0-11; no response class): stored for the guild (EDIT_GUILD_DESCRIPTION).</summary>
    [Handles(OperationCodes.UpdateGuildRecruitmentInfo, Status = SessionStatus.InWorld)]
    private AlbionResponse? UpdateGuildRecruitmentInfo(UpdateGuildRecruitmentInfoRequest req)
    {
        if (Player is not { } player) return null;
        var info = new GuildRecruitment
        {
            Language = req.ahj, SecondLanguage = req.ahk, Description = req.ahl ?? "", Join = req.ahm, BeginnerFriendly = req.ahn, Discord = req.aho ?? "",
            Home = req.ahp, TimeFrom = (sbyte)req.ahq, TimeTo = (sbyte)req.ahr, Activities = req.ahs, Requirement1 = req.aht, Requirement2 = req.ahu,
        };
        if (!world.Guilds.SetRecruitment(player, info)) Log.Info(Name, $"'{player.Character.Name}' UpdateGuildRecruitmentInfo refused (no guild or no right)");
        return null;
    }

    /// <summary>RequestGuildRecruitmentInfo (op 185, z7 {0 guild, 1 part}): aip, or rc 7400 for an unknown guild (the details view stays empty).</summary>
    [Handles(OperationCodes.RequestGuildRecruitmentInfo, Status = SessionStatus.InWorld)]
    private AlbionResponse? RequestGuildRecruitmentInfo(RequestGuildRecruitmentInfoRequest req) =>
        world.Guilds.Recruitment(req.aae, req.aaf) ?? new RequestGuildRecruitmentInfoResponse { ReturnCode = Systems.Guilds.RecruitmentNoGuild, bkc = req.aae, bkd = req.aaf };

    /// <summary>
    /// RequestGuildFinderFilteredList (op 183, z4: 0 offset (page × 10), 1 count 10, 6 language, 7 home, 8/9 member range, 12 activities):
    /// aim {0 the offset, 1 more pages, 2-35 the rows}.
    /// </summary>
    [Handles(OperationCodes.RequestGuildFinderFilteredList, Status = SessionStatus.InWorld)]
    private AlbionResponse? RequestGuildFinderFilteredList(RequestGuildFinderFilteredListRequest req)
    {
        var all = world.Guilds.Filtered(req.z4, req.z5, req.z6, req.z7, req.aaa);
        return world.Guilds.FilteredRows(all, req.zy, req.zz == 0 ? Systems.Guilds.FinderPage : req.zz);
    }

    /// <summary>RequestGuildFinderNameSearch (op 186, z5 {0 text}): ain, the guilds whose name contains it.</summary>
    [Handles(OperationCodes.RequestGuildFinderNameSearch, Status = SessionStatus.InWorld)]
    private AlbionResponse? RequestGuildFinderNameSearch(RequestGuildFinderNameSearchRequest req) => world.Guilds.NamedRows(world.Guilds.Named(req.aab));

    /// <summary>RequestGuildFinderRecommendedList (op 187, z6 {0 language, 1 byte}): aio with up to three guilds recruiting in it.</summary>
    [Handles(OperationCodes.RequestGuildFinderRecommendedList, Status = SessionStatus.InWorld)]
    private AlbionResponse? RequestGuildFinderRecommendedList(RequestGuildFinderRecommendedListRequest req) => world.Guilds.RecommendedRows(req.aac, req.aad);

    /// <summary>InstantJoinGuild (op 109, we {0 guild, 1 origin}): rc 0 ("@JOINGUILD_SUCCESSFUL"), then the guild's events as an accepted invitation; errors 4360-4365.</summary>
    [Handles(OperationCodes.InstantJoinGuild, Status = SessionStatus.InWorld)]
    private AlbionResponse? InstantJoinGuild(InstantJoinGuildRequest req)
    {
        if (Player is not { } player) return null;
        var rc = world.Guilds.InstantJoin(player, req.rc, out var guild, out var end);
        if (rc != 0 || guild is null)
        {
            Log.Info(Name, $"'{player.Character.Name}' InstantJoinGuild refused: {rc}");
            return new InstantJoinGuildResponse { ReturnCode = rc, a7w = end };
        }
        SendResponse(OperationCodes.InstantJoinGuild, new InstantJoinGuildResponse(), RequestId, RequestEncrypted);
        world.Guilds.AfterJoin(player, guild);
        return null;
    }

    /// <summary>ApplyToGuild (op 181, o1 {0 guild, 1 origin, 2 message, 3 stat categories}): rc 0 ("@LFG_SENT_APPLICATION"), 7240-7242, 1.</summary>
    [Handles(OperationCodes.ApplyToGuild, Status = SessionStatus.InWorld)]
    private AlbionResponse? ApplyToGuild(ApplyToGuildRequest req)
    {
        if (Player is not { } player) return null;
        var rc = world.Guilds.Apply(player, req.ba, req.bc, req.bd, out var end);
        if (rc != 0) Log.Info(Name, $"'{player.Character.Name}' ApplyToGuild refused: {rc}");
        return new ApplyToGuildResponse { ReturnCode = rc, akw = end };
    }

    /// <summary>AnswerGuildApplication (op 182, ow {0 mail id, 1 accept}): rc 0 (the client goes back to the guild mail list) or 7280-7287 {0 end, 1 name}.</summary>
    [Handles(OperationCodes.AnswerGuildApplication, Status = SessionStatus.InWorld)]
    private AlbionResponse? AnswerGuildApplication(AnswerGuildApplicationRequest req)
    {
        if (Player is not { } player) return null;
        var rc = world.Guilds.Answer(player, req.aw, req.ax, out var end, out var name);
        if (rc != 0) Log.Info(Name, $"'{player.Character.Name}' AnswerGuildApplication refused: {rc}");
        return new AnswerGuildApplicationResponse { ReturnCode = rc, aku = end, akv = rc == 0 ? null : name };
    }
}
