using System.Globalization;

namespace AlbionLocal.Server;

/// <summary>FactionWarfare:Claimable: which faction warfare zones' towers can be taken.</summary>
public enum OutpostClaimRule
{
    /// <summary>
    /// The zones on the front line: a warfare zone with an exit to a warfare zone (or city) another faction controls, ClusterPositionState
    /// FrontLine; the others are BackLine, their towers Unclaimable (the client's @OUTPOST_CLAIM_ERROR_NOT_A_FRONTLINE_CLUSTER) [infer: the
    /// official front line follows the campaign's duchy conflicts, which are not known].
    /// </summary>
    Frontline,

    /// <summary>Every warfare zone is on the front line.</summary>
    All,

    /// <summary>No tower can be taken (they stand, Unclaimable, as the captured calm 1207).</summary>
    None,
}

/// <summary>FactionWarfare:Camps: which faction warfare zones' rolled mob camps are faction camps (stageQ, <see cref="Systems.FactionCamps"/>).</summary>
public enum FactionCampRule
{
    /// <summary>
    /// The zones of a duchy in conflict (factionwarfare.xml initialconflict: YELLOW_03, 06, 09, 10, 13 and the red duchies) [infer: in the captures
    /// 1207 (YELLOW_10, at war with YELLOW_15, FactionWarfareClusterState key 7 true, key 9 Mountain) had three open faction camp quests at the Join,
    /// 0204 (YELLOW_02, no conflict) a Default camp (DynamicTemplate Active / Default); the official conflicts move with the campaign].
    /// </summary>
    Conflict,

    /// <summary>Every faction warfare zone (FW_*_SAFE_n, FW_LETHAL_n).</summary>
    All,

    /// <summary>No zone: every rolled camp stays a Default one.</summary>
    None,
}

/// <summary>FactionWarfare:RedZoneEvents: when the bandit assault (stageR fw-zone, <see cref="Systems.RedZoneEvents"/>) starts.</summary>
public enum RedZoneEventRule
{
    /// <summary>Official: at each eventstarttime of factionwarfareredzoneevent.xml, rolled with its chance.</summary>
    Scheduled,

    /// <summary>At every eventstarttime.</summary>
    Always,

    /// <summary>Only by the GM command .redzone start.</summary>
    None,
}

/// <summary>FactionWarfare:RedZoneRegion: which region's event and times files the bandit assault follows.</summary>
public enum RedZoneRegion
{
    /// <summary>factionwarfareredzoneevent.xml and times.xml (the client's default files).</summary>
    Default,

    /// <summary>The _europe files.</summary>
    Europe,

    /// <summary>The _asia files.</summary>
    Asia,
}

/// <summary>
/// The faction warfare towers and camps (FactionWarfare:*; analysis/work/stageQ/outposts and camps, <see cref="Systems.Outposts"/>,
/// <see cref="Systems.FactionCamps"/>): defaults = Preset official,
/// <see cref="PveDefaults"/> the pve preset's (the same).
/// </summary>
public sealed record FactionWarfareOptions
{
    /// <summary>FactionWarfare:Outposts (true): the towers of the warfare zones stand (NewOutpostObject 365); false = none is sent.</summary>
    public bool Outposts { get; init; } = true;

    /// <summary>FactionWarfare:OutpostGuards (true): each tower's outpost.xml roster (1 boss + 7 guards of its owner) stands and fights the owner's enemies.</summary>
    public bool OutpostGuards { get; init; } = true;

    /// <summary>FactionWarfare:Claimable (Frontline): which zones' towers can be taken (<see cref="OutpostClaimRule"/>).</summary>
    public OutpostClaimRule Claimable { get; init; } = OutpostClaimRule.Frontline;

    /// <summary>
    /// FactionWarfare:ControlSeconds (560): how long a faction must hold more than half of a zone's towers before it controls the zone
    /// (FactionWarfareClusterState key 4) [infer: the one captured flip came 557 s after the majority].
    /// </summary>
    public double ControlSeconds { get; init; } = 560;

    /// <summary>
    /// FactionWarfare:Camps (Conflict): which zones' rolled mob camps show their faction warfare layers (<see cref="FactionCampRule"/>): Caerleon guards
    /// that fight flagged players, an open quest and a shared chest that unlocks when the last of them dies; then the camp goes inactive and stands again
    /// later (factionwarfare.xml). Read when a cluster is resolved, so a change needs a restart.
    /// </summary>
    public FactionCampRule Camps { get; init; } = FactionCampRule.Conflict;

    /// <summary>
    /// FactionWarfare:CampaignRewardsWithoutPremium (false): the campaign chests (factionwarfare.xml campaignrewards, boughtpremium only: the client
    /// lets only a character with premium of more than 6 days claim them) are claimable without premium too (stageR fw-campaign, <see cref="Systems.FactionCampaign"/>).
    /// </summary>
    public bool CampaignRewardsWithoutPremium { get; init; }

    /// <summary>FactionWarfare:Fortresses (true): the faction fortresses of the warfare duchies stand (NewFactionFortressObject 652) and can be besieged (stageR fw-zone).</summary>
    public bool Fortresses { get; init; } = true;

    /// <summary>FactionWarfare:FortressGuards (true): each fortress tower's 9 guards of its owner (factionfortress.xml fortressguards) stand and fight the owner's enemies.</summary>
    public bool FortressGuards { get; init; } = true;

    /// <summary>FactionWarfare:RedZoneEvents (Scheduled): when the bandit assault in the red duchies starts (<see cref="RedZoneEventRule"/>).</summary>
    public RedZoneEventRule RedZoneEvents { get; init; } = RedZoneEventRule.Scheduled;

    /// <summary>FactionWarfare:RedZoneRegion (Default): the region whose event and times files the bandit assault follows (<see cref="RedZoneRegion"/>).</summary>
    public RedZoneRegion RedZoneRegion { get; init; } = RedZoneRegion.Default;

    /// <summary>The pve preset's values (the same as official).</summary>
    public static readonly FactionWarfareOptions PveDefaults = new();

    /// <summary>The FactionWarfare section out of <paramref name="values"/> over <paramref name="d"/>.</summary>
    internal static FactionWarfareOptions Load(Dictionary<string, string> values, FactionWarfareOptions d)
    {
        var inv = CultureInfo.InvariantCulture;
        string? Take(string key) => values.Remove(key, out var v) ? v : null;
        bool Flag(string key, bool fallback) => Take(key) is not { } v ? fallback
            : bool.TryParse(v, out var f) ? f : throw new InvalidDataException($"{key}: '{v}' is not true or false");
        double Number(string key, double fallback, double min, double max) => Take(key) is not { } v ? fallback
            : double.TryParse(v, NumberStyles.Float, inv, out var n) && double.IsFinite(n) && n >= min && n <= max ? n
            : throw new InvalidDataException($"{key}: '{v}' is not {min.ToString(inv)}..{max.ToString(inv)}");
        var rule = Take("FactionWarfare:Claimable");
        var camps = Take("FactionWarfare:Camps");
        var rze = Take("FactionWarfare:RedZoneEvents");
        var region = Take("FactionWarfare:RedZoneRegion");
        return new FactionWarfareOptions
        {
            Outposts = Flag("FactionWarfare:Outposts", d.Outposts),
            OutpostGuards = Flag("FactionWarfare:OutpostGuards", d.OutpostGuards),
            Claimable = rule is null ? d.Claimable : Enum.TryParse<OutpostClaimRule>(rule, ignoreCase: true, out var r) && Enum.IsDefined(r) ? r
                : throw new InvalidDataException($"FactionWarfare:Claimable: '{rule}' is not Frontline, All or None"),
            ControlSeconds = Number("FactionWarfare:ControlSeconds", d.ControlSeconds, 0, 86_400),
            Camps = camps is null ? d.Camps : Enum.TryParse<FactionCampRule>(camps, ignoreCase: true, out var c) && Enum.IsDefined(c) ? c
                : throw new InvalidDataException($"FactionWarfare:Camps: '{camps}' is not Conflict, All or None"),
            CampaignRewardsWithoutPremium = Flag("FactionWarfare:CampaignRewardsWithoutPremium", d.CampaignRewardsWithoutPremium),
            Fortresses = Flag("FactionWarfare:Fortresses", d.Fortresses),
            FortressGuards = Flag("FactionWarfare:FortressGuards", d.FortressGuards),
            RedZoneEvents = rze is null ? d.RedZoneEvents : Enum.TryParse<RedZoneEventRule>(rze, ignoreCase: true, out var z) && Enum.IsDefined(z) ? z
                : throw new InvalidDataException($"FactionWarfare:RedZoneEvents: '{rze}' is not Scheduled, Always or None"),
            RedZoneRegion = region is null ? d.RedZoneRegion : Enum.TryParse<RedZoneRegion>(region, ignoreCase: true, out var g) && Enum.IsDefined(g) ? g
                : throw new InvalidDataException($"FactionWarfare:RedZoneRegion: '{region}' is not Default, Europe or Asia"),
        };
    }
}
