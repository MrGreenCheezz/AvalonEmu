using System.Numerics;
using System.Text.Json;
using AlbionLocal.Photon;
using AlbionLocal.Photon.Serialization;
using AlbionLocal.Server;
using AlbionLocal.Server.Commands;
using AlbionLocal.Server.Content;
using AlbionLocal.Server.Data;
using AlbionLocal.Server.Entities;
using AlbionLocal.Server.Game;
using AlbionLocal.Server.Network;
using AlbionLocal.Server.Persistence;
using AlbionLocal.Server.Protocol;
using AlbionLocal.Server.Systems;
using static AlbionLocal.SelfTest.AlbionTests;
using static AlbionLocal.SelfTest.CombatTest;

namespace AlbionLocal.SelfTest;

/// <summary>
/// Stage 6, the economy (analysis/work/stage6/SPEC.md with its verifier's corrections): the golden numbers of the formulas
/// (item values, repair, city factor, crafts and shop prices, return fraction, quality weights), then light checks of
/// money, the per-account GM level, op 55 at Thetford stations, the inventory housekeeping, the bank and the market.
/// </summary>
public static class EconomyTest
{
    public static IEnumerable<(string, Action)> All =>
    [
        ("economy: item values 384/256/65536, repair 3168 (c5 3662.208, nutrition 22), c5(31940) = 36922.64, seed prices, crafts, refining recipes, Thetford ore returns 36.7/53.9 %, quality weights", Formulas),
        ("economy: Join keys 33/34, UpdateMoney {0, 1, 2}, .money, silver saved; a Player account cannot run .item add, an Admin can (.account level)", MoneyAndLevels),
        ("economy: op 55 at Thetford: repair (no response, the corrected burst), shop purchase (the captured order), failures {0 building, 1 ACT, 2 counter, 3 error} with RC 2202, a timed smelter batch", Stations),
        ("economy: salvage (ACT 3) at the repair shop: the T4 sword at half durability gives no silver (no itemvalue), 1-2 bars, 0-1 leather, kept over a relog; a worn sword; one destroyed meanwhile gives nothing; refusals; AccessStatus of the shop", Salvage),
        ("economy: enchanting (ACT 7) T4 sword .0 → .1 with 288 runes at the meld building (quality and spells kept, new object, saved), refused without runes; quality reroll (ACT 6) 1 → better at the repair shop (event 67, silver); crafting quality bonus", Enchanting),
        ("economy: awakening (ACT 8) a T4 sword@4 at the meld building: silver and 2 + 2 tokens taken, NewEquipmentItem key 11 = 1 and event 37 with 3 traits, kept over a relog; a sword@3 refused (ItemEnchantmentTooLow)", Awakening),
        ("economy: trait value reroll (ACT 8 sub 4): attunement and silver charged, event 552 old -> new (never lower), soul re-sent; an attuned TRAIT_HITPOINTS_MAX adds hitpointsmax", TraitReroll),
        ("economy: crafting: refine 10 T3 planks at the carpenter, craft a T4 sword at the forge (quality, returns, crafting fame into CRAFT_REFINE_WOOD_T4 / CRAFT_SWORDS)", Crafting),
        ("economy: crafting batch fame settles only completed units on cancellation and logout", CraftBatchCancellation),
        ("economy: crafting uses client recipe time and a move after the deadline finishes the batch before the world tick", CraftTiming),
        ("economy: content/spawns of the royal cities and Caerleon: 12 stations and a stable each on RealEstate plot centres, Martlock refines hide at its own +40 % bonus", CityStations),
        ("economy: a T3 horse crafted at a stable from a grown T3 horse + 20 T3 leather (the horse never returned)", StableCraft),
        ("economy: split 10 into 6 + 4 (revision echoed), stack all, destroy", Housekeeping),
        ("economy: bank: tab-0 Guid, RegisterToObject attaches the default tab (items first, 128 slots), deposit, relog, withdraw, shift+click both ways, relog; refusals (keys 0-2 echoed, out of range, slots, weight); two views; LoadoutReadHeaders echo", Bank),
        ("economy: market: DTO fields, empty market, fees and tax, filters and pages, offer bought and collected, buy order sold into (quick-sell query first) and aborted, overbuy refused, an offline seller's silver at its next Join", Market),
        ("economy: market price history (op 95/96, econ SPEC §1): two buys of ore summed in the current hour, average 100; another city's and another quality's sales left out", PriceHistory),
        ("economy: gathering extras (econ SPEC §2): T4 wood's bonus loot rolls its gathering tome; T1 fishing bait used from the bag: fishingspeed 0.5, 10 charges, the bite 1.5x faster, the 10th strike takes the buff", GatheringExtras),
        ("economy: market bulk collection returns client success 4, delivers once, preserves failed claims and persists across relog", MarketBulkCollection),
    ];

    private static void Check(bool cond, string what) => AlbionTests.Check(cond, what);

    private static ItemStore Catalog => Client.Items;

    private static void Formulas()
    {
        var values = new ItemValues(Catalog);
        double V(string name) => values.Of(Catalog.Find(name)!);
        Check(V("T4_MAIN_SWORD") == 384 && V("T4_ARMOR_PLATE_SET1") == 256 && V("T8_2H_CLAYMORE@3") == 65536 && V("T1_FARM_CARROT_SEED") == 400,
              $"item values: T4_MAIN_SWORD 384, T4_ARMOR_PLATE_SET1 256, T8_2H_CLAYMORE@3 65536, carrot seed 400 (got {V("T4_MAIN_SWORD")}, {V("T4_ARMOR_PLATE_SET1")}, {V("T8_2H_CLAYMORE@3")})");
        var sword = Catalog.Find("T4_MAIN_SWORD")!;
        var half = sword.FullDurabilityRaw / 2;
        var base50 = Pricing.RepairBase(384, half, sword.MaxDurability, 1);
        Check(base50 == 3168 && Pricing.CityFactor(base50, 1.156m, 1m) == 3662.208m && Pricing.RepairNutrition(384, half, sword.MaxDurability, 1) == 22,
              $"repair at 50 %: ceil(384 × 0.5 × 16.5) = 3168, c5 3662.208, nutrition 22 (got {base50})");
        Check(Pricing.RepairBase(384, sword.FullDurabilityRaw, sword.MaxDurability, 1) == 0 && Pricing.RepairBase(384, sword.FullDurabilityRaw - 1, sword.MaxDurability, 1) >= 1
              && Pricing.DurabilityFraction(99_999_999, 13500) == 0.7407m,
              "full durability costs nothing (ItemHasFullDurability); just below full at least 1 silver; f is truncated to 4 decimals (a7m)");
        Check(Pricing.CityFactor(31940) == 31940 && Pricing.CityFactor(31940, 1.156m, 1m) == 36922.64m, "c5 without Join keys 84/85 = x; with 1.156 / 1.0: 31940 → 36922.64");
        // The official repair of 103829 (12 items, key 4 = 70700960): the ten equipment items at their captured durability
        // (NewEquipmentItem key 8 before the request) give 5744; the two furniture items (NewFurnitureItem key 7, their full
        // durability captured after the repair: 135 and 140, not items.xml's 0.5 / 14) 149 + 223; Σ 6116 × 1.156 = 7070.096.
        // Exact a7m arithmetic matters: T5_MOUNT_OX at 0.95 is 792 (a float 1 − f would give 793).
        (string Name, long Raw)[] worn =
        [
            ("T4_MAIN_FIRESTAFF_CRYSTAL@1", 973_872_000), ("T4_OFF_HORN_KEEPER", 102_500_000), ("T5_HEAD_LEATHER_SET2@2", 230_750_000),
            ("T4_ARMOR_CLOTH_SET2", 96_200_000), ("T4_SHOES_PLATE_SET2", 80_000_000), ("T6_BAG", 192_275_000), ("T6_CAPE", 211_512_500),
            ("T5_MOUNT_OX", 256_500_000), ("T4_2H_TOOL_SICKLE", 26_600_000), ("T3_2H_TOOL_SICKLE", 11_400_000),
        ];
        var equipment = worn.Sum(w => Catalog.Find(w.Name) is { } t ? Pricing.RepairBase(values.Of(t), w.Raw, t.MaxDurability, 1) : -100_000);
        var furniture = Pricing.RepairBase(V("UNIQUE_FURNITUREITEM_ADC_GRAVE_B"), 1_282_500, 135, 1) + Pricing.RepairBase(V("PLAYERISLAND_FURNITUREITEM_STONE_WELL_A"), 1_330_000, 140, 1);
        Check(equipment == 5744 && furniture == 372 && Pricing.CityFactor(equipment + furniture, 1.156m, 1m) * Pricing.A7m == 70_700_960,
              $"the official 103829 repair: equipment 5744 + furniture 149 + 223 = 6116 silver, × 1.156 = key 4 70700960 (got {equipment} + {furniture})");

        // The shops: FARMING_MERCHANT's 88 goods; the captured purchases (103829: item 10 × 13, item 8 × 17) at factor 1.
        var buildings = Client.Clusters!.Buildings;
        var farm = buildings.Find("FARMING_MERCHANT")!;
        var carrot = Catalog[8]!;
        var wheat = Catalog[10]!;
        Check(farm.IsShop && farm.CraftItems.Count == 88 && carrot.Name == "T1_FARM_CARROT_SEED" && wheat.Name == "T3_FARM_WHEAT_SEED" && carrot.Recipes[0].Silver == 2000
              && Pricing.CraftPrice(carrot.Recipes[0], Pricing.Crafts(17, 1)) == 340_000_000 && Pricing.CraftPrice(wheat.Recipes[0], 13) == 650_000_000
              && Pricing.CityFactor(Pricing.CraftPrice(carrot.Recipes[0], 17), 1.156m, 1m) == 393_040_000 && Pricing.CityFactor(Pricing.CraftPrice(wheat.Recipes[0], 13), 1.156m, 1m) == 751_400_000,
              "FARMING_MERCHANT sells 88 items; index 8 = carrot seed 2000 silver: 17 → 340000000, index 10 = wheat seed: 13 → 650000000 (× 1.156 = the captured 393040000 / 751400000)");
        Check(Pricing.Crafts(13, 1) == 13 && Pricing.Crafts(13, 5) == 3, "crafts = ceil(amount / amountcrafted): 13 / 1 = 13, 13 / 5 = 3");
        Check(Pricing.CraftFame(V("T2_PLANKS"), 2, 0, 1) == 4 * Pricing.A7m && Pricing.CraftFame(V("T3_PLANKS"), 3, 0, 10) == 120 * Pricing.A7m
              && Pricing.CraftFame(V("T4_MAIN_SWORD"), 4, 0, 1) == 864 * Pricing.A7m && Pricing.CraftFame(V("T4_MAIN_SWORD@1"), 4, 1, 1) == (long)(values.Of(Catalog.Find("T4_MAIN_SWORD@1")!) * 3.375 * Pricing.A7m),
              $"crafting fame itemvalue × count × 1.5^(tier + enchantment − 2): T2 planks 4, 10 T3 planks 120, T4 sword 864, T4.1 × 3.375 (got {Pricing.CraftFame(V("T3_PLANKS"), 3, 0, 10)})");
        Check(V("T8_MAIN_SWORD") == 6144 && Pricing.CraftFame(V("T8_MAIN_SWORD"), 8, 0, 1) == 699_838_464,
              $"T8 crafting fame takes 1.5⁶ as an a7m (11.3906, bci.g): 6144 × 11.3906 = 69983.8464 (got {Pricing.CraftFame(V("T8_MAIN_SWORD"), 8, 0, 1)})");
        var bar4 = Catalog.Find("T4_METALBAR")!.Recipes;
        var bar8 = Catalog.Find("T8_METALBAR")!.Recipes[0];
        Check(bar4[0].Resources is [{ Name: "T4_ORE", Count: 2 }, { Name: "T3_METALBAR", Count: 1 }] && bar4.Count == 2 && bar4[1].Resources.Any(r => r.Name.StartsWith("T1_FACTION_", StringComparison.Ordinal))
              && Catalog.Find("T2_METALBAR")!.Recipes[0].Resources is [{ Name: "T2_ORE" }] && bar8.Resources is [{ Name: "T8_ORE", Count: 5 }, { Name: "T7_METALBAR", Count: 1 }],
              "T4_METALBAR = 2 × T4_ORE + 1 × T3_METALBAR (recipe 1 with a faction token); T2 has no refined input; T8 k = 5");

        var bonus = Client.Crafting.Bonus("0000", "ore");
        var plain = Pricing.ReturnFraction(bonus);
        var focus = Pricing.ReturnFraction(bonus + Client.GameData!.Economy.FocusEfficiency);
        Check(Math.Abs(plain - 0.367) < 0.001 && Math.Abs(focus - 0.539) < 0.001 && Pricing.Returned(new CraftResource("X", 5, 0, 0), 10, plain, 0).Count == 0
              && Pricing.Returned(new CraftResource("X", 2, 0, int.MaxValue), 10, plain, 0) is (7, var carry) && Math.Abs(carry - 0.342) < 0.001,
              $"Thetford ore: {plain:0.###} without focus, {focus:0.###} with; maxreturnamount 0 returns nothing; 2 × 10 crafts → 7 back, 0.342 carried");

        var random = new Random(1234);
        var counts = new int[6];
        for (var i = 0; i < 100_000; i++) counts[WorldSession.RollQuality(random, Client.GameData.Economy.QualityWeights)]++;
        double[] expected = [0, 0.689, 0.25, 0.05, 0.01, 0.001];
        Check(Enumerable.Range(1, 5).All(q => Math.Abs(counts[q] / 100_000.0 - expected[q]) < 0.01), $"quality rolls within 1 % of 68.9/25/5/1/0.1 (got {string.Join("/", counts.Skip(1))})");
        Check(WorldSession.DefaultTab == new Guid("7deb25d2-b3d8-4c7d-a0fa-2778d2eb0bd9"), "the bank's default tab Guid is bgf.u0[0]");
        var economy = Client.GameData.Economy;
        Check(economy is { MarketSetupFee: 0.025, MarketTax: 0.08, MarketMinimumTax: 1, MarketOrderHours: 720 }
              && Pricing.MarketSetupFee(500 * Pricing.A7m, economy) == 125_000 && Pricing.MarketSetupFee(10 * Pricing.A7m, economy) == Pricing.A7m
              && Pricing.MarketTax(300 * Pricing.A7m, economy) == 240_000 && Pricing.MarketTax(123_456, economy) == 9_876,
              "market (gamedata MarketPlace): setup fee 2.5 % of 500 = 12.5, at least 1 silver; tax 8 % of 300 = 24, a7m truncated (12.3456 → 0.9876)");
        var parsed = EconomySettings.From(System.Xml.Linq.XElement.Parse("""
            <AO><MarketPlace/><MarketPlace><TaxValues><TaxFactor name="setupfee" value="0.03"/><TaxFactor name="transactiontax" value="0.05"/>
            <AbsoluteTax name="minimumtax" value="2"/></TaxValues><Settings><Entry name="defaultexpiration" value="168"/></Settings>
            <OrderDurations><Entry durationInHrs="6"/><Entry durationInHrs="48"/></OrderDurations></MarketPlace></AO>
            """));
        Check(parsed is { MarketSetupFee: 0.03, MarketTax: 0.05, MarketMinimumTax: 2, MarketOrderHours: 168, MarketOrderDurations: [6, 48] }
              && economy.MarketOrderDurations.SequenceEqual([12, 24, 168, 720]),
              "MarketPlace parsing: values unlike the defaults are read past an empty <MarketPlace/>; gamedata's durations 12/24/168/720");
    }

    /// <summary>A Thetford map with the stations the tests need placed next to the start (content/spawns as the server loads it, made here).</summary>
    internal sealed class Town : IDisposable
    {
        public readonly string Dir = NewDir();
        public readonly ManualClock Clock = new();
        public readonly SqliteCharacterRepository Characters;
        public readonly World World;

        /// <param name="journals">The journal settings; null = the built-in ones without the merchant (a market of the players' orders only).</param>
        /// <param name="config">The server settings; null = the official defaults with JoinByCharacterId.</param>
        /// <param name="blackMarket">Also a Black Market building (blackmarketbuilding BLACKMARKET) at (-3, 6).</param>
        /// <param name="stable">Also a stable (T8_STABLE) at (6, 0).</param>
        /// <param name="meld">Also the meld building (T8_MELDBUILDING) at (6, 3).</param>
        public Town(JournalSettings? journals = null, ServerConfig? config = null, bool blackMarket = false, bool stable = false, bool meld = false)
        {
            Characters = new SqliteCharacterRepository(Path.Combine(Dir, "albionlocal.db"));
            _ = Client.Clusters!.Get("0000"); // resolved before the Join: its objects (the stations) go out with it
            var anchor = GameTime.Now;
            var stations = new StationSpawns(new Dictionary<string, IReadOnlyList<StationSpawns.Entry>>
            {
                ["0000"] =
                [
                    new("FARMING_MERCHANT", new Vector2(3, 0), 0), new("T8_SMELTER", new Vector2(-3, 0), 0), new("T8_REPAIRSHOP", new Vector2(0, 3), 0),
                    new("T1_BANK", new Vector2(0, -3), 0), new("MARKETPLACE", new Vector2(3, 3), 0),
                    new("T8_CARPENTERSWORKSHOP", new Vector2(-3, -3), 0), new("T8_FORGE", new Vector2(-3, 3), 0),
                    .. blackMarket ? [new StationSpawns.Entry("BLACKMARKET", new Vector2(-3, 6), 0)] : Array.Empty<StationSpawns.Entry>(),
                    .. stable ? [new StationSpawns.Entry("T8_STABLE", new Vector2(6, 0), 0)] : Array.Empty<StationSpawns.Entry>(),
                    .. meld ? [new StationSpawns.Entry("T8_MELDBUILDING", new Vector2(6, 3), 0)] : Array.Empty<StationSpawns.Entry>(),
                ],
            });
            World = new World(config ?? new ServerConfig { JoinByCharacterId = true }, Characters, Catalog, Client.GameData, Client.Spells)
            {
                Sync = new object(), Clock = Clock, Clusters = Client.Clusters, MapSeed = 7, GameTicks = () => anchor + Clock.Now.Ticks,
                Crafting = Client.Crafting, Legendary = Client.Legendary, Stations = stations, CraftRandom = new Random(5),
                Journals = journals ?? JournalSettings.Default with { MerchantMarkets = new HashSet<string>() },
                EventSettings = EventSettings.Default with { Merchant = new EventMerchant { Markets = [] } }, // no NPC ticket orders (stage I)
            };
        }

        /// <summary>A new character at (0, 0) of Thetford with <paramref name="silver"/> and the given inventory, joined.</summary>
        public (WorldSession Session, FakePeer Peer, Player Player) Join(string name, long silver, params InventoryItem?[] inventory) =>
            Join(name, silver, null, inventory);

        /// <summary>As <see cref="Join(string, long, InventoryItem?[])"/>, with a destiny board of its own (null = everything unlocked).</summary>
        public (WorldSession Session, FakePeer Peer, Player Player) Join(string name, long silver, Progression? progression, params InventoryItem?[] inventory)
        {
            var c = StarterKit.Default.NewCharacter(name);
            c.Progression = progression;
            c.Silver = silver * Pricing.A7m;
            c.Inventory = [.. inventory];
            Check(Characters.Create(c), $"character {name} created");
            return Rejoin(c.Id);
        }

        public (WorldSession Session, FakePeer Peer, Player Player) Rejoin(Guid id)
        {
            var peer = new FakePeer();
            var session = new WorldSession(peer, World);
            peer.Disconnected = session.OnDisconnected;
            lock (World.Sync) session.OnRequest(Request(new JoinRequest { ss = id }, 1), encrypted: false);
            return (session, peer, session.Player!);
        }

        public Building Station(string name) => World.Maps.Find("0000")!.Objects.Values.OfType<Building>().Single(b => b.Template.Name == name);

        public void Run(double seconds)
        {
            for (var i = 0; i < Math.Round(seconds * 20); i++)
            {
                Clock.Advance(TimeSpan.FromMilliseconds(50));
                lock (World.Sync) World.Update();
            }
        }

        public void Dispose()
        {
            World.Dispose();
            Characters.Dispose();
            try { Directory.Delete(Dir, true); } catch (IOException) { }
        }
    }

    /// <summary>The responses <paramref name="peer"/> got since <paramref name="mark"/> (of its responses).</summary>
    private static List<OperationResponse> Answers(FakePeer peer, int mark) => [.. peer.Responses.Skip(mark)];

    private static void MoneyAndLevels()
    {
        using var town = new Town();
        var world = town.World;
        var (session, peer, me) = town.Join("Moneybags", 1234, new InventoryItem("T4_ORE", 3));
        var join = peer.Responses.First(r => Convert.ToInt16(r.Parameters[ReservedKeys.OperationCode]) == (short)OperationCodes.Join);
        Check(L(join.Parameters, 33) == 12_340_000 && !join.Parameters.ContainsKey(34) && !join.Parameters.ContainsKey(84), "Join key 33 = 1234 silver (a7m), no gold, no price factor keys");

        var replies = new List<(string Text, bool Error)>();
        var gm = new CommandContext(world, me, AccountLevel.GameMaster, (t, e) => replies.Add((t, e)));
        var mark = peer.Events.Count;
        lock (world.Sync) world.Commands.Execute(gm, ".money +100 5");
        var money = Since(peer, mark).Where(e => e.Code == EventCodes.UpdateMoney).ToList();
        Check(money is [var m] && L(m.P, 0) == me.Id && L(m.P, 1) == 13_340_000 && L(m.P, 2) == 50_000 && replies is [(_, false)],
              ".money +100 5: UpdateMoney {0 player, 1 1334 silver, 2 5 gold}");
        mark = peer.Events.Count;
        lock (world.Sync) world.Commands.Execute(gm, ".money 0");
        Check(Since(peer, mark) is [{ Code: EventCodes.UpdateMoney } zero] && zero.P.ContainsKey(1) && L(zero.P, 1) == 0 && L(zero.P, 2) == 50_000,
              "UpdateMoney always carries keys 1 and 2, a zero too; setting silver keeps the gold");
        lock (world.Sync) world.Commands.Execute(gm, ".money add 50");
        Check(me.Character.Silver == 500_000 && me.Character.Gold == 50_000, ".money add 50: +50 silver, the gold kept");
        lock (world.Sync) world.Commands.Execute(gm, ".money 0");
        lock (world.Sync)
        {
            Check(!Wallet.TrySpend(me, 1, "test") && me.Character.Silver == 0, "spending more than the balance fails and changes nothing");
            world.Commands.Execute(gm, ".money 777");
            world.Saves.SaveAll();
        }
        world.Saves.Flush();
        Check(town.Characters.Find(me.Character.Id) is { Silver: 7_770_000, Gold: 50_000 }, "silver and gold are saved with the character");

        // GM commands at the speaker's account level: a Player account (as Accounts:DefaultLevel makes them) may not add items.
        var pat = town.Characters.CreateAccount("pat", null, world.Config.DefaultAccountLevel)!;
        var c = StarterKit.Default.NewCharacter("Patricia");
        c.Account = pat.Id;
        c.Inventory = [];
        Check(town.Characters.Create(c) && pat.Level == AccountLevel.Player, "a new account is at Accounts:DefaultLevel = Player");
        var (patSession, patPeer, patPlayer) = town.Rejoin(c.Id);
        void Say(string line)
        {
            lock (world.Sync) patSession.OnRequest(Request(new SayRequest { aa9 = line }, null), encrypted: false);
        }
        mark = patPeer.Events.Count;
        Say(".item add T4_ORE 5");
        Check(patPlayer.Inventory.Bag.Count == 0 && Since(patPeer, mark).Any(e => e.Code == EventCodes.SystemMessage && Convert.ToByte(e.P[1]) == 1),
              "a Player account's chat .item add is refused (error SystemMessage, no item)");
        var console = new List<(string, bool)>();
        lock (world.Sync) world.Commands.ExecuteConsole(world, ".account level pat Admin", (t, e) => console.Add((t, e)));
        Say(".item add T4_ORE 5");
        Check(console is [(_, false)] && town.Characters.FindAccount("pat")!.Level == AccountLevel.Admin && patPlayer.Inventory.Bag.Items.Any(i => i.Template.Name == "T4_ORE" && i.Count == 5),
              "after .account level pat Admin on the console the same line adds the ore");
    }

    private static void Stations()
    {
        using var town = new Town();
        var world = town.World;
        var sword = Catalog.Find("T4_MAIN_SWORD")!;
        var (session, peer, me) = town.Join("Smith", 10_000, new InventoryItem("T4_MAIN_SWORD") { Durability = sword.FullDurabilityRaw / 2 },
            new InventoryItem("T4_ORE", 20), new InventoryItem("T3_METALBAR", 10));
        var repair = town.Station("T8_REPAIRSHOP");
        var damaged = me.Inventory.Bag[0]!;
        void Op55(ActionOnBuildingStartRequest req)
        {
            lock (world.Sync) session.OnRequest(Request(req, 7), encrypted: false);
        }

        // A price above the server's (the client may name less, never more than +1 silver): RC 2202 {0 building, 1 ACT 2, 2 the counter, 3 PriceChanged 12}.
        var answers = peer.Responses.Count;
        Op55(new ActionOnBuildingStartRequest { wf = world.GameNow, o = repair.Id, p = 2, q = 1, r = 5000 * Pricing.A7m, s = [damaged.Id] });
        Check(Answers(peer, answers) is [{ ReturnCode: WorldSession.StationComponentError } bad] && L(bad.Parameters, 0) == repair.Id && L(bad.Parameters, 1) == 2
              && L(bad.Parameters, 2) == 1 && L(bad.Parameters, 3) == 12 && me.Character.Silver == 10_000 * Pricing.A7m,
              $"a repair at a price above the server's: RC 2202 with keys 0-3 (building, ACT, counter, PriceChanged) (got {string.Join("; ", Answers(peer, answers).Select(r => $"rc {r.ReturnCode} {string.Join(" ", r.Parameters.Select(p => $"{p.Key}={p.Value}"))}"))})");

        answers = peer.Responses.Count;
        var mark = peer.Events.Count;
        Op55(new ActionOnBuildingStartRequest { wf = world.GameNow, o = repair.Id, p = 2, q = 2, r = 3168 * Pricing.A7m, s = [damaged.Id] });
        var start = Since(peer, mark);
        Check(Answers(peer, answers).Count == 0 && start is [{ Code: EventCodes.NewBuilding } inUse] && inUse.P[16] is true, "the repair starts: no response, NewBuilding with key 16");
        town.Run(1);
        var burst = Since(peer, mark).Skip(1).Select(e => e.Code).ToList();
        Check(burst.SequenceEqual([EventCodes.UpdateMoney, EventCodes.NewEquipmentItem, EventCodes.RepairBuildingInfo, EventCodes.NewBuilding, EventCodes.ActionOnBuildingFinished])
              && damaged.DurabilityRaw == sword.FullDurabilityRaw && me.Character.Silver == (10_000 - 3168) * Pricing.A7m && Answers(peer, answers).Count == 0,
              $"at the end: UpdateMoney (3168 silver), the sword at full durability, RepairBuildingInfo, NewBuilding, 66 (got {string.Join(", ", burst)})");

        // A shop purchase (FARMING_MERCHANT, instant): 17 carrot seeds for 34000 silver, in the captured order.
        Wallet.Credit(me, 40_000 * Pricing.A7m, "test");
        var farm = town.Station("FARMING_MERCHANT");
        answers = peer.Responses.Count;
        mark = peer.Events.Count;
        Op55(new ActionOnBuildingStartRequest { wf = world.GameNow, o = farm.Id, p = 1, q = 3, r = 340_000_000, u = 8, w = 17 });
        var bought = Since(peer, mark).Select(e => e.Code).ToList();
        Check(Answers(peer, answers).Count == 0 && bought.SequenceEqual([EventCodes.NewBuilding, EventCodes.UpdateMoney, EventCodes.NewSimpleItem, EventCodes.InventoryPutItem,
                  EventCodes.CraftItemFinished, EventCodes.CraftBuildingInfo, EventCodes.NewBuilding, EventCodes.ActionOnBuildingFinished])
              && me.Inventory.Bag.Items.Single(i => i.Template.Name == "T1_FARM_CARROT_SEED").Count == 17,
              $"the purchase: no response; NewBuilding(16), UpdateMoney, the seeds, CraftItemFinished, CraftBuildingInfo, NewBuilding, 66 (got {string.Join(", ", bought)})");

        // Missing resources at the smelter (no T5 ore): RC 2202 with NotEnoughResources (8); nothing taken, no money change.
        var smelter = town.Station("T8_SMELTER");
        var bar = Catalog.Find("T4_METALBAR")!;
        answers = peer.Responses.Count;
        mark = peer.Events.Count;
        var silver = me.Character.Silver;
        Op55(new ActionOnBuildingStartRequest { wf = world.GameNow, o = smelter.Id, p = 1, q = 4, u = Catalog.Find("T5_METALBAR")!.Index, w = 10 });
        Check(Answers(peer, answers) is [{ ReturnCode: WorldSession.StationComponentError } none] && L(none.Parameters, 1) == 1 && L(none.Parameters, 2) == 4 && L(none.Parameters, 3) == 8
              && peer.Events.Count == mark && me.Character.Silver == silver, "no ore: {.., 1 ACT 1, 2 counter 4, 3 NotEnoughResources 8}, no event, no money change");

        // A timed batch: 10 bars from 20 ore + 10 T3 bars, one CraftItemFinished per craft, part of the resources back (Thetford ore).
        mark = peer.Events.Count;
        Op55(new ActionOnBuildingStartRequest { wf = world.GameNow, o = smelter.Id, p = 1, q = 5, u = bar.Index, w = 10 });
        Check(me.Action is { Over: false } && Since(peer, mark).Count(e => e.Code == EventCodes.CraftItemFinished) == 0, "the batch runs (event 64 only to others)");
        town.Run(4.5);
        var crafted = Since(peer, mark);
        var bag = me.Inventory.Bag;
        Check(crafted.Count(e => e.Code == EventCodes.CraftItemFinished) == 10 && crafted[^1].Code == EventCodes.ActionOnBuildingFinished && me.Action is null
              && bag.Items.Where(i => i.Template == bar).Sum(i => i.Count) == 10 && bag.Items.Where(i => i.Template.Name == "T4_ORE").Sum(i => i.Count) is 7,
              $"10 completed crafts: 10 x T4_METALBAR, 7 of the 20 ore back (36.7 %), 66 last (got {Show(crafted.Where(e => e.Code != EventCodes.NewSimpleItem))})");
    }

    /// <summary>
    /// The salvage (ACT 3, client code only): silver = salvage value × durability fraction × 0.75 (the formula: 384 → 288), but
    /// the sword has neither salvagevalueoverride nor its own itemvalue, so it gives none (the client's bdi+0xC8 stays
    /// a7m.MinValue); resources = count × fraction × 0.2 rounded at random (16 bars → 1.6, 8 leather → 0.8), the item gone,
    /// as saved; a worn sword leaves the equipment; a sword destroyed before the end gives nothing; refusals with RC 2202 and
    /// SalvageItemErrorCode, or RC 1 at a building without salvage.
    /// </summary>
    private static void Salvage()
    {
        var sword = Catalog.Find("T4_MAIN_SWORD")!;
        Check(Pricing.SalvageSilver(384, sword.Recipes[0], 4, sword.FullDurabilityRaw, sword.MaxDurability, 1) == 288 * Pricing.A7m
              && Pricing.SalvageSilver(384, sword.Recipes[0], 4, sword.FullDurabilityRaw / 2, sword.MaxDurability, 2) == 288 * Pricing.A7m
              && Pricing.SalvageResourceFactor(sword.Recipes[0], 4, sword.FullDurabilityRaw, sword.MaxDurability) == 0.2m
              && Pricing.SalvageResourceFactor(sword.Recipes[0] with { SalvageItemFactor = 0.4 }, 4, 0, sword.MaxDurability) == 0.4m
              && Pricing.SalvageResourceCount(16, 1, 0.2m, null) == 4 && Pricing.SalvageResourceCount(16, 1, 0.25m, new Random(1)) == 4
              && WorldSession.Salvageable(sword) && !WorldSession.Salvageable(Catalog.Find("T4_ORE")!),
              "salvage formulas: 384 × 0.75 = 288 silver at full durability (× 2 items at half: 288), resources 0.2 (salvageitemfactor over it), 3.2 → 4 without a random");

        using var town = new Town();
        var world = town.World;
        var c = StarterKit.Default.NewCharacter("Salvager");
        c.Silver = 10 * Pricing.A7m;
        c.Inventory = [new InventoryItem("T4_MAIN_SWORD") { Durability = sword.FullDurabilityRaw / 2 }, new InventoryItem("T4_ORE", 5), new InventoryItem("T4_MAIN_SWORD")];
        c.Equipment[0] = new InventoryItem("T4_MAIN_SWORD");
        Check(town.Characters.Create(c), "character Salvager created");
        var (session, peer, me) = town.Rejoin(c.Id);
        var repair = town.Station("T8_REPAIRSHOP");
        var blade = me.Inventory.Bag[0]!;
        var ore = me.Inventory.Bag[1]!;
        var spare = me.Inventory.Bag[2]!;
        var worn = me.Inventory.Equipment[0]!;
        void Op55(ActionOnBuildingStartRequest req)
        {
            lock (world.Sync) session.OnRequest(Request(req, 7), encrypted: false);
        }
        var shop = repair.AccessStatus();
        Check(shop.ddn == repair.Id && shop.bq5 == 2 && shop.bq9 is ["owner", "friend", "user"] && town.Station("T1_BANK").AccessStatus().bq9 is ["owner", "noaccess", "noaccess"],
              "the repair shop's AccessStatus lets everyone salvage (owner/friend/user); the bank's is owner/noaccess/noaccess");

        var answers = peer.Responses.Count;
        Op55(new ActionOnBuildingStartRequest { wf = world.GameNow, o = repair.Id, p = 3, q = 1, s = [ore.Id] });
        Check(Answers(peer, answers) is [{ ReturnCode: WorldSession.StationComponentError } bad] && L(bad.Parameters, 0) == repair.Id && L(bad.Parameters, 1) == 3
              && L(bad.Parameters, 2) == 1 && L(bad.Parameters, 3) == (byte)SalvageError.ItemNotSalvageable && me.Inventory.Bag[1] == ore,
              "ore: RC 2202 {0 building, 1 ACT 3, 2 counter, 3 ItemNotSalvageable}, the ore kept");
        answers = peer.Responses.Count;
        Op55(new ActionOnBuildingStartRequest { wf = world.GameNow, o = town.Station("T8_SMELTER").Id, p = 3, q = 2, s = [blade.Id] });
        Check(Answers(peer, answers) is [{ ReturnCode: WorldSession.StationGenericError }], "a smelter has no salvage: the generic RC 1");

        answers = peer.Responses.Count;
        var mark = peer.Events.Count;
        Op55(new ActionOnBuildingStartRequest { wf = world.GameNow, o = repair.Id, p = 3, q = 3, s = [blade.Id] });
        Check(Answers(peer, answers).Count == 0 && Since(peer, mark) is [{ Code: EventCodes.NewBuilding } inUse] && inUse.P[16] is true && me.Action is { Act: 3 },
              "the salvage starts: no response, NewBuilding with key 16");
        town.Run(1);
        var burst = Since(peer, mark).Skip(1).ToList();
        int Count(string name) => me.Inventory.Bag.Items.Where(i => i.Template.Name == name).Sum(i => i.Count);
        var codes = burst.Select(e => e.Code).Where(c => c is not (EventCodes.NewSimpleItem or EventCodes.InventoryPutItem)).ToList();
        Check(codes.SequenceEqual([EventCodes.InventoryDeleteItem, EventCodes.NewBuilding, EventCodes.ActionOnBuildingFinished])
              && L(burst[0].P, 0) == blade.Id && me.Inventory.FindItem(blade.Id) is null && me.Action is null
              && me.Character.Silver == 10 * Pricing.A7m && Count("T4_METALBAR") is 1 or 2 && Count("T4_LEATHER") is 0 or 1 && Count("T4_ORE") == 5,
              $"at the end: the sword deleted, no silver (no UpdateMoney), 1-2 bars and 0-1 leather, NewBuilding, 66 (got {Show(burst)})");
        var (bars, leather) = (Count("T4_METALBAR"), Count("T4_LEATHER"));

        // Destroyed before the end: the salvage is cancelled, nothing credited.
        mark = peer.Events.Count;
        Op55(new ActionOnBuildingStartRequest { wf = world.GameNow, o = repair.Id, p = 3, q = 4, s = [spare.Id] });
        lock (world.Sync) session.OnRequest(Request(new InventoryDestroyItemRequest { ri = spare.Id, rj = 44 }, null), encrypted: false);
        town.Run(1);
        Check(me.Action is null && me.Inventory.FindItem(spare.Id) is null && Count("T4_METALBAR") == bars && Count("T4_LEATHER") == leather
              && me.Character.Silver == 10 * Pricing.A7m && Since(peer, mark).Any(e => e.Code == EventCodes.ActionOnBuildingCancel)
              && !Since(peer, mark).Any(e => e.Code == EventCodes.ActionOnBuildingFinished),
              "a sword destroyed during its salvage: cancelled (65), no resources, no silver");

        // The worn sword (full durability): it leaves the equipment, 3-4 bars and 1-2 leather.
        mark = peer.Events.Count;
        Op55(new ActionOnBuildingStartRequest { wf = world.GameNow, o = repair.Id, p = 3, q = 5, s = [worn.Id] });
        town.Run(1);
        var wornBurst = Since(peer, mark);
        Check(me.Inventory.Equipment[0] is null && me.Inventory.FindItem(worn.Id) is null
              && wornBurst.Any(e => e.Code == EventCodes.InventoryDeleteItem && L(e.P, 0) == worn.Id) && wornBurst.Any(e => e.Code == EventCodes.CharacterEquipmentChanged)
              && Count("T4_METALBAR") - bars is 3 or 4 && Count("T4_LEATHER") - leather is 1 or 2,
              $"the worn sword: deleted, CharacterEquipmentChanged, 3-4 bars and 1-2 leather (got {Show(wornBurst)})");
        (bars, leather) = (Count("T4_METALBAR"), Count("T4_LEATHER"));

        // Saved: after a relog the swords are gone, the resources and the silver kept.
        lock (world.Sync) peer.Disconnect();
        world.Saves.Flush();
        var (_, _, again) = town.Rejoin(me.Character.Id);
        int Kept(string name) => again.Inventory.Bag.Items.Concat(again.Inventory.Equipment.Items).Where(i => i.Template.Name == name).Sum(i => i.Count);
        Check(Kept("T4_MAIN_SWORD") == 0 && again.Inventory.Bag.Items.Where(i => i.Template.Name == "T4_METALBAR").Sum(i => i.Count) == bars
              && Kept("T4_LEATHER") == leather && Kept("T4_ORE") == 5 && again.Character.Silver == 10 * Pricing.A7m,
              "after a relog: no sword, the bars, the leather, the ore and the silver as before");
    }

    /// <summary>
    /// Enchanting and the quality reroll (stageN enchant SPEC): a T4 sword with 300 runes → T4_MAIN_SWORD@1 in the same slot (quality 3
    /// and spells kept, a new object), 12 runes left, saved over a relog; a second sword without runes is refused (NotEnoughResources) and
    /// kept. A quality-1 sword rerolled at the repair shop always improves (RerollQualityChances q1 has no weight on 1): the price
    /// itemvalue 384 × 4.4 charged, event 67 {3 [the sword], 4 [the sword]}. The quality bonus shifts the craft roll upwards.
    /// </summary>
    private static void Enchanting()
    {
        using var town = new Town(meld: true);
        var world = town.World;
        var (session, peer, me) = town.Join("Enchanter", 5000,
            new InventoryItem("T4_MAIN_SWORD") { Quality = 3 }, new InventoryItem("T4_RUNE", 300), new InventoryItem("T4_MAIN_SWORD"));
        var meld = town.Station("T8_MELDBUILDING");
        var sword = me.Inventory.Bag[0]!;
        var spells = sword.Active.ToArray();
        void Op55(ActionOnBuildingStartRequest req)
        {
            lock (world.Sync) session.OnRequest(Request(req, 7), encrypted: false);
        }
        var mark = peer.Events.Count;
        Op55(new ActionOnBuildingStartRequest { wf = world.GameNow, o = meld.Id, p = 7, q = 1, s = [sword.Id] });
        town.Run(1);
        var made = me.Inventory.Bag[0];
        int Runes(Player p) => p.Inventory.Bag.Items.Where(i => i.Template.Name == "T4_RUNE").Sum(i => i.Count);
        Check(made is { Template.Name: "T4_MAIN_SWORD@1", Quality: 3 } && made.Id != sword.Id && made.Active.SequenceEqual(spells) && Runes(me) == 12
              && made.DurabilityRaw == made.Template.FullDurabilityRaw && me.Action is null
              && Since(peer, mark).Any(e => e.Code == EventCodes.InventoryDeleteItem && L(e.P, 0) == sword.Id)
              && Since(peer, mark).Any(e => e.Code == EventCodes.ActionOnBuildingFinished),
              $"T4 sword + 288 of 300 runes → T4_MAIN_SWORD@1 in slot 0, quality 3 and spells kept, 12 runes left (got {made}, {Runes(me)} runes; {Show(Since(peer, mark))})");
        var answers = peer.Responses.Count;
        var other = me.Inventory.Bag[2]!;
        Op55(new ActionOnBuildingStartRequest { wf = world.GameNow, o = meld.Id, p = 7, q = 2, s = [other.Id] });
        Check(Answers(peer, answers) is [{ ReturnCode: WorldSession.StationComponentError } bad] && L(bad.Parameters, 3) == (byte)EnchantError.NotEnoughResources
              && me.Inventory.Bag[2] == other && me.Action is null,
              "a second sword with 12 runes: RC 2202 NotEnoughResources, the sword kept");

        // The reroll at the repair shop: quality 1 always improves.
        var repair = town.Station("T8_REPAIRSHOP");
        var price = session.RerollPrice(other);
        Check(price == (long)Math.Round(384 * 4.4m * Pricing.A7m), $"the reroll price of a T4 sword at quality 1: 384 × 4.4 (got {price})");
        var silver = me.Character.Silver;
        mark = peer.Events.Count;
        Op55(new ActionOnBuildingStartRequest { wf = world.GameNow, o = repair.Id, p = 6, q = 3, s = [other.Id], r = price, t = [1], aa = [1] });
        town.Run(1);
        var finished = Since(peer, mark).FirstOrDefault(e => e.Code == EventCodes.ItemRerollQualityFinished).P;
        Check(other.Quality > 1 && me.Character.Silver == silver - price && finished is not null && L(finished, 2) == repair.Id
              && finished[3] is long[] better && better.SequenceEqual([other.Id]) && finished[4] is long[] all && all.SequenceEqual([other.Id]),
              $"reroll: quality 1 → {other.Quality}, {price / 10000.0} silver charged, event 67 {{2 shop, 3 [sword], 4 [sword]}} (got {Show(Since(peer, mark))})");

        lock (world.Sync) peer.Disconnect();
        world.Saves.Flush();
        var (_, _, again) = town.Rejoin(me.Character.Id);
        Check(again.Inventory.Bag[0] is { Template.Name: "T4_MAIN_SWORD@1", Quality: 3 } && Runes(again) == 12 && again.Inventory.Bag[2]?.Quality == other.Quality,
              "after a relog: the @1 sword, 12 runes and the rerolled quality kept");

        var weights = Client.GameData!.Economy.QualityWeights;
        var random = new Random(7);
        var plain = Enumerable.Range(0, 20_000).Count(_ => WorldSession.RollQuality(random, weights) > 1);
        var boosted = Enumerable.Range(0, 20_000).Count(_ => WorldSession.RollQuality(random, weights, 100) > 1);
        Check(Math.Abs(plain / 20_000.0 - 0.311) < 0.015 && Math.Abs(boosted / 20_000.0 - 622 / 1311.0) < 0.015,
              $"quality bonus 100: the chance above normal 31.1 % → 47.4 % (got {plain / 200.0:0.#} %, {boosted / 200.0:0.#} %)");
    }

    /// <summary>
    /// Awakening (stageN awakening SPEC §1-§2): a T4_MAIN_SWORD@4 with 2 UNIQUE_GVGTOKEN_GENERIC and 2 QUESTITEM_TOKEN_AVALON (100 × the T4
    /// scaling 0.0208, rounded) and 6000000 × 0.0208 silver gets a soul with 3 traits after 1.5 s: tokens and silver gone, NewEquipmentItem key
    /// 11 = 1, event 37 {0 the sword, 4 attuned to me, 8 three trait names}, saved over a relog. A T4 sword@3 is refused (enchantment &lt; 4).
    /// </summary>
    private static void Awakening()
    {
        using var town = new Town(meld: true);
        var world = town.World;
        var (session, peer, me) = town.Join("Awakener", 200_000, new InventoryItem("T4_MAIN_SWORD@4"), new InventoryItem("UNIQUE_GVGTOKEN_GENERIC", 3),
            new InventoryItem("QUESTITEM_TOKEN_AVALON", 2), new InventoryItem("T4_MAIN_SWORD@3"));
        var meld = town.Station("T8_MELDBUILDING");
        var sword = me.Inventory.Bag[0]!;
        var (price, resources) = session.AwakenCost(world.Legendary!, sword);
        void Op55(ActionOnBuildingStartRequest req)
        {
            lock (world.Sync) session.OnRequest(Request(req, 7), encrypted: false);
        }
        int Count(Player p, string name) => p.Inventory.Bag.Items.Where(i => i.Template.Name == name).Sum(i => i.Count);
        var silver = me.Character.Silver;
        var mark = peer.Events.Count;
        Op55(new ActionOnBuildingStartRequest { wf = world.GameNow, o = meld.Id, p = 8, q = 1, s = [sword.Id], ab = 0, ae = price });
        town.Run(2);
        var events = Since(peer, mark);
        var item = events.LastOrDefault(e => e.Code == EventCodes.NewEquipmentItem && L(e.P, 0) == sword.Id).P;
        var soul = events.LastOrDefault(e => e.Code == EventCodes.NewEquipmentItemLegendarySoul).P;
        Check(price == (long)Math.Round(6_000_000 * 0.020755693m * Pricing.A7m) && resources.All(r => r.Count == 2) && resources.Count == 2
              && sword.Soul is { Traits.Length: 3 } && sword.Soul.Traits.Select(t => t.Name).Distinct().Count() == 3
              && sword.Soul.Traits.All(t => t.Roll is >= 0.01 and <= 0.03) && me.Character.Silver == silver - price
              && Count(me, "UNIQUE_GVGTOKEN_GENERIC") == 1 && Count(me, "QUESTITEM_TOKEN_AVALON") == 0 && me.Action is null
              && item is not null && L(item, 11) == 1 && soul is not null && L(soul, 0) == sword.Id && soul[4] is true
              && soul[8] is string[] names && names.SequenceEqual(sword.Soul.Traits.Select(t => t.Name)) && soul[9] is double[] { Length: 3 },
              $"awakened: 3 traits rolled 0.01-0.03, {price / 10000.0} silver and 2 + 2 tokens taken, key 11 = 1, event 37 (got {sword.Soul?.Traits.Length} traits; {Show(events.Where(e => e.Code is EventCodes.NewEquipmentItem or EventCodes.NewEquipmentItemLegendarySoul))})");
        var answers = peer.Responses.Count;
        var low = me.Inventory.Bag[3]!;
        Op55(new ActionOnBuildingStartRequest { wf = world.GameNow, o = meld.Id, p = 8, q = 2, s = [low.Id], ab = 0, ae = price });
        Check(Answers(peer, answers) is [{ ReturnCode: WorldSession.StationComponentError } bad] && L(bad.Parameters, 3) == (byte)AwakenError.ItemEnchantmentTooLow && low.Soul is null,
              "a T4 sword@3: RC 2202 ItemEnchantmentTooLow");
        var traits = sword.Soul!.Traits;
        lock (world.Sync) peer.Disconnect();
        world.Saves.Flush();
        var (_, again, back) = town.Rejoin(me.Character.Id);
        var kept = back.Inventory.Bag[0];
        Check(kept is { Template.Name: "T4_MAIN_SWORD@4", Soul: { } s } && s.Traits.SequenceEqual(traits) && s.AttunedTo(me.Character.Id)
              && Since(again, 0).Any(e => e.Code == EventCodes.NewEquipmentItemLegendarySoul && L(e.P, 0) == kept.Id),
              $"after a relog: the soul with the same traits, sent with the Join items (got {kept}, soul {kept?.Soul is not null}, {string.Join(", ", kept?.Soul?.Traits.Select(t => $"{t.Name} {t.Roll}") ?? [])} vs {string.Join(", ", traits.Select(t => $"{t.Name} {t.Roll}"))})");
    }

    /// <summary>
    /// A trait value reroll (sub 4) on slot 0 of an awakened T4 sword@4 whose soul holds 1000 attunement: 6667 × 0.0208 attunement and × 33
    /// silver charged, event 552 {2 the sword, 3 slot 0, 4 old, 5 new ≥ old}, the soul re-sent. Then the sword worn with TRAIT_HITPOINTS_MAX at
    /// roll 1 adds hitpointsmax to the passives; attuned to someone else it adds none.
    /// </summary>
    private static void TraitReroll()
    {
        using var town = new Town(meld: true);
        var world = town.World;
        var (session, peer, me) = town.Join("Reroller", 100_000, new InventoryItem("T4_MAIN_SWORD@4"));
        var meld = town.Station("T8_MELDBUILDING");
        var sword = me.Inventory.Bag[0]!;
        var data = world.Legendary!;
        lock (world.Sync)
            sword.Soul = new ItemSoul
            {
                AttunedId = me.Character.Id, AttunedName = me.Character.Name, Attunement = 1000 * Pricing.A7m,
                Traits = [new SoulTrait("TRAIT_HITPOINTS_MAX", 0.02), new SoulTrait("TRAIT_CC_RESIST", 0.02), new SoulTrait("TRAIT_ABILITY_DAMAGE", 0.02)],
            };
        var (attunement, silverCost) = WorldSession.TraitCost(data, sword, data.RerollBase);
        var silver = me.Character.Silver;
        var mark = peer.Events.Count;
        lock (world.Sync)
            session.OnRequest(Request(new ActionOnBuildingStartRequest
                { wf = world.GameNow, o = meld.Id, p = 8, q = 1, s = [sword.Id], ab = 4, ac = 0, ae = silverCost, af = attunement }, 7), encrypted: false);
        town.Run(1.5);
        var done = Since(peer, mark).FirstOrDefault(e => e.Code == EventCodes.RerollItemTraitValueFinished).P;
        var roll = sword.Soul.Traits[0].Roll;
        Check(attunement == (long)Math.Round(6667 * 0.020755693m * Pricing.A7m) && silverCost == (long)Math.Round(attunement * 33m)
              && done is not null && L(done, 2) == sword.Id && !done.ContainsKey(3) && (double)done[4]! == 0.02 && (double)done[5]! == roll && roll > 0.02
              && sword.Soul.Attunement == 1000 * Pricing.A7m - attunement && me.Character.Silver == silver - silverCost
              && Since(peer, mark).Any(e => e.Code == EventCodes.NewEquipmentItemLegendarySoul && L(e.P, 0) == sword.Id),
              $"reroll slot 0: {attunement / 10000.0} attunement and {silverCost / 10000.0} silver, 0.02 -> {roll} (got {Show(Since(peer, mark).Where(e => e.Code == EventCodes.RerollItemTraitValueFinished))})");
        double Hp()
        {
            lock (world.Sync) return Passives.Of(me).Buffs.GetValueOrDefault("hitpointsmax");
        }
        lock (world.Sync)
        {
            me.Inventory.Bag.Take(0);
            me.Inventory.Equipment.Put(0, sword);
            sword.Soul = sword.Soul with { Traits = [new SoulTrait("TRAIT_HITPOINTS_MAX", 1), .. sword.Soul.Traits[1..]] };
        }
        var attuned = Hp();
        lock (world.Sync) sword.Soul = sword.Soul with { AttunedId = Guid.NewGuid() };
        Check(attuned >= 260 * 0.99 && Hp() == 0, $"TRAIT_HITPOINTS_MAX at roll 1: hitpointsmax {attuned:0.#} (at least 260 before the IP scaling); attuned to another: {Hp()}");
    }

    private static void Crafting()
    {
        using var town = new Town();
        var world = town.World;
        var board = world.GameData!.Board!;
        int Id(string node) => board.Find(node)!.Id;
        int refine = Id("CRAFT_REFINE_WOOD_T4"), swords = Id("CRAFT_SWORDS"), trainee = Id("CRAFT_TRAINEE");
        var (session, peer, me) = town.Join("Crafter", 100, new Progression { Levels = new() { [refine] = 0, [swords] = 0, [trainee] = 0 } },
            new InventoryItem("T3_WOOD", 20), new InventoryItem("T2_PLANKS", 10), new InventoryItem("T4_METALBAR", 16), new InventoryItem("T4_LEATHER", 8));
        var progression = me.Character.Progression!;
        var bag = me.Inventory.Bag;
        int Count(string name) => bag.Items.Where(i => i.Template.Name == name).Sum(i => i.Count);
        void Op55(ActionOnBuildingStartRequest req)
        {
            lock (world.Sync) session.OnRequest(Request(req, 7), encrypted: false);
        }

        // Refining: T3_PLANKS = 2 × T3_WOOD + 1 × T2_PLANKS, 10 crafts of 0.03125 s at the carpenter (no silver). Thetford wood
        // returns 1 − 1/1.18 of the resources (floored over the batch with the carry); each craft gives 8 × 1.5 = 12 fame to the
        // total and to CRAFT_REFINE_WOOD_T4 (REFINE_T4 row 0 takes T2-T8 planks), none to CRAFT_TRAINEE (T2 validitems only).
        var planks = Catalog.Find("T3_PLANKS")!;
        var answers = peer.Responses.Count;
        var mark = peer.Events.Count;
        Op55(new ActionOnBuildingStartRequest { wf = world.GameNow, o = town.Station("T8_CARPENTERSWORKSHOP").Id, p = 1, q = 1, u = planks.Index, w = 10 });
        town.Run(0.1);
        Check(Count("T3_PLANKS") == 3 && me.Character.Fame == 0 && !Since(peer, mark).Any(e => e.Code == EventCodes.UpdateFame),
              "three completed planks retain their output while fame waits for the batch settlement");
        town.Run(0.3);
        var refined = Since(peer, mark);
        var back = Pricing.ReturnFraction(Client.Crafting.Bonus("0000", "wood"));
        var progress = refined.Where(e => e.Code == EventCodes.AchievementProgressInfo).ToList();
        Check(Answers(peer, answers).Count == 0 && me.Action is null && Count("T3_PLANKS") == 10
              && Count("T3_WOOD") == (int)Math.Floor(20 * back + 1e-9) && Count("T2_PLANKS") == (int)Math.Floor(10 * back + 1e-9)
              && refined.Count(e => e.Code == EventCodes.CraftItemFinished) == 10 && refined.Count(e => e.Code == EventCodes.UpdateFame) == 1
              && L(refined.Single(e => e.Code == EventCodes.UpdateFame).P, 2) == 120 * Pricing.A7m
              && progress.All(e => L(e.P, 1) == refine) && progress.Count == 1 && L(progress[^1].P, 0) == me.Id
              && (string?)progress[^1].P[4] == "[[1200000]]" && me.Character.Fame == 120 * Pricing.A7m && progression.Progress.GetValueOrDefault(refine) == 120 * Pricing.A7m,
              $"10 x T3_PLANKS from 20 wood + 10 T2 planks, {back * 100:0.#} % back, one 120-fame award into CRAFT_REFINE_WOOD_T4 only (got planks {Count("T3_PLANKS")}, wood {Count("T3_WOOD")}, T2 {Count("T2_PLANKS")}, fame {me.Character.Fame}; {Show(refined.Where(e => e.Code is EventCodes.UpdateFame or EventCodes.AchievementProgressInfo).Take(4))})");

        // A T4 item: T4_MAIN_SWORD = 16 bars + 8 leather at the forge, one craft; a rolled quality, 384 × 1.5² = 864 fame into
        // CRAFT_SWORDS (CRAFT_BASE row 0: item tier 3-8), the craft's output before its fame and CraftItemFinished.
        mark = peer.Events.Count;
        Op55(new ActionOnBuildingStartRequest { wf = world.GameNow, o = town.Station("T8_FORGE").Id, p = 1, q = 2, u = Catalog.Find("T4_MAIN_SWORD")!.Index, w = 1 });
        town.Run(1);
        var forged = Since(peer, mark);
        var sword = bag.Items.SingleOrDefault(i => i.Template.Name == "T4_MAIN_SWORD");
        var order = forged.Select(e => e.Code).Where(c => c is EventCodes.NewEquipmentItem or EventCodes.UpdateFame or EventCodes.CraftItemFinished or EventCodes.ActionOnBuildingFinished).ToList();
        Check(sword is { Quality: >= 1 and <= 5 } && sword.DurabilityRaw == sword.Template.FullDurabilityRaw && Count("T4_METALBAR") < 16 && Count("T4_LEATHER") < 8
              && order.SequenceEqual([EventCodes.NewEquipmentItem, EventCodes.UpdateFame, EventCodes.CraftItemFinished, EventCodes.ActionOnBuildingFinished])
              && progression.Progress.GetValueOrDefault(swords) == 864 * Pricing.A7m && progression.Progress.GetValueOrDefault(refine) == 120 * Pricing.A7m && me.Character.Fame == 984 * Pricing.A7m,
              $"a T4 sword of quality {sword?.Quality}, full durability; 864 fame into CRAFT_SWORDS (got fame {me.Character.Fame}, bars {Count("T4_METALBAR")}; {Show(forged.Where(e => e.Code != EventCodes.NewSimpleItem))})");
    }

    private static void CraftBatchCancellation()
    {
        foreach (var logout in new[] { false, true })
        foreach (var made in new[] { 0, 3 })
        {
            using var town = new Town(config: new ServerConfig { JoinByCharacterId = true, CraftingReturns = false });
            var refine = town.World.GameData!.Board!.Find("CRAFT_REFINE_WOOD_T4")!.Id;
            var (session, peer, me) = town.Join("BatchCancel", 0, new Progression { Levels = new() { [refine] = 0 } },
                new InventoryItem("T3_WOOD", 20), new InventoryItem("T2_PLANKS", 10));
            var id = me.Character.Id;
            var mark = peer.Events.Count;
            lock (town.World.Sync) session.OnRequest(Request(new ActionOnBuildingStartRequest
            { wf = town.World.GameNow, o = town.Station("T8_CARPENTERSWORKSHOP").Id, p = 1, q = 1,
                u = Catalog.Find("T3_PLANKS")!.Index, w = 10 }, 7), false);
            town.Run(made == 0 ? 0 : 0.1);
            Check(me.Character.Fame == 0, "batch fame is not granted before settlement");
            void Move() => session.OnRequest(Request(new MoveRequest { wf = town.World.GameNow,
                v6 = [0f, 0f], v8 = [1f, 0f], v9 = 5.5f }, null), false);
            lock (town.World.Sync)
            {
                if (logout) session.OnDisconnected("partial crafting test");
                else Move();
            }
            var expected = made * 12 * Pricing.A7m;
            var events = Since(peer, mark).Where(e => e.Code == EventCodes.UpdateFame).ToArray();
            Check(events.Length == (made == 0 ? 0 : 1) && me.Character.Fame == expected
                && me.Character.Progression!.Progress.GetValueOrDefault(refine) == expected,
                "cancel/logout awards exactly the completed output once, including the destiny board");
            if (made > 0) Check(L(events[0].P, 2) == expected, "partial batch notification has the combined gain");
            if (!logout) lock (town.World.Sync) Move();
            town.Run(5);
            Check(me.Character.Fame == expected && Since(peer, mark).Count(e => e.Code == EventCodes.UpdateFame) == events.Length,
                "cancelled timers and repeated cancellation cannot award again");
            if (!logout) lock (town.World.Sync) session.OnDisconnected("persist batch settlement");
            town.World.Saves.Flush();
            var (_, _, restored) = town.Rejoin(id);
            int Count(string name) => restored.Inventory.Bag.Items.Where(i => i.Template.Name == name).Sum(i => i.Count);
            Check(restored.Character.Fame == expected && restored.Character.Progression!.Progress.GetValueOrDefault(refine) == expected
                && Count("T3_PLANKS") == made && Count("T3_WOOD") == 20 - made * 2 && Count("T2_PLANKS") == 10 - made,
                "completed output, exact refunds and settled fame survive reconnect");
        }
    }

    private static void CraftTiming()
    {
        var recipe = Catalog.Find("T2_PLANKS")!.Recipes[0];
        var duration = Pricing.CraftDuration(recipe, 48);
        Check(Math.Abs(duration.TotalSeconds - 0.99984) < 0.000001
            && Pricing.CraftDuration(Catalog.Find("T3_PLANKS")!.Recipes[0], 10).TotalSeconds == 0.3125,
            "actual client recipe times: 48 birch planks take about one second, ten T3 planks 0.3125 seconds");
        Check(Math.Abs(Pricing.CraftDuration(recipe, 48, 0.5).TotalSeconds - duration.TotalSeconds * 0.5) < 0.000001
            && Math.Abs(Pricing.CraftDuration(recipe, 48, 1).TotalSeconds - duration.TotalSeconds * 0.1) < 0.000001,
            "client CraftTime reduction and minimum factor 0.1 are respected");
        using var town = new Town(config: new ServerConfig { JoinByCharacterId = true, CraftingReturns = false });
        var (session, peer, me) = town.Join("TimingProbe", 0, new InventoryItem("T2_WOOD", 48));
        var mark = peer.Events.Count;
        lock (town.World.Sync) session.OnRequest(Request(new ActionOnBuildingStartRequest { wf = town.World.GameNow,
            o = town.Station("T8_CARPENTERSWORKSHOP").Id, p = 1, q = 1, u = Catalog.Find("T2_PLANKS")!.Index, w = 48 }, 7), false);
        Check(me.Action is { } action && action.End - action.Start == duration.Ticks, "server action deadline matches client batch calculation");
        // Advance real server time without a map tick, reproducing movement just after the client bar ends.
        town.Clock.Advance(duration + TimeSpan.FromMilliseconds(1));
        lock (town.World.Sync) session.OnRequest(Request(new MoveRequest { wf = town.World.GameNow,
            v6 = [0f, 0f], v8 = [1f, 0f], v9 = 5.5f }, null), false);
        int Count(string name) => me.Inventory.Bag.Items.Where(i => i.Template.Name == name).Sum(i => i.Count);
        var events = Since(peer, mark);
        Check(me.Action is null && Count("T2_PLANKS") == 48 && Count("T2_WOOD") == 0
            && events.Count(e => e.Code == EventCodes.CraftItemFinished) == 48
            && events.Count(e => e.Code == EventCodes.ActionOnBuildingFinished) == 1
            && !events.Any(e => e.Code == EventCodes.ActionOnBuildingCancel)
            && events.Count(e => e.Code == EventCodes.UpdateFame) == 1 && me.Character.Fame == 192 * Pricing.A7m,
            "movement after the client deadline finishes every due plank and one fame award instead of refunding wood");
        town.Run(1);
        Check(Count("T2_PLANKS") == 48 && me.Character.Fame == 192 * Pricing.A7m
            && Since(peer, mark).Count(e => e.Code == EventCodes.CraftItemFinished) == 48,
            "old timer callbacks cannot duplicate settled output or fame");
    }

    /// <summary>
    /// Stage N stations (analysis/work/stageN/stations/SPEC.md): Lymhurst, Bridgewatch, Martlock, Fort Sterling and Caerleon ship the
    /// 12 public stations of Thetford, each on a RealEstate plot centre of the city's template; the station handler takes the return
    /// bonus of the crafter's map, so a Martlock tannery returns hide at Martlock's 0.18 + 0.40 and Thetford's at 0.18.
    /// </summary>
    private static void CityStations()
    {
        var stations = StationSpawns.Load(Path.Combine(DataDir(), "..", "content", "spawns"));
        var store = Client.Clusters!;
        var bad = new List<string>();
        foreach (var city in new[] { "1000", "2000", "3004", "4000", "3003" })
        {
            var plots = store.Get(city).RealEstates.Select(r => r.Position).ToHashSet();
            var list = stations.For(city);
            if (list.Count != 13 || list.Select(e => e.Building).Distinct().Count() != 13 || !list.Any(e => e.Building == "T8_STABLE")) bad.Add($"{city}: {list.Count} stations");
            bad.AddRange(list.Where(e => store.Buildings.Find(e.Building) is null || !plots.Contains(e.Position)).Select(e => $"{city}: {e.Building} at {e.Position}"));
        }
        if (!stations.For("0000").Concat(stations.For("5000")).Any(e => e.Building == "T8_STABLE")) bad.Add("no stable in Thetford or Brecilien");
        var martlock = Pricing.ReturnFraction(Client.Crafting.Bonus("3004", "hide"));
        Check(bad.Count == 0 && stations.For("3004").Any(e => e.Building == "T8_TANNERY")
              && Math.Abs(martlock - Pricing.ReturnFraction(0.58)) < 1e-9 && martlock > Pricing.ReturnFraction(Client.Crafting.Bonus("0000", "hide")),
              $"12 known stations and a stable per city on plot centres, Martlock hide returns {martlock * 100:0.#} % (problems: {string.Join("; ", bad)})");
    }

    /// <summary>
    /// Stage N mounts (analysis/work/stageN/mounts/SPEC.md): T3_MOUNT_HORSE is in the stable's craftingitemlist; its recipe takes one
    /// T3_FARM_HORSE_GROWN (maxreturnamount 0: never returned) and 20 T3_LEATHER; the horse comes out as an equipment item with a quality.
    /// </summary>
    private static void StableCraft()
    {
        using var town = new Town(stable: true);
        var world = town.World;
        var (session, _, me) = town.Join("Rider", 10, new InventoryItem("T3_FARM_HORSE_GROWN", 1), new InventoryItem("T3_LEATHER", 20));
        lock (world.Sync)
            session.OnRequest(Request(new ActionOnBuildingStartRequest { wf = world.GameNow, o = town.Station("T8_STABLE").Id, p = 1, q = 1, u = Catalog.Find("T3_MOUNT_HORSE")!.Index, w = 1 }, 7), encrypted: false);
        town.Run(Pricing.CraftDuration(Catalog.Find("T3_MOUNT_HORSE")!.Recipes[0], 1).TotalSeconds + 0.1);
        var bag = me.Inventory.Bag.Items.ToList();
        Check(bag.Any(i => i.Template.Name == "T3_MOUNT_HORSE" && i.Quality >= 1) && !bag.Any(i => i.Template.Name == "T3_FARM_HORSE_GROWN") && me.Action is null,
              $"a T3 horse, the grown horse used up (bag: {string.Join(", ", bag.Select(i => $"{i.Count} x {i.Template.Name}"))})");
    }

    private static void Housekeeping()
    {
        using var town = new Town();
        var world = town.World;
        var (session, peer, me) = town.Join("Tidy", 0, new InventoryItem("T4_ORE", 10));
        var bag = me.Inventory.Bag;
        var ore = bag[0]!;
        var mark = peer.Events.Count;
        lock (world.Sync) session.OnRequest(Request(new InventorySplitStackRequest { sd = 0, se = bag.Id, sf = 41, sg = 4 }, null), encrypted: false);
        var split = Since(peer, mark);
        Check(split is [{ Code: EventCodes.NewSimpleItem } source, { Code: EventCodes.NewSimpleItem } made, { Code: EventCodes.InventoryPutItem } put]
              && L(source.P, 0) == ore.Id && L(source.P, 2) == 6 && L(made.P, 2) == 4 && L(put.P, 0) == L(made.P, 0) && L(put.P, 1) == 1 && L(put.P, 3) == 41
              && ore.Count == 6 && bag[1]!.Count == 4, $"split 10 → 6 + a new object of 4 in slot 1, revision 41 echoed (got {Show(split)})");
        mark = peer.Events.Count;
        lock (world.Sync) session.OnRequest(Request(new InventoryStackRequest { so = bag.Id, sp = 42 }, null), encrypted: false);
        var stacked = Since(peer, mark);
        Check(ore.Count == 10 && bag[1] is null && stacked.Any(e => e.Code == EventCodes.InventoryDeleteItem && L(e.P, 1) == 42), "stack all: one stack of 10 again, the emptied one deleted with the revision");
        mark = peer.Events.Count;
        lock (world.Sync) session.OnRequest(Request(new InventoryDestroyItemRequest { ri = ore.Id, rj = 43 }, null), encrypted: false);
        Check(bag.Count == 0 && Since(peer, mark) is [{ Code: EventCodes.InventoryDeleteItem } gone] && L(gone.P, 0) == ore.Id && L(gone.P, 1) == 43, "destroy: InventoryDeleteItem {0 id, 1 revision}");
    }

    private static void Bank()
    {
        using var town = new Town();
        var world = town.World;
        var (session, peer, me) = town.Join("Saver", 0, new InventoryItem("T4_ORE", 10));
        var bank = town.Station("T1_BANK");
        OperationResponse? Open(WorldSession s, FakePeer p, long building, Guid tab, bool register, int view = 0)
        {
            var before = p.Responses.Count;
            lock (world.Sync)
            {
                if (register) s.OnRequest(Request(new RegisterToObjectRequest { zg = building }, null), encrypted: false);
                s.OnRequest(Request(new ContainerOpenRequest { hi = building, hj = view, hk = tab }, 9), encrypted: false);
            }
            return p.Responses.Skip(before).FirstOrDefault();
        }
        // RegisterToObject alone, as the client's bank window sends it (no ContainerOpen for the main view): the events since, no response.
        (List<(EventCodes Code, Dictionary<byte, object?> P)> Events, int Responses) Register(WorldSession s, FakePeer p, long building)
        {
            var (events, responses) = (p.Events.Count, p.Responses.Count);
            lock (world.Sync) s.OnRequest(Request(new RegisterToObjectRequest { zg = building }, null), encrypted: false);
            return (Since(p, events), p.Responses.Count - responses);
        }
        void Move(WorldSession s, Container from, int fromSlot, Container to, int toSlot)
        {
            lock (world.Sync)
                s.OnRequest(Request(new InventoryMoveItemRequest { ru = fromSlot, rv = from.Id, rx = toSlot, ry = to.Id, rz = 1 }, null), encrypted: false);
        }
        Check(Open(session, peer, bank.Id, WorldSession.DefaultTab, register: false) is { ReturnCode: not 0 } refused && L(refused.Parameters, 0) == bank.Id
              && refused.Parameters[2] is byte[] tab && new Guid(tab) == WorldSession.DefaultTab, "ContainerOpen without RegisterToObject: RC ≠ 0 with keys 0-2 echoed");
        var market = town.Station("MARKETPLACE");
        Check(Open(session, peer, market.Id, WorldSession.DefaultTab, register: true) is { ReturnCode: not 0 } && Open(session, peer, bank.Id + 1_000_000, WorldSession.DefaultTab, register: true) is { ReturnCode: not 0 },
              "ContainerOpen of a building that is no bank, or of an object not on the player's map (another cluster's bank): refused");
        lock (world.Sync) session.OnRequest(Request(new UnRegisterFromObjectRequest { ahc = bank.Id }, null), encrypted: false);
        Check(me.OpenVaults.Count == 0 && Register(session, peer, market.Id) is { Events: [], Responses: 0 }, "RegisterToObject of the market: no vault, no answer");
        var home = me.PositionAt(DateTime.UtcNow);
        lock (world.Sync) me.Teleport(bank.Position + new Vector2(WorldSession.StationRange + 5, 0), 0, DateTime.UtcNow);
        Check(Register(session, peer, bank.Id) is { Events: [], Responses: 0 } && me.OpenVaults.Count == 0, "RegisterToObject of the bank from beyond StationRange: no vault");
        lock (world.Sync) me.Teleport(home, 0, DateTime.UtcNow);
        Check(Register(session, peer, bank.Id) is { Events: [{ Code: EventCodes.AttachItemContainer } attach], Responses: 0 }
              && L(attach.P, 0) == bank.Id && attach.P[3] is long[] { Length: 128 } && new Guid((byte[])attach.P[2]!) == WorldSession.DefaultTab
              && attach.P[6] is false && me.OpenVaults.Values.Single().Views.SetEquals([0]),
              "RegisterToObject of the bank: the empty default tab at once, AttachItemContainer {0 bank, 2 tab, 3 long[128], 6 false} into view 0, no response");
        var mark = peer.Responses.Count;
        lock (world.Sync) session.OnRequest(Request(new LoadoutReadHeadersRequest { uc = me.Character.Id }, 7), encrypted: false);
        Check(peer.Responses.Skip(mark).SingleOrDefault() is { ReturnCode: 0 } loadouts && loadouts.Parameters[0] is byte[] echoed && new Guid(echoed) == me.Character.Id
              && !loadouts.Parameters.ContainsKey(2) && !loadouts.Parameters.ContainsKey(6), "LoadoutReadHeaders: {0 the request's Guid} without lists, as official");
        var vault = me.OpenVaults.Values.Single().Container;
        var bag = me.Inventory.Bag;
        var ore = bag[0]!;

        // A move beyond the 128 slots is ignored (no event); one over the weight limit (made 0 here) puts the stack back.
        mark = peer.Events.Count;
        Move(session, bag, 0, vault, 128);
        var ignored = Since(peer, mark).Count == 0;
        var id = vault.Id;
        var limited = me.OpenVaults[id];
        me.OpenVaults[id] = limited with { WeightLimit = 0 };
        mark = peer.Events.Count;
        Move(session, bag, 0, vault, 5);
        Check(ignored && vault.Count == 0 && bag[0] == ore && Since(peer, mark) is [{ Code: EventCodes.InventoryPutItem } back] && L(back.P, 0) == ore.Id && !back.P.ContainsKey(1), // slot 0: a default key is left out
              "a move beyond the vault's slots is ignored; one over its weight limit is refused and the ore put back into inventory slot 0");
        me.OpenVaults[id] = limited;

        // Deposit, relog, withdraw, relog.
        Move(session, bag, 0, vault, 5);
        Check(vault[5] == ore && bag.Count == 0, "InventoryMoveItem with the vault's container Guid moves the ore to vault slot 5");
        lock (world.Sync) peer.Disconnect();
        world.Saves.Flush();
        var (again, peer2, me2) = town.Rejoin(me.Character.Id);
        Check(Register(again, peer2, bank.Id) is { Events: [{ Code: EventCodes.NewSimpleItem } item, { Code: EventCodes.AttachItemContainer } attached] }
              && attached.P[3] is long[] ids && ids[5] == L(item.P, 0) && L(item.P, 2) == 10 && new Guid((byte[])attached.P[1]!) == id,
              "after a relog RegisterToObject shows the ore in vault slot 5 (the same container Guid); its NewSimpleItem precedes event 99");
        Check(Open(again, peer2, bank.Id, Guid.NewGuid(), register: true) is { ReturnCode: not 0 }, "a tab that is not the default one is refused");
        var vault2 = me2.OpenVaults[id].Container;
        mark = peer2.Events.Count;
        Move(again, vault2, 5, me2.Inventory.Bag, 3);
        Check(vault2.Count == 0 && me2.Inventory.Bag[3] is { Count: 10 } withdrawn && withdrawn.Template.Name == "T4_ORE"
              && Since(peer2, mark) is [{ Code: EventCodes.InventoryPutItem } put] && L(put.P, 1) == 3, "withdraw: vault slot 5 → inventory slot 3 (InventoryPutItem)");

        var stack = me2.Inventory.Bag[3]!;
        // Shift+click: InventoryMoveGivenItems 4 of the 10 into the vault (a new object there), then those 4 back onto the stack.
        void Given(Container from, Container to, Item item, short count)
        {
            lock (world.Sync)
                again.OnRequest(Request(new InventoryMoveGivenItemsRequest { ro = from.Id, rp = 11, rq = to.Id, rr = 12, rs = [item.Id], rt = [count] }, null), encrypted: false);
        }
        mark = peer2.Events.Count;
        Given(me2.Inventory.Bag, vault2, stack, 4);
        var part = vault2[0];
        Check(stack.Count == 6 && part is { Count: 4 } && part != stack
              && Since(peer2, mark) is [{ Code: EventCodes.InventoryState } s1, { Code: EventCodes.NewSimpleItem } n1, { Code: EventCodes.NewSimpleItem } n2, { Code: EventCodes.InventoryState } s2]
              && new Guid((byte[])s1.P[2]!) == me2.Inventory.Bag.Id && L(s1.P, 3) == 11 && new Guid((byte[])s2.P[2]!) == vault2.Id && L(s2.P, 3) == 12
              && ((long[])s2.P[0]!)[0] == part.Id && L(n1.P, 0) == part.Id && L(n2.P, 0) == stack.Id,
              "shift+click into the vault: 4 of 10 as a new object in vault slot 0; InventoryState of the inventory {3 key 1}, the objects, InventoryState of the vault {3 key 3}");
        Given(vault2, me2.Inventory.Bag, part!, 0);
        Check(vault2.Count == 0 && stack.Count == 10 && me2.Inventory.Bag.Count == 1, "shift+click out of the vault: all 4 back onto the inventory stack of 6");
        var heavy = me2.OpenVaults[id];
        me2.OpenVaults[id] = heavy with { WeightLimit = 0 };
        Given(me2.Inventory.Bag, vault2, stack, 0);
        Check(vault2.Count == 0 && stack.Count == 10, "shift+click over the vault's weight limit: nothing moves");
        me2.OpenVaults[id] = heavy;
        Given(me2.Inventory.Bag, vault2, stack, 0);
        Check(vault2[0] == stack && me2.Inventory.Bag.Count == 0, "shift+click of the whole stack: the object itself into vault slot 0");
        lock (world.Sync) peer2.Disconnect();
        world.Saves.Flush();
        (again, peer2, me2) = town.Rejoin(me.Character.Id);
        Check(Register(again, peer2, bank.Id) is { Events: [{ Code: EventCodes.NewSimpleItem } kept, { Code: EventCodes.AttachItemContainer } still] }
              && ((long[])still.P[3]!)[0] == L(kept.P, 0) && L(kept.P, 2) == 10, "after a relog the shift+clicked stack is still in vault slot 0");
        vault2 = me2.OpenVaults[id].Container;
        Move(again, vault2, 0, me2.Inventory.Bag, 3);

        // Both views on the tab: closing one keeps it open, closing the other detaches it.
        Check(Open(again, peer2, bank.Id, WorldSession.DefaultTab, register: false, view: 1) is { ReturnCode: 0 } && me2.OpenVaults.Count == 1, "the additional view (slot 1) on the same tab: the same container");
        void Close(int view)
        {
            lock (world.Sync) again.OnRequest(Request(new ContainerCloseRequest { g6 = bank.Id, g7 = view, g8 = WorldSession.DefaultTab }, 9), encrypted: false);
        }
        Close(0);
        var open = me2.OpenVaults.Count;
        mark = peer2.Events.Count;
        Close(1);
        Check(open == 1 && me2.OpenVaults.Count == 0 && Since(peer2, mark) is [{ Code: EventCodes.DetachItemContainer } detach] && new Guid((byte[])detach.P[0]!) == id,
              "closing view 0 keeps the tab open, closing view 1 too detaches it (DetachItemContainer {0 container Guid})");
        Open(again, peer2, bank.Id, WorldSession.DefaultTab, register: true);
        mark = peer2.Events.Count;
        lock (world.Sync) again.OnRequest(Request(new UnRegisterFromObjectRequest { ahc = bank.Id }, null), encrypted: false);
        Move(again, me2.Inventory.Bag, 3, vault2, 0);
        Check(me2.OpenVaults.Count == 0 && me2.Inventory.Bag[3] is { Count: 10 } && Since(peer2, mark) is [{ Code: EventCodes.DetachItemContainer }],
              "UnRegisterFromObject of the bank detaches its open tab; a move with the vault's Guid after it is ignored");
        lock (world.Sync) peer2.Disconnect();
        world.Saves.Flush();
        var (third, peer3, me3) = town.Rejoin(me.Character.Id);
        mark = peer3.Events.Count;
        Check(me3.Inventory.Bag[3] is { Count: 10 } && Register(third, peer3, bank.Id) is { Events: [{ Code: EventCodes.AttachItemContainer } empty] }
              && ((long[])empty.P[3]!).All(i => i == 0),
              "after another relog the ore is in inventory slot 3 and the vault is empty");
    }

    private static void Market()
    {
        var oreType = Catalog.Find("T4_ORE")!;
        using var town = new Town();
        var world = town.World;
        var (sellerSession, sellerPeer, seller) = town.Join("Seller", 1000, new InventoryItem("T4_ORE", 5), new InventoryItem("T4_WOOD", 6));
        var (buyerSession, buyerPeer, buyer) = town.Join("Buyer", 1000, new InventoryItem("T4_ORE", 2));
        var market = town.Station("MARKETPLACE");
        OperationResponse Call(WorldSession s, FakePeer p, AlbionRequest req)
        {
            var before = p.Responses.Count;
            lock (world.Sync) s.OnRequest(Request(req, 3), encrypted: false);
            return p.Responses.Skip(before).Last();
        }
        Check(Call(buyerSession, buyerPeer, new AuctionGetOffersRequest { bqs = market.Id }) is { ReturnCode: 0 } empty && empty.Parameters[0] is string[] { Length: 0 }
              && Call(buyerSession, buyerPeer, new AuctionGetFinishedAuctionsCountRequest { bqs = market.Id }).Parameters.Keys.All(k => k >= 250),
              "an empty market: GetOffers {0 []}, FinishedCount {}");

        // Silver in a7m raw: the seller pays 12.5 setup fee for 5 × 100, gets 300 − 24 tax for 3 sold, pays 200 + 5 for a buy order.
        static long S(decimal silver) => (long)(silver * Pricing.A7m);
        var ore = seller.Inventory.Bag[0]!;
        Check(Call(sellerSession, sellerPeer, new AuctionCreateOfferRequest { bqs = market.Id, bn5 = 5, bn6 = ore.Id, bn7 = 100 * Pricing.A7m, bn8 = 24 }) is { ReturnCode: 0 }
              && seller.Inventory.Bag[0] is null && seller.Character.Silver == S(987.5m) && world.Market!.OrdersOf(seller.Character.Id).Single().Expires - DateTime.UtcNow is { TotalHours: > 23.9 and <= 24 },
              "an offer of 5 ore at 100 for 24 h: the stack leaves the inventory, the setup fee 12.5 is paid");
        var offers = (string[])Call(buyerSession, buyerPeer, new AuctionGetOffersRequest { bqs = market.Id }).Parameters[0]!;
        using var dto = JsonDocument.Parse(offers.Single());
        string[] fields = ["Id", "UnitPriceSilver", "TotalPriceSilver", "DistanceFee", "Amount", "Tier", "IsFinished", "AuctionType", "HasBuyerFetched", "HasSellerFetched",
            "SellerCharacterId", "SellerName", "BuyerCharacterId", "BuyerName", "ItemTypeId", "ItemGroupTypeId", "EnchantmentLevel", "QualityLevel", "Expires", "ReferenceId", "LocationId"];
        var root = dto.RootElement;
        Check(root.EnumerateObject().Select(p => p.Name).SequenceEqual(fields) && root.GetProperty("TotalPriceSilver").GetInt64() == 5_000_000 && root.GetProperty("UnitPriceSilver").GetInt64() % 10_000 == 0
              && root.GetProperty("AuctionType").GetString() == "offer" && root.GetProperty("SellerName").ValueKind == JsonValueKind.Null && root.GetProperty("Id").GetInt64() >= 1_000_000_000,
              $"the DTO: the official fields in order, total = unit × amount, no counterparty (got {offers.Single()})");
        var id = root.GetProperty("Id").GetInt64();
        string[] Offers(AuctionGetOffersRequest r) => (string[])Call(buyerSession, buyerPeer, r).Parameters[0]!;
        Check(Offers(new() { bqs = market.Id, bpe = "crafting", bpf = "resources", bpk = "4", bpi = "1", bpn = "0", bpp = 50 }).Length == 1
              && Offers(new() { bqs = market.Id, bpe = "weapons" }).Length == 0 && Offers(new() { bqs = market.Id, bpk = "5" }).Length == 0
              && Offers(new() { bqs = market.Id, bpi = "2", bpj = 2 }).Length == 0 && Offers(new() { bqs = market.Id, bpi = "2", bpj = 1 }).Length == 1
              && Offers(new() { bqs = market.Id, bpn = "1" }).Length == 0 && Offers(new() { bqs = market.Id, bpl = [oreType.Index] }).Length == 1
              && Offers(new() { bqs = market.Id, bpe = "UNKNOWN_LABEL" }).Length == 1
              && Call(buyerSession, buyerPeer, new AuctionGetOffersRequest { bqs = market.Id, bpp = 50, bpq = 50 }) is { Parameters: var page2 } && page2[0] is string[] { Length: 0 } && (!page2.TryGetValue(1, out var more) || more is false),
              "filters: categories crafting/resources, tier 4, quality 1 (2 at least: none, 2 at most: it), enchantment 0, the item type id; weapons, tier 5 or enchantment 1 find nothing, an unknown category string is ignored; page 2 is empty");
        Check(Call(buyerSession, buyerPeer, new AuctionBuyOfferRequest { bqs = market.Id, bn2 = 6, bn3 = id }) is { ReturnCode: WorldSession.MarketRefused } && buyer.Character.Silver == 1000 * Pricing.A7m,
              "buying more than offered is refused, nothing changes");
        Check(Call(buyerSession, buyerPeer, new AuctionBuyOfferRequest { bqs = market.Id, bn2 = 3, bn3 = id }) is { ReturnCode: 0 }
              && buyer.Character.Silver == 700 * Pricing.A7m && seller.Character.Silver == S(1263.5m)
              && Call(buyerSession, buyerPeer, new AuctionGetFinishedAuctionsCountRequest { bqs = market.Id }) is var count && L(count.Parameters, 0) == 1,
              "buying 3: buyer −300, seller +276 (300 less 8 % tax), one finished entry for the buyer");
        var finished = (string[])Call(buyerSession, buyerPeer, new AuctionGetFinishedAuctionsRequest { bqs = market.Id }).Parameters[0]!;
        var entry = JsonDocument.Parse(finished.Single()).RootElement.GetProperty("Id").GetInt64();
        Check(Call(buyerSession, buyerPeer, new AuctionFetchAuctionRequest { bqs = market.Id, boe = entry }) is { ReturnCode: 0 }
              && buyer.Inventory.Bag.Items.Single(i => i.Template.Name == "T4_ORE").Count == 5
              && Call(buyerSession, buyerPeer, new AuctionGetFinishedAuctionsCountRequest { bqs = market.Id }).Parameters.Keys.All(k => k >= 250),
              "fetching it puts the 3 ore onto the buyer's stack (5); the finished count is 0 again");

        // A buy order: the seller escrows 4 × 50 and pays 5 setup fee; the buyer sells 2 ore into it (100 less 8 tax); the rest is aborted and refunded.
        var orePrice = 50 * Pricing.A7m;
        Check(Call(sellerSession, sellerPeer, new AuctionCreateRequestRequest { bqs = market.Id, bn9 = 1, boa = 4, bob = oreType.Index, bod = orePrice }) is { ReturnCode: 0 }
              && seller.Character.Silver == S(1058.5m), "a buy order of 4 ore at 50 escrows 200 and pays the setup fee 5");
        var requests = (string[])Call(buyerSession, buyerPeer, new AuctionGetRequestsRequest { bqs = market.Id }).Parameters[0]!;
        var request = JsonDocument.Parse(requests.Single()).RootElement.GetProperty("Id").GetInt64();
        var slot = buyer.Inventory.Bag.IndexOf(buyer.Inventory.Bag.Items.Single(i => i.Template == oreType));
        // The quick-sell query (op 488, ops-audit SPEC §4): the buyer's 5 ore can sell 4 into it; an unknown item id is left
        // out, and so is the seller's wood (no buy order for it).
        var stack = buyer.Inventory.Bag[slot]!;
        var quick = Call(buyerSession, buyerPeer, new QuickSellAuctionQueryActionRequest { bqs = market.Id, bqt = [stack.Id, 987654321] });
        Check(quick is { ReturnCode: 0 } && quick.Parameters[0] is long[] and [var quickId] && quickId == stack.Id && quick.Parameters[2] is int[] and [4]
              && quick.Parameters[1] is string[] and [var quickDto] && JsonDocument.Parse(quickDto).RootElement.GetProperty("Id").GetInt64() == request
              && Call(sellerSession, sellerPeer, new QuickSellAuctionQueryActionRequest { bqs = market.Id, bqt = [seller.Inventory.Bag.Items.First().Id] }).Parameters[0] is long[] { Length: 0 },
              "quick-sell query: [the ore stack] → that buy order, 4 of the 5 (its amount); an unknown id and wood without a buy order get none");
        Check(Call(buyerSession, buyerPeer, new AuctionSellSpecificItemRequestRequest { bqs = market.Id, bqe = request, bqf = oreType.Index, bqg = slot, bqh = 2 }) is { ReturnCode: 0 }
              && buyer.Character.Silver == 792 * Pricing.A7m && buyer.Inventory.Bag[slot]!.Count == 3 && world.Market!.FinishedOf(seller.Character.Id).Count == 1,
              "selling 2 ore into it: the seller of the ore +92 (100 less 8 tax), 2 ore gone, the order's owner gets a finished entry");
        Check(Call(sellerSession, sellerPeer, new AuctionAbortAuctionRequest { bqs = market.Id, bny = request }) is { ReturnCode: 0 } && seller.Character.Silver == S(1158.5m)
              && world.Market!.OrdersOf(seller.Character.Id, MarketOrder.Request).Count == 0, "aborting the rest refunds the 100 left in escrow, not the setup fee");
        Check(Call(buyerSession, buyerPeer, new AuctionCreateRequestRequest { bqs = market.Id, bn9 = 1, boa = 1000, bob = oreType.Index, bod = Pricing.A7m }) is { ReturnCode: WorldSession.MarketRefused }
              && buyer.Character.Silver == 792 * Pricing.A7m, "a buy order of 1000 ore at 1 with 792 silver (escrow 1000 + fee 25) is refused, nothing changes");

        // Player A sells while online, goes offline; B buys; A's silver (less the tax) waits in the database for A's next Join,
        // and A collects the 2 ore bought by its buy order from the finished list.
        var wood = seller.Inventory.Bag.Items.Single(i => i.Template.Name == "T4_WOOD");
        Check(Call(sellerSession, sellerPeer, new AuctionCreateOfferRequest { bqs = market.Id, bn5 = 6, bn6 = wood.Id, bn7 = 5 * Pricing.A7m }) is { ReturnCode: 0 }
              && seller.Character.Silver == S(1157.5m), "an offer of 6 wood at 5 pays the least setup fee, 1 silver (2.5 % of 30 is 0.75)");
        var sellerId = seller.Character.Id;
        lock (world.Sync) sellerPeer.Disconnect();
        world.Saves.Flush();
        var woodOffer = JsonDocument.Parse(Offers(new() { bqs = market.Id, bpl = [Catalog.Find("T4_WOOD")!.Index] }).Single()).RootElement.GetProperty("Id").GetInt64();
        Check(Call(buyerSession, buyerPeer, new AuctionBuyOfferRequest { bqs = market.Id, bn2 = 6, bn3 = woodOffer }) is { ReturnCode: 0 } && buyer.Character.Silver == 762 * Pricing.A7m
              && world.Market!.Order(woodOffer) is null, "B buys all 6 wood from the offline A: B −30, the offer is gone");
        var (againSession, againPeer, again) = town.Rejoin(sellerId);
        Check(again.Character.Silver == S(1157.5m + 27.6m) && world.Market!.TakeCredits(sellerId) == 0,
              "A's next Join brings the 30 less 2.4 tax (+27.6), taken from the database once");
        var mine = (string[])Call(againSession, againPeer, new AuctionGetFinishedAuctionsRequest { bqs = market.Id }).Parameters[0]!;
        Check(Call(againSession, againPeer, new AuctionFetchFinishedAuctionsRequest { bqs = market.Id }) is { ReturnCode: 0 } collected && collected.Parameters[0] is byte and 4 && mine.Length == 1
              && again.Inventory.Bag.Items.Single(i => i.Template == oreType).Count == 2 && world.Market!.FinishedOf(sellerId).Count == 0,
              "A collects all its finished entries: the 2 ore of its buy order");
    }

    private static void PriceHistory()
    {
        var oreType = Catalog.Find("T4_ORE")!;
        using var town = new Town();
        var world = town.World;
        var (sellerSession, sellerPeer, seller) = town.Join("Seller", 1000, new InventoryItem("T4_ORE", 5));
        var (buyerSession, buyerPeer, _) = town.Join("Buyer", 1000);
        var market = town.Station("MARKETPLACE");
        OperationResponse Call(WorldSession s, FakePeer p, AlbionRequest req)
        {
            var before = p.Responses.Count;
            lock (world.Sync) s.OnRequest(Request(req, 3), encrypted: false);
            return p.Responses.Skip(before).Last();
        }
        Call(sellerSession, sellerPeer, new AuctionCreateOfferRequest { bqs = market.Id, bn5 = 5, bn6 = seller.Inventory.Bag[0]!.Id, bn7 = 100 * Pricing.A7m, bn8 = 24 });
        var id = world.Market!.OrdersOf(seller.Character.Id).Single().Id;
        Call(buyerSession, buyerPeer, new AuctionBuyOfferRequest { bqs = market.Id, bn2 = 2, bn3 = id });
        Call(buyerSession, buyerPeer, new AuctionBuyOfferRequest { bqs = market.Id, bn2 = 1, bn3 = id });
        world.Market.Batch(b => b.AddSale(new MarketSale("ELSEWHERE", "T4_ORE", 1, 7, Pricing.A7m, DateTime.UtcNow))); // another city's market
        var stats = Call(buyerSession, buyerPeer, new AuctionGetItemAverageStatsRequest { bqs = market.Id, bor = (short)oreType.Index, bos = 1, bot = 0 });
        var week = Call(buyerSession, buyerPeer, new AuctionGetItemAverageStatsRequest { bqs = market.Id, bor = (short)oreType.Index, bos = 2, bot = 1 });
        var average = Call(buyerSession, buyerPeer, new AuctionGetItemAverageValueRequest { bqs = market.Id, bou = (short)oreType.Index, bov = 1, bow = 2 });
        var now = DateTime.UtcNow.Ticks;
        Check(stats.Parameters[0] is long[] and [3] && stats.Parameters[1] is long[] and [300 * Pricing.A7m] && stats.Parameters[2] is long[] and [var hour]
              && now - hour < TimeSpan.TicksPerHour && hour % TimeSpan.TicksPerHour == 0 && L(average.Parameters, 0) == 100 * Pricing.A7m
              && (!week.Parameters.TryGetValue(0, out var none) || none is long[] { Length: 0 }),
              "24 h chart: one bucket (this hour) of 3 ore for 300 silver; the 4-week average 100; quality 2 has no sales; the other city's 7 are not counted");
    }

    private static void GatheringExtras()
    {
        var wood = Client.Clusters!.Harvestables.Find("WOOD")!.Tiers[4];
        var tables = Client.Clusters.Loot;
        var rolls = Enumerable.Range(0, 50).SelectMany(i => tables.RollMob(wood.LootOf(0)!, new Random(i), 10000)).ToList();
        Check(wood.Loot?.Element("LootListReference")?.Attribute("name")?.Value == "T4_WOOD_SKILLBOOK" && wood.LootOf(1) is not null
              && rolls.Count > 0 && rolls.All(r => r.Item == "T4_SKILLBOOK_GATHER_WOOD"),
              "WOOD T4 <Loot>: T4_WOOD_SKILLBOOK (chance 0.002, rare states too); rolled (×10000) it gives T4_SKILLBOOK_GATHER_WOOD only");

        using var town = new Town();
        var world = town.World;
        var (session, _, me) = town.Join("Angler", 100, new InventoryItem("T1_FISHINGBAIT", 2));
        var bait = me.Inventory.Bag[0]!;
        lock (world.Sync) session.OnRequest(Request(new UseConsumableFromInventoryRequest { ah5 = bait.Id, ah6 = 1 }, 3), encrypted: false);
        town.Run(1);
        double speed;
        int charges;
        lock (world.Sync)
        {
            speed = me.Effects.Sum("fishingspeed", world.GameNow);
            charges = me.Effects.Spells.FirstOrDefault(a => a.Spell.Category == Fishing.BaitCharges)?.Stacks ?? 0;
        }
        var zone = world.Fishing.Freshwater with { BiteMin = 6, BiteMax = 6 };
        var rod = Catalog.Find("T3_2H_TOOL_FISHINGROD")!;
        Check(bait.Count == 1 && speed == 0.5 && charges == 10
              && Fishing.BiteDelay(zone, rod, new Random(1)).TotalSeconds / Fishing.BiteDelay(zone, rod, new Random(1), speed).TotalSeconds is > 1.47 and < 1.48,
              $"T1_FISHINGBAIT used: FISHINGBAIT_LEVEL1_USE fishingspeed 0.5 for 600 s, FISHINGBAIT_LEVEL1_CHARGE with 10 charges; the bite (1 + 0.05 + 0.5) / 1.05 faster (got {speed}, {charges})");
        lock (world.Sync)
        {
            for (var i = 0; i < 9; i++) Fishing.ConsumeBait(me);
            charges = me.Effects.Spells.FirstOrDefault(a => a.Spell.Category == Fishing.BaitCharges)?.Stacks ?? 0;
            speed = me.Effects.Sum("fishingspeed", world.GameNow);
            Fishing.ConsumeBait(me);
            Check(charges == 1 && speed == 0.5 && me.Effects.Sum("fishingspeed", world.GameNow) == 0 && me.Effects.Spells.All(a => !a.Spell.Category.StartsWith("fishingbait")),
                  "9 strikes leave 1 charge and the buff; the 10th takes both");
        }
    }

    private static void MarketBulkCollection()
    {
        using var town = new Town();
        var world = town.World;
        var (session, peer, player) = town.Join("Collector", 100);
        var market = town.Station("MARKETPLACE");

        // Finished entries are the durable output of purchases/expired orders; exercise collection through the handler.
        void Pending(Player owner, params (string Item, int Amount)[] items)
        {
            lock (world.Sync) world.Market!.Batch(batch =>
            {
                foreach (var (item, amount) in items)
                    batch.AddFinished(new MarketFinished(0, owner.Character.Id, 0, MarketOrder.Offer, "0000", item, 1, amount,
                        Pricing.A7m, 0, DateTime.UtcNow, DateTime.UtcNow.AddDays(1), Guid.NewGuid(), new InventoryItem(item, amount)));
            });
        }

        OperationResponse Collect(WorldSession target, FakePeer targetPeer)
        {
            var mark = targetPeer.Responses.Count;
            lock (world.Sync) target.OnRequest(Request(new AuctionFetchFinishedAuctionsRequest { bqs = market.Id }, 3), encrypted: false);
            var response = targetPeer.Responses.Skip(mark).Single();
            var bytes = new ByteWriter();
            Protocol18.WriteOperationResponse(bytes, response);
            return Protocol18.ReadOperationResponse(new ByteReader(bytes.ToArray()));
        }

        static object? Status(OperationResponse response) => response.Parameters.GetValueOrDefault((byte)0);
        static int Count(Player owner, string item) => owner.Inventory.Bag.Items.Where(i => i.Template.Name == item).Sum(i => i.Count);
        Pending(player, ("T4_ORE", 2), ("T4_WOOD", 3), ("T4_ROCK", 4), ("T4_HIDE", 5));
        var result = Collect(session, peer);
        Check(result.ReturnCode == 0 && Status(result) is byte and 4,
              $"bulk collection success is parameter 0 byte 4 after wire serialization, not transport ReturnCode alone (got {result.Parameters.GetValueOrDefault((byte)0)})");
        Check(Count(player, "T4_ORE") == 2 && Count(player, "T4_WOOD") == 3 && Count(player, "T4_ROCK") == 4 && Count(player, "T4_HIDE") == 5
              && world.Market!.FinishedOf(player.Character.Id).Count == 0, "all four pending item types delivered once and claims consumed");
        Check(Status(Collect(session, peer)) is byte and 4 && player.Inventory.Bag.Count == 4 && Count(player, "T4_ORE") == 2,
              "repeated/empty collection succeeds without duplicating inventory");
        lock (world.Sync) peer.Disconnect();
        world.Saves.Flush();
        var (againSession, againPeer, again) = town.Rejoin(player.Character.Id);
        Check(Count(again, "T4_ORE") == 2 && Count(again, "T4_WOOD") == 3 && Count(again, "T4_ROCK") == 4 && Count(again, "T4_HIDE") == 5
              && Status(Collect(againSession, againPeer)) is byte and 4, "collected inventory and empty finished list survive relog without duplicates");

        // One free slot: ore fits, wood cannot. Successful claims remain collected and failed ones remain retryable.
        var (tightSession, tightPeer, tight) = town.Join("TightBag", 0,
            Enumerable.Range(0, Inventory.BagSlots - 1).Select(_ => new InventoryItem("T4_MAIN_SWORD")).ToArray());
        Pending(tight, ("T4_ORE", 2), ("T4_WOOD", 3), ("T4_ORE", 1));
        Check(Status(Collect(tightSession, tightPeer)) is null or (byte and 0) && Count(tight, "T4_ORE") == 3 && Count(tight, "T4_WOOD") == 0
              && world.Market!.FinishedOf(tight.Character.Id) is [{ ItemName: "T4_WOOD", Amount: 3 }],
              "partial collection reports failure, still attempts the later fitting ore and retains the wood claim");
        Check(Status(Collect(tightSession, tightPeer)) is null or (byte and 0) && Count(tight, "T4_ORE") == 3
              && world.Market!.FinishedOf(tight.Character.Id).Count == 1, "full inventory retry reports failure without consuming or duplicating claims");
        var sword = tight.Inventory.Bag.Items.First(i => i.Template.Name == "T4_MAIN_SWORD");
        lock (world.Sync) tightSession.OnRequest(Request(new InventoryDestroyItemRequest { ri = sword.Id, rj = 1 }, null), encrypted: false);
        Check(Status(Collect(tightSession, tightPeer)) is byte and 4 && Count(tight, "T4_ORE") == 3 && Count(tight, "T4_WOOD") == 3
              && world.Market!.FinishedOf(tight.Character.Id).Count == 0, "after freeing a slot only the remaining claim is delivered");
        lock (world.Sync) tightPeer.Disconnect();
        world.Saves.Flush();
        var (lastSession, lastPeer, last) = town.Rejoin(tight.Character.Id);
        Check(Count(last, "T4_ORE") == 3 && Count(last, "T4_WOOD") == 3 && Status(Collect(lastSession, lastPeer)) is byte and 4,
              "partial collection, successful retry and consumed claims persist over relog");
    }
}
