using AlbionLocal.Photon;
using System.Globalization;
using AlbionLocal.Server.Entities;
using AlbionLocal.Server.Game;
using AlbionLocal.Server.Protocol;

namespace AlbionLocal.Server.Systems;

/// <summary>
/// The guild's identity and texts (analysis/work/stageR/guild/SPEC.md §2) and its tax and upkeep flag (§5): the crest (ChangeGuildLogo
/// 358, paid in gold with a cooldown), the banner (GuildBannerChange 483), the codex (SetGuildCodex 236), the guild details' optional
/// stats (GuildGetOptionalStats 484 / GuildSetOptionalStats 485), the welcome mail (479 / 480), the name resolver of the access-rights
/// window (ResolveGuildOrPlayerName 203), the tax (ChangeGuildTax 137, GuildUpdate kind 4) and the pay-upkeep flag (136, kind 3). The guild
/// window keeps banner and codex in the GuildUpdate's full form only (keys 28 and 34-37), so a change goes to the members as kind 0.
/// </summary>
public sealed partial class Guilds
{
    /// <summary>GuildSetOptionalStats' errors (GuildOptionalStatsModule.OnSetGuildStatsResponse 0x1810ba2f0).</summary>
    public const short StatsInternal = 19400, StatsNoPermission = 19401;

    /// <summary>The codex's and the welcome mail's length (GuildCodexModule: 0x200).</summary>
    public const int TextMax = 512;

    /// <summary>The highest GuildDetailOptionalStats value (Crystal20v20LethalWins).</summary>
    private const int LastOptionalStat = 13;

    /// <summary>
    /// ChangeGuildLogo (358, qf {0-4 schema, symbol, colours; 5/6 scale, offset}; no response class, sent after the client's pay dialog):
    /// CHANGE_GUILD_LOGO, the guild's crest cooldown (Guilds:LogoChangeCooldownMinutes) and Guilds:LogoChangeGold gold from the changer;
    /// then the members get GuildUpdate kind 0 and every member in game its 106 (its nameplate to itself and its observers). Null = done,
    /// else why not (the log).
    /// </summary>
    public string? ChangeLogo(Player p, GuildLogo logo)
    {
        if (Of(p.Character.Id) is not { } g || g.Find(p.Character.Id) is not { } m || !Has(m, PermLogo)) return "no CHANGE_GUILD_LOGO right";
        var now = UtcNow;
        if (Config.GuildLogoChangeCooldownMinutes > 0 && g.Profile.LogoChanged > 0
            && new DateTime(g.Profile.LogoChanged, DateTimeKind.Utc).AddMinutes(Config.GuildLogoChangeCooldownMinutes) > now)
            return "the crest changed less than Guilds:LogoChangeCooldownMinutes ago";
        if (!Wallet.TrySpendGold(p, checked(Config.GuildLogoChangeGold * 10000), $"crest of guild '{g.Name}'")) return $"less than {Config.GuildLogoChangeGold} gold";
        world.Saves.SaveNow(p.Character);
        g.Logo = logo;
        g.Profile.LogoChanged = now.Ticks;
        Save(g);
        SaveProfile(g);
        Refresh(g);
        foreach (var member in g.Members)
            if (Online(member.Id) is { } on) Announce(on);
        Log.Info("guild", $"'{p.Character.Name}' changes the crest of guild '{g.Name}'");
        return null;
    }

    /// <summary>GuildBannerChange (483, vk {0 the banner's uniquename}; no response class): CHANGE_GUILD_LOGO [assume]; kind 0 to the members (key 28).</summary>
    public string? ChangeBanner(Player p, string? banner)
    {
        if (Of(p.Character.Id) is not { } g || g.Find(p.Character.Id) is not { } m || !Has(m, PermLogo)) return "no CHANGE_GUILD_LOGO right";
        g.Banner = Cut(banner, 128);
        Save(g);
        Refresh(g);
        Log.Info("guild", $"'{p.Character.Name}' sets the banner of guild '{g.Name}' to '{g.Banner}'");
        return null;
    }

    /// <summary>
    /// SetGuildCodex (236, aa4 {0 text}; an empty response): CHANGE_GUILD_LOGO or EDIT_GUILD_DESCRIPTION [infer: the module checks the
    /// client's permission 2], at most 512 characters (the module's own limit); the codex, its editor and time go to the members as kind 0
    /// (keys 34-37). Null = done.
    /// </summary>
    public string? SetCodex(Player p, string? text)
    {
        if (Of(p.Character.Id) is not { } g || g.Find(p.Character.Id) is not { } m || !Has(m, PermLogo | PermDescription)) return "no right to edit the codex";
        if ((text ?? "").Length > TextMax) return $"longer than {TextMax} characters";
        (g.Profile.Codex, g.Profile.CodexBy, g.Profile.CodexByName, g.Profile.CodexAt) = (text ?? "", m.Id, m.Name, world.GameNow);
        SaveProfile(g);
        Refresh(g);
        Log.Info("guild", $"'{p.Character.Name}' sets the codex of guild '{g.Name}' ({g.Profile.Codex.Length} characters)");
        return null;
    }

    /// <summary>GuildUpdate kind 0 (without the tutorial popup) to every member in game: the window's banner, codex and crest.</summary>
    private void Refresh(Guild g) => SendTo(g, FullUpdate(g, tutorial: false));

    /// <summary>
    /// GuildGetOptionalStats (484, vl {0 guild, 1 bool}): ag4 {0 the guild, 1 the chosen stats, 2 their values, 3 FlaggingStatus}. Values
    /// (ge5.a 0x181cbda40): GuildAge = the founding time (the client shows its age), the faction ones 0 (the faction warfare agent's), the
    /// hellgate and crystal counts 0 (not counted here). Null = no such guild.
    /// </summary>
    public GuildGetOptionalStatsResponse? OptionalStats(Guid id)
    {
        if (ById(id) is not { } g) return null;
        var stats = g.Profile.OptionalStats;
        return new GuildGetOptionalStatsResponse
        {
            a6i = g.Id, a6j = [.. stats], a6k = [.. stats.Select(s => s == 1 ? g.Created.Ticks : 0L)],
        };
    }

    /// <summary>GuildSetOptionalStats (485, vm {0 the stats per slot}): 19401 without the right, 19400 for an unknown stat; stored.</summary>
    public short SetOptionalStats(Player p, int[]? stats)
    {
        if (Of(p.Character.Id) is not { } g || g.Find(p.Character.Id) is not { } m || !Has(m, PermLogo | PermDescription)) return StatsNoPermission;
        if (stats is null || stats.Length > 8 || stats.Any(s => s is < 0 or > LastOptionalStat)) return StatsInternal;
        g.Profile.OptionalStats = stats;
        SaveProfile(g);
        Log.Info("guild", $"'{p.Character.Name}' sets the optional stats of guild '{g.Name}': {string.Join(",", stats)}");
        return 0;
    }

    /// <summary>ChangeNewGuildMemberMail (479, qi {0 text}; no response class): EDIT_GUILD_DESCRIPTION [assume], at most 512 characters; "" = no welcome mail.</summary>
    public string? SetWelcomeMail(Player p, string? text)
    {
        if (Of(p.Character.Id) is not { } g || g.Find(p.Character.Id) is not { } m || !Has(m, PermDescription)) return "no EDIT_GUILD_DESCRIPTION right";
        g.Profile.WelcomeMail = Cut(text, TextMax);
        SaveProfile(g);
        Log.Info("guild", $"'{p.Character.Name}' sets the welcome mail of guild '{g.Name}' ({g.Profile.WelcomeMail.Length} characters)");
        return null;
    }

    /// <summary>GetNewGuildMemberMail (480): af6 {0 the guild [assume], 1 the text}; empty outside a guild.</summary>
    public GetNewGuildMemberMailResponse WelcomeMailOf(Player p) =>
        Of(p.Character.Id) is { } g ? new GetNewGuildMemberMailResponse { a19 = g.Id, a2a = g.Profile.WelcomeMail } : new GetNewGuildMemberMailResponse { a2a = "" };

    /// <summary>
    /// ResolveGuildOrPlayerName (203, aah {0, 1, 2 string[]}): aix {0, 1, 2 Guid[]} of the same lengths. Each name resolves to the character,
    /// else the guild, else the alliance (by name or tag) of that name, Guid.Empty when none [assume: which array holds which kind is not
    /// known, so every name is tried as each].
    /// </summary>
    public ResolveGuildOrPlayerNameResponse Resolve(string[]? a, string[]? b, string[]? c)
    {
        Guid One(string? name) =>
            string.IsNullOrWhiteSpace(name) ? Guid.Empty
            : world.Characters.Find(name.Trim())?.Id ?? Find(name)?.Id ?? world.Alliances.Find(name)?.Id ?? Guid.Empty;
        return new ResolveGuildOrPlayerNameResponse { bli = [.. (a ?? []).Select(One)], blj = [.. (b ?? []).Select(One)], blk = [.. (c ?? []).Select(One)] };
    }

    /// <summary>
    /// ChangeGuildTax (137, qh {0 a7m: 1500 = 15 %}; no response class): CHANGE_GUILD_TAX, within gamedata UserTaxValues Guild 0-100 %; stored
    /// and sent to the members as GuildUpdate kind 4 (key 14). Only Guilds:SilverTax takes a tax from loot here (SPEC §5): the stored rate is
    /// shown, not charged.
    /// </summary>
    public string? ChangeTax(Player p, long a7m)
    {
        if (Of(p.Character.Id) is not { } g || g.Find(p.Character.Id) is not { } m || !Has(m, PermTax)) return "no CHANGE_GUILD_TAX right";
        if (a7m is < 0 or > 10000) return "outside 0-100 %";
        g.TaxA7m = a7m;
        Save(g);
        SendTo(g, new GuildUpdateEvent { cf1 = g.Id, cgb = 4, cgf = TaxOf(g), cgg = g.Name, cgh = "", cgt = "", cgx = "", cgz = "", cg1 = "" });
        Log.Info("guild", string.Create(CultureInfo.InvariantCulture, $"'{p.Character.Name}' sets the tax of guild '{g.Name}' to {a7m / 100.0:0.##} %"));
        return null;
    }

    /// <summary>ChangeGuildPayUpkeepFlag (136, qg {0 bool}; no response class): CHANGE_PAYUPKEEPFLAG; stored and sent as GuildUpdate kind 3 (key 13). No upkeep is charged here.</summary>
    public string? ChangePayUpkeep(Player p, bool pay)
    {
        if (Of(p.Character.Id) is not { } g || g.Find(p.Character.Id) is not { } m || !Has(m, PermUpkeep)) return "no CHANGE_PAYUPKEEPFLAG right";
        g.Profile.PayUpkeep = pay;
        SaveProfile(g);
        SendTo(g, new GuildUpdateEvent { cf1 = g.Id, cgb = 3, cge = pay, cgg = g.Name, cgh = "", cgt = "", cgx = "", cgz = "", cg1 = "" });
        Log.Info("guild", $"'{p.Character.Name}' sets the pay-upkeep flag of guild '{g.Name}' to {pay}");
        return null;
    }
}
