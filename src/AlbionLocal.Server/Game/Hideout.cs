using System.Numerics;
using AlbionLocal.Server.Data;

namespace AlbionLocal.Server.Game;

/// <summary>
/// Who may enter a hideout (accessrights.xml AccessRightsHideoutObject presets = AccessStatus key 1, Albion.Common…AccessRights.Preset):
/// Closed = the guild master and the members with MANAGE_HIDEOUTS, Private = the owner guild, Public = everyone.
/// </summary>
public enum HideoutAccess : byte { Closed = 0, Private = 1, Public = 2 }

/// <summary>
/// A guild hideout (analysis/work/stageP/hideouts/SPEC.md; schema v40 hideouts): placed from the kit in a black zone by a guild, its surface
/// object (NewHideoutObject 419) at <see cref="Position"/> in <see cref="Cluster"/>, its interior the instance "@HIDEOUT@&lt;id&gt;" built from
/// the world.xml cluster of its level. Under construction while <see cref="Phase"/> ≥ 0 (hideouts.xml constructionphases, each ending at
/// <see cref="PhaseEnds"/>), then built at <see cref="Level"/> 0-2. Food in whole nutrition, the tribute in a7m. Touched only under World.Sync.
/// </summary>
public sealed class Hideout(Guid id, Guid guild, string cluster, Vector2 position, float rotation, long placed)
{
    public Guid Id { get; } = id;

    /// <summary>The owner guild (its rows go with it).</summary>
    public Guid Guild { get; } = guild;

    /// <summary>The surface cluster (a world.xml id).</summary>
    public string Cluster { get; } = cluster;

    public Vector2 Position { get; } = position;

    public float Rotation { get; } = rotation;

    /// <summary>GameTimeStamp of the placement.</summary>
    public long Placed { get; } = placed;

    /// <summary>The construction phase (index into the data's phases); -1 = built.</summary>
    public int Phase { get; set; }

    /// <summary>GameTimeStamp the current construction phase ends (0 when built).</summary>
    public long PhaseEnds { get; set; }

    /// <summary>The upgrade level index (0 = HIDEOUT-0001a).</summary>
    public int Level { get; set; }

    /// <summary>GameTimeStamp an upgrade to <see cref="Level"/> + 1 began; 0 = none running.</summary>
    public long UpgradeStarted { get; set; }

    /// <summary>The food stored (whole nutrition).</summary>
    public double Nutrition { get; set; }

    /// <summary>The shields left (key 14; the level's maxshields at full, fewer after starving days).</summary>
    public int Shields { get; set; }

    /// <summary>GameTimeStamp of the last daily upkeep (the zone's prime time it was taken at); 0 = never.</summary>
    public long LastUpkeep { get; set; }

    /// <summary>The daily tribute to the zone's territory owner (a7m).</summary>
    public long Tribute { get; set; }

    public HideoutAccess Access { get; set; } = HideoutAccess.Private;

    public bool Built => Phase < 0;

    public bool Upgrading => UpgradeStarted != 0;

    /// <summary>The interior's instance id.</summary>
    public string Instance => HideoutData.InstanceId(Id);

    public override string ToString() => $"hideout {Id.ToString()[..8]} of guild {Guild.ToString()[..8]} in {Cluster}";
}
