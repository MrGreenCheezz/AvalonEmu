using System.Xml.Linq;
using AlbionLocal.Server.Data;
using static AlbionLocal.SelfTest.AlbionTests;

namespace AlbionLocal.SelfTest;

public static class TutorialJourneyDataTest
{
    public static IEnumerable<(string, Action)> All =>
    [
        ("tutorial journey data: all island branches, task trees, NPC tags and departure resolve", Installed),
        ("tutorial journey data: authored trades, parallel counts and alternatives survive parsing", Conditions),
        ("tutorial journey data: unsupported conditions, functions, actions and rewards fail explicitly", Unsupported),
    ];
    private static TutorialJourneyData Load() => TutorialJourneyData.Load(ClientGameDataDir(), Client.Clusters!);
    private static void Check(bool value, string message) => AlbionTests.Check(value, message);
    private static TutorialJourneyData.Quest Quest(TutorialJourneyData data, string suffix) => data.Find(TutorialJourneyData.Prefix + suffix)!;
    private static void Installed()
    {
        var data = Load();
        Check(TutorialJourneyData.FunctionIds["BuyJourney"] == 15 && TutorialJourneyData.FunctionIds["FinishTutorial"] == 19
            && TutorialJourneyData.FunctionIds["OpenClusterMap"] == 2, "native usefunction ids preserve authored UI operations");
        Check(data.Quests.Count == 17 && data.Quests.Sum(q => q.Tasks.Count) == 92, "all authored remaining variants and tasks loaded");
        Check(data.Successors(OpeningTutorialData.CraftingQuest).Single().Name.EndsWith("_E"), "D leads to E");
        Check(data.Successors(TutorialJourneyData.Prefix + "L").Select(q => q.Name[^2..]).SequenceEqual(["MA", "MB", "MC"]),
            "all camp alternatives retained");
        Check(Quest(data, "NA").Previous.Count == 3 && Quest(data, "NC").Previous.Count == 3, "weapon branches retain any-M prerequisites");
        Check(data.Maps.Keys.Order().SequenceEqual(["1008", "1009", "1010", "1011"]), "all tutorial scenes loaded");
        var crafter = data.QuestGivers.Single(g => g.Reference == "WOUNDEDCRAFTER_SECONDENCOUNTER");
        Check(crafter.Cluster == "1011" && crafter.Tags.Contains("WOUNDEDCRAFTER") && crafter.OfferTags.Contains("woundedcrafter"),
            "second crafter resolves through tags, not identical object reference");
        var tia = data.QuestGivers.Single(g => g.Reference == "CAPTAIN_TIA_STANDING");
        Check(tia.Cluster == "1010" && tia.OfferTags.SequenceEqual(["captaintia2"]), "final captain has distinct native offer tag");
        Check(data.Tutorial.TravelPlanner.EndsWith("@1010") && data.Tutorial.Destinations.Select(d => d.Cluster)
            .SequenceEqual(["1006", "4006", "0004", "3002", "2002"]), "departure and five starter destinations resolve");
        Check(Quest(data, "Q").Tasks[0].Conditions.Single().Attribute("seconds") == "900"
            && Quest(data, "Q").Tasks[1].Conditions.Single().Attribute("function") == "BuyJourney", "Q gates actual departure, not mere NPC proximity");
        Check(data.UnresolvedVisualMarkers.Order().SequenceEqual(["T2_FIBER", "T2_HIDE"]), "missing client visual circles are explicit and do not turn real gathering into proximity");
        var hintElements = GameDataFile.Open(ClientGameDataDir(), "hints.bin").Root().Elements()
            .Where(h => h.Name.LocalName is "hint" or "texthint").ToArray();
        Check(data.Hints.Count == hintElements.Length && data.Hints[(string)hintElements[0].Attribute("uniquename")!] == 0
            && data.Hints["CLEAR"] == Array.FindIndex(hintElements, h => (string?)h.Attribute("uniquename") == "CLEAR"),
            "hint and texthint share native registration order from zero");
    }
    private static void Conditions()
    {
        var data = Load();
        var e = Quest(data, "E");
        Check(e.Tasks[3].Conditions.Select(c => c.Kind).SequenceEqual(["killmob", "unlockachievement"]), "combat and destiny unlock are parallel conditions");
        var trade = Quest(data, "L").Tasks[4].Conditions.Single();
        Check(trade.Kind == "gotoquestgiver" && trade.Attribute("mustaccept") == "true"
            && trade.Children.Select(c => (c.Attribute("itemid")!, c.Attribute("count")!))
                .SequenceEqual(new[] { ("T2_PLANKS", "6"), ("T2_METALBAR", "6"), ("T2_CLOTH", "4"), ("T2_LEATHER", "4") }),
            "ship turn-in preserves explicit acceptance and all four item costs");
        Check(Quest(data, "L").Tasks[5].Conditions.Single().Attribute("seconds") == "10", "repair pause preserved separately");
        Check(Quest(data, "MA").Tasks[2].Conditions.Single().Children.Count == 3, "camp OR retains all three real alternatives");
        Check(Quest(data, "OB").Tasks[1].Conditions.Count == 4
            && Quest(data, "OB").Tasks[1].Conditions[3].Children.Count == 2, "offhand alternatives coexist with three armor choices");
        Check(Quest(data, "P").Tasks[2].Conditions.Single().Attribute("triggeronleave") == "true"
            && Quest(data, "P").Tasks[3].Conditions.Single().Attribute("count") == "10", "fort departure and ten actual tier-two kills retained");
        Check((string?)Quest(data, "P").Definition.Element("rewards")?.Element("learningpoints")?.Attribute("amount") == "100"
            && (string?)Quest(data, "F").Definition.Element("rewards")?.Element("silver")?.Attribute("absolute") == "3", "authored learning points and silver kept without granting on read");
    }
    private static void Unsupported()
    {
        var folder = ClientGameDataDir();
        var original = GameDataFile.Open(folder, "quests.bin").Root();
        var tutorial = TutorialData.Load(GameDataFile.Open(folder, "tutorial.bin").Root());
        var npc = GameDataFile.Open(folder, "questgiverobjects.bin").Root();
        void Reject(Action<XElement> mutate, string expected)
        {
            var root = new XElement(original);
            var quest = root.Elements().Single(q => (string?)q.Attribute("uniquename") == TutorialJourneyData.Prefix + "E");
            mutate(quest);
            try { TutorialJourneyData.Load(root, tutorial, Client.Clusters!, npc); }
            catch (InvalidDataException exception)
            { Check(exception.Message.Contains(expected), "failure identifies unsupported behavior"); return; }
            throw new Exception("unsupported journey condition accepted: " + expected);
        }
        Reject(q => q.Descendants("missions").First().Add(new XElement("syntheticcondition")), "unsupported condition");
        Reject(q => q.Descendants("missions").First().Add(new XElement("usefunction", new XAttribute("function", "SyntheticUnknown"))), "unsupported function");
        Reject(q => q.Descendants("preactions").First().Add(new XElement("syntheticaction")), "unsupported action");
        Reject(q => q.Element("rewards")!.Add(new XElement("syntheticreward")), "unsupported reward");
    }
}
