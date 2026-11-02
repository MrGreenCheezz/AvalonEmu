namespace AlbionLocal.Server.Game;

/// <summary>
/// Prospective character statistics, schema v48. Coverage starts with the first observed activity;
/// legacy total fame, Journal progress and Armory's retained samples cannot reconstruct these totals.
/// Native event 142 has no coverage field. Keep it here rather than inventing a character statement.
/// All fame uses the same raw a7m units as Character.Fame. Histories retain the latest 20 of each kind.
/// </summary>
public sealed class CharacterStatistics
{
    public long CoveredSinceUtc { get; set; }
    public long FameBeforeCoverage { get; set; }
    public long PveFame { get; set; }
    public long PvpFame { get; set; }
    public long GatheringFame { get; set; }
    public long CraftingFame { get; set; }
    // Farming has no confirmed field in the native four-row breakdown.
    public long FarmingFame { get; set; }
    public int PlayersKilled { get; set; }
    public int Deaths { get; set; }
    public int Knockdowns { get; set; }
    public int TimesKnockedDown { get; set; }
    public List<CharacterCombatRecord> Kills { get; set; } = [];
    public List<CharacterCombatRecord> DeathHistory { get; set; } = [];
    public List<CharacterCombatRecord> KnockdownHistory { get; set; } = [];
    public List<CharacterCombatRecord> KnockedDownHistory { get; set; } = [];

    public CharacterCombatRecord? FindCombat(Guid id) => Kills.Concat(DeathHistory).Concat(KnockdownHistory)
        .Concat(KnockedDownHistory).FirstOrDefault(entry => entry.Id == id);

    public CharacterStatistics Clone() => new()
    {
        CoveredSinceUtc = CoveredSinceUtc, FameBeforeCoverage = FameBeforeCoverage,
        PveFame = PveFame, PvpFame = PvpFame, GatheringFame = GatheringFame, CraftingFame = CraftingFame,
        FarmingFame = FarmingFame, PlayersKilled = PlayersKilled, Deaths = Deaths,
        Knockdowns = Knockdowns, TimesKnockedDown = TimesKnockedDown,
        Kills = [.. Kills], DeathHistory = [.. DeathHistory],
        KnockdownHistory = [.. KnockdownHistory], KnockedDownHistory = [.. KnockedDownHistory],
    };
}

/// <summary>
/// Immutable recorded opponent and the detail JSON captured at the result, before equipment drops.
/// Id is shared by the two participants' rows; it is an actual record id, not a character/object id.
/// FighterType: 1 player, 2 mob. Details is the native KillHistoryDetails JSON (event 148 key 2).
/// </summary>
public sealed record CharacterCombatRecord(Guid Id, string Opponent, int FighterType, long Fame,
    long Ticks, string Details, bool Knockdown);
