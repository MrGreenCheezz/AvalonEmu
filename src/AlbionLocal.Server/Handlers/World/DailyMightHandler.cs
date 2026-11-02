using AlbionLocal.Photon;
using AlbionLocal.Server.Protocol;
using AlbionLocal.Server.Systems;

namespace AlbionLocal.Server.Network;

public sealed partial class WorldSession
{
    [Handles(OperationCodes.DailyMightBonus, Status = SessionStatus.InWorld)]
    private DailyMightBonusResponse DailyMightBonus(DailyMightBonusRequest req)
    {
        if (Player is not { } p) return PvpChallenge.EmptyDaily(1);
        var response = PvpChallenge.Daily(p);
        if (response.ReturnCode != 0 && response.DebugMessage is { } why)
            SendEvent(new SystemMessageEvent { d0p = why, d0q = 0 });
        return response;
    }

    [Handles(OperationCodes.ClaimDailyMightBonus, Status = SessionStatus.InWorld)]
    private GenericResponse ClaimDailyMightBonus(ClaimDailyMightBonusRequest req)
    {
        if (Player is not { } p) return new() { ReturnCode = 1 };
        var why = PvpChallenge.DailyClaimRefusal(p);
        SendEvent(new SystemMessageEvent { d0p = why, d0q = 0 });
        return new() { ReturnCode = 1, DebugMessage = why };
    }
}
