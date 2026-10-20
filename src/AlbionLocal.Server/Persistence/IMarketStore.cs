using AlbionLocal.Server.Game;

namespace AlbionLocal.Server.Persistence;

/// <summary>
/// An open marketplace order (stage 6 SPEC §6d, the AuctionDTO of the client): a sell order ("offer") holds the item stack
/// it took from the owner's inventory (<see cref="Item"/>, with durability and spells), a buy order ("request") the
/// silver it took (<see cref="UnitPrice"/> × <see cref="Amount"/>). Scoped to the marketplace's cluster, as official
/// per-city markets. Prices are a7m raw (silver × 10⁴). <see cref="Initial"/>: the amount it was placed with (stageJ mail SPEC §4.1:
/// the market mails' totals; 0 = not known, an order of before schema v22 or a standing NPC order, read as <see cref="Amount"/>).
/// </summary>
public sealed record MarketOrder(long Id, Guid Owner, string Type, string Location, string ItemName, int Quality, int Amount, long UnitPrice,
    DateTime Created, DateTime Expires, Guid Reference, InventoryItem? Item, int Initial = 0)
{
    public const string Offer = "offer", Request = "request";

    /// <summary>The amount placed: <see cref="Initial"/>, or the open amount when that is not known.</summary>
    public int Placed => Math.Max(Initial, Amount);

    /// <summary>How many were sold into (an offer) or bought by (a request) the order so far.</summary>
    public int Filled => Placed - Amount;
}

/// <summary>
/// A finished entry waiting to be collected by its owner (the market's "Finished" tab, AuctionFetchAuction): the items
/// bought from an offer or sold into the owner's request (<see cref="Amount"/> of <see cref="ItemName"/>, the stack in
/// <see cref="Item"/>), or what an expired order gives back (its items, or <see cref="Silver"/> of a buy order's escrow).
/// </summary>
public sealed record MarketFinished(long Id, Guid Owner, long OrderId, string Type, string Location, string ItemName, int Quality, int Amount,
    long UnitPrice, long Silver, DateTime Created, DateTime Expires, Guid Reference, InventoryItem? Item);

/// <summary>One sale at a city's market (econ SPEC §1): <see cref="Amount"/> of <see cref="ItemName"/> at <see cref="UnitPrice"/> (a7m raw), for the price history.</summary>
public sealed record MarketSale(string Location, string ItemName, int Quality, int Amount, long UnitPrice, DateTime At);

/// <summary>The writes of one market operation, all or nothing (<see cref="IMarketStore.Batch"/>).</summary>
public interface IMarketBatch
{
    /// <summary>Stores a new order (its Id is ignored) and returns its id.</summary>
    long AddOrder(MarketOrder order);

    /// <summary>Sets the open amount of an order; 0 deletes it.</summary>
    void SetAmount(long order, int amount);

    /// <summary>Stores a finished entry (its Id is ignored) and returns its id.</summary>
    long AddFinished(MarketFinished entry);

    /// <summary>Sets the amount left to collect of a finished entry; 0 (with no silver) deletes it.</summary>
    void SetFinishedAmount(long entry, int amount);

    void DeleteFinished(long entry);

    /// <summary>Records a sale for the price history (and drops the sales older than <see cref="IMarketStore.HistoryDays"/>).</summary>
    void AddSale(MarketSale sale);

    /// <summary>Silver (a7m raw) for a character that is not in the world, added to it at its next Join (<see cref="IMarketStore.TakeCredits"/>).</summary>
    void Credit(Guid character, long silver);
}

/// <summary>
/// The marketplace (ARCHITECTURE.md §5.5 "IOrderStore"): orders, finished entries and the silver owed to offline sellers,
/// saved at once (each operation is one transaction), unlike the characters, which <see cref="SaveScheduler"/> writes a
/// little later. Ids are one sequence over orders and finished entries, from 1000000000 (10 digits, as official).
/// </summary>
public interface IMarketStore
{
    MarketOrder? Order(long id);

    /// <summary>The open orders of <paramref name="type"/> at <paramref name="location"/>, oldest first.</summary>
    IReadOnlyList<MarketOrder> Orders(string location, string type);

    /// <summary>The open orders of <paramref name="owner"/> (of <paramref name="type"/>; null = both), oldest first.</summary>
    IReadOnlyList<MarketOrder> OrdersOf(Guid owner, string? type = null);

    MarketFinished? Finished(long id);

    /// <summary>The finished entries of <paramref name="owner"/>, oldest first.</summary>
    IReadOnlyList<MarketFinished> FinishedOf(Guid owner);

    /// <summary>Runs <paramref name="body"/> in one transaction: an exception rolls every write of it back.</summary>
    void Batch(Action<IMarketBatch> body);

    /// <summary>Takes (and deletes) the silver owed to <paramref name="character"/>: the sum, 0 = none.</summary>
    long TakeCredits(Guid character);

    /// <summary>How long the sales are kept: the chart's longest span (4 weeks) and a day.</summary>
    public const int HistoryDays = 29;

    /// <summary>The sales of <paramref name="itemName"/> at <paramref name="location"/> since <paramref name="since"/> (quality 0 = any), newest first.</summary>
    IReadOnlyList<MarketSale> Sales(string location, string itemName, int quality, DateTime since);

    /// <summary>The open orders whose end is at or before <paramref name="now"/>, oldest first (the expiry sweep, stageJ mail SPEC §4.2).</summary>
    IReadOnlyList<MarketOrder> ExpiredOrders(DateTime now);
}
