using AlbionLocal.Server.Data;
using AlbionLocal.Server.Entities;

namespace AlbionLocal.Server;

/// <summary>
/// One cluster of the world (world.xml id; instance 0 until dungeons and expeditions, ARCHITECTURE.md §4.2).
/// It holds the players in it and, once resolved, the cluster's client data (<see cref="Template"/>) with the world objects
/// <see cref="MapSpawner"/> made from it (step 12: buildings, exits, travel points, NPC spots, location markers, static
/// harvestables, and the resource nodes of the client's placement, step 14), each in its cell of the visibility grid
/// (<see cref="Grid"/>), and the harvestables that are regrowing (step 14). The mobs (stage 5a) are world objects too, with their spawn
/// points in <see cref="MobSlots"/> (<see cref="SpawnManager"/>); since 5d they walk, and their cell follows them
/// (<see cref="Relocate"/>). Touched only under <see cref="World.Sync"/>.
/// </summary>
public sealed class Map(string cluster)
{
    private readonly Dictionary<long, Player> _players = new();
    private readonly Dictionary<long, Player> _bots = new();
    private readonly Dictionary<long, WorldObject> _objects = new();
    private readonly List<WorldObject> _clusterWide = [];

    public string Cluster { get; } = cluster;

    /// <summary>Private opening-tutorial owner. Cluster remains the authored data id.</summary>
    public Guid? TutorialOwner { get; init; }
    public string InstanceKey => TutorialOwner is { } owner ? TutorialInstances.Key(Cluster, owner) : Cluster;

    /// <summary>The cluster's objects as the client data places them; null until the load (<see cref="World.LoadTemplate"/>) is in.</summary>
    public ClusterTemplate? Template { get; private set; }

    /// <summary>Whether <see cref="World.LoadTemplate"/> has started the load (false again after a failed one, so the next Join retries).</summary>
    public bool TemplateRequested { get; internal set; }

    /// <summary>The joined players, by object id.</summary>
    public IReadOnlyDictionary<long, Player> Players => _players;

    /// <summary>
    /// Stage I bots (analysis/work/stageI/bots/SPEC.md §1.2): the bots on the map, by object id, apart from <see cref="Players"/>, so that
    /// mobs, events, rewards, parties and every "for each player on the map" loop ignore them, and the map's emptiness and unload count
    /// humans only. The visibility, the movement relay and the per-player systems of the world tick take them too.
    /// </summary>
    public IReadOnlyDictionary<long, Player> Bots => _bots;

    /// <summary>The joined humans on the map (<see cref="Players"/>; the bots not counted).</summary>
    public int HumanCount => _players.Count;

    /// <summary>The player or bot with object id <paramref name="id"/> on the map; null when none (an AttackStart's target, a killer).</summary>
    public Player? FindPlayer(long id) => _players.GetValueOrDefault(id) ?? _bots.GetValueOrDefault(id);

    /// <summary>
    /// StageN companions: the units mobs fight on the map: the joined players, then the hired companions (<see cref="Player.IsCompanion"/>);
    /// the other bots never meet a mob.
    /// </summary>
    public IEnumerable<Player> Fighters => _bots.Count == 0 ? _players.Values : _players.Values.Concat(_bots.Values.Where(b => b.IsCompanion));

    /// <summary>The players, then the bots (stageJ pvp-spells SPEC §2.1): what the scans of a player's spells look at (areas, auras, radius nodes, chains).</summary>
    public IEnumerable<Player> Characters => _players.Values.Concat(_bots.Values);

    /// <summary>The world objects, by object id, in spawn order; empty until the template is in.</summary>
    public IReadOnlyDictionary<long, WorldObject> Objects => _objects;

    /// <summary>The objects every player on the map gets at Join (<see cref="AnnounceRange.Cluster"/>): the faction trader.</summary>
    public IReadOnlyList<WorldObject> ClusterWide => _clusterWide;

    /// <summary>The visibility grid on the cluster origin; null until the template is in.</summary>
    public CellGrid? Grid { get; private set; }

    /// <summary>UTC ticks when the map got its objects: the stamps of its buildings (NewBuilding keys 8, 20, 22).</summary>
    public long CreatedTicks { get; private set; }

    /// <summary>
    /// <see cref="World.Clock"/> time the last player left (a cluster change or a logout); null while players are on it.
    /// <see cref="World.Update"/> unloads the map once it has been empty for World:MapIdleUnloadMinutes (SPEC stage 3 §5)
    /// and none of its harvestables is regrowing (<see cref="MapManager.Unload"/>).
    /// </summary>
    public TimeSpan? EmptySince { get; internal set; }

    /// <summary>Taken out of <see cref="MapManager"/>: a cluster load that finishes now must not fill it (<see cref="World.LoadTemplate"/>).</summary>
    public bool Unloaded { get; internal set; }

    /// <summary>
    /// The map's random numbers (ARCHITECTURE.md §4.4): the regrowth factors of its harvestables (<see cref="Harvesting"/>), the
    /// rare states of its resource nodes, the mobs of its RANDOM spawn points and their respawn times (<see cref="SpawnManager"/>);
    /// seeded with <see cref="World.MapSeed"/> when that is set (the self-test), so the picks are reproducible.
    /// </summary>
    public Random Random { get; internal set; } = new();

    /// <summary>
    /// The harvestables below their full charges with a regrowth step pending (<see cref="Harvestable.RegrowAt"/>), stepped by
    /// <see cref="Harvesting.Regrow"/>; while it is not empty the map is not unloaded (<see cref="MapManager.Unload"/>).
    /// </summary>
    internal List<Harvestable> Regrowing { get; } = [];

    /// <summary>The resource nodes of the client's placement (stage 4b), in spawn order: whose rare state <see cref="ResourceNodes"/> rolls.</summary>
    internal List<Harvestable> ResourceNodes { get; } = [];

    /// <summary>The fishing zones of the map (stageG fishing; Systems/Fishing picks and regrows them).</summary>
    public IReadOnlyList<FishingZone> FishingZones => _fishingZones;

    private readonly List<FishingZone> _fishingZones = [];

    /// <summary>The rare state weights by tier of the cluster's distribution; null = no node is enchanted.</summary>
    internal Func<int, IReadOnlyList<(byte State, int Weight)>>? RareWeights { get; set; }

    /// <summary><see cref="World.Clock"/> time of the next rare state roll, and the time between two (checkintervall).</summary>
    internal TimeSpan? NextRareRoll { get; set; }

    internal TimeSpan? RareInterval { get; set; }

    /// <summary>The mob spawn points of the cluster with their mob or respawn time (stage 5a, <see cref="SpawnManager"/>).</summary>
    internal List<MobSpawnSlot> MobSlots { get; } = [];

    /// <summary>
    /// A random dungeon floor's level (stageG dungeons SPEC §7: DifficultyLevels default, level 1-4 of the exit): its factors on
    /// the mobs' fame, silver and loot, with the Rates; null = 1 (every other map, and level 0).
    /// </summary>
    public DungeonDifficulty? Difficulty { get; internal set; }

    /// <summary>Whether a killed mob's spawn point never respawns (an expedition instance: clustertype mobspawns defaulttimefactor 9999).</summary>
    public bool NoRespawn { get; internal set; }

    /// <summary>
    /// StageL corrupted (SPEC §1.2): the spawn behaviours whose points the map's spawning leaves out, because a system places their mobs
    /// itself (a corrupted dungeon's demon, shards and anti-reset imps); empty on every other map.
    /// </summary>
    public IReadOnlySet<string> ScriptedSpawns { get; internal set; } = new HashSet<string>();

    /// <summary>
    /// StageH expeditions (GroupScaling, SPEC §12.1): factors on the max health and on the damage of the mobs the map's spawn points
    /// make (<see cref="Entities.Mob.HealthFactor"/>, <see cref="Entities.Mob.DamageFactor"/>); 1 on every other map.
    /// </summary>
    public double MobHealthFactor { get; internal set; } = 1;

    public double MobDamageFactor { get; internal set; } = 1;

    /// <summary>The map's timed actions (stage 5b/5c: casts, hits, damage over time, corpses, loot bags), run by the world tick.</summary>
    public MapTimers Timers { get; } = new();

    /// <summary>
    /// The players whose clients know <paramref name="o"/> (got its New* event and no Leave since): who gets an event about a
    /// world object (stage 5 SPEC conventions; ARCHITECTURE.md §4.3 "Map.SendToObservers").
    /// </summary>
    public IEnumerable<Player> ObserversOf(WorldObject o) => _players.Values.Where(p => p.Visibility.Knows(o));

    /// <summary><paramref name="e"/> to every player whose client knows <paramref name="o"/>.</summary>
    public void SendToObservers(WorldObject o, AlbionLocal.Server.Protocol.AlbionEvent e)
    {
        foreach (var p in ObserversOf(o)) p.Session.Send(e);
    }

    /// <summary>Spawn keys handed out to GM mobs on this map ("&lt;cluster&gt;:custom:&lt;n&gt;").</summary>
    internal int CustomSpawns { get; set; }

    /// <summary>Spawn keys handed out to the corpses of killed mobs on this map ("&lt;cluster&gt;:corpse:&lt;n&gt;", <see cref="AlbionLocal.Server.Corpses"/>).</summary>
    internal int CorpseCount { get; set; }

    /// <summary>
    /// Puts an object made after the map got its template on it (a mob spawned or respawned, stage 5): into the objects and
    /// its grid cell, and to every player whose window holds that cell (<see cref="VisibilityTracker.Show"/>).
    /// </summary>
    internal void AddObject(WorldObject o)
    {
        if (Grid is null) throw new InvalidOperationException($"map {Cluster} has no template yet");
        o.Map = this;
        _objects.Add(o.Id, o);
        Grid.Add(o);
        foreach (var player in _players.Values) player.Visibility.Show(o);
        if (o is Mob { World: { } world } mob) world.Plugins.MobSpawned(mob); // the plugins' MobSpawned
    }

    /// <summary>
    /// A moving object (a walking mob, stage 5d) may have left its grid cell: when it has, it goes into its new cell and
    /// every player's window is tested against it (<see cref="VisibilityTracker.Moved"/>: NewMob on entering, Leave at
    /// Visibility:LeaveCells).
    /// </summary>
    internal void Relocate(WorldObject o)
    {
        if (Grid is not { } grid || o.Map != this || !_objects.ContainsKey(o.Id) || grid.CellOf(o.Position) == o.Cell) return;
        grid.Remove(o);
        grid.Add(o);
        foreach (var player in _players.Values) player.Visibility.Moved(o);
    }

    /// <summary>Takes <paramref name="o"/> off the map: every client that knows it gets a Leave (<see cref="VisibilityTracker.Forget"/>).</summary>
    internal void RemoveObject(WorldObject o)
    {
        if (!_objects.Remove(o.Id)) return;
        Grid?.Remove(o);
        foreach (var player in _players.Values) player.Visibility.Forget(o);
    }

    internal void Add(Player player)
    {
        _players.Add(player.Id, player);
        player.Map = this;
        EmptySince = null;
    }

    /// <summary>A bot onto the map (<see cref="Bots"/>); the map's <see cref="EmptySince"/> is left alone.</summary>
    internal void AddBot(Player bot)
    {
        _bots.Add(bot.Id, bot);
        bot.Map = this;
    }

    /// <summary>A bot off the map.</summary>
    internal void RemoveBot(Player bot)
    {
        if (_bots.Remove(bot.Id)) bot.Map = null;
    }

    internal void Remove(Player player, TimeSpan now)
    {
        if (!_players.Remove(player.Id)) return;
        player.Map = null;
        if (_players.Count == 0) EmptySince = now;
    }

    /// <summary>
    /// Takes the resolved cluster and the objects made from it (<see cref="MapSpawner.Populate"/>), once: the grid is laid
    /// on the template's origin with <paramref name="cellSize"/>, every object goes into its cell.
    /// </summary>
    internal void Populate(ClusterTemplate template, IReadOnlyList<WorldObject> objects, float cellSize, long createdTicks)
    {
        if (Template is not null) throw new InvalidOperationException($"map {Cluster} has its objects already");
        Template = template;
        CreatedTicks = createdTicks;
        Grid = new CellGrid(template.Origin, cellSize);
        foreach (var o in objects)
        {
            o.Map = this;
            _objects.Add(o.Id, o);
            if (o is Harvestable { IsResourceNode: true } node) ResourceNodes.Add(node);
            if (o is FishingZone zone) _fishingZones.Add(zone);
            if (o.Range == AnnounceRange.Cluster) _clusterWide.Add(o);
            else Grid.Add(o);
        }
    }
}
