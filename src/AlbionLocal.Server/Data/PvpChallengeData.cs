using System.Globalization;
using System.Xml.Linq;

namespace AlbionLocal.Server.Data;

/// <summary>The personal Conqueror's Challenge rules of the installed regional pvpchallenge/times files.
/// These are independent of the configurable guild Might multiplier.</summary>
public sealed class PvpChallengeData
{
    public sealed record Source(short Id, string Name, string Objective, string Personal, double Factor, double Exponent,
        double FavorFactor, double SoloFactor, double Might, double Favor, IReadOnlyDictionary<string, double> Zones)
    {
        public bool UseBracketBonus { get; init; } = true;
        public bool SeasonOnly { get; init; }
        public int MinimumDivisor { get; init; } = 1;
        public double ZoneFactor(string? warning) => warning is not null && Zones.TryGetValue(warning, out var f) ? f : 1;
        public string Stat(GvgSeason season) => Personal.Length > 0 ? Personal
            : season.Might.FirstOrDefault(c => c.Objectives.Contains(Objective))?.Name ?? "";
    }
    public sealed record FavorReward(long Might, long Favor, bool BoughtPremium, bool Premium, bool NonPremium);
    public sealed record Reward(string Item, string? Loot, bool BoughtPremium, bool Premium, bool NonPremium);
    public sealed record RewardOverride(string Bracket, int Rank, bool Participation, Reward Reward);
    public sealed record SeasonReward(long Might, int Line, Reward Reward, IReadOnlyList<RewardOverride> Overrides);
    public static readonly PvpChallengeData None = new();
    public string? WeeklyItem { get; private init; }
    public long WeeklyRequirement { get; private init; }
    public TimeSpan ResetTime { get; private init; }
    public IReadOnlyList<FavorReward> WeeklyFavor { get; private init; } = [];
    public IReadOnlyDictionary<string, Source> Fame { get; private init; } = new Dictionary<string, Source>();
    public IReadOnlyDictionary<string, Source> Objectives { get; private init; } = new Dictionary<string, Source>();
    public IReadOnlyDictionary<string, IReadOnlyList<SeasonReward>> Seasons { get; private init; } = new Dictionary<string, IReadOnlyList<SeasonReward>>();

    /// <summary>345319 bep's calendar calculation (628229): (DayOfWeek + 1) % 7, at starttimeref.
    /// The last boundary is Saturday; incomplete challenges carry their progress across it.</summary>
    public long WeekStart(DateTime utc)
    {
        var start = utc.Date.AddDays(-(((int)utc.DayOfWeek + 1) % 7)) + ResetTime;
        if (start > utc) start = start.AddDays(-7);
        return start.Ticks;
    }

    public static PvpChallengeData Load(XElement? root, XElement times)
    {
        if (root?.Element("weeklychallenge") is not { } weekly) return None;
        var resetName = (string?)weekly.Attribute("starttimeref");
        var time = times.Elements("Time").FirstOrDefault(t => (string?)t.Attribute("uniquename") == resetName);
        if (!TimeSpan.TryParse((string?)time?.Attribute("time"), CultureInfo.InvariantCulture, out var reset)) return None;
        var fame = new Dictionary<string, Source>(StringComparer.Ordinal);
        var objectives = new Dictionary<string, Source>(StringComparer.Ordinal);
        short id = 0; // Client bep.b ctor 649709 increments from zero, in the objectives reader's file order.
        foreach (var e in root.Element("pvpchallengeobjectives")?.Elements() ?? [])
        {
            if (e.Name.LocalName is not ("pve" or "gathering" or "fishing" or "territoryenergycollection" or "tracking" or "objective")) continue;
            var name = (string?)e.Attribute("uniquename") ?? e.Name.LocalName;
            var source = new Source(id++, name, (string?)e.Attribute("objectivetype") ?? "", (string?)e.Attribute("personaltype") ?? "",
                D(e, "fametomightfactor"), D(e, "famescaling", 1), D(e, "mighttofavorfactor"), D(e, "solorandomdungeonfactor", 1),
                D(e, "might"), D(e, "favor"), e.Elements("dangerbonustypemultiplier").ToDictionary(
                    z => (string?)z.Attribute("pvpwarningtype") ?? "", z => D(z, "mightandfavormultiplier", 1), StringComparer.Ordinal))
                {
                    UseBracketBonus = (string?)e.Attribute("usebracketbonus") != "false",
                    SeasonOnly = (string?)e.Attribute("seasononly") == "true",
                    MinimumDivisor = (int)Math.Clamp(D(e, "mindivisor", 1), 1, short.MaxValue),
                };
            if (e.Name.LocalName == "objective") objectives[name] = source;
            else fame[name] = source;
        }
        return new()
        {
            WeeklyItem = (string?)weekly.Attribute("itemreward"), WeeklyRequirement = Raw(D(weekly, "mightrequirement")), ResetTime = reset,
            WeeklyFavor = [.. weekly.Elements("reward").Select(r => new FavorReward(Raw(D(r, "mightrequirement")), Raw(D(r, "favor")),
                (string?)r.Attribute("boughtpremium") == "true", (string?)r.Attribute("premium") == "true", (string?)r.Attribute("nonpremium") == "true"))],
            Fame = fame, Objectives = objectives,
            Seasons = root.Elements("seasonchallenge").Where(s => s.Attribute("uniquename") is not null).ToDictionary(
                s => (string)s.Attribute("uniquename")!, s => (IReadOnlyList<SeasonReward>)[.. s.Elements("reward").Select(r =>
                    new SeasonReward(Raw(D(r, "mightrequirement")), (int)D(r, "upgradeline"), ReadReward(r),
                        [.. r.Elements("rewardoverwrite").Select(o => new RewardOverride((string?)o.Attribute("gvgbracket") ?? "",
                            (int)D(o, "gvgbracketrank", -1), (string?)o.Attribute("requireminimumguildparticipation") == "true", ReadReward(o, r)))]))],
                StringComparer.Ordinal),
        };
    }
    private static Reward ReadReward(XElement e, XElement? parent = null)
    {
        bool B(string name) => (string?)(e.Attribute(name) ?? parent?.Attribute(name)) == "true";
        return new((string?)e.Attribute("displayitem") ?? "", (string?)e.Attribute("lootlistreference"),
            B("boughtpremium"), B("premium"), B("nonpremium"));
    }
    private static double D(XElement e, string name, double fallback = 0) =>
        double.TryParse((string?)e.Attribute(name), NumberStyles.Float, CultureInfo.InvariantCulture, out var d) && double.IsFinite(d) ? d : fallback;
    public static long Raw(double points) => !double.IsFinite(points) || points <= 0 ? 0
        : (long)Math.Min(long.MaxValue / 4, Math.Round(points * 10000, MidpointRounding.AwayFromZero));
}
