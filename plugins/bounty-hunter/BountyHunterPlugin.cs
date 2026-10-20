using System.Globalization;
using AlbionLocal.Plugin.Api;

namespace BountyHunter;

/// <summary>
/// Example plugin: every IntervalMinutes, when no bounty is open, it picks a living elite mob type (config Categories, from MinTier) in a
/// cluster with players and announces a bounty on it. Whoever kills a mob of that type in that cluster before it expires gets silver and
/// fame and a server-wide announcement; the kills are kept on a leaderboard in the plugin's storage, as is the open bounty (so a reload
/// or a restart keeps it). .bounties shows the bounty and the leaderboard; a GM's .bounties new opens one now, .bounties clear drops it.
/// </summary>
public sealed class BountyHunterPlugin : PluginBase
{
    public override string Name => "bounty-hunter";
    public override string Version => "1.0.0";

    private sealed class Settings
    {
        public double IntervalMinutes { get; set; } = 10;
        public double DurationMinutes { get; set; } = 20;
        public List<string> Categories { get; set; } = ["champion", "miniboss"];
        public int MinTier { get; set; } = 3;
        public long RewardSilver { get; set; } = 10000;
        public long RewardSilverPerTier { get; set; } = 2500;
        public long RewardFame { get; set; } = 5000;
        public long RewardFamePerTier { get; set; } = 1000;
        public int LeaderboardSize { get; set; } = 5;
    }

    /// <summary>The open bounty (kept in storage under "active").</summary>
    public sealed class Bounty
    {
        public string Mob { get; set; } = "";
        public string Cluster { get; set; } = "";
        public int Tier { get; set; }
        public DateTime Expires { get; set; }
    }

    /// <summary>A leaderboard row (storage "leaderboard", by character Guid).</summary>
    public sealed class Hunter
    {
        public string Name { get; set; } = "";
        public int Kills { get; set; }
        public long Silver { get; set; }
    }

    private Settings _s = new();
    private readonly Random _random = new();

    protected override void OnLoad()
    {
        _s = Config.Bind<Settings>();
        Events.MobDied += OnMobDied;
        Timers.Every(TimeSpan.FromMinutes(Math.Max(0.1, _s.IntervalMinutes)), () => Open(force: false));
        Timers.Every(TimeSpan.FromSeconds(30), Expire);
        Commands.Register("bounties", PlayerLevel.Player, "the open elite bounty and the hunters' leaderboard; a GM: .bounties new | clear", Show);
        Log.Info($"a bounty every {_s.IntervalMinutes} min on {string.Join("/", _s.Categories)} mobs from T{_s.MinTier}"
            + (Active is { } b ? $"; open: {b.Mob} in {b.Cluster}" : ""));
    }

    private Bounty? Active => Storage.Get<Bounty?>("active", null);

    private long Silver(int tier) => _s.RewardSilver + _s.RewardSilverPerTier * tier;
    private long Fame(int tier) => _s.RewardFame + _s.RewardFamePerTier * tier;

    /// <summary>"T5_MOB_KEEPER_BEAR_CHAMPION" → "Keeper Bear Champion".</summary>
    private static string Pretty(string mob)
    {
        var name = mob;
        var i = name.IndexOf("_MOB_", StringComparison.Ordinal);
        if (i >= 0) name = name[(i + 5)..];
        return CultureInfo.InvariantCulture.TextInfo.ToTitleCase(name.Replace('_', ' ').ToLowerInvariant());
    }

    /// <summary>Opens a bounty when none is open (or with <paramref name="force"/>): a random elite type of a populated cluster. Why not, or null.</summary>
    private string? Open(bool force)
    {
        if (Active is not null && !force) return "a bounty is open already";
        var candidates = Server.PopulatedClusters
            .SelectMany(c => Server.Mobs(c))
            .Where(m => m.IsAlive && !m.IsEventMob && !m.IsWorldBoss && m.Tier >= _s.MinTier
                        && _s.Categories.Contains(m.Category, StringComparer.OrdinalIgnoreCase))
            .GroupBy(m => (m.Name, m.Cluster))
            .Select(g => g.First())
            .ToList();
        if (candidates.Count == 0) return "no elite mob lives in a cluster with players";
        var pick = candidates[_random.Next(candidates.Count)];
        var bounty = new Bounty { Mob = pick.Name, Cluster = pick.Cluster, Tier = pick.Tier, Expires = Server.UtcNow.AddMinutes(_s.DurationMinutes) };
        Storage.Set("active", bounty);
        Server.Broadcast($"Bounty! Slay a {Pretty(pick.Name)} (T{pick.Tier}) in cluster {pick.Cluster} within {_s.DurationMinutes:0} min: "
            + $"{Silver(pick.Tier)} silver and {Fame(pick.Tier)} fame. (.bounties)");
        Log.Info($"bounty on {pick.Name} in {pick.Cluster} ({candidates.Count} candidate(s))");
        return null;
    }

    private void Expire()
    {
        if (Active is not { } b || Server.UtcNow < b.Expires) return;
        Storage.Remove("active");
        Server.Broadcast($"The bounty on the {Pretty(b.Mob)} in {b.Cluster} has expired.");
    }

    private void OnMobDied(MobDeathArgs e)
    {
        if (Active is not { } b || !e.Mob.Name.Equals(b.Mob, StringComparison.OrdinalIgnoreCase) || e.Mob.Cluster != b.Cluster) return;
        Storage.Remove("active");
        var killer = e.Killer;
        var silver = Silver(b.Tier);
        killer.GiveSilver(silver);
        killer.GiveFame(Fame(b.Tier), b.Tier);
        var board = Storage.Get("leaderboard", new Dictionary<string, Hunter>());
        var key = killer.CharacterId.ToString();
        var row = board.GetValueOrDefault(key) ?? new Hunter();
        row.Name = killer.Name;
        row.Kills++;
        row.Silver += silver;
        board[key] = row;
        Storage.Set("leaderboard", board);
        Server.Broadcast($"{killer.Name} claimed the bounty on the {Pretty(b.Mob)}! ({row.Kills} bount{(row.Kills == 1 ? "y" : "ies")} so far)");
    }

    private void Show(ICommandCall call)
    {
        var verb = call.Words.Count > 0 ? call.Words[0].ToLowerInvariant() : "";
        if (verb is "new" or "clear")
        {
            if (call.Level < PlayerLevel.GameMaster)
            {
                call.Error($".bounties {verb} is for GMs");
                return;
            }
            if (verb == "clear")
            {
                call.Reply(Storage.Remove("active") ? "bounty dropped" : "no bounty open");
                return;
            }
            if (Open(force: true) is { } why) call.Error(why);
            else call.Reply("bounty opened");
            return;
        }
        var lines = new List<string>();
        if (Active is { } b)
        {
            var left = b.Expires - Server.UtcNow;
            lines.Add($"Open bounty: a {Pretty(b.Mob)} (T{b.Tier}) in cluster {b.Cluster}, {Math.Max(0, left.TotalMinutes):0} min left: {Silver(b.Tier)} silver, {Fame(b.Tier)} fame");
        }
        else lines.Add($"No bounty open; the next comes within {_s.IntervalMinutes:0} min.");
        var top = Storage.Get("leaderboard", new Dictionary<string, Hunter>()).Values
            .OrderByDescending(h => h.Kills).ThenByDescending(h => h.Silver).Take(Math.Max(1, _s.LeaderboardSize)).ToList();
        lines.Add(top.Count == 0 ? "Leaderboard: nobody yet." : "Leaderboard:");
        lines.AddRange(top.Select((h, i) => $"{i + 1}. {h.Name}: {h.Kills} bount{(h.Kills == 1 ? "y" : "ies")}, {h.Silver} silver"));
        call.Reply(string.Join("\n", lines));
    }
}
