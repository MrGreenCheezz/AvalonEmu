using System.Numerics;
using System.Xml.Linq;
using AlbionLocal.Server;
using AlbionLocal.Server.AI;
using AlbionLocal.Server.Data;
using AlbionLocal.Server.Entities;
using AlbionLocal.Server.Game;
using AlbionLocal.Server.Protocol;
using AlbionLocal.Server.Systems;
using static AlbionLocal.SelfTest.AlbionTests;
using static AlbionLocal.SelfTest.CombatTest;

namespace AlbionLocal.SelfTest;

/// <summary>
/// StageD mob spells (analysis/work/stageD/mobspells/SPEC.md §7): captured mob types in 0204 on a <see cref="ManualClock"/> with
/// FakePeers, the stand cleared of other mobs, <see cref="World.MobAiChance"/> 1 where a chance would only slow the test. The
/// ghoul's bite, the wolf's howl, the soldier's frenzy channel, the mage's FREEZE → AOE_SLOW chain, the miniboss's order rules
/// and counters, a reset during a cast, the interpreter units and .mob spells off. Light: the numbers the SPEC lists.
/// </summary>
public static class MobSpellTest
{
    public static IEnumerable<(string, Action)> All =>
    [
        ("mob spells: heretic champion crush hits its animated cone, misses sidesteps and rear, and stops hitting when the cone closes", HereticCrush),
        ("mob spells: tutorial ballista nested projectile hits inside its path, misses outside and expires; mage ground damage", TutorialProjectiles),
        ("mob spells: tutorial ballista AI casts and damages a stationary player using its inherited behaviour", TutorialBallistaAi),
        ("mob spells: tutorial mortar detects within 5 m, stays idle outside, and retaliates with its 30 m ground spell", TutorialMortarRanges),
        ("spell area geometry: nested transforms, compact lifetime curves, animated rectangles and conservative reach", NestedGeometry),
        ("mob spells: ghoul bite below 70 % (CastStart/EnergyUpdate/CastFinished/CastSpell keys, CastHits then HealthUpdates, stand 1.3 s, recast 7 s)", Ghoul),
        ("mob spells: wolf HOWL in the entry tick (1.5 s, slot 1, energy -19), origin-3 damage within 7 m only", Wolf),
        ("mob spells: soldier frenzy after 3 s (ground cast, channel ticks with CastHits, the arc area, pulses in front only, ChannelingEnded and Leave, next Attack +2.5 s)", Soldier),
        ("mob spells: mage FREEZE (projectile hit, magic damage, self CastHit) then AOE_SLOW within 100 ms (areas 1700/1900, the burst at +1.7 s), cd_freeze 10 s", Mage),
        ("mob spells: miniboss order (PULSE at entry, HEROIC_STACK every 10 s, cdtnc lockouts, recast delays, no cast in a windup, slots 0-3), counters wrap 255 → 1", Miniboss),
        ("mob spells: a reset during a cast (CastCancel without key 1, fight clock and variables reset) and during the areas (they leave)", Reset),
        ("mob spells: a player's HOWL_EFFECT damage (interruptscasting) cancels the mage's FREEZE under way (CastCancel {1 true})", Interrupted),
        ("mob spells: interpreter units (references, all <AI> compile, slots, SelectRandom, If/SelectFirst/CompoundAction, variables, IfChance cadence, case)", Interpreter),
        ("mob spells: .mob spells off fights as in 5d (no CastStart)", Off),
        ("mob spells (stageN spells-bosses): a spawn spell (MOB_IS_BOSS), a death spell's area (DEMON_SOLDIER_DEATH_AOE), a mob knockback on a player, a <Movement> reposition", BossMechanics),
        ("mob spells (stageN spells2): IfTargetTookDamage, the dragon's threat areas, RandomGroundTarget, an alarm's changeaggro, an onDeath spell, a tracking mob's escape", Spells2),
    ];

    private static void Check(bool cond, string what) => AlbionTests.Check(cond, what);

    private static void HereticCrush()
    {
        using var arena0 = new Arena(spells: true);
        var (_, peer, side) = arena0.Join("CrushSidestep", new InventoryItem("T2_MAIN_SWORD"));
        var (_, _, front) = arena0.Join("CrushFront", new InventoryItem("T2_MAIN_SWORD"));
        var (_, _, rear) = arena0.Join("CrushRear", new InventoryItem("T2_MAIN_SWORD"));
        var (_, _, late) = arena0.Join("CrushLate", new InventoryItem("T2_MAIN_SWORD"));
        var (arena, map) = Quiet(arena0);
        var mob = arena.Spawn("T3_MOB_ROAMING_HERETIC_TANK_CHAMPION", new Vector2(0, -3));
        var spell = T("HERETIC_TANK_CRUSH");
        var start = arena.World.GameNow;
        var aim = mob.Position + new Vector2(0, 3);
        MobCasts.Start(arena.World, map, new MobCast
        {
            Caster = mob, Spell = spell, Counter = 1, Point = aim,
            Start = start, End = start + SpellCasts.Seconds(spell.CastingTime), Ap = 10,
        });
        // Dodge after the cast has selected its ground aim, while remaining inside its radius.
        side.Teleport(mob.Position + new Vector2(3, 0), 0, DateTime.UtcNow);
        front.Teleport(aim, 0, DateTime.UtcNow);
        rear.Teleport(mob.Position - new Vector2(0, 3), 0, DateTime.UtcNow);
        late.Teleport(mob.Position + new Vector2(10, 10), 0, DateTime.UtcNow);
        var sideHealth = side.Health.At(start);
        var rearHealth = rear.Health.At(start);
        var frontHealth = front.Health.At(start);
        arena.Run(spell.CastingTime + 0.05);
        var now = arena.World.GameNow;
        Check(front.Health.At(now) < frontHealth && SpellEffects.Stunned(front, now),
            "the charged crush damages and stuns a player still in front");
        Check(side.Health.At(now) >= sideHealth && !SpellEffects.Stunned(side, now)
            && rear.Health.At(now) >= rearHealth && !SpellEffects.Stunned(rear, now),
            "sidestepping and standing behind avoid both the damage and stun within five metres");
        var area = map.Objects.Values.OfType<MobArea>().Single(a => a.Holder == spell);
        var arc = area.Shapes.Single(n => n.Kind == "arc");
        Check(arc.AnimatedOpening!.At(0) == 60 && arc.AnimatedOpening.At(0.11) == 0,
            $"the installed crush cone closes at 110 ms rather than defaulting to 360 degrees (start {arc.AnimatedOpening.At(0)}, end {arc.AnimatedOpening.At(0.11)})");
        Check(!SpellAreas.Inside(area.Shapes, area.Reach, area.Origin, area.Yaw, area.Start,
            aim, 0.3f, area.Start + SpellCasts.Seconds(0.11), zeroOff: true),
            "a closed cone no longer hits even on its forward axis");
        arena.Run(0.2);
        var lateHealth = late.Health.At(arena.World.GameNow);
        late.Teleport(aim, 0, DateTime.UtcNow);
        arena.Run(0.1);
        Check(late.Health.At(arena.World.GameNow) >= lateHealth && !SpellEffects.Stunned(late, arena.World.GameNow),
            "entering the lingering VFX after the cone closes does not apply a late stun");
    }

    private static void NestedGeometry()
    {
        var nodes = SpellAreas.Nodes(T("MOB_HERETIC_BALLISTA_LINE").Effects.Single()).ToList();
        var shape = nodes.Single();
        var (center, yaw) = shape.Frame(Vector2.Zero, 90, 1);
        var expected = SpellMovement.Facing(121) * 1.6f + SpellMovement.Facing(87.5f) * 19.5f;
        Check(Vector2.Distance(center, expected) < 0.001f && Math.Abs(yaw - 87.5) < 0.001,
            "each nested offset uses the cumulative parent rotation");
        Check(shape.Distance.At(0) == 1 && shape.Distance.At(1) == 19.5 && shape.Distance.At(2) == 38,
            "compact 1-38 distance spans the two-second flight");
        Check(SpellAreas.Curve.Of("AS0:0;1.7:4", 10).At(1.7) == 4
            && SpellAreas.Curve.Of("-2--4", 2).At(1) == -3
            && SpellAreas.Curve.Of("1e-2", 2).At(1) == 0.01,
            "explicit second keys, negative endpoints and scientific constants retain their meaning");
        var rectangle = XElement.Parse("<spelleffectarea time='2'><area><shape><node distance='3'><rectangle width='0-4' height='2'/></node></shape></area></spelleffectarea>");
        var rect = SpellAreas.Nodes(rectangle).ToList();
        Check(!SpellAreas.Inside(rect, rect[0].Reach, Vector2.Zero, 0, 0, new Vector2(0, 3), 0.3f, 0, zeroOff: true)
            && SpellAreas.Inside(rect, rect[0].Reach, Vector2.Zero, 0, 0, new Vector2(1.5f, 3), 0, SpellCasts.Seconds(2), zeroOff: true),
            "a zero-width rectangle is inactive and its width grows over the area lifetime");
        Check(Math.Abs(rect[0].Reach - (3 + Math.Sqrt(20) / 2)) < 0.001,
            "reach includes the offset and maximum rectangle dimensions");
    }

    private static void TutorialBallistaAi()
    {
        using var arena0 = new Arena(ai: true, spells: true);
        var (_, peer, me) = arena0.Join("BallistaAiTarget", new InventoryItem("T2_MAIN_SWORD"));
        var (arena, _) = Quiet(arena0);
        var mob = arena.Spawn("T2_MOB_HERETIC_BALLISTA_TUTORIAL", new Vector2(0, -8));
        Check(mob.AI is BehaviourAI && new[] { "1009", "1010" }.Any(id => Store.Get(id).MobSpawnPoints.Any(p => p.Mob == mob.Template.Name)),
            "the tutorial places this ballista and it inherits the reference AI");
        var events = new List<Timed>();
        Engage(arena, peer, mob, [me], events);
        events.AddRange(Record(arena, peer, 4, [me]));
        Check(events.Any(e => e.Code == EventCodes.CastStart && Is(e.P, mob.Id) && Convert.ToInt16(e.P[5]) == T("MOB_HERETIC_BALLISTA_LINE").Id),
            "the ballista AI starts its line shot");
        Check(events.Any(e => e.Code == EventCodes.HealthUpdate && Is(e.P, me.Id)
            && Convert.ToInt16(e.P[7]) == T("MOB_HERETIC_BALLISTA_KNOCKBACK_EFFECT").Id && F(e.P, 2) < 0),
            "the AI's shot damages the stationary player");
    }

    private static void TutorialMortarRanges()
    {
        foreach (var distance in new[] { 4.9f, 5.1f, 20f })
        {
            using var arena0 = new Arena(ai: true, spells: true);
            var (_, peer, me) = arena0.Join("MortarRangeTarget", new InventoryItem("T2_MAIN_SWORD"));
            var (arena, _) = Quiet(arena0);
            var mob = arena.Spawn("T2_MOB_HERETIC_MORTAR", new Vector2(0, -distance));
            var spellIds = new[] { "A", "B", "C", "D", "E" }.Select(s => T("MOB_HERETIC_MORTAR_CONE_" + s)).ToList();
            Check(mob.Template is { AggroRadius: 5, AggroConeLength: 0, MoveSpeed: 0, AttackMoveSpeed: 0 }
                  && mob.AI is BehaviourAI && spellIds.All(s => s.CastRange == 30),
                "installed mortar has separate detection and ground-cast ranges, no frontal extension or movement");
            var events = Record(arena, peer, 8, [me]); // past the five-second spawn delay
            bool Shot(IEnumerable<Timed> list) => list.Any(e => e.Code == EventCodes.CastStart && Is(e.P, mob.Id)
                && spellIds.Any(s => s.Id == Convert.ToInt16(e.P[5])));
            if (distance < 5)
                Check(mob.AI.State == MobState.Combat && Shot(events), "mortar detects and fires at 4.9 m without being hit");
            else
            {
                Check(mob.AI.State == MobState.Idle && !Shot(events), $"mortar stays idle at {distance} m without a threat");
                lock (arena.World.Sync) Combat.Damage(me, mob, 1, EffectType.Physical, HitOrigin.Direct, -1, arena.World.GameNow);
                events = Record(arena, peer, 4, [me]);
                Check(mob.AI.State == MobState.Combat && Shot(events), $"first damage starts retaliation at {distance} m, beyond detection range");
            }
        }
    }

    private static void TutorialProjectiles()
    {
        using var arena0 = new Arena(spells: true);
        var (_, peer, me) = arena0.Join("BallistaTarget", new InventoryItem("T2_MAIN_SWORD"));
        var (arena, map) = Quiet(arena0);
        var world = arena.World;
        var mob = arena.Spawn("T2_MOB_HERETIC_BALLISTA_TUTORIAL", new Vector2(0, -8));
        var spell = T("MOB_HERETIC_BALLISTA_LINE");
        var effect = T("MOB_HERETIC_BALLISTA_KNOCKBACK_EFFECT");
        var node = spell.Effects.Single(e => e.Name.LocalName == "spelleffectarea");
        var shapes = SpellAreas.Nodes(node).ToList();
        Check(shapes.Count == 1 && shapes[0].Kind == "circle" && shapes[0].Reach > 38,
            "the nested ballista projectile has a circle and its full travel reach");
        var start = world.GameNow;
        Check(!SpellAreas.Inside(shapes, shapes[0].Reach, Vector2.Zero, 0, start, new Vector2(0, 10), 0.33f, start),
            "the projectile does not hit ten metres ahead before travelling there");

        var mark = peer.Events.Count;
        lock (world.Sync)
            MobCasts.Start(world, map, new MobCast { Caster = mob, Spell = spell, Point = arena.Stand, Start = start,
                End = start + SpellCasts.Seconds(spell.CastingTime), Ap = mob.Template.AbilityPower, Counter = 1 });
        arena.Run(0.75);
        Check(!Since(peer, mark).Any(e => e.Code == EventCodes.HealthUpdate && Convert.ToInt16(e.P[7]) == effect.Id),
            "no damage while the projectile has not reached the player");
        arena.Run(1.7);
        var hits = Since(peer, mark).Where(e => e.Code == EventCodes.HealthUpdate && Is(e.P, me.Id) && Convert.ToInt16(e.P[7]) == effect.Id).ToList();
        Check(hits.Count == 1 && F(hits[0].P, 2) < 0, "the moving ballista projectile deals damage exactly once");
        Check(!map.Objects.Values.OfType<MobArea>().Any(a => a.Owner == mob), "the first collision removes the ballista projectile");

        // A perpendicular shot must not hit the same player, and must leave when its flight ends.
        mark = peer.Events.Count;
        start = world.GameNow;
        lock (world.Sync)
            MobCasts.Execute(new MobCasts.MobHit(mob, map, spell, 2, mob.Template.AbilityPower, null,
                mob.Position + new Vector2(20, 0), start), spell);
        arena.Run(2.2);
        Check(!Since(peer, mark).Any(e => e.Code == EventCodes.HealthUpdate && Convert.ToInt16(e.P[7]) == effect.Id)
            && !map.Objects.Values.OfType<MobArea>().Any(a => a.Owner == mob), "a missed projectile deals no damage and expires");

        var mage = arena.Spawn("T2_MOB_HERETIC_MAGE", new Vector2(0, -5));
        var fire = T("HERETIC_MAGE_AOE");
        start = world.GameNow;
        var point = me.PositionAt(DateTime.UtcNow);
        mark = peer.Events.Count;
        lock (world.Sync)
            MobCasts.Start(world, map, new MobCast { Caster = mage, Spell = fire, Point = point, Start = start,
                End = start + SpellCasts.Seconds(fire.CastingTime), Ap = mage.Template.AbilityPower, Counter = 1 });
        arena.Run(1.9);
        Check(!Since(peer, mark).Any(e => e.Code == EventCodes.HealthUpdate && Convert.ToInt16(e.P[7]) == fire.Id), "mage damage waits for its cast");
        arena.Run(0.2);
        Check(Since(peer, mark).Any(e => e.Code == EventCodes.HealthUpdate && Is(e.P, me.Id) && Convert.ToInt16(e.P[7]) == fire.Id && F(e.P, 2) < 0),
            "a player remaining in the mage's ground circle takes damage");
    }

    private static ClusterStore Store => Client.Clusters!;

    private static SpellTemplate T(string name) => Client.Spells!.Template(name) ?? throw new Exception($"{name} not in spells.xml");

    private static bool Is(Dictionary<byte, object?> p, long id) => p.TryGetValue(0, out var v) && v is IConvertible && Convert.ToInt64(v) == id;

    private static (Arena Arena, Map Map) Quiet(Arena arena)
    {
        var map = arena.World.Maps.Find("0204")!;
        // Spell timing and hit tests use open ground, not a wall grid that may load mid-fight.
        var x = (int)Math.Floor(arena.Stand.X); var z = (int)Math.Floor(arena.Stand.Y);
        MobNav.Use(map, WalkGrid.FromCells(x - 180, z - 180, x + 180, z + 180, [], [arena.Stand]));
        lock (arena.World.Sync)
            foreach (var m in map.Objects.Values.OfType<Mob>().Where(m => Vector2.Distance(m.Position, arena.Stand) < 150).ToList())
                SpawnManager.Despawn(m, arena.World, respawn: false);
        return (arena, map);
    }

    /// <summary>An event with the GameTimeStamp of the world tick that sent it.</summary>
    private sealed record Timed(long At, EventCodes Code, Dictionary<byte, object?> P);

    /// <summary>
    /// <paramref name="seconds"/> of world ticks (or until <paramref name="until"/>), the players of <paramref name="full"/> refilled
    /// every tick (no knockdown spoils a fight); the events of <paramref name="peer"/> tagged with their tick.
    /// </summary>
    private static List<Timed> Record(Arena arena, FakePeer peer, double seconds, Player[] full, Func<bool>? until = null)
    {
        var world = arena.World;
        var list = new List<Timed>();
        for (var i = 0; i < Math.Round(seconds * 20); i++)
        {
            var mark = peer.Events.Count;
            arena.Run(0.05);
            var now = world.GameNow;
            list.AddRange(Since(peer, mark).Select(e => new Timed(now, e.Code, e.P)));
            lock (world.Sync)
                foreach (var p in full) p.Health.Set(p.Health.Max, Math.Max(now, p.Health.Stamp));
            if (until?.Invoke() == true) break;
        }
        return list;
    }

    /// <summary>World ticks until <paramref name="mob"/> fights (past its aggrodelayafterspawn), at most 10 s; the entry tick's GameTimeStamp.</summary>
    private static long Engage(Arena arena, FakePeer peer, Mob mob, Player[] full, List<Timed>? into = null)
    {
        var events = Record(arena, peer, 10, full, () => mob.AI.State == MobState.Combat);
        into?.AddRange(events);
        Check(mob.AI.State == MobState.Combat, $"{mob} fights ({mob.AI.Describe()})");
        return arena.World.GameNow;
    }

    private static string Show(IEnumerable<Timed> events) => CombatTest.Show(events.Select(e => (e.Code, e.P)));

    private static Vector2 V(Dictionary<byte, object?> p, byte k) => p[k] is float[] { Length: 2 } v ? new Vector2(v[0], v[1]) : (Vector2)p[k]!;

    private static long Ms(double ms) => SpellCasts.Seconds(ms / 1000);

    private static void Ghoul()
    {
        using var arena0 = new Arena(ai: true, spells: true);
        var world = arena0.World;
        var (_, peer, me) = arena0.Join("Bitten", new InventoryItem("T4_MAIN_SWORD"));
        var (arena, _) = Quiet(arena0);
        var ghoul = arena.Spawn("T4_MOB_ROAMING_UNDEAD_GHOUL", new Vector2(1.5f, 0));
        var bite = T("MOB_UNDEAD_GHOUL_BITE");
        Check(ghoul.Template is { Id: 1888, AbilityPower: 93, EnergyMax: 139 } && ghoul.AI is BehaviourAI && bite is { HitDelay: 0.52f, StandTime: 1.3f, RecastDelay: 7 },
              $"ghoul 1888 (AP 93, energy 139) runs its <AI>; bite hitdelay 0.52, standtime 1.3, recast 7 ({ghoul.AI.Describe()})");
        world.MobAiChance = 1;
        Engage(arena, peer, ghoul, [me]);
        var events = Record(arena, peer, 4, [me]);
        Check(ghoul.AI.State == MobState.Combat && !events.Any(e => e.Code == EventCodes.CastStart && Is(e.P, ghoul.Id)), "no bite while its health is ≥ 70 %");

        lock (world.Sync) ghoul.Health.Set(0.6f * ghoul.Health.Max, world.GameNow);
        events = Record(arena, peer, 3, [me], () => ghoul.CastCounter > 0);
        events.AddRange(Record(arena, peer, 0.6, [me]));
        var cast = events.Where(e => e.Code is EventCodes.CastStart or EventCodes.EnergyUpdate or EventCodes.CastFinished or EventCodes.CastSpell && Is(e.P, ghoul.Id)).Take(4).ToList();
        Check(cast.Select(e => e.Code).SequenceEqual([EventCodes.CastStart, EventCodes.EnergyUpdate, EventCodes.CastFinished, EventCodes.CastSpell]) && cast.All(e => e.At == cast[0].At),
              $"a bite at 60 %: CastStart, EnergyUpdate, CastFinished, CastSpell in one tick (got {Show(cast)})");
        var (start, energy, finished, spell) = (cast[0].P, cast[1].P, cast[2].P, cast[3].P);
        var end = L(start, 1);
        Check(Convert.ToInt16(start[5]) == bite.Id && L(start, 4) == end && Convert.ToInt16(start[6]) == -1 && L(start, 7) == me.Id && Convert.ToByte(start[8]) == 1 && Convert.ToByte(start[9]) == 0
              && !start.ContainsKey(2) && !spell.ContainsKey(2) && Math.Abs(ghoul.Direction - AlbionLocal.Server.AI.MotionMaster.Facing(ghoul.Position, me.PositionAt(DateTime.UtcNow))) < 1,
              $"CastStart {{5 bite, 4 = 1, 6 -1, 7 player, 8 1, 9 0}}, no key 2 in it or the CastSpell (as captured), the ghoul facing the player (got {Show([cast[0]])})");
        Check(F(energy, 2) == -5 && F(energy, 3) == 134 && Convert.ToByte(energy[4]) == 7 && L(energy, 5) == ghoul.Id && Convert.ToInt32(energy[6]) == bite.Id,
              $"EnergyUpdate {{2 -5, 3 134, 4 7, 5 mob, 6 bite}} (got {Show([cast[1]])})");
        Check(L(finished, 1) == me.Id && Convert.ToInt16(finished[2]) == bite.Id && Convert.ToByte(finished[3]) == 0 && Convert.ToByte(finished[4]) == 1 && !finished.ContainsKey(6),
              $"CastFinished {{1 player, 2 bite, 3 0, 4 1}} without key 6 (got {Show([cast[2]])})");
        Check(L(spell, 1) == me.Id && Convert.ToInt16(spell[3]) == bite.Id && L(spell, 5) - L(spell, 4) == Ms(520) && Convert.ToByte(spell[6]) == 1 && Convert.ToInt16(spell[7]) == 0,
              $"CastSpell {{1 player, 3 bite, 5 − 4 = 520 ms, 6 1, 7 0}} (got {Show([cast[3]])})");

        var hit = events.Where(e => (e.Code == EventCodes.CastHit && L(e.P, 1) == ghoul.Id) || (e.Code == EventCodes.HealthUpdate && Convert.ToInt16(e.P[7]) == bite.Id)).ToList();
        var r = Combat.ResistanceOf(me, EffectType.Physical);
        Check(hit.Count == 4 && hit[0].Code == EventCodes.CastHit && Is(hit[0].P, me.Id) && hit[1].Code == EventCodes.CastHit && Is(hit[1].P, ghoul.Id)
              && hit.Take(2).All(h => Convert.ToInt16(h.P[2]) == bite.Id && Convert.ToByte(h.P[4]) == 1)
              && Is(hit[2].P, me.Id) && F(hit[2].P, 2) == -Combat.Rounded(279 * r) && Convert.ToByte(hit[2].P[4]) == 1 && Convert.ToByte(hit[2].P[5]) == 2 && L(hit[2].P, 6) == ghoul.Id
              && Is(hit[3].P, ghoul.Id) && F(hit[3].P, 2) == 19 && Convert.ToByte(hit[3].P[4]) == 2 && Convert.ToByte(hit[3].P[5]) == 2 && L(hit[2].P, 1) == end + Ms(520),
              $"the hit at +520 ms: CastHit player, CastHit self, HealthUpdate player {{2 -round(279 × {r:0.###}), 4 1, 5 2}}, HealthUpdate self {{2 +19, 4 2, 5 2}} (got {Show(hit)})");

        lock (world.Sync) ghoul.Health.Set(0.6f * ghoul.Health.Max, world.GameNow);
        events.AddRange(Record(arena, peer, 7.5, [me]));
        var swing = events.Where(e => e.Code == EventCodes.Attack && Is(e.P, ghoul.Id) && L(e.P, 1) > end).Select(e => L(e.P, 1)).FirstOrDefault();
        var second = events.Where(e => e.Code == EventCodes.CastStart && Is(e.P, ghoul.Id)).Skip(1).Select(e => L(e.P, 1)).FirstOrDefault();
        Check(swing >= end + Ms(1300) && swing < end + Ms(1350), $"the next Attack 1.3 s after the bite (standtime > 1 / 1.1; got +{(swing - end) / 10000} ms)");
        Check(second >= end + Ms(7000) && second < end + Ms(7600), $"the second bite after its 7 s recast (got +{(second - end) / 10000} ms)");
    }

    private static void Wolf()
    {
        using var arena0 = new Arena(ai: true, spells: true);
        var world = arena0.World;
        var (_, peer, me) = arena0.Join("Near", new InventoryItem("T4_MAIN_SWORD"));
        var (_, farPeer, far) = arena0.Join("Far", new InventoryItem("T4_MAIN_SWORD"));
        var (arena, _) = Quiet(arena0);
        lock (world.Sync) far.Teleport(arena.Stand + new Vector2(11, 0), 90, DateTime.UtcNow);
        var wolf = arena.Spawn("MOB_WOLF", new Vector2(3, 0));
        var howl = T("MOB_HIDE_WOLF_HOWL");
        var script = (wolf.AI as BehaviourAI)?.Script;
        Check(wolf.Template.AbilityPower == 97 && script is { Slots: ["MOB_SPEAK", "MOB_HIDE_WOLF_HOWL"] }, $"wolf AP 97, slots MOB_SPEAK 0, HOWL 1 (got {string.Join(", ", script?.Slots ?? [])})");
        var events = new List<Timed>();
        var entry = Engage(arena, peer, wolf, [me, far], events);
        var farMark = farPeer.Events.Count;
        events.AddRange(Record(arena, peer, 1.7, [me, far]));
        var start = events.FirstOrDefault(e => e.Code == EventCodes.CastStart && Is(e.P, wolf.Id))?.P;
        var finished = events.FirstOrDefault(e => e.Code == EventCodes.CastFinished && Is(e.P, wolf.Id))?.P;
        var energy = events.FirstOrDefault(e => e.Code == EventCodes.EnergyUpdate && Is(e.P, wolf.Id))?.P;
        Check(start is not null && Convert.ToInt16(start[5]) == howl.Id && L(start, 1) == entry && L(start, 4) - L(start, 1) == Ms(1500) && L(start, 7) == wolf.Id,
              $"HOWL in the entry tick: CastStart {{7 wolf, 4 − 1 = 1500 ms}} (got {(start is null ? "none" : Show([new Timed(0, EventCodes.CastStart, start)]))})");
        Check(finished is not null && L(finished, 1) == wolf.Id && Convert.ToByte(finished[3]) == 1 && energy is not null && F(energy, 2) == -19,
              "CastFinished {1 wolf, 3 slot 1}, EnergyUpdate {2 -round(20 × 0.97) = -19}");
        var r = Combat.ResistanceOf(me, EffectType.Physical);
        var hits = events.Where(e => e.Code == EventCodes.HealthUpdate && Is(e.P, me.Id) && Convert.ToInt16(e.P[7]) == howl.Id).ToList();
        Check(hits.Count == 1 && F(hits[0].P, 2) == -Combat.Rounded(400 * 0.97 * r) && Convert.ToByte(hits[0].P[5]) == 3 && Convert.ToByte(hits[0].P[4]) == 1,
              $"the player 3 m away: HealthUpdate {{2 -round(400 × 0.97 × {r:0.###}), 4 1, 5 3}} (got {Show(hits)})");
        Check(!Since(farPeer, farMark).Any(e => e.Code == EventCodes.HealthUpdate && Is(e.P, far.Id)), "the player 8 m away is not hit");
    }

    private static void Soldier()
    {
        using var arena0 = new Arena(ai: true, spells: true);
        var world = arena0.World;
        var (_, peer, me) = arena0.Join("Front", new InventoryItem("T4_MAIN_SWORD"));
        var (_, _, behind) = arena0.Join("Behind", new InventoryItem("T4_MAIN_SWORD"));
        var (arena, _) = Quiet(arena0);
        lock (world.Sync) behind.Teleport(arena.Stand + new Vector2(30, 0), 90, DateTime.UtcNow);
        var soldier = arena.Spawn("T4_MOB_ROAMING_UNDEAD_SOLDIER", new Vector2(3, 0));
        var frenzy = T("MOB_UNDEAD_SOLDIER_CHARGED_FRENZY");
        var channel = Client.Spells!.Template(frenzy.Id + 1)!;
        var effect = T("MOB_UNDEAD_SOLDIER_CHARGED_FRENZY_EFFECT");
        Check(soldier.Template is { Id: 1908, AbilityPower: 80 } && channel.Channel is { Count: 4 }, "soldier 1908 (AP 80); the frenzy's channel has 4 ticks planned");
        world.MobAiChance = 1;
        var entry = Engage(arena, peer, soldier, [me, behind]);
        // The second player behind the soldier, 3 m on the other side (not on its threat list).
        lock (world.Sync) behind.Teleport(soldier.Position + new Vector2(3, 0), 90, DateTime.UtcNow);
        var events = Record(arena, peer, 8, [me, behind]);
        var mine = events.Where(e => Is(e.P, soldier.Id) || (e.Code == EventCodes.HealthUpdate && L(e.P, 6) == soldier.Id) || e.Code is EventCodes.NewSpellEffectArea or EventCodes.Leave).ToList();
        var start = mine.First(e => e.Code == EventCodes.CastStart).P;
        var at = L(start, 1);
        var end = at + Ms(1300);
        Check(at >= entry + SpellCasts.Seconds(3) && at < entry + SpellCasts.Seconds(3.15) && !start.ContainsKey(7) && L(start, 4) == end
              && Vector2.Distance(V(start, 2), me.PositionAt(DateTime.UtcNow)) < 0.01f,
              $"the frenzy once fight ≥ 3 s: CastStart without key 7, key 2 the target's position, end + 1.3 s (got +{(at - entry) / 10000} ms, {Show([new Timed(0, EventCodes.CastStart, start)])})");
        var energy = mine.First(e => e.Code == EventCodes.EnergyUpdate).P;
        var finished = mine.First(e => e.Code == EventCodes.CastFinished).P;
        var cast = mine.First(e => e.Code == EventCodes.CastSpell && Convert.ToInt16(e.P[3]) == frenzy.Id).P;
        Check(F(energy, 2) == -8 && !finished.ContainsKey(1) && !finished.ContainsKey(6) && L(finished, 5) == end && !cast.ContainsKey(1),
              "EnergyUpdate {2 -8}; CastFinished without keys 1 and 6; CastSpell without key 1");
        var counter = Convert.ToByte(start[8]);
        var ticks = mine.Where(e => e.Code == EventCodes.CastSpell && Convert.ToInt16(e.P[3]) == channel.Id).Select(e => e.P).ToList();
        Check(ticks.Select(t => L(t, 4) - end).SequenceEqual([Ms(100), Ms(540), Ms(980)]) && ticks.All(t => L(t, 5) == L(t, 4) && Convert.ToByte(t[6]) == counter),
              $"channel CastSpells at end + 100 / 540 / 980 ms with key 6 = the counter, none at + 1420 (got {string.Join(", ", ticks.Select(t => (L(t, 4) - end) / 10000))} ms)");
        var swings = (T("MOB_UNDEAD_SOLDIER_CHARGED_FRENZY_RIGHTSWING").Id, T("MOB_UNDEAD_SOLDIER_CHARGED_FRENZY_LEFTSWING").Id);
        var self = mine.Where(e => e.Code == EventCodes.CastHits).Select(e => e.P).ToList();
        Check(self.Count == 3 && self.All(h => h[1] is long[] c && c.All(x => x == soldier.Id) && h[2] is short[] { Length: 2 } s && s[0] == channel.Id
              && (s[1] == swings.Item1 || s[1] == swings.Item2) && h[4] is byte[] k && k.SequenceEqual(new[] { counter, (byte)0 })),
              $"each tick CastHits {{1 [mob, mob], 2 [channel, RIGHTSWING or LEFTSWING], 4 [counter, 0]}} (got {Show(mine.Where(e => e.Code == EventCodes.CastHits))})");
        var area = mine.Single(e => e.Code == EventCodes.NewSpellEffectArea && Convert.ToInt16(e.P[4]) == channel.Id).P;
        var facing = MotionMaster.Facing(soldier.Position, V(start, 2));
        Check(Math.Abs(F(area, 3) - facing) < 0.01f && Convert.ToByte(area[5]) == 0 && L(area, 6) == end + Ms(100) && L(area, 7) - L(area, 6) == Ms(1420) && Convert.ToByte(area[8]) == counter,
              $"NewSpellEffectArea {{3 atan2 facing {facing:0.##}, 4 channel, 5 0, 6 end + 100 ms, 7 − 6 = 1420 ms, 8 counter}} (got {Show(mine.Where(e => e.Code == EventCodes.NewSpellEffectArea))})");
        var r = Combat.ResistanceOf(me, EffectType.Physical);
        var hits = mine.Where(e => e.Code == EventCodes.HealthUpdate && Convert.ToInt16(e.P[7]) == effect.Id).ToList();
        Check(hits.All(h => Is(h.P, me.Id)) && hits.Select(h => L(h.P, 1) - L(area, 6)).SequenceEqual([Ms(100), Ms(540), Ms(980)])
              && hits.All(h => F(h.P, 2) == -Combat.Rounded(360 * r) && Convert.ToByte(h.P[4]) == 1 && Convert.ToByte(h.P[5]) == 2),
              $"the player in front: HealthUpdate {{2 -round(360 × {r:0.###}), 4 1, 5 2, 7 FRENZY_EFFECT}} at area start + 100 / 540 / 980 ms; none behind (got {Show(hits)})");
        var ended = mine.Single(e => e.Code == EventCodes.ChannelingEnded);
        var leave = mine.Single(e => e.Code == EventCodes.Leave && L(e.P, 0) == L(area, 0));
        Check(ended.At >= end + Ms(1420) && ended.At < end + Ms(1470) && Convert.ToInt32(ended.P[1]) == 3 && Convert.ToByte(ended.P[2]) == counter && leave.At == ended.At,
              $"ChannelingEnded {{1 3, 2 counter}} in the tick of end + 1420 ms with the area's Leave in the same tick (got at +{(ended.At - end) / 10000} ms, Leave at +{(leave.At - end) / 10000})");
        var next = mine.Where(e => e.Code == EventCodes.Attack && L(e.P, 1) > end).Select(e => L(e.P, 1)).First();
        Check(next >= end + Ms(2500) && next < end + Ms(2550), $"the next Attack 2.5 s after the cast end (got +{(next - end) / 10000} ms)");
    }

    private static void Mage()
    {
        using var arena0 = new Arena(ai: true, spells: true);
        var world = arena0.World;
        var (_, peer, me) = arena0.Join("Frozen", new InventoryItem("T4_MAIN_SWORD"));
        var (arena, _) = Quiet(arena0);
        var mage = arena.Spawn("T4_MOB_ROAMING_UNDEAD_MAGE", new Vector2(4, 0));
        var (freeze, slow, slowEffect) = (T("UNDEAD_MAGE_FREEZE"), T("MOB_UNDEAD_MAGE_AOE_SLOW"), T("MOB_UNDEAD_MAGE_AOE_SLOW_EFFECT"));
        Check(mage.Template is { Id: 2048, AbilityPower: 103 }, "mage 2048 (AP 103)");
        world.MobAiChance = 1;
        var events = new List<Timed>();
        Engage(arena, peer, mage, [me], events);
        events.AddRange(Record(arena, peer, 16, [me]));
        var starts = events.Where(e => e.Code == EventCodes.CastStart && Is(e.P, mage.Id)).ToList();
        var first = starts.First(s => Convert.ToInt16(s.P[5]) == freeze.Id).P;
        var end = L(first, 4);
        Check(L(first, 7) == me.Id && end - L(first, 1) == Ms(1500), $"FREEZE: CastStart {{7 the player}}, end + 1.5 s (got {Show(starts.Take(1))})");
        var energy = events.Where(e => e.Code == EventCodes.EnergyUpdate && Is(e.P, mage.Id)).Select(e => e.P).ToList();
        var cast = events.First(e => e.Code == EventCodes.CastSpell && Is(e.P, mage.Id) && Convert.ToInt16(e.P[3]) == freeze.Id).P;
        var d = Vector2.Distance(mage.Position, me.PositionAt(DateTime.UtcNow));
        var hit = L(cast, 5);
        Check(F(energy[0], 2) == -10 && Math.Abs(hit - (end + SpellCasts.Seconds(0.3 + d / 30))) <= Ms(1),
              $"EnergyUpdate -10, the hit at end + 0.3 + d / 30 (d {d:0.##}; got +{(hit - end) / 10000} ms)");
        var rm = Combat.ResistanceOf(me, EffectType.Magic);
        var damage = events.Where(e => e.Code == EventCodes.HealthUpdate && Is(e.P, me.Id) && Convert.ToInt16(e.P[7]) == freeze.Id).ToList();
        Check(damage.Count == 1 && L(damage[0].P, 1) == hit && F(damage[0].P, 2) == -Combat.Rounded(175.1 * rm) && Convert.ToByte(damage[0].P[4]) == 2 && Convert.ToByte(damage[0].P[5]) == 2
              && events.Any(e => e.Code == EventCodes.CastHit && Is(e.P, mage.Id) && L(e.P, 1) == mage.Id && Convert.ToInt16(e.P[2]) == freeze.Id),
              $"HealthUpdate {{2 -round(175.1 × {rm:0.###}), 4 2, 5 2, 7 FREEZE}} at the hit and a CastHit on the mage itself (got {Show(damage)})");
        var aoe = starts.First(s => Convert.ToInt16(s.P[5]) == slow.Id).P;
        var finished = events.First(e => e.Code == EventCodes.CastFinished && Is(e.P, mage.Id) && Convert.ToInt16(e.P[2]) == slow.Id).P;
        var areas = events.Where(e => e.Code == EventCodes.NewSpellEffectArea && Convert.ToInt16(e.P[4]) == slow.Id).Select(e => e.P).ToList();
        Check(L(aoe, 1) > hit && L(aoe, 1) - hit < Ms(150) && Convert.ToByte(finished[3]) == 1 && F(energy[1], 2) == -52 && areas.Count == 2
              && Convert.ToByte(areas[0][5]) == 0 && L(areas[0], 7) - L(areas[0], 6) == Ms(1700) && Convert.ToByte(areas[1][5]) == 1 && L(areas[1], 7) - L(areas[1], 6) == Ms(1900),
              $"AOE_SLOW {(L(aoe, 1) - hit) / 10000} ms after the hit: CastFinished {{3 1}}, EnergyUpdate -52, NewSpellEffectArea {{5 0, 1700 ms}} and {{5 1, 1900 ms}} (got {Show(events.Where(e => e.Code == EventCodes.NewSpellEffectArea))})");
        var burst = events.Where(e => e.Code == EventCodes.HealthUpdate && Is(e.P, me.Id) && Convert.ToInt16(e.P[7]) == slowEffect.Id).ToList();
        Check(burst.Count == 1 && L(burst[0].P, 1) == L(areas[1], 6) + Ms(1700) && F(burst[0].P, 2) == -Combat.Rounded(679.8 * rm),
              $"the burst at area start + 1.7 s, once: HealthUpdate {{2 -round(679.8 × {rm:0.###}), 7 AOE_SLOW_EFFECT}} (got {Show(burst)} at +{(burst.Count > 0 ? L(burst[0].P, 1) - L(areas[1], 6) : 0) / 10000} ms)");
        foreach (var a in areas)
        {
            var left = events.SingleOrDefault(e => e.Code == EventCodes.Leave && L(e.P, 0) == L(a, 0));
            Check(left is not null && left.At >= L(a, 7) && left.At < L(a, 7) + Ms(50), $"area [{a[5]}] leaves at its end (got {(left is null ? "none" : $"+{(left.At - L(a, 7)) / 10000} ms")})");
        }
        var again = starts.Where(s => Convert.ToInt16(s.P[5]) == freeze.Id).Skip(1).Select(s => L(s.P, 1)).FirstOrDefault();
        Check(again >= end + SpellCasts.Seconds(10), $"no FREEZE again while cd_freeze is true (10 s after the end), even with chance 1 (got +{(again - end) / 10000} ms)");
    }

    private static void Miniboss()
    {
        using var arena0 = new Arena(ai: true, spells: true);
        var world = arena0.World;
        var (_, peer, me) = arena0.Join("Hero", new InventoryItem("T4_MAIN_SWORD"));
        var (arena, _) = Quiet(arena0);
        // The player cannot be hit (CHEAT_GOD's invincibility): only the order of the mob's casts is looked at.
        var god = T("CHEAT_GOD");
        lock (world.Sync)
            SpellEffects.Apply(world, me, god, me.Id, 100, 100, world.GameNow, [SpellEffects.Make(world, me, 100, 100, god.Effects[0], 0, me, world.GameNow)]);
        var boss = arena.Spawn("T4_MOB_ROAMING_UNDEAD_SOLDIER_MINIBOSS", new Vector2(3, 0));
        var script = ((BehaviourAI)boss.AI).Script;
        var names = new[] { "MOB_UNDEAD_HEROIC_STACK_PULSE", "MOB_UNDEAD_HERO_BURNAURA", "MOB_UNDEAD_SOLDIER_SPLITTINGSLASH", "MOB_UNDEAD_HERO_CLEAVE_SOLO" };
        Check(script.Slots.SequenceEqual(names), $"slots PULSE 0, BURNAURA 1, SPLIT 2, CLEAVE 3 (got {string.Join(", ", script.Slots)})");
        typeof(Mob).GetProperty(nameof(Mob.CastCounter))!.SetValue(boss, (byte)254);
        lock (world.Sync) boss.Health.Set(0.94f * boss.Health.Max, world.GameNow); // SPLIT is ready at once (hp ≤ 0.95)
        var events = new List<Timed>();
        var entry = Engage(arena, peer, boss, [], events);
        events.AddRange(Record(arena, peer, 40, []));
        var spells = names.Select(T).ToArray();
        var cdtnc = new Dictionary<short, double> { [spells[0].Id] = 0, [spells[1].Id] = 7, [spells[2].Id] = 2, [spells[3].Id] = 2 };
        var casts = events.Where(e => e.Code == EventCodes.CastStart && Is(e.P, boss.Id)).Select(e => (Spell: Convert.ToInt16(e.P[5]), Start: L(e.P, 1), End: L(e.P, 4), Counter: Convert.ToByte(e.P[8]), Target: e.P.TryGetValue(7, out var t) ? Convert.ToInt64(t) : 0)).ToList();
        string Seq() => string.Join(", ", casts.Select(c => $"{spells.First(s => s.Id == c.Spell).Name[10..]} +{(c.Start - entry) / 10000}"));
        Check(casts.Count >= 5 && casts[0].Spell == spells[0].Id && casts[0].Start == entry && casts[0].End == entry && casts[0].Target == boss.Id
              && casts.Select(c => c.Spell).Distinct().Count() == 4, $"PULSE (instant, key 7 the mob) in the entry tick, then all four spells (got {Seq()})");
        Check(casts.Select(c => c.Counter).SequenceEqual(casts.Select((_, i) => (byte)(i == 0 ? 255 : i))), $"counters 255, 1, 2, … over all its spells (got {string.Join(", ", casts.Select(c => c.Counter))})");
        for (var i = 1; i < casts.Count; i++)
        {
            var (prev, next) = (casts[i - 1], casts[i]);
            Check(next.Start >= prev.End + SpellCasts.Seconds(cdtnc[prev.Spell]), $"cast {i} starts ≥ {cdtnc[prev.Spell]} s after the last one's end ({Seq()})");
            var same = casts.Take(i).LastOrDefault(c => c.Spell == next.Spell);
            if (same.Spell != 0)
                Check(next.Start >= same.End + SpellCasts.Seconds(spells.First(s => s.Id == next.Spell).RecastDelay), $"cast {i} waits its recastdelay ({Seq()})");
        }
        var windups = events.Where(e => e.Code == EventCodes.Attack && Is(e.P, boss.Id)).Select(e => (From: L(e.P, 1), To: L(e.P, 4))).ToList();
        Check(windups.Count > 0 && !casts.Any(c => windups.Any(w => c.Start >= w.From && c.Start < w.To)), $"no cast starts inside one of its {windups.Count} swing windups");
        var slots = events.Where(e => e.Code == EventCodes.CastFinished && Is(e.P, boss.Id)).Select(e => (Convert.ToInt16(e.P[2]), Convert.ToByte(e.P[3]))).Distinct().ToList();
        Check(slots.All(s => s.Item2 == Array.FindIndex(spells, x => x.Id == s.Item1)), "CastFinished key 3 = the slot of each spell");
        var stack = T("MOB_UNDEAD_HEROIC_STACK");
        var pulses = events.Where(e => e.Code == EventCodes.CastSpell && Is(e.P, boss.Id) && Convert.ToInt16(e.P[3]) == stack.Id).Select(e => e.P).ToList();
        Check(pulses.Count >= 3 && pulses.Take(3).Select(p => L(p, 4) - entry).SequenceEqual([SpellCasts.Seconds(5), SpellCasts.Seconds(15), SpellCasts.Seconds(25)])
              && pulses.All(p => Convert.ToByte(p[6]) == 0)
              && events.Any(e => e.Code == EventCodes.CastHit && Is(e.P, boss.Id) && Convert.ToInt16(e.P[2]) == stack.Id && Convert.ToByte(e.P[4]) == 0),
              $"HEROIC_STACK CastSpell {{6 0}} at +5 s and every 10 s, its CastHit with counter 0 (got {string.Join(", ", pulses.Select(p => (L(p, 4) - entry) / 10000))} ms)");
        var mark = peer.Events.Count;
        lock (world.Sync) Combat.Damage(me, boss, 1e7, EffectType.Physical, HitOrigin.Direct, -1, world.GameNow);
        var reward = Since(peer, mark).FirstOrDefault(e => e.Code == EventCodes.CastSpell && Is(e.P, boss.Id));
        Check(boss.Dead && (boss.Template.EnergyReward <= 0 || (reward.P is { } rp && Convert.ToByte(rp[6]) == boss.CastCounter)),
              $"at its death the ENERGYREWARD CastSpell carries its last counter {boss.CastCounter}");
    }

    /// <summary>stageJ pvp-spells phase 0 (SPEC §4 T0.9): a damage node with interruptscasting="true" interrupts a mob's cast as interruptcast does.</summary>
    private static void Interrupted()
    {
        using var arena0 = new Arena(ai: true, spells: true);
        var world = arena0.World;
        var (_, peer, me) = arena0.Join("Howler", new InventoryItem("T4_MAIN_SWORD"));
        var (arena, map) = Quiet(arena0);
        world.MobAiChance = 1;
        var mage = arena.Spawn("T4_MOB_ROAMING_UNDEAD_MAGE", new Vector2(4, 0));
        var ai = (BehaviourAI)mage.AI;
        Engage(arena, peer, mage, [me]);
        Record(arena, peer, 6, [me], () => ai.Cast is { Finished: false });
        var counter = ai.Cast?.Counter;
        var mark = peer.Events.Count;
        lock (world.Sync)
        {
            var howl = T("HOWL_EFFECT");
            SpellCasts.Execute(new SpellCasts.SpellContext(me, map, howl, 0, 100, mage, mage.Position, world.GameNow), howl);
        }
        var cancel = Since(peer, mark).Where(e => e.Code == EventCodes.CastCancel && Is(e.P, mage.Id)).Select(e => e.P).ToList();
        Check(counter is not null && ai.Cast is null && cancel.Count == 1 && cancel[0].TryGetValue(1, out var k1) && k1 is true,
              $"HOWL_EFFECT's damage on the casting mage: CastCancel {{1 true}} (got {CombatTest.Show(Since(peer, mark).Where(e => e.Code == EventCodes.CastCancel))})");
    }

    private static void Reset()
    {
        using var arena0 = new Arena(ai: true, spells: true);
        var world = arena0.World;
        var (_, peer, me) = arena0.Join("Leash", new InventoryItem("T4_MAIN_SWORD"));
        var (arena, _) = Quiet(arena0);
        world.MobAiChance = 1;
        var mage = arena.Spawn("T4_MOB_ROAMING_UNDEAD_MAGE", new Vector2(4, 0));
        var ai = (BehaviourAI)mage.AI;
        Engage(arena, peer, mage, [me]);
        Record(arena, peer, 6, [me], () => ai.Cast is { Finished: false });
        Check(ai.Cast is { } cast && cast.Spell.Name == "UNDEAD_MAGE_FREEZE" && ai.FightSeconds(world) >= 3, $"a FREEZE under way ({ai.Describe()})");
        var counter = ai.Cast!.Counter;
        lock (world.Sync) ai.SetVar(world, "interupt_freeze", "set", "true");
        var mark = peer.Events.Count;
        lock (world.Sync) mage.Threat.Clear(); // nothing left to fight: the reset in the next tick
        arena.Run(0.05);
        var cancel = Since(peer, mark).Where(e => e.Code == EventCodes.CastCancel && Is(e.P, mage.Id)).Select(e => e.P).ToList();
        Check(mage.AI.State == MobState.Returning && ai.Cast is null && cancel.Count == 1 && !cancel[0].ContainsKey(1) && Convert.ToByte(cancel[0][2]) == counter && Convert.ToByte(cancel[0][9]) == 0,
              $"the reset cancels the cast: CastCancel {{0, 2 counter, 9 0}} without key 1 (got {CombatTest.Show(Since(peer, mark).Where(e => e.Code == EventCodes.CastCancel))})");
        Check(ai.FightSeconds(world) == 0 && ai.Var(world, "interupt_freeze") == "false", "the fight clock and the resetOnMobReset variables are back to their start");

        // A second mage reset while its AOE_SLOW areas are on the ground: they leave with the reset.
        lock (world.Sync) SpawnManager.Despawn(mage, world, respawn: false);
        var other = arena.Spawn("T4_MOB_ROAMING_UNDEAD_MAGE", new Vector2(-4, 0));
        var areas = new List<long>();
        Engage(arena, peer, other, [me]);
        var events = Record(arena, peer, 10, [me], () => (areas = [.. Since(peer, mark).Where(e => e.Code == EventCodes.NewSpellEffectArea && L(e.P, 1) == other.Id).Select(e => L(e.P, 0))]).Count == 2);
        Check(areas.Count == 2, "the second mage's two AOE_SLOW areas are on the ground");
        mark = peer.Events.Count;
        lock (world.Sync) other.Threat.Clear();
        arena.Run(0.05);
        var left = Since(peer, mark).Where(e => e.Code == EventCodes.Leave).Select(e => L(e.P, 0)).ToList();
        Check(other.AI.State == MobState.Returning && areas.All(left.Contains), $"the reset takes its areas off the ground (Leave {string.Join(", ", left)})");
        _ = events;
    }

    private static void Interpreter()
    {
        // References: a chain resolves, a cycle and a missing one do not.
        var store = new Dictionary<string, XElement>(StringComparer.OrdinalIgnoreCase)
        {
            ["A"] = new("AI", new XAttribute("reference", "B")), ["B"] = new("AI", new XAttribute("reference", "C")),
            ["C"] = XElement.Parse("<AI><Behaviour><Spell name=\"X\" target=\"self\" /></Behaviour></AI>"),
            ["L1"] = new("AI", new XAttribute("reference", "L2")), ["L2"] = new("AI", new XAttribute("reference", "L1")),
        };
        XElement? Find(string n) => store.GetValueOrDefault(n);
        Check(AiScript.Compile(store["A"], "A", Find) is { Source: "C", Slots: ["X"] } && AiScript.Compile(store["L1"], "L1", Find) is null
              && AiScript.Compile(new XElement("AI", new XAttribute("reference", "NOPE")), "Z", Find) is null, "A → B → C resolves to C's AI; a cycle and a missing reference give none");
        var mobs = Store.Mobs;
        var withAi = mobs.All.Where(m => m.AI is not null).ToList();
        var compiled = withAi.Select(m => mobs.Scripts.For(m)).ToList();
        Check(withAi.Count == 4966 && compiled.All(s => s is not null), $"all 4966 <AI> blocks compile (got {withAi.Count}, {compiled.Count(s => s is null)} unresolved)");

        // The slot table (R7): Behaviour then IdleBehaviour, Events skipped, names ignoring case, isNewSpellSlot appends.
        var slots = AiScript.Compile(XElement.Parse("""
            <AI><Events><Event type="onSpawn"><Spell name="EVENTONLY" target="self" /></Event></Events>
              <Behaviour><Spell name="A" target="self" /><SelectFirst><Spell name="a" target="self" /><Spell name="B" target="self" isNewSpellSlot="true" /></SelectFirst>
                <Spell name="B" target="self" isNewSpellSlot="true" /><Spell name="b" target="self" /></Behaviour>
              <IdleBehaviour><Spell name="D" target="self" /></IdleBehaviour></AI>
            """), "S", Find)!;
        var nodes = slots.Behaviour.SelectMany(n => n is AiSelectFirst f ? f.Children : [n]).OfType<AiSpell>().Select(s => s.Slot).ToList();
        Check(slots.Slots.SequenceEqual(["A", "B", "B", "D"]) && nodes.SequenceEqual(new byte[] { 0, 0, 1, 2, 1 }) && slots.Events["onSpawn"][0] is AiSpell { Slot: 0 },
              $"slots A 0, a 0, B 1, B (new) 2, b 1, D 3; the event-only spell 0 (got {string.Join(", ", slots.Slots)} / {string.Join(", ", nodes)})");

        using var arena0 = new Arena(ai: true, spells: true);
        var world = arena0.World;
        var (_, _, me) = arena0.Join("Units", new InventoryItem("T4_MAIN_SWORD"));
        var (arena, map) = Quiet(arena0);
        var ghouls = new[] { arena.Spawn("T4_MOB_ROAMING_UNDEAD_GHOUL", new Vector2(20, 0)), arena.Spawn("T4_MOB_ROAMING_UNDEAD_GHOUL", new Vector2(23, 0)),
            arena.Spawn("T4_MOB_ROAMING_UNDEAD_GHOUL", new Vector2(40, 0)) };
        var units = AiScript.Compile(XElement.Parse("""
            <AI><Variables><Var name="n" type="int" initialValue="5" timeToLiveSeconds="2" /><Var name="flag" type="bool" initialValue="false" /></Variables>
              <Behaviour>
                <SelectRandom behaviour="OnlySelectWorkingActions"><Variable name="pick" op="set" value="heavy" weight="3" /><Variable name="pick" op="set" value="light" weight="1" /></SelectRandom>
                <SelectRandom behaviour="FailIfNotWorking"><Movement target="current" /><Variable name="pick" op="set" value="moved" /></SelectRandom>
                <If><IfVariable name="flag" op="equals" value="true" /><SelectFirst><Movement target="current" /><Variable name="ran" op="set" value="if" /></SelectFirst></If>
                <CompoundAction><Variable name="c1" op="set" value="x" /><Variable name="c2" op="set" value="y" /></CompoundAction>
                <BroadcastVariable name="told" value="yes" radius="5" />
                <Spell name="X" target="self"><IfVariable name="flag" op="equals" value="true" /><IfChance value="0.5" /></Spell>
                <Spell name="Y" target="self"><IFFIGHTDURATION op="notEquals" value="-1" /><IfChance value="0.5" /></Spell>
                <Variable name="flag" op="negate" />
              </Behaviour></AI>
            """), "U", Find)!;
        var ai = new BehaviourAI(ghouls[0], units);
        var b = units.Behaviour;
        lock (world.Sync)
        {
            var heavy = 0;
            for (var i = 0; i < 400; i++)
            {
                ai.Try(b[0]);
                if (ai.Var(world, "pick") == "heavy") heavy++;
            }
            Check(heavy is > 260 and < 340, $"SelectRandom weights 3 : 1 on the seeded map random (heavy {heavy} of 400)");
            var works = Enumerable.Range(0, 200).Count(_ => ai.Try(b[1]));
            Check(works is > 60 and < 140, $"FailIfNotWorking: a pick of the Movement (which never works) fails the node ({works} of 200 worked)");
            Check(!ai.Try(b[2]) && ai.Var(world, "ran") == "", "If: its condition false, nothing runs");
            ai.Try(b[7]);
            Check(ai.Var(world, "flag") == "true" && ai.Try(b[2]) && ai.Var(world, "ran") == "if", "Variable negate flips a bool; If → SelectFirst runs the first child that works");
            Check(ai.Try(b[3]) && ai.Var(world, "c1") == "x" && ai.Var(world, "c2") == "y", "CompoundAction without a Spell child works and runs all its children");
            ai.Try(b[4]);
            var told = ghouls.Select(g => ((BehaviourAI)g.AI).Var(world, "told")).ToList();
            Check(told.SequenceEqual(["", "yes", ""]), $"BroadcastVariable radius 5 without includeSelf: the ghoul 3 m away only (got {string.Join(", ", told)})");
            ai.SetVar(world, "n", "add", "3");
            ai.SetVar(world, "n", "subtract", "1");
            Check(ai.Var(world, "n") == "7", $"int add 3, subtract 1: 5 → 7 (got {ai.Var(world, "n")})");
        }
        arena.Run(2.05);
        lock (world.Sync)
        {
            Check(ai.Var(world, "n") == "5", "after its timeToLiveSeconds (2 s) the variable is back to its initial value");
            ai.SetVar(world, "flag", "set", "false");
            var x = (AiSpell)b[5];
            var rolls = ai.ChanceRolls;
            Check(!ai.Check(x.When) && ai.ChanceRolls == rolls, "IfChance after a false condition takes no roll");
            var y = (AiSpell)b[6];
            world.MobAiChance = 0;
            Check(!ai.Check(y.When) && ai.ChanceRolls == rolls + 1, "IFFIGHTDURATION (any case) with notEquals holds; the chance is rolled once");
        }
        world.MobAiChance = 1;
        for (var i = 0; i < 9; i++)
        {
            arena.Run(0.1);
            lock (world.Sync) Check(!ai.Check(((AiSpell)b[6]).When), $"within 1 s the failed roll stays false ({i + 1} thinks later)");
        }
        Check(ai.ChanceRolls == 1, $"thinks 0.1 s apart within 1 s take one roll (got {ai.ChanceRolls})");
        arena.Run(0.1);
        lock (world.Sync) Check(ai.Check(((AiSpell)b[6]).When) && ai.ChanceRolls == 2, "after 1 s it is rolled again");
        _ = me;
        _ = map;
    }

    private static void Off()
    {
        using var arena0 = new Arena(ai: true, spells: false);
        var world = arena0.World;
        var (_, peer, me) = arena0.Join("Plain", new InventoryItem("T4_MAIN_SWORD"));
        var (arena, _) = Quiet(arena0);
        world.MobAiChance = 1;
        var mage = arena.Spawn("T4_MOB_ROAMING_UNDEAD_MAGE", new Vector2(4, 0));
        Engage(arena, peer, mage, [me]);
        var events = Record(arena, peer, 8, [me]);
        Check(mage.AI is BehaviourAI && !events.Any(e => e.Code == EventCodes.CastStart && Is(e.P, mage.Id)) && events.Count(e => e.Code == EventCodes.Attack && Is(e.P, mage.Id)) >= 3,
              "spells off: the mage swings every 2 s and casts nothing");
        lock (world.Sync) world.MobSpells = true;
        events = Record(arena, peer, 3, [me]);
        Check(events.Any(e => e.Code == EventCodes.CastStart && Is(e.P, mage.Id)), "on again (.mob spells on): it casts");
    }

    private static void Spells2()
    {
        using var arena0 = new Arena(ai: true, spells: true);
        var world = arena0.World;
        var (_, peer, me) = arena0.Join("Tracker", new InventoryItem("T4_MAIN_SWORD"));
        var (arena, map) = Quiet(arena0);
        MobAiTest.OpenGrid(map, arena.Stand); // the escape point and the ring do not wait on the real grid's pool build
        static AiCond Cond(string xml) => AiScript.Compile(XElement.Parse($"<AI><Behaviour><Spell name=\"X\" target=\"self\">{xml}</Spell></Behaviour></AI>"), "T", _ => null)!.Behaviour[0].When!;

        // IfTargetTookDamage on the spell's target (a self node: the mob), for 1 s; DamageType Magic does not take a physical hit.
        var ghoul = arena.Spawn("T4_MOB_ROAMING_UNDEAD_GHOUL", new Vector2(30, 0));
        var ai = (BehaviourAI)ghoul.AI;
        bool before, hit, magic, later;
        lock (world.Sync)
        {
            before = ai.Check(Cond("<IfTargetTookDamage DamageType=\"All\" />"), ghoul);
            Combat.Damage(me, ghoul, 10, EffectType.Physical, HitOrigin.Direct, -1, world.GameNow);
            hit = ai.Check(Cond("<IfTargetTookDamage DamageType=\"All\" />"), ghoul);
            magic = ai.Check(Cond("<IfTargetTookDamage DamageType=\"Magic\" />"), ghoul);
        }
        arena.Run(BehaviourAI.TookDamageWindow + 0.1);
        lock (world.Sync) later = ai.Check(Cond("<IfTargetTookDamage DamageType=\"All\" />"), ghoul);
        Check(!before && hit && !magic && !later, $"IfTargetTookDamage: after the hit only, for {BehaviourAI.TookDamageWindow} s, of its type ({before} {hit} {magic} {later})");

        // RandomGroundTarget radius 9 innerradius 4 origin caster: the meteor's area lands in that ring.
        lock (world.Sync) ai.Try(new AiSpell { Spell = "MOB_DRAGON_FIRE_METEOR_TARGETED_QUICK", Target = "self", RandomGround = (9, 4, false) });
        var meteor = map.Objects.Values.OfType<MobArea>().FirstOrDefault(a => a.Owner == ghoul);
        var ring = meteor is null ? -1 : Vector2.Distance(meteor.Position, ghoul.Position);
        Check(ring is >= 3.99f and <= 9.01f, $"RandomGroundTarget: the area {ring:0.#} m from the mob, within 4-9 m");

        // The fire dragon's TargetAreaDefinition (front 110°, right 70°, back 110°, left 70° of its facing): the player behind it.
        var dragon = arena.Spawn("T6_MOB_RD_DRAGON_FIRE_ELITE_BOSS", new Vector2(0, 40));
        var dai = (BehaviourAI)dragon.AI;
        bool back, front, inBack, far, players;
        lock (world.Sync)
        {
            dragon.Threat[me] = 100;
            me.Teleport(dragon.Position + SpellMovementFacing(dragon.Direction) * -10, 0, DateTime.UtcNow);
            back = dai.Check(Cond("<IfHighestArea type=\"threat\" area=\"back\" />"));
            front = dai.Check(Cond("<IfHighestArea type=\"threat\" area=\"front\" />"));
            players = dai.Check(Cond("<IfPlayersInArea area=\"back\" op=\"greaterOrEqual\" value=\"1\" />")) && !dai.Check(Cond("<IfPlayersInArea area=\"left\" op=\"greaterOrEqual\" value=\"1\" />"));
            var farCond = Cond("<IfTargetIsInHighestArea area=\"back\" type=\"threat\"><IfTargetRange op=\"greater\" value=\"15\" /></IfTargetIsInHighestArea>");
            inBack = dai.Check(farCond, me);
            me.Teleport(dragon.Position + SpellMovementFacing(dragon.Direction) * -20, 0, DateTime.UtcNow);
            far = dai.Check(farCond, me);
        }
        Check(dai.Areas(world).Count == 8 && back && !front && players && !inBack && far,
              $"the dragon's areas ({dai.Areas(world).Count}): the threat behind it (back {back}, front {front}, IfPlayersInArea {players}), IfTargetIsInHighestArea with its own range 15 m ({inBack} at 10 m, {far} at 20 m)");

        // MOB_HERETIC_ALARMTRAP's changeaggro (effectarearadius 30, aggroradius 15): the idle friend 8 m from the caster takes the player.
        var friend = arena.Spawn("T4_MOB_ROAMING_UNDEAD_GHOUL", ghoul.Position - arena.Stand + new Vector2(0, 8)); // the caster chased the player a little
        lock (world.Sync)
        {
            me.Teleport(arena.Stand, 0, DateTime.UtcNow);
            var alarm = T("MOB_HERETIC_ALARMTRAP");
            MobCasts.Execute(new MobCasts.MobHit(ghoul, map, alarm, 1, 100, me, me.PositionAt(DateTime.UtcNow), world.GameNow), alarm);
        }
        Check(friend.AI.State == MobState.Combat && friend.Threat.ContainsKey(me), $"changeaggro: the friend within 15 m of the caster fights the player ({friend.AI.Describe()})");

        // An onDeath event spell (MOB_DEMON_BRUTE_BOSS_ONDEATH) goes out as the boss dies.
        var brute = arena.Spawn("T5_MOB_DEMON_BRUTE_VETERAN_BOSS", new Vector2(-20, 0));
        var mark = peer.Events.Count;
        lock (world.Sync) Combat.Damage(me, brute, 1e9, EffectType.Physical, HitOrigin.Direct, -1, world.GameNow);
        var dying = T("MOB_DEMON_BRUTE_BOSS_ONDEATH");
        Check(brute.Dead && Since(peer, mark).Any(e => e.Code == EventCodes.CastSpell && Is(e.P, brute.Id) && Convert.ToInt16(e.P[3]) == dying.Id), "the brute's onDeath spell: CastSpell MOB_DEMON_BRUTE_BOSS_ONDEATH");

        // A tracking boss flees: its EscapeSpellSequence in order (immunity, escape volley, dash to the escape point), then it leaves.
        var archer = arena.Spawn("T3_MOB_TR_UNDEAD_ARCHER_BOSS", new Vector2(0, -12));
        mark = peer.Events.Count;
        IReadOnlyList<string> sequence;
        lock (world.Sync)
        {
            archer.Threat[me] = 1;
            sequence = ((BehaviourAI)archer.AI).Escape(world, map, "test");
        }
        var start = archer.Position;
        var moved = 0f;
        for (var i = 0; i < 200 && !archer.Removed; i++)
        {
            arena.Run(0.05);
            if (!archer.Removed) moved = Math.Max(moved, Vector2.Distance(archer.Position, start));
        }
        var cast = Since(peer, mark).Where(e => e.Code == EventCodes.CastSpell && Is(e.P, archer.Id)).Select(e => Client.Spells!.Template((int)Convert.ToInt16(e.P[3]))?.Name).ToList();
        Check(sequence.SequenceEqual(["MOB_TRACKING_ESCAPE_IMMUNITY", "MOB_TRACKING_UNDEAD_ARCHER_ESCAPE", "MOB_TRACKING_UNDEAD_ARCHER_ESCAPE_DASH"]) && cast.SequenceEqual(sequence)
              && archer.Removed && moved > 5,
              $"the escape: CastSpell {string.Join(", ", cast)}, a dash of {moved:0.#} m away from the player, then gone ({archer.Removed})");
    }

    private static Vector2 SpellMovementFacing(float degrees) => SpellMovement.Facing(degrees);

    private static void BossMechanics()
    {
        using var arena0 = new Arena(ai: true, spells: true);
        var world = arena0.World;
        var (_, peer, me) = arena0.Join("Bossed", new InventoryItem("T4_MAIN_SWORD"));
        var (arena, map) = Quiet(arena0);
        var boss = arena.Spawn("T4_MOB_EVENT_HALLOWEEN_BOSS", new Vector2(20, 0));
        var demon = arena.Spawn("T6_MOB_MORGANA_CULTIST_DEMON_SPIKED_SUMMON", new Vector2(4, 0));
        arena.Run(0.1);
        var isBoss = T("MOB_IS_BOSS");
        Check(boss.Effects.Spells.Any(s => s.Spell == isBoss), "the boss cast its spawn spell MOB_IS_BOSS on itself at its first tick");

        var mark = peer.Events.Count;
        lock (world.Sync) Combat.Damage(me, demon, 1e9, EffectType.Physical, HitOrigin.Direct, 0, world.GameNow);
        var death = T("DEMON_SOLDIER_DEATH_AOE");
        var after = Since(peer, mark).ToList();
        Check(demon.Dead && after.Any(e => e.Code == EventCodes.CastSpell && Is(e.P, demon.Id) && Convert.ToInt16(e.P[3]) == death.Id)
              && map.Objects.Values.OfType<MobArea>().Count(a => a.Owner == demon && !a.Gone) == 2,
              "the demon's death: CastSpell DEMON_SOLDIER_DEATH_AOE and its two areas at its feet");

        var knock = T("MOB_TRACKING_UNDEAD_HARVESTER_ESCAPE_KNOCKBACK");
        var ghoul = arena.Spawn("T4_MOB_ROAMING_UNDEAD_GHOUL", new Vector2(-2, 0));
        mark = peer.Events.Count;
        var before = me.PositionAt(DateTime.UtcNow);
        lock (world.Sync) MobCasts.Execute(new MobCasts.MobHit(ghoul, map, knock, 1, 100, me, before, world.GameNow), knock);
        Check(me.Effects.Forced is { Kind: 2 } f && Vector2.Distance(f.To, before) > 1 && Since(peer, mark).Any(e => e.Code == EventCodes.ForcedMovement && Is(e.P, me.Id)),
              $"a mob's knockback pushes the player (ForcedMovement kind 2, {(me.Effects.Forced is { } g ? Vector2.Distance(g.To, before) : 0):0.#} m)");

        bool moved;
        lock (world.Sync) moved = ((BehaviourAI)ghoul.AI).Try(new AiMovement { Target = "self", Angle = 180, Distance = 4 });
        Check(moved && ghoul.Motion.Moving && Vector2.Distance(ghoul.Motion.Destination, ghoul.Position) is > 1.9f and < 4.1f,
              $"<Movement target=self angle=180 distance=4>: the ghoul walks {Vector2.Distance(ghoul.Motion.Destination, ghoul.Position):0.#} m");
    }
}
