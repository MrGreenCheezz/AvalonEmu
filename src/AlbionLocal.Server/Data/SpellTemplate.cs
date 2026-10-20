using System.Globalization;
using System.Xml.Linq;

namespace AlbionLocal.Server.Data;

/// <summary>
/// One spell of spells.xml as the combat code runs it (stage 5b, analysis/work/stage5/SPEC.md §5b.3; ARCHITECTURE.md §6.4),
/// numbered like <see cref="Game.SpellData"/> (<see cref="ClientNumbering.Spells"/>): the cast numbers of the element and its
/// effect children, kept as detached XML (<see cref="Effects"/>), which the spell executor walks tag by tag
/// (<see cref="SpellCasts"/>). Missing numbers read as 0 (the client's defaults for castingtime, hitdelay, recastdelay and
/// energyusage), a missing maxstack as 1. Immutable.
/// </summary>
public sealed class SpellTemplate
{
    /// <summary>
    /// The tags of the client's static tag→Type registry (bi1 dictionary, bi1.n3_3 0x1806b4100; stageD effects SPEC §0.1,
    /// classtags.txt, 131 tags): only a direct child with one of these tags gets a client effect index. Every other child
    /// (projectile, fofIndicator, levitate, attackprojectileoverride, the visuals, sound and texts, the channelling child) gets none.
    /// </summary>
    public static readonly IReadOnlySet<string> Registered = new HashSet<string>(StringComparer.Ordinal)
    {
        "IfCharge", "activaterandomdungeon", "adddestinyboardfishingfame", "adddestinyboardgatheringfame",
        "adddestinyboardpvefame", "additionaldamage", "addvanityconsumablecharges", "applyeffectoneventactive",
        "applyeffectoneventpassive", "applyrandomspell", "applyspell", "attributechangeovertime", "aura", "awardcraftingfocus",
        "awardlearningpoints", "awardpremiumdays", "awardsilver", "awardstanding", "buff", "buffovertime", "cancelmultispell",
        "cannotaggro", "cannotattack", "cannotattackplayers", "cannotenterhideout", "cannotlootplayers", "cannotreceivehealing",
        "cannotuseexit", "cannotuseobject", "castspellafterdistancemoved", "castspellaftermoving", "cceffectimmunity",
        "chainspell", "changeaggro", "changeequipment", "changeflagging", "claimloresnippet", "clamphitpoints",
        "clearvariableseffectinfo", "coupdegrace", "damageshield", "dash", "destroybuilding", "directattributechange",
        "dismount", "drag", "dropcarriableobject", "dummy", "dummypassive", "endtransformation", "enterobjecttimeoverride",
        "escapedungeon", "findtrack", "forcedmovement", "gainenergyondamage", "generateloot", "healonhit", "healonhitpassive",
        "hideharvestable", "interruptcast", "invincibility", "invisibility", "killmob", "knockback", "leaveisland", "link",
        "maketargettopthreat", "marker", "matchhighestthreatlevel", "mobdamagemodifier", "mobfamebycategoryovertime",
        "mobhealthmodifier", "movefreely", "multispell", "nop", "nothittable", "nothittablebyothers", "notinterruptible",
        "notselectable", "playertracking", "portalrestriction", "pull", "pulsingspell", "pulsingspellpassive",
        "reducethreatlevelbypercentage", "reflectdamage", "reflectdamageactive", "removeactiveeffects", "removeactivespell",
        "removeeffectoneventactive", "removefromthreatlist", "removespelleffectareasaffectingtarget",
        "removespelleffectareasinarea", "removespelleffectareasoftarget", "removestoredtargets", "repairequipment",
        "resetcooldowns", "resourcegatheringbuff", "resourcegatheringbuffovertime", "resurrect", "revivefrombleedout", "root",
        "setcarriable", "setcooldowns", "setcrowdcontroldiminishingreturns", "settargetforcasterownedmobs", "silence",
        "silverspawn", "smartclusterqueuejoin", "spawnmob", "spelleffectarea", "spellimmunity", "storage", "storetargets",
        "stun", "targetableonlyfrommarker", "targetsinarea", "teleport", "teleporttohome", "terminatetogglespells",
        "townportal", "transformation", "transparency", "triggerpvpchallenge", "turn", "unlockavatar", "unlockvanityunlock",
        "useamplifiedzergdebuff", "useobject", "weightbonus", "weightbonusovertime",
    };

    /// <summary>
    /// The timed effect classes (bkn subclasses, effects-verify/bkn_verified.txt, 59 tags): only their sub-effects can be
    /// Active, i.e. listed with key-7 bit 0 in ActiveSpellEffectsUpdate (SPEC §2). Every other class takes 000.
    /// </summary>
    public static readonly IReadOnlySet<string> Timed = new HashSet<string>(StringComparer.Ordinal)
    {
        "activaterandomdungeon", "applyeffectoneventactive", "attributechangeovertime", "aura", "buffovertime", "cannotaggro",
        "cannotattack", "cannotattackplayers", "cannotenterhideout", "cannotlootplayers", "cannotreceivehealing",
        "cannotuseexit", "cannotuseobject", "castspellafterdistancemoved", "castspellaftermoving", "cceffectimmunity",
        "clamphitpoints", "damageshield", "dash", "drag", "dummy", "enterobjecttimeoverride", "forcedmovement",
        "gainenergyondamage", "healonhit", "invincibility", "invisibility", "knockback", "link", "marker", "mobdamagemodifier",
        "mobfamebycategoryovertime", "mobhealthmodifier", "movefreely", "nothittable", "nothittablebyothers",
        "notinterruptible", "notselectable", "playertracking", "portalrestriction", "pull", "pulsingspell",
        "reflectdamageactive", "removeeffectoneventactive", "resourcegatheringbuffovertime", "root", "silence", "spellimmunity",
        "storage", "stun", "targetableonlyfrommarker", "targetsinarea", "teleport", "teleporttohome", "transformation",
        "transparency", "turn", "useamplifiedzergdebuff", "weightbonusovertime",
    };

    /// <summary>The spell id (<see cref="ClientNumbering.Spells"/>): CastStart key 4, CastFinished key 2, HealthUpdate key 7.</summary>
    public required short Id { get; init; }

    public required string Name { get; init; }

    /// <summary>The element: activespell, passivespell, togglespell or channelingspell.</summary>
    public required string Kind { get; init; }

    /// <summary>target: enemy, self, ground, all, … (what CastStart key 5 or key 6 names).</summary>
    public string Target { get; init; } = "";

    /// <summary>castingtime in seconds; 0 = instant (the three result events go out in the request's tick, SPEC §5b.4).</summary>
    public float CastingTime { get; init; }

    /// <summary>hitdelay: seconds from the cast end to the hit (before the projectile's flight).</summary>
    public float HitDelay { get; init; }

    /// <summary>recastdelay: the cooldown in seconds, times CastFinished key 6 (1.0 here, SPEC §5b.3).</summary>
    public float RecastDelay { get; init; }

    /// <summary>standtime: seconds a mob stands still after the cast (or its channel) ends before it walks, swings or casts again (stageD mobspells R4; 0 when missing).</summary>
    public float StandTime { get; init; }

    /// <summary>adjustgroundtargettorange="true": a ground point beyond castrange is pulled in to it (mob casts, stageD mobspells §4.2).</summary>
    public bool AdjustGroundTargetToRange { get; init; }

    /// <summary>energyusage; a share of the caster's max energy when <see cref="EnergyUsageIsRelative"/> (LIFESAVIOR 0.15 → 44 of 293).</summary>
    public float EnergyUsage { get; init; }

    public bool EnergyUsageIsRelative { get; init; }

    /// <summary>castrange in metres (the 1 m tolerance of SPEC §5b.1 comes on top).</summary>
    public float CastRange { get; init; }

    /// <summary>maxstack: how many of its over-time effects may run on one target at once (1 when missing).</summary>
    public int MaxStack { get; init; } = 1;

    /// <summary>
    /// How high ActiveSpellEffectsUpdate key 5 (the stacks of one caster's entry) goes on a target (SPEC §1): maxstack, else
    /// maxcharges for a charge spell without one (PASSIVE_AA_STACK 20, ENRAGE_BUFF), else 1.
    /// </summary>
    public int StackLimit { get; init; } = 1;

    /// <summary>startcharges: the stacks a new entry starts with (FISHINGBAIT_LEVEL1_CHARGE 10, econ SPEC §2.2); 1 when missing.</summary>
    public int StartCharges { get; init; } = 1;

    /// <summary>category: damage, buff, debuff, foodbuff, instant, … (spellimmunity category, removeactiveeffects).</summary>
    public string Category { get; init; } = "";

    /// <summary>
    /// usableincombat (default true): false = refused while its caster is in combat (OUTOFCOMBATHEAL, VANITY_RUBBERBANDING; stageM bots:
    /// <see cref="Systems.CastRefusal.InCombat"/>, and a bot never picks it).
    /// </summary>
    public bool UsableInCombat { get; init; } = true;

    /// <summary>uitype: damage, crowdcontrol, movement, buff, heal, … (the client's spell-book filter; stageM bots: the kit choice of a bot's armour).</summary>
    public string UiType { get; init; } = "";

    /// <summary>castablewhensilenced="true": a silence does not block its CastStart.</summary>
    public bool CastableWhenSilenced { get; init; }

    /// <summary>castablewhenstunned="true" (the cleanses, ICEBLOCK2, HOLYTOUCH; stageJ pvp-spells phase 0): a stun does not block its CastStart.</summary>
    public bool CastableWhenStunned { get; init; }

    /// <summary>interruptiblebyspell (default true): false = an interruptcast does not cancel its cast (UNDEAD_NECROMANCER_SUMMON_*).</summary>
    public bool InterruptibleBySpell { get; init; } = true;

    /// <summary>storedtargettag of a target="storedtarget" spell (AFTER_IMAGE_RETURN IMAGE): the tag of the caster's stored target it is cast at; null = any.</summary>
    public string? StoredTargetTag { get; init; }

    /// <summary>flyspeed of its &lt;projectile&gt; (m/s; FIRESTAFFBOLT2 85); null = no projectile, the hit comes after the hit delay alone.</summary>
    public float? ProjectileSpeed { get; init; }

    /// <summary>
    /// The effect children in document order, detached: exactly the client's effect list (bi1.d4), the children whose tag is
    /// <see cref="Registered"/>, so index i here is the client effect index i (ActiveSpellEffectsUpdate key 7 groups,
    /// NewSpellEffectArea key 5, ForcedMovement key 8). The projectile is read into <see cref="ProjectileSpeed"/> instead.
    /// </summary>
    public IReadOnlyList<XElement> Effects { get; init; } = [];

    /// <summary>
    /// The spell has a &lt;channelingspell&gt; child (PARRY, BLOCK, OUTOFCOMBATHEAL, PYROBLAST_SKILLSHOT): the channel is the next
    /// spell id (PARENT_CHANNELING, <see cref="ClientNumbering.Spells"/>) and runs after the cast (stageD effects SPEC §7 step 9).
    /// </summary>
    public bool HasChannel { get; init; }

    /// <summary>A channelingspell's own numbers (its element's attributes); null for every other kind.</summary>
    public ChannelInfo? Channel { get; init; }

    /// <summary>
    /// The type of its &lt;useobject&gt; effect (bna.UseObjectTypes: lootchest, shrine, corrupteddungeonexitdifficulty1-3, mistsentrance), lower
    /// case; null without one. The allowfakeslot spells the client casts in slot 25 at an object (stageM chest-slot25 NOTES).
    /// </summary>
    public string? UseObject => Effects.FirstOrDefault(e => e.Name.LocalName == "useobject")?.Attribute("type")?.Value.ToLowerInvariant();

    /// <summary>The template of spells.xml element <paramref name="e"/> with id <paramref name="id"/>.</summary>
    public static SpellTemplate From(short id, string name, string kind, XElement e)
    {
        var projectile = e.Element("projectile");
        return new SpellTemplate
        {
            Id = id,
            Name = name,
            Kind = kind,
            Target = (string?)e.Attribute("target") ?? "",
            CastingTime = Float(e, "castingtime"),
            HitDelay = Float(e, "hitdelay"),
            RecastDelay = Float(e, "recastdelay"),
            StandTime = Float(e, "standtime"),
            AdjustGroundTargetToRange = Bool(e, "adjustgroundtargettorange"),
            EnergyUsage = Float(e, "energyusage"),
            EnergyUsageIsRelative = string.Equals((string?)e.Attribute("energyusageisrelative"), "true", StringComparison.OrdinalIgnoreCase),
            CastRange = Float(e, "castrange"),
            MaxStack = Math.Max(1, (int)Float(e, "maxstack", 1)),
            StackLimit = Math.Max(1, (int)(e.Attribute("maxstack") is not null ? Float(e, "maxstack", 1) : Float(e, "maxcharges", 1))),
            StartCharges = Math.Max(1, (int)Float(e, "startcharges", 1)),
            Category = (string?)e.Attribute("category") ?? "",
            InterruptibleBySpell = Bool(e, "interruptiblebyspell", true),
            StoredTargetTag = (string?)e.Attribute("storedtargettag") is { Length: > 0 } stored ? stored : null,
            UsableInCombat = Bool(e, "usableincombat", true),
            UiType = (string?)e.Attribute("uitype") ?? "",
            CastableWhenSilenced = Bool(e, "castablewhensilenced"),
            CastableWhenStunned = Bool(e, "castablewhenstunned"),
            ProjectileSpeed = projectile is not null && Float(projectile, "flyspeed") is > 0 and var speed ? speed : null,
            HasChannel = e.Element("channelingspell") is not null,
            Channel = kind == "channelingspell" ? ChannelInfo.From(e) : null,
            // A channelling child is a spell of its own (the next id) and not registered: no effect of this one.
            Effects = [.. e.Elements().Where(c => Registered.Contains(c.Name.LocalName)).Select(c => new XElement(c))],
        };
    }

    /// <summary>A float attribute of a spells.xml element; missing or unreadable = <paramref name="fallback"/>.</summary>
    public static float Float(XElement e, string name, float fallback = 0) =>
        (string?)e.Attribute(name) is { Length: > 0 } v && float.TryParse(v.Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out var f) ? f : fallback;

    /// <summary>A "true"/"false" attribute (any case); missing = <paramref name="fallback"/>.</summary>
    public static bool Bool(XElement e, string name, bool fallback = false) =>
        (string?)e.Attribute(name) is { } v ? string.Equals(v.Trim(), "true", StringComparison.OrdinalIgnoreCase) : fallback;

    public override string ToString() => $"{Name} ({Id})";
}

/// <summary>
/// A channelingspell element (stageD effects SPEC §7 step 9): effectcount ticks, the first initialeffectinterval after the
/// cast end, then every effectinterval; the end (ChannelingEnded) at the last tick. allowmovement="false": a Move that walks
/// cancels it; allowcancel (default true): CastCancel ends it; damagecancelscasting: damage ends it. endeffect: the spell
/// run at the end (on the caster), unless cancelled with cancelpreventsendeffects or interrupted with interruptpreventsendeffects.
/// </summary>
public sealed record ChannelInfo(int Count, float Initial, float Interval, bool AllowMovement, bool AllowCancel, bool DamageCancels, string? EndEffect,
    bool CancelPreventsEnd, bool InterruptPreventsEnd)
{
    public static ChannelInfo From(XElement e) => new(
        Math.Max(1, (int)SpellTemplate.Float(e, "effectcount", 1)), SpellTemplate.Float(e, "initialeffectinterval"), SpellTemplate.Float(e, "effectinterval"),
        SpellTemplate.Bool(e, "allowmovement", true), SpellTemplate.Bool(e, "allowcancel", true), SpellTemplate.Bool(e, "damagecancelscasting"),
        (string?)e.Attribute("endeffect"), SpellTemplate.Bool(e, "cancelpreventsendeffects"), SpellTemplate.Bool(e, "interruptpreventsendeffects"));

    /// <summary>Seconds from the cast end to the last tick: initialeffectinterval + (effectcount − 1) × effectinterval (PARRY 0.8 s, PYROBLAST 1.2 s).</summary>
    public double Seconds => Initial + (Count - 1) * Interval;
}
