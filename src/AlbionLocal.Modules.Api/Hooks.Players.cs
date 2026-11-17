namespace AlbionLocal.Modules;

/// <summary>Players entering and leaving the game, going down, completing goals. Bots do not fire these.</summary>
public interface IPlayerHooks
{
    /// <summary>A player logged in: its first Join after the login (not a cluster change), after the client has the world. Messages work here.</summary>
    IHookRegistration OnLogin(Action<PlayerLoginArgs> handler, HookPriority priority = HookPriority.Normal);

    /// <summary>A player's connection ended (logout, disconnect, timeout), not a cluster change. Still readable; messages go nowhere.</summary>
    IHookRegistration OnLogout(Action<PlayerArgs> handler, HookPriority priority = HookPriority.Normal);

    /// <summary>A player arrived in a cluster: every Join (the login's too, with <see cref="ZoneArgs.ClusterChange"/> false), after <see cref="OnLogin"/>. Id = the cluster.</summary>
    IHookRegistration OnEnterZone(Action<ZoneArgs> handler, HookPriority priority = HookPriority.Normal);
    /// <summary>As OnEnterZone, for one cluster (its world.xml id such as "0006", or a mask with * and ?).</summary>
    IHookRegistration OnEnterZone(string cluster, Action<ZoneArgs> handler, HookPriority priority = HookPriority.Normal);

    /// <summary>A player leaves its cluster (a cluster change or a logout), still on its map. Id = the cluster.</summary>
    IHookRegistration OnLeaveZone(Action<ZoneArgs> handler, HookPriority priority = HookPriority.Normal);
    /// <summary>As OnLeaveZone, for one cluster (its world.xml id such as "0006", or a mask with * and ?).</summary>
    IHookRegistration OnLeaveZone(string cluster, Action<ZoneArgs> handler, HookPriority priority = HookPriority.Normal);

    /// <summary>A player was knocked down (by a mob, a player, a fall to 0 health).</summary>
    IHookRegistration OnKnockedDown(Action<PlayerDownArgs> handler, HookPriority priority = HookPriority.Normal);

    /// <summary>A player died (a lethal kill, the die button, a knockdown that ran out).</summary>
    IHookRegistration OnDied(Action<PlayerDownArgs> handler, HookPriority priority = HookPriority.Normal);

    /// <summary>A goal was completed: a bounty, an expedition, a corrupted dungeon. Id = its kind ("bounty", "expedition", "corrupted").</summary>
    IHookRegistration OnGoalCompleted(Action<GoalArgs> handler, HookPriority priority = HookPriority.Normal);
    /// <summary>As OnGoalCompleted, for one kind ("bounty", "expedition", "corrupted", or a mask with * and ?).</summary>
    IHookRegistration OnGoalCompleted(string kind, Action<GoalArgs> handler, HookPriority priority = HookPriority.Normal);

    /// <summary>A typed command is about to run, whoever's and from wherever (the game's chat or the server console; a module's command too): <see cref="CancellableArgs.Cancel"/> stops it and the reason goes back as its error.</summary>
    IHookRegistration OnBeforeCommand(Action<BeforeCommandArgs> handler, HookPriority priority = HookPriority.Normal);

    /// <summary>A player gained fame (kills, gathering, crafting, goals, a GM's or a module's grant), after it was added and the client told. A tome's fame, which only feeds the destiny board, is not in it.</summary>
    IHookRegistration OnFameGained(Action<FameGainedArgs> handler, HookPriority priority = HookPriority.Normal);

    /// <summary>A joined player's silver changed (loot, the market, trades, costs, a GM's or a module's). The silver of market sales made while away comes in before its Join and is not told.</summary>
    IHookRegistration OnSilverChanged(Action<SilverChangedArgs> handler, HookPriority priority = HookPriority.Normal);
}

/// <summary>A player (OnLogout), and the base of the other player args.</summary>
public class PlayerArgs(IPlayer player)
{
    /// <summary>The player.</summary>
    public IPlayer Player { get; } = player;
}

/// <summary>A player logged in (OnLogin).</summary>
public sealed class PlayerLoginArgs(IPlayer player, bool first) : PlayerArgs(player)
{
    /// <summary>The character's first login since it was made (as far as this server run can tell).</summary>
    public bool FirstLogin { get; } = first;
}

/// <summary>A player entered or is leaving a cluster (OnEnterZone, OnLeaveZone).</summary>
public sealed class ZoneArgs(IPlayer player, ZoneInfo zone, bool clusterChange) : PlayerArgs(player)
{
    /// <summary>The zone entered or left.</summary>
    public ZoneInfo Zone { get; } = zone;

    /// <summary>Entered through a cluster change (not a login) / left for one (not a logout).</summary>
    public bool ClusterChange { get; } = clusterChange;
}

/// <summary>A player was knocked down or died (OnKnockedDown, OnDied).</summary>
public sealed class PlayerDownArgs(IPlayer player, string killerName, IUnit? killer) : PlayerArgs(player)
{
    /// <summary>The killer's name as the death screen shows it (a mob's localisation tag "@…", a player's name, "" when none).</summary>
    public string KillerName { get; } = killerName;

    /// <summary>The killer when it is still on the map; null otherwise.</summary>
    public IUnit? Killer { get; } = killer;
}

/// <summary>A typed command is about to run (OnBeforeCommand): <see cref="CancellableArgs.Cancel"/> stops it, the reason going back as its error.</summary>
public sealed class BeforeCommandArgs(IPlayer? player, string line, PlayerLevel level) : CancellableArgs
{
    /// <summary>The player who typed it; null for the server console.</summary>
    public IPlayer? Player { get; } = player;

    /// <summary>The line as typed. It may hold a password (.account): do not log it as it is.</summary>
    public string Line { get; } = line;

    /// <summary>The level it runs with (the console's is Admin).</summary>
    public PlayerLevel Level { get; } = level;
}

/// <summary>What fame came from.</summary>
public enum FameSource
{
    /// <summary>One this list does not have yet.</summary>
    Other,
    /// <summary>A kill (also goals and grants).</summary>
    Kill,
    /// <summary>A harvest.</summary>
    Harvest,
    /// <summary>A craft.</summary>
    Craft,
    /// <summary>A tome used.</summary>
    Tome,
    /// <summary>Fishing.</summary>
    Fishing,
    /// <summary>Farming.</summary>
    Farm,
}

/// <summary>A player gained fame (OnFameGained).</summary>
public sealed class FameGainedArgs(IPlayer player, double amount, FameSource source, ItemType? item, bool pvp) : PlayerArgs(player)
{
    /// <summary>The fame gained, in whole units as <see cref="IPlayer.Fame"/>, with every factor applied (a part of a unit is possible).</summary>
    public double Amount { get; } = amount;

    /// <summary>What it came from.</summary>
    public FameSource Source { get; } = source;

    /// <summary>The harvested, caught or crafted item; null when there is none.</summary>
    public ItemType? Item { get; } = item;

    /// <summary>A player's kill.</summary>
    public bool Pvp { get; } = pvp;
}

/// <summary>A player's silver changed (OnSilverChanged).</summary>
public sealed class SilverChangedArgs(IPlayer player, double delta, double total, string reason) : PlayerArgs(player)
{
    /// <summary>The change, in whole units as <see cref="IPlayer.Silver"/>: negative when spent (a part of a unit is possible).</summary>
    public double Delta { get; } = delta;

    /// <summary>The player's silver now, in whole units (a part of a unit is possible).</summary>
    public double Total { get; } = total;

    /// <summary>Why, in the server's words, for a log. Not for code to read: it may change.</summary>
    public string Reason { get; } = reason;
}

/// <summary>What kind of goal was completed.</summary>
public enum GoalKind
{
    /// <summary>One this list does not have yet.</summary>
    Other,
    /// <summary>A bounty.</summary>
    Bounty,
    /// <summary>An expedition.</summary>
    Expedition,
    /// <summary>A corrupted dungeon.</summary>
    Corrupted,
}

/// <summary>A goal was completed (OnGoalCompleted).</summary>
public sealed class GoalArgs(IPlayer player, GoalKind kind, string description) : PlayerArgs(player)
{
    /// <summary>What kind of goal.</summary>
    public GoalKind Kind { get; } = kind;

    /// <summary>What was completed, as the server describes it.</summary>
    public string Description { get; } = description;
}
