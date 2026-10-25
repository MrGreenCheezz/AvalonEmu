using System.Globalization;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using AlbionLocal.Photon;
using AlbionLocal.Server.Data;
using AlbionLocal.Server.Entities;
using AlbionLocal.Server.Game;
using AlbionLocal.Server.Persistence;
using AlbionLocal.Server.Protocol;
using AlbionLocal.Server.Systems;

namespace AlbionLocal.Server.Network;

/// <summary>
/// The marketplace (stage 6 SPEC §6d): player orders only, one market per city (the marketplace's cluster), kept in
/// <see cref="World.Market"/>. A sell order ("offer") takes its items out of the inventory, a buy order ("request") its
/// silver; buying from an offer pays the seller at once (an offline seller at its next Join) and leaves the items as a
/// finished entry for the buyer, selling into a request pays the seller from the escrow and leaves the items for the
/// request's owner; finished entries are collected with AuctionFetchAuction / AuctionFetchFinishedAuctions. Orders travel as
/// JSON strings of the client's AuctionDTO (<see cref="Dto"/>, JsonFx into Albion.Common.Auction.AuctionDTO). Fees as the
/// client shows them (gamedata.xml MarketPlace, <see cref="Pricing.MarketSetupFee"/> / <see cref="Pricing.MarketTax"/>): each
/// new order pays a setup fee (2.5 %, at least 1 silver, kept when it is aborted), each sale leaves the seller the total less
/// the sales tax (8 %, halved while the seller is premium: <see cref="Premium.TaxFactor"/>); no distance fee (one market per city). Orders last the client's key 4 hours (one
/// of gamedata's OrderDurations, else defaultexpiration, 720); an expired order becomes a finished entry giving back its
/// items or silver when a list reads it or the minute's sweep finds it (<see cref="MarketExpiry"/>). A player's order that is filled,
/// expires or is aborted mails its owner the client's SUMMARY notice (<see cref="Mailer.MarketNotice"/>, stageJ mail SPEC §3). The lists filter and page as <see cref="MarketFilter"/>. Every answer has RC 0 officially; a refusal
/// here is RC <see cref="MarketRefused"/>. The journal merchant's standing orders (<see cref="JournalMerchant"/>, stageH journals) list,
/// sell and buy as the players' orders, without a store row and without an owner to pay; so do the Black Market buyer's
/// (<see cref="BlackMarket"/>, stageH blackmarket SPEC) at the Black Market building, where players place no orders.
/// </summary>
public sealed partial class WorldSession
{
    /// <summary>The RC of a refused market request (not traced which texts the client has; 3526 is special in two handlers, Q-D2).</summary>
    public const short MarketRefused = 1;

    /// <summary>Orders per list answer when the request names none (the client asks for 50, key 12), and the most it gets.</summary>
    public const int MarketPage = 50, MarketMaxPage = 200;

    /// <summary>
    /// How long an order of the client's key 4 (hours) stays open: one of gamedata's OrderDurations (12, 24, 168, 720 h, the
    /// client's dropdown); a missing or unlisted key 4 gets defaultexpiration (720 h).
    /// </summary>
    private TimeSpan OrderTime(int hours) =>
        TimeSpan.FromHours(world.Economy.MarketOrderDurations.Contains(hours) ? hours : Math.Max(1, world.Economy.MarketOrderHours));

    /// <summary>
    /// The marketplace of a request: the cluster of the player's map when key 0 is 0 (tolerated) or a marketplace building
    /// there, the Black Market's own location (<see cref="BlackMarket.Location"/>) for its building (stageH blackmarket SPEC §2:
    /// the same window and ops); null = refused (another object, or no market store).
    /// </summary>
    private string? MarketAt(Player player, long building)
    {
        if (world.Market is null || player.Map is not { } map) return null;
        if (building == 0) return map.TutorialOwner is null ? map.Cluster : map.InstanceKey;
        if (!map.Objects.TryGetValue(building, out var o) || o is not Building b) return null;
        return b.Template.Kind switch { "marketplacebuilding" => map.TutorialOwner is null ? map.Cluster : map.InstanceKey, "blackmarketbuilding" => BlackMarket.Location(map.Cluster), _ => null };
    }

    private AlbionResponse? RefuseMarket(Player player, OperationCodes op, string why, AlbionResponse? answer = null)
    {
        Log.Info(Name, $"'{player.Character.Name}' {op} refused: {why}");
        var r = answer ?? new GenericResponse();
        r.ReturnCode = MarketRefused;
        return r;
    }

    /// <summary>
    /// AuctionGetOffers (op 81, aj2): the open sell orders of the city that pass the filter (keys 1-4 shop categories, 5 quality
    /// and 6 its mode, 7 tier, 8 item type ids, 10 enchantment and 9 its mode, 15 the sort; 11 distance, 14 and 16 not used),
    /// the page of key 12 orders from offset key 13; {0 the page, 1 more after it}.
    /// </summary>
    [Handles(OperationCodes.AuctionGetOffers, Status = SessionStatus.InWorld)]
    private AlbionResponse? AuctionGetOffers(AuctionGetOffersRequest req)
    {
        if (Player is not { } player) return null;
        var filter = new MarketFilter([req.bpe, req.bpf, req.bpg, req.bph], req.bpi, req.bpj, req.bpk, req.bpn, req.bpm, req.bpl, req.bps, req.bpp, req.bpq);
        var (page, more) = OpenOrders(player, req.bqs, MarketOrder.Offer, filter);
        return new AuctionGetOffersResponse { am5 = page, am6 = more };
    }

    /// <summary>
    /// AuctionGetRequests (op 82, aj3): the open buy orders of the city that pass the filter (keys 1-4 shop categories, 5
    /// quality and 6 its mode, 7 tier, 8 item type ids, 9 enchantment and 10 its mode, 11 the sort), the page of key 12 orders
    /// from offset key 13; {0 the page, 1 more after it}.
    /// </summary>
    [Handles(OperationCodes.AuctionGetRequests, Status = SessionStatus.InWorld)]
    private AlbionResponse? AuctionGetRequests(AuctionGetRequestsRequest req)
    {
        if (Player is not { } player) return null;
        var filter = new MarketFilter([req.bpu, req.bpv, req.bpw, req.bpx], req.bpy, req.bpz, req.bp0, req.bp2, req.bp3, req.bp1, req.bp4, req.bp5, req.bp6);
        var (page, more) = OpenOrders(player, req.bqs, MarketOrder.Request, filter);
        return new AuctionGetRequestsResponse { am7 = page, am8 = more };
    }

    /// <summary>
    /// The list filter of AuctionGetOffers / AuctionGetRequests (Q-D1; the senders c32.i 0x180A73AB0 / 0x180A6E430 called by
    /// MarketPlaceBuyView.b.g 0x180FCA630 with MarketPlaceHeaderView.GetCurrentHeaderFilterSettings 0x180FBEC30, and by
    /// MarketPlacePopUpGui.QuerySellOrders 0x180FDA690 / QueryBuyOrders 0x180FD9F20). Every string is "" for "all":
    /// <see cref="Categories"/> = the category dropdown's four strings (gat.a 0x181C8A030: the shop category ids of the chosen
    /// entry and its parents), matched against the item's shopcategory and shopsubcategory1-3; <see cref="Quality"/> and
    /// <see cref="Tier"/> = the dropdown index as a number (quality 1-5, tier 1-8), <see cref="Enchantment"/> = index - 1 (0-4).
    /// A mode (cfb) compares the order's value with the filter's: 0 equal, 1 at most, 2 at least (inferred: the buy view
    /// sends 0 and 0, the item popup 2 for the quality of the sell orders it lists and 1 for the buy orders an item can
    /// fill). <see cref="ItemTypes"/>: the item type ids the client found for a search text (only with one). <see cref="Sort"/>
    /// (cfc, ButtonSortingModule.CurrentSorting 0x180FB0C20): 0/1 price up/down, 3/4 time up/down, 5/6 item up/down (by
    /// uniquename: the server has no localized names, so this differs from the list's shown names), 8/9 distance, 19 none; anything else lists offers cheapest and requests dearest first. <see cref="PageSize"/> 50 and
    /// <see cref="Offset"/> = page × 50 (MarketPlaceBuyView.b.g).
    /// </summary>
    internal sealed record MarketFilter(string?[] Categories, string? Quality, int QualityMode, string? Tier, string? Enchantment, int EnchantmentMode,
        short[]? ItemTypes, int Sort, int PageSize, int Offset);

    /// <summary>The shop category ids of items.xml (every level), to leave out a filter string that is none of them.</summary>
    private static readonly ConditionalWeakTable<ItemStore, HashSet<string>> ShopCategories = new();

    private static readonly string[] ShopCategoryLevels = ["shopcategory", "shopsubcategory1", "shopsubcategory2", "shopsubcategory3"];

    /// <summary>
    /// The open orders of a type at the request's market that pass <paramref name="filter"/>, sorted, as DTOs of one page
    /// (the expired ones turned into finished entries first); orders of item types the client does not know are left out.
    /// </summary>
    private (string[] Page, bool More) OpenOrders(Player player, long building, string type, MarketFilter filter)
    {
        if (MarketAt(player, building) is not { } location) return ([], false);
        var known = ShopCategories.GetValue(world.Items, items => items.All
            .SelectMany(t => ShopCategoryLevels.Select(l => t.Attrs.GetValueOrDefault(l))).OfType<string>().ToHashSet(StringComparer.OrdinalIgnoreCase));
        var categories = filter.Categories.Where(c => !string.IsNullOrEmpty(c) && known.Contains(c)).ToList();
        var types = filter.ItemTypes is { Length: > 0 } ? filter.ItemTypes.Select(t => (int)t).ToHashSet() : null;
        static int? Number(string? s) => int.TryParse(s, NumberStyles.Integer, CultureInfo.InvariantCulture, out var n) ? n : null;
        static bool Compare(int value, int? wanted, int mode) =>
            wanted is not { } w || (mode switch { 1 => value <= w, 2 => value >= w, _ => value == w });
        var (quality, tier, enchantment) = (Number(filter.Quality), Number(filter.Tier), Number(filter.Enchantment));
        var open = Expire(world.Market!.Orders(location, type)).Concat(JournalMerchant.Orders(world, location, type))
            .Concat(TutorialMerchant.Orders(player, location, type))
            .Concat(BlackMarket.Orders(world, player.Character, location, type))
            .Select(o => (Order: o, Item: world.Items.Find(o.ItemName)))
            .Where(x => x.Item is { } t && (types is null || types.Contains(t.Index))
                && categories.All(c => ShopCategoryLevels.Any(l => string.Equals(t.Attrs.GetValueOrDefault(l), c, StringComparison.OrdinalIgnoreCase)))
                && Compare(x.Order.Quality, quality, filter.QualityMode) && Compare(t.Tier, tier, 0) && Compare(t.Enchantment, enchantment, filter.EnchantmentMode))
            .Select(x => x.Order);
        var sorted = filter.Sort switch
        {
            0 => open.OrderBy(o => o.UnitPrice),
            1 => open.OrderByDescending(o => o.UnitPrice),
            3 => open.OrderBy(o => o.Expires),
            4 => open.OrderByDescending(o => o.Expires),
            5 => open.OrderBy(o => o.ItemName, StringComparer.OrdinalIgnoreCase),
            6 => open.OrderByDescending(o => o.ItemName, StringComparer.OrdinalIgnoreCase),
            _ => type == MarketOrder.Offer ? open.OrderBy(o => o.UnitPrice) : open.OrderByDescending(o => o.UnitPrice),
        };
        var list = sorted.ThenBy(o => o.Id).ToList();
        var size = filter.PageSize <= 0 ? MarketPage : Math.Min(filter.PageSize, MarketMaxPage);
        var offset = Math.Max(0, filter.Offset);
        return ([.. list.Skip(offset).Take(size).Select(o => Dto(o, finished: false))], list.Count > offset + size);
    }

    /// <summary>The orders of <paramref name="orders"/> that are still open; each expired one is ended (<see cref="MarketExpiry"/>: a finished entry and its owner's mail).</summary>
    private List<MarketOrder> Expire(IEnumerable<MarketOrder> orders) => MarketExpiry.Expire(world, orders);

    /// <summary>
    /// AuctionGetItemAverageStats (op 95, ajx: 0 the market object, 1 item type id, 2 quality, 3 the span ajx.a: 0 24 hours, 1 7 days,
    /// 2 4 weeks; sent by MarketPlacePopUpGui.QueryItemAverageStats 0x180FDA250 through dgi.b 0x180B231F0): the sales of the item at
    /// this city's market (econ SPEC §1), summed per bucket of the chart: an hour (24 h), a quarter day (7 days) or a day (4 weeks).
    /// The response (adw, read by MarketPlacePopUpDetailsGraphModule.OnDemandItemAveragesReceived 0x180FCF820) holds parallel arrays:
    /// 0 the amounts sold, 1 the silver paid (a7m raw; the client shows silver ÷ amount), 2 the bucket's GameTimeStamp (its start),
    /// newest first; no sales = empty arrays (the client's "no average").
    /// </summary>
    [Handles(OperationCodes.AuctionGetItemAverageStats, Status = SessionStatus.InWorld)]
    private AlbionResponse? AuctionGetItemAverageStats(AuctionGetItemAverageStatsRequest req)
    {
        if (Player is not { } player) return null;
        var (amounts, silver, stamps) = (new List<long>(), new List<long>(), new List<long>());
        if (MarketAt(player, req.bqs) is { } location && world.Items[req.bor] is { } type)
        {
            var (span, bucket) = HistorySpan(req.bot);
            var now = DateTime.UtcNow;
            foreach (var g in world.Market!.Sales(location, type.Name, req.bos, now - span)
                         .GroupBy(s => s.At.Ticks - s.At.Ticks % bucket.Ticks).OrderByDescending(g => g.Key))
            {
                amounts.Add(g.Sum(s => (long)s.Amount));
                silver.Add(g.Sum(s => s.UnitPrice * s.Amount));
                stamps.Add(g.Key);
            }
        }
        return new AuctionGetItemAverageStatsResponse { amw = [.. amounts], amx = [.. silver], amy = [.. stamps] };
    }

    /// <summary>The chart's span of ajx.a (0 24 hours, 1 7 days, 2 4 weeks, as GraphModule's 1/7/28 days) and the bucket it plots by.</summary>
    private static (TimeSpan Span, TimeSpan Bucket) HistorySpan(byte kind) => kind switch
    {
        1 => (TimeSpan.FromDays(7), TimeSpan.FromHours(6)),
        2 => (TimeSpan.FromDays(28), TimeSpan.FromDays(1)),
        _ => (TimeSpan.FromHours(24), TimeSpan.FromHours(1)),
    };

    /// <summary>
    /// AuctionGetItemAverageValue (op 96, ajy: 1 item type id, 2 quality, 3 the span as op 95): the average unit price (a7m raw) of the
    /// item's sales at this city's market over the span; 0 = no sales.
    /// </summary>
    [Handles(OperationCodes.AuctionGetItemAverageValue, Status = SessionStatus.InWorld)]
    private AlbionResponse? AuctionGetItemAverageValue(AuctionGetItemAverageValueRequest req)
    {
        if (Player is not { } player || MarketAt(player, req.bqs) is not { } location || world.Items[req.bou] is not { } type)
            return new AuctionGetItemAverageValueResponse();
        var sales = world.Market!.Sales(location, type.Name, req.bov, DateTime.UtcNow - HistorySpan(req.bow).Span);
        var amount = sales.Sum(s => (long)s.Amount);
        return new AuctionGetItemAverageValueResponse { amz = amount == 0 ? 0 : sales.Sum(s => s.UnitPrice * s.Amount) / amount };
    }

    /// <summary>
    /// AuctionCreateOffer (op 79, ajs; keys from the sender dgi.b(int, a7m, long, int) 0x180B23AF0: 1 amount, 2 the item's
    /// object id, 3 unit price a7m, 4 the duration in hours (MarketPlacePopUpGui.CreateSellOrderInternal 0x180FD2920 passes
    /// DurationInHrs, gamedata's defaultexpiration; key 1 inferred from the same call, not captured). The setup fee is paid
    /// (UpdateMoney; refused when the silver is short), the items leave the inventory (InventoryDeleteItem, or NewSimpleItem
    /// with the count left), then the response {0 GameTimeStamp: the order's end}.
    /// </summary>
    [Handles(OperationCodes.AuctionCreateOffer, Status = SessionStatus.InWorld)]
    private AlbionResponse? AuctionCreateOffer(AuctionCreateOfferRequest req)
    {
        if (Player is not { } player) return null;
        if (MarketAt(player, req.bqs) is not { } location) return RefuseMarket(player, OperationCodes.AuctionCreateOffer, $"object {req.bqs} is no marketplace here");
        // The Black Market buys only through its own orders (SPEC §3 [choice]): a sell order there would never be bought. The
        // client offers the button, and what it shows for RC 1 is unknown (SPEC C-BM2), so a system chat line says why.
        if (BlackMarket.IsLocation(location))
        {
            SendCommandReply("The Black Market takes no sell orders: sell into its buy orders instead.", error: true);
            return RefuseMarket(player, OperationCodes.AuctionCreateOffer, "no sell orders at the Black Market");
        }
        var bag = player.Inventory.Bag;
        if (bag.Find(req.bn6) is not { } item) return RefuseMarket(player, OperationCodes.AuctionCreateOffer, $"item object {req.bn6} is not in the inventory");
        var amount = req.bn5 <= 0 ? item.Count : req.bn5;
        if (amount > item.Count || req.bn7 <= 0)
            return RefuseMarket(player, OperationCodes.AuctionCreateOffer, $"{amount} of {item.Count} x {item.Template.Name} at {req.bn7}");
        var fee = Pricing.MarketSetupFee(checked(req.bn7 * amount), world.Economy);
        if (player.Character.Silver < fee) return RefuseMarket(player, OperationCodes.AuctionCreateOffer, $"not enough silver for the setup fee {fee}");
        var record = item.ToRecord(id => world.Spells?.Name(id)) with { Count = amount };
        var now = DateTime.UtcNow;
        var order = new MarketOrder(0, player.Character.Id, MarketOrder.Offer, location, item.Template.Name, item.Quality, amount, req.bn7,
            now, now + OrderTime(req.bn8), Guid.NewGuid(), record);
        long id = 0;
        world.Market!.Batch(b => id = b.AddOrder(order));
        Wallet.TrySpend(player, fee, $"setup fee of sell order {id}");
        if (amount == item.Count)
        {
            bag.Take(bag.IndexOf(item));
            SendEvent(new InventoryDeleteItemEvent { ckr = item.Id });
        }
        else
        {
            item.Count -= amount;
            SendCount(item);
        }
        player.SaveItems();
        world.Saves.SaveNow(player.Character); // the order is stored at once: so are the items it took
        Log.Info(Name, string.Create(CultureInfo.InvariantCulture, $"'{player.Character.Name}' offers {amount} x {item.Template.Name} at {req.bn7 / 10000.0:0.####} in {location} (order {id})"));
        return new AuctionCreateOfferResponse { amo = order.Expires.Ticks };
    }

    /// <summary>
    /// AuctionCreateRequest (op 80, ajt; keys from the sender dgi.b(int, int, a7m, int, int) 0x180B23A20: 1 and 2 two ints read as
    /// quality and amount (swapped when key 1 cannot be a quality), 3 the item type id, 4 the duration in hours (as op 79),
    /// 5 the unit price a7m; inferred from MarketPlaceSellView, not captured). The escrow unit × amount and the setup fee go
    /// first (one UpdateMoney; MarketPlacePopUpGui.OnCreateBuyOrder 0x180FD5290 checks the silver for both), then the
    /// response {0 true, 1 GameTimeStamp: the order's end}.
    /// </summary>
    [Handles(OperationCodes.AuctionCreateRequest, Status = SessionStatus.InWorld)]
    private AlbionResponse? AuctionCreateRequest(AuctionCreateRequestRequest req)
    {
        if (Player is not { } player) return null;
        if (MarketAt(player, req.bqs) is not { } location) return RefuseMarket(player, OperationCodes.AuctionCreateRequest, $"object {req.bqs} is no marketplace here");
        if (BlackMarket.IsLocation(location)) return RefuseMarket(player, OperationCodes.AuctionCreateRequest, "no buy orders at the Black Market (the client hides them)");
        var (quality, amount) = req.bn9 is >= 0 and <= 5 || req.boa > 5 ? (req.bn9, req.boa) : (req.boa, req.bn9);
        if (world.Items[req.bob] is not { } type || amount <= 0 || req.bod <= 0)
            return RefuseMarket(player, OperationCodes.AuctionCreateRequest, $"item type {req.bob}, amount {amount}, price {req.bod}");
        var escrow = checked(req.bod * amount);
        var fee = Pricing.MarketSetupFee(escrow, world.Economy);
        if (!Wallet.TrySpend(player, checked(escrow + fee), string.Create(CultureInfo.InvariantCulture, $"buy order for {amount} x {type.Name}, setup fee {fee / 10000.0:0.####}")))
            return RefuseMarket(player, OperationCodes.AuctionCreateRequest, "not enough silver for the escrow and the setup fee");
        var now = DateTime.UtcNow;
        var order = new MarketOrder(0, player.Character.Id, MarketOrder.Request, location, type.Name, Math.Max(1, quality), amount, req.bod,
            now, now + OrderTime(req.boc), Guid.NewGuid(), null);
        long id = 0;
        world.Market!.Batch(b => id = b.AddOrder(order));
        world.Saves.SaveNow(player.Character);
        Log.Info(Name, string.Create(CultureInfo.InvariantCulture, $"'{player.Character.Name}' wants {amount} x {type.Name} (quality {order.Quality}+) at {req.bod / 10000.0:0.####} in {location} (order {id})"));
        return new AuctionCreateRequestResponse { amp = true, amq = order.Expires.Ticks };
    }

    /// <summary>
    /// AuctionBuyOffer (op 83, ajr: 1 amount, 2 the offer's Id): the buyer pays unit × amount (UpdateMoney, before the empty
    /// response as official), the seller gets it less the sales tax (at once, or at its next Join when offline), the buyer a
    /// finished entry with the items, the offer what is left.
    /// </summary>
    [Handles(OperationCodes.AuctionBuyOffer, Status = SessionStatus.InWorld)]
    private AlbionResponse? AuctionBuyOffer(AuctionBuyOfferRequest req)
    {
        if (Player is not { } player) return null;
        if (MarketAt(player, req.bqs) is not { } location) return RefuseMarket(player, OperationCodes.AuctionBuyOffer, $"object {req.bqs} is no marketplace here");
        if ((world.Market!.Order(req.bn3) ?? JournalMerchant.Order(world, req.bn3, location) ?? TutorialMerchant.Order(player, req.bn3, location)) is not { Type: MarketOrder.Offer } offer
            || offer.Location != location || offer.Expires <= DateTime.UtcNow)
            return RefuseMarket(player, OperationCodes.AuctionBuyOffer, $"no open offer {req.bn3} here");
        if (req.bn2 <= 0 || req.bn2 > offer.Amount) return RefuseMarket(player, OperationCodes.AuctionBuyOffer, $"{req.bn2} of {offer.Amount} left");
        if (offer.Owner == player.Character.Id) return RefuseMarket(player, OperationCodes.AuctionBuyOffer, "the player's own offer");
        var price = checked(offer.UnitPrice * req.bn2);
        if (player.Character.Silver < price) return RefuseMarket(player, OperationCodes.AuctionBuyOffer, "not enough silver");
        var proceeds = price - Pricing.MarketTax(price, world.Economy, Premium.TaxFactor(world, offer.Owner)); // the seller's premium halves it
        var npc = JournalMerchant.Owns(offer) || TutorialMerchant.Owns(offer); // NPC stock is private/standing, with no seller payment
        var seller = npc ? null : OnlinePlayer(offer.Owner);
        world.Market.Batch(b =>
        {
            if (!npc) b.SetAmount(offer.Id, offer.Amount - req.bn2);
            b.AddFinished(new MarketFinished(0, player.Character.Id, offer.Id, MarketOrder.Offer, location, offer.ItemName, offer.Quality, req.bn2,
                offer.UnitPrice, 0, DateTime.UtcNow, offer.Expires, offer.Reference, offer.Item is { } i ? i with { Count = req.bn2 } : null));
            if (!npc && seller is null) b.Credit(offer.Owner, proceeds);
            b.AddSale(new MarketSale(location, offer.ItemName, offer.Quality, req.bn2, offer.UnitPrice, DateTime.UtcNow));
        });
        Wallet.TrySpend(player, price, $"{req.bn2} x {offer.ItemName} from the market");
        TutorialJourney.MarketBought(player, offer.ItemName, req.bn2);
        if (!npc && req.bn2 == offer.Amount) Mailer.MarketNotice(world, offer with { Amount = 0 }, finished: true); // stageJ mail SPEC §3
        if (seller is not null)
        {
            Wallet.Credit(seller, proceeds, string.Create(CultureInfo.InvariantCulture, $"{req.bn2} x {offer.ItemName} sold on the market, {(price - proceeds) / 10000.0:0.####} tax"));
            world.Saves.SaveNow(seller.Character);
        }
        world.Saves.SaveNow(player.Character);
        return new AuctionBuyOfferResponse();
    }

    /// <summary>
    /// AuctionSellSpecificItemRequest (op 315, aj6: 1 the request's Id, 2 item type id, 3 inventory slot, 4 amount): the
    /// item at the slot must be of the type with at least the request's quality; the amount leaves the inventory
    /// (InventoryDeleteItem or the count left), the empty response follows, then UpdateMoney with unit × amount from the
    /// escrow less the sales tax (the official order); the request's owner gets a finished entry with the items.
    /// </summary>
    [Handles(OperationCodes.AuctionSellSpecificItemRequest, Status = SessionStatus.InWorld)]
    private AlbionResponse? AuctionSellSpecificItemRequest(AuctionSellSpecificItemRequestRequest req)
    {
        if (Player is not { } player) return null;
        const OperationCodes op = OperationCodes.AuctionSellSpecificItemRequest;
        if (MarketAt(player, req.bqs) is not { } location) return RefuseMarket(player, op, $"object {req.bqs} is no marketplace here");
        if ((world.Market!.Order(req.bqe) ?? JournalMerchant.Order(world, req.bqe, location) ?? BlackMarket.Order(world, player.Character, req.bqe, location))
                is not { Type: MarketOrder.Request } request
            || request.Location != location || request.Expires <= DateTime.UtcNow)
            return RefuseMarket(player, op, $"no open buy order {req.bqe} here");
        var bag = player.Inventory.Bag;
        if (req.bqg < 0 || req.bqg >= bag.Size || bag[req.bqg] is not { } item || item.Template.Index != req.bqf
            || !item.Template.Name.Equals(request.ItemName, StringComparison.OrdinalIgnoreCase) || item.Quality < request.Quality)
            return RefuseMarket(player, op, $"inventory #{req.bqg} does not hold a {request.ItemName} of quality {request.Quality}+");
        if (req.bqh <= 0 || req.bqh > item.Count || req.bqh > request.Amount)
            return RefuseMarket(player, op, $"{req.bqh} of {item.Count} held, {request.Amount} wanted");
        if (request.Owner == player.Character.Id) return RefuseMarket(player, op, "the player's own buy order");
        var price = checked(request.UnitPrice * req.bqh);
        var npcBuyer = BlackMarket.Owns(request);
        if (npcBuyer && price > BlackMarket.Left(world, player.Character, DateTime.UtcNow))
            return RefuseMarket(player, op, "over the Black Market's daily limit (Bm:DailyLimit)");
        var record = item.ToRecord(id => world.Spells?.Name(id)) with { Count = req.bqh };
        // The merchant's and the Black Market's buy orders keep nothing: the items are gone (and the order stays as it was).
        world.Market.Batch(b =>
        {
            if (!JournalMerchant.Owns(request) && !npcBuyer)
            {
                b.SetAmount(request.Id, request.Amount - req.bqh);
                b.AddFinished(new MarketFinished(0, request.Owner, request.Id, MarketOrder.Request, location, request.ItemName, item.Quality, req.bqh,
                    request.UnitPrice, 0, DateTime.UtcNow, request.Expires, request.Reference, record));
            }
            b.AddSale(new MarketSale(location, request.ItemName, item.Quality, req.bqh, request.UnitPrice, DateTime.UtcNow)); // the price history (econ SPEC §1)
        });
        if (req.bqh == item.Count)
        {
            bag.Take(req.bqg);
            SendEvent(new InventoryDeleteItemEvent { ckr = item.Id });
        }
        else
        {
            item.Count -= req.bqh;
            SendCount(item);
        }
        player.SaveItems();
        if (!JournalMerchant.Owns(request) && !npcBuyer && req.bqh == request.Amount)
            Mailer.MarketNotice(world, request with { Amount = 0 }, finished: true); // stageJ mail SPEC §3: the buyer's order is filled
        SendResponse(op, new GenericResponse(), RequestId, RequestEncrypted);
        if (npcBuyer) BlackMarket.Record(player.Character, price, DateTime.UtcNow);
        var tax = Pricing.MarketTax(price, world.Economy, Premium.TaxFactor(world, player.Character.Id));
        Wallet.Credit(player, price - tax, string.Create(CultureInfo.InvariantCulture, $"{req.bqh} x {request.ItemName} sold into a buy order, {tax / 10000.0:0.####} tax"));
        world.Saves.SaveNow(player.Character);
        return null;
    }

    /// <summary>
    /// QuickSellAuctionQueryAction (op 488, aj9: 0 the market object as every Auction* request, 1 the object ids of the
    /// selected inventory items; ops-audit SPEC §4): per item the best open buy order it can fill, the dearest one of its
    /// type with a quality the item reaches (as AuctionSellSpecificItemRequest takes it; not the player's own), cheapest Id
    /// first on a tie. The response (aie, read by dg6.aoy 0x180b1df60): 0 the item ids, 1 the orders' AuctionDTO, 2 the
    /// amount the stack can sell into it (stack count, at most the order's amount), parallel; an item without a fitting
    /// order (or not in the inventory) is left out; no marketplace there = no offers. The sale itself stays
    /// AuctionSellSpecificItemRequest.
    /// </summary>
    [Handles(OperationCodes.QuickSellAuctionQueryAction, Status = SessionStatus.InWorld)]
    private AlbionResponse? QuickSellAuctionQueryAction(QuickSellAuctionQueryActionRequest req)
    {
        if (Player is not { } player) return null;
        if (MarketAt(player, req.bqs) is not { } location)
        {
            // The empty answer, RC 0 as before this handler: no quick-sell offers (what the client does with an RC is not traced).
            Log.Info(Name, $"'{player.Character.Name}' QuickSellAuctionQueryAction: object {req.bqs} is no marketplace here, no offers");
            return new QuickSellAuctionQueryActionResponse().FillEmpty();
        }
        var requests = Expire(world.Market!.Orders(location, MarketOrder.Request)).Concat(JournalMerchant.Orders(world, location, MarketOrder.Request))
            .Concat(BlackMarket.Orders(world, player.Character, location, MarketOrder.Request)).ToList();
        var (ids, dtos, amounts) = (new List<long>(), new List<string>(), new List<int>());
        foreach (var id in (req.bqt ?? []).Distinct())
        {
            if (player.Inventory.Bag.Find(id) is not { } item) continue;
            var best = requests
                .Where(o => o.Owner != player.Character.Id && o.Amount > 0 && item.Quality >= o.Quality
                    && o.ItemName.Equals(item.Template.Name, StringComparison.OrdinalIgnoreCase))
                .OrderByDescending(o => o.UnitPrice).ThenBy(o => o.Id).FirstOrDefault();
            if (best is null) continue;
            ids.Add(id);
            dtos.Add(Dto(best, finished: false));
            amounts.Add(Math.Min(item.Count, best.Amount));
        }
        return new QuickSellAuctionQueryActionResponse { bhb = [.. ids], bhc = [.. dtos], bhd = [.. amounts] };
    }

    /// <summary>
    /// AuctionAbortAuction (op 84, ajp: 1 the order's Id): the player's own order closes; an offer's items go to a finished
    /// entry (collected as any other), a request's escrow comes back at once (UpdateMoney); the setup fee stays paid. Empty
    /// response.
    /// </summary>
    [Handles(OperationCodes.AuctionAbortAuction, Status = SessionStatus.InWorld)]
    private AlbionResponse? AuctionAbortAuction(AuctionAbortAuctionRequest req)
    {
        if (Player is not { } player) return null;
        if (world.Market?.Order(req.bny) is not { } o || o.Owner != player.Character.Id)
            return RefuseMarket(player, OperationCodes.AuctionAbortAuction, $"order {req.bny} is not the player's");
        world.Market.Batch(b =>
        {
            b.SetAmount(o.Id, 0);
            if (o.Type == MarketOrder.Offer)
                b.AddFinished(new MarketFinished(0, o.Owner, o.Id, o.Type, o.Location, o.ItemName, o.Quality, o.Amount, o.UnitPrice, 0, DateTime.UtcNow,
                    o.Expires, o.Reference, o.Item));
        });
        if (o.Type == MarketOrder.Request) Wallet.Credit(player, o.UnitPrice * o.Amount, $"buy order {o.Id} aborted");
        Mailer.MarketNotice(world, o, finished: false); // stageJ mail SPEC §3: "expired or was canceled"
        world.Saves.SaveNow(player.Character);
        Log.Info(Name, $"'{player.Character.Name}' aborted market order {o.Id} ({o.Type} {o.Amount} x {o.ItemName})");
        return new GenericResponse();
    }

    /// <summary>AuctionModifyAuction (op 85): not made; {0 false}.</summary>
    [Handles(OperationCodes.AuctionModifyAuction, Status = SessionStatus.InWorld)]
    private AlbionResponse? AuctionModifyAuction(AuctionModifyAuctionRequest req) => new AuctionModifyAuctionResponse { anc = false };

    /// <summary>AuctionGetMyOpenAuctions (op 94): the player's open orders of the kind of key 9 (1 sell, 2 buy), key 2 = key 9 echoed.</summary>
    [Handles(OperationCodes.AuctionGetMyOpenAuctions, Status = SessionStatus.InWorld)]
    private AlbionResponse? AuctionGetMyOpenAuctions(AuctionGetMyOpenAuctionsRequest req)
    {
        if (Player is not { } player) return null;
        var type = req.bo9 == 2 ? MarketOrder.Request : MarketOrder.Offer;
        var mine = world.Market is { } market ? Expire(market.OrdersOf(player.Character.Id, type)) : [];
        return new AuctionGetMyOpenAuctionsResponse { am2 = [.. mine.Take(MarketPage).Select(o => Dto(o, finished: false))], am3 = mine.Count > MarketPage, am4 = req.bo9 };
    }

    /// <summary>AuctionGetFinishedAuctionsCount (op 90): {} when there is none, else {0 count}.</summary>
    [Handles(OperationCodes.AuctionGetFinishedAuctionsCount, Status = SessionStatus.InWorld)]
    private AlbionResponse? AuctionGetFinishedAuctionsCount(AuctionGetFinishedAuctionsCountRequest req)
    {
        if (Player is not { } player) return null;
        if (world.Market is { } market) Expire(market.OrdersOf(player.Character.Id));
        return new AuctionGetFinishedAuctionsCountResponse { ams = world.Market?.FinishedOf(player.Character.Id).Count ?? 0 };
    }

    /// <summary>AuctionGetFinishedAuctions (op 89): the player's finished entries as DTOs with IsFinished true.</summary>
    [Handles(OperationCodes.AuctionGetFinishedAuctions, Status = SessionStatus.InWorld)]
    private AlbionResponse? AuctionGetFinishedAuctions(AuctionGetFinishedAuctionsRequest req)
    {
        if (Player is not { } player) return null;
        if (world.Market is { } market) Expire(market.OrdersOf(player.Character.Id));
        var finished = world.Market?.FinishedOf(player.Character.Id) ?? [];
        return new AuctionGetFinishedAuctionsResponse { amu = [.. finished.Take(MarketPage).Select(Dto)], amv = finished.Count > MarketPage };
    }

    /// <summary>
    /// AuctionFetchAuction (op 91, aju: 1 the finished entry's Id, 2 amount, 0 = all): its items into the inventory (as a gain:
    /// into a stack where the whole amount fits, else free slots), its silver into the wallet, then {0 the entry's DTO}. RC
    /// <see cref="MarketRefused"/> when the items do not fit (Q-D3: officially perhaps the mail).
    /// </summary>
    [Handles(OperationCodes.AuctionFetchAuction, Status = SessionStatus.InWorld)]
    private AlbionResponse? AuctionFetchAuction(AuctionFetchAuctionRequest req)
    {
        if (Player is not { } player) return null;
        if (world.Market?.Finished(req.boe) is not { } entry || entry.Owner != player.Character.Id)
            return RefuseMarket(player, OperationCodes.AuctionFetchAuction, $"finished entry {req.boe} is not the player's");
        var amount = req.bof <= 0 ? entry.Amount : Math.Min(req.bof, entry.Amount);
        if (!Fetch(player, entry, amount)) return RefuseMarket(player, OperationCodes.AuctionFetchAuction, $"{amount} x {entry.ItemName} do not fit the inventory");
        return new AuctionFetchAuctionResponse { amr = Dto(entry with { Amount = amount }) };
    }

    // Client MarketPlaceCompleteView.OnTakeAllResult(cfd): 0 shows GENERIC_ERROR; 4 refreshes without an error dialog.
    private enum BulkMarketFetchResult : byte { Error = 0, Success = 4 }

    /// <summary>AuctionFetchFinishedAuctions (op 493): attempts every finished entry, returning client success only when all fit.</summary>
    [Handles(OperationCodes.AuctionFetchFinishedAuctions, Status = SessionStatus.InWorld)]
    private AlbionResponse? AuctionFetchFinishedAuctions(AuctionFetchFinishedAuctionsRequest req)
    {
        if (Player is not { } player) return null;
        var allCollected = true;
        foreach (var entry in world.Market?.FinishedOf(player.Character.Id) ?? [])
            if (!Fetch(player, entry, entry.Amount)) allCollected = false;
        return new AuctionFetchFinishedAuctionsResponse { arb = (byte)(allCollected ? BulkMarketFetchResult.Success : BulkMarketFetchResult.Error) };
    }

    /// <summary>Collects <paramref name="amount"/> of a finished entry (and its silver); false (nothing changed) when the items do not fit.</summary>
    private bool Fetch(Player player, MarketFinished entry, int amount)
    {
        var type = world.Items.Find(entry.ItemName);
        if (amount > 0 && (type is null || !type.CanCreate || !Fits(player.Inventory.Bag, type.IsEquipment ? Enumerable.Repeat((type, 1), amount) : [(type, amount)], fame: entry.Item?.Fame)))
            return false;
        world.Market!.Batch(b =>
        {
            if (amount >= entry.Amount) b.DeleteFinished(entry.Id);
            else b.SetFinishedAmount(entry.Id, entry.Amount - amount);
        });
        if (amount > 0 && type is not null)
        {
            var saved = entry.Item is { } i ? i with { Count = type.IsEquipment ? 1 : amount } : null;
            for (var n = 0; n < (type.IsEquipment ? amount : 1); n++) GiveItem(player, type, type.IsEquipment ? 1 : amount, entry.Quality, saved);
            player.SaveItems();
        }
        if (amount >= entry.Amount && entry.Silver > 0) Wallet.Credit(player, entry.Silver, $"market order {entry.OrderId} expired");
        world.Saves.SaveNow(player.Character);
        Log.Info(Name, $"'{player.Character.Name}' collected {amount} x {entry.ItemName} from the market{(entry.Silver > 0 ? $" and {entry.Silver / 10000} silver" : "")}");
        return true;
    }

    /// <summary>The player in the world who plays character <paramref name="id"/>; null = offline.</summary>
    private Player? OnlinePlayer(Guid id) => world.Maps.Maps.SelectMany(m => m.Players.Values).FirstOrDefault(p => p.Character.Id == id);

    /// <summary>An open order as the client's AuctionDTO JSON.</summary>
    internal string Dto(MarketOrder o, bool finished) =>
        Dto(o.Id, o.UnitPrice, o.Amount, o.Type, finished, o.ItemName, o.Quality, o.Expires, o.Reference);

    /// <summary>A private pending claim. The collector must be identified as its buyer: the client's
    /// MarketPlaceCompleteView.CompleteEntry compares BuyerCharacterId with the local character before
    /// sending op 91. Anonymous open listings do not need this field; pending collections do.</summary>
    internal string Dto(MarketFinished f) => Dto(f.Id, f.UnitPrice, f.Amount, f.Type, true, f.ItemName, f.Quality, f.Expires, f.Reference, f.Owner);

    /// <summary>
    /// The AuctionDTO fields in the official order and spelling: Id, UnitPriceSilver, TotalPriceSilver (= unit × amount),
    /// DistanceFee 0, Amount, Tier, IsFinished, AuctionType, HasBuyerFetched, HasSellerFetched, SellerCharacterId, SellerName,
    /// BuyerCharacterId, BuyerName (pending claims identify their owner; other participant fields are null), ItemTypeId (T4_X@n),
    /// ItemGroupTypeId (without @n), EnchantmentLevel, QualityLevel, Expires (yyyy-MM-ddTHH:mm:ss.ffffff), ReferenceId,
    /// LocationId null.
    /// </summary>
    private string Dto(long id, long unit, int amount, string type, bool finished, string itemName, int quality, DateTime expires, Guid reference, Guid? buyer = null)
    {
        var item = world.Items.Find(itemName);
        var buffer = new MemoryStream();
        using (var w = new Utf8JsonWriter(buffer))
        {
            w.WriteStartObject();
            w.WriteNumber("Id", id);
            w.WriteNumber("UnitPriceSilver", unit);
            w.WriteNumber("TotalPriceSilver", unit * amount);
            w.WriteNumber("DistanceFee", 0);
            w.WriteNumber("Amount", amount);
            w.WriteNumber("Tier", item?.Tier ?? 0);
            w.WriteBoolean("IsFinished", finished);
            w.WriteString("AuctionType", type);
            w.WriteBoolean("HasBuyerFetched", false);
            w.WriteBoolean("HasSellerFetched", finished);
            w.WriteNull("SellerCharacterId");
            w.WriteNull("SellerName");
            if (buyer is { } collector) w.WriteString("BuyerCharacterId", collector.ToString());
            else w.WriteNull("BuyerCharacterId");
            w.WriteNull("BuyerName");
            w.WriteString("ItemTypeId", item?.Name ?? itemName);
            w.WriteString("ItemGroupTypeId", item?.BaseName ?? itemName);
            w.WriteNumber("EnchantmentLevel", item?.Enchantment ?? 0);
            w.WriteNumber("QualityLevel", quality);
            w.WriteString("Expires", expires.ToString("yyyy-MM-ddTHH:mm:ss.ffffff", CultureInfo.InvariantCulture));
            w.WriteString("ReferenceId", reference.ToString());
            w.WriteNull("LocationId");
            w.WriteEndObject();
        }
        return Encoding.UTF8.GetString(buffer.ToArray());
    }
}
