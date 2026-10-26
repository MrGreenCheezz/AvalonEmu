using System.Numerics;
using AlbionLocal.Server.AI;
using AlbionLocal.Server.Data;
using AlbionLocal.Server.Entities;
using AlbionLocal.Server.Systems;

namespace AlbionLocal.Server.Bots;

/// <summary>
/// What a gatherer bot gathers (bots-world SPEC §2.5): one resource kind (harvestables.xml resource: WOOD, ROCK, ORE, FIBER, HIDE) of the
/// node tiers <see cref="MinTier"/>-<see cref="MaxTier"/> (its tier − 2 .. its tier, clipped to the tiers the map has), with the tool of its
/// <see cref="Tier"/>.
/// </summary>
public sealed record GatherProfile(string Resource, int Tier, int MinTier, int MaxTier)
{
    public bool Takes(Harvestable node) => node.Tier >= MinTier && node.Tier <= MaxTier
        && string.Equals(node.Type.Resource, Resource, StringComparison.OrdinalIgnoreCase);
}

/// <summary>
/// Stage I bots (analysis/work/stageI/bots/SPEC.md §6 phase 2, bots-world SPEC §2.5): a gatherer bot, in the open world of every tier, in
/// start towns and (Bots:City:Gatherers) on the T1 trees of royal cities. It picks a node of its <see cref="GatherProfile"/> with charges
/// left, not taken by another bot, without a human within Bots:Gatherer:AvoidPlayerRadius (6 m) or harvesting it and without a live
/// hostile mob's home within Bots:Gatherer:AvoidMobRadius (12 m), within Bots:Gatherer:Radius (150 m) of where it spawned (the nearest
/// ones of the map when none is), the score its distance × U(1, 1.5); walks there along the walk grid (riding a path of at least
/// Bots:MountDistance), the last step a short straight one that crosses no blocked cell to the ground right by the node, and stops facing
/// it; then shows 1 to min(3, charges) harvests (<see cref="BotManager.StartShow"/>: HarvestStart, HarvestFinished with the node's charges
/// to the humans who see it and know the node; its charges taken only with Bots:ConsumeNodes). A human who starts harvesting the node
/// ends the show after the repeat under way. After Bots:Gatherer:Nodes (6-20) nodes or its stay it walks out through the nearest exit
/// (gone there, or at the end of its walk to that exit's gate; the next of its slot arrives at an exit later), or without a way to one logs
/// out where it stands (World:LogoutSeconds, then gone). Attacked, it answers (<see cref="BotCombat"/>) and resumes after the fight: a bot on
/// its way out plans its walk to an exit anew, never gone where the fight left it (with Bots:Travel:Enabled the exit leads next door,
/// <see cref="BotManager.Travellers"/>).
/// <para>
/// Bot-travel (<see cref="BotManager.GoesHome"/>): with Bots:Gatherer:BankAtHome, a full bag (its nodes) or the end of
/// Bots:Gatherer:TripMinutes with something gathered sends it home instead: out through the exit to the next cluster of its way home
/// (travelling there, <see cref="BotManager.ExitOrder"/>), and in its home city to the bank door, a pause of 3-8 s and its resources banked
/// (<see cref="BotManager.Banked"/>); then a new trip (a visitor walks out again). <paramref name="done"/>: the nodes it gathered on
/// this trip before it came to this map.
/// </para>
/// </summary>
public sealed class GathererBrain(BotManager bots, GatherProfile profile, int nodes, int done = 0) : IBotBrain
{
    private enum Phase { Choose, Approach, Harvest, Leave, Logout, Bank, Banking }

    private Phase _phase = Phase.Choose;
    private int _done = done;

    /// <summary>The walkable cell it stepped off the grid from to its node (its way back onto the grid).</summary>
    private Vector2? _back;

    /// <summary>A long path that waits for the mount to be ridden.</summary>
    private (IReadOnlyList<Vector2> Path, float? Facing)? _ride;

    /// <summary>The trigger of the exit it walks out by, and the end of its walk there (the exit's gate).</summary>
    private Vector2 _exit;

    private Vector2 _gate;

    /// <summary>When its logout or its pause at the bank ends (<see cref="Phase.Logout"/>, <see cref="Phase.Banking"/>).</summary>
    private TimeSpan _until;

    /// <summary>Bot-travel: the home city it walks out towards (<see cref="Phase.Leave"/> on its way home); null otherwise.</summary>
    private string? _homing;

    /// <summary>Nodes it found no way to (it picks others).</summary>
    private readonly HashSet<Harvestable> _unreachable = [];

    public GatherProfile Profile => profile;

    /// <summary>The node it walks to or harvests; null between two.</summary>
    public Harvestable? Node { get; private set; }

    /// <summary>Nodes it gathered so far on this trip, and how many it takes before it leaves.</summary>
    public int Done => _done;

    public int Nodes => nodes;

    public string State => _phase switch
    {
        Phase.Approach => $"walk to {Node}",
        Phase.Harvest => $"harvest {Node}",
        Phase.Leave => _homing is { } home ? $"walk home to {home}" : "leave through an exit",
        Phase.Logout => "log out",
        Phase.Bank => "walk to the bank",
        Phase.Banking => "bank",
        _ => $"look for {profile.Resource} T{profile.MinTier}-{profile.MaxTier} ({_done}/{nodes} nodes)",
    };

    public void Attacked(Player bot, Player by)
    {
        // The fight takes over (BotCombat); the walk to the node or the exit, the logout, the mount and the show are given up and chosen
        // anew afterwards (a bot that was leaving still is: its stay or its nodes are over, so Think leaves again).
        (_phase, Node) = (Phase.Choose, null);
        _ride = null;
    }

    public void Think(Player bot, TimeSpan now)
    {
        var info = bot.Bot!;
        if (info.Fight.Active && BotCombat.Think(bots, bot)) return;
        if (bot.Map is not { } map || bots.NavOf(map) is not { } nav || bot.Combat.Down is not null) return;
        if (_phase == Phase.Leave && Vector2.Distance(bot.PositionAt(bots.Time), _exit) <= CityBrain.ExitReach)
        {
            if (!bots.LeaveThrough(bot, _exit)) Leave(bot, nav, now); // stageL roads phase B: a portal no longer usable sends it another way
            return;
        }
        if (bot.Mount.State == MountState.Mounting) return; // its mount bar runs
        if (_ride is { } ride)
        {
            _ride = null;
            var here = bot.PositionAt(bots.Time);
            bots.Walk(bot, [here, .. ride.Path.Skip(1)], ride.Facing);
            return;
        }
        if (info.Motion.Busy) return;
        switch (_phase)
        {
            case Phase.Approach when Node is { } node:
                if (!Free(bot, map, node, approaching: true))
                {
                    (_phase, Node) = (Phase.Choose, null);
                    return;
                }
                _phase = Phase.Harvest;
                bots.StartShow(bot, node, 1 + bots.Random.Next(Math.Min(3, node.Charges)));
                return;
            case Phase.Harvest:
                if (info.Harvest is not null) return; // the show goes on (the world tick finishes each repeat)
                _done++;
                (_phase, Node) = (Phase.Choose, null);
                return;
            case Phase.Leave:
                // At the gate: gone as it walks in; a walk that ended short of it (stopped) is planned anew.
                if (Vector2.Distance(bot.PositionAt(bots.Time), _gate) > CityBrain.ExitReach || !bots.LeaveThrough(bot, _exit)) Leave(bot, nav, now);
                return;
            case Phase.Logout:
                if (now >= _until) bots.Despawn(bot, "logged out", BotRefill.Arrival);
                return;
            case Phase.Bank: // at the bank door: a short pause
                (_phase, _until) = (Phase.Banking, now + TimeSpan.FromSeconds(3 + bots.Random.NextDouble() * 5));
                return;
            case Phase.Banking:
                if (now < _until) return;
                bots.Banked(bot);
                (_phase, _done, _homing) = (Phase.Choose, 0, null);
                if (info.Slot is null) Leave(bot, nav, now); // a visitor (no gatherer slot in its city) sets off again
                return;
        }
        // Bot-travel: a full bag or the trip's time over sends it home to bank (walking home already: on its way).
        if (bots.GoesHome(bot, _done >= nodes, now) is { } home)
        {
            info.Homing = true;
            if (map.Cluster == home) Bank(bot, nav, now);
            else
            {
                _homing = home;
                Leave(bot, nav, now);
            }
            return;
        }
        if (now >= info.StayUntil || _done >= nodes)
        {
            Leave(bot, nav, now);
            return;
        }
        Choose(bot, map, nav);
    }

    /// <summary>
    /// The next node and the walk to it (see the class); up to five of the best are tried for a way, a node without one remembered. With
    /// none it wanders 15-40 m and looks again later.
    /// </summary>
    private void Choose(Player bot, Map map, ClusterNav nav)
    {
        var here = bot.PositionAt(bots.Time);
        foreach (var node in Candidates(bot, map).Take(5))
        {
            if (Approach(nav, here, node) is not { } way)
            {
                _unreachable.Add(node);
                continue;
            }
            Node = node;
            _phase = Phase.Approach;
            Go(bot, way.Path, way.Facing);
            return;
        }
        if (nav.Grid.RandomNear(bots.Random, nav.Grid.Walkable(here) ? here : _back ?? here, 15, 40) is { } spot
            && BotNav.PathFrom(bots, nav, here, spot, _back) is { } path)
            Go(bot, path, null);
    }

    /// <summary>
    /// The nodes <paramref name="bot"/> may take now, best first (see the class): of its profile, with charges, not another bot's, no human
    /// within Bots:Gatherer:AvoidPlayerRadius or harvesting it, no live hostile mob's home within Bots:Gatherer:AvoidMobRadius, not one it
    /// found no way to; within Bots:Gatherer:Radius of its spawn, else the map's nearest; by distance × U(1, 1.5).
    /// </summary>
    public IEnumerable<Harvestable> Candidates(Player bot, Map map)
    {
        var options = bots.Options;
        var utc = DateTime.UtcNow;
        var humans = map.Players.Values.Select(p => (p, At: p.PositionAt(utc))).ToList();
        var taken = map.Bots.Values.Where(b => b != bot).Select(b => (b.Bot!.Brain as GathererBrain)?.Node).OfType<Harvestable>().ToHashSet();
        var mobs = map.Objects.Values.OfType<Mob>().Where(m => !m.Dead && m.Template is { Hostility: NpcHostility.Hostile, AggroRadius: > 0 }).Select(m => m.Home).ToList();
        var here = bot.PositionAt(bots.Time);
        var home = bot.Bot!.Home;
        var open = bots.NodesOf(map).Where(n => profile.Takes(n) && n.Charges > 0 && !taken.Contains(n) && !_unreachable.Contains(n)
            && !humans.Any(h => h.p.Harvest?.Node == n || Vector2.Distance(h.At, n.Position) <= options.AvoidPlayerRadius)
            && !mobs.Any(m => Vector2.Distance(m, n.Position) <= options.AvoidMobRadius)).ToList();
        var near = open.Where(n => Vector2.Distance(n.Position, home) <= options.GathererRadius).ToList();
        if (near.Count == 0) near = [.. open.OrderBy(n => Vector2.DistanceSquared(n.Position, here)).Take(20)];
        return near.Select(n => (n, Score: Vector2.Distance(here, n.Position) * (1 + bots.Random.NextDouble() * 0.5))).OrderBy(x => x.Score).Select(x => x.n);
    }

    /// <summary>
    /// Whether <paramref name="node"/> is still free for <paramref name="bot"/> (charges left, no human harvesting it; arriving, no human
    /// within 3 m of it: it gives way).
    /// </summary>
    private static bool Free(Player bot, Map map, Harvestable node, bool approaching)
    {
        if (node.Map != map || node.Charges <= 0) return false;
        var utc = DateTime.UtcNow;
        return !map.Players.Values.Any(p => p.Harvest?.Node == node || (approaching && Vector2.Distance(p.PositionAt(utc), node.Position) <= 3));
    }

    /// <summary>
    /// The way from <paramref name="here"/> to <paramref name="node"/>: along the walk grid to the walkable cell nearest to it (within 6 m),
    /// then a straight step that crosses no blocked cell to the ground 1.8 m from its centre on that side (or the open cell nearest to it
    /// within 4 m); the stop faces the node. Null when the node cannot be reached or the stand is more than 5 m from its centre.
    /// </summary>
    private (IReadOnlyList<Vector2> Path, float Facing)? Approach(ClusterNav nav, Vector2 here, Harvestable node)
    {
        var grid = nav.Grid;
        var centre = node.Position;
        if (grid.Nearest(centre, 6) is not { } onto) return null;
        var stand = onto;
        var off = onto - centre;
        if (off.Length() > 1.8f)
        {
            var close = centre + Vector2.Normalize(off) * 1.8f;
            if (grid.ClearOpen(onto, close)) stand = close;
            else if (grid.NearestOpen(centre, 4) is { } open && Vector2.Distance(open, centre) < off.Length() && grid.ClearOpen(onto, open)) stand = open;
        }
        if (Vector2.Distance(stand, centre) > 5) return null;
        if (BotNav.PathFrom(bots, nav, here, onto, _back) is not { } path) return null;
        _back = stand == onto ? null : onto;
        IReadOnlyList<Vector2> way = stand == onto ? path : [.. path, stand];
        return (way, MotionMaster.Facing(stand, centre));
    }

    /// <summary>
    /// The stay or the trip is over: to the gate of the nearest exit with a way (gone there), else it logs out where it stands
    /// (World:LogoutSeconds, then gone, as a city bot).
    /// </summary>
    private void Leave(Player bot, ClusterNav nav, TimeSpan now)
    {
        var here = bot.PositionAt(bots.Time);
        // On a road its portals too; bot-travel: the way home or a neighbour of its tier first (the nearest first without travel).
        foreach (var exit in bots.ExitOrder(bot, bots.ExitsOf(bot.Map!, nav), here).Take(3))
            if (BotNav.PathFrom(bots, nav, here, exit.Gate, _back) is { } path)
            {
                (_phase, Node, _exit, _gate) = (Phase.Leave, null, exit.Position, path[^1]);
                Go(bot, path, Vector2.Distance(exit.Gate, exit.Position) < 0.5f ? null : MotionMaster.Facing(exit.Gate, exit.Position));
                return;
            }
        (_phase, Node, _until) = (Phase.Logout, null, now + bot.World.LogoutTimeIn(bot.Map));
    }

    /// <summary>
    /// Bot-travel: home, it walks to the nearest bank door of its city (a POI of <see cref="BotPoi.Building"/> "bank") and faces it; without
    /// one (or a way to it) it pauses where it stands. Its resources are banked after the pause (<see cref="Phase.Banking"/>).
    /// </summary>
    private void Bank(Player bot, ClusterNav nav, TimeSpan now)
    {
        var here = bot.PositionAt(bots.Time);
        _homing = null;
        foreach (var bank in nav.Pois.Where(p => p.Building == "bank").OrderBy(p => Vector2.DistanceSquared(p.At, here)))
            if (BotNav.PathFrom(bots, nav, here, bank.At, _back) is { } path)
            {
                (_phase, Node) = (Phase.Bank, null);
                Go(bot, path, bank.Facing);
                return;
            }
        (_phase, Node, _until) = (Phase.Banking, null, now + TimeSpan.FromSeconds(3 + bots.Random.NextDouble() * 5));
    }

    /// <summary>
    /// Walks <paramref name="path"/>; one of at least Bots:MountDistance m is ridden when the bot carries a mount it may mount now (the
    /// mount bar first: MountStart, then Mounted to its observers), a shorter one walked.
    /// </summary>
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
}
