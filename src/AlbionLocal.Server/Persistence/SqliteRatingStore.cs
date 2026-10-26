namespace AlbionLocal.Server.Persistence;

/// <summary>
/// A character's place on one ladder as saved (stage N arena SPEC §5): the ladder ("duel", "1v1", "2v2", "5v5"), the name it had at its last
/// result, its Elo rating and its wins, losses and draws, and when (UTC ticks) its last result came.
/// </summary>
public sealed record RatingRow(Guid Character, string Ladder, string Name, int Rating, int Wins, int Losses, int Draws, long Updated);

/// <summary>The duel and arena ladders (<see cref="Systems.Ratings"/> keeps them in memory and writes each result through here at once: a few rows).</summary>
public interface IRatingStore
{
    /// <summary>Every saved rating.</summary>
    IReadOnlyList<RatingRow> LoadRatings();

    /// <summary>Stores <paramref name="rows"/> (new or changed) in one transaction.</summary>
    void SaveRatings(IReadOnlyList<RatingRow> rows);
}

/// <summary><see cref="IRatingStore"/> in the character database (schema v32: ratings).</summary>
public sealed partial class SqliteCharacterRepository : IRatingStore
{
    public IReadOnlyList<RatingRow> LoadRatings()
    {
        lock (_sync)
        {
            var rows = new List<RatingRow>();
            using var select = Command("SELECT character_id, ladder, name, rating, wins, losses, draws, updated FROM ratings", null);
            using var r = select.ExecuteReader();
            while (r.Read())
                if (Guid.TryParse(r.GetString(0), out var id))
                    rows.Add(new RatingRow(id, r.GetString(1), r.GetString(2), r.GetInt32(3), r.GetInt32(4), r.GetInt32(5), r.GetInt32(6), r.GetInt64(7)));
            return rows;
        }
    }

    public void SaveRatings(IReadOnlyList<RatingRow> rows)
    {
        lock (_sync)
        {
            using var tx = _db.BeginTransaction();
            foreach (var row in rows)
            {
                using var upsert = Command("""
                    INSERT INTO ratings (character_id, ladder, name, rating, wins, losses, draws, updated)
                    VALUES ($id, $ladder, $name, $rating, $wins, $losses, $draws, $updated)
                    ON CONFLICT (character_id, ladder) DO UPDATE SET name = $name, rating = $rating, wins = $wins, losses = $losses, draws = $draws, updated = $updated
                    """, tx, ("$id", row.Character.ToString()), ("$ladder", row.Ladder), ("$name", row.Name), ("$rating", row.Rating), ("$wins", row.Wins),
                    ("$losses", row.Losses), ("$draws", row.Draws), ("$updated", row.Updated));
                upsert.ExecuteNonQuery();
            }
            tx.Commit();
        }
    }
}

/// <summary>
/// A character's arena rank in one ranked category as saved (stageP league-pvp SPEC §3.3, schema v42): the arena season, the rank index in the
/// season's template (0 = ARENA_IRON_1), the rank points, the highest rank whose rank-up chest was given, and when its last result came.
/// </summary>
public sealed record RankRow(Guid Character, int Category, int Season, int Rank, int Points, int Peak, long Updated);

/// <summary>The arena ranks (<see cref="Systems.Ratings"/> keeps them in memory and writes each result through here at once).</summary>
public interface IRankStore
{
    IReadOnlyList<RankRow> LoadRanks();

    void SaveRanks(IReadOnlyList<RankRow> rows);
}

/// <summary><see cref="IRankStore"/> in the character database (schema v42: arena_ranks).</summary>
public sealed partial class SqliteCharacterRepository : IRankStore
{
    public IReadOnlyList<RankRow> LoadRanks()
    {
        lock (_sync)
        {
            var rows = new List<RankRow>();
            using var select = Command("SELECT character_id, category, season, rank, points, peak, updated FROM arena_ranks", null);
            using var r = select.ExecuteReader();
            while (r.Read())
                if (Guid.TryParse(r.GetString(0), out var id))
                    rows.Add(new RankRow(id, r.GetInt32(1), r.GetInt32(2), r.GetInt32(3), r.GetInt32(4), r.GetInt32(5), r.GetInt64(6)));
            return rows;
        }
    }

    public void SaveRanks(IReadOnlyList<RankRow> rows)
    {
        lock (_sync)
        {
            using var tx = _db.BeginTransaction();
            foreach (var row in rows)
            {
                using var upsert = Command("""
                    INSERT INTO arena_ranks (character_id, category, season, rank, points, peak, updated)
                    VALUES ($id, $category, $season, $rank, $points, $peak, $updated)
                    ON CONFLICT (character_id, category) DO UPDATE SET season = $season, rank = $rank, points = $points, peak = $peak, updated = $updated
                    """, tx, ("$id", row.Character.ToString()), ("$category", row.Category), ("$season", row.Season), ("$rank", row.Rank),
                    ("$points", row.Points), ("$peak", row.Peak), ("$updated", row.Updated));
                upsert.ExecuteNonQuery();
            }
            tx.Commit();
        }
    }
}
