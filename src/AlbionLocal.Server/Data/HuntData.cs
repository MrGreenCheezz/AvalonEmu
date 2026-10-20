using System.Globalization;
using System.Xml.Linq;

namespace AlbionLocal.Server.Data;

/// <summary>A &lt;target&gt; of dynamichunt.xml: the tracked creature (a _TR_ mob), its track tile and minimap icon. <see cref="Index"/> = the client's byq id (document order from 0).</summary>
public sealed record HuntTarget(int Index, string Name, string Category, string MobId, string TrackTile, string? MinimapIcon)
{
    /// <summary>A group target (…_GROUP…, the veteran mob); else solo.</summary>
    public bool Group => Name.Contains("_GROUP", StringComparison.Ordinal);

    /// <summary>The temporary event copies (…_TEMP_…), the witch event and the joker target: never picked for a found track.</summary>
    public bool Special => Name.Contains("_TEMP", StringComparison.Ordinal) || Category is "EVENT" or "UNDEFINED";
}

/// <summary>A main or final quest choice of a hunt: weight, chance changes and the conditions this server evaluates.</summary>
public sealed record HuntQuestPick(string Quest, double Weight, double? ChanceOnSuccess, double? ChanceOnFailure,
    int QuestCountMin, int QuestCountMax, double? HpAbove, double? HpBelow, bool NotTwiceInARow, bool OnlyUndefined);

/// <summary>
/// A &lt;hunt&gt; (<see cref="Index"/> = the client's byk id, HuntStarted key 1, NewHuntTrack key 7): type, group size, the final-quest
/// chance rules and its quest lists.
/// </summary>
public sealed record HuntDefinition(int Index, string Name, string Type, int GroupMin, int GroupMax, double InitialFinalChance,
    double ChanceOnSuccess, double ChanceOnFailure, int MinQuests, int MaxQuests, double TrackingTime,
    IReadOnlyList<HuntQuestPick> Finals, IReadOnlyList<HuntQuestPick> Mains);

/// <summary>What a hunt quest asks for.</summary>
public enum HuntQuestKind
{
    /// <summary>Go to the spot and read the track there (dynamichunttrack + interactwithtrack); guards with a randommobspawn mob.</summary>
    Track,

    /// <summary>The target waits at the spot and flees (forcemobtoescape / its escape triggers): the trail goes on from where it fled.</summary>
    Escape,

    /// <summary>The target fights to the death (mobisdead); an escape fails the quest.</summary>
    Final,
}

/// <summary>
/// A &lt;quest&gt; of &lt;quests&gt; (<see cref="Index"/> = the client's byr id): its kind, timeout, fame reward, the target's escape
/// triggers (the &lt;mob&gt; variable of mobtypevariable HUNT_TARGET) and the guards of a fight quest.
/// </summary>
public sealed record HuntQuest(int Index, string Name, HuntQuestKind Kind, double TimeoutSeconds, long Fame,
    int EscapeOnThreatCount, int EscapeOnPlayersInProximity, double EscapeAfterTimeInCombat, double EscapeAtHealth,
    double EscapeAfterFirstEngage, string? GuardSpawn, int GuardCount, int ClusterDistance);

/// <summary>
/// dynamichunt.xml (analysis/work/stageN/hunts/SPEC.md §1): the targets, the per-type spot filters, the hunts and their quests,
/// with the client's numbering (static counters in document order, from 0). Immutable.
/// </summary>
public sealed class HuntData
{
    public required IReadOnlyList<HuntTarget> Targets { get; init; }
    public required IReadOnlyList<HuntDefinition> Hunts { get; init; }
    public required IReadOnlyDictionary<string, HuntQuest> Quests { get; init; }

    /// <summary>hunttypesettings filterspotsmindistance / maxdistance by type (solo, group, raid).</summary>
    public required IReadOnlyDictionary<string, (double Min, double Max)> SpotDistance { get; init; }

    public HuntTarget? Target(string name) => Targets.FirstOrDefault(t => t.Name.Equals(name, StringComparison.OrdinalIgnoreCase));

    public HuntDefinition? Hunt(string name) => Hunts.FirstOrDefault(h => h.Name.Equals(name, StringComparison.OrdinalIgnoreCase));

    public HuntQuest? Quest(string name) => Quests.GetValueOrDefault(name);

    /// <summary>
    /// The hunt a target is tracked with (the data has no link; SPEC §1.3): SILVER → SILVER_{SOLO|GROUP}_T{tier}, the named creatures
    /// (WEREWOLF, IMP, ELEMENTAL, EAGLE) → {CATEGORY}_{SOLO|GROUP}, the rest (PANTHER, ENT, DIREBEAR) → GENERAL_{SOLO|GROUP}.
    /// </summary>
    public HuntDefinition? HuntFor(HuntTarget target, int tier)
    {
        var type = target.Group ? "GROUP" : "SOLO";
        return (target.Category == "SILVER" ? Hunt($"SILVER_{type}_T{tier}") : null)
            ?? Hunt($"{target.Category}_{type}") ?? Hunt($"GENERAL_{type}");
    }

    private static double D(XElement e, string name, double fallback = 0) =>
        double.TryParse((string?)e.Attribute(name), NumberStyles.Float, CultureInfo.InvariantCulture, out var v) ? v : fallback;

    private static int I(XElement e, string name, int fallback = 0) => (int)D(e, name, fallback);

    public static HuntData Load(XElement root)
    {
        var targets = new List<HuntTarget>();
        foreach (var c in root.Element("targets")?.Elements("category") ?? [])
            foreach (var t in c.Elements("target"))
                targets.Add(new HuntTarget(targets.Count, (string?)t.Attribute("uniquename") ?? "", (string?)c.Attribute("uniquename") ?? "",
                    (string?)t.Attribute("mobid") ?? "", (string?)t.Attribute("tracktileref") ?? "", (string?)t.Attribute("minimapicon")));

        var spots = new Dictionary<string, (double, double)>(StringComparer.OrdinalIgnoreCase);
        foreach (var s in root.Element("globalsettings")?.Elements("hunttypesettings") ?? [])
            spots[(string?)s.Attribute("type") ?? ""] = (D(s, "filterspotsmindistance", 100), D(s, "filterspotsmaxdistance", 400));

        var hunts = new List<HuntDefinition>();
        foreach (var h in root.Element("hunts")?.Elements("hunt") ?? [])
            hunts.Add(new HuntDefinition(hunts.Count, (string?)h.Attribute("uniquename") ?? "", (string?)h.Attribute("type") ?? "solo",
                I(h, "groupsizemin", 1), I(h, "groupsizemax", 3), D(h, "initialfinalquestchance"), D(h, "defaultchancechangeonsuccess"),
                D(h, "defaultchancechangeonfailure"), I(h, "minquestsuntilfinalquest"), I(h, "maxquestsuntilfinalquest", 100), D(h, "trackingtime", 18),
                Picks(h.Element("finalquests")), Picks(h.Element("mainquests"))));

        var quests = new Dictionary<string, HuntQuest>(StringComparer.OrdinalIgnoreCase);
        var index = 0;
        foreach (var q in root.Element("quests")?.Elements("quest") ?? [])
        {
            var name = (string?)q.Attribute("uniquename") ?? "";
            var vars = q.Element("variables");
            var target = vars?.Elements("mob").FirstOrDefault(m => (string?)m.Attribute("mobtypevariable") == "HUNT_TARGET");
            var guard = vars?.Elements("mob").FirstOrDefault(m => m.Attribute("randommobspawn") is not null);
            var kind = q.Descendants("mobisdead").Any() ? HuntQuestKind.Final : target is not null ? HuntQuestKind.Escape : HuntQuestKind.Track;
            var cluster = vars?.Element("dynamictreasurespot")?.Element("distancetocluster") is { } dc ? I(dc, "maxdistance") : 0;
            quests[name] = new HuntQuest(index++, name, kind, D(q, "timeout", 1200), (long)(q.Element("rewards")?.Elements("fame").Sum(f => D(f, "points")) ?? 0),
                target is null ? 0 : I(target, "escapeonthreatcount"), target is null ? 0 : I(target, "escapeonplayersinproximity"),
                target is null ? 0 : D(target, "escapeaftertimeincombat"), target is null ? 0 : D(target, "escapeathealthpercentage"),
                target is null ? 0 : D(target, "escapeafterfirstengagein"), (string?)guard?.Attribute("randommobspawn"), guard is null ? 0 : I(guard, "amount", 1), cluster);
        }
        return new HuntData { Targets = targets, Hunts = hunts, Quests = quests, SpotDistance = spots };
    }

    /// <summary>The &lt;quest&gt; choices under a finalquests / mainquests element, with the conditions of their questblocks.</summary>
    private static List<HuntQuestPick> Picks(XElement? list)
    {
        var picks = new List<HuntQuestPick>();
        if (list is null) return picks;
        foreach (var q in list.Descendants("quest"))
        {
            int min = 0, max = int.MaxValue;
            double? above = null, below = null;
            var undefined = false;
            var conditions = q.Ancestors("questblock").SelectMany(b => b.Element("conditions")?.Elements() ?? []).Concat(q.Elements());
            foreach (var c in conditions)
            {
                var op = (string?)c.Attribute("op");
                switch (c.Name.LocalName)
                {
                    case "ifquestcount":
                        var v = I(c, "value");
                        if (op == "less") max = Math.Min(max, v - 1);
                        else if (op == "greaterorequal") min = Math.Max(min, v);
                        else if (op == "greater") min = Math.Max(min, v + 1);
                        break;
                    case "ifvarfloat" when (string?)c.Attribute("variablename") == "HP_TARGET":
                        if (op is "greater" or "greaterorequal") above = D(c, "value");
                        else if (op is "less" or "lessorequal") below = D(c, "value");
                        break;
                    case "ifhunttarget" when (string?)c.Attribute("value") == "UNDEFINED":
                        undefined = op == "equals";
                        break;
                }
            }
            double? Opt(string a) => q.Attribute(a) is null ? null : D(q, a);
            picks.Add(new HuntQuestPick((string?)q.Attribute("name") ?? "", D(q, "weight", 1), Opt("chancechangeonsuccess"), Opt("chancechangeonfailure"),
                min, max, above, below, q.Element("iflastoccurrence") is not null, undefined));
        }
        return picks;
    }
}
