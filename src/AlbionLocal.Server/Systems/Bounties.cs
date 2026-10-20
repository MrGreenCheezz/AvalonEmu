using System.Globalization;
using AlbionLocal.Photon;
using AlbionLocal.Server.Content;
using AlbionLocal.Server.Data;
using AlbionLocal.Server.Entities;
using AlbionLocal.Server.Game;
using AlbionLocal.Server.Protocol;

namespace AlbionLocal.Server.Systems;

/// <summary>
/// The server bounties (PVE-DESIGN.md §2.I, step 11): every day at Bounties:ResetUtc Bounties:Daily goals are picked from the pool's
/// daily entries and every Monday Bounties:Weekly from its weekly ones, the same for every player (the pick is seeded by the period's
/// date, .bounty reroll draws again; Bounties:PvpDaily / PvpWeekly more slots for the PvP entries). Kills, boss kills, dungeon bosses,
/// expeditions, harvests, catches and (stage I) credited PvP kills and duel wins count (<see cref="Bounty.Kind"/>);
/// progress goes out as system chat lines at each quarter, the completion with its reward (silver, fame, items straight into the
/// inventory; what does not fit waits for .bounty claim). The client has no window for this, only the chat (SystemMessage, as the GM
/// replies and the boss announcements). A character's counts are kept by "period/id" (<see cref="GoalLog.Bounties"/>), old periods are
/// dropped as it counts. Touched only under <see cref="World.Sync"/>.
/// </summary>
public sealed class Bounties(World world)
{
    /// <summary>One active bounty: the pool entry and its key ("d20261017/ID" or "w20261012/ID").</summary>
    public sealed record Active(Bounty Bounty, string Key);

    private BountyGoals Settings => world.Goals.Bounties;

    /// <summary>
    /// Draws after the period's own one (.bounty reroll), per period start. Kept in memory only: after a restart the day is back at its
    /// own pick (a reroll lasts until the restart); the completions of the period still count against its cap (<see cref="Count"/>).
    /// </summary>
    private readonly Dictionary<string, int> _rerolls = new(StringComparer.Ordinal);

    /// <summary>The daily period announced last (<see cref="Update"/>); null = none yet (the start announces nothing).</summary>
    private string? _announced;

    public DateTime UtcNow => new(world.GameNow, DateTimeKind.Utc);

    /// <summary>The start of the daily period <paramref name="utc"/> is in (the last ResetUtc at or before it).</summary>
    public DateTime DayStart(DateTime utc)
    {
        var today = utc.Date + Settings.Reset;
        return utc >= today ? today : today.AddDays(-1);
    }

    /// <summary>The start of the weekly period: the daily start of the last Monday at or before it.</summary>
    public DateTime WeekStart(DateTime utc)
    {
        var day = DayStart(utc);
        return day.AddDays(-(((int)day.DayOfWeek + 6) % 7));
    }

    /// <summary>The bounties of the periods of <paramref name="utc"/>: the daily ones, then the weekly ones.</summary>
    public IReadOnlyList<Active> ActiveAt(DateTime utc)
    {
        if (!Settings.Enabled) return [];
        return [.. Pick("d", DayStart(utc), false), .. Pick("w", WeekStart(utc), true)];
    }

    /// <summary>Stage I: the extra slots of the PvP entries in a daily or weekly period (none without Pvp:Enabled).</summary>
    private int PvpSlots(bool weekly) => !world.Goals.Pvp.Enabled ? 0 : weekly ? Settings.PvpWeekly : Settings.PvpDaily;

    /// <summary>How many bounties a period has, and so gives at most: Daily (Weekly) and its PvP slots.</summary>
    private int Slots(bool weekly) => (weekly ? Settings.Weekly : Settings.Daily) + PvpSlots(weekly);

    public IReadOnlyList<Active> Current => ActiveAt(UtcNow);

    private IEnumerable<Active> Pick(string prefix, DateTime start, bool weekly)
    {
        var count = weekly ? Settings.Weekly : Settings.Daily;
        var period = prefix + start.ToString("yyyyMMdd", CultureInfo.InvariantCulture);
        // Stage I: without Pvp:Enabled no credit ever comes, so the PvP entries are left out of the draw.
        var pool = Settings.Pool.Where(b => b.Weekly == weekly && (world.Goals.Pvp.Enabled || !b.IsPvp)).ToList();
        // A seed of the date (and the rerolls), the same on every start of the server: the same bounties for every player.
        var rng = new Random(start.Year * 10000 + start.Month * 100 + start.Day + 7919 * _rerolls.GetValueOrDefault(period) + (weekly ? 1_000_003 : 0));
        for (var i = pool.Count - 1; i > 0; i--)
        {
            var j = rng.Next(i + 1);
            (pool[i], pool[j]) = (pool[j], pool[i]);
        }
        // Stage I: with PvP slots the period's own slots go to the first PvE entries of the shuffle and the PvP slots to the first PvP
        // ones; with none (PvpDaily / PvpWeekly 0) the mixed draw of before.
        var pvp = PvpSlots(weekly);
        IEnumerable<Bounty> picked = pool.Take(count);
        if (pvp > 0)
        {
            var chosen = pool.Where(b => !b.IsPvp).Take(count).Concat(pool.Where(b => b.IsPvp).Take(pvp)).ToHashSet();
            picked = pool.Where(chosen.Contains);
        }
        return picked.Select(b => new Active(b, $"{period}/{b.Id}"));
    }

    /// <summary>
    /// .bounty reroll: other daily bounties for today, until the next reset or a restart. The counts of the old ones are dropped at the next
    /// count (<see cref="Prune"/>); their completions stay and count against the day's cap of Bounties:Daily.
    /// </summary>
    public void Reroll()
    {
        var period = "d" + DayStart(UtcNow).ToString("yyyyMMdd", CultureInfo.InvariantCulture);
        _rerolls[period] = _rerolls.GetValueOrDefault(period) + 1;
        Announce();
    }

    /// <summary>Every tick: a new daily period is announced to every player in game (Bounties:AnnounceReset).</summary>
    public void Update()
    {
        if (!Settings.Enabled || Settings.Pool.Count == 0) return;
        var period = DayStart(UtcNow).ToString("yyyyMMdd", CultureInfo.InvariantCulture);
        if (_announced == period) return;
        var first = _announced is null;
        _announced = period;
        if (!first) Announce();
    }

    private void Announce()
    {
        if (!Settings.AnnounceReset) return;
        var text = "New bounties: " + string.Join("; ", Current.Select(a => (a.Bounty.Weekly ? "[weekly] " : "") + a.Bounty.Describe())) + " (.bounty)";
        Log.Info("world", $"announce: {text}");
        foreach (var p in world.Chat.Announce(text, world.Maps.Maps.SelectMany(m => m.Players.Values))) Expeditions.Tell(p, text); // stageJ: channel 7 to its members
    }

    // ---- events

    /// <summary>A kill: kill / boss / dungeon bounties for the killer and its fame group.</summary>
    public void Killed(Map map, Player killer, Mob mob, IReadOnlyList<Player> group)
    {
        if (!Settings.Enabled) return;
        var t = mob.Template;
        var boss = t.Category is "boss" or "miniboss";
        foreach (var p in group.Contains(killer) ? group : [killer, .. group])
            Count(p, b => b.Kind switch
            {
                "kill" => t.Tier >= b.MinTier && t.Tier <= b.MaxTier && (b.Mob is null || t.Name.Contains(b.Mob, StringComparison.OrdinalIgnoreCase))
                          && (b.Category is null || b.Category.Equals(t.Category, StringComparison.OrdinalIgnoreCase))
                          && (b.Cluster is null || b.Cluster.Equals(map.Cluster, StringComparison.OrdinalIgnoreCase)),
                "boss" => mob.Boss is not null && (b.Mob is null || t.Name.Contains(b.Mob, StringComparison.OrdinalIgnoreCase)),
                "dungeon" => boss && DungeonLayout.IsInstance(map.Cluster) && t.Tier >= b.MinTier,
                _ => false,
            }, 1);
    }

    /// <summary>A harvest of <paramref name="count"/> × <paramref name="item"/>.</summary>
    public void Gathered(Player p, ItemTemplate item, int count)
    {
        if (Settings.Enabled) Count(p, b => b.Kind == "gather" && item.Tier >= b.MinTier && item.Tier <= b.MaxTier && (b.Mob is null || item.Name.Contains(b.Mob, StringComparison.OrdinalIgnoreCase)), count);
    }

    /// <summary>A catch.</summary>
    public void Fished(Player p, ItemTemplate fish)
    {
        if (Settings.Enabled) Count(p, b => b.Kind == "fish" && fish.Tier >= b.MinTier, 1);
    }

    /// <summary>An expedition completed by <paramref name="p"/> (of a tier from MinTier).</summary>
    public void ExpeditionFinished(Player p, int tier)
    {
        world.Plugins.GoalCompleted(p, "expedition", $"a T{tier} expedition"); // the plugins' GoalCompleted
        if (Settings.Enabled) Count(p, b => b.Kind == "expedition" && tier >= b.MinTier, 1);
    }

    /// <summary>StageL: a corrupted dungeon completed by <paramref name="p"/> (its rank's tier 5 / 6 / 8 from MinTier).</summary>
    public void CorruptedCompleted(Player p, int tier)
    {
        world.Plugins.GoalCompleted(p, "corrupted", $"a T{tier} corrupted dungeon"); // the plugins' GoalCompleted
        if (Settings.Enabled) Count(p, b => b.Kind == "corrupted" && tier >= b.MinTier, 1);
    }

    /// <summary>StageL roads phase B: an Avalonian road chest opened by <paramref name="p"/> on a road of tier <paramref name="tier"/> (4 / 6 / 8).</summary>
    public void RoadChestOpened(Player p, int tier)
    {
        if (Settings.Enabled) Count(p, b => b.Kind == "roadchest" && tier >= b.MinTier && tier <= b.MaxTier, 1);
    }

    /// <summary>StageL roads phase B: a road portal passed by <paramref name="p"/> into a cluster of tier <paramref name="tier"/>.</summary>
    public void PortalPassed(Player p, int tier)
    {
        if (Settings.Enabled) Count(p, b => b.Kind == "portal" && tier >= b.MinTier && tier <= b.MaxTier, 1);
    }

    /// <summary>
    /// Stage I: a credit of <see cref="PvpGoals.OnPvpKill"/> for <paramref name="p"/> (<paramref name="assist"/>: an assist's): pvpkill for
    /// a kill or knockdown in a zone of factor <paramref name="zone"/> above 0 (the victim's tier, Zone = <paramref name="warning"/>, Bots,
    /// Assists), duel for a duel won.
    /// </summary>
    public void PvpCredited(Player p, PvpKill k, bool assist, double zone, string warning)
    {
        if (!Settings.Enabled) return;
        var (tier, bot) = (k.Victim.Tier, k.Victim.IsBot);
        Count(p, b => b.Kind switch
        {
            "pvpkill" => k.Outcome != PvpOutcome.DuelWin && zone > 0 && (!assist || b.Assists) && tier >= b.MinTier && tier <= b.MaxTier
                         && (b.Zone is null || b.Zone.Equals(warning, StringComparison.OrdinalIgnoreCase))
                         && b.Bots switch { "none" => !bot, "only" => bot, _ => true },
            "duel" => k.Outcome == PvpOutcome.DuelWin,
            _ => false,
        }, 1);
    }

    /// <summary>
    /// The active bounties <paramref name="match"/> takes count <paramref name="n"/> for <paramref name="p"/>; lines at each quarter, the
    /// reward at the end. A period gives at most Bounties:Daily (Weekly) completions and its PvP slots, rerolls and restarts included, so
    /// a reroll that is lost with a restart does not open a second set of rewards.
    /// </summary>
    public void Count(Player p, Func<Bounty, bool> match, int n)
    {
        if (n <= 0) return;
        var active = Current;
        if (active.Count == 0) return;
        var g = p.Character.Goals ??= new GoalLog();
        Prune(g, active);
        foreach (var a in active)
        {
            if (g.BountiesDone.Contains(a.Key) || !match(a.Bounty)) continue;
            var b = a.Bounty;
            var period = a.Key[..(a.Key.IndexOf('/') + 1)];
            if (g.BountiesDone.Count(k => k.StartsWith(period, StringComparison.Ordinal)) >= Slots(b.Weekly)) continue;
            var old = g.Bounties.GetValueOrDefault(a.Key);
            var now = Math.Min(b.Count, old + n);
            g.Bounties[a.Key] = now;
            world.Saves.MarkChanged(p.Character);
            if (now >= b.Count)
            {
                g.BountiesDone.Add(a.Key);
                g.Bounties.Remove(a.Key);
                Complete(p, a);
            }
            else if (Settings.AnnounceProgress && now * 4 / b.Count > old * 4 / b.Count)
                Expeditions.Tell(p, $"Bounty: {b.Describe()} {now}/{b.Count}");
        }
    }

    /// <summary>The counts of bounties no longer active (periods that are over, rerolled ones), the completions of periods that are over.</summary>
    private static void Prune(GoalLog g, IReadOnlyList<Active> active)
    {
        var periods = active.Select(a => a.Key[..a.Key.IndexOf('/')]).ToHashSet(StringComparer.Ordinal);
        var keys = active.Select(a => a.Key).ToHashSet(StringComparer.Ordinal);
        bool Old(string key) => !periods.Contains(key[..Math.Max(0, key.IndexOf('/'))]);
        foreach (var k in g.Bounties.Keys.Where(k => !keys.Contains(k)).ToList()) g.Bounties.Remove(k);
        g.BountiesDone.RemoveWhere(k => Old(k) && !g.BountiesOwed.Contains(k));
    }

    /// <summary>A bounty done: its reward, a chat line; items that do not fit come by mail (Mail:RewardOverflow) or wait (.bounty claim).</summary>
    private void Complete(Player p, Active a)
    {
        var given = Reward(p, a.Bounty, out var fits);
        var mailed = !fits && Mailer.Overflow(world, p.Character, [.. Items(a.Bounty).Select(x => new InventoryItem(x.Type.Name, x.Count))], $"the bounty {a.Bounty.Describe()}");
        if (!fits && !mailed) p.Character.Goals!.BountiesOwed.Add(a.Key);
        Expeditions.Tell(p, $"Bounty complete: {a.Bounty.Describe()}. Reward: {(given.Count == 0 ? "none" : string.Join(", ", given))}"
            + (fits ? "" : mailed ? "; the items do not fit the inventory and come by mail" : "; the items do not fit the inventory: free some room and type .bounty claim"));
        Log.Info("world", $"'{p.Character.Name}' completed bounty {a.Key}");
        world.Plugins.GoalCompleted(p, "bounty", a.Bounty.Describe()); // the plugins' GoalCompleted
    }

    /// <summary>The reward of <paramref name="b"/>: silver and fame now, the items when all fit (<paramref name="fits"/> false: none given).</summary>
    private List<string> Reward(Player p, Bounty b, out bool fits, bool itemsOnly = false)
    {
        var given = new List<string>();
        if (!itemsOnly && b.Silver > 0)
        {
            Wallet.Credit(p, b.Silver * 10000, $"bounty {b.Id}");
            given.Add($"{b.Silver} silver");
        }
        if (!itemsOnly && b.Fame > 0)
        {
            Destiny.Award(p, b.Fame * 10000, Destiny.Kill(p, Math.Max(1, b.MinTier)), journals: false);
            given.Add($"{b.Fame} fame");
        }
        var items = Items(b);
        fits = Network.WorldSession.Fits(p.Inventory.Bag, items);
        if (fits)
            foreach (var (type, count) in items)
            {
                p.Session.GiveItem(p, type, count);
                given.Add(count == 1 ? type.Name : $"{type.Name} x{count}");
            }
        return given;
    }

    /// <summary>The reward items the server can make ("NAME" or "NAME:n"; others are logged once and left out).</summary>
    private List<(ItemTemplate Type, int Count)> Items(Bounty b)
    {
        var list = new List<(ItemTemplate, int)>();
        foreach (var entry in b.Items)
        {
            var parts = entry.Split(':');
            var count = parts.Length > 1 && int.TryParse(parts[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out var c) ? Math.Clamp(c, 1, 999) : 1;
            if (world.Items.Find(parts[0]) is { CanCreate: true } type) list.Add((type, count));
            else Combat.LogOnce("bountyitem:" + parts[0], $"bounty reward {parts[0]} is not an item this server can create: left out");
        }
        return list;
    }

    /// <summary>.bounty claim: the owed items of done bounties, those that fit now. Returns how many bounties' items were given.</summary>
    public int Claim(Player p)
    {
        if (p.Character.Goals is not { BountiesOwed.Count: > 0 } g) return 0;
        var given = 0;
        foreach (var key in g.BountiesOwed.ToList())
        {
            var id = key[(key.IndexOf('/') + 1)..];
            if (Settings.Pool.FirstOrDefault(b => b.Id == id) is not { } b)
            {
                g.BountiesOwed.Remove(key);
                continue;
            }
            Reward(p, b, out var fits, itemsOnly: true);
            if (!fits) continue;
            g.BountiesOwed.Remove(key);
            given++;
        }
        world.Saves.MarkChanged(p.Character);
        return given;
    }

    /// <summary>.bounty: the active bounties with <paramref name="p"/>'s progress, and the season line.</summary>
    public IEnumerable<string> Describe(Player p)
    {
        var g = p.Character.Goals;
        var now = UtcNow;
        var next = DayStart(now).AddDays(1);
        foreach (var a in Current)
        {
            var done = g?.BountiesDone.Contains(a.Key) == true;
            var count = done ? a.Bounty.Count : g?.Bounties.GetValueOrDefault(a.Key) ?? 0;
            yield return $"{(a.Bounty.Weekly ? "[weekly] " : "")}{a.Bounty.Describe()}: {count}/{a.Bounty.Count}{(done ? " (done)" : "")}";
        }
        yield return $"next daily bounties at {next:yyyy-MM-dd HH:mm} UTC";
    }
}
