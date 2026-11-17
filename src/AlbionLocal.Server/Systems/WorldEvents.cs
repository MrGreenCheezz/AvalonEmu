using System.Globalization;
using System.Numerics;
using System.Xml.Linq;
using AlbionLocal.Photon;
using AlbionLocal.Server.Content;
using AlbionLocal.Server.Data;
using AlbionLocal.Server.Entities;
using AlbionLocal.Server.Game;
using AlbionLocal.Server.Protocol;

namespace AlbionLocal.Server.Systems;

/// <summary>
/// The world events: invasions and champion hunts in the open world (PVE-DESIGN.md §2.H, §3 step 7), with content/events.json
/// (<see cref="EventSettings"/>). A server feature of its own: the client gets only messages it knows (NewMob, SystemMessage, the
/// NewLootChest of a personal chest).
/// <list type="bullet">
/// <item>Stage I (analysis/work/stageI/invasions/SPEC.md): started by a player (<see cref="PlayerInvasions"/>: .invasion &lt;1-10&gt;, a
/// ticket item) or a GM with a tier 1-10 (Tiers): its mobs of the row's MobTier (<see cref="Plan"/>: the zone's invasion faction, the
/// same families, else the faction's usable mobs, heretics for T1-T3, else the nearest tier × 1.15 per step), the row's level buff,
/// factors, fame factor, extra waves and a group count; the reward the row's chest, loot tier, item tier, enchantment, silver and fame,
/// at most Player.MaxRewarded players plus their party near the point, and a ticket by chance (Drops.Won).</item>
/// <item>Start: every IntervalMinutes (uniform min..max, from the start and from the end of the last event) with Scheduled on (off by default since stage I), in a
/// random open-world cluster whose map has at least MinPlayers players and mob spawn blocks (no city, no instance; Clusters narrows it),
/// a kind picked by Weight; or by a GM (.event start). The point is a mob spawn point of the map (walkable by the data) at Distance from
/// a random player of the map (else the nearest one beyond Distance min; a GM's "here" is 10 m in front of the GM).</item>
/// <item>Its mobs: the faction of the spawn block nearest the point (UNDEAD, UNDEAD_CAMP, KEEPER, …) and of the cluster's tier, from
/// every block of the map with mobs of that faction, picked by the blocks' weights among the wave's kind (standard: the standard, trash
/// and uncategorised mobs; champion, miniboss, boss by mobs.xml category, falling back to the next lower kind the blocks have). A wave
/// puts uniform(Count) of them on a ring of SpreadMeters around the point, through the code of .mob spawn (<see cref="SpawnManager.SpawnAt"/>:
/// no spawn point, never back), with the wave's HealthFactor and DamageFactor (as a world boss's, NewMob key 14), and with the mob AI
/// on each goes for the nearest player of the map (threat 1) when that one is within its pursuitradius of its spot (else it would leash
/// on the way: its Home is its spot), the others are left to its aggro as players come. The next wave comes WaveDelaySeconds after the
/// last mob of a wave died. At most one event per map (scheduled or a GM's).</item>
/// <item>Progress: SystemMessage lines (as the GM replies and the world bosses; the client cannot show the place, so the start line
/// has the distance, the compass direction and the position for each player) to the players in the cluster (AnnounceToAll: everyone):
/// the start, each wave, the win, the failure. The damage on its mobs counts per player (<see cref="Damaged"/>, the health it took).</item>
/// <item>Win (the last wave dead): a personal loot chest of the kind's Chest spawnpoint at the point (<see cref="LootChest.Personal"/>:
/// Unlocked by character Guid for every player with at least RewardMinShare of the event's damage, wherever it is then: one back
/// before ChestMinutes may open it; LockedByQuest for the others), each opening rolls its own loot at the cluster's tier × RewardFactor
/// (<see cref="RandomDungeons.OpenChest"/>), gone after ChestMinutes; and RewardFame (× Rates:Fame) for each of those players online
/// (on any map; one offline at the win gets none). A last wave gone with no player damage (a GM's .mob kill, other kills) is no win:
/// it fails with the retreat line. Failure (DurationMinutes over): its mobs go (a despawn), no reward.</item>
/// </list>
/// Not handled (departures): no world map marker (none in the client data for a server event), healing counts for nothing, the state is
/// not saved (a restart ends every event), an event's map unloaded ends it; the point is a mob spawn point, not a POI/RandomExit spot as
/// in PVE-DESIGN §2.H (those are walkable by the data, the exits are at the edges); the champion hunt's mob stands at the point (no
/// roaming through the cluster). The time is <see cref="World.Now"/> (the self-test's manual
/// clock). Touched only under <see cref="World.Sync"/>.
/// </summary>
public sealed class WorldEvents(World world)
{
    /// <summary>How an event ended.</summary>
    public enum Outcome
    {
        Running,
        Won,
        Failed,
        Stopped,
    }

    /// <summary>Who started an event.</summary>
    public enum Source
    {
        Scheduled,
        Gm,
        Command,
        Item,
    }

    /// <summary>A running (or just ended) event: its kind, place, mobs and the damage of each player.</summary>
    public sealed class Run
    {
        public required int Id { get; init; }
        public required EventType Type { get; init; }
        public required Map Map { get; init; }
        public required Vector2 Point { get; init; }

        /// <summary>The faction of its mobs (mobs.xml faction: UNDEAD, KEEPER, …).</summary>
        public required string Faction { get; init; }

        /// <summary>The title of the chat lines, its {faction} filled.</summary>
        public required string Title { get; init; }

        /// <summary>The mobs of each wave kind, with their weights (picked at the start).</summary>
        public required IReadOnlyDictionary<WaveKind, List<(MobTemplate Mob, double Weight)>> Pools { get; init; }

        /// <summary>Who started it (stage I: the schedule, a GM, a player's .invasion or ticket).</summary>
        public Source Source { get; init; } = Source.Gm;

        /// <summary>Started by the schedule (counted against MaxRunning), not by a GM or a player.</summary>
        public bool Scheduled => Source == Source.Scheduled;

        /// <summary>Stage I: its difficulty 1-10 (Tiers); 0 = none (a scheduled event or a GM's without a tier: the cluster's mobs and rewards).</summary>
        public int Tier { get; init; }

        /// <summary>Stage I: the row of <see cref="Tier"/>; null without one.</summary>
        public EventTier? Row { get; init; }

        /// <summary>Stage I: the character that started it (.invasion, a ticket); null for a scheduled or a GM's event.</summary>
        public Guid? Starter { get; init; }

        /// <summary>Stage I: the name of <see cref="Starter"/>.</summary>
        public string StarterName { get; init; } = "";

        /// <summary>Its waves: the kind's, then the tier's ExtraWaves.</summary>
        public required IReadOnlyList<EventWave> Waves { get; init; }

        /// <summary>Stage I: × health and damage of its mobs when the faction lacks the mob tier and a nearer tier stands in (1.15 per tier).</summary>
        public double Scale { get; init; } = 1;

        /// <summary>Stage I: the level buff of its mobs (the tier's LevelBuff); null = none.</summary>
        public SpellTemplate? LevelBuff { get; init; }

        public TimeSpan StartedAt { get; init; }
        public TimeSpan EndsAt { get; init; }

        /// <summary>The wave on the map (0-based); −1 before the first.</summary>
        public int Wave { get; internal set; } = -1;

        /// <summary>When the next wave comes; null while one fights.</summary>
        public TimeSpan? NextWaveAt { get; internal set; }

        /// <summary>The living mobs of the current wave.</summary>
        public List<Mob> Mobs { get; } = [];

        /// <summary>The health each player took from its mobs, by character Guid.</summary>
        public Dictionary<Guid, double> Damage { get; } = [];

        public Outcome Outcome { get; internal set; }

        /// <summary>The reward chest after a win.</summary>
        public LootChest? Chest { get; internal set; }

        public string Cluster => Map.Cluster;

        public override string ToString() => $"event #{Id} {Type.Name} in {Cluster} at ({Point.X:0}, {Point.Y:0})";
    }

    private readonly List<Run> _running = [];

    /// <summary>
    /// Stage I: a run was won, with the characters it rewards (before its win line; the hook for season, PvP and guild goals, invasions
    /// SPEC Q5). Raised under <see cref="World.Sync"/>.
    /// </summary>
    public event Action<Run, IReadOnlyList<Guid>>? Won;
    private int _ids;
    private Random? _random;
    private TimeSpan? _nextScheduled;

    private EventSettings Settings => world.EventSettings;

    /// <summary>Its own random (the schedule, the ticket drops, Drops.Won): the maps' rolls stay as they were.</summary>
    internal Random Random => _random ??= world.MapSeed is { } seed ? new Random(seed + 7) : new Random();

    /// <summary>The events that run now.</summary>
    public IReadOnlyList<Run> Running => _running;

    /// <summary>When the schedule starts the next one (<see cref="World.Clock"/> time); null before the first tick or with Scheduled off.</summary>
    public TimeSpan? NextScheduled => _nextScheduled;

    /// <summary>The world tick: the schedule, then each event's waves, its end by time and its win.</summary>
    public void Update()
    {
        if (!Settings.Enabled) return;
        var now = world.Now;
        if (Settings.Scheduled)
        {
            _nextScheduled ??= now + Interval();
            if (now >= _nextScheduled && _running.Count(r => r.Scheduled) < Settings.MaxRunning)
            {
                var started = StartScheduled();
                // no cluster fits (no players out): look again in a minute (a start's next time is set again at its end)
                _nextScheduled = now + (started is null ? TimeSpan.FromMinutes(1) : Interval());
            }
        }
        foreach (var run in _running.ToList()) Step(run, now);
    }

    private TimeSpan Interval()
    {
        var (min, max) = Settings.IntervalMinutes;
        return TimeSpan.FromMinutes(min + (max - min) * Random.NextDouble());
    }

    /// <summary>A scheduled start: a fitting map with players, a kind by weight, a point near one of its players.</summary>
    private Run? StartScheduled()
    {
        var maps = world.Maps.Maps.Where(m => Fits(m) && m.Players.Count >= Settings.MinPlayers && _running.All(r => r.Map != m)
            && (Settings.Clusters.Count == 0 || Settings.Clusters.Contains(m.Cluster, StringComparer.OrdinalIgnoreCase))).ToList();
        var kinds = Settings.Events.Where(e => e.Weight > 0).ToList();
        if (maps.Count == 0 || kinds.Count == 0) return null;
        var map = maps[Random.Next(maps.Count)];
        var type = DungeonGenerator.Pick(Random, [.. kinds.Select(k => (k, k.Weight))]);
        var players = map.Players.Values.ToList();
        var near = players[Random.Next(players.Count)];
        try { return Start(map, type, PointNear(map, near.PositionAt(DateTime.UtcNow), type), Source.Scheduled); }
        catch (InvalidOperationException e)
        {
            Log.Info("world", $"world event {type.Name} in {map.Cluster}: {e.Message}");
            return null;
        }
    }

    /// <summary>Whether <paramref name="map"/> may have an event: a loaded open-world map with mob spawn blocks.</summary>
    public static bool Fits(Map map) =>
        map is { Grid: not null, Unloaded: false, Template: not null } && !DungeonLayout.IsInstance(map.Cluster) && !ExpeditionData.IsInstance(map.Cluster)
        && !CorruptedData.IsInstance(map.Cluster) && !HellgateData.IsInstance(map.Cluster) && !MistsIds.IsInstance(map.Cluster) && !DragonAreaIds.IsInstance(map.Cluster) && !HellDungeonIds.IsInstance(map.Cluster) && map.MobSlots.Any(s => s.Point.Block is not null);

    /// <summary>
    /// A mob spawn point of <paramref name="map"/> at the kind's Distance from <paramref name="from"/> (a random one in the ring, else the
    /// nearest beyond its min, else <paramref name="from"/> itself).
    /// </summary>
    public Vector2 PointNear(Map map, Vector2 from, EventType type) => PointNear(map, from, type.Distance);

    /// <summary>A mob spawn point of <paramref name="map"/> at <paramref name="distance"/> from <paramref name="from"/> (as above; stage I: a player's Player.Distance).</summary>
    public static Vector2 PointNear(Map map, Vector2 from, (double Min, double Max) distance)
    {
        var (min, max) = distance;
        var points = map.MobSlots.Select(s => s.Point.Position).Distinct().ToList();
        var ring = points.Where(p => Vector2.Distance(p, from) is var d && d >= min && d <= max).ToList();
        if (ring.Count > 0) return ring[map.Random.Next(ring.Count)];
        return points.Where(p => Vector2.Distance(p, from) >= min).OrderBy(p => Vector2.Distance(p, from)).FirstOrDefault(from);
    }

    /// <summary>
    /// Starts <paramref name="type"/> at <paramref name="point"/> on <paramref name="map"/>: the faction and mob pools from the map's spawn
    /// blocks, the start line, and the first wave at once. Throws <see cref="InvalidOperationException"/> when the map has no mobs for it.
    /// Stage I: with <paramref name="tier"/> 1-10 the mobs, waves and rewards of that row of Tiers (<see cref="Plan"/>), and a player
    /// <paramref name="starter"/> (.invasion, a ticket) named in the AnnounceCalled line before the start line.
    /// </summary>
    public Run Start(Map map, EventType type, Vector2 point, Source source = Source.Gm, int tier = 0, Player? starter = null)
    {
        if (!Settings.Enabled) throw new InvalidOperationException("world events are off (content/events.json Enabled)");
        if (!Fits(map)) throw new InvalidOperationException($"{map.Cluster} is no open-world map with mob spawns");
        if (_running.FirstOrDefault(r => r.Map == map) is { } other) throw new InvalidOperationException($"an event already runs on this map (#{other.Id})");
        var row = tier > 0 ? Settings.Tier(tier) : null;
        var (faction, pools, mobTier, scale) = Plan(map, point, row);
        SpellTemplate? buff = null;
        if (row?.LevelBuff is { } name && (buff = world.Spells?.Template(name)) is null)
            Combat.LogOnce("eventbuff:" + name, $"world event tier T{tier}: no spell {name} in spells.xml, its mobs get no level buff");
        var waves = row is null ? type.Waves : [.. type.Waves, .. row.ExtraWaves];
        var now = world.Now;
        var run = new Run
        {
            Id = ++_ids, Type = type, Map = map, Point = point, Faction = faction, Pools = pools, Source = source, StartedAt = now, Tier = tier, Row = row,
            Waves = waves, Scale = scale, LevelBuff = buff, Starter = starter?.Character.Id, StarterName = starter?.Character.Name ?? "",
            EndsAt = now + TimeSpan.FromMinutes(type.DurationMinutes + (row is null ? 0 : row.ExtraMinutes * row.ExtraWaves.Count)),
            Title = type.Title.Replace("{faction}", Words(faction.Replace("_CAMP", "", StringComparison.Ordinal)), StringComparison.Ordinal)
                .Replace("{tier}", (tier > 0 ? tier : mobTier).ToString(CultureInfo.InvariantCulture), StringComparison.Ordinal),
        };
        _running.Add(run);
        Log.Info("world", string.Create(CultureInfo.InvariantCulture,
            $"{run} started ({source}{(starter is null ? "" : $" by '{starter.Character.Name}'")}): {faction} T{mobTier}{(tier > 0 ? $" for difficulty T{tier}" : "")}{(scale != 1 ? $" x{scale:0.##}" : "")}{(buff is null ? "" : $" with {buff.Name}")}, {waves.Count} waves, {(run.EndsAt - now).TotalMinutes:0.#} min"));
        var called = starter is null ? "" : Fill(Settings.AnnounceCalled, run).Replace("{player}", starter.Character.Name, StringComparison.Ordinal);
        foreach (var p in Readers(run))
        {
            Tell(p, called);
            Tell(p, Fill(Settings.AnnounceStart, run)
                .Replace("{distance}", p.Map == map ? Vector2.Distance(p.PositionAt(DateTime.UtcNow), point).ToString("0", CultureInfo.InvariantCulture) : "?", StringComparison.Ordinal)
                .Replace("{direction}", p.Map == map ? Compass(point - p.PositionAt(DateTime.UtcNow)) : "?", StringComparison.Ordinal));
        }
        NextWave(run);
        return run;
    }

    /// <summary>
    /// The faction, the mob pools by wave kind, the mob tier and the health/damage scale of an event at <paramref name="point"/>; throws
    /// <see cref="InvalidOperationException"/> when there are none. Without a <paramref name="row"/> (scheduled, a GM's without a tier): the
    /// faction of the spawn block nearest the point and its mobs of the cluster's tier from every block of the map. With one (stage I,
    /// invasions SPEC §4.2): <see cref="TieredPools"/>.
    /// </summary>
    public (string Faction, Dictionary<WaveKind, List<(MobTemplate Mob, double Weight)>> Pools, int MobTier, double Scale) Plan(Map map, Vector2 point, EventTier? row)
    {
        var blocks = map.MobSlots.Where(s => s.Point.Block is not null).ToList();
        if (row is not null) return TieredPools(map, blocks, point, row);
        var tier = map.Template?.Name.Tier ?? 0;
        var nearest = blocks.Where(s => FactionOf(s.Point.Block!) is { Length: > 0 }).MinBy(s => Vector2.Distance(s.Point.Position, point))
            ?? throw new InvalidOperationException($"{map.Cluster} has no mob spawn block of a faction");
        var faction = FactionOf(nearest.Point.Block!);
        // every block of the faction (each one once), its mobs of the cluster's tier by their weights
        var pools = new Dictionary<WaveKind, List<(MobTemplate, double)>>();
        foreach (var block in blocks.Select(s => s.Point.Block!).Distinct())
            foreach (var e in block.Mobs)
            {
                if (e is not { Mob: { } mob, OnlyAsUpgrade: false, Weight: > 0 } || mob.Faction != faction || tier > 0 && mob.Tier != tier) continue;
                Add(pools, mob, e.Weight);
            }
        if (pools.Count == 0) throw new InvalidOperationException($"{map.Cluster}: no {faction} mob of T{tier} in its spawn blocks");
        return (faction, pools, tier, 1);
    }

    /// <summary>Adds <paramref name="mob"/> with <paramref name="weight"/> to the pool of its wave kind (summing a mob already there); false for a kind no wave takes.</summary>
    private static bool Add(Dictionary<WaveKind, List<(MobTemplate, double)>> pools, MobTemplate mob, double weight)
    {
        if (KindOf(mob) is not { } kind) return false;
        var list = pools.TryGetValue(kind, out var l) ? l : pools[kind] = [];
        var i = list.FindIndex(x => x.Item1 == mob);
        if (i < 0) list.Add((mob, weight));
        else list[i] = (mob, list[i].Item2 + weight);
        return true;
    }

    /// <summary>
    /// Stage I (invasions SPEC §4.2), the pools of a tier row: tm = its MobTier; the faction F = its Faction, else that of the spawn block
    /// nearest the point among the invasion factions (Player.Factions, their _CAMP too), else HERETIC. 1. the same family at tm: each F mob of
    /// the map's blocks as mobs.xml T{tm}_{family}, same wave kind, its block weight; 2. a kind still empty: every usable mobs.xml mob of F
    /// (or F_CAMP) at tm, weight 1 (<see cref="Usable"/>); 3. none and tm ≤ 3: HERETIC (the only faction with T1-T3 mobs); 4. none: the nearest
    /// tier the faction has (lower first), its mobs × 1.15 per tier missing (logged once).
    /// </summary>
    private (string, Dictionary<WaveKind, List<(MobTemplate, double)>>, int, double) TieredPools(Map map, List<MobSpawnSlot> blocks, Vector2 point, EventTier row)
    {
        var tm = row.MobTier;
        var invasion = Settings.Player;
        var near = blocks.Where(s => invasion.IsFaction(FactionOf(s.Point.Block!))).MinBy(s => Vector2.Distance(s.Point.Position, point));
        var faction = row.Faction ?? (near is null ? "HERETIC" : FactionOf(near.Point.Block!));
        var pools = Pools(blocks, faction, tm);
        if (pools.Count == 0 && tm <= 3 && faction != "HERETIC") pools = Pools(blocks, faction = "HERETIC", tm);
        if (pools.Count > 0) return (faction, pools, tm, 1);
        foreach (var t in Enumerable.Range(1, 7).SelectMany(d => new[] { tm - d, tm + d }).Where(t => t is >= 1 and <= 8))
        {
            pools = Pools(blocks, faction, t);
            if (pools.Count == 0) continue;
            var scale = Math.Pow(1.15, tm - t);
            Combat.LogOnce($"eventtier:{map.Cluster}:{faction}:{tm}", $"world event in {map.Cluster}: no {faction} mob of T{tm}, T{t} ones x{scale:0.##} instead");
            return (faction, pools, t, scale);
        }
        throw new InvalidOperationException($"{map.Cluster}: no {faction} mob of T{tm} or another tier");
    }

    /// <summary>Steps 1 and 2 of <see cref="TieredPools"/> for <paramref name="faction"/> at tier <paramref name="tm"/>.</summary>
    private Dictionary<WaveKind, List<(MobTemplate, double)>> Pools(List<MobSpawnSlot> blocks, string faction, int tm)
    {
        var pools = new Dictionary<WaveKind, List<(MobTemplate, double)>>();
        var mobs = world.Clusters?.Mobs;
        foreach (var block in blocks.Select(s => s.Point.Block!).Distinct())
            foreach (var e in block.Mobs)
            {
                if (e is not { Mob: { } mob, OnlyAsUpgrade: false, Weight: > 0 } || mob.Faction != faction || KindOf(mob) is not { } kind) continue;
                var family = mob.Name.Length > 3 && mob.Name[0] == 'T' && char.IsAsciiDigit(mob.Name[1]) && mob.Name[2] == '_' ? mob.Name[3..] : mob.Name;
                var same = mob.Tier == tm ? mob : mobs?.Find($"T{tm}_{family}");
                if (same is { } m && m.Tier == tm && KindOf(m) == kind) Add(pools, m, e.Weight);
            }
        var missing = FactionWide(faction, tm).Where(m => !pools.ContainsKey(KindOf(m)!.Value)).ToList();
        foreach (var m in missing) Add(pools, m, 1);
        return pools;
    }

    private readonly Dictionary<(string, int), List<MobTemplate>> _factionWide = [];

    /// <summary>The usable mobs.xml mobs of <paramref name="faction"/> (or its _CAMP) at tier <paramref name="tier"/> (cached).</summary>
    private List<MobTemplate> FactionWide(string faction, int tier)
    {
        if (_factionWide.TryGetValue((faction, tier), out var list)) return list;
        var camp = faction + "_CAMP";
        list = world.Clusters?.Mobs.All.Where(m => (m.Faction == faction || m.Faction == camp) && m.Tier == tier && Usable(m)).ToList() ?? [];
        return _factionWide[(faction, tier)] = list;
    }

    private static readonly string[] Unusable = ["_TEST", "DUMMY", "_RD_", "DUNGEON", "EXPEDITION", "_EXP_", "_TUT_", "_HELL", "MISTS", "TUTORIAL", "_EVENT_", "GATHERER"];

    /// <summary>A mob an invasion may take from mobs.xml (SPEC §4.2 step 2): a wave kind, a &lt;Loot&gt;, a T{n}_MOB_ name of none of the special kinds, moving, with fame.</summary>
    private static bool Usable(MobTemplate m) =>
        KindOf(m) is not null && m.Loot is not null && m.Name.StartsWith($"T{m.Tier}_MOB_", StringComparison.Ordinal) && m.MoveSpeed > 0 && m.Fame > 0
        && !Unusable.Any(u => m.Name.Contains(u, StringComparison.Ordinal));

    /// <summary>The faction a block's mobs have (the first mob's; mobs.xml faction).</summary>
    private static string FactionOf(SpawnBlock block) => block.Mobs.FirstOrDefault(e => e.Mob is not null)?.Mob!.Faction ?? "";

    /// <summary>The wave kind of <paramref name="mob"/>; null for those no wave takes (chests, critters, harmless animals).</summary>
    private static WaveKind? KindOf(MobTemplate mob) => mob.Category switch
    {
        "champion" => WaveKind.Champion,
        "miniboss" => WaveKind.Miniboss,
        "boss" => WaveKind.Boss,
        "" or "standard" or "trash" => WaveKind.Standard,
        _ => null,
    };

    /// <summary>The next wave of <paramref name="run"/>: its mobs around the point, announced, each after the nearest player.</summary>
    private void NextWave(Run run)
    {
        run.Wave++;
        run.NextWaveAt = null;
        var wave = run.Waves[run.Wave];
        var map = run.Map;
        // the kind, or the next lower one the blocks have (standard last)
        var kind = wave.Kind;
        while (!run.Pools.ContainsKey(kind) && kind > WaveKind.Standard) kind--;
        var pool = run.Pools.GetValueOrDefault(kind) ?? run.Pools.Values.First();
        var count = map.Random.Next(wave.Count.Min, wave.Count.Max + 1);
        // stage I: a tier's CountFactor, and more mobs for more players near the point (Player.GroupCountPerPlayer, at most GroupCountMax)
        if (run.Row is { } row)
        {
            var near = map.Players.Values.Count(p => Vector2.Distance(p.PositionAt(DateTime.UtcNow), run.Point) <= 60);
            var group = Math.Min(Settings.Player.GroupCountMax, 1 + Settings.Player.GroupCountPerPlayer * Math.Max(0, near - 1));
            count = Math.Max(1, (int)Math.Round(count * row.CountFactor * group));
        }
        var (health, damage) = (wave.HealthFactor * (run.Row?.HealthFactor ?? 1) * run.Scale, wave.DamageFactor * (run.Row?.DamageFactor ?? 1) * run.Scale);
        for (var i = 0; i < count; i++)
        {
            var mob = DungeonGenerator.Pick(map.Random, [.. pool]);
            var angle = 2 * Math.PI * (i + map.Random.NextDouble() * 0.5) / count;
            var offset = new Vector2((float)Math.Cos(angle), (float)Math.Sin(angle)) * (float)(run.Type.SpreadMeters * (0.5 + 0.5 * map.Random.NextDouble()));
            var facing = (float)(Math.Atan2(-offset.X, -offset.Y) * 180 / Math.PI); // facing the point (as .mob spawn faces the GM)
            var made = SpawnManager.SpawnAt(map, mob, run.Point + offset, facing, world, healthFactor: health, damageFactor: damage, worldEvent: run,
                levelBuff: run.LevelBuff, fameFactor: run.Row?.FameFactor ?? 1);
            run.Mobs.Add(made);
            // within its pursuitradius of its spot (its Home) only: one farther it would leash from on the way (most have 30-40 m)
            if (world.MobAi && Target(run, made.Position) is { } target
                && (mob.PursuitRadius <= 0 || Vector2.Distance(target.PositionAt(DateTime.UtcNow), made.Position) <= mob.PursuitRadius))
                made.AI.AddThreat(target, 1);
        }
        if (kind != wave.Kind) Combat.LogOnce($"event:{map.Cluster}:{run.Faction}:{wave.Kind}", $"{run}: no {wave.Kind} {run.Faction} mob in {map.Cluster}, {kind} instead");
        var first = run.Mobs.FirstOrDefault();
        Announce(run, Fill(Settings.AnnounceWave, run)
            .Replace("{count}", run.Mobs.Count.ToString(CultureInfo.InvariantCulture), StringComparison.Ordinal)
            .Replace("{mob}", first is null ? "?" : WorldBosses.DisplayName(first.Template), StringComparison.Ordinal));
        Log.Info("world", $"{run}: wave {run.Wave + 1} of {run.Waves.Count}, {string.Join(", ", run.Mobs.GroupBy(m => m.Template.Name).Select(g => $"{g.Key} x{g.Count()}"))}");
    }

    private static Player? Nearest(Map map, Vector2 at)
    {
        var utc = DateTime.UtcNow;
        return map.Players.Values.Where(p => p.Combat.Down is null).MinBy(p => Vector2.Distance(p.PositionAt(utc), at));
    }

    /// <summary>
    /// The first threat of a wave mob at <paramref name="at"/>: for a player's run (stage I) the nearest standing one of its starter and
    /// the starter's party on the map, else the nearest standing player within 60 m of the point, else none (so a run called near a city
    /// does not send its mobs at uninvolved players across the map); for the others the nearest standing player on the map.
    /// </summary>
    private Player? Target(Run run, Vector2 at)
    {
        if (run.Starter is not { } starter) return Nearest(run.Map, at);
        var utc = DateTime.UtcNow;
        var party = world.Parties.Of(starter)?.Members.Select(m => m.Id).ToHashSet() ?? [starter];
        party.Add(starter);
        var standing = run.Map.Players.Values.Where(p => p.Combat.Down is null).ToList();
        return standing.Where(p => party.Contains(p.Character.Id)).MinBy(p => Vector2.Distance(p.PositionAt(utc), at))
            ?? standing.Where(p => Vector2.Distance(p.PositionAt(utc), run.Point) <= 60).MinBy(p => Vector2.Distance(p.PositionAt(utc), at));
    }

    /// <summary>One event's tick: its dead mobs out, the time limit, the next wave or the win.</summary>
    private void Step(Run run, TimeSpan now)
    {
        if (run.Map.Unloaded || !world.Maps.Maps.Contains(run.Map))
        {
            End(run, Outcome.Failed, "its map was unloaded", announce: false);
            return;
        }
        run.Mobs.RemoveAll(m => m.Dead || m.Removed);
        if (now >= run.EndsAt)
        {
            End(run, Outcome.Failed, "its time is up");
            return;
        }
        if (run.Mobs.Count > 0) return;
        if (run.NextWaveAt is { } due)
        {
            if (now >= due) NextWave(run);
        }
        else if (run.Wave + 1 < run.Waves.Count) run.NextWaveAt = now + TimeSpan.FromSeconds(run.Type.WaveDelaySeconds);
        else Win(run);
    }

    /// <summary>
    /// <paramref name="attacker"/> took <paramref name="health"/> from a mob of <paramref name="run"/> (<see cref="Combat.Damage"/>, the
    /// damage up to the health it had): its share of the reward.
    /// </summary>
    public static void Damaged(Run run, Player attacker, double health)
    {
        // TODO(player bots, invasions SPEC §4.4): a bot attacker earns no share (no reward slot, no group count) once Player.IsBot exists.
        if (health > 0 && run.Outcome == Outcome.Running) run.Damage[attacker.Character.Id] = run.Damage.GetValueOrDefault(attacker.Character.Id) + health;
    }

    /// <summary>The characters with at least RewardMinShare of the event's damage (on its map or not), most damage first.</summary>
    public static List<Guid> Rewarded(Run run)
    {
        var total = run.Damage.Values.Sum();
        if (total <= 0) return [];
        return [.. run.Damage.Where(e => e.Value > 0 && e.Value >= run.Type.RewardMinShare * total).OrderByDescending(e => e.Value).Select(e => e.Key)];
    }

    /// <summary>
    /// The last wave is dead: the personal reward chest at the point, the fame, the win line. Stage I, with a tier row: at most
    /// Player.MaxRewarded players (most damage first), with PartyShare their party members within PartyShareMeters of the point too; the
    /// row's chest, loot tier, item tier and enchantment, its Silver (× Rates:Silver) and Fame (× Rates:Fame, a kill of the mob tier),
    /// and with chance Drops.Won a ticket in each one's chest roll.
    /// </summary>
    private void Win(Run run)
    {
        var map = run.Map;
        var players = Rewarded(run);
        if (players.Count == 0)
        {
            // gone with no player damage (a GM's .mob kill, other kills): no win, the retreat line
            End(run, Outcome.Failed, "its last wave went with no player damage");
            return;
        }
        var row = run.Row;
        if (row is not null) players = Shared(run, players);
        run.Chest = MakeChest(run);
        if (run.Chest is { } chest)
        {
            foreach (var id in players) chest.SetViewerState(id, 6, world.GameNow); // Unlocked for the rewarded alone, by character
            if (row is not null && Settings.Drops.Won > 0)
            {
                // The kind's ticket: one of any tier, else the one of the event's tier (4-8: Corrupted:MapsOpenDungeons' vows), else Drops:Item.
                var kind = Settings.Player.Items.Where(i => i.Kind.Equals(run.Type.Name, StringComparison.OrdinalIgnoreCase)).ToList();
                var ticket = (kind.FirstOrDefault(i => i.Tier == 0) ?? kind.FirstOrDefault(i => i.Tier == Math.Clamp(run.Tier, 4, 8)))?.Item ?? Settings.Drops.Item;
                foreach (var id in players)
                    if (Random.NextDouble() < Math.Min(1, Settings.Drops.Won * world.Rates.Loot)) chest.Bonus[id] = ticket;
            }
            map.AddObject(chest);
            map.Timers.At(world.Now + TimeSpan.FromMinutes(row?.ChestMinutes ?? run.Type.ChestMinutes), () => Combat.RemoveBag(chest, "the event chest's time is up"));
        }
        var tier = row is not null ? Math.Min(run.Tier, 8) : map.Template?.Name.Tier ?? 4;
        var fame = Rates.ScaleFame((long)Math.Round((row?.Fame ?? run.Type.RewardFame) * 10000), world.Rates.Fame);
        var silver = row is null ? 0 : (long)Math.Round(row.Silver * world.Rates.Silver) * 10000;
        foreach (var p in players.Select(world.Parties.Online).OfType<Player>())
        {
            if (fame > 0) Destiny.Award(p, fame, Destiny.Kill(p, tier), journals: false);
            Wallet.Credit(p, silver, $"{run.Title} won");
            world.GuildGoals.OnDungeon(p, run); // stage I: the weekly guild goals' dungeons, once per guild and event
        }
        Won?.Invoke(run, players);
        Announce(run, Fill(Settings.AnnounceWon, run).Replace("{count}", players.Count.ToString(CultureInfo.InvariantCulture), StringComparison.Ordinal));
        End(run, Outcome.Won, $"won by {players.Count} player(s){(run.Chest is { } c ? $", {c}" : ", no chest (see the log)")}", announce: false);
    }

    /// <summary>
    /// Stage I: the rewarded of a tiered run: the first Player.MaxRewarded of <paramref name="rewarded"/>, then (PartyShare) the online
    /// party members of those on the run's map within PartyShareMeters of the point (healers, tanks), up to MaxRewarded in all.
    /// </summary>
    private List<Guid> Shared(Run run, List<Guid> rewarded)
    {
        var s = Settings.Player;
        // the online damage dealers fill the cap first: one logged off gets no silver or fame (the payout is online only) and takes no slot
        var list = rewarded.Where(id => world.Parties.Online(id) is not null).Take(s.MaxRewarded).ToList();
        if (!s.PartyShare) return list;
        var utc = DateTime.UtcNow;
        foreach (var id in list.ToList())
            foreach (var member in world.Parties.Of(id)?.Members ?? [])
                if (list.Count < s.MaxRewarded && !list.Contains(member.Id) && world.Parties.Online(member.Id) is { } p && p.Map == run.Map
                    && Vector2.Distance(p.PositionAt(utc), run.Point) <= s.PartyShareMeters)
                    list.Add(member.Id);
        return list;
    }

    /// <summary>
    /// The reward chest: a chest of the kind's LootChestSpawnpoint (stage I: the first of the tier's Chest list that gives one) by its
    /// weights, with a rare state by weight and the tile of the cluster's biome, personal (LockedByQuest for a player without a share), its
    /// loot × RewardFactor (× the tier's LootFactor, at its LootTier, ItemTier and Enchant); null (logged once) when the spawnpoint, a
    /// chest or a tile is missing.
    /// </summary>
    private LootChest? MakeChest(Run run)
    {
        foreach (var name in run.Row is { Chest.Count: > 0 } r ? r.Chest : [run.Type.Chest])
            if (MakeChest(run, name) is { } chest) return chest;
        return null;
    }

    private LootChest? MakeChest(Run run, string name)
    {
        var map = run.Map;
        if (world.Clusters?.LootChestsIfLoaded is not { } data || !data.Spawnpoints.TryGetValue(name, out var behaviour))
        {
            Combat.LogOnce("eventchest:" + name, $"world event chest {name}: no lootchests.xml or no such LootChestSpawnpoint, no reward chest");
            return null;
        }
        var (biome, folder) = (map.Template?.Name.Biome, map.Template?.Name.TemplateFolder ?? "NONE");
        var choices = behaviour.Entries.Where(e => e.Weight > 0 && data.Chests.TryGetValue(e.Chest, out var c) && c.TileFor(biome, folder).Length > 0
            && c.States.Any(s => s.Weight > 0)).ToList();
        if (choices.Count == 0)
        {
            Combat.LogOnce("eventchest:" + name + biome, $"world event chest {name}: no chest with a tile for {biome}/{folder}, no reward chest");
            return null;
        }
        var template = data.Chests[DungeonGenerator.Pick(map.Random, [.. choices.Select(e => (e.Chest, e.Weight))])];
        var state = DungeonGenerator.Pick(map.Random, [.. template.States.Where(s => s.Weight > 0).Select(s => (s, s.Weight))]);
        var point = new LootChestSpawn
        {
            Key = new SpawnKey(map.Cluster, "event-chest", map.CustomSpawns++), Tile = "", Position = run.Point, Direction = 0,
            Component = new XElement("lootchestspawnpoint", new XAttribute("spawnbehaviour", name)), SpawnBehaviour = name, Behaviour = behaviour,
        };
        return new LootChest(world.ObjectIds.Next(), point, template, state, template.TileFor(biome, folder), 6, world.GameNow)
        {
            Personal = true, DefaultViewerState = 4, LootFactor = run.Type.RewardFactor * (run.Row?.LootFactor ?? 1),
            LootTier = run.Row?.LootTier, ItemTier = run.Row?.ItemTier, Enchant = run.Row?.Enchant ?? 0,
        };
    }

    /// <summary>.event stop (stage I: .invasion stop, <paramref name="by"/> its starter): <paramref name="run"/> ends now, its mobs go, no reward.</summary>
    public void Stop(Run run, string by = "a GM") => End(run, Outcome.Stopped, "stopped by " + by);

    /// <summary>Ends <paramref name="run"/>: its living mobs despawn (not after a win: none are left), the line, the next scheduled time.</summary>
    private void End(Run run, Outcome outcome, string why, bool announce = true)
    {
        run.Outcome = outcome;
        _running.Remove(run);
        foreach (var m in run.Mobs.ToList())
            if (!m.Dead && !m.Removed) SpawnManager.Despawn(m, world);
        run.Mobs.Clear();
        if (announce) Announce(run, Fill(outcome == Outcome.Stopped ? Settings.AnnounceStopped : Settings.AnnounceFailed, run));
        if (run.Scheduled && Settings.Scheduled) _nextScheduled = world.Now + Interval(); // the next one counts from this end
        Log.Info("world", $"{run} ended ({outcome}): {why}");
    }

    // ---- the lines ----

    /// <summary>Who reads an event's lines: the players of its cluster, or everyone in game (AnnounceToAll); stage I: and its starter's party wherever they are.</summary>
    private IEnumerable<Player> Readers(Run run)
    {
        var readers = world.Maps.Maps.Where(m => Settings.AnnounceToAll || m == run.Map).SelectMany(m => m.Players.Values);
        if (run.Starter is not { } starter) return readers;
        var party = (world.Parties.Of(starter)?.Members.Select(m => m.Id) ?? [starter]).Select(world.Parties.Online).OfType<Player>();
        return readers.Union(party);
    }

    private void Announce(Run run, string text)
    {
        if (text.Length == 0) return;
        Log.Info("world", $"announce: {text}");
        foreach (var p in Settings.AnnounceToAll ? world.Chat.Announce(text, Readers(run)) : Readers(run)) Tell(p, text); // stageJ: channel 7 to its members
    }

    /// <summary>A system chat line (SystemMessage, as the world bosses: a leading '@' would be taken for a localisation tag).</summary>
    internal static void Tell(Player p, string text)
    {
        if (text.Length > 0) p.Session.Send(new SystemMessageEvent { d0p = text.StartsWith('@') ? " " + text : text, d0q = 0 });
    }

    private static string Fill(string text, Run run) => text
        .Replace("{event}", run.Title, StringComparison.Ordinal)
        .Replace("{tier}", run.Tier.ToString(CultureInfo.InvariantCulture), StringComparison.Ordinal)
        .Replace("{cluster}", run.Cluster, StringComparison.Ordinal)
        .Replace("{x}", run.Point.X.ToString("0", CultureInfo.InvariantCulture), StringComparison.Ordinal)
        .Replace("{z}", run.Point.Y.ToString("0", CultureInfo.InvariantCulture), StringComparison.Ordinal)
        .Replace("{wave}", (run.Wave + 1).ToString(CultureInfo.InvariantCulture), StringComparison.Ordinal)
        .Replace("{waves}", run.Waves.Count.ToString(CultureInfo.InvariantCulture), StringComparison.Ordinal)
        .Replace("{minutes}", (run.EndsAt - run.StartedAt).TotalMinutes.ToString("0.#", CultureInfo.InvariantCulture), StringComparison.Ordinal); // a tier's extra minutes too

    /// <summary>"UNDEAD" → "Undead", "KEEPER" → "Keeper".</summary>
    private static string Words(string name) =>
        string.Join(' ', name.Split('_', StringSplitOptions.RemoveEmptyEntries).Select(w => char.ToUpperInvariant(w[0]) + w[1..].ToLowerInvariant()));

    /// <summary>
    /// The 8-point compass direction of <paramref name="d"/> (world x, z): +z north, +x east [infer: the north gate of Thetford and the
    /// south exit of 0204 above it are at +z, stage3 SPEC]; "here" within 1 m.
    /// </summary>
    public static string Compass(Vector2 d)
    {
        if (d.Length() < 1) return "here";
        var deg = TileMath.Mod360(Math.Atan2(d.X, d.Y) * 180 / Math.PI); // 0 north, 90 east
        return new[] { "north", "north-east", "east", "south-east", "south", "south-west", "west", "north-west" }[(int)Math.Round(deg / 45) % 8];
    }

    /// <summary>.event list: the running events, one line each (progress, damage shares, time left), and the next scheduled time.</summary>
    public IEnumerable<string> Describe()
    {
        var now = world.Now;
        foreach (var r in _running)
        {
            var total = r.Damage.Values.Sum();
            var wave = r.NextWaveAt is { } at ? string.Create(CultureInfo.InvariantCulture, $"wave {r.Wave + 2} in {(at - now).TotalSeconds:0} s")
                : $"wave {r.Wave + 1} of {r.Waves.Count}, {r.Mobs.Count} mob(s) left";
            yield return string.Create(CultureInfo.InvariantCulture,
                $"  #{r.Id} {r.Type.Name} ({r.Title}{(r.Starter is null ? "" : $", {r.Source} by '{r.StarterName}'")}) in {r.Cluster} at ({r.Point.X:0}, {r.Point.Y:0}): {wave}, {(r.EndsAt - now).TotalMinutes:0.#} min left, {r.Damage.Count} player(s) hit for {total:0}");
        }
        if (Settings is { Enabled: true, Scheduled: true } && _nextScheduled is { } next)
            yield return string.Create(CultureInfo.InvariantCulture, $"  next scheduled event in {Math.Max(0, (next - now).TotalMinutes):0.#} min");
    }
}
