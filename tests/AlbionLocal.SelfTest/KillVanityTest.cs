using System.Text.Json;
using AlbionLocal.Photon.Serialization;
using AlbionLocal.Server.Commands;
using AlbionLocal.Server.Entities;
using AlbionLocal.Server.Game;
using AlbionLocal.Server.Network;
using AlbionLocal.Server.Persistence;
using AlbionLocal.Server.Protocol;
using AlbionLocal.Server.Systems;
using static AlbionLocal.SelfTest.AlbionTests;
using static AlbionLocal.SelfTest.CombatTest;

namespace AlbionLocal.SelfTest;

/// <summary>StageR kill-vanity (analysis/work/stageR/kill-vanity/SPEC.md): victory emotes, kill trophies, a loadout from the market, the quick sell.</summary>
public static class KillVanityTest
{
    public static IEnumerable<(string, Action)> All =>
    [
        ("kill emotes: ids from killemotes.xml, a charge item (517), GetVanityConsumables, gold purchase (a wrong price refused), equip, a kill plays it (512, -1 charge, Leave), reclaim refused", Emotes),
        ("kill trophies: the black zone's KILLTROPHY_BLACK, tiers rolled from the highest, the trophy item (NewKillTrophyItem 34)", Trophies),
        ("loadout from the market: cheapest offers per slot (458), a changed offer buys nothing and sends new offers, then all bought at once into the inventory (459)", LoadoutBuy),
        ("quick sell (489): the query's row sold into its buy order, a stale row skipped, silver after the answer", QuickSell),
    ];

    private static void Check(bool cond, string what) => AlbionTests.Check(cond, what);

    /// <summary>A random whose every roll hits.</summary>
    private sealed class Always : Random
    {
        public override double NextDouble() => 0;
    }

    private static void Emotes()
    {
        using var z = new QolTest.Zone();
        var world = z.World;
        var ke = world.Vanity.KillEmotes;
        var (a, b) = (z.A.S.Player!, z.B.S.Player!);
        var flag = ke.Data.Emote("KILL_EMOTE_FLAG")!;
        var hammer = ke.Data.Emote("KILL_EMOTE_HAMMER")!;
        Check(ke.Data.Emotes.Count == 10 && flag is { Id: 1, Visual: 0, GoldPrice: 200, BundledCharges: 100, Duration: 8 } && hammer is { Id: 10, Visual: 9, GoldPrice: 600 }
              && ke.Data.MaxTriggerDistance == 25, $"killemotes.xml: 10 emotes, FLAG id 1 / visual 0 (200 gold per 100), HAMMER id 10 / visual 9 (got {ke.Data.Emotes.Count})");
        Check(z.Send(z.A, new GetVanityConsumablesRequest()) is { ReturnCode: 0 } none && (!none.Parameters.ContainsKey(0) || none.Parameters[0] is short[] { Length: 0 }),
              "GetVanityConsumables without charges: none");

        // A charge item (consumespell T1_KILL_EMOTE_ADD_CHARGES_FLAG: addvanityconsumablecharges amount 1).
        var gm = new CommandContext(world, a, AccountLevel.GameMaster, (_, _) => { });
        lock (world.Sync) world.Commands.Execute(gm, ".item add T1_KILL_EMOTE_FLAG_CHARGES_NONTRADABLE 2");
        var stack = a.Inventory.Bag.Items.First(i => i.Template.Name == "T1_KILL_EMOTE_FLAG_CHARGES_NONTRADABLE");
        var am = z.A.P.Events.Count;
        z.Send(z.A, new UseConsumableFromInventoryRequest { ah5 = stack.Id, ah6 = 1 });
        z.Run(1.5);
        Check(KillEmotes.Charges(a.Character, flag) == 1 && stack.Count == 1
              && Since(z.A.P, am).Any(e => e.Code == EventCodes.ConsumableVanityChargesAdded && L(e.P, 0) == flag.Id && L(e.P, 1) == 1),
              $"using a Guild Banner charge item: 1 charge, ConsumableVanityChargesAdded 517 {{0 1, 1 1}}, one item left (charges {KillEmotes.Charges(a.Character, flag)}, items {stack.Count})");
        Check(z.Send(z.A, new GetVanityConsumablesRequest()) is { Parameters: var list } && list[0] is short[] and [1] && list[1] is int[] and [1],
              "GetVanityConsumables: {0 [1], 1 [1]}");

        // Gold purchase: 2 bundles of the banner (2 × 200) and 1 of the hammer (600) = 1000 gold; a wrong price changes nothing.
        lock (world.Sync) Wallet.Set(a, a.Character.Silver, 1000 * 10000L);
        Check(z.Send(z.A, new BuyVanityConsumableChargesRequest { ail = 0, aim = [flag.Id, hammer.Id], ain = [2, 1], aio = 500 * 10000L }) is { ReturnCode: KillEmotes.Refused }
              && a.Character.Gold == 1000 * 10000L && KillEmotes.Charges(a.Character, hammer) == 0, "a price below the server's (500 for 1000 gold): refused, nothing taken or given");
        am = z.A.P.Events.Count;
        Check(z.Send(z.A, new BuyVanityConsumableChargesRequest { ail = 0, aim = [flag.Id, hammer.Id], ain = [2, 1], aio = 1000 * 10000L }) is { ReturnCode: 0 }
              && a.Character.Gold == 0 && KillEmotes.Charges(a.Character, flag) == 201 && KillEmotes.Charges(a.Character, hammer) == 100
              && Since(z.A.P, am).Count(e => e.Code == EventCodes.ConsumableVanityChargesAdded) == 2,
              $"the right price: 1000 gold gone, +200 banner and +100 hammer charges, two 517 (gold {a.Character.Gold})");

        // Equip, the knockdown setting, Join key 119.
        Check(z.Send(z.A, new EquipKillEmoteRequest { aiq = flag.Id }) is { ReturnCode: 0 } equipped && L(equipped.Parameters, 0) == flag.Id && ke.Equipped(a.Character) == flag.Id
              && z.Send(z.A, new EquipKillEmoteRequest { aiq = 99 }) is { ReturnCode: KillEmotes.Refused } && ke.Equipped(a.Character) == flag.Id,
              "EquipKillEmote 1: {0 1}, kept (Join 119); an unknown id: refused");
        z.Send(z.A, new ChangeKillEmotePlayOnKnockdownSettingRequest { aip = false });
        bool knock;
        lock (world.Sync) knock = ke.Played(a, b, knockdown: true);
        Check(!knock && a.Character.Vanity!.KillEmoteOnKnockdown == false, "knockdown setting off: a knockdown plays nothing");

        // The kill: NewVisualEventObject to both, one charge used, a Leave at its end.
        am = z.A.P.Events.Count;
        var bm = z.B.P.Events.Count;
        lock (world.Sync) Death.Kill(b, a, a.Id, a.Character.Name, Death.Cause.NoLoss, false, world.GameNow);
        var shown = Since(z.B.P, bm).Where(e => e.Code == EventCodes.NewVisualEventObject).Select(e => e.P).ToList();
        static long K(Dictionary<byte, object?> p, byte k) => p.ContainsKey(k) ? L(p, k) : 0;
        Check(shown is [var v] && L(v, 0) == a.Id && L(v, 1) == b.Id && K(v, 5) == flag.Visual && K(v, 6) == 0 && K(v, 8) - K(v, 7) == SpellCasts.Seconds(8)
              && Since(z.A.P, am).Any(e => e.Code == EventCodes.NewVisualEventObject) && KillEmotes.Charges(a.Character, flag) == 200
              && Since(z.A.P, am).Any(e => e.Code == EventCodes.ConsumableVanityChargesAdded && L(e.P, 1) == -1),
              $"a kill: NewVisualEventObject {{0 killer, 1 victim, 5 visual 0, 6 KILL_EMOTE, 8 − 7 = 8 s}} to both, 517 −1, 200 left (got {shown.Count})");
        var visual = L(shown[0], 2);
        bm = z.B.P.Events.Count;
        z.Run(8.5);
        Check(Since(z.B.P, bm).Any(e => e.Code == EventCodes.Leave && L(e.P, 0) == visual), "its Leave after the 8 s");

        // ReclaimVanityItem: nothing is reclaimable here.
        Check(z.Send(z.A, new ReclaimVanityItemRequest { air = 239 }) is { ReturnCode: WorldSession.VanityNotUnlocked }, "ReclaimVanityItem of an unlock the account lacks: 0x4999 not unlocked");
    }

    private static void Trophies()
    {
        using var z = new QolTest.Zone("2305"); // Northstrand Dunes, OPENPVP_BLACK_1 (archetype BLACK)
        var world = z.World;
        var a = z.A.S.Player!;
        var rules = Pvp.RulesOf(world, a.Map);
        var kt = world.Vanity.KillTrophies;
        Check(rules is { KillTrophy: "KILLTROPHY_BLACK", KillTrophyDenominator: 1 }, $"the black zone's archetype: killtrophy KILLTROPHY_BLACK, denominator 1 (got {rules.KillTrophy})");
        var table = world.Vanity.KillEmotes.Data.Trophies["KILLTROPHY_BLACK"];
        kt.Random = new Always();
        const string Small = "UNIQUE_FURNITUREITEM_KILLTROPHY_OPENWORLD_SMALL", Large = "UNIQUE_FURNITUREITEM_KILLTROPHY_OPENWORLD_LARGE";
        Check(kt.Roll(table, 30000, 0, 1) == Large && kt.Roll(table, 14000, 0, 1) == "UNIQUE_FURNITUREITEM_KILLTROPHY_OPENWORLD_MEDIUM"
              && kt.Roll(table, 7000, 0, 1) == Small && kt.Roll(table, 6000, 2, 1) is null,
              "the tiers from the highest: 30000 item value → large, 14000 → medium, 7000 → small, below 6912 → none");
        kt.Random = new Random(3);
        var hits = Enumerable.Range(0, 2000).Count(_ => kt.Roll(table, 7000, 0, 1) is not null);
        Check(hits is > 180 and < 330, $"7000 / 55296 = 12.7 % per kill (got {hits} of 2000)");
        var am = z.A.P.Events.Count;
        string? given;
        lock (world.Sync) given = kt.Give(a, Small, "Bob");
        var type = world.Items.Find(Small)!;
        Check(given == Small && a.Inventory.Bag.Items.Any(i => i.Template == type)
              && Since(z.A.P, am).Any(e => e.Code == EventCodes.NewKillTrophyItem && L(e.P, 1) == type.Index),
              "the trophy: a Combat Memento in the inventory, sent with NewKillTrophyItem 34");
    }

    /// <summary>A loadout of a T4 sword in the main hand and 5 T4 ore in the inventory, made by <paramref name="who"/>; its id.</summary>
    private static string SwordLoadout(EconomyTest.Town town, (WorldSession S, FakePeer P, Player Player) who)
    {
        OperationResponse Call(AlbionRequest r)
        {
            lock (town.World.Sync) who.S.OnRequest(Request(r, 5), encrypted: false);
            return who.P.Responses[^1];
        }
        var me = who.Player.Character.Id;
        var id = (string)Call(new LoadoutCreateRequest { tw = me, tx = "Fighter", ty = 1, tz = 1 }).Parameters[0]!;
        var names = new string[10];
        Array.Fill(names, "");
        names[0] = "T4_MAIN_SWORD";
        Call(new LoadoutUpdateRequest { ud = me, ue = id, uf = 0, ug = "Fighter", uh = 1, ui = 1, uj = names, uk = [1, 0, 0, 0, 0, 0, 0, 0, 0, 0], uo = [2, 2, 2, 2, 2, 2, 2, 2, 2, 2],
            us = ["T4_ORE"], ut = [0], uu = [5] });
        return id;
    }

    private static void LoadoutBuy()
    {
        using var town = new EconomyTest.Town();
        var world = town.World;
        var seller = town.Join("Seller", 1000, new InventoryItem("T4_MAIN_SWORD", 1), new InventoryItem("T4_MAIN_SWORD", 1), new InventoryItem("T4_ORE", 5), new InventoryItem("T4_ORE", 3));
        var buyer = town.Join("Buyer", 1000);
        var rival = town.Join("Rival", 1000);
        var market = town.Station("MARKETPLACE");
        OperationResponse Call((WorldSession S, FakePeer P, Player Player) who, AlbionRequest r)
        {
            lock (world.Sync) who.S.OnRequest(Request(r, 7), encrypted: false);
            return who.P.Responses[^1];
        }
        const long A7m = 10000;
        var bag = seller.Player.Inventory.Bag;
        var swords = bag.Items.Where(i => i.Template.Name == "T4_MAIN_SWORD").ToList();
        var ores = bag.Items.Where(i => i.Template.Name == "T4_ORE").OrderByDescending(i => i.Count).ToList();
        foreach (var (item, amount, price) in new[] { (swords[0], 1, 100), (swords[1], 1, 150), (ores[0], 5, 10), (ores[1], 3, 20) })
            Check(Call(seller, new AuctionCreateOfferRequest { bqs = market.Id, bn5 = amount, bn6 = item.Id, bn7 = price * A7m, bn8 = 24 }) is { ReturnCode: 0 }, $"offer {amount} x {item.Template.Name} at {price}");
        long IdOf(string json) => JsonDocument.Parse(json).RootElement.GetProperty("Id").GetInt64();
        long UnitOf(string json) => JsonDocument.Parse(json).RootElement.GetProperty("UnitPriceSilver").GetInt64();
        var loadout = SwordLoadout(town, buyer);

        var offers = Call(buyer, new AuctionGetLoadoutOffersRequest { bqs = market.Id, box = buyer.Player.Character.Id, boy = loadout, boz = 1 });
        Check(offers is { ReturnCode: 0 } && offers.Parameters[0] is byte[] and [0, 10] && offers.Parameters[1] is string[][] and [[var sword], [var ore]]
              && offers.Parameters[2] is short[][] and [[1], [5]] && UnitOf(sword) == 100 * A7m && UnitOf(ore) == 10 * A7m,
              "AuctionGetLoadoutOffers: slot 0 the 100-silver sword (1), inventory entry 10 the 5 ore at 10 (not the dearer ones)");
        var (swordId, oreId) = (IdOf(((string[][])offers.Parameters[1]!)[0][0]), IdOf(((string[][])offers.Parameters[1]!)[1][0]));

        // The rival buys 1 of those ore first: the buyer's purchase takes nothing and answers with new offers.
        Check(Call(rival, new AuctionBuyOfferRequest { bqs = market.Id, bn2 = 1, bn3 = oreId }) is { ReturnCode: 0 }, "the rival buys 1 ore of that offer");
        var silver = buyer.Player.Character.Silver;
        var stale = Call(buyer, new AuctionBuyLoadoutOfferRequest { bqs = market.Id, bnz = [swordId, oreId], bn0 = [0, 10], bn1 = true });
        Check(stale is { ReturnCode: 0 } && stale.Parameters.GetValueOrDefault((byte)3) is null or long[] { Length: 0 } && buyer.Player.Character.Silver == silver
              && world.Market!.Order(swordId)!.Amount == 1 && stale.Parameters[1] is string[][] and [[_], [_, _]] && stale.Parameters[2] is short[][] and [[1], [4, 1]],
              "a changed offer: nothing bought, no silver taken (the sword still listed), new offers: the sword, 4 ore at 10 + 1 at 20");
        var fresh = ((string[][])stale.Parameters[1]!).SelectMany(x => x).Select(IdOf).ToArray();

        var bought = Call(buyer, new AuctionBuyLoadoutOfferRequest { bqs = market.Id, bnz = fresh, bn0 = [0, 10, 10], bn1 = true });
        var total = (100 + 4 * 10 + 20) * A7m;
        Check(bought is { ReturnCode: 0 } && bought.Parameters[3] is long[] { Length: 3 } && buyer.Player.Character.Silver == silver - total
              && buyer.Player.Inventory.Bag.Items.Any(i => i.Template.Name == "T4_MAIN_SWORD") && buyer.Player.Inventory.Bag.Items.Where(i => i.Template.Name == "T4_ORE").Sum(i => i.Count) == 5
              && world.Market!.Order(swordId) is not { Amount: > 0 } && world.Market.FinishedOf(buyer.Player.Character.Id).Count == 0
              && (!bought.Parameters.ContainsKey(5) || Convert.ToInt32(bought.Parameters[5]) == WorldSession.LoadoutFetchOk),
              $"all bought at once: 160 silver once, the sword and 5 ore in the inventory, nothing left to collect, result Ok (silver {buyer.Player.Character.Silver})");
        var again = Call(buyer, new AuctionBuyLoadoutOfferRequest { bqs = market.Id, bnz = fresh, bn0 = [0, 10, 10], bn1 = true });
        Check(again.ReturnCode != 0 && buyer.Player.Character.Silver == silver - total, "the same purchase again: refused (the answer is used up), nothing taken");
    }

    private static void QuickSell()
    {
        using var town = new EconomyTest.Town();
        var world = town.World;
        var buyer = town.Join("Wants", 1000);
        var seller = town.Join("Has", 0, new InventoryItem("T4_ORE", 5));
        var market = town.Station("MARKETPLACE");
        OperationResponse Call((WorldSession S, FakePeer P, Player Player) who, AlbionRequest r)
        {
            lock (world.Sync) who.S.OnRequest(Request(r, 8), encrypted: false);
            return who.P.Responses[^1];
        }
        var ore = world.Items.Find("T4_ORE")!;
        Check(Call(buyer, new AuctionCreateRequestRequest { bqs = market.Id, bn9 = 1, boa = 4, bob = ore.Index, bod = 50 * 10000L }) is { ReturnCode: 0 }, "a buy order of 4 ore at 50");
        var stack = seller.Player.Inventory.Bag.Items.Single();
        var query = Call(seller, new QuickSellAuctionQueryActionRequest { bqs = market.Id, bqt = [stack.Id] });
        var order = JsonDocument.Parse(((string[])query.Parameters[1]!)[0]).RootElement.GetProperty("Id").GetInt64();
        var sold = Call(seller, new QuickSellAuctionSellActionRequest { bqs = market.Id, bqu = [stack.Id, 987654321], bqv = [order, order], bqw = [4, 1] });
        Check(sold is { ReturnCode: 0 } && sold.Parameters[0] is long[] and [var id] && id == stack.Id && stack.Count == 1
              && seller.Player.Character.Silver == 184 * 10000L && world.Market!.Order(order) is not { Amount: > 0 } && world.Market.FinishedOf(buyer.Player.Character.Id).Count == 1,
              $"quick sell: {{0 [the stack]}}, 4 ore gone, 200 less 8 % tax = 184 silver, the order filled and the buyer's ore to collect; the unknown item skipped (silver {seller.Player.Character.Silver})");
    }
}
