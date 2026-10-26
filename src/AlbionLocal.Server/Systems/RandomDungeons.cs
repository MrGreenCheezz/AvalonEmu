using System.Globalization;
using System.Numerics;
using System.Xml.Linq;
using AlbionLocal.Photon;
using AlbionLocal.Server.Content;
using AlbionLocal.Server.Data;
using AlbionLocal.Server.Entities;
using AlbionLocal.Server.Game;
using AlbionLocal.Server.Protocol;

namespace AlbionLocal.Server.Systems;

/// <summary>
/// One random dungeon (stageG dungeons SPEC §6.1, "SPEC" in this file): the exit it was entered through and the entrance it
/// leads back to, the generator's draw, the floors (ids allocated up front, layouts made lazily, off the world thread), and
/// its lifetime. Solo dungeons are one per party or player behind the exit (<see cref="Key"/>), the others one per exit.
/// </summary>
public sealed class DungeonGroup
{
    public required RandomDungeonExit Exit { get; init; }

    /// <summary>The party id or character Guid of a per-party dungeon; "" = everyone's behind the exit.</summary>
    public required string Key { get; init; }

    /// <summary>The open-world cluster of the entrance and where a player leaving arrives there (the exit's spawnpos, SPEC §6.4).</summary>
    public required string Entrance { get; init; }
    public required Arrival Out { get; init; }

    public required DungeonGenerator.Plan Plan { get; init; }
    public required int Tier { get; init; }
    public required byte Level { get; init; }
    public required string ClusterType { get; init; }
    public required bool Dragon { get; init; }
    public required int Seed { get; init; }

    /// <summary>The instance id of each floor ("@RANDOMDUNGEON@&lt;guid&gt;", one new Guid per floor, SPEC §4).</summary>
    public required IReadOnlyList<string> Ids { get; init; }

    /// <summary>The floors made so far (null = not yet), and the builds under way.</summary>
    public DungeonFloor?[] Floors { get; init; } = [];
    internal Task<DungeonFloor>?[] Builds { get; init; } = [];

    public required TimeSpan Created { get; init; }

    /// <summary><see cref="World.Clock"/> time the last player left its floors; null while someone is inside.</summary>
    public TimeSpan? EmptySince { get; set; }

    /// <summary>
    /// The characters sent into a floor (the commit, <see cref="RandomDungeons.Sent"/>) and not joined yet, with the
    /// <see cref="World.Clock"/> time until which each counts as inside: between the commit and the Join the player is on no
    /// map while its client builds the floor scene.
    /// </summary>
    public Dictionary<Guid, TimeSpan> Arriving { get; } = [];

    public bool Closed { get; set; }

    /// <summary>
    /// StageH PvE (Dungeons:GroupScaling): the players it is scaled for, the entering player's party members online when it was made (1
    /// out of a party), raised to <see cref="Entered"/>'s count as more come in; a floor takes the number of its fill.
    /// </summary>
    public int Players { get; set; } = 1;

    /// <summary>The characters sent into it so far (<see cref="RandomDungeons.Sent"/>).</summary>
    public HashSet<Guid> Entered { get; } = [];

    /// <summary>StageH PvE (Dungeons:DailyBonus): its factor on fame, silver and loot, the bonus when its faction was the dungeon of the day at its making; else 1.</summary>
    public double Bonus { get; init; } = 1;

    /// <summary>
    /// The characters that entered it from the open world and wait for the dungeon of the day's chat line (<see cref="RandomDungeons.Entering"/>):
    /// it goes out at their Join on its floor (<see cref="RandomDungeons.Arrived"/>), not on the open-world map before the loading screen.
    /// </summary>
    public HashSet<Guid> DailyLine { get; } = [];

    public int FloorCount => Ids.Count;

    /// <summary>a80.b of floor <paramref name="index"/> (0-based), known before it is made.</summary>
    public string NameOf(int index) => DungeonLayout.ClusterName(Ids[index], Dragon, Tier, Plan.Generator.Faction);

    /// <summary>The links of floor <paramref name="index"/> (SPEC §2.7, §3 rows 15-22): up to the entrance or the floor above, down to the floor below or out.</summary>
    public DungeonGenerator.Links LinksOf(int index)
    {
        var up = index == 0 ? (Entrance, "", Guid.Empty) : (Ids[index - 1], NameOf(index - 1), DungeonExits.Down);
        var down = index + 1 < FloorCount ? (Ids[index + 1], NameOf(index + 1), DungeonExits.Up) : (Entrance, "", Guid.Empty);
        return new DungeonGenerator.Links(up.Item1, up.Item2, up.Item3, down.Item1, down.Item2, down.Item3, Entrance, Exit.Position);
    }
}

/// <summary>A made floor: its layout, the bytes the client gets (ChangeCluster key 3, SelectCharacter key 4) and the cluster resolved from it.</summary>
public sealed record DungeonFloor(DungeonGroup Group, int Index, DungeonLayout Layout, byte[] Bytes, ClusterTemplate Template)
{
    public string Id => Layout.Id;

    /// <summary>Where a player coming in through the floor's exit <paramref name="door"/> arrives: its spawnpos, facing away from it (SPEC §6.4).</summary>
    public Arrival? Through(Guid door, float collisionRadius) =>
        Transfers.Through(new WorldExit(Guid.Empty, Vector2.Zero, door, Id, "", new Dictionary<string, string>()), Template, collisionRadius);
}

/// <summary>
/// Random dungeons (analysis/work/stageG/dungeons/SPEC.md): the exits on the open-world markers with their rerolls, despawns
/// and refills (§8, §9), the dungeons behind them with their floors (§5, §6), the instance maps keyed by the floor id, and
/// the closing (§9). The handlers (ClusterHandler, CharacterHandler, the login's SelectCharacter) ask it for the floors;
/// <see cref="World.Update"/> runs <see cref="Update"/>. Under <see cref="World.Sync"/>, except the floor builds, which run on
/// a pool thread and come back through <see cref="World.Post"/>.
/// </summary>
public sealed class RandomDungeons(World world)
{
    /// <summary>The markers of one open-world map and the exit each holds (null = free from <see cref="Slot.FreeAt"/>).</summary>
    private sealed class Field(Map map, ClusterTemplate template, RandomDungeonData data)
    {
        public Map Map { get; } = map;
        public ClusterTemplate Template { get; } = template;
        public RandomDungeonData Data { get; } = data;
        public List<Slot> Slots { get; } = [];
    }

    private sealed class Slot(DungeonExitMarker marker)
    {
        public DungeonExitMarker Marker { get; } = marker;
        public RandomDungeonExit? Exit { get; set; }
        public TimeSpan FreeAt { get; set; }
    }

    private readonly Dictionary<Map, Field> _fields = [];
    private readonly Dictionary<RandomDungeonExit, Slot> _slots = [];
    private readonly List<RandomDungeonExit> _gmExits = [];
    private readonly List<DungeonGroup> _groups = [];
    private readonly Dictionary<string, (DungeonGroup Group, int Index)> _ids = new(StringComparer.Ordinal);
    private TimeSpan _next;

    private DungeonSettings Settings => world.DungeonSettings;

    /// <summary>
    /// The random numbers of the exits and the dungeon seeds: their own, so that the maps' (<see cref="Map.Random"/>: mobs, nodes)
    /// draw what they drew before the dungeons; seeded with <see cref="World.MapSeed"/> when that is set (the self-test).
    /// </summary>
    private Random Rng => _rng ??= world.MapSeed is { } seed ? new Random(seed) : new Random();

    private Random? _rng;

    /// <summary>The live dungeons.</summary>
    public IReadOnlyList<DungeonGroup> Groups => _groups;

    /// <summary>Whether <paramref name="cluster"/> is a floor id of a live dungeon (made or not yet).</summary>
    public bool IsLive(string cluster) => _ids.ContainsKey(cluster);

    /// <summary>The made floor of instance id <paramref name="cluster"/>; null when it is not made yet or its dungeon is closed (or no dungeon id).</summary>
    public DungeonFloor? Floor(string cluster) => _ids.TryGetValue(cluster, out var g) ? g.Group.Floors[g.Index] : null;

    /// <summary>The dungeon and floor index of an instance id.</summary>
    public (DungeonGroup Group, int Index)? Find(string cluster) => _ids.TryGetValue(cluster, out var g) ? g : null;

    /// <summary>The exits on the markers of <paramref name="map"/>, for the GM and the self-test.</summary>
    public IEnumerable<RandomDungeonExit> ExitsOf(Map map) => map.Objects.Values.OfType<RandomDungeonExit>();

    /// <summary>
    /// At the map's fill (MapSpawner, before <see cref="Map.Populate"/>): the markers of an open-world cluster are taken over, and
    /// with Dungeons:FillAtStart each subtype gets its floor(markers × maxfillratio) exits on random markers at once (SPEC §8.3).
    /// Nothing without markers, without randomdungeons.xml read, or with Dungeons:Enabled off.
    /// </summary>
    internal IEnumerable<RandomDungeonExit> Populate(Map map, ClusterTemplate template)
    {
        if (!Settings.Enabled || template.DungeonMarkers.Count == 0 || DungeonLayout.IsInstance(map.Cluster) || ExpeditionData.IsInstance(map.Cluster) || CorruptedData.IsInstance(map.Cluster) || HellgateData.IsInstance(map.Cluster)
            || MistsIds.IsInstance(map.Cluster) || world.Clusters?.DungeonsIfLoaded is not { } data) return [];
        var field = new Field(map, template, data);
        // StageL roads (SPEC §7.1): Roads:FixedDungeons opens the fixed exits of a road map's camps alone, whatever Dungeons:FixedExits says.
        var fixedExits = Settings.FixedExits || world.Config.Roads.FixedDungeons && template.Info.Type.StartsWith("TUNNEL_", StringComparison.Ordinal);
        foreach (var m in template.DungeonMarkers)
            if (m.Type != "FIXED" || fixedExits) field.Slots.Add(new Slot(m));
        _fields[map] = field;
        if (!Settings.FillAtStart) return [];
        var made = new List<RandomDungeonExit>();
        foreach (var group in field.Slots.GroupBy(GroupOf))
            for (var free = Cap(field, group.Key, group.Count()) - group.Count(s => s.Exit is not null); free > 0; free--)
                if (SpawnOn(field, group, world.Clock.Now) is { } exit) made.Add(exit);
        return made;
    }

    /// <summary>The slot's fill group: its marker subtype, or "FIXED:&lt;name&gt;" for fixed exits (each FixedExit has its own maxfillratio).</summary>
    private static string GroupOf(Slot s) => s.Marker.Type == "FIXED" ? "FIXED:" + s.Marker.FixedExit : s.Marker.Type;

    /// <summary>How many of <paramref name="markers"/> markers of a group may hold an exit: floor(markers × maxfillratio), SPEC §8.3.</summary>
    private static int Cap(Field field, string group, int markers)
    {
        double ratio;
        if (group.StartsWith("FIXED:", StringComparison.Ordinal))
            ratio = field.Data.Exits.GetValueOrDefault(group[6..]) is { Fixed: true } fixedExit ? fixedExit.MaxFillRatio : 0;
        else ratio = Distribution(field, group)?.MaxFillRatio ?? 0;
        return (int)Math.Floor(markers * ratio + 1e-9);
    }

    /// <summary>
    /// RandomExitDistribution of the cluster's continent and tier for the subtype (Continent and tier from the .cluster name), the entry of its
    /// clustertype's playersarelethal where the tier is split by it (stageL mists SPEC §2.6, V5: Royal SOLO T6).
    /// </summary>
    private static ExitDistribution? Distribution(Field field, string subtype)
    {
        var cni = ClusterNameInfo.Parse(field.Template.Info.File);
        if (cni is null) return null;
        var continent = ClusterNameInfo.ContinentNames.GetValueOrDefault(cni.Continent, "None");
        return field.Data.DistributionFor(continent, subtype, cni.Tier, field.Template.Pvp.PlayersAreLethal);
    }

    /// <summary>
    /// The exits a free <paramref name="subtype"/> marker of <paramref name="map"/> draws from now, with their weights (a mists portal's × Mists:OpenWorldWeight):
    /// the distribution's usable ones (<see cref="SpawnOn"/>; the GM's and the self-test's look at it). Empty off the open world.
    /// </summary>
    public IReadOnlyList<(string Exit, double Weight)> ChoicesOn(Map map, string subtype) =>
        _fields.TryGetValue(map, out var field) ? Choices(field, subtype).Select(c => (c.Exit, Weight(field.Data, c))).ToList() : [];

    private List<ExitChoice> Choices(Field field, string subtype) =>
        Distribution(field, subtype)?.Exits.Where(c => c.Weight > 0 && Usable(field.Data, c.Exit, CorruptedPortals, MistsPortals) && Weight(field.Data, c) > 0).ToList() ?? [];

    /// <summary>A distribution entry's weight, a mists portal's × Mists:OpenWorldWeight.</summary>
    private double Weight(RandomDungeonData data, ExitChoice c) => data.Exits.GetValueOrDefault(c.Exit)?.MistsMode is not null ? c.Weight * world.Mists.WeightFactor : c.Weight;

    /// <summary>A new exit on a random free marker of <paramref name="group"/>; null when none is free or the data gives no usable exit.</summary>
    private RandomDungeonExit? SpawnOn(Field field, IEnumerable<Slot> group, TimeSpan now)
    {
        var free = group.Where(s => s.Exit is null && s.FreeAt <= now).ToList();
        if (free.Count == 0) return null;
        var slot = free[Rng.Next(free.Count)];
        RandomExitInfo? info;
        string? reroll = null;
        if (slot.Marker.Type == "FIXED")
        {
            info = field.Data.Exits.GetValueOrDefault(slot.Marker.FixedExit ?? "");
            reroll = info?.Reroll; // stageL roads: a road dungeon's ROADS_FIXED_DUNGEONS (levels 2-4, drawn once)
        }
        else
        {
            // SPEC §8.3: weighted over the distribution's exits, without weight 0 and without hellgate entries (corrupted and mists portals as switched).
            var choices = Choices(field, slot.Marker.Type);
            if (choices.Count == 0) return null;
            var choice = DungeonGenerator.Pick(Rng, [.. choices.Select(c => (c, Weight(field.Data, c)))]);
            info = field.Data.Exits[choice.Exit];
            reroll = choice.Reroll;
        }
        if (info is null || !Usable(field.Data, info.Name, CorruptedPortals, MistsPortals)) return null;
        var exit = Make(field.Map, field.Template, field.Data, slot.Marker, info, reroll, now, RotationOn(slot.Marker), SubtypeOn(slot.Marker, info));
        if (exit is null) return null;
        slot.Exit = exit;
        _slots[exit] = slot;
        return exit;
    }

    /// <summary>
    /// The exit <paramref name="name"/> on <paramref name="marker"/> of the map's cluster now (the marker rule for key 2, the
    /// exit's reroll from the distribution), in place of what the marker holds: the self-test's fixed placement and a GM's choice.
    /// </summary>
    public RandomDungeonExit SpawnOnMarker(Map map, DungeonExitMarker marker, string name)
    {
        if (!_fields.TryGetValue(map, out var field) || field.Slots.FirstOrDefault(s => s.Marker == marker) is not { } slot)
            throw new InvalidOperationException($"map {map.Cluster} has no such exit marker");
        if (!field.Data.Exits.TryGetValue(name, out var info) || !Usable(field.Data, name, world.Config.Corrupted.Enabled, MistsPortals)) throw new InvalidOperationException($"{name}: no usable exit");
        if (slot.Exit is { } old) Despawn(old, world.Clock.Now, "replaced");
        var reroll = Distribution(field, marker.Type)?.Exits.FirstOrDefault(c => c.Exit == name)?.Reroll ?? info.Reroll;
        var exit = Make(map, field.Template, field.Data, marker, info, reroll, world.Clock.Now, RotationOn(marker), SubtypeOn(marker, info))
                   ?? throw new InvalidOperationException($"{name}: no prefab for {map.Cluster}");
        slot.Exit = exit;
        slot.FreeAt = TimeSpan.Zero;
        _slots[exit] = slot;
        map.AddObject(exit);
        return exit;
    }

    /// <summary>
    /// Whether an exit leads into a dungeon this server can make: a known generator beneath it and no corrupted, mists or hellgate entry;
    /// stageL: with <paramref name="corrupted"/> also a corrupted portal (&lt;CorruptedDungeonGenerator/&gt;, the corrupted dungeons' entrance), with
    /// <paramref name="mists"/> a mists portal (&lt;MistsGenerator mistsmode&gt;) of a mode mists.xml has (stageL mists SPEC §6.5).
    /// </summary>
    private static bool Usable(RandomDungeonData data, string exit, bool corrupted = false, Func<string, bool>? mists = null) =>
        data.Exits.TryGetValue(exit, out var info) && (info.Corrupted ? corrupted && info.Visualization is not null
            : info.MistsMode is { } mode ? mists?.Invoke(mode) == true && info.Visualization is not null
            : !exit.Contains("CORRUPTED", StringComparison.OrdinalIgnoreCase)
            && !exit.Contains("MISTS", StringComparison.OrdinalIgnoreCase) && !exit.Contains("HELLGATE", StringComparison.OrdinalIgnoreCase)
            && info.Dungeons.Any(d => d.Weight > 0 && data.Generators.ContainsKey(d.Generator)));

    /// <summary>StageL corrupted (SPEC §6 phase B 3): the distribution's CORRUPTED_SOLO_* exits stand on the open world's markers (Corrupted:OpenWorldPortals).</summary>
    private bool CorruptedPortals => world.Config.Corrupted is { Enabled: true, OpenWorldPortals: true };

    /// <summary>StageL mists (SPEC §6.5): the distribution's MISTS_* exits of a known mode stand on the markers (Mists:Enabled, Mists:OpenWorldWeight above 0).</summary>
    private Func<string, bool> MistsPortals => world.Mists.PortalOf;

    /// <summary>StageL corrupted (SPEC §4.8): the corrupted dungeon maps open their portal (Corrupted:MapsOpenDungeons; else they stay invasion tickets).</summary>
    private bool CorruptedMaps => world.Config.Corrupted is { Enabled: true, MapsOpenDungeons: true };

    /// <summary>
    /// Key 2 of an exit on <paramref name="marker"/>: a RandomExitPositionMarker's rotation + 180 (SPEC §2.1, captured); a fixed exit is the
    /// layout's own ROADS_FIXED_EXIT_* / fixed-exit tile, at the rotation the layout gives it [inference: no capture has a fixed exit; stageL
    /// roads client check B1], so that its spawnpos (the arrival out, <see cref="RandomDungeonExit.ArrivalOut"/>) is in front of the camp's entrance.
    /// </summary>
    private static float RotationOn(DungeonExitMarker marker) => (float)TileMath.Mod360(marker.Type == "FIXED" ? marker.Direction : marker.Direction + 180);

    /// <summary>
    /// The subtype name of an exit on <paramref name="marker"/>: the marker's, but a road dungeon's fixed exit (stageL roads SPEC §7.1,
    /// &lt;faction&gt;_T&lt;n&gt;_ROAD_SOLO / _VETERAN / _ELITE [inference: the name, the generator *_SOLO / plain / *_ELITE]) that of its suffix,
    /// so that a solo one is per party (Dungeons:PerPartySubtypes) with the solo lifetime and the client's Solo subtype (key 18).
    /// </summary>
    private static string SubtypeOn(DungeonExitMarker marker, RandomExitInfo info)
    {
        var suffix = info.Name[(info.Name.LastIndexOf('_') + 1)..];
        return marker.Type == "FIXED" && info.Name.Contains("_ROAD_", StringComparison.Ordinal) && suffix is "SOLO" or "VETERAN" or "ELITE" ? suffix : marker.Type;
    }

    /// <summary>bn5.Subtypes of a marker type (SOLO 1, VETERAN 2, ELITE 3, EVENT_SOLO 4, EVENT_VETERAN 5; a fixed exit counts as veteran [assume]).</summary>
    public static byte SubtypeOf(string type) => type.ToUpperInvariant() switch
    {
        "SOLO" => 1, "VETERAN" => 2, "ELITE" => 3, "EVENT_SOLO" => 4, "EVENT_VETERAN" => 5, _ => 2,
    };

    /// <summary>
    /// The exit object for <paramref name="info"/> at <paramref name="at"/> (a marker, or the GM's spot): the dungeon beneath drawn
    /// by weight, the prefab of its visualization for the cluster's biome and subbiome (SPEC §8.2) with its component's size
    /// and spawnpos, key 2 = <paramref name="rotation"/> (a marker's rotation + 180), key 4 from ClusterTypeMappings, the level and its first reroll.
    /// </summary>
    private RandomDungeonExit? Make(Map map, ClusterTemplate template, RandomDungeonData data, ClusterObject at, RandomExitInfo info, string? reroll, TimeSpan now,
        float rotation, string subtypeName, (string, Guid[], int[], long)? reservation = null, bool fromMap = false, int? mistsTier = null)
    {
        var rng = Rng;
        var dungeons = info.Dungeons.Where(d => d.Weight > 0 && data.Generators.ContainsKey(d.Generator)).ToList();
        // StageL corrupted: a corrupted portal has no random dungeon beneath (the corrupted dungeon is made by Systems/CorruptedDungeons); nor a
        // mists portal (stageL mists: the zone is Systems/Mists'; phase B: a Knightfall Abbey portal's tier is its own, <paramref name="mistsTier"/>).
        if (info.Corrupted) dungeons = [(CorruptedGenerator, template.Name.Tier ?? 0, 1)];
        if (info.MistsMode is not null) dungeons = [(Mists.Generator, mistsTier ?? template.Name.Tier ?? 0, 1)];
        if (dungeons.Count == 0) return null;
        var (generator, tier, _) = DungeonGenerator.Pick(rng, [.. dungeons.Select(d => (d, d.Weight))]);
        string prefab;
        var visualization = 0;
        if (info.Fixed) prefab = at.Tile;
        else
        {
            var (prefabs, biome, subBiome) = PrefabsFor(template, data, info);
            if (prefabs.Count == 0)
            {
                Log.Warn("world", $"map {map.Cluster}: random exit {info.Name} has no prefab for {biome}/{subBiome}, not spawned");
                return null;
            }
            var p = DungeonGenerator.Pick(rng, [.. prefabs.Select(x => (x, x.Weight))]);
            (prefab, visualization) = (p.Name, p.Index);
        }
        var component = world.Clusters?.Tiles.Components(prefab).FirstOrDefault(c => c.Name.LocalName is "randomdungeonrandomexit" or "randomdungeonfixedexit");
        var rr = reroll is not null ? data.Rerolls.GetValueOrDefault(reroll) : null;
        var exit = new RandomDungeonExit(world.ObjectIds.Next(), at, info)
        {
            Generator = generator, Tier = tier, Rotation = rotation,
            ClusterType = info.Corrupted || info.MistsMode is not null ? "" : data.ClusterTypeFor(template.Info.Type, generator), Prefab = prefab, Visualization = visualization,
            Subtype = SubtypeOf(subtypeName), SubtypeName = subtypeName,
            Size = TileMath.V2((string?)component?.Attribute("size")) ?? Vector2.One, SpawnPos = TileMath.V2((string?)component?.Attribute("spawnpos")) ?? Vector2.Zero,
            Reroll = rr, Level = LevelOf(info, rr, rng), Reservation = reservation, FromMap = fromMap,
        };
        if (rr is { SingleRoll: false }) exit.NextReroll = now + RerollDelay(rr, rng);
        return exit;
    }

    /// <summary>The prefabs a random exit picks from in a cluster of <paramref name="template"/> (SPEC §8.2: its visualization for the cluster's biome and subbiome), with those names.</summary>
    private static (IReadOnlyList<ExitPrefab> Prefabs, string Biome, string SubBiome) PrefabsFor(ClusterTemplate template, RandomDungeonData data, RandomExitInfo info)
    {
        var cni = ClusterNameInfo.Parse(template.Info.File);
        var biome = ClusterNameInfo.BiomeNames.GetValueOrDefault(cni?.Biome ?? -1, "");
        var subBiome = ClusterNameInfo.SubBiomes.TryGetValue(cni?.SubBiome ?? -1, out var sb) ? sb[0] : "";
        return (info.Visualization is { } v && data.Visualizations.TryGetValue(v, out var vis) ? vis.For(biome, subBiome) : [], biome, subBiome);
    }

    private static byte RollLevel(LevelReroll r, Random rng) => r.Levels.Count == 0 ? (byte)0 : DungeonGenerator.Pick(rng, [.. r.Levels.Select(l => (l.Level, l.Weight))]);

    /// <summary>An exit's level (key 9): a mists portal's rarity by Mists:RarityWeights over its reroll (stageL mists SPEC Q13), else its reroll's; 0 without.</summary>
    private byte LevelOf(RandomExitInfo info, LevelReroll? rr, Random rng) =>
        info.MistsMode is not null ? world.Mists.RollRarity(rr, rng) : rr is null ? (byte)0 : RollLevel(rr, rng);

    private static TimeSpan RerollDelay(LevelReroll r, Random rng) => TimeSpan.FromSeconds(Math.Max(1, r.MinSeconds + rng.NextDouble() * Math.Max(0, r.MaxSeconds - r.MinSeconds)));

    /// <summary>
    /// .rd: an exit of <paramref name="name"/> (a RandomExit or FixedExit) at <paramref name="position"/> with rotation
    /// <paramref name="rotation"/>, on the player's map (cluster data and randomdungeons.xml read). It goes after
    /// Dungeons:GmExitSeconds unused, or lifetimeafterlastuse after its last use.
    /// </summary>
    public RandomDungeonExit Spawn(Map map, string name, Vector2 position, float rotation)
    {
        if (map.Template is not { } template || map.Grid is null) throw new InvalidOperationException($"map {map.Cluster} has no cluster data (yet)");
        if (DungeonLayout.IsInstance(map.Cluster)) throw new InvalidOperationException("random dungeon exits cannot be placed inside a dungeon");
        var data = world.Clusters?.DungeonsIfLoaded ?? throw new InvalidOperationException("randomdungeons.xml is not read (no cluster with exit markers resolved yet)");
        if (!data.Exits.TryGetValue(name, out var info)) throw new InvalidOperationException($"{name} is no RandomExit or FixedExit of randomdungeons.xml");
        if (!Usable(data, name, world.Config.Corrupted.Enabled, MistsPortals)) throw new InvalidOperationException($"{name} leads into no dungeon this server makes (hellgate, mists off or no generator)");
        var subtype = SubtypeName(info, data);
        var spot = new DungeonExitMarker
        {
            Key = new SpawnKey(map.Cluster, "gm-dungeon", _gmExits.Count), Tile = "", Position = position, Direction = (float)TileMath.Mod360(rotation - 180),
            Component = new System.Xml.Linq.XElement("randomdungeonrandomexitpositionmarker"), Type = subtype,
        };
        var reroll = data.Distribution.Values.SelectMany(d => d.Exits).FirstOrDefault(c => c.Exit == name && c.Reroll is not null)?.Reroll ?? info.Reroll;
        var exit = Make(map, template, data, spot, info, reroll, world.Clock.Now, (float)TileMath.Mod360(rotation), subtype)
                   ?? throw new InvalidOperationException($"{name}: no prefab for this cluster");
        if (Settings.GmExitSeconds > 0) exit.Expires = world.Clock.Now + TimeSpan.FromSeconds(Settings.GmExitSeconds);
        _gmExits.Add(exit);
        map.AddObject(exit);
        return exit;
    }

    /// <summary>
    /// StageL mists phase B (SPEC §8 items 1-3): a portal inside a mists zone (a Knightfall Abbey wave's, a wisp's) of <paramref name="info"/> at
    /// <paramref name="position"/>: its level fixed at <paramref name="level"/> (no reroll), its tier <paramref name="tier"/>, gone after
    /// <paramref name="lifeSeconds"/> unused or lifetimeafterlastuse after a use (as a GM's exit); null without randomdungeons.xml or a prefab.
    /// </summary>
    public RandomDungeonExit? SpawnMistsPortal(Map map, RandomExitInfo info, Vector2 position, float rotation, byte level, int tier, double lifeSeconds)
    {
        if (map.Template is not { } template || world.Clusters?.DungeonsIfLoaded is not { } data) return null;
        var spot = new DungeonExitMarker
        {
            Key = new SpawnKey(map.Cluster, "mists-portal", map.CustomSpawns++), Tile = "", Position = position, Direction = (float)TileMath.Mod360(rotation - 180),
            Component = new System.Xml.Linq.XElement("randomdungeonrandomexitpositionmarker"), Type = "SOLO",
        };
        var now = world.Clock.Now;
        if (Make(map, template, data, spot, info, null, now, (float)TileMath.Mod360(rotation), "SOLO", mistsTier: tier) is not { } exit) return null;
        (exit.Level, exit.Frozen, exit.Expires) = (level, true, now + TimeSpan.FromSeconds(Math.Max(1, lifeSeconds)));
        _gmExits.Add(exit);
        map.AddObject(exit);
        return exit;
    }

    /// <summary>The marker subtype an exit is distributed on (the first distribution entry naming it), SOLO when none does.</summary>
    private static string SubtypeName(RandomExitInfo info, RandomDungeonData data) =>
        data.Distribution.FirstOrDefault(d => d.Value.Exits.Any(c => c.Exit == info.Name)).Key.Subtype ?? (info.Fixed ? "VETERAN" : "SOLO");

    /// <summary>
    /// The dungeon <paramref name="player"/> enters through <paramref name="exit"/>: the open one of its party or itself behind a
    /// per-party exit, else the exit's; made now on the first use (SPEC §6.2 step 1: generator and tier of the exit, the draw of
    /// §5.3 steps 1-2, the floor ids up front, the level frozen). Null when the data cannot make it.
    /// </summary>
    public DungeonGroup? GroupFor(Player player, RandomDungeonExit exit)
    {
        var key = Settings.PerPartySubtypes.Contains(exit.SubtypeName)
            ? world.Parties.Of(player.Character.Id) is { } party ? "party:" + party.Id.ToString(CultureInfo.InvariantCulture) : "player:" + player.Character.Id : "";
        if (_groups.FirstOrDefault(g => g.Exit == exit && g.Key == key && !g.Closed) is { } open) return open;
        if (world.Clusters?.DungeonsIfLoaded is not { } data || !data.Generators.TryGetValue(exit.Generator, out var generator) || player.Map is not { } map) return null;
        var seed = Rng.Next();
        var plan = DungeonGenerator.Draw(generator, exit.Subtype, new Random(seed), Settings.Floors);
        var ids = plan.PathLengths.Select(_ => DungeonLayout.InstanceId(Guid.NewGuid())).ToList();
        // StageH PvE: the party members online (Dungeons:GroupScaling) and the dungeon of the day (Dungeons:DailyBonus), both at its making.
        var players = world.Parties.Of(player.Character.Id) is { } members ? Math.Max(1, members.Members.Count(m => world.Parties.Online(m.Id) is not null)) : 1;
        var daily = Settings.DailyFaction(new DateTime(world.GameNow, DateTimeKind.Utc));
        var group = new DungeonGroup
        {
            Exit = exit, Key = key, Entrance = map.Cluster, Out = exit.ArrivalOut(map.Cluster), Plan = plan, Tier = exit.Tier, Level = exit.Level,
            ClusterType = data.ClusterTypeFor(map.Template?.Info.Type ?? "", generator.Name), Dragon = DungeonGenerator.IsDragon(data, plan), Seed = seed, Ids = ids,
            Floors = new DungeonFloor?[ids.Count], Builds = new Task<DungeonFloor>?[ids.Count], Created = world.Clock.Now,
            Players = players, Bonus = daily is not null && string.Equals(daily, generator.Faction, StringComparison.OrdinalIgnoreCase) ? Settings.DailyBonus : 1,
        };
        exit.Frozen = true; // SPEC §8.4: the level is the dungeon's from now on
        _groups.Add(group);
        for (var i = 0; i < ids.Count; i++) _ids[ids[i]] = (group, i);
        Log.Info("world", string.Create(CultureInfo.InvariantCulture, $"dungeon {ids[0]}: {generator.Name} T{exit.Tier} level {exit.Level}, {plan.Subset}/{plan.Environment}/{plan.EnvironmentSubset}/{plan.Flag}, "
            + $"{ids.Count} floor(s) of {string.Join("/", plan.PathLengths)} cells, behind {exit.Name} in {map.Cluster}{(key.Length > 0 ? $" for {key}" : "")}"
            + $", {players} player(s){(group.Bonus != 1 ? $", dungeon of the day x{group.Bonus:0.##}" : "")}"));
        return group;
    }

    /// <summary>
    /// The build of floor <paramref name="index"/> (SPEC §5.3, §6.1) on a pool thread: the layout (a new draw for each try while the
    /// floor's exits are not exactly …0001 and …0002, SPEC §2.6), the cluster resolved from it, its bytes; then, under the lock,
    /// the floor is the group's and the next one is built ahead. The same task for a floor asked twice.
    /// </summary>
    public Task<DungeonFloor> Build(DungeonGroup group, int index)
    {
        if (group.Floors[index] is { } made) return Task.FromResult(made);
        if (group.Builds[index] is { } running) return running;
        var store = world.Clusters ?? throw new InvalidOperationException("no cluster data");
        var data = store.DungeonsIfLoaded ?? throw new InvalidOperationException("randomdungeons.xml not read");
        var links = group.LinksOf(index);
        var task = Task.Run(() =>
        {
            var (layout, template) = MakeFloor(store, data, group.Plan, index + 1, group.Ids[index], group.Tier, group.ClusterType, group.Exit.Subtype, links, group.Dragon,
                unchecked(group.Seed * 31 + index));
            return new DungeonFloor(group, index, layout, layout.Write(), template);
        });
        group.Builds[index] = task;
        task.ContinueWith(t => world.Post(() =>
        {
            group.Builds[index] = null;
            if (!t.IsCompletedSuccessfully)
            {
                Log.Warn("world", t.Exception?.InnerException?.Message ?? "dungeon floor not made");
                return;
            }
            Adopt(group, index, t.Result);
        }), TaskScheduler.Default);
        return task;
    }

    /// <summary>
    /// One floor (1-based <paramref name="floor"/>) made and resolved: a new draw from <paramref name="seed"/> for each try (at most 8)
    /// while the floor's exits are not exactly …0001 then …0002 (SPEC §2.6: a third exit member throws in the client, an Exit
    /// template without one leaves no way down) or a cell has no template. Reads template files: never under the world lock.
    /// </summary>
    public static (DungeonLayout Layout, ClusterTemplate Template) MakeFloor(ClusterStore store, RandomDungeonData data, DungeonGenerator.Plan plan, int floor, string id,
        int tier, string clusterType, byte subtype, DungeonGenerator.Links links, bool dragon, int seed)
    {
        Exception? last = null;
        for (var attempt = 0; attempt < 8; attempt++)
        {
            try
            {
                var layout = DungeonGenerator.Floor(data, plan, floor, id, tier, clusterType, subtype, links, new Random(unchecked(seed * 7919 + attempt)), dragon);
                var template = store.FromLayout(layout);
                var guids = template.Exits.Select(e => e.Guid).ToList();
                if (guids.Count != 2 || guids[0] != DungeonExits.Up || guids[1] != DungeonExits.Down)
                    throw new InvalidDataException($"the floor has {guids.Count} exit(s), not …0001 then …0002");
                if (template.Problems.Count > 0) Log.Warn("data", $"dungeon {layout.Id}: {template.Problems[0]}");
                return (layout, template);
            }
            catch (InvalidDataException e) { last = e; }
        }
        throw new InvalidDataException($"floor {floor} of {id} ({plan.Generator.Name}) not made: {last?.Message}", last);
    }

    /// <summary>
    /// Under the lock, once per floor (the build's own continuation, or a change waiting for it that runs first): the floor is the
    /// group's, and the next one is built ahead. Nothing for a closed dungeon.
    /// </summary>
    internal void Adopt(DungeonGroup group, int index, DungeonFloor floor)
    {
        if (group.Closed || group.Floors[index] is not null) return;
        group.Floors[index] = floor;
        Log.Info("world", string.Create(CultureInfo.InvariantCulture,
            $"dungeon floor {index + 1}/{group.FloorCount} {floor.Id}: {floor.Layout.Templates.Count} templates, {floor.Bytes.Length} layout bytes, {floor.Template.Summary()}"));
        if (index + 1 < group.FloorCount && group.Floors[index + 1] is null && group.Builds[index + 1] is null) _ = Build(group, index + 1); // the next floor ahead
    }

    /// <summary>
    /// Character <paramref name="character"/> joined <paramref name="cluster"/>: when it is a dungeon floor, the dungeon is not empty (a
    /// Join resets the empty lifetime, SPEC §9). Its arrival is over in every dungeon it was sent into.
    /// </summary>
    public void Joined(string cluster, Guid character)
    {
        foreach (var group in _groups) group.Arriving.Remove(character);
        if (_ids.TryGetValue(cluster, out var g)) g.Group.EmptySince = null;
    }

    /// <summary>
    /// <paramref name="character"/> is sent into a floor of <paramref name="group"/> (the commit of an entry or a floor change, or a floor
    /// made for it): the dungeon counts as occupied until its Join, at most Dungeons:ArrivalGraceSeconds, so that it does not close
    /// while the client loads the floor.
    /// </summary>
    public void Sent(DungeonGroup group, Guid character)
    {
        group.EmptySince = null;
        group.Arriving[character] = world.Clock.Now + TimeSpan.FromSeconds(Settings.ArrivalGraceSeconds);
        if (group.Entered.Add(character)) group.Players = Math.Max(group.Players, group.Entered.Count); // stageH: the floors filled from now on scale for them too
    }

    /// <summary>
    /// StageH PvE: <paramref name="p"/> enters <paramref name="group"/> from the open world (the commit at its exit): on a dungeon of the day it
    /// gets Dungeons:DailyText as a system chat line once it has joined the floor (<see cref="Arrived"/>).
    /// </summary>
    public void Entering(Player p, DungeonGroup group)
    {
        if (group.Bonus != 1 && Settings.DailyText.Length > 0) group.DailyLine.Add(p.Character.Id);
    }

    /// <summary>
    /// After the Join of <paramref name="p"/> (WorldSession.AfterResponse, after JoinFinished): on a floor of a dungeon it entered from the open
    /// world (<see cref="Entering"/>), the dungeon of the day's chat line.
    /// </summary>
    public void Arrived(Player p)
    {
        if (p.Map is not { } map || Find(map.Cluster) is not { } floor || !floor.Group.DailyLine.Remove(p.Character.Id)) return;
        var group = floor.Group;
        Expeditions.Tell(p, Settings.DailyText.Replace("{faction}", DailyName(group.Plan.Generator.Faction), StringComparison.Ordinal)
            .Replace("{bonus}", group.Bonus.ToString("0.##", CultureInfo.InvariantCulture), StringComparison.Ordinal));
    }

    /// <summary>A faction for a chat line: HERETIC → Heretic.</summary>
    public static string DailyName(string faction) => faction.Length == 0 ? faction : faction[..1].ToUpperInvariant() + faction[1..].ToLowerInvariant();

    /// <summary>
    /// At the fill of a dungeon floor (MapSpawner, before its mobs): the floor level's factors (DifficultyLevels default, SPEC §7) × the
    /// dungeon of the day's bonus on silver, loot and fame (<see cref="Map.Difficulty"/>), and the group scaling of the dungeon's subtype for
    /// its players now (<see cref="Map.MobHealthFactor"/>, <see cref="Map.MobDamageFactor"/>). Nothing off a dungeon floor.
    /// </summary>
    internal void FillFactors(Map map)
    {
        if (Find(map.Cluster) is not { } floor) return;
        var group = floor.Group;
        var level = world.Clusters?.DungeonsIfLoaded?.Difficulties.GetValueOrDefault(group.Level);
        if (group.Bonus != 1)
        {
            var l = level ?? new DungeonDifficulty(null, 1, 1, 1); // level 0: no modifier, factors 1
            level = l with { Silver = l.Silver * group.Bonus, Loot = l.Loot * group.Bonus, MobFame = l.MobFame * group.Bonus };
        }
        map.Difficulty = level;
        var scale = Settings.GroupFactor(group.Exit.SubtypeName, group.Players);
        (map.MobHealthFactor, map.MobDamageFactor) = (scale, Settings.ScaleDamage ? scale : 1);
        if (scale != 1 || group.Bonus != 1)
            Log.Info("world", string.Create(CultureInfo.InvariantCulture,
                $"dungeon floor {floor.Index + 1} {map.Cluster}: mobs x{scale:0.##} for {group.Players} player(s), fame/silver/loot x{group.Bonus:0.##}"));
    }

    /// <summary>The exit was used (SPEC §6.2 step 2): it despawns lifetimeafterlastuse after the last use.</summary>
    public void Used(RandomDungeonExit exit) => exit.LastUse = world.Clock.Now;

    // ---- stageM exit: the click-and-channel entry (analysis/work/stageM/exit/NOTES.md §2-§4) ----

    /// <summary>
    /// The ExitEnterStart (237) channels at random dungeon exits under way: the player, the exit, when the channel ends, where it began and
    /// the health then (damage cancels). A portal prefab in usage mode 1 (the PORTAL_SOLO ones) is clicked, not walked into: the client runs
    /// a channel of gamedata ExitSettings/@channeledexitusagetime (2 s) and sends op 237 instead of ChangeCluster.
    /// </summary>
    private readonly Dictionary<Player, (RandomDungeonExit Exit, TimeSpan Due, Vector2 From, float Health)> _exiting = [];

    /// <summary>Whether <paramref name="p"/> channels at a random dungeon exit now.</summary>
    public bool Exiting(Player p) => _exiting.ContainsKey(p);

    /// <summary>
    /// The channel at <paramref name="exit"/> begins (the session checked the exit, the square and the cooldown): 253 ExitEnterStart
    /// {player, start, end, exit} to the observers (the player's own client runs its bar already), Dungeons:ExitChannelSeconds long.
    /// A second 237 starts it again.
    /// </summary>
    public void StartExit(Player p, RandomDungeonExit exit)
    {
        var now = world.GameNow;
        var length = TimeSpan.FromSeconds(Settings.ExitChannelSeconds);
        _exiting[p] = (exit, world.Now + length, p.PositionAt(DateTime.UtcNow), p.Health.At(now));
        p.SendToObservers(new ExitEnterStartEvent { ddn = p.Id, b5w = now, b5x = now + length.Ticks, b5y = exit.Id });
        Log.Info("world", $"'{p.Character.Name}' channels at random dungeon exit {exit.Name} ({Settings.ExitChannelSeconds:0.#} s)");
    }

    /// <summary>ExitEnterCancel (238), or a move, damage, a knockdown or the exit gone during the channel: 254 to the player and those in view. False when none ran.</summary>
    public bool CancelExit(Player p, string why)
    {
        if (!_exiting.Remove(p)) return false;
        p.SendAround(new ExitEnterCancelEvent { ddn = p.Id });
        Log.Info("world", $"'{p.Character.Name}' random dungeon exit cancelled ({why})");
        return true;
    }

    /// <summary>
    /// Every tick (<see cref="World.Update"/>): the exit channels. Broken by a move beyond Dungeons:ExitCancelDistance, damage, a knockdown,
    /// the exit despawning (254); dropped silently when the player left the map. Done: 255 ExitEnterFinished {player, exit} to the player
    /// and those in view, then the entry as a walk-in ChangeCluster would make it, its answer pushed (WorldSession.EnterChannelled).
    /// </summary>
    public void UpdateExits()
    {
        if (_exiting.Count == 0) return;
        var now = world.Now;
        foreach (var (p, (exit, due, from, health)) in _exiting.ToList())
        {
            if (p.Map is null || p.Map != exit.Map || p.Session.Player != p) _exiting.Remove(p);
            else if (!p.Map.Objects.TryGetValue(exit.Id, out var live) || !ReferenceEquals(live, exit)) CancelExit(p, "the exit is gone");
            else if (p.Combat.Down is not null) CancelExit(p, "knocked down");
            else if (p.Health.At(world.GameNow) < health - 0.5f) CancelExit(p, "damaged");
            else if (Vector2.Distance(p.PositionAt(DateTime.UtcNow), from) > Settings.ExitCancelDistance) CancelExit(p, "moved");
            else if (now >= due)
            {
                _exiting.Remove(p);
                p.SendAround(new ExitEnterFinishedEvent { ddn = p.Id, b5v = exit.Id });
                p.Session.EnterChannelled(p, exit);
            }
        }
    }

    /// <summary>
    /// Every Dungeons:UpdateSeconds (<see cref="World.Update"/>): the exits' rerolls (event 324 again with the same id when the level
    /// changes), the used exits' despawn after lifetimeafterlastuse (Leave; the marker free after its cooldown and the exit's
    /// activationtime), the refills of free markers, the GM exits' expiry, and the dungeons: empty since the subtype's
    /// lifetimeafteremptyseconds, or older than maxlifetimeseconds, they close (SPEC §9).
    /// </summary>
    public void Update(TimeSpan now)
    {
        if (now < _next) return;
        _next = now + TimeSpan.FromSeconds(Settings.UpdateSeconds);
        foreach (var map in _fields.Keys.Where(m => m.Unloaded).ToList())
        {
            foreach (var slot in _fields[map].Slots)
                if (slot.Exit is { } gone) _slots.Remove(gone);
            _fields.Remove(map);
        }
        _gmExits.RemoveAll(e => e.Map is not { Unloaded: false } || !e.Map.Objects.ContainsKey(e.Id));
        foreach (var exit in _slots.Keys.Concat(_gmExits).ToList()) UpdateExit(exit, now);
        foreach (var field in _fields.Values)
            foreach (var group in field.Slots.GroupBy(GroupOf))
                if (group.Count(s => s.Exit is not null) < Cap(field, group.Key, group.Count()) && SpawnOn(field, group, now) is { } exit)
                {
                    field.Map.AddObject(exit);
                    Log.Debug("world", $"map {field.Map.Cluster}: {exit}");
                }

        var data = world.Clusters?.DungeonsIfLoaded;
        foreach (var group in _groups.ToList())
        {
            var inside = group.Ids.Sum(id => world.Maps.Find(id)?.Players.Count ?? 0);
            foreach (var late in group.Arriving.Where(a => now >= a.Value).Select(a => a.Key).ToList()) group.Arriving.Remove(late);
            if (inside > 0 || group.Arriving.Count > 0) group.EmptySince = null; // a player loading a floor counts as inside
            else group.EmptySince ??= now;
            var empty = TimeSpan.FromSeconds(data?.EmptyLifetime(group.Exit.SubtypeName) ?? 300);
            var max = TimeSpan.FromSeconds(data?.MaxLifetimeSeconds ?? 87000);
            if (group.EmptySince is { } since && now - since >= empty) Close(group, $"empty for {empty.TotalSeconds:0} s");
            else if (now - group.Created >= max) Close(group, $"older than {max.TotalSeconds:0} s");
        }
    }

    private void UpdateExit(RandomDungeonExit exit, TimeSpan now)
    {
        if (exit.Map is not { } map) return;
        // SPEC §9: a map's exit lives its spell's time, used or not, so that it and its reservation (key 24) end together; its
        // lifetimeafterlastuse does not count [assume]. The others go lifetimeafterlastuse after their last use (a GM's also unused at Expires).
        var expired = exit.Expires is { } expires && now >= expires && (exit.LastUse is null || exit.FromMap);
        if (expired || !exit.FromMap && exit.LastUse is { } used && exit.Info.LifetimeAfterLastUseSeconds > 0
            && now - used >= TimeSpan.FromSeconds(exit.Info.LifetimeAfterLastUseSeconds))
        {
            Despawn(exit, now, expired ? "expired" : "used");
            return;
        }
        if (exit.Frozen || exit.Reroll is not { SingleRoll: false } reroll || now < exit.NextReroll) return; // singlerollonly: the spawn's draw stays
        exit.NextReroll = now + RerollDelay(reroll, Rng);
        Reroll(exit, LevelOf(exit.Info, reroll, Rng));
    }

    /// <summary>
    /// The exit's level becomes <paramref name="level"/>: when it changes, the whole event 324 again with the same object id to
    /// whoever sees it (SPEC §2.1: cjb.aw updates the exit in place; captured 0 → 2 with no Leave). Not once a dungeon froze it.
    /// </summary>
    public void Reroll(RandomDungeonExit exit, byte level)
    {
        if (exit.Frozen || level == exit.Level || exit.Map is not { } map) return;
        exit.Level = level;
        foreach (var p in map.ObserversOf(exit).ToList())
            foreach (var e in exit.BuildCreate(p)) p.Session.Send(e);
    }

    /// <summary>The exit goes: Leave to whoever sees it; its marker is free again after the cooldown and the exit's activationtime (SPEC §9).</summary>
    public void Despawn(RandomDungeonExit exit, TimeSpan now, string why)
    {
        exit.Map?.RemoveObject(exit);
        if (_slots.Remove(exit, out var slot))
        {
            slot.Exit = null;
            slot.FreeAt = now + TimeSpan.FromSeconds(exit.Info.CooldownSeconds + exit.Info.ActivationSeconds);
        }
        _gmExits.Remove(exit);
        Log.Info("world", $"{exit} despawned ({why})");
    }

    /// <summary>
    /// SPEC §9 "Closing": every player still inside gets the push {0: entrance} without a request id (WorldSession.Teleport, the
    /// §6.4 arrival); the floor maps go, the ids are never used again, the layouts are dropped. A later Join or SelectCharacter
    /// for one of its ids takes the character's <see cref="Character.DungeonReturn"/>.
    /// </summary>
    public void Close(DungeonGroup group, string why)
    {
        if (group.Closed) return;
        group.Closed = true;
        foreach (var id in group.Ids)
        {
            if (world.Maps.Find(id) is not { } map) continue;
            foreach (var p in map.Players.Values.ToList())
            {
                if (p.Session.Status == Network.SessionStatus.InWorld && p.Session.Player == p) Evict(p, group);
                else world.Maps.Remove(p, world.Clock.Now);
            }
            world.Maps.Unload(map, force: true);
        }
        foreach (var id in group.Ids) _ids.Remove(id);
        for (var i = 0; i < group.Floors.Length; i++) group.Floors[i] = null;
        _groups.Remove(group);
        Log.Info("world", $"dungeon {group.Ids[0]} closed ({why})");
    }

    /// <summary>
    /// A player still inside a closing dungeon goes out. A knocked-down one in a lethal clustertype dies first (Death.Abandoned: its
    /// items drop as when it leaves knocked down), so the close does not stand it up without its death; a dead one respawns
    /// (Death.Respawn when its respawn cluster is resolved, else at the entrance with the respawn's Join). A knockdown that is not
    /// lethal ends with the move, as a plain exit would end it [assume].
    /// </summary>
    private void Evict(Player p, DungeonGroup group)
    {
        if (p.Combat.Down is { Kind: not KnockdownKind.Dead }) Death.Abandoned(p);
        if (p.Combat.Down is { Kind: KnockdownKind.Dead })
        {
            if (world.CachedCluster(Death.RespawnCluster(p)) is not null && Death.Respawn(p, "dungeon closed")) return;
            p.Session.Teleport(group.Out, Death.RespawnVia);
            return;
        }
        p.Session.Teleport(group.Out, "dungeon closed");
    }

    /// <summary>
    /// The cluster and point of a character whose saved cluster is the id of a dungeon that is gone (SPEC §9 step 4, §11): its
    /// <see cref="Character.DungeonReturn"/>, else Dungeons:FallbackCluster; the character is changed and saved. False when the
    /// character's cluster is no closed dungeon id.
    /// </summary>
    public bool ReturnIfClosed(Character c)
    {
        if (!DungeonLayout.IsInstance(c.Cluster) || IsLive(c.Cluster)) return false;
        var from = c.Cluster;
        var to = c.DungeonReturn ?? new DungeonReturn(Settings.FallbackCluster, 0, 0, 0);
        c.Cluster = to.Cluster;
        c.PositionX = to.X;
        c.PositionY = to.Y;
        c.Direction = to.Direction;
        c.PendingTransfer = null;
        world.Saves.SaveNow(c);
        Log.Info("world", string.Create(CultureInfo.InvariantCulture, $"'{c.Name}' was in dungeon {from}, which is closed: back in {to.Cluster} ({to.X:0.##}, {to.Y:0.##})"));
        return true;
    }

    // ---- inside a floor: the chests (SPEC §7) ----

    /// <summary>
    /// At a dungeon floor's fill (MapSpawner): a <see cref="LootChest"/> on every chest point of its active layers whose
    /// LootChestSpawnpoint lootchests.xml has, the chest drawn by the spawnpoint's weights and its rare state by theirs (on the map's
    /// random), with Dungeons:ChestState. Nothing on an open-world map, with Dungeons:Chests off, or before lootchests.xml is read
    /// (the floor's resolve reads it).
    /// </summary>
    public IEnumerable<LootChest> ChestsOf(Map map, ClusterTemplate template)
    {
        if (!Settings.Chests || template.LootChests.Count == 0 || !DungeonLayout.IsInstance(map.Cluster) || world.Clusters?.LootChestsIfLoaded is not { } data) yield break;
        foreach (var point in template.LootChests)
            if (MakeChest(map, point, data) is { } chest) yield return chest; // an unknown spawnpoint is in the template's problems
    }

    /// <summary>
    /// The chest of <paramref name="point"/>: the spawnpoint's chest and the chest's rare state by weight on the map's random, with the
    /// tile of the map's biome and sub-biome (NewLootChest key 4, <see cref="LootChestTemplate.TileFor"/>: Forest/green on a dungeon
    /// floor); null when none can be drawn. A chest without a tile is never drawn (logged once): the client would place nothing. StageL
    /// corrupted (SPEC §4.2 step 7 [V]): a corrupted dungeon's chests with their own <paramref name="state"/> (BossLocked), the run's
    /// <paramref name="lootTier"/> and <paramref name="lootFactor"/> (the infamy bonus).
    /// </summary>
    internal LootChest? MakeChest(Map map, LootChestSpawn point, LootChestData data, byte? state = null, int? lootTier = null, double lootFactor = 1, bool personal = false)
    {
        var (biome, folder) = (map.Template?.Name.Biome, map.Template?.Name.TemplateFolder ?? "NONE");
        var choices = point.Behaviour?.Chests.Where(c => c.Weight > 0 && data.Chests.ContainsKey(c.Chest)).ToList() ?? [];
        foreach (var (name, _) in choices.Where(c => data.Chests[c.Chest].TileFor(biome, folder).Length == 0))
            Combat.LogOnce("chesttile:" + name, $"loot chest {name} has no tile for {biome}/{folder} and no prefab: not spawned");
        choices.RemoveAll(c => data.Chests[c.Chest].TileFor(biome, folder).Length == 0);
        if (choices.Count == 0) return null;
        var chest = data.Chests[DungeonGenerator.Pick(map.Random, [.. choices.Select(c => (c.Chest, c.Weight))])];
        var states = chest.States.Where(s => s.Weight > 0).ToList();
        if (states.Count == 0) return null;
        var rare = DungeonGenerator.Pick(map.Random, [.. states.Select(s => (s, s.Weight))]);
        // StageH PvE (Dungeons:BossChests): a boss chest of a floor spawns BossLocked; BossChecks unlocks it once no boss guards it.
        var locked = Settings.BossChests && IsBossChest(point.SpawnBehaviour) && (DungeonLayout.IsInstance(map.Cluster) || MistsIds.IsDungeon(map.Cluster)); // stageL: Knightfall Abbey's too
        var lockState = state ?? (locked ? BossLocked : Settings.ChestState);
        // StageL mists (SPEC §6.1): a personal chest (a mist boss's reward) is LockedByQuest (4) to everyone its maker does not open it for.
        return new LootChest(world.ObjectIds.Next(), point, chest, rare, chest.TileFor(biome, folder), lockState, world.GameNow)
        {
            LootTier = lootTier, LootFactor = lootFactor, Personal = personal, DefaultViewerState = personal ? (byte)4 : lockState,
        };
    }

    /// <summary>ChestState BossLocked (stageH bosses SPEC §3.6).</summary>
    public const byte BossLocked = 0;

    /// <summary>Whether a LootChestSpawnpoint is a boss one (…_LOOTCHEST_BOSS_RARE, …_LOOTCHEST_MINIBOSS): its name holds BOSS.</summary>
    public static bool IsBossChest(string spawnpoint) => spawnpoint.Contains("BOSS", StringComparison.OrdinalIgnoreCase);

    /// <summary>A boss or miniboss of mobs.xml (mobtypecategory), standing (not dead, not taken off).</summary>
    private static bool StandingBoss(Mob m) => m is { Dead: false, Removed: false } && m.Template.Category is "boss" or "miniboss";

    /// <summary>
    /// Whether <paramref name="chest"/> is still guarded (Dungeons:BossChests). Its guards are fixed at its first check (the floor's fill, or its
    /// GM spawn): with a boss or miniboss of the floor (standing or dead) whose spawn point is within Dungeons:BossChestRadius of it, those of
    /// them standing; with none that near, every standing one of the floor (<paramref name="standing"/>). It stays guarded until each of them
    /// is dead or off the map; a boss that respawns later is another object and does not lock it again.
    /// </summary>
    private bool Guarded(LootChest chest, Map map, IReadOnlyList<Mob> standing)
    {
        if (chest.Guards is null)
        {
            bool Near(Mob m) => Vector2.Distance(m.Spawn.Position, chest.Position) <= Settings.BossChestRadius;
            var guards = map.Objects.Values.OfType<Mob>().Any(m => m.Template.Category is "boss" or "miniboss" && Near(m)) ? standing.Where(Near) : standing;
            chest.Guards = guards.Select(m => m.Id).ToHashSet();
        }
        return standing.Any(m => chest.Guards.Contains(m.Id));
    }

    /// <summary>
    /// The BossLocked chests of a dungeon floor whose guard is gone unlock (UpdateLootChest to every observer): at the floor's fill (after
    /// its mobs: a floor without a boss leaves none locked), at a GM chest, and when a boss or miniboss of the floor dies (Combat.Kill) or is
    /// taken off (SpawnManager.Despawn: .mob kill, a world boss's end).
    /// </summary>
    public void BossChecks(Map map)
    {
        if (!Settings.BossChests || !DungeonLayout.IsInstance(map.Cluster) && !MistsIds.IsDungeon(map.Cluster)) return; // stageL mists phase B: Knightfall Abbey's boss chests

        var locked = map.Objects.Values.OfType<LootChest>().Where(c => c.LockState == BossLocked && !c.Personal).ToList();
        if (locked.Count == 0) return;
        var bosses = map.Objects.Values.OfType<Mob>().Where(StandingBoss).ToList();
        foreach (var chest in locked.Where(c => !Guarded(c, map, bosses)))
        {
            chest.SetState(6, world.GameNow);
            foreach (var o in map.ObserversOf(chest)) o.Session.Send(chest.Update(o));
            Log.Info("world", $"{chest} unlocked: no boss guards it any more");
        }
    }

    /// <summary>A mob of a dungeon floor died (Combat.Kill, after the kill batch): a boss or miniboss may free its chests.</summary>
    public void Killed(Map map, Mob mob)
    {
        if (mob.Template.Category is "boss" or "miniboss") BossChecks(map);
    }

    /// <summary>
    /// .rd chest: a chest of LootChestSpawnpoint <paramref name="spawnpoint"/> at <paramref name="position"/> on <paramref name="map"/> (any map:
    /// the client check of SPEC §13 and the self-test), with the floor's tier and level inside a dungeon, the cluster's tier elsewhere.
    /// </summary>
    public LootChest SpawnChest(Map map, string spawnpoint, Vector2 position, float direction)
    {
        var data = world.Clusters?.LootChestsIfLoaded ?? throw new InvalidOperationException("lootchests.xml is not read (no dungeon floor with chests resolved yet)");
        if (!data.Spawnpoints.TryGetValue(spawnpoint, out var behaviour)) throw new InvalidOperationException($"{spawnpoint} is no LootChestSpawnpoint of lootchests.xml");
        var point = new LootChestSpawn
        {
            Key = new SpawnKey(map.Cluster, "gm-chest", map.CustomSpawns++), Tile = "", Position = position, Direction = (float)TileMath.Mod360(direction),
            Component = new XElement("lootchestspawnpoint", new XAttribute("spawnbehaviour", spawnpoint)), SpawnBehaviour = spawnpoint, Behaviour = behaviour,
        };
        var chest = MakeChest(map, point, data) ?? throw new InvalidOperationException($"{spawnpoint} draws no chest lootchests.xml has");
        map.AddObject(chest);
        BossChecks(map); // a boss chest with no boss on the floor opens at once
        return chest;
    }

    /// <summary>
    /// <paramref name="p"/> uses <paramref name="chest"/> (UseLootChest, or the end of the slot-25 chest cast, <see cref="FakeSlotCasts.StartChest"/>):
    /// <see cref="OpenChest"/>, dismounted (stageD mounts SPEC §4.3, as for a bag), LootChestOpened (394) {0 chest, 1 now} to the opener alone for
    /// a personal chest (stageH bosses SPEC §3.5: its own stamp is the viewer state's), else to whoever sees it with the chest's opening stamp
    /// (key 13 / 393 key 8 from now on, as the client's et); after an empty roll UpdateLootChest again, so the client ends on Empty (394 sets
    /// Open, SPEC §3.3). Null, or why not.
    /// </summary>
    public string? UseChest(Player p, LootChest chest)
    {
        if (OpenChest(p, chest) is { } why) return why;
        Mounts.BeforeAction(p, "opens a chest");
        if (chest.Personal)
        {
            p.Session.Send(new LootChestOpenedEvent { ddn = chest.Id, cn2 = world.GameNow });
            if (chest.ViewerState(p).State == 8) p.Session.Send(chest.Update(p));
            return null;
        }
        var map = chest.Map!;
        chest.OpenedTicks = world.GameNow;
        map.SendToObservers(chest, new LootChestOpenedEvent { ddn = chest.Id, cn2 = chest.OpenedTicks });
        if (chest.LockState == 8) foreach (var o in map.ObserversOf(chest)) o.Session.Send(chest.Update(o));
        return null;
    }

    /// <summary>
    /// <paramref name="p"/> opens <paramref name="chest"/> (<see cref="UseChest"/>, or RegisterToObject on it): the first opening rolls its loot,
    /// the rare state's &lt;LootByTier&gt; of the dungeon's tier (else the nearest tier below) as a mob's &lt;Loot&gt;
    /// (<see cref="LootTables.RollMob"/>) with the floor level's lootfactor × Rates:Loot as the loot factor, Loot:BlackMarketChance and
    /// Loot:BlackMarketSoftCap for the useblackmarket items and the tier as the content tier; a stackable item one stack of its amount,
    /// equipment one item per piece. Any other chest (open world, corrupted, hellgate, mists, expedition; stageH bosses SPEC §4.6) rolls
    /// its tier with Rates:Loot alone: no cluster moblootfactor × globalmoblootfactor (a mob's factor; the chest lists carry their own
    /// per-tier and per-rarity chances, loot-balance SPEC §1.2). A personal one rolls once per player into the player's own container
    /// (<see cref="LootChest.RollFor"/>, the Update to that player alone); × the chest's <see cref="LootChest.LootFactor"/> (a world
    /// event's reward, a corrupted run's infamy bonus, a hellgate chain). Null, or why not.
    /// </summary>
    public string? OpenChest(Player p, LootChest chest)
    {
        if (!chest.MayOpen(p)) return $"locked (ChestState {chest.ViewerState(p).State})";
        if (world.Corrupted.ChestRefused(p, chest) is { } refused) return refused; // stageL corrupted: a run's chests are its owner's
        if (world.Hellgates.ChestRefused(p, chest) is { } lost) return lost; // stageL hellgates phase B: a match's chests are the winners'
        if (world.Territories.ChestRefused(p, chest) is { } notOwner) return notOwner; // stageP P5: a castle chest is its owner guild's (Castles:ChestNeedsOwnership)
        if (chest.RolledFor(p)) return null;
        var map = chest.Map!;
        var floor = Find(map.Cluster);
        var tier = chest.LootTier ?? (floor is { } f ? f.Group.Tier : map.Template?.Name.Tier ?? 4); // stage I: a player invasion's chest has its own
        var factor = floor is not null ? map.Difficulty?.Loot ?? 1 : 1; // a floor's with the dungeon of the day's bonus (FillFactors)
        factor *= chest.LootFactor; // a world event's reward chest: × RewardFactor; a corrupted run's 1 + bonus; a hellgate chain's
        var loot = chest.State.LootFor(tier);
        IReadOnlyList<(string Item, int Amount)> drops = loot is not null && world.Clusters?.LootIfLoaded is { } tables
            ? tables.RollMob(loot, map.Random, factor * world.Rates.Loot, world.Config.LootBlackMarketChance,
                name => Combat.LogOnce("loot:" + name, $"loot list {name} of chest {chest.Template.Name} is not in loot.xml"),
                world.Config.LootTierCap ? tier : 0, world.Config.LootBlackMarketSoftCap)
            : [];
        // Stage I: a player invasion's chest moves its items to its ItemTier and adds its Enchant, and a won ticket is one item more.
        if (chest.ItemTier is not null || chest.Enchant > 0) drops = [.. drops.Select(d => (Shift(world.Items, d.Item, chest.ItemTier, chest.Enchant), d.Amount))];
        if (chest.Bonus.TryGetValue(p.Character.Id, out var bonus)) drops = [.. drops, (bonus, 1)];
        drops = world.ModDrops.ForChest(chest.Template.Name, world.Rates.Loot, drops); // content packs (Mods:Packs)
        var items = new List<Item>();
        foreach (var (name, amount) in drops)
        {
            if (world.Items.Find(name) is not { CanCreate: true } type)
            {
                Combat.LogOnce("item:" + name, $"loot item {name} is not in items.xml or has no New*Item event yet: not in the chest");
                continue;
            }
            if (type.IsEquipment)
                for (var i = 0; i < amount; i++) items.Add(p.Session.NewItem(type, 1));
            else items.Add(p.Session.NewItem(type, amount));
        }
        chest.RollFor(p, items, world.GameNow);
        if (chest.Personal) p.Session.Send(chest.Update(p)); // its own Open (or Empty); the others keep theirs
        else foreach (var o in map.ObserversOf(chest)) o.Session.Send(chest.Update(o)); // Open (or Empty) for all at once, the full state per viewer
        world.WorldChests.Opened(chest, p);
        world.Vanity.ChestOpened(p, chest); // stageN vanity: a boss chest's first roll may drop a cosmetic
        world.Hellgates.Opened(chest); // stageL: a hellgate's objective chest claims its reward (state 4, the exit portal)
        Log.Info("world", $"'{p.Character.Name}' opened {chest} (T{tier}): {(items.Count == 0 ? "nothing" : string.Join(", ", items.Select(i => $"{i.Template.Name} x{i.Count}")))}");
        return null;
    }

    /// <summary>
    /// Stage I (invasions SPEC §4.1): loot item <paramref name="name"/> ("T4_X" or "T4_X@1") moved to <paramref name="itemTier"/>
    /// ("T2_X") where items.xml has that item (an enchanted one at @0 where the new tier has no such variant), then with <paramref name="enchant"/> levels more (base@min(e + k, 4)) where it has that
    /// variant; unchanged otherwise.
    /// </summary>
    public static string Shift(ItemStore items, string name, int? itemTier, int enchant)
    {
        var at = name.IndexOf('@');
        var (core, level) = at < 0 ? (name, 0) : (name[..at], int.TryParse(name[(at + 1)..], out var e) ? e : 0);
        static string Named(string b, int l) => l > 0 ? $"{b}@{l}" : b;
        if (itemTier is { } t && core.Length > 3 && core[0] == 'T' && char.IsAsciiDigit(core[1]) && core[2] == '_')
        {
            var moved = $"T{t}{core[2..]}";
            if (items.Find(Named(moved, level)) is not null) core = moved;
            // T2/T3 have no @1+ variants: the new tier at @0 rather than the enchanted item kept at its own tier
            else if (level > 0 && items.Find(moved) is not null) (core, level) = (moved, 0);
        }
        if (enchant > 0 && level < 4 && items.Find(Named(core, Math.Min(4, level + enchant))) is not null) level = Math.Min(4, level + enchant);
        return Named(core, level);
    }

    // ---- dungeon maps (SPEC §10) ----

    /// <summary>The generator name a corrupted portal carries (it has no random dungeon beneath).</summary>
    public const string CorruptedGenerator = "CORRUPTED";

    /// <summary>The marker subtype of an activaterandomdungeon exittype (solo, veteran, elite).</summary>
    private static string MapSubtype(XElement node) => ((string?)node.Attribute("exittype") ?? "solo").ToUpperInvariant();

    /// <summary>Whether <paramref name="spell"/> is a dungeon map's consumespell (it has activaterandomdungeon blocks): one exit per use, one use at a time.</summary>
    public static bool IsMap(SpellTemplate spell) => spell.Effects.Any(e => e.Name.LocalName == "activaterandomdungeon");

    /// <summary>
    /// mindistance, maxdistance and staysameclustertype of an activaterandomdungeon block with the client's defaults (bju.oh
    /// 0x1806D1240: 0, 1, true; dungeon-maps SPEC §2). The client only parses them; the server counts cluster hops (§3).
    /// </summary>
    private static (int Min, int Max, bool Same) MapRange(XElement node) =>
        ((int)SpellTemplate.Float(node, "mindistance"), (int)SpellTemplate.Float(node, "maxdistance", 1), SpellTemplate.Bool(node, "staysameclustertype", true));

    /// <summary>The worldsettings archetype of a cluster type (GREEN, YELLOW, RED, BLACK, TUNNEL …); null when unknown.</summary>
    private string? ArchetypeOf(string? type)
    {
        if (type is not { Length: > 0 } || world.Clusters is not { } store) return null;
        try { return store.Archetypes.Of(type).Archetype; }
        catch (Exception x) when (x is InvalidDataException or IOException) { return null; }
    }

    /// <summary>
    /// The world.xml clusters <paramref name="min"/>..<paramref name="max"/> hops from <paramref name="from"/> (dungeon-maps SPEC §3):
    /// breadth-first over the exits of targettype Cluster (any cluster on the way, cities too), <paramref name="from"/> itself at
    /// hop 0; with <paramref name="same"/> only those of <paramref name="from"/>'s archetype. Just <paramref name="from"/> (at min 0)
    /// without world.xml.
    /// </summary>
    public List<string> MapClusters(string from, int min, int max, bool same)
    {
        if (world.Clusters?.WorldIfLoaded is not { } info || info.Find(from) is not { } start) return min == 0 ? [from] : [];
        var archetype = ArchetypeOf(start.Type);
        var hops = new Dictionary<string, int>(StringComparer.Ordinal) { [from] = 0 };
        var queue = new Queue<string>();
        queue.Enqueue(from);
        while (queue.TryDequeue(out var cluster))
        {
            var hop = hops[cluster];
            if (hop >= max) continue;
            foreach (var exit in info.Find(cluster)?.Exits ?? [])
                if (exit.TargetType == "Cluster" && info.Find(exit.TargetCluster) is not null && hops.TryAdd(exit.TargetCluster, hop + 1))
                    queue.Enqueue(exit.TargetCluster);
        }
        return [.. hops.Where(h => h.Value >= min && (!same || h.Key == from || ArchetypeOf(info.Find(h.Key)!.Type) == archetype))
            .OrderBy(h => h.Value).ThenBy(h => h.Key, StringComparer.Ordinal).Select(h => h.Key)];
    }

    /// <summary>
    /// Whether cluster <paramref name="cluster"/> may take block <paramref name="node"/>'s exit: its live map has a free marker
    /// (<see cref="CanPlace"/>), or it is not loaded and its resolved cluster (when cached) has a marker of the exittype; an
    /// unresolved cluster counts as yes (it is read when the map is used, <see cref="PlaceFrom"/>).
    /// </summary>
    private bool MayPlace(string cluster, XElement node, TimeSpan now)
    {
        if (world.Maps.Find(cluster) is { Template: not null } map)
            return _fields.TryGetValue(map, out var field) && CanPlace(field, node, now, CorruptedMaps, out _, out _);
        if (world.CachedCluster(cluster) is { } template) return template.DungeonMarkers.Any(m => m.Type == MapSubtype(node));
        return world.Clusters is not null && world.LoadCluster is null;
    }

    /// <summary>
    /// Whether the cast of <paramref name="spell"/> (a dungeon map's consumespell) by <paramref name="p"/> now places an exit: one of
    /// the activaterandomdungeon blocks whose conditions hold (IfClusterArcheType, as the cast decides them) has a cluster within its
    /// distance (<see cref="MapClusters"/>) that may take it (<see cref="MayPlace"/>: a free marker of its exittype, a dungeon this server
    /// makes and a prefab for the cluster, what <see cref="FromMap"/> needs). Checked before the item is used and again at each use
    /// (UseConsumableFromInventory 14965 @RANDOMDUNGEONMAP_NO_DUNGEON_FOUND, stageD consumables SPEC §3), so a map is never eaten for
    /// nothing (an unread cluster farther away is the exception, dungeon-maps SPEC §4); true for a spell without such blocks.
    /// </summary>
    public bool MapCanPlace(Player p, SpellTemplate spell)
    {
        if (!IsMap(spell)) return true;
        if (p.Map is not { } map || !_fields.TryGetValue(map, out var field)) return false;
        var ticks = world.GameNow;
        var c = new SpellCasts.SpellContext(p, map, spell, 0, 0, null, SpellMovement.PositionOf(p, ticks), ticks);
        var now = world.Clock.Now;
        foreach (var b in spell.Effects)
        {
            if (b.Name.LocalName != "activaterandomdungeon" || !SpellCasts.ConditionsHold(c, b)) continue;
            var (min, max, same) = MapRange(b);
            if (max <= 0 ? CanPlace(field, b, now, CorruptedMaps, out _, out _) : MapClusters(map.Cluster, min, max, same).Any(cluster => MayPlace(cluster, b, now)))
                return true;
        }
        return false;
    }

    /// <summary>
    /// The exit of block <paramref name="node"/> and the free markers of its exittype, when it is a usable dungeon with a prefab here and a
    /// marker is free (a corrupted portal only with <paramref name="corrupted"/>, Corrupted:MapsOpenDungeons).
    /// </summary>
    private static bool CanPlace(Field field, XElement node, TimeSpan now, bool corrupted, out RandomExitInfo info, out List<Slot> free)
    {
        var name = (string?)node.Attribute("dungeon") ?? "";
        var subtype = MapSubtype(node);
        free = field.Slots.Where(s => s.Exit is null && s.FreeAt <= now && s.Marker.Type == subtype).ToList();
        return field.Data.Exits.TryGetValue(name, out info!) && Usable(field.Data, name, corrupted) && (info.Fixed || PrefabsFor(field.Template, field.Data, info).Prefabs.Count > 0)
               && free.Count > 0;
    }

    /// <summary>
    /// activaterandomdungeon (SPEC §10; the effect of a dungeon map's consumespell, the first block whose conditions hold): the exit
    /// <c>dungeon</c> on a random free marker of <c>exittype</c>, with level <c>difficulty</c> (no reroll), gone after <c>time</c> seconds
    /// unused; with a <c>reservationprofile</c> reserved for the caster and its party until then (keys 21/22/24). The caster gets
    /// RandomDungeonPositionInfo (391, §2.8). With maxdistance 0 the marker is in the caster's cluster; otherwise in a random one of
    /// the clusters mindistance..maxdistance hops away (<see cref="MapClusters"/>, the caster's own among them at mindistance 0;
    /// dungeon-maps SPEC §3): one not loaded is made and filled now, one not read yet is read on a pool thread first (the exit then
    /// follows a few ticks later). False when nothing could be placed (or started).
    /// </summary>
    public bool FromMap(Player p, XElement node)
    {
        var name = (string?)node.Attribute("dungeon") ?? "";
        var now = world.Clock.Now;
        if (p.Map is not { } map || !_fields.TryGetValue(map, out var field))
        {
            Log.Info("world", $"'{p.Character.Name}': dungeon map {name} has no place here ({p.Map?.Cluster})");
            return false;
        }
        var (min, max, same) = MapRange(node);
        if (max <= 0)
        {
            if (PlaceOn(p, field, node, now, remote: false) is not null) return true;
            Log.Info("world", $"'{p.Character.Name}': dungeon map {name} has no place here ({map.Cluster})");
            return false;
        }
        var order = MapClusters(map.Cluster, min, max, same).ToArray();
        Rng.Shuffle(order);
        if (PlaceFrom(p, node, order, 0)) return true;
        Log.Info("world", $"'{p.Character.Name}': dungeon map {name} has no place within {min}-{max} clusters of {map.Cluster}");
        return false;
    }

    /// <summary>
    /// <see cref="FromMap"/> at a distance: the first of <paramref name="order"/> from <paramref name="index"/> that takes the exit.
    /// A cluster not read yet is read on a pool thread and the walk goes on from it in a later tick (<see cref="World.Post"/>); true then.
    /// </summary>
    private bool PlaceFrom(Player p, XElement node, string[] order, int index)
    {
        var subtype = MapSubtype(node);
        for (var i = index; i < order.Length; i++)
        {
            var cluster = order[i];
            var now = world.Clock.Now;
            var map = world.Maps.Find(cluster);
            if (map is not { Template: not null })
            {
                if (world.CachedCluster(cluster) is not { } template)
                {
                    if (world.Clusters is not { } store || world.LoadCluster is not null) continue; // a test loader: only clusters already read
                    var next = i;
                    store.LoadAsync(cluster).ContinueWith(task => world.Post(() =>
                    {
                        if (!task.IsCompletedSuccessfully)
                            Log.Warn("world", $"dungeon map: cluster {cluster}: {task.Exception?.InnerException?.Message ?? "load cancelled"}");
                        if (!PlaceFrom(p, node, order, task.IsCompletedSuccessfully ? next : next + 1))
                            Log.Info("world", $"'{p.Character.Name}': dungeon map {(string?)node.Attribute("dungeon")} found no place (the map is used)");
                    }), TaskScheduler.Default);
                    return true;
                }
                if (!template.DungeonMarkers.Any(m => m.Type == subtype)) continue; // no map made for nothing
                if (map is null)
                {
                    map = world.Maps.Get(cluster);
                    map.EmptySince = now; // nobody on it: unloaded after World:MapIdleUnloadMinutes once its portal is gone (HoldsMapExit)
                }
                world.LoadTemplate(map); // cached: the map is filled now (its markers taken over, Populate)
            }
            if (_fields.TryGetValue(map, out var field) && PlaceOn(p, field, node, now, remote: map != p.Map) is not null) return true;
        }
        return false;
    }

    /// <summary>The exit of block <paramref name="node"/> on a free marker of <paramref name="field"/> (null when <see cref="CanPlace"/> fails), and event 391 to the caster.</summary>
    private RandomDungeonExit? PlaceOn(Player p, Field field, XElement node, TimeSpan now, bool remote)
    {
        if (!CanPlace(field, node, now, CorruptedMaps, out var info, out var free)) return null;
        var map = field.Map;
        var subtype = MapSubtype(node);
        var slot = free[Rng.Next(free.Count)];
        var seconds = SpellTemplate.Float(node, "time", 600);
        var level = (byte)Math.Clamp((int)SpellTemplate.Float(node, "difficulty"), 0, 4);
        (string, Guid[], int[], long)? reservation = null;
        if ((string?)node.Attribute("reservationprofile") is { Length: > 0 } profile)
        {
            var party = world.Parties.Of(p.Character.Id);
            Guid[] who = party is not null ? [.. party.Members.Select(m => m.Id)] : [p.Character.Id];
            reservation = (profile, who, party is not null ? [party.Id] : [], world.GameNow + SpellCasts.Seconds(seconds));
        }
        var exit = Make(map, field.Template, field.Data, slot.Marker, info, null, now, RotationOn(slot.Marker), subtype, reservation, fromMap: true);
        if (exit is null) return null;
        exit.Level = level;
        exit.Expires = now + TimeSpan.FromSeconds(seconds);
        slot.Exit = exit;
        _slots[exit] = slot;
        map.AddObject(exit);
        p.Session.Send(new RandomDungeonPositionInfoEvent
        {
            dsi = map.Cluster,          // [0] the cluster the exit stands in (the world map marks it: WorldmapCluster.SetRandomDungeon)
            dsj = exit.Position,        // [1]
            dsk = exit.Visualization,   // [2] [assume: visualization or level, SPEC §2.8]
            dsl = exit.Subtype,         // [3] bn5.Subtypes
            dsm = exit.Name,            // [4] the dungeon (exit) name
            dso = !remote,              // [6] da4: false = the client opens the world map on it (dungeon-maps SPEC §5); true = it does not
        });
        Log.Info("world", string.Create(CultureInfo.InvariantCulture, $"'{p.Character.Name}' opened a dungeon map: {exit} in {map.Cluster} for {seconds:0} s{(reservation is { } r ? $", reserved ({r.Item1})" : "")}"));
        return exit;
    }

    /// <summary>Whether <paramref name="map"/> holds a dungeon map's exit: it is not unloaded while it does (World.Update), so a portal in a cluster nobody is in stays for its time.</summary>
    public bool HoldsMapExit(Map map) => _slots.Keys.Any(e => e.FromMap && e.Map == map);

    // ---- a death inside ----

    /// <summary>
    /// Where a player who died on <paramref name="cluster"/> respawns when that is a live dungeon floor and Dungeons:RespawnAtEntrance
    /// is on: in front of the dungeon's exit in the open world (SPEC §6.4, the way out); null otherwise (the usual respawn).
    /// </summary>
    public Arrival? RespawnOf(string cluster) => Settings.RespawnAtEntrance && Find(cluster) is { } f ? f.Group.Out : null;
}
