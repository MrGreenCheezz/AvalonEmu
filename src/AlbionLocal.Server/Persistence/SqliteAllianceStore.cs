using AlbionLocal.Server.Game;

namespace AlbionLocal.Server.Persistence;

/// <summary><see cref="IAllianceStore"/> in the character database (schema v38: alliances, alliance_guilds, alliance_invites, alliance_history).</summary>
public sealed partial class SqliteCharacterRepository : IAllianceStore
{
    public IReadOnlyList<Alliance> LoadAlliances()
    {
        lock (_sync)
        {
            var alliances = new Dictionary<Guid, Alliance>();
            using (var select = Command("SELECT id, name, tag, founder, created FROM alliances ORDER BY created, rowid", null))
            using (var r = select.ExecuteReader())
                while (r.Read())
                {
                    var a = new Alliance(Guid.Parse(r.GetString(0)), r.GetString(1), r.GetString(2), Guid.Parse(r.GetString(3)),
                        new DateTime(r.GetInt64(4), DateTimeKind.Utc));
                    alliances[a.Id] = a;
                }
            using (var select = Command("SELECT alliance, guild, joined FROM alliance_guilds ORDER BY alliance, position", null))
            using (var r = select.ExecuteReader())
                while (r.Read())
                    if (alliances.TryGetValue(Guid.Parse(r.GetString(0)), out var a))
                        a.Guilds.Add(new AllianceGuild(Guid.Parse(r.GetString(1)), new DateTime(r.GetInt64(2), DateTimeKind.Utc)));
            return [.. alliances.Values];
        }
    }

    public void SaveAlliance(Alliance a)
    {
        lock (_sync)
        {
            using var tx = _db.BeginTransaction(); // disposed without Commit = rolled back
            using (var upsert = Command("""
                       INSERT INTO alliances (id, name, tag, founder, created) VALUES ($id, $name, $tag, $founder, $created)
                       ON CONFLICT (id) DO UPDATE SET founder = $founder
                       """, tx, ("$id", a.Id.ToString()), ("$name", a.Name), ("$tag", a.Tag), ("$founder", a.Founder.ToString()), ("$created", a.Created.Ticks)))
                upsert.ExecuteNonQuery();
            using (var clear = Command("DELETE FROM alliance_guilds WHERE alliance = $id", tx, ("$id", a.Id.ToString()))) clear.ExecuteNonQuery();
            for (var i = 0; i < a.Guilds.Count; i++)
            {
                var m = a.Guilds[i];
                using var add = Command("""
                    INSERT OR REPLACE INTO alliance_guilds (guild, alliance, joined, position) VALUES ($guild, $alliance, $joined, $position)
                    """, tx, ("$guild", m.Guild.ToString()), ("$alliance", a.Id.ToString()), ("$joined", m.Joined.Ticks), ("$position", i));
                add.ExecuteNonQuery();
            }
            tx.Commit();
        }
    }

    public void DeleteAlliance(Guid id)
    {
        lock (_sync)
        {
            using var delete = Command("DELETE FROM alliances WHERE id = $id", null, ("$id", id.ToString())); // guilds and invitations follow (ON DELETE CASCADE)
            delete.ExecuteNonQuery();
        }
    }

    public IReadOnlyList<AllianceInvite> LoadAllianceInvites()
    {
        lock (_sync)
        {
            var list = new List<AllianceInvite>();
            using var select = Command("SELECT alliance, guild, mail, invited FROM alliance_invites ORDER BY invited", null);
            using var r = select.ExecuteReader();
            while (r.Read())
                list.Add(new AllianceInvite(Guid.Parse(r.GetString(0)), Guid.Parse(r.GetString(1)), r.GetInt64(2), new DateTime(r.GetInt64(3), DateTimeKind.Utc)));
            return list;
        }
    }

    public void SaveAllianceInvite(AllianceInvite i)
    {
        lock (_sync)
        {
            using var add = Command("INSERT OR REPLACE INTO alliance_invites (alliance, guild, mail, invited) VALUES ($alliance, $guild, $mail, $at)", null,
                ("$alliance", i.Alliance.ToString()), ("$guild", i.Guild.ToString()), ("$mail", i.Mail), ("$at", i.At.Ticks));
            add.ExecuteNonQuery();
        }
    }

    public void DeleteAllianceInvite(Guid alliance, Guid guild)
    {
        lock (_sync)
        {
            using var delete = Command("DELETE FROM alliance_invites WHERE alliance = $alliance AND guild = $guild", null,
                ("$alliance", alliance.ToString()), ("$guild", guild.ToString()));
            delete.ExecuteNonQuery();
        }
    }

    public IReadOnlyList<AllianceHistory> LoadAllianceHistory()
    {
        lock (_sync)
        {
            var list = new List<AllianceHistory>();
            using var select = Command("SELECT guild, changed, last_alliance, left_at FROM alliance_history", null);
            using var r = select.ExecuteReader();
            while (r.Read())
                list.Add(new AllianceHistory(Guid.Parse(r.GetString(0)), new DateTime(r.GetInt64(1), DateTimeKind.Utc),
                    r.IsDBNull(2) ? Guid.Empty : Guid.Parse(r.GetString(2)), r.IsDBNull(3) ? null : new DateTime(r.GetInt64(3), DateTimeKind.Utc)));
            return list;
        }
    }

    public void SaveAllianceHistory(AllianceHistory h)
    {
        lock (_sync)
        {
            using var set = Command("INSERT OR REPLACE INTO alliance_history (guild, changed, last_alliance, left_at) VALUES ($guild, $changed, $last, $left)", null,
                ("$guild", h.Guild.ToString()), ("$changed", h.Changed.Ticks),
                ("$last", h.LastAlliance == Guid.Empty ? DBNull.Value : h.LastAlliance.ToString()), ("$left", h.LeftAt is { } at ? at.Ticks : DBNull.Value));
            set.ExecuteNonQuery();
        }
    }
}
