using AlbionLocal.Server.Data;
using AlbionLocal.Server.Entities;
using AlbionLocal.Server.Game;
using AlbionLocal.Server.Network;
using AlbionLocal.Server.Protocol;

namespace AlbionLocal.Server.Systems;

public sealed partial class Shop
{
    private sealed record ItemDelivery(Container Bag, List<Item> Added);

    /// <summary>Stages every purchased item using the normal stack/slot rules. Only simple items are offered;
    /// unknown saved entries retain their slots, and overflow records are preserved rather than overwritten.</summary>
    private bool PrepareItems(Character snapshot, Player? live, IReadOnlyList<ShopItem> items, out ItemDelivery? delivery)
    {
        delivery = null;
        if (items.Count == 0) return true;
        var types = items.Select(i => world.Items.Find(i.Name)).ToArray();
        if (types.Any(t => t is not { IsSimple: true })) return false;
        var bag = new Container(snapshot.InventoryContainer, Inventory.BagSlots);
        if (live is not null)
        {
            for (var slot = 0; slot < bag.Size; slot++)
            {
                // SaveTo below preserves an unloaded record displaced by a live object in overflow.
                // Do not also reinsert that same record through Records(bag).
                bag.Unloaded[slot] = live.Inventory.Bag[slot] is null ? live.Inventory.Bag.Unloaded[slot] : null;
                if (live.Inventory.Bag[slot] is { } item)
                    bag.Put(slot, new Item(item.Id, item.Template, item.Count, item.Quality, item.DurabilityRaw, item.Active, item.Passive, item.Source)
                        { JournalFame = item.JournalFame, Soul = item.Soul });
            }
            // Capture current equipment, overflow and open vaults along with the detached bag.
            live.Inventory.SaveTo(snapshot, id => world.Spells?.Name(id));
            foreach (var vault in live.OpenVaults.Values)
                snapshot.Vaults[vault.Key] = Inventory.Records(vault.Container, id => world.Spells?.Name(id));
        }
        else
            for (var slot = 0; slot < Math.Min(snapshot.Inventory.Count, bag.Size); slot++)
            {
                if (snapshot.Inventory[slot] is not { } saved) continue;
                if (world.Items.Find(saved.Name) is { CanCreate: true } type) bag.Put(slot, FromRecord(type, saved));
                else bag.Unloaded[slot] = saved;
            }
        var contents = items.Select((i, index) => (Type: types[index]!, Count: i.Count)).ToArray();
        if (!WorldSession.Fits(bag, contents)) return false;
        var added = new List<Item>();
        foreach (var (type, count) in contents)
            WorldSession.PutItems(bag, type, count,
                saved => live is null ? FromRecord(type, saved) : live.Session.PrepareTradeItem(type, saved, live),
                added: (item, _) => added.Add(item));
        var overflow = snapshot.Inventory.Skip(Inventory.BagSlots).ToArray();
        var savedBag = Inventory.Records(bag, id => world.Spells?.Name(id));
        if (overflow.Length > 0)
        {
            while (savedBag.Count < Inventory.BagSlots) savedBag.Add(null);
            savedBag.AddRange(overflow);
        }
        snapshot.Inventory = savedBag;
        delivery = new(bag, added);
        return true;

        Item FromRecord(ItemTemplate type, InventoryItem saved) => new(world.ObjectIds.Next(), type, saved.Count,
            Math.Max(saved.Quality, 1), saved.Durability ?? type.FullDurabilityRaw, [], [], saved)
        {
            JournalFame = type.IsJournal ? Math.Clamp(saved.Fame ?? Item.DefaultFame(type), 0, type.Journal!.MaxFame) : 0,
            Soul = type.IsEquipment ? saved.Soul : null,
        };
    }

    /// <summary>Install all live changes before publishing any event; the receipt and bag have already committed.</summary>
    private Action InstallItems(Player player, ItemDelivery? delivery)
    {
        if (delivery is null) return () => { };
        var changed = new List<Item>();
        var added = new List<(Item Item, int Slot)>();
        var bag = player.Inventory.Bag;
        for (var slot = 0; slot < bag.Size; slot++)
        {
            if (delivery.Bag[slot] is not { } planned) continue;
            if (bag[slot] is { } current && current.Id == planned.Id)
            {
                if (current.Count == planned.Count) continue;
                current.Count = planned.Count;
                changed.Add(current);
            }
            else
            {
                bag.Put(slot, planned);
                added.Add((planned, slot));
            }
        }
        player.SaveItems();
        return () =>
        {
            foreach (var item in changed) player.Session.SendItem(item);
            foreach (var (item, slot) in added)
            {
                player.Session.SendItem(item);
                player.Session.Send(new InventoryPutItemEvent { ckw = item.Id, ckx = slot, cky = bag.Id });
            }
            foreach (var item in delivery.Added) world.Scripts.OnItemAdded(player, item);
            player.SaveItems();
        };
    }
}
