using System.Text.Json.Nodes;
using AlbionLocal.Server.Data;
using AlbionLocal.Server.Entities;
using AlbionLocal.Server.Game;

namespace AlbionLocal.Server.Systems;

/// <summary>What one equipment slot's rule takes (content/extraspells.json "Rules" → "Slots" → name).</summary>
/// <param name="Count">How many spells the item grants before the bonuses.</param>
/// <param name="Own">Whether the item's own active spell lists' unchosen options are candidates (first, in list order; a weapon's slots round-robin Q, W, E).</param>
/// <param name="Map">Candidates by items.xml shopsubcategory2, then shopsubcategory1, then "*" (after the own options).</param>
public sealed record ExtraSlotRule(int Count, bool Own, IReadOnlyDictionary<string, string[]> Map);

/// <summary>A bonus: items in one of <paramref name="Slots"/> whose enchantment (or quality) is at least <paramref name="Min"/> grant <paramref name="Extra"/> more.</summary>
public sealed record ExtraBonusRule(IReadOnlyList<int> Slots, int Min, int Extra);

/// <summary>One spell an item grants and the rule that made it (the label of event 9005 / .extrabar explain).</summary>
/// <param name="Label">"unchosen" (an unchosen option of the item's own lists), "map:&lt;key&gt;" (the slot's table), "enchant:&lt;n&gt;",
/// "quality:&lt;n&gt;", "artifact" (the artifact bonus), "item:&lt;pattern&gt;" (an explicit Items entry).</param>
public sealed record ExtraPick(short Id, string Label, int Bar = 0, int Position = 0);

/// <summary>
/// The rule generators of the extra bars (user decision 2026-10-20; README "Extra action bars", SPEC §2.2): every worn item grants extra spells
/// derived from its own data, official spells only. Per equipment slot (<see cref="ExtraSlotRule"/>): the main hand its 2 unchosen options,
/// head/armour/shoes 1 unchosen option each, off hand / cape / bag / mount 1 from a table by item family. Bonuses: enchantment and quality
/// (<see cref="ExtraBonusRule"/>), artifact items (+1, their artifact-only options first). Picks skip toggles and passives, spells on the
/// player's action bar (Q-F), spells already granted, spells the player has not unlocked (RespectUnlocks) and spells unknown here.
/// Immutable; built from the "Rules" object of content/extraspells.json (absent = <see cref="Off"/>).
/// </summary>
public sealed class ExtraSpellRules
{
    public static readonly ExtraSpellRules Off = new() { Enabled = false };

    /// <summary>The slot names of the file (Inventory slot index).</summary>
    public static readonly (string Name, int Slot)[] SlotNames =
        [("MainHand", 0), ("OffHand", 1), ("Head", 2), ("Armor", 3), ("Shoes", 4), ("Bag", 5), ("Cape", 6), ("Mount", 7)];

    public static string NameOf(int slot) => SlotNames.FirstOrDefault(s => s.Slot == slot).Name ?? Inventory.SlotNames.ElementAtOrDefault(slot) ?? "?";

    public bool Enabled { get; init; } = true;

    /// <summary>Skip spells.xml unlockedtoequip="false" spells unless the player's destiny board unlocks everything (the Progression UnlockAll default).</summary>
    public bool RespectUnlocks { get; init; } = true;

    /// <summary>By Inventory slot index.</summary>
    public IReadOnlyDictionary<int, ExtraSlotRule> Slots { get; init; } = new Dictionary<int, ExtraSlotRule>();

    public IReadOnlyList<ExtraBonusRule> Enchantment { get; init; } = [];
    public IReadOnlyList<ExtraBonusRule> Quality { get; init; } = [];

    /// <summary>The artifact bonus: how many more, in which slots, and the name tags that mark an artifact besides an ARTEFACT recipe resource.</summary>
    public int ArtifactExtra { get; init; }
    public IReadOnlyList<int> ArtifactSlots { get; init; } = [];
    public IReadOnlyList<string> ArtifactTags { get; init; } = [];

    /// <summary>Reads the "Rules" object; problems are added to <paramref name="problems"/>, unknown spells checked against <paramref name="spells"/>.</summary>
    public static ExtraSpellRules Parse(JsonObject? o, string path, SpellData? spells, List<string> problems)
    {
        if (o is null) return Off;
        bool Flag(string k, bool d) => o[k] is JsonValue v && v.TryGetValue<bool>(out var b) ? b : d;
        int Int(JsonNode? n, int d) => n is JsonValue v && v.TryGetValue<int>(out var i) ? i : d;
        List<int> SlotList(JsonNode? n, string where)
        {
            var r = new List<int>();
            if (n is not JsonArray a) return r;
            foreach (var x in a)
                if (x is JsonValue v && v.TryGetValue<string>(out var s) && Slot(s) is { } slot) r.Add(slot);
                else problems.Add($"{path}: Rules.{where}: unknown slot {x} (slots: {string.Join(", ", SlotNames.Select(n => n.Name))})");
            return r;
        }
        string[] SpellList(JsonNode? n, string where)
        {
            var r = new List<string>();
            if (n is not JsonArray a) { problems.Add($"{path}: Rules.{where}: not a list of spells (left out)"); return []; }
            foreach (var x in a)
            {
                if (x is not JsonValue v || !v.TryGetValue<string>(out var name)) continue;
                name = name.Trim().ToUpperInvariant();
                if (spells is not null)
                {
                    var id = spells.IdOf(name);
                    if (id < 0) { problems.Add($"{path}: Rules.{where}: unknown spell {name} (left out)"); continue; }
                    if (!ExtraSpellTable.IsActiveSpell(spells, id)) { problems.Add($"{path}: Rules.{where}: {name} is not an activespell (left out)"); continue; }
                }
                r.Add(name);
            }
            return [.. r];
        }
        var slots = new Dictionary<int, ExtraSlotRule>();
        if (o["Slots"] is JsonObject so)
            foreach (var (key, node) in so)
            {
                if (Slot(key) is not { } slot) { if (key.Any(char.IsUpper)) problems.Add($"{path}: Rules.Slots: unknown slot {key} (left out)"); continue; }
                if (node is not JsonObject r) continue;
                var map = new Dictionary<string, string[]>(StringComparer.OrdinalIgnoreCase);
                if (r["Map"] is JsonObject m)
                    foreach (var (family, list) in m)
                        map[family.Trim()] = SpellList(list, $"Slots.{key}.Map.{family}");
                slots[slot] = new ExtraSlotRule(Math.Clamp(Int(r["Count"], 1), 0, 12), r["Own"] is JsonValue ov && ov.TryGetValue<bool>(out var own) && own, map);
            }
        List<ExtraBonusRule> Bonuses(string name)
        {
            var r = new List<ExtraBonusRule>();
            if (o[name] is not JsonArray a) return r;
            foreach (var x in a.OfType<JsonObject>())
                r.Add(new ExtraBonusRule(SlotList(x["Slots"], name), Int(x["Min"], int.MaxValue), Math.Clamp(Int(x["Extra"], 1), 0, 12)));
            return r;
        }
        var art = o["Artifact"] as JsonObject;
        return new ExtraSpellRules
        {
            Enabled = Flag("Enabled", true),
            RespectUnlocks = Flag("RespectUnlocks", true),
            Slots = slots,
            Enchantment = Bonuses("Enchantment"),
            Quality = Bonuses("Quality"),
            ArtifactExtra = Math.Clamp(Int(art?["Extra"], 0), 0, 12),
            ArtifactSlots = SlotList(art?["Slots"], "Artifact.Slots"),
            ArtifactTags = art?["Tags"] is JsonArray tags ? [.. tags.OfType<JsonValue>().Select(t => t.TryGetValue<string>(out var s) ? s.ToUpperInvariant() : "").Where(s => s.Length > 0)] : [],
        };
    }

    private static int? Slot(string name) =>
        SlotNames.FirstOrDefault(s => string.Equals(s.Name, name.Trim(), StringComparison.OrdinalIgnoreCase)) is { Name: not null } hit ? hit.Slot : null;

    // ---- artifacts (cached per item store / spell data)

    private sealed record ArtifactIndex(ItemStore Items, SpellData Spells, HashSet<string> Artifacts, HashSet<short> Common);

    private static ArtifactIndex? _index;

    /// <summary>
    /// The artifact items (a craftingrequirements resource named *_ARTEFACT_* other than the favour tokens) and the active spells of the
    /// non-artifact items: an artifact item's options outside that set are its own unique ones (ARMOR_PLATE_KEEPER FORCESHIELD,
    /// 2H_CLAYMORE_AVALON MAJESTIC_SMASH).
    /// </summary>
    private static ArtifactIndex Index(ItemStore items, SpellData spells)
    {
        if (_index is { } i && i.Items == items && i.Spells == spells) return i;
        var artifacts = new HashSet<string>(StringComparer.Ordinal);
        foreach (var t in items.All)
            if (t.Name == t.BaseName && t.Recipes.Any(r => r.Resources.Any(x => x.Name.Contains("_ARTEFACT_", StringComparison.Ordinal) && !x.Name.Contains("ARTEFACT_TOKEN", StringComparison.Ordinal))))
                artifacts.Add(t.Name);
        var common = new HashSet<short>();
        foreach (var (name, lists) in spells.Items)
            if (!artifacts.Contains(name))
                foreach (var l in lists.Active) common.UnionWith(l);
        return _index = new ArtifactIndex(items, spells, artifacts, common);
    }

    /// <summary>Whether <paramref name="t"/> is an artifact item: an ARTEFACT recipe resource (of its base item) or a name tag of the rules.</summary>
    public bool IsArtifact(ItemTemplate t, ItemStore items, SpellData spells) =>
        Index(items, spells).Artifacts.Contains(t.BaseName) || ArtifactTags.Any(tag => t.BaseName.Contains(tag, StringComparison.OrdinalIgnoreCase));

    /// <summary>The options of <paramref name="t"/>'s active lists that no non-artifact item has.</summary>
    public static HashSet<short> UniqueOptions(ItemTemplate t, ItemStore items, SpellData spells)
    {
        var index = Index(items, spells);
        var r = new HashSet<short>();
        if (spells.SlotsOf(t.Name) is { } lists)
            foreach (var l in lists.Active)
                foreach (var id in l)
                    if (!index.Common.Contains(id)) r.Add(id);
        return r;
    }

    // ---- planning

    /// <summary>What one item will grant: its ordered candidates (each with its label), how many before the bonuses, the bonus labels, its unique options.</summary>
    public sealed class Plan(ItemTemplate item, int slot)
    {
        public ItemTemplate Item { get; } = item;
        public int Slot { get; } = slot;
        public List<(short Id, string Label)> Candidates { get; } = [];
        public int Base { get; set; }
        public List<string> Bonuses { get; } = [];
        public HashSet<short> Unique { get; set; } = [];
        public bool Artifact { get; set; }
        public List<ExtraPick> Picks { get; } = [];

        /// <summary>How many of <see cref="Picks"/> (the first ones) are base picks; the rest are bonuses.</summary>
        public int BaseTaken { get; set; }
    }

    /// <summary>
    /// The plan of item <paramref name="t"/> (quality <paramref name="quality"/>) worn in Inventory slot <paramref name="slot"/> with the active
    /// choices <paramref name="chosen"/> (-1 none); null when no rule covers the slot.
    /// </summary>
    public Plan? PlanFor(ItemTemplate t, int quality, IReadOnlyList<short> chosen, int slot, ItemStore items, SpellData spells)
    {
        if (!Enabled || !Slots.TryGetValue(slot, out var rule)) return null;
        var plan = new Plan(t, slot) { Base = rule.Count };
        var added = new HashSet<short>();
        if (rule.Own && spells.SlotsOf(t.Name) is { } lists)
        {
            // Unchosen options, round-robin over the active slots (Q, W, E for a weapon), each slot's list in order.
            var per = lists.Active.Select((l, i) => l.Where(id => i >= chosen.Count || chosen[i] != id).ToArray()).ToArray();
            for (var k = 0; per.Any(l => k < l.Length); k++)
                foreach (var l in per)
                    if (k < l.Length && !chosen.Contains(l[k]) && added.Add(l[k])) plan.Candidates.Add((l[k], "unchosen"));
        }
        string? family = null;
        string[]? mapped = null;
        foreach (var key in new[] { t.Attrs.GetValueOrDefault("shopsubcategory2"), t.Attrs.GetValueOrDefault("shopsubcategory1"), "*" })
            if (key is { Length: > 0 } && rule.Map.TryGetValue(key, out mapped)) { family = key; break; }
        if (mapped is not null)
            foreach (var name in mapped)
                if (spells.IdOf(name) is var id and >= 0 && added.Add(id)) plan.Candidates.Add((id, $"map:{family}"));
        if (ArtifactExtra > 0 && ArtifactSlots.Contains(slot) && IsArtifact(t, items, spells))
        {
            plan.Artifact = true;
            plan.Unique = UniqueOptions(t, items, spells);
            for (var i = 0; i < ArtifactExtra; i++) plan.Bonuses.Add("artifact");
        }
        foreach (var b in Enchantment)
            if (b.Slots.Contains(slot) && t.Enchantment >= b.Min)
                for (var i = 0; i < b.Extra; i++) plan.Bonuses.Add($"enchant:{t.Enchantment}");
        foreach (var b in Quality)
            if (b.Slots.Contains(slot) && quality >= b.Min)
                for (var i = 0; i < b.Extra; i++) plan.Bonuses.Add($"quality:{quality}");
        return plan;
    }

    /// <summary>
    /// Takes the plans' spells: first every plan's base picks in the given order (an artifact's unique options kept for its artifact bonus), then
    /// every plan's bonuses (the artifact ones prefer the unique options). <paramref name="taken"/> (the action bar, spells granted already) grows.
    /// </summary>
    public static void Resolve(IReadOnlyList<Plan> plans, HashSet<short> taken, Func<int, bool> usable, SpellData spells)
    {
        bool Free(short id) => !taken.Contains(id) && ExtraSpellTable.IsActiveSpell(spells, id) && usable(id);
        void Take(Plan p, string? bonus)
        {
            var preferUnique = bonus == "artifact";
            var keepUnique = bonus is null && p.Artifact;
            (short Id, string Label)? hit = null;
            if (preferUnique)
                foreach (var c in p.Candidates)
                    if (p.Unique.Contains(c.Id) && Free(c.Id)) { hit = c; break; }
            if (hit is null)
                foreach (var c in p.Candidates)
                    if (Free(c.Id) && !(keepUnique && p.Unique.Contains(c.Id))) { hit = c; break; }
            if (hit is not { } h) return;
            taken.Add(h.Id);
            p.Picks.Add(new ExtraPick(h.Id, bonus ?? h.Label));
            if (bonus is null) p.BaseTaken++;
        }
        foreach (var p in plans)
            for (var i = 0; i < p.Base; i++) Take(p, null);
        foreach (var p in plans)
            foreach (var b in p.Bonuses) Take(p, b);
    }

    /// <summary>The spells the player may use (RespectUnlocks: unlocked by default, or a board that unlocks everything).</summary>
    public Func<int, bool> Usable(Player p)
    {
        if (!RespectUnlocks || p.World.Spells is not { } spells) return _ => true;
        if (p.Progression is null || p.World.GameData is { } data && p.Progression.UnlocksAll(data)) return _ => true;
        return id => !spells.IsLocked(id);
    }
}
