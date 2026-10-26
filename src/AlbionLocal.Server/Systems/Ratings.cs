using System.Globalization;
using AlbionLocal.Photon;
using AlbionLocal.Server.Entities;
using AlbionLocal.Server.Persistence;

namespace AlbionLocal.Server.Systems;

/// <summary>
/// The duel and arena ladders (analysis/work/stageN/arena/SPEC.md §5): an Elo rating per character and ladder ("duel", "1v1", "2v2", "5v5",
/// "crystal" the Crystal Arena of stageP P6),
/// starting at Arena:StartRating (1200, arena.bin startingelo) and moved by Arena:K (20, elok) × (result − expected) per result, a team's
/// expectation from the average ratings of its players. Kept in memory, read once from the repository (<see cref="IRatingStore"/>, schema v32;
/// in memory only without one) and written at each result. Under <see cref="World.Sync"/>.
/// </summary>
public sealed class Ratings(World world)
{
    public const string Duel = "duel";

    /// <summary>The ladders in their display order.</summary>
    public static readonly IReadOnlyList<string> Ladders = [Duel, "1v1", "2v2", "5v5", Arenas.Crystal];

    private Dictionary<(Guid, string), RatingRow>? _rows;

    private IRatingStore? Store => world.Characters as IRatingStore;

    private ArenaOptions Options => world.Config.Arena;

    private Dictionary<(Guid, string), RatingRow> Rows
    {
        get
        {
            if (_rows is not null) return _rows;
            _rows = [];
            try
            {
                foreach (var r in Store?.LoadRatings() ?? []) _rows[(r.Character, r.Ladder)] = r;
            }
            catch (Exception e) { Log.Warn("arena", $"the ratings could not be read: {e.Message}"); }
            return _rows;
        }
    }

    /// <summary>The ladder name of <paramref name="text"/> (case-insensitive; "duels" too), else null.</summary>
    public static string? LadderOf(string text) =>
        Ladders.FirstOrDefault(l => l.Equals(text.Trim(), StringComparison.OrdinalIgnoreCase)) ?? (text.Trim().Equals("duels", StringComparison.OrdinalIgnoreCase) ? Duel : null);

    /// <summary>The saved row of a character on a ladder; null before its first result.</summary>
    public RatingRow? Find(Guid character, string ladder) => Rows.GetValueOrDefault((character, ladder));

    /// <summary>A character's rating on a ladder (Arena:StartRating before its first result).</summary>
    public int RatingOf(Guid character, string ladder) => Find(character, ladder)?.Rating ?? Options.StartRating;

    /// <summary>Elo's expected score of a side rated <paramref name="a"/> against one rated <paramref name="b"/>.</summary>
    public static double Expected(double a, double b) => 1 / (1 + Math.Pow(10, (b - a) / 400));

    /// <summary>
    /// A result on <paramref name="ladder"/> between team <paramref name="a"/> and team <paramref name="b"/> (players by character and name):
    /// <paramref name="scoreA"/> 1 a win of A, 0 a loss, 0.5 a draw. Each player's rating moves by round(K × (score − expected)), the
    /// expectation from the teams' average ratings; the rows are saved at once. Returns each player's change.
    /// </summary>
    public Dictionary<Guid, int> Record(string ladder, IReadOnlyList<(Guid Id, string Name)> a, IReadOnlyList<(Guid Id, string Name)> b, double scoreA)
    {
        var changes = new Dictionary<Guid, int>();
        if (a.Count == 0 || b.Count == 0) return changes;
        var avgA = a.Average(p => RatingOf(p.Id, ladder));
        var avgB = b.Average(p => RatingOf(p.Id, ladder));
        var expectA = Expected(avgA, avgB);
        var deltaA = (int)Math.Round(Options.K * (scoreA - expectA), MidpointRounding.AwayFromZero);
        var deltaB = (int)Math.Round(Options.K * ((1 - scoreA) - (1 - expectA)), MidpointRounding.AwayFromZero);
        var now = DateTime.UtcNow.Ticks;
        var changed = new List<RatingRow>();
        void Apply((Guid Id, string Name) p, int delta, double score)
        {
            var row = Find(p.Id, ladder) ?? new RatingRow(p.Id, ladder, p.Name, Options.StartRating, 0, 0, 0, 0);
            row = row with
            {
                Name = p.Name, Rating = Math.Max(0, row.Rating + delta), Updated = now,
                Wins = row.Wins + (score == 1 ? 1 : 0), Losses = row.Losses + (score == 0 ? 1 : 0), Draws = row.Draws + (score is > 0 and < 1 ? 1 : 0),
            };
            Rows[(p.Id, ladder)] = row;
            changed.Add(row);
            changes[p.Id] = delta;
        }
        foreach (var p in a) Apply(p, deltaA, scoreA);
        foreach (var p in b) Apply(p, deltaB, 1 - scoreA);
        try { Store?.SaveRatings(changed); }
        catch (Exception e) { Log.Warn("arena", $"the ratings could not be saved: {e.Message}"); }
        Log.Info("arena", string.Create(CultureInfo.InvariantCulture,
            $"{ladder}: {string.Join(", ", a.Select(p => p.Name))} ({avgA:0}) {(scoreA == 1 ? "beat" : scoreA == 0 ? "lost to" : "drew with")} {string.Join(", ", b.Select(p => p.Name))} ({avgB:0}): {deltaA:+0;-0;0} / {deltaB:+0;-0;0}"));
        return changes;
    }

    /// <summary>A duel won by <paramref name="winner"/> (Duels.Finish, Arena:DuelsRated): the duel ladder; the chat line to both.</summary>
    public void DuelWon(Player winner, Player loser)
    {
        if (!Options.DuelsRated || winner.IsBot || loser.IsBot) return;
        var changes = Record(Duel, [(winner.Character.Id, winner.Character.Name)], [(loser.Character.Id, loser.Character.Name)], 1);
        foreach (var p in new[] { winner, loser })
            if (p.Map is not null)
                Expeditions.Tell(p, string.Create(CultureInfo.InvariantCulture,
                    $"Duel rating: {RatingOf(p.Character.Id, Duel)} ({changes.GetValueOrDefault(p.Character.Id):+0;-0;0})."));
    }

    /// <summary>The best <paramref name="n"/> of <paramref name="ladder"/> (rating, then wins, then name).</summary>
    public IReadOnlyList<RatingRow> Top(string ladder, int n) =>
        [.. Rows.Values.Where(r => r.Ladder == ladder).OrderByDescending(r => r.Rating).ThenByDescending(r => r.Wins).ThenBy(r => r.Name, StringComparer.OrdinalIgnoreCase).Take(n)];

    /// <summary>A character's place on a ladder (1 = the best); 0 before its first result.</summary>
    public int RankOf(Guid character, string ladder)
    {
        if (Find(character, ladder) is not { } mine) return 0;
        return 1 + Rows.Values.Count(r => r.Ladder == ladder && (r.Rating > mine.Rating || r.Rating == mine.Rating && r.Wins > mine.Wins));
    }

    // ---- the arena ranks (stageP league-pvp SPEC §3.3, phase P7; Arena:RankPoints) ----

    private Dictionary<(Guid, int), RankRow>? _ranks;

    private Dictionary<(Guid, int), RankRow> Ranks
    {
        get
        {
            if (_ranks is not null) return _ranks;
            _ranks = [];
            try
            {
                foreach (var r in (world.Characters as IRankStore)?.LoadRanks() ?? []) _ranks[(r.Character, r.Category)] = r;
            }
            catch (Exception e) { Log.Warn("arena", $"the arena ranks could not be read: {e.Message}"); }
            return _ranks;
        }
    }

    /// <summary>A character's saved rank in ranked category <paramref name="category"/> (1 the Crystal Arena, 2 the 1v1); null before its first ranked result.</summary>
    public RankRow? ArenaRank(Guid character, int category) => Ranks.GetValueOrDefault((character, category));

    /// <summary>
    /// [H] The rank point change of a ranked result (see <see cref="ArenaOptions.RankPoints"/>): <paramref name="eloDelta"/> the result's Elo
    /// change, <paramref name="elo"/> the Elo after it, <paramref name="score"/> 1 a win, 0 a loss, else a draw (0 RP).
    /// </summary>
    public static int RankPointChange(Data.ArenaData.Rank rank, double factor, int eloDelta, int elo, double score, ArenaOptions o)
    {
        double rp;
        if (score == 1)
            rp = rank.WinModifier * Math.Max(o.RankPointMinGain, eloDelta) + (o.RankPointBoosters && elo > rank.ExpectedElo ? rank.EloWinBooster : 0);
        else if (score == 0)
            rp = -(rank.LoseModifier * Math.Max(o.RankPointMinGain, -eloDelta) + (o.RankPointBoosters && elo < rank.ExpectedElo ? rank.EloLoseBooster : 0));
        else return 0;
        return (int)Math.Round(rp * factor, MidpointRounding.AwayFromZero);
    }

    /// <summary>
    /// A ranked result of <paramref name="character"/> in <paramref name="category"/>: its rank points move by <paramref name="change"/>
    /// (never below 0), then the rank climbs while the points reach its promotion threshold and falls while they are under its demotion
    /// threshold; saved at once. Returns the row before and after.
    /// </summary>
    public (RankRow Before, RankRow After) AddRankPoints(Guid character, int category, Data.ArenaData data, int change)
    {
        var before = ArenaRank(character, category) ?? new RankRow(character, category, Data.ArenaData.SeasonIndex, 0, 0, 0, 0);
        var points = Math.Max(0, before.Points + change);
        var rank = Math.Clamp(before.Rank, 0, data.Ranks.Count - 1);
        while (rank < data.Ranks.Count - 1 && points >= data.Ranks[rank].Promotion) rank++;
        while (rank > 0 && points < data.Ranks[rank].Demotion) rank--;
        var after = before with { Season = Data.ArenaData.SeasonIndex, Rank = rank, Points = points, Updated = DateTime.UtcNow.Ticks };
        Save(after);
        return (before, after);
    }

    /// <summary>The rank-up chests up to <paramref name="peak"/> were given: the row keeps it (no chest twice for a rank in a season).</summary>
    public void MarkPeak(Guid character, int category, int peak)
    {
        if (ArenaRank(character, category) is { } row && row.Peak < peak) Save(row with { Peak = peak });
    }

    private void Save(RankRow row)
    {
        Ranks[(row.Character, row.Category)] = row;
        try { (world.Characters as IRankStore)?.SaveRanks([row]); }
        catch (Exception e) { Log.Warn("arena", $"the arena ranks could not be saved: {e.Message}"); }
    }

    /// <summary>The character called <paramref name="name"/> on any ladder (case-insensitive), for .rating &lt;name&gt; of one offline.</summary>
    public Guid? FindByName(string name) =>
        Rows.Values.Where(r => r.Name.Equals(name.Trim(), StringComparison.OrdinalIgnoreCase)).OrderByDescending(r => r.Updated).Select(r => (Guid?)r.Character).FirstOrDefault();
}
