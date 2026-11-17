namespace AlbionLocal.Modules;

/// <summary>Maps being built and dropped. Id = the cluster.</summary>
public interface IMapHooks
{
    /// <summary>A map was built: its objects and mobs are on it. A cluster's map comes again after an unload, and an instance (a dungeon floor, an expedition) has a cluster of its own. Id = the cluster.</summary>
    IHookRegistration OnLoaded(Action<MapArgs> handler, HookPriority priority = HookPriority.Normal);
    /// <summary>As OnLoaded, for one cluster (its world.xml id such as "0006", or a mask with * and ?).</summary>
    IHookRegistration OnLoaded(string cluster, Action<MapArgs> handler, HookPriority priority = HookPriority.Normal);

    /// <summary>A built map was dropped (empty for a while, or its instance closed): it is gone from <see cref="IServer.LoadedClusters"/>. Id = the cluster.</summary>
    IHookRegistration OnUnloaded(Action<MapArgs> handler, HookPriority priority = HookPriority.Normal);
    /// <summary>As OnUnloaded, for one cluster (its world.xml id such as "0006", or a mask with * and ?).</summary>
    IHookRegistration OnUnloaded(string cluster, Action<MapArgs> handler, HookPriority priority = HookPriority.Normal);
}

/// <summary>A map was built or dropped (OnLoaded, OnUnloaded).</summary>
public sealed class MapArgs(ZoneInfo zone, string instance)
{
    /// <summary>The zone: its cluster id, kind, colour, PvP rules.</summary>
    public ZoneInfo Zone { get; } = zone;

    /// <summary>What <see cref="IServer.Mobs"/> and the like find the map by: the cluster, or a key of its own for a private map (the opening tutorial's).</summary>
    public string Instance { get; } = instance;
}
