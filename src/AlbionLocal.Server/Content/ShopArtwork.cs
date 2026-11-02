using System.Text.Json;
using AlbionLocal.Server.Data;
using AlbionLocal.Server.Game;

namespace AlbionLocal.Server.Content;

/// <summary>One artwork mapping shared by the native store and its local checkout.</summary>
public sealed class ShopArtwork
{
    public sealed record Entry(string Native = "", string File = "", string NativeBottom = "");
    public sealed class Manifest
    {
        public Dictionary<string, Entry> Entries { get; set; } = new(StringComparer.OrdinalIgnoreCase);
        public NameMetadata LocalizedNames { get; set; } = new();
    }

    public sealed class NameMetadata
    {
        public Dictionary<string, string> Tags { get; set; } = new(StringComparer.OrdinalIgnoreCase);
    }

    private readonly Dictionary<string, Entry> _entries;
    private readonly Dictionary<string, string> _files;
    private readonly Dictionary<string, string> _nameTags;

    private ShopArtwork(Dictionary<string, Entry> entries, Dictionary<string, string> files, Dictionary<string, string> nameTags)
    {
        _entries = entries;
        _files = files;
        _nameTags = new(nameTags, StringComparer.OrdinalIgnoreCase);
    }

    public static ShopArtwork Load(string content)
    {
        var path = Path.Combine(content, "shop-art.json");
        var manifest = File.Exists(path)
            ? JsonSerializer.Deserialize<Manifest>(File.ReadAllText(path), new JsonSerializerOptions { PropertyNameCaseInsensitive = true })
                ?? throw new InvalidDataException("empty shop artwork manifest")
            : new Manifest();
        var entries = new Dictionary<string, Entry>(manifest.Entries, StringComparer.OrdinalIgnoreCase);
        var files = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var entry in entries.Values)
        {
            if (entry.File.Length == 0) continue;
            if (!entry.File.EndsWith(".png", StringComparison.OrdinalIgnoreCase) || entry.File.Contains("..", StringComparison.Ordinal)
                || entry.File.Any(c => !char.IsAsciiLetterOrDigit(c) && c is not ('_' or '-' or '.')))
                throw new InvalidDataException("shop artwork must name a PNG inside content/shop-art");
            var file = Path.Combine(content, "shop-art", entry.File);
            if (System.IO.File.Exists(file)) files.TryAdd(entry.File, file);
        }
        return new(entries, files, manifest.LocalizedNames.Tags);
    }

    /// <summary>Exact original localization key; the native client selects its active language.</summary>
    public string? NameTag(string unlock) => _nameTags.GetValueOrDefault(unlock);

    public Entry? Resolve(ShopProduct product, VanityCatalog vanity)
    {
        if (_entries.TryGetValue("product:" + product.Id, out var productArt)) return productArt;
        foreach (var id in product.Unlocks ?? [])
            if (vanity.Find(id) is { } unlock && _entries.TryGetValue(unlock.Name, out var art)) return art;
        return _entries.GetValueOrDefault("category:" + product.Category);
    }

    public string ImageUrl(Entry? entry) => entry is not null && _files.ContainsKey(entry.File)
        ? "/shop/art/" + Uri.EscapeDataString(entry.File) : "";

    // HTTP only serves enumerated artwork, never arbitrary client or content files.
    public string? FilePath(string filename) => _files.GetValueOrDefault(filename);
}
