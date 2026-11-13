using AlbionLocal.Photon;
using AlbionLocal.Server.Data;
using AlbionLocal.Server.Entities;
using AlbionLocal.Server.Game;

namespace AlbionLocal.Server.Systems;

/// <summary>
/// The guild islands (public issue #20 follow-up): the island merchant's guild tab buys (BuyGuildIsland 220) and upgrades
/// (UpgradeGuildIsland 222) the island of the player's guild; the guild's members see it in GetIslandInfos and travel to it as to their own.
/// <list type="bullet">
/// <item>The request carries no price (the sender c32.j_21 0x180A76B20 fills px keys 0 the merchant and 1 "&lt;AP&gt;@&lt;city&gt;" only;
/// c32.i_81 0x180A751C0 fills ab9 keys 0, 1 and 2 = the new level): the server prices it from worldsettings.xml as the window does
/// (IslandMerchantGui.UpdateGuildIslandView 0x18104CC40: bgz.a.e(highest guild island level &lt; level), less the global discount bgf.abp).</item>
/// <item>It is paid from the guild account: the guild tab opens the pay dialog in mode 1 (gxd.ji 0x180D78300 sets gxc 1; the personal
/// tab's gxd.jj sets 0), and PaySilverDetailGui.OnPay 0x1812B13A0 checks mode 1 against the guild's silver (the local guild c2n +0xC0)
/// with @PAYMENT_NOT_ENOUGH_SILVER_GUILD, mode 0 against the player's (ctp +0x2C8) with …_PLAYER.</item>
/// <item>The right is ISLAND_MANAGEMENT (<see cref="GuildRoles.IslandManagement"/>, IslandMerchantGui.HasIslandManagementRights
/// 0x181048C20); premiumrequirement is 0 for every guild type.</item>
/// </list>
/// What a guild island does not do yet: building, farming, labourers and furniture there (the owner checks of those paths are a
/// character's), so its plots stay empty; the GUILD_ISLAND access rights of the island management (everyone of the guild may visit).
/// </summary>
public sealed partial class Islands
{
    /// <summary>The islands of guild <paramref name="guild"/>, oldest first.</summary>
    public IReadOnlyList<Island> OfGuild(Guid guild) => [.. _islands.Values.Where(i => i.Guild && i.Owner == guild).OrderBy(i => i.Bought)];

    /// <summary>The highest level of the guild's islands of any city; -1 without one (the client's GetHighestIslandLevel(ctp, 1) 0x181047E50 over ctp+0x460).</summary>
    public int GuildHighestLevel(Guid guild) => OfGuild(guild).Select(i => i.Level).DefaultIfEmpty(-1).Max();

    /// <summary>
    /// The silver (whole units) of <paramref name="level"/> of guild type <paramref name="type"/> for <paramref name="guild"/>: firstsilverprice
    /// while none of its islands has that level, else silverprice (the same for every guild type: level 0 5,000,000); 0 with Islands:FreeIslands.
    /// </summary>
    public long? GuildPriceOf(Guid guild, IslandType type, int level)
    {
        if (type.LevelAt(level) is not { } l) return null;
        if (world.Config.IslandsFreeIslands) return 0;
        return GuildHighestLevel(guild) < level ? l.FirstSilverPrice : l.SilverPrice;
    }

    /// <summary>What the guild account pays for <paramref name="silver"/>: in a7m, after the client's global discount (Join key 83).</summary>
    private long GuildCharge(long silver) => silver <= 0 ? 0 : Pricing.Discounted(silver * Pricing.A7m, world.Config.GoldPriceIndex);

    /// <summary>Whether <paramref name="p"/> may enter <paramref name="island"/>: its own, or its guild's guild island.</summary>
    public bool MayEnter(Player p, Island island) =>
        island.Guild ? world.Guilds.Of(p.Character.Id)?.Id == island.Owner : island.Owner == p.Character.Id;

    /// <summary>The checks both guild operations share: the merchant in view, its access point text, the guild type of the city, the guild and the right.</summary>
    private (Guild? Guild, IslandType? Type, string Why) GuildCheck(Player p, IslandAccessPoint ap, string? apText)
    {
        if (!Enabled) return (null, null, "islands are off (Islands:Enabled)");
        if (p.Map is not { } map || ap.Map != map || !p.Visibility.Knows(ap)) return (null, null, $"island merchant {ap.Id} is not in view");
        if (Data?.Types.FirstOrDefault(t => t.Kind == 1 && t.City == map.Cluster) is not { } type) return (null, null, $"{map.Cluster} has no guild island type");
        if (apText != $"{ap.Spawn.Guid:D}@{map.Cluster}") return (null, null, $"access point '{apText}' is not the merchant's {ap.Spawn.Guid:D}@{map.Cluster}");
        if (world.Guilds.Of(p.Character.Id) is not { } g) return (null, null, $"'{p.Character.Name}' is in no guild");
        if ((GuildRoles.Permissions(g.Find(p.Character.Id)!.Role) & GuildRoles.IslandManagement) == 0)
            return (null, null, $"'{p.Character.Name}' ({g.Find(p.Character.Id)!.Role}) lacks ISLAND_MANAGEMENT in guild '{g.Name}'");
        return (g, type, "");
    }

    /// <summary>
    /// BuyGuildIsland: the guild island of the merchant's city for the guild of <paramref name="p"/>, level 0, its price taken from the guild
    /// account (<see cref="Guilds.SpendAccount"/>: saved at once, GuildUpdate kind 2 to the members). Refused (null, the reason): see
    /// <see cref="GuildCheck"/>, the guild owns this type already, the account holds too little.
    /// </summary>
    public Island? BuyGuild(Player p, IslandAccessPoint ap, string? apText, out string why)
    {
        var (g, type, check) = GuildCheck(p, ap, apText);
        why = check;
        if (g is null || type is null) return null;
        if (OfGuild(g.Id).Any(i => i.Type == type.Id)) why = $"guild '{g.Name}' owns a {type.Name} already";
        else if (AccessPointOf(type) is not { } apId) why = $"no island access point of {type.City} known";
        else if (GuildPriceOf(g.Id, type, 0) is not { } price) why = $"{type.Name} has no level 0";
        else if (g.SilverA7m < GuildCharge(price)) why = $"the account of guild '{g.Name}' has {g.SilverA7m / Pricing.A7m} silver, the island costs {GuildCharge(price) / Pricing.A7m}";
        else if (!world.Guilds.SpendAccount(g, GuildCharge(price), $"guild island {type.Name} bought by '{p.Character.Name}'")) why = "the guild account could not be saved";
        else
        {
            var island = new Island(Guid.NewGuid(), g.Id, g.Name, type.Id, apId) { Level = 0, Bought = world.GameNow, Guild = true };
            _islands[island.Id] = island;
            store?.SaveIsland(island);
            Log.Info("world", $"'{p.Character.Name}' buys {island} ({type.Name}) for guild '{g.Name}'");
            return island;
        }
        return null;
    }

    /// <summary>
    /// UpgradeGuildIsland: the guild's island of the merchant's city to <paramref name="level"/> (the next level), paid from the guild account;
    /// then as <see cref="Upgrade"/>: level and stamp now (the client's ckt.c.b 0x180B2BCC0 raises its level and stamps "upgrading" itself on
    /// RC 0), players on it go home, its map is dropped. Refused: see <see cref="GuildCheck"/>, no island, upgrading, not the next level, the account.
    /// </summary>
    public bool UpgradeGuild(Player p, IslandAccessPoint ap, string? apText, int level, out string why)
    {
        var (g, type, check) = GuildCheck(p, ap, apText);
        why = check;
        if (g is null || type is null) return false;
        var now = world.GameNow;
        if (OfGuild(g.Id).FirstOrDefault(i => i.Type == type.Id) is not { } island) why = $"guild '{g.Name}' has no {type.Name}";
        else if (island.Upgrading(now)) why = $"{island} is being upgraded";
        else if (level != island.Level + 1 || GuildPriceOf(g.Id, type, level) is not { } price) why = $"{island}: level {level} is not its next level";
        else if (g.SilverA7m < GuildCharge(price)) why = $"the account of guild '{g.Name}' has {g.SilverA7m / Pricing.A7m} silver, the upgrade costs {GuildCharge(price) / Pricing.A7m}";
        else if (!world.Guilds.SpendAccount(g, GuildCharge(price), $"upgrade of {island} by '{p.Character.Name}'")) why = "the guild account could not be saved";
        else
        {
            Evict(island, "upgrade", "@ISLAND_KICKED_BECAUSE_UPGRADING");
            island.Level = level;
            island.LevelChanged = now;
            store?.SaveIsland(island);
            Log.Info("world", $"'{p.Character.Name}' upgrades {island} of guild '{g.Name}'");
            return true;
        }
        return false;
    }

    /// <summary>A guild is disbanded: its islands go (players on them home first; the rows follow the guild row by ON DELETE CASCADE).</summary>
    public void GuildDeleted(Guid guild)
    {
        foreach (var i in OfGuild(guild))
        {
            Evict(i, "guild disbanded");
            _islands.Remove(i.Id);
            store?.DeleteIsland(i.Id);
            Log.Info("world", $"{i} goes with its guild");
        }
    }
}
