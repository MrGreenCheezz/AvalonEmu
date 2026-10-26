using System.Diagnostics;
using System.Globalization;
using System.Numerics;
using AlbionLocal.Photon;
using AlbionLocal.Server.Content;
using AlbionLocal.Server.Data;
using AlbionLocal.Server.Entities;
using AlbionLocal.Server.Game;
using AlbionLocal.Server.Network;
using AlbionLocal.Server.Protocol;
using AlbionLocal.Server.Systems;

namespace AlbionLocal.Server.Bots;

/// <summary>How a bot's population slot fills again after the bot is gone (<see cref="BotManager.Despawn"/>).</summary>
public enum BotRefill
{
    /// <summary>At the next population pass (the map's last human left; a player took its name).</summary>
    Now,

    /// <summary>After Bots:RespawnSeconds (a defeat, a GM's .bot clear), with the same name.</summary>
    Respawn,

    /// <summary>After Bots:ArrivalSeconds, by another "player" coming through an exit (it left through one or logged out).</summary>
    Arrival,
}

/// <summary>
/// One place of a map's bot population (SPEC §1.4): its role and number, the persona "&lt;cluster&gt;:&lt;role&gt;:&lt;index&gt;" (stable for the
/// cluster over respawns and map reloads), the name its bots keep over a respawn, the live bot or when the slot is ready again.
/// </summary>
public sealed class BotSlot
{
    public required BotRole Role { get; init; }

    public required int Index { get; init; }

    public required string PersonaId { get; init; }

    public string? Name { get; internal set; }

    public Player? Bot { get; internal set; }

    /// <summary><see cref="World.Clock"/> time from which the slot may be filled.</summary>
    public TimeSpan ReadyAt { get; internal set; }

    /// <summary>The next bot arrives at an exit (after a turnover), even inside a human's view, as a player coming through it.</summary>
    public bool ArriveAtExit { get; internal set; }

    /// <summary>A patrol's group among the map's (Bots:Patrol:GroupSize slots in index order share one); 0 for the other roles.</summary>
    public int Group { get; init; }

    /// <summary>
    /// Bot-roster: the roster identity (<see cref="BotIdentity.Index"/>) the slot holds while its bot is online or waits for its respawn
    /// (no other slot takes it meanwhile); null when its bot left through an exit or logged out (then <see cref="LastIdentity"/>).
    /// </summary>
    public int? Identity { get; internal set; }

    /// <summary>The identity of the slot's last bot: a turnover's newcomer is another one while another is free.</summary>
    public int? LastIdentity { get; internal set; }
}

/// <summary>
/// Stage I: the player bots of the world (analysis/work/stageI/bots/SPEC.md §1.2, §1.4, §1.7): <see cref="World.Bots"/>. A bot is an ordinary
/// <see cref="Player"/> with <see cref="Player.Bot"/> on a <see cref="BotPeer"/> session whose character is transient (no account, no row,
/// never saved), kept in <see cref="Map.Bots"/>: the humans see it through the normal visibility (NewCharacter, event 3, Leave), and mobs,
/// events, rewards and parties never do. Per loaded map a population (content/bots.json Counts by its zone class × Scale by its humans, caps
/// Bots:MaxPerMap and Bots:MaxTotal) is filled every Bots:PopulateSeconds while a human is on it and its walk grid exists
/// (<see cref="BotNavCache"/>), at most Bots:SpawnPerPass at a time, each bot out of every human's view (more than Bots:MinSpawnCells
/// cells away). A bot thinks every Bots:ThinkSeconds while a human is within Bots:ActiveRange or sees it, walks its path on every tick
/// (<see cref="BotMotion"/>), leaves after its stay (Bots:StayMinutes: through an exit or a logout; the next of its slot arrives at an
/// exit Bots:ArrivalSeconds later), and every bot of a map goes Bots:LingerSeconds after its last human. GM bots (.bot spawn) are extra.
/// Phase 1 populates city bots (<see cref="CityBrain"/>), phase 2 gatherers (<see cref="GathererBrain"/>: their harvest shows, their fights
/// and defeats, <see cref="BotCombat"/>), phase 3 patrols (<see cref="PatrolBrain"/>: groups of Bots:Patrol:GroupSize on a route, only where
/// a Red player may be fought: PvpAllowed, PvpForced, None) and the rewards of a defeat (<see cref="BotRewards"/>). A fighting bot decides
/// every Bots:Combat:TickMs. Touched only under <see cref="World.Sync"/>.
/// </summary>
public sealed partial class BotManager(World world)
{
    /// <summary>The account of every bot character: a constant no repository account has.</summary>
    public static readonly Guid Account = new("b0700000-0000-0000-0000-000000000000");

    /// <summary>The roles the population fills (phase 1 city bots, phase 2 gatherers, phase 3 patrols; stageL mists phase B the Drifters).</summary>
    public static readonly IReadOnlySet<BotRole> PopulatedRoles = new HashSet<BotRole> { BotRole.City, BotRole.Gatherer, BotRole.Patrol, BotRole.Drifter };

    /// <summary>A patrol or a Drifter (stageL mists: a patrol of one): Red, on a route, attacking the players it may fight on sight.</summary>
    public static bool Patrols(BotRole role) => role is BotRole.Patrol or BotRole.Drifter;

    /// <summary>Harvestable types gatherers leave alone beside the corpses (mobs' and treasure harvestables).</summary>
    private static readonly string[] NotGathered = ["_CRITTER", "_GUARDIAN", "_TREASURE"];

    /// <summary>What the manager keeps per map: its walk grid, slots, zone class, next pass and the POIs it found no path to.</summary>
    private sealed class MapState
    {
        public ClusterNav? Nav;
        public List<BotSlot>? Slots;
        public TimeSpan NextPass;
        public readonly HashSet<BotPoi> Unreachable = [];
        public List<Harvestable>? Nodes;

        /// <summary>The patrol groups by <see cref="BotSlot.Group"/>.</summary>
        public readonly Dictionary<int, BotGroup> Groups = [];

        /// <summary>Its humans by object id → <see cref="World.Clock"/> time the bots first saw them on it (Bots:Patrol:JoinGraceSeconds).</summary>
        public readonly Dictionary<long, TimeSpan> Arrived = [];
    }

    private readonly Dictionary<Map, MapState> _maps = [];
    private readonly HashSet<string> _live = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<int> _held = []; // bot-roster: the identities a slot holds (BotSlot.Identity)
    private (BotSettings Settings, int Seed, int Size, BotRoster Roster)? _roster;
    private HashSet<string>? _characterNames;
    private Random? _random;
    private bool? _enabled;
    private int _total;

    public BotNavCache Nav { get; } = new(world);

    public BotFactory Factory { get; } = new(world);

    /// <summary>The rewards of a bot's defeat and their farming caps.</summary>
    public BotRewards Rewards { get; } = new(world);

    /// <summary>Bot-social: the world bots' presence to invites, friend requests and whispers, and their local chat.</summary>
    public BotSocial Social => _social ??= new BotSocial(world, this);

    private BotSocial? _social;

    /// <summary>The bots' random numbers (seeded with World.MapSeed when that is set: the self-test).</summary>
    public Random Random => _random ??= world.MapSeed is { } seed ? new Random(seed) : new Random();

    public BotOptions Options => world.Config.Bots;

    private readonly Dictionary<double, Progression> _gear = [];

    /// <summary>
    /// The destiny board a bot wears its gear with (Bots:Gear:Mastery; a companion's Companions:GearMastery, stageN botpower): the world's
    /// unlock-all one at 1, else a copy with every level × the share (rounded); null without game data. Built once per share.
    /// </summary>
    public Progression? GearProgression(double? share = null)
    {
        var s = share ?? Options.GearMastery;
        if (s >= 1 || world.UnlockAll is not { } all) return world.UnlockAll;
        if (!_gear.TryGetValue(s, out var board))
        {
            board = all.Clone();
            foreach (var (id, level) in all.Levels) board.Levels[id] = (int)Math.Round(level * s, MidpointRounding.AwayFromZero);
            _gear[s] = board;
        }
        return board;
    }

    public BotSettings Settings => world.BotSettings;

    /// <summary>
    /// Bot-roster: the roster of Bots:Roster:Seed and Bots:Roster:Size over content/bots.json's syllables (built once, again after a change
    /// of those); null with Bots:Roster:Enabled false.
    /// </summary>
    public BotRoster? Roster
    {
        get
        {
            if (!Options.RosterEnabled) return null;
            if (_roster is { } r && r.Settings == Settings && r.Seed == Options.RosterSeed && r.Size == Options.RosterSize) return r.Roster;
            var roster = BotRoster.Build(Settings, Options.RosterSeed, Options.RosterSize);
            if (roster.All.Count < Options.RosterSize)
                Log.Warn("bots", $"Bots:Roster:Size {Options.RosterSize}: the name syllables of content/bots.json give {roster.All.Count} names only");
            _roster = (Settings, Options.RosterSeed, Options.RosterSize, roster);
            return roster;
        }
    }

    /// <summary>Whether roster identity <paramref name="identity"/> may come online now: no slot holds it and its name is free (<see cref="Taken"/>).</summary>
    public bool IdentityFree(BotIdentity identity) => !_held.Contains(identity.Index) && !_away.Contains(identity.Index) && !Taken(identity.Name);

    /// <summary>The roster identity <paramref name="slot"/> holds, released (a turnover, a player took its name, its map unloaded).</summary>
    private void Release(BotSlot slot)
    {
        if (slot.Identity is not { } held) return;
        _held.Remove(held);
        (slot.LastIdentity, slot.Identity) = (held, null);
    }

    /// <summary>Bot-schedule: the factor of Bots:Schedule now (the bots' clock, local by Bots:Schedule:UtcOffsetHours).</summary>
    public double ScheduleFactor => Options.ScheduleAt(Time);

    /// <summary>
    /// Whether a roster identity of home tier <paramref name="tier"/> wears it in zone class <paramref name="zone"/> on a map of tier
    /// <paramref name="mapTier"/> (<see cref="BotFactory.TierFor"/>'s range): a royal city's CityTiers, a start town's 1-2, a mists zone's
    /// own, the open world's map tier ±1 (a tier 0 map counts as 4).
    /// </summary>
    public bool TierAllowed(string zone, int mapTier, int tier) => zone switch
    {
        "City" => Settings.CityTiers.GetValueOrDefault(tier) > 0,
        "Start" => tier is 1 or 2,
        "Mists" when mapTier > 0 => tier == mapTier,
        _ => Math.Abs(tier - (mapTier == 0 ? 4 : mapTier)) <= 1,
    };

    /// <summary>Bots:Enabled, or a GM's .bot on|off since the start; switching off takes every bot away.</summary>
    public bool Enabled
    {
        get => _enabled ?? Options.Enabled;
        set
        {
            _enabled = value;
            if (!value) ClearAll("bots switched off", BotRefill.Now);
        }
    }

    /// <summary>Bots:World:Enabled under the master: the open-world population (City, Gatherer, Patrol, Drifter).</summary>
    public bool WorldEnabled => Enabled && Options.WorldEnabled;

    /// <summary>Bots:Arena:Enabled, Bots:Hellgate:Enabled, Bots:Invader:Enabled under the master (not Bots:World:Enabled).</summary>
    public bool ArenaBots => Enabled && Options.ArenaEnabled;

    public bool HellgateBots => Enabled && Options.HellgateEnabled;

    public bool InvaderBots => Enabled && Options.InvaderEnabled;

    /// <summary>Whether the population may place bots of world role <paramref name="role"/> (Bots:City/Gatherer/Patrol/Drifter:Enabled).</summary>
    public bool RoleEnabled(BotRole role) => role switch
    {
        BotRole.City => Options.CityEnabled,
        BotRole.Gatherer => Options.GathererEnabled,
        BotRole.Patrol => Options.PatrolEnabled,
        BotRole.Drifter => Options.DrifterEnabled,
        _ => true,
    };

    /// <summary>The bots' clock: the world's game time as a UTC DateTime (production: the UTC time; the self-test: its manual clock).</summary>
    public DateTime Time => new(world.GameNow, DateTimeKind.Utc);

    /// <summary>Every bot in the world.</summary>
    public IEnumerable<Player> All => world.Maps.Maps.SelectMany(m => m.Bots.Values);

    public int Count => _total;

    /// <summary>The population slots of <paramref name="map"/> (empty before its first pass).</summary>
    public IReadOnlyList<BotSlot> SlotsOf(Map map) => _maps.GetValueOrDefault(map)?.Slots ?? [];

    private MapState StateOf(Map map) => _maps.TryGetValue(map, out var s) ? s : _maps[map] = new MapState();

    /// <summary>The walk grid of <paramref name="map"/> when built (a build is started otherwise); kept by the map while it has bots.</summary>
    public ClusterNav? NavOf(Map map)
    {
        var state = StateOf(map);
        if (state.Nav is null && Nav.TryGet(map, out var nav)) state.Nav = nav;
        return state.Nav;
    }

    /// <summary>
    /// The zone class of <paramref name="map"/> (SPEC §1.4): City for a royal city (world.xml PLAYERCITY_*), Start for a start town
    /// (STARTINGCITY), Roads for a Roads of Avalon map (TUNNEL_*, stageL roads SPEC §5.6), Mists for a live mists zone (stageL mists SPEC §6.9:
    /// its Drifters, not the Yellow or Black counts), else its pvpwarningtype (Safe, Yellow, Orange, Red, Black); null where bots never are: no
    /// template, a dungeon floor, an expedition, content/bots.json Exclude, and every other PvpDisabled cluster (islands, hideouts, arenas) unless it is in Include.
    /// </summary>
    public string? ZoneOf(Map map)
    {
        if (map.Template is not { } t || DungeonLayout.IsInstance(map.Cluster) || world.Expeditions.IsLive(map.Cluster) || CorruptedData.IsInstance(map.Cluster)
            || HellgateData.IsInstance(map.Cluster) || ArenaIds.IsInstance(map.Cluster) || Settings.Exclude.Contains(map.Cluster)) return null;
        if (MistsIds.IsInstance(map.Cluster)) return world.Mists.Find(map.Cluster) is null ? null : "Mists";
        var type = t.Info.Type;
        if (type.StartsWith("PLAYERCITY_", StringComparison.Ordinal)) return "City";
        if (type == "STARTINGCITY") return "Start";
        if (type.StartsWith("TUNNEL_", StringComparison.Ordinal)) return "Roads"; // stageL roads: black zones of their own counts and gank groups
        if (t.Pvp.Rule == PvpRule.PvpDisabled && !Settings.Include.Contains(map.Cluster)) return null;
        var warning = t.Pvp.WarningType;
        return BotSettings.Zones.FirstOrDefault(z => z.Equals(warning, StringComparison.OrdinalIgnoreCase)) ?? "Safe";
    }

    // ---- the world tick ----

    /// <summary>
    /// One world tick (after the world events): every map's population pass when due and its linger, then each bot's walk and, when due and
    /// a human is near, its brain.
    /// </summary>
    public void Update(TimeSpan now)
    {
        foreach (var gone in _maps.Keys.Where(m => m.Unloaded).ToList()) Unloaded(gone); // a map dropped by another system
        Social.Update(now); // bot-social: delayed answers, city chatter
        var enabled = Enabled;
        if (_transit.Count > 0) Travel(now); // bot-travel: the bots between two clusters arrive (or log out on the way)
        foreach (var map in world.Maps.Maps)
        {
            if (map.HumanCount == 0 && map.Bots.Count == 0) continue;
            if (enabled)
            {
                Arrivals(map, now);
                Populate(map, now);
            }
            if (map.Bots.Count == 0) continue;
            foreach (var bot in map.Bots.Values.ToList())
            {
                var info = bot.Bot!;
                if (!enabled && info.Role != BotRole.Companion) continue; // stageN: hired companions walk and think with the world's bots off too
                if (info.Fight.Defeated)
                {
                    if (info.Fight.DespawnAt is { } at && now >= at) Despawn(bot, "defeated", BotRefill.Respawn);
                    continue;
                }
                info.Motion.Step(bot, this);
                if (info.Harvest is not null) ShowStep(bot);
                // On a spell's forced path and 300 ms after it (stageJ pvp-spells SPEC §2.9) the brain waits: a stop or a walk now would cut the path short.
                if (bot.Map != map || now < info.NextThink || SpellMovement.Holds(bot, world.GameNow)) continue;
                // A fighting bot decides every Bots:Combat:TickMs (a swing interval is ~1 s), an idle one every Bots:ThinkSeconds.
                info.NextThink = now + (info.Fight.Active || info.Role == BotRole.Companion ? TimeSpan.FromMilliseconds(Options.CombatTickMs) : Seconds(Options.ThinkSeconds));
                // A corrupted dungeon's invader hunts the owner over the whole map, seen or not (stageL corrupted SPEC §4.5); a hellgate's enemy the party.
                // StageN: a companion thinks wherever its owner is (it is never far from him).
                if (info.Fight.Active || info.Role is BotRole.Invader or BotRole.Hellgate or BotRole.Arena or BotRole.Companion || Active(map, bot)) info.Brain.Think(bot, now);
            }
        }
    }

    /// <summary>The humans of <paramref name="map"/> the bots see there for the first time noted with <paramref name="now"/>, the gone ones forgotten.</summary>
    private void Arrivals(Map map, TimeSpan now)
    {
        var arrived = StateOf(map).Arrived;
        foreach (var id in map.Players.Keys) arrived.TryAdd(id, now);
        if (arrived.Count > map.HumanCount)
            foreach (var gone in arrived.Keys.Where(id => !map.Players.ContainsKey(id)).ToList()) arrived.Remove(gone);
    }

    /// <summary>Whether <paramref name="p"/> has been on <paramref name="map"/> for Bots:Patrol:JoinGraceSeconds: a patrol may attack him on sight.</summary>
    public bool Settled(Map map, Player p) => Options.JoinGraceSeconds <= 0
        || (_maps.GetValueOrDefault(map)?.Arrived.TryGetValue(p.Id, out var at) == true && world.Clock.Now - at >= TimeSpan.FromSeconds(Options.JoinGraceSeconds));

    /// <summary>A bot thinks while a human sees it or is within Bots:ActiveRange; else it finishes its leg and stands (SPEC §1.4).</summary>
    private bool Active(Map map, Player bot)
    {
        if (bot.Observers.Count > 0) return true;
        var at = bot.PositionAt(Time);
        var utc = DateTime.UtcNow;
        foreach (var human in map.Players.Values)
            if (Vector2.Distance(human.PositionAt(utc), at) <= Options.ActiveRange) return true;
        return false;
    }

    /// <summary>The population pass of <paramref name="map"/> when due, and its bots' leave Bots:LingerSeconds after the last human did.</summary>
    private void Populate(Map map, TimeSpan now)
    {
        if (map.HumanCount == 0)
        {
            // A live hellgate's enemy team stays while its party is out (a relog, .hg leave): the instance's closing takes it (stageL hellgates).
            if (map.EmptySince is { } since && now - since >= TimeSpan.FromSeconds(Options.LingerSeconds) && !world.Hellgates.IsLive(map.Cluster))
                Clear(map, $"no players for {Options.LingerSeconds:0} s", BotRefill.Now);
            return;
        }
        // Bots:World:Enabled off: no world population (the content bots and a GM's .bot spawn are not made here).
        if (!Options.WorldEnabled) return;
        var state = StateOf(map);
        if (now < state.NextPass) return;
        state.NextPass = now + TimeSpan.FromSeconds(Options.PopulateSeconds);
        if (ZoneOf(map) is not { } zone || !Options.ZoneAllowed(zone, map.Cluster)) return; // Bots:Zones
        // StageL mists (Mists:DrifterNeedsRed): a private zone's Drifters go once none of its players is Red (out of a fight: off at the next pass).
        if (zone == "Mists" && map.Bots.Count > 0 && !DriftersWanted(map))
            foreach (var drifter in map.Bots.Values.Where(b => b.Bot is { Role: BotRole.Drifter, Fight.Active: false }).ToList())
                Despawn(drifter, "no Red player in its private zone any more", BotRefill.Now);
        var counts = CountsFor(map, zone);
        // No walk grid is built for a map whose zone gets none of the populated roles (phase 1: the open world's gatherers and patrols).
        if (!counts.Any(c => c.Value > 0 && PopulatedRoles.Contains(c.Key)) || NavOf(map) is not { Usable: true } nav) return;
        state.Slots ??= BuildSlots(map, counts, zone, now);
        int Target(BotRole role) => TargetOf(map, counts, role);
        Leaving(state.Slots, Target, now);
        var spawned = 0;
        foreach (var slot in state.Slots)
        {
            if (slot.Bot is not null || now < slot.ReadyAt || slot.Index >= Target(slot.Role)) continue;
            if (slot.Role == BotRole.Drifter && !DriftersWanted(map)) continue; // stageL mists: Mists:DrifterNeedsRed
            if (spawned >= Options.SpawnPerPass || map.Bots.Count >= Options.MaxPerMap || _total >= Options.MaxTotal) break;
            var mapTier = map.Template!.Name.Tier ?? 0;
            var tier = Factory.TierFor(zone, mapTier, Random);
            // Bot-roster: the slot's identity (kept over a respawn), else a free one of its kind with its home tier nearest; it wears its home
            // tier where the zone has that tier. None free (or the roster off): a fresh name as before.
            BotIdentity? identity = null;
            if (Roster is { } roster)
            {
                if (slot.Identity is { } held && (held >= roster.All.Count || Taken(roster.All[held].Name))) Release(slot);
                identity = slot.Identity is { } keep ? roster.All[keep] : roster.Pick(slot.Role, tier, IdentityFree, Random, slot.LastIdentity);
                if (identity is not null && TierAllowed(zone, mapTier, identity.Tier)) tier = identity.Tier;
            }
            // StageN botpower: the patrols of red and black zones (the roads too) one tier above (Bots:Gear:RedZoneTierBonus).
            if (slot.Role == BotRole.Patrol && RedOrBlack(zone)) tier = Math.Clamp(tier + Options.RedZoneTierBonus, 1, 8);
            GatherProfile? profile = null;
            if (slot.Role == BotRole.Gatherer && (profile = ProfileFor(map, tier)) is null)
            {
                slot.ReadyAt = TimeSpan.MaxValue; // no harvestable of a kind it can gather on this map: the slot stays empty
                Log.Debug("bots", $"map {map.Cluster}: no harvestable for a gatherer, slot {slot.PersonaId} left empty");
                continue;
            }
            // A turnover's newcomer comes through one exit (on a road a portal, stageL roads phase B): a new patrol group's route is picked near that one.
            // A patrol whose group came through a portal whose window is still open follows it there (one pass for the group).
            var mates = Patrols(slot.Role) ? state.Groups.GetValueOrDefault(slot.Group) : null;
            var exits = slot.ArriveAtExit ? ExitsOf(map, nav, mates) : [];
            var door = mates is null ? null : exits.FirstOrDefault(e => PortalAt(map, e.Position) is { Slot.Link: { } l } && GroupWindow(mates, l));
            door ??= exits.Count > 0 ? exits[Random.Next(exits.Count)] : null;
            var arrival = door is not null ? (door.Arrival, door.Facing, (BotPoi?)null) : ((Vector2, float, BotPoi?)?)null;
            BotGroup? group = null;
            if (Patrols(slot.Role) && (group = GroupFor(map, nav, state, slot, arrival?.Item1)) is null) continue;
            var spot = arrival ?? (group is not null ? PickStop(map, nav, group) : PickSpot(map, nav, slot.Role, profile));
            if (spot is not { } found) continue;
            var (at, facing, poi) = found;
            if (Spawn(map, slot.Role, profile?.Tier ?? tier, at, facing, slot, poi, profile, group, identity: identity) is null) continue;
            spawned++;
            if (door is not null && PortalAt(map, door.Position) is { Slot.Link: { } link }) PassAs(group, link); // it came through: a charge and the window, or its group's
        }
    }

    /// <summary>
    /// The counts of <paramref name="map"/>'s zone class <paramref name="zone"/> (and its override): Bots:City:Gatherers added in a royal
    /// city, the roles Bots:&lt;Role&gt;:Enabled turns off left out.
    /// </summary>
    private IReadOnlyDictionary<BotRole, int> CountsFor(Map map, string zone)
    {
        var counts = Settings.CountsOf(zone, map.Cluster, map.Template!.Info.Type);
        if (zone == "City" && Options.CityGatherers > 0 && !counts.ContainsKey(BotRole.Gatherer))
            counts = new Dictionary<BotRole, int>(counts) { [BotRole.Gatherer] = Options.CityGatherers }; // Bots:City:Gatherers on the T1 trees
        if (counts.Keys.Any(r => !RoleEnabled(r))) counts = counts.Where(c => RoleEnabled(c.Key)).ToDictionary(); // Bots:<Role>:Enabled
        return counts;
    }

    /// <summary>How many bots of <paramref name="role"/> <paramref name="map"/> has now: its count × the Scale of its humans × the hour's factor.</summary>
    private int TargetOf(Map map, IReadOnlyDictionary<BotRole, int> counts, BotRole role) =>
        (int)Math.Round(counts.GetValueOrDefault(role) * Settings.ScaleOf(map.HumanCount) * ScheduleFactor); // bot-schedule: the hour's factor

    /// <summary>
    /// The slots of a map's zone class (and its override) for the populated roles: per role its count × the largest Scale factor (so that a
    /// factor above 1 has room), at most Bots:MaxPerMap; a pass fills the first round(count × the factor of its humans). Patrols only where the
    /// cluster's rule lets a Red player be fought (PvpAllowed, PvpForced, None: elsewhere a Red bot turns Blue), in groups of
    /// Bots:Patrol:GroupSize slots in index order; a Drifter (stageL mists) is a group of its own.
    /// </summary>
    private List<BotSlot> BuildSlots(Map map, IReadOnlyDictionary<BotRole, int> counts, string zone, TimeSpan now)
    {
        // Room for the largest Scale factor times the curve's highest hour (bot-schedule).
        var most = Math.Max(1.0, Settings.Scale.Values.DefaultIfEmpty(1.0).Max() * Math.Max(1.0, Options.ScheduleHours.DefaultIfEmpty(1.0).Max()));
        var slots = new List<BotSlot>();
        foreach (var (role, count) in counts.OrderBy(c => c.Key))
        {
            if (!PopulatedRoles.Contains(role) || (Patrols(role) && !PatrolsAllowed(map))) continue;
            var n = (int)Math.Ceiling(count * most);
            var (group, left) = (0, 0);
            for (var i = 0; i < n && slots.Count < Options.MaxPerMap; i++)
            {
                if (role == BotRole.Patrol && left-- <= 0) (group, left) = (group + 1, GroupSize(zone) - 1);
                slots.Add(new BotSlot
                {
                    Role = role, Index = i, PersonaId = $"{map.Cluster}:{role.ToString().ToLowerInvariant()}:{i}", ReadyAt = now,
                    Group = role == BotRole.Patrol ? group : role == BotRole.Drifter ? 1000 + i : 0,
                });
            }
        }
        Log.Debug("bots", $"map {map.Cluster} ({zone}): {slots.Count} bot slot(s)");
        return slots;
    }

    /// <summary>
    /// Bot-schedule: when the hour's curve (or fewer humans' Scale) leaves bots of slots at or above their role's <paramref name="target"/>, up
    /// to Bots:Schedule:LeavePerPass of them (the highest places first; not one fighting, beaten or already leaving) go: one no human sees logs
    /// out at once, a seen one ends its stay now (it walks out through an exit or logs out as at the end of a stay, after its dwell); the slot
    /// stays empty while it is above the target.
    /// </summary>
    private void Leaving(List<BotSlot> slots, Func<BotRole, int> target, TimeSpan now)
    {
        var left = Options.ScheduleLeavePerPass;
        if (left <= 0) return;
        foreach (var slot in slots.OrderByDescending(s => s.Index))
        {
            if (left <= 0) return;
            if (slot.Bot is not { Bot: { } info } bot || slot.Index < target(slot.Role) || info.Fight.Active || info.Fight.Defeated || info.StayUntil <= now) continue;
            left--;
            // Out of every human's view it logs out now (no one would see it walk off; an idle bot far away does not think); else it ends its stay.
            if (bot.Observers.Count == 0) Despawn(bot, "logs off: the hour's population is smaller", BotRefill.Arrival);
            else
            {
                info.StayUntil = now;
                Log.Debug("bots", $"'{bot.Character.Name}' ({slot.PersonaId}) leaves: the hour's population is smaller");
            }
        }
    }

    /// <summary>
    /// StageL mists (SPEC §6.9): whether a mists zone's Drifters may come now: always in a shared zone, in a private one under
    /// Mists:DrifterNeedsRed only while one of its players is Red-flagged (a PvE team never meets an opponent it did not ask for).
    /// </summary>
    public bool DriftersWanted(Map map) =>
        !world.Config.Mists.DrifterNeedsRed || world.Mists.Find(map.Cluster) is not { TeamKey.Length: > 0 } || map.Players.Values.Any(p => p.Flag == Pvp.Red);

    /// <summary>Whether patrols may be on <paramref name="map"/>: its rule lets a Red player be fought (PvpAllowed, PvpForced, None).</summary>
    public bool PatrolsAllowed(Map map) => Pvp.RulesOf(world, map).Rule is PvpRule.PvpAllowed or PvpRule.PvpForced or PvpRule.None;

    /// <summary>Whether the zone class <paramref name="zone"/> is a red or black zone, or a road (black) (stageN botpower).</summary>
    public static bool RedOrBlack(string zone) => zone is "Red" or "Black" or "Roads";

    /// <summary>
    /// A group size of Bots:Patrol:GroupSize; on a road (zone class Roads) of Bots:Roads:PatrolGroupSize (stageL roads SPEC §5.6); in a red or
    /// black zone of Bots:Patrol:RedGroupSize (stageN botpower).
    /// </summary>
    private int GroupSize(string zone)
    {
        var (min, max) = zone == "Roads" ? Options.RoadsPatrolGroupSize : zone is "Red" or "Black" ? Options.RedGroupSize : Options.GroupSize;
        return Math.Max(1, (int)Math.Round(min + Random.NextDouble() * (max - min)));
    }

    /// <summary>
    /// The group a patrol of <paramref name="slot"/> walks with: its group's while a member of it is on the map and its stay is not over (the
    /// newcomer walks to the leader), else a new one on a route of the map (<see cref="PatrolRoute.Pick"/>, near <paramref name="near"/>, the
    /// exit it arrives at after a turnover): the members of a leaving group go on their way out alone. Null when the map has no route (the
    /// slot tries again a minute later).
    /// </summary>
    private BotGroup? GroupFor(Map map, ClusterNav nav, MapState state, BotSlot slot, Vector2? near)
    {
        if (state.Groups.GetValueOrDefault(slot.Group) is { Members.Count: > 0 } live && live.StayUntil > world.Clock.Now) return live;
        if (PatrolRoute.Pick(nav, map.Template!, Random, near, PortalStops(map, nav, near)) is not { } route)
        {
            Log.Debug("bots", $"map {map.Cluster}: no patrol route, slot {slot.PersonaId} waits");
            slot.ReadyAt = world.Clock.Now + TimeSpan.FromMinutes(1);
            return null;
        }
        var group = state.Groups[slot.Group] = new BotGroup(route);
        // Bot support roles (Bots:Support:Enabled): a patrol group's makeup of its zone class by weight (a Drifter roams alone, a dps).
        if (Options.SupportEnabled && slot.Role == BotRole.Patrol && PatrolMakeup.Pick(Options.MakeupsOf(ZoneOf(map)), Random) is { } makeup)
            group.Makeup = makeup.Parts;
        return group;
    }

    /// <summary>A stop of <paramref name="group"/>'s route out of every human's view (a new group's start; a newcomer walks to its leader from there).</summary>
    private (Vector2, float, BotPoi?)? PickStop(Map map, ClusterNav nav, BotGroup group)
    {
        foreach (var stop in group.Route.Stops.OrderBy(_ => Random.Next()))
        {
            var at = group.Members.Count == 0 ? stop : nav.Grid.RandomNear(Random, stop, 1, 3) ?? stop;
            if (OutOfView(map, at)) return (at, (float)(Random.NextDouble() * 360), null);
        }
        return null;
    }

    // ---- the portals of a road (stageL roads phase B, SPEC §7.2) ----

    /// <summary>
    /// The exits a bot of <paramref name="map"/> may come or go through: the cluster's (<see cref="ClusterNav.Exits"/>), and on a road (zone class
    /// Roads, Bots:Roads:Portals) every portal whose link a bot may use now (<see cref="AvalonRoads.BotMayUse"/>), its gate the walkable cell
    /// nearest to the portal within 4 m (the client uses one within 5 m) and its arrival the point in front of it where one coming through stands, facing away.
    /// A member of <paramref name="group"/> may also take a portal inside the window its group opened (<see cref="PassAs"/>).
    /// </summary>
    public IReadOnlyList<BotExit> ExitsOf(Map map, ClusterNav nav, BotGroup? group = null)
    {
        if (!Options.RoadsPortals || ZoneOf(map) != "Roads") return nav.Exits;
        var now = world.GameNow;
        var exits = new List<BotExit>(nav.Exits);
        foreach (var p in map.Objects.Values.OfType<TunnelExit>())
            if (p.Slot.Link is { } link && MayPass(group, link, now) && nav.Grid.Nearest(p.Position, 4) is { } gate)
            {
                var at = nav.Grid.Nearest(TunnelExit.ArrivalAt(map.Cluster, p.Position, p.Direction, p.SpawnPos).Position, 6) ?? gate;
                exits.Add(new BotExit(p.Position, at, Transfers.Facing(p.Position, at), gate));
            }
        return exits;
    }

    /// <summary>The portal of <paramref name="map"/> standing at <paramref name="position"/> (an exit of <see cref="ExitsOf"/>); null for a plain exit.</summary>
    private static TunnelExit? PortalAt(Map map, Vector2 position) =>
        map.Objects.Values.OfType<TunnelExit>().FirstOrDefault(p => Vector2.DistanceSquared(p.Position, position) < 0.01f);

    /// <summary>
    /// <paramref name="bot"/> at the exit <paramref name="exit"/> it walked to is gone through it (<see cref="BotRefill.Arrival"/>); through a portal
    /// the pass takes its charge and opens the "recently used" window players see. False when that portal may not be used any more (its link
    /// closed, its charges spent meanwhile): the brain picks another way out. A member of <paramref name="group"/> follows the others free.
    /// </summary>
    public bool LeaveThrough(Player bot, Vector2 exit, BotGroup? group = null)
    {
        if (bot.Map is { } map && PortalAt(map, exit) is { } portal)
        {
            if (portal.Slot.Link is not { } link || !MayPass(group, link, world.GameNow)) return false;
            PassAs(group, link);
            Despawn(bot, $"left through the portal {portal.Slot}", BotRefill.Arrival);
            return true;
        }
        if (!Depart(bot, exit)) Despawn(bot, "left through an exit", BotRefill.Arrival); // bot-travel: next door, else gone as before
        return true;
    }

    /// <summary>Whether <paramref name="group"/>'s window on <paramref name="link"/> (the pass of one of its members) is still open.</summary>
    private bool GroupWindow(BotGroup? group, RoadLink link) => group is not null && group.Through.GetValueOrDefault(link) > world.GameNow;

    /// <summary>Whether a bot of <paramref name="group"/> may pass through <paramref name="link"/>: the bot rule, or inside its group's window.</summary>
    private bool MayPass(BotGroup? group, RoadLink link, long now) => AvalonRoads.BotMayUse(link, now) || group is not null && group.Through.GetValueOrDefault(link) > now;

    /// <summary>
    /// A bot of <paramref name="group"/> passes through <paramref name="link"/>: a gank group passes as one, as a party following inside the
    /// recently-used window (SPEC §5.4, §7.2): the first member's pass takes the charge and opens the window, the others inside it go free.
    /// </summary>
    private void PassAs(BotGroup? group, RoadLink link)
    {
        if (GroupWindow(group, link)) return;
        world.Roads.Pass(link, world.GameNow);
        if (group is not null) group.Through[link] = link.RecentUntil;
    }

    /// <summary>The gates of a road's portals within 400 m of <paramref name="near"/> (anywhere without it) for a patrol's beat (Bots:Roads:Portals); empty elsewhere.</summary>
    public IReadOnlyList<Vector2> PortalStops(Map map, ClusterNav nav, Vector2? near)
    {
        if (!Options.RoadsPortals || ZoneOf(map) != "Roads") return [];
        return [.. map.Objects.Values.OfType<TunnelExit>().Select(p => nav.Grid.Nearest(p.Position, 4)).OfType<Vector2>()
            .Where(g => near is not { } n || Vector2.Distance(g, n) <= 400)];
    }

    /// <summary>
    /// A spawn spot out of every human's view (SPEC §1.4, D7): a city bot at a POI it may dwell at (70 %) or a lattice node, facing the POI
    /// or anywhere; a gatherer on walkable ground within 10 m of a node of its <paramref name="profile"/>; null after 16 tries (try again at
    /// the next pass).
    /// </summary>
    private (Vector2, float, BotPoi?)? PickSpot(Map map, ClusterNav nav, BotRole role, GatherProfile? profile = null)
    {
        var nodes = profile is null ? [] : NodesOf(map).Where(profile.Takes).ToList();
        for (var i = 0; i < 16; i++)
        {
            (Vector2 At, float Facing, BotPoi? Poi)? c = role == BotRole.Gatherer && nodes.Count > 0
                ? nav.Grid.Nearest(nodes[Random.Next(nodes.Count)].Position + new Vector2(Random.Next(-6, 7), Random.Next(-6, 7)), 4) is { } near
                    ? (near, (float)(Random.NextDouble() * 360), null) : null
                : role == BotRole.City && Random.NextDouble() < 0.7 && PickPoi(map, nav, null) is { } poi
                ? (poi.At, poi.Facing, poi)
                : nav.Roads.RandomNode(Random) is { } node ? (node, (float)(Random.NextDouble() * 360), null) : null;
            if (c is { } spot && OutOfView(map, spot.At)) return spot;
        }
        return null;
    }

    /// <summary>Whether <paramref name="p"/>'s cell is more than Bots:MinSpawnCells cells (Chebyshev) from every human's cell on <paramref name="map"/>.</summary>
    public bool OutOfView(Map map, Vector2 p)
    {
        if (map.Grid is not { } grid) return true;
        var cells = Options.SpawnCells(world.Config.Visibility);
        var cell = grid.CellOf(p);
        var utc = DateTime.UtcNow;
        foreach (var human in map.Players.Values)
            if (CellGrid.Distance(cell, human.Visibility.PlayerCell ?? grid.CellOf(human.PositionAt(utc))) <= cells) return false;
        return true;
    }

    /// <summary>
    /// A POI of <paramref name="map"/> by weight for a city bot: not <paramref name="except"/>, not one Bots:City:PerPoi bots dwell at or walk
    /// to, not one no path was found to; null when none is left.
    /// </summary>
    public BotPoi? PickPoi(Map map, ClusterNav nav, BotPoi? except)
    {
        var state = StateOf(map);
        var busy = map.Bots.Values.Select(b => (b.Bot!.Brain as CityBrain)?.Poi).OfType<BotPoi>().GroupBy(p => p).ToDictionary(g => g.Key, g => g.Count());
        var open = nav.Pois.Where(p => p != except && !state.Unreachable.Contains(p) && busy.GetValueOrDefault(p) < Options.CityPerPoi).ToList();
        if (open.Count == 0) return null;
        var roll = Random.NextDouble() * open.Sum(p => p.Weight);
        foreach (var p in open)
            if ((roll -= p.Weight) < 0) return p;
        return open[^1];
    }

    /// <summary>Remembers <paramref name="poi"/> as one no path leads to on <paramref name="map"/> (its bots pick others).</summary>
    public void Unreachable(Map map, BotPoi poi) => StateOf(map).Unreachable.Add(poi);

    /// <summary>How many POIs of <paramref name="map"/> no path was found to (.bot).</summary>
    public int UnreachableOf(Map map) => _maps.GetValueOrDefault(map)?.Unreachable.Count ?? 0;

    /// <summary>A path on <paramref name="nav"/> (<see cref="RoadMap.Path"/> with Bots:Nav:MaxExpansions); a slow one logged at Debug.</summary>
    public IReadOnlyList<Vector2>? FindPath(ClusterNav nav, Vector2 from, Vector2 to)
    {
        var watch = Stopwatch.StartNew();
        var path = nav.Roads.Path(from, to, Options.MaxExpansions);
        if (watch.Elapsed.TotalMilliseconds > Options.SlowPathMs)
            Log.Debug("bots", string.Create(CultureInfo.InvariantCulture, $"{nav.Cluster}: path {from} → {to} took {watch.Elapsed.TotalMilliseconds:0.0} ms ({(path is null ? "none" : $"{path.Count - 1} legs")})"));
        return path;
    }

    private TimeSpan Seconds((double Min, double Max) range) => TimeSpan.FromSeconds(range.Min + Random.NextDouble() * (range.Max - range.Min));

    /// <summary>A whole number of <paramref name="range"/> (min..max, both included; stageN botpower).</summary>
    private int Roll((double Min, double Max) range) => Roll(range, Random);

    private static int Roll((double Min, double Max) range, Random random)
    {
        var (min, max) = ((int)Math.Round(range.Min), (int)Math.Round(range.Max));
        return max <= min ? min : min + random.Next(max - min + 1);
    }

    // ---- what gatherers gather ----

    /// <summary>
    /// The harvestables of <paramref name="map"/> a gatherer may take (bots-world SPEC §2.5): every one of its objects (the template's static
    /// trees and rocks and the placed resource nodes) whose resource has a tool in content/bots.json Gear.Tools, no corpse and none of the
    /// critter, guardian and treasure types; read once per map.
    /// </summary>
    public IReadOnlyList<Harvestable> NodesOf(Map map)
    {
        var state = StateOf(map);
        return state.Nodes ??= [.. map.Objects.Values.OfType<Harvestable>().Where(h => h.Corpse is null && h.Type.Resource is { } r && Settings.Tools.ContainsKey(r)
            && !NotGathered.Any(x => h.Type.Name.Contains(x, StringComparison.OrdinalIgnoreCase)))];
    }

    /// <summary>
    /// What a gatherer of gear tier <paramref name="tier"/> gathers on <paramref name="map"/> (bots-world SPEC §2.5): a resource kind with nodes
    /// of the tiers <paramref name="tier"/> − 2 .. <paramref name="tier"/> (weighted by their count), the band clipped to the tiers present;
    /// with none in its band the bot takes the tier present nearest to its own (lower first: a royal city's T1 trees make a T1 gatherer);
    /// null when the map has no node a gatherer takes.
    /// </summary>
    public GatherProfile? ProfileFor(Map map, int tier, string? prefer = null)
    {
        var nodes = NodesOf(map);
        if (nodes.Count == 0) return null;
        var present = nodes.Select(n => n.Tier).Distinct().ToList();
        if (!present.Any(t => t >= tier - 2 && t <= tier))
            tier = present.OrderBy(t => Math.Abs(t - tier)).ThenBy(t => t).First();
        var band = nodes.Where(n => n.Tier >= tier - 2 && n.Tier <= tier).ToList();
        var pick = band[Random.Next(band.Count)]; // a kind by its share of the band's nodes
        // Bot-travel: a gatherer that came from another cluster keeps its kind where the band has it.
        if (prefer is not null && band.FirstOrDefault(n => string.Equals(n.Type.Resource, prefer, StringComparison.OrdinalIgnoreCase)) is { } same) pick = same;
        var tiers = band.Where(n => n.Type.Resource == pick.Type.Resource).Select(n => n.Tier).ToList();
        return new GatherProfile(pick.Type.Resource!.ToUpperInvariant(), Math.Clamp(tier, 1, 8), tiers.Min(), tiers.Max());
    }

    /// <summary>
    /// A gatherer's flag (SPEC §1.8, D4) on <paramref name="map"/>: Bots:Gatherer:Flag Zone = Red in a red or black zone (warning type) with
    /// Bots:Gatherer:RedShare, else Blue, but Blue on a road (zone class Roads, stageL roads SPEC §5.6: its gankers are the patrols); or always
    /// Blue / Red.
    /// </summary>
    private byte GathererFlag(Map map) => Options.GathererFlag switch
    {
        BotGathererFlag.Blue => Pvp.Blue,
        BotGathererFlag.Red => Pvp.Red,
        _ when ZoneOf(map) == "Roads" => Pvp.Blue,
        _ => Pvp.RulesOf(world, map).WarningType is { } w && (w.Equals("red", StringComparison.OrdinalIgnoreCase) || w.Equals("black", StringComparison.OrdinalIgnoreCase))
            && Random.NextDouble() < Options.GathererRedShare ? Pvp.Red : Pvp.Blue,
    };

    // ---- spawn and despawn ----

    /// <summary>
    /// A new bot of <paramref name="role"/> with gear of <paramref name="tier"/> at <paramref name="at"/> on <paramref name="map"/> (SPEC §1.2):
    /// the character, the session on <see cref="BotPeer"/>, the gear, its flag as the zone keeps it, onto the map with full bars, then into the
    /// view of the humans around (NewCharacter). <paramref name="slot"/> null = a GM's bot (persona "gm:&lt;id&gt;", no stay, never respawns).
    /// A city bot dwells at <paramref name="poi"/> first; a gatherer gathers by <paramref name="profile"/> (null: <see cref="ProfileFor"/> its
    /// tier, which may move its tier to the nodes the map has). Null when the map has no template, no free name was found or a gatherer
    /// finds nothing to gather. Bot support roles: a patrol's <paramref name="part"/> (null: its group's next, <see cref="BotGroup.NextPart"/>)
    /// with Bots:Support:Enabled, its weapon and kit by it; one whose weapon did not resolve at its tier (no holy staff below T3) a dps.
    /// </summary>
    public Player? Spawn(Map map, BotRole role, int tier, Vector2 at, float facing, BotSlot? slot = null, BotPoi? poi = null, GatherProfile? profile = null,
        BotGroup? group = null, double healthFactor = 1, double damageFactor = 1, bool? elite = null, BotIdentity? identity = null, PatrolPart? part = null)
    {
        if (map.Template is null) return null;
        if (role == BotRole.Gatherer && (profile ??= ProfileFor(map, tier)) is null) return null;
        // A patrol walks with a group on a route: a GM's without one gets a new one on the route nearest to it.
        if (Patrols(role) && (group ??= NavOf(map) is { } nav && PatrolRoute.Pick(nav, map.Template, Random, at, PortalStops(map, nav, at)) is { } route ? new BotGroup(route) : null) is null)
            return null;
        if (profile is not null) tier = profile.Tier;
        if (identity is not null && (_held.Contains(identity.Index) && slot?.Identity != identity.Index || Taken(identity.Name))) identity = null;
        var name = identity?.Name ?? (slot?.Name is { } kept && !Taken(kept) ? kept : BotNames.Next(Settings, Random, Taken));
        if (name is null)
        {
            Log.Warn("bots", $"map {map.Cluster}: no free bot name in 50 tries, spawn skipped (more syllables in content/bots.json Names)");
            return null;
        }
        var now = world.Clock.Now;
        var id = world.ObjectIds.Next();
        // Bot-roster gear realism: a population bot's pieces mixed in tier, now and then enchanted or (red and black zones) an artifact, by its
        // identity's own numbers (the same set every time); a GM's, a content bot's and a companion's gear as before.
        var populated = slot is not null && PopulatedRoles.Contains(role);
        var zone = populated ? ZoneOf(map) ?? "Safe" : "";
        var style = populated ? new GearStyle(identity?.GearRandom(role, tier, profile?.Resource) ?? Random, Options.GearTierSpread, Options.GearMixedShare,
            Options.EnchantChance.GetValueOrDefault(zone), Options.GearEnchant, Options.ArtifactChance.GetValueOrDefault(zone)) : null;
        var qualities = identity is not null ? identity.GearRandom(role, tier + 100, profile?.Resource) : Random; // its qualities the same too
        var ownPart = role == BotRole.Patrol && Options.SupportEnabled ? part ?? group?.NextPart() ?? PatrolPart.Dps : PatrolPart.Dps;
        var (character, avatar, gear) = Factory.Make(role, tier, map.Cluster, name, at, facing, Random, profile?.Resource,
            role == BotRole.Gatherer ? GathererFlag(map) : null, identity, style, ownPart);
        if (ownPart != PatrolPart.Dps && !SupportWeapon(gear, ownPart)) ownPart = PatrolPart.Dps;
        var prefer = ownPart switch { PatrolPart.Healer => Companions.Prefer(CompanionKind.Healer), PatrolPart.Tank => Companions.Prefer(CompanionKind.Tank), _ => null };
        var session = WorldSession.ForBot(world);
        var inventory = new Inventory(character.EquipmentContainer, character.InventoryContainer);
        // StageN botpower: a world bot's gear of quality Bots:Gear:Quality; a patrol or Drifter an elite with Bots:Gear:EliteChance (its
        // weapon and armour enchanted where items.xml has the level, of Bots:Gear:EliteQuality).
        var worldBot = role is BotRole.Patrol or BotRole.Drifter or BotRole.Gatherer;
        // A GM's .bot spawn … elite (<paramref name="elite"/> true) forces it, for any role.
        var isElite = elite ?? (Patrols(role) && slot is not null && Random.NextDouble() < Options.EliteChance);
        if (isElite) gear = [.. gear.Select(g => Factory.Enchanted(g, Roll(Options.EliteEnchant)))];
        foreach (var item in gear)
            inventory.Equipment.Put(Array.IndexOf(Inventory.SlotNames, item.Slot),
                session.NewBotItem(item, prefer, quality: !worldBot ? 1 : isElite ? Roll(Options.EliteQuality) : Roll(Options.Quality, qualities)));
        // StageM bots: an armed gatherer's weapon waits in its bag (its tool has no spell slot); BotCombat.Arm swaps it in when attacked.
        if (role == BotRole.Gatherer && Factory.GathererWeapon(tier, Random) is { } weapon && inventory.Bag.FreeSlot() is var free and >= 0)
            inventory.Bag.Put(free, session.NewBotItem(weapon, quality: Roll(Options.Quality)));
        // StageN botpower: healing potions in the potion slot (Bots:Combat:Potions), drunk by BotCombat's tactics when low.
        if (worldBot && Roll(Options.Potions) is var potions and > 0 && Factory.ResolveAny("T{t}_POTION_HEAL", tier) is { Slot: "potion" } potion)
            inventory.Equipment.Put(8, new Item(world.ObjectIds.Next(), potion, potions, 1, potion.FullDurabilityRaw, [], [], null));
        if (Patrols(role) && Options.Food && Factory.ResolveAny("T{t}_MEAL_STEW", tier) is { Slot: "food" } meal)
            inventory.Equipment.Put(9, new Item(world.ObjectIds.Next(), meal, 1, 1, meal.FullDurabilityRaw, [], [], null));
        IBotBrain brain = role switch
        {
            BotRole.Gatherer => new GathererBrain(this, profile!, (int)Math.Round(Options.GathererNodes.Min + Random.NextDouble() * (Options.GathererNodes.Max - Options.GathererNodes.Min))),
            BotRole.Patrol or BotRole.Drifter => new PatrolBrain(this, group!), // stageL mists: a Drifter roams the zone alone

            BotRole.Invader => new InvaderBrain(this, b => b.World.Corrupted.TargetOf(b)),
            BotRole.Hellgate => new InvaderBrain(this, b => b.World.Hellgates.TargetOf(b)), // stageL hellgates: hunts the party
            BotRole.Arena => new InvaderBrain(this, b => b.World.Arenas.TargetOf(b)), // stage N arena: hunts the other team
            _ => new CityBrain(this),
        };
        var stay = now + TimeSpan.FromMinutes(Options.StayMinutes.Min + Random.NextDouble() * (Options.StayMinutes.Max - Options.StayMinutes.Min));
        if (group is not null && slot is not null)
        {
            if (group.StayUntil == TimeSpan.MaxValue) group.StayUntil = stay; // a group leaves together
            stay = group.StayUntil;
        }
        var info = new BotInfo
        {
            Role = role, Tier = tier, PersonaId = slot?.PersonaId ?? $"gm:{id}", Brain = brain, Avatar = avatar, Home = at, Slot = slot, SpawnedAt = now,
            StayUntil = slot is null ? TimeSpan.MaxValue : stay, Group = group, HealthFactor = healthFactor, DamageFactor = damageFactor, Elite = isElite, Part = ownPart,
            NextThink = now + TimeSpan.FromSeconds(Random.NextDouble() * Options.ThinkSeconds.Max),
            Identity = slot is null ? null : identity?.Index,
        };
        if (role == BotRole.Gatherer && slot is not null) // bot-travel: its home city and the end of its first trip
            (info.HomeCity, info.TripUntil) = (HomeCityOf(map.Cluster), now + TimeSpan.FromMinutes(Options.GathererTripMinutes));
        // StageM bots: what a gatherer carries from its spawn (Gear.GathererStartCarry), minted only by its defeat's bag like its harvests.
        if (role == BotRole.Gatherer && Factory.GathererStartCarry(profile!, Random) is { } carry) info.Fight.Carried[carry.Item] = carry.Amount;
        var bot = new Player(session, world, id, character, inventory, GearProgression()) { Bot = info, Facing = facing, MoveStamp = world.GameNow };
        session.AttachBot(bot);
        group?.Members.Add(bot);
        Flagging.Arrive(bot, Pvp.RulesOf(world, map));
        map.AddBot(bot);
        if (character.Mounted) Mounts.Restore(bot);
        bot.RecalculateStats(world.GameNow);
        _live.Add(name);
        _total++;
        if (slot is not null)
        {
            slot.Bot = bot;
            slot.Name = name;
            slot.ArriveAtExit = false;
            if (slot.Identity != identity?.Index) Release(slot);
            if (identity is not null) _held.Add((slot.Identity = identity.Index).Value);
        }
        (brain as CityBrain)?.Begin(poi, now);
        bot.Visibility.Join(map, at);
        bot.Visibility.AnnounceSelf(); // the humans already in range get its NewCharacter now (Join deferred it)
        BotCombat.Eat(this, bot); // stageN botpower: its meal's buff (Bots:Combat:Food)
        Log.Debug("bots", string.Create(CultureInfo.InvariantCulture,
            $"map {map.Cluster}: {role} bot '{name}' T{tier}{(isElite ? " elite" : "")} ({info.PersonaId}{(identity is null ? "" : $", roster #{identity.Index}")}) at ({at.X:0.#}, {at.Y:0.#}), {gear.Count} items, seen by {bot.Observers.Count}"));
        return bot;
    }

    /// <summary>
    /// Takes <paramref name="bot"/> off its map (SPEC §1.2): a knockdown's bag and silver placed where it lay (<see cref="BotRewards.Despawning"/>:
    /// at its timed despawn, and as well when a GM, the linger, a player taking its name or .bot off takes it earlier; not on a map being
    /// unloaded), a looping emote stopped, its toggles, mount and fight dropped, a Leave to every human who knows it, off the map, its name
    /// free; its slot fills again by <paramref name="refill"/>. Nothing is saved.
    /// </summary>
    public void Despawn(Player bot, string why, BotRefill refill)
    {
        if (bot.Map is not { } map || bot.Bot is not { } info) return;
        if (!map.Unloaded) Rewards.Despawning(bot);
        EndShow(bot, cancelled: true);
        StopEmote(bot);
        SpellCasts.EndToggles(bot, SpellCasts.ToggleEnd.Left);
        Mounts.Leave(bot);
        Combat.Leave(bot);
        bot.Visibility.LeavePlayers();
        map.RemoveBot(bot);
        info.Group?.Members.Remove(bot);
        _live.Remove(bot.Character.Name);
        _total--;
        if (info.Role == BotRole.Companion) world.Companions.Gone(bot, why); // stageN: out of its owner's list and party
        if (info.Slot is null && info.Identity is { } away) _away.Remove(away); // bot-travel: a visitor's identity is offline now
        if (info.Slot is { } slot && slot.Bot == bot)
        {
            var now = world.Clock.Now;
            slot.Bot = null;
            (slot.ReadyAt, slot.ArriveAtExit) = refill switch
            {
                BotRefill.Respawn => (now + Seconds(Options.RespawnSeconds), false),
                BotRefill.Arrival => (now + Seconds(Options.ArrivalSeconds), true),
                _ => (now, false),
            };
            if (refill == BotRefill.Arrival)
            {
                slot.Name = null; // another "player" comes next
                Release(slot); // bot-roster: its identity is offline now and may come back later (here or elsewhere)
            }
        }
        Log.Debug("bots", $"map {map.Cluster}: bot '{bot.Character.Name}' ({info.PersonaId}) gone: {why}");
    }

    /// <summary>
    /// Bot support roles: whether <paramref name="gear"/> holds the weapon of <paramref name="part"/> (<see cref="Companions.KindOf"/>'s rule: a
    /// healer a holy or nature staff; a tank a mace, a hammer or a shield).
    /// </summary>
    private static bool SupportWeapon(List<ItemTemplate> gear, PatrolPart part)
    {
        var main = gear.FirstOrDefault(g => g.Slot == "mainhand")?.Name ?? "";
        var off = gear.FirstOrDefault(g => g.Slot == "offhand")?.Name ?? "";
        return part == PatrolPart.Healer ? main.Contains("HOLYSTAFF", StringComparison.Ordinal) || main.Contains("NATURESTAFF", StringComparison.Ordinal)
            : main.Contains("MACE", StringComparison.Ordinal) || main.Contains("HAMMER", StringComparison.Ordinal) || off.Contains("SHIELD", StringComparison.Ordinal);
    }

    /// <summary>Every bot of <paramref name="map"/> despawned (<see cref="Despawn"/>); returns how many.</summary>
    public int Clear(Map map, string why, BotRefill refill)
    {
        var bots = map.Bots.Values.Where(b => !b.IsCompanion).ToList(); // stageN: hired companions go with their owner only (Companions)
        foreach (var bot in bots) Despawn(bot, why, refill);
        if (bots.Count > 0) Log.Info("bots", $"map {map.Cluster}: {bots.Count} bot(s) gone: {why}");
        return bots.Count;
    }

    /// <summary>Every bot in the world despawned; returns how many.</summary>
    public int ClearAll(string why, BotRefill refill) => DropTravellers(why) + world.Maps.Maps.ToList().Sum(m => Clear(m, why, refill));

    /// <summary>The map is unloaded (World's idle unload; a map dropped by another system): its bots gone, its slots forgotten.</summary>
    public void Unloaded(Map map)
    {
        foreach (var bot in map.Bots.Values.ToList()) Despawn(bot, "the map is unloaded", BotRefill.Now);
        if (_maps.GetValueOrDefault(map)?.Slots is { } slots)
            foreach (var slot in slots) Release(slot);
        _maps.Remove(map);
    }

    // ---- companions (stageN companions SPEC §1) ----

    /// <summary>
    /// StageN companions: a new companion bot of <paramref name="tier"/> wearing <paramref name="gear"/> (each item's spells chosen with
    /// <paramref name="prefer"/> first) at <paramref name="at"/> on <paramref name="map"/>, thinking with <paramref name="brain"/>, as a GM's bot
    /// (no slot, no stay): onto the map with full bars, into the view of the humans around. Null when no free name was found.
    /// </summary>
    public Player? SpawnCompanion(Map map, int tier, Vector2 at, float facing, IReadOnlyList<ItemTemplate> gear, Func<SpellTemplate, bool>? prefer,
        IBotBrain brain, CompanionInfo companion)
    {
        if (BotNames.Next(Settings, Random, Taken) is not { } name) return null;
        var now = world.Clock.Now;
        var id = world.ObjectIds.Next();
        var (character, avatar, _) = Factory.Make(BotRole.Companion, tier, map.Cluster, name, at, facing, Random);
        var session = WorldSession.ForBot(world);
        var inventory = new Inventory(character.EquipmentContainer, character.InventoryContainer);
        foreach (var item in gear) inventory.Equipment.Put(Array.IndexOf(Inventory.SlotNames, item.Slot), session.NewBotItem(item, prefer));
        var info = new BotInfo
        {
            Role = BotRole.Companion, Tier = tier, PersonaId = $"companion:{id}", Brain = brain, Avatar = avatar, Home = at, SpawnedAt = now,
            NextThink = now, Companion = companion,
        };
        var bot = new Player(session, world, id, character, inventory, GearProgression(world.Config.Companions.GearMastery)) { Bot = info, Facing = facing, MoveStamp = world.GameNow };
        session.AttachBot(bot);
        Flagging.Arrive(bot, Pvp.RulesOf(world, map));
        map.AddBot(bot);
        bot.RecalculateStats(world.GameNow);
        _live.Add(name);
        _total++;
        bot.Visibility.Join(map, at);
        bot.Visibility.AnnounceSelf();
        Log.Info("bots", string.Create(CultureInfo.InvariantCulture,
            $"map {map.Cluster}: companion '{name}' ({companion.Kind}, T{tier}) of '{companion.OwnerName}' at ({at.X:0.#}, {at.Y:0.#}), {gear.Count} items"));
        return bot;
    }

    /// <summary>
    /// Bot-party: world bot <paramref name="bot"/> (City, Gatherer, Patrol, Drifter) leaves its world role to play as a companion: a gatherer
    /// draws the weapon of its bag (when it has one), its harvest show, emote, mount bar and mount end (the mount object taken away), its
    /// walk stops and its fight is forgotten; it leaves its patrol group and frees its population slot (a newcomer arrives after
    /// Bots:ArrivalSeconds, as at a turnover), so it never stays or respawns as a world bot; a Red one is Blue (ChangeFlaggingFinished).
    /// Then it is a <see cref="BotRole.Companion"/> thinking with <paramref name="brain"/>, its <see cref="BotInfo.Companion"/> made by
    /// <paramref name="companion"/> from its (now armed) gear. Its gear, tier and spells stay its own.
    /// </summary>
    public void Recruit(Player bot, IBotBrain brain, Func<Player, CompanionInfo> companion)
    {
        if (bot.Bot is not { } info) return;
        var ticks = world.GameNow;
        var now = world.Clock.Now;
        if (info.Role == BotRole.Gatherer) BotCombat.Arm(bot, true, ticks);
        EndShow(bot, cancelled: true);
        StopEmote(bot);
        Mounts.Cancel(bot, "joins a party");
        if (bot.IsMounted) Mounts.Dismount(bot);
        Mounts.Leave(bot);
        info.Motion.Stop(bot, bot.Facing, this);
        info.Fight.End();
        info.Fight.Spared.Clear();
        info.Group?.Members.Remove(bot);
        info.Group = null;
        if (info.Slot is { } slot && slot.Bot == bot)
        {
            slot.Bot = null;
            (slot.ReadyAt, slot.ArriveAtExit, slot.Name) = (now + Seconds(Options.ArrivalSeconds), true, null);
            Release(slot); // bot-roster: its identity is online as a party member; once it goes, it may come back
        }
        info.Slot = null;
        info.StayUntil = TimeSpan.MaxValue;
        info.Role = BotRole.Companion;
        info.Brain = brain;
        info.Companion = companion(bot);
        info.NextThink = now;
        if (bot.Flag == Pvp.Red) Flagging.Set(bot, Pvp.Blue);
        if (bot.RecalculateStats(ticks, keep: true)) bot.SendToObservers(Combat.PlayerCombo(bot, ticks)); // Companions:HealthFactor now
        Log.Info("bots", $"'{bot.Character.Name}' ({info.PersonaId}) joins the party of '{info.Companion.OwnerName}' as a {info.Companion.Kind} companion");
    }

    /// <summary>
    /// StageN companions: <paramref name="bot"/> is put at <paramref name="at"/> on <paramref name="map"/>: on its own map a Teleport (event 4) to
    /// its observers; on another one it leaves its map as a despawn does (a Leave to who knows it, its fight, casts and toggles dropped) and joins
    /// <paramref name="map"/> (NewCharacter to the humans around), standing with full bars when it was knocked down.
    /// </summary>
    public void PutCompanion(Player bot, Map map, Vector2 at, float facing)
    {
        if (bot.Bot is not { } info) return;
        info.Motion.Reset();
        if (bot.Map == map)
        {
            bot.Teleport(at, facing, Time);
            return;
        }
        var down = bot.Combat.Down is not null;
        if (bot.Map is { } from)
        {
            StopEmote(bot);
            SpellCasts.EndToggles(bot, SpellCasts.ToggleEnd.Left);
            Combat.Leave(bot);
            bot.Visibility.LeavePlayers();
            from.RemoveBot(bot);
        }
        bot.Combat.InCombat = false;
        bot.Combat.Hit = false;
        info.Fight.End();
        bot.Character.Cluster = map.Cluster;
        (bot.Character.PositionX, bot.Character.PositionY, bot.Character.Direction) = (at.X, at.Y, facing);
        bot.Movement = new MovementState(at, at, 0, Time) { Direction = facing, Stamp = bot.NextMoveStamp() };
        bot.Facing = facing;
        Flagging.Arrive(bot, Pvp.RulesOf(world, map));
        map.AddBot(bot);
        var ticks = world.GameNow;
        if (down)
        {
            bot.Health.Set(bot.Health.Max, ticks);
            bot.Energy.Set(bot.Energy.Max, ticks);
        }
        bot.RecalculateStats(ticks, keep: true);
        bot.Visibility.Join(map, at);
        bot.Visibility.AnnounceSelf();
        Log.Debug("bots", $"companion '{bot.Character.Name}' follows to {map.Cluster}");
    }

    // ---- names ----

    /// <summary>
    /// The saved characters' names, read once at the start (ServerHost, before the world loop runs), so that no spawn reads the repository
    /// inside a tick; kept by <see cref="NameTaken"/>. A world started without it (the self-test) reads them at its first <see cref="Taken"/>.
    /// </summary>
    public void LoadNames() => _characterNames = ReadNames();

    private HashSet<string> ReadNames() => new(world.Characters.List().Select(c => c.Name), StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Whether <paramref name="name"/> is not free for a bot (case ignored): a saved character's (<see cref="LoadNames"/>, then kept by
    /// <see cref="NameTaken"/>), a live bot's or joined human's, or one of content/bots.json ReservedNames.
    /// </summary>
    public bool Taken(string name)
    {
        return (_characterNames ??= ReadNames()).Contains(name) || _live.Contains(name) || Settings.ReservedNames.Contains(name)
            || world.Maps.Maps.Any(m => m.Players.Values.Any(p => p.Character.Name.Equals(name, StringComparison.OrdinalIgnoreCase)));
    }

    /// <summary>A player created a character named <paramref name="name"/> (posted by CharacterListHandler): a bot that carries it goes, the name is taken.</summary>
    public void NameTaken(string name)
    {
        _characterNames?.Add(name);
        DropTravellers("a player created a character of its name", name);
        foreach (var bot in All.Where(b => b.Character.Name.Equals(name, StringComparison.OrdinalIgnoreCase)).ToList())
        {
            if (bot.Bot!.Slot is { } slot)
            {
                slot.Name = null;
                Release(slot);
            }
            Despawn(bot, "a player created a character of its name", BotRefill.Now);
        }
    }

    // ---- fights (BotCombat) ----

    /// <summary>A player's hit on <paramref name="bot"/> (Pvp.Damage, before its damage): it answers (<see cref="BotCombat.Attacked"/>).</summary>
    public void Attacked(Player bot, Player attacker, long ticks) => BotCombat.Attacked(this, bot, attacker, ticks);

    /// <summary><paramref name="attacker"/>'s hit took <paramref name="damage"/> of <paramref name="bot"/>'s health (Pvp.Damage): <see cref="BotCombat.Hurt"/>.</summary>
    public void Hurt(Player bot, Player attacker, double damage, long ticks) => BotCombat.Hurt(bot, attacker, damage, ticks);

    /// <summary>
    /// The hit that took <paramref name="bot"/> to 0 (Pvp.Damage; <paramref name="crime"/> judged before it): its defeat and the killer's
    /// rewards (<see cref="BotCombat.Defeated"/>), its despawn later.
    /// </summary>
    public void Defeated(Player bot, Player killer, bool crime, long ticks)
    {
        var before = bot.Bot?.Fight.Defeated ?? true;
        BotCombat.Defeated(this, bot, killer, crime, ticks);
        if (!before && bot.Bot!.Fight.Defeated) Social.FightEnded(bot, won: false);
    }

    /// <summary><paramref name="bot"/> knocked <paramref name="victim"/> down (Pvp.KnockedDown): <see cref="BotCombat.KnockedDownPlayer"/>.</summary>
    public void KnockedDownPlayer(Player bot, Player victim, long ticks)
    {
        BotCombat.KnockedDownPlayer(this, bot, victim, ticks);
        if (victim.Bot is null) Social.FightEnded(bot, won: true);
    }

    // ---- harvest shows (bots-world SPEC §2.5) ----

    /// <summary>
    /// <paramref name="bot"/> shows <paramref name="repeats"/> harvests of <paramref name="node"/> with its mainhand tool, from now: dismounted
    /// first, then HarvestStart (59) {0 bot, 1 = 2 now, 3 node, 4 type, 5 harvesttimeseconds × the tool's time factor, 6 the tool's object
    /// (never sent: an unknown one, as for every other player's tool), 7 the tool's item id} to its observers that know the node; the world
    /// tick finishes each repeat (<see cref="ShowStep"/>).
    /// </summary>
    public void StartShow(Player bot, Harvestable node, int repeats)
    {
        Mounts.BeforeAction(bot, "harvests");
        var tool = bot.Inventory.Equipment[0];
        Show(bot, new BotHarvest(node, tool, world.GameNow, ShowSeconds(node, tool), repeats - 1));
    }

    /// <summary>The time of one harvest of <paramref name="node"/> with <paramref name="tool"/> (by hand when the tool cannot take it).</summary>
    private static float ShowSeconds(Harvestable node, Item? tool) =>
        Harvesting.Duration(node.TierData, Harvesting.TimeFactor(node.Type, node.TierData, tool?.Template) ?? node.TierData.NoToolTimeFactor);

    private void Show(Player bot, BotHarvest h)
    {
        bot.Bot!.Harvest = h;
        Harvesting.ToWatchers(bot, h.Node, Harvesting.StartEvent(bot, h.Node, h.Tool, h.Start, h.Duration));
    }

    /// <summary>
    /// The world tick of a show: 1 ms after the end of a repeat's bar, HarvestFinished (61) {0 bot, 1 start, 2 the bar's end, 3 node, 4 the
    /// resource's item id, 5 the amount the node's charges give, 6 0, 7 0, 8 the node's charges (unchanged, so that it does not look emptied;
    /// left out at 0), 9 none, 10 an empty byte[]} to its observers that know the node, the amount "carried"; with Bots:ConsumeNodes the
    /// charges taken first as by a player (HarvestableChangeState to all who know the node, the regrowth). The next repeat's HarvestStart
    /// follows at once, stamped at that end, unless the node is empty, gone or a human harvests it: then the show is over.
    /// </summary>
    private void ShowStep(Player bot)
    {
        var info = bot.Bot!;
        if (info.Harvest is not { } h || world.GameNow < h.End + TimeSpan.TicksPerMillisecond) return;
        var node = h.Node;
        var map = bot.Map;
        if (map is null || node.Map != map || node.Charges <= 0 || world.Items.Find(node.TierData.ItemOf(node.RareState) ?? "") is not { } item)
        {
            EndShow(bot, cancelled: true);
            return;
        }
        var tier = node.TierData;
        var amount = Harvesting.Amount(tier, node.Charges);
        if (Options.ConsumeNodes)
        {
            node.Charges -= Harvesting.ChargesTaken(tier, node.Charges);
            Harvesting.StartRegrowth(map, node, world.Clock.Now, world.Rates.Regrowth);
            Harvesting.SendState(map, node);
        }
        Harvesting.ToWatchers(bot, node, new HarvestFinishedEvent
        {
            HarvesterId = bot.Id,                  // [0]
            Start = h.Start,                       // [1]
            Finish = h.End,                        // [2]
            HarvestableId = node.Id,               // [3]
            ItemId = item.Index,                   // [4]
            Amount = (short)Math.Min(short.MaxValue, amount), // [5]
            ChargesLeft = node.Charges,            // [8] its charges as they are (left out at 0)
            BonusLootItems = [],                   // [9]
            Extra = new() { [6] = (short)0, [7] = (short)0, [10] = Array.Empty<byte>() }, // [6] [7] no bonus, [10] the wire's empty byte[]
        });
        info.Fight.Carried[item.Name] = info.Fight.Carried.GetValueOrDefault(item.Name) + amount;
        info.Harvest = null;
        if (h.Repeats > 0 && node.Charges > 0 && !map.Players.Values.Any(p => p.Harvest?.Node == node))
            Show(bot, h with { Start = h.End, Repeats = h.Repeats - 1 });
    }

    /// <summary>A show under way ends; <paramref name="cancelled"/> (attacked, defeated, gone): HarvestCancel (60) {0 bot, 1 start} to its observers that know the node.</summary>
    public void EndShow(Player bot, bool cancelled)
    {
        if (bot.Bot is not { Harvest: { } h } info) return;
        info.Harvest = null;
        if (cancelled) Harvesting.ToWatchers(bot, h.Node, new HarvestCancelEvent { HarvesterId = bot.Id, Start = h.Start });
    }

    // ---- what the brains do ----

    /// <summary><paramref name="bot"/> walks <paramref name="path"/> (a looping emote stopped first), stopping facing <paramref name="facing"/>.</summary>
    public void Walk(Player bot, IReadOnlyList<Vector2> path, float? facing)
    {
        StopEmote(bot);
        bot.Bot!.Motion.Walk(bot, path, facing, this);
    }

    /// <summary>
    /// <paramref name="bot"/> plays emote <paramref name="emote"/> (PlayEmote 77 {0 id, 1 emote, 3 start} to its observers, key 2 left out as
    /// captured); a looping one stays on (<see cref="BotInfo.Emote"/>) and goes to every new observer with its start (<see cref="EmoteFor"/>).
    /// </summary>
    public void Emote(Player bot, int emote)
    {
        StopEmote(bot);
        var e = new BotEmote(emote, world.GameNow, BotSettings.LoopingEmotes.Contains(emote));
        bot.SendToObservers(EmoteEvent(bot, e));
        if (e.Loops) bot.Bot!.Emote = e;
    }

    /// <summary>A looping emote of <paramref name="bot"/> ends: StopEmote 78 {0 id} to its observers.</summary>
    public void StopEmote(Player bot)
    {
        if (bot.Bot is not { Emote: { } e } info) return;
        info.Emote = null;
        if (e.Loops) bot.SendToObservers(new StopEmoteEvent { ddn = bot.Id });
    }

    /// <summary>The PlayEmote of a bot's looping emote for a new observer (its spawn block, after Mounted); null when it shows none.</summary>
    public static AlbionEvent? EmoteFor(Player bot) => bot.Bot is { Emote: { Loops: true } e } ? EmoteEvent(bot, e) : null;

    /// <summary>PlayEmote 77: key 1 through Extra, so that it goes out whatever the index (the serializer drops a 0).</summary>
    private static PlayEmoteEvent EmoteEvent(Player bot, BotEmote e) => new() { ddn = bot.Id, dms = e.Start, Extra = new() { [1] = e.Emote } };
}
