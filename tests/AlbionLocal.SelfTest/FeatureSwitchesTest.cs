using AlbionLocal.Server;
using AlbionLocal.Server.Configuration;
using AlbionLocal.Server.Game;
using AlbionLocal.Server.Protocol;

namespace AlbionLocal.SelfTest;

/// <summary>
/// StageR feature switches (analysis/work/stageR/switches/SPEC.md): the default table equals the official FeatureSwitchInfo table of the
/// captures (but [151], kept off: no shop), and Client:FeatureSwitches overrides it by name or index.
/// </summary>
public static class FeatureSwitchesTest
{
    public static IEnumerable<(string, Action)> All =>
    [
        ("feature switches: the default table is the official one (13 corrections of the client defaults), 151 off, event 503", Defaults),
        ("feature switches: Client:FeatureSwitches by name or index, over Party:Finder; unknown names, 227 and 112 refused", Overrides),
    ];

    private static void Check(bool condition, string message) => AlbionTests.Check(condition, message);

    /// <summary>
    /// The off entries of FeatureSwitchInfo key 0 in the 14 decoded events of captures 20261015_113433, 20261015_114814 and
    /// 20261016_002835 (all identical): every other entry is on.
    /// </summary>
    private static readonly int[] CapturedOff =
        [16, 24, 41, 46, 56, 70, 77, 83, 87, 100, 101, 103, 105, 117, 132, 135, 155, 156, 161, 177, 180, 192, 194, 200, 202, 203, 207, 208,
         210, 212, 217, 218, 219, 220, 223];

    private static void Defaults()
    {
        var captured = Enumerable.Range(0, FeatureSwitches.Count).Select(i => !CapturedOff.Contains(i)).ToArray();
        Check(FeatureSwitches.Official().SequenceEqual(captured), "Official() is the captured table, entry for entry");
        var client = FeatureSwitches.Defaults();
        var differing = Enumerable.Range(0, FeatureSwitches.Count).Where(i => client[i] != captured[i]).ToArray();
        Check(differing.SequenceEqual([19, 24, 25, 26, 41, 46, 77, 83, 100, 151, 171, 177, 217, 218]),
              $"the captured table differs from the client defaults in 14 entries (got {string.Join(",", differing)})");
        var sent = LoginWorld.Features(new ServerConfig());
        Check(sent.Length == FeatureSwitches.Count && differing.All(i => sent[i] == captured[i] || i == 151) && !sent[151]
              && Enumerable.Range(0, FeatureSwitches.Count).All(i => sent[i] == captured[i] || i == 151)
              && sent[FeatureSwitches.DynamicTemplates] && sent[FeatureSwitches.PartyFinder],
              "the default table equals the official one in every entry, [151] (the Albion Store's learning points) off");
        Check(FeatureSwitches.Names.Count == FeatureSwitches.Count && FeatureSwitches.Names[171] == "PartyFinder" && FeatureSwitches.Names[226] == "XRayOccluders"
              && FeatureSwitches.IndexOf("partyfinder") == 171 && FeatureSwitches.IndexOf("151") == 151 && FeatureSwitches.IndexOf("227") == -1
              && FeatureSwitches.IndexOf("NoSuchSwitch") == -1, "the name table (227 Features names) and IndexOf");
        var ev = LoginWorld.FeatureSwitchInfo(new ServerConfig());
        Check(ev.b91 is { } t && t.SequenceEqual(sent) && ev.b95 == """{"PC":{"LimitsByContext":{"SpellVfx":-1}}}""" && ev.b92 is null && ev.b93 is null
              && ev.b94 is null && ev.b96 is null, "FeatureSwitchInfo (503): key 0 the table, key 4 the captured VFX limits, nothing else");
        Check(LoginWorld.Describe(new ServerConfig()) == "[151] DestinyBoardShowBuyLPButtonAndCTA off", "the log names what differs from official");
    }

    private static void Overrides()
    {
        static ServerConfig Load(string json) => EditableConfiguration.Parse(json).Validate(Environment.CurrentDirectory);
        var c = Load("""{"Client":{"FeatureSwitches":{"ClusterValueOverview":true,"151":true,"partyfinder":false}}}""");
        var t = LoginWorld.Features(c);
        Check(c.FeatureSwitchOverrides.Count == 3 && t[24] && t[151] && !t[171] && LoginWorld.FeatureSwitchInfo(c).b91!.SequenceEqual(t),
              "Client:FeatureSwitches sets switches by name (any case) and index, in the Login table and event 503");
        var both = Load("""{"Party":{"Finder":false},"Client":{"FeatureSwitches":{"PartyFinder":true}}}""");
        Check(LoginWorld.Features(both)[171] && !LoginWorld.Features(Load("""{"Party":{"Finder":false}}"""))[171],
              "Party:Finder still switches 171; Client:FeatureSwitches wins over it");
        foreach (var json in new[]
                 {
                     """{"Client":{"FeatureSwitches":{"NoSuchSwitch":true}}}""", """{"Client":{"FeatureSwitches":{"227":true}}}""",
                     """{"Client":{"FeatureSwitches":{"DynamicTemplates":false}}}""", """{"Client":{"FeatureSwitches":{"112":true}}}""",
                     """{"Client":{"FeatureSwitches":{"24":"maybe"}}}""", """{"Client":{"FeatureSwitches":{"24":true,"ClusterValueOverview":false}}}""",
                 })
        {
            var refused = false;
            try { Load(json); }
            catch (Exception e) when (e is InvalidDataException or System.Text.Json.JsonException) { refused = true; }
            Check(refused, $"config error: {json}");
        }
    }
}
