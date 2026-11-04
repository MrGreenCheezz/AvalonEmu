namespace AlbionLocal.Modules;

/// <summary>Mobs. Id = the mob's mobs.xml uniquename.</summary>
public interface IMobHooks
{
    /// <summary>A mob was put on a map (a spawn point's spawn or respawn, a GM's, an event's, a module's). Fires for every mob of a cluster when it loads: keep it cheap.</summary>
    IHookRegistration OnSpawned(Action<MobArgs> handler, HookPriority priority = HookPriority.Normal);
    /// <summary>As OnSpawned, for one mob (its mobs.xml uniquename, or a mask with * and ?).</summary>
    IHookRegistration OnSpawned(string mob, Action<MobArgs> handler, HookPriority priority = HookPriority.Normal);

    /// <summary>A mob was killed (after its rewards, loot and the systems' own handling). Despawns (.mob kill) do not count.</summary>
    IHookRegistration OnDied(Action<MobDeathArgs> handler, HookPriority priority = HookPriority.Normal);
    /// <summary>As OnDied, for one mob (its mobs.xml uniquename, or a mask with * and ?).</summary>
    IHookRegistration OnDied(string mob, Action<MobDeathArgs> handler, HookPriority priority = HookPriority.Normal);

    /// <summary>A mob's loot was rolled, before the bag is made: <see cref="LootArgs.Items"/> may be changed (add, remove).</summary>
    IHookRegistration OnLootRolled(Action<LootArgs> handler, HookPriority priority = HookPriority.Normal);
    /// <summary>As OnLootRolled, for one mob (its mobs.xml uniquename, or a mask with * and ?).</summary>
    IHookRegistration OnLootRolled(string mob, Action<LootArgs> handler, HookPriority priority = HookPriority.Normal);

    /// <summary>
    /// A mob's AI is about to run its update (each world tick, while players are on its map and the mob AI is on): <see cref="CancellableArgs.Cancel"/> skips the
    /// engine's AI for this update, so the module drives the mob itself (<see cref="IMob.MoveTo"/>, <see cref="IMob.CastSpell"/>). A walk keeps being sent; a
    /// mob left cancelled does not chase, swing, leash or finish its return home. Its spawn spells have run. With a handler for everything this runs for every mob
    /// of every populated map: register for one mob where possible.
    /// </summary>
    IHookRegistration OnBeforeThink(Action<BeforeThinkArgs> handler, HookPriority priority = HookPriority.Normal);
    /// <summary>As OnBeforeThink, for one mob (its mobs.xml uniquename, or a mask with * and ?).</summary>
    IHookRegistration OnBeforeThink(string mob, Action<BeforeThinkArgs> handler, HookPriority priority = HookPriority.Normal);

    /// <summary>
    /// An idle mob picked a player in its aggro range: <see cref="CancellableArgs.Cancel"/> keeps it from that target for now (it picks again at its next update, so
    /// this comes every tick while it is refused). Damage, threat and a fellow's alert start a fight without asking: OnAggro tells of every start.
    /// </summary>
    IHookRegistration OnBeforeAggro(Action<BeforeAggroArgs> handler, HookPriority priority = HookPriority.Normal);
    /// <summary>As OnBeforeAggro, for one mob (its mobs.xml uniquename, or a mask with * and ?).</summary>
    IHookRegistration OnBeforeAggro(string mob, Action<BeforeAggroArgs> handler, HookPriority priority = HookPriority.Normal);

    /// <summary>A mob's fight started: it had no target and has one now (its own pick, damage, threat, a fellow's alert); told at the next tick. Not a change of target during the fight. Not for mobs that do not fight (guards, decorations).</summary>
    IHookRegistration OnAggro(Action<MobAggroArgs> handler, HookPriority priority = HookPriority.Normal);
    /// <summary>As OnAggro, for one mob (its mobs.xml uniquename, or a mask with * and ?).</summary>
    IHookRegistration OnAggro(string mob, Action<MobAggroArgs> handler, HookPriority priority = HookPriority.Normal);

    /// <summary>
    /// A mob is about to give up its fight (<see cref="BeforeLeaveCombatArgs.Reason"/>): <see cref="CancellableArgs.Cancel"/> keeps it in the fight, and it is asked again at its next update
    /// while the reason holds. Left with no target it stands where it is until a player hits it or <see cref="IMob.SetTarget"/> gives it one. Not for the AI being switched off.
    /// </summary>
    IHookRegistration OnBeforeLeaveCombat(Action<BeforeLeaveCombatArgs> handler, HookPriority priority = HookPriority.Normal);
    /// <summary>As OnBeforeLeaveCombat, for one mob (its mobs.xml uniquename, or a mask with * and ?).</summary>
    IHookRegistration OnBeforeLeaveCombat(string mob, Action<BeforeLeaveCombatArgs> handler, HookPriority priority = HookPriority.Normal);

    /// <summary>A mob's fight ended without its death: it lost its targets or its leash and goes home, or the mob AI went off. Actions on the mob (a cast, a walk, a despawn) wait for the next tick.</summary>
    IHookRegistration OnLeaveCombat(Action<MobLeaveCombatArgs> handler, HookPriority priority = HookPriority.Normal);
    /// <summary>As OnLeaveCombat, for one mob (its mobs.xml uniquename, or a mask with * and ?).</summary>
    IHookRegistration OnLeaveCombat(string mob, Action<MobLeaveCombatArgs> handler, HookPriority priority = HookPriority.Normal);

    /// <summary>A spawn point's respawn time has come, its mob picked: <see cref="CancellableArgs.Cancel"/> keeps it away; the point tries again after another respawn time. Not the first spawn of a loading map.</summary>
    IHookRegistration OnBeforeRespawn(Action<BeforeRespawnArgs> handler, HookPriority priority = HookPriority.Normal);
    /// <summary>As OnBeforeRespawn, for one mob (its mobs.xml uniquename, or a mask with * and ?).</summary>
    IHookRegistration OnBeforeRespawn(string mob, Action<BeforeRespawnArgs> handler, HookPriority priority = HookPriority.Normal);
}

/// <summary>A mob (OnSpawned), and the base of the other mob args.</summary>
public class MobArgs(IMob mob)
{
    /// <summary>The mob.</summary>
    public IMob Mob { get; } = mob;
}

/// <summary>A mob was killed (OnDied).</summary>
public sealed class MobDeathArgs(IMob mob, IPlayer killer, IReadOnlyList<IPlayer> credited) : MobArgs(mob)
{
    /// <summary>The player credited with the kill (a companion's owner for a companion's kill).</summary>
    public IPlayer Killer { get; } = killer;

    /// <summary>The killer's fame group (party members near) and a world boss's contributors.</summary>
    public IReadOnlyList<IPlayer> Credited { get; } = credited;
}

/// <summary>A mob's AI is about to run its update (OnBeforeThink): <see cref="CancellableArgs.Cancel"/> skips the engine's AI for this update.</summary>
public sealed class BeforeThinkArgs(IMob mob, double deltaSeconds) : CancellableArgs
{
    /// <summary>The mob.</summary>
    public IMob Mob { get; } = mob;

    /// <summary>The seconds since the last world tick (a map without players does not update its mobs: more may have passed for this one).</summary>
    public double DeltaSeconds { get; } = deltaSeconds;
}

/// <summary>An idle mob picked a player (OnBeforeAggro): <see cref="CancellableArgs.Cancel"/> keeps it from that target for now.</summary>
public sealed class BeforeAggroArgs(IMob mob, IPlayer player) : CancellableArgs
{
    /// <summary>The mob.</summary>
    public IMob Mob { get; } = mob;

    /// <summary>The player it picked (it may be a bot: <see cref="IPlayer.IsBot"/>).</summary>
    public IPlayer Player { get; } = player;
}

/// <summary>A mob's fight started (OnAggro).</summary>
public sealed class MobAggroArgs(IMob mob, IPlayer player) : MobArgs(mob)
{
    /// <summary>Its first target (it may be a bot: <see cref="IPlayer.IsBot"/>).</summary>
    public IPlayer Player { get; } = player;
}

/// <summary>Why a mob's fight ended.</summary>
public enum LeaveCombatReason
{
    /// <summary>One this list does not have yet: see <see cref="MobLeaveCombatArgs.Detail"/>.</summary>
    Other,
    /// <summary>It has no target left.</summary>
    NoTarget,
    /// <summary>Its target went beyond its pursuit radius, or stayed out of its reach too long without a hit.</summary>
    TooFar,
    /// <summary>The mob's AI was switched off.</summary>
    AiOff,
    /// <summary>Its target stayed out of its sight too long without a hit.</summary>
    OutOfSight,
}

/// <summary>A mob is about to give up its fight (OnBeforeLeaveCombat): <see cref="CancellableArgs.Cancel"/> keeps it in.</summary>
public sealed class BeforeLeaveCombatArgs(IMob mob, LeaveCombatReason reason, string detail) : CancellableArgs
{
    /// <summary>The mob.</summary>
    public IMob Mob { get; } = mob;

    /// <summary>Why.</summary>
    public LeaveCombatReason Reason { get; } = reason;

    /// <summary>The same in the server's words, for a log. Not for code to read: it may change.</summary>
    public string Detail { get; } = detail;
}

/// <summary>A mob's fight ended without its death (OnLeaveCombat).</summary>
public sealed class MobLeaveCombatArgs(IMob mob, LeaveCombatReason reason, string detail) : MobArgs(mob)
{
    /// <summary>Why.</summary>
    public LeaveCombatReason Reason { get; } = reason;

    /// <summary>The same in the server's words, for a log. Not for code to read: it may change.</summary>
    public string Detail { get; } = detail;
}

/// <summary>A spawn point's mob is about to come back (OnBeforeRespawn). It is not on the map yet, so there is no <see cref="IMob"/>.</summary>
public sealed class BeforeRespawnArgs(MobType mob, string cluster, float x, float y) : CancellableArgs
{
    /// <summary>The kind of mob.</summary>
    public MobType Mob { get; } = mob;

    /// <summary>The cluster.</summary>
    public string Cluster { get; } = cluster;

    /// <summary>The spawn point's X, in metres on the map.</summary>
    public float X { get; } = x;

    /// <summary>The spawn point's Y, in metres on the map.</summary>
    public float Y { get; } = y;
}

/// <summary>A mob's loot was rolled (OnLootRolled): change <see cref="Items"/> to change the drop.</summary>
public sealed class LootArgs(IMob mob, IPlayer killer, LootList items) : MobArgs(mob)
{
    /// <summary>The player credited with the kill.</summary>
    public IPlayer Killer { get; } = killer;

    /// <summary>The rolled items; change the list to change the drop (<c>e.Items.Add("T4_ORE", 3)</c>; items the server cannot create are logged and left out).</summary>
    public LootList Items { get; } = items;
}
