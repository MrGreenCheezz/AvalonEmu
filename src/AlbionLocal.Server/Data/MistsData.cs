using System.Globalization;
using System.Numerics;
using System.Xml.Linq;

namespace AlbionLocal.Server.Data;

/// <summary>
/// mists.xml and miststemplatemetadata.xml as the Mists use them (analysis/work/stageL/mists/SPEC.md, "SPEC" in this file, §2.1-§2.4): the
/// global settings, the 11 modes with their difficulty levels, treasures and open-world exit weights, the 11 mapsets (309 maps), the 21
/// template pools, the faction weights, and the metadata's 23 masters with their 83 slots, 51 templates with their layers and 3 dungeons
/// with their rooms. Read with the first open-world cluster that has random exit markers or with a mists instance (off the world thread,
/// <see cref="ClusterStore.Mists"/>); immutable.
/// </summary>
public sealed class MistsData
{
    /// <summary>&lt;globalsettings&gt;: the attributes by name (mistscityentrancetargetclusterid "5001", openworldexitportalalivetimer 30, …).</summary>
    public IReadOnlyDictionary<string, string> Globals { get; private init; } = new Dictionary<string, string>();

    /// <summary>&lt;modetypesetting&gt; by mode type (mists, mistsdungeon, resourcemists): instancerandomdungeondifficultygroup ("mists" or none).</summary>
    public IReadOnlyDictionary<string, string?> DifficultyGroups { get; private init; } = new Dictionary<string, string?>();

    public IReadOnlyDictionary<string, MistsMode> Modes { get; private init; } = new Dictionary<string, MistsMode>();

    /// <summary>&lt;mistsmaps&gt; mapset by name, its maps in file order.</summary>
    public IReadOnlyDictionary<string, IReadOnlyList<MistsMap>> Mapsets { get; private init; } = new Dictionary<string, IReadOnlyList<MistsMap>>();

    public IReadOnlyDictionary<string, MistsTemplatePool> Pools { get; private init; } = new Dictionary<string, MistsTemplatePool>();

    /// <summary>&lt;factionweightingtable&gt; (MORGANA 1, UNDEAD 2, HERETIC 1).</summary>
    public IReadOnlyList<(string Faction, double Weight)> Factions { get; private init; } = [];

    /// <summary>miststemplatemetadata.xml masters by name (minimap data and slots in file order).</summary>
    public IReadOnlyDictionary<string, MistsMaster> Masters { get; private init; } = new Dictionary<string, MistsMaster>();

    /// <summary>miststemplatemetadata.xml templates by name (size, faction, layers with their tags).</summary>
    public IReadOnlyDictionary<string, MistsTemplateMeta> Templates { get; private init; } = new Dictionary<string, MistsTemplateMeta>();

    /// <summary>miststemplatemetadata.xml dungeons by name: the room count (MISTS_DNG_UND_01: 124).</summary>
    public IReadOnlyDictionary<string, int> Dungeons { get; private init; } = new Dictionary<string, int>();

    /// <summary>Phase B (SPEC §3.2): miststemplatemetadata.xml dungeons by name with their minimap data and rooms (a7c.b's input).</summary>
    public IReadOnlyDictionary<string, MistsDungeonMeta> DungeonMetas { get; private init; } = new Dictionary<string, MistsDungeonMeta>();

    /// <summary>
    /// Phase B (SPEC §2.2): &lt;mistdungeonsettings&gt; RoomSetting by name (default, duo): per set (rose, scale, sword) the weighted rewards of a
    /// Knightfall Abbey room, a LootChest (MD_UNDEAD_SOLO_REWARD, 15) or a Shrine (MD_SHRINE_ROSE_BUFF, 85).
    /// </summary>
    public IReadOnlyDictionary<string, IReadOnlyDictionary<string, IReadOnlyList<MistsRoomReward>>> RoomSettings { get; private init; } =
        new Dictionary<string, IReadOnlyDictionary<string, IReadOnlyList<MistsRoomReward>>>();

    /// <summary>Phase B (SPEC §2.1): &lt;modetypesetting&gt; wisp timing by mode type (mists 300 / 60 / 60, mistsdungeon 300 / 0 / 0, resourcemists 120 / 0 / 0).</summary>
    public IReadOnlyDictionary<string, MistsWispTiming> WispTimings { get; private init; } = new Dictionary<string, MistsWispTiming>();

    /// <summary>
    /// Phase B (op 477, br4.a: the mode's number in load order, 0- or 1-based untraced, SPEC Q6): the open mode a static entrance's
    /// <paramref name="id"/> names. The open modes stand at even places (0, 2, 4) and the dungeons after them, so a 0-based id names an open
    /// mode directly and a 1-based one names it at <paramref name="id"/> - 1; null for neither.
    /// </summary>
    public MistsMode? StaticMode(int id)
    {
        var order = Modes.Values.ToList();
        foreach (var i in new[] { id, id - 1 })
            if (i >= 0 && i < order.Count && order[i].Type == "mists") return order[i];
        return null;
    }

    /// <summary>All maps of all mapsets.</summary>
    public int MapCount => Mapsets.Values.Sum(m => m.Count);

    /// <summary>A global setting as a number; <paramref name="fallback"/> when absent.</summary>
    public double Global(string name, double fallback) =>
        Globals.TryGetValue(name, out var v) && double.TryParse(v, NumberStyles.Float, CultureInfo.InvariantCulture, out var d) ? d : fallback;

    /// <summary>mistscityentrancetargetclusterid: the world.xml cluster every city entrance leads to (5001, Brecilien's entrance hub).</summary>
    public string CityTarget => Globals.GetValueOrDefault("mistscityentrancetargetclusterid") is { Length: > 0 } c ? c : "5001";

    /// <summary>
    /// SPEC §2.3 rule [inference]: the maps of <paramref name="mode"/>'s mapset a zone of rarity <paramref name="level"/> may draw: those with
    /// mindifficulty ≤ level ≤ maxdifficulty (absent: 0 and 4).
    /// </summary>
    public IReadOnlyList<MistsMap> MapsFor(MistsMode mode, int level) =>
        Mapsets.TryGetValue(mode.Mapset, out var maps) ? [.. maps.Where(m => m.MinDifficulty <= level && level <= m.MaxDifficulty)] : [];

    /// <summary>The difficulty group a mode's instances take their mob level factors from (randomdungeons.xml DifficultyLevels; "mists").</summary>
    public string? DifficultyGroupOf(MistsMode mode) => DifficultyGroups.GetValueOrDefault(mode.Type);

    public static MistsData Load(XElement mists, XElement metadata)
    {
        static double D(XElement e, string a, double fallback = 0) =>
            double.TryParse((string?)e.Attribute(a), NumberStyles.Float, CultureInfo.InvariantCulture, out var v) ? v : fallback;
        static int I(XElement e, string a, int fallback = 0) =>
            int.TryParse((string?)e.Attribute(a), NumberStyles.Integer, CultureInfo.InvariantCulture, out var v) ? v : fallback;
        static string S(XElement e, string a) => (string?)e.Attribute(a) ?? "";
        static bool B(XElement e, string a) => string.Equals((string?)e.Attribute(a), "true", StringComparison.OrdinalIgnoreCase);
        static Vector2 V(XElement e, string a) => TileMath.V2((string?)e.Attribute(a)) ?? Vector2.Zero;

        var globals = mists.Element("globalsettings");
        var groups = new Dictionary<string, string?>(StringComparer.Ordinal);
        var wisps = new Dictionary<string, MistsWispTiming>(StringComparer.Ordinal);
        foreach (var t in globals?.Elements("modetypesetting") ?? [])
        {
            groups[S(t, "type")] = (string?)t.Attribute("instancerandomdungeondifficultygroup");
            wisps[S(t, "type")] = new MistsWispTiming(D(t, "wispsspawninsecbeforeclustershutdown", 300), D(t, "wispsstaggeredspawninterval"), D(t, "wispsstaggeredspawnintervalstart"));
        }
        var rooms = new Dictionary<string, IReadOnlyDictionary<string, IReadOnlyList<MistsRoomReward>>>(StringComparer.Ordinal);
        foreach (var r in mists.Element("mistdungeonsettings")?.Elements("RoomSetting") ?? [])
            rooms.TryAdd(S(r, "name"), r.Elements("DungeonMultiRewardObject").ToDictionary(o => S(o, "set"), o => (IReadOnlyList<MistsRoomReward>)
                [.. o.Elements().Select(x => new MistsRoomReward(x.Name.LocalName, x.Name.LocalName == "Shrine" ? S(x, "buff") : S(x, "name"), D(x, "weight", 1), (string?)x.Attribute("prefab")))],
                StringComparer.OrdinalIgnoreCase));

        var modes = new Dictionary<string, MistsMode>(StringComparer.Ordinal);
        foreach (var m in mists.Element("mistsmodes")?.Elements("mistsmode") ?? [])
            modes.TryAdd(S(m, "name"), new MistsMode
            {
                Name = S(m, "name"), Type = S(m, "type"), ClusterType = S(m, "clustertype"), DisplayName = S(m, "clusterdisplayname"),
                MaxTeamSize = Math.Max(1, I(m, "maxteamsize", 1)), Mapset = S(m, "mapset"),
                Levels = [.. m.Elements("difficultylevel").Select(l => new MistsLevel(I(l, "level"), (string?)l.Attribute("wispmob"),
                    (string?)l.Element("wispinstancedifficultyrollref")?.Attribute("randomdungeonrollref")) { WispVisualization = (string?)l.Attribute("wispmobexitvisualizationref"), RareDistribution = (string?)l.Attribute("rareresourcedistribution") })],
                Treasures = [.. m.Element("treasures")?.Elements("subtype").Select(t => new MistsTreasure(S(t, "name"), Math.Max(0, I(t, "rolls", 1)), D(t, "chance", 1),
                    D(t, "initialblocktimeseconds"), D(t, "finalblocktimeseconds"), I(t, "mindifficulty"))) ?? []],
                OpenWorldExits = [.. m.Element("openworldexitdistribution")?.Elements("warningtype").Select(w => (S(w, "type"), D(w, "weight", 1))) ?? []],
                ExitsLeadTo = (string?)m.Attribute("exitsleadtomode"), BossRooms = D(m, "numberofbossrooms"), BossRoomRadius = D(m, "bossroompreventionradius"),
                RoomSetting = (string?)m.Attribute("mistdungeonroomsetting") ?? "default",
                DungeonEntrances = m.Element("specialexitsettings")?.Elements("exitsettings").Where(x => S(x, "type") == "MistsDungeonEntrance")
                    .Select(x => new MistsSpecialExit(S(x, "targetmode"), D(x, "minwaveinterval", 170), D(x, "maxwaveinterval", 214), D(x, "exitspawnchance", 1),
                        D(x, "initialblocktimeseconds"), D(x, "finalblocktimeseconds"), D(x, "playerdistanceblocking"), D(x, "portaldisappearancetimer", 120),
                        [.. x.Elements("exit").Select(e => new MistsSpecialExitTier(I(e, "tier", 5), D(e, "weight", 1), S(e, "randomdungeonexitvisualizationref"), I(e, "mindifficulty")))]))
                    .FirstOrDefault(),
            });

        var mapsets = new Dictionary<string, IReadOnlyList<MistsMap>>(StringComparer.Ordinal);
        foreach (var set in mists.Element("mistsmaps")?.Elements("mapset") ?? [])
            mapsets.TryAdd(S(set, "name"), [.. set.Elements("map").Select(m => new MistsMap
            {
                Id = S(m, "id"), Master = S(m, "mastertemplate"), Pool = S(m, "templatepool"), Tier = I(m, "clustertier", 5), SubBiome = S(m, "subbiome"),
                SoftMax = I(m, "softplayerlimitmax", 10), Weight = D(m, "weight", 1), CloseSeconds = D(m, "clusterclosetime", 600),
                ShutdownSeconds = D(m, "clustershutdowntime", 1200), MinDifficulty = I(m, "mindifficulty", 0), MaxDifficulty = I(m, "maxdifficulty", 4),
                EntranceTier = I(m, "requiredentrancetier", 0),
            })]);

        var pools = new Dictionary<string, MistsTemplatePool>(StringComparer.Ordinal);
        foreach (var p in mists.Element("misttemplates")?.Elements("templatepool") ?? [])
        {
            var sizes = new Dictionary<string, IReadOnlyList<MistsCategory>>(StringComparer.OrdinalIgnoreCase);
            foreach (var size in p.Elements("size"))
                sizes[S(size, "name")] = [.. size.Elements("type").Select(t => new MistsCategory(S(t, "category"), D(t, "weight", 1), B(t, "unique"),
                    [.. t.Element("rolllayertag")?.Elements("layertag").Select(l => (S(l, "name"), D(l, "weight", 1))) ?? []],
                    [.. t.Elements("template").Select(x => new MistsPoolTemplate(S(x, "id"), D(x, "weight", 1), B(x, "unique"),
                        S(x, "layertags").Split('|', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries),
                        I(x, "mindifficulty", 0), I(x, "maxdifficulty", 4)))]))];
            pools.TryAdd(S(p, "id"), new MistsTemplatePool(S(p, "id"), sizes));
        }

        var masters = new Dictionary<string, MistsMaster>(StringComparer.Ordinal);
        foreach (var m in metadata.Elements("master"))
        {
            var range = V(m, "minimapHeightRange");
            masters.TryAdd(S(m, "name"), new MistsMaster(S(m, "name"),
                new MiniMap(V(m, "minimapBoundsMin"), V(m, "minimapBoundsMax"), B(m, "minimapHeightRangeEnabled"), range, B(m, "minimapBaseLevelEnabled"),
                    (float)D(m, "minimapBaseLevel"), MistsLayout.MaterialPreset),
                [.. m.Elements("slot").Select(s =>
                {
                    var (x, y, z) = TileMath.V3((string?)s.Attribute("pos"));
                    return new MistsSlot(S(s, "name"), (float)x, (float)y, (float)z, S(s, "size"));
                })]));
        }
        var templates = new Dictionary<string, MistsTemplateMeta>(StringComparer.Ordinal);
        foreach (var t in metadata.Elements("template"))
            templates.TryAdd(S(t, "name"), new MistsTemplateMeta(S(t, "name"), S(t, "size"), S(t, "faction"),
                [.. t.Elements("layer").Select(l => (S(l, "id"), S(l, "name").Trim(), S(l, "tags").Split('|', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)))]));
        var dungeons = metadata.Elements("dungeon").ToDictionary(d => S(d, "name"), d => d.Elements("room").Count(), StringComparer.Ordinal);
        var dungeonMetas = new Dictionary<string, MistsDungeonMeta>(StringComparer.Ordinal);
        foreach (var d in metadata.Elements("dungeon"))
            dungeonMetas.TryAdd(S(d, "name"), new MistsDungeonMeta(S(d, "name"),
                new MiniMap(V(d, "minimapBoundsMin"), V(d, "minimapBoundsMax"), B(d, "minimapHeightRangeEnabled"), V(d, "minimapHeightRange"), B(d, "minimapBaseLevelEnabled"),
                    (float)D(d, "minimapBaseLevel"), MistsLayout.MaterialPreset),
                [.. d.Elements("room").Select(r => new MistsRoom(I(r, "idx"), [.. r.Elements("rect").Select(x => (V(x, "vMin"), V(x, "vMax")))]))]));

        return new MistsData
        {
            Globals = globals?.Attributes().ToDictionary(a => a.Name.LocalName, a => a.Value, StringComparer.Ordinal) ?? [],
            DifficultyGroups = groups, Modes = modes, Mapsets = mapsets, Pools = pools,
            Factions = [.. mists.Element("factionweightingtable")?.Elements("faction").Select(f => (S(f, "id"), D(f, "weight", 1))) ?? []],
            Masters = masters, Templates = templates, Dungeons = dungeons, DungeonMetas = dungeonMetas, RoomSettings = rooms, WispTimings = wisps,
        };
    }
}

/// <summary>
/// A &lt;mistsmode&gt; (SPEC §2.2): type (mists, mistsdungeon, resourcemists), the worldsettings clustertype (ChangeCluster key 4), its display
/// name, the team size, the mapset, the five difficulty levels (the wisp mob and the instance's single mob-level roll), the treasures and the
/// warning-type weights of its open-world exit.
/// </summary>
public sealed record MistsMode
{
    public required string Name { get; init; }
    public required string Type { get; init; }
    public required string ClusterType { get; init; }
    public string DisplayName { get; init; } = "";
    public int MaxTeamSize { get; init; } = 1;
    public required string Mapset { get; init; }
    public IReadOnlyList<MistsLevel> Levels { get; init; } = [];
    public IReadOnlyList<MistsTreasure> Treasures { get; init; } = [];
    public IReadOnlyList<(string Type, double Weight)> OpenWorldExits { get; init; } = [];

    /// <summary>Phase B: exitsleadtomode, the mode a dungeon's or a resource zone's ways on lead into (MISTS_SOLO_YELLOW, MISTS_SOLO_BLACK); null = its own.</summary>
    public string? ExitsLeadTo { get; init; }

    /// <summary>Phase B (a7c.b, br4 +0x30/+0x34): a dungeon mode's numberofbossrooms (3) and bossroompreventionradius (90 m).</summary>
    public double BossRooms { get; init; }
    public double BossRoomRadius { get; init; }

    /// <summary>Phase B: mistdungeonroomsetting (br4 +0x28, default "default"; the duo dungeon "duo").</summary>
    public string RoomSetting { get; init; } = "default";

    /// <summary>Phase B (SPEC §2.2): the MistsDungeonEntrance &lt;specialexitsettings&gt; of an open mode (the Knightfall Abbey portals); null = none.</summary>
    public MistsSpecialExit? DungeonEntrances { get; init; }

    /// <summary>The zone kind: Knightfall Abbey (mistsdungeon) or a resource zone (resourcemists); else an open zone (mists).</summary>
    public bool IsDungeon => Type == "mistsdungeon";
    public bool IsResource => Type == "resourcemists";

    /// <summary>A duo mode (maxteamsize 2): the client asks a player without a party first (bum.v).</summary>
    public bool Duo => MaxTeamSize > 1;

    /// <summary>The level entry of rarity <paramref name="level"/>; null when the mode has none.</summary>
    public MistsLevel? Level(int level) => Levels.FirstOrDefault(l => l.Level == level);
}

/// <summary>
/// A &lt;difficultylevel&gt; of a mode: the rarity, its portal wisp (phase B: MOB_UNIQUE_MISTS_PORTAL_WISP_*, and the RandomExitVisualization of the
/// portal its death leaves) and the randomdungeons RandomLevelReroll of the instance's mob level.
/// </summary>
public sealed record MistsLevel(int Level, string? WispMob, string? InstanceRoll)
{
    public string? WispVisualization { get; init; }

    /// <summary>
    /// Public issue #35: the level's rareresourcedistribution (mists.xml: MISTS_E0 … MISTS_E4, whose enchanted weights rise with the rarity in
    /// rareresourcedistribution.xml), the rare-state weights of a zone's resource nodes in place of world.xml's attribute.
    /// </summary>
    public string? RareDistribution { get; init; }
}

/// <summary>Phase B: a mode's MistsDungeonEntrance waves (SPEC §2.2): the target mode, wave interval, chance, block times, distance to players, life, tiers.</summary>
public sealed record MistsSpecialExit(string TargetMode, double MinWaveSeconds, double MaxWaveSeconds, double Chance, double InitialBlockSeconds, double FinalBlockSeconds,
    double PlayerDistance, double LifeSeconds, IReadOnlyList<MistsSpecialExitTier> Tiers);

/// <summary>One &lt;exit&gt; of a MistsDungeonEntrance: its tier (the dungeon map's requiredentrancetier), weight, RandomExitVisualization and lowest rarity.</summary>
public sealed record MistsSpecialExitTier(int Tier, double Weight, string Visualization, int MinDifficulty);

/// <summary>Phase B: a RoomSetting reward: Kind LootChest (its LootChestSpawnpoint name) or Shrine (its buffshrines.xml buff), weight, the shrine's prefab.</summary>
public sealed record MistsRoomReward(string Kind, string Name, double Weight, string? Prefab);

/// <summary>Phase B: a &lt;modetypesetting&gt;'s wisps: they come in the last BeforeShutdown seconds, from IntervalStart on every Interval seconds (0 = once).</summary>
public sealed record MistsWispTiming(double BeforeShutdown, double Interval, double IntervalStart);

/// <summary>Phase B: a metadata &lt;dungeon&gt;: its minimap data and rooms.</summary>
public sealed record MistsDungeonMeta(string Name, MiniMap MiniMap, IReadOnlyList<MistsRoom> Rooms);

/// <summary>A dungeon &lt;room idx&gt; with its rectangles (vMin, vMax); its centre is that of their bounds (a7i, a7c.b's boss spacing).</summary>
public sealed record MistsRoom(int Index, IReadOnlyList<(Vector2 Min, Vector2 Max)> Rects)
{
    public Vector2 Centre => Rects.Count == 0 ? Vector2.Zero
        : (Rects.Select(r => r.Min).Aggregate(Vector2.Min) + Rects.Select(r => r.Max).Aggregate(Vector2.Max)) / 2;

    public bool Contains(Vector2 p) => Rects.Any(r => p.X >= r.Min.X && p.X <= r.Max.X && p.Y >= r.Min.Y && p.Y <= r.Max.Y);
}

/// <summary>A &lt;treasures&gt; subtype: rolls × chance, the block times between which one unlocks, and the lowest rarity it comes at.</summary>
public sealed record MistsTreasure(string Subtype, int Rolls, double Chance, double InitialBlockSeconds, double FinalBlockSeconds, int MinDifficulty);

/// <summary>A &lt;map&gt; of a mapset (SPEC §2.3): master template, template pool, tier, subbiome (the template folder), limits, weight, times, rarity window.</summary>
public sealed record MistsMap
{
    public required string Id { get; init; }
    public required string Master { get; init; }
    public required string Pool { get; init; }
    public int Tier { get; init; } = 5;
    public string SubBiome { get; init; } = "green";
    public int SoftMax { get; init; } = 10;
    public double Weight { get; init; } = 1;
    public double CloseSeconds { get; init; } = 600;
    public double ShutdownSeconds { get; init; } = 1200;
    public int MinDifficulty { get; init; }
    public int MaxDifficulty { get; init; } = 4;

    /// <summary>Phase B: a dungeon map's requiredentrancetier (the tier of the portal it is behind); 0 = any.</summary>
    public int EntranceTier { get; init; }

    /// <summary>The templates/ folder of the subbiome (green → GREEN, red → RED, dead → DEAD).</summary>
    public string Folder => SubBiome.ToUpperInvariant() is "RED" or "DEAD" ? SubBiome.ToUpperInvariant() : "GREEN";

    /// <summary>The ClusterNameInformation subbiome value (0 green, 1 red, 2 dead).</summary>
    public int SubBiomeValue => Folder switch { "RED" => 1, "DEAD" => 2, _ => 0 };
}

/// <summary>A &lt;templatepool&gt;: its categories per slot size (medium, small).</summary>
public sealed record MistsTemplatePool(string Id, IReadOnlyDictionary<string, IReadOnlyList<MistsCategory>> Sizes);

/// <summary>A &lt;type category&gt; of a pool size: weight, unique (misc: once per zone), the rolled layer tags (resource: t5..t8) and its templates.</summary>
public sealed record MistsCategory(string Name, double Weight, bool Unique, IReadOnlyList<(string Tag, double Weight)> RollTags, IReadOnlyList<MistsPoolTemplate> Templates);

/// <summary>A pool &lt;template&gt;: weight, unique, the layer tags its layers need (fiber, ore, …) and the rarity window.</summary>
public sealed record MistsPoolTemplate(string Id, double Weight, bool Unique, string[] LayerTags, int MinDifficulty, int MaxDifficulty);

/// <summary>A metadata &lt;master&gt;: its minimap data (SPEC §6.3 step 4) and slots in file order.</summary>
public sealed record MistsMaster(string Name, MiniMap MiniMap, IReadOnlyList<MistsSlot> Slots);

/// <summary>A master &lt;slot&gt;: name (Medium_01, Small_0n; not unique), position in the master's frame, size (Medium, Small).</summary>
public sealed record MistsSlot(string Name, float X, float Y, float Z, string Size);

/// <summary>A metadata &lt;template&gt;: size, faction and its layers (id, name, tags such as "fiber|t5").</summary>
public sealed record MistsTemplateMeta(string Name, string Size, string Faction, IReadOnlyList<(string Id, string Name, string[] Tags)> Layers);

/// <summary>
/// The Mists' instance ids (SPEC §3.1, a4l: "@" + ClusterInstanceType + "@" + the Guid): MISTSDUNGEON 10, MISTS 11, RESOURCEMISTS 12.
/// Phase A makes "@MISTS@" zones, phase B also Knightfall Abbey ("@MISTSDUNGEON@") and the resource zones ("@RESOURCEMISTS@").
/// </summary>
public static class MistsIds
{
    public const string Prefix = "@MISTS@", DungeonPrefix = "@MISTSDUNGEON@", ResourcePrefix = "@RESOURCEMISTS@";

    /// <summary>The prefix of mode type <paramref name="type"/> + the Guid (lower case, hyphens: no '_' or '.', which the name parse splits at).</summary>
    public static string InstanceId(Guid id, string type = "mists") =>
        (type switch { "mistsdungeon" => DungeonPrefix, "resourcemists" => ResourcePrefix, _ => Prefix }) + id.ToString();

    /// <summary>Whether <paramref name="cluster"/> is a Knightfall Abbey instance.</summary>
    public static bool IsDungeon(string? cluster) => cluster?.StartsWith(DungeonPrefix, StringComparison.Ordinal) == true;


    /// <summary>Whether <paramref name="cluster"/> is the id of a mists instance of any of the three kinds.</summary>
    public static bool IsInstance(string? cluster) =>
        cluster is not null && (cluster.StartsWith(Prefix, StringComparison.Ordinal) || cluster.StartsWith(DungeonPrefix, StringComparison.Ordinal)
                                || cluster.StartsWith(ResourcePrefix, StringComparison.Ordinal));
}
