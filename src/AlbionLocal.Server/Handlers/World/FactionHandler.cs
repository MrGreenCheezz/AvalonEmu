using AlbionLocal.Photon;
using AlbionLocal.Server.Protocol;

namespace AlbionLocal.Server.Network;

/// <summary>StageN faction (analysis/work/stageN/faction/SPEC.md §1): enlisting at a faction trader, the rules in <see cref="Systems.Factions"/>.</summary>
public sealed partial class WorldSession
{
    /// <summary>
    /// EnlistFactionWarfare (op 433, r5 {0 the trader's object id, 1 bool}; sent by FactionFlaggingView after its confirm box, cw0.u): accepted →
    /// aev {0 GameTimeStamp: from when a switch is allowed again [inference]} and FactionEnlistmentChanged (482); refused → RC 1 (the official
    /// codes are not traced; the cooldown has a chat line).
    /// </summary>
    [Handles(OperationCodes.EnlistFactionWarfare, Status = SessionStatus.InWorld)]
    private AlbionResponse? EnlistFactionWarfare(EnlistFactionWarfareRequest req)
    {
        if (Player is not { } player) return null;
        var (stamp, refused) = world.Factions.Enlist(player, req.j8);
        if (refused is null) return new EnlistFactionWarfareResponse { aq5 = stamp };
        Log.Info(Name, $"'{player.Character.Name}' enlisting at {req.j8} (key 1 {req.j9}) refused: {refused}");
        return new GenericResponse { ReturnCode = 1 };
    }
}
