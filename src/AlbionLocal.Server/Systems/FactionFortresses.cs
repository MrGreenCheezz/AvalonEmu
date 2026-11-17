using System.Globalization;
using AlbionLocal.Photon;
using System.Numerics;
using AlbionLocal.Server.Data;
using AlbionLocal.Server.Entities;
using AlbionLocal.Server.Protocol;

namespace AlbionLocal.Server.Systems;

/// <summary>
/// StageR fw-zone (analysis/work/stageR/fw-zone/SPEC.md §1; FactionWarfare:Fortresses / FortressGuards): the 20 faction fortresses of the warfare
/// duchies (world.xml factionfortresses; the factionfortress member of their *_FactionWarfare_Fortress_* template) and their siege.
/// <list type="bullet">
/// <item><b>The tower</b> (<see cref="FortressObject"/>, 652): owned by its duchy's defaultfaction at first (the fortress owner is the duchy owner
/// for the client: 652 key 4 and 656 key 1 go to dex+0x2c, FactionWarfareClusterState key 9); its capture area (652 keys 7-13 and
/// FactionCaptureAreaProgressUpdate 655, the outposts' 366 layout) and its 9 tower guards of the owner (factionfortress.xml fortressguards).</item>
/// <item><b>The fight</b> (FactionFortressFightStateUpdate 659 to the fortress's map, client dez): PreSiegeAnnouncement (presiegeannouncementdurationsec
/// 60) → PreSiege (presiegedurationsec 540; the HUD's "Siege begins in" counts to key 2) → ActiveSiege: the defensive progress rises
/// activesiegedefensepointspersecond (1) to activesiegedefensepointgoal (1800; key 6 at key 7, the client extrapolates it itself) and the defenders
/// win at the goal; the attackers win by holding the capture area (flagged for the attacking faction, alive, unmounted, within captureradius 10 of
/// the tower, the tower guards dead [infer, as the outposts]) until their points are full (attackerclaimtime 30 s, the owner's drained in
/// attackerunclaimtime 2 s): FactionFortressClaimed 656 {0 tower, 1 new owner}. The end: FactionFortressFightEnded 661 {0 fight, 1 winner, 2 the
/// attacker won [assume], 3 false}, Finished until postsiegeresetdelaysec (600), then NotStarted. The two factions' players elsewhere get
/// FactionFortressFightStartedInRemoteClusterEvent 664 {0 duchy, 1 attacker, 2 defender, 3 cutoff false} at the start and …Finished… 665
/// {0 duchy, 1 attacker, 2 defender, 3 winner, 4 cutoff false} at the end (glr.aq 0x180cad5a0 / 0x180caddb0: key 0 is a duchy uniquename).</item>
/// </list>
/// What starts a siege officially (the duchy supply war at the evaluation times) is not built: a GM starts one (.fortress siege). Not done: the
/// last stand (FightState 4), the weapon caches (657/658), the player ejection (653), the cutoff fights (660/666-669), the rewards. Under
/// <see cref="World.Sync"/>; the state lives while the server runs.
/// </summary>
public sealed class FactionFortresses(World world)
{
    /// <summary>FactionFortressState (client enum).</summary>
    public const byte Inactive = 0, PreSiegeState = 1, ActiveSiegeState = 2, RedZonePhase2 = 3;

    /// <summary>FactionFortressFightState (client enum).</summary>
    public const byte NotStarted = 0, Announcement = 1, PreSiege = 2, ActiveSiege = 3, LastStand = 4, Finished = 5;

    /// <summary>A fortress's siege.</summary>
    public sealed class Fight
    {
        public byte Attacker { get; internal set; }
        public byte Defender { get; internal set; }
        public byte Phase { get; internal set; } = Announcement;
        public long Since { get; internal set; }
        public long Until { get; internal set; }
        public byte Winner { get; internal set; }

        /// <summary>The defensive progress (points) at <see cref="DefenseAt"/> (game ticks), rising while ActiveSiege.</summary>
        public double Defense { get; internal set; }
        public long DefenseAt { get; internal set; }
    }

    /// <summary>One fortress (by its world.xml id), kept while the server runs.</summary>
    public sealed class State(Guid id, string cluster, FortressSpawn spawn, byte owner)
    {
        public Guid Id { get; } = id;
        public string Cluster { get; } = cluster;
        public FortressSpawn Spawn { get; } = spawn;
        public byte DefaultOwner { get; } = owner;
        public byte Owner { get; internal set; } = owner;
        public byte Holder { get; internal set; } = owner;
        public double Value { get; internal set; } = 1000;
        public double Rate { get; internal set; }
        public long Stamp { get; internal set; }
        internal long ValueAt;
        public int Claim { get; internal set; } = Outposts.Unclaimable;
        public int Zone { get; internal set; }
        public Fight? Fight { get; internal set; }

        /// <summary>The red-zone event's phase 2 runs in this fortress's duchy (FactionFortressState 3 while no fight).</summary>
        public bool RedZone { get; internal set; }

        public IReadOnlyList<Mob?> Squad => Guards;
        internal Mob?[] Guards = [];
        internal DateTime?[] GuardsBack = [];
        internal long LastTick;
        internal (byte, byte, int, int, long, double) Sent;
        internal byte SentState = 0xff;

        public byte FortressState => Fight is { Phase: Announcement or PreSiege } ? PreSiegeState
            : Fight is { Phase: ActiveSiege or LastStand } ? ActiveSiegeState : RedZone ? RedZonePhase2 : Inactive;

        /// <summary>NewFactionFortressObject 652 (ckc.af(aoq): key 4 → dex.ac, 5 the state, 6 its time, 7-13 the capture area as 366's 1-7).</summary>
        public NewFactionFortressObjectEvent BuildNew(long id, FortressSpawn spawn) => new()
        {
            b8v = id, b8w = spawn.Position, b8x = spawn.Direction, b8y = Id, b8z = Owner, b80 = FortressState, b81 = Fight?.Since ?? Stamp,
            b82 = Holder, b83 = Claim, b84 = Zone, b85 = Raw(Value), b86 = Target, b87 = ValueAt, b88 = Ticks,
        };

        /// <summary>FactionCaptureAreaProgressUpdate 655 (ckc.ajv case 0x28f → the same a5a as 652's keys 7-13).</summary>
        public FactionCaptureAreaProgressUpdateEvent BuildProgress(long id) => new()
        {
            ddn = id, b7o = Holder, b7p = Claim, b7q = Zone, b7r = Raw(Value), b7s = Target, b7t = ValueAt, b7u = Ticks,
        };

        private static long Raw(double v) => (long)Math.Round(v * 10000);
        private long Target => Rate > 0 ? Outposts.Full : Rate < 0 ? 0 : Raw(Value);
        private long Ticks => Rate == 0 ? 0 : (long)Math.Round(Math.Sign(Rate) * TimeSpan.TicksPerSecond / Math.Abs(Rate));

        public override string ToString() => string.Create(CultureInfo.InvariantCulture,
            $"owner {Owner}, state {FortressState}, points {Value:0} of {Holder}{(Rate != 0 ? $" ({Rate:+0.#;-0.#}/s)" : "")}, claim {Claim}, zone {Zone}")
            + (Fight is { } f ? string.Create(CultureInfo.InvariantCulture, $", fight {f.Attacker} vs {f.Defender} phase {f.Phase}, defense {f.Defense:0}") : "");
    }

    private readonly Dictionary<Guid, State> _states = [];
    private readonly List<FortressObject> _towers = [];
    private FortressData? _data;
    private bool _read;

    private FactionWarfareOptions O => world.Config.FactionWarfare;

    private DateTime UtcNow => new(world.GameNow, DateTimeKind.Utc);

    /// <summary>factionfortress.xml, read on first use (the 345319 values without client data).</summary>
    public FortressData Data
    {
        get
        {
            if (_read) return _data ?? FortressData.Default;
            _read = true;
            if (world.Clusters?.Folder is { } folder)
                try { _data = FortressData.Load(GameDataFile.Open(folder, "factionfortress.bin").Root()); }
                catch (Exception e) when (e is IOException or InvalidDataException or UnauthorizedAccessException)
                {
                    Log.Warn("faction", $"factionfortress.bin not read: {e.Message}; the fortresses stand without guards");
                }
            return _data ?? FortressData.Default;
        }
    }

    /// <summary>The fortresses made so far.</summary>
    public IEnumerable<State> All => _states.Values;

    /// <summary>The fortress of <paramref name="cluster"/>, if it was made.</summary>
    public State? Of(string cluster) => _states.Values.FirstOrDefault(s => s.Cluster == cluster);

    // ---- the map fill

    /// <summary>A fortress cluster's tower (<see cref="World.MapSpawner"/>): one cluster-wide object, owned by its duchy's faction at first.</summary>
    public IEnumerable<WorldObject> Populate(Map map, ClusterTemplate template)
    {
        if (!O.Fortresses || template.Fortresses.Count == 0 || world.Factions.Data is not { } fw) yield break;
        var duchy = fw.ClusterFaction.GetValueOrDefault(template.Id);
        if (!Pvp.IsFaction(duchy)) yield break;
        _towers.RemoveAll(t => t.Map is { Unloaded: true } || t.Map == map);
        foreach (var spawn in template.Fortresses)
        {
            if (!_states.TryGetValue(spawn.Guid, out var s))
                _states[spawn.Guid] = s = new State(spawn.Guid, template.Id, spawn, duchy) { Stamp = world.GameNow, ValueAt = world.GameNow };
            (s.Guards, s.GuardsBack, s.LastTick) = (new Mob?[spawn.Guards.Count], new DateTime?[spawn.Guards.Count], world.GameNow);
            var tower = new FortressObject(world.ObjectIds.Next(), spawn, s);
            _towers.Add(tower);
            yield return tower;
        }
    }

    /// <summary>The Join's phase B after the cluster-wide objects: a running fight's 659.</summary>
    public void Joined(Player p)
    {
        if (p.IsBot || p.Map is not { } map) return;
        foreach (var t in _towers.Where(t => t.Map == map))
            if (t.State.Fight is { } fight) p.Session.Send(FightUpdate(t, fight));
    }

    // ---- the tick

    public void Update()
    {
        if (_towers.Count == 0) return;
        _towers.RemoveAll(t => t.Map is { Unloaded: true });
        foreach (var t in _towers.Where(t => t.Map is not null).ToList())
        {
            try { Tick(t, t.Map!); }
            catch (Exception e) when (e is InvalidOperationException or KeyNotFoundException or ArgumentException)
            {
                Combat.LogOnce("fortress:" + t.State.Id, $"fortress {t.State.Cluster}: {e.Message}");
            }
        }
    }

    private static bool Standing(Mob? m, Map map) => m is { Dead: false, Removed: false } && m.Map == map;

    private void Tick(FortressObject tower, Map map)
    {
        var s = tower.State;
        var now = world.GameNow;
        var dt = Math.Clamp(TimeSpan.FromTicks(now - s.LastTick).TotalSeconds, 0, 5);
        s.LastTick = now;
        Guards(s, map);
        if (s.Fight is { } fight) Phases(tower, map, fight, now);
        var guards = s.Guards.Count(m => Standing(m, map));
        var active = s.Fight is { Phase: ActiveSiege or LastStand };
        s.Claim = !active ? Outposts.Unclaimable : guards > 0 ? Outposts.Guarded : Outposts.Claimable;
        if (s.Claim == Outposts.Claimable && s.Fight is { } f)
        {
            var time = new DateTime(now, DateTimeKind.Utc);
            var factions = map.Players.Values
                .Where(p => p.Combat.Down is null && !p.IsMounted && (p.Flag == f.Attacker || p.Flag == s.Owner)
                    && Vector2.Distance(p.PositionAt(time), s.Spawn.Position) <= s.Spawn.CaptureRadius)
                .Select(p => p.Flag).Distinct().ToList();
            Progress(s, factions, dt);
            if (s.Holder == f.Attacker && s.Value >= 1000)
            {
                Captured(tower, map, f);
                return;
            }
        }
        else if (s.Holder != s.Owner || s.Value < 1000 || s.Rate != 0) Settle(s);
        Send(tower, map);
    }

    /// <summary>The fight's clock: announcement → pre-siege → active (the defense rising to its goal) → finished → gone.</summary>
    private void Phases(FortressObject tower, Map map, Fight f, long now)
    {
        var d = Data;
        var s = tower.State;
        if (f.Phase == ActiveSiege)
        {
            f.Defense = Math.Min(d.DefenseGoal, f.Defense + TimeSpan.FromTicks(now - f.DefenseAt).TotalSeconds * d.DefensePerSecond);
            f.DefenseAt = now;
            if (f.Defense >= d.DefenseGoal)
            {
                End(tower, map, f, f.Defender);
                return;
            }
        }
        if (now < f.Until) return;
        switch (f.Phase)
        {
            case Announcement:
                Enter(tower, map, f, PreSiege, d.PreSiegeSeconds);
                break;
            case PreSiege:
                (f.Defense, f.DefenseAt) = (0, now);
                Enter(tower, map, f, ActiveSiege, 0);
                break;
            case Finished:
                s.Fight = null;
                foreach (var p in map.Players.Values) p.Session.Send(new FactionFortressFightStateUpdateEvent { b74 = tower.Id, b75 = now, b76 = now, b77 = NotStarted, b78 = f.Attacker, b79 = f.Defender });
                Resend(tower, map);
                break;
        }
    }

    private void Enter(FortressObject tower, Map map, Fight f, byte phase, double seconds)
    {
        var now = world.GameNow;
        (f.Phase, f.Since, f.Until) = (phase, now, seconds > 0 ? now + TimeSpan.FromSeconds(seconds).Ticks : long.MaxValue);
        if (phase == ActiveSiege) f.Until = now + TimeSpan.FromSeconds(Data.DefenseGoal / Math.Max(1e-6, Data.DefensePerSecond)).Ticks; // when the goal is due
        var update = FightUpdate(tower, f);
        foreach (var p in map.Players.Values) p.Session.Send(update);
        Resend(tower, map);
        world.Outposts.Refresh(); // 468 key 10: the attacker
        Log.Info("world", $"fortress {tower.State.Cluster}: {world.Factions.Name(f.Attacker)} vs {world.Factions.Name(f.Defender)}, phase {phase}");
    }

    /// <summary>FactionFortressFightStateUpdate 659 (dez.k(aom) 0x180aec090): 0 the fight (the tower's id [assume]), 1/2 the phase's start and end, 3 the phase, 4 attacker, 5 defender, 6/7 the defense at its time.</summary>
    private static FactionFortressFightStateUpdateEvent FightUpdate(FortressObject tower, Fight f) => new()
    {
        b74 = tower.Id, b75 = f.Since, b76 = f.Until == long.MaxValue ? f.Since : f.Until, b77 = f.Phase, b78 = f.Attacker, b79 = f.Defender,
        b8a = (long)Math.Round(f.Defense * 10000), b8b = f.DefenseAt == 0 ? f.Since : f.DefenseAt,
    };

    /// <summary>The attackers' full points: the fortress is theirs (656), the fight ends (661) and the new owner's guards come.</summary>
    private void Captured(FortressObject tower, Map map, Fight f)
    {
        var s = tower.State;
        s.Owner = f.Attacker;
        Settle(s);
        foreach (var m in s.Guards.Where(m => Standing(m, map))) SpawnManager.Despawn(m!, world, respawn: false);
        Array.Clear(s.Guards);
        Array.Clear(s.GuardsBack);
        map.SendToObservers(tower, new FactionFortressClaimedEvent { ddn = tower.Id, b7v = s.Owner });
        End(tower, map, f, f.Attacker);
    }

    private void End(FortressObject tower, Map map, Fight f, byte winner)
    {
        f.Winner = winner;
        map.SendToObservers(tower, new FactionFortressFightEndedEvent { b70 = tower.Id, b71 = winner, b72 = winner == f.Attacker });
        Enter(tower, map, f, Finished, Data.ResetSeconds);
        Remote(tower, map, f, new FactionFortressFightFinishedInRemoteClusterEventEvent
        {
            b6x = DuchyName(tower.State.Cluster), b6y = f.Attacker, b6z = f.Defender, b60 = winner,
        });
        Log.Info("world", $"fortress {tower.State.Cluster}: won by {world.Factions.Name(winner)}");
    }

    /// <summary>664 / 665 to the two factions' enlisted players on other maps [infer: who gets them].</summary>
    private void Remote(FortressObject tower, Map map, Fight f, AlbionEvent ev)
    {
        foreach (var other in world.Maps.Maps.Where(m => m != map))
            foreach (var p in other.Players.Values)
                if (!p.IsBot && (p.Faction == f.Attacker || p.Faction == f.Defender)) p.Session.Send(ev);
    }

    private string DuchyName(string cluster) => world.Factions.Data?.DuchyOf(cluster)?.Name ?? "";

    /// <summary>The capture area at rest: its owner's points full and still.</summary>
    private void Settle(State s) => (s.Holder, s.Value, s.Rate, s.Zone, s.Stamp, s.ValueAt) = (s.Owner, 1000, 0, Outposts.NoTeamInZone, world.GameNow, world.GameNow);

    /// <summary>One tick of the capture area's points (the outposts' rule with factionfortress.xml's times).</summary>
    private void Progress(State s, List<byte> factions, double dt)
    {
        var d = Data;
        s.Value = Math.Clamp(s.Value + s.Rate * dt, 0, 1000);
        s.ValueAt = world.GameNow;
        static double Per(double seconds) => 1000 / Math.Max(0.1, seconds);
        var rate = 0d;
        if (factions.Count >= 2) s.Zone = Outposts.MultipleTeamsInZone;
        else if (factions.Count == 1)
        {
            var f = factions[0];
            s.Zone = f == s.Owner ? Outposts.OwnerTeamInZone : Outposts.ClaimingTeamInZone;
            rate = f == s.Holder ? Per(s.Holder == s.Owner ? d.OwnerClaimSeconds : d.AttackerClaimSeconds)
                : -Per(f == s.Owner ? d.OwnerUnclaimSeconds : d.AttackerUnclaimSeconds);
            if (rate < 0 && s.Value <= 0) (s.Holder, s.Value, rate) = (f, 0, Per(f == s.Owner ? d.OwnerClaimSeconds : d.AttackerClaimSeconds));
        }
        else
        {
            s.Zone = Outposts.NoTeamInZone;
            rate = s.Holder == s.Owner ? Per(d.OwnerNoTeamClaimSeconds) : -Per(d.AttackerNoTeamUnclaimSeconds);
            if (rate < 0 && s.Value <= 0) (s.Holder, s.Value, rate) = (s.Owner, 0, Per(d.OwnerNoTeamClaimSeconds));
        }
        if (rate > 0 && s.Value >= 1000 && s.Holder == s.Owner) rate = 0;
        if (rate < 0 && s.Value <= 0) rate = 0;
        if (Math.Abs(rate - s.Rate) > 1e-9) (s.Rate, s.Stamp) = (rate, world.GameNow);
    }

    /// <summary>655 when the capture area's regime changed; 652 again when the fortress state or owner did.</summary>
    private static void Send(FortressObject tower, Map map)
    {
        var s = tower.State;
        if (s.SentState != s.FortressState) Resend(tower, map);
        var key = (s.Owner, s.Holder, s.Claim, s.Zone, s.Stamp, s.Rate);
        if (key == s.Sent) return;
        s.Sent = key;
        map.SendToObservers(tower, s.BuildProgress(tower.Id));
    }

    private static void Resend(FortressObject tower, Map map)
    {
        var s = tower.State;
        s.SentState = s.FortressState;
        s.Sent = (s.Owner, s.Holder, s.Claim, s.Zone, s.Stamp, s.Rate);
        map.SendToObservers(tower, s.BuildNew(tower.Id, tower.Spawn));
    }

    // ---- guards

    /// <summary>The owner's tower guards stand on their spots; a dead one is back after its mobs.xml respawn time (T8: 1350 s).</summary>
    private void Guards(State s, Map map)
    {
        if (!O.FortressGuards || s.Guards.Length == 0 || world.Clusters is not { } store) return;
        var now = UtcNow;
        for (var i = 0; i < s.Guards.Length; i++)
        {
            var m = s.Guards[i];
            if (Standing(m, map)) continue;
            var spot = s.Spawn.Guards[i];
            if (Data.GuardOf(spot.GuardType, s.Owner) is not { } name || store.Mobs.Find(name) is not { } template) continue;
            if (m is not null)
            {
                (s.Guards[i], s.GuardsBack[i]) = (null, now + TimeSpan.FromSeconds(template.RespawnSecondsMin));
                continue;
            }
            if (s.GuardsBack[i] is { } back && now < back) continue;
            var owner = s.Owner;
            s.Guards[i] = SpawnManager.SpawnAt(map, template, spot.Position, spot.Direction, world, behavior: "GUARD",
                foes: p => Pvp.IsFaction(p.Flag) && p.Flag != owner);
            s.GuardsBack[i] = null;
        }
    }

    // ---- other systems

    /// <summary>
    /// FactionWarfareClusterState keys 9 / 10 for a cluster of a fortress's duchy (the fortress view reads 9 as the duchy owner, 10 as the active
    /// attacker: FactionFortressObjectView.UpdateFortressTowerState 0x18126ef50 → FactionFortress.UpdateFortressTowerState(…, eDuchyOwner,
    /// eActiveAttacker, …)): while a fight runs or after the fortress changed hands; null = leave the outposts' values.
    /// </summary>
    public (byte Owner, byte Attacker)? ZoneOverride(string cluster)
    {
        if (_states.Count == 0 || world.Factions.Data?.DuchyOf(cluster) is not { } duchy) return null;
        var s = _states.Values.FirstOrDefault(x => duchy.Clusters.Contains(x.Cluster));
        if (s is null) return null;
        if (s.Fight is { Phase: not Finished } f) return (s.Owner, f.Attacker);
        return s.Owner != s.DefaultOwner ? (s.Owner, (byte)0x80) : null;
    }

    /// <summary>The red-zone event's phase 2 (<see cref="RedZoneEvents"/>): the fortresses of <paramref name="duchies"/> show FactionFortressState 3.</summary>
    public void RedZone(IReadOnlyCollection<string> duchies)
    {
        var fw = world.Factions.Data;
        foreach (var s in _states.Values) s.RedZone = fw?.DuchyOf(s.Cluster) is { } d && duchies.Contains(d.Name);
        foreach (var t in _towers.Where(t => t.Map is not null)) Send(t, t.Map!);
    }

    // ---- GM

    public IEnumerable<string> Describe(Map map)
    {
        var towers = _towers.Where(t => t.Map == map).ToList();
        if (towers.Count == 0) yield return "no faction fortress on this map";
        foreach (var t in towers)
            yield return string.Create(CultureInfo.InvariantCulture,
                $"#{t.Id} at ({t.Position.X:0}, {t.Position.Y:0}): {t.State}, guards {t.State.Guards.Count(m => Standing(m, map))}/{t.State.Guards.Length}");
    }

    /// <summary>.fortress siege / owner / clear / next on the fortress of <paramref name="map"/>.</summary>
    public string Gm(Map map, string action, byte faction)
    {
        if (_towers.FirstOrDefault(t => t.Map == map) is not { } tower) return "no faction fortress on this map";
        var s = tower.State;
        switch (action)
        {
            case "siege":
                if (!Pvp.IsFaction(faction) || faction == s.Owner) return "an attacking faction 1-6 other than the owner";
                if (s.Fight is { Phase: not Finished }) return "a siege runs already";
                var f = new Fight { Attacker = faction, Defender = s.Owner };
                s.Fight = f;
                Enter(tower, map, f, Announcement, Data.AnnouncementSeconds);
                Remote(tower, map, f, new FactionFortressFightStartedInRemoteClusterEventEvent { b62 = DuchyName(s.Cluster), b63 = f.Attacker, b64 = f.Defender });
                return $"#{tower.Id}: {world.Factions.Name(faction)} besieges {world.Factions.Name(s.Owner)}'s fortress (announcement {Data.AnnouncementSeconds:0} s)";
            case "next":
                if (s.Fight is not { } running) return "no siege runs";
                running.Until = world.GameNow;
                if (running.Phase == ActiveSiege) running.Defense = Data.DefenseGoal;
                Phases(tower, map, running, world.GameNow);
                return $"#{tower.Id}: {s}";
            case "owner":
                if (!Pvp.IsFaction(faction)) return "a faction 1-6";
                s.Owner = faction;
                Settle(s);
                foreach (var m in s.Guards.Where(m => Standing(m, map))) SpawnManager.Despawn(m!, world, respawn: false);
                Array.Clear(s.Guards);
                Array.Clear(s.GuardsBack);
                map.SendToObservers(tower, new FactionFortressClaimedEvent { ddn = tower.Id, b7v = faction });
                Resend(tower, map);
                world.Outposts.Refresh();
                return $"#{tower.Id}: owner {world.Factions.Name(faction)}";
            case "clear":
                foreach (var m in s.Guards.Where(m => Standing(m, map))) SpawnManager.Despawn(m!, world, respawn: false);
                for (var i = 0; i < s.GuardsBack.Length; i++) s.GuardsBack[i] = UtcNow + TimeSpan.FromHours(1);
                Array.Clear(s.Guards);
                return $"#{tower.Id}: its tower guards are gone for an hour";
            default:
                return "actions: siege <faction 1-6>, next, owner <faction 1-6>, clear";
        }
    }
}
