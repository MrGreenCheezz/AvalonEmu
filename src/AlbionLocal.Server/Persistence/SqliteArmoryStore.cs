using System.Text.Json;
using AlbionLocal.Photon;
using AlbionLocal.Server.Game;

namespace AlbionLocal.Server.Persistence;

public sealed partial class SqliteCharacterRepository : IArmoryStore
{
    public IReadOnlyList<ArmorySample> LoadArmorySamples(long since)
    {
        lock (_sync)
        {
            var result = new List<ArmorySample>();
            using var cmd = Command("SELECT state FROM armory_samples WHERE updated >= $since", null, ("$since", since));
            using var reader = cmd.ExecuteReader();
            while (reader.Read())
            {
                try
                {
                    if (JsonSerializer.Deserialize<ArmorySample>(reader.GetString(0)) is { } row
                        && row.Id != Guid.Empty && row.Metrics is { Length: 23 } && row.Slots is { Length: 10 }) result.Add(row);
                }
                catch (JsonException ex) { Log.Warn("armory", $"Unreadable sample preserved in database: {ex.Message}"); }
            }
            return result;
        }
    }
    public void SaveArmorySamples(IReadOnlyList<ArmorySample> samples, long retainSince)
    {
        if (samples.Count == 0) return;
        lock (_sync)
        {
            using var tx = _db.BeginTransaction();
            foreach (var row in samples)
            {
                using var cmd = Command("""
                    INSERT INTO armory_samples(id, updated, state) VALUES($id, $updated, $state)
                    ON CONFLICT(id) DO UPDATE SET updated=excluded.updated, state=excluded.state
                    """, tx, ("$id", row.Id.ToString("D")), ("$updated", row.Updated), ("$state", JsonSerializer.Serialize(row)));
                cmd.ExecuteNonQuery();
            }
            using var trim = Command("DELETE FROM armory_samples WHERE updated < $since", tx, ("$since", retainSince));
            trim.ExecuteNonQuery();
            tx.Commit();
        }
    }
}
