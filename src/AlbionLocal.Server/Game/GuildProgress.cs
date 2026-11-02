namespace AlbionLocal.Server.Game;

/// <summary>
/// The guild goals' state of a guild (stageI guild-goals SPEC §2.7, §2.8; the guilds row's progress column, JSON, saved with the account
/// every 30 s): its guild seasons (the current one and a few before it), the weekly goals of this week and the last, the members'
/// weekly logs of the Member Logs window (309) and the season-end member rewards not given yet (§2.6).
/// </summary>
public sealed class GuildProgress
{
    /// <summary>Native new-member mail (479/480), kept in the existing guild JSON. Empty disables the welcome.</summary>
    public string NewMemberMail { get; set; } = "";

    /// <summary>Recruitment configuration (184/185); null in older saves means not configured.</summary>
    public GuildRecruitmentSettings? Recruitment { get; set; }

    /// <summary>485's ordered optional-stat slots, including None and duplicates; null in older saves means unset.
    /// Kept separately because184 replaces Recruitment without changing these slots.</summary>
    public int[]? OptionalStats { get; set; }

    /// <summary>Appearance's512-character guild codex (236), including its actual last editor and commit time.</summary>
    public string Codex { get; set; } = "";
    public Guid CodexAuthor { get; set; }
    public string CodexAuthorName { get; set; } = "";
    public long CodexUpdatedAt { get; set; }

    /// <summary>481/482: the guild's displayed preference,0 unset,1..6 faction. It does not enlist any character.</summary>
    public byte FactionAllegiance { get; set; }

    /// <summary>The guild's seasons, the newest last (at most <see cref="Systems.GuildGoals.SeasonsKept"/>).</summary>
    public List<GuildSeasonState> Seasons { get; set; } = [];

    /// <summary>This week's goals; null = none rolled yet.</summary>
    public GuildWeek? Week { get; set; }

    /// <summary>The week before (the blessing's source and .guild goals' "last week").</summary>
    public GuildWeek? LastWeek { get; set; }

    /// <summary>The Member Logs per member (QueryGuildPlayerStats, op 309).</summary>
    public Dictionary<Guid, GuildMemberLog> Logs { get; set; } = [];

    /// <summary>The season-end member rewards per member not given yet (an offline member's, given at its next login; paid once).</summary>
    public Dictionary<Guid, List<GuildMemberReward>> Owed { get; set; } = [];

    /// <summary>Committed account movements, oldest first (GetGuildAccountLogs, op 159). Existing balances have no invented history.</summary>
    public List<GuildAccountTransaction> AccountTransactions { get; set; } = [];

    /// <summary>
    /// QueryAllianceGuildStats (310): points observed since this counter was introduced. Null in older saves; retained season
    /// totals cannot reconstruct either a lifetime total (only four seasons are kept) or their weekly distribution.
    /// </summary>
    public GuildSeasonPointLog? SeasonPointLog { get; set; }
}

/// <summary>The native recruitment editor's own values, kept with the guild's existing JSON.
/// This does not register a public Guild Finder listing or manufacture guild activity statistics.</summary>
public sealed class GuildRecruitmentSettings
{
    public byte PrimaryLanguage { get; set; }
    public byte SecondaryLanguage { get; set; }
    public string Description { get; set; } = "";
    public Guid Author { get; set; }
    public string AuthorName { get; set; } = "";
    public byte JoinSettings { get; set; }
    public bool BeginnerFriendly { get; set; }
    public string DiscordLink { get; set; } = "";
    public byte Home { get; set; }
    public short ActiveFrom { get; set; } = -1;
    public short ActiveTo { get; set; } = -1;
    public int Activities { get; set; }
    public long MinimumFame { get; set; }
    public long MinimumPvpFame { get; set; }
}

/// <summary>
/// Prospective guild season points, in whole points. All is the total since CoveredSinceUtc, not an invented lifetime total;
/// the first recorded week may be partial. Stored in the same progress JSON as the season totals, with no schema migration.
/// </summary>
public sealed class GuildSeasonPointLog
{
    public long CoveredSinceUtc { get; set; }

    /// <summary>Monday at Bounties:ResetUtc, matching the guild's Member Logs.</summary>
    public long Week { get; set; }

    public long This { get; set; }

    public long Last { get; set; }

    public long All { get; set; }
}

/// <summary>A recorded guild account movement: a7m amount, UTC ticks, and the native reason/localization parameter.</summary>
public sealed class GuildAccountTransaction
{
    public Guid Character { get; set; }
    public string PlayerName { get; set; } = "";
    public int LogType { get; set; }
    public int Reason { get; set; }
    public string Parameter { get; set; } = "";
    public long Amount { get; set; }
    public long At { get; set; }
}

/// <summary>A season-end member reward (SPEC §2.6): the season it closes and the guild's bracket (iron … crystal: the items and the journal's gotguildmemberreward).</summary>
public sealed class GuildMemberReward
{
    public string Season { get; set; } = "";

    public string Bracket { get; set; } = "";
}

/// <summary>
/// The guild might of one category in a season (SPEC §1.7, phase 4): the members' might summed (a7m raw; a member who left keeps nothing
/// in the list, the total keeps his might), the levels reached (the client's level: progression rows reached) and each member's might.
/// </summary>
public sealed class GuildMight
{
    public long Total { get; set; }

    public int Level { get; set; }

    public Dictionary<Guid, long> Members { get; set; } = [];
}

/// <summary>
/// The Guild Challenge and season points of a guild in one season (SPEC §2.2, §2.4): the challenge points (whole points, the 390 total),
/// the level reached, the guild season points with their activity breakdown (GvgSeasonActivity names, 447), the challenge points each
/// member gave (390's rows; a member who left keeps nothing in the list, the total keeps his points), the fraction of a challenge point
/// not yet whole (small gains add up), the GameTimeStamp of the last change (390 key 1: the client refills its bar only when it changes)
/// and the one of the last season points gain (152's ties: the guild that reached a score first ranks higher); phase 4 the guild might per
/// category (450 / 449), and whether the season's end was paid (<see cref="Closed"/>, once).
/// </summary>
public sealed class GuildSeasonState
{
    public string Season { get; set; } = "";

    public long Challenge { get; set; }

    public int Level { get; set; } = 1;

    public long Points { get; set; }

    public Dictionary<string, long> Activity { get; set; } = [];

    public Dictionary<Guid, long> Members { get; set; } = [];

    public double Carry { get; set; }

    public long Updated { get; set; }

    public long PointsAt { get; set; }

    public Dictionary<string, GuildMight> Might { get; set; } = [];

    public bool Closed { get; set; }
}

/// <summary>
/// A guild's goals of one week (SPEC §2.3): the week's start (UTC ticks of Monday at the bounties' reset), its goals, the fame (a7m)
/// below a whole point per fame kind not yet counted, and the blessing steps it holds.
/// </summary>
public sealed class GuildWeek
{
    public long Start { get; set; }

    public List<GuildGoal> Goals { get; set; } = [];

    public Dictionary<string, long> Carry { get; set; } = [];

    /// <summary>The blessing of this week: the goals finished the week before (at most Guild:MaxBlessing).</summary>
    public int Blessing { get; set; }
}

/// <summary>One weekly guild goal: its pool and kind, the least tier it counts (0 = any), the target, the progress and whether it is done (paid once).</summary>
public sealed class GuildGoal
{
    public string Pool { get; set; } = "";

    public string Kind { get; set; } = "";

    public int Tier { get; set; }

    public long Target { get; set; }

    public long Progress { get; set; }

    public bool Done { get; set; }

    /// <summary>"kill 600 T5+ mobs" (the .guild goals line and the announcements).</summary>
    public string Describe() => Kind switch
    {
        "killmobs" => $"kill {Target} T{Tier}+ mobs",
        "bosses" => $"defeat {Target} bosses",
        "dungeons" => $"clear {Target} dungeons, expeditions or events",
        "pvpkills" => $"defeat {Target} enemies in PvP",
        "pvpfame" => $"earn {Target} PvP fame",
        "gather" => Tier > 0 ? $"gather {Target} T{Tier}+ resources" : $"gather {Target} resources",
        "fish" => $"catch {Target} fish",
        "craftfame" => $"earn {Target} crafting fame",
        _ => $"{Kind} {Target}",
    };
}

/// <summary>
/// A member's Member Logs (309, SPEC §2.5): the values of the 10 stats (bew 0-9, a7m raw) this week, the week before and all time, the
/// week they belong to (a gain in a later week moves them) and the highest map tier it played at this week and the week before (the
/// weekly goals' tier).
/// </summary>
public sealed class GuildMemberLog
{
    /// <summary>The bew stats of the window (GuildMemberLogSearchBar.Init order).</summary>
    public const int TaxPaid = 0, SilverBalance = 1, GatheringFame = 4, CraftingFame = 5, PveFame = 6, PvpFame = 7, Count = 10;

    public long Week { get; set; }

    public long[] This { get; set; } = new long[Count];

    public long[] Last { get; set; } = new long[Count];

    public long[] All { get; set; } = new long[Count];

    public int Tier { get; set; }

    public int LastTier { get; set; }
}
