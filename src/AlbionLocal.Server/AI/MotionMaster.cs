using System.Numerics;
using AlbionLocal.Server.Data;
using AlbionLocal.Server.Entities;

namespace AlbionLocal.Server.AI;

/// <summary>
/// A mob's walking (stage 5d, analysis/work/stage5/SPEC.md §5d.2; ARCHITECTURE.md §4.4 "Unit.MoveTo → MovementCodec →
/// event 3"): straight lines, no navmesh. <see cref="MoveTo"/> takes a new sample {From = where it is now, Target, Speed,
/// Stamp = now (monotonic, <see cref="Mob.NextStamp"/>), Direction = the travel direction} and sends it to the mob's
/// observers as event 3 {0 id, 1 <see cref="MovementCodec.EncodeSample"/>: flag 1 always, flag 2 while target != pos};
/// <see cref="MoveAlong"/> walks a path leg by leg (stageN mobnav, <see cref="MobNav"/>), each leg its own sample;
/// the server's position is that sample extrapolated (<see cref="Mob.Position"/>), the same as a player's. The client
/// dead-reckons a sample for at most 1 s (a5k.a 0x1809163f0, stage 7 movement SPEC §4), so a walk is sent again every
/// CombatSettings.MobMoveResend (0.5 s) from where the mob has got to; the arrival is a stop (22 bytes, pos = target,
/// speed 0). NewMob keys 7/8/9/10 come from the same sample (<see cref="Mob.BuildCreate"/>), so a re-send never carries
/// an older stamp than the last event 3. Each tick a walking mob's visibility cell follows it
/// (<see cref="Map.Relocate"/>: NewMob or Leave when it crosses a player's window). Touched only under <see cref="World.Sync"/>.
/// </summary>
public sealed class MotionMaster(Mob me)
{
    /// <summary>GameTimeStamp of the last event 3 sent for the walk.</summary>
    private long _sent;

    /// <summary>Whether the mob is walking: its sample has a target it has not reached.</summary>
    public bool Moving { get; private set; }

    /// <summary>The direction to face at the end of the walk (the home's rotation after a return); null = the travel direction.</summary>
    private float? _arrivalDirection;

    /// <summary>The speed the walk was asked for, before the mob's movespeed buffs (<see cref="Rescale"/>).</summary>
    private float _baseSpeed;

    /// <summary>Where the current leg ends.</summary>
    public Vector2 Destination => me.Movement.Target;

    /// <summary>The waypoints after the current leg (stageN mobnav: a path round walls), each sent as its own event 3 sample.</summary>
    private readonly Queue<Vector2> _legs = new();

    /// <summary>Where the whole walk ends (the last waypoint, else the current leg's end).</summary>
    public Vector2 FinalDestination => _legs.Count > 0 ? _legs.Last() : me.Movement.Target;

    /// <summary>Waypoints left after the current leg.</summary>
    public int LegsLeft => _legs.Count;

    /// <summary>
    /// Walks <paramref name="path"/> (its first point is where the mob stands, as <see cref="MobNav.Path"/> gives it): the first leg as
    /// <see cref="MoveTo"/>, each later one from the end of the one before as its own sample when the mob gets there (no stop between).
    /// </summary>
    public void MoveAlong(IReadOnlyList<Vector2> path, float speed, float? arrivalDirection = null)
    {
        if (path.Count < 2)
        {
            MoveTo(path.Count == 1 ? path[0] : me.Position, speed, arrivalDirection);
            return;
        }
        MoveTo(path[1], speed, arrivalDirection);
        if (!Moving) return;
        for (var i = 2; i < path.Count; i++) _legs.Enqueue(path[i]);
    }

    /// <summary>
    /// Walks straight from where the mob is now to <paramref name="target"/> at <paramref name="speed"/> m/s, facing that way
    /// (<paramref name="arrivalDirection"/>: the facing it stops with; null = keep the travel direction): a new sample, sent
    /// to the observers at once. A target it stands on already is a stop. The speed is × the mob's movespeedbonus sum, 0 while
    /// it is stunned or rooted (stageD effects SPEC §3.1, <see cref="Systems.SpellEffects.SpeedFactor"/>); nothing on a forced path
    /// (a knockback or fear, stageD SPEC §5.3).
    /// </summary>
    public void MoveTo(Vector2 target, float speed, float? arrivalDirection = null)
    {
        if (Systems.SpellMovement.Moving(me, me.GameNow)) return;
        _legs.Clear();
        var here = me.Position;
        _baseSpeed = speed;
        speed *= Systems.SpellEffects.SpeedFactor(me, me.GameNow);
        if (speed <= 0 || Vector2.Distance(here, target) < 0.01f)
        {
            Stop(arrivalDirection);
            return;
        }
        _arrivalDirection = arrivalDirection;
        Set(new MovementState(here, target, speed, me.Time) { Direction = Facing(here, target), Stamp = me.NextStamp() });
        Moving = true;
    }

    /// <summary>
    /// Stands where the mob is now (an arrival, the attack range reached, a reset), facing <paramref name="direction"/> (null:
    /// as it walked): a stop sample (pos = target) to the observers; nothing when it stands already and the facing is the same,
    /// nor on a forced path. A reset can force a new stop sample even when already stationary.
    /// </summary>
    public void Stop(float? direction = null, bool force = false)
    {
        if (Systems.SpellMovement.Moving(me, me.GameNow)) return;
        _legs.Clear();
        var m = me.Movement;
        var face = direction ?? m.Direction;
        if (!force && !Moving && m.From == m.Target && Math.Abs(face - m.Direction) < 0.01f) return;
        var here = me.Position;
        Moving = false;
        Set(new MovementState(here, here, 0, me.Time) { Direction = face, Stamp = me.NextStamp() });
    }

    /// <summary>
    /// The mob's movespeed buffs, stun or root changed: a walk under way is sent again from where it is at the new speed (a
    /// stop when it may not move); nothing when it stands or the speed is the same.
    /// </summary>
    public void Rescale()
    {
        if (!Moving) return;
        var speed = _baseSpeed * Systems.SpellEffects.SpeedFactor(me, me.GameNow);
        if (Math.Abs(speed - me.Movement.Speed) < 0.001f) return;
        var direction = _arrivalDirection;
        var legs = _legs.ToArray();
        MoveTo(Destination, _baseSpeed, direction);
        if (Moving) foreach (var leg in legs) _legs.Enqueue(leg);
    }

    /// <summary>
    /// A forced path (stageD effects SPEC §5.3: the observers got it as ForcedMovement): the sample from <paramref name="from"/> to
    /// <paramref name="to"/> at <paramref name="speed"/> without event 3, its own walk dropped; at the end the same with from = to
    /// (it stands there; the AI's next walk goes out from there).
    /// </summary>
    public void Forced(Vector2 from, Vector2 to, float speed, float facing)
    {
        Moving = false;
        _legs.Clear();
        me.Movement = new MovementState(from, to, speed, me.Time) { Direction = facing, Stamp = me.NextStamp() };
        _sent = me.Movement.Stamp;
        me.Map?.Relocate(me);
    }

    /// <summary>
    /// Freezes the mob where it is without telling anyone (its death: the client plays the fall where it last saw it); the
    /// corpse then stands there for later NewMobs and its loot bag.
    /// </summary>
    public void Freeze()
    {
        var here = me.Position;
        Moving = false;
        _legs.Clear();
        me.Movement = me.Movement with { From = here, Target = here, Speed = 0, At = me.Time };
    }

    /// <summary>
    /// The world tick: a walking mob that has reached its target stops (returns true once, at the arrival); one still on its
    /// way gets its sample sent again from where it is when the last one is <paramref name="resend"/> old. The visibility
    /// cell follows it either way.
    /// </summary>
    public bool Update(TimeSpan resend)
    {
        if (!Moving) return false;
        var arrived = me.Position == me.Movement.Target;
        if (arrived && _legs.Count > 0)
        {
            // A waypoint: the next leg from it at once, no stop between (the client gets the new sample as any re-send).
            var m0 = me.Movement;
            var next = _legs.Dequeue();
            Set(new MovementState(m0.Target, next, m0.Speed, me.Time) { Direction = Facing(m0.Target, next), Stamp = me.NextStamp() });
            return false;
        }
        if (arrived)
        {
            Moving = false;
            var m = me.Movement;
            Set(new MovementState(m.Target, m.Target, 0, me.Time) { Direction = _arrivalDirection ?? m.Direction, Stamp = me.NextStamp() });
            return true;
        }
        if (me.GameNow - _sent >= resend.Ticks)
            Set(new MovementState(me.Position, me.Movement.Target, me.Movement.Speed, me.Time) { Direction = me.Movement.Direction, Stamp = me.NextStamp() });
        else me.Map?.Relocate(me);
        return false;
    }

    /// <summary>The yaw from <paramref name="from"/> to <paramref name="to"/> in degrees (x = sin, z = cos, as TileMath.Rotate).</summary>
    public static float Facing(Vector2 from, Vector2 to) =>
        (float)TileMath.Mod360(Math.Atan2((double)to.X - from.X, (double)to.Y - from.Y) * 180 / Math.PI);

    /// <summary>Takes <paramref name="sample"/>, sends it as event 3 to every client that knows the mob, and moves its cell.</summary>
    private void Set(MovementState sample)
    {
        me.Movement = sample;
        _sent = sample.Stamp;
        if (me.Map is not { } map) return;
        var ev = MovementCodec.Event(me.Id, MovementCodec.EncodeSample(sample.Stamp, sample.From, sample.Direction, sample.Speed, sample.Target, 0));
        foreach (var p in map.ObserversOf(me)) p.Session.SendMovement(ev);
        map.Relocate(me);
    }
}
