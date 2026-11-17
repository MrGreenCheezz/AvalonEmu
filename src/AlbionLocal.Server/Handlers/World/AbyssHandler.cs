using System.Numerics;
using AlbionLocal.Photon;
using AlbionLocal.Server.Entities;
using AlbionLocal.Server.Protocol;
using AlbionLocal.Server.Systems;

namespace AlbionLocal.Server.Network;

/// <summary>
/// The Abyssal Depths (analysis/work/stageR/abyss/SPEC.md, "SPEC" here; Systems/AbyssalDepths): the Hell Den entrance's op 532 and the floors' exits.
/// </summary>
public sealed partial class WorldSession
{
    /// <summary>
    /// HellDungeonUseStaticEntrance (op 532, vq) {0 GameTimeStamp, 1 the entrance (638), 2 the mode's id}: sent by the entrance window's Join
    /// (HellDungeonEntranceGui.OnClickJoin 0x18105DE80 → cnz.af(bum, Action) → c32.j) once the client's own checks pass. Accepted → a bare RC 0
    /// (OnRegisterResponse ignores it) and the way in follows (Systems/AbyssalDepths.Update); refused → ajj {0 blocked until, 1 closed until} with the
    /// code the window shows (21280-21287, SPEC §2.2).
    /// </summary>
    [Handles(OperationCodes.HellDungeonUseStaticEntrance, Status = SessionStatus.InWorld)]
    private AlbionResponse? HellDungeonUseStaticEntrance(HellDungeonUseStaticEntranceRequest req)
    {
        if (Player is not { } player) return null;
        var (code, blocked, closed, why) = world.Abyss.Use(player, req.pz, req.p0);
        if (code == 0) return new HellDungeonUseStaticEntranceResponse();
        Log.Info(Name, $"'{player.Character.Name}' abyssal depths entrance {req.pz} (mode {req.p0}) refused, {code}: {why}");
        return new HellDungeonUseStaticEntranceResponse { ReturnCode = code, bnm = blocked, bnn = closed };
    }

    /// <summary>
    /// ChangeCluster {0: an Abyssal Depths exit} (the views' EnterExit, SPEC §2.3): in its square and up, then the upward exit out to the way back
    /// ({0: the Hell Den}), an Open downward exit into the next floor ({0 id, 3 a9c, 4 clustertype, 5 tier}; a floor not built yet is answered once
    /// it is, with the kept request id). False = not such an exit.
    /// </summary>
    private bool AbyssChange(Player player, ChangeClusterRequest req, out ChangeClusterResponse? response)
    {
        response = null;
        if (player.Map is not { } map || !map.Objects.TryGetValue(req.ee, out var o) || o is not AbyssExit exit) return false;
        if (player.Combat.Down is { } down)
        {
            response = Refuse(player, req.ee, ChangeClusterRefusal.NotNearAnExit, $"knocked down ({down.Kind})");
            return true;
        }
        if (Reach(player, map, exit.Position, Vector2.Max(exit.Size, new Vector2(world.DungeonSettings.MinSquare)), exit.Kind) is { } refused)
        {
            response = Refuse(player, req.ee, refused.Code, refused.Why);
            return true;
        }
        var abyss = world.Abyss;
        var from = abyss.Find(map.Cluster);
        if (exit.Upward || from is null)
        {
            var to = abyss.Out(player, from);
            Commit(player, to, $"abyss:{map.Cluster}:up");
            response = new ChangeClusterResponse { anu = to.Cluster };
            return true;
        }
        if (!exit.Downward || exit.State != (int)AbyssDownwardState.Open)
        {
            response = Refuse(player, req.ee, ChangeClusterRefusal.ExitNotConnected, $"the {exit.ExitKind} is not open");
            return true;
        }
        var (ready, build, team, why) = abyss.NextFloor(player, from);
        if (ready is not null)
        {
            response = Down(player, ready, team);
            return true;
        }
        if (build is null)
        {
            response = Refuse(player, req.ee, ChangeClusterRefusal.ExitNotConnected, why ?? "no deeper floor");
            return true;
        }
        var pending = new PendingChange(player, map, exit.Id, RequestId, RequestEncrypted);
        _changing = pending;
        Log.Info(Name, $"'{player.Character.Name}' takes a downward exit of {from}: building the next floor, the answer follows");
        build.ContinueWith(t => world.Post(() =>
        {
            if (_changing != pending || _closed || Status != SessionStatus.InWorld || Player != pending.Player || pending.Player.Map != pending.Map)
            {
                if (_changing == pending) _changing = null;
                if (t.IsCompletedSuccessfully) abyss.Register(t.Result, from.Back); // the floor stands for the rest of the team
                return;
            }
            _changing = null;
            var resp = t.IsCompletedSuccessfully ? Down(player, abyss.Register(t.Result, from.Back), team)
                : Refuse(player, pending.ExitId, ChangeClusterRefusal.ExitNotConnected, $"the next floor was not built: {t.Exception?.InnerException?.Message}");
            SendResponse(OperationCodes.ChangeCluster, resp, pending.RequestId, pending.Encrypted);
        }), TaskScheduler.Default);
        return true;
    }

    /// <summary>Into the next floor: admitted (its team counted in, the way back kept), committed, answered {0 id, 3 a9c, 4 clustertype, 5 tier}.</summary>
    private ChangeClusterResponse Down(Player player, AbyssInstance next, string team)
    {
        var to = world.Abyss.Admit(player, next, team, back: null);
        Commit(player, to, $"abyss:{next.Id}:down");
        return new ChangeClusterResponse { anu = next.Id, anx = next.Bytes, any = next.Mode.ClusterType, anz = next.Tier };
    }
}
