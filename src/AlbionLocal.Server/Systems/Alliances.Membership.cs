using AlbionLocal.Photon;
using AlbionLocal.Server.Entities;
using AlbionLocal.Server.Game;

namespace AlbionLocal.Server.Systems;

/// <summary>
/// Alliance membership (analysis/work/stageP/hideouts/SPEC.md §2.1, §4 A.3-A.5, phase A2): the founder guild invites a guild by name
/// (AllianceInvite 211: an alliance-invitation mail, kind 2, in the invited guild's mailbox), the invited guild answers from that mail
/// (AllianceAnswerInvitation 212 {0 the mail's id, 1 accept}: MailInvitationControls sends the id it was shown with), the founder cancels
/// an open invitation (213) or kicks a guild (214, never itself). Alliances:MaxGuilds and MaxPlayers limit invitations, joins and the
/// guild invitations of its guilds; Alliances:ChangeCooldownMinutes the joins.
/// </summary>
public sealed partial class Alliances
{
    // AllianceInvite's RCs (GuildAllianceView_a_c 0x1810aac80).
    public const short InviteNotInGuild = 8440, InviteNotInAlliance = 8441, InviteNoRights = 8442, InviteGuildNotFound = 8443,
        InviteGuildInAlliance = 8444, InviteFullGuilds = 8445, InviteFullPlayers = 8446, InviteAlreadyInvited = 8447;

    // AllianceAnswerInvitation's RCs (MailInvitationControls AcceptAllianceInvite callback 0x18154baf0).
    public const short AnswerAlreadyInAlliance = 8481, AnswerNoRights = 8482, AnswerFullGuilds = 8483, AnswerFullPlayers = 8484,
        AnswerCooldown = 8485, AnswerRejoinCooldown = 8486;

    // AllianceCancelInvitation's (0x18109c650) and AllianceKickGuild's (0x18109c8b0) RCs.
    public const short CancelNotInGuild = 8520, CancelNotInAlliance = 8521, CancelNoRights = 8522, CancelNotFound = 8523,
        KickNotInGuild = 8560, KickNotInAlliance = 8561, KickNoRights = 8562, KickGuildNotFound = 8563, KickFounder = 8564;

    /// <summary>The alliance-invitation mail's subject as stored (the client shows its own "@MAIL_SUBJECT_ALLIANCE_INVITATION" for kind 2).</summary>
    public const string InvitationSubject = "Alliance Invitation";

    /// <summary>
    /// Whether <paramref name="g"/>'s alliance can take <paramref name="more"/> more players (Alliances:MaxPlayers; guild invitations 4251
    /// and joins 4123 of its guilds); true without an alliance.
    /// </summary>
    public bool Fits(Guild g, int more = 1) => OfGuild(g.Id) is not { } a || Players(a) + more <= Config.AllianceMaxPlayers;

    /// <summary>
    /// AllianceInvite (211) {0 the inviter's alliance, 1 the guild's name}: not in a guild 8440, its guild in no alliance (or another one
    /// than key 0) 8441, not the founder guild or no ALLIANCE_MANAGEMENT 8442, no such guild 8443, the guild in an alliance 8444, the
    /// alliance at Alliances:MaxGuilds 8445 (open invitations count) or MaxPlayers with the guild 8446, invited already 8447. On 0 the
    /// guild gets the invitation mail (kind 2, category Guild, from the inviting guild; body "inviter|alliance" [H]) and it is saved.
    /// </summary>
    public short Invite(Player p, Guid alliance, string? guildName)
    {
        if (!Enabled || Guilds.Of(p.Character.Id) is not { } g) return InviteNotInGuild;
        if (OfGuild(g.Id) is not { } a || (alliance != Guid.Empty && a.Id != alliance)) return InviteNotInAlliance;
        if (a.Founder != g.Id || !MayManage(g, p.Character.Id)) return InviteNoRights;
        if (Guilds.Find(guildName) is not { } target) return InviteGuildNotFound;
        if (OfGuild(target.Id) is not null) return InviteGuildInAlliance;
        if (_invites.Any(i => i.Alliance == a.Id && i.Guild == target.Id)) return InviteAlreadyInvited;
        if (a.Guilds.Count + InvitesOf(a).Count() >= Config.AllianceMaxGuilds) return InviteFullGuilds;
        if (Players(a) + target.Members.Count > Config.AllianceMaxPlayers) return InviteFullPlayers;
        var mail = Mailer.Send(world, target.Id, guild: true, MailKind.AllianceInvitation, MailCategory.Guild, g.Name, InvitationSubject,
            $"{g.Name}|{a.Name}", senderId: p.Character.Id, fromGuild: true);
        var invite = new AllianceInvite(a.Id, target.Id, mail, UtcNow);
        try { store?.SaveAllianceInvite(invite); }
        catch (Exception ex)
        {
            Log.Warn("alliance", $"invitation of guild '{target.Name}' not saved: {ex.Message}");
            if (mail != 0) world.Mail!.DeleteMails(target.Id, true, [mail], 0, [], MailCategory.All);
            return Refused;
        }
        _invites.Add(invite);
        Log.Info("alliance", $"'{p.Character.Name}' invites guild '{target.Name}' into alliance '{a.Name}' (mail {mail})");
        return 0;
    }

    /// <summary>
    /// AllianceAnswerInvitation (212) {0 the invitation mail's id, 1 accept}: the open invitation of the player's guild with that mail
    /// (none: rc 1), then <see cref="Answer(Player, AllianceInvite, bool, out long)"/>.
    /// </summary>
    public short Answer(Player p, long mail, bool accept, out long cooldownEnd)
    {
        cooldownEnd = 0;
        if (!Enabled || Guilds.Of(p.Character.Id) is not { } g) return AnswerNoRights;
        return _invites.FirstOrDefault(i => i.Guild == g.Id && i.Mail == mail) is { } invite ? Answer(p, invite, accept, out cooldownEnd) : Refused;
    }

    /// <summary>
    /// The answer of the invited guild's member <paramref name="p"/>: no ALLIANCE_MANAGEMENT 8482. A decline drops the invitation and its
    /// mail (rc 0). An accept: the guild in an alliance 8481, the alliance at Alliances:MaxGuilds 8483 or MaxPlayers with it 8484, the rejoin
    /// cooldown for that alliance 8486 or the change cooldown 8485 (<paramref name="cooldownEnd"/> = key 0); on 0 the invitation and mail go
    /// and the guild joins (<see cref="AddGuild"/>: kind 5, nameplates, the Alliance chat channel).
    /// </summary>
    public short Answer(Player p, AllianceInvite invite, bool accept, out long cooldownEnd)
    {
        cooldownEnd = 0;
        if (Guilds.Of(p.Character.Id) is not { } g || g.Id != invite.Guild || !MayManage(g, p.Character.Id)) return AnswerNoRights;
        if (ById(invite.Alliance) is not { } a)
        {
            DropInvite(invite);
            return Refused;
        }
        if (!accept)
        {
            DropInvite(invite);
            if (Guilds.ById(a.Founder) is { } founder) Mailer.GuildNotice(world, founder, "Alliance invitation declined", $"The guild '{g.Name}' declined to join the alliance '{a.Name}'.");
            Log.Info("alliance", $"'{p.Character.Name}' declines the invitation of guild '{g.Name}' into alliance '{a.Name}'");
            return 0;
        }
        if (OfGuild(g.Id) is not null) return AnswerAlreadyInAlliance;
        if (a.Guilds.Count >= Config.AllianceMaxGuilds) return AnswerFullGuilds;
        if (Players(a) + g.Members.Count > Config.AllianceMaxPlayers) return AnswerFullPlayers;
        if (RejoinCooldownEnd(g.Id, a.Id) is { } rejoin)
        {
            cooldownEnd = rejoin.Ticks;
            return AnswerRejoinCooldown;
        }
        if (ChangeCooldownEnd(g.Id) is { } end)
        {
            cooldownEnd = end.Ticks;
            return AnswerCooldown;
        }
        DropInvite(invite);
        foreach (var other in InvitesTo(g.Id).ToList()) DropInvite(other); // in an alliance now: the other invitations are moot
        AddGuild(a, g);
        Mailer.GuildNotice(world, g, "Alliance joined", $"Your guild joined the alliance '{a.Name}'.");
        if (Guilds.ById(a.Founder) is { } f) Mailer.GuildNotice(world, f, "Alliance member joined", $"The guild '{g.Name}' joined the alliance '{a.Name}'.");
        return 0;
    }

    /// <summary>
    /// AllianceCancelInvitation (213) {0 the invited guild}: not in a guild 8520, in no alliance 8521, not the founder guild or no
    /// ALLIANCE_MANAGEMENT 8522, no open invitation of that guild 8523; on 0 the invitation and its mail go.
    /// </summary>
    public short Cancel(Player p, Guid guild)
    {
        if (!Enabled || Guilds.Of(p.Character.Id) is not { } g) return CancelNotInGuild;
        if (OfGuild(g.Id) is not { } a) return CancelNotInAlliance;
        if (a.Founder != g.Id || !MayManage(g, p.Character.Id)) return CancelNoRights;
        if (_invites.FirstOrDefault(i => i.Alliance == a.Id && i.Guild == guild) is not { } invite) return CancelNotFound;
        DropInvite(invite);
        Log.Info("alliance", $"'{p.Character.Name}' cancels the invitation of guild {guild} into alliance '{a.Name}'");
        return 0;
    }

    /// <summary>
    /// AllianceKickGuild (214) {0 the guild}: not in a guild 8560, in no alliance 8561, not the founder guild or no ALLIANCE_MANAGEMENT 8562,
    /// the guild not in the alliance 8563, the founder guild itself 8564; on 0 the guild is out (kind 5, leave protection, rejoin cooldown).
    /// </summary>
    public short Kick(Player p, Guid guild)
    {
        if (!Enabled || Guilds.Of(p.Character.Id) is not { } g) return KickNotInGuild;
        if (OfGuild(g.Id) is not { } a) return KickNotInAlliance;
        if (a.Founder != g.Id || !MayManage(g, p.Character.Id)) return KickNoRights;
        if (guild == a.Founder) return KickFounder;
        if (a.Find(guild) is null || Guilds.ById(guild) is not { } target) return KickGuildNotFound;
        Remove(a, target, $"is kicked by '{p.Character.Name}' from");
        return 0;
    }
}
