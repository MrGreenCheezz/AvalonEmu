using System.Globalization;
using System.Xml.Linq;

namespace AlbionLocal.Server.Data;

/// <summary>
/// matchtypes.xml and orbtypes.xml (stageP league-pvp SPEC §2.5, §3.1; analysis/work/stageP/league-pvp/SPEC.md): the arena and crystal match
/// rules by uniquename (MATCH_ARENA, MATCH_1V1_ARENA, MATCH_CRYSTAL_GVG_NONLETHAL, …) and the orb types (ORB_ARENA, ORB_CRYSTAL_GVG_*). Read
/// with the first arena match (3 KB, on the world thread, <see cref="ClusterStore.MatchTypes"/>); immutable.
/// </summary>
public sealed class MatchTypeData
{
    /// <summary>How an orb scores (the orbtype's score element).</summary>
    public enum OrbScore
    {
        /// <summary>instantscore (ORB_ARENA): each claim costs the other team ticketreductionorbclaim.</summary>
        Instant,

        /// <summary>
        /// roundbasedscore (the crystal orbs): each claim costs the other team ticketreductionorbclaim; every roundduration the side holding
        /// more orbs takes the matchtype's ticketreductionlookup of the difference, and its scoring orbs reset (resetorbs).
        /// </summary>
        RoundBased,

        /// <summary>constantscore (test types only): not run here.</summary>
        Constant,
    }

    /// <summary>
    /// One &lt;orbtype&gt;: a zone capture (zonecapture: a team alone within <see cref="Radius"/> m for orbclaimtime s claims it; an orb of the other
    /// team first takes orbunclaimtime s; a claim under way with nobody near drops after orbnoteamunclaimtime s) or a channel capture
    /// (channelcapture: a player's channel of orbclaimtime s within capturerange m), its score, the claim's ticket loss, the cooldown after the
    /// fight's start (orbcooldowntimeonstart) and the reset rule (resetorbs: onlyscoringorbs, all).
    /// </summary>
    public sealed record OrbType(string Name, bool Channel, double Radius, double ClaimSeconds, double UnclaimSeconds, double NoTeamUnclaimSeconds,
        OrbScore Score, int ClaimReduction, double CooldownOnStart, string ResetOrbs);

    private readonly Dictionary<string, OrbType> _orbs = new(StringComparer.Ordinal);

    public OrbType? Orb(string? name) => name is not null && _orbs.TryGetValue(name, out var o) ? o : null;

    /// <summary>
    /// A &lt;warcamp&gt;&lt;defense&gt;: the camp's turret (engageattackrange m, a hit every attackinterval s of attackdamage × the target's
    /// maximum health, islethal) and its friendly buff (buffspell within buffradius m).
    /// </summary>
    public sealed record WarcampDefense(double EngageRange, double Interval, double Damage, bool Lethal, double BuffRadius, string BuffSpell);

    /// <summary>
    /// One &lt;matchtype&gt;: matchdurationminutes, roundduration (s), ticketdefault, respawndelay (s), ticketreductionbydeath,
    /// resetafterticketloss (the 1v1's rounds), doublehealerpenalty (a spell, "" = none), orbtype ("" = the map's orb tiles' own types), the
    /// ticketreductionlookup rows (orb difference → tickets) and the warcamp defense (null = a camp without a turret).
    /// </summary>
    public sealed record MatchType(string Name, double Minutes, double RoundSeconds, int Tickets, double RespawnSeconds, int TicketsPerDeath, bool Rounds,
        string DoubleHealerPenalty, string OrbType, IReadOnlyDictionary<int, int> Lookup, WarcampDefense? Defense)
    {
        /// <summary>The list index in matchtypes.xml (StartArenaMatchInfos key 1, `bd6.abo`).</summary>
        public int Index { get; init; }

        /// <summary>The lookup's tickets for an orb difference of <paramref name="difference"/> (the highest row at or below it; 0 without).</summary>
        public int ReductionFor(int difference) =>
            Lookup.Where(r => r.Key <= difference).OrderByDescending(r => r.Key).Select(r => r.Value).FirstOrDefault();
    }

    private readonly Dictionary<string, MatchType> _byName;

    private MatchTypeData(Dictionary<string, MatchType> byName) => _byName = byName;

    public IReadOnlyCollection<MatchType> All => _byName.Values;

    public MatchType? Find(string? name) => name is not null && _byName.TryGetValue(name, out var m) ? m : null;

    internal static string S(XElement e, string a) => ((string?)e.Attribute(a) ?? "").Trim();

    internal static double D(XElement e, string a, double fallback = 0) =>
        double.TryParse(S(e, a), NumberStyles.Float, CultureInfo.InvariantCulture, out var d) ? d : fallback;

    internal static bool B(XElement e, string a) => S(e, a).Equals("true", StringComparison.OrdinalIgnoreCase);

    public static MatchTypeData Load(XElement matchTypes, XElement? orbTypes = null)
    {
        var byName = new Dictionary<string, MatchType>(StringComparer.Ordinal);
        var index = -1;
        foreach (var e in matchTypes.Elements().Where(x => x.Name.LocalName == "matchtype"))
        {
            index++;
            var name = S(e, "uniquename");
            if (name.Length == 0) continue;
            var lookup = new Dictionary<int, int>();
            foreach (var r in e.Elements().Where(x => x.Name.LocalName == "ticketreductionlookup"))
                lookup[(int)D(r, "orbdifference")] = (int)D(r, "ticketreduction");
            var d = e.Elements().FirstOrDefault(x => x.Name.LocalName == "warcamp")?.Elements().FirstOrDefault(x => x.Name.LocalName == "defense");
            var defense = d is null ? null
                : new WarcampDefense(D(d, "engageattackrange"), D(d, "attackinterval", 1), D(d, "attackdamage"), B(d, "islethal"), D(d, "buffradius"), S(d, "buffspell"));
            byName[name] = new MatchType(name, D(e, "matchdurationminutes"), D(e, "roundduration"), (int)D(e, "ticketdefault"), D(e, "respawndelay"),
                (int)D(e, "ticketreductionbydeath"), B(e, "resetafterticketloss"), S(e, "doublehealerpenalty"), S(e, "orbtype"), lookup, defense) { Index = index };
        }
        var data = new MatchTypeData(byName);
        foreach (var e in orbTypes?.Elements().Where(x => x.Name.LocalName == "orbtype") ?? [])
        {
            var name = S(e, "uniquename");
            if (name.Length == 0) continue;
            XElement? Child(string n) => e.Elements().FirstOrDefault(x => x.Name.LocalName == n);
            var zone = Child("zonecapture");
            var channel = Child("channelcapture");
            var (score, scoreNode) = Child("instantscore") is { } i ? (OrbScore.Instant, i)
                : Child("roundbasedscore") is { } r ? (OrbScore.RoundBased, r) : (OrbScore.Constant, Child("constantscore"));
            var capture = zone ?? channel;
            if (capture is null || scoreNode is null) continue;
            data._orbs[name] = new OrbType(name, zone is null, zone is not null ? D(zone, "orbzoneradius") : D(capture, "capturerange", 8), D(capture, "orbclaimtime", 10),
                D(capture, "orbunclaimtime"), D(capture, "orbnoteamunclaimtime"), score, (int)D(scoreNode, "ticketreductionorbclaim"),
                D(scoreNode, "orbcooldowntimeonstart"), S(scoreNode, "resetorbs"));
        }
        return data;
    }
}
