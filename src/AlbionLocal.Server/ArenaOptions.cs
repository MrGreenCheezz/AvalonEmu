using System.Globalization;

namespace AlbionLocal.Server;

/// <summary>
/// The arena queues, matches and ratings (Arena:*, analysis/work/stageN/arena/SPEC.md §4). Every key optional (registering is the consent to
/// the fight). The modes' maps and team sizes are the client's (arena.bin, matchtypes.bin). The record's values are the official preset's
/// (matchtypes MATCH_ARENA: 150 tickets, 2 per death, a 25 min limit, a 15 s countdown; no silver or fame, no bots, no duel rating);
/// <see cref="PveDefaults"/> are the pve preset's scaled-down figures for a few players, with bots, silver and fame.
/// </summary>
public sealed record ArenaOptions
{
    /// <summary>The pve preset's arena (the figures of before the official audit, 2026-10-26).</summary>
    public static ArenaOptions PveDefaults { get; } = new()
    {
        Bots = true, CountdownSeconds = 10, Tickets2v2 = 6, Tickets5v5 = 15, TicketsPerDeath = 1, MatchMinutes = 15, DuelsRated = true,
        WinSilver = 5000, LossSilver = 1500, WinFame = 30000, LossFame = 10000,
        ItemPowerCap = false, DoubleHealerPenalty = false, EnterBuffs = false, WarcampDefense = false, Psp = false, Orbs = false, CrystalArena = false, RankPoints = false,
    };

    /// <summary>Arena:Enabled (true): ArenaRegister and .arena join take players into the queues.</summary>
    public bool Enabled { get; init; } = true;

    /// <summary>
    /// Arena:Bots (false, official; the pve preset true): with too few players a queue starts its match after <see cref="BotWaitSeconds"/> with
    /// player-bots in the missing slots. It needs Bots:Enabled as well; without bots a match starts only with both teams of players.
    /// </summary>
    public bool Bots { get; init; }

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

    /// <summary>Arena:CountdownSeconds (15, matchtypes countdown; the pve preset 10): the truce before each fight (and each 1v1 round); nobody can hurt the other team then.</summary>
    public double CountdownSeconds { get; init; } = 15;

    /// <summary>Arena:RespawnSeconds (10, matchtypes MATCH_ARENA respawndelay): a team mode's player down comes back at its camp this much later.</summary>
    public double RespawnSeconds { get; init; } = 10;

    /// <summary>
    /// Arena:Tickets2v2 and Arena:Tickets5v5 (150, matchtypes MATCH_ARENA; the pve preset 6 and 15): a team's tickets in the team modes, each
    /// death costing Arena:TicketsPerDeath (2, MATCH_ARENA; the pve preset 1), each runestone claim of the other team 10 more (<see cref="Orbs"/>)
    /// (1v1: the official 2 rounds, best of three, whatever these say).
    /// </summary>
    public int Tickets2v2 { get; init; } = 150;

    public int Tickets5v5 { get; init; } = 150;

    public int TicketsPerDeath { get; init; } = 2;

    /// <summary>Arena:MatchMinutes (25, MATCH_ARENA; the pve preset 15; 1v1 the official 10): the time limit, after which the team with more tickets wins (equal: a draw).</summary>
    public double MatchMinutes { get; init; } = 25;

    /// <summary>Arena:EndSeconds (15): the players stay in the arena this long after the result, then go back where they registered.</summary>
    public double EndSeconds { get; init; } = 15;

    /// <summary>Arena:Rated (true): the arena results move the arena ratings; Arena:RateWithBots (false): also matches with bots in them.</summary>
    public bool Rated { get; init; } = true;

    public bool RateWithBots { get; init; }

    /// <summary>Arena:StartRating (1200, arena.bin startingelo) and Arena:K (20, elok): the Elo of a new ladder entry and the step of a result.</summary>
    public int StartRating { get; init; } = 1200;

    public double K { get; init; } = 20;

    /// <summary>Arena:DuelsRated (false, official: duels have no rating; the pve preset true): a duel won by knockdown, the area or a disconnect moves both duelists' duel rating.</summary>
    public bool DuelsRated { get; init; }

    /// <summary>
    /// Arena:WinSilver, Arena:LossSilver, Arena:WinFame, Arena:LossFame (0, official: an arena pays season points and arena.bin's items, no silver
    /// or fame; the pve preset 5000, 1500, 30000, 10000): a match's silver and fame (× Rates), the pve preset's stand-in for the official
    /// season points (<see cref="Psp"/>); a draw pays the loss figures.
    /// </summary>
    public double WinSilver { get; init; }

    public double LossSilver { get; init; }

    public double WinFame { get; init; }

    public double LossFame { get; init; }

    /// <summary>
    /// Arena:RewardItems (true): arena.bin's unranked items: a win T2_LOOTBAG_ARENA_WIN_BASIC_0 (premium T3_LOOTBAG_ARENA_WIN_PREMIUM_0), a loss
    /// 5 (premium 7) T2_SKILLBOOK_NONTRADABLE, and the first Arena:DailyWins (3, rewardsperday) wins of a day T4_LOOTBAG_EXPEDITION_ROYAL_SIGIL.
    /// </summary>
    public bool RewardItems { get; init; } = true;

    public int DailyWins { get; init; } = 3;

    /// <summary>Arena:RewardsWithBots (true): a match with bots pays too (false: only matches between players).</summary>
    public bool RewardsWithBots { get; init; } = true;

    /// <summary>
    /// Arena:ItemPowerCap (true; the pve preset false): the arenacategory item power caps on every worn item during a match (stageP league-pvp
    /// SPEC §2.5): the unranked team modes a hard cap at 600 (minitempoweraverage 600, "capped at 600 Item Power"), the 1v1 a soft cap at 900
    /// with 20 % of the power above it ("any IP above reduced by 80 %"): IP' = min(IP, cap) + max(0, IP − cap) × factor [infer: per item].
    /// </summary>
    public bool ItemPowerCap { get; init; } = true;

    /// <summary>
    /// Arena:DoubleHealerPenalty (true; the pve preset false): matchtypes doublehealerpenalty (MATCH_ARENA: DOUBLEHEALER_PENALTY, healmodifier
    /// −0.35 for 10 s) on every member of a team with more than one healer (a holy or nature staff in the main hand, itemroles ARENA_HEAL),
    /// refreshed while it lasts [infer: who carries it].
    /// </summary>
    public bool DoubleHealerPenalty { get; init; } = true;

    /// <summary>
    /// Arena:EnterBuffs (true; the pve preset false): the worldsettings ARENA_* spawn buffs on the arrival in a match: ARENA_ENTER (full health
    /// and energy, cooldowns reset, the cape cooldown removed), ARENA_OVERLOAD_PROTECTION (+99 max load), in the 1v1 ARENA_1V1_ENTER (healing −60 %).
    /// </summary>
    public bool EnterBuffs { get; init; } = true;

    /// <summary>
    /// Arena:WarcampDefense (true; the pve preset false): the matchtype's &lt;warcamp&gt;&lt;defense&gt;: each camp's turret hits the other team
    /// within 18 m for 10 % of its maximum health every 0.4 s during the fight (lethal in the arena: a ticket), and its own team within 8.7 m has
    /// ARENA_WARCAMP_INVULNERABILITY (broken by attacking, back after leaving the circle).
    /// </summary>
    public bool WarcampDefense { get; init; } = true;

    /// <summary>
    /// Arena:Psp (true; the pve preset false): arena.bin's personal season points at the result (Seasons.Gain): unranked win 1200 (premium 1500),
    /// loss or draw 400 (600); the 1v1 its ARENA_IRON_1 figures, win 400 (500), loss 175 (250) (with <see cref="RankPoints"/> those of the player's rank, the Crystal Arena's too). Paid with the other
    /// rewards (Arena:RewardsWithBots), only while an Adventurer's Challenge season runs.
    /// </summary>
    public bool Psp { get; init; } = true;

    /// <summary>
    /// Arena:Orbs (true; the pve preset false, whose few tickets one claim would end): the map's orbs ("runestones", stageP league-pvp SPEC §3.2):
    /// ARENA-STANDARD's three ORB_ARENA (a team alone within 9 m for 10 s claims one, 4 s more to take the other team's, a claim costs the other
    /// team 10 tickets, none in the fight's first 60 s); a channel orb (the crystal types: a player's 7.5 s channel within 8 m, broken by damage
    /// or moving; each claim −1 and the 120 s round score) where a mode's matchtype has one (the Crystal Arena, <see cref="CrystalArena"/>).
    /// NewOrbObject, OrbClaimed, OrbReset and ClaimOrbStart / Finished / Cancel go to the client [infer: NewOrbObject key 2 the tile name, key 4
    /// the owner team; capture R2].
    /// </summary>
    public bool Orbs { get; init; } = true;

    /// <summary>
    /// Arena:OrbRoundLookup (false): MATCH_ARENA's ticketreductionlookup (roundduration 60 s; orb difference 1/2/3/4 → 10/10/20/20) on top of
    /// the instant score, which the tutorial does not mention [infer: unused officially].
    /// </summary>
    public bool OrbRoundLookup { get; init; }

    /// <summary>
    /// Arena:MatchHud (false in both presets until captures R2/R3 confirm the fields; official true): the match HUD's partly decoded events:
    /// OrbUpdate (131) on each orb change, MatchUpdate (121) on each ticket change, StartArenaMatchInfos (119) at the first countdown and
    /// MatchNewCombatRound / MatchEndCombatRound (171/172) at the crystal orbs' 120 s cycles and the 1v1 rounds. False sends none of them (a wrong one may throw in the
    /// client, as the empty GetArenaRankings did); the score stays in chat.
    /// </summary>
    public bool MatchHud { get; init; }

    /// <summary>
    /// Arena:CrystalArena (true; the pve preset false, which keeps category 1 on the 5v5): the client's category 1 (ranked, the Crystal Arena,
    /// stageP league-pvp SPEC §3.4) is its own 5v5 queue on CrystalRealm03 with matchtypes MATCH_CRYSTAL_GVG_NONLETHAL: 150 tickets, 6 per death,
    /// 18 s respawn, 24 min; the map's three channel runestones (a 7.5 s channel, −1 ticket per claim; every 120 s the side holding more orbs
    /// takes 20/30/40 from the other and its orbs turn neutral, with <see cref="Orbs"/>), the 900 soft cap (× 0.2 above, with
    /// <see cref="ItemPowerCap"/>), WARCAMP_INVULNERABILITY and the camp turret (non-lethal), and arena.bin's ranked rewards and season points.
    /// False: category 1 queues for the unranked 5v5 as before (.arena join crystal is refused).
    /// </summary>
    public bool CrystalArena { get; init; } = true;

    /// <summary>
    /// Arena:RankPoints (true; the pve preset false): the ranked categories' rank points (RP) and the season template's 17 ranks (stageP
    /// league-pvp SPEC §3.3; arena.bin RANKED_BRACKETS_ARENA_S26: ARENA_IRON_1 … ARENA_CRYSTAL, promotion at 150, 300, … 2400 RP, demotion
    /// below 0, 100, … 2350) for the Crystal Arena (category 1) and the 1v1 (category 2, rankpointfactor 0.5); a new rank's rank-up chest
    /// (seasonrankrewards) once per season; the rank's win and loss rewards and season points; GetArenaRankings the real rank and RP.
    /// [H] The rank point formula is not in the client data (capture R3): an Elo-like placeholder from the rated result's Elo change d (the
    /// ladder's Arena:K): a win gives winrankpointmodifier × max(<see cref="RankPointMinGain"/>, d), plus elowinbooster while the Elo is above
    /// the rank's expectedelo (<see cref="RankPointBoosters"/>); a loss takes loserankpointmodifier × max(min, −d), plus elolosebooster while
    /// the Elo is below expectedelo; × the category's rankpointfactor; a draw nothing; never below 0 RP. Only rated results count
    /// (Arena:Rated, Arena:RateWithBots). False: no RP, the 1v1 pays the unranked items, GetArenaRankings the lowest rank and 0 RP.
    /// </summary>
    public bool RankPoints { get; init; } = true;

    /// <summary>Arena:RankPointMinGain (1) [H]: the least Elo change the rank point placeholder counts for a win or a loss.</summary>
    public double RankPointMinGain { get; init; } = 1;

    /// <summary>Arena:RankPointBoosters (true) [H]: the rank's elowinbooster / elolosebooster on top (see <see cref="RankPoints"/>).</summary>
    public bool RankPointBoosters { get; init; } = true;

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
            TicketsPerDeath = Int("Arena:TicketsPerDeath", d.TicketsPerDeath, 1, 1000),
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
            ItemPowerCap = Flag("Arena:ItemPowerCap", d.ItemPowerCap),
            DoubleHealerPenalty = Flag("Arena:DoubleHealerPenalty", d.DoubleHealerPenalty),
            EnterBuffs = Flag("Arena:EnterBuffs", d.EnterBuffs),
            WarcampDefense = Flag("Arena:WarcampDefense", d.WarcampDefense),
            Psp = Flag("Arena:Psp", d.Psp),
            Orbs = Flag("Arena:Orbs", d.Orbs),
            OrbRoundLookup = Flag("Arena:OrbRoundLookup", d.OrbRoundLookup),
            MatchHud = Flag("Arena:MatchHud", d.MatchHud),
            CrystalArena = Flag("Arena:CrystalArena", d.CrystalArena),
            RankPoints = Flag("Arena:RankPoints", d.RankPoints),
            RankPointMinGain = Number("Arena:RankPointMinGain", d.RankPointMinGain, 0, 1000),
            RankPointBoosters = Flag("Arena:RankPointBoosters", d.RankPointBoosters),
        };
    }
}

/// <summary>Arena:BotFill: how many bots a match short of players gets (<see cref="ArenaOptions.BotFill"/>).</summary>
public enum ArenaBotFill
{
    Balanced,
    Full,
}
