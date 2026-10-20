using System.Globalization;
using System.Xml;
using System.Xml.Linq;
using AlbionLocal.Server.Data;
using AlbionLocal.Server.Mods;

namespace AlbionLocal.Server.Web;

/// <summary>An item of the catalogue: the official element (or a pack's new one, <see cref="Pack"/> set).</summary>
public sealed record CatItem(string Name, string Kind, int Tier, string Slot, XElement El, string? Pack = null);

/// <summary>A spell of the catalogue (activespell, passivespell, togglespell).</summary>
public sealed record CatSpell(string Name, string Kind, XElement El, string? Pack = null);

/// <summary>
/// The mod editor's read-only view of the official GameData (modeditor SPEC §2): items and spells as XML elements, their EN/RU names
/// (only the @ITEMS_ and @SPELLS_ entries of localization.bin, streamed), which items offer which spell, the crafting stations of every
/// item (buildings.bin), and the official loot: which loot lists, mobs and chests name an item. Loaded once, off the world thread, from
/// the official folder (only read); nothing here is ever written.
/// </summary>
public sealed class ModCatalog
{
    public required string Folder { get; init; }
    public string? Build { get; init; }
    public List<CatItem> Items { get; } = [];
    public Dictionary<string, CatItem> ItemByName { get; } = new(StringComparer.OrdinalIgnoreCase);
    public List<CatSpell> Spells { get; } = [];
    public Dictionary<string, CatSpell> SpellByName { get; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>tuid → (EN-US, RU-RU) of the @ITEMS_ and @SPELLS_ entries.</summary>
    public Dictionary<string, (string? En, string? Ru)> Loc { get; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>spell → the items whose craftingspelllist offers it.</summary>
    public Dictionary<string, List<string>> OfferedBy { get; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>item → the stations (buildings with a craftingitemlist) that craft it, with their craftitem element.</summary>
    public Dictionary<string, List<(string Station, XElement CraftItem)>> StationsOf { get; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Every building with a craftingitemlist (a crafting station).</summary>
    public List<string> Stations { get; } = [];

    /// <summary>Every name used as a crafting resource by an official item (the ingredient picker).</summary>
    public SortedSet<string> Resources { get; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>mobs.bin: name → mobtypecategory.</summary>
    public Dictionary<string, string> Mobs { get; } = new(StringComparer.OrdinalIgnoreCase);

    public List<string> Chests { get; } = [];

    /// <summary>The studio's detail views: mobs.bin's &lt;Mob&gt;, loot.bin's &lt;Lootlist&gt; and the crafting stations of buildings.bin, by name.</summary>
    public Dictionary<string, XElement> MobEls { get; } = new(StringComparer.OrdinalIgnoreCase);
    public Dictionary<string, XElement> LootListEls { get; } = new(StringComparer.OrdinalIgnoreCase);
    public Dictionary<string, XElement> StationEls { get; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// The character models official equipment uses (<see cref="ModBuilder.MeshOf"/> of every weapon and equipmentitem) → the items
    /// that use it, lowest tier first: the studio's model picker and the check of a draft's mesh.
    /// </summary>
    public Dictionary<string, List<CatItem>> Models => _models ??= Items.Where(i => i.Kind is "weapon" or "equipmentitem")
        .GroupBy(i => ModBuilder.MeshOf(i.El), StringComparer.OrdinalIgnoreCase)
        .ToDictionary(g => g.Key, g => g.OrderBy(i => i.Tier).ToList(), StringComparer.OrdinalIgnoreCase);

    private Dictionary<string, List<CatItem>>? _models;

    /// <summary>Whether the client has this model: one an official item uses, or one of CustomMeshes/meshes.toc (as the modkit's check).</summary>
    public bool ModelKnown(string mesh) => Models.ContainsKey(mesh) || ModBuilder.TocModels(Folder).Contains(mesh);

    /// <summary>The icons official items use (<see cref="ModBuilder.SpriteOf"/>): the only ones a new item may take.</summary>
    public HashSet<string> Sprites => _sprites ??= new HashSet<string>(Items.Select(i => ModBuilder.SpriteOf(i.El)), StringComparer.OrdinalIgnoreCase);

    private HashSet<string>? _sprites;

    /// <summary>The localization prefixes kept from localization.bin (the rest of its 78 MB is skipped).</summary>
    private static readonly string[] LocPrefixes = ["@ITEMS_", "@SPELLS_", "@MOB_", "@BUILDINGS_"];

    private readonly Dictionary<string, List<string>> _itemLists = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, List<string>> _listParents = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, List<string>> _listUsers = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, List<string>> _itemUsers = new(StringComparer.OrdinalIgnoreCase);

    private static XElement LoadXml(string folder, string name)
    {
        var xml = GameDataFile.Open(folder, name).DecodeXml(out _);
        using var reader = XmlReader.Create(new MemoryStream(xml), new XmlReaderSettings
        {
            DtdProcessing = DtdProcessing.Prohibit, IgnoreWhitespace = true, IgnoreComments = true,
        });
        return XElement.Load(reader);
    }

    private static void Add(Dictionary<string, List<string>> map, string key, string value)
    {
        if (!map.TryGetValue(key, out var list)) map[key] = list = [];
        if (list.Count < 4000 && !list.Contains(value)) list.Add(value);
    }

    /// <summary>Reads the catalogue from <paramref name="folder"/> (items, spells and localization are needed; buildings, mobs, loot and lootchests are optional).</summary>
    public static ModCatalog Load(string folder)
    {
        var c = new ModCatalog { Folder = folder, Build = ModBuilder.ReadBuild(folder) };
        foreach (var e in LoadXml(folder, "items.bin").Elements())
        {
            if (e.Attribute("uniquename")?.Value is not { Length: > 0 } name || c.ItemByName.ContainsKey(name)) continue;
            var item = new CatItem(name, e.Name.LocalName, int.TryParse(e.Attribute("tier")?.Value, out var t) ? t : 0, e.Attribute("slottype")?.Value ?? "", e);
            c.Items.Add(item);
            c.ItemByName[name] = item;
            foreach (var cs in e.Descendants("craftspell"))
                if (cs.Attribute("uniquename")?.Value is { Length: > 0 } spell) Add(c.OfferedBy, spell, name);
            foreach (var r in e.Elements("craftingrequirements").Elements("craftresource"))
                if (r.Attribute("uniquename")?.Value is { Length: > 0 } res) c.Resources.Add(res);
        }
        foreach (var e in LoadXml(folder, "spells.bin").Elements())
        {
            if (e.Name.LocalName is not ("activespell" or "passivespell" or "togglespell")) continue;
            if (e.Attribute("uniquename")?.Value is not { Length: > 0 } name || c.SpellByName.ContainsKey(name)) continue;
            var spell = new CatSpell(name, e.Name.LocalName, e);
            c.Spells.Add(spell);
            c.SpellByName[name] = spell;
        }
        if (File.Exists(Path.Combine(folder, "buildings.bin")))
            foreach (var b in LoadXml(folder, "buildings.bin").Elements())
            {
                if (b.Attribute("uniquename")?.Value is not { Length: > 0 } station || b.Element("craftingitemlist") is not { } list) continue;
                c.Stations.Add(station);
                c.StationEls[station] = b;
                foreach (var ci in list.Elements("craftitem"))
                    if (ci.Attribute("uniquename")?.Value is { Length: > 0 } item)
                    {
                        if (!c.StationsOf.TryGetValue(item, out var at)) c.StationsOf[item] = at = [];
                        at.Add((station, ci));
                    }
            }
        if (File.Exists(Path.Combine(folder, "loot.bin")))
            foreach (var list in LoadXml(folder, "loot.bin").Descendants("Lootlist"))
            {
                if (list.Attribute("name")?.Value is not { Length: > 0 } name) continue;
                c.LootListEls.TryAdd(name, list);
                foreach (var i in list.Descendants("Item"))
                    if (i.Attribute("type")?.Value is { Length: > 0 } item) Add(c._itemLists, item, name);
                foreach (var r in list.Descendants("LootListReference"))
                    if (r.Attribute("name")?.Value is { Length: > 0 } child) Add(c._listParents, child, name);
            }
        if (File.Exists(Path.Combine(folder, "mobs.bin")))
            foreach (var mob in LoadXml(folder, "mobs.bin").Elements("Mob"))
            {
                if (mob.Attribute("uniquename")?.Value is not { Length: > 0 } name) continue;
                c.Mobs[name] = mob.Attribute("mobtypecategory")?.Value ?? "";
                c.MobEls[name] = mob;
                c.Users(mob, "mob " + name);
            }
        if (File.Exists(Path.Combine(folder, "lootchests.bin")))
            foreach (var chest in LoadXml(folder, "lootchests.bin").Elements("LootChest"))
            {
                if (chest.Attribute("uniquename")?.Value is not { Length: > 0 } name) continue;
                c.Chests.Add(name);
                c.Users(chest, "chest " + name);
            }
        c.LoadNames(folder);
        return c;
    }

    private void Users(XElement owner, string who)
    {
        foreach (var r in owner.Descendants("LootListReference"))
            if (r.Attribute("name")?.Value is { Length: > 0 } list) Add(_listUsers, list, who);
        foreach (var i in owner.Descendants("Item"))
            if (i.Attribute("type")?.Value is { Length: > 0 } item) Add(_itemUsers, item, who);
    }

    /// <summary>localization.bin streamed (78 MB of XML): only the @ITEMS_, @SPELLS_, @MOB_ and @BUILDINGS_ entries, EN-US and RU-RU.</summary>
    private void LoadNames(string folder)
    {
        using var r = GameDataFile.Open(folder, "localization.bin").CreateReader();
        var lang = XNamespace.Xml + "lang";
        while (!r.EOF)
        {
            if (r.NodeType != XmlNodeType.Element || r.LocalName != "tu") { r.Read(); continue; }
            var tuid = r.GetAttribute("tuid");
            if (tuid is null || !LocPrefixes.Any(p => tuid.StartsWith(p, StringComparison.Ordinal))) { r.Skip(); continue; }
            var tu = (XElement)XNode.ReadFrom(r);
            string? Text(string l) => tu.Elements("tuv").FirstOrDefault(t => string.Equals((string?)t.Attribute(lang), l, StringComparison.OrdinalIgnoreCase))?.Element("seg")?.Value;
            Loc[tuid] = (Text("EN-US"), Text("RU-RU"));
        }
    }

    /// <summary>The mobs and chests whose loot names <paramref name="item"/>, directly or through loot lists (up to 4 levels of lists).</summary>
    public List<string> DropSources(string item, int max = 60)
    {
        var found = new List<string>();
        void Take(IEnumerable<string>? who)
        {
            foreach (var w in who ?? [])
                if (found.Count < max && !found.Contains(w)) found.Add(w);
        }
        Take(_itemUsers.GetValueOrDefault(item));
        var lists = new HashSet<string>(_itemLists.GetValueOrDefault(item) ?? [], StringComparer.OrdinalIgnoreCase);
        var frontier = lists.ToList();
        for (var depth = 0; depth < 4 && frontier.Count > 0 && lists.Count < 2000; depth++)
        {
            var next = new List<string>();
            foreach (var l in frontier)
                foreach (var p in _listParents.GetValueOrDefault(l) ?? [])
                    if (lists.Add(p)) next.Add(p);
            frontier = next;
        }
        foreach (var l in lists) Take(_listUsers.GetValueOrDefault(l));
        return found;
    }

    /// <summary>The loot lists that name <paramref name="item"/> directly.</summary>
    public IReadOnlyList<string> LootLists(string item) => _itemLists.GetValueOrDefault(item) ?? [];

    /// <summary>The mobs and chests that use loot list <paramref name="list"/> directly.</summary>
    public IReadOnlyList<string> ListUsers(string list) => _listUsers.GetValueOrDefault(list) ?? [];

    /// <summary>The loot lists that reference <paramref name="list"/>.</summary>
    public IReadOnlyList<string> ListParents(string list) => _listParents.GetValueOrDefault(list) ?? [];

    public static string MobNameKey(XElement mob) =>
        mob.Attribute("namelocatag")?.Value is { Length: > 1 } k ? k : "@MOB_" + mob.Attribute("uniquename")?.Value;

    public static string SpellNameKey(XElement spell) =>
        spell.Attribute("namelocatag")?.Value is { Length: > 1 } k ? k : "@SPELLS_" + spell.Attribute("uniquename")?.Value;

    public static string SpellDescKey(XElement spell) =>
        spell.Attribute("descriptionlocatag")?.Value is { Length: > 1 } k ? k : SpellNameKey(spell) + "_DESC";

    /// <summary>A number attribute of an element, invariant culture; null when missing or not a number.</summary>
    public static double? Num(XElement e, string attr) =>
        double.TryParse(e.Attribute(attr)?.Value, NumberStyles.Float, CultureInfo.InvariantCulture, out var v) ? v : null;
}
