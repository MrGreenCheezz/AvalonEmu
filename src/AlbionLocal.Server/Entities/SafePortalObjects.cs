using System.Numerics;
using AlbionLocal.Server.Data;
using AlbionLocal.Server.Protocol;

namespace AlbionLocal.Server.Entities;

// The S.A.F.E. portal of an Outlands portal town and the personal return portal it opens (analysis/work/stageR/safe-portal/SPEC.md,
// "SPEC" below). Handlers/World/SafePortalHandler, Systems/SafePortals.

/// <summary>
/// A portal town's S.A.F.E. portal (NewOutlandsTeleportationPortal 606, a12 → cnf; handler cjb.q 0x180AAF8F0): the template's critical member
/// outlandsteleportationportal_01. The client binds it to the landscape tile at the exact position (cjb.a(pos, ["outlandsteleportationportal"]),
/// as NewExit), so key 2 must be the tile's position; cnf takes its destinations from outlandsteleportationportals.xml by the cluster. A click
/// opens the S.A.F.E. window (RequestOutlandsTeleportationUsage 517), Travel sends ChangeCluster {0 its id, 4 the cost}.
/// </summary>
public sealed class SafePortal(long id, CriticalMember spawn) : WorldObject(id, spawn)
{
    public new CriticalMember Spawn { get; } = spawn;

    public override string Kind => "S.A.F.E. portal";
    public override string TypeName => Spawn.Path;
    public override bool LeaveOnRange => false;

    public override IEnumerable<AlbionEvent> BuildCreate(Player viewer)
    {
        yield return new NewOutlandsTeleportationPortalEvent
        {
            d7b = Id,                          // [0]
            d7c = Spawn.Guid ?? Guid.Empty,    // [1] the world.xml criticalObjects Guid (cnf.ou; aqp "<guid>@<cluster>")
            d7d = Position,                    // [2] the tile position (bound to the landscape)
            d7e = Direction,                   // [3] yaw
        };
    }
}

/// <summary>The spawn record of a return portal: no template object, a tile placed by the event (key 4).</summary>
public sealed record SafeReturnPortalSpawn : ClusterObject;

/// <summary>
/// A player's return portal (NewOutlandsTeleportationReturnPortal 607, a13 → cng; handler cjb.p 0x180AAF6F0): the tile key 4 names
/// (returnportaltile) placed by the client at keys 1/2. Usable only by the player whose binding (Join key 130) equals key 3 (cng.aqq) before
/// the binding's end (Join key 129); its click is a plain ChangeCluster {0 its id, 1 "", 4 0} back to the portal town (cng.aqs → Join key 127).
/// </summary>
public sealed class SafeReturnPortal(long id, SafeReturnPortalSpawn spawn, Guid owner, string bindingId, string tile) : WorldObject(id, spawn)
{
    /// <summary>The character whose return portal it is.</summary>
    public Guid Owner { get; } = owner;

    /// <summary>Join key 130 / key 3: the binding it belongs to.</summary>
    public string BindingId { get; } = bindingId;

    public override string Kind => "S.A.F.E. return portal";
    public override string TypeName => BindingId;
    public override bool LeaveOnRange => true;

    public override IEnumerable<AlbionEvent> BuildCreate(Player viewer)
    {
        yield return new NewOutlandsTeleportationReturnPortalEvent
        {
            d7f = Id,          // [0]
            d7g = Position,    // [1]
            d7h = Direction,   // [2]
            d7i = BindingId,   // [3] compared with the local player's binding id (cng.aqq)
            d7j = tile,        // [4] the tile to place (a9f.u; unknown = "NewOutlandsTeleportationReturnPortal: …" error, no portal)
        };
    }
}
