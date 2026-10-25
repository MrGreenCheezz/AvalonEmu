using System.Globalization;
using AlbionLocal.Photon;
using AlbionLocal.Server.Content;
using AlbionLocal.Server.Data;
using AlbionLocal.Server.Entities;
using AlbionLocal.Server.Game;
using AlbionLocal.Server.Protocol;

namespace AlbionLocal.Server.Systems;

/// <summary>
/// The Adventurer's Challenge, the personal season tracker (analysis/work/stageH/journals/SPEC.md §C, §E; PVE-DESIGN.md step 11).
/// A fame gain (<see cref="Destiny.Award"/>) gives points = factor × (whole fame)^exponent of the UTC month's season (§C.2; premium factor 1,
/// rounded to 4 decimals), the factor by its source: a PvE kill pve (Season:Dungeon inside a random dungeon or an expedition), a harvest
/// gathering, a catch fishing; crafts, PvP and tomes none. A boss kill adds Season:BossBonus. Every gain goes through the a35 rules
/// (shared client code, §C.4) for each tracker, as the client runs them on its own copy from PersonalSeasonPointsGained (376 {0 gain a7m,
/// 1 GameTimeStamp}, sent before the UpdateFame): a tracker with a reset interval (the daily) that was finished (points ≥ cap) starts
/// again from 0 at its period start, an unfinished one carries over; points never exceed the cap, the highest psp of the triggers whose
/// unlockday has come. A trigger crossed unlocks its rewards (event 375 {0 season id, 1 ids}): UNLOCKED_CLAIMABLE when the character may
/// claim it (brj.c.h with the character's premium, <see cref="Eligible(Player, PersonalSeasonData.Reward)"/>), else UNLOCKED_NOT_CLAIMABLE, or (Season:GrantPremiumRewards) given
/// right away and CLAIMED. The window asks op 372 (<see cref="Response"/>) and claims with op 375 (<see cref="Claim"/>). Touched only
/// under <see cref="World.Sync"/>.
/// </summary>
public static class Seasons
{
    /// <summary>The reward states of op 372 key 9 (brh.s 0x18073a490), by their number in <see cref="SeasonLog.Rewards"/>.</summary>
    public static readonly string[] StateNames = ["LOCKED", "UNLOCKED_NOT_CLAIMABLE", "UNLOCKED_CLAIMABLE", "CLAIMED"];

    public const int Locked = 0, Ineligible = 1, Claimable = 2, Claimed = 3;

    /// <summary>ClaimPersonalSeasonReward return codes (PersonalSeasonGui.OnClaimResultMessage 0x180c7acd0).</summary>
    public const short RcAlreadyClaimed = 15001, RcNotEnoughPoints = 15002, RcNotClaimable = 15004, RcNoInventorySpace = 15005;

    /// <summary>The eligibility of brj.c.h without premium (isPremium false).</summary>
    public static bool Eligible(PersonalSeasonData.Reward r) => r.Eligible(isPremium: false, premiumDays: 0);

    /// <summary>
    /// The eligibility of brj.c.h for <paramref name="p"/> (stageJ focus-lp SPEC §5.5): isPremium = <see cref="Premium.Is(Player)"/>,
    /// premiumDays = <see cref="Premium.Days"/>. Under Premium:Mode Free the premium and boughtpremium rewards become claimable and the
    /// nonpremium-only ones (the 12 learningpointsreward entries among them) do not, as officially for a premium player.
    /// </summary>
    public static bool Eligible(Player p, PersonalSeasonData.Reward r) => Premium.Is(p) ? r.Eligible(isPremium: true, premiumDays: Premium.Days(p)) : Eligible(r);

    /// <summary>
    /// The state of <paramref name="r"/> in <paramref name="log"/> as of now: an unlocked, unclaimed one is judged again by
    /// <see cref="Eligible(Player, PersonalSeasonData.Reward)"/> (its stored state is that of its unlock, and the premium may have changed
    /// since: a save from before Premium:Mode Free, a GM's .premium); one the server owes (Season:GrantPremiumRewards) stays the server's.
    /// </summary>
    public static int StateOf(Player p, SeasonLog log, PersonalSeasonData.Reward r) =>
        log.Rewards.GetValueOrDefault(r.Id) is var s && (s is Ineligible or Claimable) && !log.Owed.Contains(r.Id)
            ? Eligible(p, r) ? Claimable : Ineligible : s;

    /// <summary>The points (a7m raw) of <paramref name="fame"/> (a7m raw) at <paramref name="factor"/> (§C.2): round(factor × fame^exponent, 4 decimals), half up.</summary>
    public static long Points(double factor, double exponent, long fame) =>
        fame <= 0 || factor <= 0 ? 0 : (long)Math.Round(factor * Math.Pow(fame / 10000.0, exponent) * 10000, MidpointRounding.AwayFromZero);

    /// <summary>The UTC moment of the world's clock (the GameTimeStamp of 376 key 1).</summary>
    public static DateTime UtcNow(World world) => new(world.GameNow, DateTimeKind.Utc);

    /// <summary>The month's season, null without personalseasons.xml (read with the journal) or with Season:Enabled off.</summary>
    public static PersonalSeasonData.Season? Current(World world, out DateTime start)
    {
        start = PersonalSeasonData.StartOf(UtcNow(world));
        return world.Goals.Season.Enabled ? world.Clusters?.SeasonsIfLoaded?.Of(UtcNow(world)) : null;
    }

    /// <summary>
    /// The points of a fame gain of <paramref name="p"/> (<paramref name="gain"/> = UpdateFame key 2, a7m raw), by its source (see the
    /// class); 0 = none. <paramref name="pvp"/>: a player kill gives none.
    /// </summary>
    public static long PointsOf(Player p, long gain, FameContext ctx, bool pvp, PersonalSeasonData.Season season)
    {
        var s = p.World.Goals.Season;
        double factor;
        switch (ctx.Source)
        {
            case FameSource.Kill when !pvp:
                var inside = p.Map is { } map && (DungeonLayout.IsInstance(map.Cluster) || ExpeditionData.IsInstance(map.Cluster) || CorruptedData.IsInstance(map.Cluster) || HellgateData.IsInstance(map.Cluster));
                factor = (inside ? s.Dungeon : null) ?? s.Pve ?? season.Pve;
                break;
            case FameSource.Harvest: factor = s.Gathering ?? season.Gathering; break;
            case FameSource.Fishing: factor = s.Fishing ?? season.Fishing; break;
            default: return 0;
        }
        return (long)Math.Round(Points(factor, season.Exponent, gain) * s.PointsFactor);
    }

    /// <summary>A fame gain's points for <paramref name="p"/> (from <see cref="Destiny.Award"/>, before its UpdateFame).</summary>
    public static void OnFame(Player p, long gain, FameContext ctx, bool pvp)
    {
        if (Current(p.World, out _) is not { } season) return;
        Gain(p, PointsOf(p, gain, ctx, pvp, season));
    }

    /// <summary>A boss kill's flat Season:BossBonus for <paramref name="p"/> (a world boss or a mob of category boss / miniboss).</summary>
    public static void OnBossKill(Player p, Mob mob)
    {
        if (mob.Boss is null && mob.Template.Category is not ("boss" or "miniboss")) return;
        var bonus = (long)Math.Round(p.World.Goals.Season.BossBonus * 10000 * p.World.Goals.Season.PointsFactor);
        if (bonus > 0) Gain(p, bonus);
    }

    /// <summary>
    /// <paramref name="raw"/> points (a7m) for <paramref name="p"/> now: 376 to the client, the same gain on every tracker of the season
    /// (a35), the rewards it unlocks (375, and the server's own grants), the same points for the Guild Challenge of its guild
    /// (<see cref="GuildGoals.OnChallenge"/>; the journal's reachseasonpoints, all in GUILD_GUILD_SEASON, follow the guild's season points
    /// there, stageI guild-goals SPEC §0.7). Returns the ids unlocked.
    /// </summary>
    public static List<string> Gain(Player p, long raw)
    {
        var world = p.World;
        if (raw <= 0 || Current(world, out var start) is not { } season) return [];
        var now = UtcNow(world);
        var id = $"{season.Name}@{now.Year}";
        var log = LogOf(p.Character, id);
        p.Session.Send(new PersonalSeasonPointsGainedEvent { dly = raw, dlz = world.GameNow });
        var unlocked = new List<PersonalSeasonData.Reward>();
        foreach (var t in season.Trackers) Add(log, season, start, t, raw, now, unlocked);
        log.Total += raw;
        world.Saves.MarkChanged(p.Character);
        var ids = Unlock(p, id, log, season, unlocked);
        world.GuildGoals.OnChallenge(p, raw);
        return ids;
    }

    /// <summary>The character's log of season <paramref name="id"/> (made when missing).</summary>
    public static SeasonLog LogOf(Character c, string id)
    {
        var goals = c.Goals ??= new GoalLog();
        if (!goals.Seasons.TryGetValue(id, out var log)) goals.Seasons[id] = log = new SeasonLog();
        return log;
    }

    /// <summary>
    /// a35.p (§C.4) for tracker <paramref name="t"/> at <paramref name="now"/>: a tracker with a reset interval whose last update is before
    /// its current period's start goes back to 0 only when it was finished (points ≥ cap), its rewards LOCKED again; an unfinished one keeps
    /// its points. The last update is now in both cases.
    /// </summary>
    public static void Refresh(SeasonLog log, PersonalSeasonData.Season season, DateTime start, PersonalSeasonData.Tracker t, DateTime now)
    {
        if (t.ResetDays < 1) return;
        var day = PersonalSeasonData.DayOf(start, now);
        if (day < 0) return;
        var periodStart = start.AddDays(day - day % t.ResetDays);
        if (new DateTime(log.Updated.GetValueOrDefault(t.Name), DateTimeKind.Utc) < periodStart)
        {
            var points = log.Points.GetValueOrDefault(t.Name);
            if (points > 0 && points >= t.Cap(day))
            {
                log.Points[t.Name] = 0;
                foreach (var r in t.Triggers.SelectMany(g => g.Rewards)) log.Rewards.Remove(r.Id);
            }
            log.Updated[t.Name] = now.Ticks;
        }
    }

    /// <summary>a35.g (§C.4): the refresh, then <paramref name="raw"/> up to the cap; the rewards of each trigger crossed into <paramref name="unlocked"/>.</summary>
    public static void Add(SeasonLog log, PersonalSeasonData.Season season, DateTime start, PersonalSeasonData.Tracker t, long raw, DateTime now,
        List<PersonalSeasonData.Reward> unlocked)
    {
        Refresh(log, season, start, t, now);
        var cap = t.Cap(PersonalSeasonData.DayOf(start, now));
        var old = log.Points.GetValueOrDefault(t.Name);
        log.Updated[t.Name] = now.Ticks;
        if (old >= cap) return;
        var points = Math.Min(old + raw, cap);
        log.Points[t.Name] = points;
        foreach (var trigger in t.Triggers)
            if (old < trigger.Psp && trigger.Psp <= points) unlocked.AddRange(trigger.Rewards);
    }

    /// <summary>
    /// The rewards just unlocked: their states, event 375 with their ids, and the server's own grants (Season:GrantPremiumRewards for the
    /// ones the client will not let it claim, Season:DailyExtra with the daily's).
    /// </summary>
    private static List<string> Unlock(Player p, string id, SeasonLog log, PersonalSeasonData.Season season, List<PersonalSeasonData.Reward> unlocked)
    {
        if (unlocked.Count == 0) return [];
        var settings = p.World.Goals.Season;
        foreach (var r in unlocked)
            if (!log.Rewards.ContainsKey(r.Id)) log.Rewards[r.Id] = Eligible(p, r) ? Claimable : Ineligible;
        p.Session.Send(new NewUnlockedPersonalSeasonRewardsEvent { dcr = id, dcs = [.. unlocked.Select(r => r.Id)] });
        Log.Info("season", $"'{p.Character.Name}' unlocked {string.Join(", ", unlocked.Select(r => r.Id))} of {id}");
        p.World.Vanity.SeasonRewards(p, unlocked.Count); // stageN vanity
        foreach (var r in unlocked)
            if (settings.GrantPremiumRewards && log.Rewards[r.Id] == Ineligible && r.BoughtPremium)
            {
                var given = Grant(p, log, season, r);
                var mailed = false;
                if (given is not null) log.Rewards[r.Id] = Claimed;
                // A full bag is no reason to lose it: the reward comes by mail (Mail:RewardOverflow) or waits (the client never lets the
                // character claim it itself).
                else if (Makeable(p.World, r))
                {
                    mailed = Mailer.Overflow(p.World, p.Character, [new InventoryItem(p.World.Items.Find(r.Item!)!.Name, (int)Math.Clamp(r.Amount, 1, short.MaxValue))],
                        $"the Adventurer's Challenge ({r.Id})");
                    if (mailed) log.Rewards[r.Id] = Claimed;
                    else log.Owed.Add(r.Id);
                }
                Expeditions.Tell(p, given is not null ? $"Adventurer's Challenge reward: {given}"
                    : mailed ? $"Adventurer's Challenge: the reward of {r.Id} does not fit the inventory and comes by mail"
                    : log.Owed.Contains(r.Id) ? $"Adventurer's Challenge: the reward of {r.Id} does not fit the inventory: free some room and type .bounty claim"
                    : $"Adventurer's Challenge: {r.Id} unlocked, but its reward cannot be given here");
            }
        if (settings.DailyExtra.Count > 0 && season.Find(unlocked[0].Id) is { Tracker.Type: "daily" })
        {
            var (given, waiting) = (new List<string>(), new List<string>());
            foreach (var name in settings.DailyExtra)
                if (p.World.Items.Find(name) is not { CanCreate: true } type) continue;
                else if (Network.WorldSession.Fits(p.Inventory.Bag, [(type, 1)]))
                {
                    p.Session.GiveItem(p, type, 1);
                    given.Add(type.Name);
                }
                else waiting.Add(type.Name);
            var mailed = Mailer.Overflow(p.World, p.Character, [.. waiting.Select(n => new InventoryItem(n))], "the daily challenge");
            if (!mailed) (p.Character.Goals ??= new GoalLog()).ItemsOwed.AddRange(waiting);
            if (given.Count > 0) Expeditions.Tell(p, $"Daily challenge complete, extra reward: {string.Join(", ", given)}");
            if (waiting.Count > 0)
                Expeditions.Tell(p, $"Daily challenge complete; {string.Join(", ", waiting)} does not fit the inventory" + (mailed ? " and comes by mail" : ": free some room and type .bounty claim"));
        }
        p.World.Saves.MarkChanged(p.Character);
        return [.. unlocked.Select(r => r.Id)];
    }

    /// <summary>Whether <paramref name="r"/> is an itemreward of an item the server can make (so a failed grant means a full inventory).</summary>
    private static bool Makeable(World world, PersonalSeasonData.Reward r) =>
        r.Kind == "itemreward" && r.Item is { } item && world.Items.Find(item) is { CanCreate: true };

    /// <summary>The rewards and daily items waiting for room in <paramref name="p"/>'s inventory (<see cref="ClaimOwed"/>).</summary>
    public static int OwedCount(Player p) => p.Character.Goals is { } g ? g.ItemsOwed.Count + g.Seasons.Values.Sum(l => l.Owed.Count) : 0;

    /// <summary>
    /// .bounty claim: the owed season rewards (CLAIMED once given, of any season the data knows; one it no longer knows is dropped) and
    /// the owed daily items, as far as they fit now. Returns how many were given.
    /// </summary>
    public static int ClaimOwed(Player p)
    {
        if (p.Character.Goals is not { } g) return 0;
        var given = 0;
        foreach (var (id, log) in g.Seasons)
            foreach (var rewardId in log.Owed.ToList())
            {
                if (p.World.Clusters?.SeasonsIfLoaded?.Parse(id) is not ({ } season, _) || season.Find(rewardId) is not { } found || !Makeable(p.World, found.Reward))
                {
                    log.Owed.Remove(rewardId);
                    continue;
                }
                if (Grant(p, log, season, found.Reward) is not { } what) continue;
                log.Owed.Remove(rewardId);
                log.Rewards[rewardId] = Claimed;
                Expeditions.Tell(p, $"Adventurer's Challenge reward: {what}");
                given++;
            }
        foreach (var name in g.ItemsOwed.ToList())
        {
            if (p.World.Items.Find(name) is not { CanCreate: true } type) g.ItemsOwed.Remove(name);
            else if (Network.WorldSession.Fits(p.Inventory.Bag, [(type, 1)]))
            {
                p.Session.GiveItem(p, type, 1);
                g.ItemsOwed.Remove(name);
                given++;
            }
        }
        p.World.Saves.MarkChanged(p.Character);
        return given;
    }

    /// <summary>
    /// A reward's grant: an itemreward's items into the inventory (null when they cannot be made or do not fit), a pspreward's points to
    /// the main and secondary trackers (§C.4, through a35: clamped, may unlock their triggers; no 376, the window asks 372 again), a
    /// learningpointsreward its learning points (<see cref="LearningPoints.Grant"/>, kind 1; nonpremium-only, so only under Premium:Mode None).
    /// Returns what was given, for a chat line.
    /// </summary>
    private static string? Grant(Player p, SeasonLog log, PersonalSeasonData.Season season, PersonalSeasonData.Reward r)
    {
        switch (r.Kind)
        {
            case "itemreward":
                if (r.Item is null || p.World.Items.Find(r.Item) is not { CanCreate: true } type) return null;
                var count = (int)Math.Clamp(r.Amount, 1, short.MaxValue);
                if (!Network.WorldSession.Fits(p.Inventory.Bag, [(type, count)])) return null;
                p.Session.GiveItem(p, type, count);
                return count == 1 ? type.Name : $"{type.Name} x{count}";
            case "pspreward":
                var (world, now) = (p.World, UtcNow(p.World));
                var start = PersonalSeasonData.StartOf(now);
                var unlocked = new List<PersonalSeasonData.Reward>();
                foreach (var t in season.Trackers.Where(t => t.Type is "main" or "secondary"))
                    Add(log, season, start, t, r.Amount * 10000, now, unlocked);
                Unlock(p, $"{season.Name}@{now.Year}", log, season, unlocked);
                return string.Create(CultureInfo.InvariantCulture, $"{r.Amount} season points");
            case "learningpointsreward":
                LearningPoints.Grant(p, r.Amount * 10000);
                return string.Create(CultureInfo.InvariantCulture, $"{r.Amount} learning points");
            default:
                return $"nothing ({r.Kind} is not made here)";
        }
    }

    /// <summary>
    /// GetPersonalSeasonTrackerData (op 372, §C.3) for <paramref name="id"/>: 0 the id, 1 now, 2/3 the trackers in file order with their
    /// points (refreshed by a35 first for the current season), 4-7 empty, 8/9 every reward that is not LOCKED with its state. An unknown
    /// id, no season data or Season:Enabled off: the empty answer with the echo.
    /// </summary>
    public static GetPersonalSeasonTrackerDataResponse Response(Player p, string? id)
    {
        var world = p.World;
        var answer = new GetPersonalSeasonTrackerDataResponse { a21 = id ?? "", a22 = world.GameNow };
        if (!world.Goals.Season.Enabled || world.Clusters?.SeasonsIfLoaded?.Parse(id) is not ({ } season, var start)) return answer.FillEmpty();
        var log = p.Character.Goals?.Seasons.GetValueOrDefault(id!);
        var now = UtcNow(world);
        if (log is not null && start == PersonalSeasonData.StartOf(now))
            foreach (var t in season.Trackers) Refresh(log, season, start, t, now);
        answer.a23 = [.. season.Trackers.Select(t => t.Name)];
        answer.a24 = [.. season.Trackers.Select(t => log?.Points.GetValueOrDefault(t.Name) ?? 0)];
        var states = season.Rewards.Where(r => log?.Rewards.GetValueOrDefault(r.Id) > Locked).ToList();
        answer.a29 = [.. states.Select(r => r.Id)];
        answer.a3a = [.. states.Select(r => StateNames[StateOf(p, log!, r)])];
        return answer.FillEmpty();
    }

    /// <summary>
    /// ClaimPersonalSeasonReward (op 375, §C.4): the return code. Another season than the month's, an unknown or ineligible reward 15004,
    /// claimed 15001, not unlocked 15002, items that do not fit 15005; else granted (<see cref="Grant"/>) and CLAIMED, 0.
    /// </summary>
    public static short Claim(Player p, string? seasonId, string? rewardId)
    {
        var world = p.World;
        if (Current(world, out _) is not { } season || seasonId != $"{season.Name}@{UtcNow(world).Year}" || rewardId is null
            || season.Find(rewardId) is not { } found) return RcNotClaimable;
        var log = LogOf(p.Character, seasonId);
        foreach (var t in season.Trackers) Refresh(log, season, PersonalSeasonData.StartOf(UtcNow(world)), t, UtcNow(world));
        switch (StateOf(p, log, found.Reward))
        {
            case Claimed: return RcAlreadyClaimed;
            case Locked: return RcNotEnoughPoints;
            case Ineligible: return RcNotClaimable;
        }
        if (Grant(p, log, season, found.Reward) is not { } given) return Makeable(world, found.Reward) ? RcNoInventorySpace : RcNotClaimable;
        log.Rewards[rewardId] = Claimed;
        world.Saves.MarkChanged(p.Character);
        Log.Info("season", $"'{p.Character.Name}' claimed {rewardId} of {seasonId}: {given}");
        return 0;
    }

    /// <summary>A short description of <paramref name="p"/>'s season for .bounty / .season: points per tracker and cap.</summary>
    public static string Describe(Player p)
    {
        if (Current(p.World, out var start) is not { } season) return "Adventurer's Challenge: off (or no personalseasons.bin)";
        var now = UtcNow(p.World);
        var log = p.Character.Goals?.Seasons.GetValueOrDefault($"{season.Name}@{now.Year}");
        var day = PersonalSeasonData.DayOf(start, now);
        var owed = OwedCount(p);
        return $"Adventurer's Challenge {season.Name}@{now.Year} (day {day + 1}): " + string.Join(", ", season.Trackers.Select(t =>
            string.Create(CultureInfo.InvariantCulture, $"{t.Type} {(log?.Points.GetValueOrDefault(t.Name) ?? 0) / 10000.0:0.##}/{t.Cap(day) / 10000.0:0.##}")))
            + (owed > 0 ? $"; {owed} reward(s) waiting for room: .bounty claim" : "");
    }
}
