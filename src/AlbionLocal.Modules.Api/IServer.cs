namespace AlbionLocal.Modules;

/// <summary>
/// The game as a module sees it: its data (items, spells, mobs, zones), what is online and loaded, messages, spawns and world events. Players and mobs are reached through
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

    /// <summary>The rules the server runs with.</summary>
    GamePreset Preset { get; }

    /// <summary>A system line to every online player (the Announcement channel with Chat:Announcements, else a SystemMessage).</summary>
    void Broadcast(string text);

    /// <summary>A system line to every player in <paramref name="cluster"/>.</summary>
    void SendToZone(string cluster, string text);

    /// <summary>Whether items.xml has <paramref name="item"/> and the server can create it.</summary>
    bool ItemExists(string item);

    /// <summary>The kind of item called <paramref name="name"/> (a uniquename, "@n" for an enchantment); null when items.xml has none.</summary>
    ItemType? Item(string name);

    /// <summary>
    /// An item by its parts: <c>Server.Item("MAIN_SWORD", 6, 2)</c> is T6_MAIN_SWORD@2, and <c>Server.Item(e.Item.Family, e.Item.Tier + 1)</c> is the next tier of one. Null when there is none such.
    /// </summary>
    ItemType? Item(string family, int tier, int enchantment = 0);

    /// <summary>Every kind of item of items.xml, to search: <c>Server.Items.Where(i => i.Weapon == WeaponType.Bow &amp;&amp; i.Tier == 8)</c>. The enchantments are entries of their own.</summary>
    IReadOnlyList<ItemType> Items { get; }

    /// <summary>The spell called <paramref name="name"/> of spells.xml; null when it has none.</summary>
    SpellInfo? Spell(string name);

    /// <summary>The kind of mob called <paramref name="name"/> of mobs.xml; null when it has none or it is not read yet.</summary>
    MobType? Mob(string name);

    /// <summary>The zone with cluster id <paramref name="cluster"/>; null when world.xml has none or it is not read yet.</summary>
    ZoneInfo? Zone(string cluster);

    /// <summary>
    /// A mob of mobs.xml <paramref name="mob"/> at (<paramref name="x"/>, <paramref name="y"/>) in loaded cluster <paramref name="cluster"/>; it never
    /// respawns. Null (and a log line) when the cluster is not loaded, mobs.xml is not read yet, there is no such mob, or the call comes from inside a damage or heal
    /// hook (spawn a moment later: <c>Timers.After(TimeSpan.Zero, ...)</c>).
    /// </summary>
    IMob? SpawnMob(string cluster, string mob, float x, float y, float facing = 0);

    /// <summary>As <see cref="SpawnMob"/>, but a one-off world boss: the boss announcement, its health and damage factors, its loot rules (content/bosses.json). Not from inside a damage or heal hook either.</summary>
    IMob? SpawnBoss(string cluster, string mob, float x, float y, float facing = 0);

    /// <summary>
    /// A mob type of the module's own, called <paramref name="name"/>, a copy of mobs.xml's <paramref name="basedOn"/> with the numbers <paramref name="setup"/> sets. The hooks
    /// (<c>[ForId]</c>), <see cref="SpawnMob"/>, <see cref="SpawnBoss"/> and <see cref="IGameplay.Summon"/> take its name, and it touches no mob of the base type. The clients see the
    /// base type's model, name and animations: those live in their own files. It goes when the module unloads, and its living mobs with it. Call it in OnLoad; null when
    /// the name is taken or there is no such base type (a line in the log).
    /// </summary>
    /// <example>
    /// <code><![CDATA[
    /// protected override void OnLoad() => Server.DefineMob("MY_WARDEN", "T4_MOB_ROAMING_UNDEAD_SOLDIER_MINIBOSS", m =>
    /// {
    ///     m.MaxHealth = 250000;
    ///     m.AttackDamage = 900;
    ///     m.RunSpeed = 7;
    ///     m.NoRotation = true;      // only what this module tells it to cast
    /// });
    /// ]]></code>
    /// </example>
    MobType? DefineMob(string name, string basedOn, Action<MobDefinition>? setup = null);

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

    /// <summary>The zone it is on, as it is now: its kind, colour and PvP rules. On no map its <see cref="ZoneInfo.Cluster"/> is "".</summary>
    ZoneInfo Zone { get; }

    /// <summary>The position's X, in metres on the map.</summary>
    float X { get; }
    /// <summary>The position's Y, in metres on the map.</summary>
    float Y { get; }

    /// <summary>Current health.</summary>
    float Health { get; }
    /// <summary>Maximum health.</summary>
    float MaxHealth { get; }

    /// <summary>It is an <see cref="IPlayer"/>; otherwise an <see cref="IMob"/>.</summary>
    bool IsPlayer { get; }

    /// <summary>Current energy (a mob's own energy for its spells).</summary>
    float Energy { get; }
    /// <summary>Maximum energy.</summary>
    float MaxEnergy { get; }

    /// <summary>On a map and alive (a player: joined and not knocked down or dead; a mob: not dead).</summary>
    bool IsAlive { get; }

    /// <summary>A player in combat; a mob with a target.</summary>
    bool InCombat { get; }

    /// <summary>The spells on it now (<see cref="IGameplay.Buffs"/>).</summary>
    IReadOnlyList<IBuff> Buffs { get; }

    /// <summary>
    /// The calling module's data on this unit under <paramref name="key"/>, made by <paramref name="factory"/> on first use (a data bag: kept
    /// while the unit lives — a player until it leaves its map, a mob until it dies or despawns — and cleared at the module's unload).
    /// Callable only from inside the module's own handlers, timers and commands.
    /// </summary>
    T GetData<T>(string key, Func<T> factory);

    /// <summary>Sets the calling module's data <paramref name="key"/> on this unit.</summary>
    void SetData<T>(string key, T value);

    /// <summary>Removes the calling module's data <paramref name="key"/>; false when it was not there.</summary>
    bool RemoveData(string key);

    /// <summary>The calling module's data of class <typeparamref name="T"/> on this unit, made on first use: <c>player.Data&lt;Rage&gt;().Stacks++</c>. It is <see cref="GetData{T}"/> with the class as the key.</summary>
    T Data<T>() where T : class, new() => GetData(typeof(T).FullName!, static () => new T());

    /// <summary>Removes the calling module's data of class <typeparamref name="T"/>; false when it was not there.</summary>
    bool RemoveData<T>() where T : class => RemoveData(typeof(T).FullName!);

    /// <summary>
    /// A timer of the calling module bound to this unit: <paramref name="callback"/> after <paramref name="delay"/>, then every
    /// <paramref name="delay"/> again until it has run <paramref name="repeats"/> times (0 = until cancelled). It is cancelled when the unit
    /// goes (a player leaving its map, a mob dying or despawning) and at the module's unload. A named timer replaces this module's timer of the
    /// same name on this unit.
    /// </summary>
    IUnitTimer AddTimer(TimeSpan delay, int repeats, Action<IUnitTimer> callback, string? name = null);

    /// <summary>Cancels the calling module's timer <paramref name="name"/> on this unit.</summary>
    bool RemoveTimer(string name);
}

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

    /// <summary>A server-side bot (a player bot or a hired companion) and not a human: messages go nowhere.</summary>
    bool IsBot { get; }

    /// <summary>Whether this player and <paramref name="other"/> are in one party.</summary>
    bool IsInPartyWith(IPlayer other);

    /// <summary>Still joined (on a map, its connection open).</summary>
    bool IsOnline { get; }

    /// <summary>The silver, in whole units.</summary>
    long Silver { get; }
    /// <summary>The total fame, in whole units.</summary>
    long Fame { get; }

    /// <summary>The item in <paramref name="slot"/>; null when the slot is empty.</summary>
    ItemInfo? Worn(EquipSlot slot);

    /// <summary>The weapon or tool in the main hand; null when the hand is empty.</summary>
    ItemInfo? MainHand => Worn(EquipSlot.MainHand);
    /// <summary>The off-hand item; null when none.</summary>
    ItemInfo? OffHand => Worn(EquipSlot.OffHand);
    /// <summary>The helmet; null when none.</summary>
    ItemInfo? Head => Worn(EquipSlot.Head);
    /// <summary>The armour; null when none.</summary>
    ItemInfo? Armor => Worn(EquipSlot.Armor);
    /// <summary>The shoes; null when none.</summary>
    ItemInfo? Shoes => Worn(EquipSlot.Shoes);
    /// <summary>The bag; null when none.</summary>
    ItemInfo? Bag => Worn(EquipSlot.Bag);
    /// <summary>The cape; null when none.</summary>
    ItemInfo? Cape => Worn(EquipSlot.Cape);
    /// <summary>The mount; null when none.</summary>
    ItemInfo? Mount => Worn(EquipSlot.Mount);
    /// <summary>The potion; null when none.</summary>
    ItemInfo? Potion => Worn(EquipSlot.Potion);
    /// <summary>The food; null when none.</summary>
    ItemInfo? Food => Worn(EquipSlot.Food);

    /// <summary>The worn items, one per filled slot in slot order (so <c>Equipment[0]</c> need not be the main hand: use <see cref="MainHand"/> or <see cref="Worn"/>).</summary>
    IReadOnlyList<ItemInfo> Equipment { get; }

    /// <summary>The inventory's stacks and items.</summary>
    IReadOnlyList<ItemInfo> Inventory { get; }

    /// <summary>Whether a worn item is <paramref name="nameOrMask"/> (see <see cref="ItemType.Is"/>): a uniquename with or without "@n", or a mask with * and ?.</summary>
    bool IsWearing(string nameOrMask) => Equipment.Any(i => i.Is(nameOrMask));

    /// <summary>How many of <paramref name="item"/> the inventory holds (all stacks). The name is exact: "@1" is another item.</summary>
    int CountItem(string item);

    /// <summary>A chat line to this player only (SystemMessage; <paramref name="error"/> shows it as an error).</summary>
    bool SendMessage(string text, bool error = false);

    /// <summary>Puts <paramref name="count"/> × <paramref name="item"/> into the inventory (equipment one per slot, with <paramref name="quality"/>); returns how many went in (fewer when it is full, 0 with a hint in the log when there is no such item).</summary>
    int GiveItem(string item, int count = 1, ItemQuality quality = ItemQuality.Normal);

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
}

/// <summary>
/// A mob (a live view; after its death <see cref="IUnit.IsAlive"/> is false). Its actions, called from inside a damage, heal or <c>OnBeforeAggro</c> hook, wait for the next tick
/// and return true: whether they worked is not known yet then.
/// </summary>
public interface IMob : IUnit
{
    /// <summary>The kind of mob it is (<see cref="IUnit.Name"/> is its uniquename).</summary>
    MobType Type { get; }

    /// <summary>The tier (1-8) of mobs.xml.</summary>
    int Tier { get; }

    /// <summary>Its rank and role.</summary>
    MobCategory Category { get; }

    /// <summary>The fame a kill gives before rates and factors (whole units).</summary>
    double Fame { get; }

    /// <summary>A world boss (a boss slot's or a GM's / module's one-off).</summary>
    bool IsWorldBoss { get; }

    /// <summary>A world event's wave mob.</summary>
    bool IsEventMob { get; }

    /// <summary>Its current target; null when it has none or the target has left its map.</summary>
    IUnit? Target { get; }

    /// <summary>The players on its threat list that it can still fight, the most threat first. The AI's target is not always the first: it changes only for clearly more threat.</summary>
    IReadOnlyList<ThreatEntry> Threat { get; }

    /// <summary>The spell it is casting or channelling; null when none.</summary>
    SpellInfo? CurrentSpell { get; }

    /// <summary>It is casting or channelling.</summary>
    bool IsCasting => CurrentSpell is not null;

    /// <summary>It is walking.</summary>
    bool IsMoving { get; }

    /// <summary>
    /// One auto-attack at <paramref name="target"/>, with its animation and its time between swings (it takes the target first when it has another): what a mob driven by a module
    /// (<c>Mobs.OnBeforeThink</c> cancelled) does instead of the swings of the AI. False when it cannot now: the next swing is not due, the target is out of reach or sight,
    /// it is casting, stunned or has no attack, or the target cannot be fought.
    /// </summary>
    bool Attack(IPlayer target);

    /// <summary>Whether <paramref name="unit"/> is within the reach of its auto-attack: its attack range and both units' sizes. Walk to a target only until it is.</summary>
    bool InReach(IUnit unit);

    /// <summary>Stops its walk. False when it is not walking.</summary>
    bool Stop();

    /// <summary>Turns it towards (<paramref name="x"/>, <paramref name="y"/>); a walk stops.</summary>
    bool FaceTo(float x, float y);

    /// <summary>
    /// Makes <paramref name="player"/> its target through its threat list: an idle mob starts to fight it (<c>Mobs.OnAggro</c>), a fighting one takes it, with threat
    /// up to the highest on the list so that it keeps it until another player gains more. False when it cannot (dead, on its way home, the player elsewhere or down).
    /// </summary>
    bool SetTarget(IPlayer player);

    /// <summary><paramref name="threat"/> for <paramref name="player"/> (negative takes some off): an idle mob starts to fight it. False when it cannot (dead, on its way home, the player elsewhere or down).</summary>
    bool AddThreat(IPlayer player, double threat);

    /// <summary>
    /// Casts a spells.xml active or toggle spell through the mobs' own cast path (CastStart for the clients, its cast time, energy, hit, channel; <c>Spells.OnCast</c>).
    /// The target: <paramref name="target"/>, else its current target; a self spell itself; a ground spell at (<paramref name="x"/>, <paramref name="y"/>), else at the target.
    /// False when it cannot: dead, stunned, silenced, on a forced path, casting already, the mob spells off, not enough energy, no such spell, no valid target, out of range or sight. No
    /// cooldown applies. <paramref name="castSeconds"/>: its cast time instead of spells.xml's, the time players have to get away. To run a spell at once use <see cref="IGameplay.CastSpell(IMob, string, IUnit?, float?, float?)"/>.
    /// A mob without an &lt;AI&gt; rotation goes on chasing and swinging during a cast unless <c>Mobs.OnBeforeThink</c> cancels its AI, and its cast cannot be interrupted.
    /// </summary>
    bool CastSpell(string spell, IUnit? target = null, float? x = null, float? y = null, double? castSeconds = null);

    /// <summary>
    /// Walks to (<paramref name="x"/>, <paramref name="y"/>), round walls where the map has a walk grid, at its walk speed (idle) or its attack speed (otherwise). The engine's AI gives
    /// the next orders unless <c>Mobs.OnBeforeThink</c> cancels it. False when it did not start: dead, unable to walk, rooted, stunned, no way there, or there already.
    /// </summary>
    bool MoveTo(float x, float y);

    /// <summary>Takes it off the map (no corpse, no rewards, no loot; a fight's players leave combat). A spawn point's mob comes back after its respawn time unless <paramref name="respawn"/> is false. False when it is gone already.</summary>
    bool Despawn(bool respawn = true);
}
