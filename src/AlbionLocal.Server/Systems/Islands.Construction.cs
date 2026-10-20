using System.Globalization;
using System.Numerics;
using AlbionLocal.Photon;
using AlbionLocal.Server.Data;
using AlbionLocal.Server.Entities;
using AlbionLocal.Server.Game;

namespace AlbionLocal.Server.Systems;

/// <summary>
/// Islands phase 5 (analysis/work/stageN/islands5/SPEC.md): a player builds the farm buildings of buildings.xml (T1_FARMHOUSE, T1_HERBGARDEN,
/// T1_PASTURE, T1_KENNEL: the farmbuildings with &lt;buildablegrounds&gt;) on the plots of the own island from the client's build menu
/// (ConstructionSiteCreate 68), paying the building's placecost in silver and its &lt;craftingrequirements&gt; from the inventory at once (the
/// construction site of the official game is not modelled: the finished building stands at once), and gives one up again (BuildingGiveup 64;
/// destructionreturnfactor 0: nothing comes back). The plots are the island template's buildable ground (tiles.xml BuildingAndFertile,
/// <see cref="ClusterTemplate.BuildCells"/>): a building's 20 × 20 m needs the four 10 m cells under it free and active, so every level
/// allows what its template activates (level 0 one plot, level 5 sixteen). Also here: picking a placed animal up (PlaceableObjectPickup 71)
/// and farmingmodifiers.xml's island yield bonus.
/// </summary>
public sealed partial class Islands
{
    /// <summary>The size of a buildable ground cell (tiles.xml width/height 10).</summary>
    public const float CellSize = 10;

    /// <summary>The side of a farm building (tiles.xml T1_FARMHOUSE … width/height 20): 2 × 2 cells.</summary>
    public const float BuildingSize = 20;

    /// <summary>
    /// Whether <paramref name="template"/> can be built by a player on an island: a buildings.xml farmbuilding with &lt;buildablegrounds&gt;
    /// (the four of the farming category), or the T2 player house (analysis/work/stageN/labourers/SPEC.md §1.1).
    /// </summary>
    public static bool Buildable(BuildingTemplate template) =>
        template.Kind == "farmbuilding" && template.Element.Element("buildablegrounds") is not null
        || IsHouse(template) && template.Tier == 2; // labourers SPEC §1.1: the T2 house from the build menu, the higher tiers by upgrading it

    /// <summary>The silver (a7m) of placing <paramref name="template"/>: its placecost (100 for the pasture, 50 for the farm) [whole silver: SPEC §1.2].</summary>
    public static long PlaceCost(BuildingTemplate template) =>
        (long)Math.Round(double.TryParse(template.Attrs.GetValueOrDefault("placecost"), NumberStyles.Float, CultureInfo.InvariantCulture, out var c) ? c * Pricing.A7m : 0);

    /// <summary>The resources a building takes (&lt;craftingrequirements&gt;&lt;craftresource uniquename count&gt;).</summary>
    public static IReadOnlyList<(string Item, int Count)> BuildCost(BuildingTemplate template) =>
        [.. (template.Element.Element("craftingrequirements")?.Elements("craftresource") ?? [])
            .Select(r => ((string?)r.Attribute("uniquename") ?? "", TileMath.Int((string?)r.Attribute("count"))))
            .Where(r => r.Item1.Length > 0 && r.Item2 > 0)];

    /// <summary>
    /// The plot a building placed at <paramref name="at"/> on <paramref name="map"/> takes: the nearest centre (within half a cell per axis) whose
    /// four 10 m cells are all buildable ground of the island's template, none of them under another building of the island. Null with the reason.
    /// </summary>
    public Vector2? PlotAt(Map map, Island island, Vector2 at, out string why)
    {
        why = "";
        var cells = map.Template?.BuildCells ?? [];
        if (cells.Count == 0)
        {
            why = "the island's template has no buildable ground";
            return null;
        }
        var half = CellSize / 2;
        Vector2? best = null;
        foreach (var c in cells)
            foreach (var corner in (ReadOnlySpan<Vector2>)[new(half, half), new(-half, half), new(half, -half), new(-half, -half)])
            {
                var centre = c + corner;
                var d = centre - at;
                if (MathF.Abs(d.X) > half + 0.01f || MathF.Abs(d.Y) > half + 0.01f) continue;
                if (best is { } b && Vector2.Distance(b, at) <= Vector2.Distance(centre, at)) continue;
                if (!Covered(cells, centre)) continue;
                best = centre;
            }
        if (best is not { } plot)
        {
            why = string.Create(CultureInfo.InvariantCulture, $"({at.X:0.#}, {at.Y:0.#}) is on no free buildable ground of the island (level {island.Level})");
            return null;
        }
        if (BuildingsOf(island.Id).FirstOrDefault(o => MathF.Abs(o.X - plot.X) < BuildingSize - 0.01f && MathF.Abs(o.Z - plot.Y) < BuildingSize - 0.01f) is { } taken)
        {
            why = string.Create(CultureInfo.InvariantCulture, $"the plot at ({plot.X:0.#}, {plot.Y:0.#}) overlaps the {taken.Name} at ({taken.X:0.#}, {taken.Z:0.#})");
            return null;
        }
        return plot;
    }

    /// <summary>Whether the four cells around <paramref name="centre"/> are all in <paramref name="cells"/>.</summary>
    private static bool Covered(IReadOnlyList<Vector2> cells, Vector2 centre)
    {
        var half = CellSize / 2;
        foreach (var off in (ReadOnlySpan<Vector2>)[new(half, half), new(-half, half), new(half, -half), new(-half, -half)])
        {
            var want = centre + off;
            var found = false;
            foreach (var c in cells)
                if (Vector2.DistanceSquared(c, want) < 0.25f) { found = true; break; }
            if (!found) return false;
        }
        return true;
    }

    /// <summary>The number of plots of <paramref name="template"/> (2 × 2 cells, disjoint, greedy): what .island plots shows.</summary>
    public static int PlotCount(ClusterTemplate template)
    {
        var free = template.BuildCells.ToList();
        var n = 0;
        foreach (var c in template.BuildCells.OrderBy(c => c.X).ThenBy(c => c.Y))
        {
            var centre = c + new Vector2(CellSize / 2, CellSize / 2);
            if (!Covered(free, centre)) continue;
            free.RemoveAll(f => MathF.Abs(f.X - centre.X) < CellSize && MathF.Abs(f.Y - centre.Y) < CellSize);
            n++;
        }
        return n;
    }

    /// <summary>
    /// The checks of ConstructionSiteCreate by <paramref name="p"/>: on its own island, building type <paramref name="typeId"/> buildable
    /// (<see cref="Buildable"/>), a free plot at <paramref name="at"/> (<see cref="PlotAt"/>), the silver and the resources in the inventory.
    /// The template, the plot, the silver and the resources, or the refusal.
    /// </summary>
    public (BuildingTemplate? Template, Vector2 Plot, long Silver, List<(ItemTemplate Item, int Count)> Cost, string Why) CheckConstruct(Player p, int typeId, Vector2 at)
    {
        List<(ItemTemplate, int)> cost = [];
        if (!Enabled || p.Map is not { } map || Find(map.Cluster) is not { } island) return (null, default, 0, cost, "not on an island");
        if (island.Owner != p.Character.Id) return (null, default, 0, cost, $"{island} is not the player's");
        if (typeId is < short.MinValue or > short.MaxValue || Buildings?[(short)typeId] is not { } template) return (null, default, 0, cost, $"no building type {typeId}");
        if (!Buildable(template)) return (null, default, 0, cost, $"{template.Name} ({template.Kind}) cannot be built on an island");
        if (PlotAt(map, island, at, out var why) is not { } plot) return (null, default, 0, cost, why);
        foreach (var (name, count) in BuildCost(template))
        {
            if (world.Items.Find(name) is not { } item) return (null, default, 0, cost, $"{template.Name} needs {name}, which items.xml does not have");
            var have = p.Inventory.Bag.Items.Where(i => i.Template == item).Sum(i => i.Count);
            if (have < count) return (null, default, 0, cost, $"{template.Name} needs {count} x {name}, the inventory has {have}");
            cost.Add((item, count));
        }
        var silver = PlaceCost(template);
        if (p.Character.Silver < silver) return (null, default, 0, cost, $"{p.Character.Silver / Pricing.A7m} silver, placing {template.Name} costs {silver / Pricing.A7m}");
        return (template, plot, silver, cost, "");
    }

    /// <summary>
    /// BuildingGiveup's checks: player building object <paramref name="id"/> of <paramref name="p"/>'s own island, no animal placed in it (pick
    /// them up first, PlaceableObjectPickup). The building, or the refusal.
    /// </summary>
    public (PlayerBuilding? Building, string Why) CheckGiveup(Player p, long id)
    {
        if (p.Map?.Objects.GetValueOrDefault(id) is not PlayerBuilding b) return (null, $"object {id} is no island building");
        if (b.Island.Owner != p.Character.Id) return (null, $"{b.Template.Name} of {b.Island} is not the player's");
        var animals = _crops.Values.Count(f => f.Building == b.Building.Id && world.Items.Find(f.Item)?.Animal is not null);
        if (animals > 0) return (null, $"{animals} animal(s) still in the {b.Template.Name}: pick them up first");
        if (LabourersOf(b.Building.Id).Count is > 0 and var labourers) return (null, $"{labourers} labourer(s) still live in the {b.Template.Name}: dismiss them first");
        if (FurnitureOf(b.Building.Id).Count is > 0 and var furniture) return (null, $"{furniture} piece(s) of furniture still in the {b.Template.Name}: pick them up first");
        return (b, "");
    }

    // ---- picking up (PlaceableObjectPickup 71) ----

    /// <summary>
    /// The checks of picking farmable <paramref name="id"/> up (client FarmableGui.OnPickupPressed 0x18110FA40 → co0.af 0x180B8CC00 → c32.j
    /// 0x180A77A90, request y6 {0 id}): an own placed item whose items.xml pickupable is true (the animals; seeds are not), within reach. The
    /// object, or the refusal. The client asks first when it is still growing ("will cause it to lose all progress and food").
    /// </summary>
    public (FarmableObject? Object, string Why) CheckPickup(Player p, long id)
    {
        if (p.Map?.Objects.GetValueOrDefault(id) is not FarmableObject o) return (null, $"object {id} is no placed farmable");
        if (o.Island.Owner != p.Character.Id) return (null, $"{o} is not the player's");
        if (!string.Equals(o.Seed.Attrs.GetValueOrDefault("pickupable"), "true", StringComparison.OrdinalIgnoreCase)) return (null, $"{o.Seed.Name} cannot be picked up");
        if (!InReach(p, o)) return (null, $"{o} is out of reach");
        return (o, "");
    }

    // ---- farmingmodifiers.xml: the island yield bonus ----

    private Dictionary<(string City, string Farmable), double>? _islandYield;

    /// <summary>
    /// The island yield bonus of <paramref name="farmable"/> (a seed, or a grown animal for its products) on <paramref name="island"/>:
    /// farmingmodifiers.xml &lt;location clusterid="&lt;the island's city&gt;"&gt;&lt;farmingyieldmodifier farmable islandvalue&gt; (0.1 for the
    /// city's specialities, e.g. Lymhurst carrots, burdock, goose eggs, pumpkins); 0 for the rest or without the file.
    /// </summary>
    public double IslandYieldBonus(Island island, string farmable)
    {
        if (_islandYield is null)
        {
            _islandYield = [];
            try
            {
                if (world.Clusters?.Folder is { } folder)
                    foreach (var loc in GameDataFile.Open(folder, "farmingmodifiers.bin").Root().Elements("location"))
                        if ((string?)loc.Attribute("clusterid") is { Length: > 0 } city)
                            foreach (var m in loc.Elements("farmingyieldmodifier"))
                                if ((string?)m.Attribute("farmable") is { Length: > 0 } name
                                    && double.TryParse((string?)m.Attribute("islandvalue"), NumberStyles.Float, CultureInfo.InvariantCulture, out var v))
                                    _islandYield[(city, name.ToUpperInvariant())] = v;
            }
            catch (Exception e) when (e is InvalidDataException or IOException or System.Xml.XmlException)
            {
                Log.Warn("world", $"farmingmodifiers.xml not read, no island yield bonus: {e.Message}");
            }
        }
        return _islandYield.GetValueOrDefault((island.City, farmable.ToUpperInvariant()));
    }

    /// <summary>The factor on the items of a harvest or product: (1 + <see cref="IslandYieldBonus"/>) × the owner's premium factor.</summary>
    public double YieldFactor(FarmableObject o, double premium) => (1 + IslandYieldBonus(o.Island, o.Seed.Name)) * premium;
}
