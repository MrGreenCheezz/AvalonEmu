namespace AlbionLocal.Server.Game;

/// <summary>GuildRole (stageH guilds SPEC §0): an int on the client's member c2p, a byte on the wire; the enum order is the rank order (§5).</summary>
public enum GuildRole : byte
{
    None = 0, Recruit = 1, Member = 2, Guardsman = 3, Officer = 4, Warmaster = 5, MasterOfCoin = 6, TheRightHand = 7, Guildmaster = 8,
}

/// <summary>
/// The crest of a guild as CreateGuild sent it (keys 1-7) and every guild message echoes it (GuildUpdate 20-26, Join 71-77,
/// NewCharacter 44-50, GuildMemberWorldUpdate 4-10): schema and symbol are ids of the client's logo dictionaries, the colours
/// RGB / palette values; the server only stores them (SPEC §2.7: unknown ids give the client a null part, no crash).
/// </summary>
public sealed record GuildLogo(int Schema, int Symbol, int Color1, int Color2, int SymbolColor, float Scale, float Offset)
{
    /// <summary>The crest of a guild made by the GM command (no crest dialog): the first schema and symbol, white symbol, scale 1.</summary>
    public static readonly GuildLogo Default = new(0, 0, 0, 0, 16777215, 1, 0);
}

/// <summary>A member of a guild (SPEC §4): the character, its name (from the character table), its role, when it joined and was last seen (UTC).</summary>
public sealed class GuildMember(Guid id, string name, GuildRole role, DateTime joined)
{
    public Guid Id { get; } = id;

    public string Name { get; } = name;

    public GuildRole Role { get; set; } = role;

    public DateTime Joined { get; } = joined;

    /// <summary>GuildUpdate key 4 / GuildPlayerUpdated key 3 of an offline member (an online one is sent "now").</summary>
    public DateTime LastSeen { get; set; } = joined;
}

/// <summary>
/// A guild (stageH guilds SPEC §4): its id, unique name (NOCASE), crest and banner, the account fields the GuildUpdate carries
/// (the guild account's silver, a7m: Guilds:SilverTax and the contracts fill it, .guild payout empties it; tax a7m, unused), the guild
/// experience and contracts of §10, the guild goals' state (stage I) and the ids of its eight role tags (<see cref="GuildRoles"/>; per
/// guild [choice]). Members in join order. Saved at once by the guild store; the account, the experience, the contracts and the guild
/// goals, which change with every kill, every 30 s.
/// </summary>
public sealed class Guild(Guid id, string name, DateTime created, GuildLogo logo, string banner, Guid[] tags)
{
    public Guid Id { get; } = id;

    public string Name { get; set; } = name;

    public DateTime Created { get; } = created;

    public GuildLogo Logo { get; set; } = logo;

    /// <summary>CreateGuild key 8, the banner's uniquename (stored only).</summary>
    public string Banner { get; set; } = banner;

    public long SilverA7m { get; set; }

    public long TaxA7m { get; set; }

    /// <summary>The guild experience in whole points (SPEC §10.3).</summary>
    public long Xp { get; set; }

    /// <summary>The fraction of a point not yet in <see cref="Xp"/> (runtime only: at most one point is lost over a restart).</summary>
    public double XpCarry { get; set; }

    /// <summary>The contracts of the current period (SPEC §10.6); null = none rolled yet.</summary>
    public GuildContracts? Contracts { get; set; }

    /// <summary>The guild goals' seasons, weeks and member logs (stageI guild-goals SPEC §2.8), saved with the contracts.</summary>
    public GuildProgress Progress { get; set; } = new();

    /// <summary>The ids of the eight role tags in <see cref="GuildRoles.TagRoles"/> order (GuildFullAccessTagsUpdated key 1).</summary>
    public Guid[] Tags { get; } = tags;

    public List<GuildMember> Members { get; } = [];

    public GuildMember? Find(Guid id) => Members.FirstOrDefault(m => m.Id == id);

    public GuildMember? Find(string? name) =>
        string.IsNullOrWhiteSpace(name) ? null : Members.FirstOrDefault(m => string.Equals(m.Name, name.Trim(), StringComparison.OrdinalIgnoreCase));

    /// <summary>The id of the GUILDMASTER tag (GuildFullAccessTagsUpdated key 0, not the guild id: SPEC §2.6).</summary>
    public Guid MasterTag => Tags[GuildRoles.TagIndex(GuildRole.Guildmaster)];

    /// <summary>The guild master (one per guild; null only while a load repairs a guild).</summary>
    public GuildMember? Master => Members.FirstOrDefault(m => m.Role == GuildRole.Guildmaster);
}

/// <summary>
/// One guild contract (SPEC §10.6): <see cref="Kind"/> kill (mobs of <see cref="Tier"/> or more), dungeon (dungeon instances cleared), gather
/// (resources received) or expedition (completed), <see cref="Count"/> to reach, <see cref="Progress"/> so far, <see cref="Done"/>.
/// </summary>
public sealed class GuildContract
{
    public string Kind { get; set; } = "";

    public int Tier { get; set; }

    public int Count { get; set; }

    public int Progress { get; set; }

    public bool Done { get; set; }

    /// <summary>"kill 500 T5+ mobs" (the .guild contracts line and the announcements).</summary>
    public string Describe() => Kind switch
    {
        "kill" => $"kill {Count} T{Tier}+ mobs",
        "dungeon" => $"clear {Count} T{Tier}+ dungeons",
        "gather" => $"gather {Count} T{Tier}+ resources",
        "expedition" => $"complete {Count} T{Tier}+ expeditions",
        _ => $"{Kind} {Count}",
    };
}

/// <summary>The contracts of one period of a guild (the guilds row's contracts column, JSON): the period's key ("w20261012" / "d20261017").</summary>
public sealed class GuildContracts
{
    public string Period { get; set; } = "";

    public List<GuildContract> Goals { get; set; } = [];
}

/// <summary>
/// The role tags of a guild (SPEC §2.6, the official 002835 table without its custom tag): the localisation key (key 2), the
/// permission mask (key 5), key 6 and the roles whose tag may assign it (key 8, as tag indices). RECRUIT members hold no tag (the
/// capture's 0xFF row), every other member exactly the tag of its role, so a member's rights are its role's mask (§5).
/// </summary>
public static class GuildRoles
{
    /// <summary>GuildPermission KICK_FROM_GUILD and INVITE (SPEC §0).</summary>
    public const long Kick = 4, Invite = 8;

    /// <summary>GuildPermission ACCESS_GUILD_MAIL (SPEC §0; stageJ mail SPEC §1.5): the guild's mailbox and mail to its members (the guild master and The Right Hand).</summary>
    public const long AccessGuildMail = 65536;

    /// <summary>GuildPermission ALLIANCE_MANAGEMENT (stageP hideouts SPEC §1.1): found, leave, invite to, kick from and disband the guild's alliance, answer invitations.</summary>
    public const long AllianceManagement = 131072;

    /// <summary>All 36 permission flags (the GUILDMASTER tag).</summary>
    public const long All = 0xFFFFFFFFF;

    /// <summary>key 3 of every role tag in the capture (a colour).</summary>
    public const int TagColour = 1497839052;

    /// <summary>The tag order (key 1 index i ↔ this role).</summary>
    public static readonly GuildRole[] TagRoles =
    [
        GuildRole.TheRightHand, GuildRole.MasterOfCoin, GuildRole.Member, GuildRole.Officer,
        GuildRole.Guildmaster, GuildRole.Recruit, GuildRole.Guardsman, GuildRole.Warmaster,
    ];

    /// <summary>Key 2: the localisation keys, which the client translates itself.</summary>
    public static readonly string[] Names =
    [
        "@GUILDROLE_THE_RIGHT_HAND", "@GUILDROLE_MASTER_OF_COIN", "@GUILDROLE_MEMBER", "@GUILDROLE_OFFICER",
        "@GUILDROLE_GUILDMASTER", "@GUILDROLE_RECRUIT", "@GUILDROLE_GUARDSMAN", "@GUILDROLE_WARMASTER",
    ];

    /// <summary>Key 5, the permission masks of the capture.</summary>
    public static readonly long[] Masks = [68709515261, 15971929001, 134218240, 18119396008, All, 0, 134218240, 11676945080];

    /// <summary>Key 6 (only THE_RIGHT_HAND and GUILDMASTER have one in the capture).</summary>
    public static readonly long[] Second = [68709515261, 0, 0, 0, All, 0, 0, 0];

    /// <summary>Key 8: the tags that may assign tag i (the capture's lists with its indices 6-8 moved to 5-7); empty = the GM only.</summary>
    public static readonly byte[][] Assigners = [[], [0], [3, 6, 7, 1, 0], [7, 1, 0], [], [3, 6, 2, 7, 1, 0], [3, 7, 1, 0], [1, 0]];

    /// <summary>Key 9 of the capture: [0] where key 8 has a list.</summary>
    public static readonly byte[][] Editors = [[], [0], [0], [0], [], [0], [0], [0]];

    public static int TagIndex(GuildRole role) => Array.IndexOf(TagRoles, role);

    /// <summary>The rights of a member of <paramref name="role"/>: its tag's mask (a RECRUIT holds none: 0).</summary>
    public static long Permissions(GuildRole role) => role is GuildRole.Recruit or GuildRole.None ? 0 : Masks[TagIndex(role)];

    /// <summary>The tag a member of <paramref name="role"/> holds; -1 = none (RECRUIT).</summary>
    public static int HeldTag(GuildRole role) => role is GuildRole.Recruit or GuildRole.None ? -1 : TagIndex(role);

    /// <summary>
    /// The client's CanKick (c2p.k(c2p) 0x180a44690, SPEC §0): never the guild master; the guild master anyone else; others need
    /// KICK_FROM_GUILD and every tag the target holds (with role tags only: the target is a RECRUIT or of the kicker's role).
    /// </summary>
    public static bool CanKick(GuildRole kicker, GuildRole target) =>
        target != GuildRole.Guildmaster
        && (kicker == GuildRole.Guildmaster || (Permissions(kicker) & Kick) != 0 && (HeldTag(target) < 0 || HeldTag(target) == HeldTag(kicker)));

    /// <summary>
    /// Whether a member of <paramref name="assigner"/> may give or take the tag of <paramref name="role"/> to a member of
    /// <paramref name="target"/> (SPEC §3.4): the GM always (not its own tag: that is the hand-over); others when their tag is in
    /// the tag's key-8 list and the target ranks below them [choice]. The GM tag is the GM's only.
    /// </summary>
    public static bool CanAssign(GuildRole assigner, GuildRole role, GuildRole target)
    {
        if (role == GuildRole.Guildmaster || target == GuildRole.Guildmaster) return assigner == GuildRole.Guildmaster;
        if (assigner == GuildRole.Guildmaster) return true;
        return HeldTag(assigner) is >= 0 and var held && Assigners[TagIndex(role)].Contains((byte)held) && target < assigner && role < assigner;
    }
}
