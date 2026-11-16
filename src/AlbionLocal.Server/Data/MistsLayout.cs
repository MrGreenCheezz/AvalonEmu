using System.Globalization;
using System.Numerics;
using System.Text;

namespace AlbionLocal.Server.Data;

/// <summary>
/// The layout of one mists zone (analysis/work/stageL/mists/SPEC.md, "SPEC" in this file, §3.2, §6.3): the a80 of layout type 2 (a9i) the
/// client reads from ChangeCluster key 3 / SelectCharacter key 4 and builds the zone from (bg3.abo(a9i, clustertype, tier) 0x180681EC0):
/// the a80 base with the master's MiniMapData (material preset "Mists"), every a9r with its own height and an i32 0 room array (open
/// mists have no rooms), and the a9i tail {t mode, u map id, v rarity} (a9i.ke 0x180947430). <see cref="Write"/> mirrors the client's
/// writer, <see cref="Read"/> its reader (a80.aq + a9r.g + a9i.kf) for the self-test.
/// </summary>
public sealed record MistsLayout : A80Layout
{
    /// <summary>MiniMapData.MiniMapMaterialPreset of a mists zone (a7c.b, gen.c l.227: "Mists").</summary>
    public const string MaterialPreset = "Mists";

    /// <summary>a80.a of an a9i.</summary>
    public const byte LayoutType = 2;

    /// <summary>a80.f: the master's minimap bounds, height range and base level, turned with it.</summary>
    public required MiniMap MiniMap { get; init; }

    /// <summary>a9i.t (a7e.d): the mists mode name (MISTS_SOLO_YELLOW).</summary>
    public required string Mode { get; init; }

    /// <summary>a9i.u (a7e.e): the mists.xml map id (SOLO_YELLOW_COMMON_01).</summary>
    public required string Map { get; init; }

    /// <summary>a9i.v (a7e.b): the zone's rarity 0-4 (the portal's key 9).</summary>
    public byte Level { get; init; }

    /// <summary>Public issue #35, server only (not written): the rarity's rareresourcedistribution (<see cref="MistsLevel.RareDistribution"/>); null = DEFAULT.</summary>
    public string? RareDistribution { get; init; }

    public override byte[] Write()
    {
        using var ms = new MemoryStream();
        using (var w = new BinaryWriter(ms, new UTF8Encoding(false), leaveOpen: true))
        {
            WriteHead(w, LayoutType, MiniMap, rooms: true);
            w.Write(Mode); w.Write(Map); w.Write(Level);          // a9i.ke: t, u, v
        }
        return ms.ToArray();
    }

    /// <summary>The client's reader (type 2 only; <see cref="A80Layout.ClusterType"/>, which is no part of the bytes, comes back empty). A broken buffer throws <see cref="InvalidDataException"/>.</summary>
    public static MistsLayout Read(byte[] bytes)
    {
        try
        {
            using var r = new BinaryReader(new MemoryStream(bytes), new UTF8Encoding(false));
            if (r.ReadByte() != LayoutType) throw new InvalidDataException("not a layout of type 2 (a9i)");
            var (id, name, origin, size, mini, templates) = ReadHead(r, rooms: true);
            var layout = new MistsLayout
            {
                Id = id, Name = name, Origin = origin, Size = size, Templates = templates, ClusterType = "", MiniMap = mini,
                Mode = r.ReadString(), Map = r.ReadString(), Level = r.ReadByte(),
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
/// The open mists generator (SPEC §6.3, a port of a7c.a 0x180937470 / ba5.a 0x181FF8310 by its rules, not its random numbers): the map of the
/// mode's mapset by weight within its rarity window, the faction by factionweightingtable, the master at (0, 0, 0) turned k·90°, then per
/// master slot in metadata order a category of the template pool's size by weight (a unique one, misc, once) and a template of it by weight
/// (unique ones once, the rarity window), turned k·90° at the master-turned slot position with the slot's height, the layers its rolled tag
/// and its layertags switch on; the bounds are the master's collider bounds turned with it (V10).
/// </summary>
public static class MistsGenerator
{
    /// <summary>The ClusterNameInformation subbiome aliases (bgv: GRN, RED, DED) by template folder.</summary>
    public static string SubBiomeToken(string folder) => folder switch { "RED" => "RED", "DEAD" => "DED", _ => "GRN" };

    /// <summary>
    /// a80.b (a7b.j8 0x180937060, V6): "&lt;map id with '-'&gt;_MIS_MI_&lt;GRN|RED|DED&gt;_T&lt;tier&gt;_&lt;faction&gt;_MIS" (ClusterType 21 Mists, Biome 8
    /// Mists, Continent 4 Mists; short aliases as ClusterNameInformation.b() writes them). Phase B: ClusterType 22 MistsDungeon ("MID") for
    /// Knightfall Abbey, 23 ResourceMists ("RMI") for a resource zone, by the mode's type (a7b.j8's MistsModeType switch).
    /// </summary>
    public static string ClusterName(MistsMap map, string faction, string type = "mists") =>
        string.Create(CultureInfo.InvariantCulture, $"{map.Id.Replace('_', '-')}_{TypeToken(type)}_MI_{SubBiomeToken(map.Folder)}_T{map.Tier}_{DungeonLayout.FactionToken(faction)}_MIS");

    /// <summary>The ClusterType alias of a mode type: MIS (mists), MID (mistsdungeon), RMI (resourcemists).</summary>
    public static string TypeToken(string type) => type switch { "mistsdungeon" => "MID", "resourcemists" => "RMI", _ => "MIS" };

    /// <summary>
    /// A map of <paramref name="mode"/>'s mapset for rarity <paramref name="level"/> by weight (SPEC §2.3); phase B: behind a portal of
    /// <paramref name="tier"/> a dungeon map of that requiredentrancetier. Null when none fits.
    /// </summary>
    public static MistsMap? PickMap(MistsData data, MistsMode mode, int level, Random rng, int? tier = null)
    {
        var maps = data.MapsFor(mode, level).Where(m => m.Weight > 0).ToList();
        if (tier is { } t && maps.Any(m => m.EntranceTier == t)) maps = [.. maps.Where(m => m.EntranceTier == t)];
        return maps.Count == 0 ? null : DungeonGenerator.Pick(rng, [.. maps.Select(m => (m, m.Weight))]);
    }

    /// <summary>MistDungeonRoomFlags (a7d.c): NONE, ROSE, SCALE, SWORD; MOBS 8, BOSS 16.</summary>
    public static readonly byte[] RoomSets = [0, 1, 2, 4];
    public const byte RoomMobs = 8, RoomBoss = 16;

    /// <summary>The set of a room's flags (rose, scale, sword: the RoomSetting's DungeonMultiRewardObject set); "" for none.</summary>
    public static string SetOf(byte flags) => (flags & 7) switch { 1 => "rose", 2 => "scale", 4 => "sword", _ => "" };

    /// <summary>
    /// The boss rooms of a dungeon (a7c.a_t1a78 0x180937900): floor(numberofbossrooms), one more with the fraction's chance; each the first of
    /// the rooms left in random order whose centre is at least bossroompreventionradius from every boss room before it (a7c.a_t1a77
    /// 0x1809374C0; none: the first of that order); every room when there are not more rooms than that.
    /// </summary>
    public static HashSet<int> BossRooms(MistsDungeonMeta meta, MistsMode mode, Random rng)
    {
        var count = (int)mode.BossRooms;
        var fraction = mode.BossRooms - count;
        if (fraction > 0.0001 && rng.NextDouble() <= fraction) count++;
        if (count >= meta.Rooms.Count) return [.. Enumerable.Range(0, meta.Rooms.Count)];
        var left = Enumerable.Range(0, meta.Rooms.Count).ToList();
        var bosses = new HashSet<int>();
        for (var k = 0; k < count && left.Count > 0; k++)
        {
            var order = left.OrderBy(_ => rng.Next()).ToList();
            var pick = order.FirstOrDefault(i => bosses.All(b => Vector2.Distance(meta.Rooms[i].Centre, meta.Rooms[b].Centre) >= mode.BossRoomRadius), order[0]);
            left.Remove(pick);
            bosses.Add(pick);
        }
        return bosses;
    }

    /// <summary>
    /// The layout of Knightfall Abbey <paramref name="id"/> (a7c.b 0x180937D80): one template, instanceslot_00 = the map's dungeon at (0, 0, 0)
    /// unturned, each room's flag byte a random set (Next(1, 4): ROSE, SCALE, SWORD) | MOBS or | BOSS (<see cref="BossRooms"/>), the layers
    /// R#nnn_BASE and R#nnn_MOBS or R#nnn_BOSS (a7d.a); a80.d/e the dungeon's minimap rectangle, a80.f its MiniMapData with preset "Mists"; the tail
    /// {mode, map, 0}. Throws <see cref="InvalidDataException"/> when the dungeon is not in the metadata or its template is missing.
    /// </summary>
    public static MistsLayout Dungeon(ClusterStore store, MistsData data, MistsMode mode, MistsMap map, string faction, string id, Random rng)
    {
        if (!data.DungeonMetas.TryGetValue(map.Master, out var meta)) throw new InvalidDataException($"mists map {map.Id}: dungeon {map.Master} is not in miststemplatemetadata.xml");
        var folder = map.Folder;
        if (store.Layout(folder, map.Master) is null) throw new InvalidDataException($"mists dungeon {map.Master} is not in templates/{folder}");
        var flags = new byte[meta.Rooms.Count];
        for (var i = 0; i < flags.Length; i++) flags[i] = RoomSets[rng.Next(1, 4)];
        var bosses = BossRooms(meta, mode, rng);
        var layers = new List<string>();
        for (var i = 0; i < flags.Length; i++)
        {
            var boss = bosses.Contains(i);
            flags[i] |= boss ? RoomBoss : RoomMobs;
            var room = meta.Rooms[i].Index.ToString("000", CultureInfo.InvariantCulture);
            layers.Add($"R#{room}_BASE");
            layers.Add($"R#{room}_{(boss ? "BOSS" : "MOBS")}");
        }
        var mini = meta.MiniMap;
        return new MistsLayout
        {
            Id = id, Name = ClusterName(map, faction, mode.Type), Origin = mini.Min, Size = mini.Max - mini.Min, DisplayName = mode.DisplayName, ClusterType = mode.ClusterType,
            Templates = [new DungeonTemplate("instanceslot_00", map.Master, folder, Vector2.Zero, 0, layers, 0, flags)], MiniMap = mini,
            Mode = mode.Name, Map = map.Id, Level = 0,
        };
    }

    /// <summary>The zone's faction by factionweightingtable (UNDEAD 2, MORGANA 1, HERETIC 1); UNDEAD without the table.</summary>
    public static string PickFaction(MistsData data, Random rng) =>
        data.Factions.Count == 0 ? "UNDEAD" : DungeonGenerator.Pick(rng, [.. data.Factions.Select(f => (f.Faction, f.Weight))]);

    /// <summary>The rectangle <paramref name="min"/>..<paramref name="max"/> turned by <paramref name="rotation"/> degrees about (0, 0): its origin and size.</summary>
    public static (Vector2 Origin, Vector2 Size) Turned(Vector2 min, Vector2 max, float rotation)
    {
        var lo = new Vector2(float.MaxValue);
        var hi = new Vector2(float.MinValue);
        foreach (var (x, z) in new[] { (min.X, min.Y), (min.X, max.Y), (max.X, min.Y), (max.X, max.Y) })
        {
            var (rx, rz) = TileMath.Rotate(x, z, rotation);
            var p = new Vector2((float)rx, (float)rz);
            lo = Vector2.Min(lo, p);
            hi = Vector2.Max(hi, p);
        }
        return (lo, hi - lo);
    }

    /// <summary>
    /// The layout of zone <paramref name="id"/> of <paramref name="mode"/> on <paramref name="map"/> at rarity <paramref name="level"/> (steps 3-6).
    /// Reads the master's template (its collider bounds): never under the world lock. Throws <see cref="InvalidDataException"/> when the
    /// master or the pool is unknown.
    /// </summary>
    public static MistsLayout Zone(ClusterStore store, MistsData data, MistsMode mode, MistsMap map, int level, string faction, string id, Random rng)
    {
        if (!data.Masters.TryGetValue(map.Master, out var master)) throw new InvalidDataException($"mists map {map.Id}: master {map.Master} is not in miststemplatemetadata.xml");
        if (!data.Pools.TryGetValue(map.Pool, out var pool)) throw new InvalidDataException($"mists map {map.Id}: template pool {map.Pool} is not in mists.xml");
        var folder = map.Folder;
        var masterLayout = store.Layout(folder, map.Master) ?? throw new InvalidDataException($"mists master {map.Master} is not in templates/{folder}");
        var masterRotation = rng.Next(0, 4) * 90f;
        var templates = new List<DungeonTemplate> { new("instanceslot_00", map.Master, folder, Vector2.Zero, masterRotation, [], 0) };
        var usedTemplates = new HashSet<string>(StringComparer.Ordinal);
        var usedCategories = new HashSet<string>(StringComparer.Ordinal);
        bool Fits(MistsPoolTemplate t) => t.Weight > 0 && t.MinDifficulty <= level && level <= t.MaxDifficulty && !(t.Unique && usedTemplates.Contains(t.Id));
        for (var i = 0; i < master.Slots.Count; i++)
        {
            var slot = master.Slots[i];
            var categories = (pool.Sizes.GetValueOrDefault(slot.Size) ?? [])
                .Where(c => c.Weight > 0 && !(c.Unique && usedCategories.Contains(c.Name)) && c.Templates.Any(Fits)).ToList();
            if (categories.Count == 0) continue; // nothing fits this slot: it stays the master's own ground
            var category = DungeonGenerator.Pick(rng, [.. categories.Select(c => (c, c.Weight))]);
            if (category.Unique) usedCategories.Add(category.Name);
            var choice = DungeonGenerator.Pick(rng, [.. category.Templates.Where(Fits).Select(t => (t, t.Weight))]);
            if (choice.Unique) usedTemplates.Add(choice.Id);
            var tag = category.RollTags.Count > 0 ? DungeonGenerator.Pick(rng, category.RollTags) : null;
            string[] wanted = tag is null ? choice.LayerTags : [.. choice.LayerTags, tag];
            var layers = data.Templates.TryGetValue(choice.Id, out var meta) && wanted.Length > 0
                ? meta.Layers.Where(l => wanted.All(w => l.Tags.Contains(w, StringComparer.OrdinalIgnoreCase))).Select(l => l.Id).ToList() : [];
            var (x, z) = TileMath.Rotate(slot.X, slot.Z, masterRotation);
            templates.Add(new DungeonTemplate(string.Create(CultureInfo.InvariantCulture, $"instanceslot_{i + 1:00}"), choice.Id, folder,
                new Vector2((float)x, (float)z), rng.Next(0, 4) * 90f, layers, slot.Y));
        }
        var (origin, size) = Turned(masterLayout.ColBoundsMin ?? master.MiniMap.Min, masterLayout.ColBoundsMax ?? master.MiniMap.Max, masterRotation);
        var (miniOrigin, miniSize) = Turned(master.MiniMap.Min, master.MiniMap.Max, masterRotation);
        return new MistsLayout
        {
            Id = id, Name = ClusterName(map, faction, mode.Type), Origin = origin, Size = size, Templates = templates, DisplayName = mode.DisplayName,

            ClusterType = mode.ClusterType, MiniMap = master.MiniMap with { Min = miniOrigin, Max = miniOrigin + miniSize },
            Mode = mode.Name, Map = map.Id, Level = (byte)Math.Clamp(level, 0, 4), RareDistribution = mode.Level(level)?.RareDistribution,
        };
    }
}
