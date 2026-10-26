using System.Globalization;
using System.Xml.Linq;
using static AlbionLocal.Server.Data.ClusterNameInfo;

namespace AlbionLocal.Server.Data;

/// <summary>The XML attribute rules of the client's readers (class s; the port's a_int, a_bool, a_long) the layout files are read with.</summary>
internal static class XmlAttr
{
    public static string Str(XElement e, string name, string fallback = "") => (string?)e.Attribute(name) ?? fallback;

    public static int Int(XElement e, string name, int fallback) =>
        e.Attribute(name)?.Value is { } v && int.TryParse(v.Trim(), NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out var i) ? i : fallback;

    public static long Long(XElement e, string name, long fallback) =>
        e.Attribute(name)?.Value is { } v ? long.TryParse(v.Trim(), NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out var l) ? l
            : throw new InvalidDataException($"<{e.Name.LocalName}> {name}='{v}' is not an integer") : fallback;

    /// <summary>Missing = <paramref name="fallback"/>, "" = false, else true for true/True/TRUE/yes/1.</summary>
    public static bool Bool(XElement e, string name, bool fallback) =>
        e.Attribute(name)?.Value is not { } v ? fallback : v.Length != 0 && v is "true" or "True" or "TRUE" or "yes" or "1";

    /// <summary>A flag list ("a, b;c") lower-cased; null when absent or empty.</summary>
    public static string[]? Flags(XElement e, string name) =>
        e.Attribute(name)?.Value is { Length: > 0 } v && Split(v, ",; ").Select(f => f.Trim().ToLowerInvariant()).Where(f => f.Length > 0).ToArray() is { Length: > 0 } flags
            ? flags : null;
}

/// <summary>
/// resourcedistpresets.xml (bnl / bnm / bnn / bnq, SPEC §4.5): per cluster key, how many resource prefabs of each node type,
/// resource and tier the placement draws. A preset's filter is a set of strings; the first preset whose filter holds the
/// cluster's whole key wins (bnl.abo 0x1806EB640).
/// </summary>
public sealed class ResourcePresets
{
    /// <summary>Node types by resourcenode@nodetype: low 10, medium 20, high 30 (anything else is low).</summary>
    public static readonly IReadOnlyDictionary<int, string> NodeTypes = new Dictionary<int, string> { [-1] = "invalid", [10] = "low", [20] = "medium", [30] = "high" };

    private readonly List<(HashSet<string> Filter, ResourcePreset Preset)> _presets;

    private ResourcePresets(List<(HashSet<string>, ResourcePreset)> presets) => _presets = presets;

    public int Count => _presets.Count;

    /// <summary>bnl.c8 0x1806EB7D0, bnm.d 0x1806EC310: <paramref name="registry"/> = <see cref="Registry"/>.</summary>
    public static ResourcePresets Load(XElement root, IReadOnlySet<string> registry)
    {
        var list = new List<(HashSet<string>, ResourcePreset)>();
        foreach (var p in root.Elements("preset"))
        {
            var required = new Dictionary<(int, string), int>();
            var entries = new List<PresetEntry>();
            var keys = new HashSet<(int, string, int)>();
            foreach (var child in p.Elements())
            {
                var nt = EnumParse(NodeTypes, child.Name.LocalName, -1);
                if (nt == -1) continue;
                foreach (var r in child.Elements())
                {
                    var res = r.Name.LocalName.ToUpperInvariant();
                    if (!registry.Contains(res)) continue;
                    var tier = XmlAttr.Int(r, "tier", -1);
                    if (tier == -1 || keys.Contains((nt, res, tier))) continue; // "Duplicate DistributionPreset found"
                    var amount = XmlAttr.Str(r, "amount");
                    if (amount.Length == 0 || Amount(amount) is not { } v) continue;
                    if (!XmlAttr.Bool(r, "alwaysplacefillernodes", false) && v == (0, 0)) continue;
                    required[(nt, res)] = required.GetValueOrDefault((nt, res)) + v.A + v.B;
                    keys.Add((nt, res, tier));
                    entries.Add(new PresetEntry(nt, res, tier, v.A, v.B));
                }
            }
            // bnp.Compare 0x1806ED560: node type descending, resource (culture, ignore case) ascending, tier descending.
            entries = [.. entries.OrderByDescending(e => e.NodeType).ThenBy(e => e.Resource, ClientCompare.Instance).ThenByDescending(e => e.Tier)];
            list.Add((Filter(p), new ResourcePreset(XmlAttr.Str(p, "name"), entries, required)));
        }
        return new ResourcePresets(list);
    }

    /// <summary>bnn.c(string, out bnn) 0x1806ECEE0: "x" → (x, 0), "a-b" → (a, b - a) (count = a + Next(b - a)); null = unreadable.</summary>
    internal static (int A, int B)? Amount(string s)
    {
        s = string.Concat(s.Where(ch => !char.IsWhiteSpace(ch)));
        static int? I(string t) => int.TryParse(t, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out var i) ? i : null;
        if (!s.Contains('-')) return I(s) is { } x ? (x, 0) : null;
        var parts = s.Split('-');
        if (I(parts[0]) is not { } a || I(parts[1]) is not { } b) return null;
        return a <= b ? (a, b - a) : null;
    }

    /// <summary>bnq.b(XmlElement) 0x1806ED900, with the defaults of a missing attribute; a tier outside 1..8 never matches.</summary>
    private static HashSet<string> Filter(XElement e)
    {
        var f = new HashSet<string>(StringComparer.Ordinal);
        var tier = XmlAttr.Int(e, "tier", -1);
        if (tier is < 1 or > 8) return f;
        f.Add(tier.ToString(CultureInfo.InvariantCulture));
        f.UnionWith(SplitList(XmlAttr.Str(e, "biome")));
        f.UnionWith(Split(XmlAttr.Str(e, "type", "World"), ", "));
        f.UnionWith(SplitList(XmlAttr.Str(e, "pvprules", "None,PvpDisabled,FactionPvpOnly,PvpForced,PvpAllowed")));
        f.UnionWith(SplitList(XmlAttr.Str(e, "continent", "None,Royal,Outlands")));
        f.UnionWith(SplitList(XmlAttr.Str(e, "clusterquality", "None,Q6,Q5,Q4,Q3,Q2,Q1")));
        f.UnionWith(SplitList(XmlAttr.Str(e, "difficulty", "D0,D1,D2,D3,D4")));
        return f;
    }

    /// <summary>bnj(CNI, PvpRules, int) 0x1806EAC80 → bnq.b(bnj) 0x1806EE270: the key strings of a cluster.</summary>
    public static HashSet<string> Key(ClusterNameInfo c, int pvpRules, int difficulty = 0) => new(StringComparer.Ordinal)
    {
        BiomeNames.GetValueOrDefault(c.Biome, c.Biome.ToString(CultureInfo.InvariantCulture)),
        QualityNames.GetValueOrDefault(c.Quality, c.Quality.ToString(CultureInfo.InvariantCulture)),
        Types.TryGetValue(c.Type, out var t) ? t[0] : c.Type == -1 ? "Invalid" : c.Type.ToString(CultureInfo.InvariantCulture),
        ContinentNames.GetValueOrDefault(c.Continent, c.Continent.ToString(CultureInfo.InvariantCulture)),
        PvpRuleNames.GetValueOrDefault(pvpRules, pvpRules.ToString(CultureInfo.InvariantCulture)),
        c.Tier.ToString(CultureInfo.InvariantCulture),
        "D" + difficulty.ToString(CultureInfo.InvariantCulture),
    };

    /// <summary>The first preset whose filter holds every string of <paramref name="key"/>; null = none (no resource nodes).</summary>
    public ResourcePreset? Find(IReadOnlySet<string> key) => _presets.FirstOrDefault(p => key.All(p.Filter.Contains)).Preset;

    /// <summary>
    /// bng.abp 0x1806E7DC0: the resource names presets and mappings may use: the harvestables.xml names without
    /// SILVERCOINS and ESSENCE, plus MOBCAMP and ROAMINGMOB (mob spawn prefabs, stage 5).
    /// </summary>
    public static IReadOnlySet<string> Registry(HarvestableStore harvestables)
    {
        var names = new HashSet<string>(StringComparer.Ordinal) { "MOBCAMP", "ROAMINGMOB" };
        foreach (var h in harvestables.All)
            if (!h.Name.Contains("SILVERCOINS") && !h.Name.Contains("ESSENCE")) names.Add(h.Name);
        return names;
    }
}

/// <summary>One preset (bnm): <see cref="Entries"/> in the placement's order; <see cref="Required"/> = nodes needed per (node type, resource).</summary>
public sealed record ResourcePreset(string Name, IReadOnlyList<PresetEntry> Entries, IReadOnlyDictionary<(int NodeType, string Resource), int> Required);

/// <summary>One preset entry: <see cref="A"/> + Next(<see cref="B"/>) prefabs of <see cref="Resource"/> T<see cref="Tier"/> on the nodes of <see cref="NodeType"/>.</summary>
public sealed record PresetEntry(int NodeType, string Resource, int Tier, int A, int B);

/// <summary>A prefab of a mapping list (bnv): weight × 1000 (bnv.c), its condition (bnw) and where it may not go.</summary>
public sealed record ResourcePrefab(string Name, long Weight)
{
    /// <summary>bnw: continent, faction (0 = any), flags and activeDuringEvent; null = always active.</summary>
    public (int Continent, int Faction, string[]? Flags, bool ActiveDuringEvent)? Condition { get; init; }

    public bool AllowedInTerritory { get; init; } = true;
    public bool AllowedInFactionFortress { get; init; } = true;

    /// <summary>bnw.e 0x1806FF110 with the cluster's continent and faction and Login key 79.</summary>
    public bool Active(int continent, int faction, IReadOnlySet<string>? flags)
    {
        if (Condition is not { } w) return true;
        if ((w.Continent != 0 && w.Continent != continent) || (w.Faction != 0 && w.Faction != faction)) return false;
        if (w.Flags is null) return true;
        var hit = flags is not null && w.Flags.Any(f => f.Length > 0 && flags.Contains(f));
        return hit == w.ActiveDuringEvent;
    }
}

/// <summary>
/// resourceprefabmappings.xml (bnr / bns / bnk / bnu, SPEC §4.5): per (cluster type, biome, subbiome, tier) a mapping, the
/// first definition winning; in it per (resource, tier) the prefab list and the filler list, each sorted by name with
/// <see cref="ClientCompare"/>.
/// </summary>
public sealed class ResourcePrefabMappings
{
    private readonly Dictionary<(int Type, int Biome, int SubBiome, int Tier), (string Name, IReadOnlyDictionary<(string, int), PrefabLists> Lists)> _mappings;

    private ResourcePrefabMappings(Dictionary<(int, int, int, int), (string, IReadOnlyDictionary<(string, int), PrefabLists>)> mappings) => _mappings = mappings;

    /// <summary>Every prefab name of every list (the tiles the resolver must know the footprint of).</summary>
    public IEnumerable<string> PrefabNames => _mappings.Values.SelectMany(m => m.Lists.Values).SelectMany(l => l.Prefabs.Concat(l.Fillers)).Select(p => p.Name).Distinct();

    /// <summary>bnk(CNI) 0x1806EB020: the mapping of the cluster (its name and lists); null = none.</summary>
    public (string Name, IReadOnlyDictionary<(string Resource, int Tier), PrefabLists> Lists)? Find(ClusterNameInfo c) =>
        _mappings.TryGetValue((c.Type, c.Biome, c.SubBiome, c.Tier), out var m) ? m : null;

    /// <summary>bnr.c8 0x1806EE7E0, bns.c 0x1806EEF70.</summary>
    public static ResourcePrefabMappings Load(XElement root, IReadOnlySet<string> registry)
    {
        var subBiomes = new Dictionary<int, string> { [-1] = "invalid", [0] = "green", [1] = "red", [2] = "dead" };
        var mappings = new Dictionary<(int, int, int, int), (string, IReadOnlyDictionary<(string, int), PrefabLists>)>();
        foreach (var m in root.Elements("mapping"))
        {
            var tier = XmlAttr.Int(m, "tier", -1);
            if (tier is < 1 or > 8) continue;
            var sub = EnumParse(subBiomes, (string?)m.Attribute("subbiome"), -1);
            var keys = new List<(int, int, int, int)>();
            foreach (var b in SplitList(XmlAttr.Str(m, "biome")))
            {
                var bi = Lookup(Biomes, b);
                if (bi == -1) continue;
                foreach (var ct in Split(XmlAttr.Str(m, "clustertype"), ", ")) keys.Add((Lookup(Types, ct), bi, sub, tier));
            }
            Dictionary<(string, int), PrefabLists>? lists = null;
            foreach (var key in keys)
            {
                if (mappings.ContainsKey(key)) continue; // the first definition wins
                lists ??= Lists(m, registry);
                mappings[key] = (XmlAttr.Str(m, "name"), lists);
            }
        }
        return new ResourcePrefabMappings(mappings);
    }

    private static Dictionary<(string, int), PrefabLists> Lists(XElement m, IReadOnlySet<string> registry)
    {
        var lists = new Dictionary<(string, int), PrefabLists>();
        foreach (var r in m.Elements())
        {
            var res = r.Name.LocalName.ToUpperInvariant();
            if (!registry.Contains(res)) continue;
            var tier = XmlAttr.Int(r, "tier", -1);
            if (tier == -1 || lists.ContainsKey((res, tier))) continue;
            lists[(res, tier)] = new PrefabLists(List(r, "prefabs", false), List(r, "fillerprefabs", true));
        }
        return lists;
    }

    /// <summary>bnu.c 0x1806EFC40: the prefabs of every &lt;<paramref name="container"/>&gt;, once per name, sorted (0x1806F0410).</summary>
    private static List<ResourcePrefab> List(XElement r, string container, bool allowEmpty)
    {
        var faction = new Dictionary<int, string>(FactionNames);
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var list = new List<ResourcePrefab>();
        foreach (var pe in r.Elements(container).Elements("prefab"))
        {
            var name = XmlAttr.Str(pe, "name");
            if (name.Length == 0 && !allowEmpty) continue;
            var f = EnumParse(faction, (string?)pe.Attribute("faction"), 0);
            var continent = EnumParse(ContinentNames, (string?)pe.Attribute("continent"), 0);
            var flags = XmlAttr.Flags(pe, "flags");
            if (!seen.Add(name)) continue;
            list.Add(new ResourcePrefab(name, Math.Abs(XmlAttr.Long(pe, "weight", 1)) * 1000)
            {
                Condition = continent != 0 || flags is not null || f != 0 ? (continent, f, flags, XmlAttr.Bool(pe, "activeDuringEvent", true)) : null,
                AllowedInFactionFortress = XmlAttr.Bool(pe, "allowedinfactionfortress", true),
                AllowedInTerritory = XmlAttr.Bool(pe, "allowedinterritory", true),
            });
        }
        return [.. list.OrderBy(p => p.Name, ClientCompare.Instance)];
    }
}

/// <summary>The two lists of a (resource, tier): the prefabs pass 1 places, the fillers pass 2 puts on the nodes left (an empty name places nothing).</summary>
public sealed record PrefabLists(IReadOnlyList<ResourcePrefab> Prefabs, IReadOnlyList<ResourcePrefab> Fillers);

/// <summary>
/// worldsettings.xml: world.xml cluster type → clustertype@archetype → archetype, whose pvpwarningtype (bgu+0x30, read by
/// bgw.cy 0x180692DE0; default safe) keys the dynamic-template pool and whose pvprule (bgu+0x18, bgu.av 0x18068B260;
/// default PvpAllowed, the verifier's correction) keys the preset.
/// </summary>
public sealed class WorldArchetypes
{
    private static readonly Dictionary<int, string> Warnings = new() { [0] = "safe", [1] = "yellow", [2] = "orange", [3] = "red", [4] = "black" };

    private readonly Dictionary<string, string> _archetypeOf;
    private readonly Dictionary<string, XElement> _archetypes;

    private WorldArchetypes(Dictionary<string, string> archetypeOf, Dictionary<string, XElement> archetypes)
    {
        _archetypeOf = archetypeOf;
        _archetypes = archetypes;
    }

    public static WorldArchetypes Load(XElement root)
    {
        var of = new Dictionary<string, string>(StringComparer.Ordinal);
        var archetypes = new Dictionary<string, XElement>(StringComparer.Ordinal);
        var poi = new Dictionary<string, int>(StringComparer.Ordinal);
        var spawnBuffs = new Dictionary<string, IReadOnlyList<SpawnBuff>>(StringComparer.Ordinal);
        var tunnels = new Dictionary<string, string>(StringComparer.Ordinal);
        var entry = new Dictionary<string, (double, bool)>(StringComparer.Ordinal);
        foreach (var e in root.Descendants("clustertype"))
        {
            if ((string?)e.Attribute("name") is not { } n) continue;
            if ((string?)e.Attribute("archetype") is { } a) of.TryAdd(n, a);
            if ((string?)e.Attribute("tunnelpreset") is { Length: > 0 } tunnel) tunnels.TryAdd(n, tunnel); // stageL roads SPEC §1.3
            // stageP SPEC §2.4: the spell cooldowns on entering and the short spawn buff of the cluster one comes from
            entry.TryAdd(n, (double.TryParse((string?)e.Attribute("spellcooldownsonenteringseconds"), System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var cd) ? cd : 0,
                string.Equals((string?)e.Attribute("hasshortspawnbuffonleave"), "true", StringComparison.OrdinalIgnoreCase)));
            // stageH bosses SPEC §4.2: <lootchestspawns><poilootchestspawnlimit upgradelevelindex="1" op="greaterOrEqual" limit="1"/>
            if (e.Element("lootchestspawns")?.Elements("poilootchestspawnlimit").FirstOrDefault() is { } limit
                && int.TryParse((string?)limit.Attribute("limit"), out var l)) poi.TryAdd(n, l);
            // stageL hellgates phase B: <spawnbuff><buff name="HELL_2V2_ENTER" isclusterchange="Must"/>...</spawnbuff>
            if (e.Element("spawnbuff") is { } buffs)
                spawnBuffs.TryAdd(n, [.. buffs.Elements("buff").Where(b => (string?)b.Attribute("name") is { Length: > 0 }).Select(b => new SpawnBuff((string)b.Attribute("name")!,
                    b.Attributes().Where(a => a.Name.LocalName != "name").ToDictionary(a => a.Name.LocalName, a => a.Value, StringComparer.Ordinal)))]);
        }
        foreach (var e in root.Descendants("archetype"))
            if ((string?)e.Attribute("name") is { } n) archetypes.TryAdd(n, new XElement(e.Name, e.Attributes()));
        return new WorldArchetypes(of, archetypes) { _poiLimits = poi, _spawnBuffs = spawnBuffs, _tunnels = tunnels, _entry = entry };
    }

    private Dictionary<string, string> _tunnels = [];

    private Dictionary<string, (double Cooldown, bool ShortOnLeave)> _entry = [];

    /// <summary>
    /// The clustertype's spellcooldownsonenteringseconds of world.xml cluster type <paramref name="clusterType"/> (stageP SPEC §2.4: 15 on the
    /// yellow, red and black open world, their dungeons and tunnels; 0 elsewhere): every spell slot's cooldown on arriving there.
    /// </summary>
    public double EnterCooldownOf(string clusterType) => _entry.GetValueOrDefault(clusterType).Cooldown;

    /// <summary>
    /// The clustertype's hasshortspawnbuffonleave (true on the dungeons, expeditions and tunnels): one coming from such a cluster has the
    /// spawnbuff condition fromshortspawnbuffcluster (SPAWN_INVULNERABILITY_SHORT instead of SPAWN_INVULNERABILITY, stageP SPEC §2.4).
    /// </summary>
    public bool ShortSpawnBuffOnLeave(string clusterType) => _entry.GetValueOrDefault(clusterType).ShortOnLeave;

    /// <summary>
    /// The tunnelpreset of world.xml cluster type <paramref name="clusterType"/> (stageL roads SPEC §1.3: SAFEAREA → SAFEZONE, OPENPVP_BLACK_1 →
    /// OPENPVP_BLACK_LOW, TUNNEL_ROYAL → TUNNEL_ROYAL, …): the cluster's place in the road network; null = none (cities, start areas, dungeons).
    /// </summary>
    public string? TunnelPresetOf(string clusterType) => _tunnels.GetValueOrDefault(clusterType);

    private Dictionary<string, int> _poiLimits = [];

    private Dictionary<string, IReadOnlyList<SpawnBuff>> _spawnBuffs = [];

    /// <summary>The &lt;spawnbuff&gt; buffs of world.xml cluster type <paramref name="clusterType"/> in document order (stageL hellgates phase B); empty without.</summary>
    public IReadOnlyList<SpawnBuff> SpawnBuffsOf(string clusterType) => _spawnBuffs.GetValueOrDefault(clusterType) ?? [];

    /// <summary>
    /// The poilootchestspawnlimit of world.xml cluster type <paramref name="clusterType"/> (stageH bosses SPEC §4.2: how many POI
    /// chests stand at once in the cluster; SAFEAREA, YELLOW, RED and BLACK 1, the dungeon types 5); null = no limit.
    /// </summary>
    public int? PoiChestLimitOf(string clusterType) => _poiLimits.TryGetValue(clusterType, out var l) ? l : null;

    /// <summary>The archetype of world.xml cluster type <paramref name="clusterType"/>, its PvpWarningLevel and PvpRules.</summary>
    public (string? Archetype, int PvpWarning, int PvpRules) Of(string clusterType)
    {
        var name = _archetypeOf.GetValueOrDefault(clusterType);
        var e = name is null ? null : _archetypes.GetValueOrDefault(name);
        return (name, EnumParse(Warnings, (string?)e?.Attribute("pvpwarningtype"), 0), EnumParse(PvpRuleNames, (string?)e?.Attribute("pvprule"), 2));
    }

    /// <summary>The PvP fields of world.xml cluster type <paramref name="clusterType"/>'s archetype (stageE pvp SPEC §1.1; the client's defaults without one).</summary>
    public ClusterPvp PvpOf(string clusterType) =>
        _archetypeOf.GetValueOrDefault(clusterType) is { } name ? ClusterPvp.Of(_archetypes.GetValueOrDefault(name), name) : ClusterPvp.Default;

    /// <summary>
    /// The clusterdangerbonustype of world.xml cluster type <paramref name="clusterType"/>'s archetype (SAFEAREA → GREEN →
    /// "safe", stage 5 SPEC §5c.2): the gamedata.xml ClusterDangerBonus of its kill rewards; null when it has none (cities).
    /// </summary>
    public string? DangerTypeOf(string clusterType) =>
        _archetypeOf.GetValueOrDefault(clusterType) is { } name && _archetypes.GetValueOrDefault(name) is { } e
            && (string?)e.Attribute("clusterdangerbonustype") is { Length: > 0 } type ? type : null;
}

/// <summary>
/// One &lt;buff&gt; of a worldsettings.xml cluster type's &lt;spawnbuff&gt; (stageL hellgates phase B): the spell and its conditions (ispvppossible,
/// isclusterchange, fromhideout, isrevive, … → Must, MustNot or DoesNotMatter). <see cref="Applies"/> decides with the caller's facts.
/// </summary>
public sealed record SpawnBuff(string Name, IReadOnlyDictionary<string, string> Conditions)
{
    /// <summary>
    /// Whether the buff is given when <paramref name="holds"/> says which conditions are true: each Must one true, each MustNot one false
    /// (DoesNotMatter and unknown values ignored).
    /// </summary>
    public bool Applies(Func<string, bool> holds) => Conditions.All(c => c.Value switch
    {
        "Must" => holds(c.Key),
        "MustNot" => !holds(c.Key),
        _ => true,
    });
}

/// <summary>
/// rareresourcedistribution.xml (SPEC §5.4): per distribution name (world.xml cluster@rareresourcedistribution) and tier
/// the weights of the rare (enchantment) states 0-3; a tier without its own entry takes the tier-less one. The server
/// rolls each resource node's state from it, every <see cref="CheckInterval"/>.
/// </summary>
public sealed class RareResourceDistributions
{
    private readonly Dictionary<string, Dictionary<int, (byte State, int Weight)[]>> _byName;

    private RareResourceDistributions(Dictionary<string, Dictionary<int, (byte, int)[]>> byName, TimeSpan interval)
    {
        _byName = byName;
        CheckInterval = interval;
    }

    /// <summary>checkintervall (3600 s): how often a node's rare state is rolled again.</summary>
    public TimeSpan CheckInterval { get; }

    public static RareResourceDistributions Load(XElement root)
    {
        var byName = new Dictionary<string, Dictionary<int, (byte, int)[]>>(StringComparer.OrdinalIgnoreCase);
        foreach (var d in root.Elements("RareResourceDistribution"))
        {
            var tiers = new Dictionary<int, (byte, int)[]>();
            foreach (var t in d.Elements("Tier"))
                tiers.TryAdd(XmlAttr.Int(t, "value", 0), [.. t.Elements("RareState").Select(s => ((byte)XmlAttr.Int(s, "state", 0), XmlAttr.Int(s, "weight", 0)))]);
            byName.TryAdd(XmlAttr.Str(d, "name"), tiers);
        }
        var seconds = XmlAttr.Int(root, "checkintervall", 3600);
        return new RareResourceDistributions(byName, TimeSpan.FromSeconds(Math.Max(60, seconds)));
    }

    /// <summary>The state weights of <paramref name="distribution"/> for <paramref name="tier"/> (its own entry, else the tier-less one; DEFAULT for an unknown name); empty = none.</summary>
    public IReadOnlyList<(byte State, int Weight)> Weights(string? distribution, int tier)
    {
        if (!_byName.TryGetValue(distribution ?? "DEFAULT", out var tiers) && !_byName.TryGetValue("DEFAULT", out tiers)) return [];
        return tiers.TryGetValue(tier, out var w) ? w : tiers.TryGetValue(0, out var any) ? any : [];
    }
}
