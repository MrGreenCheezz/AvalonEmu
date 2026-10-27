using AlbionLocal.Server.Entities;
using AlbionLocal.Server.Persistence;

namespace AlbionLocal.Server.Systems;

/// <summary>Laboratory tutorial stock. Prices are local policy; no installed client price table defines these offers.</summary>
public static class TutorialMerchant
{
    public const long FirstId = 890_000_000;
    public const long LaboratoryMulePrice = 30_000, LaboratoryEquipmentPrice = 750_000;
    private static readonly string[] Stock = ["T2_MOUNT_MULE",
        "T2_HEAD_PLATE_SET1", "T2_ARMOR_PLATE_SET1", "T2_SHOES_PLATE_SET1",
        "T2_HEAD_LEATHER_SET1", "T2_ARMOR_LEATHER_SET1", "T2_SHOES_LEATHER_SET1",
        "T2_HEAD_CLOTH_SET1", "T2_ARMOR_CLOTH_SET1", "T2_SHOES_CLOTH_SET1", "T2_OFF_SHIELD", "T2_OFF_BOOK"];

    public static bool Owns(MarketOrder order) => order.Owner == Guid.Empty && order.Id > FirstId && order.Id < FirstId + 100_000;

    public static IEnumerable<MarketOrder> Orders(Player player, string location, string type)
    {
        if (type != MarketOrder.Offer || !player.World.Config.TutorialOpening || player.Character.Tutorial is not { Version: 1 }
            || player.Map is not { Cluster: "1011" } map
            || map.TutorialOwner is { } owner && owner != player.Character.Id || location != map.InstanceKey) yield break;
        foreach (var name in Stock)
        {
            if (player.World.Items.Find(name) is not { CanCreate: true } item) continue;
            var id = FirstId + item.Index;
            var today = DateTime.UtcNow.Date;
            yield return new MarketOrder(id, Guid.Empty, MarketOrder.Offer, location, name, 1, 1,
                name == "T2_MOUNT_MULE" ? LaboratoryMulePrice : LaboratoryEquipmentPrice, today, today.AddDays(30),
                new Guid((int)id, 0x5455, 0x544f, [0x52, 0x49, 0x41, 0x4c, 0, 0, 0, 1]), null);
        }
    }

    public static MarketOrder? Order(Player player, long id, string location) =>
        Orders(player, location, MarketOrder.Offer).FirstOrDefault(o => o.Id == id);
}
