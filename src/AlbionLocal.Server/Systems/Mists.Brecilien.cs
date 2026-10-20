using System.Globalization;
using System.Numerics;
using AlbionLocal.Photon;
using AlbionLocal.Server.Data;
using AlbionLocal.Server.Entities;
using AlbionLocal.Server.Network;
using AlbionLocal.Server.Protocol;

namespace AlbionLocal.Server.Systems;

/// <summary>
/// The Mists, phase B (analysis/work/stageL/mists/SPEC.md §6.7, §6.8, §8 items 4, 6): Brecilien's static entrance (op 477: a zone of rarity 0
/// after secondstoenter, then MISTS_JOINED_FROM_STATIC_ENTRANCE_DEBUFF and its 300 s block), the Brecilien standing (StandingType MistCity:
/// the mists kills' fame × content/mists.json StandingPerFame × Mists:StandingFactor, UpdateStanding 87, again after every Join) with the
/// favour its City Trader takes (the FAVOR currency [design]; the client is not told it: UpdateCurrency 85 is not traced, a client check) and
/// its standing gates, and the official preset's camp free-for-all rule (Pvp:Mode Official only).
/// Under <see cref="World.Sync"/>.
/// </summary>
public sealed partial class Mists
{
    /// <summary>staticentranceblockerdebuff (spells.xml: cannotuseobject mistsstaticentrance for 300 s, kept over cluster changes and death).</summary>
    public const string StaticDebuff = "MISTS_JOINED_FROM_STATIC_ENTRANCE_DEBUFF";
    public const double StaticBlockSeconds = 300;

    /// <summary>standings.xml mistcity: the points from which each of the 10 ranks starts (max 10 000 000), whole points.</summary>
    public static readonly long[] StandingRanks = [0, 50_000, 150_000, 300_000, 600_000, 1_000_000, 1_800_000, 3_250_000, 5_700_000, 10_000_000];

    /// <summary>A static entrance's countdown under way: the entrance, the mode, its end, the build or the zone it joins, the team key, the way back.</summary>
    private sealed record StaticCast(Map Map, MistsStaticEntrance Entrance, MistsMode Mode, TimeSpan Due, Task<MistsBuild>? Build, MistsZone? Join, string TeamKey, Arrival Back);

    private readonly Dictionary<Player, StaticCast> _static = [];
    private readonly Dictionary<Guid, TimeSpan> _staticBlocked = [];

    /// <summary>The clock time until which <paramref name="p"/> may not use a static entrance again; null = it may.</summary>
    public TimeSpan? StaticBlockedUntil(Player p) => _staticBlocked.TryGetValue(p.Character.Id, out var until) && world.Clock.Now < until ? until : null;

    // ---- the static entrance (SPEC §6.7, op 477) ----

    /// <summary>
    /// MistsUseStaticEntrance (op 477) {1 the entrance, 2 the mode's id (<see cref="MistsData.StaticMode"/>)}: Mists:Enabled and StaticEntrance, the
    /// entrance on the player's map within opengatedistance (8 m) + Transfer:PositionTolerance, not knocked down, not blocked by the debuff, no
    /// other entry under way; then the zone's build starts and the answer is {0: now + secondstoenter} (3 s), after which the player goes in
    /// (<see cref="UpdateStatic"/>). Returns the stamp, or why it was refused.
    /// </summary>
    public (long Stamp, string? Refused) UseStatic(Player p, long entranceId, int modeId)
    {
        if (!Options.Enabled || !Options.StaticEntrance) return (0, "the static entrance is closed on this server");
        if (Data is not { } data) return (0, "mists.xml is not read");
        if (p.Map is not { } map || !map.Objects.TryGetValue(entranceId, out var o) || o is not MistsStaticEntrance entrance) return (0, $"no static entrance {entranceId}");
        if (p.Combat.Down is { } down) return (0, $"knocked down ({down.Kind})");
        var utc = DateTime.UtcNow;
        var reach = data.Global("opengatedistance", 8) + world.Config.Transfer.PositionTolerance;
        var at = p.PositionAt(utc);
        if (Vector2.Distance(at, entrance.Position) > reach) return (0, string.Create(CultureInfo.InvariantCulture, $"{Vector2.Distance(at, entrance.Position):0.#} m from the entrance (at most {reach:0.#})"));
        if (StaticBlockedUntil(p) is { } until)
        {
            Expeditions.Tell(p, Settings.Text("StaticBlocked", ("seconds", Math.Ceiling((until - world.Clock.Now).TotalSeconds).ToString(CultureInfo.InvariantCulture))));
            return (0, "blocked by the static entrance's debuff");
        }
        if (_static.ContainsKey(p) || Entering(p)) return (0, "an entry is under way");
        if (data.StaticMode(modeId) is not { } mode) return (0, $"mode id {modeId} names no open mists mode");
        var place = $"static:{entrance.Id}";
        var join = Joinable(p, mode, 0, null, place, out var teamKey);
        var build = join is null ? Pending(p, teamKey, place, () => Build(mode, 0)) : null;
        if (join is null && build is null) return (0, $"no map of {mode.Mapset} for rarity 0");
        var seconds = data.Global("secondstoenter", 3);
        _static[p] = new StaticCast(map, entrance, mode, world.Now + TimeSpan.FromSeconds(seconds), build, join, teamKey, new Arrival(map.Cluster, at, Transfers.Facing(entrance.Position, at)));
        Log.Info("world", $"'{p.Character.Name}' uses {entrance} for {mode.Name}: in {seconds:0.#} s");
        return (world.GameNow + TimeSpan.FromSeconds(seconds).Ticks, null);
    }

    /// <summary>
    /// Every tick: each static entry at its end goes in once its zone is built (at most 30 s more): the way back where the player stood, the
    /// 300 s block and the debuff (<see cref="StaticDebuff"/>), then the push into the zone (arrival as from a portal); a zone that closed during
    /// the countdown is found or built again. A player gone, knocked down or whose build failed drops it (a chat line for the latter).
    /// </summary>
    private void UpdateStatic(TimeSpan now)
    {
        foreach (var (p, started) in _static.ToList())
        {
            var cast = started;
            void Drop(string? why)
            {
                _static.Remove(p);
                if (cast.Build is { } b) Abandon(p, b);
                if (why is not null) Expeditions.Tell(p, Settings.Text("Refused", ("why", why)));
            }
            if (p.Map != cast.Map || p.Session.Player != p || p.Combat.Down is not null)
            {
                Drop(null);
                continue;
            }
            if (now < cast.Due || p.Session.Status != SessionStatus.InWorld) continue;
            if (cast.Join is { Closed: true })
            {
                // The zone it was to join closed during the countdown: another open one of its team, else a build (as the enter cast does).
                var place = $"static:{cast.Entrance.Id}";
                var again = Joinable(p, cast.Mode, 0, null, place, out var key);
                var rebuilt = again is null ? Pending(p, key, place, () => Build(cast.Mode, 0)) : null;
                if (again is null && rebuilt is null)
                {
                    Drop("the zone closed; try again");
                    continue;
                }
                _static[p] = cast = cast with { Join = again, Build = rebuilt, TeamKey = key };
            }
            var zone = cast.Join is { Closed: false } joined ? joined : null;
            if (zone is null && cast.Build is { IsCompleted: true } build)
            {
                if (!build.IsCompletedSuccessfully || (zone = Resolve(p, build, cast.TeamKey, null, cast.Map.Cluster)) is null)
                {
                    Drop("the zone could not be made; try again");
                    continue;
                }
            }
            else if (zone is null)
            {
                if (now >= cast.Due + TimeSpan.FromSeconds(30)) Drop("the zone is not ready; try again");
                continue;
            }
            _static.Remove(p);
            p.Character.DungeonReturn = Back(cast.Back);
            _staticBlocked[p.Character.Id] = now + TimeSpan.FromSeconds(StaticBlockSeconds);
            Pvp.ApplySpell(p, StaticDebuff, world.GameNow);
            Send(p, zone, ArrivalFor(zone, null), answer: false);
        }
    }

    // ---- the standing and the City Trader (SPEC §6.7) ----

    /// <summary>The rank 1-10 of a standing of <paramref name="raw"/> (a7m).</summary>
    public static int RankOf(long raw) => StandingRanks.Count(r => raw / 10000 >= r);

    /// <summary>
    /// A mists kill (Combat.Kill): each member of the fame group gains standing of its fame <paramref name="gain"/> (a7m, after the rates) ×
    /// StandingPerFame × Mists:StandingFactor; nothing outside a live zone.
    /// </summary>
    public void Standing(Map map, IReadOnlyList<Player> group, long gain)
    {
        if (gain <= 0 || Find(map.Cluster) is null) return;
        var add = (long)Math.Round(gain * Settings.StandingPerFame * Options.StandingFactor);
        foreach (var p in group) GainStanding(p, add);
    }

    /// <summary>
    /// <paramref name="p"/> gains <paramref name="add"/> standing (a7m, capped at 10 000 000) and FavorPerStanding of the whole gain as favour
    /// (also past the cap, so the City Trader's currency never dries up [design]): UpdateStanding (87) {0 MistCity, 1 the total, 2 the gain,
    /// 3 0} [inference on keys 2-5], and the chat line Mists.Standing at a new rank.
    /// </summary>
    public void GainStanding(Player p, long add)
    {
        var c = p.Character;
        if (add <= 0) return;
        c.MistFavor += (long)Math.Round(add * Settings.FavorPerStanding);
        var before = c.MistStanding;
        c.MistStanding = Math.Min(StandingRanks[^1] * 10000, before + add);
        var gained = c.MistStanding - before;
        if (gained <= 0) return;
        SendStanding(p, gained);
        if (RankOf(c.MistStanding) > RankOf(before))
            Expeditions.Tell(p, Settings.Text("Standing", ("gain", (gained / 10000).ToString("N0", CultureInfo.InvariantCulture)),
                ("total", (c.MistStanding / 10000).ToString("N0", CultureInfo.InvariantCulture)), ("rank", RankOf(c.MistStanding))));
    }

    /// <summary>
    /// UpdateStanding (87) {0 MistCity, 1 the total, 2 <paramref name="gained"/>, 3 0}: the client keeps the StandingType standings in their own
    /// dictionary (ctp.k1(StandingType) 0x1809A91A0, ctp+0x590), which no Join key fills (105 is the factions' a7m[6]), so this goes after every
    /// Join too (<see cref="Joined"/>, gain 0).
    /// </summary>
    private static void SendStanding(Player p, long gained) =>
        p.Session.Send(new UpdateStandingEvent { d5y = 0, d5z = p.Character.MistStanding, d50 = gained, d51 = 0, Extra = new() { [0] = 0 } }); // key 0 MistCity, which the serializer would leave out

    /// <summary>The recipe requirements the City Trader's checks take (<see cref="TraderRefusal"/>): the standing and the FAVOR currency.</summary>
    public static bool Supports(string requirement) => requirement is "standing" or "currency FAVOR";

    /// <summary>
    /// Why <paramref name="p"/> may not buy <paramref name="crafts"/> of a recipe with a standing or FAVOR requirement (QUESTITEM_TOKEN_MISTS: 675
    /// FAVOR, standing 150 000): a standing type other than mistcity, too little standing (with the chat line Mists.StandingTooLow), too little
    /// favour; null = it may (or the recipe has neither).
    /// </summary>
    public string? TraderRefusal(Player p, CraftRecipe recipe, int crafts)
    {
        var c = p.Character;
        if (recipe.Standing is { } st)
        {
            if (!st.Type.Equals("mistcity", StringComparison.OrdinalIgnoreCase)) return $"the {st.Type} standing is not kept on this server";
            if (c.MistStanding < (long)Math.Round(st.Min * 10000))
            {
                Expeditions.Tell(p, Settings.Text("StandingTooLow", ("need", st.Min.ToString("N0", CultureInfo.InvariantCulture)),
                    ("have", (c.MistStanding / 10000).ToString("N0", CultureInfo.InvariantCulture))));
                return string.Create(CultureInfo.InvariantCulture, $"standing {c.MistStanding / 10000} of {st.Min:0}");
            }
        }
        foreach (var (name, amount) in recipe.Currencies)
        {
            if (name.StartsWith("FACTION_", StringComparison.Ordinal)) continue; // Factions.ShopRefusal's
            if (name != "FAVOR") return $"the currency {name} is not kept on this server";
            var need = (long)Math.Round(amount * 10000) * crafts;
            if (c.MistFavor < need) return string.Create(CultureInfo.InvariantCulture, $"{c.MistFavor / 10000} of {need / 10000} FAVOR");
        }
        return null;
    }

    /// <summary>The FAVOR of <paramref name="crafts"/> of the recipe is taken (after <see cref="TraderRefusal"/> let the purchase through).</summary>
    public void PayFavor(Player p, CraftRecipe recipe, int crafts)
    {
        foreach (var (name, amount) in recipe.Currencies.Where(x => x.Name == "FAVOR"))
            p.Character.MistFavor = Math.Max(0, p.Character.MistFavor - (long)Math.Round(amount * 10000) * crafts);
    }

    // ---- the camps' free-for-all (SPEC §6.8, official) ----

    /// <summary>Mists:UnrestrictedZones under Pvp:Mode Official (SPEC §6.8: official-only; under Consensual or Off no 544 and no change).</summary>
    public bool CampsUnrestricted => Options.UnrestrictedZones && world.Config.PvpMode == PvpMode.Official;

    /// <summary>
    /// Pvp.Of: whether <paramref name="a"/> and <paramref name="b"/> (two players outside one party) both stand in one free-for-all circle of a
    /// zone (<see cref="CampsUnrestricted"/>; 544) whose camp quest is under way for one of them (inside its area, not done) [inference]: then Hostile
    /// (a guild mate still Neutral under Guilds:NoFriendlyFire).
    /// </summary>
    public bool Unrestricted(Player a, Player b)
    {
        if (!CampsUnrestricted || a.IsBot || b.IsBot || a.Map is not { } map || b.Map != map || Find(map.Cluster) is null || Pvp.SameParty(a, b)) return false;
        var utc = DateTime.UtcNow;
        var (pa, pb) = (a.PositionAt(utc), b.PositionAt(utc));
        foreach (var z in map.Objects.Values.OfType<MistsUnrestrictedZone>())
        {
            if (Vector2.Distance(pa, z.Position) > z.Radius || Vector2.Distance(pb, z.Position) > z.Radius) continue;
            if (world.WorldChests.AreasOf(map).Any(q => Vector2.Distance(q.Spawn.Position, z.Position) <= z.Radius
                    && q.Progress.Any(kv => (kv.Key == a.Character.Id || kv.Key == b.Character.Id) && kv.Value is { Inside: true, Done: false })))
                return true;
        }
        return false;
    }
}
