using System.Numerics;
using AlbionLocal.Photon;
using AlbionLocal.Photon.Serialization;
using AlbionLocal.Server.Entities;
using AlbionLocal.Server.Protocol;
using AlbionLocal.Server.Systems;

namespace AlbionLocal.Server.Network;

public sealed partial class WorldSession
{
    /// <summary>
    /// Movement is predicted by the client; Move gets no response. A MoveResponse is a server
    /// correction (cyl.ai7 0x180a02e90): a non-null position teleports the player, then the
    /// direction is set and the movement stopped, so answering every Move pins the player in place.
    /// Quiet: hot path (about 10 per second while walking), kept out of the info log. The position is saved by
    /// the periodic save (extrapolated then) and on disconnect. Each Move recomputes the world objects in view
    /// (<see cref="VisibilityTracker.Update"/>, SPEC stage 2 §5), which costs something only when the player's cell changed,
    /// and the Move of a harvesting player that leaves its spot cancels its harvest (<see cref="HarvestMoved"/>).
    /// Stage 7 (analysis/work/stage7/SPEC.md §2.2): each accepted Move is relayed at once to the players who know this one
    /// (event 3, <see cref="Player.Move"/>; never to the mover), stamped with the server's receive time (kept monotonic per
    /// player, so one clock for every observer and no client offset to correct), before the visibility pass, so a player
    /// who walks out of view gets this last sample and then the Leave, one who walks into view the NewCharacter built from it.
    /// Stage 5e: a knocked-down player does not move (rc 881, the official answer, <see cref="Knockdown"/>); a Move that walks
    /// preserves the get-up protection, whose authored effects do not break on movement. StageD (effects SPEC §3.2): a stunned or rooted player's Move that walks is ignored (the
    /// client blocks its own input from its list; nothing is relayed); its stop or turn on the spot as the CC lands is taken.
    /// During its own dash (ForcedMovement, effects SPEC §5.3) every Move is ignored until the first one whose key 6 is the
    /// ForcedMovement's key 2 (the client's echo) or 300 ms after its end; a Move that walks ends a channel with allowmovement="false".
    /// StageD mounts: a Move that walks off cancels a mount under way (MountCancel).
    /// </summary>
    [Handles(OperationCodes.Move, Status = SessionStatus.InWorld, Quiet = true)]
    private AlbionResponse? Move(MoveRequest req)
    {
        if (Player is not { } player) return null;
        if (player.Combat.Down is not null) return new GenericResponse { ReturnCode = Knockdown.MoveRefused };
        var walks = req.v6 is [var px, var py] && Knockdown.Walks(new Vector2(px, py), req.v8 is [var wx, var wy] ? new Vector2(wx, wy) : null);
        if (walks && (SpellEffects.Stunned(player, world.GameNow) || SpellEffects.Rooted(player, world.GameNow))) return null;
        if (!SpellMovement.AcceptMove(player, req.wb, world.GameNow, req.v6 is [var ex, var ey] ? new Vector2(ex, ey) : null)) return null; // on a dash: only its key-6 echo ends the lock
        if (walks && player.Combat.Channel is { Info.AllowMovement: false }) SpellCasts.EndChannel(player, SpellCasts.ChannelEnd.Cancelled);
        if (walks) Emotes.Stop(player, "walks"); // stageN qol: a looping emote ends for the observers (the client has left it)
        if (walks) Revive.Interrupt(player, "walks", interrupted: false); // stageN: PLAYER_BLEEDOUT_REVIVE has keepmovingaftercast false (the client has left it)
        if (player.Movement is null)
            Log.Debug(Name, $"clock: client GameTimeStamp is {(req.wf - DateTime.UtcNow.Ticks) / TimeSpan.TicksPerMillisecond} ms off server UTC");
        var now = DateTime.UtcNow;
        if (req.v6 is [var x, var y])
            player.Move(
                new Vector2(x, y),                                                         // [1] position
                req.v8 is [var tx, var ty] ? new Vector2(tx, ty) : new Vector2(x, y),     // [3] target
                req.v9,                                                                    // [4] speed
                req.v7,                                                                    // [2] direction in degrees
                req.wa,                                                                    // [5] → event 3 cs2
                now);
        player.Facing = req.v7; // [2] direction in degrees
        if (player.Harvest is not null) HarvestMoved(player);
        if (player.Fishing is not null) FishingMoved(player); // stageG: walking off the spot ends a fishing session
        // A station action (repair, craft batch; stage 6 SPEC §6a.1) ends with a walk: a Move whose target is off the spot.
        if (player.Action is { Over: false } && player.Movement is { } walk && Vector2.Distance(walk.From, walk.Target) > Harvesting.WalkOffTolerance)
            CancelAction(player, "the player walks", tellClient: true);
        // So does the mount bar (stageD mounts SPEC §4.2: the 3 captured MountCancels all came after the rider moved).
        if (player.Mount.State == MountState.Mounting && player.Movement is { } off && Vector2.Distance(off.From, off.Target) > Harvesting.WalkOffTolerance)
            Mounts.Cancel(player, "the player walks");
        player.UpdateVisibility(now);
        world.Saves.MarkDirty(player.Character);
        return null;
    }

    /// <summary>
    /// A movement event of another object (event 3, <see cref="MovementCodec"/>) to this client: unreliable on channel 0, as
    /// the official server sends 96 % of them; the client drops a sample older than its newest anyway (stage 7 SPEC §2.2).
    /// </summary>
    internal void SendMovement(EventData ev)
    {
        if (Peer is BotPeer) return; // stage I: never reached (a bot observes nobody), kept as the guard of the relay
        Peer.SendEventUnreliable(ev, MovementChannel);
        Log.Trace(Name, $"-> movement of object {ev.Parameters[0]}");
    }

    /// <summary>ENet channel of event 3 (official: 0).</summary>
    private const byte MovementChannel = 0;

    /// <summary>
    /// A server teleport within the player's cluster (GM .go): the MoveResponse correction to this client (key 0 the
    /// position, which cyl.ai7 0x180a02e90 teleports to even when empty, key 1 the facing; then the client stops), pushed
    /// without a request id because Move carries none, then <see cref="Player.Teleport"/>: Teleport (event 4) to the
    /// observers and the visibility of the new cell. A harvest under way ends as on a walk-off.
    /// </summary>
    internal void Relocate(Vector2 to, float facing)
    {
        if (Player is not { } player) return;
        if (player.Harvest is not null) CancelHarvest(player, "teleported", tellClient: true);
        Systems.Fishing.End(player, "teleported");
        SendResponse(OperationCodes.Move, new MoveResponse { bgh = [to.X, to.Y], bgi = facing }, requestId: null, encrypted: false);
        player.Teleport(to, facing, DateTime.UtcNow);
        world.Saves.MarkDirty(player.Character);
    }
}
