namespace AlbionLocal.Plugin.Api;

/// <summary>
/// The game events. Subscribe with += in <see cref="IPlugin.Load"/> (unsubscribe with -= any time; at unload the server drops them all).
/// Every handler runs on the world thread, inside the game's own processing: keep it short (the server warns about a handler that takes
/// longer than Plugins:SlowMs), never block, never wait for another thread. A handler that throws is logged; after Plugins:MaxErrors errors
/// the plugin is disabled and the game goes on. The <c>…Args</c> objects are valid only during the call (keep the IPlayer/IMob, not the args).
/// </summary>
public interface IPluginEvents
{
    /// <summary>The server has started (all plugins loaded, listeners open). A plugin loaded later (reload) does not get it.</summary>
    event Action? ServerStarted;

    /// <summary>The server is stopping: last chance to save. Players are still in game.</summary>
    event Action? ServerStopping;

    /// <summary>Every world tick (50 ms by default). Prefer <see cref="IPluginTimers"/> for anything periodic.</summary>
    event Action<TickArgs>? Tick;

    /// <summary>A player logged in: its first Join after the login (not a cluster change), after the client has the world. Messages work here.</summary>
    event Action<PlayerLoginArgs>? PlayerLogin;

    /// <summary>A player's connection ended (logout, disconnect, timeout), not a cluster change. Still readable; messages go nowhere.</summary>
    event Action<PlayerArgs>? PlayerLogout;

    /// <summary>A player arrived in a cluster: every Join (the login's too, with <see cref="ZoneArgs.ClusterChange"/> false), after <see cref="PlayerLogin"/>.</summary>
    event Action<ZoneArgs>? ZoneEnter;

    /// <summary>A player leaves its cluster (a cluster change or a logout), still on its map.</summary>
    event Action<ZoneArgs>? ZoneLeave;

    /// <summary>A mob was put on a map (a spawn point's spawn or respawn, a GM's, an event's, a plugin's). Fires for every mob of a cluster when it loads: keep it cheap.</summary>
    event Action<MobArgs>? MobSpawned;

    /// <summary>A mob was killed (after its rewards, loot and the systems' own handling). Despawns (.mob kill) do not count.</summary>
    event Action<MobDeathArgs>? MobDied;

    /// <summary>A player was knocked down (by a mob, a player, a fall to 0 health).</summary>
    event Action<PlayerDownArgs>? PlayerKnockedDown;

    /// <summary>A player died (a lethal kill, the die button, a knockdown that ran out).</summary>
    event Action<PlayerDownArgs>? PlayerDied;

    /// <summary>
    /// A hit is about to land (player → mob, mob → player, player → player), after resistances and before damage shields. <see cref="DamageArgs.Amount"/>
    /// may be changed (0 = no damage). Fires for every hit, damage over time included: keep it very cheap.
    /// </summary>
    event Action<DamageArgs>? Damage;

    /// <summary>A consumable was used: one use from the inventory (potions, food, chests, maps…) or the potion/food slot.</summary>
    event Action<ItemArgs>? ItemUsed;

    /// <summary>Items were crafted at a station (or bought at an NPC shop, which is a craft), per batch.</summary>
    event Action<ItemArgs>? ItemCrafted;

    /// <summary>A mob's loot was rolled, before the bag is made: <see cref="LootArgs.Items"/> may be changed (add, remove).</summary>
    event Action<LootArgs>? LootRolled;

    /// <summary>A harvest (gathering or a fishing catch) gave items.</summary>
    event Action<HarvestArgs>? Harvested;

    /// <summary>A goal was completed: a bounty, an expedition, a corrupted dungeon.</summary>
    event Action<GoalArgs>? GoalCompleted;

    /// <summary>A station action was asked for (craft, repair, salvage, enchant, reroll, awaken), with whether the server accepted it.</summary>
    event Action<BuildingActionArgs>? BuildingAction;

    /// <summary>A character joined a party (a new party's leader too).</summary>
    event Action<PartyArgs>? PartyJoined;

    /// <summary>A character left a party (left, kicked, disconnected).</summary>
    event Action<PartyArgs>? PartyLeft;

    /// <summary>A Say line (not a command) a player typed.</summary>
    event Action<ChatArgs>? ChatMessage;
}

/// <summary>A world tick: the world clock and the time since the last tick, in seconds.</summary>
public sealed class TickArgs(double seconds, double delta)
{
    public double Seconds { get; } = seconds;
    public double DeltaSeconds { get; } = delta;
}

public class PlayerArgs(IPlayer player)
{
    public IPlayer Player { get; } = player;
}

public sealed class PlayerLoginArgs(IPlayer player, bool first) : PlayerArgs(player)
{
    /// <summary>The character's first login since it was made (as far as this server run can tell).</summary>
    public bool FirstLogin { get; } = first;
}

public sealed class ZoneArgs(IPlayer player, string cluster, bool clusterChange) : PlayerArgs(player)
{
    /// <summary>The cluster entered or left.</summary>
    public string Cluster { get; } = cluster;

    /// <summary>Entered through a cluster change (not a login) / left for one (not a logout).</summary>
    public bool ClusterChange { get; } = clusterChange;
}

public class MobArgs(IMob mob)
{
    public IMob Mob { get; } = mob;
}

public sealed class MobDeathArgs(IMob mob, IPlayer killer, IReadOnlyList<IPlayer> credited) : MobArgs(mob)
{
    /// <summary>The player credited with the kill (a companion's owner for a companion's kill).</summary>
    public IPlayer Killer { get; } = killer;

    /// <summary>The killer's fame group (party members near) and a world boss's contributors.</summary>
    public IReadOnlyList<IPlayer> Credited { get; } = credited;
}

public sealed class PlayerDownArgs(IPlayer player, string killerName, IUnit? killer) : PlayerArgs(player)
{
    /// <summary>The killer's name as the death screen shows it (a mob's localisation tag "@…", a player's name, "" when none).</summary>
    public string KillerName { get; } = killerName;

    /// <summary>The killer when it is still on the map; null otherwise.</summary>
    public IUnit? Killer { get; } = killer;
}

public enum DamageKind
{
    PlayerToMob,
    MobToPlayer,
    PlayerToPlayer,
}

public sealed class DamageArgs(IUnit attacker, IUnit target, DamageKind kind, bool overTime, double amount)
{
    public IUnit Attacker { get; } = attacker;
    public IUnit Target { get; } = target;
    public DamageKind Kind { get; } = kind;

    /// <summary>A damage-over-time tick (not a direct hit).</summary>
    public bool OverTime { get; } = overTime;

    /// <summary>The damage about to be dealt; set it to change the hit (clamped at 0).</summary>
    public double Amount { get; set; } = amount;
}

public sealed class ItemArgs(IPlayer player, string item, int count, string source) : PlayerArgs(player)
{
    /// <summary>The item's uniquename.</summary>
    public string Item { get; } = item;

    public int Count { get; } = count;

    /// <summary>ItemUsed: "inventory" or "slot"; ItemCrafted: the station building's name.</summary>
    public string Source { get; } = source;
}

/// <summary>One loot entry (uniquename, count).</summary>
public sealed record LootItem(string Item, int Count);

public sealed class LootArgs(IMob mob, IPlayer killer, List<LootItem> items) : MobArgs(mob)
{
    public IPlayer Killer { get; } = killer;

    /// <summary>The rolled items; change the list to change the drop (unknown or non-creatable items are logged and left out).</summary>
    public List<LootItem> Items { get; } = items;
}

public sealed class HarvestArgs(IPlayer player, string item, int count, string kind) : PlayerArgs(player)
{
    public string Item { get; } = item;
    public int Count { get; } = count;

    /// <summary>"gather" or "fish".</summary>
    public string Kind { get; } = kind;
}

public sealed class GoalArgs(IPlayer player, string kind, string description) : PlayerArgs(player)
{
    /// <summary>"bounty", "expedition" or "corrupted".</summary>
    public string Kind { get; } = kind;

    /// <summary>What was completed, as the server describes it.</summary>
    public string Description { get; } = description;
}

public sealed class BuildingActionArgs(IPlayer player, string building, string action, bool accepted) : PlayerArgs(player)
{
    /// <summary>The station's building uniquename.</summary>
    public string Building { get; } = building;

    /// <summary>"CraftItem", "RepairItem", "SalvageItem", "UpgradeEnchantment", "RerollQuality", "Awaken" (or a number for another).</summary>
    public string Action { get; } = action;

    /// <summary>Whether the server started it (false: refused, with a reason sent to the client).</summary>
    public bool Accepted { get; } = accepted;
}

public sealed class PartyArgs(Guid characterId, string name, IPlayer? player, int partyId, int members, string reason)
{
    public Guid CharacterId { get; } = characterId;
    public string Name { get; } = name;

    /// <summary>The character's player when it is online.</summary>
    public IPlayer? Player { get; } = player;

    public int PartyId { get; } = partyId;

    /// <summary>The party's size after the change.</summary>
    public int Members { get; } = members;

    /// <summary>PartyJoined: "invited" or "created"; PartyLeft: "leaves", "is kicked", "disconnected".</summary>
    public string Reason { get; } = reason;
}

public sealed class ChatArgs(IPlayer player, string text) : PlayerArgs(player)
{
    public string Text { get; } = text;
}
