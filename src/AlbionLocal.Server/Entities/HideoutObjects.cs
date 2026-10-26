using System.Numerics;
using System.Xml.Linq;
using AlbionLocal.Server.Data;
using AlbionLocal.Server.Game;
using AlbionLocal.Server.Protocol;

namespace AlbionLocal.Server.Entities;

// The guild hideouts' world objects (analysis/work/stageP/hideouts/SPEC.md §2.5, "SPEC" below): the hideout on the surface of its black zone
// (NewHideoutObject 419), and inside its instance the management (NewHideoutManagement 420 + HideoutManagementUpdate 425) and the exit
// (NewHideoutExit 421). Made from the hideout store (Systems/Hideouts), not from a template tile.

/// <summary>The spawn record of a hideout object (no template tile behind the surface object; the interior's tiles for the others).</summary>
public sealed record HideoutObjectSpawn : ClusterObject
{
    public static HideoutObjectSpawn At(string cluster, string slot, int n, string tile, Vector2 position, float direction) => new()
    {
        Key = new SpawnKey(cluster, slot, n), Tile = tile, Position = position, Direction = direction, Component = new XElement("hideout"),
    };
}

/// <summary>What the surface object's keys 13-29 carry for the client (Systems.Hideouts.Surface): state, shields, construction and durability.</summary>
public sealed record HideoutSurfaceState(byte Level, byte Shields, byte Mode, byte Phase, long PhaseEnds, long Durability, long Stamp);

/// <summary>
/// A hideout on the surface (NewHideoutObject 419, client at3 → cpb; cpb.af(at3) 0x180bb2580, decoded in analysis/work/stageP/hideouts/h1):
/// 0 id, 1 the hideout Guid, 2 the hideoutbuilding type (bbh.abo: UNIQUE_HIDEOUT), 3 its uniquename (cou+0x70, the tile as in NewBuilding), 4 the
/// position, 5 the rotation, 6/7 the owner guild and name, 8/9 its alliance and name, 11 the placement stamp [H], 13 the level index (cpb_af_13:
/// an index into hideouts.xml upgradelevels, bounds-checked), 14 the shields left (HideoutStateProvider: maxshields = intact, fewer = damaged,
/// 0 = ruin), 15 the mode (bcv.a: 1 construction with 17 the phase index into constructionphases and 18 its end, cpb_af_t14d7; 2 established,
/// cpb_x8_5; the client itself advances a phase at its end, cpb_x8_6), 21/22 the durability bar (a7m and its stamp, a5a.m), 25 the power
/// pool's stamp. Neutral and left out (0/false/""): 10, 12, 16, 19, 20, 23, 24, 26-29 (meanings not decoded, SPEC §8 risk 1). Then its
/// AccessStatus (preset, owner/visitor/noaccess). Left out of range like a building.
/// </summary>
public sealed class HideoutObject(long id, HideoutObjectSpawn spawn, Hideout hideout, short buildingType, Func<Hideout, HideoutObjectInfo> info)
    : WorldObject(id, spawn), IAccessStatusObject
{
    public Hideout Hideout { get; } = hideout;

    public short BuildingType { get; } = buildingType;

    public override string Kind => "hideout";
    public override string TypeName => HideoutData.Kit;
    public override bool LeaveOnRange => true;

    public override IEnumerable<AlbionEvent> BuildCreate(Player viewer)
    {
        yield return NewObject();
    }

    /// <summary>The 419 as it stands now (also sent again to the observers on every change of state, cpb.af compares and animates).</summary>
    public NewHideoutObjectEvent NewObject()
    {
        var i = info(Hideout);
        var s = i.State;
        var e = new NewHideoutObjectEvent
        {
            c1o = Id,                 // [0]
            c1p = Hideout.Id,         // [1]
            c1q = BuildingType,       // [2] buildings.xml UNIQUE_HIDEOUT
            c1r = HideoutData.Kit,    // [3] the uniquename (cou+0x70)
            c1s = Position,           // [4]
            c1t = Direction,          // [5]
            c1u = Hideout.Guild,      // [6]
            c1v = i.GuildName,        // [7]
            c1w = i.Alliance,         // [8] (left out without one)
            c1x = i.AllianceName,     // [9]
            c1z = Hideout.Placed,     // [11] [H] the placement
            c11 = s.Level,            // [13] level index (bcv.e)
            c12 = s.Shields,          // [14] shields left
            c13 = s.Mode,             // [15] bcv.a: 1 construction, 2 established
            c15 = s.Phase,            // [17] construction phase (with 15 = 1)
            c16 = s.PhaseEnds,        // [18] its end
            c19 = s.Durability,       // [21] a7m
            c2a = s.Stamp,            // [22] its stamp
            c2d = s.Stamp,            // [25] the power pool's stamp (24 = 0 power)
        };
        e.Extra = new() { [13] = s.Level, [14] = s.Shields }; // also at 0 (bytes the client indexes and compares)
        if (s.Mode == 1) e.Extra[17] = s.Phase;              // [17] also at phase 0
        return e;
    }

    /// <summary>AccessStatus {0 id, 1 the preset, 4 owner/guild/everyone, 5 their roles by the preset, 6 "", the guild's name, ""}.</summary>
    public AccessStatusEvent AccessStatus() => Systems.Hideouts.Access(Id, Hideout, info(Hideout).GuildName);
}

/// <summary>The owner names of a hideout and its state for the client (Systems.Hideouts.Info).</summary>
public sealed record HideoutObjectInfo(string GuildName, Guid Alliance, string AllianceName, HideoutSurfaceState State);

/// <summary>
/// The hideout management inside the instance (NewHideoutManagement 420, client at2 → ckj, ckj.af 0x180b38500): 0 id, 1 the instance id
/// "@HIDEOUT@&lt;guid&gt;" (parsed back into the hideout Guid; Make Home compares Join key 67 with it, HideoutManagementGui.OnClickMakeHome),
/// 2 the tile position, 3/4 the owner guild and name, 5 the hideout's surface position [H], 7 the power stamp; 6, 8-13 neutral. Followed by
/// HideoutManagementUpdate 425 (ckj.x8 0x180b39cd0): 1/2 guild, 3 alliance, 6 upgrading with 7 its start, 8 the level index, 9 the food
/// (a7m; the storage is the level's), 10 the tribute (a7m); 4, 5, 11-14 neutral. And its AccessStatus (as the surface object's).
/// </summary>
public sealed class HideoutManagement(long id, HideoutObjectSpawn spawn, Hideout hideout, Func<Hideout, HideoutObjectInfo> info, Func<long> now)
    : WorldObject(id, spawn), IAccessStatusObject
{
    public Hideout Hideout { get; } = hideout;

    public override string Kind => "hideout management";
    public override string TypeName => "HIDEOUT_MANAGEMENT";
    public override bool LeaveOnRange => true;

    public override IEnumerable<AlbionEvent> BuildCreate(Player viewer)
    {
        var i = info(Hideout);
        yield return new NewHideoutManagementEvent
        {
            c1a = Id,                 // [0]
            c1b = Hideout.Instance,   // [1] "@HIDEOUT@<guid>"
            c1c = Position,           // [2]
            c1d = Hideout.Guild,      // [3]
            c1e = i.GuildName,        // [4]
            c1f = Hideout.Position,   // [5] [H] the surface position
            c1h = now(),              // [7] the power pool's stamp
        };
        yield return Update();
    }

    /// <summary>HideoutManagementUpdate 425 as the hideout stands now.</summary>
    public HideoutManagementUpdateEvent Update()
    {
        var i = info(Hideout);
        var e = new HideoutManagementUpdateEvent
        {
            ddn = Id,                                                     // [0]
            ci0 = Hideout.Guild,                                          // [1]
            ci1 = i.GuildName,                                            // [2]
            ci2 = i.Alliance,                                             // [3]
            ci5 = Hideout.Upgrading,                                      // [6] an upgrade runs (ckj_are: AlreadyUpgrading)
            ci6 = Hideout.UpgradeStarted,                                 // [7] its start (the end = start + the next level's time)
            ci7 = (byte)Hideout.Level,                                    // [8] level index
            ci8 = (long)Math.Round(Hideout.Nutrition * Game.Pricing.A7m), // [9] the food, a7m
            ci9 = Hideout.Tribute,                                        // [10] the tribute, a7m
        };
        e.Extra = new() { [8] = (byte)Hideout.Level }; // [8] also at level 0
        return e;
    }

    public AccessStatusEvent AccessStatus() => Systems.Hideouts.Access(Id, Hideout, info(Hideout).GuildName);
}

/// <summary>The exit inside a hideout (NewHideoutExit 421, at1): 0 id, 1 the position, 2 the rotation; ExitEnterStart on it leads out.</summary>
public sealed class HideoutExitObject(long id, HideoutObjectSpawn spawn, Hideout hideout) : WorldObject(id, spawn)
{
    public Hideout Hideout { get; } = hideout;

    public override string Kind => "hideout exit";
    public override string TypeName => spawn.Tile;
    public override bool LeaveOnRange => true;

    public override IEnumerable<AlbionEvent> BuildCreate(Player viewer)
    {
        yield return new NewHideoutExitEvent { c07 = Id, c08 = Position, c09 = Direction };
    }
}
