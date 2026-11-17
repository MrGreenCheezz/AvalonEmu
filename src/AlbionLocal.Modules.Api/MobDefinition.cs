namespace AlbionLocal.Modules;

/// <summary>
/// The numbers of a mob type of a module's own (<see cref="IServer.DefineMob"/>). What is left null stays as the base type has it. All of it is the server's: the
/// clients see the base type's model, name and animations.
/// </summary>
public sealed class MobDefinition
{
    /// <summary>Maximum health.</summary>
    public float? MaxHealth { get; set; }
    /// <summary>Health regained per second out of combat.</summary>
    public float? HealthRegen { get; set; }
    /// <summary>Maximum energy, for its spells.</summary>
    public float? MaxEnergy { get; set; }
    /// <summary>Energy regained per second.</summary>
    public float? EnergyRegen { get; set; }

    /// <summary>The damage of an auto-attack, before the target's resistance.</summary>
    public float? AttackDamage { get; set; }
    /// <summary>Auto-attacks per second.</summary>
    public float? AttackSpeed { get; set; }
    /// <summary>The reach of an auto-attack, in metres.</summary>
    public float? AttackRange { get; set; }
    /// <summary>The ability power its spells scale with (100 = as written in spells.xml).</summary>
    public float? AbilityPower { get; set; }

    /// <summary>The speed of its idle walk, in metres per second.</summary>
    public float? WalkSpeed { get; set; }
    /// <summary>The speed of its chase and its run home, in metres per second.</summary>
    public float? RunSpeed { get; set; }

    /// <summary>The armour (physical resistance).</summary>
    public float? Armor { get; set; }
    /// <summary>The magic resistance.</summary>
    public float? MagicResist { get; set; }
    /// <summary>The crowd-control resistance.</summary>
    public float? CrowdControlResist { get; set; }
    /// <summary>Knockbacks, pulls and fears do not move it.</summary>
    public bool? ImmuneToForcedMovement { get; set; }

    /// <summary>How near a player must come for it to pick the player by itself, in metres.</summary>
    public float? AggroRadius { get; set; }
    /// <summary>How far from home it may go before it gives up the fight, in metres (0 = no limit).</summary>
    public float? PursuitRadius { get; set; }
    /// <summary>How far it calls its fellows to a fight, in metres.</summary>
    public float? AlertRadius { get; set; }

    /// <summary>A reset does not take it home: where it stands becomes home.</summary>
    public bool? KeepsPositionOnReset { get; set; }
    /// <summary>A reset takes it home without refilling its health.</summary>
    public bool? KeepsHealthOnReset { get; set; }

    /// <summary>The fame of a kill before rates and factors, in whole units.</summary>
    public double? Fame { get; set; }
    /// <summary>The factor on the silver of a kill.</summary>
    public float? SilverFactor { get; set; }

    /// <summary>No spells of the base type's rotation: only an auto-attack and what the module tells it to cast.</summary>
    public bool NoRotation { get; set; }
    /// <summary>No loot of the base type's table: the bag holds what <see cref="IMobHooks.OnLootRolled(Action{LootArgs}, HookPriority)"/> puts in.</summary>
    public bool NoLoot { get; set; }
    /// <summary>None of the spells the base type casts at its death.</summary>
    public bool NoDeathSpells { get; set; }

    /// <summary>
    /// A text the clients are sent as the mob's owner guild (NewMob key 6, empty for ordinary mobs). Whether and where the client shows it on a mob has not been checked
    /// in the game; the mob's own name always comes from the client's files.
    /// </summary>
    public string? Title { get; set; }
}

/// <summary>One line of a mob's threat list (<see cref="IMob.Threat"/>).</summary>
/// <param name="Player">The player.</param>
/// <param name="Amount">Its threat.</param>
public sealed record ThreatEntry(IPlayer Player, double Amount);
