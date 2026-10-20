using System.Globalization;
using System.Numerics;
using AlbionLocal.Photon;
using AlbionLocal.Server.Data;
using AlbionLocal.Server.Entities;
using AlbionLocal.Server.Game;
using AlbionLocal.Server.Protocol;

namespace AlbionLocal.Server.Systems;

/// <summary>
/// Houses and labourers on the islands (analysis/work/stageN/labourers/SPEC.md): the player houses (T2-T8_PLAYERHOUSE, built from the build menu
/// on a plot like a farm building, upgraded a tier by BuildingChangeRenovationState 61), the furniture in them (PlaceableObjectPlace 69 of a
/// furnitureitem inside the house; beds, tables and trophies make the labourers happy) and the labourers (hired from the house's menu for silver
/// or by placing a labourer contract, LaborerStartJob 257 with a _FULL journal, LaborerTakeJobLoot 258 after the joblength, LaborerDismiss 259,
/// LaborerMove 260, LaborerBuyItem 261 the _EMPTY journals, LaborerUpgrade 262 to the next tier). The inventory side is the session's
/// (Handlers/World/LabourerHandler). Touched only under <see cref="World.Sync"/>.
/// </summary>
public sealed partial class Islands
{
    /// <summary>Half the side of a house (tiles.xml T2_PLAYERHOUSE 20 × 20): what is placed in it must stand within this of its centre, less a margin.</summary>
    public const float HouseHalf = BuildingSize / 2 - 1;

    /// <summary>The names a new labourer gets (namegenerators.xml is not read) [ours].</summary>
    private static readonly string[] LabourerNames =
    [
        "Aldric", "Bertram", "Cedric", "Dunstan", "Edmund", "Godwin", "Hamond", "Osric", "Wilfred", "Ansel",
        "Brunhild", "Edith", "Gisela", "Hilda", "Isolde", "Mathilde", "Odile", "Rowena", "Sibyl", "Wynn",
    ];

    private readonly Dictionary<Guid, IslandLabourer> _labourers = [];
    private IReadOnlyDictionary<string, LabourerTemplate>? _labourerTypes;

    /// <summary>The labourers of buildings.xml by uniquename (empty without cluster data).</summary>
    public IReadOnlyDictionary<string, LabourerTemplate> LabourerTypes =>
        _labourerTypes ??= Buildings is { } store ? LabourerTemplate.All(store) : new Dictionary<string, LabourerTemplate>();

    /// <summary>The labourer of building type <paramref name="typeId"/>; null when it is no labourer.</summary>
    public LabourerTemplate? LabourerType(int typeId) =>
        typeId is >= short.MinValue and <= short.MaxValue && Buildings?[(short)typeId] is { Kind: "labourer" } t ? LabourerTypes.GetValueOrDefault(t.Name) : null;

    /// <summary>A player house of the islands: T2..T8_PLAYERHOUSE (not the founder houses nor the guild halls).</summary>
    public static bool IsHouse(BuildingTemplate t) =>
        t.Kind == "playerbuilding" && t.Name.Length == "T2_PLAYERHOUSE".Length && t.Name.EndsWith("_PLAYERHOUSE", StringComparison.Ordinal);

    /// <summary>The labourers of the house <paramref name="building"/>, oldest first.</summary>
    public IReadOnlyList<IslandLabourer> LabourersOf(Guid building) => [.. _labourers.Values.Where(l => l.Building == building).OrderBy(l => l.Hired)];

    /// <summary>The furniture placed in <paramref name="building"/> (island_farmables rows of furnitureitems), oldest first.</summary>
    public IReadOnlyList<Farmable> FurnitureOf(Guid building) =>
        [.. _crops.Values.Where(f => f.Building == building && world.Items.Find(f.Item) is { IsFurniture: true }).OrderBy(f => f.Planted)];

    /// <summary>Whether <paramref name="at"/> is inside the house <paramref name="b"/> (its 20 × 20 m less a metre).</summary>
    public static bool Inside(IslandBuilding b, Vector2 at) => MathF.Abs(at.X - b.X) <= HouseHalf && MathF.Abs(at.Y - b.Z) <= HouseHalf;

    /// <summary>The own house of <paramref name="p"/>'s island <paramref name="at"/> is in, or the refusal.</summary>
    private (PlayerBuilding? House, string Why) HouseAt(Player p, Vector2 at)
    {
        if (!Enabled || p.Map is not { } map || Find(map.Cluster) is not { } island) return (null, "not on an island");
        if (island.Owner != p.Character.Id) return (null, $"{island} is not the player's");
        var house = map.Objects.Values.OfType<PlayerBuilding>().FirstOrDefault(b => IsHouse(b.Template) && Inside(b.Building, at));
        return house is null ? (null, string.Create(CultureInfo.InvariantCulture, $"({at.X:0.#}, {at.Y:0.#}) is in no house of the island")) : (house, "");
    }

    private static readonly IReadOnlyList<(string Item, int Count)> NoCost = [];

    // ---- happiness (SPEC §3) ----

    /// <summary>
    /// The happiness the house's furniture gives <paramref name="l"/> (SPEC §3 [our reading of the data]): the beds and tables of the house, best
    /// first, each serving labourersperfurnitureitem labourers in hire order (a bed one, a table four or six); the labourer gets its bed's and its
    /// table's labourerhappiness, each at most its maxbed/maxtablecontribution, plus every trophy of its profession (or "all"), together at most
    /// maxtrophycontribution; all at most happinessunits × 100.
    /// </summary>
    public double HappinessTarget(IslandLabourer l)
    {
        if (LabourerTypes.GetValueOrDefault(l.Type) is not { } type) return 0;
        var mates = LabourersOf(l.Building);
        var place = 0;
        for (var i = 0; i < mates.Count; i++) if (mates[i].Id == l.Id) place = i;
        var furniture = FurnitureOf(l.Building).Select(f => world.Items.Find(f.Item)!).ToList();
        double Num(ItemTemplate t, string key) => double.TryParse(t.Attrs.GetValueOrDefault(key), NumberStyles.Float, CultureInfo.InvariantCulture, out var d) ? d : 0;
        double Served(string kind)
        {
            var seat = 0;
            foreach (var f in furniture.Where(f => f.Attrs.GetValueOrDefault("labourerfurnituretype") == kind).OrderByDescending(f => Num(f, "labourerhappiness")))
            {
                seat += Math.Max(1, (int)Num(f, "labourersperfurnitureitem"));
                if (place < seat) return Num(f, "labourerhappiness");
            }
            return 0;
        }
        var trophies = furniture.Where(f => f.Attrs.GetValueOrDefault("labourerfurnituretype") == "trophy"
                && f.Attrs.GetValueOrDefault("labourersaffected", "all") is var who && (who == "all" || who == type.Profession))
            .Sum(f => Num(f, "labourerhappiness"));
        var sum = Math.Min(Served("bed"), type.MaxBed) + Math.Min(Served("table"), type.MaxTable) + Math.Min(trophies, type.MaxTrophy);
        return Math.Min(sum, type.MaxHappiness);
    }

    /// <summary>
    /// <paramref name="l"/>'s happiness at <paramref name="now"/>: from the stored value towards <see cref="HappinessTarget"/> by one unit (100) per
    /// happinessmovementtickrate seconds [guess, SPEC §3].
    /// </summary>
    public double Happiness(IslandLabourer l, long now)
    {
        var target = HappinessTarget(l);
        var tick = LabourerTypes.GetValueOrDefault(l.Type)?.HappinessTickSeconds is > 0 and var t ? t : 360;
        var moved = Math.Max(0, now - l.HappinessAt) / (double)TimeSpan.TicksPerSecond / tick * LabourerTemplate.HappinessPerUnit;
        return l.Happiness < target ? Math.Min(target, l.Happiness + moved) : Math.Max(target, l.Happiness - moved);
    }

    /// <summary>
    /// Before the furniture of <paramref name="building"/> changes: every labourer's happiness is fixed at its value now (it then moves towards the
    /// new target from there). <see cref="SendHouse"/> after the change.
    /// </summary>
    private void CommitHappiness(Guid building)
    {
        var now = world.GameNow;
        foreach (var l in LabourersOf(building))
        {
            l.Happiness = Happiness(l, now);
            l.HappinessAt = now;
            store?.SaveLabourer(l);
        }
    }

    /// <summary>The LaborerObjectInfo of every labourer of <paramref name="building"/> again, to the players on <paramref name="map"/> who know it.</summary>
    private void SendHouse(Map map, Guid building)
    {
        foreach (var o in map.Objects.Values.OfType<LabourerObject>().Where(o => o.Labourer.Building == building).ToList()) SendLabourer(o);
    }

    // ---- the events (SPEC §2.2) ----

    /// <summary>
    /// LaborerObjectInfo (56, arn → cph.abq 0x180BB75F0): 0 id, 1/2 avatar and ring [guess: the sim reads neither], 3 its fame (a7m), 4 its
    /// happiness now (a7m), 5 the happiness it moves to (a7m), 6 the stamp of key 4, 7 settled in at (before it: "settling in"), 8 the hire, 9 the
    /// owner, 10 its name.
    /// </summary>
    public LaborerObjectInfoEvent LabourerInfo(LabourerObject o, long now)
    {
        var l = o.Labourer;
        return new LaborerObjectInfoEvent
        {
            ddn = o.Id,
            cmy = o.Type.Avatar,
            cmz = o.Type.AvatarRing,
            cm0 = l.Fame,
            cm1 = (long)Math.Round(Happiness(l, now) * 10000),
            cm2 = (long)Math.Round(HappinessTarget(l) * 10000),
            cm3 = now,
            cm4 = SettledAt(l, o.Type),
            cm5 = l.Hired,
            cm6 = o.Island.Owner,
            cm7 = l.Name,
        };
    }

    /// <summary>
    /// LaborerObjectJobInfo (57, aro → cph.x8 0x180BB9200): 0 id, 1 working, 2 the journal's item id, 3 the yield (a7m, 1 = 100 %), 4 the happiness at
    /// the start [guess for 3/4: the job's two a7m], 5 done at. Without a job only 0 and 1 false.
    /// </summary>
    public LaborerObjectJobInfoEvent LabourerJobInfo(LabourerObject o)
    {
        var l = o.Labourer;
        if (l.Journal is null || world.Items.Find(l.Journal) is not { } journal) return new LaborerObjectJobInfoEvent { ddn = o.Id, cm8 = false };
        return new LaborerObjectJobInfoEvent
        {
            ddn = o.Id,
            cm8 = true,
            cm9 = journal.Index,
            cna = (long)Math.Round(l.JobYield * 10000),
            cnb = (long)Math.Round(l.JobYield * LabourerTemplate.HappinessPerUnit * (journal.Tier) * 10000),
            cnc = l.JobEnd,
        };
    }

    /// <summary>Sends <paramref name="o"/>'s two infos to every player on its map who knows it.</summary>
    public void SendLabourer(LabourerObject o)
    {
        if (o.Map is not { } map) return;
        var now = world.GameNow;
        foreach (var p in map.Players.Values)
            if (p.Visibility.Knows(o))
            {
                p.Session.Send(LabourerInfo(o, now));
                p.Session.Send(LabourerJobInfo(o));
            }
    }

    /// <summary>When <paramref name="l"/> has settled in: the hire + settleintime.</summary>
    public static long SettledAt(IslandLabourer l, LabourerTemplate type) => l.Hired + (long)(type.SettleInSeconds * TimeSpan.TicksPerSecond);

    // ---- objects (SPEC §2.1) ----

    private LabourerObject? Object(Map map, Island island, IslandLabourer l)
    {
        if (LabourerTypes.GetValueOrDefault(l.Type) is not { } type || Buildings?.Find(l.Type) is not { } building) return null;
        return new LabourerObject(world.ObjectIds.Next(), IslandObjectSpawn.At(map.Cluster, "labourer", _spawns++, type.Name, new Vector2(l.X, l.Z), l.Rotation), l, type,
            building, island, this, GuildOf);
    }

    private FurnitureObject? FurnitureObjectOf(Map map, Island island, Farmable f)
    {
        if (world.Items.Find(f.Item) is not { IsFurniture: true } item) return null;
        return new FurnitureObject(world.ObjectIds.Next(), IslandObjectSpawn.At(map.Cluster, "furniture", _spawns++, item.Name, new Vector2(f.X, f.Z), 0),
            f, item, ItemBuildingId(item), island);
    }

    /// <summary>The furniture and labourers of house <paramref name="b"/> as map objects (after the house's own).</summary>
    private IEnumerable<WorldObject> HouseObjects(Map map, Island island, IslandBuilding b)
    {
        foreach (var f in FurnitureOf(b.Id))
            if (FurnitureObjectOf(map, island, f) is { } o) yield return o;
        foreach (var l in LabourersOf(b.Id))
            if (Object(map, island, l) is { } o) yield return o;
            else Log.Warn("world", $"{island}: {l} is no labourer of buildings.xml, left out");
    }

    // ---- hiring (SPEC §2.3) ----

    /// <summary>
    /// The checks of hiring labourer type <paramref name="typeId"/> at <paramref name="at"/> (PlaceableObjectPlace 69 of the house menu's "Hire
    /// labourer", BuildingHireModule.OnHireLaborer → dth.o → cyy.u 0x180A08E80, or of a labourer contract <paramref name="contract"/>): inside an own
    /// house, the house's laborerslimit not reached; without a contract the type must be in the house's &lt;placelaborer&gt; list (T2) and the
    /// hireprice is paid; a contract hires its own labourer (its contractitem) and is used up. The house, the type and the silver, or the refusal.
    /// </summary>
    public (PlayerBuilding? House, LabourerTemplate? Type, long Silver, string Why) CheckHire(Player p, int typeId, ItemTemplate? contract, Vector2 at)
    {
        if (LabourerType(typeId) is not { } type) return (null, null, 0, $"building type {typeId} is no labourer");
        var (house, why) = HouseAt(p, at);
        if (house is null) return (null, null, 0, why);
        if (contract is not null && !string.Equals(type.ContractItem, contract.Name, StringComparison.OrdinalIgnoreCase))
            return (null, null, 0, $"{contract.Name} hires {type.ContractItem ?? "nothing"}'s labourer, not {type.Name}");
        if (contract is null && !(house.Template.Element.Element("placelaborer")?.Elements("laborer")
                .Any(e => string.Equals((string?)e.Attribute("uniquename"), type.Name, StringComparison.OrdinalIgnoreCase)) ?? false))
            return (null, null, 0, $"{house.Template.Name} hires no {type.Name} (a contract places other tiers)");
        var limit = TileMath.Int(house.Template.Attrs.GetValueOrDefault("laborerslimit"));
        if (LabourersOf(house.Building.Id).Count >= limit) return (null, null, 0, $"{house.Template.Name} has room for {limit} labourer(s)");
        var silver = contract is null ? (long)Math.Round(type.HirePrice * Pricing.A7m) : 0;
        if (p.Character.Silver < silver) return (null, null, 0, $"{p.Character.Silver / Pricing.A7m} silver, hiring costs {silver / Pricing.A7m}");
        return (house, type, silver, "");
    }

    /// <summary>A new labourer of <paramref name="type"/> in <paramref name="house"/> at <paramref name="at"/>: saved, on the map.</summary>
    public LabourerObject? Hire(Map map, PlayerBuilding house, LabourerTemplate type, Vector2 at, float rotation)
    {
        var now = world.GameNow;
        var l = new IslandLabourer(Guid.NewGuid(), house.Island.Id, house.Building.Id, type.Name, LabourerNames[map.Random.Next(LabourerNames.Length)], at.X, at.Y, rotation, now)
        {
            Happiness = type.HappinessAfterHire * LabourerTemplate.HappinessPerUnit,
            HappinessAt = now,
        };
        if (Object(map, house.Island, l) is not { } o) return null;
        _labourers[l.Id] = l;
        store?.SaveLabourer(l);
        map.AddObject(o);
        Log.Info("world", string.Create(CultureInfo.InvariantCulture, $"{house.Island}: {l} hired into the {house.Template.Name} at ({at.X:0.#}, {at.Y:0.#})"));
        return o;
    }

    // ---- furniture (SPEC §1.3) ----

    /// <summary>
    /// The checks of placing <paramref name="item"/> (a furnitureitem that is placeableindoors, its item building type <paramref name="typeId"/>)
    /// at <paramref name="at"/>: inside an own house whose furniturelimit is not reached. The house, or the refusal.
    /// </summary>
    public (PlayerBuilding? House, string Why) CheckFurniture(Player p, ItemTemplate item, int typeId, Vector2 at)
    {
        if (!item.IsFurniture) return (null, $"{item.Name} is no furniture");
        if (item.Attrs.GetValueOrDefault("placeableindoors") != "true") return (null, $"{item.Name} is not placed indoors");
        if (typeId != ItemBuildingId(item)) return (null, $"building type {typeId} is not {item.Name}'s {ItemBuildingId(item)}");
        var (house, why) = HouseAt(p, at);
        if (house is null) return (null, why);
        var limit = TileMath.Int(house.Template.Attrs.GetValueOrDefault("furniturelimit"));
        if (FurnitureOf(house.Building.Id).Count >= limit) return (null, $"{house.Template.Name} has room for {limit} piece(s) of furniture");
        return (house, "");
    }

    /// <summary><paramref name="item"/> placed in <paramref name="house"/>: saved, on the map; the labourers' happiness moves to the new target.</summary>
    public FurnitureObject? PlaceFurniture(Map map, PlayerBuilding house, ItemTemplate item, Vector2 at, float rotation)
    {
        CommitHappiness(house.Building.Id);
        var row = new Farmable(Guid.NewGuid(), house.Island.Id, house.Building.Id, item.Name, at.X, at.Y, world.GameNow);
        if (FurnitureObjectOf(map, house.Island, row) is not { } o) return null;
        _crops[row.Id] = row;
        store?.SaveFarmable(row);
        map.AddObject(o);
        SendHouse(map, house.Building.Id);
        Log.Info("world", string.Create(CultureInfo.InvariantCulture, $"{house.Island}: {item.Name} placed in the {house.Template.Name} at ({at.X:0.#}, {at.Y:0.#})"));
        return o;
    }

    /// <summary>The checks of picking up own furniture <paramref name="id"/> (PlaceableObjectPickup 71): the object, or the refusal.</summary>
    public (FurnitureObject? Object, string Why) CheckFurniturePickup(Player p, long id)
    {
        if (p.Map?.Objects.GetValueOrDefault(id) is not FurnitureObject o) return (null, $"object {id} is no furniture");
        if (o.Island.Owner != p.Character.Id) return (null, $"{o} is not the player's");
        return (o, "");
    }

    /// <summary>Takes furniture <paramref name="o"/> off its map and out of the store; the labourers' happiness moves to the new target.</summary>
    public void RemoveFurniture(FurnitureObject o)
    {
        CommitHappiness(o.Row.Building);
        _crops.Remove(o.Row.Id);
        store?.DeleteFarmable(o.Row.Id);
        var map = o.Map;
        map?.RemoveObject(o);
        if (map is not null) SendHouse(map, o.Row.Building);
    }

    // ---- jobs (SPEC §4) ----

    /// <summary>An own labourer object <paramref name="id"/> on <paramref name="p"/>'s map, or the refusal.</summary>
    public (LabourerObject? Labourer, string Why) OwnLabourer(Player p, long id)
    {
        if (p.Map?.Objects.GetValueOrDefault(id) is not LabourerObject o) return (null, $"object {id} is no labourer");
        if (o.Island.Owner != p.Character.Id) return (null, $"{o.Labourer} is not the player's");
        return (o, "");
    }

    /// <summary>
    /// The checks of LaborerStartJob (257) with <paramref name="journal"/>: the labourer settled in and idle, the journal a _FULL one it accepts
    /// (&lt;journalsaccepted&gt;), and the yield at its happiness now at least 0.5 (cph.af 0x180BB83C0, "Not enough yield to start the job!").
    /// The yield, or the refusal.
    /// </summary>
    public (double Yield, string Why) CheckStartJob(LabourerObject o, ItemTemplate journal)
    {
        var l = o.Labourer;
        var now = world.GameNow;
        if (now < SettledAt(l, o.Type)) return (0, $"{l} is still settling in");
        if (l.Journal is not null) return (0, $"{l} is at work already");
        if (journal.Journal is not { State: JournalState.Full }) return (0, $"{journal.Name} is no full journal");
        if (!o.Type.JournalsAccepted.Contains(journal.Name, StringComparer.OrdinalIgnoreCase)) return (0, $"{o.Type.Name} does not work with {journal.Name}");
        var yield = LabourerTemplate.Yield(Happiness(l, now), journal.Tier);
        if (yield < 0.5) return (0, string.Create(CultureInfo.InvariantCulture, $"a yield of {yield:0.##} is not enough (0.5 needed: more beds, tables, trophies)"));
        return (yield, "");
    }

    /// <summary>
    /// A GM's .labourer finish on <paramref name="map"/> (an island): every labourer there has settled in (its hire moved back by its settleintime)
    /// and every running job is done now, so LaborerTakeJobLoot gives its loot; saved, both infos sent. The labourers changed.
    /// </summary>
    public int FinishAll(Map map)
    {
        var now = world.GameNow;
        var n = 0;
        foreach (var o in map.Objects.Values.OfType<LabourerObject>().ToList())
        {
            var l = o.Labourer;
            var changed = false;
            if (now < SettledAt(l, o.Type))
            {
                l.Hired = Math.Min(l.Hired, now - (long)(o.Type.SettleInSeconds * TimeSpan.TicksPerSecond) - 1);
                changed = true;
            }
            if (l.Journal is not null && l.JobEnd > now)
            {
                l.JobEnd = now;
                changed = true;
            }
            if (!changed) continue;
            store?.SaveLabourer(l);
            SendLabourer(o);
            n++;
        }
        return n;
    }

    /// <summary>The job starts now with <paramref name="journal"/> at <paramref name="yield"/>: done joblength later; saved, sent.</summary>
    public void StartJob(LabourerObject o, ItemTemplate journal, double yield)
    {
        var l = o.Labourer;
        var now = world.GameNow;
        l.Happiness = Happiness(l, now);
        l.HappinessAt = now;
        (l.Journal, l.JobStart, l.JobEnd, l.JobYield) = (journal.Name, now, now + (long)(o.Type.JobSeconds * TimeSpan.TicksPerSecond), yield);
        store?.SaveLabourer(l);
        SendLabourer(o);
        Log.Info("world", string.Create(CultureInfo.InvariantCulture, $"{o.Island}: {l} works with {journal.Name} at {yield:0.##} yield"));
    }

    /// <summary>
    /// What a done job of <paramref name="o"/> gives, rolled once (SPEC §4): round(baselootamount × yield × yieldmultiplier) picks from the journal's
    /// &lt;lootlist&gt; by weight; each gives its items (or silver) and its labourerfame to the labourer. Null with the reason when there is no
    /// done job.
    /// </summary>
    public (List<(ItemTemplate Item, int Count)> Items, long Silver, long Fame, string Why) JobLoot(LabourerObject o, Random random)
    {
        var l = o.Labourer;
        if (l.Journal is null || world.Items.Find(l.Journal)?.Journal is not { } journal) return ([], 0, 0, $"{l} has no job");
        if (world.GameNow < l.JobEnd) return ([], 0, 0, $"{l} is not back yet");
        var items = new Dictionary<ItemTemplate, int>();
        long silver = 0, fame = 0;
        var total = journal.Loot.Sum(e => e.Weight);
        var rolls = (int)Math.Round(journal.BaseLoot * l.JobYield * o.Type.YieldMultiplier);
        for (var i = 0; i < rolls && total > 0; i++)
        {
            var pick = random.NextDouble() * total;
            var entry = journal.Loot[^1];
            foreach (var e in journal.Loot)
                if ((pick -= e.Weight) < 0) { entry = e; break; }
            fame += (long)Math.Round(entry.Fame * 10000);
            silver += entry.Silver;
            if (entry.Item is { } name && world.Items.Find(name) is { CanCreate: true } item && entry.Amount > 0)
                items[item] = items.GetValueOrDefault(item) + entry.Amount;
            else if (entry.Item is { } unknown) Log.Warn("world", $"{journal.Journal} loot {unknown} unknown or not creatable, skipped");
        }
        return ([.. items.Select(kv => (kv.Key, kv.Value))], silver, fame, "");
    }

    /// <summary>The job of <paramref name="o"/> is collected: its fame learned, no job; saved, sent.</summary>
    public void JobTaken(LabourerObject o, long fame)
    {
        var l = o.Labourer;
        l.Fame += fame;
        (l.Journal, l.JobStart, l.JobEnd, l.JobYield) = (null, 0, 0, 0);
        store?.SaveLabourer(l);
        SendLabourer(o);
    }

    /// <summary>LaborerMove (260): <paramref name="o"/> to <paramref name="at"/> in its own house; re-announced there. False when outside it.</summary>
    public bool MoveLabourer(LabourerObject o, Vector2 at, float rotation)
    {
        if (o.Map is not { } map || _buildings.GetValueOrDefault(o.Labourer.Building) is not { } house || !Inside(house, at)) return false;
        (o.Labourer.X, o.Labourer.Z, o.Labourer.Rotation) = (at.X, at.Y, rotation);
        store?.SaveLabourer(o.Labourer);
        map.RemoveObject(o);
        if (Object(map, o.Island, o.Labourer) is { } moved) map.AddObject(moved);
        return true;
    }

    /// <summary>LaborerDismiss (259): <paramref name="o"/> leaves (off the map, out of the store); a job under way is lost.</summary>
    public void Dismiss(LabourerObject o)
    {
        _labourers.Remove(o.Labourer.Id);
        store?.DeleteLabourer(o.Labourer.Id);
        o.Map?.RemoveObject(o);
        Log.Info("world", $"{o.Island}: {o.Labourer} dismissed");
    }

    /// <summary>
    /// The checks of LaborerUpgrade (262; cph.aq9 0x180BB8BA0, "Advance to Tier {0}"): an idle labourer whose fame reached fametoprogress and whose
    /// type has an upgradeableto. The next type, or the refusal.
    /// </summary>
    public (LabourerTemplate? Next, string Why) CheckUpgrade(LabourerObject o)
    {
        var l = o.Labourer;
        if (o.Type.UpgradeableTo is not { } next || LabourerTypes.GetValueOrDefault(next) is not { } type) return (null, $"{o.Type.Name} is the last tier");
        if (l.Journal is not null) return (null, $"{l} is at work");
        if (l.Fame < (long)Math.Round(o.Type.FameToProgress * 10000)) return (null, $"{l} has {l.Fame / 10000} of {o.Type.FameToProgress} fame");
        return (type, "");
    }

    /// <summary>The labourer advances to <paramref name="next"/> (its fame starts again from what is left over); LaborerGotUpgraded (290), re-announced.</summary>
    public void Upgrade(LabourerObject o, LabourerTemplate next)
    {
        var l = o.Labourer;
        CommitHappiness(l.Building);
        l.Fame = Math.Max(0, l.Fame - (long)Math.Round(o.Type.FameToProgress * 10000));
        l.Type = next.Name;
        store?.SaveLabourer(l);
        if (o.Map is not { } map) return;
        foreach (var p in map.Players.Values)
            if (p.Visibility.Knows(o)) p.Session.Send(new LaborerGotUpgradedEvent { ddn = o.Id });
        map.RemoveObject(o);
        if (Object(map, o.Island, l) is { } upgraded) map.AddObject(upgraded);
        Log.Info("world", $"{o.Island}: {l} advanced to {next.Name}");
    }

    // ---- houses (SPEC §1) ----

    /// <summary>
    /// The checks of a house upgrade (BuildingChangeRenovationState 61 type 2, RenovationStartView.OnClickStartSelectedRenovation): an own house
    /// with an upgradeableto, the next tier's placecost silver and craftingrequirements in the inventory (taken at once: no construction site,
    /// as a new building). The house, the next tier, the silver and the resources, or the refusal.
    /// </summary>
    public (PlayerBuilding? House, BuildingTemplate? Next, long Silver, List<(ItemTemplate Item, int Count)> Cost, string Why) CheckHouseUpgrade(Player p, long id)
    {
        List<(ItemTemplate, int)> cost = [];
        if (p.Map?.Objects.GetValueOrDefault(id) is not PlayerBuilding b || !IsHouse(b.Template)) return (null, null, 0, cost, $"object {id} is no house");
        if (b.Island.Owner != p.Character.Id) return (null, null, 0, cost, $"{b.Template.Name} of {b.Island} is not the player's");
        if (b.Template.Attrs.GetValueOrDefault("upgradeableto") is not { Length: > 0 } nextName || Buildings?.Find(nextName) is not { } next)
            return (null, null, 0, cost, $"{b.Template.Name} is the last tier");
        foreach (var (name, count) in BuildCost(next))
        {
            if (world.Items.Find(name) is not { } item) return (null, null, 0, cost, $"{next.Name} needs {name}, which items.xml does not have");
            var have = p.Inventory.Bag.Items.Where(i => i.Template == item).Sum(i => i.Count);
            if (have < count) return (null, null, 0, cost, $"{next.Name} needs {count} x {name}, the inventory has {have}");
            cost.Add((item, count));
        }
        var silver = PlaceCost(next);
        if (p.Character.Silver < silver) return (null, null, 0, cost, $"{p.Character.Silver / Pricing.A7m} silver, {next.Name} costs {silver / Pricing.A7m}");
        return (b, next, silver, cost, "");
    }

    /// <summary>The house becomes <paramref name="next"/> (same Guid and spot; saved; the map object announced again).</summary>
    public void UpgradeHouse(PlayerBuilding house, BuildingTemplate next)
    {
        var b = house.Building with { Name = next.Name };
        _buildings[b.Id] = b;
        store?.SaveIslandBuilding(b);
        if (house.Map is { } map)
        {
            map.RemoveObject(house);
            map.AddObject(Object(map, house.Island, b, next));
        }
        Log.Info("world", $"{house.Island}: {house.Template.Name} upgraded to {next.Name}");
    }

    /// <summary>
    /// UpdateUnlockedBuildings (224, a1n → cs6.k 0x180BFE2A0: the names go into the set ctp.af(b5j) 0x180994CF0 checks): the houses (unlocked
    /// "false" in buildings.xml) for the owner arriving on an island, so the build menu offers them.
    /// </summary>
    public void Joined(Player p)
    {
        if (!Enabled || p.Map is not { } map || Find(map.Cluster) is null || Buildings is not { } buildings) return;
        int[] houses = [.. buildings.All.Where(IsHouse).Select(t => (int)t.Id)];
        if (houses.Length > 0) p.Session.Send(new UpdateUnlockedBuildingsEvent { d57 = houses });
    }
}
