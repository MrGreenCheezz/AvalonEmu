using System.Text.Json;
using System.Xml.Linq;
using AlbionLocal.Photon;
using AlbionLocal.Server.Mods;

namespace AlbionLocal.Server.Web;

/// <summary>
/// The icons <c>modkit icons</c> extracted (icons SPEC): &lt;dir&gt;/index.json maps sprite names to PNG files below &lt;dir&gt;. Re-read when
/// index.json changes (its time is checked on each use); a missing or broken cache is simply "no icons".
/// </summary>
public sealed class IconCache(string dir)
{
    private readonly object _lock = new();
    private Dictionary<string, string> _map = new(StringComparer.OrdinalIgnoreCase);
    private DateTime _stamp = DateTime.MinValue;

    public string Folder { get; } = Path.GetFullPath(dir);

    private Dictionary<string, string> Map()
    {
        lock (_lock)
        {
            var index = Path.Combine(Folder, "index.json");
            var stamp = File.Exists(index) ? File.GetLastWriteTimeUtc(index) : DateTime.MinValue;
            if (stamp == _stamp) return _map;
            _stamp = stamp;
            var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            try
            {
                if (stamp != DateTime.MinValue)
                {
                    using var doc = JsonDocument.Parse(File.ReadAllBytes(index));
                    foreach (var p in doc.RootElement.GetProperty("sprites").EnumerateObject())
                        if (p.Value.GetString() is { } rel) map.TryAdd(p.Name, rel);
                }
            }
            catch (Exception e) when (e is IOException or JsonException or KeyNotFoundException or InvalidOperationException or UnauthorizedAccessException)
            {
                Log.Warn("web", $"icon cache {index} unreadable: {e.Message}");
            }
            return _map = map;
        }
    }

    /// <summary>True when an extracted cache with icons is there.</summary>
    public bool Available => Map().Count > 0;

    public bool Has(string? sprite) => sprite is { Length: > 0 } && Map().ContainsKey(sprite);

    /// <summary>Every cached sprite with its atlas (the folder of its PNG: ItemAtlas, SpellAtlas, …), sorted by name: the studio's icon picker.</summary>
    public List<(string Sprite, string Atlas)> Entries()
    {
        var map = Map();
        lock (_lock)
        {
            if (_entries is not null && ReferenceEquals(_entriesOf, map)) return _entries;
            _entriesOf = map;
            return _entries = [.. map.Select(p => (p.Key, p.Value.Split('/', '\\')[0])).OrderBy(p => p.Key, StringComparer.OrdinalIgnoreCase)];
        }
    }

    private List<(string, string)>? _entries;
    private object? _entriesOf;

    /// <summary>The PNG of a sprite: a file inside the cache folder (index entries that point elsewhere are ignored), or null.</summary>
    public string? FileOf(string sprite)
    {
        if (!Map().TryGetValue(sprite, out var rel) || rel.Contains("..") || Path.IsPathRooted(rel) || !rel.EndsWith(".png", StringComparison.OrdinalIgnoreCase)) return null;
        var full = Path.GetFullPath(Path.Combine(Folder, rel));
        return full.StartsWith(Folder + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase) && File.Exists(full) ? full : null;
    }

    /// <summary>The client's icon of an item: its uisprite, else the uniquename without "Tn_" (<see cref="ModBuilder.SpriteOf"/>), else the full name.</summary>
    public string? ForItem(XElement item)
    {
        var sprite = ModBuilder.SpriteOf(item);
        if (Has(sprite)) return sprite;
        var name = item.Attribute("uniquename")?.Value;
        return Has(name) ? name : null;
    }

    /// <summary>The icon of a spell: its uisprite, else its uniquename.</summary>
    public string? ForSpell(XElement spell)
    {
        var sprite = spell.Attribute("uisprite")?.Value;
        if (Has(sprite)) return sprite;
        var name = spell.Attribute("uniquename")?.Value;
        return Has(name) ? name : null;
    }

    /// <summary>A transparent 1×1 PNG: the answer for an unknown sprite (no broken image).</summary>
    public static readonly byte[] Blank = Png.Encode(1, 1, new byte[4]);
}
