using System.Collections.Concurrent;
using System.Globalization;
using AlbionLocal.Photon;

namespace AlbionLocal.Server.Data;

/// <summary>
/// The cluster data of the client's GameData folder (ARCHITECTURE.md §4.2, §5.1; SPEC stage 2 §2), loaded lazily and
/// cached: nothing is read at startup. The first cluster asked for reads cluster/world.bin (<see cref="WorldInfo"/>),
/// indexes cluster/tiles.bin (<see cref="TileIndex"/>, the biggest file, read in one stream), buildings.bin and
/// harvestables.bin; each cluster then reads its .cluster file and the templates it uses, which are cached by (folder,
/// ref) because many clusters share them. Thread-safe: the world asks for a map's cluster on a background thread
/// (<see cref="AlbionLocal.Server.World.LoadTemplate"/>) at the first Join into it, so the world lock never waits for the XML (§4.4). A
/// load that fails (not in world.xml; a file missing, broken or unreadable at the moment, e.g. while an official update
/// rewrites game-local) is not kept, neither for the cluster nor for the shared indexes: the next request reads again.
/// </summary>
public sealed class ClusterStore
{
    private readonly ConcurrentDictionary<string, Retrying<ClusterTemplate>> _clusters = new(StringComparer.Ordinal);
    private readonly Shared _shared;

    /// <summary>
    /// What every view of the store (<see cref="With"/>) shares: the indexes, the parsed templates and the layout files;
    /// only the resolved clusters are per view, because they depend on the view's <see cref="ClusterOptions"/>.
    /// </summary>
    private sealed class Shared
    {
        public readonly ConcurrentDictionary<string, Retrying<TemplateLayout?>> Layouts = new(StringComparer.Ordinal);
        public required Retrying<WorldInfo> World;
        public required Retrying<TileIndex> Tiles;
        public required Retrying<BuildingStore> Buildings;
        public required Retrying<HarvestableStore> Harvestables;
        public required Retrying<NameIndex> ExpeditionAgents;
        public required Retrying<NameIndex> QuestGiverTypes;
        public required Retrying<ResourcePresets> Presets;
        public required Retrying<ResourcePrefabMappings> Mappings;
        public required Retrying<DynamicTemplates> Dynamic;
        public required Retrying<WorldArchetypes> Archetypes;
        public required Retrying<RareResourceDistributions> Rare;
        public required Retrying<MobStore> Mobs;
        public required Retrying<SpawnBehaviors> SpawnBehaviors;
        public required Retrying<LootTables> Loot;
        public required Retrying<RandomDungeonData> Dungeons;
        public required Retrying<LootChestData> LootChests;
        public required Retrying<LocalQuestData> LocalQuests;
        public required Retrying<JournalCatalog> Journal;
        public required Retrying<PersonalSeasonData> Seasons;
        public required Retrying<ExpeditionData> Expeditions;
        public required Retrying<IslandData> Islands;
        public required Retrying<CorruptedData> Corrupted;
        public required Retrying<HellgateData> Hellgates;
        public required Retrying<RoadNetworkData> Roads;
        public required Retrying<MistsData> Mists;
        public required Retrying<BuffShrineData> BuffShrines;
        public required Retrying<EmoteData> Emotes;
        public required Retrying<MatchTypeData> MatchTypes;
        public required Retrying<ArenaData> Arena;
    }

    /// <param name="folder">The client's GameData folder; nothing is read before the first cluster is asked for.</param>
    public ClusterStore(string folder, ClusterOptions? options = null)
    {
        Folder = folder;
        Options = options ?? new ClusterOptions();
        Retrying<HarvestableStore> harvestables = new(() => HarvestableStore.Load(GameDataFile.Open(Folder, "harvestables.bin")));
        Retrying<ResourcePrefabMappings> mappings = new(() => ResourcePrefabMappings.Load(GameDataFile.Open(Folder, "resourceprefabmappings.bin").Root(),
            ResourcePresets.Registry(harvestables.Value)));
        Retrying<MobStore> mobs = new(() => MobStore.Load(GameDataFile.Open(Folder, "mobs.bin")));
        _shared = new Shared
        {
            World = new(() => WorldInfo.Load(GameDataFile.Open(Folder, Path.Combine("cluster", "world.bin")))),
            // The resource prefabs keep their footprint even when bare (their components come from compoundcontents).
            Tiles = new(() =>
            {
                HashSet<string> prefabs;
                try { prefabs = mappings.Value.PrefabNames.ToHashSet(StringComparer.Ordinal); }
                catch (Exception e) when (e is InvalidDataException or IOException) { prefabs = []; } // the clusters still resolve, without resource nodes
                return TileIndex.Load(GameDataFile.Open(Folder, Path.Combine("cluster", "tiles.bin")), TemplateLayout.TileKinds.Contains, prefabs.Contains);
            }),
            Buildings = new(() => BuildingStore.Load(GameDataFile.Open(Folder, "buildings.bin"), Options.BuildingIdOffset)),
            Harvestables = harvestables,
            ExpeditionAgents = new(() => new NameIndex(ClientNumbering.ExpeditionAgents(GameDataFile.Open(Folder, "expeditionagents.bin").Elements()))),
            QuestGiverTypes = new(() => new NameIndex(ClientNumbering.QuestGiverTypes(GameDataFile.Open(Folder, "questgivertypes.bin").Elements()))),
            Presets = new(() => ResourcePresets.Load(GameDataFile.Open(Folder, "resourcedistpresets.bin").Root(), ResourcePresets.Registry(harvestables.Value))),
            Mappings = mappings,
            Dynamic = new(() => DynamicTemplates.Load(GameDataFile.Open(Folder, "dynamictemplates.bin").Root(), GameDataFile.Open(Folder, "dynamicclusters.bin").Root())),
            Archetypes = new(() => WorldArchetypes.Load(GameDataFile.Open(Folder, "worldsettings.bin").Root())),
            Rare = new(() => RareResourceDistributions.Load(GameDataFile.Open(Folder, "rareresourcedistribution.bin").Root())),
            Mobs = mobs,
            SpawnBehaviors = new(() => SpawnBehaviors.Load(GameDataFile.Open(Folder, "randomspawnbehaviors.bin"), mobs.Value)),
            Loot = new(() => LootTables.Load(GameDataFile.Open(Folder, "loot.bin").Root())),
            Dungeons = new(() => RandomDungeonData.Load(GameDataFile.Open(Folder, "randomdungeons.bin").Root(), GameDataFile.Open(Folder, "templatemetadata.bin").Root())),
            LootChests = new(() => LootChestData.Load(GameDataFile.Open(Folder, "lootchests.bin").Root())),
            LocalQuests = new(() => LocalQuestData.Load(GameDataFile.Open(Folder, "localquests.bin").Root())),
            Journal = new(() => JournalCatalog.Load(GameDataFile.Open(Folder, "albionjournal.bin").Root())),
            Seasons = new(() => PersonalSeasonData.Load(GameDataFile.Open(Folder, "personalseasons.bin").Root())),
            Expeditions = new(() => ExpeditionData.Load(GameDataFile.Open(Folder, "expeditioncategories.bin").Root(), GameDataFile.Open(Folder, "expeditionagents.bin").Root(),
                GameDataFile.Open(Folder, "expeditions.bin").Root())),
            Islands = new(() => IslandData.Load(GameDataFile.Open(Folder, "worldsettings.bin").Root())),
            Corrupted = new(() => CorruptedData.Load(GameDataFile.Open(Folder, "corrupteddungeons.bin").Root())),
            Hellgates = new(() => HellgateData.Load(GameDataFile.Open(Folder, "hellgate.bin").Root())),
            Roads = new(() => RoadNetworkData.Load(GameDataFile.Open(Folder, "tunnelsystem.bin").Root(), GameDataFile.Open(Folder, "gamedata.bin").Root())),
            Mists = new(() => MistsData.Load(GameDataFile.Open(Folder, "mists.bin").Root(), GameDataFile.Open(Folder, "miststemplatemetadata.bin").Root())),
            BuffShrines = new(() => BuffShrineData.Load(GameDataFile.Open(Folder, "buffshrines.bin").Root())),
            Emotes = new(() => EmoteData.Load(GameDataFile.Open(Folder, "emotes.bin").Root())),
            MatchTypes = new(() => MatchTypeData.Load(GameDataFile.Open(Folder, "matchtypes.bin").Root(), GameDataFile.Open(Folder, "orbtypes.bin").Root())),
            Arena = new(() => ArenaData.Load(Folder)),
        };
    }

    private ClusterStore(ClusterStore of, ClusterOptions options)
    {
        Folder = of.Folder;
        Options = options;
        _shared = of._shared;
    }

    /// <summary>
    /// A view of the same client data with other <paramref name="options"/> (another <see cref="LayoutSession"/>): it shares
    /// the indexes and the parsed templates (and the building ids, numbered with this store's BuildingIdOffset), and
    /// resolves and caches its clusters for itself. The self-test uses it for the captures of other days.
    /// </summary>
    public ClusterStore With(ClusterOptions options) => new(this, options);

    /// <summary>The client's GameData folder (Paths:ClientGameData).</summary>
    public string Folder { get; }

    public ClusterOptions Options { get; }

    public WorldInfo World => _shared.World.Value;

    /// <summary>world.xml when it has been read already (by the first cluster resolved), else null: a look-up that never reads the file.</summary>
    public WorldInfo? WorldIfLoaded => _shared.World.IsValueCreated ? _shared.World.Value : null;

    public TileIndex Tiles => _shared.Tiles.Value;

    public BuildingStore Buildings => _shared.Buildings.Value;

    public HarvestableStore Harvestables => _shared.Harvestables.Value;

    /// <summary>expeditionagents.xml by uniquename (NewExpeditionAgent key 3).</summary>
    public NameIndex ExpeditionAgents => _shared.ExpeditionAgents.Value;

    /// <summary>questgivertypes.xml by uniquename (QuestGiverInfoForPlayer key 2).</summary>
    public NameIndex QuestGiverTypes => _shared.QuestGiverTypes.Value;

    /// <summary>resourcedistpresets.xml (stage 4b).</summary>
    public ResourcePresets Presets => _shared.Presets.Value;

    /// <summary>resourceprefabmappings.xml (stage 4b).</summary>
    public ResourcePrefabMappings Mappings => _shared.Mappings.Value;

    /// <summary>dynamictemplates.xml and dynamicclusters.xml (stage 4b).</summary>
    public DynamicTemplates Dynamic => _shared.Dynamic.Value;

    /// <summary>worldsettings.xml: the archetype (pvp warning level, pvp rules) of a world.xml cluster type.</summary>
    public WorldArchetypes Archetypes => _shared.Archetypes.Value;

    /// <summary>rareresourcedistribution.xml: the rare (enchantment) state weights of the resource nodes.</summary>
    public RareResourceDistributions RareResources => _shared.Rare.Value;

    /// <summary>mobs.xml (stage 5): read with the first cluster resolved, so the world thread never waits for it.</summary>
    public MobStore Mobs => _shared.Mobs.Value;

    /// <summary>mobs.xml when it has been read already, else null: a look-up that never reads the file (GM commands under the world lock).</summary>
    public MobStore? MobsIfLoaded => _shared.Mobs.IsValueCreated ? _shared.Mobs.Value : null;

    /// <summary>randomspawnbehaviors.xml (stage 5): the mob pools of the RANDOM spawn points.</summary>
    public SpawnBehaviors SpawnBehaviors => _shared.SpawnBehaviors.Value;

    /// <summary>loot.xml (stage 5c): the mobs' item drops; read with the first cluster that binds mobs.</summary>
    public LootTables Loot => _shared.Loot.Value;

    /// <summary>loot.xml when it has been read already, else null: a look-up that never reads the file (a kill under the world lock).</summary>
    public LootTables? LootIfLoaded => _shared.Loot.IsValueCreated ? _shared.Loot.Value : null;

    /// <summary>randomdungeons.xml and templatemetadata.xml (stageG dungeons): read with the first cluster that has exit markers.</summary>
    public RandomDungeonData Dungeons => _shared.Dungeons.Value;

    /// <summary>randomdungeons.xml when it has been read already, else null: a look-up that never reads the file (under the world lock).</summary>
    public RandomDungeonData? DungeonsIfLoaded => _shared.Dungeons.IsValueCreated ? _shared.Dungeons.Value : null;

    /// <summary>lootchests.xml (stageG dungeons, the chests): read with the first dungeon floor that has chest spawn points.</summary>
    public LootChestData LootChests => _shared.LootChests.Value;

    /// <summary>lootchests.xml when it has been read already, else null: a look-up that never reads the file (under the world lock).</summary>
    public LootChestData? LootChestsIfLoaded => _shared.LootChests.IsValueCreated ? _shared.LootChests.Value : null;

    /// <summary>localquests.xml (stageH bosses SPEC §4.4, the camp quests): read with the first cluster that has a localquestarea tile.</summary>
    public LocalQuestData LocalQuests => _shared.LocalQuests.Value;

    /// <summary>localquests.xml when it has been read already, else null: a look-up that never reads the file (under the world lock).</summary>
    public LocalQuestData? LocalQuestsIfLoaded => _shared.LocalQuests.IsValueCreated ? _shared.LocalQuests.Value : null;

    /// <summary>albionjournal.xml (ops-audit SPEC §3.4, the journal tracker): read alongside the first cluster (<see cref="LoadJournalAsync"/>).</summary>
    public JournalCatalog Journal => _shared.Journal.Value;

    /// <summary>albionjournal.xml when it has been read already, else null: a look-up that never reads the file (under the world lock).</summary>
    public JournalCatalog? JournalIfLoaded => _shared.Journal.IsValueCreated ? _shared.Journal.Value : null;

    /// <summary>personalseasons.xml (stageH journals SPEC §C, the Adventurer's Challenge): read with the journal (<see cref="LoadJournalAsync"/>).</summary>
    public PersonalSeasonData Seasons => _shared.Seasons.Value;

    /// <summary>personalseasons.xml when it has been read already, else null: a look-up that never reads the file (under the world lock).</summary>
    public PersonalSeasonData? SeasonsIfLoaded => _shared.Seasons.IsValueCreated ? _shared.Seasons.Value : null;

    /// <summary>expeditioncategories.xml, expeditionagents.xml and expeditions.xml (stageH expeditions SPEC §3.1, §4): read with the first cluster that has an agent or is an expedition.</summary>
    public ExpeditionData Expeditions => _shared.Expeditions.Value;

    /// <summary>The expedition tables when they have been read already, else null: a look-up that never reads the files (under the world lock).</summary>
    public ExpeditionData? ExpeditionsIfLoaded => _shared.Expeditions.IsValueCreated ? _shared.Expeditions.Value : null;

    /// <summary>worldsettings.xml &lt;islands&gt; (stageH islands SPEC §0.1): read with the first cluster that has an island merchant or is an island.</summary>
    public IslandData Islands => _shared.Islands.Value;

    /// <summary>The island types when they have been read already, else null: a look-up that never reads the file (under the world lock).</summary>
    public IslandData? IslandsIfLoaded => _shared.Islands.IsValueCreated ? _shared.Islands.Value : null;

    /// <summary>corrupteddungeons.xml (stageL corrupted SPEC §1.1): read with the first cluster that has a corrupted entrance (a Hell Den) or is a corrupted dungeon.</summary>
    public CorruptedData Corrupted => _shared.Corrupted.Value;

    /// <summary>corrupteddungeons.xml when it has been read already, else null: a look-up that never reads the file (under the world lock).</summary>
    public CorruptedData? CorruptedIfLoaded => _shared.Corrupted.IsValueCreated ? _shared.Corrupted.Value : null;

    /// <summary>hellgate.xml (stageL hellgates SPEC §2.1): read with the first cluster that has a hellgate shrine (a Hell Den) or is a hellgate.</summary>
    public HellgateData Hellgates => _shared.Hellgates.Value;

    /// <summary>hellgate.xml when it has been read already, else null: a look-up that never reads the file (under the world lock).</summary>
    public HellgateData? HellgatesIfLoaded => _shared.Hellgates.IsValueCreated ? _shared.Hellgates.Value : null;

    /// <summary>tunnelsystem.xml and gamedata.xml's ExitSettings (stageL roads SPEC §1.2): read with the road network's build (Systems/AvalonRoads, off the world thread).</summary>
    public RoadNetworkData Roads => _shared.Roads.Value;

    /// <summary>The road network tables when they have been read already, else null: a look-up that never reads the files (under the world lock).</summary>
    public RoadNetworkData? RoadsIfLoaded => _shared.Roads.IsValueCreated ? _shared.Roads.Value : null;

    /// <summary>mists.xml and miststemplatemetadata.xml (stageL mists SPEC §2): read with the first cluster that has exit markers (the mists portals) or a mists city entrance.</summary>
    public MistsData Mists => _shared.Mists.Value;

    /// <summary>The mists tables when they have been read already, else null: a look-up that never reads the files (under the world lock).</summary>
    public MistsData? MistsIfLoaded => _shared.Mists.IsValueCreated ? _shared.Mists.Value : null;

    /// <summary>buffshrines.xml (stageN shrines SPEC §1): read with the first cluster that places a buff shrine.</summary>
    public BuffShrineData BuffShrines => _shared.BuffShrines.Value;

    /// <summary>emotes.xml (stageN qol SPEC §1, 6 KB): read with the first player emote, on the world thread.</summary>
    public EmoteData Emotes => _shared.Emotes.Value;

    /// <summary>matchtypes.xml and orbtypes.xml (stageP league-pvp SPEC §2.5, §3.1, 3 KB): read with the first arena match, on the world thread.</summary>
    public MatchTypeData MatchTypes => _shared.MatchTypes.Value;

    /// <summary>arena_europe.bin (else arena.bin; stageP league-pvp SPEC §3.3, 7 KB): the categories and the season's ranks, read with the first ranked result.</summary>
    public ArenaData Arena => _shared.Arena.Value;

    /// <summary>
    /// A random dungeon floor or a mists zone from its layout (<see cref="ClusterTemplate.FromLayout"/>), not cached here (the dungeon or the
    /// zone keeps it): reads the template files the first time, so never under the world lock.
    /// </summary>
    public ClusterTemplate FromLayout(A80Layout layout) => ClusterTemplate.FromLayout(this, layout);

    /// <summary>Whether <paramref name="cluster"/> has been resolved; not after a failed try (the next <see cref="Get"/> tries again).</summary>
    public bool IsLoaded(string cluster) => _clusters.TryGetValue(cluster, out var lazy) && lazy.IsValueCreated;

    /// <summary>
    /// The resolved cluster <paramref name="cluster"/> (world.xml id), from the cache or read now (the first one also
    /// builds the shared indexes: a few seconds). Logs one line per cluster resolved. Not in world.xml, or a file
    /// missing or broken: <see cref="InvalidDataException"/> (an unreadable file: its IOException), not cached.
    /// </summary>
    public ClusterTemplate Get(string cluster) => _clusters.GetOrAdd(cluster, id => new Retrying<ClusterTemplate>(() =>
    {
        var watch = System.Diagnostics.Stopwatch.StartNew();
        var first = !_shared.Tiles.IsValueCreated;
        if (first) _ = LoadJournalAsync(); // alongside world.bin and tiles.bin, not after them
        var template = Resolve(id, Options.Session);
        watch.Stop();
        var indexes = first ? string.Create(CultureInfo.InvariantCulture,
            $" (with world.bin {World.LoadTime.TotalSeconds:0.00} s, tiles.bin {Tiles.LoadTime.TotalSeconds:0.00} s)") : "";
        Log.Info("data", string.Create(CultureInfo.InvariantCulture,
            $"cluster {id}: {template.Summary()} from {template.Instances.Count} template(s) in {watch.Elapsed.TotalSeconds:0.00} s{indexes}"));
        if (template.Resources is { } layout && (layout.Rolls.Count > 0 || layout.Preset is not null))
            Log.Info("data", $"cluster {id}: {layout.Summary()}"
                + (layout.Rolls.Count > 0 ? $"; dynamic templates {string.Join(", ", layout.Rolls.Values.Select(r => $"{r.Slot} {r.Template}"))}" : ""));
        if (template.Problems.Count > 0)
            Log.Warn("data", $"cluster {id}: {template.Problems.Count} kind(s) of object not resolved, first: {template.Problems[0]}");
        foreach (var line in template.MobBindingReport()) Log.Warn("world", $"map {id}: {line}"); // once per cluster, not per map (SPEC §5a.2)
        return template;
    })).Value;

    /// <summary>Whether a broken or missing albionjournal.bin has been warned about (once per process, every try reads it again).</summary>
    private int _journalWarned;

    /// <summary>Whether a broken or missing personalseasons.bin has been warned about (once per process).</summary>
    private int _seasonsWarned;

    /// <summary>
    /// <see cref="Journal"/> on a pool thread: started with the first cluster (so at SelectCharacter's prefetch, in parallel
    /// with world.bin), and again by the tracker when a Join came before it was in. A broken or missing albionjournal.bin
    /// gives null and is warned about once (the clusters do not need it); not kept, so the next call tries again.
    /// </summary>
    public Task<JournalCatalog?> LoadJournalAsync() => Task.Run<JournalCatalog?>(() =>
    {
        // personalseasons.bin with it (the season tracker; a broken one leaves the tracker empty, the journal still loads).
        try { _ = Seasons; }
        catch (Exception e) when (e is InvalidDataException or IOException)
        {
            if (Interlocked.Exchange(ref _seasonsWarned, 1) == 0) Log.Warn("data", $"personalseasons.bin: {e.Message}; the Adventurer's Challenge stays empty");
        }
        try { return Journal; }
        catch (Exception e) when (e is InvalidDataException or IOException)
        {
            if (Interlocked.Exchange(ref _journalWarned, 1) == 0)
                Log.Warn("data", $"albionjournal.bin: {e.Message}; the journal tracker gets no suggestions");
            return null;
        }
    });

    /// <summary><see cref="Get"/> on a pool thread.</summary>
    public Task<ClusterTemplate> LoadAsync(string cluster) => Task.Run(() => Get(cluster));

    /// <summary>
    /// Resolves <paramref name="cluster"/> without caching or logging it (the shared indexes and templates are still cached).
    /// With <paramref name="session"/> the dyn slots get their rolled templates and the resource nodes are placed (stage 4b);
    /// without (null: --check-content and the golden checks, which compare with world.xml's counts of the default
    /// templates) every slot keeps its default template and no layout is computed.
    /// </summary>
    public ClusterTemplate Resolve(string cluster, LayoutSession? session = null)
    {
        // StageL corrupted (SPEC §4.2 step 4): a variant id resolves its world.xml cluster with another cluster type and tier.
        if (ClusterVariant.Parse(cluster) is { } variant)
        {
            var of = World.Find(variant.World) ?? throw new InvalidDataException($"cluster {variant.World} of variant {cluster} is not in cluster/world.bin");
            return ClusterTemplate.Resolve(this, of with { Type = variant.Type }, session, variant.Tier);
        }
        var info = World.Find(cluster) ?? throw new InvalidDataException($"cluster {cluster} is not in cluster/world.bin");
        return ClusterTemplate.Resolve(this, info, session);
    }

    /// <summary>Templates parsed so far (found or not).</summary>
    public int TemplateCount => _shared.Layouts.Count;

    /// <summary>Drops the parsed templates (--check-content, between clusters, to bound its memory).</summary>
    public void ForgetTemplates() => _shared.Layouts.Clear();

    /// <summary>
    /// templates/&lt;folder&gt;/&lt;ref&gt;.template.bin, parsed once; null when the file is missing. The folder is the cluster
    /// name's (<see cref="ClusterName.TemplateFolder"/>) and nothing else: clusterdump.py falls back to the other folders,
    /// but in 345319 every one of the 15,384 template instances is in its own folder (--check-content).
    /// </summary>
    internal TemplateLayout? Layout(string folder, string reference)
    {
        var file = Path.Combine("templates", folder, reference + ".template.bin");
        return _shared.Layouts.GetOrAdd(folder + "/" + reference, _ => new Retrying<TemplateLayout?>(() =>
            File.Exists(Path.Combine(Folder, file)) ? TemplateLayout.Build(GameDataFile.Open(Folder, file).Root(), Tiles, folder, reference) : null)).Value;
    }
}

/// <summary>
/// StageL corrupted dungeons (SPEC D1, §4.2 step 4): a world.xml cluster resolved with another worldsettings cluster type and tier than
/// its own, as the store caches it under <see cref="Id"/> ("CORRUPT-001#T8#CORRUPTED_DUNGEON_PRO"): the mobs bind at <see cref="Tier"/>
/// with <see cref="Type"/>'s spawn blocks, and the danger type, PvP rules and chest tier follow them. The 20 corrupted maps all say
/// CORRUPTED_DUNGEON_INTERMEDIATE and T6 in world.xml; a Hunter run needs T5 and BEGINNER, a Slayer run T8 and PRO (at most 60 variants).
/// </summary>
public sealed record ClusterVariant(string World, int Tier, string Type)
{
    private const char Separator = '#';

    /// <summary>The store's key of the variant.</summary>
    public string Id => string.Create(CultureInfo.InvariantCulture, $"{World}{Separator}T{Tier}{Separator}{Type}");

    /// <summary>The variant of key <paramref name="id"/>; null for a plain cluster id (no world.xml id has a '#').</summary>
    public static ClusterVariant? Parse(string id)
    {
        var parts = id.Split(Separator);
        return parts.Length == 3 && parts[1].Length > 1 && parts[1][0] == 'T'
            && int.TryParse(parts[1][1..], NumberStyles.Integer, CultureInfo.InvariantCulture, out var tier) && parts[0].Length > 0 && parts[2].Length > 0
            ? new ClusterVariant(parts[0], tier, parts[2]) : null;
    }
}

/// <summary>
/// A <see cref="Lazy{T}"/> (one thread runs the factory, the others wait for it, the value is kept) that does not keep a
/// failure: Lazy's own mode would throw the first exception forever; here it reaches the callers of that run only and the
/// next caller runs the factory again.
/// </summary>
internal sealed class Retrying<T>(Func<T> factory)
{
    private Lazy<T> _lazy = new(factory);

    public bool IsValueCreated => Volatile.Read(ref _lazy).IsValueCreated;

    public T Value
    {
        get
        {
            var lazy = Volatile.Read(ref _lazy);
            try
            {
                return lazy.Value;
            }
            catch
            {
                Interlocked.CompareExchange(ref _lazy, new Lazy<T>(factory), lazy);
                throw;
            }
        }
    }
}

/// <summary>
/// Settings of the cluster resolver. <see cref="EventLayers"/>: the activelayer flags switched on (World:EventLayers;
/// halloween, christmas, easter, anniversary, anniversary_prelude); none by default, because what turns them on in the
/// client is open (SPEC §9). <see cref="BuildingIdOffset"/>: building type id = buildings.xml index + this
/// (ClientNumbering:BuildingIdOffset, SPEC §4). <see cref="Session"/>: the Login values the open-world layout is computed
/// with (stage 4b: dynamic templates and resource nodes), by default what our Login sends (<see cref="LayoutSession.Login"/>);
/// null = default templates and no resource nodes.
/// </summary>
public sealed record ClusterOptions
{
    public IReadOnlySet<string> EventLayers { get; init; } = new HashSet<string>();

    public int BuildingIdOffset { get; init; } = 1;

    public LayoutSession? Session { get; init; } = LayoutSession.Login;
}
