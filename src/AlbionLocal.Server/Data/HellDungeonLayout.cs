using System.Globalization;
using System.Numerics;
using System.Text;

namespace AlbionLocal.Server.Data;

/// <summary>
/// The layout of one Abyssal Depths floor (analysis/work/stageR/abyss/SPEC.md, "SPEC" in this file, §3): the a80 of layout type 3 (a9c) the
/// client reads from ChangeCluster key 3 / SelectCharacter key 4 and builds the instance from (ciy.a_9 for type 3 needs key 4, the clustertype, then
/// bg3.abo(a9c, clustertype, tier) 0x180682320: display "@GENERIC_HELL_DUNGEON_CLUSTER", time region US_05_00). As for the dragon areas the client
/// places exactly the templates of the bytes. The tail (a9c.ke 0x1805328B0): string o the mode, string p the map id, byte r the map's difficulty
/// level, string q the variant ("default"), then the i64 ticks of the c4 s (0 from a9c's ctor; ba7.ka 0x18056E3C0 does not set it). ba7.ka passes
/// bug's static string "HELL_DUNGEON_LEVEL_ALL" to the base where the dragon generator passes its minimap preset "Dragon" [code: bug cctor].
/// </summary>
public sealed record HellDungeonLayout : A80Layout
{
    /// <summary>MiniMapData.MiniMapMaterialPreset (bug static +8, bug cctor 0x180789B80).</summary>
    public const string MaterialPreset = "HELL_DUNGEON_LEVEL_ALL";

    /// <summary>a80.a of an a9c (a9c's ctor).</summary>
    public const byte LayoutType = 3;

    public const string DefaultVariant = "default";

    public required MiniMap MiniMap { get; init; }

    /// <summary>a9c.o: the mode name (HELL_DUNGEON_DUO_ORANGE).</summary>
    public required string Mode { get; init; }

    /// <summary>a9c.p: the map id (DUO_ORANGE_HELL_DUNGEON_01).</summary>
    public required string Map { get; init; }

    /// <summary>a9c.r: the map's difficulty level (0-2).</summary>
    public byte Level { get; init; }

    /// <summary>a9c.q (ba8.f, "default").</summary>
    public string Variant { get; init; } = DefaultVariant;

    /// <summary>a9c.s (a c4, ticks): 0 [code: never set by ba7.ka; what the official server puts there is not traced].</summary>
    public long Offset { get; init; }

    public override byte[] Write()
    {
        using var ms = new MemoryStream();
        using (var w = new BinaryWriter(ms, new UTF8Encoding(false), leaveOpen: true))
        {
            WriteHead(w, LayoutType, MiniMap, rooms: false);
            w.Write(Mode); w.Write(Map); w.Write(Level); w.Write(Variant); w.Write(Offset); // a9c.ke: o, p, r, q, s
        }
        return ms.ToArray();
    }

    /// <summary>The client's reader (a80.aq + a9c.kf 0x180532970) for the self-test. A broken buffer throws <see cref="InvalidDataException"/>.</summary>
    public static HellDungeonLayout Read(byte[] bytes)
    {
        try
        {
            using var r = new BinaryReader(new MemoryStream(bytes), new UTF8Encoding(false));
            if (r.ReadByte() != LayoutType) throw new InvalidDataException("not a layout of type 3 (a9c)");
            var (id, name, origin, size, mini, templates) = ReadHead(r, rooms: false);
            var layout = new HellDungeonLayout
            {
                Id = id, Name = name, Origin = origin, Size = size, Templates = templates, ClusterType = "", MiniMap = mini,
                Mode = r.ReadString(), Map = r.ReadString(), Level = r.ReadByte(), Variant = r.ReadString(), Offset = r.ReadInt64(),
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

/// <summary>The Abyssal Depths instance ids: a4l.a(Guid, HellDungeonModeType) 0x1809004C0 = "@" + ClusterInstanceType HELLDUNGEON (13) + "@" + the Guid.</summary>
public static class HellDungeonIds
{
    public const string Prefix = "@HELLDUNGEON@";

    public static string InstanceId(Guid id) => Prefix + id.ToString();

    public static bool IsInstance(string? cluster) => cluster?.StartsWith(Prefix, StringComparison.Ordinal) == true;
}

/// <summary>
/// The Abyssal Depths layout generator: the client's own (ba7 : ba4&lt;buq, a9c&gt;) builds the template list in the shared base ba4, as the
/// dragon areas' cdq does, so this follows <see cref="DragonAreaGenerator.Build"/>: the map's master at (0, 0, 0) unturned [assume: as the dragon
/// maps' restrictrotation; the masters' slot positions are their own frame], per metadata slot in file order the pool's categories for its size
/// (slot size Medium → pool "medium", Small → "small"), a template by weight (unique ones once), turned k·90° [infer, as the Mists], at the slot,
/// its layers those of the category's rolled tag (noboss, bossduo …). The templates come from the map's subbiome folder (DEAD).
/// </summary>
public static class HellDungeonGenerator
{
    /// <summary>
    /// a80.b (ba7.j8 0x18056E120): ClusterNameInformation {Id = the map id with '_' → '-', ClusterType 24 HellDungeon (HED), Biome bug static 0 = 2
    /// Highland (HL), SubBiome the map's (dead: DED), Tier the map's, Faction the drawn one, Continent 1 Royal (ROY)}:
    /// "DUO-ORANGE-HELL-DUNGEON-01_HED_HL_DED_T6_HER_ROY".
    /// </summary>
    public static string ClusterName(HellDungeonMap map, string faction)
    {
        var sub = map.SubBiome.ToLowerInvariant() switch { "dead" => "DED", "red" => "RED", "green" => "GRN", _ => "NON" };
        return string.Create(CultureInfo.InvariantCulture, $"{map.Id.Replace('_', '-')}_HED_HL_{sub}_T{map.Tier}_{DungeonLayout.FactionToken(faction)}_ROY");
    }

    /// <summary>The faction by factionweightingtable (HERETIC); HERETIC without one.</summary>
    public static string PickFaction(HellDungeonData data, Random rng) =>
        data.Factions.Count == 0 ? "HERETIC" : DungeonGenerator.Pick(rng, [.. data.Factions.Select(f => (f.Faction, f.Weight))]);

    private static List<string> LayersOf(IReadOnlyList<(string Id, string Name, string[] Tags)> layers, string? tag) =>
        tag is null ? [] : [.. layers.Where(l => l.Tags.Contains(tag, StringComparer.OrdinalIgnoreCase) || string.Equals(l.Name, tag, StringComparison.OrdinalIgnoreCase)).Select(l => l.Id)];

    /// <summary>The layout of instance <paramref name="id"/> of <paramref name="mode"/> on <paramref name="map"/>. Reads the master's template: never under the world lock.</summary>
    public static HellDungeonLayout Build(ClusterStore store, HellDungeonData data, HellDungeonMode mode, HellDungeonMap map, string faction, string id, Random rng)
    {
        var folder = map.Folder;
        if (!data.Masters.TryGetValue(map.Master, out var master)) throw new InvalidDataException($"hell dungeon map {map.Id}: master {map.Master} is not in helldungeonstemplatemetadata.xml");
        if (!data.Pools.TryGetValue(map.Pool, out var pool)) throw new InvalidDataException($"hell dungeon map {map.Id}: template pool {map.Pool} is not in helldungeons.xml");
        var masterLayout = store.Layout(folder, map.Master) ?? throw new InvalidDataException($"hell dungeon master {map.Master} is not in templates/{folder}");
        var templates = new List<DungeonTemplate> { new("instanceslot_00", map.Master, folder, Vector2.Zero, 0, [], 0) };
        var used = new HashSet<string>(StringComparer.Ordinal);
        bool Fits(DragonPoolTemplate t) => t.Weight > 0 && !(t.Unique && used.Contains(t.Id));
        for (var i = 0; i < master.Slots.Count; i++)
        {
            var slot = master.Slots[i];
            var categories = (pool.Sizes.GetValueOrDefault(slot.Size) ?? []).Where(c => c.Weight > 0 && c.Templates.Any(Fits)).ToList();
            if (categories.Count == 0) continue; // nothing fits: the master's own ground
            var category = DungeonGenerator.Pick(rng, [.. categories.Select(c => (c, c.Weight))]);
            var choice = DungeonGenerator.Pick(rng, [.. category.Templates.Where(Fits).Select(t => (t, t.Weight))]);
            if (choice.Unique) used.Add(choice.Id);
            var tag = category.RollTags.Count > 0 ? DungeonGenerator.Pick(rng, category.RollTags) : null;
            var layers = data.Templates.TryGetValue(choice.Id, out var meta) ? LayersOf(meta.Layers, tag) : [];
            templates.Add(new DungeonTemplate(string.Create(CultureInfo.InvariantCulture, $"instanceslot_{i + 1:00}"), choice.Id, folder,
                new Vector2(slot.X, slot.Z), choice.RestrictRotation ? 0 : rng.Next(0, 4) * 90f, layers, slot.Y));
        }
        var origin = masterLayout.ColBoundsMin ?? master.MiniMap.Min;
        var size = (masterLayout.ColBoundsMax ?? master.MiniMap.Max) - origin;
        return new HellDungeonLayout
        {
            Id = id, Name = ClusterName(map, faction), Origin = origin, Size = size, Templates = templates, DisplayName = mode.DisplayName, ClusterType = mode.ClusterType,
            MiniMap = master.MiniMap, Mode = mode.Name, Map = map.Id, Level = (byte)Math.Clamp(map.Level, 0, 255),
        };
    }
}
