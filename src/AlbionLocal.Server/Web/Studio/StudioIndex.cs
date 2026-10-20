using System.Text.Json.Nodes;
using System.Xml.Linq;
using AlbionLocal.Server.Mods;

namespace AlbionLocal.Server.Web.Studio;

/// <summary>One row of a catalogue search: what a picker tile shows, and the lower-case text the search matches.</summary>
public sealed record StudioRow(string Kind, string Name, string? En, string? Ru, string? Sprite, Dictionary<string, string?> Facets, JsonObject Stats, string? Pack = null)
{
    public string Key { get; } = $"{Name} {En} {Ru}".ToLowerInvariant();

    public JsonObject ToJson(IconCache icons) => new()
    {
        ["kind"] = Kind, ["name"] = Name, ["en"] = En, ["ru"] = Ru, ["pack"] = Pack,
        ["icon"] = Sprite is not null && icons.Has(Sprite) ? StudioApi.IconUrl(Sprite) : null,
        ["facets"] = new JsonObject([.. Facets.Where(f => f.Value is not null).Select(f => KeyValuePair.Create(f.Key, (JsonNode?)f.Value))]),
        ["stats"] = Stats.DeepClone(),
    };
}

/// <summary>
/// The studio's search over the catalogue (studio SPEC §2): items, spells, mobs, loot lists and crafting stations as small rows,
/// built once per catalogue, searched server-side (every word of the query in the name or the EN/RU text) with filters and paging.
/// </summary>
public sealed class StudioIndex
{
    public required ModCatalog Catalog { get; init; }
    public List<StudioRow> Items { get; } = [];
    public List<StudioRow> Spells { get; } = [];
    public List<StudioRow> Mobs { get; } = [];
    public List<StudioRow> LootLists { get; } = [];
    public List<StudioRow> Stations { get; } = [];

    public static StudioIndex Build(ModCatalog c, IconCache icons)
    {
        var x = new StudioIndex { Catalog = c };
        foreach (var i in c.Items) x.Items.Add(ItemRow(c.Loc.GetValueOrDefault("@ITEMS_" + i.Name), i, icons));
        foreach (var s in c.Spells) x.Spells.Add(SpellRow(c.Loc.GetValueOrDefault(ModCatalog.SpellNameKey(s.El)), s, c, icons));
        foreach (var (name, mob) in c.MobEls)
        {
            var (en, ru) = c.Loc.GetValueOrDefault(ModCatalog.MobNameKey(mob));
            x.Mobs.Add(new StudioRow("mob", name, en, ru, null,
                new() { ["tier"] = mob.Attribute("tier")?.Value, ["category"] = c.Mobs.GetValueOrDefault(name) is { Length: > 0 } cat ? cat : null, ["faction"] = mob.Attribute("faction")?.Value },
                new JsonObject { ["hp"] = mob.Attribute("hitpointsmax")?.Value, ["fame"] = mob.Attribute("fame")?.Value, ["loot"] = mob.Descendants("LootListReference").Count() }));
        }
        x.Mobs.Sort((a, b) => string.Compare(a.Name, b.Name, StringComparison.OrdinalIgnoreCase));
        foreach (var (name, list) in c.LootListEls)
            x.LootLists.Add(new StudioRow("lootlist", name, null, null, null, [],
                new JsonObject { ["items"] = list.Descendants("Item").Count(), ["lists"] = list.Descendants("LootListReference").Count(), ["users"] = c.ListUsers(name).Count }));
        x.LootLists.Sort((a, b) => string.Compare(a.Name, b.Name, StringComparison.OrdinalIgnoreCase));
        foreach (var (name, b) in c.StationEls)
        {
            var (en, ru) = c.Loc.GetValueOrDefault("@BUILDINGS_" + name);
            x.Stations.Add(new StudioRow("station", name, en, ru, null, new() { ["tier"] = b.Attribute("tier")?.Value },
                new JsonObject { ["crafts"] = b.Element("craftingitemlist")?.Elements("craftitem").Count() ?? 0 }));
        }
        x.Stations.Sort((a, b) => string.Compare(a.Name, b.Name, StringComparison.OrdinalIgnoreCase));
        return x;
    }

    public static StudioRow ItemRow((string? En, string? Ru) names, CatItem i, IconCache icons) => new("item", i.Name, names.En, names.Ru, icons.ForItem(i.El) ?? ModBuilder.SpriteOf(i.El),
        new()
        {
            ["tier"] = i.Tier.ToString(), ["type"] = i.Kind, ["slot"] = i.Slot.Length > 0 ? i.Slot : null,
            ["category"] = i.El.Attribute("shopcategory")?.Value, ["subcategory"] = i.El.Attribute("shopsubcategory1")?.Value,
        },
        new JsonObject
        {
            ["ip"] = i.El.Attribute("itempower")?.Value ?? i.El.Attribute("dummyitempower")?.Value, ["damage"] = i.El.Attribute("attackdamage")?.Value,
            ["craftable"] = i.El.Element("craftingrequirements") is not null,
        }, i.Pack);

    public static StudioRow SpellRow((string? En, string? Ru) names, CatSpell s, ModCatalog c, IconCache icons) => new("spell", s.Name, names.En, names.Ru, icons.ForSpell(s.El) ?? s.El.Attribute("uisprite")?.Value,
        new() { ["type"] = s.Kind, ["category"] = s.El.Attribute("category")?.Value, ["offered"] = c.OfferedBy.ContainsKey(s.Name) || s.Pack is not null ? "1" : null },
        new JsonObject
        {
            ["cooldown"] = s.El.Attribute("recastdelay")?.Value, ["energy"] = s.El.Attribute("energyusage")?.Value, ["range"] = s.El.Attribute("castrange")?.Value,
            ["items"] = c.OfferedBy.GetValueOrDefault(s.Name)?.Count ?? 0,
        }, s.Pack);

    public List<StudioRow> Of(string kind) => kind switch
    {
        "item" => Items, "spell" => Spells, "mob" => Mobs, "lootlist" => LootLists, "station" => Stations, _ => [],
    };

    /// <summary>
    /// Rows of <paramref name="kind"/> (plus <paramref name="extra"/>, the packs' own) matching every word of <paramref name="q"/> and
    /// every filter (facet = value; "tier" also as "6"); exact names first, then names that start with the query.
    /// </summary>
    public (int Total, List<StudioRow> Rows) Search(string kind, IEnumerable<StudioRow> extra, string q, IReadOnlyDictionary<string, string> filters, int offset, int limit)
    {
        var words = q.ToLowerInvariant().Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        IEnumerable<StudioRow> all = extra.Concat(Of(kind));
        foreach (var (facet, value) in filters)
            if (value.Length > 0) all = all.Where(r => string.Equals(r.Facets.GetValueOrDefault(facet), value, StringComparison.OrdinalIgnoreCase));
        if (words.Length > 0) all = all.Where(r => words.All(w => r.Key.Contains(w, StringComparison.Ordinal)));
        var list = all.ToList();
        if (q.Length > 0)
        {
            var needle = q.Trim();
            list = [.. list.Select((r, i) => (r, i, rank: string.Equals(r.Name, needle, StringComparison.OrdinalIgnoreCase) || string.Equals(r.En, needle, StringComparison.OrdinalIgnoreCase) || string.Equals(r.Ru, needle, StringComparison.OrdinalIgnoreCase) ? 0
                    : r.Name.StartsWith(needle, StringComparison.OrdinalIgnoreCase) || (r.En?.StartsWith(needle, StringComparison.OrdinalIgnoreCase) ?? false) || (r.Ru?.StartsWith(needle, StringComparison.OrdinalIgnoreCase) ?? false) ? 1 : 2))
                .OrderBy(x => x.rank).ThenBy(x => x.i).Select(x => x.r)];
        }
        return (list.Count, list.Skip(Math.Max(0, offset)).Take(Math.Clamp(limit, 1, 200)).ToList());
    }

    /// <summary>The values a facet takes (the filter drop-downs), most common first, at most 200.</summary>
    public JsonObject Facets(string kind)
    {
        var o = new JsonObject();
        var rows = Of(kind);
        foreach (var facet in rows.SelectMany(r => r.Facets.Keys).Distinct())
        {
            if (facet == "offered") continue;
            var values = rows.Select(r => r.Facets.GetValueOrDefault(facet)).OfType<string>().GroupBy(v => v, StringComparer.OrdinalIgnoreCase)
                .OrderBy(g => facet == "tier" && int.TryParse(g.Key, out var t) ? t : int.MaxValue).ThenByDescending(g => g.Count()).ThenBy(g => g.Key, StringComparer.OrdinalIgnoreCase).Take(200)
                .Select(g => (JsonNode)new JsonObject { ["value"] = g.Key, ["count"] = g.Count() });
            o[facet] = new JsonArray([.. values]);
        }
        return o;
    }
}
