using System.Xml.Linq;

namespace AlbionLocal.Server.Data;

/// <summary>Read-only preflight for tutorial development. Successful data checks do not imply working tutorial gameplay.</summary>
public static class TutorialCheck
{
    public static int Run(string folder, TextWriter output, bool details = false)
    {
        var failed = 0;
        void Check(bool ok, string message)
        {
            output.WriteLine($"{(ok ? "PASS" : "FAIL")} {message}");
            if (!ok) failed++;
        }

        var files = new[] { "tutorial.bin", "quests.bin", "albionjournalquests.bin", "questgiverobjects.bin",
            "questgiverreferences.bin", "questgivertypes.bin", "hints.bin", "items.bin" }
            .ToDictionary(name => name, name => GameDataFile.Open(folder, name));
        var roots = files.Values.Where(f => f.Name != "items.bin").ToDictionary(f => f.Name, f => f.Root());
        var data = TutorialData.Load(roots["tutorial.bin"]);
        var store = new ClusterStore(folder);
        var items = ItemStore.Load(files["items.bin"]);
        output.WriteLine("Tutorial definition checks (read-only; no listeners or saves opened)");
        output.WriteLine($"Start: {data.StartCluster}; {data.States.Count} states; {data.StartingItems.Count} starting items; {data.Destinations.Count} destinations");

        foreach (var id in data.Clusters.OrderBy(id => id == data.StartCluster ? 0 : 1).ThenBy(id => id, StringComparer.Ordinal))
        {
            if (store.World.Find(id) is not { } info) { Check(false, $"tutorial map {id} exists"); continue; }
            var map = store.Get(id);
            Check(map.Problems.Count == 0, $"map {id} ({info.DisplayName}) resolves; {map.Problems.Count} problems");
            if (details)
            {
                output.WriteLine($"  {map.LocationMarkers.Count} location markers, {info.QuestGivers.Count} quest givers, {map.Exits.Count} exits");
                if (id == data.StartCluster) DescribeOpening(map, output);
            }
            if (id == data.StartCluster)
            {
                Check(map.Critical.Count(c => c.Type == "startpoint") == 1,
                    "starting map has one explicit startpoint (distinct from death respawn)");
                foreach (var questName in new[] { "MISSIONS_T2_TUTORIAL_TEST_A", "MISSIONS_T2_TUTORIAL_TEST_B" })
                {
                    var quests = roots["quests.bin"].Elements("quest")
                        .Where(q => (string?)q.Attribute("uniquename") == questName).ToArray();
                    Check(quests.Length == 1, $"opening quest {questName} exists exactly once");
                    if (quests.Length != 1) continue;
                    // Validate references, not the runtime meaning/numbering of quest tasks. A marker may
                    // legitimately have several positions; do not collapse it with Single/First.
                    foreach (var marker in quests[0].Descendants("tasks").DescendantsAndSelf().Attributes("locationmarkerid")
                        .Select(a => a.Value).Distinct(StringComparer.Ordinal))
                        Check(map.LocationMarkers.Any(m => m.LocationId == marker), $"{questName}: marker {marker} resolves");
                }
            }
        }
        foreach (var item in data.StartingItems) Check(items.Find(item) is not null, $"starting item {item} exists");
        foreach (var destination in data.Destinations)
            Check(store.World.Find(destination.Cluster) is not null, $"destination {destination.Cluster} exists");

        var planner = data.TravelPlanner.Split('@');
        Check(store.World.Find(planner[1])?.Travelpoints.Any(p => p.Id == Guid.Parse(planner[0])) == true,
            "tutorial travel planner exists in its declared map");

        var hintNames = roots["hints.bin"].DescendantsAndSelf()
            .Select(e => (string?)e.Attribute("uniquename")).Where(n => n is not null).ToHashSet(StringComparer.Ordinal);
        foreach (var hint in data.States.SelectMany(s => new[] { s.TriggerHint, s.InfoButtonHint }).OfType<string>().Distinct(StringComparer.Ordinal))
            Check(hintNames.Contains(hint), $"hint {hint} exists");

        if (details)
        {
            foreach (var state in data.States)
                output.WriteLine($"  state {state.Id}: {state.Name}; previous={state.PreviousState ?? "(none)"}; hint={state.TriggerHint ?? "(none)"}");
            foreach (var file in files.Values) output.WriteLine($"  {file.Name}: sha256 {file.Sha256}");
        }
        foreach (var file in files.Values.Where(f => f.Name is "quests.bin" or "albionjournalquests.bin"))
        {
            var root = roots[file.Name];
            output.WriteLine($"  {file.Name}: {root.Elements("quest").Count()} quest definitions (not executed by this check)");
        }
        output.WriteLine($"Definition checks: {failed} failed. Interactive tutorial and quest execution remain unverified.");
        return failed == 0 ? 0 : 1;
    }

    private static void DescribeOpening(ClusterTemplate map, TextWriter output)
    {
        output.WriteLine("  Opening coordinates: movement plane = x,z; height = y; world positions already include template placement.");
        foreach (var point in map.Critical.Where(c => c.Type == "startpoint"))
            output.WriteLine(FormattableString.Invariant($"  startpoint {point.Key}: x={point.Position.X}, z={point.Position.Y}, height={point.Height}, direction={point.Direction}"));
        foreach (var point in map.Critical.Where(c => c.Type == "questgiverobject"))
            output.WriteLine(FormattableString.Invariant($"  npc {point.Component.Attribute("reference")?.Value}: x={point.Position.X}, z={point.Position.Y}, height={point.Height}, direction={point.Direction}; guid={point.Guid}"));
        foreach (var marker in map.LocationMarkers.OrderBy(m => m.LocationId, StringComparer.Ordinal))
            output.WriteLine(FormattableString.Invariant($"  marker {marker.LocationId}: x={marker.Position.X}, z={marker.Position.Y}, height={marker.Height}, radius={marker.Radius}; key={marker.Key}"));
        output.WriteLine("  These are data references, not proof that any quest is active, displayed, or completed in the client.");
    }
}
