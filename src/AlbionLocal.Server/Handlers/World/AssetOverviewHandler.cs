using AlbionLocal.Photon;
using AlbionLocal.Server.Protocol;
using AlbionLocal.Server.Systems;

namespace AlbionLocal.Server.Network;

/// <summary>
/// The Asset Overview window (analysis/work/stageR/assets/SPEC.md; logic in Systems/AssetOverview.cs): the list of the character's bank
/// locations (520), a location's tabs (521) and a tab's items (522), the same three for a search (524-526), and the cache unfreeze (523).
/// Read-only: every answer is built from the vaults as they are, nothing is moved or copied. The recover operations (527, and 31/32/413 of
/// the bank's recovery vaults) stay unanswered: no recovery vault exists on this server (SPEC "Not done").
/// </summary>
public sealed partial class WorldSession
{
    /// <summary>The filter of this session's last AssetOverviewSearch, for its SearchTabs/SearchTabContent (null = none yet).</summary>
    private AssetOverview.Search? _assetSearch;

    /// <summary>AssetOverview (op 520, pa: no keys; gp4.mk 0x180CEE200 when the window opens): adg, the location list (AssetOverview.List).</summary>
    [Handles(OperationCodes.AssetOverview, Status = SessionStatus.InWorld)]
    private AlbionResponse? AssetOverviewList(AssetOverviewRequest req)
    {
        if (Player is not { } player) return null;
        var l = AssetOverview.List(world, AssetOverview.Locations(world, player), null);
        return new AssetOverviewResponse { alm = l.At, aln = l.Ids, alo = l.Clusters, alp = l.Kinds, alq = l.Counts, alr = l.Values, als = l.Names, alt = l.Positions };
    }

    /// <summary>
    /// AssetOverviewSearch (op 524, pc: 0 int[] the item type indices the client's filter matched, 1-4 the category strings, 5 short quality,
    /// 6 tier, 7 enchantment; gp4.mq 0x180CEE420 while a filter is set): adh, the locations with a match and the matching counts and values.
    /// The filter is kept for the search's tabs and contents. Key 0 already holds the text, category, tier and enchantment filters (gp1.mk
    /// 0x180CEB890), so the server matches by it and by the quality [assume: exact quality].
    /// </summary>
    [Handles(OperationCodes.AssetOverviewSearch, Status = SessionStatus.InWorld)]
    private AlbionResponse? AssetOverviewSearch(AssetOverviewSearchRequest req)
    {
        if (Player is not { } player) return null;
        _assetSearch = new AssetOverview.Search(req.b5 is null ? null : [.. req.b5], req.ca);
        var l = AssetOverview.List(world, AssetOverview.Locations(world, player), _assetSearch);
        return new AssetOverviewSearchResponse { alm = l.At, aln = l.Ids, alo = l.Clusters, alp = l.Kinds, alq = l.Counts, alr = l.Values, als = l.Names, alt = l.Positions };
    }

    /// <summary>
    /// AssetOverviewTabs (op 521, pg: 0 the location's Guid; gp8.a38 0x180CEF310): adn {0 the location (the client drops an answer for another,
    /// gpv.l4 0x180CE9590), 1 tab Guids, 2 names, 3 icons, 4 colours (RGBA int), 5 item counts [assume], 6 bbr}. An unknown location: empty.
    /// </summary>
    [Handles(OperationCodes.AssetOverviewTabs, Status = SessionStatus.InWorld)]
    private AlbionResponse? AssetOverviewTabs(AssetOverviewTabsRequest req)
    {
        if (Player is not { } player) return null;
        var tabs = AssetOverview.Locations(world, player).FirstOrDefault(l => l.Id == req.ci)?.Tabs ?? [];
        var (name, icon, color, type) = AssetOverview.TabDisplay(world);
        return new AssetOverviewTabsResponse
        {
            alx = req.ci, aly = [.. tabs.Select(t => t.Id)], alz = [.. tabs.Select(_ => name)], al0 = [.. tabs.Select(_ => icon)],
            al1 = [.. tabs.Select(_ => color)], al2 = [.. tabs.Select(t => t.Items.Count)], al3 = [.. tabs.Select(_ => type)],
        };
    }

    /// <summary>
    /// AssetOverviewSearchTabs (op 525, pe: 0 the location's Guid): adj as 521 for the tabs with a match of the last search, 7 the number of
    /// matches per tab (the client lays out that many cells, gpz.a 0x180CF0E20), 8 false (no recovery info).
    /// </summary>
    [Handles(OperationCodes.AssetOverviewSearchTabs, Status = SessionStatus.InWorld)]
    private AlbionResponse? AssetOverviewSearchTabs(AssetOverviewSearchTabsRequest req)
    {
        if (Player is not { } player) return null;
        var search = _assetSearch ?? new AssetOverview.Search(null, 0);
        var tabs = (AssetOverview.Locations(world, player).FirstOrDefault(l => l.Id == req.cg)?.Tabs ?? [])
            .Select(t => (t, n: AssetOverview.Matching(world, t, search).Count)).Where(e => e.n > 0).ToList();
        var (name, icon, color, type) = AssetOverview.TabDisplay(world);
        return new AssetOverviewSearchTabsResponse
        {
            alx = req.cg, aly = [.. tabs.Select(e => e.t.Id)], alz = [.. tabs.Select(_ => name)], al0 = [.. tabs.Select(_ => icon)],
            al1 = [.. tabs.Select(_ => color)], al2 = [.. tabs.Select(e => e.t.Items.Count)], al3 = [.. tabs.Select(_ => type)],
            alv = [.. tabs.Select(e => e.n)], alw = [.. tabs.Select(_ => false)],
        };
    }

    /// <summary>AssetOverviewTabContent (op 522, pf: 0 the tab's Guid; gpy.a4b 0x180CF0CB0): adm, the tab's items in their slots
    /// (AssetOverview.Content). An unknown tab: only the Guid and the bbr (no items).</summary>
    [Handles(OperationCodes.AssetOverviewTabContent, Status = SessionStatus.InWorld)]
    private AlbionResponse? AssetOverviewTabContent(AssetOverviewTabContentRequest req)
    {
        if (Player is not { } player) return null;
        var r = TabRows(player, req.ch, null);
        return new AssetOverviewTabContentResponse
        {
            al4 = req.ch, al5 = r?.Type ?? AssetOverview.PlayerBankTab, al6 = r?.Types ?? [], al7 = r?.Slots ?? [], al8 = r?.Counts ?? [], al9 = r?.Durability ?? [],
            ama = r?.Fame ?? [], amb = r?.Quality ?? [], amc = r?.Crafters ?? [], amd = r?.Active ?? [], ame = r?.Passive ?? [], amf = r?.Legendary ?? [],
        };
    }

    /// <summary>AssetOverviewSearchTabContent (op 526, pd: 0 the tab's Guid; gp9.a4b 0x180CEF9E0): adi, the tab's items that the last search
    /// matches, in order without slots (key 3 unsent: the client fills its first cells, which its row count from 525 key 7 shows).</summary>
    [Handles(OperationCodes.AssetOverviewSearchTabContent, Status = SessionStatus.InWorld)]
    private AlbionResponse? AssetOverviewSearchTabContent(AssetOverviewSearchTabContentRequest req)
    {
        if (Player is not { } player) return null;
        var r = TabRows(player, req.ce, _assetSearch ?? new AssetOverview.Search(null, 0));
        return new AssetOverviewSearchTabContentResponse
        {
            al4 = req.ce, al5 = r?.Type ?? AssetOverview.PlayerBankTab, al6 = r?.Types ?? [], al8 = r?.Counts ?? [], al9 = r?.Durability ?? [],
            ama = r?.Fame ?? [], amb = r?.Quality ?? [], amc = r?.Crafters ?? [], amd = r?.Active ?? [], ame = r?.Passive ?? [], amf = r?.Legendary ?? [],
        };
    }

    /// <summary>The content rows of the tab <paramref name="tab"/> of the player's vaults, null when it has none.</summary>
    private AssetOverview.Rows? TabRows(Entities.Player player, Guid tab, AssetOverview.Search? search) =>
        AssetOverview.Locations(world, player).SelectMany(l => l.Tabs).FirstOrDefault(t => t.Id == tab) is { } t ? AssetOverview.Content(world, t, search) : null;

    /// <summary>
    /// AssetOverviewUnfreezeCache (op 523, ph: no keys, no response class): the official server keeps a snapshot of the overview (adf key 0
    /// is its time) and this lets it go. This server builds every answer fresh from the vaults, so there is nothing to release; no answer.
    /// </summary>
    [Handles(OperationCodes.AssetOverviewUnfreezeCache, Status = SessionStatus.InWorld)]
    private AlbionResponse? AssetOverviewUnfreezeCache(AssetOverviewUnfreezeCacheRequest req) => null;
}
