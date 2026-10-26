using AlbionLocal.Photon;
using AlbionLocal.Server.Data;
using AlbionLocal.Server.Protocol;

namespace AlbionLocal.Server.Network;

/// <summary>The world map's cluster markers (analysis/work/stageH/ops-audit/SPEC.md §2, phase 1).</summary>
public sealed partial class WorldSession
{
    /// <summary>
    /// SubscribeToCluster (op 141, abl: 0 string[] cluster ids). Not a subscription but a pull with a client-side cache
    /// (Worldmap.UpdateVisibleClusters 0x180e86420, fw3.a 0x181be82b0): the open world map asks for the clusters in view that
    /// it has no fresh ClusterInfoUpdate of, [] when it closes (Worldmap.OnDisable 0x180e828c0), and the same ids again
    /// after a few minutes. Officially there is no response, one event ClusterInfoUpdate (139) per id within 0.14 s; here
    /// the same, every time it is asked, from <see cref="ClusterInfo"/>. An id left unanswered (not in world.xml, or a
    /// territory cluster) just stays pending on the client and is not asked again.
    /// </summary>
    [Handles(OperationCodes.SubscribeToCluster, Status = SessionStatus.InWorld)]
    private AlbionResponse? SubscribeToCluster(SubscribeToClusterRequest req)
    {
        if (req.agl is not { Length: > 0 } ids) return null;
        // Read with the cluster the player joined: never loaded here under the world lock.
        if (world.Clusters?.WorldIfLoaded is not { } info)
        {
            Log.Debug(Name, $"SubscribeToCluster: world.xml not loaded, {ids.Length} cluster(s) left unanswered");
            return null;
        }
        var sent = 0;
        foreach (var id in ids.Distinct(StringComparer.Ordinal))
            if (ClusterInfo(info, id, world.StaticDungeons, world.Territories) is { } ev)
            {
                SendEvent(ev);
                sent++;
            }
        Log.Debug(Name, $"SubscribeToCluster: {sent} of {ids.Length} cluster(s) answered");
        return null;
    }

    /// <summary>
    /// The phase-1 ClusterInfoUpdate (al5) of <paramref name="id"/>, built by hand in the official plain shape (SPEC §2) and
    /// never with FillEmpty: empty non-null dictionaries 45/47 make WorldmapCluster.UpdateClusterValue 0x181be0840 throw on a
    /// missing "silver" entry. Key 0 the id; 2, 3, 58 ""; 34 and 57 -1 ("none"), the other scalars 0 (left out); the hideout
    /// (20-23) and treasure (51-56) arrays empty; the static dungeon entrances (65-68, stageN static-dungeons SPEC §3,
    /// WorldmapCluster.UpdateStaticDungeonInfos 0x181be2930): per dungeon with an entrance exit here (key = staticdungeonid, ae+0x70)
    /// whose event runs, its id (65), end (66) and rarity (67), parallel; 68 the value of every such dungeon by id string ("D"), left out
    /// for a cluster without entrances or without <paramref name="dungeons"/> (the client reads a missing one as HIDDEN); per world.xml outpost one entry in 19 (ContestedState)
    /// and 27 and two in 28, since the outpost loop of WorldmapCluster.UpdateCluster 0x181be11c0 indexes 19 and 27 per outpost
    /// of its own client data (no outpost: 19 empty, 27 and 28 left out, as official). Left out (null): 1, 4-18 (the territory
    /// reader indexes them without a null check), 31, 35, 37-39, 41-47 (44-47 the cluster value), 49, 50, 59-64. Null for an id not in world.xml
    /// and for a cluster with territories: there is no official sample of those, and UpdateTerritories 0x181be2e30 would
    /// index 4-18 per territory. With Territories:WorldMapInfo (stageP territories SPEC §6 P1, off by default in both presets) a territory cluster
    /// is answered too, keys 4-18 one entry per territory (<see cref="Systems.Territories.FillWorldMap"/>).
    /// </summary>
    public static ClusterInfoUpdateEvent? ClusterInfo(WorldInfo world, string id, Systems.StaticDungeons? dungeons = null, Systems.Territories? territories = null)
    {
        if (world.Find(id) is not { } cluster) return null;
        var territory = cluster.Territories > 0;
        if (territory && territories is null) return null;
        var outposts = cluster.Outposts.Count;
        var entered = dungeons?.EnteredFrom(cluster).ToList() ?? [];
        var running = entered.Select(d => (Dungeon: d, Event: dungeons!.Current(d))).Where(x => x.Event is not null).ToList();
        var e = new ClusterInfoUpdateEvent
        {
            bx9 = id, byb = "", byc = "", bzv = "",
            by7 = -1, bzu = -1,
            bys = new int[outposts],                          // [19] ContestedState per outpost (0)
            by0 = outposts > 0 ? new byte[outposts] : null,   // [27] per outpost (official: the cluster's key 32, 0 here)
            by1 = outposts > 0 ? new bool[2 * outposts] : null, // [28] two per outpost
            byt = [], byu = [], byv = [], byw = [],             // [20]-[23] hideouts
            bzo = [], bzp = [], bzq = [], bzr = [], bzs = [], bzt = [], // [51]-[56] treasure markers, parallel
            bz2 = [.. running.Select(x => x.Dungeon.Id)],         // [65]-[67] static dungeon events running: id, end, rarity
            bz3 = [.. running.Select(x => x.Event!.Value.End)],
            bz4 = [.. running.Select(x => x.Event!.Value.Def.Rarity)],
            bz5 = entered.Count > 0 ? entered.ToDictionary(d => d.Id.ToString(), d => dungeons!.Value(d)) : null, // [68] value per dungeon
        };
        return !territory || territories!.FillWorldMap(e, cluster) ? e : null;
    }
}
