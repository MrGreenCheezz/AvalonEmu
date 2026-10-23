namespace AlbionLocal.Server.Persistence;

/// <summary><see cref="IRoadStore"/> in the character database (schema v26: road_links, road_slots).</summary>
public sealed partial class SqliteCharacterRepository : IRoadStore
{
    public (IReadOnlyList<RoadLinkRow> Links, IReadOnlyList<RoadSlotRow> Slots) LoadRoads()
    {
        lock (_sync)
        {
            var links = new List<RoadLinkRow>();
            using (var select = Command("""
                       SELECT id, out_cluster, out_path, in_cluster, in_path, type, opened, closes, charges, next_charge, recent_until FROM road_links ORDER BY id
                       """, null))
            using (var r = select.ExecuteReader())
                while (r.Read())
                    links.Add(new RoadLinkRow(r.GetInt64(0), r.GetString(1), r.GetString(2), r.GetString(3), r.GetString(4), r.GetInt32(5), r.GetInt64(6), r.GetInt64(7),
                        r.GetInt32(8), r.GetInt64(9), r.GetInt64(10)));
            var slots = new List<RoadSlotRow>();
            using (var select = Command("SELECT cluster, path, state, keyword, closes FROM road_slots ORDER BY cluster, path", null))
            using (var r = select.ExecuteReader())
                while (r.Read())
                    slots.Add(new RoadSlotRow(r.GetString(0), r.GetString(1), r.GetInt32(2), r.IsDBNull(3) ? null : r.GetString(3), r.GetInt64(4)));
            return (links, slots);
        }
    }

    public void SaveRoads(IReadOnlyList<RoadLinkRow> links, IReadOnlyList<long> droppedLinks, IReadOnlyList<RoadSlotRow> slots, IReadOnlyList<(string Cluster, string Path)> droppedSlots)
    {
        lock (_sync)
        {
            using var tx = _db.BeginTransaction(); // one Commit for the batch
            foreach (var id in droppedLinks)
            {
                using var delete = Command("DELETE FROM road_links WHERE id = $id", tx, ("$id", id));
                delete.ExecuteNonQuery();
            }
            foreach (var l in links)
            {
                using var upsert = Command("""
                    INSERT INTO road_links (id, out_cluster, out_path, in_cluster, in_path, type, opened, closes, charges, next_charge, recent_until)
                    VALUES ($id, $oc, $op, $ic, $ip, $type, $opened, $closes, $charges, $next, $recent)
                    ON CONFLICT (id) DO UPDATE SET closes = $closes, charges = $charges, next_charge = $next, recent_until = $recent
                    """, tx, ("$id", l.Id), ("$oc", l.OutCluster), ("$op", l.OutPath), ("$ic", l.InCluster), ("$ip", l.InPath), ("$type", l.Type),
                    ("$opened", l.Opened), ("$closes", l.Closes), ("$charges", l.Charges), ("$next", l.NextCharge), ("$recent", l.RecentUntil));
                upsert.ExecuteNonQuery();
            }
            foreach (var (cluster, path) in droppedSlots)
            {
                using var delete = Command("DELETE FROM road_slots WHERE cluster = $c AND path = $p", tx, ("$c", cluster), ("$p", path));
                delete.ExecuteNonQuery();
            }
            foreach (var s in slots)
            {
                using var upsert = Command("""
                    INSERT INTO road_slots (cluster, path, state, keyword, closes) VALUES ($c, $p, $state, $keyword, $closes)
                    ON CONFLICT (cluster, path) DO UPDATE SET state = $state, keyword = $keyword, closes = $closes
                    """, tx, ("$c", s.Cluster), ("$p", s.Path), ("$state", s.State), ("$keyword", s.Keyword), ("$closes", s.Closes));
                upsert.ExecuteNonQuery();
            }
            tx.Commit();
        }
    }

    public void ClearRoads()
    {
        lock (_sync)
        {
            using var tx = _db.BeginTransaction();
            using (var links = Command("DELETE FROM road_links", tx)) links.ExecuteNonQuery();
            using (var slots = Command("DELETE FROM road_slots", tx)) slots.ExecuteNonQuery();
            tx.Commit();
        }
    }
}
