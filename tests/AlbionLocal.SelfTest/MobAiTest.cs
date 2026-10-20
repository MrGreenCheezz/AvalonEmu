using System.Buffers.Binary;
using System.Numerics;
using AlbionLocal.Server;
using AlbionLocal.Server.AI;
using AlbionLocal.Server.Content;
using AlbionLocal.Server.Data;
using AlbionLocal.Server.Entities;
using AlbionLocal.Server.Game;
using AlbionLocal.Server.Persistence;
using AlbionLocal.Server.Protocol;
using AlbionLocal.Server.Systems;
using static AlbionLocal.SelfTest.AlbionTests;
using static AlbionLocal.SelfTest.CombatTest;

namespace AlbionLocal.SelfTest;

/// <summary>
/// Stage 5d (analysis/work/stage5/SPEC.md §5d.4): the mob AI in 0204 on a <see cref="ManualClock"/> with FakePeers, the
/// spawn points around the stand taken away first. A soldier veteran (2306) aggroes a player stepping inside its
/// aggroradius, runs at it with event 3 at attackmovespeed, stops in reach and swings every 1 / attackspeed with the
/// HealthUpdate on the player at Attack key 4; pulled away it resets (the MOB_RETURNHOME regeneration combo, the buff's
/// ActiveSpellEffectsUpdate, the run home with spell immunity, full bars and the clear on arrival). A rabbit is passive
/// and roams; the target changes only past 1.1 × the current threat in attack range.
/// </summary>
public static class MobAiTest
{
    public static IEnumerable<(string, Action)> All =>
    [
        ("mob AI: soldier aggro inside 8 m, chase (event 3), stop in reach, Attack every 2.5 s with the player's HealthUpdate, leash with MOB_RETURNHOME, home and reset", SoldierFight),
        ("mob AI: a passive rabbit roams and fights only when hit; the target changes past 1.1 × the threat in range", PassiveAndThreat),
        ("mob AI: a soldier led past its pursuit radius resets there; .mob ai off settles a returning mob", PursuitLeash),
        ("mob AI: a mage's magic Attack (k3 4, flight to k5, HealthUpdate EffectType 2 at k4) every 2 s; a ghoul's every 0.91-1.0 s", RangedAndFast),
        ("mob AI (stageN mobnav): no aggro through a wall; a mage chases round it on free cells and casts only in sight", AroundWall),
    ];

    private const string Soldier = "T4_MOB_UNDEAD_SOLDIER_VETERAN";

    /// <summary>
    /// An arena with the AI on and every mob within 150 m of the stand despawned for good, so only the test's own mobs act, on an open walk
    /// grid (<see cref="OpenGrid"/>).
    /// </summary>
    private static (Arena Arena, Map Map) Quiet(Arena arena)
    {
        var map = arena.World.Maps.Find("0204")!;
        OpenGrid(map, arena.Stand);
        lock (arena.World.Sync)
            foreach (var m in map.Objects.Values.OfType<Mob>().Where(m => Vector2.Distance(m.Position, arena.Stand) < 150).ToList())
                SpawnManager.Despawn(m, arena.World, respawn: false);
        return (arena, map);
    }

    /// <summary>
    /// A walk grid without walls 150 m around <paramref name="around"/> for <paramref name="map"/> (<see cref="MobNav.Use"/>): the stage-5d
    /// fights walk straight, as they were written. Without it the map's real grid (the guard tower and houses by the stand) came from a pool
    /// thread at a tick that depended on the machine's load, and the rabbit's roam and the soldier's chase changed with it (the old flakiness).
    /// </summary>
    internal static void OpenGrid(Map map, Vector2 around)
    {
        int x = (int)Math.Floor(around.X), z = (int)Math.Floor(around.Y);
        MobNav.Use(map, WalkGrid.FromCells(x - 150, z - 150, x + 150, z + 150, [], [around]));
    }

    /// <summary>Event 3 key 1 of our own mobs (MovementCodec layout): stamp, position, speed (flag 1), target (flag 2, else the position).</summary>
    private static (long Ts, Vector2 Pos, float Speed, Vector2 Target) Sample(Dictionary<byte, object?> p)
    {
        var b = (byte[])p[1]!;
        var ts = BinaryPrimitives.ReadInt64LittleEndian(b.AsSpan(1));
        var pos = new Vector2(BinaryPrimitives.ReadSingleLittleEndian(b.AsSpan(9)), BinaryPrimitives.ReadSingleLittleEndian(b.AsSpan(13)));
        var at = 18;
        var speed = 0f;
        if ((b[0] & MovementCodec.FlagSpeed) != 0) (speed, at) = (BinaryPrimitives.ReadSingleLittleEndian(b.AsSpan(at)), at + 4);
        var target = (b[0] & MovementCodec.FlagTarget) != 0
            ? new Vector2(BinaryPrimitives.ReadSingleLittleEndian(b.AsSpan(at)), BinaryPrimitives.ReadSingleLittleEndian(b.AsSpan(at + 4))) : pos;
        return (ts, pos, speed, target);
    }

    private static List<(long Ts, Vector2 Pos, float Speed, Vector2 Target)> Moves(FakePeer peer, int mark, Mob mob) =>
        [.. Since(peer, mark).Where(e => e.Code == EventCodes.Move && L(e.P, 0) == mob.Id).Select(e => Sample(e.P))];

    private static void SoldierFight()
    {
        using var arena0 = new Arena(ai: true);
        var world = arena0.World;
        var (session, peer, me) = arena0.Join("Tank", new InventoryItem("T4_MAIN_SWORD"));
        var (arena, map) = Quiet(arena0);
        var t = Client.Clusters!.Mobs.Find(Soldier)!;
        Check(t is { Id: 2306, AggroRadius: 8, AttackSpeed: 0.4f, AttackDamage: 431, MeleeAttackDamageTime: 1, AttackType: "melee", HitPointsRegeneration: 33, Hostility: NpcHostility.Hostile }
              && t.PursuitRadius > 0,
              $"mobs.xml {Soldier}: 2306, aggroradius 8, attackspeed 0.4, 431 damage, meleeattackdamagetime 1, melee, hp regen 33 (got aggro {t.AggroRadius}, speed {t.AttackSpeed}, "
              + $"damage {t.AttackDamage}, windup {t.MeleeAttackDamageTime}, {t.AttackType}, regen {t.HitPointsRegeneration}, pursuit {t.PursuitRadius}, run {t.AttackMoveSpeed}, "
              + $"range {t.AttackRange}, roaming {t.RoamingRadius}, delay {t.AggroDelayAfterSpawn})");
        var soldier = arena.Spawn(Soldier, new Vector2(25, 0));
        Check(soldier.AI is AggressiveAI and not PassiveAI && me.Visibility.Knows(soldier), "the soldier gets the AggressiveAI and is in view");

        // 1. 25 m away nothing happens (its roaming stays within its radius of home), even past aggrodelayafterspawn.
        arena.Run(Math.Max(1, t.AggroDelayAfterSpawn + 0.5));
        Check(soldier.AI.State == MobState.Idle && soldier.TargetId == 0, $"no aggro 25 m away ({soldier.AI.Describe()})");

        // The player steps 7 m from it: the next tick it aggroes and runs at the player at attackmovespeed.
        lock (world.Sync) me.Teleport(soldier.Position + new Vector2(-7, 0), 90, DateTime.UtcNow);
        var mark = peer.Events.Count;
        var entry = world.GameNow + SpellCasts.Seconds(0.05);
        arena.Run(0.05);
        var run = Moves(peer, mark, soldier);
        var here = me.PositionAt(DateTime.UtcNow);
        var reach = t.AttackRange + t.CollisionRadius + world.CollisionRadius;
        Check(soldier.AI.State == MobState.Combat && soldier.TargetId == me.Id && run.Count == 1 && run[0].Speed == t.AttackMoveSpeed
              && Vector2.Distance(run[0].Target, here) <= reach && run[0].Target != run[0].Pos,
              $"aggro inside 8 m: target = the player, event 3 at {t.AttackMoveSpeed} m/s towards it, ending within reach {reach:0.##} (got {run.Count} samples, {soldier.AI.Describe()})");

        // 2. It stops in reach, then swings at 1 s + its 1 s windup + n × 2.5 s, each Attack {1 start, 2 me, 3 melee, 4 = 5 =
        //    start + 1 s, 6 0}.
        arena.Run(6);
        var events = Since(peer, mark);
        var moves = Moves(peer, mark, soldier);
        Check(moves.Count >= 2 && moves[^1].Speed == 0 && moves[^1].Pos == moves[^1].Target && Vector2.Distance(soldier.Position, here) <= reach,
              $"then a stop (speed 0, pos = target) in reach (got {moves.Count} samples, the mob {Vector2.Distance(soldier.Position, here):0.##} m away)");
        var attacks = events.Where(e => e.Code == EventCodes.Attack && L(e.P, 0) == soldier.Id).Select(e => e.P).ToList();
        Check(attacks.Count == 2 && L(attacks[0], 1) == entry + SpellCasts.Seconds(2) && L(attacks[1], 1) - L(attacks[0], 1) == SpellCasts.Seconds(2.5)
              && attacks.All(a => L(a, 2) == me.Id && Convert.ToByte(a[3]) == 1 && L(a, 4) - L(a, 1) == SpellCasts.Seconds(1) && L(a, 5) == L(a, 4) && Convert.ToByte(a[6]) == 0),
              $"two Attacks in 6 s: the first 1 s + 1 s windup after the aggro, then 2.5 s apart (1 / 0.4), k4 − k1 = 1000 ms, k3 = 1 (got {Show(attacks.Select(a => (EventCodes.Attack, a)))})");
        var r = Combat.ResistanceOf(me, EffectType.Physical);
        var hits = events.Where(e => e.Code == EventCodes.HealthUpdate && L(e.P, 0) == me.Id).Select(e => e.P).ToList();
        var dmg = Combat.Rounded(431 * r);
        Check(hits.Count == 2 && hits.Select(h => L(h, 1)).SequenceEqual(attacks.Select(a => L(a, 4))) && hits.All(h => F(h, 2) == -dmg && Convert.ToByte(h[4]) == 1
              && Convert.ToByte(h[5]) == 0 && L(h, 6) == soldier.Id && Convert.ToInt16(h[7]) == -1) && F(hits[1], 3) == me.Health.Max - 2 * dmg,
              $"each hit: HealthUpdate on the player {{1 = Attack k4, 2: -round(431 × {r:0.###}), 3 left, 4: 1, 5: 0, 6 mob, 7: -1}} (got {Show(hits.Select(h => (EventCodes.HealthUpdate, h)))})");
        var state = events.Where(e => e.Code == EventCodes.InCombatStateUpdate).Select(e => e.P).ToList();
        Check(me.Combat.InCombat && me.Combat.Hit && state.Count == 1 && state[0][1] is true && state[0][2] is true && me.Health.RegenPerSecond == 0
              && events.Any(e => e.Code == EventCodes.RegenerationHealthChanged && L(e.P, 0) == me.Id),
              $"the player is in combat, hit: InCombatStateUpdate {{1 true, 2 true}} and its regeneration off (got {Show(state.Select(s => (EventCodes.InCombatStateUpdate, s)))})");

        // 3. The player jumps beyond its pursuit radius (40): the mob runs after it at 5 m/s and resets 4 s after its last
        //    Attack with the target out of reach (§9 Q3, before it gets 40 m from home): the reset sequence, the run home
        //    with immunity.
        var home = soldier.Home;
        var energy = soldier.Energy.RegenPerSecond;
        mark = peer.Events.Count;
        lock (world.Sync) me.Teleport(home + new Vector2(-(t.PursuitRadius + 6), 0), 90, DateTime.UtcNow);
        for (var i = 0; i < 400 && soldier.AI.State != MobState.Returning; i++) arena.Run(0.05);
        var lastAttack = L(attacks[^1], 1);
        Check(soldier.AI.State == MobState.Returning && soldier.Immune && soldier.TargetId == 0 && soldier.Threat.Count == 0 && !me.Combat.InCombat
              && !Since(peer, mark).Any(e => e.Code == EventCodes.Attack) && world.GameNow - lastAttack >= SpellCasts.Seconds(4) && world.GameNow - lastAttack < SpellCasts.Seconds(4.1),
              $"the leash 4 s after its last Attack: returning, immune, no threat or target; the player's combat is over at once "
              + $"({(world.GameNow - lastAttack) / 1e7:0.##} s, {soldier.AI.Describe()})");
        var combo = Since(peer, mark).Where(e => e.Code == EventCodes.RegenerationHealthEnergyComboChanged && L(e.P, 0) == soldier.Id).Select(e => e.P).ToList();
        Check(combo.Count == 1 && F(combo[0], 5) == 699 && F(combo[0], 10) == energy + 20 && F(combo[0], 3) == soldier.Health.Max,
              $"RegenerationHealthEnergyComboChanged {{5: (33 + 200) × 3 = 699, 10: base + 20}} (got {Show(combo.Select(c => (EventCodes.RegenerationHealthEnergyComboChanged, c)))})");
        var resetAt = world.Now;
        arena.Run(0.25);
        var buff = Since(peer, mark).Where(e => e.Code == EventCodes.ActiveSpellEffectsUpdate && L(e.P, 0) == soldier.Id).Select(e => e.P).ToList();
        Check(buff.Count == 1 && buff[0][1] is short[] and [1063] && buff[0][2] is float[] and [100] && buff[0][3] is float[] and [100] && buff[0][4] is long[] { Length: 1 }
              && buff[0][5] is byte[] and [1] && buff[0][7] is byte[] and [0x49, 0x02] && buff[0][9] is byte[] and [1] && buff[0][10] is byte[] and [0]
              && !buff[0].ContainsKey(6) && !buff[0].ContainsKey(8),
              $"0.2 s later ActiveSpellEffectsUpdate {{1 [1063], 2 [100], 3 [100], 4 [ts], 5 {{1}}, 7 {{0x49, 0x02}}, 9 {{1}}, 10 {{0}}}} (got {Show(buff.Select(b => (EventCodes.ActiveSpellEffectsUpdate, b)))})");
        var back = Moves(peer, mark, soldier).SkipWhile(m => m.Target != home).ToList(); // the chase's samples first
        Check(back.Count >= 1 && back[0].Speed == t.AttackMoveSpeed && back.All(m => m.Target == home),
              $"event 3 home at attackmovespeed, R = {Combat.ResistanceOf(me, EffectType.Physical):0.###} (got {back.Count} samples home)");
        // Damage on it while it returns does nothing.
        mark = peer.Events.Count;
        var (value, stamp) = (soldier.Health.Value, soldier.Health.Stamp);
        lock (world.Sync) Combat.Damage(me, soldier, 1000, EffectType.Physical, HitOrigin.Direct, -1, world.GameNow);
        Check(soldier.Health.Value == value && soldier.Health.Stamp == stamp && !Since(peer, mark).Any(e => e.Code == EventCodes.HealthUpdate) && soldier.Threat.Count == 0,
              "damage during the return does nothing (spell immunity): no HealthUpdate, no threat");

        // 4. Home: a stop there, full bars, the combo still with the buff's regeneration, one with the base one, and 0.15 s
        //    later the clear list (mobs/mob_reset.txt, all 3 arrivals); idle again.
        for (var i = 0; i < 400 && soldier.AI.State == MobState.Returning; i++) arena.Run(0.05);
        var last = Moves(peer, mark, soldier);
        Check(soldier.AI.State == MobState.Idle && !soldier.Immune && last.Count > 0 && last[^1].Speed == 0 && last[^1].Pos == home && soldier.Position == home
              && soldier.Health.At(world.GameNow) == soldier.Health.Max && soldier.Energy.At(world.GameNow) == soldier.Energy.Max
              && !Since(peer, mark).Any(e => e.Code == EventCodes.ActiveSpellEffectsUpdate),
              $"home after {(world.Now - resetAt).TotalSeconds:0.##} s: a stop at home, full bars, idle, no clear yet ({soldier.AI.Describe()})");
        var homeMark = peer.Events.Count;
        arena.Run(0.1);
        var early = Since(peer, homeMark).Count(e => e.Code == EventCodes.ActiveSpellEffectsUpdate);
        arena.Run(0.1);
        var arrival = Since(peer, mark);
        var homeCombo = arrival.Where(e => e.Code == EventCodes.RegenerationHealthEnergyComboChanged && L(e.P, 0) == soldier.Id).Select(e => e.P).ToList();
        var clear = arrival.Where(e => e.Code == EventCodes.ActiveSpellEffectsUpdate && L(e.P, 0) == soldier.Id).Select(e => e.P).ToList();
        Check(early == 0 && clear.Count == 1, $"the clear 0.15 s after the arrival, not within 0.1 s (got {early} early, {clear.Count} in all)");
        Check(homeCombo.Count == 2 && F(homeCombo[0], 5) == 699 && F(homeCombo[0], 2) == soldier.Health.Max && F(homeCombo[0], 10) == energy + 20
              && F(homeCombo[1], 5) == 33 && F(homeCombo[1], 2) == soldier.Health.Max && F(homeCombo[1], 10) == energy && L(homeCombo[0], 1) == L(homeCombo[1], 1)
              && clear.Count == 1 && clear[0][3] is float[] { Length: 0 } && clear[0][4] is long[] { Length: 0 } && clear[0][7] is byte[] and [0] && clear[0][9] is byte[] and [0],
              $"then the combos {{2 max, 5: 699, 10 base + 20}}, {{2 max, 5: 33, 10 base}} and the clear {{3 [], 4 [], 7 {{0}}, 9 {{0}}}} (got {Show(arrival.Where(e => e.Code != EventCodes.Move))})");

        // 5. Every event 3 of the mob has a non-decreasing stamp; a newcomer's NewMob carries none older (k9).
        var all = Moves(peer, 0, soldier);
        var (_, newcomer, _) = arena.Join("Newcomer", new InventoryItem("T4_MAIN_SWORD"));
        var create = Since(newcomer, 0).Single(e => e.Code == EventCodes.NewMob && L(e.P, 0) == soldier.Id).P;
        Check(all.Zip(all.Skip(1)).All(p => p.Second.Ts >= p.First.Ts) && L(create, 9) >= all[^1].Ts,
              $"{all.Count} event 3 samples with non-decreasing stamps; NewMob k9 ≥ the last of them");
        _ = session;
    }

    private static void PassiveAndThreat()
    {
        using var arena0 = new Arena(ai: true);
        var world = arena0.World;
        var (_, peer, me) = arena0.Join("Hunter", new InventoryItem("T4_MAIN_SWORD"));
        var (_, _, other) = arena0.Join("Helper", new InventoryItem("T4_MAIN_SWORD"));
        var (arena, _) = Quiet(arena0);

        // A rabbit (aggroradius 0): passive; it roams within its radius of home at movespeed; hit, it takes the attacker.
        var rabbit = arena.Spawn("MOB_RABBIT", new Vector2(3, 0));
        var rt = rabbit.Template;
        var mark = peer.Events.Count;
        arena.Run(Math.Max(rt.RoamingIdleTimeMax, 1) + 1.5);
        var walks = Moves(peer, mark, rabbit);
        Check(rabbit.AI is PassiveAI && rabbit.AI.State == MobState.Idle && rabbit.TargetId == 0
              && !Since(peer, mark).Any(e => e.Code == EventCodes.Attack),
              $"the rabbit is passive: no aggro, no Attack next to the player (aggroradius {rt.AggroRadius})");
        Check(rt.RoamingRadius <= 0 || rt.MoveSpeed <= 0
              || (walks.Count > 0 && walks[0].Speed == rt.MoveSpeed && Vector2.Distance(walks[0].Target, rabbit.Home) <= rt.RoamingRadius + 0.01f),
              $"it roams: event 3 at movespeed {rt.MoveSpeed} to a point within roamingradius {rt.RoamingRadius} of home, after its idle wait (got {walks.Count} samples)");
        if (rt.RoamingRadius > 0 && rt.MoveSpeed > 0)
        {
            // At the point it waits uniform(roamingidletimemin, max), then walks on (SPEC §5d.4 item 6).
            (long Ts, Vector2 Pos, float Speed, Vector2 Target)? stop = null, next = null;
            for (var i = 0; i < 80 && next is null; i++)
            {
                arena.Run(0.5);
                var all = Moves(peer, mark, rabbit);
                var go = all.FindIndex(m => m.Speed > 0);
                var at = go < 0 ? -1 : all.FindIndex(go, m => m.Speed == 0 && m.Pos == m.Target);
                var on = at < 0 ? -1 : all.FindIndex(at, m => m.Speed > 0);
                if (at >= 0) stop = all[at];
                if (on >= 0) next = all[on];
            }
            var wait = next is { } n && stop is { } s ? (n.Ts - s.Ts) / (double)TimeSpan.TicksPerSecond : -1;
            Check(wait >= rt.RoamingIdleTimeMin - 0.001 && wait <= Math.Max(rt.RoamingIdleTimeMin, rt.RoamingIdleTimeMax) + 0.051,
                  $"after the stop it waits within [{rt.RoamingIdleTimeMin}, {rt.RoamingIdleTimeMax}] s before the next walk (got {wait:0.###} s)");
        }
        lock (world.Sync) Combat.Damage(me, rabbit, 1, EffectType.Physical, HitOrigin.Direct, -1, world.GameNow);
        Check(rabbit.AI.State == MobState.Combat && rabbit.TargetId == me.Id, $"hit, it fights the attacker ({rabbit.AI.Describe()})");

        // Idle behaviours of the point: GUARD stands (no event 3); PATROL walks its waypoints, WALK at movespeed, RUN at
        // attackmovespeed, waiting each one's idle seconds.
        var map = world.Maps.Find("0204")!;
        Mob guard, patrol;
        Waypoint w1 = new(arena.Stand + new Vector2(-6, 4), 0.5f, "WALK"), w2 = new(arena.Stand + new Vector2(-6, -4), 0.5f, "RUN");
        lock (world.Sync)
        {
            guard = SpawnManager.SpawnAt(map, rt, arena.Stand + new Vector2(0, -4), 0, world, "GUARD");
            patrol = SpawnManager.SpawnAt(map, rt, arena.Stand + new Vector2(-6, 0), 0, world, "PATROL", [w1, w2]);
        }
        mark = peer.Events.Count;
        arena.Run(6);
        var legs = Moves(peer, mark, patrol).Where(m => m.Speed > 0).ToList();
        Check(Moves(peer, mark, guard).Count == 0, "a GUARD point's mob sends no event 3 at idle");
        Check(rt.MoveSpeed <= 0 || (legs.Count >= 2 && legs[0].Target == w1.Position && legs[0].Speed == rt.MoveSpeed
              && legs.First(m => m.Target == w2.Position).Speed == rt.AttackMoveSpeed),
              $"a PATROL point's mob walks to waypoint 1 at movespeed, then runs to waypoint 2 at attackmovespeed (got {legs.Count} legs)");

        // Threat: a soldier next to both players. Its target changes only past 1.1 × the current one's threat (in range).
        var soldier = arena.Spawn(Soldier, new Vector2(0, 1.5f));
        arena.Run(Math.Max(0.05, soldier.Template.AggroDelayAfterSpawn + 0.05));
        Check(soldier.AI.State == MobState.Combat && soldier.TargetId != 0, "the soldier aggroes one of the two");
        var (first, second) = soldier.TargetId == me.Id ? (me, other) : (other, me);
        var f = soldier.Template.DamageAggroFactor;
        lock (world.Sync) Combat.Damage(first, soldier, 100, EffectType.Physical, HitOrigin.Direct, -1, world.GameNow);
        lock (world.Sync) Combat.Damage(second, soldier, 105, EffectType.Physical, HitOrigin.Direct, -1, world.GameNow);
        arena.Run(0.05);
        Check(soldier.TargetId == first.Id && soldier.Threat[second] == 105 * f && soldier.Threat[first] == 1 + 100 * f,
              $"threat {105 * f:0.#} against {1 + 100 * f:0.#} (< 1.1 ×): the target stays ({soldier.AI.Describe()})");
        lock (world.Sync) Combat.Damage(second, soldier, 20, EffectType.Physical, HitOrigin.Direct, -1, world.GameNow);
        arena.Run(0.05);
        Check(soldier.TargetId == second.Id, $"threat {125 * f:0.#} against {1 + 100 * f:0.#} (> 1.1 ×): it switches ({soldier.AI.Describe()})");
    }

    private static void PursuitLeash()
    {
        // The no-hit leash out of the way (60 s), so only the distance from home ends the chase (SPEC §5d.4 item 4).
        using var arena0 = new Arena(ai: true, combat: CombatSettings.Default with { MobLeashNoHit = TimeSpan.FromSeconds(60) });
        var world = arena0.World;
        var (_, peer, me) = arena0.Join("Runner", new InventoryItem("T4_MAIN_SWORD"));
        var (arena, _) = Quiet(arena0);
        var soldier = arena.Spawn(Soldier, new Vector2(25, 0));
        var t = soldier.Template;
        var home = soldier.Home;
        arena.Run(Math.Max(1, t.AggroDelayAfterSpawn + 0.5));
        lock (world.Sync) me.Teleport(soldier.Position + new Vector2(-7, 0), 90, DateTime.UtcNow);
        arena.Run(0.05);
        Check(soldier.AI.State == MobState.Combat && t.PursuitRadius > 0, $"it aggroes ({soldier.AI.Describe()})");

        // The player 10 m past the radius, away from home: the mob runs after it and resets the tick it is past pursuitradius.
        lock (world.Sync) me.Teleport(home + new Vector2(-(t.PursuitRadius + 10), 0), 90, DateTime.UtcNow);
        var mark = peer.Events.Count;
        for (var i = 0; i < 600 && soldier.AI.State == MobState.Combat; i++) arena.Run(0.05);
        var far = Vector2.Distance(soldier.Position, home);
        Check(soldier.AI.State == MobState.Returning && soldier.Immune && soldier.AI.Describe().Contains("last reset: pursuit radius")
              && far > t.PursuitRadius && far <= t.PursuitRadius + t.AttackMoveSpeed * 0.05f + 0.01f,
              $"the leash at {far:0.##} m from home (pursuitradius {t.PursuitRadius}): returning, immune ({soldier.AI.Describe()})");
        var combo = Since(peer, mark).Where(e => e.Code == EventCodes.RegenerationHealthEnergyComboChanged && L(e.P, 0) == soldier.Id).Select(e => e.P).ToList();
        Check(combo.Count == 1 && F(combo[0], 5) == 699, $"the reset's combo {{5: 699}} (got {Show(combo.Select(c => (EventCodes.RegenerationHealthEnergyComboChanged, c)))})");

        // .mob ai off while it runs home: it stops where it is, refilled, not immune, idle, with the clear, and stays there.
        arena.Run(0.5);
        mark = peer.Events.Count;
        lock (world.Sync) world.MobAi = false;
        var settled = soldier.Position;
        var clear = Since(peer, mark).Where(e => e.Code == EventCodes.ActiveSpellEffectsUpdate && L(e.P, 0) == soldier.Id).Select(e => e.P).ToList();
        arena.Run(1);
        Check(soldier.AI.State == MobState.Idle && !soldier.Immune && !soldier.Motion.Moving && soldier.Position == settled && Vector2.Distance(settled, home) > 1
              && soldier.Health.At(world.GameNow) == soldier.Health.Max && clear.Count == 1 && clear[0][9] is byte[] and [0]
              && !Since(peer, mark).Any(e => e.Code == EventCodes.Move && L(e.P, 0) == soldier.Id && Sample(e.P).Speed > 0),
              $"AI off: idle where it stood, full bars, not immune, the clear, no more walking ({soldier.AI.Describe()}, {clear.Count} clears)");
    }

    private static void RangedAndFast()
    {
        using var arena0 = new Arena(ai: true);
        var world = arena0.World;
        var (_, peer, me) = arena0.Join("Target", new InventoryItem("T4_MAIN_SWORD"));
        var (arena, map) = Quiet(arena0);

        // A mage 6 m away, hit: it swings at once in reach (magic, range 9): Attack {3 4, 4 = 1 + 0.417 s, 5 = 4 + the flight
        // at 20 m/s}, the HealthUpdate {1 = k4, 2 -round(damage × R magic), 4 2, 5 1, 6 mage} at the impact; 2 s apart.
        var mage = arena.Spawn("T4_MOB_ROAMING_UNDEAD_MAGE", new Vector2(6, 0));
        var t = mage.Template;
        var mark = peer.Events.Count;
        var entry = world.GameNow;
        lock (world.Sync) Combat.Damage(me, mage, 1, EffectType.Physical, HitOrigin.Direct, -1, world.GameNow);
        arena.Run(4.5);
        var events = Since(peer, mark);
        var attacks = events.Where(e => e.Code == EventCodes.Attack && L(e.P, 0) == mage.Id).Select(e => e.P).ToList();
        var flight = SpellCasts.Seconds(Vector2.Distance(mage.Position, me.PositionAt(DateTime.UtcNow)) / world.Combat.AutoAttackProjectileSpeed);
        var first = entry + SpellCasts.Seconds(1) + SpellCasts.Seconds(t.MeleeAttackDamageTime);
        Check(t is { AttackType: "magic", AttackSpeed: 0.5f } && attacks.Count == 2 && L(attacks[0], 1) >= first && L(attacks[0], 1) < first + SpellCasts.Seconds(0.05)
              && L(attacks[1], 1) - L(attacks[0], 1) == SpellCasts.Seconds(2)
              && attacks.All(a => Convert.ToByte(a[3]) == 4 && L(a, 4) - L(a, 1) == SpellCasts.Seconds(t.MeleeAttackDamageTime) && Math.Abs(L(a, 5) - L(a, 4) - flight) <= 1),
              $"mage ({t.AttackType}, speed {t.AttackSpeed}, windup {t.MeleeAttackDamageTime}): two magic Attacks 2 s apart, k5 − k4 = the flight (got {Show(attacks.Select(a => (EventCodes.Attack, a)))})");
        var hits = events.Where(e => e.Code == EventCodes.HealthUpdate && L(e.P, 0) == me.Id).Select(e => e.P).ToList();
        var dmg = Combat.Rounded(t.AttackDamage * Combat.ResistanceOf(me, EffectType.Magic));
        Check(hits.Count == 2 && hits.Select(h => L(h, 1)).SequenceEqual(attacks.Select(a => L(a, 4)))
              && hits.All(h => F(h, 2) == -dmg && Convert.ToByte(h[4]) == 2 && Convert.ToByte(h[5]) == 1 && L(h, 6) == mage.Id),
              $"each magic hit: HealthUpdate {{1 = k4, 2: -{dmg}, 4: 2, 5: 1, 6 mage}} (got {Show(hits.Select(h => (EventCodes.HealthUpdate, h)))})");

        // .mob ai off mid-fight: no threat, no target, the player out of combat at once, no more swings.
        lock (world.Sync) world.MobAi = false;
        mark = peer.Events.Count;
        arena.Run(2.5);
        Check(mage.AI.State == MobState.Idle && mage.Threat.Count == 0 && mage.TargetId == 0 && !me.Combat.InCombat
              && !Since(peer, mark).Any(e => e.Code == EventCodes.Attack),
              $"AI off mid-fight: idle, no threat or target, the player out of combat, no Attack ({mage.AI.Describe()})");

        // A ghoul (attackspeed 1.1, windup 0.35): each swing 1 / 1.1 s after the last one's start, on the next 50 ms tick.
        lock (world.Sync)
        {
            SpawnManager.Despawn(mage, world, respawn: false);
            world.MobAi = true;
        }
        var ghoul = arena.Spawn("T4_MOB_ROAMING_UNDEAD_GHOUL", new Vector2(1.5f, 0));
        var g = ghoul.Template;
        mark = peer.Events.Count;
        lock (world.Sync) Combat.Damage(me, ghoul, 1, EffectType.Physical, HitOrigin.Direct, -1, world.GameNow);
        arena.Run(3.5);
        var swings = Since(peer, mark).Where(e => e.Code == EventCodes.Attack && L(e.P, 0) == ghoul.Id).Select(e => e.P).ToList();
        var gaps = swings.Zip(swings.Skip(1)).Select(p => L(p.Second, 1) - L(p.First, 1)).ToList();
        Check(g.AttackSpeed == 1.1f && swings.Count >= 3 && gaps.All(d => d >= SpellCasts.Seconds(1 / g.AttackSpeed) && d <= SpellCasts.Seconds(1.0))
              && swings.All(a => Convert.ToByte(a[3]) == 1 && L(a, 4) - L(a, 1) == SpellCasts.Seconds(g.MeleeAttackDamageTime) && L(a, 5) == L(a, 4)),
              $"ghoul: Attacks 0.91-1.0 s apart, k4 − k1 = {g.MeleeAttackDamageTime} s (got {swings.Count}, gaps {string.Join(", ", gaps.Select(d => d / 10000))} ms)");
        _ = map;
    }

    /// <summary>
    /// StageN mobnav: a hand-made grid on 0204 with a wall 13 m long across the line between a mage and the player (7 m apart, inside
    /// its range 9): it does not aggro through it; hit, it walks round the wall's end (never into a tile-blocked cell) and its first
    /// Attack comes only once it sees the player.
    /// </summary>
    private static void AroundWall()
    {
        using var arena0 = new Arena(ai: true);
        var world = arena0.World;
        var (_, peer, me) = arena0.Join("Target", new InventoryItem("T4_MAIN_SWORD"));
        var (arena, map) = Quiet(arena0);
        var s = arena.Stand;
        int sx = (int)Math.Floor(s.X), sz = (int)Math.Floor(s.Y);
        var grid = WalkGrid.FromCells(sx - 60, sz - 60, sx + 60, sz + 60, Enumerable.Range(sz - 6, 13).Select(z => (sx + 10, z)), [s]);
        MobNav.Use(map, grid);
        try
        {
            var mage = arena.Spawn("T4_MOB_ROAMING_UNDEAD_MAGE", new Vector2(14.5f, 0.5f));
            lock (world.Sync) me.Teleport(s + new Vector2(7.5f, 0.5f), 90, DateTime.UtcNow);
            arena.Run(Math.Max(1, mage.Template.AggroDelayAfterSpawn + 0.5));
            Check(mage.AI.State == MobState.Idle, $"no aggro through the wall (aggroradius {mage.Template.AggroRadius}, {mage.AI.Describe()})");

            var mark = peer.Events.Count;
            lock (world.Sync) Combat.Damage(me, mage, 1, EffectType.Physical, HitOrigin.Direct, -1, world.GameNow);
            var blocked = 0;
            var blind = 0;
            var maxOff = 0f;
            var seen = 0;
            for (var i = 0; i < 100 && seen == 0; i++)
            {
                var before = peer.Events.Count;
                arena.Run(0.05);
                var here = mage.Position;
                if (!grid.Open(here)) blocked++;
                maxOff = Math.Max(maxOff, Math.Abs(here.Y - s.Y));
                if (Since(peer, before).Any(e => e.Code == EventCodes.Attack && L(e.P, 0) == mage.Id))
                {
                    seen++;
                    if (!grid.Sight(here, me.PositionAt(DateTime.UtcNow))) blind++;
                }
            }
            Check(mage.AI.State == MobState.Combat && seen == 1 && blind == 0 && blocked == 0 && maxOff > 6,
                  $"hit: it walks round the wall's end (max {maxOff:0.#} m off the line, {blocked} ticks in a blocked cell) and attacks only in sight "
                  + $"(attacks {seen}, blind {blind}; {mage.AI.Describe()})");
        }
        finally { MobNav.Use(map, null); }
    }

    private static void Check(bool cond, string what) => AlbionTests.Check(cond, what);
}
