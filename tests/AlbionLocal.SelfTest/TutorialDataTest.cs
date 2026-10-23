using System.Xml.Linq;
using AlbionLocal.Server.Data;
using static AlbionLocal.SelfTest.AlbionTests;

namespace AlbionLocal.SelfTest;

public static class TutorialDataTest
{
    public static IEnumerable<(string, Action)> All =>
    [
        ("tutorial data: client ids preserve file order, branches and standalone states", Numbering),
        ("tutorial data: duplicate names, dangling references and cycles are refused", References),
        ("tutorial data: malformed settings cannot become default rewards or destinations", InvalidSettings),
        ("tutorial data: definitions and returned collections do not share mutable XML", Immutable),
        ("tutorial data: installed client definitions and starting equipment resolve", Installed),
    ];

    // Synthetic names, items and GUID; no client data is included in this fixture.
    private static XElement Fixture() => XElement.Parse("""
        <tutorial learningpointsifskipped="12">
          <clustergroup startcluster="island-a"><cluster id="island-b" /></clustergroup>
          <travelplanner id="11111111-2222-3333-4444-555555555555@island-b">
            <target clusterid="mainland" title="@SYNTHETIC_TITLE" desc="@SYNTHETIC_DESCRIPTION" />
          </travelplanner>
          <startstate uniquename="BEGIN" triggerhint="MOVE" hidedestinyboard="true" />
          <startgear><item itemid="SYNTHETIC_SHOES" /></startgear>
          <state uniquename="SIDE" previousstate="BEGIN" showdestinyboardflyout="true" />
          <state uniquename="BRANCH" previousstate="BEGIN" resetquestonrevisit="true" />
          <state uniquename="INDEPENDENT" audiochange="SYNTHETIC_SOUND" />
          <endstate uniquename="DONE" previousstate="SIDE" />
          <unlockachievementskippedtutorial><achievement id="SYNTHETIC_START" masterylevel="1" /></unlockachievementskippedtutorial>
        </tutorial>
        """);

    private static void Check(bool condition, string message) => AlbionTests.Check(condition, message);

    private static void Numbering()
    {
        var data = TutorialData.Load(Fixture());
        Check(data.States.Select(s => s.Id).SequenceEqual([0, 1, 2, 3, 4]) && data.Start.Id == 0 && data.End.Id == 4,
            "only state elements take ids, starting at zero");
        Check(data.Find(2) is { Name: "BRANCH", PreviousState: "BEGIN", ResetQuestOnRevisit: true }
            && data.Find("SIDE") is { Id: 1, ShowDestinyBoardFlyout: true }, "branches and flags are kept");
        Check(data.Find(-1) is null && data.Find(5) is null && data.Find("missing") is null, "unknown ids do not map to a valid state");
        Check(data.Clusters.SetEquals(["island-a", "island-b"]) && data.StartingItems.SequenceEqual(["SYNTHETIC_SHOES"])
            && data.LearningPointsIfSkipped == 12 && data.SkippedUnlocks is [{ Achievement: "SYNTHETIC_START", MasteryLevel: 1 }],
            "start, gear and skip rewards parsed without granting them");
        Check(TutorialData.Load(Fixture()).Start.Id == 0, "a new load starts its own counter");
    }

    private static void Reject(XElement root, string expected)
    {
        try { TutorialData.Load(root); }
        catch (InvalidDataException e)
        {
            Check(e.Message.Contains("tutorial.bin") && e.Message.Contains(expected), $"useful failure: {e.Message}");
            return;
        }
        throw new Exception("invalid tutorial accepted: " + expected);
    }

    private static void References()
    {
        var root = Fixture();
        root.Elements("state").First().SetAttributeValue("uniquename", "BEGIN");
        Reject(root, "duplicate state");
        root = Fixture();
        root.Elements("state").First().SetAttributeValue("previousstate", "absent");
        Reject(root, "missing previousstate");
        root = Fixture();
        root.Element("startstate")!.SetAttributeValue("previousstate", "SIDE");
        Reject(root, "cyclic previousstate");
        root = Fixture();
        root.Element("endstate")!.Remove();
        Reject(root, "endstate");
        root = Fixture();
        root.Add(new XElement(root.Element("clustergroup")!));
        Reject(root, "clustergroup");
    }

    private static void InvalidSettings()
    {
        var root = Fixture();
        root.Element("startstate")!.SetAttributeValue("hidedestinyboard", "yes-please");
        Reject(root, "hidedestinyboard");
        root = Fixture(); root.Element("startstate")!.SetAttributeValue("hidedestinyboard", ""); Reject(root, "hidedestinyboard");
        root = Fixture(); root.SetAttributeValue("learningpointsifskipped", "-1"); Reject(root, "learningpointsifskipped");
        root = Fixture(); root.Element("travelplanner")!.SetAttributeValue("id", "not-a-reference"); Reject(root, "GUID@cluster");
        root = Fixture(); root.Element("travelplanner")!.SetAttributeValue("id", "11111111-2222-3333-4444-555555555555@missing"); Reject(root, "non-tutorial");
        root = Fixture(); root.Element("travelplanner")!.Elements("target").Remove(); Reject(root, "no destinations");
        root = Fixture(); root.Element("unlockachievementskippedtutorial")!.Element("achievement")!.SetAttributeValue("masterylevel", "0");
        Reject(root, "masterylevel");
    }

    private static void Immutable()
    {
        var root = Fixture();
        var data = TutorialData.Load(root);
        root.Element("startstate")!.SetAttributeValue("uniquename", "CHANGED");
        Check(data.Start.Name == "BEGIN" && data.Find("CHANGED") is null, "no retained mutable XML");
        try { ((IList<TutorialData.State>)data.States)[0] = data.End; throw new Exception("state list was mutable"); }
        catch (NotSupportedException) { }
    }

    private static void Installed()
    {
        var data = TutorialData.Load(GameDataFile.Open(ClientGameDataDir(), "tutorial.bin").Root());
        Check(data.Start is { Id: 0, Name: "STARTTUTORIAL" } && data.Find("FIRSTSTEPS")?.Id == 1,
            "client numbering pinned for the inspected start states");
        Check(data.StartingItems.All(i => Client.Items.Find(i) is not null), "starting items resolve against this client");
        var world = Client.Clusters!.World;
        Check(data.Clusters.All(id => world.Find(id) is not null) && data.Destinations.All(d => world.Find(d.Cluster) is not null),
            "all map and destination references exist");
    }
}
