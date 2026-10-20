using System.Numerics;
using System.Xml.Linq;
using AlbionLocal.Server.Data;
using AlbionLocal.Server.Game;
using AlbionLocal.Server.Protocol;

namespace AlbionLocal.Server.Entities;

// The objects of an island instance that come from the island store, not from the template (stageH islands SPEC §3, "SPEC" below):
// the player buildings (farms, herb gardens) and the crops planted in them. Both are NewBuilding 45 like the NPC buildings; a crop
// follows with its FarmableObjectInfo 200.

/// <summary>The spawn record of an island object made from the store (no template tile behind it).</summary>
public sealed record IslandObjectSpawn : ClusterObject
{
    public static IslandObjectSpawn At(string cluster, string slot, int n, string tile, Vector2 position, float direction) => new()
    {
        Key = new SpawnKey(cluster, slot, n), Tile = tile, Position = position, Direction = direction, Component = new XElement("island"),
    };
}

/// <summary>
/// A player building on an island (NewBuilding 45, SPEC §3.1 [cap 113433 T1_HERBGARDEN]): {0 id, 1 Guid, 2 type, 3 the uniquename,
/// 4 centre, 7 durability × 10000, 8 built, 9 owner, 11/12 owner name, 18 10000, 19 -1, 20/22 the map load, 27 true, 29 byte 0} and
/// its AccessStatus preset 0 (after JoinFinished, or right after it). Left out of range like an NPC building.
/// </summary>
public sealed class PlayerBuilding(long id, IslandObjectSpawn spawn, IslandBuilding building, BuildingTemplate template, Island island, long loadTime,
    Func<Guid, (Guid Id, string Name)?> guildOf) : WorldObject(id, spawn), IAccessStatusObject
{
    public IslandBuilding Building { get; } = building;

    public BuildingTemplate Template { get; } = template;

    public Island Island { get; } = island;

    public override string Kind => "player building";
    public override string TypeName => Template.Name;
    public override bool LeaveOnRange => true;

    /// <summary>The seeds its &lt;farmableitems&gt; lists (buildings.xml: the farm the vegetables, the herb garden the herbs); empty = no farm.</summary>
    public IReadOnlyList<string> FarmableItems { get; } =
        [.. template.Element.Element("farmableitems")?.Elements("farmableitem").Select(e => (string?)e.Attribute("uniquename")).OfType<string>() ?? []];

    public override IEnumerable<AlbionEvent> BuildCreate(Player viewer)
    {
        yield return new NewBuildingEvent
        {
            ObjectId = Id,                                              // [0]
            BuildingGuid = Building.Id,                                 // [1]
            TypeId = Template.Id,                                       // [2] T1_HERBGARDEN 344
            Tile = Template.Name,                                       // [3] the uniquename
            Position = Position,                                        // [4] its centre
            Direction = Direction,                                      // [5] left out at 0
            Durability = (long)Math.Round(Template.Durability * 10000), // [7] 12000 → 120000000
            DurabilityStamp = Building.Built,                           // [8]
            cto = Island.Owner,                                         // [9] the owner character
            ctp = guildOf(Island.Owner)?.Id ?? Guid.Empty,              // [10] its guild (left out without one)
            ctq = Island.OwnerName, ctr = Island.OwnerName,             // [11], [12] the owner's name
            Nutrition = (long)Math.Round(Template.NutritionStorage * 10000), // [18] 10000
            ItemTypeId = -1,                                            // [19]
            LoadTime = loadTime, CraftCapacityStamp = loadTime,         // [20], [22] the map load
            ct6 = true,                                                 // [27]
            Extra = new() { [29] = (byte)0 },                           // [29]
        };
    }

    /// <summary>AccessStatus {0 id, 1 preset 0 (left out), 4 the groups, 5 owner / noaccess / noaccess, 6 "", the guild's name, ""} (SPEC §1.5).</summary>
    public AccessStatusEvent AccessStatus() => new()
    {
        ddn = Id,
        bq5 = 0,
        bq8 = Entities.Building.AccessGroups,
        bq9 = ["owner", "noaccess", "noaccess"],
        bra = ["", guildOf(Island.Owner)?.Name ?? "", ""],
    };

    /// <summary>The slot of a seed placed at <paramref name="at"/> (SPEC §3.2, Q13): the nearest of 3 × 3 points 6 m apart around the centre, null beyond 6.5 m on an axis.</summary>
    public Vector2? SlotOf(Vector2 at)
    {
        var d = at - Position;
        if (MathF.Abs(d.X) > Systems.Islands.SlotReach || MathF.Abs(d.Y) > Systems.Islands.SlotReach) return null;
        static float Snap(float v) => Math.Clamp(MathF.Round(v / Systems.Islands.SlotSpacing), -1, 1) * Systems.Islands.SlotSpacing;
        return Position + new Vector2(Snap(d.X), Snap(d.Y));
    }
}

/// <summary>
/// A crop (SPEC §3.2): NewBuilding {0 id, 1 Guid, 2 the seed's item building type (665), 3 the seed's uniquename, 4 its slot, 7 10000,
/// 8/20/22 the planting stamp, 9 owner, 11/12 owner name, 19 -1, 25 true, 26/28 true only when just planted, 29 byte 0}, then its
/// FarmableObjectInfo (<see cref="Systems.Islands.Info"/>). No AccessStatus (113433: the seeds have none).
/// </summary>
public sealed class FarmableObject(long id, IslandObjectSpawn spawn, Farmable crop, ItemTemplate seed, short typeId, Island island, Systems.Islands islands)
    : WorldObject(id, spawn)
{
    public Farmable Crop { get; } = crop;

    public ItemTemplate Seed { get; } = seed;

    /// <summary>NewBuilding key 2: ClientNumbering:ItemBuildingBase + the seed's place index.</summary>
    public short TypeId { get; } = typeId;

    public Island Island { get; } = island;

    /// <summary>Set while the planting announces it (keys 26/28 as official at planting); false for every later announce.</summary>
    public bool JustPlanted { get; set; }

    /// <summary>Its grown FarmableObjectInfo has been sent (Systems.Islands.Update).</summary>
    public bool RipeSent { get; set; }

    /// <summary>The clock time its growth is sent again while Islands:GrowthSpeed is not 1.</summary>
    public TimeSpan NextRefresh { get; set; }

    public override string Kind => "crop";
    public override string TypeName => Seed.Name;
    public override bool LeaveOnRange => true;

    public override IEnumerable<AlbionEvent> BuildCreate(Player viewer)
    {
        yield return new NewBuildingEvent
        {
            ObjectId = Id,                                      // [0]
            BuildingGuid = Crop.Id,                             // [1]
            TypeId = TypeId,                                    // [2] T6_FARM_FOXGLOVE_SEED 665
            Tile = Seed.Name,                                   // [3] the item's uniquename
            Position = Position,                                // [4] the slot
            Durability = 10000,                                 // [7]
            DurabilityStamp = Crop.Planted,                     // [8] the planting
            cto = Island.Owner,                                 // [9]
            ctq = Island.OwnerName, ctr = Island.OwnerName,     // [11], [12]
            ItemTypeId = -1,                                    // [19]
            LoadTime = Crop.Planted, CraftCapacityStamp = Crop.Planted, // [20], [22] = [8]
            ct4 = true,                                         // [25]
            ct5 = JustPlanted, ct7 = JustPlanted,               // [26], [28] at planting only
            Extra = new() { [29] = (byte)0 },                   // [29]
        };
        yield return islands.Info(this, viewer.World.GameNow);
    }
}

/// <summary>
/// A piece of furniture placed in a house (analysis/work/stageN/labourers/SPEC.md §1.3): NewBuilding {0 id, 1 Guid, 2 the item's building type
/// (ClientNumbering:ItemBuildingBase + its place index, as a seed's), 3 the item's uniquename, 4 its spot, 5 yaw, 7 10000, 8/20/22 the placing,
/// 9 owner, 11/12 owner name, 19 -1, 25 true, 29 byte 0}. Stored as an island_farmables row of the house (an item at a spot in a building).
/// </summary>
public sealed class FurnitureObject(long id, IslandObjectSpawn spawn, Farmable row, ItemTemplate item, short typeId, Island island)
    : WorldObject(id, spawn)
{
    public Farmable Row { get; } = row;

    public ItemTemplate Item { get; } = item;

    public short TypeId { get; } = typeId;

    public Island Island { get; } = island;

    public override string Kind => "furniture";
    public override string TypeName => Item.Name;
    public override bool LeaveOnRange => true;

    public override IEnumerable<AlbionEvent> BuildCreate(Player viewer)
    {
        yield return new NewBuildingEvent
        {
            ObjectId = Id,                                      // [0]
            BuildingGuid = Row.Id,                              // [1]
            TypeId = TypeId,                                    // [2]
            Tile = Item.Name,                                   // [3]
            Position = Position,                                // [4]
            Direction = Direction,                              // [5]
            Durability = 10000,                                 // [7]
            DurabilityStamp = Row.Planted,                      // [8]
            cto = Island.Owner,                                 // [9]
            ctq = Island.OwnerName, ctr = Island.OwnerName,     // [11], [12]
            ItemTypeId = -1,                                    // [19]
            LoadTime = Row.Planted, CraftCapacityStamp = Row.Planted, // [20], [22]
            ct4 = true,                                         // [25]
            Extra = new() { [29] = (byte)0 },                   // [29]
        };
    }
}

/// <summary>
/// A labourer in a house (SPEC §2): NewBuilding of its labourer type (the client makes its cph LaborerObject of a b53 template, cjb.a
/// 0x180A9FE50) {0 id, 1 Guid, 2 type, 3 uniquename, 4 spot, 5 yaw, 7 durability, 8 the hire, 9 owner, 11/12 owner name, 19 -1, 20/22 the
/// hire, 29 byte 0}, then LaborerObjectInfo (56) and LaborerObjectJobInfo (57) (<see cref="Systems.Islands.LabourerInfo"/>), and its
/// AccessStatus (the cph checks its AccessRightsLaborerObject before every request, cyb.u 0x1809FE160).
/// </summary>
public sealed class LabourerObject(long id, IslandObjectSpawn spawn, IslandLabourer labourer, LabourerTemplate type, BuildingTemplate building, Island island,
    Systems.Islands islands, Func<Guid, (Guid Id, string Name)?> guildOf) : WorldObject(id, spawn), IAccessStatusObject
{
    public IslandLabourer Labourer { get; } = labourer;

    public LabourerTemplate Type { get; set; } = type;

    public BuildingTemplate Building { get; set; } = building;

    public Island Island { get; } = island;

    public override string Kind => "labourer";
    public override string TypeName => Type.Name;
    public override bool LeaveOnRange => true;

    public override IEnumerable<AlbionEvent> BuildCreate(Player viewer)
    {
        yield return new NewBuildingEvent
        {
            ObjectId = Id,                                              // [0]
            BuildingGuid = Labourer.Id,                                 // [1]
            TypeId = Type.Id,                                           // [2] T2_LABOURER_WOOD
            Tile = Type.Name,                                           // [3]
            Position = Position,                                        // [4]
            Direction = Direction,                                      // [5]
            Durability = (long)Math.Round(Building.Durability * 10000), // [7] 50 → 500000
            DurabilityStamp = Labourer.Hired,                           // [8]
            cto = Island.Owner,                                         // [9]
            ctp = guildOf(Island.Owner)?.Id ?? Guid.Empty,              // [10]
            ctq = Island.OwnerName, ctr = Island.OwnerName,             // [11], [12]
            Nutrition = (long)Math.Round(Building.NutritionStorage * 10000), // [18]
            ItemTypeId = -1,                                            // [19]
            LoadTime = Labourer.Hired, CraftCapacityStamp = Labourer.Hired, // [20], [22]
            Extra = new() { [29] = (byte)0 },                           // [29]
        };
        var now = viewer.World.GameNow;
        yield return islands.LabourerInfo(this, now);
        yield return islands.LabourerJobInfo(this);
    }

    /// <summary>AccessStatus as the island's buildings' (owner / noaccess / noaccess).</summary>
    public AccessStatusEvent AccessStatus() => new()
    {
        ddn = Id,
        bq5 = 0,
        bq8 = Entities.Building.AccessGroups,
        bq9 = ["owner", "noaccess", "noaccess"],
        bra = ["", guildOf(Island.Owner)?.Name ?? "", ""],
    };
}
