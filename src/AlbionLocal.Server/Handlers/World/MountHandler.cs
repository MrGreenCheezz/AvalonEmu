using AlbionLocal.Photon;
using AlbionLocal.Server.Entities;
using AlbionLocal.Server.Protocol;
using AlbionLocal.Server.Systems;

namespace AlbionLocal.Server.Network;

/// <summary>Mounting and dismounting (stageD mounts SPEC §1, §4; the rules in <see cref="Mounts"/>): Mount (op 199) and MountCancel (op 200).</summary>
public sealed partial class WorldSession
{
    /// <summary>
    /// Mount (op 199, xz): 0 the client's GameTimeStamp, 1 the mount item's object id (slot 7), 2 true = mount (absent = dismount),
    /// 3 true = remount at the own mount object. No response class, and the official server answers none (0 of 18 captured): any
    /// response ends the client's mount (cyk.ai7 0x180A01C70, rc not read), so an accepted mount gets none and a refused one
    /// (<see cref="Mounts.Check"/>) a bare response with <see cref="Mounts.Refused"/> and nothing else. A dismount request cancels a
    /// mount under way (MountCancel) or dismounts (<see cref="Mounts.Dismount"/>); unanswered either way.
    /// </summary>
    [Handles(OperationCodes.Mount, Status = SessionStatus.InWorld)]
    private AlbionResponse? Mount(MountRequest req)
    {
        if (Player is not { Map: not null } player) return null;
        if (!req.v4)
        {
            if (player.Mount.State == MountState.Mounting) Mounts.Cancel(player, "dismount request");
            else Mounts.Dismount(player);
            return null;
        }
        if (Mounts.Check(player, req.v3, req.v5) is { } why)
        {
            Log.Info(Name, $"'{player.Character.Name}' Mount refused ({Mounts.Refused}): {why}");
            return new GenericResponse { ReturnCode = Mounts.Refused };
        }
        Fishing.End(player, "the player mounts"); // stageG fishing SPEC §4
        Emotes.Stop(player, "mounts"); // stageN qol
        Mounts.Start(player, remount: req.v5);
        return null;
    }

    /// <summary>MountCancel (op 200, x0: 0 GameTimeStamp; no response class): the mount bar under way ends (SPEC §4.2: MountCancel to the player and its observers).</summary>
    [Handles(OperationCodes.MountCancel, Status = SessionStatus.InWorld)]
    private AlbionResponse? MountCancel(MountCancelRequest req)
    {
        if (Player is { } player) Mounts.Cancel(player, "cancelled by the client");
        return null;
    }
}
