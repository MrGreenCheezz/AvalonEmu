using System.Globalization;
using AlbionLocal.Photon;
using AlbionLocal.Server.Data;
using AlbionLocal.Server.Entities;
using AlbionLocal.Server.Protocol;
using ConsumableSystem = AlbionLocal.Server.Systems.Consumables;

namespace AlbionLocal.Server.Systems;

public sealed partial class Shop
{
    internal static bool IsVanityChargeReward(SpellTemplate spell) =>
        spell.Effects.Any(e => e.Name.LocalName == "addvanityconsumablecharges");

    internal bool CanRedeemVanityChargeItem(SpellTemplate spell) =>
        HasOwnershipStore && TryVanityChargeReward(spell, out _);

    /// <summary>Resolve the entire authored reward before consuming anything. The 10 current 345319 spells award
    /// one KillEmote charge each; their 20 tradable/nontradable items share these spells. Shop bundle size is unrelated.</summary>
    private bool TryVanityChargeReward(SpellTemplate spell, out Dictionary<short, int> charges)
    {
        charges = [];
        try
        {
            foreach (var effect in spell.Effects)
            {
                if (effect.Name.LocalName == "nop") continue;
                if (effect.Name.LocalName != "addvanityconsumablecharges" || (string?)effect.Attribute("target") != "self"
                    || (string?)effect.Attribute("type") != "KillEmote"
                    || !int.TryParse((string?)effect.Attribute("amount"), NumberStyles.Integer, CultureInfo.InvariantCulture, out var amount)
                    || amount <= 0) return false;
                var name = (string?)effect.Attribute("uniquename");
                if (KillEmotes.All.FirstOrDefault(e => string.Equals(e.Name, name, StringComparison.Ordinal)) is not { } emote) return false;
                charges[emote.Id] = checked(charges.GetValueOrDefault(emote.Id) + amount);
            }
            return charges.Count > 0;
        }
        catch (OverflowException) { return false; }
    }

    /// <summary>One unit of a normal cancelable inventory batch: account charges and item decrement commit together.
    /// Earned items can be redeemed when shop purchases are disabled. Nothing spends gold or selects an emote.</summary>
    internal bool RedeemVanityChargeItem(Player p, Item item, SpellTemplate spell, bool inUse)
    {
        if (!HasOwnershipStore || !TryVanityChargeReward(spell, out var charges)) return false;
        var slot = -1;
        var result = Attempt(() =>
        {
            if (p.Character.Transient || p.Session.Player != p || p.Map is null || p.Combat.Down is not null
                || item.Count <= 0 || (slot = p.Inventory.Bag.IndexOf(item)) < 0
                || world.Characters.FindAccount(AccountId(p.Character.Account)) is not { } account
                || account.BannedUntil is { } until && until.Ticks > world.GameNow)
                return new(false, "Este objeto no puede canjearse ahora.");
            var state = State(p.Character.Account).Clone();
            ConsumableSystem.Store(p);
            var snapshot = p.Character.Clone();
            p.Inventory.SaveTo(snapshot, id => world.Spells?.Name(id));
            // Preserve the cloned Vaults verbatim; a cosmetic item never rewrites open bank containers.
            if (slot >= snapshot.Inventory.Count || snapshot.Inventory[slot] is not { } saved)
                return new(false, "El objeto ya no está en la mochila.");
            snapshot.Inventory[slot] = item.Count > 1 ? saved with { Count = item.Count - 1 } : null;
            foreach (var (id, amount) in charges)
                state.Consumables[id] = checked(state.Consumables.GetValueOrDefault(id) + amount);
            Write(state, snapshot);
            return new(true, "Cargas añadidas.");
        });
        if (!result.Success)
        {
            Log.Warn("items", $"'{p.Character.Name}' could not redeem {item.Template.Name}: {result.Message}");
            return false;
        }

        // Once durable, install the bag before presentation. A failed packet cannot turn a completed use into a retry.
        item.Count--;
        if (item.Count == 0) p.Inventory.Bag.Take(slot);
        p.Inventory.SaveTo(p.Character, id => world.Spells?.Name(id));
        world.Saves.MarkChanged(p.Character);
        try
        {
            var now = world.GameNow;
            var here = SpellMovement.PositionOf(p, now);
            p.SendAround(new CastSpellEvent
            {
                ddn = p.Id, bvw = p.Id, bvx = here, bvy = spell.Id, bvz = now, bv0 = now,
                Extra = new() { [6] = (byte)0, [7] = (short)0 },
            });
            if (item.Count > 0)
                p.Session.Send(new NewSimpleItemEvent { dax = item.Id, day = item.Template.Index, daz = (short)item.Count, da0 = inUse });
            else p.Session.Send(new InventoryDeleteItemEvent { ckr = item.Id });
            foreach (var (id, amount) in charges) SendConsumableDelta(AccountId(p.Character.Account), id, amount);
            p.SendAround(Combat.CastHit(p.Id, p.Id, spell.Id, 0));
        }
        catch (Exception e) { Log.Warn("items", $"vanity charges committed for '{p.Character.Name}'; presentation interrupted: {e.Message}"); }
        Log.Info("items", $"'{p.Character.Name}' redeemed 1 x {item.Template.Name}: {string.Join(", ", charges.Select(c => $"{c.Key}+{c.Value}"))} charges, {item.Count} items left");
        return true;
    }
}
