using AlbionLocal.Photon;
using AlbionLocal.Server.Data;
using AlbionLocal.Server.Entities;
using AlbionLocal.Server.Game;
using AlbionLocal.Server.Network;
using AlbionLocal.Server.Protocol;

namespace AlbionLocal.Server.Systems;

/// <summary>Personal Conqueror's Challenge (451–457). Activity is recorded prospectively, never copied from guild
/// Might or historical total fame. The installed client's season keys and reward items remain authoritative.
/// All entry points run under World.Sync; reward inventory and its claim marker commit together.</summary>
public static partial class PvpChallenge
{
    public const short NeedsBoughtPremium = 18080, AlreadyClaimed = 18081, NotEnoughMight = 18082,
        InTutorial = 18083, NoSpace = 18084;
    private const long Max = long.MaxValue / 4;
    private static PvpChallengeData Data(World world) => world.GvgSeasons?.PersonalChallenge ?? PvpChallengeData.None;
    // During a known off-season, the next client season receives Might. After the last known season we do
    // not fabricate another client season; weekly activity still works and future data can add its calendar.
    private static GvgSeason? Season(World world) => world.GvgSeasons?.Seasons
        .Where(s => s.End.Ticks > world.GameNow).OrderBy(s => s.Start).FirstOrDefault();
    private static bool BoughtPremium(Player p) => Premium.Is(p) && (p.World.Shop.PurchasedPremiumDays(p.Character) >= 7
        || p.PremiumOverride == true || p.PremiumOverride is null && p.World.Config.PremiumMode == PremiumMode.Free);

    private static PersonalMightLog LogOf(Player p)
    {
        var goals = p.Character.Goals ??= new();
        if (goals.Conqueror is not { } log)
        {
            goals.Conqueror = log = new() { CoveredSinceUtc = p.World.GameNow };
            Log.Info("conqueror", $"'{p.Character.Name}': personal Might coverage starts {new DateTime(log.CoveredSinceUtc, DateTimeKind.Utc):O}; earlier activities are unknown");
            p.World.Saves.MarkChanged(p.Character);
        }
        var week = Data(p.World).WeekStart(new DateTime(p.World.GameNow, DateTimeKind.Utc));
        if (log.Week != week)
        {
            // Never discard unfinished progress or an unclaimed earned chest. A claimed cycle resets at the
            // next Saturday boundary. Unclaimed premium chests currently wait here too (no automatic mail).
            if (log.WeeklyClaimed)
            {
                log.WeeklyMight = 0;
                log.WeeklyClaimed = false;
                log.WeeklyFavorPaid.Clear();
            }
            log.Week = week;
            p.World.Saves.MarkChanged(p.Character);
        }
        return log;
    }

    private static byte State(Player p, PersonalMightLog log) => log.WeeklyClaimed ? (byte)3
        : Data(p.World).WeeklyRequirement <= 0 || log.WeeklyMight < Data(p.World).WeeklyRequirement ? (byte)0
        : BoughtPremium(p) ? (byte)2 : (byte)1;

    /// <summary>345319 A52A30 → 1136CAC: key 0 season total, key 1 weekly total, key 2 reward state.</summary>
    public static GetPvpChallengeDataResponse Response(Player p)
    {
        var log = LogOf(p);
        var season = Season(p.World);
        var total = season is not null && log.Seasons.TryGetValue(season.Key, out var stats) ? Total(stats.Values) : 0;
        return new() { a3f = total, a3g = log.WeeklyMight, a3h = State(p, log) };
    }

    /// <summary>453 uses personaltype, falling back to the season's mapped guild category (345319 649740,
    /// 1145520). An unrecorded past season returns empty arrays, not reconstructed historical statistics.</summary>
    public static GetPersonalMightStatsResponse Stats(Player p, int index)
    {
        var season = p.World.GvgSeasons?.Seasons.FirstOrDefault(s => s.Index == index);
        if (season is null) return new() { ReturnCode = 1, a2y = index, a2z = [], a20 = [] };
        var log = LogOf(p);
        var rows = log.Seasons.TryGetValue(season.Key, out var stats)
            ? stats.OrderBy(s => s.Key, StringComparer.Ordinal).ToArray() : [];
        return new() { a2y = index, a2z = [.. rows.Select(r => r.Key)], a20 = [.. rows.Select(r => r.Value)] };
    }

    public static void OnFame(Player p, long gain, FameContext context, bool pvp)
    {
        if (pvp) return;
        var name = context.Source switch { FameSource.Kill => "pve", FameSource.Harvest => "gathering", FameSource.Fishing => "fishing", _ => null };
        if (name is not null) Fame(p, gain, name);
    }

    /// <summary>The final hunt's actual quest fame, whose Destiny.Award has journals:false. It does not also
    /// enter OnFame; ordinary mob-kill fame remains its separate PvE credit.</summary>
    public static void OnTracking(Player p, long gain) => Fame(p, gain, "tracking");

    private static void Fame(Player p, long gain, string name)
    {
        if (gain <= 0 || OpeningTutorial.Active(p) || !Pvp.RulesOf(p.World, p.Map).PlayersAreLethal
            || !Data(p.World).Fame.TryGetValue(name, out var rule)) return;
        var solo = name == "pve" && DungeonLayout.IsInstance(p.Map?.Cluster)
            && (p.World.Parties.Of(p.Character.Id) is not { } party || party.Members.Count <= 1);
        var points = rule.Factor * Math.Pow(gain / 10000.0, rule.Exponent)
            * rule.ZoneFactor(Pvp.RulesOf(p.World, p.Map).WarningType) * (solo ? rule.SoloFactor : 1);
        Gain(p, rule, PvpChallengeData.Raw(points), PvpChallengeData.Raw(points * rule.FavorFactor));
    }

    /// <summary>Called only by completed gameplay objectives; no synthetic or GM objective credits.</summary>
    public static void OnObjective(Player p, string? objective, int participants = 1)
    {
        if (objective is null || OpeningTutorial.Active(p) || !Data(p.World).Objectives.TryGetValue(objective, out var rule)) return;
        // 345319 628ADA–628B58 (344269 62598A): divide both raw rewards by
        // max(actual participants, objective.mindivisor), before bracket/premium bonuses.
        var divisor = Math.Max(rule.MinimumDivisor, Math.Max(1, participants));
        Gain(p, rule, PvpChallengeData.Raw(rule.Might) / divisor, PvpChallengeData.Raw(rule.Favor) / divisor);
    }

    private static void Gain(Player p, PvpChallengeData.Source source, long might, long favor, GvgSeason? targetSeason = null, byte reachedBracket = 0, bool send = true)
    {
        if (might <= 0 && favor <= 0) return;
        var data = Data(p.World);
        if (data.WeeklyRequirement <= 0) return;
        long mightBracket = 0, favorBracket = 0;
        if (!source.SeasonOnly && source.UseBracketBonus && Season(p.World) is { } current && p.World.Guilds.Of(p.Character.Id) is { } guild
            && guild.Progress.Seasons.FirstOrDefault(s => s.Season == current.Key) is { } guildSeason
            && current.BracketOf(guildSeason.Points) is { } bracket)
        {
            var boostedMight = Multiply(might, bracket.MightFactor);
            var boostedFavor = Multiply(favor, bracket.FavorFactor);
            mightBracket = boostedMight - might;
            favorBracket = boostedFavor - favor;
            might = boostedMight;
            favor = boostedFavor;
        }
        var sourceFavor = favor;
        var log = LogOf(p);
        var before = State(p, log);
        if ((targetSeason ?? Season(p.World)) is { } season && source.Stat(season) is { Length: > 0 } name)
        {
            if (!log.Seasons.TryGetValue(season.Key, out var stats)) log.Seasons[season.Key] = stats = new(StringComparer.Ordinal);
            stats[name] = Add(stats.GetValueOrDefault(name), might);
        }
        if (!source.SeasonOnly) log.WeeklyMight = Math.Min(data.WeeklyRequirement, Add(log.WeeklyMight, might));
        foreach (var reward in data.WeeklyFavor)
            if (!source.SeasonOnly && log.WeeklyMight >= reward.Might && !log.WeeklyFavorPaid.Contains(reward.Might)
                && (BoughtPremium(p) && reward.BoughtPremium || Premium.Is(p) && reward.Premium || !Premium.Is(p) && reward.NonPremium))
            {
                favor = Add(favor, reward.Favor);
                log.WeeklyFavorPaid.Add(reward.Might);
            }
        var oldFavor = p.Character.MistFavor;
        p.Character.MistFavor = Add(oldFavor, favor);
        p.World.Saves.MarkChanged(p.Character);
        if (!send) return;
        // 345319 C8F7C0–C90165: 496 is the original overhead/chat receipt, not a season-counter update.
        // Keys 1/4 are final amounts; 2/5 contain their bracket portion (628370), not another award.
        // Premium's numeric Might/Favor factor remains unconfirmed; never substitute its fame factor.
        // Weekly Favor milestones are paid through currency 85, not attributed again to this activity.
        p.Session.Send(new MightAndFavorReceivedEventEvent
        {
            cqt = source.Id, cqu = might, cqv = mightBracket, cqx = sourceFavor, cqy = favorBracket,
            cq1 = reachedBracket != 0, cq2 = reachedBracket, cq3 = [], cq4 = [],
        });
        if (p.Character.MistFavor != oldFavor && p.World.Factions.Data?.CurrencyIndex.TryGetValue("FAVOR", out var index) == true)
            p.Session.Send(new UpdateCurrencyEvent { d2v = Factions.General, d2x = index, d2y = p.Character.MistFavor - oldFavor, d24 = p.Character.MistFavor });
        var state = State(p, log);
        if (state != before) p.Session.Send(new WeeklyPvpChallengeRewardStateUpdateEvent { d6s = state });
    }

    public static short Claim(Player p)
    {
        if (OpeningTutorial.Active(p)) return InTutorial;
        var log = LogOf(p);
        if (log.WeeklyClaimed) return AlreadyClaimed;
        var data = Data(p.World);
        if (data.WeeklyRequirement <= 0 || log.WeeklyMight < data.WeeklyRequirement) return NotEnoughMight;
        if (!BoughtPremium(p)) return NeedsBoughtPremium;
        if (data.WeeklyItem is not { } name || p.World.Items.Find(name) is not { CanCreate: true } type) return 1;
        var snapshot = p.Character.Clone();
        snapshot.Goals!.Conqueror!.WeeklyClaimed = true;
        var rc = CommitReward(p, snapshot, [(type, 1)], NoSpace);
        if (rc == 0) p.Session.Send(new WeeklyPvpChallengeRewardStateUpdateEvent { d6s = 3 });
        return rc;
    }

    /// <summary>One atomic saved claim and its entire inventory delivery. Empty items commits only the claim/loot
    /// ledger. Capacity is checked before any roll is exposed or live state changes; post-commit events may be retried
    /// by requesting the authoritative ledger, never by awarding a second copy.</summary>
    private static short CommitReward(Player p, Character snapshot, IReadOnlyList<(ItemTemplate Type, int Count)> items, short noSpace)
    {
        var liveBag = p.Inventory.Bag;
        if (!WorldSession.Fits(liveBag, items)) return noSpace;
        p.Inventory.SaveTo(snapshot, id => p.World.Spells?.Name(id));
        foreach (var vault in p.OpenVaults.Values) snapshot.Vaults[vault.Key] = Inventory.Records(vault.Container, id => p.World.Spells?.Name(id));
        var bag = new Container(liveBag.Id, liveBag.Size);
        for (var slot = 0; slot < bag.Size; slot++)
        {
            bag.Unloaded[slot] = liveBag[slot] is null ? liveBag.Unloaded[slot] : null;
            if (liveBag[slot] is { } item)
                bag.Put(slot, new Item(item.Id, item.Template, item.Count, item.Quality, item.DurabilityRaw, item.Active, item.Passive, item.Source)
                    { JournalFame = item.JournalFame, Soul = item.Soul });
        }
        var touched = new HashSet<Item>();
        foreach (var (type, count) in items)
            touched.UnionWith(WorldSession.PutItems(bag, type, count, record => p.Session.PrepareTradeItem(type, record, p)));
        var records = Inventory.Records(bag, id => p.World.Spells?.Name(id));
        var overflow = snapshot.Inventory.Skip(bag.Size).ToArray();
        if (overflow.Length > 0)
        {
            while (records.Count < bag.Size) records.Add(null);
            records.AddRange(overflow);
        }
        snapshot.Inventory = records;
        try
        {
            if (!p.World.Saves.Commit(() =>
            {
                // Commit's save barrier refreshes the joined player's movement before writing. Preserve that
                // current position instead of overwriting it with the earlier reward-planning snapshot.
                snapshot.PositionX = p.Character.PositionX;
                snapshot.PositionY = p.Character.PositionY;
                snapshot.PlayedMilliseconds = p.Character.PlayedMilliseconds;
                return p.World.Characters.Save(snapshot);
            })) return 1;
        }
        catch (Exception e)
        {
            Log.Error("conqueror", $"Reward transaction failed for {p.Character.Id}: {e.Message}");
            return 1;
        }
        // Install the entire committed state before any packet/hook can throw. A retry sees the claim marker.
        p.Character.Goals = snapshot.Goals;
        var added = new List<(Item Item, int Slot)>();
        var changed = new List<Item>();
        foreach (var planned in touched)
        {
            var slot = bag.IndexOf(planned);
            if (liveBag[slot] is { } current && current.Id == planned.Id)
            {
                current.Count = planned.Count;
                changed.Add(current);
            }
            else { liveBag.Put(slot, planned); added.Add((planned, slot)); }
        }
        p.SaveItems();
        foreach (var item in changed) p.Session.SendItem(item);
        foreach (var (item, slot) in added)
        {
            p.Session.SendItem(item);
            p.Session.Send(new InventoryPutItemEvent { ckw = item.Id, ckx = slot, cky = liveBag.Id });
        }
        foreach (var (item, _) in added) p.World.Scripts.OnItemAdded(p, item);
        return 0;
    }
    private static long Add(long a, long b) => Math.Min(Max, Math.Clamp(a, 0, Max) + Math.Clamp(b, 0, Max));
    // Native a7m multiplication (345319 9442A0): integer product / 10000, truncated.
    private static long Multiply(long value, double factor) => (long)Math.Min(Max,
        decimal.Truncate((decimal)value * PvpChallengeData.Raw(Math.Max(0, factor)) / 10000));
    private static long Total(IEnumerable<long> values) => values.Aggregate(0L, Add);
}
