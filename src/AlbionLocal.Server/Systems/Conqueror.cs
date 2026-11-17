using System.Globalization;
using AlbionLocal.Photon;
using AlbionLocal.Server.Data;
using AlbionLocal.Server.Entities;
using AlbionLocal.Server.Game;
using AlbionLocal.Server.Protocol;

namespace AlbionLocal.Server.Systems;

/// <summary>
/// The personal might and the Conqueror's Challenge (analysis/work/stageR/stats/SPEC.md §2-3, "SPEC" here; the data rules of stageI pvp-goals SPEC
/// §1.3). Every might the client data gives a character (pvpchallenge's objectives, as <see cref="GuildGoals"/> counts it for the guild but without
/// Guild:MightFactor and without needing a guild: PvE kill fame × 0.0016, a third in a random dungeon without a party; gathering and fishing fame ×
/// 0.017; a corrupted dungeon's objective; nothing in an orange-warning zone) counts
/// <list type="bullet">
/// <item>for the guild season of the client data that runs, by its personaltype (the Might Gain window, GetPersonalMightStats 453);</item>
/// <item>for this week (Bounties' week, the weeklychallenge's starttimeref DOWNTIME [infer]): the weekly bar of op 451, the weeklychallenge's favor
/// rewards (2000 FAVOR at 5000 / 10000 / 15000, everyone), the weekly chest at 20000 (UNIQUE_LOOTCHEST_CONQUEROR_WEEKLY_REWARD, claimed with op 452:
/// with premium, else state 1 whose claim button opens the premium store, as the client's OnClickClaim).</item>
/// </list>
/// Kept in <see cref="GoalLog.Conqueror"/>. Touched only under <see cref="World.Sync"/>.
/// </summary>
public static class Conqueror
{
    /// <summary>bep.RewardStates: LOCKED, UNLOCKED_NOT_CLAIMABLE, UNLOCKED_CLAIMABLE, CLAIMED.</summary>
    public const byte Locked = 0, NotClaimable = 1, Claimable = 2, Claimed = 3;

    /// <summary>
    /// ConquerorsChallengeWeeklyItemReward.OnClaimResponse (0x18113b3b0): 18080 NEEDSPREMIUM, 18081 ALREADY_CLAIMED, 18084 the inventory's
    /// no-free-space line; 18082 / 18083 / any other → "@GENERIC_ERROR_INTERNAL_ERROR".
    /// </summary>
    public const short RcOk = 0, RcNeedsPremium = 18080, RcAlreadyClaimed = 18081, RcGeneric = 18082, RcNoSpace = 18084;

    private static MightRules Rules(World world) => world.GvgSeasons?.Might ?? MightRules.None;

    /// <summary><paramref name="p"/>'s log with the week rolled to now (a new week: no might, no rewards, the chest locked).</summary>
    public static ConquerorLog LogOf(Player p)
    {
        var goals = p.Character.Goals ??= new GoalLog();
        var log = goals.Conqueror ??= new ConquerorLog();
        var week = p.World.Bounties.WeekStart(Seasons.UtcNow(p.World)).Ticks;
        if (log.Week != week) (log.Week, log.WeekMight, log.WeekRewards, log.Chest) = (week, 0, 0, Locked);
        return log;
    }

    /// <summary>A PvE fame gain (a7m) of <paramref name="p"/>: its might by the pve / gathering / fishing rule (see the class).</summary>
    public static void OnFame(Player p, long gain, FameContext ctx)
    {
        if (ctx.Source switch { FameSource.Kill => "pve", FameSource.Harvest => "gathering", FameSource.Fishing => "fishing", _ => null } is not { } rule
            || !Rules(p.World).Fame.TryGetValue(rule, out var r) || r.Factor <= 0) return;
        var solo = rule == "pve" && DungeonLayout.IsInstance(p.Map?.Cluster)
            && (p.World.Parties.Of(p.Character.Id) is not { } party || party.Members.Count <= 1);
        Add(p, r.Objective, Rules(p.World).Personal.GetValueOrDefault(rule),
            gain * r.Factor * r.ZoneFactor(Pvp.RulesOf(p.World, p.Map).WarningType) * (solo ? r.SoloDungeonFactor : 1));
    }

    /// <summary>A might objective of the client data done by <paramref name="p"/> (a corrupted dungeon's mightreward): its might.</summary>
    public static void OnObjective(Player p, string? objective)
    {
        if (objective is null || !Rules(p.World).Objectives.TryGetValue(objective, out var o) || o.Might <= 0) return;
        Add(p, o.Type, Rules(p.World).Personal.GetValueOrDefault(objective), o.Might * 10000);
    }

    /// <summary>
    /// <paramref name="a7m"/> might of objective type <paramref name="type"/> for <paramref name="p"/>: the running client season's personaltype
    /// (<paramref name="personal"/>, else the category mapping the type), this week's might, the favor rewards and the chest it reaches.
    /// </summary>
    public static void Add(Player p, string type, string? personal, double a7m)
    {
        if (a7m < 1 || double.IsNaN(a7m) || p.IsBot) return;
        var world = p.World;
        var add = (long)Math.Min(a7m, long.MaxValue / 4);
        var log = LogOf(p);
        var season = world.GuildGoals.Season;
        var name = personal ?? season.Might.FirstOrDefault(c => c.Objectives.Contains(type))?.Name;
        if (season.Index is { } index && name is not null)
        {
            if (!log.Seasons.TryGetValue(index, out var stats)) log.Seasons[index] = stats = new(StringComparer.Ordinal);
            stats[name] = Math.Min(stats.GetValueOrDefault(name) + add, long.MaxValue / 4);
        }
        log.WeekMight = Math.Min(log.WeekMight + add, long.MaxValue / 4);
        world.Saves.MarkChanged(p.Character);
        if (Rules(world).Weekly is not { } weekly) return;
        // The favor rewards in file order (their requirements rise), each once a week; FAVOR is the Brecilien trader's currency here too.
        while (log.WeekRewards < weekly.Rewards.Count && weekly.Rewards[log.WeekRewards] is var r && log.WeekMight >= r.Might)
        {
            log.WeekRewards++;
            if (!(Premium.Is(p) ? r.Premium : r.NonPremium) || r.Favor <= 0) continue;
            GiveFavor(p, r.Favor);
            Log.Info("goals", string.Create(CultureInfo.InvariantCulture, $"'{p.Character.Name}' Conqueror's Challenge: {r.Might / 10000} weekly might, +{r.Favor / 10000} favor"));
        }
        if (log.Chest == Locked && weekly.Might > 0 && log.WeekMight >= weekly.Might)
            p.Session.Send(new WeeklyPvpChallengeRewardStateUpdateEvent { d6s = StateOf(p, log, weekly) });
    }

    /// <summary>
    /// <paramref name="a7m"/> FAVOR (the currency of currencies.xml the Conqueror's window shows and the Brecilien trader takes): the balance and
    /// UpdateCurrency (85) {2 its index, 3 the gain, 9 the balance} [infer: as the faction currencies, Factions.SendCurrency].
    /// </summary>
    private static void GiveFavor(Player p, long a7m)
    {
        p.Character.MistFavor += a7m;
        if (p.World.Factions.Data is { } data && data.CurrencyIndex.TryGetValue("FAVOR", out var index))
            p.Session.Send(new UpdateCurrencyEvent { d2x = index, d2y = a7m, d24 = p.Character.MistFavor });
    }

    /// <summary>The weekly chest's state: CLAIMED, LOCKED below the requirement, else CLAIMABLE with premium and UNLOCKED_NOT_CLAIMABLE without.</summary>
    public static byte StateOf(Player p, ConquerorLog log, WeeklyChallenge? weekly) =>
        log.Chest == Claimed ? Claimed
        : weekly is null || weekly.Might <= 0 || log.WeekMight < weekly.Might ? Locked
        : Premium.Is(p) ? Claimable : NotClaimable;

    /// <summary>
    /// GetPvpChallengeData (451, c2y_d_6 0x180a52a30): 0 the season might (the sum of the running client season's, none in a local season), 1 this
    /// week's might, 2 the chest's state. Key 0 left at 0 when there is none, as captured.
    /// </summary>
    public static GetPvpChallengeDataResponse Data(Player p)
    {
        var log = LogOf(p);
        var season = p.World.GuildGoals.Season.Index is { } index && log.Seasons.TryGetValue(index, out var stats) ? stats.Values.Sum() : 0;
        return new GetPvpChallengeDataResponse { a3f = season, a3g = log.WeekMight, a3h = StateOf(p, log, Rules(p.World).Weekly) };
    }

    /// <summary>
    /// ClaimPvpChallengeWeeklyReward (452; OnClickClaim sends it only in state 2): the chest into the inventory, CLAIMED (saved first) and 497 {0 3};
    /// CLAIMED already 18081, without premium 18080, no space 18084, locked or no data 18082.
    /// </summary>
    public static short ClaimWeekly(Player p)
    {
        var log = LogOf(p);
        var weekly = Rules(p.World).Weekly;
        switch (StateOf(p, log, weekly))
        {
            case Claimed: return RcAlreadyClaimed;
            case NotClaimable: return RcNeedsPremium;
            case Locked: return RcGeneric;
        }
        if (p.World.Items.Find(weekly!.Item) is not { CanCreate: true } type)
        {
            Log.Warn("goals", $"Conqueror's Challenge: the weekly chest {weekly.Item} is not in items.xml");
            return RcGeneric;
        }
        if (!Network.WorldSession.Fits(p.Inventory.Bag, [(type, 1)])) return RcNoSpace;
        log.Chest = Claimed; // the claim first: a failure after it can never give a second chest
        p.World.Saves.MarkChanged(p.Character);
        p.Session.GiveItem(p, type, 1);
        p.Session.Send(new WeeklyPvpChallengeRewardStateUpdateEvent { d6s = Claimed });
        Log.Info("goals", $"'{p.Character.Name}' claimed the Conqueror's weekly chest {type.Name}");
        return RcOk;
    }

    /// <summary>
    /// GetPersonalMightStats (453 {0 the season's wire index}; MightGainInfoGui, c2y+0x38): 0 the season, 1 the personaltypes, 2 their might (a7m);
    /// the window matches key 0 with its season and looks each personal objective up in key 1 (MightGainInfoGui.OnPersonalStatsChanged 0x181144310).
    /// </summary>
    public static GetPersonalMightStatsResponse Stats(Player p, int season)
    {
        var stats = LogOf(p).Seasons.GetValueOrDefault(season);
        return new GetPersonalMightStatsResponse
        {
            a2y = season, a2z = stats is null ? [] : [.. stats.Keys], a20 = stats is null ? [] : [.. stats.Values],
        };
    }

    /// <summary>For .might: this week's might, the chest, the season's might by personaltype.</summary>
    public static string Describe(Player p)
    {
        var log = LogOf(p);
        var weekly = Rules(p.World).Weekly;
        var index = p.World.GuildGoals.Season.Index;
        var stats = index is { } i ? log.Seasons.GetValueOrDefault(i) : null;
        var chest = StateOf(p, log, weekly) switch { Locked => "locked", NotClaimable => "needs premium", Claimable => "claimable", _ => "claimed" };
        var season = stats is { Count: > 0 }
            ? "season " + string.Join(", ", stats.Select(s => string.Create(CultureInfo.InvariantCulture, $"{s.Key} {s.Value / 10000.0:0.##}")))
            : "no season might";
        return string.Create(CultureInfo.InvariantCulture,
            $"Conqueror's Challenge: {log.WeekMight / 10000.0:0.##}/{(weekly?.Might ?? 0) / 10000} might this week, chest {chest}; {season}");
    }
}
