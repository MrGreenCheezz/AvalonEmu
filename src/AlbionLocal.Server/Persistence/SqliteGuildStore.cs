using System.Text.Json;
using AlbionLocal.Server.Game;
using Microsoft.Data.Sqlite;

namespace AlbionLocal.Server.Persistence;

/// <summary><see cref="IGuildStore"/> in the character database (schema v17: guilds, guild_members, guild_left; v18: the contracts; v25: the guild goals).</summary>
public sealed partial class SqliteCharacterRepository : IGuildStore
{
    public IReadOnlyList<Guild> LoadGuilds()
    {
        lock (_sync)
        {
            var guilds = new Dictionary<Guid, Guild>();
            using (var select = Command("SELECT id, name, created, logo, banner, silver, tax, xp, tags, contracts, progress FROM guilds ORDER BY created, rowid", null))
            using (var r = select.ExecuteReader())
                while (r.Read())
                {
                    var g = new Guild(Guid.Parse(r.GetString(0)), r.GetString(1), new DateTime(r.GetInt64(2), DateTimeKind.Utc),
                        JsonSerializer.Deserialize<GuildLogo>(r.GetString(3)) ?? GuildLogo.Default, r.GetString(4),
                        JsonSerializer.Deserialize<Guid[]>(r.GetString(8)) ?? [])
                    {
                        SilverA7m = r.GetInt64(5), TaxA7m = r.GetInt64(6), Xp = r.GetInt64(7),
                        Contracts = r.IsDBNull(9) ? null : JsonSerializer.Deserialize<GuildContracts>(r.GetString(9)),
                        Progress = (r.IsDBNull(10) ? null : JsonSerializer.Deserialize<GuildProgress>(r.GetString(10))) ?? new GuildProgress(),
                    };
                    guilds[g.Id] = g;
                }
            using (var select = Command("""
                       SELECT m.guild, m.character, c.name, m.role, m.joined, m.last_seen
                       FROM guild_members m JOIN characters c ON c.id = m.character ORDER BY m.guild, m.position
                       """, null))
            using (var r = select.ExecuteReader())
                while (r.Read())
                    if (guilds.TryGetValue(Guid.Parse(r.GetString(0)), out var g))
                        g.Members.Add(new GuildMember(Guid.Parse(r.GetString(1)), r.GetString(2), (GuildRole)r.GetInt32(3), new DateTime(r.GetInt64(4), DateTimeKind.Utc))
                        {
                            LastSeen = new DateTime(r.GetInt64(5), DateTimeKind.Utc),
                        });
            return [.. guilds.Values];
        }
    }

    public void SaveGuild(Guild g)
    {
        lock (_sync)
        {
            using var tx = _db.BeginTransaction(); // disposed without Commit = rolled back
            using (var upsert = Command("""
                       INSERT INTO guilds (id, name, created, logo, banner, silver, tax, xp, tags, contracts, progress)
                       VALUES ($id, $name, $created, $logo, $banner, $silver, $tax, $xp, $tags, $contracts, $progress)
                       ON CONFLICT (id) DO UPDATE SET name = $name, logo = $logo, banner = $banner, silver = $silver, tax = $tax, xp = $xp, tags = $tags,
                           contracts = $contracts, progress = $progress
                       """, tx, ("$id", g.Id.ToString()), ("$name", g.Name), ("$created", g.Created.Ticks), ("$logo", JsonSerializer.Serialize(g.Logo)),
                       ("$banner", g.Banner), ("$silver", g.SilverA7m), ("$tax", g.TaxA7m), ("$xp", g.Xp), ("$tags", JsonSerializer.Serialize(g.Tags)),
                       ("$contracts", ContractsJson(g)), ("$progress", JsonSerializer.Serialize(g.Progress))))
                upsert.ExecuteNonQuery();
            using (var clear = Command("DELETE FROM guild_members WHERE guild = $id", tx, ("$id", g.Id.ToString()))) clear.ExecuteNonQuery();
            for (var i = 0; i < g.Members.Count; i++)
            {
                var m = g.Members[i];
                // A member moved from another guild row (never: one guild per character) replaces it (the character is the key).
                using var add = Command("""
                    INSERT OR REPLACE INTO guild_members (character, guild, role, joined, last_seen, position)
                    VALUES ($character, $guild, $role, $joined, $seen, $position)
                    """, tx, ("$character", m.Id.ToString()), ("$guild", g.Id.ToString()), ("$role", (int)m.Role), ("$joined", m.Joined.Ticks),
                    ("$seen", m.LastSeen.Ticks), ("$position", i));
                add.ExecuteNonQuery();
            }
            tx.Commit();
        }
    }

    public void SaveGuildProgress(Guild g)
    {
        lock (_sync)
        {
            using var update = Command("UPDATE guilds SET silver = $silver, xp = $xp, contracts = $contracts, progress = $progress WHERE id = $id", null,
                ("$id", g.Id.ToString()), ("$silver", g.SilverA7m), ("$xp", g.Xp), ("$contracts", ContractsJson(g)), ("$progress", JsonSerializer.Serialize(g.Progress)));
            update.ExecuteNonQuery();
        }
    }

    private static object ContractsJson(Guild g) => g.Contracts is { } c ? JsonSerializer.Serialize(c) : DBNull.Value;

    public void DeleteGuild(Guid id)
    {
        lock (_sync)
        {
            using var delete = Command("DELETE FROM guilds WHERE id = $id", null, ("$id", id.ToString())); // the members follow (ON DELETE CASCADE)
            delete.ExecuteNonQuery();
        }
    }

    public DateTime? GuildLeftAt(Guid character)
    {
        lock (_sync)
        {
            using var select = Command("SELECT left_at FROM guild_left WHERE character = $id", null, ("$id", character.ToString()));
            return select.ExecuteScalar() is long ticks ? new DateTime(ticks, DateTimeKind.Utc) : null;
        }
    }

    public void SetGuildLeftAt(Guid character, DateTime at)
    {
        lock (_sync)
        {
            using var set = Command("INSERT OR REPLACE INTO guild_left (character, left_at) VALUES ($id, $at)", null, ("$id", character.ToString()), ("$at", at.Ticks));
            set.ExecuteNonQuery();
        }
    }
}
