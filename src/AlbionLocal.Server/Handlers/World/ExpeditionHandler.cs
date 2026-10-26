using AlbionLocal.Photon;
using AlbionLocal.Server.Entities;
using AlbionLocal.Server.Protocol;
using AlbionLocal.Server.Systems;

namespace AlbionLocal.Server.Network;

/// <summary>
/// The expedition operations (analysis/work/stageH/expeditions/SPEC.md, "SPEC" here, §2): registration at an agent, the Join channel,
/// the checkpoints and the exits of an instance. The state and the rules are in <see cref="Expeditions"/>; every answer is RC 0 with
/// an empty body except a refused ExpeditionRegister (RC 10843 + a62, §2.2).
/// </summary>
public sealed partial class WorldSession
{
    /// <summary>
    /// ExpeditionRegister (271, sg; §2.1): 0 the agent's object id, 1 the category index, 2 the expedition index or -1, 3 the difficulty.
    /// Accepted: RC 0, then 263 kind 1 and kind 5 (solo). Refused: RC 10843 + a62, and the reason as a system line (the click closed the
    /// agent window, so the client may show no box, §2.2 [inf]).
    /// </summary>
    [Handles(OperationCodes.ExpeditionRegister, Status = SessionStatus.InWorld)]
    private AlbionResponse? ExpeditionRegister(ExpeditionRegisterRequest req)
    {
        if (Player is not { } player) return null;
        var a62 = world.Expeditions.Register(player, req.kr, req.ks, req.kt, req.ku, out var why);
        if (a62 == Expeditions.Ok) return new GenericResponse();
        Log.Info(Name, $"'{player.Character.Name}' expedition registration refused, a62 {a62}: {why}");
        Expeditions.Tell(player, $"Expedition registration refused: {why}");
        return new GenericResponse { ReturnCode = Expeditions.RefusalCode(a62) };
    }

    /// <summary>ExpeditionRegisterCancel (272, the HUD's "leave queue"): the registration ends (263 kind 3, a62 23).</summary>
    [Handles(OperationCodes.ExpeditionRegisterCancel, Status = SessionStatus.InWorld)]
    private AlbionResponse? ExpeditionRegisterCancel(ExpeditionRegisterCancelRequest req)
    {
        if (Player is { } player) world.Expeditions.Cancel(player, "left the queue");
        return new GenericResponse();
    }

    /// <summary>DeclineExpeditionInvitation (274; a party invitation declined, the Ready popup's "no", a Join refused by the client): as 272.</summary>
    [Handles(OperationCodes.DeclineExpeditionInvitation, Status = SessionStatus.InWorld)]
    private AlbionResponse? DeclineExpeditionInvitation(DeclineExpeditionInvitationRequest req)
    {
        if (Player is { } player) world.Expeditions.Cancel(player, "declined");
        return new GenericResponse();
    }

    /// <summary>JoinExpedition (273, the party leader's invitation accepted, §3.3): wait for the others, then ready with them.</summary>
    [Handles(OperationCodes.JoinExpedition, Status = SessionStatus.InWorld)]
    private AlbionResponse? JoinExpedition(JoinExpeditionRequest req)
    {
        if (Player is { } player && !world.Expeditions.Accept(player)) Log.Info(Name, $"'{player.Character.Name}' JoinExpedition without an open invitation");
        return new GenericResponse();
    }

    /// <summary>EnteringExpeditionStart (278, r9; §2.5): the Join click. RC 0 whatever happens (cw4.ai7 takes any answer).</summary>
    [Handles(OperationCodes.EnteringExpeditionStart, Status = SessionStatus.InWorld)]
    private AlbionResponse? EnteringExpeditionStart(EnteringExpeditionStartRequest req)
    {
        if (Player is { } player && world.Expeditions.StartEntering(player) is { } why)
            Log.Info(Name, $"'{player.Character.Name}' expedition Join refused: {why}");
        return new GenericResponse();
    }

    /// <summary>EnteringExpeditionCancel (279, r8): the Join channel was interrupted on the client.</summary>
    [Handles(OperationCodes.EnteringExpeditionCancel, Status = SessionStatus.InWorld)]
    private AlbionResponse? EnteringExpeditionCancel(EnteringExpeditionCancelRequest req)
    {
        if (Player is { } player) world.Expeditions.CancelEntering(player, "cancelled by the client");
        return new GenericResponse();
    }

    /// <summary>ActivateExpeditionCheckPoint (280, ol; §2.7.3): the player walked into a checkpoint. RC 0 always (the callback ignores it).</summary>
    [Handles(OperationCodes.ActivateExpeditionCheckPoint, Status = SessionStatus.InWorld)]
    private AlbionResponse? ActivateExpeditionCheckPoint(ActivateExpeditionCheckPointRequest req)
    {
        if (Player is { } player) world.Expeditions.ActivateCheckpoint(player, req.ai);
        return new GenericResponse();
    }

    /// <summary>
    /// ExitEnterStart (237, sf; §2.10): 0 the client's time, 1 the exit object. The client ends its channel bar on ANY op-237 response,
    /// whatever the RC (cw7.ai7 0x1809DF810 has the same body as the 254 handler cw7.aft), so an accepted channel gets no response; refused:
    /// RC 0 and ExitEnterCancel (254). An expedition exit through <c>Expeditions.StartExit</c>; a random dungeon portal clicked in the
    /// open world (usage mode 1, stageM exit NOTES) through <see cref="StartDungeonExit"/> (Handlers/World/DungeonHandler).
    /// </summary>
    [Handles(OperationCodes.ExitEnterStart, Status = SessionStatus.InWorld)]
    private AlbionResponse? ExitEnterStart(ExitEnterStartRequest req)
    {
        if (Player is not { } player) return null;
        if (player.Map is { } map && map.Objects.TryGetValue(req.kq, out var o) && o is RandomDungeonExit portal)
        {
            if (StartDungeonExit(player, portal) is { } no)
            {
                Log.Info(Name, $"'{player.Character.Name}' random dungeon exit {req.kq} ({portal.Name}) refused: {no}");
                SendEvent(new ExitEnterCancelEvent { ddn = player.Id });
                return new GenericResponse();
            }
            return null; // accepted: a response would end the client's 2 s bar at once
        }
        // StageP hideouts: the way into a hideout (its surface object) and out (its exit inside), Handlers/World/HideoutHandler.
        if (player.Map is { } here && here.Objects.TryGetValue(req.kq, out var ho) && ho is HideoutObject or HideoutExitObject)
        {
            if (world.Hideouts.StartChannel(player, ho) is { } no)
            {
                Log.Info(Name, $"'{player.Character.Name}' hideout way through object {req.kq} refused: {no}");
                TellHideout($"The hideout cannot be entered now: {no}.");
                SendEvent(new ExitEnterCancelEvent { ddn = player.Id });
                return new GenericResponse();
            }
            return null; // accepted: the 4 s bar runs until 255 or 254
        }
        if (world.Expeditions.StartExit(player, req.kq) is { } why)
        {
            Log.Info(Name, $"'{player.Character.Name}' exit object {req.kq} refused: {why}");
            SendEvent(new ExitEnterCancelEvent { ddn = player.Id });
            return new GenericResponse();
        }
        return null; // accepted: as above, the 5 s bar runs until 255 or 254
    }

    /// <summary>ExitEnterCancel (238): the exit channel was interrupted on the client (an expedition exit or a random dungeon portal).</summary>
    [Handles(OperationCodes.ExitEnterCancel, Status = SessionStatus.InWorld)]
    private AlbionResponse? ExitEnterCancel(ExitEnterCancelRequest req)
    {
        if (Player is { } player && !world.Dungeons.CancelExit(player, "cancelled by the client") && !world.Hideouts.CancelChannel(player, "cancelled by the client"))
            world.Expeditions.CancelExit(player, "cancelled by the client");
        return new GenericResponse();
    }

    /// <summary>
    /// The escape spell inside an expedition (ChangeCluster {0: 0, 3: true}, §2.10, escapedungeonspellavailable): out as through an exit,
    /// answered {0: the return cluster}. False outside an instance.
    /// </summary>
    private bool ExpeditionEscape(Player player, ChangeClusterRequest req, out ChangeClusterResponse? response)
    {
        response = null;
        if (!req.eh || world.Expeditions.Find(player.Map?.Cluster) is not { } instance) return false;
        if (player.Combat.Down is { } down)
        {
            response = Refuse(player, 0, ChangeClusterRefusal.NotNearAnExit, $"knocked down ({down.Kind})");
            return true;
        }
        var to = world.Expeditions.Leave(player, instance, "escape", answer: true);
        Commit(player, to, $"expedition:{instance.Id}:escape");
        response = new ChangeClusterResponse { anu = to.Cluster };
        return true;
    }
}
