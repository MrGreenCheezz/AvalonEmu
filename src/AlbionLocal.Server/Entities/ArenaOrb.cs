using AlbionLocal.Server.Data;
using AlbionLocal.Server.Protocol;

namespace AlbionLocal.Server.Entities;

/// <summary>
/// An arena orb ("runestone", stageP league-pvp SPEC §1.2, §3.2): an &lt;orb type&gt; tile of an arena match's map as NewOrbObject (111, auk:
/// 0 id, 1 the orbtypes.xml uniquename (OrbObjectView.Name is "@ARENA_NAME_" + the type), 2 the tiles.xml tile name [infer: as NewShrine's
/// key 4], 3 position, 4 the owning team's Guid, empty while neutral [infer]). Its capture state is <see cref="Systems.Arenas"/>'s.
/// Landscape-bound like a shrine: never left on range, sent again on re-entry.
/// </summary>
public sealed class ArenaOrb(long id, OrbSpawn spawn, MatchTypeData.OrbType type) : WorldObject(id, spawn)
{
    public MatchTypeData.OrbType Type { get; } = type;

    /// <summary>The owning team (0, 1), −1 neutral.</summary>
    public int Owner { get; set; } = -1;

    /// <summary>The owner's team Guid (key 4); empty while neutral.</summary>
    public Guid OwnerGuid { get; set; }

    /// <summary>A zone capture under way: the team (−1 none) and its seconds so far (an enemy orb's unclaim first, then the claim).</summary>
    public int Claiming { get; set; } = -1;
    public double Progress { get; set; }

    /// <summary>When a team last stood in the zone (a claim under way drops orbnoteamunclaimtime after it).</summary>
    public TimeSpan LastPresence { get; set; }

    public override string Kind => "orb";
    public override string TypeName => $"{Type.Name} (team {Owner})";
    public override bool LeaveOnRange => false;

    public override IEnumerable<AlbionEvent> BuildCreate(Player viewer)
    {
        yield return new NewOrbObjectEvent { c7r = Id, c7s = Type.Name, c7t = Spawn.Tile, c7u = Position, c7v = OwnerGuid };
    }
}
