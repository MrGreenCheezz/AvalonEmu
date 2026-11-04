namespace AlbionLocal.Modules;

/// <summary>An equipment slot of a player (<see cref="IPlayer.Worn"/>, <see cref="ItemType.Slot"/>).</summary>
public enum EquipSlot
{
    /// <summary>Not a slot: the item is not worn.</summary>
    None = -1,
    /// <summary>The main hand: a weapon or a gathering tool (two-handed ones too).</summary>
    MainHand = 0,
    /// <summary>The off hand: a shield, a book, a torch.</summary>
    OffHand = 1,
    /// <summary>The helmet.</summary>
    Head = 2,
    /// <summary>The armour.</summary>
    Armor = 3,
    /// <summary>The shoes.</summary>
    Shoes = 4,
    /// <summary>The bag.</summary>
    Bag = 5,
    /// <summary>The cape.</summary>
    Cape = 6,
    /// <summary>The mount.</summary>
    Mount = 7,
    /// <summary>The potion.</summary>
    Potion = 8,
    /// <summary>The food.</summary>
    Food = 9,
}

/// <summary>An item's quality. The values compare: <c>item.Quality &gt;= ItemQuality.Excellent</c>.</summary>
public enum ItemQuality
{
    /// <summary>Normal.</summary>
    Normal = 1,
    /// <summary>Good.</summary>
    Good = 2,
    /// <summary>Outstanding.</summary>
    Outstanding = 3,
    /// <summary>Excellent.</summary>
    Excellent = 4,
    /// <summary>Masterpiece.</summary>
    Masterpiece = 5,
}

/// <summary>What an item is, as the market groups it (items.xml shopcategory).</summary>
public enum ItemCategory
{
    /// <summary>Anything else: quest items, tokens, maps, trophies, labourers.</summary>
    Other,
    /// <summary>A weapon (<see cref="ItemType.Weapon"/>).</summary>
    Weapon,
    /// <summary>A shield, book or torch (<see cref="ItemType.OffHand"/>).</summary>
    OffHand,
    /// <summary>A helmet (<see cref="ItemType.Armor"/>).</summary>
    Head,
    /// <summary>An armour (<see cref="ItemType.Armor"/>).</summary>
    Armor,
    /// <summary>Shoes (<see cref="ItemType.Armor"/>).</summary>
    Shoes,
    /// <summary>A bag.</summary>
    Bag,
    /// <summary>A cape.</summary>
    Cape,
    /// <summary>A mount.</summary>
    Mount,
    /// <summary>Food, potions, tomes, silver bags (<see cref="ItemType.Consumable"/>).</summary>
    Consumable,
    /// <summary>Raw and refined resources, fish, alchemy drops.</summary>
    Resource,
    /// <summary>Gathering tools (<see cref="ItemType.Tool"/>) and gathering gear such as baits.</summary>
    Gathering,
    /// <summary>Seeds, animals, farm products.</summary>
    Farming,
    /// <summary>Furniture and repair kits.</summary>
    Furniture,
    /// <summary>Artefacts and runes, the materials of artefact gear.</summary>
    Artefact,
    /// <summary>Skins and other looks.</summary>
    Vanity,
}

/// <summary>A weapon's line (items.xml shopsubcategory1). Claymores are swords, longbows are bows: the whole tree of the destiny board.</summary>
public enum WeaponType
{
    /// <summary>Not a weapon.</summary>
    None,
    /// <summary>Swords.</summary>
    Sword,
    /// <summary>Axes.</summary>
    Axe,
    /// <summary>Maces.</summary>
    Mace,
    /// <summary>Hammers.</summary>
    Hammer,
    /// <summary>Spears.</summary>
    Spear,
    /// <summary>Daggers.</summary>
    Dagger,
    /// <summary>Quarterstaffs.</summary>
    Quarterstaff,
    /// <summary>War gloves.</summary>
    WarGloves,
    /// <summary>Bows.</summary>
    Bow,
    /// <summary>Crossbows.</summary>
    Crossbow,
    /// <summary>Fire staffs.</summary>
    FireStaff,
    /// <summary>Frost staffs.</summary>
    FrostStaff,
    /// <summary>Holy staffs.</summary>
    HolyStaff,
    /// <summary>Nature staffs.</summary>
    NatureStaff,
    /// <summary>Arcane staffs.</summary>
    ArcaneStaff,
    /// <summary>Cursed staffs.</summary>
    CursedStaff,
    /// <summary>Shapeshifter staffs.</summary>
    ShapeshifterStaff,
    /// <summary>A weapon of a line this list does not have yet: see <see cref="ItemType.Subcategory"/>.</summary>
    Other,
}

/// <summary>What a helmet, armour or pair of shoes is made of.</summary>
public enum ArmorType
{
    /// <summary>Not a helmet, armour or shoes.</summary>
    None,
    /// <summary>Cloth.</summary>
    Cloth,
    /// <summary>Leather.</summary>
    Leather,
    /// <summary>Plate.</summary>
    Plate,
    /// <summary>Something else, such as a unique look.</summary>
    Other,
}

/// <summary>What an off-hand item is.</summary>
public enum OffHandType
{
    /// <summary>Not an off-hand item.</summary>
    None,
    /// <summary>A shield.</summary>
    Shield,
    /// <summary>A book.</summary>
    Book,
    /// <summary>A torch.</summary>
    Torch,
    /// <summary>Something else.</summary>
    Other,
}

/// <summary>What a gathering tool is for.</summary>
public enum ToolType
{
    /// <summary>Not a gathering tool.</summary>
    None,
    /// <summary>A sickle, for fibre.</summary>
    Fiber,
    /// <summary>A skinning knife, for hide.</summary>
    Hide,
    /// <summary>A pickaxe, for ore.</summary>
    Ore,
    /// <summary>A stone hammer, for rock.</summary>
    Rock,
    /// <summary>A wood axe, for wood.</summary>
    Wood,
    /// <summary>A fishing rod.</summary>
    Fishing,
    /// <summary>A tracking toolkit.</summary>
    Tracking,
    /// <summary>Something else.</summary>
    Other,
}

/// <summary>What a consumable is.</summary>
public enum ConsumableType
{
    /// <summary>Not a consumable.</summary>
    None,
    /// <summary>Food.</summary>
    Food,
    /// <summary>A potion.</summary>
    Potion,
    /// <summary>A tome.</summary>
    Tome,
    /// <summary>A silver bag.</summary>
    SilverBag,
    /// <summary>Something else.</summary>
    Other,
}

/// <summary>
/// A kind of item from items.xml: everything about it without reading its name. Every hook and <see cref="IServer.Item(string)"/> hand these out. Where the API
/// asks for an item's name as a string, an <see cref="ItemType"/> goes as it is: <c>player.GiveItem(e.Item)</c>. Two are equal when they are the same kind (the same
/// <see cref="Name"/>), whatever the quality or the count of an <see cref="ItemInfo"/>.
/// </summary>
/// <example>
/// <code><![CDATA[
/// if (e.Item.Weapon == WeaponType.Sword && e.Item.Tier >= 6) ...
/// if (player.MainHand is { Weapon: WeaponType.Bow, IsTwoHanded: true }) ...
/// if (e.Item.Is("T*_MAIN_SWORD")) ...   // a name or a mask, for the rare case that a type does not say it
/// ]]></code>
/// </example>
public class ItemType : IEquatable<ItemType>
{
    internal ItemType() { }

    private protected ItemType(ItemType type)
    {
        Name = type.Name;
        BaseName = type.BaseName;
        Family = type.Family;
        Tier = type.Tier;
        Enchantment = type.Enchantment;
        Slot = type.Slot;
        Category = type.Category;
        Subcategory = type.Subcategory;
        Weapon = type.Weapon;
        Armor = type.Armor;
        OffHand = type.OffHand;
        Tool = type.Tool;
        Consumable = type.Consumable;
        IsTwoHanded = type.IsTwoHanded;
        ItemPower = type.ItemPower;
        MaxStack = type.MaxStack;
        MaxQuality = type.MaxQuality;
        MaxDurability = type.MaxDurability;
        _attributes = type._attributes;
    }

    private readonly IReadOnlyDictionary<string, string>? _attributes;

    /// <summary>The whole uniquename, with "@n" for an enchantment: "T4_MAIN_SWORD@1".</summary>
    public string Name { get; internal init; } = "";

    /// <summary>The uniquename without the enchantment: "T4_MAIN_SWORD".</summary>
    public string BaseName { get; internal init; } = "";

    /// <summary>The uniquename without tier and enchantment, the same for every tier of an item: "MAIN_SWORD".</summary>
    public string Family { get; internal init; } = "";

    /// <summary>The tier (1-8).</summary>
    public int Tier { get; internal init; }

    /// <summary>The enchantment: 1 for "@1", the level of an enchanted resource; 0 for none.</summary>
    public int Enchantment { get; internal init; }

    /// <summary>The equipment slot it goes into; <see cref="EquipSlot.None"/> when it cannot be worn.</summary>
    public EquipSlot Slot { get; internal init; } = EquipSlot.None;

    /// <summary>What it is, as the market groups it.</summary>
    public ItemCategory Category { get; internal init; }

    /// <summary>items.xml shopsubcategory1 as it is ("kennel", "pasture", …), for what the types here do not cover; "" when none.</summary>
    public string Subcategory { get; internal init; } = "";

    /// <summary>The weapon's line; <see cref="WeaponType.None"/> when it is not a weapon.</summary>
    public WeaponType Weapon { get; internal init; }

    /// <summary>What a helmet, armour or shoes are made of; <see cref="ArmorType.None"/> for anything else.</summary>
    public ArmorType Armor { get; internal init; }

    /// <summary>What an off-hand item is; <see cref="OffHandType.None"/> for anything else.</summary>
    public OffHandType OffHand { get; internal init; }

    /// <summary>What a gathering tool is for; <see cref="ToolType.None"/> for anything else.</summary>
    public ToolType Tool { get; internal init; }

    /// <summary>What a consumable is; <see cref="ConsumableType.None"/> for anything else.</summary>
    public ConsumableType Consumable { get; internal init; }

    /// <summary>A weapon or tool that takes both hands.</summary>
    public bool IsTwoHanded { get; internal init; }

    /// <summary>The base item power of items.xml, for this enchantment.</summary>
    public double ItemPower { get; internal init; }

    /// <summary>The most of it in one stack.</summary>
    public int MaxStack { get; internal init; }

    /// <summary>The highest quality it can have.</summary>
    public ItemQuality MaxQuality { get; internal init; } = ItemQuality.Normal;

    /// <summary>The durability when new, in items.xml units; 0 when it has none.</summary>
    public double MaxDurability { get; internal init; }

    /// <summary>A weapon.</summary>
    public bool IsWeapon => Weapon != WeaponType.None;

    /// <summary>A helmet, armour or shoes.</summary>
    public bool IsArmor => Armor != ArmorType.None;

    /// <summary>A gathering tool.</summary>
    public bool IsTool => Tool != ToolType.None;

    /// <summary>Whether it is <paramref name="nameOrMask"/>: a uniquename with or without "@n" ("T4_MAIN_SWORD" is every enchantment of it, "T4_MAIN_SWORD@1" only that one), or a mask with * and ? ("T*_MAIN_SWORD"). Case does not matter.</summary>
    public bool Is(string nameOrMask) => NameMask.Matches(BaseName, nameOrMask) || NameMask.Matches(Name, nameOrMask);

    /// <summary>The raw items.xml attribute <paramref name="name"/> ("attackdamage", "unlockedtocraft", …), for what no property here covers; null when it has none.</summary>
    public string? Attribute(string name) => _attributes?.GetValueOrDefault(name);

    internal IReadOnlyDictionary<string, string>? Attributes
    {
        init => _attributes = value;
    }

    /// <summary>The uniquename.</summary>
    public sealed override string ToString() => Name;

    /// <summary>The same kind of item: the same <see cref="Name"/>.</summary>
    public bool Equals(ItemType? other) => other is not null && Name == other.Name;

    /// <summary>The same kind of item: the same <see cref="Name"/>.</summary>
    public sealed override bool Equals(object? obj) => Equals(obj as ItemType);

    /// <summary>The hash of <see cref="Name"/>.</summary>
    public sealed override int GetHashCode() => Name.GetHashCode();

    /// <summary>The same kind of item.</summary>
    public static bool operator ==(ItemType? a, ItemType? b) => a is null ? b is null : a.Equals(b);

    /// <summary>Not the same kind of item.</summary>
    public static bool operator !=(ItemType? a, ItemType? b) => !(a == b);

    /// <summary>The uniquename, so that an item goes wherever the API takes an item's name. Null is "", which finds nothing.</summary>
    public static implicit operator string(ItemType? item) => item?.Name ?? "";
}

/// <summary>One real item: a kind of item (all of <see cref="ItemType"/>) with its count, quality and durability, as it is at the moment it was read.</summary>
public sealed class ItemInfo : ItemType
{
    internal ItemInfo(ItemType type, int count, ItemQuality quality, double durability) : base(type) => (Count, Quality, Durability) = (count, quality, durability);

    /// <summary>How many are in the stack.</summary>
    public int Count { get; }

    /// <summary>The quality.</summary>
    public ItemQuality Quality { get; }

    /// <summary>The durability left, in items.xml units.</summary>
    public double Durability { get; }

    /// <summary>It has no durability left.</summary>
    public bool IsBroken => MaxDurability > 0 && Durability <= 0;
}

/// <summary>One loot entry: the kind of item and how many.</summary>
/// <param name="Item">The kind of item.</param>
/// <param name="Count">How many.</param>
public sealed record LootItem(ItemType Item, int Count);

/// <summary>The rolled items of a loot hook. It is a list, to change as any list: <c>e.Items.Add("T4_ORE", 3)</c>, <c>e.Items.RemoveAll(i => i.Item.Category == ItemCategory.Resource)</c>.</summary>
public sealed class LootList : List<LootItem>
{
    private readonly Func<string, ItemType?> _find;

    internal LootList(IEnumerable<LootItem> items, Func<string, ItemType?> find) : base(items) => _find = find;

    /// <summary>Adds <paramref name="count"/> × <paramref name="item"/> (a uniquename, "@n" for an enchantment); false, with a hint in the log, when items.xml has no such item.</summary>
    public bool Add(string item, int count = 1)
    {
        if (_find(item) is not { } type) return false;
        Add(new LootItem(type, count));
        return true;
    }
}

/// <summary>Whole-name matching with * and ?, ignoring case: the rule of hook ids, <see cref="ItemType.Is"/> and the like.</summary>
internal static class NameMask
{
    public static bool Matches(string name, string mask)
    {
        int n = 0, m = 0, star = -1, back = 0;
        while (n < name.Length)
        {
            if (m < mask.Length && (mask[m] == '?' || char.ToUpperInvariant(mask[m]) == char.ToUpperInvariant(name[n])))
            {
                n++;
                m++;
            }
            else if (m < mask.Length && mask[m] == '*')
            {
                star = m++;
                back = n;
            }
            else if (star >= 0)
            {
                m = star + 1;
                n = ++back;
            }
            else return false;
        }
        while (m < mask.Length && mask[m] == '*') m++;
        return m == mask.Length;
    }
}
