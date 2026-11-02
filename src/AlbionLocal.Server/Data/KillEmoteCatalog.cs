using System.Globalization;
using System.Xml.Linq;

namespace AlbionLocal.Server.Data;

/// <summary>The client's expendable cosmetics. bgi's consumable counter starts at 1; bhb's visual counter starts at 0.
/// BuyVanityConsumableCharges quantities count bundles, not individual charges (VanityPreviewGui.DoBuyAllVanityItems).</summary>
public sealed class KillEmoteCatalog
{
    public sealed record Emote(short Id, short VisualId, string Name, string Item, long GoldPrice, int BundleCharges,
        float Duration, string Rotation, float MinRotation, float MaxRotation);
    public IReadOnlyList<Emote> All { get; }
    public float MaxDistance { get; }
    public int MaxInArea { get; }
    public float AreaRadius { get; }
    public static readonly KillEmoteCatalog Empty = Load(new XElement("killemotes"));
    private KillEmoteCatalog(List<Emote> all, float distance, int max, float radius)
        => (All, MaxDistance, MaxInArea, AreaRadius) = (all, distance, max, radius);
    public Emote? Find(short id) => All.FirstOrDefault(e => e.Id == id);
    public static KillEmoteCatalog Load(string folder) => Load(GameDataFile.Open(folder, "killemotes.bin").Root());
    public static KillEmoteCatalog Load(XElement root)
    {
        static float Number(XElement? e, string name, float fallback) =>
            float.TryParse((string?)e?.Attribute(name), NumberStyles.Float, CultureInfo.InvariantCulture, out var v) && float.IsFinite(v) ? v : fallback;
        var all = new List<Emote>();
        short visual = 0;
        foreach (var e in root.Element("emotes")?.Elements("killemote") ?? [])
        {
            var index = visual++;
            var name = (string?)e.Attribute("uniquename") ?? "";
            var item = (string?)e.Attribute("itemreference") ?? "";
            var price = (long)Number(e, "goldprice", 0) * 10000;
            var bundle = (int)Number(e, "bundledchargesamount", 0);
            var duration = Number(e, "duration", 0);
            // Keep numbering even when an unsupported entry cannot be offered.
            if (name.Length == 0 || item.Length == 0 || price <= 0 || bundle <= 0 || duration <= 0) continue;
            all.Add(new((short)(index + 1), index, name, item, price, bundle, duration,
                (string?)e.Attribute("rotationmode") ?? "default", Number(e, "randrotationoffsetmindegree", 0), Number(e, "randrotationoffsetmaxdegree", 0)));
        }
        var g = root.Element("globalsettings");
        return new(all, Math.Max(0, Number(g, "maxtriggerdistance", 25)), Math.Max(1, (int)Number(g, "maxelementsinarea", 20)),
            Math.Max(0, Number(g, "maxelementsinarearadius", 30)));
    }
}
