using AlbionLocal.Photon;
using AlbionLocal.Server.Protocol;

namespace AlbionLocal.Server.Network;

/// <summary>The hunts' operations 496-500 (analysis/work/stageN/hunts/SPEC.md §2); the rules are in <see cref="Systems.Hunts"/>.</summary>
public sealed partial class WorldSession
{
    /// <summary>PartyAnswerStartHuntRequest (op 496, yc {0 Guid, 1 answer}): party members join a hunt at its start here, so only rc 0.</summary>
    [Handles(OperationCodes.PartyAnswerStartHuntRequest, Status = SessionStatus.InWorld)]
    private AlbionResponse? PartyAnswerStartHuntRequest(PartyAnswerStartHuntRequestRequest req) => new GenericResponse { ReturnCode = 0 };

    /// <summary>HuntAbort (op 497, v0 {0 hunt Guid}): the player gives its hunt up (HuntAborted reason 11 to every member); rc 0, 1 without one.</summary>
    [Handles(OperationCodes.HuntAbort, Status = SessionStatus.InWorld)]
    private AlbionResponse? HuntAbort(HuntAbortRequest req) =>
        new GenericResponse { ReturnCode = Player is { } p && world.Hunts.AbortBy(p, req.qd) ? (short)0 : (short)1 };

    /// <summary>UseFindTrackSpellFromItemPrepare (op 498, ace {0 long}): the tracking toolkit's find cast (<see cref="Systems.Hunts.Find"/>).</summary>
    [Handles(OperationCodes.UseFindTrackSpellFromItemPrepare, Status = SessionStatus.InWorld)]
    private AlbionResponse? UseFindTrackSpellFromItemPrepare(UseFindTrackSpellFromItemPrepareRequest req)
    {
        if (Player is not { } player) return new GenericResponse { ReturnCode = 1 };
        var (code, why) = world.Hunts.Find(player);
        if (why is not null) Log.Info(Name, $"'{player.Character.Name}' find track ({req.ah9}) refused: {why}");
        return new GenericResponse { ReturnCode = code };
    }

    /// <summary>InteractWithTrackStart (op 499, v2 {0 time, 1 long, 2 long}): reading a hunt track (either id names it).</summary>
    [Handles(OperationCodes.InteractWithTrackStart, Status = SessionStatus.InWorld)]
    private AlbionResponse? InteractWithTrackStart(InteractWithTrackStartRequest req)
    {
        if (Player is not { } player) return new GenericResponse { ReturnCode = 1 };
        var (code, why) = world.Hunts.Read(player, req.qf, req.qg);
        if (why is not null) Log.Info(Name, $"'{player.Character.Name}' read track {req.qf}/{req.qg} refused: {why}");
        return new GenericResponse { ReturnCode = code };
    }

    /// <summary>InteractWithTrackCancel (op 500, v1 {0 time, 1 track}): stops the reading; rc 0.</summary>
    [Handles(OperationCodes.InteractWithTrackCancel, Status = SessionStatus.InWorld)]
    private AlbionResponse? InteractWithTrackCancel(InteractWithTrackCancelRequest req)
    {
        if (Player is { } player) world.Hunts.CancelRead(player, "cancelled");
        return new GenericResponse { ReturnCode = 0 };
    }
}
