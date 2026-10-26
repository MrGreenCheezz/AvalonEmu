using System.Globalization;
using System.Xml.Linq;

namespace AlbionLocal.Server.Data;

/// <summary>
/// A castletype of castles.xml (stageP territories SPEC §1): its uniquename (SMALLCASTLE = castle outpost, MEDIUMCASTLE = castle), its index in the
/// file (NewCastleObject key 2: cjb.b0 0x180aa5bc0 looks the type up with bbk.abo(int), the list in file order; an unknown index drops the
/// object), the world map prefab and the fight timers (eliteguardarrivalinmin, castlelordarrivalinmin, castlephasetimeoutinmin; the capture zone
/// of castlecapture, for phase P5).
/// </summary>
public sealed record CastleType(string Name, int Index, string WorldMapPrefab, double EliteArrivalMinutes, double LordArrivalMinutes, double PhaseTimeoutMinutes,
    IReadOnlyDictionary<string, string> Capture)
{
    /// <summary>A castlecapture attribute as a number (castles.xml; <paramref name="fallback"/> when missing: the 345319 value).</summary>
    public double CaptureValue(string name, double fallback) =>
        Capture.TryGetValue(name, out var v) && double.TryParse(v, NumberStyles.Float, CultureInfo.InvariantCulture, out var d) ? d : fallback;

    /// <summary>capturezoneradius (12 m): the circle a guild has to hold.</summary>
    public double CaptureRadius => CaptureValue("capturezoneradius", 12);

    /// <summary>attackerclaimtime (16 s): an attacking team alone in the zone fills the claim in this time.</summary>
    public double AttackerClaimSeconds => CaptureValue("attackerclaimtime", 16);

    /// <summary>attackerunclaimtime (8 s): another team alone in the zone empties an attacker's claim in this time.</summary>
    public double AttackerUnclaimSeconds => CaptureValue("attackerunclaimtime", 8);

    /// <summary>attackernoteamunclaimtime (30 s): with nobody in the zone an attacker's claim runs out in this time.</summary>
    public double AttackerNoTeamUnclaimSeconds => CaptureValue("attackernoteamunclaimtime", 30);

    /// <summary>ownerclaimtime (16 s): the owner's team alone in the zone ends the fight (keeps the castle) in this time.</summary>
    public double OwnerClaimSeconds => CaptureValue("ownerclaimtime", 16);

    /// <summary>ownerunclaimtime (8 s): the owner's team alone in the zone empties an attacker's claim in this time.</summary>
    public double OwnerUnclaimSeconds => CaptureValue("ownerunclaimtime", 8);

    /// <summary>castlelordrespawntime (300 s): an unclaimed capture zone brings the lord back after this time.</summary>
    public double LordRespawnSeconds => CaptureValue("castlelordrespawntime", 300);

    /// <summary>The castle chests' treasuredistribution subtype: CASTLE_OUTPOST for SMALLCASTLE, CASTLE otherwise.</summary>
    public string ChestSubtype => Name == "SMALLCASTLE" ? "CASTLE_OUTPOST" : "CASTLE";
}

/// <summary>One weighted draw of treasuredistribution (an &lt;Event&gt;): the LootChestSpawnpoints with their weights and the &lt;None&gt; weight.</summary>
public sealed record CastleChestDraw(IReadOnlyList<(string Spawnpoint, int Weight)> Chests, int NoneWeight)
{
    /// <summary>One draw on <paramref name="random"/>: a spawnpoint, or null for None.</summary>
    public string? Roll(Random random)
    {
        var total = Chests.Sum(c => Math.Max(0, c.Weight)) + Math.Max(0, NoneWeight);
        if (total <= 0) return null;
        var r = random.Next(total);
        foreach (var (spawnpoint, weight) in Chests)
        {
            if (r < Math.Max(0, weight)) return spawnpoint;
            r -= Math.Max(0, weight);
        }
        return null;
    }
}

/// <summary>
/// The castle chests of treasuredistribution_&lt;region&gt;.bin (stageP territories SPEC §1, §3.4) for one subtype (CASTLE, CASTLE_OUTPOST):
/// the draw of its &lt;Distribute continent="Outlands" subtype&gt; by cluster tier (the &lt;subtype&gt;_STANDARD and _WEEKEND events, each
/// TreasureLootchest resolved to its lootchest spawnpoint), the respawn times (UTC) of the ScheduledEvent &lt;subtype&gt;_STANDARD per
/// weekday and the windows of &lt;subtype&gt;_WEEKEND (Start..End per weekday) in which a respawn draws the weekend event instead.
/// </summary>
public sealed class CastleChestSchedule
{
    public IReadOnlyDictionary<int, CastleChestDraw> Standard { get; init; } = new Dictionary<int, CastleChestDraw>();

    public IReadOnlyDictionary<int, CastleChestDraw> Weekend { get; init; } = new Dictionary<int, CastleChestDraw>();

    public IReadOnlyDictionary<DayOfWeek, IReadOnlyList<TimeSpan>> Respawns { get; init; } = new Dictionary<DayOfWeek, IReadOnlyList<TimeSpan>>();

    public IReadOnlyDictionary<DayOfWeek, IReadOnlyList<(TimeSpan Start, TimeSpan End)>> WeekendWindows { get; init; } =
        new Dictionary<DayOfWeek, IReadOnlyList<(TimeSpan, TimeSpan)>>();

    private static CastleChestDraw? ForTier(IReadOnlyDictionary<int, CastleChestDraw> draws, int tier) =>
        draws.TryGetValue(tier, out var d) ? d : draws.Count == 0 ? null : draws.MinBy(kv => Math.Abs(kv.Key - tier)).Value;

    /// <summary>The draw of a respawn at <paramref name="slot"/> (UTC) in a cluster of <paramref name="tier"/>: the weekend one inside its window.</summary>
    public CastleChestDraw? DrawAt(DateTime slot, int tier)
    {
        var inWeekend = WeekendWindows.TryGetValue(slot.DayOfWeek, out var windows) && windows.Any(w => slot.TimeOfDay >= w.Start && slot.TimeOfDay <= w.End);
        return (inWeekend ? ForTier(Weekend, tier) : null) ?? ForTier(Standard, tier);
    }

    /// <summary>
    /// The latest respawn at or before <paramref name="utc"/> (UTC; up to a week back): the data's times, or with <paramref name="hourly"/>
    /// every full hour (Castles:ChestSchedule hourly). Null when the data has none.
    /// </summary>
    public DateTime? LastSlot(DateTime utc, bool hourly)
    {
        if (hourly) return new DateTime(utc.Year, utc.Month, utc.Day, utc.Hour, 0, 0, DateTimeKind.Utc);
        for (var d = 0; d <= 7; d++)
        {
            var day = utc.Date.AddDays(-d);
            if (!Respawns.TryGetValue(day.DayOfWeek, out var times)) continue;
            foreach (var t in times.OrderByDescending(t => t))
                if (day + t <= utc) return DateTime.SpecifyKind(day + t, DateTimeKind.Utc);
        }
        return null;
    }

    private static readonly TimeSpan[] EveryTwoHours = [.. Enumerable.Range(0, 7).Select(i => TimeSpan.FromHours(10 + 2 * i))];

    /// <summary>The 345319 Europe values (every 2 h UTC 10:00-22:00, weekend legendary 17:59-19:58 on Saturday and Sunday) of <paramref name="subtype"/>.</summary>
    public static CastleChestSchedule Fallback(string subtype)
    {
        var outpost = subtype == "CASTLE_OUTPOST";
        var p = outpost ? "SPAWNPOINT_TREASURE_CASTLE_OUTPOST_" : "SPAWNPOINT_TREASURE_CASTLE_";
        var standard = new CastleChestDraw(outpost
            ? [(p + "STANDARD", 400), (p + "UNCOMMON", 200), (p + "RARE", 70), (p + "LEGENDARY", 21)]
            : [(p + "STANDARD", 240), (p + "UNCOMMON", 120), (p + "RARE", 40), (p + "LEGENDARY", 21)], outpost ? 309 : 579);
        var weekend = new CastleChestDraw([(p + "LEGENDARY", 1)], 0);
        var window = (IReadOnlyList<(TimeSpan, TimeSpan)>)[(new TimeSpan(17, 59, 0), new TimeSpan(19, 58, 0))];
        return new CastleChestSchedule
        {
            Standard = new Dictionary<int, CastleChestDraw> { [5] = standard },
            Weekend = new Dictionary<int, CastleChestDraw> { [5] = weekend },
            Respawns = Enum.GetValues<DayOfWeek>().ToDictionary(d => d, _ => (IReadOnlyList<TimeSpan>)EveryTwoHours),
            WeekendWindows = new Dictionary<DayOfWeek, IReadOnlyList<(TimeSpan, TimeSpan)>> { [DayOfWeek.Saturday] = window, [DayOfWeek.Sunday] = window },
        };
    }

    /// <summary>The schedule of <paramref name="subtype"/> out of treasuredistribution's root; null when the file lacks its distribution or schedule.</summary>
    public static CastleChestSchedule? Load(XElement root, string subtype)
    {
        var spawnpoints = root.Descendants("TreasureLootchest").Where(t => t.Attribute("uniquename") is not null && t.Attribute("lootchest") is not null)
            .GroupBy(t => XmlAttr.Str(t, "uniquename")).ToDictionary(g => g.Key, g => XmlAttr.Str(g.First(), "lootchest"), StringComparer.Ordinal);
        var dist = root.Descendants("Distribute").FirstOrDefault(d => XmlAttr.Str(d, "continent") == "Outlands" && XmlAttr.Str(d, "subtype") == subtype);
        var events = new Dictionary<string, XElement>(StringComparer.Ordinal);
        foreach (var e in root.Descendants("ScheduledEvent")) events.TryAdd(XmlAttr.Str(e, "name"), e);
        if (dist is null || !events.TryGetValue(subtype + "_STANDARD", out var std)) return null;
        CastleChestDraw Draw(XElement e) => new(
            [.. e.Elements("TreasureLootchest").Where(t => spawnpoints.ContainsKey(XmlAttr.Str(t, "name")))
                .Select(t => (spawnpoints[XmlAttr.Str(t, "name")], XmlAttr.Int(t, "weight", 0)))],
            e.Elements("None").Sum(n => XmlAttr.Int(n, "weight", 0)));
        Dictionary<int, CastleChestDraw> Draws(string name)
        {
            var d = new Dictionary<int, CastleChestDraw>();
            foreach (var t in dist.Elements("ClusterTier"))
                if (t.Elements("Event").FirstOrDefault(e => XmlAttr.Str(e, "name") == name) is { } e) d[XmlAttr.Int(t, "tier", 0)] = Draw(e);
            return d;
        }
        static TimeSpan? Utc(XElement e) => TimeSpan.TryParse(XmlAttr.Str(e, "utc"), CultureInfo.InvariantCulture, out var t) ? t : null;
        static DayOfWeek? Day(XElement e) => Enum.TryParse<DayOfWeek>(XmlAttr.Str(e, "day"), out var d) ? d : null;
        var respawns = new Dictionary<DayOfWeek, IReadOnlyList<TimeSpan>>();
        foreach (var w in std.Descendants("Weekday"))
            if (Day(w) is { } day) respawns[day] = [.. w.Elements("Respawn").Select(Utc).OfType<TimeSpan>()];
        var windows = new Dictionary<DayOfWeek, IReadOnlyList<(TimeSpan, TimeSpan)>>();
        if (events.TryGetValue(subtype + "_WEEKEND", out var weekend))
            foreach (var w in weekend.Descendants("Weekday"))
                if (Day(w) is { } day && w.Element("Start") is { } s && w.Element("End") is { } en && Utc(s) is { } from && Utc(en) is { } to)
                    windows[day] = [(from, to)];
        return new CastleChestSchedule { Standard = Draws(subtype + "_STANDARD"), Weekend = Draws(subtype + "_WEEKEND"), Respawns = respawns, WeekendWindows = windows };
    }
}

/// <summary>
/// A timeregion of worldsettings_&lt;region&gt; (SPEC §1): its name (US_22_00 …, world.xml cluster timeregion), the UTC start of its prime time,
/// the timezone (hours, for the seasontimes events in cluster time) and whether the world map shows it.
/// </summary>
public sealed record TimeRegion(string Name, TimeSpan Start, double TimeZone, bool Visible);

/// <summary>A towerlevel of territorytypes WATCHTOWER (SPEC §1): its level, powerpointspool (the tower power points the level holds) and decaydays.</summary>
public sealed record TowerLevel(int Level, double Pool, double DecayDays);

/// <summary>
/// The rules of the guild territories from the client's GameData (stageP territories SPEC §1, §3.1): territorytypes.bin's WATCHTOWER (claim
/// cost, nutrition, the tower levels' count and the base season points by cluster type and tier), castles.bin, the timeregions and prime time
/// length of worldsettings_&lt;region&gt;.bin and the claim rules of gamedata_&lt;region&gt;.bin (Guild initialclaimterritoryguildfamerequirement,
/// claimterritoryguildfamerequirement, Territory territoryclaimtimeinsec). Read once at the first use (<see cref="Systems.Territories"/>); immutable.
/// <see cref="Fallback"/> holds the 345319 values for a folder without the files (a self-test's world).
/// </summary>
public sealed class TerritoryRules
{
    /// <summary>territorytype WATCHTOWER claimcost (silver).</summary>
    public long ClaimCost { get; init; } = 2_000_000;

    /// <summary>nutritionstorage, secondspernutrition and claimnutrition of WATCHTOWER.</summary>
    public long NutritionStorage { get; init; } = 38_880;

    public double SecondsPerNutrition { get; init; } = 22.222222;

    public long ClaimNutrition { get; init; } = 4000;

    /// <summary>The tower levels of WATCHTOWER (towerlevels/towerlevel), 8.</summary>
    public int TowerLevels => TowerLevelRows.Count;

    /// <summary>The towerlevel rows in level order (the 345319 values without the file).</summary>
    public IReadOnlyList<TowerLevel> TowerLevelRows { get; init; } =
    [
        new(1, 9613, 2), new(2, 14996, 2.4), new(3, 22744, 2.8), new(4, 33791, 3.2), new(5, 49419, 3.6), new(6, 71384, 4), new(7, 102079, 4.4),
        new(8, 144766, 4.8),
    ];

    /// <summary>PointsSettings: the base season points of an owned territory per scoring tick by cluster type and tier.</summary>
    public IReadOnlyDictionary<(string ClusterType, int Tier), int> BasePoints { get; init; } = new Dictionary<(string, int), int>();

    /// <summary>castles.bin in file order.</summary>
    public IReadOnlyList<CastleType> Castles { get; init; } = [];

    /// <summary>worldsettings timeregions by name.</summary>
    public IReadOnlyDictionary<string, TimeRegion> TimeRegions { get; init; } = new Dictionary<string, TimeRegion>(StringComparer.Ordinal);

    /// <summary>timeregions primetimeduration (1:00).</summary>
    public TimeSpan PrimeTime { get; init; } = TimeSpan.FromHours(1);

    /// <summary>gamedata Guild initialclaimterritoryguildfamerequirement and claimterritoryguildfamerequirement (fame, per further territory).</summary>
    public long FirstClaimFame { get; init; } = 1_000_000;

    public long FurtherClaimFame { get; init; } = 1_000_000;

    /// <summary>gamedata Territory territoryclaimtimeinsec.</summary>
    public double ClaimSeconds { get; init; } = 15;

    /// <summary>
    /// territorytypes GuardSettings respawntimeseconds (3600): how long a castle stays without its guards after a capture before they stand
    /// again (the server's reading of the fortification guards' respawn for the castles; castles.xml has no own value).
    /// </summary>
    public double GuardRespawnSeconds { get; init; } = 3600;

    /// <summary>The castle chests of treasuredistribution by subtype (CASTLE, CASTLE_OUTPOST); a subtype the file lacks has its fallback.</summary>
    public IReadOnlyDictionary<string, CastleChestSchedule> ChestSchedules { get; init; } = new Dictionary<string, CastleChestSchedule>();

    /// <summary>The chest schedule of <paramref name="subtype"/>: the data's, else the 345319 Europe values.</summary>
    public CastleChestSchedule ChestSchedule(string subtype) => ChestSchedules.GetValueOrDefault(subtype) ?? CastleChestSchedule.Fallback(subtype);

    /// <summary>The 345319 values without any file (no castles, no timeregions: every cluster is then never in prime time).</summary>
    public static readonly TerritoryRules Fallback = new();

    /// <summary>The castle type <paramref name="name"/>; null = none.</summary>
    public CastleType? Castle(string? name) => Castles.FirstOrDefault(c => c.Name == name);

    /// <summary>The base points of a territory in a cluster of <paramref name="clusterType"/> and <paramref name="tier"/>; 0 = none in the data.</summary>
    public int PointsOf(string clusterType, int tier) => BasePoints.GetValueOrDefault((clusterType, tier));

    /// <summary>
    /// Whether <paramref name="utc"/> is in the prime time of <paramref name="timeRegion"/> (its start, shifted by <paramref name="offsetHours"/>,
    /// plus <see cref="PrimeTime"/>, across midnight too); an unknown region never is.
    /// </summary>
    public bool InPrimeTime(string timeRegion, DateTime utc, double offsetHours = 0)
    {
        if (!TimeRegions.TryGetValue(timeRegion, out var r)) return false;
        var day = TimeSpan.FromDays(1);
        var start = (r.Start + TimeSpan.FromHours(offsetHours)).Ticks % day.Ticks;
        if (start < 0) start += day.Ticks;
        var since = (utc.TimeOfDay.Ticks - start + day.Ticks) % day.Ticks;
        return since < PrimeTime.Ticks;
    }

    private static double D(XElement e, string name, double fallback) =>
        double.TryParse((string?)e.Attribute(name), NumberStyles.Float, CultureInfo.InvariantCulture, out var d) ? d : fallback;

    /// <summary>The rules out of the four files' roots (any may be null: its values stay the fallback's).</summary>
    public static TerritoryRules Load(XElement? territoryTypes, XElement? castles, XElement? worldSettings, XElement? gameData, XElement? treasures = null)
    {
        var tower = territoryTypes?.Elements("territorytype").FirstOrDefault(t => XmlAttr.Str(t, "name") == "WATCHTOWER");
        var f = Fallback;
        var points = new Dictionary<(string, int), int>();
        foreach (var v in tower?.Descendants("PointsValues") ?? [])
            foreach (var t in v.Elements("ClusterTier"))
                points[(XmlAttr.Str(v, "clustertype"), XmlAttr.Int(t, "tier", 0))] = XmlAttr.Int(t, "points", 0);
        var castleList = (castles?.Elements("Castle") ?? []).Select((c, i) =>
        {
            var capture = c.Element("castlecapture");
            return new CastleType(XmlAttr.Str(c, "uniquename"), i, XmlAttr.Str(c, "worldmapprefab"), D(c, "eliteguardarrivalinmin", 0),
                D(c, "castlelordarrivalinmin", 0), D(c, "castlephasetimeoutinmin", 30),
                capture?.Attributes().ToDictionary(a => a.Name.LocalName, a => a.Value, StringComparer.Ordinal) ?? new Dictionary<string, string>());
        }).ToList();
        var regions = worldSettings?.Descendants("timeregions").FirstOrDefault();
        var regionList = new Dictionary<string, TimeRegion>(StringComparer.Ordinal);
        foreach (var r in regions?.Elements("timeregion") ?? [])
            if (TimeSpan.TryParse(XmlAttr.Str(r, "starttime"), CultureInfo.InvariantCulture, out var start))
                regionList[XmlAttr.Str(r, "name")] = new TimeRegion(XmlAttr.Str(r, "name"), start, D(r, "timezone", 0), XmlAttr.Str(r, "worldmapvisible") == "true");
        var guild = gameData?.Descendants("Guild").FirstOrDefault(g => g.Attribute("claimterritoryguildfamerequirement") is not null);
        var territory = gameData?.Descendants("Territory").FirstOrDefault(t => t.Attribute("territoryclaimtimeinsec") is not null);
        return new TerritoryRules
        {
            ClaimCost = tower is null ? f.ClaimCost : XmlAttr.Long(tower, "claimcost", f.ClaimCost),
            NutritionStorage = tower is null ? f.NutritionStorage : XmlAttr.Long(tower, "nutritionstorage", f.NutritionStorage),
            SecondsPerNutrition = tower is null ? f.SecondsPerNutrition : D(tower, "secondspernutrition", f.SecondsPerNutrition),
            ClaimNutrition = tower is null ? f.ClaimNutrition : XmlAttr.Long(tower, "claimnutrition", f.ClaimNutrition),
            TowerLevelRows = tower?.Element("towerlevels")?.Elements("towerlevel")
                .Select(t => new TowerLevel(XmlAttr.Int(t, "level", 0), D(t, "powerpointspool", 0), D(t, "decaydays", 1)))
                .Where(t => t.Level > 0).OrderBy(t => t.Level).ToList() is { Count: > 0 } levels ? levels : f.TowerLevelRows,
            BasePoints = points,
            Castles = castleList,
            TimeRegions = regionList,
            PrimeTime = TimeSpan.TryParse((string?)regions?.Attribute("primetimeduration"), CultureInfo.InvariantCulture, out var prime) ? prime : f.PrimeTime,
            FirstClaimFame = guild is null ? f.FirstClaimFame : XmlAttr.Long(guild, "initialclaimterritoryguildfamerequirement", f.FirstClaimFame),
            FurtherClaimFame = guild is null ? f.FurtherClaimFame : XmlAttr.Long(guild, "claimterritoryguildfamerequirement", f.FurtherClaimFame),
            ClaimSeconds = territory is null ? f.ClaimSeconds : D(territory, "territoryclaimtimeinsec", f.ClaimSeconds),
            GuardRespawnSeconds = territoryTypes?.Descendants("GuardSettings").FirstOrDefault() is { } guards
                ? D(guards, "respawntimeseconds", f.GuardRespawnSeconds) : f.GuardRespawnSeconds,
            ChestSchedules = treasures is null ? new Dictionary<string, CastleChestSchedule>()
                : new[] { "CASTLE", "CASTLE_OUTPOST" }.Select(s => (Subtype: s, Schedule: CastleChestSchedule.Load(treasures, s)))
                    .Where(x => x.Schedule is not null).ToDictionary(x => x.Subtype, x => x.Schedule!, StringComparer.Ordinal),
        };
    }

    /// <summary>
    /// The files of <paramref name="region"/> ("europe", "asia"; "" = the West's worldsettings.bin and gamedata.bin, as Guild:Region) from the client's
    /// GameData <paramref name="folder"/>; a missing file keeps the fallback's values of its part.
    /// </summary>
    public static TerritoryRules Load(string folder, string region)
    {
        var suffix = region.Length == 0 ? "" : "_" + region;
        XElement? Root(string name) => File.Exists(Path.Combine(folder, name)) ? GameDataFile.Open(folder, name).Root() : null;
        return Load(Root("territorytypes.bin"), Root("castles.bin"), Root($"worldsettings{suffix}.bin"), Root($"gamedata{suffix}.bin"),
            Root($"treasuredistribution{suffix}.bin"));
    }
}
