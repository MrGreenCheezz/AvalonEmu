using AlbionLocal.Server.Game;

namespace AlbionLocal.Server.Persistence;

/// <summary>
/// The owned city plots, the bids on their auctions and the buildings on them (analysis/work/realestate/SPEC.md; schema v45: real_estates,
/// real_estate_bids, real_estate_buildings), each change saved at once. A plot's row goes with its owner character (ON DELETE CASCADE), a bid
/// with its bidder, a building with its plot.
/// </summary>
public interface IRealEstateStore
{
    /// <summary>Every owned plot with its owner's name (from the character table).</summary>
    IReadOnlyList<RealEstatePlot> LoadPlots();

    /// <summary>Stores <paramref name="plot"/> (claimed, changed hands, its sale price or outline changed).</summary>
    void SavePlot(RealEstatePlot plot);

    /// <summary>Removes the row of a plot that became free (given up), with its buildings.</summary>
    void DeletePlot(Guid id);

    /// <summary>Every bid with its bidder's name.</summary>
    IReadOnlyList<RealEstateBid> LoadBids();

    /// <summary>Stores a bid (new, raised or settled); one row per plot, bidder and auction.</summary>
    void SaveBid(RealEstateBid bid);

    /// <summary>Removes the settled bids of auctions that started before <paramref name="auction"/>.</summary>
    void DeleteBidsBefore(long auction);

    IReadOnlyList<RealEstateBuilding> LoadRealEstateBuildings();

    void SaveRealEstateBuilding(RealEstateBuilding building);

    void DeleteRealEstateBuilding(Guid id);
}
