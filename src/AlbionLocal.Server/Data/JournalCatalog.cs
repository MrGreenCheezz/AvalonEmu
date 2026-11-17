using System.Globalization;
using System.Xml.Linq;

namespace AlbionLocal.Server.Data;

/// <summary>
/// albionjournal.xml (ops-audit SPEC §3.4 for the tracker; analysis/work/stageH/journals/SPEC.md §B for the journal): every
/// achievement of the Albion Journal by uniquename, in file order with its category and subcategory, whether it mirrors a Steam
/// achievement (steamachievementname, the one-shot milestones such as SA_EXPEDITION_FINISH_01), its goal (absoluteprogressmax),
/// its reward, and its missions as a tree (<see cref="Mission"/>); the categories' rewards (categoryrewards). Read with the first
/// cluster resolved (off the world thread, <see cref="ClusterStore.Journal"/>); immutable.
/// </summary>
public sealed class JournalCatalog
{
    /// <summary>One achievement: categories/category@uniquename, the achievement's name.</summary>
    public sealed record Entry(string Category, string Name, bool Steam);

    /// <summary>
    /// A filter of a mission (If* elements and not / or / and over them, SPEC §B.5): a predicate on the event. <see cref="Kind"/> is the
    /// element name, <see cref="Op"/> and <see cref="Value"/> its op / value attributes (IfInRandomDungeon: value = generatortype,
    /// IfClusterTypeFromFileName: value = clustertype).
    /// </summary>
    public sealed record Filter(string Kind, string Op, string Value, IReadOnlyList<Filter> Children);

    /// <summary>
    /// A mission element of an achievement (SPEC §B.3, §B.5). A container (the achievement itself, "all"; any count=n; progresssum
    /// sum=n; or; and) has <see cref="Children"/>; a leaf (killmob, gather, fameearned …) has its attributes, its filters (all of them
    /// must hold) and its mobid list. <see cref="Index"/>: its 1-based place in document order among the achievement's mission elements
    /// (a container takes one too), the "_n" of its sub-condition key (key 4 of op 144). <see cref="Goal"/>: what completes it, in its
    /// units (counts; fame and points as a7m raw, × 10000 of the file's whole value). <see cref="Supported"/>: false when it has a kind,
    /// attribute or child this server cannot judge; it never progresses then (safe).
    /// </summary>
    public sealed class Mission
    {
        public required Achievement Achievement { get; init; }
        public required string Kind { get; init; }
        public int Index { get; init; }
        public long Goal { get; init; }
        public IReadOnlyDictionary<string, string> Attributes { get; init; } = new Dictionary<string, string>();
        public IReadOnlyList<Filter> Filters { get; init; } = [];
        public IReadOnlySet<string> Mobs { get; init; } = new HashSet<string>();
        public IReadOnlyList<Mission> Children { get; init; } = [];
        public bool Supported { get; init; }

        public bool IsContainer => Kind is "all" or "any" or "progresssum" or "or" or "and";

        /// <summary>The key its progress is kept under (achievement name + "_" + index).</summary>
        public string Key => $"{Achievement.Name}_{Index}";

        /// <summary>The fame and point missions count a7m raw, the client sees whole units (SPEC §B.5: whole fame).</summary>
        public bool Raw => Kind is "killmobfame" or "fameearned" or "gatherfame" or "fishingfame" or "reachseasonpoints" or "killplayerfame" or "gainchallengepoints";

        public string? Attr(string name) => Attributes.GetValueOrDefault(name);

        public int IntAttr(string name, int fallback) => int.TryParse(Attr(name), NumberStyles.Integer, CultureInfo.InvariantCulture, out var v) ? v : fallback;
    }

    /// <summary>An achievement with what the journal needs: subcategory, goal, reward and the mission tree (<see cref="Root"/>, kind "all").</summary>
    public sealed class Achievement
    {
        public required string Category { get; init; }
        public required string Subcategory { get; init; }
        public required string Name { get; init; }
        public bool Steam { get; init; }
        public long Max { get; init; }
        public string? RewardItem { get; init; }
        public int RewardAmount { get; init; } = 1;
        public Mission Root { get; internal set; } = null!;

        /// <summary>The sub-condition string of mission <paramref name="m"/> (key 4 of op 144): &lt;subcategory&gt;&lt;name&gt;_&lt;index&gt;.</summary>
        public string SubKey(Mission m) => Subcategory + Name + "_" + m.Index.ToString(CultureInfo.InvariantCulture);

        /// <summary>Its leaves, in document order.</summary>
        public IEnumerable<Mission> Leaves => Walk(Root).Where(m => !m.IsContainer);

        private static IEnumerable<Mission> Walk(Mission m) => m.Children.SelectMany(Walk).Prepend(m);
    }

    /// <summary>A category reward (categoryrewards/itemreward): claimable once that share of the category's achievements is done.</summary>
    public sealed record CategoryReward(string Category, string Id, string Item, double Share);

    /// <summary>The leaf kinds this server can judge, with the attributes each may have (any other attribute makes the leaf unsupported).</summary>
    private static readonly Dictionary<string, HashSet<string>> Known = new(StringComparer.Ordinal)
    {
        ["killmob"] = ["count", "countpartykills", "mintier", "maxtier", "category", "nameloca", "requiresfinalboss", "faction", "mobid"],
        ["killmobfame"] = ["value", "category", "nameloca", "faction"],
        ["fameearned"] = ["value", "nameloca"],
        ["gatherfame"] = ["value", "nameloca"],
        ["fishingfame"] = ["value", "nameloca"],
        ["gather"] = ["count", "mintier", "maxtier", "nameloca", "minenchantmentlevel", "maxenchantmentlevel"],
        ["finishexpedition"] = ["count", "category", "name", "nameloca", "type", "mindifficulty", "maxdifficulty", "timelimit"],
        ["reachseasonpoints"] = ["value", "nameloca"],
        // Stage I guild goals (guild-goals SPEC §1.10): counted only for a member of a guild (isinguild); the level is set, not added.
        ["gainchallengepoints"] = ["value", "isinguild", "nameloca"],
        ["reachguildchallengelevel"] = ["value", "nameloca"],
        // Phase 3: a season-end member reward of the guild's bracket or a higher one (minrank: iron … crystal).
        ["gotguildmemberreward"] = ["minrank", "nameloca"],
        // Stage I PvP (pvp-goals SPEC §1.2, §2.4); stageL corrupted: specialcondition KillCorruptedDungeonInvader / KillCorruptedDungeonHost.
        ["killplayer"] = ["count", "where", "assists", "clustertype", "nameloca", "specialcondition"],
        ["knockdownplayer"] = ["count", "assists", "clustertype", "nameloca", "specialcondition"],
        // StageL corrupted: the infamy reached (set, not added; amount the goal).
        ["haveinfamy"] = ["amount", "infamytype", "nameloca"],
        ["killplayerfame"] = ["value", "countpartykills", "assists", "nameloca"],
        ["killingspree"] = ["count", "seconds", "where", "countassists", "nameloca"],
        ["die"] = ["where", "minvalue", "nameloca"],
        ["finishduel"] = ["count", "outcome", "nameloca"],
        // StageR lore: a lore snippet claimed (the LEAD_UP_SEASON_LORE_KEEPER missions, and the leaf made for each loreachievement).
        ["claimloresnippet"] = ["loresnippet", "nameloca"],
    };

    /// <summary>The filters this server can judge (any other one is false, so its mission never progresses).</summary>
    public static readonly IReadOnlySet<string> KnownFilters = new HashSet<string>(StringComparer.Ordinal)
    {
        "IfClusterArcheType", "IfTargetMobName", "IfInRandomDungeon", "IfClusterId", "IfClusterType", "IfPartySize", "not", "or", "and",
        "IfTargetIsInOpposingFaction", "IfClusterTypeFromFileName", "IfCombatHistoryAttackers",
    };

    private readonly HashSet<string> _names;
    private readonly Dictionary<string, Achievement> _byName;
    private readonly Dictionary<string, List<Mission>> _leaves;

    private JournalCatalog(List<Entry> entries, List<Achievement> achievements, List<CategoryReward> rewards)
    {
        Entries = entries;
        _names = entries.Select(e => e.Name).ToHashSet(StringComparer.Ordinal);
        Achievements = achievements;
        _byName = achievements.GroupBy(a => a.Name, StringComparer.Ordinal).ToDictionary(g => g.Key, g => g.First(), StringComparer.Ordinal);
        CategoryRewards = rewards;
        _leaves = achievements.SelectMany(a => a.Leaves).Where(m => m.Supported).GroupBy(m => m.Kind).ToDictionary(g => g.Key, g => g.ToList(), StringComparer.Ordinal);
        CategorySizes = achievements.GroupBy(a => a.Category).ToDictionary(g => g.Key, g => g.Count(), StringComparer.Ordinal);
    }

    /// <summary>The achievements in file order (achievement, tutorialachievement and loreachievement under a subcategory).</summary>
    public IReadOnlyList<Entry> Entries { get; }

    /// <summary>The achievements with their missions, in file order.</summary>
    public IReadOnlyList<Achievement> Achievements { get; }

    /// <summary>Every category's rewards (PVE_1 …), in file order.</summary>
    public IReadOnlyList<CategoryReward> CategoryRewards { get; }

    /// <summary>The number of achievements per category (the denominator of a category reward's share).</summary>
    public IReadOnlyDictionary<string, int> CategorySizes { get; }

    /// <summary>Whether <paramref name="name"/> is an achievement the client knows (it skips unknown names of event 600).</summary>
    public bool Contains(string name) => _names.Contains(name);

    /// <summary>The achievement <paramref name="name"/>; null = none.</summary>
    public Achievement? Find(string name) => _byName.GetValueOrDefault(name);

    /// <summary>The supported leaves of kind <paramref name="kind"/> (killmob, gather …) over every achievement.</summary>
    public IReadOnlyList<Mission> LeavesOf(string kind) => _leaves.TryGetValue(kind, out var l) ? l : [];

    /// <summary>
    /// What the tracker shows a character that tracks nothing: one achievement of each of <paramref name="categories"/> in
    /// turn, each category's in file order, until <paramref name="count"/>; the Steam mirrors are left out. Officially a
    /// fresh character got one of each kind (a tutorial goal, gathering T2, kill T3 mobs, a season goal) for its [] request.
    /// </summary>
    public string[] Suggested(int count, IReadOnlyList<string> categories)
    {
        var queues = categories.Select(c => new Queue<string>(Entries.Where(e => e.Category == c && !e.Steam).Select(e => e.Name))).ToList();
        var list = new List<string>();
        while (list.Count < count && queues.Any(q => q.Count > 0))
            foreach (var q in queues)
                if (list.Count < count && q.Count > 0) list.Add(q.Dequeue());
        return [.. list];
    }

    public static JournalCatalog Load(XElement root)
    {
        var entries = new List<Entry>();
        var achievements = new List<Achievement>();
        var rewards = new List<CategoryReward>();
        foreach (var category in root.Element("categories")?.Elements("category") ?? [])
        {
            var name = (string?)category.Attribute("uniquename") ?? "";
            foreach (var r in category.Element("categoryrewards")?.Elements("itemreward") ?? [])
                if ((string?)r.Attribute("id") is { Length: > 0 } id)
                    rewards.Add(new CategoryReward(name, id, (string?)r.Attribute("item") ?? "",
                        double.TryParse((string?)r.Attribute("percentageofmissionsfinished"), NumberStyles.Float, CultureInfo.InvariantCulture, out var share) ? share : 1));
            foreach (var sub in category.Elements().Where(e => e.Name.LocalName is "subcategory" or "tutorialsubcategory"))
                foreach (var a in sub.Elements().Where(e => e.Name.LocalName.EndsWith("achievement", StringComparison.Ordinal)))
                    if ((string?)a.Attribute("name") is { Length: > 0 } achievement)
                    {
                        var steam = a.Attribute("steamachievementname") is not null;
                        entries.Add(new Entry(name, achievement, steam));
                        achievements.Add(Parse(a, name, (string?)sub.Attribute("uniquename") ?? "", achievement, steam));
                    }
        }
        return new JournalCatalog(entries, achievements, rewards);
    }

    private static Achievement Parse(XElement a, string category, string subcategory, string name, bool steam)
    {
        var ach = new Achievement
        {
            Category = category, Subcategory = subcategory, Name = name, Steam = steam,
            Max = Math.Max(1, XmlAttr.Long(a, "absoluteprogressmax", 1)),
            RewardItem = (string?)a.Attribute("rewarditem") is { Length: > 0 } item ? item : null,
            RewardAmount = Math.Max(1, XmlAttr.Int(a, "rewardamount", 1)),
        };
        var index = 0;
        var children = a.Elements().Where(IsMission).Select(e => ParseMission(ach, e, ref index)).ToList();
        // StageR lore (SPEC §5): a <loreachievement loresnippet="KEEPER_20"> has no missions; it is done when its snippet is claimed [infer:
        // absoluteprogressmax 1, the window's LoreAchievementEntry shows the snippet's text and the reward's Claim button], so one leaf stands for it.
        if (children.Count == 0 && a.Name.LocalName == "loreachievement" && (string?)a.Attribute("loresnippet") is { Length: > 0 } lore)
            children.Add(new Mission
            {
                Achievement = ach, Kind = "claimloresnippet", Index = ++index, Goal = 1, Supported = true,
                Attributes = new Dictionary<string, string>(StringComparer.Ordinal) { ["loresnippet"] = lore },
            });
        ach.Root = new Mission { Achievement = ach, Kind = "all", Index = 0, Supported = true, Children = children };
        return ach;
    }

    /// <summary>Whether a child of an achievement or a container is a mission (not a filter, a list entry or a comment).</summary>
    private static bool IsMission(XElement e) => !e.Name.LocalName.StartsWith("If", StringComparison.Ordinal) && e.Name.LocalName is not ("not" or "mobid");

    private static Mission ParseMission(Achievement ach, XElement e, ref int index)
    {
        var kind = e.Name.LocalName;
        var attrs = e.Attributes().ToDictionary(x => x.Name.LocalName, x => x.Value, StringComparer.Ordinal);
        var own = ++index;
        if (kind is "any" or "progresssum" or "or" or "and")
        {
            var children = new List<Mission>();
            foreach (var c in e.Elements().Where(IsMission)) children.Add(ParseMission(ach, c, ref index));
            var goal = kind switch
            {
                "any" => XmlAttr.Long(e, "count", children.Count),
                "progresssum" => XmlAttr.Long(e, "sum", 1),
                _ => 1,
            };
            return new Mission
            {
                Achievement = ach, Kind = kind, Index = own, Attributes = attrs, Goal = goal, Children = children,
                Supported = children.Count > 0 && (kind != "progresssum" || children.All(c => !c.Raw)),
            };
        }
        var filters = e.Elements().Where(c => c.Name.LocalName.StartsWith("If", StringComparison.Ordinal) || c.Name.LocalName is "not" or "or" or "and")
            .Select(ParseFilter).ToList();
        var mobs = e.Elements("mobid").Select(m => (string?)m.Attribute("name")).OfType<string>().ToHashSet(StringComparer.Ordinal);
        // A single mob as an attribute (<killmob mobid="T8_MOB_RD_..._BOSS"/>: the random-dungeon and Avalon bosses) joins the list.
        if (attrs.GetValueOrDefault("mobid") is { Length: > 0 } single) mobs.Add(single);
        var others = e.Elements().Any(c => c.Name.LocalName != "mobid" && !(c.Name.LocalName.StartsWith("If", StringComparison.Ordinal) || c.Name.LocalName is "not" or "or" or "and"));
        var known = Known.TryGetValue(kind, out var allowed) && attrs.Keys.All(allowed.Contains) && !others
            && (attrs.GetValueOrDefault("category") is null or "roaming" || kind == "finishexpedition")
            && (mobs.Count == 0 || kind == "killmob");
        long whole = kind switch
        {
            "killmobfame" or "fameearned" or "gatherfame" or "fishingfame" or "reachseasonpoints" or "killplayerfame" or "gainchallengepoints" => XmlAttr.Long(e, "value", 1) * 10000,
            "reachguildchallengelevel" => XmlAttr.Long(e, "value", 1),
            "haveinfamy" => XmlAttr.Long(e, "amount", 1),
            _ => XmlAttr.Long(e, "count", 1),
        };
        return new Mission
        {
            Achievement = ach, Kind = kind, Index = own, Attributes = attrs, Goal = Math.Max(1, whole), Filters = filters, Mobs = mobs, Supported = known,
        };
    }

    private static Filter ParseFilter(XElement e) => new(e.Name.LocalName,
        (string?)e.Attribute("op") ?? "equals",
        (string?)e.Attribute("value") ?? (string?)e.Attribute("generatortype") ?? (string?)e.Attribute("clustertype") ?? "",
        [.. e.Elements().Select(ParseFilter)]);
}
