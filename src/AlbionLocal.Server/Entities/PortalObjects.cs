using System.Numerics;
using AlbionLocal.Server.Data;
using AlbionLocal.Server.Protocol;

namespace AlbionLocal.Server.Entities;

// The city portals into the Outlands (analysis/work/portals/SPEC.md, "SPEC" below): the Realmgate in a royal city's Conquerors'
// Hall and the portal of its Outlands portal town. Both tiles are placed by the client with the landscape; the event binds to the
// tile at the exact position (cjb.a(pos, ["portalentrance"]) / ["portalexit"], as NewExit) and makes it usable. Never left.

/// <summary>
/// A hall's Realmgate (NewPortalEntrance 322, aun → cls; handler cjb.a9 0x180a9f7a0): the client looks its world.xml portalentrance up by
/// "&lt;key 2&gt;@&lt;cluster&gt;" (a0.ab1) and opens the portal window on a click (UsePortalEntrance 294, Handlers/World/PortalHandler).
/// </summary>
public sealed class PortalEntrance(long id, PortalEntranceSpawn spawn, WorldPortalEntrance world) : WorldObject(id, spawn)
{
    public new PortalEntranceSpawn Spawn { get; } = spawn;

    /// <summary>The world.xml portalentrance (its city; its pos = the tile position, checked by the spawner).</summary>
    public WorldPortalEntrance World { get; } = world;

    public override Vector2 Position => World.Position;

    public override string Kind => "portal entrance";
    public override string TypeName => $"of {World.Kind}";
    public override bool LeaveOnRange => false;

    public override IEnumerable<AlbionEvent> BuildCreate(Player viewer)
    {
        yield return new NewPortalEntranceEvent
        {
            c8c = Id,           // [0]
            c8d = Position,     // [1] world.xml portalentrance/@pos = the tile position (bound to the landscape)
            c8e = World.Id,     // [2] the Guid a0.ab1 finds the entrance by; another one gives a portal without targets
        };
    }
}

/// <summary>
/// A portal town's portal (NewPortalExit 323, auo → clt; handler cjb.ay 0x180aa5660): the client looks its world.xml portalexit up by
/// "&lt;key 2&gt;@&lt;cluster&gt;" (a0.ab2) and sends a plain ChangeCluster {0: its id} once the player stands in its square
/// (PortalExitObjectView.EnterExit 0x181a73ab0).
/// </summary>
public sealed class PortalExit(long id, PortalExitSpawn spawn, WorldPortalExit world) : WorldObject(id, spawn)
{
    public new PortalExitSpawn Spawn { get; } = spawn;

    /// <summary>The world.xml portalexit (its city, the entrance it leads back to; its pos = the tile position, checked by the spawner).</summary>
    public WorldPortalExit World { get; } = world;

    public override Vector2 Position => World.Position;

    /// <summary>The component's size ("10 10"): the square the player walks into, as the client's (clt ctor 0x180b5cc60: pos ± size/2); 1 × 1 without.</summary>
    public Vector2 Size => TileMath.V2(Spawn.Attr("size")) ?? Vector2.One;

    public override string Kind => "portal exit";
    public override string TypeName => $"of {World.Kind} to {World.TargetCluster}";
    public override bool LeaveOnRange => false;

    public override IEnumerable<AlbionEvent> BuildCreate(Player viewer)
    {
        yield return new NewPortalExitEvent
        {
            c8f = Id,           // [0]
            c8g = Position,     // [1] world.xml portalexit/@pos = the tile position (bound to the landscape)
            c8h = World.Id,     // [2] the Guid a0.ab2 finds the exit by
        };
    }
}
