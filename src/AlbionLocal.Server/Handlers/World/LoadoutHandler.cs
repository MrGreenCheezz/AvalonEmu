using AlbionLocal.Photon;
using AlbionLocal.Server.Data;
using AlbionLocal.Server.Entities;
using AlbionLocal.Server.Game;
using AlbionLocal.Server.Protocol;

namespace AlbionLocal.Server.Network;

/// <summary>
/// StageN qol (analysis/work/stageN/qol/SPEC.md §2): loadouts (ops 425-431), the vault and market windows' saved builds. The client's buffer
/// (ctm, one per owner Guid) reads the headers, creates an empty loadout and fills it with an update; the server stores what it is sent
/// (<see cref="Loadout"/>) and gives it back unchanged. The owner is either the character or its current guild (Join key 56).
/// Guild members can read/use shared builds; MANAGE_LOADOUTS is required for every mutation. Equip moves the listed
/// items from the inventory and the bank tabs the client has open into the equipment and the inventory; nothing is ever created.
/// </summary>
public sealed partial class WorldSession
{
    /// <summary>RCs the client maps (ctm.c.e, ctm.b.e, ctm.a.d 0x180BF3DE0): create 17001 NoEmptySlots / 17002 AccessDenied, update 17122
    /// UpdateConflict, read 17041 (quietly dropped), -500 internal error.</summary>
    public const short LoadoutNoEmptySlots = 0x4269, LoadoutAccessDenied = 0x426A, LoadoutNotFound = 0x4291, LoadoutConflict = 0x42E2;

    /// <summary>Legacy bound; actual personal/guild limits come from the installed loadouts.bin (30 / 100 in 345319).</summary>
    public const int MaxLoadouts = 100;

    /// <summary>The per-slot status bytes of LoadoutEquip key 1 (a8n): 0 nothing asked, 1 equipped, 4 kept as it was, 5 not found.</summary>
    public const byte EquipNone = 0, EquipDone = 1, EquipKept = 4, EquipMissing = 5;

    private List<Loadout>? Loadouts(Guid owner, bool write = false) => Player is not { } p ? null
        : owner == p.Character.Id ? p.Character.Loadouts
        : world.Guilds.LoadoutOwner(p.Character.Id, owner, write)?.Loadouts.Entries;

    private int LoadoutOrderVersion(Guid owner) => Player is { } p
        ? world.Guilds.LoadoutOwner(p.Character.Id, owner)?.Loadouts.OrderVersion ?? 0 : 0;

    private bool SaveLoadouts(Guid owner, List<Loadout> next, int? orderVersion = null)
    {
        if (Player is not { } p) return false;
        if (owner != p.Character.Id) return world.Guilds.SaveLoadouts(p.Character.Id, owner, next, orderVersion);
        p.Character.Loadouts.Clear();
        p.Character.Loadouts.AddRange(next);
        world.Saves.MarkDirty(p.Character);
        return true;
    }

    /// <summary>
    /// LoadoutReadHeaders (op 427, xk {0 owner Guid}): {0 owner, 2 ids, 3 positions, 4 names, 5 colours, 6 icons} by position.
    /// Build 345319 C0CBD0 reads key1 into buffer+38; C0D680 sends it as op430 key3 (ordering revision).
    /// </summary>
    [Handles(OperationCodes.LoadoutReadHeaders, Status = SessionStatus.InWorld)]
    private AlbionResponse? LoadoutReadHeaders(LoadoutReadHeadersRequest req)
    {
        var list = Loadouts(req.uc);
        var sorted = (list ?? []).OrderBy(l => l.Position).ThenBy(l => l.Id, StringComparer.Ordinal).ToList();
        return new LoadoutReadHeadersResponse
        {
            ReturnCode = list is null ? LoadoutAccessDenied : (short)0,
            bcm = req.uc, bcn = LoadoutOrderVersion(req.uc), bco = [.. sorted.Select(l => l.Id)], bcp = [.. sorted.Select(l => l.Position)], bcq = [.. sorted.Select(l => l.Name)],
            bcr = [.. sorted.Select(l => l.Color)], bcs = [.. sorted.Select(l => l.Icon)],
        };
    }

    /// <summary>LoadoutCreate (op 425, xf: 0 owner, 1 name, 2 colour, 3 icon, 4 a8m source): an empty loadout; {0 its id, 1 true}. The client then reads it and sends its content.</summary>
    [Handles(OperationCodes.LoadoutCreate, Status = SessionStatus.InWorld)]
    private AlbionResponse? LoadoutCreate(LoadoutCreateRequest req)
    {
        if (Loadouts(req.tw, write: true) is not { } list) return new LoadoutCreateResponse { ReturnCode = LoadoutAccessDenied };
        var settings = world.Guilds.LoadoutSettings;
        if (!settings.ValidHeader(req.tx ?? "", req.ty, req.tz)) return new LoadoutCreateResponse { ReturnCode = 1 };
        if (list.Count >= (req.tw == Player!.Character.Id ? settings.PlayerLimit : settings.GuildLimit))
            return new LoadoutCreateResponse { ReturnCode = LoadoutNoEmptySlots };
        var loadout = new Loadout
        {
            Id = Guid.NewGuid().ToString("N"), Owner = req.tw, Name = req.tx ?? "", Color = req.ty, Icon = req.tz,
            Position = list.Count == 0 ? 0 : list.Max(l => l.Position) is >= 0 and < 255 and var last ? last + 1
                : Enumerable.Range(0, 256).First(p => list.All(l => l.Position != p)),
        };
        if (!SaveLoadouts(req.tw, [.. list, loadout])) return new LoadoutCreateResponse { ReturnCode = -500 };
        Log.Info(Name, $"loadout '{loadout.Name}' created ({loadout.Id})");
        return new LoadoutCreateResponse { bce = loadout.Id, bcf = true };
    }

    /// <summary>LoadoutRead (op 426, xj: 0 owner, 1 id): the stored loadout (ahu, every key as it was sent); an unknown id: 17041.</summary>
    [Handles(OperationCodes.LoadoutRead, Status = SessionStatus.InWorld)]
    private AlbionResponse? LoadoutRead(LoadoutReadRequest req)
    {
        if (Loadouts(req.ua)?.FirstOrDefault(l => l.Id == req.ub) is not { } l) return new LoadoutReadResponse { ReturnCode = LoadoutNotFound };
        return new LoadoutReadResponse
        {
            bct = req.ua, bcu = l.Id, bcv = l.Name, bcw = l.Color, bcx = l.Icon, bcy = l.Version,
            bcz = l.Equipment, bc0 = l.Quality, bc1 = l.SlotStacks, bc2 = l.ActiveSpells, bc3 = l.PassiveSpells,
            bc4 = l.PreferenceA, bc5 = l.PreferenceB, bc6 = l.PreferenceC, bc7 = l.PotionFoodPreference,
            bc8 = l.Items, bc9 = l.ItemQuality, bda = l.ItemStacks,
        };
    }

    /// <summary>
    /// LoadoutUpdate (op 428, xl: 0 owner, 1 id, 2 version read, 3 name, 4 colour, 5 icon, 6-17 the content, 345319 sender A6AF50): stored
    /// whole with the version raised; {0 id, 1 new version}. A stale version returns UpdateConflict so an older editor cannot overwrite it.
    /// </summary>
    [Handles(OperationCodes.LoadoutUpdate, Status = SessionStatus.InWorld)]
    private AlbionResponse? LoadoutUpdate(LoadoutUpdateRequest req)
    {
        if (Loadouts(req.ud, write: true) is not { } list) return new LoadoutUpdateResponse { ReturnCode = LoadoutAccessDenied };
        var index = list.FindIndex(l => l.Id == req.ue);
        if (index < 0) return new LoadoutUpdateResponse { ReturnCode = LoadoutConflict };
        var old = list[index];
        if (req.uf != old.Version) return new LoadoutUpdateResponse { ReturnCode = LoadoutConflict };
        if (old.Version == int.MaxValue) return new LoadoutUpdateResponse { ReturnCode = -500 };
        var updated = old with
        {
            Name = req.ug ?? old.Name, Color = req.uh, Icon = req.ui, Version = old.Version + 1,
            Equipment = req.uj ?? [], Quality = req.uk ?? [], SlotStacks = req.ul ?? [], ActiveSpells = req.um ?? [], PassiveSpells = req.un ?? [],
            PreferenceA = req.uo ?? [], PreferenceB = req.up ?? [], PreferenceC = req.uq ?? [], PotionFoodPreference = req.ur ?? [],
            Items = req.us ?? [], ItemQuality = req.ut ?? [], ItemStacks = req.uu ?? [],
        };
        if (!ValidLoadout(updated)) return new LoadoutUpdateResponse { ReturnCode = 1 };
        var next = list.ToList();
        next[index] = updated;
        if (!SaveLoadouts(req.ud, next)) return new LoadoutUpdateResponse { ReturnCode = -500 };
        return new LoadoutUpdateResponse { bdb = old.Id, bdc = old.Version + 1 };
    }

    /// <summary>LoadoutDelete (op 429, xg: 0 owner, 1 id): {0 owner, 1 id}, which the client drops from its list.</summary>
    [Handles(OperationCodes.LoadoutDelete, Status = SessionStatus.InWorld)]
    private AlbionResponse? LoadoutDelete(LoadoutDeleteRequest req)
    {
        if (Loadouts(req.t1, write: true) is not { } list) return new LoadoutDeleteResponse { ReturnCode = LoadoutAccessDenied };
        var next = list.Where(l => l.Id != req.t2).ToList();
        // A repeated delete of an already absent id is harmless; ownership/permissions are still checked.
        if (next.Count != list.Count && !SaveLoadouts(req.t1, next)) return new LoadoutDeleteResponse { ReturnCode = -500 };
        return new LoadoutDeleteResponse { bcg = req.t1, bch = req.t2 };
    }

    /// <summary>Op430 sends only changed positions (345319 C0D680), plus the revision from op427. Its callback rereads headers (C0C640).</summary>
    [Handles(OperationCodes.LoadoutOrderUpdate, Status = SessionStatus.InWorld)]
    private AlbionResponse? LoadoutOrderUpdate(LoadoutOrderUpdateRequest req)
    {
        if (Loadouts(req.t6, write: true) is not { } list) return new LoadoutOrderUpdateResponse { ReturnCode = LoadoutAccessDenied };
        if (req.t7 is not { } ids || req.t8 is not { } positions || ids.Length != positions.Length || ids.Length > list.Count
            || ids.Any(id => string.IsNullOrEmpty(id) || list.All(l => l.Id != id))
            || ids.Distinct(StringComparer.Ordinal).Count() != ids.Length) return new LoadoutOrderUpdateResponse { ReturnCode = 1 };
        var next = list.ToList();
        for (var i = 0; i < ids.Length; i++)
        {
            var index = next.FindIndex(l => l.Id == ids[i]);
            next[index] = next[index] with { Position = positions[i] };
        }
        if (next.Select(l => l.Position).Distinct().Count() != next.Count) return new LoadoutOrderUpdateResponse { ReturnCode = LoadoutConflict };
        var revision = LoadoutOrderVersion(req.t6);
        if (!next.SequenceEqual(list))
        {
            // Personal loadouts retain their existing ordering token; guild editors share a durable CAS revision.
            var guild = req.t6 != Player!.Character.Id;
            if (guild && req.t9 != revision) return new LoadoutOrderUpdateResponse { ReturnCode = LoadoutConflict };
            if (guild && revision == int.MaxValue) return new LoadoutOrderUpdateResponse { ReturnCode = -500 };
            if (guild) revision++;
            if (!SaveLoadouts(req.t6, next, revision)) return new LoadoutOrderUpdateResponse { ReturnCode = -500 };
        }
        return new LoadoutOrderUpdateResponse { bck = req.t6, bcl = revision };
    }

    /// <summary>
    /// Bound shared client data before publishing it to other members. Build 345319 sender A6AF50 allocates ten equipment entries,
    /// two potion/food values and three parallel inventory arrays; amounts are positive Int16 values. Requested inventory quantities
    /// may span several actual stacks. Saving a build never grants ownership of its gear.
    /// </summary>
    private bool ValidLoadout(Loadout l)
    {
        if (!world.Guilds.LoadoutSettings.ValidHeader(l.Name, l.Color, l.Icon)
            || l.Equipment.Length > Inventory.EquipmentSlots || l.Quality.Length != l.Equipment.Length
            || l.ActiveSpells.Length != l.Equipment.Length || l.PassiveSpells.Length != l.Equipment.Length
            || l.PreferenceA.Length != l.Equipment.Length || l.PreferenceB.Length != l.Equipment.Length
            || l.PreferenceC.Length != l.Equipment.Length || l.SlotStacks.Length > 2
            || l.PotionFoodPreference.Length != l.SlotStacks.Length
            || l.Items.Length > Inventory.BagSlots || l.ItemQuality.Length != l.Items.Length || l.ItemStacks.Length != l.Items.Length
            || l.Quality.Any(q => q is < 0 or > 5) || l.ItemQuality.Any(q => q is < 0 or > 5)
            || l.PreferenceA.Concat(l.PreferenceB).Concat(l.PreferenceC).Concat(l.PotionFoodPreference).Any(p => p is < 0 or > 2)
            || l.ActiveSpells.Concat(l.PassiveSpells).Any(s => s is null || s.Length > 64 || s.Any(c => c != '-' && (c < '0' || c > '9')))
            || l.SlotStacks.Any(n => n is < 0 or > short.MaxValue)) return false;
        for (var slot = 0; slot < l.Equipment.Length; slot++)
        {
            var name = l.Equipment[slot];
            if (name is null) return false;
            if (name.Length == 0) continue;
            if (world.Items.Find(name) is not { } item || item.Slot != Inventory.SlotNames[slot]) return false;
        }
        for (var i = 0; i < l.Items.Length; i++)
        {
            if (l.Items[i] is not { Length: > 0 } name || world.Items.Find(name) is not { } item
                || l.ItemStacks[i] is < 1 or > short.MaxValue) return false;
        }
        return true;
    }

    /// <summary>
    /// LoadoutEquip (op 431, xh: 0 owner, 1 a Guid of the local player (ctp+0x558), 2 id): {0 0, 1 a status per equipment slot}. Each slot the
    /// loadout names takes an item of that family under its tier, enchantment and quality preferences, closest first, from the inventory or
    /// an open bank tab; the item it replaces goes where that one came from. Missing ones stay missing (status 5), the slot keeps its item. Then
    /// the inventory part: what the bag lacks of each listed entry comes from the open bank tabs (whole stacks, a split for the rest), while
    /// the bag has room. Never in an archetype without equipment changes (corrupted dungeons) and never the mount slot while the mount is in use.
    /// </summary>
    [Handles(OperationCodes.LoadoutEquip, Status = SessionStatus.InWorld)]
    private AlbionResponse? LoadoutEquip(LoadoutEquipRequest req)
    {
        if (Player is not { } player || Loadouts(req.t3)?.FirstOrDefault(l => l.Id == req.t5) is not { } loadout)
            return new LoadoutEquipResponse { ReturnCode = LoadoutNotFound, bcj = new byte[Inventory.EquipmentSlots] };
        var status = new byte[Inventory.EquipmentSlots];
        var swapping = Systems.Pvp.RulesOf(world, player.Map).AllowEquipmentSwapping;
        var equipment = player.Inventory.Equipment;
        var bag = player.Inventory.Bag;
        var sources = new List<Container> { bag };
        sources.AddRange(LoadoutVaults(player));
        var equipped = false;
        var spellsChanged = false;
        var mountMoved = false;
        for (var slot = 0; slot < status.Length; slot++)
        {
            if (slot >= loadout.Equipment.Length || loadout.Equipment[slot] is not { Length: > 0 } want) continue;
            if (world.Items.Find(want) is not { } wanted) { status[slot] = EquipMissing; continue; }
            var choice = new LoadoutEquipmentChoice(wanted, loadout, slot);
            // Zero/missing amounts in older builds do not mean unequip. Keep that legacy route below.
            if ((slot is 8 or 9) && slot - 8 < loadout.SlotStacks.Length && loadout.SlotStacks[slot - 8] > 0)
            {
                status[slot] = EquipLoadoutConsumable(player, loadout, slot, choice, sources, swapping, out var amountChanged);
                equipped |= amountChanged;
                continue;
            }
            if (equipment[slot] is { } worn && choice.Matches(worn))
            {
                if (swapping) spellsChanged |= ApplyLoadoutSpells(player, worn, loadout, slot);
                status[slot] = EquipKept;
                continue;
            }
            if (!swapping || slot == Systems.Mounts.MountSlot && player.Mount.State != MountState.None) { status[slot] = EquipKept; continue; }
            var found = sources.SelectMany(c => Enumerable.Range(0, c.Size).Select(s => (C: c, Slot: s, Item: c[s])))
                .Where(x => x.Item is { } i && choice.Matches(i) && equipment.Fits(i, slot))
                .OrderBy(x => choice.Distance(x.Item!)).ThenBy(x => sources.IndexOf(x.C)).FirstOrDefault();
            if (found.Item is not { } item) { status[slot] = EquipMissing; continue; }
            var displaced = equipment[slot];
            if (displaced is not null && (!found.C.Fits(displaced, found.Slot)
                || !VaultTakes(player, found.C, displaced.Template, displaced.Count, equipment, item)))
            { status[slot] = EquipMissing; continue; }
            equipment.Put(slot, item);
            found.C.Put(found.Slot, displaced);
            if (displaced is not null) world.Scripts.OnUnequip(player, displaced);
            world.Scripts.OnEquip(player, item);
            Systems.Consumables.Equipped(player, slot);
            SendEvent(new InventoryPutItemEvent { ckw = item.Id, ckx = slot, cky = equipment.Id });
            if (displaced is not null) SendEvent(new InventoryPutItemEvent { ckw = displaced.Id, ckx = found.Slot, cky = found.C.Id });
            spellsChanged |= ApplyLoadoutSpells(player, item, loadout, slot);
            status[slot] = EquipDone;
            equipped = true;
            mountMoved |= slot == Systems.Mounts.MountSlot;
        }
        if (equipped || spellsChanged) SendEquipmentChanged(player, intoEquipment: equipped, spellChanged: spellsChanged);
        if (mountMoved) Systems.Mounts.Unequipped(player);
        var fetched = FetchInventory(player, loadout);
        if (equipped || spellsChanged || fetched > 0) player.SaveItems();
        Log.Info(Name, $"loadout '{loadout.Name}' equipped: slots [{string.Join(",", status)}], {fetched} stack(s) from the bank");
        return new LoadoutEquipResponse { bcj = status };
    }

    /// <summary>Only owned tabs of the bank currently registered and in reach may supply or receive a loadout.</summary>
    private IEnumerable<Container> LoadoutVaults(Player player)
    {
        if (player.Map is not { } map) yield break;
        var tabs = PersonalBankTabs(player);
        foreach (var vault in player.OpenVaults.Values)
            if (player.RegisteredTo == vault.Building && map.Objects.TryGetValue(vault.Building, out var obj)
                && obj is Building { Template.Kind: "bankbuilding" } bank && BankUnusable(player, bank) is null
                && tabs.Any(t => t.Id == vault.Tab)) yield return vault.Container;
    }

    /// <summary>
    /// 345319 LoadoutPreferencesView.Collect 1070733-10707FF / sender A6B490-A6B581:
    /// Amount[0/1] is an item count for equipment slots 8/9; its preference is 0 &lt;=, 1 =, 2 &gt;=
    /// (521C40/521430, no inversion). The client does not contain the server's stack selection policy.
    /// Local policy: keep a matching worn amount; otherwise target the requested count, allowing a
    /// smaller available count only for EqualOrWorse. Plan the return of surplus/displaced items before moving any.
    /// </summary>
    private byte EquipLoadoutConsumable(Player player, Loadout loadout, int slot, LoadoutEquipmentChoice choice, List<Container> sources,
        bool swapping, out bool changed)
    {
        changed = false;
        var equipment = player.Inventory.Equipment;
        var worn = equipment[slot];
        var amountIndex = slot - 8;
        var requested = loadout.SlotStacks[amountIndex];
        var amountPreference = amountIndex < loadout.PotionFoodPreference.Length ? loadout.PotionFoodPreference[amountIndex] : 1;
        bool Matches(Item item) => choice.Matches(item) && equipment.Fits(item, slot);
        bool AmountFits(int count) => amountPreference switch { 0 => count <= requested, 1 => count == requested, _ => count >= requested };
        if (worn is not null && Matches(worn) && AmountFits(worn.Count)) return EquipKept;
        if (!swapping) return EquipKept;
        if (equipment.Unloaded[slot] is not null) return EquipMissing;
        var candidates = sources.SelectMany((container, order) => Enumerable.Range(0, container.Size)
                .Where(s => container[s] is { Count: > 0 } item && Matches(item))
                .Select(s => (Container: container, Slot: s, Item: container[s]!, Order: order)))
            .OrderBy(x => choice.Distance(x.Item)).ThenBy(x => x.Order).ToList();
        var matchingWorn = worn is not null && Matches(worn) ? worn : null;
        // Preferences can admit several variants, but one equipped stack must use one actual template.
        // Try the nearest eligible variant with enough stock/return space; do not add different tiers or enchantments.
        IEnumerable<Item> current = matchingWorn is null ? [] : [matchingWorn];
        var variants = current.Concat(candidates.Select(x => x.Item))
            .DistinctBy(item => (item.Template, item.JournalFame)).OrderBy(choice.Distance);
        foreach (var exemplar in variants)
        {
            var kept = matchingWorn is not null && exemplar.StacksWith(matchingWorn) ? matchingWorn : null;
            var maximum = MaxStack(exemplar.Template);
            if (requested > maximum && amountPreference != 0) continue;
            var target = Math.Min(requested, maximum);
            var available = (long)(kept?.Count ?? 0) + candidates.Where(x => exemplar.StacksWith(x.Item)).Sum(x => (long)x.Item.Count);
            if (available < target)
            {
                if (amountPreference != 0) continue;
                target = (int)available;
            }
            if (target <= 0) continue;
            var needed = Math.Max(0, target - (kept?.Count ?? 0));
            var withdrawals = new Dictionary<Item, int>();
            foreach (var candidate in candidates)
            {
                if (needed == 0) break;
                if (!exemplar.StacksWith(candidate.Item)) continue;
                var count = Math.Min(needed, candidate.Item.Count);
                withdrawals[candidate.Item] = count;
                needed -= count;
            }
            var returning = kept is not null ? Math.Max(0, kept.Count - target) : worn?.Count ?? 0;
            List<(Container Container, int Slot, int Count)>? returns = worn is null ? []
                : PlanLoadoutReturn(player, worn, returning, sources, withdrawals);
            if (returns is null) continue;

            if (kept is null && worn is not null)
            {
                equipment.Take(slot);
                world.Scripts.OnUnequip(player, worn);
            }
            foreach (var candidate in candidates)
                if (withdrawals.TryGetValue(candidate.Item, out var count))
                    MoveLoadoutCount(player, candidate.Container, candidate.Item, equipment, slot, count);
            foreach (var destination in returns)
                MoveLoadoutCount(player, equipment, worn!, destination.Container, destination.Slot, destination.Count);
            if (kept is null)
            {
                world.Scripts.OnEquip(player, equipment[slot]!);
                Systems.Consumables.Equipped(player, slot);
            }
            changed = true;
            return EquipDone;
        }
        return EquipMissing;
    }

    /// <summary>Plan exact return destinations after withdrawals; execution uses the same slots/counts under World.Sync.</summary>
    private static List<(Container Container, int Slot, int Count)>? PlanLoadoutReturn(Player player, Item item, int count,
        List<Container> containers, Dictionary<Item, int> withdrawals)
    {
        var plan = new List<(Container Container, int Slot, int Count)>();
        if (count == 0) return plan;
        var maximum = MaxStack(item.Template);
        foreach (var container in containers)
        {
            int Remaining(int slot) => container[slot] is { } stack ? stack.Count - withdrawals.GetValueOrDefault(stack) : 0;
            var slots = Enumerable.Range(0, container.Size)
                .Where(s => Remaining(s) > 0 && container[s]!.StacksWith(item))
                .Concat(Enumerable.Range(0, container.Size).Where(s => Remaining(s) == 0 && container.Unloaded[s] is null));
            var weight = container.Items.Sum(stack => Weight(stack.Template) * (stack.Count - withdrawals.GetValueOrDefault(stack)));
            var vault = player.OpenVaults.Values.FirstOrDefault(v => v.Container == container);
            foreach (var slot in slots)
            {
                // The same bank policy as MoveGiven: fill compatible stacks before consuming free slots.
                var moving = Math.Min(count, maximum - Remaining(slot));
                if (vault is not null && Weight(item.Template) > 0)
                    moving = (int)Math.Min(moving, Math.Max(0, Math.Floor((vault.WeightLimit - weight) / Weight(item.Template))));
                if (moving <= 0) continue;
                plan.Add((container, slot, moving));
                weight += Weight(item.Template) * moving;
                count -= moving;
                if (count == 0) return plan;
            }
        }
        return null;
    }

    /// <summary>The existing move/merge/split wire path, preserving live item metadata and total item count.</summary>
    private void MoveLoadoutCount(Player player, Container from, Item source, Container to, int slot, int count)
    {
        if (to[slot] is { } target)
        {
            target.Count += count;
            source.Count -= count;
            SendCount(target);
            if (source.Count > 0) SendCount(source);
            else
            {
                if (from.IndexOf(source) is var sourceSlot && sourceSlot >= 0) from.Take(sourceSlot);
                SendEvent(new InventoryDeleteItemEvent { ckr = source.Id });
            }
            return;
        }
        var moved = source;
        if (count == source.Count)
        {
            if (from.IndexOf(source) is var sourceSlot && sourceSlot >= 0) from.Take(sourceSlot);
        }
        else
        {
            moved = CreateItem(source.Template, source.ToRecord(id => world.Spells?.Name(id)) with { Count = count }, SpellUsable(player.Progression));
            source.Count -= count;
            SendCount(source);
        }
        to.Put(slot, moved);
        SendEvent(new InventoryPutItemEvent { ckw = moved.Id, ckx = slot, cky = to.Id });
    }

    /// <summary>
    /// Build 345319 ctj.i/j (RVAs C09D60/C09FE0) encode each choice as its candidate index, using
    /// StringBuilder.Append(int), or '-' when no spell was selected. These are not spell names or ids.
    /// Resolve that index in the configured template, then check the spell against the real item: a tier fallback
    /// can have fewer or differently ordered choices, and must not turn the same index into another spell.
    /// An unspecified choice keeps the item's current spell. Use the same eligibility and update path
    /// as EquipmentItemChangeSpell, including items already worn when the loadout is equipped.
    /// </summary>
    private bool ApplyLoadoutSpells(Player player, Item item, Loadout loadout, int equipmentSlot)
    {
        if (!item.Template.IsEquipment || item.Count != 1 || world.Spells is not { } spells ||
            equipmentSlot >= loadout.Equipment.Length || spells.SlotsOf(loadout.Equipment[equipmentSlot]) is not { } slots) return false;
        var changed = false;
        var activeChanged = new List<int>();
        var usable = SpellSelectable(player.Progression);
        Apply(loadout.ActiveSpells, item.Active, slots.Active, active: true);
        Apply(loadout.PassiveSpells, item.Passive, slots.Passive, active: false);
        if (!changed) return false;
        SendItemObject(item);
        Systems.OpeningTutorial.Update(player);
        foreach (var slot in activeChanged) Systems.OpeningTutorial.SwordSpellSelected(player, item, true, slot);
        return true;

        void Apply(string[] selections, short[] current, short[][] candidates, bool active)
        {
            if (equipmentSlot >= selections.Length || selections[equipmentSlot] is not { Length: > 0 } encoded) return;
            for (var slot = 0; slot < encoded.Length && slot < current.Length && slot < candidates.Length; slot++)
            {
                var index = encoded[slot] - '0';
                if (index < 0 || index > 9 || index >= candidates[slot].Length) continue;
                var spell = candidates[slot][index];
                if (current[slot] == spell || spells.Check(item.Template.Name, active, slot, spell, usable) != 0) continue;
                current[slot] = spell;
                changed = true;
                if (active) activeChanged.Add(slot);
            }
        }
    }

    /// <summary>
    /// 345319 LoadoutPreferencesView.Collect 10705D8-10706F7 / sender A6B3B9-A6B48C:
    /// PreferenceA is tier, B enchantment and C quality. Family identity comes from the existing Armory item-family
    /// authority; equal names/categories alone are not interchangeable weapons or armour. Local selection order is
    /// nearest tier, then enchantment, then quality, followed by the source-container order at the call site.
    /// </summary>
    private sealed class LoadoutEquipmentChoice(ItemTemplate wanted, Loadout loadout, int slot)
    {
        private readonly string family = Systems.ArmoryUsage.Family(wanted);
        private readonly int quality = slot < loadout.Quality.Length ? loadout.Quality[slot] : 0;
        private readonly int tierPreference = slot < loadout.PreferenceA.Length ? loadout.PreferenceA[slot] : 1;
        private readonly int enchantmentPreference = slot < loadout.PreferenceB.Length ? loadout.PreferenceB[slot] : 1;
        private readonly int qualityPreference = slot < loadout.PreferenceC.Length ? loadout.PreferenceC[slot] : 2;

        public bool Matches(Item item) => item.Template.Slot == wanted.Slot
            && string.Equals(Systems.ArmoryUsage.Family(item.Template), family, StringComparison.Ordinal)
            && LoadoutValueFits(item.Template.Tier, wanted.Tier, tierPreference)
            && LoadoutValueFits(item.Template.Enchantment, wanted.Enchantment, enchantmentPreference)
            && (quality <= 0 || LoadoutValueFits(item.Quality, quality, qualityPreference));

        public (int Tier, int Enchantment, int Quality) Distance(Item item) =>
            (Math.Abs(item.Template.Tier - wanted.Tier), Math.Abs(item.Template.Enchantment - wanted.Enchantment), Math.Abs(item.Quality - quality));
    }

    /// <summary>LoadoutItemPreference: 0 EqualOrWorse, 1 Equal, 2 EqualOrBetter. Zero enchantment is a real value.</summary>
    private static bool LoadoutValueFits(int have, int want, int preference) => preference switch
    {
        0 => have <= want,
        1 => have == want,
        _ => have >= want,
    };

    /// <summary>The inventory part of a loadout: per entry, what the bag lacks comes out of the open bank tabs. Returns the stacks moved or split.</summary>
    private int FetchInventory(Player player, Loadout loadout)
    {
        var bag = player.Inventory.Bag;
        var vaults = LoadoutVaults(player).ToList();
        if (vaults.Count == 0) return 0;
        // Claim actual objects, not a total by name: two rows can request different qualities of the same item.
        var claimed = new Dictionary<Item, int>();
        var moved = 0;
        for (var i = 0; i < loadout.Items.Length; i++)
        {
            if (loadout.Items[i] is not { Length: > 0 } name) continue;
            var quality = i < loadout.ItemQuality.Length ? loadout.ItemQuality[i] : 0;
            var need = Math.Max(1, i < loadout.ItemStacks.Length ? loadout.ItemStacks[i] : 1);
            bool Matches(Item it) => it.Template.Name == name && (quality <= 1 || !it.Template.IsEquipment || it.Quality == quality);
            foreach (var item in bag.Items.Where(Matches).OrderBy(it => Math.Abs(it.Quality - quality)))
            {
                var have = Math.Min(need, Math.Max(0, item.Count - claimed.GetValueOrDefault(item)));
                claimed[item] = claimed.GetValueOrDefault(item) + have;
                need -= have;
                if (need == 0) break;
            }
            foreach (var vault in vaults)
                for (var s = 0; s < vault.Size && need > 0; s++)
                {
                    if (vault[s] is not { } stack || !Matches(stack)) continue;
                    var fetched = FetchLoadoutStack(player, vault, s, stack, need, claimed);
                    need -= fetched;
                    if (fetched > 0) moved++;
                }
        }
        return moved;
    }

    /// <summary>
    /// Transfer only what this loadout row still needs. Fill compatible stacks before requiring a free slot;
    /// a full bag must not prevent later rows from topping up stacks either. Splits retain the item's live metadata.
    /// </summary>
    private int FetchLoadoutStack(Player player, Container vault, int slot, Item source, int need, Dictionary<Item, int> claimed)
    {
        var bag = player.Inventory.Bag;
        var moved = 0;
        var max = MaxStack(source.Template);
        if (max > 1)
            foreach (var target in bag.Items)
            {
                if (!target.StacksWith(source) || target.Count >= max) continue;
                var count = Math.Min(need - moved, Math.Min(source.Count, max - target.Count));
                if (count <= 0) break;
                MoveLoadoutCount(player, vault, source, bag, bag.IndexOf(target), count);
                claimed[target] = claimed.GetValueOrDefault(target) + count;
                moved += count;
                if (source.Count == 0) return moved;
            }
        var remaining = Math.Min(need - moved, source.Count);
        if (remaining > 0 && bag.FreeSlot() is var free && free >= 0)
        {
            MoveLoadoutCount(player, vault, source, bag, free, remaining);
            var part = bag[free]!;
            claimed[part] = claimed.GetValueOrDefault(part) + remaining;
            moved += remaining;
        }
        return moved;
    }
}
