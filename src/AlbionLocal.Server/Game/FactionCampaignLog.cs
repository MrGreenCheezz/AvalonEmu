namespace AlbionLocal.Server.Game;

/// <summary>
/// StageR fw-campaign (analysis/work/stageR/fw-campaign/SPEC.md §2): a character's faction warfare campaign, kept in the goals JSON (no schema
/// step): the faction points (a7m raw, after the rank's multiplier) earned in the current UTC day, campaign month and week, the daily
/// rewards given today, and the campaign rewards' states this month (1 unlocked, 3 claimed; Systems.FactionCampaign).
/// </summary>
public sealed class FactionCampaignLog
{
    /// <summary>The UTC day (days since 0001-01-01) <see cref="DayPoints"/> and <see cref="DayRewards"/> count for.</summary>
    public int Day { get; set; }

    public long DayPoints { get; set; }

    /// <summary>The daily reward ids given in <see cref="Day"/>.</summary>
    public HashSet<string> DayRewards { get; set; } = new(StringComparer.Ordinal);

    /// <summary>The campaign month (months since 0001-01, GetFactionWarfareCampaignData key 4) <see cref="MonthPoints"/> and <see cref="Rewards"/> count for.</summary>
    public int Month { get; set; }

    public long MonthPoints { get; set; }

    /// <summary>The campaign rewards of <see cref="Month"/> that are not LOCKED: 1 unlocked (told with 484), 3 claimed.</summary>
    public Dictionary<string, int> Rewards { get; set; } = new(StringComparer.Ordinal);

    /// <summary>The week (the client's dex.z: Monday-based weeks since 0001-01-01) <see cref="WeekPoints"/> counts for.</summary>
    public int Week { get; set; }

    public long WeekPoints { get; set; }

    /// <summary>The points of the week before <see cref="Week"/> (the weekly report), and the faction they went to.</summary>
    public long LastWeekPoints { get; set; }

    public byte LastWeekFaction { get; set; }

    /// <summary>The faction <see cref="WeekPoints"/> went to (the last one that earned).</summary>
    public byte WeekFaction { get; set; }

    public FactionCampaignLog Clone() => new()
    {
        Day = Day, DayPoints = DayPoints, DayRewards = new(DayRewards, StringComparer.Ordinal), Month = Month, MonthPoints = MonthPoints,
        Rewards = new(Rewards, StringComparer.Ordinal), Week = Week, WeekPoints = WeekPoints, LastWeekPoints = LastWeekPoints,
        LastWeekFaction = LastWeekFaction, WeekFaction = WeekFaction,
    };
}
