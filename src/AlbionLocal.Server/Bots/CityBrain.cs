using System.Numerics;
using AlbionLocal.Server.Data;
using AlbionLocal.Server.Entities;

namespace AlbionLocal.Server.Bots;

/// <summary>
/// Stage I: what a bot decides (analysis/work/stageI/bots/SPEC.md §1.1): <see cref="Think"/> on its schedule (Bots:ThinkSeconds) while a
/// human is near, <see cref="Attacked"/> when a player's hit lands on it (phase 2: gatherers and patrols answer).
/// </summary>
public interface IBotBrain
{
    void Think(Player bot, TimeSpan now);

    void Attacked(Player bot, Player by);

    /// <summary>What it is doing, for .bot info ("dwell at door", "walk to station", …).</summary>
    string State { get; }
}

/// <summary>
/// Stage I bots (bots-world SPEC §2.4): a city bot, Blue and never in a fight. It dwells at a point of interest of its cluster (a bank or
/// market door, a crafting station, a travel point, …; <see cref="BotPoi"/>) for the POI's dwell time, facing it, now and then with an
/// emote (Bots:City:EmoteChance, the weights of content/bots.json; a looping one, sitting or dancing, is shown to whoever comes into view
/// later and stopped before it walks on) or a short shuffle of 1-3 m, then walks on: 60 % to another POI by weight (not one Bots:City:PerPoi
/// bots dwell at already, nor one it found no path to), 40 % to a spot 15-40 m away, always along <see cref="RoadMap.Path"/>. When its stay
/// is over it walks to an exit and is gone there, or logs out where it stands (World:LogoutSeconds, then gone): a player leaving.
/// </summary>
public sealed class CityBrain(BotManager bots) : IBotBrain
{
    private enum Phase { Dwell, Walk, Leave, Logout }

    private Phase _phase = Phase.Dwell;
    private TimeSpan _until;

    /// <summary>When the dwell's shuffle comes (30 % of the dwells, somewhere in their middle); null = none.</summary>
    private TimeSpan? _shuffleAt;

    /// <summary>The trigger of the exit it leaves through (<see cref="Phase.Leave"/>).</summary>
    private Vector2 _exit;

    /// <summary>How near a leaving bot comes to the exit's trigger before it is gone (m, SPEC §1.4 turnover).</summary>
    public const float ExitReach = 2;

    /// <summary>The POI it dwells at or walks to; null when wandering.</summary>
    public BotPoi? Poi { get; private set; }

    public string State => _phase switch
    {
        Phase.Dwell => Poi is { } p ? $"dwell at {p.Kind}" : "dwell",
        Phase.Walk => Poi is { } p ? $"walk to {p.Kind}" : "wander",
        Phase.Leave => "leave through an exit",
        _ => "log out",
    };

    /// <summary>A newly spawned bot dwells where it stands: at <paramref name="poi"/> (null: a spot or an exit it came through) for its dwell time.</summary>
    public void Begin(BotPoi? poi, TimeSpan now)
    {
        Poi = poi;
        Dwell(now, poi?.Dwell ?? (2, 6));
    }

    /// <summary>A dwell of <paramref name="range"/> seconds from <paramref name="now"/>, with a shuffle in its middle 30 % of the time.</summary>
    private void Dwell(TimeSpan now, (float Min, float Max) range)
    {
        _phase = Phase.Dwell;
        _until = now + Seconds(range);
        _shuffleAt = bots.Random.NextDouble() < 0.3 ? now + (_until - now) * (0.3 + bots.Random.NextDouble() * 0.4) : null;
    }

    public void Attacked(Player bot, Player by) { } // never fought (BotPvp: Friendly in every zone)

    public void Think(Player bot, TimeSpan now)
    {
        var info = bot.Bot!;
        if (_phase == Phase.Leave && Vector2.Distance(bot.PositionAt(bots.Time), _exit) <= ExitReach)
        {
            bots.Despawn(bot, "left through an exit", BotRefill.Arrival); // at the trigger before the gate: gone as it walks in
            return;
        }
        if (info.Motion.Busy || bot.Map is not { } map || bots.NavOf(map) is not { } nav) return;
        switch (_phase)
        {
            case Phase.Walk:
                Dwell(now, Poi?.Dwell ?? (3, 10));
                if (Poi is not null && bots.Random.NextDouble() < bots.Options.CityEmoteChance) bots.Emote(bot, PickEmote());
                return;
            case Phase.Leave:
                bots.Despawn(bot, "left through an exit", BotRefill.Arrival);
                return;
            case Phase.Logout:
                if (now >= _until) bots.Despawn(bot, "logged out", BotRefill.Arrival);
                return;
        }
        if (now < _until)
        {
            // The dwell's shuffle: 1-3 m on the spot, then on with the dwell (not while it sits or dances).
            if (_shuffleAt is { } at && now >= at && info.Emote is null)
            {
                _shuffleAt = null;
                var here = bot.PositionAt(bots.Time);
                if (nav.Grid.RandomNear(bots.Random, here, 1, 3) is { } spot) bots.Walk(bot, [here, spot], Poi?.Facing);
            }
            return;
        }
        if (now >= info.StayUntil)
        {
            Leave(bot, nav, now);
            return;
        }
        Next(bot, map, nav);
    }

    /// <summary>
    /// The next goal: a POI by weight (60 %) or a spot 15-40 m away. A POI without a path is remembered as unreachable only when the path
    /// could start where the bot stands (<see cref="RoadMap.CanAttach"/>): a bot off the lattice blames no POI. A bot off the walk grid
    /// (a GM's bot by a wall) first steps onto the nearest walkable cell within 6 m, or stands when there is none.
    /// </summary>
    private void Next(Player bot, Map map, ClusterNav nav)
    {
        var here = bot.PositionAt(bots.Time);
        if (!nav.Grid.Walkable(here))
        {
            if (nav.Grid.Nearest(here, 6) is { } back)
            {
                (Poi, _phase) = (null, Phase.Walk);
                bots.Walk(bot, [here, back], null);
            }
            return; // nothing walkable near: it stands (a GM's bot; .bot clear takes it), blaming no POI
        }
        BotPoi? poi = null;
        Vector2? goal = null;
        var (near, far) = bots.Options.CityWanderMeters;
        if (bots.Random.NextDouble() < bots.Options.CityPoiShare && bots.PickPoi(map, nav, except: Poi) is { } p) (poi, goal) = (p, p.At); // Bots:City:PoiShare
        else goal = nav.Grid.RandomNear(bots.Random, here, (float)near, (float)far) ?? nav.Roads.RandomNode(bots.Random); // Bots:City:WanderMeters
        if (goal is not { } g) return;
        if (bots.FindPath(nav, here, g) is not { } path)
        {
            if (poi is not null && nav.Roads.CanAttach(here)) bots.Unreachable(map, poi);
            return;
        }
        Poi = poi;
        _phase = Phase.Walk;
        bots.Walk(bot, path, poi?.Facing);
    }

    /// <summary>
    /// The stay is over: with Bots:City:ExitShare (0.5) to the gate of the nearest exit with a path (<see cref="BotExit.Gate"/>, facing the trigger; gone
    /// there or within 2 m of the trigger, SPEC §1.4), else (or without one) a logout where it stands.
    /// </summary>
    private void Leave(Player bot, ClusterNav nav, TimeSpan now)
    {
        var here = bot.PositionAt(bots.Time);
        if (bots.Random.NextDouble() < bots.Options.CityExitShare)
            foreach (var exit in nav.Exits.OrderBy(e => Vector2.DistanceSquared(e.Gate, here)))
                if (bots.FindPath(nav, here, exit.Gate) is { } path)
                {
                    Poi = null;
                    _phase = Phase.Leave;
                    _exit = exit.Position;
                    bots.Walk(bot, path, Vector2.Distance(exit.Gate, exit.Position) < 0.5f ? null : AI.MotionMaster.Facing(exit.Gate, exit.Position));
                    return;
                }
        bots.StopEmote(bot);
        _phase = Phase.Logout;
        _until = now + bot.World.LogoutTime;
    }

    private int PickEmote()
    {
        var emotes = bots.Settings.Emotes.Where(e => e.Value > 0).ToList();
        if (emotes.Count == 0) return 14;
        var roll = bots.Random.Next(emotes.Sum(e => e.Value));
        foreach (var (emote, weight) in emotes)
            if ((roll -= weight) < 0) return emote;
        return emotes[^1].Key;
    }

    private TimeSpan Seconds((float Min, float Max) range) => TimeSpan.FromSeconds(range.Min + bots.Random.NextDouble() * (range.Max - range.Min));
}
