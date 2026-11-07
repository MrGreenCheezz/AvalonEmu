using AlbionLocal.Server.Data;
using AlbionLocal.Server.Protocol;

namespace AlbionLocal.Server.Entities;

/// <summary>
/// StageQ: a faction warfare tower (NewOutpostObject 365, client aul; analysis/work/stageQ/outposts): cluster-wide, so every player of the map
/// gets it in the Join's phase B with its OutpostUpdate (366) right after it (captured: 3 towers × 2 Joins in 1207, at any distance), and it is
/// never left while the player stays. Its state is <see cref="Systems.Outposts.State"/>'s, which outlives the map.
/// </summary>
public sealed class OutpostObject(long id, OutpostSpawn spawn, Systems.Outposts.State state) : WorldObject(id, spawn)
{
    public new OutpostSpawn Spawn { get; } = spawn;

    /// <summary>Its owner, claim and contest, kept by the world (<see cref="Systems.Outposts"/>).</summary>
    public Systems.Outposts.State State { get; } = state;

    public override string Kind => "outpost";
    public override string TypeName => Spawn.Type;
    public override AnnounceRange Range => AnnounceRange.Cluster;
    public override bool LeaveOnRange => false;

    public override IEnumerable<AlbionEvent> BuildCreate(Player viewer)
    {
        yield return new NewOutpostObjectEvent
        {
            c7w = Id,           // [0]
            c7x = Spawn.Tile,   // [1] FOREST_GREEN_FACTION_WARFARE_OUTPOST_TOWER
            c7y = Spawn.Type,   // [2] OUTPOST_SAFE_3: the tower's state layer
            c7z = Position,     // [3] the tower tile = world.xml outpost pos
        };
        yield return State.BuildUpdate(Id);
    }
}
