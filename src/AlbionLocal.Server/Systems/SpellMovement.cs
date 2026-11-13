using System.Numerics;
using System.Xml.Linq;
using AlbionLocal.Photon;
using AlbionLocal.Server.Data;
using AlbionLocal.Server.Entities;
using AlbionLocal.Server.Protocol;

namespace AlbionLocal.Server.Systems;

/// <summary>
/// A forced movement under way (stageD effects SPEC §5.1): ForcedMovement key 1 kind (the client's bkg.MovementTypes: 0 dash,
/// 1 teleport, 2 knockback, 3 forcedmovement, 4 pull; stageM movement NOTES §1 B), key 2 start and key 3 end (GameTimeStamps), the straight path (key 5) and its speed (key 4), the spell
/// and client index of the node (keys 7/8). The unit's position follows the path over [start, end] (<see cref="At"/>).
/// </summary>
public sealed record ForcedMove(byte Kind, long Start, long End, Vector2 From, Vector2 To, float Speed, short Spell, byte Index)
{
    /// <summary>A player's own move: the lock ends with its first Move carrying key 6 = <see cref="Start"/>, or 300 ms after the end.</summary>
    public bool Released { get; internal set; }

    /// <summary>Where the unit is on the path at <paramref name="now"/>.</summary>
    public Vector2 At(long now)
    {
        if (now >= End || End <= Start) return now >= Start ? To : From;
        return Vector2.Lerp(From, To, Math.Clamp((float)((now - Start) / (double)(End - Start)), 0, 1));
    }
}

/// <summary>
/// The movement effects (analysis/work/stageD/effects/SPEC.md §5, batch 1 part B): dash (the caster's own, kind 0), teleport (the
/// caster's own blink, kind 1, <see cref="Teleport"/>), knockback (2), pull (4) and forcedmovement (fear, 3), each a straight path sent as ForcedMovement (140, ao4) {0 unit, 1 kind, 2 start
/// (Int64 ticks), 3 end (compressed), 4 speed, 5 [x0, z0, x1, z1], 7 spell, 8 client index, 9 source (the caster; left out for a
/// spell an area applied)} to the unit and its observers in the batch of the cast or hit. While it moves a mob runs no AI walk
/// or swing and sends no event 3 (the next walk of its AI after the end goes out from the end point); a player's Moves are
/// ignored until its first Move with key 6 = key 2 (the client's echo, 8 of 8 captured) or 300 ms after the end. A dash's
/// endeffect runs at its end at the end point, after CastSpell {0 me, 1 me, 2 end, 3 spell, 4 end, 5 end, 6 counter 0}. The
/// terrain cuts a path short (<see cref="Stopped"/>) and decides where a blink lands (<see cref="Landing"/>), both on the map's
/// walk grid. Touched only under <see cref="World.Sync"/>.
/// </summary>
public static class SpellMovement
{
    /// <summary>How long after its end a player's dash still blocks Moves without the key-6 echo (SPEC §5.3).</summary>
    public static readonly TimeSpan EchoGrace = TimeSpan.FromMilliseconds(300);

    /// <summary>ForcedMovement key 1 for a teleport (bkg.MovementTypes.Teleport; the client's teleport node bld.oh sets 1).</summary>
    public const byte TeleportKind = 1;

    /// <summary>
    /// The ForcedMovement kind of a movement tag (bkg.MovementTypes: Dash 0, Teleport 1, Knockback 2, ForcedMovement 3, Pull 4, Drag 5,
    /// DragVictim 6, Turn 7; the node readers bk1.oh set 4 for pull, bkh.oh 3 for forcedmovement). Before stageM a pull went out as 1,
    /// which the client reads as a teleport.
    /// </summary>
    public static byte KindOf(string tag) => tag switch { "dash" => 0, "teleport" => TeleportKind, "pull" => 4, "knockback" => 2, _ => 3 };

    /// <summary>Whether <paramref name="unit"/> is on a forced path at <paramref name="now"/>.</summary>
    public static bool Moving(IUnit unit, long now) => unit.Effects.Forced is { } f && now < f.End;

    /// <summary>
    /// Whether bot <paramref name="bot"/> is still held by its forced path at <paramref name="now"/> (stageJ pvp-spells SPEC §2.9): on it, or
    /// within <see cref="EchoGrace"/> after its end, the time a player's own Moves stay ignored without the key-6 echo a bot never sends. Its
    /// brain and walk wait meanwhile and go on from the path's end.
    /// </summary>
    public static bool Holds(Player bot, long now) => bot.Effects.Forced is { } f && now < f.End + EchoGrace.Ticks;

    /// <summary>
    /// Where <paramref name="unit"/> is at GameTimeStamp <paramref name="now"/>: on its forced path while it moves (the game clock),
    /// else a mob's extrapolated sample, a player's position from its last Move, a bot's from its last sample on the game clock (its stamps
    /// are game time, <see cref="Bots.BotManager.Time"/>).
    /// </summary>
    public static Vector2 PositionOf(IUnit unit, long now)
    {
        if (unit.Effects.Forced is { Released: false } f && now < f.End) return f.At(now);
        return unit switch
        {
            Mob m => m.Position,
            Player { IsBot: true } b => b.PositionAt(new DateTime(now, DateTimeKind.Utc)),
            Player p => p.PositionAt(DateTime.UtcNow),
            _ => Vector2.Zero,
        };
    }

    /// <summary>
    /// The time of a knockback or pull path (its listed duration, SPEC §5.1): knockback distance / movespeed; pull from
    /// <paramref name="target"/> to enddistance from <paramref name="caster"/> at movespeed. 0 = no move.
    /// </summary>
    public static double Seconds(XElement node, IUnit caster, IUnit target, long now)
    {
        var speed = SpellTemplate.Float(node, "movespeed", 10);
        if (speed <= 0) return 0;
        if (node.Name.LocalName == "knockback") return SpellTemplate.Float(node, "distance") / speed;
        if (caster == target) return 0;
        var length = Vector2.Distance(PositionOf(caster, now), PositionOf(target, now)) - SpellTemplate.Float(node, "enddistance", 1);
        return Math.Max(0, length) / speed;
    }

    /// <summary>
    /// A knockback, pull or fear on <paramref name="unit"/> by the hit <paramref name="c"/> of <paramref name="spell"/>
    /// (<paramref name="sub"/> listed with its time already, <see cref="SpellEffects.Make"/>): away from <paramref name="source"/> (the
    /// area centre or the caster; FromCollisionPoint is read as that too) for a knockback (distance at movespeed), toward the caster to
    /// enddistance for a pull, away from the source for a fear (initialdirection awayfromsource; random: any way) at movespeed, else
    /// the unit's attackmovespeed (movespeedpreset run: the T4 soldier's 6.5), for the listed time. <paramref name="fromArea"/>: no key 9.
    /// </summary>
    public static void Push(World world, IUnit caster, SpellTemplate spell, SubEffect sub, IUnit unit, Vector2 source, long start, bool fromArea)
    {
        if (unit.Map is not { } map || sub.DurationMs <= 0) return;
        var node = sub.Node;
        var tag = sub.Tag;
        var here = PositionOf(unit, start);
        var seconds = sub.DurationMs / 1000.0;
        var away = here - source;
        var dir = away.LengthSquared() > 1e-6f ? Vector2.Normalize(away) : Facing(caster is Player cp ? cp.Facing : caster is Mob cm ? cm.Direction : 0);
        float speed;
        Vector2 to;
        switch (tag)
        {
            case "knockback":
                speed = SpellTemplate.Float(node, "movespeed", 10);
                to = here + dir * SpellTemplate.Float(node, "distance");
                break;
            case "pull":
                speed = SpellTemplate.Float(node, "movespeed", 10);
                to = here - dir * (float)(speed * seconds);
                break;
            default:
                if ((string?)node.Attribute("initialdirection") == "random")
                {
                    var a = map.Random.NextDouble() * 2 * Math.PI;
                    dir = new Vector2((float)Math.Sin(a), (float)Math.Cos(a));
                }
                speed = SpellTemplate.Float(node, "movespeed") is > 0 and var s ? s
                    : unit is Mob m ? m.Template.AttackMoveSpeed : Player.DefaultSpeed;
                to = here + dir * (float)(speed * seconds);
                break;
        }
        to = Stopped(world, map, here, to); // stageN spells-bosses: a wall ends the push (a mob's knockback no longer throws a player into rock)
        Start(world, unit, new ForcedMove(KindOf(tag), start, start + sub.DurationMs * TimeSpan.TicksPerMillisecond, here, to, speed, spell.Id, (byte)sub.Index),
            fromArea ? 0 : caster.Id, null);
    }

    /// <summary>
    /// How far along its line a unit standing on a blocked cell may go to leave it (m): the first open cell within this is where the
    /// test of its path starts; with none it does not move. Before, a path from a blocked cell was not tested at all.
    /// </summary>
    private const float LeaveBlocked = 1.5f;

    /// <summary>
    /// Where a forced path from <paramref name="from"/> towards <paramref name="to"/> ends on the map's walk grid (stageN spells-bosses,
    /// public issue #27): the whole way when the line meets no blocked cell (or there is no grid yet), else <paramref name="clearance"/>
    /// before the point where it enters the first one — water, rock, a wall, the cluster edge. One grid ray (<see cref="WalkGrid.Ray"/>,
    /// the cell walk of the client's a84.f 0x18052CC30), so the end is exact, not the furthest tenth of the line. The ray runs
    /// <paramref name="clearance"/> past the aim too, so a path that ends right at a wall keeps the same distance. Only the line itself
    /// is tested: a path that grazes a corner sideways keeps less than the clearance to it [the client pushes its own unit out there,
    /// a84.g; not modelled]. A start on a blocked cell is tested from the first open cell on the line (<see cref="LeaveBlocked"/>).
    /// </summary>
    public static Vector2 Stopped(World world, Map map, Vector2 from, Vector2 to, float clearance = WalkGrid.UnitRadius)
    {
        if (AI.MobNav.Grid(world, map) is not { } grid) return to;
        var line = to - from;
        var length = line.Length();
        if (!float.IsFinite(length)) return from;
        if (length < 0.01f) return to;
        var dir = line / length;
        var start = from;
        if (!grid.Open(from))
        {
            if (!grid.Ray(from, from + dir * Math.Min(length, LeaveBlocked), out start, blocked: false)) return from;
            start += dir * 0.01f; // inside the open cell, not on its border
        }
        if (!grid.Ray(start, to + dir * clearance, out var hit)) return to;
        var free = Vector2.Distance(from, hit) - clearance;
        return free <= Vector2.Distance(from, start) + 0.01f ? from : from + dir * Math.Min(free, length);
    }

    /// <summary>
    /// Where a blink from <paramref name="from"/> to <paramref name="to"/> lands (public issue #27): at the aim when no tile blocks its
    /// cell, else at the point nearest to it on the way back to the caster that is <see cref="WalkGrid.UnitRadius"/> inside open
    /// ground; null when there is none (nothing open between the aim and the caster) and the blink does not happen. What lies between
    /// the two is not tested: a teleport is not a path, and whether a spell may be aimed across a wall is the spell's own
    /// <c>lineofsight</c> (BLINK "false"), which the client tests before it sends the cast (cwc.u 0x1809D9270: the grid ray with the
    /// spell's lineofsightcollision mask, bio.vv 0x1806C3100 — Projectile 2, Ground 1, Both 3). So a blink crosses a river or a wall
    /// to open ground behind it, and one aimed into the water, a rock or off the cluster lands on the near edge of it.
    /// </summary>
    public static Vector2? Landing(World world, Map map, Vector2 from, Vector2 to)
    {
        if (AI.MobNav.Grid(world, map) is not { } grid || grid.Open(to)) return to;
        var back = from - to;
        var length = back.Length();
        if (!float.IsFinite(length) || length < 0.01f) return null;
        var dir = back / length;
        var at = to;
        for (var tries = 0; tries < 8; tries++) // an open strip thinner than the radius is passed over
        {
            if (!grid.Ray(at, from, out var edge, blocked: false)) return null;
            var d = Vector2.Distance(to, edge) + WalkGrid.UnitRadius;
            if (d >= length - 0.01f) return null;
            at = to + dir * d;
            if (grid.Open(at)) return at;
        }
        return null;
    }

    /// <summary>
    /// A mob's own dash (stageN spells-bosses, 5f.4: the charges and leaps of the bosses) by node <paramref name="node"/> (client index
    /// <paramref name="index"/>) of <paramref name="spell"/>: as a player's (<see cref="Dash"/>) from where it stands to <paramref name="aim"/>
    /// (+ endpositionoffset along the line with a target), the length clamped to [mindistance, maxdistance or castrange] and stopped by
    /// the walls (<see cref="Stopped"/>); ForcedMovement kind 0 to its observers, its sample on the path; the endeffect at the end point.
    /// </summary>
    public static void MobDash(World world, Mob mob, SpellTemplate spell, XElement node, int index, Vector2 aim, bool hasTarget, long ticks, Action<Vector2, long>? endEffect)
    {
        if (mob.Map is not { } map) return;
        var speed = SpellTemplate.Float(node, "movespeed", 10);
        if (speed <= 0) return;
        var here = PositionOf(mob, ticks);
        var line = aim - here;
        var length = line.Length();
        var dir = length > 0.01f ? line / length : Facing(mob.Direction);
        if (hasTarget) length += SpellTemplate.Float(node, "endpositionoffset");
        var max = SpellTemplate.Float(node, "maxdistance") is > 0 and var m ? m : spell.CastRange;
        length = Math.Max(SpellTemplate.Float(node, "mindistance"), length);
        if (max > 0) length = Math.Min(max, length);
        if (length <= 0.01f) return;
        var to = Stopped(world, map, here, here + dir * length);
        length = Vector2.Distance(here, to);
        if (length <= 0.01f) return;
        var seconds = (string?)node.Attribute("ruleset") == "constanttime" && max > 0 ? max / speed : length / speed;
        var end = ticks + (long)Math.Round(seconds * 1000) * TimeSpan.TicksPerMillisecond;
        var move = new ForcedMove(0, ticks, end, here, to, speed, spell.Id, (byte)index);
        Start(world, mob, move, mob.Id, endEffect is null ? null : () => endEffect(move.To, move.End));
    }

    /// <summary>
    /// A dash of <paramref name="c"/>'s caster by node <paramref name="node"/> (client index <paramref name="index"/>) of
    /// <paramref name="spell"/> (SPEC §5.1): from where it is to the aim point (ground spells) or to the target + endpositionoffset
    /// along the line (HEROICSTRIKE2 −2), the length clamped to [mindistance, maxdistance or castrange]; the end after length /
    /// movespeed (timebasedondistance, the default; FLAMEDASH 4.59 m / 28 → 164 ms) or maxdistance (castrange) / movespeed
    /// (constanttime; HAMMER_SHOVE 2 / 16). Its endeffect runs at the end. The path is stopped by the terrain
    /// (<see cref="Stopped"/>: a wall, water, the cluster edge), as a knockback and a mob's dash are: the client only follows the path
    /// of the ForcedMovement (cux.afk glides along key 5 and sets the unit on its end), so the end the server sends is where the
    /// character stands (public issue #27: AXE_CHARGE aimed across a river ended on the water; the client lets it be aimed there,
    /// because water blocks the ground but not a spell's line of sight). A dash with no room at all — the caster at the wall it aims
    /// at — sends no path and runs its endeffect where the caster stands [inference: not captured].
    /// </summary>
    public static void Dash(SpellCasts.SpellContext c, SpellTemplate spell, XElement node, int index)
    {
        var p = c.Caster;
        var world = p.World;
        var speed = SpellTemplate.Float(node, "movespeed", 10);
        if (speed <= 0 || p.Combat.Down is not null || p.Map is not { } map) return;
        var here = PositionOf(p, c.Ticks);
        var aim = c.Target is { } target ? PositionOf(target, c.Ticks) : c.Point;
        var line = aim - here;
        var length = line.Length();
        var dir = length > 0.01f ? line / length : Facing(p.Facing);
        if (c.Target is not null) length += SpellTemplate.Float(node, "endpositionoffset");
        var max = SpellTemplate.Float(node, "maxdistance") is > 0 and var m ? m : spell.CastRange;
        length = Math.Max(SpellTemplate.Float(node, "mindistance"), length);
        if (max > 0) length = Math.Min(max, length);
        if (length <= 0.01f) return;
        var to = Stopped(world, map, here, here + dir * length);
        var travelled = Vector2.Distance(here, to);
        var endEffect = (string?)node.Attribute("endeffect") is { Length: > 0 } name ? world.Spells?.Template(name) : null;
        if (travelled <= 0.01f)
        {
            // A wall right in front: no path. The endeffect (AXE_CHARGE_CONDITION, FLAMEDASH_END) still runs, on the spot, after the cast's own batch.
            var spot = new ForcedMove(0, c.Ticks, c.Ticks, here, here, speed, spell.Id, (byte)index);
            if (endEffect is not null) map.Timers.At(world.Now, () => EndEffect(c, endEffect, spot));
            return;
        }
        var seconds = (string?)node.Attribute("ruleset") == "constanttime" && max > 0 ? max / speed : travelled / speed;
        var end = c.Ticks + (long)Math.Round(seconds * 1000) * TimeSpan.TicksPerMillisecond;
        var move = new ForcedMove(0, c.Ticks, end, here, to, speed, spell.Id, (byte)index);
        Start(world, p, move, p.Id, endEffect is null ? null : () => EndEffect(c, endEffect, move));
    }

    /// <summary>
    /// A blink of <paramref name="c"/>'s caster to <paramref name="to"/> by the teleport node of client index <paramref name="index"/> of
    /// <paramref name="spell"/> (BLINK 5, DMG_BLINK 3, MOUNTSPELL_BAT_BLINK 2, ENIGMA_BLADE_BLINK 3, DELAYED_TELEPORT 1; stageM movement
    /// NOTES §1 A/B, §5.1): ForcedMovement {0 me, 1 1 teleport, 2 start, 3 end = start, 5 [from, to], 7 spell, 8 index, 9 me} to the caster
    /// and its observers. The client moves its own player only by a ForcedMovement (cxf.aft → cux.ai3/afk): with the end already past it
    /// sets the position to the path's end on its next frame, and its next Move echoes key 2 as for a dash. Until then (or 300 ms) the
    /// caster's older Moves are ignored, so none of them puts it back. Before stageM the server sent Teleport (event 4) to the observers
    /// only: the caster's client stayed where it was and its next Move undid the blink.
    /// </summary>
    public static void Teleport(SpellCasts.SpellContext c, SpellTemplate spell, Vector2 to, int index)
    {
        var p = c.Caster;
        var here = PositionOf(p, c.Ticks);
        Log.Debug("combat", $"'{p.Character.Name}' blinks by {spell.Name}: ({here.X:0.##}, {here.Y:0.##}) → ({to.X:0.##}, {to.Y:0.##}), {Vector2.Distance(here, to):0.0} m");
        Start(p.World, p, new ForcedMove(TeleportKind, c.Ticks, c.Ticks, here, to, 0, spell.Id, (byte)index), p.Id, null);
    }

    /// <summary>
    /// The unit on its path: ForcedMovement to it and its observers, its sample set without event 3, its end at the end time. A path
    /// without time (a teleport) puts it at the end at once: a fresh sample there, the saved position, the cells in view.
    /// </summary>
    private static void Start(World world, IUnit unit, ForcedMove move, long source, Action? atEnd)
    {
        var map = unit.Map!;
        unit.Effects.Forced = move;
        unit.SendAround(new ForcedMovementEvent
        {
            ddn = unit.Id, ca3 = move.Kind, ca4 = move.Start, ca5 = move.End, ca6 = move.Speed,
            ca7 = [move.From.X, move.From.Y, move.To.X, move.To.Y], ca9 = move.Spell, cba = move.Index, cbb = source,
        });
        var facing = AI.MotionMaster.Facing(move.From, move.To);
        var jump = move.End <= move.Start;
        switch (unit)
        {
            case Mob mob:
                if (jump) mob.Motion.Forced(move.To, move.To, 0, facing);
                else mob.Motion.Forced(move.From, move.To, move.Speed, facing);
                break;
            case Player p:
                // Real time for the server's own extrapolation (PositionAt); the path itself is read on the game clock (PositionOf). A bot's
                // samples are on the game clock (BotManager.Time), so its path is too; its brain waits for the end (no key-6 echo).
                var utc = p.IsBot ? new DateTime(move.Start, DateTimeKind.Utc) : DateTime.UtcNow + TimeSpan.FromTicks(move.Start - world.GameNow);
                if (jump)
                {
                    p.Movement = new MovementState(move.To, move.To, 0, utc) { Direction = facing, Stamp = p.NextMoveStamp() };
                    p.Facing = facing;
                    (p.Character.PositionX, p.Character.PositionY) = (move.To.X, move.To.Y);
                    p.UpdateVisibility(DateTime.UtcNow);
                }
                else p.Movement = new MovementState(move.From, move.To, move.Speed, utc) { Direction = facing, Stamp = p.NextMoveStamp() };
                break;
        }
        Log.Debug("combat", $"#{unit.Id} forced movement kind {move.Kind} by {move.Spell}: ({move.From.X:0.##}, {move.From.Y:0.##}) → ({move.To.X:0.##}, {move.To.Y:0.##}) in {(move.End - move.Start) / TimeSpan.TicksPerMillisecond} ms");
        SpellCasts.At(world, map, move.End, () =>
        {
            if (unit.Effects.Forced != move || unit.Map != map) return;
            End(world, unit, move);
            atEnd?.Invoke();
        });
    }

    /// <summary>The end of a path: the unit stands at its end (a mob's next AI walk goes out from there; a player's own Move follows).</summary>
    private static void End(World world, IUnit unit, ForcedMove move)
    {
        switch (unit)
        {
            case Mob mob:
                unit.Effects.Forced = null;
                mob.Motion.Forced(move.To, move.To, 0, mob.Direction);
                break;
            case Player p:
                if (move.Released)
                {
                    unit.Effects.Forced = null; // its own Moves have taken over already
                    break;
                }
                // Kept for the key-6 echo (AcceptMove); it stands at the end until its next Move.
                p.Movement = new MovementState(move.To, move.To, 0, DateTime.UtcNow) { Direction = p.Facing, Stamp = p.NextMoveStamp() };
                p.UpdateVisibility(DateTime.UtcNow);
                break;
        }
    }

    /// <summary>
    /// A dash's endeffect (FLAMEDASH_END, DODGE_MOVESPEED, HAMMER_SHOVE_AREA): CastSpell {0 me, 1 me, 2 the end point, 3 spell,
    /// 4 end, 5 end, 6 0} to the caster and its observers, then the spell runs at the end point.
    /// </summary>
    private static void EndEffect(SpellCasts.SpellContext c, SpellTemplate effect, ForcedMove move)
    {
        var p = c.Caster;
        if (p.Map != c.Map || p.Combat.Down is not null) return;
        p.SendAround(new CastSpellEvent { ddn = p.Id, bvw = p.Id, bvx = move.To, bvy = effect.Id, bvz = move.End, bv0 = move.End, Extra = new() { [6] = (byte)0 } });
        SpellCasts.Execute(c with { Target = null, Point = move.To, Ticks = move.End, Depth = c.Depth + 1 }, effect);
    }

    /// <summary>
    /// Whether a Move of <paramref name="p"/> with key 6 <paramref name="echo"/> is taken (SPEC §5.3): always without a forced move;
    /// during one only the echo of its key 2, which ends the lock; 300 ms after its end the lock ends by itself.
    /// </summary>
    /// <remarks><paramref name="at"/>: the Move's key 1 position. The release of a player's own dash or blink is logged at Info (stageM
    /// movement NOTES §5.4): the client sets its echo before it starts the move (cxf.aft), so an echo at the path's start means it got
    /// the ForcedMovement and did not move, one at the end that it moved; no echo within the 300 ms means it never took the event.</remarks>
    public static bool AcceptMove(Player p, long echo, long now, Vector2? at = null)
    {
        if (p.Effects.Forced is not { } f) return true;
        if (echo == f.Start || now >= f.End + EchoGrace.Ticks)
        {
            if (!f.Released && f.Kind is 0 or TeleportKind && !p.IsBot)
                Log.Info("combat", echo == f.Start
                    ? $"'{p.Character.Name}' echoes its forced move kind {f.Kind} by {f.Spell} ({Vector2.Distance(f.From, f.To):0.0} m) "
                      + (at is { } q ? $"at {Vector2.Distance(q, f.To):0.0} m from the path's end, {Vector2.Distance(q, f.From):0.0} m from its start" : "")
                    : $"'{p.Character.Name}' sent no echo of its forced move kind {f.Kind} by {f.Spell} within {EchoGrace.TotalMilliseconds:0} ms of its end");
            f.Released = true;
            if (now >= f.End) p.Effects.Forced = null;
            return true;
        }
        return false;
    }

    /// <summary>The unit vector of a yaw in degrees (x = sin, z = cos, as <see cref="AI.MotionMaster.Facing"/>).</summary>
    public static Vector2 Facing(float degrees) => new((float)Math.Sin(degrees * Math.PI / 180), (float)Math.Cos(degrees * Math.PI / 180));
}
