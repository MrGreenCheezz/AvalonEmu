using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using AlbionLocal.Photon;
using AlbionLocal.Plugin.Api;
using AlbionLocal.Server.Data;
using AlbionLocal.Server.Entities;
using AlbionLocal.Server.Game;
using AlbionLocal.Server.Network;
using AlbionLocal.Server.Protocol;

namespace AlbionLocal.Server.Systems;

/// <summary>One extra spell an item grants: the spell, and optionally where it prefers to go (bar 1-2, position 1-6; 0 = anywhere).</summary>
public sealed record ExtraSpellEntry(string Spell, int Bar = 0, int Position = 0);

/// <summary>An entry of an extraspells.json: an item name or a pattern (* and ?, matched against the name with and without "@n") and its spells.</summary>
public sealed record ExtraSpellRule(string Pattern, string Source, IReadOnlyList<ExtraSpellEntry> Spells)
{
    internal Regex? Glob { get; init; }

    public bool Exact => Glob is null;

    public bool Matches(string item, string baseName) =>
        Glob is { } g ? g.IsMatch(item) || g.IsMatch(baseName) : string.Equals(Pattern, item, StringComparison.OrdinalIgnoreCase) || string.Equals(Pattern, baseName, StringComparison.OrdinalIgnoreCase);
}

/// <summary>
/// The extra spells of the items (analysis/work/stageN/extrabars/SPEC.md §2): content/extraspells.json (the built-in defaults and the bar layout)
/// and mods/&lt;pack&gt;/server/extraspells.json of the packs in Mods:Packs. A file is either {"Bars": 2, "BarSize": 6, "Items": {…}} or the
/// item map itself {"ITEM_OR_PATTERN": ["SPELL", {"spell": "SPELL", "bar": 2, "position": 3}], …}; keys that are not item names ("note",
/// "$schema", …) are skipped. Lenient: a bad file or entry is warned about and left out (the Studio writes these files while the server runs).
/// </summary>
public sealed class ExtraSpellTable
{
    public static readonly ExtraSpellTable Empty = new([], 2, 6, []);

    /// <summary>The rule generators (content/extraspells.json "Rules"; absent = off): what the worn items grant when no Items entry names them.</summary>
    public ExtraSpellRules Generators { get; init; } = ExtraSpellRules.Off;

    /// <summary>The rules in precedence order: exact names first, then patterns, each in file order (content, then the packs).</summary>
    public IReadOnlyList<ExtraSpellRule> Rules { get; }

    /// <summary>How many bars (0-2; 0 = the feature is off) and slots per bar (1-6).</summary>
    public int Bars { get; }
    public int Size { get; }

    /// <summary>What was wrong while reading (warned at the load; .extrabar reload lists them).</summary>
    public IReadOnlyList<string> Problems { get; }

    public int Slots => Bars * Size;

    public ExtraSpellTable(IReadOnlyList<ExtraSpellRule> rules, int bars, int size, IReadOnlyList<string> problems)
    {
        Rules = [.. rules.Where(r => r.Exact), .. rules.Where(r => !r.Exact)];
        Bars = bars;
        Size = size;
        Problems = problems;
    }

    /// <summary>The extra spells of item <paramref name="item"/> (uniquename, with or without "@n"), in precedence order.</summary>
    public IEnumerable<ExtraSpellEntry> For(string item) => Matching(item).Select(m => m.Entry);

    /// <summary>The explicit entries for <paramref name="item"/> with the rule (pattern) that names each, in precedence order.</summary>
    public IEnumerable<(ExtraSpellRule Rule, ExtraSpellEntry Entry)> Matching(string item)
    {
        var at = item.IndexOf('@');
        var baseName = at < 0 ? item : item[..at];
        foreach (var rule in Rules)
            if (rule.Matches(item, baseName))
                foreach (var e in rule.Spells) yield return (rule, e);
    }

    private static readonly Regex ItemKey = new(@"^[A-Za-z0-9_*?@]+$", RegexOptions.CultureInvariant);

    /// <summary>Reads content/extraspells.json and the packs' server/extraspells.json; <paramref name="spells"/> checks the names (null: not checked).</summary>
    public static ExtraSpellTable Load(string contentDir, string modsDir, IReadOnlyList<string> packs, SpellData? spells)
    {
        var problems = new List<string>();
        var rules = new List<ExtraSpellRule>();
        int bars = 2, size = 6;
        var generators = ExtraSpellRules.Off;
        Read(Path.Combine(contentDir, "extraspells.json"), "content", spells, rules, problems, ref bars, ref size, layout: true, ref generators);
        foreach (var pack in packs)
            Read(Path.Combine(modsDir, pack, "server", "extraspells.json"), pack, spells, rules, problems, ref bars, ref size, layout: false, ref generators);
        foreach (var p in problems) Log.Warn("extrabars", p);
        var table = new ExtraSpellTable(rules, bars, size, problems) { Generators = generators };
        Log.Info("extrabars", $"{table.Rules.Count} item entries with extra spells, rules {(generators.Enabled ? $"on ({generators.Slots.Count} slots)" : "off")}, {bars} bar(s) of {size}");
        return table;
    }

    private static void Read(string path, string source, SpellData? spells, List<ExtraSpellRule> rules, List<string> problems, ref int bars, ref int size, bool layout,
        ref ExtraSpellRules generators)
    {
        if (!File.Exists(path)) return;
        JsonObject? root;
        try { root = JsonNode.Parse(File.ReadAllText(path), documentOptions: new JsonDocumentOptions { CommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true }) as JsonObject; }
        catch (Exception e) when (e is JsonException or IOException)
        {
            problems.Add($"{path}: {e.Message} (file left out)");
            return;
        }
        if (root is null) { problems.Add($"{path}: not a JSON object (file left out)"); return; }
        if (layout)
        {
            if (root["Bars"] is JsonValue b && b.TryGetValue<int>(out var nb)) bars = Math.Clamp(nb, 0, 2);
            if (root["BarSize"] is JsonValue s && s.TryGetValue<int>(out var ns)) size = Math.Clamp(ns, 1, 6);
            generators = ExtraSpellRules.Parse(root["Rules"] as JsonObject, path, spells, problems);
        }
        var items = root["Items"] as JsonObject ?? root;
        foreach (var (key, value) in items)
        {
            if (!ItemKey.IsMatch(key) || key is "Bars" or "BarSize" or "Items" or "Rules" || key.All(c => !char.IsUpper(c))) continue; // note, $schema, …
            if (value is not JsonArray list) { problems.Add($"{path}: {key}: not a list of spells (left out)"); continue; }
            var entries = new List<ExtraSpellEntry>();
            foreach (var node in list)
            {
                ExtraSpellEntry? e = node switch
                {
                    JsonValue v when v.TryGetValue<string>(out var name) => new ExtraSpellEntry(name.Trim().ToUpperInvariant()),
                    JsonObject o when o["spell"]?.GetValue<string>() is { } name => new ExtraSpellEntry(name.Trim().ToUpperInvariant(),
                        o["bar"] is JsonValue bv && bv.TryGetValue<int>(out var bar) ? Math.Clamp(bar, 0, 2) : 0,
                        o["position"] is JsonValue pv && pv.TryGetValue<int>(out var pos) ? Math.Clamp(pos, 0, 6) : 0),
                    _ => null,
                };
                if (e is null) { problems.Add($"{path}: {key}: an entry is neither \"SPELL\" nor {{\"spell\": …}} (left out)"); continue; }
                if (spells is not null)
                {
                    var id = spells.IdOf(e.Spell);
                    if (id < 0) { problems.Add($"{path}: {key}: unknown spell {e.Spell} (left out)"); continue; }
                    if (!IsActiveSpell(spells, id)) { problems.Add($"{path}: {key}: {e.Spell} is not an activespell (only those go into the extra bars; left out)"); continue; }
                }
                entries.Add(e);
            }
            if (entries.Count == 0) continue;
            var glob = key.AsSpan().IndexOfAny('*', '?') >= 0
                ? new Regex("^" + Regex.Escape(key).Replace("\\*", ".*").Replace("\\?", ".") + "$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)
                : null;
            rules.Add(new ExtraSpellRule(key.ToUpperInvariant(), source, entries) { Glob = glob });
        }
    }

    /// <summary>An activespell (not a toggle, not a passive): what an extra slot holds.</summary>
    public static bool IsActiveSpell(SpellData spells, int id) => spells.Template(id) is { } t ? t.Kind == "activespell" : spells.IsActive(id);
}

/// <summary>The extra slots of one player as last built (one per <see cref="Player"/> object: rebuilt at every Join).</summary>
public sealed class ExtraBarState
{
    /// <summary>Spell id per slot (-1 empty).</summary>
    public short[] Spells = [];

    /// <summary>The equipment slot of the item that grants it (-1: an override or empty).</summary>
    public int[] Equipment = [];

    /// <summary>The granting item's uniquename (null: an override or empty).</summary>
    public string?[] Items = [];

    /// <summary>0 empty, 1 a worn item, 2 an override (plugin, rule, GM).</summary>
    public byte[] Sources = [];

    /// <summary>The rule that put the spell there (<see cref="ExtraPick.Label"/>; "override"; null empty).</summary>
    public string?[] Labels = [];

    public bool Built;
}

/// <summary>
/// The extra action bars (analysis/work/stageN/extrabars/SPEC.md; user decision 2026-10-20): two bars of six slots (Shift+1..6, Ctrl+1..6 in the
/// client mod) filled from the worn items' extra spells (<see cref="ExtraSpellTable"/>) in a fixed equipment order — main hand, off hand, head,
/// armour, shoes, cape, bag, mount, food, potion — first those with a preferred bar/position, then the rest in order, each spell once; the
/// plugins' overrides (<see cref="IPlayer.SetExtraSlot"/>, rules extrabar.set, GM .extrabar set) go over it (kept in memory per character, not saved).
/// Rebuilt when the equipment changes (OnEquip/OnUnequip, the equipment events), at Join, at a reload and before an extra cast. Nothing is
/// saved: the bars are derived from the gear.
/// <para>The client mod's channel (opt-in per connection): op 9001 hello → event 9001 the bars (again on every change and Join), event 9002 the
/// cooldowns after an extra slot's spell was cast or a plugin changed cooldowns, event 9003 a refused extra cast. A vanilla client never gets
/// one of them. The cast itself: CastStart in slot 25 with the spell's id (the client's own fake-slot cast, preferred) or op 9002
/// (<see cref="WorldSession"/> ExtraBarHandler). World thread only.</para>
/// </summary>
public sealed class ExtraBars(World world)
{
    /// <summary>The equipment slots in the bars' order: main hand, off hand, head, armour, shoes, cape, bag, mount, food, potion.</summary>
    public static readonly int[] Order = [0, 1, 2, 3, 4, 6, 5, 7, 9, 8];

    private ExtraSpellTable? _table;
    private readonly ConditionalWeakTable<Player, ExtraBarState> _states = new();
    private readonly Dictionary<Guid, Dictionary<int, short>> _overrides = [];
    private readonly Dictionary<Guid, int> _clients = [];
    private int _revision, _catalogRevision = 1;

    /// <summary>The item table: loaded on first use from World.Config (content, Mods:Packs), replaced by <see cref="Reload"/> or a test.</summary>
    public ExtraSpellTable Table
    {
        get => _table ??= ExtraSpellTable.Load(world.Config.ContentPath, world.Config.ModsPath, world.Config.ModPacks, world.Spells);
        set => _table = value;
    }

    /// <summary>Reads the files again and rebuilds every joined player's bars (Studio Generate, GM .extrabar reload).</summary>
    public ExtraSpellTable Reload()
    {
        _table = ExtraSpellTable.Load(world.Config.ContentPath, world.Config.ModsPath, world.Config.ModPacks, world.Spells);
        _catalogRevision++;
        foreach (var map in world.Maps.Maps.ToList())
            foreach (var p in map.Players.Values.ToList())
            {
                Refresh(p, ExtraBarChangeSource.Equipment);
                SendCatalog(p);
            }
        return _table;
    }

    /// <summary>Whether <paramref name="p"/>'s connection runs the mod (said hello).</summary>
    public static bool Modded(Player p) => p.Session.ExtraBarsProtocol > 0;

    public ExtraBarState State(Player p)
    {
        var st = _states.GetValue(p, _ => new ExtraBarState());
        if (!st.Built) Refresh(p, ExtraBarChangeSource.Equipment);
        return st;
    }

    /// <summary>The slots' spell uniquenames (null = empty).</summary>
    public IReadOnlyList<string?> Names(Player p)
    {
        var st = State(p);
        return [.. st.Spells.Select(id => id < 0 ? null : world.Spells?.Name(id))];
    }

    /// <summary>The slot that holds spell <paramref name="spellId"/>, or -1.</summary>
    public int IndexOf(Player p, int spellId) => spellId < 0 || p.IsBot ? -1 : Array.IndexOf(State(p).Spells, (short)spellId);

    /// <summary>The worn item that grants slot <paramref name="index"/>'s spell; null for an override or an empty slot.</summary>
    public Item? ItemAt(Player p, int index)
    {
        var st = State(p);
        return index >= 0 && index < st.Equipment.Length && st.Equipment[index] is var eq and >= 0 ? p.Inventory.Equipment[eq] : null;
    }

    /// <summary>One built slot list: per slot the spell, the equipment slot and item that grant it, the source and the rule label.</summary>
    private sealed record Built(short[] Spells, int[] Equipment, string?[] Items, byte[] Sources, string?[] Labels, List<string> Explain);

    /// <summary>The spell ids on <paramref name="p"/>'s action bar: the worn items' chosen actives (Q-F).</summary>
    private static HashSet<short> ActionBar(Player p)
    {
        var r = new HashSet<short>();
        for (var eq = 0; eq < Math.Min(5, p.Inventory.Equipment.Size); eq++)
            if (p.Inventory.Equipment[eq] is { } item)
                foreach (var id in item.Active) if (id >= 0) r.Add(id);
        return r;
    }

    /// <summary>
    /// The slots as the gear and the overrides make them now (SPEC §2): the overrides; per worn item in <see cref="Order"/> its explicit Items
    /// entries (they replace the rules for that item), else the rule generators' plan (<see cref="ExtraSpellRules"/>): first every item's base
    /// picks, then every item's bonuses (artifact, enchantment, quality); a preferred bar/position first, the rest into the first free slots.
    /// </summary>
    private Built Build(Player p)
    {
        var table = Table;
        var n = table.Slots;
        var spells = new short[n];
        var equipment = new int[n];
        var items = new string?[n];
        var sources = new byte[n];
        var labels = new string?[n];
        var explain = new List<string>();
        Array.Fill(spells, (short)-1);
        Array.Fill(equipment, -1);
        if (n == 0 || world.Spells is not { } data) return new(spells, equipment, items, sources, labels, explain);
        var placed = new HashSet<short>();
        if (_overrides.TryGetValue(p.Character.Id, out var ov))
            foreach (var (index, id) in ov)
                if (index < n && placed.Add(id)) (spells[index], sources[index], labels[index]) = (id, 2, "override");
        var rules = table.Generators;
        var taken = ActionBar(p);
        taken.UnionWith(placed);
        var seen = new HashSet<short>();
        // Per worn item (equipment order): its explicit entries, or its rule plan.
        var worn = new List<(int Eq, Item Item, List<ExtraPick>? Named, ExtraSpellRules.Plan? Plan)>();
        foreach (var eq in Order)
        {
            if (eq >= p.Inventory.Equipment.Size || p.Inventory.Equipment[eq] is not { } item) continue;
            var matching = table.Matching(item.Template.Name).ToList();
            if (matching.Count > 0)
            {
                var named = new List<ExtraPick>();
                foreach (var (rule, e) in matching)
                {
                    var id = data.IdOf(e.Spell);
                    if (id < 0 || !ExtraSpellTable.IsActiveSpell(data, id) || !seen.Add(id)) continue;
                    named.Add(new ExtraPick(id, $"item:{rule.Pattern}", e.Bar, e.Position));
                    taken.Add(id);
                }
                worn.Add((eq, item, named, null));
            }
            else if (rules.PlanFor(item.Template, item.Quality, item.Active, eq, world.Items, data) is { } plan) worn.Add((eq, item, null, plan));
        }
        var plans = worn.Where(w => w.Plan is not null).Select(w => w.Plan!).ToList();
        ExtraSpellRules.Resolve(plans, taken, rules.Usable(p), data);
        var candidates = new List<(ExtraPick Pick, int Equipment, string Item)>();
        foreach (var w in worn)
            if (w.Named is { } named) candidates.AddRange(named.Select(x => (x, w.Eq, w.Item.Template.Name)));
            else candidates.AddRange(w.Plan!.Picks.Take(w.Plan.BaseTaken).Select(x => (x, w.Eq, w.Item.Template.Name)));
        foreach (var w in worn)
            if (w.Plan is { } plan) candidates.AddRange(plan.Picks.Skip(plan.BaseTaken).Select(x => (x, w.Eq, w.Item.Template.Name)));
        void Put(int index, (ExtraPick Pick, int Equipment, string Item) c)
        {
            (spells[index], equipment[index], items[index], sources[index], labels[index]) = (c.Pick.Id, c.Equipment, c.Item, 1, c.Pick.Label);
            placed.Add(c.Pick.Id);
        }
        foreach (var c in candidates)
            if (c.Pick.Bar > 0 && c.Pick.Position > 0 && c.Pick.Bar <= table.Bars && c.Pick.Position <= table.Size
                && (c.Pick.Bar - 1) * table.Size + c.Pick.Position - 1 is var at && spells[at] < 0 && !placed.Contains(c.Pick.Id))
                Put(at, c);
        var dropped = new List<string>();
        foreach (var c in candidates)
        {
            if (placed.Contains(c.Pick.Id)) continue;
            var free = Array.IndexOf(spells, (short)-1);
            if (free < 0) { dropped.Add(data.Name(c.Pick.Id) ?? "?"); continue; }
            Put(free, c);
        }
        foreach (var w in worn)
        {
            var head = $"{ExtraSpellRules.NameOf(w.Eq)} {w.Item.Template.Name} (quality {w.Item.Quality}, .{w.Item.Template.Enchantment})";
            if (w.Named is { } named)
                explain.Add($"{head}: Items entry → {(named.Count == 0 ? "nothing new" : string.Join(", ", named.Select(x => $"{data.Name(x.Id)} [{x.Label}]")))}");
            else if (w.Plan is { } plan)
            {
                var want = plan.Base + plan.Bonuses.Count;
                explain.Add($"{head}: {plan.Base} base{(plan.Bonuses.Count > 0 ? " + " + string.Join(" + ", plan.Bonuses) : "")}"
                    + (plan.Artifact ? $" (artifact; its own: {(plan.Unique.Count == 0 ? "-" : string.Join(", ", plan.Unique.Select(u => data.Name(u))))})" : "")
                    + $" → {(plan.Picks.Count == 0 ? "nothing" : string.Join(", ", plan.Picks.Select(x => $"{data.Name(x.Id)} [{x.Label}]")))}"
                    + (plan.Picks.Count < want ? $" ({want - plan.Picks.Count} without a free candidate)" : ""));
            }
        }
        foreach (var eq in Order)
            if (eq < p.Inventory.Equipment.Size && p.Inventory.Equipment[eq] is { } item && !worn.Any(w => w.Eq == eq))
                explain.Add($"{ExtraSpellRules.NameOf(eq)} {item.Template.Name}: no rule for this slot");
        if (dropped.Count > 0) explain.Add($"the bars are full, left out: {string.Join(", ", dropped)}");
        return new(spells, equipment, items, sources, labels, explain);
    }

    /// <summary>
    /// What item <paramref name="t"/> of quality <paramref name="quality"/> grants as if <paramref name="p"/> wore it (op 9003 → event 9005, also for
    /// unworn items): its explicit Items entries, else its rule plan with the active choices of the same item when worn or in the bag (same name and
    /// quality), otherwise the first usable option of each active slot assumed chosen; only the item's own choices count as the action bar.
    /// </summary>
    public List<ExtraPick> Resolve(Player p, ItemTemplate t, int quality)
    {
        var table = Table;
        var r = new List<ExtraPick>();
        if (table.Slots == 0 || world.Spells is not { } data) return r;
        var named = table.Matching(t.Name).ToList();
        if (named.Count > 0)
        {
            foreach (var (rule, e) in named)
                if (data.IdOf(e.Spell) is var id and >= 0 && ExtraSpellTable.IsActiveSpell(data, id) && r.All(x => x.Id != id))
                    r.Add(new ExtraPick(id, $"item:{rule.Pattern}", e.Bar, e.Position));
            return r;
        }
        var slot = Array.IndexOf(Inventory.SlotNames, t.Slot);
        var rules = table.Generators;
        var usable = rules.Usable(p);
        Item? same = null;
        foreach (var c in new[] { p.Inventory.Equipment, p.Inventory.Bag })
            for (var i = 0; i < c.Size && same is null; i++)
                if (c[i] is { } it && it.Template == t && it.Quality == quality) same = it;
        short[] chosen = same is not null ? same.Active
            : data.SlotsOf(t.Name) is { } lists ? [.. lists.Active.Select(l => l.FirstOrDefault(id => usable(id), (short)-1))] : [];
        if (slot < 0 || rules.PlanFor(t, quality, chosen, slot, world.Items, data) is not { } plan) return r;
        ExtraSpellRules.Resolve([plan], [.. chosen.Where(id => id >= 0)], usable, data);
        r.AddRange(plan.Picks);
        return r;
    }

    /// <summary>.extrabar explain: per worn item the rule's plan and its picks (with their labels).</summary>
    public IReadOnlyList<string> Explain(Player p) => Build(p).Explain;

    /// <summary>
    /// Builds <paramref name="p"/>'s bars again; when they changed: OnExtraBarChanged per changed slot (not at the first build of a Player object,
    /// a Join) and the bars to a modded client. Returns whether they changed.
    /// </summary>
    public bool Refresh(Player p, ExtraBarChangeSource source, bool send = true)
    {
        if (p.IsBot) return false;
        var st = _states.GetValue(p, _ => new ExtraBarState());
        var (spells, equipment, items, sources, labels, _) = Build(p);
        if (st.Built && spells.AsSpan().SequenceEqual(st.Spells) && equipment.AsSpan().SequenceEqual(st.Equipment) && sources.AsSpan().SequenceEqual(st.Sources)
            && labels.SequenceEqual(st.Labels))
            return false;
        var (before, first) = (st.Spells, !st.Built);
        (st.Spells, st.Equipment, st.Items, st.Sources, st.Labels, st.Built) = (spells, equipment, items, sources, labels, true);
        var names = world.Spells;
        if (!first)
            for (var i = 0; i < spells.Length; i++)
            {
                var from = i < before.Length ? before[i] : (short)-1;
                if (from == spells[i]) continue;
                Log.Debug("extrabars", $"'{p.Character.Name}' extra slot {i + 1}: {names?.Name(from) ?? "-"} → {names?.Name(spells[i]) ?? "-"} ({source})");
                world.Plugins.ExtraBarChanged(p, i, names?.Name(from), names?.Name(spells[i]), items[i], sources[i] == 2 ? ExtraBarChangeSource.Plugin : source);
            }
        if (send && Modded(p)) SendBars(p);
        return true;
    }

    /// <summary>
    /// Puts active spell <paramref name="spell"/> into slot <paramref name="index"/> over the gear (null/"" removes the override): the plugins'
    /// SetExtraSlot, rules extrabar.set, GM .extrabar set. Null when done, else why not.
    /// </summary>
    public string? SetOverride(Player p, int index, string? spell, ExtraBarChangeSource source)
    {
        if (p.IsBot) return "a bot has no extra bars";
        var n = Table.Slots;
        if (n == 0) return "the extra bars are off (content/extraspells.json Bars 0)";
        if (index < 0 || index >= n) return $"extra slot {index + 1} does not exist (1-{n})";
        var key = p.Character.Id;
        if (string.IsNullOrWhiteSpace(spell))
        {
            if (_overrides.TryGetValue(key, out var had) && had.Remove(index) && had.Count == 0) _overrides.Remove(key);
        }
        else
        {
            if (world.Spells is not { } data) return "no spell data";
            var id = data.IdOf(spell.Trim().ToUpperInvariant());
            if (id < 0) return $"unknown spell '{spell}'";
            if (!ExtraSpellTable.IsActiveSpell(data, id)) return $"{data.Name(id)} is not an activespell (toggles and passives cannot go into the extra bars)";
            if (!_overrides.TryGetValue(key, out var ov)) _overrides[key] = ov = [];
            foreach (var other in ov.Where(o => o.Value == id && o.Key != index).Select(o => o.Key).ToList()) ov.Remove(other);
            ov[index] = id;
        }
        if (!_states.TryGetValue(p, out var st) || !st.Built) State(p); // the first build of this Player object: no hooks
        else Refresh(p, source);
        return null;
    }

    /// <summary>Takes all of <paramref name="p"/>'s overrides away (GM .extrabar clear).</summary>
    public void ClearOverrides(Player p)
    {
        if (_overrides.Remove(p.Character.Id)) Refresh(p, ExtraBarChangeSource.Plugin);
    }

    /// <summary>Whether slot <paramref name="index"/> holds an override.</summary>
    public bool IsOverride(Player p, int index) => _overrides.TryGetValue(p.Character.Id, out var ov) && ov.ContainsKey(index);

    // ---- the mod's channel

    /// <summary>ExtraBarsHello: the connection runs the mod; it gets the bars at once (and after every Join of this character until it logs out).</summary>
    public void Hello(Player p, int protocol, string? mod)
    {
        var v = Math.Clamp(protocol, 1, CustomCodes.ExtraBarsProtocol);
        p.Session.ExtraBarsProtocol = v;
        _clients[p.Character.Id] = v;
        Log.Info("extrabars", $"'{p.Character.Name}' runs the extra-bars mod ({mod ?? "?"}, protocol {protocol})");
        Refresh(p, ExtraBarChangeSource.Equipment, send: false);
        SendCatalog(p);
        SendBars(p);
    }

    /// <summary>After a Join's JoinFinished: the bars built for the new Player object; a character whose mod said hello before gets them.</summary>
    public void Joined(Player p)
    {
        if (p.IsBot) return;
        Refresh(p, ExtraBarChangeSource.Equipment, send: false);
        if (!_clients.TryGetValue(p.Character.Id, out var v)) return;
        p.Session.ExtraBarsProtocol = v;
        SendCatalog(p);
        SendBars(p);
    }

    /// <summary>The character logged out (not a cluster change): the next login must say hello again.</summary>
    public void LoggedOut(Guid character) => _clients.Remove(character);

    /// <summary>The cooldown of a spell as the client's sweep needs it: (start, end) ticks, (0, 0) when ready.</summary>
    private (long Start, long End) Cooldown(Player p, short id)
    {
        var now = world.GameNow;
        if (id < 0 || !p.Combat.Cooldowns.TryGetValue(id, out var end) || end <= now) return (0, 0);
        var full = world.Spells?.Template(id) is { } t ? SpellCasts.Seconds(t.RecastDelay * SpellCasts.CooldownFactorOf(p)) : 0;
        return (end - Math.Max(full, end - now), end);
    }

    /// <summary>
    /// Event 9004, the item → extra spells catalog (the mod lists an item's extra spells in its tooltip), to a modded client: one entry per
    /// (rule, spell) in precedence order, so the mod's matching (same * / ? rules, with and without "@n", each spell once) gives what
    /// <see cref="ExtraSpellTable.For"/> gives.
    /// </summary>
    public void SendCatalog(Player p)
    {
        if (!Modded(p) || p.IsBot) return;
        var table = Table;
        List<string> patterns = [], names = [];
        List<int> ids = [];
        foreach (var rule in table.Rules)
            foreach (var e in rule.Spells)
            {
                patterns.Add(rule.Pattern);
                names.Add(e.Spell);
                ids.Add(world.Spells?.IdOf(e.Spell) ?? -1);
            }
        p.Session.Send(new ExtraSpellCatalogEvent
        {
            Patterns = [.. patterns], Spells = [.. names], SpellIds = [.. ids],
            Revision = _catalogRevision, Bars = (byte)table.Bars, Size = (byte)table.Size, Protocol = CustomCodes.ExtraBarsProtocol,
        });
    }

    /// <summary>
    /// Op 9003 → event 9005: the extra spells of item <paramref name="name"/> (with or without "@n"; <paramref name="enchantment"/> when it has none)
    /// of quality <paramref name="quality"/> (0 = 1) as if worn (<see cref="Resolve(Player, ItemTemplate, int)"/>), with each spell's rule label.
    /// An unknown item is answered with empty lists (Known false).
    /// </summary>
    public void Query(Player p, string name, int quality, int enchantment, int requestId)
    {
        if (!Modded(p) || p.IsBot) return;
        name = name.Trim().ToUpperInvariant();
        if (!name.Contains('@') && enchantment > 0) name = $"{name}@{enchantment}";
        quality = Math.Clamp(quality <= 0 ? 1 : quality, 1, 5);
        var t = name.Length == 0 ? null : world.Items.Find(name);
        var picks = t is null ? [] : Resolve(p, t, quality);
        p.Session.Send(new ExtraItemSpellsEvent
        {
            RequestId = requestId, Item = t?.Name ?? name, Quality = quality, Enchantment = t?.Enchantment ?? Math.Max(0, enchantment), Known = t is not null,
            Spells = [.. picks.Select(x => world.Spells?.Name(x.Id) ?? "")], SpellIds = [.. picks.Select(x => (int)x.Id)], Labels = [.. picks.Select(x => x.Label)],
            Protocol = CustomCodes.ExtraBarsProtocol,
            Extra = new() { [0] = requestId }, // sent even when 0
        });
    }

    /// <summary>Event 9001, the whole bars, to a modded client.</summary>
    public void SendBars(Player p)
    {
        if (!Modded(p)) return;
        var st = State(p);
        var table = Table;
        var n = st.Spells.Length;
        var starts = new long[n];
        var ends = new long[n];
        for (var i = 0; i < n; i++) (starts[i], ends[i]) = Cooldown(p, st.Spells[i]);
        p.Session.Send(new ExtraBarsEvent
        {
            Bars = (byte)table.Bars, Size = (byte)table.Size,
            Spells = [.. st.Spells.Select(s => (int)s)],
            Names = [.. st.Spells.Select(s => s < 0 ? "" : world.Spells?.Name(s) ?? "")],
            Items = [.. st.Items.Select(i => i ?? "")],
            Sources = [.. st.Sources],
            Labels = [.. st.Labels.Select(l => l ?? "")],
            CooldownStarts = starts, CooldownEnds = ends,
            Revision = Interlocked.Increment(ref _revision),
            Protocol = CustomCodes.ExtraBarsProtocol,
            Extra = new() { [0] = (byte)table.Bars, [1] = (byte)table.Size }, // sent even when 0
        });
    }

    /// <summary>
    /// Cooldowns changed (a cast of <paramref name="spells"/> finished, a plugin's SetCooldown/ResetCooldowns): event 9002 for the slots that hold
    /// them (null = every non-empty slot), to a modded client only.
    /// </summary>
    public void Cooled(Player p, IReadOnlyCollection<short>? spells)
    {
        if (!Modded(p) || p.IsBot) return;
        var st = State(p);
        List<byte> indexes = [];
        List<long> starts = [], ends = [];
        List<int> ids = [];
        for (var i = 0; i < st.Spells.Length; i++)
        {
            var id = st.Spells[i];
            if (id < 0 || spells is not null && !spells.Contains(id)) continue;
            var (s, e) = Cooldown(p, id);
            indexes.Add((byte)i);
            starts.Add(s);
            ends.Add(e);
            ids.Add(id);
        }
        if (indexes.Count == 0) return;
        p.Session.Send(new ExtraBarCooldownsEvent { Indexes = [.. indexes], Starts = [.. starts], Ends = [.. ends], Spells = [.. ids] });
    }

    /// <summary>Event 9003 to a modded client: an extra cast was refused (<paramref name="reason"/> a code of the SPEC, <paramref name="text"/> a plugin's reason).</summary>
    public static void Refused(Player p, int index, int spell, string reason, byte counter, string? text = null)
    {
        if (!Modded(p)) return;
        p.Session.Send(new ExtraBarCastRefusedEvent
        {
            Index = (byte)(index is >= 0 and < 255 ? index : 255), Spell = spell, Reason = reason, Counter = counter, Text = text ?? "",
            Extra = new() { [0] = (byte)(index is >= 0 and < 255 ? index : 255) },
        });
    }
}
