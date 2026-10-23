using System.Collections.Frozen;
using System.Globalization;
using System.Numerics;
using System.Xml.Linq;

namespace AlbionLocal.Server.Data;

/// <summary>
/// The client-authored island continuation, after opening quests A–D and before starter-town missions.
/// Definitions retain the complete XML: conditions, alternative branches, turn-in item costs, hints, buffs,
/// blockers, NPC variables and rewards are not reduced to a list of location markers.
/// Instances consume these shared definitions without changing them.
/// </summary>
public sealed class TutorialJourneyData
{
    public const string Prefix = "MISSIONS_T2_TUTORIAL_TEST_";
    // cb3.UseFunctionType in client build 345319; these ids are sent by the actual UI operations.
    public static IReadOnlyDictionary<string, int> FunctionIds { get; } = new Dictionary<string, int>(StringComparer.Ordinal)
    {
        ["OpenClusterMap"] = 2, ["OpenDestinyBoard"] = 3, ["InteractWithMarketplace"] = 14,
        ["BuyJourney"] = 15, ["FinishTutorial"] = 19
    }.ToFrozenDictionary(StringComparer.Ordinal);
    public sealed record Condition(string Kind, IReadOnlyDictionary<string, string> Attributes,
        IReadOnlyList<Condition> Children)
    {
        public string? Attribute(string name) => Attributes.GetValueOrDefault(name);
    }
    public sealed record Task(int Index, XElement Definition, IReadOnlyList<Condition> Conditions,
        IReadOnlyList<XElement> Preactions);
    public sealed record Quest(int Id, string Name, XElement Definition, IReadOnlyList<Task> Tasks)
    {
        public IReadOnlyList<string> Previous => Split((string?)Definition.Attribute("previousquests"));
        public string? Next => (string?)Definition.Attribute("nextquest");
    }
    public sealed record QuestGiver(string Cluster, string Reference, Guid Guid, int Type,
        Vector2 Position, float Direction)
    {
        public IReadOnlyList<string> Tags { get; init; } = [];
        public IReadOnlyList<string> OfferTags { get; init; } = [];
        public IReadOnlyList<IReadOnlyList<string>> OfferSlots { get; init; } = [];
    }

    public required TutorialData Tutorial { get; init; }
    public required IReadOnlyList<Quest> Quests { get; init; }
    public required IReadOnlyDictionary<string, Quest> ByName { get; init; }
    public required IReadOnlyDictionary<string, ClusterTemplate> Maps { get; init; }
    public required IReadOnlyList<QuestGiver> QuestGivers { get; init; }
    public IReadOnlyDictionary<string, int> Hints { get; init; } = FrozenDictionary<string, int>.Empty;
    /// <summary>Client visual references which have no static circle in this installation. Gathering still uses actual item events.</summary>
    public IReadOnlyList<string> UnresolvedVisualMarkers { get; init; } = [];

    public Quest? Find(string name) => ByName.GetValueOrDefault(name);
    public IEnumerable<Quest> Successors(string previous) => Quests.Where(q => q.Previous.Contains(previous));
    public IEnumerable<LocationMarkerSpawn> Markers(string cluster, string name) => Maps.TryGetValue(cluster, out var map)
        ? map.LocationMarkers.Where(m => m.LocationId == name) : [];

    private static readonly FrozenSet<string> Conditions = new[] {
        "equipitem", "gotolocation", "killmob", "unlockachievement", "gotoquestgiver", "usefunction",
        "marketbuyitem", "acquireitem", "mount", "interactwithbuilding", "gather", "or", "haveplaytime",
        "selectspell", "haveavgitempower"
    }.ToFrozenSet(StringComparer.Ordinal);
    private static readonly FrozenSet<string> Functions = new[] {
        "InteractWithMarketplace", "OpenClusterMap", "OpenDestinyBoard", "FinishTutorial", "BuyJourney"
    }.ToFrozenSet(StringComparer.Ordinal);
    private static readonly string[] Expected = ["E", "F", "H", "I", "K", "KA", "L", "MA", "MB", "MC",
        "NA", "NB", "NC", "OA", "OB", "P", "Q"];

    public static TutorialJourneyData Load(string folder, ClusterStore store)
    {
        // bhq.af() resets to zero; bhq.af(XmlElement,bool) assigns then increments the same counter for
        // both hint and texthint. ct5 dispatches event a0n/390's index through that catalog (build 345319).
        var hints = GameDataFile.Open(folder, "hints.bin").Root().Elements()
            .Where(e => e.Name.LocalName is "hint" or "texthint")
            .Select((e, index) => (Name: Required(e, "uniquename"), Id: index))
            .ToFrozenDictionary(h => h.Name, h => h.Id, StringComparer.Ordinal);
        var result = Load(GameDataFile.Open(folder, "quests.bin").Root(),
            TutorialData.Load(GameDataFile.Open(folder, "tutorial.bin").Root()), store,
            GameDataFile.Open(folder, "questgiverobjects.bin").Root(),
            GameDataFile.Open(folder, "questgivertypes.bin").Root(), hints);
        return result;
    }

    /// <summary>Separate parsing entry point permits testing changed/unsupported client definitions explicitly.</summary>
    public static TutorialJourneyData Load(XElement root, TutorialData tutorial, ClusterStore store, XElement npcRoot,
        XElement? npcTypeRoot = null, IReadOnlyDictionary<string, int>? hints = null)
    {
        var maps = tutorial.Clusters.ToFrozenDictionary(id => id, store.Get, StringComparer.Ordinal);
        foreach (var map in maps.Values)
            if (map.Problems.Count != 0) throw Bad($"unresolved island map {map.Id}");
        var catalog = root.Elements().Where(e => e.Name.LocalName is "quest" or "playerfactionquest").ToArray();
        var npcTypes = npcRoot.Elements("questgiverobject").Select(e => Required(e, "uniquename")).ToArray();
        var givers = maps.Values.SelectMany(map => map.Critical.Where(c => c.Type == "questgiverobject").Select(c =>
        {
            var reference = Required(c.Component, "reference");
            var type = Array.IndexOf(npcTypes, reference);
            if (type < 0 || c.Guid is not { } guid || guid == Guid.Empty) throw Bad($"unresolved giver {reference}@{map.Id}");
            var typeName = Required(npcRoot.Elements("questgiverobject").ElementAt(type), "questgivertype");
            var typeXml = npcTypeRoot?.Elements("questgivertype").SingleOrDefault(e => (string?)e.Attribute("uniquename") == typeName);
            return new QuestGiver(map.Id, reference, guid, type, c.Position, c.Direction)
            {
                Tags = Split((string?)typeXml?.Attribute("tags") ?? typeName),
                OfferTags = typeXml?.Elements("questslot").SelectMany(e => Split((string?)e.Attribute("tags"))).ToArray() ?? [],
                OfferSlots = typeXml?.Elements("questslot").Select(e => Split((string?)e.Attribute("tags"))).ToArray() ?? []
            };
        })).ToArray();

        var quests = Expected.Select(suffix =>
        {
            var name = Prefix + suffix;
            var id = Array.FindIndex(catalog, e => (string?)e.Attribute("uniquename") == name);
            if (id < 0) throw Bad($"missing {name}");
            var xml = new XElement(catalog[id]);
            var tasks = xml.Element("tasks")?.Elements().Select((task, index) =>
            {
                if (task.Name.LocalName is not ("missionstep" or "extendedmissionstep" or "extendedcompletionstep"))
                    throw Bad($"{name}: unsupported task {task.Name}");
                var conditions = (task.Name == "missionstep" ? task.Elements() : task.Element("missions")?.Elements() ?? [])
                    .Select(ReadCondition).ToArray();
                if (conditions.Length == 0) throw Bad($"{name}: empty task {index}");
                var actions = task.Element("preactions")?.Elements().ToArray() ?? [];
                foreach (var action in actions)
                {
                    switch (action.Name.LocalName)
                    {
                        case "settutorialstate":
                            if (tutorial.Find(Required(action, "state")) is null) throw Bad($"unknown state {action}");
                            break;
                        case "settutorialblocker": Required(action, "id"); Number(action, "statetoset", 0); break;
                        case "openhintbox":
                            var hint = Required(action, "uniquename");
                            if (hints is not null && !hints.ContainsKey(hint)) throw Bad($"unknown hint {hint}");
                            break;
                        default: throw Bad($"{name}: unsupported action {action.Name}");
                    }
                }
                foreach (var child in task.Elements())
                    if (task.Name != "missionstep" && child.Name.LocalName is not ("preactions" or "missions"))
                        throw Bad($"{name}: unsupported task element {child.Name}");
                return new Task(index, task, Array.AsReadOnly(conditions), Array.AsReadOnly(actions));
            }).ToArray() ?? [];
            if (tasks.Length == 0) throw Bad($"{name}: missing tasks");
            foreach (var variable in xml.Element("variables")?.Elements() ?? [])
            {
                if (variable.Name.LocalName is not ("dynamiccluster" or "dynamicquestgiver"))
                    throw Bad($"{name}: unsupported variable {variable.Name}");
                Required(variable, "name");
                foreach (var child in variable.Elements())
                    if (child.Name.LocalName is not ("distancetocluster" or "distancefromquestgiver" or "questgivertag"))
                        throw Bad($"{name}: unsupported variable constraint {child.Name}");
            }
            if (xml.Element("rewards") is not { } rewards) throw Bad($"{name}: missing rewards");
            foreach (var reward in rewards.Elements())
            {
                switch (reward.Name.LocalName)
                {
                    case "item": Required(reward, "itemid"); Number(reward, "count", 1); break;
                    case "silver": Number(reward, "absolute", 0); break;
                    case "learningpoints": Number(reward, "amount", 0); break;
                    default: throw Bad($"{name}: unsupported reward {reward.Name}");
                }
            }
            return new Quest(id, name, xml, Array.AsReadOnly(tasks));
        }).ToArray();
        var names = quests.Select(q => q.Name).Append(OpeningTutorialData.CraftingQuest).ToHashSet(StringComparer.Ordinal);
        foreach (var quest in quests)
        {
            if (quest.Previous.Count == 0 || quest.Previous.Any(p => !names.Contains(p))) throw Bad($"{quest.Name}: broken predecessor");
            if (quest.Next is { } next && !names.Contains(next)) throw Bad($"{quest.Name}: broken nextquest");
            foreach (var e in quest.Definition.Descendants())
            {
                var marker = (string?)e.Attribute("locationmarkerid") ?? (string?)e.Attribute("gotolocationmarkerid");
                if (marker is not null && e.Name != "gather" && !maps.Values.Any(m => m.LocationMarkers.Any(p => p.LocationId == marker && p.Radius > 0)))
                    throw Bad($"{quest.Name}: unresolved marker {marker}");
                if (e.Attribute("buildingid") is { } building
                    && !maps.Values.Any(m => m.Buildings.Any(b => b.Template.Name == building.Value)))
                    throw Bad($"{quest.Name}: unresolved building {building.Value}");
            }
        }
        var address = tutorial.TravelPlanner.Split('@');
        if (!maps.TryGetValue(address[1], out var endMap)
            || !endMap.Travelpoints.Any(p => p.Guid == Guid.Parse(address[0]))) throw Bad("departure planner missing from map");
        foreach (var target in tutorial.Destinations)
            if (store.World.Find(target.Cluster) is null) throw Bad($"unknown departure destination {target.Cluster}");
        return new TutorialJourneyData { Tutorial = tutorial, Quests = Array.AsReadOnly(quests),
            ByName = quests.ToFrozenDictionary(q => q.Name, StringComparer.Ordinal), Maps = maps, QuestGivers = Array.AsReadOnly(givers),
            Hints = hints ?? FrozenDictionary<string, int>.Empty,
            UnresolvedVisualMarkers = quests.SelectMany(q => q.Definition.Descendants("gather"))
                .Select(e => (string?)e.Attribute("locationmarkerid")).OfType<string>()
                .Where(marker => !maps.Values.Any(m => m.LocationMarkers.Any(p => p.LocationId == marker)))
                .Distinct(StringComparer.Ordinal).ToArray() };
    }

    private static Condition ReadCondition(XElement xml)
    {
        var kind = xml.Name.LocalName;
        if (!Conditions.Contains(kind)) throw Bad($"unsupported condition {kind}");
        var children = new List<Condition>();
        foreach (var child in xml.Elements())
        {
            if (kind == "or") children.Add(ReadCondition(child));
            else if ((kind == "acquireitem" || kind == "gotoquestgiver") && child.Name == "item")
            {
                Required(child, kind == "acquireitem" ? "id" : "itemid");
                if (kind == "gotoquestgiver") Number(child, "count", 1);
                children.Add(new Condition("item", Attributes(child), []));
            }
            else throw Bad($"unsupported {kind} child {child.Name}");
        }
        switch (kind)
        {
            case "or": if (children.Count < 2) throw Bad("or requires alternatives"); break;
            case "acquireitem":
                if (xml.Attribute("itemid") is null && (children.Count == 0 || (string?)xml.Attribute("listtype") != "or"))
                    throw Bad("acquireitem requires itemid or an explicit alternative item list");
                Number(xml, "count", 1); break;
            case "gather": Required(xml, "itemid"); Number(xml, "count", 1); break;
            case "killmob": Required(xml, "faction"); Number(xml, "count", 1); break;
            case "equipitem": case "mount": Required(xml, "itemid"); break;
            case "marketbuyitem": Required(xml, "item"); Number(xml, "count", 1); break;
            case "interactwithbuilding": Required(xml, "buildingid"); break;
            case "unlockachievement": Required(xml, "achievement"); Number(xml, "masterylevel", 1); break;
            case "selectspell": Required(xml, "activespellslot"); break;
            case "haveplaytime": Number(xml, "seconds", 0); break;
            case "haveavgitempower": Number(xml, "itempower", 0); break;
            case "usefunction": if (!Functions.Contains(Required(xml, "function"))) throw Bad($"unsupported function {xml}"); break;
            case "gotolocation":
                if (xml.Attribute("locationmarkerid") is null && xml.Attribute("gotolocationmarkerid") is null)
                    throw Bad("gotolocation requires authored marker (including the client's gotolocationmarkerid spelling)");
                break;
        }
        if (xml.Attribute("seconds") is not null) Number(xml, "seconds", 0);
        return new Condition(kind, Attributes(xml), children.AsReadOnly());
    }

    private static FrozenDictionary<string, string> Attributes(XElement xml) => xml.Attributes()
        .ToFrozenDictionary(a => a.Name.LocalName, a => a.Value, StringComparer.Ordinal);
    private static string Required(XElement xml, string attribute) => (string?)xml.Attribute(attribute) is { Length: > 0 } value
        ? value : throw Bad($"{xml.Name}: missing {attribute}");
    private static double Number(XElement xml, string attribute, double min) =>
        double.TryParse(Required(xml, attribute), NumberStyles.Float, CultureInfo.InvariantCulture, out var value)
            && double.IsFinite(value) && value >= min ? value : throw Bad($"{xml.Name}: invalid {attribute}");
    private static IReadOnlyList<string> Split(string? text) => (text ?? "").Split([',', ';', ' '], StringSplitOptions.RemoveEmptyEntries);
    private static InvalidDataException Bad(string reason) => new($"Tutorial journey: {reason}");
}
