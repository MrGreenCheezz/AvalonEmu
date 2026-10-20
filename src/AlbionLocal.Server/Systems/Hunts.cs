using System.Globalization;
using System.Numerics;
using AlbionLocal.Photon;
using AlbionLocal.Server.AI;
using AlbionLocal.Server.Data;
using AlbionLocal.Server.Entities;
using AlbionLocal.Server.Game;
using AlbionLocal.Server.Protocol;

namespace AlbionLocal.Server.Systems;

/// <summary>
/// The hunts / tracking (stageN hunts, analysis/work/stageN/hunts/SPEC.md, "SPEC" here), on dynamichunt.xml (<see cref="HuntData"/>):
/// <list type="bullet">
/// <item><b>Find</b>: a tracking toolkit (Tn_2H_TOOL_TRACKING, kind trackingitem) in the main hand and UseFindTrackSpellFromItemPrepare (498):
/// the Tn_FIND_TRACK cast (castingtime, recastdelay; moving cancels it), then wild tracks near the player (up to <see cref="WildPerFind"/>
/// within the spell's findtrack radius, on mob spawn points; a target of the toolkit's tier or one below, solo or group by the party) and
/// MinimapHuntTrackMarkers (563) for the findtrack time to the finder and its party on the map, or NoTracksFound.</item>
/// <item><b>Read a track</b>: InteractWithTrackStart (499) within <see cref="InteractRange"/>: a channel of the hunt's trackingtime (−11 %
/// with a toolkit), InteractWithTrackStart / Finished events, HuntTrackUsed; moving or op 500 cancels. A wild track starts a hunt.</item>
/// <item><b>The hunt</b>: HuntStarted (554) to the finder and its party members online (they share it); a chain of quests, each at a spot
/// filterspots 100-400 m on (a mob spawn point away from the last ones), with a trail of used footprints, a minimap ping 20-60 m off the
/// spot and a system line with the distance and direction: Track quests (read the track there; a fight quest's guards), Escape quests (the
/// target waits and flees at its HP threshold, too many attackers, players nearby or its combat timers, via <see cref="BehaviourAI.Escape"/>;
/// its health carries over) and the Final quest (kill it; an escape fails the quest and the trail goes on). The final quest's chance follows
/// the data (initial, per success, forced at maxquestsuntilfinalquest).</item>
/// <item><b>Reward</b>: the kill's normal loot (the _TR_ mob's loot list, <see cref="Combat.Kill"/>), plus the final quest's fame × (1 +
/// the toolkit's tracking fame bonus) for every member, HuntFinished (555).</item>
/// <item><b>Ends</b>: HuntAbort (497), a quest timeout, no member online, a new hunt by a member: HuntAborted (556) with the reason.</item>
/// </list>
/// In memory only (a hunt is lost on a restart). Touched only under <see cref="World.Sync"/>.
/// </summary>
public sealed class Hunts(World world)
{
    /// <summary>HuntAbortReason (Albion.Common.GameData.DynamicHunt).</summary>
    public const byte NewHuntStarted = 2, PlayerWentOffline = 8, QuestTimedOut = 10, PlayerRequest = 11, NoPlayersInCluster = 13, Cheat = 15;

    /// <summary>Wild tracks a find makes sure of within its radius.</summary>
    public const int WildPerFind = 2;

    /// <summary>How close a player must stand to read a track (m).</summary>
    public const float InteractRange = 6;

    /// <summary>The radius in which players count for escapeonplayersinproximity (m; the final quests' combat marker).</summary>
    public const float ProximityRange = 20;

    /// <summary>How long a hunt waits for a member to be back in game (a cluster change, a relog) before it is aborted.</summary>
    public static readonly TimeSpan OfflineGrace = TimeSpan.FromSeconds(60);

    /// <summary>How long a wild track lies before it goes.</summary>
    public static readonly TimeSpan WildLifetime = TimeSpan.FromMinutes(10);

    /// <summary>The share of trackingtime a toolkit saves (trackingitem trackingtimereduction, 0.11 for every tier).</summary>
    public const double ToolTimeReduction = 0.11;

    public sealed class Hunt
    {
        public required Guid Id { get; init; }
        public required HuntDefinition Definition { get; init; }
        public required HuntTarget Target { get; init; }
        public required MobTemplate Mob { get; init; }
        public required Map Map { get; init; }
        public required List<Guid> Members { get; init; }

        /// <summary>The toolkit's trackingfamebonus of the starter (0.05 a tier above T3).</summary>
        public double FameBonus { get; init; }

        public int QuestCount { get; set; }
        public double FinalChance { get; set; }

        /// <summary>HP_TARGET: the target's health share, carried from escape to escape.</summary>
        public float HpTarget { get; set; } = 1;

        public HuntQuest? Quest { get; set; }
        public HuntQuestPick? Pick { get; set; }
        public string? LastQuest { get; set; }
        public Vector2 Location { get; set; }

        /// <summary>LOCATION_HISTORY (historymaxsize 10, the start included): spots are chosen away from them.</summary>
        public List<Vector2> History { get; } = [];

        public TimeSpan QuestEnds { get; set; }

        /// <summary>Since when no member has been in game (World.Now); null while one is.</summary>
        public TimeSpan? AloneSince { get; set; }

        /// <summary>The track to read in a Track quest; the footprints of the trail.</summary>
        public HuntTrackObject? Track { get; set; }
        public List<HuntTrackObject> Footprints { get; } = [];

        public Mob? TargetMob { get; set; }
        public List<Mob> Guards { get; } = [];

        /// <summary>GameTimeStamp of the target's first engagement in this quest; of the escape start, its health then.</summary>
        public long? EngagedAt { get; set; }
        public long? EscapeAt { get; set; }
        public float FleeHp { get; set; }
        public string? EscapeReason { get; set; }

        public override string ToString() => $"hunt {Id.ToString("N")[..8]} {Definition.Name} for {Target.Name}";
    }

    private sealed record FindCast(Player Player, ItemTemplate Tool, SpellTemplate? Spell, long End, Vector2 At, float Radius, double Seconds);

    private sealed record TrackRead(Player Player, HuntTrackObject Track, long End, Vector2 At);

    private HuntData? _data;
    private bool _loadFailed;
    private readonly List<Hunt> _hunts = [];
    private readonly Dictionary<Player, FindCast> _finding = [];
    private readonly Dictionary<Player, TrackRead> _reading = [];
    private readonly Dictionary<Guid, long> _findReady = [];
    private readonly List<HuntTrackObject> _wild = [];
    private Random? _random;

    private Random Random => _random ??= world.MapSeed is { } seed ? new Random(seed) : new Random();

    /// <summary>The running hunts.</summary>
    public IReadOnlyList<Hunt> All => _hunts;

    /// <summary>The wild tracks lying on the maps.</summary>
    public IReadOnlyList<HuntTrackObject> WildTracks => _wild;

    /// <summary>dynamichunt.bin of the client's GameData folder, read at the first use; null without it (logged once).</summary>
    public HuntData? Data
    {
        get
        {
            if (_data is not null || _loadFailed) return _data;
            try
            {
                if (world.Clusters is not { } store) throw new InvalidDataException("no client data");
                _data = HuntData.Load(GameDataFile.Open(store.Folder, "dynamichunt.bin").Root());
                Log.Info("data", $"dynamichunt.bin: {_data.Targets.Count} targets, {_data.Hunts.Count} hunts, {_data.Quests.Count} quests");
            }
            catch (Exception e) when (e is InvalidDataException or IOException)
            {
                _loadFailed = true;
                Log.Warn("data", $"dynamichunt.bin: {e.Message}; no hunts");
            }
            return _data;
        }
    }

    /// <summary>The hunt <paramref name="character"/> takes part in; null when none.</summary>
    public Hunt? Of(Guid character) => _hunts.FirstOrDefault(h => h.Members.Contains(character));

    /// <summary>The tracking toolkit in <paramref name="p"/>'s main hand; null when none.</summary>
    public static Item? Toolkit(Player p) => p.Inventory.Equipment[0] is { Template.Kind: "trackingitem" } tool ? tool : null;

    /// <summary>trackingfamebonus of a toolkit tier: T4 0.05 … T8 0.25 (T3 none).</summary>
    public static double FameBonusOf(int tier) => Math.Max(0, (tier - 3) * 0.05);

    /// <summary>A player event to the player and its observers.</summary>
    private static void ToAll(Player p, AlbionEvent e)
    {
        p.Session.Send(e);
        p.SendToObservers(e);
    }

    private static void Tell(Player p, string text) => p.Session.Send(new SystemMessageEvent { d0p = text, d0q = 1 });

    // ---- find ----

    /// <summary>UseFindTrackSpellFromItemPrepare (498): starts the find cast; returns the ReturnCode (0 started, 1 refused) and why.</summary>
    public (short Code, string? Why) Find(Player p)
    {
        if (p.Map is not { } map) return (1, "on no map");
        if (Toolkit(p) is not { } tool) return Refuse(p, "no tracking toolkit in the main hand");
        if (!WorldEvents.Fits(map)) return Refuse(p, "no tracks here (open-world maps with mobs only)");
        if (Data is null) return Refuse(p, "no dynamichunt data");
        if (_finding.ContainsKey(p) || _reading.ContainsKey(p)) return (1, "busy");
        var now = world.GameNow;
        if (_findReady.GetValueOrDefault(p.Character.Id) > now) return Refuse(p, "the toolkit is not ready yet");
        var spell = world.Spells?.Template($"T{tool.Template.Tier}_FIND_TRACK");
        var effect = spell?.Effects.FirstOrDefault(e => e.Name.LocalName == "findtrack");
        var radius = effect is null ? 200 : SpellTemplate.Float(effect, "radius", 200);
        var seconds = effect is null ? 20 : SpellTemplate.Float(effect, "time", 20);
        var cast = spell?.CastingTime ?? 7;
        var at = p.PositionAt(DateTime.UtcNow);
        var end = now + SpellCasts.Seconds(cast);
        _finding[p] = new FindCast(p, tool.Template, spell, end, at, radius, seconds);
        _findReady[p.Character.Id] = end + SpellCasts.Seconds(spell?.RecastDelay ?? 20);
        p.SendToObservers(new CastStartEvent { ddn = p.Id, bwb = now, bwc = at, bwe = end, bwf = spell?.Id ?? -1, bwg = -1, bwh = p.Id });
        Log.Info("hunt", string.Create(CultureInfo.InvariantCulture, $"'{p.Character.Name}' looks for tracks with {tool.Template.Name}: {cast:0.#} s, radius {radius:0} m"));
        return (0, null);
    }

    private static (short, string?) Refuse(Player p, string why)
    {
        Tell(p, $"Tracking: {why}.");
        return (1, why);
    }

    private void Found(FindCast f)
    {
        var p = f.Player;
        if (p.Map is not { } map || Data is not { } data) return;
        var at = p.PositionAt(DateTime.UtcNow);
        var group = PartyOnMap(p).Count > 1;
        var near = _wild.Where(t => t.Map == map && !t.Used && Vector2.Distance(t.Position, at) <= f.Radius).ToList();
        for (var tries = 0; near.Count < WildPerFind && tries < 6; tries++)
        {
            var spot = WorldEvents.PointNear(map, at, (15, f.Radius));
            if (Vector2.Distance(spot, at) < 5 || near.Any(t => Vector2.Distance(t.Position, spot) < 10)) continue;
            if (PickTarget(data, f.Tool.Tier, group) is not { } pick) break;
            var track = new HuntTrackObject(world.ObjectIds.Next(), map.Cluster, spot, (float)(Random.NextDouble() * 360), pick.Target, pick.Hunt, Guid.Empty)
            {
                Expires = world.Now + WildLifetime,
            };
            map.AddObject(track);
            _wild.Add(track);
            near.Add(track);
        }
        var to = PartyOnMap(p);
        Log.Debug("hunt", $"find by '{p.Character.Name}' at {at}: {near.Count} wild track(s) within {f.Radius:0} m");
        if (near.Count == 0)
        {
            p.Session.Send(new NoTracksFoundEvent());
            Tell(p, "Tracking: no tracks around here.");
            return;
        }
        var markers = new MinimapHuntTrackMarkersEvent
        {
            crd = map.Cluster, cre = p.Id, crf = [.. near.Select(t => t.Target.Index)], crg = [.. near.Select(t => t.Hunt.Index)],
            crh = [.. near.Select(t => t.Used)], cri = [.. near.Select(t => t.Position)], crj = world.GameNow + SpellCasts.Seconds(f.Seconds),
            crk = at, crl = f.Radius,
        };
        foreach (var m in to) m.Session.Send(markers);
        foreach (var m in to)
            Tell(m, $"Tracking: {near.Count} track{(near.Count == 1 ? "" : "s")} found: "
                + string.Join(", ", near.Select(t => $"{Name(t.Target)} {Hint(m.PositionAt(DateTime.UtcNow), t.Position)}")) + ".");
        Log.Info("hunt", $"'{p.Character.Name}' found {near.Count} track(s): {string.Join(", ", near)}");
    }

    /// <summary>A target and its hunt for a found track: tier = the toolkit's or one below (at least the lowest), solo or group.</summary>
    private (HuntTarget Target, HuntDefinition Hunt)? PickTarget(HuntData data, int toolTier, bool group)
    {
        if (world.Clusters?.MobsIfLoaded is not { } mobs) return null;
        var pool = new List<(HuntTarget, HuntDefinition, double)>();
        foreach (var t in data.Targets)
        {
            if (t.Special || t.Group != group || mobs.Find(t.MobId) is not { } mob) continue;
            if (mob.Tier > toolTier || mob.Tier < toolTier - 1) continue;
            if (data.HuntFor(t, mob.Tier) is not { } hunt) continue;
            pool.Add((t, hunt, t.Category == "SILVER" ? 3 : 1));
        }
        if (pool.Count == 0) return null;
        var roll = Random.NextDouble() * pool.Sum(x => x.Item3);
        foreach (var (t, h, w) in pool)
            if ((roll -= w) < 0) return (t, h);
        return (pool[^1].Item1, pool[^1].Item2);
    }

    // ---- reading a track ----

    /// <summary>InteractWithTrackStart (499): starts reading <paramref name="trackId"/> (either id of the request); ReturnCode and why.</summary>
    public (short Code, string? Why) Read(Player p, params long[] trackIds)
    {
        if (p.Map is not { } map) return (1, "on no map");
        var track = trackIds.Select(id => map.Objects.GetValueOrDefault(id)).OfType<HuntTrackObject>().FirstOrDefault();
        if (track is null) return (1, "no such track");
        if (track.Used) return Refuse(p, "this track has been read");
        var hunt = Of(p.Character.Id);
        if (track.HuntId != Guid.Empty && hunt?.Id != track.HuntId) return Refuse(p, "this track belongs to another hunt");
        if (track.HuntId == Guid.Empty && Toolkit(p) is null) return Refuse(p, "a tracking toolkit in the main hand reads a track");
        var at = p.PositionAt(DateTime.UtcNow);
        if (Vector2.Distance(at, track.Position) > InteractRange) return Refuse(p, "too far from the track");
        if (_reading.ContainsKey(p) || _finding.ContainsKey(p)) return (1, "busy");
        var seconds = track.Hunt.TrackingTime * (Toolkit(p) is null ? 1 : 1 - ToolTimeReduction);
        var now = world.GameNow;
        var end = now + SpellCasts.Seconds(seconds);
        _reading[p] = new TrackRead(p, track, end, at);
        ToAll(p, new InteractWithTrackStartEvent { ddn = p.Id, ckl = now, ckm = end, ckn = track.Id });
        Log.Info("hunt", string.Create(CultureInfo.InvariantCulture, $"'{p.Character.Name}' reads {track} ({seconds:0.#} s)"));
        return (0, null);
    }

    /// <summary>InteractWithTrackCancel (500), a move, a logout: the reading stops.</summary>
    public void CancelRead(Player p, string why)
    {
        if (!_reading.Remove(p, out var r)) return;
        if (p.Map is not null) ToAll(p, new InteractWithTrackCancelEvent { ddn = p.Id, cki = r.Track.Id, ckj = 0 });
        Log.Info("hunt", $"'{p.Character.Name}' stops reading {r.Track}: {why}");
    }

    private void TrackRead_(TrackRead r)
    {
        var (p, track) = (r.Player, r.Track);
        if (track.Map is not { } map || !map.Objects.ContainsKey(track.Id)) return;
        ToAll(p, new InteractWithTrackFinishedEvent { ddn = p.Id, ckk = track.Id });
        track.Used = true;
        map.SendToObservers(track, new HuntTrackUsedEvent { ddn = track.Id });
        if (track.HuntId == Guid.Empty)
        {
            _wild.Remove(track);
            map.Timers.At(world.Now + TimeSpan.FromSeconds(30), () => map.RemoveObject(track));
            Start(p, track);
        }
        else if (_hunts.FirstOrDefault(h => h.Id == track.HuntId) is { } hunt && hunt.Track == track)
        {
            Tell(hunt, "Hunt: the track is read, the trail goes on.");
            Succeeded(hunt, track.Position);
        }
    }

    // ---- the hunt ----

    /// <summary>The party members of <paramref name="p"/> on its map (itself first); just it without a party.</summary>
    private List<Player> PartyOnMap(Player p)
    {
        var list = new List<Player> { p };
        if (world.Parties.Of(p.Character.Id) is { } party)
            foreach (var m in party.Members)
                if (m.Id != p.Character.Id && world.Parties.Online(m.Id) is { } o && o.Map == p.Map) list.Add(o);
        return list;
    }

    private IEnumerable<Player> Online(Hunt h) => h.Members.Select(world.Parties.Online).OfType<Player>();

    private void Tell(Hunt h, string text)
    {
        foreach (var p in Online(h)) Tell(p, text);
    }

    /// <summary>Starts the hunt of a wild <paramref name="track"/> read by <paramref name="p"/> (with its party on the map); null without data.</summary>
    public Hunt? Start(Player p, HuntTrackObject track)
    {
        if (p.Map is not { } map || Data is null || world.Clusters?.MobsIfLoaded?.Find(track.Target.MobId) is not { } mob) return null;
        var members = PartyOnMap(p);
        foreach (var m in members)
            if (Of(m.Character.Id) is { } old) Abort(old, NewHuntStarted, $"'{m.Character.Name}' starts a new hunt");
        var tool = Toolkit(p);
        var hunt = new Hunt
        {
            Id = Guid.NewGuid(), Definition = track.Hunt, Target = track.Target, Mob = mob, Map = map, Members = [.. members.Select(m => m.Character.Id)],
            FameBonus = tool is null ? 0 : FameBonusOf(tool.Template.Tier), FinalChance = track.Hunt.InitialFinalChance, Location = track.Position,
        };
        hunt.History.Add(track.Position);
        _hunts.Add(hunt);
        var started = new HuntStartedEvent { edc = hunt.Id, edd = hunt.Definition.Index, ede = map.Cluster };
        foreach (var m in members) m.Session.Send(started);
        Log.Info("hunt", $"'{p.Character.Name}' starts {hunt} ({mob.Name}) with {string.Join(", ", members.Select(m => "'" + m.Character.Name + "'"))}");
        Tell(hunt, $"Hunt started: {Name(track.Target)}. Follow the trail.");
        NextQuest(hunt, track.Position);
        return hunt;
    }

    /// <summary>The quest after a success (track read, target fled in an Escape quest): the chance of the final goes up.</summary>
    private void Succeeded(Hunt h, Vector2 from)
    {
        h.QuestCount++;
        h.FinalChance += h.Pick?.ChanceOnSuccess ?? h.Definition.ChanceOnSuccess;
        NextQuest(h, from);
    }

    /// <summary>Picks and sets up the next quest of <paramref name="h"/> from <paramref name="from"/> (SPEC §3).</summary>
    private void NextQuest(Hunt h, Vector2 from)
    {
        var data = Data!;
        ClearQuest(h);
        var def = h.Definition;
        var final = h.QuestCount >= def.MaxQuests || h.QuestCount >= def.MinQuests && Random.NextDouble() < h.FinalChance;
        HuntQuestPick? pick = null;
        if (!final)
        {
            var mains = def.Mains.Where(q => !q.OnlyUndefined && h.QuestCount >= q.QuestCountMin && h.QuestCount <= q.QuestCountMax
                && (q.HpAbove is not { } a || h.HpTarget > a) && (q.HpBelow is not { } b || h.HpTarget < b)
                && !(q.NotTwiceInARow && q.Quest == h.LastQuest) && data.Quest(q.Quest) is not null).ToList();
            pick = Weighted(mains);
        }
        pick ??= Weighted([.. def.Finals.Where(q => data.Quest(q.Quest) is not null)]);
        if (pick is null || data.Quest(pick.Quest) is not { } quest)
        {
            Abort(h, Cheat, "no quest to pick");
            return;
        }
        h.Pick = pick;
        h.Quest = quest;
        h.LastQuest = quest.Name;
        h.EngagedAt = h.EscapeAt = null;
        h.EscapeReason = null;
        var (min, max) = data.SpotDistance.GetValueOrDefault(def.Type, (100, 400));
        var spot = Spot(h, from, min, max);
        h.Location = spot;
        h.History.Add(spot);
        if (h.History.Count > 10) h.History.RemoveAt(0);
        h.QuestEnds = world.Now + TimeSpan.FromSeconds(quest.TimeoutSeconds);
        var map = h.Map;
        // The trail: used footprints at a third and two thirds of the way.
        foreach (var k in new[] { 1f / 3, 2f / 3 })
        {
            var mark = new HuntTrackObject(world.ObjectIds.Next(), map.Cluster, Vector2.Lerp(from, spot, k), Facing(from, spot), h.Target, def, h.Id) { Used = true };
            map.AddObject(mark);
            h.Footprints.Add(mark);
        }
        switch (quest.Kind)
        {
            case HuntQuestKind.Track:
                h.Track = new HuntTrackObject(world.ObjectIds.Next(), map.Cluster, spot, Facing(from, spot), h.Target, def, h.Id);
                map.AddObject(h.Track);
                if (quest.GuardSpawn is { } behavior && world.Clusters?.SpawnBehaviors.Blocks(behavior).FirstOrDefault() is { } block)
                    for (var i = 0; i < quest.GuardCount; i++)
                        if (block.Pick(Random) is { } guard)
                            h.Guards.Add(SpawnManager.SpawnAt(map, guard, spot + Around(4), (float)(Random.NextDouble() * 360), world));
                break;
            default:
                var mob = SpawnManager.SpawnAt(map, h.Mob, spot, Facing(spot, from), world);
                if (h.HpTarget < 1) mob.Health.Set(Math.Max(1, mob.Health.Max * h.HpTarget), world.GameNow);
                h.TargetMob = mob;
                break;
        }
        var what = quest.Kind switch
        {
            HuntQuestKind.Track => "find and read the next track",
            HuntQuestKind.Escape => $"{Name(h.Target)} rests ahead, it will flee when hurt",
            _ => $"{Name(h.Target)} is cornered: kill it",
        };
        // The hint: a minimap ping 20-60 m off the spot (LOCATION_MARKER_MINIMAP offsetmin/max), the way told in the chat.
        var hint = spot + Around((float)(20 + Random.NextDouble() * 40), exact: true);
        foreach (var p in Online(h))
        {
            if (p.Map == map)
            {
                p.Session.Send(new MiniMapPingEvent { crr = hint, crs = 0, crt = "Hunt" });
                Tell(p, $"Hunt ({quest.Kind.ToString().ToLowerInvariant()} {h.QuestCount + 1}): {what}. The trail leads {Hint(p.PositionAt(DateTime.UtcNow), hint)}.");
            }
            else Tell(p, $"Hunt: {what} in {map.Cluster}.");
        }
        Log.Info("hunt", string.Create(CultureInfo.InvariantCulture,
            $"{h}: quest {h.QuestCount + 1} {quest.Name} ({quest.Kind}) at ({spot.X:0.#}, {spot.Y:0.#}), {Vector2.Distance(from, spot):0} m on, final chance {h.FinalChance:0.###}, HP {h.HpTarget:0.##}"));
    }

    private HuntQuestPick? Weighted(List<HuntQuestPick> picks)
    {
        if (picks.Count == 0) return null;
        var roll = Random.NextDouble() * picks.Sum(q => q.Weight);
        foreach (var q in picks)
            if ((roll -= q.Weight) < 0) return q;
        return picks[^1];
    }

    /// <summary>A mob spawn point min..max m from <paramref name="from"/>, at least 50 m from the last spots (else any in the ring).</summary>
    private Vector2 Spot(Hunt h, Vector2 from, double min, double max)
    {
        var points = h.Map.MobSlots.Select(s => s.Point.Position).Distinct()
            .Where(p => Vector2.Distance(p, from) is var d && d >= min && d <= max).ToList();
        var fresh = points.Where(p => h.History.All(o => Vector2.Distance(o, p) >= 50)).ToList();
        if (fresh.Count > 0) return fresh[Random.Next(fresh.Count)];
        return WorldEvents.PointNear(h.Map, from, (min, max));
    }

    private Vector2 Around(float r, bool exact = false)
    {
        var a = Random.NextDouble() * 2 * Math.PI;
        var d = exact ? r : r * (float)Random.NextDouble();
        return new Vector2((float)Math.Cos(a) * d, (float)Math.Sin(a) * d);
    }

    private static float Facing(Vector2 from, Vector2 to) => (float)(Math.Atan2(to.X - from.X, to.Y - from.Y) * 180 / Math.PI + 360) % 360;

    /// <summary>"~120 m north-east" from <paramref name="from"/> to <paramref name="to"/> (z is north).</summary>
    public static string Hint(Vector2 from, Vector2 to)
    {
        var d = to - from;
        var dist = d.Length();
        if (dist < 15) return "right here";
        string[] names = ["north", "north-east", "east", "south-east", "south", "south-west", "west", "north-west"];
        var angle = (Math.Atan2(d.X, d.Y) * 180 / Math.PI + 360 + 22.5) % 360;
        return string.Create(CultureInfo.InvariantCulture, $"~{Math.Round(dist / 10) * 10:0} m {names[(int)(angle / 45) % 8]}");
    }

    private static string Name(HuntTarget t) => t.MobId.Replace("_MOB_TR_", " ", StringComparison.Ordinal).Replace('_', ' ').ToLowerInvariant();

    /// <summary>Takes the quest's objects off the map: footprints, the track, guards still standing; a living target that is not fighting.</summary>
    private void ClearQuest(Hunt h)
    {
        foreach (var f in h.Footprints) h.Map.RemoveObject(f);
        h.Footprints.Clear();
        if (h.Track is { } t) h.Map.RemoveObject(t);
        h.Track = null;
        foreach (var g in h.Guards.Where(g => !g.Dead && !g.Removed && g.Threat.Count == 0)) SpawnManager.Despawn(g, world, respawn: false);
        h.Guards.Clear();
        if (h.TargetMob is { Dead: false, Removed: false } m) SpawnManager.Despawn(m, world, respawn: false);
        h.TargetMob = null;
    }

    /// <summary>Ends <paramref name="h"/> without a kill: HuntAborted {0 hunt, 1 HuntAbortReason} to its members online.</summary>
    public void Abort(Hunt h, byte reason, string why)
    {
        if (!_hunts.Remove(h)) return;
        ClearQuest(h);
        var e = new HuntAbortedEvent { ecq = h.Id, ecr = reason };
        foreach (var p in Online(h))
        {
            p.Session.Send(e);
            Tell(p, $"Hunt aborted: {why}.");
        }
        Log.Info("hunt", $"{h} aborted ({reason}): {why}");
    }

    /// <summary>HuntAbort (497) of a member.</summary>
    public bool AbortBy(Player p, Guid id)
    {
        if (Of(p.Character.Id) is not { } h || id != Guid.Empty && h.Id != id) return false;
        Abort(h, PlayerRequest, $"'{p.Character.Name}' gives up");
        return true;
    }

    /// <summary><see cref="Combat.Kill"/>: a hunt's target died. The hunt is won: the final quest's fame for every member online, HuntFinished.</summary>
    public void Killed(Mob mob, Player killer)
    {
        if (_hunts.FirstOrDefault(h => h.TargetMob == mob) is not { } h) return;
        _hunts.Remove(h);
        h.TargetMob = null;
        ClearQuest(h);
        var quest = h.Quest?.Kind == HuntQuestKind.Final ? h.Quest
            : h.Definition.Finals.Select(f => Data?.Quest(f.Quest)).FirstOrDefault(q => q is not null) ?? h.Quest;
        var points = quest?.Fame ?? 0;
        var gain = Rates.ScaleFame((long)Math.Round(points * 10000 * (1 + h.FameBonus)), world.Rates.Fame);
        var e = new HuntFinishedEvent { ecs = h.Id };
        foreach (var p in Online(h))
        {
            if (gain > 0) Destiny.Award(p, gain, Destiny.Kill(p, h.Mob.Tier), journals: false);
            p.Session.Send(e);
            Tell(p, string.Create(CultureInfo.InvariantCulture, $"Hunt complete: {Name(h.Target)} slain by '{killer.Character.Name}' (+{gain / 10000.0:0.#} fame)."));
        }
        world.Vanity.HuntWon(Online(h), Name(h.Target)); // stageN vanity: the final kill may drop a cosmetic for each member
        Log.Info("hunt", string.Create(CultureInfo.InvariantCulture, $"{h} won by '{killer.Character.Name}' after {h.QuestCount + 1} quests: {gain / 10000.0:0.#} fame each"));
    }

    /// <summary>Starts the target's flight (its EscapeSpellSequence, <see cref="BehaviourAI.Escape"/>; without a BehaviourAI it just leaves).</summary>
    public void Escape(Hunt h, string why)
    {
        if (h.TargetMob is not { Dead: false, Removed: false, Map: { } map } mob || h.EscapeAt is not null) return;
        var now = world.GameNow;
        h.EscapeAt = now;
        h.EscapeReason = why;
        h.FleeHp = Math.Clamp(mob.Health.At(now) / Math.Max(1, mob.Health.Max), 0.01f, 1);
        Tell(h, $"Hunt: {Name(h.Target)} flees ({why})!");
        if (mob.AI is BehaviourAI b) b.Escape(world, map, "hunt: " + why);
        else SpawnManager.Despawn(mob, world, respawn: false);
    }

    /// <summary>The world tick: find casts and track readings that end, hunts' timeouts, escape triggers and fled targets, old wild tracks.</summary>
    public void Update()
    {
        var now = world.GameNow;
        foreach (var f in _finding.Values.ToList())
        {
            var p = f.Player;
            if (p.Map is null || Vector2.Distance(p.PositionAt(DateTime.UtcNow), f.At) > 0.75f)
            {
                _finding.Remove(p);
                if (p.Map is not null) p.SendToObservers(new CastCancelEvent { ddn = p.Id, buo = true, bup = 0 });
                Log.Info("hunt", $"'{p.Character.Name}' stops looking for tracks: {(p.Map is null ? "left the map" : "moved")}");
                continue;
            }
            if (now < f.End) continue;
            _finding.Remove(p);
            Found(f);
        }
        foreach (var r in _reading.Values.ToList())
        {
            if (r.Player.Map is null || Vector2.Distance(r.Player.PositionAt(DateTime.UtcNow), r.At) > 0.75f) { CancelRead(r.Player, "moved"); continue; }
            if (now < r.End) continue;
            _reading.Remove(r.Player);
            TrackRead_(r);
        }
        foreach (var t in _wild.Where(t => t.Expires <= world.Now || t.Map is null || t.Map.Unloaded).ToList())
        {
            _wild.Remove(t);
            t.Map?.RemoveObject(t);
        }
        foreach (var h in _hunts.ToList()) Update(h, now);
    }

    private void Update(Hunt h, long now)
    {
        if (h.Map.Unloaded) { Abort(h, NoPlayersInCluster, "its map is gone"); return; }
        // No member in game (a cluster change in between counts as one): aborted after OfflineGrace.
        if (Online(h).Any()) h.AloneSince = null;
        else if ((h.AloneSince ??= world.Now) + OfflineGrace <= world.Now) { Abort(h, PlayerWentOffline, "no hunter online"); return; }
        if (world.Now >= h.QuestEnds) { Abort(h, QuestTimedOut, "the trail went cold (quest timeout)"); return; }
        if (h.Quest is not { Kind: not HuntQuestKind.Track } q || h.TargetMob is not { } mob || mob.Dead) return;
        if (mob.Removed)
        {
            // Fled (or despawned): the trail goes on from where it went; its health carries over.
            h.TargetMob = null;
            h.HpTarget = h.EscapeAt is null ? Math.Clamp(mob.Health.At(now) / Math.Max(1, mob.Health.Max), 0.01f, 1) : h.FleeHp;
            if (q.Kind == HuntQuestKind.Final)
            {
                h.FinalChance += h.Pick?.ChanceOnFailure ?? h.Definition.ChanceOnFailure;
                Tell(h, $"Hunt: {Name(h.Target)} escaped, pick up its trail again.");
                NextQuest(h, mob.Position);
            }
            else Succeeded(h, mob.Position);
            return;
        }
        if (h.EscapeAt is { } since)
        {
            // Without a running AI (MobAi off) the flight never ends by itself: it leaves after 15 s.
            if (now - since > SpellCasts.Seconds(15)) SpawnManager.Despawn(mob, world, respawn: false);
            return;
        }
        var hp = mob.Health.At(now) / Math.Max(1, mob.Health.Max);
        if (mob.Threat.Count > 0 || hp < h.HpTarget - 0.001f) h.EngagedAt ??= now; // attacked (a hit without threat: MobAi off)
        var near = h.Map.Players.Values.Count(p => Vector2.Distance(p.PositionAt(DateTime.UtcNow), mob.Position) <= ProximityRange);
        var engaged = h.EngagedAt is { } e ? (now - e) / (double)TimeSpan.TicksPerSecond : -1;
        var why = q.EscapeAtHealth > 0 && hp <= q.EscapeAtHealth ? string.Create(CultureInfo.InvariantCulture, $"health {hp:P0}")
            : q.EscapeOnThreatCount > 0 && mob.Threat.Count >= q.EscapeOnThreatCount ? $"{mob.Threat.Count} attackers"
            : q.EscapeOnPlayersInProximity > 0 && h.EngagedAt is not null && near >= q.EscapeOnPlayersInProximity ? $"{near} players nearby"
            : q.EscapeAfterTimeInCombat > 0 && engaged >= q.EscapeAfterTimeInCombat ? "too long a fight"
            : q.EscapeAfterFirstEngage > 0 && engaged >= q.EscapeAfterFirstEngage ? "the first engage timer"
            : null;
        if (why is not null) Escape(h, why);
    }

    /// <summary>For .hunt: one line per hunt.</summary>
    public IEnumerable<string> Describe() => _hunts.Select(h => string.Create(CultureInfo.InvariantCulture,
        $"{h}: quest {h.QuestCount + 1} {h.Quest?.Name} at ({h.Location.X:0}, {h.Location.Y:0}) in {h.Map.Cluster}, final chance {h.FinalChance:0.###}, HP {h.HpTarget:0.##}, {(h.QuestEnds - world.Now).TotalSeconds:0} s left, members {h.Members.Count}"));
}
