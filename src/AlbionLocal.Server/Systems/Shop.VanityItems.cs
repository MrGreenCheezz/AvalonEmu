using AlbionLocal.Server.Data;
using AlbionLocal.Server.Entities;
using AlbionLocal.Server.Game;
using AlbionLocal.Server.Protocol;

namespace AlbionLocal.Server.Systems;

public sealed partial class Shop
{
    // Earned account rights do not depend on whether the administrator enables purchases in shop.json.
    internal bool HasOwnershipStore => Store is not null;

    /// <summary>An original unlock item grants account rights, without charging money or creating an order.
    /// SaveShop commits the full character inventory and the account unlocks together after older saves drain.
    /// A storage failure, duplicate or unsupported backend leaves the item untouched.</summary>
    internal bool ConsumeVanityItem(Player p, Item item, SpellTemplate spell, IReadOnlyList<VanityCatalog.Unlock> unlocks)
    {
        if (!HasOwnershipStore || p.Character.Transient) return false;
        short[] added = [];
        var slot = -1;
        var result = Attempt(() =>
        {
            if (p.Map is null || p.Combat.Down is not null || p.Combat.Batch is not null || item.Count <= 0
                || (slot = p.Inventory.Bag.IndexOf(item)) < 0
                || world.Characters.FindAccount(AccountId(p.Character.Account)) is not { } account
                || account.BannedUntil is { } until && until.Ticks > world.GameNow)
                return new(false, "El objeto no se puede usar ahora.");
            var state = State(p.Character.Account).Clone();
            var owned = world.Vanity.Owned(p.Character);
            added = [.. unlocks.Where(u => !u.Free && !owned.Contains(u.Id)).Select(u => u.Id).Distinct()];
            if (added.Length == 0) return new(false, "La cuenta ya tiene estas apariencias.");
            var copy = VanitySnapshot(p);
            if (slot >= copy.Inventory.Count || copy.Inventory[slot] is not { } saved)
                return new(false, "El objeto ya no está en la mochila.");
            copy.Inventory[slot] = item.Count > 1 ? saved with { Count = item.Count - 1 } : null;
            state.Unlocks.UnionWith(added);
            Write(state, copy);
            return new(true, "Apariencias desbloqueadas.");
        });
        if (!result.Success) return false;

        // Install the committed inventory before any packet. Retrying the request finds the account already owns
        // the complete reward; transport failure after this point cannot turn a committed use into another use.
        item.Count--;
        if (item.Count == 0) p.Inventory.Bag.Take(slot);
        p.SaveItems();
        var now = world.GameNow;
        var here = SpellMovement.PositionOf(p, now);
        p.SendAround(new CastSpellEvent { ddn = p.Id, bvw = p.Id, bvx = here, bvy = spell.Id, bvz = now, bv0 = now,
            Extra = new() { [6] = (byte)0, [7] = (short)0 } });
        if (item.Count > 0) p.Session.Send(new NewSimpleItemEvent { dax = item.Id, day = item.Template.Index, daz = (short)item.Count });
        else p.Session.Send(new InventoryDeleteItemEvent { ckr = item.Id });
        PublishVanityUnlocks(p.Character.Account, added);
        p.SendAround(Combat.CastHit(p.Id, p.Id, spell.Id, 0));
        return true;
    }

    private Character VanitySnapshot(Player p)
    {
        global::AlbionLocal.Server.Systems.Consumables.Store(p);
        var copy = p.Character.Clone();
        p.Inventory.SaveTo(copy, id => world.Spells?.Name(id));
        foreach (var vault in p.OpenVaults.Values) copy.Vaults[vault.Key] = Inventory.Records(vault.Container, id => world.Spells?.Name(id));
        return copy;
    }

    internal void PublishVanityUnlocks(Guid account, IReadOnlyList<short> ids, bool owned = true)
    {
        account = AccountId(account);
        foreach (var p in world.Maps.Maps.SelectMany(m => m.Players.Values)
            .Where(p => !p.Character.Transient && AccountId(p.Character.Account) == account))
            foreach (var id in ids) p.Session.Send(new UnlockVanityUnlockEvent { d2f = owned, Extra = new() { [0] = id, [1] = owned } });
    }
}
