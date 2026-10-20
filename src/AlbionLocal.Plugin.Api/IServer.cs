namespace AlbionLocal.Plugin.Api;

/// <summary>
/// The game as a plugin sees it: what is online and loaded, messages, spawns and world events. Players and mobs are reached through
/// <see cref="IPlayer"/> and <see cref="IMob"/>. Silver and fame are whole units here (the server stores them ×10000 internally).
/// World thread only.
/// </summary>
public interface IServer
{
    /// <summary>The joined players (humans, one per character; bots left out).</summary>
    IReadOnlyList<IPlayer> OnlinePlayers { get; }

    /// <summary>The joined player called <paramref name="name"/> (case-insensitive); null when not online.</summary>
    IPlayer? FindPlayer(string name);

    /// <summary>The clusters that are loaded (have a map in memory), by world.xml id ("0006", "1012").</summary>
    IReadOnlyList<string> LoadedClusters { get; }

    /// <summary>The clusters that have at least one player.</summary>
    IReadOnlyList<string> PopulatedClusters { get; }

    /// <summary>The living mobs of <paramref name="cluster"/> (empty when it is not loaded).</summary>
    IReadOnlyList<IMob> Mobs(string cluster);

    /// <summary>The server's UTC time.</summary>
    DateTime UtcNow { get; }

    /// <summary>The preset the server runs ("Pve" or "Official").</summary>
    string Preset { get; }

    /// <summary>A system line to every online player (the Announcement channel with Chat:Announcements, else a SystemMessage).</summary>
    void Broadcast(string text);

    /// <summary>A system line to every player in <paramref name="cluster"/>.</summary>
    void SendToZone(string cluster, string text);

    /// <summary>Whether items.xml has <paramref name="item"/> and the server can create it.</summary>
    bool ItemExists(string item);

    /// <summary>
    /// A mob of mobs.xml <paramref name="mob"/> at (<paramref name="x"/>, <paramref name="y"/>) in loaded cluster <paramref name="cluster"/>; it never
    /// respawns. Null (and a log line) when the cluster is not loaded, mobs.xml is not read yet, or there is no such mob.
    /// </summary>
    IMob? SpawnMob(string cluster, string mob, float x, float y, float facing = 0);

    /// <summary>As <see cref="SpawnMob"/>, but a one-off world boss: the boss announcement, its health and damage factors, its loot rules (content/bosses.json).</summary>
    IMob? SpawnBoss(string cluster, string mob, float x, float y, float facing = 0);

    /// <summary>
    /// Starts world event <paramref name="kind"/> (content/events.json: "invasion", "champion", …) in loaded cluster <paramref name="cluster"/> at
    /// (<paramref name="x"/>, <paramref name="y"/>), tier 1-10 or 0 for the cluster's own. Null when it started, else why not.
    /// </summary>
    string? StartEvent(string kind, string cluster, float x, float y, int tier = 0);

    /// <summary>The world event kinds content/events.json defines.</summary>
    IReadOnlyList<string> EventKinds { get; }
}

/// <summary>Something with health on a map: a player or a mob.</summary>
public interface IUnit
{
    /// <summary>The object id on its map (changes at every Join for a player, every spawn for a mob).</summary>
    long ObjectId { get; }

    /// <summary>A player's character name, a mob's mobs.xml uniquename.</summary>
    string Name { get; }

    /// <summary>The cluster it is on ("" when it is on no map).</summary>
    string Cluster { get; }

    float X { get; }
    float Y { get; }

    /// <summary>Current and maximum health.</summary>
    float Health { get; }
    float MaxHealth { get; }

    bool IsPlayer { get; }

    // ---- API v2

    /// <summary>Current and maximum energy (a mob's own energy for its spells).</summary>
    float Energy { get; }
    float MaxEnergy { get; }

    /// <summary>On a map and alive (a player: joined and not knocked down or dead; a mob: not dead).</summary>
    bool IsAlive { get; }

    /// <summary>A player in combat; a mob with a target.</summary>
    bool InCombat { get; }

    /// <summary>The spells on it now (<see cref="IGameplay.Buffs"/>).</summary>
    IReadOnlyList<IBuff> Buffs { get; }

    /// <summary>
    /// The calling plugin's data on this unit under <paramref name="key"/>, made by <paramref name="factory"/> on first use (a data bag: kept
    /// while the unit lives — a player until it leaves its map, a mob until it dies or despawns — and cleared at the plugin's unload).
    /// Callable only from inside the plugin's own handlers, timers and commands.
    /// </summary>
    T GetData<T>(string key, Func<T> factory);

    /// <summary>Sets the calling plugin's data <paramref name="key"/> on this unit.</summary>
    void SetData<T>(string key, T value);

    /// <summary>Removes the calling plugin's data <paramref name="key"/>; false when it was not there.</summary>
    bool RemoveData(string key);

    /// <summary>
    /// A timer of the calling plugin bound to this unit: <paramref name="callback"/> after <paramref name="delay"/>, then every
    /// <paramref name="delay"/> again until it has run <paramref name="repeats"/> times (0 = until cancelled). It is cancelled when the unit
    /// goes (a player leaving its map, a mob dying or despawning) and at the plugin's unload. A named timer replaces this plugin's timer of the
    /// same name on this unit.
    /// </summary>
    IUnitTimer AddTimer(TimeSpan delay, int repeats, Action<IUnitTimer> callback, string? name = null);

    /// <summary>Cancels the calling plugin's timer <paramref name="name"/> on this unit.</summary>
    bool RemoveTimer(string name);
}

/// <summary>An item as a plugin sees it: uniquename (with "@n" for an enchantment), count and quality (1 normal … 5 masterpiece).</summary>
public sealed record ItemInfo(string Name, int Count, int Quality);

/// <summary>
/// A player (a live view: every property reads the current state). After it logs out <see cref="IsOnline"/> is false; reading still works
/// (the last known values), the actions do nothing and return false/0. The same object is handed out for the same joined player, so it may
/// be kept in a dictionary while the player is online.
/// </summary>
public interface IPlayer : IUnit
{
    /// <summary>The character's Guid (stable across logins: the key to store per-player data under).</summary>
    Guid CharacterId { get; }

    /// <summary>The account level (Player, GameMaster, Admin).</summary>
    PlayerLevel Level { get; }

    /// <summary>Still joined (on a map, its connection open).</summary>
    bool IsOnline { get; }

    /// <summary>Silver and total fame, in whole units.</summary>
    long Silver { get; }
    long Fame { get; }

    /// <summary>The worn items, one per filled slot (main hand, off hand, head, armour, shoes, bag, cape, mount, potion, food).</summary>
    IReadOnlyList<ItemInfo> Equipment { get; }

    /// <summary>The inventory's stacks and items.</summary>
    IReadOnlyList<ItemInfo> Inventory { get; }

    /// <summary>How many of <paramref name="item"/> the inventory holds (all stacks).</summary>
    int CountItem(string item);

    /// <summary>A chat line to this player only (SystemMessage; <paramref name="error"/> shows it as an error).</summary>
    bool SendMessage(string text, bool error = false);

    /// <summary>Puts <paramref name="count"/> × <paramref name="item"/> into the inventory (equipment one per slot, with <paramref name="quality"/>); returns how many went in (fewer when it is full).</summary>
    int GiveItem(string item, int count = 1, int quality = 1);

    /// <summary>Takes <paramref name="count"/> × <paramref name="item"/> from the inventory (any stacks); false and nothing taken when there are not that many.</summary>
    bool TakeItem(string item, int count = 1);

    /// <summary>Gives <paramref name="silver"/> (whole units, ≥ 0).</summary>
    bool GiveSilver(long silver);

    /// <summary>Takes <paramref name="silver"/>; false and nothing taken when the player has less.</summary>
    bool TakeSilver(long silver);

    /// <summary>Gives <paramref name="fame"/> (whole units) as PvE kill fame of tier <paramref name="tier"/>: it feeds the destiny board like a kill.</summary>
    bool GiveFame(long fame, int tier = 4);

    /// <summary>Moves the player within its cluster to (<paramref name="x"/>, <paramref name="y"/>).</summary>
    bool Teleport(float x, float y, float facing = 0);

    /// <summary>Sends the player to another cluster through a loading screen, at (x, y) or at the cluster's spawn point. The move follows once the cluster is resolved.</summary>
    bool TeleportTo(string cluster, float? x = null, float? y = null);

    /// <summary>Puts spell <paramref name="spell"/> (spells.xml uniquename, a buff such as "POTION_HEAL_P3" or a food buff) on the player as its own.</summary>
    bool ApplyBuff(string spell);

    /// <summary>Takes every entry of spell <paramref name="spell"/> off the player.</summary>
    bool RemoveBuff(string spell);

    /// <summary>Whether spell <paramref name="spell"/> is on the player now.</summary>
    bool HasBuff(string spell);

    // ---- API v2: the spells in the slots

    /// <summary>The spell in <paramref name="slot"/> now (uniquename); null when the slot is empty (no item, no spell chosen).</summary>
    string? GetSlotSpell(SpellSlot slot);

    /// <summary>The spells the equipped item offers for <paramref name="slot"/> (what <see cref="SlotSpellMode.FromItemList"/> accepts; potion and food: their own spell).</summary>
    IReadOnlyList<string> GetSlotSpellOptions(SpellSlot slot);

    /// <summary>
    /// Puts <paramref name="spell"/> into <paramref name="slot"/> of the equipped item. <see cref="SlotSpellMode.FromItemList"/>: only an option of that item
    /// (<see cref="GetSlotSpellOptions"/>); <see cref="SlotSpellMode.Any"/> (experimental): any spell of the slot's kind. Without
    /// <paramref name="duration"/> it is kept like the player's own choice (saved with the item; potion and food: while that stack stays in the slot);
    /// with it, the previous spell comes back when it runs out (and at a zone change or relog the saved choice is the previous one). The client is told
    /// (the item and the action bar), the passives are worked out again, OnSlotSpellChanged fires. The engine's own checks of the player's choice
    /// (unlocks, combat) do not apply. Null when done, else why not.
    /// </summary>
    string? SetSlotSpell(SpellSlot slot, string spell, SlotSpellMode mode = SlotSpellMode.FromItemList, TimeSpan? duration = null, SlotSpellCooldown cooldown = SlotSpellCooldown.Keep);

    // ---- the extra action bars (analysis/work/stageN/extrabars/SPEC.md)

    /// <summary>
    /// The extra slots now (bar 1 = 0-5, bar 2 = 6-11; as many as the server's bars, 12 by default): spell uniquenames, null = empty. Built
    /// from the worn items' extra spells (extraspells.json) in equipment order, with the plugins' overrides on top.
    /// </summary>
    IReadOnlyList<string?> GetExtraSlots();

    /// <summary>
    /// Puts active spell <paramref name="spell"/> into extra slot <paramref name="index"/> (0-based) over what the gear gives; null or "" takes the
    /// override away (the gear's spell comes back). Kept in memory until cleared or the server restarts (zone changes keep it, nothing is saved).
    /// The same spell elsewhere in the bars leaves that slot (a spell is in the bars once). The mod's client is told; OnExtraBarChanged fires.
    /// Null when done, else why not.
    /// </summary>
    string? SetExtraSlot(int index, string? spell);
}

/// <summary>A mob (a live view; after its death <see cref="IUnit.IsAlive"/> is false).</summary>
public interface IMob : IUnit
{
    /// <summary>The tier (1-8) of mobs.xml.</summary>
    int Tier { get; }

    /// <summary>mobs.xml's category ("standard", "champion", "miniboss", "boss", …; "" when none).</summary>
    string Category { get; }

    /// <summary>The fame a kill gives before rates and factors (whole units).</summary>
    double Fame { get; }

    /// <summary>A world boss (a boss slot's or a GM's / plugin's one-off).</summary>
    bool IsWorldBoss { get; }

    /// <summary>A world event's wave mob.</summary>
    bool IsEventMob { get; }
}
