using System.Globalization;
using System.Xml.Linq;
using AlbionLocal.Photon;

namespace AlbionLocal.Server.Data;

/// <summary>
/// The item templates (<see cref="ItemTemplate"/>), found by client index and by uniquename ignoring case: the items.xml
/// variants, then the _EMPTY and _FULL variants the client makes of every journalitem (ids 11972-12237 in 345319). Loaded from
/// the client's items.bin (<see cref="Load"/>); data/items.tsv, which analysis/tools/gen_items.py made from the same
/// file, is now only the golden reference of the self-test.
/// </summary>
public sealed class ItemStore
{
    private readonly ItemTemplate?[] _byIndex;
    private readonly Dictionary<string, ItemTemplate> _byName = new(StringComparer.OrdinalIgnoreCase);

    public ItemStore(IEnumerable<ItemTemplate> templates)
    {
        All = [.. templates];
        _byIndex = new ItemTemplate?[All.Count == 0 ? 1 : All.Max(t => t.Index) + 1];
        foreach (var t in All)
        {
            _byIndex[t.Index] = t;
            if (!_byName.TryAdd(t.Name, t)) Log.Warn("items", $"item {t.Name} twice (ids {_byName[t.Name].Index} and {t.Index}); the name finds the first");
        }
    }

    /// <summary>Every template in load order (client index order for the client's file).</summary>
    public IReadOnlyList<ItemTemplate> All { get; }

    public int Count => All.Count;

    /// <summary>The template with client index <paramref name="index"/>; null = none.</summary>
    public ItemTemplate? this[int index] => index > 0 && index < _byIndex.Length ? _byIndex[index] : null;

    /// <summary>By uniquename (with @N for enchantments), ignoring case; null = unknown.</summary>
    public ItemTemplate? Find(string name) => _byName.GetValueOrDefault(name);

    /// <summary>The known name with the fewest edits from <paramref name="name"/> (ignoring case), for "unknown item" warnings; null for an empty store.</summary>
    public string? Closest(string name)
    {
        var wanted = name.ToUpperInvariant();
        string? best = null;
        var bestEdits = int.MaxValue;
        foreach (var known in _byName.Keys)
            if (Edits(wanted, known.ToUpperInvariant()) is var edits && edits < bestEdits)
                (best, bestEdits) = (known, edits);
        return best;
    }

    /// <summary>
    /// Levenshtein distance: insertions, deletions and substitutions turning <paramref name="a"/> into <paramref name="b"/>
    /// (also the closest operation name of Content/OpcodeStubs).
    /// </summary>
    internal static int Edits(string a, string b)
    {
        var row = new int[b.Length + 1];
        for (var j = 0; j <= b.Length; j++) row[j] = j;
        for (var i = 1; i <= a.Length; i++)
        {
            var diagonal = row[0];
            row[0] = i;
            for (var j = 1; j <= b.Length; j++)
            {
                var above = row[j];
                row[j] = Math.Min(Math.Min(above + 1, row[j - 1] + 1), diagonal + (a[i - 1] == b[j - 1] ? 0 : 1));
                diagonal = above;
            }
        }
        return row[b.Length];
    }

    /// <summary>
    /// items.xml, numbered by <see cref="ClientNumbering.Items"/>. An enchantment takes its item's attributes with its own
    /// over them (as analysis/tools/gen_gamedata.py); the spell lists are resolved once the whole file is read, because a
    /// craftingspelllist reference may name an item further down. A number that does not parse reads as 0 and is warned
    /// once per attribute (<see cref="NumberReader"/>).
    /// </summary>
    public static ItemStore Load(GameDataFile file)
    {
        var rows = new List<(short Index, Dictionary<string, string> Attrs, string Name, string Base, string Kind, string? Consume, string[] Harvests, XElement Own,
            string?[] MountSpells)>();
        var lists = new Dictionary<string, SpellListSource>(StringComparer.Ordinal);
        var numbers = new NumberReader(file.Name);
        double Num(Dictionary<string, string> a, string key, string item) => numbers.Num(a.GetValueOrDefault(key), key, item);
        int Int(Dictionary<string, string> a, string key, string item) => numbers.Int(a.GetValueOrDefault(key), key, item);
        Dictionary<string, string>? baseAttrs = null;
        string?[] baseMountSpells = [];
        foreach (var (index, item, enchantment) in ClientNumbering.Items(file.Elements()))
        {
            var baseName = item.Attribute("uniquename")!.Value;
            // <canharvest resourcetype="WOOD"/> children of a gathering tool; its enchantments harvest the same.
            string[] harvests = [.. item.Elements("canharvest").Select(c => c.Attribute("resourcetype")?.Value).OfType<string>()];
            if (enchantment is null)
            {
                baseAttrs = Attributes(item, null);
                baseMountSpells = MountSpells(item, baseName);
                rows.Add((index, baseAttrs, baseName, baseName, item.Name.LocalName, baseAttrs.GetValueOrDefault("consumespell"), harvests, item, baseMountSpells));
                if (baseName.Length > 0)
                    lists[baseName] = SpellListSource.Of(item, Math.Max(Int(baseAttrs, "activespellslots", baseName), Int(baseAttrs, "passivespellslots", baseName)));
                continue;
            }
            var attrs = Attributes(enchantment, baseAttrs);
            // An enchantment without a consumespell of its own (or an empty one) casts the item's (gen_item_spells.py).
            var consume = enchantment.Attribute("consumespell")?.Value is { Length: > 0 } own ? own : baseAttrs!.GetValueOrDefault("consumespell");
            // An enchantment's own mount spell list, if it has one, replaces the item's.
            var mountSpells = enchantment.Element("mountspelllist") is null ? baseMountSpells : MountSpells(enchantment, baseName);
            rows.Add((index, attrs, $"{baseName}@{enchantment.Attribute("enchantmentlevel")?.Value ?? "?"}", baseName, item.Name.LocalName, consume, harvests, enchantment,
                mountSpells));
        }

        var resolved = new Dictionary<string, List<string?>[]?>(StringComparer.Ordinal);
        var shared = new Dictionary<string, string[][]>(StringComparer.Ordinal);
        string[][] SpellLists(string baseName, int n)
        {
            if (n == 0) return [];
            if (shared.TryGetValue(baseName, out var done)) return done;
            var full = SpellListSource.Resolve(baseName, lists, resolved, 0) ?? [];
            return shared[baseName] = [.. Enumerable.Range(0, n).Select(i => i < full.Length ? full[i].OfType<string>().ToArray() : [])];
        }
        // Journals (stageH journals SPEC §A.1): after the whole file the client makes <name>_EMPTY and then <name>_FULL of each
        // journalitem in file order (bdh.db 0x18060ae20 → bdm.b / bdm.a), each taking the next id of the same counter.
        var nextId = rows.Count == 0 ? 1 : rows.Max(r => r.Index) + 1;
        var journals = new Dictionary<string, JournalData>(StringComparer.Ordinal);
        foreach (var r in rows.Where(r => r.Kind == "journalitem" && r.Name == r.Base))
        {
            var mission = r.Own.Element("famefillingmissions")?.Elements().FirstOrDefault();
            var empty = checked((short)nextId++);
            var full = checked((short)nextId++);
            journals[r.Name] = new JournalData(r.Name, JournalState.Base, (long)Math.Round(Num(r.Attrs, "maxfame", r.Name) * 10000),
                mission?.Name.LocalName ?? "", mission?.Attribute("mintier") is { } min ? numbers.Int(min.Value, "mintier", r.Name) : 0,
                mission?.Attribute("value") is { } value ? numbers.Num(value.Value, "value", r.Name) : 0,
                (mission?.Elements("validitem").Select(v => (string?)v.Attribute("id")).OfType<string>() ?? []).ToHashSet(StringComparer.OrdinalIgnoreCase),
                (long)Math.Round((r.Own.Element("craftingrequirements") is { } price ? numbers.Num((string?)price.Attribute("silver"), "silver", r.Name) : 0) * 10000),
                empty, full)
            {
                BaseLoot = numbers.Num(r.Attrs.GetValueOrDefault("baselootamount"), "baselootamount", r.Name), // T7_JOURNAL_WOOD 38.4
                Loot = [.. (r.Own.Element("lootlist")?.Elements("loot") ?? []).Select(l => new JournalLoot((string?)l.Attribute("itemname"),
                    l.Attribute("itemamount") is { } a ? numbers.Int(a.Value, "itemamount", r.Name) : 1,
                    l.Attribute("silveramount") is { } sa ? (long)Math.Round(numbers.Num(sa.Value, "silveramount", r.Name) * 10000) : 0,
                    numbers.Num((string?)l.Attribute("weight"), "weight", r.Name), numbers.Num((string?)l.Attribute("labourerfame"), "labourerfame", r.Name)))],
            };
        }
        // StageH islands SPEC §0.4: the client numbers the placeable items (farmableitem, furnitureitem elements) in document order for their building type.
        var placeIndex = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (var r in rows.Where(r => r.Name == r.Base && r.Kind is "farmableitem" or "furnitureitem")) placeIndex.TryAdd(r.Name, placeIndex.Count);
        FarmData? Farm((short Index, Dictionary<string, string> Attrs, string Name, string Base, string Kind, string? Consume, string[] Harvests, XElement Own, string?[] MountSpells) r)
        {
            if (r.Kind != "farmableitem" || r.Attrs.GetValueOrDefault("kind") != "plant" || r.Own.Element("harvest") is not { } harvest) return null;
            double N(XElement? e, string key, double fallback) => e?.Attribute(key) is { } v ? numbers.Num(v.Value, key, r.Name) : fallback;
            var seed = harvest.Element("seed");
            return new FarmData(N(harvest, "growtime", 0), (string?)harvest.Attribute("lootlist") ?? "", N(harvest, "lootchance", 1), N(harvest, "fame", 0),
                N(seed, "chance", 0), (int)N(seed, "amount", 1), Num(r.Attrs, "activefarmfocuscost", r.Name), Math.Max(0, Int(r.Attrs, "activefarmmaxcycles", r.Name)),
                Num(r.Attrs, "activefarmactiondurationseconds", r.Name), Num(r.Attrs, "activefarmbonus", r.Name));
        }
        AnimalData? Animal((short Index, Dictionary<string, string> Attrs, string Name, string Base, string Kind, string? Consume, string[] Harvests, XElement Own, string?[] MountSpells) r)
        {
            if (r.Kind != "farmableitem" || r.Attrs.GetValueOrDefault("kind") != "animal") return null;
            double N(XElement? e, string key, double fallback) => e?.Attribute(key) is { } v ? numbers.Num(v.Value, key, r.Name) : fallback;
            var grown = r.Own.Element("grownitem");
            var offspring = grown?.Element("offspring");
            var food = r.Own.Element("consumption")?.Element("food");
            var accepted = food?.Element("acceptedfood");
            return new AnimalData((string?)grown?.Attribute("uniquename"), N(grown, "growtime", 0), N(grown, "fame", 0), N(offspring, "chance", 0), (int)N(offspring, "amount", 0),
                N(food, "nutritionmax", 0), N(food, "secondspernutrition", 0), (string?)accepted?.Attribute("foodcategory") ?? "", (string?)accepted?.Attribute("favorite"),
                N(accepted, "favoritebonus", 0),
                [.. (r.Own.Element("products")?.Elements("product") ?? []).Select(p => new AnimalProduct(N(p, "productiontime", 0), (string?)p.Attribute("lootlist") ?? "",
                    N(p, "lootchance", 1), N(p, "fame", 0)))],
                r.Attrs.ContainsKey("activefarmfocuscost") ? Num(r.Attrs, "activefarmfocuscost", r.Name) : 0,
                r.Attrs.ContainsKey("activefarmmaxcycles") ? Math.Max(0, Int(r.Attrs, "activefarmmaxcycles", r.Name)) : 0,
                r.Attrs.ContainsKey("activefarmactiondurationseconds") ? Num(r.Attrs, "activefarmactiondurationseconds", r.Name) : 1,
                r.Attrs.ContainsKey("activefarmbonus") ? Num(r.Attrs, "activefarmbonus", r.Name) : 0);
        }
        var templates = rows.Select(r =>
        {
            var a = r.Attrs;
            int activeSlots = Int(a, "activespellslots", r.Name), passiveSlots = Int(a, "passivespellslots", r.Name);
            return new ItemTemplate
            {
                Index = r.Index,
                Name = r.Name,
                Kind = r.Kind,
                Slot = a.GetValueOrDefault("slottype", ""),
                Tier = Int(a, "tier", r.Name),
                Enchantment = Int(a, "enchantmentlevel", r.Name),
                MaxDurability = Num(a, "durability", r.Name),
                ItemPower = Num(a, "itempower", r.Name),
                ProgressionType = a.GetValueOrDefault("itempowerprogressiontype", ""),
                TwoHanded = a.GetValueOrDefault("twohanded") == "true",
                MasteryModifier = Num(a, "masterymodifier", r.Name),
                HitPointsMax = Num(a, "hitpointsmax", r.Name),
                HitPointsRegeneration = Num(a, "hitpointsregeneration", r.Name),
                EnergyMax = Num(a, "energymax", r.Name),
                HitPointsRegenerationBonus = Num(a, "hitpointsregenerationbonus", r.Name),
                EnergyRegenerationBonus = Num(a, "energyregenerationbonus", r.Name),
                MountHitPointsMax = Num(a, "mounthitpointsmax", r.Name),
                MountHitPointsRegeneration = Num(a, "mounthitpointsregeneration", r.Name),
                Mount = r.Kind != "mount" ? null : new MountData(
                    Num(a, "mounttime", r.Name), Num(a, "dismounttime", r.Name), Num(a, "remounttime", r.Name), Num(a, "remountdistance", r.Name),
                    Num(a, "halfmountrange", r.Name), Num(a, "forceddismountcooldown", r.Name), Num(a, "fulldismountcooldown", r.Name),
                    a.GetValueOrDefault("mountedbuff"), a.GetValueOrDefault("halfmountedbuff"),
                    a.GetValueOrDefault("dismountbuff") is { Length: > 0 } dismount ? dismount : "DISMOUNTED",
                    a.GetValueOrDefault("forceddismountbuff") is { Length: > 0 } forced ? forced : "FORCE_DISMOUNTED",
                    r.MountSpells.Length == 0 ? new string?[MountData.SpellSlotCount] : r.MountSpells),
                ActiveSpellSlots = activeSlots,
                PassiveSpellSlots = passiveSlots,
                SpellLists = SpellLists(r.Base, Math.Max(activeSlots, passiveSlots)),
                ConsumeSpell = r.Consume,
                UnlockedToEquip = a.GetValueOrDefault("unlockedtoequip") == "true",
                MaxStackSize = Int(a, "maxstacksize", r.Name),
                MaxQuality = a.ContainsKey("maxqualitylevel") ? Int(a, "maxqualitylevel", r.Name) : 5,
                CanHarvest = r.Harvests,
                ItemValue = Num(a, "itemvalue", r.Name),
                FameValue = Num(a, "famevalue", r.Name),
                OwnItemValue = r.Own.Attribute("itemvalue") is { } own ? numbers.Num(own.Value, "itemvalue", r.Name) : null,
                Recipes = [.. r.Own.Elements("craftingrequirements").Select(CraftRecipe.From)],
                Upgrade = r.Name != r.Base && r.Own.Element("upgraderequirements") is { } up ? UpgradeCost.From(up) : null,
                Attrs = a,
                Journal = r.Kind == "journalitem" ? journals.GetValueOrDefault(r.Base) : null,
                Farm = r.Name == r.Base ? Farm(r) : null,
                Animal = r.Name == r.Base ? Animal(r) : null,
                PlaceIndex = r.Name == r.Base ? placeIndex.GetValueOrDefault(r.Name, -1) : -1,
            };
        }).ToList();
        // The two variants share the base item's attributes (weight, tier, the shop categories the market filters by); no recipe.
        foreach (var t in templates.Where(t => t.Journal is { State: JournalState.Base }).ToList())
            foreach (var state in (JournalState[])[JournalState.Empty, JournalState.Full])
                templates.Add(new ItemTemplate
                {
                    Index = state == JournalState.Empty ? t.Journal!.Empty : t.Journal!.Full,
                    Name = t.Name + (state == JournalState.Empty ? "_EMPTY" : "_FULL"),
                    Kind = t.Kind,
                    Tier = t.Tier,
                    MaxStackSize = t.MaxStackSize,
                    Attrs = t.Attrs,
                    Journal = t.Journal with { State = state },
                });
        templates.Sort((a, b) => a.Index.CompareTo(b.Index));
        var store = new ItemStore(templates);
        numbers.Report();
        return store;
    }

    /// <summary>The spellslot names of &lt;mountspell&gt; in CharacterSpellSlot order (0-13, the client's enum names, matched ignoring case).</summary>
    private static readonly string[] SpellSlotNames =
    [
        "mainhand1", "mainhand2", "offhandormainhand3", "armor", "head", "shoes", "vanitymainhand1", "vanitymainhand2",
        "vanityoffhandormainhand3", "vanityarmor", "vanityhead", "vanityshoes", "potion", "food",
    ];

    /// <summary>
    /// The &lt;mountspelllist&gt; of a mount (bdq.a 0x180619280): each &lt;mountspell uniquename spellslot/&gt; at its CharacterSpellSlot,
    /// a later entry for the same slot over an earlier one; an unknown slot or an empty name is warned and skipped, as the client
    /// logs it. Empty when the element has no list.
    /// </summary>
    private static string?[] MountSpells(XElement item, string itemName)
    {
        if (item.Element("mountspelllist") is not { } list) return [];
        var spells = new string?[MountData.SpellSlotCount];
        foreach (var m in list.Elements("mountspell"))
        {
            var slot = Array.FindIndex(SpellSlotNames, n => string.Equals(n, (string?)m.Attribute("spellslot"), StringComparison.OrdinalIgnoreCase));
            if (slot < 0 || m.Attribute("uniquename")?.Value is not { Length: > 0 } name)
                Log.Warn("data", $"mount {itemName} has an invalid mountspell (spellslot '{(string?)m.Attribute("spellslot")}'): skipped");
            else spells[slot] = name;
        }
        return spells;
    }

    /// <summary>The element's attributes over <paramref name="under"/> (the item's, for an enchantment).</summary>
    private static Dictionary<string, string> Attributes(XElement e, Dictionary<string, string>? under)
    {
        var attrs = under is null ? new Dictionary<string, string>(StringComparer.Ordinal) : new Dictionary<string, string>(under, StringComparer.Ordinal);
        foreach (var a in e.Attributes())
            if (!a.IsNamespaceDeclaration) attrs[a.Name.LocalName] = a.Value;
        return attrs;
    }

    /// <summary>
    /// The &lt;craftingspelllist&gt; of one item before references are followed (bc9.a 0x1805fd300): per slot list the
    /// craftspell names (slots="all" or none = every list, "1,2" = lists 0 and 1, other values dropped), the removespell
    /// names and the reference. Names may be null (an entry without uniquename): they never resolve to a spell but count
    /// as entries, as in analysis/tools/gen_item_spells.py.
    /// </summary>
    private sealed record SpellListSource(int Lists, List<string?>[] Own, HashSet<string?> Removed, string? Reference)
    {
        /// <param name="n">max(activespellslots, passivespellslots) of the item.</param>
        public static SpellListSource Of(XElement item, int n)
        {
            var own = Enumerable.Range(0, n).Select(_ => new List<string?>()).ToArray();
            var removed = new HashSet<string?>();
            if (item.Element("craftingspelllist") is not { } csl) return new(n, own, removed, null);
            foreach (var c in csl.Elements())
            {
                var name = c.Attribute("uniquename")?.Value;
                if (c.Name.LocalName == "removespell") removed.Add(name);
                else if (c.Name.LocalName == "craftspell")
                {
                    var slots = c.Attribute("slots")?.Value ?? "all";
                    var targets = slots == "all" ? Enumerable.Range(0, n)
                        : slots.Split(',').Select(k => k.Trim()).Where(k => k.Length > 0 && k.All(char.IsAsciiDigit)).Select(k => int.Parse(k, CultureInfo.InvariantCulture) - 1);
                    foreach (var k in targets)
                        if (k >= 0 && k < n) own[k].Add(name);
                }
            }
            return new(n, own, removed, csl.Attribute("reference")?.Value);
        }

        /// <summary>
        /// bc9.ll 0x180600d50: the lists of <paramref name="name"/> with its reference followed (at most 16 deep; a
        /// cycle ends there): taken as they are when the item has no entries and no removespell, else appended to the
        /// own entries without the removed names. Can be longer than the item's own slot count (the reference's lists
        /// taken whole). Cached per name; null = no such item or too deep.
        /// </summary>
        public static List<string?>[]? Resolve(string name, Dictionary<string, SpellListSource> sources, Dictionary<string, List<string?>[]?> cache, int depth)
        {
            if (cache.TryGetValue(name, out var cached)) return cached;
            if (!sources.TryGetValue(name, out var s) || depth > 16) return null;
            var lists = s.Own.Select(l => new List<string?>(l)).ToArray();
            if (s.Reference is { Length: > 0 } reference && Resolve(reference, sources, cache, depth + 1) is { } other)
            {
                if (lists.All(l => l.Count == 0) && s.Removed.Count == 0)
                    lists = [.. other.Select(l => new List<string?>(l)), .. Enumerable.Range(0, Math.Max(0, s.Lists - other.Length)).Select(_ => new List<string?>())];
                else
                    for (var i = 0; i < Math.Min(other.Length, s.Lists); i++)
                        lists[i].AddRange(other[i].Where(x => !s.Removed.Contains(x)));
            }
            return cache[name] = lists;
        }
    }
}
