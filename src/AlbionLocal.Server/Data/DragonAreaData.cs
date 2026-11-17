using System.Globalization;
using System.Numerics;
using System.Xml.Linq;

namespace AlbionLocal.Server.Data;

/// <summary>
/// dragonarea.xml and dragonareatemplatemetadata.xml (analysis/work/stageR/dragon-areas/SPEC.md, "SPEC" here, §1, §4): the global settings, the
/// entrance types (the order is the index NewDragonAreaEntrance key 1 carries, bzm.abo(int) in dff's ctor 0x180AF46A0 [infer: file order]), the
/// modes and their map sets, the template pools and the faction weights, and the metadata's master (DRAGON_001: minimap data, layers by tag,
/// slots with their slot tags) and pool templates (size, layers by tag): the input of the instance layout (<see cref="DragonAreaGenerator"/>).
/// Immutable.
/// </summary>
public sealed class DragonAreaData
{
    /// <summary>&lt;globalsettings entranceblockerdebuff&gt; (DRAGON_AREA_JOINED_FROM_ENTRANCE_DEBUFF: cannotuseobject dragonareaentrance 600 s).</summary>
    public string EntranceBlockerDebuff { get; private init; } = "DRAGON_AREA_JOINED_FROM_ENTRANCE_DEBUFF";

    /// <summary>&lt;globalsettings clustershutdownalerttimer&gt; (60 s): the warning before an instance shuts down (bzm.abw → bzm+0x28).</summary>
    public double ShutdownAlertSeconds { get; private init; } = 60;

    /// <summary>&lt;entrancetype&gt;s in file order.</summary>
    public IReadOnlyList<DragonEntranceType> EntranceTypes { get; private init; } = [];

    /// <summary>&lt;mode&gt;s by name.</summary>
    public IReadOnlyDictionary<string, DragonAreaMode> Modes { get; private init; } = new Dictionary<string, DragonAreaMode>();

    /// <summary>&lt;mapset&gt; name → its &lt;map&gt;s.</summary>
    public IReadOnlyDictionary<string, IReadOnlyList<DragonAreaMap>> MapSets { get; private init; } = new Dictionary<string, IReadOnlyList<DragonAreaMap>>();

    /// <summary>&lt;templatepool&gt;s by id.</summary>
    public IReadOnlyDictionary<string, DragonTemplatePool> Pools { get; private init; } = new Dictionary<string, DragonTemplatePool>();

    /// <summary>&lt;factionweightingtable&gt; (KEEPER 1).</summary>
    public IReadOnlyList<(string Faction, double Weight)> Factions { get; private init; } = [];

    /// <summary>dragonareatemplatemetadata.xml masters by name (DRAGON_001).</summary>
    public IReadOnlyDictionary<string, DragonMaster> Masters { get; private init; } = new Dictionary<string, DragonMaster>();

    /// <summary>dragonareatemplatemetadata.xml templates by name (M_DR_*, S_DR_*): size, faction, layers with their tags.</summary>
    public IReadOnlyDictionary<string, MistsTemplateMeta> Templates { get; private init; } = new Dictionary<string, MistsTemplateMeta>();

    /// <summary>The entrance type named <paramref name="name"/> and its index, or null.</summary>
    public (DragonEntranceType Type, int Index)? Entrance(string name)
    {
        for (var i = 0; i < EntranceTypes.Count; i++)
            if (string.Equals(EntranceTypes[i].Name, name, StringComparison.OrdinalIgnoreCase)) return (EntranceTypes[i], i);
        return null;
    }

    public static DragonAreaData Load(XElement root, XElement? metadata = null)
    {
        static double D(XElement e, string a, double fallback) =>
            double.TryParse((string?)e.Attribute(a), NumberStyles.Float, CultureInfo.InvariantCulture, out var v) ? v : fallback;
        static string S(XElement e, string a) => (string?)e.Attribute(a) ?? "";
        static bool B(XElement e, string a) => string.Equals((string?)e.Attribute(a), "true", StringComparison.OrdinalIgnoreCase);
        static string[] Tags(XElement e, string a) => S(e, a).Split(['|', ','], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        static IReadOnlyList<(string, double)> Rolls(XElement? e) => [.. e?.Elements("layertag").Select(l => (S(l, "name"), D(l, "weight", 1))).Where(l => l.Item2 > 0) ?? []];
        var types = (root.Element("entrancetypes")?.Elements("entrancetype") ?? []).Select(t => new DragonEntranceType(
            S(t, "name"), S(t, "tileref"), S(t, "nameloca"), D(t, "locktime", 0), D(t, "lifetime", 0), D(t, "despawntime", 0), S(t, "minimapmarker"),
            [.. t.Elements("mode").Select(m => (string?)m.Attribute("name")).OfType<string>()])).ToList();
        var modes = new Dictionary<string, DragonAreaMode>(StringComparer.Ordinal);
        foreach (var m in root.Element("modes")?.Elements("mode") ?? [])
            if ((string?)m.Attribute("name") is { Length: > 0 } name)
                modes[name] = new DragonAreaMode(name, S(m, "clustertype"), S(m, "clusterdisplayname"),
                    (int)D(m, "minteamsize", 1), (int)D(m, "maxteamsize", 1), S(m, "mapset"), S(m, "bossmob"), D(m, "itempowerrequirement", 0), B(m, "applyleaveprotection"))
                {
                    DifficultyGroup = (string?)m.Attribute("instancerandomdungeondifficultygroup"),
                };
        var sets = new Dictionary<string, IReadOnlyList<DragonAreaMap>>(StringComparer.Ordinal);
        foreach (var s in root.Element("maps")?.Elements("mapset") ?? [])
            if ((string?)s.Attribute("name") is { Length: > 0 } name)
                sets[name] = [.. s.Elements("map").Select(m => new DragonAreaMap(S(m, "id"), S(m, "mastertemplate"), S(m, "templatepool"), (int)D(m, "clustertier", 0),
                    D(m, "clusterclosetime", 0), D(m, "clustershutdownstart", 0), D(m, "clustershutdowntime", 0))
                {
                    Level = (byte)Math.Clamp(D(m, "difficultylevel", 0), 0, 255), Weight = D(m, "weight", 1), RestrictRotation = B(m, "restrictrotation"),
                    RollTags = Rolls(m.Element("mastertemplaterolllayertag")),
                })];
        var pools = new Dictionary<string, DragonTemplatePool>(StringComparer.Ordinal);
        foreach (var p in root.Element("templates")?.Elements("templatepool") ?? [])
        {
            var sizes = new Dictionary<string, IReadOnlyList<DragonCategory>>(StringComparer.OrdinalIgnoreCase);
            foreach (var size in p.Elements("size"))
                sizes[S(size, "name")] = [.. size.Elements("type").Select(t => new DragonCategory(S(t, "category"), D(t, "weight", 1), Rolls(t.Element("rolllayertag")),
                    Tags(t, "requiredslottags"), string.Equals(S(t, "selectionmode"), "prioritypick", StringComparison.OrdinalIgnoreCase),
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
                        (float)D(m, "minimapBaseLevel", 0), DragonAreaLayout.MaterialPreset),
                    Layers(m),
                    [.. m.Elements("slot").Select(s =>
                    {
                        var (x, y, z) = TileMath.V3((string?)s.Attribute("pos"));
                        return new DragonSlot(S(s, "name"), (float)x, (float)y, (float)z, S(s, "size"), Tags(s, "slottags"));
                    })]));
            foreach (var t in metadata.Elements("template"))
                templates.TryAdd(S(t, "name"), new MistsTemplateMeta(S(t, "name"), S(t, "size"), S(t, "faction"), Layers(t)));
        }
        var g = root.Element("globalsettings");
        return new DragonAreaData
        {
            EntranceBlockerDebuff = g is not null && S(g, "entranceblockerdebuff") is { Length: > 0 } debuff ? debuff : "DRAGON_AREA_JOINED_FROM_ENTRANCE_DEBUFF",
            ShutdownAlertSeconds = g is null ? 60 : D(g, "clustershutdownalerttimer", 60),
            EntranceTypes = types, Modes = modes, MapSets = sets, Pools = pools, Masters = masters, Templates = templates,
            Factions = [.. root.Element("factionweightingtable")?.Elements("faction").Select(f => (S(f, "id"), D(f, "weight", 1))).Where(f => f.Item2 > 0) ?? []],
        };
    }
}

/// <summary>An &lt;entrancetype&gt;: the tile NewDragonAreaEntrance key 4 places (tileref), its times in seconds and the modes it offers.</summary>
public sealed record DragonEntranceType(string Name, string Tile, string NameLoca, double LockSeconds, double LifeSeconds, double DespawnSeconds, string MinimapMarker,
    IReadOnlyList<string> Modes);

/// <summary>A &lt;mode&gt;: its cluster type (worldsettings), team sizes, map set, boss mob, item power requirement and the black zone's leave protection.</summary>
public sealed record DragonAreaMode(string Name, string ClusterType, string DisplayName, int MinTeam, int MaxTeam, string MapSet, string BossMob, double ItemPower,
    bool LeaveProtection)
{
    /// <summary>instancerandomdungeondifficultygroup ("dragonarea"): the randomdungeons.xml group the instance's mob level comes from.</summary>
    public string? DifficultyGroup { get; init; }
}

/// <summary>
/// A &lt;map&gt; of a map set: the master template (DRAGON_001), its template pool, tier and lifetime (close, shutdown start, shutdown) in seconds; its
/// difficultylevel (cdr.b → a88.n), weight, restrictrotation (the master stays unturned) and the master's rolled layer tags (mastertemplaterolllayertag).
/// </summary>
public sealed record DragonAreaMap(string Id, string Master, string Pool, int Tier, double CloseSeconds, double ShutdownStartSeconds, double ShutdownSeconds)
{
    public byte Level { get; init; }
    public double Weight { get; init; } = 1;
    public bool RestrictRotation { get; init; }
    public IReadOnlyList<(string Tag, double Weight)> RollTags { get; init; } = [];
}

/// <summary>A &lt;templatepool&gt;: its categories per slot size (medium, small).</summary>
public sealed record DragonTemplatePool(string Id, IReadOnlyDictionary<string, IReadOnlyList<DragonCategory>> Sizes);

/// <summary>
/// A &lt;type category&gt; of a pool size: weight, the rolled layer tag (orangesolo …), the slot tags a slot needs for it (requiredslottags
/// DRAKE_ISLAND), selectionmode prioritypick, and its templates.
/// </summary>
public sealed record DragonCategory(string Name, double Weight, IReadOnlyList<(string Tag, double Weight)> RollTags, string[] RequiredSlotTags, bool PriorityPick,
    IReadOnlyList<DragonPoolTemplate> Templates);

/// <summary>A pool &lt;template&gt;: weight, unique, restrictrotation (placed unturned).</summary>
public sealed record DragonPoolTemplate(string Id, double Weight, bool Unique, bool RestrictRotation);

/// <summary>A metadata &lt;master&gt;: its minimap data, its layers (id, name, tags) and its slots in file order.</summary>
public sealed record DragonMaster(string Name, MiniMap MiniMap, IReadOnlyList<(string Id, string Name, string[] Tags)> Layers, IReadOnlyList<DragonSlot> Slots);

/// <summary>A master &lt;slot&gt;: name (Medium_01, Small_01; not unique), position in the master's frame, size (Medium, Small), slot tags (DRAKE_ISLAND).</summary>
public sealed record DragonSlot(string Name, float X, float Y, float Z, string Size, string[] Tags);
