using AlbionLocal.Photon;
using System.Globalization;
using AlbionLocal.Server.Data;
using AlbionLocal.Server.Entities;
using AlbionLocal.Server.Game;
using AlbionLocal.Server.Protocol;

namespace AlbionLocal.Server.Systems;

/// <summary>
/// The faction warfare campaign, the personal side (analysis/work/stageR/fw-campaign/SPEC.md, "SPEC" here): the faction points a character earns
/// (<see cref="Factions.Earn"/>, after the rank's multiplier) count for the UTC day, the campaign month and the week
/// (<see cref="FactionCampaignLog"/>). A day's 1250 / 2500 / 5000 points give the daily standing rewards at once; a month's 25000 … 100000 points
/// unlock the campaign chests (from their unlockday on), which the window claims (op 437, <see cref="Claim"/>). Both go out as
/// FactionWarfareCampaignRewardsUnlocked (484). The window reads GetFactionWarfareCampaignData (436, <see cref="Response"/>); the weekly report
/// (434) shows the last week's points. Touched only under <see cref="World.Sync"/>.
/// </summary>
public static class FactionCampaign
{
    /// <summary>bcg.f.a, the tracks of key 0: 0 the campaign (month), 1 the daily.</summary>
    public const int CampaignTrack = 0, DailyTrack = 1;

    /// <summary>bcg.d, the reward states of key 3 (as the personal season's): 0 locked, 1 unlocked (not claimable), 2 claimable, 3 claimed.</summary>
    public const int Locked = 0, Unlocked = 1, Claimable = 2, Claimed = 3;

    /// <summary>
    /// ClaimFactionWarfareItemReward's return codes the client knows (FactionWarfareRewardWeeklyItemReward.OnClaimRewardResponse 0x181174ef0):
    /// 17480 "@CONTAINER_INVENTORY_ERROR_WEIGHT…" (drs.d 6), 17481 the inventory's not-enough-space line (drs.d 10); any other → "@GENERIC_ERROR_INTERNAL_ERROR".
    /// </summary>
    public const short RcOk = 0, RcGeneric = 1, RcTooHeavy = 17480, RcNoSpace = 17481;

    /// <summary>The UTC day index of <paramref name="utc"/> (days since 0001-01-01).</summary>
    public static int Day(DateTime utc) => (int)(utc.Date.Ticks / TimeSpan.TicksPerDay);

    /// <summary>The client's week of <paramref name="utc"/> (dex.z 0x180aeb0e0): the Monday of its week, in ticks / 7 days.</summary>
    public static int Week(DateTime utc) => (int)((utc.Date - TimeSpan.FromDays(((int)utc.DayOfWeek + 6) % 7)).Ticks / TimeSpan.FromDays(7).Ticks);

    /// <summary>The campaign day of <paramref name="utc"/> the unlockdays are compared with (bcg.f.e 0x1805c1ea0: unlockday ≤ Day − 1; campaignstarttime 0UTC).</summary>
    public static int CampaignDay(DateTime utc) => utc.Day - 1;

    /// <summary><paramref name="p"/>'s campaign log with the day, month and week rolled to now.</summary>
    public static FactionCampaignLog LogOf(Player p)
    {
        var goals = p.Character.Goals ??= new GoalLog();
        var log = goals.FactionCampaign ??= new FactionCampaignLog();
        var now = Seasons.UtcNow(p.World);
        var (day, month, week) = (Day(now), Network.WorldSession.CampaignMonth(now), Week(now));
        if (log.Day != day) (log.Day, log.DayPoints, log.DayRewards) = (day, 0, new(StringComparer.Ordinal));
        if (log.Month != month) (log.Month, log.MonthPoints, log.Rewards) = (month, 0, new(StringComparer.Ordinal));
        if (log.Week != week)
        {
            (log.LastWeekPoints, log.LastWeekFaction) = log.Week == week - 1 ? (log.WeekPoints, log.WeekFaction) : (0, (byte)0);
            (log.Week, log.WeekPoints, log.WeekFaction) = (week, 0, 0);
        }
        return log;
    }

    /// <summary>
    /// <paramref name="points"/> (a7m raw) went to <paramref name="p"/>'s enlisted faction: they count for the day, month and week; a daily
    /// trigger crossed gives its standingreward to the enlisted faction (UpdateFactionStanding 86), a campaign trigger reached (points and
    /// unlockday) becomes UNLOCKED; the new ones go out as 484 {0 the reward ids} [infer: 484's handler c3d.c4 looks each id up in both tracks].
    /// </summary>
    public static void OnPoints(Player p, long points)
    {
        if (points <= 0 || p.World.Factions.Data is not { } data) return;
        var log = LogOf(p);
        log.DayPoints += points;
        log.MonthPoints += points;
        log.WeekPoints += points;
        log.WeekFaction = p.Faction;
        p.World.Factions.Tally(p.Character.Id, p.Faction, log.Month, log.MonthPoints);
        var unlocked = new List<string>();
        foreach (var t in data.Campaign.Daily)
        {
            if (log.DayPoints < t.PointRequirement * 10000 || !log.DayRewards.Add(t.Reward.Id)) continue;
            unlocked.Add(t.Reward.Id);
            if (t.Reward.Kind == "standingreward" && t.Reward.Amount > 0) p.World.Factions.GrantStanding(p, p.Faction, t.Reward.Amount * 10000);
            Log.Info("faction", string.Create(CultureInfo.InvariantCulture, $"'{p.Character.Name}' daily campaign reward {t.Reward.Id} ({t.Reward.Amount} standing)"));
        }
        unlocked.AddRange(Unlock(p, log, data));
        p.World.Saves.MarkChanged(p.Character);
        if (unlocked.Count > 0) p.Session.Send(new FactionWarfareCampaignRewardsUnlockedEvent { b68 = [.. unlocked] });
        // StageR gold (not official, Gold:Rewards): the day's last daily reward reached gives a little gold, once a campaign day.
        if (data.Campaign.Daily.Count > 0 && data.Campaign.Daily.All(t => log.DayRewards.Contains(t.Reward.Id))) GoldRewards.FactionDaily(p, log.Day);
    }

    /// <summary>The campaign rewards reached since the last look (points and unlockday), now UNLOCKED; their ids.</summary>
    private static List<string> Unlock(Player p, FactionCampaignLog log, FactionWarfareData data)
    {
        var day = CampaignDay(Seasons.UtcNow(p.World));
        var ids = new List<string>();
        foreach (var t in data.Campaign.Campaign)
        {
            if (log.Rewards.ContainsKey(t.Reward.Id) || log.MonthPoints < t.PointRequirement * 10000 || t.UnlockDay > day) continue;
            log.Rewards[t.Reward.Id] = Unlocked;
            ids.Add(t.Reward.Id);
            Log.Info("faction", $"'{p.Character.Name}' unlocked campaign reward {t.Reward.Id}");
        }
        return ids;
    }

    /// <summary>
    /// Whether <paramref name="p"/> may claim <paramref name="r"/>: bcg.e.m with its premium (the chests are boughtpremium only: premium of more
    /// than 6 days, <see cref="Premium.Days"/>), or FactionWarfare:CampaignRewardsWithoutPremium.
    /// </summary>
    public static bool Eligible(Player p, FactionCampaignData.Reward r) =>
        p.World.Config.FactionWarfare.CampaignRewardsWithoutPremium || r.Eligible(Premium.Is(p), Premium.Is(p) ? Premium.Days(p) : 0);

    /// <summary>The state of the campaign reward of <paramref name="t"/> for <paramref name="p"/> now (SPEC §1.3).</summary>
    public static int StateOf(Player p, FactionCampaignLog log, FactionCampaignData.Trigger t) =>
        log.Rewards.GetValueOrDefault(t.Reward.Id) switch
        {
            Claimed => Claimed,
            Unlocked => Eligible(p, t.Reward) ? Claimable : Unlocked,
            _ => Locked,
        };

    /// <summary>
    /// GetFactionWarfareCampaignData (436; dex.z 0x180aeb3b0 → dex.a): key 0/1 the tracks and their points (official order [1 daily, 0 campaign]),
    /// 2/3 the campaign rewards that are not LOCKED and their states, 4 the campaign month, 5 the points of each faction this month (index
    /// faction − 1, the characters this server knows), 6/7/8 per faction the warfare clusters it controls by colour (GrowingFactionFlag: blue,
    /// yellow, red [infer: the captured sums 42 / 31 of keys 7 / 8 are the yellow / red duchy clusters]).
    /// </summary>
    public static GetFactionWarfareCampaignDataResponse Response(Player p)
    {
        var world = p.World;
        var log = LogOf(p);
        var r = new GetFactionWarfareCampaignDataResponse
        {
            awa = [DailyTrack, CampaignTrack], awb = [log.DayPoints, log.MonthPoints], awe = log.Month,
            awf = world.Factions.TallyOf(log.Month), awg = new int[6], awh = new int[6], awi = new int[6],
        };
        var ids = new List<string>();
        var states = new List<int>();
        if (world.Factions.Data is { } data)
        {
            Unlock(p, log, data);
            foreach (var t in data.Campaign.Campaign)
                if (StateOf(p, log, t) is var s and not Locked)
                {
                    ids.Add(t.Reward.Id);
                    states.Add(s);
                }
            if (world.Clusters?.World is { } worldInfo)
                foreach (var cluster in worldInfo.Clusters)
                {
                    if (!data.ClusterFaction.ContainsKey(cluster.Id)) continue;
                    var owner = world.Outposts.ControllerOf(cluster.Id);
                    if (owner is < 1 or > 6) continue;
                    var counts = cluster.Type switch
                    {
                        var t when t.Contains("YELLOW", StringComparison.Ordinal) => r.awh,
                        var t when t.Contains("RED", StringComparison.Ordinal) || t.Contains("BLACK", StringComparison.Ordinal) => r.awi,
                        _ => r.awg,
                    };
                    counts[owner - 1]++;
                }
        }
        r.awc = [.. ids];
        r.awd = [.. states];
        return r;
    }

    /// <summary>
    /// ClaimFactionWarfareItemReward (437 {0 the reward id}; FactionWarfareRewardWeeklyItemReward.OnClickClaim 0x181175320, only on a CLAIMABLE
    /// reward after its own inventory check): the reward's item of the enlisted faction into the inventory, CLAIMED. Returns the return code.
    /// </summary>
    public static short Claim(Player p, string? id)
    {
        if (p.World.Factions.Data is not { } data || data.Campaign.FindCampaign(id) is not { } t) return RcGeneric;
        var log = LogOf(p);
        Unlock(p, log, data);
        if (StateOf(p, log, t) != Claimable) return RcGeneric;
        if (t.Reward.ItemFor(p.Faction) is not { } name || p.World.Items.Find(name) is not { CanCreate: true } type)
        {
            Log.Warn("faction", $"campaign reward {t.Reward.Id}: no item for {p.World.Factions.Name(p.Faction)}");
            return RcGeneric;
        }
        if (!Network.WorldSession.Fits(p.Inventory.Bag, [(type, 1)])) return RcNoSpace;
        p.Session.GiveItem(p, type, 1);
        log.Rewards[t.Reward.Id] = Claimed;
        p.World.Saves.MarkChanged(p.Character);
        Log.Info("faction", $"'{p.Character.Name}' claimed campaign reward {t.Reward.Id}: {type.Name}");
        return RcOk;
    }

    /// <summary>
    /// GetFactionWarfareWeeklyReport (434; dex.ab 0x180ae91a0 → dex.b): key 0 this week (the client keeps the report while key 0 is its week
    /// and key 1 its faction, and shows the week before: AddDays(key0 × 7 − 7)), key 1 the enlisted faction, key 10 the faction points of the
    /// last week [infer: the label 0x80 shows key 10 with the faction's currency]. The other counters and the claimable bonus (key 14, which
    /// enables the claim button) stay 0: their rules are not known (SPEC §4).
    /// </summary>
    public static GetFactionWarfareWeeklyReportResponse WeeklyReport(Player p)
    {
        var log = LogOf(p);
        return new GetFactionWarfareWeeklyReportResponse
        {
            awj = log.Week, awk = p.Faction,
            awt = log.LastWeekFaction == p.Faction ? log.LastWeekPoints : 0,
        };
    }
}
