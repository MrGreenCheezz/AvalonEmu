using System.Text.Json;
using AlbionLocal.Server.Game;

namespace AlbionLocal.Server.Persistence;

/// <summary><see cref="IGuildBoardStore"/> in the character database (the stageR guild step: guild_profiles, guild_applications, guild_logs).</summary>
public sealed partial class SqliteCharacterRepository : IGuildBoardStore
{
    public IReadOnlyDictionary<Guid, GuildProfile> LoadGuildProfiles()
    {
        lock (_sync)
        {
            var all = new Dictionary<Guid, GuildProfile>();
            using var select = Command("SELECT guild, data FROM guild_profiles", null);
            using var r = select.ExecuteReader();
            while (r.Read())
                if (JsonSerializer.Deserialize<GuildProfile>(r.GetString(1)) is { } p) all[Guid.Parse(r.GetString(0))] = p;
            return all;
        }
    }

    public void SaveGuildProfile(Guid guild, GuildProfile profile)
    {
        lock (_sync)
        {
            using var upsert = Command("INSERT INTO guild_profiles (guild, data) VALUES ($guild, $data) ON CONFLICT (guild) DO UPDATE SET data = $data", null,
                ("$guild", guild.ToString()), ("$data", JsonSerializer.Serialize(profile)));
            upsert.ExecuteNonQuery();
        }
    }

    public void AddGuildApplication(GuildApplication a)
    {
        lock (_sync)
        {
            using var insert = Command("""
                INSERT OR REPLACE INTO guild_applications (mail, guild, character, stats, applied) VALUES ($mail, $guild, $character, $stats, $applied)
                """, null, ("$mail", a.Mail), ("$guild", a.Guild.ToString()), ("$character", a.Character.ToString()), ("$stats", a.Stats), ("$applied", a.Applied.Ticks));
            insert.ExecuteNonQuery();
        }
    }

    public GuildApplication? GuildApplication(long mail) =>
        OneApplication("SELECT mail, guild, character, stats, applied FROM guild_applications WHERE mail = $mail", ("$mail", mail));

    public GuildApplication? GuildApplicationOf(Guid guild, Guid character) =>
        OneApplication("SELECT mail, guild, character, stats, applied FROM guild_applications WHERE guild = $guild AND character = $character ORDER BY mail DESC LIMIT 1",
            ("$guild", guild.ToString()), ("$character", character.ToString()));

    private GuildApplication? OneApplication(string sql, params (string, object?)[] args)
    {
        lock (_sync)
        {
            using var select = Command(sql, null, args);
            using var r = select.ExecuteReader();
            return r.Read()
                ? new GuildApplication(r.GetInt64(0), Guid.Parse(r.GetString(1)), Guid.Parse(r.GetString(2)), r.GetInt32(3), new DateTime(r.GetInt64(4), DateTimeKind.Utc))
                : null;
        }
    }

    public void DeleteGuildApplication(long mail)
    {
        lock (_sync)
        {
            using var delete = Command("DELETE FROM guild_applications WHERE mail = $mail", null, ("$mail", mail));
            delete.ExecuteNonQuery();
        }
    }

    public void AddGuildLog(Guid guild, GuildLogEntry e)
    {
        lock (_sync)
        {
            using var insert = Command("""
                INSERT INTO guild_logs (guild, type, reason, actor, parameter, amount, at) VALUES ($guild, $type, $reason, $actor, $parameter, $amount, $at)
                """, null, ("$guild", guild.ToString()), ("$type", e.Type), ("$reason", e.Reason), ("$actor", e.Actor), ("$parameter", e.Parameter),
                ("$amount", e.Amount), ("$at", e.At));
            insert.ExecuteNonQuery();
        }
    }

    public IReadOnlyList<GuildLogEntry> GuildLogs(Guid guild, int type, long since)
    {
        lock (_sync)
        {
            var rows = new List<GuildLogEntry>();
            using var select = Command("""
                SELECT type, reason, actor, parameter, amount, at FROM guild_logs WHERE guild = $guild AND type = $type AND at >= $since ORDER BY at DESC, id DESC
                """, null, ("$guild", guild.ToString()), ("$type", type), ("$since", since));
            using var r = select.ExecuteReader();
            while (r.Read()) rows.Add(new GuildLogEntry(r.GetInt32(0), r.GetInt32(1), r.GetString(2), r.GetString(3), r.GetInt64(4), r.GetInt64(5)));
            return rows;
        }
    }

    public int PurgeGuildLogs(long before)
    {
        lock (_sync)
        {
            using var delete = Command("DELETE FROM guild_logs WHERE at < $before", null, ("$before", before));
            return delete.ExecuteNonQuery();
        }
    }
}
