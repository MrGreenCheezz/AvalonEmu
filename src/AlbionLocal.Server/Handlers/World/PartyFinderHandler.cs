using AlbionLocal.Photon;
using AlbionLocal.Server.Protocol;

namespace AlbionLocal.Server.Network;

public sealed partial class WorldSession
{
    [Handles(OperationCodes.PartyFinderRegisterForUpdates, Status = SessionStatus.InWorld)]
    private AlbionResponse? PartyFinderRegisterForUpdates(PartyFinderRegisterForUpdatesRequest req)
    {
        if (Player is not { } p) return null;
        var rc = world.PartyFinder.Watch(p, req.xn);
        return rc == 0 ? null : new GenericResponse { ReturnCode = rc };
    }

    [Handles(OperationCodes.PartyFinderUnregisterForUpdates, Status = SessionStatus.InWorld)]
    private AlbionResponse? PartyFinderUnregisterForUpdates(PartyFinderUnregisterForUpdatesRequest req)
    {
        if (Player is { } p) world.PartyFinder.Unwatch(p, req.xo);
        return null;
    }

    [Handles(OperationCodes.PartyFinderFulltextSearch, Status = SessionStatus.InWorld)]
    private AlbionResponse? PartyFinderFulltextSearch(PartyFinderFulltextSearchRequest req)
    {
        if (Player is not { } p) return null;
        // key 2 is the guild/alliance visibility filter, not a page number (PartyFinderFindView +0x7C).
        var rc = world.PartyFinder.Watch(p, req.xc, req.xd, req.xe);
        return rc == 0 ? null : new GenericResponse { ReturnCode = rc };
    }

    [Handles(OperationCodes.PartyFinderEnlistNewPartySearch, Status = SessionStatus.InWorld)]
    private AlbionResponse? PartyFinderEnlistNewPartySearch(PartyFinderEnlistNewPartySearchRequest req) =>
        Player is { } p ? new GenericResponse { ReturnCode = world.PartyFinder.Enlist(p, req.w5, req.w6, req.w7, req.w8, req.w9, req.xa, req.xb, false) } : null;

    [Handles(OperationCodes.PartyFinderChangePartySearch, Status = SessionStatus.InWorld)]
    private AlbionResponse? PartyFinderChangePartySearch(PartyFinderChangePartySearchRequest req) =>
        Player is { } p ? new GenericResponse { ReturnCode = world.PartyFinder.Enlist(p, req.ww, req.wx, req.wy, req.wz, req.w0, req.w1, req.w2, true) } : null;

    [Handles(OperationCodes.PartyFinderDeletePartySearch, Status = SessionStatus.InWorld)]
    private AlbionResponse? PartyFinderDeletePartySearch(PartyFinderDeletePartySearchRequest req) =>
        Player is { } p ? new GenericResponse { ReturnCode = world.PartyFinder.Delete(p, false) } : null;

    [Handles(OperationCodes.PartyFinderRegisterApplicants, Status = SessionStatus.InWorld)]
    private AlbionResponse? PartyFinderRegisterApplicants(PartyFinderRegisterApplicantsRequest req) =>
        // 345319 sender A87AB0 writes minimum IP to +0x1C/key2, maximum group size to +0x20/key3.
        Player is { } p ? new GenericResponse { ReturnCode = world.PartyFinder.RegisterApplicant(p, req.xg, req.xh, req.xi, req.xj, req.xk, req.xl, req.xm) } : null;

    [Handles(OperationCodes.PartyFinderUnregisterApplicants, Status = SessionStatus.InWorld)]
    private AlbionResponse? PartyFinderUnregisterApplicants(PartyFinderUnregisterApplicantsRequest req)
    {
        if (Player is not { } p) return null;
        var rc = world.PartyFinder.Delete(p, true);
        // Official capture: unregister has a bare response without key 255; c3i dispatches it by operation.
        SendResponse(OperationCodes.PartyFinderUnregisterApplicants, new GenericResponse { ReturnCode = rc }, null, RequestEncrypted);
        return null;
    }

    [Handles(OperationCodes.PartyFinderChangeRole, Status = SessionStatus.InWorld)]
    private AlbionResponse? PartyFinderChangeRole(PartyFinderChangeRoleRequest req) =>
        Player is { } p ? new GenericResponse { ReturnCode = world.PartyFinder.ChangeRole(p, req.w4, req.w3) } : null;

    [Handles(OperationCodes.PartyFinderApplyForGroup, Status = SessionStatus.InWorld)]
    private AlbionResponse? PartyFinderApplyForGroup(PartyFinderApplyForGroupRequest req) =>
        Player is { } p ? new PartyFinderApplyForGroupResponse { ReturnCode = world.PartyFinder.Apply(p, req.wv) } : null;

    [Handles(OperationCodes.PartyFinderAcceptOrDeclineApplyForGroup, Status = SessionStatus.InWorld)]
    private AlbionResponse? PartyFinderAcceptOrDeclineApplyForGroup(PartyFinderAcceptOrDeclineApplyForGroupRequest req) =>
        Player is { } p ? new GenericResponse { ReturnCode = world.PartyFinder.Answer(p, req.wt, req.wu) } : null;

    /// <summary>
    /// 345319 A8AF80 constructs the inspection model from event 373: type/quality/durability, legendary marker and mastery
    /// per equipment slot. The finder retains those real slot values at session start and manual refresh. Key5 is an
    /// estimated-price cache seed: 0 means unknown, never an invented ItemValue. Mastery uses the same inspect helper.
    /// </summary>
    [Handles(OperationCodes.PartyFinderGetEquipmentSnapshot, Status = SessionStatus.InWorld)]
    private AlbionResponse? PartyFinderGetEquipmentSnapshot(PartyFinderGetEquipmentSnapshotRequest req)
    {
        if (Player is not { } viewer) return null;
        if (world.PartyFinder.Inspect(viewer, req.xf) is not { } snapshot) return new GenericResponse { ReturnCode = InspectNotFound };
        SendEvent(snapshot);
        return null;
    }

    [Handles(OperationCodes.PartyFinderRequestEquipmentSnapshot, Status = SessionStatus.InWorld)]
    private AlbionResponse? PartyFinderRequestEquipmentSnapshot(PartyFinderRequestEquipmentSnapshotRequest req)
    {
        if (Player is { } p) world.PartyFinder.RefreshEquipment(p);
        return null;
    }
}
