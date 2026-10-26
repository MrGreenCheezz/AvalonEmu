namespace AlbionLocal.Server.Game;

/// <summary>A guild of an alliance (analysis/work/stageP/hideouts/SPEC.md §2.2): the guild and when it joined (AllianceGetMemberInfos key 17, "Member since").</summary>
public sealed class AllianceGuild(Guid guild, DateTime joined)
{
    public Guid Guild { get; } = guild;

    public DateTime Joined { get; } = joined;
}

/// <summary>
/// An alliance of guilds (SPEC §1.2, §4): its id, unique name (3-30) and tag (2-5; both NOCASE), the founder guild (the only one that
/// invites, kicks and disbands; it cannot be kicked) and its guilds in join order (the founder first). Saved at once by the alliance store.
/// </summary>
public sealed class Alliance(Guid id, string name, string tag, Guid founder, DateTime created)
{
    public Guid Id { get; } = id;

    public string Name { get; } = name;

    public string Tag { get; } = tag;

    /// <summary>The founder guild (AllianceGetMemberInfos key 0; passes to the oldest other guild when the founder guild is deleted).</summary>
    public Guid Founder { get; set; } = founder;

    public DateTime Created { get; } = created;

    public List<AllianceGuild> Guilds { get; } = [];

    public AllianceGuild? Find(Guid guild) => Guilds.FirstOrDefault(m => m.Guild == guild);
}

/// <summary>An open invitation of <see cref="Guild"/> into <see cref="Alliance"/> (SPEC §4 A.3): the alliance-invitation mail (kind 2) in the guild's mailbox is <see cref="Mail"/> (0 = none: mail off).</summary>
public sealed record AllianceInvite(Guid Alliance, Guid Guild, long Mail, DateTime At);

/// <summary>
/// A guild's alliance history for the cooldowns (SPEC §1.2): when it last founded or joined an alliance (<see cref="Changed"/>), and which
/// alliance it last left and when (<see cref="LastAlliance"/>, <see cref="LeftAt"/>; Guid.Empty / null = none).
/// </summary>
public sealed record AllianceHistory(Guid Guild, DateTime Changed, Guid LastAlliance, DateTime? LeftAt);
