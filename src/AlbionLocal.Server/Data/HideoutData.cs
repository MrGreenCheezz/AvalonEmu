using System.Globalization;
using System.Xml.Linq;

namespace AlbionLocal.Server.Data;

/// <summary>A construction phase of hideouts.xml &lt;constructionphases&gt; (bcv.d): its length, durability, recovery; &lt;phaseinvulnerable/&gt; lasts until the next prime time.</summary>
public sealed record HideoutPhase(double Seconds, long MaxDurability, double Recovery, bool Invulnerable);

/// <summary>
/// An upgrade level of hideouts.xml &lt;upgradelevels&gt; (bcv.e, NewHideoutObject key 13 indexes this list): 0 = HIDEOUT-0001a, 1 = …b, 2 = …c; the
/// world.xml cluster of its interior, the shields (key 14 at full), durability, the daily food and the food storage, the upgrade to it (silver,
/// seconds, resources; none for the first) and who may make it a home.
/// </summary>
public sealed record HideoutLevel(int Index, string Cluster, int MaxShields, long MaxDurability, long DailyUpkeep, long MaxNutrition, long UpgradeSilver,
    double UpgradeSeconds, IReadOnlyList<(string Item, int Count)> UpgradeResources, bool HomeGuild, bool HomeAlliance, bool HomePublic);

/// <summary>
/// The guild hideouts' client data (analysis/work/stageP/hideouts/SPEC.md §1.1): hideouts.xml (starting food, entry channel, the construction
/// phases, the three upgrade levels), the kit of items.xml (&lt;hideoutitem UNIQUE_HIDEOUT&gt;: distances, placement time, prime-time window), gamedata
/// Guild maxhideouts and the worldsettings clustertypes with hideoutplacementallowed (OPENPVP_BLACK_1-6) or the Roads' hideout tunnels. Plus the
/// instance ids "@HIDEOUT@&lt;guid&gt;" (a4l ClusterInstanceType HIDEOUT = 8). Immutable; <see cref="Fallback"/> holds the 345319 values.
/// </summary>
public sealed class HideoutData
{
    /// <summary>"@HIDEOUT@": a4l.f(Guid) for ClusterInstanceType HIDEOUT (the management's key 1 parses it back, ckj.af 0x180b38500).</summary>
    public const string InstancePrefix = "@HIDEOUT@";

    /// <summary>The kit (items.xml hideoutitem) and the building (buildings.xml hideoutbuilding) share this name.</summary>
    public const string Kit = "UNIQUE_HIDEOUT";

    public long StartingNutrition { get; init; } = 15_000;

    /// <summary>enterchanneltime: the channel at the entrance (s).</summary>
    public double EnterChannelSeconds { get; init; } = 4;

    public double GracePeriodDays { get; init; } = 3;

    public IReadOnlyList<HideoutPhase> Phases { get; init; } = [new(1200, 20_000, 0, false), new(0, 0, 0, true), new(1200, 40_000, 15, false)];

    public IReadOnlyList<HideoutLevel> Levels { get; init; } =
    [
        new(0, "HIDEOUT-0001a", 2, 50_000, 15_000, 150_000, 0, 0, [], true, false, false),
        new(1, "HIDEOUT-0001b", 4, 100_000, 20_000, 400_000, 20_000_000, 172_800,
            [("UNIQUE_GVGTOKEN_GENERIC", 1000), ("T1_WOOD", 1250), ("T1_ROCK", 125), ("T6_STONEBLOCK", 500)], true, false, false),
        new(2, "HIDEOUT-0001c", 5, 150_000, 25_000, 750_000, 80_000_000, 345_600,
            [("UNIQUE_GVGTOKEN_GENERIC", 4000), ("T1_WOOD", 5000), ("T1_ROCK", 500), ("T6_STONEBLOCK", 2000)], true, true, false),
    ];

    /// <summary>The kit's mindistance (to another hideout, m) and mindistanceintunnel (the Roads).</summary>
    public double MinDistance { get; init; } = 180;

    public double MinDistanceInTunnel { get; init; } = 60;

    /// <summary>The kit's placementduration (s): the cast before the hideout stands.</summary>
    public double PlacementSeconds { get; init; } = 15;

    /// <summary>The kit's primetimedurationminutes: how long from the prime time's start the kit may be placed.</summary>
    public double PrimeTimeMinutes { get; init; } = 60;

    /// <summary>gamedata Guild maxhideouts.</summary>
    public int MaxPerGuild { get; init; } = 6;

    /// <summary>The worldsettings clustertypes with hideoutplacementallowed="true".</summary>
    public IReadOnlySet<string> PlacementTypes { get; init; } = new HashSet<string>(StringComparer.Ordinal)
        { "DEBUG_BLACK", "OPENPVP_BLACK_1", "OPENPVP_BLACK_2", "OPENPVP_BLACK_2_PLAYTEST", "OPENPVP_BLACK_3", "OPENPVP_BLACK_4", "OPENPVP_BLACK_5", "OPENPVP_BLACK_6" };

    /// <summary>The worldsettings clustertypes whose archetype is a hideout tunnel of the Roads of Avalon (TUNNEL_HIDEOUT, TUNNEL_HIDEOUT_DEEP).</summary>
    public IReadOnlySet<string> TunnelTypes { get; init; } = new HashSet<string>(StringComparer.Ordinal) { "TUNNEL_HIDEOUT", "TUNNEL_HIDEOUT_DEEP" };

    /// <summary>itemrecovery feefactor and notclaimabletime (phase H5).</summary>
    public double RecoveryFeeFactor { get; init; } = 450;

    public double RecoveryWaitSeconds { get; init; } = 604_800;

    /// <summary>The 345319 values without any file.</summary>
    public static readonly HideoutData Fallback = new();

    /// <summary>Level <paramref name="index"/> (0-2); null beyond.</summary>
    public HideoutLevel? Level(int index) => index >= 0 && index < Levels.Count ? Levels[index] : null;

    /// <summary>The instance id of hideout <paramref name="guid"/>.</summary>
    public static string InstanceId(Guid guid) => InstancePrefix + guid.ToString("D");

    /// <summary>Whether <paramref name="cluster"/> is a hideout instance id.</summary>
    public static bool IsInstance(string? cluster) => cluster?.StartsWith(InstancePrefix, StringComparison.Ordinal) == true;

    /// <summary>The hideout Guid of instance id <paramref name="cluster"/>; null when it is none.</summary>
    public static Guid? GuidOf(string? cluster) =>
        IsInstance(cluster) && Guid.TryParse(cluster![InstancePrefix.Length..], out var g) ? g : null;

    private static double D(XElement? e, string name, double fallback) =>
        e is not null && double.TryParse((string?)e.Attribute(name), NumberStyles.Float, CultureInfo.InvariantCulture, out var d) ? d : fallback;

    private static bool B(XElement? e, string name) => string.Equals((string?)e?.Attribute(name), "true", StringComparison.OrdinalIgnoreCase);

    /// <summary>The values out of hideouts.xml, the kit's items.xml attributes, gamedata.xml and worldsettings.xml (any may be null: its part stays the fallback's).</summary>
    public static HideoutData Load(XElement? hideouts, IReadOnlyDictionary<string, string>? kit, XElement? gameData, XElement? worldSettings)
    {
        var f = Fallback;
        var h = hideouts?.Element("hideout");
        var phases = h?.Element("constructionphases")?.Elements()
            .Select(p => p.Name.LocalName == "phaseinvulnerable" ? new HideoutPhase(0, 0, 0, true)
                : new HideoutPhase(D(p, "durationseconds", 0), (long)D(p, "maxdurability", 0), D(p, "durabilityrecovery", 0), false)).ToList();
        var levels = h?.Element("upgradelevels")?.Elements("level").Select((l, i) =>
        {
            var up = l.Element("upgraderequirement");
            var home = l.Element("makehome");
            return new HideoutLevel(i, (string?)l.Attribute("cluster") ?? "", (int)D(l, "maxshields", 0), (long)D(l, "maxdurability", 0),
                (long)D(l, "dailyupkeepnutrition", 0), (long)D(l, "maximumnutritionstorage", 0), (long)D(up, "silver", 0), D(up, "time", 0),
                [.. (up?.Elements("upgraderesource") ?? []).Select(r => ((string?)r.Attribute("uniquename") ?? "", (int)D(r, "count", 0))).Where(r => r.Item1.Length > 0)],
                B(home, "guild"), B(home, "alliance"), B(home, "public"));
        }).ToList();
        double K(string name, double fallback) => kit?.GetValueOrDefault(name) is { } v && double.TryParse(v, NumberStyles.Float, CultureInfo.InvariantCulture, out var d) ? d : fallback;
        var guild = gameData?.Descendants("Guild").FirstOrDefault(g => g.Attribute("maxhideouts") is not null);
        HashSet<string>? placement = null, tunnels = null;
        if (worldSettings is not null)
        {
            placement = new HashSet<string>(StringComparer.Ordinal);
            tunnels = new HashSet<string>(StringComparer.Ordinal);
            foreach (var c in worldSettings.Descendants("clustertype"))
            {
                if ((string?)c.Attribute("name") is not { } name) continue;
                if (B(c, "hideoutplacementallowed")) placement.Add(name);
                if ((string?)c.Attribute("archetype") is "TUNNEL_HIDEOUT" or "TUNNEL_HIDEOUT_DEEP") tunnels.Add(name);
            }
        }
        var recovery = hideouts?.Element("itemrecovery");
        return new HideoutData
        {
            StartingNutrition = (long)D(h, "startingnutrition", f.StartingNutrition),
            EnterChannelSeconds = D(h, "enterchanneltime", f.EnterChannelSeconds),
            GracePeriodDays = D(h, "graceperioddays", f.GracePeriodDays),
            Phases = phases is { Count: > 0 } ? phases : f.Phases,
            Levels = levels is { Count: > 0 } ? levels : f.Levels,
            MinDistance = K("mindistance", f.MinDistance),
            MinDistanceInTunnel = K("mindistanceintunnel", f.MinDistanceInTunnel),
            PlacementSeconds = K("placementduration", f.PlacementSeconds),
            PrimeTimeMinutes = K("primetimedurationminutes", f.PrimeTimeMinutes),
            MaxPerGuild = guild is null ? f.MaxPerGuild : (int)D(guild, "maxhideouts", f.MaxPerGuild),
            PlacementTypes = placement is { Count: > 0 } ? placement : f.PlacementTypes,
            TunnelTypes = tunnels is { Count: > 0 } ? tunnels : f.TunnelTypes,
            RecoveryFeeFactor = D(recovery, "feefactor", f.RecoveryFeeFactor),
            RecoveryWaitSeconds = D(recovery, "notclaimabletime", f.RecoveryWaitSeconds),
        };
    }

    /// <summary>The files of the client's GameData <paramref name="folder"/> (gamedata/worldsettings of <paramref name="region"/>, "" = the West's); a missing file keeps its part's fallback.</summary>
    public static HideoutData Load(string folder, string region, IReadOnlyDictionary<string, string>? kit)
    {
        var suffix = region.Length == 0 ? "" : "_" + region;
        XElement? Root(string name) => File.Exists(Path.Combine(folder, name)) ? GameDataFile.Open(folder, name).Root() : null;
        return Load(Root("hideouts.bin"), kit, Root($"gamedata{suffix}.bin") ?? Root("gamedata.bin"), Root($"worldsettings{suffix}.bin") ?? Root("worldsettings.bin"));
    }
}
