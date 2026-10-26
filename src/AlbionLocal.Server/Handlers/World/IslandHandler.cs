using System.Globalization;
using AlbionLocal.Photon;
using AlbionLocal.Server.Entities;
using AlbionLocal.Server.Protocol;

namespace AlbionLocal.Server.Network;

/// <summary>
/// The island merchant's requests (ops-audit SPEC §3.1; stageH islands SPEC phase 1: the list, phase 2: buying, upgrading, the showroom, the
/// flags; journeys are BuyJourney's) and the farming on an island (phase 3: planting, watering, harvesting, destroying a crop). The rules
/// are <see cref="Systems.Islands"/>'; here the inventory, the timers and the answers.
/// </summary>
public sealed partial class WorldSession
{
    /// <summary>
    /// GetIslandInfos (op 218, request without fields, response afw; stageH islands SPEC §1.2): asked when the island merchant's or the
    /// island travel point's window opens and again after a journey. The player's islands (<see cref="Systems.Islands.Infos"/>); none =
    /// the empty answer the stub gave before (four of them on 2026-10-15, no client error).
    /// </summary>
    [Handles(OperationCodes.GetIslandInfos, Status = SessionStatus.InWorld)]
    private AlbionResponse? GetIslandInfos(GetIslandInfosRequest req) => Player is { } p ? world.Islands.Infos(p) : null;

    /// <summary>The RC of a refused island request (BuyMyIsland, UpgradeMyIsland, the farming ops): the client's generic error (SPEC Q2: the real codes are not captured).</summary>
    public const short IslandRefused = 1;

    /// <summary>
    /// BuyMyIsland (op 219, p0 {0 the NewIslandAccessPoint, 1 "&lt;its id&gt;@&lt;city&gt;", 2 the price a7m}; client c32.j_20 0x180A77D50, SPEC §2.2):
    /// a new level-0 island of the merchant's city (<see cref="Systems.Islands.Buy"/>). UpdateMoney first, then ad8 {0 the island, 1 the access
    /// point, 2 level byte 0}; the client adds the island to its lists from it (ckt.a.b 0x180B2B190) and shows "Visit". Refused: RC 1.
    /// </summary>
    [Handles(OperationCodes.BuyMyIsland, Status = SessionStatus.InWorld)]
    private AlbionResponse? BuyMyIsland(BuyMyIslandRequest req)
    {
        if (Player is not { } p) return null;
        var why = "";
        if (p.Map?.Objects.GetValueOrDefault(req.dk) is not IslandAccessPoint ap) why = $"object {req.dk} is no island merchant";
        else if (world.Islands.Buy(p, ap, req.dl, req.dm, out why) is { } island)
            return new BuyMyIslandResponse { anf = island.Id, ang = island.AccessPoint, anh = (byte)island.Level, Extra = { [2] = (byte)island.Level } }; // [2] sent as a byte also at 0
        Log.Info(Name, $"'{p.Character.Name}' island purchase refused: {why}");
        return new GenericResponse { ReturnCode = IslandRefused };
    }

    /// <summary>
    /// UpgradeMyIsland (op 221, acb {0 the island, 1 the price a7m}; ckt.af 0x180B3B0C0, SPEC §2.3): the next level (<see cref="Systems.Islands.Upgrade"/>);
    /// the bare RC 0 (no response class; the client raises the level and starts its 130 s itself, ckt.b.b), refused RC 1.
    /// </summary>
    [Handles(OperationCodes.UpgradeMyIsland, Status = SessionStatus.InWorld)]
    private AlbionResponse? UpgradeMyIsland(UpgradeMyIslandRequest req)
    {
        if (Player is not { } p) return null;
        if (world.Islands.Upgrade(p, req.ah3, req.ah4, out var why)) return new GenericResponse();
        Log.Info(Name, $"'{p.Character.Name}' island upgrade refused: {why}");
        return new GenericResponse { ReturnCode = IslandRefused };
    }

    /// <summary>ChangePlayerIslandData (op 389, qj {0 island, 1 favourite, 2 hidden}; SPEC §2.5): stored, RC 0 (GetIslandInfos keys 21/22); not the player's: RC 1.</summary>
    [Handles(OperationCodes.ChangePlayerIslandData, Status = SessionStatus.InWorld)]
    private AlbionResponse? ChangePlayerIslandData(ChangePlayerIslandDataRequest req) =>
        Player is not { } p ? null : new GenericResponse { ReturnCode = world.Islands.SetFlags(p, req.e0, req.e1, req.e2) ? (short)0 : IslandRefused };

    /// <summary>
    /// TravelIslandShowroom (op 533, ab1: 0 long), from IslandMerchantGui.OnClickShowroom 0x18104a3a0 with an island merchant
    /// entry selected. No response class, but the callback (IslandMerchantGui.OnTravelShowroomResponse 0x18104b770 → ggd.at
    /// 0x181cd77c0) waits for the return code: 0 = travel, 21320 / 21321 a message box, any other = refused without a text.
    /// SPEC §2.4: the merchant in view, its city's showroom (world.xml ISLAND-PLAYER-&lt;BIOME&gt;-0001e-SHOWROOM; none for Caerleon and the
    /// Mists) resolved (the merchant's GetIslandInfos asks for it) → RC 0, then the push ChangeCluster {0: the showroom} and the arrival
    /// at its travel point + spawnpos, as a journey. Refused: the bare {253, 255} with RC 1 (nothing misleading shows). The way back is
    /// BuyJourney from the showroom's travel point to the city (JourneyHandler).
    /// </summary>
    [Handles(OperationCodes.TravelIslandShowroom, Status = SessionStatus.InWorld)]
    private AlbionResponse? TravelIslandShowroom(TravelIslandShowroomRequest req)
    {
        if (Player is not { } p) return null;
        string why;
        if (_changing is not null) why = "a cluster change is under way";
        else if (!world.Islands.Enabled) why = "islands are off";
        else if (p.Map is not { } map || map.Objects.GetValueOrDefault(req.aha) is not IslandAccessPoint ap || !p.Visibility.Knows(ap)) why = $"object {req.aha} is no island merchant in view";
        else if (world.Islands.ShowroomOf(map.Cluster) is not { } showroom) why = $"{map.Cluster} has no island showroom";
        else if (world.CachedCluster(showroom) is not { } target)
        {
            world.PrefetchCluster(showroom);
            why = $"{showroom} is not resolved yet (asked for)";
        }
        else if (Systems.Islands.ArrivalOf(target) is not { } at) why = $"{showroom} has no travel point";
        else
        {
            Log.Info(Name, $"'{p.Character.Name}' travels to the island showroom {showroom}");
            SendResponse(OperationCodes.TravelIslandShowroom, new GenericResponse(), RequestId, RequestEncrypted);
            Teleport(new Arrival(showroom, at, p.Facing), $"showroom:{map.Cluster}");
            return null;
        }
        Log.Info(Name, $"'{p.Character.Name}' showroom trip refused: {why}");
        return new GenericResponse { ReturnCode = NoShowroom };
    }

    /// <summary>
    /// The RC of a refused TravelIslandShowroom: refused, no message (not one of the two the client has a text for). Not the SPEC's 8040:
    /// that is BuyJourney's table (ggd.as); this callback (ggd.at) knows only 21320 / 21321, so 8040 would show nothing either.
    /// </summary>
    public const short NoShowroom = 1;

    // ---- farming (SPEC phase 3) ----

    /// <summary>
    /// PlaceableObjectPlace (op 69, y7 {0 GameTimeStamp, 1 the item stack, 2 the item's building type, 3 the position, 4 rotation}; SPEC §3.2,
    /// cap 103829 ×6, no response). A seed into a farm building's slot of the own island (<see cref="Systems.Islands.CheckPlant"/>): the player
    /// dismounts (official: the dismount burst +0.13 s), and after FarmableObjects placetimeseconds (1 s) one seed leaves the stack (NewSimpleItem
    /// with the new count, InventoryDeleteItem for the last), then the crop's NewBuilding and FarmableObjectInfo. Refused: nothing but the stack
    /// again (its count unchanged). Furniture is not placed yet (phase 5): silent as before.
    /// </summary>
    [Handles(OperationCodes.PlaceableObjectPlace, Status = SessionStatus.InWorld)]
    private AlbionResponse? PlaceableObjectPlace(PlaceableObjectPlaceRequest req)
    {
        if (Player is not { } p || p.Map is not { } map) return null;
        // Labourers SPEC §2.3, §1.3: a labourer (the house menu's hire: no item; or a labourer contract) and furniture have their own way.
        if (world.Islands.LabourerType(req.x8) is not null) return PlaceLabourer(p, map, req);
        if (p.Inventory.Bag.Find(req.x7) is not { } stack)
        {
            Log.Info(Name, $"'{p.Character.Name}' places object {req.x7}: not in the inventory");
            return null;
        }
        if (Systems.Hideouts.IsKit(stack.Template)) return PlaceHideout(p, map, stack, req); // stageP: the hideout kit (Handlers/World/HideoutHandler)
        if (stack.Template.IsFurniture) return PlaceFurniture(p, map, stack, req);
        var (building, slot, why) = world.Islands.CheckPlant(p, stack.Template, req.x8, req.x9);
        if (building is null)
        {
            Log.Info(Name, $"'{p.Character.Name}' places {stack.Template.Name}: refused, {why}");
            SendCount(stack);
            return null;
        }
        Systems.Mounts.BeforeAction(p, "plants");
        map.Timers.At(world.Clock.Now + Systems.Islands.PlaceTime, () =>
        {
            if (Player != p || p.Map != map || building.Map != map) return;
            var seed = stack.Template;
            var at = p.Inventory.Bag.IndexOf(stack);
            if (at < 0 || stack.Count <= 0)
            {
                Log.Info(Name, $"'{p.Character.Name}' planting {seed.Name}: the stack is gone");
                return;
            }
            if (map.Objects.Values.OfType<FarmableObject>().Any(o => System.Numerics.Vector2.Distance(o.Position, slot) < 0.5f))
            {
                SendCount(stack);
                return;
            }
            if (--stack.Count > 0) SendCount(stack);
            else
            {
                p.Inventory.Bag.Take(at);
                SendEvent(new InventoryDeleteItemEvent { ckr = stack.Id });
            }
            world.Islands.Plant(map, building, seed, slot);
            world.Saves.SaveNow(p.Character); // the crop is stored at once: so is the seed it took
        });
        return null;
    }

    /// <summary>
    /// BoostFarmable (op 291, pn {0 GameTimeStamp, 1 a sequence byte, 2 1, 3 the crop}; SPEC §3.4, cap 103829 ×6, no response): the focus
    /// watering of an own crop (<see cref="Systems.Islands.CheckBoost"/>). The player dismounts; after activefarmactiondurationseconds (1 s)
    /// CraftingFocusUpdate {0 player, 1 now, 2 −cost, 3 the focus left}, the crop's FarmableObjectInfo (13 now, 14 its waterings) and the
    /// BoostFarmable event {0 player, 1 the request's byte, 2 3 (finished), 3 the crop, 4 its building type, 6 the request's stamp + 1 s}.
    /// Refused: nothing.
    /// </summary>
    [Handles(OperationCodes.BoostFarmable, Status = SessionStatus.InWorld)]
    private AlbionResponse? BoostFarmable(BoostFarmableRequest req)
    {
        if (Player is not { } p || p.Map is not { } map) return null;
        var (crop, _, why) = world.Islands.CheckBoost(p, req.ct);
        if (crop is null)
        {
            Log.Info(Name, $"'{p.Character.Name}' watering of object {req.ct} refused: {why}");
            return null;
        }
        Systems.Mounts.BeforeAction(p, "waters");
        var duration = TimeSpan.FromSeconds(crop.Seed.Farm?.ActionSeconds ?? crop.Seed.Animal!.ActionSeconds);
        map.Timers.At(world.Clock.Now + duration, () =>
        {
            if (Player != p || p.Map != map || crop.Map != map) return;
            var (again, cost, late) = world.Islands.CheckBoost(p, crop.Id);
            if (again is null)
            {
                Log.Info(Name, $"'{p.Character.Name}' watering of {crop}: {late}");
                return;
            }
            var now = world.GameNow;
            var (used, _) = Systems.Focus.Spend(p, cost, now);
            Systems.Focus.Send(p, now, -used);
            world.Islands.Watered(crop, now);
            world.Islands.SendInfo(crop);
            SendEvent(new BoostFarmableEvent
            {
                ddn = p.Id,                      // [0]
                btz = req.cr,                    // [1] the request's sequence byte
                bt0 = 3,                         // [2] finished (ak6.a.d)
                bt1 = crop.Id,                   // [3]
                bt2 = crop.TypeId,               // [4] the crop's building type
                bt4 = req.wf + duration.Ticks,   // [6] the request's stamp + the action's duration (exact as captured)
            });
            Log.Info(Name, string.Create(CultureInfo.InvariantCulture, $"'{p.Character.Name}' watered {crop} for {cost:0} focus"));
        });
        return null;
    }

    /// <summary>
    /// FarmableHarvest (op 73, sr {0 the crop}; SPEC §3.5): a grown own crop (<see cref="Systems.Islands.Ripe"/>) gives its harvest
    /// (<see cref="Systems.Islands.HarvestOf"/>) into the inventory (NewSimpleItem / InventoryPutItem) and the farming fame (UpdateFame:
    /// fameearned and the farmharvestfame nodes, × Rates:Fame), then the response ss {0 the item names, 1 their counts} and the crop's Leave.
    /// Refused (not grown, not the player's, out of reach, the inventory full): RC 1.
    /// </summary>
    [Handles(OperationCodes.FarmableHarvest, Status = SessionStatus.InWorld)]
    private AlbionResponse? FarmableHarvest(FarmableHarvestRequest req)
    {
        if (Player is not { } p || p.Map is not { } map) return null;
        string why;
        if (map.Objects.GetValueOrDefault(req.k9) is not FarmableObject crop || crop.Seed.Farm is not { } farm) why = $"object {req.k9} is no crop";
        else if (crop.Island.Owner != p.Character.Id) why = $"{crop} is not the player's";
        else if (!world.Islands.InReach(p, crop)) why = $"{crop} is out of reach";
        else if (!world.Islands.Ripe(crop.Crop, farm, world.GameNow)) why = $"{crop} is not grown yet";
        else if (world.Islands.HarvestOf(crop, map.Random, world.Islands.YieldFactor(crop, Systems.Premium.CropYieldFactor(p))) is not { } harvest) why = "loot.xml is not read";
        else if (!Fits(p.Inventory.Bag, harvest.Items.Select(i => (i.Item, i.Count)))) why = "the harvest does not fit the inventory";
        else
        {
            Systems.Mounts.BeforeAction(p, "harvests");
            foreach (var (item, count) in harvest.Items) GiveItem(p, item, count);
            var fame = Game.Rates.ScaleFame(harvest.Fame, world.Rates.Fame);
            if (fame > 0) Systems.Destiny.Award(p, fame, Systems.Destiny.Farm(crop.Seed), source: crop.Seed);
            world.Islands.Remove(crop, $"harvested by '{p.Character.Name}': {string.Join(", ", harvest.Items.Select(i => $"{i.Count} x {i.Item.Name}"))}");
            world.Saves.SaveNow(p.Character); // the crop's row went at once: so do the items it gave
            return new FarmableHarvestResponse { la = [.. harvest.Items.Select(i => i.Item.Name)], lb = [.. harvest.Items.Select(i => (short)i.Count)] };
        }
        Log.Info(Name, $"'{p.Character.Name}' harvest of object {req.k9} refused: {why}");
        return new GenericResponse { ReturnCode = IslandRefused };
    }

    /// <summary>FarmableDestroy (op 75, sk {0 the crop}; SPEC §3.5): an own crop in reach goes (Leave, out of the store), no loot; RC 0, else RC 1.</summary>
    [Handles(OperationCodes.FarmableDestroy, Status = SessionStatus.InWorld)]
    private AlbionResponse? FarmableDestroy(FarmableDestroyRequest req)
    {
        if (Player is not { } p) return null;
        if (p.Map?.Objects.GetValueOrDefault(req.lc) is not FarmableObject crop || crop.Island.Owner != p.Character.Id || !world.Islands.InReach(p, crop))
        {
            Log.Info(Name, $"'{p.Character.Name}' destroy of object {req.lc} refused: no crop of the player's in reach");
            return new GenericResponse { ReturnCode = IslandRefused };
        }
        world.Islands.Remove(crop, $"destroyed by '{p.Character.Name}'");
        return new GenericResponse();
    }

    // ---- animals (analysis/work/stageN/mounts/SPEC.md; stageH islands SPEC phase 4) ----

    /// <summary>An own animal of the player's map within reach, or the refusal.</summary>
    private (FarmableObject? Animal, string Why) OwnAnimal(Player p, long id)
    {
        if (p.Map?.Objects.GetValueOrDefault(id) is not FarmableObject o || o.Seed.Animal is null) return (null, $"object {id} is no animal");
        if (o.Island.Owner != p.Character.Id) return (null, $"{o} is not the player's");
        if (!world.Islands.InReach(p, o)) return (null, $"{o} is out of reach");
        return (o, "");
    }

    /// <summary>
    /// FarmableFill (op 77, sl {0 the animal, 1 long[] item stacks, 2 short[] counts}): feeds an own animal in reach from the offered
    /// stacks (<see cref="Systems.Islands.Feed"/>: its acceptedfood category, up to nutritionmax). The used items leave their stacks
    /// (NewSimpleItem with the new count, InventoryDeleteItem for the last), the animal's FarmableObjectInfo follows, and the response
    /// sm {0 the item names, 1 the counts used}. Refused (no own animal in reach, nothing it eats, full): RC 1.
    /// </summary>
    [Handles(OperationCodes.FarmableFill, Status = SessionStatus.InWorld)]
    private AlbionResponse? FarmableFill(FarmableFillRequest req)
    {
        if (Player is not { } p) return null;
        var (animal, why) = OwnAnimal(p, req.ld);
        if (animal is not null)
        {
            var offered = new List<(Item, int)>();
            var ids = req.le ?? [];
            for (var i = 0; i < ids.Length; i++)
                if (p.Inventory.Bag.Find(ids[i]) is { } stack && offered.All(o => o.Item1 != stack))
                    offered.Add((stack, req.lf is { } counts && i < counts.Length ? Math.Max(0, (int)counts[i]) : stack.Count));
            var used = world.Islands.Feed(animal, offered);
            if (used.Count > 0)
            {
                Systems.Mounts.BeforeAction(p, "feeds");
                foreach (var (stack, n) in used)
                {
                    stack.Count -= n;
                    if (stack.Count > 0) SendCount(stack);
                    else
                    {
                        p.Inventory.Bag.Take(p.Inventory.Bag.IndexOf(stack));
                        SendEvent(new InventoryDeleteItemEvent { ckr = stack.Id });
                    }
                }
                world.Saves.SaveNow(p.Character); // the animal's row went at once: so does the food it took
                return new FarmableFillResponse { la = [.. used.Select(u => u.Stack.Template.Name)], lb = [.. used.Select(u => (short)u.Used)] };
            }
            why = $"{animal} eats none of the offered items or is full";
        }
        Log.Info(Name, $"'{p.Character.Name}' feeding of object {req.ld} refused: {why}");
        return new GenericResponse { ReturnCode = IslandRefused };
    }

    /// <summary>
    /// FarmableFinishGrownItem (op 74, sn {0 the animal, 1 a bool [meaning unknown, logged]}): a grown own baby in reach gives the grown animal
    /// and the baby back by its offspring chance (<see cref="Systems.Islands.GrownOf"/>) into the inventory, the &lt;grownitem fame&gt; as farming
    /// fame, then the response so {0 the item names, 1 their counts} and the animal's Leave. Refused (not grown, …, inventory full): RC 1.
    /// </summary>
    [Handles(OperationCodes.FarmableFinishGrownItem, Status = SessionStatus.InWorld)]
    private AlbionResponse? FarmableFinishGrownItem(FarmableFinishGrownItemRequest req)
    {
        if (Player is not { } p || p.Map is not { } map) return null;
        var (animal, why) = OwnAnimal(p, req.lg);
        if (animal is not null)
        {
            if (world.Islands.GrownOf(animal, map.Random) is not { } grown) why = $"{animal} is no grown baby";
            else if (!Fits(p.Inventory.Bag, grown.Items.Select(i => (i.Item, i.Count)))) why = "the grown animal does not fit the inventory";
            else
            {
                Systems.Mounts.BeforeAction(p, "collects");
                foreach (var (item, count) in grown.Items) GiveItem(p, item, count);
                var fame = Game.Rates.ScaleFame(grown.Fame, world.Rates.Fame);
                if (fame > 0) Systems.Destiny.Award(p, fame, Systems.Destiny.Farm(animal.Seed), source: animal.Seed);
                world.Islands.Remove(animal, $"collected grown by '{p.Character.Name}' (flag {req.lh}): {string.Join(", ", grown.Items.Select(i => $"{i.Count} x {i.Item.Name}"))}");
                world.Saves.SaveNow(p.Character);
                return new FarmableFinishGrownItemResponse { la = [.. grown.Items.Select(i => i.Item.Name)], lb = [.. grown.Items.Select(i => (short)i.Count)] };
            }
        }
        Log.Info(Name, $"'{p.Character.Name}' collecting of object {req.lg} refused: {why}");
        return new GenericResponse { ReturnCode = IslandRefused };
    }

    /// <summary>
    /// FarmableGetProduct (op 76, sp {0 the animal, 1 the product index, 2 "take all"}): a grown own animal in reach whose production is
    /// complete gives the product's lootlist (eggs, milk, …) × the island yield bonus and the owner's premium factor
    /// (<see cref="Systems.Islands.YieldFactor"/>) and its fame; its production starts again (FarmableObjectInfo), then the response sq {0 the
    /// item names, 1 their counts}. Key 2 is the client's per-item-type setting "FARMABLE_TAKE_ALL&lt;item type&gt;" (gg5.ay 0x180C5F130,
    /// default true; islands5 SPEC §2.3): with it every other ready animal of the same item in the same building and in reach gives its product
    /// too, as far as the inventory takes it. Refused (not ready, no such product, inventory full): RC 1.
    /// </summary>
    [Handles(OperationCodes.FarmableGetProduct, Status = SessionStatus.InWorld)]
    private AlbionResponse? FarmableGetProduct(FarmableGetProductRequest req)
    {
        if (Player is not { } p || p.Map is not { } map) return null;
        var (animal, why) = OwnAnimal(p, req.li);
        if (animal is not null)
        {
            var premium = Systems.Premium.CropYieldFactor(p);
            if (world.Islands.ProductOf(animal, req.lj, map.Random, world.Islands.YieldFactor(animal, premium)) is not { } product) why = $"{animal} has no product {req.lj} ready";
            else if (!Fits(p.Inventory.Bag, product.Items.Select(i => (i.Item, i.Count)))) why = "the product does not fit the inventory";
            else
            {
                var taken = new List<(FarmableObject Animal, List<(Data.ItemTemplate Item, int Count)> Items, long Fame)> { (animal, product.Items, product.Fame) };
                if (req.lk)
                    foreach (var other in map.Objects.Values.OfType<FarmableObject>().Where(o => o != animal && o.Seed == animal.Seed && o.Crop.Building == animal.Crop.Building
                                 && o.Island.Owner == p.Character.Id && world.Islands.InReach(p, o)).ToList())
                    {
                        if (world.Islands.ProductOf(other, req.lj, map.Random, world.Islands.YieldFactor(other, premium)) is not { } more) continue;
                        if (!Fits(p.Inventory.Bag, taken.SelectMany(t => t.Items).Concat(more.Items).Select(i => (i.Item, i.Count)))) break;
                        taken.Add((other, more.Items, more.Fame));
                    }
                Systems.Mounts.BeforeAction(p, "collects");
                foreach (var (item, count) in taken.SelectMany(t => t.Items)) GiveItem(p, item, count);
                foreach (var (from, _, rawFame) in taken)
                {
                    var fame = Game.Rates.ScaleFame(rawFame, world.Rates.Fame);
                    if (fame > 0) Systems.Destiny.Award(p, fame, Systems.Destiny.Farm(from.Seed), source: from.Seed);
                    world.Islands.ProductTaken(from);
                }
                var all = taken.SelectMany(t => t.Items).GroupBy(i => i.Item).Select(g => (Item: g.Key, Count: g.Sum(i => i.Count))).ToList();
                Log.Info(Name, $"'{p.Character.Name}' collected {string.Join(", ", all.Select(i => $"{i.Count} x {i.Item.Name}"))} from {taken.Count} x {animal.Seed.Name} (take all {req.lk})");
                world.Saves.SaveNow(p.Character);
                return new FarmableGetProductResponse { la = [.. all.Select(i => i.Item.Name)], lb = [.. all.Select(i => (short)Math.Min(short.MaxValue, i.Count))] };
            }
        }
        Log.Info(Name, $"'{p.Character.Name}' product of object {req.li} refused: {why}");
        return new GenericResponse { ReturnCode = IslandRefused };
    }

    // ---- building on the island and picking up (islands phase 5, analysis/work/stageN/islands5/SPEC.md) ----

    /// <summary>A refusal the player should read (the client's generic error says nothing): a system chat line.</summary>
    private void TellIsland(string text) => SendEvent(new SystemMessageEvent { d0p = text.StartsWith('@') ? " " + text : text, d0q = 0 });

    /// <summary>
    /// ConstructionSiteCreate (op 68, rb {0 building type, 1 float[2] position, 2 rotation}; build menu BuildingDetailsViewGui.OnClickBuild
    /// 0x1813811B0 → placement mode): a farm building on a free plot of the own island (<see cref="Systems.Islands.CheckConstruct"/>). The
    /// official construction site (NewBuilding key 15, ConstructionSiteInfo, InstallResource…) is skipped: the placecost silver (UpdateMoney)
    /// and the &lt;craftingrequirements&gt; leave the inventory at once and the finished building stands on the plot (NewBuilding,
    /// AccessStatus); the response aei {0 its object id}. Refused: RC 1 and a system line with the reason (what is missing).
    /// </summary>
    [Handles(OperationCodes.ConstructionSiteCreate, Status = SessionStatus.InWorld)]
    private AlbionResponse? ConstructionSiteCreate(ConstructionSiteCreateRequest req)
    {
        if (Player is not { } p || p.Map is not { } map) return null;
        var at = req.g4 is { Length: >= 2 } v ? new System.Numerics.Vector2(v[0], v[1]) : p.PositionAt(DateTime.UtcNow);
        var (template, plot, silver, cost, why) = world.Islands.CheckConstruct(p, req.g3, at);
        if (template is not null && world.Islands.Find(map.Cluster) is { } island)
        {
            if (world.Islands.Build(island, template.Name, plot, req.g5, out why) is { } built)
            {
                Systems.Wallet.TrySpend(p, silver, $"placing {template.Name}");
                foreach (var (item, count) in cost) TakeItems(p, item, count);
                world.Saves.SaveNow(p.Character); // the building's row went at once: so do the silver and the resources
                var id = map.Objects.Values.OfType<PlayerBuilding>().FirstOrDefault(b => b.Building.Id == built.Id)?.Id ?? 0;
                Log.Info(Name, string.Create(CultureInfo.InvariantCulture,
                    $"'{p.Character.Name}' built {template.Name} at ({plot.X:0.#}, {plot.Y:0.#}) for {silver / Game.Pricing.A7m} silver and {string.Join(", ", cost.Select(c => $"{c.Count} x {c.Item.Name}"))}"));
                return new ConstructionSiteCreateResponse { aoj = id };
            }
        }
        Log.Info(Name, $"'{p.Character.Name}' construction of building type {req.g3} refused: {why}");
        TellIsland(why);
        return new GenericResponse { ReturnCode = IslandRefused };
    }

    /// <summary>
    /// BuildingGiveup (op 64, pt {0 the building}; BuildingPropertyModule.OnButtonGiveup 0x181322290): an own island building goes (Leave, out of
    /// the store) with the crops in it; buildings.xml destructionreturnfactor 0: nothing comes back. Refused while an animal stands in it (pick
    /// it up first) and for any other object: RC 1 with a system line.
    /// </summary>
    [Handles(OperationCodes.BuildingGiveup, Status = SessionStatus.InWorld)]
    private AlbionResponse? BuildingGiveup(BuildingGiveupRequest req)
    {
        if (Player is not { } p) return null;
        var (building, why) = world.Islands.CheckGiveup(p, req.c4);
        if (building is null)
        {
            Log.Info(Name, $"'{p.Character.Name}' give-up of object {req.c4} refused: {why}");
            TellIsland(why);
            return new GenericResponse { ReturnCode = IslandRefused };
        }
        world.Islands.Demolish(building.Building);
        Log.Info(Name, $"'{p.Character.Name}' gave up the {building.Template.Name} of {building.Island}");
        return new GenericResponse();
    }

    /// <summary>
    /// PlaceableObjectPickup (op 71, y6 {0 the object}; FarmableGui.OnPickupPressed 0x18110FA40 → co0.af 0x180B8CC00 → c32.j 0x180A77A90, a
    /// callback on the RC): an own placed animal (items.xml pickupable) in reach goes back into the inventory as the item it was placed as
    /// (NewSimpleItem / InventoryPutItem), losing its growth and food as the client's question says, then its Leave; RC 0. Refused (a seed,
    /// not the player's, out of reach, the inventory full): RC 1.
    /// </summary>
    [Handles(OperationCodes.PlaceableObjectPickup, Status = SessionStatus.InWorld)]
    private AlbionResponse? PlaceableObjectPickup(PlaceableObjectPickupRequest req)
    {
        if (Player is not { } p) return null;
        if (p.Map?.Objects.GetValueOrDefault(req.x5) is FurnitureObject) return PickupFurniture(p, req.x5);
        var (o, why) = world.Islands.CheckPickup(p, req.x5);
        if (o is not null && !Fits(p.Inventory.Bag, [(o.Seed, 1)])) (o, why) = (null, "the inventory is full");
        if (o is null)
        {
            Log.Info(Name, $"'{p.Character.Name}' pick-up of object {req.x5} refused: {why}");
            return new GenericResponse { ReturnCode = IslandRefused };
        }
        GiveItem(p, o.Seed, 1);
        world.Islands.Remove(o, $"picked up by '{p.Character.Name}'");
        world.Saves.SaveNow(p.Character);
        return new GenericResponse();
    }
}
