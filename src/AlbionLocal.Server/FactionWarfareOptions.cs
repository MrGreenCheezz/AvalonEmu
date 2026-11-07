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

/// <summary>
/// The faction warfare towers (FactionWarfare:*; analysis/work/stageQ/outposts, <see cref="Systems.Outposts"/>): defaults = Preset official,
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
        return new FactionWarfareOptions
        {
            Outposts = Flag("FactionWarfare:Outposts", d.Outposts),
            OutpostGuards = Flag("FactionWarfare:OutpostGuards", d.OutpostGuards),
            Claimable = rule is null ? d.Claimable : Enum.TryParse<OutpostClaimRule>(rule, ignoreCase: true, out var r) && Enum.IsDefined(r) ? r
                : throw new InvalidDataException($"FactionWarfare:Claimable: '{rule}' is not Frontline, All or None"),
            ControlSeconds = Number("FactionWarfare:ControlSeconds", d.ControlSeconds, 0, 86_400),
        };
    }
}
