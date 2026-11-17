using AlbionLocal.Server.Data;
using AlbionLocal.Server.Protocol;

namespace AlbionLocal.Server.Entities;

/// <summary>
/// StageR fw-zone: a faction fortress tower (NewFactionFortressObject 652, client aoq → ckc; analysis/work/stageR/fw-zone/SPEC.md §1). The client
/// (cjb.a2 0x180a9e670) binds it to the tile with a factionfortress component at key 1 and looks its fortress up by key 3; a 652 for an id it
/// already knows updates that object (ckc.af(aoq) 0x180b34c20), which is how a state change reaches it. Cluster-wide like the outposts [infer].
/// </summary>
public sealed class FortressObject(long id, FortressSpawn spawn, Systems.FactionFortresses.State state) : WorldObject(id, spawn)
{
    public new FortressSpawn Spawn { get; } = spawn;

    public Systems.FactionFortresses.State State { get; } = state;

    public override string Kind => "faction fortress";
    public override string TypeName => Spawn.Tile;
    public override AnnounceRange Range => AnnounceRange.Cluster;
    public override bool LeaveOnRange => false;

    public override IEnumerable<AlbionEvent> BuildCreate(Player viewer)
    {
        yield return State.BuildNew(Id, Spawn);
    }
}
