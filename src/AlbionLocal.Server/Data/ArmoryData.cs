using System.Globalization;
using System.Xml.Linq;
using AlbionLocal.Server.Game;

namespace AlbionLocal.Server.Data;

/// <summary>Eligibility rules from the installed armory.bin. Unknown requirements fail closed.</summary>
public sealed class ArmoryData
{
    public sealed record Activity(int Id, string Name, int MinimumUses, XElement Requirements);
    public IReadOnlyList<Activity> Activities { get; private init; } = [];
    public Activity? Find(int id) => Activities.FirstOrDefault(a => a.Id == id);
    public static ArmoryData Load(string folder)
    {
        if (!File.Exists(Path.Combine(folder, "armory.bin"))) return new();
        var entries = GameDataFile.Open(folder, "armory.bin").ElementsIn("activities").Select(x => x.Element).ToArray();
        return new() { Activities = entries.Select((e, i) => new Activity(i, (string?)e.Attribute("uniquename") ?? "",
            Math.Max(1, (int?)e.Element("scoreweights")?.Attribute("minuses") ?? 1),
            e.Element("datasetrequirements") ?? new XElement("unsupported"))).ToArray() };
    }
    public bool Accepts(ArmorySample sample) => Find(sample.Activity) is { } a && Matches(a.Requirements, sample);
    private static bool Matches(XElement e, ArmorySample s) => e.Name.LocalName switch
    {
        "datasetrequirements" or "and" => e.HasElements && e.Elements().All(c => Matches(c, s)),
        "or" => e.Elements().Any(c => Matches(c, s)),
        "duration" => s.DurationSeconds / 60 >= Number(e, "mindurationminutes"),
        "metric" => Enum.TryParse<ArmoryMetric>((string?)e.Attribute("name"), true, out var m)
            && s.Value(m) >= Number(e, "minvalue"),
        _ => false,
    };
    private static decimal Number(XElement e, string name) => decimal.TryParse((string?)e.Attribute(name),
        NumberStyles.Float, CultureInfo.InvariantCulture, out var d) && d >= 0 ? d : decimal.MaxValue;
}
