namespace AlbionLocal.Server.Persistence;

/// <summary><see cref="ITerritoryStore"/> in the character database (schema v39: territory_owners; v41: territory_state).</summary>
public sealed partial class SqliteCharacterRepository : ITerritoryStore
{
    public IReadOnlyList<TerritoryOwner> LoadTerritoryOwners()
    {
        lock (_sync)
        {
            var list = new List<TerritoryOwner>();
            using var select = Command("""
                SELECT o.territory, o.guild, o.since, s.nutrition, s.fed_at, s.access, s.power, s.power_at
                FROM territory_owners o LEFT JOIN territory_state s ON s.territory = o.territory ORDER BY o.since
                """, null);
            using var r = select.ExecuteReader();
            while (r.Read())
                list.Add(new TerritoryOwner(Guid.Parse(r.GetString(0)), Guid.Parse(r.GetString(1)), new DateTime(r.GetInt64(2), DateTimeKind.Utc))
                {
                    Nutrition = r.IsDBNull(3) ? 0 : r.GetDouble(3),
                    FedAt = r.IsDBNull(4) ? default : new DateTime(r.GetInt64(4), DateTimeKind.Utc),
                    Access = r.IsDBNull(5) ? (byte)0 : (byte)r.GetInt64(5),
                    Power = r.IsDBNull(6) ? 0 : r.GetDouble(6),
                    PowerAt = r.IsDBNull(7) ? default : new DateTime(r.GetInt64(7), DateTimeKind.Utc),
                });
            return list;
        }
    }

    public void SaveTerritoryOwner(TerritoryOwner o)
    {
        lock (_sync)
        {
            using var tx = _db.BeginTransaction();
            var id = ("$territory", (object?)o.Territory.ToString());
            if (o.Guild == Guid.Empty)
            {
                using var delete = Command("DELETE FROM territory_state WHERE territory = $territory; DELETE FROM territory_owners WHERE territory = $territory", tx, id);
                delete.ExecuteNonQuery();
            }
            else
            {
                using (var owner = Command("""
                    INSERT INTO territory_owners (territory, guild, since) VALUES ($territory, $guild, $since)
                    ON CONFLICT (territory) DO UPDATE SET guild = excluded.guild, since = excluded.since
                    """, tx, id, ("$guild", o.Guild.ToString()), ("$since", o.Since.Ticks)))
                    owner.ExecuteNonQuery();
                using var state = Command("""
                    INSERT OR REPLACE INTO territory_state (territory, nutrition, fed_at, access, power, power_at)
                    VALUES ($territory, $nutrition, $fed, $access, $power, $powerat)
                    """, tx, id, ("$nutrition", o.Nutrition), ("$fed", o.FedAt.Ticks), ("$access", (int)o.Access), ("$power", o.Power), ("$powerat", o.PowerAt.Ticks));
                state.ExecuteNonQuery();
            }
            tx.Commit();
        }
    }
}
