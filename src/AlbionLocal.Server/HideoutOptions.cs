using System.Globalization;

namespace AlbionLocal.Server;

/// <summary>Hideouts:BankVault: what the bank inside a hideout opens.</summary>
public enum HideoutBankVault
{
    /// <summary>Official: the hideout's own vault, one location of its own ("@HIDEOUT@&lt;guid&gt;|&lt;tab&gt;"), never a city's.</summary>
    Own,

    /// <summary>The vault of the city nearest to the hideout's zone (fewer vaults; not the official rule).</summary>
    City,
}

/// <summary>
/// The guild hideouts (Hideouts:*; analysis/work/stageP/hideouts/SPEC.md §6, phases H1-H4; <see cref="Systems.Hideouts"/>): defaults = Preset
/// official, <see cref="PveDefaults"/> the pve preset's (the same: the SPEC proposes no pve deviation for these keys). "data" = the client's
/// GameData value (null here).
/// </summary>
public sealed record HideoutOptions
{
    /// <summary>Hideouts:Enabled (true): the hideout kit is placed, hideouts stand, can be entered and used; false = the kit cannot be placed and no hideout is shown.</summary>
    public bool Enabled { get; init; } = true;

    /// <summary>Hideouts:MaxPerGuild ("data" = gamedata Guild maxhideouts 6).</summary>
    public int? MaxPerGuild { get; init; }

    /// <summary>Hideouts:RequireTerritory (true): only in a zone with a world.xml territory (the Roads: a hideout tunnel); false = any zone that allows hideouts.</summary>
    public bool RequireTerritory { get; init; } = true;

    /// <summary>Hideouts:RequirePrimeTime (true): the kit only during the zone's prime time (items.xml primetimedurationminutes 60 from the timeregion's start).</summary>
    public bool RequirePrimeTime { get; init; } = true;

    /// <summary>Hideouts:PrimeTime ("data" = the zone's timeregion; "HH:MM" = that UTC start for every zone).</summary>
    public TimeSpan? PrimeTime { get; init; }

    /// <summary>
    /// Hideouts:ConstructionSeconds ("data" = hideouts.xml: 1200 s, invulnerable until the next prime time, 1200 s; a number = the whole
    /// construction in that many seconds, split evenly over the three phases, a local shortcut).
    /// </summary>
    public double? ConstructionSeconds { get; init; }

    /// <summary>Hideouts:Upkeep (true): the daily food (dailyupkeepnutrition) at the zone's prime time; false = nothing is eaten.</summary>
    public bool Upkeep { get; init; } = true;

    /// <summary>Hideouts:PowerDecay (true): kept for phase H6 (power cores); read, not used yet.</summary>
    public bool PowerDecay { get; init; } = true;

    /// <summary>
    /// Hideouts:Sieges (false): attacks on hideouts (phase H7) are not made; the construction phases are timers and a starving hideout only
    /// loses its shields. true changes nothing yet (no attacks are made).
    /// </summary>
    public bool Sieges { get; init; }

    /// <summary>Hideouts:BankVault (Own, official): the bank inside opens the hideout's own vault; City = the nearest city's vault.</summary>
    public HideoutBankVault BankVault { get; init; } = HideoutBankVault.Own;

    /// <summary>Hideouts:RecoveryFeeFactor and RecoveryWaitSeconds ("data" = hideouts.xml itemrecovery 450 / 604800): read for phase H5.</summary>
    public double? RecoveryFeeFactor { get; init; }

    public double? RecoveryWaitSeconds { get; init; }

    /// <summary>Hideouts:Headquarters (true): read for phase H8 (needs GvG season points).</summary>
    public bool Headquarters { get; init; } = true;

    /// <summary>
    /// Hideouts:BlockRadius (50 m): how far a hideout must stand from a territory's monolith, a castle, a cluster exit and the world.xml
    /// outposts of the zone (the @HIDEOUT_PLACEMENT_BLOCKED_BY_* rules; their radii are not in the client data, SPEC §1.1 [choice]).
    /// </summary>
    public double BlockRadius { get; init; } = 50;

    /// <summary>Hideouts:RespawnAtHome (true): a death respawns at the character's home hideout (Make Home) instead of its city.</summary>
    public bool RespawnAtHome { get; init; } = true;

    /// <summary>The pve preset's values (the SPEC proposes none of its own for hideouts).</summary>
    public static readonly HideoutOptions PveDefaults = new();

    /// <summary>The Hideouts section out of <paramref name="values"/> over <paramref name="d"/>.</summary>
    internal static HideoutOptions Load(Dictionary<string, string> values, HideoutOptions d)
    {
        var inv = CultureInfo.InvariantCulture;
        string? Take(string key) => values.Remove(key, out var v) ? v : null;
        bool IsData(string v) => v.Equals("data", StringComparison.OrdinalIgnoreCase);
        bool Flag(string key, bool fallback) => Take(key) is not { } v ? fallback
            : bool.TryParse(v, out var f) ? f : throw new InvalidDataException($"{key}: '{v}' is not true or false");
        double Number(string key, double fallback, double min, double max) => Take(key) is not { } v ? fallback
            : double.TryParse(v, NumberStyles.Float, inv, out var n) && double.IsFinite(n) && n >= min && n <= max ? n
            : throw new InvalidDataException($"{key}: '{v}' is not {min.ToString(inv)}..{max.ToString(inv)}");
        double? DataNumber(string key, double? fallback, double min, double max) => Take(key) is not { } v ? fallback : IsData(v) ? null
            : double.TryParse(v, NumberStyles.Float, inv, out var n) && double.IsFinite(n) && n >= min && n <= max ? n
            : throw new InvalidDataException($"{key}: '{v}' is not data or {min.ToString(inv)}..{max.ToString(inv)}");
        var prime = Take("Hideouts:PrimeTime");
        var vault = Take("Hideouts:BankVault");
        return new HideoutOptions
        {
            Enabled = Flag("Hideouts:Enabled", d.Enabled),
            MaxPerGuild = DataNumber("Hideouts:MaxPerGuild", d.MaxPerGuild, 0, 1000) is { } max ? (int)max : null,
            RequireTerritory = Flag("Hideouts:RequireTerritory", d.RequireTerritory),
            RequirePrimeTime = Flag("Hideouts:RequirePrimeTime", d.RequirePrimeTime),
            PrimeTime = prime is null ? d.PrimeTime : IsData(prime) ? null
                : TimeSpan.TryParseExact(prime, @"hh\:mm", inv, out var at) && at < TimeSpan.FromDays(1) ? at
                : throw new InvalidDataException($"Hideouts:PrimeTime: '{prime}' is not data or HH:MM (UTC)"),
            ConstructionSeconds = DataNumber("Hideouts:ConstructionSeconds", d.ConstructionSeconds, 0, 30 * 86_400),
            Upkeep = Flag("Hideouts:Upkeep", d.Upkeep),
            PowerDecay = Flag("Hideouts:PowerDecay", d.PowerDecay),
            Sieges = Flag("Hideouts:Sieges", d.Sieges),
            BankVault = vault is null ? d.BankVault : Enum.TryParse<HideoutBankVault>(vault, ignoreCase: true, out var bv) && Enum.IsDefined(bv) ? bv
                : throw new InvalidDataException($"Hideouts:BankVault: '{vault}' is not Own or City"),
            RecoveryFeeFactor = DataNumber("Hideouts:RecoveryFeeFactor", d.RecoveryFeeFactor, 0, 1_000_000),
            RecoveryWaitSeconds = DataNumber("Hideouts:RecoveryWaitSeconds", d.RecoveryWaitSeconds, 0, 365 * 86_400),
            Headquarters = Flag("Hideouts:Headquarters", d.Headquarters),
            BlockRadius = Number("Hideouts:BlockRadius", d.BlockRadius, 0, 1000),
            RespawnAtHome = Flag("Hideouts:RespawnAtHome", d.RespawnAtHome),
        };
    }
}
