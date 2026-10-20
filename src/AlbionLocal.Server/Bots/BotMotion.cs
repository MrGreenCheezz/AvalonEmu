using System.Numerics;
using AlbionLocal.Server.AI;
using AlbionLocal.Server.Entities;

namespace AlbionLocal.Server.Bots;

/// <summary>
/// Stage I bots (analysis/work/stageI/bots/SPEC.md §1.6, bots-world SPEC §2.2): a bot walking a path, leg by leg, with
/// <see cref="Player.Move"/> as a client's Moves would: each leg one sample from where the bot is to the next waypoint at
/// <see cref="Player.DefaultSpeed"/> × its movespeed factor, re-sent from the extrapolated position every Bots:MoveResendSeconds (0.5)
/// while a human sees it (the client dead-reckons a sample at most 1 s), the next leg at once when the bot is within 0.05 m of the
/// leg's end (the world tick checks), a pause of 0.3-1.2 s at a corner with Bots:Move:PauseChance, and a stop sample facing the goal
/// at the end. Never a Teleport: a new path starts where the bot is. While it cannot move (knocked down, stunned, rooted) it stands
/// and the leg waits; on a spell's forced path (<see cref="Systems.SpellMovement"/>) and 300 ms after it (<see cref="Systems.SpellMovement.Holds"/>) it sends nothing and goes on from the path's end. Every stamp is the world's game time (<see cref="World.GameNow"/>), so the self-test's manual clock moves bots.
/// </summary>
public sealed class BotMotion
{
    private List<Vector2>? _points;
    private int _next;
    private float? _facing;
    private long _sent;
    private long? _pauseUntil;
    private bool _held;

    /// <summary>Whether a path is under way (walking, or paused at a corner).</summary>
    public bool Busy => _points is not null;

    /// <summary>The waypoints still ahead (the leg under way first); empty when standing.</summary>
    public IEnumerable<Vector2> Ahead => _points is null ? [] : _points.Skip(_next);

    /// <summary>The goal of the path under way; null when standing.</summary>
    public Vector2? Goal => _points?[^1];

    /// <summary>
    /// Walks <paramref name="path"/> (its first point where the bot is, as <see cref="Data.RoadMap.Path"/> gives it), stopping facing
    /// <paramref name="facing"/> (null: the last leg's direction). A path of one point is a stop there.
    /// </summary>
    public void Walk(Player bot, IReadOnlyList<Vector2> path, float? facing, BotManager bots)
    {
        if (path.Count < 2)
        {
            Stop(bot, facing, bots);
            return;
        }
        _points = [.. path];
        _next = 1;
        _facing = facing;
        _pauseUntil = null;
        _held = false;
        Leg(bot, _points[0], bots);
    }

    /// <summary>Drops the path under way without a sample (stageN companions: a teleport or a move to another map sends its own).</summary>
    public void Reset()
    {
        _points = null;
        _pauseUntil = null;
        _held = false;
    }

    /// <summary>Ends the walk where the bot is now: a stop sample facing <paramref name="facing"/> (null: as it faces).</summary>
    public void Stop(Player bot, float? facing, BotManager bots)
    {
        var now = bots.Time;
        var here = bot.PositionAt(now);
        _points = null;
        _pauseUntil = null;
        bot.Move(here, here, 0, facing ?? bot.Facing, 0, now);
    }

    /// <summary>One world tick: the leg's arrival, a pause's end, a resend while seen, a hold while it cannot move.</summary>
    public void Step(Player bot, BotManager bots)
    {
        if (_points is null) return;
        var now = bots.Time;
        var ticks = bot.World.GameNow;
        // On a forced path (a knockback, a pull, a fear, its own dash; stageJ pvp-spells phase 2) and for the echo grace after it it sends
        // nothing, a pause at a corner included: the leg starts again where the path ended.
        if (Systems.SpellMovement.Holds(bot, ticks))
        {
            _pauseUntil = null;
            _held = true;
            return;
        }
        if (_pauseUntil is { } until)
        {
            if (ticks < until) return;
            _pauseUntil = null;
            Leg(bot, bot.PositionAt(now), bots);
            return;
        }
        var here = bot.PositionAt(now);
        if (Speed(bot, ticks) <= 0)
        {
            if (!_held) bot.Move(here, here, 0, bot.Facing, 0, now); // it stands where the hold caught it
            _held = true;
            return;
        }
        if (_held)
        {
            _held = false;
            Leg(bot, here, bots);
            return;
        }
        var target = _points[_next];
        if (Vector2.Distance(here, target) <= ArrivalDistance)
        {
            _next++;
            if (_next >= _points.Count)
            {
                var facing = _facing ?? bot.Facing;
                _points = null;
                bot.Move(target, target, 0, facing, 0, now);
                return;
            }
            if (bots.Random.NextDouble() < bots.Options.PauseChance)
            {
                bot.Move(target, target, 0, bot.Facing, 0, now);
                _pauseUntil = ticks + TimeSpan.FromSeconds(0.3 + bots.Random.NextDouble() * 0.9).Ticks;
                return;
            }
            Leg(bot, target, bots);
            return;
        }
        if (bot.Observers.Count > 0 && ticks - _sent >= TimeSpan.FromSeconds(bots.Options.MoveResendSeconds).Ticks) Leg(bot, here, bots);
    }

    /// <summary>
    /// A new observer is about to get the bot's spawn block (VisibilityTracker): while it walks a leg, the leg's sample is issued again
    /// from where the bot is now with a fresh stamp, so that NewCharacter keys 14/18 are not the start of a leg it began unseen seconds
    /// ago (the client dead-reckons a sample at most 1 s, and snaps a jump of more than 6 m). Not relayed: the observer is already in
    /// <see cref="Player.Observers"/> before its NewCharacter, and the others get the sample with the next resend.
    /// </summary>
    public void Refresh(Player bot, BotManager bots)
    {
        if (_points is null || _pauseUntil is not null || _held) return;
        var ticks = bot.World.GameNow;
        var speed = Speed(bot, ticks);
        if (speed <= 0) return;
        var now = bots.Time;
        var from = bot.PositionAt(now);
        var target = _points[_next];
        var facing = MotionMaster.Facing(from, target);
        bot.Movement = new MovementState(from, target, speed, now) { Direction = facing, Stamp = bot.NextMoveStamp() };
        bot.Facing = facing;
    }

    /// <summary>A leg ends when the bot is this near its waypoint (m).</summary>
    public const float ArrivalDistance = 0.05f;

    /// <summary>The walk speed now: 5.5 m/s × the movespeed factor (0 while knocked down, stunned or rooted).</summary>
    private static float Speed(Player bot, long ticks) =>
        bot.Combat.Down is not null ? 0 : Player.DefaultSpeed * Systems.SpellEffects.SpeedFactor(bot, ticks);

    /// <summary>The sample of the leg to the next waypoint from <paramref name="from"/>.</summary>
    private void Leg(Player bot, Vector2 from, BotManager bots)
    {
        var target = _points![_next];
        var ticks = bot.World.GameNow;
        bot.Move(from, target, Speed(bot, ticks), MotionMaster.Facing(from, target), 0, bots.Time);
        _sent = ticks;
    }
}
