using System.Globalization;
using System.Numerics;
using AlbionLocal.Photon;
using AlbionLocal.Server.Data;
using AlbionLocal.Server.Entities;
using AlbionLocal.Server.Protocol;

namespace AlbionLocal.Server.Systems;

/// <summary>
/// StageQ: the faction warfare towers (analysis/work/stageQ/outposts: DATA.md the client data, CLIENT.md the client's handlers; FactionWarfare:*
/// of <see cref="FactionWarfareOptions"/>). Every warfare zone's world.xml outposts stand as <see cref="OutpostObject"/>s, cluster-wide (365 + 366
/// in the Join's phase B, as captured in 1207), owned at first by the zone's duchy defaultfaction; their state belongs to the world and outlives
/// the map (not saved over a restart).
/// <list type="bullet">
/// <item><b>Guards</b> (FactionWarfare:OutpostGuards): the owner's outpost.xml roster for the cluster tier (1 T5/T6_MOB_CITY_&lt;F&gt;_BOSS + 7
/// guards) on the tower's 8 guardspawnpoints; they fight players flagged for another faction (<see cref="Mob.Foes"/>). A dead one is back after its
/// respawndelay (boss 15 s, guards 30 s) once the tower has been quiet (ContestedState 0) for respawnblockdelay (15 s) [infer]; after a capture
/// the new owner's come after their respawndelay at once (the captured ClaimState 3 → 2 came 15 s after the claim).</item>
/// <item><b>ClaimState</b> (client: 1 Unclaimable shows the cooldown widget, 2 Guarded a dim zone, 3 Claimable a lit zone and a live circle):
/// Unclaimable in a zone off the front line (FactionWarfare:Claimable), else Guarded while a guard stands, Claimable when all are dead.</item>
/// <item><b>Capture</b> (no client operation: by presence, outpost.xml outpostcapture): the alive players flagged for a faction within
/// capturezoneradius (12 m) of the capture zone, mounted ones not counting (SimpleFeedback 470 tells a player in the zone who cannot claim: 0 not
/// flagged, 1 not a front line zone, 2 / 3 mounted). The points (0-1000) belong to key 1's faction, at first the owner with all 1000. One faction
/// alone fills its own in attackerclaimtime (16 s; the owner's in ownerclaimtime) and empties another's in attackerunclaimtime (8 s; the owner's
/// in ownerunclaimtime), taking key 1 at 0; nobody: an attacker's run out in attackernoteamunclaimtime (30 s), the owner's refill in
/// ownernoteamclaimtime (30 s); two factions hold it. An attacker's full 1000 takes the tower (the captured 8 + 16 s): OutpostClaimed 367 {0, 1 new,
/// 2 old owner, 3 ContestedState}, then 366. The client draws the circle itself from 366 keys 4 (value at key 6), 5 (its target) and 7 (the signed
/// time per point), so 366 goes out only when one of them, the state or the owner changes.</item>
/// <item><b>ContestedState</b> (minimap icons, the owner faction's "under attack" alert): Minor (1) while a player of another faction is within
/// participationzoneradius (50 m) or a guard fights, Major (2) while the boss is below bossmajoralerthealth (0.7) or dead in a fight, or an attacker
/// is in the zone of a Claimable tower; each held for minor/majorattackstatepersistence (15 / 30 s) after it last held [infer: the captured
/// contest ended 30 s after the claim].</item>
/// <item><b>Zone</b> (FactionWarfareClusterState 468 to the map's players at the Join and on a change): the controller (key 4) is the duchy's
/// faction until another faction has held more than half the towers for FactionWarfare:ControlSeconds; key 3 the owner per tower in world.xml
/// order, key 5 per faction whether a player flagged for it is on the map [infer], key 6 FrontLine 1 / BackLine 2. The world map's
/// ClusterInfoUpdate keys 19 and 27 carry the towers' contest and owners (<see cref="FillWorldMap"/>).</item>
/// </list>
/// Not done: supplies and the campaign, the duchy conflicts (the front line here is a neighbour rule), fortresses, red-zone events, the faction
/// points of a capture. Under <see cref="World.Sync"/>.
/// </summary>
public sealed class Outposts(World world)
{
    /// <summary>366 key 4/5: 1000 points as a7m raw.</summary>
    public const long Full = 10_000_000;

    /// <summary>ClaimState (client enum): Initial 0, Unclaimable 1, Guarded 2, Claimable 3.</summary>
    public const int Unclaimable = 1, Guarded = 2, Claimable = 3;

    /// <summary>ContestedState and FactionCaptureZoneClaimingState (client enums).</summary>
    public const int NotContested = 0, MinorContested = 1, MajorContested = 2;

    public const int NoTeamInZone = 0, MultipleTeamsInZone = 1, OwnerTeamInZone = 2, ClaimingTeamInZone = 3;

    /// <summary>ClusterPositionState (468 key 6).</summary>
    public const int FrontLine = 1, BackLine = 2;

    /// <summary>SimpleFeedback 470 key 0 (@OUTPOST_CLAIM_ERROR_*): not flagged, not a front line cluster, mounted, mounted at the own tower.</summary>
    public const byte NotFlagged = 0, NotFrontline = 1, Mounted = 2, MountedOwner = 3;

    /// <summary>How often a player in a capture zone who cannot claim hears why.</summary>
    private static readonly TimeSpan FeedbackEvery = TimeSpan.FromSeconds(5);

    /// <summary>One tower's state (by its world.xml Guid), kept while the server runs.</summary>
    public sealed class State(Guid id, string cluster, OutpostSpawn spawn, byte owner)
    {
        public Guid Id { get; } = id;
        public string Cluster { get; } = cluster;
        public OutpostSpawn Spawn { get; } = spawn;

        /// <summary>366 key 8 / the minimap flag: the faction that holds it.</summary>
        public byte Owner { get; internal set; } = owner;

        /// <summary>366 key 1: the faction whose points <see cref="Value"/> are (the owner, or the attacker filling its own).</summary>
        public byte Holder { get; internal set; } = owner;

        /// <summary>The points 0-1000 at <see cref="Stamp"/>, moving by <see cref="Rate"/> per second (towards 0 or 1000).</summary>
        public double Value { get; internal set; } = 1000;

        /// <summary>Points per second (signed; 0 = still).</summary>
        public double Rate { get; internal set; }

        /// <summary>GameTimeStamp of the last regime change (<see cref="Rate"/>, <see cref="Holder"/>): what makes a new 366.</summary>
        public long Stamp { get; internal set; }

        /// <summary>GameTimeStamp <see cref="Value"/> was worked out at (366 key 6: the client extrapolates from it).</summary>
        internal long ValueAt;

        public int Claim { get; internal set; } = Guarded;
        public int Zone { get; internal set; }
        public int Contested { get; internal set; }

        /// <summary>The guard of each spot (the boss first) while it stands or lies dead; null while it is due.</summary>
        public IReadOnlyList<Mob?> Squad => Guards;

        internal DateTime MinorUntil, MajorUntil, CalmSince, LastTick;
        internal Mob?[] Guards = [];
        internal DateTime?[] GuardsBack = [];
        internal bool[] AfterClaim = [];
        internal (byte, byte, int, int, long, double, int) Sent;

        /// <summary>OutpostUpdate 366 for tower <paramref name="id"/> (keys 3 and 9 at 0 and key 7 at 0 stay out: the client's defaults).</summary>
        public OutpostUpdateEvent BuildUpdate(long id) => new()
        {
            ddn = id, b7o = Holder, b7p = Claim, b7q = Zone, b7r = (long)Math.Round(Value * 10000), b7s = Rate > 0 ? Full : Rate < 0 ? 0 : (long)Math.Round(Value * 10000),
            b7t = ValueAt, b7u = Rate == 0 ? 0 : (long)Math.Round(Math.Sign(Rate) * TimeSpan.TicksPerSecond / Math.Abs(Rate)), dff = Owner, dfg = Contested,
        };

        public override string ToString() => string.Create(CultureInfo.InvariantCulture,
            $"{Spawn.Type} owner {Owner}, points {Value:0} of {Holder}{(Rate != 0 ? $" ({Rate:+0.#;-0.#}/s)" : "")}, claim {Claim}, zone {Zone}, contested {Contested}");
    }

    /// <summary>A warfare zone's control: its controller, a majority on the way to it, what 468 last said.</summary>
    private sealed class Zone(byte controller)
    {
        public byte Controller = controller;
        public byte Majority;
        public DateTime? MajoritySince;
        public string Sent = "";
    }

    private readonly Dictionary<Guid, State> _states = [];
    private readonly List<OutpostObject> _towers = [];
    private readonly Dictionary<string, Zone> _zones = new(StringComparer.Ordinal);
    private readonly Dictionary<long, DateTime> _feedback = [];
    private OutpostData? _data;
    private bool _read;

    private FactionWarfareOptions O => world.Config.FactionWarfare;

    private DateTime UtcNow => new(world.GameNow, DateTimeKind.Utc);

    /// <summary>outpost.xml, read on first use; null without client data (no guards then; the rules default to the 345319 values).</summary>
    public OutpostData? Data
    {
        get
        {
            if (_read) return _data;
            _read = true;
            if (world.Clusters?.Folder is not { } folder) return null;
            try { _data = OutpostData.Load(GameDataFile.Open(folder, "outpost.bin").Root()); }
            catch (Exception e) when (e is IOException or InvalidDataException or UnauthorizedAccessException)
            {
                Log.Warn("faction", $"outpost.bin not read: {e.Message}; the towers stand without guards");
            }
            return _data;
        }
    }

    private OutpostData.Rules RulesOf(State s) => Data?.For(s.Spawn.Type) ?? OutpostData.Default;

    /// <summary>The tower states of <paramref name="cluster"/> (those made so far).</summary>
    public IEnumerable<State> Of(string cluster) => _states.Values.Where(s => s.Cluster == cluster);

    // ---- the map fill

    /// <summary>A warfare zone's towers (<see cref="MapSpawner.Populate"/>): one cluster-wide object per outpost member with its world.xml outpost.</summary>
    public IEnumerable<WorldObject> Populate(Map map, ClusterTemplate template)
    {
        if (!O.Outposts || template.Outposts.Count == 0 || world.Factions.Data is not { } fw) yield break;
        var duchy = fw.ClusterFaction.GetValueOrDefault(template.Id);
        if (!Pvp.IsFaction(duchy)) yield break;
        _towers.RemoveAll(t => t.Map is { Unloaded: true } || t.Map == map);
        _zones.TryAdd(template.Id, new Zone(duchy));
        var now = UtcNow;
        foreach (var spawn in template.Outposts)
        {
            if (spawn.World is null) continue;
            if (!_states.TryGetValue(spawn.Guid, out var s))
                _states[spawn.Guid] = s = new State(spawn.Guid, template.Id, spawn, duchy) { Stamp = world.GameNow };
            (s.Guards, s.GuardsBack, s.AfterClaim) = (new Mob?[spawn.Guards.Count], new DateTime?[spawn.Guards.Count], new bool[spawn.Guards.Count]);
            (s.LastTick, s.CalmSince) = (now, now);
            Settle(s);
            var tower = new OutpostObject(world.ObjectIds.Next(), spawn, s);
            _towers.Add(tower);
            yield return tower;
        }
    }

    /// <summary>The tower back at rest: its owner's points full and still (a new map, a capture).</summary>
    private void Settle(State s) => (s.Holder, s.Value, s.Rate, s.Zone, s.Stamp, s.ValueAt) = (s.Owner, 1000, 0, NoTeamInZone, world.GameNow, world.GameNow);

    /// <summary>The Join's phase B after the cluster-wide objects (<see cref="VisibilityTracker.SendClusterWide"/>): the zone's 468.</summary>
    public void Joined(Player p)
    {
        if (!p.IsBot && p.Map is { } map && Build(map) is { } state) p.Session.Send(state);
    }

    // ---- the tick

    /// <summary>The world tick: every standing tower's guards, contest and claim; each zone's control and 468.</summary>
    public void Update()
    {
        if (_towers.Count == 0) return;
        _towers.RemoveAll(t => t.Map is { Unloaded: true });
        var now = UtcNow;
        foreach (var t in _towers.Where(t => t.Map is not null))
        {
            try { Tick(t, t.Map!, now); }
            catch (Exception e) when (e is InvalidOperationException or KeyNotFoundException or ArgumentException)
            {
                Combat.LogOnce("outpost:" + t.State.Id, $"outpost {t.State.Cluster} {t.State.Id}: {e.Message}");
            }
        }
        foreach (var map in _towers.Where(t => t.Map is not null).Select(t => t.Map!).Distinct()) Control(map, now);
    }

    private static bool Standing(Mob? m, Map map) => m is { Dead: false, Removed: false } && m.Map == map;

    private void Tick(OutpostObject tower, Map map, DateTime now)
    {
        var s = tower.State;
        var r = RulesOf(s);
        var dt = Math.Clamp((now - s.LastTick).TotalSeconds, 0, 5);
        s.LastTick = now;
        Guards(s, map, now, r); // first: a fresh tower's guards stand before its state is read
        if (map.Players.Count == 0 && s.Claim != Claimable) return; // nobody there: only the guards come back
        var ticks = world.GameNow;
        var time = new DateTime(ticks, DateTimeKind.Utc);
        var frontline = Frontline(map.Cluster);
        // ContestedState: enemies near, guards fighting, the boss hurt, an attacker in the zone.
        var zoneCentre = s.Spawn.CaptureZone;
        var near = map.Players.Values.Where(p => p.Combat.Down is null && Vector2.Distance(p.PositionAt(time), zoneCentre) <= r.ParticipationRadius).ToList();
        var guards = s.Guards.Where(m => Standing(m, map)).Select(m => m!).ToList();
        var boss = Standing(s.Guards.FirstOrDefault(), map) ? s.Guards[0] : null;
        var fighting = guards.Any(m => m.Threat.Count > 0);
        if (fighting || near.Any(p => Pvp.IsFaction(p.Flag) && p.Flag != s.Owner)) s.MinorUntil = now + TimeSpan.FromSeconds(r.MinorAttackSeconds);
        if (boss is not null && boss.Threat.Count > 0 && boss.Health.At(ticks) < boss.Health.Max * r.BossMajorAlertHealth
            || boss is null && s.Guards.Length > 0 && fighting)
            s.MajorUntil = now + TimeSpan.FromSeconds(r.MajorAttackSeconds);
        // The capture zone: the flagged, alive, unmounted players within its radius, by faction.
        var inZone = near.Where(p => Vector2.Distance(p.PositionAt(time), zoneCentre) <= r.CaptureRadius).ToList();
        var claimers = inZone.Where(p => Pvp.IsFaction(p.Flag) && !p.IsMounted).ToList();
        s.Claim = !frontline ? Unclaimable : guards.Count > 0 ? Guarded : Claimable;
        Feedback(s, inZone, frontline, now);
        if (s.Claim == Claimable)
        {
            var factions = claimers.Select(p => p.Flag).Distinct().ToList();
            if (factions.Any(f => f != s.Owner)) s.MajorUntil = now + TimeSpan.FromSeconds(r.MajorAttackSeconds);
            Progress(s, r, factions, dt);
            if (s.Holder != s.Owner && s.Value >= 1000)
            {
                Captured(tower, map, now, r);
                return;
            }
        }
        else if (s.Holder != s.Owner || s.Value < 1000 || s.Rate != 0) Settle(s); // the guards are back (or the zone left the front): the owner's again
        s.Contested = now < s.MajorUntil ? MajorContested : now < s.MinorUntil ? MinorContested : NotContested;
        if (s.Contested != NotContested) s.CalmSince = now;
        Send(tower, map);
    }

    /// <summary>One tick of the points of a Claimable tower with <paramref name="factions"/> (the flagged factions in its zone).</summary>
    private void Progress(State s, OutpostData.Rules r, List<byte> factions, double dt)
    {
        s.Value = Math.Clamp(s.Value + s.Rate * dt, 0, 1000); // the last regime up to now, then the next one from here
        s.ValueAt = world.GameNow;
        var rate = 0d;
        if (factions.Count >= 2) s.Zone = MultipleTeamsInZone;
        else if (factions.Count == 1)
        {
            var f = factions[0];
            s.Zone = f == s.Owner ? OwnerTeamInZone : ClaimingTeamInZone;
            rate = f == s.Holder ? 1000 / Math.Max(0.1, s.Holder == s.Owner ? r.OwnerClaimSeconds : r.AttackerClaimSeconds)
                : -1000 / Math.Max(0.1, f == s.Owner ? r.OwnerUnclaimSeconds : r.AttackerUnclaimSeconds);
            if (rate < 0 && s.Value <= 0) (s.Holder, s.Value, rate) = (f, 0, 1000 / Math.Max(0.1, f == s.Owner ? r.OwnerClaimSeconds : r.AttackerClaimSeconds));
        }
        else
        {
            s.Zone = NoTeamInZone;
            rate = s.Holder == s.Owner ? 1000 / Math.Max(0.1, r.OwnerNoTeamClaimSeconds) : -1000 / Math.Max(0.1, r.AttackerNoTeamUnclaimSeconds);
            if (rate < 0 && s.Value <= 0) (s.Holder, s.Value, rate) = (s.Owner, 0, 1000 / Math.Max(0.1, r.OwnerNoTeamClaimSeconds));
        }
        if (rate > 0 && s.Value >= 1000 && s.Holder == s.Owner) rate = 0; // the owner's full: still
        if (rate < 0 && s.Value <= 0) rate = 0;
        if (Math.Abs(rate - s.Rate) > 1e-9) (s.Rate, s.Stamp) = (rate, world.GameNow); // a new regime: the client extrapolates from here
    }

    /// <summary>An attacker's 1000: it owns the tower. 367 to the map, the old guards go, the new owner's come after their respawndelay.</summary>
    private void Captured(OutpostObject tower, Map map, DateTime now, OutpostData.Rules r)
    {
        var s = tower.State;
        var old = s.Owner;
        s.Owner = s.Holder;
        Settle(s);
        s.Claim = Claimable;
        s.MajorUntil = now + TimeSpan.FromSeconds(r.MajorAttackSeconds);
        s.Contested = MajorContested;
        foreach (var m in s.Guards.Where(m => Standing(m, map))) SpawnManager.Despawn(m!, world, respawn: false);
        var roster = Roster(s, map);
        for (var i = 0; i < s.Guards.Length; i++)
        {
            s.Guards[i] = null;
            s.GuardsBack[i] = i < roster.Count ? now + TimeSpan.FromSeconds(roster[i].RespawnSeconds) : null;
            s.AfterClaim[i] = true;
        }
        map.SendToObservers(tower, new OutpostClaimedEvent { ddn = tower.Id, dfc = s.Owner, dfd = old, dfe = s.Contested });
        Send(tower, map, force: true);
        Log.Info("world", $"outpost {map.Cluster} {Short(s.Id)}: taken by {world.Factions.Name(s.Owner)} from {world.Factions.Name(old)}");
    }

    /// <summary>366 to the map's players when what the client draws changed (or <paramref name="force"/>).</summary>
    private static void Send(OutpostObject tower, Map map, bool force = false)
    {
        var s = tower.State;
        var key = (s.Owner, s.Holder, s.Claim, s.Contested, s.Stamp, s.Rate, s.Zone);
        if (!force && key == s.Sent) return;
        s.Sent = key;
        map.SendToObservers(tower, s.BuildUpdate(tower.Id));
    }

    /// <summary>SimpleFeedback 470 to a player in the zone of a tower it cannot claim (at most every 5 s each).</summary>
    private void Feedback(State s, List<Player> inZone, bool frontline, DateTime now)
    {
        foreach (var p in inZone)
        {
            // A Claimable tower: an unflagged or mounted player; a tower off the front line: a player of another faction.
            byte? why = s.Claim == Claimable ? !Pvp.IsFaction(p.Flag) ? NotFlagged : p.IsMounted ? (p.Flag == s.Owner ? MountedOwner : Mounted) : null
                : !frontline && Pvp.IsFaction(p.Flag) && p.Flag != s.Owner ? NotFrontline : null;
            if (why is not { } code || _feedback.TryGetValue(p.Id, out var last) && now - last < FeedbackEvery) continue;
            _feedback[p.Id] = now;
            p.Session.Send(new SimpleFeedbackEvent { dym = code });
        }
        if (_feedback.Count > 256) foreach (var id in _feedback.Where(f => now - f.Value > FeedbackEvery).Select(f => f.Key).ToList()) _feedback.Remove(id);
    }

    // ---- guards

    /// <summary>The owner's roster for the cluster tier, one entry per guard spot (boss first): mob and respawndelay.</summary>
    private List<(MobTemplate Mob, double RespawnSeconds)> Roster(State s, Map map)
    {
        var list = new List<(MobTemplate, double)>();
        if (!O.OutpostGuards || Data is not { } data || world.Clusters is not { } store) return list;
        foreach (var g in data.Guards(RulesOf(s).GuardSetup, s.Owner, map.Template?.Name.Tier ?? 4))
        {
            if (store.Mobs.Find(g.Mob) is not { } template)
            {
                Combat.LogOnce("outpost-mob:" + g.Mob, $"outpost guard {g.Mob} is not in mobs.xml: not spawned");
                continue;
            }
            for (var i = 0; i < g.Amount; i++) list.Add((template, g.RespawnSeconds));
        }
        return list;
    }

    /// <summary>
    /// The guards stand: a spot never filled gets its guard now, a dead one's comes back after its respawndelay once the tower has been quiet
    /// for respawnblockdelay (after a capture at once).
    /// </summary>
    private void Guards(State s, Map map, DateTime now, OutpostData.Rules r)
    {
        if (s.Guards.Length == 0) return;
        List<(MobTemplate Mob, double RespawnSeconds)>? roster = null;
        for (var i = 0; i < s.Guards.Length; i++)
        {
            var m = s.Guards[i];
            if (Standing(m, map)) continue;
            roster ??= Roster(s, map);
            if (i >= roster.Count) continue;
            if (m is not null) // died (or was taken off): its respawn starts now
            {
                s.Guards[i] = null;
                (s.GuardsBack[i], s.AfterClaim[i]) = (now + TimeSpan.FromSeconds(roster[i].RespawnSeconds), false);
                continue;
            }
            if (s.GuardsBack[i] is { } back && (now < back || !s.AfterClaim[i] && (s.Contested != NotContested || now - s.CalmSince < TimeSpan.FromSeconds(r.RespawnBlockSeconds))))
                continue;
            var spot = s.Spawn.Guards[i];
            var owner = s.Owner;
            s.Guards[i] = SpawnManager.SpawnAt(map, roster[i].Mob, spot.Position, spot.Direction, world, behavior: "GUARD",
                foes: p => Pvp.IsFaction(p.Flag) && p.Flag != owner);
            (s.GuardsBack[i], s.AfterClaim[i]) = (null, false);
        }
    }

    // ---- the zone

    /// <summary>Whether the towers of <paramref name="cluster"/> can be taken (FactionWarfare:Claimable; Frontline: an exit to a zone or city another faction controls).</summary>
    public bool Frontline(string cluster)
    {
        switch (O.Claimable)
        {
            case OutpostClaimRule.All: return true;
            case OutpostClaimRule.None: return false;
        }
        if (world.Clusters?.WorldIfLoaded?.Find(cluster) is not { } info) return false;
        var own = ControllerOf(cluster);
        return info.Exits.Select(e => ControllerOf(e.TargetCluster)).Any(f => Pvp.IsFaction(f) && f != own);
    }

    /// <summary>A zone's controller (its duchy's faction until another took it), a city's or other duchy cluster's faction; 0 outside the duchies.</summary>
    public byte ControllerOf(string cluster) =>
        _zones.TryGetValue(cluster, out var z) ? z.Controller : world.Factions.Data?.ClusterFaction.GetValueOrDefault(cluster) ?? 0;

    /// <summary>The towers of <paramref name="map"/> in world.xml order (468 key 3, ClusterInfoUpdate 19 / 27): each one's state, null when none.</summary>
    private List<State?> InOrder(string cluster) =>
        world.Clusters?.WorldIfLoaded?.Find(cluster) is { } info ? [.. info.Outposts.Select(o => _states.GetValueOrDefault(o.Id))] : [];

    /// <summary>The control of the zone of <paramref name="map"/>: a majority on its way to control, then 468 when anything in it changed.</summary>
    private void Control(Map map, DateTime now)
    {
        if (!_zones.TryGetValue(map.Cluster, out var z)) return;
        var towers = InOrder(map.Cluster);
        var majority = towers.Where(t => t is not null).GroupBy(t => t!.Owner).FirstOrDefault(g => g.Count() * 2 > towers.Count)?.Key ?? (byte)0;
        if (majority == 0 || majority == z.Controller) (z.Majority, z.MajoritySince) = (majority, null);
        else
        {
            if (z.Majority != majority) (z.Majority, z.MajoritySince) = (majority, now);
            if (now - z.MajoritySince >= TimeSpan.FromSeconds(O.ControlSeconds))
            {
                var old = z.Controller;
                (z.Controller, z.MajoritySince) = (majority, null);
                Log.Info("world", $"zone {map.Cluster}: controlled by {world.Factions.Name(majority)} (was {world.Factions.Name(old)})");
            }
        }
        if (Build(map) is not { } state) return;
        var key = string.Join(",", state.b7a ?? []) + "|" + string.Join(",", state.b7b ?? []) + "|" + Convert.ToHexString(state.b7c ?? []) + "|" + state.b7d
            + "|" + string.Join("", (state.b7e ?? []).Select(b => b ? '1' : '0')) + "|" + state.b7f;
        if (key == z.Sent) return;
        z.Sent = key;
        foreach (var p in map.Players.Values) p.Session.Send(state);
    }

    /// <summary>
    /// FactionWarfareClusterState 468 of <paramref name="map"/> (CLIENT.md: keys 1/2 the same length ≤ 6, key 3 one per world.xml outpost, key 5
    /// bool[6], else the client skips the update): the controller with its 1000 points (a rising majority with its share of
    /// FactionWarfare:ControlSeconds beside it), the towers' owners, the flagged presence, the front line; key 9 the duchy's faction.
    /// Null for a map without towers.
    /// </summary>
    private FactionWarfareClusterStateEvent? Build(Map map)
    {
        if (!_zones.TryGetValue(map.Cluster, out var z)) return null;
        var towers = InOrder(map.Cluster);
        if (towers.Count == 0) return null;
        var now = UtcNow;
        var factions = new List<byte> { z.Controller };
        var points = new List<long> { Full };
        if (z.MajoritySince is { } since && O.ControlSeconds > 0)
        {
            var share = Math.Clamp((now - since).TotalSeconds / O.ControlSeconds, 0, 1);
            factions.Add(z.Majority);
            points[0] = (long)Math.Round(Full * (1 - share));
            points.Add(Full - points[0]);
        }
        var presence = new bool[6];
        foreach (var p in map.Players.Values)
            if (Pvp.IsFaction(p.Flag)) presence[p.Flag - 1] = true;
        return new FactionWarfareClusterStateEvent
        {
            b69 = world.GameNow, b7a = [.. factions], b7b = [.. points], b7c = [.. towers.Select(t => t?.Owner ?? z.Controller)], b7d = z.Controller,
            b7e = presence, b7f = Frontline(map.Cluster) ? FrontLine : BackLine, b7i = world.Factions.Data?.ClusterFaction.GetValueOrDefault(map.Cluster) ?? 0,
        };
    }

    /// <summary>The world map's ClusterInfoUpdate of a warfare zone (<see cref="Network.WorldSession.ClusterInfo"/>): key 19 the towers' ContestedState, 27 their owners.</summary>
    public void FillWorldMap(ClusterInfoUpdateEvent e, ClusterInfo cluster)
    {
        if (!O.Outposts || cluster.Outposts.Count == 0) return;
        var towers = InOrder(cluster.Id);
        var owner = ControllerOf(cluster.Id);
        e.bys = [.. towers.Select(t => t?.Contested ?? 0)];
        e.by0 = [.. towers.Select(t => t?.Owner ?? owner)];
    }

    // ---- GM

    /// <summary>.outpost: the towers of <paramref name="map"/>, nearest first from <paramref name="at"/>.</summary>
    public IEnumerable<string> Describe(Map map, Vector2 at)
    {
        var towers = _towers.Where(t => t.Map == map).OrderBy(t => Vector2.Distance(t.Position, at)).ToList();
        if (towers.Count == 0) yield return "no faction warfare towers on this map";
        var z = _zones.GetValueOrDefault(map.Cluster);
        if (z is not null)
            yield return $"zone {map.Cluster}: controller {world.Factions.Name(z.Controller)}, {(Frontline(map.Cluster) ? "front line" : "back line")}"
                + (z.MajoritySince is { } since ? string.Create(CultureInfo.InvariantCulture, $", {world.Factions.Name(z.Majority)} holds the majority since {(UtcNow - since).TotalSeconds:0} s") : "");
        foreach (var t in towers)
            yield return string.Create(CultureInfo.InvariantCulture,
                $"#{t.Id} at ({t.Position.X:0}, {t.Position.Y:0}) {Vector2.Distance(t.Position, at):0} m: {t.State}, guards {t.State.Guards.Count(m => Standing(m, map))}/{t.State.Guards.Length}");
    }

    /// <summary>.outpost owner &lt;faction&gt; / clear: the nearest tower gets that owner (guards anew), or loses its guards (Claimable).</summary>
    public string Gm(Map map, Vector2 at, string action, byte faction)
    {
        if (_towers.Where(t => t.Map == map).MinBy(t => Vector2.Distance(t.Position, at)) is not { } tower) return "no faction warfare tower on this map";
        var s = tower.State;
        var now = UtcNow;
        switch (action)
        {
            case "owner":
                if (!Pvp.IsFaction(faction)) return "a faction 1-6 (Highland 1, Forest 2, Steppe 3, Mountain 4, Swamp 5, Caerleon 6)";
                (s.Holder, s.Value) = (faction, 1000);
                Captured(tower, map, now, RulesOf(s));
                return $"#{tower.Id}: owner {world.Factions.Name(faction)}";
            case "clear":
                foreach (var m in s.Guards.Where(m => Standing(m, map))) SpawnManager.Despawn(m!, world, respawn: false);
                return $"#{tower.Id}: its guards are gone (they come back as after deaths)";
            default:
                return "actions: owner <faction 1-6>, clear";
        }
    }

    private static string Short(Guid id) => id.ToString()[..8];
}
