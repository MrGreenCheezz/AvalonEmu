using System.Globalization;
using System.Xml.Linq;

namespace AlbionLocal.Server.Data;

/// <summary>
/// StageR fw-zone (analysis/work/stageR/fw-zone/SPEC.md §1): factionfortress.xml, the fortress fight and the fortress guards.
/// <list type="bullet">
/// <item><c>fortressfight</c>: presiegeannouncementdurationsec 60, presiegedurationsec 540, postsiegeresetdelaysec 600, activesiegedefensepointgoal
/// 1800 at activesiegedefensepointspersecond 1 (the client's defensive progress bar runs from these: de2.a 0x180ad9a70 reads goal and rate from
/// its own copy), the capture area's claim times (owner 2 / unclaim 30 / no team 45, attacker 30 / unclaim 2 / no team 45), the weapon caches and
/// the winner's and loser's rewards.</item>
/// <item><c>fortressguards/guardtype[@name]/faction[@name @mobref]</c>: the tower's guard spots name a guardtype (T8_MOB_CITY_FORTRESS_SWORD …),
/// the owner's faction picks the mob.</item>
/// </list>
/// </summary>
public sealed class FortressData
{
    public double AnnouncementSeconds { get; init; } = 60;
    public double PreSiegeSeconds { get; init; } = 540;
    public double ResetSeconds { get; init; } = 600;
    public double DefenseGoal { get; init; } = 1800;
    public double DefensePerSecond { get; init; } = 1;

    public double OwnerClaimSeconds { get; init; } = 2;
    public double OwnerUnclaimSeconds { get; init; } = 30;
    public double OwnerNoTeamClaimSeconds { get; init; } = 45;
    public double AttackerClaimSeconds { get; init; } = 30;
    public double AttackerUnclaimSeconds { get; init; } = 2;
    public double AttackerNoTeamUnclaimSeconds { get; init; } = 45;

    /// <summary>guardtype name → FlaggingStatus → mobs.xml uniquename.</summary>
    public IReadOnlyDictionary<string, IReadOnlyDictionary<byte, string>> Guards { get; init; } = new Dictionary<string, IReadOnlyDictionary<byte, string>>();

    /// <summary>The mob of <paramref name="guardType"/> for <paramref name="faction"/>; null when the file has none.</summary>
    public string? GuardOf(string guardType, byte faction) =>
        Guards.TryGetValue(guardType, out var byFaction) && byFaction.TryGetValue(faction, out var mob) ? mob : null;

    public static readonly FortressData Default = new();

    public static FortressData Load(XElement root)
    {
        var f = root.Element("fortressfight");
        double D(string name, double fallback) =>
            double.TryParse((string?)f?.Attribute(name), NumberStyles.Float, CultureInfo.InvariantCulture, out var v) ? v : fallback;
        var guards = new Dictionary<string, IReadOnlyDictionary<byte, string>>(StringComparer.Ordinal);
        foreach (var g in root.Element("fortressguards")?.Elements("guardtype") ?? [])
        {
            var byFaction = new Dictionary<byte, string>();
            foreach (var x in g.Elements("faction"))
                if (FactionWarfareData.FlagOf((string?)x.Attribute("name")) is var flag and > 0 && (string?)x.Attribute("mobref") is { Length: > 0 } mob) byFaction[flag] = mob;
            guards[(string?)g.Attribute("name") ?? ""] = byFaction;
        }
        var d = Default;
        return new FortressData
        {
            AnnouncementSeconds = D("presiegeannouncementdurationsec", d.AnnouncementSeconds), PreSiegeSeconds = D("presiegedurationsec", d.PreSiegeSeconds),
            ResetSeconds = D("postsiegeresetdelaysec", d.ResetSeconds), DefenseGoal = D("activesiegedefensepointgoal", d.DefenseGoal),
            DefensePerSecond = D("activesiegedefensepointspersecond", d.DefensePerSecond),
            OwnerClaimSeconds = D("ownerclaimtime", d.OwnerClaimSeconds), OwnerUnclaimSeconds = D("ownerunclaimtime", d.OwnerUnclaimSeconds),
            OwnerNoTeamClaimSeconds = D("ownernoteamclaimtime", d.OwnerNoTeamClaimSeconds), AttackerClaimSeconds = D("attackerclaimtime", d.AttackerClaimSeconds),
            AttackerUnclaimSeconds = D("attackerunclaimtime", d.AttackerUnclaimSeconds), AttackerNoTeamUnclaimSeconds = D("attackernoteamunclaimtime", d.AttackerNoTeamUnclaimSeconds),
            Guards = guards,
        };
    }
}

/// <summary>
/// StageR fw-zone (SPEC §2): factionwarfareredzoneevent(_europe|_asia).xml and the times(_europe|_asia).xml it refers to: the bandit assault.
/// <list type="bullet">
/// <item><c>redzoneevent</c>: advancenotificationminutes 15, the six placement bonuses (496217.11 … 165405.70 faction points) and minimumdivisor 20;
/// <c>phase</c> × 2 (45 min, rzesupplymultiplier 1.0; 30 min, 1.5); <c>eventstarttime[@ref @offsetseconds @chance]</c>: a times.xml Time
/// (FW_DUCHY_EVALUATION_TIME_OF_DAY_n, FW_RZE_POSSIBLE_START_TIME_OF_DAY_n) − 900 s, rolled with the chance.</item>
/// <item>The captured 479 (Europe, 2026-10-15 19:36 UTC: phase Phase1 until 19:45) fits a start rolled at 18:45 (19:00 − 900 s), 15 min of
/// announcement, Phase1 19:00-19:45, Phase2 until 20:15 [infer].</item>
/// </list>
/// </summary>
public sealed class RedZoneEventData
{
    public sealed record Phase(double Minutes, double SupplyMultiplier, string Buff);

    public sealed record Start(TimeSpan TimeOfDay, double OffsetSeconds, double Chance);

    public double AnnouncementMinutes { get; init; } = 15;
    public IReadOnlyList<double> Bonuses { get; init; } = [496217.11, 413514.26, 330811.41, 165405.70, 165405.70, 165405.70];
    public double MinimumDivisor { get; init; } = 20;
    public IReadOnlyList<Phase> Phases { get; init; } = [new(45, 1.0, "FACTION_PVE_BONUS_RZE_PHASE_1"), new(30, 1.5, "FACTION_PVE_BONUS_RZE_PHASE_2")];
    public IReadOnlyList<Start> Starts { get; init; } = [];
    public string? RewardChest { get; init; } = "FACTION_TREASURE_MEDIUM_FORTRESS_RZE";

    public static readonly RedZoneEventData Default = new();

    /// <summary>The event file over <paramref name="times"/> (Time uniquename → time of day, UTC [infer]).</summary>
    public static RedZoneEventData Load(XElement root, IReadOnlyDictionary<string, TimeSpan> times)
    {
        var inv = CultureInfo.InvariantCulture;
        static string S(XElement e, string a) => ((string?)e.Attribute(a) ?? "").Trim();
        double D(XElement? e, string a, double fallback) => e is not null && double.TryParse(S(e, a), NumberStyles.Float, inv, out var v) ? v : fallback;
        var z = root.Element("redzoneevent");
        var d = Default;
        if (z is null) return d;
        var phases = z.Elements("phase").Select(p => new Phase(D(p, "durationminutes", 30), D(p, "rzesupplymultiplier", 1), S(p, "buff"))).ToList();
        var starts = z.Elements("eventstarttime")
            .Where(s => times.ContainsKey(S(s, "ref")))
            .Select(s => new Start(times[S(s, "ref")], D(s, "offsetseconds", 0), Math.Clamp(D(s, "chance", 0), 0, 1))).ToList();
        string[] names = ["firstbonus", "secondbonus", "thirdbonus", "fourthbonus", "fifthbonus", "sixthbonus"];
        return new RedZoneEventData
        {
            AnnouncementMinutes = D(z, "advancenotificationminutes", d.AnnouncementMinutes),
            Bonuses = [.. names.Select((n, i) => D(z, n, d.Bonuses[i]))],
            MinimumDivisor = Math.Max(1, D(z, "minimumdivisor", d.MinimumDivisor)),
            Phases = phases.Count > 0 ? phases : d.Phases,
            Starts = starts,
            RewardChest = (string?)z.Element("phasetwoeventsettings")?.Attribute("rewardlootchestname") ?? d.RewardChest,
        };
    }

    /// <summary>times.xml: every &lt;Time uniquename time="hh:mm:ss"&gt;.</summary>
    public static Dictionary<string, TimeSpan> Times(XElement root)
    {
        var times = new Dictionary<string, TimeSpan>(StringComparer.Ordinal);
        foreach (var t in root.Elements("Time"))
            if ((string?)t.Attribute("uniquename") is { } name && TimeSpan.TryParse((string?)t.Attribute("time"), CultureInfo.InvariantCulture, out var at))
                times[name] = at;
        return times;
    }
}
