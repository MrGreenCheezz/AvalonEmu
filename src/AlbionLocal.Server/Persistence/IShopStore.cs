using AlbionLocal.Server.Game;

namespace AlbionLocal.Server.Persistence;

/// <summary>Atomic shop entitlement/wallet/receipt storage, optionally with character snapshots (silver purchases).</summary>
public interface IShopStore
{
    ShopAccount? LoadShop(Guid account);
    /// <summary>Immutable per-character premium; backends may cache this without exposing mutable wallets or receipts.</summary>
    ShopPremium? LoadPremium(Guid account, Guid character) => LoadShop(account)?.Premium.GetValueOrDefault(character);
    bool SaveShop(IReadOnlyList<ShopAccount> accounts, IReadOnlyList<Character> characters);
    /// <summary>A backend must provide the complete persisted local order book before accepting limit orders.</summary>
    bool SupportsGoldMarket => false;
    IReadOnlyList<ShopAccount> LoadGoldMarketAccounts() => [];
    /// <summary>Accounts with completed dealer receipts or book fills, for shared execution history.
    /// Backends overriding only the order book retain their known fills; SQLite supplies both real sources.</summary>
    IReadOnlyList<ShopAccount> LoadGoldExecutionAccounts() => LoadGoldMarketAccounts();
}
