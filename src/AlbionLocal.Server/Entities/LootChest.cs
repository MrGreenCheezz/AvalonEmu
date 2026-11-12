using System.Reflection;
using AlbionLocal.Server.Data;
using AlbionLocal.Server.Protocol;

namespace AlbionLocal.Server.Entities;

/// <summary>
/// A loot chest (stageG dungeons SPEC §7 "Chests", stageH bosses SPEC §3.1-3.2): on a lootchestspawnpoint tile, the chest its
/// LootChestSpawnpoint drew by weight with one of its rare states, as NewLootChest 392 (aue; the client's clj, filled in clj.af
/// 0x180B47580) and UpdateLootChest 393 (a1a, clj.ajv case 0x189). Opened by UseLootChest (op 253) or straight by RegisterToObject:
/// its loot is rolled once, at the first opening (<see cref="Roll"/>), and then it is taken from like a mob's bag
/// (Network/LootHandler: AttachItemContainer, InventoryMoveGivenItems; it leaves when empty). The random dungeon chests never respawn
/// (respawn="Never" on every dungeon spawnpoint) and anyone on the floor may open them. A <see cref="Personal"/> chest (the camp
/// chests of stageH) has a ChestState per viewer (keys 23/24, <see cref="SetViewerState"/>). Touched only under <see cref="World.Sync"/>.
/// </summary>
public sealed class LootChest(long id, LootChestSpawn spawn, LootChestTemplate template, ChestRareState state, string tile, byte lockState, long spawnTicks)
    : LootObject(id, spawn)
{
    public LootChestTemplate Template { get; private set; } = template;

    /// <summary>The rare state drawn at the spawn: its rarity (key 21) and its loot by tier.</summary>
    public ChestRareState State { get; private set; } = state;

    /// <summary>Key 4, the tile the client places (<see cref="LootChestTemplate.TileFor"/>): never empty (no tile, no chest).</summary>
    public string Tile { get; private set; } = tile;

    /// <summary>
    /// A reroll of an unopened chest (stageH bosses SPEC §4.2 rule 5): another chest of its spawnpoint, rare state and tile, sent as a
    /// full NewLootChest with the same id.
    /// </summary>
    internal void Redraw(LootChestTemplate template, ChestRareState state, string tile) => (Template, State, Tile) = (template, state, tile);

    /// <summary>Key 18: the LootChestSpawnpoint's name (the client's minimap and tooltip rules), not an event name.</summary>
    public string SpawnpointName => spawn.SpawnBehaviour;

    /// <summary>
    /// ChestState (key 5; key 23 too unless <see cref="Personal"/>): BossLocked 0, LockedByTimer 1, … Unlocked 6, Open 7, Empty 8.
    /// Dungeons:ChestState at the spawn (Unlocked [assume]: the SPEC gives no lock rule; a boss chest BossLocked, Dungeons:BossChests), Open once opened, Empty when its roll gave
    /// nothing (<see cref="SetState"/>).
    /// </summary>
    public byte LockState { get; private set; } = lockState;

    /// <summary>GameTimeStamp of the spawn (key 6).</summary>
    public long SpawnTicks { get; } = spawnTicks;

    /// <summary>GameTimeStamp of the last change of <see cref="LockState"/> (key 7 / 393 key 2: the start of a LockedByTimer countdown).</summary>
    public long StateTicks { get; private set; } = spawnTicks;

    /// <summary>GameTimeStamp of the last opening (key 13 / 393 key 8, et; LootChestOpened sets it in the client); MinValue before.</summary>
    public long OpenedTicks { get; internal set; } = long.MinValue;

    /// <summary>A personal chest (key 22): its state is per viewer (keys 23/24), key 15 1 and none of the static keys 8, 10, 13, 16, 26, 28.</summary>
    public bool Personal { get; init; }

    /// <summary>Whether its spawnpoint has a MinimapDisplay: key 19 is read only then (stageH bosses SPEC §3.1).</summary>
    public bool MinimapDisplay { get; init; }

    /// <summary>A factor on its loot (a world event's reward chest: RewardFactor, <see cref="Systems.WorldEvents"/>); 1 for every other chest.</summary>
    public double LootFactor { get; init; } = 1;

    /// <summary>Stage I (a player invasion's reward chest): the tier its loot is rolled at; null = the cluster's.</summary>
    public int? LootTier { get; init; }

    /// <summary>Stage I: the tier its rolled items are moved to where items.xml has them; null = kept.</summary>
    public int? ItemTier { get; init; }

    /// <summary>Stage I: enchantment levels added to its rolled items where items.xml has the variant.</summary>
    public int Enchant { get; init; }

    /// <summary>Stage I: an item more in the loot of these characters (Drops.Won: an invasion ticket), by character Guid.</summary>
    public Dictionary<Guid, string> Bonus { get; } = [];

    /// <summary>
    /// Public issue #19 (coffers SPEC §3): a contested treasure's PvP circle of <see cref="LootChestTemplate.PvpZoneRadius"/> is up: NewLootChest key 27
    /// (aue.c4w → clj+0x100, the barrier's state; UnrestrictedPvpZoneStatus 309 changes it), and the players inside are unrestricted
    /// (<see cref="Systems.TreasureCoffers"/>). False for every other chest (key 27 left out, as captured).
    /// </summary>
    public bool PvpZone { get; set; }

    /// <summary>
    /// Public issue #23: a listed treasure's Guid (LocalTreasuresUpdate key 1, <see cref="Systems.TreasureCoffers"/>), sent as NewLootChest key 20:
    /// clj.af 0x180B47580 parses key 20 into clj+0xD4, by which LootChestObjectView.OnMouseOverEnter 0x181A5A180 finds the treasure (cjm.a(Guid))
    /// for its tooltip. Null for every other chest (key 20 left out).
    /// </summary>
    public Guid? TreasureGuid { get; set; }

    /// <summary>Protected by mobs (key 19 with <see cref="MinimapDisplay"/>; event 395 changes it).</summary>
    public bool ProtectedByMobs { get; set; }

    /// <summary>
    /// StageH PvE (Dungeons:BossChests): the object ids of the bosses guarding a BossLocked dungeon chest, fixed at its first check
    /// (<see cref="Systems.RandomDungeons.BossChecks"/>); null before it and for every other chest.
    /// </summary>
    internal HashSet<long>? Guards { get; set; }

    /// <summary>The rolled loot (an empty container until the first opening).</summary>
    public override Container Items => _items;

    private Container _items = new(Guid.NewGuid(), 0);

    /// <summary>
    /// The ChestState of a viewer of a <see cref="Personal"/> chest with no state of its own (its local quest not done: 4 on a camp
    /// chest), from the spawn (<see cref="SpawnTicks"/>). Set at the spawn and never changed by <see cref="Roll"/>: a second player
    /// still sees it locked after the first one opened it (stageH bosses SPEC §4.4, §8.10).
    /// </summary>
    public byte DefaultViewerState { get; init; } = lockState;

    /// <summary>The viewer states of a <see cref="Personal"/> chest by character Guid, with their stamps; the others have <see cref="DefaultViewerState"/>.</summary>
    private readonly Dictionary<Guid, (byte State, long Ticks)> _viewers = [];

    /// <summary>Whether the loot has been rolled (<see cref="Roll"/>).</summary>
    public bool Rolled { get; private set; }

    /// <summary>The loot of each player that opened a <see cref="Personal"/> chest (rolled once per player, stageH bosses SPEC §3.5).</summary>
    private readonly Dictionary<Guid, Container> _own = [];

    private static readonly Container NoItems = new(Guid.Empty, 0);

    /// <summary>The container <paramref name="p"/> takes from: its own on a personal chest (empty before its opening), else the chest's.</summary>
    public override Container ItemsFor(Player p) => !Personal ? Items : _own.GetValueOrDefault(p.Character.Id) ?? NoItems;

    /// <summary>Whether <paramref name="p"/>'s loot has been rolled: its own on a personal chest, else the chest's.</summary>
    public bool RolledFor(Player p) => Personal ? _own.ContainsKey(p.Character.Id) : Rolled;

    /// <summary>
    /// The loot of <paramref name="p"/>: on a personal chest its own container and viewer state Open (Empty when nothing came up), from
    /// <paramref name="now"/>; on the others <see cref="Roll"/>.
    /// </summary>
    internal void RollFor(Player p, IReadOnlyList<Item> items, long now)
    {
        if (!Personal)
        {
            Roll(items, now);
            return;
        }
        if (_own.ContainsKey(p.Character.Id)) return;
        var c = new Container(Guid.NewGuid(), Math.Max(1, items.Count));
        for (var i = 0; i < items.Count; i++) c.Put(i, items[i]);
        _own[p.Character.Id] = c;
        SetViewerState(p, (byte)(items.Count == 0 ? 8 : 7), now);
    }

    /// <summary>A personal chest's state of character <paramref name="id"/> cleared (the camp's cleanup): it sees <see cref="DefaultViewerState"/> again.</summary>
    internal void ResetViewer(Guid id)
    {
        _viewers.Remove(id);
        _own.Remove(id);
    }

    /// <summary>The players of a personal chest with a state of their own.</summary>
    public IEnumerable<Guid> Viewers => _viewers.Keys;

    /// <summary>
    /// The ChestState <paramref name="viewer"/> sees (key 23) and its stamp (key 24): on a personal chest its own, else
    /// <see cref="DefaultViewerState"/> from the spawn; on the others the chest's.
    /// </summary>
    public (byte State, long Ticks) ViewerState(Player viewer) =>
        !Personal ? (LockState, StateTicks) : _viewers.TryGetValue(viewer.Character.Id, out var own) ? own : (DefaultViewerState, SpawnTicks);

    /// <summary>The ChestState of <paramref name="viewer"/> on a personal chest (its local quest done: Unlocked) from <paramref name="now"/>.</summary>
    public void SetViewerState(Player viewer, byte state, long now) => SetViewerState(viewer.Character.Id, state, now);

    /// <summary>The same by character Guid (one not on the map now, e.g. a world event's rewarded player away at the win).</summary>
    public void SetViewerState(Guid character, byte state, long now) => _viewers[character] = (state, now);

    /// <summary>The chest's ChestState from <paramref name="now"/> (key 5, and key 7 its stamp).</summary>
    public void SetState(byte state, long now) => (LockState, StateTicks) = (state, now);

    /// <summary>Key 21 for a viewer in <paramref name="viewerState"/>: −1 while the rarity is hidden (hiderarityuntil), else the drawn one.</summary>
    public int RarityFor(byte viewerState) => Template.HidesRarity(viewerState) ? -1 : State.Rarity;

    /// <summary>Unlocked or open for <paramref name="p"/> (key 23 on a personal chest, else key 5): it may open it (the loot is rolled at the first opening).</summary>
    public override bool MayOpen(Player p) => ViewerState(p).State is >= 6 and not 8;

    /// <summary>
    /// The loot, once: <paramref name="items"/> (rolled by the caller with the floor's tier and factors) in a container of their own;
    /// Open, or Empty when nothing came up (it stays, shown empty and not to be opened again [assume]), from <paramref name="now"/>.
    /// A personal chest rolls per player instead (<see cref="RollFor"/>).
    /// </summary>
    internal void Roll(IReadOnlyList<Item> items, long now)
    {
        if (Rolled) return;
        Rolled = true;
        _items = new Container(Guid.NewGuid(), Math.Max(1, items.Count));
        for (var i = 0; i < items.Count; i++) _items.Put(i, items[i]);
        SetState((byte)(items.Count == 0 ? 8 : 7), now);
        OpenedTicks = now;
    }

    /// <summary>
    /// UpdateLootChest (a1a) for <paramref name="viewer"/>: the complete state, as the NewLootChest keys in brackets. The client assigns
    /// every key from the message (clj.ajv case 0x189, stageH bosses SPEC §3.2), so a key left out clears its field (the rarity to 0 and
    /// shown, a personal chest to non-personal, the viewer state to BossLocked): keys 0-10 and 12-19 always go, also when 0 or false;
    /// 11 (as 16, not read) as in the create. Built per viewer (keys 13, 16, 17), so send it to each observer.
    /// </summary>
    public UpdateLootChestEvent Update(Player viewer)
    {
        var (mine, since) = ViewerState(viewer);
        var e = new UpdateLootChestEvent
        {
            ddn = Id,                                   // [0]
            d4g = LockState,                            // [1] ChestState (5)
            d4h = StateTicks,                           // [2] its stamp (7)
            d4i = [], d4j = [], d4k = [],               // [3] [4] [5] the Guid lists (8, 9, 10): none
            d4l = false, d4m = false,                   // [6] (11) [7] restricted (12): a change to false fires the "unrestricted" callback
            // [8] et (13): on a personal chest not yet opened 0, what the client holds from its create (key 13 left out, as
            // captured, so the ts default); a static chest's create sends MinValue
            // (a personal chest's opening is the viewer's own: the stamp of its Open or Empty)
            d4n = !Personal ? OpenedTicks : mine >= 7 ? since : 0,
            d4o = false,                                // [9] (14)
            d4p = Personal ? 1 : 0,                     // [10] (15)
            d4q = Personal ? 0 : 10000,                 // [11] a7m (16), not read
            d4r = true,                                 // [12] (17)
            d4s = RarityFor(mine),                      // [13] rarity (21), −1 hidden
            d4t = Personal,                             // [14] personal (22)
            d4u = !Personal,                            // [15] (26)
            d4v = mine,                                 // [16] the viewer's ChestState (23)
            d4w = since,                                // [17] its stamp (24)
            d4x = 0,                                    // [18] carriables (29)
            d4y = 0,                                    // [19] FlaggingStatus (31)
        };
        e.Extra = Zeros(e, [1, 2, 6, 7, 8, 9, 10, 13, 14, 15, 16, 17, 18, 19]);
        return e;
    }

    public override string Kind => "loot chest";

    public override string TypeName => $"{Template.Name} ({State.State}{(Rolled ? $", {Items.Count} item(s)" : "")})";

    /// <summary>
    /// NewLootChest (392, aue), the keys clj.af reads (stageH bosses SPEC §3.1, the captured key sets of 0204): 0 id, 1 position,
    /// 2 rotation (absent at 0), 3 the LootChest uniquename (looked up in lootchests.xml, an unknown one throws), 4 the tile (required:
    /// without it the client places nothing), 5 the ChestState, 6 the spawn stamp, 7 the state's stamp (the start of a LockedByTimer
    /// countdown), 17 true, 18 the spawnpoint name, 19 protected (with a MinimapDisplay), 21 the rarity (−1 hidden), 23 the viewer's
    /// ChestState, 29 carriables 0 and 31 FlaggingStatus 0. A static chest adds 8 and 10 (empty Guid lists), 13 MinValue (et), 16
    /// 10000, 26 and 28 true; a personal one 15 1, 22 true and 24 the viewer's stamp; a contested treasure 27 true (<see cref="PvpZone"/>,
    /// issue #19), 20 a listed treasure's Guid (issue #23). Keys 9, 11, 12, 14, 20 and 27 (otherwise), 25 and 30 stay out (as captured).
    /// </summary>
    public override IEnumerable<AlbionEvent> BuildCreate(Player viewer)
    {
        var (mine, since) = ViewerState(viewer);
        var e = new NewLootChestEvent
        {
            c35 = Id,                                   // [0]
            c36 = Position,                             // [1]
            c37 = Direction,                            // [2] absent at 0
            c38 = Template.Name,                        // [3] lootchests.xml uniquename
            c39 = Tile,                                 // [4] the tile to place
            c4a = LockState,                            // [5] ChestState → clj+0x88
            c4b = SpawnTicks,                           // [6]
            c4c = StateTicks,                           // [7] the timer lock's start
            c4m = true,                                 // [17]
            c4n = SpawnpointName,                       // [18]
            c4o = ProtectedByMobs && MinimapDisplay,    // [19] read only with a MinimapDisplay
            c4q = RarityFor(mine),                      // [21] rarity, −1 hidden
            c4s = mine,                                 // [23] ChestState for this viewer → clj+0x8C
            c4y = 0,                                    // [29] carriables delivered
            c40 = 0,                                    // [31] FlaggingStatus
            c4w = PvpZone,                              // [27] a treasure's PvP circle up (issue #19; absent = false otherwise)
            c4p = TreasureGuid?.ToString(),             // [20] a listed treasure's Guid (issue #23; absent otherwise)
        };
        if (Personal)
        {
            e.c4k = 1;                                  // [15]
            e.c4r = true;                               // [22] personal
            e.c4t = since;                              // [24] the viewer's stamp (Unlocked: the unlock time)
            if (mine >= 7) e.c4i = since;                // [13] its own opening
        }
        else
        {
            e.c4d = []; e.c4f = [];                     // [8] [10] none may open / has opened (as captured)
            e.c4i = OpenedTicks;                        // [13] MinValue until opened
            e.c4l = 10000;                              // [16] a7m
            e.c4v = true; e.c4x = true;                 // [26] [28]
        }
        e.Extra = Zeros(e, [5, 21, 23, 29, 31]);
        yield return e;
    }

    /// <summary>
    /// The [Param] fields of <paramref name="e"/> with these <paramref name="keys"/> whose value is 0 or false, which the serializer
    /// would leave out, as Extra parameters: sent as they are (the type of the field: bool, byte, short, int, long, float).
    /// </summary>
    private static Dictionary<byte, object?> Zeros(AlbionEvent e, byte[] keys)
    {
        var zeros = new Dictionary<byte, object?>();
        foreach (var f in e.GetType().GetFields())
            if (f.GetCustomAttribute<ParamAttribute>() is { } p && keys.Contains(p.Key) && f.GetValue(e) is { } v && f.FieldType.IsValueType
                && AlbionSerializer.ToWire(v) is null)
                zeros[p.Key] = v;
        return zeros;
    }
}
