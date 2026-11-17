namespace AlbionLocal.Server.Game;

/// <summary>
/// What the owner of a player's station keeps on it (analysis/work/stageR/buildings/SPEC.md §2-§3; schema v51+ building_states): the usage
/// fees per role, the fees collected and not yet taken, the nutrition, the silver deposited for feeding rewards with the reward per nutrition
/// point, and the access preset. Every amount is a7m raw (× 10000). Keyed by the building's Guid (NewBuilding key 1).
/// </summary>
public sealed class BuildingState(Guid id)
{
    public Guid Id { get; } = id;

    /// <summary>
    /// The fee a "friend" (the owner's guild under the private and public presets) pays, silver per 100 nutrition the use eats
    /// (CraftBuildingChangeSettings key 1, CraftBuildingInfo key 1; the client's cox+0x208, read by cox.ab8 for the friend role).
    /// </summary>
    public long FriendFee { get; set; }

    /// <summary>The fee a "user" (everyone under the public preset) pays (op 47 key 2, CraftBuildingInfo key 2; cox+0x210).</summary>
    public long UserFee { get; set; }

    /// <summary>The fees collected and not yet taken by the owner (CraftBuildingInfo key 4, the window's "earned silver"; op 48 takes it).</summary>
    public long Earned { get; set; }

    /// <summary>The nutrition (NewBuilding key 18; the client's cou+0xE8): each craft above FreeCrafting maxTier eats some, food fills it.</summary>
    public long Nutrition { get; set; }

    /// <summary>The silver the owner deposited to reward other players who feed the building (NewBuilding key 23; cou+0x120).</summary>
    public long RewardStorage { get; set; }

    /// <summary>The reward per nutrition point a feeder gets from <see cref="RewardStorage"/> (NewBuilding key 24; cou+0x128).</summary>
    public long RewardPerNutrition { get; set; }

    /// <summary>The access preset (AccessRights Preset: 0 closed, 1 private, 2 public; AccessStatus key 1).</summary>
    public int Preset { get; set; } = 2;
}
