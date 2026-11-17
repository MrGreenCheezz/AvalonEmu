using System.Globalization;
using AlbionLocal.Photon;
using AlbionLocal.Server.Data;
using AlbionLocal.Server.Entities;
using AlbionLocal.Server.Game;
using AlbionLocal.Server.Protocol;
using AlbionLocal.Server.Systems;

namespace AlbionLocal.Server.Network;

/// <summary>StudyItemErrorCode (key 3 of a failed op 55 with RC 2202 for ACT 4).</summary>
public enum StudyError : byte
{
    OutOfRange = 2, ItemNotStudyable = 3, ItemHasNotFullDurability = 4, NotEnoughSilver = 5, NotEnoughCraftCapacity = 6,
    InvalidRights = 7, PlayerIsMounted = 12, NoItemToStudyFound = 13, ItemWontAddFameToAchievement = 14, ItemNotUnlockedToCraft = 15,
}

public sealed partial class WorldSession
{
    /// <summary>The study's duration: not in the client's data (cmm.aap has no own code) [assume: as the salvage].</summary>
    public static readonly TimeSpan StudyTime = SalvageTime;

    /// <summary>Whether <paramref name="t"/> offers the study: a crafting station (not a shop) without allowsstudy="false".</summary>
    public static bool Studies(BuildingTemplate t) =>
        t.CraftItems.Count > 0 && !t.IsShop && t.Attrs.GetValueOrDefault("allowsstudy") != "false";

    /// <summary>
    /// StudyItem (ACT 4; stageR items SPEC §1, no capture): the request is the salvage's (cmm.aaw 0x180B71800 → c32.i: key 5
    /// the item object ids, key 4 the client's price). The client's own checks, in its order (cmm.an 0x180B729E0 and cmm.ao
    /// 0x180B72D70): no item → NoItemToStudyFound (13); per item: in the player's inventory bag (else ItemNotStudyable, the
    /// client's Invalid 0 for an item outside its inventories [infer]), one the station crafts (3), not damaged (4), giving
    /// fame and feeding an in-progress craftitemfame node (14). Accepted: NewBuilding with key 16 and event 64 to the
    /// observers; at the end (<see cref="StudyTime"/>) the items deleted (InventoryDeleteItem), the fame
    /// (<see cref="Study.Fame"/> × Rates:Fame × Rates:CraftingFame, per item type through <see cref="Destiny.Award"/>: its
    /// UpdateFame and board events), NewBuilding, ActionOnBuildingFinished — the salvage's burst [infer]. No silver is charged:
    /// ownerless stations have no fee, as for the craft [assume]; key 4 is not checked. The craft unlock (15) is not checked
    /// either: crafting does not check it on this server.
    /// </summary>
    private AlbionResponse? StartStudy(Player player, Building building, ActionOnBuildingStartRequest req)
    {
        AlbionResponse Refuse(StudyError e, string why) => StationRefused(player, req, StationComponentError, (byte)e, why);
        var ids = (req.s ?? []).Distinct().ToList();
        if (ids.Count == 0) return Refuse(StudyError.NoItemToStudyFound, "no item listed");
        var bag = player.Inventory.Bag;
        var items = new List<Item>();
        foreach (var id in ids)
        {
            if (player.Inventory.FindItem(id) is not { } item || bag.IndexOf(item) < 0)
                return Refuse(StudyError.ItemNotStudyable, $"object {id} is not in the player's inventory");
            var t = item.Template;
            if (!building.Template.CraftItems.Contains(t.Name, StringComparer.OrdinalIgnoreCase)
                && !building.Template.CraftItems.Contains(t.BaseName, StringComparer.OrdinalIgnoreCase))
                return Refuse(StudyError.ItemNotStudyable, $"{building.Template.Name} does not craft {t.Name}");
            if (t.IsEquipment && t.MaxDurability > 0 && item.DurabilityRaw < t.FullDurabilityRaw)
                return Refuse(StudyError.ItemHasNotFullDurability, $"{t.Name} is damaged");
            if (!Study.GivesFame(t) || Study.Fame(world.Items, t, 1, world.Economy) <= 0 || !Study.FeedsBoard(player, t))
                return Refuse(StudyError.ItemWontAddFameToAchievement, $"{t.Name} adds no fame to the destiny board");
            items.Add(item);
        }
        var now = world.GameNow;
        var action = new BuildingAction(building, req.p, req.q, now, now + StudyTime.Ticks);
        Systems.Mounts.BeforeAction(player, "studies"); // as every accepted station action (stageD mounts SPEC §4.3)
        player.Action = action;
        SendEvent(building.NewBuilding(inUse: true));
        StartedToObservers(player, action);
        Log.Info(Name, $"'{player.Character.Name}' studies {string.Join(", ", items.Select(i => $"{i.Count} x {i.Template.Name}"))} at {building.Template.Name}");
        player.Map!.Timers.At(world.ClockAt(action.End), () => FinishStudy(player, action, items));
        return null;
    }

    private void FinishStudy(Player player, BuildingAction action, List<Item> items)
    {
        if (action.Over || player.Action != action || player != Player) return;
        var bag = player.Inventory.Bag;
        var still = items.Where(i => player.Inventory.FindItem(i.Id) == i && bag.IndexOf(i) >= 0).ToList();
        if (still.Count == 0)
        {
            CancelAction(player, "the items are gone", tellClient: true);
            return;
        }
        action.Over = true;
        player.Action = null;
        var gains = new Dictionary<ItemTemplate, long>();
        foreach (var item in still)
        {
            bag.Take(bag.IndexOf(item));
            SendEvent(new InventoryDeleteItemEvent { ckr = item.Id });
            var fame = Rates.ScaleFame(Study.Fame(world.Items, item.Template, item.Count, world.Economy), world.Rates.Fame * world.Rates.CraftingFame);
            gains[item.Template] = gains.GetValueOrDefault(item.Template) + fame;
        }
        player.SaveItems();
        foreach (var (type, fame) in gains)
            if (fame > 0) Destiny.Award(player, fame, Destiny.Craft(type), source: type);
        SendEvent(action.Building.NewBuilding());
        Finished(player, action);
        Log.Info(Name, string.Create(CultureInfo.InvariantCulture,
            $"'{player.Character.Name}' studied {still.Count} item(s): {string.Join(", ", gains.Select(g => $"{g.Key.Name} {g.Value / 10000.0:0.####} fame"))}"));
    }
}
