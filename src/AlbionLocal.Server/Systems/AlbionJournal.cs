using System.Globalization;
using AlbionLocal.Photon;
using AlbionLocal.Server.Data;
using AlbionLocal.Server.Entities;
using AlbionLocal.Server.Game;
using AlbionLocal.Server.Protocol;
using M = AlbionLocal.Server.Data.JournalCatalog.Mission;

namespace AlbionLocal.Server.Systems;

/// <summary>
/// One thing that happened, for the journal's missions (SPEC §B.5): its kind (the leaf kind it may count for: killmob, killmobfame,
/// fameearned, gatherfame, fishingfame, gather, finishexpedition, reachseasonpoints; stage I: killplayer, knockdownplayer,
/// killplayerfame, killingspree, die, finishduel, gainchallengepoints, reachguildchallengelevel, gotguildmemberreward), the amount (1 kill,
/// fame or points a7m raw, items, a guild challenge level, 1 reward),
/// whether it sets the progress instead of adding to it, and what the filters look at.
/// </summary>
public sealed record JournalEvent(string Kind, long Amount)
{
    public bool Set { get; init; }
    public Map? Map { get; init; }
    public Mob? Mob { get; init; }
    public ItemTemplate? Item { get; init; }

    /// <summary>A kill credited to a party member near the killer, not the killer itself (killmob counts it only with countpartykills).</summary>
    public bool PartyKill { get; init; }

    public int PartySize { get; init; } = 1;

    /// <summary>finishexpedition: the expedition's uniquename, its category, its hardcore level (0 = none) and the run's seconds.</summary>
    public string? Expedition { get; init; }
    public string? ExpeditionCategory { get; init; }
    public int Difficulty { get; init; }
    public double Seconds { get; init; }

    /// <summary>Stage I PvP (pvp-goals SPEC §2.4): the receiver's and the victim's flags (IfTargetIsInOpposingFaction), the victim's attackers
    /// (IfCombatHistoryAttackers), an assist's credit, a player-bot victim (or killer, for die), pvp / pve (killingspree, die), the duel's
    /// outcome (finishduel: "Win" for the winner's credit, null for every finished duel).</summary>
    public byte KillerFlag { get; init; }
    public byte VictimFlag { get; init; }
    public int Attackers { get; init; } = 1;
    public bool Assist { get; init; }
    public bool Bot { get; init; }
    public string? Where { get; init; }
    public string? Outcome { get; init; }

    /// <summary>StageL corrupted: a PvP credit's specialcondition (KillCorruptedDungeonInvader, KillCorruptedDungeonHost; null = none), and haveinfamy's type.</summary>
    public string? Special { get; init; }
    public string? InfamyType { get; init; }

    /// <summary>killingspree: the receiver's kills of the last minutes (GameTimeStamps, assists marked); each leaf counts its own window.</summary>
    public IReadOnlyList<(long Ticks, bool Assist)>? Spree { get; init; }

    /// <summary>gotguildmemberreward: the guild's bracket of the reward (iron … crystal).</summary>
    public string? Bracket { get; init; }

    /// <summary>StageR lore: claimloresnippet's lore.xml uniquename.</summary>
    public string? LoreSnippet { get; init; }
}

/// <summary>
/// The Albion Journal (analysis/work/stageH/journals/SPEC.md §B; PVE-DESIGN.md step 11, the bestiary of PVE and CREATURES first). The
/// achievements of albionjournal.xml (<see cref="JournalCatalog"/>) progress from the server's events (<see cref="JournalEvent"/>: kills,
/// fame gains, harvests, expeditions, season points; stage I the PvP credits of <see cref="PvpGoals"/>, deaths to players, duels), each leaf mission whose filters hold taking the event's amount up to its goal; an
/// achievement is done when its mission tree is (siblings all, any count=n, progresssum sum=n, or). A leaf, attribute or filter the server
/// cannot judge never progresses (safe: the client shows 0). Only the categories of Journal:Categories (empty = all). On the wire: a tracked
/// achievement's progress 600 kind 1 {[name], [progress]}; a completion 362 {0 name, 2 ""}, 600 kind 0 (the whole tracked list) and
/// 601 {0 claimable count}; op 144 (<see cref="Achievements"/>) is the window's state, op 514 (<see cref="Claim"/>) the claims, never
/// granted by the server itself. Touched only under <see cref="World.Sync"/>.
/// </summary>
public static class AlbionJournal
{
    /// <summary>ClaimAlbionJournalReward results (gc8.a 0x181c9f900).</summary>
    public const short Ok = 0, AlreadyClaimed = 20561, NotClaimableYet = 20562, InvalidReward = 20563, NoInventorySpace = 20564;

    /// <summary>The journal when it is on and read, else null.</summary>
    public static JournalCatalog? Catalog(World world) => world.Goals.Journal.Enabled ? world.Clusters?.JournalIfLoaded : null;

    /// <summary>
    /// Whether <paramref name="a"/> progresses: its category is in Journal:Categories, and a Steam mirror (steamachievementname) only with
    /// Journal:SteamMirrors, since nobody has traced whether the client hands a 362 of one to the Steam API (a real unlock on the account).
    /// </summary>
    private static bool Enabled(World world, JournalCatalog.Achievement a) =>
        (!a.Steam || world.Goals.Journal.SteamMirrors)
        && (world.Goals.Journal.Categories is not { Count: > 0 } list || list.Contains(a.Category, StringComparer.OrdinalIgnoreCase));

    // ---- events

    /// <summary>A fame gain (<see cref="Destiny.Award"/>): fameearned always, killmobfame for a PvE kill, gatherfame / fishingfame for a harvest / catch.</summary>
    public static void OnFame(Player p, long gain, FameContext ctx, bool pvp)
    {
        if (gain <= 0) return;
        var map = p.Map;
        Count(p, new JournalEvent("fameearned", gain) { Map = map });
        var kind = ctx.Source switch
        {
            FameSource.Kill when !pvp => "killmobfame",
            FameSource.Harvest => "gatherfame",
            FameSource.Fishing => "fishingfame",
            _ => null,
        };
        if (kind is not null) Count(p, new JournalEvent(kind, gain) { Map = map });
    }

    /// <summary>A kill of <paramref name="mob"/>: one killmob for the killer and each player of its fame group (party members near it).</summary>
    public static void Killed(Map map, Player killer, Mob mob, IReadOnlyList<Player> group)
    {
        foreach (var p in group.Contains(killer) ? group : [killer, .. group])
        {
            Count(p, new JournalEvent("killmob", 1) { Map = map, Mob = mob, PartyKill = p != killer, PartySize = Math.Max(1, group.Count) });
            // Stage I: the PvE sprees (killingspree where=pve; a party member's kill as an assist).
            if (Catalog(p.World) is not null) Count(p, new JournalEvent("killingspree", 0) { Map = map, Where = "pve", Spree = Spree(p.PvpState.PveKills, p.World.GameNow, p != killer) });
        }
    }

    /// <summary>A kill at <paramref name="ticks"/> added to <paramref name="kills"/>, the ones older than SpreeKeep dropped; the list.</summary>
    private static List<(long Ticks, bool Assist)> Spree(List<(long Ticks, bool Assist)> kills, long ticks, bool assist)
    {
        kills.RemoveAll(k => ticks - k.Ticks > SpreeKeep);
        kills.Add((ticks, assist));
        return kills;
    }

    /// <summary>How long a kill stays in a spree list: the longest seconds of the data's killingspree leaves (60) and some.</summary>
    private static readonly long SpreeKeep = TimeSpan.FromMinutes(5).Ticks;

    /// <summary>
    /// Stage I: a credit of <see cref="PvpGoals.OnPvpKill"/> for <paramref name="p"/> (<paramref name="assist"/>: an assist's). A death
    /// counts killplayer, a knockdown knockdownplayer and with Pvp:KnockdownIsKill killplayer too (a bot victim only with
    /// Pvp:JournalCountsBots), both the pvp spree; a duel won finishduel outcome Win.
    /// </summary>
    public static void PvpCredited(Player p, PvpKill k, bool assist)
    {
        if (Catalog(p.World) is null) return;
        var s = p.World.Goals.Pvp;
        var e = KillPlayer(p, k, assist);
        if (k.Outcome == PvpOutcome.DuelWin)
        {
            Count(p, e with { Kind = "finishduel", Outcome = "Win" });
            return;
        }
        if (!k.Victim.IsBot || s.JournalCountsBots)
        {
            if (k.Outcome == PvpOutcome.Knockdown) Count(p, e with { Kind = "knockdownplayer" });
            if (k.Outcome == PvpOutcome.Death || s.KnockdownIsKill) Count(p, e);
        }
        Count(p, e with { Kind = "killingspree", Amount = 0, Spree = Spree(p.PvpState.PvpKills, k.Ticks, assist) });
    }

    private static JournalEvent KillPlayer(Player p, PvpKill k, bool assist) => new("killplayer", 1)
    {
        Map = k.Map, KillerFlag = p.Flag, VictimFlag = k.Victim.Flag, Attackers = k.Attackers, Assist = assist, Bot = k.Victim.IsBot, Where = "pvp",
        Special = p.World.Corrupted.SpecialOf(p, k.Victim), // stageL: an invasion's two sides
    };

    /// <summary>
    /// Stage I: the death (an execute) that follows a knockdown <paramref name="p"/> was credited for, without Pvp:KnockdownIsKill: only
    /// the killplayer the knockdown did not count (knockdownplayer and the spree were counted with it).
    /// </summary>
    public static void PvpKillOnly(Player p, PvpKill k, bool assist)
    {
        if (Catalog(p.World) is not null && (!k.Victim.IsBot || p.World.Goals.Pvp.JournalCountsBots)) Count(p, KillPlayer(p, k, assist));
    }

    /// <summary>Stage I: <paramref name="fame"/> (a7m) of a PvP kill's fame for sharer <paramref name="p"/>: killplayerfame (a party member's with countpartykills / assists).</summary>
    public static void PvpFame(Player p, PvpKill k, long fame) =>
        Count(p, new JournalEvent("killplayerfame", fame)
        {
            Map = k.Map, KillerFlag = p.Flag, VictimFlag = k.Victim.Flag, Attackers = k.Attackers, PartyKill = k.Killer.Player != p, Bot = k.Victim.IsBot, Where = "pvp",
        });

    /// <summary>Stage I: a duel that ended with a result (a winner, or the time run out): finishduel without an outcome for <paramref name="p"/>.</summary>
    public static void DuelFinished(Player p, Map? map) => Count(p, new JournalEvent("finishduel", 1) { Map = map, Where = "pvp" });

    /// <summary>A harvest of <paramref name="count"/> × <paramref name="item"/> (keys 5 + 6 + 7 of HarvestFinished).</summary>
    public static void Gathered(Player p, ItemTemplate item, int count) =>
        Count(p, new JournalEvent("gather", count) { Map = p.Map, Item = item });

    /// <summary>An expedition run completed by <paramref name="p"/> in <paramref name="seconds"/> (finishexpedition: name, category, hardcore level, timelimit).</summary>
    public static void ExpeditionFinished(Player p, ExpeditionInstance i, double seconds) =>
        Count(p, new JournalEvent("finishexpedition", 1)
        {
            Map = p.Map, Expedition = i.Expedition.Name, ExpeditionCategory = i.Category.Name, Difficulty = i.Difficulty, Seconds = seconds,
        });

    /// <summary>
    /// The guild's season points (a7m raw) after a gain, or at a member's login: reachseasonpoints is set to them (all 12 such leaves are
    /// in GUILD_GUILD_SEASON; stageI guild-goals SPEC §0.7, the personal total of before was a mistake).
    /// </summary>
    public static void SeasonPoints(Player p, long total) => Count(p, new JournalEvent("reachseasonpoints", total) { Set = true, Map = p.Map });

    /// <summary>
    /// At a login, once (<see cref="GoalLog.JournalVersion"/> below 1): the reachseasonpoints progress saved before that fix (from the
    /// personal season total; a Set never lowers it) is dropped, the guild's points set it again. Achievements already done stay done.
    /// </summary>
    public static void GuildSeasonPoints(Player p)
    {
        if (p.Character.Goals is not { JournalVersion: < 1 } goals || Catalog(p.World) is not { } journal) return;
        goals.JournalVersion = 1;
        var dropped = journal.LeavesOf("reachseasonpoints").Count(leaf => goals.Journal.Remove(leaf.Key));
        p.World.Saves.MarkChanged(p.Character);
        if (dropped > 0) p.Session.Send(TrackerEvent(p.Character, journal));
    }

    /// <summary>Stage I: <paramref name="points"/> guild challenge points (a7m raw) <paramref name="p"/> gave its guild: gainchallengepoints.</summary>
    public static void ChallengePoints(Player p, long points) => Count(p, new JournalEvent("gainchallengepoints", points) { Map = p.Map });

    /// <summary>Stage I: the guild challenge level of <paramref name="p"/>'s guild (after a level-up, or at its login): reachguildchallengelevel is set to it.</summary>
    public static void GuildChallengeLevel(Player p, int level) => Count(p, new JournalEvent("reachguildchallengelevel", level) { Set = true, Map = p.Map });

    /// <summary>Stage I: a season-end member reward of <paramref name="bracket"/> given to <paramref name="p"/>: gotguildmemberreward of that minrank and the lower ones.</summary>
    public static void GuildMemberReward(Player p, string bracket) => Count(p, new JournalEvent("gotguildmemberreward", 1) { Map = p.Map, Bracket = bracket });

    /// <summary>StageL corrupted (albionjournal.xml PVP_CORRUPTEDS): the corrupted dungeon infamy after a gain: haveinfamy infamytype="CorruptedDungeon" is set to it.</summary>
    public static void InfamyChanged(Player p, long total) =>
        Count(p, new JournalEvent("haveinfamy", total) { Set = true, Map = p.Map, InfamyType = "CorruptedDungeon" });

    /// <summary>StageR lore (SPEC §5): lore snippet <paramref name="name"/> claimed by <paramref name="p"/>: claimloresnippet loresnippet=name.</summary>
    public static void LoreClaimed(Player p, string name) => Count(p, new JournalEvent("claimloresnippet", 1) { Map = p.Map, LoreSnippet = name });

    // ---- progress

    /// <summary>
    /// <paramref name="e"/> for <paramref name="p"/>: every supported leaf of its kind in an enabled category, of an achievement not done,
    /// whose conditions hold, takes the amount (up to its goal); then the tracker updates and the completions go out.
    /// </summary>
    public static void Count(Player p, JournalEvent e)
    {
        if (Catalog(p.World) is not { } journal) return;
        var goals = p.Character.Goals ??= new GoalLog();
        var touched = new List<JournalCatalog.Achievement>();
        foreach (var leaf in journal.LeavesOf(e.Kind))
        {
            var a = leaf.Achievement;
            if (goals.Done.ContainsKey(a.Name) || !Enabled(p.World, a) || !Matches(p.World, leaf, e)) continue;
            var old = goals.Journal.GetValueOrDefault(leaf.Key);
            if (old >= leaf.Goal) continue;
            // A spree is the most kills in the leaf's own window ending now, kept as a maximum (never lowered).
            var (amount, set) = e.Spree is { } spree ? (SpreeOf(leaf, spree), true) : (e.Amount, e.Set);
            var now = Math.Min(leaf.Goal, set ? Math.Max(old, amount) : old + amount);
            if (now == old) continue;
            goals.Journal[leaf.Key] = now;
            if (touched.LastOrDefault() != a && !touched.Contains(a)) touched.Add(a);
        }
        if (touched.Count == 0) return;
        p.World.Saves.MarkChanged(p.Character);
        var tracked = p.Character.JournalTracked ?? [];
        var update = new List<JournalCatalog.Achievement>();
        var completed = new List<JournalCatalog.Achievement>();
        foreach (var a in touched)
        {
            if (Done(goals, a.Root))
            {
                goals.Done[a.Name] = DateTime.UtcNow.Ticks;
                foreach (var m in a.Leaves) goals.Journal.Remove(m.Key);
                completed.Add(a);
            }
            else if (tracked.Contains(a.Name)) update.Add(a);
        }
        if (update.Count > 0)
            p.Session.Send(new JournalAchievementProgressUpdateEvent { cme = 1, cmf = [.. update.Select(a => a.Name)], cmg = [.. update.Select(a => Progress(goals, a))] });
        if (completed.Count == 0) return;
        foreach (var a in completed)
        {
            p.Session.Send(new AlbionJournalAchievementCompletedEvent { br2 = a.Name, br4 = "" });
            Log.Info("journal", $"'{p.Character.Name}' completed {a.Name} ({a.Category})");
        }
        p.Session.Send(TrackerEvent(p.Character, journal));
        p.Session.Send(new JournalClaimableRewardUpdateEvent { cmh = Claimable(p.World, p.Character, journal).Count });
    }

    /// <summary>killingspree: the kills of <paramref name="spree"/> within the leaf's seconds of the last one (assists only with countassists, default true).</summary>
    private static long SpreeOf(M leaf, IReadOnlyList<(long Ticks, bool Assist)> spree)
    {
        if (spree.Count == 0) return 0;
        var since = spree[^1].Ticks - TimeSpan.FromSeconds(leaf.IntAttr("seconds", 60)).Ticks;
        var assists = leaf.Attr("countassists") != "false";
        return spree.Count(k => k.Ticks >= since && (assists || !k.Assist));
    }

    /// <summary>Whether <paramref name="leaf"/> counts <paramref name="e"/>: its attributes, its mob list and its filters (all of them).</summary>
    public static bool Matches(World world, M leaf, JournalEvent e)
    {
        var mob = e.Mob?.Template;
        switch (leaf.Kind)
        {
            case "killmob":
                if (mob is null || (e.PartyKill && leaf.Attr("countpartykills") != "true")) return false;
                if (leaf.Mobs.Count > 0 && !leaf.Mobs.Contains(mob.Name)) return false;
                if (mob.Tier < leaf.IntAttr("mintier", 0) || mob.Tier > leaf.IntAttr("maxtier", 99)) return false;
                if (leaf.Attr("requiresfinalboss") == "true" && mob.Category != "boss") return false;
                if (leaf.Attr("faction") is { } faction && !faction.Equals(mob.Faction, StringComparison.OrdinalIgnoreCase)) return false;
                if (leaf.Attr("category") == "roaming" && !Roaming(e.Map)) return false;
                break;
            case "killmobfame":
                if (leaf.Attr("faction") is not null) return false; // the gain does not know the mob
                if (leaf.Attr("category") == "roaming" && !Roaming(e.Map)) return false;
                break;
            case "gather":
                if (e.Item is not { } item || item.Tier < leaf.IntAttr("mintier", 0) || item.Tier > leaf.IntAttr("maxtier", 99)) return false;
                if (item.Enchantment < leaf.IntAttr("minenchantmentlevel", 0) || item.Enchantment > leaf.IntAttr("maxenchantmentlevel", 99)) return false;
                break;
            case "finishexpedition":
                if (e.Expedition is not { } name) return false;
                if (leaf.Attr("name") is { } n && name != n && !name.EndsWith("_" + n, StringComparison.Ordinal)) return false;
                if (leaf.Attr("category") is { } c && c != e.ExpeditionCategory) return false;
                if (leaf.Attr("type") == "hardcore" && e.Difficulty <= 0) return false;
                if (leaf.Attr("mindifficulty") is not null && (e.Difficulty < leaf.IntAttr("mindifficulty", 0) || e.Difficulty > leaf.IntAttr("maxdifficulty", 99))) return false;
                if (leaf.Attr("timelimit") is not null && e.Seconds > leaf.IntAttr("timelimit", 0)) return false;
                break;
            case "killplayer" or "knockdownplayer":
                if (e.Assist && leaf.Attr("assists") != "AlsoCountAssists") return false;
                if (leaf.Attr("specialcondition") is { } special && special != e.Special) return false; // stageL corrupted
                if (leaf.Attr("where") is { } where && where != e.Where) return false;
                if (leaf.Attr("clustertype") is { } type && !FileTypeIs(world, e.Map, type)) return false;
                break;
            case "killplayerfame":
                if (e.PartyKill && leaf.Attr("countpartykills") != "true" && leaf.Attr("assists") != "AlsoCountAssists") return false;
                break;
            case "killingspree" or "die":
                if ((leaf.Attr("where") ?? "pvp") != e.Where) return false;
                break;
            case "finishduel":
                if (!string.Equals(leaf.Attr("outcome"), e.Outcome, StringComparison.OrdinalIgnoreCase)) return false;
                break;
            case "gotguildmemberreward":
                if (SeasonBracket.RankOf(leaf.Attr("minrank")) is var min && (min < 0 || SeasonBracket.RankOf(e.Bracket) < min)) return false;
                break;
            case "haveinfamy":
                if (!string.Equals(leaf.Attr("infamytype"), e.InfamyType, StringComparison.OrdinalIgnoreCase)) return false;
                break;
            case "claimloresnippet":
                if (!string.Equals(leaf.Attr("loresnippet"), e.LoreSnippet, StringComparison.OrdinalIgnoreCase)) return false;
                break;
        }
        return leaf.Filters.All(f => Holds(world, f, e));
    }

    /// <summary>An open-world kill (killmob category="roaming"): not inside a random dungeon or an expedition.</summary>
    private static bool Roaming(Map? map) => map is not null && !DungeonLayout.IsInstance(map.Cluster) && !ExpeditionData.IsInstance(map.Cluster) && !CorruptedData.IsInstance(map.Cluster) && !HellgateData.IsInstance(map.Cluster);

    /// <summary>A filter on the event (SPEC §B.5); an unknown one is false.</summary>
    public static bool Holds(World world, JournalCatalog.Filter f, JournalEvent e)
    {
        bool Compare(string? actual) => actual is not null && (f.Op == "contains"
            ? actual.Contains(f.Value, StringComparison.OrdinalIgnoreCase) : actual.Equals(f.Value, StringComparison.OrdinalIgnoreCase));
        return f.Kind switch
        {
            "not" => !f.Children.All(c => Holds(world, c, e)),
            "and" => f.Children.All(c => Holds(world, c, e)),
            "or" => f.Children.Any(c => Holds(world, c, e)),
            "IfClusterArcheType" => Compare(ArchetypeOf(world, e.Map)),
            "IfClusterType" => Compare(ClusterTypeOf(world, e.Map)),
            "IfClusterId" => Compare(e.Map?.Cluster),
            "IfTargetMobName" => Compare(e.Mob?.Template.Name),
            "IfInRandomDungeon" => e.Map is { } m && world.Dungeons.Find(m.Cluster) is { } d && (f.Value.Length == 0 || d.Group.Plan.Generator.Name.Equals(f.Value, StringComparison.OrdinalIgnoreCase)),
            "IfPartySize" => CompareInt(f, e.PartySize),
            // Stage I PvP: both flags factions and not the same; the cluster file's type token; the victim's attackers.
            "IfTargetIsInOpposingFaction" => Pvp.IsFaction(e.KillerFlag) && Pvp.IsFaction(e.VictimFlag) && e.KillerFlag != e.VictimFlag,
            "IfClusterTypeFromFileName" => FileTypeIs(world, e.Map, f.Value),
            "IfCombatHistoryAttackers" => CompareInt(f, e.Attackers),
            _ => false,
        };
    }

    /// <summary><paramref name="actual"/> against the filter's op (greaterOrEqual, lessOrEqual, greater, less, else equals) and integer value.</summary>
    private static bool CompareInt(JournalCatalog.Filter f, int actual) =>
        int.TryParse(f.Value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var n) && f.Op switch
        {
            "greaterOrEqual" => actual >= n, "lessOrEqual" => actual <= n, "greater" => actual > n, "less" => actual < n, _ => actual == n,
        };

    /// <summary>
    /// IfClusterTypeFromFileName / a PvP leaf's clustertype (pvp-goals SPEC §1.2 [inference]): the type token of the map's world.xml cluster
    /// file, "0201_WRL_…" World, "…_DNG_…" Dungeon (the static dungeons); a random dungeon or expedition instance neither; any other
    /// value (Hellgate, DragonArea, AvalonianRoads … zones this server does not have) false.
    /// </summary>
    private static bool FileTypeIs(World world, Map? map, string type)
    {
        if (map is null || world.Dungeons.Find(map.Cluster) is not null || DungeonLayout.IsInstance(map.Cluster) || ExpeditionData.IsInstance(map.Cluster)
            || CorruptedData.IsInstance(map.Cluster) || HellgateData.IsInstance(map.Cluster)) return false;
        var parts = map.Template?.Info.File.Split('_');
        var token = parts is { Length: > 1 } ? parts[1] : null;
        return type switch { "World" => token == "WRL", "Dungeon" => token == "DNG", _ => false };
    }

    /// <summary>The world.xml cluster type of the map (a random dungeon's: its group's).</summary>
    private static string? ClusterTypeOf(World world, Map? map) =>
        map is null ? null : world.Dungeons.Find(map.Cluster) is { } d ? d.Group.ClusterType : map.Template?.Info.Type;

    /// <summary>The worldsettings.xml archetype of the map's cluster type (GREEN, YELLOW, RED, BLACK …); null when unknown.</summary>
    private static string? ArchetypeOf(World world, Map? map)
    {
        if (ClusterTypeOf(world, map) is not { Length: > 0 } type || world.Clusters is not { } store) return null;
        try { return store.Archetypes.Of(type).Archetype; }
        catch (Exception x) when (x is InvalidDataException or IOException) { return null; }
    }

    // ---- state

    /// <summary>A mission's value in the client's units (whole fame / points; counts as they are), capped at its goal.</summary>
    private static long Value(GoalLog g, M m) => m.Kind switch
    {
        "all" or "and" => m.Children.Sum(c => Value(g, c)),
        "any" => Math.Min(m.Goal, m.Children.Count(c => Done(g, c))),
        "progresssum" => Math.Min(m.Goal, m.Children.Sum(c => Value(g, c))),
        "or" => m.Children.Select(c => Value(g, c)).DefaultIfEmpty(0).Max(),
        _ => Math.Min(m.Goal, g.Journal.GetValueOrDefault(m.Key)) / (m.Raw ? 10000 : 1),
    };

    /// <summary>Whether a mission is complete.</summary>
    private static bool Done(GoalLog g, M m) => m.Kind switch
    {
        "all" or "and" => m.Children.Count > 0 && m.Children.All(c => Done(g, c)),
        "any" => m.Children.Count(c => Done(g, c)) >= m.Goal,
        "progresssum" => m.Supported && m.Children.Sum(c => Value(g, c)) >= m.Goal,
        "or" => m.Children.Any(c => Done(g, c)),
        _ => m.Supported && g.Journal.GetValueOrDefault(m.Key) >= m.Goal,
    };

    /// <summary>An achievement's progress as the client shows it: its maximum when done, else its mission tree's value (below the maximum).</summary>
    public static long Progress(GoalLog? g, JournalCatalog.Achievement a) =>
        g is null ? 0 : g.Done.ContainsKey(a.Name) ? a.Max : Math.Min(a.Max - 1, Value(g, a.Root));

    /// <summary>The tracked list's progress, parallel to <paramref name="names"/> (0 for unknown names).</summary>
    public static long[] ProgressOf(World world, Character c, IReadOnlyList<string> names) =>
        Catalog(world) is { } journal ? [.. names.Select(n => journal.Find(n) is { } a ? Progress(c.Goals, a) : 0)] : new long[names.Count];

    /// <summary>JournalAchievementProgressUpdate kind 0: the whole tracked list with its progress.</summary>
    public static JournalAchievementProgressUpdateEvent TrackerEvent(Character c, JournalCatalog journal)
    {
        var names = c.JournalTracked ?? [];
        return new JournalAchievementProgressUpdateEvent { cme = 0, cmf = [.. names], cmg = [.. names.Select(n => journal.Find(n) is { } a ? Progress(c.Goals, a) : 0)] };
    }

    /// <summary>Whether a category reward is open: the share of its category's achievements done.</summary>
    private static bool Unlocked(GoalLog g, JournalCatalog journal, JournalCatalog.CategoryReward r) =>
        journal.CategorySizes.GetValueOrDefault(r.Category) is var total and > 0
        && g.Done.Keys.Count(n => journal.Find(n)?.Category == r.Category) >= r.Share * total - 1e-9;

    /// <summary>The ids the character may claim now (§B.4): done achievements with a reward the server can make, and open category rewards, not claimed.</summary>
    public static List<string> Claimable(World world, Character c, JournalCatalog journal)
    {
        if (c.Goals is not { } g) return [];
        bool Makeable(string? item) => item is not null && world.Items.Find(item) is { CanCreate: true };
        var list = g.Done.Keys.Where(n => !g.Claimed.Contains(n) && Makeable(journal.Find(n)?.RewardItem)).ToList();
        list.AddRange(journal.CategoryRewards.Where(r => !g.Claimed.Contains(r.Id) && Makeable(r.Item) && Unlocked(g, journal, r)).Select(r => r.Id));
        return list;
    }

    /// <summary>
    /// GetCharacterSteamAchievements (op 144, §B.2) for <paramref name="c"/>: 0 the name, 1 the done achievements, 2/3 those in progress
    /// with their progress (above 0, below the maximum), 4 the done sub-conditions of those in progress, 5 the claimed reward ids.
    /// </summary>
    public static GetCharacterSteamAchievementsResponse Achievements(World world, Character? c, string name)
    {
        var answer = new GetCharacterSteamAchievementsResponse { ast = name };
        if (c?.Goals is not { } g || Catalog(world) is not { } journal) return answer.FillEmpty();
        var going = g.Journal.Keys.Select(k => journal.Find(k[..Math.Max(0, k.LastIndexOf('_'))])).OfType<JournalCatalog.Achievement>()
            .Distinct().Where(a => !g.Done.ContainsKey(a.Name)).Select(a => (a, Progress(g, a))).Where(x => x.Item2 > 0).ToList();
        answer.asu = [.. g.Done.Keys];
        answer.asv = [.. going.Select(x => x.a.Name)];
        answer.asw = [.. going.Select(x => x.Item2)];
        answer.asx = [.. going.SelectMany(x => Walk(x.a.Root).Where(m => m.Index > 0 && Done(g, m)).Select(m => x.a.SubKey(m)))];
        answer.asy = [.. g.Claimed];
        return answer.FillEmpty();
    }

    private static IEnumerable<M> Walk(M m) => m.Children.SelectMany(Walk).Prepend(m);

    /// <summary>
    /// ClaimAlbionJournalReward (op 514, §B.6): per id unknown 20563, claimed 20561, not done / category share not reached 20562, an item
    /// the server cannot make 20563 (not marked claimed), not fitting 20564; else the item × amount into the inventory, claimed, and 601
    /// with the new count. Returns the results, parallel to <paramref name="ids"/>.
    /// </summary>
    public static short[] Claim(Player p, IReadOnlyList<string> ids)
    {
        var world = p.World;
        var results = new short[ids.Count];
        if (Catalog(world) is not { } journal)
        {
            Array.Fill(results, InvalidReward);
            return results;
        }
        var g = p.Character.Goals ??= new GoalLog();
        for (var i = 0; i < ids.Count; i++)
        {
            var id = ids[i];
            string? item;
            int amount;
            bool open;
            if (journal.Find(id) is { } a) (item, amount, open) = (a.RewardItem, a.RewardAmount, g.Done.ContainsKey(id));
            else if (journal.CategoryRewards.FirstOrDefault(r => r.Id == id) is { } r) (item, amount, open) = (r.Item, 1, Unlocked(g, journal, r));
            else
            {
                results[i] = InvalidReward;
                continue;
            }
            if (g.Claimed.Contains(id)) results[i] = AlreadyClaimed;
            else if (!open) results[i] = NotClaimableYet;
            else if (item is null || world.Items.Find(item) is not { CanCreate: true } type) results[i] = InvalidReward;
            else if (!Network.WorldSession.Fits(p.Inventory.Bag, [(type, amount)])) results[i] = NoInventorySpace;
            else
            {
                p.Session.GiveItem(p, type, amount);
                g.Claimed.Add(id);
                world.Saves.MarkChanged(p.Character);
                p.Session.Send(new JournalClaimableRewardUpdateEvent { cmh = Claimable(world, p.Character, journal).Count });
                Log.Info("journal", $"'{p.Character.Name}' claimed {id}: {type.Name} x{amount}");
                GoldRewards.JournalClaimed(p, id); // stageR gold (not official, Gold:Rewards): a little gold, a weekly cap
            }
        }
        return results;
    }
}
