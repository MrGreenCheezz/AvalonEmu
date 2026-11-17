using AlbionLocal.Photon;
using AlbionLocal.Server.Protocol;

namespace AlbionLocal.Server.Network;

/// <summary>
/// Overcharge (stageR items SPEC §2, <see cref="Systems.Overcharge"/>): two requests the client never waits an answer for (c32.br / c32.bs
/// send them and the client updates from OverChargeStatus / OverChargeEnd). No response.
/// </summary>
public sealed partial class WorldSession
{
    /// <summary>OverCharge (355, x8 {0 the enabled slot mask}): the next stack on the enabled slots; a refusal only re-sends the status.</summary>
    [Handles(OperationCodes.OverCharge, Status = SessionStatus.InWorld)]
    private AlbionResponse? OverCharge(OverChargeRequest req)
    {
        if (Player is not { } player) return null;
        if (Systems.Overcharge.Start(player, req.wg) is { } why) Log.Info(Name, $"'{player.Character.Name}' overcharge (mask {req.wg}) refused: {why}");
        return null;
    }

    /// <summary>OverChargeEnd (356, x9 {0 slot mask}): those slots end now, with their break roll.</summary>
    [Handles(OperationCodes.OverChargeEnd, Status = SessionStatus.InWorld)]
    private AlbionResponse? OverChargeEnd(OverChargeEndRequest req)
    {
        if (Player is { } player) Systems.Overcharge.End(player, req.wh);
        return null;
    }
}
