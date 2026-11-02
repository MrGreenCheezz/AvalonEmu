using System.Text.Json;
using AlbionLocal.Server.Game;

namespace AlbionLocal.Server.Persistence;

/// <summary>Schema v44. A checkout's account state and character snapshots commit together, or none do.
/// Receipt entitlements, including whole LearningPoints, are additive JSON fields (old orders default to zero).
/// The delivered LP balance uses the existing character learning_points/lp_stamp columns in the same transaction.</summary>
public sealed partial class SqliteCharacterRepository
{
    // The gameplay hot path needs only immutable premium records, never a cloned wallet/history. All access
    // and invalidation share _sync; failed transactions leave these last-committed values unchanged.
    private readonly Dictionary<Guid, Dictionary<Guid, ShopPremium>> _shopPremium = [];

    public bool SupportsGoldMarket => true;

    /// <summary>Additive v44 JSON state; no schema migration or separate wallet is needed.</summary>
    public IReadOnlyList<ShopAccount> LoadGoldMarketAccounts() => ReadGoldAccounts("""
        SELECT account, state FROM shop_accounts
        WHERE json_array_length(state, '$.GoldMarket.Orders') > 0
           OR json_array_length(state, '$.GoldMarket.Fills') > 0
           OR json_array_length(state, '$.GoldMarket.Requests') > 0
           OR coalesce(json_extract(state, '$.GoldMarket.PendingSilver'), 0) > 0
        """);

    /// <summary>The market graph is shared, so actual dealer receipts of other accounts also contribute.
    /// This read query does not initialize wallets, execute orders, or change the matching book's scope.</summary>
    public IReadOnlyList<ShopAccount> LoadGoldExecutionAccounts() => ReadGoldAccounts("""
        SELECT account, state FROM shop_accounts
        WHERE json_array_length(state, '$.GoldMarket.Fills') > 0
           OR EXISTS (
               SELECT 1 FROM json_each(state, '$.Orders') AS receipt
               WHERE json_extract(receipt.value, '$.Status') = 'completed'
                 AND (json_extract(receipt.value, '$.ProductId') LIKE 'exchange.buy.%'
                   OR json_extract(receipt.value, '$.ProductId') LIKE 'exchange.sell.%')
           )
        """);

    private IReadOnlyList<ShopAccount> ReadGoldAccounts(string query)
    {
        lock (_sync)
        {
            using var select = Command(query, null);
            using var reader = select.ExecuteReader();
            var accounts = new List<ShopAccount>();
            while (reader.Read())
            {
                var id = Guid.Parse(reader.GetString(0));
                var state = JsonSerializer.Deserialize<ShopAccount>(reader.GetString(1))
                    ?? throw new InvalidDataException($"Empty gold market state for {id}");
                if (state.Account != id) throw new InvalidDataException($"Gold market owner differs from {id}");
                accounts.Add(state);
            }
            return accounts;
        }
    }

    public ShopPremium? LoadPremium(Guid account, Guid character)
    {
        lock (_sync)
        {
            if (!_shopPremium.TryGetValue(account, out var premium))
            {
                premium = LoadShop(account)?.Premium ?? [];
                _shopPremium[account] = premium;
            }
            return premium.GetValueOrDefault(character);
        }
    }

    public ShopAccount? LoadShop(Guid account)
    {
        lock (_sync)
        {
            using var select = Command("SELECT state FROM shop_accounts WHERE account = $account", null,
                ("$account", account.ToString()));
            if (select.ExecuteScalar() is not string json) return null;
            var state = JsonSerializer.Deserialize<ShopAccount>(json)
                ?? throw new InvalidDataException($"Empty shop state for {account}");
            if (state.Account != account) throw new InvalidDataException($"Shop state owner differs from {account}");
            return state;
        }
    }

    public bool SaveShop(IReadOnlyList<ShopAccount> accounts, IReadOnlyList<Character> characters)
    {
        if (accounts.Select(a => a.Account).Distinct().Count() != accounts.Count
            || characters.Select(c => c.Id).Distinct().Count() != characters.Count)
            throw new ArgumentException("A shop transaction may include each account and character only once.");
        lock (_sync)
        {
            using var tx = _db.BeginTransaction();
            foreach (var state in accounts)
            {
                using (var exists = Command("SELECT 1 FROM accounts WHERE id = $account", tx, ("$account", state.Account.ToString())))
                    if (exists.ExecuteScalar() is null) return false;
                using var save = Command("""
                    INSERT INTO shop_accounts(account, state) VALUES ($account, $state)
                    ON CONFLICT(account) DO UPDATE SET state = excluded.state
                    """, tx, ("$account", state.Account.ToString()), ("$state", JsonSerializer.Serialize(state)));
                save.ExecuteNonQuery();
            }
            foreach (var character in characters)
            {
                using (var owner = Command("SELECT account FROM characters WHERE id = $id", tx, ("$id", character.Id.ToString())))
                    if (owner.ExecuteScalar() is not string id || id != character.Account.ToString()) return false;
                if (!Write(character, tx)) return false;
            }
            tx.Commit();
            foreach (var state in accounts) _shopPremium[state.Account] = new(state.Premium);
            return true;
        }
    }
}
