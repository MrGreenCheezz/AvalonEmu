using AlbionLocal.Photon;
using AlbionLocal.Server.Game;
using AlbionLocal.Server.Protocol;

namespace AlbionLocal.Server.Systems;

public sealed partial class Guilds
{
    // 345319 GuildNewMemberMailGui.OnShow 107DBE3 checks this exact permission;
    // InitGui 107D5B7 sets UIInput.characterLimit to 0x1f4 (500 UTF-16 characters).
    private const long ManageNewMemberMail = 0x200000;
    private const int NewMemberMailLimit = 500;

    public Guild? NewMemberMailOwner(Guid character) =>
        Of(character) is { } g && g.Find(character) is { } member
        && (GuildRoles.Permissions(member.Role) & ManageNewMemberMail) != 0 ? g : null;

    /// <summary>345319 cxm.u 9F3F57-9F4272 accepts the own guild's configuration only with context 0,
    /// and copies response keys 2-15 into c2q. Context1 is public details via deb.a(aip) ADC160.
    /// Explicit empty strings keep the native editor out of Loading.</summary>
    public RequestGuildRecruitmentInfoResponse RecruitmentInfo(Guid character, Guid guild, byte context)
    {
        var owner = context == 1 ? ById(guild) : NewMemberMailOwner(character);
        if (owner is null || owner.Id != guild || context > 1)
            return new() { ReturnCode = 7400, bkc = guild, bkd = context,
                bkg = "", bki = "", bkl = "", bkn = -1, bko = -1 };
        var settings = owner.Progress.Recruitment ?? new GuildRecruitmentSettings();
        var response = new RequestGuildRecruitmentInfoResponse
        {
            bkc = owner.Id, bkd = context,
            bke = settings.PrimaryLanguage, bkf = settings.SecondaryLanguage,
            bkg = settings.Description, bkh = settings.Author, bki = settings.AuthorName,
            bkj = settings.JoinSettings, bkk = settings.BeginnerFriendly, bkl = settings.DiscordLink,
            bkm = settings.Home, bkn = settings.ActiveFrom, bko = settings.ActiveTo,
            bkp = settings.Activities, bkq = settings.MinimumFame, bkr = settings.MinimumPvpFame,
        };
        if (context == 1) FillFinderDetails(response, character, owner);
        return response;
    }

    /// <summary>184 changes the current member's guild only. Native language/home/time enums and
    /// the 350-character description limit come from 345319; hours use bu1 (-1 means unset in 185).
    /// JoinSettings controls the public application/open-join routes through the same stored values.</summary>
    public bool ChangeRecruitment(Guid character, string author, UpdateGuildRecruitmentInfoRequest request)
    {
        if (NewMemberMailOwner(character) is not { } g
            || request.ahj is < 1 or > 31 || request.ahk > 31 || request.ahm > 2
            || request.ahp is < 1 or > 10 || request.ahq > 23 || request.ahr > 23
            || request.ahs < 0 || (request.ahs & ~0xfff) != 0 || request.aht < 0 || request.ahu < 0
            || request.ahl is null || request.ahl.Length > 350 || request.aho is null) return false;
        var before = g.Progress.Recruitment;
        g.Progress.Recruitment = new()
        {
            PrimaryLanguage = request.ahj, SecondaryLanguage = request.ahk,
            Description = request.ahl, Author = character, AuthorName = author,
            JoinSettings = request.ahm, BeginnerFriendly = request.ahn, DiscordLink = request.aho,
            Home = request.ahp, ActiveFrom = request.ahq, ActiveTo = request.ahr,
            Activities = request.ahs, MinimumFame = request.aht, MinimumPvpFame = request.ahu,
        };
        try { store?.SaveGuildProgress(g); }
        catch (Exception ex)
        {
            g.Progress.Recruitment = before;
            Log.Warn("guild", $"recruitment of guild '{g.Name}' not saved: {ex.Message}");
            return false;
        }
        return true;
    }

    /// <summary>Save before acknowledging the native editor. An empty message disables future welcomes;
    /// a failed write restores the previous text. Existing mail and existing members are never rewritten.</summary>
    public bool ChangeNewMemberMail(Guid character, string? text)
    {
        if (NewMemberMailOwner(character) is not { } g || text is null || text.Length > NewMemberMailLimit
            || text.Any(c => char.IsControl(c) && c is not ('\r' or '\n' or '\t'))) return false;
        var before = g.Progress.NewMemberMail;
        if (text == before) return true;
        g.Progress.NewMemberMail = text;
        try { store?.SaveGuildProgress(g); }
        catch (Exception ex)
        {
            g.Progress.NewMemberMail = before;
            Log.Warn("guild", $"new-member mail of guild '{g.Name}' not saved: {ex.Message}");
            return false;
        }
        return true;
    }

    private void WelcomeNewMember(Guid character, Guild g)
    {
        if (string.IsNullOrWhiteSpace(g.Progress.NewMemberMail)) return;
        // Installed 345319 GUILD_NEW_MEMBER_MAIL_DESCRIPTION promises an automatic message to new
        // members. Use the normal durable guild-category inbox; no postage, attachments or login replay.
        // The subject is local presentation text: the original server's subject was not captured.
        Mailer.Send(world, character, guild: false, MailKind.Normal, MailCategory.Guild,
            g.Name, $"Welcome to {g.Name}", g.Progress.NewMemberMail, fromGuild: true);
    }
}
