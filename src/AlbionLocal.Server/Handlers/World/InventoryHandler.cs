using System.Globalization;
using AlbionLocal.Photon;
using AlbionLocal.Server.Data;
using AlbionLocal.Server.Entities;
using AlbionLocal.Server.Game;
using AlbionLocal.Server.Protocol;

namespace AlbionLocal.Server.Network;

/// <summary>
/// Inventory housekeeping (stage 6 SPEC §6a.4: destroy, split, split into, stack all, reorder, add to stacks) and the
/// helpers the economy uses to give and take items (crafting, the shops, the market). Every request carries the
/// container's revision (the client's c15 counter, raised before it sends), which the answers echo: InventoryPutItem key 3,
/// InventoryDeleteItem key 1, as InventoryMoveItem key 5. None of these answers was captured (Q-A4): the layouts are the
/// client's and the order is inferred. A request that does not fit gets no answer; the client then resyncs from its own
/// prediction table. Works on the inventory and on an open bank vault tab (<see cref="Player.FindContainer"/>).
/// </summary>
public sealed partial class WorldSession
{
    /// <summary>The stack limit of <paramref name="type"/>: items.xml maxstacksize, 999 without it; 1 for equipment.</summary>
    internal static int MaxStack(ItemTemplate type) => type.IsEquipment ? 1 : type.MaxStackSize > 0 ? type.MaxStackSize : Harvesting.DefaultMaxStack;

    /// <summary>items.xml weight of one <paramref name="type"/> (0 without it).</summary>
    internal static double Weight(ItemTemplate type) =>
        double.TryParse(type.Attrs.GetValueOrDefault("weight"), NumberStyles.Float, CultureInfo.InvariantCulture, out var w) ? w : 0;

    /// <summary>The weight of everything in <paramref name="c"/>.</summary>
    internal static double Weight(Container c) => c.Items.Sum(i => Weight(i.Template) * i.Count);

    /// <summary>How many of <paramref name="type"/> lie in <paramref name="c"/> (all stacks).</summary>
    internal static int CountOf(Container c, ItemTemplate type) => c.Items.Where(i => i.Template == type).Sum(i => i.Count);

    /// <summary>
    /// Whether all of <paramref name="items"/> fit <paramref name="bag"/> the way <see cref="GiveItem"/> puts them (a count into
    /// one existing stack that takes it whole, else new stacks of at most <see cref="MaxStack"/> in free slots), with
    /// <paramref name="freed"/> slots more (stacks a craft empties before its output comes). <paramref name="fame"/>: the journals'
    /// saved fame (a market fetch; null = the default), a partly filled one always needs a free slot (as GiveItem).
    /// </summary>
    internal static bool Fits(Container bag, IEnumerable<(ItemTemplate Type, int Count)> items, int freed = 0, long? fame = null)
    {
        var free = Enumerable.Range(0, bag.Size).Count(s => bag[s] is null && bag.Unloaded[s] is null) + freed;
        var room = bag.Items.ToDictionary(i => i, i => MaxStack(i.Template) - i.Count);
        foreach (var (type, count) in items)
        {
            if (count <= 0) continue;
            var max = MaxStack(type);
            var journalFame = type.IsJournal ? fame ?? Item.DefaultFame(type) : 0;
            var alone = type.Journal is { } j && journalFame > 0 && journalFame < j.MaxFame;
            if (max > 1 && !alone && room.FirstOrDefault(r => r.Key.Template == type && r.Key.JournalFame == journalFame && !r.Key.PartlyFilled && r.Value >= count).Key is { } stack)
            {
                room[stack] -= count;
                continue;
            }
            free -= (count + max - 1) / max;
            if (free < 0) return false;
        }
        return true;
    }

    /// <summary>
    /// Puts <paramref name="count"/> × <paramref name="type"/> into the inventory as the official server does for an item gained
    /// (HARVEST.md §5): an existing stack that takes the whole count gets NewSimpleItem again with its new total; otherwise new
    /// objects (stacks of at most <see cref="MaxStack"/>, equipment one each with <paramref name="quality"/>) go into the lowest
    /// free slots, each NewSimpleItem / NewEquipmentItem + InventoryPutItem (no revision). <paramref name="saved"/> gives a
    /// moved stack its durability and spells (a market fetch). The caller checked <see cref="Fits"/>. Returns the objects
    /// touched (the stack, or the new ones), in order; the player hooks' OnItemAdded runs for the new ones.
    /// </summary>
    internal List<Item> GiveItem(Player player, ItemTemplate type, int count, int quality = 1, InventoryItem? saved = null)
    {
        var bag = player.Inventory.Bag;
        var touched = new List<Item>();
        var max = MaxStack(type);
        // A journal goes onto a stack of the same fame only, and a partly filled one onto none (Item.StacksWith).
        var fame = type.IsJournal ? saved?.Fame ?? Item.DefaultFame(type) : 0;
        if (max > 1 && bag.Items.FirstOrDefault(i => i.Template == type && i.JournalFame == fame && !i.PartlyFilled && i.Count + count <= max) is { } stack
            && !(type.Journal is { } j && fame > 0 && fame < j.MaxFame))
        {
            stack.Count += count;
            SendEvent(CountEvent(stack));
            touched.Add(stack);
            return touched;
        }
        for (var left = count; left > 0;)
        {
            var slot = bag.FreeSlot();
            if (slot < 0)
            {
                Log.Warn(Name, $"{left} x {type.Name} did not fit the inventory and are lost (the caller did not check)");
                break;
            }
            var n = Math.Min(left, max);
            left -= n;
            var record = (saved ?? new InventoryItem(type.Name, n)) with { Count = n, Quality = saved?.Quality ?? quality };
            if (saved is null && Systems.OpeningTutorial.Active(player) && player.Character.Tutorial!.Stage == 21
                && type.Name == "T1_MAIN_SWORD") record = record with { Active = [] };
            var item = CreateItem(type, record, SpellUsable(player.Progression));
            bag.Put(slot, item);
            SendEvent(new InventoryPutItemEvent { ckw = item.Id, ckx = slot, cky = bag.Id });
            world.Scripts.OnItemAdded(player, item);
            touched.Add(item);
        }
        return touched;
    }

    /// <summary>
    /// Takes <paramref name="count"/> × <paramref name="type"/> from the inventory, from the last stacks first: a stack that
    /// keeps some gets NewSimpleItem with its new count, an emptied one InventoryDeleteItem {0 id}. The caller checked
    /// <see cref="CountOf"/>. Returns the slots emptied.
    /// </summary>
    internal int TakeItems(Player player, ItemTemplate type, int count)
    {
        var bag = player.Inventory.Bag;
        var emptied = 0;
        for (var slot = bag.Size - 1; slot >= 0 && count > 0; slot--)
        {
            if (bag[slot] is not { } stack || stack.Template != type) continue;
            var n = Math.Min(count, stack.Count);
            count -= n;
            stack.Count -= n;
            if (stack.Count > 0) SendEvent(CountEvent(stack));
            else
            {
                bag.Take(slot);
                SendEvent(new InventoryDeleteItemEvent { ckr = stack.Id });
                emptied++;
            }
        }
        return emptied;
    }

    /// <summary>The count of a stack to the client: NewSimpleItem {0 id, 1 type, 2 count} again (as UseConsumableFromInventory), a journal's NewJournalItem.</summary>
    private void SendCount(Item stack) => SendEvent(CountEvent(stack));

    /// <summary>
    /// InventoryDestroyItem (op 29, client class sj: 0 item object, 1 revision): the client has removed the item already; the
    /// server drops it and confirms with InventoryDeleteItem {0 id, 1 revision}. An equipped item leaves the equipment (its
    /// script's OnUnequip, CharacterEquipmentChanged).
    /// </summary>
    [Handles(OperationCodes.InventoryDestroyItem, Status = SessionStatus.InWorld)]
    private AlbionResponse? InventoryDestroyItem(InventoryDestroyItemRequest req)
    {
        if (Player is not { } player) return null;
        var inventory = player.Inventory;
        var container = inventory.Equipment.Find(req.ri) is not null ? inventory.Equipment
            : inventory.Bag.Find(req.ri) is not null ? inventory.Bag
            : player.OpenVaults.Values.Select(v => v.Container).FirstOrDefault(c => c.Find(req.ri) is not null);
        if (container?.Find(req.ri) is not { } item)
        {
            Log.Info(Name, $"destroy of unknown item object {req.ri}");
            return null;
        }
        var mountSlot = container == inventory.Equipment && container.IndexOf(item) == Systems.Mounts.MountSlot;
        container.Take(container.IndexOf(item));
        SendEvent(new InventoryDeleteItemEvent { ckr = item.Id, cks = req.rj });
        if (container == inventory.Equipment)
        {
            world.Scripts.OnUnequip(player, item);
            SendEquipmentChanged(player, intoEquipment: false);
        }
        if (mountSlot) Systems.Mounts.Destroyed(player); // dismounted, the mount object gone (stageD mounts SPEC §4.4)
        player.SaveItems();
        Log.Info(Name, $"'{player.Character.Name}' destroyed {item} x{item.Count}");
        return null;
    }

    /// <summary>
    /// InventorySplitStack (op 33, sk: 0 slot, 1 container Guid, 2 revision, 3 count): <paramref name="req"/>'s count leaves the
    /// stack into the lowest free slot of the same container as a new object. Answer: NewSimpleItem of the source with its
    /// new count, NewSimpleItem of the new object, InventoryPutItem {0 new id, 1 slot, 2 Guid, 3 revision}.
    /// </summary>
    [Handles(OperationCodes.InventorySplitStack, Status = SessionStatus.InWorld)]
    private AlbionResponse? InventorySplitStack(InventorySplitStackRequest req)
    {
        if (Player is not { } player) return null;
        if (player.FindContainer(req.se) is not { } c || req.sd < 0 || req.sd >= c.Size || c[req.sd] is not { } source
            || source.Template.IsEquipment || req.sg <= 0 || req.sg >= source.Count || c.FreeSlot() is var slot && slot < 0)
        {
            Log.Info(Name, $"split of {req.se}#{req.sd} into {req.sg} refused");
            return null;
        }
        source.Count -= req.sg;
        SendCount(source);
        var part = CreateItem(source.Template, (source.Source ?? new InventoryItem(source.Template.Name)) with { Count = req.sg, Quality = source.Quality },
            SpellUsable(player.Progression));
        c.Put(slot, part);
        SendEvent(new InventoryPutItemEvent { ckw = part.Id, ckx = slot, cky = c.Id, ckz = req.sf });
        player.SaveItems();
        Log.Info(Name, $"'{player.Character.Name}' split {source.Template.Name}: {source.Count} + {part.Count} in #{slot}");
        return null;
    }

    /// <summary>
    /// InventorySplitStackInto (op 34, sl: 0 from slot, 1 from Guid, 2 to Guid, 3 to slot, 4 from revision, 5 to revision,
    /// 6 count): the count goes onto the target slot, a new object there when it is empty, into the stack there when it is the
    /// same type with room (its NewSimpleItem). The client refuses counts ≤ 1 itself.
    /// </summary>
    [Handles(OperationCodes.InventorySplitStackInto, Status = SessionStatus.InWorld)]
    private AlbionResponse? InventorySplitStackInto(InventorySplitStackIntoRequest req)
    {
        if (Player is not { } player) return null;
        var from = player.FindContainer(req.si);
        if (from is null && LootSource(player, req.si) is { } loot) return SplitFromLoot(player, loot, req); // stageM: a split drag out of the loot
        var to = player.FindContainer(req.sj);
        if (from is null || to is null || req.sh < 0 || req.sh >= from.Size || req.sk < 0 || req.sk >= to.Size || from[req.sh] is not { } source
            || source.Template.IsEquipment || req.sn <= 0 || req.sn >= source.Count || to == player.Inventory.Equipment
            || to[req.sk] is { } target && (!target.StacksWith(source) || target.Count + req.sn > MaxStack(target.Template))
            || to[req.sk] is null && to.Unloaded[req.sk] is not null || !VaultTakes(player, to, source.Template, req.sn, from))
        {
            Log.Info(Name, $"split of {req.si}#{req.sh} into {req.sj}#{req.sk} ({req.sn}) refused");
            if (from is null) ResyncTarget(player, to); // e.g. a loot bag gone meanwhile
            return null;
        }
        source.Count -= req.sn;
        SendCount(source);
        if (to[req.sk] is { } stack)
        {
            stack.Count += req.sn;
            SendCount(stack);
        }
        else
        {
            var part = CreateItem(source.Template, (source.Source ?? new InventoryItem(source.Template.Name)) with { Count = req.sn, Quality = source.Quality },
                SpellUsable(player.Progression));
            to.Put(req.sk, part);
            SendEvent(new InventoryPutItemEvent { ckw = part.Id, ckx = req.sk, cky = to.Id, ckz = req.sm });
        }
        player.SaveItems();
        return null;
    }

    /// <summary>
    /// InventoryStack (op 35, sm: 0 Guid, 1 revision), "stack all": the stacks of each type fill the earlier ones up to the
    /// stack limit; per changed stack NewSimpleItem with its count, per emptied one InventoryDeleteItem {0 id, 1 revision}.
    /// </summary>
    [Handles(OperationCodes.InventoryStack, Status = SessionStatus.InWorld)]
    private AlbionResponse? InventoryStack(InventoryStackRequest req)
    {
        if (Player is not { } player || player.FindContainer(req.so) is not { } c || c == player.Inventory.Equipment) return null;
        var merged = Merge(c, c, req.sp);
        if (merged > 0) player.SaveItems();
        return null;
    }

    /// <summary>
    /// InventoryAddToStacks (op 38, rd: 0 from Guid, 1 from revision, 2 to Guid, 3 to revision): every stack of the first
    /// container whose type has a stack with room in the second moves into it as far as it fits (the answers as op 35).
    /// </summary>
    [Handles(OperationCodes.InventoryAddToStacks, Status = SessionStatus.InWorld)]
    private AlbionResponse? InventoryAddToStacks(InventoryAddToStacksRequest req)
    {
        if (Player is not { } player || player.FindContainer(req.re) is not { } from || player.FindContainer(req.rg) is not { } to
            || from == to || from == player.Inventory.Equipment || to == player.Inventory.Equipment) return null;
        var merged = Merge(from, to, req.rf);
        if (merged > 0) player.SaveItems();
        return null;
    }

    /// <summary>
    /// Moves stack contents from <paramref name="from"/> into stacks of the same type in <paramref name="to"/> with room (in
    /// slot order; the same container: into earlier slots only). Returns how many stacks changed.
    /// </summary>
    private int Merge(Container from, Container to, long revision)
    {
        var changed = 0;
        for (var s = from == to ? 1 : 0; s < from.Size; s++)
        {
            if (from[s] is not { } source || MaxStack(source.Template) <= 1) continue;
            var max = MaxStack(source.Template);
            var moved = false;
            for (var t = 0; t < (from == to ? s : to.Size) && source.Count > 0; t++)
            {
                if (to[t] is not { } target || target == source || !target.StacksWith(source) || target.Count >= max) continue;
                var n = Math.Min(max - target.Count, source.Count);
                target.Count += n;
                source.Count -= n;
                SendCount(target);
                changed++;
                moved = true;
            }
            if (source.Count == 0)
            {
                from.Take(s);
                SendEvent(new InventoryDeleteItemEvent { ckr = source.Id, cks = revision });
            }
            else if (moved) SendCount(source);
        }
        return changed;
    }

    /// <summary>
    /// InventoryReorder (op 36, si: 0 Guid, 1 object ids in the new order, 2 revision): the listed items take the first slots
    /// in that order, the others follow in their old order; InventoryPutItem {0 id, 1 slot, 2 Guid, 3 revision} per item that moved.
    /// </summary>
    [Handles(OperationCodes.InventoryReorder, Status = SessionStatus.InWorld)]
    private AlbionResponse? InventoryReorder(InventoryReorderRequest req)
    {
        if (Player is not { } player || player.FindContainer(req.sa) is not { } c || c == player.Inventory.Equipment) return null;
        if (Enumerable.Range(0, c.Size).Any(s => c.Unloaded[s] is not null))
        {
            Log.Info(Name, $"reorder of {req.sa} refused: it holds saved entries that did not load");
            return null;
        }
        var listed = (req.sb ?? []).Select(c.Find).OfType<Item>().Distinct().ToList();
        var order = listed.Concat(c.Items.Where(i => !listed.Contains(i))).ToList();
        var before = order.ToDictionary(i => i, c.IndexOf);
        for (var s = 0; s < c.Size; s++) c.Take(s);
        for (var s = 0; s < order.Count; s++)
        {
            c.Put(s, order[s]);
            if (before[order[s]] != s) SendEvent(new InventoryPutItemEvent { ckw = order[s].Id, ckx = s, cky = c.Id, ckz = req.sc });
        }
        player.SaveItems();
        return null;
    }

    /// <summary>
    /// Whether <paramref name="to"/> may take <paramref name="count"/> × <paramref name="type"/> more: always unless it is an
    /// open vault tab, whose weight limit (gamedata CityBank 100000) counts what lies there already (an item coming from the
    /// same vault does not add).
    /// </summary>
    internal static bool VaultTakes(Player player, Container to, ItemTemplate type, int count, Container from)
    {
        if (to == from || player.OpenVaults.Values.FirstOrDefault(v => v.Container == to) is not { } vault) return true;
        return Weight(to) + Weight(type) * count <= vault.WeightLimit;
    }
}
