using AlbionLocal.Server.Protocol;
using AlbionLocal.Server.Systems;

namespace AlbionLocal.Server.Network;

public sealed partial class WorldSession
{
    [Handles(OperationCodes.EventBasedPopupAddSeen, Status = SessionStatus.InWorld)]
    private AlbionResponse? EventBasedPopupAddSeen(EventBasedPopupAddSeenRequest request)
    {
        EventBasedPopups.AddSeen(world, Player!.Character, request);
        return null;
    }
}
