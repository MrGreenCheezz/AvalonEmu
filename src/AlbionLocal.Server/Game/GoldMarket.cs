namespace AlbionLocal.Server.Game;

/// <summary>Account-owned local order book data, serialized inside the existing shop_accounts transaction.
/// All monetary values are a7m raw (10000 = one unit); timestamps are UTC GameTime ticks.</summary>
public sealed class GoldMarketState
{
    public List<GoldLimitOrder> Orders { get; set; } = [];
    public List<GoldMarketFill> Fills { get; set; } = [];
    public List<GoldMarketRequest> Requests { get; set; } = [];
    /// <summary>Silver owed to a deleted funding character. Claimed only by a character of this account.</summary>
    public long PendingSilver { get; set; }
}

public sealed record GoldLimitOrder
{
    public string Id { get; init; } = "";
    public Guid Account { get; init; }
    public Guid Character { get; init; }
    public bool Buy { get; init; }
    public long Gold { get; init; }
    public long Remaining { get; init; }
    public long Price { get; init; }
    public long SilverEscrow { get; init; }
    public long SetupFee { get; init; }
    public long CreatedAt { get; init; }
    public long ExpiresAt { get; init; }
    public string Status { get; init; } = "open";
}

/// <summary>The same immutable execution is saved on both accounts; Id deduplicates the public history.</summary>
public sealed record GoldMarketFill(string Id, Guid Buyer, Guid Seller, string BuyOrder, string SellOrder,
    long Gold, long Silver, long Price, long At);

/// <summary>Public read model of a completed local execution. Source distinguishes an actual dealer receipt
/// from an order-book fill without inventing a dealer account/counterparty. Monetary values remain raw a7m.</summary>
public sealed record GoldMarketExecution(string Id, string Source, long Gold, long Silver, long Price, long At);

/// <summary>The original reply is retained even after subsequent fills/cancellation, so a replay cannot spend again.</summary>
public sealed record GoldMarketRequest(string Id, string Operation, bool Buy, Guid Character,
    long Gold, long Price, int Hours, long TaxRate, GoldMarketResult Result);

public sealed record GoldMarketResult(short Code = 0, string Message = "", long Remaining = 0,
    long Executed = 0, long Silver = 0, long AveragePrice = 0, long TaxRate = 0, long SetupFee = 0)
{
    public bool Success => Code is 0 or 9843 or 9882; // native callbacks also recognize an immediate/partial execution
}

public sealed record GoldMarketInfo(short Code, string Message, GoldLimitOrder? Buy = null, GoldLimitOrder? Sell = null);
