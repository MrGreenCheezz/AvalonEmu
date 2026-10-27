using System.Globalization;
using System.Numerics;
using AlbionLocal.Photon;
using AlbionLocal.Server.Data;
using AlbionLocal.Server.Entities;
using AlbionLocal.Server.Game;
using AlbionLocal.Server.Protocol;
using AlbionLocal.Server.Systems;

namespace AlbionLocal.Server.Network;

/// <summary>ActionComponentType (op 55 key 2): what a station action is.</summary>
public enum StationAction : byte { CraftItem = 1, RepairItem = 2, SalvageItem = 3, RerollQuality = 6, UpgradeEnchantment = 7, Awaken = 8 }

/// <summary>RepairItemErrorCode (key 3 of a failed op 55 with RC 2202 for ACT 2).</summary>
public enum RepairError : byte
{
    OutOfRange = 2, ItemNotRepairable = 3, ItemHasFullDurability = 4, NotEnoughSilver = 6, PriceChanged = 12, NoItemToRepairFound = 14,
}

/// <summary>SalvageItemErrorCode (key 3 of a failed op 55 with RC 2202 for ACT 3; cml.aaq 0x180B6E700 shows it as @REPAIRBUILDING_SALVAGE_ERROR_*).</summary>
public enum SalvageError : byte { OutOfRange = 2, ItemNotSalvageable = 3, InventoryFull = 4, ItemTierToHigh = 6 }

/// <summary>CraftItemErrorCode (key 3 of a failed op 55 with RC 2202 for ACT 1).</summary>
public enum CraftError : byte { NotEnoughSilver = 6, NotEnoughResources = 8, InventoryFull = 14, PriceChanged = 23 }

/// <summary>
/// Op 55 ActionOnBuildingStart, the station actions (stage 6 SPEC §6a.1, §6a.3, §6c with the verifier's corrections): the
/// repair (ACT 2) and crafting (ACT 1), which is also how a player buys at an NPC shop (FARMING_MERCHANT and the other
/// isshop buildings: there is no buy op). A success gets no response and no event 64 to the actor, only the burst of events
/// and ActionOnBuildingFinished (66); other players who know the actor get 64 at the start and 66 at the end (65 on a cancel).
/// A failure is the response {0 building, 1 ACT, 2 = request key 3 (the counter cuo.cy, which cvw.v 0x1809D4270 matches with
/// key 1 against its pending action, else it drops the answer), 3 error}: RC 2202 makes the client show the component's own
/// message for key 3 (RepairItemErrorCode / CraftItemErrorCode, cl2.aaq); any other non-zero RC a generic error, key 3
/// ignored, which is used for what has no code (an unknown building, an ACT it does not offer, an unknown item).
/// </summary>
public sealed partial class WorldSession
{
    /// <summary>The RC of a failure with the component's error code in key 3 (enum ft: 2202).</summary>
    public const short StationComponentError = 2202;

    /// <summary>The RC of a failure without a code of its own: the client's generic error 1.</summary>
    public const short StationGenericError = 1;

    /// <summary>How far from a station's position the player may stand (lenient: the building's tile is large; not traced).</summary>
    public const float StationRange = 20;

    /// <summary>The repair's duration until Q-A3 is settled (official own repair: 0.815 s; others' 0.025-1.6 s).</summary>
    public static readonly TimeSpan RepairTime = TimeSpan.FromSeconds(0.8);

    /// <summary>The salvage's duration (not captured; as <see cref="RepairTime"/>: gamedata gives both the crafttimefactor 0.1).</summary>
    public static readonly TimeSpan SalvageTime = RepairTime;

    /// <summary>
    /// ActionOnBuildingStart (op 55, client class ok; keys: 0 client time, 1 building object, 2 ACT, 3 counter, 4 the price
    /// the client expects (a7m), 5 repair: item object ids, 7 craft: item index, 8 recipe index, 9 amount, 10 craft by
    /// uniquename). Checks in order: the building is on the player's map, in its view and within <see cref="StationRange"/>;
    /// no other station action runs; the building offers the ACT; then the ACT's own checks.
    /// </summary>
    [Handles(OperationCodes.ActionOnBuildingStart, Status = SessionStatus.InWorld)]
    private AlbionResponse? ActionOnBuildingStart(ActionOnBuildingStartRequest req)
    {
        if (Player is not { } player) return null;
        AlbionResponse Generic(string why) => StationRefused(player, req, StationGenericError, 0, why);
        if (player.Map is not { } map || !map.Objects.TryGetValue(req.o, out var o) || o is not Building building || !player.Visibility.Known.Contains(building))
            return Generic($"object {req.o} is no building in the player's view");
        if (player.Action is { Over: false } busy)
            return Generic($"another station action runs at building {busy.Building.Id}");
        if (Systems.OpeningTutorial.ForgeLocked(player, building)) return Generic("tutorial forge is locked until its quest is accepted");
        var far = Vector2.Distance(player.PositionAt(DateTime.UtcNow), building.Position) > StationRange;
        return (StationAction)req.p switch
        {
            StationAction.RepairItem when !building.Template.Repairs => Generic($"{building.Template.Name} does not repair"),
            StationAction.RepairItem when far => StationRefused(player, req, StationComponentError, (byte)RepairError.OutOfRange, "out of range"),
            StationAction.RepairItem => StartRepair(player, building, req),
            StationAction.SalvageItem when !building.Template.Salvages => Generic($"{building.Template.Name} does not salvage"),
            StationAction.SalvageItem when far => StationRefused(player, req, StationComponentError, (byte)SalvageError.OutOfRange, "out of range"),
            StationAction.SalvageItem => StartSalvage(player, building, req),
            StationAction.CraftItem when building.Template.CraftItems.Count == 0 => Generic($"{building.Template.Name} crafts nothing"),
            StationAction.CraftItem when far => Generic("out of range"),
            StationAction.CraftItem => StartCraft(player, building, req),
            StationAction.UpgradeEnchantment when !building.Template.Enchants => Generic($"{building.Template.Name} does not enchant"),
            StationAction.UpgradeEnchantment when far => StationRefused(player, req, StationComponentError, (byte)EnchantError.OutOfRange, "out of range"),
            StationAction.UpgradeEnchantment => StartEnchant(player, building, req),
            StationAction.RerollQuality when !building.Template.RerollsQuality => Generic($"{building.Template.Name} does not reroll quality"),
            StationAction.RerollQuality when far => StationRefused(player, req, StationComponentError, (byte)RerollError.OutOfRange, "out of range"),
            StationAction.RerollQuality => StartReroll(player, building, req),
            StationAction.Awaken when !building.Template.Awakens => Generic($"{building.Template.Name} does not awaken"),
            StationAction.Awaken when far => StationRefused(player, req, StationComponentError, (byte)AwakenError.OutOfRange, "out of range"),
            StationAction.Awaken => StartLegendary(player, building, req),
            _ => Generic($"ACT {req.p} is not made yet"),
        };
    }

    /// <summary>The failure response of op 55 (see the class), logged with why.</summary>
    private ActionOnBuildingStartResponse StationRefused(Player player, ActionOnBuildingStartRequest req, short rc, byte error, string why)
    {
        Log.Info(Name, $"'{player.Character.Name}' station action {(StationAction)req.p} at object {req.o} refused (RC {rc}{(error != 0 ? $", error {error}" : "")}): {why}");
        return new ActionOnBuildingStartResponse { ReturnCode = rc, ajj = req.o, ajk = req.p, ajl = req.q, ajm = error };
    }

    /// <summary>
    /// The repair (ACT 2): every listed object that is the player's (worn or in the inventory), repairable (equipment with an
    /// items.xml durability) and below full is repaired; none listed or found → NoItemToRepairFound, all full →
    /// ItemHasFullDurability, an item without durability → ItemNotRepairable. The price is Σ <see cref="Pricing.RepairBase"/>
    /// (whole silver, the client's formula with the a7m durability fraction) with the city factor 1 and no usage fee (no
    /// RepairBuildingInfo with fees is sent); the client's key 4 is taken per <see cref="Pricing.Accepts"/> (else
    /// PriceChanged) and must be in the wallet (else NotEnoughSilver). The burst as the official own repair of 103829
    /// (verifier): NewBuilding with key 16 at the start; at the end (<see cref="RepairTime"/>) UpdateMoney (charged then), the
    /// repaired items re-sent with full durability, RepairBuildingInfo {0 building, 7 [], 8 []}, NewBuilding without key 16,
    /// ActionOnBuildingFinished. No DurabilityChanged (97). Furniture is repairable officially, but its full durability is
    /// not items.xml's (103829: 135 and 140 against the attributes 0.5 and 14; source not found), so it is refused as
    /// ItemNotRepairable for now; this server cannot create furniture yet anyway.
    /// </summary>
    private AlbionResponse? StartRepair(Player player, Building building, ActionOnBuildingStartRequest req)
    {
        AlbionResponse Refuse(RepairError e, string why) => StationRefused(player, req, StationComponentError, (byte)e, why);
        var found = (req.s ?? []).Distinct().Select(player.Inventory.FindItem).OfType<Item>().ToList();
        if (found.Count == 0) return Refuse(RepairError.NoItemToRepairFound, "no item of the player listed");
        if (found.FirstOrDefault(i => !i.Template.IsEquipment || i.Template.MaxDurability <= 0) is { } plain)
            return Refuse(RepairError.ItemNotRepairable, $"{plain.Template.Name} has no durability");
        var damaged = found.Where(i => i.DurabilityRaw < i.Template.FullDurabilityRaw).ToList();
        if (damaged.Count == 0) return Refuse(RepairError.ItemHasFullDurability, "every listed item is at full durability");
        var silver = damaged.Sum(i => Pricing.RepairBase(world.ItemValues.Of(i.Template), i.DurabilityRaw, i.Template.MaxDurability, i.Count, world.Economy));
        var expected = silver * Pricing.A7m;
        if (!Pricing.Accepts(req.r, expected))
            return Refuse(RepairError.PriceChanged, string.Create(CultureInfo.InvariantCulture, $"the client's price {req.r / 10000.0:0.####} is not the server's {silver}"));
        if (player.Character.Silver < req.r) return Refuse(RepairError.NotEnoughSilver, $"{player.Character.Silver / 10000} silver of {req.r / 10000}");
        var now = world.GameNow;
        var action = new BuildingAction(building, req.p, req.q, now, now + RepairTime.Ticks);
        Systems.Mounts.BeforeAction(player, "repairs"); // stageD mounts SPEC §4.3: an accepted station action dismounts first
        player.Action = action;
        SendEvent(building.NewBuilding(inUse: true));
        StartedToObservers(player, action);
        // Guilds SPEC §10.3: the repair perk of the player's guild level lowers what is charged (the client's price is the check above).
        var price = world.Guilds.RepairPrice(player, req.r);
        Log.Info(Name, $"'{player.Character.Name}' repairs {damaged.Count} item(s) at {building.Template.Name} for {silver} silver (client {req.r / 10000.0:0.####}"
            + (price < req.r ? $", {price / 10000.0:0.####} charged with the guild perk)" : ")"));
        player.Map!.Timers.At(world.ClockAt(action.End), () => FinishRepair(player, action, damaged, price));
        return null;
    }

    private void FinishRepair(Player player, BuildingAction action, List<Item> items, long price)
    {
        if (action.Over || player.Action != action || player != Player) return;
        var still = items.Where(i => player.Inventory.FindItem(i.Id) == i).ToList();
        if (still.Count == 0 || !Wallet.TrySpend(player, price, $"repair at {action.Building.Template.Name}"))
        {
            CancelAction(player, still.Count == 0 ? "the items are gone" : "not enough silver any more", tellClient: true);
            return;
        }
        action.Over = true;
        player.Action = null;
        var equipped = false;
        foreach (var item in still)
        {
            item.DurabilityRaw = item.Template.FullDurabilityRaw;
            SendItemObject(item);
            equipped |= player.Inventory.IsEquipped(item);
        }
        SendEvent(new RepairBuildingInfoEvent { ddn = action.Building.Id }.FillEmpty()); // {0 building, 7 [], 8 []}: no fees, no shared info
        SendEvent(action.Building.NewBuilding());
        Finished(player, action);
        if (equipped && player.RecalculateStats(world.GameNow)) SendAround(player, Systems.Combat.PlayerCombo(player, world.GameNow));
        player.SaveItems();
    }

    /// <summary>
    /// The salvage (ACT 3, from the client code only: no capture has one). The request is the repair's (cml.aaw
    /// 0x180B6E930 → c32.i: key 5 the item object ids, key 4 the fee cml.aam, 0 without an owner; no fee is charged). Every
    /// listed object must be the player's (inventory or worn, not the mount: else ItemNotSalvageable), salvageable
    /// (<see cref="Salvageable"/>), of a tier up to the building's (else ItemTierToHigh), and give something
    /// (<see cref="SalvageGains"/>); what it gives must fit the inventory once the items are gone (else InventoryFull).
    /// Accepted: NewBuilding with key 16 and event 64 to the observers, as the repair. At the end (<see cref="SalvageTime"/>):
    /// the items deleted (InventoryDeleteItem {0 id}; a worn one leaves the equipment), UpdateMoney with the silver (only when there is any), the
    /// resources (<see cref="GiveItem"/>), NewBuilding without key 16, ActionOnBuildingFinished. The order of that burst is
    /// inferred (the repair's), not captured.
    /// </summary>
    private AlbionResponse? StartSalvage(Player player, Building building, ActionOnBuildingStartRequest req)
    {
        AlbionResponse Refuse(SalvageError e, string why) => StationRefused(player, req, StationComponentError, (byte)e, why);
        var listed = (req.s ?? []).Distinct().Select(id => player.Inventory.FindItem(id)).ToList();
        if (listed.Count == 0) return Refuse(SalvageError.ItemNotSalvageable, "no item listed");
        if (listed.Any(i => i is null)) return Refuse(SalvageError.ItemNotSalvageable, "an object that is not the player's");
        var items = listed.OfType<Item>().ToList();
        if (items.FirstOrDefault(i => player.Inventory.Equipment.IndexOf(i) == Systems.Mounts.MountSlot || !Salvageable(i.Template)) is { } plain)
            return Refuse(SalvageError.ItemNotSalvageable, $"{plain.Template.Name} cannot be salvaged");
        if (items.FirstOrDefault(i => i.Template.Tier > building.Template.Tier) is { } high)
            return Refuse(SalvageError.ItemTierToHigh, $"{high.Template.Name} is above the building's tier {building.Template.Tier}");
        if (items.FirstOrDefault(i => SalvageGains(i, null) is { Silver: <= 0, Resources.Count: 0 }) is { } nothing)
            return Refuse(SalvageError.ItemNotSalvageable, $"{nothing.Template.Name} gives nothing");
        if (!Fits(player.Inventory.Bag, MostResources(items), freed: items.Count(i => !player.Inventory.IsEquipped(i))))
            return Refuse(SalvageError.InventoryFull, "the resources do not fit the inventory");
        var now = world.GameNow;
        var action = new BuildingAction(building, req.p, req.q, now, now + SalvageTime.Ticks);
        Systems.Mounts.BeforeAction(player, "salvages"); // as every accepted station action (stageD mounts SPEC §4.3)
        player.Action = action;
        SendEvent(building.NewBuilding(inUse: true));
        StartedToObservers(player, action);
        Log.Info(Name, $"'{player.Character.Name}' salvages {string.Join(", ", items.Select(i => $"{i.Count} x {i.Template.Name}"))} at {building.Template.Name}");
        player.Map!.Timers.At(world.ClockAt(action.End), () => FinishSalvage(player, action, items));
        return null;
    }

    private void FinishSalvage(Player player, BuildingAction action, List<Item> items)
    {
        if (action.Over || player.Action != action || player != Player) return;
        var inventory = player.Inventory;
        var still = items.Where(i => inventory.FindItem(i.Id) == i).ToList();
        if (still.Count == 0 || !Fits(inventory.Bag, MostResources(still), freed: still.Count(i => !inventory.IsEquipped(i))))
        {
            CancelAction(player, still.Count == 0 ? "the items are gone" : "the resources do not fit any more", tellClient: true);
            return;
        }
        action.Over = true;
        player.Action = null;
        long silver = 0;
        var resources = new List<(ItemTemplate Type, int Count)>();
        var unequipped = false;
        foreach (var item in still)
        {
            var (s, r) = SalvageGains(item, world.CraftRandom);
            silver += s;
            resources.AddRange(r);
            var container = inventory.IsEquipped(item) ? inventory.Equipment : inventory.Bag;
            container.Take(container.IndexOf(item));
            SendEvent(new InventoryDeleteItemEvent { ckr = item.Id });
            if (container == inventory.Equipment)
            {
                world.Scripts.OnUnequip(player, item);
                unequipped = true;
            }
        }
        if (unequipped) SendEquipmentChanged(player, intoEquipment: false);
        Wallet.Credit(player, silver, $"salvage at {action.Building.Template.Name}");
        var gained = resources.GroupBy(r => r.Type).Select(g => (Type: g.Key, Count: g.Sum(r => r.Count))).ToList();
        foreach (var (type, count) in gained) GiveItem(player, type, count);
        SendEvent(action.Building.NewBuilding());
        Finished(player, action);
        if (unequipped && player.RecalculateStats(world.GameNow)) SendAround(player, Systems.Combat.PlayerCombo(player, world.GameNow));
        player.SaveItems();
        Log.Info(Name, string.Create(CultureInfo.InvariantCulture,
            $"'{player.Character.Name}' salvaged {still.Count} item(s): {silver / 10000.0:0.####} silver{string.Concat(gained.Select(g => $", {g.Count} x {g.Type.Name}"))}"));
    }

    /// <summary>
    /// Whether the client lets <paramref name="type"/> be salvaged (bdi+0x60, items.xml salvageable): the attribute when
    /// given; without it equipment (equipmentitem, weapon; not mounts) yes, everything else no.
    /// </summary>
    public static bool Salvageable(ItemTemplate type) =>
        type.Attrs.GetValueOrDefault("salvageable") is { } flag ? flag == "true" : type.Kind is "equipmentitem" or "weapon";

    /// <summary>
    /// What salvaging <paramref name="item"/> gives: silver (<see cref="Pricing.SalvageSilver"/>, a7m raw) from its salvage
    /// value as the client has it (bdi+0xC8, set once at parse time, bdi.li 0x1805F4060: salvagevalueoverride, else the
    /// element's own itemvalue attribute, else a7m.MinValue from the ctor 0x1805F10A0; the value worked out later from the
    /// recipe, bdi.d, goes to +0xB0 only and no subclass parser bc4/bc9/bdd/bdm writes +0xC8), so an item without either
    /// attribute (every weapon and armour piece of items.xml) gives no silver, as the client's preview (cml.al +0xC8 &lt; 1:
    /// resources only); and per resource of its first recipe count × stack × the resource
    /// factor (<see cref="Pricing.SalvageResourceFactor"/>) rounded at random with <paramref name="random"/> (null: up, the
    /// most it can give). Resources this server cannot create are left out.
    /// </summary>
    internal (long Silver, List<(ItemTemplate Type, int Count)> Resources) SalvageGains(Item item, Random? random)
    {
        var t = item.Template;
        var recipe = t.Recipes.FirstOrDefault();
        var value = double.TryParse(t.Attrs.GetValueOrDefault("salvagevalueoverride"), NumberStyles.Float, CultureInfo.InvariantCulture, out var v)
            ? v : t.OwnItemValue ?? 0;
        var maxDurability = t.IsEquipment ? t.MaxDurability : 0;
        var silver = Pricing.SalvageSilver(value, recipe, t.Tier, item.DurabilityRaw, maxDurability, item.Count, world.Economy);
        var factor = Pricing.SalvageResourceFactor(recipe, t.Tier, item.DurabilityRaw, maxDurability, world.Economy);
        var resources = new List<(ItemTemplate, int)>();
        foreach (var r in recipe?.Resources ?? [])
        {
            var type = (r.Enchantment > 0 ? world.Items.Find($"{r.Name}@{r.Enchantment}") : null) ?? world.Items.Find(r.Name);
            if (type is not { CanCreate: true }) continue;
            var n = Pricing.SalvageResourceCount(r.Count, item.Count, factor, random);
            if (n > 0) resources.Add((type, n));
        }
        return (silver, resources);
    }

    /// <summary>The most resources salvaging <paramref name="items"/> can give (each rounded up), for the inventory check.</summary>
    private List<(ItemTemplate Type, int Count)> MostResources(IEnumerable<Item> items) =>
        [.. items.SelectMany(i => SalvageGains(i, null).Resources).GroupBy(r => r.Type).Select(g => (g.Key, g.Sum(r => r.Count)))];

    /// <summary>
    /// Crafting (ACT 1; stage 6 SPEC §6c), a shop purchase too: the item by key 7 (client index) or key 10 (uniquename), which
    /// must be in the building's craftingitemlist; recipe key 8 (default 0); amount key 9. Crafts = ceil(amount /
    /// amountcrafted); output = crafts × amountcrafted (C-C4). Price = recipe silver × crafts (a7m; gold recipes and other
    /// requirement kinds than resources → NotEnoughResources), the client's key 4 per <see cref="Pricing.Accepts"/> (else
    /// PriceChanged, 23) and in the wallet (6); resources crafts × count each in the inventory (8); the output must fit
    /// before anything is taken (14). A shop (isshop, the faction trader) or a recipe without time runs at once: NewBuilding
    /// key 16, UpdateMoney, the resources, the output (NewSimpleItem / NewEquipmentItem + InventoryPutItem, into a stack where
    /// it fits), CraftItemFinished {0 player, 1 building, 2 [output], 3 [], 4 []}, CraftBuildingInfo {0 building, 6 [], 7 []},
    /// NewBuilding, ActionOnBuildingFinished (the captured own order). At a station (not a shop) CraftingFocusUpdate follows the
    /// UpdateMoney (stageJ focus-lp SPEC §5.4): with the craft window's "use focus" (UseFlag 1, Focus:Crafting) and a recipe with
    /// craftingfocus, the whole batch's focus (<see cref="Focus.Cost"/> with the player's destiny board reduction) is spent at the start,
    /// partly when there is less (the efficiency bonus 0.59 × the share covered goes into the return bonus), key 2 = −used; without
    /// focus key 2 is left out, as captured. At a
    /// crafting station a batch uses <see cref="Pricing.CraftDuration"/>: silver and resources go at the start, each craft's
    /// output and returned resources arrive together when the batch timer ends, with one CraftItemFinished; a
    /// cancel gives back the silver and resources of the crafts not done. Equipment gets a rolled quality
    /// (<see cref="RollQuality"/>). Fame is granted once for the completed output when the batch finishes or is cancelled;
    /// shop purchases and dontgivefameoncraft items none. The nutrition fee is 0: ownerless stations send no fee data
    /// (SPEC §6c). A cancel gives back the focus of the crafts not done too [inference] (CraftingFocusUpdate +refund).
    /// </summary>
    private AlbionResponse? StartCraft(Player player, Building building, ActionOnBuildingStartRequest req)
    {
        AlbionResponse Refuse(CraftError e, string why) => StationRefused(player, req, StationComponentError, (byte)e, why);
        AlbionResponse Generic(string why) => StationRefused(player, req, StationGenericError, 0, why);
        var item = req.x is { Length: > 0 } name ? world.Items.Find(name) : world.Items[req.u];
        if (item is null) return Generic($"unknown item {req.x ?? req.u.ToString(CultureInfo.InvariantCulture)}");
        if (!building.Template.CraftItems.Contains(item.Name, StringComparer.OrdinalIgnoreCase)
            && !building.Template.CraftItems.Contains(item.BaseName, StringComparer.OrdinalIgnoreCase))
            return Generic($"{building.Template.Name} does not make {item.Name}");
        if (!item.CanCreate) return Generic($"{item.Name} is a {item.Kind}, which this server cannot create yet");
        if (req.v < 0 || req.v >= item.Recipes.Count) return Generic($"{item.Name} has no recipe {req.v}");
        var recipe = item.Recipes[req.v];
        if (req.w <= 0 || req.w > 9999) return Generic($"amount {req.w}");
        // StageL mists phase B: the City Trader's standing gates and FAVOR (Systems/Mists) are kept; every other kind is not yet.
        if (recipe.Gold > 0 || recipe.Unsupported.Any(u => !Systems.Mists.Supports(u) && !Systems.Factions.Supports(u)))
            return Refuse(CraftError.NotEnoughResources, $"the recipe needs {(recipe.Gold > 0 ? "gold" : string.Join(", ", recipe.Unsupported))}, not made here yet");
        var crafts = Pricing.Crafts(req.w, recipe.AmountCrafted);
        if (world.Mists.TraderRefusal(player, recipe, crafts) is { } gated) return Refuse(CraftError.NotEnoughResources, gated);
        if (world.Factions.ShopRefusal(player, recipe, crafts, building.Template) is { } faction) return Refuse(CraftError.NotEnoughResources, faction); // stageN faction SPEC §3
        var price = Pricing.CraftPrice(recipe, crafts);
        if (!Pricing.Accepts(req.r, price))
            return Refuse(CraftError.PriceChanged, string.Create(CultureInfo.InvariantCulture, $"the client's price {req.r / 10000.0:0.####} is not the server's {price / 10000.0:0.####}"));
        if (player.Character.Silver < req.r) return Refuse(CraftError.NotEnoughSilver, $"{player.Character.Silver / 10000} silver of {req.r / 10000}");
        var bag = player.Inventory.Bag;
        var needs = new List<(ItemTemplate Type, int Count)>();
        foreach (var r in recipe.Resources)
        {
            if (world.Items.Find(r.Name) is not { } type) return Refuse(CraftError.NotEnoughResources, $"unknown resource {r.Name}");
            var need = checked(r.Count * crafts);
            if (CountOf(bag, type) < need) return Refuse(CraftError.NotEnoughResources, $"{CountOf(bag, type)} of {need} x {r.Name}");
            needs.Add((type, need));
        }
        var output = crafts * recipe.AmountCrafted;
        if (!Fits(bag, item.IsEquipment ? Enumerable.Repeat((item, 1), output) : [(item, output)]))
            return Refuse(CraftError.InventoryFull, $"{output} x {item.Name} do not fit the inventory");

        var instant = building.Template.IsShop || recipe.Time <= 0;
        var now = world.GameNow;
        var timeReduction = player.Effects.Sum("crafttime", now);
        long Due(int completed) => checked(now + Pricing.CraftDuration(recipe, completed, timeReduction).Ticks);
        var action = new BuildingAction(building, req.p, req.q, now, instant ? now : Due(crafts));
        Systems.Mounts.BeforeAction(player, "crafts"); // stageD mounts SPEC §4.3: an accepted station action dismounts first
        player.Action = action;
        SendEvent(building.NewBuilding(inUse: true));                                          // 1.
        Wallet.TrySpend(player, req.r, $"{crafts} x {item.Name} at {building.Template.Name}"); // 2. UpdateMoney
        world.Mists.PayFavor(player, recipe, crafts); // stageL mists phase B: the City Trader's FAVOR
        world.Factions.Pay(player, recipe, crafts);   // stageN faction SPEC §3: the faction trader's FACTION_* points

        double focusUsed = 0, focusShare = 0;
        if (!building.Template.IsShop)
        {
            Focus.Settle(player, now);
            // Focus buys only return bonus: with Crafting:Returns off (or Rates:CraftingReturn 0) none is spent.
            if (world.Config.FocusCrafting && world.Config.CraftingReturns && world.Rates.CraftingReturn > 0 && player.Character.UseFlag(1) && recipe.Focus > 0)
                (focusUsed, focusShare) = Focus.Spend(player, Focus.Cost(recipe, crafts, Focus.Reduction(player, item, now), world.Economy.FocusCostConstant), now);
            Focus.Send(player, now, -focusUsed);                                               // 3. CraftingFocusUpdate
        }
        foreach (var (type, need) in needs) TakeItems(player, type, need);                     // 4. the resources
        // The quality bonus (destiny board itemcraftquality + focus), fixed for the batch (enchanting SPEC §3).
        var qualityBonus = item.IsEquipment && !building.Template.IsShop ? Focus.QualityBonus(player, item, now, focusShare) : 0;
        if (!instant) StartedToObservers(player, action);
        // The return bonus: the city's and the category's, + ActionFocus CraftingEfficiency × the share of the focus cost covered.
        var returns = !building.Template.IsShop && world.Config.CraftingReturns
            ? Math.Min(1, Pricing.ReturnFraction(world.Crafting.Bonus(player.Map!.Cluster, item.Attrs.GetValueOrDefault("craftingcategory", ""))
                + world.Economy.FocusEfficiency * focusShare) * world.Rates.CraftingReturn) : 0; // × Rates:CraftingReturn
        Log.Info(Name, string.Create(CultureInfo.InvariantCulture,
            $"'{player.Character.Name}' crafts {output} x {item.Name} ({crafts} craft(s), recipe {req.v}) at {building.Template.Name} for {req.r / 10000.0:0.####} silver{(focusUsed > 0 ? $", {focusUsed:0} focus" : "")}{(returns > 0 ? $", {returns * 100:0.#} % of the resources back" : "")}{(instant ? "" : $", {Pricing.CraftDuration(recipe, crafts, timeReduction).TotalSeconds:0.####} s")}"));
        var fames = !building.Template.IsShop && item.Attrs.GetValueOrDefault("dontgivefameoncraft") != "true";
        var done = 0;
        var outputSettled = false;
        void SettleOutput()
        {
            if (outputSettled || done == 0) return;
            outputSettled = true;
            var made = new List<Item>();
            for (var n = 0; n < (item.IsEquipment ? done * recipe.AmountCrafted : 1); n++)
                made.AddRange(GiveItem(player, item, item.IsEquipment ? 1 : done * recipe.AmountCrafted,
                    item.IsEquipment ? RollQuality(world.CraftRandom, world.Economy.QualityWeights, qualityBonus) : 1));
            if (returns > 0) GiveReturns(player, recipe, done, returns);
            if (fames) AwardCraftFame(player, item, checked(done * recipe.AmountCrafted));
            var finished = new CraftItemFinishedEvent { ddn = player.Id, b1d = building.Id, b1e = [.. made.Select(i => i.Id).Distinct()], b1f = [], b1g = [] };
            SendEvent(finished);
            if (!instant) player.SendToObservers(finished);
        }
        action.OnCancel = () =>
        {
            SettleOutput();
            var left = crafts - done;
            if (left <= 0) return;
            foreach (var r in recipe.Resources)
                if (world.Items.Find(r.Name) is { } type && Fits(player.Inventory.Bag, [(type, r.Count * left)])) GiveItem(player, type, r.Count * left);
            Wallet.Credit(player, req.r * left / crafts, $"{left} craft(s) of {item.Name} cancelled");
            Focus.Refund(player, focusUsed * left / crafts, world.GameNow);
        };
        void Craft()
        {
            if (action.Over || player.Action != action || player != Player) return;
            done = crafts;
            SettleOutput();
            action.Over = true;
            player.Action = null;
            SendEvent(new CraftBuildingInfoEvent { ddn = building.Id }.FillEmpty());             // 7. {0 building, 6 [], 7 []}
            SendEvent(building.NewBuilding());                                                   // 8.
            Finished(player, action);                                                            // 9.
            player.SaveItems();
        }
        action.BeforeCancel = () =>
        {
            // A move can arrive after the client's bar ends but before the next world tick.
            // Produce the elapsed portion first; if all units are due, this is a finish, not a cancel.
            while (done < crafts && world.GameNow >= Due(done + 1)) done++;
            if (done == crafts) Craft();
        };
        if (instant) Craft();
        else player.Map!.Timers.At(world.ClockAt(Due(crafts)), Craft);
        return null;
    }

    /// <summary>
    /// The resources one batch of <paramref name="crafts"/> crafts gives back: per resource count × crafts × the fraction,
    /// floored, with the remainder carried per resource on the player (<see cref="Player.ReturnCarry"/>), at most its
    /// maxreturnamount per craft (0 = never); what does not fit the inventory is not given.
    /// </summary>
    private void GiveReturns(Player player, CraftRecipe recipe, int crafts, double fraction)
    {
        foreach (var r in recipe.Resources)
        {
            if (world.Items.Find(r.Name) is not { } type) continue;
            var (n, carry) = Pricing.Returned(r, crafts, fraction, player.ReturnCarry.GetValueOrDefault(r.Name));
            player.ReturnCarry[r.Name] = carry;
            if (n > 0 && Fits(player.Inventory.Bag, [(type, n)])) GiveItem(player, type, n);
        }
    }

    /// <summary>
    /// The fame of <paramref name="count"/> crafted <paramref name="item"/>s (<see cref="Pricing.CraftFame"/>: itemvalue ×
    /// count × 1.5^(tier + enchantment − 2), × Rates:Fame × Rates:CraftingFame) to the total and, × the item's destinyandjournalcraftfamefactor (1.1-1.4 on
    /// some items, else 1; assumed to scale only the board's share, as its name says), to the destiny board's craftitemfame
    /// nodes of the item (<see cref="Destiny.Craft"/>: CRAFT_TRAINEE, CRAFT_REFINE_*_Tn, CRAFT_SWORDS, …) and the fameearned
    /// ones. Not captured: where official puts the UpdateFame in the burst is open; here it follows the crafted items.
    /// </summary>
    private void AwardCraftFame(Player player, ItemTemplate item, int count)
    {
        var fame = Rates.ScaleFame(Pricing.CraftFame(world.ItemValues.Of(item), item.Tier, item.Enchantment, count, world.Economy), world.Rates.Fame * world.Rates.CraftingFame);
        if (fame <= 0) return;
        var factor = double.TryParse(item.Attrs.GetValueOrDefault("destinyandjournalcraftfamefactor"), NumberStyles.Float, CultureInfo.InvariantCulture, out var f) && f > 0 ? f : 1;
        Destiny.Award(player, fame, Destiny.Craft(item), boardGain: (long)Math.Floor(fame * (decimal)factor), source: item);
    }

    /// <summary>
    /// A quality 1-5 by gamedata CraftingQualityChances (689/250/50/10/1 of 1000), with <paramref name="bonus"/> quality points
    /// (<see cref="Focus.QualityBonus"/>): every weight above normal × (1 + bonus / 100) [assumption, enchanting SPEC §3: the
    /// official server formula is not in the client]. Without a bonus the plain integer roll.
    /// </summary>
    public static int RollQuality(Random random, IReadOnlyList<int> weights, double bonus = 0)
    {
        if (bonus <= 0)
        {
            var roll = random.Next(Math.Max(1, weights.Sum()));
            for (var q = 0; q < weights.Count; q++)
                if ((roll -= weights[q]) < 0) return q + 1;
            return 1;
        }
        var scaled = weights.Select((w, q) => q == 0 ? w : w * (1 + bonus / 100)).ToArray();
        var r = random.NextDouble() * scaled.Sum();
        for (var q = 0; q < scaled.Length; q++)
            if ((r -= scaled[q]) < 0) return q + 1;
        return scaled.Length;
    }

    /// <summary>The quality a reroll of quality <paramref name="quality"/> gives, by gamedata RerollQualityChances; null = none to roll (5, or a quality the table lacks).</summary>
    public static int? RollReroll(Random random, IReadOnlyList<IReadOnlyList<int>> table, int quality)
    {
        if (quality < 1 || quality > table.Count || table[quality - 1].Sum() <= 0) return null;
        var weights = table[quality - 1];
        var roll = random.Next(weights.Sum());
        for (var q = 0; q < weights.Count; q++)
            if ((roll -= weights[q]) < 0) return Math.Max(quality, q + 1);
        return quality;
    }

    /// <summary>ActionOnBuildingStart (64) {0 actor, 1 building, 2 start, 3 end, 4 building type id, 5 ACT} to the players who know the actor.</summary>
    private static void StartedToObservers(Player player, BuildingAction a) =>
        player.SendToObservers(new ActionOnBuildingStartEvent { ddn = player.Id, brl = a.Building.Id, brm = a.Start, brn = a.End, bro = a.Building.Template.Id, brp = a.Act });

    /// <summary>ActionOnBuildingFinished (66) {0 actor, 1 end, 2 building, 3 type id, 4 ACT} to the actor and the players who know it.</summary>
    private void Finished(Player player, BuildingAction a) =>
        SendAround(player, new ActionOnBuildingFinishedEvent { ddn = player.Id, brh = a.End, bri = a.Building.Id, brj = a.Building.Template.Id, brk = a.Act });

    /// <summary>
    /// Ends the player's station action before its end (a move, a cast, logout): its refund (<see cref="BuildingAction.OnCancel"/>),
    /// the building's NewBuilding without key 16, and ActionOnBuildingCancel (65) {0 actor, 1 ACT} to the actor (when
    /// <paramref name="tellClient"/>) and its observers. Nothing is charged for an unfinished repair.
    /// </summary>
    internal void CancelAction(Player player, string why, bool tellClient)
    {
        if (player.Action is not { Over: false } a) return;
        a.BeforeCancel?.Invoke();
        if (a.Over || player.Action != a) return;
        a.Over = true;
        player.Action = null;
        a.OnCancel?.Invoke();
        var cancel = new ActionOnBuildingCancelEvent { ddn = player.Id, brg = a.Act };
        if (tellClient)
        {
            SendEvent(a.Building.NewBuilding());
            SendEvent(cancel);
        }
        player.SendToObservers(cancel);
        player.SaveItems();
        Log.Info(Name, $"'{player.Character.Name}' station action {(StationAction)a.Act} at {a.Building.Template.Name} cancelled: {why}");
    }
}
