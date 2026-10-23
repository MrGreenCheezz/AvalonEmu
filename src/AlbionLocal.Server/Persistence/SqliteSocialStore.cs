namespace AlbionLocal.Server.Persistence;

/// <summary><see cref="ISocialStore"/> in the character database (schema v21: friends, friend_requests, mutes).</summary>
public sealed partial class SqliteCharacterRepository : ISocialStore
{
    public SocialData LoadSocial()
    {
        lock (_sync)
        {
            var friends = new List<(Guid, Guid, DateTime)>();
            var requests = new List<(Guid, Guid, DateTime)>();
            var mutes = new List<(Guid, Guid, string, DateTime)>();
            var profiles = new Dictionary<Guid, SocialProfile>();
            using (var select = Command("SELECT a, b, since FROM friends ORDER BY since, rowid", null))
            using (var r = select.ExecuteReader())
                while (r.Read()) friends.Add((Guid.Parse(r.GetString(0)), Guid.Parse(r.GetString(1)), new DateTime(r.GetInt64(2), DateTimeKind.Utc)));
            using (var select = Command("SELECT from_char, to_char, created FROM friend_requests ORDER BY created, rowid", null))
            using (var r = select.ExecuteReader())
                while (r.Read()) requests.Add((Guid.Parse(r.GetString(0)), Guid.Parse(r.GetString(1)), new DateTime(r.GetInt64(2), DateTimeKind.Utc)));
            using (var select = Command("SELECT owner, muted, muted_name, at FROM mutes ORDER BY at, rowid", null))
            using (var r = select.ExecuteReader())
                while (r.Read()) mutes.Add((Guid.Parse(r.GetString(0)), Guid.Parse(r.GetString(1)), r.GetString(2), new DateTime(r.GetInt64(3), DateTimeKind.Utc)));
            // A profile of every character a friend or request row names (the friends list shows offline ones too).
            using (var select = Command("""
                       SELECT id, name, template, faction, last_left_at FROM characters
                       WHERE id IN (SELECT a FROM friends UNION SELECT b FROM friends UNION SELECT from_char FROM friend_requests UNION SELECT to_char FROM friend_requests)
                       """, null))
            using (var r = select.ExecuteReader())
                while (r.Read())
                    profiles[Guid.Parse(r.GetString(0))] = new SocialProfile(r.GetString(1), r.GetInt32(2), (byte)r.GetInt32(3), r.GetInt64(4));
            return new SocialData(friends, requests, mutes, profiles);
        }
    }

    /// <summary>The pair's row key: the smaller Guid string first (PRIMARY KEY (a, b)).</summary>
    private static (string A, string B) Pair(Guid a, Guid b) =>
        string.CompareOrdinal(a.ToString(), b.ToString()) < 0 ? (a.ToString(), b.ToString()) : (b.ToString(), a.ToString());

    public void AddFriends(Guid a, Guid b, DateTime since)
    {
        var (x, y) = Pair(a, b);
        Execute("INSERT OR IGNORE INTO friends (a, b, since) VALUES ($a, $b, $since)", ("$a", x), ("$b", y), ("$since", since.Ticks));
    }

    public void RemoveFriends(Guid a, Guid b)
    {
        var (x, y) = Pair(a, b);
        Execute("DELETE FROM friends WHERE a = $a AND b = $b", ("$a", x), ("$b", y));
    }

    public void AddFriendRequest(Guid from, Guid to, DateTime at) =>
        Execute("INSERT OR REPLACE INTO friend_requests (from_char, to_char, created) VALUES ($from, $to, $at)", ("$from", from.ToString()), ("$to", to.ToString()), ("$at", at.Ticks));

    public void RemoveFriendRequest(Guid from, Guid to) =>
        Execute("DELETE FROM friend_requests WHERE from_char = $from AND to_char = $to", ("$from", from.ToString()), ("$to", to.ToString()));

    public void AddMute(Guid owner, Guid muted, string name, DateTime at) =>
        Execute("INSERT OR REPLACE INTO mutes (owner, muted, muted_name, at) VALUES ($owner, $muted, $name, $at)",
            ("$owner", owner.ToString()), ("$muted", muted.ToString()), ("$name", name), ("$at", at.Ticks));

    public void RemoveMute(Guid owner, Guid muted) =>
        Execute("DELETE FROM mutes WHERE owner = $owner AND muted = $muted", ("$owner", owner.ToString()), ("$muted", muted.ToString()));

    private void Execute(string sql, params (string, object?)[] values)
    {
        lock (_sync)
        {
            using var command = Command(sql, null, values);
            command.ExecuteNonQuery();
        }
    }
}
