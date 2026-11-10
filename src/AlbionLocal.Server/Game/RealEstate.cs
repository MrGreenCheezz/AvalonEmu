using System.Globalization;
using System.Xml.Linq;

namespace AlbionLocal.Server.Game;

/// <summary>
/// The real-estate numbers of gamedata.xml (analysis/work/realestate/SPEC.md §1): &lt;RealEstateAuctionSettings&gt; (the auction schedule and the
/// owner's bid factor) and &lt;RealEstateSettings&gt;&lt;Sale&gt; (the claim price: square metre prices by ground kind, a factor per location quality
/// A-E). The defaults are 345319's values, used when a part is missing. Upkeep (all 0 in 345319) and the outline costs are read too.
/// </summary>
public sealed record RealEstateSettings
{
    public static RealEstateSettings Default { get; } = new();

    /// <summary>auctioncycledurationdays (28): a plot goes to auction once per cycle.</summary>
    public double CycleDays { get; init; } = 28;

    /// <summary>auctionlengthdays (7): how long an auction runs.</summary>
    public double LengthDays { get; init; } = 7;

    /// <summary>auctioncyclereferencetimestamp (2017-07-03T12:00 UTC): the start of the first cycle.</summary>
    public DateTime Reference { get; init; } = new(2017, 7, 3, 12, 0, 0, DateTimeKind.Utc);

    /// <summary>ownerexclusivebidtimeinmin (1440): the final minutes of an auction in which only the owner may bid.</summary>
    public double OwnerExclusiveMinutes { get; init; } = 1440;

    /// <summary>ownerbidmultiplier (2): what an owner's bid on the own plot counts.</summary>
    public double OwnerBidMultiplier { get; init; } = 2;

    /// <summary>Sale squaremeterpricenormal (0): the silver per m² of ground that is not building area.</summary>
    public double SquareMetrePriceNormal { get; init; }

    /// <summary>Sale squaremeterpricebuildingarea (12500): the silver per m² of building area.</summary>
    public double SquareMetrePriceBuilding { get; init; } = 12500;

    /// <summary>Sale overallpricefactor (1).</summary>
    public double OverallPriceFactor { get; init; } = 1;

    /// <summary>Sale LocationQuality factors (A 40, B 9, C 3, D 1.5, E 1).</summary>
    public IReadOnlyDictionary<string, double> QualityFactors { get; init; } =
        new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase) { ["A"] = 40, ["B"] = 9, ["C"] = 3, ["D"] = 1.5, ["E"] = 1 };

    /// <summary>Upkeep squaremeterpricebuildingarea (0 in 345319: no upkeep).</summary>
    public double UpkeepPerSquareMetre { get; init; }

    /// <summary>OutlineTypes (Marker 0, Fence 1000, Wall 2000): the cost of each outline, in the order of the client's OutlineType enum.</summary>
    public IReadOnlyList<(string Name, double Cost)> Outlines { get; init; } = [("Marker", 0), ("Fence", 1000), ("Wall", 2000)];

    /// <summary>The settings from gamedata.xml's root; a missing part keeps its default.</summary>
    public static RealEstateSettings From(XElement gamedata)
    {
        static double Num(XElement? e, string attr, double fallback) =>
            double.TryParse((string?)e?.Attribute(attr), NumberStyles.Float, CultureInfo.InvariantCulture, out var d) ? d : fallback;
        var d = Default;
        var auction = gamedata.Descendants("RealEstateAuctionSettings").FirstOrDefault();
        var settings = gamedata.Descendants("RealEstateSettings").FirstOrDefault();
        var sale = settings?.Element("Sale");
        var upkeep = settings?.Element("Upkeep");
        var reference = DateTime.TryParse((string?)auction?.Attribute("auctioncyclereferencetimestamp"), CultureInfo.InvariantCulture,
            DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal, out var r) ? r : d.Reference;
        var factors = sale?.Elements("LocationQuality")
            .Where(q => (string?)q.Attribute("locationType") is { Length: > 0 })
            .ToDictionary(q => (string)q.Attribute("locationType")!, q => Num(q, "factor", 1), StringComparer.OrdinalIgnoreCase);
        var outlines = settings?.Element("OutlineTypes")?.Elements("OutlineType")
            .Select(o => ((string?)o.Attribute("name") ?? "", Num(o, "cost", 0))).Where(o => o.Item1.Length > 0).ToList();
        return new RealEstateSettings
        {
            CycleDays = Num(auction, "auctioncycledurationdays", d.CycleDays),
            LengthDays = Num(auction, "auctionlengthdays", d.LengthDays),
            Reference = reference,
            OwnerExclusiveMinutes = Num(auction, "ownerexclusivebidtimeinmin", d.OwnerExclusiveMinutes),
            OwnerBidMultiplier = Num(auction, "ownerbidmultiplier", d.OwnerBidMultiplier),
            SquareMetrePriceNormal = Num(sale, "squaremeterpricenormal", d.SquareMetrePriceNormal),
            SquareMetrePriceBuilding = Num(sale, "squaremeterpricebuildingarea", d.SquareMetrePriceBuilding),
            OverallPriceFactor = Num(sale, "overallpricefactor", d.OverallPriceFactor),
            QualityFactors = factors is { Count: > 0 } ? factors : d.QualityFactors,
            UpkeepPerSquareMetre = Num(upkeep, "squaremeterpricebuildingarea", d.UpkeepPerSquareMetre),
            Outlines = outlines is { Count: > 0 } ? outlines : d.Outlines,
        };
    }
}

/// <summary>
/// The state of a real-estate plot of a city (schema v45 real_estates; analysis/work/realestate/SPEC.md §2): its template Guid (NewRealEstate
/// key 1), the owner (the character and its name; <see cref="Guid.Empty"/> = free), since when, the price of the last won auction or claim, the
/// owner's sale price (a7m; <see cref="NotForSale"/> = none), the outline (OutlineType byte) and the start of the last auction settled for it.
/// A row exists only while the plot is owned. Touched only under World.Sync.
/// </summary>
public sealed class RealEstatePlot(Guid id, string cluster)
{
    /// <summary>NewRealEstate key 10 of a plot that is not for sale: a7m of -1 silver (the client's cly.arf compares with it).</summary>
    public const long NotForSale = -10_000;

    public Guid Id { get; } = id;

    /// <summary>The world.xml cluster the plot is in.</summary>
    public string Cluster { get; } = cluster;

    public Guid Owner { get; set; }

    public string OwnerName { get; set; } = "";

    /// <summary>GameTimeStamp the owner got it (NewRealEstate key 12).</summary>
    public long Since { get; set; }

    /// <summary>a7m: the winning bid of the last auction, or the claim price (key 11, the client's "Last winning bid").</summary>
    public long LastPrice { get; set; }

    /// <summary>a7m: the owner's asking price (SetSaleStatusForEstate; key 10), or <see cref="NotForSale"/>.</summary>
    public long SalePrice { get; set; } = NotForSale;

    /// <summary>The outline (client OutlineType: 0 Marker, 1 Fence, 2 Wall; key 7).</summary>
    public byte Outline { get; set; }

    public bool Owned => Owner != Guid.Empty;

    public override string ToString() => $"plot {Id:D} in {Cluster}";
}

/// <summary>
/// A bid on an owned plot's auction (schema v45 real_estate_bids): the bidder's highest bid of the auction that started at <see cref="Auction"/>
/// (a7m, held from the bidder's silver until the auction is settled), <see cref="Settled"/> once it was paid or given back.
/// </summary>
public sealed record RealEstateBid(Guid Plot, Guid Bidder, string BidderName, long Amount, long Placed, long Auction, bool Settled);

/// <summary>A building its owner put up on a city plot (schema v45 real_estate_buildings): a buildings.xml crafting station, its centre and facing.</summary>
public sealed record RealEstateBuilding(Guid Id, Guid Plot, string Name, float X, float Z, float Rotation, long Built);
