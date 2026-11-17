using System.Globalization;
using AlbionLocal.Server.Entities;
using AlbionLocal.Server.Game;
using AlbionLocal.Server.Protocol;

namespace AlbionLocal.Server.Systems;

/// <summary>
/// The gold rewards of play (analysis/work/stageR/gold/SPEC.md, Gold:Rewards:*, <see cref="GoldRewardOptions"/>): NOT official behaviour. The
/// official game gives no gold in play (SPEC §1); this server has no store and no gold market, so at the user's request (2026-11-17) a few
/// bounded moments of normal play give a little gold: the day's first <see cref="GoldRewardOptions.DailyFameRequired"/> fame, the Conqueror's
/// weekly chest reached, an Albion Journal reward claimed (a weekly cap), the faction campaign's last daily reward. Each goes through
/// <see cref="Wallet.CreditGold"/> (UpdateMoney 81) with a system message that names the reason [assume: the client has no reward display
/// that carries gold for these features, SPEC §2]. The caps live in <see cref="GoalLog.Gold"/> (the goals JSON column). Bots get none.
/// Under <see cref="World.Sync"/>.
/// </summary>
public static class GoldRewards
{
    private static GoldRewardOptions? On(Player p) => p.World.Config.GoldRewards is { Enabled: true } o && !p.IsBot ? o : null;

    /// <summary><paramref name="p"/>'s gold log with the daily and weekly periods (the bounties') rolled to now.</summary>
    public static GoldLog LogOf(Player p)
    {
        var log = (p.Character.Goals ??= new GoalLog()).Gold ??= new GoldLog();
        var now = Seasons.UtcNow(p.World);
        var (day, week) = (p.World.Bounties.DayStart(now).Ticks, p.World.Bounties.WeekStart(now).Ticks);
        if (log.Day != day) (log.Day, log.DayFame, log.DailyGiven) = (day, 0, false);
        if (log.Week != week) (log.Week, log.JournalGold, log.ConquerorGiven) = (week, 0, false);
        return log;
    }

    /// <summary><paramref name="gold"/> whole gold to <paramref name="p"/>: the wallet (UpdateMoney) and a system message "+N gold: <paramref name="reason"/>".</summary>
    private static void Give(Player p, long gold, string reason)
    {
        if (gold <= 0) return;
        Wallet.CreditGold(p, gold * 10000, "gold reward: " + reason);
        p.Session.Send(new SystemMessageEvent { d0p = string.Create(CultureInfo.InvariantCulture, $"[Gold] +{gold} gold: {reason}."), d0q = 0 });
    }

    /// <summary>
    /// A fame gain of <paramref name="p"/> (a kill, a player kill, a harvest, a catch, a crop, a craft; not a tome, not a GM's grant): it counts for
    /// the day, and the first time the day reaches DailyFameRequired the DailyFame gold goes out.
    /// </summary>
    public static void OnFame(Player p, long gain, FameContext ctx)
    {
        if (On(p) is not { DailyFame: > 0 } o || gain <= 0 || ctx.Source == FameSource.Tome) return;
        var log = LogOf(p);
        if (log.DailyGiven) return;
        log.DayFame = Math.Min(log.DayFame + gain, long.MaxValue / 4);
        if (log.DayFame < o.DailyFameRequired * 10000) return;
        log.DailyGiven = true;
        Give(p, o.DailyFame, string.Create(CultureInfo.InvariantCulture, $"your first {o.DailyFameRequired:N0} fame today"));
    }

    /// <summary>The Conqueror's Challenge's weekly might reached its chest (<see cref="Conqueror.Add"/>): ConquerorWeekly gold, once a week.</summary>
    public static void ConquerorWeekly(Player p)
    {
        if (On(p) is not { ConquerorWeekly: > 0 } o) return;
        var log = LogOf(p);
        if (log.ConquerorGiven) return;
        log.ConquerorGiven = true;
        Give(p, o.ConquerorWeekly, "the Conqueror's Challenge of this week completed");
    }

    /// <summary>An Albion Journal reward claimed (<see cref="AlbionJournal.Claim"/>): JournalClaim gold, up to JournalWeeklyCap a week.</summary>
    public static void JournalClaimed(Player p, string id)
    {
        if (On(p) is not { JournalClaim: > 0 } o) return;
        var log = LogOf(p);
        var gold = o.JournalWeeklyCap > 0 ? Math.Min(o.JournalClaim, o.JournalWeeklyCap - log.JournalGold) : o.JournalClaim;
        if (gold <= 0) return;
        log.JournalGold += gold;
        Give(p, gold, $"Albion Journal reward {id} claimed"
            + (o.JournalWeeklyCap > 0 && log.JournalGold >= o.JournalWeeklyCap ? " (this week's journal gold is complete)" : ""));
    }

    /// <summary>The faction campaign's last daily reward reached (<see cref="FactionCampaign.OnPoints"/>, campaign day <paramref name="day"/>): FactionDaily gold, once a day.</summary>
    public static void FactionDaily(Player p, int day)
    {
        if (On(p) is not { FactionDaily: > 0 } o) return;
        var log = LogOf(p);
        if (log.FactionDay == day) return;
        log.FactionDay = day;
        Give(p, o.FactionDaily, "the faction campaign's daily rewards completed");
    }

    /// <summary>For .goldrewards: the sources and where <paramref name="p"/> stands in this period.</summary>
    public static string Describe(Player p)
    {
        var o = p.World.Config.GoldRewards;
        if (!o.Enabled) return "Gold rewards are off (Gold:Rewards:Enabled false).";
        var log = LogOf(p);
        var today = FactionCampaign.Day(Seasons.UtcNow(p.World));
        return string.Create(CultureInfo.InvariantCulture,
            $"Gold rewards: daily fame {(log.DailyGiven ? "given" : $"{log.DayFame / 10000:N0}/{o.DailyFameRequired:N0} fame")} ({o.DailyFame} gold); "
            + $"Conqueror's week {(log.ConquerorGiven ? "given" : "open")} ({o.ConquerorWeekly} gold); "
            + $"journal {log.JournalGold}/{(o.JournalWeeklyCap > 0 ? o.JournalWeeklyCap.ToString(CultureInfo.InvariantCulture) : "no cap")} this week ({o.JournalClaim} a claim); "
            + $"faction campaign today {(log.FactionDay == today ? "given" : "open")} ({o.FactionDaily} gold)");
    }
}
