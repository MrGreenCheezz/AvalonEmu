using System.Collections.Frozen;
using System.Globalization;
using System.Xml.Linq;

namespace AlbionLocal.Server.Data;

/// <summary>
/// Immutable definitions from tutorial.bin. The client resets its state counter, then numbers startstate/state/endstate
/// from zero in document order (345319: bhr.n(), bhr.n(XmlElement, bool), bgh.c8). Other elements do not take an id.
/// PreviousState is a client hint-history link, not proof of an allowed server-side quest transition.
/// </summary>
public sealed class TutorialData
{
    public sealed record State(int Id, string Name, bool IsStart, bool IsEnd, string? PreviousState,
        string? TriggerHint, string? InfoButtonHint, string? AudioChange, bool HideDestinyBoard,
        bool ShowDestinyBoardFlyout, bool ResetQuestOnRevisit);
    public sealed record Destination(string Cluster, string Title, string Description);
    public sealed record Unlock(string Achievement, int MasteryLevel);

    public string StartCluster { get; }
    public IReadOnlySet<string> Clusters { get; }
    public IReadOnlyList<State> States { get; }
    public State Start { get; }
    public State End { get; }
    public string TravelPlanner { get; }
    public IReadOnlyList<Destination> Destinations { get; }
    public IReadOnlyList<string> StartingItems { get; }
    public IReadOnlyList<Unlock> SkippedUnlocks { get; }
    public long LearningPointsIfSkipped { get; }
    private readonly FrozenDictionary<string, State> _states;

    private TutorialData(string startCluster, IEnumerable<string> clusters, List<State> states, string travelPlanner,
        List<Destination> destinations, List<string> items, List<Unlock> unlocks, long learningPoints)
    {
        StartCluster = startCluster;
        Clusters = clusters.Prepend(startCluster).ToFrozenSet(StringComparer.Ordinal);
        States = states.AsReadOnly();
        _states = states.ToFrozenDictionary(s => s.Name, StringComparer.Ordinal);
        Start = states.Single(s => s.IsStart);
        End = states.Single(s => s.IsEnd);
        TravelPlanner = travelPlanner;
        Destinations = destinations.AsReadOnly();
        StartingItems = items.AsReadOnly();
        SkippedUnlocks = unlocks.AsReadOnly();
        LearningPointsIfSkipped = learningPoints;
    }

    public State? Find(string name) => _states.GetValueOrDefault(name);
    public State? Find(int id) => id >= 0 && id < States.Count ? States[id] : null;

    /// <summary>Parse and validate definitions only. This does not start or advance a player's tutorial.</summary>
    public static TutorialData Load(XElement root)
    {
        if (root.Name != "tutorial") throw Bad("expected tutorial root");
        var group = Single(root, "clustergroup");
        var startCluster = Required(group, "startcluster");
        var clusters = group.Elements("cluster").Select(e => Required(e, "id")).ToList();
        Unique(clusters, "cluster");
        var planner = Single(root, "travelplanner");
        var plannerId = Required(planner, "id");
        var address = plannerId.Split('@');
        if (address.Length != 2 || !Guid.TryParse(address[0], out _) || string.IsNullOrWhiteSpace(address[1]))
            throw Bad("travelplanner id must be a GUID@cluster reference");
        if (!clusters.Prepend(startCluster).Contains(address[1], StringComparer.Ordinal))
            throw Bad($"travelplanner refers to non-tutorial cluster {address[1]}");

        var states = root.Elements().Where(e => e.Name.LocalName is "startstate" or "state" or "endstate")
            .Select((e, id) => new State(id, Required(e, "uniquename"), e.Name == "startstate", e.Name == "endstate",
                Optional(e, "previousstate"), Optional(e, "triggerhint"), Optional(e, "infobuttonhint"), Optional(e, "audiochange"),
                Bool(e, "hidedestinyboard"), Bool(e, "showdestinyboardflyout"), Bool(e, "resetquestonrevisit"))).ToList();
        if (states.Count(s => s.IsStart) != 1 || states.Count(s => s.IsEnd) != 1)
            throw Bad("exactly one startstate and one endstate required");
        Unique(states.Select(s => s.Name), "state");
        var byName = states.ToDictionary(s => s.Name, StringComparer.Ordinal);
        foreach (var state in states)
        {
            var visited = new HashSet<string>(StringComparer.Ordinal) { state.Name };
            var previous = state.PreviousState;
            while (previous is not null)
            {
                if (!byName.TryGetValue(previous, out var parent)) throw Bad($"state {state.Name}: missing previousstate {previous}");
                if (!visited.Add(previous)) throw Bad($"state {state.Name}: cyclic previousstate {previous}");
                previous = parent.PreviousState;
            }
        }

        var destinations = planner.Elements("target").Select(e =>
            new Destination(Required(e, "clusterid"), Required(e, "title"), Required(e, "desc"))).ToList();
        if (destinations.Count == 0) throw Bad("travelplanner has no destinations");
        Unique(destinations.Select(d => d.Cluster), "destination");
        var items = Single(root, "startgear").Elements("item").Select(e => Required(e, "itemid")).ToList();
        Unique(items, "starting item");
        var unlocks = Single(root, "unlockachievementskippedtutorial").Elements("achievement")
            .Select(e => new Unlock(Required(e, "id"), checked((int)Number(e, "masterylevel", 1, int.MaxValue)))).ToList();
        Unique(unlocks.Select(u => u.Achievement), "skipped achievement");
        return new TutorialData(startCluster, clusters, states, plannerId, destinations, items, unlocks,
            Number(root, "learningpointsifskipped", 0, long.MaxValue));
    }

    private static XElement Single(XElement parent, string name)
    {
        var elements = parent.Elements(name).ToList();
        return elements.Count == 1 ? elements[0] : throw Bad($"exactly one {name} required");
    }

    private static string Required(XElement element, string name) => Optional(element, name)
        ?? throw Bad($"{element.Name}: missing {name}");
    private static string? Optional(XElement element, string name) => (string?)element.Attribute(name) is { } value
        && !string.IsNullOrWhiteSpace(value) ? value.Trim() : null;
    private static bool Bool(XElement element, string name) => element.Attribute(name) is not { } attribute ? false
        : bool.TryParse(attribute.Value, out var result) ? result : throw Bad($"{element.Name}: invalid {name}");
    private static long Number(XElement element, string name, long min, long max) =>
        long.TryParse(Required(element, name), NumberStyles.None, CultureInfo.InvariantCulture, out var result) && result >= min && result <= max
            ? result : throw Bad($"{element.Name}: invalid {name}");
    private static void Unique(IEnumerable<string> values, string kind)
    {
        var names = new HashSet<string>(StringComparer.Ordinal);
        foreach (var value in values) if (!names.Add(value)) throw Bad($"duplicate {kind} {value}");
    }
    private static InvalidDataException Bad(string reason) => new($"tutorial.bin: {reason}");
}
