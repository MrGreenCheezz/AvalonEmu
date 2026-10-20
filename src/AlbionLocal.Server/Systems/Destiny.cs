using System.Globalization;
using AlbionLocal.Photon;
using AlbionLocal.Server.Data;
using AlbionLocal.Server.Entities;
using AlbionLocal.Server.Game;
using AlbionLocal.Server.Protocol;

namespace AlbionLocal.Server.Systems;

/// <summary>
/// Fame and the destiny board in play (analysis/work/progression/PROGRESSION.md): a gain raises the character's total fame
/// and, when the character has a board of its own (<see cref="Character.Progression"/>; null = everything unlocked, which
/// takes no fame), the in-progress nodes it feeds (<see cref="DestinyBoard.Apply"/>). The burst goes out as §6.6 has it:
/// per node in ascending id its FinishedAchievement (151) per completed level, then its AchievementProgressInfo (152) —
/// the official server sends the post-level-up 152 twice, once is enough for the client —, UpdateFame (82) last. While premium
/// the nodes get UpdateFame key 2 × premiumdestinyboardprogressionfactor (1.5, Premium:FameFactor) for the key-5 sources (§2.3; the
/// total × 1). Auto-learn nodes with learning points (stageJ focus-lp SPEC D6, §4) spend them: UpdateLearningPoints (83, key 6 = 0)
/// right before the first such node's events, UpdateFame keys 12 = 13 = the extra progress, 14 = the points spent. A level-up needs
/// nothing else on the wire: the client applies its rewards from the 151 itself (§5). Touched only under <see cref="World.Sync"/>.
/// </summary>
public static class Destiny
{
    /// <summary>
    /// The fame of a kill, a harvest or a craft for <paramref name="p"/>: <paramref name="gain"/> (a7m, UpdateFame key 2, the
    /// zone bonus included) to the total and the board (<paramref name="boardGain"/> when the board takes another amount: a
    /// craft's destinyandjournalcraftfamefactor), <paramref name="bonus"/> = key 3 (a kill's zone-bonus part). A kill sends
    /// keys 5 and 15 true (a player kill, <paramref name="pvp"/>: key 15 only), a harvest key 5 only (HARVEST.md §5), a craft as a harvest (not captured). Then the
    /// labourer journals of the inventory take the board's amount (<see cref="Journals.OnFame"/>; <paramref name="source"/> = the harvested,
    /// caught or crafted item their validitems are checked against; <paramref name="journals"/> false: none, a GM grant). Returns the board's steps (empty without a board).
    /// A PvE gain (not <paramref name="pvp"/>, <paramref name="guild"/> true: not a GM's grant) first takes the fame perk of the player's
    /// guild level (all three amounts) and then feeds the guild's experience (guilds SPEC §10.3, <see cref="Guilds.OnFame"/>); any gain but a
    /// GM's goes to the member's Member Logs and the guild's craft goals (stage I, <see cref="GuildGoals.OnFame"/>).
    /// </summary>
    public static List<DestinyBoard.Step> Award(Player p, long gain, FameContext ctx, long bonus = 0, long? boardGain = null, bool pvp = false,
        ItemTemplate? source = null, bool journals = true, bool guild = true)
    {
        if (guild && !pvp && gain > 0)
        {
            if (p.World.Guilds.FameBonus(p) is var perk and > 0)
            {
                gain = Rates.ScaleFame(gain, 1 + perk);
                bonus = Rates.ScaleFame(bonus, 1 + perk);
                boardGain = boardGain is { } b ? Rates.ScaleFame(b, 1 + perk) : null;
            }
            p.World.Guilds.OnFame(p, gain);
        }
        if (guild) p.World.GuildGoals.OnFame(p, gain, ctx, pvp);
        var c = p.Character;
        var before = c.Fame;
        c.Fame += gain;
        p.World.Vanity.Fame(p, before, c.Fame); // stageN vanity: a total-fame milestone may drop a cosmetic
        p.World.Saves.MarkChanged(c);
        // The premium share of the nodes (UpdateFame key 5 sources only; the client adds the same premium part to what it shows itself).
        var board = boardGain ?? gain;
        if (!pvp && Premium.FameFactor(p) is var premium && premium != 1) board = Rates.ScaleFame(board, premium);
        var steps = Advance(p, board, ctx, out var spent, out var extra);
        if (journals)
        {
            // PVE-DESIGN step 11 (stageH journals SPEC §C.3, §B.2): the season points (376) and the journal's fame missions (600) before the UpdateFame.
            Seasons.OnFame(p, gain, ctx, pvp);
            AlbionJournal.OnFame(p, gain, ctx, pvp);
        }
        p.Session.Send(new UpdateFameEvent
        {
            d3f = p.Id,                         // [0]
            d3g = c.Fame,                       // [1] the new total (a7m)
            d3h = gain,                         // [2] the gain, zone bonus included
            d3i = bonus,                        // [3] the zone-bonus part (kills)
            d3k = !pvp,                         // [5] premium may apply (kills, harvests; not a player kill, stageE pvp SPEC §4.3)
            d3r = extra, d3s = extra,           // [12], [13] the progress bought with learning points (auto-learn; 0 = left out)
            d3t = spent,                        // [14] the learning points spent
            d3u = ctx.Source == FameSource.Kill, // [15] kills and tomes; not on harvests
        });
        if (journals) Journals.OnFame(p, boardGain ?? gain, ctx, source, pvp); // stageH journals SPEC §A.3: after the UpdateFame of the gain
        if (guild && !pvp) p.World.Factions.OnFame(p, gain, ctx.Source); // stageN faction SPEC §2: faction points and standing in a warfare cluster
        if (ctx.Source == FameSource.Kill) Attunement.OnFame(p, gain); // stageN awakening SPEC §5: the awakened weapon in hand gains attunement
        return steps;
    }

    /// <summary>
    /// A tome's fame (adddestinyboardpvefame, stageD consumables SPEC §4.3; PROGRESSION.md §2.2, §6.3): <paramref name="gain"/> (a7m) × Rates:Fame
    /// to the nodes a kill would feed by the equipped gear, the total fame unchanged; UpdateFame {0, 1 the total, 2 gain, 7 the
    /// tome's item type id, 15 true}, no key 5.
    /// </summary>
    public static List<DestinyBoard.Step> Tome(Player p, long gain, ItemTemplate? tome, int tier)
    {
        gain = Rates.ScaleFame(gain, p.World.Rates.Fame);
        var kill = Kill(p, tier);
        var steps = Advance(p, gain, kill with { Source = FameSource.Tome }, out var spent, out var extra);
        p.World.Saves.MarkChanged(p.Character);
        p.Session.Send(new UpdateFameEvent { d3f = p.Id, d3g = p.Character.Fame, d3h = gain, d3m = tome?.Index ?? 0, d3r = extra, d3s = extra, d3t = spent, d3u = true });
        return steps;
    }

    /// <summary>
    /// A gathering tome or the fish food's fishing fame (adddestinyboardgatheringfame / adddestinyboardfishingfame, stageN spells-bosses):
    /// <paramref name="gain"/> (a7m) × Rates:Fame to every in-progress node <paramref name="pick"/> chooses (the tome's achievementid node,
    /// the fishingfame nodes), the total fame unchanged; then the UpdateFame of a tome {0, 1 the total, 2 gain, 7 the item, 15 true}.
    /// </summary>
    public static List<DestinyBoard.Step> TomeTo(Player p, long gain, ItemTemplate? tome, Func<BoardNode, bool> pick)
    {
        gain = Rates.ScaleFame(gain, p.World.Rates.Fame);
        var steps = new List<DestinyBoard.Step>();
        long spent = 0, extra = 0;
        if (p.Character.Progression is { } progression && p.World.GameData?.Board is { } board)
            foreach (var id in progression.Levels.Keys.Where(id => board.InProgress(progression, id) && pick(board.Nodes[id])).Order().ToList())
            {
                steps.AddRange(Advance(p, gain, new FameContext(FameSource.Tome, 0, [], id), out var s, out var x));
                (spent, extra) = (spent + s, extra + x);
            }
        p.World.Saves.MarkChanged(p.Character);
        p.Session.Send(new UpdateFameEvent { d3f = p.Id, d3g = p.Character.Fame, d3h = gain, d3m = tome?.Index ?? 0, d3r = extra, d3s = extra, d3t = spent, d3u = true });
        return steps;
    }

    /// <summary>
    /// The board part of a gain: the nodes' new levels and progress, and their 151/152 events to the player. With learning points, the
    /// auto-learn flag on (UseFlag 0, Join key 99) and nodes marked for auto-learn (op 312), those nodes spend points (<see cref="LearningBudget"/>):
    /// <paramref name="spent"/> and <paramref name="extra"/> (a7m) for the UpdateFame, the UpdateLearningPoints before the first one's events.
    /// </summary>
    public static List<DestinyBoard.Step> Advance(Player p, long gain, FameContext ctx, out long spent, out long extra)
    {
        (spent, extra) = (0, 0);
        if (p.Character.Progression is not { } progression || p.World.GameData?.Board is not { } board) return [];
        var c = p.Character;
        var budget = LearningBudget(p);
        var steps = board.Apply(progression, gain, ctx, budget);
        if (budget is { Spent: > 0 })
        {
            (spent, extra) = (budget.Spent, budget.Extra);
            c.LearningPoints = budget.Points;
            p.World.Saves.MarkChanged(c);
        }
        if (budget is not null && c.AutoLearn is { } auto && budget.Nodes.Count != auto.Ids.Length) // neighbours that inherited the flag
        {
            c.AutoLearn = auto with { Ids = [.. budget.Nodes.Order()] };
            p.World.Saves.MarkChanged(c);
        }
        var told = spent == 0;
        foreach (var s in steps)
        {
            if (!told && s.Node == budget!.First)
            {
                p.Session.Send(LearningPoints.Event(p, -spent, LearningPoints.Silent)); // §6.6: right before the first auto-learn node's events
                told = true;
            }
            p.Session.Send(s.Finished ? Finished(p, s) : Progress(p, s));
        }
        if (steps.Any(s => s.Finished))
        {
            Log.Info("destiny", $"'{p.Character.Name}': " + string.Join(", ", steps.Where(s => s.Finished).Select(s => $"{board.Nodes[s.Node]} level {s.Level + 1}")));
            // §5: the server's own bars follow the new levels (destiny wear bonus); the client works its bars out from the
            // 151 itself and the official server sends no 96 here, so neither do we.
            p.RecalculateStats(p.World.GameNow);
        }
        return steps;
    }

    /// <summary>
    /// The auto-learn nodes of <paramref name="p"/> and the learning points a gain may spend on them, or null without nodes marked for
    /// auto-learn (op 312). The nodes count whether or not there are points to spend (a marked node finishing level 0 passes the mark to
    /// its neighbours either way, as the client's cs6.k_t1b88); the points are 0 with AutoUseLearningPoints off or none left. The fame
    /// factor is characters.xml learningpointfamefactor (5).
    /// </summary>
    private static DestinyBoard.LearningBudget? LearningBudget(Player p)
    {
        var c = p.Character;
        if (c.AutoLearn is not { Ids.Length: > 0 } auto) return null;
        var factor = (long)Math.Round((p.World.GameData?.Defaults ?? CharacterDefaults.Default).LearningPointFameFactor * DestinyBoard.A7m);
        return new DestinyBoard.LearningBudget(new SortedSet<int>(auto.Ids), c.UseFlag(0) ? Math.Max(0, c.LearningPoints) : 0, factor);
    }

    /// <summary>The context of a kill: the equipped main hand, off hand, head, armor and shoes, the mob's tier.</summary>
    public static FameContext Kill(Player p, int mobTier) =>
        new(FameSource.Kill, mobTier, [.. DestinyBoard.KillSlots.Select(s => p.Inventory.Equipment[s]).OfType<Item>()
            .Select(i => (DestinyBoard.BaseName(i.Template.Name), i.Template.Tier))]);

    /// <summary>The context of a harvest: the resource and its tier.</summary>
    public static FameContext Harvest(ItemTemplate resource) =>
        new(FameSource.Harvest, resource.Tier, [(DestinyBoard.BaseName(resource.Name), resource.Tier)]);

    /// <summary>The context of a catch (stageG fishing SPEC §9): the fish and its tier (GATHER_FISH's itemlist T?_FISH*).</summary>
    public static FameContext Fishing(ItemTemplate fish) =>
        new(FameSource.Fishing, fish.Tier, [(DestinyBoard.BaseName(fish.Name), fish.Tier)]);

    /// <summary>The context of a crop's harvest (stageH islands SPEC §3.5): the seed and its tier (farmharvestfame's itemlist T?_FARM_*_SEED).</summary>
    public static FameContext Farm(ItemTemplate seed) =>
        new(FameSource.Farm, seed.Tier, [(DestinyBoard.BaseName(seed.Name), seed.Tier)]);

    /// <summary>The context of a craft: the crafted item and its tier (craftitemfame, its tier gates on both columns).</summary>
    public static FameContext Craft(ItemTemplate item) =>
        new(FameSource.Craft, item.Tier, [(DestinyBoard.BaseName(item.Name), item.Tier)]);

    /// <summary>
    /// UpdateFame key 2 of a harvest (HARVEST.md §4): items.xml famevalue × base amount × the cluster's gatheringfamefactor,
    /// a7m, in floats and truncated as the kill fame (T2 fiber 1.5 × 3 = 4.5, T4 7.5 × 2 = 15; T1 famevalue 0 = none).
    /// </summary>
    public static long HarvestFame(double fameValue, int amount, double gatheringFactor) =>
        (long)((float)fameValue * amount * (float)gatheringFactor * 10000f);

    /// <summary>FinishedAchievement (151, aow): 0 player, 1 node, 2 the level just completed (0 for a plain node).</summary>
    public static FinishedAchievementEvent Finished(Player p, DestinyBoard.Step s) => new() { cac = p.Id, cad = s.Node, cae = s.Level };

    /// <summary>
    /// AchievementProgressInfo (152, akj): 0 player, 1 node, 2 its level (left out at 0, as official; the client ignores it),
    /// 3 v / levelFame, 4 "[[v]]" (v as a7m raw).
    /// </summary>
    public static AchievementProgressInfoEvent Progress(Player p, DestinyBoard.Step s) => new()
    {
        ddn = p.Id, brc = s.Node, brd = s.Level, bre = s.Fraction, brf = s.Json,
    };

    /// <summary>
    /// FullAchievementProgressInfo (153, api) of a Join: 0 player, 1 the in-progress ids with v &gt; 0, 2 their levels
    /// (ignored by the client), 3 fractions, 4 "[[v]]" (§6.5; without it the board shows no progress). Null when there is
    /// nothing to send (no board, or no node with progress).
    /// </summary>
    public static FullAchievementProgressInfoEvent? FullProgress(long playerId, Progression progression, DestinyBoard? board)
    {
        if (board is null) return null;
        var entries = progression.Progress.Where(e => e.Value > 0 && board.InProgress(progression, e.Key))
            .Select(e => new DestinyBoard.Step(e.Key, false, progression.Levels[e.Key], e.Value, board.LevelFame(e.Key, progression.Levels[e.Key])))
            .ToList();
        if (entries.Count == 0) return null;
        return new FullAchievementProgressInfoEvent
        {
            ddn = playerId,
            cc3 = [.. entries.Select(e => e.Node)],
            cc4 = [.. entries.Select(e => e.Level)],
            cc5 = [.. entries.Select(e => e.Fraction)],
            cc6 = [.. entries.Select(e => e.Json)],
        };
    }

    /// <summary>A node's state for .progress show: "name (id) level n, v / need".</summary>
    public static string Describe(DestinyBoard board, Progression p, int id)
    {
        var level = p.Levels.GetValueOrDefault(id);
        var count = board.LevelCount(id);
        return level >= count ? $"{board.Nodes[id]} level {level} (max)"
            : string.Create(CultureInfo.InvariantCulture,
                $"{board.Nodes[id]} level {level}/{count}, {p.Progress.GetValueOrDefault(id) / 10000.0:0.####} / {board.LevelFame(id, level) / 10000.0:0.####}");
    }
}
