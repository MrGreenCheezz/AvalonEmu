using AlbionLocal.Photon;
using AlbionLocal.Server.Entities;
using AlbionLocal.Server.Game;
using AlbionLocal.Server.Protocol;

namespace AlbionLocal.Server.Systems;

/// <summary>
/// Overcharge (stageR items SPEC §2): the HUD's Overcharge button (OverChargeButton, cyt.u 0x180A05990) sends OverCharge (355) {0 the
/// slot mask the player enabled (option OverChargeSlotsEnabledFlags)}; every enabled combat slot (0 main hand … 4 shoes) whose item
/// has canbeovercharged="true" gets the next stack of gamedata OverCharge: the first costs its overchargeitemcount tokens
/// (UNIQUE_GVGTOKEN_GENERIC, 10), the second extends to its duration from the first activation for nothing. While it lasts the item
/// has +bonus item power (100) in every stat and damage formula (<see cref="Item.OverchargeBonus"/>). It ends at its time, by
/// OverChargeEnd (356) {0 slot mask} (the client sends it before it unequips an overcharged item, c2e.q 0x180A3D390), or when the
/// item leaves its slot: the item then breaks with the stack's trashchance (15 % / 30 %; deleted) or loses durabilityloss × its full
/// durability [infer]. Events: OverChargeStatus (369) {0 player, 1 the overcharged slot mask (+0x40000000 when a slot is at a second
/// stack, as c2e.n 0x180A3C6C0 builds it), 2 GameTimeStamp[5] the end per slot (MinValue = none), 3 byte[5] the stack per slot},
/// sent at every Join right before JoinFinished (as captured, all empty) and after each change; OverChargeEnd (368) {0 player,
/// 1 slot mask, 2 reason}: the client clears those slots (cyt.aft 0x180A056F0) and plays the break effect for reason 3 (cpx.awt)
/// [infer: 3 = broken; 1 for the others, assumed].
/// </summary>
public static class Overcharge
{
    /// <summary>The overcharged item per slot of an online player (object ids are new at every Join, so this is not saved).</summary>
    private static readonly System.Runtime.CompilerServices.ConditionalWeakTable<Player, Dictionary<int, Item>> Worn = new();

    /// <summary>The overchargeable slots: the five combat slots (the official status arrays have 5 entries).</summary>
    public const int Slots = 5;

    /// <summary>OverChargeEnd key 2 for a broken item (the client plays the break effect for it).</summary>
    public const int ReasonBroken = 3;

    /// <summary>OverChargeEnd key 2 otherwise [assume].</summary>
    public const int ReasonEnded = 1;

    /// <summary>Whether <paramref name="item"/> may be overcharged: items.xml canbeovercharged="true" (absent counts as no [assume]).</summary>
    public static bool Allowed(Item item) => item.Template.Attrs.GetValueOrDefault("canbeovercharged") == "true";

    /// <summary>
    /// OverCharge (355): the next stack on every slot of <paramref name="mask"/> that holds an item that may be overcharged. Returns why
    /// nothing happened, or null when the slots were overcharged (the status is sent either way).
    /// </summary>
    public static string? Start(Player p, int mask)
    {
        var settings = p.World.Economy.Overcharge;
        var now = p.World.GameNow;
        var slots = Enumerable.Range(0, Slots).Where(s => (mask >> s & 1) != 0 && p.Inventory.Equipment[s] is { } i && Allowed(i)).ToList();
        string? why = null;
        if (slots.Count == 0) why = "no enabled slot holds an item that can be overcharged";
        else
        {
            var current = p.Character.Overcharge;
            // c2e.r 0x180A3D4A0: the lowest stack among the chosen slots + 1, 0 when none of them is overcharged, none past the last.
            var stacks = slots.Select(s => current.FirstOrDefault(o => o.Slot == s)?.Stack ?? -1).ToList();
            var next = stacks.Min() + 1;
            if (next >= settings.Stacks.Count) why = "every chosen slot is at the last stack";
            else
            {
                var stack = settings.Stacks[next];
                var token = stack.ItemCount > 0 ? p.World.Items.Find(settings.Item) : null;
                if (stack.ItemCount > 0 && (token is null || Network.WorldSession.CountOf(p.Inventory.Bag, token) < stack.ItemCount))
                    why = $"{stack.ItemCount} x {settings.Item} needed";
                else
                {
                    if (token is not null) p.Session.TakeItems(p, token, stack.ItemCount);
                    var list = current.ToList();
                    foreach (var s in slots)
                    {
                        var old = list.FirstOrDefault(o => o.Slot == s);
                        if (old is not null && old.Stack >= next) continue;
                        // A first stack runs its duration from now; an extension to its duration counted from the first activation.
                        var end = old is null ? now + TimeSpan.FromSeconds(stack.Duration).Ticks
                            : old.End + TimeSpan.FromSeconds(stack.Duration - settings.Stacks[old.Stack].Duration).Ticks;
                        list.RemoveAll(o => o.Slot == s);
                        list.Add(new OverchargeSlot(s, next, end));
                        var item = p.Inventory.Equipment[s]!;
                        item.OverchargeBonus = (float)stack.Bonus;
                        Worn.GetOrCreateValue(p)[s] = item;
                    }
                    p.Character.Overcharge = [.. list.OrderBy(o => o.Slot)];
                    p.World.Saves.MarkChanged(p.Character);
                    p.SaveItems();
                    Log.Info("overcharge", $"'{p.Character.Name}' overcharges slots {string.Join(", ", slots)} at stack {next}");
                    Changed(p, now);
                }
            }
        }
        p.Session.Send(Status(p));
        return why;
    }

    /// <summary>OverChargeEnd (356) or a slot whose item left: ends the slots of <paramref name="mask"/> that are overcharged, with their break roll.</summary>
    public static void End(Player p, int mask)
    {
        var ending = p.Character.Overcharge.Where(o => (mask >> o.Slot & 1) != 0).ToList();
        if (ending.Count == 0) return;
        Finish(p, ending, p.World.GameNow);
    }

    /// <summary>
    /// Each tick for an online player with overcharged slots: the slots whose time is up, or whose item is gone from the slot
    /// (<see cref="Item.OverchargeBonus"/> 0 marks a new item there), end.
    /// </summary>
    public static void Update(Player p)
    {
        var list = p.Character.Overcharge;
        if (list.Count == 0) return;
        var now = p.World.GameNow;
        var worn = Worn.GetOrCreateValue(p);
        var ending = list.Where(o => o.End <= now || !worn.TryGetValue(o.Slot, out var i) || p.Inventory.Equipment[o.Slot] != i).ToList();
        if (ending.Count > 0) Finish(p, ending, now);
    }

    /// <summary>At a Join: the overcharged slots' items get their bonus back (a slot emptied meanwhile is ended at the next tick).</summary>
    public static void Joined(Player p)
    {
        var stacks = p.World.Economy.Overcharge.Stacks;
        foreach (var o in p.Character.Overcharge)
            if (p.Inventory.Equipment[o.Slot] is { } item && o.Stack < stacks.Count)
            {
                item.OverchargeBonus = (float)stacks[o.Stack].Bonus;
                Worn.GetOrCreateValue(p)[o.Slot] = item;
            }
        if (p.Character.Overcharge.Count > 0) p.RecalculateStats(p.World.GameNow);
    }

    private static void Finish(Player p, List<OverchargeSlot> ending, long now)
    {
        var settings = p.World.Economy.Overcharge;
        int broken = 0, ended = 0;
        var unequipped = false;
        foreach (var o in ending)
        {
            var worn = Worn.GetOrCreateValue(p);
            var tracked = worn.GetValueOrDefault(o.Slot);
            worn.Remove(o.Slot);
            if (tracked is not null) tracked.OverchargeBonus = 0;
            // The roll applies where the player still has the item (worn, or just moved to the bag); gone elsewhere: it only ends.
            var item = tracked is not null && p.Inventory.FindItem(tracked.Id) == tracked ? tracked : null;
            var stack = settings.Stacks[Math.Clamp(o.Stack, 0, settings.Stacks.Count - 1)];
            if (item is null) { ended |= 1 << o.Slot; continue; }
            if (p.World.CraftRandom.NextDouble() < stack.TrashChance)
            {
                var container = p.Inventory.IsEquipped(item) ? p.Inventory.Equipment : p.Inventory.Bag;
                container.Take(container.IndexOf(item));
                p.Session.Send(new InventoryDeleteItemEvent { ckr = item.Id });
                if (container == p.Inventory.Equipment)
                {
                    p.World.Scripts.OnUnequip(p, item);
                    unequipped = true;
                }
                broken |= 1 << o.Slot;
                Log.Info("overcharge", $"'{p.Character.Name}': the overcharged {item.Template.Name} broke");
            }
            else
            {
                if (item.Template.IsEquipment && item.Template.MaxDurability > 0)
                {
                    item.DurabilityRaw = Math.Max(0, item.DurabilityRaw - (long)(stack.DurabilityLoss * item.Template.FullDurabilityRaw));
                    Network.WorldSession.SendItemTo(p, item);
                }
                ended |= 1 << o.Slot;
            }
        }
        p.Character.Overcharge = [.. p.Character.Overcharge.Where(o => !ending.Contains(o))];
        p.World.Saves.MarkChanged(p.Character);
        if (ended != 0) p.Session.Send(new OverChargeEndEvent { ddn = p.Id, dfh = ended, dfi = ReasonEnded });
        if (broken != 0) p.Session.Send(new OverChargeEndEvent { ddn = p.Id, dfh = broken, dfi = ReasonBroken });
        p.Session.Send(Status(p));
        if (unequipped) p.Session.SendEquipmentChanged(p, intoEquipment: false);
        else Changed(p, now);
        p.SaveItems();
    }

    private static void Changed(Player p, long now)
    {
        if (p.RecalculateStats(now)) p.SendAround(Combat.PlayerCombo(p, now));
    }

    /// <summary>OverChargeStatus (369) of <paramref name="p"/>'s slots (see the class).</summary>
    public static OverChargeStatusEvent Status(Player p)
    {
        var ends = Enumerable.Repeat(long.MinValue, Slots).ToArray();
        var stacks = new byte[Slots];
        var flags = 0;
        foreach (var o in p.Character.Overcharge.Where(o => o.Slot is >= 0 and < Slots))
        {
            ends[o.Slot] = o.End;
            stacks[o.Slot] = (byte)o.Stack;
            flags |= 1 << o.Slot;
            if (o.Stack > 0) flags |= 0x40000000;
        }
        return new OverChargeStatusEvent { ddn = p.Id, dfj = flags, dfk = ends, dfl = stacks };
    }
}
