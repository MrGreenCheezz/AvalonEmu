using System.Numerics;
using AlbionLocal.Server.Data;
using AlbionLocal.Server.Protocol;

namespace AlbionLocal.Server.Entities;

/// <summary>HellDungeonStaticEntranceState (Albion.Common.GameData.HellDungeons): Open, Closed.</summary>
public enum AbyssEntranceState : byte
{
    Open = 0,
    Closed = 1,
}

/// <summary>
/// A Hell Den's Abyssal Depths entrance (analysis/work/stageR/abyss/SPEC.md §2.1): the critical member helldungeonstaticentrance_01 of the six
/// "Antiquarian's Den" clusters as NewHellDungeonStaticEntrance (638, a2v → cnz; handler cjb.a 0x180AA1C40): {0 id, 1 its criticalObjects Guid,
/// 2 position (the client binds the landscape tile with the helldungeonstaticentrance component there: none, no entrance), 3 rotation,
/// 4 HellDungeonStaticEntranceState, 5 GameTimeStamp of the next state change [infer: the join module counts down to it on its Enter/Closed
/// button]}. A change goes out as UpdateHellDungeonStaticEntranceState (639: {0 id, 1 state, 2 the stamp}; cnz.ajv 0x180B86430). Landscape-bound:
/// never left, sent again on re-entry.
/// </summary>
public sealed class AbyssEntrance(long id, CriticalMember spawn) : WorldObject(id, spawn)
{
    public CriticalMember Member { get; } = spawn;

    public AbyssEntranceState State { get; set; } = AbyssEntranceState.Open;

    /// <summary>GameTimeStamp of the next change (key 5).</summary>
    public long NextChange { get; set; }

    /// <summary>World clock of the next change.</summary>
    public TimeSpan ChangeAt { get; set; }

    public override string Kind => "abyssal depths entrance";
    public override string TypeName => $"{Member.Tile} ({State})";
    public override bool LeaveOnRange => false;

    public override IEnumerable<AlbionEvent> BuildCreate(Player viewer)
    {
        yield return new NewHellDungeonStaticEntranceEvent
        {
            d9z = Id,                           // [0]
            d90 = Member.Guid ?? Guid.Empty,    // [1]
            d91 = Position,                     // [2] the tile's position (the client's landscape binding)
            d92 = Direction,                    // [3]
            d93 = (byte)State,                  // [4]
            d94 = NextChange,                   // [5]
        };
    }

    public UpdateHellDungeonStaticEntranceStateEvent Update() => new() { ddn = Id, d3x = (byte)State, d3y = NextChange };
}

/// <summary>HellDungeonDownwardExitState: Open, Closed, Broken.</summary>
public enum AbyssDownwardState
{
    Open = 0,
    Closed = 1,
    Broken = 2,
}

/// <summary>
/// An exit of an Abyssal Depths floor (SPEC §2.3), a tile with its component in the floor's templates, bound by the client to the landscape tile
/// with that component at key 1's position (cjb.a 0x180A9FB60, else no object): {0 id, 1 position, 2 the target cluster (cnw.af(string) 0x180B851D0:
/// a world.xml cluster id it looks up for its tooltip; an instance id finds nothing and leaves the target empty)} and per kind:
/// <list type="bullet">
/// <item>Upward (633, a2w → cn0; cjb.f): 3 GameTimeStamp from which it can be used (cn0.abn 0x180B7A620: usable while it is in the past).</item>
/// <item>Downward (635, a2s → cnu; cjb.e): 3 float rotation (cnu's ctor), 4 GameTimeStamp, 5 HellDungeonDownwardExitState.</item>
/// <item>Soul (634, a2u → cnx; cjb.c, the component "helldungeosoulexit" as tiles.xml spells it): 3 GameTimeStamp, 4 HellDungeonSoulExitState.</item>
/// </list>
/// The upward and downward views' EnterExit (0x181A536C0, 0x18128B370) send ChangeCluster {0: this id} (cyd.u with HandleChangeClusterResponse),
/// answered by Handlers/World/AbyssHandler. The soul exit's EnterExit does nothing (its use goes through the soul carriable: not traced).
/// </summary>
public sealed class AbyssExit(long id, MistsSpot spawn, string target) : WorldObject(id, spawn)
{
    public MistsSpot Spot { get; } = spawn;

    /// <summary>helldungeonupwardexit, helldungeondownwardexit or helldungeosoulexit.</summary>
    public string ExitKind => Spot.Kind;

    public bool Upward => ExitKind == "helldungeonupwardexit";
    public bool Downward => ExitKind == "helldungeondownwardexit";

    /// <summary>Key 2: the world.xml cluster the floor was entered from.</summary>
    public string Target { get; } = target;

    /// <summary>A downward exit's state (Open, Closed); a soul exit's (0 Open, 1 Closed).</summary>
    public int State { get; set; }

    /// <summary>GameTimeStamp key 3 (upward, soul) / 4 (downward).</summary>
    public long Stamp { get; set; }

    /// <summary>The trigger square (the component's size: 8 × 8 up, 7 × 7 down and soul).</summary>
    public Vector2 Size => TileMath.V2(Spot.Attr("size")) ?? new Vector2(7);

    public override string Kind => "abyssal depths exit";
    public override string TypeName => $"{ExitKind} {State}";
    public override bool LeaveOnRange => false;

    public override IEnumerable<AlbionEvent> BuildCreate(Player viewer)
    {
        if (Upward) yield return new NewHellDungeonUpwardExitEvent { d9u = Id, d9v = Position, d9w = Target, d95 = Stamp };
        else if (Downward) yield return new NewHellDungeonDownwardExitEvent { d9u = Id, d9v = Position, d9w = Target, d9r = Direction, d9s = Stamp, d9t = State };
        else yield return new NewHellDungeonSoulExitEvent { d9u = Id, d9v = Position, d9w = Target, d9x = Stamp, d9y = State };
    }
}

/// <summary>
/// A resurrection shrine of an Abyssal Depths floor (SPEC §2.4; the resurrectionshrine tile HIGHLAND_DEAD_DNG_HELL_RESSHRINE_A): NewResurrectionShrine
/// (627, a2x → cn1; cjb.g 0x180AADAC0) {0 id, 1 position, 2 rotation, 3 the tile (cn1's ctor looks it up in the scene: "HellDungeonShrine: Tile not
/// found" otherwise), 4 ResurrectionShrineState Active}. Its use (cn1.af(b4q, b4s): a carried soul) is not traced: it only stands.
/// </summary>
public sealed class AbyssResurrectionShrine(long id, MistsSpot spawn) : WorldObject(id, spawn)
{
    public MistsSpot Spot { get; } = spawn;

    public override string Kind => "resurrection shrine";
    public override string TypeName => Spot.Tile;
    public override bool LeaveOnRange => false;

    public override IEnumerable<AlbionEvent> BuildCreate(Player viewer)
    {
        yield return new NewResurrectionShrineEvent { d96 = Id, d97 = Position, d98 = Direction, d99 = Spot.Tile, eaa = 0 };
    }
}
