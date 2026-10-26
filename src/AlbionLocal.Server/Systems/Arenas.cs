using System.Globalization;
using System.Numerics;
using AlbionLocal.Photon;
using AlbionLocal.Server.AI;
using AlbionLocal.Server.Bots;
using AlbionLocal.Server.Data;
using AlbionLocal.Server.Entities;
using AlbionLocal.Server.Game;
using AlbionLocal.Server.Network;
using AlbionLocal.Server.Protocol;

namespace AlbionLocal.Server.Systems;

/// <summary>The arena instance ids: "@ARENA@&lt;guid&gt;" (a4l.ClusterInstanceType ARENA = 4, as "@HELLCLUSTER@" is 2; SPEC §1.4).</summary>
public static class ArenaIds
{
    public const string Prefix = "@ARENA@";

    public static string InstanceId(Guid guid) => Prefix + guid.ToString();

    public static bool IsInstance(string? cluster) => cluster?.StartsWith(Prefix, StringComparison.Ordinal) == true;
}

/// <summary>
/// An arena mode (SPEC §2): its name (the ladder's too), the team size, the world.xml map (arena.bin's arenacategory cluster), the client's
/// arena category id it answers (the list index in arena.bin: 0 unranked, 1 ranked crystal, 2 ranked_1v1), whether a death ends the round
/// (MATCH_1V1_ARENA resetafterticketloss: both back to their camps) and the time limit in minutes.
/// </summary>
public sealed record ArenaMode(string Name, int TeamSize, string Cluster, int Category, bool Rounds, double Minutes)
{
    /// <summary>The arenacategory's matchtype (matchtypes.xml: its warcamp defense, double-healer penalty, orbs; stageP league-pvp SPEC §3.1).</summary>
    public string MatchType { get; init; } = "MATCH_ARENA";

    /// <summary>The arenacategory's item power cap (Arena:ItemPowerCap, stageP SPEC §2.5): unranked 600 hard, the 1v1 900 soft (× 0.2 above).</summary>
    public ItemPowerCap ItemPowerCap { get; init; } = new(600, 0);

    /// <summary>arena.bin's personal season points (Arena:Psp, stageP SPEC §3.3): a win and a loss, each without and with premium.</summary>
    public (int Win, int WinPremium, int Loss, int LossPremium) Psp { get; init; } = (1200, 1500, 400, 600);

    /// <summary>
    /// StageP P6: the tickets, the loss per death and the respawn delay come from the matchtype (ticketdefault, ticketreductionbydeath,
    /// respawndelay; these figures when matchtypes.bin is missing) instead of Arena:Tickets5v5 / TicketsPerDeath / RespawnSeconds.
    /// </summary>
    public (int Tickets, int PerDeath, double Respawn)? MatchTypeFigures { get; init; }

    /// <summary>The arena.bin category name whose rank-template rewards the mode pays ("ranked", "ranked_1v1"); null = the unranked items.</summary>
    public string? RankedCategory { get; init; }

    public override string ToString() => Name;
}

/// <summary>A registration in a queue: its players (a party registered as a group, or one), the mode, when it came and the roles the client sent.</summary>
public sealed class ArenaEntry
{
    public required ArenaMode Mode { get; init; }
    public required List<(Guid Id, string Name)> Players { get; init; }
    public required TimeSpan Since { get; init; }
    public required long SinceTicks { get; init; }
    public int Category { get; init; }
    public int[] Roles { get; init; } = [];
}

/// <summary>The life of an arena match (SPEC §4.2).</summary>
public enum ArenaState
{
    /// <summary>Found: each player is asked (the client's ready popup) until Arena:AcceptSeconds.</summary>
    ReadyCheck,

    /// <summary>Sent to the instance: waiting for the players to load it (Arena:ArrivalSeconds at most).</summary>
    Arriving,

    /// <summary>The truce before a fight or a 1v1 round (Arena:CountdownSeconds).</summary>
    Countdown,

    Fight,

    /// <summary>A 1v1 round lost: a short pause, then both back to their camps and a new countdown.</summary>
    RoundOver,

    /// <summary>The result is in: the players stay Arena:EndSeconds, then go back.</summary>
    Ended,
}

/// <summary>A match: its instance, mode, teams (0 and 1), state and score. Its humans are kept by character (a relog finds them again).</summary>
public sealed class ArenaMatch
{
    public required Guid Guid { get; init; }
    public string Id => ArenaIds.InstanceId(Guid);
    public required ArenaMode Mode { get; init; }
    public required List<ArenaEntry> Entries { get; init; }

    /// <summary>The players' teams (0 or 1) and names by character.</summary>
    public Dictionary<Guid, int> Team { get; } = [];
    public Dictionary<Guid, string> Names { get; } = [];

    /// <summary>Where each player goes back to (the spot it was at when the match began).</summary>
    public Dictionary<Guid, DungeonReturn> Back { get; } = [];

    public HashSet<Guid> Accepted { get; } = [];
    public HashSet<Guid> Arriving { get; } = [];

    /// <summary>The players who left during the match (ArenaLeave, .arena leave, gone too long): they lose with their team.</summary>
    public HashSet<Guid> Left { get; } = [];

    /// <summary>The bots by team, and how many each team should have (topped up at each countdown, and Arena:RespawnSeconds after a bot's defeat).</summary>
    public Dictionary<Player, int> Bots { get; } = [];
    public int[] BotSlots { get; } = new int[2];
    public List<(int Team, TimeSpan At)> BotRespawns { get; } = [];

    public ArenaState State { get; set; } = ArenaState.ReadyCheck;
    public TimeSpan Due { get; set; }
    public long FoundTicks { get; set; }
    public TimeSpan? FightSince { get; set; }
    public int Round { get; set; }
    public int[] Tickets { get; } = new int[2];
    public int[] Kills { get; } = new int[2];

    /// <summary>The downed players of a team mode and when they get up at their camp.</summary>
    public Dictionary<Guid, TimeSpan> RespawnAt { get; } = [];

    /// <summary>The camps' spawn positions per team (the template's warcamps), set at the start.</summary>
    public Vector2[][] Spawns { get; set; } = [[], []];

    /// <summary>StageP: the camps' own positions per team (the warcamp tiles; the turret's and the invulnerability circle's centre).</summary>
    public Vector2[] Camps { get; set; } = [];

    /// <summary>StageP: the next turret volley (Arena:WarcampDefense) and the next double-healer check (Arena:DoubleHealerPenalty).</summary>
    public TimeSpan TurretDue { get; set; }
    public TimeSpan HealersDue { get; set; }

    /// <summary>StageP: when each member last got the double-healer penalty.</summary>
    public Dictionary<Player, TimeSpan> Penalized { get; } = [];

    /// <summary>StageP orbs (Arenas.Orbs.cs): the teams' Guids (OrbClaimed key 1), the map's orbs, the players' orb channels, the last orb tick, the next round score.</summary>
    public Guid[] TeamGuids { get; } = [Guid.NewGuid(), Guid.NewGuid()];
    public List<ArenaOrb> Orbs { get; } = [];
    public Dictionary<Player, OrbChannel> Channels { get; } = [];
    public TimeSpan? OrbsAt { get; set; }
    public TimeSpan? RoundAt { get; set; }

    /// <summary>StageP P7: each member's rank index at the result, whose rewards it gets (before the result's rank points).</summary>
    public Dictionary<Guid, int> RewardRank { get; } = [];

    /// <summary>StageP P6: the orb scoring cycles begun (MatchNewCombatRound key 2 with Arena:MatchHud).</summary>
    public int CombatRound { get; set; }

    /// <summary>The winning team (0, 1), -1 for a draw, null before the end.</summary>
    public int? Winner { get; set; }

    public bool HadBots { get; set; }
    public bool Closed { get; set; }

    public IEnumerable<Guid> TeamOf(int team) => Team.Where(t => t.Value == team).Select(t => t.Key);

    public override string ToString() => $"arena {Mode.Name} {Id}";
}

/// <summary>
/// The arena (analysis/work/stageN/arena/SPEC.md, "SPEC" here): the queues of the modes 1v1 (ARENA-1v1-01, the client's ranked_1v1 category),
/// 2v2 and 5v5 (ARENA-STANDARD, the unranked and ranked categories; 2v2 by .arena join only, the client offers no 2v2), taken by the client's
/// ArenaRegister (op 281) at the arena agency and .arena join. The queue status goes to the client as ArenaRegistrationInfo (267) with the
/// statuses its registration HUD and ready popup read (SPEC §1.2: 3 signed up, 5 in the queue, 7 the ready check, 8 accepted, 2 the ready
/// count, 4 out of the queue, 9 cleared). A match is made from whole registrations, balanced by count then rating; with too few players and
/// Arena:Bots it starts after Arena:BotWaitSeconds with player-bots (BotRole.Arena, the hellgate bots' hunt) in the missing slots. The match is
/// an "@ARENA@&lt;guid&gt;" copy of its map; the players go in at their team's camp (the template's warcamps), the two teams Hostile only while
/// a fight runs. A player at 0 health is knocked down without wear or loss (never killed, whatever the zone's lethality) and costs its team a
/// ticket; the 1v1 is the official best of three rounds (2 tickets, a reset after each), the team modes respawn the fallen at their camp after
/// Arena:RespawnSeconds until a team's tickets are gone or the time is up. Then the rewards, the ladder (<see cref="Ratings"/>) and the way back.
/// The match HUD's events (StartArenaMatchInfos 119, EndArenaMatch 120) are not sent (SPEC §1.3): the score is in chat; MatchUpdate 121 only
/// under Arena:MatchHud (Arenas.Orbs.cs). StageP (league-pvp SPEC §2.5, §3.3; Arenas.Rules.cs): the official arena rules: item power caps, the
/// double-healer penalty, the ARENA_* entry buffs, the camps' turret and invulnerability, personal season points; the orbs and their ticket
/// loss (§3.2, Arenas.Orbs.cs).
/// <see cref="World.Update"/> runs <see cref="Update"/>. Under <see cref="World.Sync"/>.
/// </summary>
public sealed partial class Arenas(World world)
{
    /// <summary>The ArenaRegistrationInfo statuses (a6q) the client's dg9.r(g7s, short) reads (SPEC §1.2).</summary>
    public const int StatusCounts = 2, StatusSignedUp = 3, StatusOut = 4, StatusQueued = 5, StatusReady = 7, StatusAccepted = 8, StatusCleared = 9;

    /// <summary>The modes (SPEC §2): the 1v1 on its own map, the team modes on the standard arena.</summary>
    public static readonly IReadOnlyList<ArenaMode> Modes =
    [
        new("1v1", 1, "ARENA-1v1-01", 2, Rounds: true, Minutes: 10)
        {
            MatchType = "MATCH_1V1_ARENA", ItemPowerCap = new(900, 0.2), Psp = (400, 500, 175, 250), // RANKED_BRACKETS_ARENA_S26 ARENA_IRON_1 ranked_1v1
            RankedCategory = "ranked_1v1",
        },
        new("2v2", 2, "ARENA-STANDARD", 0, Rounds: false, Minutes: 0),
        new("5v5", 5, "ARENA-STANDARD", 0, Rounds: false, Minutes: 0),
        // StageP P6: the Crystal Arena (arena.bin "ranked": CrystalRealm03, MATCH_CRYSTAL_GVG_NONLETHAL 24 min, 150 tickets, 6 per death, 18 s).
        new(Crystal, 5, "CrystalRealm03", 1, Rounds: false, Minutes: 24)
        {
            MatchType = "MATCH_CRYSTAL_GVG_NONLETHAL", ItemPowerCap = new(900, 0.2), Psp = (1200, 1500, 500, 750), // ARENA_IRON_1 ranked
            MatchTypeFigures = (150, 6, 18), RankedCategory = "ranked",
        },
    ];

    /// <summary>The Crystal Arena's mode and ladder name (Arena:CrystalArena).</summary>
    public const string Crystal = "crystal";

    /// <summary>The mode of a name ("1v1", "2v2", "5v5"; "1", "2", "5" too), else null.</summary>
    public static ArenaMode? ModeOf(string name) =>
        Modes.FirstOrDefault(m => m.Name.Equals(name.Trim(), StringComparison.OrdinalIgnoreCase) || m.TeamSize.ToString(CultureInfo.InvariantCulture) == name.Trim());

    /// <summary>
    /// The mode of the client's arena category (ArenaRegister key 1): 2 ranked_1v1 → 1v1, 0 unranked → 5v5, 1 ranked → the Crystal Arena
    /// with <paramref name="crystal"/> (Arena:CrystalArena), else the 5v5 as before stageP; else null.
    /// </summary>
    public static ArenaMode? ModeOfCategory(int category, bool crystal = true) =>
        category switch { 2 => Modes[0], 0 => Modes[2], 1 => crystal ? Modes[3] : Modes[2], _ => null };

    private readonly List<ArenaEntry> _queue = [];
    private readonly Dictionary<string, ArenaMatch> _matches = new(StringComparer.Ordinal);

    /// <summary>The days' arena wins per character (Arena:DailyWins' extra reward): the UTC day and the count. Not saved.</summary>
    private readonly Dictionary<Guid, (DateOnly Day, int Wins)> _dailyWins = [];

    private ArenaOptions Options => world.Config.Arena;

    public IReadOnlyList<ArenaEntry> Queue => _queue;
    public IReadOnlyCollection<ArenaMatch> Matches => _matches.Values;

    /// <summary>The live match of instance <paramref name="cluster"/>; null for any other cluster or a closed one.</summary>
    public ArenaMatch? Find(string? cluster) => cluster is not null && _matches.TryGetValue(cluster, out var m) && !m.Closed ? m : null;

    public bool IsLive(string cluster) => _matches.ContainsKey(cluster);

    /// <summary>The world.xml map of a live instance (World.TemplateOf, SelectCharacter key 2, the push's key 1).</summary>
    public string? WorldOf(string cluster) => Find(cluster)?.Mode.Cluster;

    /// <summary>The registration of character <paramref name="id"/>, or null.</summary>
    public ArenaEntry? EntryOf(Guid id) => _queue.FirstOrDefault(e => e.Players.Any(p => p.Id == id));

    /// <summary>The match (any state, before its close) character <paramref name="id"/> is in, or null.</summary>
    public ArenaMatch? MatchOf(Guid id) => _matches.Values.FirstOrDefault(m => !m.Closed && m.Team.ContainsKey(id));

    private Player? Online(Guid id) => world.Parties.Online(id);

    private void Tell(Guid id, string text)
    {
        if (Online(id) is { } p) Expeditions.Tell(p, text);
    }

    private void TellAll(ArenaMatch m, string text)
    {
        foreach (var id in m.Team.Keys) Tell(id, text);
        foreach (var p in InMap(m).Where(p => !m.Team.ContainsKey(p.Character.Id))) Expeditions.Tell(p, text); // a GM watching
    }

    private IEnumerable<Player> InMap(ArenaMatch m) => world.Maps.Find(m.Id)?.Players.Values ?? Enumerable.Empty<Player>();

    // ---- the client's queue status (SPEC §1.2) ----

    /// <summary>ArenaRegistrationInfo (267) to <paramref name="p"/>: status, the category, the timestamp (5: since when queued; 7: when found), the counts.</summary>
    private void Info(Player p, int status, int category, long since = 0, int total = 0, int ready = 0, int[]? roles = null)
    {
        p.Session.Send(new ArenaRegistrationInfoEvent
        {
            ddn = p.Id, br8 = category, br9 = status, bsa = since == 0 ? world.GameNow : since, bse = roles ?? [], bsg = [], bsh = total, bsi = ready,
        });
    }

    private void Info(Guid id, int status, int category, long since = 0, int total = 0, int ready = 0, int[]? roles = null)
    {
        if (Online(id) is { } p) Info(p, status, category, since, total, ready, roles);
    }

    /// <summary>A registration (back) in the queue: 3 (signed up, all confirmed), then 5 (queued since then) to each of its players.</summary>
    private void Queued(ArenaEntry e)
    {
        foreach (var (id, _) in e.Players)
        {
            Info(id, StatusSignedUp, e.Category, e.SinceTicks, e.Players.Count, e.Players.Count, e.Roles);
            Info(id, StatusQueued, e.Category, e.SinceTicks, e.Players.Count, e.Players.Count);
        }
    }

    // ---- the queue (SPEC §4.1) ----

    /// <summary>
    /// <paramref name="p"/> registers for <paramref name="mode"/> (ArenaRegister with the client's <paramref name="category"/>, or .arena join),
    /// with its party's online members when <paramref name="group"/> (at most the team size). Refused: arenas off, already queued or in a
    /// match, inside an instance, knocked down. Returns why, or null.
    /// </summary>
    public string? Register(Player p, ArenaMode mode, bool group, int? category = null, int[]? roles = null)
    {
        if (!Options.Enabled) return "the arena is closed on this server";
        if (mode.Name == Crystal && !Options.CrystalArena) return "the Crystal Arena is off on this server (Arena:CrystalArena)";
        var members = new List<Player> { p };
        if (group && world.Parties.Of(p.Character.Id) is { } party)
            foreach (var id in party.Members.Select(m => m.Id).Where(id => id != p.Character.Id))
                if (Online(id) is { } q) members.Add(q);
        if (members.Count > mode.TeamSize) return $"your group ({members.Count}) is larger than a {mode.Name} team";
        foreach (var q in members)
        {
            var who = q == p ? "you are" : $"{q.Character.Name} is";
            if (EntryOf(q.Character.Id) is { } queued) return $"{who} in the {queued.Mode.Name} queue already (.arena leave)";
            if (MatchOf(q.Character.Id) is not null) return $"{who} in an arena match";
            if (q.Character.DungeonReturn is not null || ArenaIds.IsInstance(q.Map?.Cluster)) return $"{who} inside an instance";
            if (q.Combat.Down is not null) return $"{who} knocked down";
        }
        var entry = new ArenaEntry
        {
            Mode = mode, Players = [.. members.Select(q => (q.Character.Id, q.Character.Name))], Since = world.Now, SinceTicks = world.GameNow,
            Category = category ?? mode.Category, Roles = roles ?? [],
        };
        _queue.Add(entry);
        world.PrefetchCluster(mode.Cluster);
        Queued(entry);
        var waiting = _queue.Where(e => e.Mode == mode).Sum(e => e.Players.Count);
        foreach (var q in members)
            Expeditions.Tell(q, $"Arena {mode.Name}: you are in the queue ({waiting} waiting, {mode.TeamSize * 2} needed"
                + (Options.Bots ? string.Create(CultureInfo.InvariantCulture, $"; bots fill in after {Options.BotWaitSeconds:0} s") : "") + "). Leave it with .arena leave.");
        Log.Info("arena", $"{string.Join(", ", entry.Players.Select(x => $"'{x.Name}'"))} queue for {mode.Name} (category {entry.Category})");
        return null;
    }

    /// <summary>
    /// <paramref name="p"/> leaves the queue (ArenaRegisterCancel, ArenaLeave, .arena leave) or declines its ready check (DeclineArenaInvitation):
    /// its whole registration goes (status 4 to each of its players); a ready check it declines ends. False when it was in neither.
    /// </summary>
    public bool Unregister(Player p, string why)
    {
        var id = p.Character.Id;
        if (EntryOf(id) is { } entry)
        {
            _queue.Remove(entry);
            Out(entry, why);
            return true;
        }
        if (MatchOf(id) is { State: ArenaState.ReadyCheck } m)
        {
            FailReadyCheck(m, [id], why);
            return true;
        }
        return false;
    }

    private void Out(ArenaEntry e, string why)
    {
        foreach (var (id, _) in e.Players)
        {
            Info(id, StatusOut, e.Category);
            Tell(id, $"Arena {e.Mode.Name}: you are out of the queue ({why}).");
        }
        Log.Info("arena", $"{string.Join(", ", e.Players.Select(x => $"'{x.Name}'"))} leave the {e.Mode.Name} queue: {why}");
    }

    /// <summary>
    /// The matchmaking of <paramref name="mode"/> (every tick): the registrations in order, each whole on the team with fewer players (then
    /// the lower average rating) while it fits; both teams full → a match. Short of players, once the oldest registration has waited
    /// Arena:BotWaitSeconds (Arena:Bots): a match of what fits, with bots. The map must be read (and, with bots, its walk grid built) first.
    /// </summary>
    private void MatchUp(ArenaMode mode)
    {
        var waiting = _queue.Where(e => e.Mode == mode).ToList();
        if (waiting.Count == 0) return;
        var teams = new List<ArenaEntry>[] { [], [] };
        int Count(int t) => teams[t].Sum(e => e.Players.Count);
        double Avg(int t) => teams[t].Count == 0 ? 0 : teams[t].SelectMany(e => e.Players).Average(x => world.Ratings.RatingOf(x.Id, mode.Name));
        foreach (var e in waiting)
        {
            var order = Count(0) != Count(1) ? (Count(0) < Count(1) ? new[] { 0, 1 } : [1, 0]) : Avg(0) <= Avg(1) ? [0, 1] : [1, 0];
            foreach (var t in order)
                if (Count(t) + e.Players.Count <= mode.TeamSize)
                {
                    teams[t].Add(e);
                    break;
                }
            if (Count(0) == mode.TeamSize && Count(1) == mode.TeamSize) break;
        }
        var full = Count(0) == mode.TeamSize && Count(1) == mode.TeamSize;
        var bots = !full && BotsAvailable && world.Now - waiting[0].Since >= TimeSpan.FromSeconds(Options.BotWaitSeconds);
        if (!full && !bots) return;
        if (world.CachedCluster(mode.Cluster) is not { } template)
        {
            world.PrefetchCluster(mode.Cluster);
            return;
        }
        if (bots)
        {
            if (!world.Bots.Nav.TryGet(template, out var nav)) return; // the walk grid is being built
            if (!nav.Usable)
            {
                Combat.LogOnce("arena:nav:" + mode.Cluster, $"arena {mode.Name}: {mode.Cluster} has no walk grid for bots; waiting for players");
                return;
            }
        }
        if (Count(1) > Count(0)) (teams[0], teams[1]) = (teams[1], teams[0]);
        var m = new ArenaMatch { Guid = Guid.NewGuid(), Mode = mode, Entries = [.. teams[0], .. teams[1]] };
        for (var t = 0; t < 2; t++)
            foreach (var e in teams[t])
                foreach (var (id, name) in e.Players)
                    (m.Team[id], m.Names[id]) = (t, name);
        _queue.RemoveAll(m.Entries.Contains);
        _matches[m.Id] = m;
        m.FoundTicks = world.GameNow;
        Log.Info("arena", $"{m}: {Describe(m, 0)} vs {Describe(m, 1)}{(full ? "" : " (bots to fill)")}");
        if (!Options.ReadyCheck)
        {
            Start(m);
            return;
        }
        m.State = ArenaState.ReadyCheck;
        m.Due = world.Now + TimeSpan.FromSeconds(Options.AcceptSeconds);
        var category = m.Entries[0].Category;
        foreach (var id in m.Team.Keys)
        {
            Info(id, StatusReady, category, m.FoundTicks, m.Team.Count, 0);
            Tell(id, string.Create(CultureInfo.InvariantCulture, $"Arena {mode.Name}: a match is ready. Accept within {Options.AcceptSeconds:0} s (the popup, or .arena accept)."));
        }
    }

    /// <summary>Whether bots can fill a match: Arena:Bots, Bots:Enabled and Bots:Arena:Enabled (not Bots:World:Enabled).</summary>
    private bool BotsAvailable => Options.Bots && world.Bots.ArenaBots;

    private string Describe(ArenaMatch m, int team) => string.Join(", ", m.TeamOf(team).Select(id => m.Names[id])) is { Length: > 0 } names ? names : "bots";

    /// <summary>JoinArenaMatch (op 285) or .arena accept: <paramref name="p"/> accepts its ready check (8 to it, 2 with the count to all). False without one.</summary>
    public bool Accept(Player p)
    {
        if (MatchOf(p.Character.Id) is not { State: ArenaState.ReadyCheck } m) return false;
        if (!m.Accepted.Add(p.Character.Id)) return true;
        var category = m.Entries[0].Category;
        Info(p, StatusAccepted, category, m.FoundTicks, m.Team.Count, m.Accepted.Count);
        foreach (var id in m.Team.Keys) Info(id, StatusCounts, category, m.FoundTicks, m.Team.Count, m.Accepted.Count);
        if (m.Accepted.Count == m.Team.Count) Start(m);
        return true;
    }

    /// <summary>
    /// A ready check fails: the registrations of <paramref name="refused"/> leave the queue (4), the others go back to its front (3, 5) in their
    /// old order; the match is dropped.
    /// </summary>
    private void FailReadyCheck(ArenaMatch m, IReadOnlyCollection<Guid> refused, string why)
    {
        _matches.Remove(m.Id);
        m.Closed = true;
        var back = new List<ArenaEntry>();
        foreach (var e in m.Entries)
        {
            if (e.Players.Any(x => refused.Contains(x.Id))) Out(e, why);
            else back.Add(e);
        }
        _queue.InsertRange(0, back.OrderBy(e => e.Since));
        foreach (var e in back)
        {
            Queued(e);
            foreach (var (id, _) in e.Players) Tell(id, $"Arena {e.Mode.Name}: the match did not start ({why}); you are back in the queue.");
        }
        Log.Info("arena", $"{m}: the ready check failed ({why})");
    }

    // ---- the match (SPEC §4.2) ----

    /// <summary>
    /// The match begins: the players still online, outside instances and standing go to their team's camp in a new "@ARENA@&lt;guid&gt;" copy
    /// of the mode's map (9 clears their queue HUD; the push {0 id, 1 map}), their way back saved; the others leave it (4). The bots wanted
    /// (Arena:BotFill) come with the first player's arrival.
    /// </summary>
    private void Start(ArenaMatch m)
    {
        var template = world.CachedCluster(m.Mode.Cluster);
        var category = m.Entries[0].Category;
        foreach (var id in m.Team.Keys.ToList())
        {
            var p = Online(id);
            if (p?.Map is not null && p.Combat.Down is null && p.Character.DungeonReturn is null && !ArenaIds.IsInstance(p.Map.Cluster)) continue;
            m.Team.Remove(id);
            Info(id, StatusOut, category);
            Tell(id, $"Arena {m.Mode.Name}: you could not join the match.");
        }
        // Without bots a team left with nobody would give the other a free win: the match does not start, the rest go back to the queue.
        if (!BotsAvailable && m.Team.Count > 0 && template is not null && (!m.TeamOf(0).Any() || !m.TeamOf(1).Any()))
        {
            FailReadyCheck(m, [.. m.Entries.SelectMany(e => e.Players).Select(x => x.Id).Where(id => !m.Team.ContainsKey(id))], "an opponent could not join");
            return;
        }
        if (m.Team.Count == 0 || template is null)
        {
            _matches.Remove(m.Id);
            m.Closed = true;
            foreach (var id in m.Team.Keys) Info(id, StatusOut, category);
            Log.Warn("arena", $"{m}: not started ({(template is null ? $"map {m.Mode.Cluster} not read" : "no player left")})");
            return;
        }
        var humans = new[] { m.TeamOf(0).Count(), m.TeamOf(1).Count() };
        var target = Options.BotFill == ArenaBotFill.Full ? m.Mode.TeamSize : Math.Max(humans[0], humans[1]);
        for (var t = 0; t < 2; t++) m.BotSlots[t] = BotsAvailable ? Math.Max(0, target - humans[t]) : 0;
        m.HadBots = m.BotSlots.Sum() > 0;
        m.Spawns = SpawnsOf(template);
        m.Camps = template.Warcamps.Count >= 2 ? [template.Warcamps[0].Position, template.Warcamps[1].Position] : [m.Spawns[0][0], m.Spawns[1][0]];
        var tickets = TicketsOf(m);
        (m.Tickets[0], m.Tickets[1]) = (tickets, tickets);
        m.State = ArenaState.Arriving;
        m.Due = world.Now + TimeSpan.FromSeconds(Options.ArrivalSeconds);
        foreach (var id in m.Team.Keys)
        {
            var p = Online(id)!;
            var at = p.PositionAt(DateTime.UtcNow);
            var back = new DungeonReturn(p.Map!.Cluster, at.X, at.Y, p.Facing);
            m.Back[id] = back;
            m.Arriving.Add(id);
            p.Character.DungeonReturn = back;
            Info(p, StatusCleared, category);
            Expeditions.Tell(p, $"Arena {m.Mode.Name}: {Describe(m, 0)} against {Describe(m, 1)}{(m.HadBots ? " (with bots)" : "")}. No items are lost here.");
            p.Session.Teleport(Arrival(m, id), $"arena:{m.Id}", m.Mode.Cluster);
        }
        Log.Info("arena", $"{m} starts on {m.Mode.Cluster}: {m.Team.Count} player(s), bots {m.BotSlots[0]}+{m.BotSlots[1]}, {tickets} tickets each");
    }

    /// <summary>
    /// The two camps' spawn positions of <paramref name="t"/> (its first two warcamps, SPEC §3); without two, the default spawn and a point
    /// 30 m east of it.
    /// </summary>
    internal static Vector2[][] SpawnsOf(ClusterTemplate t)
    {
        if (t.Warcamps.Count >= 2) return [[.. t.Warcamps[0].Spawns], [.. t.Warcamps[1].Spawns]];
        var at = Transfers.DefaultSpawn(t);
        return [[at], [at + new Vector2(30, 0)]];
    }

    /// <summary>Where the <paramref name="index"/>-th member of team <paramref name="team"/> stands at a (re)start, facing the other camp.</summary>
    private static (Vector2 At, float Facing) Spot(ArenaMatch m, int team, int index)
    {
        var spots = m.Spawns[team];
        var at = spots[index % spots.Length];
        var other = m.Spawns[1 - team];
        return (at, MotionMaster.Facing(at, other.Length > 0 ? other[0] : at + Vector2.UnitX));
    }

    private Arrival Arrival(ArenaMatch m, Guid id)
    {
        var team = m.Team[id];
        var (at, facing) = Spot(m, team, m.TeamOf(team).ToList().IndexOf(id));
        return new Arrival(m.Id, at, facing);
    }

    /// <summary>
    /// After JoinFinished (WorldSession): in an arena instance, a member that arrived is there (the countdown begins once all are, or a team's
    /// bots come with the first); anyone else watches. Outside nothing.
    /// </summary>
    public void Joined(Player p)
    {
        if (Find(p.Map?.Cluster) is not { } m) return;
        if (!m.Team.TryGetValue(p.Character.Id, out var team))
        {
            Expeditions.Tell(p, $"You are watching {m} (not a member).");
            return;
        }
        var arrived = m.Arriving.Remove(p.Character.Id);
        Entered(p, m, arrived);
        Expeditions.Tell(p, string.Create(CultureInfo.InvariantCulture,
            $"Arena {m.Mode.Name}: you are on team {(team == 0 ? "blue" : "red")}. {(m.Mode.Rounds ? "Best of three rounds" : $"{m.Tickets[team]} tickets: each defeat costs {PerDeath(m)}")}; .arena leave forfeits."));
        if (m.State == ArenaState.Arriving && m.Arriving.Count == 0) Begin(m);
    }

    /// <summary>All arrived (or Arena:ArrivalSeconds passed): the bots, the first countdown.</summary>
    private void Begin(ArenaMatch m)
    {
        foreach (var late in m.Arriving.ToList())
        {
            m.Arriving.Remove(late);
            m.Left.Add(late);
            Log.Info("arena", $"{m}: '{m.Names[late]}' never arrived");
        }
        if (!InMap(m).Any(p => m.Team.ContainsKey(p.Character.Id)))
        {
            Close(m, "nobody arrived");
            return;
        }
        if (world.Maps.Find(m.Id) is { } map) PlaceOrbs(m, map);
        HudStart(m);
        Countdown(m);
        // A team with neither a player nor a bot (none could be made): no fight and no reward for the other side.
        for (var t = 0; t < 2; t++)
            if (!InMap(m).Any(p => m.Team.TryGetValue(p.Character.Id, out var team) && team == t) && !m.Bots.Any(b => b.Value == t))
            {
                TellAll(m, "Arena: the match is called off (an empty team).");
                Close(m, $"team {t} is empty");
                return;
            }
    }

    /// <summary>
    /// A countdown (Arena:CountdownSeconds): every member and bot back at its camp with full health and energy (a knocked-down one gets up
    /// there), the bots topped up to their team's slots, the chat line.
    /// </summary>
    private void Countdown(ArenaMatch m)
    {
        m.Round++;
        m.State = ArenaState.Countdown;
        m.Due = world.Now + TimeSpan.FromSeconds(Options.CountdownSeconds);
        m.RespawnAt.Clear();
        if (world.Maps.Find(m.Id) is not { } map) return;
        var index = new int[2];
        foreach (var p in map.Players.Values.Where(p => m.Team.ContainsKey(p.Character.Id)).OrderBy(p => p.Id))
        {
            var team = m.Team[p.Character.Id];
            Reset(p, Spot(m, team, index[team]++));
        }
        foreach (var (bot, team) in m.Bots.ToList())
            if (bot.Map == map) Reset(bot, Spot(m, team, index[team]++));
        for (var t = 0; t < 2; t++)
            while (m.Bots.Count(b => b.Value == t) < m.BotSlots[t] && SpawnBot(m, map, t, index[t]++)) { }
        TellAll(m, m.Mode.Rounds
            ? string.Create(CultureInfo.InvariantCulture, $"Arena: round {m.Round} ({Score(m)}) begins in {Options.CountdownSeconds:0} s.")
            : string.Create(CultureInfo.InvariantCulture, $"Arena: the fight begins in {Options.CountdownSeconds:0} s."));
    }

    /// <summary>A member or bot back at <paramref name="spot"/>: up (no get-up buff), full bars, moved there unless it stands there.</summary>
    private static void Reset(Player p, (Vector2 At, float Facing) spot)
    {
        if (p.Combat.Down is not null) Knockdown.GetUp(p);
        else if (!p.IsBot) Knockdown.Heal(p);
        else p.Health.Set(p.Health.Max, Math.Max(p.World.GameNow, p.Health.Stamp));
        p.Combat.BuffDue = null;
        if (Vector2.Distance(p.PositionAt(DateTime.UtcNow), spot.At) < 1) return; // there already (the first countdown, after the arrival)
        if (p.IsBot) p.Teleport(spot.At, spot.Facing, DateTime.UtcNow);
        else p.Session.Relocate(spot.At, spot.Facing);
    }

    /// <summary>A bot of team <paramref name="team"/> (BotRole.Arena) at its camp; false when none could be made (no walk grid, no free name).</summary>
    private bool SpawnBot(ArenaMatch m, Map map, int team, int index)
    {
        if (!world.Bots.ArenaBots || world.Bots.NavOf(map) is not { Usable: true } nav) return false;
        var (spot, facing) = Spot(m, team, index);
        var at = nav.Grid.Walkable(spot) ? spot : nav.Grid.Nearest(spot, 6) ?? spot;
        if (world.Bots.Spawn(map, BotRole.Arena, BotTier(m), at, facing, healthFactor: Options.BotHealthFactor, damageFactor: Options.BotDamageFactor) is not { } bot)
            return false;
        m.Bots[bot] = team;
        Entered(bot, m, arrived: true);
        Log.Info("arena", $"{m}: bot '{bot.Character.Name}' T{bot.Bot!.Tier} for team {team}");
        return true;
    }

    /// <summary>Arena:BotTier, or the members' average item power as a tier (T4 700 … T8 1100; 4 without any).</summary>
    private int BotTier(ArenaMatch m)
    {
        if (Options.BotTier > 0) return Options.BotTier;
        var ips = m.Team.Keys.Select(Online).OfType<Player>().Select(Expeditions.AverageItemPower).Where(ip => ip > 0).ToList();
        return ips.Count == 0 ? 4 : Math.Clamp((int)Math.Round((ips.Average() - 300) / 100), 2, 8);
    }

    private static string Score(ArenaMatch m) => m.Mode.Rounds
        ? $"blue {2 - m.Tickets[1]} - {2 - m.Tickets[0]} red"
        : $"tickets blue {m.Tickets[0]} - {m.Tickets[1]} red";

    // ---- the fight ----

    /// <summary>What <paramref name="b"/> is to <paramref name="a"/> (two humans) in an arena (Pvp.Of): team mates Friendly, the other team Hostile during a fight and Neutral else, anyone else Neutral; null outside an arena.</summary>
    public Relation? RelationOf(Player a, Player b)
    {
        if (a.Map is not { } map || b.Map != map || Find(map.Cluster) is not { } m) return null;
        if (!m.Team.TryGetValue(a.Character.Id, out var ta) || !m.Team.TryGetValue(b.Character.Id, out var tb)) return Relation.Neutral;
        return ta == tb ? Relation.Friendly : m.State == ArenaState.Fight ? Relation.Hostile : Relation.Neutral;
    }

    /// <summary>Whether bot <paramref name="bot"/> and human <paramref name="other"/> are on the two teams of a match during its fight (BotPvp.Of).</summary>
    public bool Opponents(Player bot, Player other) =>
        bot.Map is { } map && other.Map == map && Find(map.Cluster) is { State: ArenaState.Fight } m && m.Bots.TryGetValue(bot, out var team)
        && m.Team.TryGetValue(other.Character.Id, out var t) && t != team;

    /// <summary>The member an arena bot hunts (InvaderBrain): the nearest standing one of the other team, during a fight.</summary>
    public Player? TargetOf(Player bot)
    {
        if (Find(bot.Map?.Cluster) is not { State: ArenaState.Fight } m || !m.Bots.TryGetValue(bot, out var team)) return null;
        var at = bot.PositionAt(world.Bots.Time);
        return InMap(m).Where(p => p.Combat.Down is null && m.Team.TryGetValue(p.Character.Id, out var t) && t != team)
            .MinBy(p => Vector2.DistanceSquared(p.PositionAt(DateTime.UtcNow), at));
    }

    /// <summary>
    /// A member at 0 health in a fight (Pvp.Damage, instead of the knockdown's wear and penalties and of a lethal zone's death): the
    /// HealthUpdate, the plain knockdown, the team's ticket. False when <paramref name="target"/> is in no arena match.
    /// </summary>
    public bool Down(Player attacker, Player target, AlbionEvent update, long ticks) => Down(attacker.Id, attacker.Character.Name, target, update, ticks);

    /// <summary><see cref="Down(Player, Player, AlbionEvent, long)"/> by a causer that is no player (a camp's turret: the target itself, as GM damage).</summary>
    private bool Down(long causer, string causerName, Player target, AlbionEvent update, long ticks)
    {
        if (Find(target.Map?.Cluster) is not { } m || !m.Team.TryGetValue(target.Character.Id, out var team)) return false;
        target.SendAround(update);
        Knockdown.Start(target, causer, causerName, ticks, KnockdownKind.Pvp);
        Scored(m, team, causerName, target.Character.Name);
        if (m.State == ArenaState.Fight && !m.Mode.Rounds) m.RespawnAt[target.Character.Id] = world.Now + TimeSpan.FromSeconds(RespawnOf(m));
        return true;
    }

    /// <summary>An arena bot at 0 health (Pvp.Damage, instead of the bots' defeat: no loot, no rewards): it leaves, its team pays a ticket, a new one comes later.</summary>
    public bool BotDown(Player attacker, Player bot, AlbionEvent update) => BotDown(attacker.Character.Name, bot, update);

    private bool BotDown(string causerName, Player bot, AlbionEvent update)
    {
        if (Find(bot.Map?.Cluster) is not { } m || !m.Bots.TryGetValue(bot, out var team)) return false;
        bot.SendAround(update);
        m.Bots.Remove(bot);
        world.Bots.Despawn(bot, "defeated in the arena", BotRefill.Now);
        Scored(m, team, causerName, bot.Character.Name);
        if (m.State == ArenaState.Fight && !m.Mode.Rounds) m.BotRespawns.Add((team, world.Now + TimeSpan.FromSeconds(RespawnOf(m))));
        return true;
    }

    /// <summary>Team <paramref name="team"/> lost one: its ticket, the chat line; at none left the other team wins; a 1v1 round is over.</summary>
    private void Scored(ArenaMatch m, int team, string killer, string victim)
    {
        if (m.State != ArenaState.Fight) return;
        m.Tickets[team] = Math.Max(0, m.Tickets[team] - (m.Mode.Rounds ? 1 : PerDeath(m)));
        m.Kills[1 - team]++;
        TellAll(m, $"Arena: {killer} defeats {victim} ({Score(m)}).");
        HudScore(m);
        if (m.Tickets[team] == 0) End(m, 1 - team, $"{(team == 0 ? "blue" : "red")} has no tickets left");
        else if (m.Mode.Rounds)
        {
            m.State = ArenaState.RoundOver;
            m.Due = world.Now + TimeSpan.FromSeconds(3);
        }
    }

    /// <summary>The result: <paramref name="winner"/> 0, 1, or -1 a draw; the chat lines, the rewards, the ladder; the players stay Arena:EndSeconds.</summary>
    private void End(ArenaMatch m, int winner, string why)
    {
        if (m.Winner is not null) return;
        m.Winner = winner;
        m.State = ArenaState.Ended;
        m.Due = world.Now + TimeSpan.FromSeconds(Options.EndSeconds);
        foreach (var p in InMap(m).Where(p => p.Combat.Down is not null).ToList()) Knockdown.GetUp(p);
        TellAll(m, winner < 0 ? $"Arena: a draw ({why})." : $"Arena: team {(winner == 0 ? "blue" : "red")} ({Describe(m, winner)}) wins ({why}).");
        var rated = Options.Rated && (!m.HadBots || Options.RateWithBots) && m.TeamOf(0).Any() && m.TeamOf(1).Any();
        var changes = rated
            ? world.Ratings.Record(m.Mode.Name, [.. m.TeamOf(0).Select(id => (id, m.Names[id]))], [.. m.TeamOf(1).Select(id => (id, m.Names[id]))], winner < 0 ? 0.5 : winner == 0 ? 1 : 0)
            : [];
        foreach (var id in m.Team.Keys) m.RewardRank[id] = RankIndexOf(id, m);
        var ranks = RankPointsAt(m, changes, winner);
        foreach (var id in m.Team.Keys)
        {
            var won = winner >= 0 && m.Team[id] == winner;
            if (Online(id) is { } p)
            {
                var given = !m.HadBots || Options.RewardsWithBots ? Reward(p, won, m) + SeasonPoints(p, won, m) : "no reward (bots)";
                if (won) world.Vanity.ArenaWon(p, m.Mode.Name); // stageN vanity
                Expeditions.Tell(p, $"Arena {m.Mode.Name}: {(winner < 0 ? "draw" : won ? "victory" : "defeat")}. {given}."
                    + (changes.TryGetValue(id, out var d) ? string.Create(CultureInfo.InvariantCulture, $" {m.Mode.Name} rating {world.Ratings.RatingOf(id, m.Mode.Name)} ({d:+0;-0;0}).") : "")
                    + ranks.GetValueOrDefault(id, "")
                    + string.Create(CultureInfo.InvariantCulture, $" Back in {Options.EndSeconds:0} s (or .arena leave)."));
            }
        }
        Log.Info("arena", $"{m} ended: {(winner < 0 ? "a draw" : $"team {winner} wins")} ({why}), kills {m.Kills[0]}:{m.Kills[1]}{(rated ? ", rated" : "")}");
    }

    /// <summary>A member's reward (SPEC §4.4): silver and fame × Rates, and with Arena:RewardItems arena.bin's unranked items. Returns the chat summary.</summary>
    private string Reward(Player p, bool won, ArenaMatch m)
    {
        var given = new List<string>();
        var silver = (long)Math.Round((won ? Options.WinSilver : Options.LossSilver) * 10000 * world.Rates.Silver);
        if (silver > 0)
        {
            Wallet.Credit(p, silver, $"arena {m.Mode.Name}");
            given.Add(string.Create(CultureInfo.InvariantCulture, $"{silver / 10000.0:0.##} silver"));
        }
        var fame = Rates.ScaleFame((long)Math.Round((won ? Options.WinFame : Options.LossFame) * 10000), world.Rates.Fame);
        if (fame > 0)
        {
            Destiny.Award(p, fame, Destiny.Kill(p, BotTier(m)), pvp: true, journals: false);
            given.Add(string.Create(CultureInfo.InvariantCulture, $"{fame / 10000.0:0.##} fame"));
        }
        if (Options.RewardItems)
        {
            var premium = Premium.Is(p);
            var items = new List<(string Name, int Count)>();
            // The daily limited win reward (rewardsperday 3, dailylimitcategory "unranked" shared by the three queues).
            var limited = false;
            if (won)
            {
                var today = DateOnly.FromDateTime(DateTime.UtcNow);
                var (day, wins) = _dailyWins.GetValueOrDefault(p.Character.Id);
                wins = day == today ? wins : 0;
                limited = wins < Options.DailyWins;
                _dailyWins[p.Character.Id] = (today, wins + 1);
            }
            if (RankedLines(p, won, m) is { } lines)
            {
                items.AddRange(lines.Where(l => l.Item.Length > 0).Select(l => (l.Item, l.Count)));
                if (limited) items.AddRange(RankFor(p, m)!.Limited.Where(l => l.Item.Length > 0).Select(l => (l.Item, l.Count)));
            }
            else if (won)
            {
                items.Add((premium ? "T3_LOOTBAG_ARENA_WIN_PREMIUM_0" : "T2_LOOTBAG_ARENA_WIN_BASIC_0", 1));
                if (limited) items.Add(("T4_LOOTBAG_EXPEDITION_ROYAL_SIGIL", 1));
            }
            else items.Add(("T2_SKILLBOOK_NONTRADABLE", premium ? 7 : 5));
            given.AddRange(GiveItems(p, items, $"the arena ({m.Mode.Name})"));
        }
        world.Saves.MarkChanged(p.Character);
        return given.Count == 0 ? "No reward" : "Reward: " + string.Join(", ", given);
    }

    /// <summary>Gives <paramref name="items"/> to <paramref name="p"/> (the bag, else the mail); the given ones' names for the chat line.</summary>
    private List<string> GiveItems(Player p, IEnumerable<(string Name, int Count)> items, string why)
    {
        var given = new List<string>();
        foreach (var (name, count) in items)
        {
            if (world.Items.Find(name) is not { CanCreate: true } type) continue;
            if (WorldSession.Fits(p.Inventory.Bag, [(type, count)])) p.Session.GiveItem(p, type, count);
            else if (!Mailer.Overflow(world, p.Character, [new InventoryItem(type.Name, count)], why)) continue;
            given.Add(count == 1 ? type.Name : $"{type.Name} x{count}");
        }
        return given;
    }

    // ---- leaving and closing ----

    /// <summary>
    /// <paramref name="p"/> leaves its match (ArenaLeave, .arena leave, the close): during it a forfeit for its team once nobody of it is left;
    /// out to its way back (<paramref name="answer"/> false: the push).
    /// </summary>
    public Arrival Leave(Player p, ArenaMatch m, string why, bool answer = false)
    {
        var id = p.Character.Id;
        var back = m.Back.GetValueOrDefault(id) ?? p.Character.DungeonReturn ?? new DungeonReturn(Options.FallbackCluster, 0, 0, 0);
        if (m.State is not ArenaState.Ended && m.Team.ContainsKey(id)) m.Left.Add(id);
        m.Arriving.Remove(id);
        p.Character.DungeonReturn = null;
        if (p.Combat.Down is not null) Knockdown.GetUp(p);
        var to = new Arrival(back.Cluster, new Vector2(back.X, back.Y), back.Direction);
        Log.Info("arena", $"'{p.Character.Name}' leaves {m} ({why})");
        if (!answer) p.Session.Teleport(to, $"arena:{m.Id}:{why}");
        return to;
    }

    /// <summary>A character whose saved cluster is an arena that is gone (closed, a restart): its way back (else Arena:FallbackCluster), saved. False otherwise.</summary>
    public bool ReturnIfClosed(Character c)
    {
        if (!ArenaIds.IsInstance(c.Cluster) || IsLive(c.Cluster)) return false;
        var to = c.DungeonReturn ?? new DungeonReturn(Options.FallbackCluster, 0, 0, 0);
        (c.Cluster, c.PositionX, c.PositionY, c.Direction) = (to.Cluster, to.X, to.Y, to.Direction);
        c.PendingTransfer = null;
        c.DungeonReturn = null;
        world.Saves.SaveNow(c);
        Log.Info("arena", $"'{c.Name}' was in an arena that is gone: back in {to.Cluster}");
        return true;
    }

    /// <summary>The match closes: everyone still inside goes back, the bots go, the map is unloaded.</summary>
    public void Close(ArenaMatch m, string why)
    {
        if (m.Closed) return;
        m.Closed = true;
        m.Channels.Clear();
        foreach (var bot in m.Bots.Keys.Where(b => b.Map is not null).ToList()) world.Bots.Despawn(bot, $"the arena closes ({why})", BotRefill.Now);
        m.Bots.Clear();
        if (world.Maps.Find(m.Id) is { } map)
        {
            foreach (var p in map.Players.Values.ToList())
            {
                if (p.Session.Status == SessionStatus.InWorld && p.Session.Player == p) Leave(p, m, why);
                else world.Maps.Remove(p, world.Clock.Now);
            }
            world.Maps.Unload(map, force: true);
        }
        _matches.Remove(m.Id);
        Log.Info("arena", $"{m} closed ({why})");
    }

    /// <summary>The server stops or a GM ends everything: every match closes (no result), the queue empties.</summary>
    public void CloseAll(string why)
    {
        foreach (var m in _matches.Values.ToList()) Close(m, why);
        foreach (var e in _queue.ToList()) Out(e, why);
        _queue.Clear();
    }

    // ---- the tick ----

    /// <summary>
    /// Every tick (<see cref="World.Update"/>): registrations of players gone leave the queue; each mode's matchmaking; each match's timers: the
    /// ready check's end, the arrival's, the countdown's, a 1v1 round's pause, the fight's respawns, its time limit and a team that left;
    /// the closing after the result.
    /// </summary>
    public void Update()
    {
        if (_queue.Count == 0 && _matches.Count == 0) return;
        var now = world.Now;
        foreach (var e in _queue.ToList())
            if (e.Players.Any(x => Online(x.Id) is null))
            {
                _queue.Remove(e);
                Out(e, "a player went offline");
            }
        foreach (var mode in Modes) MatchUp(mode);
        foreach (var m in _matches.Values.ToList())
        {
            switch (m.State)
            {
                case ArenaState.ReadyCheck:
                    var gone = m.Team.Keys.Where(id => Online(id) is null).ToList();
                    if (gone.Count > 0) FailReadyCheck(m, gone, "a player went offline");
                    else if (now >= m.Due) FailReadyCheck(m, [.. m.Team.Keys.Where(id => !m.Accepted.Contains(id))], "not accepted in time");
                    break;
                case ArenaState.Arriving:
                    if (now >= m.Due) Begin(m);
                    break;
                case ArenaState.Countdown:
                    Rules(m, now);
                    if (now >= m.Due)
                    {
                        m.State = ArenaState.Fight;
                        if (m.FightSince is null || m.Mode.Rounds) HudRound(m, m.Mode.Rounds ? m.Round : 1);
                        m.FightSince ??= now;
                        TellAll(m, "Arena: fight!");
                    }
                    break;
                case ArenaState.RoundOver:
                    if (now >= m.Due) Countdown(m);
                    break;
                case ArenaState.Fight:
                    Rules(m, now);
                    if (m.State == ArenaState.Fight) OrbTick(m, now);
                    if (m.State == ArenaState.Fight) Fight(m, now);
                    break;
                case ArenaState.Ended:
                    if (now >= m.Due) Close(m, "finished");
                    break;
            }
            // Nobody of the match inside any more (all left, logged out): it closes; a result first when a fight was under way.
            if (!m.Closed && m.State is not (ArenaState.ReadyCheck or ArenaState.Arriving) && !InMap(m).Any(p => m.Team.ContainsKey(p.Character.Id)))
            {
                if (m.Winner is null && m.FightSince is not null) End(m, -1, "everyone left");
                Close(m, "empty");
            }
        }
    }

    /// <summary>
    /// A fight's tick: the fallen of a team mode up at their camp when due, the bots back, a team whose players all left (with players on the
    /// other side) loses, the time limit (Arena:MatchMinutes; the 1v1 its own 10) gives the win to the team with more tickets.
    /// </summary>
    private void Fight(ArenaMatch m, TimeSpan now)
    {
        var map = world.Maps.Find(m.Id);
        foreach (var (id, at) in m.RespawnAt.ToList())
        {
            if (now < at) continue;
            m.RespawnAt.Remove(id);
            if (map?.Players.Values.FirstOrDefault(p => p.Character.Id == id) is { } p)
                Reset(p, Spot(m, m.Team[id], m.TeamOf(m.Team[id]).ToList().IndexOf(id)));
        }
        foreach (var due in m.BotRespawns.Where(b => now >= b.At).ToList())
        {
            m.BotRespawns.Remove(due);
            if (map is not null) SpawnBot(m, map, due.Team, m.Bots.Count(b => b.Value == due.Team));
        }
        bool Present(int team) =>
            m.TeamOf(team).Any(id => !m.Left.Contains(id) && (map?.Players.Values.Any(p => p.Character.Id == id) ?? false)) || m.Bots.Any(b => b.Value == team)
            || m.BotRespawns.Any(b => b.Team == team);
        if (!Present(0) && Present(1)) End(m, 1, "blue left");
        else if (!Present(1) && Present(0)) End(m, 0, "red left");
        else if (now - m.FightSince >= TimeSpan.FromMinutes(m.Mode.Minutes > 0 ? m.Mode.Minutes : Options.MatchMinutes))
            End(m, m.Tickets[0] == m.Tickets[1] ? -1 : m.Tickets[0] > m.Tickets[1] ? 0 : 1, "time is up");
    }

    // ---- GM and status ----

    /// <summary>.arena status: the queue and the matches, one line each.</summary>
    public IEnumerable<string> Describe()
    {
        foreach (var mode in Modes)
        {
            var entries = _queue.Where(e => e.Mode == mode).ToList();
            if (entries.Count > 0)
                yield return string.Create(CultureInfo.InvariantCulture,
                    $"  {mode.Name} queue: {string.Join("; ", entries.Select(e => $"{string.Join(", ", e.Players.Select(x => x.Name))} ({(world.Now - e.Since).TotalSeconds:0} s)"))}");
        }
        foreach (var m in _matches.Values)
            yield return string.Create(CultureInfo.InvariantCulture,
                $"  {m}: {m.State}, {Describe(m, 0)} vs {Describe(m, 1)}, {m.Bots.Count} bot(s), {Score(m)}{(m.FightSince is { } f ? $", {(world.Now - f).TotalMinutes:0.#} min" : "")}");
    }

    /// <summary>.arena end (GM): the match of <paramref name="p"/> ends now, won by <paramref name="team"/> (0 blue, 1 red, -1 a draw).</summary>
    public string? Finish(Player p, int team)
    {
        if ((MatchOf(p.Character.Id) ?? Find(p.Map?.Cluster)) is not { } m) return "no arena match";
        if (m.State is ArenaState.ReadyCheck or ArenaState.Arriving) return "the match has not begun";
        if (m.Winner is not null) return "the match is over already";
        m.FightSince ??= world.Now;
        End(m, team, "a GM ended it");
        return null;
    }
}
