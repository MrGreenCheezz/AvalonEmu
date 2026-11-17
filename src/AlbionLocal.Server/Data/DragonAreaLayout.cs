using System.Globalization;
using System.Numerics;
using System.Text;

namespace AlbionLocal.Server.Data;

/// <summary>
/// The layout of one dragon area instance (analysis/work/stageR/dragon-areas/SPEC.md, "SPEC" in this file, §4): the a80 of layout type 4 (a88)
/// the client reads from ChangeCluster key 3 / SelectCharacter key 4 (a80.aq 0x180511780) and builds the instance from (ciy.a_9 0x18096E2F0
/// needs key 4, the clustertype, then bg3.abo(a88, clustertype, tier) 0x180682760: id a80.c, file a80.b, display "@GENERIC_DRAGON_AREA_CLUSTER",
/// time region US_05_00, the minimap rectangle from a80.f). The client does not generate it: it places exactly the templates the bytes name,
/// so the server's walls (<see cref="ClusterTemplate.FromLayout"/> of the same layout) are the client's. The a80 base as for the Mists (no room
/// bytes: a9r.g writes them for type 2 only), the MiniMapData with the material preset "Dragon" (bzm static +8, passed by cdq.ka 0x180891080
/// to ba4.f), then the a88 tail (a88.ke 0x180531D50): k the mode (cdr.c), l the map id (cdr.d), the byte n (cdr.b, the map's difficultylevel),
/// m (cdr.e, "default" when empty: cdr's cctor).
/// </summary>
public sealed record DragonAreaLayout : A80Layout
{
    /// <summary>MiniMapData.MiniMapMaterialPreset of a dragon area (bzm cctor: static +8 "Dragon").</summary>
    public const string MaterialPreset = "Dragon";

    /// <summary>a80.a of an a88.</summary>
    public const byte LayoutType = 4;

    /// <summary>a88.m when the generation input names none (cdr cctor: "default").</summary>
    public const string DefaultVariant = "default";

    public required MiniMap MiniMap { get; init; }

    /// <summary>a88.k: the mode name (DRAGON_AREA_1_TO_1_ORANGE).</summary>
    public required string Mode { get; init; }

    /// <summary>a88.l: the map id (DRAGON_AREA_1_TO_1_ORANGE of MAPSET_1_TO_1_PLAYERS_ORANGE).</summary>
    public required string Map { get; init; }

    /// <summary>a88.n: the map's difficulty level (0).</summary>
    public byte Level { get; init; }

    /// <summary>a88.m (cdr.e).</summary>
    public string Variant { get; init; } = DefaultVariant;

    public override byte[] Write()
    {
        using var ms = new MemoryStream();
        using (var w = new BinaryWriter(ms, new UTF8Encoding(false), leaveOpen: true))
        {
            WriteHead(w, LayoutType, MiniMap, rooms: false);
            w.Write(Mode); w.Write(Map); w.Write(Level); w.Write(Variant); // a88.ke: k, l, n, m
        }
        return ms.ToArray();
    }

    /// <summary>The client's reader (a80.aq + a9r.g + a88.kf 0x180531DF0) for the self-test; <see cref="A80Layout.ClusterType"/> comes back empty. A broken buffer throws <see cref="InvalidDataException"/>.</summary>
    public static DragonAreaLayout Read(byte[] bytes)
    {
        try
        {
            using var r = new BinaryReader(new MemoryStream(bytes), new UTF8Encoding(false));
            if (r.ReadByte() != LayoutType) throw new InvalidDataException("not a layout of type 4 (a88)");
            var (id, name, origin, size, mini, templates) = ReadHead(r, rooms: false);
            var layout = new DragonAreaLayout
            {
                Id = id, Name = name, Origin = origin, Size = size, Templates = templates, ClusterType = "", MiniMap = mini,
                Mode = r.ReadString(), Map = r.ReadString(), Level = r.ReadByte(), Variant = r.ReadString(),
            };
            if (r.BaseStream.Position != r.BaseStream.Length) throw new InvalidDataException("trailing bytes");
            return layout;
        }
        catch (Exception e) when (e is EndOfStreamException or IOException)
        {
            throw new InvalidDataException($"layout does not parse: {e.Message}", e);
        }
    }
}

/// <summary>
/// The dragon area instance ids (a4l ClusterInstanceType DRAGONAREA 14): "@DRAGONAREA@" + the Guid, as the Mists' "@MISTS@".
/// </summary>
public static class DragonAreaIds
{
    public const string Prefix = "@DRAGONAREA@";

    public static string InstanceId(Guid id) => Prefix + id.ToString();

    public static bool IsInstance(string? cluster) => cluster?.StartsWith(Prefix, StringComparison.Ordinal) == true;
}

/// <summary>
/// The dragon area layout generator. The client's own (cdq : ba4&lt;bzx, a88&gt;, the counterpart of the Mists' a7b) builds the template list
/// in the shared base ba4 and only fills the a88 tail in cdq.ka; its per-slot evaluator bzw.rk 0x1807D6DA0 admits a template by its faction
/// against the instance's, the instance level within the template's window and the slot tags. This port follows the Mists generator's rules
/// (<see cref="MistsGenerator.Zone"/>), which use the same base, with the dragon data's additions: the map by weight, the faction by
/// factionweightingtable (KEEPER), the master DRAGON_001 at (0, 0, 0), unturned when the map has restrictrotation (all of them), its layers those
/// of the map's rolled mastertemplaterolllayertag (orangesolo → Layer_01: the mode's mobs, chests, exits and runestones); per master slot in
/// metadata order the categories of the pool for its size whose requiredslottags the slot has, a prioritypick one before the others [infer:
/// the DRAKE_ISLAND slots get the drake island], a template by weight, unturned with restrictrotation else k·90° [infer, as the Mists], at the
/// slot's position and height, its layers those of the category's rolled tag. Whatever template a slot gets, the client builds exactly the
/// bytes it is sent, so the choice only decides which camps stand where.
/// </summary>
public static class DragonAreaGenerator
{
    /// <summary>
    /// a80.b (cdq.j8 0x180890DE0): ClusterNameInformation {Id = the map id with '_' → '-', ClusterType 25 DragonArea (DRA), Biome 9 Dragon (DR, bzm
    /// static +0), SubBiome the map's (invalid: NON), Tier the map's clustertier, Faction the drawn one, Continent 5 DragonLands (DRL)}.FullName:
    /// "DRAGON-AREA-1-TO-1-ORANGE_DRA_DR_NON_T6_KPR_DRL".
    /// </summary>
    public static string ClusterName(DragonAreaMap map, string faction) =>
        string.Create(CultureInfo.InvariantCulture, $"{map.Id.Replace('_', '-')}_DRA_DR_NON_T{map.Tier}_{DungeonLayout.FactionToken(faction)}_DRL");

    /// <summary>A map of <paramref name="mode"/>'s map set by weight; null when it has none.</summary>
    public static DragonAreaMap? PickMap(DragonAreaData data, DragonAreaMode mode, Random rng) =>
        data.MapSets.GetValueOrDefault(mode.MapSet) is { Count: > 0 } maps && maps.Where(m => m.Weight > 0).ToList() is { Count: > 0 } live
            ? DungeonGenerator.Pick(rng, [.. live.Select(m => (m, m.Weight))]) : null;

    /// <summary>The faction by factionweightingtable (KEEPER); KEEPER without one.</summary>
    public static string PickFaction(DragonAreaData data, Random rng) =>
        data.Factions.Count == 0 ? "KEEPER" : DungeonGenerator.Pick(rng, [.. data.Factions.Select(f => (f.Faction, f.Weight))]);

    /// <summary>The layers of a template whose tags hold <paramref name="tag"/>; none without a tag.</summary>
    private static List<string> LayersOf(IReadOnlyList<(string Id, string Name, string[] Tags)> layers, string? tag) =>
        tag is null ? [] : [.. layers.Where(l => l.Tags.Contains(tag, StringComparer.OrdinalIgnoreCase) || string.Equals(l.Name, tag, StringComparison.OrdinalIgnoreCase)).Select(l => l.Id)];

    /// <summary>
    /// The layout of instance <paramref name="id"/> of <paramref name="mode"/> on <paramref name="map"/>. Reads the master's template (its collider
    /// bounds): never under the world lock. Throws <see cref="InvalidDataException"/> when the master or the pool is unknown.
    /// </summary>
    public static DragonAreaLayout Build(ClusterStore store, DragonAreaData data, DragonAreaMode mode, DragonAreaMap map, string faction, string id, Random rng)
    {
        const string folder = "NONE";
        if (!data.Masters.TryGetValue(map.Master, out var master)) throw new InvalidDataException($"dragon area map {map.Id}: master {map.Master} is not in dragonareatemplatemetadata.xml");
        if (!data.Pools.TryGetValue(map.Pool, out var pool)) throw new InvalidDataException($"dragon area map {map.Id}: template pool {map.Pool} is not in dragonarea.xml");
        var masterLayout = store.Layout(folder, map.Master) ?? throw new InvalidDataException($"dragon area master {map.Master} is not in templates/{folder}");
        var masterRotation = map.RestrictRotation ? 0f : rng.Next(0, 4) * 90f;
        var masterTag = map.RollTags.Count > 0 ? DungeonGenerator.Pick(rng, map.RollTags) : null;
        var templates = new List<DungeonTemplate> { new("instanceslot_00", map.Master, folder, Vector2.Zero, masterRotation, LayersOf(master.Layers, masterTag), 0) };
        var used = new HashSet<string>(StringComparer.Ordinal);
        bool Fits(DragonPoolTemplate t) => t.Weight > 0 && !(t.Unique && used.Contains(t.Id));
        for (var i = 0; i < master.Slots.Count; i++)
        {
            var slot = master.Slots[i];
            var categories = (pool.Sizes.GetValueOrDefault(slot.Size) ?? [])
                .Where(c => c.Weight > 0 && c.RequiredSlotTags.All(t => slot.Tags.Contains(t, StringComparer.OrdinalIgnoreCase)) && c.Templates.Any(Fits)).ToList();
            if (categories.Where(c => c.PriorityPick).ToList() is { Count: > 0 } priority) categories = priority;
            if (categories.Count == 0) continue; // nothing fits this slot: it stays the master's own ground
            var category = DungeonGenerator.Pick(rng, [.. categories.Select(c => (c, c.Weight))]);
            var choice = DungeonGenerator.Pick(rng, [.. category.Templates.Where(Fits).Select(t => (t, t.Weight))]);
            if (choice.Unique) used.Add(choice.Id);
            var tag = category.RollTags.Count > 0 ? DungeonGenerator.Pick(rng, category.RollTags) : masterTag;
            var layers = data.Templates.TryGetValue(choice.Id, out var meta) ? LayersOf(meta.Layers, tag) : [];
            var (x, z) = TileMath.Rotate(slot.X, slot.Z, masterRotation);
            templates.Add(new DungeonTemplate(string.Create(CultureInfo.InvariantCulture, $"instanceslot_{i + 1:00}"), choice.Id, folder,
                new Vector2((float)x, (float)z), choice.RestrictRotation ? masterRotation : rng.Next(0, 4) * 90f, layers, slot.Y));
        }
        var (origin, size) = MistsGenerator.Turned(masterLayout.ColBoundsMin ?? master.MiniMap.Min, masterLayout.ColBoundsMax ?? master.MiniMap.Max, masterRotation);
        var (miniOrigin, miniSize) = MistsGenerator.Turned(master.MiniMap.Min, master.MiniMap.Max, masterRotation);
        return new DragonAreaLayout
        {
            Id = id, Name = ClusterName(map, faction), Origin = origin, Size = size, Templates = templates, DisplayName = mode.DisplayName, ClusterType = mode.ClusterType,
            MiniMap = master.MiniMap with { Min = miniOrigin, Max = miniOrigin + miniSize }, Mode = mode.Name, Map = map.Id, Level = map.Level,
        };
    }
}
