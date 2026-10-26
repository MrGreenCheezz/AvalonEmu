using System.Globalization;
using AlbionLocal.Photon;
using AlbionLocal.Server.Data;
using AlbionLocal.Server.Entities;
using AlbionLocal.Server.Game;
using AlbionLocal.Server.Protocol;

namespace AlbionLocal.Server.Network;

/// <summary>
/// The refusals of ChangeCluster this server uses (client enum RcOpChangeCluster, 1640-1676; SPEC stage 3 §2.2). The client
/// (LocalPlayerCharacterView.HandleChangeClusterResponse 0x1819bed20) shows an error line for the first four, with the
/// request id echoed, and clears its pending flag on any of them, so the player stays and may try again.
/// </summary>
public enum ChangeClusterRefusal : short
{
    /// <summary>The player is not in the exit's square.</summary>
    NotNearAnExit = 1640,

    /// <summary>The exit leads nowhere the server can send the player (no world.xml target, target exit or spawnpos missing).</summary>
    ExitNotConnected = 1641,

    /// <summary>Request key 0 is no exit of the player's map.</summary>
    ExitInfoNotFound = 1642,

    /// <summary>Within the exit cooldown after the Join (also shown over the character).</summary>
    TooEarlyWaitForExitCooldown = 1647,

    /// <summary>The reputation is below the target archetype's requiredreputationtoenter (stageE pvp SPEC §3.4).</summary>
    ReputationTooLow = Systems.Flagging.ReputationTooLow,

    /// <summary>A faction-flagged player and a target archetype with allowfactionflaggedenter false (stageE pvp SPEC §3.1).</summary>
    NoFactionPlayersAllowed = Systems.Flagging.NoFactionPlayersAllowed,

    /// <summary>A change of this session is being resolved already (no text).</summary>
    AlreadyEnteringExit = 1669,
}

/// <summary>
/// Moving between clusters (ROADMAP stage 3, ARCHITECTURE.md §4.2 and §8 step 13; "SPEC" = analysis/work/stage3/SPEC.md).
/// The game connection is never reused: the ChangeCluster response makes the client drop it, ask the login connection for
/// the game server of the target (GetGameServerByCluster, LoginSession) and Join on a new connection, whose Join key 0 is
/// the account. So the commit puts everything the new Join needs into the saved character (cluster, arrival, facing,
/// <see cref="PendingTransfer"/>) and leaves this session detached (<see cref="SessionStatus.Transferring"/>).
/// </summary>
public sealed partial class WorldSession
{
    /// <summary>Slack on the exit cooldown (SPEC §4.1: cooldown − 0.5 s), for the client's clock and the Move delay.</summary>
    private static readonly TimeSpan CooldownSlack = TimeSpan.FromSeconds(0.5);

    /// <summary><see cref="World.Clock"/> time of this session's Join: the exit cooldown counts from it, as the client's counts from Join key 68.</summary>
    private TimeSpan _joinedAt;

    /// <summary>A ChangeCluster waiting for its target cluster to be resolved (SPEC §4.2); null when none.</summary>
    private PendingChange? _changing;

    /// <summary>The connection has ended (<see cref="OnDisconnected"/>): a deferred answer is dropped.</summary>
    private bool _closed;

    /// <summary>
    /// What a deferred ChangeCluster needs to go on: who used which exit on which map, and how to answer it; for a deferred
    /// travel-point journey (JourneyHandler) <see cref="ExitId"/> is the travel point's object id and <paramref name="Journey"/> its request.
    /// </summary>
    private sealed record PendingChange(Player Player, Map Map, long ExitId, int? RequestId, bool Encrypted, BuyJourneyRequest? Journey = null, bool Escape = false);

    /// <summary>
    /// Using an exit (op 41, client class qa, plaintext: 0 the NewExit object id, 1 "" and 2 [-1], both ignored; SPEC
    /// §2.1). The client sends it while the player stands in the exit's square, once its own checks pass (cyd.u 0x1809feba0).
    /// Checked in SPEC §4.1's order: an exit of the player's map (else 1642), with a world.xml target (1641), the player in
    /// its square (1640), the exit cooldown over (1647); a change already being resolved gets 1669. Then the target cluster:
    /// cached → at once; else it is resolved in the background and the answer follows with the kept request id (§4.2),
    /// about 0.1 s later (official answers take 0.12-0.17 s). The answer is exactly {0: target cluster id} (§2.2: no
    /// FillEmpty, whose key 0 "" crashed the client in ciy.a; keys 1-5 would select instanced clusters), after the commit
    /// (<see cref="Commit"/>). No mass Leave goes before it (§2.2: the official burst cost the client its DISCONNECT once).
    /// </summary>
    [Handles(OperationCodes.ChangeCluster, Status = SessionStatus.InWorld)]
    private ChangeClusterResponse? ChangeCluster(ChangeClusterRequest req)
    {
        if (Player is not { } player) return null;
        if (_changing is not null)
            return Refuse(player, req.ee, ChangeClusterRefusal.AlreadyEnteringExit, _changing.Journey is null
                ? $"exit object {_changing.ExitId} is being resolved already" : $"a journey from object {_changing.ExitId} is being resolved");
        // StageP hideouts: a hideout's surface object or its exit walked into (Handlers/World/HideoutHandler).
        if (HideoutChange(player, req, out var hideout)) return hideout;
        // StageL roads: a Roads of Avalon portal, NewTunnelExit 448 (Handlers/World/RoadHandler).
        if (RoadChange(player, req, out var road)) return road;
        // StageG dungeons: a random dungeon exit, an exit of a dungeon floor, the dungeon escape {0: 0, 3: true} (Handlers/World/DungeonHandler).
        if (DungeonChange(player, req, RequestId, RequestEncrypted, out var dungeon)) return dungeon;
        // StageH expeditions: the escape spell inside an instance (Handlers/World/ExpeditionHandler).
        if (ExpeditionEscape(player, req, out var escape)) return escape;
        // StageL corrupted: the exit portal and the escape spell inside a corrupted dungeon (Handlers/World/CorruptedHandler).
        if (CorruptedChange(player, req, out var corrupted)) return corrupted;
        // StageL hellgates: the escape spell (also the exit portal's) inside a hellgate (Handlers/World/HellgateHandler).
        if (HellgateEscape(player, req, out var hellgate)) return hellgate;
        // StageL mists: a zone's border exit, Brecilien entrance or open-world exit, and a mists world cluster's city entrance (Handlers/World/MistsHandler).
        if (MistsChange(player, req, out var mists)) return mists;
        return Change(player, req.ee, null, RequestId, RequestEncrypted);
    }

    /// <summary>
    /// The checks, then the change through exit <paramref name="exitId"/> into <paramref name="resolved"/> (the target cluster
    /// a deferred change waited for), else the cached target; null = deferred (<see cref="Resume"/> answers later). Once the target
    /// is known its PvP entry rules apply (stageE, <see cref="Systems.Flagging.EntryRefusal"/>: 1655 faction-flagged, 1652 reputation).
    /// </summary>
    private ChangeClusterResponse? Change(Player player, long exitId, ClusterTemplate? resolved, int? requestId, bool encrypted)
    {
        if (Check(player, exitId) is { } refused) return Refuse(player, exitId, refused.Code, refused.Why);
        var exit = (Exit)player.Map!.Objects[exitId];
        var targetId = exit.World.TargetCluster;
        var target = resolved ?? world.CachedCluster(targetId);
        if (target is null)
        {
            if (world.ResolveCluster(targetId) is not { } load)
                return Refuse(player, exitId, ChangeClusterRefusal.ExitNotConnected, "this server has no cluster data");
            var pending = new PendingChange(player, player.Map, exitId, requestId, encrypted);
            _changing = pending;
            Log.Info(Name, $"'{player.Character.Name}' takes exit {Short(exit.World.Id)} to {targetId}: resolving {targetId}, the answer follows");
            load.ContinueWith(t => world.Post(() => Resume(pending, t)), TaskScheduler.Default);
            return null;
        }
        if (Systems.Flagging.EntryRefusal(player, target.Pvp) is { } barred) // stageE: the target's PvP entry rules
            return Refuse(player, exitId, (ChangeClusterRefusal)barred.Code, barred.Why);
        if (Transfers.Through(exit.World, target, world.CollisionRadius) is not { } to)
        {
            Log.Warn(Name, $"exit {Short(exit.World.Id)} of {player.Map.Cluster}: its target exit {Short(exit.World.Target)} is not in {targetId} or has no spawnpos");
            return Refuse(player, exitId, ChangeClusterRefusal.ExitNotConnected, $"no arrival point in {targetId}");
        }
        Commit(player, to, $"exit:{exit.World.Id}");
        var clientCluster = TutorialInstances.ClientCluster(player.Character, world);
        return new ChangeClusterResponse { anu = clientCluster,
            anv = clientCluster != to.Cluster ? to.Cluster : null }; // the same identity as the next Join, with its authored base map
    }

    /// <summary>
    /// SPEC §4.1 "Validate" 1-4 for the player now: null = the exit may be used, else the refusal and why (for the log).
    /// The position is the last Move extrapolated (at most about 0.1 s old); the cooldown counts from this session's Join
    /// on the world clock and depends on the type of the cluster the player is in (<see cref="Transfers.ExitCooldown"/>).
    /// </summary>
    private (ChangeClusterRefusal Code, string Why)? Check(Player player, long exitId)
    {
        if (player.Map is not { } map || !map.Objects.TryGetValue(exitId, out var o) || o is not Exit exit)
            return (ChangeClusterRefusal.ExitInfoNotFound, $"object {exitId} is no exit of {player.Map?.Cluster ?? player.Character.Cluster}");
        // StageE E3: a knocked-down or dead player stays (else the change's Knockdown.Leave would stand it up next door without its
        // death) [infer: no own code; 1640 is one the client shows].
        if (player.Combat.Down is { } down) return (ChangeClusterRefusal.NotNearAnExit, $"knocked down ({down.Kind})");
        var w = exit.World;
        if (w.Target == Guid.Empty || w.TargetCluster.Length == 0 || world.Clusters?.World.Find(w.TargetCluster) is null)
            return (ChangeClusterRefusal.ExitNotConnected,
                $"exit {Short(w.Id)} leads nowhere ({(w.TargetCluster.Length == 0 ? "no world.xml target" : $"{w.TargetCluster} is not in world.xml")})");
        return Reach(player, map, exit.Position, Transfers.Size(exit.Spawn), $"exit {Short(w.Id)}");
    }

    /// <summary>
    /// SPEC §4.1 "Validate" 3-4 for any exit: the player in the square <paramref name="size"/> about <paramref name="centre"/>
    /// (± Transfer:PositionTolerance), the exit cooldown since the Join over. <paramref name="what"/> names the exit for the log.
    /// </summary>
    private (ChangeClusterRefusal Code, string Why)? Reach(Player player, Map map, System.Numerics.Vector2 centre, System.Numerics.Vector2 size, string what)
    {
        var at = player.PositionAt(DateTime.UtcNow);
        if (!Transfers.InSquare(centre, size, at, world.Config.Transfer.PositionTolerance))
            return (ChangeClusterRefusal.NotNearAnExit, string.Create(CultureInfo.InvariantCulture,
                $"at ({at.X:0.##}, {at.Y:0.##}), outside the {size.X:0.#} x {size.Y:0.#} square of {what} at ({centre.X:0.##}, {centre.Y:0.##})"));
        return Cooldown(map);
    }

    /// <summary>SPEC §4.1 "Validate" 4: the exit cooldown since this session's Join over (Transfer:CheckExitCooldown); null = over.</summary>
    private (ChangeClusterRefusal Code, string Why)? Cooldown(Map map)
    {
        if (world.Config.Transfer.CheckExitCooldown && map.Template is { } here)
        {
            var cooldown = Transfers.ExitCooldown(here.Name, world.GameData);
            var since = world.Clock.Now - _joinedAt;
            if (since < cooldown - CooldownSlack)
                return (ChangeClusterRefusal.TooEarlyWaitForExitCooldown, string.Create(CultureInfo.InvariantCulture,
                    $"{since.TotalSeconds:0.##} s after the Join; the exit cooldown in {here.Id} is {cooldown.TotalSeconds:0.#} s"));
        }
        return null;
    }

    private ChangeClusterResponse Refuse(Player player, long exitId, ChangeClusterRefusal code, string why)
    {
        Log.Info(Name, $"'{player.Character.Name}' exit object {exitId} refused, {code} ({(short)code}): {why}");
        return new ChangeClusterResponse { ReturnCode = (short)code };
    }

    /// <summary>
    /// The target of a deferred change is in (World.Post, under the lock): if the session is still in the world on the map
    /// the exit was used on, the checks run again and the answer goes out with the kept request id; a failed load is
    /// refused with 1641 (SPEC §4.2). A session that left meanwhile gets nothing.
    /// </summary>
    private void Resume(PendingChange pending, Task<ClusterTemplate> load)
    {
        if (_changing != pending) return;
        _changing = null;
        if (_closed || Status != SessionStatus.InWorld || Player != pending.Player || pending.Player.Map != pending.Map)
        {
            Log.Info(Name, $"exit object {pending.ExitId}: the player has left {pending.Map.Cluster} meanwhile, no answer");
            return;
        }
        ChangeClusterResponse? resp;
        if (load.IsCompletedSuccessfully) resp = Change(pending.Player, pending.ExitId, load.Result, pending.RequestId, pending.Encrypted);
        else
        {
            Log.Warn(Name, $"exit object {pending.ExitId}: the target cluster could not be resolved: {load.Exception?.InnerException?.Message ?? "load cancelled"}");
            resp = Refuse(pending.Player, pending.ExitId, ChangeClusterRefusal.ExitNotConnected, "target cluster not resolved");
        }
        if (resp is not null) SendResponse(OperationCodes.ChangeCluster, resp, pending.RequestId, pending.Encrypted);
    }

    /// <summary>
    /// .tele (SPEC §6): the commit of an exit change with Via "tele", then the ChangeCluster response pushed without a
    /// request id (§2.6: c3s.akx still hands it to ci7.x_8 by op code, which runs the whole change; official uses this push
    /// after BuyJourney). Only for a session in the world, which has sent JoinFinished (the client needs its local player).
    /// <paramref name="via"/>: the <see cref="PendingTransfer.Via"/> ("tele"; stageE's respawn of a dead player, <see cref="Systems.Death.RespawnVia"/>).
    /// <paramref name="world"/>: into an expedition instance (stageH expeditions SPEC §2.6), key 1 = the world.xml cluster the client builds
    /// the instanced copy from (never key 0 "@EXPEDITION@…" without it: the client would throw); also onto an island (stageH islands SPEC
    /// §1.3), with <paramref name="name"/> as key 2 (the owner's name, the map's title); into a corrupted dungeon (stageL corrupted SPEC §2.3)
    /// also with <paramref name="type"/> as key 4, the worldsettings cluster type the client builds the instance as (else world.xml's); into a mists
    /// zone (stageL mists SPEC §3.4) {0 id, 3 <paramref name="layout"/> (a9i), 4 the mode's clustertype, 5 <paramref name="tier"/>}.
    /// </summary>
    internal void Teleport(Arrival to, string via = "tele", string? world = null, string? name = null, string? type = null, byte[]? layout = null, int tier = 0)
    {
        if (Status != SessionStatus.InWorld || Player is not { } player)
            throw new InvalidOperationException($"{Name}: only a session in the world can be teleported (it is {Status})");
        Commit(player, to, via);
        SendResponse(OperationCodes.ChangeCluster, new ChangeClusterResponse { anu = to.Cluster, anv = world, anw = name, anx = layout, any = type, anz = tier }, requestId: null, encrypted: false);
    }

    /// <summary>
    /// SPEC §4.1 "Commit", under World.Sync before the response is queued: the logout countdown stops; the player leaves the
    /// world as on a disconnect (<see cref="Leave"/>: OnLogout, items saved when a hook ran, off the map; its own client gets
    /// no Leave); the live character takes the target cluster, the arrival point and facing, the cluster it came from and
    /// the time, and a <see cref="PendingTransfer"/>, and is saved now (ARCHITECTURE.md §5.3). The order matters: the save
    /// refreshes the position from the player's last Move (World.StorePosition) for a player still on a map, which would put
    /// the old position back over the arrival. Then the session is detached for good (<see cref="SessionStatus.Transferring"/>):
    /// no player, no character, no keepalive (only players on maps get one), every later request dropped. The client
    /// disconnects it itself 1-16 ms after the response; the server never does (a DISCONNECT that came first would send the
    /// client to the login screen, §4.1), and a lost DISCONNECT ends it by the 30 s timeout. Another connection playing the
    /// same live character goes first (<see cref="DetachDuplicate"/>), for the same reason as the order.
    /// </summary>
    private void Commit(Player player, Arrival to, string via)
    {
        var c = player.Character;
        var from = player.Map?.Cluster ?? c.Cluster;
        CancelLogout();
        foreach (var other in world.Maps.Maps.SelectMany(m => m.Players.Values).Where(p => p != player && p.Character == c).ToList())
            other.Session.DetachDuplicate(other, $"which another one takes to {to.Cluster}");
        Leave(player);
        var now = DateTime.UtcNow.Ticks;
        c.Cluster = to.Cluster;
        c.PositionX = to.Position.X;
        c.PositionY = to.Position.Y;
        c.Direction = to.Direction;
        c.PreviousCluster = from;
        c.LastLeftAt = now;
        c.PendingTransfer = new PendingTransfer(from, to.Cluster, to.Position.X, to.Position.Y, to.Direction, now, via);
        world.Saves.SaveNow(c);
        Detach();
        Log.Info(Name, string.Create(CultureInfo.InvariantCulture,
            $"'{c.Name}' leaves {from} for {to.Cluster} ({via}): arrival ({to.Position.X:0.###}, {to.Position.Y:0.###}) facing {to.Direction:0.####}, saved"));
    }

    /// <summary>This session's part in the world ends for good (<see cref="SessionStatus.Transferring"/>): no player, no character, no change waiting.</summary>
    private void Detach()
    {
        Status = SessionStatus.Transferring;
        Player = null;
        Character = null;
        _changing = null;
    }

    /// <summary>
    /// <paramref name="stale"/>, this session's player, plays the live character another connection is taking to
    /// <paramref name="to"/> (<see cref="Commit"/>). That happens when a client lost its game connection without a
    /// DISCONNECT and joined again (a reconnect, a quick relogin) while the server keeps the old one up to its 30 s timeout:
    /// the new Join shares the live character (World.LoadCharacter). The old player leaves the world before the commit's
    /// save: else the save would refresh the position from that player's last Move (World.StorePosition) over the arrival,
    /// and the next Join would take the character from that player, still in the old cluster (World.LoadCharacter). Its
    /// session is detached as the committing one, so its end saves nothing either; it is not disconnected (Transferring:
    /// closed by its timeout, or replaced by a CONNECT from its endpoint). StageE E3: also at the new Join itself
    /// (CharacterHandler), so a lethal death cannot drop the items of one copy while the other still holds them.
    /// </summary>
    internal void DetachDuplicate(Player stale, string why)
    {
        CancelLogout();
        Leave(stale);
        Detach();
        Log.Info(Name, $"'{stale.Character.Name}' is also played on this connection, {why}: detached");
    }

    /// <summary>The first 8 hex digits of a Guid, as the logs and .exits name exits.</summary>
    internal static string Short(Guid id) => id.ToString()[..8];
}
