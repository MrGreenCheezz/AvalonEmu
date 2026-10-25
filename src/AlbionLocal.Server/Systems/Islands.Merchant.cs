using AlbionLocal.Photon;
using AlbionLocal.Server.Data;
using AlbionLocal.Server.Entities;
using AlbionLocal.Server.Game;
using AlbionLocal.Server.Protocol;

namespace AlbionLocal.Server.Systems;

/// <summary>
/// Phase 2 of the islands SPEC (PVE-DESIGN step 14): buying a personal island at the island merchant (BuyMyIsland 219, §2.2), its upgrades
/// (UpgradeMyIsland 221, §2.3), the showroom trip (TravelIslandShowroom 533, §2.4) and the favourite / hidden flags (ChangePlayerIslandData
/// 389, §2.5). The prices are worldsettings.xml's (§0.1), the client's own price taken by the stage 6 rule (<see cref="Pricing.Accepts"/>).
/// </summary>
public sealed partial class Islands
{
    /// <summary>The highest level of <paramref name="owner"/>'s personal islands of any city; -1 without one (the client's GetHighestIslandLevel(ctp, 0, 0)).</summary>
    public int HighestLevel(Guid owner) =>
        Of(owner).Where(i => Data?.TypeAt(i.Type)?.Kind == 0).Select(i => i.Level).DefaultIfEmpty(-1).Max();

    /// <summary>
    /// The silver (whole units) of reaching <paramref name="level"/> of <paramref name="type"/> for <paramref name="owner"/> (§0.1, bgz.a.e): its
    /// firstsilverprice while none of the owner's personal islands has that level yet (level 0: while it owns none: 20,000 for the first
    /// island, 1,000,000 for every further one), else silverprice; 0 with Islands:FreeIslands; null when the type has no such level.
    /// </summary>
    public long? PriceOf(Guid owner, IslandType type, int level)
    {
        if (type.LevelAt(level) is not { } l) return null;
        if (world.Config.IslandsFreeIslands) return 0;
        return HighestLevel(owner) < level ? l.FirstSilverPrice : l.SilverPrice;
    }

    /// <summary>
    /// BuyMyIsland (§2.2): a personal island of the city of <paramref name="ap"/> (the merchant in view, "&lt;its id&gt;@&lt;city&gt;" its key 1),
    /// at level 0, for the client's price <paramref name="clientPrice"/> (a7m) when it fits the expected one (charged as it is; nothing with
    /// Islands:FreeIslands). Refused (null, the reason): islands off, no personal type here, one owned already, too few premium days
    /// (<see cref="Premium.Days"/> below the type's premiumrequirement, the client's own check), the price, the silver.
    /// </summary>
    public Island? Buy(Player p, IslandAccessPoint ap, string? apText, long clientPrice, out string why)
    {
        why = "";
        if (!Enabled) why = "islands are off (Islands:Enabled)";
        else if (p.Map is not { } map || ap.Map != map || !p.Visibility.Knows(ap)) why = $"island merchant {ap.Id} is not in view";
        else if (Data?.PersonalOf(map.Cluster) is not { } type) why = $"{map.Cluster} has no personal island type";
        else if (apText != $"{ap.Spawn.Guid:D}@{map.Cluster}") why = $"access point '{apText}' is not the merchant's {ap.Spawn.Guid:D}@{map.Cluster}";
        else if (Of(p.Character.Id).Any(i => i.Type == type.Id)) why = $"'{p.Character.Name}' owns a {type.Name} already";
        else if (Premium.Days(p) < type.PremiumRequirement) why = $"premium days {Premium.Days(p)} < {type.PremiumRequirement} (Islands:PremiumDays, premium or Islands:BuyWithoutPremium)";
        else if (AccessPointOf(type) is null) why = $"no island access point of {type.City} known";
        else if (PriceOf(p.Character.Id, type, 0) is not { } price) why = $"{type.Name} has no level 0";
        else
        {
            var charge = world.Config.IslandsFreeIslands ? 0 : clientPrice;
            if (!world.Config.IslandsFreeIslands && !Pricing.Accepts(clientPrice, price * Pricing.A7m)) why = $"the client's price {clientPrice / Pricing.A7m} is not {price}";
            else if (!Wallet.TrySpend(p, charge, $"island {type.Name}")) why = $"{p.Character.Silver / Pricing.A7m} silver, the island costs {charge / Pricing.A7m}";
            else if (Give(p.Character, type, 0, out why) is { } island)
            {
                world.Saves.SaveNow(p.Character); // the island is stored at once: so is the silver it took
                return island;
            }
            else Wallet.Credit(p, charge, "island not given");
        }
        return null;
    }

    /// <summary>
    /// UpgradeMyIsland (§2.3): <paramref name="id"/>, the player's, not upgrading, below its type's top level, to the next level for the client's
    /// price (as <see cref="Buy"/>). Then the level and its change time are set now (the client does the same on RC 0 and shows "upgrading"
    /// for 130 s, journeys onto it are refused with 8046 meanwhile) and saved; players on it go home with @ISLAND_KICKED_BECAUSE_UPGRADING
    /// and its map is dropped, so the next visit loads the new template.
    /// </summary>
    public bool Upgrade(Player p, Guid id, long clientPrice, out string why)
    {
        why = "";
        var now = world.GameNow;
        if (!Enabled) why = "islands are off (Islands:Enabled)";
        else if (Find(id) is not { } island || island.Owner != p.Character.Id) why = $"no island {id} of '{p.Character.Name}'";
        else if (island.Upgrading(now)) why = $"{island} is being upgraded";
        else if (Data?.TypeAt(island.Type) is not { } type || PriceOf(p.Character.Id, type, island.Level + 1) is not { } price)
            why = $"{island} is at its top level";
        else if (Premium.Days(p) < type.PremiumRequirement) why = $"premium days {Premium.Days(p)} < {type.PremiumRequirement} (Islands:PremiumDays, premium or Islands:BuyWithoutPremium)";
        else
        {
            var charge = world.Config.IslandsFreeIslands ? 0 : clientPrice;
            if (!world.Config.IslandsFreeIslands && !Pricing.Accepts(clientPrice, price * Pricing.A7m)) why = $"the client's price {clientPrice / Pricing.A7m} is not {price}";
            else if (!Wallet.TrySpend(p, charge, $"upgrade of {island}")) why = $"{p.Character.Silver / Pricing.A7m} silver, the upgrade costs {charge / Pricing.A7m}";
            else
            {
                Evict(island, "upgrade", "@ISLAND_KICKED_BECAUSE_UPGRADING");
                island.Level++;
                island.LevelChanged = now;
                store?.SaveIsland(island);
                world.Saves.SaveNow(p.Character); // as Buy
                Log.Info("world", $"'{p.Character.Name}' upgrades {island}");
                return true;
            }
        }
        return false;
    }

    /// <summary>ChangePlayerIslandData (§2.5): the favourite and hidden flags of the player's island (GetIslandInfos keys 21/22); false when it is none of theirs.</summary>
    public bool SetFlags(Player p, Guid id, bool favorite, bool hidden)
    {
        if (Find(id) is not { } island || island.Owner != p.Character.Id) return false;
        (island.Favorite, island.Hidden) = (favorite, hidden);
        store?.SaveIsland(island);
        return true;
    }

    /// <summary>The showroom cluster (world.xml, ISLAND-PLAYER-FOREST-0001e-SHOWROOM) of <paramref name="city"/>'s personal island type; null for Caerleon and the Mists.</summary>
    public string? ShowroomOf(string city) => Data?.PersonalOf(city)?.Showroom;

    /// <summary>The city whose island merchant leads to showroom <paramref name="cluster"/> (the way back, §2.4); null when it is no showroom.</summary>
    public string? CityOfShowroom(string cluster) =>
        Data?.Types.Where(t => t.Showroom == cluster).OrderBy(t => t.Kind).FirstOrDefault()?.City;

    /// <summary>The merchant's window opened (GetIslandInfos): its city's showroom is resolved in the background, so that a showroom trip finds it.</summary>
    private void PrefetchShowroom(Player p)
    {
        if (p.Map is { } map && !IslandData.IsInstance(map.Cluster) && ShowroomOf(map.Cluster) is { } showroom && world.CachedCluster(showroom) is null)
            world.PrefetchCluster(showroom);
    }
}
