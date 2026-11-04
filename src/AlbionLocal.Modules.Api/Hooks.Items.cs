namespace AlbionLocal.Modules;

/// <summary>Equipment, consumables, crafting, gathering. Id = the item's uniquename, except where a hook says otherwise. Bots do not fire these.</summary>
public interface IItemHooks
{
    /// <summary>An item went into an equipment slot (also every worn item at a login: <see cref="EquipArgs.AtLogin"/>).</summary>
    IHookRegistration OnEquip(Action<EquipArgs> handler, HookPriority priority = HookPriority.Normal);
    /// <summary>As OnEquip, for one item (its uniquename, or a mask with * and ?). The id has no "@n" enchantment suffix.</summary>
    IHookRegistration OnEquip(string item, Action<EquipArgs> handler, HookPriority priority = HookPriority.Normal);
    /// <summary>An item left an equipment slot.</summary>
    IHookRegistration OnUnequip(Action<EquipArgs> handler, HookPriority priority = HookPriority.Normal);
    /// <summary>As OnUnequip, for one item (its uniquename, or a mask with * and ?). The id has no "@n" enchantment suffix.</summary>
    IHookRegistration OnUnequip(string item, Action<EquipArgs> handler, HookPriority priority = HookPriority.Normal);

    /// <summary>The main-hand weapon changed (id = the new weapon; "" when the hand is empty now).</summary>
    IHookRegistration OnWeaponSwap(Action<WeaponSwapArgs> handler, HookPriority priority = HookPriority.Normal);
    /// <summary>As OnWeaponSwap, for one new main-hand weapon (its uniquename without "@n", or a mask with * and ?; "" for an empty hand).</summary>
    IHookRegistration OnWeaponSwap(string item, Action<WeaponSwapArgs> handler, HookPriority priority = HookPriority.Normal);

    /// <summary>An item's durability reached 0 (death and knockdown wear, a gathering tool, a fishing rod).</summary>
    IHookRegistration OnBreak(Action<ItemBreakArgs> handler, HookPriority priority = HookPriority.Normal);
    /// <summary>As OnBreak, for one item (its uniquename, or a mask with * and ?). The id has no "@n" enchantment suffix.</summary>
    IHookRegistration OnBreak(string item, Action<ItemBreakArgs> handler, HookPriority priority = HookPriority.Normal);

    /// <summary>
    /// The worn items' passives are worked out again (the equipment changed): add stat modifiers with <see cref="PassiveArgs.Add"/>
    /// (pick one from <see cref="Stat"/>: <c>Stat.AttackDamage</c>, <c>Stat.MagicSpellDamage</c>, …). A filtered hook runs
    /// when that item is worn.
    /// </summary>
    IHookRegistration OnCalcPassives(Action<PassiveArgs> handler, HookPriority priority = HookPriority.Normal);
    /// <summary>As OnCalcPassives, for one item (its uniquename, or a mask with * and ?). It runs when that item is worn; the id has no "@n" enchantment suffix.</summary>
    IHookRegistration OnCalcPassives(string item, Action<PassiveArgs> handler, HookPriority priority = HookPriority.Normal);

    /// <summary>A consumable was used: one use from the inventory (potions, food, chests, maps…) or the potion/food slot. Id = the item.</summary>
    IHookRegistration OnUsed(Action<ItemUsedArgs> handler, HookPriority priority = HookPriority.Normal);
    /// <summary>As OnUsed, for one item (its uniquename, or a mask with * and ?).</summary>
    IHookRegistration OnUsed(string item, Action<ItemUsedArgs> handler, HookPriority priority = HookPriority.Normal);

    /// <summary>Items were crafted at a station (or bought at an NPC shop, which is a craft), per batch. Id = the item.</summary>
    IHookRegistration OnCrafted(Action<ItemCraftedArgs> handler, HookPriority priority = HookPriority.Normal);
    /// <summary>As OnCrafted, for one item (its uniquename, or a mask with * and ?).</summary>
    IHookRegistration OnCrafted(string item, Action<ItemCraftedArgs> handler, HookPriority priority = HookPriority.Normal);

    /// <summary>A harvest (gathering or a fishing catch) gave items. Id = the item.</summary>
    IHookRegistration OnHarvested(Action<HarvestArgs> handler, HookPriority priority = HookPriority.Normal);
    /// <summary>As OnHarvested, for one item (its uniquename, or a mask with * and ?).</summary>
    IHookRegistration OnHarvested(string item, Action<HarvestArgs> handler, HookPriority priority = HookPriority.Normal);

    /// <summary>A station action was asked for (craft, repair, salvage, enchant, reroll, awaken), with whether the server accepted it. Id = the station's building.</summary>
    IHookRegistration OnStationAction(Action<BuildingActionArgs> handler, HookPriority priority = HookPriority.Normal);
    /// <summary>As OnStationAction, for one station (its building uniquename, or a mask with * and ?).</summary>
    IHookRegistration OnStationAction(string building, Action<BuildingActionArgs> handler, HookPriority priority = HookPriority.Normal);

    /// <summary>A player is about to take an item out of a loot container (a mob's bag, a dead player's bag, a chest): <see cref="CancellablePlayerArgs.Cancel"/> leaves it there, the reason going to the player. Per item of a take-all. Silver piles are not items. Id = the item.</summary>
    IHookRegistration OnBeforeLootTake(Action<BeforeLootTakeArgs> handler, HookPriority priority = HookPriority.Normal);
    /// <summary>As OnBeforeLootTake, for one item (its uniquename, or a mask with * and ?). The id has the "@n" enchantment suffix when the item has one.</summary>
    IHookRegistration OnBeforeLootTake(string item, Action<BeforeLootTakeArgs> handler, HookPriority priority = HookPriority.Normal);

    /// <summary>A loot chest's loot was rolled at its first opening (per player on a personal chest), before its items are made: <see cref="ChestLootArgs.Items"/> may be changed (add, remove). Id = the chest.</summary>
    IHookRegistration OnChestLootRolled(Action<ChestLootArgs> handler, HookPriority priority = HookPriority.Normal);
    /// <summary>As OnChestLootRolled, for one chest (its uniquename, or a mask with * and ?).</summary>
    IHookRegistration OnChestLootRolled(string chest, Action<ChestLootArgs> handler, HookPriority priority = HookPriority.Normal);
}

/// <summary>An item went into or left an equipment slot (OnEquip, OnUnequip).</summary>
public sealed class EquipArgs(IPlayer player, ItemInfo item, bool atLogin) : PlayerArgs(player)
{
    /// <summary>The item, with its tier, weapon type, quality and so on.</summary>
    public ItemInfo Item { get; } = item;

    /// <summary>The equipment slot.</summary>
    public EquipSlot Slot => Item.Slot;

    /// <summary>It fires because the character is logging in with this item worn; the player did not just put it on.</summary>
    public bool AtLogin { get; } = atLogin;
}

/// <summary>The weapon or tool in the main hand changed (OnWeaponSwap).</summary>
public sealed class WeaponSwapArgs(IPlayer player, ItemType? from, ItemInfo? to) : PlayerArgs(player)
{
    /// <summary>What was in the hand before; null when it was empty.</summary>
    public ItemType? From { get; } = from;
    /// <summary>What is in the hand now; null when it is empty now.</summary>
    public ItemInfo? To { get; } = to;
}

/// <summary>What wore an item out.</summary>
public enum BreakCause
{
    /// <summary>The player died.</summary>
    Death,
    /// <summary>The player was knocked down.</summary>
    Knockdown,
    /// <summary>Gathering.</summary>
    Gathering,
    /// <summary>Fishing.</summary>
    Fishing,
}

/// <summary>An item's durability reached 0 (OnBreak).</summary>
public sealed class ItemBreakArgs(IPlayer player, ItemInfo item, BreakCause cause) : PlayerArgs(player)
{
    /// <summary>The item.</summary>
    public ItemInfo Item { get; } = item;

    /// <summary>What wore it out.</summary>
    public BreakCause Cause { get; } = cause;
}

/// <summary>The worn items' passives are being worked out (OnCalcPassives): add stat modifiers with <see cref="Add"/>.</summary>
public sealed class PassiveArgs(IPlayer player, IReadOnlyList<ItemInfo> worn, Action<string, double> add) : PlayerArgs(player)
{
    /// <summary>The worn items.</summary>
    public IReadOnlyList<ItemInfo> Worn { get; } = worn;

    /// <summary>The item the filtered hook runs for; null for a global one.</summary>
    public ItemInfo? Item { get; internal set; }

    /// <summary>Adds <paramref name="value"/> to stat <paramref name="attribute"/> (pick one from <see cref="Stat"/>) while this equipment is worn (summed with the passives).</summary>
    public void Add(string attribute, double value) => add(attribute, value);
}

/// <summary>A consumable was used (OnUsed).</summary>
public sealed class ItemUsedArgs(IPlayer player, ItemType item, bool fromSlot) : PlayerArgs(player)
{
    /// <summary>The item.</summary>
    public ItemType Item { get; } = item;

    /// <summary>It came from the potion or food slot; else from the inventory.</summary>
    public bool FromSlot { get; } = fromSlot;
}

/// <summary>Items were crafted (OnCrafted).</summary>
public sealed class ItemCraftedArgs(IPlayer player, ItemType item, int count, string station) : PlayerArgs(player)
{
    /// <summary>The item.</summary>
    public ItemType Item { get; } = item;

    /// <summary>How many the batch made.</summary>
    public int Count { get; } = count;

    /// <summary>The station building's uniquename.</summary>
    public string Station { get; } = station;
}

/// <summary>What loot is taken from.</summary>
public enum LootContainer
{
    /// <summary>A loot bag: a mob's, or a dead player's.</summary>
    Bag,
    /// <summary>A loot chest.</summary>
    Chest,
    /// <summary>Something else.</summary>
    Other,
}

/// <summary>A player is about to take an item out of a loot container (OnBeforeLootTake).</summary>
public sealed class BeforeLootTakeArgs(IPlayer player, ItemInfo item, int count, LootContainer container) : CancellablePlayerArgs(player)
{
    /// <summary>The item in the container.</summary>
    public ItemInfo Item { get; } = item;

    /// <summary>How many it asks for (what finds no room in its inventory still stays).</summary>
    public int Count { get; } = count;

    /// <summary>What it takes from.</summary>
    public LootContainer Container { get; } = container;
}

/// <summary>A chest's loot was rolled (OnChestLootRolled): change <see cref="Items"/> to change what is in it.</summary>
public sealed class ChestLootArgs(IPlayer player, string chest, string cluster, LootList items) : PlayerArgs(player)
{
    /// <summary>The chest's uniquename.</summary>
    public string Chest { get; } = chest;

    /// <summary>The cluster it stands in.</summary>
    public string Cluster { get; } = cluster;

    /// <summary>The rolled items; change the list to change the loot (<c>e.Items.Add("T4_ORE", 3)</c>; items the server cannot create are logged and left out).</summary>
    public LootList Items { get; } = items;
}

/// <summary>How a harvest was done.</summary>
public enum HarvestKind
{
    /// <summary>Gathering.</summary>
    Gather,
    /// <summary>A fishing catch.</summary>
    Fish,
}

/// <summary>A harvest gave items (OnHarvested).</summary>
public sealed class HarvestArgs(IPlayer player, ItemType item, int count, HarvestKind kind) : PlayerArgs(player)
{
    /// <summary>The item.</summary>
    public ItemType Item { get; } = item;
    /// <summary>How many it gave.</summary>
    public int Count { get; } = count;

    /// <summary>How it was done.</summary>
    public HarvestKind Kind { get; } = kind;
}

/// <summary>What a station is asked to do.</summary>
public enum StationActionKind
{
    /// <summary>One this list does not have yet.</summary>
    Other,
    /// <summary>Craft an item.</summary>
    CraftItem,
    /// <summary>Repair an item.</summary>
    RepairItem,
    /// <summary>Salvage an item.</summary>
    SalvageItem,
    /// <summary>Enchant an item.</summary>
    UpgradeEnchantment,
    /// <summary>Roll an item's quality again.</summary>
    RerollQuality,
    /// <summary>Awaken an item.</summary>
    Awaken,
}

/// <summary>A station action was asked for (OnStationAction).</summary>
public sealed class BuildingActionArgs(IPlayer player, string building, StationActionKind action, bool accepted) : PlayerArgs(player)
{
    /// <summary>The station's building uniquename.</summary>
    public string Building { get; } = building;

    /// <summary>What it was asked to do.</summary>
    public StationActionKind Action { get; } = action;

    /// <summary>Whether the server started it (false: refused, with a reason sent to the client).</summary>
    public bool Accepted { get; } = accepted;
}
