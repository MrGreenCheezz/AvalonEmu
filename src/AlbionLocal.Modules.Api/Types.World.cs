namespace AlbionLocal.Modules;

/// <summary>What a spell is (the spells.xml element).</summary>
public enum SpellKind
{
    /// <summary>Anything else.</summary>
    Other,
    /// <summary>A spell that is cast.</summary>
    Active,
    /// <summary>A spell that is always on.</summary>
    Passive,
    /// <summary>A spell that is switched on and off.</summary>
    Toggle,
}

/// <summary>What a spell does, as spells.xml groups it; <see cref="SpellInfo.CategoryName"/> has the text for the rare ones.</summary>
public enum SpellCategory
{
    /// <summary>None.</summary>
    None,
    /// <summary>A rare one: see <see cref="SpellInfo.CategoryName"/>.</summary>
    Other,
    /// <summary>instant.</summary>
    Instant,
    /// <summary>damage.</summary>
    Damage,
    /// <summary>heal.</summary>
    Heal,
    /// <summary>buff.</summary>
    Buff,
    /// <summary>debuff.</summary>
    Debuff,
    /// <summary>crowdcontrol.</summary>
    CrowdControl,
    /// <summary>forcedmovement.</summary>
    ForcedMovement,
    /// <summary>movementbuff.</summary>
    MovementBuff,
    /// <summary>foodbuff.</summary>
    FoodBuff,
    /// <summary>mountbuff.</summary>
    MountBuff,
    /// <summary>buff_damageshield.</summary>
    DamageShield,
    /// <summary>invisibility.</summary>
    Invisibility,
    /// <summary>healingsickness.</summary>
    HealingSickness,
}

/// <summary>What a spell can be aimed at (spells.xml target).</summary>
public enum SpellTarget
{
    /// <summary>None, or one this list does not have yet.</summary>
    None,
    /// <summary>self.</summary>
    Self,
    /// <summary>enemy.</summary>
    Enemy,
    /// <summary>enemyplayers.</summary>
    EnemyPlayers,
    /// <summary>enemymobs.</summary>
    EnemyMobs,
    /// <summary>enemyunmounted.</summary>
    EnemyUnmounted,
    /// <summary>ground.</summary>
    Ground,
    /// <summary>all.</summary>
    All,
    /// <summary>allplayers.</summary>
    AllPlayers,
    /// <summary>allmobs.</summary>
    AllMobs,
    /// <summary>allunmounted.</summary>
    AllUnmounted,
    /// <summary>friendall.</summary>
    FriendAll,
    /// <summary>friendother.</summary>
    FriendOther,
    /// <summary>friendallplayers.</summary>
    FriendAllPlayers,
    /// <summary>friendotherplayers.</summary>
    FriendOtherPlayers,
    /// <summary>friendallmobs.</summary>
    FriendAllMobs,
    /// <summary>friendothermobs.</summary>
    FriendOtherMobs,
    /// <summary>friendfaction.</summary>
    FriendFaction,
    /// <summary>factionself.</summary>
    FactionSelf,
    /// <summary>other.</summary>
    Other,
    /// <summary>storedtarget.</summary>
    StoredTarget,
    /// <summary>knockeddownplayer.</summary>
    KnockedDownPlayer,
    /// <summary>knockeddownfriendplayer.</summary>
    KnockedDownFriendPlayer,
}

/// <summary>
/// A spell from spells.xml: every hook about a spell hands one out, and <see cref="IServer.Spell"/> finds one. Where the API asks for a spell's name as a string it goes as
/// it is: <c>Game.ApplyBuff(target, e.Spell)</c>. Two are equal when they have the same <see cref="Name"/>.
/// </summary>
/// <example>
/// <code><![CDATA[
/// if (e.Spell is { Category: SpellCategory.CrowdControl }) e.Cancel();
/// if (e.Spell.Is("MOB_*")) ...
/// ]]></code>
/// </example>
public sealed class SpellInfo : IEquatable<SpellInfo>
{
    internal SpellInfo() { }

    /// <summary>The uniquename.</summary>
    public string Name { get; internal init; } = "";

    /// <summary>The numeric spell id the client casts it by.</summary>
    public int Id { get; internal init; } = -1;

    /// <summary>What it is.</summary>
    public SpellKind Kind { get; internal init; }

    /// <summary>What it does.</summary>
    public SpellCategory Category { get; internal init; }

    /// <summary>The spells.xml category as it is; "" when none.</summary>
    public string CategoryName { get; internal init; } = "";

    /// <summary>What it can be aimed at.</summary>
    public SpellTarget Target { get; internal init; }

    /// <summary>The casting time in seconds, before any reduction.</summary>
    public double CastTime { get; internal init; }

    /// <summary>The cooldown in seconds, before any reduction.</summary>
    public double Cooldown { get; internal init; }

    /// <summary>The cast range in metres.</summary>
    public float Range { get; internal init; }

    /// <summary>The most stacks it can have on one unit.</summary>
    public int StackLimit { get; internal init; } = 1;

    /// <summary>It is channelled: it ticks (<see cref="ISpellHooks.OnTick(Action{SpellTickArgs}, HookPriority)"/>).</summary>
    public bool IsChanneled { get; internal init; }

    /// <summary>It flies as a projectile.</summary>
    public bool IsProjectile { get; internal init; }

    /// <summary>Whether it is <paramref name="nameOrMask"/>: a uniquename or a mask with * and ?. Case does not matter.</summary>
    public bool Is(string nameOrMask) => NameMask.Matches(Name, nameOrMask);

    /// <summary>The uniquename.</summary>
    public override string ToString() => Name;

    /// <summary>The same spell: the same <see cref="Name"/>.</summary>
    public bool Equals(SpellInfo? other) => other is not null && Name == other.Name;

    /// <summary>The same spell: the same <see cref="Name"/>.</summary>
    public override bool Equals(object? obj) => Equals(obj as SpellInfo);

    /// <summary>The hash of <see cref="Name"/>.</summary>
    public override int GetHashCode() => Name.GetHashCode();

    /// <summary>The same spell.</summary>
    public static bool operator ==(SpellInfo? a, SpellInfo? b) => a is null ? b is null : a.Equals(b);

    /// <summary>Not the same spell.</summary>
    public static bool operator !=(SpellInfo? a, SpellInfo? b) => !(a == b);

    /// <summary>The uniquename, so that a spell goes wherever the API takes a spell's name. Null is "", which finds nothing.</summary>
    public static implicit operator string(SpellInfo? spell) => spell?.Name ?? "";
}

/// <summary>A mob's rank and role (mobs.xml mobtypecategory).</summary>
public enum MobCategory
{
    /// <summary>None.</summary>
    None,
    /// <summary>standard.</summary>
    Standard,
    /// <summary>trash.</summary>
    Trash,
    /// <summary>champion.</summary>
    Champion,
    /// <summary>miniboss.</summary>
    MiniBoss,
    /// <summary>boss.</summary>
    Boss,
    /// <summary>summon.</summary>
    Summon,
    /// <summary>critter.</summary>
    Critter,
    /// <summary>environment.</summary>
    Environment,
    /// <summary>chest.</summary>
    Chest,
    /// <summary>harmless.</summary>
    Harmless,
    /// <summary>vanity.</summary>
    Vanity,
    /// <summary>One this list does not have yet.</summary>
    Other,
}

/// <summary>A mob's side (mobs.xml faction; the camp and static variants count as their side). <see cref="MobType.FactionName"/> has the text.</summary>
public enum MobFaction
{
    /// <summary>Any other: a city's guards, treasures, events, statues.</summary>
    Other,
    /// <summary>The Keepers.</summary>
    Keeper,
    /// <summary>The Undead.</summary>
    Undead,
    /// <summary>The Heretics.</summary>
    Heretic,
    /// <summary>Morgana's followers.</summary>
    Morgana,
    /// <summary>The Avalonians.</summary>
    Avalon,
    /// <summary>The Demons.</summary>
    Demon,
    /// <summary>Critters.</summary>
    Critter,
}

/// <summary>A kind of mob from mobs.xml (<see cref="IMob.Type"/>, <see cref="IServer.Mob"/>). Where the API asks for a mob's name as a string it goes as it is. Two are equal when they have the same <see cref="Name"/>.</summary>
public sealed class MobType : IEquatable<MobType>
{
    internal MobType() { }

    /// <summary>The uniquename.</summary>
    public string Name { get; internal init; } = "";

    /// <summary>The tier (1-8).</summary>
    public int Tier { get; internal init; }

    /// <summary>Its rank and role.</summary>
    public MobCategory Category { get; internal init; }

    /// <summary>Its side.</summary>
    public MobFaction Faction { get; internal init; }

    /// <summary>mobs.xml faction as it is; "" when none.</summary>
    public string FactionName { get; internal init; } = "";

    /// <summary>The fame a kill gives before rates and factors (whole units).</summary>
    public double Fame { get; internal init; }

    /// <summary>The maximum health of mobs.xml.</summary>
    public float MaxHealth { get; internal init; }

    /// <summary>A champion, mini boss or boss.</summary>
    public bool IsElite => Category is MobCategory.Champion or MobCategory.MiniBoss or MobCategory.Boss;

    /// <summary>Whether it is <paramref name="nameOrMask"/>: a uniquename or a mask with * and ?. Case does not matter.</summary>
    public bool Is(string nameOrMask) => NameMask.Matches(Name, nameOrMask);

    /// <summary>The uniquename.</summary>
    public override string ToString() => Name;

    /// <summary>The same kind of mob: the same <see cref="Name"/>.</summary>
    public bool Equals(MobType? other) => other is not null && Name == other.Name;

    /// <summary>The same kind of mob: the same <see cref="Name"/>.</summary>
    public override bool Equals(object? obj) => Equals(obj as MobType);

    /// <summary>The hash of <see cref="Name"/>.</summary>
    public override int GetHashCode() => Name.GetHashCode();

    /// <summary>The same kind of mob.</summary>
    public static bool operator ==(MobType? a, MobType? b) => a is null ? b is null : a.Equals(b);

    /// <summary>Not the same kind of mob.</summary>
    public static bool operator !=(MobType? a, MobType? b) => !(a == b);

    /// <summary>The uniquename, so that a mob type goes wherever the API takes a mob's name. Null is "", which finds nothing.</summary>
    public static implicit operator string(MobType? mob) => mob?.Name ?? "";
}

/// <summary>What kind of place a zone is.</summary>
public enum ZoneKind
{
    /// <summary>Anything else, or nothing known yet.</summary>
    Other,
    /// <summary>A city.</summary>
    City,
    /// <summary>The open world.</summary>
    OpenWorld,
    /// <summary>A passage between zones.</summary>
    Passage,
    /// <summary>A dungeon.</summary>
    Dungeon,
    /// <summary>A corrupted dungeon.</summary>
    CorruptedDungeon,
    /// <summary>A hellgate.</summary>
    Hellgate,
    /// <summary>An expedition.</summary>
    Expedition,
    /// <summary>An arena.</summary>
    Arena,
    /// <summary>The Roads of Avalon.</summary>
    Road,
    /// <summary>A player's or a guild's island.</summary>
    Island,
    /// <summary>A hideout.</summary>
    Hideout,
    /// <summary>The tutorial.</summary>
    Tutorial,
}

/// <summary>A zone's colour on the world map.</summary>
public enum ZoneColor
{
    /// <summary>None: its rules are its own (see <see cref="ZoneInfo.Pvp"/>).</summary>
    None,
    /// <summary>Blue: safe.</summary>
    Blue,
    /// <summary>Yellow.</summary>
    Yellow,
    /// <summary>Red.</summary>
    Red,
    /// <summary>Black.</summary>
    Black,
}

/// <summary>Whether players may fight in a zone.</summary>
public enum ZonePvp
{
    /// <summary>No: cities, islands, hideouts.</summary>
    Disabled,
    /// <summary>Between faction-flagged players only.</summary>
    FactionOnly,
    /// <summary>Whoever flags hostile may.</summary>
    Allowed,
    /// <summary>Everyone, but for party, guild and alliance: black zones.</summary>
    Forced,
}

/// <summary>A zone (cluster), as it is now: <see cref="IUnit.Zone"/>, <see cref="IServer.Zone"/>. Two are equal when they are the same cluster.</summary>
/// <example>
/// <code><![CDATA[
/// if (player.Zone is { Color: ZoneColor.Black, IsLethal: true }) ...
/// if (player.Zone.Kind == ZoneKind.City) ...
/// ]]></code>
/// </example>
public sealed class ZoneInfo : IEquatable<ZoneInfo>
{
    internal ZoneInfo() { }

    /// <summary>The cluster's world.xml id ("0006"), what <see cref="IServer.Mobs"/> and the like take; "" when it is no zone (a unit on no map).</summary>
    public string Cluster { get; internal init; } = "";

    /// <summary>The name on the world map; "" when it has none.</summary>
    public string DisplayName { get; internal init; } = "";

    /// <summary>What kind of place it is.</summary>
    public ZoneKind Kind { get; internal init; }

    /// <summary>Its colour on the world map.</summary>
    public ZoneColor Color { get; internal init; }

    /// <summary>Whether players may fight here.</summary>
    public ZonePvp Pvp { get; internal init; }

    /// <summary>A player's death at a player's hands here is a real one, with the gear lost.</summary>
    public bool IsLethal { get; internal init; }

    /// <summary>The world.xml cluster type as it is ("OPENPVP_BLACK_2"), for what the types here do not cover; "" when none.</summary>
    public string Type { get; internal init; } = "";

    /// <summary>The cluster id.</summary>
    public override string ToString() => Cluster;

    /// <summary>The same cluster.</summary>
    public bool Equals(ZoneInfo? other) => other is not null && Cluster == other.Cluster;

    /// <summary>The same cluster.</summary>
    public override bool Equals(object? obj) => Equals(obj as ZoneInfo);

    /// <summary>The hash of <see cref="Cluster"/>.</summary>
    public override int GetHashCode() => Cluster.GetHashCode();

    /// <summary>The same cluster.</summary>
    public static bool operator ==(ZoneInfo? a, ZoneInfo? b) => a is null ? b is null : a.Equals(b);

    /// <summary>Not the same cluster.</summary>
    public static bool operator !=(ZoneInfo? a, ZoneInfo? b) => !(a == b);

    /// <summary>The cluster id, so that a zone goes wherever the API takes a cluster. Null is "", which finds nothing.</summary>
    public static implicit operator string(ZoneInfo? zone) => zone?.Cluster ?? "";
}

/// <summary>The rules the server runs with (<see cref="IServer.Preset"/>).</summary>
public enum GamePreset
{
    /// <summary>The official rules.</summary>
    Official,
    /// <summary>The relaxed PvE rules.</summary>
    Pve,
}

/// <summary>
/// The buff attributes of spells.xml as names to pick from, for <see cref="PassiveArgs.Add"/> and <see cref="IGameplay.SetModifier"/>: <c>Game.SetModifier(unit, "rage", Stat.AttackDamage, 0.2)</c>.
/// They are plain strings, so a pack's own attribute can go there too. Bonuses are shares: 0.2 is +20%.
/// </summary>
public static class Stat
{
    /// <summary>physicalattackdamagebonus: auto-attack damage.</summary>
    public const string AttackDamage = "physicalattackdamagebonus";
    /// <summary>magicattackdamagebonus: magic auto-attack damage.</summary>
    public const string MagicAttackDamage = "magicattackdamagebonus";
    /// <summary>physicalspelldamagebonus: physical spell damage.</summary>
    public const string PhysicalSpellDamage = "physicalspelldamagebonus";
    /// <summary>magicspelldamagebonus: magic spell damage.</summary>
    public const string MagicSpellDamage = "magicspelldamagebonus";
    /// <summary>attackspeedbonus: attack speed.</summary>
    public const string AttackSpeed = "attackspeedbonus";
    /// <summary>attackrangebonus: attack range.</summary>
    public const string AttackRange = "attackrangebonus";
    /// <summary>bonusdamagevsplayers: damage dealt to players.</summary>
    public const string DamageVsPlayers = "bonusdamagevsplayers";
    /// <summary>bonusdamagevsmobs: damage dealt to mobs.</summary>
    public const string DamageVsMobs = "bonusdamagevsmobs";
    /// <summary>damagevsmountsmodifier: damage dealt to mounts.</summary>
    public const string DamageVsMounts = "damagevsmountsmodifier";
    /// <summary>bonusdefensevsplayers: defence against players.</summary>
    public const string DefenseVsPlayers = "bonusdefensevsplayers";
    /// <summary>bonusdefensevsmobs: defence against mobs.</summary>
    public const string DefenseVsMobs = "bonusdefensevsmobs";
    /// <summary>physicalarmor: armour.</summary>
    public const string Armor = "physicalarmor";
    /// <summary>magicresistance: magic resistance.</summary>
    public const string MagicResist = "magicresistance";
    /// <summary>crowdcontrolresistance: crowd-control resistance.</summary>
    public const string CrowdControlResist = "crowdcontrolresistance";
    /// <summary>bonusccrvsplayers: crowd-control resistance against players.</summary>
    public const string CrowdControlResistVsPlayers = "bonusccrvsplayers";
    /// <summary>bonusccrvsmobs: crowd-control resistance against mobs.</summary>
    public const string CrowdControlResistVsMobs = "bonusccrvsmobs";
    /// <summary>bonusccdurationvsplayers: crowd-control duration on players.</summary>
    public const string CrowdControlDurationVsPlayers = "bonusccdurationvsplayers";
    /// <summary>bonusccdurationvsmobs: crowd-control duration on mobs.</summary>
    public const string CrowdControlDurationVsMobs = "bonusccdurationvsmobs";
    /// <summary>ccdurationvsmountsmodifier: crowd-control duration on mounts.</summary>
    public const string CrowdControlDurationVsMounts = "ccdurationvsmountsmodifier";
    /// <summary>hitpointsmaxbonus: maximum health.</summary>
    public const string MaxHealth = "hitpointsmaxbonus";
    /// <summary>hitpointsregeneration: health regeneration, as a number.</summary>
    public const string HealthRegen = "hitpointsregeneration";
    /// <summary>hitpointsregenerationbonus: health regeneration, as a share.</summary>
    public const string HealthRegenBonus = "hitpointsregenerationbonus";
    /// <summary>energyregeneration: energy regeneration, as a number.</summary>
    public const string EnergyRegen = "energyregeneration";
    /// <summary>energyregenerationbonus: energy regeneration, as a share.</summary>
    public const string EnergyRegenBonus = "energyregenerationbonus";
    /// <summary>energycostreduction: spells cost less energy.</summary>
    public const string EnergyCostReduction = "energycostreduction";
    /// <summary>magiccasttimereduction: shorter casts.</summary>
    public const string CastTimeReduction = "magiccasttimereduction";
    /// <summary>magiccooldownreduction: shorter cooldowns.</summary>
    public const string CooldownReduction = "magiccooldownreduction";
    /// <summary>magiccooldownratemodifier: the rate cooldowns run at.</summary>
    public const string CooldownRate = "magiccooldownratemodifier";
    /// <summary>healbonus: heals cast.</summary>
    public const string HealBonus = "healbonus";
    /// <summary>healmodifier: heals received.</summary>
    public const string HealModifier = "healmodifier";
    /// <summary>healingsicknessmodifier: healing sickness.</summary>
    public const string HealingSickness = "healingsicknessmodifier";
    /// <summary>threatbonus: threat caused.</summary>
    public const string ThreatBonus = "threatbonus";
    /// <summary>movespeedbonus: movement speed. Movement is the client's own: use it in a buff spell, a server-side modifier alone is not seen there.</summary>
    public const string MoveSpeed = "movespeedbonus";
    /// <summary>movespeed: movement speed, as a number. Movement is the client's own, as for <see cref="MoveSpeed"/>.</summary>
    public const string MoveSpeedFlat = "movespeed";
    /// <summary>maxload: the load carried, as a number.</summary>
    public const string MaxLoad = "maxload";
    /// <summary>maxloadbonus: the load carried, as a share.</summary>
    public const string MaxLoadBonus = "maxloadbonus";
    /// <summary>itempower: item power.</summary>
    public const string ItemPower = "itempower";
    /// <summary>mobfamemodifier: fame from mobs.</summary>
    public const string MobFame = "mobfamemodifier";
    /// <summary>moblootmodifier: loot from mobs.</summary>
    public const string MobLoot = "moblootmodifier";
    /// <summary>mobsilvermodifier: silver from mobs.</summary>
    public const string MobSilver = "mobsilvermodifier";
    /// <summary>resourcefamemodifier: fame from gathering.</summary>
    public const string ResourceFame = "resourcefamemodifier";
    /// <summary>fishingfamemodifier: fame from fishing.</summary>
    public const string FishingFame = "fishingfamemodifier";
    /// <summary>trackingfamemodifier: fame from tracking.</summary>
    public const string TrackingFame = "trackingfamemodifier";
    /// <summary>destinyboardprogressionbonus: destiny board progress.</summary>
    public const string DestinyProgress = "destinyboardprogressionbonus";
    /// <summary>gatheringyield: resources per harvest.</summary>
    public const string GatheringYield = "gatheringyield";
    /// <summary>gatheringspeed: harvest speed.</summary>
    public const string GatheringSpeed = "gatheringspeed";
    /// <summary>fishingyield: catches.</summary>
    public const string FishingYield = "fishingyield";
    /// <summary>fishingspeed: fishing speed.</summary>
    public const string FishingSpeed = "fishingspeed";
    /// <summary>craftingquality: the quality crafted.</summary>
    public const string CraftingQuality = "craftingquality";
    /// <summary>crafttime: crafting time.</summary>
    public const string CraftTime = "crafttime";
    /// <summary>factionpointsbonus: faction points.</summary>
    public const string FactionPoints = "factionpointsbonus";
    /// <summary>mightgain: might.</summary>
    public const string MightGain = "mightgain";
    /// <summary>balancemodifier: the game's balancing factor.</summary>
    public const string Balance = "balancemodifier";
    /// <summary>focusfireprotectionpenetration: what goes through focus fire protection.</summary>
    public const string FocusFirePenetration = "focusfireprotectionpenetration";
}
