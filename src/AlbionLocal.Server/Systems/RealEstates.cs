using System.Globalization;
using System.Numerics;
using System.Xml.Linq;
using AlbionLocal.Photon;
using AlbionLocal.Server.Data;
using AlbionLocal.Server.Entities;
using AlbionLocal.Server.Game;
using AlbionLocal.Server.Persistence;
using AlbionLocal.Server.Protocol;

namespace AlbionLocal.Server.Systems;

/// <summary>
/// The city plots (public issue #18; analysis/work/realestate/SPEC.md): every realestate tile of a city template is a
/// <see cref="RealEstateObject"/> (NewRealEstate 191). A free plot is claimed at its price (ClaimRealEstate 171, the client's "Claim"); an owned
/// one goes to auction once per cycle (gamedata RealEstateAuctionSettings: 28 days from 2017-07-03 12:00 UTC, 7 days long; the last 24 h only
/// the owner may bid, the owner's bid counts twice). Bids are held from the bidder's silver; when the auction ends the highest bid wins and is
/// kept, every other bid comes back, the previous owner gets RealEstate:OwnerLosingBonus of the winning bid, and every bidder gets a mail. An
/// owner may also list the plot for sale (SetSaleStatusForEstate, BuyRealEstate), change its outline, put up crafting stations on it from the
/// build menu and give it up. The plots under the content/spawns stations stay with the city (<see cref="RealEstateObject.Reserved"/>). Owned
/// plots, bids and buildings are written at once to <see cref="IRealEstateStore"/>. Touched only under <see cref="World.Sync"/>.
/// </summary>
public sealed class RealEstates
{
    private readonly World world;
    private readonly IRealEstateStore? store;
    private readonly Dictionary<Guid, RealEstatePlot> _plots = [];
    private readonly Dictionary<(Guid Plot, Guid Bidder, long Auction), RealEstateBid> _bids = [];
    private readonly Dictionary<Guid, RealEstateBuilding> _buildings = [];
    private TimeSpan _nextUpdate;

    /// <summary>The side of a building on a plot (a crafting station: 20 × 20 m, as the content/spawns stations on the 20 × 20 plots).</summary>
    public const float BuildingSize = 20;

    public RealEstates(World world, IRealEstateStore? store)
    {
        this.world = world;
        this.store = store;
        if (store is null) return;
        foreach (var p in store.LoadPlots()) _plots[p.Id] = p;
        foreach (var b in store.LoadBids()) _bids[(b.Plot, b.Bidder, b.Auction)] = b;
        foreach (var b in store.LoadRealEstateBuildings().Where(b => _plots.ContainsKey(b.Plot))) _buildings[b.Id] = b;
        if (_plots.Count > 0 || _bids.Count > 0)
            Log.Info("world", $"{_plots.Count} owned city plot(s) loaded, {_bids.Count(b => !b.Value.Settled)} open bid(s), {_buildings.Count} building(s) on them");
    }

    /// <summary>RealEstate:Enabled.</summary>
    public bool Enabled => world.Config.RealEstateEnabled;

    /// <summary>gamedata.xml's real-estate numbers.</summary>
    public RealEstateSettings Data => world.Economy.RealEstate;

    public IEnumerable<RealEstatePlot> Owned => _plots.Values;

    public RealEstatePlot? Find(Guid plot) => _plots.GetValueOrDefault(plot);

    public IEnumerable<RealEstateBid> BidsOn(Guid plot) => _bids.Values.Where(b => b.Plot == plot);

    public IEnumerable<RealEstateBuilding> BuildingsOn(Guid plot) => _buildings.Values.Where(b => b.Plot == plot);

    // ---- the schedule ----

    /// <summary>A GM's shift of the schedule (.plot auction start): the cycles count from here instead of the data's reference; in memory only.</summary>
    public DateTime? Anchor { get; set; }

    /// <summary>A GM's early end (.plot auction end) of the auction that started at Start: it ends at End instead; in memory only.</summary>
    private (long Start, long End)? _endedEarly;

    public TimeSpan Cycle => TimeSpan.FromDays(world.Config.RealEstateAuctionCycleDays ?? Data.CycleDays);

    public TimeSpan Length => TimeSpan.FromDays(Math.Min(world.Config.RealEstateAuctionLengthDays ?? Data.LengthDays, Cycle.TotalDays));

    public TimeSpan OwnerExclusive => TimeSpan.FromMinutes(Math.Min(world.Config.RealEstateOwnerExclusiveMinutes ?? Data.OwnerExclusiveMinutes, Length.TotalMinutes));

    public double OwnerMultiplier => world.Config.RealEstateOwnerBidMultiplier ?? Data.OwnerBidMultiplier;

    /// <summary>
    /// The auction of the cycle <paramref name="now"/> (GameTimeStamp) is in: its start (the cycle's start, at or before now), the end of
    /// bidding for everyone but the owner (the end less RealEstate:OwnerExclusiveMinutes) and its end (start + the auction length), the
    /// RealEstateGetAuctionData keys 3, 4 and 5 (SPEC §1.3).
    /// </summary>
    public (long Start, long OwnersOnly, long End) Window(long now)
    {
        var anchor = (Anchor ?? Data.Reference).Ticks;
        var cycle = Math.Max(Cycle.Ticks, TimeSpan.TicksPerMinute);
        var k = (long)Math.Floor((now - anchor) / (double)cycle);
        var start = anchor + k * cycle;
        while (start > now) start -= cycle;
        while (start + cycle <= now) start += cycle;
        var end = start + Length.Ticks;
        var ownersOnly = end - OwnerExclusive.Ticks;
        if (_endedEarly is { } early && early.Start == start && early.End < end) (end, ownersOnly) = (early.End, Math.Min(ownersOnly, early.End));
        return (start, ownersOnly, end);
    }

    // ---- the plots on a map ----

    /// <summary>
    /// The plots of <paramref name="map"/>'s city template (RealEstate:Enabled; not on an instance) and the buildings standing on the owned ones.
    /// A plot whose rectangle holds a content/spawns building is the city's (<see cref="RealEstateObject.Reserved"/>).
    /// </summary>
    public IEnumerable<WorldObject> Populate(Map map, ClusterTemplate template)
    {
        if (!Enabled || template.RealEstates.Count == 0 || map.TutorialOwner is not null || map.Cluster.Contains('@')) yield break;
        var stations = world.Stations.For(map.Cluster).Select(s => s.Position).ToList();
        var city = CityName(map.Cluster);
        var now = DateTime.UtcNow.Ticks;
        foreach (var r in template.RealEstates)
        {
            var o = new RealEstateObject(world.ObjectIds.Next(), r, null, GuildOf);
            var reserved = stations.Any(s => o.Contains(s));
            if (reserved) o = new RealEstateObject(o.Id, r, city, GuildOf);
            else o.Plot = _plots.TryGetValue(r.Guid, out var plot) && plot.Cluster == map.Cluster ? plot : null;
            yield return o;
            if (o.Plot is null) continue;
            foreach (var b in BuildingsOn(r.Guid).ToList())
                if (MakeBuilding(map.Cluster, b, now) is { } building) yield return building;
        }
    }

    /// <summary>The displayed name of <paramref name="cluster"/> (world.xml displayname), else its id.</summary>
    public string CityName(string cluster) =>
        world.Clusters?.WorldIfLoaded?.Find(cluster)?.DisplayName is { Length: > 0 } name ? name : cluster;

    private (Guid Id, string Name)? GuildOf(Guid character) => world.Guilds.Of(character) is { } g ? (g.Id, g.Name) : null;

    /// <summary>The plot object <paramref name="id"/> on <paramref name="p"/>'s map.</summary>
    public RealEstateObject? ObjectOf(Player p, long id) => p.Map?.Objects.GetValueOrDefault(id) as RealEstateObject;

    /// <summary>The plot object with template Guid <paramref name="plot"/> on <paramref name="p"/>'s map.</summary>
    public RealEstateObject? ObjectOf(Player p, Guid plot) => p.Map?.Objects.Values.OfType<RealEstateObject>().FirstOrDefault(o => o.Guid == plot);

    /// <summary>The plot object of <paramref name="plot"/> on its loaded city map; null when that map is not loaded.</summary>
    public RealEstateObject? ObjectOf(RealEstatePlot plot) =>
        world.Maps.Find(plot.Cluster)?.Objects.Values.OfType<RealEstateObject>().FirstOrDefault(o => o.Guid == plot.Id);

    /// <summary>The changed plot again to everyone who sees it (NewRealEstate updates the client's object, then its AccessStatus).</summary>
    public void Changed(RealEstateObject o)
    {
        if (o.Map is not { } map) return;
        map.SendToObservers(o, o.NewRealEstate());
        map.SendToObservers(o, o.AccessStatus());
    }

    private void Changed(RealEstatePlot plot, Guid id)
    {
        if (world.Maps.Find(plot.Cluster)?.Objects.Values.OfType<RealEstateObject>().FirstOrDefault(o => o.Guid == id) is not { } o) return;
        o.Plot = plot.Owned ? plot : null;
        Changed(o);
    }

    // ---- prices ----

    /// <summary>
    /// The claim price and the auction's least bid (a7m, whole silver): the tile's overridesaleprice when it is 0 or more, else (RealEstateGui
    /// GetClaimPrice 0x181616E90 → bgf.abo 0x1805CAFC0) the plot's m² × Sale squaremeterpricebuildingarea (12500; the whole plot counted as
    /// building area, squaremeterpricenormal is 0) × its locationquality's factor (A 40 … E 1) × overallpricefactor, × RealEstate:PriceFactor.
    /// </summary>
    public long Price(RealEstateObject o)
    {
        var data = Data;
        double silver;
        if (double.TryParse(o.Spawn.Attr("overridesaleprice"), NumberStyles.Float, CultureInfo.InvariantCulture, out var over) && over >= 0) silver = over;
        else
        {
            var factor = o.Spawn.Quality is { } q && data.QualityFactors.TryGetValue(q, out var f) ? f : 1;
            silver = o.Size.X * o.Size.Y * data.SquareMetrePriceBuilding * factor * data.OverallPriceFactor;
        }
        return (long)Math.Round(silver * world.Config.RealEstatePriceFactor) * Pricing.A7m;
    }

    private static string Silver(long raw) => (raw / Pricing.A7m).ToString("N0", CultureInfo.InvariantCulture);

    // ---- claim, give up, outline ----

    /// <summary>ClaimRealEstate: a free plot of the player's map for its <see cref="Price"/>. True = claimed (paid, saved, shown).</summary>
    public bool Claim(Player p, Guid plotId, out string why)
    {
        why = "";
        if (!Enabled) why = "real estate is off on this server";
        else if (ObjectOf(p, plotId) is not { } o) why = $"no plot {plotId} here";
        else if (o.Reserved) why = $"this plot belongs to {o.ReservedBy}";
        else if (o.Plot is not null) why = $"this plot belongs to {o.Plot.OwnerName}";
        else if (Price(o) is var price && !Wallet.TrySpend(p, price, $"claiming {o.Guid}"))
            why = $"@REALESTATE_ERROR_CLAIM_NOT_ENOUGH_SILVER";
        else
        {
            var plot = new RealEstatePlot(o.Guid, p.Map!.Cluster)
            {
                Owner = p.Character.Id, OwnerName = p.Character.Name, Since = world.GameNow, LastPrice = price,
            };
            _plots[plot.Id] = plot;
            store?.SavePlot(plot);
            world.Saves.SaveNow(p.Character);
            o.Plot = plot;
            Changed(o);
            var (start, _, end) = Window(world.GameNow);
            var next = end > world.GameNow ? start : start + Cycle.Ticks;
            Notice(p.Character.Id, $"Your plot in {CityName(plot.Cluster)} is now available",
                $"Congratulations, your plot in {CityName(plot.Cluster)} is now available to you.\n\nNext auction for this property starts:\n{Date(next)}");
            Log.Info("world", $"'{p.Character.Name}' claimed {plot} ({o.TypeName}) for {Silver(price)} silver");
            return true;
        }
        return false;
    }

    /// <summary>GiveUpRealEstate: the player's own plot becomes free; the buildings on it go, the open bids on it come back to their bidders.</summary>
    public bool GiveUp(Player p, Guid plotId, out string why)
    {
        why = "";
        if (!Enabled) why = "real estate is off on this server";
        else if (!_plots.TryGetValue(plotId, out var plot) || plot.Owner != p.Character.Id) why = "this plot is not yours";
        else
        {
            Free(plot, "given up");
            Log.Info("world", $"'{p.Character.Name}' gave up {plot}");
            return true;
        }
        return false;
    }

    /// <summary>.plot free: a GM frees <paramref name="plot"/> as if its owner gave it up.</summary>
    public string ForceFree(RealEstatePlot plot)
    {
        var owner = plot.OwnerName;
        Free(plot, "taken back by a GM");
        Log.Info("world", $"{plot} of '{owner}' freed by a GM");
        return $"{plot} of {owner} is free";
    }

    /// <summary>A plot becomes free: its buildings come down, its row goes, its unsettled bids are given back, its object is shown free.</summary>
    private void Free(RealEstatePlot plot, string why)
    {
        foreach (var b in BuildingsOn(plot.Id).ToList()) Demolish(plot, b);
        _plots.Remove(plot.Id);
        store?.DeletePlot(plot.Id);
        foreach (var bid in BidsOn(plot.Id).Where(b => !b.Settled).ToList())
        {
            Pay(bid.Bidder, bid.Amount, $"bid on {plot} given back ({why})");
            Settle(bid);
            Notice(bid.Bidder, $"Auction for plot in {CityName(plot.Cluster)} cancelled",
                $"The plot you bid on in {CityName(plot.Cluster)} was {why}; the auction is cancelled.\n\nSilver returned:\n{Silver(bid.Amount)}");
        }
        plot.Owner = Guid.Empty;
        Changed(plot, plot.Id);
    }

    /// <summary>ChangeRealEstateOutline: the own plot's outline (gamedata OutlineTypes: Marker, Fence, Wall), its cost in gold (the client's "Cost").</summary>
    public bool ChangeOutline(Player p, Guid plotId, string? outline, out string why)
    {
        why = "";
        var index = Data.Outlines.ToList().FindIndex(o => string.Equals(o.Name, outline, StringComparison.OrdinalIgnoreCase));
        if (!Enabled) why = "real estate is off on this server";
        else if (!_plots.TryGetValue(plotId, out var plot) || plot.Owner != p.Character.Id) why = "this plot is not yours";
        else if (index < 0) why = $"no outline '{outline}'";
        else if ((long)Math.Round(Data.Outlines[index].Cost * Pricing.A7m) is var gold && !Wallet.TrySpendGold(p, gold, $"outline {outline}")) why = "not enough gold";
        else
        {
            plot.Outline = (byte)index;
            store?.SavePlot(plot);
            world.Saves.SaveNow(p.Character);
            Changed(plot, plot.Id);
            return true;
        }
        return false;
    }

    // ---- selling to another player ----

    /// <summary>SetSaleStatusForEstate: the owner lists the plot at <paramref name="price"/> (a7m) or takes it off sale (<paramref name="cancel"/>).</summary>
    public bool SetSale(Player p, long objectId, long price, bool cancel, out string why)
    {
        why = "";
        var (_, ownersOnly, end) = Window(world.GameNow);
        if (!Enabled) why = "real estate is off on this server";
        else if (ObjectOf(p, objectId)?.Plot is not { } plot || plot.Owner != p.Character.Id) why = "@REALESTATE_ERROR_SELL_NO_PERMISSION";
        else if (!cancel && world.GameNow >= ownersOnly && world.GameNow < end) why = "@REALESTATE_ERROR_CANT_SELL";
        else if (!cancel && price <= 0) why = "@REALESTATE_ERROR_CANT_SELL";
        else
        {
            plot.SalePrice = cancel ? RealEstatePlot.NotForSale : price / Pricing.A7m * Pricing.A7m;
            store?.SavePlot(plot);
            Changed(plot, plot.Id);
            Log.Info("world", $"'{p.Character.Name}' {(cancel ? "took {plot} off sale" : $"listed {plot} for {Silver(plot.SalePrice)} silver")}");
            return true;
        }
        return false;
    }

    /// <summary>BuyRealEstate: another player buys a listed plot at its asking price (<paramref name="price"/> must still be it); the buildings stay with the plot.</summary>
    public bool Buy(Player p, long objectId, long price, out string why)
    {
        why = "";
        var (_, ownersOnly, end) = Window(world.GameNow);
        if (!Enabled) why = "real estate is off on this server";
        else if (ObjectOf(p, objectId)?.Plot is not { SalePrice: >= 0 } plot) why = "@REALESTATE_ERROR_CANT_BUY";
        else if (plot.Owner == p.Character.Id) why = "@REALESTATE_ERROR_CANT_BUY";
        else if (world.GameNow >= ownersOnly && world.GameNow < end) why = "@REALESTATE_ERROR_CANT_BUY";
        else if (price != plot.SalePrice) why = "@REALESTATE_ERROR_BUY_PRICE_CHANGED";
        else if (!Wallet.TrySpend(p, plot.SalePrice, $"buying {plot}")) why = "@REALESTATE_ERROR_BUY_NOT_ENOUGH_SILVER";
        else
        {
            var seller = plot.Owner;
            Pay(seller, plot.SalePrice, $"{plot} sold");
            Notice(seller, $"Your plot in {CityName(plot.Cluster)} was sold",
                $"A plot you put up for sale in {CityName(plot.Cluster)} was successfully sold.\n\nNew owner:\n{p.Character.Name}\n\nSilver:\n{Silver(plot.SalePrice)}");
            Log.Info("world", $"'{p.Character.Name}' bought {plot} from {plot.OwnerName} for {Silver(plot.SalePrice)} silver");
            Hand(plot, p.Character.Id, p.Character.Name, plot.SalePrice, keepBuildings: true);
            world.Saves.SaveNow(p.Character);
            return true;
        }
        return false;
    }

    /// <summary>The plot to a new owner (a sale or an auction): not for sale, the outline back to the marker, its buildings kept (owner changed) or demolished.</summary>
    private void Hand(RealEstatePlot plot, Guid owner, string name, long price, bool keepBuildings)
    {
        if (!keepBuildings)
            foreach (var b in BuildingsOn(plot.Id).ToList()) Demolish(plot, b);
        plot.Owner = owner;
        plot.OwnerName = name;
        plot.Since = world.GameNow;
        plot.LastPrice = price;
        plot.SalePrice = RealEstatePlot.NotForSale;
        plot.Outline = 0;
        store?.SavePlot(plot);
        Changed(plot, plot.Id);
        if (world.Maps.Find(plot.Cluster) is { } map)
            foreach (var b in map.Objects.Values.OfType<Building>().Where(b => b.PlotBuilding?.Plot == plot.Id).ToList())
            {
                b.Owner = OwnerOf(plot);
                map.SendToObservers(b, b.NewBuilding());
            }
    }

    // ---- the auction ----

    /// <summary>
    /// RealEstateGetAuctionData (op 264, zp {0 the plot object}; the window's OnShow → cly.arg): aih {0 the asker's own bid (a7m, the client
    /// multiplies an owner's by ownerbidmultiplier), 1 the highest bidder's name, 2 the highest bid as it counts, 3 the start of the current
    /// cycle's auction, 4 the end of bidding for all but the owner, 5 its end} (SPEC §1.3); null for an unknown object.
    /// </summary>
    public RealEstateGetAuctionDataResponse? AuctionData(Player p, long objectId)
    {
        if (!Enabled || ObjectOf(p, objectId) is not { } o) return null;
        var (start, ownersOnly, end) = Window(world.GameNow);
        var bids = BidsOn(o.Guid).Where(b => b.Auction == start).ToList();
        var own = bids.FirstOrDefault(b => b.Bidder == p.Character.Id);
        var top = Highest(o.Plot, bids);
        // A settled auction: the winner is the owner now, its bid the plot's last price (what it counts depended on the owner before).
        if (world.GameNow >= end && o.Plot is { } plot && bids.FirstOrDefault(b => b.Settled && b.Bidder == plot.Owner && b.Amount == plot.LastPrice) is { } won)
            top = (won, won.Amount);
        return new RealEstateGetAuctionDataResponse
        {
            bhp = own?.Amount ?? 0,
            bhq = top?.Bid.BidderName,
            bhr = top?.Counts ?? 0,
            bhs = start, bht = ownersOnly, bhu = end,
        };
    }

    /// <summary>What <paramref name="bid"/> counts: an owner's bid on the own plot × the owner multiplier.</summary>
    public long Counts(RealEstatePlot? plot, RealEstateBid bid) =>
        plot is not null && bid.Bidder == plot.Owner ? (long)Math.Round(bid.Amount * OwnerMultiplier) : bid.Amount;

    /// <summary>The bid that wins among <paramref name="bids"/> (what it counts, the earliest of equal ones); null without bids.</summary>
    private (RealEstateBid Bid, long Counts)? Highest(RealEstatePlot? plot, IEnumerable<RealEstateBid> bids) =>
        bids.Select(b => (Bid: b, Counts: Counts(plot, b))).OrderByDescending(b => b.Counts).ThenBy(b => b.Bid.Placed).FirstOrDefault() is { Bid: not null } top ? top : null;

    /// <summary>
    /// RealEstateBidOnAuction (op 265, zo {0 the plot object, 1 the bid a7m}; RealEstateBidGui → cly.aqr 0x180B60F10): the player's whole bid
    /// (it replaces the earlier one, of which only the difference is taken) on an owned plot while its auction runs. Refused: a free or city plot,
    /// outside the auction, a non-owner in the final OwnerExclusive minutes, not above the own earlier bid, counting less than the plot's
    /// <see cref="Price"/> or not above the best other bid, not enough silver.
    /// </summary>
    public bool Bid(Player p, long objectId, long amount, out string why)
    {
        why = "";
        var now = world.GameNow;
        var (start, ownersOnly, end) = Window(now);
        amount = amount / Pricing.A7m * Pricing.A7m;
        if (!Enabled) { why = "real estate is off on this server"; return false; }
        if (ObjectOf(p, objectId) is not { } o) { why = "@REALESTATE_AUCTION_NO_AUCTION_FOUND"; return false; }
        if (o.Reserved || o.Plot is not { } plot) { why = o.Reserved ? $"this plot belongs to {o.ReservedBy}" : "this plot is free: claim it instead"; return false; }
        var owner = plot.Owner == p.Character.Id;
        if (now >= end) { why = $"the auction of this plot has ended; the next one starts {Date(start + Cycle.Ticks)}"; return false; }
        if (now >= ownersOnly && !owner)
        {
            why = $"Only the owner is allowed to bid in the final {OwnerExclusive.TotalHours:0} hours of the auction.";
            return false;
        }
        var key = (o.Guid, p.Character.Id, start);
        var earlier = _bids.GetValueOrDefault(key);
        var held = earlier is { Settled: false } ? earlier.Amount : 0;
        var bid = new RealEstateBid(o.Guid, p.Character.Id, p.Character.Name, amount, now, start, false);
        var counts = Counts(plot, bid);
        var others = BidsOn(o.Guid).Where(b => b.Auction == start && b.Bidder != p.Character.Id).ToList();
        if (amount <= (earlier?.Amount ?? 0)) why = "@REALESTATE_AUCTION_BID_HAS_TO_BE_HIGHER_THAN_PREVIOUS";
        else if (counts < Price(o)) why = $"the least bid on this plot is {Silver(owner ? (long)Math.Ceiling(Price(o) / OwnerMultiplier / Pricing.A7m) * Pricing.A7m : Price(o))} silver";
        else if (Highest(plot, others) is { } top && counts <= top.Counts) why = "@REALESTATE_AUCTION_ALREADY_HIGHER_BID";
        else if (!Wallet.TrySpend(p, amount - held, $"bid on {plot}")) why = "@REALESTATE_AUCTION_NOT_ENOUGH_SILVER";
        else
        {
            var before = Highest(plot, earlier is { Settled: false } ? others.Append(earlier) : others);
            _bids[key] = bid;
            store?.SaveBid(bid);
            world.Saves.SaveNow(p.Character);
            Log.Info("world", $"'{p.Character.Name}' bids {Silver(amount)} silver on {plot} (counts {Silver(counts)})");
            // The official mails: the bidder who led before and the owner hear of a new highest bidder.
            if (before is { } led && led.Bid.Bidder != p.Character.Id)
                Notice(led.Bid.Bidder, $"New high bidder for plot in {CityName(plot.Cluster)}",
                    $"The highest bidder for a plot you bid on in {CityName(plot.Cluster)} has changed.\n\nNew highest bidder:\n{p.Character.Name}");
            if (!owner && (before is null || before.Value.Bid.Bidder != plot.Owner))
                Notice(plot.Owner, $"New high bidder for your plot in {CityName(plot.Cluster)}",
                    $"The highest bidder for a plot you own in {CityName(plot.Cluster)} has changed.\n\nNew highest bidder:\n{p.Character.Name} has bid {Silver(amount)}\n\n"
                    + $"If the auction ends with this bid you will receive\n\nOwner's losing bonus:\n{Silver(Bonus(amount))}\n\nand your plot will be owned by {p.Character.Name}");
            return true;
        }
        return false;
    }

    private long Bonus(long winning) => (long)Math.Round(winning * world.Config.RealEstateOwnerLosingBonus / Pricing.A7m) * Pricing.A7m;

    /// <summary>
    /// Once every 30 s: the auctions that ended. A plot's open bids are settled when their auction's end has passed (<see cref="Resolve"/>);
    /// settled bids of auctions before the current one are dropped.
    /// </summary>
    public void Update()
    {
        if (!Enabled || world.Clock.Now < _nextUpdate) return;
        _nextUpdate = world.Clock.Now + TimeSpan.FromSeconds(30);
        var now = world.GameNow;
        var (start, _, end) = Window(now);
        foreach (var plot in _bids.Values.Where(b => !b.Settled && (b.Auction < start || now >= end)).Select(b => b.Plot).Distinct().ToList())
            Resolve(plot);
        if (_bids.Values.Any(b => b.Settled && b.Auction < start))
        {
            foreach (var k in _bids.Where(b => b.Value.Settled && b.Value.Auction < start).Select(b => b.Key).ToList()) _bids.Remove(k);
            store?.DeleteBidsBefore(start);
        }
    }

    /// <summary>
    /// Settles the open bids on <paramref name="plotId"/>: the highest (as it counts) wins and is kept, the others come back. Another player's
    /// win hands the plot over (the buildings on it come down, the previous owner gets RealEstate:OwnerLosingBonus of the winning bid); the
    /// owner's win keeps it. Every bidder and the previous owner get the official mail's facts. Returns a line for the log and the GM.
    /// </summary>
    public string Resolve(Guid plotId)
    {
        var open = BidsOn(plotId).Where(b => !b.Settled).ToList();
        if (open.Count == 0) return $"plot {plotId}: no open bids";
        if (!_plots.TryGetValue(plotId, out var plot))
        {
            foreach (var b in open)
            {
                Pay(b.Bidder, b.Amount, $"bid on plot {plotId} given back (free plot)");
                Settle(b);
            }
            return $"plot {plotId}: free, {open.Count} bid(s) given back";
        }
        var city = CityName(plot.Cluster);
        var previous = (plot.Owner, plot.OwnerName);
        var (win, counts) = Highest(plot, open)!.Value;
        foreach (var b in open)
        {
            Settle(b);
            if (b == win) continue;
            Pay(b.Bidder, b.Amount, $"losing bid on {plot}");
            var body = $"The auction of {(b.Bidder == previous.Owner ? "your plot" : "a plot you bid on")} in {city} has concluded, and you have lost.\n\n"
                + $"Previous owner:\n{previous.OwnerName}\n\nNew owner:\n{win.BidderName}\n\nYour final bid:\n{Silver(b.Amount)}\n\nWinning bid:\n{Silver(win.Amount)}\n\nSilver returned:\n{Silver(b.Amount)}";
            if (b.Bidder == previous.Owner && win.Bidder != previous.Owner) body += $"\n\nOwner's losing bonus:\n{Silver(Bonus(win.Amount))}";
            Notice(b.Bidder, $"Lost auction for {(b.Bidder == previous.Owner ? "your plot" : "plot")} in {city}", body);
        }
        Notice(win.Bidder, $"Won auction for {(win.Bidder == previous.Owner ? "your plot" : "plot")} in {city}",
            $"The auction of {(win.Bidder == previous.Owner ? "your plot you bid on" : "a plot you bid on")} in {city} has concluded, and you have won.\n\n"
            + $"Previous owner:\n{previous.OwnerName}\n\nNew owner:\n{win.BidderName}\n\nYour final bid:\n{Silver(win.Amount)}");
        if (win.Bidder == previous.Owner)
        {
            plot.LastPrice = win.Amount;
            store?.SavePlot(plot);
            Changed(plot, plot.Id);
            Log.Info("world", $"auction of {plot} ended: the owner '{previous.OwnerName}' keeps it for {Silver(win.Amount)} silver ({open.Count} bid(s))");
            return $"{plot}: the owner {previous.OwnerName} keeps it ({Silver(win.Amount)} silver, {open.Count} bid(s))";
        }
        var bonus = Bonus(win.Amount);
        Pay(previous.Owner, bonus, $"owner's losing bonus of {plot}");
        if (!open.Any(b => b.Bidder == previous.Owner))
            Notice(previous.Owner, $"Lost auction for your plot in {city}",
                $"The auction of your plot in {city} has concluded, and you have lost.\n\nPrevious owner:\n{previous.OwnerName}\n\nNew owner:\n{win.BidderName}\n\n"
                + $"Winning bid:\n{Silver(win.Amount)}\n\nOwner's losing bonus:\n{Silver(bonus)}");
        Hand(plot, win.Bidder, win.BidderName, win.Amount, keepBuildings: false);
        Log.Info("world", $"auction of {plot} ended: '{win.BidderName}' wins it from '{previous.OwnerName}' for {Silver(win.Amount)} silver (counts {Silver(counts)}); bonus {Silver(bonus)}");
        return $"{plot}: {win.BidderName} wins it from {previous.OwnerName} for {Silver(win.Amount)} silver ({open.Count} bid(s))";
    }

    private void Settle(RealEstateBid bid)
    {
        var settled = bid with { Settled = true };
        _bids[(bid.Plot, bid.Bidder, bid.Auction)] = settled;
        store?.SaveBid(settled);
    }

    /// <summary>.plot auction start: the current cycle's auction begins now (the schedule is shifted in memory). Returns its end.</summary>
    public string StartAuctionNow()
    {
        Anchor = new DateTime(world.GameNow, DateTimeKind.Utc);
        _endedEarly = null;
        var (_, ownersOnly, end) = Window(world.GameNow);
        return $"auction started for every owned plot: bidding until {Date(ownersOnly)}, the owner until {Date(end)}";
    }

    /// <summary>.plot auction end: the running auction ends now (in memory: its window shows it over) and every plot's bids are settled.</summary>
    public string EndAuctionNow()
    {
        var now = world.GameNow;
        var (start, _, end) = Window(now);
        if (now < end) _endedEarly = (start, now);
        var lines = _bids.Values.Where(b => !b.Settled).Select(b => b.Plot).Distinct().ToList().Select(Resolve).ToList();
        return lines.Count == 0 ? "auction ended: no open bids" : "auction ended:\n" + string.Join("\n", lines);
    }

    // ---- money and mail ----

    /// <summary>Silver to a character: into its wallet when it plays, else owed to it at its next Join (the market's credits).</summary>
    private void Pay(Guid character, long raw, string why)
    {
        if (raw <= 0 || character == Guid.Empty) return;
        if (world.Parties.Online(character) is { } p)
        {
            Wallet.Credit(p, raw, why);
            world.Saves.SaveNow(p.Character);
        }
        else if (world.Market is { } market) market.Batch(b => b.Credit(character, raw));
        else Log.Warn("world", $"{Silver(raw)} silver for {character} ({why}) lost: no market store to keep it");
    }

    /// <summary>A real-estate mail (category RealEstate, plain text with the official mails' facts).</summary>
    private void Notice(Guid to, string subject, string body)
    {
        if (to != Guid.Empty) Mailer.Send(world, to, guild: false, MailKind.Normal, MailCategory.RealEstate, "Real Estate", subject, body);
    }

    private static string Date(long ticks) => new DateTime(ticks, DateTimeKind.Utc).ToString("yyyy-MM-dd HH:mm 'UTC'", CultureInfo.InvariantCulture);

    // ---- buildings on a plot ----

    /// <summary>Whether a player may put up <paramref name="template"/> on a city plot: a buildings.xml crafting station (craftbuilding), not a tutorial one.</summary>
    public static bool Buildable(BuildingTemplate template) =>
        template.Kind == "craftbuilding" && !template.Name.EndsWith("_TUTORIAL", StringComparison.OrdinalIgnoreCase);

    private BuildingOwner? OwnerOf(RealEstatePlot plot) =>
        plot.Owned ? new BuildingOwner(plot.Owner, plot.OwnerName, GuildOf(plot.Owner)?.Id ?? Guid.Empty, GuildOf(plot.Owner)?.Name ?? "") : null;

    private Building? MakeBuilding(string cluster, RealEstateBuilding b, long loadTime)
    {
        if (world.Clusters?.Buildings.Find(b.Name) is not { } template || !_plots.TryGetValue(b.Plot, out var plot)) return null;
        var spawn = new BuildingSpawn
        {
            Key = new SpawnKey(cluster, "plot", (int)(b.Built % int.MaxValue)), Tile = template.Name, Position = new Vector2(b.X, b.Z), Direction = b.Rotation,
            Component = new XElement("building", new XAttribute("name", template.Name)), Guid = b.Id, Template = template,
        };
        return new Building(world.ObjectIds.Next(), spawn, b.Built, loadTime) { Owner = OwnerOf(plot), PlotBuilding = b };
    }

    /// <summary>
    /// The checks of ConstructionSiteCreate in a city: an own plot under <paramref name="at"/>, building type <paramref name="typeId"/> a
    /// crafting station (<see cref="Buildable"/>), a free 20 m square of the plot nearest <paramref name="at"/>, its placecost silver and its
    /// &lt;craftingrequirements&gt; in the inventory. Null template = refused, with the reason.
    /// </summary>
    public (BuildingTemplate? Template, RealEstateObject? Plot, Vector2 Centre, long Silver, List<(ItemTemplate Item, int Count)> Cost, string Why) CheckConstruct(Player p, int typeId, Vector2 at)
    {
        List<(ItemTemplate, int)> cost = [];
        if (!Enabled || p.Map is not { } map) return (null, null, default, 0, cost, "not on a city plot");
        var o = map.Objects.Values.OfType<RealEstateObject>().Where(o => o.Contains(at)).MinBy(o => Vector2.Distance(o.Position, at));
        if (o is null) return (null, null, default, 0, cost, "not on a city plot");
        if (o.Plot is not { } plot || plot.Owner != p.Character.Id) return (null, null, default, 0, cost, "this plot is not yours");
        if (typeId is < short.MinValue or > short.MaxValue || world.Clusters?.Buildings[(short)typeId] is not { } template) return (null, null, default, 0, cost, $"no building type {typeId}");
        if (!Buildable(template)) return (null, null, default, 0, cost, $"{template.Name} ({template.Kind}) cannot be built on a city plot (crafting stations only)");
        var taken = BuildingsOn(plot.Id).ToList();
        var half = BuildingSize / 2;
        Vector2? best = null;
        for (var x = o.Position.X - o.Size.X / 2 + half; x <= o.Position.X + o.Size.X / 2 - half + 0.01f; x += BuildingSize)
            for (var z = o.Position.Y - o.Size.Y / 2 + half; z <= o.Position.Y + o.Size.Y / 2 - half + 0.01f; z += BuildingSize)
            {
                var c = new Vector2(x, z);
                if (taken.Any(b => MathF.Abs(b.X - c.X) < BuildingSize - 0.01f && MathF.Abs(b.Z - c.Y) < BuildingSize - 0.01f)) continue;
                if (best is { } bb && Vector2.Distance(bb, at) <= Vector2.Distance(c, at)) continue;
                best = c;
            }
        if (best is not { } centre) return (null, null, default, 0, cost, "the plot has no free 20 x 20 m square left");
        foreach (var (name, count) in Islands.BuildCost(template))
        {
            if (world.Items.Find(name) is not { } item) return (null, null, default, 0, cost, $"{template.Name} needs {name}, which items.xml does not have");
            var have = p.Inventory.Bag.Items.Where(i => i.Template == item).Sum(i => i.Count);
            if (have < count) return (null, null, default, 0, cost, $"{template.Name} needs {count} x {name}, the inventory has {have}");
            cost.Add((item, count));
        }
        var silver = Islands.PlaceCost(template);
        if (p.Character.Silver < silver) return (null, null, default, 0, cost, $"{p.Character.Silver / Pricing.A7m} silver, placing {template.Name} costs {silver / Pricing.A7m}");
        return (template, o, centre, silver, cost, "");
    }

    /// <summary>Puts up <paramref name="template"/> at <paramref name="centre"/> of the own plot <paramref name="o"/>: saved and shown. The caller takes the cost.</summary>
    public Building? Build(RealEstateObject o, BuildingTemplate template, Vector2 centre, float rotation)
    {
        if (o.Plot is not { } plot || o.Map is not { } map) return null;
        var b = new RealEstateBuilding(Guid.NewGuid(), plot.Id, template.Name, centre.X, centre.Y, rotation, world.GameNow);
        _buildings[b.Id] = b;
        store?.SaveRealEstateBuilding(b);
        if (MakeBuilding(map.Cluster, b, map.CreatedTicks) is not { } building) return null;
        map.AddObject(building);
        Log.Info("world", string.Create(CultureInfo.InvariantCulture, $"{plot.OwnerName} built {template.Name} on {plot} at ({centre.X:0.#}, {centre.Y:0.#})"));
        return building;
    }

    /// <summary>BuildingGiveup's checks for a station on a city plot: the player's own. The building, or the refusal; null building and empty reason = not a plot building.</summary>
    public (Building? Building, string Why) CheckGiveup(Player p, long id)
    {
        if (p.Map?.Objects.GetValueOrDefault(id) is not Building { PlotBuilding: { } b } building) return (null, "");
        if (!_plots.TryGetValue(b.Plot, out var plot) || plot.Owner != p.Character.Id) return (null, $"the {building.Template.Name} is not yours");
        return (building, "");
    }

    /// <summary>A station on a plot comes down (given up, the plot freed or lost at auction): out of the store and off the map; nothing comes back.</summary>
    public void Demolish(RealEstatePlot plot, RealEstateBuilding b)
    {
        _buildings.Remove(b.Id);
        store?.DeleteRealEstateBuilding(b.Id);
        if (world.Maps.Find(plot.Cluster) is { } map && map.Objects.Values.OfType<Building>().FirstOrDefault(o => o.PlotBuilding?.Id == b.Id) is { } o)
            map.RemoveObject(o);
    }

    /// <summary>A character's plots, bids and buildings go with it (the rows follow by ON DELETE CASCADE).</summary>
    public void CharacterDeleted(Guid character)
    {
        foreach (var plot in _plots.Values.Where(p => p.Owner == character).ToList())
        {
            foreach (var b in BuildingsOn(plot.Id).ToList()) _buildings.Remove(b.Id);
            _plots.Remove(plot.Id);
            plot.Owner = Guid.Empty;
            Changed(plot, plot.Id);
        }
        foreach (var k in _bids.Where(b => b.Value.Bidder == character).Select(b => b.Key).ToList()) _bids.Remove(k);
    }

    /// <summary>The plots of <paramref name="map"/> for .plot list: owned ones with owner, price and bids, then the counts.</summary>
    public IEnumerable<string> Describe(Map map)
    {
        var objects = map.Objects.Values.OfType<RealEstateObject>().ToList();
        var (start, ownersOnly, end) = Window(world.GameNow);
        yield return $"{map.Cluster}: {objects.Count} plots, {objects.Count(o => o.Plot is not null)} owned, {objects.Count(o => o.Reserved)} held by the city; "
            + $"auction {Date(start)} - {Date(end)} (owners only from {Date(ownersOnly)}){(world.GameNow < end ? ", running" : "")}";
        foreach (var o in objects.Where(o => o.Plot is not null))
            yield return $"  #{o.Id} {o.TypeName} at ({o.Position.X:0}, {o.Position.Y:0}): {o.Plot!.OwnerName}, last {Silver(o.Plot.LastPrice)}"
                + (o.Plot.SalePrice >= 0 ? $", for sale {Silver(o.Plot.SalePrice)}" : "")
                + $", {BidsOn(o.Guid).Count(b => !b.Settled)} open bid(s), {BuildingsOn(o.Guid).Count()} building(s)";
    }
}
