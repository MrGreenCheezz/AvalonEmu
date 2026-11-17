using AlbionLocal.Photon;
using AlbionLocal.Server.Protocol;
using AlbionLocal.Server.Systems;

namespace AlbionLocal.Server.Network;

/// <summary>
/// The smugglers' crates and the carried objects (analysis/work/stageR/smugglers/SPEC.md §3, §4): the pile pickup (op 518 / 519), the delivery
/// at a Smuggler Stash (op 304 / 305) and the pickup of a dropped carriable (op 439 / 440); the rules are in <see cref="Smugglers"/> and
/// <see cref="Carriables"/>. None of them has a response class: the client's channel class (cyw, cwv, cyv) reads the RC only, 0 = accepted
/// (the events follow), any other = refused (its action state reset) [infer from cyv.ai7 0x180A060D0, the same pattern in cyw and cwv].
/// </summary>
public sealed partial class WorldSession
{
    /// <summary>PickupFromPiledObjectStart (op 518, y5 {0 time, 1 the pile}).</summary>
    [Handles(OperationCodes.PickupFromPiledObjectStart, Status = SessionStatus.InWorld)]
    private AlbionResponse? PickupFromPiledObjectStart(PickupFromPiledObjectStartRequest req)
    {
        if (Player is not { } player) return new GenericResponse { ReturnCode = Smugglers.Refused };
        if (world.Smugglers.StartPile(player, req.x4) is not { } why) return new GenericResponse();
        Log.Info(Name, $"'{player.Character.Name}' PickupFromPiledObjectStart #{req.x4} refused: {why}");
        return new GenericResponse { ReturnCode = Smugglers.Refused };
    }

    /// <summary>PickupFromPiledObjectCancel (op 519, y4: no keys).</summary>
    [Handles(OperationCodes.PickupFromPiledObjectCancel, Status = SessionStatus.InWorld)]
    private AlbionResponse? PickupFromPiledObjectCancel(PickupFromPiledObjectCancelRequest req)
    {
        if (Player is { } player) world.Smugglers.Cancel(player, "cancelled by the client");
        return new GenericResponse();
    }

    /// <summary>DeliverCarriableObjectStart (op 304, rw {0 time, 1 the destination}).</summary>
    [Handles(OperationCodes.DeliverCarriableObjectStart, Status = SessionStatus.InWorld)]
    private AlbionResponse? DeliverCarriableObjectStart(DeliverCarriableObjectStartRequest req)
    {
        if (Player is not { } player) return new GenericResponse { ReturnCode = Smugglers.Refused };
        if (world.Smugglers.StartDeliver(player, req.jn) is not { } why) return new GenericResponse();
        Log.Info(Name, $"'{player.Character.Name}' DeliverCarriableObjectStart #{req.jn} refused: {why}");
        return new GenericResponse { ReturnCode = Smugglers.Refused };
    }

    /// <summary>DeliverCarriableObjectCancel (op 305, rv: no keys).</summary>
    [Handles(OperationCodes.DeliverCarriableObjectCancel, Status = SessionStatus.InWorld)]
    private AlbionResponse? DeliverCarriableObjectCancel(DeliverCarriableObjectCancelRequest req)
    {
        if (Player is { } player) world.Smugglers.Cancel(player, "cancelled by the client");
        return new GenericResponse();
    }

    /// <summary>PickupCarriableObjectStart (op 439, y3 {0 time, 1 the carriable on the ground}).</summary>
    [Handles(OperationCodes.PickupCarriableObjectStart, Status = SessionStatus.InWorld)]
    private AlbionResponse? PickupCarriableObjectStart(PickupCarriableObjectStartRequest req)
    {
        if (Player is not { } player) return new GenericResponse { ReturnCode = Carriables.Refused };
        if (world.Carriables.StartPickup(player, req.x3) is not { } why) return new GenericResponse();
        Log.Info(Name, $"'{player.Character.Name}' PickupCarriableObjectStart #{req.x3} refused: {why}");
        return new GenericResponse { ReturnCode = Carriables.Refused };
    }

    /// <summary>PickupCarriableObjectCancel (op 440, y2 {0 time}).</summary>
    [Handles(OperationCodes.PickupCarriableObjectCancel, Status = SessionStatus.InWorld)]
    private AlbionResponse? PickupCarriableObjectCancel(PickupCarriableObjectCancelRequest req)
    {
        if (Player is { } player) world.Carriables.CancelPickup(player, "cancelled by the client");
        return new GenericResponse();
    }
}
