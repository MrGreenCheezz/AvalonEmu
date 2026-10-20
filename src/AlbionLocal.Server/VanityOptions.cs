using System.Globalization;

namespace AlbionLocal.Server;

/// <summary>
/// The cosmetics (Vanity:*, analysis/work/stageN/vanity/SPEC.md §3): wardrobe and mount skins are never bought here; they drop, very rarely,
/// from content. Each chance is per player and event, × <see cref="ChanceFactor"/>; 0 turns a source off. The same under both presets.
/// </summary>
public sealed record VanityOptions
{
    /// <summary>Vanity:Enabled (true): the drops; the store, the wardrobe and the mount skins work either way.</summary>
    public bool Enabled { get; init; } = true;

    /// <summary>Vanity:ChanceFactor (1): × every chance below.</summary>
    public double ChanceFactor { get; init; } = 1;

    /// <summary>Vanity:WorldBossChance (0.02): each looter of a world boss.</summary>
    public double WorldBossChance { get; init; } = 0.02;

    /// <summary>Vanity:BossChance (0.003): the killer of any other boss (a dungeon's, an expedition's, a hellgate's guardian …).</summary>
    public double BossChance { get; init; } = 0.003;

    /// <summary>Vanity:MinibossChance (0.001) and Vanity:EliteChance (0.0003, a champion): the killer.</summary>
    public double MinibossChance { get; init; } = 0.001;

    public double EliteChance { get; init; } = 0.0003;

    /// <summary>
    /// Vanity:BossChestChance (0.004): whoever opens a boss chest (a lootchests.xml name with BOSS: random, static, corrupted, hellgate,
    /// avalonian dungeons) first, × (1 + its rarity: standard 0 … legendary 3); Vanity:ChestChance (0): any other chest.
    /// </summary>
    public double BossChestChance { get; init; } = 0.004;

    public double ChestChance { get; init; }

    /// <summary>Vanity:HuntChance (0.02): every member online when a hunt's final target dies.</summary>
    public double HuntChance { get; init; } = 0.02;

    /// <summary>Vanity:ArenaWinChance (0.005): each winner of an arena match; Vanity:TournamentChance (0.25): the duel tournament's winner.</summary>
    public double ArenaWinChance { get; init; } = 0.005;

    public double TournamentChance { get; init; } = 0.25;

    /// <summary>Vanity:SeasonRewardChance (0.01): per Adventurer's Challenge reward unlocked; Vanity:GuildGoalChance (0.01): each member online at a weekly guild goal.</summary>
    public double SeasonRewardChance { get; init; } = 0.01;

    public double GuildGoalChance { get; init; } = 0.01;

    /// <summary>Vanity:FameMilestones ("1000000,5000000,10000000,25000000,50000000,100000000"): total fame marks; Vanity:FameMilestoneChance (0.25) at each one passed.</summary>
    public IReadOnlyList<long> FameMilestones { get; init; } = DefaultMilestones;

    /// <summary>The default marks, one shared list (two default configs compare equal).</summary>
    public static readonly IReadOnlyList<long> DefaultMilestones = [1_000_000, 5_000_000, 10_000_000, 25_000_000, 50_000_000, 100_000_000];

    public double FameMilestoneChance { get; init; } = 0.25;

    /// <summary>Vanity:ThemeChance (0.75): a source with a theme (undead, keeper, morgana, heretic, demon, mists, corrupted) draws from its themed skins this often.</summary>
    public double ThemeChance { get; init; } = 0.75;

    /// <summary>Vanity:DuplicateSilver (25000): what a drop pays instead when the player owns every skin of the pool already.</summary>
    public double DuplicateSilver { get; init; } = 25000;

    /// <summary>The Vanity section out of <paramref name="values"/> over <paramref name="d"/>.</summary>
    internal static VanityOptions Load(Dictionary<string, string> values, VanityOptions d)
    {
        var inv = CultureInfo.InvariantCulture;
        string? Take(string key) => values.Remove(key, out var v) ? v : null;
        double Number(string key, double fallback, double min, double max) => Take(key) is not { } v ? fallback
            : double.TryParse(v, NumberStyles.Float, inv, out var n) && double.IsFinite(n) && n >= min && n <= max ? n
            : throw new InvalidDataException($"{key}: '{v}' is not {min.ToString(inv)}..{max.ToString(inv)}");
        double Chance(string key, double fallback) => Number(key, fallback, 0, 1);
        bool Flag(string key, bool fallback) => Take(key) is not { } v ? fallback
            : bool.TryParse(v, out var f) ? f : throw new InvalidDataException($"{key}: '{v}' is not true or false");
        IReadOnlyList<long> Marks(string key, IReadOnlyList<long> fallback)
        {
            if (Take(key) is not { } v) return fallback;
            var list = new List<long>();
            foreach (var part in v.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
                list.Add(long.TryParse(part, NumberStyles.Integer, inv, out var n) && n > 0 && n <= 100_000_000_000
                    ? n : throw new InvalidDataException($"{key}: '{part}' is not a fame amount"));
            list.Sort();
            return list;
        }
        return new VanityOptions
        {
            Enabled = Flag("Vanity:Enabled", d.Enabled),
            ChanceFactor = Number("Vanity:ChanceFactor", d.ChanceFactor, 0, 1000),
            WorldBossChance = Chance("Vanity:WorldBossChance", d.WorldBossChance),
            BossChance = Chance("Vanity:BossChance", d.BossChance),
            MinibossChance = Chance("Vanity:MinibossChance", d.MinibossChance),
            EliteChance = Chance("Vanity:EliteChance", d.EliteChance),
            BossChestChance = Chance("Vanity:BossChestChance", d.BossChestChance),
            ChestChance = Chance("Vanity:ChestChance", d.ChestChance),
            HuntChance = Chance("Vanity:HuntChance", d.HuntChance),
            ArenaWinChance = Chance("Vanity:ArenaWinChance", d.ArenaWinChance),
            TournamentChance = Chance("Vanity:TournamentChance", d.TournamentChance),
            SeasonRewardChance = Chance("Vanity:SeasonRewardChance", d.SeasonRewardChance),
            GuildGoalChance = Chance("Vanity:GuildGoalChance", d.GuildGoalChance),
            FameMilestones = Marks("Vanity:FameMilestones", d.FameMilestones),
            FameMilestoneChance = Chance("Vanity:FameMilestoneChance", d.FameMilestoneChance),
            ThemeChance = Chance("Vanity:ThemeChance", d.ThemeChance),
            DuplicateSilver = Number("Vanity:DuplicateSilver", d.DuplicateSilver, 0, 1e9),
        };
    }
}
