using System.Globalization;
using System.Xml.Linq;

namespace AlbionLocal.Server.Data;

/// <summary>
/// craftingmodifiers.xml (stage 6 SPEC §6c "Return rate"): per crafting location (world.xml cluster id) the refining and
/// crafting bonus and the bonus per crafting category (items.xml craftingcategory: "ore", "sword", …), and which categories
/// are refining (fiber, ore, rock, wood, hide). Thetford 0000: refining 0.18, crafting 0.18, ore +0.40.
/// </summary>
public sealed class CraftingModifiers
{
    private readonly HashSet<string> _refining;
    private readonly Dictionary<string, Location> _locations;

    /// <summary>One craftinglocation: its two bonuses and the category modifiers.</summary>
    public sealed record Location(double Refining, double Crafting, IReadOnlyDictionary<string, double> Categories);

    public CraftingModifiers(IEnumerable<string> refining, IReadOnlyDictionary<string, Location> locations)
    {
        _refining = new HashSet<string>(refining, StringComparer.OrdinalIgnoreCase);
        _locations = new Dictionary<string, Location>(locations, StringComparer.OrdinalIgnoreCase);
    }

    /// <summary>No locations: every bonus is 0.</summary>
    public static CraftingModifiers None { get; } = new([], new Dictionary<string, Location>());

    public bool IsRefining(string category) => _refining.Contains(category);

    /// <summary>
    /// The return bonus of an item of <paramref name="category"/> crafted in <paramref name="cluster"/>: the location's refining
    /// bonus for a refining category, else its crafting bonus, plus the category's own modifier and any matching active
    /// global production bonuses. Outside a crafting location only the global bonuses apply.
    /// </summary>
    public double Bonus(string cluster, string category, IEnumerable<FestivitiesData.CraftBonus>? activeBonuses = null)
    {
        var bonus = _locations.TryGetValue(cluster, out var at)
            ? (IsRefining(category) ? at.Refining : at.Crafting) + at.Categories.GetValueOrDefault(category)
            : 0;
        // 345319 dev.c(bdi), AE7680 -> AD70C0: sum each active effect whose category matches
        // item.craftingcategory. Values are fractions (0.10 = +10 %), independent of crafting fame.
        if (activeBonuses is not null)
            foreach (var active in activeBonuses)
                if (string.Equals(active.Category, category, StringComparison.OrdinalIgnoreCase)) bonus += active.Value;
        return bonus;
    }

    public static CraftingModifiers Load(GameDataFile file) => From(file.Root());

    public static CraftingModifiers From(XElement root)
    {
        static double Num(XElement? e, string attr) =>
            double.TryParse((string?)e?.Attribute(attr), NumberStyles.Float, CultureInfo.InvariantCulture, out var d) ? d : 0;
        var refining = root.Elements("refiningcategory").Elements("item").Select(i => (string?)i.Attribute("category")).OfType<string>().ToList();
        var locations = new Dictionary<string, Location>(StringComparer.OrdinalIgnoreCase);
        foreach (var l in root.Elements("craftinglocation"))
        {
            if ((string?)l.Attribute("clusterid") is not { Length: > 0 } id) continue;
            var categories = new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase);
            foreach (var m in l.Elements("craftingmodifier"))
                if ((string?)m.Attribute("name") is { Length: > 0 } name) categories[name] = Num(m, "value");
            locations.TryAdd(id, new Location(Num(l.Element("refiningbonus"), "value"), Num(l.Element("craftingbonus"), "value"), categories));
        }
        return new CraftingModifiers(refining, locations);
    }
}
