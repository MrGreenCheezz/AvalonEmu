using System.Globalization;
using System.Numerics;
using AlbionLocal.Photon;
using AlbionLocal.Server.Data;
using AlbionLocal.Server.Entities;
using AlbionLocal.Server.Protocol;
using AlbionLocal.Server.Systems;

namespace AlbionLocal.Server.AI;

/// <summary>
/// A hostile mob that fights (stage 5d, analysis/work/stage5/SPEC.md §5d.1-§5d.3; ARCHITECTURE.md §6.3 level 1):
/// <list type="bullet">
/// <item><b>Idle</b>: GUARD points stand at home facing the tile's rotation; PATROL points walk their waypoints (WALK at
/// movespeed, RUN at attackmovespeed) and wait each one's idle seconds; any other mob with a roamingradius walks at
/// movespeed to a random point within it of home and waits uniform(roamingidletimemin, max). Only while a client sees it.</item>
/// <item><b>Aggro</b> (every tick, aggrodelayafterspawn after the spawn; not <see cref="PassiveAI"/>): the nearest player on
/// the map within aggroradius, or within the frontal cone (aggrofrontalconeangle/length) in front of it, with threat 1.
/// Mobs of a faction in its alertsfactions within its alertradius that are idle join on the same target.</item>
/// <item><b>Threat</b>: damage × damageaggrofactor, healing × healingaggrofactor (to the healer); the target changes only
/// to a player with more than the current one's threat × minthreatfactorforchangetotarget (1.1 when that player is in attack
/// range, 1.3 out of it). NewMob key 12 is the target (<see cref="Mob.TargetId"/>).</item>
/// <item><b>Chase</b>: at the target at attackmovespeed, stopping just inside attack range (attackrange + both
/// collision radii); aimed again when the target has moved CombatSettings.MobReaimDistance from the aim point. Straight when the
/// line is clear on the map's walk grid, else along a <see cref="MobNav"/> path round the walls (stageN mobnav), planned anew at
/// most every <see cref="RepathSeconds"/> while it walks; no grid (none built yet, no colliders): straight as before.</item>
/// <item><b>Sight</b> (stageN mobnav): it aggroes only a player it sees (<see cref="WalkGrid.Sight"/> on the grid), and swings
/// only at a target in reach it sees; in reach without sight it walks at the target until it does.</item>
/// <item><b>Attack</b>: in range it stops and swings every 1 / attackspeed (from the last swing's start, on the first tick
/// at or after; the first one CombatSettings.MobReactionTime + meleeattackdamagetime after the combat entry): Attack (13) to the mob's observers {0 mob,
/// 1 start, 2 target, 3 type 1 melee / 2 ranged / 4 magic, 4 start + meleeattackdamagetime, 5 impact (= key 4 for melee,
/// + distance / AutoAttackProjectileSpeed otherwise), 6 0}; at the impact the HealthUpdate on the player stamped key 4
/// (<see cref="Combat.DamagePlayer"/>): attackdamage × the player's resistance, unscaled. The &lt;AI&gt; spells are
/// <see cref="BehaviourAI"/> (stageD mobspells), through <see cref="Act"/> before the chase and the swing.</item>
/// <item><b>Leash</b> (<see cref="Reset"/>): farther from home than pursuitradius (<see cref="PassiveAI"/>: passivepursuitradius;
/// 0 = no distance leash), no valid threat left, or CombatSettings.MobLeashNoHit since its last swing while the target is out
/// of reach (§9 Q3: counted from the mob's last Attack, as captured). Then the MOB_RETURNHOME buff (or the mob's returnhomespell), the run home with spell immunity, the refill
/// and the clear on arrival.</item>
/// </list>
/// Touched only under <see cref="World.Sync"/>.
/// </summary>
public class AggressiveAI(Mob me) : MobAI(me)
{
    /// <summary>Whether it picks targets by itself (aggroradius); false for <see cref="PassiveAI"/>.</summary>
    protected virtual bool Aggressive => true;

    /// <summary>The leash distance from home: pursuitradius (passive mobs: passivepursuitradius); 0 = none.</summary>
    protected virtual float Pursuit => Me.Template.PursuitRadius;

    /// <summary><see cref="World.Clock"/> time an idle mob moves again (roaming, patrol waits); null = at once.</summary>
    private TimeSpan? _idleUntil;

    /// <summary>The PATROL waypoint it walks to or waits at.</summary>
    private int _waypoint = -1;

    /// <summary>GameTimeStamp of its next swing.</summary>
    private long _nextSwing;

    /// <summary>GameTimeStamp of its next swing (a cast moves it, stageD mobspells R4).</summary>
    protected long NextSwing
    {
        get => _nextSwing;
        set => _nextSwing = value;
    }

    /// <summary>GameTimeStamp its last swing's windup ends (Attack key 4): no spell starts before (stageD mobspells R4).</summary>
    protected long WindupEnd { get; private set; }

    /// <summary>The no-hit leash counts again from now (a mob CastStart restarts it, stageD mobspells §5).</summary>
    protected void RestartLeash(World world) => _lastSwing = world.Now;

    /// <summary>
    /// <see cref="World.Clock"/> time of its last swing's start (its combat entry before the first): the no-hit leash counts
    /// from it, as the captured resets came 3.8-4.0 s after the mob's last Attack (SPEC §9 Q3, mobs/mob_reset.txt).
    /// </summary>
    private TimeSpan _lastSwing;

    /// <summary>Where it aimed its chase (the target's position then).</summary>
    private Vector2? _aim;

    /// <summary><see cref="World.Now"/> of its last path search (stageN mobnav): a walking mob searches again only <see cref="RepathSeconds"/> later.</summary>
    private TimeSpan _pathedAt = TimeSpan.FromDays(-1);

    /// <summary>The least time between two path searches of a walking mob (stageN mobnav).</summary>
    public const double RepathSeconds = 0.75;

    /// <summary>Whether <paramref name="b"/> is in its sight from <paramref name="a"/> on <paramref name="map"/> (no walk grid: always).</summary>
    protected static bool Sees(World world, Map map, Vector2 a, Vector2 b) => MobNav.Sees(world, map, a, b);

    /// <summary>Counts swings and resets: a swing's impact or a reset's buff that belongs to an older one is dropped.</summary>
    private int _generation;

    /// <summary>The return-home buff's ActiveSpellEffectsUpdate went out (so the arrival's clear has something to clear).</summary>
    private bool _buffSent;

    /// <summary>Why the last reset came, for .mob ai.</summary>
    private string? _resetWhy;

    public override string Describe() => string.Create(CultureInfo.InvariantCulture,
        $"{Kind} {State}{(Me.Threat.Count > 0 ? $", threat {string.Join(", ", Me.Threat.Select(t => $"'{t.Key.Character.Name}' {t.Value:0.#}"))}" : "")}"
        + $"{(Me.TargetId != 0 ? $", target #{Me.TargetId}" : "")}{(Me.Motion.Moving ? $", walking to ({Me.Motion.Destination.X:0.#}, {Me.Motion.Destination.Y:0.#})" : "")}"
        + $", home ({Me.Home.X:0.#}, {Me.Home.Y:0.#}){(_resetWhy is { } why ? $", last reset: {why}" : "")}");

    protected override void Update(World world, Map map)
    {
        var arrived = Me.Motion.Update(world.Combat.MobMoveResend);
        switch (State)
        {
            case MobState.Idle:
                if (Aggressive && Aggro(world, map) is { } target)
                {
                    EnterCombat(world, map, target, 1, alert: true);
                    Fight(world, map);
                }
                else Idle(world, map, arrived);
                break;
            case MobState.Combat:
                Fight(world, map);
                break;
            case MobState.Returning:
                if (Me.Motion.Moving) break;
                if (!arrived && Vector2.Distance(Me.Position, Me.Home) >= 0.01f && Me.Template.AttackMoveSpeed > 0)
                {
                    WalkHome(world, map);
                    break;
                }
                // Home (a mob that cannot walk: where it stands), once its return buff is out (a reset at home or a
                // keepPositionOnReset mob waits the buff's 0.2 s, so the buff still comes before the arrival's clear).
                if (_buffSent || Me.Template.DontResetAfterPursuit) Arrive(world, map);
                break;
        }
    }

    // ---- idle ----

    private void Idle(World world, Map map, bool arrived)
    {
        var t = Me.Template;
        var point = Me.Spawn;
        if (string.Equals(point.Behavior, "GUARD", StringComparison.OrdinalIgnoreCase)) return; // stands at home facing the tile
        var now = world.Now;
        if (arrived)
        {
            // At a waypoint: its idle seconds; at a roaming point: uniform(roamingidletimemin, max).
            _idleUntil = now + TimeSpan.FromSeconds(Patrols && _waypoint >= 0 ? point.Waypoints[_waypoint].Idle
                : t.RoamingIdleTimeMin + (Math.Max(t.RoamingIdleTimeMin, t.RoamingIdleTimeMax) - t.RoamingIdleTimeMin) * map.Random.NextDouble());
            return;
        }
        if (Me.Motion.Moving || !map.ObserversOf(Me).Any()) return; // nobody sees it: it stands (active cells, ARCHITECTURE.md §4.3)
        if (_idleUntil is null)
        {
            // Just spawned or back home: a first wait before it moves.
            _idleUntil = now + TimeSpan.FromSeconds(Patrols ? 0 : t.RoamingIdleTimeMin + (Math.Max(t.RoamingIdleTimeMin, t.RoamingIdleTimeMax) - t.RoamingIdleTimeMin) * map.Random.NextDouble());
            return;
        }
        if (now < _idleUntil) return;
        if (Patrols)
        {
            _waypoint = (_waypoint + 1) % point.Waypoints.Count;
            var w = point.Waypoints[_waypoint];
            Me.Motion.MoveTo(w.Position, string.Equals(w.Pace, "RUN", StringComparison.OrdinalIgnoreCase) ? t.AttackMoveSpeed : t.MoveSpeed);
            if (!Me.Motion.Moving) _idleUntil = now + TimeSpan.FromSeconds(w.Idle);
            return;
        }
        if (t.RoamingRadius <= 0 || t.MoveSpeed <= 0) return;
        var angle = map.Random.NextDouble() * 2 * Math.PI;
        var radius = t.RoamingRadius * Math.Sqrt(map.Random.NextDouble());
        var spot = Me.Home + new Vector2((float)(Math.Sin(angle) * radius), (float)(Math.Cos(angle) * radius));
        if (MobNav.Grid(world, map) is { } grid && grid.Free(Me.Position) && !grid.ClearFree(Me.Position, spot))
        {
            _idleUntil = null; // a roaming point behind a wall or a cliff (stageN mobnav): another after the next wait
            return;
        }
        Me.Motion.MoveTo(spot, t.MoveSpeed);
        if (!Me.Motion.Moving) _idleUntil = null;
    }

    /// <summary>A PATROL point with waypoints walks them; any other roams.</summary>
    private bool Patrols => string.Equals(Me.Spawn.Behavior, "PATROL", StringComparison.OrdinalIgnoreCase) && Me.Spawn.Waypoints.Count > 0;

    /// <summary>
    /// The player it aggroes now: the nearest one on the map within aggroradius, or within the frontal cone, once
    /// aggrodelayafterspawn has passed since its spawn; null when none. The cone (19 mobs: length 3-10 m, angle 40-180°) is
    /// read as extra reach in front, a vision cone, not as a limit on the radius: the SPEC's "inside the frontal cone when
    /// set" leaves it open and no capture shows a mob ignoring a player behind it within aggroradius.
    /// </summary>
    private Player? Aggro(World world, Map map)
    {
        var t = Me.Template;
        if (t.AggroRadius <= 0 || world.Now - Me.SpawnedAt < TimeSpan.FromSeconds(t.AggroDelayAfterSpawn)) return null;
        if (Systems.SpellEffects.CannotAggro(Me, world.GameNow)) return null; // cannotaggro (stageD effects batch 3): it starts no fight
        var here = Me.Position;
        var reach = Math.Max(t.AggroRadius, t.AggroConeLength);
        Player? best = null;
        var bestDistance = float.MaxValue;
        foreach (var p in map.Fighters)
        {
            if (!Valid(p, map) || (p.IsMounted && !t.CanAggroMountedPlayers)) continue; // mobs.xml canaggromountedplayers (stageD mounts)
            if (Systems.SpellEffects.InvisibleToMobs(p, world.GameNow)) continue; // invisibility to mobs (stageD effects batch 2): no aggro
            var at = p.PositionAt(DateTime.UtcNow);
            var d = Vector2.Distance(here, at);
            if (d > reach || d >= bestDistance) continue;
            if (d > t.AggroRadius && !InCone(here, at)) continue;
            if (!Sees(world, map, here, at)) continue; // behind a wall (stageN mobnav)
            (best, bestDistance) = (p, d);
        }
        return best;
    }

    /// <summary>Whether <paramref name="at"/> lies within the frontal cone (half of aggrofrontalconeangle either side of its facing).</summary>
    private bool InCone(Vector2 here, Vector2 at)
    {
        var t = Me.Template;
        if (t.AggroConeAngle <= 0 || Vector2.Distance(here, at) > t.AggroConeLength) return false;
        var off = Math.Abs(MotionMaster.Facing(here, at) - Me.Direction) % 360;
        return Math.Min(off, 360 - off) <= t.AggroConeAngle / 2;
    }

    /// <summary>A player it may fight: on its map, alive, joined, neither knocked down nor on the get-up's invulnerability (stage 5e).</summary>
    protected static bool Valid(Player p, Map map) => p.Map == map && !p.Combat.Untouchable
        && p.Health.At(p.World.GameNow) > 0 && !SpellEffects.InvisibleToMobs(p, p.World.GameNow);

    // ---- combat ----

    /// <summary>
    /// Combat starts (an aggro pick, damage, a fellow's alert) on <paramref name="target"/> with <paramref name="threat"/>:
    /// it becomes the NewMob key 12 target, the first swing waits CombatSettings.MobReactionTime + meleeattackdamagetime (SPEC
    /// §5d.1 "1.0 s + the swing windup": soldier 2.0 s, ghoul 1.35 s, against the captured first Attack about 1.8 s after the
    /// player's first hit); with <paramref name="alert"/>
    /// the idle mobs of its alertsfactions within its alertradius join on the same target (they do not alert further).
    /// </summary>
    private void EnterCombat(World world, Map map, Player target, double threat, bool alert)
    {
        State = MobState.Combat;
        Me.Threat[target] = Me.Threat.GetValueOrDefault(target) + threat;
        Me.TargetId = target.Id;
        _nextSwing = world.GameNow + world.Combat.MobReactionTime.Ticks + SpellCasts.Seconds(Me.Template.MeleeAttackDamageTime);
        _lastSwing = world.Now;
        _aim = null;
        _idleUntil = null;
        Log.Debug("ai", string.Create(CultureInfo.InvariantCulture,
            $"{Me} fights '{target.Character.Name}': first basic attack ready in {(_nextSwing - world.GameNow) / (double)TimeSpan.TicksPerSecond:0.###} s"));
        OnEnterCombat(world);
        if (!alert || Me.Template.AlertRadius <= 0) return;
        var here = Me.Position;
        foreach (var other in map.Objects.Values.OfType<Mob>())
            if (other != Me && !other.Dead && other.AI is AggressiveAI { State: MobState.Idle } fellow && Alerts(other.Template.Faction)
                && Vector2.Distance(other.Position, here) <= Me.Template.AlertRadius)
                fellow.EnterCombat(world, map, target, 1, alert: false);
    }

    /// <summary>Combat has just started (the fight clock of <see cref="BehaviourAI"/>, stageD mobspells R6).</summary>
    protected virtual void OnEnterCombat(World world) { }

    /// <summary>
    /// The &lt;AI&gt; step of a fight tick (<see cref="BehaviourAI"/>), after the leash checks and before the chase and the swing:
    /// true when the mob casts, stands after a cast or has just started one, so it neither walks nor swings this tick.
    /// </summary>
    protected virtual bool Act(World world, Map map, Player target) => false;

    /// <summary>A leash reset starts (before the threat is cleared): <see cref="BehaviourAI"/> cancels its cast and resets its state.</summary>
    protected virtual void OnReset(World world, Map map) { }

    /// <summary>The reset's own steps are done (its effects cleared, the run home started): <see cref="BehaviourAI"/>'s onReset.</summary>
    protected virtual void OnResetDone(World world, Map map) { }

    /// <summary>Whether a mob of <paramref name="faction"/> answers this one's alert (alertsfactions: "all", "NONE" or a list of factions).</summary>
    private bool Alerts(string faction)
    {
        var list = Me.Template.AlertsFactions;
        if (string.Equals(list, "all", StringComparison.OrdinalIgnoreCase)) return true;
        return list.Split([',', ';', ' '], StringSplitOptions.RemoveEmptyEntries).Any(f => string.Equals(f, faction, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>
    /// The fight's tick: the threat list cleaned up, the leash checks, the target, then the chase or the swing. Stunned it does
    /// nothing (<see cref="Paused"/>); rooted it does not chase (its walk speed is 0) and swings in range only.
    /// </summary>
    private void Fight(World world, Map map)
    {
        foreach (var p in Me.Threat.Keys.Where(p => !Valid(p, map)).ToList()) Me.Threat.Remove(p);
        if (Me.Threat.Count == 0)
        {
            Reset(world, map, "no target left");
            return;
        }
        if (Pursuit > 0 && Vector2.Distance(Me.Position, Me.Home) > Pursuit)
        {
            Reset(world, map, "pursuit radius");
            return;
        }
        var target = Target();
        if (Paused(world)) return;
        if (Act(world, map, target)) return;
        var here = Me.Position;
        var there = target.PositionAt(DateTime.UtcNow);
        var reach = Reach(world);
        var distance = Vector2.Distance(here, there);
        var grid = MobNav.Grid(world, map);
        var sees = grid is null || grid.Sight(here, there);
        if (distance > reach || !sees)
        {
            if (world.Now - _lastSwing >= world.Combat.MobLeashNoHit)
            {
                Reset(world, map, distance > reach ? "no hit while out of reach" : "no hit while out of sight");
                return;
            }
            if (Me.Template.AttackMoveSpeed <= 0 || SpellEffects.Rooted(Me, world.GameNow)) return;
            if (Me.Motion.Moving && _aim is { } aim && Vector2.Distance(aim, there) <= world.Combat.MobReaimDistance) return;
            Chase(world, grid, here, there, distance, reach, sees);
            return;
        }
        if (Me.Motion.Moving) Me.Motion.Stop(MotionMaster.Facing(here, there));
        _aim = null;
        var now = world.GameNow;
        if (now >= _nextSwing && CanAttack) Swing(world, map, target, now);
    }

    /// <summary>
    /// The chase's walk (stageN mobnav): to just inside the attack range on the line to the target when that line is clear on the
    /// grid (or there is no grid), else along a path to the target itself (in reach and sight it stops on the next tick); a walking
    /// mob searches again only <see cref="RepathSeconds"/> after its last search, and over the tick's search budget it keeps its walk.
    /// </summary>
    private void Chase(World world, WalkGrid? grid, Vector2 here, Vector2 there, float distance, float reach, bool sees)
    {
        var back = distance > 0.01f ? (here - there) / distance : Vector2.Zero;
        var stop = sees ? there + back * Math.Max(0, reach - Math.Min(0.5f, reach * 0.25f)) : there;
        // A mob standing off the free cells (by a wall) walks straight when it sees the target and the line from the free cell
        // next to it is clear: the open ground right by its spawn.
        if (grid is null || grid.ClearFree(here, stop) || sees && !grid.Free(here) && grid.ClearFree(grid.NearestFree(here, 3) ?? here, stop))
        {
            _aim = there;
            Me.Motion.MoveTo(stop, Me.Template.AttackMoveSpeed);
            return;
        }
        if (Me.Motion.Moving && world.Now - _pathedAt < TimeSpan.FromSeconds(RepathSeconds)) return;
        var path = MobNav.Path(world, grid, here, there, partial: true, out var overBudget);
        if (overBudget) return; // the next tick
        _aim = there;
        _pathedAt = world.Now;
        if (path is null || path.Count < 2)
        {
            if (Me.Motion.Moving) Me.Motion.Stop(MotionMaster.Facing(here, there)); // no way to it: it waits (the leash ends it)
            return;
        }
        Me.Motion.MoveAlong(path, Me.Template.AttackMoveSpeed);
    }

    /// <summary>
    /// The run home (stageN mobnav): along a <see cref="MobNav"/> path when the straight line crosses a wall; straight without a grid,
    /// without a path or over the tick's search budget (a returning mob is immune and must get home).
    /// </summary>
    private void WalkHome(World world, Map map)
    {
        var t = Me.Template;
        if (MobNav.Grid(world, map) is { } grid && MobNav.Path(world, grid, Me.Position, Me.Home, partial: false, out _) is { Count: > 2 } path)
        {
            Me.Motion.MoveAlong(path, t.AttackMoveSpeed, Me.HomeDirection);
            return;
        }
        Me.Motion.MoveTo(Me.Home, t.AttackMoveSpeed, Me.HomeDirection);
    }

    /// <summary>
    /// Stunned (stageD SPEC §3.2): it stands (its walk stopped by the speed factor, <see cref="MotionMaster.Rescale"/>) and its
    /// swing timer pauses: the time left to its next swing when the stun came is kept until it ends. The same on a forced path
    /// (knockback, fear; stageD SPEC §5.3: no AI walk or swing until its end). Returns whether it is stunned or moved.
    /// </summary>
    private bool Paused(World world)
    {
        var now = world.GameNow;
        if (!SpellEffects.Stunned(Me, now) && !SpellMovement.Moving(Me, now))
        {
            _swingLeft = null;
            return false;
        }
        _swingLeft ??= Math.Max(0, _nextSwing - now);
        _nextSwing = now + _swingLeft.Value;
        if (Me.Motion.Moving) Me.Motion.Stop();
        _lastSwing = world.Now; // no leash for the no-hit time while it cannot act
        return true;
    }

    /// <summary>The swing time left when a stun came; null while it is not stunned.</summary>
    private long? _swingLeft;

    /// <summary>Whether it has an auto-attack (attacktype melee, ranged or magic and an attackspeed) and may use it (no cannotattack, stageD SPEC §3.2).</summary>
    private bool CanAttack => Me.Template.AttackType is "melee" or "ranged" or "magic" && Me.Template.AttackSpeed > 0 && !SpellEffects.CannotAttack(Me, Me.GameNow);

    /// <summary>Centre distance within which it hits: attackrange + its collision radius + the character's.</summary>
    private float Reach(World world) => Me.Template.AttackRange + Me.Template.CollisionRadius + world.CollisionRadius;

    /// <summary>
    /// Its target by threat (SPEC §5d.1): the current one unless another player's threat exceeds the current one's ×
    /// minthreatfactorforchangetotarget (in attack range 1.1, out of it 1.3).
    /// </summary>
    private Player Target()
    {
        var current = Me.Threat.Keys.FirstOrDefault(p => p.Id == Me.TargetId);
        var best = Me.Threat.MaxBy(t => t.Value).Key;
        if (current is not null && best != current)
        {
            var world = best.World;
            var inRange = Vector2.Distance(Me.Position, best.PositionAt(DateTime.UtcNow)) <= Reach(world);
            var factor = inRange ? Me.Template.MinThreatFactorInRange : Me.Template.MinThreatFactorOutOfRange;
            if (Me.Threat[best] <= Me.Threat[current] * factor) best = current;
        }
        Me.TargetId = best.Id;
        return best;
    }

    /// <summary>
    /// One swing at <paramref name="target"/> starting at <paramref name="now"/>: Attack (13) to the mob's observers (the
    /// target included), and at the impact the damage (attackdamage × the player's resistance; HealthUpdate {1 key 4 of the
    /// Attack, 4 1 physical for melee and ranged, 2 magic, 5 0 melee / 1 ranged and magic, 6 the mob, 7 -1}). A melee hit
    /// needs the target still within reach + 1 m at the impact. The next swing is 1 / attackspeed after this one's start,
    /// run on the first tick at or after it: the captured intervals are never shorter (ghoul 1/1.1 → 1.003-1.005 s, soldier
    /// 2.5 → 2.51 s, mage 2.0 → 2.01-2.04 s, mobs/attack_timing.txt), so there are no catch-up slots.
    /// </summary>
    private void Swing(World world, Map map, Player target, long now)
    {
        var t = Me.Template;
        var interval = SpellCasts.Seconds(1 / (t.AttackSpeed * Math.Max(0.01, 1 + Me.Effects.Sum("attackspeedbonus", now))));
        _lastSwing = world.Now;
        _nextSwing = now + interval;
        var melee = t.AttackType == "melee";
        var damageAt = now + SpellCasts.Seconds(t.MeleeAttackDamageTime);
        WindupEnd = damageAt;
        var flight = melee ? 0 : Vector2.Distance(Me.Position, target.PositionAt(DateTime.UtcNow)) / world.Combat.AutoAttackProjectileSpeed;
        var impact = damageAt + SpellCasts.Seconds(flight);
        Log.Debug("ai", string.Create(CultureInfo.InvariantCulture,
            $"{Me} starts a basic attack on '{target.Character.Name}': hit in {(impact - now) / (double)TimeSpan.TicksPerSecond:0.###} s"));
        byte type = t.AttackType switch { "ranged" => 2, "magic" => 4, _ => 1 };
        map.SendToObservers(Me, new AttackEvent
        {
            ddn = Me.Id, bs0 = now, bs1 = target.Id, bs2 = type, bs3 = damageAt, bs4 = impact, Extra = new() { [6] = (byte)0 },
        });
        var effect = t.AttackType == "magic" ? EffectType.Magic : EffectType.Physical;
        var origin = melee ? HitOrigin.MeleeAutoAttack : HitOrigin.RangedAutoAttack;
        var generation = _generation;
        SpellCasts.At(world, map, impact, () =>
        {
            if (generation != _generation || Me.Dead || Me.Removed || State != MobState.Combat || !Valid(target, map)) return;
            if (melee && Vector2.Distance(Me.Position, target.PositionAt(DateTime.UtcNow)) > Reach(world) + SpellCasts.RangeTolerance) return;
            Combat.DamagePlayer(Me, target, t.AttackDamage * SpellEffects.DealtFactor(Me, spell: false, effect, vsPlayer: true, damageAt) * Combat.ResistanceOf(target, effect),
                effect, origin, -1, damageAt);
        });
    }

    public override void OnDamaged(Player attacker, double damage)
    {
        // A returning mob is immune (no damage, so no call here) except a dontresetafterpursuit one: hit, it fights again.
        var threat = damage * Me.Template.DamageAggroFactor;
        if (State == MobState.Combat)
        {
            Me.Threat[attacker] = Me.Threat.GetValueOrDefault(attacker) + threat;
            return;
        }
        if (Me.Map is { } map) EnterCombat(attacker.World, map, attacker, threat, alert: true); // a damaged mob always fights
    }

    public override void AddThreat(Player p, double threat)
    {
        if (State == MobState.Combat)
        {
            Me.Threat[p] = Math.Max(0, Me.Threat.GetValueOrDefault(p) + threat);
            return;
        }
        if (State == MobState.Returning || Me.Dead || Me.Map is not { } map || !Valid(p, map)) return;
        EnterCombat(p.World, map, p, Math.Max(0, threat), alert: false);
    }

    public override void OnHealed(Player healer, Player target, double amount)
    {
        if (State != MobState.Combat) return;
        Me.Threat[healer] = Me.Threat.GetValueOrDefault(healer) + amount * Me.Template.HealingAggroFactor;
    }

    public override void Forget(Player p)
    {
        Me.Threat.Remove(p);
        if (Me.TargetId == p.Id) Me.TargetId = 0;
    }

    public override void OnDeath()
    {
        _generation++;
        Me.Threat.Clear();
        Me.Immune = false;
        State = MobState.Idle;
        Me.Motion.Freeze();
    }

    // ---- leash (SPEC §5d.3) ----

    /// <summary>
    /// The reset (all 8 captured ones): its swings stop, the threat and key 12 are cleared, the players it fought whose last
    /// enemy it was leave combat; then RegenerationHealthEnergyComboChanged (95) to its observers with the return-home buff's
    /// regeneration ((base + 200) × 3 health, base + 20 energy for MOB_RETURNHOME, spell 1063), CombatSettings.MobReturnBuffDelay
    /// later the buff's ActiveSpellEffectsUpdate (11), and the run home at attackmovespeed with spell immunity
    /// (<see cref="Mob.Immune"/>). dontresetafterpursuit: home only, no buff and no refill; keepPositionOnReset: where it stands
    /// becomes home, no walk. A cast under way is cancelled first (<see cref="OnReset"/>, stageD mobspells §4.3).
    /// </summary>
    protected void Reset(World world, Map map, string why)
    {
        OnReset(world, map);
        _generation++;
        _resetWhy = why;
        Me.Threat.Clear();
        Me.TargetId = 0;
        _aim = null;
        Combat.EnemyGone(map, Me);
        // An in-place reset still needs a fresh stop sample: the client may retain
        // the last attack's facing target even when the server was already stationary.
        if (!Me.Motion.Moving) Me.Motion.Stop(force: true);
        Log.Debug("ai", $"{Me} resets: {why}");
        Summons.Reset(world, map, Me); // ttlafterreset, ttlaftercasterreset (stageD effects batch 3)
        var t = Me.Template;
        if (t.KeepPositionOnReset)
        {
            Me.Motion.Stop(); // a reset mid-chase stands where it is: that is home now
            Me.Home = Me.Position;
            Me.HomeDirection = Me.Direction;
        }
        State = MobState.Returning;
        if (t.DontResetAfterPursuit)
        {
            WalkHome(world, map);
            OnResetDone(world, map);
            return;
        }
        // The players' damage over time and debuffs end with the reset (the buff's list, then the arrival's clear, tell the clients).
        Me.Effects.Clear();
        if (Me.LevelBuff is not null) SpawnManager.LevelBuff(map, Me, world, fill: false); // stageG: keeponretreat, in the list the return buff sends
        Me.Motion.Rescale();
        var buff = ReturnHomeBuff.Of(world, t.ReturnHomeSpell);
        var now = world.GameNow;
        Me.Health.Set(Me.Health.At(now), Math.Max(now, Me.Health.Stamp));
        Me.Health.RegenPerSecond = (t.HitPointsRegeneration + buff.HitPoints) * (1 + buff.HitPointsBonus);
        Me.Energy.Set(Me.Energy.At(now), Math.Max(now, Me.Energy.Stamp));
        Me.Energy.RegenPerSecond = t.EnergyRegeneration + buff.Energy;
        map.SendToObservers(Me, RegenerationCombo(Me, now));
        Me.Immune = true;
        _buffSent = false;
        var generation = _generation;
        map.Timers.At(world.Now + world.Combat.MobReturnBuffDelay, () =>
        {
            if (generation != _generation || State != MobState.Returning || Me.Dead || Me.Map != map) return;
            _buffSent = true;
            // The buff as the mob's list (new observers get it after NewMob); the built-in layout without spell data.
            if (world.Spells?.Template(buff.Spell) is { } spell && spell.Effects.Count == buff.SubEffects)
            {
                var subs = spell.Effects.Select((node, i) => SpellEffects.Make(world, Me, 100, 100, node, i, Me, now)).ToArray();
                SpellEffects.Apply(world, Me, spell, 0, 100, 100, now, subs);
                SpellEffects.Send(world, Me);
            }
            else map.SendToObservers(Me, buff.Effects(Me.Id, now));
        });
        WalkHome(world, map); // keepPositionOnReset: home already, a no-op
        OnResetDone(world, map);
    }

    /// <summary>
    /// Home again (all 3 captured arrivals, mobs/mob_reset.txt): with the reset buff, health and energy at max and
    /// RegenerationHealthEnergyComboChanged still with the buff's regeneration, at once a second one with the base
    /// regeneration (key 5 left out at 0), and CombatSettings.MobHomeClearDelay later the clear ActiveSpellEffectsUpdate (the
    /// death clear's layout, captured 17 times; at once with <paramref name="clearNow"/>); immunity off, out of the players'
    /// enemies; idle.
    /// </summary>
    private void Arrive(World world, Map map, bool clearNow = false)
    {
        State = MobState.Idle;
        _idleUntil = null;
        _waypoint = -1;
        var t = Me.Template;
        Me.Immune = false;
        _generation++; // a buff still waiting is dropped
        Combat.EnemyGone(map, Me);
        if (t.DontResetAfterPursuit) return;
        var now = world.GameNow;
        Me.Health.Set(Me.Health.Max, now);
        Me.Energy.Set(Me.Energy.Max, now);
        map.SendToObservers(Me, RegenerationCombo(Me, now));
        Me.Effects.Clear();
        Me.Health.Fill(SpellEffects.MobMaxHealth(Me, now), t.HitPointsRegeneration, now); // a world boss's HealthFactor stays (stageH)
        SpawnManager.LevelBuff(map, Me, world); // stageG: a dungeon level's buff stays (keeponretreat), the health full at its maximum
        Me.Energy.Fill(t.EnergyMax, t.EnergyRegeneration, now);
        map.SendToObservers(Me, RegenerationCombo(Me, now));
        Log.Debug("ai", $"{Me} is home");
        // The clear: the empty list, or the list with the level buff alone on a dungeon floor.
        void Clear()
        {
            if (Me.LevelBuff is null) map.SendToObservers(Me, Combat.EffectsCleared(Me.Id));
            else SpellEffects.Send(world, Me);
        }
        if (clearNow)
        {
            Clear();
            return;
        }
        var generation = _generation; // a new reset before it sends its own buff instead; a death sends its own clear
        map.Timers.At(world.Now + world.Combat.MobHomeClearDelay, () =>
        {
            if (generation != _generation || Me.Dead || Me.Map != map) return;
            Clear();
        });
    }

    /// <summary>
    /// The AI goes off (.mob ai off, World.MobAi): a fight ends as a reset would without the run home (threat, key 12 and the
    /// players' enemies cleared, its swings dropped), a return ends where the mob is with the arrival's refill and clear, and
    /// every walk stops there, so nothing stays immune, in combat or drifting from the clients' view.
    /// </summary>
    public override void Halt(World world, Map map)
    {
        switch (State)
        {
            case MobState.Combat:
                _generation++;
                Me.Threat.Clear();
                Me.TargetId = 0;
                _aim = null;
                Combat.EnemyGone(map, Me);
                break;
            case MobState.Returning:
                Me.Motion.Stop();
                Arrive(world, map, clearNow: true);
                break;
        }
        State = MobState.Idle;
        _idleUntil = null;
        Me.Motion.Stop();
    }

    /// <summary>
    /// RegenerationHealthEnergyComboChanged (95, aym): 0 mob, 1 now, 2/3 health and max, 5 health regeneration (left out at
    /// 0), 6 Int64 now, 7/8 energy and max, 10 energy regeneration, 11 Int64 now.
    /// </summary>
    public static RegenerationHealthEnergyComboChangedEvent RegenerationCombo(Mob mob, long now) => new()
    {
        ddn = mob.Id, dtz = now, dt0 = mob.Health.At(now), dt1 = mob.Health.Max, dt3 = mob.Health.RegenPerSecond, dt4 = now,
        dt5 = mob.Energy.At(now), dt6 = mob.Energy.Max, dt8 = mob.Energy.RegenPerSecond, dt9 = now,
    };
}

/// <summary>
/// A mob with aggroradius 0 (or the client's default hostility): it never picks a target itself, fights back when damaged
/// (or alerted) and leashes at passivepursuitradius (SPEC §5d.1, §5d.3). Critters without an attack (attacktype none) only
/// take the attacker as their target and follow it.
/// </summary>
public sealed class PassiveAI(Mob me) : AggressiveAI(me)
{
    protected override bool Aggressive => false;

    protected override float Pursuit => Me.Template.PassivePursuitRadius;
}

/// <summary>
/// The buff a mob runs home with (SPEC §5d.3): MOB_RETURNHOME (spell 1063) or the mob's returnhomespell, read from its
/// spells.xml buffovertime children (hitpointsregeneration +200, hitpointsregenerationbonus 2 → × 3, energyregeneration
/// +20) and its effect count (4 with the spellimmunity) for ActiveSpellEffectsUpdate key 7.
/// </summary>
public sealed record ReturnHomeBuff(short Spell, float HitPoints, float HitPointsBonus, float Energy, int SubEffects)
{
    /// <summary>MOB_RETURNHOME as spells.xml has it, for a world without spell data.</summary>
    public static ReturnHomeBuff Default { get; } = new(1063, 200, 2, 20, 4);

    public const string DefaultSpell = "MOB_RETURNHOME";

    /// <summary>The buff of <paramref name="spell"/> (null: MOB_RETURNHOME); the built-in one without spell data or when spells.xml lacks it.</summary>
    public static ReturnHomeBuff Of(World world, string? spell)
    {
        var s = world.Spells?.Template(spell ?? DefaultSpell);
        if (s is null && spell is not null && world.Spells is not null)
        {
            Combat.LogOnce("returnhome:" + spell, $"returnhomespell {spell} is not in spells.xml: MOB_RETURNHOME used");
            s = world.Spells.Template(DefaultSpell);
        }
        if (s is null) return Default;
        float hp = 0, bonus = 0, energy = 0;
        foreach (var e in s.Effects.Where(e => e.Name.LocalName == "buffovertime"))
        {
            var value = Data.SpellTemplate.Float(e, "value");
            switch ((string?)e.Attribute("type"))
            {
                case "hitpointsregeneration": hp += value; break;
                case "hitpointsregenerationbonus": bonus += value; break;
                case "energyregeneration": energy += value; break;
            }
        }
        return new ReturnHomeBuff(s.Id, hp, bonus, energy, Math.Max(1, s.Effects.Count));
    }

    /// <summary>
    /// Its ActiveSpellEffectsUpdate (11) on mob <paramref name="id"/> (21 captured, all alike): {0 mob, 1 [spell], 2 [100],
    /// 3 [100], 4 [the reset's stamp], 5 {1} (the stack count, most likely), 7 3 bits per timed sub-effect, value 1 each,
    /// little endian (4 sub-effects: {0x49, 0x02}), 9 {1} (one bit per buff), 10 {0}}; keys 6 and 8 left out.
    /// </summary>
    public ActiveSpellEffectsUpdateEvent Effects(long id, long stamp) => Combat.BuffEffects(id, Spell, SubEffects, stamp);
}
