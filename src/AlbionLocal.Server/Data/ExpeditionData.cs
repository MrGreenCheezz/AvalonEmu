using System.Globalization;
using System.Xml.Linq;

namespace AlbionLocal.Server.Data;

/// <summary>
/// The expedition tables of the client data (analysis/work/stageH/expeditions/SPEC.md, "SPEC" here, §3.1, §4.1): expeditioncategories.xml,
/// expeditionagents.xml and expeditions.xml, each numbered by file order with the TEST entries (the client's static counters bwg.n /
/// bwe.n: ExpeditionRegister keys 1 and 2, ExpeditionRegistrationInfo keys 2 and 10, FullExpeditionInfo key 1). Read with the first
/// cluster that has an expedition agent or is an expedition (off the world thread, <see cref="ClusterStore.Expeditions"/>); immutable.
/// </summary>
public sealed class ExpeditionData
{
    /// <summary>"@EXPEDITION@": a4l.f(Guid) = "@" + ClusterInstanceType 3 + "@" + guid (SPEC §2.6).</summary>
    public const string InstancePrefix = "@EXPEDITION@";

    /// <summary>The instance id of <paramref name="guid"/> ("@EXPEDITION@&lt;guid&gt;", Guid "D" form, lower case).</summary>
    public static string InstanceId(Guid guid) => InstancePrefix + guid.ToString();

    /// <summary>Whether <paramref name="cluster"/> is an expedition instance id.</summary>
    public static bool IsInstance(string? cluster) => cluster?.StartsWith(InstancePrefix, StringComparison.Ordinal) == true;

    /// <summary>
    /// SPEC §4.3: spawn tile names of active expedition layers that no mobs.xml mob has, and the mob they stand for (the task's own mob
    /// where a task depends on it; TRAP and the Torturer's CULTIST miniboss are guesses, SPEC §15 Q9). LOOTCHEST_HCE_FINAL_REWARD is a
    /// loot chest spawn point, never a mob: not here, so it stays unbound. The built-in values of Expeditions:SpawnAliases.
    /// </summary>
    public static readonly IReadOnlyDictionary<string, string> DefaultSpawnAliases = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
    {
        ["T5_MOB_EXP_UNDEAD_GHOUL_VETERAN_BOSS"] = "T5_MOB_EXP_UNDEAD_GHOUL_BOSS_VETERAN",
        ["T6_MOB_EXP_HERETIC_GRENADE_VETERAN_MINIBOSS"] = "T6_MOB_EXP_HERETIC_DYNAMITE_VETERAN_MINIBOSS",
        ["T6_MOB_EXP_HERETIC_TRAP_VETERAN"] = "T6_MOB_EXP_HERETIC_ALARMTRAP_VETERAN",
        ["T6_MOB_EXP_MORGANA_CULTIST_TORTURER_VETERAN_MINIBOSS"] = "T6_MOB_EXP_MORGANA_TORTURER_VETERAN",
    };

    /// <summary>
    /// The alias table in use: Expeditions:SpawnAliases (content/expeditions.json), set by ServerHost at the start before any cluster is
    /// resolved (the binding runs in the cluster store, off the world thread, which has no settings of its own); else the built-in one.
    /// </summary>
    public static IReadOnlyDictionary<string, string> SpawnAliases { get; set; } = DefaultSpawnAliases;

    /// <summary>
    /// The mob a spawn point of an expedition cluster named <paramref name="name"/> spawns when mobs.xml has no such mob (SPEC §4.3): the
    /// alias of <see cref="SpawnAliases"/>, else the one mob whose uniquename has the same "_" tokens; null = none (left out).
    /// </summary>
    public static MobTemplate? ResolveSpawn(MobStore mobs, string name)
    {
        if (SpawnAliases.TryGetValue(name, out var alias)) return mobs.Find(alias);
        var tokens = name.Split('_').Order(StringComparer.Ordinal).ToArray();
        var same = mobs.All.Where(m => m.Name.Length == name.Length && m.Name.Split('_').Order(StringComparer.Ordinal).SequenceEqual(tokens, StringComparer.Ordinal)).Take(2).ToList();
        return same.Count == 1 ? same[0] : null;
    }

    /// <summary>One reward of a category's &lt;rewards&gt; or &lt;bonusrewards&gt;: silver (absolute), an item (itemid, enchantmentlevel, count) or fame points.</summary>
    public sealed record Reward(string Kind, string Item, int Enchantment, int Count, long Amount);

    /// <summary>A hardcore difficulty level (SPEC §3.1): its mob buff spell and factors.</summary>
    /// <summary>A hardcore difficultylevels entry; <see cref="BonusChest"/> = spawnbonuslootchest (level 18: the HCE_FINAL_REWARD chest, econ SPEC §3.1).</summary>
    public sealed record Difficulty(int Level, string MobModifier, double Silver, double Loot, double MobFame, string TokenLootList, bool BonusChest = false);

    /// <summary>An expeditioncategory (bwg): grouptype STANDARD (1 player), VETERAN, HARDCORE (5), ELITE (2) (bwg.ae).</summary>
    public sealed record Category(int Index, string Name, int Tier, string GroupType, double MinItemPower, bool Unlocked,
        IReadOnlyList<Reward> Rewards, IReadOnlyList<Reward> BonusRewards, IReadOnlyList<Difficulty> Difficulties)
    {
        public int GroupSize => GroupType switch { "STANDARD" => 1, "ELITE" => 2, _ => 5 };
        public bool Test => Name.StartsWith("TEST", StringComparison.Ordinal);
    }

    /// <summary>A killmob task: a mob by uniquename (<see cref="Mob"/>) or any mob of a faction (<see cref="Faction"/>), <see cref="Count"/> times.</summary>
    public sealed record KillTask(string? Mob, string? Faction, int Count)
    {
        public override string ToString() => $"{Mob ?? Faction} x{Count}";
    }

    /// <summary>
    /// An expedition (bwe): its world.xml cluster (the cluster attribute up to the first "_", bwe.o), category, timeout and bonus time in
    /// seconds (bonustimeout defaults to timeout), the narrator's name generator and the steps' tasks.
    /// </summary>
    public sealed record Expedition(int Index, string Name, string World, string Category, int CategoryIndex, int Timeout, int BonusTimeout,
        string Narrator, string Title, IReadOnlyList<IReadOnlyList<KillTask>> Steps);

    private readonly Dictionary<string, Category> _categories;

    private ExpeditionData(List<Category> categories, List<(string Name, int[] Categories)> agents, List<Expedition> expeditions)
    {
        Categories = categories;
        Agents = agents;
        Expeditions = expeditions;
        _categories = categories.ToDictionary(c => c.Name, StringComparer.Ordinal);
    }

    /// <summary>The categories in file order (index = position).</summary>
    public IReadOnlyList<Category> Categories { get; }

    /// <summary>The agents in file order (NewExpeditionAgent key 3 = position) with the category indices each offers.</summary>
    public IReadOnlyList<(string Name, int[] Categories)> Agents { get; }

    /// <summary>The expeditions in file order (index = position).</summary>
    public IReadOnlyList<Expedition> Expeditions { get; }

    public Category? CategoryOf(string name) => _categories.GetValueOrDefault(name);

    public Category? CategoryAt(int index) => index >= 0 && index < Categories.Count ? Categories[index] : null;

    public Expedition? ExpeditionAt(int index) => index >= 0 && index < Expeditions.Count ? Expeditions[index] : null;

    public Expedition? Find(string name) =>
        int.TryParse(name, NumberStyles.Integer, CultureInfo.InvariantCulture, out var i) ? ExpeditionAt(i)
        : Expeditions.FirstOrDefault(e => e.Name.Equals(name, StringComparison.OrdinalIgnoreCase));

    /// <summary>The categories agent <paramref name="agent"/> offers; empty for an unknown agent.</summary>
    public IReadOnlyList<int> CategoriesOf(int agent) => agent >= 0 && agent < Agents.Count ? Agents[agent].Categories : [];

    /// <summary>
    /// Whether <paramref name="e"/> can be played here: its world cluster is in world.xml (<paramref name="hasCluster"/>; 0-2, 19 and 30 are
    /// not, SPEC §4.3) and it has at least one task.
    /// </summary>
    public static bool Playable(Expedition e, Func<string, bool> hasCluster) => e.World.Length > 0 && hasCluster(e.World) && e.Steps.Any(s => s.Count > 0);

    public static ExpeditionData Load(XElement categories, XElement agents, XElement expeditions)
    {
        static int Int(XElement e, string a, int fallback = 0) =>
            int.TryParse((string?)e.Attribute(a), NumberStyles.Integer, CultureInfo.InvariantCulture, out var i) ? i : fallback;
        static double Num(XElement e, string a, double fallback = 0) =>
            double.TryParse((string?)e.Attribute(a), NumberStyles.Float, CultureInfo.InvariantCulture, out var d) ? d : fallback;
        static List<Reward> Rewards(XElement? list) => [.. (list?.Elements() ?? []).Select(r => r.Name.LocalName switch
        {
            "silver" => new Reward("silver", "", 0, 0, (long)Num(r, "absolute")),
            "itemreward" => new Reward("item", (string?)r.Attribute("itemid") ?? "", Int(r, "enchantmentlevel"), Int(r, "count", 1), 0),
            "famepoints" => new Reward("fame", "", 0, 0, (long)Num(r, "points")),
            var other => new Reward(other, "", 0, 0, 0),
        })];
        var cats = categories.Elements("expeditioncategory").Select((c, i) => new Category(i, (string?)c.Attribute("uniquename") ?? "", Int(c, "tier", -1),
            ((string?)c.Attribute("grouptype") ?? "STANDARD").ToUpperInvariant(), Num(c, "minitempoweraverage", -1), (string?)c.Attribute("unlockedtoregister") == "true",
            Rewards(c.Element("rewards")), Rewards(c.Element("bonusrewards")),
            [.. (c.Element("difficultylevels")?.Elements("difficulty") ?? []).Select(d => new Difficulty(Int(d, "level"), (string?)d.Attribute("mobmodifier") ?? "",
                Num(d, "silverfactor", 1), Num(d, "lootfactor", 1), Num(d, "mobfamefactor", 1), (string?)d.Attribute("tokenlootlist") ?? "",
                (string?)d.Attribute("spawnbonuslootchest") == "true"))])).ToList();
        var index = cats.GroupBy(c => c.Name).ToDictionary(g => g.Key, g => g.First().Index, StringComparer.Ordinal);
        var ags = agents.Elements("expeditionagent").Select(a => ((string?)a.Attribute("uniquename") ?? "",
            a.Element("expeditions")?.Elements("expedition").Select(e => index.GetValueOrDefault((string?)e.Attribute("category") ?? "", -1)).Where(i => i >= 0).ToArray() ?? [])).ToList();
        var exps = expeditions.Elements("expedition").Select((e, i) =>
        {
            var raw = (string?)e.Attribute("cluster") ?? "";
            var category = (string?)e.Attribute("category") ?? "";
            var timeout = Int(e, "timeout");
            var mission = e.Element("mission");
            return new Expedition(i, (string?)e.Attribute("uniquename") ?? "", raw.Split('_')[0], category, index.GetValueOrDefault(category, -1), timeout,
                Int(e, "bonustimeout", timeout), (string?)e.Element("narrator")?.Attribute("namegenerator") ?? "", (string?)mission?.Attribute("title") ?? "",
                [.. (mission?.Elements("step") ?? []).Select(s => (IReadOnlyList<KillTask>)[.. s.Elements("killmob").Select(k =>
                    new KillTask((string?)k.Attribute("mobid") is { Length: > 0 } m ? m : null, (string?)k.Attribute("faction") is { Length: > 0 } f ? f : null, Math.Max(1, Int(k, "count", 1))))])]);
        }).ToList();
        return new ExpeditionData(cats, ags, exps);
    }
}
