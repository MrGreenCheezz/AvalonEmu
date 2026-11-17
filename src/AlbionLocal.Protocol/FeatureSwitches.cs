namespace AlbionLocal.Server.Protocol;

/// <summary>
/// Feature switch table sent in the Login response (ahw.bfv, key 99). The client stores the
/// array as is in dew.a (dew.a(Boolean[],...) 0x180ae8020) and dew.a(Features) indexes it
/// without a length check, so it must hold one entry per Albion.Common.FeatureSwitches.Features
/// value or the client throws IndexOutOfRange.
/// </summary>
public static class FeatureSwitches
{
    /// <summary>Features.NumEntries; dew..cctor allocates bool[0xE3].</summary>
    public const int Count = 227;

    /// <summary>
    /// Features.DynamicTemplates: when on (the client default), the client rolls the dynamic templates of a cluster's dyn
    /// slots itself when it loads the cluster (ciy.ar 0x180971800 → bal.a; stage 4b SPEC §4.3), and so must the server.
    /// </summary>
    public const int DynamicTemplates = 112;

    /// <summary>
    /// Features.PartyFinder: off by default in the client (cdg.a), but on in the official table (FeatureSwitchInfo event, key 0,
    /// bool[227], [171] = true in captures 20261015_113433, 20261015_114814 and 20261016_002835). Off, PartyFinderGui.OnShow
    /// (0x180f6ba40) shows "@GUILDFINDER_FEATURE_DISABLED" instead of the window and f8n prints the same for every finder error.
    /// </summary>
    public const int PartyFinder = 171;

    /// <summary>
    /// Features the client turns off by default (cdg.a(Features, bool) 0x180885580, which
    /// dew..cctor uses to fill the table before any server data arrives). All others default on.
    /// </summary>
    private static readonly int[] OffByDefault =
    [
        16,  // VfxLog
        19,  // CrossPlatformRewards
        25,  // GuestAccountUseGoldmarket
        26,  // GuestAccountPlayerToPlayerTrade
        56,  // MistsMatchmakingAllianceCheck
        70,  // MistsGroupModeMatchmakingIncludeReservedSpaceForSoftCap
        87,  // MobileNotificationTokenCollection
        101, // AlbionShopFakeKickstartPacks
        103, // ClusterActivityChestPerDropLogging
        105, // MobileUiUnloadOnLowMem
        117, // EstimatedMarketValueFreeze
        132, // HellDungeonsGroupModeMatchmakingIncludeReservedSpaceForSoftCap
        135, // HellDungeonsMatchmakingAllianceCheck
        151, // DestinyBoardShowBuyLPButtonAndCTA
        155, // HellDungeonUseLongBlocker
        156, // ActivityHudNotificationBadge
        161, // ShowWebShopLinksInEU
        171, // PartyFinder
        180, // GamepadRebinding
        192, // DragonAreaGroupModeMatchmakingIncludeReservedSpaceForSoftCap
        194, // DragonAreaMatchmakingAllianceCheck
        200, // UseMaxVfxLifeTime
        202, // ConstrainableObjectLog
        203, // LoreSnippetIgnoreHiddenUntil
        207, // GuestAccountPurchaseNative
        208, // GuestAccountGlobalChat
        210, // GuestAccountIslandTravel
        212, // BlackMarketEstimatedMarketValueFreeze
        219, // MobileIAPSubscriptionCrossgradingAndroidEnabled
        220, // MobileIAPSubscriptionCrossgradingiOSEnabled
        223, // SendReliableMobMoveBeforeCastStart
    ];

    /// <summary>A fresh copy of the client's default table.</summary>
    public static bool[] Defaults()
    {
        var flags = Enumerable.Repeat(true, Count).ToArray();
        foreach (var i in OffByDefault) flags[i] = false;
        return flags;
    }
}
