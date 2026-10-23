using System.Xml.Linq;

namespace AlbionLocal.Server.Data;

/// <summary>
/// One templates/&lt;FOLDER&gt;/&lt;ref&gt;.template.bin reduced to what the cluster resolver places, in the template's own
/// frame (SPEC stage 2 §2 "Resolution"): its criticalscenemembers with their tile and components, and every tile with a
/// component of <see cref="TileKinds"/>: top-level tiles, tiles of a layergroup layer (with that layer's id, decided
/// per template instance), and the children of compound tiles (tiles.xml compoundtile, its direct children always,
/// those in a compound layer when the template's compoundtile lists that layer in activelayers), placed through the
/// compound's position and roty. tiles/criticaltile entries repeat the critical members and are skipped. Built once per
/// (folder, ref) and shared by every cluster using the template (<see cref="ClusterStore"/>); the parsed XML is not kept:
/// merged and inline components are detached copies.
/// <para>
/// The numbers (<see cref="SpawnKey.Index"/>) come from the template's structure alone, never from the kinds placed, so
/// placing one more kind later (a lootchest, a shrine) moves no key: in file order, one per criticalscenemember; one per
/// component of each tile (at least one, also for a tile without any), top-level or in a layergroup layer, active or
/// not; one for each compound tile, then the numbers of all its tiles.xml children (<see cref="TileIndex.CompoundSize"/>,
/// <see cref="CompoundChild.Number"/>); one for any other entry (criticaltile, customspawnpos, roads).
/// </para>
/// <para>
/// Stage I bots (bots SPEC §1.6): beside them, unnumbered, every tile with walk-blocking cells (<see cref="Colliders"/>: top-level,
/// in a layergroup layer, compound children of an active compound layer) and the road graphs of any layer (<see cref="Roads"/>),
/// which the cluster places like the rest (<see cref="ClusterTemplate.Colliders"/>, <see cref="ClusterTemplate.Roads"/>).
/// </para>
/// </summary>
internal sealed class TemplateLayout
{
    /// <summary>The components of ordinary tiles the resolver turns into objects (<see cref="ClusterTemplate.Resolve"/>).</summary>
    public static readonly IReadOnlySet<string> TileKinds = new HashSet<string>(StringComparer.Ordinal)
    {
        "harvestable", "mobspawnpoint", "resourcenode", "locationmarker", "fishingzone", "tutorialblocker",
        "randomdungeonrandomexitpositionmarker", "randomdungeonfixedexit", // stageG dungeons: the exit markers and the fixed exits
        "lootchestspawnpoint", // stageG dungeons: the chests of the reward layers; stageH: the open world's chest spots
        "localquestarea", // stageH bosses: the camp quest circles
        "expeditionexit", "expeditionnarrator", "expeditioncheckpoint", // stageH expeditions: the instance objects (SPEC §2.7)
        "islandmanagement", // stageH islands: the island management spot (NewIslandManagement, SPEC §1.5)
        "namedmarkerpoint", // stageL corrupted: the invader spawn points (InvaderSpawnpoint, SPEC §4.4); stageL hellgates: the objective chest, exit portal, chain exit
        "shrine", // stageN shrines: a buff shrine (NewShrine 396, SPEC §2)
        "hellgateshrine", // stageL hellgates: a Hell Den's hellgate shrine (NewHellgateShrine, SPEC §3.1)
        "arearestrictionsettings", // stageL hellgates phase B: the HellgateFxManager's restricted area (SPEC §2.4)
        "treasurespawnpoint", "mistswispspawn", "unrestrictedpvpzone", // stageL mists phase B: the dungeon's treasures, the wisp spawns, the camps' FFA circles
        "warcamp", // stage N arena: an arena team's camp and its spawn positions (SPEC §3)
    };

    public required string Reference { get; init; }

    /// <summary>The folder the file was found in.</summary>
    public required string Folder { get; init; }

    public required IReadOnlyList<LocalMember> Members { get; init; }

    public required IReadOnlyList<LocalTile> Tiles { get; init; }

    /// <summary>Stage I bots: the tiles with walk-blocking cells, in the template frame (<see cref="TileIndex.Collision"/>).</summary>
    public IReadOnlyList<LocalCollider> Colliders { get; init; } = [];

    /// <summary>Stage I bots: the &lt;roads nodes links&gt; graphs of the template (bots-world SPEC §1.2: any layer, never by its name).</summary>
    public IReadOnlyList<LocalRoad> Roads { get; init; } = [];

    /// <summary>
    /// Islands phase 5 (stageN islands5 SPEC §1): the buildable 10 × 10 m ground cells, tiles.xml groundtype BuildingAndFertile (the
    /// &lt;BIOME&gt;_GROUND_UNIVERSAL[_CORNER|_END|_MID|_SIDE] tiles; the _INACTIVE_ ones are Normal), top level or in a layergroup layer.
    /// </summary>
    public IReadOnlyList<LocalCell> BuildCells { get; init; } = [];

    /// <summary>Whether tile <paramref name="name"/> is buildable island ground (<see cref="BuildCells"/>).</summary>
    public static bool IsBuildGround(string name) =>
        name.EndsWith("_GROUND_UNIVERSAL", StringComparison.Ordinal) || name.Contains("_GROUND_UNIVERSAL_", StringComparison.Ordinal)
        && (name.EndsWith("_CORNER", StringComparison.Ordinal) || name.EndsWith("_END", StringComparison.Ordinal)
            || name.EndsWith("_MID", StringComparison.Ordinal) || name.EndsWith("_SIDE", StringComparison.Ordinal))
        && !name.Contains("_INACTIVE", StringComparison.Ordinal);

    /// <summary>StageL mists (SPEC §6.3 step 4, V10): the template's collider bounds (colBoundsMin, colBoundsMax), a mists zone's a80.d/e; null without.</summary>
    public System.Numerics.Vector2? ColBoundsMin { get; init; }

    public System.Numerics.Vector2? ColBoundsMax { get; init; }

    /// <summary>The name of each layergroup layer by id (UNDEAD_DYN_STATIC, …): what a rolled dynamic template switches on (<see cref="DynamicRoll.LayerOn"/>).</summary>
    public IReadOnlyDictionary<string, string> LayerNames { get; init; } = new Dictionary<string, string>();

    public static TemplateLayout Build(XElement root, TileIndex index, string folder, string reference)
    {
        var n = 0; // the next number (see the summary)
        var members = new List<LocalMember>();
        foreach (var m in root.Element("criticalscenemembers")?.Elements("criticalscenemember") ?? [])
        {
            var number = n++;
            if (m.Element("tile") is not { } t) continue;
            var name = (string?)t.Attribute("name") ?? "";
            var (x, y, z) = TileMath.V3((string?)t.Attribute("pos"));
            members.Add(new LocalMember(number, (string?)m.Attribute("id") ?? "", (string?)m.Attribute("type") ?? "", (string?)m.Attribute("layerId") ?? "",
                name, x, y, z, TileMath.YawOf(t), [.. Merge(index.Components(name), [.. t.Elements()], _ => true).Kept.Select(k => k.Component)]));
        }

        var tiles = new List<LocalTile>();
        var colliders = new List<LocalCollider>();
        var roads = new List<LocalRoad>();
        var cells = new List<LocalCell>();
        var layerNames = new Dictionary<string, string>(StringComparer.Ordinal);
        void Tile(XElement e, string? layer)
        {
            var name = (string?)e.Attribute("name") ?? "";
            var (x, y, z) = TileMath.V3((string?)e.Attribute("pos"));
            var yaw = TileMath.YawOf(e);
            if (index.Collision(name) is { } grid) colliders.Add(new LocalCollider(layer, grid, x, z, yaw));
            if (IsBuildGround(name)) cells.Add(new LocalCell(layer, x, z));
            var (kept, count) = Merge(index.Components(name), [.. e.Elements()], TileKinds.Contains);
            foreach (var (k, c) in kept)
                tiles.Add(new LocalTile(n + k, layer, name, x, y, z, yaw, c));
            n += Math.Max(1, count);
        }
        void Compound(XElement e, string? layer)
        {
            var name = (string?)e.Attribute("name") ?? "";
            var first = n + 1; // n is the compound's own number, its children's follow
            n = first + index.CompoundSize(name);
            var (x, y, z) = TileMath.V3((string?)e.Attribute("pos"));
            var frame = new Placement(x, y, z, TileMath.YawOf(e));
            var active = ((string?)e.Attribute("activelayers") ?? "").Split(' ', StringSplitOptions.RemoveEmptyEntries);
            foreach (var child in index.CompoundColliders(name))
            {
                if (child.Layer is not null && !active.Contains(child.Layer)) continue;
                var (cx, _, cz) = frame.Apply(child.X, child.Y, child.Z);
                colliders.Add(new LocalCollider(layer, index.Collision(child.Name)!, cx, cz, TileMath.Mod360(frame.Yaw + child.Yaw)));
            }
            var children = index.Compound(name);
            if (children.Count == 0) return;
            foreach (var child in children)
            {
                if (child.Layer is not null && !active.Contains(child.Layer)) continue;
                var (wx, wy, wz) = frame.Apply(child.X, child.Y, child.Z);
                var yaw = TileMath.Mod360(frame.Yaw + child.Yaw);
                var components = index.Components(child.Name);
                for (var k = 0; k < components.Count; k++)
                    if (TileKinds.Contains(components[k].Name.LocalName))
                        tiles.Add(new LocalTile(first + child.Number + k, layer, child.Name, wx, wy, wz, yaw, components[k]));
            }
        }
        foreach (var e in root.Element("tiles")?.Elements() ?? [])
        {
            switch (e.Name.LocalName)
            {
                case "tile": Tile(e, null); break;
                case "compoundtile": Compound(e, null); break;
                case "layergroup":
                    foreach (var layer in e.Elements("layer"))
                    {
                        var id = (string?)layer.Attribute("id"); // a layer without one is never active, but numbered
                        if (id is not null && (string?)layer.Attribute("name") is { } layerName) layerNames.TryAdd(id, layerName);
                        foreach (var child in layer.Elements())
                            if (id is not null && child.Name.LocalName == "tile") Tile(child, id);
                            else if (id is not null && child.Name.LocalName == "compoundtile") Compound(child, id);
                            else
                            {
                                if (id is not null && child.Name.LocalName == "roads" && LocalRoad.Read(child, id) is { } layerRoad) roads.Add(layerRoad);
                                n++;
                            }
                    }
                    break;
                default: // criticaltile (a member again), customspawnpos, roads
                    if (e.Name.LocalName == "roads" && LocalRoad.Read(e, null) is { } road) roads.Add(road);
                    n++;
                    break;
            }
        }
        return new TemplateLayout
        {
            Reference = reference, Folder = folder, Members = members, Tiles = tiles, LayerNames = layerNames, Colliders = colliders, Roads = roads, BuildCells = cells,
            ColBoundsMin = TileMath.V2((string?)root.Attribute("colBoundsMin")), ColBoundsMax = TileMath.V2((string?)root.Attribute("colBoundsMax")),
        };
    }

    /// <summary>
    /// The components of a template tile (clusterdump.py GameData.components): each inline one merged over the tiles.xml
    /// one of the same name (attributes merged, inline win; the inline children, else the definition's), then the
    /// definition's other ones. Kept: those <paramref name="keep"/> wants, each with its place K in that whole list;
    /// Count: the length of the whole list. The result never belongs to the template's XML.
    /// </summary>
    private static (List<(int K, XElement Component)> Kept, int Count) Merge(IReadOnlyList<XElement> definition, IReadOnlyList<XElement> inline,
        Func<string, bool> keep)
    {
        var kept = new List<(int, XElement)>();
        var k = 0;
        foreach (var c in inline)
        {
            if (keep(c.Name.LocalName))
            {
                if (definition.FirstOrDefault(d => d.Name == c.Name) is { } d)
                {
                    var merged = new XElement(c.Name, d.Attributes());
                    foreach (var a in c.Attributes()) merged.SetAttributeValue(a.Name, a.Value);
                    merged.Add(c.HasElements ? c.Elements() : d.Elements());
                    kept.Add((k, merged));
                }
                else kept.Add((k, new XElement(c)));
            }
            k++;
        }
        foreach (var d in definition)
        {
            if (inline.Any(c => c.Name == d.Name)) continue; // merged above
            if (keep(d.Name.LocalName)) kept.Add((k, d));
            k++;
        }
        return (kept, k);
    }
}

/// <summary>A critical scene member in the template frame; <see cref="Index"/> = its <see cref="SpawnKey.Index"/> (its place among the members).</summary>
internal sealed record LocalMember(int Index, string Id, string Type, string Layer, string Tile, double X, double Y, double Z, double Yaw,
    IReadOnlyList<XElement> Components);

/// <summary>
/// One component of a tile in the template frame; <see cref="Layer"/> = the layergroup layer it is in (null = always
/// placed), <see cref="Index"/> = its <see cref="SpawnKey.Index"/> (the tile's first number + the component's place).
/// </summary>
internal readonly record struct LocalTile(int Index, string? Layer, string Tile, double X, double Y, double Z, double Yaw, XElement Component);

/// <summary>
/// Stage I bots: a tile with walk-blocking cells in the template frame (x, z, yaw); <see cref="Layer"/> = the layergroup layer it is in
/// (null = always placed). A compound's child is already placed through the compound.
/// </summary>
internal readonly record struct LocalCollider(string? Layer, TileCollision Grid, double X, double Z, double Yaw);

/// <summary>Islands phase 5: a buildable ground cell's centre in the template frame; <see cref="Layer"/> as <see cref="LocalCollider.Layer"/>.</summary>
internal readonly record struct LocalCell(string? Layer, double X, double Z);

/// <summary>
/// Stage I bots (bots-world SPEC §1.2): one &lt;roads nodes="x z;x z;…" links="0 1;1 2;…"/&gt; in the template frame; <see cref="Layer"/> = its
/// layergroup layer (null = top level). The client's own use of roads is not traced; the bots' patrols walk them.
/// </summary>
internal sealed record LocalRoad(string? Layer, IReadOnlyList<(double X, double Z)> Nodes, IReadOnlyList<(int A, int B)> Links)
{
    /// <summary>The roads element <paramref name="e"/>; null when its nodes do not read or a link names no node.</summary>
    public static LocalRoad? Read(XElement e, string? layer)
    {
        try
        {
            var nodes = ((string?)e.Attribute("nodes") ?? "").Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .Select(p => TileMath.V3(p)).Select(v => (X: v.X, Z: v.Y)).ToList();
            var links = new List<(int, int)>();
            foreach (var l in ((string?)e.Attribute("links") ?? "").Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            {
                var ends = l.Split(' ', StringSplitOptions.RemoveEmptyEntries);
                if (ends.Length != 2 || !int.TryParse(ends[0], out var a) || !int.TryParse(ends[1], out var b) || a < 0 || b < 0 || a >= nodes.Count || b >= nodes.Count) return null;
                links.Add((a, b));
            }
            return nodes.Count == 0 ? null : new LocalRoad(layer, nodes, links);
        }
        catch (InvalidDataException) { return null; }
    }
}
