using System.Globalization;
using System.Numerics;
using AlbionLocal.Photon;
using AlbionLocal.Server.Data;
using AlbionLocal.Server.Entities;
using AlbionLocal.Server.Game;
using AlbionLocal.Server.Protocol;

namespace AlbionLocal.Server.Systems;

/// <summary>A built dragon area instance, not yet registered (off the world thread): its Guid, mode, map, faction, the a88 layout with its bytes and the cluster from it.</summary>
public sealed record DragonAreaBuild(Guid Guid, DragonAreaMode Mode, DragonAreaMap Map, string Faction, DragonAreaLayout Layout, byte[] Bytes, ClusterTemplate Template)
{
    public string Id => DragonAreaIds.InstanceId(Guid);
}

/// <summary>
/// One dragon area instance ("@DRAGONAREA@&lt;guid&gt;"): its build, the way back (in front of the entrance it was entered by), its times from the
/// map (clusterclosetime: no newcomer after it; clustershutdowntime: everyone out), who was sent in, and the arrival its team shares.
/// </summary>
public sealed class DragonAreaInstance(DragonAreaBuild build)
{
    public DragonAreaBuild Build { get; } = build;
    public string Id => Build.Id;
    public DragonAreaMode Mode => Build.Mode;
    public DragonAreaMap Map => Build.Map;
    public int Tier => Build.Map.Tier;
    public ClusterTemplate Template => Build.Template;
    public byte[] Bytes => Build.Bytes;

    /// <summary>Where the exits lead and a death or the shutdown sends the players: the entrance's cluster, in front of it.</summary>
    public required Arrival Back { get; init; }

    public required TimeSpan Created { get; init; }
    public required TimeSpan CloseAt { get; init; }
    public required TimeSpan ShutdownAt { get; init; }

    /// <summary>The team's arrival (an exit of the instance, chosen at the first send).</summary>
    public Arrival? Arrival { get; set; }

    public HashSet<Guid> Entered { get; } = [];
    public Dictionary<Guid, TimeSpan> Arriving { get; } = [];
    public TimeSpan? EmptySince { get; set; }
    public bool Closed { get; set; }
    public bool WarnedAlert { get; set; }

    public override string ToString() => $"dragon area {Mode.Name} {Map.Id} {Id}";
}

/// <summary>
/// Dragon areas, the Ancient Lands (analysis/work/stageR/dragon-areas/SPEC.md, "SPEC" here). Part one: the open-world entrance (694) with its life
/// cycle. Part two: the entrances drawn by treasuredistribution.xml on the tracking spots (<see cref="Populate"/>), the use (549: the
/// UsingDragonAreaEntranceStateUpdate 699 Channeling, then WaitingToEnter, which opens the client's matchmaking window), the entry (550: the team
/// of the party waiting at the entrance, the mode of its size, the a88 instance built off the world thread, the ChangeCluster push
/// {0 id, 3 a88, 4 the mode's clustertype, 5 the tier}), the instance's exits (695, ExitEnterStart 237 → out in front of the entrance), the
/// shutdown, a relog and a dead instance. Not run: the main quest, dragon energy, teleporter keys, the boss and the treasures (SPEC §6).
/// Under <see cref="World.Sync"/>, except the builds.
/// </summary>
public sealed class DragonAreas(World world)
{
    /// <summary>The channel of the use before the player waits to enter [assume: no data names it; the client takes the server's stamps].</summary>
    public const double UseChannelSeconds = 3;

    /// <summary>An instance empty this long (and past its arrivals' grace) closes [assume].</summary>
    public const double EmptySeconds = 300;

    /// <summary>The way back stands this far in front (south) of the entrance [assume].</summary>
    public const float BackOffset = 4;

    /// <summary>UsingDragonAreaEntranceState (Invalid, Channeling, WaitingToEnter, Locked) and am6.UpdateType (Active, Canceled, Succeeded, Reconnect).</summary>
    public const byte StateInvalid = 0, StateChanneling = 1, StateWaiting = 2, UpdateActive = 0, UpdateCanceled = 1, UpdateSucceeded = 2;

    private readonly List<DragonAreaEntrance> _entrances = [];
    private readonly Dictionary<string, DragonAreaInstance> _instances = new(StringComparer.Ordinal);
    private readonly List<Task> _builds = [];

    /// <summary>A player using an entrance: its state and stamps, and the clock time its channel ends.</summary>
    public sealed class Using(DragonAreaEntrance entrance)
    {
        public DragonAreaEntrance Entrance { get; } = entrance;
        public byte State { get; set; } = StateChanneling;
        public long Start { get; set; }
        public long End { get; set; }
        public TimeSpan Due { get; set; }
    }

    private readonly Dictionary<Player, Using> _using = [];

    /// <summary>A build under way for a team at an entrance.</summary>
    private sealed record PendingEntry(Task<DragonAreaBuild> Build, List<Player> Team, DragonAreaEntrance Entrance, Arrival Back, TimeSpan Since);

    private readonly List<PendingEntry> _pending = [];

    /// <summary>An exit channel: the exit, its due time, where it began.</summary>
    private readonly Dictionary<Player, (DragonAreaExit Exit, TimeSpan Due, Vector2 From)> _exiting = [];

    private Random Rng => _rng ??= world.MapSeed is { } seed ? new Random(seed) : new Random();
    private Random? _rng;

    /// <summary>The live entrances.</summary>
    public IReadOnlyList<DragonAreaEntrance> Entrances => _entrances;

    /// <summary>The live instances.</summary>
    public IReadOnlyCollection<DragonAreaInstance> Instances => _instances.Values;

    /// <summary>The live instance of id <paramref name="cluster"/>; null for any other cluster or a closed one.</summary>
    public DragonAreaInstance? Find(string? cluster) => cluster is not null && _instances.TryGetValue(cluster, out var i) && !i.Closed ? i : null;

    public bool IsLive(string cluster) => _instances.ContainsKey(cluster);

    /// <summary>The use state of <paramref name="p"/>, or null.</summary>
    public Using? UsingOf(Player p) => _using.GetValueOrDefault(p);

    /// <summary>The builds under way (the self-test waits for them).</summary>
    public Task Building()
    {
        _builds.RemoveAll(t => t.IsCompleted);
        return Task.WhenAll(_builds);
    }

    // ---- the entrances ----

    /// <summary>
    /// A new entrance of <paramref name="typeName"/> on <paramref name="map"/> at <paramref name="at"/>: Open for the type's lifetime, then Closed for
    /// its despawntime [infer: the entrance type's times], then gone. <paramref name="add"/> false: not put on the map (a map's fill adds it).
    /// Null with the reason when the data or the type is missing or the map has no template.
    /// </summary>
    public (DragonAreaEntrance? Entrance, string? Why) Spawn(Map map, Vector2 at, float direction, string typeName, bool add = true)
    {
        if (world.Clusters is not { } store) return (null, "no client data");
        DragonAreaData data;
        try { data = store.DragonAreas; }
        catch (Exception e) when (e is InvalidDataException or IOException) { return (null, $"dragonarea.xml not read: {e.Message}"); }
        if (data.Entrance(typeName) is not var (type, index)) return (null, $"no entrance type {typeName} ({string.Join(", ", data.EntranceTypes.Select(t => t.Name))})");
        if (add && map.Grid is null) return (null, $"{map.Cluster} is not loaded");
        var spawn = new DragonAreaEntranceSpawn
        {
            Key = new SpawnKey(map.Cluster, "dragonentrance", map.CustomSpawns++), Tile = type.Tile, Position = at, Direction = direction,
            Component = new System.Xml.Linq.XElement("dragonareaentrance"),
        };
        var now = world.Clock.Now;
        var entrance = new DragonAreaEntrance(world.ObjectIds.Next(), spawn, type, index)
        {
            State = DragonAreaEntranceState.Open,
            StateEnd = world.GameNow + TimeSpan.FromSeconds(type.LifeSeconds).Ticks,
            CloseAt = now + TimeSpan.FromSeconds(type.LifeSeconds),
            RemoveAt = now + TimeSpan.FromSeconds(type.LifeSeconds + type.DespawnSeconds),
        };
        if (add) map.AddObject(entrance);
        _entrances.Add(entrance);
        Log.Info("world", $"{entrance} on {map.Cluster} at ({at.X:0.#}, {at.Y:0.#}): open for {type.LifeSeconds:0} s");
        return (entrance, null);
    }

    /// <summary>
    /// At the fill of a map (MapSpawner): a live instance's exits (695) and its mob level (randomdungeons.xml group of the mode, level of the map);
    /// an open-world map's entrances by DragonAreas:NaturalSpawn (<see cref="Natural"/>).
    /// </summary>
    public IEnumerable<WorldObject> Populate(Map map, ClusterTemplate template)
    {
        if (Find(map.Cluster) is { } instance)
        {
            var exits = template.Critical.Where(m => m.Type == "dragonareareexit").Select(m => (WorldObject)new DragonAreaExit(world.ObjectIds.Next(), m, instance.Back.Cluster)).ToList();
            if (instance.Mode.DifficultyGroup is { } g && world.Clusters?.DungeonsIfLoaded?.DifficultyGroups.GetValueOrDefault(g) is { } group)
                map.Difficulty = group.GetValueOrDefault(instance.Map.Level);
            Log.Info("world", $"{instance}: {exits.Count} exit(s), {template.MobSpawnPoints.Count} mob spawn point(s)");
            return exits;
        }
        return DragonAreaIds.IsInstance(map.Cluster) ? [] : Natural(map, template);
    }

    /// <summary>
    /// DragonAreas:NaturalSpawn: on an open-world map (Royal, Outlands) the DRAGONAREA_ENTRANCE events of treasuredistribution.xml for its continent,
    /// type and tier: each draws once at the fill (its activationchance, then an entrance type or &lt;None&gt; by weight) and puts the entrance on a free
    /// tracking spot (TreasureSpawnpointTracking prefab) at least minimumspawndistance (120 m) from the other entrances [infer: bzm.abw +0x98]; with
    /// DragonAreas:RedrawMinutes n the event draws again n minutes after its entrance is gone or it drew nothing.
    /// </summary>
    private List<WorldObject> Natural(Map map, ClusterTemplate template)
    {
        var made = new List<WorldObject>();
        if (!world.Config.DragonAreaNaturalSpawn || world.Clusters is not { } store || store.TreasuresIfLoaded is not { } treasures || store.DragonAreasIfLoaded is null) return made;
        if (ClusterNameInfo.Parse(template.Info.File) is not { Type: 0 } cni || cni.Continent is not (1 or 2)
            || !ClusterNameInfo.ContinentNames.TryGetValue(cni.Continent, out var continent)) return made;
        var spots = template.Resources?.Placements.Where(p => p.Name == ClusterTemplate.TrackingPrefab).Select(p => (p.Position, p.NodeDirection)).ToList() ?? [];
        if (spots.Count == 0) return made;
        var events = treasures.DragonEntranceEventsFor(continent, template.Info.Type, template.Name.Tier ?? cni.Tier);
        foreach (var ev in events)
            for (var i = 0; i < Math.Max(1, ev.Slots); i++)
                if (Draw(map, ev, spots, add: false) is { } e) made.Add(e);
        if (events.Count > 0)
            Log.Info("world", $"map {map.Cluster}: {spots.Count} tracking spot(s), {events.Count} dragon area entrance event(s); {made.Count} entrance(s) now");
        return made;
    }

    private DragonAreaEntrance? Draw(Map map, DragonEntranceEvent ev, IReadOnlyList<(Vector2 Position, float Direction)> spots, bool add)
    {
        var rng = Rng; // the system's own: the map's draws (mobs, treasures) stay as without dragon areas
        DragonAreaEntrance? placed = null;
        if (rng.NextDouble() < ev.Chance)
        {
            var options = ev.Entries.Select(e => ((string?)e.Type, e.Weight)).ToList();
            if (ev.NoneWeight > 0) options.Add((null, ev.NoneWeight));
            var min = 120f;
            if (options.Count > 0 && DungeonGenerator.Pick(rng, options) is { } type
                && spots.Where(s => _entrances.Where(x => x.Map == map || x.Map is null).All(x => Vector2.Distance(x.Position, s.Position) >= min)).ToList() is { Count: > 0 } free)
            {
                var spot = free[rng.Next(free.Count)];
                placed = Spawn(map, spot.Position, spot.Direction, type, add).Entrance;
                if (placed is not null) placed.Origin = (ev, spots);
            }
        }
        if (placed is null) Redraw(map, ev, spots);
        return placed;
    }

    /// <summary>DragonAreas:RedrawMinutes n: the event draws again n minutes later (0: never, neverreschedule).</summary>
    private void Redraw(Map map, DragonEntranceEvent ev, IReadOnlyList<(Vector2, float)> spots)
    {
        if (world.Config.DragonAreaRedrawMinutes <= 0) return;
        map.Timers.At(world.Now + TimeSpan.FromMinutes(world.Config.DragonAreaRedrawMinutes), () =>
        {
            if (!map.Unloaded && map.Grid is not null) Draw(map, ev, spots, add: true);
        });
    }

    // ---- the use and the entry (SPEC §3) ----

    /// <summary>
    /// UseDragonAreaEntrance 549 with key 2 false, its checks passed: the player channels (699 {Active, Channeling, the entrance, now, the end}),
    /// then waits to enter (<see cref="Update"/>: 699 WaitingToEnter, which opens the client's DragonAreaMatchmakingGui: cvo.f5(am6) 0x1809D0490).
    /// </summary>
    public void Use(Player p, DragonAreaEntrance entrance)
    {
        var now = world.GameNow;
        var u = new Using(entrance) { Start = now, End = now + TimeSpan.FromSeconds(UseChannelSeconds).Ticks, Due = world.Now + TimeSpan.FromSeconds(UseChannelSeconds) };
        _using[p] = u;
        Tell699(p, u, UpdateActive);
        Log.Info("world", $"'{p.Character.Name}' uses {entrance}: channelling {UseChannelSeconds:0.#} s");
    }

    /// <summary>549 with key 2 true (the window's Abort, cvo.ah6 0), a move away, the entrance gone: 699 {Canceled, Invalid}. False when none ran.</summary>
    public bool Cancel(Player p, string why)
    {
        if (!_using.Remove(p, out var u)) return false;
        u.State = StateInvalid;
        Tell699(p, u, UpdateCanceled);
        Log.Info("world", $"'{p.Character.Name}' stops using {u.Entrance} ({why})");
        return true;
    }

    private static void Tell699(Player p, Using u, byte update) => p.SendAround(new UsingDragonAreaEntranceStateUpdateEvent
    {
        ddn = p.Id, b3t = update, b3u = u.State, b3v = u.Entrance.Id, b3w = u.Start, b3x = u.End,
    });

    /// <summary>The mode of entrance type <paramref name="type"/> for a team of <paramref name="size"/> (minteamsize ≤ size ≤ maxteamsize) [infer: 549 names no mode].</summary>
    public static DragonAreaMode? ModeFor(DragonAreaData data, DragonEntranceType type, int size) =>
        type.Modes.Select(m => data.Modes.GetValueOrDefault(m)).OfType<DragonAreaMode>().FirstOrDefault(m => m.MinTeam <= size && size <= m.MaxTeam);

    /// <summary>
    /// EnterDragonAreaEntrance 550 by <paramref name="p"/>, waiting at <paramref name="entrance"/>: the team (the player and the members of its party
    /// waiting at the same entrance, up to the largest mode), the mode of its size, then the build; the push follows in <see cref="Update"/>.
    /// Returns why not, or null.
    /// </summary>
    public string? Enter(Player p, DragonAreaEntrance entrance)
    {
        if (!world.Config.DragonAreasEnabled) return "dragon areas are disabled here (DragonAreas:Enabled)";
        if (world.Clusters is not { } store || store.DragonAreasIfLoaded is not { } data) return "dragonarea.xml is not read";
        if (_using.GetValueOrDefault(p) is not { State: StateWaiting } u || u.Entrance != entrance) return "not waiting at this entrance";
        if (_pending.Any(x => x.Team.Contains(p))) return "already entering";
        var party = world.Parties.Of(p.Character.Id);
        var team = new List<Player> { p };
        if (party is not null)
            team.AddRange(_using.Where(kv => kv.Key != p && kv.Value.Entrance == entrance && kv.Value.State == StateWaiting && kv.Key.Map == p.Map
                && world.Parties.Of(kv.Key.Character.Id) == party && !_pending.Any(x => x.Team.Contains(kv.Key))).Select(kv => kv.Key));
        if (ModeFor(data, entrance.Type, team.Count) is not { } mode)
            return $"no mode of {entrance.Type.Name} for a team of {team.Count} ({string.Join(", ", entrance.Type.Modes)})";
        if (DragonAreaGenerator.PickMap(data, mode, Rng) is not { } map) return $"mode {mode.Name}: map set {mode.MapSet} has no map";
        var faction = DragonAreaGenerator.PickFaction(data, Rng);
        var seed = Rng.Next();
        var guid = Guid.NewGuid();
        var task = Task.Run(() =>
        {
            var layout = DragonAreaGenerator.Build(store, data, mode, map, faction, DragonAreaIds.InstanceId(guid), new Random(seed));
            var template = store.FromLayout(layout);
            if (template.Problems.Count > 0) Log.Warn("data", $"dragon area {layout.Id}: {template.Problems[0]}");
            return new DragonAreaBuild(guid, mode, map, faction, layout, layout.Write(), template);
        });
        _builds.Add(task);
        var cluster = entrance.Map?.Cluster ?? p.Map!.Cluster;
        var back = new Arrival(cluster, entrance.Position + new Vector2(0, -BackOffset), 0);
        _pending.Add(new PendingEntry(task, team, entrance, back, world.Now));
        Log.Info("world", $"'{p.Character.Name}' enters {entrance} with {team.Count} player(s): {mode.Name}, map {map.Id}; building");
        return null;
    }

    /// <summary>The build is in: registered, the team sent in (699 Succeeded, the way back kept, the push).</summary>
    private void Finish(PendingEntry entry)
    {
        var build = entry.Build.Result;
        var now = world.Clock.Now;
        var instance = new DragonAreaInstance(build)
        {
            Back = entry.Back, Created = now,
            CloseAt = now + TimeSpan.FromSeconds(build.Map.CloseSeconds), ShutdownAt = now + TimeSpan.FromSeconds(Math.Max(60, build.Map.ShutdownSeconds)),
        };
        _instances[instance.Id] = instance;
        Log.Info("world", string.Create(CultureInfo.InvariantCulture,
            $"{instance}: {build.Layout.Templates.Count} templates, {build.Bytes.Length} layout bytes, T{instance.Tier} {build.Faction}; shuts down in {(instance.ShutdownAt - now).TotalMinutes:0} min; {build.Template.Summary()}"));
        foreach (var p in entry.Team)
        {
            if (p.Session.Player != p || p.Map is null || p.Session.Status != Network.SessionStatus.InWorld) continue;
            if (_using.Remove(p, out var u))
            {
                u.State = StateWaiting;
                Tell699(p, u, UpdateSucceeded);
            }
            p.Character.DungeonReturn = new DungeonReturn(instance.Back.Cluster, instance.Back.Position.X, instance.Back.Position.Y, instance.Back.Direction);
            Send(p, instance);
        }
    }

    /// <summary>
    /// Where the team arrives (no DragonAreaPlayerSpawnpoint marker stands in DRAGON_001): at one of its exits, the same for the whole team [infer: each
    /// exit has a spawnpos]; without exits the template's default spawn.
    /// </summary>
    public Arrival ArrivalFor(DragonAreaInstance instance)
    {
        if (instance.Arrival is { } a) return a;
        var exits = instance.Template.Critical.Where(m => m.Type == "dragonareareexit" && m.Attr("type") != "MainQuestLocked").ToList();
        var at = exits.Count == 0 ? new Arrival(instance.Id, Transfers.DefaultSpawn(instance.Template), 0) : MistsBorderExit.ArrivalOf(exits[Rng.Next(exits.Count)], instance.Id);
        instance.Arrival = at;
        return at;
    }

    /// <summary><paramref name="p"/> goes into <paramref name="instance"/>: counted inside until its Join, the map made and filled, the push {0 id, 3 a88, 4 the clustertype, 5 the tier}.</summary>
    public void Send(Player p, DragonAreaInstance instance)
    {
        var at = ArrivalFor(instance);
        instance.Arriving[p.Character.Id] = world.Clock.Now + TimeSpan.FromSeconds(world.DungeonSettings.ArrivalGraceSeconds);
        instance.Entered.Add(p.Character.Id);
        instance.EmptySince = null;
        world.LoadTemplate(world.Maps.Get(instance.Id));
        Log.Info("world", string.Create(CultureInfo.InvariantCulture, $"'{p.Character.Name}' goes into {instance} at ({at.Position.X:0.##}, {at.Position.Y:0.##}); way back {instance.Back.Cluster}"));
        p.Session.Teleport(at, $"dragonarea:{instance.Id}", type: instance.Mode.ClusterType, layout: instance.Bytes, tier: instance.Tier);
    }

    // ---- the exits ----

    /// <summary>ExitEnterStart 237 on a dragon area exit: Active, in its square, up; the channel of Dungeons:ExitChannelSeconds [infer: the client's own bar]. Why not, or null.</summary>
    public string? StartExit(Player p, DragonAreaExit exit)
    {
        if (exit.State != DragonAreaExitState.Active) return "the exit is inactive";
        if (p.Combat.Down is { } down) return $"knocked down ({down.Kind})";
        var at = p.PositionAt(DateTime.UtcNow);
        if (!Transfers.InSquare(exit.Position, Vector2.Max(exit.Size, new Vector2(world.DungeonSettings.MinSquare)), at, world.Config.Transfer.PositionTolerance)) return "not at the exit";
        var now = world.GameNow;
        var length = TimeSpan.FromSeconds(world.DungeonSettings.ExitChannelSeconds);
        _exiting[p] = (exit, world.Now + length, at);
        p.SendToObservers(new ExitEnterStartEvent { ddn = p.Id, b5w = now, b5x = now + length.Ticks, b5y = exit.Id });
        return null;
    }

    /// <summary>ExitEnterCancel 238, a move, a knockdown: 254. False when none ran.</summary>
    public bool CancelExit(Player p, string why)
    {
        if (!_exiting.Remove(p)) return false;
        p.SendAround(new ExitEnterCancelEvent { ddn = p.Id });
        Log.Info("world", $"'{p.Character.Name}' dragon area exit cancelled ({why})");
        return true;
    }

    /// <summary>The way back of <paramref name="p"/> (its <see cref="Character.DungeonReturn"/>, else the instance's), cleared.</summary>
    public Arrival Out(Player p, DragonAreaInstance? instance)
    {
        var back = p.Character.DungeonReturn is { Cluster.Length: > 0 } b && !DragonAreaIds.IsInstance(b.Cluster) ? new Arrival(b.Cluster, new Vector2(b.X, b.Y), b.Direction)
            : instance?.Back ?? new Arrival(world.Config.Mists.FallbackCluster, Vector2.Zero, 0);
        p.Character.DungeonReturn = null;
        return back;
    }

    /// <summary>
    /// A character whose saved cluster is an instance that is gone (closed, or a restart): its way back, saved. False when its cluster is no dragon
    /// area instance that is gone.
    /// </summary>
    public bool ReturnIfClosed(Character c)
    {
        if (!DragonAreaIds.IsInstance(c.Cluster) || Find(c.Cluster) is not null) return false;
        var to = c.DungeonReturn is { Cluster.Length: > 0 } b && !DragonAreaIds.IsInstance(b.Cluster) ? b : new DungeonReturn(world.Config.Mists.FallbackCluster, -40, -4, 0);
        (c.Cluster, c.PositionX, c.PositionY, c.Direction) = (to.Cluster, to.X, to.Y, to.Direction);
        c.PendingTransfer = null;
        c.DungeonReturn = null;
        world.Saves.SaveNow(c);
        Log.Info("world", $"'{c.Name}' was in a dragon area that is gone: back in {to.Cluster}");
        return true;
    }

    /// <summary>The instance closes (its shutdown, empty, a GM): everyone inside out to the way back (a dead one respawns); the map goes.</summary>
    public void Close(DragonAreaInstance instance, string why)
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
                _exiting.Remove(p);
                if (p.Combat.Down is { Kind: not KnockdownKind.Dead }) Death.Abandoned(p);
                if (p.Combat.Down is { Kind: KnockdownKind.Dead } && Death.Respawn(p, "dragon area closed")) continue;
                var to = Out(p, instance);
                Expeditions.Tell(p, $"The Ancient Lands close; you are back in {to.Cluster}.");
                p.Session.Teleport(to, $"dragonarea:{instance.Id}:closed");
            }
            world.Maps.Unload(map, force: true);
        }
        _instances.Remove(instance.Id);
        Log.Info("world", $"{instance} closed ({why})");
    }

    // ---- the timers ----

    /// <summary>
    /// Every tick: the entrances' life cycle (an Open one past its lifetime closes, 694 again [infer]; a closed one past its despawntime goes, its users
    /// cancelled, its event redrawn by DragonAreas:RedrawMinutes), the use channels (→ WaitingToEnter; a user gone, away or knocked down cancelled), the
    /// finished builds (the team sent in), the exit channels, then each instance's alert, shutdown and empty close.
    /// </summary>
    public void Update()
    {
        var now = world.Clock.Now;
        foreach (var e in _entrances.ToList())
        {
            if (e.Map is not { } map || map.Unloaded) { _entrances.Remove(e); continue; }
            if (now >= e.RemoveAt)
            {
                map.RemoveObject(e);
                _entrances.Remove(e);
                if (e.Origin is var (ev, spots)) Redraw(map, ev, spots);
                continue;
            }
            if (e.State == DragonAreaEntranceState.Open && now >= e.CloseAt)
            {
                e.State = DragonAreaEntranceState.Closed;
                e.StateEnd = world.GameNow + (e.RemoveAt - now).Ticks;
                map.SendToObservers(e, e.Event());
            }
        }
        foreach (var (p, u) in _using.ToList())
        {
            if (p.Session.Player != p || p.Map is null || p.Map != u.Entrance.Map) { _using.Remove(p); continue; }
            if (!_entrances.Contains(u.Entrance) || u.Entrance.State != DragonAreaEntranceState.Open) Cancel(p, "the entrance closed");
            else if (p.Combat.Down is not null) Cancel(p, "knocked down");
            else if (u.State == StateChanneling && now >= u.Due)
            {
                u.State = StateWaiting;
                u.Start = world.GameNow;
                u.End = u.Entrance.StateEnd;
                Tell699(p, u, UpdateActive);
            }
        }
        foreach (var entry in _pending.Where(x => x.Build.IsCompleted).ToList())
        {
            _pending.Remove(entry);
            if (entry.Build.IsCompletedSuccessfully) Finish(entry);
            else
            {
                Log.Warn("world", $"dragon area not built: {entry.Build.Exception?.InnerException?.Message}");
                foreach (var p in entry.Team)
                {
                    Cancel(p, "the instance could not be built");
                    Expeditions.Tell(p, "The Ancient Lands could not be built; try again.");
                }
            }
        }
        _builds.RemoveAll(t => t.IsCompleted);
        foreach (var (p, (exit, due, from)) in _exiting.ToList())
        {
            if (p.Map is null || p.Map != exit.Map || p.Session.Player != p) _exiting.Remove(p);
            else if (p.Combat.Down is not null) CancelExit(p, "knocked down");
            else if (Vector2.Distance(p.PositionAt(DateTime.UtcNow), from) > world.DungeonSettings.ExitCancelDistance) CancelExit(p, "moved");
            else if (world.Now >= due)
            {
                _exiting.Remove(p);
                p.SendAround(new ExitEnterFinishedEvent { ddn = p.Id, b5v = exit.Id });
                var instance = Find(exit.Map?.Cluster);
                var to = Out(p, instance);
                Log.Info("world", $"'{p.Character.Name}' leaves {instance?.ToString() ?? exit.Map?.Cluster} by {exit} to {to.Cluster}");
                p.Session.Teleport(to, $"dragonarea:exit:{exit.Id}");
            }
        }
        var alert = TimeSpan.FromSeconds(world.Clusters?.DragonAreasIfLoaded?.ShutdownAlertSeconds ?? 60);
        foreach (var instance in _instances.Values.ToList())
        {
            var map = world.Maps.Find(instance.Id);
            if (now >= instance.ShutdownAt)
            {
                Close(instance, "shutdown");
                continue;
            }
            if (!instance.WarnedAlert && now >= instance.ShutdownAt - alert)
            {
                instance.WarnedAlert = true;
                foreach (var p in map?.Players.Values ?? Enumerable.Empty<Player>())
                    Expeditions.Tell(p, string.Create(CultureInfo.InvariantCulture, $"The Ancient Lands close in {(instance.ShutdownAt - now).TotalSeconds:0} seconds."));
            }
            foreach (var late in instance.Arriving.Where(a => now >= a.Value).Select(a => a.Key).ToList()) instance.Arriving.Remove(late);
            if ((map?.Players.Count ?? 0) > 0 || instance.Arriving.Count > 0) instance.EmptySince = null;
            else instance.EmptySince ??= now;
            if (instance.EmptySince is { } since && now - since >= TimeSpan.FromSeconds(EmptySeconds)) Close(instance, $"empty for {EmptySeconds:0} s");
        }
    }

    /// <summary>After JoinFinished in an instance: counted inside.</summary>
    public void Joined(Player p)
    {
        if (p.Map is not { } map || Find(map.Cluster) is not { } instance) return;
        instance.Arriving.Remove(p.Character.Id);
        instance.Entered.Add(p.Character.Id);
        instance.EmptySince = null;
        Expeditions.Tell(p, string.Create(CultureInfo.InvariantCulture,
            $"Ancient Lands ({instance.Mode.Name}, T{instance.Tier}): closes in {Math.Max(0, (instance.ShutdownAt - world.Clock.Now).TotalMinutes):0} minutes. The exits lead back to {instance.Back.Cluster}."));
    }

    /// <summary>.dragon list: the live instances.</summary>
    public IEnumerable<string> Describe()
    {
        var now = world.Clock.Now;
        foreach (var i in _instances.Values)
            yield return string.Create(CultureInfo.InvariantCulture,
                $"  {i.Id} {i.Mode.Name} {i.Map.Id} T{i.Tier}: {world.Maps.Find(i.Id)?.Players.Count ?? 0} inside, {i.Entered.Count} entered, shuts down in {Math.Max(0, (i.ShutdownAt - now).TotalMinutes):0} min, back to {i.Back.Cluster}");
    }
}
