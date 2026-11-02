using AlbionLocal.Server.Entities;
using AlbionLocal.Server.Protocol;

namespace AlbionLocal.Server.Systems;

public static partial class PvpChallenge
{
    private const string DailyEnergyUnavailable =
        "El bono diario de Poder aún no está disponible: el servidor local no genera energía de territorios.";

    /// <summary>
    /// 345319 C92580 (344269 gkj.l4, C8EDD0) ends IsLoading, then reads 0/1 as parallel
    /// territory names / whole energy units, 3 as raw MightBonus, 4 as ClaimableTill, and 2 as
    /// CanClaim. The guild itself comes from the local-player model, not from key 2.
    /// C926E6 converts energy to a7m; C927ED/C9286B/C928EA consume keys 3/4/2.
    /// </summary>
    public static DailyMightBonusResponse Daily(Player p)
    {
        // Empty is truthful without owned watchtowers, and lets the native no-guild/no-territory view
        // finish loading. Do not return it as a successful result for a real territory owner: that
        // would falsely say the guild owns no territories (MightBonusModule, 345319 1143D4F).
        return HasDailyTerritories(p)
            ? EmptyDaily(1, DailyEnergyUnavailable)
            : EmptyDaily();
    }

    public static DailyMightBonusResponse EmptyDaily(short code = 0, string? message = null) => new()
    {
        ReturnCode = code, DebugMessage = message,
        av5 = [], av6 = [], av7 = false, av8 = 0, av9 = 0,
    };

    /// <summary>No earned daily entitlement exists yet. A claim is refused, never acknowledged as paid.
    /// The native claim callback refreshes 505 (345319 C947A0/C9480E, old C91040/C910AE).
    /// </summary>
    public static string DailyClaimRefusal(Player p) => HasDailyTerritories(p)
        ? DailyEnergyUnavailable : "No hay un bono diario de Poder disponible para cobrar.";

    private static bool HasDailyTerritories(Player p) => p.World.Guilds.Of(p.Character.Id) is { } guild
        && p.World.Territories.All.Any(t => t.Owner == guild.Id && !t.NpcHeld && !t.Territory.IsCastle);

    // Functional boundary: Territories currently persists ownership, nutrition and tower power, but
    // never generates or records Siphoned Energy (Territories.SeasonPoints explicitly leaves it absent).
    // pvpchallenge.bin territoryenergycollection supplies energytomightfactor=14, exponent=0.64,
    // usebracketbonus=false and mighttofavorfactor=0. Its actual daily settlement/expiry window and
    // production ledger must be established before adding prospective daily entitlements and an
    // atomic claimed marker. Tower power and guild season points are not substitutes for energy;
    // existing owners, clocks and character logs must not be used to fabricate historical production.
}
