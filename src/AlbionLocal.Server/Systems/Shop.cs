using System.Security.Cryptography;
using AlbionLocal.Photon;
using AlbionLocal.Server.Content;
using AlbionLocal.Server.Data;
using AlbionLocal.Server.Entities;
using AlbionLocal.Server.Game;
using AlbionLocal.Server.Persistence;
using AlbionLocal.Server.Protocol;

namespace AlbionLocal.Server.Systems;

/// <summary>Local simulated commerce. All public operations run under World.Sync. Durable state is authoritative; events follow the commit.</summary>
public sealed partial class Shop(World world)
{
    private ShopCatalog? _catalog;
    private ShopArtwork? _artwork;
    private readonly Dictionary<string, (Guid Account, Guid? Character, long Expires)> _tokens = new(StringComparer.Ordinal);
    private IShopStore? Store => world.Characters as IShopStore;
    public ShopCatalog Catalog => _catalog ??= ShopCatalog.Load(Path.Combine(world.Config.ContentPath, "shop.json"));
    public ShopArtwork Artwork => _artwork ??= ShopArtwork.Load(world.Config.ContentPath);
    public bool Enabled => Store is not null && Catalog.Enabled;
    public string BaseUrl { get; set; } = "";
    public long SilverPerGold => Catalog.SilverPerGold;
    public long PremiumGoldPrice(int days) => Catalog.PremiumGold.TryGetValue(days, out var n) ? checked(n * 10000) : 0;
    private Guid AccountId(Guid id) => id == Guid.Empty ? world.Characters.Account.Id : id;

    private ShopAccount State(Guid id)
    {
        id = AccountId(id);
        var state = Store?.LoadShop(id) ?? new ShopAccount { Account = id };
        if (!state.GoldInitialized)
        {
            var characters = world.Characters.List(id).Select(c => Live(c.Id)?.Character ?? c).ToArray();
            state.Gold = characters.Aggregate(0L, (sum, c) => checked(sum + Math.Max(0, c.Gold)));
            foreach (var c in characters) state.Unlocks.UnionWith(c.Vanity?.Unlocks ?? []);
            state.GoldInitialized = true;
            // Persist the one-time migration before any character receives the shared balance. Otherwise a later
            // legacy save could be summed a second time. Character snapshots cannot overwrite this separate row.
            if (Store is not null && world.Characters.FindAccount(id) is not null && !Store.SaveShop([state], []))
                throw new IOException("could not initialize account shop state");
        }
        return state;
    }

    public long Gold(Guid account) => State(account).Gold;
    public IReadOnlySet<short> Owned(Guid account) => State(account).Unlocks;
    public IReadOnlyList<ShopProduct> Products(Guid account) => Enabled ? Catalog.Products(world.Vanity.Catalog, world.Items) : [];
    public IReadOnlyList<ShopOrder> History(Guid account) => State(account).Orders.OrderByDescending(o => o.CreatedAt).ToArray();
    public long PremiumEnd(Character c) => c.Transient ? 0 : Store?.LoadPremium(AccountId(c.Account), c.Id)?.EndsAt ?? 0;
    public int PurchasedPremiumDays(Character c) => c.Transient ? 0 : Store?.LoadPremium(AccountId(c.Account), c.Id)?.PurchasedDays ?? 0;
    public void Synchronize(Character c) { if (Enabled && !c.Transient) c.Gold = Gold(c.Account); }
    private Player? Live(Guid character) => world.Maps.Maps.SelectMany(m => m.Players.Values).FirstOrDefault(p => p.Character.Id == character);

    private bool Allowed(Guid account) => Enabled && world.Characters.FindAccount(AccountId(account)) is { } a
        && !(a.BannedUntil is { } until && until.Ticks > world.GameNow);
    private static bool ValidKey(string key) => key.Length is >= 8 and <= 128 && key.All(c => char.IsAsciiLetterOrDigit(c) || c is '-' or '_' or '.' or ':');
    private void Write(ShopAccount state, params Character[] chars)
    {
        if (Store is null || !Store.SaveShop([state], chars)) throw new IOException("shop transaction refused");
    }
    private ShopResult Attempt(Func<ShopResult> operation)
    {
        try { return world.Saves.Commit(operation); }
        catch (Exception e) when (e is IOException or InvalidOperationException or OverflowException or System.Data.Common.DbException)
        {
            Log.Error("shop", $"transaction failed: {e.Message}");
            return new(false, "No se pudo guardar la compra. Puedes volver a consultar o confirmar el mismo pedido.");
        }
    }

    public ShopResult Prepare(Guid account, Guid character, string productId, string requestId)
    {
        var product = Products(account).FirstOrDefault(p => p.Id == productId);
        return product is null ? new(false, "La oferta no está disponible.") : PrepareProduct(account, character, product, requestId);
    }

    private ShopResult PrepareProduct(Guid account, Guid character, ShopProduct product, string requestId, long silver = 0) => Attempt(() =>
    {
        account = AccountId(account);
        if (!Allowed(account) || !ValidKey(requestId)) return new(false, "Cuenta o identificador de pedido no válido.");
        var c = world.LoadCharacter(character);
        if (c is null || AccountId(c.Account) != account || c.Transient) return new(false, "Elige un personaje de esta cuenta.");
        var state = State(account);
        if (state.Orders.FirstOrDefault(o => o.Id == requestId) is { } previous)
            return previous.ProductId == product.Id && previous.Character == character && previous.Price == product.Price && previous.Currency == product.Currency
                && previous.LearningPoints == product.LearningPoints && previous.Gold == product.Gold && previous.Silver == silver
                && previous.PremiumDays == product.PremiumDays && previous.Unlocks.SequenceEqual(product.Unlocks ?? [])
                && previous.Items.SequenceEqual(product.Items ?? [])
                ? new(previous.Status is "pending" or "completed", previous.Status, previous)
                : new(false, "Este identificador ya corresponde a otra compra.");
        if (product.Unlocks is { Length: > 0 } && product.Unlocks.Any(state.Unlocks.Contains))
            return new(false, "Ya tienes una o más apariencias de esta oferta. No se ha cobrado.");
        var order = new ShopOrder
        {
            Id = requestId, Account = account, Character = character, ProductId = product.Id, Title = product.Title,
            Currency = product.Currency, Price = product.Price, Gold = product.Gold, Silver = silver, PremiumDays = product.PremiumDays,
            LearningPoints = product.LearningPoints, Items = product.Items?.ToArray() ?? [],
            Unlocks = product.Unlocks?.ToArray() ?? [], CreatedAt = world.GameNow, ExpiresAt = checked(world.GameNow + TimeSpan.FromMinutes(15).Ticks),
        };
        state.Orders.Add(order);
        Write(state);
        return new(true, "Confirma tu compra.", order);
    });

    public ShopResult Confirm(Guid account, string orderId) => Attempt(() =>
    {
        account = AccountId(account);
        if (!Allowed(account)) return new(false, "La tienda no está disponible para esta cuenta.");
        var state = State(account);
        var index = state.Orders.FindIndex(o => o.Id == orderId);
        if (index < 0) return new(false, "Pedido no encontrado.");
        var order = state.Orders[index];
        if (order.Status == "completed") return new(true, "Compra completada.", order);
        if (order.Status != "pending") return new(false, order.Error ?? "El pedido ya no está pendiente.", order);
        ShopResult Refuse(string message)
        {
            var failed = order with { Status = "failed", Error = message };
            state.Orders[index] = failed;
            Write(state);
            return new(false, message, failed);
        }
        if (order.ExpiresAt <= world.GameNow) return Refuse("La oferta expiró. Solicita una nueva.");
        var live = Live(order.Character);
        var original = live?.Character ?? world.LoadCharacter(order.Character);
        if (original is null || AccountId(original.Account) != account) return Refuse("El personaje ya no pertenece a esta cuenta.");
        if (order.Unlocks.Any(id => state.Unlocks.Contains(id) || world.Vanity.Catalog.Find(id) is null))
            return Refuse("Una apariencia ya es tuya o no está disponible. No se ha cobrado.");
        if (order.Currency == "gold" && state.Gold < order.Price || order.Currency == "silver" && original.Silver < order.Price)
            return Refuse("Saldo insuficiente. No se ha cobrado.");
        var c = original.Clone();
        if (!PrepareItems(c, live, order.Items, out var delivery))
            return Refuse("No hay espacio para todos los objetos del paquete o alguno ya no está disponible. No se ha cobrado.");
        if (order.LearningPoints > 0 && !LearningPoints.TryPurchase(world, c, order.LearningPoints, world.GameNow,
                live?.PremiumOverride, order.PremiumDays > 0))
            return Refuse("El personaje no tiene espacio para todos los puntos de aprendizaje de este paquete. No se ha cobrado.");
        if (order.Currency == "gold") state.Gold = checked(state.Gold - order.Price);
        else if (order.Currency == "silver") c.Silver = checked(c.Silver - order.Price);
        else if (order.Currency != "cash") return Refuse("Moneda desconocida.");
        state.Gold = checked(state.Gold + order.Gold);
        c.Gold = state.Gold;
        c.Silver = checked(c.Silver + order.Silver);
        state.Unlocks.UnionWith(order.Unlocks);
        if (order.PremiumDays > 0)
        {
            Premium.PreparePurchase(world, c, world.GameNow);
            var before = state.Premium.GetValueOrDefault(c.Id);
            state.Premium[c.Id] = new(checked(Math.Max(world.GameNow, before?.EndsAt ?? 0) + TimeSpan.FromDays(order.PremiumDays).Ticks),
                checked((before?.PurchasedDays ?? 0) + order.PremiumDays));
        }
        var completed = order with { Status = "completed", CompletedAt = world.GameNow };
        state.Orders[index] = completed;
        Write(state, c);
        // Only publish changes after both receipt and entitlement have been committed.
        if (live is not null)
        {
            original.Silver = c.Silver; original.Gold = c.Gold;
            original.CraftingFocus = c.CraftingFocus; original.FocusStamp = c.FocusStamp;
            original.LearningPoints = c.LearningPoints; original.LpStamp = c.LpStamp;
            var publishItems = InstallItems(live, delivery);
            publishItems();
            if (order.PremiumDays > 0) Premium.Changed(live, world.GameNow);
            if (order.LearningPoints > 0)
                live.Session.Send(LearningPoints.Event(live, checked((long)order.LearningPoints * 10000), LearningPoints.Reward));
            Wallet.Send(live);
        }
        Publish(state, order.Unlocks);
        Log.Info("shop", $"local order {order.Id} completed: {order.ProductId}, account {account}");
        return new(true, "Compra completada.", completed);
    });

    public ShopResult Cancel(Guid account, string orderId) => Attempt(() =>
    {
        if (!Allowed(account)) return new(false, "Cuenta no disponible.");
        var s = State(account);
        var index = s.Orders.FindIndex(o => o.Id == orderId);
        if (index < 0) return new(false, "Pedido no encontrado.");
        var order = s.Orders[index];
        if (order.Status == "cancelled") return new(true, "Compra cancelada.", order);
        if (order.Status != "pending") return new(false, "La compra ya fue procesada.", order);
        order = order with { Status = "cancelled" };
        s.Orders[index] = order;
        Write(s);
        return new(true, "Compra cancelada. No se ha cobrado.", order);
    });

    public ShopResult BuyVanity(Player p, short type, short[] ids, long quotedPrice, string requestId)
    {
        if (ids.Length is < 1 or > 128 || ids.Distinct().Count() != ids.Length) return new(false, "Selección inválida.");
        var unlocks = ids.Select(id => world.Vanity.Catalog.Find(id)).ToArray();
        if (unlocks.Any(u => u is null || u.Free || (short)u.Type != type)) return new(false, "Apariencia no disponible.");
        long price;
        try { price = unlocks.Aggregate(0L, (sum, u) => checked(sum + Catalog.VanityPrice(u!))); }
        catch (OverflowException) { return new(false, "Importe inválido."); }
        if (quotedPrice != price) return new(false, "El precio ha cambiado.");
        var product = new ShopProduct("native.vanity." + string.Join("-", ids.Order()), "Apariencias", "vanity", "gold", price, Unlocks: ids);
        var result = PrepareProduct(p.Character.Account, p.Character.Id, product, requestId);
        return result.Success ? Confirm(p.Character.Account, requestId) : result;
    }

    public ShopResult BuyPremium(Player p, int days, string currency, long quotedPrice, string requestId)
    {
        var product = Products(p.Character.Account).FirstOrDefault(o => o.Id == $"premium.{days}.{currency}");
        if (product is null || product.Price != quotedPrice) return new(false, "La oferta de premium ha cambiado.");
        var prepared = Prepare(p.Character.Account, p.Character.Id, product.Id, requestId);
        return prepared.Success ? Confirm(p.Character.Account, requestId) : prepared;
    }

    public ShopResult ExchangeGold(Player p, bool buy, long goldRaw, long quotedSilver, string requestId)
    {
        if (goldRaw is <= 0 or > 10_000_000_000 || goldRaw % 10000 != 0) return new(false, "Cantidad de oro inválida.");
        var silver = checked(goldRaw * SilverPerGold);
        if (quotedSilver != silver) return new(false, "La cotización ha cambiado.");
        var product = new ShopProduct($"exchange.{(buy ? "buy" : "sell")}.{goldRaw}", buy ? "Compra de oro" : "Venta de oro", "exchange",
            buy ? "silver" : "gold", buy ? silver : goldRaw, Gold: buy ? goldRaw : 0);
        var prepared = PrepareProduct(p.Character.Account, p.Character.Id, product, requestId, buy ? 0 : silver);
        return prepared.Success ? Confirm(p.Character.Account, requestId) : prepared;
    }

    public bool GrantVanity(Character c, short id)
    {
        if (!HasOwnershipStore || c.Transient || world.Vanity.Catalog.Find(id) is null) return false;
        return Attempt(() =>
        {
            var state = State(c.Account).Clone();
            if (!state.Unlocks.Add(id)) return new(true, "owned");
            Write(state);
            return new(true, "granted");
        }).Success;
    }

    public bool RevokeVanity(Character c, VanityCatalog.Unlock unlock)
    {
        if (!HasOwnershipStore || c.Transient) return false;
        var copies = new List<Character>();
        var mountCategories = new Dictionary<Guid, int[]>();
        var result = Attempt(() =>
        {
            var state = State(c.Account).Clone();
            var removed = state.Unlocks.Remove(unlock.Id);
            foreach (var saved in world.Characters.List(state.Account))
            {
                var copy = Live(saved.Id) is { } live ? VanitySnapshot(live) : world.LoadCharacter(saved.Id)!.Clone();
                if (copy.Vanity is not { } v) continue;
                var changed = v.Unlocks.Remove(unlock.Id);
                for (var i = 0; i < v.Wardrobe.Length; i++)
                    if (unlock.Type == VanityCatalog.UnlockType.Skin && v.Wardrobe[i] == unlock.Number) { v.Wardrobe[i] = -1; changed = true; }
                var removedMounts = v.MountSkins.Where(x => unlock.Type == VanityCatalog.UnlockType.Mount && x.Value == unlock.Number).Select(x => x.Key).ToArray();
                if (removedMounts.Length > 0) mountCategories[copy.Id] = removedMounts;
                foreach (var key in removedMounts)
                { v.MountSkins.Remove(key); changed = true; }
                if (changed) copies.Add(copy);
                removed |= changed;
            }
            if (!removed) return new(false, "not owned");
            if (!Store!.SaveShop([state], copies)) throw new IOException("vanity revocation failed");
            return new(true, "revoked");
        });
        if (!result.Success) return false;
        // All live selections reflect the committed revocation before any observer packet can fail.
        foreach (var copy in copies)
            if (Live(copy.Id) is { } p) p.Character.Vanity = copy.Vanity;
        // 345319 97919E → AB6F90: key 1 false removes this id from the account cache and signals its listeners.
        PublishVanityUnlocks(c.Account, [unlock.Id], owned: false);
        foreach (var copy in copies)
            if (Live(copy.Id) is { } p)
            {
                p.SendAround(new UpdateWardrobeEvent { ddn = p.Id, d59 = Vanity.Wardrobe(p.Character) });
                // 345319 BCF50C/BCF517: category and skin (-1 clears). The same event updates the own
                // mount selection cache (AB497E) and the currently ridden appearance, without remounting.
                foreach (var category in mountCategories.GetValueOrDefault(copy.Id) ?? [])
                    p.SendAround(new ChangeMountSkinEvent { ddn = p.Id, bws = category, bwt = -1 });
            }
        return true;
    }

    public void SetGold(Player p, long raw)
    {
        if (!Enabled || p.Character.Transient) { p.Character.Gold = raw; return; }
        var result = Attempt(() => { var s = State(p.Character.Account); s.Gold = Math.Max(0, raw); Write(s); Publish(s, []); return new(true, "updated"); });
        if (!result.Success) throw new InvalidOperationException(result.Message);
    }

    public bool TransferGold(Player from, Player to, long raw)
    {
        if (!Enabled || from.Character.Transient || to.Character.Transient) return false;
        if (AccountId(from.Character.Account) == AccountId(to.Character.Account)) return raw >= 0 && Gold(from.Character.Account) >= raw;
        return Attempt(() =>
        {
            var a = State(from.Character.Account); var b = State(to.Character.Account);
            if (raw < 0 || a.Gold < raw) return new(false, "Saldo insuficiente.");
            a.Gold -= raw; b.Gold = checked(b.Gold + raw);
            if (!Store!.SaveShop([a, b], [])) throw new IOException("gold transfer refused");
            Publish(a, []); Publish(b, []);
            return new(true, "transferred");
        }).Success;
    }

    private void Publish(ShopAccount state, short[] ids)
    {
        foreach (var p in world.Maps.Maps.SelectMany(m => m.Players.Values).Where(p => AccountId(p.Character.Account) == state.Account && !p.Character.Transient))
        {
            p.Character.Gold = state.Gold;
            Wallet.Send(p);
            foreach (var id in ids) p.Session.Send(new UnlockVanityUnlockEvent { d2f = true, Extra = new() { [0] = id } });
        }
    }

    public string IssueLoginToken(Guid account, Guid? character)
    {
        if (!Allowed(account) || string.IsNullOrEmpty(BaseUrl)) return "";
        foreach (var old in _tokens.Where(t => t.Value.Expires <= world.GameNow).Select(t => t.Key).ToArray()) _tokens.Remove(old);
        var token = Convert.ToHexString(RandomNumberGenerator.GetBytes(32)).ToLowerInvariant();
        _tokens[token] = (AccountId(account), character, world.GameNow + TimeSpan.FromMinutes(2).Ticks);
        return token;
    }

    public (Guid Account, Guid? Character)? RedeemLoginToken(string token)
    {
        if (!_tokens.Remove(token, out var value) || value.Expires <= world.GameNow || !Allowed(value.Account)) return null;
        return (value.Account, value.Character);
    }
}
