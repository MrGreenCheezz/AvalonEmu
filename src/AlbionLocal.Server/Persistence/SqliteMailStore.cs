using System.Text.Json;
using AlbionLocal.Server.Game;
using Microsoft.Data.Sqlite;

namespace AlbionLocal.Server.Persistence;

/// <summary><see cref="IMailStore"/> in the character database (schema v22: mails, mail_attachments, mail_sends).</summary>
public sealed partial class SqliteCharacterRepository : IMailStore
{
    /// <summary>The first mail id (GetMailInfos key 3 is a long; the captured ones have 7 digits).</summary>
    public const long FirstMailId = 1_000_000;

    private const string MailColumns = "m.id, coalesce(m.character, m.guild), m.guild IS NOT NULL, m.kind, m.category, m.sender, m.sender_id, m.from_guild, m.subject, m.body, "
        + "m.editor, m.received, m.expires, m.read, (SELECT count(*) FROM mail_attachments a WHERE a.mail = m.id)";

    /// <summary>The owner column of a mailbox (a constant, never input).</summary>
    private static string Box(bool guild) => guild ? "guild" : "character";

    public IReadOnlyList<Mail> MailPage(Guid owner, bool guild, MailCategory category, int page, int size)
    {
        lock (_sync)
            return ReadMails($"SELECT {MailColumns} FROM mails m WHERE m.{Box(guild)} = $owner AND ($category = 0 OR m.category = $category) ORDER BY m.id DESC LIMIT $size OFFSET $offset",
                null, ("$owner", owner.ToString()), ("$category", (int)category), ("$size", size), ("$offset", (long)Math.Max(0, page) * size));
    }

    public long MailCount(Guid owner, bool guild, MailCategory category)
    {
        lock (_sync)
        {
            using var count = Command($"SELECT count(*) FROM mails WHERE {Box(guild)} = $owner AND ($category = 0 OR category = $category)", null,
                ("$owner", owner.ToString()), ("$category", (int)category));
            return Convert.ToInt64(count.ExecuteScalar());
        }
    }

    public int UnreadMails(Guid owner, bool guild)
    {
        lock (_sync)
        {
            using var count = Command($"SELECT count(*) FROM mails WHERE {Box(guild)} = $owner AND read = 0", null, ("$owner", owner.ToString()));
            return Convert.ToInt32(count.ExecuteScalar());
        }
    }

    public Mail? Mail(long id)
    {
        lock (_sync) return ReadMails($"SELECT {MailColumns} FROM mails m WHERE m.id = $id", null, ("$id", id)).FirstOrDefault();
    }

    public IReadOnlyList<MailAttachment> Attachments(long mail)
    {
        lock (_sync)
        {
            using var select = Command("SELECT id, mail, item FROM mail_attachments WHERE mail = $mail ORDER BY rowid", null, ("$mail", mail));
            using var r = select.ExecuteReader();
            var list = new List<MailAttachment>();
            while (r.Read())
                if (JsonSerializer.Deserialize<InventoryItem>(r.GetString(2)) is { } item) list.Add(new MailAttachment(Guid.Parse(r.GetString(0)), r.GetInt64(1), item));
            return list;
        }
    }

    public long AddMail(Mail m, IReadOnlyList<InventoryItem>? items = null)
    {
        lock (_sync)
        {
            using var tx = _db.BeginTransaction();
            long id;
            // AUTOINCREMENT (seeded at 999999 in v22): an id is never given out twice, so the client's cached row and DeleteMail's
            // "everything up to the newest I listed" bound can never land on a mail the player has not seen.
            using (var insert = Command("""
                       INSERT INTO mails (character, guild, kind, category, sender, sender_id, from_guild, subject, body, editor, received, expires, read)
                       VALUES ($character, $guild, $kind, $category, $sender, $senderId, $fromGuild, $subject, $body, $editor, $received, $expires, 0)
                       RETURNING id
                       """, tx,
                       ("$character", m.Guild ? null : m.Owner.ToString()), ("$guild", m.Guild ? m.Owner.ToString() : null), ("$kind", (int)m.Kind),
                       ("$category", (int)m.Category), ("$sender", m.Sender), ("$senderId", m.SenderId == Guid.Empty ? null : m.SenderId.ToString()),
                       ("$fromGuild", m.FromGuild ? 1 : 0), ("$subject", m.Subject), ("$body", m.Body), ("$editor", m.Editor), ("$received", m.Received.Ticks),
                       ("$expires", m.Expires?.Ticks)))
                id = Convert.ToInt64(insert.ExecuteScalar());
            foreach (var item in items ?? [])
                using (var attach = Command("INSERT INTO mail_attachments (id, mail, item) VALUES ($id, $mail, $item)", tx,
                           ("$id", Guid.NewGuid().ToString()), ("$mail", id), ("$item", JsonSerializer.Serialize(item))))
                    attach.ExecuteNonQuery();
            tx.Commit();
            return id;
        }
    }

    public void MarkMailsRead(Guid owner, bool guild, IEnumerable<long> ids, bool read)
    {
        lock (_sync)
        {
            using var tx = _db.BeginTransaction();
            foreach (var id in ids.Distinct())
                using (var update = Command($"UPDATE mails SET read = $read WHERE id = $id AND {Box(guild)} = $owner", tx,
                           ("$read", read ? 1 : 0), ("$id", id), ("$owner", owner.ToString())))
                    update.ExecuteNonQuery();
            tx.Commit();
        }
    }

    public int DeleteMails(Guid owner, bool guild, IReadOnlyCollection<long>? ids, long upper, IReadOnlyCollection<long> keep, MailCategory category)
    {
        lock (_sync)
        {
            using var tx = _db.BeginTransaction();
            const string NoAttachments = "NOT EXISTS (SELECT 1 FROM mail_attachments a WHERE a.mail = mails.id)";
            var deleted = 0;
            if (ids is not null)
                foreach (var id in ids.Distinct())
                    using (var delete = Command($"DELETE FROM mails WHERE id = $id AND {Box(guild)} = $owner AND {NoAttachments}", tx, ("$id", id), ("$owner", owner.ToString())))
                        deleted += delete.ExecuteNonQuery();
            else
            {
                // The ids to keep go in as one JSON array (json_each), however many the client sent.
                using var delete = Command($"""
                    DELETE FROM mails WHERE {Box(guild)} = $owner AND id <= $upper AND ($category = 0 OR category = $category) AND {NoAttachments}
                        AND id NOT IN (SELECT value FROM json_each($keep))
                    """, tx, ("$owner", owner.ToString()), ("$upper", upper), ("$category", (int)category), ("$keep", JsonSerializer.Serialize(keep)));
                deleted = delete.ExecuteNonQuery();
            }
            tx.Commit();
            return deleted;
        }
    }

    public bool TakeAttachment(Guid id)
    {
        lock (_sync)
        {
            using var delete = Command("DELETE FROM mail_attachments WHERE id = $id", null, ("$id", id.ToString()));
            return delete.ExecuteNonQuery() == 1;
        }
    }

    public int PurgeMails(DateTime now)
    {
        lock (_sync)
        {
            using var delete = Command("DELETE FROM mails WHERE expires IS NOT NULL AND expires <= $now AND NOT EXISTS (SELECT 1 FROM mail_attachments a WHERE a.mail = mails.id)",
                null, ("$now", now.Ticks));
            return delete.ExecuteNonQuery();
        }
    }

    public void TrimMailbox(Guid owner, bool guild, int max)
    {
        lock (_sync)
        {
            using var delete = Command($"""
                DELETE FROM mails WHERE id IN (
                    SELECT id FROM mails WHERE {Box(guild)} = $owner AND read = 1 AND NOT EXISTS (SELECT 1 FROM mail_attachments a WHERE a.mail = mails.id)
                    ORDER BY id LIMIT max(0, (SELECT count(*) FROM mails WHERE {Box(guild)} = $owner) - $max))
                """, null, ("$owner", owner.ToString()), ("$max", max));
            delete.ExecuteNonQuery();
        }
    }

    public int MailSends(Guid character, DateTime since)
    {
        lock (_sync)
        {
            using var count = Command("SELECT count(*) FROM mail_sends WHERE character = $id AND at >= $since", null, ("$id", character.ToString()), ("$since", since.Ticks));
            return Convert.ToInt32(count.ExecuteScalar());
        }
    }

    public void AddMailSend(Guid character, DateTime at, DateTime keepFrom)
    {
        lock (_sync)
        {
            using var tx = _db.BeginTransaction();
            using (var old = Command("DELETE FROM mail_sends WHERE character = $id AND at < $keep", tx, ("$id", character.ToString()), ("$keep", keepFrom.Ticks)))
                old.ExecuteNonQuery();
            using (var add = Command("INSERT INTO mail_sends (character, at) VALUES ($id, $at)", tx, ("$id", character.ToString()), ("$at", at.Ticks)))
                add.ExecuteNonQuery();
            tx.Commit();
        }
    }

    public (Guid Id, string Name)? CharacterNamed(string name)
    {
        lock (_sync)
        {
            using var select = Command("SELECT id, name FROM characters WHERE name = $name", null, ("$name", name.Trim()));
            using var r = select.ExecuteReader();
            return r.Read() ? (Guid.Parse(r.GetString(0)), r.GetString(1)) : null;
        }
    }

    private List<Mail> ReadMails(string sql, SqliteTransaction? tx, params (string Name, object? Value)[] args)
    {
        using var select = Command(sql, tx, args);
        using var r = select.ExecuteReader();
        var list = new List<Mail>();
        while (r.Read())
            list.Add(new Mail(r.GetInt64(0), Guid.Parse(r.GetString(1)), r.GetInt64(2) != 0, (MailKind)r.GetInt32(3), (MailCategory)r.GetInt32(4), r.GetString(5),
                r.IsDBNull(6) ? Guid.Empty : Guid.Parse(r.GetString(6)), r.GetInt64(7) != 0, r.GetString(8), r.GetString(9), r.IsDBNull(10) ? null : r.GetString(10),
                new DateTime(r.GetInt64(11), DateTimeKind.Utc), r.IsDBNull(12) ? null : new DateTime(r.GetInt64(12), DateTimeKind.Utc), r.GetInt64(13) != 0,
                r.GetInt32(14)));
        return list;
    }
}
