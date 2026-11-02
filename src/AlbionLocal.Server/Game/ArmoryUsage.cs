namespace AlbionLocal.Server.Game;

// Installed 345319 Albion.Common.Armory.Metric. Stored in ordinary units, converted to a7m only on the wire.
public enum ArmoryMetric
{
    Uses, Kills, Deaths, DamageReceived, DamageDealt, HealingDone, OutpostCaptured, DynamicCamp,
    MobsKilled, SuccessWin, BuildingDamage, PvPFameGained, PvEFameGained, VortexCore, TreasureSmuggler,
    PvEEncounters, PvPEncounters, HealingReceived, Duration, MinItemPower, AvgItemPower, MaxItemPower,
    CrowdControlsInflicted,
}

/// <summary>A single observed equipment/activity interval. Its stable ID makes periodic saves idempotent.
/// A process restart closes the old interval; no offline time, inventory snapshots or historical backfill count as use.</summary>
public sealed class ArmorySample
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid Character { get; set; }
    public int Activity { get; set; }
    public string Location { get; set; } = "";
    public string Equipment { get; set; } = "";
    public string Consumables { get; set; } = "";
    public string Spells { get; set; } = "";
    public string[] Slots { get; set; } = [];
    public long Started { get; set; }
    public long Updated { get; set; }
    public decimal DurationSeconds { get; set; }
    public decimal[] Metrics { get; set; } = new decimal[23];
    public int MinItemPower { get; set; }
    public int MaxItemPower { get; set; }
    public decimal ItemPowerSum { get; set; }
    public int ItemPowerObservations { get; set; }
    public decimal Value(ArmoryMetric metric) => metric == ArmoryMetric.Duration ? DurationSeconds
        : (int)metric < Metrics.Length ? Metrics[(int)metric] : 0;
}
