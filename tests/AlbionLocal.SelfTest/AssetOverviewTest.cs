using System.Text.Json;
using AlbionLocal.Server.Game;
using AlbionLocal.Server.Network;
using AlbionLocal.Server.Protocol;

namespace AlbionLocal.SelfTest;

/// <summary>StageR assets (analysis/work/stageR/assets/SPEC.md): the Asset Overview window over the character's real bank vaults.</summary>
public static class AssetOverviewTest
{
    public static IEnumerable<(string, Action)> All =>
    [
        ("asset overview: ore in Thetford's vault and a sword in Bridgewatch's: 520 lists both cities with counts, 521 the default tab, 522 the items in their slots, 524-526 find the ore only; nothing moved", Overview),
    ];

    private static void Check(bool cond, string what) => AlbionTests.Check(cond, what);

    private static Guid[] Guids(object? o) => o is byte[] b ? [.. b.Chunk(16).Select(c => new Guid(c))] : [];

    private static bool Eq<T>(object? o, params T[] want) => o is T[] a && a.SequenceEqual(want);

    private static Guid G(object? o) => o is byte[] { Length: 16 } b ? new Guid(b) : Guid.Empty;

    private static void Overview()
    {
        using var z = new QolTest.Zone();
        var a = z.A.S.Player!;
        var tab = WorldSession.DefaultTab;
        var ore = z.World.Items.Find("T4_ORE")!;
        var sword = z.World.Items.Find("T4_MAIN_SWORD")!;
        string thetford = Character.VaultKey("0000", tab), bridgewatch = Character.VaultKey("2000", tab);
        lock (z.World.Sync)
        {
            a.Character.Vaults[thetford] = [null, null, null, new InventoryItem("T4_ORE", 10)];
            a.Character.Vaults[bridgewatch] = [.. Enumerable.Repeat<InventoryItem?>(null, 7), new InventoryItem("T4_MAIN_SWORD") { Quality = 3 }];
        }
        var before = JsonSerializer.Serialize(a.Character.Vaults);

        var list = z.Send(z.A, new AssetOverviewRequest());
        var ids = Guids(list?.Parameters.GetValueOrDefault((byte)1));
        Check(list is { ReturnCode: 0 } && ids.Length == 2 && Eq<string>(list.Parameters[2], "0000", "2000") && Eq<int>(list.Parameters[4], 1, 1)
              && list.Parameters[5] is long[] { Length: 2 } values && values[0] == z.World.ItemValues.Of(ore) * 10 * 10_000,
              "AssetOverview 520: two locations, clusters 0000 (Thetford) and 2000 (Bridgewatch), one stack each, the ore's value × 10");

        var tabs = z.Send(z.A, new AssetOverviewTabsRequest { ci = ids[1] });
        var swordTab = WorldSession.VaultContainerId(a.Character.Id, bridgewatch);
        Check(tabs is { ReturnCode: 0 } && G(tabs.Parameters[0]) == ids[1] && Guids(tabs.Parameters[1]) is [var t] && t == swordTab
              && Eq<string>(tabs.Parameters[2], "@BUILDINGS_T1_BANK") && Eq<string>(tabs.Parameters[3], "icon_tag_chest") && Eq<int>(tabs.Parameters[5], 1),
              "AssetOverviewTabs 521: Bridgewatch's default tab (its vault's container Guid, @BUILDINGS_T1_BANK, icon_tag_chest, 1 item)");

        var content = z.Send(z.A, new AssetOverviewTabContentRequest { ch = swordTab });
        Check(content is { ReturnCode: 0 } && G(content.Parameters[0]) == swordTab && content.Parameters[2] is int[] { Length: 1 } types && types[0] == sword.Index
              && Eq<short>(content.Parameters[3], 7) && Eq<short>(content.Parameters[4], 1) && Eq<short>(content.Parameters[7], 3),
              "AssetOverviewTabContent 522: the sword in slot 7, count 1, quality 3");

        var search = z.Send(z.A, new AssetOverviewSearchRequest { b5 = [ore.Index] });
        var found = Guids(search?.Parameters.GetValueOrDefault((byte)1));
        Check(search is { ReturnCode: 0 } && found is [var where] && where == ids[0] && Eq<string>(search.Parameters[2], "0000") && Eq<int>(search.Parameters[4], 1),
              "AssetOverviewSearch 524 for the ore's type: only Thetford");
        var oreTab = WorldSession.VaultContainerId(a.Character.Id, thetford);
        var hits = z.Send(z.A, new AssetOverviewSearchTabsRequest { cg = ids[0] });
        var none = z.Send(z.A, new AssetOverviewSearchTabsRequest { cg = ids[1] });
        var cells = z.Send(z.A, new AssetOverviewSearchTabContentRequest { ce = oreTab });
        Check(hits is { ReturnCode: 0 } && Guids(hits.Parameters[1]) is [var h] && h == oreTab && Eq<int>(hits.Parameters[7], 1)
              && none is { ReturnCode: 0 } && Guids(none.Parameters.GetValueOrDefault((byte)1)).Length == 0
              && cells is { ReturnCode: 0 } && cells.Parameters[2] is int[] { Length: 1 } oreTypes && oreTypes[0] == ore.Index && !cells.Parameters.ContainsKey(3)
              && Eq<short>(cells.Parameters[4], 10),
              "AssetOverviewSearchTabs 525: Thetford's tab with 1 match, Bridgewatch none; SearchTabContent 526: the 10 ore without slots");

        Check(z.Send(z.A, new AssetOverviewUnfreezeCacheRequest()) is null && JsonSerializer.Serialize(a.Character.Vaults) == before,
              "AssetOverviewUnfreezeCache 523: no answer; the vaults are as before (nothing moved)");
    }
}
