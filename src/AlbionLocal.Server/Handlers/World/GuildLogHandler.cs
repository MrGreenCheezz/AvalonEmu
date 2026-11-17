using AlbionLocal.Photon;
using AlbionLocal.Server.Protocol;

namespace AlbionLocal.Server.Network;

/// <summary>
/// The guild window's logs (analysis/work/stageR/guild/SPEC.md §3; the rows in Systems/Guilds.Logs.cs): the history logs of the account
/// and the members (GetGuildAccountLogs 159 page by page, GetGuildAccountLogsLargeAmount 160 all at once as GuildAccountLogEvent 418), and
/// the chest and access-right logs, which are empty: this server has no guild chests or access-right lists that could write any.
/// </summary>
public sealed partial class WorldSession
{
    /// <summary>
    /// GetGuildAccountLogs (op 159, tq {0 guild, 1 LogType, 2 offset, 3 count, 4 EventFilter, 5 AmountFilter, 6 text, 7 days}): afj with the
    /// matching rows of the page, newest first; empty arrays for another guild (the official answer of an empty log had the five keys).
    /// </summary>
    [Handles(OperationCodes.GetGuildAccountLogs, Status = SessionStatus.InWorld)]
    private AlbionResponse? GetGuildAccountLogs(GetGuildAccountLogsRequest req)
    {
        if (Player is not { } player) return null;
        var rows = world.Guilds.Logs(player, req.mv, req.mw, req.mz, req.m0, req.m1, req.m2) ?? [];
        return Systems.Guilds.LogPage(rows, req.mx, req.my);
    }

    /// <summary>
    /// GetGuildAccountLogsLargeAmount (op 160, tr as 159; no response class): the client's "copy to clipboard" of a long log gets every
    /// matching row as one GuildAccountLogEvent 418 (nothing when there is none).
    /// </summary>
    [Handles(OperationCodes.GetGuildAccountLogsLargeAmount, Status = SessionStatus.InWorld)]
    private AlbionResponse? GetGuildAccountLogsLargeAmount(GetGuildAccountLogsLargeAmountRequest req)
    {
        if (Player is not { } player) return null;
        var rows = world.Guilds.Logs(player, req.m3, req.m4, req.m7, req.m8, req.m9, req.na) ?? [];
        if (Systems.Guilds.LogEvent(rows) is { } e) SendEvent(e);
        return null;
    }

    /// <summary>GetChestLogs (op 157, tf): ae6 with empty arrays: no guild chest here logs item moves (SPEC §3.2).</summary>
    [Handles(OperationCodes.GetChestLogs, Status = SessionStatus.InWorld)]
    private AlbionResponse? GetChestLogs(GetChestLogsRequest req) => new GetChestLogsResponse().FillEmpty();

    /// <summary>GetAccessRightLogs (op 158, s9): ae1 with empty arrays: no access-right list here changes (SPEC §3.3).</summary>
    [Handles(OperationCodes.GetAccessRightLogs, Status = SessionStatus.InWorld)]
    private AlbionResponse? GetAccessRightLogs(GetAccessRightLogsRequest req) => new GetAccessRightLogsResponse().FillEmpty();

    /// <summary>SetSavingChestLogsFlag (op 441, aba {0, 1 Guid, 2 bool}; no response class): no chest here keeps logs, nothing is stored [assume].</summary>
    [Handles(OperationCodes.SetSavingChestLogsFlag, Status = SessionStatus.InWorld)]
    private AlbionResponse? SetSavingChestLogsFlag(SetSavingChestLogsFlagRequest req)
    {
        Log.Info(Name, $"SetSavingChestLogsFlag {req.af8}/{req.af9} {req.aga}: no guild chests here, ignored");
        return null;
    }

    /// <summary>GetSavingChestLogsFlag (op 442, ux {0, 1 Guid}; no response class): the bare rc 0 [assume: no chest, so no flag to report].</summary>
    [Handles(OperationCodes.GetSavingChestLogsFlag, Status = SessionStatus.InWorld)]
    private AlbionResponse? GetSavingChestLogsFlag(GetSavingChestLogsFlagRequest req) => new GenericResponse();
}
