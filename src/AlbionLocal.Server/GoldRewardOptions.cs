using System.Globalization;

namespace AlbionLocal.Server;

/// <summary>
/// The gold rewards of play (Gold:Rewards:*, analysis/work/stageR/gold/SPEC.md): NOT official behaviour. The official game hands out no gold
/// in play (this client's data has no gold reward anywhere, SPEC §1); its gold comes from the store and the gold market, which this server
/// has neither of. So that gold prices (a guild crest, victory emote charges) can be paid at all, a few bounded reward moments give a little
/// gold, delivered through <see cref="Systems.Wallet"/> with a system message that says why. On by default under every preset: a deliberate
/// departure from "official by default", made at the user's request on 2026-11-17. Each amount is whole gold; 0 turns that source off.
/// </summary>
public sealed record GoldRewardOptions
{
    /// <summary>
    /// Gold:Rewards:Enabled (true under every preset, NOT official: the user's request of 2026-11-17, as the server has no store and no gold
    /// market to get gold from): the gold rewards below; false = gold only through the GM's .money, as officially without a store.
    /// </summary>
    public bool Enabled { get; init; } = true;

    /// <summary>Gold:Rewards:DailyFame (100): once per character and daily period, when the day's fame reaches Gold:Rewards:DailyFameRequired.</summary>
    public long DailyFame { get; init; } = 100;

    /// <summary>Gold:Rewards:DailyFameRequired (10000): the fame of the day (kills, PvP, gathering, fishing, farming, crafting; no tomes, no GM grants).</summary>
    public long DailyFameRequired { get; init; } = 10_000;

    /// <summary>Gold:Rewards:ConquerorWeekly (250): once a week, when the Conqueror's Challenge's weekly might reaches its chest (premium or not).</summary>
    public long ConquerorWeekly { get; init; } = 250;

    /// <summary>Gold:Rewards:JournalClaim (10): each Albion Journal reward claimed (op 514), up to Gold:Rewards:JournalWeeklyCap a week.</summary>
    public long JournalClaim { get; init; } = 10;

    /// <summary>Gold:Rewards:JournalWeeklyCap (200): the most gold the journal claims give in one week (0 = no cap; each reward is claimed once ever).</summary>
    public long JournalWeeklyCap { get; init; } = 200;

    /// <summary>Gold:Rewards:FactionDaily (50): once a campaign day, when the faction warfare campaign's last daily reward is reached.</summary>
    public long FactionDaily { get; init; } = 50;

    /// <summary>The Gold:Rewards section out of <paramref name="values"/> over <paramref name="d"/>.</summary>
    internal static GoldRewardOptions Load(Dictionary<string, string> values, GoldRewardOptions d)
    {
        var inv = CultureInfo.InvariantCulture;
        string? Take(string key) => values.Remove(key, out var v) ? v : null;
        long Amount(string key, long fallback, long max) => Take(key) is not { } v ? fallback
            : long.TryParse(v, NumberStyles.Integer, inv, out var n) && n >= 0 && n <= max ? n
            : throw new InvalidDataException($"{key}: '{v}' is not a whole number 0..{max.ToString(inv)}");
        bool Flag(string key, bool fallback) => Take(key) is not { } v ? fallback
            : bool.TryParse(v, out var f) ? f : throw new InvalidDataException($"{key}: '{v}' is not true or false");
        return new GoldRewardOptions
        {
            Enabled = Flag("Gold:Rewards:Enabled", d.Enabled),
            DailyFame = Amount("Gold:Rewards:DailyFame", d.DailyFame, 100_000),
            DailyFameRequired = Amount("Gold:Rewards:DailyFameRequired", d.DailyFameRequired, 1_000_000_000),
            ConquerorWeekly = Amount("Gold:Rewards:ConquerorWeekly", d.ConquerorWeekly, 100_000),
            JournalClaim = Amount("Gold:Rewards:JournalClaim", d.JournalClaim, 100_000),
            JournalWeeklyCap = Amount("Gold:Rewards:JournalWeeklyCap", d.JournalWeeklyCap, 10_000_000),
            FactionDaily = Amount("Gold:Rewards:FactionDaily", d.FactionDaily, 100_000),
        };
    }
}
