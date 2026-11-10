using AlbionLocal.Server.Game;

namespace AlbionLocal.Server.Persistence;

/// <summary><see cref="IRealEstateStore"/> in the character database (schema v45: real_estates, real_estate_bids, real_estate_buildings).</summary>
public sealed partial class SqliteCharacterRepository : IRealEstateStore
{
    public IReadOnlyList<RealEstatePlot> LoadPlots()
    {
        lock (_sync)
        {
            var list = new List<RealEstatePlot>();
            using var select = Command("""
                SELECT p.id, p.cluster, p.owner, c.name, p.since, p.last_price, p.sale_price, p.outline
                FROM real_estates p JOIN characters c ON c.id = p.owner ORDER BY p.since, p.rowid
                """, null);
            using var r = select.ExecuteReader();
            while (r.Read())
                list.Add(new RealEstatePlot(Guid.Parse(r.GetString(0)), r.GetString(1))
                {
                    Owner = Guid.Parse(r.GetString(2)), OwnerName = r.GetString(3), Since = r.GetInt64(4), LastPrice = r.GetInt64(5),
                    SalePrice = r.GetInt64(6), Outline = (byte)r.GetInt32(7),
                });
            return list;
        }
    }

    public void SavePlot(RealEstatePlot p)
    {
        lock (_sync)
        {
            using var upsert = Command("""
                INSERT INTO real_estates (id, cluster, owner, since, last_price, sale_price, outline)
                VALUES ($id, $cluster, $owner, $since, $last, $sale, $outline)
                ON CONFLICT (id) DO UPDATE SET owner = $owner, since = $since, last_price = $last, sale_price = $sale, outline = $outline
                """, null, ("$id", p.Id.ToString()), ("$cluster", p.Cluster), ("$owner", p.Owner.ToString()), ("$since", p.Since),
                ("$last", p.LastPrice), ("$sale", p.SalePrice), ("$outline", (int)p.Outline));
            upsert.ExecuteNonQuery();
        }
    }

    public void DeletePlot(Guid id)
    {
        lock (_sync)
        {
            using var delete = Command("DELETE FROM real_estates WHERE id = $id", null, ("$id", id.ToString()));
            delete.ExecuteNonQuery();
        }
    }

    public IReadOnlyList<RealEstateBid> LoadBids()
    {
        lock (_sync)
        {
            var list = new List<RealEstateBid>();
            using var select = Command("""
                SELECT b.plot, b.bidder, c.name, b.amount, b.placed, b.auction, b.settled
                FROM real_estate_bids b JOIN characters c ON c.id = b.bidder ORDER BY b.placed, b.rowid
                """, null);
            using var r = select.ExecuteReader();
            while (r.Read())
                list.Add(new RealEstateBid(Guid.Parse(r.GetString(0)), Guid.Parse(r.GetString(1)), r.GetString(2), r.GetInt64(3), r.GetInt64(4), r.GetInt64(5),
                    r.GetInt64(6) != 0));
            return list;
        }
    }

    public void SaveBid(RealEstateBid b)
    {
        lock (_sync)
        {
            using var upsert = Command("""
                INSERT INTO real_estate_bids (plot, bidder, auction, amount, placed, settled) VALUES ($plot, $bidder, $auction, $amount, $placed, $settled)
                ON CONFLICT (plot, bidder, auction) DO UPDATE SET amount = $amount, placed = $placed, settled = $settled
                """, null, ("$plot", b.Plot.ToString()), ("$bidder", b.Bidder.ToString()), ("$auction", b.Auction), ("$amount", b.Amount),
                ("$placed", b.Placed), ("$settled", b.Settled ? 1 : 0));
            upsert.ExecuteNonQuery();
        }
    }

    public void DeleteBidsBefore(long auction)
    {
        lock (_sync)
        {
            using var delete = Command("DELETE FROM real_estate_bids WHERE settled = 1 AND auction < $auction", null, ("$auction", auction));
            delete.ExecuteNonQuery();
        }
    }

    public IReadOnlyList<RealEstateBuilding> LoadRealEstateBuildings()
    {
        lock (_sync)
        {
            var list = new List<RealEstateBuilding>();
            using var select = Command("SELECT id, plot, name, x, z, rotation, built FROM real_estate_buildings ORDER BY built, rowid", null);
            using var r = select.ExecuteReader();
            while (r.Read())
                list.Add(new RealEstateBuilding(Guid.Parse(r.GetString(0)), Guid.Parse(r.GetString(1)), r.GetString(2), (float)r.GetDouble(3), (float)r.GetDouble(4),
                    (float)r.GetDouble(5), r.GetInt64(6)));
            return list;
        }
    }

    public void SaveRealEstateBuilding(RealEstateBuilding b)
    {
        lock (_sync)
        {
            using var upsert = Command("""
                INSERT INTO real_estate_buildings (id, plot, name, x, z, rotation, built) VALUES ($id, $plot, $name, $x, $z, $rot, $built)
                ON CONFLICT (id) DO UPDATE SET name = $name, x = $x, z = $z, rotation = $rot
                """, null, ("$id", b.Id.ToString()), ("$plot", b.Plot.ToString()), ("$name", b.Name), ("$x", (double)b.X), ("$z", (double)b.Z),
                ("$rot", (double)b.Rotation), ("$built", b.Built));
            upsert.ExecuteNonQuery();
        }
    }

    public void DeleteRealEstateBuilding(Guid id)
    {
        lock (_sync)
        {
            using var delete = Command("DELETE FROM real_estate_buildings WHERE id = $id", null, ("$id", id.ToString()));
            delete.ExecuteNonQuery();
        }
    }
}
