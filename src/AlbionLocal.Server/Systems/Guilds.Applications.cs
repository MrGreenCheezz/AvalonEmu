using System.Text.Json;
using AlbionLocal.Photon;
using AlbionLocal.Server.Entities;
using AlbionLocal.Server.Game;
using AlbionLocal.Server.Protocol;

namespace AlbionLocal.Server.Systems;

public sealed partial class Guilds
{
    private const string ApplicationStatsPrefix = "AvalonGuildApplication/v1:";

    // 345319 gev.a 1CC2520 (181), 1CC1FC0 (109), MailInvitationControls154C060 (182).
    public const short AlreadyApplied = 7240, ApplyAlreadyMember = 7241, ApplyCooldown = 7242;
    public const short OpenJoinAlreadyMember = 4360, NotOpenForJoin = 4361, OpenJoinFull = 4362,
        OpenJoinAllianceFull = 4363, OpenJoinCooldown = 4365;
    public const short ApplicationGuildFull = 7280, ApplicationAlreadyMember = 7281,
        ApplicationPlayerDeleted = 7282, ApplicationUnableToAdd = 7283, ApplicationCooldown = 7284,
        ApplicationAllianceFull = 7285, ApplicationNoPermission = 7287;

    private IEnumerable<Mail> PendingApplications(Guild guild)
    {
        if (!Mailer.On(world)) yield break;
        for (var page = 0; ; page++)
        {
            var mails = world.Mail!.MailPage(guild.Id, true, MailCategory.Guild, page, Mailer.PageSize);
            foreach (var mail in mails)
                if (mail.Kind == MailKind.GuildApplication && mail.SenderId != Guid.Empty
                    && (mail.Expires is null || mail.Expires > UtcNow)) yield return mail;
            if (mails.Count < Mailer.PageSize) yield break;
        }
    }

    private Dictionary<byte, long> SharedApplicationStats(Character c, int categories)
    {
        var stats = new Dictionary<byte, long>();
        // 345319 GuildApplicationStatToDisplay 0..4 = TF/KF/PF/GF/CF. The other selected
        // categories use only their actual retained ledgers. Unobserved history is omitted, not fabricated.
        if ((categories & 1) != 0) stats[0] = FinderTotal(c);
        if (c.Statistics is { } observed)
        {
            if ((categories & 2) != 0) stats[1] = Math.Max(0, observed.PvpFame);
            if ((categories & 4) != 0) stats[2] = Math.Max(0, observed.PveFame);
            if ((categories & 8) != 0) stats[3] = Math.Max(0, observed.GatheringFame);
            if ((categories & 16) != 0) stats[4] = Math.Max(0, observed.CraftingFame);
        }
        if ((categories & 32) != 0 && c.Infamy is { } infamy)
            for (var type = 2; type <= 4; type++)
                if (infamy.Current.TryGetValue(type, out var value)) stats[(byte)(type + 3)] = Math.Max(0, value);
        if ((categories & 64) != 0)
        {
            void Arena(string ladder, byte played, byte wins)
            {
                if (world.Ratings.Find(c.Id, ladder) is not { } row) return;
                stats[played] = (long)Math.Max(0, row.Wins) + Math.Max(0, row.Losses) + Math.Max(0, row.Draws);
                stats[wins] = Math.Max(0, row.Wins);
            }
            Arena("5v5", 8, 9);
            Arena(Arenas.Crystal, 10, 11);
        }
        // Crystal League GvG variants (category128, stat ids12-17) have no retained battle ledger.
        return stats;
    }

    /// <summary>ReadMail key8 is the selected, real snapshot from the application. Current native
    /// MailListRow 10197C8 and MailReadView FF67EF use a fixed subject for kind1, so the stored
    /// subject can retain this metadata while Body remains the exact plain application text.</summary>
    public Dictionary<byte, long>? ApplicationStats(Mail mail)
    {
        if (mail.Kind != MailKind.GuildApplication || !mail.Subject.StartsWith(ApplicationStatsPrefix, StringComparison.Ordinal)) return null;
        try { return JsonSerializer.Deserialize<Dictionary<byte, long>>(mail.Subject[ApplicationStatsPrefix.Length..]); }
        catch (JsonException) { return null; }
    }

    public short ApplyThroughFinder(Player applicant, ApplyToGuildRequest request, out long cooldownEnd)
    {
        cooldownEnd = 0;
        var c = applicant.Character;
        if (Of(c.Id) is not null) return ApplyAlreadyMember;
        if (c.Transient || !Enabled || !Mailer.On(world) || ById(request.ba) is not { } guild
            || guild.Progress.Recruitment is not { JoinSettings: 1 } settings
            || !FinderEligible(c, settings) || request.bc?.Length > 500 || request.bd < 0 || (request.bd & ~0xff) != 0) return Refused;
        if (CooldownEnd(c.Id) is { } cooldown) { cooldownEnd = cooldown.Ticks; return ApplyCooldown; }
        if (PendingApplications(guild).Any(mail => mail.SenderId == c.Id)) return AlreadyApplied;
        var subject = ApplicationStatsPrefix + JsonSerializer.Serialize(SharedApplicationStats(c, request.bd));
        try
        {
            return Mailer.Send(world, guild.Id, guild: true, MailKind.GuildApplication, MailCategory.Guild,
                c.Name ?? "", subject, request.bc ?? "", senderId: c.Id) > 0 ? (short)0 : Refused;
        }
        catch (Exception ex)
        {
            Log.Warn("guild", $"application of '{c.Name}' to '{guild.Name}' not saved: {ex.Message}");
            return Refused;
        }
    }

    public short InstantJoinThroughFinder(Player player, Guid guildId, out Guild? joined, out long cooldownEnd)
    {
        joined = null;
        cooldownEnd = 0;
        if (Of(player.Character.Id) is not null) return OpenJoinAlreadyMember;
        if (player.Character.Transient || ById(guildId) is not { } guild
            || guild.Progress.Recruitment is not { JoinSettings: 2 } settings) return NotOpenForJoin;
        if (!FinderEligible(player.Character, settings)) return Refused;
        var check = CheckAdmission(player.Character, guild, out cooldownEnd);
        if (check != 0) return check switch
        {
            JoinInGuild => OpenJoinAlreadyMember, JoinGuildFull => OpenJoinFull,
            JoinAllianceFull => OpenJoinAllianceFull, JoinCooldown => OpenJoinCooldown, _ => Refused,
        };
        var result = CommitAdmission(player.Character, guild, player.Character.Id, player.Character.Name ?? "", 23);
        if (result != 0) return result;
        joined = guild;
        return 0;
    }

    public short AnswerApplication(Player moderator, long mailId, bool accept, out Guid character, out Guild? joined, out string applicantName, out long cooldownEnd)
    {
        character = Guid.Empty;
        joined = null;
        applicantName = "";
        cooldownEnd = 0;
        if (Of(moderator.Character.Id) is not { } guild || guild.Find(moderator.Character.Id) is not { } member
            // Local permission policy: the guild's inbox and the existing invitation right are both required.
            || !Mailer.CanReadGuildMail(member.Role) || (GuildRoles.Permissions(member.Role) & GuildRoles.Invite) == 0)
            return ApplicationNoPermission;
        if (!Mailer.On(world) || world.Mail!.Mail(mailId) is not { Guild: true, Kind: MailKind.GuildApplication } mail
            || mail.Owner != guild.Id || mail.SenderId == Guid.Empty || mail.Expires is { } expiry && expiry <= UtcNow) return ApplicationUnableToAdd;
        applicantName = mail.Sender;
        if (accept)
        {
            var live = Online(mail.SenderId);
            var c = live?.Character ?? world.Characters.Find(mail.SenderId);
            if (c is null || c.Transient) return ApplicationPlayerDeleted;
            applicantName = c.Name ?? mail.Sender;
            var account = c.Account == Guid.Empty ? world.Characters.Account.Id : c.Account;
            if (world.Characters.FindAccount(account)?.BannedUntil is { } banned && banned > UtcNow) return ApplicationUnableToAdd;
            if (Of(c.Id) is { } current && current != guild) return ApplicationAlreadyMember;
            // A committed membership with an application mail left by an interrupted cleanup is a successful retry.
            if (Of(c.Id) is null)
            {
                var check = CheckAdmission(c, guild, out cooldownEnd);
                if (check != 0) return check switch
                {
                    JoinInGuild => ApplicationAlreadyMember, JoinGuildFull => ApplicationGuildFull,
                    JoinAllianceFull => ApplicationAllianceFull, JoinCooldown => ApplicationCooldown, _ => ApplicationUnableToAdd,
                };
                if (CommitAdmission(c, guild, moderator.Character.Id, moderator.Character.Name ?? "", 22) != 0) return ApplicationUnableToAdd;
                character = c.Id;
                joined = guild;
            }
        }
        try { world.Mail!.DeleteMails(guild.Id, true, [mail.Id], 0, [], MailCategory.Guild); }
        catch (Exception ex)
        {
            // Membership is already durable; a retry cannot add the member or award a welcome twice.
            Log.Warn("guild", $"application mail {mail.Id} cleanup failed: {ex.Message}");
            if (joined is null) return ApplicationUnableToAdd;
        }
        Mailer.Notify(world, guild.Id, true);
        return 0;
    }

    public void AfterApplicationJoin(Guid character, Guild guild)
    {
        if (guild.Find(character) is { } member) AfterJoin(guild, member, Online(character));
    }
}
