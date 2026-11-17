using System.Numerics;
using System.Security.Cryptography;
using System.Text;
using AlbionLocal.Server.Data;
using AlbionLocal.Server.Entities;
using AlbionLocal.Server.Game;
using AlbionLocal.Server.Protocol;

namespace AlbionLocal.Server.Systems;

/// <summary>
/// The Asset Overview window (analysis/work/stageR/assets/SPEC.md): a read-only view over the bank vaults a character owns, one location
/// per vault location (a city's bank, a hideout's own bank, the shared vault under Bank:Shared), its tabs and their items. Built fresh
/// from <see cref="Character.Vaults"/> (and the vault tabs open right now) on every request, so nothing is cached and nothing moves.
/// <para>Client model (345319): the location list is adf/gps (gps ctor 0x180CE8C80), a location's tabs adk (gpv.l4 0x180CE9590, gp8.a39
/// 0x180CEF460), a tab's items adl (gpz.a 0x180CF1340). The client groups a location by its cluster (gp2.a 0x180CEC570: the seven cities by
/// their cluster ids, hideouts by kind 4, the rest Royal Continent / Outlands) and sums the location counts and values itself.</para>
/// </summary>
public static class AssetOverview
{
    /// <summary>cfi.b of a city bank vault [assume: 3 is the guild island player bank and 4 the hideout (gp2.a 0x180CEC1B0 maps them to
    /// RecoveryType 3/4), 5 is grouped under the Outlands (gp2.a 0x180CEC570); which of 0-2 the official server gives a city bank is not
    /// known, 1 is used].</summary>
    public const int CityBank = 1;

    /// <summary>cfi.b of a hideout's bank (gp2.a 0x180CEC570: kind 4 = the Hideouts group, label @ASSET_OVERVIEW_NAVIGATION_ENTRY_HIDEOUTS).</summary>
    public const int HideoutBank = 4;

    /// <summary>bbr of a player bank tab (cfi.l 0x1808A94D0: PBNK ↔ 4; 10 GVLT guild vault, 13/14 the recovery vaults).</summary>
    public const int PlayerBankTab = 4;

    /// <summary>The fallback cluster of a location that has none of its own (the shared vault, a gone hideout): Thetford [assume].</summary>
    public const string FallbackCluster = "0000";

    /// <summary>One vault tab: its key in <see cref="Character.Vaults"/>, its overview Guid (the vault's container Guid) and its items by slot.</summary>
    public sealed record Tab(string Key, Guid Id, Guid BankTab, IReadOnlyList<(int Slot, InventoryItem Item)> Items);

    /// <summary>One location (adf row): its Guid, cfi.b kind, cluster id (key 2), name (key 6), map position (key 7) and tabs.</summary>
    public sealed record Location(Guid Id, string Vault, int Kind, string Cluster, string Name, Vector2? Position, IReadOnlyList<Tab> Tabs)
    {
        public int Count => Tabs.Sum(t => t.Items.Count);
    }

    /// <summary>The filter of the last AssetOverviewSearch (op 524): pc key 0 the item type indices the client matched (text, categories,
    /// tier, enchantment: gp1.mk 0x180CEB890), key 5 the quality (0 = any).</summary>
    public sealed record Search(HashSet<int>? Types, int Quality)
    {
        public bool Matches(ItemTemplate type, InventoryItem item) =>
            (Types is null || Types.Contains(type.Index)) && (Quality <= 0 || Math.Max(item.Quality, 1) == Quality);
    }

    /// <summary>
    /// The locations of <paramref name="player"/>'s vaults that hold anything (empty ones are not listed [assume]), each tab read from the open
    /// vault when it is open now, else from the saved slots; items whose type this server does not know are left out.
    /// </summary>
    public static List<Location> Locations(World world, Player player)
    {
        var c = player.Character;
        var slots = new Dictionary<string, IReadOnlyList<InventoryItem?>>(StringComparer.Ordinal);
        foreach (var (key, items) in c.Vaults) slots[key] = items;
        foreach (var vault in player.OpenVaults.Values) slots[vault.Key] = Inventory.Records(vault.Container, id => world.Spells?.Name(id));
        var byLocation = new SortedDictionary<string, List<Tab>>(StringComparer.Ordinal);
        foreach (var (key, items) in slots)
        {
            var bar = key.LastIndexOf('|');
            if (bar <= 0 || !Guid.TryParse(key[(bar + 1)..], out var bankTab)) continue;
            if (key.StartsWith(Network.WorldSession.ChestPrefix, StringComparison.Ordinal)) continue; // stageR buildings: a furniture chest is no bank
            var listed = items.Select((item, slot) => (slot, item))
                .Where(e => e.item is { } i && world.Items.Find(i.Name) is not null).Select(e => (e.slot, e.item!)).ToList();
            if (listed.Count == 0) continue;
            var location = key[..bar];
            if (!byLocation.TryGetValue(location, out var tabs)) byLocation[location] = tabs = [];
            tabs.Add(new Tab(key, Network.WorldSession.VaultContainerId(c.Id, key), bankTab, listed));
        }
        return [.. byLocation.Select(kv => Describe(world, c, kv.Key, kv.Value))];
    }

    /// <summary>The adf row of the vault location <paramref name="vault"/>: a hideout's own vault (Hideouts:BankVault Own) is the hideout's
    /// zone and position, a gone hideout's the fallback cluster, the shared vault the character's last city, any other its cluster id.</summary>
    private static Location Describe(World world, Character c, string vault, List<Tab> tabs)
    {
        var id = NameGuid($"albionlocal:assets:{c.Id}:{vault}");
        tabs.Sort((a, b) => string.CompareOrdinal(a.Key, b.Key));
        if (HideoutData.IsInstance(vault))
        {
            if (world.Hideouts.Find(vault) is { } h)
                return new Location(id, vault, HideoutBank, h.Cluster, world.Hideouts.Info(h).GuildName, h.Position, tabs);
            return new Location(id, vault, HideoutBank, Known(world, c.LastCity) ?? FallbackCluster, "", null, tabs);
        }
        if (vault == Character.SharedVault)
            return new Location(id, vault, CityBank, Known(world, c.LastCity) ?? FallbackCluster, "", null, tabs);
        return new Location(id, vault, CityBank, vault, "", null, tabs);
    }

    /// <summary><paramref name="cluster"/> when it is set (and a world.xml cluster, when world.xml is loaded).</summary>
    private static string? Known(World world, string? cluster) =>
        string.IsNullOrEmpty(cluster) ? null : world.Clusters?.WorldIfLoaded is { } w && w.Find(cluster) is null ? null : cluster;

    /// <summary>A name-based (MD5, version 3) Guid, stable for the same text.</summary>
    public static Guid NameGuid(string text)
    {
        var hash = MD5.HashData(Encoding.UTF8.GetBytes(text));
        hash[6] = (byte)((hash[6] & 0x0F) | 0x30);
        hash[8] = (byte)((hash[8] & 0x3F) | 0x80);
        return new Guid(hash);
    }

    /// <summary>
    /// The estimated value of one stack, a7m raw [assume]: the item's itemvalue (Game.ItemValues, bdi.d) in silver × the count. This server
    /// has no estimated market value of its own (RequestEstimatedMarketValue is unanswered, content/opcode-stubs.json).
    /// </summary>
    public static long Value(World world, InventoryItem item) =>
        world.Items.Find(item.Name) is { } t ? (long)Math.Round(world.ItemValues.Of(t) * Pricing.A7m) * Math.Max(item.Count, 1) : 0;

    /// <summary>
    /// The location list (AssetOverview 520 / AssetOverviewSearch 524, adf): 0 the snapshot's GameTimeStamp, 1 Guids, 2 cluster ids, 3 cfi.b,
    /// 4 item counts, 5 values (a7m), 6 names, 7 positions. Key 8 (a second cluster per row, read as optional by gps) stays unsent. With
    /// <paramref name="search"/> only the matching items count, and only locations with a match are listed.
    /// </summary>
    public static (long At, Guid[] Ids, string[] Clusters, int[] Kinds, int[] Counts, long[] Values, string[] Names, Vector2[] Positions) List(
        World world, IReadOnlyList<Location> locations, Search? search)
    {
        var rows = locations.Select(l => (l, items: Items(world, l, search))).Where(r => r.items.Count > 0).ToList();
        return (world.GameNow, [.. rows.Select(r => r.l.Id)], [.. rows.Select(r => r.l.Cluster)], [.. rows.Select(r => r.l.Kind)],
            [.. rows.Select(r => r.items.Count)], [.. rows.Select(r => r.items.Sum(i => Value(world, i)))], [.. rows.Select(r => r.l.Name)],
            [.. rows.Select(r => r.l.Position ?? Vector2.Zero)]);
    }

    private static List<InventoryItem> Items(World world, Location l, Search? search) =>
        [.. l.Tabs.SelectMany(t => Matching(world, t, search)).Select(e => e.Item)];

    /// <summary>The items of <paramref name="tab"/> that <paramref name="search"/> matches (all without one).</summary>
    public static List<(int Slot, InventoryItem Item)> Matching(World world, Tab tab, Search? search) =>
        [.. tab.Items.Where(e => search is null || (world.Items.Find(e.Item.Name) is { } t && search.Matches(t, e.Item)))];

    /// <summary>The bank tab's display (adk keys 2-4, 6): CityBank DefaultTab's name, icon and colour for every tab (only the default tab
    /// exists here: tab purchase is not made), bbr 4 (player bank).</summary>
    public static (string Name, string Icon, int Color, int Type) TabDisplay(World world) =>
        (world.Economy.BankTabName, world.Economy.BankTabIcon, world.Economy.BankTabColor, PlayerBankTab);

    /// <summary>
    /// A tab's items as adl (AssetOverviewTabContent 522 / AssetOverviewSearchTabContent 526): 1 bbr, 2 type indices, 3 slots (null = in
    /// order: the search's list of matches, gpz.a 0x180CF1340 then puts them in the first cells), 4 counts, 5 durability (a7m), 6 a journal's
    /// fame (a7m) [infer: dn8 takes both a7m], 7 qualities, 8 crafter names (none kept: ""), 9/10 active/passive spell ids per slot (-1 none),
    /// 11 LegendaryItemType (a6i: 1 awakened), 12-14 the recovery info (none: 0, 0, null).
    /// </summary>
    public static Rows Content(World world, Tab tab, Search? search)
    {
        var items = Matching(world, tab, search);
        var types = new List<int>();
        var slots = new List<short>();
        var counts = new List<short>();
        var durability = new List<long>();
        var fame = new List<long>();
        var quality = new List<short>();
        var crafters = new List<string>();
        var active = new List<int[]>();
        var passive = new List<int[]>();
        var legendary = new List<byte>();
        foreach (var (slot, item) in items)
        {
            var t = world.Items.Find(item.Name)!;
            types.Add(t.Index);
            slots.Add((short)slot);
            counts.Add((short)Math.Clamp(item.Count, 1, short.MaxValue));
            durability.Add(t.IsEquipment ? item.Durability ?? t.FullDurabilityRaw : 0);
            fame.Add(t.IsJournal ? item.Fame ?? Item.DefaultFame(t) : 0);
            quality.Add((short)Math.Max(item.Quality, 1));
            crafters.Add("");
            active.Add(Spells(world, item.Active));
            passive.Add(Spells(world, item.Passive));
            legendary.Add((byte)(t.IsEquipment && item.Soul is not null ? 1 : 0));
        }
        return new Rows(tab.Id, PlayerBankTab, [.. types], search is null ? [.. slots] : null, [.. counts], [.. durability], [.. fame], [.. quality],
            [.. crafters], [.. active], [.. passive], [.. legendary]);
    }

    /// <summary>The adl keys 0-11 of a tab's content (<see cref="Content"/>).</summary>
    public sealed record Rows(Guid Tab, int Type, int[] Types, short[]? Slots, short[] Counts, long[] Durability, long[] Fame, short[] Quality,
        string[] Crafters, int[][] Active, int[][] Passive, byte[] Legendary);

    /// <summary>The spell ids of saved spell names (-1 for none or an unknown name); empty when none were saved (the client then shows no
    /// choice).</summary>
    private static int[] Spells(World world, string?[]? names) =>
        names is null || world.Spells is not { } spells ? [] : [.. names.Select(n => (int)spells.IdOf(n))];
}
