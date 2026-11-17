using AlbionLocal.Photon;
using AlbionLocal.Server.Entities;
using AlbionLocal.Server.Game;
using AlbionLocal.Server.Persistence;
using AlbionLocal.Server.Protocol;

namespace AlbionLocal.Server.Systems;

/// <summary>
/// The guild finder and applications (analysis/work/stageR/guild/SPEC.md §1): the recruitment info a guild sets (UpdateGuildRecruitmentInfo
/// 184) and anyone reads (RequestGuildRecruitmentInfo 185), the finder's three lists (183 filtered, 186 by name, 187 recommended), the
/// "Join" of an open guild (InstantJoinGuild 109) and the "Apply" of an application guild (ApplyToGuild 181): a kind-1 mail in the guild's
/// mailbox (the client's accept / decline controls, MailInvitationControls), answered with AnswerGuildApplication 182. Joins by any way
/// mail the guild's welcome text (ChangeNewGuildMemberMail 479) and write the history log (Guilds.Logs.cs). Under <see cref="World.Sync"/>.
/// </summary>
public sealed partial class Guilds
{
    // InstantJoinGuild (gev.a 0x181cc1fc0): 0 "@JOINGUILD_SUCCESSFUL", 4360 already in a guild, 4361 not open for joining, 4362 full,
    // 4363 the alliance full, 4364 rejoin cooldown, 4365 the cooldown with its end in key 0 ("@LFG_ERROR_GUILD_COOLDOWN").
    public const short InstantInGuild = 4360, InstantNotOpen = 4361, InstantFull = 4362, InstantAllianceFull = 4363, InstantCooldown = 4365;

    // ApplyToGuild (gev.a 0x181cc2520): 0 "@LFG_SENT_APPLICATION", 7240 already applied, 7241 already in a guild, 7242 the cooldown (key 0),
    // anything else "@LFG_ERROR_NOT_ABLE_TO_SEND".
    public const short ApplyAlready = 7240, ApplyInGuild = 7241, ApplyCooldown = 7242;

    // AnswerGuildApplication (MailInvitationControls 0x18154c060): 7280 guild full, 7281 the player in a guild [infer], 7282 the player deleted
    // [infer], 7283 unable to add, 7284 guild change cooldown (key 0 end, key 1 name), 7285 alliance full, 7286 rejoin cooldown, 7287 no permission.
    public const short AnswerGuildFull = 7280, AnswerInGuild = 7281, AnswerDeleted = 7282, AnswerUnable = 7283, AnswerCooldown = 7284,
        AnswerAllianceFull = 7285, AnswerNoPermission = 7287;

    /// <summary>RequestGuildRecruitmentInfo of an unknown guild (deb.a 0x180adc160: 7400 = the details view without data).</summary>
    public const short RecruitmentNoGuild = 7400;

    /// <summary>GuildJoinSettings.</summary>
    public const byte JoinInvite = 0, JoinApplication = 1, JoinOpen = 2;

    /// <summary>Guild permissions this part checks (GuildPermission): CHANGE_GUILD_LOGO, CHANGE_PAYUPKEEPFLAG, CHANGE_GUILD_TAX, EDIT_GUILD_DESCRIPTION.</summary>
    public const long PermLogo = 2, PermUpkeep = 8192, PermTax = 16384, PermDescription = 2097152;

    /// <summary>Rows per finder page (dee.c 0x180add7a0 asks c32.i(page × 10, 10, …)).</summary>
    public const int FinderPage = 10;

    private IGuildBoardStore? Board => store as IGuildBoardStore;

    private void LoadProfiles()
    {
        if (Board is not { } board) return;
        foreach (var (id, p) in board.LoadGuildProfiles())
            if (_byId.TryGetValue(id, out var g)) g.Profile = p;
        var purged = board.PurgeGuildLogs(UtcNow.AddDays(-LogKeepDays).Ticks);
        if (purged > 0) Log.Info("guild", $"{purged} guild log row(s) older than {LogKeepDays} days dropped");
    }

    /// <summary>Saves the profile of <paramref name="g"/> at once (false = the write failed; the caller keeps the change in memory anyway).</summary>
    public bool SaveProfile(Guild g)
    {
        try { Board?.SaveGuildProfile(g.Id, g.Profile); return true; }
        catch (Exception ex)
        {
            Log.Warn("guild", $"profile of guild '{g.Name}' not saved: {ex.Message}");
            return false;
        }
    }

    /// <summary>Whether the member's role has any of <paramref name="mask"/> (the GM has all).</summary>
    public static bool Has(GuildMember m, long mask) => (GuildRoles.Permissions(m.Role) & mask) != 0;

    // ---- recruitment info (SPEC §1.3) ----

    /// <summary>
    /// UpdateGuildRecruitmentInfo (184, no response class): stored for the member's guild when it may edit the guild description
    /// (EDIT_GUILD_DESCRIPTION [assume]; the founder sends it right after CreateGuild). Strings are cut to 512 / 64 characters [assume].
    /// </summary>
    public bool SetRecruitment(Player p, GuildRecruitment info)
    {
        if (Of(p.Character.Id) is not { } g || g.Find(p.Character.Id) is not { } m || !Has(m, PermDescription)) return false;
        info.Description = Cut(info.Description, 512);
        info.Discord = Cut(info.Discord, 64);
        if (info.Join > JoinOpen) info.Join = JoinInvite;
        g.Profile.Recruitment = info;
        SaveProfile(g);
        Log.Info("guild", $"'{p.Character.Name}' sets the recruitment info of guild '{g.Name}' (join {info.Join})");
        return true;
    }

    private static string Cut(string? s, int max) => s is null ? "" : s.Length > max ? s[..max] : s;

    /// <summary>The recruitment info of <paramref name="g"/>, the default "invite only" one when it set none.</summary>
    public static GuildRecruitment RecruitmentOf(Guild g) => g.Profile.Recruitment ?? new GuildRecruitment();

    /// <summary>
    /// RequestGuildRecruitmentInfo (185, z7 {0 guild, 1 z7.a}): aip (deb.a 0x180adc160 → c2q / c2m): 0 the guild, 1 the request's key 1,
    /// 2/3 languages, 4 the description, 5 a Guid and 6 a string of the description's label [assume: the guild's id and name], 7 join
    /// settings, 8 beginner friendly, 9 discord, 10 home, 11/12 active hours, 13 activities, 14/15 the requirements, 16-19 fame stats
    /// (0: not counted here), 20/21 the members [infer: c2m.b / c2m.a], 22-25 rankings (0). Null = no such guild (7400).
    /// </summary>
    public RequestGuildRecruitmentInfoResponse? Recruitment(Guid id, byte part)
    {
        if (ById(id) is not { } g) return null;
        var r = RecruitmentOf(g);
        var members = (short)Math.Min(short.MaxValue, g.Members.Count);
        return new RequestGuildRecruitmentInfoResponse
        {
            bkc = g.Id, bkd = part, bke = r.Language, bkf = r.SecondLanguage, bkg = r.Description, bkh = g.Id, bki = g.Name, bkj = r.Join,
            bkk = r.BeginnerFriendly, bkl = r.Discord, bkm = r.Home, bkn = r.TimeFrom, bko = r.TimeTo, bkp = r.Activities,
            bkq = r.Requirement1, bkr = r.Requirement2, bkw = members, bkx = (short)OnlineCount(g),
        };
    }

    private int OnlineCount(Guild g) => g.Members.Count(m => Online(m.Id) is not null);

    // ---- the finder's lists (SPEC §1.2) ----

    /// <summary>
    /// The guilds a filtered search (183) lists, most members first: a language filter (key 6, 0 = any) matches either language, a home
    /// (key 7, 0 = any) the home, an activity mask (key 12) any shared activity, a member range (keys 8/9 when key 9 &gt; 0) the count [infer
    /// from the client's dej fields: language, home, member range, activities].
    /// </summary>
    public List<Guild> Filtered(byte language, byte home, int minMembers, int maxMembers, int activities) =>
    [
        .. _byId.Values.Where(g =>
        {
            var r = RecruitmentOf(g);
            return (language == 0 || r.Language == language || r.SecondLanguage == language) && (home == 0 || r.Home == home)
                   && (activities == 0 || (r.Activities & activities) != 0)
                   && (maxMembers <= 0 || g.Members.Count >= minMembers && g.Members.Count <= maxMembers);
        }).OrderByDescending(g => g.Members.Count).ThenBy(g => g.Name, StringComparer.OrdinalIgnoreCase),
    ];

    /// <summary>The guilds whose name contains <paramref name="text"/> (any case), at most 50 [assume].</summary>
    public List<Guild> Named(string? text) =>
        string.IsNullOrWhiteSpace(text) ? []
        : [.. _byId.Values.Where(g => g.Name.Contains(text.Trim(), StringComparison.OrdinalIgnoreCase)).OrderBy(g => g.Name.Length).Take(50)];

    /// <summary>Up to three guilds recruiting in <paramref name="language"/> (any guild that takes applications or joins when none does) [assume].</summary>
    public List<Guild> Recommended(byte language) =>
    [
        .. _byId.Values.Where(g => RecruitmentOf(g).Join != JoinInvite && (language == 0 || RecruitmentOf(g).Language == language || RecruitmentOf(g).SecondLanguage == language))
            .OrderByDescending(g => g.Members.Count).Take(3),
    ];

    /// <summary>
    /// RequestGuildFinderFilteredList's rows (aim keys 2-35, dee.b 0x180adcfb0 into ded / dei): 2 Guid, 3 name, 4 the alliance's tag (shown
    /// beside the name), 5 and 18 the member count [infer: ded.d / ded.c], 6-10 / 11-12 the crest's ints / floats, 13 the banner, 14 discord,
    /// 15 join settings, 16 the description, 17 its Guid [assume: the guild's], 19/20 active hours, 21 home, 22/23 languages, 24 activities,
    /// 25 beginner friendly, 26/27 rankings (0), 28-31 weekly fame (0), 32/33 the requirements. Every array present and of one length.
    /// </summary>
    public RequestGuildFinderFilteredListResponse FilteredRows(IReadOnlyList<Guild> all, int offset, int count)
    {
        var rows = all.Skip(Math.Max(0, offset)).Take(Math.Clamp(count, 1, 50)).ToList();
        var rec = rows.Select(RecruitmentOf).ToList();
        short Members(Guild g) => (short)Math.Min(short.MaxValue, g.Members.Count);
        return new RequestGuildFinderFilteredListResponse
        {
            bh3 = Math.Max(0, offset), bh4 = offset + rows.Count < all.Count,
            bh5 = [.. rows.Select(g => g.Id)], bh6 = [.. rows.Select(g => g.Name)], bh7 = [.. rows.Select(AllianceTag)], bh8 = [.. rows.Select(Members)],
            bh9 = [.. rows.Select(g => g.Logo.Schema)], bia = [.. rows.Select(g => g.Logo.Symbol)], bib = [.. rows.Select(g => g.Logo.Color1)],
            bic = [.. rows.Select(g => g.Logo.Color2)], bid = [.. rows.Select(g => g.Logo.SymbolColor)], bie = [.. rows.Select(g => g.Logo.Scale)],
            bif = [.. rows.Select(g => g.Logo.Offset)], big = [.. rows.Select(g => g.Banner ?? "")], bih = [.. rec.Select(r => r.Discord)],
            bii = [.. rec.Select(r => r.Join)], bij = [.. rec.Select(r => r.Description)], bik = [.. rows.Select(g => g.Id)], bil = [.. rows.Select(Members)],
            bim = [.. rec.Select(r => (byte)Math.Max((short)0, r.TimeFrom))], bin = [.. rec.Select(r => (byte)Math.Max((short)0, r.TimeTo))],
            bio = [.. rec.Select(r => r.Home)], bip = [.. rec.Select(r => r.Language)], biq = [.. rec.Select(r => r.SecondLanguage)],
            bir = [.. rec.Select(r => r.Activities)], bis = [.. rec.Select(r => r.BeginnerFriendly)], bit = new int[rows.Count], biu = new int[rows.Count],
            biv = new long[rows.Count], biw = new long[rows.Count], bix = new long[rows.Count], biy = new long[rows.Count],
            biz = [.. rec.Select(r => r.Requirement1)], bi0 = [.. rec.Select(r => r.Requirement2)],
        };
    }

    /// <summary>RequestGuildFinderNameSearch's rows (ain, deg.b 0x180ade370 into dei): 0 Guid, 1 name, 2-6 / 7-8 the crest, 9 discord, 10 join, 11/12 the requirements.</summary>
    public RequestGuildFinderNameSearchResponse NamedRows(IReadOnlyList<Guild> rows)
    {
        var rec = rows.Select(RecruitmentOf).ToList();
        return new RequestGuildFinderNameSearchResponse
        {
            bi3 = [.. rows.Select(g => g.Id)], bi4 = [.. rows.Select(g => g.Name)], bi5 = [.. rows.Select(g => g.Logo.Schema)], bi6 = [.. rows.Select(g => g.Logo.Symbol)],
            bi7 = [.. rows.Select(g => g.Logo.Color1)], bi8 = [.. rows.Select(g => g.Logo.Color2)], bi9 = [.. rows.Select(g => g.Logo.SymbolColor)],
            bja = [.. rows.Select(g => g.Logo.Scale)], bjb = [.. rows.Select(g => g.Logo.Offset)], bjc = [.. rec.Select(r => r.Discord)],
            bjd = [.. rec.Select(r => r.Join)], bje = [.. rec.Select(r => r.Requirement1)], bjf = [.. rec.Select(r => r.Requirement2)],
        };
    }

    /// <summary>
    /// RequestGuildFinderRecommendedList's rows (aio, deh.b 0x180aded80 into dek): 0 the request's key 1 [assume], 1 0, 2 the language
    /// (the client files the rows under it), 3-27 the filtered list's columns without its counters.
    /// </summary>
    public RequestGuildFinderRecommendedListResponse RecommendedRows(byte language, byte part)
    {
        var rows = Recommended(language);
        var f = FilteredRows(rows, 0, Math.Max(1, rows.Count));
        return new RequestGuildFinderRecommendedListResponse
        {
            bji = part, bjk = language,
            bjl = f.bh5, bjm = f.bh6, bjn = f.bh7, bjo = f.bh8, bjp = f.bh9, bjq = f.bia, bjr = f.bib, bjs = f.bic, bjt = f.bid, bju = f.bie, bjv = f.bif,
            bjw = f.big, bjx = f.bih, bjy = f.bii, bjz = f.bij, bj0 = f.bik, bj1 = f.bil, bj2 = f.bim, bj3 = f.bin, bj4 = f.bio, bj5 = f.bip, bj6 = f.biq,
            bj7 = f.bir, bj8 = f.biz, bj9 = f.bi0,
        };
    }

    private string AllianceTag(Guild g) => world.Alliances.OfGuild(g.Id)?.Tag ?? "";

    // ---- joining by the finder (SPEC §1.4, §1.5) ----

    /// <summary>
    /// InstantJoinGuild (109): a guild whose join settings are OpenJoin takes <paramref name="p"/> at once with Guilds:JoinRole. Errors as
    /// the constants above; <paramref name="cooldownEnd"/> = key 0 of 4365. The response and then <see cref="AfterJoin"/> are the handler's.
    /// </summary>
    public short InstantJoin(Player p, Guid id, out Guild? guild, out long cooldownEnd)
    {
        (guild, cooldownEnd) = (null, 0);
        var me = p.Character.Id;
        if (!Enabled) return Refused;
        if (Of(me) is not null) return InstantInGuild;
        if (ById(id) is not { } g || RecruitmentOf(g).Join != JoinOpen) return InstantNotOpen;
        if (g.Members.Count >= Config.GuildMaxMembers) return InstantFull;
        if (!world.Alliances.Fits(g)) return InstantAllianceFull;
        if (CooldownEnd(me) is { } end)
        {
            cooldownEnd = end.Ticks;
            return InstantCooldown;
        }
        Admit(g, me, p.Character.Name ?? "");
        AddLog(g, GuildLogType.Event, GuildLogReason.InstantJoined, p.Character.Name ?? "", p.Character.Name ?? "");
        guild = g;
        return 0;
    }

    /// <summary>
    /// ApplyToGuild (181, o1 {0 guild, 1 origin, 2 message, 3 GuildApplicationStatCategories}): a guild that takes applications (or opens
    /// joins [assume]) gets a kind-1 mail (category Guild, from the applicant, the message as its body) in its mailbox, NewUnreadMails to the
    /// members who may read it. In a guild 7241, an open application to it 7240, the guild change cooldown 7242 (key 0), an invite-only or
    /// unknown guild or mail off 1.
    /// </summary>
    public short Apply(Player p, Guid id, string? message, int stats, out long cooldownEnd)
    {
        cooldownEnd = 0;
        var me = p.Character.Id;
        if (!Enabled || Board is not { } board || !Mailer.On(world)) return Refused;
        if (Of(me) is not null) return ApplyInGuild;
        if (ById(id) is not { } g || RecruitmentOf(g).Join == JoinInvite) return Refused;
        if (board.GuildApplicationOf(g.Id, me) is { } open && world.Mail!.Mail(open.Mail) is not null) return ApplyAlready;
        if (CooldownEnd(me) is { } end)
        {
            cooldownEnd = end.Ticks;
            return ApplyCooldown;
        }
        var mail = Mailer.Send(world, g.Id, guild: true, MailKind.GuildApplication, MailCategory.Guild, p.Character.Name ?? "", "", Cut(message, 512).Trim(),
            senderId: me);
        if (mail == 0) return Refused;
        board.AddGuildApplication(new GuildApplication(mail, g.Id, me, stats, UtcNow));
        Log.Info("guild", $"'{p.Character.Name}' applies to guild '{g.Name}' (mail {mail})");
        return 0;
    }

    /// <summary>
    /// ReadMail key 8 of an application mail (Dictionary&lt;GuildApplicationStatToDisplay, long&gt;): TF (0) the applicant's total fame when it
    /// asked to show it; the other stats are not counted by this server and left out [assume]. Empty for any other mail.
    /// </summary>
    public Dictionary<byte, long> ApplicationStats(long mail)
    {
        var stats = new Dictionary<byte, long>();
        if (Board?.GuildApplication(mail) is { } a && (a.Stats & 1) != 0 && world.Characters.Find(a.Character) is { } c) stats[0] = c.Fame;
        return stats;
    }

    /// <summary>
    /// AnswerGuildApplication (182, ow {0 mail id, 1 accept}) by <paramref name="p"/>, who needs INVITE [assume: the right that adds
    /// members]. Decline: the application and its mail go, the applicant gets the system mail GUILDAPPLDECLINED (the client's
    /// "@MAIL_CONTENT_GUILD_APPLICATION_DENIED" with the sender = the guild). Accept: the applicant (in game or not) joins with
    /// Guilds:JoinRole, the mail goes. Errors as the constants above (<paramref name="cooldownEnd"/>/<paramref name="name"/> = keys 0/1 of 7284).
    /// </summary>
    public short Answer(Player p, long mail, bool accept, out long cooldownEnd, out string? name)
    {
        (cooldownEnd, name) = (0, null);
        if (Of(p.Character.Id) is not { } g || g.Find(p.Character.Id) is not { } me || (GuildRoles.Permissions(me.Role) & GuildRoles.Invite) == 0) return AnswerNoPermission;
        if (Board is not { } board || board.GuildApplication(mail) is not { } a || a.Guild != g.Id) return AnswerUnable;
        if (!accept)
        {
            Withdraw(g, a);
            if (world.Characters.Find(a.Character) is not null)
                Mailer.Send(world, a.Character, guild: false, MailKind.System, MailCategory.Guild, g.Name, "GUILDAPPLDECLINED", "");
            Log.Info("guild", $"'{me.Name}' declines the application of {a.Character} to guild '{g.Name}'");
            return 0;
        }
        if (world.Characters.Find(a.Character) is not { } c)
        {
            Withdraw(g, a);
            return AnswerDeleted;
        }
        name = c.Name;
        if (Of(c.Id) is not null) return AnswerInGuild;
        if (g.Members.Count >= Config.GuildMaxMembers) return AnswerGuildFull;
        if (!world.Alliances.Fits(g)) return AnswerAllianceFull;
        if (CooldownEnd(c.Id) is { } end)
        {
            cooldownEnd = end.Ticks;
            return AnswerCooldown;
        }
        Withdraw(g, a);
        Admit(g, c.Id, c.Name ?? "");
        AddLog(g, GuildLogType.Event, GuildLogReason.Accepted, me.Name, c.Name ?? "");
        if (Online(c.Id) is { } joiner)
        {
            joiner.Session.Send(new SystemMessageEvent { d0p = $"[Guild] Your application to {g.Name} was accepted.", d0q = 0 });
            AfterJoin(joiner, g);
        }
        else
        {
            // Offline: the others learn of the member now, the joiner gets the whole guild at its next login (Joined).
            var m = g.Find(c.Id)!;
            SendTo(g, PlayerUpdate(m, KindAdd, online: false), except: m.Id);
            SendTo(g, Tags(g), except: m.Id);
            Mailer.GuildNotice(world, g, "New member", $"{m.Name} joined the guild.");
        }
        return 0;
    }

    /// <summary>The application <paramref name="a"/> is settled: its row and its mail go (NewUnreadMails when it was unread).</summary>
    private void Withdraw(Guild g, GuildApplication a)
    {
        Board?.DeleteGuildApplication(a.Mail);
        if (world.Mail is { } mails)
        {
            var unread = mails.UnreadMails(g.Id, true);
            mails.DeleteMails(g.Id, true, [a.Mail], 0, [], MailCategory.All);
            if (mails.UnreadMails(g.Id, true) != unread) Mailer.Notify(world, g.Id, guild: true);
        }
    }

    /// <summary>Makes <paramref name="id"/> a member of <paramref name="g"/> (Guilds:JoinRole; checks are the caller's), saved, with the welcome mail.</summary>
    private void Admit(Guild g, Guid id, string name)
    {
        g.Members.Add(new GuildMember(id, name, Config.GuildJoinRole, UtcNow));
        _byMember[id] = g;
        _invites.Remove(id);
        Save(g);
        Changed(id);
        Welcome(g, id);
        Log.Info("guild", $"'{name}' joins guild '{g.Name}' as {Config.GuildJoinRole} ({g.Members.Count} members)");
    }

    /// <summary>The guild's welcome text (ChangeNewGuildMemberMail) into the new member's mailbox: a plain guild mail "Welcome to …" [assume].</summary>
    private void Welcome(Guild g, Guid member)
    {
        if (g.Profile.WelcomeMail is not { Length: > 0 } text) return;
        Mailer.Send(world, member, guild: false, MailKind.Normal, MailCategory.Guild, g.Name, $"Welcome to {g.Name}", text, fromGuild: true);
    }
}
