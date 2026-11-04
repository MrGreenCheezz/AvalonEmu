using AlbionLocal.Photon;
using AlbionLocal.Server.Entities;
using AlbionLocal.Server.Game;
using AlbionLocal.Server.Protocol;
using AlbionLocal.Server.Systems;

namespace AlbionLocal.Server.Network;

/// <summary>
/// Taking single items out of an open loot container (a mob's bag, a dead player's bag, a chest's loot; stageM loot-moves NOTES).
/// The client's take-all is InventoryMoveGivenItems (<see cref="InventoryMoveGivenItems"/>); a shift+click, a drag onto a bag slot
/// and a drag onto (or double-click into) the equipment are the ordinary InventoryMoveItem with the loot container's Guid as the
/// source (GameGui.MoveItemToItemContainer, UIItemSlot.PerformServerMove, GameGui.EquipItem → c13.b(int,Guid,int,Guid)), a split
/// drag InventorySplitStackInto. A loot container is a source only, never a target: it is not one of <see cref="Player.FindContainer"/>'s.
/// </summary>
public sealed partial class WorldSession
{
    /// <summary>
    /// A loot container a move names: the bag, its container for this player (<see cref="LootObject.ItemsFor"/>) and why the player may
    /// not take from it now (null: it may).
    /// </summary>
    private sealed record LootTake(LootObject Bag, Container Items, string? Refused);

    /// <summary>
    /// The loot container <paramref name="id"/> on the player's map (the open bag first), with the rules of <see cref="RegisterToObject"/>:
    /// open in its window (<see cref="Player.OpenBag"/>), still on the map, the player standing, a looter of it (<see cref="LootObject.MayOpen"/>:
    /// ownership, party, a dead player's protection time, a chest's viewer state), within reach and, for a corrupted run's chest, its
    /// owner (<see cref="CorruptedDungeons.ChestRefused"/>), for a hellgate match's, its winners (<see cref="Hellgates.ChestRefused"/>). Null when no loot container has that Guid.
    /// </summary>
    private LootTake? LootSource(Player player, Guid id)
    {
        if (player.Map is not { } map || id == Guid.Empty) return null;
        var bag = player.OpenBag is { } open && open.ItemsFor(player).Id == id ? open
            : map.Objects.Values.OfType<LootObject>().FirstOrDefault(b => b.ItemsFor(player).Id == id);
        if (bag is null) return null;
        var refused = bag.Map != map || map.Objects.GetValueOrDefault(bag.Id) != bag ? "it is gone"
            : player.OpenBag != bag ? "its window is not open"
            : player.Combat.Down is not null ? "knocked down"
            : !bag.MayOpen(player) ? "not a looter"
            : !InReach(player, bag) ? "out of reach"
            : bag is LootChest chest ? world.Corrupted.ChestRefused(player, chest) ?? world.Hellgates.ChestRefused(player, chest) : null;
        return new LootTake(bag, bag.ItemsFor(player), refused);
    }

    /// <summary>
    /// After items left <paramref name="items"/>, <paramref name="bag"/>'s container for <paramref name="player"/>: emptied, the window
    /// closes (DetachItemContainer) and the bag goes (its Leave; <see cref="Combat.RemoveBag"/> closes it for the others too), a personal
    /// chest only for this player (its viewer state Empty, 8, stageH); else every other player with the same container open gets its
    /// InventoryState {0 ids, 2 Guid} without a revision, so its client drops what is gone (a failed revision check resyncs its views),
    /// after the new count (NewSimpleItem) of every stack in <paramref name="changed"/> still in the loot: a partial take leaves the
    /// ids as they were, and the client's InventoryState handling never touches an object's count.
    /// </summary>
    private void AfterLootTake(Player player, LootObject bag, Container items, IEnumerable<Item>? changed = null)
    {
        if (items.Count == 0)
        {
            player.OpenBag = null;
            SendEvent(new DetachItemContainerEvent { b20 = items.Id });
            if (bag is LootChest { Personal: true } own)
            {
                own.SetViewerState(player, 8, world.GameNow);
                SendEvent(own.Update(player));
            }
            else Combat.RemoveBag(bag, "emptied");
            return;
        }
        if (bag.Map is not { } map) return;
        var counts = (changed ?? []).Distinct().Where(i => i.Count > 0 && items.ObjectIds().Contains(i.Id)).ToList();
        foreach (var other in map.Players.Values)
            if (other != player && other.OpenBag == bag && bag.ItemsFor(other) == items)
            {
                foreach (var stack in counts) other.Session.Send(CountEvent(stack));
                other.Session.Send(new InventoryStateEvent { ck0 = items.ObjectIds(), ck2 = items.Id });
            }
    }

    /// <summary>The loot window of <paramref name="bag"/> is still open for <paramref name="player"/> and the bag on its map.</summary>
    private static bool Attached(Player player, LootObject bag) =>
        player.OpenBag == bag && bag.Map is { } map && map == player.Map && map.Objects.GetValueOrDefault(bag.Id) == bag;

    /// <summary>
    /// The client predicted a move into <paramref name="target"/> (the inventory or the equipment) out of a container it may no longer
    /// know (the loot bag gone, its window closed: the client drops a put or a state addressed to an unknown Guid unseen): the
    /// target's InventoryState {0 ids, 2 Guid} without a revision fails the client's check and reverts every view to the server state.
    /// </summary>
    private void ResyncTarget(Player player, Container? target)
    {
        if (target is not null && (target == player.Inventory.Bag || target == player.Inventory.Equipment))
            SendEvent(new InventoryStateEvent { ck0 = target.ObjectIds(), ck2 = target.Id });
    }

    /// <summary>
    /// The client's prediction of a refused move out of <paramref name="items"/> goes back: the item is put into its loot slot again
    /// {0 id, 1 slot, 2 loot Guid, 3 the loot's revision of the request} (InventoryPutItem: its revision check fails and the client's views
    /// resync to the server state, stageM loot-moves NOTES §1); without an item there (taken meanwhile) the loot's InventoryState without
    /// a revision does the same. Never silent: an unanswered move leaves the icon where the client put it.
    /// </summary>
    private void LootPutBack(Container items, int slot, long revision)
    {
        if (slot >= 0 && slot < items.Size && items[slot] is { } item) SendEvent(new InventoryPutItemEvent { ckw = item.Id, ckx = slot, cky = items.Id, ckz = revision });
        else SendEvent(new InventoryStateEvent { ck0 = items.ObjectIds(), ck2 = items.Id });
    }

    /// <summary>
    /// InventoryMoveItem out of an open loot container (<paramref name="take"/>) into the inventory or the equipment (stageM loot-moves
    /// NOTES §5): 0 the loot slot, 1 the loot Guid, 2 its revision, 3 the target slot, 4 the inventory or equipment Guid, 5 its revision.
    /// <list type="bullet">
    /// <item>An empty slot that fits: the object moves; InventoryPutItem {id, slot, target, key 5}.</item>
    /// <item>A stack of the same type with room: as much as fits joins it (NewSimpleItem of the stack; the rest stays in the loot, re-sent),
    /// then InventoryState of the loot {key 2} and of the target {key 5}.</item>
    /// <item>An occupied equipment slot: the item is equipped and the replaced one (with the offhand for a two-handed weapon, the
    /// two-handed mainhand for an offhand) goes into the first free inventory slots, as the client's equipment → inventory fallback does
    /// (ctp.atf); captured order: put(moved), [96], 90 CharacterEquipmentChanged, put(replaced) without a revision (the client cannot be
    /// told its fallback revision, so it resyncs to the server state). OnUnequip/OnEquip and Consumables.Equipped as for any equip; a
    /// mount replaced while dismounted takes its mount object with it.</item>
    /// <item>An occupied inventory slot would swap into the loot: refused (the client's SwapNotPossible).</item>
    /// </list>
    /// The mount-in-use and allowequipmentswapping rules of <see cref="InventoryMoveItem"/> hold. A refusal puts the item back
    /// (<see cref="LootPutBack"/>). The slot is read under the lock, so a second take of the same object finds it gone: no duplication.
    /// Then <see cref="AfterLootTake"/>, OnItemAdded for the taken object, the items saved. No response.
    /// </summary>
    private AlbionResponse? MoveFromLoot(Player player, LootTake take, InventoryMoveItemRequest req)
    {
        var (bag, loot, refused) = take;
        var inventory = player.Inventory;
        var equipment = inventory.Equipment;
        var moved = req.ru >= 0 && req.ru < loot.Size ? loot[req.ru] : null;
        AlbionResponse? Back(string why)
        {
            Log.Info(Name, $"'{player.Character.Name}' take of {loot.Id}#{req.ru} ({moved?.Template.Name ?? "empty"}) into {req.ry}#{req.rx} refused: {why}");
            LootPutBack(loot, req.ru, req.rw);
            if (refused is not null && !Attached(player, bag)) ResyncTarget(player, player.FindContainer(req.ry)); // gone or closed: the put-back may go unseen
            return null;
        }
        if (refused is not null) return Back(refused);
        if (moved is null) return Back("nothing in that loot slot (taken already)");
        var to = player.FindContainer(req.ry);
        if (to != inventory.Bag && to != equipment) return Back("the target is not the inventory or the equipment");
        if (req.rx < 0 || req.rx >= to.Size) return Back("no such target slot");
        var other = to[req.rx];
        if (to == equipment)
        {
            if (req.rx == Mounts.MountSlot && player.Mount.State != MountState.None) return Back($"the mount slot is in use ({player.Mount.State})");
            if (!Pvp.RulesOf(world, player.Map).AllowEquipmentSwapping) return Back($"no equipment change in {player.Map?.Cluster} (allowequipmentswapping false)");
        }
        if (!to.Fits(moved, req.rx)) return Back($"'{moved.Template.Name}' does not fit that slot");
        if (other is null && to.Unloaded[req.rx] is not null) return Back("the slot keeps an unloaded saved item");

        var max = MaxStack(moved.Template);
        if (world.Modules.BeforeLootTake(player, loot, moved, other is not null && max > 1 && other.StacksWith(moved) ? Math.Min(moved.Count, max - other.Count) : moved.Count)) return Back("a module refused it"); // the modules' Items.OnBeforeLootTake
        if (other is not null && max > 1 && other.StacksWith(moved))
        {
            if (other.Count >= max) return Back("the stack there is full");
            var n = Math.Min(moved.Count, max - other.Count);
            other.Count += n;
            moved.Count -= n;
            if (moved.Count == 0) loot.Take(req.ru);
            SendCount(other);
            if (moved.Count > 0) SendCount(moved);
            SendEvent(new InventoryStateEvent { ck0 = loot.ObjectIds(), ck2 = loot.Id, ck3 = req.rw });
            SendEvent(new InventoryStateEvent { ck0 = to.ObjectIds(), ck2 = to.Id, ck3 = req.rz });
            AfterLootTake(player, bag, loot, [moved]);
            player.SaveItems();
            Log.Info(Name, $"'{player.Character.Name}' took {moved.Template.Name} x{n} from {bag} onto the stack in {(to == equipment ? Inventory.SlotNames[req.rx] : $"inventory #{req.rx}")}");
            return null;
        }
        if (other is not null && to != equipment) return Back("the inventory slot is taken (its item cannot go into the loot)");

        // The equipment slots the new item empties: the one it goes to, the offhand for a two-handed mainhand, the two-handed mainhand for an offhand.
        var replaced = new List<(int Slot, Item Item)>();
        if (to == equipment)
        {
            if (other is not null) replaced.Add((req.rx, other));
            if (req.rx == 0 && moved.Template.TwoHanded && equipment[1] is { } offhand) replaced.Add((1, offhand));
            if (req.rx == 1 && equipment[0] is { Template.TwoHanded: true } twoHanded) replaced.Add((0, twoHanded));
        }
        var free = Enumerable.Range(0, inventory.Bag.Size).Where(s => inventory.Bag[s] is null && inventory.Bag.Unloaded[s] is null).Take(replaced.Count).ToList();
        if (free.Count < replaced.Count) return Back("no free inventory slot for the replaced item");

        loot.Take(req.ru);
        var puts = new List<(Item Item, int Slot)>();
        for (var i = 0; i < replaced.Count; i++)
        {
            equipment.Take(replaced[i].Slot);
            inventory.Bag.Put(free[i], replaced[i].Item);
            puts.Add((replaced[i].Item, free[i]));
        }
        to.Put(req.rx, moved);
        if (to == equipment)
        {
            foreach (var (_, item) in replaced) world.Scripts.OnUnequip(player, item);
            world.Scripts.OnEquip(player, moved);
            Consumables.Equipped(player, req.rx); // Combat:EquipCooldownSeconds on the potion and food slots (0 = off)
        }
        SendEvent(new InventoryPutItemEvent { ckw = moved.Id, ckx = req.rx, cky = to.Id, ckz = req.rz });
        if (to == equipment) SendEquipmentChanged(player, intoEquipment: true);
        foreach (var (item, slot) in puts) SendEvent(new InventoryPutItemEvent { ckw = item.Id, ckx = slot, cky = inventory.Bag.Id });
        if (to == equipment && req.rx == Mounts.MountSlot && replaced.Count > 0) Mounts.Unequipped(player); // the old mount's object goes with it (stageD mounts SPEC §4.4)
        AfterLootTake(player, bag, loot);
        world.Scripts.OnItemAdded(player, moved);
        player.SaveItems();
        Log.Info(Name, $"'{player.Character.Name}' took {moved.Template.Name} x{moved.Count} from {bag} into {(to == equipment ? Inventory.SlotNames[req.rx] : $"inventory #{req.rx}")}"
            + (replaced.Count > 0 ? $" ({string.Join(", ", replaced.Select(r => r.Item.Template.Name))} into the inventory)" : ""));
        return null;
    }

    /// <summary>
    /// InventorySplitStackInto out of an open loot container (a split drag, stageM loot-moves NOTES §5.3): a part of a stack into an
    /// inventory slot, empty or the same type with room, under the rules of <see cref="LootSource"/>; the loot stack's new count goes out
    /// as NewSimpleItem, the part as a new object with its put {key 5} or onto the stack there (its NewSimpleItem). The equipment is no
    /// target, a split never empties the stack. A refusal resyncs the client (<see cref="LootPutBack"/>). The items are saved. No response.
    /// </summary>
    private AlbionResponse? SplitFromLoot(Player player, LootTake take, InventorySplitStackIntoRequest req)
    {
        var (bag, loot, refused) = take;
        var to = player.Inventory.Bag;
        var source = req.sh >= 0 && req.sh < loot.Size ? loot[req.sh] : null;
        refused ??= source is null ? "nothing in that loot slot"
            : player.FindContainer(req.sj) != to ? "the target is not the inventory"
            : source.Template.IsEquipment || req.sn <= 0 || req.sn >= source.Count ? $"a split of {req.sn} of {source.Count}"
            : req.sk < 0 || req.sk >= to.Size ? "no such target slot"
            : to[req.sk] is { } target && (!target.StacksWith(source) || target.Count + req.sn > MaxStack(target.Template)) ? "the target slot is taken"
            : to[req.sk] is null && to.Unloaded[req.sk] is not null ? "the slot keeps an unloaded saved item" : null;
        if (refused is null && world.Modules.BeforeLootTake(player, loot, source!, req.sn)) refused = "a module refused it"; // the modules' Items.OnBeforeLootTake
        if (refused is not null)
        {
            Log.Info(Name, $"'{player.Character.Name}' split of {loot.Id}#{req.sh} into {req.sj}#{req.sk} ({req.sn}) refused: {refused}");
            SendEvent(new InventoryStateEvent { ck0 = loot.ObjectIds(), ck2 = loot.Id });
            if (!Attached(player, bag)) ResyncTarget(player, to); // gone or closed: the loot's state may go unseen
            return null;
        }
        source!.Count -= req.sn;
        SendCount(source);
        Item added;
        if (to[req.sk] is { } stack)
        {
            stack.Count += req.sn;
            SendCount(stack);
            added = stack;
        }
        else
        {
            added = CreateItem(source.Template, (source.Source ?? new InventoryItem(source.Template.Name)) with { Count = req.sn, Quality = source.Quality },
                SpellUsable(player.Progression));
            to.Put(req.sk, added);
            SendEvent(new InventoryPutItemEvent { ckw = added.Id, ckx = req.sk, cky = to.Id, ckz = req.sm });
            world.Scripts.OnItemAdded(player, added);
        }
        AfterLootTake(player, bag, loot, [source]);
        player.SaveItems();
        Log.Info(Name, $"'{player.Character.Name}' took {source.Template.Name} x{req.sn} of a stack from {bag} into inventory #{req.sk}");
        return null;
    }
}
