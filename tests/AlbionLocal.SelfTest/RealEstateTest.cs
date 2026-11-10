using System.Numerics;
using AlbionLocal.Photon.Serialization;
using AlbionLocal.Server;
using AlbionLocal.Server.Content;
using AlbionLocal.Server.Entities;
using AlbionLocal.Server.Game;
using AlbionLocal.Server.Network;
using AlbionLocal.Server.Persistence;
using AlbionLocal.Server.Protocol;
using static AlbionLocal.SelfTest.AlbionTests;

namespace AlbionLocal.SelfTest;

/// <summary>
/// The city plots (public issue #18, analysis/work/realestate/SPEC.md) on the client's cluster data: Thetford's 80 plots, a claim, an auction
/// between the owner and another player, a station on the won plot, the reload and the give-up, through FakePeer sessions.
/// </summary>
public static class RealEstateTest
{
    public static IEnumerable<(string, Action)> All =>
    [
        ("real estate: Thetford's plots (NewRealEstate free / city), claim, auction (owner x2, outbid, settled: winner owns, losers paid back + bonus, mails), a station on the plot, reload, give-up", MainPath),
    ];

    private static T Fill<T>(OperationResponse r) where T : new()
    {
        var m = new T();
        AlbionSerializer.FillFields(m, r.Parameters);
        return m;
    }

    private static void MainPath()
    {
        var store = Client.Clusters!;
        var template = store.Get("0000");
        var stations = StationSpawns.Load(Path.Combine(DataDir(), "..", "content", "spawns"));
        var dir = NewDir();
        var characters = Repository(dir);
        var clock = new ManualClock();
        var anchor = DateTime.UtcNow.Ticks;
        World MakeWorld() => new(new ServerConfig { JoinByCharacterId = true }, characters, Client.Items, Client.GameData, Client.Spells)
        {
            Sync = new object(), Clusters = store, Clock = clock, GameTicks = () => anchor + clock.Now.Ticks, Stations = stations, MapSeed = 5,
        };
        var world = MakeWorld();
        world.MobAi = false;

        // A free 20 x 20 plot of quality E (5 000 000 silver) away from the stations.
        var spot = template.RealEstates.First(r => r.Size == new Vector2(20, 20) && r.Quality == "E"
            && stations.For("0000").All(s => Vector2.Distance(s.Position, r.Position) > 30));
        Character Hero(string prefix, long silver, params (string Item, int Count)[] items)
        {
            var c = StarterKit.Default.NewCharacter(prefix + Guid.NewGuid().ToString("N")[..10]);
            (c.Cluster, c.PositionX, c.PositionY, c.Silver) = ("0000", spot.Position.X, spot.Position.Y, silver);
            foreach (var (item, count) in items) c.Inventory.Add(new InventoryItem(item, count));
            Check(characters.Create(c), "character created");
            return c;
        }
        const long M = 1_000_000 * Pricing.A7m;
        var a = Hero("A", 30 * M);
        var b = Hero("B", 30 * M, ("T1_WOOD", 25), ("T1_ROCK", 3), ("T2_STONEBLOCK", 150));
        (WorldSession, FakePeer) Join(Character c)
        {
            var peer = new FakePeer();
            var session = new WorldSession(peer, world);
            lock (world.Sync) session.OnRequest(Request(new JoinRequest { ss = c.Id }, null), encrypted: true);
            Check(peer.Responses is [{ ReturnCode: 0 }], "join answered");
            return (session, peer);
        }
        var (sa, pa) = Join(a);
        var (sb, pb) = Join(b);
        OperationResponse Call(WorldSession s, FakePeer peer, AlbionRequest req)
        {
            var before = peer.Responses.Count;
            lock (world.Sync) s.OnRequest(Request(req, 7), encrypted: false);
            return peer.Responses.Skip(before).Single();
        }
        var map = world.Maps.Find("0000")!;
        var plots = map.Objects.Values.OfType<RealEstateObject>().ToList();
        var plot = plots.Single(o => o.Guid == spot.Guid);
        var free = plot.NewRealEstate();
        Check(plots.Count == 80 && plots.Count(o => o.Reserved) is >= 10 and var held && held == plots.Count(o => stations.For("0000").Any(st => o.Contains(st.Position))) && plots.Where(o => o.Reserved).All(o => o.NewRealEstate().c9l == RealEstateObject.CityOwner)
              && free.c9l == Guid.Empty && free.c9r == RealEstatePlot.NotForSale && free.c9k == new Vector2(20, 20) && sa.Player!.Visibility.Knows(plot)
              && world.RealEstates.Price(plot) == 5 * M,
              $"Thetford: {plots.Count} plots, {plots.Count(o => o.Reserved)} held by the city (stations {stations.For("0000").Count}); the free plot without owner, not for sale, "
              + $"in view, price {world.RealEstates.Price(plot) / Pricing.A7m}");

        // The claim: paid, owned, B sees the new owner.
        var seen = pb.Events.Count;
        var claim = Call(sa, pa, new ClaimRealEstateRequest { fy = plot.Guid });
        var update = pb.Events.Skip(seen).FirstOrDefault(e => e.Parameters[ReservedKeys.EventCode] is (short)191);
        Check(claim.ReturnCode == 0 && plot.Plot?.Owner == a.Id && sa.Player!.Character.Silver == 25 * M && update is not null
              && Call(sb, pb, new ClaimRealEstateRequest { fy = plot.Guid }).ReturnCode == 1,
              $"claim RC {claim.ReturnCode}: A owns it, 25 M silver left (has {sa.Player!.Character.Silver / Pricing.A7m}), B got NewRealEstate with the owner; B's claim refused");

        // The auction: B outbids the plot price, A's own bid counts twice, B raises; the data shows each their own bid and the leader.
        world.RealEstates.StartAuctionNow();
        var low = Call(sb, pb, new RealEstateBidOnAuctionRequest { y7 = plot.Id, y8 = 4 * M });
        var b1 = Call(sb, pb, new RealEstateBidOnAuctionRequest { y7 = plot.Id, y8 = 6 * M });
        var a1 = Call(sa, pa, new RealEstateBidOnAuctionRequest { y7 = plot.Id, y8 = 4 * M });
        var b2 = Call(sb, pb, new RealEstateBidOnAuctionRequest { y7 = plot.Id, y8 = 9 * M });
        var data = Fill<RealEstateGetAuctionDataResponse>(Call(sa, pa, new RealEstateGetAuctionDataRequest { y9 = plot.Id }));
        Check(low.ReturnCode == 1 && b1.ReturnCode == 0 && a1.ReturnCode == 0 && b2.ReturnCode == 0 && sb.Player!.Character.Silver == 21 * M && sa.Player!.Character.Silver == 21 * M
              && data.bhp == 4 * M && data.bhq == b.Name && data.bhr == 9 * M && data.bhs <= world.GameNow && data.bhu > data.bht && data.bht > world.GameNow,
              $"bids: under the price RC {low.ReturnCode}, B 6 M RC {b1.ReturnCode}, A 4 M (x2) RC {a1.ReturnCode}, B 9 M RC {b2.ReturnCode}; held: A {sa.Player.Character.Silver / Pricing.A7m}, "
              + $"B {sb.Player.Character.Silver / Pricing.A7m}; A's data: own {data.bhp / Pricing.A7m}, leader {data.bhq} {data.bhr / Pricing.A7m}");

        // The end: B wins and pays 9 M; A gets the 4 M bid back and half of 9 M; both have mail.
        var mailA = world.Mail!.UnreadMails(a.Id, false);
        world.RealEstates.EndAuctionNow();
        var over = Fill<RealEstateGetAuctionDataResponse>(Call(sa, pa, new RealEstateGetAuctionDataRequest { y9 = plot.Id }));
        Check(over.bhu <= world.GameNow && over.bhq == b.Name && over.bhr == 9 * M && over.bhp == 4 * M
              && Call(sb, pb, new RealEstateBidOnAuctionRequest { y7 = plot.Id, y8 = 10 * M }).ReturnCode == 1,
              $"after the end the window shows it over: winner {over.bhq} {over.bhr / Pricing.A7m}, A's final bid {over.bhp / Pricing.A7m}; no more bids");
        Check(plot.Plot?.Owner == b.Id && plot.Plot.LastPrice == 9 * M && sb.Player.Character.Silver == 21 * M && sa.Player.Character.Silver == 29.5 * M
              && world.Mail.UnreadMails(a.Id, false) > mailA && world.Mail.UnreadMails(b.Id, false) > 0 && world.RealEstates.BidsOn(plot.Guid).All(x => x.Settled),
              $"settled: owner {plot.Plot?.OwnerName}, last price {plot.Plot?.LastPrice / Pricing.A7m}; A {sa.Player.Character.Silver / Pricing.A7m} (29.5 M), B {sb.Player.Character.Silver / Pricing.A7m} (21 M)");

        // A station on the won plot (the build menu's op 68), with B as its owner.
        var smelter = store.Buildings.Find("T2_SMELTER")!;
        var built = Call(sb, pb, new ConstructionSiteCreateRequest { g3 = smelter.Id, g4 = [plot.Position.X + 3, plot.Position.Y - 2], g5 = 0 });
        var station = map.Objects.Values.OfType<Building>().SingleOrDefault(o => o.PlotBuilding?.Plot == plot.Guid);
        var refused = Call(sa, pa, new ConstructionSiteCreateRequest { g3 = smelter.Id, g4 = [plot.Position.X, plot.Position.Y], g5 = 0 });
        Check(built.ReturnCode == 0 && station is { Template.Name: "T2_SMELTER" } && station.Position == plot.Position && station.NewBuilding().cto == b.Id
              && Fill<ConstructionSiteCreateResponse>(built).aoj == station.Id && sb.Player.Character.Silver == 21 * M - 100 * Pricing.A7m && refused.ReturnCode == 1,
              $"station RC {built.ReturnCode} at {station?.Position} (plot {plot.Position}), owner key {station?.NewBuilding().cto}; A's on B's plot RC {refused.ReturnCode}");

        // The state is saved: a new world over the same database has the owner and the station.
        var again = MakeWorld();
        Check(again.RealEstates.Find(plot.Guid) is { } saved && saved.Owner == b.Id && saved.LastPrice == 9 * M && again.RealEstates.BuildingsOn(plot.Guid).Count() == 1,
              "reloaded: B owns the plot with its station");
        again.Dispose();

        // Give-up: the plot is free again and the station gone.
        var gone = Call(sb, pb, new GiveUpRealEstateRequest { o6 = plot.Guid });
        Check(gone.ReturnCode == 0 && plot.Plot is null && !map.Objects.Values.OfType<Building>().Any(o => o.PlotBuilding is not null) && world.RealEstates.Find(plot.Guid) is null,
              $"give-up RC {gone.ReturnCode}: free, station gone");
        world.Dispose();
        characters.Dispose();
        try { Directory.Delete(dir, true); } catch (IOException) { }
    }

    private static void Check(bool cond, string what) => AlbionTests.Check(cond, what);
}
