using System.Globalization;
using System.Xml.Linq;
using AlbionLocal.Server.Data;

namespace AlbionLocal.Server.Game;

/// <summary>Where a fame gain comes from (analysis/work/progression/PROGRESSION.md §2.2): which mission types it feeds.</summary>
public enum FameSource
{
    /// <summary>A PvE mob kill: fameearned + killmobfame nodes of the equipped gear (UpdateFame keys 3, 5, 15).</summary>
    Kill,

    /// <summary>A harvest: fameearned + gatherfame nodes of the resource (UpdateFame key 5, no 15).</summary>
    Harvest,

    /// <summary>A craft or refine at a station: fameearned + craftitemfame nodes of the crafted item (UpdateFame key 5, no 15; not captured).</summary>
    Craft,

    /// <summary>
    /// A Tome of Insight used from the inventory (adddestinyboardpvefame, stageD consumables SPEC §4.3; PROGRESSION.md §2.2): the
    /// nodes of a kill by the equipped gear, without the mob tier gate (UpdateFame keys 7 and 15, no 5; the total fame stays).
    /// </summary>
    Tome,

    /// <summary>A catch (stageG fishing SPEC §9): fameearned + fishingfame nodes of the fish (GATHER_FISH, GATHER_FISH_FISH; UpdateFame key 5, no 15).</summary>
    Fishing,

    /// <summary>A crop harvested on an island (stageH islands SPEC §3.5): fameearned + farmharvestfame nodes of the seed (FARM_CROPS by T?_FARM_CARROT_SEED …; UpdateFame key 5, no 15).</summary>
    Farm,
}

/// <summary>
/// What a fame gain is about: the mob's or the resource's tier (MissionTargetMin/MaxTier) and the items it is checked
/// against (killmobfame: the equipped main hand, off hand, head, armor and shoes; gatherfame: the resource; craftitemfame:
/// the crafted item, whose tier is the target tier too), as (base
/// name without @N, tier). <see cref="Only"/> = every gain goes to that one node (the GM's .fame add &lt;amount&gt; &lt;node&gt;).
/// </summary>
public sealed record FameContext(FameSource Source, int TargetTier, IReadOnlyList<(string BaseName, int Tier)> Items, int? Only = null);

/// <summary>
/// One level (row) of a template achievement: the fame it needs (a7m, ceil(Fame × famemultiplier)) and the tier gates of
/// a gain while the node is at this level (PROGRESSION.md §1.2; elite rows and plain nodes have none: 0..99); completing
/// it unlocks <see cref="UnlockTier"/> (0 = nothing) for the node's itemuse patterns.
/// </summary>
public readonly record struct BoardLevel(long Fame, int TargetMin, int TargetMax, int ItemMin, int ItemMax, int UnlockTier)
{
    /// <summary>The learning points this level costs with auto-learn, a7m (§1.2: ceil(row.LP × lpmultiplier) whole points; elite rows and plain nodes 0).</summary>
    public long Lp { get; init; }
}

/// <summary>One destiny board node (id = <see cref="ClientNumbering.Achievements"/> order) with what the fame rules need.</summary>
public sealed class BoardNode
{
    public required int Id { get; init; }
    public required string Name { get; init; }
    public required bool IsTemplate { get; init; }

    /// <summary>Authored tracking="forced" nodes cannot be toggled by TrackAchievements.</summary>
    public bool ForcedTracking { get; init; }

    /// <summary>The mission type: fameearned, killmobfame, gatherfame, craftitemfame, farmharvestfame, … (templates: missiontype).</summary>
    public required string Mission { get; init; }

    /// <summary>
    /// The items a gain must match: a template's &lt;itemlist&gt; patterns (T?_ARMOR_CLOTH_SET1, T?_FIBER*), a plain node's
    /// &lt;equipitem&gt;/&lt;validitem&gt; ids (matched as given, PROGRESSION.md §8 question 2); empty = any.
    /// </summary>
    public required string[] Items { get; init; }

    /// <summary>The graph neighbours (&lt;parentachievements&gt;, allrequired is always false), by id.</summary>
    public int[] Neighbours { get; set; } = [];

    /// <summary>Plain: one level (the mission value, gates from its mintier); template: base rows, then elite rows.</summary>
    public required BoardLevel[] Levels { get; init; }

    /// <summary>The template's base level count (100); a plain node's 1. Levels beyond it are elite (COMBAT_SPEC, COMBAT_OFF_SPEC).</summary>
    public required int BaseLevels { get; init; }

    /// <summary>A plain node's itemuse reward entries (unlocked when it is finished).</summary>
    public string[] ItemUse { get; init; } = [];

    /// <summary>A template's &lt;baserewards&gt;&lt;unlock type="itemuse"&gt; patterns (unlocked per level by its UnlockTier).</summary>
    public string[] ItemUsePatterns { get; init; } = [];

    /// <summary>&lt;overridemasterylevel level="n"&gt;'s own itemuse patterns, by row (GATHER_*: the tool and gatherer head at row 0).</summary>
    public IReadOnlyDictionary<int, string[]> ItemUseAt { get; init; } = new Dictionary<int, string[]>();

    public override string ToString() => $"{Name} ({Id})";
}

/// <summary>
/// The destiny board's fame rules (analysis/work/progression/PROGRESSION.md, every formula replayed against the captured
/// bursts there): which in-progress nodes a gain feeds (§2), how a node takes it level by level (§3, and §4's auto-learn split
/// with learning points, stageJ focus-lp SPEC D6), what a level-up makes available (§2.1, §5.3), and the
/// item-use unlocks that the client rebuilds from FullAchievementInfo (NOTES-protocol "Destiny board unlocks"), so that the
/// server can tell what a character may equip. Tables from achievements.xml (<see cref="FromClient"/>); immutable but for
/// the unlock cache.
/// </summary>
public sealed class DestinyBoard
{
    /// <summary>a7m fixed point: 10000 = 1 (PROGRESSION.md: every value is a7m, Int64 scale 10000).</summary>
    public const long A7m = 10000;

    /// <summary>The equipment slots whose items killmobfame item lists use (MainHand, OffHand, Head, Armor, Shoes; §2.2).</summary>
    public static readonly int[] KillSlots = [0, 1, 2, 3, 4];

    private readonly Dictionary<string, int> _byName = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<int, List<string>> _namesByTier = [];
    private readonly Dictionary<(int Node, int Row), string[]> _rowUnlocks = [];

    private DestinyBoard(BoardNode[] nodes, IEnumerable<(string BaseName, int Tier)> items)
    {
        Nodes = nodes;
        foreach (var n in nodes) _byName.TryAdd(n.Name, n.Id);
        foreach (var (name, tier) in items.Distinct())
        {
            if (!_namesByTier.TryGetValue(tier, out var list)) _namesByTier[tier] = list = [];
            list.Add(name);
        }
        StartId = _byName.GetValueOrDefault("START", -1);
    }

    /// <summary>Indexed by achievement id.</summary>
    public IReadOnlyList<BoardNode> Nodes { get; }

    /// <summary>START (693 in 345319, tracking="forced"): the root, in progress for a new character; -1 when missing.</summary>
    public int StartId { get; }

    /// <summary>The node of an achievements.xml id (any case) or of a number; null = none.</summary>
    public BoardNode? Find(string nameOrId) =>
        _byName.TryGetValue(nameOrId, out var id) ? Nodes[id]
        : int.TryParse(nameOrId, NumberStyles.Integer, CultureInfo.InvariantCulture, out var n) && n >= 0 && n < Nodes.Count ? Nodes[n] : null;

    /// <summary>Levels of a node: 1 for a plain node, base + elite rows for a template.</summary>
    public int LevelCount(int id) => Nodes[id].Levels.Length;

    /// <summary>a7m fame a node needs while at <paramref name="level"/> (§1.2: ceil(row.Fame × famemultiplier); plain: the mission value).</summary>
    public long LevelFame(int id, int level) => Nodes[id].Levels[level].Fame;

    /// <summary>a7m learning points a node's <paramref name="level"/> costs with auto-learn (§1.2: ceil(row.LP × lpmultiplier); 0 = auto-learn cannot buy it).</summary>
    public long LevelLp(int id, int level) => Nodes[id].Levels[level].Lp;

    // ---- the tables ----

    /// <summary>
    /// The tables from achievements.xml's top-level elements (in file order) and the item table (for the itemuse
    /// patterns): per node its kind, mission type and value, item list, neighbours, template rows with ceil(Fame × FM)
    /// (a7m mul then ceil, as the client's a7m maths, §1), the tier columns by the template's structure attribute, the
    /// elite rows, and the itemuse rewards. A number that does not parse reads as 0 (the row then needs no fame).
    /// </summary>
    public static DestinyBoard FromClient(IEnumerable<XElement> achievements, ItemStore items)
    {
        static string? Attr(XElement e, string name) => e.Attribute(name)?.Value;
        static double Num(string? s) => double.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out var d) ? d : 0;
        static long ToA7m(double v) => (long)Math.Round(v * A7m);
        static long Ceil(long a7m) => (a7m + A7m - 1) / A7m * A7m;
        var top = achievements.ToList();
        var templates = new Dictionary<string, XElement>();
        foreach (var t in top)
            if (t.Name.LocalName == "template") templates[Attr(t, "name") ?? ""] = t;

        static List<string[]> Rows(XElement? e) => e is null ? [] :
            [.. string.Concat(e.Nodes().OfType<XText>().Select(t => t.Value)).Split('\n')
                .Select(l => l.Trim()).Where(l => l.Length > 0).Select(l => l.Split(';'))];

        var nodes = new List<BoardNode>();
        var neighbours = new List<string[]>();
        foreach (var (id, el) in ClientNumbering.Achievements(top))
        {
            var name = Attr(el, "id") ?? "";
            neighbours.Add([.. (el.Element("parentachievements")?.Elements("achievement") ?? []).Select(a => Attr(a, "id") ?? "")]);
            if (el.Name.LocalName == "achievement")
            {
                var mission = el.Descendants("mission").FirstOrDefault();
                var min = mission is null ? 0 : (int)Num(Attr(mission, "mintier"));
                nodes.Add(new BoardNode
                {
                    Id = id, Name = name, IsTemplate = false, Mission = mission is null ? "" : Attr(mission, "type") ?? "",
                    ForcedTracking = Attr(el, "tracking") == "forced",
                    Items = mission is null ? [] : [.. mission.Elements().Where(c => c.Name.LocalName is "equipitem" or "validitem").Select(c => Attr(c, "id") ?? "")],
                    Levels = [new BoardLevel(ToA7m(Num(mission is null ? null : Attr(mission, "value"))), min, 99, 0, 99, 0)],
                    BaseLevels = 1,
                    ItemUse = [.. el.Descendants("reward").Where(r => Attr(r, "type") == "itemuse").Elements("entry").Select(e => Attr(e, "id") ?? "")],
                });
                continue;
            }
            var template = templates.GetValueOrDefault(Attr(el, "usetemplate") ?? "");
            var fm = ToA7m(Attr(el, "famemultiplier") is { } f ? Num(f) : 1);
            var lm = ToA7m(Attr(el, "lpmultiplier") is { } lpm ? Num(lpm) : 1);
            var levels = new List<BoardLevel>();
            var baseRows = template?.Element("baselevels");
            var columns = (Attr(baseRows ?? new XElement("x"), "structure") ?? "").Split(';');
            int Col(string column) => Array.IndexOf(columns, column);
            int fame = Col("Fame"), lp = Col("LP"), tmin = Col("MissionTargetMinTier"), tmax = Col("MissionTargetMaxTier"),
                imin = Col("MissionItemMinTier"), imax = Col("MissionItemMaxTier"), unlock = Col("UnlockTier");
            int Cell(string[] row, int col, int fallback) => col >= 0 && col < row.Length && row[col].Length > 0 ? (int)Num(row[col]) : fallback;
            long Times(string[] row, int col, long factor) => Ceil(ToA7m(Num(col >= 0 && col < row.Length ? row[col] : null)) * factor / A7m);
            long Fame(string[] row, int col) => Times(row, col, fm);
            foreach (var row in Rows(baseRows))
                levels.Add(new BoardLevel(Fame(row, fame), Cell(row, tmin, 0), Cell(row, tmax, 99), Cell(row, imin, 0), Cell(row, imax, 99), Cell(row, unlock, 0))
                    { Lp = lp >= 0 ? Times(row, lp, lm) : 0 });
            var baseCount = levels.Count;
            var elite = template?.Element("elitelevels");
            var eliteFame = Array.IndexOf((Attr(elite ?? new XElement("x"), "structure") ?? "").Split(';'), "Fame");
            foreach (var row in Rows(elite)) levels.Add(new BoardLevel(Fame(row, eliteFame), 0, 99, 0, 99, 0));
            static string[] Patterns(XElement? rewards) =>
                [.. (rewards?.Elements("unlock") ?? []).Where(u => Attr(u, "type") == "itemuse").Elements("itempattern").Select(p => Attr(p, "pattern") ?? "")];
            nodes.Add(new BoardNode
            {
                Id = id, Name = name, IsTemplate = true, Mission = Attr(el, "missiontype") ?? "",
                ForcedTracking = Attr(el, "tracking") == "forced",
                Items = [.. (el.Element("itemlist")?.Elements("itempattern") ?? []).Select(p => Attr(p, "pattern") ?? "")],
                Levels = [.. levels], BaseLevels = baseCount,
                ItemUsePatterns = Patterns(el.Element("baserewards")),
                ItemUseAt = el.Elements("overridemasterylevel")
                    .Select(o => (Row: (int)Num(Attr(o, "level")), Patterns: Patterns(o.Element("baserewards"))))
                    .Where(o => o.Patterns.Length > 0).ToDictionary(o => o.Row, o => o.Patterns),
            });
        }
        var board = new DestinyBoard([.. nodes], items.All.Select(t => (BaseName(t.Name), t.Tier)));
        for (var i = 0; i < nodes.Count; i++)
            nodes[i].Neighbours = [.. neighbours[i].Select(n => board._byName.GetValueOrDefault(n, -1)).Where(n => n >= 0).Distinct()];
        return board;
    }

    /// <summary>A uniquename without its @N enchantment suffix (what the patterns and the ItemUse set use).</summary>
    public static string BaseName(string name) => name.IndexOf('@') is var i and >= 0 ? name[..i] : name;

    // ---- which nodes a gain feeds (§2) ----

    /// <summary>Whether <paramref name="id"/> takes fame now: in progress (FullAchievementInfo key 2, <see cref="Progression.Levels"/>), below its level count, not finished.</summary>
    public bool InProgress(Progression p, int id) =>
        id >= 0 && id < Nodes.Count && p.Levels.TryGetValue(id, out var level) && level < LevelCount(id) && !p.Completed.Contains(id);

    /// <summary>
    /// The nodes a gain feeds, ascending (§2.2): in progress, and fameearned for every source, killmobfame for a kill and
    /// gatherfame for a harvest whose item list matches one of the context's items and whose current row's tier gates
    /// pass (target: the mob's or resource's tier; item: the matching item's), craftitemfame for a craft by the crafted item
    /// (CRAFT_SWORDS by T?_MAIN_SWORD, CRAFT_REFINE_WOOD_T4 by T?_PLANKS*, the plain CRAFT_TRAINEE by its T2 validitems).
    /// Each gets the full amount.
    /// </summary>
    public List<int> Targets(Progression p, FameContext ctx)
    {
        if (ctx.Only is { } only) return InProgress(p, only) ? [only] : [];
        var list = new List<int>();
        foreach (var (id, level) in p.Levels)
        {
            if (!InProgress(p, id)) continue;
            var node = Nodes[id];
            var mission = ctx.Source switch { FameSource.Kill or FameSource.Tome => "killmobfame", FameSource.Craft => "craftitemfame", FameSource.Fishing => "fishingfame", FameSource.Farm => "farmharvestfame", _ => "gatherfame" };
            if (node.Mission == "fameearned") { list.Add(id); continue; }
            if (node.Mission != mission) continue;
            var row = node.Levels[level];
            if (ctx.Source != FameSource.Tome && (ctx.TargetTier < row.TargetMin || ctx.TargetTier > row.TargetMax)) continue; // a tome: no mob
            if (node.Items.Length == 0
                || ctx.Items.Any(i => i.Tier >= row.ItemMin && i.Tier <= row.ItemMax && node.Items.Any(pattern => Matches(node, pattern, i.BaseName))))
                list.Add(id);
        }
        return list;
    }

    /// <summary>A template's patterns match as globs ('?' one character, '*' any run); a plain node's ids as given.</summary>
    private static bool Matches(BoardNode node, string pattern, string baseName) =>
        node.IsTemplate ? GameData.Glob(pattern, baseName) : pattern == baseName;

    // ---- applying it (§3) ----

    /// <summary>
    /// One event of a gain: FinishedAchievement (151) of level <see cref="Level"/> just completed, or AchievementProgressInfo
    /// (152) of the node at <see cref="Level"/> with <see cref="Value"/> of <see cref="Need"/> (a7m).
    /// </summary>
    public readonly record struct Step(int Node, bool Finished, int Level, long Value, long Need)
    {
        /// <summary>Key 3 of 152: v / levelFame.</summary>
        public float Fraction => Need > 0 ? (float)((double)Value / Need) : 0;

        /// <summary>Key 4 of 152: "[[v]]", v as a7m raw (one inner array per mission).</summary>
        public string Json => string.Create(CultureInfo.InvariantCulture, $"[[{Value}]]");
    }

    /// <summary>
    /// The learning points a gain may spend (§4, stageJ focus-lp SPEC D6): the auto-learn nodes (<see cref="Nodes"/>, extended by the
    /// neighbours an auto-learn node makes available, as the client's cs6.k_t1b88 inherits the flag), the points left (a7m), the fame factor
    /// (learningpointfamefactor 5 as a7m, A5), and what the gain did: the points spent, the extra progress bought (Σ gain − used,
    /// UpdateFame keys 12/13) and the first node that spent any (the 83 goes right before its events, §6.6).
    /// </summary>
    public sealed class LearningBudget(ISet<int> nodes, long points, long factor = 5 * A7m)
    {
        public ISet<int> Nodes { get; } = nodes;
        public long Points { get; set; } = points;
        public long Factor { get; } = factor;
        public long Spent { get; set; }
        public long Extra { get; set; }
        public int? First { get; set; }
    }

    /// <summary>a7m division as the client's (b74.u): a / b as a7m, truncated.</summary>
    private static long Div(long a, long b) => b == 0 ? 0 : (long)((Int128)a * A7m / b);

    /// <summary>a7m product as the client's: a × b as a7m, truncated.</summary>
    private static long Mul(long a, long b) => (long)((Int128)a * b / A7m);

    /// <summary>
    /// The auto-learn split (b74.u 0x1808222B0, §4, all a7m): of <paramref name="fame"/> into a node at <paramref name="progress"/> of
    /// <paramref name="levelFame"/>, with <paramref name="points"/> learning points and the level's price <paramref name="levelLp"/>, how much
    /// fame is used, how much progress it gives and the points it takes. Without points (or price) the plain rule: gain = used = min(rest, fame).
    /// GATHER_FIBER_T4 at 15012.5 of 30000 (35 LP) and 6.75 fame: used 6.75, gain 33.75, LP 0.0393.
    /// </summary>
    public static (long Used, long Gain, long Lp) Split(long fame, long progress, long points, long levelFame, long levelLp, long factor)
    {
        var d = levelFame - progress;
        long used = Math.Min(d, fame), gain = used, lp = 0;
        if (points > 0 && fame > 0 && d > 0 && levelLp > 0)
        {
            gain = d;
            used = Div(d, factor);
            if (fame < used) { used = fame; gain = Mul(fame, factor); }
            var x = Div(levelFame, levelLp); // fame per learning point at this level
            lp = Div(gain, x);
            if (points < lp) { lp = points; gain = Mul(points, x); used = Div(gain, factor); }
            var rest = fame - used;
            if (rest > 0 && gain < d)
            {
                var m = Math.Min(d - gain, rest);
                used += m;
                gain += m;
            }
        }
        return (Math.Max(0, used), Math.Max(0, gain), lp);
    }

    /// <summary>
    /// Adds <paramref name="amount"/> (a7m) to every node of <see cref="Targets"/>, ascending, by §3: per node its level's
    /// rest is filled, each completed level gives a Finished step (with the level just completed), a node leaving level 0
    /// makes its neighbours available (at level 0, after the targets were chosen: they get the next gain), a plain node or
    /// a template past its last row is finished (the rest is lost), else the node's Progress step ends its events.
    /// A finished plain node moves to <see cref="Progression.Completed"/>; a template at its last level stays in
    /// <see cref="Progression.Levels"/> at its level count (as <see cref="Progression.UnlockAll"/> keeps them; the client
    /// moves it to its finished set on the 151 itself). With <paramref name="lp"/>, an auto-learn node takes its fame by
    /// <see cref="Split"/> (§4) while points are left, the nodes sharing them in ascending id [inference, PROGRESSION Q4]. Returns the
    /// steps in send order.
    /// </summary>
    public List<Step> Apply(Progression p, long amount, FameContext ctx, LearningBudget? lp = null)
    {
        var steps = new List<Step>();
        if (amount <= 0) return steps;
        foreach (var id in Targets(p, ctx))
        {
            var level = p.Levels[id];
            var v = p.Progress.GetValueOrDefault(id);
            var left = amount;
            var auto = lp is not null && lp.Nodes.Contains(id);
            while (true)
            {
                var need = LevelFame(id, level);
                var (used, gain, points) = Split(left, v, auto ? lp!.Points : 0, need, LevelLp(id, level), lp?.Factor ?? 5 * A7m);
                left -= used;
                v += gain;
                if (points > 0)
                {
                    lp!.Points -= points;
                    lp.Spent += points;
                    lp.Extra += gain - used;
                    lp.First ??= id;
                }
                if (v < need) break;
                steps.Add(new Step(id, true, level, 0, need));
                if (level == 0)
                {
                    var before = auto ? new HashSet<int>(p.Levels.Keys) : null;
                    MakeNeighboursAvailable(p, id, counted: true);
                    if (before is not null)
                        foreach (var n in p.Levels.Keys.Where(n => !before.Contains(n))) lp!.Nodes.Add(n);
                }
                level++;
                v = 0;
                if (level >= LevelCount(id)) break;
            }
            p.Progress.Remove(id);
            if (level >= LevelCount(id) && !Nodes[id].IsTemplate)
            {
                p.Levels.Remove(id);
                p.Complete(id);
                continue;
            }
            p.Levels[id] = level;
            if (level >= LevelCount(id)) continue;
            if (v > 0) p.Progress[id] = v;
            steps.Add(new Step(id, false, level, v, LevelFame(id, level)));
        }
        return steps;
    }

    /// <summary>
    /// The nodes next to <paramref name="id"/> (either side of a &lt;parentachievements&gt; entry) that become available now
    /// that it has a level or is finished (§2.1: not finished, not yet in progress, a neighbour finished or at level 1+): in
    /// progress at level 0. <paramref name="counted"/>: <paramref name="id"/> is just leaving level 0 (its new level or its
    /// finish is stored after this), so it counts already.
    /// </summary>
    public void MakeNeighboursAvailable(Progression p, int id, bool counted = false)
    {
        bool Counts(int n) => (counted && n == id) || p.Completed.Contains(n) || (p.Levels.TryGetValue(n, out var l) && l >= 1);
        var candidates = Nodes[id].Neighbours.Concat(Nodes.Where(n => n.Neighbours.Contains(id)).Select(n => n.Id)).Distinct();
        foreach (var n in candidates)
            if (n != id && !p.Completed.Contains(n) && !p.Levels.ContainsKey(n) && Nodes[n].Neighbours.Concat([id]).Any(Counts))
                p.Levels[n] = 0;
    }

    /// <summary>
    /// Sets a node to <paramref name="level"/> (the GM's .progress set): a plain node is finished at 1 and in progress at 0;
    /// a template takes 0..its level count. Its progress inside the level is dropped; at 1+ (or finished) its neighbours
    /// become available. Lowering a level does not take neighbours back (the client never loses them either).
    /// </summary>
    public void SetLevel(Progression p, int id, int level)
    {
        var node = Nodes[id];
        p.Progress.Remove(id);
        if (!node.IsTemplate && level >= 1)
        {
            p.Levels.Remove(id);
            p.Complete(id);
        }
        else
        {
            p.Completed.Remove(id);
            p.Levels[id] = Math.Clamp(level, 0, LevelCount(id));
        }
        if (level >= 1) MakeNeighboursAvailable(p, id);
    }

    /// <summary>
    /// An honest start (PROGRESSION.md §7.1): nothing finished, START in progress at level 0, so that only items.xml
    /// unlockedtoequip items (T1 and a few specials) may be equipped until START's 45 fame unlock T2 bags and capes and
    /// COMBAT_TRAINEE's 250 kill fame the T2 weapons and armour.
    /// </summary>
    public Progression FairStart()
    {
        var p = new Progression();
        if (StartId >= 0) p.Levels[StartId] = 0;
        return p;
    }

    // ---- item use (what may be equipped) ----

    /// <summary>
    /// The ItemUse set the client rebuilds from FullAchievementInfo (ctp+0x4d8, checked by ctp.ahf 0x1809965e0 with the
    /// item's base name): the itemuse entries of every finished plain node, and per template for each completed row r
    /// (r &lt; level) with UnlockTier t the items of tier t whose base name matches its itemuse patterns (the
    /// &lt;baserewards&gt; ones and those of an &lt;overridemasterylevel level="r"&gt;; levels 1/10/30/60/100 → T4..T8).
    /// Elite rows unlock nothing.
    /// </summary>
    public HashSet<string> ItemUse(Progression p)
    {
        var set = new HashSet<string>(StringComparer.Ordinal);
        foreach (var id in p.Completed)
            if (id >= 0 && id < Nodes.Count) set.UnionWith(Nodes[id].ItemUse);
        foreach (var (id, level) in p.Levels)
        {
            if (id < 0 || id >= Nodes.Count || !Nodes[id].IsTemplate) continue;
            var node = Nodes[id];
            for (var r = 0; r < Math.Min(level, node.BaseLevels); r++)
                if (node.Levels[r].UnlockTier > 0) set.UnionWith(RowUnlocks(node, r));
        }
        return set;
    }

    /// <summary>The base names one completed row unlocks (cached per node and row).</summary>
    private string[] RowUnlocks(BoardNode node, int row)
    {
        lock (_rowUnlocks)
        {
            if (_rowUnlocks.TryGetValue((node.Id, row), out var cached)) return cached;
            var patterns = node.ItemUsePatterns.Concat(node.ItemUseAt.GetValueOrDefault(row) ?? []).ToArray();
            var names = patterns.Length == 0 ? [] : _namesByTier.GetValueOrDefault(node.Levels[row].UnlockTier) ?? [];
            return _rowUnlocks[(node.Id, row)] = [.. names.Where(n => patterns.Any(p => GameData.Glob(p, n)))];
        }
    }

    /// <summary>
    /// Whether a character with <paramref name="p"/> may equip <paramref name="item"/>, as the client decides it
    /// (ItemNotUnlockedToEquip, NOTES-protocol): items.xml unlockedtoequip, or its base name in <see cref="ItemUse"/>.
    /// Null = everything unlocked.
    /// </summary>
    public bool CanEquip(Progression? p, ItemTemplate item) =>
        p is null || item.UnlockedToEquip || ItemUse(p).Contains(BaseName(item.Name));
}
