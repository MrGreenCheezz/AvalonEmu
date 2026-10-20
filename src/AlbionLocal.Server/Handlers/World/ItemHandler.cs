using AlbionLocal.Photon;
using AlbionLocal.Server.Data;
using AlbionLocal.Server.Entities;
using AlbionLocal.Server.Game;
using AlbionLocal.Server.Protocol;

namespace AlbionLocal.Server.Network;

/// <summary>Item objects and their spells: drag and drop between containers, spell choice, the equipment events.</summary>
public sealed partial class WorldSession
{
    /// <summary>
    /// NewEquipmentItem (event 30) with the full item: the client copies every field when it updates an
    /// existing object (cjb.bi 0x180aa8690), so a re-send after a spell change must carry everything.
    /// Keys 9/10 are never null (c1r.af 0x180a1fa00 iterates them) and always full length: the client only
    /// overwrites the indices present, so a short array would leave stale choices.
    /// </summary>
    private void SendItemObject(Item item)
    {
        SendEvent(ItemObject(item));
        if (item.Soul is not null) SendEvent(SoulEvent(item, Player?.Character.Id ?? Guid.Empty));
    }

    /// <summary>
    /// <paramref name="item"/> to its holder <paramref name="p"/>: NewEquipmentItem and, for an awakened weapon, its soul (event 37), as
    /// <see cref="SendItemObject"/> for the callers outside a session handler (death, wear).
    /// </summary>
    internal static void SendItemTo(Player p, Item item)
    {
        p.Session.Send(ItemObject(item));
        if (item.Soul is not null) p.Session.Send(SoulEvent(item, p.Character.Id));
    }

    /// <summary>
    /// NewEquipmentItemLegendarySoul (event 37, client class atm; stageN awakening SPEC §2): key 0 = the item object (c1r.ajv 0x180A203B0
    /// makes or updates the item's soul c2b, c2b.t 0x180A37A60 copies the keys), 1 soul Guid, 2 Name, 3 Era, 4 AttunedToMe (for
    /// <paramref name="viewer"/>), 5 AttunedPlayerName, 6 Strain (a7m), 7 Attunement (a7m), 8/9 the traits (uniquename, roll 0-1; "" = an
    /// empty slot), 10/11 per slot the offered replacements of a trait generation (empty arrays elsewhere), 12/13 a7m (not identified, 0).
    /// It must follow the NewEquipmentItem: that one with key 11 = 0 drops the soul (c1r.af 0x180A1FCA0).
    /// </summary>
    internal static NewEquipmentItemLegendarySoulEvent SoulEvent(Item item, Guid viewer)
    {
        var soul = item.Soul!;
        return new NewEquipmentItemLegendarySoulEvent
        {
            ddn = item.Id, cyw = soul.Id, cyx = soul.Name, cyy = soul.Era, cyz = soul.AttunedTo(viewer), cy0 = soul.AttunedName,
            cy1 = soul.Strain, cy2 = soul.Attunement,
            cy3 = [.. soul.Traits.Select(t => t.Name)], cy4 = [.. soul.Traits.Select(t => t.Roll)],
            cy5 = [.. soul.Traits.Select((_, k) => k == soul.PendingSlot ? soul.Pending.Select(t => t.Name).ToArray() : [])],
            cy6 = [.. soul.Traits.Select((_, k) => k == soul.PendingSlot ? soul.Pending.Select(t => t.Roll).ToArray() : [])],
        }.FillEmpty();
    }

    /// <summary>
    /// The NewEquipmentItem of <see cref="SendItemObject"/>; <paramref name="inUse"/> sets key 3 (da0, c10.ao5), which the official
    /// server sets on the mount item while it is mounted or mounting and clears at the dismount (stageD mounts SPEC §4.1, Q10).
    /// </summary>
    internal static NewEquipmentItemEvent ItemObject(Item item, bool inUse = false) =>
        new NewEquipmentItemEvent
        {
            dax = item.Id, day = item.Template.Index, daz = (short)item.Count,
            da0 = inUse,                  // [3] the mount in use
            cyq = (byte)item.Quality,     // [7] quality (1 = normal)
            cyr = item.DurabilityRaw,     // [8] durability, a7m raw; 0 shows the item broken
            cys = item.Active,            // [9] spell id per active slot, -1 = none
            cyt = item.Passive,           // [10] spell id per passive slot, -1 = none
            cyu = (byte)(item.Soul is null ? 0 : 1), // [11] LegendaryItemType (a6i): 1 = awakened; 0 drops the soul client-side
        }.FillEmpty();

    /// <summary>Saved spell choice (by name) or, for a new item, the official default selection.</summary>
    private (short[] Active, short[] Passive) Selection(string itemName, InventoryItem saved, Func<int, bool> usable)
    {
        if (world.Spells is not { } spells || spells.SlotsOf(itemName) is not { } slots) return ([], []);
        var (defA, defP) = spells.DefaultSelection(itemName, usable);
        short[] Restore(string?[]? names, short[] fallback, int length) => names is null
            ? fallback
            : Enumerable.Range(0, length).Select(i => i < names.Length ? spells.IdOf(names[i]) : (short)-1).ToArray();
        return (Restore(saved.Active, defA, slots.ActiveSlots), Restore(saved.Passive, defP, slots.PassiveSlots));
    }

    /// <summary>
    /// Spells the owner can use, for the default selection of a new item: spells.xml unlockedtoequip (not
    /// locked) or unlocked through the destiny board (SpellUse rewards, ctp.ahg 0x18099e250). A progression
    /// that unlocks everything makes every spell usable; any other only the unlocked-by-default ones.
    /// TODO: resolve SpellUse rewards (achievements.xml unlock type="spelluse" item patterns and the
    /// UnlockSpells tags by level, model in analysis/work/item-spells/spelluse_cov.py) into a spell id set per
    /// player; until then a partly unlocked owner gets a preselection where the official server may leave -1.
    /// </summary>
    private Func<int, bool> SpellUsable(Progression? progression) =>
        progression is not null && world.GameData is { } data && progression.UnlocksAll(data) ? _ => true : id => world.Spells?.IsLocked(id) == false;

    /// <summary>
    /// Spells a choice may set (op 270). With a progression the client holds the same unlocks (event 150)
    /// and has already checked SpellNotUnlocked itself (a6b.jp 0x180924e50), so the server trusts it, as
    /// InventoryMoveItem does for ItemNotUnlockedToEquip; the other checks still run. Without game data
    /// the client knows only the unlocked-by-default spells.
    /// </summary>
    private Func<int, bool> SpellSelectable(Progression? progression) =>
        progression is not null ? _ => true : id => world.Spells?.IsLocked(id) == false;

    /// <summary>
    /// CharacterEquipmentChanged key 7: active spell per CharacterSpellSlot (14 entries, cp0.x8 bil[14]):
    /// 0-2 mainhand active slots (2 = offhand active slot 0 when the mainhand has fewer than 3), 3 armor,
    /// 4 head, 5 shoes, 6-11 vanity (-1), 12/13 consumespell of the potion/food. -1 = none. Passives are
    /// not part of it. Verified on 65 own events in the captures.
    /// </summary>
    internal int[] SpellSlots(Inventory inventory)
    {
        var equipment = inventory.Equipment;
        var slots = Enumerable.Repeat(-1, 14).ToArray();
        int Active(int equipmentSlot, int i) => equipment[equipmentSlot] is { } it && i < it.Active.Length ? it.Active[i] : -1;
        var mainhand = equipment[0];
        for (var i = 0; i < 3; i++) slots[i] = Active(0, i);
        if (mainhand is null || mainhand.Active.Length < 3) slots[2] = Active(1, 0);
        slots[3] = Active(3, 0);
        slots[4] = Active(2, 0);
        slots[5] = Active(4, 0);
        if (world.Spells is { } spells)
        {
            // A plugin's slot spell (Systems.SlotSpells) in place of the consumespell while that stack lies in the slot.
            if (equipment[8] is { } potion) slots[12] = potion.ConsumeOverride ?? spells.ConsumeSpell(potion.Template.Name);
            if (equipment[9] is { } food) slots[13] = food.ConsumeOverride ?? spells.ConsumeSpell(food.Template.Name);
        }
        return slots;
    }

    /// <summary>
    /// Choosing a spell on an item (op 270, client class sb: 0 item object id, 1 true = active slot, 2 slot
    /// index, 3 spell id). The client applies it locally first (cw6.u 0x1809df4e0) and expects no response.
    /// The official server re-sends the full NewEquipmentItem and, for an equipped item, CharacterEquipmentChanged
    /// with key 9 = true (also for passive changes); inventory items get only the NewEquipmentItem
    /// (capture 113433). The choice is stored on the item and saved with the character
    /// (<see cref="Persistence.SaveScheduler.ChangeDelay"/> later).
    /// A rejected change re-sends the unchanged item so the client rolls its local change back (inferred
    /// from c1s.ajk; no rejection was captured).
    /// </summary>
    [Handles(OperationCodes.EquipmentItemChangeSpell, Status = SessionStatus.InWorld)]
    private AlbionResponse? EquipmentItemChangeSpell(EquipmentItemChangeSpellRequest req)
    {
        if (Player is not { } player || world.Spells is not { } spells) return null;
        var inventory = player.Inventory;
        if (inventory.FindItem(req.kc) is not { Template.IsEquipment: true } item)
        {
            Log.Warn(Name, $"spell change for unknown item object {req.kc}");
            return null;
        }
        // Order of the client's own checks (a6b.jp 0x180924e50); LockedInCombat (18) needs combat state.
        var error = item.Count != 1 ? 28 : spells.Check(item.Template.Name, req.kd, req.ke, req.kf, SpellSelectable(player.Progression));
        var equipped = inventory.IsEquipped(item);
        if (error != 0)
        {
            Log.Info(Name, $"spell {req.kf} for '{item.Template.Name}' {(req.kd ? "active" : "passive")} slot {req.ke} rejected ({error})");
            SendItemObject(item);
            // Key 9 only starts the client's spell change cooldown (ctp.ahe → ct4.c onspellchange): false here.
            if (equipped) SendEquipmentChanged(player, intoEquipment: false);
            return null;
        }
        var before = (req.kd ? item.Active : item.Passive)[req.ke];
        (req.kd ? item.Active : item.Passive)[req.ke] = (short)req.kf;
        item.Temporary?.Remove((req.kd, req.ke)); // the player's own choice ends a plugin's temporary one (no revert)
        SendItemObject(item);
        if (equipped) SendEquipmentChanged(player, intoEquipment: false, spellChanged: true);
        player.SaveItems();
        Log.Info(Name, $"'{item.Template.Name}' {(req.kd ? "active" : "passive")} slot {req.ke} = {spells.Name(req.kf)}");
        if (equipped && before != req.kf) Systems.SlotSpells.PlayerChose(player, item, req.kd, req.ke, before);
        return null;
    }

    /// <summary>
    /// A slot spell changed by the server (<see cref="Systems.SlotSpells"/>): the full NewEquipmentItem, then CharacterEquipmentChanged with the
    /// new key 7 (the action bar: the client sets each slot's spell by its id, cp0.x8 0x180BDAB90 from crr.aft) and key 9 false (no spell change
    /// cooldown in the client's item window, which only the player's own choice starts).
    /// </summary>
    internal void SendSlotSpellChange(Player player, Item item)
    {
        SendItemObject(item);
        SendEquipmentChanged(player, intoEquipment: false);
    }

    /// <summary>
    /// Drag and drop between and inside the equipment and inventory containers. The client sends
    /// no response-expecting request; the official server answers with InventoryPutItem per moved
    /// object (revision = request key 5) and CharacterEquipmentChanged when the equipment changed
    /// (capture 113433), preceded by RegenerationPlayerComboChanged when the bars
    /// changed. An occupied target swaps the two objects. The client checks unlocks itself before
    /// sending the move (ItemNotUnlockedToEquip), so the server only checks the slot type
    /// (<see cref="Container.Fits"/>). An item leaving the equipment gets its script's OnUnequip, one entering it
    /// OnEquip (unequip first), before the bars are recalculated. StageD mounts (SPEC §4.4, §4.9): the mount slot does not move while
    /// the mount is in use; a mount taken off or swapped while dismounted takes its mount object (and the halfmounted buff) with it.
    /// A put into the target carries the target's revision (key 5, as captured), a put back into the source (a refusal, the displaced
    /// item of a swap) the source's (key 2): the client checks each put against the snapshot of that container's own revision
    /// (stageM loot-moves NOTES §2). StageM: a move out of an open loot container is <see cref="MoveFromLoot"/>; one into it is refused.
    /// </summary>
    [Handles(OperationCodes.InventoryMoveItem, Status = SessionStatus.InWorld)]
    private AlbionResponse? InventoryMoveItem(InventoryMoveItemRequest req)
    {
        if (Player is not { } player) return null;
        var inventory = player.Inventory;
        // Equipment, inventory or an open bank vault tab (stage 6b: the vault's container Guid of AttachItemContainer).
        var from = player.FindContainer(req.rv);
        var to = player.FindContainer(req.ry);
        // StageM loot-moves: a shift+click, a drag onto a bag slot or onto the equipment out of an open loot container (never into one).
        if (from is null && LootSource(player, req.rv) is { } loot) return MoveFromLoot(player, loot, req);
        if (from is not null && to is null && req.ru >= 0 && req.ru < from.Size && from[req.ru] is { } toLoot && LootSource(player, req.ry) is not null)
        {
            Log.Info(Name, $"'{toLoot.Template.Name}' into a loot container: refused (a loot container takes nothing)");
            SendEvent(new InventoryPutItemEvent { ckw = toLoot.Id, ckx = req.ru, cky = req.rv, ckz = req.rw });
            return null;
        }
        if (from is null || to is null || req.ru < 0 || req.ru >= from.Size || req.rx < 0 || req.rx >= to.Size)
        {
            Log.Warn(Name, $"move from {req.rv}#{req.ru} to {req.ry}#{req.rx}: unknown container or slot");
            if (from is null) ResyncTarget(player, to); // e.g. a loot bag gone meanwhile: the client's prediction into the target goes back
            return null;
        }
        var moved = from[req.ru];
        var other = to[req.rx];
        if (moved is null) return null;
        var mountSlot = (from == inventory.Equipment && req.ru == Systems.Mounts.MountSlot) || (to == inventory.Equipment && req.rx == Systems.Mounts.MountSlot);
        if (mountSlot && player.Mount.State != MountState.None)
        {
            // The mount is in use (stageD mounts SPEC §4.9; the official answer is unknown, Q9): put back, as a move that does not fit.
            Log.Info(Name, $"'{moved.Template.Name}': the mount slot is in use ({player.Mount.State}), move refused");
            SendEvent(new InventoryPutItemEvent { ckw = moved.Id, ckx = req.ru, cky = req.rv, ckz = req.rw });
            return null;
        }
        // StageL corrupted (SPEC §1.3): an archetype with allowequipmentswapping false (the corrupted dungeons) keeps the equipment as it is.
        if ((from == inventory.Equipment || to == inventory.Equipment) && from != to && !Systems.Pvp.RulesOf(world, player.Map).AllowEquipmentSwapping)
        {
            Log.Info(Name, $"'{moved.Template.Name}': no equipment change in {player.Map?.Cluster} (allowequipmentswapping false), move refused");
            SendEvent(new InventoryPutItemEvent { ckw = moved.Id, ckx = req.ru, cky = req.rv, ckz = req.rw });
            return null;
        }
        if (!to.Fits(moved, req.rx) || !from.Fits(other, req.ru)
            || !VaultTakes(player, to, moved.Template, moved.Count, from) || other is not null && !VaultTakes(player, from, other.Template, other.Count, to))
        {
            // Put the item back where it was so the client does not keep it in the target slot.
            Log.Info(Name, $"'{moved.Template.Name}' does not fit {req.ry}#{req.rx}");
            SendEvent(new InventoryPutItemEvent { ckw = moved.Id, ckx = req.ru, cky = req.rv, ckz = req.rw });
            return null;
        }
        to.Put(req.rx, moved);
        from.Put(req.ru, other);
        var equipment = inventory.Equipment;
        var equipmentChanged = from == equipment || to == equipment;
        var intoEquipment = to == equipment;
        if (from != to && from == equipment)
        {
            world.Scripts.OnUnequip(player, moved);
            if (other is not null) world.Scripts.OnEquip(player, other);
        }
        else if (from != to && intoEquipment)
        {
            if (other is not null) world.Scripts.OnUnequip(player, other);
            world.Scripts.OnEquip(player, moved);
            Systems.Consumables.Equipped(player, req.rx); // Combat:EquipCooldownSeconds on the potion and food slots (0 = off)
        }
        // Order as captured (113433, 114814): out of equipment = (96), 90, put; into equipment =
        // put(moved), (96), 90, put(displaced). Equipment onto an occupied inventory slot: not captured.
        if (equipmentChanged && !intoEquipment) SendEquipmentChanged(player, intoEquipment: false);
        SendEvent(new InventoryPutItemEvent { ckw = moved.Id, ckx = req.rx, cky = req.ry, ckz = req.rz });
        if (equipmentChanged && intoEquipment) SendEquipmentChanged(player, intoEquipment: true);
        if (other is not null) SendEvent(new InventoryPutItemEvent { ckw = other.Id, ckx = req.ru, cky = req.rv, ckz = req.rw }); // into the source: its revision (key 2)
        if (mountSlot && from != to) Systems.Mounts.Unequipped(player); // the mount object goes with it (stageD mounts SPEC §4.4)
        player.SaveItems();
        Log.Info(Name, $"moved '{moved.Template.Name}' to {(intoEquipment ? Inventory.SlotNames[req.rx] : $"inventory #{req.rx}")}");
        return null;
    }

    /// <summary>
    /// A new item in the first free inventory slot (GM .item add, Commands/Builtin/ItemCommands), under the lock. Events as
    /// the official server sends them for an item gained in a free slot (captures 103829 and 114814: harvest before
    /// HarvestFinished 61, crafting before CraftItemFinished 71, journal reward claim op 514 ClaimAlbionJournalReward):
    /// NewSimpleItem / NewEquipmentItem (as on Join, spells by the default choice), then InventoryPutItem
    /// (0 object, 1 slot, 2 inventory Guid; no revision). The official server adds a gathered stack to one of the same
    /// type instead (NewSimpleItem again for that object, no put); here every add is a new object. Then the player hooks'
    /// OnItemAdded. Saved like a move, <see cref="Persistence.SaveScheduler.ChangeDelay"/> later. Returns the slot; null
    /// when the inventory is full.
    /// </summary>
    public int? AddInventoryItem(ItemTemplate type, int count, int quality)
    {
        if (Player is not { } player) return null;
        var bag = player.Inventory.Bag;
        if (bag.FreeSlot() is var slot && slot < 0) return null;
        var item = CreateItem(type, new InventoryItem(type.Name, count) { Quality = quality }, SpellUsable(player.Progression));
        bag.Put(slot, item);
        SendEvent(new InventoryPutItemEvent { ckw = item.Id, ckx = slot, cky = bag.Id });
        world.Scripts.OnItemAdded(player, item);
        player.SaveItems();
        Log.Info(Name, $"added '{type.Name}' x{count} to inventory #{slot}");
        return slot;
    }

    /// <summary>
    /// CharacterEquipmentChanged (event 90): the visible equipment of the player object. When the player's own bars
    /// change (<see cref="Player.RecalculateStats"/>), RegenerationPlayerComboChanged (event 96) with the new bars goes
    /// right before it, as the official server does. Only the player's health/energy decide: mount, bag and food
    /// moves send none (113433: the ox out of and back into slot 7 got only 90 and the put). Known
    /// deviations: current health/energy are set to the new maximum (every captured swap happened at
    /// full health), and the mount bar is sent full, while the official server keeps the mount's
    /// current value and regenerates it (114814).
    /// Both go with the same keys to the players who know this one (stage 7 SPEC §4; official key 3, a long, is rare and left
    /// out): CharacterEquipmentChanged key 2 dresses the remote model, key 7 sets its spells.
    /// </summary>
    private void SendEquipmentChanged(Player player, bool intoEquipment, bool spellChanged = false)
    {
        var now = GameTime.Now;
        if (player.RecalculateStats(now)) SendAround(player, Systems.Combat.PlayerCombo(player, now));
        world.ExtraBars.Refresh(player, Plugin.Api.ExtraBarChangeSource.Equipment); // the extra bars (a change no OnEquip/OnUnequip saw)
        SendAround(player, new CharacterEquipmentChangedEvent
        {
            ddn = player.Id,                               // [0] player object id
            bw3 = now,                                     // [1] GameTimeStamp (ticks; Int64 = absolute)
            bw4 = player.Inventory.EquipmentTypeIds(),     // [2] item type id per EquipmentSlot
            bw9 = SpellSlots(player.Inventory),            // [7] active spell per CharacterSpellSlot [14]
            bxa = intoEquipment,                           // [8] true when an item went into equipment
            bxb = spellChanged,                            // [9] true after a spell change (starts the client's spell cooldown)
        });
    }

    /// <summary>An event about <paramref name="player"/> to its own client, then the same to its observers (stage 7 SPEC §4).</summary>
    private void SendAround(Player player, AlbionEvent e)
    {
        SendEvent(e);
        player.SendToObservers(e);
    }
}
