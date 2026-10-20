namespace AlbionLocal.Server.Data;

/// <summary>
/// One item variant of items.xml as the client numbers it (<see cref="ClientNumbering.Items"/>): a top-level item, or
/// one of its enchantments (NAME@N), which takes the item's attributes with the &lt;enchantment&gt;'s own over them
/// (itempower, durability, …). The hot fields are typed; <see cref="Attrs"/> holds every raw attribute, so a script or a
/// content override can read any of them without a loader change (ARCHITECTURE.md §5.1).
/// </summary>
public sealed class ItemTemplate
{
    /// <summary>Client item type id (NewSimpleItem/NewEquipmentItem key 1), from 1.</summary>
    public required short Index { get; init; }

    /// <summary>items.xml uniquename, with @N for an enchantment (T4_MAIN_SWORD@1).</summary>
    public required string Name { get; init; }

    /// <summary>The element name: equipmentitem, weapon, mount, simpleitem, consumableitem, furnitureitem, …</summary>
    public required string Kind { get; init; }

    /// <summary>slottype (mainhand, offhand, head, armor, shoes, bag, cape, mount, potion, food); "" = none.</summary>
    public string Slot { get; init; } = "";

    public int Tier { get; init; }

    /// <summary>enchantmentlevel: N for a NAME@N variant; resources carry their own (T4_ORE_LEVEL1 = 1); 0 = none.</summary>
    public int Enchantment { get; init; }

    /// <summary>durability in items.xml units; NewEquipmentItem key 8 carries it as a7m raw (x10000).</summary>
    public double MaxDurability { get; init; }

    /// <summary>itempower (the enchantment's for @N).</summary>
    public double ItemPower { get; init; }

    /// <summary>itempowerprogressiontype: key into the gamedata.xml hitpoints/energy shares.</summary>
    public string ProgressionType { get; init; } = "";

    public bool TwoHanded { get; init; }
    public double MasteryModifier { get; init; }
    public double HitPointsMax { get; init; }
    public double HitPointsRegeneration { get; init; }
    public double EnergyMax { get; init; }
    public double HitPointsRegenerationBonus { get; init; }
    public double EnergyRegenerationBonus { get; init; }
    public double MountHitPointsMax { get; init; }
    public double MountHitPointsRegeneration { get; init; }

    /// <summary>The &lt;mount&gt; numbers and buffs (stageD mounts SPEC §2); null for every other kind.</summary>
    public MountData? Mount { get; init; }

    public int ActiveSpellSlots { get; init; }
    public int PassiveSpellSlots { get; init; }

    /// <summary>
    /// Spell candidates by uniquename, one list per slot index (max of the active and passive slot counts), from
    /// &lt;craftingspelllist&gt; as the client builds them (bc9.b 0x1805fdcb0, bc9.a 0x1805fd300, bc9.ll 0x180600d50):
    /// craftspell slots="all" (or none) goes into every list, "1,2" into lists 0 and 1; reference="X" takes X's lists,
    /// as they are when the item has no entries and no removespell of its own, else appended to the own entries minus
    /// the removed names. Active slot i takes the active/toggle spells of list i, passive slot i the passive ones
    /// (<see cref="Game.SpellData"/>). Enchantments share the item's lists.
    /// </summary>
    public string[][] SpellLists { get; init; } = [];

    /// <summary>consumespell (the enchantment's own if it has one): the spell a potion or food casts; null = none.</summary>
    public string? ConsumeSpell { get; init; }

    /// <summary>unlockedtoequip="true": equippable without a destiny board unlock (T1 and a few specials, ctp.ahf 0x1809965e0).</summary>
    public bool UnlockedToEquip { get; init; }

    /// <summary>
    /// maxqualitylevel: the highest quality (1 normal ... 5 masterpiece) the item can have; 1 for 412 items (artifact and
    /// battle mounts, the drakes, ...), 5 for the other equipment. 5 when not given (no equipment kind, hand-made test tables).
    /// </summary>
    public int MaxQuality { get; init; } = 5;

    /// <summary>maxstacksize (999 for most stackable items); 0 = not given (equipment and the other unstackable kinds).</summary>
    public int MaxStackSize { get; init; }

    /// <summary>
    /// The resource types of the tool's &lt;canharvest resourcetype="WOOD"/&gt; children (axes WOOD, sickles FIBER, pickaxes
    /// ORE, stone hammers ROCK, skinning knives HIDE; an enchantment takes its item's); empty = no gathering tool. Matched
    /// against harvestables.xml Harvestable@resource (World/Harvesting.cs).
    /// </summary>
    public IReadOnlyList<string> CanHarvest { get; init; } = [];

    /// <summary>itemvalue (T2_FIBER 4, T3_FIBER 2, T4_FIBER 4, T1_WOOD 0): a gathering tool loses gamedata ToolUse × this × the amount (HARVEST.md §4).</summary>
    public double ItemValue { get; init; }

    /// <summary>famevalue (T2 1.5, T3 3, T4 7.5, T1 0): the gathering fame per item (not sent yet, HARVEST.md §4).</summary>
    public double FameValue { get; init; }

    /// <summary>
    /// The itemvalue attribute of this variant's own element (the &lt;enchantment&gt;'s for @N, never the item's inherited one);
    /// null = none, the value is then worked out from the first recipe (<see cref="Game.ItemValues"/>, bdi.d 0x1805F23E0).
    /// </summary>
    public double? OwnItemValue { get; init; }

    /// <summary>
    /// The &lt;craftingrequirements&gt; of this variant's own element in file order (an @N variant has its enchantment's, never
    /// the item's): the recipes a station crafts it by (op 55 key 8 = the index here) and what a shop sells it for.
    /// </summary>
    public IReadOnlyList<CraftRecipe> Recipes { get; init; } = [];

    /// <summary>
    /// The &lt;upgraderequirements&gt; of this variant's own &lt;enchantment&gt; element (enchanting SPEC, stageN/enchant): what
    /// upgrading one item of the level below into this one costs at a station (ACT 7), per item of the stack (T4_MAIN_SWORD@1:
    /// 288 T4_RUNE, @2 T4_SOUL, @3 T4_RELIC); null = the item cannot be upgraded into this level (base items, @4, most others).
    /// The client reads it from the target variant (cmn.al 0x180B74800: bdh.abo(item, enchantment + 1), its bdi+0x58).
    /// </summary>
    public UpgradeCost? Upgrade { get; init; }

    /// <summary>Every attribute of the element (the enchantment's over the item's for @N), raw.</summary>
    public IReadOnlyDictionary<string, string> Attrs { get; init; } = new Dictionary<string, string>();

    /// <summary>Unique name without the @N enchantment suffix.</summary>
    public string BaseName => Name.IndexOf('@') is var i and >= 0 ? Name[..i] : Name;

    /// <summary>
    /// Items the client creates with NewEquipmentItem (quality, durability, spells). StageN hunts: a trackingitem too (the tracking toolkit:
    /// its descriptor bty derives from the equipment one bc9, and c10.af 0x180A19EC0 makes it a c2k, a c1r equipment item).
    /// </summary>
    public bool IsEquipment => Kind is "equipmentitem" or "weapon" or "mount" or "transformationweapon" or "trackingitem";

    /// <summary>
    /// Items the client creates with NewSimpleItem: the kinds the official server sends that way (captures 103829,
    /// 113433, 114814). The client builds the object by its descriptor (c10.af 0x180a19ec0) and NewSimpleItem
    /// (cjb.bl 0x180aa8c90) casts it to c2i; furniture (c1v, sent as NewFurnitureItem 33 in the captures), journals (c17,
    /// NewJournalItem 35) and the c2g kinds are no c2i, so the cast throws and the item never appears.
    /// </summary>
    public bool IsSimple => Kind is "simpleitem" or "consumableitem" or "consumablefrominventoryitem" or "farmableitem";

    /// <summary>A furnitureitem (beds, tables, trophies, chests): sent with NewFurnitureItem (33, captures 103829/113433; the client's c1v is no c2i).</summary>
    public bool IsFurniture => Kind == "furnitureitem";

    /// <summary>A labourercontract (T2_LABOURER_CONTRACT_WOOD): sent with NewLaborerItem (36); placed, it hires its labourer (labourers SPEC §2).</summary>
    public bool IsLabourerContract => Kind == "labourercontract";

    /// <summary>
    /// The labourer journal data of a journalitem (stageH journals SPEC §A.1): the base item of items.xml (never used in play) and
    /// the _EMPTY and _FULL variants the client makes of it after the whole file; null for every other kind.
    /// </summary>
    public JournalData? Journal { get; init; }

    /// <summary>
    /// An _EMPTY or _FULL journal: sent with NewJournalItem (35, journals SPEC §A.2; the client's c17 is no c2i, so NewSimpleItem
    /// throws for it), stacks only at fame 0 or full. The base journalitem is not one (labourers sell _EMPTY and take _FULL).
    /// </summary>
    public bool IsJournal => Journal is { State: not JournalState.Base };

    /// <summary>The crop data of a plant farmableitem (a seed: its &lt;harvest&gt; and the focus watering, stageH islands SPEC §3); null for every other item.</summary>
    public FarmData? Farm { get; init; }

    /// <summary>The animal data of an animal farmableitem (a baby or a grown animal: growth, food, products; stageN mounts SPEC); null for every other item.</summary>
    public AnimalData? Animal { get; init; }

    /// <summary>A seed or an animal: it can be placed in a farm building and grows there.</summary>
    public bool IsFarmable => Farm is not null || Animal is not null;

    /// <summary>
    /// The position among the farmableitem and furnitureitem elements of items.xml, document order from 0 (T6_FARM_FOXGLOVE_SEED 12); -1 for
    /// every other item. Placed, the item is a building of type ClientNumbering:ItemBuildingBase + this (665; stageH islands SPEC §0.4).
    /// </summary>
    public int PlaceIndex { get; init; } = -1;

    /// <summary>Items this server can send (<see cref="IsEquipment"/>, <see cref="IsSimple"/>, <see cref="IsJournal"/>, <see cref="IsFurniture"/>, <see cref="IsLabourerContract"/>); the other kinds need their own New*Item event.</summary>
    public bool CanCreate => IsEquipment || IsSimple || IsJournal || IsFurniture || IsLabourerContract;

    /// <summary>Full durability as a7m raw (fixed point, 10000 = 1; NewEquipmentItem key 8, c1r.af(atl)).</summary>
    public long FullDurabilityRaw => (long)Math.Round(MaxDurability * 10000);

    public override string ToString() => $"{Name} (#{Index})";
}

/// <summary>
/// The &lt;mount&gt; attributes a mount item needs (analysis/work/stageD/mounts/SPEC.md §2; client copies cu8.f5 0x1809C55C0,
/// clo.aqt 0x180B58DB0): mounttime (3 or 10 s), dismounttime (0 everywhere: instant), remounttime (0.5 or 5 s) and remountdistance
/// (2 m) at the own mount object, halfmountrange, the forced-dismount and full-dismount cooldowns (30 s, one 90), and the spells
/// by uniquename: mountedbuff (T5_MOUNT_OX_MOUNTED), halfmountedbuff, dismountbuff (DISMOUNTED) and forceddismountbuff
/// (FORCE_DISMOUNTED). The hostile* buffs are PvP only, read from the raw attributes (stageE). <see cref="Spells"/>: the mount's abilities, its
/// &lt;mountspelllist&gt;&lt;mountspell uniquename spellslot/&gt; children (battle mounts, drakes, the frost ram; none for most mounts) by
/// CharacterSpellSlot, as the client's bdq.a 0x180619280 keeps them in bdq+0x4d0 (string[14], spellslot parsed as the enum; a slot
/// of 14 or more is refused there with "has invalid spellslot").
/// </summary>
public sealed record MountData(double MountTime, double DismountTime, double RemountTime, double RemountDistance, double HalfMountRange,
    double ForcedDismountCooldown, double FullDismountCooldown, string? MountedBuff, string? HalfMountedBuff, string DismountBuff, string ForcedDismountBuff,
    IReadOnlyList<string?> Spells)
{
    /// <summary>The length of <see cref="Spells"/>: CharacterSpellSlot 0-13 (the client's array, bdq.a).</summary>
    public const int SpellSlotCount = 14;
}

/// <summary>
/// A plant seed (items.xml farmableitem kind="plant", stageH islands SPEC §3.3-§3.5): &lt;harvest growtime lootlist lootchance fame&gt; with its
/// &lt;seed chance amount&gt;, and the focus watering attributes activefarmfocuscost (1000), activefarmmaxcycles (1),
/// activefarmactiondurationseconds (1) and activefarmbonus (2 × (1 − seed chance) for every crop). Times in seconds.
/// </summary>
public sealed record FarmData(double GrowTime, string LootList, double LootChance, double Fame, double SeedChance, int SeedAmount,
    double FocusCost, int MaxCycles, double ActionSeconds, double Bonus);

/// <summary>
/// An animal farmableitem (items.xml kind="animal", stageH islands SPEC phase 4, stageN mounts SPEC): a baby's &lt;grownitem uniquename
/// growtime fame&gt; with its &lt;offspring chance amount&gt; (null for a grown animal), its &lt;consumption&gt;&lt;food nutritionmax
/// secondspernutrition&gt; with the &lt;acceptedfood foodcategory favorite favoritebonus&gt; (one nutrition lasts secondspernutrition s of growth:
/// a full feed grows a baby in its growtime), the &lt;products&gt; of a grown animal, and the activefarm focus attributes. Times in seconds.
/// </summary>
public sealed record AnimalData(string? GrownItem, double GrowTime, double Fame, double OffspringChance, int OffspringAmount,
    double NutritionMax, double SecondsPerNutrition, string FoodCategory, string? Favorite, double FavoriteBonus,
    IReadOnlyList<AnimalProduct> Products, double FocusCost, int MaxCycles, double ActionSeconds, double Bonus)
{
    /// <summary>The length of one cycle: the growth of a baby, else the first product's productiontime; 0 when it has neither.</summary>
    public double CycleTime => GrownItem is not null ? GrowTime : Products.Count > 0 ? Products[0].ProductionTime : 0;
}

/// <summary>A &lt;product productiontime lootlist lootchance fame&gt; of a grown animal (chicken: eggs every 79200 s).</summary>
public sealed record AnimalProduct(double ProductionTime, string LootList, double LootChance, double Fame);

/// <summary>
/// One &lt;craftingrequirements&gt; of items.xml (bbu.aa 0x1805911E0, stage 6 SPEC §6c): silver and gold in whole units
/// (the verifier: not a7m), time, amountcrafted (default 1), craftingfocus, the flags, and the resources consumed per craft.
/// <see cref="Unsupported"/> lists the other requirement kinds (currency, playerfactionstanding, standing, vanityunlocked),
/// which this server cannot satisfy yet.
/// </summary>
public sealed record CraftRecipe(double Silver, double Gold, double Time, int AmountCrafted, double Focus, IReadOnlyList<CraftResource> Resources)
{
    public bool ForceSingleCraft { get; init; }
    public bool SwapTransaction { get; init; }

    /// <summary>
    /// salvageitemfactor / salvagesilverfactor of the recipe (bbu.aa 0x1805911E0: bbu+0x88 / +0x80, default −1): when given
    /// (≥ 0) they replace the durability-based salvage factors of the resources / the silver (bca.l 0x18059AD40, bca.i
    /// 0x180598FD0); null = not given.
    /// </summary>
    public double? SalvageItemFactor { get; init; }
    public double? SalvageSilverFactor { get; init; }

    /// <summary>Requirement kinds other than craftresource (currency uniquename, playerfactionstanding, …); empty = only resources.</summary>
    public IReadOnlyList<string> Unsupported { get; init; } = [];

    /// <summary>StageL mists phase B: &lt;standing type minstanding&gt; (the City Trader's mistcity gates, whole points); null = none.</summary>
    public (string Type, double Min)? Standing { get; init; }

    /// <summary>StageL mists phase B: the &lt;currency uniquename amount&gt; requirements per craft (FAVOR 675, whole units).</summary>
    public IReadOnlyList<(string Name, double Amount)> Currencies { get; init; } = [];

    /// <summary>StageN faction (SPEC §3): &lt;playerfactionstanding faction minstanding&gt; (the faction trader's gates, whole points); null = none.</summary>
    public (string Faction, double Min)? FactionStanding { get; init; }

    /// <summary>buildingfilter: the one building this recipe is made at (the faction traders' vanity unlocks); null = any.</summary>
    public string? BuildingFilter { get; init; }

    /// <summary>returnproductnotresource (bbu+0x78): the return bonus gives back the product, not resources (6 recipes of this build).</summary>
    public bool ReturnProductNotResource { get; init; }

    /// <summary>
    /// Whether the return bonus applies at all (bbu+0x64, stageJ focus-lp SPEC §2.4): returnproductnotresource, or a resource with a
    /// maxreturnamount above 0 (not given = no cap). The client zeroes the craftingfocus of a recipe that is not (the 9 wedding vanity ones).
    /// </summary>
    public bool Returnable => ReturnProductNotResource || Resources.Any(r => r.MaxReturn > 0);

    /// <summary>A recipe from its element; numbers that do not parse read as their default.</summary>
    public static CraftRecipe From(System.Xml.Linq.XElement e)
    {
        static double Num(System.Xml.Linq.XElement e, string name, double fallback) =>
            double.TryParse((string?)e.Attribute(name), System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var d) ? d : fallback;
        var resources = e.Elements("craftresource").Select(r => new CraftResource(
            (string?)r.Attribute("uniquename") ?? "", (int)Num(r, "count", 0), (int)Num(r, "enchantmentlevel", 0),
            (int)Math.Min(int.MaxValue, Num(r, "maxreturnamount", int.MaxValue)))).ToList();
        return new CraftRecipe(Num(e, "silver", 0), Num(e, "gold", 0), Num(e, "time", 0), Math.Max(1, (int)Num(e, "amountcrafted", 1)), Num(e, "craftingfocus", 0), resources)
        {
            ForceSingleCraft = (string?)e.Attribute("forcesinglecraft") == "true",
            SwapTransaction = (string?)e.Attribute("swaptransaction") == "true",
            ReturnProductNotResource = (string?)e.Attribute("returnproductnotresource") == "true",
            SalvageItemFactor = Num(e, "salvageitemfactor", -1) is var sif and >= 0 ? sif : null,
            SalvageSilverFactor = Num(e, "salvagesilverfactor", -1) is var ssf and >= 0 ? ssf : null,
            Unsupported = [.. e.Elements().Where(c => c.Name.LocalName != "craftresource")
                .Select(c => c.Name.LocalName + ((string?)c.Attribute("uniquename") is { } u ? $" {u}" : ""))],
            Standing = e.Element("standing") is { } st ? ((string?)st.Attribute("type") ?? "", Num(st, "minstanding", 0)) : null,
            Currencies = [.. e.Elements("currency").Select(c => ((string?)c.Attribute("uniquename") ?? "", Num(c, "amount", 0)))],
            FactionStanding = e.Element("playerfactionstanding") is { } fs ? ((string?)fs.Attribute("faction") ?? "", Num(fs, "minstanding", 0)) : null,
            BuildingFilter = (string?)e.Attribute("buildingfilter") is { Length: > 0 } filter ? filter : null,

        };
    }
}

/// <summary>Which of a journal's three items a template is (journals SPEC §A.1).</summary>
public enum JournalState { Base, Empty, Full }

/// <summary>
/// A labourer journal (items.xml &lt;journalitem&gt;, stageH journals SPEC §A): <see cref="Journal"/> = the base uniquename
/// (T4_JOURNAL_WOOD), <see cref="MaxFame"/> = maxfame as a7m raw, the one &lt;famefillingmissions&gt; child: <see cref="Mission"/>
/// (gatherfame, craftitemfame, killmobfame, fishingfame, fameearned), its mintier, its value (the activity fame that fills the
/// journal, §A.3, Q-A1) and its &lt;validitem&gt; ids (empty = any item); <see cref="Price"/> = the silver of its
/// craftingrequirements (a labourer's price; a7m raw); <see cref="Empty"/> / <see cref="Full"/> = the item ids of the two variants.
/// </summary>
public sealed record JournalData(string Journal, JournalState State, long MaxFame, string Mission, int MinTier, double Value,
    IReadOnlySet<string> ValidItems, long Price, short Empty, short Full)
{
    /// <summary>
    /// The journal fame a gain of <paramref name="gain"/> activity fame (a7m raw) gives: gain × maxfame / value, floored (§A.3: T4 wood
    /// 15 fame × 1800 / 1200 = 22.5); a value of 0 counts 1:1.
    /// </summary>
    public long Fill(long gain) => Value > 0 ? (long)Math.Floor((decimal)gain * (MaxFame / 10000m) / (decimal)Value) : gain;

    /// <summary>baselootamount: the loot rolls a labourer's job with this journal makes at 100 % yield (analysis/work/stageN/labourers/SPEC.md §4).</summary>
    public double BaseLoot { get; init; }

    /// <summary>The &lt;lootlist&gt; entries a labourer's job rolls from, by weight (SPEC §4).</summary>
    public IReadOnlyList<JournalLoot> Loot { get; init; } = [];
}

/// <summary>
/// One &lt;loot&gt; of a journal's &lt;lootlist&gt;: <see cref="Item"/> × <see cref="Amount"/> (itemname, itemamount) or <see cref="Silver"/> whole
/// silver (silveramount), chosen by <see cref="Weight"/>; <see cref="Fame"/> = labourerfame, what the labourer learns from it (whole fame).
/// </summary>
public sealed record JournalLoot(string? Item, int Amount, long Silver, double Weight, double Fame);

/// <summary>A craftresource: <see cref="Count"/> × <see cref="Name"/> per craft; <see cref="MaxReturn"/> caps the returned resources (0 = never returned).</summary>
public sealed record CraftResource(string Name, int Count, int Enchantment, int MaxReturn);

/// <summary>
/// One &lt;upgraderequirements&gt; (client bbu at bdi+0x58): silver and gold in whole units (none in this build's items.xml) and
/// the &lt;upgraderesource&gt; children (uniquename, count, enchantmentlevel), all per upgraded item.
/// </summary>
public sealed record UpgradeCost(double Silver, double Gold, IReadOnlyList<CraftResource> Resources)
{
    public static UpgradeCost From(System.Xml.Linq.XElement e)
    {
        static double Num(System.Xml.Linq.XElement e, string name) =>
            double.TryParse((string?)e.Attribute(name), System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var d) ? d : 0;
        return new UpgradeCost(Num(e, "silver"), Num(e, "gold"),
            [.. e.Elements("upgraderesource").Select(r => new CraftResource((string?)r.Attribute("uniquename") ?? "", (int)Num(r, "count"), (int)Num(r, "enchantmentlevel"), 0))]);
    }
}
