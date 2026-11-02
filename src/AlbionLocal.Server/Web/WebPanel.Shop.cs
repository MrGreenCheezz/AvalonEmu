using System.Collections.Concurrent;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using AlbionLocal.Server.Game;
using AlbionLocal.Server.Persistence;

namespace AlbionLocal.Server.Web;

/// <summary>Player storefront. Its cookies and permissions are independent of the administrator panel.</summary>
public sealed partial class WebPanel
{
    private const string ShopCookie = "alp_shop_session", ShopLoginCookie = "alp_shop_login";
    private readonly ConcurrentDictionary<string, ShopSession> _shopSessions = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, DateTime> _shopChallenges = new(StringComparer.Ordinal);
    private readonly RateLimit _shopLogins = new(10, TimeSpan.FromMinutes(5));

    private sealed class ShopSession(Guid account, string name, Guid? character)
    {
        public Guid Account { get; } = account;
        public string Name { get; } = name;
        public string Csrf { get; } = Token();
        public Guid? Character { get; set; } = character;
        public DateTime Expires { get; set; }
    }

    private sealed record ShopCharacter(Guid Id, string Name);
    private sealed record ShopCharge(string Name, int Quantity);
    private const int ShopPageSize = 30;
    private sealed record ShopView(IReadOnlyList<ShopProduct> Products, IReadOnlyList<ShopCharacter> Characters, long Gold,
        IReadOnlySet<short> Owned, IReadOnlyDictionary<short, string> UnlockNames, IReadOnlyDictionary<string, string> Images);

    private async Task<WebResponse> HandleShop(WebRequest r)
    {
        if (r.Method == "GET" && r.Path.StartsWith("/shop/art/", StringComparison.Ordinal))
        {
            var file = _shopArtwork.FilePath(r.Path["/shop/art/".Length..]);
            if (file is null) return Text(404, "artwork not found");
            var info = new FileInfo(file);
            if (!info.Exists || info.Length > 4 * 1024 * 1024) return Text(404, "artwork unavailable");
            return new WebResponse(200, "", "image/png")
            {
                BinaryBody = await File.ReadAllBytesAsync(file), CacheControl = "public, max-age=3600",
            };
        }
        // MiniHttp serves HTTP. An HTTPS/null origin is not this origin, even if its authority happens to match.
        if (r.Method == "POST" && r.Headers.TryGetValue("Origin", out var origin)
            && (!Uri.TryCreate(origin, UriKind.Absolute, out var uri) || uri.Scheme != "http"))
            return ShopError(null, 403, "La solicitud no procede de esta tienda.");
        if (r.Method == "GET" && (r.Path == "/shop" || r.Path == "/shop/enter") && r.Q("token").Length > 0)
        {
            var identity = await OnWorld(() => _world.Shop.RedeemLoginToken(r.Q("token")));
            var account = identity is { } login ? _world.Characters.FindAccount(login.Account) : null;
            if (!ShopAccountAllowed(account))
                return ShopLoginPage("El enlace ha caducado o ya se utilizó. Inicia sesión para continuar.", 403);
            var product = await OnWorld(() => _world.Shop.Products(account!.Id).FirstOrDefault(p => p.Id == r.Q("product"))?.Id);
            return StartShopSession(r, account!, identity!.Value.Character, product);
        }
        if (r.Path == "/shop/login")
            return r.Method == "GET" ? ShopLoginPage() : await ShopLogin(r);

        var s = AuthenticateShop(r);
        if (s is null) return r.Method == "GET" ? WebResponse.Redirect("/shop/login") : ShopError(null, 403, "Tu sesión terminó. Vuelve a iniciar sesión.");
        if (r.Method == "POST" && !SameToken(s.Csrf, r.F("csrf")))
            return ShopError(s, 403, "El formulario ha caducado. Recarga la página e inténtalo de nuevo.");
        switch (r.Method, r.Path)
        {
            case ("GET", "/shop"): return await ShopCatalog(r, s);
            case ("GET", "/shop/product"): return await ShopProductPage(r, s);
            case ("GET", "/shop/order"): return await ShopOrderPage(r, s);
            case ("GET", "/shop/history"): return await ShopHistory(s);
            case ("POST", "/shop/character"): return await ShopSelectCharacter(r, s);
            case ("POST", "/shop/prepare"): return await ShopPrepare(r, s);
            case ("POST", "/shop/confirm"): return await ShopConfirm(r, s);
            case ("POST", "/shop/cancel"): return await ShopCancel(r, s);
            case ("POST", "/shop/logout"):
                if (r.Cookies.TryGetValue(ShopCookie, out var token)) _shopSessions.TryRemove(token, out _);
                var logout = WebResponse.Redirect("/shop/login");
                logout.Headers.Add(("Set-Cookie", ShopSessionCookie("", 0)));
                return logout;
            default: return ShopError(s, 404, "Esta página no existe.");
        }
    }

    private static bool ShopAccountAllowed(Account? account) => account is not null
        && !(account.BannedUntil is { } banned && banned > DateTime.UtcNow)
        && !(account.LockedUntil is { } locked && locked > DateTime.UtcNow);

    private static bool SameToken(string expected, string supplied) => supplied.Length > 0 && supplied.Length == expected.Length
        && CryptographicOperations.FixedTimeEquals(Encoding.ASCII.GetBytes(expected), Encoding.ASCII.GetBytes(supplied));

    private ShopSession? AuthenticateShop(WebRequest r)
    {
        var now = DateTime.UtcNow;
        foreach (var (key, old) in _shopSessions)
            lock (old) { if (old.Expires <= now) _shopSessions.TryRemove(key, out _); }
        if (!r.Cookies.TryGetValue(ShopCookie, out var token) || !_shopSessions.TryGetValue(token, out var s)) return null;
        lock (s)
        {
            if (s.Expires <= now || !ShopAccountAllowed(_world.Characters.FindAccount(s.Account)))
            {
                _shopSessions.TryRemove(token, out _);
                return null;
            }
            s.Expires = now.AddMinutes(_options.SessionMinutes);
        }
        return s;
    }

    private WebResponse ShopLoginPage(string? message = null, int status = 200)
    {
        var now = DateTime.UtcNow;
        foreach (var (key, expiry) in _shopChallenges)
            if (expiry <= now) _shopChallenges.TryRemove(key, out _);
        if (_shopChallenges.Count > 10_000) return ShopError(null, 429, "Demasiadas solicitudes. Espera un momento.");
        var token = Token();
        _shopChallenges[token] = now.AddMinutes(10);
        var body = "<section class=\"card shop-login\"><h1>Tu tienda de Albion</h1><p>Entra con la cuenta que usas para jugar.</p>"
            + (message is null ? "" : Note(message, true))
            + "<form method=post action=\"/shop/login\" class=stack>" + ShopHidden("csrf", token)
            + "<label>Cuenta<input name=name required maxlength=254 autocomplete=username></label>"
            + "<label>Contraseña<input name=password type=password required maxlength=72 autocomplete=current-password></label>"
            + "<button>Entrar a la tienda</button></form><p><a href=\"/register\">Crear una cuenta</a></p></section>";
        var response = ShopPage("Entrar", body, null, status);
        response.Headers.Add(("Set-Cookie", $"{ShopLoginCookie}={token}; Path=/shop; HttpOnly; SameSite=Strict; Max-Age=600"));
        return response;
    }

    private async Task<WebResponse> ShopLogin(WebRequest r)
    {
        if (!r.Cookies.TryGetValue(ShopLoginCookie, out var challenge) || !SameToken(challenge, r.F("csrf"))
            || !_shopChallenges.TryRemove(challenge, out var expiry) || expiry <= DateTime.UtcNow)
            return ShopLoginPage("Recarga el formulario para iniciar sesión.", 403);
        if (!_shopLogins.Allow(r.Remote)) return ShopLoginPage("Demasiados intentos. Espera unos minutos.", 429);
        var account = _world.Characters.FindAccount(r.F("name").Trim());
        var password = r.F("password");
        bool valid;
        if (account?.PasswordHash is { } hash) valid = Passwords.Verify(password, hash);
        else { Passwords.Dummy(password); valid = false; }
        if (!valid || !ShopAccountAllowed(account))
        {
            await Task.Delay(300);
            return ShopLoginPage("No se pudo iniciar sesión. Revisa la cuenta y la contraseña.", 403);
        }
        return StartShopSession(r, account!, null);
    }

    private WebResponse StartShopSession(WebRequest r, Account account, Guid? preferred, string? product = null)
    {
        var characters = _world.Characters.List(account.Id);
        var character = characters.FirstOrDefault(c => c.Id == preferred)?.Id ?? characters.FirstOrDefault()?.Id;
        if (r.Cookies.TryGetValue(ShopCookie, out var old)) _shopSessions.TryRemove(old, out _);
        var token = Token();
        _shopSessions[token] = new ShopSession(account.Id, account.Shown, character)
            { Expires = DateTime.UtcNow.AddMinutes(_options.SessionMinutes) };
        // Only a catalogue id reaches this argument; no user-supplied URL is ever a redirect destination.
        var response = WebResponse.Redirect(product is null ? "/shop" : ShopProductUrl(product));
        response.Headers.Add(("Set-Cookie", ShopSessionCookie(token, _options.SessionMinutes * 60)));
        response.Headers.Add(("Set-Cookie", $"{ShopLoginCookie}=; Path=/shop; HttpOnly; SameSite=Strict; Max-Age=0"));
        return response;
    }

    private static string ShopSessionCookie(string token, int seconds) => $"{ShopCookie}={token}; Path=/shop; HttpOnly; SameSite=Strict; Max-Age={seconds}";
    private Task<ShopView> ShopSnapshot(ShopSession s) => OnWorld(() =>
    {
        var products = _world.Shop.Products(s.Account);
        return new ShopView(products, _world.Characters.List(s.Account).Select(c => new ShopCharacter(c.Id, c.Name)).ToArray(),
            _world.Shop.Gold(s.Account), _world.Shop.Owned(s.Account), ShopUnlockNames(products, products.SelectMany(p => p.Unlocks ?? [])),
            products.ToDictionary(p => p.Id, p => _shopArtwork.ImageUrl(_shopArtwork.Resolve(p, _world.Vanity.Catalog)), StringComparer.Ordinal));
    });

    // Called only within OnWorld: renderers receive plain strings, never a live world/catalogue reference.
    private IReadOnlyDictionary<short, string> ShopUnlockNames(IReadOnlyList<ShopProduct> products, IEnumerable<short> ids)
    {
        var titles = products.ToDictionary(p => p.Id, p => p.Title, StringComparer.Ordinal);
        return ids.Distinct().ToDictionary(id => id, id => titles.GetValueOrDefault($"vanity.{id}")
            ?? _world.Vanity.Catalog.Find(id)?.Name.Replace('_', ' ') ?? $"Apariencia {id}");
    }
    private static Guid? SelectedCharacter(ShopSession s) { lock (s) return s.Character; }

    private async Task<WebResponse> ShopSelectCharacter(WebRequest r, ShopSession s)
    {
        if (!Guid.TryParse(r.F("character"), out var id)
            || !await OnWorld(() => _world.Characters.List(s.Account).Any(c => c.Id == id)))
            return ShopError(s, 400, "Elige un personaje de tu cuenta.");
        lock (s) s.Character = id;
        return WebResponse.Redirect("/shop");
    }

    private async Task<WebResponse> ShopCatalog(WebRequest r, ShopSession s)
    {
        var view = await ShopSnapshot(s);
        var query = r.Q("q").Trim();
        var category = r.Q("category");
        var products = view.Products.Where(p => (category.Length == 0 || p.Category == category)
            && (query.Length == 0 || p.Title.Contains(query, StringComparison.OrdinalIgnoreCase)
                || p.Description.Contains(query, StringComparison.OrdinalIgnoreCase))).ToArray();
        var pages = Math.Max(1, (products.Length + ShopPageSize - 1) / ShopPageSize);
        var page = Math.Clamp(int.TryParse(r.Q("page"), out var requested) ? requested : 1, 1, pages);
        var shown = products.Skip((page - 1) * ShopPageSize).Take(ShopPageSize).ToArray();
        var body = new StringBuilder(ShopHeading("Prepara tu próxima aventura", "Oro, Premium y apariencias para tu cuenta.", view.Gold))
            .Append(ShopCharacterPicker(s, view.Characters))
            .Append("<form method=get action=\"/shop\" class=\"inline shop-filters\"><label>Buscar<input type=search name=q value=\"")
            .Append(H(query)).Append("\" placeholder=\"Buscar productos\"></label><label>Categoría<select name=category><option value=\"\">Todas</option>");
        foreach (var item in view.Products.Select(p => p.Category).Distinct().OrderBy(c => c))
            body.Append($"<option value=\"{H(item)}\"{(item == category ? " selected" : "")}>{H(ShopCategory(item))}</option>");
        body.Append("</select></label><button>Buscar</button><a href=\"/shop\">Ver todo</a></form>")
            .Append($"<p class=muted>{products.Length} productos{(products.Length > 0 ? $" · Mostrando {(page - 1) * ShopPageSize + 1}–{(page - 1) * ShopPageSize + shown.Length}" : "")}</p>")
            .Append("<div class=shop-products>");
        foreach (var product in shown) body.Append(ShopProductCard(product, ShopAlreadyOwned(product, view.Owned), view.Images.GetValueOrDefault(product.Id)));
        body.Append("</div>");
        if (pages > 1) body.Append(ShopPagination(page, pages, query, category));
        if (products.Length == 0) body.Append(Note("No hay productos con esos filtros. Prueba otra búsqueda o elige Todas.", false));
        return ShopPage("Tienda", body.ToString(), s);
    }

    private async Task<WebResponse> ShopProductPage(WebRequest r, ShopSession s)
    {
        var view = await ShopSnapshot(s);
        var p = view.Products.FirstOrDefault(p => p.Id == r.Q("id"));
        if (p is null) return ShopError(s, 404, "Este producto no está disponible.");
        var selected = view.Characters.FirstOrDefault(c => c.Id == SelectedCharacter(s));
        var body = "<p><a href=\"/shop\">Volver al catálogo</a></p><section class=\"card shop-detail\">"
            + ShopImage(view.Images.GetValueOrDefault(p.Id), p.Title) + ShopProductIntro(p) + "<h2>Incluye</h2>" + ShopBenefits(p.Gold, p.PremiumDays, p.Unlocks, names: view.UnlockNames, learningPoints: p.LearningPoints, items: p.Items)
            + $"<p class=shop-price>{H(ShopPrice(p.Currency, p.Price))}</p>";
        if (ShopAlreadyOwned(p, view.Owned)) body += Note("Ya tienes una o más apariencias de este producto. Elige otra oferta.", false);
        else if (selected is null) body += Note("Crea un personaje en el juego y selecciónalo en la tienda para continuar.", false);
        else body += $"<p>Personaje: <strong>{H(selected.Name)}</strong> <a href=\"/shop\">Cambiar</a></p>"
            + ShopForm(s, "/shop/prepare", "Continuar al checkout", ShopHidden("product", p.Id) + ShopHidden("requestId", Token()));
        return ShopPage(p.Title, body + "</section>", s);
    }

    private async Task<WebResponse> ShopPrepare(WebRequest r, ShopSession s)
    {
        if (SelectedCharacter(s) is not { } character) return ShopError(s, 400, "Selecciona un personaje antes de comprar.");
        var requestId = r.F("requestId");
        if (requestId.Length is < 16 or > 128) return ShopError(s, 400, "Recarga el producto para continuar.");
        var result = await OnWorld(() => _world.Shop.Prepare(s.Account, character, r.F("product"), requestId));
        return result.Success && result.Order is { } order ? WebResponse.Redirect(ShopOrderUrl(order.Id)) : ShopError(s, 400, result.Message);
    }

    private async Task<WebResponse> ShopConfirm(WebRequest r, ShopSession s)
    {
        if (r.F("accept") != "yes" || r.F("method") != "demo")
            return ShopError(s, 400, "Confirma la compra con el método de demostración.");
        var result = await OnWorld(() => _world.Shop.Confirm(s.Account, r.F("order")));
        if (!result.Success || result.Order is not { } order) return ShopError(s, 400, result.Message);
        // The purchase is already durable. Refreshing this presentation only reads the receipt; it never buys again.
        var url = ShopOrderUrl(order.Id);
        return ShopPage("Procesando", "<section class=\"card shop-processing\" role=status><span class=shop-spinner aria-hidden=true></span>"
            + "<h1>Preparando tu recibo</h1><p>La confirmación se ha guardado.</p>"
            + $"<a class=shop-link href=\"{H(url)}\">Ver recibo</a></section>", s,
            head: $"<meta http-equiv=refresh content=\"1;url={H(url)}\">");
    }

    private async Task<WebResponse> ShopCancel(WebRequest r, ShopSession s)
    {
        var result = await OnWorld(() => _world.Shop.Cancel(s.Account, r.F("order")));
        return result.Success && result.Order is { } order ? WebResponse.Redirect(ShopOrderUrl(order.Id)) : ShopError(s, 400, result.Message);
    }

    private async Task<WebResponse> ShopOrderPage(WebRequest r, ShopSession s)
    {
        var (order, characters, names, charges, image) = await OnWorld(() =>
        {
            var foundOrder = _world.Shop.History(s.Account).FirstOrDefault(o => o.Id == r.Q("id"));
            var product = _world.Shop.Products(s.Account).FirstOrDefault(p => p.Id == foundOrder?.ProductId);
            return (foundOrder, _world.Characters.List(s.Account).Select(c => new ShopCharacter(c.Id, c.Name)).ToArray(),
                ShopUnlockNames(_world.Shop.Products(s.Account), foundOrder?.Unlocks ?? []),
                ShopChargeSnapshot(foundOrder?.Charges ?? []),
                product is null ? "" : _shopArtwork.ImageUrl(_shopArtwork.Resolve(product, _world.Vanity.Catalog)));
        });
        if (order is null) return ShopError(s, 404, "No se encontró este pedido en tu cuenta.");
        var pending = order.Status == "pending";
        var title = pending ? "Confirma tu compra" : order.Status == "completed" ? "Compra completada" : "Estado del pedido";
        var character = characters.FirstOrDefault(c => c.Id == order.Character)?.Name ?? "Personaje eliminado";
        var body = new StringBuilder($"<section class=\"card shop-checkout\"><h1>{H(title)}</h1><p class=\"shop-status status-{H(order.Status)}\">{H(ShopStatus(order.Status))}</p>")
            .Append(ShopImage(image, order.Title)).Append($"<h2>{H(order.Title)}</h2>").Append(ShopBenefits(order.Gold, order.PremiumDays, order.Unlocks, order.Silver, names, order.LearningPoints, order.Items))
            .Append(ShopChargeList(charges))
            .Append("<table>").Append(Row("Personaje", character)).Append(Row("Total", ShopPrice(order.Currency, order.Price)))
            .Append(Row("Método", order.Currency == "cash" ? "Demo •••• 4242" : "Saldo del juego"))
            .Append(Row("Pedido", order.Id)).Append(Row("Creado", ShopDate(order.CreatedAt)))
            .Append(pending ? Row("Válido hasta", ShopDate(order.ExpiresAt)) : order.CompletedAt is { } at ? Row("Confirmado", ShopDate(at)) : "")
            .Append("</table>");
        if (order.Error is { Length: > 0 } error) body.Append(Note(error, true));
        if (pending)
            body.Append("<p class=muted>Simulación local. No se realizará ningún cargo real.</p>")
                .Append(ShopForm(s, "/shop/confirm", "Confirmar compra", ShopHidden("order", order.Id) + ShopHidden("method", "demo")
                    + "<label class=shop-consent><input type=checkbox name=accept value=yes required> He revisado el producto, el personaje y el total.</label>", "stack"))
                .Append(ShopForm(s, "/shop/cancel", "Cancelar pedido", ShopHidden("order", order.Id), "inline", "link"));
        else if (order.Status == "completed") body.Append("<p>El contenido ya está disponible. Puedes volver al juego.</p>");
        body.Append("<p><a href=\"/shop\">Seguir comprando</a> <span aria-hidden=true> · </span> <a href=\"/shop/history\">Ver mis pedidos</a></p></section>");
        return ShopPage(title, body.ToString(), s);
    }

    private async Task<WebResponse> ShopHistory(ShopSession s)
    {
        var orders = await OnWorld(() => _world.Shop.History(s.Account)
            .Select(o => (Order: o, Charges: ShopChargeSnapshot(o.Charges))).ToList());
        var body = new StringBuilder("<section class=card><h1>Mis pedidos</h1>");
        if (orders.Count == 0) body.Append("<p>Aún no tienes pedidos. <a href=\"/shop\">Explora la tienda</a>.</p>");
        else
        {
            body.Append("<div class=scroll><table><thead><tr><th>Producto</th><th>Total</th><th>Estado</th><th>Pedido</th></tr></thead><tbody>");
            foreach (var entry in orders.OrderByDescending(o => o.Order.CreatedAt))
            {
                var o = entry.Order;
                body.Append($"<tr><td>{H(o.Title)}{ShopChargeList(entry.Charges)}</td><td>{H(ShopPrice(o.Currency, o.Price))}</td><td>{H(ShopStatus(o.Status))}</td>"
                    + $"<td><a href=\"{H(ShopOrderUrl(o.Id))}\">{H(o.Id)}</a></td></tr>");
            }
            body.Append("</tbody></table></div>");
        }
        return ShopPage("Mis pedidos", body.Append("</section>").ToString(), s);
    }

    // Quantities come from the immutable receipt, never the current consumable stock or bundle size.
    // Resolve only the display names while holding the world lock, just like the skin names.
    private ShopCharge[] ShopChargeSnapshot(IReadOnlyDictionary<short, int> charges) => charges.OrderBy(c => c.Key)
        .Select(c => new ShopCharge(_world.Shop.KillEmotes.Find(c.Key)?.Name ?? $"Gesto de victoria {c.Key}", c.Value)).ToArray();

    private static string ShopChargeList(IReadOnlyList<ShopCharge> charges) => charges.Count == 0 ? ""
        : "<ul class=shop-benefits>" + string.Concat(charges.Select(c => $"<li>{c.Quantity.ToString("N0", ShopCulture)} cargas de {H(c.Name)}</li>")) + "</ul>";

    private static string ShopOrderUrl(string id) => "/shop/order?id=" + Uri.EscapeDataString(id);
    private static string ShopProductUrl(string id) => "/shop/product?id=" + Uri.EscapeDataString(id);
    private static string ShopPagination(int page, int pages, string query, string category)
    {
        string Link(int target, string label) => $"<a class=shop-link href=\"/shop?page={target}&amp;q={Uri.EscapeDataString(query)}&amp;category={Uri.EscapeDataString(category)}\">{label}</a>";
        return "<nav class=shop-pagination aria-label=\"Páginas del catálogo\">"
            + (page > 1 ? Link(page - 1, "Anterior") : "") + $"<span>Página {page} de {pages}</span>"
            + (page < pages ? Link(page + 1, "Siguiente") : "") + "</nav>";
    }
    private static string ShopHidden(string name, string value) => $"<input type=hidden name={name} value=\"{H(value)}\">";
    private static string ShopForm(ShopSession s, string action, string label, string inputs = "", string cls = "inline", string buttonClass = "") =>
        $"<form method=post action=\"{action}\" class=\"{cls}\">{ShopHidden("csrf", s.Csrf)}{inputs}<button class=\"{buttonClass}\">{H(label)}</button></form>";

    private static string ShopHeading(string title, string description, long gold) => $"<section class=shop-heading><div><h1>{H(title)}</h1><p>{H(description)}</p></div>"
        + $"<p class=shop-wallet>Oro disponible<strong>{ShopAmount(gold)}</strong></p></section>";

    private static string ShopCharacterPicker(ShopSession s, IReadOnlyList<ShopCharacter> characters)
    {
        if (characters.Count == 0) return Note("Tu cuenta todavía no tiene personajes. Crea uno en el juego para comprar.", false);
        var inputs = new StringBuilder("<label>Personaje<select name=character>");
        foreach (var c in characters) inputs.Append($"<option value=\"{c.Id}\"{(c.Id == SelectedCharacter(s) ? " selected" : "")}>{H(c.Name)}</option>");
        return "<section class=\"card shop-character\">" + ShopForm(s, "/shop/character", "Seleccionar", inputs.Append("</select></label>").ToString()) + "</section>";
    }

    private static string ShopProductIntro(ShopProduct p) => $"<p class=shop-category>{H(ShopCategory(p.Category))}</p><h2>{H(p.Title)}</h2><p>{H(p.Description)}</p>";
    private static bool ShopAlreadyOwned(ShopProduct p, IReadOnlySet<short> owned) => p.Unlocks is { Length: > 0 } && p.Unlocks.Any(owned.Contains);
    private static string ShopImage(string? image, string title) => string.IsNullOrEmpty(image) ? ""
        : $"<div class=shop-art><img src=\"{H(image)}\" alt=\"{H(title)}\" loading=lazy decoding=async></div>";
    private static string ShopProductCard(ShopProduct p, bool owned, string? image) => "<article class=\"card shop-product\">" + ShopImage(image, p.Title) + ShopProductIntro(p)
        + (owned ? "<p class=shop-status>Incluye apariencias que ya tienes</p>" : "")
        + ShopBenefits(p.Gold, p.PremiumDays, p.Unlocks, learningPoints: p.LearningPoints, items: p.Items) + $"<div class=shop-product-footer><strong>{H(ShopPrice(p.Currency, p.Price))}</strong>"
        + $"<a class=shop-link href=\"/shop/product?id={Uri.EscapeDataString(p.Id)}\">Ver producto<span class=shop-sr-only>: {H(p.Title)}</span></a></div></article>";

    private static string ShopBenefits(long gold, int premium, short[]? unlocks, long silver = 0, IReadOnlyDictionary<short, string>? names = null, int learningPoints = 0,
        IReadOnlyList<ShopItem>? items = null)
    {
        var list = new StringBuilder("<ul class=shop-benefits>");
        if (gold > 0) list.Append($"<li>{ShopAmount(gold)} de oro para tu cuenta</li>");
        if (silver > 0) list.Append($"<li>{ShopAmount(silver)} de plata para el personaje</li>");
        if (premium > 0) list.Append($"<li>{premium} días de Premium para el personaje</li>");
        if (learningPoints > 0) list.Append($"<li>{learningPoints.ToString("N0", ShopCulture)} puntos de aprendizaje para el personaje</li>");
        foreach (var item in items ?? [])
            list.Append($"<li>{item.Count.ToString("N0", ShopCulture)} × {H(string.IsNullOrWhiteSpace(item.Title) ? item.Name : item.Title)} para el inventario del personaje</li>");
        if (unlocks is { Length: > 0 })
        {
            if (names is null) list.Append($"<li>{unlocks.Length} apariencia(s) para tu cuenta</li>");
            else foreach (var id in unlocks) list.Append($"<li>{H(names.GetValueOrDefault(id) ?? $"Apariencia {id}")}</li>");
        }
        return list.Append("</ul>").ToString();
    }

    private static readonly CultureInfo ShopCulture = CultureInfo.GetCultureInfo("es-CR");
    private static string ShopDate(long ticks) => ticks > 0 && ticks <= DateTime.MaxValue.Ticks
        ? new DateTime(ticks, DateTimeKind.Utc).ToString("yyyy-MM-dd HH:mm 'UTC'", CultureInfo.InvariantCulture) : "—";
    private static string ShopAmount(long amount) => (amount / 10000m).ToString("N0", ShopCulture);
    private static string ShopPrice(string currency, long amount) => currency switch
    {
        "cash" => "$" + (amount / 100m).ToString("N2", ShopCulture) + " USD (demo)",
        "gold" => ShopAmount(amount) + " de oro",
        "silver" => ShopAmount(amount) + " de plata",
        _ => amount.ToString(ShopCulture) + " " + currency,
    };
    private static string ShopCategory(string category) => category.ToLowerInvariant() switch
    {
        "gold" => "Oro", "premium" => "Premium", "learningpoints" => "Puntos de aprendizaje", "vanity" or "cosmetic" or "cosmetics" => "Apariencias",
        "mounts" => "Monturas", "bundle" or "bundles" => "Paquetes", _ => category,
    };
    private static string ShopStatus(string status) => status switch
    {
        "pending" => "Pendiente de confirmación", "completed" => "Completado", "cancelled" => "Cancelado", "failed" => "No completado", _ => status,
    };
    private static WebResponse ShopError(ShopSession? s, int status, string message) => ShopPage("No se pudo continuar", Note(message, true)
        + "<p><a href=\"/shop\">Volver a la tienda</a></p>", s, status);

    private static WebResponse ShopPage(string title, string body, ShopSession? s, int status = 200, string head = "")
    {
        var nav = "<a href=\"/shop\">Tienda</a><a href=\"/shop/history\">Mis pedidos</a>";
        nav += s is null ? "<a href=\"/shop/login\">Entrar</a>" : ShopForm(s, "/shop/logout", "Salir de " + s.Name, buttonClass: "link");
        return Page(title, "<div class=shop>" + body + "<footer class=shop-disclosure>Simulación local</footer></div>", null,
            status, head + $"<style>{ShopCss}</style>", nav, "es");
    }

    private const string ShopCss = """
        .shop{--shop-gold:#b88a39;--shop-ink:#223b4c}.shop h1{font:600 clamp(1.8rem,4vw,2.8rem)/1.15 Georgia,serif;margin:0 0 16px}.shop h2{font-size:1.35rem}.shop p{max-width:70ch}
        .shop-art{display:grid;place-items:center;aspect-ratio:4/3;max-height:320px;overflow:hidden;margin:0 0 20px;border-radius:6px;background:radial-gradient(ellipse at 50% 75%,#4c443b,#20242a 70%)}.shop-art img{width:100%;height:100%;object-fit:contain}.shop-detail .shop-art,.shop-checkout .shop-art{max-width:460px;margin:0 auto 24px}
        .shop-heading{display:flex;align-items:center;justify-content:space-between;gap:24px;padding:24px 0 12px;border-bottom:2px solid var(--shop-gold);margin-bottom:20px}.shop-wallet{flex-shrink:0;color:var(--muted);margin:0}.shop-wallet strong{display:block;color:var(--fg);font-size:1.7rem;font-variant-numeric:tabular-nums}
        .shop .shop-character{padding:8px 16px}.shop-character label,.shop-filters label{display:flex;align-items:center;gap:12px}.shop .shop-filters{display:flex;gap:14px;margin-bottom:24px;align-items:end}.shop-filters label{display:grid;gap:5px}.shop-filters input{width:270px}
        .shop-products{display:grid;grid-template-columns:repeat(auto-fit,minmax(min(100%,270px),1fr));gap:20px}.shop-product{display:flex;flex-direction:column;margin:0;border-top:3px solid var(--shop-gold);padding:20px}.shop-category{color:var(--muted);margin:0 0 10px}.shop-benefits{padding-left:20px;margin:8px 0 24px}.shop-benefits:empty{display:none}.shop-benefits li+li{margin-top:6px}.shop-product-footer{margin-top:auto;display:flex;flex-wrap:wrap;align-items:center;justify-content:space-between;gap:12px;padding-top:16px;border-top:1px solid var(--line)}
        .shop-link{display:inline-block;padding:7px 12px;border:1px solid var(--accent);border-radius:6px;text-decoration:none}.shop-detail,.shop-checkout{max-width:700px;padding:24px}.shop-price{font-size:1.5rem;font-weight:600}.shop-login{max-width:600px;margin:32px auto;padding:28px}.shop .shop-consent{display:flex;gap:10px;align-items:flex-start;margin:14px 0}.shop-consent input{margin-top:5px}.shop-status{color:var(--muted)}.shop .status-completed{color:#267349}.shop .status-failed{color:var(--bad)}
        .shop-pagination{display:flex;align-items:center;justify-content:center;gap:16px;margin:24px 0}.shop-disclosure{color:var(--muted);font-size:.85rem;margin-top:24px;padding-top:12px;border-top:1px solid var(--line)}.shop a:focus-visible,.shop button:focus-visible,.shop input:focus-visible,.shop select:focus-visible{outline:3px solid var(--shop-gold);outline-offset:3px}.shop-processing{text-align:center;padding:48px 24px}.shop-processing p{margin:16px auto}.shop-spinner{display:inline-block;width:36px;height:36px;border:3px solid var(--line);border-top-color:var(--shop-gold);border-radius:50%;animation:shop-spin 1s linear infinite;margin-bottom:20px}@keyframes shop-spin{to{transform:rotate(360deg)}}
        .shop-sr-only{position:absolute;width:1px;height:1px;padding:0;margin:-1px;overflow:hidden;clip:rect(0,0,0,0);white-space:nowrap;border:0}@media(prefers-reduced-motion:reduce){.shop-spinner{animation:none}}@media(max-width:580px){.shop-heading{align-items:flex-start;flex-direction:column;gap:4px}.shop-filters label,.shop-filters input{width:100%}.shop-character label{flex-wrap:wrap}.shop-checkout,.shop-detail,.shop-login{padding:18px}.shop-checkout td{overflow-wrap:anywhere}}
        """;
}
