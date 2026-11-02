using System.Globalization;
using AlbionLocal.Server.Game;
using AlbionLocal.Server.Protocol;
using AlbionLocal.Server.Systems;

namespace AlbionLocal.Server.Network;

/// <summary>Native shop operations. Money is a7m raw (10000 = one unit), including the quoted price per gold.</summary>
public sealed partial class WorldSession
{
    // Request counters restart on a new connection. Include the connection and operation in the durable receipt key.
    private readonly string _shopConnection = Guid.NewGuid().ToString("N");
    private int? _shopNavigationCategory;
    private string? ShopRequest(OperationCodes operation) => RequestId is { } id
        ? $"native:{_shopConnection}:{(short)operation}:{id.ToString(CultureInfo.InvariantCulture)}" : null;

    [Handles(OperationCodes.GetShopPurchaseUrl, Status = SessionStatus.InWorld)]
    private AlbionResponse GetShopPurchaseUrl(GetShopPurchaseUrlRequest req) =>
        NativeShopProtocol.PurchaseUrl(world, Player!.Character.Account, Player.Character.Id, req.oy);

    [Handles(OperationCodes.GetShopTilesForCategory, Status = SessionStatus.InWorld)]
    private AlbionResponse GetShopTilesForCategory(GetShopTilesForCategoryRequest req)
    {
        var tiles = NativeShopCatalogue.Tiles(world, Player!.Character.Account, req.o0);
        if (_shopNavigationCategory != req.o0)
        {
            _shopNavigationCategory = req.o0;
            foreach (var update in NativeShopCatalogue.NavigationUpdates(world, Player.Character.Account, req.o0)) SendEvent(update);
        }
        foreach (var tile in tiles) SendEvent(tile);
        return NativeShopCatalogue.Response(req.o0, tiles);
    }

    private void SendShopCatalogue()
    {
        if (Player is not { } p || string.IsNullOrEmpty(world.Shop.BaseUrl)) return;
        foreach (var message in NativeShopCatalogue.Initial(world, p.Character.Account)) SendEvent(message);
    }

    [Handles(OperationCodes.AccountSubscriptionInfo, Status = SessionStatus.InWorld)]
    private AlbionResponse AccountSubscriptionInfo(AccountSubscriptionInfoRequest req) =>
        NativeShopProtocol.Subscriptions(world, Player!.Character.Account);

    [Handles(OperationCodes.CharacterSubscriptionInfo, Status = SessionStatus.InWorld)]
    private AlbionResponse CharacterSubscriptionInfo(CharacterSubscriptionInfoRequest req) => NativeShopProtocol.NoSubscription();

    [Handles(OperationCodes.BuyPremium, Status = SessionStatus.InWorld)]
    private AlbionResponse BuyPremium(BuyPremiumRequest req)
    {
        if (Player is not { } p || ShopRequest(OperationCodes.BuyPremium) is not { } nonce)
            return new BuyPremiumResponse { ReturnCode = 10522 };
        // 345319: Gold confirmation16014F8 and Silver confirmation1601772 call9CC4A0(days,gold,silver).
        // SenderA65C1A–24 writes key0=days,key1=the silver quote (0 when paying gold),key2=the package gold price.
        // PremiumPackages.days/goldcost in gamedata.bin define the native selector (30/90/180/360 days).
        var gold = world.Shop.PremiumGoldPrice(req.dn);
        if (gold <= 0 || req.dq != gold || req.dp < 0)
        {
            Expeditions.Tell(p, "La oferta de Premium ha cambiado. Vuelve a abrir la selección.");
            return new BuyPremiumResponse { ReturnCode = 10522 };
        }
        var silver = req.dp > 0;
        var result = world.Shop.BuyPremium(p, req.dn, silver ? "silver" : "gold", silver ? req.dp : req.dq, nonce);
        short code = 0;
        if (!result.Success)
        {
            // 345319's gold callback handles 10520 only; its silver callback handles 10521/10522.
            // Confirmed at RVA 1601230 and 1601835. A blanket 10522 silently hid failed gold payments.
            var insufficient = result.Order is { Status: "failed" } order &&
                (order.Currency == "silver" && p.Character.Silver < order.Price ||
                 order.Currency == "gold" && world.Shop.Gold(p.Character.Account) < order.Price);
            code = insufficient ? (short)(silver ? 10521 : 10520) : (short)10522;
            if (!insufficient) Expeditions.Tell(p, result.Message);
        }
        return new BuyPremiumResponse
        {
            ReturnCode = code,
            DebugMessage = result.Success ? null : result.Message,
            ani = result.Success ? result.Order?.Price ?? 0 : 0,
        };
    }

    [Handles(OperationCodes.GoldMarketGetBuyOffer, Status = SessionStatus.InWorld)]
    private AlbionResponse GoldMarketGetBuyOffer(GoldMarketGetBuyOfferRequest req)
    {
        var quote = GoldQuote(req.pk);
        return new GoldMarketGetBuyOfferResponse { a5v = quote.Total, a5w = quote.Rate };
    }

    [Handles(OperationCodes.GoldMarketGetSellOffer, Status = SessionStatus.InWorld)]
    private AlbionResponse GoldMarketGetSellOffer(GoldMarketGetSellOfferRequest req)
    {
        var quote = GoldQuote(req.pm);
        return new GoldMarketGetSellOfferResponse { a6c = quote.Total, a6d = quote.Rate };
    }

    [Handles(OperationCodes.GoldMarketGetBuyOfferFromSilver, Status = SessionStatus.InWorld)]
    private AlbionResponse GoldMarketGetBuyOfferFromSilver(GoldMarketGetBuyOfferFromSilverRequest req)
    {
        var quote = GoldFromSilver(req.pl, roundUp: false);
        return new GoldMarketGetBuyOfferFromSilverResponse { a5t = quote.Gold, a5u = quote.Rate };
    }

    [Handles(OperationCodes.GoldMarketGetSellOfferFromSilver, Status = SessionStatus.InWorld)]
    private AlbionResponse GoldMarketGetSellOfferFromSilver(GoldMarketGetSellOfferFromSilverRequest req)
    {
        var quote = GoldFromSilver(req.pn, roundUp: true);
        return new GoldMarketGetSellOfferFromSilverResponse { a6a = quote.Gold, a6b = quote.Rate };
    }

    private (long Total, long Rate) GoldQuote(long raw)
    {
        // The native view requests amount0 when it opens, and still displays response key1 as Current Price
        // (GetGoldView.HandleGoldMarketGetBuyOfferResponse, 345319 RVA10CD269). Quoting zero is not a trade.
        if (!world.Shop.Enabled || world.Shop.SilverPerGold <= 0 || raw is < 0 or > 10_000_000_000 || raw % 10000 != 0) return (0, 0);
        try { return (checked(raw * world.Shop.SilverPerGold), checked(world.Shop.SilverPerGold * 10000)); }
        catch (OverflowException) { return (0, 0); }
    }

    private (long Gold, long Rate) GoldFromSilver(long silver, bool roundUp)
    {
        if (!world.Shop.Enabled || silver < 0 || world.Shop.SilverPerGold <= 0) return (0, 0);
        try
        {
            var price = checked(world.Shop.SilverPerGold * 10000);
            var units = silver / price + (roundUp && silver % price != 0 ? 1 : 0);
            var raw = checked(units * 10000);
            return GoldQuote(raw).Rate is > 0 ? (raw, price) : (0, 0);
        }
        catch (OverflowException) { return (0, 0); }
    }

    [Handles(OperationCodes.GoldMarketBuyGold, Status = SessionStatus.InWorld)]
    private AlbionResponse GoldMarketBuyGold(GoldMarketBuyGoldRequest req)
    {
        var quote = GoldQuote(req.o8);
        if (Player is not { } p || ShopRequest(OperationCodes.GoldMarketBuyGold) is not { } nonce || req.o8 <= 0 || quote.Rate == 0 || req.o9 != quote.Total)
            return new GoldMarketBuyGoldResponse { ReturnCode = 9760 };
        // The local dealer charges the exact server quote and no commission; never spend a client-proposed extra amount.
        var result = world.Shop.ExchangeGold(p, buy: true, req.o8, req.o9, nonce);
        var insufficient = !result.Success && result.Order is { Status: "failed", Currency: "silver" } order
            && p.Character.Silver < order.Price;
        if (!result.Success && !insufficient) Expeditions.Tell(p, result.Message);
        return new GoldMarketBuyGoldResponse
        {
            ReturnCode = result.Success ? (short)0 : insufficient ? (short)9761 : (short)-500,
            DebugMessage = result.Success ? null : result.Message,
            a5d = result.Success ? req.o8 : 0, a5e = result.Success ? quote.Total : 0,
            a5f = result.Success ? quote.Rate : 0, a5g = 0,
        };
    }

    [Handles(OperationCodes.GoldMarketSellGold, Status = SessionStatus.InWorld)]
    private AlbionResponse GoldMarketSellGold(GoldMarketSellGoldRequest req)
    {
        var quote = GoldQuote(req.po);
        if (Player is not { } p || ShopRequest(OperationCodes.GoldMarketSellGold) is not { } nonce || req.po <= 0 || quote.Rate == 0 || req.pp != quote.Total)
            return new GoldMarketSellGoldResponse { ReturnCode = 9800 };
        var result = world.Shop.ExchangeGold(p, buy: false, req.po, req.pp, nonce);
        var insufficient = !result.Success && result.Order is { Status: "failed", Currency: "gold" } order
            && world.Shop.Gold(p.Character.Account) < order.Price;
        if (!result.Success && !insufficient) Expeditions.Tell(p, result.Message);
        return new GoldMarketSellGoldResponse
        {
            // GetGoldView.d.d RVA10E25DD–260F: 9800 stale offer,9801 insufficient gold,9802 limit,-500 generic.
            ReturnCode = result.Success ? (short)0 : insufficient ? (short)9801 : (short)-500,
            DebugMessage = result.Success ? null : result.Message,
            a6e = result.Success ? req.po : 0, a6f = result.Success ? quote.Total : 0,
            a6g = result.Success ? quote.Rate : 0, a6h = 0,
        };
    }

    [Handles(OperationCodes.GoldMarketGetInfos, Status = SessionStatus.InWorld)]
    private AlbionResponse GoldMarketGetInfos(GoldMarketGetInfosRequest req)
    {
        var info = world.Shop.GoldOrders(Player!);
        // 345319 del.x callback RVA AE1400 copies response keys4..11 into model+30..68.
        // OrderView.OnGoldMarketInfosUpdate RVA10DD830 consumes sell(amount,price,end), then
        // buy(amount,purchaseFee,purchaseFeeRate,price,end). Key12 is the current purchase fee rate.
        // The unverified/global summary keys0..3 stay unknown (zero); they are not invented liquidity.
        return new GoldMarketGetInfosResponse
        {
            ReturnCode = info.Code, DebugMessage = info.Code == 0 ? null : info.Message,
            a51 = info.Sell?.Remaining ?? 0, a52 = info.Sell?.Price ?? 0, a53 = info.Sell?.ExpiresAt ?? 0,
            a54 = info.Buy?.Remaining ?? 0, a55 = 0, a56 = 0, a57 = info.Buy?.Price ?? 0,
            a58 = info.Buy?.ExpiresAt ?? 0, a59 = 0,
        };
    }

    [Handles(OperationCodes.GoldMarketGetAverageInfo, Status = SessionStatus.InWorld)]
    private AlbionResponse GoldMarketGetAverageInfo(GoldMarketGetAverageInfoRequest req)
    {
        // 345319 del.a.c RVAAD7310 reads parallel key0 a7m raw prices/key1 GameTimeStamp, then filters the
        // requested pj window (1/7/28 days). The shared market uses actual completed dealer receipts and book fills
        // from all local accounts, deduplicated by the system; never seed points before a real local execution.
        var points = world.Shop.GoldExecutions()
            .Where(f => f.At > 0 && f.At <= world.GameNow && f.Price > 0 && f.Gold > 0 && f.Silver > 0)
            .OrderBy(f => f.At).TakeLast(512).ToArray();
        return new GoldMarketGetAverageInfoResponse { a5r = points.Select(r => r.Price).ToArray(), a5s = points.Select(r => r.At).ToArray() };
    }

    [Handles(OperationCodes.GoldMarketCreateBuyOrder, Status = SessionStatus.InWorld)]
    private AlbionResponse GoldMarketCreateBuyOrder(GoldMarketCreateBuyOrderRequest req)
    {
        // 345319 sender A6C1E0 (confirmation closure13ED8B0): key0 unit price,1 gold,2 accepted tax rate,3 hours.
        // Callback13DB9C0: 0=pending,9880=insufficient silver,9881=existing,9882=partial/immediate execution.
        var result = ShopRequest(OperationCodes.GoldMarketCreateBuyOrder) is { } nonce
            ? world.Shop.CreateGoldOrder(Player!, true, req.pd, req.pc, req.pf, req.pe, nonce)
            : new GoldMarketResult(-500, "Falta el identificador de la solicitud.");
        return new GoldMarketCreateBuyOrderResponse
        {
            ReturnCode = result.Code, DebugMessage = result.Success ? null : result.Message,
            a5h = result.Remaining, a5i = result.Executed, a5j = result.AveragePrice,
            a5k = result.TaxRate, a5l = result.Silver,
        };
    }

    [Handles(OperationCodes.GoldMarketCreateSellOrder, Status = SessionStatus.InWorld)]
    private AlbionResponse GoldMarketCreateSellOrder(GoldMarketCreateSellOrderRequest req)
    {
        // 345319 sender A6A670, called13DCDC3: key0 unit price,1 gold,2 hours (all money is raw a7m).
        // Callback13DD220: 9840=insufficient gold,9841=setup fee,9842=existing,9843=execution,9844=daily limit.
        var result = ShopRequest(OperationCodes.GoldMarketCreateSellOrder) is { } nonce
            ? world.Shop.CreateGoldOrder(Player!, false, req.ph, req.pg, req.pi, 0, nonce)
            : new GoldMarketResult(-500, "Falta el identificador de la solicitud.");
        return new GoldMarketCreateSellOrderResponse
        {
            ReturnCode = result.Code, DebugMessage = result.Success ? null : result.Message,
            a5m = result.Remaining, a5n = result.Executed, a5o = result.AveragePrice, a5p = result.Silver, a5q = 0,
        };
    }

    [Handles(OperationCodes.GoldMarketCancelOrder, Status = SessionStatus.InWorld)]
    private AlbionResponse GoldMarketCancelOrder(GoldMarketCancelOrderRequest req)
    {
        // 345319 CancelSellOrder10D2070 ->10E1A40 ->AE0780 sends true; CancelBuyOrder10D1F10
        // ->10E1CB0 ->AE0880 sends false. A7A2A0 writes that side flag to key0 (not an order ID).
        var result = ShopRequest(OperationCodes.GoldMarketCancelOrder) is { } nonce
            ? world.Shop.CancelGoldOrder(Player!, buy: !req.pb, nonce)
            : new GoldMarketResult(-500, "Falta el identificador de la solicitud.");
        return new GenericResponse { ReturnCode = result.Code, DebugMessage = result.Success ? null : result.Message };
    }
}

internal static class NativeShopProtocol
{
    internal static GetShopPurchaseUrlResponse PurchaseUrl(World world, Guid account, Guid? character, int tile = 0)
    {
        if (!world.Shop.Enabled || !Uri.TryCreate(world.Shop.BaseUrl, UriKind.Absolute, out var uri)
            || uri.Scheme != Uri.UriSchemeHttp || !uri.IsLoopback)
            return new GetShopPurchaseUrlResponse { ReturnCode = 1, a4j = "", DebugMessage = "La tienda local no está disponible." };
        var product = tile == 0 ? null : NativeShopCatalogue.Product(world, account, tile);
        if (tile != 0 && product is null) return new GetShopPurchaseUrlResponse { ReturnCode = 1, a4j = "", DebugMessage = "La oferta ya no está disponible." };
        var token = world.Shop.IssueLoginToken(account, character);
        if (string.IsNullOrEmpty(token)) return new GetShopPurchaseUrlResponse { ReturnCode = 1, a4j = "" };
        return new GetShopPurchaseUrlResponse { a4j = world.Shop.BaseUrl.TrimEnd('/') + "?token=" + Uri.EscapeDataString(token)
            + (product is null ? "" : "&product=" + Uri.EscapeDataString(product.Id)) };
    }

    internal static AccountSubscriptionInfoResponse Subscriptions(World world, Guid account)
    {
        var characters = world.Characters.List(account);
        return new AccountSubscriptionInfoResponse
        {
            ajg = characters.Select(c => c.Id).ToArray(), ajh = new bool[characters.Count], aji = new int[characters.Count],
        };
    }

    // Premium time is purchased once. No recurring billing agreement exists, including while that Premium time is active.
    internal static CharacterSubscriptionInfoResponse NoSubscription() => new() { aoc = false, aod = 0, aoe = "", aof = 0, aog = 0 };
}
