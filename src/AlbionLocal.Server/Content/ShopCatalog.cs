using System.Text.Json;
using AlbionLocal.Server.Data;
using AlbionLocal.Server.Game;

namespace AlbionLocal.Server.Content;

/// <summary>Local-only offers. Cash prices are demonstration cents; game currencies use a7m. No payment provider is contacted.</summary>
public sealed class ShopCatalog
{
    private Dictionary<string, string> _names = new(StringComparer.OrdinalIgnoreCase);
    public bool Enabled { get; set; } = true;
    public long SilverPerGold { get; set; } = 5000;
    public long DefaultSkinGold { get; set; } = 2500;
    public long DefaultMountGold { get; set; } = 3500;
    public Dictionary<int, long> PremiumGold { get; set; } = new() { [7] = 1000, [30] = 3750, [90] = 10500, [180] = 19500, [360] = 36000 };
    public List<Offer> Offers { get; set; } = [];
    public sealed record Offer(string Id, string Title, string Category, string Currency, long Price,
        long Gold = 0, int PremiumDays = 0, string[]? UnlockNames = null, string Description = "", int LearningPoints = 0, int GoldBonus = 0,
        ShopItem[]? Items = null);

    public static ShopCatalog Load(string path)
    {
        var options = new JsonSerializerOptions { PropertyNameCaseInsensitive = true, ReadCommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true };
        var c = File.Exists(path) ? JsonSerializer.Deserialize<ShopCatalog>(File.ReadAllText(path), options) ?? throw new InvalidDataException("empty shop catalogue") : Defaults();
        var namesPath = Path.Combine(Path.GetDirectoryName(path) ?? ".", "shop-names.es.json");
        if (File.Exists(namesPath)) c._names = new(JsonSerializer.Deserialize<Dictionary<string, string>>(File.ReadAllText(namesPath))
            ?? throw new InvalidDataException("empty shop names catalogue"), StringComparer.OrdinalIgnoreCase);
        if (c.SilverPerGold is < 1 or > 1_000_000 || c.DefaultSkinGold is < 1 or > 1_000_000 || c.DefaultMountGold is < 1 or > 1_000_000
            || c.PremiumGold.Count == 0 || c.PremiumGold.Any(x => x.Key is < 1 or > 3650 || x.Value is < 1 or > 100_000_000))
            throw new InvalidDataException("shop: invalid currency or premium settings");
        if (c.Offers.Select(o => o.Id).Distinct(StringComparer.Ordinal).Count() != c.Offers.Count
            || c.Offers.Any(o => string.IsNullOrWhiteSpace(o.Id) || o.Id.Length > 96 || o.Currency is not ("cash" or "gold" or "silver")
                || o.Price is < 1 or > 1_000_000_000_000 || o.Gold is < 0 or > 1_000_000_000_000 || o.PremiumDays is < 0 or > 3650
                || o.LearningPoints is < 0 or > 100_000_000
                || o.GoldBonus < 0 || o.GoldBonus > o.Gold / 10000
                || o.Items is { Length: > 32 }
                || (o.Items ?? []).Any(i => string.IsNullOrWhiteSpace(i.Name) || i.Name.Length > 128 || i.Count is < 1 or > 1_000_000)
                || (o.Items ?? []).Select(i => i.Name).Distinct(StringComparer.OrdinalIgnoreCase).Count() != (o.Items?.Length ?? 0)
                || o.Gold == 0 && o.PremiumDays == 0 && o.LearningPoints == 0 && (o.UnlockNames is null || o.UnlockNames.Length == 0) && (o.Items is null || o.Items.Length == 0)))
            throw new InvalidDataException("shop: invalid or duplicate offer");
        return c;
    }

    public static ShopCatalog Defaults() => new()
    {
        Offers =
        [
            new("gold.1000", "1.000 de oro", "gold", "cash", 995, 10_000_000, Description: "Oro para la cuenta local."),
            new("gold.5000", "5.000 de oro", "gold", "cash", 3995, 50_000_000, Description: "Oro para skins y premium."),
            new("gold.15000", "15.000 de oro", "gold", "cash", 9995, 150_000_000),
            new("premium.7.cash", "Premium · 7 días", "premium", "cash", 495, PremiumDays: 7),
            new("premium.30.cash", "Premium · 30 días", "premium", "cash", 1495, PremiumDays: 30),
            new("premium.90.cash", "Premium · 90 días", "premium", "cash", 3995, PremiumDays: 90),
            new("bundle.duelist", "Colección del Duelista", "bundles", "cash", 2495, 10_000_000, 30,
                ["SKIN_COUGAR_DUELIST", "UNIQUE_HEAD_VANITY_DUELIST", "UNIQUE_ARMOR_VANITY_DUELIST", "UNIQUE_SHOES_VANITY_DUELIST", "UNIQUE_CAPE_VANITY_DUELIST"],
                "Apariencias del Duelista, 1.000 de oro y 30 días de premium. La apariencia de montura requiere una montura compatible."),
        ],
    };

    public long VanityPrice(VanityCatalog.Unlock u) => u.GoldPrice > 0 ? u.GoldPrice
        : checked((u.Type == VanityCatalog.UnlockType.Mount ? DefaultMountGold :
            u.Type == VanityCatalog.UnlockType.Skin ? DefaultSkinGold : 500) * 10000);

    public IReadOnlyList<ShopProduct> Products(VanityCatalog vanity, ItemStore? items = null)
    {
        var products = new List<ShopProduct>();
        foreach (var o in Offers)
        {
            var unlocks = (o.UnlockNames ?? []).Select(n => vanity.Find(n)).ToArray();
            // A pack with data from another build must never silently deliver only some of its components.
            if (unlocks.Any(u => u is null) || (o.Items ?? []).Any(i => items?.Find(i.Name) is not { IsSimple: true })) continue;
            products.Add(new(o.Id, o.Title, o.Category, o.Currency, o.Price, o.Gold, o.PremiumDays, unlocks.Select(u => u!.Id).Distinct().ToArray(),
                o.Description, o.LearningPoints, o.GoldBonus, o.Items?.ToArray()));
        }
        foreach (var (days, price) in PremiumGold.OrderBy(x => x.Key))
        {
            products.Add(new($"premium.{days}.gold", $"Premium · {days} días", "premium", "gold", checked(price * 10000), PremiumDays: days));
            products.Add(new($"premium.{days}.silver", $"Premium · {days} días", "premium", "silver", checked(price * 10000 * SilverPerGold), PremiumDays: days));
        }
        foreach (var u in vanity.All.Where(u => !u.Free && !string.IsNullOrWhiteSpace(u.Name)))
            products.Add(new($"vanity.{u.Id}", Name(u), u.Type == VanityCatalog.UnlockType.Mount ? "mounts" : "vanity", "gold", VanityPrice(u),
                Unlocks: [u.Id], Description: "Desbloqueo permanente para tu cuenta. Puedes aplicarlo desde las opciones de apariencia del juego."));
        if (products.Select(p => p.Id).Distinct(StringComparer.Ordinal).Count() != products.Count)
            throw new InvalidDataException("shop: a configured offer duplicates a generated product id");
        return products;
    }

    private string Name(VanityCatalog.Unlock u) => _names.GetValueOrDefault(u.Name) ??
        System.Globalization.CultureInfo.GetCultureInfo("es").TextInfo.ToTitleCase(u.Name.Replace("UNIQUE_", "").Replace("VANITY_", "").Replace("SKIN_", "").Replace('_', ' ').ToLowerInvariant());
}
