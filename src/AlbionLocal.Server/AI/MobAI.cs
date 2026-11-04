using AlbionLocal.Server.Data;
using AlbionLocal.Server.Entities;

namespace AlbionLocal.Server.AI;

/// <summary>What a mob's AI is doing (stage 5d, SPEC §5d.1): Idle → Combat (chase, attack) → Returning (leash) → Idle; a dead mob has none.</summary>
public enum MobState
{
    Idle,
    Combat,
    Returning,
}

/// <summary>
/// A mob's brain (stage 5d, analysis/work/stage5/SPEC.md §5d; ARCHITECTURE.md §6.3 level 1 without the &lt;AI&gt; rotations,
/// which are 5f, <c>BehaviourAI</c>). <see cref="For"/> picks it from the mobs.xml attributes: <see cref="NullAI"/> for the
/// mobs that do not fight players (guards and guard towers, decorations, friendly NPCs), <see cref="AggressiveAI"/> for a
/// hostile mob with an aggroradius, <see cref="PassiveAI"/> (aggroradius 0: it fights back only when damaged) for the rest.
/// The world tick runs <see cref="Update(Map, World)"/> on every map with players while World.MobAi is on; the combat code
/// tells it about damage, healing, players that leave or go down, and the death. Touched only under <see cref="World.Sync"/>.
/// </summary>
public abstract class MobAI(Mob me)
{
    protected Mob Me { get; } = me;

    public MobState State { get; protected set; } = MobState.Idle;

    /// <summary>The AI kind for .mob ai and the logs.</summary>
    public string Kind => GetType().Name;

    /// <summary>
    /// The AI of <paramref name="mob"/> by its mobs.xml attributes (SPEC §5d.1): npchostility guard, decoration or friendly →
    /// <see cref="NullAI"/>; hostile with aggroradius &gt; 0 → <see cref="AggressiveAI"/>; otherwise (aggroradius 0, or the
    /// client's default hostility) → <see cref="PassiveAI"/>; either of the last two with a non-empty resolved &lt;AI&gt; →
    /// <see cref="BehaviourAI"/> with the same aggro rules (stageD mobspells §4.1).
    /// </summary>
    public static MobAI For(Mob mob)
    {
        var t = mob.Template;
        if (t.Hostility is not (NpcHostility.Hostile or NpcHostility.None)) return new NullAI(mob);
        var aggressive = t is { Hostility: NpcHostility.Hostile, AggroRadius: > 0 };
        // A fighting mob with a non-empty <AI> (after its references) runs its rotation (stageD mobspells, BehaviourAI).
        if (t.AI is not null && mob.World?.Clusters?.MobsIfLoaded?.Scripts.For(t) is { Empty: false } script) return new BehaviourAI(mob, script, aggressive);
        return aggressive ? new AggressiveAI(mob) : new PassiveAI(mob);
    }

    /// <summary>One world tick of this mob on <paramref name="map"/>.</summary>
    protected abstract void Update(World world, Map map);

    /// <summary><paramref name="attacker"/> took <paramref name="damage"/> health off the mob (not lethal).</summary>
    public abstract void OnDamaged(Player attacker, double damage);

    /// <summary>
    /// <paramref name="threat"/> for <paramref name="p"/> without damage (changeaggro, maketargettopthreat, a summoner's target; stageD
    /// effects batch 2): a fighting mob adds it, an idle one starts to fight it. Here: it becomes the NewMob key 12 target.
    /// </summary>
    public virtual void AddThreat(Player p, double threat)
    {
        if (Me.TargetId == 0) Me.TargetId = p.Id;
    }

    /// <summary><paramref name="healer"/> healed <paramref name="target"/> by <paramref name="amount"/>.</summary>
    public virtual void OnHealed(Player healer, Player target, double amount) { }

    /// <summary><paramref name="p"/> is no target any more (it left the map, or went down): off the threat list.</summary>
    public virtual void Forget(Player p) { }

    /// <summary>The mob died (<see cref="Systems.Combat.Kill"/>): no AI, no threat, its pending swings dropped, it stops where it is.</summary>
    public virtual void OnDeath() { }

    /// <summary>The AI goes off (World.MobAi): the mob stops fighting and walking where it is and is idle (<see cref="Halt(World)"/>).</summary>
    public virtual void Halt(World world, Map map) { }

    /// <summary>A line for .mob ai.</summary>
    public virtual string Describe() => $"{Kind} {State}";

    /// <summary>
    /// The world tick of a map (ARCHITECTURE.md §4.4 step 4, "units: AI"): every living mob's AI, while players are on the
    /// map (a map without players sleeps, §4.2). Idle mobs that no client sees stand still (§4.3 "active cells"); fights and
    /// returns run on.
    /// </summary>
    public static void Update(Map map, World world)
    {
        if (map.Players.Count == 0 || map.Grid is null) return;
        foreach (var mob in map.Objects.Values.OfType<Mob>().ToList())
        {
            if (mob.Dead || mob.Removed || mob.Map != map) continue;
            try
            {
                if (!mob.SpawnSpellsCast) Systems.MobEvents.Spawned(world, map, mob); // stageN spells-bosses: <SpawnSpells>
                if (world.Modules.BeforeThink(mob, map)) continue; // the modules' Mobs.OnBeforeThink
                mob.AI.Update(world, map);
            }
            catch (Exception e)
            {
                // One broken mob must not take the rest of the world tick with it: logged once per template, it stops.
                Systems.Combat.LogOnce("ai-failed:" + mob.Template.Name, $"{mob}: AI failed, the mob stops: {e}");
                try { mob.AI.Halt(world, map); } catch (Exception) { /* logged above */ }
            }
        }
    }

    /// <summary>
    /// The AI goes off for the whole world (.mob ai off, ARCHITECTURE.md §6.3): every living mob on every map stops
    /// fighting, returning and walking where it is (<see cref="Halt(World, Map)"/>), so none stays immune, keeps players in
    /// combat or drifts from the clients' dead-reckoning.
    /// </summary>
    public static void Halt(World world)
    {
        foreach (var map in world.Maps.Maps)
            foreach (var mob in map.Objects.Values.OfType<Mob>().ToList())
                if (!mob.Dead && !mob.Removed && mob.Map == map) mob.AI.Halt(world, map);
    }

    /// <summary><paramref name="p"/> leaves <paramref name="map"/> or goes down: every mob there drops it from its threat list.</summary>
    public static void Forget(Map map, Player p)
    {
        foreach (var mob in map.Objects.Values.OfType<Mob>())
            if (mob.Threat.ContainsKey(p)) mob.AI.Forget(p);
    }

    /// <summary>A heal of <paramref name="target"/> by <paramref name="healer"/>: threat for the healer on every mob fighting the target.</summary>
    public static void Healed(Map map, Player healer, Player target, double amount)
    {
        if (amount <= 0) return;
        foreach (var mob in map.Objects.Values.OfType<Mob>())
            if (mob.Threat.ContainsKey(target)) mob.AI.OnHealed(healer, target, amount);
    }
}

/// <summary>
/// The AI of a mob that does not fight players (5a's behaviour): it stands at its point, facing the tile's rotation, and
/// sends no event 3; damage makes the attacker its NewMob key 12 target, as in 5b. Guards and guard towers are NullAI
/// until flagged players exist.
/// </summary>
public sealed class NullAI(Mob me) : MobAI(me)
{
    protected override void Update(World world, Map map) { }

    public override void OnDamaged(Player attacker, double damage)
    {
        if (Me.TargetId == 0) Me.TargetId = attacker.Id;
    }
}
