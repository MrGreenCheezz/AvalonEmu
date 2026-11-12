using System.Globalization;
using System.Numerics;
using System.Xml.Linq;
using AlbionLocal.Photon;
using AlbionLocal.Server.Content;
using AlbionLocal.Server.Data;
using AlbionLocal.Server.Entities;
using AlbionLocal.Server.Game;
using AlbionLocal.Server.Network;
using AlbionLocal.Server.Protocol;

namespace AlbionLocal.Server.Systems;

/// <summary>
/// A built mists zone, not yet registered (<see cref="Mists.Build"/>, off the world thread): its Guid, mode, map, rarity, the instance's mob
/// level, the faction, the a9i layout with its bytes and the cluster resolved from it.
/// </summary>
public sealed record MistsBuild(Guid Guid, MistsMode Mode, MistsMap Map, byte Level, byte MobLevel, string Faction, MistsLayout Layout, byte[] Bytes, ClusterTemplate Template)
{
    /// <summary>"@MISTS@", "@MISTSDUNGEON@" (Knightfall Abbey) or "@RESOURCEMISTS@" + the Guid, by the mode's type.</summary>
    public string Id => MistsIds.InstanceId(Guid, Mode.Type);
}

/// <summary>
/// One mists zone (analysis/work/stageL/mists/SPEC.md, "SPEC" here, §6.2): "@MISTS@&lt;guid&gt;", its mode, map, rarity and mob level, the layout
/// and cluster, the team it is private to ("" when shared), its times (closes for newcomers, shuts down), who was sent in, the return portals
/// of the arrivals, the planned treasures and the mist bosses.
/// </summary>
public sealed class MistsZone(MistsBuild build)
{
    public MistsBuild Build { get; } = build;
    public string Id => Build.Id;
    public MistsMode Mode => Build.Mode;
    public MistsMap Map => Build.Map;
    public byte Level => Build.Level;
    public int Tier => Build.Map.Tier;
    public ClusterTemplate Template => Build.Template;
    public byte[] Bytes => Build.Bytes;

    /// <summary>Mists:Matchmaking Private: the portal or border and the party or player it is for; "" in a shared zone.</summary>
    public required string TeamKey { get; init; }

    /// <summary>The level reroll its next zones draw from (the open-world portal's MISTS_OPEN_WORLD_MODE_*); null = rarity 0.</summary>
    public LevelReroll? Reroll { get; init; }

    /// <summary>The world cluster of the open-world portal its first zone was entered from (kept over the borders); null when unknown.</summary>
    public string? Entrance { get; init; }

    /// <summary>The players its build was waited for by (the team coming in together; at least 1).</summary>
    public int Expected { get; init; } = 1;

    /// <summary>The first arrival (Private: the teammates who come in later arrive there too).</summary>
    public Arrival? FirstArrival { get; set; }

    public required TimeSpan Created { get; init; }

    /// <summary>World clock times: no new team after <see cref="CloseAt"/> (Shared), everyone out at <see cref="ShutdownAt"/> (its GameTimeStamp <see cref="ShutdownTicks"/>, 521 key 5).</summary>
    public required TimeSpan CloseAt { get; init; }
    public required TimeSpan ShutdownAt { get; init; }
    public required long ShutdownTicks { get; init; }

    /// <summary>The characters sent in so far, and those not joined yet with the clock time until which each counts as inside.</summary>
    public HashSet<Guid> Entered { get; } = [];
    public Dictionary<Guid, TimeSpan> Arriving { get; } = [];

    /// <summary>The characters who got the zone's chat line (once each).</summary>
    public HashSet<Guid> Told { get; } = [];

    public TimeSpan? EmptySince { get; set; }
    public bool Closed { get; set; }

    /// <summary>The shutdown warnings that went out (clustershutdowncountdowntimer 300 s, clustershutdownalerttimer 60 s).</summary>
    public bool WarnedCountdown { get; set; }
    public bool WarnedAlert { get; set; }

    /// <summary>A duo zone entered by one player alone: its mobs got Mists:DuoSoloScaling (until a second player comes in).</summary>
    public bool Scaled { get; set; }

    /// <summary>Each arrival's return portal (520), by character.</summary>
    public Dictionary<Guid, MistsReturnPortal> Returns { get; } = [];

    /// <summary>The treasures still to come (unlocked at their time) and the mist bosses standing (their object ids).</summary>
    public List<MistsTreasureSpot> Treasures { get; } = [];
    public HashSet<long> Bosses { get; } = [];

    /// <summary>Mists:OpenWorldExit Random: the world cluster its open-world exit leads to (drawn at its fill); null = the viewer's way back.</summary>
    public string? OpenWorldTarget { get; set; }

    /// <summary>Phase B: the clock time of the next Knightfall Abbey portal wave (an open mode's MistsDungeonEntrance); null = none.</summary>
    public TimeSpan? NextWave { get; set; }

    /// <summary>Phase B: the clock times the end-of-life portal wisps come (wispsspawninsecbeforeclustershutdown, staggered), the next first.</summary>
    public List<TimeSpan> WispTimes { get; } = [];

    /// <summary>Phase B: the portal wisps standing, by mob object id.</summary>
    public Dictionary<long, MistsWisp> Wisps { get; } = [];

    /// <summary>
    /// Phase B, Knightfall Abbey: by the room's index, the shrine or the reward chest (its spot) each MOBS room drew, both given when the room is
    /// cleared, and the rooms cleared already.
    /// </summary>
    public Dictionary<int, MistsRoomReward> Shrines { get; } = [];
    public Dictionary<int, LootChestSpawn> RewardChests { get; } = [];
    public HashSet<int> Cleared { get; } = [];

    public override string ToString() => $"mists zone {Mode.Name} {Map.Id} rarity {Level} {Id}";
}

/// <summary>A planned treasure of a zone (SPEC §6.4): its subtype, the chest spawnpoint or mob it is, where, and the clock time it unlocks.</summary>
public sealed record MistsTreasureSpot(string Subtype, string Name, Vector2 At, TimeSpan Due);

/// <summary>
/// Phase B (SPEC §8 items 2, 3): a portal wisp standing in a zone: the mode and rarity of the portal its death leaves, that portal's
/// RandomExitVisualization, the wisp spawn it stands at (null = a camp's spot) and the clock time it noticed a player (its fight began).
/// </summary>
public sealed record MistsWisp(MistsMode Target, byte Level, string? Visualization, MistsWispSpawn? Spot)
{
    public TimeSpan? AggroAt { get; set; }
}

/// <summary>
/// The Mists, phase A (analysis/work/stageL/mists/SPEC.md §7): the open world's portals' enter cast (ENTER_MISTS in slot 25, a server cast of 3 s:
/// CastStart 14, CastFinished 18, CastSpell 19; a cancel or a knockdown ends it, damage does not), the zones behind them (one per team under
/// Mists:Matchmaking Private, shared up to the map's soft limit under Shared), built off the world thread from the a9i layout of the open mists
/// generator, the ChangeCluster push {0 id, 3 layout, 4 clustertype, 5 tier}, the zone's objects (border exits 537, the Brecilien entrance 527,
/// the open-world exit 523, each arrival's return portal 520) and MistsPlayerJoinedInfo (521) after each Join, the camps (the open world's camp
/// code), the treasures and the mist bosses, the ways out (Handlers/World/MistsHandler: borders, Brecilien, the open world, op 476), the
/// shutdown warnings and push out, the empty zones' close, the relog and the respawn. The mists world clusters around Brecilien (5100-5103) get
/// their city entrances. Phase B (SPEC §8) is in the partial files Mists.Abbey.cs (Knightfall Abbey, the wisps, the resource zones, the camps'
/// free-for-all circles) and Mists.Brecilien.cs (the static entrance, the standing and the City Trader, the free-for-all rule).
/// <see cref="World.Update"/> runs <see cref="Update"/>. Under <see cref="World.Sync"/>, except the builds.
/// </summary>
public sealed partial class Mists(World world)
{
    /// <summary>CharacterSpellSlot.FakeSpell (25), and the system spell the client casts there at a mists portal (cyi.u 0x180A00CB0; useobject mistsentrance).</summary>
    public const byte EnterSlot = FakeSlotCasts.Slot;
    public const string EnterSpell = "ENTER_MISTS";

    /// <summary>ENTER_MISTS without spell data (spells.xml: castingtime 3, castrange 5).</summary>
    public const double DefaultCastSeconds = 3, DefaultCastRange = 5;

    /// <summary>The generator name a mists portal carries (no random dungeon beneath it).</summary>
    public const string Generator = "MISTS";

    /// <summary>mistsdifficultyleveluimapping: the rarity names for the chat lines.</summary>
    public static readonly string[] RarityNames = ["Mundane", "Standard", "Uncommon", "Rare", "Legendary"];

    /// <summary>
    /// An enter cast under way: the portal, its mode, the spell and counter, its stamps and the clock time it is due, the build (shared,
    /// <see cref="Pending"/>) or the zone it joins. A <see cref="FakeSlotCast"/> (one per player with the corrupted entrance and the chest
    /// opening, <see cref="FakeSlotCasts"/>): damage leaves it on, its end waits for the zone (<see cref="ZoneFor"/>, at most 30 s more).
    /// </summary>
    private sealed record EnterCast(Map Map, Vector2 Aim, RandomDungeonExit Exit, MistsMode Mode, int SpellId, byte Counter, long Start, long End, TimeSpan Due,
        Task<MistsBuild>? Build, MistsZone? Join, string TeamKey, Mists Owner)
        : FakeSlotCast(Map, Aim, SpellId, Counter, Start, End, Due)
    {
        public string Place => $"portal:{Exit.Id}";

        /// <summary>A zone closed during the cast hands over to another zone or a new build (<see cref="ZoneFor"/>).</summary>
        public Task<MistsBuild>? Build { get; set; } = Build;
        public MistsZone? Join { get; set; } = Join;
        public string TeamKey { get; set; } = TeamKey;

        /// <summary>The zone <see cref="Ready"/> found, or why it gives up and the chat line's reason.</summary>
        private MistsZone? _zone;
        private (string Why, string Tell)? _failed;

        public override string What => "mists enter cast";

        /// <summary>CastSpell (19) names the portal.</summary>
        public override Vector2 HitAt => Exit.Position;

        /// <summary>ENTER_MISTS: damagecancelscasting false.</summary>
        public override bool DamageCancels => false;

        public override void Dropped(Player p)
        {
            if (Build is { } build) Owner.Abandon(p, build);
        }

        public override bool Ready(Player p) => (_zone = Owner.ZoneFor(p, this, out _failed)) is not null;

        public override string? GivesUp(Player p, TimeSpan now)
        {
            var failed = _failed;
            if (failed is null && now >= Due + TimeSpan.FromSeconds(30)) failed = ("the zone is not built in time", "the zone is not ready; try again");
            if (failed is not { } f) return null;
            Expeditions.Tell(p, Owner.Settings.Text("Refused", ("why", f.Tell)));
            return f.Why;
        }

        public override void Complete(Player p) => Owner.Finish(p, this, _zone!);
    }

    /// <summary>A zone being built for a pending key (<see cref="PendingKey"/>): the build, the characters waiting for it, since when.</summary>
    private sealed class PendingZone(Task<MistsBuild> build, TimeSpan since)
    {
        public Task<MistsBuild> Build { get; } = build;
        public TimeSpan Since { get; } = since;
        public HashSet<Guid> Members { get; } = [];
    }

    private readonly Dictionary<string, MistsZone> _zones = new(StringComparer.Ordinal);
    private readonly Dictionary<string, PendingZone> _pending = new(StringComparer.Ordinal);
    private readonly List<Task> _builds = [];

    private MistsOptions Options => world.Config.Mists;

    private MistsSettings Settings => world.MistsSettings;

    /// <summary>mists.xml when it has been read (the first cluster with exit markers or a mists zone); null before.</summary>
    public MistsData? Data => world.Clusters?.MistsIfLoaded;

    private Random Rng => _rng ??= world.MapSeed is { } seed ? new Random(seed) : new Random();
    private Random? _rng;

    public IReadOnlyCollection<MistsZone> Zones => _zones.Values;

    /// <summary>The live zone of id <paramref name="cluster"/>; null for any other cluster or a closed one.</summary>
    public MistsZone? Find(string? cluster) => cluster is not null && _zones.TryGetValue(cluster, out var z) && !z.Closed ? z : null;

    public bool IsLive(string cluster) => _zones.ContainsKey(cluster);

    /// <summary>Whether the open world's mists portals stand (Mists:Enabled, OpenWorldWeight above 0) for the mode <paramref name="mode"/> mists.xml has.</summary>
    public bool PortalOf(string mode) => Options is { Enabled: true, OpenWorldWeight: > 0 } && Data?.Modes.ContainsKey(mode) == true;

    /// <summary>Mists:OpenWorldWeight: the factor on a mists portal's distribution weight.</summary>
    public double WeightFactor => Options.OpenWorldWeight;

    /// <summary>Whether <paramref name="p"/> has an enter cast under way.</summary>
    public bool Entering(Player p) => world.FakeCasts.Of(p) is EnterCast;

    /// <summary>The builds under way (the self-test waits for them).</summary>
    public Task Building()
    {
        _builds.RemoveAll(t => t.IsCompleted);
        return Task.WhenAll(_builds);
    }

    // ---- the rarity ----

    /// <summary>
    /// A portal's or a next zone's rarity 0-4: by Mists:RarityWeights when set (pve), else by <paramref name="reroll"/> (the distribution's
    /// MISTS_OPEN_WORLD_MODE_*); 0 without either.
    /// </summary>
    public byte RollRarity(LevelReroll? reroll, Random rng)
    {
        if (Options.RarityWeights is { Count: 5 } w) return DungeonGenerator.Pick(rng, [.. w.Select((x, i) => ((byte)i, x))]);
        return reroll is { Levels.Count: > 0 } r ? DungeonGenerator.Pick(rng, [.. r.Levels.Select(l => (l.Level, l.Weight))]) : (byte)0;
    }

    // ---- the build (SPEC §6.2, §6.3) ----

    /// <summary>
    /// A new zone of <paramref name="mode"/> at rarity <paramref name="level"/>, built on a pool thread: the map, faction, seed and mob level drawn
    /// now, then the layout (<see cref="MistsGenerator.Zone"/>) and the cluster from it (<see cref="ClusterStore.FromLayout"/>). Null when the
    /// data cannot make one.
    /// </summary>
    public Task<MistsBuild>? Build(MistsMode mode, int level, int? tier = null)
    {
        if (world.Clusters is not { } store || Data is not { } data || MistsGenerator.PickMap(data, mode, level, Rng, tier) is not { } map) return null;
        var faction = MistsGenerator.PickFaction(data, Rng);
        var seed = Rng.Next();
        var mobLevel = mode.Level(level)?.InstanceRoll is { } roll && store.DungeonsIfLoaded?.Rerolls.GetValueOrDefault(roll) is { Levels.Count: > 0 } r
            ? DungeonGenerator.Pick(Rng, [.. r.Levels.Select(l => (l.Level, l.Weight))]) : (byte)0;
        var guid = Guid.NewGuid();
        var task = Task.Run(() =>
        {
            // Phase B: Knightfall Abbey from a7c.b's dungeon generator, every other mode (the resource zones too) from the open one.
            var id = MistsIds.InstanceId(guid, mode.Type);
            var layout = mode.IsDungeon ? MistsGenerator.Dungeon(store, data, mode, map, faction, id, new Random(seed))
                : MistsGenerator.Zone(store, data, mode, map, level, faction, id, new Random(seed));
            var template = store.FromLayout(layout);
            if (template.Problems.Count > 0) Log.Warn("data", $"mists zone {layout.Id}: {template.Problems[0]}");
            return new MistsBuild(guid, mode, map, (byte)Math.Clamp(level, 0, 4), mobLevel, faction, layout, layout.Write(), template);
        });
        _builds.Add(task);
        return task;
    }

    /// <summary>
    /// The zone of a finished build, registered: its times from the map × Mists:LifetimeFactor, the world cluster it was entered from
    /// (<paramref name="entrance"/>) and the players coming in with it (<paramref name="expected"/>).
    /// </summary>
    public MistsZone Register(MistsBuild build, string teamKey, LevelReroll? reroll, string? entrance = null, int expected = 1)
    {
        var now = world.Clock.Now;
        var shutdown = TimeSpan.FromSeconds(build.Map.ShutdownSeconds * Options.LifetimeFactor);
        var zone = new MistsZone(build)
        {
            TeamKey = teamKey, Reroll = reroll, Entrance = entrance, Expected = Math.Max(1, expected), Created = now,
            CloseAt = now + TimeSpan.FromSeconds(build.Map.CloseSeconds * Options.LifetimeFactor), ShutdownAt = now + shutdown, ShutdownTicks = world.GameNow + shutdown.Ticks,
        };
        _zones[zone.Id] = zone;
        Log.Info("world", string.Create(CultureInfo.InvariantCulture,
            $"{zone}: {build.Layout.Templates.Count} templates ({string.Join(", ", build.Layout.Templates.Skip(1).Select(t => t.Template))}), {build.Bytes.Length} layout bytes, "
            + $"T{zone.Tier} {build.Faction}, mob level {build.MobLevel}, shuts down in {shutdown.TotalMinutes:0} min{(teamKey.Length > 0 ? $" for {teamKey}" : ", shared")}; {build.Template.Summary()}"));
        return zone;
    }

    /// <summary>
    /// The team key of <paramref name="p"/> at <paramref name="place"/> (a portal or a border) under Private (SPEC §6.2, V8): a solo mode's
    /// player alone; a duo mode's party (its first <see cref="MistsMode.MaxTeamSize"/> members there share a zone), else the player.
    /// </summary>
    private string TeamKey(Player p, MistsMode mode, string place) =>
        mode.Duo && world.Parties.Of(p.Character.Id) is { } party ? $"{place}:party:{party.Id.ToString(CultureInfo.InvariantCulture)}" : $"{place}:player:{p.Character.Id}";

    /// <summary>The team key of a new zone <paramref name="p"/> makes at <paramref name="place"/>: "" under Shared, its team's under Private.</summary>
    public string TeamKeyOf(Player p, MistsMode mode, string place) => Options.Matchmaking == MistsMatchmaking.Shared ? "" : TeamKey(p, mode, place);

    /// <summary>
    /// The zone a border at <paramref name="place"/> of <paramref name="from"/> leads <paramref name="p"/> into when one is open (its team's, or a
    /// shared one of the mode and tier; never <paramref name="from"/> itself); null = a new one, for <paramref name="teamKey"/>.
    /// </summary>
    public MistsZone? NextZone(Player p, MistsZone from, string place, out string teamKey) =>
        LeadsTo(from) is var mode && mode == from.Mode ? Joinable(p, mode, null, from.Tier, place, out teamKey, except: from) : Joinable(p, mode, null, null, place, out teamKey, except: from);

    /// <summary>A new zone of <paramref name="from"/>'s next mode (<see cref="LeadsTo"/>) behind a border, of a rarity rolled anew (Mists:RarityWeights or its portal's reroll).</summary>
    public Task<MistsBuild>? BuildNext(MistsZone from) => Build(LeadsTo(from), RollRarity(from.Reroll, Rng));

    /// <summary>Phase B: the mode <paramref name="from"/>'s ways on lead into: its exitsleadtomode (Knightfall Abbey, a resource zone), else its own.</summary>
    public MistsMode LeadsTo(MistsZone from) => from.Mode.ExitsLeadTo is { } m && Data?.Modes.GetValueOrDefault(m) is { } mode ? mode : from.Mode;

    /// <summary>
    /// The open zone <paramref name="p"/> joins at <paramref name="place"/>: under Private its team's with room, the teammates still on their way
    /// counted (a pending build's, an enter cast's; a full team: Mists.TeamFull and a zone of its own, <paramref name="teamKey"/> the player's);
    /// under Shared one of the mode (and <paramref name="level"/>, <paramref name="tier"/> when given) with room and still open. Never
    /// <paramref name="except"/> (the zone a border is in). Null = a new one, or the build under way for the key (<see cref="Pending"/>).
    /// </summary>
    private MistsZone? Joinable(Player p, MistsMode mode, int? level, int? tier, string place, out string teamKey, MistsZone? except = null)
    {
        var now = world.Clock.Now;
        if (Options.Matchmaking == MistsMatchmaking.Shared)
        {
            teamKey = "";
            return _zones.Values.FirstOrDefault(z => z != except && !z.Closed && z.TeamKey.Length == 0 && z.Mode == mode && (level is null || z.Level == level)
                && (tier is null || z.Tier == tier) && now < z.CloseAt && Inside(z) < z.Map.SoftMax);
        }
        teamKey = TeamKey(p, mode, place);
        var key = teamKey;
        var id = p.Character.Id;
        var team = _zones.Values.FirstOrDefault(z => z != except && !z.Closed && z.TeamKey == key);
        var coming = new HashSet<Guid>(team?.Entered ?? []);
        coming.UnionWith(_pending.GetValueOrDefault(key)?.Members ?? []);
        if (team is not null) coming.UnionWith(world.FakeCasts.All.Where(e => e.Value is EnterCast c && c.Join == team).Select(e => e.Key.Character.Id));
        if (coming.Contains(id) || coming.Count < mode.MaxTeamSize) return team;
        Expeditions.Tell(p, Settings.Text("TeamFull", ("max", mode.MaxTeamSize)));
        teamKey = $"{place}:player:{id}";
        return null;
    }

    /// <summary>The key a build is shared by: the team key under Private, the portal or border under Shared.</summary>
    private static string PendingKey(string teamKey, string place) => teamKey.Length > 0 ? teamKey : "shared:" + place;

    /// <summary>
    /// The build <paramref name="p"/> waits for at <paramref name="place"/> when no zone is open: the one under way for the same key (its team's
    /// under Private, the same portal or border under Shared: players who go in together share one zone), else a new one by <paramref name="build"/>.
    /// Null when none can be made.
    /// </summary>
    public Task<MistsBuild>? Pending(Player p, string teamKey, string place, Func<Task<MistsBuild>?> build)
    {
        var key = PendingKey(teamKey, place);
        if (!_pending.TryGetValue(key, out var pending) || pending.Build.IsFaulted || pending.Build.IsCanceled)
        {
            if (build() is not { } task) return null;
            _pending[key] = pending = new PendingZone(task, world.Now);
        }
        pending.Members.Add(p.Character.Id);
        return pending.Build;
    }

    /// <summary><paramref name="p"/> waits no more for <paramref name="build"/> (it went in, gave up or left); a build nobody waits for is dropped.</summary>
    public void Abandon(Player p, Task<MistsBuild> build)
    {
        foreach (var (key, pending) in _pending.Where(e => e.Value.Build == build).ToList())
            if (pending.Members.Remove(p.Character.Id) && pending.Members.Count == 0) _pending.Remove(key);
    }

    /// <summary>
    /// The zone <paramref name="p"/> goes into once its pending <paramref name="build"/> is done: the zone already registered from it (a teammate
    /// was first), else under Private an open zone of its team with room (this build dropped), else the build registered with the players who
    /// waited for it. Null when the zone made from it is closed already.
    /// </summary>
    public MistsZone? Resolve(Player p, Task<MistsBuild> build, string teamKey, LevelReroll? reroll, string? entrance)
    {
        var made = build.Result;
        var waiting = _pending.Values.FirstOrDefault(x => x.Build == build)?.Members.Count ?? 1;
        Abandon(p, build);
        if (_zones.TryGetValue(made.Id, out var zone)) return zone.Closed ? null : zone;
        var id = p.Character.Id;
        if (teamKey.Length > 0 && _zones.Values.FirstOrDefault(z => !z.Closed && z.TeamKey == teamKey && (z.Entered.Contains(id) || z.Entered.Count < z.Mode.MaxTeamSize)) is { } team)
            return team;
        return Register(made, teamKey, reroll, entrance, waiting);
    }

    /// <summary>The players on the zone's map now and those on the way in.</summary>
    private int Inside(MistsZone z) => (world.Maps.Find(z.Id)?.Players.Count ?? 0) + z.Arriving.Count;

    // ---- the enter cast (SPEC §3.5, §6.5) ----

    /// <summary>
    /// CastStart on slot 25 with ENTER_MISTS (<paramref name="spellId"/>) aimed at <paramref name="aim"/>: a mists portal of the map within castrange +
    /// Transfer:PositionTolerance of the aim, the aim as near the player; not knocked down, no other server cast, Mists:Enabled; then a server
    /// cast of castingtime (3 s) through <see cref="FakeSlotCasts"/>: CastStart (14) to the observers (the client casts its own), and the zone's
    /// build starts at once. Returns why it was refused, or null.
    /// </summary>
    public string? StartEnter(Player p, int spellId, Vector2 aim, byte counter, long start)
    {
        if (!Options.Enabled) return "the mists are closed here";
        if (Data is not { } data) return "mists.xml is not read";
        if (p.Map is not { Template: not null } map) return "not on a map";
        if (p.Combat.Down is { } down) return $"knocked down ({down.Kind})";
        if (p.PvpState.Execute is not null || p.PvpState.FlagCast is not null || p.Combat.Cast is not null || world.Corrupted.Entering(p)) return "a cast is under way";
        var spell = world.Spells?.Template(spellId);
        var range = (spell?.CastRange is > 0 and var r ? r : DefaultCastRange) + world.Config.Transfer.PositionTolerance;
        var at = p.PositionAt(DateTime.UtcNow);
        var exit = map.Objects.Values.OfType<RandomDungeonExit>().Where(e => e.Info.MistsMode is not null).MinBy(e => Vector2.Distance(e.Position, aim));
        if (exit is null || Vector2.Distance(exit.Position, aim) > range || Vector2.Distance(at, aim) > range) return "not at a mists portal";
        if (!data.Modes.TryGetValue(exit.Info.MistsMode!, out var mode)) return $"mode {exit.Info.MistsMode} is not in mists.xml";
        world.FakeCasts.Interrupt(p, "a new mists enter cast", interrupted: false); // before the team count and the build (an old cast's build let go)
        var place = $"portal:{exit.Id}";
        var join = Joinable(p, mode, exit.Level, mode.IsDungeon ? exit.Tier : null, place, out var teamKey); // phase B: a dungeon of the portal's tier
        var build = join is null ? Pending(p, teamKey, place, () => Build(mode, exit.Level, exit.Tier)) : null;
        if (join is null && build is null) return $"no map of {mode.Mapset} for rarity {exit.Level}";
        var seconds = spell?.CastingTime is > 0 and var ct ? ct : DefaultCastSeconds;
        var cast = new EnterCast(map, aim, exit, mode, spellId, counter, start, start + SpellCasts.Seconds(seconds), world.Now + TimeSpan.FromSeconds(seconds), build, join, teamKey, this);
        world.FakeCasts.Start(p, cast); // stands up, dismounts, CastStart (14) to the observers
        Log.Info("world", $"'{p.Character.Name}' casts the mists entrance at {exit} ({mode.Name}, rarity {exit.Level}{(join is null ? ", a new zone" : $", into {join.Id}")})");
        return null;
    }

    /// <summary>
    /// At the enter cast's due time (<see cref="EnterCast.Ready"/>): the zone it joins (closed meanwhile: another open one of its team, else a
    /// build the cast waits for), else its build's zone once done; null while the build runs, or with <paramref name="failed"/> set (why it
    /// gives up, the chat line's reason) when there is none.
    /// </summary>
    private MistsZone? ZoneFor(Player p, EnterCast cast, out (string Why, string Tell)? failed)
    {
        failed = null;
        if (cast.Join is { Closed: true })
        {
            var again = Joinable(p, cast.Mode, cast.Exit.Level, cast.Mode.IsDungeon ? cast.Exit.Tier : null, cast.Place, out var key);
            var rebuilt = again is null ? Pending(p, key, cast.Place, () => Build(cast.Mode, cast.Exit.Level, cast.Exit.Tier)) : null;
            if (again is null && rebuilt is null)
            {
                failed = ("the zone closed", "the zone closed; try again");
                return null;
            }
            (cast.Join, cast.Build, cast.TeamKey) = (again, rebuilt, key);
        }
        if (cast.Join is { Closed: false } join) return join;
        if (cast.Build is not { IsCompleted: true } build) return null;
        if (!build.IsCompletedSuccessfully)
        {
            Log.Warn("world", $"mists zone not built: {build.Exception?.InnerException?.Message}");
            failed = ("the zone could not be built", "the zone could not be built; try again");
            return null;
        }
        // Phase B: a portal inside a zone hands on that zone's reroll and entrance (its way back stays the open world's).
        var parent = Find(cast.Map.Cluster);
        if (Resolve(p, build, cast.TeamKey, parent?.Reroll ?? cast.Exit.Reroll, parent?.Entrance ?? cast.Map.Cluster) is { } zone) return zone;
        failed = ("its zone closed", "the zone closed; try again");
        return null;
    }

    /// <summary>
    /// The cast is over: after CastFinished (18) and CastSpell (19) around (<see cref="FakeSlotCasts"/>), the portal used (gone lifetimeafterlastuse
    /// later), the player into the zone (the push).
    /// </summary>
    private void Finish(Player p, EnterCast cast, MistsZone zone)
    {
        world.Dungeons.Used(cast.Exit);
        // Phase B: a portal inside a zone (Knightfall Abbey's, a wisp's, a resource zone's) keeps the way back to the open world.
        if (!MistsIds.IsInstance(cast.Map.Cluster)) p.Character.DungeonReturn = Back(cast.Exit.ArrivalOut(cast.Map.Cluster));
        Send(p, zone, ArrivalFor(zone, null), answer: false);
    }

    /// <summary>The way back of an arrival (the character's <see cref="Character.DungeonReturn"/>: kept over the borders, used by every way out).</summary>
    private static DungeonReturn Back(Arrival a) => new(a.Cluster, a.Position.X, a.Position.Y, a.Direction);

    // ---- into a zone (SPEC §6.5 "Arrival") ----

    /// <summary>The zone's border exits (mistsborderexit members), in template order.</summary>
    public static IReadOnlyList<CriticalMember> Borders(MistsZone zone) => [.. zone.Template.Critical.Where(m => m.Type == "mistsborderexit")];

    /// <summary>
    /// Where a player arrives (V11): coming through a border at <paramref name="from"/> (the old zone's border position) the border of the
    /// opposite side (E↔W, N↔S: the one whose direction from the centre is nearest −from [inference]); else (a portal) under Private the
    /// teammates' arrival (the zone's first), under Shared the border farthest from the players inside, else a random border; without
    /// borders the zone's default spawn.
    /// </summary>
    public Arrival ArrivalFor(MistsZone zone, Vector2? from)
    {
        // Phase B: into Knightfall Abbey or a resource zone (not through a border) at a MistsPlayerSpawnpoint (SPEC §8 item 3), the teammates together.
        if (zone.Mode.Type != "mists" && from is null && zone.Template.MistsSpots.Where(s => s.Kind == "MistsSpawnpoint").ToList() is { Count: > 0 } spawns)
        {
            if (zone.TeamKey.Length > 0 && zone.FirstArrival is { } together) return together;
            var s = spawns[Rng.Next(spawns.Count)];
            return new Arrival(zone.Id, s.Position, s.Direction);
        }
        var borders = Borders(zone);
        if (borders.Count == 0) return new Arrival(zone.Id, Transfers.DefaultSpawn(zone.Template), 0);
        CriticalMember pick;
        if (from is { } f && f != Vector2.Zero)
        {
            var back = Vector2.Normalize(-f);
            pick = borders.MaxBy(b => b.Position == Vector2.Zero ? -2 : Vector2.Dot(Vector2.Normalize(b.Position), back))!;
        }
        else if (zone.TeamKey.Length > 0 && zone.FirstArrival is { } first) return first;
        else if (Options.Matchmaking == MistsMatchmaking.Shared && world.Maps.Find(zone.Id) is { Players.Count: > 0 } map)
        {
            var others = map.Players.Values.Select(p => p.PositionAt(DateTime.UtcNow)).ToList();
            pick = borders.MaxBy(b => others.Min(o => Vector2.Distance(o, MistsBorderExit.ArrivalOf(b, zone.Id).Position)))!;
        }
        else pick = borders[Rng.Next(borders.Count)];
        return MistsBorderExit.ArrivalOf(pick, zone.Id);
    }

    /// <summary>
    /// <paramref name="p"/> goes into <paramref name="zone"/> at <paramref name="at"/>: counted inside until its Join (Dungeons:ArrivalGraceSeconds),
    /// the map made and filled now (the zone has its cluster), a return portal at the arrival for openworldexitportalalivetimer (30 s), then the
    /// push {0 id, 3 layout, 4 clustertype, 5 tier} (<paramref name="answer"/> false), or the caller answers a ChangeCluster with the arrival.
    /// </summary>
    public Arrival Send(Player p, MistsZone zone, Arrival at, bool answer)
    {
        var id = p.Character.Id;
        zone.Arriving[id] = world.Clock.Now + TimeSpan.FromSeconds(world.DungeonSettings.ArrivalGraceSeconds);
        zone.Entered.Add(id);
        zone.EmptySince = null;
        zone.FirstArrival ??= at;
        var map = world.Maps.Get(zone.Id);
        world.LoadTemplate(map);
        if (zone.Scaled && zone.Entered.Count > 1)
        {
            // A second player in a duo zone filled for one: no Mists:DuoSoloScaling for its spawns and respawns from now on (the mobs standing keep theirs) [design].
            zone.Scaled = false;
            (map.MobHealthFactor, map.MobDamageFactor) = (1, 1);
            Log.Info("world", $"{zone}: a second player comes in, the duo scaling ends");
        }
        if (zone.Returns.Remove(id, out var old) && old.Map is { } oldMap) oldMap.RemoveObject(old);
        if (map.Grid is not null)
        {
            var alive = Data?.Global("openworldexitportalalivetimer", 30) ?? 30;
            var spot = new CriticalMember
            {
                Key = new SpawnKey(zone.Id, "mists-return", map.CustomSpawns++), Tile = "SHARED_MIST_WISP_PORTAL", Position = at.Position, Direction = at.Direction,
                Component = new XElement("mistsimmediatereturnexit"), Type = "mistsimmediatereturnexit", Member = "return", Path = "",
            };
            var portal = new MistsReturnPortal(world.ObjectIds.Next(), spot, id, world.GameNow + TimeSpan.FromSeconds(alive).Ticks);
            zone.Returns[id] = portal;
            map.AddObject(portal);
        }
        p.Character.DungeonReturn ??= new DungeonReturn(Options.FallbackCluster, 0, 0, 0);
        Log.Info("world", string.Create(CultureInfo.InvariantCulture, $"'{p.Character.Name}' goes into {zone} at ({at.Position.X:0.##}, {at.Position.Y:0.##}); way back {p.Character.DungeonReturn.Cluster}"));
        if (!answer) p.Session.Teleport(at, $"mists:{zone.Id}", type: zone.Mode.ClusterType, layout: zone.Bytes, tier: zone.Tier);
        return at;
    }

    /// <summary>
    /// After JoinFinished (WorldSession): in a zone, MistsPlayerJoinedInfo (521, c4a.q 0x180A98800, V2) {0 the player's return portal while it
    /// lives (else 0), 1 its end, 2 the way back, 3 false, 4 the border exits' target (the way back), 5 the shutdown}, then once the chat lines.
    /// Phase B, on every map: a character with Brecilien standing gets it as UpdateStanding 87 (the client's standings start empty at a Join).
    /// </summary>
    public void Joined(Player p)
    {
        if (p.Character.MistStanding > 0) SendStanding(p, 0);
        if (p.Map is not { } map || Find(map.Cluster) is not { } zone) return;
        var id = p.Character.Id;
        zone.Arriving.Remove(id);
        zone.Entered.Add(id);
        zone.EmptySince = null;
        var back = p.Character.DungeonReturn?.Cluster is { Length: > 0 } c && !MistsIds.IsInstance(c) ? c : Options.FallbackCluster;
        var portal = zone.Returns.GetValueOrDefault(id) is { } r && world.GameNow <= r.End && r.Map == map ? r : null;
        p.Session.Send(new MistsPlayerJoinedInfoEvent
        {
            ebf = portal?.Id ?? 0,      // [0] the return portal (520) the client lets this player click
            ebg = portal?.End ?? 0,     // [1] its end (the tooltip's "available for")
            ebh = back,                 // [2] the return target (world.xml), the tooltip's
            ebi = false,                // [3]
            ebj = back,                 // [4] the target cluster of every border exit (cnk.aqt): the client's exit checks and tooltip [design]
            ebk = zone.ShutdownTicks,   // [5] the shutdown (c4a.a = key 5 - clustershutdowncountdowntimer)
        });
        var left = zone.ShutdownAt - world.Now;
        if (zone.Told.Add(id))
        {
            Expeditions.Tell(p, Settings.Text("Entered", ("mode", zone.Mode.DisplayName), ("rarity", RarityNames[Math.Clamp((int)zone.Level, 0, 4)]), ("tier", zone.Tier),
                ("minutes", Math.Round(Math.Max(0, left.TotalMinutes)).ToString(CultureInfo.InvariantCulture))));
            if (zone.Scaled) Expeditions.Tell(p, Settings.Text("DuoAlone"));
        }
        // Within the shutdown countdown already (a late arrival, a relog): the Closing line it missed.
        if (left <= TimeSpan.FromSeconds(Data?.Global("clustershutdowncountdowntimer", 300) ?? 300))
            Expeditions.Tell(p, Settings.Text("Closing", ("seconds", Math.Round(Math.Max(0, left.TotalSeconds)).ToString(CultureInfo.InvariantCulture))));
    }

    // ---- the zone's fill (SPEC §6.4) ----

    /// <summary>
    /// At the fill of a map (MapSpawner, before its mobs): a mists world cluster's (5100-5103) city entrances; a zone's border exits, city
    /// entrance and open-world exit, its mob level (DifficultyLevels group mists, <see cref="Map.Difficulty"/>), Mists:DuoSoloScaling for one
    /// player in a duo zone, and its treasures planned.
    /// </summary>
    internal IEnumerable<WorldObject> Populate(Map map, ClusterTemplate template)
    {
        if (!Options.Enabled) return [];
        if (Find(map.Cluster) is not { } zone)
            return MistsIds.IsInstance(map.Cluster) ? [] : WorldObjectsOf(template);
        var objects = new List<WorldObject>();
        foreach (var m in template.Critical)
            switch (m.Type)
            {
                case "mistsborderexit": objects.Add(new MistsBorderExit(world.ObjectIds.Next(), m)); break;
                case "mistscityentrance": objects.Add(new MistsCityEntrance(world.ObjectIds.Next(), m)); break;
                case "mistsopenworldexit": objects.Add(new MistsOpenWorldExit(world.ObjectIds.Next(), m, v => OpenWorldTargetOf(zone, v))); break;
                case "mistsdungeonexit": objects.Add(new MistsDungeonExit(world.ObjectIds.Next(), m)); break; // phase B: Knightfall Abbey's ways out
            }
        PopulateB(zone, map, template, objects);
        if (Options.OpenWorldExit == MistsExitTarget.Random && objects.Any(o => o is MistsOpenWorldExit))
        {
            zone.OpenWorldTarget = RandomWorldCluster(zone);
            if (zone.OpenWorldTarget is { } t) world.PrefetchCluster(t);
        }
        world.PrefetchCluster(Data?.CityTarget ?? "5001"); // the Brecilien entrance's arrival
        var group = Data?.DifficultyGroupOf(zone.Mode) is { } g ? world.Clusters?.DungeonsIfLoaded?.DifficultyGroups.GetValueOrDefault(g) : null;
        map.Difficulty = group?.GetValueOrDefault(zone.Build.MobLevel);
        zone.Scaled = zone.Mode.Duo && Math.Max(zone.Entered.Count, zone.Expected) <= 1 && Options.DuoSoloScaling != 1; // the team that waited for its build
        if (zone.Scaled) (map.MobHealthFactor, map.MobDamageFactor) = (Options.DuoSoloScaling, Options.DuoSoloScaling);
        PlanTreasures(zone, map, template);
        return objects;
    }

    /// <summary>Where the open-world exit leads <paramref name="viewer"/>: the zone's drawn cluster (Random), else the viewer's way back.</summary>
    private string OpenWorldTargetOf(MistsZone zone, Player viewer) =>
        zone.OpenWorldTarget ?? (viewer.Character.DungeonReturn?.Cluster is { Length: > 0 } c && !MistsIds.IsInstance(c) ? c : Options.FallbackCluster);

    /// <summary>
    /// Mists:OpenWorldExit Random (SPEC §6.5): a world.xml World cluster of the warning type drawn by the mode's openworldexitdistribution (safe,
    /// yellow, red, black), the zone's tier ± 1, on the continent of its entrance (<see cref="MistsZone.Entrance"/>; the other one, royal or
    /// outlands, only when none of the type is there: black on royal); null when none fits (the way back then).
    /// </summary>
    private string? RandomWorldCluster(MistsZone zone)
    {
        if (world.Clusters is not { } store || store.WorldIfLoaded is not { } info || zone.Mode.OpenWorldExits.Count == 0) return null;
        var type = DungeonGenerator.Pick(Rng, [.. zone.Mode.OpenWorldExits.Select(w => (w.Type, w.Weight))]);
        var warning = type switch { "safe" => 0, "yellow" => 1, "red" => 3, "black" => 4, _ => -1 };
        var home = zone.Entrance is { } e && info.Clusters.FirstOrDefault(c => c.Id == e) is { } ec && ClusterNameInfo.Parse(ec.File) is { Continent: 1 or 2 } h ? h.Continent : 0;
        var (same, other) = (new List<string>(), new List<string>());
        foreach (var c in info.Clusters)
        {
            if (ClusterNameInfo.Parse(c.File) is not { Type: 0, Continent: 1 or 2 } cni || Math.Abs(cni.Tier - zone.Tier) > 1) continue;
            try
            {
                if (store.Archetypes.Of(c.Type).PvpWarning == warning) (home == 0 || cni.Continent == home ? same : other).Add(c.Id);
            }
            catch (Exception ex) when (ex is InvalidDataException or IOException) { return null; }
        }
        var options = same.Count > 0 ? same : other;
        return options.Count == 0 ? null : options[Rng.Next(options.Count)];
    }

    /// <summary>
    /// The zone's treasures (SPEC §6.4): each &lt;treasures&gt; subtype content/mists.json maps rolls rolls × chance (from its mindifficulty);
    /// a success is one of its names, unlocked uniform(initial, final block time) × Mists:BlockFactor after the start, at a camp's mob spawn point
    /// at least ArrivalMinDistance from the arrivals and MinTreasureSpacing from the other treasures [design: the open masters have no
    /// treasure tiles].
    /// </summary>
    private void PlanTreasures(MistsZone zone, Map map, ClusterTemplate template)
    {
        var arrivals = Borders(zone).Select(b => MistsBorderExit.ArrivalOf(b, zone.Id).Position).ToList();
        var spots = template.MobSpawnPoints.Select(s => s.Position).Distinct().ToList();
        if (spots.Count == 0) spots = [.. zone.Build.Layout.Templates.Skip(1).Select(t => t.Position)];
        if (spots.Count == 0) return;
        var rng = map.Random;
        // Phase B: Knightfall Abbey's treasures stand on its treasurespawnpoint tiles of their subtype (SPEC §2.5) and are its own (DungeonTreasures).
        var dungeon = zone.Mode.IsDungeon;
        void Plan(string subtype, string name, double from, double to)
        {
            var candidates = dungeon && template.MistsSpots.Where(s => s.Kind == "treasure:" + subtype).Select(s => s.Position).ToList() is { Count: > 0 } tiles ? tiles : spots;
            var free = candidates.Where(s => arrivals.All(a => Vector2.Distance(a, s) >= Settings.ArrivalMinDistance)
                && zone.Treasures.All(o => Vector2.Distance(o.At, s) >= Settings.MinTreasureSpacing)).ToList();
            var at = (free.Count > 0 ? free : candidates)[rng.Next(free.Count > 0 ? free.Count : candidates.Count)];
            var seconds = (from + rng.NextDouble() * Math.Max(0, to - from)) * Options.BlockFactor;
            zone.Treasures.Add(new MistsTreasureSpot(subtype, name, at, zone.Created + TimeSpan.FromSeconds(seconds)));
        }
        foreach (var t in zone.Mode.Treasures)
        {
            if (zone.Level < t.MinDifficulty || !(dungeon ? Settings.DungeonTreasures : Settings.Treasures).TryGetValue(t.Subtype, out var names) || names.Count == 0) continue;
            for (var i = 0; i < t.Rolls; i++)
            {
                if (rng.NextDouble() >= t.Chance) continue;
                Plan(t.Subtype, names[rng.Next(names.Count)].Replace("{tier}", zone.Tier.ToString(CultureInfo.InvariantCulture), StringComparison.Ordinal),
                    t.InitialBlockSeconds, t.FinalBlockSeconds);
            }
        }
        // Phase B (SPEC §8 item 3): a zone the resource modes lead back to (MISTS_SOLO_BLACK) holds, by ResourceWispChance, a resource wisp of one of them [design].
        if (Data?.Modes.Values.Where(m => m.IsResource && m.ExitsLeadTo == zone.Mode.Name).ToList() is { Count: > 0 } resources && rng.NextDouble() < Settings.ResourceWispChance)
        {
            var wisps = zone.Mode.Treasures.FirstOrDefault(t => t.Subtype == "WISPS");
            Plan(ResourceWisp, resources[rng.Next(resources.Count)].Name, wisps?.InitialBlockSeconds ?? 60, wisps?.FinalBlockSeconds ?? 420);
        }
        if (zone.Treasures.Count > 0)
            Log.Info("world", string.Create(CultureInfo.InvariantCulture, $"{zone}: treasures {string.Join(", ", zone.Treasures.Select(t => $"{t.Name} in {(t.Due - zone.Created).TotalSeconds:0} s"))}"));
    }

    /// <summary>
    /// A treasure whose time has come: a LootChestSpawnpoint's chest (Unlocked; the client channels it) or a mob of mobs.xml (MYTHICALMOBS: a
    /// mist boss, <see cref="MistsZone.Bosses"/>); a name that is neither is warned about once and dropped.
    /// </summary>
    private void Unlock(MistsZone zone, Map map, MistsTreasureSpot t)
    {
        if (t.Subtype == ResourceWisp)
        {
            if (Data?.Modes.GetValueOrDefault(t.Name) is { } resource) SpawnWisp(zone, map, resource, RollRarity(zone.Reroll, map.Random), t.At, null);
            return;
        }
        if (world.Clusters?.LootChestsIfLoaded is { } chests && chests.Spawnpoints.TryGetValue(t.Name, out var behaviour))
        {
            var point = new LootChestSpawn
            {
                Key = new SpawnKey(map.Cluster, "mists-treasure", map.CustomSpawns++), Tile = "", Position = t.At, Direction = 0,
                Component = new XElement("lootchestspawnpoint", new XAttribute("spawnbehaviour", t.Name)), SpawnBehaviour = t.Name, Behaviour = behaviour,
            };
            if (world.Dungeons.MakeChest(map, point, chests, state: 6, lootTier: zone.Tier) is { } chest)
            {
                world.TreasureCoffers.Contest(map, chest, t.Subtype); // issue #19: its pvpzoneradius circle (Pvp:Mode Official); #23: listed
                map.AddObject(chest);
                Log.Info("world", $"{zone}: {t.Subtype} {chest}");
            }
            return;
        }
        if (world.Clusters?.MobsIfLoaded?.Find(t.Name) is { } mob)
        {
            var m = SpawnManager.SpawnAt(map, mob, t.At, (float)(map.Random.NextDouble() * 360), world);
            if (t.Subtype == "MYTHICALMOBS") zone.Bosses.Add(m.Id);
            Log.Info("world", $"{zone}: {t.Subtype} {m}");
            return;
        }
        Combat.LogOnce("mists-treasure:" + t.Name, $"mists treasure {t.Name} ({t.Subtype}) is no LootChestSpawnpoint or mob of the client data: not spawned");
    }

    // ---- the mist boss (SPEC §6.1, §6.4) ----

    /// <summary>The players who get a mist boss's reward chest (Combat.Kill, before the death clears its threat): BossChestShare of its threat, and the killer; null for any other mob.</summary>
    public List<Player>? Looters(Mob mob, Player killer)
    {
        if (mob.Map is not { } map || Find(map.Cluster) is not { } zone || !zone.Bosses.Contains(mob.Id)) return null;
        var total = mob.Threat.Values.Where(v => v > 0).Sum();
        var list = mob.Threat.Where(kv => kv.Value > 0 && kv.Value >= Options.BossChestShare * total && kv.Key.Map == map).Select(kv => kv.Key).ToList();
        if (!list.Contains(killer)) list.Insert(0, killer);
        return list;
    }

    /// <summary>
    /// A mob of a zone died (Combat.Kill, after the kill batch): a mist boss's death is told to everyone inside (Mists.BossDown) and, with
    /// Mists:BossChest, leaves a personal reward chest (MISTS_TREASURE_SOLO) at its body, open to each of <paramref name="looters"/> alone.
    /// </summary>
    public void Killed(Map map, Mob mob, IReadOnlyList<Player>? looters)
    {
        if (Find(map.Cluster) is not { } zone) return;
        KilledB(zone, map, mob);
        if (!zone.Bosses.Remove(mob.Id)) return;
        foreach (var p in map.Players.Values) Expeditions.Tell(p, Settings.Text("BossDown", ("boss", mob.Template.Name)));
        if (!Options.BossChest || world.Clusters?.LootChestsIfLoaded is not { } chests || Settings.Treasures.GetValueOrDefault("CHEST") is not [var spawnpoint, ..]
            || !chests.Spawnpoints.TryGetValue(spawnpoint, out var behaviour)) return;
        var point = new LootChestSpawn
        {
            Key = new SpawnKey(map.Cluster, "mists-boss", map.CustomSpawns++), Tile = "", Position = mob.Position, Direction = 0,
            Component = new XElement("lootchestspawnpoint", new XAttribute("spawnbehaviour", spawnpoint)), SpawnBehaviour = spawnpoint, Behaviour = behaviour,
        };
        if (world.Dungeons.MakeChest(map, point, chests, state: 6, lootTier: zone.Tier, personal: true) is not { } chest) return;
        foreach (var p in looters ?? []) chest.SetViewerState(p, 6, world.GameNow);
        map.AddObject(chest);
        Log.Info("world", $"{zone}: the mist boss {mob.Template.Name} is slain; reward chest {chest} for {string.Join(", ", (looters ?? []).Select(p => p.Character.Name))}");
    }

    // ---- leaving (SPEC §6.5, §6.6) ----

    /// <summary>The way back of <paramref name="p"/> (its <see cref="Character.DungeonReturn"/>, else Mists:FallbackCluster), cleared: it is out of the mists.</summary>
    public Arrival Out(Player p)
    {
        var back = p.Character.DungeonReturn is { Cluster.Length: > 0 } b && !MistsIds.IsInstance(b.Cluster) ? b : new DungeonReturn(Options.FallbackCluster, 0, 0, 0);
        p.Character.DungeonReturn = null;
        if (back.X == 0 && back.Y == 0 && world.CachedCluster(back.Cluster) is { } t) return new Arrival(back.Cluster, Transfers.DefaultSpawn(t), 0);
        return new Arrival(back.Cluster, new Vector2(back.X, back.Y), back.Direction);
    }

    /// <summary>The Brecilien entrance's arrival (SPEC §6.5): mistscityentrancetargetclusterid (5001) at its first respawn point, (−40, −4) before it is read [inference].</summary>
    public Arrival CityArrival()
    {
        var city = Data?.CityTarget ?? "5001";
        return world.CachedCluster(city) is { RespawnPoints: [var spawn, ..] } ? new Arrival(city, spawn.Position, spawn.Direction) : new Arrival(city, new Vector2(-40, -4), 0);
    }

    /// <summary>
    /// A death in a zone respawns in Mists:RespawnCluster (SPEC §6.6), out of the mists (the way back cleared), a zone closing meanwhile as well
    /// (Close, Death.Departed); null elsewhere.
    /// </summary>
    public string? RespawnOf(Player p)
    {
        if (!MistsIds.IsInstance(p.Map?.Cluster)) return null;
        p.Character.DungeonReturn = null;
        return Options.RespawnCluster;
    }

    /// <summary>
    /// The cluster and point of a character whose saved cluster is the id of a zone that is gone (closed, or a restart): its way back, else
    /// Mists:FallbackCluster; changed and saved. False when its cluster is no mists zone that is gone.
    /// </summary>
    public bool ReturnIfClosed(Character c)
    {
        if (!MistsIds.IsInstance(c.Cluster) || Find(c.Cluster) is not null) return false;
        var to = c.DungeonReturn is { Cluster.Length: > 0 } b && !MistsIds.IsInstance(b.Cluster) ? b : new DungeonReturn(Options.FallbackCluster, -40, -4, 0);
        (c.Cluster, c.PositionX, c.PositionY, c.Direction) = (to.Cluster, to.X, to.Y, to.Direction);
        c.PendingTransfer = null;
        c.DungeonReturn = null;
        world.Saves.SaveNow(c);
        Log.Info("world", $"'{c.Name}' was in a mists zone that is gone: back in {to.Cluster}");
        return true;
    }

    /// <summary>
    /// The zone closes (its shutdown, empty, a GM): every player inside is pushed out to its way back (a knocked-down one in a lethal zone dies
    /// first, a dead one respawns), the chat line Mists.Closed; the map goes, the id is never used again.
    /// </summary>
    public void Close(MistsZone zone, string why)
    {
        if (zone.Closed) return;
        zone.Closed = true;
        if (world.Maps.Find(zone.Id) is { } map)
        {
            foreach (var p in map.Players.Values.ToList())
            {
                if (p.Session.Status != SessionStatus.InWorld || p.Session.Player != p)
                {
                    world.Maps.Remove(p, world.Clock.Now);
                    continue;
                }
                if (p.Combat.Down is { Kind: not KnockdownKind.Dead }) Death.Abandoned(p);
                if (p.Combat.Down is { Kind: KnockdownKind.Dead } && Death.Respawn(p, "mists zone closed")) continue;
                var to = Out(p);
                Expeditions.Tell(p, Settings.Text("Closed", ("place", to.Cluster)));
                p.Session.Teleport(to, p.Combat.Down is { Kind: KnockdownKind.Dead } ? Death.RespawnVia : $"mists:{zone.Id}:closed");
            }
            world.Maps.Unload(map, force: true);
        }
        _zones.Remove(zone.Id);
        Log.Info("world", $"{zone} closed ({why})");
    }

    // ---- the timers ----

    /// <summary>
    /// Every tick (<see cref="World.Update"/>, after <see cref="FakeSlotCasts.Update"/> has run the enter casts: a player gone or knocked down ends
    /// one; at its end, once its zone is built, at most 30 s more, the push; the zone it joins closed meanwhile: another one or a new build,
    /// <see cref="ZoneFor"/>): the finished builds let go (a pending one nobody went in by after 60 s), then each zone's return portals at their end, its treasures due, the shutdown warnings, the shutdown, and an
    /// empty zone's close after Mists:EmptySeconds.
    /// </summary>
    public void Update()
    {
        var now = world.Now;
        UpdateStatic(now); // phase B: Brecilien's static entrance
        _builds.RemoveAll(t => t.IsCompleted);
        foreach (var (key, pending) in _pending.Where(e => e.Value.Build.IsCompleted && now - e.Value.Since >= TimeSpan.FromSeconds(60)).ToList()) _pending.Remove(key);
        var ticks = world.GameNow;
        var countdown = TimeSpan.FromSeconds(Data?.Global("clustershutdowncountdowntimer", 300) ?? 300);
        var alert = TimeSpan.FromSeconds(Data?.Global("clustershutdownalerttimer", 60) ?? 60);
        foreach (var zone in _zones.Values.ToList())
        {
            var map = world.Maps.Find(zone.Id);
            foreach (var (who, portal) in zone.Returns.Where(r => ticks > r.Value.End).ToList())
            {
                zone.Returns.Remove(who);
                portal.Map?.RemoveObject(portal);
            }
            if (map is { Grid: not null, Unloaded: false })
                foreach (var t in zone.Treasures.Where(t => now >= t.Due).ToList())
                {
                    zone.Treasures.Remove(t);
                    Unlock(zone, map, t);
                }
            if (now >= zone.ShutdownAt)
            {
                Close(zone, "shutdown");
                continue;
            }
            if (map is { Grid: not null, Unloaded: false }) UpdateB(zone, map, now); // phase B: the dungeon portal waves, the wisps
            foreach (var (warned, before, set) in new (bool, TimeSpan, Action)[] { (zone.WarnedCountdown, countdown, () => zone.WarnedCountdown = true), (zone.WarnedAlert, alert, () => zone.WarnedAlert = true) })
            {
                if (warned || now < zone.ShutdownAt - before) continue;
                set();
                foreach (var p in map?.Players.Values ?? Enumerable.Empty<Player>())
                    Expeditions.Tell(p, Settings.Text("Closing", ("seconds", Math.Round((zone.ShutdownAt - now).TotalSeconds).ToString(CultureInfo.InvariantCulture))));
            }
            foreach (var late in zone.Arriving.Where(a => now >= a.Value).Select(a => a.Key).ToList()) zone.Arriving.Remove(late);
            if ((map?.Players.Count ?? 0) > 0 || zone.Arriving.Count > 0) zone.EmptySince = null;
            else zone.EmptySince ??= now;
            if (zone.EmptySince is { } since && now - since >= TimeSpan.FromSeconds(Options.EmptySeconds)) Close(zone, $"empty for {Options.EmptySeconds:0} s");
        }
    }

    /// <summary>.mists list: the live zones, one line each.</summary>
    public IEnumerable<string> Describe()
    {
        var now = world.Now;
        foreach (var z in _zones.Values)
            yield return string.Create(CultureInfo.InvariantCulture,
                $"  {z.Id} {z.Mode.Name} {z.Map.Id} T{z.Tier} {RarityNames[Math.Clamp((int)z.Level, 0, 4)]}: {world.Maps.Find(z.Id)?.Players.Count ?? 0} inside, "
                + $"{z.Entered.Count} entered, shuts down in {Math.Max(0, (z.ShutdownAt - now).TotalMinutes):0} min, {z.Treasures.Count} treasure(s) to come, {z.Bosses.Count} boss(es), "
                + $"{z.Wisps.Count} wisp(s){(z.NextWave is { } w ? $", dungeon portals in {Math.Max(0, (w - now).TotalSeconds):0} s" : "")}")

                + (z.TeamKey.Length > 0 ? $", private ({z.TeamKey})" : ", shared");
    }
}
