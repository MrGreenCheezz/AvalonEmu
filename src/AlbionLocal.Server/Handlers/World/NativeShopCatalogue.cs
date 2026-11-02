using System.Globalization;
using System.Text.Json;
using AlbionLocal.Server.Data;
using AlbionLocal.Server.Content;
using AlbionLocal.Server.Game;
using AlbionLocal.Server.Protocol;

namespace AlbionLocal.Server.Network;

/// <summary>
/// Server-owned catalogue for the native ProductStore. Wire fields are from the 345319 generated schema;
/// their consumer was traced in the earlier 344269 client: c20.k/l (A499D0/A4A2F0),
/// c20.k(ay8) (A48230), ProductStoreSmallTile.InitContent (F46850), and c31.i (A8DC50).
/// ProductStore sends the tile ID to GetShopPurchaseUrl; only the local checkout grants anything.
/// </summary>
internal static class NativeShopCatalogue
{
    // The native sidebar has no scroll, and its pager creates every page button. Keep at most eight
    // sidebar entries and eight pages per individual-skin leaf; use the original LinkTile for folders.
    // Twenty-one full-height illustrations plus the Back link fit in eight six-slot pages.
    private const int LeafSize = 21;
    private sealed class Category(int id, string key, string title, Category? parent = null)
    {
        public int Id { get; } = id;
        public string Key { get; } = key;
        public string Title { get; } = title;
        public Category? Parent { get; } = parent;
        public List<ShopProduct> Products { get; } = [];
        public List<Category> Children { get; } = [];
    }

    // Stable IDs survive reordering or adding products. A collision is rejected instead of opening the wrong offer.
    internal static int TileId(string product)
    {
        uint hash = 2166136261;
        foreach (var c in product) hash = unchecked((hash ^ c) * 16777619);
        return checked(1_000_000 + (int)(hash & 0x3fffffff));
    }

    private static ShopProduct[] Products(World world, Guid account)
    {
        // Illustration availability must never decide whether a skin can be bought. Individual
        // unlocks use the native entitlement atlas/preview; promotional offers use their exact art.
        // Gold/silver Premium offers remain available through the native Premium selector.
        var products = world.Shop.Products(account)
            .Where(p => p.Currency == "cash" || p.Unlocks is { Length: > 0 })
            .OrderBy(p => p.Category, StringComparer.Ordinal).ThenBy(p => p.Price).ThenBy(p => p.Id, StringComparer.Ordinal).ToArray();
        var ids = products.Select(p => TileId(p.Id)).Concat(new[] { ExternalLinkId(2), ExternalLinkId(3) }).ToArray();
        if (ids.Distinct().Count() != ids.Length)
            throw new InvalidDataException("native shop tile ID collision");
        return products;
    }

    internal static ShopProduct? Product(World world, Guid account, int tile) =>
        Products(world, account).FirstOrDefault(p => TileId(p.Id) == tile);

    private static Category[] Navigation(World world, Guid account)
    {
        var products = Products(world, account);
        // These are protocol identities, not arbitrary sidebar positions. The original client
        // selects each category's icon and localized Info flyout from this exact numeric value.
        // SHOP_CATEGORY_PREMIUM/GOLD say "Get Premium/Get Gold". The storefront uses the
        // short resource names; every tag here exists in the installed 345319 localization.bin.
        var roots = new[] { new Category(4, "featured", "@SHOP_CATEGORY_FEATURED"), new Category(3, "premium", "@GENERIC_PREMIUM"),
            new Category(2, "gold", "@GENERIC_GOLD"), new Category(13, "learningpoints", "@GENERIC_LEARNINGPOINTS"),
            new Category(5, "vanity", "@OPTIONS_VANITY_HEADLINE"), new Category(6, "starter", "@SHOP_CATEGORY_STARTER_BUNDLES"),
            new Category(7, "value", "@SHOP_CATEGORY_VANITY_VALUE_BUNDLES") };
        var nodes = roots.ToList();
        Category Add(Category parent, string key, string title)
        {
            var category = new Category(TileId("category:" + key), key, title, parent);
            if (nodes.Any(c => c.Id == category.Id)) throw new InvalidDataException("native shop category ID collision");
            parent.Children.Add(category); nodes.Add(category); return category;
        }
        void AddItems(Category category, IEnumerable<ShopProduct> source)
        {
            var ordered = source.OrderBy(p => p.Title, StringComparer.Create(CultureInfo.GetCultureInfo("es"), true))
                .ThenBy(p => p.Id, StringComparer.Ordinal).ToArray();
            if (ordered.Length <= LeafSize) { category.Products.AddRange(ordered); return; }
            for (var offset = 0; offset < ordered.Length; offset += LeafSize)
            {
                var chunk = ordered.Skip(offset).Take(LeafSize).ToArray();
                // Category names are localization keys, not text to interpolate into a literal.
                var page = Add(category, category.Key + "/" + offset, $"{offset + 1}–{offset + chunk.Length}");
                page.Products.AddRange(chunk);
            }
        }
        var cash = products.Where(p => p.Currency == "cash").ToArray();
        roots[1].Products.AddRange(cash.Where(p => p.Category == "premium").OrderByDescending(p => p.PremiumDays));
        roots[2].Products.AddRange(cash.Where(p => p.Category == "gold").OrderByDescending(p => p.Gold));
        roots[3].Products.AddRange(cash.Where(p => p.Category == "learningpoints").OrderByDescending(p => p.LearningPoints));
        roots[5].Products.AddRange(cash.Where(p => p.Category == "starter").OrderBy(p => Rank(p.Id,
            "starter.ranger.360", "starter.druid.360", "starter.paladin.360", "starter.demonicwarrior.360", "starter.barbarian.180")));
        roots[6].Products.AddRange(cash.Where(p => p.Category == "value").OrderBy(p => Rank(p.Id,
            "value.titan.19000", "value.priest.19000", "value.reaver.19000", "value.rogue.3500", "value.monk.9000",
            "value.frostmage.9000", "value.firemage.9000", "value.soulkeeper.3500", "value.cursed-direboar.1700",
            "value.armored-pony.1700", "value.keeper-horse.320")));
        var promotional = cash.Where(p => p.Category is "bundles" or "mounts" or "vanity").ToArray();
        roots[4].Products.AddRange(promotional.Where(p => !string.IsNullOrEmpty(Illustration(world, p)?.NativeBottom))
            .OrderBy(p => Rank(p.Id, "bundle.soulkeeper", "mount.undead-direwolf", "mount.mistbison")));
        // Featured is a curated sequence observed in the original storefront. Response packs
        // its large cards three per page; do not sort this list by price or product category.
        foreach (var id in new[] { "bundle.corrupted-preacher", "mount.cursed-direboar", "bundle.essentials",
                     "value.titan.19000", "starter.paladin.360", "starter.barbarian.180", "value.reaver.19000" })
            if (cash.FirstOrDefault(p => p.Id == id) is { } offer) roots[0].Products.Add(offer);
        // Keep the full local catalogue without replacing the original promotional storefront
        // with hundreds of inventory records and an overflowing page selector.
        var individualRoot = Add(roots[4], "wardrobe", "@VANITY_UI_HEADLINE");
        var mounts = Add(individualRoot, "wardrobe/mounts", "@VANITY_UI_MOUNT_SKINS_HEADLINE");
        var wardrobe = Add(individualRoot, "wardrobe/character", "@VANITY_UI_WARDROBE_HEADLINE");
        var unillustrated = promotional.Where(p => string.IsNullOrEmpty(Illustration(world, p)?.NativeBottom)).ToArray();
        if (unillustrated.Length > 0) AddItems(Add(individualRoot, "wardrobe/collections", "@SHOP_CATEGORY_MOREOFFERS"), unillustrated);
        var individuals = products.Where(p => p.Id.StartsWith("vanity.", StringComparison.Ordinal) && p.Unlocks is { Length: 1 })
            .Select(p => (Product: p, Unlock: world.Vanity.Catalog.Find(p.Unlocks![0]))).Where(x => x.Unlock is not null).ToArray();
        foreach (var group in individuals.Where(x => x.Unlock!.Type == VanityCatalog.UnlockType.Mount).GroupBy(x => x.Unlock!.Slot).OrderBy(g => g.Key))
        {
            var name = group.Key >= 0 && group.Key < world.Vanity.Catalog.MountCategories.Count ? world.Vanity.Catalog.MountCategories[group.Key] : "Otras";
            var title = name is "HORSES" or "ARMORED_HORSES" or "OXEN" or "DIREWOLVES" or "STAGS" or "COUGARS"
                or "DIREBOARS" or "DIREBEARS" or "LIZARDS" or "DONKEYS" or "MAMMOTHS"
                ? "@VANITY_UI_MOUNT_SKINS_" + name : "@VANITY_UI_MOUNT_SKINS_HEADLINE";
            AddItems(Add(mounts, "mounts/" + name, title), group.Select(x => x.Product));
        }
        foreach (var group in individuals.Where(x => x.Unlock!.Type != VanityCatalog.UnlockType.Mount)
                     .GroupBy(x => x.Unlock!.Type == VanityCatalog.UnlockType.Skin ? x.Unlock.Slot : 100 + (int)x.Unlock.Type).OrderBy(g => g.Key))
        {
            var title = group.Key switch
            {
                0 => "@BUILDING_ITEMS_CATEGORY_WEAPONS", 1 => "@EQUIPMENT_SLOT_OFFHAND", 2 => "@EQUIPMENT_SLOT_HEAD",
                3 => "@EQUIPMENT_SLOT_ARMOR", 4 => "@EQUIPMENT_SLOT_SHOES", 5 => "@EQUIPMENT_SLOT_BAG", 6 => "@EQUIPMENT_SLOT_CAPE",
                100 => "@VANITY_CATEGORY_EMOTE", 103 => "@VANITY_UI_CUSTOMIZATION_HEADLINE",
                104 => "@VANITY_UI_AVATARS_CATEGORY_AVATAR", 105 => "@VANITY_UI_AVATARS_CATEGORY_AVATAR_RING", _ => "@VANITY_UI_HEADLINE"
            };
            AddItems(Add(wardrobe, "vanity/" + group.Key, title), group.Select(x => x.Product));
        }
        return nodes.Where(c => c.Products.Count > 0 || c.Children.Count > 0).ToArray();
    }

    private static int Rank(string id, params string[] first)
    {
        var rank = Array.IndexOf(first, id);
        return rank < 0 ? first.Length : rank;
    }

    private static int LinkId(Category category) => TileId("navigate:" + category.Key);

    private static int ExternalLinkId(int category) => TileId("external:" + category.ToString(CultureInfo.InvariantCulture));

    private static ShopTileUpdateEvent ExternalLink(int category)
    {
        var gold = category == 2;
        // 345319 ProductStoreLinkTile..cctor F2BC90 defines these two teaser models.
        // F2B520 loads dw8 and F2B3B0 hides an empty dw7, so ordinary amount-only
        // product cards cannot also serve as the external gold/Premium modal links.
        return new()
        {
            dw3 = ExternalLinkId(category), dw4 = gold ? "navigate:gold" : "navigate:premium", dw6 = 3,
            dw7 = gold ? "@SHOP_CATEGORY_GOLD" : "@SHOP_CATEGORY_PREMIUM",
            dw8 = gold ? "illu_offer_gold_teaser" : "illu_offer_premium_teaser",
            dw9 = "", dxa = "", dxb = "", dxc = "", dxd = [], dxe = Design(gold ? "gold" : "premium"),
            dxf = false, dxg = false, dxh = category, dxi = -1, dxj = "", dxk = -1, dxl = -1,
            dxv = [], dxw = [], dxx = [], dxy = [], dxz = [], dx0 = [],
        };
    }

    private static ShopArtwork.Entry? Illustration(World world, ShopProduct product, IReadOnlyList<ShopProduct>? offers = null)
    {
        var direct = world.Shop.Artwork.Resolve(product, world.Vanity.Catalog);
        if (!string.IsNullOrEmpty(direct?.Native)) return direct;
        if (!product.Id.StartsWith("vanity.", StringComparison.Ordinal) || product.Unlocks is not { Length: 1 }) return direct;
        // An individual still buys exactly one unlock. Preserve both halves of its collection's
        // original illustration; the title, price, entitlement and 3D preview remain that single item.
        // Match actual membership, never a similar family name or unrelated placeholder artwork.
        foreach (var offer in (offers ?? world.Shop.Catalog.Products(world.Vanity.Catalog, world.Items))
                     .Where(p => p.Currency == "cash" && p.Unlocks?.Contains(product.Unlocks[0]) == true))
        {
            var art = world.Shop.Artwork.Resolve(offer, world.Vanity.Catalog);
            if (!string.IsNullOrEmpty(art?.Native)) return art;
        }
        return direct;
    }

    private static IEnumerable<ShopProduct> Descendants(Category category) =>
        category.Products.Concat(category.Children.SelectMany(Descendants));

    internal static ShopUpdateEvent Update(World world, Guid account, int selected = 0, bool registerAll = false, bool registerChildren = false)
    {
        var navigation = Navigation(world, account);
        var visible = navigation.Where(c => c.Parent is null).ToList();
        var current = navigation.FirstOrDefault(c => c.Id == selected);
        if (current?.Parent is not null) visible.Add(current);
        var categories = registerAll ? navigation : registerChildren && current is not null
            ? visible.Concat(current.Children).Distinct().ToArray() : visible.ToArray();
        var update = new ShopUpdateEvent
        {
            dx2 = world.Shop.Enabled,
            dx3 = navigation.Any(c => c.Id == 2) ? ExternalLinkId(2) : -1,
            dx4 = navigation.Any(c => c.Id == 3) ? ExternalLinkId(3) : -1,
            dx5 = categories.Select(c => c.Id).ToArray(), dx6 = categories.Select(c => c.Title).ToArray(),
            dx7 = categories.Select(c => c.Products.Select(p => TileId(p.Id)).Concat(c.Children.Select(LinkId))
                .Concat(c.Parent is null ? [] : new[] { LinkId(c.Parent) }).ToArray()).ToArray(),
            // No timed promotional banners: these parallel arrays describe banners, not categories.
            dx8 = [], dx9 = [], dya = [], dyb = [], dyc = [], dyd = [], dye = [], dyf = [], dyg = [], dyh = [], dyi = [],
        };
        return update;
    }

    internal static IEnumerable<ShopUpdateEvent> NavigationUpdates(World world, Guid account, int selected)
    {
        // A4BBAF invalidates the five-minute category request timestamp. Invalidate the next links
        // as well so revisiting a cached child still obtains its current compact sidebar context.
        yield return Update(world, account, selected, registerChildren: true);
        yield return Update(world, account, selected);
    }

    internal static ShopTileUpdateEvent[] Tiles(World world, Guid account, int category)
    {
        var node = Navigation(world, account).FirstOrDefault(c => c.Id == category);
        if (node is null) return [];
        var offers = Products(world, account).Where(p => p.Currency == "cash").ToArray();
        return node.Products.Select(p => Tile(world, p, offers)).Concat(node.Children.Select(child => Link(child, false)))
            .Concat(node.Parent is null ? [] : new[] { Link(node.Parent, true) }).ToArray();

        ShopTileUpdateEvent Link(Category child, bool back) => new()
        {
            dw3 = LinkId(child), dw4 = "navigate:" + child.Key, dw6 = 3, dw7 = back ? "@GENERIC_BUTTON_BACK" : child.Title,
            dw8 = Descendants(child).Select(p => Illustration(world, p, offers)?.Native).FirstOrDefault(s => !string.IsNullOrEmpty(s)) ?? "",
            dw9 = back ? child.Title : "", dxa = "", dxb = "", dxc = "", dxh = child.Id,
            dxe = Design("vanity"),
            dxk = -1, dxl = -1, dxi = -1,
            dxv = [], dxw = [], dxx = [], dxy = [], dxz = [], dx0 = [],
        };
    }

    internal static IEnumerable<AlbionEvent> Initial(World world, Guid account)
    {
        // A4B907–A4B939 replaces the visible list but retains category models while Enabled=true.
        // Register the navigation targets, then immediately publish only the compact root sidebar.
        yield return Update(world, account, registerAll: true);
        var update = Update(world, account);
        yield return update;
        if (!update.dx2) yield break;
        // ShopUpdate names the links, but only ShopTileUpdate creates their models. Preload these
        // two dedicated teasers so the gold/Premium views can open the store before any category
        // was visited. Their IDs never replace a purchasable product or enter a product grid.
        // Confirmed in 345319: A4DD76 assigns the links; F2B95E/10CE0F7 hide a missing gold link.
        if (update.dx3 >= 0) yield return ExternalLink(2);
        if (update.dx4 >= 0) yield return ExternalLink(3);
    }

    internal static GetShopTilesForCategoryResponse Response(int category, IReadOnlyList<ShopTileUpdateEvent> tiles)
    {
        // a4l describes the two-row grid's slots. A large card occupies a whole column; its second
        // slot must be -1, otherwise the next card is drawn on top of its lower half.
        var slots = new List<int>();
        // The native two-row grid consumes column-major slots. Small product grids are authored
        // in reading order, matching Premium/Gold in the original store (largest packs on top).
        if (tiles.All(t => t.dw6 is 2 or 6))
        {
            foreach (var page in tiles.Chunk(6))
                foreach (var index in new[] { 0, 3, 1, 4, 2, 5 })
                    slots.Add(index < page.Length ? page[index].dw3 : -1);
            return new() { a4k = category, a4l = slots.ToArray(), a4m = 0 };
        }
        foreach (var tile in tiles)
        {
            var large = tile.dw6 is 0 or 4 or 5 or 8 or 9;
            if (large && slots.Count % 2 != 0) slots.Add(-1);
            slots.Add(tile.dw3);
            if (large) slots.Add(-1);
        }
        return new() { a4k = category, a4l = slots.ToArray(), a4m = 0 };
    }

    private static ShopTileUpdateEvent Tile(World world, ShopProduct p, IReadOnlyList<ShopProduct>? offers = null)
    {
        var id = TileId(p.Id);
        var cash = p.Currency == "cash";
        // Native entitlements are display metadata only; the checkout remains the delivery authority.
        // LargeTile resolves vanity entitlement 6 through the client's own unlock registry, including
        // its exact icon and preview. Sending kind 0 discarded that metadata and left a black tile.
        var kinds = new List<int>();
        var amounts = new List<int>();
        var names = new List<string>();
        var extras = new List<int>();
        if (p.PremiumDays > 0) Add(4, p.PremiumDays, "");
        if (p.LearningPoints > 0) Add(9, p.LearningPoints, "");
        if (p.Gold > 0) Add(3, checked((int)(p.Gold / 10000)), "", p.GoldBonus);
        foreach (var unlock in p.Unlocks ?? []) Add(6, 1, unlock.ToString(CultureInfo.InvariantCulture));
        // Entitlement kind5 carries the original MailItem[] JSON contract, not the numeric
        // item index. It is display metadata; the local checkout delivers the durable items.
        if (p.Items is { Length: > 0 }) Add(5, 0, JsonSerializer.Serialize(p.Items.Select(i => new { ItemName = i.Name, StackSize = i.Count })));
        var artwork = Illustration(world, p, offers);
        var individualIllustration = p.Id.StartsWith("vanity.", StringComparison.Ordinal) && !string.IsNullOrEmpty(artwork?.Native);
        var splitIllustration = !string.IsNullOrEmpty(artwork?.NativeBottom);
        // 345319 dr2.q16996B0 prefixes currency amounts with dmp.el's original GOLD
        // symbol (literal39455); the same symbol registry contains SILVER (literal39715).
        // Invariant grouping matches the active English client/reference. Unlike the native
        // formatter's CurrentCulture, this server subtitle does not yet follow other client locales.
        var price = cash ? ""
            : $"\uFFFF{(p.Currency == "gold" ? "GOLD" : "SILVER")}\uFFFF {(p.Price / 10000m).ToString("N0", CultureInfo.InvariantCulture)}";
        var family = cash ? OfferFamily(p.Id) : null;
        var referencePrice = cash ? ReferencePrice(p) : -1;
        var discounted = cash && referencePrice > p.Price;
        return new ShopTileUpdateEvent
        {
            // ProductStoreSmallTile loads Gui/MainAtlasIcons/ + dw8, not a web URL.
            // The split promotional illustration belongs to LargeImageTile (4). LargeTile (0)
            // anchors a visible '+' and item list below the art, pushing those icons out of its card.
            // The 345319 level59 LargeImageTile prefab instead places the title at the foot of the art
            // and exposes entitlements through the native preview. Both occupy a full grid column.
            dw3 = id, dw4 = p.Id, dw5 = 0,
            // Exact individual mount posters have both halves too. SmallPreviewTile (6)
            // only loads dw8 (345319 F39BCA); selecting it first silently drops dxc.
            // Collection illustrations retain their original bottom half for individual unlocks too.
            dw6 = p.Items is { Length: > 0 } ? (byte)0 : splitIllustration ? (p.LearningPoints > 0 && p.Unlocks is not { Length: > 0 } ? (byte)8 : (byte)4) : individualIllustration ? (byte)6
                : p.Unlocks is { Length: > 0 } ? (byte)0 : (byte)2,
            dw7 = family is not null ? "@SHOP_OFFER_" + family + "_TITLE" : DisplayTitle(world, p),
            dw8 = artwork?.Native ?? "", dw9 = cash ? DisplaySubtitle(p) : price,
            // Param 8 is bottom text; Param 9 is the second image. The client's intermediate model
            // reorders them, so copying its field offsets directly would print the resource name.
            dxa = individualIllustration && splitIllustration ? "" : cash ? DisplayInfo(p, family)
                : string.IsNullOrEmpty(p.Description) ? price : p.Description + "\n\n" + price,
            dxb = "", dxc = artwork?.NativeBottom ?? "", dxd = [], dxe = Design(p.Category), dxf = false, dxg = true,
            // ProductStoreLinkTile in the external gold/Premium modal jumps to this category.
            dxh = p.Category switch { "gold" => 2, "premium" => 3, _ => 0 },
            dxi = -1, dxj = p.Id,
            // Non-cash prices must not be displayed as USD. TryGetPriceUSD returns false for a negative value.
            // 345319 price-model A4D110/8D1B90: start=0, end=MaxValue activates a permanent
            // discount. All-zero dates disable it; MaxValue also suppresses the F39230 countdown.
            // The comparison price is presentation metadata. The charged amount is always p.Price.
            dxk = referencePrice, dxl = discounted ? checked((int)p.Price) : -1,
            dxm = 0, dxn = 0, dxo = discounted ? long.MaxValue : 0,
            dxp = "", dxq = "", dxr = "", dxs = "", dxt = [], dxu = "",
            dxv = [id], dxw = [false], dxx = [kinds.ToArray()], dxy = [amounts.ToArray()],
            dxz = [extras.ToArray()], dx0 = [names.ToArray()], dx1 = false,
        };
        void Add(int kind, int amount, string name, int extra = 0) { kinds.Add(kind); amounts.Add(amount); names.Add(name); extras.Add(extra); }
    }

    private static string DisplayTitle(World world, ShopProduct p)
    {
        // The manifest preserves the item's actual namelocatag (and avatar/customization tags).
        // All 605 retained keys exist in 345319 localization.bin. Let the native client localize
        // these individual tiles; the Spanish catalogue title remains the local receipt text.
        if (p.Id.StartsWith("vanity.", StringComparison.Ordinal) && p.Unlocks is { Length: 1 }
            && world.Vanity.Catalog.Find(p.Unlocks[0]) is { } unlock
            && world.Shop.Artwork.NameTag(unlock.Name) is { Length: > 0 } nameTag)
            return nameTag;
        // Resource-only tiles render their own localized amount. Portrait cards already have
        // the bundle kind in their subtitle; repeating it in the title crowds out the art.
        if (p.Category is "premium" or "gold" or "learningpoints") return "";
        // This offer uses the mount's original title; there is no SHOP_OFFER_KEEPER title tag.
        if (p.Id == "value.keeper-horse.320") return "@ITEMS_SKIN_HORSE_KEEPER";
        if (p.Currency != "cash") return p.Title;
        foreach (var prefix in new[] { "Colección: ", "Colección del ", "Colección de la ", "Paquete inicial: ", "Paquete de valor: " })
            if (p.Title.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) return p.Title[prefix.Length..];
        return p.Title;
    }

    // Explicit aliases verified against 345319 localization.bin. Resource/offer names do not
    // always match the displayed family (Arcanist=BATTLEMAGE, Reaver=MARAUDER, for example).
    // Unknown configurable offers and individual unlocks retain their own supplied text.
    private static string? OfferFamily(string id) => id switch
    {
        "bundle.essentials" => "ESSENTIALS",
        "bundle.firemage" or "value.firemage.9000" => "FIREMAGE", "bundle.frostmage" or "value.frostmage.9000" => "FROSTMAGE",
        "bundle.alchemist" => "ALCHEMIST", "bundle.assassin" => "ASSASSIN",
        "bundle.barbarian" or "starter.barbarian.180" => "BARBARIAN", "bundle.tracker" => "TRACKER",
        "bundle.arcanist" => "BATTLEMAGE", "bundle.paladin" or "starter.paladin.360" => "PALADIN",
        "bundle.frostknight" => "FROSTKNIGHT", "bundle.adventurer-druid" or "starter.druid.360" => "DRUID",
        "bundle.adventurer-knight" => "KNIGHT_ADVENTURER", "bundle.adventurer-mage" => "MAGE_ADVENTURER",
        "bundle.adventurer-rogue" or "value.rogue.3500" => "ROGUE_ADVENTURER", "bundle.dark-swamp-priest" => "DARK_SWAMP",
        "bundle.demonhunter" => "DEMONHUNTER", "bundle.demonicwarrior" or "starter.demonicwarrior.360" => "DEMONICWARRIOR",
        "bundle.dragon-hunter" => "DRAGONHUNTER", "bundle.marauder" or "value.reaver.19000" => "MARAUDER",
        "bundle.monk" or "value.monk.9000" => "MONK", "bundle.priest" or "value.priest.19000" => "PRIEST_HOLY",
        "bundle.ranger" or "starter.ranger.360" => "RANGER", "bundle.soulkeeper" or "value.soulkeeper.3500" => "SOULKEEPER",
        "bundle.valkyrie" => "VALKYRIE", "bundle.zealot" => "ZEALOT",
        "bundle.corrupted-preacher" => "CORRUPTED_PREACHER", "bundle.faction-warfare-commander" => "FW_COMMANDER",
        "bundle.titan" or "value.titan.19000" => "TITAN",
        "bundle.barbarian-anniversary" => "BARBARIAN_ANNIVERSARY", "bundle.priest-anniversary" => "PRIEST_ANNIVERSARY",
        "bundle.paladin-anniversary" => "PALADIN_ANNIVERSARY", "bundle.marauder-anniversary" => "MARAUDER_ANNIVERSARY",
        "bundle.alchemist-anniversary" => "ALCHEMIST_ANNIVERSARY", "bundle.corrupted-preacher-anniversary" => "CORRUPTED_PREACHER_ANNIVERSARY",
        "mount.darkfang" => "COUGAR_DARK", "mount.cursed-direboar" or "value.cursed-direboar.1700" => "OCCULT_DIREBOAR",
        "mount.armored-pony" or "value.armored-pony.1700" => "ARMORED_PONY", "mount.hell-mare" => "HELL_MARE", "mount.mistbison" => "OX_MIST",
        "mount.mistcougar" => "MISTCOUGAR", "mount.mistwolf" => "MISTWOLF", "mount.shadowcat" => "SHADOWCAT",
        "mount.undead-direwolf" => "UNDEAD_DIREWOLF",
        _ => null,
    };

    private static string DisplaySubtitle(ShopProduct p) => p.Category switch
    {
        "mounts" => "@SHOP_OFFER_MOUNT_SKIN_SUB_TITLE",
        "bundles" => "@SHOP_OFFER_WARDROBE_MOUNT_SUB_TITLE",
        "starter" => "@SHOP_OFFER_STARTER_BUNDLE_SUB_TITLE",
        "value" => "@SHOP_OFFER_VALUE_BUNDLE_SUB_TITLE",
        "featured" => "@SHOP_OFFER_PACK_SUB_TITLE",
        _ => "",
    };

    private static string DisplayInfo(ShopProduct p, string? family)
    {
        if (p.Category == "starter") return "@SHOP_OFFER_STARTER_BUNDLE_INFO_GENERIC";
        if (p.Category == "value") return "@SHOP_OFFER_VALUE_BUNDLE_INFO_GENERIC";
        if (p.Id == "bundle.essentials") return "@SHOP_OFFER_ESSENTIALS_PACK_INFO";
        // Those official descriptions advertise a limited availability window, avatars, or guild/
        // faction variants that these local offers do not promise. Retain the actual offer's text.
        if (family is null || family.EndsWith("_ANNIVERSARY", StringComparison.Ordinal)
            || family is "BARBARIAN" or "KNIGHT_ADVENTURER" or "MAGE_ADVENTURER" or "ROGUE_ADVENTURER" or "FW_COMMANDER")
            return p.Description;
        return "@SHOP_OFFER_" + family + "_INFO";
    }

    private static int ReferencePrice(ShopProduct p)
    {
        var original = p.Id switch
        {
            "starter.ranger.360" or "starter.druid.360" or "starter.paladin.360" or "starter.demonicwarrior.360" => 18387,
            "starter.barbarian.180" => 11291,
            "value.titan.19000" or "value.priest.19000" or "value.reaver.19000" => 12990,
            "value.rogue.3500" or "value.soulkeeper.3500" => 4990,
            "value.monk.9000" or "value.frostmage.9000" or "value.firemage.9000" => 7990,
            "value.cursed-direboar.1700" or "value.armored-pony.1700" => 2490,
            "value.keeper-horse.320" => 1695,
            "bundle.corrupted-preacher" => 2995,
            "mount.cursed-direboar" => 1495,
            _ => 0,
        };
        return original > p.Price && p.Price >= 0 ? original : checked((int)p.Price);
    }

    private static int[] Design(string category)
    {
        // 345319 GetDesignColor F38510 requires exactly six packed RRGGBBAA values. Empty
        // arrays produce zero (black). White preserves the original silver border. Burgundy
        // comes from the native prefab; the other category tints follow the supplied reference.
        var colors = category switch
        {
            "premium" => new uint[] { 0xFFFFFFFF, 0x261118FF, 0x91001B4D, 0x91001B4D, 0x680F2EFF, 0x260512FF },
            "gold" or "value" => new uint[] { 0xFFFFFFFF, 0x2F1F1AFF, 0xA6821A4D, 0x805500BF, 0x503B1BFF, 0x2F1F1AFF },
            "learningpoints" => new uint[] { 0xFFFFFFFF, 0x101B46FF, 0x17376C55, 0x243F8B66, 0x17285BFF, 0x10153BFF },
            "starter" => new uint[] { 0xFFFFFFFF, 0x320706FF, 0x700C0C55, 0x700C0C66, 0x4C0A06FF, 0x330402FF },
            "featured" => new uint[] { 0xFFFFFFFF, 0x321049FF, 0x6535C94D, 0x2000114D, 0x6535C9FF, 0x200011FF },
            _ => new uint[] { 0xFFFFFFFF, 0x24282CFF, 0x00000000, 0x10101566, 0x00000000, 0x00000000 },
        };
        return colors.Select(c => unchecked((int)c)).ToArray();
    }
}
