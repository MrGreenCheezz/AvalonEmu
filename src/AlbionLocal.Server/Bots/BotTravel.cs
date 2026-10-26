using System.Globalization;
using System.Numerics;
using AlbionLocal.Photon;
using AlbionLocal.Server.Content;
using AlbionLocal.Server.Data;
using AlbionLocal.Server.Entities;
using AlbionLocal.Server.Systems;

namespace AlbionLocal.Server.Bots;

/// <summary>
/// Bot-travel: a world bot between two clusters (<see cref="BotManager.Travellers"/>): it walked into the exit of <see cref="From"/> and
/// appears at <see cref="ArriveAt"/> (<see cref="World.Clock"/> time) at the entrance <see cref="Entrance"/> (world.xml exit Guid) of
/// <see cref="To"/>. Off every map meanwhile; its name stays taken and its roster identity held.
/// </summary>
public sealed record BotTraveller(Player Bot, string From, string To, Guid Entrance, TimeSpan ArriveAt);

/// <summary>
/// Bot-travel (analysis/work/official-audit/audit-bots.md improvement 6): world bots move between neighbouring clusters instead of
/// vanishing at an exit, and gatherers make trips from their home city and back to its bank.
/// <para>
/// <b>Travel.</b> A City, Gatherer or Patrol bot that walks into a plain exit (not a road portal) with Bots:Travel:Enabled leaves its map
/// as a player taking the exit does (a Leave to who saw it, its slot free for a newcomer after Bots:ArrivalSeconds) and is in transit for
/// Bots:Travel:Seconds; then it appears at the matching entrance of the target cluster (the arrival point of the exit whose world.xml Guid
/// the exit names) as the same Player object: name, Guid, gear, health and bag. The rule for map loading is the population's own: bots
/// live only where a player is. So a bot travels only when, at the moment it takes the exit, the target map is loaded with a player on
/// it, its zone gets world bots (no instance: dungeons, islands, hellgates, arenas, expeditions and mists zones have no zone class;
/// Bots:Zones; Bots:&lt;Role&gt;:Enabled), its walk grid is built and a slot of the bot's role is free there (a gatherer walking home may
/// come as a slotless visitor). Otherwise it is gone at the exit as before (it "logged out"). At its arrival the same is checked again:
/// a map unloaded or emptied meanwhile, or the slot taken, and it logs out on the way. Nothing is stored.
/// </para>
/// <para>
/// <b>Route.</b> A leaving bot prefers an exit to a neighbour that takes it, of a zone tier nearest its own (<see cref="ExitOrder"/>);
/// a gatherer walking home takes the exit to the next cluster of the shortest way over world.xml's exits (at most Bots:Travel:MaxHops,
/// never through an instance, a building interior, a road or a cluster Bots:Zones leaves out).
/// </para>
/// <para>
/// <b>Trips.</b> A population gatherer's home city is the nearest royal city or rest (world.xml PLAYERCITY_* with a bank: not an interior,
/// a hall, a portal town or the Hell Den) within Bots:Travel:MaxHops of where it came online. When its bag is full (Bots:Gatherer:Nodes
/// nodes) or Bots:Gatherer:TripMinutes are over and it carries something, with Bots:Gatherer:BankAtHome it walks home cluster by cluster,
/// in its home city to the bank door (the *_BANK_ENTRANCE exit tile), pauses there and its gathered resources are banked: gone, in that
/// city only (no global storage). Then a new trip begins: a visitor walks out again, one with a city slot gathers on.
/// </para>
/// </summary>
public sealed partial class BotManager
{
    private readonly List<BotTraveller> _transit = [];

    /// <summary>The roster identities held off a slot: travelling, or visiting a map without a slot (<see cref="IdentityFree"/>).</summary>
    private readonly HashSet<int> _away = [];

    /// <summary>A cluster's home city (<see cref="HomeCityOf"/>), found once per cluster.</summary>
    private readonly Dictionary<string, string?> _homes = new(StringComparer.Ordinal);

    /// <summary>The bots between two clusters now.</summary>
    public IReadOnlyList<BotTraveller> Travellers => _transit;

    /// <summary>Bots:Travel:Enabled under Bots:Enabled and Bots:World:Enabled.</summary>
    public bool TravelEnabled => WorldEnabled && Options.TravelEnabled;

    /// <summary>The roles that travel: City, Gatherer, Patrol (a Drifter lives in a mists zone, an instance; the content bots never).</summary>
    public static bool Travels(BotRole role) => role is BotRole.City or BotRole.Gatherer or BotRole.Patrol;

    /// <summary>The world.xml exit of <paramref name="map"/> whose trigger stands at <paramref name="position"/> (a <see cref="BotExit.Position"/>).</summary>
    private static WorldExit? WorldExitAt(Map map, Vector2 position) =>
        map.Template?.Exits.FirstOrDefault(e => e.World is not null && Vector2.DistanceSquared(e.Position, position) < 0.01f)?.World;

    /// <summary>
    /// Whether <paramref name="target"/> takes a travelling bot of <paramref name="role"/> now (see the class): loaded with a player, a zone
    /// with world bots, its walk grid, room under Bots:MaxPerMap and a free slot of the role (a gatherer walking home,
    /// <paramref name="homing"/>, without one too). The walk grid and the free slot (null for a visitor); null = it does not.
    /// </summary>
    private (ClusterNav Nav, BotSlot? Slot)? Accept(Map target, BotRole role, bool homing)
    {
        if (target.Unloaded || target.HumanCount == 0 || target.Template is null || !RoleEnabled(role)) return null;
        if (ZoneOf(target) is not { } zone || !Options.ZoneAllowed(zone, target.Cluster)) return null;
        if (target.Bots.Count >= Options.MaxPerMap || NavOf(target) is not { Usable: true } nav) return null;
        var slot = FreeSlot(target, zone, role);
        return slot is not null || homing && role == BotRole.Gatherer ? (nav, slot) : null;
    }

    /// <summary>A slot of <paramref name="role"/> on <paramref name="map"/> without a bot, within the role's count now and not left empty for good.</summary>
    private BotSlot? FreeSlot(Map map, string zone, BotRole role)
    {
        if (_maps.GetValueOrDefault(map)?.Slots is not { } slots) return null;
        var target = TargetOf(map, CountsFor(map, zone), role);
        return slots.FirstOrDefault(s => s.Role == role && s.Bot is null && s.ReadyAt != TimeSpan.MaxValue && s.Index < target);
    }

    /// <summary>
    /// <paramref name="bot"/> at the exit at <paramref name="exitPosition"/> sets off to the neighbour it leads to (see the class); false when
    /// it does not travel (the caller takes it away as before).
    /// </summary>
    private bool Depart(Player bot, Vector2 exitPosition)
    {
        if (!TravelEnabled || bot.Map is not { } map || bot.Bot is not { } info || !Travels(info.Role) || info.Fight.Defeated) return false;
        if (WorldExitAt(map, exitPosition) is not { } exit || world.Maps.Find(exit.TargetCluster) is not { } target || target == map) return false;
        if (Accept(target, info.Role, info.Homing) is null) return false;
        var now = world.Clock.Now;
        EndShow(bot, cancelled: true);
        StopEmote(bot);
        SpellCasts.EndToggles(bot, SpellCasts.ToggleEnd.Left);
        Mounts.Leave(bot);
        Combat.Leave(bot);
        info.Motion.Reset();
        bot.Visibility.LeavePlayers();
        map.RemoveBot(bot);
        info.Group?.Members.Remove(bot);
        info.Group = null;
        if (info.Slot is { } slot && slot.Bot == bot)
        {
            slot.Bot = null;
            (slot.ReadyAt, slot.ArriveAtExit, slot.Name) = (now + Seconds(Options.ArrivalSeconds), true, null); // another "player" comes next
            Release(slot);
        }
        info.Slot = null;
        if (info.Identity is { } id) _away.Add(id); // held while it travels: no slot takes it meanwhile
        var arrive = now + Seconds(Options.TravelSeconds);
        _transit.Add(new BotTraveller(bot, map.Cluster, target.Cluster, exit.Target, arrive));
        Log.Debug("bots", string.Create(CultureInfo.InvariantCulture,
            $"map {map.Cluster}: bot '{bot.Character.Name}' ({info.PersonaId}) travels to {target.Cluster}{(info.Homing ? $" on its way home to {info.HomeCity}" : "")}, there in {(arrive - now).TotalSeconds:0.#} s"));
        return true;
    }

    /// <summary>The travellers due now arrive, or log out on the way when their target no longer takes them.</summary>
    private void Travel(TimeSpan now)
    {
        foreach (var t in _transit.Where(t => now >= t.ArriveAt).ToList())
        {
            _transit.Remove(t);
            if (!Arrive(t, now)) Drop(t, $"logged out on the way to {t.To} (the map no longer takes it)");
        }
    }

    /// <summary>
    /// <paramref name="t"/> arrives on its target map (see the class): at the entrance, in its slot (or none, a visitor), with a new stay and
    /// brain for the map (a gatherer keeps its kind where it can, its nodes and trip; a patrol joins its slot's group or starts one near the
    /// entrance), then into the view of the humans around. False when the map does not take it now.
    /// </summary>
    private bool Arrive(BotTraveller t, TimeSpan now)
    {
        var bot = t.Bot;
        var info = bot.Bot!;
        if (!TravelEnabled || world.Maps.Find(t.To) is not { } map || Accept(map, info.Role, info.Homing) is not { } accepted) return false;
        var (nav, slot) = accepted;
        if (Entrance(map, nav, t.Entrance) is not { } door) return false;
        var (at, facing) = door;
        var stay = now + TimeSpan.FromMinutes(Options.StayMinutes.Min + Random.NextDouble() * (Options.StayMinutes.Max - Options.StayMinutes.Min));
        IBotBrain brain;
        BotGroup? group = null;
        switch (info.Role)
        {
            case BotRole.Gatherer:
                var old = info.Brain as GathererBrain;
                if ((ProfileFor(map, info.Tier, old?.Profile.Resource) ?? old?.Profile) is not { } profile) return false;
                var nodes = old?.Nodes ?? (int)Math.Round(Options.GathererNodes.Min + Random.NextDouble() * (Options.GathererNodes.Max - Options.GathererNodes.Min));
                brain = new GathererBrain(this, profile, nodes, old?.Done ?? 0);
                break;
            case BotRole.Patrol:
                if (slot is null || (group = GroupFor(map, nav, StateOf(map), slot, at)) is null) return false;
                if (group.StayUntil == TimeSpan.MaxValue) group.StayUntil = stay; // a group leaves together
                stay = group.StayUntil;
                brain = new PatrolBrain(this, group);
                break;
            default:
                brain = new CityBrain(this);
                break;
        }
        info.Brain = brain;
        info.Group = group;
        group?.Members.Add(bot);
        info.Slot = slot;
        info.PersonaId = slot?.PersonaId ?? $"{map.Cluster}:visitor:{bot.Id}";
        (info.StayUntil, info.Home, info.NextThink) = (stay, at, now + TimeSpan.FromSeconds(Random.NextDouble() * Options.ThinkSeconds.Max));
        bot.Combat.InCombat = false;
        bot.Combat.Hit = false;
        info.Fight.End();
        bot.Character.Cluster = map.Cluster;
        (bot.Character.PositionX, bot.Character.PositionY, bot.Character.Direction) = (at.X, at.Y, facing);
        bot.Movement = new MovementState(at, at, 0, Time) { Direction = facing, Stamp = bot.NextMoveStamp() };
        bot.Facing = facing;
        Flagging.Arrive(bot, Pvp.RulesOf(world, map));
        map.AddBot(bot);
        bot.RecalculateStats(world.GameNow, keep: true);
        if (slot is not null)
        {
            Release(slot); // a slot that waited for its respawn gives that identity up
            (slot.Bot, slot.Name, slot.ArriveAtExit) = (bot, bot.Character.Name, false);
            if (info.Identity is { } id)
            {
                _away.Remove(id);
                _held.Add((slot.Identity = id).Value);
            }
        }
        (brain as CityBrain)?.Begin(null, now);
        bot.Visibility.Join(map, at);
        bot.Visibility.AnnounceSelf();
        Log.Debug("bots", string.Create(CultureInfo.InvariantCulture,
            $"map {map.Cluster}: bot '{bot.Character.Name}' ({info.PersonaId}) arrives from {t.From} at ({at.X:0.#}, {at.Y:0.#}), seen by {bot.Observers.Count}"));
        return true;
    }

    /// <summary>
    /// Where one coming through the exit of world.xml Guid <paramref name="entrance"/> stands on <paramref name="map"/> and faces: that exit's
    /// <see cref="BotExit.Arrival"/>, else the walkable cell nearest to its spawn position; null when the map has no such exit.
    /// </summary>
    private static (Vector2 At, float Facing)? Entrance(Map map, ClusterNav nav, Guid entrance)
    {
        if (map.Template!.Exits.FirstOrDefault(e => e.Guid == entrance) is not { } spawn) return null;
        if (nav.Exits.FirstOrDefault(x => Vector2.DistanceSquared(x.Position, spawn.Position) < 0.01f) is { } exit) return (exit.Arrival, exit.Facing);
        return spawn.SpawnPosition is { } p && nav.Grid.Nearest(p, 6) is { } at ? (at, Transfers.Facing(spawn.Position, at)) : null;
    }

    /// <summary>A traveller that did not arrive is gone: its name and identity free.</summary>
    private void Drop(BotTraveller t, string why)
    {
        var bot = t.Bot;
        _live.Remove(bot.Character.Name);
        _total--;
        if (bot.Bot?.Identity is { } id) _away.Remove(id);
        Log.Debug("bots", $"bot '{bot.Character.Name}' ({t.From} → {t.To}) gone: {why}");
    }

    /// <summary>Every traveller (of name <paramref name="name"/> when given) gone (<see cref="Drop"/>); returns how many.</summary>
    private int DropTravellers(string why, string? name = null)
    {
        var gone = _transit.Where(t => name is null || t.Bot.Character.Name.Equals(name, StringComparison.OrdinalIgnoreCase)).ToList();
        foreach (var t in gone)
        {
            _transit.Remove(t);
            Drop(t, why);
        }
        return gone.Count;
    }

    // ---- route choice ----

    /// <summary>
    /// <paramref name="exits"/> in the order <paramref name="bot"/> tries them when it leaves (see the class): the nearest first; with
    /// Bots:Travel:Enabled for a travelling role, first the exit to the next cluster of its way home (walking home), then the exits to a
    /// neighbour that takes it (a zone of its tier first, then by the tier distance), then the others.
    /// </summary>
    public IEnumerable<BotExit> ExitOrder(Player bot, IEnumerable<BotExit> exits, Vector2 here)
    {
        var near = exits.OrderBy(e => Vector2.DistanceSquared(e.Gate, here)).ToList();
        if (!TravelEnabled || bot.Map is not { } map || bot.Bot is not { } info || !Travels(info.Role)) return near;
        var next = info.Homing && info.HomeCity is { } home ? NextHop(map.Cluster, home) : null;
        return [.. near.OrderBy(e => ExitScore(map, e, info, next))]; // stable: the nearest first within a score
    }

    private int ExitScore(Map map, BotExit exit, BotInfo info, string? next)
    {
        if (WorldExitAt(map, exit.Position) is not { } w) return 100; // a road's portal: as an exit that leads nowhere bots go
        if (next is not null && w.TargetCluster == next) return 0;
        if (world.Maps.Find(w.TargetCluster) is not { } target || target == map || Accept(target, info.Role, false) is null || ZoneOf(target) is not { } zone) return 100;
        var tier = target.Template!.Name.Tier ?? 0;
        return 1 + (TierAllowed(zone, tier, info.Tier) ? 0 : Math.Abs(info.Tier - (tier == 0 ? 4 : tier)));
    }

    // ---- the cluster graph (world.xml exits) ----

    /// <summary>A home city: a royal city or rest with a bank (PLAYERCITY_*, not an interior, a hall, a portal town or the Hell Den), allowed by Bots:Zones.</summary>
    private bool HomeCity(ClusterInfo c) => c.Type.StartsWith("PLAYERCITY_", StringComparison.Ordinal) && Passable(c) && !c.Type.Contains("PORTALCITY", StringComparison.Ordinal)
        && Options.ZoneAllowed("City", c.Id);

    /// <summary>
    /// Whether a way over the cluster graph may pass <paramref name="c"/>: no instance, no building interior (bank, market, hall: *_NOFURNITURE),
    /// no Hell Den, no road, not content/bots.json Exclude, not left out by Bots:Zones (a zone known from its cluster type or its loaded data).
    /// </summary>
    private bool Passable(ClusterInfo c)
    {
        var id = c.Id;
        if (DungeonLayout.IsInstance(id) || MistsIds.IsInstance(id) || CorruptedData.IsInstance(id) || HellgateData.IsInstance(id) || ArenaIds.IsInstance(id)
            || Settings.Exclude.Contains(id) || c.Type.Contains("NOFURNITURE", StringComparison.Ordinal) || c.Type.Contains("HELLDEN", StringComparison.Ordinal)
            || c.Type.StartsWith("TUNNEL_", StringComparison.Ordinal)) return false;
        if (Options.Zones.Count == 0) return true;
        var zone = c.Type.StartsWith("PLAYERCITY_", StringComparison.Ordinal) ? "City" : c.Type == "STARTINGCITY" ? "Start"
            : world.CachedCluster(id) is { } t ? (t.Pvp.Rule == PvpRule.PvpDisabled && !Settings.Include.Contains(id) ? null
                : BotSettings.Zones.FirstOrDefault(z => z.Equals(t.Pvp.WarningType, StringComparison.OrdinalIgnoreCase)) ?? "Safe")
            : "";
        return zone is not null && (zone.Length == 0 || Options.ZoneAllowed(zone, id)); // a zone not known yet: its map's own check decides
    }

    /// <summary>
    /// The shortest way (cluster ids after <paramref name="from"/>, the goal last) over world.xml's exits to a cluster <paramref name="goal"/>
    /// takes, at most Bots:Travel:MaxHops long and only through <see cref="Passable"/> clusters; null when there is none (or no world.xml).
    /// </summary>
    private List<string>? Way(string from, Func<ClusterInfo, bool> goal)
    {
        if (world.Clusters?.WorldIfLoaded is not { } info) return null;
        var previous = new Dictionary<string, string>(StringComparer.Ordinal) { [from] = "" };
        var frontier = new List<string> { from };
        for (var hop = 0; hop < Options.TravelMaxHops && frontier.Count > 0; hop++)
        {
            var next = new List<string>();
            foreach (var id in frontier)
                foreach (var to in info.Find(id)?.Exits.Select(e => e.TargetCluster).Distinct() ?? [])
                {
                    if (previous.ContainsKey(to) || info.Find(to) is not { } c) continue;
                    previous[to] = id;
                    if (goal(c))
                    {
                        var way = new List<string> { to };
                        for (var at = id; at != from; at = previous[at]) way.Insert(0, at);
                        return way;
                    }
                    if (Passable(c)) next.Add(to);
                }
            frontier = next;
        }
        return null;
    }

    /// <summary>The home city of a gatherer that comes online on <paramref name="cluster"/> (see the class): itself when it is one; null when none is near enough.</summary>
    public string? HomeCityOf(string cluster)
    {
        if (_homes.TryGetValue(cluster, out var known)) return known;
        if (world.Clusters?.WorldIfLoaded is not { } info) return null; // not cached: world.xml is read at the first Join
        var home = info.Find(cluster) is { } c && HomeCity(c) ? cluster : Way(cluster, HomeCity)?[^1];
        return _homes[cluster] = home;
    }

    /// <summary>The next cluster on the way from <paramref name="from"/> to <paramref name="home"/>; null when there is none within Bots:Travel:MaxHops.</summary>
    public string? NextHop(string from, string home) => from == home ? null : Way(from, c => c.Id == home)?[0];

    // ---- gatherers' trips ----

    /// <summary>
    /// The home city <paramref name="bot"/> (a gatherer) walks to now to bank (see the class): walking home already, or its trip over
    /// (<paramref name="full"/>: Bots:Gatherer:Nodes nodes, or Bots:Gatherer:TripMinutes) with something gathered; null when it does not
    /// (Bots:Gatherer:BankAtHome or Bots:Travel:Enabled off, no home city).
    /// </summary>
    public string? GoesHome(Player bot, bool full, TimeSpan now) =>
        TravelEnabled && Options.GathererBankAtHome && bot.Bot is { HomeCity: { } home } info
        && (info.Homing || (full || now >= info.TripUntil) && info.Fight.CarriedTotal > 0) ? home : null;

    /// <summary><paramref name="bot"/> banked at its home city's bank: what it gathered is gone (in that city only), a new trip begins.</summary>
    public void Banked(Player bot)
    {
        var info = bot.Bot!;
        var total = info.Fight.CarriedTotal;
        info.Fight.Carried.Clear();
        info.Homing = false;
        info.TripUntil = world.Clock.Now + TimeSpan.FromMinutes(Options.GathererTripMinutes);
        Log.Debug("bots", $"map {bot.Map?.Cluster}: gatherer '{bot.Character.Name}' banked {total} resource(s) at its home city's bank");
    }
}
