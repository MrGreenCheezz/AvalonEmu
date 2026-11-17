using System.Numerics;
using System.Xml.Linq;
using AlbionLocal.Server.Data;
using AlbionLocal.Server.Protocol;

namespace AlbionLocal.Server.Entities;

/// <summary>The spawn of an object the smugglers' system puts on a map (a pile, a station, a dropped carriable): no template tile of its own.</summary>
public sealed record SmugglerSpawn : ClusterObject
{
    public static SmugglerSpawn At(string cluster, int index, string tile, Vector2 at, float direction, string kind) => new()
    {
        Key = new SpawnKey(cluster, "smugglers", index), Tile = tile, Position = at, Direction = direction, Component = new XElement(kind),
    };
}

/// <summary>
/// A smugglers' crate pile (analysis/work/stageR/smugglers/SPEC.md §3, "SPEC" here): NewPiledObject (612, aum; cjb.o 0x180AAF180 → cne, the
/// fields by cnd.af(aum) 0x180B7D7E0): key 0 id, 1 position, 2 direction, 3 the piledobjects.xml index (brc.abo(int): a bre makes a cne, a
/// smuggler pile), 4 the treasuredistribution.xml TreasurePile index (bfz.abo(int), &lt; 0 none), 5 the tile the client places (cne → a9f.u
/// GetTile: "PiledObject: Tile not found" without), 6 the PileState (0 Protected, 1 Lootable, 2 Empty), 7 the crates left (cnd+0x58,
/// SmugglerCratePileFX.SetCratesCount), 8 int[] the crates' rarities (cnd+0x60), 9 a stamp (cnd+0x78, read nowhere traced: the spawn
/// [assume]), 10 the end of the protection (cnd+0x70: cnd.aqt "protected" while now &lt; it), 11 the crates being taken now (cnd+0x5C,
/// SetCratesChanneling). PiledObjectStateChanged (613, aw8; cnd.ajv 0x180B7D9B0) changes keys 6, 7, 10, 8, 11 with key 5 true when the
/// rarities changed (the view rebuilds the pile, SmugglersCratePileObjectView.OnPileStateChanged 0x18127F6B0). A world object of its own
/// (not landscape-bound), so leaving the window sends Leave.
/// </summary>
public sealed class SmugglerCratePile(long id, SmugglerSpawn spawn, PileDefinition definition, int treasureIndex, long spawnTicks) : WorldObject(id, spawn)
{
    /// <summary>PileState (Albion.Common.GameData.PiledObjectInfos).</summary>
    public const byte Protected = 0, Lootable = 1, Empty = 2;

    public PileDefinition Definition { get; } = definition;

    /// <summary>Key 4: the TreasurePile's index in the client's treasure list; −1 = none.</summary>
    public int TreasureIndex { get; } = treasureIndex;

    public byte State { get; set; } = Lootable;

    /// <summary>The crates left, each its rarity (key 8; key 7 their count); taken from the end.</summary>
    public List<byte> Crates { get; } = [];

    /// <summary>Key 11: the pickups under way.</summary>
    public int Channeling { get; set; }

    /// <summary>Key 10: the end of the protection (GameTimeStamp); 0 = none.</summary>
    public long ProtectedUntil { get; set; }

    public long SpawnTicks { get; } = spawnTicks;

    public override string Kind => "pile";
    public override string TypeName => $"{Definition.Name} ({Crates.Count} crate(s), state {State})";
    public override bool LeaveOnRange => true;

    private int[] Rarities => [.. Crates.Select(r => (int)r)];

    public override IEnumerable<AlbionEvent> BuildCreate(Player viewer)
    {
        yield return new NewPiledObjectEvent
        {
            c70 = Id, c71 = Position, c72 = Direction, c73 = Definition.Index, c74 = TreasureIndex, c75 = Spawn.Tile, c76 = State,
            c77 = Crates.Count, c78 = Rarities, c79 = SpawnTicks, c8a = ProtectedUntil, c8b = Channeling,
            Extra = new() { [3] = Definition.Index, [6] = State }, // index 0 and state 0 go out too
        };
    }

    /// <summary>PiledObjectStateChanged (613): {0 id, 1 state, 2 crates, 3 protection end, 4 rarities, 5 <paramref name="raritiesChanged"/>, 6 channeling}.</summary>
    public PiledObjectStateChangedEvent BuildUpdate(bool raritiesChanged) => new()
    {
        ddn = Id, dl5 = State, dl6 = Crates.Count, dl7 = ProtectedUntil, dl8 = Rarities, dl9 = raritiesChanged, dma = Channeling,
        Extra = new() { [1] = State, [2] = Crates.Count },
    };
}

/// <summary>
/// A smugglers' crate delivery station, the Smuggler Stash (SPEC §3.4): NewSmugglerCrateDeliveryStation (614, au0; cjb.n 0x180AAEF00): key 0 id,
/// 1 position, 2 direction. The client looks the landscape tile with the smugglerscratedeliverystation component up at the position (cjb.a), the
/// one its own resource placement put there (the *_SMUGGLER_CRATE_DELIVERY_STATION prefab of the roaming-mob nodes), and binds the object
/// to it (cnc); nothing shows without that tile. Landscape-bound as the exits: never left, sent again on re-entry.
/// </summary>
public sealed class SmugglerDeliveryStation(long id, SmugglerSpawn spawn) : WorldObject(id, spawn)
{
    public override string Kind => "smuggler station";
    public override string TypeName => Spawn.Tile;
    public override bool LeaveOnRange => false;

    public override IEnumerable<AlbionEvent> BuildCreate(Player viewer)
    {
        yield return new NewSmugglerCrateDeliveryStationEvent { da3 = Id, da4 = Position, da5 = Direction };
    }
}

/// <summary>
/// A carriable object on the ground (SPEC §4.4): NewCarriableObject (486, as9; cjb.a0 0x180A9DE30 makes the class of the carriable type, a
/// smugglers' crate a col): key 0 id, 1 position, 2 direction, 3 the carriables.xml index (bbj.abq(int)), 4 the rarity (b4q.ba: the
/// rarity's tile, a9f.u GetTile). Keys 5-12 are not sent: their meaning is not traced (the dropper, the pickup delay, the claim state) [infer:
/// the client reads them null-safe]. Taken by PickupCarriableObjectStart (op 439).
/// </summary>
public sealed class CarriableDrop(long id, SmugglerSpawn spawn, CarriableType type, byte rarity, long droppedTicks) : WorldObject(id, spawn)
{
    public CarriableType Type { get; } = type;
    public byte Rarity { get; } = rarity;
    public long DroppedTicks { get; } = droppedTicks;

    /// <summary>Who is picking it up now; null = nobody.</summary>
    public Player? TakenBy { get; set; }

    public override string Kind => "carriable";
    public override string TypeName => $"{Type.Name} rarity {Rarity}";
    public override bool LeaveOnRange => true;

    public override IEnumerable<AlbionEvent> BuildCreate(Player viewer)
    {
        yield return new NewCarriableObjectEvent { cuj = Id, cuk = Position, cul = Direction, cum = Type.Index, cun = Rarity, Extra = new() { [3] = Type.Index } };
    }
}
