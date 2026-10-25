using System.Text.Json;
using AlbionLocal.Photon.Serialization;
using AlbionLocal.Server;
using AlbionLocal.Server.Content;
using AlbionLocal.Server.Game;
using AlbionLocal.Server.Network;
using AlbionLocal.Server.Persistence;
using AlbionLocal.Server.Protocol;
using AlbionLocal.Server.Systems;
using static AlbionLocal.SelfTest.AlbionTests;
using static AlbionLocal.SelfTest.CombatTest;

namespace AlbionLocal.SelfTest;

/// <summary>
/// PVE-DESIGN step 12, the economy: the shared bank (Bank:Shared) and the Black Market's NPC buyer (Bm:BuyFactor, Bm:DailyLimit;
/// analysis/work/stageH/blackmarket/SPEC.md §3).
/// </summary>
public static class BlackMarketTest
{
    public static IEnumerable<(string, Action)> All =>
    [
        ("economy: Bank:Shared (off in both presets; on by key): the Thetford bank opens one vault holding the old 0000 and 4000 vaults' items; a full shared vault keeps the rest in the city's; off again, the shared vault goes back into a city's", SharedBank),
        ("economy: Black Market (Bm:BuyFactor pve 5, official 0): T4 sword orders at 5 x 384 x quality at the BLACKMARKET building only; a quality-3 sword sold at 3 x 1920 less 8 % tax; Bm:DailyLimit thins and hides the orders until the next day; no player orders there (a chat line); saved", Buyer),
    ];

    private static void Check(bool cond, string what) => AlbionTests.Check(cond, what);

    private static readonly Guid Tab = WorldSession.DefaultTab;

    private static void SharedBank()
    {
        var pve = LoadTestConfig(Path.Combine(NewDir(), "none.json"), ["--Preset=pve"]);
        var official = LoadTestConfig(Path.Combine(NewDir(), "none.json"), ["--Preset=official"]);
        Check(!pve.BankShared && !official.BankShared && pve is { BmBuyFactor: 5, BmDailyLimit: 1_000_000 } && official is { BmBuyFactor: 0, BmDailyLimit: 1_000_000 }
              && LoadTestConfig(Path.Combine(NewDir(), "none.json"), ["--Bank:Shared=true"]).BankShared,
              "presets: Bank:Shared false in both; Bm:BuyFactor pve 5, official 0; Bm:DailyLimit 1000000; an explicit key wins");

        using var town = new EconomyTest.Town(config: new ServerConfig { JoinByCharacterId = true, BankShared = true });
        var world = town.World;
        var c = StarterKit.Default.NewCharacter("Hoarder");
        c.Vaults[Character.VaultKey("0000", Tab)] = [new InventoryItem("T4_ORE", 5)];
        c.Vaults[Character.VaultKey("4000", Tab)] = [new InventoryItem("T4_HIDE", 2), null, null, new InventoryItem("T4_WOOD", 7)];
        Check(town.Characters.Create(c), "character created with vaults in Thetford and Martlock");
        var (session, peer, me) = town.Rejoin(c.Id);
        var bank = town.Station("T1_BANK");
        var mark = peer.Events.Count;
        lock (world.Sync) session.OnRequest(Request(new RegisterToObjectRequest { zg = bank.Id }, null), encrypted: false);
        var vault = me.OpenVaults.Values.SingleOrDefault();
        Check(vault is { Key: var key } && key == Character.VaultKey(Character.SharedVault, Tab)
              && vault.Container[0]?.Template.Name == "T4_ORE" && vault.Container[1] is { Template.Name: "T4_HIDE", Count: 2 } && vault.Container[2] is { Template.Name: "T4_WOOD", Count: 7 }
              && Since(peer, mark).Count(e => e.Code == EventCodes.AttachItemContainer) == 1 && me.Character.Vaults.Keys.SequenceEqual([key]),
              "the Thetford bank opens the shared vault: ore, hide and wood in slots 0-2, the city vaults gone");
        lock (world.Sync) peer.Disconnect();
        world.Saves.Flush();
        Check(town.Characters.Find(c.Id)?.Vaults.Keys.SingleOrDefault() == Character.VaultKey(Character.SharedVault, Tab), "saved under the location 'shared'");

        // A full shared vault (2 slots here) keeps the rest in the city's vault; Bank:Shared false: the shared vault into the city's.
        var x = new Character();
        x.Vaults[Character.VaultKey("0000", Tab)] = [new InventoryItem("T4_ORE", 1), new InventoryItem("T4_HIDE", 1)];
        x.Vaults[Character.VaultKey(Character.SharedVault, Tab)] = [new InventoryItem("T4_WOOD", 1)];
        var shared = Character.VaultKey(Character.SharedVault, Tab);
        Check(WorldSession.MergeVaults(x, shared, Tab, 2) == 1 && x.Vaults[shared] is [{ Name: "T4_WOOD" }, { Name: "T4_ORE" }]
              && x.Vaults[Character.VaultKey("0000", Tab)] is [null, { Name: "T4_HIDE" }],
              "a full shared vault: the ore moves into slot 1, the hide stays in Thetford's vault");
        var city = Character.VaultKey("4000", Tab);
        Check(WorldSession.MergeVaults(x, city, Tab, 128) == 2 && x.Vaults[city] is [{ Name: "T4_WOOD" }, { Name: "T4_ORE" }] && !x.Vaults.ContainsKey(shared)
              && x.Vaults.ContainsKey(Character.VaultKey("0000", Tab)),
              "Bank:Shared off: Martlock's bank takes the shared vault (Thetford's stays its own)");
    }

    private static void Buyer()
    {
        using var town = new EconomyTest.Town(config: new ServerConfig { JoinByCharacterId = true, BmBuyFactor = 5, BmDailyLimit = 8000 }, blackMarket: true);
        var world = town.World;
        var (session, peer, me) = town.Join("Seller", 0, new InventoryItem("T4_MAIN_SWORD", 1), new InventoryItem("T4_MAIN_SWORD", 1) { Quality = 3 }, new InventoryItem("T4_ORE", 5));
        var bm = town.Station("BLACKMARKET");
        var market = town.Station("MARKETPLACE");
        OperationResponse Call(AlbionRequest req)
        {
            var before = peer.Responses.Count;
            lock (world.Sync) session.OnRequest(Request(req, 3), encrypted: false);
            return peer.Responses.Skip(before).Last();
        }
        static JsonElement Dto(string json) => JsonDocument.Parse(json).RootElement;
        var sword = Client.Items.Find("T4_MAIN_SWORD")!;
        string[] Orders(long building, short type) => (string[])Call(new AuctionGetRequestsRequest { bqs = building, bp1 = [type] }).Parameters[0]!;
        long Id(int quality) => BlackMarket.FirstId + sword.Index * 8L + quality;
        static (long Id, int Quality, long Unit, int Amount) Row(JsonElement o) =>
            (o.GetProperty("Id").GetInt64(), o.GetProperty("QualityLevel").GetInt32(), o.GetProperty("UnitPriceSilver").GetInt64() / Pricing.A7m, o.GetProperty("Amount").GetInt32());
        var rows = Orders(bm.Id, sword.Index).Select(Dto).Select(Row).OrderBy(r => r.Quality).ToList();
        Check(bm.Template.Kind == "blackmarketbuilding"
              && rows.SequenceEqual([(Id(1), 1, 1920L, 4), (Id(2), 2, 3840L, 2), (Id(3), 3, 5760L, 1), (Id(4), 4, 7680L, 1)])
              && Orders(bm.Id, Client.Items.Find("T4_ORE")!.Index).Length == 0 && Orders(market.Id, sword.Index).Length == 0,
              "the Black Market's buy orders for T4_MAIN_SWORD: 5 x 384 x quality, amounts of 8000 a day (no quality 5); none for ore, none at the marketplace");
        var chat = peer.Events.Count;
        Check(Call(new AuctionCreateOfferRequest { bqs = bm.Id, bn5 = 1, bn6 = me.Inventory.Bag[0]!.Id, bn7 = 1000 * Pricing.A7m, bn8 = 24 }).ReturnCode != 0
              && Since(peer, chat).Count(e => e.Code == EventCodes.SystemMessage) == 1
              && Call(new AuctionCreateRequestRequest { bqs = bm.Id, bn9 = 1, boa = 1, bob = sword.Index, boc = 24, bod = 10 * Pricing.A7m }).ReturnCode != 0
              && me.Inventory.Bag[0] is { Template.Name: "T4_MAIN_SWORD" } && world.Market!.Orders(BlackMarket.Location("0000"), MarketOrder.Offer).Count == 0,
              "no sell or buy orders of players at the Black Market (a chat line for the sell order)");
        var fine = me.Inventory.Bag[1]!;
        Check(Call(new QuickSellAuctionQueryActionRequest { bqs = bm.Id, bqt = [fine.Id] }).Parameters[1] is string[] and [var quick] && Dto(quick).GetProperty("Id").GetInt64() == Id(3)
              && Call(new AuctionSellSpecificItemRequestRequest { bqs = bm.Id, bqe = Id(3), bqf = sword.Index, bqg = 1, bqh = 1 }) is { ReturnCode: 0 }
              && me.Character.Silver == 5760 * Pricing.A7m - Pricing.MarketTax(5760 * Pricing.A7m, world.Economy) && me.Inventory.Bag[1] is null
              && me.Character.BlackMarket == new BlackMarketLog(BlackMarket.Day(DateTime.UtcNow), 5760 * Pricing.A7m) && world.Market!.FinishedOf(Guid.Empty).Count == 0,
              "the quality-3 sword sold (quick sell finds its quality's order): 5760 less 8 % tax, today's sales 5760, nobody gets the sword");
        Check(Orders(bm.Id, sword.Index).Select(Dto).Select(Row).SequenceEqual([(Id(1), 1, 1920L, 1)])
              && Call(new AuctionSellSpecificItemRequestRequest { bqs = bm.Id, bqe = Id(2), bqf = sword.Index, bqg = 0, bqh = 1 }).ReturnCode != 0
              && Call(new AuctionSellSpecificItemRequestRequest { bqs = bm.Id, bqe = Id(1), bqf = sword.Index, bqg = 0, bqh = 1 }).ReturnCode == 0
              && Orders(bm.Id, sword.Index).Length == 0,
              "2240 left of the day's 8000: only one quality-1 order; the sword is refused at quality 2, sold at 1, then no orders");
        me.Character.BlackMarket = new BlackMarketLog(BlackMarket.Day(DateTime.UtcNow) - 1, 8000 * Pricing.A7m);
        Check(Orders(bm.Id, sword.Index).Length == 4, "the next day the orders are back");
        lock (world.Sync) peer.Disconnect();
        world.Saves.Flush();
        Check(town.Characters.Find(me.Character.Id)?.BlackMarket?.Sold == 8000 * Pricing.A7m, "the day's sales are saved with the character (schema v16)");
    }
}
