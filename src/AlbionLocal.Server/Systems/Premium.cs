using AlbionLocal.Server.Entities;
using AlbionLocal.Server.Game;
using AlbionLocal.Server.Protocol;

namespace AlbionLocal.Server.Systems;

/// <summary>
/// Premium:Mode None uses the character's purchased expiry; Free gives everyone premium at no cost. A GM can
/// override one player for its session (<see cref="Set"/>). The client learns it
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

    /// <summary>The session override, then Free mode, then the persisted expiry against the world's UTC game clock.</summary>
    public static bool Is(Player p) => End(p, p.World.GameNow) > 0;

    /// <summary>Online and offline sellers use the same per-character entitlement (including its expiry).</summary>
    public static bool Is(World world, Guid character) => world.LoadCharacter(character) is { } c && End(world, c, world.GameNow) > 0;

    private static bool? Override(World world, Guid character) =>
        world.Maps.Maps.SelectMany(m => m.Players.Values).FirstOrDefault(p => p.Character.Id == character)?.PremiumOverride;

    private static bool Unlimited(World world, bool? sessionOverride) => sessionOverride ?? world.Config.PremiumMode == PremiumMode.Free;

    /// <summary>
    /// Remaining premium days in the character list (Login key 35). The client converts days to its duration type;
    /// this field is not the UTC expiry timestamp used by Join key 90.
    /// </summary>
    public static long ListDays(ServerConfig config) => config.PremiumMode == PremiumMode.Free ? (long)FreeSpan.TotalDays : 0;

    /// <summary>Remaining days, rounded up so an active fraction of a day is visible in Login key 35.</summary>
    public static long ListDays(World world, Character character, long? now = null)
    {
        var ticks = now ?? world.GameNow;
        var end = End(world, character, ticks);
        return end == 0 ? 0 : (long)Math.Ceiling((end - ticks) / (double)TimeSpan.TicksPerDay);
    }

    /// <summary>UTC expiry for the character list/subscription panels, or zero when inactive.</summary>
    public static long End(World world, Character character, long now) => End(world, character, now, Override(world, character.Id));

    /// <summary>Join key 90 / event 286 key 1: the real purchased expiry; only Free/GM premium gets a moving end.</summary>
    public static long End(Player p, long now) => End(p.World, p.Character, now, p.PremiumOverride);

    private static long End(World world, Character character, long now, bool? sessionOverride)
    {
        if (Unlimited(world, sessionOverride)) return now + FreeSpan.Ticks;
        if (sessionOverride == false) return 0;
        var end = world.Shop.PremiumEnd(character);
        return end > now ? end : 0;
    }

    /// <summary>
    /// Settles a checkout's copy under the old entitlement, before it is extended. This neither saves nor sends
    /// anything. Publishing the settled fields only after the transaction succeeds prevents free gains on failure.
    /// </summary>
    public static void PreparePurchase(World world, Character snapshot, long now)
    {
        var sessionOverride = Override(world, snapshot.Id);
        snapshot.CraftingFocus = Focus.Current(world, snapshot, now, sessionOverride);
        snapshot.FocusStamp = now;
        LearningPoints.AccrueSnapshot(world, snapshot, now, sessionOverride);
    }

    /// <summary>
    /// Old premium remains accountable after it expires. Splitting at that boundary preserves earned resources
    /// without charging the inactive interval at the premium rate. Purchases settle the copy before extending it.
    /// </summary>
    internal static IEnumerable<(long Start, long End, bool Active)> AccrualPeriods(World world, Character c, long from, long to, bool? sessionOverride)
    {
        if (to <= from) yield break;
        if (Unlimited(world, sessionOverride))
        {
            yield return (from, to, true);
            yield break;
        }
        var end = sessionOverride == false ? 0 : world.Shop.PremiumEnd(c);
        if (from < end)
        {
            yield return (from, Math.Min(to, end), true);
            from = Math.Min(to, end);
        }
        if (from < to) yield return (from, to, false);
    }

    /// <summary>
    /// Lifetime purchased days (Join key 91, event 286 key 2), retained after expiry. Free/GM premium and the explicit
    /// Islands:BuyWithoutPremium compatibility setting retain their configured eligibility.
    /// </summary>
    public static int Days(Player p) => Math.Max(p.World.Shop.PurchasedPremiumDays(p.Character),
        Unlimited(p.World, p.PremiumOverride) || p.World.Config.IslandsBuyWithoutPremium ? p.World.Config.IslandsPremiumDays : 0);

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
        var before = End(p, now) > 0;
        Focus.Settle(p, now);
        LearningPoints.Accrue(p, now, send: true);
        p.PremiumOverride = on;
        if ((End(p, now) > 0) == before) return false;
        // Premium from now on: its accrual starts here (a stamp without accrual may be long ago). Switched off, the stamp stays: the
        // remainder carries if the state without premium accrues too, else its Accrue moves the stamp itself.
        if (End(p, now) > 0) p.Character.LpStamp = now;
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

    /// <summary>Stops both client accrual bars once on expiry; only an unlimited premium refreshes its moving end.</summary>
    public static void Update(Player p, long now)
    {
        if (p.PremiumEnd > 0 && p.PremiumEnd <= now && End(p, now) == 0)
        {
            LearningPoints.Accrue(p, now, send: true);
            Changed(p, now); // the focus bar settles at the old expiry before sending zero regeneration
            return;
        }
        if (!Unlimited(p.World, p.PremiumOverride) || p.PremiumEnd - now >= RefreshBefore.Ticks) return;
        p.PremiumEnd = End(p, now);
        p.Session.Send(new PremiumChangedEvent { ddn = p.Id, dru = p.PremiumEnd, drv = Days(p) });
    }
}
