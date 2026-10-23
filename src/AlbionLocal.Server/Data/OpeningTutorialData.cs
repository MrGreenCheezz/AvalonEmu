using System.Globalization;
using System.Xml.Linq;
using AlbionLocal.Server.Entities;
using AlbionLocal.Server.Game;

namespace AlbionLocal.Server.Data;

/// <summary>The deliberately bounded opening through first gathering and crafting, including the shield reward.
/// Coordinates, radii, time requirements and client ids come from this installation, not screenshot pixels.</summary>
public sealed class OpeningTutorialData
{
    public const string FirstQuest = "MISSIONS_T2_TUTORIAL_TEST_A";
    public const string TowerQuest = "MISSIONS_T2_TUTORIAL_TEST_B";
    public const string GatheringQuest = "MISSIONS_T2_TUTORIAL_TEST_C";
    public const string CraftingQuest = "MISSIONS_T2_TUTORIAL_TEST_D";
    public const string Forge = "T1_FORGE_TUTORIAL";
    public sealed record Acquisition(string Item, int Count, string Marker);
    public sealed record Task(int Index, string Kind, string Marker, double Seconds, string? TutorialState)
    {
        public string? SpellNamePart { get; init; }
        public IReadOnlyList<Acquisition> Acquisitions { get; init; } = [];
        public string? Item { get; init; }
        public string? Function { get; init; }
        public IReadOnlyList<string> EntryStates { get; init; } = [];
    }
    public sealed record Quest(int Id, string Name, IReadOnlyList<Task> Tasks, IReadOnlyList<string> Variables);
    public required TutorialData Tutorial { get; init; }
    public required ClusterTemplate Map { get; init; }
    public required CriticalMember Start { get; init; }
    public required CriticalMember Crafter { get; init; }
    public required int CrafterType { get; init; }
    public required Quest First { get; init; }
    public required Quest Tower { get; init; }
    public required Quest Gathering { get; init; }
    public required Quest Crafting { get; init; }
    public TutorialJourneyData? Journey { get; init; }
    public IReadOnlyList<LocationMarkerSpawn> Markers(string name) => Map.LocationMarkers.Where(m => m.LocationId == name).ToArray();

    public static OpeningTutorialData Load(string folder, ClusterStore store)
    {
        var tutorial = TutorialData.Load(GameDataFile.Open(folder, "tutorial.bin").Root());
        var map = store.Get(tutorial.StartCluster);
        var starts = map.Critical.Where(c => c.Type == "startpoint").ToArray();
        var crafters = map.Critical.Where(c => c.Type == "questgiverobject"
            && (string?)c.Component.Attribute("reference") == "WOUNDEDCRAFTER").ToArray();
        if (starts.Length != 1 || crafters.Length != 1 || map.Problems.Count != 0)
            throw new InvalidDataException("Opening tutorial requires one startpoint, one wounded crafter and a resolved map.");
        var types = GameDataFile.Open(folder, "questgiverobjects.bin").Root().Elements("questgiverobject").ToArray();
        var npcType = Array.FindIndex(types, e => (string?)e.Attribute("uniquename") == "WOUNDEDCRAFTER");
        if (npcType < 0 || crafters[0].Guid is not { } guid || guid == Guid.Empty)
            throw new InvalidDataException("Opening tutorial crafter type/GUID is missing.");
        // beu.da resets bog's counter; bog.o3 counts quest and playerfactionquest together, from zero.
        var quests = GameDataFile.Open(folder, "quests.bin").Root().Elements()
            .Where(e => e.Name.LocalName is "quest" or "playerfactionquest").ToArray();
        Quest Read(string name, int limit)
        {
            var id = Array.FindIndex(quests, e => (string?)e.Attribute("uniquename") == name);
            if (id < 0) throw new InvalidDataException($"Missing opening quest {name}.");
            var xml = quests[id];
            var tasks = xml.Element("tasks")?.Elements().Select((e, i) =>
            {
                var missions = (e.Name.LocalName == "missionstep" ? e.Elements() : e.Element("missions")?.Elements())?.ToArray() ?? [];
                var acquisitions = missions.All(m => m.Name == "acquireitem") ? missions.Select(m =>
                {
                    var item = (string?)m.Attribute("itemid") ?? throw new InvalidDataException($"{name}: missing item.");
                    var count = (int?)m.Attribute("count") ?? 0;
                    var location = (string?)m.Attribute("locationmarkerid") ?? "";
                    if (count <= 0 || !map.LocationMarkers.Any(p => p.LocationId == location))
                        throw new InvalidDataException($"{name}: invalid acquisition {item}.");
                    return new Acquisition(item, count, location);
                }).ToArray() : [];
                var mission = acquisitions.Length > 0 ? missions[0] : missions.SingleOrDefault();
                if (mission is null || mission.Name.LocalName is not ("gotolocation" or "gotoquestgiver" or "or" or "acquireitem" or "interactwithbuilding" or "usefunction" or "equipitem" or "selectspell"))
                    throw new InvalidDataException($"{name} task {i}: unsupported opening condition.");
                var marker = (string?)mission.Attribute("locationmarkerid") ?? "";
                if (marker.Length == 0 && mission.Name.LocalName is not ("usefunction" or "equipitem" or "selectspell"))
                    throw new InvalidDataException($"{name}: missing marker.");
                if (marker.Length > 0 && !map.LocationMarkers.Any(m => m.LocationId == marker && m.Radius > 0 && float.IsFinite(m.Radius)))
                    throw new InvalidDataException($"{name}: unresolved marker {marker}.");
                var seconds = mission.Attribute("seconds") is { } secondsAttr
                    ? double.Parse(secondsAttr.Value, CultureInfo.InvariantCulture) : 0;
                if (!double.IsFinite(seconds) || seconds < 0) throw new InvalidDataException($"{name}: invalid seconds.");
                string? spellPart = null;
                if (mission.Name == "or")
                {
                    var choices = mission.Elements().ToArray();
                    spellPart = (string?)choices.FirstOrDefault(c => c.Name == "usespell")?.Attribute("spellnamepart");
                    if (choices.Length != 2 || spellPart != "DODGE"
                        || (string?)choices.FirstOrDefault(c => c.Name == "gotolocation")?.Attribute("locationmarkerid") != marker)
                        throw new InvalidDataException($"{name} task {i}: unsupported alternative conditions.");
                }
                var states = e.Element("preactions")?.Elements("settutorialstate").Select(s => (string?)s.Attribute("state") ?? "").ToArray() ?? [];
                foreach (var entry in states)
                    if (tutorial.Find(entry) is null) throw new InvalidDataException($"Unknown tutorial state {entry}.");
                var state = states.LastOrDefault();
                return new Task(i, mission.Name.LocalName, marker, seconds, state)
                { SpellNamePart = spellPart, Acquisitions = Array.AsReadOnly(acquisitions),
                    Item = (string?)mission.Attribute("itemid"), Function = (string?)mission.Attribute("function"), EntryStates = Array.AsReadOnly(states) };
            }).ToArray() ?? [];
            if (tasks.Length != limit) throw new InvalidDataException($"{name}: opening tasks missing.");
            var variables = xml.Element("variables")?.Elements().Select(e => e.Name.LocalName).ToArray() ?? [];
            if (variables.Any(v => v is not ("dynamiccluster" or "dynamicquestgiver")))
                throw new InvalidDataException($"{name}: unsupported opening variables.");
            if (xml.Element("rewards") is not { } rewards || (name == CraftingQuest
                ? rewards.Elements().Count() != 1 || (string?)rewards.Element("item")?.Attribute("itemid") != "T1_OFF_SHIELD"
                    || (int?)rewards.Element("item")?.Attribute("count") != 1
                : rewards.HasElements))
                throw new InvalidDataException($"{name}: reward definitions differ from this bounded opening.");
            return new Quest(id, name, Array.AsReadOnly(tasks), Array.AsReadOnly(variables));
        }
        var first = Read(FirstQuest, 4);
        var tower = Read(TowerQuest, 7);
        var gathering = Read(GatheringQuest, 3);
        var crafting = Read(CraftingQuest, 6);
        var craftXml = quests[crafting.Id].Element("tasks")!.Elements().ToArray();
        if ((string?)quests[crafting.Id].Attribute("previousquests") != GatheringQuest
            || crafting.Tasks[0].Kind != "interactwithbuilding"
            || (string?)craftXml[0].Element("missions")?.Element("interactwithbuilding")?.Attribute("buildingid") != Forge
            || !crafting.Tasks[1].Acquisitions.Select(a => (a.Item, a.Count)).SequenceEqual(new[] { ("T1_MAIN_SWORD", 1) })
            || crafting.Tasks[2].Function != "OpenInventory" || crafting.Tasks[3].Kind != "equipitem" || crafting.Tasks[3].Item != "T1_MAIN_SWORD"
            || crafting.Tasks[4].Kind != "selectspell"
            || (string?)craftXml[4].Element("missions")?.Element("selectspell")?.Attribute("activespellslot") != "mainhand1"
            || crafting.Tasks[5].Kind != "gotoquestgiver" || map.Buildings.Count(b => b.Template.Name == Forge) != 1)
            throw new InvalidDataException("Unsupported first crafting quest or missing forge.");
        if (first.Tasks[^1].Kind != "gotoquestgiver" || tower.Tasks.Take(5).Any(t => t.Kind != "gotolocation")
            || tower.Tasks[5].Kind != "or" || tower.Tasks[6].Kind != "gotoquestgiver"
            || (string?)quests[tower.Id].Attribute("previousquests") != FirstQuest
            || (string?)quests[gathering.Id].Attribute("previousquests") != TowerQuest
            || gathering.Tasks[0].Kind != "gotolocation" || gathering.Tasks[2].Kind != "gotoquestgiver"
            || !gathering.Tasks[1].Acquisitions.Select(a => (a.Item, a.Count)).SequenceEqual(new[] { ("T1_ROCK", 6), ("T1_WOOD", 6) }))
            throw new InvalidDataException("Opening quest chain differs from the inspected version.");
        return new OpeningTutorialData { Tutorial = tutorial, Map = map, Start = starts[0], Crafter = crafters[0],
            CrafterType = npcType, First = first, Tower = tower, Gathering = gathering, Crafting = crafting,
            Journey = TutorialJourneyData.Load(folder, store) };
    }

    public Character Create(string name, ItemStore items, GameData board)
    {
        var c = new Character { Name = name, Cluster = Tutorial.StartCluster, PositionX = Start.Position.X,
            PositionY = Start.Position.Y, Direction = Start.Direction, Progression = board.Board?.FairStart()
                ?? throw new InvalidDataException("Tutorial requires destiny board data."),
            Tutorial = new OpeningTutorialProgress(0, Guid.NewGuid(), DateTime.UtcNow.Ticks) };
        foreach (var itemName in Tutorial.StartingItems)
        {
            var item = items.Find(itemName) ?? throw new InvalidDataException($"Missing tutorial item {itemName}.");
            var slot = Array.FindIndex(Inventory.SlotNames, s => s.Equals(item.Slot, StringComparison.OrdinalIgnoreCase));
            if (slot < 0 || c.Equipment[slot] is not null) throw new InvalidDataException($"Invalid tutorial slot for {itemName}.");
            c.Equipment[slot] = new InventoryItem(itemName);
        }
        return c;
    }
}
