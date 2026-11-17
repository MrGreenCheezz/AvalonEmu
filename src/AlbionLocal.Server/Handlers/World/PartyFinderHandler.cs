using AlbionLocal.Photon;
using AlbionLocal.Server.Protocol;

namespace AlbionLocal.Server.Network;

/// <summary>
/// StageR party finder (analysis/work/stageR/party/SPEC.md §5; the rules in Systems/PartyFinder.cs), ops 359-371 of build 345319. Where the
/// official server answered (capture 002835, build 345402, matched by shape): 359, 360 and 371 get no response, 368 rc 0 with the request
/// id, 369 rc 0 without it. The response goes out before the events it causes, as captured (368 → response, then Update).
/// </summary>
public sealed partial class WorldSession
{
    /// <summary>The response now, ahead of the events the request causes; with <paramref name="requestId"/> false without key 255.</summary>
    private Action<short> FinderReply(OperationCodes code, bool requestId = true) =>
        rc => SendResponse(code, new GenericResponse { ReturnCode = rc }, requestId ? RequestId : null, RequestEncrypted);

    /// <summary>PartyFinderRegisterForUpdates (359, yo: 0 Activity): FullUpdate (370), then the activity's updates. No response.</summary>
    [Handles(OperationCodes.PartyFinderRegisterForUpdates, Status = SessionStatus.InWorld)]
    private AlbionResponse? PartyFinderRegisterForUpdates(PartyFinderRegisterForUpdatesRequest req)
    {
        if (Player is { } p) world.Parties.Finder.Register(p, req.xn);
        return null;
    }

    /// <summary>PartyFinderUnregisterForUpdates (360, yr: 0 Activity). No response.</summary>
    [Handles(OperationCodes.PartyFinderUnregisterForUpdates, Status = SessionStatus.InWorld)]
    private AlbionResponse? PartyFinderUnregisterForUpdates(PartyFinderUnregisterForUpdatesRequest req)
    {
        if (Player is { } p) world.Parties.Finder.Unregister(p, req.xo);
        return null;
    }

    /// <summary>PartyFinderEnlistNewPartySearch (361, yk): rc 0 or 14444, then Update (371) kind 0.</summary>
    [Handles(OperationCodes.PartyFinderEnlistNewPartySearch, Status = SessionStatus.InWorld)]
    private AlbionResponse? PartyFinderEnlist(PartyFinderEnlistNewPartySearchRequest req)
    {
        if (Player is { } p)
            world.Parties.Finder.Enlist(p, req.w5, req.w6, req.w7, req.w8, req.w9, req.xa, req.xb, FinderReply(OperationCodes.PartyFinderEnlistNewPartySearch));
        return null;
    }

    /// <summary>PartyFinderDeletePartySearch (362, yj): rc 0, then the listing goes.</summary>
    [Handles(OperationCodes.PartyFinderDeletePartySearch, Status = SessionStatus.InWorld)]
    private AlbionResponse? PartyFinderDelete(PartyFinderDeletePartySearchRequest req)
    {
        if (Player is { } p) world.Parties.Finder.Delete(p, FinderReply(OperationCodes.PartyFinderDeletePartySearch));
        return null;
    }

    /// <summary>PartyFinderChangePartySearch (363, yh, the fields of 361): as an enlist of the same leader (Update kind 1).</summary>
    [Handles(OperationCodes.PartyFinderChangePartySearch, Status = SessionStatus.InWorld)]
    private AlbionResponse? PartyFinderChange(PartyFinderChangePartySearchRequest req)
    {
        if (Player is { } p)
            world.Parties.Finder.Enlist(p, req.ww, req.wx, req.wy, req.wz, req.w0, req.w1, req.w2, FinderReply(OperationCodes.PartyFinderChangePartySearch));
        return null;
    }

    /// <summary>PartyFinderChangeRole (364, yi: 0 int[3] roles, 1 Guid): rc 0, then the updates that show the roles.</summary>
    [Handles(OperationCodes.PartyFinderChangeRole, Status = SessionStatus.InWorld)]
    private AlbionResponse? PartyFinderChangeRole(PartyFinderChangeRoleRequest req)
    {
        if (Player is { } p) world.Parties.Finder.ChangeRole(p, req.w3, FinderReply(OperationCodes.PartyFinderChangeRole));
        return null;
    }

    /// <summary>PartyFinderApplyForGroup (365, yg: 0 listing key): rc 0 (joined), 14611 (pending) or an RcOpPartyFinderApplyForGroup error.</summary>
    [Handles(OperationCodes.PartyFinderApplyForGroup, Status = SessionStatus.InWorld)]
    private AlbionResponse? PartyFinderApply(PartyFinderApplyForGroupRequest req)
    {
        if (Player is not { } p) return null;
        var rc = world.Parties.Finder.Apply(p, req.wv);
        if (rc is not (0 or Systems.PartyFinder.ApplyPending)) Log.Info(Name, $"'{p.Character.Name}' PartyFinderApplyForGroup refused: {rc}");
        return new PartyFinderApplyForGroupResponse { ReturnCode = rc };
    }

    /// <summary>PartyFinderAcceptOrDeclineApplyForGroup (366, yf: 0 applicant Guid, 1 accept): rc 0, 14601, 14602 or 14604.</summary>
    [Handles(OperationCodes.PartyFinderAcceptOrDeclineApplyForGroup, Status = SessionStatus.InWorld)]
    private AlbionResponse? PartyFinderAnswer(PartyFinderAcceptOrDeclineApplyForGroupRequest req) =>
        Player is { } p ? new GenericResponse { ReturnCode = world.Parties.Finder.Answer(p, req.wt, req.wu) } : null;

    /// <summary>PartyFinderGetEquipmentSnapshot (367, ym: 0 player name): EquipmentSnapshot (373). No response [assume].</summary>
    [Handles(OperationCodes.PartyFinderGetEquipmentSnapshot, Status = SessionStatus.InWorld)]
    private AlbionResponse? PartyFinderGetEquipmentSnapshot(PartyFinderGetEquipmentSnapshotRequest req)
    {
        if (Player is { } p) world.Parties.Finder.Snapshot(p, req.xf);
        return null;
    }

    /// <summary>PartyFinderRegisterApplicants (368, yn): rc 0 with the request id (as captured), then Update list 1 kind 0.</summary>
    [Handles(OperationCodes.PartyFinderRegisterApplicants, Status = SessionStatus.InWorld)]
    private AlbionResponse? PartyFinderRegisterApplicants(PartyFinderRegisterApplicantsRequest req)
    {
        if (Player is { } p)
            world.Parties.Finder.RegisterApplicant(p, req.xg, req.xh, req.xi, req.xj, req.xk, req.xl, req.xm, FinderReply(OperationCodes.PartyFinderRegisterApplicants));
        return null;
    }

    /// <summary>PartyFinderUnregisterApplicants (369, yq): rc 0 without the request id (as captured), then Update list 1 kind 3.</summary>
    [Handles(OperationCodes.PartyFinderUnregisterApplicants, Status = SessionStatus.InWorld)]
    private AlbionResponse? PartyFinderUnregisterApplicants(PartyFinderUnregisterApplicantsRequest req)
    {
        if (Player is { } p) world.Parties.Finder.UnregisterApplicant(p, FinderReply(OperationCodes.PartyFinderUnregisterApplicants, requestId: false));
        return null;
    }

    /// <summary>PartyFinderFulltextSearch (370, yl: 0 Activity, 1 text, 2 int): a FullUpdate of the matching listings. No response [assume].</summary>
    [Handles(OperationCodes.PartyFinderFulltextSearch, Status = SessionStatus.InWorld)]
    private AlbionResponse? PartyFinderFulltextSearch(PartyFinderFulltextSearchRequest req)
    {
        if (Player is { } p) world.Parties.Finder.Search(p, req.xc, req.xd);
        return null;
    }

    /// <summary>PartyFinderRequestEquipmentSnapshot (371, yp): the player's entries again with its current item power. No response (as captured).</summary>
    [Handles(OperationCodes.PartyFinderRequestEquipmentSnapshot, Status = SessionStatus.InWorld)]
    private AlbionResponse? PartyFinderRequestEquipmentSnapshot(PartyFinderRequestEquipmentSnapshotRequest req)
    {
        if (Player is { } p) world.Parties.Finder.Refresh(p);
        return null;
    }
}
