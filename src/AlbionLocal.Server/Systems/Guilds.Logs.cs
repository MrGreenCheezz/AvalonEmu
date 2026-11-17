using AlbionLocal.Photon;
using AlbionLocal.Server.Entities;
using AlbionLocal.Server.Game;
using AlbionLocal.Server.Protocol;

namespace AlbionLocal.Server.Systems;

/// <summary>
/// The guild's history logs (analysis/work/stageR/guild/SPEC.md §3): what the server really does is written as it happens: silver
/// deposits and withdrawals of the guild account (LogType silver: bhv 2 / 3, the payout of .guild payout as a withdrawal) and the member
/// events (LogType just_event: invited 21, application accepted 22, instant join 23, left 24, kicked 25, role assigned 26). The guild window's
/// log views (AbstractGuildHistoryLogView) page through them with GetGuildAccountLogs (159) and copy them all with
/// GetGuildAccountLogsLargeAmount (160, answered by GuildAccountLogEvent 418). Gold and siphoned energy never move here: those lists are
/// empty. Rows older than <see cref="LogKeepDays"/> go at the start.
/// </summary>
public sealed partial class Guilds
{
    /// <summary>The client's longest DateFilter (SixtyDays): rows older than this are never asked for.</summary>
    public const int LogKeepDays = 60;

    /// <summary>One log row of <paramref name="g"/> now, saved at once (a failed write is logged and dropped: the move it records stands).</summary>
    public void AddLog(Guild g, int type, int reason, string actor, string parameter, long amount = 0)
    {
        if (Board is not { } board) return;
        try { board.AddGuildLog(g.Id, new GuildLogEntry(type, reason, actor, parameter, amount, UtcNow.Ticks)); }
        catch (Exception ex) { Log.Warn("guild", $"log row of guild '{g.Name}' not saved: {ex.Message}"); }
    }

    /// <summary>
    /// The rows a log view asks for (tq / tr: 0 guild, 1 LogType, 2 offset, 3 count, 4 EventFilter, 5 AmountFilter, 6 search text, 7 DateFilter
    /// days), newest first: an event filter (1 joiners 21-23, 2 leavers 24-25, 3 role changes 26), an amount filter (1 deposits, 2
    /// withdrawals) and the text (in the actor's name or the parameter, any case). Null = not the asker's guild.
    /// </summary>
    public List<GuildLogEntry>? Logs(Player p, Guid guild, int type, int events, int amounts, string? search, int days)
    {
        if (Of(p.Character.Id) is not { } g || g.Id != guild) return null;
        if (Board is not { } board) return [];
        var since = days > 0 ? UtcNow.AddDays(-Math.Min(days, LogKeepDays)).Ticks : 0;
        var text = search?.Trim() ?? "";
        return
        [
            .. board.GuildLogs(g.Id, type, since).Where(e =>
                (events switch
                {
                    1 => e.Reason is GuildLogReason.Invited or GuildLogReason.Accepted or GuildLogReason.InstantJoined,
                    2 => e.Reason is GuildLogReason.Left or GuildLogReason.Kicked,
                    3 => e.Reason == GuildLogReason.AssignedRole,
                    _ => true,
                })
                && (amounts switch { 1 => e.Amount > 0, 2 => e.Amount < 0, _ => true })
                && (text.Length == 0 || e.Actor.Contains(text, StringComparison.OrdinalIgnoreCase) || e.Parameter.Contains(text, StringComparison.OrdinalIgnoreCase))),
        ];
    }

    /// <summary>
    /// GetGuildAccountLogs' page (afj, df8.d.c 0x180b0c800): 0 the actors, 1 the bhv reasons (an int enum array), 2 the parameters, 3 the
    /// amounts (a7m), 4 the times; rows <paramref name="offset"/>.. of at most <paramref name="count"/> (the client asks 101 to see a next page).
    /// </summary>
    public static GetGuildAccountLogsResponse LogPage(IReadOnlyList<GuildLogEntry> rows, int offset, int count)
    {
        var page = rows.Skip(Math.Max(0, offset)).Take(Math.Clamp(count, 0, 1000)).ToList();
        return new GetGuildAccountLogsResponse
        {
            aw9 = [.. page.Select(e => e.Actor)], axa = [.. page.Select(e => e.Reason)], axb = [.. page.Select(e => e.Parameter)],
            axc = [.. page.Select(e => e.Amount)], axd = [.. page.Select(e => e.At)],
        };
    }

    /// <summary>
    /// GuildAccountLogEvent 418 (apt, df8.c 0x180b0ea30) with every row: 0 the first row's index, 1 the total (the client finishes when row
    /// total − 1 is filled), 2-6 the rows as in <see cref="LogPage"/>. Null without rows: total 0 would make the client index row −1.
    /// </summary>
    public static GuildAccountLogEventEvent? LogEvent(IReadOnlyList<GuildLogEntry> rows) =>
        rows.Count == 0 ? null : new GuildAccountLogEventEvent
        {
            cea = 0, ceb = rows.Count, cec = [.. rows.Select(e => e.Actor)], ced = [.. rows.Select(e => e.Reason)], cee = [.. rows.Select(e => e.Parameter)],
            cef = [.. rows.Select(e => e.Amount)], ceg = [.. rows.Select(e => e.At)],
        };
}
