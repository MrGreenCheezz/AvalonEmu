using System.Globalization;
using System.Xml.Linq;
using AlbionLocal.Server.Data;
using AlbionLocal.Server.Entities;
using AlbionLocal.Server.Game;

namespace AlbionLocal.Server.Systems;

public sealed partial class Shop
{
    private sealed record GoldOrderRules(long BuyFee, long SellFee, HashSet<int> Hours);
    private GoldOrderRules? _goldOrderRules;
    private bool GoldOrdersEnabled => Enabled && Store!.SupportsGoldMarket;

    private GoldOrderRules OrderRules
    {
        get
        {
            if (_goldOrderRules is { } cached) return cached;
            // These are the installed client's own displayed setup fee and duration options, not a guessed market tax.
            using var xml = GameDataFile.Open(world.Config.ClientGameDataPath, "gamedata.bin").OpenXml();
            XDocument data;
            try { data = XDocument.Load(xml); }
            catch (System.Xml.XmlException e) { throw new InvalidDataException("Invalid gold market client XML", e); }
            var market = data.Descendants("GoldMarket").Single();
            long Fee(string name)
            {
                if (!decimal.TryParse((string?)market.Attribute(name), NumberStyles.Number, CultureInfo.InvariantCulture, out var value)
                    || value < 0 || value > long.MaxValue / 10000m)
                    throw new InvalidDataException($"Invalid GoldMarket {name}");
                var raw = value * 10000;
                if (raw != decimal.Truncate(raw)) throw new InvalidDataException($"Inexact GoldMarket {name}");
                return (long)raw;
            }
            var hours = data.Descendants("OrderDurations").Elements("Entry")
                .Select(e => int.TryParse((string?)e.Attribute("durationInHrs"), NumberStyles.Integer,
                    CultureInfo.InvariantCulture, out var h) ? h : 0).ToHashSet();
            var rules = new GoldOrderRules(Fee("setupbuyordercost"), Fee("setupsellordercost"), hours);
            if (rules.BuyFee < 0 || rules.SellFee < 0 || hours.Count == 0 || hours.Any(h => h is < 1 or > 8760))
                throw new InvalidDataException("Invalid gold order settings in installed client data");
            return _goldOrderRules = rules;
        }
    }

    // Local policy: limit orders match actual other-account orders only, best price then oldest first,
    // at the resting order's price. The existing fixed-price immediate dealer is a separate facility.
    // No purchase tax is charged (as for that dealer); GetInfos key12 and the confirmed buy quote both use zero.
    // We retain executions, not synthetic prices, and never cross an account's orders against itself.
    public GoldMarketResult CreateGoldOrder(Player player, bool buy, long gold, long price, int hours, long taxRate, string requestId)
        => CommitGoldMarket(player, book =>
        {
            var state = book.Account(player.Character.Account);
            if (book.Replay(state, requestId, "create", buy, player.Character.Id, gold, price, hours, taxRate) is { } replay) return replay;
            GoldMarketResult Remember(GoldMarketResult result) => book.Remember(state,
                new(requestId, "create", buy, player.Character.Id, gold, price, hours, taxRate, result));
            if (!ValidKey(requestId) || gold is <= 0 or > 10_000_000_000 || gold % 10000 != 0
                || price <= 0 || price > (long)int.MaxValue * 10000 || price % 10000 != 0 || taxRate != 0
                || !OrderRules.Hours.Contains(hours))
                return new(-500, "Cantidad, precio, comisión o duración de la orden no válidos.");
            if (state.GoldMarket.Orders.Any(o => o.Status == "open" && o.Buy == buy))
                return Remember(new((short)(buy ? 9881 : 9842), "Ya tienes una orden abierta de este tipo."));
            var c = book.Character(player.Character.Id, state.Account)
                ?? throw new InvalidOperationException("The order's character no longer belongs to its account.");
            var total = GoldSilver(gold, price);
            var fee = buy ? OrderRules.BuyFee : OrderRules.SellFee;
            if (buy && c.Silver < checked(total + fee)) return Remember(new(9880, "No tienes plata suficiente para la orden y su coste."));
            if (!buy && state.Gold < gold) return Remember(new(9840, "No tienes oro suficiente para la orden."));
            if (!buy && c.Silver < fee) return Remember(new(9841, "No tienes plata suficiente para el coste de publicación."));
            book.SpendSilver(state, c, checked(fee + (buy ? total : 0)));
            if (!buy) book.ChangeGold(state, -gold);
            var order = new GoldLimitOrder
            {
                Id = requestId, Account = state.Account, Character = c.Id, Buy = buy, Gold = gold,
                Remaining = gold, Price = price, SilverEscrow = buy ? total : 0, SetupFee = fee,
                CreatedAt = world.GameNow, ExpiresAt = checked(world.GameNow + TimeSpan.FromHours(hours).Ticks),
            };
            state.GoldMarket.Orders.Add(order);
            book.Changed.Add(state.Account);
            var executions = book.Match(order);
            var remaining = state.GoldMarket.Orders.Single(o => o.Id == requestId).Remaining;
            var executed = executions.Aggregate(0L, (sum, fill) => checked(sum + fill.Gold));
            var silver = executions.Aggregate(0L, (sum, fill) => checked(sum + fill.Silver));
            var average = executed == 0 ? 0 : checked((long)((decimal)silver * 10000 / executed));
            return Remember(new((short)(executed == 0 ? 0 : buy ? 9882 : 9843), "Orden guardada.",
                remaining, executed, silver, average, 0, fee));
        });

    public GoldMarketResult CancelGoldOrder(Player player, bool buy, string requestId) => CommitGoldMarket(player, book =>
    {
        var state = book.Account(player.Character.Account);
        if (book.Replay(state, requestId, "cancel", buy, player.Character.Id, 0, 0, 0, 0) is { } replay) return replay;
        if (!ValidKey(requestId)) return new(-500, "Identificador de operación no válido.");
        if (state.GoldMarket.Orders.FirstOrDefault(o => o.Status == "open" && o.Buy == buy) is { } order)
            book.Close(state, order, "cancelled");
        // A repeat cancel is successful without refunding twice. Remembering its key also prevents a delayed
        // cancel packet from cancelling a newer order on the same side.
        return book.Remember(state, new(requestId, "cancel", buy, player.Character.Id, 0, 0, 0, 0,
            new(0, "Orden cancelada; se ha devuelto el saldo pendiente. El coste de publicación no se devuelve.")));
    });

    public GoldMarketInfo GoldOrders(Player player)
    {
        // A poll must not enter SaveScheduler.Commit: that barrier flushes every dirty character even when
        // the book itself has nothing to save. Only this account's expiry/refund requires the write path.
        if (!GoldOrdersEnabled || player.Character.Transient || !Allowed(player.Character.Account))
            return new(-500, "El libro de órdenes no está disponible para esta cuenta.");
        try
        {
            var state = Store!.LoadShop(AccountId(player.Character.Account));
            if (state is null) return new(0, "");
            if (state.GoldMarket.PendingSilver == 0
                && !state.GoldMarket.Orders.Any(o => o.Status == "open" && o.ExpiresAt <= world.GameNow))
                return new(0, "", state.GoldMarket.Orders.FirstOrDefault(o => o.Status == "open" && o.Buy),
                    state.GoldMarket.Orders.FirstOrDefault(o => o.Status == "open" && !o.Buy));
        }
        catch (Exception e) when (e is IOException or InvalidOperationException or System.Data.Common.DbException)
        {
            return new(-500, "No se pudo consultar el libro de órdenes.");
        }
        GoldLimitOrder? buy = null, sell = null;
        var result = CommitGoldMarket(player, book =>
        {
            var state = book.Account(player.Character.Account);
            buy = state.GoldMarket.Orders.FirstOrDefault(o => o.Status == "open" && o.Buy);
            sell = state.GoldMarket.Orders.FirstOrDefault(o => o.Status == "open" && !o.Buy);
            return new();
        });
        return new(result.Code, result.Message, result.Success ? buy : null, result.Success ? sell : null);
    }

    /// <summary>All real completed local executions, independent of the requesting account. Existing durable
    /// receipts already commit with dealer balances; matching fills already commit on both book accounts.
    /// Project those records without another ledger write, fabricated counterparty, or a second count of a fill.</summary>
    public IReadOnlyList<GoldMarketExecution> GoldExecutions()
    {
        if (!Enabled) return [];
        var accounts = Store!.LoadGoldExecutionAccounts();
        var rows = new List<GoldMarketExecution>();
        foreach (var fill in accounts.SelectMany(s => s.GoldMarket.Fills).DistinctBy(f => f.Id, StringComparer.Ordinal))
            if (!string.IsNullOrEmpty(fill.Id) && fill.At > 0 && fill.Gold > 0 && fill.Silver > 0 && fill.Price > 0)
                rows.Add(new("orderbook:" + fill.Id, "orderbook", fill.Gold, fill.Silver, fill.Price, fill.At));
        foreach (var account in accounts)
            foreach (var order in account.Orders)
            {
                if (order.Status != "completed" || string.IsNullOrEmpty(order.Id)
                    || order.CompletedAt is not > 0 || order.ProductId is not { } product) continue;
                var buy = product.StartsWith("exchange.buy.", StringComparison.Ordinal) && order.Currency == "silver";
                var sell = product.StartsWith("exchange.sell.", StringComparison.Ordinal) && order.Currency == "gold";
                if (!buy && !sell) continue;
                var gold = buy ? order.Gold : order.Price;
                var silver = buy ? order.Price : order.Silver;
                if (gold <= 0 || silver <= 0) continue;
                var price = (decimal)silver * 10000 / gold;
                if (price <= 0 || price > long.MaxValue) continue;
                rows.Add(new($"dealer:{account.Account:N}:{order.Id}", "dealer", gold, silver, (long)price, order.CompletedAt.Value));
            }
        return rows.DistinctBy(r => r.Id, StringComparer.Ordinal).OrderBy(r => r.At).ThenBy(r => r.Id, StringComparer.Ordinal).ToArray();
    }

    private GoldMarketResult CommitGoldMarket(Player player, Func<GoldBook, GoldMarketResult> operation)
    {
        GoldBook? book = null;
        GoldMarketResult? reply = null;
        var committed = Attempt(() =>
        {
            if (!GoldOrdersEnabled || player.Character.Transient || !Allowed(player.Character.Account))
                return new(false, "El libro de órdenes no está disponible para esta cuenta.");
            book = new(this, world);
            var account = book.Account(player.Character.Account);
            if (book.Character(player.Character.Id, account.Account) is null)
                return new(false, "El personaje ya no pertenece a la cuenta.");
            book.Expire();
            book.ClaimOrphanedSilver(account, player.Character.Id);
            reply = operation(book);
            // Cancellation may discover that the funding character was deleted after the order was placed.
            // Deliver that account's refund to its current character in this same transaction, not on a later query.
            book.ClaimOrphanedSilver(account, player.Character.Id);
            book.Save();
            return new(true, reply.Message);
        });
        if (!committed.Success) return new(-500, committed.Message);
        // A failure while notifying a client must not report the committed transaction as an aborted debit.
        book!.Publish();
        return reply!;
    }

    private static long GoldSilver(long gold, long price) => checked((gold / 10000) * price);

    /// <summary>Detached working copies, used only inside the existing save barrier and one SaveShop transaction.</summary>
    private sealed class GoldBook(Shop shop, World world)
    {
        private readonly Dictionary<Guid, ShopAccount> _accounts = shop.Store!.LoadGoldMarketAccounts().ToDictionary(s => s.Account);
        private readonly Dictionary<Guid, Character> _characters = [];
        private readonly HashSet<Guid> _changedCharacters = [];
        private readonly HashSet<Guid> _changedGold = [];
        public HashSet<Guid> Changed { get; } = [];

        public ShopAccount Account(Guid id)
        {
            id = shop.AccountId(id);
            if (!_accounts.TryGetValue(id, out var state)) _accounts[id] = state = shop.State(id);
            return state;
        }

        public Character? Character(Guid id, Guid account)
        {
            if (_characters.TryGetValue(id, out var copy)) return shop.AccountId(copy.Account) == account ? copy : null;
            var source = shop.Live(id)?.Character ?? world.LoadCharacter(id);
            if (source is null || source.Transient || shop.AccountId(source.Account) != account) return null;
            return _characters[id] = source.Clone();
        }

        public GoldMarketResult? Replay(ShopAccount state, string id, string operation, bool buy, Guid character,
            long gold, long price, int hours, long taxRate)
        {
            var old = state.GoldMarket.Requests.FirstOrDefault(r => r.Id == id);
            if (old is null) return null;
            return old.Operation == operation && old.Buy == buy && old.Character == character && old.Gold == gold
                && old.Price == price && old.Hours == hours && old.TaxRate == taxRate ? old.Result
                : new(-500, "Este identificador ya corresponde a otra operación del mercado.");
        }

        public GoldMarketResult Remember(ShopAccount state, GoldMarketRequest request)
        {
            state.GoldMarket.Requests.Add(request);
            Changed.Add(state.Account);
            return request.Result;
        }

        public void SpendSilver(ShopAccount state, Character c, long amount)
        {
            if (amount < 0 || c.Silver < amount || shop.AccountId(c.Account) != state.Account)
                throw new InvalidOperationException("Invalid gold market silver debit");
            if (amount == 0) return;
            c.Silver = checked(c.Silver - amount);
            _changedCharacters.Add(c.Id);
            Changed.Add(state.Account);
        }

        public void ChangeGold(ShopAccount state, long delta)
        {
            if (delta == 0) return;
            var gold = checked(state.Gold + delta);
            if (gold < 0) throw new InvalidOperationException("Negative gold market balance");
            state.Gold = gold;
            _changedGold.Add(state.Account);
            Changed.Add(state.Account);
        }

        private void Credit(ShopAccount state, Guid character, long silver)
        {
            if (silver < 0) throw new InvalidDataException("Negative gold market credit");
            if (silver == 0) return;
            if (Character(character, state.Account) is { } c)
            {
                c.Silver = checked(c.Silver + silver);
                _changedCharacters.Add(c.Id);
            }
            else state.GoldMarket.PendingSilver = checked(state.GoldMarket.PendingSilver + silver);
            Changed.Add(state.Account);
        }

        public void ClaimOrphanedSilver(ShopAccount state, Guid character)
        {
            if (state.GoldMarket.PendingSilver <= 0) return;
            var c = Character(character, state.Account) ?? throw new InvalidOperationException("No character for gold market credit");
            c.Silver = checked(c.Silver + state.GoldMarket.PendingSilver);
            _changedCharacters.Add(c.Id);
            state.GoldMarket.PendingSilver = 0;
            Changed.Add(state.Account);
        }

        private void Replace(ShopAccount state, GoldLimitOrder order)
        {
            var index = state.GoldMarket.Orders.FindIndex(o => o.Id == order.Id);
            if (index < 0 || order.Account != state.Account) throw new InvalidDataException("Gold market order owner mismatch");
            state.GoldMarket.Orders[index] = order;
            Changed.Add(state.Account);
        }

        public void Close(ShopAccount state, GoldLimitOrder order, string status)
        {
            if (order.Status != "open") return;
            if (order.Buy) Credit(state, order.Character, order.SilverEscrow);
            else ChangeGold(state, order.Remaining);
            Replace(state, order with { Remaining = 0, SilverEscrow = 0, Status = status });
        }

        public void Expire()
        {
            foreach (var state in _accounts.Values.ToArray())
                foreach (var order in state.GoldMarket.Orders.Where(o => o.Status == "open" && o.ExpiresAt <= world.GameNow).ToArray())
                    Close(state, order, "expired");
        }

        public List<GoldMarketFill> Match(GoldLimitOrder incoming)
        {
            var fills = new List<GoldMarketFill>();
            var opposite = _accounts.Values.Where(s => s.Account != incoming.Account && shop.Allowed(s.Account))
                .SelectMany(s => s.GoldMarket.Orders)
                .Where(o => o.Status == "open" && o.Remaining > 0 && o.ExpiresAt > world.GameNow && o.Buy != incoming.Buy
                    && (incoming.Buy ? o.Price <= incoming.Price : o.Price >= incoming.Price))
                .OrderBy(o => incoming.Buy ? o.Price : -o.Price).ThenBy(o => o.CreatedAt).ThenBy(o => o.Id, StringComparer.Ordinal).ToArray();
            foreach (var resting in opposite)
            {
                if (incoming.Remaining == 0) break;
                var buy = incoming.Buy ? incoming : resting;
                var sell = incoming.Buy ? resting : incoming;
                var buyer = Account(buy.Account);
                var seller = Account(sell.Account);
                var gold = Math.Min(buy.Remaining, sell.Remaining);
                if (gold <= 0 || gold % 10000 != 0 || buy.Price < sell.Price || resting.Price <= 0)
                    throw new InvalidDataException("Invalid persisted gold order");
                var silver = GoldSilver(gold, resting.Price);
                var reserved = GoldSilver(gold, buy.Price);
                if (buy.SilverEscrow < reserved) throw new InvalidDataException("Gold order escrow is insufficient");
                ChangeGold(buyer, gold);
                Credit(buyer, buy.Character, checked(reserved - silver));
                Credit(seller, sell.Character, silver);
                buy = buy with { Remaining = buy.Remaining - gold, SilverEscrow = buy.SilverEscrow - reserved,
                    Status = buy.Remaining == gold ? "filled" : "open" };
                sell = sell with { Remaining = sell.Remaining - gold, Status = sell.Remaining == gold ? "filled" : "open" };
                Replace(buyer, buy);
                Replace(seller, sell);
                var fill = new GoldMarketFill(Guid.NewGuid().ToString("N"), buyer.Account, seller.Account,
                    buy.Id, sell.Id, gold, silver, resting.Price, world.GameNow);
                buyer.GoldMarket.Fills.Add(fill);
                seller.GoldMarket.Fills.Add(fill);
                fills.Add(fill);
                incoming = incoming.Buy ? buy : sell;
            }
            return fills;
        }

        public void Save()
        {
            if (Changed.Count == 0) return;
            var characters = _changedCharacters.Select(id => _characters[id]).ToArray();
            foreach (var c in characters) c.Gold = Account(c.Account).Gold;
            if (!shop.Store!.SaveShop(Changed.Select(id => _accounts[id]).ToArray(), characters))
                throw new IOException("gold market transaction refused");
        }

        public void Publish()
        {
            if (Changed.Count == 0) return;
            foreach (var id in _changedCharacters)
            {
                var c = _characters[id];
                if (shop.Live(c.Id) is { } p) { p.Character.Silver = c.Silver; p.Character.Gold = c.Gold; }
            }
            foreach (var id in _changedGold) shop.Publish(_accounts[id], []);
            foreach (var id in _changedCharacters)
                if (!_changedGold.Contains(shop.AccountId(_characters[id].Account)) && shop.Live(id) is { } p) Wallet.Send(p);
        }
    }
}
