namespace AlbionLocal.Server.Game;

/// <summary>
/// An owned island (stageH islands SPEC §0.5): its Guid (the instance's, "@ISLAND@&lt;guid&gt;"), the owner character and its name (from
/// the character table; GetIslandInfos key 3, ChangeCluster key 2), the island type (worldsettings.xml, 1-based; GetIslandInfos key 2), the
/// access point it is reached from ("&lt;islandaccesspoint id&gt;@&lt;city&gt;", key 1 and Join key 81), the level 0-5 (the template
/// …0001a-f), when it was bought and when its level last changed (UTC ticks; 0 = never, key 7) and the favourite / hidden flags of the
/// merchant list (keys 21/22). Saved at once by the island store (a change is rare: a gift, a level).
/// </summary>
public sealed class Island(Guid id, Guid owner, string ownerName, int type, string accessPoint)
{
    /// <summary>How long a level change keeps the island "upgrading" (the client's ctp.b.f adds 1,300,000,000 ticks: 130 s).</summary>
    public static readonly TimeSpan UpgradeTime = TimeSpan.FromSeconds(130);

    public Guid Id { get; } = id;

    public Guid Owner { get; } = owner;

    public string OwnerName { get; } = ownerName;

    /// <summary>The island type id (Data.IslandData, 1-based document order).</summary>
    public int Type { get; } = type;

    /// <summary>"&lt;islandaccesspoint id D&gt;@&lt;city&gt;" (41 characters for a city id of 4).</summary>
    public string AccessPoint { get; } = accessPoint;

    /// <summary>0-5.</summary>
    public int Level { get; set; }

    /// <summary>UTC ticks of the purchase (or the GM's gift; GetIslandInfos key 8).</summary>
    public long Bought { get; init; }

    /// <summary>UTC ticks of the last level change; 0 = never (key 7).</summary>
    public long LevelChanged { get; set; }

    public bool Favorite { get; set; }

    public bool Hidden { get; set; }

    /// <summary>The instance id, "@ISLAND@&lt;guid&gt;" (cluster of the characters on it, Join key 8).</summary>
    public string Cluster => Data.IslandData.InstanceId(Id);

    /// <summary>The city of the access point ("1000").</summary>
    public string City => AccessPoint[(AccessPoint.IndexOf('@') + 1)..];

    /// <summary>The access point's world.xml islandaccesspoint id.</summary>
    public Guid AccessPointId => Guid.TryParse(AccessPoint.Split('@')[0], out var g) ? g : Guid.Empty;

    /// <summary>Whether a level change is under way at <paramref name="utcTicks"/> (journeys refused with 8046, SPEC §1.3).</summary>
    public bool Upgrading(long utcTicks) => LevelChanged > 0 && utcTicks < LevelChanged + UpgradeTime.Ticks;

    public override string ToString() => $"island {Id.ToString()[..8]} (type {Type}, level {Level}, {AccessPoint}) of '{OwnerName}'";
}

/// <summary>
/// A player building on an island (stageH islands SPEC §0.5, §3.1): a buildings.xml building (T1_FARMHOUSE, T1_HERBGARDEN, …) by uniquename,
/// its centre (x, z) and yaw, and when it was built (UTC ticks, NewBuilding key 8). Until building on islands exists (phase 5) a GM's
/// .island build or Islands:StarterBuildings puts them there.
/// </summary>
public sealed record IslandBuilding(Guid Id, Guid Island, string Name, float X, float Z, float Rotation, long Built);

/// <summary>
/// A crop planted in a farm building of an island (SPEC §0.5, §3.2-§3.5): the seed item, the building it stands in, its slot (x, z), the
/// GameTimeStamp of the planting (NewBuilding keys 8/20/22) and the focus watering: how often (FarmableObjectInfo key 14) and the stamp
/// of the last (key 13; <see cref="long.MinValue"/> = never, as official).
/// </summary>
public sealed record Farmable(Guid Id, Guid Island, Guid Building, string Item, float X, float Z, long Planted)
{
    /// <summary>The planting stamp; a GM's .island ripen moves it back.</summary>
    public long Planted { get; set; } = Planted;

    public int Boosts { get; set; }

    public long BoostedAt { get; set; } = long.MinValue;

    /// <summary>An animal's nutrition (food units, items.xml nutrition) at <see cref="FedAt"/>; 0 for a crop (schema v28).</summary>
    public double Nutrition { get; set; }

    /// <summary>The stamp of <see cref="Nutrition"/> and <see cref="Growth"/>; 0 = the planting (schema v28).</summary>
    public long FedAt { get; set; }

    /// <summary>An animal's growth (a baby) or product progress (a grown animal) in ticks at <see cref="FedAt"/> (schema v28).</summary>
    public long Growth { get; set; }
}

/// <summary>
/// A labourer living in a house of an island (analysis/work/stageN/labourers/SPEC.md §2, schema v33): its buildings.xml &lt;labourer&gt; type
/// (T2_LABOURER_WOOD; it changes when it advances a tier), the house (an island building), its spot and yaw in the house, its name, the hire
/// (GameTimeStamp: settled in settleintime later), the labourer fame it learned (a7m raw), its happiness (whole, 0..units × 100) at a stamp
/// (it moves towards what the house's furniture gives, SPEC §3) and the job under way or done: the _FULL journal it works with, its start
/// and end stamps and the yield fixed at the start. Saved at once by the island store.
/// </summary>
public sealed class IslandLabourer(Guid id, Guid island, Guid building, string type, string name, float x, float z, float rotation, long hired)
{
    public Guid Id { get; } = id;
    public Guid Island { get; } = island;
    public Guid Building { get; } = building;

    /// <summary>buildings.xml labourer uniquename.</summary>
    public string Type { get; set; } = type;

    public string Name { get; } = name;
    public float X { get; set; } = x;
    public float Z { get; set; } = z;
    public float Rotation { get; set; } = rotation;

    /// <summary>GameTimeStamp of the hire (a GM's .labourer finish moves it back so that the labourer has settled in).</summary>
    public long Hired { get; set; } = hired;

    /// <summary>The labourer fame learned (a7m raw): what lets it advance a tier.</summary>
    public long Fame { get; set; }

    /// <summary>The happiness (whole) at <see cref="HappinessAt"/>.</summary>
    public double Happiness { get; set; }

    public long HappinessAt { get; set; }

    /// <summary>The _FULL journal of the job; null = no job.</summary>
    public string? Journal { get; set; }

    public long JobStart { get; set; }

    /// <summary>GameTimeStamp the job is done at.</summary>
    public long JobEnd { get; set; }

    /// <summary>The yield of the job (1 = 100 %), fixed at its start.</summary>
    public double JobYield { get; set; }

    public override string ToString() => $"labourer '{Name}' ({Type})";
}
