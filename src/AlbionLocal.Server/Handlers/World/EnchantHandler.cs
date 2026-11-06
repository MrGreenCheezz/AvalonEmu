using System.Globalization;
using AlbionLocal.Photon;
using AlbionLocal.Server.Data;
using AlbionLocal.Server.Entities;
using AlbionLocal.Server.Game;
using AlbionLocal.Server.Protocol;
using AlbionLocal.Server.Systems;

namespace AlbionLocal.Server.Network;

/// <summary>UpgradeItemEnchantmentErrorCode (key 3 of a failed op 55 with RC 2202 for ACT 7).</summary>
public enum EnchantError : byte
{
    OutOfRange = 2, ItemNotUpgradeable = 3, ItemNotUpgradeableAnymore = 4, ItemTierToHigh = 5, ItemDamaged = 6, NotEnoughResources = 7,
    PriceChanged = 14, NotEnoughGold = 15, NotEnoughSilver = 16,
}

/// <summary>RerollQualityErrorCode (key 3 of a failed op 55 with RC 2202 for ACT 6).</summary>
public enum RerollError : byte
{
    OutOfRange = 2, ItemNotRerollable = 3, ItemCanNotBeImproved = 4, ItemAlreadyAtMaxQuality = 6, ItemTierToHigh = 7, NotEnoughSilver = 8,
    PriceChanged = 11, ItemsNotMatchingRequest = 15,
}

/// <summary>
/// Enchanting (ACT 7) and the quality reroll (ACT 6) at the stations (analysis/work/stageN/enchant/SPEC.md). Both run as the repair:
/// nothing is taken at the start (NewBuilding key 16, event 64 to the observers); at the end everything is checked again, then paid and
/// done in one step, so a failure on the way (item moved, resources gone, silver spent) cancels without a loss.
/// </summary>
public sealed partial class WorldSession
{
    /// <summary>The enchanting's duration (not captured; as <see cref="RepairTime"/>).</summary>
    public static readonly TimeSpan EnchantTime = RepairTime;

    /// <summary>The reroll's duration (not captured; as <see cref="RepairTime"/>).</summary>
    public static readonly TimeSpan RerollTime = RepairTime;

    /// <summary>
    /// The variant upgrading <paramref name="type"/> gives: NAME@(enchantment + 1) when it has upgraderequirements (the client's
    /// cmn.al 0x180B74800 reads them from the target variant); null = cannot be upgraded.
    /// </summary>
    internal ItemTemplate? UpgradeTarget(ItemTemplate type) =>
        world.Items.Find($"{type.BaseName}@{type.Enchantment + 1}") is { Upgrade: not null } next && next.BaseName == type.BaseName ? next : null;

    /// <summary>What upgrading a stack of <paramref name="count"/> into <paramref name="target"/> takes from the inventory (count × each upgraderesource), or why not.</summary>
    private (List<(ItemTemplate Type, int Count)>? Needs, string? Missing) UpgradeNeeds(Player player, ItemTemplate target, int count)
    {
        var needs = new List<(ItemTemplate, int)>();
        foreach (var r in target.Upgrade!.Resources)
        {
            var type = (r.Enchantment > 0 ? world.Items.Find($"{r.Name}@{r.Enchantment}") : null) ?? world.Items.Find(r.Name);
            if (type is null) return (null, $"unknown resource {r.Name}");
            var need = checked(r.Count * count);
            var have = CountOf(player.Inventory.Bag, type);
            if (have < need) return (null, $"{have} of {need} x {type.Name}");
            needs.Add((type, need));
        }
        return (needs, null);
    }

    /// <summary>
    /// Enchanting (ACT 7; client cmn, EnchantItemView; request as the repair's: key 5 = [the item object], key 4 = the usage fee, 0 at
    /// an ownerless station). The item must be the player's (inventory or worn, not the mount), have an upgrade target
    /// (<see cref="UpgradeTarget"/>, else ItemNotUpgradeable, ItemNotUpgradeableAnymore when it is enchanted already) of a tier up to
    /// the building's (ItemTierToHigh), not be broken (ItemDamaged), and the inventory must hold count × the target's upgraderesources
    /// (NotEnoughResources). The upgraderequirements carry no silver or gold in this build; silver would be checked as the price (×
    /// count), gold is refused (NotEnoughGold). At the end (<see cref="EnchantTime"/>): the resources go, the item object is replaced by
    /// a new one of the target type in the same slot (InventoryDeleteItem, NewEquipmentItem/NewSimpleItem, InventoryPutItem) with its
    /// quality, spells and durability share; a worn one updates the equipment (CharacterEquipmentChanged). Then NewBuilding without
    /// key 16 and ActionOnBuildingFinished, which the client's EnchantItemView answers with its success sound.
    /// </summary>
    private AlbionResponse? StartEnchant(Player player, Building building, ActionOnBuildingStartRequest req)
    {
        AlbionResponse Refuse(EnchantError e, string why) => StationRefused(player, req, StationComponentError, (byte)e, why);
        if ((req.s ?? []).Distinct().ToList() is not [var id] || player.Inventory.FindItem(id) is not { } item)
            return Refuse(EnchantError.ItemNotUpgradeable, "not one item of the player listed");
        if (player.Inventory.Equipment.IndexOf(item) == Mounts.MountSlot) return Refuse(EnchantError.ItemNotUpgradeable, "the mount slot");
        if (UpgradeTarget(item.Template) is not { Upgrade: { } cost } target)
            return Refuse(item.Template.Enchantment > 0 ? EnchantError.ItemNotUpgradeableAnymore : EnchantError.ItemNotUpgradeable, $"{item.Template.Name} has no upgrade");
        if (target.Tier > building.Template.Tier) return Refuse(EnchantError.ItemTierToHigh, $"{target.Name} is above the building's tier {building.Template.Tier}");
        if (item.Template.IsEquipment && item.Template.MaxDurability > 0 && item.DurabilityRaw <= 0) return Refuse(EnchantError.ItemDamaged, $"{item.Template.Name} is broken");
        if (cost.Gold > 0) return Refuse(EnchantError.NotEnoughGold, "gold upgrades are not made");
        var silver = (long)Math.Round((decimal)cost.Silver * Pricing.A7m) * item.Count;
        if (!Pricing.Accepts(req.r, silver, world.Config.GoldPriceIndex))
            return Refuse(EnchantError.PriceChanged, string.Create(CultureInfo.InvariantCulture, $"the client's price {req.r / 10000.0:0.####} is not the server's {silver / 10000.0:0.####}"));
        var price = Math.Max(req.r, 0);
        if (player.Character.Silver < price) return Refuse(EnchantError.NotEnoughSilver, $"{player.Character.Silver / 10000} silver of {price / 10000}");
        if (UpgradeNeeds(player, target, item.Count) is { Missing: { } missing }) return Refuse(EnchantError.NotEnoughResources, missing);
        var now = world.GameNow;
        var action = new BuildingAction(building, req.p, req.q, now, now + EnchantTime.Ticks);
        Mounts.BeforeAction(player, "enchants");
        player.Action = action;
        SendEvent(building.NewBuilding(inUse: true));
        StartedToObservers(player, action);
        Log.Info(Name, $"'{player.Character.Name}' enchants {item} into {target.Name} at {building.Template.Name}");
        player.Map!.Timers.At(world.ClockAt(action.End), () => FinishEnchant(player, action, item, target, price));
        return null;
    }

    private void FinishEnchant(Player player, BuildingAction action, Item item, ItemTemplate target, long price)
    {
        if (action.Over || player.Action != action || player != Player) return;
        var inventory = player.Inventory;
        var equipped = inventory.IsEquipped(item);
        var container = equipped ? inventory.Equipment : inventory.Bag;
        var slot = container.IndexOf(item);
        if (inventory.FindItem(item.Id) != item || slot < 0)
        {
            CancelAction(player, "the item is gone", tellClient: true);
            return;
        }
        if (UpgradeNeeds(player, target, item.Count) is not { Needs: { } needs } || player.Character.Silver < price)
        {
            CancelAction(player, "the resources or the silver are gone", tellClient: true);
            return;
        }
        if (price > 0 && !Wallet.TrySpend(player, price, $"enchanting at {action.Building.Template.Name}"))
        {
            CancelAction(player, "not enough silver any more", tellClient: true);
            return;
        }
        action.Over = true;
        player.Action = null;
        foreach (var (type, need) in needs) TakeItems(player, type, need);
        var record = item.ToRecord(id => world.Spells?.Name(id)) with
        {
            Name = target.Name,
            // The durability share stays (a full item stays full: the target's durability is higher).
            Durability = item.Template.MaxDurability > 0 && item.DurabilityRaw < item.Template.FullDurabilityRaw
                ? (long)Math.Floor((decimal)item.DurabilityRaw * target.FullDurabilityRaw / item.Template.FullDurabilityRaw) : null,
        };
        container.Take(slot);
        SendEvent(new InventoryDeleteItemEvent { ckr = item.Id });
        if (equipped) world.Scripts.OnUnequip(player, item);
        var made = CreateItem(target, record, SpellUsable(player.Progression));
        container.Put(slot, made);
        SendEvent(new InventoryPutItemEvent { ckw = made.Id, ckx = slot, cky = container.Id });
        if (equipped)
        {
            world.Scripts.OnEquip(player, made);
            SendEquipmentChanged(player, intoEquipment: true);
        }
        SendEvent(action.Building.NewBuilding());
        Finished(player, action);
        player.SaveItems();
        Log.Info(Name, $"'{player.Character.Name}' enchanted {item.Template.Name} into {made}{string.Concat(needs.Select(n => $", {n.Count} x {n.Type.Name}"))}");
    }

    /// <summary>
    /// The reroll price of <paramref name="item"/> (a7m; the client's cmk.al 0x180B6CF20 → bca.i 0x180598A50): item value ×
    /// RerollQuality CostModifier of its quality, + the silver of its first recipe; 0 when its quality has no modifier (5).
    /// The client then applies the building's fee factors (none at an ownerless station) and a discount (ciy.a), which only lower it.
    /// </summary>
    public long RerollPrice(Item item)
    {
        var mods = world.Economy.RerollCost;
        if (item.Quality < 1 || item.Quality > mods.Count) return 0;
        var value = world.ItemValues.Of(item.Template) * mods[item.Quality - 1] + (item.Template.Recipes.FirstOrDefault()?.Silver ?? 0);
        return (long)Math.Round((decimal)value * Pricing.A7m);
    }

    /// <summary>
    /// The quality reroll (ACT 6; client cmk, RerollItemQualityView; request cmk.aaw 0x180B6CA80: key 5 the item objects, 4 the price,
    /// 6 their counts, 12 a short, 13 their qualities as the client sees them). Each listed item must be the player's (not the mount)
    /// equipment (ItemNotRerollable), of a tier up to the building's (ItemTierToHigh), below its maxqualitylevel (ItemAlreadyAtMaxQuality)
    /// with a price (ItemCanNotBeImproved); keys 6/13, when sent, must match (ItemsNotMatchingRequest); the client's price per
    /// <see cref="Pricing.Accepts"/> against Σ <see cref="RerollPrice"/> (PriceChanged) and in the wallet (NotEnoughSilver). At the end
    /// (<see cref="RerollTime"/>): the silver (UpdateMoney), each item's new quality by RerollQualityChances (never lower), the improved
    /// items re-sent (NewEquipmentItem, same object), ItemRerollQualityFinished {0 player, 1 time, 2 building, 3 improved items, 4 all
    /// items}, which the view turns into its effects (3 empty: "no improvement") and refills its slots from 4; NewBuilding, 66.
    /// The official ItemNeedsFullDurability is not enforced (the client does not check it; the rule is server-side and unknown).
    /// </summary>
    private AlbionResponse? StartReroll(Player player, Building building, ActionOnBuildingStartRequest req)
    {
        AlbionResponse Refuse(RerollError e, string why) => StationRefused(player, req, StationComponentError, (byte)e, why);
        var ids = (req.s ?? []).ToList();
        if (ids.Count == 0 || ids.Distinct().Count() != ids.Count) return Refuse(RerollError.ItemNotRerollable, "no items or one listed twice");
        var items = new List<Item>();
        foreach (var id in ids)
        {
            if (player.Inventory.FindItem(id) is not { } item || player.Inventory.Equipment.IndexOf(item) == Mounts.MountSlot)
                return Refuse(RerollError.ItemNotRerollable, $"object {id} is not the player's");
            if (!item.Template.IsEquipment || item.Template.Kind == "mount") return Refuse(RerollError.ItemNotRerollable, $"{item.Template.Name} is no equipment");
            if (item.Template.Tier > building.Template.Tier) return Refuse(RerollError.ItemTierToHigh, $"{item.Template.Name} is above the building's tier {building.Template.Tier}");
            if (item.Quality >= item.Template.MaxQuality) return Refuse(RerollError.ItemAlreadyAtMaxQuality, $"{item} is at quality {item.Quality}");
            if (RerollPrice(item) <= 0 || world.Economy.RerollWeights is var table && (item.Quality > table.Count || table[item.Quality - 1].Sum() <= 0))
                return Refuse(RerollError.ItemCanNotBeImproved, $"{item} has no reroll at quality {item.Quality}");
            items.Add(item);
        }
        if (req.aa is { Length: > 0 } seen && (seen.Length != items.Count || items.Where((i, k) => i.Quality != seen[k]).Any()))
            return Refuse(RerollError.ItemsNotMatchingRequest, "the client's qualities are not the server's");
        if (req.t is { Length: > 0 } counts && (counts.Length != items.Count || items.Where((i, k) => i.Count != counts[k]).Any()))
            return Refuse(RerollError.ItemsNotMatchingRequest, "the client's counts are not the server's");
        var expected = items.Sum(RerollPrice);
        if (!Pricing.Accepts(req.r, expected, world.Config.GoldPriceIndex))
            return Refuse(RerollError.PriceChanged, string.Create(CultureInfo.InvariantCulture, $"the client's price {req.r / 10000.0:0.####} is not the server's {expected / 10000.0:0.####}"));
        if (player.Character.Silver < req.r) return Refuse(RerollError.NotEnoughSilver, $"{player.Character.Silver / 10000} silver of {req.r / 10000}");
        var now = world.GameNow;
        var action = new BuildingAction(building, req.p, req.q, now, now + RerollTime.Ticks);
        Mounts.BeforeAction(player, "rerolls");
        player.Action = action;
        SendEvent(building.NewBuilding(inUse: true));
        StartedToObservers(player, action);
        var qualities = items.Select(i => i.Quality).ToArray();
        Log.Info(Name, string.Create(CultureInfo.InvariantCulture,
            $"'{player.Character.Name}' rerolls the quality of {string.Join(", ", items)} at {building.Template.Name} for {req.r / 10000.0:0.####} silver (key 12 = {req.z})"));
        player.Map!.Timers.At(world.ClockAt(action.End), () => FinishReroll(player, action, items, qualities, req.r));
        return null;
    }

    private void FinishReroll(Player player, BuildingAction action, List<Item> items, int[] qualities, long price)
    {
        if (action.Over || player.Action != action || player != Player) return;
        if (items.Where((i, k) => player.Inventory.FindItem(i.Id) != i || i.Quality != qualities[k]).Any())
        {
            CancelAction(player, "an item is gone or changed", tellClient: true);
            return;
        }
        if (!Wallet.TrySpend(player, price, $"quality reroll at {action.Building.Template.Name}"))
        {
            CancelAction(player, "not enough silver any more", tellClient: true);
            return;
        }
        action.Over = true;
        player.Action = null;
        var improved = new List<Item>();
        foreach (var item in items)
        {
            var quality = Math.Min(item.Template.MaxQuality, RollReroll(world.CraftRandom, world.Economy.RerollWeights, item.Quality) ?? item.Quality);
            if (quality <= item.Quality) continue;
            item.Quality = quality;
            SendItemObject(item);
            improved.Add(item);
        }
        SendEvent(new ItemRerollQualityFinishedEvent
        {
            ddn = player.Id, cl8 = world.GameNow, cl9 = action.Building.Id,
            cma = [.. improved.Select(i => i.Id)], cmb = [.. items.Select(i => i.Id)],
        });
        SendEvent(action.Building.NewBuilding());
        Finished(player, action);
        if (improved.Any(player.Inventory.IsEquipped) && player.RecalculateStats(world.GameNow)) SendAround(player, Combat.PlayerCombo(player, world.GameNow));
        player.SaveItems();
        Log.Info(Name, $"'{player.Character.Name}' rerolled {items.Count} item(s): {string.Join(", ", items.Select((i, k) => $"{i.Template.Name} {qualities[k]} -> {i.Quality}"))}");
    }
}
