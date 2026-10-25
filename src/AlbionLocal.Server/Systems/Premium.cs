using AlbionLocal.Server.Entities;
using AlbionLocal.Server.Protocol;

namespace AlbionLocal.Server.Systems;

/// <summary>
/// Premium (analysis/work/stageJ/focus-lp/SPEC.md D1-D3, §5.2, §5.5): Premium:Mode None (official preset: nobody, there is no premium shop)
/// or Free (pve preset: everyone, always, at no cost); a GM can switch one player for its session (<see cref="Set"/>). The client learns it
/// from Join key 90 (the end; it compares it with now, ctp.asj) and key 91 (the days), PremiumChanged (286) on a live change, and the
/// character list's Login key 35; it then works out the halved market tax and its premium UI itself. The server applies the premium
/// factors of characters.xml (each but the tax with a Premium:* key over it): the destiny board's share of fame, silver pickups, the
/// harvest's premium bonus, island crops, focus regeneration and learning points (<see cref="Focus"/>, <see cref="LearningPoints"/>).
/// Touched only under <see cref="World.Sync"/>.
/// </summary>
public static class Premium
{
    /// <summary>How far ahead a Free premium ends in the client's eyes (it shows the days left; re-sent before it runs short, <see cref="Update"/>) [choice].</summary>
    public static readonly TimeSpan FreeSpan = TimeSpan.FromDays(365);

    /// <summary>A Free premium end closer than this is pushed out again with a PremiumChanged (<see cref="Update"/>).</summary>
    public static readonly TimeSpan RefreshBefore = TimeSpan.FromDays(30);

    /// <summary>Whether <paramref name="p"/> is premium: the GM's switch of its session, else Premium:Mode.</summary>
    public static bool Is(Player p) => p.PremiumOverride ?? p.World.Config.PremiumMode == PremiumMode.Free;

    /// <summary>Whether the character <paramref name="character"/> is premium: its player's state when it is online, else Premium:Mode.</summary>
    public static bool Is(World world, Guid character) =>
        world.Maps.Maps.SelectMany(m => m.Players.Values).FirstOrDefault(p => p.Character.Id == character) is { } online
            ? Is(online) : world.Config.PremiumMode == PremiumMode.Free;

    /// <summary>
    /// Remaining premium days in the character list (Login key 35). The client converts days to its duration type;
    /// this field is not the UTC expiry timestamp used by Join key 90.
    /// </summary>
    public static long ListDays(ServerConfig config) => config.PremiumMode == PremiumMode.Free ? (long)FreeSpan.TotalDays : 0;

    /// <summary>The premium end of <paramref name="p"/> (Join key 90, 286 key 1): <paramref name="now"/> + <see cref="FreeSpan"/> while premium, else 0 (left out).</summary>
    public static long End(Player p, long now) => Is(p) ? now + FreeSpan.Ticks : 0;

    /// <summary>
    /// The premium days of <paramref name="p"/> (Join key 91, 286 key 2; ctp+0x3D0): Islands:PremiumDays (31 as captured; the island merchant
    /// sells from 7 on and the season's boughtpremium rewards need more than 6) while premium or with Islands:BuyWithoutPremium (the temporary
    /// rule of both presets: no premium can be had yet), else 0 (left out: the merchant offers no purchase); whether islands are on or not.
    /// </summary>
    public static int Days(Player p) => Is(p) || p.World.Config.IslandsBuyWithoutPremium ? p.World.Config.IslandsPremiumDays : 0;

    /// <summary>The destiny board's factor on a kill's or harvest's fame (UpdateFame key 5 sources; premiumdestinyboardprogressionfactor 1.5, Premium:FameFactor), 1 without premium.</summary>
    public static double FameFactor(Player p) => Is(p) ? p.World.Config.PremiumFameFactor ?? Defaults(p.World).PremiumFameFactor : 1;

    /// <summary>The factor on a silver pile taken (premiumsilverlootfactor 1.5, Premium:SilverFactor), 1 without premium.</summary>
    public static double SilverFactor(Player p) => Is(p) ? p.World.Config.PremiumSilverFactor ?? Defaults(p.World).PremiumSilverFactor : 1;

    /// <summary>The share of a harvest's amount given as its premium bonus (HarvestFinished key 7; premiumgatheringyieldbonuschance 0.5, Premium:GatherBonusChance), 0 without premium.</summary>
    public static double GatherBonusChance(Player p) => Is(p) ? p.World.Config.PremiumGatherBonusChance ?? Defaults(p.World).PremiumGatherBonusChance : 0;

    /// <summary>The factor on an island crop's harvest items (premiumcropyieldfactor 2, Premium:CropYieldFactor), 1 without premium.</summary>
    public static double CropYieldFactor(Player p) => Is(p) ? p.World.Config.PremiumCropYieldFactor ?? Defaults(p.World).PremiumCropYieldFactor : 1;

    /// <summary>
    /// The factor on the sales tax of a sale by <paramref name="seller"/> (premiummarkettransactionfeefactor 0.5; no key: the client
    /// computes the tax it shows with the data's factor itself, so the server must take the same), 1 without premium.
    /// </summary>
    public static double TaxFactor(World world, Guid seller) => Is(world, seller) ? Defaults(world).PremiumMarketFeeFactor : 1;

    private static Data.CharacterDefaults Defaults(World world) => world.GameData?.Defaults ?? Data.CharacterDefaults.Default;

    /// <summary>The Join keys of premium: 90 the end (left out without premium), 91 the days; remembered as the end the client knows.</summary>
    public static void JoinKeys(Player p, JoinResponse r, long now)
    {
        p.PremiumEnd = End(p, now);
        r.bas = p.PremiumEnd;   // [90] GameTimeStamp → ctp+0x3C8 (0 = left out: no premium)
        r.bat = Days(p);  // [91] → ctp+0x3D0 (0 = left out)
    }

    /// <summary>
    /// The GM's switch (.premium on|off|mode): <paramref name="on"/> true / false over Premium:Mode, null = the mode's again. When the state
    /// changes: the focus and the learning points are settled under the old state, then the burst of the captured premium start (SPEC §3:
    /// RegenerationCraftingChanged 94 with the new regeneration, UpdateLearningPoints 83 with the new seconds per point, PremiumChanged 286
    /// {0, 1 end, 2 days}). Returns whether it changed.
    /// </summary>
    public static bool Set(Player p, bool? on, long now)
    {
        var before = Is(p);
        Focus.Settle(p, now);
        LearningPoints.Accrue(p, now, send: true);
        p.PremiumOverride = on;
        if (Is(p) == before) return false;
        // Premium from now on: its accrual starts here (a stamp without accrual may be long ago). Switched off, the stamp stays: the
        // remainder carries if the state without premium accrues too, else its Accrue moves the stamp itself.
        if (Is(p)) p.Character.LpStamp = now;
        p.World.Saves.MarkChanged(p.Character);
        Changed(p, now);
        return true;
    }

    /// <summary>The events of a live premium change (see <see cref="Set"/>): 94, 83 and 286.</summary>
    public static void Changed(Player p, long now)
    {
        p.Session.Send(Focus.Bar(p, now));
        p.Session.Send(LearningPoints.Event(p, 0, LearningPoints.Silent));
        p.PremiumEnd = End(p, now);
        p.Session.Send(new PremiumChangedEvent { ddn = p.Id, dru = p.PremiumEnd, drv = Days(p) });
    }

    /// <summary>A Free premium whose end the client knows comes closer than <see cref="RefreshBefore"/>: a PremiumChanged with the end pushed out (<see cref="LearningPoints.Update"/>).</summary>
    public static void Update(Player p, long now)
    {
        if (!Is(p) || p.PremiumEnd - now >= RefreshBefore.Ticks) return;
        p.PremiumEnd = End(p, now);
        p.Session.Send(new PremiumChangedEvent { ddn = p.Id, dru = p.PremiumEnd, drv = Days(p) });
    }
}
