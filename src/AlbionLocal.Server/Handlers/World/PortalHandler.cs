using System.Globalization;
using AlbionLocal.Photon;
using AlbionLocal.Server.Data;
using AlbionLocal.Server.Entities;
using AlbionLocal.Server.Game;
using AlbionLocal.Server.Protocol;
using AlbionLocal.Server.Systems;

namespace AlbionLocal.Server.Network;

/// <summary>
/// The refusals of UsePortalEntrance (11760-11770). ggd.aq 0x181cd6b00, the request's callback, shows a message box for each: 11760
/// "@USE_PORTAL_ENTRANCE_ERROR_NOT_POSSIBLE", 11761 _REPUTATION_TOO_LOW, 11762 _RESTRICTION_BINDING ("your last-used portal exit"),
/// 11763-11768 _RESTRICTION_&lt;CITY&gt; ("You can only use the portal in …") in <see cref="PortalKind"/> order (Caerleon … Fort Sterling;
/// the switch's jump table), 11769/11770 @CHANGECLUSTER_ERROR_CLUSTER_FULL. RC 0 and any other code end the portal window's wait.
/// </summary>
public enum UsePortalEntranceRefusal : short
{
    /// <summary>No such Realmgate or target, knocked down, a change under way.</summary>
    NotPossible = 11760,

    /// <summary>The reputation is below the portal town's requiredreputationtoenter.</summary>
    ReputationTooLow = 11761,

    /// <summary>Bound to Caerleon's portal; + <see cref="PortalKind"/> for the other cities.</summary>
    OnlyPortalOfCaerleon = 11763,
}

/// <summary>
/// The city portals into the Outlands (analysis/work/portals/SPEC.md, "SPEC" here). Each royal city's Conquerors' Hall holds a Realmgate
/// (<see cref="PortalEntrance"/>) to the city's portal town in the Outlands, whose portal (<see cref="PortalExit"/>) leads back into the
/// hall. The client's portal window (PortalInfoModule) asks for the binding (QueryPortalBinding 296), sends UsePortalEntrance 294 for the
/// way out and ResetPortalBinding 295 to remove the binding; the way back is a plain ChangeCluster at the portal town's portal.
/// </summary>
public sealed partial class WorldSession
{
    /// <summary>
    /// UsePortalEntrance (op 294, client class aci, sent by c32.i from ggd.aq 0x181cd5cc0 after the "you will be bound" box): 0 the
    /// NewPortalEntrance object id, 1 the chosen portal exit "&lt;guid&gt;@&lt;cluster&gt;" (al.m). Answered as BuyJourney [infer: not
    /// captured]: a bare RC 0, then the ChangeCluster response {0: target cluster} pushed without a request id, which runs the change.
    /// </summary>
    [Handles(OperationCodes.UsePortalEntrance, Status = SessionStatus.InWorld)]
    private AlbionResponse? UsePortalEntrance(UsePortalEntranceRequest req)
    {
        if (Player is not { } player) return null;
        if (_changing is not null)
            return RefusePortal(player, req.aih, UsePortalEntranceRefusal.NotPossible, $"object {_changing.ExitId} is being resolved for a cluster change already");
        if (player.Combat.Down is { } down) // stageE E3: no way out for the knocked-down and the dead [infer: no own code]
            return RefusePortal(player, req.aih, UsePortalEntranceRefusal.NotPossible, $"knocked down ({down.Kind})");
        return EnterRealmgate(player, req, null, RequestId, RequestEncrypted);
    }

    /// <summary>
    /// SPEC §3, in order: a Realmgate of the player's map, in its view; key 1 a world.xml portal exit that leads back to this Realmgate;
    /// not bound to another city (11763 + that city, Transfer:PortalBinding); then, the portal town known (read in the background first when
    /// it is not, as for an exit), its PvP entry rules (11761 reputation, else 11760). The pass binds the character to the Realmgate's city
    /// and puts it at the portal exit's spawnpos, facing away from the portal.
    /// </summary>
    private AlbionResponse? EnterRealmgate(Player player, UsePortalEntranceRequest req, ClusterTemplate? resolved, int? requestId, bool encrypted)
    {
        const UsePortalEntranceRefusal no = UsePortalEntranceRefusal.NotPossible;
        if (player.Map is not { } map || !map.Objects.TryGetValue(req.aih, out var o) || o is not PortalEntrance gate)
            return RefusePortal(player, req.aih, no, $"object {req.aih} is no portal entrance of {player.Map?.Cluster ?? player.Character.Cluster}");
        if (!player.Visibility.Known.Contains(gate)) return RefusePortal(player, req.aih, no, $"portal entrance {req.aih} is not in the player's view");
        if (world.Clusters?.World.PortalExit(req.aii) is not { } found || found.Exit.Target != gate.World.Id)
            return RefusePortal(player, req.aih, no, $"target '{req.aii}' is no portal exit of the {gate.World.Kind} portal");
        var c = player.Character;
        if (world.Config.Transfer.PortalBinding && c.PortalBinding?.City is { } bound && bound != gate.World.Kind)
            return RefusePortal(player, req.aih, UsePortalEntranceRefusal.OnlyPortalOfCaerleon + (short)bound, $"bound to the {bound} portal");
        var town = found.Cluster.Id;
        var target = resolved ?? world.CachedCluster(town);
        if (target is null)
        {
            if (world.ResolveCluster(town) is not { } load) return RefusePortal(player, req.aih, no, "this server has no cluster data");
            var pending = new PendingChange(player, map, req.aih, requestId, encrypted);
            _changing = pending;
            Log.Info(Name, $"'{c.Name}' takes the {gate.World.Kind} portal to {town}: resolving {town}, the answer follows");
            load.ContinueWith(t => world.Post(() => ResumeRealmgate(pending, req, t)), TaskScheduler.Default);
            return null;
        }
        if (Flagging.EntryRefusal(player, target.Pvp) is { } barred) // stageE: the target's PvP entry rules, as at an exit
            return RefusePortal(player, req.aih, barred.Code == Flagging.ReputationTooLow ? UsePortalEntranceRefusal.ReputationTooLow : no, barred.Why);
        if (target.PortalExits.FirstOrDefault(p => p.Guid == found.Exit.Id) is not { SpawnPosition: { } at } door)
        {
            Log.Warn(Name, $"portal exit {Short(found.Exit.Id)} of {town} is not in its template or has no spawnpos");
            return RefusePortal(player, req.aih, no, $"no arrival point in {town}");
        }
        Bind(c, gate.World.Kind);
        Commit(player, new Arrival(target.Id, at, Transfers.Facing(door.Position, at)), $"portal:{Short(gate.World.Id)}");
        SendResponse(OperationCodes.UsePortalEntrance, new GenericResponse(), requestId, encrypted);
        SendResponse(OperationCodes.ChangeCluster, new ChangeClusterResponse { anu = target.Id }, requestId: null, encrypted: false);
        return null;
    }

    /// <summary>The portal town of a deferred pass is in (World.Post): as <see cref="Resume"/>, checked again, answered with the kept request id.</summary>
    private void ResumeRealmgate(PendingChange pending, UsePortalEntranceRequest req, Task<ClusterTemplate> load)
    {
        if (_changing != pending) return;
        _changing = null;
        if (_closed || Status != SessionStatus.InWorld || Player != pending.Player || pending.Player.Map != pending.Map)
        {
            Log.Info(Name, $"portal entrance {pending.ExitId}: the player has left {pending.Map.Cluster} meanwhile, no answer");
            return;
        }
        AlbionResponse? resp;
        if (load.IsCompletedSuccessfully) resp = EnterRealmgate(pending.Player, req, load.Result, pending.RequestId, pending.Encrypted);
        else
        {
            Log.Warn(Name, $"portal entrance {pending.ExitId}: the portal town could not be resolved: {load.Exception?.InnerException?.Message ?? "load cancelled"}");
            resp = RefusePortal(pending.Player, pending.ExitId, UsePortalEntranceRefusal.NotPossible, "target cluster not resolved");
        }
        if (resp is not null) SendResponse(OperationCodes.UsePortalEntrance, resp, pending.RequestId, pending.Encrypted);
    }

    private GenericResponse RefusePortal(Player player, long objectId, UsePortalEntranceRefusal code, string why)
    {
        Log.Info(Name, $"'{player.Character.Name}' portal entrance {objectId} refused, {(short)code}: {why}");
        return new GenericResponse { ReturnCode = (short)code };
    }

    /// <summary>Whether the request is for a <see cref="PortalExit"/> of the player's map, and its answer (null = deferred).</summary>
    private bool PortalChange(Player player, ChangeClusterRequest req, out ChangeClusterResponse? response)
    {
        response = null;
        if (player.Map is not { } map || !map.Objects.TryGetValue(req.ee, out var o) || o is not PortalExit portal) return false;
        response = LeavePortalTown(player, portal, null, RequestId, RequestEncrypted);
        return true;
    }

    /// <summary>
    /// The way back (SPEC §4), a plain ChangeCluster at the portal town's portal (PortalExitObjectView.EnterExit 0x181a73ab0), checked as
    /// an exit: not knocked down (1640), a hall in world.xml behind it (1641), the player in its square and the exit cooldown over (1640,
    /// 1647), then the hall's PvP entry rules. The arrival is the Realmgate's spawnpos in the hall, facing away from it; the binding moves
    /// to the portal's city ("If you return through an Outland portal connected to another city, your binding will be changed").
    /// </summary>
    private ChangeClusterResponse? LeavePortalTown(Player player, PortalExit portal, ClusterTemplate? resolved, int? requestId, bool encrypted)
    {
        var map = player.Map!;
        var w = portal.World;
        if (player.Combat.Down is { } down) return Refuse(player, portal.Id, ChangeClusterRefusal.NotNearAnExit, $"knocked down ({down.Kind})");
        if (w.Target == Guid.Empty || world.Clusters?.World.Find(w.TargetCluster) is null)
            return Refuse(player, portal.Id, ChangeClusterRefusal.ExitNotConnected, $"portal exit {Short(w.Id)} leads nowhere ('{w.TargetCluster}' is not in world.xml)");
        if (Reach(player, map, portal.Position, portal.Size, $"portal exit {Short(w.Id)}") is { } refused) return Refuse(player, portal.Id, refused.Code, refused.Why);
        var target = resolved ?? world.CachedCluster(w.TargetCluster);
        if (target is null)
        {
            if (world.ResolveCluster(w.TargetCluster) is not { } load)
                return Refuse(player, portal.Id, ChangeClusterRefusal.ExitNotConnected, "this server has no cluster data");
            var pending = new PendingChange(player, map, portal.Id, requestId, encrypted);
            _changing = pending;
            Log.Info(Name, $"'{player.Character.Name}' takes the {w.Kind} portal back to {w.TargetCluster}: resolving {w.TargetCluster}, the answer follows");
            load.ContinueWith(t => world.Post(() => ResumePortalTown(pending, portal, t)), TaskScheduler.Default);
            return null;
        }
        if (Flagging.EntryRefusal(player, target.Pvp) is { } barred) // stageE: the target's PvP entry rules
            return Refuse(player, portal.Id, (ChangeClusterRefusal)barred.Code, barred.Why);
        if (target.PortalEntrances.FirstOrDefault(p => p.Guid == w.Target) is not { SpawnPosition: { } at } gate)
        {
            Log.Warn(Name, $"portal exit {Short(w.Id)} of {map.Cluster}: its portal entrance {Short(w.Target)} is not in {w.TargetCluster} or has no spawnpos");
            return Refuse(player, portal.Id, ChangeClusterRefusal.ExitNotConnected, $"no arrival point in {w.TargetCluster}");
        }
        Bind(player.Character, w.Kind);
        Commit(player, new Arrival(target.Id, at, Transfers.Facing(gate.Position, at)), $"portal:{Short(w.Id)}");
        return new ChangeClusterResponse { anu = target.Id }; // [0] the target, byte-identical to the new Join's key 8
    }

    /// <summary>The hall of a deferred way back is in (World.Post): if the session still waits on the same map, the pass runs again (all its checks).</summary>
    private void ResumePortalTown(PendingChange pending, PortalExit portal, Task<ClusterTemplate> load)
    {
        if (_changing != pending) return;
        _changing = null;
        if (_closed || Status != SessionStatus.InWorld || Player != pending.Player || pending.Player.Map != pending.Map)
        {
            Log.Info(Name, $"portal exit {Short(portal.World.Id)}: the player has left {pending.Map.Cluster} meanwhile, no answer");
            return;
        }
        var resp = load.IsCompletedSuccessfully ? LeavePortalTown(pending.Player, portal, load.Result, pending.RequestId, pending.Encrypted)
            : Refuse(pending.Player, pending.ExitId, ChangeClusterRefusal.ExitNotConnected, "target cluster not resolved");
        if (resp is not null) SendResponse(OperationCodes.ChangeCluster, resp, pending.RequestId, pending.Encrypted);
    }

    /// <summary>The character is bound to <paramref name="city"/> from now on (Transfer:PortalBinding; the commit that follows saves it).</summary>
    private void Bind(Character c, PortalKind city)
    {
        if (!world.Config.Transfer.PortalBinding || c.PortalBinding?.City == city) return;
        Log.Info(Name, $"'{c.Name}' is bound to the {city} portal{(c.PortalBinding?.City is { } before ? $" (was {before})" : "")}");
        c.PortalBinding = new PortalBinding(city, c.PortalBinding?.RemovedAt ?? 0);
    }

    /// <summary>
    /// QueryPortalBinding (op 296, zk, sent when the portal window opens, ggd.aq 0x181cd6d70): {0 the name of the bound portal, 1 its
    /// city + 1}; no key 0 = not bound (ggd.f.c 0x180c5b220: null → no binding; else key 1 − 1 against the Realmgate's kind → "this
    /// city" or "another city"). Key 0 is matched against the window's target names (ggo.a1y: the portal town's world.xml
    /// displayname) to preselect the bound one.
    /// </summary>
    [Handles(OperationCodes.QueryPortalBinding, Status = SessionStatus.InWorld)]
    private QueryPortalBindingResponse? QueryPortalBinding(QueryPortalBindingRequest req)
    {
        if (Player is not { } player) return null;
        if (!world.Config.Transfer.PortalBinding || player.Character.PortalBinding?.City is not { } city) return new QueryPortalBindingResponse();
        return new QueryPortalBindingResponse
        {
            bg9 = world.Clusters?.WorldIfLoaded?.PortalTown(city)?.DisplayName is { Length: > 0 } name ? name : city.ToString(),
            bha = (byte)((int)city + 1),
        };
    }

    /// <summary>
    /// ResetPortalBinding (op 295, aag, the window's "Remove" button at another city's Realmgate after its warning box): the binding goes
    /// and the removal cooldown starts (gamedata.xml PortalSettings bindingresetdays, Transfer:PortalBindingResetDays; the client counts it
    /// from Join key 63). RC 0 makes the window show "not bound" (ggd.h.b 0x180c75690); within the cooldown the request is refused, which
    /// the client's window, having greyed the button out, never sends.
    /// </summary>
    [Handles(OperationCodes.ResetPortalBinding, Status = SessionStatus.InWorld)]
    private AlbionResponse? ResetPortalBinding(ResetPortalBindingRequest req)
    {
        if (Player is not { } player) return null;
        var c = player.Character;
        if (c.PortalBinding is not { City: { } city } binding) return new GenericResponse();
        var now = world.GameNow;
        var free = binding.RemovedAt + TimeSpan.FromDays(world.Config.Transfer.PortalBindingResetDays ?? world.GameData?.PortalBindingResetDays ?? 7).Ticks;
        if (binding.RemovedAt != 0 && now < free)
        {
            var left = TimeSpan.FromTicks(free - now);
            Expeditions.Tell(player, string.Create(CultureInfo.InvariantCulture, $"Binding removal is on cooldown for another {(left.TotalHours >= 1 ? $"{left.TotalHours:0.#} h" : $"{left.TotalMinutes:0} min")}."));
            Log.Info(Name, $"'{c.Name}' may not remove its {city} portal binding yet: the last removal was {TimeSpan.FromTicks(now - binding.RemovedAt).TotalHours:0.#} h ago");
            return new GenericResponse { ReturnCode = (short)UsePortalEntranceRefusal.NotPossible };
        }
        c.PortalBinding = new PortalBinding(null, now);
        world.Saves.MarkChanged(c);
        Log.Info(Name, $"'{c.Name}' removed its {city} portal binding");
        return new GenericResponse();
    }
}
