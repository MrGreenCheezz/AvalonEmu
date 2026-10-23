using AlbionLocal.Server.Game;

namespace AlbionLocal.Server.Persistence;

/// <summary><see cref="IIslandStore"/> in the character database (schema v19: islands; v20: island_buildings, island_farmables; v28: the animals' nutrition and growth; v33: island_labourers).</summary>
public sealed partial class SqliteCharacterRepository : IIslandStore
{
    public IReadOnlyList<Island> LoadIslands()
    {
        lock (_sync)
        {
            var islands = new List<Island>();
            using var select = Command("""
                SELECT i.id, i.owner, c.name, i.island_type, i.level, i.access_point, i.bought, i.level_changed, i.favorite, i.hidden
                FROM islands i JOIN characters c ON c.id = i.owner ORDER BY i.bought, i.rowid
                """, null);
            using var r = select.ExecuteReader();
            while (r.Read())
                islands.Add(new Island(Guid.Parse(r.GetString(0)), Guid.Parse(r.GetString(1)), r.GetString(2), r.GetInt32(3), r.GetString(5))
                {
                    Level = r.GetInt32(4), Bought = r.GetInt64(6), LevelChanged = r.GetInt64(7), Favorite = r.GetInt64(8) != 0, Hidden = r.GetInt64(9) != 0,
                });
            return islands;
        }
    }

    public void SaveIsland(Island i)
    {
        lock (_sync)
        {
            using var upsert = Command("""
                INSERT INTO islands (id, owner, island_type, level, access_point, bought, level_changed, favorite, hidden)
                VALUES ($id, $owner, $type, $level, $ap, $bought, $changed, $favorite, $hidden)
                ON CONFLICT (id) DO UPDATE SET level = $level, level_changed = $changed, favorite = $favorite, hidden = $hidden
                """, null, ("$id", i.Id.ToString()), ("$owner", i.Owner.ToString()), ("$type", i.Type), ("$level", i.Level), ("$ap", i.AccessPoint),
                ("$bought", i.Bought), ("$changed", i.LevelChanged), ("$favorite", i.Favorite ? 1 : 0), ("$hidden", i.Hidden ? 1 : 0));
            upsert.ExecuteNonQuery();
        }
    }

    public void DeleteIsland(Guid id)
    {
        lock (_sync)
        {
            using var delete = Command("DELETE FROM islands WHERE id = $id", null, ("$id", id.ToString()));
            delete.ExecuteNonQuery();
        }
    }

    public IReadOnlyList<IslandBuilding> LoadIslandBuildings()
    {
        lock (_sync)
        {
            var list = new List<IslandBuilding>();
            using var select = Command("SELECT id, island, name, x, z, rotation, built FROM island_buildings ORDER BY built, rowid", null);
            using var r = select.ExecuteReader();
            while (r.Read())
                list.Add(new IslandBuilding(Guid.Parse(r.GetString(0)), Guid.Parse(r.GetString(1)), r.GetString(2), (float)r.GetDouble(3), (float)r.GetDouble(4),
                    (float)r.GetDouble(5), r.GetInt64(6)));
            return list;
        }
    }

    public void SaveIslandBuilding(IslandBuilding b)
    {
        lock (_sync)
        {
            using var upsert = Command("""
                INSERT INTO island_buildings (id, island, name, x, z, rotation, built) VALUES ($id, $island, $name, $x, $z, $rot, $built)
                ON CONFLICT (id) DO UPDATE SET name = $name, x = $x, z = $z, rotation = $rot
                """, null, ("$id", b.Id.ToString()), ("$island", b.Island.ToString()), ("$name", b.Name), ("$x", (double)b.X), ("$z", (double)b.Z),
                ("$rot", (double)b.Rotation), ("$built", b.Built));
            upsert.ExecuteNonQuery();
        }
    }

    public void DeleteIslandBuilding(Guid id)
    {
        lock (_sync)
        {
            using var delete = Command("DELETE FROM island_buildings WHERE id = $id", null, ("$id", id.ToString()));
            delete.ExecuteNonQuery();
        }
    }

    public IReadOnlyList<Farmable> LoadFarmables()
    {
        lock (_sync)
        {
            var list = new List<Farmable>();
            using var select = Command("SELECT id, island, building, item, x, z, planted, boosts, boosted_at, nutrition, fed_at, growth FROM island_farmables ORDER BY planted, rowid", null);
            using var r = select.ExecuteReader();
            while (r.Read())
                list.Add(new Farmable(Guid.Parse(r.GetString(0)), Guid.Parse(r.GetString(1)), Guid.Parse(r.GetString(2)), r.GetString(3), (float)r.GetDouble(4),
                    (float)r.GetDouble(5), r.GetInt64(6)) { Boosts = r.GetInt32(7), BoostedAt = r.GetInt64(8), Nutrition = r.GetDouble(9), FedAt = r.GetInt64(10), Growth = r.GetInt64(11) });
            return list;
        }
    }

    public void SaveFarmable(Farmable f)
    {
        lock (_sync)
        {
            using var upsert = Command("""
                INSERT INTO island_farmables (id, island, building, item, x, z, planted, boosts, boosted_at, nutrition, fed_at, growth)
                VALUES ($id, $island, $building, $item, $x, $z, $planted, $boosts, $at, $nutrition, $fed, $growth)
                ON CONFLICT (id) DO UPDATE SET planted = $planted, boosts = $boosts, boosted_at = $at, nutrition = $nutrition, fed_at = $fed, growth = $growth
                """, null, ("$id", f.Id.ToString()), ("$island", f.Island.ToString()), ("$building", f.Building.ToString()), ("$item", f.Item),
                ("$x", (double)f.X), ("$z", (double)f.Z), ("$planted", f.Planted), ("$boosts", f.Boosts), ("$at", f.BoostedAt),
                ("$nutrition", f.Nutrition), ("$fed", f.FedAt), ("$growth", f.Growth));
            upsert.ExecuteNonQuery();
        }
    }

    public void DeleteFarmable(Guid id)
    {
        lock (_sync)
        {
            using var delete = Command("DELETE FROM island_farmables WHERE id = $id", null, ("$id", id.ToString()));
            delete.ExecuteNonQuery();
        }
    }

    public IReadOnlyList<IslandLabourer> LoadLabourers()
    {
        lock (_sync)
        {
            var list = new List<IslandLabourer>();
            using var select = Command("""
                SELECT id, island, building, type, name, x, z, rotation, hired, fame, happiness, happiness_at, journal, job_start, job_end, job_yield
                FROM island_labourers ORDER BY hired, rowid
                """, null);
            using var r = select.ExecuteReader();
            while (r.Read())
                list.Add(new IslandLabourer(Guid.Parse(r.GetString(0)), Guid.Parse(r.GetString(1)), Guid.Parse(r.GetString(2)), r.GetString(3), r.GetString(4),
                    (float)r.GetDouble(5), (float)r.GetDouble(6), (float)r.GetDouble(7), r.GetInt64(8))
                {
                    Fame = r.GetInt64(9), Happiness = r.GetDouble(10), HappinessAt = r.GetInt64(11), Journal = r.IsDBNull(12) ? null : r.GetString(12),
                    JobStart = r.GetInt64(13), JobEnd = r.GetInt64(14), JobYield = r.GetDouble(15),
                });
            return list;
        }
    }

    public void SaveLabourer(IslandLabourer l)
    {
        lock (_sync)
        {
            using var upsert = Command("""
                INSERT INTO island_labourers (id, island, building, type, name, x, z, rotation, hired, fame, happiness, happiness_at, journal, job_start, job_end, job_yield)
                VALUES ($id, $island, $building, $type, $name, $x, $z, $rot, $hired, $fame, $happiness, $hat, $journal, $start, $end, $yield)
                ON CONFLICT (id) DO UPDATE SET type = $type, hired = $hired, x = $x, z = $z, rotation = $rot, fame = $fame, happiness = $happiness, happiness_at = $hat,
                    journal = $journal, job_start = $start, job_end = $end, job_yield = $yield
                """, null, ("$id", l.Id.ToString()), ("$island", l.Island.ToString()), ("$building", l.Building.ToString()), ("$type", l.Type), ("$name", l.Name),
                ("$x", (double)l.X), ("$z", (double)l.Z), ("$rot", (double)l.Rotation), ("$hired", l.Hired), ("$fame", l.Fame), ("$happiness", l.Happiness),
                ("$hat", l.HappinessAt), ("$journal", (object?)l.Journal ?? DBNull.Value), ("$start", l.JobStart), ("$end", l.JobEnd), ("$yield", l.JobYield));
            upsert.ExecuteNonQuery();
        }
    }

    public void DeleteLabourer(Guid id)
    {
        lock (_sync)
        {
            using var delete = Command("DELETE FROM island_labourers WHERE id = $id", null, ("$id", id.ToString()));
            delete.ExecuteNonQuery();
        }
    }
}
