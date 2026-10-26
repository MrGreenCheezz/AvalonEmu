using System.Xml.Linq;

namespace AlbionLocal.Server.Data;

/// <summary>
/// The client's arena file (arena_europe.bin, the region the client logs in with, Login key 26; arena.bin without it; stageP league-pvp
/// SPEC §1.3, §3.3): the arenacategory list (its index is the client's category id: 0 unranked, 1 ranked = the Crystal Arena, 2 ranked_1v1),
/// the arenaseason list and the season's rank template (RANKED_BRACKETS_ARENA_S26: 17 ranks ARENA_IRON_1 … ARENA_CRYSTAL with their
/// promotion and demotion thresholds, rank point modifiers and per category rewards). Read with the first ranked arena result or
/// GetArenaRankings (7 KB, on the world thread, <see cref="ClusterStore.Arena"/>); immutable.
/// </summary>
public sealed class ArenaData
{
    /// <summary>
    /// The arenaseason index GetArenaRankings key 0 and StartArenaMatchInfos key 15 carry: 12 = SEASON_34 (2026-09-12 … 11-21) in
    /// arena_europe.bin, as the official Europe server sent (content/opcode-stubs.json before stageP P7; arena.xml (West) would be 18).
    /// </summary>
    public const short SeasonIndex = 12;

    /// <summary>The ranked categories GetArenaRankings lists, in its order (ranked, ranked_1v1; the same ids in all three region files).</summary>
    public static readonly IReadOnlyList<int> RankedCategories = [1, 2];

    /// <summary>One reward line: an item (count) or personal season points (<see cref="Psp"/> &gt; 0); premium "Premium", "NonPremium" or "" (both), role "" or ARENA_HEAL.</summary>
    public sealed record Reward(string Item, int Count, int Psp, string Premium, string Role)
    {
        public bool For(bool premium) => Premium.Length == 0 || Premium == (premium ? "Premium" : "NonPremium");
    }

    /// <summary>
    /// One &lt;arenacategory&gt;: name, cluster, matchtype, isranked, minitempoweraverage, rankpointfactor (1 without; ranked_1v1 0.5),
    /// dailylimitcategory ("" = its own name).
    /// </summary>
    public sealed record Category(int Id, string Name, string Cluster, string MatchType, bool Ranked, double MinItemPower, double RankPointFactor, string DailyLimit);

    /// <summary>
    /// One &lt;rank&gt; of the season template: its index (0 = ARENA_IRON_1), id, promotionpointthreshold, demotionpointthreshold,
    /// winrankpointmodifier, loserankpointmodifier, elowinbooster, elolosebooster, expectedelo, and its rewards by category name:
    /// the win and loss lines, the rank-up items (seasonrankrewards) and the limited (daily) win items.
    /// </summary>
    public sealed record Rank(int Index, string Id, int Promotion, int Demotion, double WinModifier, double LoseModifier, double EloWinBooster,
        double EloLoseBooster, double ExpectedElo, IReadOnlyDictionary<string, IReadOnlyList<Reward>> Win, IReadOnlyDictionary<string, IReadOnlyList<Reward>> Lose,
        IReadOnlyDictionary<string, IReadOnlyList<Reward>> RankUp, IReadOnlyList<Reward> Limited)
    {
        public IReadOnlyList<Reward> WinOf(string category) => Win.GetValueOrDefault(category) ?? [];
        public IReadOnlyList<Reward> LoseOf(string category) => Lose.GetValueOrDefault(category) ?? [];
        public IReadOnlyList<Reward> RankUpOf(string category) => RankUp.GetValueOrDefault(category) ?? [];
    }

    public IReadOnlyList<Category> Categories { get; }
    public IReadOnlyList<string> Seasons { get; }

    /// <summary>The ranks of season <see cref="SeasonIndex"/>'s template (the last template without it), lowest first.</summary>
    public IReadOnlyList<Rank> Ranks { get; }

    /// <summary>The rank template's name (RANKED_BRACKETS_ARENA_S26).</summary>
    public string Template { get; }

    private ArenaData(IReadOnlyList<Category> categories, IReadOnlyList<string> seasons, IReadOnlyList<Rank> ranks, string template) =>
        (Categories, Seasons, Ranks, Template) = (categories, seasons, ranks, template);

    public Category? CategoryOf(int id) => id >= 0 && id < Categories.Count ? Categories[id] : null;

    /// <summary>The rank at <paramref name="index"/>, clamped to the template.</summary>
    public Rank RankAt(int index) => Ranks[Math.Clamp(index, 0, Ranks.Count - 1)];

    private static IEnumerable<XElement> Kids(XElement? e, string name) => e?.Elements().Where(x => x.Name.LocalName == name) ?? [];

    private static IReadOnlyList<Reward> Rewards(XElement e) =>
    [
        .. e.Elements().Select(r => r.Name.LocalName switch
        {
            "itemreward" => new Reward(MatchTypeData.S(r, "itemid"), Math.Max(1, (int)MatchTypeData.D(r, "count", 1)), 0, MatchTypeData.S(r, "premiumstatus"), MatchTypeData.S(r, "role")),
            "pspreward" => new Reward("", 0, (int)MatchTypeData.D(r, "amount"), MatchTypeData.S(r, "premiumstatus"), MatchTypeData.S(r, "role")),
            _ => null,
        }).OfType<Reward>(),
    ];

    private static IReadOnlyDictionary<string, IReadOnlyList<Reward>> ByCategory(XElement rank, string element) =>
        Kids(rank, element).GroupBy(e => MatchTypeData.S(e, "category"), StringComparer.Ordinal)
            .ToDictionary(g => g.Key, g => (IReadOnlyList<Reward>)[.. g.SelectMany(Rewards)], StringComparer.Ordinal);

    /// <summary>The file's root (comments are not elements, so the category index is the list index as the client counts it).</summary>
    public static ArenaData Load(XElement root)
    {
        var categories = Kids(root.Elements().FirstOrDefault(e => e.Name.LocalName == "arenacategories"), "arenacategory")
            .Select((e, i) => new Category(i, MatchTypeData.S(e, "name"), MatchTypeData.S(e, "cluster"), MatchTypeData.S(e, "matchtype"), MatchTypeData.B(e, "isranked"),
                MatchTypeData.D(e, "minitempoweraverage"), MatchTypeData.D(e, "rankpointfactor", 1), MatchTypeData.S(e, "dailylimitcategory") is { Length: > 0 } d ? d : MatchTypeData.S(e, "name")))
            .ToList();
        var seasonNodes = Kids(root.Elements().FirstOrDefault(e => e.Name.LocalName == "arenaseasons"), "arenaseason").ToList();
        var templates = Kids(root.Elements().FirstOrDefault(e => e.Name.LocalName == "arenaseasonranktemplates"), "arenaseasonranktemplate").ToList();
        if (categories.Count == 0 || templates.Count == 0) throw new InvalidDataException("arena: no arenacategory or no arenaseasonranktemplate");
        var wanted = SeasonIndex < seasonNodes.Count ? MatchTypeData.S(seasonNodes[SeasonIndex], "seasonranktemplate") : "";
        var template = templates.FirstOrDefault(t => MatchTypeData.S(t, "name") == wanted) ?? templates[^1];
        var ranks = Kids(template, "rank").Select((r, i) => new Rank(i, MatchTypeData.S(r, "id"), (int)MatchTypeData.D(r, "promotionpointthreshold", 32767),
            (int)MatchTypeData.D(r, "demotionpointthreshold"), MatchTypeData.D(r, "winrankpointmodifier", 1), MatchTypeData.D(r, "loserankpointmodifier", 1),
            MatchTypeData.D(r, "elowinbooster"), MatchTypeData.D(r, "elolosebooster"), MatchTypeData.D(r, "expectedelo"), ByCategory(r, "winrewards"),
            ByCategory(r, "loserewards"), ByCategory(r, "seasonrankrewards"), [.. Kids(r, "limitedwinrewards").SelectMany(Rewards)])).ToList();
        if (ranks.Count == 0) throw new InvalidDataException($"arena: {MatchTypeData.S(template, "name")} has no rank");
        return new ArenaData(categories, [.. seasonNodes.Select(s => MatchTypeData.S(s, "name"))], ranks, MatchTypeData.S(template, "name"));
    }

    /// <summary>arena_europe.bin from the client's GameData <paramref name="folder"/>, else arena.bin.</summary>
    public static ArenaData Load(string folder) =>
        Load(GameDataFile.Open(folder, File.Exists(Path.Combine(folder, "arena_europe.bin")) ? "arena_europe.bin" : "arena.bin").Root());
}
