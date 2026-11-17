using System.Globalization;
using AlbionLocal.Photon;
using AlbionLocal.Server.Data;
using AlbionLocal.Server.Entities;
using AlbionLocal.Server.Protocol;

namespace AlbionLocal.Server.Systems;

/// <summary>
/// StageR fw-zone (analysis/work/stageR/fw-zone/SPEC.md §2; FactionWarfare:RedZoneEvents / RedZoneRegion): the bandit assault in the red (lethal)
/// duchies, factionwarfareredzoneevent.xml.
/// <list type="bullet">
/// <item><b>Schedule</b>: at each eventstarttime (a times.xml Time + offsetseconds −900) the start is rolled with its chance (Always: every one;
/// None: only .redzone start). Announcement for advancenotificationminutes (15) → Phase1 (45 min) → Phase2 (30 min) → the end; this fits the
/// captured RedZoneWorldMapEvent (Europe 19:36 UTC: Phase1 until 19:45) [infer].</item>
/// <item><b>Messages</b>: RedZonePlayerNotification 476 {0 kind: 0 announced, 1 begun, 2 phase 2, 3 ended; 1 the place 1-3, 2 the faction points
/// received, 3 the faction} (FactionWarfareRedZoneEventPopupGui.ShowRedZoneEventNotification 0x181173610; kind 0 also clears the client's
/// standings, dex.ag 0x180ae99b0) to every enlisted player [assume: who gets it]; RedZoneWorldMapEvent 479 {0 the phase's end, 1 the phase,
/// 2 the phase 2 duchies' uniquenames} (dex.af 0x180ae9700, bcg.abv looks each name up) to every player at a change, with the world map's
/// ClusterInfoUpdates (as captured) and at the Join; FactionWarfareClusterState 468 keys 11 DuchyRedZoneEventState, 12 the phase, 13 its end
/// in the red duchies' zones.</item>
/// <item><b>Phase 1</b> hands the red duchies' towers to Caerleon, the bandits (the third-party write-up; "overrun by bandits - capture Red
/// Regions", @FACTION_WARFARE_FORTRESS_GUI_STATUS_REDZONE_EVENT_DESCRIPTION) [infer]; they go back to their duchy's faction at the end [assume].
/// <b>Phase 2</b> runs in two red duchies drawn at random ("the battle has intensified within the remaining Provinces") [assume: which two], their
/// fortresses show FactionFortressState 3.</item>
/// <item><b>Standings</b> (RequestRedZoneEventStandings 540 → a7m[6] by faction − 1, de3.h 0x180ad9eb0): each tower of an active red duchy
/// adds supplygeneration outpost pointspersecond (0.1853) × the phase's rzesupplymultiplier per second to its owner [infer: the event counts
/// supply]. At the end the factions are placed by standings; each enlisted player who was flagged in a red zone during the event gets the
/// place's bonus (firstbonus 496217.11 … sixthbonus) / max(participants of the faction, minimumdivisor 20) faction points [infer].</item>
/// </list>
/// Not done: the fortress chest and weapon caches of phase 2 (478 RedZoneFortressEventChestOpened, FACTION_TREASURE_MEDIUM_FORTRESS_RZE), the
/// phase buffs (FACTION_PVE_BONUS_RZE_PHASE_n, fame/silver/loot 1.15 / 1.3), the treasure modifiers, 477 (a reset; nothing to clean up here),
/// 671 (the client of 345319 has no handler for it).
/// </summary>
public sealed class RedZoneEvents(World world)
{
    /// <summary>bch.RedZoneEventPhase (client enum).</summary>
    public const byte None = 0, Announcement = 1, Phase1 = 2, Phase2 = 3, End = 4;

    /// <summary>DuchyRedZoneEventState (client enum).</summary>
    public const byte Inactive = 0, RunningPhaseInactive = 1, PhaseActive = 2;

    /// <summary>RedZonePlayerNotification 476 key 0 (bch.a, the popup's four texts).</summary>
    public const byte Announced = 0, Begun = 1, Intensified = 2, Ended = 3;

    private RedZoneEventData? _data;
    private bool _read;
    private readonly double[] _standings = new double[6];
    private readonly Dictionary<Guid, byte> _participants = [];
    private readonly List<string> _phase2 = [];
    private long _lastTick;
    private DateTime? _nextCheck;
    private Random? _random;

    private FactionWarfareOptions O => world.Config.FactionWarfare;
    private Random Random => _random ??= world.MapSeed is { } seed ? new Random(seed + 11) : new Random();
    private DateTime UtcNow => new(world.GameNow, DateTimeKind.Utc);

    public byte Phase { get; private set; }

    /// <summary>The current phase's end (game ticks).</summary>
    public long Until { get; private set; }

    /// <summary>The duchies of phase 2.</summary>
    public IReadOnlyList<string> Phase2Duchies => _phase2;

    /// <summary>factionwarfareredzoneevent(_region).bin over times(_region).bin; the 345319 defaults without client data.</summary>
    public RedZoneEventData Data
    {
        get
        {
            if (_read) return _data ?? RedZoneEventData.Default;
            _read = true;
            var suffix = O.RedZoneRegion switch { RedZoneRegion.Europe => "_europe", RedZoneRegion.Asia => "_asia", _ => "" };
            if (world.Clusters?.Folder is { } folder)
                try
                {
                    var times = RedZoneEventData.Times(GameDataFile.Open(folder, $"times{suffix}.bin").Root());
                    _data = RedZoneEventData.Load(GameDataFile.Open(folder, $"factionwarfareredzoneevent{suffix}.bin").Root(), times);
                }
                catch (Exception e) when (e is IOException or InvalidDataException or UnauthorizedAccessException)
                {
                    Log.Warn("faction", $"factionwarfareredzoneevent{suffix}.bin not read: {e.Message}; no scheduled bandit assault");
                }
            return _data ?? RedZoneEventData.Default;
        }
    }

    /// <summary>The red (lethal, not home) duchies.</summary>
    private IEnumerable<FactionWarfareData.Duchy> RedDuchies => world.Factions.Data?.Duchies.Where(d => d.Lethal && !d.Home) ?? [];

    /// <summary>The standings by faction − 1 as a7m raw (540).</summary>
    public long[] Standings() => [.. _standings.Select(v => (long)Math.Round(v * 10000))];

    // ---- the tick

    public void Update()
    {
        var now = world.GameNow;
        var dt = _lastTick == 0 ? 0 : Math.Clamp(TimeSpan.FromTicks(now - _lastTick).TotalSeconds, 0, 5);
        _lastTick = now;
        if (Phase == None)
        {
            Schedule();
            return;
        }
        if (Phase is Phase1 or Phase2) Accumulate(dt);
        if (now >= Until) Advance();
    }

    /// <summary>The next eventstarttime: rolled once when it is reached.</summary>
    private void Schedule()
    {
        if (O.RedZoneEvents == RedZoneEventRule.None || Data.Starts.Count == 0) return;
        var now = UtcNow;
        _nextCheck ??= now;
        if (now - _nextCheck < TimeSpan.FromSeconds(1)) return; // once a second is enough
        var starts = Data.Starts.Select(s => (Start: s, At: now.Date + s.TimeOfDay + TimeSpan.FromSeconds(s.OffsetSeconds))).ToList();
        foreach (var (start, at) in starts)
        {
            // A start between the last check and now (a day's times, shifted: −900 s puts 00:00 at 23:45 of the day before).
            foreach (var when in new[] { at, at.AddDays(1), at.AddDays(-1) })
                if (when > _nextCheck && when <= now)
                {
                    _nextCheck = now;
                    if (O.RedZoneEvents == RedZoneEventRule.Always || Random.NextDouble() < start.Chance) Begin();
                    else Log.Info("faction", $"bandit assault at {when:HH:mm} UTC not rolled (chance {start.Chance:0.##})");
                    return;
                }
        }
        _nextCheck = now;
    }

    /// <summary>The event starts: announcement.</summary>
    public void Begin()
    {
        if (Phase != None) return;
        Array.Clear(_standings);
        _participants.Clear();
        _phase2.Clear();
        Enter(Announcement, Data.AnnouncementMinutes);
        Notify(Announced);
    }

    /// <summary>The next phase (also .redzone next).</summary>
    public void Advance()
    {
        switch (Phase)
        {
            case Announcement:
                Enter(Phase1, Data.Phases[0].Minutes);
                world.Outposts.Force(RedDuchies.SelectMany(d => d.Clusters), 6); // the bandits hold the red zones [infer]
                Notify(Begun);
                break;
            case Phase1:
                _phase2.Clear();
                _phase2.AddRange(RedDuchies.OrderBy(_ => Random.Next()).Take(2).Select(d => d.Name));
                Enter(Phase2, Data.Phases.Count > 1 ? Data.Phases[1].Minutes : 30);
                world.Fortresses.RedZone(_phase2);
                Notify(Intensified);
                break;
            case Phase2:
                Finish();
                break;
        }
    }

    private void Enter(byte phase, double minutes)
    {
        Phase = phase;
        Until = world.GameNow + TimeSpan.FromMinutes(minutes).Ticks;
        Broadcast();
        world.Outposts.Refresh();
        Log.Info("faction", $"bandit assault: phase {phase} until {new DateTime(Until, DateTimeKind.Utc):HH:mm:ss} UTC" + (_phase2.Count > 0 ? $" in {string.Join(", ", _phase2)}" : ""));
    }

    /// <summary>The end: the factions placed by standings, the participants' bonus points, the towers back to their duchies.</summary>
    private void Finish()
    {
        var places = Enumerable.Range(1, 6).Where(f => _standings[f - 1] > 0).OrderByDescending(f => _standings[f - 1]).ToList();
        var counts = _participants.Values.GroupBy(f => f).ToDictionary(g => g.Key, g => g.Count());
        foreach (var p in Online())
        {
            var place = places.IndexOf(p.Faction) + 1;
            long raw = 0;
            if (place > 0 && _participants.ContainsKey(p.Character.Id) && place <= Data.Bonuses.Count)
            {
                var points = Data.Bonuses[place - 1] / Math.Max(Data.MinimumDivisor, counts.GetValueOrDefault(p.Faction));
                raw = (long)Math.Floor(points * 10000);
                world.Factions.Earn(p, raw, null);
            }
            p.Session.Send(new RedZonePlayerNotificationEvent { dta = Ended, dtb = (byte)Math.Max(0, place), dtc = raw, dtd = p.Faction });
        }
        world.Outposts.Force(RedDuchies.SelectMany(d => d.Clusters), null);
        world.Fortresses.RedZone([]);
        _phase2.Clear();
        Log.Info("faction", $"bandit assault ended: {string.Join(", ", places.Select(f => $"{world.Factions.Name((byte)f)} {_standings[f - 1]:0}"))}");
        Phase = None;
        Until = world.GameNow;
        _nextCheck = UtcNow; // the start times that passed during the event are not rolled afterwards
        Broadcast();
        world.Outposts.Refresh();
    }

    /// <summary>Each second of a phase: the towers of the active red duchies give their owners supply; the flagged players there take part.</summary>
    private void Accumulate(double dt)
    {
        if (world.Factions.Data is not { } fw || dt <= 0) return;
        var active = RedDuchies.Where(d => Phase == Phase1 || _phase2.Contains(d.Name)).SelectMany(d => d.Clusters).ToHashSet(StringComparer.Ordinal);
        var phase = Data.Phases[Math.Min(Data.Phases.Count - 1, Phase == Phase1 ? 0 : 1)];
        foreach (var (cluster, owner) in world.Outposts.Owners())
            if (active.Contains(cluster) && Pvp.IsFaction(owner)) _standings[owner - 1] += fw.OutpostSupplyPerSecond * phase.SupplyMultiplier * dt;
        foreach (var map in world.Maps.Maps.Where(m => active.Contains(m.Cluster)))
            foreach (var p in map.Players.Values)
                if (!p.IsBot && Pvp.IsFaction(p.Faction) && p.Flag == p.Faction) _participants[p.Character.Id] = p.Faction;
    }

    private IEnumerable<Player> Online() => world.Maps.Maps.SelectMany(m => m.Players.Values).Where(p => !p.IsBot);

    private void Notify(byte kind)
    {
        foreach (var p in Online().Where(p => Pvp.IsFaction(p.Faction)))
            p.Session.Send(new RedZonePlayerNotificationEvent { dta = kind, dtd = p.Faction });
    }

    private void Broadcast()
    {
        var e = WorldMapEvent();
        foreach (var p in Online()) p.Session.Send(e);
    }

    /// <summary>RedZoneWorldMapEvent 479.</summary>
    public RedZoneWorldMapEventEvent WorldMapEvent() => new() { dte = Until, dtf = Phase, dtg = Phase == Phase2 && _phase2.Count > 0 ? [.. _phase2] : null };

    /// <summary>The Join (after the cluster-wide objects) and the world map: 479 while an event runs.</summary>
    public void Joined(Player p)
    {
        if (!p.IsBot && Phase != None) p.Session.Send(WorldMapEvent());
    }

    /// <summary>468 keys 11-13 for a zone of a red duchy while the event runs.</summary>
    public void Fill(FactionWarfareClusterStateEvent e, string cluster)
    {
        if (Phase == None || world.Factions.Data?.DuchyOf(cluster) is not { Lethal: true, Home: false } duchy) return;
        e.b7k = Phase == Phase1 || Phase == Phase2 && _phase2.Contains(duchy.Name) ? PhaseActive : RunningPhaseInactive;
        e.b7l = Phase;
        e.b7m = Until;
    }

    // ---- GM

    public string Describe() => Phase == None ? "no bandit assault runs" + (Data.Starts.Count > 0 ? $" ({Data.Starts.Count} start times a day, {O.RedZoneEvents})" : "")
        : string.Create(CultureInfo.InvariantCulture, $"bandit assault phase {Phase} until {new DateTime(Until, DateTimeKind.Utc):HH:mm:ss} UTC")
          + (_phase2.Count > 0 ? $" in {string.Join(", ", _phase2)}" : "")
          + "; standings " + string.Join(", ", Enumerable.Range(1, 6).Select(f => $"{world.Factions.Name((byte)f)} {_standings[f - 1]:0}"))
          + $"; {_participants.Count} taking part";
}
