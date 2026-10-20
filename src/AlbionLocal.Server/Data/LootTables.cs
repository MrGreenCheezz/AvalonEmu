using System.Globalization;
using System.Xml.Linq;
using AlbionLocal.Photon;

namespace AlbionLocal.Server.Data;

/// <summary>
/// loot.xml (stage 5c, analysis/work/stage5/SPEC.md §5c.4): the &lt;Lootlist&gt;s by name, and the roll of a mob's
/// &lt;Loot&gt; through them, as the client's own copy of the roll does it (bd0.abo 0x180614970; attributes from b4h.tw
/// 0x1807f8c50, b4g.tw, b4j.tw; amounts b4g.ab 0x1807f84b0). Every child of a list, of an &lt;AND&gt; or of the mob's
/// &lt;Loot&gt; comes with its chance (1 when missing; above 1 an expected count: its whole part always, plus one more with
/// the rest as probability); an &lt;OR&gt; picks one child by weight (attribute weight, 1 when missing) and does not roll
/// that child's chance again; an &lt;Item type amount enchantmentlevel&gt; gives amount (a number or "min-max") of type, as
/// NAME@N for enchantment level N; a &lt;LootListReference name&gt; rolls that list. The total loot factor (the cluster's
/// moblootfactor × globalmoblootfactor) scales the chances of the mob's own &lt;Loot&gt; entries only (the client's "top
/// level factor only"; scaled further down it would double every chance-1 item now and then), and only where
/// cangetmodified is not false. A reference with evaluateChance="false" is not rolled: its chance scales the chances of
/// the list's entries below it, and the amounts of its items. An item with useblackmarket="true" drops only with
/// Loot:BlackMarketChance (the client's "drops only via blackmarket": the official server takes it from its Black Market).
/// Not modelled: scalewithgoldeconomy (the gold price), the per-listtype factors (1 in the client) and the continent filter
/// (no loot.xml entry has one). The mob's &lt;Harvestable&gt; children (silver, skinning) are not items and are left to the
/// caller. Tier sanity (loot-balance SPEC §3 [design]): with a content tier, an item more than one tier above it drops as such only
/// when an entry on its way rolled with a chance of at most <see cref="RareChance"/> (the data's explicitly rare entries, e.g.
/// T7_DIRECTLOOTDROP_WHITE 0.40 in a T5 chest list); otherwise it comes one tier above the content (T7_X in a T5 roll: T6_X).
/// Immutable; read off the world thread (<see cref="ClusterStore.Loot"/>).
/// </summary>
public sealed class LootTables
{
    /// <summary>Recursion depth past which a reference is not followed (a list that names itself).</summary>
    private const int MaxDepth = 16;

    /// <summary>The chance at or below which an entry counts as explicitly rare for the tier sanity (loot-balance SPEC §3).</summary>
    public const double RareChance = 0.5;

    private readonly Dictionary<string, XElement> _lists;

    private LootTables(Dictionary<string, XElement> lists) => _lists = lists;

    public int Count => _lists.Count;

    public static LootTables Load(XElement root)
    {
        var lists = new Dictionary<string, XElement>(StringComparer.Ordinal);
        foreach (var e in root.Elements("Lootlist"))
            if ((string?)e.Attribute("name") is { Length: > 0 } name) lists.TryAdd(name, e);
        if (lists.Count == 0) throw new InvalidDataException("loot.bin: no <Lootlist>");
        Log.Debug("data", $"loot.bin: {lists.Count} loot lists");
        return new LootTables(lists);
    }

    public bool Has(string name) => _lists.ContainsKey(name);

    /// <summary>
    /// A roll of a mob's &lt;Loot&gt; element with the total loot factor <paramref name="factor"/> and
    /// <paramref name="blackMarketChance"/> for useblackmarket items: the item uniquenames (NAME@N with an enchantment) and
    /// amounts, in roll order, the same name merged. <paramref name="unknown"/> gets each list name loot.xml does not have.
    /// <paramref name="contentTier"/> (0 = none): the tier of the mob or chest for the tier sanity (an item above it + 1 only through an
    /// explicitly rare entry). <paramref name="blackMarketSoftCap"/> (0 = none; Loot:BlackMarketSoftCap): past that many useblackmarket
    /// items in this roll each further one only with cap / (its number), so a list of hundreds of Black Market rolls (an Avalonian or
    /// static dungeon chest) gives tens of items, not hundreds (loot-balance SPEC §2.3 [design]: the official Black Market supplies only
    /// what it holds).
    /// </summary>
    public List<(string Item, int Amount)> RollMob(XElement loot, Random random, double factor = 1, double blackMarketChance = 1, Action<string>? unknown = null,
        int contentTier = 0, int blackMarketSoftCap = 0)
    {
        var roll = new Roll(this, random, blackMarketChance, unknown, contentTier > 0 ? contentTier + 1 : int.MaxValue, blackMarketSoftCap);
        roll.Children(loot, factor, 1, 1, 0, 1);
        return [.. roll.Drops.GroupBy(d => d.Item, StringComparer.Ordinal).Select(g => (g.Key, g.Sum(d => d.Amount)))];
    }

    /// <summary>The roll of the list named <paramref name="name"/> (once, chance 1, every useblackmarket item dropping).</summary>
    public List<(string Item, int Amount)> RollList(string name, Random random)
    {
        var roll = new Roll(this, random, 1, null, int.MaxValue, 0);
        if (_lists.TryGetValue(name, out var list)) roll.Children(list, 1, 1, 1, 0, 1);
        return roll.Drops;
    }

    /// <summary>
    /// One roll: its random, the black market chance and soft cap, the highest tier a common entry gives, the useblackmarket items so
    /// far and the drops so far.
    /// </summary>
    private sealed class Roll(LootTables tables, Random random, double blackMarketChance, Action<string>? unknown, int maxTier, int softCap)
    {
        public readonly List<(string Item, int Amount)> Drops = [];

        private int _blackMarket;

        /// <summary>
        /// The entries of a list, an &lt;AND&gt; or the mob's &lt;Loot&gt;, each with its own chance (bd0.abo's container
        /// branch). <paramref name="factor"/> is the total loot factor of this level, <paramref name="inherited"/> the chance
        /// of an evaluateChance="false" reference above, <paramref name="amountFactor"/> the factor on item amounts, <paramref name="rare"/>
        /// the smallest entry chance on the way here (1 = none below 1; the tier sanity).
        /// </summary>
        public void Children(XElement container, double factor, double inherited, double amountFactor, int depth, double rare)
        {
            if (depth > MaxDepth) return;
            foreach (var child in container.Elements())
            {
                var kind = child.Name.LocalName;
                if (!IsEntry(child)) continue; // <Harvestable>: the caller's
                var own = Number(child, "chance", 1);
                var chance = own;
                var amounts = amountFactor;
                var modified = XmlAttr.Bool(child, "cangetmodified", true);
                if (modified && kind == "Item")
                {
                    chance *= factor;
                    amounts *= inherited;
                }
                else if (modified) chance *= factor * inherited;
                // Below this level the factor is 1 (top level only) and nothing is inherited, but for a reference that is not rolled.
                var (childFactor, childInherited) = (1.0, 1.0);
                if (kind == "LootListReference" && !XmlAttr.Bool(child, "evaluateChance", true))
                    (chance, childFactor, childInherited) = (1, factor, modified ? inherited * own : own);
                var way = Math.Min(rare, Math.Min(1, chance));
                for (; chance > 0; chance -= 1)
                    if (chance >= 1 || random.NextDouble() < chance) Node(child, childFactor, childInherited, amounts, depth + 1, way);
            }
        }

        /// <summary>One entry whose chance came: an item drops, a reference rolls its list, an &lt;OR&gt; picks, an &lt;AND&gt; rolls its entries.</summary>
        private void Node(XElement e, double factor, double inherited, double amountFactor, int depth, double rare)
        {
            if (depth > MaxDepth) return;
            switch (e.Name.LocalName)
            {
                case "Item":
                    Item(e, amountFactor, rare);
                    break;
                case "LootListReference":
                    if ((string?)e.Attribute("name") is not { } name) break;
                    if (tables._lists.TryGetValue(name, out var list)) Children(list, factor, inherited, amountFactor, depth + 1, rare);
                    else unknown?.Invoke(name);
                    break;
                case "OR":
                    // The pick's chance is not rolled; a picked reference starts without an inherited chance (bd0.abo's OR branch).
                    if (Pick([.. e.Elements().Where(IsEntry)]) is { } one)
                        Node(one, 1, one.Name.LocalName == "LootListReference" ? 1 : inherited, amountFactor, depth + 1, rare);
                    break;
                case "AND":
                    Children(e, factor, inherited, amountFactor, depth + 1, rare);
                    break;
            }
        }

        /// <summary>
        /// An item that came: its amount (× <paramref name="amountFactor"/>, the rest a chance of one more); a useblackmarket item only with
        /// the black market chance and under the soft cap; above the highest tier of a common entry one tier above the content unless <paramref name="rare"/>.
        /// </summary>
        private void Item(XElement e, double amountFactor, double rare)
        {
            if ((string?)e.Attribute("type") is not { Length: > 0 } type) return;
            if (rare > RareChance && TierOf(type) > maxTier) type = $"T{maxTier}{type[2..]}";
            var amount = Amount(e, random);
            if (amount <= 0) return;
            if (amountFactor != 1)
            {
                var scaled = amount * amountFactor;
                var whole = (int)Math.Floor(scaled);
                amount = whole + (scaled > whole && random.NextDouble() < scaled - whole ? 1 : 0);
                if (amount <= 0) return;
            }
            if (XmlAttr.Bool(e, "useblackmarket", false))
            {
                if (blackMarketChance < 1 && !(random.NextDouble() < blackMarketChance)) return;
                if (softCap > 0 && _blackMarket >= softCap && !(random.NextDouble() < (double)softCap / (_blackMarket + 1))) return;
                _blackMarket++;
            }
            Drops.Add((XmlAttr.Int(e, "enchantmentlevel", 0) is > 0 and var level ? $"{type}@{level}" : type, amount));
        }

        /// <summary>The child an &lt;OR&gt; picks by weight (1 when missing); null when it has none or all weights are 0.</summary>
        private XElement? Pick(List<XElement> children)
        {
            var total = children.Sum(c => Math.Max(0, Number(c, "weight", 1)));
            if (total <= 0) return null;
            var roll = random.NextDouble() * total;
            foreach (var c in children)
            {
                roll -= Math.Max(0, Number(c, "weight", 1));
                if (roll < 0) return c;
            }
            return children[^1];
        }
    }

    /// <summary>The tier of an item name (T5_X: 5; 0 without a T&lt;n&gt;_ prefix).</summary>
    public static int TierOf(string item) =>
        item.Length > 3 && item[0] == 'T' && char.IsAsciiDigit(item[1]) && item[2] == '_' ? item[1] - '0' : 0;

    /// <summary>Whether <paramref name="e"/> is a loot entry (not a &lt;Harvestable&gt; or anything else).</summary>
    private static bool IsEntry(XElement e) => e.Name.LocalName is "Item" or "LootListReference" or "OR" or "AND";

    /// <summary>amount: "n" or "min-max" (uniform, both included); 1 when missing.</summary>
    private static int Amount(XElement e, Random random)
    {
        var text = ((string?)e.Attribute("amount"))?.Trim();
        if (string.IsNullOrEmpty(text)) return 1;
        var dash = text.IndexOf('-', 1);
        if (dash > 0 && int.TryParse(text[..dash], NumberStyles.Integer, CultureInfo.InvariantCulture, out var min)
            && int.TryParse(text[(dash + 1)..], NumberStyles.Integer, CultureInfo.InvariantCulture, out var max))
            return max <= min ? min : random.Next(min, max + 1);
        return int.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out var n) ? n : 1;
    }

    private static double Number(XElement e, string name, double fallback) =>
        (string?)e.Attribute(name) is { Length: > 0 } v && double.TryParse(v.Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out var d) ? d : fallback;
}
