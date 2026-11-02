using System.Globalization;
using AlbionLocal.Photon;
using AlbionLocal.Server.Data;
using AlbionLocal.Server.Entities;
using AlbionLocal.Server.Protocol;

namespace AlbionLocal.Server.Network;

public sealed partial class WorldSession
{
    private ulong _goldCraftSequence;

    /// <summary>345319 gold recipes use the ordinary op55 ACT1. CraftItemView1588FD0 passes the confirmed gold
    /// total through cl3.aaw/B4D8A0 to senderA6C740/key4. Key9 is the final quantity, in AmountCrafted-sized groups.
    /// The normal building/list/range checks run before this route; no extra NPC offers are manufactured.</summary>
    private AlbionResponse? StartGoldCraft(Player player, Building building, ItemTemplate item, ActionOnBuildingStartRequest req)
    {
        // This native op is also sent fire-and-forget, without the RPC RequestId used by the shop callbacks.
        // ENet deduplicates its reliable delivery; each newly dispatched Buy gets its own connection-scoped receipt.
        var receipt = ShopRequest(OperationCodes.ActionOnBuildingStart)
            ?? $"native:{_shopConnection}:goldcraft:{(++_goldCraftSequence).ToString(CultureInfo.InvariantCulture)}";
        Log.Info(Name, $"'{player.Character.Name}' gold craft request item={item.Name}, recipe={req.v}, quantity(key9)={req.w}, quote(key4)={req.r} a7m gold");
        var result = world.Shop.CraftGold(player, item, req.v, req.w, req.r, receipt);
        if (!result.Purchase.Success)
            return StationRefused(player, req, result.Error == 0 ? StationGenericError : StationComponentError,
                (byte)result.Error, result.Purchase.Message);

        // Gold, resources, output and the receipt are already durable. Their native burst follows the same
        // instant-shop lifecycle as StartCraft; replay only finishes the request and does not grant again.
        var now = world.GameNow;
        var action = new BuildingAction(building, req.p, req.q, now, now);
        Systems.Mounts.BeforeAction(player, "crafts");
        SendEvent(building.NewBuilding(inUse: true));
        result.Publish();
        SendEvent(new CraftItemFinishedEvent
        {
            ddn = player.Id, b1d = building.Id, b1e = result.ItemIds.ToArray(), b1f = [], b1g = [],
        });
        SendEvent(new CraftBuildingInfoEvent { ddn = building.Id }.FillEmpty());
        SendEvent(building.NewBuilding());
        Finished(player, action);
        player.Inventory.SaveTo(player.Character, id => world.Spells?.Name(id));
        world.Saves.MarkChanged(player.Character);
        return null;
    }
}
