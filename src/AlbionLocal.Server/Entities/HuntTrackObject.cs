using System.Numerics;
using System.Xml.Linq;
using AlbionLocal.Server.Data;
using AlbionLocal.Server.Protocol;

namespace AlbionLocal.Server.Entities;

/// <summary>The client data a hunt track is made from: none, it is the server's (a track tile of dynamichunt.xml at a chosen point).</summary>
public sealed record HuntTrackSpawn : ClusterObject;

/// <summary>
/// A hunt track (stageN hunts, analysis/work/stageN/hunts/SPEC.md §2): a footprint the tracking toolkit finds, or a mark of a running
/// hunt's trail. NewHuntTrack (558, a24 → cn9.af 0x180B8AF80): 0 id, 1 position, 2 rotation, 3 the target's tracktileref (the client
/// looks the tile up by name), 4 used (AlreadyUsed), 5 the hunt Guid it belongs to (empty: a wild track anyone may read; another hunt's
/// than the viewer's: NotUseable), 6 target index (byq), 7 hunt index (byk), 8 short (≥ 1, kept 1), 9 short (0 → the client's 20).
/// Reading it is ops 499/500 (<see cref="Systems.Hunts"/>); HuntTrackUsed (561) greys it. Left when out of range.
/// </summary>
public sealed class HuntTrackObject(long id, string cluster, Vector2 position, float direction, HuntTarget target, HuntDefinition hunt, Guid huntId)
    : WorldObject(id, new HuntTrackSpawn
    {
        Key = new SpawnKey(cluster, "hunt", (int)(id & int.MaxValue)), Tile = target.TrackTile, Position = position, Direction = direction,
        Component = new XElement("hunttrack"),
    })
{
    public HuntTarget Target { get; } = target;

    public HuntDefinition Hunt { get; } = hunt;

    /// <summary>The hunt it is a trail mark of; <see cref="Guid.Empty"/>: a wild track that starts a hunt.</summary>
    public Guid HuntId { get; } = huntId;

    /// <summary>Read (or a mere footprint of the trail): AlreadyUsed, nobody can read it.</summary>
    public bool Used { get; set; }

    /// <summary>When a wild track goes (World.Now); null: with its hunt.</summary>
    public TimeSpan? Expires { get; init; }

    public override string Kind => "hunt track";

    public override string TypeName => $"{Target.Name} ({Hunt.Name}{(HuntId == Guid.Empty ? "" : ", hunt " + HuntId.ToString("N")[..8])}{(Used ? ", used" : "")})";

    public override bool LeaveOnRange => true;

    public NewHuntTrackEvent Build() => new()
    {
        eaw = Id, eax = Position, eay = Direction, eaz = Target.TrackTile, ea0 = Used, ea1 = HuntId, ea2 = Target.Index, ea3 = Hunt.Index, ea4 = 1, ea5 = 0,
    };

    public override IEnumerable<AlbionEvent> BuildCreate(Player viewer)
    {
        yield return Build();
    }
}
