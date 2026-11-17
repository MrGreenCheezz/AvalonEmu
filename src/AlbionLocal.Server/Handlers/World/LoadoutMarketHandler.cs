using System.Globalization;
using AlbionLocal.Photon;
using AlbionLocal.Server.Entities;
using AlbionLocal.Server.Game;
using AlbionLocal.Server.Persistence;
using AlbionLocal.Server.Protocol;
using AlbionLocal.Server.Systems;

namespace AlbionLocal.Server.Network;

/// <summary>
/// StageR: buying a loadout at the market and the quick sell (analysis/work/stageR/kill-vanity/SPEC.md §4, §5), on this city's order book
/// (<see cref="MarketAt"/>, one per city as every Auction* request).
/// <para>The market's "Quick Purchase Loadouts" view (MarketPlaceLoadoutsBuyView) asks AuctionGetLoadoutOffers 458 (ajz {0 market, 1 the
/// loadout owner Guid, 2 the loadout id, 3 how many loadouts}) and shows the answer (ad3 {0 byte[] slot keys, 1 string[][] AuctionDTOs per key,
/// 2 short[][] the amount to buy of each}; the key is the equipment slot 0-9, or 10 + the index of the loadout's inventory entry,
/// MarketPlaceLoadoutsBuyView.CreateEntryList 0x1810222C0). Purchase sends AuctionBuyLoadoutOffer 459 (ajq {0 market, 1 the chosen offer
/// ids, 2 their slot keys, 3 move the items into the inventory}); the answer adp is ad3 (new offers when prices changed: the client then
/// shows "Updated Offers") + {3 the bought offer ids, 4 their slot keys, 5 LoadoutBoughtAuctionFetchingResult}.</para>
/// <para>All or nothing: every chosen offer must still be open at the price and amount this session's last answer showed, the player must
/// have the silver for all of it, and only then the silver goes and every offer is bought in one market batch; otherwise nothing is bought
/// or charged and the answer carries fresh offers for the asked slots. The items arrive as the market's finished entries (collected in
/// "Completed Transactions"), or, with key 3, straight into the inventory when they all fit (else they stay there: InventoryFull).</para>
/// </summary>
public sealed partial class WorldSession
{
    /// <summary>The RCs the loadout view shows as "@MARKETPLACEGUI_CATEGORY_LOADOUTS_FEATURE_DISABLED" (OnLoadoutOffersUpdated 0x4792, OnLoadoutOfferBought 0x47BE).</summary>
    public const short LoadoutOffersUnavailable = 0x4792, LoadoutBuyUnavailable = 0x47BE;

    /// <summary>LoadoutBoughtAuctionFetchingResult.</summary>
    public const int LoadoutFetchOk = 0, LoadoutFetchInventoryFull = 3;

    /// <summary>One offer of a loadout answer: its slot key, the amount to buy and the unit price shown.</summary>
    internal sealed record LoadoutOffer(long OrderId, byte Slot, int Amount, long UnitPrice);

    /// <summary>The last AuctionGetLoadoutOffers / AuctionBuyLoadoutOffer answer of this session: its market and the offers it showed.</summary>
    internal sealed record LoadoutProposal(string Location, Loadout Loadout, int Count, Dictionary<long, LoadoutOffer> Offers);

    private LoadoutProposal? _loadoutProposal;

    /// <summary>One thing a loadout needs: its slot key, the item, its quality and LoadoutItemPreference (0 ≤, 1 =, 2 ≥), the amount.</summary>
    private sealed record LoadoutNeed(byte Slot, string Item, int Quality, int Preference, int Amount);

    /// <summary>
    /// What <paramref name="loadout"/> × <paramref name="count"/> needs (MarketPlaceLoadoutsBuyView.PrepareCache 0x181023DE0): each named equipment
    /// slot (the potion and food slots their stack, others 1; no off-hand beside a two-handed weapon), then each inventory entry (key 10 + its
    /// index, its stack) [infer: the whole loadout; the client sends nothing about what the player has already].
    /// </summary>
    private List<LoadoutNeed> Needs(Loadout loadout, int count)
    {
        var needs = new List<LoadoutNeed>();
        var twoHanded = loadout.Equipment.Length > 0 && world.Items.Find(loadout.Equipment[0]) is { TwoHanded: true };
        for (var slot = 0; slot < Math.Min(10, loadout.Equipment.Length); slot++)
        {
            if (loadout.Equipment[slot] is not { Length: > 0 } name || slot == 1 && twoHanded) continue;
            var stack = slot is 8 or 9 && slot - 8 < loadout.SlotStacks.Length ? Math.Max(1, loadout.SlotStacks[slot - 8]) : 1;
            var preference = slot is 8 or 9 ? loadout.PotionFoodPreference.ElementAtOrDefault(slot - 8) : slot < loadout.PreferenceA.Length ? loadout.PreferenceA[slot] : 2;
            needs.Add(new LoadoutNeed((byte)slot, name, loadout.Quality.ElementAtOrDefault(slot), preference, stack * count));
        }
        for (var i = 0; i < loadout.Items.Length && 10 + i <= byte.MaxValue; i++)
            if (loadout.Items[i] is { Length: > 0 } name)
                needs.Add(new LoadoutNeed((byte)(10 + i), name, loadout.ItemQuality.ElementAtOrDefault(i), 2, Math.Max(1, loadout.ItemStacks.ElementAtOrDefault(i)) * count));
        return needs;
    }

    /// <summary>The cheapest open offers of this city (not the player's own) that cover <paramref name="need"/>, as far as there are any.</summary>
    private List<LoadoutOffer> Cheapest(Player player, string location, LoadoutNeed need)
    {
        var picked = new List<LoadoutOffer>();
        var left = need.Amount;
        foreach (var o in Expire(world.Market!.Orders(location, MarketOrder.Offer))
                     .Where(o => o.Owner != player.Character.Id && o.Amount > 0 && o.ItemName.Equals(need.Item, StringComparison.OrdinalIgnoreCase)
                         && QualityFits(o.Quality, need.Quality, need.Preference))
                     .OrderBy(o => o.UnitPrice).ThenBy(o => o.Id))
        {
            if (left <= 0) break;
            var take = Math.Min(left, o.Amount);
            picked.Add(new LoadoutOffer(o.Id, need.Slot, take, o.UnitPrice));
            left -= take;
        }
        return picked;
    }

    /// <summary>
    /// The offers of <paramref name="needs"/> as the ad3 keys (every asked slot, an empty list where the city has none: the client shows it as
    /// not available), remembered as this session's proposal.
    /// </summary>
    private (byte[] Slots, string[][] Dtos, short[][] Amounts) Propose(Player player, string location, Loadout loadout, int count, List<LoadoutNeed> needs)
    {
        var offers = new Dictionary<long, LoadoutOffer>();
        var (slots, dtos, amounts) = (new List<byte>(), new List<string[]>(), new List<short[]>());
        foreach (var need in needs)
        {
            var picked = Cheapest(player, location, need).Where(o => !offers.ContainsKey(o.OrderId)).ToList();
            foreach (var o in picked) offers[o.OrderId] = o;
            slots.Add(need.Slot);
            dtos.Add([.. picked.Select(o => Dto(world.Market!.Order(o.OrderId)! with { Amount = o.Amount }, finished: false))]);
            amounts.Add([.. picked.Select(o => (short)Math.Min(short.MaxValue, o.Amount))]);
        }
        _loadoutProposal = new LoadoutProposal(location, loadout, count, offers);
        return ([.. slots], [.. dtos], [.. amounts]);
    }

    /// <summary>AuctionGetLoadoutOffers (op 458): the cheapest offers of this city for every piece of the loadout × key 3 (see the class).</summary>
    [Handles(OperationCodes.AuctionGetLoadoutOffers, Status = SessionStatus.InWorld)]
    private AlbionResponse? AuctionGetLoadoutOffers(AuctionGetLoadoutOffersRequest req)
    {
        if (Player is not { } player) return null;
        if (MarketAt(player, req.bqs) is not { } location)
            return WithCode(RefuseMarket(player, OperationCodes.AuctionGetLoadoutOffers, $"object {req.bqs} is no marketplace here",
                new AuctionGetLoadoutOffersResponse().FillEmpty()), LoadoutOffersUnavailable);
        if (Loadouts(req.box)?.FirstOrDefault(l => l.Id == req.boy) is not { } loadout)
            return WithCode(RefuseMarket(player, OperationCodes.AuctionGetLoadoutOffers, $"no loadout {req.boy} of {req.box}",
                new AuctionGetLoadoutOffersResponse().FillEmpty()), LoadoutOffersUnavailable);
        var count = Math.Max(1, (int)req.boz);
        var (slots, dtos, amounts) = Propose(player, location, loadout, count, Needs(loadout, count));
        Log.Info(Name, $"'{player.Character.Name}' loadout '{loadout.Name}' x{count} at {location}: {_loadoutProposal!.Offers.Count} offer(s) for {slots.Length} slot(s)");
        return new AuctionGetLoadoutOffersResponse { am9 = slots, ana = dtos, anb = amounts };
    }

    private static AlbionResponse WithCode(AlbionResponse? r, short code)
    {
        r ??= new GenericResponse();
        r.ReturnCode = code;
        return r;
    }

    /// <summary>
    /// AuctionBuyLoadoutOffer (op 459): the chosen offers of this session's last answer, bought all together or not at all (see the class).
    /// RC 0 always once the market and the proposal are known (a changed offer answers with fresh offers, no silver is taken); not enough
    /// silver: RC <see cref="MarketRefused"/>.
    /// </summary>
    [Handles(OperationCodes.AuctionBuyLoadoutOffer, Status = SessionStatus.InWorld)]
    private AlbionResponse? AuctionBuyLoadoutOffer(AuctionBuyLoadoutOfferRequest req)
    {
        if (Player is not { } player) return null;
        const OperationCodes op = OperationCodes.AuctionBuyLoadoutOffer;
        var empty = new AuctionBuyLoadoutOfferResponse().FillEmpty();
        if (MarketAt(player, req.bqs) is not { } location || _loadoutProposal is not { } proposal || proposal.Location != location)
            return WithCode(RefuseMarket(player, op, "no loadout offers asked at this market", empty), LoadoutBuyUnavailable);
        var ids = req.bnz ?? [];
        if (ids.Length == 0 || ids.Distinct().Count() != ids.Length) return RefuseMarket(player, op, $"{ids.Length} offer ids", empty);
        var lines = new List<(MarketOrder Order, LoadoutOffer Shown)>();
        var stale = new HashSet<byte>();
        foreach (var id in ids)
        {
            if (!proposal.Offers.TryGetValue(id, out var shown)) return RefuseMarket(player, op, $"offer {id} was not in the answer", empty);
            if (world.Market!.Order(id) is { Type: MarketOrder.Offer } o && o.Location == location && o.Expires > DateTime.UtcNow
                && o.UnitPrice == shown.UnitPrice && o.Amount >= shown.Amount && o.Owner != player.Character.Id)
                lines.Add((o, shown));
            else stale.Add(shown.Slot);
        }
        if (stale.Count > 0)
        {
            // Nothing bought: fresh offers for every slot asked (the client's "Updated Offers"), so the whole set can be confirmed again.
            var asked = lines.Select(l => l.Shown.Slot).Concat(stale).ToHashSet();
            var (slots, dtos, amounts) = Propose(player, location, proposal.Loadout, proposal.Count, [.. Needs(proposal.Loadout, proposal.Count).Where(n => asked.Contains(n.Slot))]);
            Log.Info(Name, $"'{player.Character.Name}' loadout purchase: slot(s) {string.Join(",", stale.Order())} changed, nothing bought, new offers sent");
            return new AuctionBuyLoadoutOfferResponse { am9 = slots, ana = dtos, anb = amounts, amj = [], amk = [], aml = LoadoutFetchOk };
        }
        var total = lines.Aggregate(0L, (sum, l) => checked(sum + l.Order.UnitPrice * l.Shown.Amount));
        if (player.Character.Silver < total) return RefuseMarket(player, op, string.Create(CultureInfo.InvariantCulture, $"{total / 10000.0:0.####} silver needed"), empty);
        var fetch = req.bn1 && FitsAll(player, lines);
        var now = DateTime.UtcNow;
        var payouts = new List<(MarketOrder Order, long Proceeds, Player? Seller)>();
        foreach (var (o, shown) in lines)
        {
            var price = checked(o.UnitPrice * shown.Amount);
            payouts.Add((o, price - Pricing.MarketTax(price, world.Economy, Premium.TaxFactor(world, o.Owner)), OnlinePlayer(o.Owner)));
        }
        // One batch: every offer's amount, the buyer's finished entries (unless the items go straight into the bag), the offline sellers' pay
        // and the price history; then the silver, which the check above guarantees under the world lock.
        world.Market!.Batch(b =>
        {
            foreach (var ((o, shown), (_, proceeds, seller)) in lines.Zip(payouts))
            {
                b.SetAmount(o.Id, o.Amount - shown.Amount);
                if (!fetch)
                    b.AddFinished(new MarketFinished(0, player.Character.Id, o.Id, MarketOrder.Offer, location, o.ItemName, o.Quality, shown.Amount,
                        o.UnitPrice, 0, now, o.Expires, o.Reference, o.Item is { } i ? i with { Count = shown.Amount } : null));
                if (seller is null) b.Credit(o.Owner, proceeds);
                b.AddSale(new MarketSale(location, o.ItemName, o.Quality, shown.Amount, o.UnitPrice, now));
            }
        });
        Wallet.TrySpend(player, total, $"loadout '{proposal.Loadout.Name}' from the market");
        if (fetch)
        {
            foreach (var (o, shown) in lines)
            {
                var type = world.Items.Find(o.ItemName)!;
                var saved = o.Item is { } i ? i with { Count = type.IsEquipment ? 1 : shown.Amount } : null;
                for (var n = 0; n < (type.IsEquipment ? shown.Amount : 1); n++) GiveItem(player, type, type.IsEquipment ? 1 : shown.Amount, o.Quality, saved);
            }
            player.SaveItems();
        }
        foreach (var ((o, shown), (_, proceeds, seller)) in lines.Zip(payouts))
        {
            if (shown.Amount == o.Amount) Mailer.MarketNotice(world, o with { Amount = 0 }, finished: true);
            if (seller is null) continue;
            Wallet.Credit(seller, proceeds, string.Create(CultureInfo.InvariantCulture, $"{shown.Amount} x {o.ItemName} sold on the market"));
            world.Saves.SaveNow(seller.Character);
        }
        world.Saves.SaveNow(player.Character);
        _loadoutProposal = null;
        var result = req.bn1 && !fetch ? LoadoutFetchInventoryFull : LoadoutFetchOk;
        Log.Info(Name, string.Create(CultureInfo.InvariantCulture,
            $"'{player.Character.Name}' bought loadout '{proposal.Loadout.Name}': {lines.Count} offer(s) for {total / 10000.0:0.####} silver{(fetch ? ", into the inventory" : req.bn1 ? ", inventory full: in Completed Transactions" : "")}"));
        return new AuctionBuyLoadoutOfferResponse
        {
            am9 = [], ana = [], anb = [], amj = [.. lines.Select(l => l.Order.Id)], amk = [.. lines.Select(l => l.Shown.Slot)], aml = result,
        };
    }

    /// <summary>Whether every bought item fits the inventory at once (an equipment piece per slot, a stack as one gain).</summary>
    private bool FitsAll(Player player, List<(MarketOrder Order, LoadoutOffer Shown)> lines)
    {
        var gains = new List<(Data.ItemTemplate, int)>();
        foreach (var (o, shown) in lines)
        {
            if (world.Items.Find(o.ItemName) is not { CanCreate: true } type) return false;
            if (type.IsEquipment) gains.AddRange(Enumerable.Repeat((type, 1), shown.Amount));
            else gains.Add((type, shown.Amount));
        }
        return Fits(player.Inventory.Bag, gains);
    }

    /// <summary>
    /// QuickSellAuctionSellAction (op 489, aka {0 market, 1 the item object ids, 2 the buy order ids, 3 the amounts}; MarketPlaceQuickSellGui
    /// through dg8.aoz 0x180B1E710 with the rows QuickSellAuctionQueryAction answered): each item sold into its order exactly as
    /// AuctionSellSpecificItemRequest sells (<see cref="SellIntoRequest"/>), a row whose item, order or amount no longer holds is skipped;
    /// the answer (aif {0 long[]}) lists the item ids sold (the client plays the sale sound when it is not empty and says "Some items from your
    /// inventory could not be sold!" otherwise [infer: dg2&lt;aka, aif, …, HashSet&lt;long&gt;&gt;]), then the silver per sale.
    /// </summary>
    [Handles(OperationCodes.QuickSellAuctionSellAction, Status = SessionStatus.InWorld)]
    private AlbionResponse? QuickSellAuctionSellAction(QuickSellAuctionSellActionRequest req)
    {
        if (Player is not { } player) return null;
        const OperationCodes op = OperationCodes.QuickSellAuctionSellAction;
        if (MarketAt(player, req.bqs) is not { } location)
            return RefuseMarket(player, op, $"object {req.bqs} is no marketplace here", new QuickSellAuctionSellActionResponse { bhe = [] });
        var (items, orders, amounts) = (req.bqu ?? [], req.bqv ?? [], req.bqw ?? []);
        var sold = new List<long>();
        var pay = new List<(MarketOrder Request, long Price, long Tax, int Amount)>();
        for (var i = 0; i < Math.Min(items.Length, Math.Min(orders.Length, amounts.Length)); i++)
        {
            var bag = player.Inventory.Bag;
            var slot = Enumerable.Range(0, bag.Size).FirstOrDefault(s => bag[s]?.Id == items[i], -1);
            int amount = amounts[i];
            if (slot < 0 || bag[slot] is not { } item) continue;
            if ((world.Market!.Order(orders[i]) ?? JournalMerchant.Order(world, orders[i], location) ?? BlackMarket.Order(world, player.Character, orders[i], location))
                    is not { Type: MarketOrder.Request } request
                || request.Location != location || request.Expires <= DateTime.UtcNow || request.Owner == player.Character.Id
                || !item.Template.Name.Equals(request.ItemName, StringComparison.OrdinalIgnoreCase) || item.Quality < request.Quality
                || amount <= 0 || amount > item.Count || amount > request.Amount || SellIntoRequestRefusal(player, request, amount) is not null)
            {
                Log.Info(Name, $"'{player.Character.Name}' quick sell of item {items[i]} into order {orders[i]} x{amount}: skipped (changed)");
                continue;
            }
            var (price, tax) = SellIntoRequest(player, location, request, slot, item, amount);
            pay.Add((request, price, tax, amount));
            sold.Add(items[i]);
        }
        SendResponse(op, new QuickSellAuctionSellActionResponse { bhe = [.. sold] }, RequestId, RequestEncrypted);
        foreach (var (request, price, tax, amount) in pay) PaySale(player, request, price, tax, amount);
        return null;
    }
}
