using System.Text.Json;
using AlbionLocal.Server.Game;
using Microsoft.Data.Sqlite;

namespace AlbionLocal.Server.Persistence;

/// <summary>
/// <see cref="IMarketStore"/> in the character database (schema v7: market_orders, market_finished, market_credits; v22 initial_amount; market_sales, econ SPEC §1), on
/// the repository's connection and lock: an order goes when its owner is deleted.
/// </summary>
public sealed partial class SqliteCharacterRepository : IMarketStore
{
    /// <summary>The first market id (AuctionDTO Id has 10 digits officially).</summary>
    public const long FirstMarketId = 1_000_000_000;

    private const string OrderColumns = "id, owner, type, location, uniquename, quality, amount, unit_price, created, expires, reference, item, initial_amount";
    private const string FinishedColumns = "id, owner, order_id, type, location, uniquename, quality, amount, unit_price, silver, created, expires, reference, item";

    public MarketOrder? Order(long id)
    {
        lock (_sync) return ReadOrders($"SELECT {OrderColumns} FROM market_orders WHERE id = $id", null, ("$id", id)).FirstOrDefault();
    }

    public IReadOnlyList<MarketOrder> Orders(string location, string type)
    {
        lock (_sync)
            return ReadOrders($"SELECT {OrderColumns} FROM market_orders WHERE location = $location AND type = $type ORDER BY id", null,
                ("$location", location), ("$type", type));
    }

    public IReadOnlyList<MarketOrder> OrdersOf(Guid owner, string? type = null)
    {
        lock (_sync)
            return ReadOrders($"SELECT {OrderColumns} FROM market_orders WHERE owner = $owner AND ($type IS NULL OR type = $type) ORDER BY id", null,
                ("$owner", owner.ToString()), ("$type", type));
    }

    public MarketFinished? Finished(long id)
    {
        lock (_sync) return ReadFinished($"SELECT {FinishedColumns} FROM market_finished WHERE id = $id", ("$id", id)).FirstOrDefault();
    }

    public IReadOnlyList<MarketFinished> FinishedOf(Guid owner)
    {
        lock (_sync) return ReadFinished($"SELECT {FinishedColumns} FROM market_finished WHERE owner = $owner ORDER BY id", ("$owner", owner.ToString()));
    }

    public void Batch(Action<IMarketBatch> body)
    {
        lock (_sync)
        {
            using var tx = _db.BeginTransaction(); // disposed without Commit = rolled back
            body(new MarketBatch(this, tx));
            tx.Commit();
        }
    }

    public IReadOnlyList<MarketOrder> ExpiredOrders(DateTime now)
    {
        lock (_sync) return ReadOrders($"SELECT {OrderColumns} FROM market_orders WHERE expires <= $now ORDER BY id", null, ("$now", now.Ticks));
    }

    public IReadOnlyList<MarketSale> Sales(string location, string itemName, int quality, DateTime since)
    {
        lock (_sync)
        {
            using var select = Command("""
                SELECT location, uniquename, quality, amount, unit_price, at FROM market_sales
                WHERE location = $location AND uniquename = $name AND at >= $since AND ($quality <= 0 OR quality = $quality) ORDER BY at DESC
                """, null, ("$location", location), ("$name", itemName), ("$since", since.Ticks), ("$quality", quality));
            using var r = select.ExecuteReader();
            var list = new List<MarketSale>();
            while (r.Read())
                list.Add(new MarketSale(r.GetString(0), r.GetString(1), r.GetInt32(2), r.GetInt32(3), r.GetInt64(4), new DateTime(r.GetInt64(5), DateTimeKind.Utc)));
            return list;
        }
    }

    public long TakeCredits(Guid character)
    {
        lock (_sync)
        {
            using var tx = _db.BeginTransaction();
            long sum;
            using (var select = Command("SELECT coalesce(sum(silver), 0) FROM market_credits WHERE character = $id", tx, ("$id", character.ToString())))
                sum = Convert.ToInt64(select.ExecuteScalar());
            using (var delete = Command("DELETE FROM market_credits WHERE character = $id", tx, ("$id", character.ToString())))
                delete.ExecuteNonQuery();
            tx.Commit();
            return sum;
        }
    }

    private sealed class MarketBatch(SqliteCharacterRepository repo, SqliteTransaction tx) : IMarketBatch
    {
        public long AddOrder(MarketOrder o)
        {
            var id = NextId();
            using var insert = repo.Command($"INSERT INTO market_orders ({OrderColumns}) VALUES ($id, $owner, $type, $location, $name, $quality, $amount, $price, $created, $expires, $reference, $item, $initial)", tx,
                ("$id", id), ("$owner", o.Owner.ToString()), ("$type", o.Type), ("$location", o.Location), ("$name", o.ItemName), ("$quality", o.Quality),
                ("$amount", o.Amount), ("$price", o.UnitPrice), ("$created", o.Created.Ticks), ("$expires", o.Expires.Ticks), ("$reference", o.Reference.ToString()),
                ("$item", o.Item is null ? null : JsonSerializer.Serialize(o.Item)), ("$initial", o.Placed));
            insert.ExecuteNonQuery();
            return id;
        }

        public void SetAmount(long order, int amount)
        {
            using var cmd = amount <= 0
                ? repo.Command("DELETE FROM market_orders WHERE id = $id", tx, ("$id", order))
                : repo.Command("UPDATE market_orders SET amount = $amount WHERE id = $id", tx, ("$id", order), ("$amount", amount));
            cmd.ExecuteNonQuery();
        }

        public long AddFinished(MarketFinished f)
        {
            var id = NextId();
            using var insert = repo.Command($"INSERT INTO market_finished ({FinishedColumns}) VALUES ($id, $owner, $order, $type, $location, $name, $quality, $amount, $price, $silver, $created, $expires, $reference, $item)", tx,
                ("$id", id), ("$owner", f.Owner.ToString()), ("$order", f.OrderId), ("$type", f.Type), ("$location", f.Location), ("$name", f.ItemName),
                ("$quality", f.Quality), ("$amount", f.Amount), ("$price", f.UnitPrice), ("$silver", f.Silver), ("$created", f.Created.Ticks),
                ("$expires", f.Expires.Ticks), ("$reference", f.Reference.ToString()), ("$item", f.Item is null ? null : JsonSerializer.Serialize(f.Item)));
            insert.ExecuteNonQuery();
            return id;
        }

        public void SetFinishedAmount(long entry, int amount)
        {
            using var cmd = amount <= 0
                ? repo.Command("DELETE FROM market_finished WHERE id = $id", tx, ("$id", entry))
                : repo.Command("UPDATE market_finished SET amount = $amount WHERE id = $id", tx, ("$id", entry), ("$amount", amount));
            cmd.ExecuteNonQuery();
        }

        public void DeleteFinished(long entry)
        {
            using var cmd = repo.Command("DELETE FROM market_finished WHERE id = $id", tx, ("$id", entry));
            cmd.ExecuteNonQuery();
        }

        public void AddSale(MarketSale sale)
        {
            if (sale.Amount <= 0) return;
            using (var old = repo.Command("DELETE FROM market_sales WHERE at < $before", tx, ("$before", (sale.At - TimeSpan.FromDays(IMarketStore.HistoryDays)).Ticks)))
                old.ExecuteNonQuery();
            using var insert = repo.Command("INSERT INTO market_sales (location, uniquename, quality, amount, unit_price, at) VALUES ($location, $name, $quality, $amount, $price, $at)", tx,
                ("$location", sale.Location), ("$name", sale.ItemName), ("$quality", sale.Quality), ("$amount", sale.Amount), ("$price", sale.UnitPrice), ("$at", sale.At.Ticks));
            insert.ExecuteNonQuery();
        }

        public void Credit(Guid character, long silver)
        {
            if (silver == 0) return;
            using var cmd = repo.Command("INSERT INTO market_credits (character, silver) VALUES ($id, $silver)", tx, ("$id", character.ToString()), ("$silver", silver));
            cmd.ExecuteNonQuery();
        }

        /// <summary>One more than the highest id in either table, at least <see cref="FirstMarketId"/>.</summary>
        private long NextId()
        {
            using var max = repo.Command("SELECT max(coalesce((SELECT max(id) FROM market_orders), 0), coalesce((SELECT max(id) FROM market_finished), 0))", tx);
            return Math.Max(FirstMarketId, Convert.ToInt64(max.ExecuteScalar()) + 1);
        }
    }

    private List<MarketOrder> ReadOrders(string sql, SqliteTransaction? tx, params (string Name, object? Value)[] args)
    {
        using var select = Command(sql, tx, args);
        using var r = select.ExecuteReader();
        var list = new List<MarketOrder>();
        while (r.Read())
            list.Add(new MarketOrder(r.GetInt64(0), Guid.Parse(r.GetString(1)), r.GetString(2), r.GetString(3), r.GetString(4), r.GetInt32(5), r.GetInt32(6),
                r.GetInt64(7), new DateTime(r.GetInt64(8), DateTimeKind.Utc), new DateTime(r.GetInt64(9), DateTimeKind.Utc), Guid.Parse(r.GetString(10)),
                r.IsDBNull(11) ? null : JsonSerializer.Deserialize<InventoryItem>(r.GetString(11)), r.IsDBNull(12) ? 0 : r.GetInt32(12)));
        return list;
    }

    private List<MarketFinished> ReadFinished(string sql, params (string Name, object? Value)[] args)
    {
        using var select = Command(sql, null, args);
        using var r = select.ExecuteReader();
        var list = new List<MarketFinished>();
        while (r.Read())
            list.Add(new MarketFinished(r.GetInt64(0), Guid.Parse(r.GetString(1)), r.GetInt64(2), r.GetString(3), r.GetString(4), r.GetString(5), r.GetInt32(6),
                r.GetInt32(7), r.GetInt64(8), r.GetInt64(9), new DateTime(r.GetInt64(10), DateTimeKind.Utc), new DateTime(r.GetInt64(11), DateTimeKind.Utc),
                Guid.Parse(r.GetString(12)), r.IsDBNull(13) ? null : JsonSerializer.Deserialize<InventoryItem>(r.GetString(13))));
        return list;
    }
}
