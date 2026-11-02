using System.Globalization;
using AlbionLocal.Photon;
using AlbionLocal.Server.Data;
using AlbionLocal.Server.Entities;
using AlbionLocal.Server.Game;
using AlbionLocal.Server.Network;
using AlbionLocal.Server.Protocol;

namespace AlbionLocal.Server.Systems;

public sealed partial class Shop
{
    internal sealed record GoldCraftResult(ShopResult Purchase, CraftError Error, IReadOnlyList<long> ItemIds, Action Publish);

    /// <summary>All20 authored gold recipes in345319 have time0 and no secondary currency. Commit their account
    /// debit, resource consumption, normal item placement and completed receipt through the existing SaveShop transaction.
    /// Preparation runs after the save barrier; no live item, wallet event or item hook changes before that commit.</summary>
    internal GoldCraftResult CraftGold(Player player, ItemTemplate item, int recipeIndex, int amount, long quote, string receipt)
    {
        CraftError error = 0;
        Inventory? staged = null;
        ShopAccount? committed = null;
        var added = new List<Item>();
        var made = new List<Item>();
        ShopResult Refuse(CraftError code, string message) { error = code; return new(false, message); }
        var result = Attempt(() =>
        {
            if (!Allowed(player.Character.Account) || player.Character.Transient || player.Session.Player != player
                || player.Map is null || player.Combat.Down is not null || !ValidKey(receipt))
                return Refuse(0, "La compra con oro no está disponible ahora.");
            if (recipeIndex < 0 || recipeIndex >= item.Recipes.Count || amount is <= 0 or > 9999 || !item.CanCreate)
                return Refuse(0, "La receta o la cantidad no son válidas.");
            var recipe = item.Recipes[recipeIndex];
            if (!double.IsFinite(recipe.Gold) || recipe.Gold <= 0 || recipe.Silver != 0 || recipe.Time > 0
                || recipe.Focus != 0 || recipe.Unsupported.Count != 0)
                return Refuse(0, "Esta receta con oro no usa el contrato de compra instantánea.");
            if (recipe.AmountCrafted <= 0 || amount % recipe.AmountCrafted != 0
                || recipe.ForceSingleCraft && amount != recipe.AmountCrafted)
                return Refuse(CraftError.InvalidAmount, "La cantidad debe respetar el tamaño del grupo de esta receta.");
            var operations = amount / recipe.AmountCrafted;
            // Native1576B0A–3E prices the final quantity, not the number of groups. The current native fireworks
            // selector has minimum5 and quotes25 gold for5 Blue units (recipe.gold5, AmountCrafted5).
            var price = checked((decimal)recipe.Gold * 10000m * amount);
            if (price != decimal.Truncate(price) || price > long.MaxValue || quote != (long)price)
                return Refuse(CraftError.PriceChanged, "El precio con oro cambió. Vuelve a seleccionar la receta.");
            var output = amount;
            var product = string.Create(CultureInfo.InvariantCulture, $"goldcraft.{item.Index}.{recipeIndex}.{amount}");
            var state = State(player.Character.Account).Clone();
            if (state.Orders.FirstOrDefault(o => o.Id == receipt) is { } previous)
            {
                if (previous.Status != "completed" || previous.ProductId != product || previous.Character != player.Character.Id
                    || previous.Currency != "gold" || previous.Price != quote
                    || !previous.Items.SequenceEqual([new ShopItem(item.Name, output)]))
                    return Refuse(0, "Este identificador ya corresponde a otra compra.");
                return new(true, "La compra ya está completada.", previous);
            }
            if (state.Gold < quote) return Refuse(CraftError.NotEnoughGold, "No tienes suficiente oro.");

            var inventory = GoldCraftInventory(player);
            var needs = new Dictionary<ItemTemplate, int>();
            foreach (var resource in recipe.Resources)
            {
                var name = resource.Enchantment > 0 ? $"{resource.Name}@{resource.Enchantment}" : resource.Name;
                if (world.Items.Find(name) is not { CanCreate: true } type || resource.Count <= 0)
                    return Refuse(CraftError.NotEnoughResources, "La receta contiene un recurso no disponible.");
                needs[type] = checked(needs.GetValueOrDefault(type) + resource.Count * operations);
            }
            foreach (var (type, count) in needs)
            {
                if (WorldSession.CountOf(inventory.Bag, type) < count)
                    return Refuse(CraftError.NotEnoughResources, $"No tienes suficientes recursos: {type.Name}.");
                var left = count;
                for (var slot = inventory.Bag.Size - 1; slot >= 0 && left > 0; slot--)
                {
                    if (inventory.Bag[slot] is not { } stack || stack.Template != type) continue;
                    var take = Math.Min(left, stack.Count);
                    stack.Count -= take;
                    left -= take;
                    if (stack.Count == 0) inventory.Bag.Take(slot);
                }
            }
            if (!WorldSession.Fits(inventory.Bag, [(item, output)]))
                return Refuse(CraftError.InventoryFull, "No hay espacio para el resultado. No se ha cobrado oro.");
            made = WorldSession.PutItems(inventory.Bag, item, output,
                saved => player.Session.PrepareTradeItem(item, saved, player), added: (created, _) => added.Add(created));
            Systems.Consumables.Store(player);
            var snapshot = player.Character.Clone();
            // SaveTo preserves every unknown/displaced entry and overflow. Vaults remain the untouched clone;
            // rebuilding open bank records here would make this purchase modify unrelated containers.
            inventory.SaveTo(snapshot, id => world.Spells?.Name(id));
            state.Gold = checked(state.Gold - quote);
            snapshot.Gold = state.Gold;
            var order = new ShopOrder
            {
                Id = receipt, Account = state.Account, Character = snapshot.Id, ProductId = product, Title = item.Name,
                Currency = "gold", Price = quote, Items = [new ShopItem(item.Name, output)], Status = "completed",
                CreatedAt = world.GameNow, ExpiresAt = world.GameNow, CompletedAt = world.GameNow,
            };
            state.Orders.Add(order);
            Write(state, snapshot);
            staged = inventory;
            committed = state;
            return new(true, "Compra con oro completada.", order);
        });
        if (!result.Success) return new(result, error, [], () => { });
        if (staged is null || committed is null) return new(result, 0, [], () => { }); // Durable replay.

        // Install the complete committed bag and every live account mirror before any packet can fail.
        var bag = player.Inventory.Bag;
        var removed = new List<Item>();
        var changed = new List<Item>();
        var placed = new List<(Item Item, int Slot)>();
        for (var slot = 0; slot < bag.Size; slot++)
        {
            var planned = staged.Bag[slot];
            if (bag[slot] is { } current && (planned is null || current.Id != planned.Id))
            {
                removed.Add(current);
                bag.Take(slot);
            }
            if (planned is null) continue;
            if (bag[slot] is { } retained)
            {
                if (retained.Count == planned.Count) continue;
                retained.Count = planned.Count;
                changed.Add(retained);
            }
            else { bag.Put(slot, planned); placed.Add((planned, slot)); }
        }
        foreach (var p in world.Maps.Maps.SelectMany(m => m.Players.Values)
            .Where(p => AccountId(p.Character.Account) == committed.Account && !p.Character.Transient))
            p.Character.Gold = committed.Gold;
        player.Inventory.SaveTo(player.Character, id => world.Spells?.Name(id));
        world.Saves.MarkChanged(player.Character);
        return new(result, 0, made.Select(i => i.Id).Distinct().ToArray(), () =>
        {
            Publish(committed, []);
            foreach (var removedItem in removed) player.Session.Send(new InventoryDeleteItemEvent { ckr = removedItem.Id });
            foreach (var changedItem in changed) player.Session.SendItem(changedItem);
            foreach (var (placedItem, slot) in placed)
            {
                player.Session.SendItem(placedItem);
                player.Session.Send(new InventoryPutItemEvent { ckw = placedItem.Id, ckx = slot, cky = bag.Id });
            }
            foreach (var addedItem in added) world.Scripts.OnItemAdded(player, addedItem);
            Log.Info("shop", $"'{player.Character.Name}' gold-crafted {result.Order!.Items[0].Count} x {item.Name}, paid {quote} a7m gold");
        });
    }

    private static Inventory GoldCraftInventory(Player player)
    {
        var source = player.Inventory;
        var copy = new Inventory(source.Equipment.Id, source.Bag.Id);
        foreach (var (from, to) in new[] { (source.Equipment, copy.Equipment), (source.Bag, copy.Bag) })
            for (var slot = 0; slot < from.Size; slot++)
            {
                to.Unloaded[slot] = from.Unloaded[slot];
                if (from[slot] is { } item)
                    to.Put(slot, new Item(item.Id, item.Template, item.Count, item.Quality, item.DurabilityRaw,
                        item.Active, item.Passive, item.Source) { JournalFame = item.JournalFame, Soul = item.Soul });
            }
        copy.Overflow.AddRange(source.Overflow);
        return copy;
    }
}
