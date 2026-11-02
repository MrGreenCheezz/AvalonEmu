using System.Text.Json;

namespace AlbionLocal.Server.Game;

/// <summary>Account-owned local shop state, independent of character deletion. Gold/silver use a7m (×10000),
/// simulated cash uses cents, and receipt LearningPoints are whole points delivered to the selected character.</summary>
public sealed class ShopAccount
{
    public Guid Account { get; set; }
    public bool GoldInitialized { get; set; }
    public long Gold { get; set; }
    public HashSet<short> Unlocks { get; set; } = [];
    public Dictionary<Guid, ShopPremium> Premium { get; set; } = [];
    public Dictionary<short, int> Consumables { get; set; } = [];
    public Dictionary<Guid, ShopKillEmote> KillEmotes { get; set; } = [];
    public List<ShopOrder> Orders { get; set; } = [];
    /// <summary>Real local limit orders, escrow and executions; independent of character deletion.</summary>
    public GoldMarketState GoldMarket { get; set; } = new();
    /// <summary>Native event-based popup history is account-owned, with a character on every seen record.</summary>
    public EventBasedPopupState EventBasedPopups { get; set; } = new();
    public ShopAccount Clone() => JsonSerializer.Deserialize<ShopAccount>(JsonSerializer.Serialize(this))!;
}

public sealed record ShopPremium(long EndsAt, int PurchasedDays);

/// <summary>Selection is per character; its expendable charges belong to the account.</summary>
public sealed record ShopKillEmote(short ConsumableId = 0, bool PlayOnKnockdown = true);

/// <summary>A durable quote and receipt. Confirmation uses its stored terms, never a client-supplied price.</summary>
public sealed record ShopOrder
{
    public string Id { get; init; } = "";
    public Guid Account { get; init; }
    public Guid Character { get; init; }
    public string ProductId { get; init; } = "";
    public string Title { get; init; } = "";
    public string Currency { get; init; } = "cash";
    public long Price { get; init; }
    public long Gold { get; init; }
    public long Silver { get; init; }
    public int PremiumDays { get; init; }
    /// <summary>Whole learning points delivered to Character; omitted on older receipts means zero.</summary>
    public int LearningPoints { get; init; }
    /// <summary>Exact inventory contents of this quote; older receipts contain none.</summary>
    public ShopItem[] Items { get; init; } = [];
    public short[] Unlocks { get; init; } = [];
    public Dictionary<short, int> Charges { get; init; } = [];
    public string Status { get; init; } = "pending";
    public string? Error { get; init; }
    public long CreatedAt { get; init; }
    public long ExpiresAt { get; init; }
    public long? CompletedAt { get; init; }
}

public sealed record ShopProduct(string Id, string Title, string Category, string Currency, long Price,
    long Gold = 0, int PremiumDays = 0, short[]? Unlocks = null, string Description = "", int LearningPoints = 0, int GoldBonus = 0,
    ShopItem[]? Items = null);

/// <summary>Inventory item identity and exact quantity; the title is receipt text, never an item lookup key.</summary>
public sealed record ShopItem(string Name, int Count, string Title = "");

public sealed record ShopResult(bool Success, string Message, ShopOrder? Order = null);
