using System.Globalization;
using System.Xml.Linq;

namespace AlbionLocal.Server.Data;

/// <summary>A guildchallengelevel row: the level, the guild challenge points it starts at (whole points) and its season points.</summary>
public sealed record ChallengeLevel(int Level, long Points, int SeasonPoints);

/// <summary>A bracketsettings row: iron, bronze, silver, gold or crystal and the season points it starts at.</summary>
public sealed record SeasonBracket(string Name, long Threshold)
{
    /// <summary>The brackets from the lowest (the order of gotguildmemberreward's minrank).</summary>
    public static readonly IReadOnlyList<string> Order = ["iron", "bronze", "silver", "gold", "crystal"];

    /// <summary>The position of <paramref name="name"/> in <see cref="Order"/>; -1 = none.</summary>
    public static int RankOf(string? name) => name is null ? -1 : Order.ToList().IndexOf(name.ToLowerInvariant());
}

/// <summary>An action of a season event (gvgseasons seasontimes, the client's but.EventType: territoriesclaimable, resetterritories, …) with its attributes.</summary>
public sealed record SeasonAction(string Type, IReadOnlyDictionary<string, string> Attrs);

/// <summary>
/// One eventtime of a season's seasontimes (stageP territories SPEC §1, §3.5): its day (1 = the season's first), its UTC time, whether the time is
/// the cluster's local time (calculateusingclustertimezone: minus the timeregion's timezone), the timeregions of its clusterfilter (null = every
/// cluster) and its actions in file order. All three region files filter by the same six US_* timeregions, the names world.xml gives every
/// territory cluster, so a filter only leaves out NOT_USED.
/// </summary>
public sealed record SeasonEvent(int Day, TimeSpan Time, bool ClusterTimeZone, IReadOnlyList<string>? TimeRegions, IReadOnlyList<SeasonAction> Actions)
{
    /// <summary>When it happens in the season that starts at <paramref name="start"/> for a cluster of timezone <paramref name="timezoneHours"/>.</summary>
    public DateTime At(DateTime start, double timezoneHours) =>
        DateTime.SpecifyKind(start.Date, DateTimeKind.Utc).AddDays(Day - 1) + Time - (ClusterTimeZone ? TimeSpan.FromHours(timezoneHours) : TimeSpan.Zero);

    /// <summary>Whether its clusterfilter takes a cluster of <paramref name="timeRegion"/>.</summary>
    public bool Applies(string timeRegion) => TimeRegions is null || TimeRegions.Contains(timeRegion, StringComparer.Ordinal);
}

/// <summary>A progression row of a might category (progressiontables.xml): the guild might it starts at (a7m raw, as the client's bq9.a +0x10) and its season points.</summary>
public sealed record MightLevel(long Might, int SeasonPoints);

/// <summary>
/// A guild might category of a season (pvpchallenge's mightcategorytemplate, the client's bqz; stageI guild-goals SPEC §1.7): its uniquename
/// (450 key 2, 449 key 1, the type-68 popup's "category"), its progression rows (row i is the client's level i + 1: bqz.g is the index of the
/// last row reached, -1 below the first, and the entry shows index + 1) and the objective types that feed it (its objectivemappings).
/// </summary>
public sealed record MightCategory(string Name, IReadOnlyList<MightLevel> Levels, IReadOnlySet<string> Objectives)
{
    /// <summary>The rows <paramref name="might"/> (a7m) has reached: the level the client shows (bqz.g + 1).</summary>
    public int LevelOf(long might) => Levels.Count(l => l.Might <= might);
}

/// <summary>
/// A fame rule of pvpchallengeobjectives (pve, gathering, fishing): its objective type, fametomightfactor, solorandomdungeonfactor (1 = none)
/// and the dangerbonustypemultiplier per pvpwarningtype (orange 0: nothing in an orange-warning zone; a type left out counts 1).
/// </summary>
public sealed record MightFameRule(string Objective, double Factor, double SoloDungeonFactor, IReadOnlyDictionary<string, double> Zones)
{
    public double ZoneFactor(string? warning) => warning is not null && Zones.TryGetValue(warning, out var f) ? f : 1;
}

/// <summary>
/// The might sources of pvpchallenge_&lt;region&gt; (stageI guild-goals SPEC §1.7): the fame rules by element (pve, gathering, fishing) and the
/// objectives by uniquename with their type and might (PVPCHALLENGE_CORRUPTED_SLAYER: CORRUPTED_SLAYER, 180.88).
/// </summary>
public sealed record MightRules(IReadOnlyDictionary<string, MightFameRule> Fame, IReadOnlyDictionary<string, (string Type, double Might)> Objectives)
{
    public static readonly MightRules None = new(new Dictionary<string, MightFameRule>(), new Dictionary<string, (string, double)>());
}

/// <summary>
/// One guild season (stageI guild-goals SPEC §1.2, §2.1): a season of the client's gvgseasons file (<see cref="Index"/> = its 0-based
/// position there, the wire's season id of GetGvgSeasonRank and the popup JSON) or a local one of the server (<see cref="Index"/> null:
/// after the last client season, Guild:LocalSeasonDays long, never shown in the client's windows). <see cref="Key"/> keys the guilds'
/// saved state (the uniquename, "LOCAL@n" for a local one). <see cref="Might"/> = its guild might categories (the season's
/// mightcategorytemplate, DEFAULT when it names none as the client's bq0.a; a local season takes the last client season's; none without
/// the client's pvpchallenge files).
/// </summary>
public sealed record GvgSeason(int? Index, string Key, DateTime Start, DateTime End, IReadOnlyList<ChallengeLevel> Levels, IReadOnlyList<SeasonBracket> Brackets)
{
    public IReadOnlyList<MightCategory> Might { get; init; } = [];

    /// <summary>
    /// Its seasontimes (stageP territories SPEC §3.5): the territory schedule (claimable windows, resets, scoring, battles) in file order; a local
    /// season takes the last client season's (SEASON_34's), as the SPEC proposes.
    /// </summary>
    public IReadOnlyList<SeasonEvent> Schedule { get; init; } = [];

    /// <summary>
    /// invadersettings bossinvader mobname when it blocksterritoryclaim (the Tower Guardian, T6_MOB_ARCANE_ELEMENTAL_ELITE_BOSS; null = none): spawned
    /// at the monoliths by the schedule's spawninvader (stageP territories SPEC §3.1). A local season takes the last client season's.
    /// </summary>
    public string? Invader { get; init; }

    /// <summary>The season's territorytypes types (WATCHTOWER, CASTLE): the territories its resetterritories resets (none listed = watchtowers only).</summary>
    public IReadOnlyList<string> TerritoryTypes { get; init; } = [];

    /// <summary>A season of the client data: its windows run (GvgSeasonGui.UpdateData: start &lt; now &lt; end).</summary>
    public bool IsClient => Index is not null;

    /// <summary>The level of <paramref name="points"/> as FillGuildPointsData 0x18107aeb0: the last row whose challengepoints are at most them (1 below the first).</summary>
    public int LevelOf(long points)
    {
        var level = 1;
        foreach (var l in Levels)
            if (l.Points <= points) level = l.Level;
        return level;
    }

    /// <summary>The row of <paramref name="level"/>; null = none in the table.</summary>
    public ChallengeLevel? Row(int level) => Levels.FirstOrDefault(l => l.Level == level);

    /// <summary>The bracket of <paramref name="points"/> season points: the highest threshold reached; null below iron.</summary>
    public SeasonBracket? BracketOf(long points) => Brackets.Where(b => points >= b.Threshold).MaxBy(b => b.Threshold);

    /// <summary>The might category <paramref name="name"/>; null = none in this season.</summary>
    public MightCategory? MightCategory(string? name) => Might.FirstOrDefault(c => c.Name == name);
}

/// <summary>
/// gvgseasons_&lt;region&gt;.xml with the season dates of times_&lt;region&gt;.xml (stageI guild-goals SPEC §1.2, §2.1): per &lt;season&gt; in
/// file order its uniquename, start and end (startdatetimeref / enddatetimeref → the DateTime defaultdatetime), the 99 guild challenge
/// levels and the brackets; with pvpchallenge_&lt;region&gt;.xml and progressiontables.xml its might categories (§1.7) and the might
/// sources. Read at the start (a few KB, ServerHost); immutable. Without the files the built-in table of SEASON_34
/// (<see cref="Fallback"/>) gives local seasons from <see cref="DefaultLocalStart"/>.
/// </summary>
public sealed class GvgSeasonData
{
    /// <summary>SEASON_34's end (Europe), the last client season: where the local seasons start without client data.</summary>
    public static readonly DateTime DefaultLocalStart = new(2026, 11, 9, 11, 0, 0, DateTimeKind.Utc);

    /// <summary>
    /// SEASON_34's challenge table as a formula (every listed level matches): level n starts at 100 000 × n(n-1)/2 points and gives
    /// min(300, 20 × (n-1)) season points; 99 levels.
    /// </summary>
    public static readonly IReadOnlyList<ChallengeLevel> DefaultLevels =
        [.. Enumerable.Range(1, 99).Select(n => new ChallengeLevel(n, 100_000L * n * (n - 1) / 2, Math.Min(300, 20 * (n - 1))))];

    /// <summary>SEASON_34's brackets.</summary>
    public static readonly IReadOnlyList<SeasonBracket> DefaultBrackets =
        [new("crystal", 250_000), new("gold", 80_000), new("silver", 25_000), new("bronze", 7_500), new("iron", 1_000)];

    /// <summary>The template of a season that names no mightcategorytemplate (bq0.a, the client's constant).</summary>
    public const string DefaultMightTemplate = "DEFAULT";

    /// <summary>No client seasons: only local ones with the built-in table.</summary>
    public static readonly GvgSeasonData Fallback = new([]);

    public GvgSeasonData(IReadOnlyList<GvgSeason> seasons) => Seasons = seasons;

    /// <summary>The client's seasons in file order (index = position).</summary>
    public IReadOnlyList<GvgSeason> Seasons { get; }

    /// <summary>The might sources of the region's pvpchallenge file (none without it).</summary>
    public MightRules Might { get; init; } = MightRules.None;

    /// <summary>The client season running at <paramref name="utc"/> (start &lt; now &lt; end, as the client's own check); null = none.</summary>
    public GvgSeason? ClientSeason(DateTime utc) => Seasons.FirstOrDefault(s => s.Start < utc && utc < s.End);

    /// <summary>
    /// The guild season of <paramref name="utc"/>: the client's, else the local one of <paramref name="localDays"/>-day cycles from the
    /// last client season's end (n counts from 0 there; a gap between two client seasons falls into a local season of a negative n).
    /// A local season takes the last client season's table, brackets and might categories.
    /// </summary>
    public GvgSeason Current(DateTime utc, int localDays)
    {
        if (ClientSeason(utc) is { } client) return client;
        var last = Seasons.Count > 0 ? Seasons.MaxBy(s => s.End) : null;
        var anchor = last?.End ?? DefaultLocalStart;
        var span = TimeSpan.FromDays(Math.Max(1, localDays));
        var n = (long)Math.Floor((utc - anchor) / span);
        var start = anchor + span * n;
        return new GvgSeason(null, string.Create(CultureInfo.InvariantCulture, $"LOCAL@{n}"), start, start + span,
            last?.Levels is { Count: > 0 } levels ? levels : DefaultLevels, last?.Brackets is { Count: > 0 } brackets ? brackets : DefaultBrackets)
        {
            Might = last?.Might ?? [],
            Schedule = last?.Schedule ?? [],
            Invader = last?.Invader,
            TerritoryTypes = last?.TerritoryTypes ?? [],
        };
    }

    /// <summary>The season of <paramref name="key"/> (a client uniquename or LOCAL@n; null = neither): a past season's brackets at its close.</summary>
    public GvgSeason? ByKey(string key, int localDays)
    {
        if (Seasons.FirstOrDefault(s => s.Key == key) is { } client) return client;
        if (!key.StartsWith("LOCAL@", StringComparison.Ordinal)
            || !long.TryParse(key.AsSpan(6), NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out var n)) return null;
        var anchor = Seasons.Count > 0 ? Seasons.MaxBy(s => s.End)!.End : DefaultLocalStart;
        var local = Current(anchor + TimeSpan.FromDays(Math.Max(1, localDays)) * n + TimeSpan.FromMinutes(1), localDays);
        return local.Key == key ? local : null;
    }

    /// <summary>
    /// The seasons of <paramref name="seasons"/> (gvgseasons*.xml) with their dates from <paramref name="times"/> (times*.xml); one without
    /// both dates is skipped from the dates but keeps its index. With <paramref name="pvpchallenge"/> (pvpchallenge*.xml) and
    /// <paramref name="progression"/> (progressiontables.xml) every season gets its might categories and the data its might sources.
    /// </summary>
    public static GvgSeasonData Load(XElement seasons, XElement times, XElement? pvpchallenge = null, XElement? progression = null)
    {
        var templates = pvpchallenge is not null && progression is not null ? MightTemplates(pvpchallenge, progression) : [];
        var dates = new Dictionary<string, DateTime>(StringComparer.Ordinal);
        foreach (var d in times.Elements("DateTime"))
            if ((string?)d.Attribute("uniquename") is { } name
                && DateTime.TryParse((string?)d.Attribute("defaultdatetime"), CultureInfo.InvariantCulture, DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal, out var at))
                dates[name] = DateTime.SpecifyKind(at, DateTimeKind.Utc);
        var list = new List<GvgSeason>();
        foreach (var s in seasons.Elements("season"))
        {
            var name = XmlAttr.Str(s, "uniquename");
            var start = dates.GetValueOrDefault(XmlAttr.Str(s, "startdatetimeref"), DateTime.MaxValue);
            var end = dates.GetValueOrDefault(XmlAttr.Str(s, "enddatetimeref"), DateTime.MinValue);
            var levels = s.Element("guildchallenge")?.Elements("guildchallengelevel")
                .Select(l => new ChallengeLevel(XmlAttr.Int(l, "level", 0), XmlAttr.Long(l, "challengepoints", 0), XmlAttr.Int(l, "seasonpoints", 0)))
                .Where(l => l.Level > 0).OrderBy(l => l.Level).ToList() ?? [];
            var brackets = s.Element("bracketsettings")?.Elements("bracket")
                .Select(b => new SeasonBracket(XmlAttr.Str(b, "level"), XmlAttr.Long(b, "pointsthreshold", 0))).ToList() ?? [];
            list.Add(new GvgSeason(list.Count, name, DateTime.SpecifyKind(start, DateTimeKind.Utc), DateTime.SpecifyKind(end, DateTimeKind.Utc),
                levels.Count > 0 ? levels : DefaultLevels, brackets.Count > 0 ? brackets : DefaultBrackets)
            {
                Might = templates.GetValueOrDefault(XmlAttr.Str(s, "mightcategorytemplate", DefaultMightTemplate)) ?? [],
                Schedule = Schedule(s.Element("seasontimes")),
                Invader = s.Element("invadersettings")?.Elements("bossinvader").FirstOrDefault(b => XmlAttr.Str(b, "blocksterritoryclaim") == "true") is { } boss
                    ? XmlAttr.Str(boss, "mobname") : null,
                TerritoryTypes = [.. (s.Element("territorytypes")?.Elements("territorytype") ?? []).Select(t => XmlAttr.Str(t, "type")).Where(t => t.Length > 0).Distinct()],
            });
        }
        return new GvgSeasonData(list) { Might = pvpchallenge is not null ? Rules(pvpchallenge) : MightRules.None };
    }

    /// <summary>The eventtimes of a seasontimes element (none without one); an unreadable day or time is skipped.</summary>
    public static IReadOnlyList<SeasonEvent> Schedule(XElement? seasontimes)
    {
        var list = new List<SeasonEvent>();
        foreach (var day in seasontimes?.Elements("day") ?? [])
        {
            if (!int.TryParse((string?)day.Attribute("number"), NumberStyles.Integer, CultureInfo.InvariantCulture, out var n) || n < 1) continue;
            foreach (var e in day.Elements("eventtime"))
            {
                if (!TimeSpan.TryParse(((string?)e.Attribute("utctime") ?? "").TrimEnd('Z', 'z'), CultureInfo.InvariantCulture, out var time)) continue;
                var filter = e.Element("clusterfilter");
                list.Add(new SeasonEvent(n, time, (string?)e.Attribute("calculateusingclustertimezone") == "true",
                    filter is null ? null : [.. filter.Elements("timeregion").Select(t => XmlAttr.Str(t, "name"))],
                    [.. e.Elements().Where(a => a.Name.LocalName != "clusterfilter")
                        .Select(a => new SeasonAction(a.Name.LocalName, a.Attributes().ToDictionary(x => x.Name.LocalName, x => x.Value, StringComparer.Ordinal)))]));
            }
        }
        return list;
    }

    private static double D(XElement e, string name, double fallback) =>
        double.TryParse((string?)e.Attribute(name), NumberStyles.Float, CultureInfo.InvariantCulture, out var d) ? d : fallback;

    /// <summary>The mightcategorytemplates of <paramref name="pvpchallenge"/>: their categories in file order with their progressiontables rows (a category whose table is unknown is left out).</summary>
    private static Dictionary<string, IReadOnlyList<MightCategory>> MightTemplates(XElement pvpchallenge, XElement progression)
    {
        var tables = new Dictionary<string, IReadOnlyList<MightLevel>>(StringComparer.Ordinal);
        foreach (var t in progression.Elements("table"))
            tables[XmlAttr.Str(t, "uniquename")] = [.. t.Elements("progression").OrderBy(r => XmlAttr.Int(r, "level", 0))
                .Select(r => new MightLevel((long)Math.Round(D(r, "points", 0) * 10000), XmlAttr.Int(r, "seasonpoints", 0)))];
        var result = new Dictionary<string, IReadOnlyList<MightCategory>>(StringComparer.Ordinal);
        foreach (var t in pvpchallenge.Elements("mightcategorytemplate"))
            result[XmlAttr.Str(t, "uniquename")] = [.. t.Elements("category")
                .Where(c => tables.ContainsKey(XmlAttr.Str(c, "progressiontable")))
                .Select(c => new MightCategory(XmlAttr.Str(c, "uniquename"), tables[XmlAttr.Str(c, "progressiontable")],
                    c.Elements("objectivemapping").Select(m => XmlAttr.Str(m, "objectivetype")).ToHashSet(StringComparer.Ordinal)))];
        return result;
    }

    /// <summary>The pvpchallengeobjectives of <paramref name="pvpchallenge"/>: the pve / gathering / fishing fame rules and every objective's type and might.</summary>
    private static MightRules Rules(XElement pvpchallenge)
    {
        var fame = new Dictionary<string, MightFameRule>(StringComparer.Ordinal);
        var objectives = new Dictionary<string, (string, double)>(StringComparer.Ordinal);
        foreach (var e in pvpchallenge.Element("pvpchallengeobjectives")?.Elements() ?? [])
        {
            if (e.Name.LocalName == "objective")
                objectives[XmlAttr.Str(e, "uniquename")] = (XmlAttr.Str(e, "objectivetype"), D(e, "might", 0));
            else if (e.Name.LocalName is "pve" or "gathering" or "fishing")
                fame[e.Name.LocalName] = new MightFameRule(XmlAttr.Str(e, "objectivetype"), D(e, "fametomightfactor", 0), D(e, "solorandomdungeonfactor", 1),
                    e.Elements("dangerbonustypemultiplier").GroupBy(m => XmlAttr.Str(m, "pvpwarningtype"), StringComparer.OrdinalIgnoreCase)
                        .ToDictionary(m => m.Key, m => D(m.First(), "mightandfavormultiplier", 1), StringComparer.OrdinalIgnoreCase));
        }
        return new MightRules(fame, objectives);
    }

    /// <summary>
    /// The region's files from the client's GameData <paramref name="folder"/>: gvgseasons_&lt;region&gt;.bin and times_&lt;region&gt;.bin ("" =
    /// the West's gvgseasons.bin, times.bin), with pvpchallenge_&lt;region&gt;.bin and progressiontables.bin for the guild might when both are there.
    /// </summary>
    public static GvgSeasonData Load(string folder, string region)
    {
        var suffix = region.Length == 0 ? "" : "_" + region;
        var might = File.Exists(Path.Combine(folder, $"pvpchallenge{suffix}.bin")) && File.Exists(Path.Combine(folder, "progressiontables.bin"));
        return Load(GameDataFile.Open(folder, $"gvgseasons{suffix}.bin").Root(), GameDataFile.Open(folder, $"times{suffix}.bin").Root(),
            might ? GameDataFile.Open(folder, $"pvpchallenge{suffix}.bin").Root() : null, might ? GameDataFile.Open(folder, "progressiontables.bin").Root() : null);
    }
}
