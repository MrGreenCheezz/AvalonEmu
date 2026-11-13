using AlbionLocal.Server.Data;
using AlbionLocal.Server.Protocol;

namespace AlbionLocal.Server.Entities;

/// <summary>
/// The objects of an expedition instance (stageH expeditions SPEC §2.7): sent cluster-wide at the Join (the finish exit from the completion
/// on) and never left (the client's
/// expedition service ghj keeps lists of the exit and narrator views; a Leave would remove them).
/// </summary>
public abstract class ExpeditionObject(long id, ExpeditionObjectSpawn spawn) : WorldObject(id, spawn)
{
    public new ExpeditionObjectSpawn Spawn { get; } = spawn;
    public override string TypeName => Spawn.Tile;
    public override AnnounceRange Range => AnnounceRange.Cluster;
    public override bool LeaveOnRange => false;
}

/// <summary>
/// NewExpeditionExit (251, atq; cka.af 0x180B33930): key 3 the tile name "ExpeditionExit" (the client makes that prefab itself), key 4
/// spawnatstartup: true for the start exit, false for the finish exit, which the client shows closed until it sees the expedition
/// complete but keeps clickable (issue #29): the server sends the finish exit only at the completion (Expeditions.OpenFinishExit).
/// Used by ExitEnterStart (op 237, SPEC §2.10).
/// </summary>
public sealed class ExpeditionExit(long id, ExpeditionObjectSpawn spawn) : ExpeditionObject(id, spawn)
{
    public override string Kind => "expedition exit";

    /// <summary>The start exit (usable at once); false = the finish exit (usable after the completion).</summary>
    public bool Start => Spawn.SpawnAtStartup;

    public override IEnumerable<AlbionEvent> BuildCreate(Player viewer)
    {
        yield return new NewExpeditionExitEvent
        {
            czk = Id,           // [0]
            czl = Position,     // [1] the tile position
            czm = Direction,    // [2] its roty (0 in all data: left out)
            czn = Spawn.Tile,   // [3] "ExpeditionExit"
            czo = Start,        // [4] spawnatstartup
        };
    }
}

/// <summary>
/// NewExpeditionNarrator (252, atr; ckb.af): key 3 the tile name "ExpeditionNarrator" or "ExpeditionNarratorFinish", key 4 true for the
/// finish one (hidden until the completion). A click sends RegisterToObject (op 45), which gets no answer.
/// </summary>
public sealed class ExpeditionNarrator(long id, ExpeditionObjectSpawn spawn) : ExpeditionObject(id, spawn)
{
    public override string Kind => "expedition narrator";

    public bool Finish => Spawn.Tile.EndsWith("Finish", StringComparison.OrdinalIgnoreCase);

    public override IEnumerable<AlbionEvent> BuildCreate(Player viewer)
    {
        yield return new NewExpeditionNarratorEvent
        {
            czp = Id,           // [0]
            czq = Position,     // [1]
            czr = Direction,    // [2] the tile's roty (90 in the data)
            czs = Spawn.Tile,   // [3]
            czt = Finish,       // [4]
        };
    }
}

/// <summary>
/// NewExpeditionCheckPoint (315, atp; cjb.ao 0x180AA3F30): key 1 float[2] the exact tile position (the client binds the landscape tile with
/// the expeditioncheckpoint component there, |d| &lt; 0.001), key 2 the state: 0 not reached, 1 active (the respawn point), 2 superseded.
/// A later 315 with the same id changes the state.
/// </summary>
public sealed class ExpeditionCheckPoint(long id, ExpeditionObjectSpawn spawn) : ExpeditionObject(id, spawn)
{
    public override string Kind => "expedition checkpoint";

    public int State { get; set; }

    public override IEnumerable<AlbionEvent> BuildCreate(Player viewer)
    {
        yield return new NewExpeditionCheckPointEvent
        {
            czh = Id,                           // [0]
            czi = [Position.X, Position.Y],     // [1] float[] [x, z]
            czj = State,                        // [2]
        };
    }
}
