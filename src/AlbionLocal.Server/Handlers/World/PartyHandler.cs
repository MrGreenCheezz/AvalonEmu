using AlbionLocal.Photon;
using AlbionLocal.Server.Protocol;
using AlbionLocal.Server.Systems;

namespace AlbionLocal.Server.Network;

/// <summary>
/// Parties (analysis/work/stageD/party/SPEC.md §1, §7; the rules in <see cref="Parties"/>): invite, answer, leave, kick, leader,
/// loot mode, marks, roles and a member's cluster. All but PartySetRole carry a request id, so each gets a response (rc 0 or
/// the client's error code). Join requests (226/228) and ready checks are not covered (SPEC §8).
/// The native finder has its own handlers in PartyFinderHandler and reuses this membership system.
/// </summary>
public sealed partial class WorldSession
{
    /// <summary>PartyInvitePlayer (op 225, ys: 0 target Guid, empty when typed by name, 1 name, 2 bool unknown): rc 0 {0 the target's name} or the error (ah2).</summary>
    [Handles(OperationCodes.PartyInvitePlayer, Status = SessionStatus.InWorld)]
    private AlbionResponse? PartyInvitePlayer(PartyInvitePlayerRequest req)
    {
        if (Player is not { } player) return null;
        var rc = world.Parties.Invite(player, req.xp, req.xq, out var name);
        if (rc != 0) Log.Info(Name, $"'{player.Character.Name}' PartyInvitePlayer refused: {rc}");
        return rc == 0 ? new PartyInvitePlayerResponse { bgo = name } : new GenericResponse { ReturnCode = rc };
    }

    /// <summary>PartyAnswerInvitation (op 227, ya: 0 accept, 1 auto, 2 PartyAnswerDeclineReason): an empty response, rc 0 or 9080.</summary>
    [Handles(OperationCodes.PartyAnswerInvitation, Status = SessionStatus.InWorld)]
    private AlbionResponse? PartyAnswerInvitation(PartyAnswerInvitationRequest req) =>
        Player is { } player ? new GenericResponse { ReturnCode = world.Parties.AnswerInvitation(player, req.wi, req.wj, req.wk) } : null;

    /// <summary>PartyLeave (op 229, no fields; sent also outside a party): an empty response, rc 0 or 9161.</summary>
    [Handles(OperationCodes.PartyLeave, Status = SessionStatus.InWorld)]
    private AlbionResponse? PartyLeave(PartyLeaveRequest req) =>
        Player is { } player ? new GenericResponse { ReturnCode = world.Parties.Leave(player) } : null;

    /// <summary>PartyKickPlayer (op 230, yt: 0 Guid, 1 name): rc 0 {0 the kicked one's name} (ah3) or 9200-9202.</summary>
    [Handles(OperationCodes.PartyKickPlayer, Status = SessionStatus.InWorld)]
    private AlbionResponse? PartyKickPlayer(PartyKickPlayerRequest req)
    {
        if (Player is not { } player) return null;
        var rc = world.Parties.Kick(player, req.xs, req.xt, out var name);
        return rc == 0 ? new PartyKickPlayerResponse { bgq = name } : new GenericResponse { ReturnCode = rc };
    }

    /// <summary>PartyMakeLeader (op 231, yv: 0 Guid, 1 name): rc 0 {0 the new leader's name} (ah4) or 9240-9242.</summary>
    [Handles(OperationCodes.PartyMakeLeader, Status = SessionStatus.InWorld)]
    private AlbionResponse? PartyMakeLeader(PartyMakeLeaderRequest req)
    {
        if (Player is not { } player) return null;
        var rc = world.Parties.MakeLeader(player, req.xu, req.xv, out var name);
        return rc == 0 ? new PartyMakeLeaderResponse { bgr = name } : new GenericResponse { ReturnCode = rc };
    }

    /// <summary>PartyChangeLootSetting (op 232, ye: 0 PartyLootModus): an empty response (the client ignores it), rc 0 or 9202.</summary>
    [Handles(OperationCodes.PartyChangeLootSetting, Status = SessionStatus.InWorld)]
    private AlbionResponse? PartyChangeLootSetting(PartyChangeLootSettingRequest req) =>
        Player is { } player ? new GenericResponse { ReturnCode = world.Parties.ChangeLootSetting(player, req.ws) } : null;

    /// <summary>PartyMarkObject (op 233, yw: 0 slot, 1 mark): rc 0.</summary>
    [Handles(OperationCodes.PartyMarkObject, Status = SessionStatus.InWorld)]
    private AlbionResponse? PartyMarkObject(PartyMarkObjectRequest req)
    {
        if (Player is not { } player) return null;
        world.Parties.MarkObject(player, req.xw, req.xx);
        return new GenericResponse();
    }

    /// <summary>PartySetRole (op 234, y0: 0 role; no request id, no response).</summary>
    [Handles(OperationCodes.PartySetRole, Status = SessionStatus.InWorld)]
    private AlbionResponse? PartySetRole(PartySetRoleRequest req)
    {
        if (Player is { } player) world.Parties.SetRole(player, req.x1);
        return null;
    }

    /// <summary>GetPartyMemberCluster (op 546, u4: 0 name): rc 0 {0 the member's cluster id} (agq), 9000 when it is no member that plays [choice].</summary>
    [Handles(OperationCodes.GetPartyMemberCluster, Status = SessionStatus.InWorld)]
    private AlbionResponse? GetPartyMemberCluster(GetPartyMemberClusterRequest req)
    {
        if (Player is not { } player) return null;
        return world.Parties.MemberCluster(player, req.o4) is { } cluster
            ? new GetPartyMemberClusterResponse { a4z = cluster }
            : new GenericResponse { ReturnCode = Parties.PlayerNotFound };
    }
}
