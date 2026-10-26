using System.Numerics;
using AlbionLocal.Photon;
using AlbionLocal.Server.Data;
using AlbionLocal.Server.Entities;
using AlbionLocal.Server.Game;
using AlbionLocal.Server.Protocol;

namespace AlbionLocal.Server.Systems;

/// <summary>
/// The guild territories and castles (analysis/work/stageP/territories/SPEC.md, phase P1; settings <see cref="TerritoryOptions"/>): the data layer
/// (world.xml's 268 watchtower territories and 118 castle territories, 23 MEDIUMCASTLE castles and 95 SMALLCASTLE castle outposts, with the rules of
/// <see cref="TerritoryRules"/> and the season's seasontimes), the world presence (a <see cref="MonolithObject"/> per watchtower territory and a
/// <see cref="CastleObject"/> per castle territory in their clusters, all unowned) and the info answers of the territory window: GetMonolithInfo
/// (117), GetClaimInfo (118) and GetTerritorySeasonPoints (120). The world map's territory keys of ClusterInfoUpdate (4-18) only with
/// Territories:WorldMapInfo (<see cref="FillWorldMap"/>). Phase P5, the castle fights, captures, owners, chests and castle scoring, is in
/// Territories.Castles.cs; the owners are saved in <paramref name="store"/> (schema v39). Claiming watchtowers, their scoring, raids and attacks
/// are the later phases. Under <see cref="World.Sync"/>.
/// </summary>
public sealed partial class Territories(World world, Persistence.ITerritoryStore? store = null)
{
    private TerritoryRules? _rules;
    private Dictionary<Guid, TerritoryState>? _states;

    private TerritoryOptions S => world.Config.Territories;

    /// <summary>Territories:Enabled.</summary>
    public bool Enabled => S.Enabled;

    /// <summary>Castles:Enabled with Territories:Enabled.</summary>
    public bool CastlesOn => S.Enabled && S.CastlesEnabled;

    private DateTime UtcNow => new(world.GameNow, DateTimeKind.Utc);

    /// <summary>
    /// The GameData rules of the region of Guild:Region (as the guild seasons), read at the first use; without client data or with a broken file
    /// the 345319 values of <see cref="TerritoryRules.Fallback"/> (logged).
    /// </summary>
    public TerritoryRules Rules
    {
        get
        {
            if (_rules is not null) return _rules;
            if (world.Clusters is not { } store) return _rules = TerritoryRules.Fallback;
            try
            {
                _rules = TerritoryRules.Load(store.Folder, world.Goals.Guild.Region);
                Log.Info("data", $"territories: {_rules.Castles.Count} castle types, {_rules.TimeRegions.Count} timeregions (prime time {_rules.PrimeTime:h\\:mm}), "
                    + $"claim cost {_rules.ClaimCost}, {_rules.BasePoints.Count} base point rows");
            }
            catch (Exception e) when (e is InvalidDataException or IOException or System.Xml.XmlException)
            {
                Log.Warn("data", $"territories: {e.Message}; the built-in 345319 values");
                _rules = TerritoryRules.Fallback;
            }
            return _rules;
        }
    }

    /// <summary>Every territory of world.xml by Guid (unowned at first; null without world.xml).</summary>
    private Dictionary<Guid, TerritoryState>? States
    {
        get
        {
            if (_states is not null) return _states;
            if (world.Clusters?.World is not { } info) return null;
            _states = [];
            foreach (var c in info.Clusters)
                foreach (var t in c.TerritoryList)
                    _states.TryAdd(t.Id, new TerritoryState(t, c));
            LoadOwners(_states);
            return _states;
        }
    }

    /// <summary>Every territory (the data layer; none without world.xml).</summary>
    public IEnumerable<TerritoryState> All => States?.Values ?? Enumerable.Empty<TerritoryState>();

    /// <summary>The territory <paramref name="key"/>: its Guid as text (the requests' key 0, av.o), else its name; null = none.</summary>
    public TerritoryState? Find(string? key)
    {
        if (string.IsNullOrWhiteSpace(key) || States is not { } states) return null;
        if (Guid.TryParse(key, out var id)) return states.GetValueOrDefault(id);
        return states.Values.FirstOrDefault(s => string.Equals(s.Territory.Name, key.Trim(), StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>
    /// The monolith of every watchtower territory and the castle of every castle territory of <paramref name="template"/> (MapSpawner): the
    /// critical monolith / castle member whose Guid is the territory's (else the one at its world.xml position), unowned. A territory without its
    /// member, or a castle of a castletype castles.xml lacks, is left out with a warning (the client would drop it).
    /// </summary>
    public IEnumerable<WorldObject> Populate(Map map, ClusterTemplate template)
    {
        if (!Enabled || template.Info.TerritoryList.Count == 0 || States is not { } states) yield break;
        foreach (var t in template.Info.TerritoryList)
        {
            if (!states.TryGetValue(t.Id, out var state)) continue;
            var type = t.IsCastle ? "castle" : "monolith";
            if (t.IsCastle && !CastlesOn) continue;
            var member = template.Critical.FirstOrDefault(m => m.Type == type && m.Guid == t.Id)
                ?? template.Critical.Where(m => m.Type == type && Vector2.Distance(m.Position, t.Monolith) < 1).MinBy(m => Vector2.Distance(m.Position, t.Monolith));
            if (member is null)
            {
                Log.Warn("world", $"map {map.Cluster}: territory {t.Name} ({t.Id}) has no {type} member, left out");
                continue;
            }
            if (!t.IsCastle)
            {
                yield return Register(new MonolithObject(world.ObjectIds.Next(), member, state));
                continue;
            }
            if (Rules.Castle(t.CastleType) is not { } castle)
            {
                Log.Warn("world", $"map {map.Cluster}: castle {t.Name} of castletype '{t.CastleType}' not in castles.xml, left out");
                continue;
            }
            yield return Register(new CastleObject(world.ObjectIds.Next(), member, state, castle), template);
        }
    }

    /// <summary>Whether <paramref name="utc"/> is in <paramref name="state"/>'s cluster's prime time (Territories:PrimeTimeOffsetHours).</summary>
    public bool InPrimeTime(TerritoryState state, DateTime utc) => Rules.InPrimeTime(state.TimeRegion, utc, S.PrimeTimeOffsetHours);

    /// <summary>
    /// Whether the season lets <paramref name="state"/> be claimed at <paramref name="utc"/> (SPEC §3.5): the last territoriesclaimable /
    /// territoriesunclaimable of the guild season's seasontimes (a local season: the last client season's) that applies to its timeregion, at its
    /// time in this season (cluster time where the event says so); unclaimable before the first. Territories:SeasonSchedule off, or no schedule
    /// at all (no client seasons): always.
    /// </summary>
    public bool Claimable(TerritoryState state, DateTime utc)
    {
        if (!S.SeasonSchedule) return true;
        var season = world.GuildGoals.Season;
        if (season.Schedule.Count == 0) return true;
        var tz = Rules.TimeRegions.GetValueOrDefault(state.TimeRegion)?.TimeZone ?? 0;
        var claimable = false;
        foreach (var (e, at) in season.Schedule.Where(e => e.Applies(state.TimeRegion)).Select(e => (e, at: e.At(season.Start, tz))).OrderBy(x => x.at))
        {
            if (at > utc) break;
            foreach (var a in e.Actions)
                if (a.Type == "territoriesclaimable") claimable = true;
                else if (a.Type == "territoriesunclaimable") claimable = false;
        }
        return claimable;
    }

    /// <summary>The territories and castles <paramref name="g"/> owns.</summary>
    public int OwnedBy(Guid g) => g == Guid.Empty ? 0 : All.Count(s => s.Owner == g);

    /// <summary>
    /// The guild fame of <paramref name="g"/> (a7m): its members' fame since they joined it (Member Logs all time: PvE, PvP, gathering and crafting
    /// fame), the server's stand-in for the official guild fame.
    /// </summary>
    public static long GuildFame(Guild g) => g.Progress.Logs.Values.Sum(l =>
        new[] { GuildMemberLog.PveFame, GuildMemberLog.PvpFame, GuildMemberLog.GatheringFame, GuildMemberLog.CraftingFame }.Sum(i => i < l.All.Length ? l.All[i] : 0));

    // ---- the territory window (117, 118, 120)

    /// <summary>
    /// GetMonolithInfo (117, ud {0 territory}; af3 {0 long, 1 int}): kept by the client's territory model (f7b.l4 0x181c58c50) under
    /// "GetMonolithInfoResponse" with no reader found; the SPEC's reading is the food (nutrition) left and the tower level [guess]: a held
    /// watchtower's food (a7m, as GetMyTerritories key 1) and tower level; unowned (or the NPC holder's, or unknown): 0 and 0.
    /// </summary>
    public GetMonolithInfoResponse MonolithInfo(Player p, string? territory)
    {
        if (Find(territory) is not { Territory.IsCastle: false, Owned: true, NpcHeld: false } s) return new() { a1n = 0, a1o = 0 };
        var now = UtcNow;
        return new() { a1n = (long)Math.Round(NutritionAt(s, now) * Pricing.A7m), a1o = TowerLevelAt(s, now) };
    }

    /// <summary>
    /// GetClaimInfo (118, tg {0 territory}; ae7, read by the claim requirements f7a.a 0x181c56e40): 0 the requester's guild fame (a7m, compared with
    /// the client's own requirement, f7a.a 0x181c54e00: initialclaim + claimterritory × key 1), 1 the territories its guild owns, 2 not read there
    /// (false), 3 whether the season lets it be claimed now (the client adds its own prime-time check; <see cref="Claimable"/>). Without a guild
    /// keys 0/1 are 0.
    /// </summary>
    public GetClaimInfoResponse ClaimInfo(Player p, string? territory)
    {
        var g = world.Guilds.Of(p.Character.Id);
        var state = Find(territory);
        return new GetClaimInfoResponse
        {
            as9 = g is null ? 0 : GuildFame(g),
            ata = g is null ? 0 : OwnedBy(g.Id),
            atb = false,
            atc = Enabled && state is { Territory.IsCastle: false } && !state.Owned && Claimable(state, UtcNow),
        };
    }

    /// <summary>
    /// GetTerritorySeasonPoints (120, u5 {0 territory}; agr, read by WorldmapTerritoryUISeasonPointsModule.SetInfo's callback 0x18185e830): 0 the
    /// owner is the reader's guild, 1/2 a multiplier and its time (but.z), 3/4 the points inputs of av.o, 5 the reader's own Siphoning Mages (the
    /// loop's own slots), 6 the score multiplier (shown above 0), 7 the tower level (-1 = none: factor 1), 8 the energy storage. Phase P4
    /// fills only key 0 (the reader's guild holds it); keys 1-6 and 8 stay 0 and 7 -1 (av.o 0x180641e60 turns 1/2/5 into a rate over time and
    /// 6 into days × 86400 for the storage bars, whose meaning is not settled: capture T1), so the client shows no points breakdown yet.
    /// </summary>
    public GetTerritorySeasonPointsResponse SeasonPoints(Player p, string? territory) => new()
    {
        a40 = Find(territory) is { Owned: true } s && world.Guilds.Of(p.Character.Id)?.Id == s.Owner, a47 = -1,
    };

    // ---- the world map

    /// <summary>
    /// ClusterInfoUpdate's territory keys of <paramref name="cluster"/> (SPEC §2.1, WorldmapCluster.UpdateTerritories 0x181be2e30, which indexes
    /// each array by the cluster's territories in client order without checks): one entry per world.xml territory, all of the same length:
    /// 4 owner Guid, 5 owner name, 6 alliance name, 7 the state byte, 8 the Siphoning Mages (an empty Guid[] each), 9 alliance Guid, 10-18 the
    /// numbers (0); 62-64 stay 0. Only with Territories:WorldMapInfo; false = nothing filled (the caller sends no event then).
    /// </summary>
    public bool FillWorldMap(ClusterInfoUpdateEvent e, ClusterInfo cluster)
    {
        if (!Enabled || !S.WorldMapInfo) return false;
        var list = cluster.TerritoryList.Select(t => States?.GetValueOrDefault(t.Id) ?? new TerritoryState(t, cluster)).ToList();
        var n = list.Count;
        e.byd = [.. list.Select(s => s.Owner)];
        e.bye = [.. list.Select(s => s.OwnerName)];
        e.byf = [.. list.Select(s => s.AllianceName)];
        e.byg = new byte[n];
        e.byh = [.. list.Select(_ => Array.Empty<Guid>())];
        e.byi = [.. list.Select(s => s.Alliance)];
        e.byj = new long[n];
        e.byk = new int[n];
        e.byl = new long[n];
        e.bym = new long[n];
        e.byn = new long[n];
        e.byo = new long[n];
        e.byp = new long[n];
        e.byq = new long[n];
        e.byr = new long[n];
        return true;
    }

    /// <summary>The lengths of ClusterInfoUpdate keys 4-18 of <paramref name="e"/> (-1 = left out), for the self-test's same-length check.</summary>
    public static int[] WorldMapLengths(ClusterInfoUpdateEvent e) =>
    [
        e.byd?.Length ?? -1, e.bye?.Length ?? -1, e.byf?.Length ?? -1, e.byg?.Length ?? -1, e.byh?.Length ?? -1, e.byi?.Length ?? -1,
        e.byj?.Length ?? -1, e.byk?.Length ?? -1, e.byl?.Length ?? -1, e.bym?.Length ?? -1, e.byn?.Length ?? -1, e.byo?.Length ?? -1,
        e.byp?.Length ?? -1, e.byq?.Length ?? -1, e.byr?.Length ?? -1,
    ];

    /// <summary>.territory: the counts and rules, or one territory's state.</summary>
    public IEnumerable<string> Describe(string? key)
    {
        if (!Enabled)
        {
            yield return "territories are off on this server (Territories:Enabled)";
            yield break;
        }
        if (!string.IsNullOrWhiteSpace(key))
        {
            if (Find(key) is not { } s)
            {
                yield return $"no territory '{key}'";
                yield break;
            }
            var t = s.Territory;
            var now = UtcNow;
            yield return $"{t.Name} ({t.Id}): {(t.IsCastle ? t.CastleType : $"WATCHTOWER {t.ResourceType}")} in {s.Cluster.Id} {s.Cluster.DisplayName} ({s.Cluster.Type}, {s.TimeRegion})";
            yield return (s.Owned ? $"owner {s.OwnerName}" : "unowned") + $"; prime time now: {InPrimeTime(s, now)}; claimable by the season now: {Claimable(s, now)}";
            foreach (var line in t.IsCastle ? DescribeCastle(s) : DescribeWatchtower(s)) yield return line;
            yield break;
        }
        var all = All.ToList();
        yield return $"{all.Count(s => !s.Territory.IsCastle)} watchtower territories, {all.Count(s => s.Territory.CastleType == "MEDIUMCASTLE")} castles, "
            + $"{all.Count(s => s.Territory.CastleType == "SMALLCASTLE")} castle outposts in {all.Select(s => s.Cluster.Id).Distinct().Count()} clusters; "
            + $"{all.Count(s => s.Owned)} owned";
        var r = Rules;
        yield return $"claim cost {S.ClaimCost ?? r.ClaimCost} silver, guild fame {S.GuildFameRequirement ?? r.FirstClaimFame}, prime time {r.PrimeTime:h\\:mm} "
            + $"({(S.PrimeTimeOnly ? "claims only then" : "any time")}), season schedule {(S.SeasonSchedule ? "data" : "off")}, world map info {(S.WorldMapInfo ? "on" : "off")}";
        yield return $"castles {(CastlesOn ? "on" : "off")}: guards x{S.CastleGuardScale}, chests {(S.CastleChests ? S.CastleChestsHourly ? "hourly" : "data" : "off")}"
            + $"{(S.CastleChestNeedsOwnership ? " for the owner" : " for anyone")}, season points {S.CastleSeasonPoints} per {S.ScoreTickMinutes} min; "
            + $"{_castles.Count(c => c.Map is { Unloaded: false })} castle(s) on loaded maps, {all.Count(s => s.Territory.IsCastle && s.Owned)} owned";
    }
}
