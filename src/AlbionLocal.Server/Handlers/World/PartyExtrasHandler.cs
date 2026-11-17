using AlbionLocal.Photon;
using AlbionLocal.Server.Protocol;

namespace AlbionLocal.Server.Network;

/// <summary>
/// StageR party (analysis/work/stageR/party/SPEC.md; the rules in Systems/PartyExtras.cs): ready checks, party navigation, the
/// FW reinforcements flag and join requests. Every one of these requests carries a request id, so each gets a response.
/// </summary>
public sealed partial class WorldSession
{
    /// <summary>PartyReadyCheckBegin (op 512, yx: no keys): rc 0 or 20481-20484 (the client's enum na, shown by df9.k(na)).</summary>
    [Handles(OperationCodes.PartyReadyCheckBegin, Status = SessionStatus.InWorld)]
    private AlbionResponse? PartyReadyCheckBegin(PartyReadyCheckBeginRequest req) =>
        Player is { } player ? new GenericResponse { ReturnCode = world.Parties.BeginReadyCheck(player) } : null;

    /// <summary>PartyReadyCheckUpdate (op 513, yy: 0 bool ready): rc 0 or 20520.</summary>
    [Handles(OperationCodes.PartyReadyCheckUpdate, Status = SessionStatus.InWorld)]
    private AlbionResponse? PartyReadyCheckUpdate(PartyReadyCheckUpdateRequest req) =>
        Player is { } player ? new GenericResponse { ReturnCode = world.Parties.AnswerReadyCheck(player, req.xy) } : null;

    /// <summary>SetPartyNavigation (op 547, aa7: 0 destination, 1 Guid, 2/3 flags): an empty response (ai4), rc 0 or 9241/9242.</summary>
    [Handles(OperationCodes.SetPartyNavigation, Status = SessionStatus.InWorld)]
    private AlbionResponse? SetPartyNavigation(SetPartyNavigationRequest req)
    {
        if (Player is not { } player) return null;
        var rc = world.Parties.SetNavigation(player, req.afy, req.af0, req.af1);
        return rc == 0 ? new SetPartyNavigationResponse() : new GenericResponse { ReturnCode = rc };
    }

    /// <summary>EndPartyNavigation (op 548, r4: 0 Guid): an empty response (aeu), rc 0 or 9241/9242.</summary>
    [Handles(OperationCodes.EndPartyNavigation, Status = SessionStatus.InWorld)]
    private AlbionResponse? EndPartyNavigation(EndPartyNavigationRequest req)
    {
        if (Player is not { } player) return null;
        var rc = world.Parties.EndNavigation(player);
        return rc == 0 ? new EndPartyNavigationResponse() : new GenericResponse { ReturnCode = rc };
    }

    /// <summary>PartyChangeFactionWarfareRequestReinforcementsSetting (op 235, yd: 0 bool): rc 0 or 9202 (the client ignores the response).</summary>
    [Handles(OperationCodes.PartyChangeFactionWarfareRequestReinforcementsSetting, Status = SessionStatus.InWorld)]
    private AlbionResponse? PartyChangeFwReinforcements(PartyChangeFactionWarfareRequestReinforcementsSettingRequest req) =>
        Player is { } player ? new GenericResponse { ReturnCode = world.Parties.SetFwReinforcements(player, req.wr) } : null;

    /// <summary>PartyRequestJoin (op 226, yz: 0 Guid, 1 name): rc 0 {0 the target's name} (ah5), else the error (f8l.b).</summary>
    [Handles(OperationCodes.PartyRequestJoin, Status = SessionStatus.InWorld)]
    private AlbionResponse? PartyRequestJoin(PartyRequestJoinRequest req)
    {
        if (Player is not { } player) return null;
        var rc = world.Parties.RequestJoin(player, req.xz, req.x0, out var name);
        if (rc != 0) Log.Info(Name, $"'{player.Character.Name}' PartyRequestJoin refused: {rc}");
        return rc == 0 ? new PartyRequestJoinResponse { bgs = name } : new GenericResponse { ReturnCode = rc };
    }

    /// <summary>PartyAnswerJoinRequest (op 228, yb: 0 accept, 1 auto, 2 requester Guid, 3 reason): an empty response, rc 0 or 9120.</summary>
    [Handles(OperationCodes.PartyAnswerJoinRequest, Status = SessionStatus.InWorld)]
    private AlbionResponse? PartyAnswerJoinRequest(PartyAnswerJoinRequestRequest req) =>
        Player is { } player ? new GenericResponse { ReturnCode = world.Parties.AnswerJoinRequest(player, req.wl, req.wm, req.wn, req.wo) } : null;
}
