using AlbionLocal.Photon;
using AlbionLocal.Server.Entities;
using AlbionLocal.Server.Protocol;

namespace AlbionLocal.Server.Network;

/// <summary>
/// A player's station seen from its owner and its users (analysis/work/stageR/buildings/SPEC.md): the cancel of a running station action, the
/// owner's fees and earnings, the nutrition and its feeding rewards. The rules are <see cref="Systems.OwnedBuildings"/>'; a refusal is RC 1
/// with a system line saying why (the client's own codes for these requests are not known: it shows its generic error).
/// </summary>
public sealed partial class WorldSession
{
    /// <summary>The RC of a refused owner request.</summary>
    public const short BuildingRefused = 1;

    private GenericResponse BuildingRefusal(Player p, string op, string why)
    {
        Log.Info(Name, $"'{p.Character.Name}' {op} refused: {why}");
        TellIsland(why);
        return new GenericResponse { ReturnCode = BuildingRefused };
    }

    /// <summary>
    /// ActionOnBuildingCancel (op 56, oj {0 the client's time}): the player stops its station action itself (the client allows it 0.3 s after
    /// the start, cuo.ah4 0x1809C5BD0). Before this handler the op had no response class, so the server ignored it and let the action run to its
    /// end: the craft was charged and finished although the client had stopped its bar. Now it is cancelled as a move cancels it (the refund of
    /// the crafts not done, NewBuilding without key 16, ActionOnBuildingCancel 65 to the actor and its observers) and answered with an empty
    /// RC 0: the client's op-56 handler (cvw.ai7 0x1809D37F0, case 0x38) first puts the local player back into its idle state and then reads the
    /// answer as an ac2, showing an error only when keys 1/2 match its pending action, which an empty answer never does. Nothing running: RC 0
    /// too (the client has nothing to wait for) [infer: the official answer is not captured].
    /// </summary>
    [Handles(OperationCodes.ActionOnBuildingCancel, Status = SessionStatus.InWorld)]
    private AlbionResponse? ActionOnBuildingCancel(ActionOnBuildingCancelRequest req)
    {
        if (Player is not { } player) return null;
        if (player.Action is { Over: false }) CancelAction(player, "cancelled by the client", tellClient: true);
        return new GenericResponse();
    }

    /// <summary>CraftBuildingChangeSettings (op 47, rf {0 building, 1 friends' fee, 2 users' fee}, a7m per 100 nutrition; cox.af 0x180BA7580).</summary>
    [Handles(OperationCodes.CraftBuildingChangeSettings, Status = SessionStatus.InWorld)]
    private AlbionResponse? CraftBuildingChangeSettings(CraftBuildingChangeSettingsRequest req)
    {
        if (Player is not { } p) return null;
        return world.OwnedBuildings.ChangeFees(p, req.hl, req.hm, req.hn) is { } why ? BuildingRefusal(p, "fee change", why) : new GenericResponse();
    }

    /// <summary>CraftBuildingTakeMoney (op 48, rg {0 building}; cox.aqu 0x180BA7A30): the fees collected into the owner's wallet (UpdateMoney).</summary>
    [Handles(OperationCodes.CraftBuildingTakeMoney, Status = SessionStatus.InWorld)]
    private AlbionResponse? CraftBuildingTakeMoney(CraftBuildingTakeMoneyRequest req)
    {
        if (Player is not { } p) return null;
        return world.OwnedBuildings.TakeMoney(p, req.ho) is { } why ? BuildingRefusal(p, "taking the fees", why) : new GenericResponse();
    }

    /// <summary>
    /// RepairBuildingChangeSettings (op 49, zy {0 building, 1, 2 the fees}) and RepairBuildingTakeMoney (op 50): the repair station's twins of
    /// 47/48 [infer: the same keys as rf/rg]. Players cannot put up a repair station on this server (the city plots take crafting stations),
    /// so every building these name has no owner and the request is refused.
    /// </summary>
    [Handles(OperationCodes.RepairBuildingChangeSettings, Status = SessionStatus.InWorld)]
    private AlbionResponse? RepairBuildingChangeSettings(RepairBuildingChangeSettingsRequest req)
    {
        if (Player is not { } p) return null;
        return world.OwnedBuildings.ChangeFees(p, req.zn, req.zo, req.zp) is { } why ? BuildingRefusal(p, "repair fee change", why) : new GenericResponse();
    }

    [Handles(OperationCodes.RepairBuildingTakeMoney, Status = SessionStatus.InWorld)]
    private AlbionResponse? RepairBuildingTakeMoney(RepairBuildingTakeMoneyRequest req)
    {
        if (Player is not { } p) return null;
        return world.OwnedBuildings.TakeMoney(p, req.zq) is { } why ? BuildingRefusal(p, "taking the repair fees", why) : new GenericResponse();
    }

    /// <summary>
    /// BuildingFillNutrition (op 60, ps {0 building, 1 the food stack, 2 the count}; BuildingFeedView.FillNutritionWith → cou.af 0x180BA0E80, which
    /// sends only a count up to the stack's): the food leaves the stack (NewSimpleItem with the new count, InventoryDeleteItem for the last), the
    /// building's nutrition rises (NewBuilding key 18 to those who see it), a feeder other than the owner gets the reward (UpdateMoney); RC 0.
    /// </summary>
    [Handles(OperationCodes.BuildingFillNutrition, Status = SessionStatus.InWorld)]
    private AlbionResponse? BuildingFillNutrition(BuildingFillNutritionRequest req)
    {
        if (Player is not { } p) return null;
        var (building, stack, count, nutrition, why) = world.OwnedBuildings.CheckFill(p, req.c1, req.c2, req.c3);
        if (building is null || stack is null) return BuildingRefusal(p, "feeding a building", why);
        if ((stack.Count -= count) > 0) SendCount(stack);
        else
        {
            p.Inventory.Bag.Take(p.Inventory.Bag.IndexOf(stack));
            SendEvent(new InventoryDeleteItemEvent { ckr = stack.Id });
        }
        var reward = world.OwnedBuildings.Fill(p, building, nutrition);
        p.SaveItems();
        world.Saves.SaveNow(p.Character);
        Log.Info(Name, $"'{p.Character.Name}' fed {count} x {stack.Template.Name} to {building.Template.Name}: +{nutrition / (double)Game.Pricing.A7m:0.##} nutrition"
            + (reward > 0 ? $", {reward / (double)Game.Pricing.A7m:0.####} silver reward" : ""));
        return new GenericResponse();
    }

    /// <summary>BuildingNutritionSilverStorageDeposit (op 65, pv {0 building, 1 a7m}; cou.aqu 0x180BA1FA0): the owner's silver into the reward pool.</summary>
    [Handles(OperationCodes.BuildingNutritionSilverStorageDeposit, Status = SessionStatus.InWorld)]
    private AlbionResponse? BuildingNutritionSilverStorageDeposit(BuildingNutritionSilverStorageDepositRequest req)
    {
        if (Player is not { } p) return null;
        return world.OwnedBuildings.Deposit(p, req.c7, req.c8) is { } why ? BuildingRefusal(p, "reward deposit", why) : new GenericResponse();
    }

    /// <summary>BuildingNutritionSilverStorageWithdraw (op 66, pw {0 building, 1 a7m}): from the reward pool back into the owner's wallet.</summary>
    [Handles(OperationCodes.BuildingNutritionSilverStorageWithdraw, Status = SessionStatus.InWorld)]
    private AlbionResponse? BuildingNutritionSilverStorageWithdraw(BuildingNutritionSilverStorageWithdrawRequest req)
    {
        if (Player is not { } p) return null;
        return world.OwnedBuildings.Withdraw(p, req.c9, req.da) is { } why ? BuildingRefusal(p, "reward withdrawal", why) : new GenericResponse();
    }

    /// <summary>BuildingNutritionSilverRewardSet (op 67, pu {0 building, 1 a7m per nutrition point}; cou.aqw 0x180BA22C0).</summary>
    [Handles(OperationCodes.BuildingNutritionSilverRewardSet, Status = SessionStatus.InWorld)]
    private AlbionResponse? BuildingNutritionSilverRewardSet(BuildingNutritionSilverRewardSetRequest req)
    {
        if (Player is not { } p) return null;
        return world.OwnedBuildings.SetReward(p, req.c5, req.c6) is { } why ? BuildingRefusal(p, "reward change", why) : new GenericResponse();
    }
}
