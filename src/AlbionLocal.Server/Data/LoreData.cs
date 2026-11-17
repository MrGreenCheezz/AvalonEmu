using System.Globalization;
using System.Xml.Linq;

namespace AlbionLocal.Server.Data;

/// <summary>
/// lore.xml (analysis/work/stageR/lore/SPEC.md §1): the lore snippets, read the way the client's bsj.l(XmlReader) 0x18076CA60 does. The
/// client numbers them in file order from 0 (the static counter bsj.k, reset before the load): that number is NewLoreSnippetObject key 4,
/// LoreSnippedClaimed key 0 and the ints of RequestLoreSnippetStates, looked up with bsi.abo. Each has a uniquename, the title, description
/// and location hint keys (defaults @LORE_&lt;name&gt;_TITLE/_DESC/_HINT), hideuntilref (a time reference, none in the data), claimduration
/// (default c4.l(5) = 5 s, the channel of the claim) and prototype; bsj.w: a TEST_ or DEBUG_ name or prototype="true" is a debug snippet.
/// Immutable.
/// </summary>
public sealed class LoreData
{
    /// <summary>The claim channel when lore.xml gives none (bsj's static c4.l(5): 5 × 10^7 ticks).</summary>
    public static readonly TimeSpan DefaultClaimDuration = TimeSpan.FromSeconds(5);

    /// <summary>One loresnippet; <see cref="Index"/> its client number.</summary>
    public sealed record Snippet(int Index, string Name, string Title, string Description, string Hint, string HideUntil, TimeSpan ClaimDuration, bool Prototype)
    {
        /// <summary>bsj.w: TEST_ / DEBUG_ names and prototypes.</summary>
        public bool Debug => Prototype || Name.StartsWith("TEST_", StringComparison.Ordinal) || Name.StartsWith("DEBUG_", StringComparison.Ordinal);
    }

    private readonly Dictionary<string, Snippet> _byName;

    private LoreData(List<Snippet> all)
    {
        All = all;
        _byName = all.GroupBy(s => s.Name, StringComparer.OrdinalIgnoreCase).ToDictionary(g => g.Key, g => g.First(), StringComparer.OrdinalIgnoreCase);
    }

    /// <summary>The snippets in file order (index = position).</summary>
    public IReadOnlyList<Snippet> All { get; }

    /// <summary>The snippet <paramref name="name"/> (any case); null = none.</summary>
    public Snippet? Find(string? name) => name is not null && _byName.TryGetValue(name, out var s) ? s : null;

    /// <summary>The snippet number <paramref name="index"/>; null out of range.</summary>
    public Snippet? At(int index) => index >= 0 && index < All.Count ? All[index] : null;

    public static LoreData Load(XElement root)
    {
        var list = new List<Snippet>();
        foreach (var e in root.Element("loresnippets")?.Elements("loresnippet") ?? [])
        {
            var name = (string?)e.Attribute("uniquename") ?? "";
            // claimduration: seconds [infer: s.d reads a c4 attribute; the data has none, the default is 5 s]
            var duration = double.TryParse((string?)e.Attribute("claimduration"), NumberStyles.Float, CultureInfo.InvariantCulture, out var s) && s >= 0
                ? TimeSpan.FromSeconds(s) : DefaultClaimDuration;
            list.Add(new Snippet(list.Count, name,
                (string?)e.Attribute("loretitleloca") ?? $"@LORE_{name}_TITLE",
                (string?)e.Attribute("loredescriptionloca") ?? $"@LORE_{name}_DESC",
                (string?)e.Attribute("lorelocationhintloca") ?? $"@LORE_{name}_HINT",
                (string?)e.Attribute("hideuntilref") ?? "",
                duration,
                string.Equals((string?)e.Attribute("prototype"), "true", StringComparison.OrdinalIgnoreCase)));
        }
        return new LoreData(list);
    }
}
