using AlbionLocal.Photon;
using AlbionLocal.Plugin.Api;
using AlbionLocal.Server.Entities;
using AlbionLocal.Server.Protocol;

namespace AlbionLocal.Server.Systems;

/// <summary>
/// The spell in a slot of an equipped item, changed by the server (plugins' <see cref="IPlayer.SetSlotSpell"/>, the rules' slot.set, GM .slot;
/// analysis/work/stageN/plugins/SPEC.md §10). The choice is the item's own (<see cref="Item.Active"/> / <see cref="Item.Passive"/>, the same arrays the
/// player's EquipmentItemChangeSpell sets), so the cast check (<see cref="SpellCasts.Check"/> against CharacterEquipmentChanged key 7), the passives
/// (<see cref="Passives.Of"/>: the item's passive ids are in its signature) and the save follow by themselves; potion and food have no choice and use
/// <see cref="Item.ConsumeOverride"/>.
/// <para>The client (decompiled, 2026-10-19): NewEquipmentItem keys 9/10 go through c1r.af 0x180A1FA00 → c1r.af 0x180A20170 (an active id looked up
/// with be7.abp in the whole spell table and kept when it is an active spell) and be7.abq for the passives (bounded only by the item's passive slot
/// count); CharacterEquipmentChanged key 7 goes through crr.aft 0x180BE90E0 → cp0.x8 0x180BDAB90 (be7.abp per CharacterSpellSlot into the action bar's
/// spell array). None of them compares the id with the item's spell list, so a spell from outside the list (<see cref="SlotSpellMode.Any"/>) is shown
/// and cast by its id; only the item's spell window has none of its options marked as chosen.</para>
/// A temporary change keeps the previous choice in <see cref="Item.Temporary"/> (what the save writes meanwhile) and comes back after its duration
/// on a map timer, unless the item left the slot, the player left the map (the next Join builds the item from the save: the previous choice) or another
/// change of that slot came first. World thread only.
/// </summary>
public static class SlotSpells
{
    /// <summary>Where a slot's spell lives: the equipment slot, active or passive, the index in the item's array (-1 = the consumable override), the CharacterSpellSlot.</summary>
    public readonly record struct Place(int Equipment, bool Active, int Index, byte? CastSlot);

    /// <summary>The slot names of the GM command and the rules (case-insensitive), with their aliases.</summary>
    public static readonly (string Name, SpellSlot Slot)[] Names =
    [
        ("Q", SpellSlot.Q), ("W", SpellSlot.W), ("E", SpellSlot.E), ("R", SpellSlot.Armor), ("D", SpellSlot.Head), ("F", SpellSlot.Shoes),
        ("Potion", SpellSlot.Potion), ("Food", SpellSlot.Food),
        ("WeaponPassive", SpellSlot.WeaponPassive), ("ArmorPassive", SpellSlot.ArmorPassive), ("ArmorPassive2", SpellSlot.ArmorPassive2),
        ("HeadPassive", SpellSlot.HeadPassive), ("ShoesPassive", SpellSlot.ShoesPassive), ("CapePassive", SpellSlot.CapePassive),
        ("BagPassive", SpellSlot.BagPassive), ("MountPassive", SpellSlot.MountPassive), ("MountPassive2", SpellSlot.MountPassive2),
        // aliases
        ("Armor", SpellSlot.Armor), ("Head", SpellSlot.Head), ("Shoes", SpellSlot.Shoes), ("Passive", SpellSlot.WeaponPassive),
    ];

    /// <summary>The canonical names (the rules' select options, .slot list).</summary>
    public static readonly string[] SlotOptions = [.. Names.Take(17).Select(n => n.Name)];

    public static SpellSlot? Parse(string? name) =>
        Names.FirstOrDefault(n => string.Equals(n.Name, name?.Trim(), StringComparison.OrdinalIgnoreCase)) is { Name: not null } hit ? hit.Slot : null;

    public static string NameOf(SpellSlot slot) => Names.FirstOrDefault(n => n.Slot == slot).Name ?? slot.ToString();

    /// <summary>
    /// Where <paramref name="slot"/> lives for <paramref name="p"/> now (<see cref="Network.WorldSession.SpellSlots"/>: E is the main hand's third spell, or
    /// the off hand's first when the main hand has fewer than three); null for <see cref="SpellSlot.Other"/>.
    /// </summary>
    public static Place? Locate(Player p, SpellSlot slot) => slot switch
    {
        SpellSlot.Q => new(0, true, 0, 0),
        SpellSlot.W => new(0, true, 1, 1),
        SpellSlot.E => p.Inventory.Equipment[0] is { Active.Length: >= 3 } ? new(0, true, 2, 2) : new(1, true, 0, 2),
        SpellSlot.Armor => new(3, true, 0, 3),
        SpellSlot.Head => new(2, true, 0, 4),
        SpellSlot.Shoes => new(4, true, 0, 5),
        SpellSlot.Potion => new(8, true, -1, Consumables.PotionSlot),
        SpellSlot.Food => new(9, true, -1, Consumables.FoodSlot),
        SpellSlot.WeaponPassive => new(0, false, 0, null),
        SpellSlot.ArmorPassive => new(3, false, 0, null),
        SpellSlot.ArmorPassive2 => new(3, false, 1, null),
        SpellSlot.HeadPassive => new(2, false, 0, null),
        SpellSlot.ShoesPassive => new(4, false, 0, null),
        SpellSlot.CapePassive => new(6, false, 0, null),
        SpellSlot.BagPassive => new(5, false, 0, null),
        SpellSlot.MountPassive => new(7, false, 0, null),
        SpellSlot.MountPassive2 => new(7, false, 1, null),
        _ => null,
    };

    /// <summary>The <see cref="SpellSlot"/> of an item's active/passive index in equipment slot <paramref name="equipment"/> (the player's own choice); null for none.</summary>
    public static SpellSlot? SlotOf(Player p, int equipment, bool active, int index)
    {
        foreach (var (_, slot) in Names)
            if (Locate(p, slot) is { } at && at.Equipment == equipment && at.Active == active && at.Index == index) return slot;
        return null;
    }

    /// <summary>The spell id in <paramref name="slot"/> now; -1 = none.</summary>
    public static short Current(Player p, SpellSlot slot)
    {
        if (Locate(p, slot) is not { } at || p.Inventory.Equipment[at.Equipment] is not { } item) return -1;
        if (at.Index < 0) return item.ConsumeOverride ?? p.World.Spells?.ConsumeSpell(item.Template.Name) ?? -1;
        var ids = at.Active ? item.Active : item.Passive;
        return at.Index < ids.Length ? ids[at.Index] : (short)-1;
    }

    /// <summary>The spell ids the equipped item offers for <paramref name="slot"/>; a potion or food: its consumespell.</summary>
    public static IReadOnlyList<short> Options(Player p, SpellSlot slot)
    {
        if (Locate(p, slot) is not { } at || p.Inventory.Equipment[at.Equipment] is not { } item || p.World.Spells is not { } spells) return [];
        if (at.Index < 0) return spells.ConsumeSpell(item.Template.Name) is var id and >= 0 ? [(short)id] : [];
        if (spells.SlotsOf(item.Template.Name) is not { } lists) return [];
        var all = at.Active ? lists.Active : lists.Passive;
        return at.Index < all.Length ? all[at.Index] : [];
    }

    /// <summary>
    /// Puts spell <paramref name="spellName"/> into <paramref name="slot"/> (<see cref="IPlayer.SetSlotSpell"/>). Null when done (also when it was there
    /// already and no duration was asked), else why not.
    /// </summary>
    public static string? Set(Player p, SpellSlot slot, string spellName, SlotSpellMode mode, TimeSpan? duration, SlotSpellCooldown cooldown)
    {
        var world = p.World;
        if (world.Spells is not { } spells) return "no spell data";
        if (p.Map is not { } map) return "the player is not in the world";
        if (Locate(p, slot) is not { } at) return $"{slot} is not a slot that holds a spell";
        if (p.Inventory.Equipment[at.Equipment] is not { } item) return $"nothing is equipped for slot {NameOf(slot)} ({Inventory.SlotNames[at.Equipment]})";
        var id = spells.IdOf(spellName?.Trim());
        if (id < 0) return $"unknown spell '{spellName}'";
        if (at.Active ? !spells.IsActive(id) : !spells.IsPassive(id))
            return $"{spells.Name(id)} is not {(at.Active ? "an active" : "a passive")} spell (slot {NameOf(slot)} takes {(at.Active ? "active" : "passive")} spells)";
        if (duration is { } d && (d <= TimeSpan.Zero || d > TimeSpan.FromDays(1))) return "the duration must be above 0 and at most a day";
        if (at.Index >= 0 && at.Index >= (at.Active ? item.Active : item.Passive).Length)
            return $"'{item.Template.Name}' has no {(at.Active ? "active" : "passive")} slot {at.Index + 1}";
        if (mode == SlotSpellMode.FromItemList && !Options(p, slot).Contains(id))
            return $"{spells.Name(id)} is not an option of '{item.Template.Name}' for slot {NameOf(slot)} (options: {string.Join(", ", Options(p, slot).Select(o => spells.Name(o)))}; mode Any takes any spell)";

        var before = Current(p, slot);
        var key = (at.Active, at.Index);
        if (before == id && duration is null)
        {
            item.Temporary?.Remove(key); // asked for for good: what was temporary stays
            return null;
        }
        // What comes back after a temporary change: the choice saved by an earlier temporary one still running, else the choice now
        // (for a potion or food its override, -1 = the consumespell).
        var saved = item.Temporary?.TryGetValue(key, out var old) == true ? old.Saved : at.Index < 0 ? (short)(item.ConsumeOverride ?? -1) : before;
        var token = Write(item, at, id);
        if (duration is { } span)
        {
            (item.Temporary ??= [])[key] = (saved, token);
            map.Timers.At(world.Clock.Now + span, () => Revert(p, map, item, slot, at, token));
        }
        else item.Temporary?.Remove(key);
        Cooldowns(p, at, before, id, cooldown);
        Changed(p, item, at, slot, before, id, SlotChangeSource.Plugin, duration is not null);
        return null;
    }

    private static int _tokens;

    /// <summary>Puts <paramref name="id"/> into the place; returns a new token (the revert timer of a temporary change checks it).</summary>
    private static int Write(Item item, Place at, short id)
    {
        if (at.Index < 0) item.ConsumeOverride = id;
        else (at.Active ? item.Active : item.Passive)[at.Index] = id;
        return Interlocked.Increment(ref _tokens);
    }

    /// <summary>A temporary change ran out: the saved choice comes back, if nothing else changed the slot meanwhile.</summary>
    private static void Revert(Player p, Map map, Item item, SpellSlot slot, Place at, int token)
    {
        var key = (at.Active, at.Index);
        if (p.Map != map || p.Inventory.Equipment[at.Equipment] != item || item.Temporary?.TryGetValue(key, out var entry) != true || entry.Token != token) return;
        item.Temporary.Remove(key);
        var before = Current(p, slot);
        if (at.Index < 0) item.ConsumeOverride = entry.Saved >= 0 ? entry.Saved : null;
        else (at.Active ? item.Active : item.Passive)[at.Index] = entry.Saved;
        Changed(p, item, at, slot, before, Current(p, slot), SlotChangeSource.Revert, false);
    }

    /// <summary>The player chose a spell in an equipped item's window (EquipmentItemChangeSpell, after its own events): OnSlotSpellChanged.</summary>
    public static void PlayerChose(Player p, Item item, bool active, int index, short before)
    {
        var e = p.Inventory.Equipment;
        for (var s = 0; s < Inventory.EquipmentSlots; s++)
            if (e[s] == item && SlotOf(p, s, active, index) is { } slot)
            {
                var spells = p.World.Spells;
                p.World.Plugins.SlotSpellChanged(p, slot, item, spells?.Name(before), spells?.Name(Current(p, slot)), SlotChangeSource.Player, false);
                return;
            }
    }

    /// <summary>
    /// The cooldowns at the change (<see cref="SlotSpellCooldown"/>), then the slot's cooldown state for the new spell to the client (SpellCooldownUpdate, as
    /// <see cref="Plugins.GameplayFacade"/> sends it): the client keeps cooldowns per slot, so the old spell's cooldown must not stay on it.
    /// </summary>
    private static void Cooldowns(Player p, Place at, short before, short id, SlotSpellCooldown mode)
    {
        var now = p.World.GameNow;
        var cooldowns = p.Combat.Cooldowns;
        switch (mode)
        {
            case SlotSpellCooldown.Carry when before >= 0 && cooldowns.TryGetValue(before, out var end) && end > now:
                cooldowns[id] = Math.Max(cooldowns.GetValueOrDefault(id), end);
                break;
            case SlotSpellCooldown.Reset:
                cooldowns.Remove(id);
                break;
        }
        if (at.CastSlot is not { } slot || at.Index < 0 || p.World.Spells?.Template(id) is not { } t) return; // potion/food keep their slot cooldown
        var left = Math.Max(0, cooldowns.GetValueOrDefault(id) - now);
        var span = Math.Max(SpellCasts.Seconds(t.RecastDelay), left);
        if (span <= 0) return;
        p.Session.Send(new SpellCooldownUpdateEvent { dy7 = [slot], dy8 = [now + left], dy9 = [now + left - span], dza = [span], dzb = [0] });
    }

    /// <summary>After a change: the item and the action bar to the client, the stats and passives anew, the save, OnSlotSpellChanged.</summary>
    private static void Changed(Player p, Item item, Place at, SpellSlot slot, short before, short now, SlotChangeSource source, bool temporary)
    {
        var world = p.World;
        p.Session.SendSlotSpellChange(p, item);
        if (!at.Active) SpellEffects.Refresh(world, p); // the passive set's signature changed: worked out again with the new id
        p.SaveItems();
        var spells = world.Spells;
        Log.Info("items", $"'{p.Character.Name}' slot {NameOf(slot)} ({item.Template.Name}): {spells?.Name(before) ?? "-"} → {spells?.Name(now) ?? "-"} ({source}{(temporary ? ", temporary" : "")})");
        world.Plugins.SlotSpellChanged(p, slot, item, spells?.Name(before), spells?.Name(now), source, temporary);
    }
}
