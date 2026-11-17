namespace AlbionLocal.Server.Protocol;

/// <summary>
/// Feature switch table: the Login response's key 99 (ahw.bfv) and FeatureSwitchInfo (event 503, aou, key 0), one entry per
/// Albion.Common.FeatureSwitches.Features value. The client stores the array as is in dew.a (dew.a(Boolean[],...) 0x180ae8020,
/// from dew.a(ahw) 0x180ae8740 and dew.a(g7s,short) 0x180ae7f40) and dew.a(Features) 0x180ae7ec0 indexes it without a length
/// check, so it must hold exactly <see cref="Count"/> entries or the client throws IndexOutOfRange (analysis/work/stageR/switches/SPEC.md).
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
    /// Features.PartyFinder: off by default in the client (cdg.a), but on in the official table. Off, PartyFinderGui.OnShow
    /// (0x180f6ba40) shows "@GUILDFINDER_FEATURE_DISABLED" instead of the window and f8n prints the same for every finder error.
    /// </summary>
    public const int PartyFinder = 171;

    /// <summary>The Features names by index (the client enum Albion.Common.FeatureSwitches.Features of build 345319).</summary>
    public static readonly IReadOnlyList<string> Names =
    [
        /*   0 */ "MarketPlaces", "MarketPlaceCache", "Duels", "FestivityTransactions", "SmartClusterQueue", "ZergDebuff", "MassMailDelete",
        /*   7 */ "EstimatedMarketValue", "BlackMarketOptimizations", "XignCode", "Ace", "AceRiskKick", "BattlEye", "SpeedHackDetection",
        /*  14 */ "FactionWarfare", "PerformanceTracking", "VfxLog", "MarketingTracking", "SpellTracking", "CrossPlatformRewards",
        /*  20 */ "ClientPerformanceMonitoring", "StaticDungeonEvents", "LoadoutMarketplace", "GuildMightStatistics",
        /*  24 */ "ClusterValueOverview", "GuestAccountUseGoldmarket", "GuestAccountPlayerToPlayerTrade", "NewAchievementOverflow",
        /*  28 */ "AchievementOverflowAsync", "EventBasedPopups", "DidYouKnowFields", "LoadoutMarketplaceBuyAndEquip",
        /*  32 */ "VanityConsumableCharges", "KillEmotes", "ArenaBadBehaviourPunishment", "TownPortal", "ArenaRankedQueue",
        /*  37 */ "BatchSendingOfCastEvents", "BatchSendingOfHealthEvents", "SkipObsoleteCastSpellEvents", "SkipObsoleteCastHitEvents",
        /*  41 */ "UseSkillBookNewBatching", "OptionsLog", "DailyGuildActivityUpdate", "GuildFinder", "MovePacketStats",
        /*  46 */ "AutoBanBotPatterns", "RegisterForMists", "MistsStaticEntrance", "MistsCityRoadsEntrance", "MistsOpenWorldExit",
        /*  51 */ "MistsCityEntrance", "AchievementOverflowUseLp", "AchievementOverflowUseLpMission", "MistsMatchmakingLastPartyCheck",
        /*  55 */ "MistsMatchmakingPartyCheck", "MistsMatchmakingAllianceCheck", "MistsMatchmakingGuildCheck", "GuildAndAllianceAnonymity",
        /*  59 */ "PlayerAnonymity", "MobileKeepClustersLoaded", "EnableAlbionStore", "EnableGoldMarket", "CharacterMeshCacheLowThreshold",
        /*  64 */ "ClaimTransactions", "GuildFinderRecommendations", "GuildFinderNameSearch", "GuildFinderFilterSearch",
        /*  68 */ "RegisterForMistsDungeon", "FactionWarfareRedZoneEvent", "MistsGroupModeMatchmakingIncludeReservedSpaceForSoftCap",
        /*  71 */ "MistsDungeonExit", "MobilePrewarmShaders", "AlbionShopNewTileFetching", "QuestDirectionalIndicators",
        /*  75 */ "OpenWorldAttackTracking", "SelectableNameTags", "SpeedHackDetectionAutoBan", "BlackMarketBuffering",
        /*  79 */ "SpeedHackDetectionStrict", "QuickSell", "PendingAccountActions", "DavidsBotDetection", "DavidsBotDetectionAutoBan",
        /*  84 */ "BotWaypointDetection", "MovementWatermarkCheck", "MovementWatermarkCheckTutorial", "MobileNotificationTokenCollection",
        /*  88 */ "DavidsBotDetectionPlayerReports", "DynamicHunts", "AwakenedItemsCrafting", "AwakenedItemTraits",
        /*  92 */ "AwakenedItemProgression", "RaidTerritory", "MobileAchievements", "DailyMightBonus", "FortificationUpgrades",
        /*  97 */ "FortificationPointGeneration", "FortificationPlayerEjection", "RaiseSiegeBanner", "AlbionShopFakeFounderPacks",
        /* 101 */ "AlbionShopFakeKickstartPacks", "ClusterActivityChest", "ClusterActivityChestPerDropLogging", "DuelSetWager",
        /* 105 */ "MobileUiUnloadOnLowMem", "PlayerActivityTracking", "GlobalActivityQuotient", "AlbionJournal",
        /* 109 */ "AlbionJournalRewardClaiming", "SteamAchievementUnlocks", "PacketKey", "DynamicTemplates",
        /* 113 */ "OutlandsTeleportationCityPortal", "OutlandsTeleportationReturnPortal", "CustomHudLayouts", "DynamicMiniMapGen",
        /* 117 */ "EstimatedMarketValueFreeze", "GlobalTreasureEvents", "RegisterForResourceMists", "SmugglerCrates", "CagedSmugglers",
        /* 122 */ "AssetOverview", "AssetOverviewHideEmptyTabs", "AssetOverviewCacheRejectedRequests", "SmugglersNetwork",
        /* 126 */ "SmugglersNetworkPaging", "DropKillTrophies", "TakeOverOwnHalfConnectedSession", "ArmoryTracking", "ArmorySearch",
        /* 131 */ "RegisterForHellDungeons", "HellDungeonsGroupModeMatchmakingIncludeReservedSpaceForSoftCap",
        /* 133 */ "HellDungeonsMatchmakingLastPartyCheck", "HellDungeonsMatchmakingPartyCheck", "HellDungeonsMatchmakingAllianceCheck",
        /* 136 */ "HellDungeonsMatchmakingGuildCheck", "ArmoryStatistics", "ArmoryData", "HellDungeonSouls", "HellDungeonSoulShrine",
        /* 141 */ "CarriableKeyChests", "ResurrectionShrines", "HellDungeonStaticEntrance", "AsyncSpellVfx", "HellDungeonDownwardExit",
        /* 146 */ "PvpChallengeClaimReward", "PvpChallengeClaimRewardItems", "InventoryTryMoveBuggyItem", "ArmoryPerformanceCalculation",
        /* 150 */ "CreatorBattle", "DestinyBoardShowBuyLPButtonAndCTA", "ShowWebShopLinksOnIos",
        /* 153 */ "HellDungeonMatchmakingStartTimeOffsetActive", "LearningPointFameCompensationDorian", "HellDungeonUseLongBlocker",
        /* 156 */ "ActivityHudNotificationBadge", "FactionFortressPlayerEjection", "ShowWebShopLinks", "ShowWebShopLinksOnAndroid",
        /* 160 */ "ShowWebShopLinksInUS", "ShowWebShopLinksInEU", "FactionFortressWeaponCaches", "FactionPortals", "GamePass",
        /* 165 */ "FactionFortressRespawnLogic", "FactionFortressFightRewardHandout", "FactionFortressCutoffFightRewardHandout",
        /* 168 */ "FactionFortressClusterQueueHandling", "DuchySupplyWarClusterQueueHandling",
        /* 170 */ "FactionFortressRedZoneEventClusterQueueHandling", "PartyFinder", "FactionWarfareDefensiveBonus", "LeaveProtectionSystem",
        /* 174 */ "LeaveProtectionSystemParty", "LeaveProtectionSystemGuild", "LeaveProtectionSystemAlliance",
        /* 177 */ "ClientPriorityUpdateScheduler", "FactionWarfareRedZoneEventStateUpdateCache", "FactionBattleStandard",
        /* 180 */ "GamepadRebinding", "PlatformOnlyAccountUpgradePrompt", "LoreSnippets", "Arena1v1", "PacketLossPing", "TakeAllLooting",
        /* 186 */ "SimplePerformanceLog", "ProductStoreCategoryInfo", "SocialUiLocalPlayers", "DragonArea", "DragonAreaTeleporters",
        /* 191 */ "RegisterForDragonArea", "DragonAreaGroupModeMatchmakingIncludeReservedSpaceForSoftCap",
        /* 193 */ "DragonAreaMatchmakingPartyCheck", "DragonAreaMatchmakingAllianceCheck", "DragonAreaMatchmakingGuildCheck",
        /* 196 */ "SkipVfxOnPlayerDummies", "AsyncSpellVfxGround", "EnableContactSupportOnConsole", "GamepadScreenShakeVibration",
        /* 200 */ "UseMaxVfxLifeTime", "RevertNonLoopVfxToPrefabLifetime", "ConstrainableObjectLog", "LoreSnippetIgnoreHiddenUntil",
        /* 204 */ "AlbionJournalMissionTrackingCustomStartAndEndDates", "EMVLootSplitting", "DragonAttackEvent",
        /* 207 */ "GuestAccountPurchaseNative", "GuestAccountGlobalChat", "GuestAccountLocalChat", "GuestAccountIslandTravel",
        /* 211 */ "BlackMarketEstimatedMarketValue", "BlackMarketEstimatedMarketValueFreeze", "RegionQueueInfo", "QueuePriorityKickOthers",
        /* 215 */ "KnockdownRespawn", "InGameSurvey", "AgeAssuranceEnabledAndroid", "AgeAssuranceEnabledIos",
        /* 219 */ "MobileIAPSubscriptionCrossgradingAndroidEnabled", "MobileIAPSubscriptionCrossgradingiOSEnabled",
        /* 221 */ "ClientFrameSpikeLogging", "ReplicateMobCastingStateOnInterestEnter", "SendReliableMobMoveBeforeCastStart",
        /* 224 */ "ReplicateMobCastRotationForStandingStillCasts", "VfxAddonAttachmentFix", "XRayOccluders"
    ];

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

    /// <summary>
    /// The official table where it differs from the client defaults: FeatureSwitchInfo key 0 in all 14 fully decoded events of
    /// captures 20261015_113433, 20261015_114814 and 20261016_002835 (identical in every one, one after each Join).
    /// </summary>
    public static readonly IReadOnlyDictionary<int, bool> OfficialChanges = new Dictionary<int, bool>
    {
        [24] = false,  // ClusterValueOverview: the world map's "cluster value" / "silver value" overlays (WorldmapUI.CreateDropDown)
        [41] = false,  // UseSkillBookNewBatching: ItemDetailsView.IsChanneledFullStackUsage takes the older path
        [46] = false,  // AutoBanBotPatterns: no client reader (server side)
        [77] = false,  // SpeedHackDetectionAutoBan: no client reader (server side)
        [83] = false,  // DavidsBotDetectionAutoBan: no client reader (server side)
        [100] = false, // AlbionShopFakeFounderPacks: no client reader found (shop)
        [177] = false, // ClientPriorityUpdateScheduler: PriorityUpdateScheduler.Awake / OnFeatureSwitchChanged (client update scheduling)
        [217] = false, // AgeAssuranceEnabledAndroid: no client reader (mobile)
        [218] = false, // AgeAssuranceEnabledIos: no client reader (mobile)
        [19] = true,   // CrossPlatformRewards: no client reader (gamedata CrossPlatformRewards: a server-side reward mail)
        [25] = true,   // GuestAccountUseGoldmarket: GoldMarketGui.OnShow, guests only (Login key 86)
        [26] = true,   // GuestAccountPlayerToPlayerTrade: do2.m, guests only (Login key 86)
        [151] = true,  // DestinyBoardShowBuyLPButtonAndCTA: official on, but kept off here (see ServerDefaultOff)
        [171] = true,  // PartyFinder
    };

    /// <summary>
    /// Official on, but off on this server by default: [151] shows the destiny board's learning point "shop" button
    /// (LearningPointStripe.ShopButton → AchievementBoardGui.OnClickShopButton 0x18120ddf0) and the "Refill" call to action
    /// (DestinyBoardCTA state 2), which both open the Albion Store window (ProductStoreGui) on its learning point page. This
    /// server has no shop (project rule) and answers none of its requests.
    /// </summary>
    public static readonly IReadOnlyList<int> ServerDefaultOff = [151];

    /// <summary>
    /// FeatureSwitchInfo key 4 (dew.e), as the official server sends it in every event: VFX limit overrides per platform, read by
    /// VfxLimitManager.ApplyVfxLimitOverridesFromFeatureSwitch (0x18129d320): on PC the "SpellVfx" context limit -1 ([infer] no limit).
    /// </summary>
    public const string OfficialVfxLimits = """{"PC":{"LimitsByContext":{"SpellVfx":-1}}}""";

    /// <summary>A fresh copy of the client's default table.</summary>
    public static bool[] Defaults()
    {
        var flags = Enumerable.Repeat(true, Count).ToArray();
        foreach (var i in OffByDefault) flags[i] = false;
        return flags;
    }

    /// <summary>A fresh copy of the official table (the client defaults with <see cref="OfficialChanges"/>).</summary>
    public static bool[] Official()
    {
        var flags = Defaults();
        foreach (var (i, on) in OfficialChanges) flags[i] = on;
        return flags;
    }

    /// <summary>This server's default table: the official one with <see cref="ServerDefaultOff"/> off.</summary>
    public static bool[] ServerDefaults()
    {
        var flags = Official();
        foreach (var i in ServerDefaultOff) flags[i] = false;
        return flags;
    }

    /// <summary>The index of a switch named by its Features name (any case) or by its number 0..226; -1 when it is neither.</summary>
    public static int IndexOf(string nameOrIndex)
    {
        var s = nameOrIndex.Trim();
        if (int.TryParse(s, System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out var i))
            return i < Count ? i : -1;
        for (var n = 0; n < Count; n++)
            if (Names[n].Equals(s, StringComparison.OrdinalIgnoreCase)) return n;
        return -1;
    }
}
