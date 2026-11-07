using System.Xml.Linq;

namespace AlbionLocal.Server.Data;

/// <summary>PvpRules (bgu+0x18, stageE pvp SPEC §0): the archetype's pvprule.</summary>
public enum PvpRule : byte
{
    PvpDisabled = 0,
    FactionPvpOnly = 1,
    PvpAllowed = 2,
    PvpForced = 3,
    None = 4,
}

/// <summary>PlayerTradeRule (bgu+0x24, stageJ trade SPEC §1.3): the archetype's allowtrade.</summary>
public enum PlayerTradeRule : byte
{
    Allowed = 0,
    Disabled = 1,
    GroupOnly = 2,
}

/// <summary>
/// The PvP fields of a cluster's worldsettings.xml archetype (stageE pvp SPEC §1.1, bgu.av 0x18068B260), with the client
/// parser's defaults for a missing attribute: pvprule (PvpAllowed), playersarelethal (true), allowduel (true),
/// allowfactionflagging (true), ignoreguilds (false), outlawbuffenabled (true), knockdowngetuptimeseconds_pvp (none: the
/// gamedata DeathSettings value), knockdownbuffaftergetup (none: DeathSettings'), requiredreputationtoenter (none),
/// allowfactionflaggedenter (true; CITY-START and CITY-BLACK false: no faction-flagged player may come in, SPEC §1.1 V10),
/// reputationgainenabled (true; false only on DRAGON_RANDOM_DUNGEON_BLACK: no reputation from mobs and harvests there),
/// pvpwarningtype (safe; "orange" only on ORANGE: its deaths keep the equipment, stageE pvp SPEC §4.4).
/// </summary>
public sealed record ClusterPvp(PvpRule Rule, bool PlayersAreLethal = true, bool AllowDuel = true, bool AllowFactionFlagging = true,
    bool IgnoreGuilds = false, bool OutlawBuffEnabled = true, double? KnockdownSecondsPvp = null, string? KnockdownBuffAfterGetUp = null,
    int? RequiredReputationToEnter = null, bool AllowFactionFlaggedEnter = true, bool ReputationGainEnabled = true, string WarningType = "safe")
{
    /// <summary>An orange zone (pvpwarningtype orange): a death there uses the *Orange item loss events (SPEC §4.4).</summary>
    public bool Orange => WarningType == "orange";

    /// <summary>
    /// A death here loses no items (stageE E3, Death.Kill) though playersarelethal: the arenas (ARENA_*, whose teams and matches
    /// the client handles through state not modelled here, ctp.af_21 +0x4c0) and <see cref="Default"/> [infer].
    /// </summary>
    public bool NoDeathLoss { get; init; }

    /// <summary>
    /// allowtrade (stageJ trade SPEC §1.3; Allowed without it): Disabled in the hellgates, corrupted dungeons, the hardcore expeditions,
    /// the 1v1 arena and most of the Mists; GroupOnly (trades only within one party) in HELL_GREEN and the dragon areas.
    /// </summary>
    public PlayerTradeRule AllowTrade { get; init; }

    /// <summary>
    /// allowequipmentswapping (true without it): false in the corrupted dungeons (stageL corrupted SPEC §1.3) and the hellgates: no item
    /// goes into or out of the equipment there (Handlers/World/ItemHandler).
    /// </summary>
    public bool AllowEquipmentSwapping { get; init; } = true;

    /// <summary>
    /// allowmounts of the clustertype itself, not of its archetype (true without it; <see cref="WorldArchetypes.PvpOf"/>): false in 80 cluster types,
    /// every dungeon, the expeditions (T3-T6_EXPEDITION_*, HARDCORE_EXPEDITION_*), the arenas, the corrupted and hell dungeons, the mists dungeons
    /// and the dragon areas: no mount there (<see cref="Systems.Mounts.Check"/>), and a character that comes in mounted arrives on foot
    /// (<see cref="Systems.Mounts.Restore"/>; the client offers no dismount there, public issue #8).
    /// </summary>
    public bool AllowMounts { get; init; } = true;

    /// <summary>
    /// An archetype with no attributes at all (the client's defaults), as for a cluster type worldsettings.xml does not name:
    /// lethal, but without the full loot of a named lethal zone (<see cref="NoDeathLoss"/>).
    /// </summary>
    public static ClusterPvp Default { get; } = new(PvpRule.PvpAllowed) { NoDeathLoss = true };

    /// <summary>
    /// The fail-safe rules of a cluster whose archetype is not known: its template still loading or failed, or worldsettings.xml
    /// not read (no PvP, no duels and no trades, so a flagged player cannot attack anyone in a city that has not resolved).
    /// </summary>
    public static ClusterPvp Unresolved { get; } = new(PvpRule.PvpDisabled, PlayersAreLethal: false, AllowDuel: false) { AllowTrade = PlayerTradeRule.Disabled };

    /// <summary>A city's rules (CITY-*: PvpDisabled, not lethal): the self-test's map without cluster data.</summary>
    public static ClusterPvp City { get; } = new(PvpRule.PvpDisabled, PlayersAreLethal: false);

    /// <summary>Reads <paramref name="archetype"/> named <paramref name="name"/> (null = <see cref="Default"/>).</summary>
    public static ClusterPvp Of(XElement? archetype, string? name = null)
    {
        if (archetype is null) return Default;
        var rule = Enum.TryParse<PvpRule>((string?)archetype.Attribute("pvprule"), ignoreCase: true, out var r) && Enum.IsDefined(r) ? r : PvpRule.PvpAllowed;
        var seconds = XmlAttr.Int(archetype, "knockdowngetuptimeseconds_pvp", -1);
        var reputation = archetype.Attribute("requiredreputationtoenter") is null ? (int?)null : XmlAttr.Int(archetype, "requiredreputationtoenter", 0);
        return new ClusterPvp(rule,
            XmlAttr.Bool(archetype, "playersarelethal", true),
            XmlAttr.Bool(archetype, "allowduel", true),
            XmlAttr.Bool(archetype, "allowfactionflagging", true),
            XmlAttr.Bool(archetype, "ignoreguilds", false),
            XmlAttr.Bool(archetype, "outlawbuffenabled", true),
            seconds >= 0 ? seconds : null,
            (string?)archetype.Attribute("knockdownbuffaftergetup") is { Length: > 0 } buff ? buff : null,
            reputation,
            XmlAttr.Bool(archetype, "allowfactionflaggedenter", true),
            XmlAttr.Bool(archetype, "reputationgainenabled", true),
            (string?)archetype.Attribute("pvpwarningtype") is { Length: > 0 } warning ? warning : "safe")
        {
            NoDeathLoss = name?.StartsWith("ARENA_", StringComparison.Ordinal) == true,
            AllowTrade = Enum.TryParse<PlayerTradeRule>((string?)archetype.Attribute("allowtrade"), ignoreCase: true, out var t) && Enum.IsDefined(t) ? t : PlayerTradeRule.Allowed,
            AllowEquipmentSwapping = XmlAttr.Bool(archetype, "allowequipmentswapping", true),
        };
    }
}
