using AlbionLocal.Server.Data;
using AlbionLocal.Server.Protocol;

namespace AlbionLocal.Server.Entities;

/// <summary>The spawn record of a dragon area entrance: no template object, its tile placed by the event (key 4).</summary>
public sealed record DragonAreaEntranceSpawn : ClusterObject;

/// <summary>DragonAreaEntranceState (Albion.Common.GameData.DragonArea): Protected, Open, Closed.</summary>
public enum DragonAreaEntranceState
{
    Protected = 0,
    Open = 1,
    Closed = 2,
}

/// <summary>
/// A dragon area entrance in the open world (NewDragonAreaEntrance 694, a3x → dff; handler cjb.j 0x180AAE4D0; analysis/work/stageR/dragon-areas/SPEC.md
/// §2): {0 id, 1 the entrance type's index in dragonarea.xml, 2 position, 3 rotation, 4 the tile (tileref, placed by the client: a9f.u/a9f.p in dff's
/// ctor; an unknown one logs "DragonAreaEntranceObject: Tile not found"), 5 DragonAreaEntranceState, 6 a GameTimeStamp [infer: the end of the
/// state]}. The client only lets the player interact while it is Open (dff.aqv 0x180AF5220).
/// </summary>
public sealed class DragonAreaEntrance(long id, DragonAreaEntranceSpawn spawn, DragonEntranceType type, int typeIndex) : WorldObject(id, spawn)
{
    public DragonEntranceType Type { get; } = type;
    public int TypeIndex { get; } = typeIndex;

    public DragonAreaEntranceState State { get; set; } = DragonAreaEntranceState.Open;

    /// <summary>GameTimeStamp: when the current state ends (key 6).</summary>
    public long StateEnd { get; set; }

    /// <summary>World clock: when the entrance goes (removed from the map).</summary>
    public TimeSpan RemoveAt { get; set; }

    /// <summary>The treasuredistribution event and its spots it was drawn by (null: the GM's), for DragonAreas:RedrawMinutes.</summary>
    public (DragonEntranceEvent Event, IReadOnlyList<(System.Numerics.Vector2 Position, float Direction)> Spots)? Origin { get; set; }

    /// <summary>World clock: when an Open entrance closes.</summary>
    public TimeSpan CloseAt { get; set; }

    public override string Kind => "dragon area entrance";
    public override string TypeName => Type.Name;
    public override bool LeaveOnRange => true;

    public NewDragonAreaEntranceEvent Event() => new()
    {
        edu = Id,                 // [0]
        edv = TypeIndex,          // [1] bzm.abo(int): the entrance type
        edw = Position,           // [2]
        edx = Direction,          // [3]
        edy = Type.Tile,          // [4] the tile to place
        edz = (int)State,         // [5]
        ed0 = StateEnd,           // [6]
    };

    public override IEnumerable<AlbionEvent> BuildCreate(Player viewer)
    {
        yield return Event();
    }
}

/// <summary>DragonAreaExitState (Albion.Common.GameData.DragonArea): Active, Inactive.</summary>
public enum DragonAreaExitState : byte
{
    Active = 0,
    Inactive = 1,
}

/// <summary>
/// A way out of a dragon area instance (NewDragonAreaExit 695, a3y → coa; handler cjb.i 0x180AAE260; SPEC §2): a critical member of type
/// dragonareareexit of the master DRAGON_001 (8 per mode layer and one without a layer; size 7 × 7). {0 id, 1 the member's position (the client binds
/// the dragonareareexit tile there), 2 the target cluster (coa.af(a3y) 0x180B94E10 resolves it with a0.abs as a world.xml cluster id and builds the
/// exit's target from it: the cluster the entrance stood in), 3 DragonAreaExitState (coa.aqt: usable while Active)}. Its type AlwaysActive stands
/// Active; MainQuestLocked stays Inactive while the main quest is not done here [infer: the quest is not run]. The client channels on it
/// (DragonAreaExitObjectView.EnterExit 0x181288390: a local channel, then ExitEnterStart 237 with the exit's id).
/// </summary>
public sealed class DragonAreaExit(long id, CriticalMember spawn, string target) : WorldObject(id, spawn)
{
    public CriticalMember Member { get; } = spawn;

    /// <summary>The world.xml cluster it leads to (key 2).</summary>
    public string Target { get; } = target;

    public DragonAreaExitState State { get; set; } = spawn.Attr("type") == "MainQuestLocked" ? DragonAreaExitState.Inactive : DragonAreaExitState.Active;

    /// <summary>The trigger square (dragonareareexit size, 7 × 7).</summary>
    public System.Numerics.Vector2 Size => Data.TileMath.V2(Member.Attr("size")) ?? new System.Numerics.Vector2(7);

    public override string Kind => "dragon area exit";
    public override string TypeName => Member.Member;
    public override bool LeaveOnRange => false;

    public override IEnumerable<AlbionEvent> BuildCreate(Player viewer)
    {
        yield return new NewDragonAreaExitEvent
        {
            ed1 = Id,             // [0]
            ed2 = Position,       // [1] the dragonareareexit tile's position
            ed3 = Target,         // [2] the target cluster (world.xml id)
            ed4 = (byte)State,    // [3]
        };
    }
}
