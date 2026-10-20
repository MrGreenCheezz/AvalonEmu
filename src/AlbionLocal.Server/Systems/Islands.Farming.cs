using System.Globalization;
using System.Numerics;
using AlbionLocal.Photon;
using AlbionLocal.Server.Data;
using AlbionLocal.Server.Entities;
using AlbionLocal.Server.Game;
using AlbionLocal.Server.Protocol;

namespace AlbionLocal.Server.Systems;

/// <summary>
/// Phase 3 of the islands SPEC (PVE-DESIGN step 14): the player buildings of an island (farm T1_FARMHOUSE, herb garden T1_HERBGARDEN; placed
/// by a GM or Islands:StarterBuildings until building exists, phase 5) and the crops planted in them (PlaceableObjectPlace 69, §3.2), their
/// growth (FarmableObjectInfo 200, §3.3, × Islands:GrowthSpeed), the focus watering (BoostFarmable 291, §3.4) and the harvest
/// (FarmableHarvest 73, §3.5: the seed's loot list, its seed back by chance, the farming fame) or destruction (FarmableDestroy 75). Every
/// building and crop is kept in the island store at once; the map objects are made from it whenever the island's map loads. The
/// inventory side (the seed taken, the harvest given) is the session's (Handlers/World/IslandHandler).
/// </summary>
public sealed partial class Islands
{
    /// <summary>The slots of a farm: 3 × 3 on this lattice (m) around the building's centre (SPEC §3.2 [cap 103829: 6 of them], Q13).</summary>
    public const float SlotSpacing = 6;

    /// <summary>How far from the centre (per axis, m) a seed may be placed and still snap to a slot.</summary>
    public const float SlotReach = 6.5f;

    /// <summary>How far (m) the player may stand from the slot it plants in, waters or harvests (the client walks there first).</summary>
    public const float ActionReach = 12;

    /// <summary>gamedata.xml FarmableObjects placetimeseconds (1): the planting takes this long (official +1.12 s).</summary>
    public static readonly TimeSpan PlaceTime = TimeSpan.FromSeconds(1);

    /// <summary>How often the growth of the crops in view is sent again while Islands:GrowthSpeed is not 1 (the client's bar runs at 1).</summary>
    public static readonly TimeSpan GrowthRefresh = TimeSpan.FromMinutes(5);

    /// <summary>The starter buildings (Islands:StarterBuildings) at two herb garden spots captured on a level-2 island (SPEC §3.1, 113433).</summary>
    public static readonly (string Building, Vector2 At)[] StarterSpots = [("T1_FARMHOUSE", new(145, 105)), ("T1_HERBGARDEN", new(145, 135))];

    private readonly Dictionary<Guid, IslandBuilding> _buildings = [];
    private readonly Dictionary<Guid, Farmable> _crops = [];
    private int _spawns;

    /// <summary>The player buildings of <paramref name="island"/>, oldest first.</summary>
    public IReadOnlyList<IslandBuilding> BuildingsOf(Guid island) => [.. _buildings.Values.Where(b => b.Island == island).OrderBy(b => b.Built)];

    /// <summary>The crops of <paramref name="island"/>, oldest first.</summary>
    public IReadOnlyList<Farmable> CropsOf(Guid island) => [.. _crops.Values.Where(f => f.Island == island).OrderBy(f => f.Planted)];

    /// <summary>The building type id of a placed <paramref name="item"/> (NewBuilding key 2, SPEC §0.4): ClientNumbering:ItemBuildingBase + its place index; -1 for none.</summary>
    public short ItemBuildingId(ItemTemplate item) => item.PlaceIndex < 0 ? (short)-1 : (short)(world.Config.ItemBuildingBase + item.PlaceIndex);

    /// <summary>buildings.xml (a small file): null without cluster data.</summary>
    private BuildingStore? Buildings
    {
        get
        {
            try { return world.Clusters?.Buildings; }
            catch (Exception e) when (e is InvalidDataException or IOException)
            {
                Log.Warn("world", $"buildings.xml not read: {e.Message}");
                return null;
            }
        }
    }

    /// <summary>
    /// A player building <paramref name="name"/> (buildings.xml) on <paramref name="island"/> with its centre at <paramref name="at"/>, saved, and
    /// on the island's map at once when it is loaded; null with the reason when it is no farm building (only farms and herb gardens grow
    /// anything yet) or overlaps another building (their slots 3 × 6 m apart). <paramref name="place"/> false: the store only (the map
    /// being filled yields it itself).
    /// </summary>
    public IslandBuilding? Build(Island island, string name, Vector2 at, float rotation, out string why, bool place = true)
    {
        why = "";
        if (Buildings?.Find(name) is not { } template) why = $"no building {name} in buildings.xml";
        else if (template.Kind != "farmbuilding" && !IsHouse(template)) why = $"{template.Name} is a {template.Kind}; only farm buildings (T1_FARMHOUSE, T1_HERBGARDEN, T1_PASTURE, T1_KENNEL) and houses (T2-T8_PLAYERHOUSE) are built";
        else if (BuildingsOf(island.Id).FirstOrDefault(b => Vector2.Distance(new Vector2(b.X, b.Z), at) < 3 * SlotSpacing) is { } near)
            why = $"too near the {near.Name} at ({near.X:0.#}, {near.Z:0.#})";
        else
        {
            var b = new IslandBuilding(Guid.NewGuid(), island.Id, template.Name, at.X, at.Y, rotation, world.GameNow);
            _buildings[b.Id] = b;
            store?.SaveIslandBuilding(b);
            if (place && world.Maps.Find(island.Cluster) is { Grid: not null } map) map.AddObject(Object(map, island, b, template));
            Log.Info("world", string.Create(CultureInfo.InvariantCulture, $"{island}: {template.Name} built at ({at.X:0.#}, {at.Y:0.#})"));
            return b;
        }
        return null;
    }

    /// <summary>Removes a player building and its crops (GM .island unbuild): off the map, out of the store.</summary>
    public void Demolish(IslandBuilding b)
    {
        _buildings.Remove(b.Id);
        foreach (var f in _crops.Values.Where(f => f.Building == b.Id).ToList()) _crops.Remove(f.Id);
        foreach (var l in _labourers.Values.Where(l => l.Building == b.Id).ToList()) _labourers.Remove(l.Id); // their rows follow by ON DELETE CASCADE
        store?.DeleteIslandBuilding(b.Id); // the crops follow by ON DELETE CASCADE
        if (_islands.GetValueOrDefault(b.Island) is { } island && world.Maps.Find(island.Cluster) is { } map)
            foreach (var o in map.Objects.Values.Where(o => o is PlayerBuilding pb && pb.Building.Id == b.Id || o is FarmableObject c && c.Crop.Building == b.Id
                         || o is FurnitureObject f && f.Row.Building == b.Id || o is LabourerObject l && l.Labourer.Building == b.Id).ToList())
                map.RemoveObject(o);
    }

    /// <summary>
    /// Islands:StarterBuildings: a farm and a herb garden (<see cref="StarterSpots"/>) on a new island, and on the map load of an island
    /// without any building (one given before phase 3, or one a GM cleared), so older saves can farm too.
    /// </summary>
    private void AddStarterBuildings(Island island, bool place = true)
    {
        foreach (var (name, at) in StarterSpots)
            if (Build(island, name, at, 0, out var why, place) is null) Log.Warn("world", $"{island}: starter {name} not built: {why}");
    }

    private PlayerBuilding Object(Map map, Island island, IslandBuilding b, BuildingTemplate template) =>
        new(world.ObjectIds.Next(), IslandObjectSpawn.At(map.Cluster, "building", _spawns++, template.Name, new Vector2(b.X, b.Z), b.Rotation), b, template, island,
            map.CreatedTicks > 0 ? map.CreatedTicks : world.GameNow, GuildOf); // the map load (still 0 while the map is being filled)

    private FarmableObject? Object(Map map, Island island, Farmable f)
    {
        if (world.Items.Find(f.Item) is not { IsFarmable: true } seed) return null;
        return new FarmableObject(world.ObjectIds.Next(), IslandObjectSpawn.At(map.Cluster, "crop", _spawns++, seed.Name, new Vector2(f.X, f.Z), 0), f, seed,
            ItemBuildingId(seed), island, this);
    }

    /// <summary>The objects of the island's buildings, each followed by its crops (§1.5: each player building followed by its farmables).</summary>
    private IEnumerable<WorldObject> FarmObjects(Map map, Island island)
    {
        if (world.Config.IslandsStarterBuildings && BuildingsOf(island.Id).Count == 0) AddStarterBuildings(island, place: false);
        var buildings = BuildingsOf(island.Id);
        if (buildings.Count == 0 || Buildings is not { } store) yield break;
        foreach (var b in buildings)
        {
            if (store.Find(b.Name) is not { } template)
            {
                Log.Warn("world", $"{island}: building {b.Name} is not in buildings.xml, left out");
                continue;
            }
            yield return Object(map, island, b, template);
            foreach (var f in _crops.Values.Where(f => f.Building == b.Id && world.Items.Find(f.Item) is not { IsFurniture: true }).OrderBy(f => f.Planted))
                if (Object(map, island, f) is { } o) yield return o;
                else Log.Warn("world", $"{island}: crop {f.Item} is no seed of items.xml, left out");
            foreach (var o in HouseObjects(map, island, b)) yield return o; // labourers SPEC §2.1: the furniture, then the labourers
        }
    }

    // ---- growth (§3.3) ----

    /// <summary>The growth of <paramref name="crop"/> at GameTimeStamp <paramref name="now"/> in ticks: (now − planted) × Islands:GrowthSpeed, at most the growtime.</summary>
    public long GrownTicks(Farmable crop, FarmData farm, long now)
    {
        var grown = (long)Math.Min(TimeSpan.FromSeconds(farm.GrowTime).Ticks, Math.Max(0, now - crop.Planted) * world.Config.IslandsGrowthSpeed);
        return Math.Max(0, grown);
    }

    /// <summary>Whether <paramref name="crop"/> is grown at <paramref name="now"/> (harvestable: growth ≥ &lt;harvest growtime&gt;, 79200 s for every crop).</summary>
    public bool Ripe(Farmable crop, FarmData farm, long now) => GrownTicks(crop, farm, now) >= TimeSpan.FromSeconds(farm.GrowTime).Ticks;

    /// <summary>
    /// FarmableObjectInfo (200, aor, client co0.af_4 0x180B8E130; SPEC §3.3): 0 id, 4 the growth in whole seconds × 10000 (left out at 0: at
    /// planting), 5 the stamp it holds at (the planting + those seconds at GrowthSpeed 1, exact as captured), 6-11 empty (animals'), 12 1.0f
    /// (any other value makes the client recompute its rates), 13 the last watering (long.MinValue = never), 14 the waterings (left out at 0).
    /// </summary>
    public FarmableObjectInfoEvent Info(FarmableObject o, long now)
    {
        if (o.Seed.Animal is { } animal) return AnimalInfo(o, animal, now);
        var grown = o.Seed.Farm is { } farm ? GrownTicks(o.Crop, farm, now) : 0;
        var whole = grown / TimeSpan.TicksPerSecond * TimeSpan.TicksPerSecond;
        return new FarmableObjectInfoEvent
        {
            ddn = o.Id,
            b9c = whole / 1000,             // [4] a7m seconds (ticks / 1000)
            b9d = now - (grown - whole),    // [5] key 5 − key 4 × 1000 = the planting at speed 1
            b9e = [], b9f = [], b9g = [], b9h = [], b9i = [], b9j = [],
            b9k = 1f,                       // [12]
            b9l = o.Crop.BoostedAt,         // [13]
            b9m = o.Crop.Boosts,            // [14]
        };
    }

    /// <summary>Sends <paramref name="o"/>'s FarmableObjectInfo to every player on its map that knows it.</summary>
    public void SendInfo(FarmableObject o)
    {
        if (o.Map is not { } map) return;
        var info = Info(o, world.GameNow);
        foreach (var p in map.Players.Values)
            if (p.Visibility.Knows(o)) p.Session.Send(info);
    }

    /// <summary>
    /// The island maps' crops, every world tick: one that has just ripened gets its FarmableObjectInfo again (the bar full), and while
    /// Islands:GrowthSpeed is not 1 every crop in view gets it every <see cref="GrowthRefresh"/> (the client's bar runs at 1).
    /// </summary>
    public void Update(Map map, TimeSpan now)
    {
        if (!IslandData.IsInstance(map.Cluster) || map.Players.Count == 0) return;
        var stamp = world.GameNow;
        var refresh = world.Config.IslandsGrowthSpeed != 1;
        foreach (var o in map.Objects.Values.OfType<FarmableObject>())
        {
            bool ripe;
            if (o.Seed.Farm is { } farm) ripe = Ripe(o.Crop, farm, stamp);
            else if (o.Seed.Animal is { } animal) ripe = AnimalGrown(o.Crop, animal, stamp);
            else continue;
            if (ripe && !o.RipeSent || refresh && !ripe && now >= o.NextRefresh)
            {
                o.RipeSent = ripe;
                o.NextRefresh = now + GrowthRefresh;
                SendInfo(o);
            }
        }
    }

    /// <summary>GM .island ripen: every crop of <paramref name="island"/> grown now (its planting moved back), sent again.</summary>
    public int Ripen(Island island)
    {
        var now = world.GameNow;
        var n = 0;
        foreach (var f in CropsOf(island.Id))
        {
            if (world.Items.Find(f.Item)?.Animal is { } animal)
            {
                CommitAnimal(f, animal, now);
                f.Growth = CycleTicks(animal);
                store?.SaveFarmable(f);
                n++;
                continue;
            }
            if (world.Items.Find(f.Item)?.Farm is not { } farm) continue;
            f.Planted = Math.Min(f.Planted, now - (long)Math.Ceiling(TimeSpan.FromSeconds(farm.GrowTime).Ticks / world.Config.IslandsGrowthSpeed));
            store?.SaveFarmable(f);
            n++;
        }
        if (world.Maps.Find(island.Cluster) is { } map)
            foreach (var o in map.Objects.Values.OfType<FarmableObject>()) SendInfo(o);
        return n;
    }

    // ---- planting (§3.2) ----

    /// <summary>
    /// The checks of a planting: <paramref name="p"/> on its own island, <paramref name="seed"/> a plant seed whose item building type is
    /// <paramref name="typeId"/>, placed within <see cref="SlotReach"/> of a farm building of the island whose &lt;farmableitems&gt; list it, its
    /// slot free and near the player. The building and the slot, or the refusal.
    /// </summary>
    public (PlayerBuilding? Building, Vector2 Slot, string Why) CheckPlant(Player p, ItemTemplate seed, int typeId, Vector2 at)
    {
        if (!Enabled || p.Map is not { } map || Find(map.Cluster) is not { } island) return (null, default, "not on an island");
        if (island.Owner != p.Character.Id) return (null, default, $"{island} is not the player's");
        if (!seed.IsFarmable) return (null, default, $"{seed.Name} is no plant seed or animal");
        if (typeId != ItemBuildingId(seed)) return (null, default, $"building type {typeId} is not {seed.Name}'s {ItemBuildingId(seed)}");
        var building = map.Objects.Values.OfType<PlayerBuilding>().Where(b => b.SlotOf(at) is not null)
            .OrderBy(b => Vector2.Distance(b.Position, at)).FirstOrDefault();
        if (building?.SlotOf(at) is not { } slot) return (null, default, string.Create(CultureInfo.InvariantCulture, $"({at.X:0.#}, {at.Y:0.#}) is in no farm building"));
        if (!building.FarmableItems.Contains(seed.Name, StringComparer.OrdinalIgnoreCase)) return (null, default, $"{building.Template.Name} grows no {seed.Name}");
        if (map.Objects.Values.OfType<FarmableObject>().Any(o => Vector2.Distance(o.Position, slot) < 0.5f)) return (null, default, "the slot is taken");
        if (Vector2.Distance(p.PositionAt(DateTime.UtcNow), slot) > ActionReach) return (null, default, "the slot is out of the player's reach");
        return (building, slot, "");
    }

    /// <summary>The crop of <paramref name="seed"/> in <paramref name="building"/>'s <paramref name="slot"/>, planted now: saved and announced (NewBuilding with keys 26/28, FarmableObjectInfo).</summary>
    public FarmableObject? Plant(Map map, PlayerBuilding building, ItemTemplate seed, Vector2 slot)
    {
        var crop = new Farmable(Guid.NewGuid(), building.Island.Id, building.Building.Id, seed.Name, slot.X, slot.Y, world.GameNow);
        if (Object(map, building.Island, crop) is not { } o) return null;
        _crops[crop.Id] = crop;
        store?.SaveFarmable(crop);
        o.JustPlanted = true;
        map.AddObject(o);
        o.JustPlanted = false;
        Log.Info("world", string.Create(CultureInfo.InvariantCulture, $"{building.Island}: {seed.Name} planted at ({slot.X:0.#}, {slot.Y:0.#})"));
        return o;
    }

    /// <summary>Whether <paramref name="p"/> stands within <see cref="ActionReach"/> of crop <paramref name="o"/> (watering, harvest, destruction).</summary>
    public bool InReach(Player p, FarmableObject o) => Vector2.Distance(p.PositionAt(DateTime.UtcNow), o.Position) <= ActionReach;

    // ---- watering (§3.4) ----

    /// <summary>
    /// The checks of a focus watering of crop object <paramref name="id"/>: the player's own, near, fewer waterings than activefarmmaxcycles (1)
    /// and enough focus for activefarmfocuscost × Islands:WaterFocusFactor. The crop and the focus it takes, or the refusal.
    /// </summary>
    public (FarmableObject? Crop, double Cost, string Why) CheckBoost(Player p, long id)
    {
        if (p.Map?.Objects.GetValueOrDefault(id) is not FarmableObject o || !o.Seed.IsFarmable) return (null, 0, $"object {id} is no crop");
        var (maxCycles, focusCost) = o.Seed.Farm is { } farm ? (farm.MaxCycles, farm.FocusCost) : (o.Seed.Animal!.MaxCycles, o.Seed.Animal.FocusCost);
        if (o.Island.Owner != p.Character.Id) return (null, 0, $"{o} is not the player's");
        if (o.Crop.Boosts >= maxCycles) return (null, 0, $"{o} is watered already ({o.Crop.Boosts}/{maxCycles})");
        if (!InReach(p, o)) return (null, 0, $"{o} is out of reach");
        var cost = focusCost * world.Config.IslandsWaterFocusFactor;
        var focus = Focus.Current(p, world.GameNow);
        if (focus < cost) return (null, 0, string.Create(CultureInfo.InvariantCulture, $"{focus:0} focus, watering takes {cost:0}"));
        return (o, cost, "");
    }

    /// <summary>The watering done at <paramref name="now"/>: one more, its stamp, saved (FarmableObjectInfo keys 13/14).</summary>
    public void Watered(FarmableObject o, long now)
    {
        o.Crop.Boosts++;
        o.Crop.BoostedAt = now;
        store?.SaveFarmable(o.Crop);
    }

    // ---- the harvest (§3.5) ----

    /// <summary>
    /// What harvesting <paramref name="o"/> gives, rolled once (§3.5): its &lt;harvest lootlist&gt; with lootchance (T6_FOXGLOVE_LOOT: 3-6
    /// T6_FOXGLOVE, 10 % a worm), its seed back with &lt;seed chance&gt; (+ half the activefarmbonus when it was watered and
    /// Islands:FocusSeedReturn: a certain seed), and the fame (&lt;harvest fame&gt;, a7m). The items (not the seed) × <paramref name="yield"/>
    /// (the owner's premium premiumcropyieldfactor, <see cref="Premium.CropYieldFactor"/>), the fraction at random. Null while loot.xml is not read.
    /// </summary>
    public (List<(ItemTemplate Item, int Count)> Items, long Fame)? HarvestOf(FarmableObject o, Random random, double yield = 1)
    {
        if (o.Seed.Farm is not { } farm || world.Clusters?.LootIfLoaded is not { } loot) return null;
        var items = new List<(ItemTemplate, int)>();
        if (random.NextDouble() < farm.LootChance)
            foreach (var (name, n) in loot.RollList(farm.LootList, random).GroupBy(d => d.Item).Select(g => (g.Key, g.Sum(d => d.Amount))))
                if (world.Items.Find(name) is { } item && n > 0) items.Add((item, yield == 1 ? n : Game.Rates.Scale(n, yield, random)));
                else Log.Warn("world", $"harvest of {o.Seed.Name}: item {name} unknown");
        var seedChance = o.Crop.Boosts > 0 && world.Config.IslandsFocusSeedReturn ? Math.Min(1, farm.SeedChance + farm.Bonus / 2) : farm.SeedChance;
        if (farm.SeedAmount > 0 && random.NextDouble() < seedChance) items.Add((o.Seed, farm.SeedAmount));
        return (items, (long)Math.Round(farm.Fame * 10000));
    }

    /// <summary>Takes crop <paramref name="o"/> off its map (Leave) and out of the store (harvested or destroyed).</summary>
    public void Remove(FarmableObject o, string why)
    {
        _crops.Remove(o.Crop.Id);
        store?.DeleteFarmable(o.Crop.Id);
        o.Map?.RemoveObject(o);
        Log.Info("world", $"{o.Island}: {o.Seed.Name} {why}");
    }

    /// <summary>.island buildings: one line per building with its crops.</summary>
    public IEnumerable<string> DescribeFarms(Island island)
    {
        var now = world.GameNow;
        foreach (var b in BuildingsOf(island.Id))
        {
            var crops = _crops.Values.Where(f => f.Building == b.Id).Select(f => world.Items.Find(f.Item) switch
            {
                { Farm: { } farm } => string.Create(CultureInfo.InvariantCulture, $"{f.Item} {100.0 * GrownTicks(f, farm, now) / TimeSpan.FromSeconds(farm.GrowTime).Ticks:0}%{(f.Boosts > 0 ? " watered" : "")}"),
                { Animal: { } a } when CycleTicks(a) > 0 => string.Create(CultureInfo.InvariantCulture,
                    $"{f.Item} {100.0 * AnimalAt(f, a, now).Growth / CycleTicks(a):0}% fed {AnimalAt(f, a, now).Nutrition:0}/{a.NutritionMax:0}"),
                _ => f.Item,
            });
            yield return string.Create(CultureInfo.InvariantCulture, $"{b.Name} at ({b.X:0.#}, {b.Z:0.#}): {string.Join(", ", crops.DefaultIfEmpty("no crops"))}");
        }
    }

    // ---- animals (stageH islands SPEC phase 4, analysis/work/stageN/mounts/SPEC.md) ----

    /// <summary>The length of an animal's cycle in ticks (a baby's growtime, a grown animal's first productiontime).</summary>
    public static long CycleTicks(AnimalData a) => TimeSpan.FromSeconds(a.CycleTime).Ticks;

    /// <summary>
    /// An animal's growth (ticks) and nutrition at <paramref name="now"/>: from its stored values at <see cref="Farmable.FedAt"/>, it grows
    /// while it has nutrition (one unit lasts secondspernutrition s), × Islands:GrowthSpeed, up to its cycle; it eats only while growing.
    /// </summary>
    public (long Growth, double Nutrition) AnimalAt(Farmable f, AnimalData a, long now)
    {
        var since = f.FedAt == 0 ? f.Planted : f.FedAt;
        var elapsed = (long)Math.Min(long.MaxValue / 4, Math.Max(0, now - since) * world.Config.IslandsGrowthSpeed);
        var perUnit = a.SecondsPerNutrition * TimeSpan.TicksPerSecond;
        var fed = perUnit <= 0 ? elapsed : (long)Math.Min(elapsed, f.Nutrition * perUnit);
        var grow = Math.Min(fed, Math.Max(0, CycleTicks(a) - f.Growth));
        return (f.Growth + grow, perUnit <= 0 ? f.Nutrition : Math.Max(0, f.Nutrition - grow / perUnit));
    }

    /// <summary>Stores <see cref="AnimalAt"/> at <paramref name="now"/> in the farmable (before a change of its food or growth).</summary>
    public void CommitAnimal(Farmable f, AnimalData a, long now)
    {
        (f.Growth, f.Nutrition) = AnimalAt(f, a, now);
        f.FedAt = now;
    }

    /// <summary>Whether the animal's cycle is complete at <paramref name="now"/> (a baby grown, a product ready).</summary>
    public bool AnimalGrown(Farmable f, AnimalData a, long now) => CycleTicks(a) > 0 && AnimalAt(f, a, now).Growth >= CycleTicks(a);

    /// <summary>
    /// The FarmableObjectInfo of an animal: keys 4/5 its growth or product progress (whole seconds × 10000 and their stamp, as a crop's),
    /// keys 1/2 and 8/9 its nutrition now (a7m) [unverified which of the two bars the client shows as food; both are sent], key 3 false.
    /// </summary>
    private FarmableObjectInfoEvent AnimalInfo(FarmableObject o, AnimalData a, long now)
    {
        var (grown, nutrition) = AnimalAt(o.Crop, a, now);
        var whole = grown / TimeSpan.TicksPerSecond * TimeSpan.TicksPerSecond;
        var food = (long)Math.Round(nutrition * 10000);
        var eats = a.NutritionMax > 0;
        return new FarmableObjectInfoEvent
        {
            ddn = o.Id,
            b89 = eats ? food : 0,          // [1] nutrition (a7m)
            b9a = eats ? now : 0,           // [2] its stamp
            b9c = whole / 1000,             // [4] growth, a7m seconds
            b9d = now - (grown - whole),    // [5]
            b9e = [], b9f = [],
            b9g = eats ? [food] : [], b9h = eats ? [now] : [], // [8], [9] the food entry: nutrition and stamp
            b9i = [], b9j = [],
            b9k = 1f,
            b9l = o.Crop.BoostedAt,
            b9m = o.Crop.Boosts,
        };
    }

    /// <summary>The nutrition one <paramref name="food"/> gives <paramref name="a"/>: its items.xml nutrition (× 1 + favoritebonus for the favourite), 0 when not accepted.</summary>
    public static double NutritionOf(AnimalData a, ItemTemplate food)
    {
        if (a.NutritionMax <= 0 || !food.Attrs.TryGetValue("nutrition", out var text)
            || !string.Equals(food.Attrs.GetValueOrDefault("foodcategory"), a.FoodCategory, StringComparison.OrdinalIgnoreCase)
            || !double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out var n) || n <= 0) return 0;
        return string.Equals(food.Name, a.Favorite, StringComparison.OrdinalIgnoreCase) ? n * (1 + a.FavoriteBonus) : n;
    }

    /// <summary>
    /// Feeds animal <paramref name="o"/> (FarmableFill): of each offered stack (at most its count) as many items as it takes to fill the
    /// nutrition to nutritionmax (the last one may overfill; capped). The items used per stack; the animal is saved and its info sent.
    /// </summary>
    public List<(Item Stack, int Used)> Feed(FarmableObject o, IEnumerable<(Item Stack, int Count)> offered)
    {
        var used = new List<(Item, int)>();
        if (o.Seed.Animal is not { } a) return used;
        CommitAnimal(o.Crop, a, world.GameNow);
        foreach (var (stack, count) in offered)
        {
            var per = NutritionOf(a, stack.Template);
            var n = 0;
            while (per > 0 && n < Math.Min(count, stack.Count) && o.Crop.Nutrition < a.NutritionMax - 1e-9)
            {
                o.Crop.Nutrition = Math.Min(a.NutritionMax, o.Crop.Nutrition + per);
                n++;
            }
            if (n > 0) used.Add((stack, n));
        }
        if (used.Count == 0) return used;
        store?.SaveFarmable(o.Crop);
        SendInfo(o);
        Log.Info("world", string.Create(CultureInfo.InvariantCulture,
            $"{o.Island}: {o.Seed.Name} fed {string.Join(", ", used.Select(u => $"{u.Item2} x {u.Item1.Template.Name}"))}, nutrition {o.Crop.Nutrition:0}/{a.NutritionMax:0}"));
        return used;
    }

    /// <summary>
    /// What collecting grown baby <paramref name="o"/> gives (FarmableFinishGrownItem): the grown animal, the baby back with &lt;offspring
    /// chance&gt; (+ activefarmbonus when it was cared for with focus [guess]) and the &lt;grownitem fame&gt; (a7m). Null when it is no grown baby.
    /// </summary>
    public (List<(ItemTemplate Item, int Count)> Items, long Fame)? GrownOf(FarmableObject o, Random random)
    {
        if (o.Seed.Animal is not { GrownItem: { } name } a || !AnimalGrown(o.Crop, a, world.GameNow) || world.Items.Find(name) is not { } grown) return null;
        var items = new List<(ItemTemplate, int)> { (grown, 1) };
        var chance = o.Crop.Boosts > 0 ? Math.Min(1, a.OffspringChance + a.Bonus) : a.OffspringChance;
        if (a.OffspringAmount > 0 && random.NextDouble() < chance) items.Add((o.Seed, a.OffspringAmount));
        return (items, (long)Math.Round(a.Fame * 10000));
    }

    /// <summary>
    /// What product <paramref name="index"/> of grown animal <paramref name="o"/> gives once its production is complete (FarmableGetProduct): its
    /// lootlist with lootchance and its fame (a7m), the items × <paramref name="yield"/> (<see cref="YieldFactor"/>: the island bonus and the
    /// owner's premium, the fraction at random). Null when it has no such product, it is not ready, or loot.xml is not read.
    /// </summary>
    public (List<(ItemTemplate Item, int Count)> Items, long Fame)? ProductOf(FarmableObject o, int index, Random random, double yield = 1)
    {
        if (o.Seed.Animal is not { GrownItem: null } a || index < 0 || index >= a.Products.Count || !AnimalGrown(o.Crop, a, world.GameNow)
            || world.Clusters?.LootIfLoaded is not { } loot) return null;
        var product = a.Products[index];
        var items = new List<(ItemTemplate, int)>();
        if (random.NextDouble() < product.LootChance)
            foreach (var (name, n) in loot.RollList(product.LootList, random).GroupBy(d => d.Item).Select(g => (g.Key, g.Sum(d => d.Amount))))
                if (world.Items.Find(name) is { } item && n > 0) items.Add((item, yield == 1 ? n : Game.Rates.Scale(n, yield, random)));
                else Log.Warn("world", $"product of {o.Seed.Name}: item {name} unknown");
        return (items, (long)Math.Round(product.Fame * 10000));
    }

    /// <summary>A product collected: the production starts again (the nutrition left is kept), saved and sent.</summary>
    public void ProductTaken(FarmableObject o)
    {
        if (o.Seed.Animal is not { } a) return;
        CommitAnimal(o.Crop, a, world.GameNow);
        o.Crop.Growth = 0;
        o.RipeSent = false;
        store?.SaveFarmable(o.Crop);
        SendInfo(o);
    }
}
