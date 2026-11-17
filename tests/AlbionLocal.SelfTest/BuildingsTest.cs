using System.Numerics;
using AlbionLocal.Photon.Serialization;
using AlbionLocal.Server;
using AlbionLocal.Server.Content;
using AlbionLocal.Server.Entities;
using AlbionLocal.Server.Game;
using AlbionLocal.Server.Network;
using AlbionLocal.Server.Protocol;
using static AlbionLocal.SelfTest.AlbionTests;

namespace AlbionLocal.SelfTest;

/// <summary>
/// A player's buildings (analysis/work/stageR/buildings/SPEC.md): the cancel of a station action (op 56), a T4 smelter on an own city plot with
/// its owner's fees, nutrition, feeding rewards and access preset, and a furniture chest on an island that keeps its items.
/// </summary>
public static class BuildingsTest
{
    public static IEnumerable<(string, Action)> All =>
    [
        ("buildings: ActionOnBuildingCancel (op 56) cancels a running craft batch: RC 0, 65, the silver and resources back, no 66 later", Cancel),
        ("buildings: an owned T4 smelter: fee per 100 nutrition (op 47) on top of the price, nutrition eaten, take money (48), NutritionLow, feed (60) with reward (65/67), closed preset, reload", OwnedStation),
        ("buildings: a T2 furniture chest in an island house opens (op 45: AttachItemContainer with the chest's Guid), keeps an item across a reload, refuses its pick-up while full", Chest),
    ];

    private static (World World, Func<Character, (WorldSession, FakePeer)> Join, Func<WorldSession, FakePeer, AlbionRequest, OperationResponse> Call,
        Action<double> Run, Func<World> Again) Thetford(string dir)
    {
        var store = Client.Clusters!;
        _ = store.Get("0000");
        var stations = StationSpawns.Load(Path.Combine(DataDir(), "..", "content", "spawns"));
        var characters = Repository(dir);
        var clock = new ManualClock();
        var anchor = DateTime.UtcNow.Ticks;
        World MakeWorld() => new(new ServerConfig { JoinByCharacterId = true }, characters, Client.Items, Client.GameData, Client.Spells)
        {
            Sync = new object(), Clusters = store, Clock = clock, GameTicks = () => anchor + clock.Now.Ticks, Stations = stations, MapSeed = 5, MobAi = false,
        };
        var world = MakeWorld();
        (WorldSession, FakePeer) Join(Character c)
        {
            Check(characters.Create(c), "character created");
            var peer = new FakePeer();
            var session = new WorldSession(peer, world);
            lock (world.Sync) session.OnRequest(Request(new JoinRequest { ss = c.Id }, null), encrypted: true);
            return (session, peer);
        }
        OperationResponse Call(WorldSession s, FakePeer peer, AlbionRequest req)
        {
            var before = peer.Responses.Count;
            lock (world.Sync) s.OnRequest(Request(req, 7), encrypted: false);
            return peer.Responses.Skip(before).Single();
        }
        void Run(double seconds)
        {
            clock.Advance(TimeSpan.FromSeconds(seconds));
            lock (world.Sync) world.Update();
        }
        return (world, Join, Call, Run, MakeWorld);
    }

    private static Character Hero(string prefix, Vector2 at, long silver, params (string Item, int Count)[] items)
    {
        var c = StarterKit.Default.NewCharacter(prefix + Guid.NewGuid().ToString("N")[..10]);
        (c.Cluster, c.PositionX, c.PositionY, c.Silver) = ("0000", at.X, at.Y, silver);
        foreach (var (item, count) in items) c.Inventory.Add(new InventoryItem(item, count));
        return c;
    }

    private static int Count(Player p, string name) => p.Inventory.Bag.Items.Where(i => i.Template.Name == name).Sum(i => i.Count);

    /// <summary>A batch of 5 T4 bars at Thetford's public smelter, cancelled by op 56 after a second: everything comes back, nothing is made.</summary>
    private static void Cancel()
    {
        var dir = NewDir();
        var (world, join, call, run, _) = Thetford(dir);
        var spot = world.Stations.For("0000").First(e => e.Building == "T8_SMELTER").Position;
        var bar = Client.Items.Find("T4_METALBAR")!;
        var recipe = bar.Recipes[0];
        var hero = Hero("C", spot, 100_000 * Pricing.A7m, [.. recipe.Resources.Select(r => (r.Name, r.Count * 5))]);
        var (s, peer) = join(hero);
        var me = s.Player!;
        var smelter = world.Maps.Find("0000")!.Objects.Values.OfType<Building>().First(b => b.Template.Name == "T8_SMELTER" && b.State is null);
        var before = recipe.Resources.Select(r => Count(me, r.Name)).ToList();
        var price = Pricing.CraftPrice(recipe, 5);
        var started = peer.Responses.Count;
        lock (world.Sync) s.OnRequest(Request(new ActionOnBuildingStartRequest { wf = world.GameNow, o = smelter.Id, p = 1, q = 1, r = price, x = bar.Name, w = 5 }, 7), encrypted: false);
        var running = me.Action is { Act: 1 } && peer.Responses.Count == started;
        var mark = peer.Events.Count;
        var answer = call(s, peer, new ActionOnBuildingCancelRequest { wf = world.GameNow });
        run(600);
        var events = peer.Events.Skip(mark).Select(e => (short)e.Parameters[ReservedKeys.EventCode]).ToList();
        Check(running && answer.ReturnCode == 0 && me.Action is null && events.Contains((short)EventCodes.ActionOnBuildingCancel)
              && !events.Contains((short)EventCodes.ActionOnBuildingFinished) && Count(me, bar.Name) == 0 && me.Character.Silver == 100_000 * Pricing.A7m
              && recipe.Resources.Select(r => Count(me, r.Name)).SequenceEqual(before),
              $"started {running}; op 56 RC {answer.ReturnCode}, action {me.Action}, events [{string.Join(",", events)}], bars {Count(me, bar.Name)}, silver {me.Character.Silver / Pricing.A7m}");
        var idle = call(s, peer, new ActionOnBuildingCancelRequest { wf = world.GameNow });
        Check(idle.ReturnCode == 0, $"op 56 with nothing running: RC {idle.ReturnCode}");
        world.Dispose();
    }

    /// <summary>The owner loop of a T4 smelter on A's plot, used by B (no guild: a "user" under the public preset).</summary>
    private static void OwnedStation()
    {
        var dir = NewDir();
        var (world, join, call, run, again) = Thetford(dir);
        var stations = world.Stations.For("0000").Select(s => s.Position).ToList();
        var spot = Client.Clusters!.Get("0000").RealEstates.First(r => r.Size == new Vector2(20, 20) && stations.All(s => Vector2.Distance(s, r.Position) > 30));
        var bar = Client.Items.Find("T4_METALBAR")!;
        var recipe = bar.Recipes[0];
        const long M = 1_000_000 * Pricing.A7m;
        var (sa, pa) = join(Hero("A", spot.Position, 10 * M, ("T3_MEAL_SOUP", 2), ("T1_CARROT", 3)));
        var (sb, pb) = join(Hero("B", spot.Position, 10 * M, [.. recipe.Resources.Select(r => (r.Name, r.Count * 4)), ("T1_CARROT", 5)]));
        var a = sa.Player!;
        var b = sb.Player!;
        var plot = world.Maps.Find("0000")!.Objects.Values.OfType<RealEstateObject>().Single(o => o.Guid == spot.Guid);
        Building? station = null;
        lock (world.Sync)
        {
            if (world.RealEstates.Claim(a, plot.Guid, out var why))
                station = world.RealEstates.Build(plot, Client.Clusters!.Buildings.Find("T4_SMELTER")!, plot.Position, 0);
            else Check(false, $"claim refused: {why}");
        }
        if (station?.State is not { } state) { Check(false, "no owned smelter"); world.Dispose(); return; }
        var storage = Server.Systems.OwnedBuildings.Storage(station.Template);
        var created = station.NewBuilding();
        Check(state.Nutrition == (long)Math.Round(0.11245 * 795000 * Pricing.A7m) && created.Nutrition == state.Nutrition && created.ct6 == false
              && created.ItemTypeId == Client.Items.Find("T3_MEAL_SOUP")!.Index && state.Preset == 2 && state.UserFee == 0,
              $"a new T4 smelter: nutrition {state.Nutrition / Pricing.A7m} (0.11245 x 795000), key 27 {created.ct6}, dish {created.ItemTypeId}, preset {state.Preset}");

        // The owner's fee: 100 silver per 100 nutrition for users; B's craft pays it on top of the recipe silver.
        var set = call(sa, pa, new CraftBuildingChangeSettingsRequest { hl = station.Id, hm = 50 * Pricing.A7m, hn = 100 * Pricing.A7m });
        var setByB = call(sb, pb, new CraftBuildingChangeSettingsRequest { hl = station.Id, hm = 0, hn = 0 });
        var perCraft = world.OwnedBuildings.NutritionPerCraft(bar);
        var fee = Server.Systems.OwnedBuildings.Fee(perCraft, 100 * Pricing.A7m) * 2;
        var price = Pricing.CraftPrice(recipe, 2) + fee;
        var wrong = call(sb, pb, new ActionOnBuildingStartRequest { wf = world.GameNow, o = station.Id, p = 1, q = 1, r = Pricing.CraftPrice(recipe, 2), x = bar.Name, w = 2 });
        var nutrition = state.Nutrition;
        var started = pb.Responses.Count;
        lock (world.Sync) sb.OnRequest(Request(new ActionOnBuildingStartRequest { wf = world.GameNow, o = station.Id, p = 1, q = 2, r = price, x = bar.Name, w = 2 }, 7), encrypted: false);
        var accepted = pb.Responses.Count == started && b.Action is not null;
        run(600);
        Check(set.ReturnCode == 0 && setByB.ReturnCode == 1 && state.FriendFee == 50 * Pricing.A7m && state.UserFee == 100 * Pricing.A7m && perCraft > 0 && fee > 0
              && wrong.ReturnCode == WorldSession.StationComponentError && L(wrong.Parameters, 3) == (byte)CraftError.PriceChanged
              && accepted && Count(b, bar.Name) == 2 && b.Character.Silver == 10 * M - price && state.Earned == fee && state.Nutrition == nutrition - 2 * perCraft,
              $"fees set RC {set.ReturnCode} (B {setByB.ReturnCode}); without the fee RC {wrong.ReturnCode}; with it accepted {accepted}: bars {Count(b, bar.Name)}, B paid "
              + $"{(10 * M - b.Character.Silver) / (double)Pricing.A7m} of {price / (double)Pricing.A7m}, earned {state.Earned / (double)Pricing.A7m}, nutrition -{(nutrition - state.Nutrition) / (double)Pricing.A7m} (2 x {perCraft / (double)Pricing.A7m})");

        // The owner takes the fees; an empty building refuses with NutritionLow; the owner feeds it (2 soups, the favourite dish: x2).
        var silverA = a.Character.Silver;
        var take = call(sa, pa, new CraftBuildingTakeMoneyRequest { ho = station.Id });
        state.Nutrition = perCraft / 2;
        var low = call(sb, pb, new ActionOnBuildingStartRequest { wf = world.GameNow, o = station.Id, p = 1, q = 3, r = price, x = bar.Name, w = 2 });
        var soup = a.Inventory.Bag.Items.Single(i => i.Template.Name == "T3_MEAL_SOUP");
        var perSoup = world.OwnedBuildings.NutritionOf(station, soup.Template);
        var fed = call(sa, pa, new BuildingFillNutritionRequest { c1 = station.Id, c2 = soup.Id, c3 = 2 });
        Check(take.ReturnCode == 0 && a.Character.Silver == silverA + fee && state.Earned == 0
              && low.ReturnCode == WorldSession.StationComponentError && L(low.Parameters, 3) == (byte)CraftError.NutritionLow
              && fed.ReturnCode == 0 && Count(a, "T3_MEAL_SOUP") == 0 && state.Nutrition == perCraft / 2 + 2 * perSoup && perSoup > 0,
              $"take RC {take.ReturnCode} (+{(a.Character.Silver - silverA) / (double)Pricing.A7m}); low RC {low.ReturnCode} error {L(low.Parameters, 3)}; fed RC {fed.ReturnCode}, "
              + $"nutrition {state.Nutrition / (double)Pricing.A7m} (2 x {perSoup / (double)Pricing.A7m})");

        // The feeding reward: A deposits 1000 silver and pays 1 silver per nutrition; B's 5 carrots (48 each) earn 240 of it.
        var deposit = call(sa, pa, new BuildingNutritionSilverStorageDepositRequest { c7 = station.Id, c8 = 1000 * Pricing.A7m });
        var reward = call(sa, pa, new BuildingNutritionSilverRewardSetRequest { c5 = station.Id, c6 = Pricing.A7m });
        var silverB = b.Character.Silver;
        var carrots = b.Inventory.Bag.Items.Single(i => i.Template.Name == "T1_CARROT");
        var feedB = call(sb, pb, new BuildingFillNutritionRequest { c1 = station.Id, c2 = carrots.Id, c3 = 5 });
        var withdraw = call(sa, pa, new BuildingNutritionSilverStorageWithdrawRequest { c9 = station.Id, da = 100 * Pricing.A7m });
        Check(deposit.ReturnCode == 0 && reward.ReturnCode == 0 && feedB.ReturnCode == 0 && b.Character.Silver == silverB + 240 * Pricing.A7m
              && withdraw.ReturnCode == 0 && state.RewardStorage == 660 * Pricing.A7m && station.NewBuilding().ct2 == state.RewardStorage && station.NewBuilding().ct3 == Pricing.A7m,
              $"deposit RC {deposit.ReturnCode}, reward RC {reward.ReturnCode}, B fed RC {feedB.ReturnCode} (+{(b.Character.Silver - silverB) / (double)Pricing.A7m}), withdraw RC {withdraw.ReturnCode}, pool {state.RewardStorage / (double)Pricing.A7m}");

        // Closed: B may not craft any more (InvalidRights), the AccessStatus says so.
        var closed = call(sa, pa, new AccessRightsChangeSettingsRequest { d = station.Id, g = 0 });
        var refused = call(sb, pb, new ActionOnBuildingStartRequest { wf = world.GameNow, o = station.Id, p = 1, q = 4, r = price, x = bar.Name, w = 1 });
        Check(closed.ReturnCode == 0 && state.Preset == 0 && station.AccessStatus().bq9 is ["owner", "noaccess", "noaccess"]
              && refused.ReturnCode == WorldSession.StationComponentError && L(refused.Parameters, 3) == (byte)CraftError.InvalidRights,
              $"closed RC {closed.ReturnCode}: preset {state.Preset}, B's craft RC {refused.ReturnCode} error {L(refused.Parameters, 3)}");

        // Saved: a new world over the same database has the fees, the pool and the preset.
        var w2 = again();
        var kept = w2.OwnedBuildings.For(state.Id, station.Template);
        Check(kept.UserFee == 100 * Pricing.A7m && kept.RewardStorage == 660 * Pricing.A7m && kept.Preset == 0 && kept.Nutrition == state.Nutrition,
              $"reloaded: user fee {kept.UserFee / Pricing.A7m}, pool {kept.RewardStorage / Pricing.A7m}, preset {kept.Preset}");
        w2.Dispose();

        // The plot given up: the station comes down and its pool goes to A's wallet.
        var silverBefore = a.Character.Silver;
        var gone = call(sa, pa, new GiveUpRealEstateRequest { o6 = plot.Guid });
        Check(gone.ReturnCode == 0 && a.Character.Silver == silverBefore + 660 * Pricing.A7m, $"give-up RC {gone.ReturnCode}: A got {(a.Character.Silver - silverBefore) / (double)Pricing.A7m} back");
        world.Dispose();
    }

    /// <summary>A T2 chest placed in the island house: opened, an item moved in, closed; reloaded with the item; its pick-up refused while full.</summary>
    private static void Chest()
    {
        var (world, island, s, peer, call, _) = IslandTest.OnIsland(("T1_WOOD", 30), ("T1_ROCK", 3), ("T2_STONEBLOCK", 180), ("T2_FURNITUREITEM_CHEST", 1), ("T4_ORE", 7));
        var me = s.Player!;
        var house = Client.Clusters!.Buildings.Find("T2_PLAYERHOUSE")!;
        var cell = me.Map!.Template!.BuildCells.OrderBy(c => c.X).ThenBy(c => c.Y).First();
        var built = call(new ConstructionSiteCreateRequest { g3 = house.Id, g4 = [cell.X + 5, cell.Y + 5], g5 = 0 }, 3);
        var chestItem = Client.Items.Find("T2_FURNITUREITEM_CHEST")!;
        var centre = cell + new Vector2(5, 5);
        var placed = call(new PlaceableObjectPlaceRequest
        {
            x7 = me.Inventory.Bag.Items.Single(i => i.Template == chestItem).Id, x8 = world.Islands.ItemBuildingId(chestItem), x9 = centre + new Vector2(3, 3),
        }, 4);
        var chest = me.Map.Objects.Values.OfType<FurnitureObject>().SingleOrDefault();
        if (chest is null) { Check(false, $"house RC {built.ReturnCode}, chest placed RC {placed.ReturnCode}: no chest"); world.Dispose(); return; }
        (me.Character.PositionX, me.Character.PositionY) = (chest.Position.X, chest.Position.Y);
        lock (world.Sync) me.Teleport(chest.Position, 0, DateTime.UtcNow);
        var mark = peer.Events.Count;
        call(new RegisterToObjectRequest { zg = chest.Id }, 5);
        var attach = peer.Events.Skip(mark).LastOrDefault(e => e.Parameters[ReservedKeys.EventCode] is short c && c == (short)EventCodes.AttachItemContainer);
        var ore = me.Inventory.Bag.Items.First(i => i.Template.Name == "T4_ORE" && i.Count == 7);
        var from = me.Inventory.Bag.IndexOf(ore);
        var oreBefore = Count(me, "T4_ORE");
        call(new InventoryMoveItemRequest { ru = (byte)from, rv = me.Inventory.Bag.Id, rx = 0, ry = chest.Row.Id, rw = ore.Id, rz = 7 }, 6);
        var moved = me.FindContainer(chest.Row.Id)?.Count;
        var refused = call(new PlaceableObjectPickupRequest { x5 = chest.Id }, 7);
        call(new UnRegisterFromObjectRequest { ahc = chest.Id }, 8);
        lock (world.Sync) world.Saves.SaveNow(me.Character);
        world.Saves.Flush();
        var size = attach?.Parameters.GetValueOrDefault((byte)5);
        var saved = world.Characters.Find(me.Character.Id)?.Vaults.GetValueOrDefault(WorldSession.ChestKey(chest.Row.Id));
        Check(built.ReturnCode == 0 && placed.ReturnCode == 0 && attach is not null && attach.Parameters[1] is byte[] { Length: 16 } g && new Guid(g) == chest.Row.Id
              && size is not null && Convert.ToInt32(size) == chestItem.Container!.Value.Capacity
              && moved == 1 && Count(me, "T4_ORE") == oreBefore - 7 && refused.ReturnCode != 0 && me.OpenVaults.Count == 0
              && saved?.Count(i => i is { Name: "T4_ORE", Count: 7 }) == 1,
              $"house RC {built.ReturnCode}, placed RC {placed.ReturnCode}, attach {(attach is null ? "none" : $"{attach.Parameters[1]} size {size}")}, in the chest {moved}, "
              + $"pick-up RC {refused.ReturnCode}, saved {saved?.Count(i => i is not null)} ({string.Join(",", saved?.Select(i => i is null ? "-" : $"{i.Name}x{i.Count}") ?? [])}), ore {Count(me, "T4_ORE")}, open {me.OpenVaults.Count}, guid {(attach?.Parameters[1] is byte[] gb ? string.Join("", gb.Select(x => x.ToString("x2"))) : "?")} row {chest.Row.Id:N}");
        world.Dispose();
    }

    private static long L(Dictionary<byte, object> p, byte key) => p.TryGetValue(key, out var v) ? Convert.ToInt64(v) : 0;

    private static void Check(bool cond, string what) => AlbionTests.Check(cond, what);
}
