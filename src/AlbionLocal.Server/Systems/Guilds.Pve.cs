using System.Globalization;
using AlbionLocal.Photon;
using AlbionLocal.Server.Data;
using AlbionLocal.Server.Entities;
using AlbionLocal.Server.Game;
using AlbionLocal.Server.Protocol;

namespace AlbionLocal.Server.Systems;

/// <summary>
/// The PvE guild (analysis/work/stageH/guilds/SPEC.md §10, PVE-DESIGN step 13 part 2), every part behind its Guilds: key (off in the
/// official preset): the guild experience from the members' PvE fame and their clears, its levels (Guilds:Levels) and their perks
/// (fame, mob silver, gathering yield, repair; Rates-like factors in the existing code paths), shared hunting without a party, the
/// guild account filled by Guilds:SilverTax (GuildUpdate kind 2) and paid out by .guild payout, and the contracts (§10.6). The client
/// has no guild level, experience or contract field outside GvG, so these are told as system lines ([Guild] …) and by .guild info /
/// .guild contracts. The account, the experience and the contracts are saved every 30 s and at the end (<see cref="Flush"/>), not at
/// every kill.
/// </summary>
public sealed partial class Guilds
{
    private readonly HashSet<Guild> _progressChanged = [];
    private readonly HashSet<Guild> _accountChanged = [];
    private readonly HashSet<(Guid Guild, object Instance)> _clearsCounted = [];
    private TimeSpan _nextAccount, _nextFlush;

    /// <summary>The contract kinds with their weekly base counts (a daily period: a fifth, at least 1) [choice].</summary>
    private static readonly (string Kind, int Weekly)[] ContractKinds = [("kill", 500), ("dungeon", 5), ("gather", 2000), ("expedition", 3)];

    // ---- levels and perks (§10.3) ----

    /// <summary>The level of <paramref name="g"/>: how many Guilds:Levels thresholds its experience has reached (1 at 0).</summary>
    public int Level(Guild g) => Math.Max(1, Config.GuildLevels.Count(t => g.Xp >= t));

    /// <summary>The experience the next level starts at; null at the top level.</summary>
    public long? NextLevelXp(Guild g) => Level(g) < Config.GuildLevels.Count ? Config.GuildLevels[Level(g)] : null;

    /// <summary>The levels of <paramref name="p"/>'s guild above 1 (the perk steps); 0 without a guild.</summary>
    private int Steps(Player p) => Of(p.Character.Id) is { } g ? Level(g) - 1 : 0;

    /// <summary>The PvE fame bonus of <paramref name="p"/> (Guilds:FamePerLevel per step), with the guild blessing's (<see cref="GuildGoals.Blessing"/>, stage I).</summary>
    public double FameBonus(Player p) => Steps(p) * Config.GuildFamePerLevel + world.GuildGoals.Blessing(p).Fame;

    /// <summary>The bonus on the silver piles of <paramref name="p"/>'s mob kills (Guilds:SilverPerLevel per step), with the blessing's.</summary>
    public double SilverBonus(Player p) => Steps(p) * Config.GuildSilverPerLevel + world.GuildGoals.Blessing(p).Silver;

    /// <summary>The gathering and fishing yield bonus of <paramref name="p"/> (Guilds:GatheringPerLevel per step), added to the gear's, with the blessing's.</summary>
    public double GatheringBonus(Player p) => Steps(p) * Config.GuildGatheringPerLevel + world.GuildGoals.Blessing(p).Yield;

    /// <summary>What a repair the client priced at <paramref name="price"/> (a7m) costs <paramref name="p"/>: less Guilds:RepairPerLevel per step (at most 90 %).</summary>
    public long RepairPrice(Player p, long price) =>
        price - (long)Math.Floor(price * (decimal)Math.Min(0.9, Steps(p) * Config.GuildRepairPerLevel));

    /// <summary>"+4 % fame, +4 % mob silver, +2 % gathering, −10 % repair" for <paramref name="level"/>.</summary>
    public string Perks(int level)
    {
        var s = level - 1;
        static string P(double v) => (v * 100).ToString("0.#", CultureInfo.InvariantCulture);
        return s <= 0 ? "no perks yet"
            : $"+{P(s * Config.GuildFamePerLevel)} % fame, +{P(s * Config.GuildSilverPerLevel)} % mob silver, +{P(s * Config.GuildGatheringPerLevel)} % gathering, "
              + $"-{P(Math.Min(0.9, s * Config.GuildRepairPerLevel))} % repair";
    }

    /// <summary>
    /// <paramref name="xp"/> more experience for <paramref name="g"/> (whole points, the fraction carried); a level reached is told to
    /// every member in game as a system line with its perks.
    /// </summary>
    public void AddXp(Guild g, double xp)
    {
        if (xp <= 0 || !_byId.ContainsKey(g.Id)) return;
        var before = Level(g);
        var total = g.XpCarry + xp;
        var whole = (long)Math.Floor(total);
        g.XpCarry = total - whole;
        if (whole == 0) return;
        g.Xp += whole;
        _progressChanged.Add(g);
        if (Level(g) is var after && after > before)
        {
            Tell(g, $"[Guild] {g.Name} reached guild level {after}: {Perks(after)}");
            Log.Info("guild", $"guild '{g.Name}' reached level {after} ({g.Xp} xp)");
        }
    }

    /// <summary>A PvE fame gain of <paramref name="gain"/> (a7m, the perk included) by <paramref name="p"/>: Guilds:XpShare of its points to its guild.</summary>
    public void OnFame(Player p, long gain)
    {
        if (Config.GuildXpShare > 0 && gain > 0 && Of(p.Character.Id) is { } g) AddXp(g, gain / 10000.0 * Config.GuildXpShare);
    }

    // ---- shared hunting and the account (§10.4, §10.5) ----

    /// <summary>The guild of <paramref name="killer"/> when its members share kills without a party (Guilds:ShareWithoutParty); null otherwise.</summary>
    public Guild? SharingGuild(Player killer) => Config.GuildShareWithoutParty ? Of(killer.Character.Id) : null;

    /// <summary>The guild a silver pickup of <paramref name="p"/> pays Guilds:SilverTax to; null = none (the economy's tax applies).</summary>
    public Guild? TaxGuild(Player p) => Config.GuildSilverTax > 0 ? Of(p.Character.Id) : null;

    /// <summary>GuildUpdate key 14 (the tax, 1500 = 15 %): Guilds:SilverTax when it is on, else the stored value.</summary>
    private long TaxOf(Guild g) => Config.GuildSilverTax > 0 ? (long)Math.Round(Config.GuildSilverTax * 10000) : g.TaxA7m;

    /// <summary><paramref name="a7m"/> into the account of <paramref name="g"/>; the members get GuildUpdate kind 2 at the next second.</summary>
    public void Deposit(Guild g, long a7m)
    {
        if (a7m <= 0 || !_byId.ContainsKey(g.Id)) return;
        g.SilverA7m += a7m;
        _progressChanged.Add(g);
        _accountChanged.Add(g);
    }

    /// <summary>
    /// GuildUpdate kind 2 (SPEC §2.2: the client sets the accounts from keys 11 and 12): 0 the guild, 10 kind 2, 11 the silver,
    /// 14 the tax, and the official later updates' scalars (15 the name, 16 / 28 / 32 / 34 / 36 ""); no arrays.
    /// </summary>
    public GuildUpdateEvent AccountUpdate(Guild g) => new()
    {
        cf1 = g.Id, cgb = 2, cgc = g.SilverA7m, cgf = TaxOf(g), cgg = g.Name, cgh = world.Alliances.PlateOf(world.Alliances.OfGuild(g.Id)), cgt = "", cgx = "", cgz = "", cg1 = "",
    };

    /// <summary>
    /// .guild payout: <paramref name="by"/> (MASTER_OF_COIN or higher) pays <paramref name="silver"/> whole silver of its guild's account
    /// to the member <paramref name="name"/>, who must be in game. Returns the error text; null = paid (saved at once on both sides).
    /// </summary>
    public string? Payout(Player by, string? name, long silver)
    {
        if (Of(by.Character.Id) is not { } g) return "you are in no guild";
        if (g.Find(by.Character.Id)!.Role < GuildRole.MasterOfCoin) return "only a Master of Coin, The Right Hand or the guild master pays out";
        if (g.Find(name) is not { } m) return $"no member '{name}'";
        if (Online(m.Id) is not { } to) return $"'{m.Name}' is not in game";
        if (silver <= 0) return "give an amount of silver";
        if (silver > g.SilverA7m / 10000) return $"the guild account has {g.SilverA7m / 10000} silver";
        var a7m = silver * 10000;
        g.SilverA7m -= a7m;
        try { store?.SaveGuildProgress(g); }
        catch (Exception ex)
        {
            g.SilverA7m += a7m; // nothing paid: the account keeps its silver
            Log.Warn("guild", $"payout of guild '{g.Name}' not saved: {ex.Message}");
            return "the guild account could not be saved, nothing paid";
        }
        _progressChanged.Remove(g);
        _accountChanged.Add(g);
        Wallet.Credit(to, a7m, $"guild payout by '{by.Character.Name}'");
        world.GuildGoals.PaidOut(g, m.Id, a7m); // stage I: the Member Logs' SILVERBALANCE
        world.Saves.SaveNow(to.Character);
        Tell(g, $"[Guild] {by.Character.Name} paid {silver} silver of the guild account to {m.Name}");
        Mailer.GuildNotice(world, g, "Guild account payout", $"{by.Character.Name} paid {silver} silver of the guild account to {m.Name}.");
        Log.Info("guild", $"'{by.Character.Name}' pays {silver} silver of guild '{g.Name}' to '{m.Name}' ({g.SilverA7m / 10000} left)");
        return null;
    }

    /// <summary>
    /// <paramref name="g"/> is deleted (its last member left, or the GM's .guild disband): its account goes to <paramref name="to"/>
    /// (at once in game, else at its next Join as the market's offline silver), so a deleted guild destroys no silver; without a
    /// receiver (a deleted character) the amount is logged as lost.
    /// </summary>
    private void PayOutAccount(Guild g, GuildMember? to, bool receiverDeleted = false)
    {
        var a7m = g.SilverA7m;
        if (a7m <= 0) return;
        g.SilverA7m = 0;
        if (to is not null && Online(to.Id) is { } p)
        {
            Wallet.Credit(p, a7m, $"account of the disbanded guild '{g.Name}'");
            world.Saves.SaveNow(p.Character);
        }
        else if (to is not null && !receiverDeleted && world.Market is { } market)
            market.Batch(b => b.Credit(to.Id, a7m));
        else
        {
            Log.Warn("guild", $"guild '{g.Name}' deleted with {a7m / 10000} silver in its account: nobody to pay, lost");
            return;
        }
        Log.Info("guild", $"account of guild '{g.Name}' ({a7m / 10000} silver) paid to '{to.Name}'");
    }

    // ---- clears and contracts (§10.3, §10.6) ----

    /// <summary>
    /// A mob killed with <paramref name="credited"/> sharing it: once per guild among them, a kill for its kill contracts and, for a
    /// dungeon boss (a boss or miniboss of a dungeon instance, as the dungeon bounties), Guilds:ClearXp × its tier and the dungeon
    /// contracts once per guild and instance (a floor's further bosses add nothing: §10.3 pays per clear) [choice].
    /// </summary>
    public void Killed(Map map, Mob mob, IReadOnlyList<Player> credited)
    {
        if (!Enabled) return;
        var t = mob.Template;
        var dungeonBoss = t.Category is "boss" or "miniboss" && DungeonLayout.IsInstance(map.Cluster);
        foreach (var g in credited.Select(p => Of(p.Character.Id)).OfType<Guild>().Distinct())
        {
            Count(g, "kill", t.Tier, 1);
            if (!dungeonBoss || !CountClear(g, map.Cluster!)) continue;
            AddXp(g, Config.GuildClearXp * Math.Max(1, t.Tier));
            Count(g, "dungeon", t.Tier, 1);
        }
    }

    /// <summary>A harvest of <paramref name="count"/> × <paramref name="item"/> by <paramref name="p"/>: its guild's gather contracts.</summary>
    public void Gathered(Player p, ItemTemplate item, int count)
    {
        if (Of(p.Character.Id) is { } g) Count(g, "gather", item.Tier, count);
    }

    /// <summary>An expedition of <paramref name="tier"/> completed by <paramref name="p"/>: once per guild and <paramref name="instance"/>, Guilds:ClearXp × tier and the contracts.</summary>
    public void ExpeditionFinished(Player p, int tier, object instance)
    {
        if (Of(p.Character.Id) is not { } g) return;
        if (!CountClear(g, instance)) return;
        AddXp(g, Config.GuildClearXp * Math.Max(1, tier));
        Count(g, "expedition", tier, 1);
    }

    /// <summary>Whether this is the first clear of <paramref name="instance"/> (a dungeon instance id, an expedition) counted for <paramref name="g"/>.</summary>
    private bool CountClear(Guild g, object instance)
    {
        if (_clearsCounted.Count > 10_000) _clearsCounted.Clear(); // a long-running server: old instances are long gone
        return _clearsCounted.Add((g.Id, instance));
    }

    /// <summary>The key of the contract period now: "w" + the week's start (Monday at the bounties' reset) or "d" + the day's.</summary>
    private string PeriodKey()
    {
        var now = new DateTime(world.GameNow, DateTimeKind.Utc);
        return Config.GuildContractPeriod == GuildContractPeriod.Daily
            ? "d" + world.Bounties.DayStart(now).ToString("yyyyMMdd", CultureInfo.InvariantCulture)
            : "w" + world.Bounties.WeekStart(now).ToString("yyyyMMdd", CultureInfo.InvariantCulture);
    }

    /// <summary>
    /// The contracts of <paramref name="g"/> for the period now (Guilds:Contracts; null = off). A new period rolls Guilds:ContractCount
    /// of the kinds (<see cref="ContractKinds"/>), of the tier most of its members in game play at (their maps' tier, 4 without one, 3..8),
    /// with counts × (members + 1) / 2 (1..5), and tells them to the members in game.
    /// </summary>
    public GuildContracts? ContractsOf(Guild g)
    {
        if (!Enabled || !Config.GuildContracts) return null;
        var period = PeriodKey();
        if (g.Contracts?.Period == period) return g.Contracts;
        var tiers = g.Members.Select(m => Online(m.Id)?.Map?.Template?.Name.Tier).OfType<int>().Where(t => t > 0).ToList();
        var tier = tiers.Count == 0 ? 4 : Math.Clamp(tiers.GroupBy(t => t).OrderByDescending(x => x.Count()).ThenByDescending(x => x.Key).First().Key, 3, 8);
        var size = Math.Clamp((g.Members.Count + 1) / 2, 1, 5);
        var rng = new Random(g.Id.GetHashCode() ^ period.Aggregate(17, (h, ch) => h * 31 + ch));
        var kinds = ContractKinds.OrderBy(_ => rng.Next()).Take(Config.GuildContractCount);
        var daily = Config.GuildContractPeriod == GuildContractPeriod.Daily;
        g.Contracts = new GuildContracts
        {
            Period = period,
            Goals = [.. kinds.Select(k => new GuildContract { Kind = k.Kind, Tier = tier, Count = Math.Max(1, (daily ? k.Weekly / 5 : k.Weekly) * size) })],
        };
        _progressChanged.Add(g);
        Tell(g, "[Guild] New guild contracts: " + string.Join("; ", g.Contracts.Goals.Select(c => c.Describe())) + " (.guild contracts)");
        Log.Info("guild", $"guild '{g.Name}' contracts {period}: {string.Join("; ", g.Contracts.Goals.Select(c => c.Describe()))}");
        return g.Contracts;
    }

    /// <summary>
    /// <paramref name="n"/> of <paramref name="kind"/> at <paramref name="tier"/> for <paramref name="g"/>'s open contracts of that kind and
    /// at most that tier: a line to the members at each quarter; the completion's reward (Guilds:ContractXp and Guilds:ContractSilver ×
    /// tier / 4) goes to the guild and is told to them.
    /// </summary>
    private void Count(Guild g, string kind, int tier, int n)
    {
        if (n <= 0 || ContractsOf(g) is not { } contracts) return;
        foreach (var c in contracts.Goals)
        {
            if (c.Done || c.Kind != kind || tier < c.Tier) continue;
            var old = c.Progress;
            c.Progress = Math.Min(c.Count, old + n);
            _progressChanged.Add(g);
            if (c.Progress < c.Count)
            {
                if (c.Progress * 4 / c.Count > old * 4 / c.Count) Tell(g, $"[Guild] Contract: {c.Describe()} {c.Progress}/{c.Count}");
                continue;
            }
            c.Done = true;
            var xp = (long)Math.Round(Config.GuildContractXp * c.Tier / 4.0);
            var silver = (long)Math.Round(Config.GuildContractSilver * c.Tier / 4.0);
            Tell(g, $"[Guild] Contract complete: {c.Describe()}. Reward: {xp} guild xp, {silver} silver into the guild account");
            Log.Info("guild", $"guild '{g.Name}' completed contract {contracts.Period}: {c.Describe()}");
            Deposit(g, silver * 10000);
            AddXp(g, xp);
        }
    }

    /// <summary>.guild contracts: the period's contracts of <paramref name="g"/> with their progress and the next change.</summary>
    public IEnumerable<string> DescribeContracts(Guild g)
    {
        if (ContractsOf(g) is not { } contracts)
        {
            yield return "guild contracts are off on this server (Guilds:Contracts)";
            yield break;
        }
        foreach (var c in contracts.Goals) yield return $"{c.Describe()}: {c.Progress}/{c.Count}{(c.Done ? " (done)" : "")}";
        var now = new DateTime(world.GameNow, DateTimeKind.Utc);
        var next = Config.GuildContractPeriod == GuildContractPeriod.Daily ? world.Bounties.DayStart(now).AddDays(1) : world.Bounties.WeekStart(now).AddDays(7);
        yield return string.Create(CultureInfo.InvariantCulture, $"new contracts at {next:yyyy-MM-dd HH:mm} UTC");
    }

    // ---- the tick and the saves ----

    /// <summary>A system line to every member of <paramref name="g"/> in game.</summary>
    public void Tell(Guild g, string text)
    {
        var e = new SystemMessageEvent { d0p = text, d0q = 0 };
        foreach (var m in g.Members)
            Online(m.Id)?.Session.Send(e);
    }

    /// <summary>The tick's PvE part: the account updates (kind 2) once a second, the progress saved every 30 s.</summary>
    private void UpdatePve(TimeSpan now)
    {
        if (_accountChanged.Count > 0 && now >= _nextAccount)
        {
            _nextAccount = now + TimeSpan.FromSeconds(1);
            foreach (var g in _accountChanged)
                if (_byId.ContainsKey(g.Id)) SendTo(g, AccountUpdate(g));
            _accountChanged.Clear();
        }
        if (_progressChanged.Count > 0 && now >= _nextFlush)
        {
            _nextFlush = now + TimeSpan.FromSeconds(30);
            Flush();
        }
    }

    /// <summary>The guild goals of <paramref name="g"/> changed (stage I, <see cref="GuildGoals"/>): saved with the account at the next save.</summary>
    public void Touch(Guild g)
    {
        if (_byId.ContainsKey(g.Id)) _progressChanged.Add(g);
    }

    /// <summary>Saves the account, the experience, the contracts and the guild goals of every guild changed since the last save (the tick, the world's end).</summary>
    public void Flush()
    {
        foreach (var g in _progressChanged.ToList())
        {
            if (_byId.ContainsKey(g.Id))
                try { store?.SaveGuildProgress(g); }
                catch (Exception ex)
                {
                    Log.Warn("guild", $"guild '{g.Name}' progress not saved (retried): {ex.Message}");
                    continue; // kept for the next save
                }
            _progressChanged.Remove(g);
        }
    }
}
