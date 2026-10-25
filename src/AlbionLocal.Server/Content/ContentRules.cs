namespace AlbionLocal.Server.Content;

/// <summary>
/// The config keys over the server's own content of content/*.json (official audit 2026-10-26): under Preset official the official game's
/// rules, whatever the files say; a key that allows a feature (the pve preset's values) lets its file decide. Applied once at the start
/// (<see cref="ServerHost"/>), so every system reads the settings as they apply.
/// </summary>
public static class ContentRules
{
    /// <summary>
    /// Events:PlayerInvasions false: no player invasions (.invasion, the tickets), no ticket sell orders, no ticket drops. The rest of the
    /// file (the kinds, the GM's .event start, Scheduled) stays.
    /// </summary>
    public static EventSettings Apply(ServerConfig c, EventSettings s) => c.EventsPlayerInvasions ? s : s with
    {
        Player = s.Player with { Enabled = false },
        Merchant = s.Merchant with { Markets = [], Prices = new Dictionary<string, long>(StringComparer.OrdinalIgnoreCase) },
        Drops = s.Drops with { Boss = 0, Miniboss = 0, Won = 0 },
    };

    /// <summary>
    /// Bounties:Enabled, PvpGoals:Enabled, Season:Rules, Guilds:BossSeasonPoints and Guilds:SeasonRewards over content/goals.json: off / Official
    /// = no bounties, the built-in official PvP rules, the client data's Adventurer's Challenge, no boss season points, no bracket silver or items.
    /// </summary>
    public static GoalSettings Apply(ServerConfig c, GoalSettings s)
    {
        var guild = s.Guild;
        if (!c.GuildBossSeasonPoints) guild = guild with { BossSeasonPoints = new Dictionary<string, long>(StringComparer.Ordinal) };
        if (c.GuildSeasonRewards == RuleSource.Official)
            guild = guild with
            {
                BracketSilver = new Dictionary<string, long>(StringComparer.Ordinal),
                BracketItems = new Dictionary<string, IReadOnlyDictionary<string, int>>(StringComparer.Ordinal),
            };
        return s with
        {
            Bounties = c.BountiesEnabled ? s.Bounties : s.Bounties with { Enabled = false },
            Pvp = c.PvpGoalsEnabled ? s.Pvp : new PvpGoalSettings { Enabled = s.Pvp.Enabled },
            Season = c.SeasonRules == RuleSource.Content ? s.Season : new SeasonGoals { Enabled = s.Season.Enabled },
            Guild = guild,
        };
    }

    /// <summary>Dungeons:DailyBonus (a number replaces the file's; null keeps it) and Dungeons:GroupScaling (false: never) over content/dungeons.json.</summary>
    public static DungeonSettings Apply(ServerConfig c, DungeonSettings s) => s with
    {
        DailyBonus = c.DungeonsDailyBonus ?? s.DailyBonus,
        GroupScaling = c.DungeonsGroupScaling && s.GroupScaling,
    };

    /// <summary>WorldBosses:ExtraSpawns (false: none of the file's own bosses) and WorldBosses:Loot (null keeps the file's) over content/bosses.json.</summary>
    public static BossSettings Apply(ServerConfig c, BossSettings s) => s with
    {
        ExtraSpawns = c.WorldBossesExtraSpawns ? s.ExtraSpawns : [],
        Loot = c.WorldBossesLoot ?? s.Loot,
    };

    /// <summary>
    /// Expeditions:Rules Official over content/expeditions.json: the group categories need a group, party members get the invitation window,
    /// no hardcore carry, no group scaling, no extra items on every run, the leave penalty.
    /// </summary>
    public static ExpeditionSettings Apply(ServerConfig c, ExpeditionSettings s) => c.ExpeditionsRules == RuleSource.Content ? s : s with
    {
        SoloGroupCategories = false, PartyAutoAccept = false, HardcorePartyCarry = false, GroupScaling = false, EveryRunItems = [], LeavePenalty = true,
    };
}
