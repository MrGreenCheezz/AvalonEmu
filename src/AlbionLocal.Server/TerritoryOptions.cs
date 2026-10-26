using System.Globalization;

namespace AlbionLocal.Server;

/// <summary>
/// The guild territories and castles (Territories:*, Castles:*; analysis/work/stageP/territories/SPEC.md §7; <see cref="Systems.Territories"/>):
/// defaults = Preset official, <see cref="PveDefaults"/> the pve preset's. "data" = the client's GameData value (null here). Raids and
/// Attacks are read and kept for the later phases; the others are in use (P1 world presence, P3 claims and upkeep, P4 scoring, P5 castles).
/// </summary>
public sealed record TerritoryOptions
{
    /// <summary>Territories:Enabled (true, both presets): the monoliths and castles in their clusters and the territory answers; false = none (the map keeps skipping territory clusters).</summary>
    public bool Enabled { get; init; } = true;

    /// <summary>
    /// Territories:WorldMapInfo (false, both presets): ClusterInfoUpdate keys 4-18 (one entry per territory of the cluster) for the 269 territory
    /// clusters; false = those clusters stay unanswered, as before. The client's map indexes the arrays without checks (a wrong length throws in
    /// WorldmapCluster.UpdateTerritories), and no official sample confirms the shape yet (capture T1).
    /// </summary>
    public bool WorldMapInfo { get; init; }

    /// <summary>Territories:ClaimCost ("data" = territorytypes claimcost 2000000; pve 200000): silver from the guild account per claim.</summary>
    public long? ClaimCost { get; init; }

    /// <summary>Territories:GuildFameRequirement ("data" = gamedata claimterritoryguildfamerequirement 1000000 per territory; pve 0).</summary>
    public long? GuildFameRequirement { get; init; }

    /// <summary>Territories:PrimeTimeOnly (true; pve false): claim, raid and battle only in the cluster's prime time (timeregion start + 1 h).</summary>
    public bool PrimeTimeOnly { get; init; } = true;

    /// <summary>Territories:PrimeTimeOffsetHours (0, both presets): shifts every timeregion's prime time (players in other time zones).</summary>
    public double PrimeTimeOffsetHours { get; init; }

    /// <summary>Territories:ClaimChannelSeconds ("data" = gamedata territoryclaimtimeinsec 15).</summary>
    public double? ClaimChannelSeconds { get; init; }

    /// <summary>Territories:Food (true; pve false): nutrition storage and the loss of an empty territory (P3).</summary>
    public bool Food { get; init; } = true;

    /// <summary>Territories:SeasonSchedule ("data" = the season's seasontimes; pve "off" = always claimable, never reset).</summary>
    public bool SeasonSchedule { get; init; } = true;

    /// <summary>Territories:Invader (true, both presets): the season's Tower Guardian (spawninvader) blocks claiming until killed (P3; none with SeasonSchedule off).</summary>
    public bool Invader { get; init; } = true;

    /// <summary>Territories:SeasonPointsFactor (1, both presets): × every territory and castle season point.</summary>
    public double SeasonPointsFactor { get; init; } = 1;

    /// <summary>Territories:ScoreTickMinutes (60, both presets): the scoring tick (P4); the official tick is not in the data (capture T6), a placeholder.</summary>
    public double ScoreTickMinutes { get; init; } = 60;

    /// <summary>
    /// Territories:PrimeTimeBonus (1, both presets): × a tick's base points paid once more (activity TerritoryPrimeTime) for a tick in the
    /// territory's prime time (P4; @SEASON_POINT_BREAKDOWN_TERRITORYPRIMETIME, the amount is not in the data: capture T6); 0 = none.
    /// </summary>
    public double PrimeTimeBonus { get; init; } = 1;

    /// <summary>Territories:Raids (true; pve false) and Territories:Attacks (true, both presets): raids (P6) and scheduled attacks (P7).</summary>
    public bool Raids { get; init; } = true;

    public bool Attacks { get; init; } = true;

    /// <summary>Territories:NpcOwners (false; pve true): unclaimed watchtowers held by an NPC holder whose fortification guards must die before a claim (P3).</summary>
    public bool NpcOwners { get; init; }

    /// <summary>Territories:MaxPerGuild (0 = no cap, both presets; officially the alliance penalty limits instead).</summary>
    public int MaxPerGuild { get; init; }

    /// <summary>Castles:Enabled (true, both presets): the castles and castle outposts with their fights, captures, owners and chests (phase P5); needs Territories:Enabled.</summary>
    public bool CastlesEnabled { get; init; } = true;

    /// <summary>Castles:Chests (true) and Castles:ChestSchedule ("data" = treasuredistribution CASTLE_* every 2 h UTC 10-22; "hourly"), P5.</summary>
    public bool CastleChests { get; init; } = true;

    public bool CastleChestsHourly { get; init; }

    /// <summary>Castles:ChestNeedsOwnership (true; pve false): only the owner guild opens a castle chest (lootchests requirescastleownership), P5.</summary>
    public bool CastleChestNeedsOwnership { get; init; } = true;

    /// <summary>Castles:GuardScale (1; pve 0.6): × the health of castle guards, elites and the lord, P5.</summary>
    public double CastleGuardScale { get; init; } = 1;

    /// <summary>Castles:SeasonPoints (0 = off until a capture gives the official value): season points per castle scoring tick, P5.</summary>
    public double CastleSeasonPoints { get; init; }

    /// <summary>The pve preset's values (SPEC §7).</summary>
    public static readonly TerritoryOptions PveDefaults = new()
    {
        ClaimCost = 200_000, GuildFameRequirement = 0, PrimeTimeOnly = false, Food = false, SeasonSchedule = false, Raids = false, NpcOwners = true,
        CastleChestNeedsOwnership = false, CastleGuardScale = 0.6,
    };

    /// <summary>The Territories and Castles sections out of <paramref name="values"/> over <paramref name="d"/>.</summary>
    internal static TerritoryOptions Load(Dictionary<string, string> values, TerritoryOptions d)
    {
        var inv = CultureInfo.InvariantCulture;
        string? Take(string key) => values.Remove(key, out var v) ? v : null;
        bool IsData(string v) => v.Equals("data", StringComparison.OrdinalIgnoreCase);
        double Number(string key, double fallback, double min, double max) => Take(key) is not { } v ? fallback
            : double.TryParse(v, NumberStyles.Float, inv, out var n) && double.IsFinite(n) && n >= min && n <= max ? n
            : throw new InvalidDataException($"{key}: '{v}' is not {min.ToString(inv)}..{max.ToString(inv)}");
        long? DataLong(string key, long? fallback, long max) => Take(key) is not { } v ? fallback : IsData(v) ? null
            : long.TryParse(v, NumberStyles.Integer, inv, out var n) && n >= 0 && n <= max ? n
            : throw new InvalidDataException($"{key}: '{v}' is not data or 0..{max.ToString(inv)}");
        bool Flag(string key, bool fallback) => Take(key) is not { } v ? fallback
            : bool.TryParse(v, out var f) ? f : throw new InvalidDataException($"{key}: '{v}' is not true or false");
        var channel = Take("Territories:ClaimChannelSeconds");
        var schedule = Take("Territories:SeasonSchedule");
        var timers = Take("Castles:Timers");
        if (timers is not null && !IsData(timers)) throw new InvalidDataException($"Castles:Timers: '{timers}' is not data (castles.xml)");
        var chestSchedule = Take("Castles:ChestSchedule");
        return new TerritoryOptions
        {
            Enabled = Flag("Territories:Enabled", d.Enabled),
            WorldMapInfo = Flag("Territories:WorldMapInfo", d.WorldMapInfo),
            ClaimCost = DataLong("Territories:ClaimCost", d.ClaimCost, 10_000_000_000),
            GuildFameRequirement = DataLong("Territories:GuildFameRequirement", d.GuildFameRequirement, 10_000_000_000),
            PrimeTimeOnly = Flag("Territories:PrimeTimeOnly", d.PrimeTimeOnly),
            PrimeTimeOffsetHours = Number("Territories:PrimeTimeOffsetHours", d.PrimeTimeOffsetHours, -24, 24),
            ClaimChannelSeconds = channel is null ? d.ClaimChannelSeconds : IsData(channel) ? null
                : double.TryParse(channel, NumberStyles.Float, inv, out var cs) && cs is >= 0 and <= 600 ? cs
                : throw new InvalidDataException($"Territories:ClaimChannelSeconds: '{channel}' is not data or 0..600"),
            Food = Flag("Territories:Food", d.Food),
            SeasonSchedule = schedule is null ? d.SeasonSchedule : IsData(schedule) ? true
                : schedule.Equals("off", StringComparison.OrdinalIgnoreCase) ? false
                : throw new InvalidDataException($"Territories:SeasonSchedule: '{schedule}' is not data or off"),
            Invader = Flag("Territories:Invader", d.Invader),
            SeasonPointsFactor = Number("Territories:SeasonPointsFactor", d.SeasonPointsFactor, 0, 1000),
            ScoreTickMinutes = Number("Territories:ScoreTickMinutes", d.ScoreTickMinutes, 1, 10_080),
            PrimeTimeBonus = Number("Territories:PrimeTimeBonus", d.PrimeTimeBonus, 0, 1000),
            Raids = Flag("Territories:Raids", d.Raids),
            Attacks = Flag("Territories:Attacks", d.Attacks),
            NpcOwners = Flag("Territories:NpcOwners", d.NpcOwners),
            MaxPerGuild = (int)Number("Territories:MaxPerGuild", d.MaxPerGuild, 0, 1000),
            CastlesEnabled = Flag("Castles:Enabled", d.CastlesEnabled),
            CastleChests = Flag("Castles:Chests", d.CastleChests),
            CastleChestsHourly = chestSchedule is null ? d.CastleChestsHourly : IsData(chestSchedule) ? false
                : chestSchedule.Equals("hourly", StringComparison.OrdinalIgnoreCase) ? true
                : throw new InvalidDataException($"Castles:ChestSchedule: '{chestSchedule}' is not data or hourly"),
            CastleChestNeedsOwnership = Flag("Castles:ChestNeedsOwnership", d.CastleChestNeedsOwnership),
            CastleGuardScale = Number("Castles:GuardScale", d.CastleGuardScale, 0.01, 100),
            CastleSeasonPoints = Number("Castles:SeasonPoints", d.CastleSeasonPoints, 0, 100_000),
        };
    }
}
