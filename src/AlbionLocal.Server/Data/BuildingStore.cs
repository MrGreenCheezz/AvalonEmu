using System.Globalization;
using System.Xml.Linq;
using AlbionLocal.Photon;

namespace AlbionLocal.Server.Data;

/// <summary>
/// The building templates of buildings.xml (<see cref="BuildingTemplate"/>), by type id (<see cref="ClientNumbering.Buildings"/>)
/// and by uniquename ignoring case. The template tile of an action building names its building: the actionbuilding tile
/// of a cluster template is the uniquename here (SWAMP_GREEN_REPAIRSHOP), and NewBuilding key 3 places that tile.
/// </summary>
public sealed class BuildingStore
{
    private readonly Dictionary<short, BuildingTemplate> _byId = new();
    private readonly Dictionary<string, BuildingTemplate> _byName = new(StringComparer.OrdinalIgnoreCase);

    public BuildingStore(IEnumerable<BuildingTemplate> templates)
    {
        All = [.. templates];
        foreach (var t in All)
        {
            _byId[t.Id] = t;
            if (!_byName.TryAdd(t.Name, t)) Log.Warn("data", $"building {t.Name} twice (ids {_byName[t.Name].Id} and {t.Id}); the name finds the first");
        }
    }

    /// <summary>Every building in file order.</summary>
    public IReadOnlyList<BuildingTemplate> All { get; }

    public int Count => All.Count;

    public BuildingTemplate? this[short id] => _byId.GetValueOrDefault(id);

    /// <summary>By uniquename (= template tile name), ignoring case; null = unknown.</summary>
    public BuildingTemplate? Find(string name) => _byName.GetValueOrDefault(name);

    /// <summary>
    /// buildings.xml, numbered by <see cref="ClientNumbering.Buildings"/> with <paramref name="idOffset"/>. A
    /// buildingreference takes the attributes of the template it names, with its own (uniquename) over them; one whose
    /// template is missing, and elements without uniquename, are skipped as the client skips them (bbh.c8), with a warning.
    /// </summary>
    public static BuildingStore Load(GameDataFile file, int idOffset = 1)
    {
        var numbered = ClientNumbering.Buildings(file.Elements(), idOffset).ToList();
        var definitions = new Dictionary<string, XElement>(StringComparer.Ordinal);
        foreach (var (_, el) in numbered)
            if (el.Name.LocalName != "buildingreference" && (string?)el.Attribute("uniquename") is { Length: > 0 } name)
                definitions.TryAdd(name, el);
        var templates = new List<BuildingTemplate>();
        var skipped = new List<string>();
        foreach (var (id, el) in numbered)
        {
            if ((string?)el.Attribute("uniquename") is not { Length: > 0 } name)
            {
                skipped.Add($"#{id} {el.Name.LocalName} without uniquename");
                continue;
            }
            var reference = el.Name.LocalName == "buildingreference" ? (string?)el.Attribute("template") : null;
            var definition = el;
            if (el.Name.LocalName == "buildingreference")
            {
                if (reference is null || !definitions.TryGetValue(reference, out var found))
                {
                    skipped.Add($"#{id} {name}: template '{reference}' missing");
                    continue;
                }
                definition = found;
            }
            var attrs = definition.Attributes().ToDictionary(a => a.Name.LocalName, a => a.Value);
            foreach (var a in el.Attributes()) attrs[a.Name.LocalName] = a.Value;
            double Num(string key, double fallback) =>
                double.TryParse(attrs.GetValueOrDefault(key), NumberStyles.Float, CultureInfo.InvariantCulture, out var d) ? d : fallback;
            templates.Add(new BuildingTemplate
            {
                Id = id,
                Name = name,
                Kind = definition.Name.LocalName,
                Reference = reference,
                Tier = TileMath.Int(attrs.GetValueOrDefault("tier")),
                Durability = Num("durability", 100),
                NutritionStorage = Num("nutritionstorage", 1),
                CraftCapacity = Num("craftcapacity", 0),
                CraftItems = [.. (definition.Element("craftingitemlist")?.Descendants("craftitem") ?? [])
                    .Select(c => (string?)c.Attribute("uniquename")).OfType<string>().Distinct(StringComparer.OrdinalIgnoreCase)],
                Attrs = attrs,
                Element = definition,
            });
        }
        if (skipped.Count > 0) Log.Warn("data", $"{file.Name}: {skipped.Count} building(s) skipped (first: {skipped[0]})");
        return new BuildingStore(templates);
    }
}

/// <summary>
/// One buildings.xml building. For a buildingreference (SWAMP_GREEN_REPAIRSHOP → T8_REPAIRSHOP) the fields and
/// <see cref="Attrs"/> are those of the template it names, its uniquename over them.
/// </summary>
public sealed record BuildingTemplate
{
    /// <summary>NewBuilding key 2.</summary>
    public required short Id { get; init; }

    public required string Name { get; init; }

    /// <summary>Element name of the definition (repairbuilding, craftbuilding, bankbuilding, …; the referenced one's for a reference).</summary>
    public required string Kind { get; init; }

    /// <summary>The template a buildingreference names; null for a definition.</summary>
    public string? Reference { get; init; }

    public int Tier { get; init; }

    /// <summary>durability; absent = 100 (NewBuilding key 7 = this × 10000: 1000000 for the bank, market and faction trader, SPEC §3).</summary>
    public double Durability { get; init; }

    /// <summary>nutritionstorage; absent = 1 (key 18 = this × 10000).</summary>
    public double NutritionStorage { get; init; }

    /// <summary>craftcapacity; absent = 0 (key 21 = this × 10000, sent only when above 0).</summary>
    public double CraftCapacity { get; init; }

    /// <summary>
    /// The items of its &lt;craftingitemlist&gt; (craftitem uniquenames, file order, each once): what op 55 CraftItem may make
    /// there (stage 6 SPEC §6a.2, §6c; a shop's goods, a station's recipes). Empty for a building that crafts nothing.
    /// </summary>
    public IReadOnlyList<string> CraftItems { get; init; } = [];

    /// <summary>isshop="true" (the NPC shops: FARMING_MERCHANT, the vanity store, …) or a factionbuilding (the faction trader).</summary>
    public bool IsShop => Attrs.GetValueOrDefault("isshop") == "true" || Kind == "factionbuilding";

    /// <summary>Repairs items (op 55 RepairItem): the repair shops, and the marketplace (captured: another player's repair there).</summary>
    public bool Repairs => Kind is "repairbuilding" or "marketplacebuilding";

    /// <summary>
    /// Salvages items (op 55 SalvageItem, ACT 3): the client's salvage component (cml) exists on the repair shop (cpl) and the
    /// meld building (cpj) only.
    /// </summary>
    public bool Salvages => Kind is "repairbuilding" or "meldbuilding";

    /// <summary>
    /// Upgrades an item's enchantment with runes, souls and relics (op 55 UpgradeEnchantment, ACT 7): the client's component (cmn)
    /// exists on the meld building class cpj only (enchanting SPEC §1).
    /// </summary>
    public bool Enchants => Kind is "meldbuilding";

    /// <summary>Awakens weapons and modifies their traits (op 55 AwakenLegendaryItem, ACT 8): the client's component (cm2) is on cpj only (awakening SPEC §1).</summary>
    public bool Awakens => Kind is "meldbuilding";

    /// <summary>Rerolls an item's quality (op 55 RerollQuality, ACT 6): the client's component (cmk) exists on the repair shop class cpl only.</summary>
    public bool RerollsQuality => Kind is "repairbuilding";

    /// <summary>All attributes (definition, then the reference's own).</summary>
    public IReadOnlyDictionary<string, string> Attrs { get; init; } = new Dictionary<string, string>();

    /// <summary>The definition element, for its children (shop lists, craft lists).</summary>
    public required XElement Element { get; init; }
}
