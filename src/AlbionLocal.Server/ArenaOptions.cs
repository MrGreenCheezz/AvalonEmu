using System.Globalization;

namespace AlbionLocal.Server;

/// <summary>
/// The arena queues, matches and ratings (Arena:*, analysis/work/stageN/arena/SPEC.md §4). Every key optional; the same under both presets
/// (registering is the consent to the fight). The modes' maps and team sizes are the client's (arena.bin, matchtypes.bin); the tickets, times
/// and rewards here are the server's scaled-down figures for a few players.
/// </summary>
public sealed record ArenaOptions
{
    /// <summary>Arena:Enabled (true): ArenaRegister and .arena join take players into the queues.</summary>
    public bool Enabled { get; init; } = true;

    /// <summary>Arena:Bots (true): with too few players a queue starts its match after <see cref="BotWaitSeconds"/> with player-bots in the missing slots.</summary>
    public bool Bots { get; init; } = true;

    /// <summary>Arena:BotWaitSeconds (20): how long the oldest registration waits for other players before bots fill in.</summary>
    public double BotWaitSeconds { get; init; } = 20;

    /// <summary>
    /// Arena:BotFill (Balanced): Balanced = each team gets bots up to the larger team's player count (one player against one bot; bots never fight
    /// bots, so a bot beside a player only helps against players); Full = every empty slot of both teams.
    /// </summary>
    public ArenaBotFill BotFill { get; init; } = ArenaBotFill.Balanced;

    /// <summary>Arena:BotTier (0): the bots' gear tier; 0 = from the players' average item power (T4 700 … T8 1100).</summary>
    public int BotTier { get; init; }

    /// <summary>Arena:BotDamageFactor (1) and Arena:BotHealthFactor (1): the arena bots' difficulty.</summary>
    public double BotDamageFactor { get; init; } = 1;

    public double BotHealthFactor { get; init; } = 1;

    /// <summary>Arena:ReadyCheck (true): a match found asks every player (ArenaRegistrationInfo status 7, the client's ready popup) before it starts.</summary>
    public bool ReadyCheck { get; init; } = true;

    /// <summary>Arena:AcceptSeconds (60, arena.bin acceptqueuetimeout): the ready check's time; who has not accepted by then leaves the queue.</summary>
    public double AcceptSeconds { get; init; } = 60;

    /// <summary>Arena:ArrivalSeconds (60): how long a match waits for its players to load the map before it starts without the missing ones.</summary>
    public double ArrivalSeconds { get; init; } = 60;

    /// <summary>Arena:CountdownSeconds (10): the truce before each fight (and each 1v1 round); nobody can hurt the other team then.</summary>
    public double CountdownSeconds { get; init; } = 10;

    /// <summary>Arena:RespawnSeconds (10, matchtypes MATCH_ARENA respawndelay): a team mode's player down comes back at its camp this much later.</summary>
    public double RespawnSeconds { get; init; } = 10;

    /// <summary>Arena:Tickets2v2 (6) and Arena:Tickets5v5 (15): the deaths a team can afford in the team modes (1v1: the official 2, best of three).</summary>
    public int Tickets2v2 { get; init; } = 6;

    public int Tickets5v5 { get; init; } = 15;

    /// <summary>Arena:MatchMinutes (15; 1v1 the official 10): the time limit, after which the team with more tickets wins (equal: a draw).</summary>
    public double MatchMinutes { get; init; } = 15;

    /// <summary>Arena:EndSeconds (15): the players stay in the arena this long after the result, then go back where they registered.</summary>
    public double EndSeconds { get; init; } = 15;

    /// <summary>Arena:Rated (true): the arena results move the arena ratings; Arena:RateWithBots (false): also matches with bots in them.</summary>
    public bool Rated { get; init; } = true;

    public bool RateWithBots { get; init; }

    /// <summary>Arena:StartRating (1200, arena.bin startingelo) and Arena:K (20, elok): the Elo of a new ladder entry and the step of a result.</summary>
    public int StartRating { get; init; } = 1200;

    public double K { get; init; } = 20;

    /// <summary>Arena:DuelsRated (true): a duel won by knockdown, the area or a disconnect moves both duelists' duel rating.</summary>
    public bool DuelsRated { get; init; } = true;

    /// <summary>
    /// Arena:WinSilver (5000), Arena:LossSilver (1500), Arena:WinFame (30000), Arena:LossFame (10000): a match's silver and fame (× Rates), in
    /// place of the official season points (no seasons here); a draw pays the loss figures.
    /// </summary>
    public double WinSilver { get; init; } = 5000;

    public double LossSilver { get; init; } = 1500;

    public double WinFame { get; init; } = 30000;

    public double LossFame { get; init; } = 10000;

    /// <summary>
    /// Arena:RewardItems (true): arena.bin's unranked items: a win T2_LOOTBAG_ARENA_WIN_BASIC_0 (premium T3_LOOTBAG_ARENA_WIN_PREMIUM_0), a loss
    /// 5 (premium 7) T2_SKILLBOOK_NONTRADABLE, and the first Arena:DailyWins (3, rewardsperday) wins of a day T4_LOOTBAG_EXPEDITION_ROYAL_SIGIL.
    /// </summary>
    public bool RewardItems { get; init; } = true;

    public int DailyWins { get; init; } = 3;

    /// <summary>Arena:RewardsWithBots (true): a match with bots pays too (false: only matches between players).</summary>
    public bool RewardsWithBots { get; init; } = true;

    /// <summary>Arena:FallbackCluster (0000): where a player of an arena that is gone (a restart) goes without a saved way back.</summary>
    public string FallbackCluster { get; init; } = "0000";

    /// <summary>The Arena section out of <paramref name="values"/> over <paramref name="d"/>.</summary>
    internal static ArenaOptions Load(Dictionary<string, string> values, ArenaOptions d)
    {
        var inv = CultureInfo.InvariantCulture;
        string? Take(string key) => values.Remove(key, out var v) ? v : null;
        double Number(string key, double fallback, double min, double max) => Take(key) is not { } v ? fallback
            : double.TryParse(v, NumberStyles.Float, inv, out var n) && double.IsFinite(n) && n >= min && n <= max ? n
            : throw new InvalidDataException($"{key}: '{v}' is not {min.ToString(inv)}..{max.ToString(inv)}");
        int Int(string key, int fallback, int min, int max) => (int)Number(key, fallback, min, max);
        bool Flag(string key, bool fallback) => Take(key) is not { } v ? fallback
            : bool.TryParse(v, out var f) ? f : throw new InvalidDataException($"{key}: '{v}' is not true or false");
        return new ArenaOptions
        {
            Enabled = Flag("Arena:Enabled", d.Enabled),
            Bots = Flag("Arena:Bots", d.Bots),
            BotWaitSeconds = Number("Arena:BotWaitSeconds", d.BotWaitSeconds, 0, 3600),
            BotFill = Take("Arena:BotFill") is not { } fill ? d.BotFill
                : Enum.TryParse<ArenaBotFill>(fill, ignoreCase: true, out var bf) && Enum.IsDefined(bf) ? bf
                : throw new InvalidDataException($"Arena:BotFill: '{fill}' is not one of {string.Join(", ", Enum.GetNames<ArenaBotFill>())}"),
            BotTier = Int("Arena:BotTier", d.BotTier, 0, 8),
            BotDamageFactor = Number("Arena:BotDamageFactor", d.BotDamageFactor, 0.01, 100),
            BotHealthFactor = Number("Arena:BotHealthFactor", d.BotHealthFactor, 0.01, 100),
            ReadyCheck = Flag("Arena:ReadyCheck", d.ReadyCheck),
            AcceptSeconds = Number("Arena:AcceptSeconds", d.AcceptSeconds, 1, 3600),
            ArrivalSeconds = Number("Arena:ArrivalSeconds", d.ArrivalSeconds, 1, 3600),
            CountdownSeconds = Number("Arena:CountdownSeconds", d.CountdownSeconds, 0, 600),
            RespawnSeconds = Number("Arena:RespawnSeconds", d.RespawnSeconds, 0, 600),
            Tickets2v2 = Int("Arena:Tickets2v2", d.Tickets2v2, 1, 1000),
            Tickets5v5 = Int("Arena:Tickets5v5", d.Tickets5v5, 1, 1000),
            MatchMinutes = Number("Arena:MatchMinutes", d.MatchMinutes, 1, 1440),
            EndSeconds = Number("Arena:EndSeconds", d.EndSeconds, 0, 3600),
            Rated = Flag("Arena:Rated", d.Rated),
            RateWithBots = Flag("Arena:RateWithBots", d.RateWithBots),
            StartRating = Int("Arena:StartRating", d.StartRating, 0, 100_000),
            K = Number("Arena:K", d.K, 0, 1000),
            DuelsRated = Flag("Arena:DuelsRated", d.DuelsRated),
            WinSilver = Number("Arena:WinSilver", d.WinSilver, 0, 1e9),
            LossSilver = Number("Arena:LossSilver", d.LossSilver, 0, 1e9),
            WinFame = Number("Arena:WinFame", d.WinFame, 0, 1e9),
            LossFame = Number("Arena:LossFame", d.LossFame, 0, 1e9),
            RewardItems = Flag("Arena:RewardItems", d.RewardItems),
            DailyWins = Int("Arena:DailyWins", d.DailyWins, 0, 1000),
            RewardsWithBots = Flag("Arena:RewardsWithBots", d.RewardsWithBots),
            FallbackCluster = Take("Arena:FallbackCluster")?.Trim() is { Length: > 0 } fb ? fb : d.FallbackCluster,
        };
    }
}

/// <summary>Arena:BotFill: how many bots a match short of players gets (<see cref="ArenaOptions.BotFill"/>).</summary>
public enum ArenaBotFill
{
    Balanced,
    Full,
}
