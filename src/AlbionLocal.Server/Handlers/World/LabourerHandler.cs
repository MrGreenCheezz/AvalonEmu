using System.Globalization;
using System.Numerics;
using AlbionLocal.Photon;
using AlbionLocal.Server.Data;
using AlbionLocal.Server.Entities;
using AlbionLocal.Server.Protocol;

namespace AlbionLocal.Server.Network;

/// <summary>
/// Houses, furniture and labourers on the islands (analysis/work/stageN/labourers/SPEC.md): the requests, the inventory and the silver. The
/// rules are <see cref="Systems.Islands"/>' (Islands.Labourers.cs). A refusal is RC 1 (<see cref="IslandRefused"/>) with a system line saying why.
/// </summary>
public sealed partial class WorldSession
{
    /// <summary>
    /// PlaceableObjectPlace (69) of a labourer type (SPEC §2.3): the house menu's "Hire labourer" (no item, the hireprice paid: UpdateMoney) or a
    /// labourer contract stack (one used up). The labourer stands at the spot at once (NewBuilding, LaborerObjectInfo, LaborerObjectJobInfo,
    /// AccessStatus); the answer is ah6 {0 the request's stamp, 1 its object id}. Refused: RC 1, a system line, the contract stack again.
    /// </summary>
    private AlbionResponse? PlaceLabourer(Player p, Map map, PlaceableObjectPlaceRequest req)
    {
        var at = req.x9;
        var contract = req.x7 != 0 ? p.Inventory.Bag.Find(req.x7) : null;
        string why;
        if (req.x7 != 0 && contract is not { Template.IsLabourerContract: true }) why = $"object {req.x7} is no labourer contract in the inventory";
        else
        {
            var (house, type, silver, refused) = world.Islands.CheckHire(p, req.x8, contract?.Template, at);
            why = refused;
            if (house is not null && type is not null && world.Islands.Hire(map, house, type, at, req.ya) is { } hired)
            {
                Systems.Mounts.BeforeAction(p, "hires");
                if (contract is not null) TakeItems(p, contract.Template, 1);
                else Systems.Wallet.TrySpend(p, silver, $"hiring {type.Name}");
                world.Saves.SaveNow(p.Character); // the labourer's row went at once: so do the silver or the contract
                return new PlaceableObjectPlaceResponse { bgv = req.x6, bgw = hired.Id };
            }
        }
        Log.Info(Name, $"'{p.Character.Name}' hiring of building type {req.x8} refused: {why}");
        TellIsland(why);
        if (contract is not null) SendCount(contract);
        return new GenericResponse { ReturnCode = IslandRefused };
    }

    /// <summary>
    /// PlaceableObjectPlace (69) of a furnitureitem (SPEC §1.3): into an own house (furniturelimit); one leaves the stack, the furniture stands at
    /// once (NewBuilding), the house's labourers get their LaborerObjectInfo with the new happiness target; ah6 {0 stamp, 1 its object id}.
    /// Refused: RC 1, a system line, the stack again.
    /// </summary>
    private AlbionResponse? PlaceFurniture(Player p, Map map, Item stack, PlaceableObjectPlaceRequest req)
    {
        var (house, why) = world.Islands.CheckFurniture(p, stack.Template, req.x8, req.x9);
        if (house is not null && world.Islands.PlaceFurniture(map, house, stack.Template, req.x9, req.ya) is { } placed)
        {
            Systems.Mounts.BeforeAction(p, "furnishes");
            TakeItems(p, stack.Template, 1);
            world.Saves.SaveNow(p.Character);
            return new PlaceableObjectPlaceResponse { bgv = req.x6, bgw = placed.Id };
        }
        Log.Info(Name, $"'{p.Character.Name}' placing {stack.Template.Name} refused: {why}");
        TellIsland(why);
        SendCount(stack);
        return new GenericResponse { ReturnCode = IslandRefused };
    }

    /// <summary>PlaceableObjectPickup (71) of own furniture: back into the inventory, off the map; RC 0. Inventory full: RC 1.</summary>
    private AlbionResponse? PickupFurniture(Player p, long id)
    {
        var (o, why) = world.Islands.CheckFurniturePickup(p, id);
        if (o is not null && !Fits(p.Inventory.Bag, [(o.Item, 1)])) (o, why) = (null, "the inventory is full");
        if (o is null)
        {
            Log.Info(Name, $"'{p.Character.Name}' pick-up of furniture {id} refused: {why}");
            return new GenericResponse { ReturnCode = IslandRefused };
        }
        GiveItem(p, o.Item, 1);
        world.Islands.RemoveFurniture(o);
        world.Saves.SaveNow(p.Character);
        return new GenericResponse();
    }

    /// <summary>A refused labourer request: the log, the system line, RC 1.</summary>
    private GenericResponse LabourerRefused(Player p, string what, string why)
    {
        Log.Info(Name, $"'{p.Character.Name}' {what} refused: {why}");
        TellIsland(why);
        return new GenericResponse { ReturnCode = IslandRefused };
    }

    /// <summary>
    /// LaborerStartJob (257, w3 {0 the labourer, 1 the journal item}; cph.af 0x180BB7FB0 → c32.j): a settled, idle own labourer takes a _FULL
    /// journal it accepts at a yield of at least 0.5 (SPEC §4); the journal leaves the inventory, LaborerObjectJobInfo (57) shows the job. RC 0.
    /// </summary>
    [Handles(OperationCodes.LaborerStartJob, Status = SessionStatus.InWorld)]
    private AlbionResponse? LaborerStartJob(LaborerStartJobRequest req)
    {
        if (Player is not { } p) return null;
        var (o, why) = world.Islands.OwnLabourer(p, req.tb);
        if (o is null) return LabourerRefused(p, "job start", why);
        if (p.Inventory.Bag.Find(req.tc) is not { } journal) return LabourerRefused(p, "job start", $"object {req.tc} is not in the inventory");
        var (yield, refused) = world.Islands.CheckStartJob(o, journal.Template);
        if (refused.Length > 0) return LabourerRefused(p, "job start", refused);
        TakeItems(p, journal.Template, 1);
        world.Islands.StartJob(o, journal.Template, yield);
        world.Saves.SaveNow(p.Character);
        return new GenericResponse();
    }

    /// <summary>
    /// LaborerTakeJobLoot (258, w4 {0 the labourer}; cph.aq8 0x180BB8AB0 → cyb.u → c32.o): a done job's loot (SPEC §4: the journal's lootlist,
    /// baselootamount × the yield) into the inventory (NewSimpleItem / InventoryPutItem; silver: UpdateMoney), the labourer fame to the labourer,
    /// no job any more (LaborerObjectJobInfo). Not done, no job, the inventory too full: RC 1.
    /// </summary>
    [Handles(OperationCodes.LaborerTakeJobLoot, Status = SessionStatus.InWorld)]
    private AlbionResponse? LaborerTakeJobLoot(LaborerTakeJobLootRequest req)
    {
        if (Player is not { } p || p.Map is not { } map) return null;
        var (o, why) = world.Islands.OwnLabourer(p, req.td);
        if (o is null) return LabourerRefused(p, "loot", why);
        var (items, silver, fame, refused) = world.Islands.JobLoot(o, map.Random);
        if (refused.Length > 0) return LabourerRefused(p, "loot", refused);
        if (!Fits(p.Inventory.Bag, items)) return LabourerRefused(p, "loot", "the job's loot does not fit the inventory");
        foreach (var (item, count) in items) GiveItem(p, item, count);
        if (silver > 0) Systems.Wallet.Credit(p, silver, $"{o.Labourer.Name}'s job");
        world.Islands.JobTaken(o, fame);
        world.Saves.SaveNow(p.Character);
        Log.Info(Name, string.Create(CultureInfo.InvariantCulture,
            $"'{p.Character.Name}' took {o.Labourer}'s loot: {string.Join(", ", items.Select(i => $"{i.Count} x {i.Item.Name}"))}{(silver > 0 ? $", {silver / 10000} silver" : "")}; +{fame / 10000.0:0.#} labourer fame"));
        return new GenericResponse();
    }

    /// <summary>LaborerDismiss (259, w1 {0 the labourer}; "@LABOURER_QUESTION_DISMISS"): an own labourer leaves; a job under way is lost. RC 0.</summary>
    [Handles(OperationCodes.LaborerDismiss, Status = SessionStatus.InWorld)]
    private AlbionResponse? LaborerDismiss(LaborerDismissRequest req)
    {
        if (Player is not { } p) return null;
        var (o, why) = world.Islands.OwnLabourer(p, req.s7);
        if (o is null) return LabourerRefused(p, "dismissal", why);
        world.Islands.Dismiss(o);
        return new GenericResponse();
    }

    /// <summary>
    /// LaborerMove (260, w2 {0 the labourer, 1 float[2] the spot, 2 yaw}; ConstructionConfirmationGui.OnDoLaborerMove 0x18138EBE0 → cx9.u): to a
    /// spot in its own house (Leave, then it is announced there). Outside it: RC 1.
    /// </summary>
    [Handles(OperationCodes.LaborerMove, Status = SessionStatus.InWorld)]
    private AlbionResponse? LaborerMove(LaborerMoveRequest req)
    {
        if (Player is not { } p) return null;
        var (o, why) = world.Islands.OwnLabourer(p, req.s8);
        if (o is null) return LabourerRefused(p, "move", why);
        if (req.s9 is not { Length: >= 2 } v) return LabourerRefused(p, "move", "no spot");
        if (!world.Islands.MoveLabourer(o, new Vector2(v[0], v[1]), req.ta)) return LabourerRefused(p, "move", "the spot is not in the labourer's house");
        return new GenericResponse();
    }

    /// <summary>
    /// LaborerBuyItem (261, w0 {0 the labourer, 1 the item type, 2 the count, 3 the price a7m}; cph.af 0x180BB7EC0 → cx7.u 0x1809FA4F0): _EMPTY
    /// journals of the labourer's &lt;journalssold&gt; at their price (items.xml craftingrequirements silver × JournalSettings SellFactor, as the
    /// journal merchant) each; the silver goes (UpdateMoney), the journals come into the inventory. RC 0; refused RC 1.
    /// </summary>
    [Handles(OperationCodes.LaborerBuyItem, Status = SessionStatus.InWorld)]
    private AlbionResponse? LaborerBuyItem(LaborerBuyItemRequest req)
    {
        if (Player is not { } p) return null;
        var (o, why) = world.Islands.OwnLabourer(p, req.s3);
        if (o is null) return LabourerRefused(p, "journal purchase", why);
        if (world.Items[req.s4] is not { Journal.State: JournalState.Empty } journal || !o.Type.JournalsSold.Contains(journal.Name, StringComparer.OrdinalIgnoreCase))
            return LabourerRefused(p, "journal purchase", $"{o.Type.Name} sells no item {req.s4}");
        var count = Math.Clamp((int)req.s5, 1, 999);
        var price = (long)Math.Round(journal.Journal.Price * world.Journals.SellFactor) * count;
        if (!Fits(p.Inventory.Bag, [(journal, count)])) return LabourerRefused(p, "journal purchase", "the journals do not fit the inventory");
        if (!Systems.Wallet.TrySpend(p, price, $"{count} x {journal.Name} from {o.Labourer.Name}"))
            return LabourerRefused(p, "journal purchase", $"{count} x {journal.Name} cost {price / Game.Pricing.A7m} silver");
        GiveItem(p, journal, count);
        world.Saves.SaveNow(p.Character);
        return new GenericResponse();
    }

    /// <summary>
    /// LaborerUpgrade (262, w5 {0 the labourer}; cph.aq9 0x180BB8BA0 → c32.q, "Advance to Tier {0}"): an idle own labourer whose fame reached its
    /// fametoprogress becomes its upgradeableto (LaborerGotUpgraded 290, then announced again as the new type). RC 0; refused RC 1.
    /// </summary>
    [Handles(OperationCodes.LaborerUpgrade, Status = SessionStatus.InWorld)]
    private AlbionResponse? LaborerUpgrade(LaborerUpgradeRequest req)
    {
        if (Player is not { } p) return null;
        var (o, why) = world.Islands.OwnLabourer(p, req.te);
        if (o is null) return LabourerRefused(p, "advance", why);
        var (next, refused) = world.Islands.CheckUpgrade(o);
        if (next is null) return LabourerRefused(p, "advance", refused);
        world.Islands.Upgrade(o, next);
        return new GenericResponse();
    }

    /// <summary>
    /// BuildingChangeRenovationState (61, pq {0 the building, 1 cos.RenovationType}; RenovationStartView): Upgrade (2) of an own house to its
    /// upgradeableto (SPEC §1.2: the next tier's placecost and craftingrequirements taken at once, no construction site); the house is announced
    /// again as the new tier. Any other renovation, or a refusal: RC 1 with a system line.
    /// </summary>
    [Handles(OperationCodes.BuildingChangeRenovationState, Status = SessionStatus.InWorld)]
    private AlbionResponse? BuildingChangeRenovationState(BuildingChangeRenovationStateRequest req)
    {
        if (Player is not { } p) return null;
        if (req.cz != 2) return LabourerRefused(p, $"renovation {req.cz} of object {req.cy}", "only upgrading a house is done here (no repairs: island buildings do not wear)");
        var (house, next, silver, cost, why) = world.Islands.CheckHouseUpgrade(p, req.cy);
        if (house is null || next is null) return LabourerRefused(p, $"upgrade of object {req.cy}", why);
        Systems.Wallet.TrySpend(p, silver, $"upgrading to {next.Name}");
        foreach (var (item, count) in cost) TakeItems(p, item, count);
        world.Islands.UpgradeHouse(house, next);
        world.Saves.SaveNow(p.Character);
        return new GenericResponse();
    }
}
