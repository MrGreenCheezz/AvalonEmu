using System.Globalization;
using System.Numerics;
using System.Xml.Linq;

namespace AlbionLocal.Server.Data;

/// <summary>
/// helldungeons.xml and helldungeonstemplatemetadata.xml, the Abyssal Depths ("hell dungeons" in the client; analysis/work/stageR/abyss/SPEC.md,
/// "SPEC" here, §1): the global settings (the static entrance's open and close timers, the blocker debuffs, the party entrance timers), the modes
/// (HELL_DUNGEON_DUO_ORANGE, _TRIO_ORANGE: in file order, the id op 532 key 2 names, bum's ctor numbering them from 0 [code: bum..ctor 0x18078E8B0
/// takes a static counter]) and their map sets (one map per difficulty level 0-2, each with its master, pool, tier, subbiome, soft player limit,
/// downward exits open and life times), the template pools (the dragon areas' format) and the metadata's masters and pool templates. Immutable.
/// </summary>
public sealed class HellDungeonData
{
    /// <summary>&lt;globalsettings staticentranceopentimer&gt; (300 s): how long a city static entrance stays Open [infer: the Open half of its cycle].</summary>
    public double StaticOpenSeconds { get; private init; } = 300;

    /// <summary>&lt;globalsettings staticentranceclosetimer&gt; (300 s): how long it stays Closed.</summary>
    public double StaticCloseSeconds { get; private init; } = 300;

    /// <summary>&lt;globalsettings staticentranceblockerdebuff&gt; (cannotuseobject helldungeonstaticentrance 70 s) and its long variant (300 s, feature switch 155).</summary>
    public string BlockerDebuff { get; private init; } = "HELL_DUNGEON_JOINED_FROM_STATIC_ENTRANCE_DEBUFF";

    public string BlockerDebuffLong { get; private init; } = "HELL_DUNGEON_JOINED_FROM_STATIC_ENTRANCE_DEBUFF_LONG";

    /// <summary>&lt;globalsettings citypartyentrancetimer&gt; (60 s): party members entering from the city this long after the first join its instance [infer].</summary>
    public double CityPartySeconds { get; private init; } = 60;

    /// <summary>&lt;globalsettings downwardpartyentrancetimer&gt; (300 s): the same for a downward exit [infer].</summary>
    public double DownwardPartySeconds { get; private init; } = 300;

    /// <summary>&lt;globalsettings chestexitprefabtilename&gt;, chestexitchannelseconds (2), chestexitclosetimeseconds (60).</summary>
    public string ChestExitTile { get; private init; } = "HIGHLAND_DEAD_DNG_HELL_EXIT_CHEST";

    /// <summary>&lt;modetypesetting playerdeathvisualevent&gt; and the rest are client-side only.</summary>
    public IReadOnlyList<HellDungeonMode> Modes { get; private init; } = [];

    /// <summary>&lt;mapset&gt; name → its &lt;map&gt;s.</summary>
    public IReadOnlyDictionary<string, IReadOnlyList<HellDungeonMap>> MapSets { get; private init; } = new Dictionary<string, IReadOnlyList<HellDungeonMap>>();

    /// <summary>&lt;helldungeontemplates&gt; pools by id (the dragon areas' format: size → categories with a rolled layer tag and templates).</summary>
    public IReadOnlyDictionary<string, DragonTemplatePool> Pools { get; private init; } = new Dictionary<string, DragonTemplatePool>();

    /// <summary>&lt;factionweightingtable&gt; (HERETIC 1).</summary>
    public IReadOnlyList<(string Faction, double Weight)> Factions { get; private init; } = [];

    /// <summary>The metadata's masters (HELLDNG_MASTER_01_LVL1-3) by name.</summary>
    public IReadOnlyDictionary<string, DragonMaster> Masters { get; private init; } = new Dictionary<string, DragonMaster>();

    /// <summary>The metadata's pool templates by name (size, faction, layers by tag).</summary>
    public IReadOnlyDictionary<string, MistsTemplateMeta> Templates { get; private init; } = new Dictionary<string, MistsTemplateMeta>();

    /// <summary>The mode of op 532's key 2: its place in file order (0-based [code: bum's counter]); a 1-based id is taken at id - 1 as a fallback [assume].</summary>
    public HellDungeonMode? ModeById(int id) =>
        id >= 0 && id < Modes.Count ? Modes[id] : id == Modes.Count && id > 0 ? Modes[id - 1] : null;

    public HellDungeonMode? Mode(string name) => Modes.FirstOrDefault(m => string.Equals(m.Name, name, StringComparison.OrdinalIgnoreCase));

    /// <summary>The map of <paramref name="mode"/>'s map set at difficulty level <paramref name="level"/> (by weight when several); null when there is none.</summary>
    public HellDungeonMap? MapAt(HellDungeonMode mode, int level, Random rng) =>
        MapSets.GetValueOrDefault(mode.MapSet)?.Where(m => m.Level == level && m.Weight > 0).ToList() is { Count: > 0 } maps
            ? DungeonGenerator.Pick(rng, [.. maps.Select(m => (m, m.Weight))]) : null;

    public static HellDungeonData Load(XElement root, XElement? metadata = null)
    {
        static double D(XElement e, string a, double fallback) =>
            double.TryParse((string?)e.Attribute(a), NumberStyles.Float, CultureInfo.InvariantCulture, out var v) ? v : fallback;
        static string S(XElement e, string a) => (string?)e.Attribute(a) ?? "";
        static bool B(XElement e, string a) => string.Equals((string?)e.Attribute(a), "true", StringComparison.OrdinalIgnoreCase);
        static IReadOnlyList<(string, double)> Rolls(XElement? e) => [.. e?.Elements("layertag").Select(l => (S(l, "name"), D(l, "weight", 1))).Where(l => l.Item2 > 0) ?? []];
        var modes = (root.Element("helldungeonmodes")?.Elements("helldungeonmode") ?? []).Select((m, i) => new HellDungeonMode(i, S(m, "name"), S(m, "clustertype"),
            S(m, "clusterdisplayname"), (int)D(m, "maxteamsize", 1), S(m, "mapset"), D(m, "soulexitopenduration", 0), D(m, "itempowerrequirement", 0))
        {
            DifficultyGroup = (string?)m.Attribute("instancerandomdungeondifficultygroup"),
        }).ToList();
        var sets = new Dictionary<string, IReadOnlyList<HellDungeonMap>>(StringComparer.Ordinal);
        foreach (var s in root.Element("helldungeonmaps")?.Elements("mapset") ?? [])
            if ((string?)s.Attribute("name") is { Length: > 0 } name)
                sets[name] = [.. s.Elements("map").Select(m => new HellDungeonMap(S(m, "id"), (int)D(m, "difficultylevel", 0), S(m, "mastertemplate"), S(m, "templatepool"),
                    (int)D(m, "clustertier", 0), S(m, "subbiome"))
                {
                    Weight = D(m, "weight", 1), SoftLimit = (int)D(m, "softplayerlimitmax", 16), DownwardOpenMin = (int)D(m, "downwardexitsopenmin", 0),
                    DownwardOpenMax = (int)D(m, "downwardexitsopenmax", 0), CloseSeconds = D(m, "clusterclosetime", 300), ShutdownStartSeconds = D(m, "clustershutdownstart", 720),
                    ShutdownDamageSeconds = D(m, "clustershutdowndamagestart", 900), ShutdownSeconds = D(m, "clustershutdowntime", 1020),
                })];
        var pools = new Dictionary<string, DragonTemplatePool>(StringComparer.Ordinal);
        foreach (var p in root.Element("helldungeontemplates")?.Elements("templatepool") ?? [])
        {
            var sizes = new Dictionary<string, IReadOnlyList<DragonCategory>>(StringComparer.OrdinalIgnoreCase);
            foreach (var size in p.Elements("size"))
                sizes[S(size, "name")] = [.. size.Elements("type").Select(t => new DragonCategory(S(t, "category"), D(t, "weight", 1), Rolls(t.Element("rolllayertag")), [], false,
                    [.. t.Elements("template").Select(x => new DragonPoolTemplate(S(x, "id"), D(x, "weight", 1), B(x, "unique"), B(x, "restrictrotation")))]))];
            pools.TryAdd(S(p, "id"), new DragonTemplatePool(S(p, "id"), sizes));
        }
        var masters = new Dictionary<string, DragonMaster>(StringComparer.Ordinal);
        var templates = new Dictionary<string, MistsTemplateMeta>(StringComparer.Ordinal);
        if (metadata is not null)
        {
            static Vector2 V(XElement e, string a) => TileMath.V2((string?)e.Attribute(a)) ?? Vector2.Zero;
            static IReadOnlyList<(string Id, string Name, string[] Tags)> Layers(XElement e) =>
                [.. e.Elements("layer").Select(l => (S(l, "id"), S(l, "name").Trim(), S(l, "tags").Split('|', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)))];
            foreach (var m in metadata.Elements("master"))
                masters.TryAdd(S(m, "name"), new DragonMaster(S(m, "name"),
                    new MiniMap(V(m, "minimapBoundsMin"), V(m, "minimapBoundsMax"), B(m, "minimapHeightRangeEnabled"), V(m, "minimapHeightRange"), B(m, "minimapBaseLevelEnabled"),
                        (float)D(m, "minimapBaseLevel", 0), HellDungeonLayout.MaterialPreset),
                    Layers(m),
                    [.. m.Elements("slot").Select(s =>
                    {
                        var (x, y, z) = TileMath.V3((string?)s.Attribute("pos"));
                        return new DragonSlot(S(s, "name"), (float)x, (float)y, (float)z, S(s, "size"), []);
                    })]));
            foreach (var t in metadata.Elements("template"))
                templates.TryAdd(S(t, "name"), new MistsTemplateMeta(S(t, "name"), S(t, "size"), S(t, "faction"), Layers(t)));
        }
        var g = root.Element("globalsettings");
        string G(string a, string fallback) => g is not null && S(g, a) is { Length: > 0 } v ? v : fallback;
        return new HellDungeonData
        {
            StaticOpenSeconds = g is null ? 300 : D(g, "staticentranceopentimer", 300),
            StaticCloseSeconds = g is null ? 300 : D(g, "staticentranceclosetimer", 300),
            BlockerDebuff = G("staticentranceblockerdebuff", "HELL_DUNGEON_JOINED_FROM_STATIC_ENTRANCE_DEBUFF"),
            BlockerDebuffLong = G("staticentranceblockerdebufflong", "HELL_DUNGEON_JOINED_FROM_STATIC_ENTRANCE_DEBUFF_LONG"),
            CityPartySeconds = g is null ? 60 : D(g, "citypartyentrancetimer", 60),
            DownwardPartySeconds = g is null ? 300 : D(g, "downwardpartyentrancetimer", 300),
            ChestExitTile = G("chestexitprefabtilename", "HIGHLAND_DEAD_DNG_HELL_EXIT_CHEST"),
            Modes = modes, MapSets = sets, Pools = pools, Masters = masters, Templates = templates,
            Factions = [.. root.Element("factionweightingtable")?.Elements("faction").Select(f => (S(f, "id"), D(f, "weight", 1))).Where(f => f.Item2 > 0) ?? []],
        };
    }
}

/// <summary>
/// A &lt;helldungeonmode&gt;: its id (file order, op 532 key 2), the worldsettings clustertype (HELL_DUNGEON_DUO_ORANGE), display name, team size,
/// map set, the soul exit's open duration, the item power requirement (900) and the randomdungeons.xml difficulty group of its mobs.
/// </summary>
public sealed record HellDungeonMode(int Id, string Name, string ClusterType, string DisplayName, int MaxTeam, string MapSet, double SoulExitOpenSeconds, double ItemPower)
{
    public string? DifficultyGroup { get; init; }
}

/// <summary>
/// A &lt;map&gt; of a map set: its id, difficulty level (0-2: the floors one below the other), master, template pool, tier, subbiome (dead: the
/// templates/DEAD folder), weight, soft player limit, downward exits open (min-max), and its times in seconds from the instance's start: close
/// (no newcomer after it), shutdown start, shutdown damage start, shutdown (everyone out).
/// </summary>
public sealed record HellDungeonMap(string Id, int Level, string Master, string Pool, int Tier, string SubBiome)
{
    public double Weight { get; init; } = 1;
    public int SoftLimit { get; init; } = 16;
    public int DownwardOpenMin { get; init; }
    public int DownwardOpenMax { get; init; }
    public double CloseSeconds { get; init; } = 300;
    public double ShutdownStartSeconds { get; init; } = 720;
    public double ShutdownDamageSeconds { get; init; } = 900;
    public double ShutdownSeconds { get; init; } = 1020;

    /// <summary>The templates folder of the subbiome (dead → DEAD).</summary>
    public string Folder => SubBiome.ToLowerInvariant() switch { "dead" => "DEAD", "red" => "RED", "green" => "GREEN", _ => "NONE" };
}
