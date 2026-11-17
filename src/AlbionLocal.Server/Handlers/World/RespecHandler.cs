using AlbionLocal.Photon;
using AlbionLocal.Server.Protocol;

namespace AlbionLocal.Server.Network;

/// <summary>The destiny board re-spec (stageR items SPEC §3, <see cref="Systems.FameCredits"/>).</summary>
public sealed partial class WorldSession
{
    /// <summary>
    /// ReSpecAchievement (147, aaj {0 node, 1 its current level, 2 the target level, 3 the silver the client expects}; sent by cy8.u
    /// 0x1809FCFF0 with a callback that reads only the return code): a plain response, ReturnCode 0 when done (the client then sets the
    /// level itself), else the re-spec error code. UpdateReSpecPoints and UpdateMoney go out before it.
    /// </summary>
    [Handles(OperationCodes.ReSpecAchievement, Status = SessionStatus.InWorld)]
    private AlbionResponse? ReSpecAchievement(ReSpecAchievementRequest req)
    {
        if (Player is not { } player) return null;
        var code = Systems.FameCredits.ReSpec(player, req.aay, req.aaz, req.aa0, req.aa1, out var why);
        Log.Info(Name, $"'{player.Character.Name}' re-spec node {req.aay} {req.aaz} → {req.aa0}: {(code == 0 ? "done" : $"refused ({code})")}, {why}");
        return new GenericResponse { ReturnCode = code };
    }
}
