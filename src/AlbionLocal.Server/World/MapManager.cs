using AlbionLocal.Server.Entities;

namespace AlbionLocal.Server;

/// <summary>
/// The maps of the running server, one per cluster (or per character in the opening tutorial), created on entry
/// (ARCHITECTURE.md §4.2). A map without players is unloaded after World:MapIdleUnloadMinutes (<see cref="Unload"/>, from
/// <see cref="World.Update"/>; SPEC stage 3 §5); the next Join into its cluster creates a new one.
/// Touched only under <see cref="World.Sync"/>.
/// </summary>
public sealed class MapManager
{
    private readonly Dictionary<string, Map> _maps = new();

    public IReadOnlyCollection<Map> Maps => _maps.Values;

    /// <summary>Lookup by map InstanceKey (the cluster id for shared maps); null if not loaded.</summary>
    public Map? Find(string cluster) => _maps.GetValueOrDefault(cluster);

    /// <summary>The seed of a new map's <see cref="Map.Random"/> (<see cref="World.MapSeed"/>); null = a random one.</summary>
    public int? Seed { get; set; }

    /// <summary>The map of <paramref name="cluster"/>, created when nobody has entered it yet (Join, before the player is on it).</summary>
    public Map Get(string cluster, Guid? tutorialOwner = null)
    {
        var key = tutorialOwner is { } owner ? TutorialInstances.Key(cluster, owner) : cluster;
        if (!_maps.TryGetValue(key, out var map)) _maps[key] = map = new Map(cluster)
        { TutorialOwner = tutorialOwner, Random = Seed is { } seed ? new Random(seed) : new() };
        return map;
    }

    /// <summary>Puts a joined player on its selected map, or its shared cluster if none is supplied.</summary>
    public void Add(Player player, Map? map = null) => (map ?? Get(player.Character.Cluster)).Add(player);

    /// <summary>Takes the player off its map (the connection ended, or a cluster change); <paramref name="now"/> = <see cref="World.Clock"/> time.</summary>
    public void Remove(Player player, TimeSpan now) => player.Map?.Remove(player, now);

    /// <summary>
    /// Drops <paramref name="map"/> with its objects (their ids are never handed out again, ARCHITECTURE.md §4.1). Only a map
    /// without players: one with players stays. One with harvestables still regrowing (<see cref="Map.Regrowing"/>) stays
    /// too, until they are full: the next map would place them anew with full charges (<see cref="MapSpawner"/>), so a
    /// node emptied before a stay elsewhere would come back early (a WOOD_GIANTTREE T1 takes up to 11 min from 0 to 5).
    /// A node at 0 that does not regrow (respawntimeseconds 0) is full again on the next map. <paramref name="force"/>: also with
    /// harvestables regrowing (a closed dungeon's floor, whose id is never used again).
    /// </summary>
    public bool Unload(Map map, bool force = false)
    {
        if (map.Players.Count > 0 || map.Regrowing.Count > 0 && !force || !_maps.TryGetValue(map.InstanceKey, out var registered) || registered != map) return false;
        _maps.Remove(map.InstanceKey);
        map.Unloaded = true;
        return true;
    }
}
