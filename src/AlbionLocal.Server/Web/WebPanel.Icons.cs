using System.Xml.Linq;

namespace AlbionLocal.Server.Web;

/// <summary>Icons in the Mods section (icons SPEC §3): /admin/icons/&lt;sprite&gt;.png out of the modkit's icon cache.</summary>
public sealed partial class WebPanel
{
    private readonly IconCache _icons;

    /// <summary>GET /admin/icons/&lt;sprite&gt;.png (admin session): the cached PNG, else 404 with a transparent pixel (no broken image).</summary>
    private WebResponse IconFile(string name)
    {
        var sprite = name.EndsWith(".png", StringComparison.OrdinalIgnoreCase) ? name[..^4] : name;
        if (sprite.Length is > 0 and <= 200 && _icons.FileOf(sprite) is { } file)
        {
            try { return new WebResponse(200, "", "image/png") { Bytes = File.ReadAllBytes(file) }; }
            catch (IOException) { }
        }
        return new WebResponse(404, "", "image/png") { Bytes = IconCache.Blank };
    }

    /// <summary>An &lt;img&gt; of a sprite, or "" when the cache has no such icon.</summary>
    private string Icon(string? sprite, int size = 32) => _icons.Has(sprite)
        ? $"<img class=icon src=\"/admin/icons/{Enc(sprite!)}.png\" width={size} height={size} alt=\"\" loading=lazy>" : "";

    private string ItemIcon(XElement item, int size = 32) => Icon(_icons.ForItem(item), size);

    private string SpellIcon(XElement spell, int size = 32) => Icon(_icons.ForSpell(spell), size);

    /// <summary>The hint on the Mods pages while no icon cache is there.</summary>
    private string IconHint() => _icons.Available ? ""
        : "<p class=muted>No icons yet: run <code>dotnet run --project tools/AlbionLocal.ModKit -- icons</code> once (reads the client's atlases, read only, into data/icon-cache).</p>";
}
