using AlbionLocal.Photon;
using AlbionLocal.Server.Protocol;
using AlbionLocal.Server.Systems;

namespace AlbionLocal.Server.Network;

/// <summary>
/// Alliances (analysis/work/stageP/hideouts/SPEC.md §2.1; the rules in <see cref="Alliances"/>): create, disband, leave, invite, answer,
/// cancel, kick, the member list
/// and the detail info. The responses go with the request's id (the client's callbacks); an op without a response class gets the
/// generic one with its return code.
/// </summary>
public sealed partial class WorldSession
{
    /// <summary>AllianceCreate (op 208, 0 name, 1 tag [H order]): rc 0 without keys after the guild's GuildUpdate kind 5, else 8320-8330 (8330 with key 0 = the cooldown's end).</summary>
    [Handles(OperationCodes.AllianceCreate, Status = SessionStatus.InWorld)]
    private AlbionResponse? AllianceCreate(AllianceCreateRequest req)
    {
        if (Player is not { } player) return null;
        var rc = world.Alliances.Create(player, req.an, req.ao, out var end);
        if (rc != 0) Log.Info(Name, $"'{player.Character.Name}' AllianceCreate refused: {rc}");
        return new AllianceCreateResponse { ReturnCode = rc, ajo = end };
    }

    /// <summary>AllianceDisband (op 209, 0 alliance): rc 0 (the founder guild) or 1.</summary>
    [Handles(OperationCodes.AllianceDisband, Status = SessionStatus.InWorld)]
    private AlbionResponse? AllianceDisband(AllianceDisbandRequest req) =>
        Player is { } player ? new GenericResponse { ReturnCode = world.Alliances.Disband(player, req.ap) } : null;

    /// <summary>AllianceLeave (op 215, 0 alliance): rc 0 or 1 (the founder guild with others: disband instead).</summary>
    [Handles(OperationCodes.AllianceLeave, Status = SessionStatus.InWorld)]
    private AlbionResponse? AllianceLeave(AllianceLeaveRequest req) =>
        Player is { } player ? new GenericResponse { ReturnCode = world.Alliances.Leave(player, req.av) } : null;

    /// <summary>AllianceInvite (op 211, 0 alliance, 1 guild name): rc 0 (the invitation mail is in the guild's mailbox) or 8440-8447.</summary>
    [Handles(OperationCodes.AllianceInvite, Status = SessionStatus.InWorld)]
    private AlbionResponse? AllianceInvite(AllianceInviteRequest req)
    {
        if (Player is not { } player) return null;
        var rc = world.Alliances.Invite(player, req._as, req.at);
        if (rc != 0) Log.Info(Name, $"'{player.Character.Name}' AllianceInvite refused: {rc}");
        return new GenericResponse { ReturnCode = rc };
    }

    /// <summary>AllianceAnswerInvitation (op 212, 0 the invitation mail's id, 1 accept): rc 0, or 8481-8486 (8485/8486 with key 0 = the cooldown's end), 1 for no such invitation.</summary>
    [Handles(OperationCodes.AllianceAnswerInvitation, Status = SessionStatus.InWorld)]
    private AlbionResponse? AllianceAnswerInvitation(AllianceAnswerInvitationRequest req)
    {
        if (Player is not { } player) return null;
        var rc = world.Alliances.Answer(player, req.ak, req.al, out var end);
        if (rc != 0) Log.Info(Name, $"'{player.Character.Name}' AllianceAnswerInvitation refused: {rc}");
        return new AllianceAnswerInvitationResponse { ReturnCode = rc, ajn = end };
    }

    /// <summary>AllianceCancelInvitation (op 213, 0 the invited guild): rc 0 or 8520-8523.</summary>
    [Handles(OperationCodes.AllianceCancelInvitation, Status = SessionStatus.InWorld)]
    private AlbionResponse? AllianceCancelInvitation(AllianceCancelInvitationRequest req) =>
        Player is { } player ? new GenericResponse { ReturnCode = world.Alliances.Cancel(player, req.am) } : null;

    /// <summary>AllianceKickGuild (op 214, 0 the guild): rc 0 or 8560-8564.</summary>
    [Handles(OperationCodes.AllianceKickGuild, Status = SessionStatus.InWorld)]
    private AlbionResponse? AllianceKickGuild(AllianceKickGuildRequest req) =>
        Player is { } player ? new GenericResponse { ReturnCode = world.Alliances.Kick(player, req.au) } : null;

    /// <summary>AllianceGetMemberInfos (op 210, 0 alliance): the member list (<see cref="Alliances.MemberInfos"/>), rc 1 for an unknown alliance.</summary>
    [Handles(OperationCodes.AllianceGetMemberInfos, Status = SessionStatus.InWorld)]
    private AlbionResponse? AllianceGetMemberInfos(AllianceGetMemberInfosRequest req) =>
        Player is not { } player ? null : world.Alliances.MemberInfos(player, req.ar) ?? new AllianceGetMemberInfosResponse { ReturnCode = Alliances.Refused };

    /// <summary>AllianceGetDetailInfo (op 217, 0 the alliance's name or tag): <see cref="Alliances.DetailInfo"/>, rc 1 for none.</summary>
    [Handles(OperationCodes.AllianceGetDetailInfo, Status = SessionStatus.InWorld)]
    private AlbionResponse? AllianceGetDetailInfo(AllianceGetDetailInfoRequest req) =>
        Player is null ? null : world.Alliances.DetailInfo(req.aq) ?? new AllianceGetDetailInfoResponse { ReturnCode = Alliances.Refused };
}
