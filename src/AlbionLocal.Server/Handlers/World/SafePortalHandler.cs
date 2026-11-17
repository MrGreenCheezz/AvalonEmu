using System.Globalization;
using System.Numerics;
using AlbionLocal.Photon;
using AlbionLocal.Server.Data;
using AlbionLocal.Server.Entities;
using AlbionLocal.Server.Game;
using AlbionLocal.Server.Protocol;
using AlbionLocal.Server.Systems;

namespace AlbionLocal.Server.Network;

/// <summary>
/// The S.A.F.E. portal of an Outlands portal town (analysis/work/stageR/safe-portal/SPEC.md, "SPEC" here): the window's costs (op 517), the
/// use (a ChangeCluster at the town portal with the cost in key 4: CostsModule.OnClickUse → f8q.mn → cyd.u(cnf, a7m) → c32.i) and the way
/// back (a ChangeCluster at the personal return portal: OutlandsTeleportationReturnPortalObjectView.EnterExit 0x181296550). The refusals are
/// RcOpChangeCluster's (Albion.Common.Photon).
/// </summary>
public sealed partial class WorldSession
{
    /// <summary>RcOpChangeCluster 1653 KnockedDownOrDead.</summary>
    private const ChangeClusterRefusal KnockedDownOrDead = (ChangeClusterRefusal)1653;

    /// <summary>RcOpChangeCluster 1671 UsageBlockedByObjectBlockerBuff (the blocker debuff).</summary>
    private const ChangeClusterRefusal BlockedByDebuff = (ChangeClusterRefusal)1671;

    /// <summary>RcOpChangeCluster 1672 SilverCostsChanged: the client drops its cached 517 answer and asks again (f8q.l4(long, g7q) 0x181C76F90).</summary>
    private const ChangeClusterRefusal SilverCostsChanged = (ChangeClusterRefusal)1672;

    /// <summary>RcOpChangeCluster 1673 NoEnoughSilver.</summary>
    private const ChangeClusterRefusal NoEnoughSilver = (ChangeClusterRefusal)1673;

    /// <summary>RcOpChangeCluster 1674 NoOutlandsClusterFound.</summary>
    private const ChangeClusterRefusal NoOutlandsClusterFound = (ChangeClusterRefusal)1674;

    /// <summary>RcOpChangeCluster 1675 NoExitFoundInOutlandCluster.</summary>
    private const ChangeClusterRefusal NoExitFoundInOutlandCluster = (ChangeClusterRefusal)1675;

    /// <summary>
    /// How near the player must stand to a S.A.F.E. or return portal [assume: the client's own range, a setting at +0x6d0 of its world settings,
    /// is not traced to a number; the window only opens in range].
    /// </summary>
    public const float SafePortalReach = 10;

    /// <summary>How far past the return portal the player arrives, along the facing [assume: not on the portal itself].</summary>
    public const float ReturnPortalOffset = 3;

    /// <summary>
    /// RequestOutlandsTeleportationUsage (op 517, z9 without keys; sent when the S.A.F.E. window opens, f8q ctor 0x181C76450 / f8q.l4(bool)):
    /// air {0 the uses that count (GameTimeStamp[]), 1 InitialCost (the baggage), 2 TravelCosts (with the repeat-use tax), 3 NextPriceChange}
    /// (f8q.l4(air) 0x181C768F0; the client adds the global discount itself). Without the data file an empty answer.
    /// </summary>
    [Handles(OperationCodes.RequestOutlandsTeleportationUsage, Status = SessionStatus.InWorld)]
    private RequestOutlandsTeleportationUsageResponse? RequestOutlandsTeleportationUsage(RequestOutlandsTeleportationUsageRequest req)
    {
        if (Player is not { } player) return null;
        if (world.SafePortals.Quote(player) is not { } q) return new RequestOutlandsTeleportationUsageResponse();
        return new RequestOutlandsTeleportationUsageResponse { bk4 = q.Uses.Length > 0 ? q.Uses : null, bk5 = q.Initial, bk6 = q.Travel, bk7 = q.NextChange ?? 0 };
    }

    /// <summary>Whether the request is for a S.A.F.E. portal or a return portal of the player's map, and its answer (null = deferred).</summary>
    private bool SafePortalChange(Player player, ChangeClusterRequest req, out ChangeClusterResponse? response)
    {
        response = null;
        if (player.Map is not { } map || !map.Objects.TryGetValue(req.ee, out var o)) return false;
        if (o is SafePortal portal)
        {
            response = UseSafePortal(player, portal, req.ei, null, null, RequestId, RequestEncrypted);
            return true;
        }
        if (o is SafeReturnPortal back)
        {
            response = UseReturnPortal(player, back, null, RequestId, RequestEncrypted);
            return true;
        }
        return false;
    }

    /// <summary>
    /// The use (SPEC §3), in order: not knocked down (1653), in reach (1640), not under the blocker debuff (1671), the town has destinations
    /// in outlandsteleportationportals.xml that world.xml knows (1674), the client's total (key 4) is today's travel cost after the global
    /// discount (1672 otherwise). Then a destination is drawn [assume: evenly], resolved in the background when needed, and one of its exits
    /// with a spawnpos drawn (1675 without one) [infer: RcOpChangeCluster 1675 NoExitFoundInOutlandCluster]; the silver is taken (1673), the
    /// binding made with the return portal at that spawnpos, and the player goes there facing away from the exit.
    /// </summary>
    private ChangeClusterResponse? UseSafePortal(Player player, SafePortal portal, long clientCost, string? drawn, ClusterTemplate? resolved, int? requestId, bool encrypted)
    {
        var map = player.Map!;
        var safe = world.SafePortals;
        if (player.Combat.Down is { } down) return Refuse(player, portal.Id, KnockedDownOrDead, $"knocked down ({down.Kind})");
        var at = player.PositionAt(DateTime.UtcNow);
        if (Vector2.Distance(at, portal.Position) > SafePortalReach + world.Config.Transfer.PositionTolerance)
            return Refuse(player, portal.Id, ChangeClusterRefusal.NotNearAnExit, string.Create(CultureInfo.InvariantCulture, $"{Vector2.Distance(at, portal.Position):0.#} m from the S.A.F.E. portal"));
        if (safe.Blocked(player)) return Refuse(player, portal.Id, BlockedByDebuff, "the blocker debuff is on");
        if (safe.Data is not { } data || !data.Destinations.TryGetValue(map.Cluster, out var list)) return Refuse(player, portal.Id, NoOutlandsClusterFound, $"no destinations for {map.Cluster}");
        var known = list.Where(id => world.Clusters?.World.Find(id) is not null).ToList();
        if (known.Count == 0) return Refuse(player, portal.Id, NoOutlandsClusterFound, $"none of the {list.Count} destinations of {map.Cluster} is in world.xml");
        if (safe.Quote(player) is not { } quote) return Refuse(player, portal.Id, NoOutlandsClusterFound, "no data");
        if (!Pricing.Accepts(clientCost, quote.Travel, world.Config.GoldPriceIndex))
            return Refuse(player, portal.Id, SilverCostsChanged, string.Create(CultureInfo.InvariantCulture, $"the client's cost {clientCost / 10000.0:0.##} is not {quote.Travel / 10000.0:0.##}"));
        var target = drawn ?? known[map.Random.Next(known.Count)];
        var template = resolved ?? world.CachedCluster(target);
        if (template is null)
        {
            if (world.ResolveCluster(target) is not { } load) return Refuse(player, portal.Id, NoOutlandsClusterFound, "this server has no cluster data");
            var pending = new PendingChange(player, map, portal.Id, requestId, encrypted);
            _changing = pending;
            Log.Info(Name, $"'{player.Character.Name}' uses the S.A.F.E. portal of {map.Cluster} to {target}: resolving {target}, the answer follows");
            load.ContinueWith(t => world.Post(() => ResumeSafe(pending, t, loaded => UseSafePortal(player, portal, clientCost, target, loaded, pending.RequestId, pending.Encrypted))),
                TaskScheduler.Default);
            return null;
        }
        var exits = template.Exits.Where(e => e.World is not null && e.SpawnPosition is not null).ToList();
        if (exits.Count == 0) return Refuse(player, portal.Id, NoExitFoundInOutlandCluster, $"{target} has no exit with a spawnpos");
        var exit = exits[map.Random.Next(exits.Count)];
        var spot = exit.SpawnPosition!.Value;
        var facing = Transfers.Facing(exit.Position, spot);
        var charge = Pricing.Discounted(quote.Travel, world.Config.GoldPriceIndex);
        if (!Wallet.TrySpend(player, charge, "S.A.F.E. portal"))
            return Refuse(player, portal.Id, NoEnoughSilver, string.Create(CultureInfo.InvariantCulture, $"{charge / 10000.0:0.##} silver needed"));
        var away = spot - exit.Position;
        var arrival = away.LengthSquared() > 0.0001f ? spot + ReturnPortalOffset * Vector2.Normalize(away) : spot;
        safe.Bind(player, map.Cluster, target, spot);
        player.SendAround(new PlayerUsedOutlandsTeleportationPortalEvent { ddn = player.Id }); // 610 [infer: the use's effect]
        Commit(player, new Arrival(target, arrival, facing), $"safeportal:{map.Cluster}");
        return new ChangeClusterResponse { anu = target };
    }

    /// <summary>
    /// The way back (SPEC §5): the player's own return portal (owner and binding id, 1641 otherwise), the binding still on, not knocked down
    /// (1653), in reach (1640); then the portal town is resolved and the player arrives at its portal exit's spawnpos [assume: the S.A.F.E.
    /// portal has no spawnpos of its own], the binding ends (the next Join carries none).
    /// </summary>
    private ChangeClusterResponse? UseReturnPortal(Player player, SafeReturnPortal back, ClusterTemplate? resolved, int? requestId, bool encrypted)
    {
        var map = player.Map!;
        var c = player.Character;
        if (back.Owner != c.Id || SafePortals.Active(c, world.GameNow) is not { } b || b.Id != back.BindingId)
            return Refuse(player, back.Id, ChangeClusterRefusal.ExitNotConnected, "not the player's live return portal");
        if (player.Combat.Down is { } down) return Refuse(player, back.Id, KnockedDownOrDead, $"knocked down ({down.Kind})");
        var at = player.PositionAt(DateTime.UtcNow);
        if (Vector2.Distance(at, back.Position) > SafePortalReach + world.Config.Transfer.PositionTolerance)
            return Refuse(player, back.Id, ChangeClusterRefusal.NotNearAnExit, string.Create(CultureInfo.InvariantCulture, $"{Vector2.Distance(at, back.Position):0.#} m from the return portal"));
        var town = resolved ?? world.CachedCluster(b.Town);
        if (town is null)
        {
            if (world.ResolveCluster(b.Town) is not { } load) return Refuse(player, back.Id, ChangeClusterRefusal.ExitNotConnected, "this server has no cluster data");
            var pending = new PendingChange(player, map, back.Id, requestId, encrypted);
            _changing = pending;
            load.ContinueWith(t => world.Post(() => ResumeSafe(pending, t, loaded => UseReturnPortal(player, back, loaded, pending.RequestId, pending.Encrypted))), TaskScheduler.Default);
            return null;
        }
        var safe = town.Critical.FirstOrDefault(m => m.Type == SafePortals.MemberType);
        var arrival = town.PortalExits.FirstOrDefault(p => p.SpawnPosition is not null) is { SpawnPosition: { } spot } door
            ? new Arrival(town.Id, spot, Transfers.Facing(door.Position, spot))
            : safe is not null ? new Arrival(town.Id, safe.Position + new Vector2(0, -ReturnPortalOffset), 180) : (Arrival?)null;
        if (arrival is not { } to) return Refuse(player, back.Id, ChangeClusterRefusal.ExitNotConnected, $"no arrival point in {b.Town}");
        world.SafePortals.Unbind(c, "went back through the return portal");
        Commit(player, to, $"safereturn:{map.Cluster}");
        return new ChangeClusterResponse { anu = town.Id };
    }

    /// <summary>The cluster of a deferred S.A.F.E. use or return is in (World.Post): if the session still waits on the same map, the pass runs again.</summary>
    private void ResumeSafe(PendingChange pending, Task<ClusterTemplate> load, Func<ClusterTemplate, ChangeClusterResponse?> again)
    {
        if (_changing != pending) return;
        _changing = null;
        if (_closed || Status != SessionStatus.InWorld || Player != pending.Player || pending.Player.Map != pending.Map)
        {
            Log.Info(Name, $"S.A.F.E. object {pending.ExitId}: the player has left {pending.Map.Cluster} meanwhile, no answer");
            return;
        }
        var resp = load.IsCompletedSuccessfully ? again(load.Result)
            : Refuse(pending.Player, pending.ExitId, ChangeClusterRefusal.ExitNotConnected, "target cluster not resolved");
        if (resp is not null) SendResponse(OperationCodes.ChangeCluster, resp, pending.RequestId, pending.Encrypted);
    }
}
