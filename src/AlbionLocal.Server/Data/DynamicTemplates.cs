using System.Xml.Linq;
using static AlbionLocal.Server.Data.ClusterNameInfo;

namespace AlbionLocal.Server.Data;

/// <summary>
/// The dynamic templates (stage 4b, analysis/work/stage4b/SPEC.md §4.3; port placement.py dyn_roll): the client rolls,
/// when it loads a cluster with feature 112 DynamicTemplates on, which dynamic template each dyn slot of the cluster gets
/// (bal.a 0x180559AD0, from ciy.ar 0x180971800), from the same date seed as the resource placement. A slot that gets one
/// loads it instead of its default template (a83.w 0x18052A460), so the server must roll the same way to know the
/// cluster's static objects. dynamicclusters.xml lists the slots (bzj / bzk), dynamictemplates.xml the pools per
/// (continent, biome, tier, pvp warning level) (byg / byf / bfc). The time-seeded requiredslottags branch and unique
/// entries are not in the current data (the roll refuses the first, honours the second).
/// </summary>
public sealed class DynamicTemplates
{
    private static readonly Dictionary<int, string> SlotSizes = new() { [0] = "small", [1] = "medium" };          // Albion SlotSize
    private static readonly Dictionary<int, string> DynSlotSizes = new() { [0] = "none", [1] = "small", [2] = "medium" }; // a8q.DynamicTemplateSlotSize
    private static readonly Dictionary<int, string> Warnings = new() { [0] = "safe", [1] = "yellow", [2] = "orange", [3] = "red", [4] = "black" };

    private readonly Dictionary<string, Pool> _pools;
    private readonly Dictionary<(int Continent, int Biome, int Tier, int Warning), string?> _mapping;
    private readonly Dictionary<string, List<(string Slot, int Size)>> _slots;

    private DynamicTemplates(Dictionary<string, Pool> pools, Dictionary<(int, int, int, int), string?> mapping, Dictionary<string, List<(string, int)>> slots)
    {
        _pools = pools;
        _mapping = mapping;
        _slots = slots;
    }

    /// <summary>byg.da 0x1807C1F70 (dynamictemplates.xml) and bzj.da 0x1807CEE20 / bzk.b 0x1807CF280 (dynamicclusters.xml).</summary>
    public static DynamicTemplates Load(XElement templates, XElement clusters)
    {
        var pools = new Dictionary<string, Pool>(StringComparer.Ordinal);
        foreach (var tp in templates.Descendants("templatepool"))
            if (Pool.Read(tp) is { Id.Length: > 0 } pool) pools[pool.Id] = pool; // set_Item: the last one wins
        var mapping = new Dictionary<(int, int, int, int), string?>();
        foreach (var m in templates.Element("templatemappingpool")?.Elements("mapping") ?? [])
        {
            var continent = EnumParse(ContinentNames, (string?)m.Attribute("continent"), 0);
            var tier = XmlAttr.Int(m, "clustertier", -1);
            if (tier is < 1 or > 8) continue;
            var warning = EnumParse(Warnings, (string?)m.Attribute("pvpwarninglevel"), 4);
            foreach (var b in SplitList(XmlAttr.Str(m, "biome")))
                if (Lookup(Biomes, b) is var bi and not -1) mapping.TryAdd((continent, bi, tier, warning), (string?)m.Attribute("templatepool")); // duplicates skipped
        }
        var slots = new Dictionary<string, List<(string, int)>>(StringComparer.Ordinal);
        foreach (var c in clusters.Elements("cluster"))
        {
            var key = XmlAttr.Str(c, "name").Split('_', 2)[0]; // bgv.i: the name before the first '_'
            var list = new List<(string, int)>();
            foreach (var s in c.Elements("dyntemplateslot"))
            {
                var size = EnumParse(DynSlotSizes, (string?)s.Attribute("size"), 0);
                var id = (string?)s.Attribute("id");
                if (size == 0 || id is null || list.Any(x => x.Item1 == id)) continue;
                list.Add((id, size));
            }
            slots.TryAdd(key, list);
        }
        return new DynamicTemplates(pools, mapping, slots);
    }

    /// <summary>The dyn slots of cluster <paramref name="clusterId"/> (cni.Id) in file order; empty when it has none.</summary>
    public IReadOnlyList<(string Slot, int Size)> Slots(string clusterId) => _slots.GetValueOrDefault(clusterId) ?? [];

    /// <summary>
    /// bal.a(CNI, PvpWarningLevel, flags, GameTimeStamp, bcm) 0x180559AD0: slot → the rolled template; slots that keep their
    /// default ("default" category, or a category without a template left) are not in it. One MT19937 seeded with
    /// d0.a(cni.b(), key 57) serves the whole roll. <paramref name="seed"/> gets the seed string, empty when nothing rolls.
    /// </summary>
    public IReadOnlyDictionary<string, DynamicRoll> Roll(ClusterNameInfo cni, int pvpWarning, IReadOnlySet<string>? flags, long key57, out string seed)
    {
        seed = "";
        var result = new Dictionary<string, DynamicRoll>(StringComparer.Ordinal);
        if (!_slots.TryGetValue(cni.Id, out var slots)) return result;
        var seedText = cni.ShortName;
        var rng = new ClientRandom(DaySeed.Of(seedText, key57));
        if (_mapping.GetValueOrDefault((cni.Continent, cni.Biome, cni.Tier, pvpWarning)) is not { } poolName || !_pools.TryGetValue(poolName, out var pool))
            return result;
        seed = DaySeed.Text(seedText, key57);
        // bfg<byc> ctor 0x182916C70 (no draws): per size the categories of weight > 0; per category the templates of weight
        // > 0 that pass the flag filter (bfh.a 0x1829185B0) and its rolllayertag lists.
        var hasTags = false;
        var bySize = new Dictionary<int, WeightedPicks<Entry>>();
        foreach (var (size, categories) in pool.Sizes)
        {
            var picks = bySize[size] = new WeightedPicks<Entry>(rng);
            foreach (var cat in categories)
            {
                if (cat.Weight <= 0) continue;
                var entry = new Entry(cat, new WeightedPicks<Template>(rng), [.. cat.Rolls.Where(r => r.Count > 0).Select(r => new WeightedPicks<string>(rng, r))]);
                picks.Add(entry, cat.Weight);
                hasTags |= cat.RequiredSlotTags is not null;
                foreach (var t in cat.Templates)
                    if (t.Weight > 0 && (t.Flags is null || flags is not null && t.Flags.Any(f => f.Length > 0 && flags.Contains(f))))
                        entry.Templates.Add(t, t.Weight);
            }
        }
        foreach (var (slot, size) in slots)
        {
            var slotSize = size == 1 ? 0 : 1;
            if (!bySize.TryGetValue(slotSize, out var categories)) throw new InvalidDataException($"dyn slot {slot}: pool {poolName} has no {SlotSizes[slotSize]} size");
            if (hasTags) throw new InvalidDataException($"pool {poolName}: requiredslottags, which the client rolls with a time-seeded generator");
            var entry = categories.Pick(e => e.Category.Unique) ?? throw new InvalidDataException($"dyn slot {slot}: no category");
            if (entry.Category.Name == "default") continue;
            var tags = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var roll in entry.Rolls)
                if (roll.Peek() is { } tag) tags.Add(tag);
            if (entry.Templates.Pick(t => t.Unique) is not { } template) continue; // "TemplatePool-Cat ... Exhausted"
            if (template.LayerTags is not null) tags.UnionWith(template.LayerTags);
            if (template.ForceFaction is { } forced) tags.Add(FactionNames.GetValueOrDefault(forced, forced.ToString(System.Globalization.CultureInfo.InvariantCulture)).ToUpperInvariant());
            else if (cni.Faction != 0) tags.Add(cni.FactionTag);
            result[slot] = new DynamicRoll(slot, entry.Category.Name, template.Id, tags);
        }
        return result;
    }

    private sealed record Entry(Category Category, WeightedPicks<Template> Templates, List<WeightedPicks<string>> Rolls);

    /// <summary>byc : bfb (bfb.mq 0x180653190).</summary>
    private sealed record Template(string Id, long Weight, bool Unique, string[]? Flags, string[]? LayerTags, int? ForceFaction);

    /// <summary>bfc.a: a category of a size (type@category).</summary>
    private sealed record Category(string Name, long Weight, bool Unique, string[]? RequiredSlotTags, List<Template> Templates, List<List<(string, long)>> Rolls);

    /// <summary>byf : bfc, parsed by bfc.o 0x180653460: per size its categories in file order.</summary>
    private sealed record Pool(string Id, List<(int Size, List<Category> Categories)> Sizes)
    {
        public static Pool Read(XElement e)
        {
            var sizes = new List<(int, List<Category>)>();
            foreach (var s in e.Elements("size"))
            {
                var size = EnumParse(SlotSizes, (string?)s.Attribute("name"), 0);
                var cats = new List<Category>();
                foreach (var t in s.Elements("type"))
                {
                    var name = XmlAttr.Str(t, "category");
                    if (name.Length == 0) continue;
                    var templates = new List<Template>();
                    var rolls = new List<List<(string, long)>>();
                    foreach (var c in t.Elements())
                    {
                        if (c.Name.LocalName == "template" && XmlAttr.Str(c, "id") is { Length: > 0 } id)
                        {
                            var ff = (string?)c.Attribute("forcefaction");
                            templates.Add(new Template(id, A7m.Parse((string?)c.Attribute("weight"), A7m.One), XmlAttr.Bool(c, "unique", false), XmlAttr.Flags(c, "flags"),
                                (string?)c.Attribute("layertags") is { } lt ? Split(lt, ",; |").Select(x => x.Trim()).Where(x => x.Length > 0).ToArray() : null,
                                string.IsNullOrEmpty(ff) || EnumParse(FactionNames, ff, -1) is not (>= 0 and var faction) ? null : faction)); // unknown = none
                        }
                        else if (c.Name.LocalName == "rolllayertag") // bff.b 0x1806545B0
                            rolls.Add([.. c.Elements("layertag").Select(l => (XmlAttr.Str(l, "name"), A7m.Parse((string?)l.Attribute("weight"), A7m.One)))]);
                    }
                    if (templates.Count == 0 && name != "default") continue; // byf.rl(): only "default" may be empty
                    if (cats.Any(x => x.Name == name)) continue; // "Duplicate category found"
                    var required = (string?)t.Attribute("requiredslottags") is { } rst ? Split(rst, ",; ") : null;
                    cats.Add(new Category(name, A7m.Parse((string?)t.Attribute("weight"), A7m.One), XmlAttr.Bool(t, "unique", false),
                        required is { Length: > 0 } ? required : null, templates, rolls));
                }
                if (cats.Count > 0 && sizes.All(x => x.Item1 != size)) sizes.Add((size, cats));
            }
            return new Pool(XmlAttr.Str(e, "id"), sizes);
        }
    }
}

/// <summary>A dyn slot's rolled template: its category, the template id and the layer tags (rolled tags, template tags, faction).</summary>
public sealed record DynamicRoll(string Slot, string Category, string Template, IReadOnlySet<string> Tags)
{
    /// <summary>DynamicTemplateFeatureLayer (DynamicTemplate 604 key 2): NotInitialized 0, Default 1, FactionWarfare 2.</summary>
    public const byte Default = 1, FactionWarfare = 2;

    /// <summary>DynamicTemplateState (604 key 1): Active 1, Inactive 2.</summary>
    public const byte Active = 1, Inactive = 2;

    /// <summary>
    /// The feature layer the slot shows (stageQ camps, <see cref="Systems.FactionCamps"/>): FactionWarfare for a mob camp of a faction warfare
    /// zone FactionWarfare:Camps picks (its *_DYN_VAR_FWARFARE_* layers: the Caerleon camp, chest and open quest), else Default.
    /// </summary>
    public byte FeatureLayer { get; init; } = Default;

    /// <summary>Whether the rolled template has VAR layers for its tags: the client shows none of them before a DynamicTemplate 604 (stageQ camps).</summary>
    public bool Switched { get; init; }

    /// <summary>
    /// Whether layer <paramref name="name"/> of the rolled template is on. The layers are named "&lt;TAG&gt;_DYN_&lt;part&gt;"
    /// (UNDEAD_DYN_STATIC, T5_DYN_PERSIST, KEEPER_DYN_VAR_DEFAULT_ACTIVE, …); a tag of the roll picks its layers (the client's ba2.s
    /// 0x18056BC20: the upper-case parts before DYN). On: STATIC and PERSIST (the captured statics of 1207 slot 04 and 0204 slot 21 are
    /// exactly '' + UNDEAD_DYN_STATIC, SPEC §4.3, HARVEST.md), and the STATIC and ACTIVE parts of the slot's <see cref="FeatureLayer"/>
    /// (VAR_DEFAULT_* or VAR_FWARFARE_*), the state the server keeps it in while its camp stands (stageQ camps: the client's bak.h
    /// 0x180558FA0 switches to the (feature layer, state) layers of a DynamicTemplate event; the INACTIVE parts hold no server object).
    /// </summary>
    public bool LayerOn(string? name)
    {
        if (Parse(name) is not { } part) return false;
        var feature = FeatureLayer == FactionWarfare ? "FWARFARE" : "DEFAULT";
        return part is "STATIC" or "PERSIST" || part == $"VAR_{feature}_STATIC" || part == $"VAR_{feature}_ACTIVE";
    }

    /// <summary>Whether <paramref name="name"/> is one of the roll's VAR layers of feature layer <paramref name="feature"/> (null: of any).</summary>
    public bool IsVar(string? name, byte? feature = null) =>
        Parse(name) is { } part && part.StartsWith("VAR_", StringComparison.Ordinal)
        && (feature is null || part.StartsWith(feature == FactionWarfare ? "VAR_FWARFARE_" : "VAR_DEFAULT_", StringComparison.Ordinal));

    /// <summary>The part after "&lt;TAG&gt;_DYN_" of a layer of one of the roll's tags; null for another layer.</summary>
    private string? Parse(string? name)
    {
        if (name is null) return null;
        var n = name.Trim();
        var at = n.IndexOf("_DYN_", StringComparison.Ordinal);
        return at <= 0 || !Tags.Contains(n[..at]) ? null : n[(at + 5)..];
    }
}
