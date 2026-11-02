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
/// season's template (0 = ARENA_IRON_1), the rank points, the highest rank whose rank-up chest was given, when its last result came,
/// and the highest observed rank earned (v51), whose unpaid chests survive a demotion.
/// </summary>
public sealed record RankRow(Guid Character, int Category, int Season, int Rank, int Points, int Peak, long Updated, int EarnedPeak = 0);

/// <summary>The arena ranks (<see cref="Systems.Ratings"/> keeps them in memory and writes each result through here at once).</summary>
public interface IRankStore
{
    IReadOnlyList<RankRow> LoadRanks();

    void SaveRanks(IReadOnlyList<RankRow> rows);
}

/// <summary><see cref="IRankStore"/> in the character database (v42: arena_ranks, v51: earned_peak).</summary>
public sealed partial class SqliteCharacterRepository : IRankStore, IRankRewardStore
{
    public IReadOnlyList<RankRow> LoadRanks()
    {
        lock (_sync)
        {
            var rows = new List<RankRow>();
            using var select = Command("SELECT character_id, category, season, rank, points, peak, updated, earned_peak FROM arena_ranks", null);
            using var r = select.ExecuteReader();
            while (r.Read())
                if (Guid.TryParse(r.GetString(0), out var id))
                    rows.Add(new RankRow(id, r.GetInt32(1), r.GetInt32(2), r.GetInt32(3), r.GetInt32(4), r.GetInt32(5), r.GetInt64(6), r.GetInt32(7)));
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
                    INSERT INTO arena_ranks (character_id, category, season, rank, points, peak, updated, earned_peak)
                    VALUES ($id, $category, $season, $rank, $points, $peak, $updated, $earned)
                    ON CONFLICT (character_id, category) DO UPDATE SET season = $season, rank = $rank, points = $points, peak = $peak,
                        updated = $updated, earned_peak = max(arena_ranks.earned_peak, $earned)
                    """, tx, ("$id", row.Character.ToString()), ("$category", row.Category), ("$season", row.Season), ("$rank", row.Rank),
                    ("$points", row.Points), ("$peak", row.Peak), ("$updated", row.Updated), ("$earned", Math.Max(row.EarnedPeak, Math.Max(row.Rank, row.Peak))));
                upsert.ExecuteNonQuery();
            }
            tx.Commit();
        }
    }

    public bool SaveArenaRankReward(RankRow expected, RankRow next, Game.Character character, Game.Mail? mail,
        IReadOnlyList<Game.InventoryItem> attachments, out long mailId)
    {
        mailId = 0;
        if (character.Transient || character.Id != expected.Character || next != (expected with { Peak = next.Peak })
            || next.Peak != expected.Peak + 1 || next.Peak > expected.EarnedPeak
            || mail is not null && (mail.Owner != character.Id || mail.Guild || attachments.Count == 0)
            || mail is null && attachments.Count != 0) return false;
        lock (_sync)
        {
            using var tx = _db.BeginTransaction();
            using (var owner = Command("SELECT account FROM characters WHERE id = $id", tx, ("$id", character.Id.ToString())))
                if (owner.ExecuteScalar() is not string account || account != character.Account.ToString()) return false;
            // The claim's expected row must still be durable. A failed preceding RP save cannot silently consume a reward.
            using (var update = Command("""
                UPDATE arena_ranks SET peak = $peak WHERE character_id = $id AND category = $category AND season = $season
                    AND rank = $rank AND points = $points AND peak = $previous AND updated = $updated AND earned_peak = $earned
                """, tx, ("$peak", next.Peak), ("$id", expected.Character.ToString()), ("$category", expected.Category),
                ("$season", expected.Season), ("$rank", expected.Rank), ("$points", expected.Points),
                ("$previous", expected.Peak), ("$updated", expected.Updated), ("$earned", expected.EarnedPeak)))
                if (update.ExecuteNonQuery() != 1) return false;
            if (!Write(character, tx)) return false;
            var deliveredMail = mail is null ? 0 : AddMail(mail, attachments, tx);
            tx.Commit();
            mailId = deliveredMail;
            return true;
        }
    }
}

/// <summary>Commits one complete rank's chest entitlement with its bag delivery or one overflow mail.
/// No split stores or success-only ACK can guarantee this: stores without this capability leave the entitlement pending.</summary>
public interface IRankRewardStore
{
    bool SaveArenaRankReward(RankRow expected, RankRow next, Game.Character character, Game.Mail? mail,
        IReadOnlyList<Game.InventoryItem> attachments, out long mailId);
}
