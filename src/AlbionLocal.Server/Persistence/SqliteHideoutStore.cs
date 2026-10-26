using System.Numerics;
using AlbionLocal.Server.Game;

namespace AlbionLocal.Server.Persistence;

/// <summary><see cref="IHideoutStore"/> in the character database (schema v40: hideouts, hideout_homes).</summary>
public sealed partial class SqliteCharacterRepository : IHideoutStore
{
    public IReadOnlyList<Hideout> LoadHideouts()
    {
        lock (_sync)
        {
            var list = new List<Hideout>();
            using var select = Command("""
                SELECT id, guild, cluster, x, z, rotation, placed, phase, phase_ends, level, upgrade_started, nutrition, shields, last_upkeep, tribute, access
                FROM hideouts ORDER BY placed, rowid
                """, null);
            using var r = select.ExecuteReader();
            while (r.Read())
                list.Add(new Hideout(Guid.Parse(r.GetString(0)), Guid.Parse(r.GetString(1)), r.GetString(2), new Vector2((float)r.GetDouble(3), (float)r.GetDouble(4)),
                    (float)r.GetDouble(5), r.GetInt64(6))
                {
                    Phase = r.GetInt32(7), PhaseEnds = r.GetInt64(8), Level = r.GetInt32(9), UpgradeStarted = r.GetInt64(10), Nutrition = r.GetDouble(11),
                    Shields = r.GetInt32(12), LastUpkeep = r.GetInt64(13), Tribute = r.GetInt64(14), Access = (HideoutAccess)r.GetInt32(15),
                });
            return list;
        }
    }

    public void SaveHideout(Hideout h)
    {
        lock (_sync)
        {
            using var upsert = Command("""
                INSERT INTO hideouts (id, guild, cluster, x, z, rotation, placed, phase, phase_ends, level, upgrade_started, nutrition, shields, last_upkeep, tribute, access)
                VALUES ($id, $guild, $cluster, $x, $z, $rot, $placed, $phase, $ends, $level, $upgrade, $food, $shields, $upkeep, $tribute, $access)
                ON CONFLICT (id) DO UPDATE SET phase = $phase, phase_ends = $ends, level = $level, upgrade_started = $upgrade, nutrition = $food,
                    shields = $shields, last_upkeep = $upkeep, tribute = $tribute, access = $access
                """, null, ("$id", h.Id.ToString()), ("$guild", h.Guild.ToString()), ("$cluster", h.Cluster), ("$x", (double)h.Position.X), ("$z", (double)h.Position.Y),
                ("$rot", (double)h.Rotation), ("$placed", h.Placed), ("$phase", h.Phase), ("$ends", h.PhaseEnds), ("$level", h.Level), ("$upgrade", h.UpgradeStarted),
                ("$food", h.Nutrition), ("$shields", h.Shields), ("$upkeep", h.LastUpkeep), ("$tribute", h.Tribute), ("$access", (int)h.Access));
            upsert.ExecuteNonQuery();
        }
    }

    public void DeleteHideout(Guid id)
    {
        lock (_sync)
        {
            using var delete = Command("DELETE FROM hideouts WHERE id = $id", null, ("$id", id.ToString()));
            delete.ExecuteNonQuery();
        }
    }

    public IReadOnlyList<(Guid Character, Guid Hideout)> LoadHideoutHomes()
    {
        lock (_sync)
        {
            var list = new List<(Guid, Guid)>();
            using var select = Command("SELECT character, hideout FROM hideout_homes", null);
            using var r = select.ExecuteReader();
            while (r.Read()) list.Add((Guid.Parse(r.GetString(0)), Guid.Parse(r.GetString(1))));
            return list;
        }
    }

    public void SaveHideoutHome(Guid character, Guid? hideout)
    {
        lock (_sync)
        {
            using var cmd = hideout is { } h
                ? Command("INSERT OR REPLACE INTO hideout_homes (character, hideout) VALUES ($c, $h)", null, ("$c", character.ToString()), ("$h", h.ToString()))
                : Command("DELETE FROM hideout_homes WHERE character = $c", null, ("$c", character.ToString()));
            cmd.ExecuteNonQuery();
        }
    }
}
