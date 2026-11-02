using AlbionLocal.Server.Protocol;

namespace AlbionLocal.Server.Network;

public sealed partial class LoginSession
{
    private int? _shopNavigationCategory;
    [Handles(OperationCodes.GetShopPurchaseUrl, Status = SessionStatus.Authed)]
    private AlbionResponse GetShopPurchaseUrl(GetShopPurchaseUrlRequest req) => World is { } world
        ? NativeShopProtocol.PurchaseUrl(world, _account.Id, world.Accounts.Selected(_account.Id), req.oy)
        : new GetShopPurchaseUrlResponse { ReturnCode = 1, a4j = "" };

    [Handles(OperationCodes.GetShopTilesForCategory, Status = SessionStatus.Authed)]
    private AlbionResponse GetShopTilesForCategory(GetShopTilesForCategoryRequest req)
    {
        var tiles = World is { } world ? NativeShopCatalogue.Tiles(world, _account.Id, req.o0) : [];
        // ShopUpdate invalidates the native tile cache and may cause a repeated request. Publish a
        // navigation context only once per transition, or that refresh becomes a request loop.
        if (World is { } navigationWorld && _shopNavigationCategory != req.o0)
        {
            _shopNavigationCategory = req.o0;
            foreach (var update in NativeShopCatalogue.NavigationUpdates(navigationWorld, _account.Id, req.o0)) SendEvent(update);
        }
        foreach (var tile in tiles) SendEvent(tile);
        return NativeShopCatalogue.Response(req.o0, tiles);
    }

    protected override void AfterResponse(OperationCodes code, AlbionResponse response)
    {
        if (code == OperationCodes.Login && response.ReturnCode == 0 && World is { } world && !string.IsNullOrEmpty(world.Shop.BaseUrl))
            foreach (var message in NativeShopCatalogue.Initial(world, _account.Id)) SendEvent(message);
        AfterEvaluationResponse(code, response);
    }

    partial void AfterEvaluationResponse(OperationCodes code, AlbionResponse response);

    [Handles(OperationCodes.AccountSubscriptionInfo, Status = SessionStatus.Authed)]
    private AlbionResponse AccountSubscriptionInfo(AccountSubscriptionInfoRequest req) => World is { } world
        ? NativeShopProtocol.Subscriptions(world, _account.Id)
        : new AccountSubscriptionInfoResponse
        {
            ajg = characters.List(_account.Id).Select(c => c.Id).ToArray(),
            ajh = new bool[characters.List(_account.Id).Count], aji = new int[characters.List(_account.Id).Count],
        };

    [Handles(OperationCodes.CharacterSubscriptionInfo, Status = SessionStatus.Authed)]
    private AlbionResponse CharacterSubscriptionInfo(CharacterSubscriptionInfoRequest req) => NativeShopProtocol.NoSubscription();

    [Handles(OperationCodes.GetVanityUnlocks, Status = SessionStatus.Authed)]
    private AlbionResponse GetVanityUnlocks(GetVanityUnlocksRequest req)
    {
        var owned = World is { } world ? new HashSet<short>(world.Shop.Owned(_account.Id)) : [];
        foreach (var c in characters.List(_account.Id)) owned.UnionWith(c.Vanity?.Unlocks ?? []);
        return new GetVanityUnlocksResponse { a49 = owned.Order().ToArray() };
    }

    [Handles(OperationCodes.GetVanityConsumables, Status = SessionStatus.Authed)]
    private AlbionResponse GetVanityConsumables(GetVanityConsumablesRequest req)
    {
        if (World is not { } world) return new GetVanityConsumablesResponse { bni = [], bnj = [] };
        var owned = world.Shop.Consumables(_account.Id);
        return new GetVanityConsumablesResponse { bni = owned.Ids, bnj = owned.Charges };
    }
}
