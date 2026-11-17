namespace AlbionLocal.Server.Persistence;

/// <summary>
/// One PvP kill or knockdown as saved (stageR stats SPEC §1): its id (the Guid the client asks the details of), the game time (GameTimeStamp
/// ticks), the killer's and the victim's character ids and names, whether it was a knockdown, the killer's fame (a7m raw), both sides' average
/// item power, and the details JSON the client's KillHistoryDetails reads.
/// </summary>
public sealed record KillRow(Guid Id, long At, Guid Killer, string KillerName, Guid Victim, string VictimName, bool Knockdown, long Fame,
    int KillerIp, int VictimIp, string Details);

/// <summary>The kill history (<see cref="Systems.KillHistory"/> keeps it in memory and writes each new row through here at once).</summary>
public interface IKillHistoryStore
{
    IReadOnlyList<KillRow> LoadKills();

    void SaveKill(KillRow row);
}

/// <summary><see cref="IKillHistoryStore"/> in the character database (stageR stats: kill_history).</summary>
public sealed partial class SqliteCharacterRepository : IKillHistoryStore
{
    public IReadOnlyList<KillRow> LoadKills()
    {
        lock (_sync)
        {
            var rows = new List<KillRow>();
            using var select = Command("SELECT id, at, killer, killer_name, victim, victim_name, knockdown, fame, killer_ip, victim_ip, details FROM kill_history ORDER BY at", null);
            using var r = select.ExecuteReader();
            while (r.Read())
                if (Guid.TryParse(r.GetString(0), out var id) && Guid.TryParse(r.GetString(2), out var killer) && Guid.TryParse(r.GetString(4), out var victim))
                    rows.Add(new KillRow(id, r.GetInt64(1), killer, r.GetString(3), victim, r.GetString(5), r.GetInt64(6) != 0, r.GetInt64(7),
                        r.GetInt32(8), r.GetInt32(9), r.GetString(10)));
            return rows;
        }
    }

    public void SaveKill(KillRow row)
    {
        lock (_sync)
        {
            using var insert = Command("""
                INSERT OR REPLACE INTO kill_history (id, at, killer, killer_name, victim, victim_name, knockdown, fame, killer_ip, victim_ip, details)
                VALUES ($id, $at, $killer, $kname, $victim, $vname, $knockdown, $fame, $kip, $vip, $details)
                """, null, ("$id", row.Id.ToString()), ("$at", row.At), ("$killer", row.Killer.ToString()), ("$kname", row.KillerName),
                ("$victim", row.Victim.ToString()), ("$vname", row.VictimName), ("$knockdown", row.Knockdown ? 1 : 0), ("$fame", row.Fame),
                ("$kip", row.KillerIp), ("$vip", row.VictimIp), ("$details", row.Details));
            insert.ExecuteNonQuery();
        }
    }
}
