using System.Globalization;
using System.Numerics;
using AlbionLocal.Photon;
using AlbionLocal.Server.Data;
using AlbionLocal.Server.Entities;
using AlbionLocal.Server.Game;
using AlbionLocal.Server.Protocol;

namespace AlbionLocal.Server.Systems;

/// <summary>A built Abyssal Depths floor, not yet registered (off the world thread): its Guid, mode, map, faction, the a9c layout with its bytes and the cluster from it.</summary>
public sealed record AbyssBuild(Guid Guid, HellDungeonMode Mode, HellDungeonMap Map, string Faction, HellDungeonLayout Layout, byte[] Bytes, ClusterTemplate Template)
{
    public string Id => HellDungeonIds.InstanceId(Guid);
}

/// <summary>
/// One Abyssal Depths floor instance ("@HELLDUNGEON@&lt;guid&gt;"): its build, the way back (the Hell Den it was entered from), its times from the
/// map (clusterclosetime: no new team after it; clustershutdownstart, clustershutdowndamagestart, clustershutdowntime: the client's shutdown and
/// everyone out), the teams inside with the time each came in and the arrival each shares.
/// </summary>
public sealed class AbyssInstance(AbyssBuild build)
{
    public AbyssBuild Build { get; } = build;
    public string Id => Build.Id;
    public HellDungeonMode Mode => Build.Mode;
    public HellDungeonMap Map => Build.Map;
    public int Level => Build.Map.Level;
    public int Tier => Build.Map.Tier;
    public ClusterTemplate Template => Build.Template;
    public byte[] Bytes => Build.Bytes;

    public required Arrival Back { get; init; }

    public required TimeSpan Created { get; init; }
    public required TimeSpan CloseAt { get; init; }
    public required TimeSpan ShutdownStartAt { get; init; }
    public required TimeSpan ShutdownAt { get; init; }

    /// <summary>GameTimeStamps of the shutdown (HellDungeonsPlayerJoinedInfo 622 keys 0-2).</summary>
    public long ShutdownEndStamp { get; init; }
    public long ShutdownStartStamp { get; init; }
    public long ShutdownDamageStamp { get; init; }

    /// <summary>The teams (party "party:&lt;id&gt;" or a lone character's Guid) with the clock time each first came in.</summary>
    public Dictionary<string, TimeSpan> Teams { get; } = new(StringComparer.Ordinal);

    /// <summary>Each team's arrival (one HellDungeonPlayerSpawnpoint per team [infer]).</summary>
    public Dictionary<string, Arrival> Arrivals { get; } = new(StringComparer.Ordinal);

    public HashSet<Guid> Entered { get; } = [];
    public Dictionary<Guid, TimeSpan> Arriving { get; } = [];
    public TimeSpan? EmptySince { get; set; }
    public bool Closed { get; set; }
    public bool WarnedShutdown { get; set; }

    public override string ToString() => $"abyssal depths {Mode.Name} {Map.Id} {Id}";
}

/// <summary>
/// The Abyssal Depths ("hell dungeons" in the client; analysis/work/stageR/abyss/SPEC.md, "SPEC" here): the Hell Dens' static entrances (638/639,
/// Open and Closed in turns of staticentranceopentimer/closetimer), their use (op 532: the client's checks mirrored, its return codes 21280-21287),
/// the way in after AbyssalDepths:EnterDelaySeconds (the team's floor-1 instance of the mode: its party's within citypartyentrancetimer, else a
/// shared open one, else a new one built off the world thread; the push {0 id, 3 a9c, 4 clustertype, 5 tier}; the blocker debuff), the floors'
/// exits (upward: out to the Hell Den; downward: into the next floor's instance, the same team rules with downwardpartyentrancetimer), the
/// resurrection shrines (standing only), the join info (622) and the shutdown (clustershutdowntime: everyone out), a relog and a dead instance.
/// Not run: souls, soul shrines and soul exits' use, keys and key chests, the chest exit, the treasures, the shutdown damage (SPEC §6).
/// Under <see cref="World.Sync"/>, except the builds.
/// </summary>
public sealed class AbyssalDepths(World world)
{
    /// <summary>Op 532's return codes (HellDungeonEntranceGui.DisplayUsageError 0x18105D840 / OnRegisterResponse 0x18105E110).</summary>
    public const short NotFound = 21280, OutOfRange = 21281, EntranceClosed = 21282, AlreadyEntering = 21283, Blocked = 21284, Disabled = 21285,
        ModeNotAllowed = 21286, NotEnoughItemPower = 21287;

    /// <summary>How near the entrance the player must stand [assume: the client checks its own interaction range (cnz.af 0x180B85E70); 8 m as the Mists' static entrance].</summary>
    public const float UseRange = 8;

    /// <summary>The blocker debuff's time (spells.xml HELL_DUNGEON_JOINED_FROM_STATIC_ENTRANCE_DEBUFF: cannotuseobject helldungeonstaticentrance 70 s; the _LONG one 300 s).</summary>
    public const double BlockSeconds = 70, BlockSecondsLong = 300;

    /// <summary>An instance empty this long (and past its arrivals' grace) closes [assume, as the dragon areas].</summary>
    public const double EmptySeconds = 300;

    private readonly List<AbyssEntrance> _entrances = [];
    private readonly Dictionary<string, AbyssInstance> _instances = new(StringComparer.Ordinal);

    /// <summary>The builds under way by "mode|level|team".</summary>
    private readonly Dictionary<string, Task<AbyssBuild>> _builds = new(StringComparer.Ordinal);

    /// <summary>An accepted use: the entrance, the mode, when the player goes in, the way back, the build it waits for.</summary>
    private sealed class Entry(AbyssEntrance entrance, HellDungeonMode mode, TimeSpan due, Arrival back)
    {
        public AbyssEntrance Entrance { get; } = entrance;
        public HellDungeonMode Mode { get; } = mode;
        public TimeSpan Due { get; } = due;
        public Arrival Back { get; } = back;
        public string? BuildKey { get; set; }
    }

    private readonly Dictionary<Player, Entry> _entries = [];
    private readonly Dictionary<Guid, TimeSpan> _blocked = [];

    private Random Rng => _rng ??= world.MapSeed is { } seed ? new Random(seed) : new Random();
    private Random? _rng;

    public IReadOnlyList<AbyssEntrance> Entrances => _entrances;
    public IReadOnlyCollection<AbyssInstance> Instances => _instances.Values;

    public AbyssInstance? Find(string? cluster) => cluster is not null && _instances.TryGetValue(cluster, out var i) && !i.Closed ? i : null;

    public bool IsLive(string cluster) => _instances.ContainsKey(cluster);

    /// <summary>Whether <paramref name="p"/> waits to go in (an accepted use).</summary>
    public bool Waiting(Player p) => _entries.ContainsKey(p);

    /// <summary>The builds under way (the self-test waits for them).</summary>
    public Task Building() => Task.WhenAll(_builds.Values.ToList());

    private HellDungeonData? Data
    {
        get
        {
            if (world.Clusters is not { } store) return null;
            try { return store.HellDungeons; }
            catch (Exception e) when (e is InvalidDataException or IOException)
            {
                Log.Warn("data", $"helldungeons.xml not read: {e.Message}");
                return null;
            }
        }
    }

    // ---- the city entrances (SPEC §2.1) ----

    /// <summary>
    /// At the fill of a map (MapSpawner): a Hell Den's helldungeonstaticentrance members as entrances (638), their state on the shared cycle
    /// (Open staticentranceopentimer, then Closed staticentranceclosetimer, from the world clock's start [assume: one phase for all]); a live
    /// instance's exits, resurrection shrines and mob level.
    /// </summary>
    public IEnumerable<WorldObject> Populate(Map map, ClusterTemplate template)
    {
        if (Find(map.Cluster) is { } instance) return Inside(map, instance, template);
        if (HellDungeonIds.IsInstance(map.Cluster)) return [];
        var made = new List<WorldObject>();
        foreach (var m in template.Critical.Where(m => m.Type == "helldungeonstaticentrance"))
        {
            var e = new AbyssEntrance(world.ObjectIds.Next(), m);
            Phase(e);
            _entrances.Add(e);
            made.Add(e);
        }
        return made;
    }

    /// <summary>The entrance's state and next change on the cycle (open, close) of helldungeons.xml at the world clock's time.</summary>
    private void Phase(AbyssEntrance e)
    {
        var (open, close) = (Data?.StaticOpenSeconds ?? 300, Data?.StaticCloseSeconds ?? 300);
        var period = Math.Max(1, open + close);
        var t = world.Clock.Now.TotalSeconds % period;
        var left = t < open ? open - t : period - t;
        e.State = t < open ? AbyssEntranceState.Open : AbyssEntranceState.Closed;
        e.ChangeAt = world.Clock.Now + TimeSpan.FromSeconds(left);
        e.NextChange = world.GameNow + TimeSpan.FromSeconds(left).Ticks;
    }

    /// <summary>.abyss open|close: the entrance's state set by a GM until its next regular change (639 to the observers).</summary>
    public void Force(AbyssEntrance e, AbyssEntranceState state, double seconds)
    {
        e.State = state;
        e.ChangeAt = world.Clock.Now + TimeSpan.FromSeconds(seconds);
        e.NextChange = world.GameNow + TimeSpan.FromSeconds(seconds).Ticks;
        e.Map?.SendToObservers(e, e.Update());
    }

    /// <summary>The clock time until which <paramref name="p"/> may not use a static entrance again (the blocker debuff); null = it may.</summary>
    public TimeSpan? BlockedUntil(Player p) => _blocked.TryGetValue(p.Character.Id, out var until) && world.Clock.Now < until ? until : null;

    /// <summary>
    /// HellDungeonUseStaticEntrance (op 532) {1 the entrance, 2 the mode's id}: the client's own checks (cnz.af(bum, out no) 0x180B85E70: the entrance
    /// and mode known, the player in range, the entrance not Closed) and the server's (AbyssalDepths:Enabled, the blocker debuff, the item power
    /// requirement of the mode, no other entry under way). Accepted: the entry is queued (<see cref="Update"/>). Returns the code (0 = accepted), the
    /// blocked-until and closed-until stamps (response keys 0, 1: shown with 21284 and 21282) and why (for the log).
    /// </summary>
    public (short Code, long BlockedUntil, long ClosedUntil, string? Why) Use(Player p, long entranceId, int modeId)
    {
        if (p.Map is not { } map || !map.Objects.TryGetValue(entranceId, out var o) || o is not AbyssEntrance entrance) return (NotFound, 0, 0, $"no abyssal depths entrance {entranceId}");
        if (!world.Config.AbyssalDepthsEnabled) return (Disabled, 0, 0, "disabled here (AbyssalDepths:Enabled)");
        if (Data is not { } data) return (NotFound, 0, 0, "helldungeons.xml is not read");
        if (data.ModeById(modeId) is not { } mode) return (ModeNotAllowed, 0, 0, $"mode id {modeId} names no mode ({data.Modes.Count} modes)");
        if (p.Combat.Down is { } down) return (AlreadyEntering, 0, 0, $"knocked down ({down.Kind})");
        var at = p.PositionAt(DateTime.UtcNow);
        var reach = UseRange + world.Config.Transfer.PositionTolerance;
        if (Vector2.Distance(at, entrance.Position) > reach)
            return (OutOfRange, 0, 0, string.Create(CultureInfo.InvariantCulture, $"{Vector2.Distance(at, entrance.Position):0.#} m from the entrance (at most {reach:0.#})"));
        if (entrance.State == AbyssEntranceState.Closed) return (EntranceClosed, 0, entrance.NextChange, "the entrance is closed");
        if (BlockedUntil(p) is { } until) return (Blocked, world.GameNow + (until - world.Clock.Now).Ticks, 0, "blocked by the static entrance's debuff");
        if (_entries.ContainsKey(p)) return (AlreadyEntering, 0, 0, "an entry is under way");
        if (world.Config.AbyssalDepthsCheckItemPower && mode.ItemPower > 0 && Expeditions.AverageItemPower(p) is var ip && ip < mode.ItemPower)
            return (NotEnoughItemPower, 0, 0, string.Create(CultureInfo.InvariantCulture, $"item power {ip:0} of {mode.ItemPower:0}"));
        var due = world.Clock.Now + TimeSpan.FromSeconds(world.Config.AbyssalDepthsEnterDelay);
        _entries[p] = new Entry(entrance, mode, due, new Arrival(map.Cluster, at, Transfers.Facing(entrance.Position, at)));
        Log.Info("world", string.Create(CultureInfo.InvariantCulture, $"'{p.Character.Name}' uses {entrance} for {mode.Name}: in {world.Config.AbyssalDepthsEnterDelay:0.#} s"));
        return (0, 0, 0, null);
    }

    // ---- the instances (SPEC §3) ----

    /// <summary>The team of <paramref name="p"/>: its party, else itself.</summary>
    public string TeamOf(Player p) => world.Parties.Of(p.Character.Id) is { } party ? $"party:{party.Id}" : p.Character.Id.ToString();

    /// <summary>
    /// The live instance a team going to floor <paramref name="level"/> of <paramref name="mode"/> joins: the one its team came into within
    /// <paramref name="window"/> seconds (citypartyentrancetimer, downwardpartyentrancetimer [infer: the party binding]); else (AbyssalDepths:SharedInstances)
    /// an open one before its close time with room under the map's soft player limit; else none.
    /// </summary>
    public AbyssInstance? Joinable(HellDungeonMode mode, int level, string team, double window)
    {
        var now = world.Clock.Now;
        var live = _instances.Values.Where(i => !i.Closed && i.Mode == mode && i.Level == level).ToList();
        if (live.FirstOrDefault(i => i.Teams.TryGetValue(team, out var since) && (now - since).TotalSeconds <= window && now < i.ShutdownStartAt) is { } bound) return bound;
        if (!world.Config.AbyssalDepthsShared) return null;
        return live.Where(i => now < i.CloseAt && (world.Maps.Find(i.Id)?.Players.Count ?? 0) + i.Arriving.Count < i.Map.SoftLimit).OrderBy(i => i.Created).FirstOrDefault();
    }

    /// <summary>The build of floor <paramref name="level"/> of <paramref name="mode"/> for <paramref name="team"/>, started or the one under way; null when the data has no such map.</summary>
    public Task<AbyssBuild>? Build(HellDungeonMode mode, int level, string team, out string key)
    {
        key = $"{mode.Name}|{level}|{team}";
        if (_builds.TryGetValue(key, out var running)) return running;
        if (world.Clusters is not { } store || Data is not { } data || data.MapAt(mode, level, Rng) is not { } map) return null;
        var faction = HellDungeonGenerator.PickFaction(data, Rng);
        var seed = Rng.Next();
        var guid = Guid.NewGuid();
        var task = Task.Run(() =>
        {
            var layout = HellDungeonGenerator.Build(store, data, mode, map, faction, HellDungeonIds.InstanceId(guid), new Random(seed));
            var template = store.FromLayout(layout);
            if (template.Problems.Count > 0) Log.Warn("data", $"abyssal depths {layout.Id}: {template.Problems[0]}");
            return new AbyssBuild(guid, mode, map, faction, layout, layout.Write(), template);
        });
        _builds[key] = task;
        Log.Info("world", $"abyssal depths: building {mode.Name} floor {level + 1} ({map.Id}) for {team}");
        return task;
    }

    /// <summary>A finished build registered as a live instance (once: a second call returns the same), its times counted from now.</summary>
    public AbyssInstance Register(AbyssBuild build, Arrival back)
    {
        if (_instances.TryGetValue(build.Id, out var known)) return known;
        foreach (var k in _builds.Where(kv => kv.Value.IsCompletedSuccessfully && kv.Value.Result == build).Select(kv => kv.Key).ToList()) _builds.Remove(k);
        var now = world.Clock.Now;
        var game = world.GameNow;
        var map = build.Map;
        var instance = new AbyssInstance(build)
        {
            Back = back, Created = now,
            CloseAt = now + TimeSpan.FromSeconds(map.CloseSeconds),
            ShutdownStartAt = now + TimeSpan.FromSeconds(map.ShutdownStartSeconds),
            ShutdownAt = now + TimeSpan.FromSeconds(Math.Max(60, map.ShutdownSeconds)),
            ShutdownStartStamp = game + TimeSpan.FromSeconds(map.ShutdownStartSeconds).Ticks,
            ShutdownDamageStamp = game + TimeSpan.FromSeconds(map.ShutdownDamageSeconds).Ticks,
            ShutdownEndStamp = game + TimeSpan.FromSeconds(Math.Max(60, map.ShutdownSeconds)).Ticks,
        };
        _instances[instance.Id] = instance;
        Log.Info("world", string.Create(CultureInfo.InvariantCulture,
            $"{instance}: {build.Layout.Templates.Count} templates, {build.Bytes.Length} layout bytes, T{instance.Tier} {build.Faction}; shuts down in {(instance.ShutdownAt - now).TotalMinutes:0} min; {build.Template.Summary()}"));
        return instance;
    }

    /// <summary>
    /// The arrival of <paramref name="team"/> in <paramref name="instance"/>: its own if it has one, else the next of the floor's HellDungeonPlayerSpawnpoint
    /// markers (bug static +0x10 names the marker; one per team in turn [infer]), else the cluster's default spawn.
    /// </summary>
    public Arrival ArrivalFor(AbyssInstance instance, string team)
    {
        if (instance.Arrivals.TryGetValue(team, out var a)) return a;
        var spawns = instance.Template.MistsSpots.Where(s => s.Kind == "HellDungeonPlayerSpawnpoint").Select(s => s.Position).Distinct().ToList();
        var at = spawns.Count == 0 ? Transfers.DefaultSpawn(instance.Template) : spawns[instance.Arrivals.Count % spawns.Count];
        var arrival = new Arrival(instance.Id, at, 0);
        instance.Arrivals[team] = arrival;
        return arrival;
    }

    /// <summary>
    /// <paramref name="p"/> is let into <paramref name="instance"/> as a member of <paramref name="team"/>: the team counted in, the player arriving until its
    /// Join, the map made and filled; from the city also the way back (<paramref name="back"/>) and the blocker debuff (staticentranceblockerdebuff, or its
    /// long variant under feature switch 155 HellDungeonUseLongBlocker, off). Returns where it arrives; the caller sends it there.
    /// </summary>
    public Arrival Admit(Player p, AbyssInstance instance, string team, Arrival? back)
    {
        var now = world.Clock.Now;
        instance.Teams.TryAdd(team, now);
        var at = ArrivalFor(instance, team);
        instance.Arriving[p.Character.Id] = now + TimeSpan.FromSeconds(world.DungeonSettings.ArrivalGraceSeconds);
        instance.Entered.Add(p.Character.Id);
        instance.EmptySince = null;
        if (back is { } b)
        {
            p.Character.DungeonReturn = new DungeonReturn(b.Cluster, b.Position.X, b.Position.Y, b.Direction);
            _blocked[p.Character.Id] = now + TimeSpan.FromSeconds(BlockSeconds);
            if (Data is { } data) Pvp.ApplySpell(p, data.BlockerDebuff, world.GameNow);
        }
        world.LoadTemplate(world.Maps.Get(instance.Id));
        Log.Info("world", string.Create(CultureInfo.InvariantCulture, $"'{p.Character.Name}' goes into {instance} ({team}) at ({at.Position.X:0.##}, {at.Position.Y:0.##}); way back {instance.Back.Cluster}"));
        return at;
    }

    /// <summary>Admit, then the push {0 id, 3 a9c, 4 the mode's clustertype, 5 the tier} (ciy.a_9's layout type 3 needs key 4 as the dragon areas' type 4).</summary>
    public void Send(Player p, AbyssInstance instance, string team, Arrival? back)
    {
        var at = Admit(p, instance, team, back);
        p.Session.Teleport(at, $"abyss:{instance.Id}", type: instance.Mode.ClusterType, layout: instance.Bytes, tier: instance.Tier);
    }

    /// <summary>
    /// A downward exit of <paramref name="from"/> taken by <paramref name="p"/> (SPEC §3.4): the next floor (difficulty level + 1 of the same map set): the
    /// instance its team joins (<see cref="Joinable"/> with downwardpartyentrancetimer), else its build. Why not, when the map set has no deeper floor.
    /// </summary>
    public (AbyssInstance? Ready, Task<AbyssBuild>? Build, string Team, string? Why) NextFloor(Player p, AbyssInstance from)
    {
        var team = TeamOf(p);
        var window = Data?.DownwardPartySeconds ?? 300;
        if (Joinable(from.Mode, from.Level + 1, team, window) is { } ready) return (ready, null, team, null);
        return Build(from.Mode, from.Level + 1, team, out _) is { } build ? (null, build, team, null)
            : (null, null, team, $"{from.Mode.MapSet} has no floor {from.Level + 2}");
    }

    // ---- inside (SPEC §2.3, §2.4) ----

    /// <summary>
    /// A floor's objects at its fill: its upward exits (633, usable at once [assume]: key 3 now), its downward exits (635: downwardexitsopenmin-max of them
    /// Open, the rest Closed, drawn per instance [infer: the map's attributes]), its soul exits (634, Closed: their use is not traced), its resurrection
    /// shrines (627) and the mob level of the mode's randomdungeons.xml difficulty group at the map's difficultylevel.
    /// </summary>
    private List<WorldObject> Inside(Map map, AbyssInstance instance, ClusterTemplate template)
    {
        var objects = new List<WorldObject>();
        var now = world.GameNow;
        var spots = template.MistsSpots;
        var downs = spots.Where(s => s.Kind == "helldungeondownwardexit").ToList();
        var open = downs.Count == 0 ? 0 : Math.Clamp(Rng.Next(instance.Map.DownwardOpenMin, Math.Max(instance.Map.DownwardOpenMin, instance.Map.DownwardOpenMax) + 1), 0, downs.Count);
        var opened = downs.OrderBy(_ => Rng.Next()).Take(open).ToHashSet();
        foreach (var s in spots)
            switch (s.Kind)
            {
                case "helldungeonupwardexit":
                    objects.Add(new AbyssExit(world.ObjectIds.Next(), s, instance.Back.Cluster) { Stamp = now });
                    break;
                case "helldungeondownwardexit":
                    objects.Add(new AbyssExit(world.ObjectIds.Next(), s, "") { Stamp = now, State = (int)(opened.Contains(s) ? AbyssDownwardState.Open : AbyssDownwardState.Closed) });
                    break;
                case "helldungeosoulexit":
                    objects.Add(new AbyssExit(world.ObjectIds.Next(), s, instance.Back.Cluster) { Stamp = now, State = 1 });
                    break;
                case "resurrectionshrine":
                    objects.Add(new AbyssResurrectionShrine(world.ObjectIds.Next(), s));
                    break;
            }
        if (instance.Mode.DifficultyGroup is { } g && world.Clusters?.DungeonsIfLoaded?.DifficultyGroups.GetValueOrDefault(g) is { } group)
            map.Difficulty = group.GetValueOrDefault(instance.Level);
        Log.Info("world", $"{instance}: {objects.OfType<AbyssExit>().Count(e => e.Upward)} upward, {open}/{downs.Count} downward open, "
            + $"{objects.OfType<AbyssResurrectionShrine>().Count()} shrine(s), {template.MobSpawnPoints.Count} mob spawn point(s)");
        return objects;
    }

    /// <summary>After JoinFinished in an instance: counted inside, HellDungeonsPlayerJoinedInfo (622) and the chat line.</summary>
    public void Joined(Player p)
    {
        if (p.Map is not { } map || Find(map.Cluster) is not { } instance) return;
        instance.Arriving.Remove(p.Character.Id);
        instance.Entered.Add(p.Character.Id);
        instance.EmptySince = null;
        p.Session.Send(JoinedInfo(instance));
        Expeditions.Tell(p, string.Create(CultureInfo.InvariantCulture,
            $"The Depths ({instance.Mode.Name}, floor {instance.Level + 1}, T{instance.Tier}): the shutdown begins in {Math.Max(0, (instance.ShutdownStartAt - world.Clock.Now).TotalMinutes):0} minutes."));
    }

    /// <summary>
    /// HellDungeonsPlayerJoinedInfo (622, a2q → ddy.n(g7s) 0x180AD5E90, the client's HellDungeonManager "OnPlayerJoined"): key 0 → ddy+0x20, key 1 → +0x18
    /// (the timer ddy.ac fires at: the shutdown's start, ddy.x), key 2 → +0x10; the shutdown fog's share (key 0 − key 2)/(key 0 − key 1). [infer] key 0 the
    /// shutdown's end (clustershutdowntime), key 1 its start (clustershutdownstart), key 2 the damage start (clustershutdowndamagestart); key 3 a c4 [assume: 0].
    /// </summary>
    public static HellDungeonsPlayerJoinedInfoEvent JoinedInfo(AbyssInstance i) => new() { d9l = i.ShutdownEndStamp, d9m = i.ShutdownStartStamp, d9n = i.ShutdownDamageStamp, d9o = 0 };

    // ---- the way out ----

    /// <summary>The way back of <paramref name="p"/> (its <see cref="Character.DungeonReturn"/>, else the instance's), cleared.</summary>
    public Arrival Out(Player p, AbyssInstance? instance)
    {
        var back = p.Character.DungeonReturn is { Cluster.Length: > 0 } b && !HellDungeonIds.IsInstance(b.Cluster) ? new Arrival(b.Cluster, new Vector2(b.X, b.Y), b.Direction)
            : instance?.Back ?? new Arrival(world.Config.Mists.FallbackCluster, Vector2.Zero, 0);
        p.Character.DungeonReturn = null;
        return back;
    }

    /// <summary>A character whose saved cluster is an instance that is gone (closed, or a restart): its way back, saved. False otherwise.</summary>
    public bool ReturnIfClosed(Character c)
    {
        if (!HellDungeonIds.IsInstance(c.Cluster) || Find(c.Cluster) is not null) return false;
        var to = c.DungeonReturn is { Cluster.Length: > 0 } b && !HellDungeonIds.IsInstance(b.Cluster) ? b : new DungeonReturn(world.Config.Mists.FallbackCluster, -40, -4, 0);
        (c.Cluster, c.PositionX, c.PositionY, c.Direction) = (to.Cluster, to.X, to.Y, to.Direction);
        c.PendingTransfer = null;
        c.DungeonReturn = null;
        world.Saves.SaveNow(c);
        Log.Info("world", $"'{c.Name}' was in an Abyssal Depths floor that is gone: back in {to.Cluster}");
        return true;
    }

    /// <summary>The instance closes (its shutdown, empty, a GM): everyone inside out to the way back (a dead one respawns); the map goes.</summary>
    public void Close(AbyssInstance instance, string why)
    {
        if (instance.Closed) return;
        instance.Closed = true;
        if (world.Maps.Find(instance.Id) is { } map)
        {
            foreach (var p in map.Players.Values.ToList())
            {
                if (p.Session.Status != Network.SessionStatus.InWorld || p.Session.Player != p)
                {
                    world.Maps.Remove(p, world.Clock.Now);
                    continue;
                }
                if (p.Combat.Down is { Kind: not KnockdownKind.Dead }) Death.Abandoned(p);
                if (p.Combat.Down is { Kind: KnockdownKind.Dead } && Death.Respawn(p, "abyssal depths closed")) continue;
                var to = Out(p, instance);
                Expeditions.Tell(p, $"The Depths close; you are back in {to.Cluster}.");
                p.Session.Teleport(to, $"abyss:{instance.Id}:closed");
            }
            world.Maps.Unload(map, force: true);
        }
        _instances.Remove(instance.Id);
        Log.Info("world", $"{instance} closed ({why})");
    }

    // ---- the timers ----

    /// <summary>
    /// Every tick: the entrances' cycle (639 at each change), the accepted uses at their time (into the joinable instance, else waiting for its build),
    /// the finished builds (registered, their waiting players sent in), then each instance's shutdown warning, shutdown and empty close.
    /// </summary>
    public void Update()
    {
        var now = world.Clock.Now;
        var data = world.Clusters?.HellDungeonsIfLoaded;
        foreach (var e in _entrances.ToList())
        {
            if (e.Map is not { } map || map.Unloaded) { _entrances.Remove(e); continue; }
            if (now < e.ChangeAt) continue;
            var next = e.State == AbyssEntranceState.Open ? AbyssEntranceState.Closed : AbyssEntranceState.Open;
            var seconds = next == AbyssEntranceState.Open ? data?.StaticOpenSeconds ?? 300 : data?.StaticCloseSeconds ?? 300;
            e.State = next;
            e.ChangeAt = now + TimeSpan.FromSeconds(seconds);
            e.NextChange = world.GameNow + TimeSpan.FromSeconds(seconds).Ticks;
            map.SendToObservers(e, e.Update());
        }
        foreach (var (p, entry) in _entries.ToList())
        {
            if (p.Session.Player != p || p.Map is null || p.Map != entry.Entrance.Map || p.Combat.Down is not null) { _entries.Remove(p); continue; }
            if (now < entry.Due || p.Session.Status != Network.SessionStatus.InWorld) continue;
            var team = TeamOf(p);
            if (Joinable(entry.Mode, 0, team, data?.CityPartySeconds ?? 60) is { } joined)
            {
                _entries.Remove(p);
                Send(p, joined, team, entry.Back);
                continue;
            }
            if (entry.BuildKey is null)
            {
                if (Build(entry.Mode, 0, team, out var key) is null)
                {
                    _entries.Remove(p);
                    Expeditions.Tell(p, $"The Depths cannot be built: {entry.Mode.MapSet} has no first floor.");
                    continue;
                }
                entry.BuildKey = key;
            }
            if (!_builds.TryGetValue(entry.BuildKey, out var build) || !build.IsCompleted) continue;
            _entries.Remove(p);
            if (!build.IsCompletedSuccessfully)
            {
                _builds.Remove(entry.BuildKey);
                Log.Warn("world", $"abyssal depths not built: {build.Exception?.InnerException?.Message}");
                Expeditions.Tell(p, "The Depths could not be built; try again.");
                continue;
            }
            Send(p, Register(build.Result, entry.Back), team, entry.Back);
        }
        foreach (var (key, build) in _builds.Where(kv => kv.Value.IsFaulted || kv.Value.IsCanceled).ToList())
        {
            Log.Warn("world", $"abyssal depths build {key} failed: {build.Exception?.InnerException?.Message}");
            _builds.Remove(key);
        }
        foreach (var instance in _instances.Values.ToList())
        {
            var map = world.Maps.Find(instance.Id);
            if (now >= instance.ShutdownAt)
            {
                Close(instance, "shutdown");
                continue;
            }
            if (!instance.WarnedShutdown && now >= instance.ShutdownStartAt)
            {
                instance.WarnedShutdown = true;
                foreach (var p in map?.Players.Values ?? Enumerable.Empty<Player>())
                    Expeditions.Tell(p, string.Create(CultureInfo.InvariantCulture, $"The Depths collapse: everyone is thrown out in {(instance.ShutdownAt - now).TotalSeconds:0} seconds."));
            }
            foreach (var late in instance.Arriving.Where(a => now >= a.Value).Select(a => a.Key).ToList()) instance.Arriving.Remove(late);
            if ((map?.Players.Count ?? 0) > 0 || instance.Arriving.Count > 0) instance.EmptySince = null;
            else instance.EmptySince ??= now;
            if (instance.EmptySince is { } since && now - since >= TimeSpan.FromSeconds(EmptySeconds)) Close(instance, $"empty for {EmptySeconds:0} s");
        }
    }

    /// <summary>.abyss list: the entrances and the live instances.</summary>
    public IEnumerable<string> Describe()
    {
        var now = world.Clock.Now;
        foreach (var e in _entrances)
            yield return string.Create(CultureInfo.InvariantCulture, $"  entrance {e.Id} on {e.Map?.Cluster}: {e.State}, changes in {Math.Max(0, (e.ChangeAt - now).TotalSeconds):0} s");
        foreach (var i in _instances.Values)
            yield return string.Create(CultureInfo.InvariantCulture,
                $"  {i.Id} {i.Mode.Name} {i.Map.Id} (floor {i.Level + 1}) T{i.Tier}: {world.Maps.Find(i.Id)?.Players.Count ?? 0} inside, {i.Teams.Count} team(s), shuts down in {Math.Max(0, (i.ShutdownAt - now).TotalMinutes):0} min, back to {i.Back.Cluster}");
    }
}
