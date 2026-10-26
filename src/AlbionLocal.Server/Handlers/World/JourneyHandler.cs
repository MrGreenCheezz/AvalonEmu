using System.Globalization;
using AlbionLocal.Photon;
using AlbionLocal.Server.Data;
using AlbionLocal.Server.Entities;
using AlbionLocal.Server.Protocol;

namespace AlbionLocal.Server.Network;

/// <summary>
/// The refusals of BuyJourney this server uses (client enum jc, 8040-8050). ggd.as 0x181cd7280, the request's callback,
/// shows a message box for each: 8040 "@FASTTRAVELPOINT_ERROR_BUY_JOURNEY_NOT_POSSIBLE", 8041 _NOT_ENOUGH_SILVER, 8042
/// _NO_LUGGAGE_ALLOWED, 8043 _REPUTATION_TOO_LOW, 8044/8045 @CHANGECLUSTER_ERROR_NOFACTIONPL…/_NOOPPOSINGF…, 8046
/// @ISLAND_ACCESS_CURRENTLY_UPGRADI…, 8047 @HIDEOUT_ACCESS_CURRENTLY_UPGRAD…, 8048 @CHANGECLUSTER_ERROR_CLUSTER_FUL…, 8049
/// _TOO_EARLY, 8050 _NOT_ALLOWED_AS_GUEST (mapping by the switch's jump table at 0x181cd7520); RC 0 and any other code
/// end the travel window's wait (its Action&lt;bool&gt;, true only for RC 0).
/// </summary>
public enum BuyJourneyRefusal : short
{
    /// <summary>No such travel point, target or route, or a journey this server does not make (islands, home).</summary>
    NotPossible = 8040,

    /// <summary>The island is being upgraded (stageH islands SPEC §1.3: within 130 s of its level change).</summary>
    IslandUpgrading = 8046,

    /// <summary>The home hideout is being upgraded (@HIDEOUT_ACCESS_CURRENTLY_UPGRADING, stageP hideouts H3).</summary>
    HideoutUpgrading = 8047,

    /// <summary>The character's silver is below the client's price (stage 6).</summary>
    NotEnoughSilver = 8041,

    /// <summary>Only a no-luggage route reaches the target and the inventory is not empty.</summary>
    NoLuggageAllowed = 8042,
}

/// <summary>
/// Fast travel from a travel point (the harbour NPC of a city; ROADMAP stage 3, SPEC stage 3 §10 "travel-point journeys",
/// capture 002835 1000 → 0000). The client works out the destinations and the price itself (travel window
/// ggq/ggd) and sends BuyJourney; the official server answers it with a bare RC 0, then pushes the ChangeCluster response
/// {0: target cluster} without a request id, which runs the whole change as after an exit (§2.6): the client drops the game
/// connection, asks the login connection for the game server (op 17) and joins on a new one. The commit and the new Join
/// are those of an exit (<see cref="Commit"/>, SPEC §4.1/§4.5).
/// </summary>
public sealed partial class WorldSession
{
    /// <summary>
    /// A journey this session may make: from which object (a travel point, or an island merchant; FromGuid its world.xml id), to which
    /// world.xml travel point (Guid.Empty onto an island: its template's one), by which route, onto which island (stageH islands SPEC §1.3;
    /// null for every other journey).
    /// </summary>
    private sealed record JourneyPlan(WorldObject From, Guid FromGuid, Guid To, string Cluster, JourneyRoute Route, Game.Island? Island = null, Game.Hideout? Hideout = null);

    /// <summary>
    /// BuyJourney (op 201, client class pz, plaintext, sent by c32.i 0x180a6ec50 from the travel window, ggd.d.f
    /// 0x180c5b060): 0 the object id of the NewTravelpoint used (an island trip: the NewIslandAccessPoint's), 1 the target
    /// "&lt;travelpoint guid&gt;@&lt;cluster&gt;", 2 the kind (pz.a: 0 a journey; the window's "home" entry is 1), 3 the island Guid
    /// (island trips only), 4 the price the client worked out (a7m: silver × 10⁴; captured 369226400 = 36,922.64 silver,
    /// which the official server took exactly: UpdateMoney {0: player, 1: new silver} came 0.132 s after the request, with
    /// the response). Checked: the travel point is on the player's map and in its view, the kind is a plain journey, the
    /// target a world.xml travel point that the client's route rule reaches (<see cref="Transfers.Route"/>; the no-luggage
    /// route only with an empty inventory, else 8042). Refused with 8040 otherwise. Island trips (stageH islands SPEC §1.3, §1.4):
    /// from the island merchant onto an own island (<see cref="IslandPlan"/>), and from an island's travel point back to its city. Price (stage 6 SPEC §0): the client's key 4 is charged as it is, with UpdateMoney
    /// before the bare RC-0 response as official (refused with 8041 when the wallet holds less, 8040 for a negative price);
    /// the server's own price (Transfers.Route's cost × the client's luggage value, NOTES-protocol.md "Travel-point journeys")
    /// is not worked out, so the §0.1 upper bound is not checked.
    /// Arrival = the target travel point's spawnpos turned with its tile (its pos + the rotated spawnpos, exact in the
    /// capture), facing = the player's last Move direction (<see cref="Player.Facing"/>; the atan2 rule of exits would have
    /// given 225.88 instead of 310.45593). A target cluster not resolved yet is resolved in the background first, as for an
    /// exit (SPEC §4.2); a second request meanwhile is refused. The answers are sent here (null to the dispatcher): the
    /// bare RC 0 {253, 255} as official, then the push. No Leave burst (SPEC §2.2).
    /// </summary>
    [Handles(OperationCodes.BuyJourney, Status = SessionStatus.InWorld)]
    private AlbionResponse? BuyJourney(BuyJourneyRequest req)
    {
        if (Player is not { } player) return null;
        if (_changing is not null) return RefuseJourney(player, req.df, BuyJourneyRefusal.NotPossible, $"object {_changing.ExitId} is being resolved for a cluster change already");
        if (player.Combat.Down is { } down) // stageE E3: no way out for the knocked-down and the dead [infer: no own code]
            return RefuseJourney(player, req.df, BuyJourneyRefusal.NotPossible, $"knocked down ({down.Kind})");
        return Journey(player, req, null, RequestId, RequestEncrypted);
    }

    /// <summary>The checks, then the journey into <paramref name="resolved"/> (a deferred journey's target) or the cached target; null = answered here, or deferred.</summary>
    private AlbionResponse? Journey(Player player, BuyJourneyRequest req, ClusterTemplate? resolved, int? requestId, bool encrypted)
    {
        var (plan, code, why) = Plan(player, req);
        if (plan is null) return RefuseJourney(player, req.df, code, why);
        var target = resolved ?? world.CachedCluster(plan.Cluster);
        if (target is null)
        {
            if (world.ResolveCluster(plan.Cluster) is not { } load)
                return RefuseJourney(player, req.df, BuyJourneyRefusal.NotPossible, "this server has no cluster data");
            var pending = new PendingChange(player, player.Map!, req.df, requestId, encrypted, req);
            _changing = pending;
            Log.Info(Name, $"'{player.Character.Name}' journey to {plan.Cluster}: resolving {plan.Cluster}, the answer follows");
            load.ContinueWith(t => world.Post(() => ResumeJourney(pending, t)), TaskScheduler.Default);
            return null;
        }
        if ((plan.Island is not null || plan.Hideout is not null ? target.Travelpoints.FirstOrDefault() : target.Travelpoints.FirstOrDefault(t => t.Guid == plan.To)) is not { SpawnPosition: { } at })
        {
            Log.Warn(Name, $"travel point {Short(plan.To)} of {plan.Cluster} is not in its template or has no spawnpos");
            return RefuseJourney(player, req.df, BuyJourneyRefusal.NotPossible, $"no arrival point in {plan.Cluster}");
        }
        if (Systems.Flagging.EntryRefusal(player, target.Pvp) is { } barred) // stageE: the target's PvP entry rules, as at an exit [infer: no own code]
            return RefuseJourney(player, req.df, BuyJourneyRefusal.NotPossible, barred.Why);
        var from = player.Map!.Cluster;
        if (req.dj < 0) return RefuseJourney(player, req.df, BuyJourneyRefusal.NotPossible, $"a negative price {req.dj}");
        if (!Systems.Wallet.TrySpend(player, req.dj, $"journey to {plan.Cluster}"))
            return RefuseJourney(player, req.df, BuyJourneyRefusal.NotEnoughSilver, $"{player.Character.Silver / 10000} silver, the journey costs {req.dj / 10000}");
        Log.Info(Name, string.Create(CultureInfo.InvariantCulture,
            $"'{player.Character.Name}' journey {from} -> {plan.Cluster} via {string.Join(" -> ", plan.Route.Clusters)} (cost {plan.Route.Cost:0.##}"
            + $"{(plan.Route.NoLuggage ? ", no luggage" : "")}): the client's price {req.dj / 10000.0:0.##} silver, charged"));
        if (player.Map.TutorialOwner is not null) Systems.TutorialJourney.Departed(player);
        Commit(player, new Arrival(plan.Cluster, at, player.Facing), plan.Island is { } island ? $"island:{island.Id}" : plan.Hideout is { } home ? $"hideout:{home.Id}:journey" : $"journey:{Short(plan.FromGuid)}");
        SendResponse(OperationCodes.BuyJourney, new GenericResponse(), requestId, encrypted);
        // Onto an island (SPEC §1.3, +0.021 s): {0 "@ISLAND@<guid>", 1 the template of its level, 2 the owner's name}, no 255.
        SendResponse(OperationCodes.ChangeCluster, new ChangeClusterResponse
        {
            anu = TutorialInstances.ClientCluster(player.Character, world), anv = plan.Island is null && plan.Hideout is null ? null : target.Id,
            anw = plan.Island?.OwnerName ?? (plan.Hideout is { } h ? world.Hideouts.Info(h).GuildName : null),
        }, requestId: null, encrypted: false);
        return null;
    }

    /// <summary>
    /// The checks of <see cref="BuyJourney"/> for the player now: the plan, or the refusal and why (for the log). "Empty
    /// inventory" is the bag here; the client's own check (ctp.atx 0x1809a7f00) looks at two containers, ctp+0x318 and
    /// +0x310, the second not identified.
    /// </summary>
    private (JourneyPlan? Plan, BuyJourneyRefusal Code, string Why) Plan(Player player, BuyJourneyRequest req)
    {
        const BuyJourneyRefusal no = BuyJourneyRefusal.NotPossible;
        if (player.Map is not { } map || !map.Objects.TryGetValue(req.df, out var o))
            return (null, no, $"object {req.df} is not on {player.Map?.Cluster ?? player.Character.Cluster}");
        if (o is IslandAccessPoint ap) return IslandPlan(player, map, ap, req);
        if (o is not Travelpoint from) return (null, no, $"object {req.df} is a {o.Kind}, no travel point");
        if (map.TutorialOwner is not null && !Systems.TutorialJourney.TravelAllowed(player))
            return (null, no, "the tutorial departure objectives have not been completed");
        if (!player.Visibility.Known.Contains(from)) return (null, no, $"travel point {req.df} is not in the player's view");
        // StageP hideouts (H3): the window's home entry (kind 1) with a home hideout: the Journey Back into it, at its travel point inside.
        if (req.dh == 1 && world.Hideouts.HomeOf(player.Character.Id) is { } homeHideout)
        {
            if (homeHideout.Instance == map.Cluster) return (null, no, "already in the home hideout");
            if (homeHideout.Upgrading) return (null, BuyJourneyRefusal.HideoutUpgrading, $"{homeHideout} is being upgraded");
            return (new JourneyPlan(from, from.Spawn.Guid, Guid.Empty, homeHideout.Instance, new JourneyRoute(0, false, [map.Cluster, homeHideout.Instance]), Hideout: homeHideout), default, "");
        }
        if (req.dh != 0 || req.di != Guid.Empty) return (null, no, $"journey kind {req.dh} (island {req.di}): only travel-point journeys are made");
        var text = req.dg ?? "";
        var at = text.IndexOf('@');
        var info = world.Clusters?.World;
        if (at < 0 || !Guid.TryParse(text[..at], out var to) || info?.Find(text[(at + 1)..]) is not { } cluster
            || cluster.Travelpoints.All(t => t.Id != to))
            return (null, no, $"target '{text}' is no world.xml travel point");
        // StageH islands (SPEC §1.4): an island's travel point leads only to the travel point of its access point (world.xml has no
        // connection from island travel points), at no cost.
        if (Data.IslandData.IsInstance(map.Cluster))
        {
            if (world.Islands.Find(map.Cluster) is not { } island) return (null, no, $"{map.Cluster} is no island of this server");
            var home = info.Find(island.City)?.IslandAccessPoints.FirstOrDefault(a => a.Id == island.AccessPointId)?.Travelpoint;
            if (cluster.Id != island.City || to != home) return (null, no, $"from {island} only to its city's travel point {home}@{island.City}, not '{text}'");
            return (new JourneyPlan(from, from.Spawn.Guid, to, cluster.Id, new JourneyRoute(0, false, [map.Cluster, cluster.Id])), default, "");
        }
        // StageH islands (SPEC §2.4, Q5): from an island showroom only back to the travel point of its city's island merchant, at no cost.
        if (world.Islands.CityOfShowroom(map.Cluster) is { } showroomCity)
        {
            var merchant = info.Find(showroomCity)?.IslandAccessPoints.FirstOrDefault()?.Travelpoint;
            if (cluster.Id != showroomCity || to != merchant) return (null, no, $"from the showroom {map.Cluster} only to {merchant}@{showroomCity}, not '{text}'");
            return (new JourneyPlan(from, from.Spawn.Guid, to, cluster.Id, new JourneyRoute(0, false, [map.Cluster, cluster.Id])), default, "");
        }
        var source = Transfers.TravelpointKey(from.Spawn.Guid, map.Cluster);
        var targetKey = Transfers.TravelpointKey(to, cluster.Id);
        if (source == targetKey) return (null, no, "the target is the travel point used");
        if (Transfers.Route(info, source, targetKey) is not { } route) return (null, no, $"no travel route from {source} to {targetKey}");
        if (route.NoLuggage && player.Inventory.Bag.Count > 0)
            return (null, BuyJourneyRefusal.NoLuggageAllowed, $"only a no-luggage route reaches {cluster.Id} ({string.Join(" -> ", route.Clusters)}) and the inventory holds {player.Inventory.Bag.Count} item(s)");
        return (new JourneyPlan(from, from.Spawn.Guid, to, cluster.Id, route), default, "");
    }

    /// <summary>
    /// City → island (stageH islands SPEC §1.3, cap 103829): BuyJourney {0 the NewIslandAccessPoint, 1 "&lt;its travelpointid&gt;@&lt;city&gt;",
    /// 2 0, 3 the island}. Checked: islands on, the merchant in view, a plain journey, the island the player's own (phase 1: owner only)
    /// and reached from this merchant, key 1 its travel point, not upgrading (8046); the target is the island's instance, its arrival the
    /// template's travel point (Guid.Empty), no route (the client prices a same-city trip 0).
    /// </summary>
    private (JourneyPlan? Plan, BuyJourneyRefusal Code, string Why) IslandPlan(Player player, Map map, IslandAccessPoint ap, BuyJourneyRequest req)
    {
        const BuyJourneyRefusal no = BuyJourneyRefusal.NotPossible;
        if (!world.Islands.Enabled) return (null, no, "islands are off (Islands:Enabled)");
        if (!player.Visibility.Known.Contains(ap)) return (null, no, $"island merchant {req.df} is not in the player's view");
        if (req.dh != 0) return (null, no, $"journey kind {req.dh} to an island");
        if (world.Islands.Find(req.di) is not { } island) return (null, no, $"no island {req.di}");
        if (island.Owner != player.Character.Id) return (null, no, $"{island} is not the player's");
        if (island.AccessPointId != ap.Spawn.Guid || island.City != map.Cluster)
            return (null, no, $"{island} is not reached from the merchant {Short(ap.Spawn.Guid)}@{map.Cluster}");
        var text = req.dg ?? "";
        if (ap.Spawn.World is { } w && (text.IndexOf('@') is not (> 0 and var at) || !Guid.TryParse(text[..at], out var tp) || tp != w.Travelpoint
                || text[(at + 1)..] != map.Cluster))
            return (null, no, $"target '{text}' is not the merchant's travel point {w.Travelpoint}@{map.Cluster}");
        if (island.Upgrading(world.GameNow)) return (null, BuyJourneyRefusal.IslandUpgrading, $"{island} is being upgraded");
        if (world.Islands.WorldOf(island.Cluster) is null) return (null, no, $"{island}: no template (island types not read)");
        return (new JourneyPlan(ap, ap.Spawn.Guid, Guid.Empty, island.Cluster, new JourneyRoute(0, false, [map.Cluster, island.Cluster]), island), default, "");
    }

    private GenericResponse RefuseJourney(Player player, long objectId, BuyJourneyRefusal code, string why)
    {
        Log.Info(Name, $"'{player.Character.Name}' journey from object {objectId} refused, {code} ({(short)code}): {why}");
        return new GenericResponse { ReturnCode = (short)code };
    }

    /// <summary>The target of a deferred journey is in (World.Post, under the lock): as <see cref="Resume"/>, checked again, answered with the kept request id.</summary>
    private void ResumeJourney(PendingChange pending, Task<ClusterTemplate> load)
    {
        if (_changing != pending) return;
        _changing = null;
        if (_closed || Status != SessionStatus.InWorld || Player != pending.Player || pending.Player.Map != pending.Map)
        {
            Log.Info(Name, $"journey from object {pending.ExitId}: the player has left {pending.Map.Cluster} meanwhile, no answer");
            return;
        }
        AlbionResponse? resp;
        if (load.IsCompletedSuccessfully) resp = Journey(pending.Player, pending.Journey!, load.Result, pending.RequestId, pending.Encrypted);
        else
        {
            Log.Warn(Name, $"journey from object {pending.ExitId}: the target cluster could not be resolved: {load.Exception?.InnerException?.Message ?? "load cancelled"}");
            resp = RefuseJourney(pending.Player, pending.ExitId, BuyJourneyRefusal.NotPossible, "target cluster not resolved");
        }
        if (resp is not null) SendResponse(OperationCodes.BuyJourney, resp, pending.RequestId, pending.Encrypted);
    }
}
