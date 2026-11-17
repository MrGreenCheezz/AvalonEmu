namespace AlbionLocal.Server.Game;

/// <summary>
/// The guild's recruitment info (analysis/work/stageR/guild/SPEC.md §1.3): UpdateGuildRecruitmentInfo (184, ab7) keys 0-11 as the guild
/// window's edit module sets them (GuildRecruitmentInfoEditModule, the client's c2q). <see cref="Join"/> is GuildJoinSettings (0 Invite
/// only, 1 Application, 2 OpenJoin); <see cref="TimeFrom"/>/<see cref="TimeTo"/> the active hours (bu1, -1 = none); the requirements are
/// fame thresholds (a7m) the client shows in its "requirements" popup.
/// </summary>
public sealed class GuildRecruitment
{
    public byte Language { get; set; }

    public byte SecondLanguage { get; set; }

    /// <summary>Key 2 [infer: the builder's first string, the description input].</summary>
    public string Description { get; set; } = "";

    public byte Join { get; set; }

    public bool BeginnerFriendly { get; set; }

    /// <summary>Key 5 [infer: the discord invite code; the client prepends https://discord.gg/].</summary>
    public string Discord { get; set; } = "";

    public byte Home { get; set; }

    public short TimeFrom { get; set; } = -1;

    public short TimeTo { get; set; } = -1;

    public int Activities { get; set; }

    public long Requirement1 { get; set; }

    public long Requirement2 { get; set; }
}

/// <summary>
/// The guild's texts and settings beyond the guilds row (SPEC §2, §4; the character database's guild_profiles, one JSON row per guild):
/// recruitment info, the codex (GuildUpdate keys 34-37), the welcome mail, the optional stats of the guild details, the pay-upkeep flag
/// (GuildUpdate key 13) and when the crest last changed (the gamedata cooldown).
/// </summary>
public sealed class GuildProfile
{
    /// <summary>Null until the guild set its recruitment info (the finder then shows it as "invite only" [assume]).</summary>
    public GuildRecruitment? Recruitment { get; set; }

    public string Codex { get; set; } = "";

    public Guid CodexBy { get; set; }

    public string CodexByName { get; set; } = "";

    /// <summary>GameTimeStamp (UTC ticks) of the last codex change; 0 = never.</summary>
    public long CodexAt { get; set; }

    /// <summary>ChangeNewGuildMemberMail (479): the text a new member is mailed; "" = none.</summary>
    public string WelcomeMail { get; set; } = "";

    /// <summary>GuildSetOptionalStats (485): the GuildDetailOptionalStats chosen for the guild details, in slot order.</summary>
    public int[] OptionalStats { get; set; } = [];

    /// <summary>ChangeGuildPayUpkeepFlag (136), GuildUpdate key 13 (stored and shown; no upkeep is charged here).</summary>
    public bool PayUpkeep { get; set; }

    /// <summary>UTC ticks of the last crest change (Guilds:LogoChangeCooldownMinutes); 0 = never.</summary>
    public long LogoChanged { get; set; }
}

/// <summary>
/// One row of the guild's history logs (GetGuildAccountLogs 159, SPEC §3.1): <see cref="Type"/> the client's LogType (0 silver, 1 gold,
/// 2 siphoned energy, 3 just_event), <see cref="Reason"/> its bhv value (<see cref="GuildLogReason"/>), the actor's name, the reason's
/// parameter (a target name, or "name;role tag" for a rank change), the amount (a7m, negative = out) and when (UTC ticks).
/// </summary>
public sealed record GuildLogEntry(int Type, int Reason, string Actor, string Parameter, long Amount, long At);

/// <summary>The bhv values the server writes (GuildHistoryLogEntry.GetLocalizedReasonLocaTag 0x18107c520).</summary>
public static class GuildLogReason
{
    public const int Deposit = 2, Withdrawal = 3, Invited = 21, Accepted = 22, InstantJoined = 23, Left = 24, Kicked = 25, AssignedRole = 26;
}

/// <summary>The client's LogType (Albion.Common.GameData.TransactionLogs).</summary>
public static class GuildLogType
{
    public const int Silver = 0, Gold = 1, SiphonedEnergy = 2, Event = 3;
}

/// <summary>An open guild application (ApplyToGuild 181): the kind-1 mail in the guild's mailbox it lives in, who applied, the stat categories it shows.</summary>
public sealed record GuildApplication(long Mail, Guid Guild, Guid Character, int Stats, DateTime Applied);
