using System.Numerics;
using AlbionLocal.Photon;
using AlbionLocal.Server.AI;
using AlbionLocal.Server.Data;
using AlbionLocal.Server.Entities;
using AlbionLocal.Server.Systems;

namespace AlbionLocal.Server.Bots;

/// <summary>
/// A patrol's beat (bots-world SPEC §2.6): its stops on walkable ground, walked one after the other along the walk grid. A road (one of
/// <see cref="RoadMap.Routes"/>, a stop every <see cref="PatrolRoute.Spacing"/> m along it) is walked end to end and back
/// (<paramref name="Loop"/> false); without roads a loop through 3-5 points (exit arrivals, mob patrol waypoints, camp centres). StageL roads
/// phase B: on a road with two portals or more in reach, a loop through their gates (<see cref="Portals"/>), where the gank group waits.
/// </summary>
public sealed record PatrolRoute(IReadOnlyList<Vector2> Stops, bool Loop)
{
    /// <summary>The stops at a road's portal (Bots:Roads:PortalWaitSeconds there).</summary>
    public IReadOnlySet<int> Portals { get; init; } = new HashSet<int>();

    /// <summary>The distance between two stops along a road (m).</summary>
    public const float Spacing = 40;

    /// <summary>A road shorter than this is no beat (m).</summary>
    public const float MinRoad = 20;

    /// <summary>The stop nearest to <paramref name="p"/>.</summary>
    public int Nearest(Vector2 p) => Enumerable.Range(0, Stops.Count).MinBy(i => Vector2.DistanceSquared(Stops[i], p));

    /// <summary>Whether stop <paramref name="i"/> is an end of a road (a longer dwell there).</summary>
    public bool IsEnd(int i) => !Loop && (i == 0 || i == Stops.Count - 1);

    /// <summary>
    /// A route of <paramref name="nav"/> for a patrol (see the record): with two <paramref name="portals"/> or more (a road's portal gates,
    /// BotManager.PortalStops) a loop through them from the one nearest to <paramref name="near"/> (else a random one), each next the nearest
    /// left; else near <paramref name="near"/> (a GM's patrol: the road with a point nearest to it within 150 m, else a loop from the point
    /// nearest to it), else a road at random, else a loop from a random point; null when the cluster offers neither.
    /// </summary>
    public static PatrolRoute? Pick(ClusterNav nav, ClusterTemplate template, Random random, Vector2? near = null, IReadOnlyList<Vector2>? portals = null)
    {
        if (portals is { Count: >= 2 })
        {
            var left = portals.ToList();
            var at = near is { } from ? left.MinBy(p => Vector2.DistanceSquared(p, from)) : left[random.Next(left.Count)];
            var gates = new List<Vector2>();
            while (left.Remove(at))
            {
                gates.Add(at);
                if (left.Count == 0) break;
                at = left.MinBy(p => Vector2.DistanceSquared(p, at));
            }
            return new PatrolRoute(gates, Loop: true) { Portals = Enumerable.Range(0, gates.Count).ToHashSet() };
        }
        var roads = nav.Roads.Routes.Where(r => r.Zip(r.Skip(1), Vector2.Distance).Sum() >= MinRoad).ToList();
        IReadOnlyList<Vector2>? road = null;
        if (near is { } n)
            road = roads.Select(r => (r, d: r.Min(p => Vector2.Distance(p, n)))).Where(x => x.d <= 150).OrderBy(x => x.d).Select(x => x.r).FirstOrDefault();
        else if (roads.Count > 0) road = roads[random.Next(roads.Count)];
        if (road is not null && Along(nav, road) is { Count: >= 2 } stops) return new PatrolRoute(stops, Loop: false);
        return LoopOf(nav, template, random, near);
    }

    /// <summary>The stops of a road: its first point, one every <see cref="Spacing"/> m along it, its last; each on walkable ground.</summary>
    private static List<Vector2> Along(ClusterNav nav, IReadOnlyList<Vector2> road)
    {
        var stops = new List<Vector2> { road[0] };
        var walked = 0f;
        for (var i = 1; i < road.Count; i++)
        {
            walked += Vector2.Distance(road[i - 1], road[i]);
            if (walked < Spacing && i < road.Count - 1) continue;
            stops.Add(road[i]);
            walked = 0;
        }
        return [.. stops.Select(p => nav.Grid.Walkable(p) ? p : nav.Grid.Nearest(p, 6)).OfType<Vector2>()];
    }

    /// <summary>
    /// A loop of 3-5 points: the start (nearest to <paramref name="near"/>, else at random) among the exits' arrivals, the mobs' patrol
    /// waypoints and the camp centres (each moved onto walkable ground within 6 m), then its nearest ones at least 10 m apart; spots 30-80 m
    /// around the start fill a loop that has too few.
    /// </summary>
    private static PatrolRoute? LoopOf(ClusterNav nav, ClusterTemplate template, Random random, Vector2? near)
    {
        var grid = nav.Grid;
        var points = nav.Exits.Select(e => e.Arrival)
            .Concat(template.MobSpawnPoints.Where(m => string.Equals(m.Behavior, "PATROL", StringComparison.OrdinalIgnoreCase)).SelectMany(m => m.Waypoints.Select(w => w.Position)))
            .Concat(template.LocalQuestAreas.Select(q => q.Position))
            .Select(p => grid.Walkable(p) ? p : grid.Nearest(p, 6)).OfType<Vector2>().ToList();
        Vector2? first = points.Count == 0 ? null : near is { } n ? points.MinBy(p => Vector2.DistanceSquared(p, n)) : points[random.Next(points.Count)];
        first ??= nav.Roads.RandomNode(random);
        if (first is not { } start) return null;
        var stops = new List<Vector2> { start };
        var want = 3 + random.Next(3);
        foreach (var p in points.OrderBy(p => Vector2.DistanceSquared(p, start)))
        {
            if (stops.Count >= want) break;
            if (stops.All(s => Vector2.Distance(s, p) >= 10) && Vector2.Distance(p, start) <= 250) stops.Add(p);
        }
        for (var i = 0; i < 12 && stops.Count < 3; i++)
            if (grid.RandomNear(random, start, 30, 80) is { } spot && stops.All(s => Vector2.Distance(s, spot) >= 10)) stops.Add(spot);
        return stops.Count >= 2 ? new PatrolRoute(stops, Loop: true) : null;
    }
}

/// <summary>
/// A group of 1-3 patrol bots (Bots:Patrol:GroupSize; SPEC §1.4, bots-world SPEC §2.6): one route, its first live member the leader
/// (the next one when it falls), where the leader is on the route, and one fight: a hit on a member engages them all
/// (<see cref="Engaged"/>, kept by the group: a member's defeat does not end the others' answer), and the target one of them takes on sight is the others' too (<see cref="BotCombat.Engage"/>). Its
/// members leave together (one stay). Touched only under <see cref="World.Sync"/>.
/// </summary>
public sealed class BotGroup(PatrolRoute route)
{
    public PatrolRoute Route => route;

    /// <summary>Its live members, in their slot order.</summary>
    public List<Player> Members { get; } = [];

    /// <summary>
    /// Bot support roles: the parts its members take as they spawn (Bots:Patrol:Makeups, drawn for a new population group); empty = every
    /// member a damage dealer.
    /// </summary>
    public IReadOnlyList<PatrolPart> Makeup { get; set; } = [];

    /// <summary>
    /// The part of its next member: the first one of <see cref="Makeup"/> its live members do not hold yet (a fallen healer's place goes to
    /// its replacement), else a damage dealer.
    /// </summary>
    public PatrolPart NextPart()
    {
        var held = Members.Where(m => m.Map is not null && m.Bot is { Fight.Defeated: false }).Select(m => m.Bot!.Part).ToList();
        foreach (var part in Makeup)
            if (!held.Remove(part)) return part;
        return PatrolPart.Dps;
    }

    /// <summary>
    /// The road portals one of it passed through (stageL roads phase B): link → the GameTimeStamp its recently-used window ends, inside which
    /// the other members follow free (BotManager.PassAs).
    /// </summary>
    public Dictionary<RoadLink, long> Through { get; } = [];

    /// <summary>Attacker object id → GameTimeStamp of its last hit on any member (<see cref="BotPvp.Engaged"/>).</summary>
    public Dictionary<long, long> Engaged { get; } = [];

    /// <summary>The stop the leader walks to or dwells at, and its way along a road (+1, −1).</summary>
    public int Stop { get; set; }

    public int Step { get; set; } = 1;

    /// <summary>The stop the leader walks to next instead of the following one (back on the route after a fight, a new leader); null = on.</summary>
    public int? Rejoin { get; set; }

    /// <summary>The leader walks to <see cref="Stop"/> (true) or dwells there until <see cref="DwellUntil"/>.</summary>
    public bool Walking { get; set; }

    public TimeSpan DwellUntil { get; set; }

    /// <summary><see cref="World.Clock"/> time its population members leave (one stay for the group, Bots:StayMinutes; never for a GM's group).</summary>
    public TimeSpan StayUntil { get; set; } = TimeSpan.MaxValue;

    /// <summary>The leader of the last decision (a change sends the new one to its nearest stop).</summary>
    public Player? LastLeader { get; set; }

    /// <summary>The first member that is on the map and not defeated; null when none is left.</summary>
    public Player? Leader => Members.FirstOrDefault(m => m.Map is not null && m.Bot is { Fight.Defeated: false });

    /// <summary>The place of <paramref name="bot"/> behind the leader (1, 2, …; 0 = the leader).</summary>
    public int SlotOf(Player bot) => Members.Where(m => m.Map is not null && m.Bot is { Fight.Defeated: false }).ToList().IndexOf(bot);

    /// <summary>The next stop: <see cref="Rejoin"/>, else on along the loop, or along the road (turning at its ends).</summary>
    public void Advance()
    {
        var n = route.Stops.Count;
        if (Rejoin is { } back)
        {
            (Stop, Rejoin) = (Math.Clamp(back, 0, n - 1), null);
            return;
        }
        if (route.Loop)
        {
            Stop = (Stop + 1) % n;
            return;
        }
        if (Stop + Step < 0 || Stop + Step >= n) Step = -Step;
        Stop = Math.Clamp(Stop + Step, 0, n - 1);
    }
}

/// <summary>
/// Stage I bots (analysis/work/stageI/bots/SPEC.md §6 phase 3, bots-world SPEC §2.6): a patrol bot, Red, only in clusters whose rule lets
/// a Red player be fought (PvpAllowed, PvpForced, None). The group's leader walks its <see cref="PatrolRoute"/> stop by stop (riding a path of
/// at least Bots:MountDistance), dwelling Bots:Patrol:RouteDwellSeconds at a stop and Bots:Patrol:RouteEndDwellSeconds at a road's end;
/// the others follow 2.5 m × their place behind it along its facing, re-aimed every second (mounted when it rides). On sight it attacks
/// (<see cref="BotCombat.Scan"/>: the nearest player in its view within Bots:Patrol:AggroRadius whom the server's PvP rules let it fight,
/// <see cref="BotPvp.Of"/>: under the pve preset only a Red one, unless he engaged the group first), with a way to him, not one it spared,
/// not a duelist, not one who arrived on the map within Bots:Patrol:JoinGraceSeconds; the whole group takes him on
/// (<see cref="BotCombat.Think"/> fights). After the fight (the leash, or no one engaged) the leader walks back to the nearest stop of its
/// route. When the stay is over the group walks out through the nearest exit (or logs out where it stands). Touched only under
/// <see cref="World.Sync"/>.
/// </summary>
public sealed class PatrolBrain(BotManager bots, BotGroup group) : IBotBrain
{
    /// <summary>How far behind the leader a follower walks per place (m), and how near to that point is near enough.</summary>
    public const float FollowSpacing = 2.5f, FollowReach = 1.5f;

    private enum Phase { Patrol, Leave, Logout }

    private Phase _phase = Phase.Patrol;

    /// <summary>A long path that waits for the mount to be ridden.</summary>
    private (IReadOnlyList<Vector2> Path, float? Facing)? _ride;

    /// <summary>A follower's last aim behind the leader.</summary>
    private Vector2? _aimed;

    private Vector2 _exit, _gate;
    private TimeSpan _until;

    public BotGroup Group => group;

    public string State => _phase switch
    {
        Phase.Leave => "leave through an exit",
        Phase.Logout => "log out",
        _ when group.Leader is { } l && l.Bot?.Brain != this => $"follow '{l.Character.Name}'",
        _ => group.Walking ? $"patrol to stop {group.Stop + 1}/{group.Route.Stops.Count}" : $"dwell at stop {group.Stop + 1}/{group.Route.Stops.Count}",
    };

    public void Attacked(Player bot, Player by)
    {
        // The fight takes over (BotCombat); the walk and the mount under way are given up, the route taken up again afterwards.
        _ride = null;
        _aimed = null;
    }

    public void Think(Player bot, TimeSpan now)
    {
        var info = bot.Bot!;
        var fought = info.Fight.Active;
        if (fought && BotCombat.Think(bots, bot)) return;
        if (bot.Map is not { } map || bots.NavOf(map) is not { } nav || bot.Combat.Down is not null) return;
        var here = bot.PositionAt(bots.Time);
        if (fought && group.Leader == bot)
        {
            // Back on the route: the leader walks to its nearest stop first.
            (group.Rejoin, group.Walking, group.DwellUntil) = (group.Route.Nearest(here), false, now);
        }
        switch (_phase)
        {
            case Phase.Leave:
                // stageL roads phase B: a portal that may not be used any more on arrival sends it another way (LeaveThrough false)
                if (Vector2.Distance(here, _exit) <= CityBrain.ExitReach || (!info.Motion.Busy && Vector2.Distance(here, _gate) <= CityBrain.ExitReach))
                {
                    if (!bots.LeaveThrough(bot, _exit, group)) Leave(bot, nav, now);
                }
                else if (!info.Motion.Busy) Leave(bot, nav, now);
                return;
            case Phase.Logout:
                if (now >= _until) bots.Despawn(bot, "logged out", BotRefill.Arrival);
                return;
        }
        if (BotCombat.Support(bots, bot)) return; // bot support roles: a healer heals a member left hurt
        if (BotCombat.Scan(bots, bot, nav) is { } target)
        {
            BotCombat.Engage(bots, bot, target);
            return;
        }
        if (BotCombat.GroupTarget(bots, bot)) return; // a member fights: it joins in
        if (bot.Mount.State == MountState.Mounting) return; // its mount bar runs
        if (_ride is { } ride)
        {
            _ride = null;
            bots.Walk(bot, [here, .. ride.Path.Skip(1)], ride.Facing);
            return;
        }
        if (now >= info.StayUntil)
        {
            Leave(bot, nav, now);
            return;
        }
        var leader = group.Leader;
        if (leader is not null && leader != bot)
        {
            Follow(bot, leader, nav, here, now);
            return;
        }
        if (group.LastLeader != bot)
        {
            // A new leader (the last one fell or left): from its nearest stop on.
            (group.LastLeader, group.Rejoin, group.Walking, group.DwellUntil) = (bot, group.Route.Nearest(here), false, now);
        }
        Lead(bot, nav, here, now);
    }

    /// <summary>
    /// The leader on its route: at a stop it dwells (longer at a road's end), then walks to the next stop; a stop without a way to it is
    /// passed over.
    /// </summary>
    private void Lead(Player bot, ClusterNav nav, Vector2 here, TimeSpan now)
    {
        var route = group.Route;
        if (bot.Bot!.Motion.Busy) return;
        if (group.Walking)
        {
            group.Walking = false;
            // stageL roads phase B: at a portal the gank group waits for Red players coming through (Bots:Roads:PortalWaitSeconds)
            group.DwellUntil = now + Seconds(route.Portals.Contains(group.Stop) ? bots.Options.RoadsPortalWaitSeconds
                : route.IsEnd(group.Stop) ? bots.Options.RouteEndDwellSeconds : bots.Options.RouteDwellSeconds);
            return;
        }
        if (now < group.DwellUntil) return;
        group.Advance();
        var stop = route.Stops[group.Stop];
        if (Vector2.Distance(here, stop) < 1)
        {
            group.Walking = true; // there already: the next think dwells
            return;
        }
        if (BotNav.PathFrom(bots, nav, here, stop) is not { } path)
        {
            Log.Debug("bots", $"'{bot.Character.Name}' ({bot.Bot.PersonaId}): no way to stop {group.Stop + 1} {stop}, the next one");
            group.DwellUntil = now + TimeSpan.FromSeconds(1);
            return;
        }
        group.Walking = true;
        Go(bot, path, null);
    }

    /// <summary>
    /// A follower: to Bots:Patrol:FollowSpacing (<see cref="FollowSpacing"/>) m × its place behind the leader along its facing (on walkable ground near it), straight when
    /// the line is clear, else along a path; on the leader's mount when it rides, off it when the leader stands unmounted. Every second.
    /// </summary>
    private void Follow(Player bot, Player leader, ClusterNav nav, Vector2 here, TimeSpan now)
    {
        var info = bot.Bot!;
        info.NextThink = now + TimeSpan.FromSeconds(1);
        var lead = leader.PositionAt(bots.Time);
        var moving = leader.Bot!.Motion.Busy;
        if (leader.IsMounted && !bot.IsMounted && Mounts.ItemOf(bot) is { } mount && Mounts.Check(bot, mount.Id) is null)
        {
            if (info.Motion.Busy) info.Motion.Stop(bot, null, bots);
            Mounts.Start(bot, remount: false);
            return;
        }
        if (!leader.IsMounted && bot.IsMounted && !moving) Mounts.Dismount(bot);
        var angle = leader.Facing * Math.PI / 180;
        var behind = lead - new Vector2((float)Math.Sin(angle), (float)Math.Cos(angle)) * (float)bots.Options.FollowSpacing * Math.Max(1, group.SlotOf(bot));
        var goal = nav.Grid.Walkable(behind) ? behind : nav.Grid.Nearest(behind, 4) ?? lead;
        if (Vector2.Distance(here, goal) <= FollowReach)
        {
            if (info.Motion.Busy && !moving) info.Motion.Stop(bot, leader.Facing, bots);
            return;
        }
        if (info.Motion.Busy && _aimed is { } aimed && Vector2.Distance(aimed, goal) < 1) return;
        var path = nav.Grid.Clear(here, goal) ? [here, goal] : BotNav.PathFrom(bots, nav, here, goal);
        if (path is null) return;
        _aimed = goal;
        bots.Walk(bot, path, leader.Facing);
    }

    /// <summary>The stay is over: to the gate of the nearest exit with a way (on a road a portal too; gone there), else a logout where it stands.</summary>
    private void Leave(Player bot, ClusterNav nav, TimeSpan now)
    {
        var here = bot.PositionAt(bots.Time);
        foreach (var exit in bots.ExitsOf(bot.Map!, nav, group).OrderBy(e => Vector2.DistanceSquared(e.Gate, here)).Take(3))
            if (BotNav.PathFrom(bots, nav, here, exit.Gate) is { } path)
            {
                (_phase, _exit, _gate) = (Phase.Leave, exit.Position, path[^1]);
                Go(bot, path, Vector2.Distance(exit.Gate, exit.Position) < 0.5f ? null : MotionMaster.Facing(exit.Gate, exit.Position));
                return;
            }
        (_phase, _until) = (Phase.Logout, now + bot.World.LogoutTimeIn(bot.Map));
    }

    /// <summary>Walks <paramref name="path"/>; one of at least Bots:MountDistance m is ridden when the bot may mount now (the mount bar first).</summary>
    private void Go(Player bot, IReadOnlyList<Vector2> path, float? facing)
    {
        var length = path.Zip(path.Skip(1), Vector2.Distance).Sum();
        if (length >= bots.Options.MountDistance && !bot.IsMounted && Mounts.ItemOf(bot) is { } mount && Mounts.Check(bot, mount.Id) is null)
        {
            Mounts.Start(bot, remount: false);
            _ride = (path, facing);
            return;
        }
        bots.Walk(bot, path, facing);
    }

    private TimeSpan Seconds((double Min, double Max) range) => TimeSpan.FromSeconds(range.Min + bots.Random.NextDouble() * (range.Max - range.Min));
}
