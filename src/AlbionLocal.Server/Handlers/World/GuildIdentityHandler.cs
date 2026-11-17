using AlbionLocal.Photon;
using AlbionLocal.Server.Game;
using AlbionLocal.Server.Protocol;

namespace AlbionLocal.Server.Network;

/// <summary>
/// The guild's identity, texts, tax and upkeep flag (analysis/work/stageR/guild/SPEC.md §2, §5; the rules in Systems/Guilds.Identity.cs).
/// The requests without a response class get none; a refusal only goes to the log (the client checks the rights itself first).
/// </summary>
public sealed partial class WorldSession
{
    private void GuildRefused(string op, string? why)
    {
        if (why is not null) Log.Info(Name, $"'{Player?.Character.Name}' {op} refused: {why}");
    }

    /// <summary>ChangeGuildLogo (op 358, qf {0-4 ints, 5/6 floats}; no response class): the crest, paid in gold (Guilds:LogoChangeGold).</summary>
    [Handles(OperationCodes.ChangeGuildLogo, Status = SessionStatus.InWorld)]
    private AlbionResponse? ChangeGuildLogo(ChangeGuildLogoRequest req)
    {
        if (Player is { } player) GuildRefused("ChangeGuildLogo", world.Guilds.ChangeLogo(player, new GuildLogo(req.eq, req.er, req.es, req.et, req.eu, req.ev, req.ew)));
        return null;
    }

    /// <summary>GuildBannerChange (op 483, vk {0 banner uniquename}; no response class).</summary>
    [Handles(OperationCodes.GuildBannerChange, Status = SessionStatus.InWorld)]
    private AlbionResponse? GuildBannerChange(GuildBannerChangeRequest req)
    {
        if (Player is { } player) GuildRefused("GuildBannerChange", world.Guilds.ChangeBanner(player, req.pq));
        return null;
    }

    /// <summary>SetGuildCodex (op 236, aa4 {0 text}): the empty ai3, rc 1 when refused.</summary>
    [Handles(OperationCodes.SetGuildCodex, Status = SessionStatus.InWorld)]
    private AlbionResponse? SetGuildCodex(SetGuildCodexRequest req)
    {
        if (Player is not { } player) return null;
        var why = world.Guilds.SetCodex(player, req.afv);
        GuildRefused("SetGuildCodex", why);
        return new SetGuildCodexResponse { ReturnCode = why is null ? (short)0 : Systems.Guilds.Refused };
    }

    /// <summary>GuildGetOptionalStats (op 484, vl {0 guild, 1 bool}): ag4; rc 1 for an unknown guild.</summary>
    [Handles(OperationCodes.GuildGetOptionalStats, Status = SessionStatus.InWorld)]
    private AlbionResponse? GuildGetOptionalStats(GuildGetOptionalStatsRequest req) =>
        world.Guilds.OptionalStats(req.pr) ?? new GuildGetOptionalStatsResponse { ReturnCode = Systems.Guilds.Refused, a6i = req.pr, a6j = [], a6k = [] };

    /// <summary>GuildSetOptionalStats (op 485, vm {0 stats}; no response class, the module's callback reads the rc): rc 0, 19400, 19401.</summary>
    [Handles(OperationCodes.GuildSetOptionalStats, Status = SessionStatus.InWorld)]
    private AlbionResponse? GuildSetOptionalStats(GuildSetOptionalStatsRequest req) =>
        Player is { } player ? new GenericResponse { ReturnCode = world.Guilds.SetOptionalStats(player, req.pt) } : null;

    /// <summary>ChangeNewGuildMemberMail (op 479, qi {0 text}; no response class): the welcome mail of new members.</summary>
    [Handles(OperationCodes.ChangeNewGuildMemberMail, Status = SessionStatus.InWorld)]
    private AlbionResponse? ChangeNewGuildMemberMail(ChangeNewGuildMemberMailRequest req)
    {
        if (Player is { } player) GuildRefused("ChangeNewGuildMemberMail", world.Guilds.SetWelcomeMail(player, req.ez));
        return null;
    }

    /// <summary>GetNewGuildMemberMail (op 480): af6 {0 guild, 1 text}.</summary>
    [Handles(OperationCodes.GetNewGuildMemberMail, Status = SessionStatus.InWorld)]
    private AlbionResponse? GetNewGuildMemberMail(GetNewGuildMemberMailRequest req) => Player is { } player ? world.Guilds.WelcomeMailOf(player) : null;

    /// <summary>ResolveGuildOrPlayerName (op 203, aah {0-2 string[]}): aix {0-2 Guid[]}, Guid.Empty for an unknown name.</summary>
    [Handles(OperationCodes.ResolveGuildOrPlayerName, Status = SessionStatus.InWorld)]
    private AlbionResponse? ResolveGuildOrPlayerName(ResolveGuildOrPlayerNameRequest req) => world.Guilds.Resolve(req.aau, req.aav, req.aaw);

    /// <summary>ChangeGuildTax (op 137, qh {0 a7m}; no response class): GuildUpdate kind 4 to the members.</summary>
    [Handles(OperationCodes.ChangeGuildTax, Status = SessionStatus.InWorld)]
    private AlbionResponse? ChangeGuildTax(ChangeGuildTaxRequest req)
    {
        if (Player is { } player) GuildRefused("ChangeGuildTax", world.Guilds.ChangeTax(player, req.ey));
        return null;
    }

    /// <summary>ChangeGuildPayUpkeepFlag (op 136, qg {0 bool}; no response class): GuildUpdate kind 3 to the members.</summary>
    [Handles(OperationCodes.ChangeGuildPayUpkeepFlag, Status = SessionStatus.InWorld)]
    private AlbionResponse? ChangeGuildPayUpkeepFlag(ChangeGuildPayUpkeepFlagRequest req)
    {
        if (Player is { } player) GuildRefused("ChangeGuildPayUpkeepFlag", world.Guilds.ChangePayUpkeep(player, req.ex));
        return null;
    }
}
