using AlbionLocal.Server.Protocol;
using AlbionLocal.Server.Systems;

namespace AlbionLocal.Server.Network;

public sealed partial class WorldSession
{
    [Handles(OperationCodes.ChangeAvatar, Status = SessionStatus.InWorld)]
    private ChangeAvatarResponse ChangeAvatar(ChangeAvatarRequest req) => Player is { } p
        ? Avatars.Change(p, req.d7, req.d8)
        : new() { ReturnCode = 1 };
}
