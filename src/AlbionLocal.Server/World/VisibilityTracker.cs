using System.Numerics;
using AlbionLocal.Server.Entities;
using AlbionLocal.Server.Protocol;

namespace AlbionLocal.Server;

/// <summary>
/// What a player's client has been told about the world objects of its map (TC m_clientGUIDs; ARCHITECTURE.md §4.3 with
/// SPEC stage 2 §5 and §6). An object of the window kinds is sent when its cell comes within
/// <see cref="VisibilityOptions.EnterCells"/> (4) of the player's cell, and forgotten at <see cref="VisibilityOptions.LeaveCells"/>
/// (6, one cell of hysteresis) or more: with a Leave for the kinds that get one (<see cref="WorldObject.LeaveOnRange"/>), silently for the
/// landscape-bound kinds, which are sent again on re-entry. Cluster-wide objects (the faction trader) are sent once and
/// never left. Harvestables (static ones and resource nodes) go out in lists of 40; an enchanted resource node as event 40.
/// The Join phases (all 15 captured Joins): <see cref="Join"/> sends phase A, the window, before the items and the Join
/// response; <see cref="SendClusterWide"/> phase B, between the Join response and JoinFinished; <see cref="FlushLists"/>
/// phase C, the harvestable lists right after JoinFinished (official: 0-33 ms). Afterwards <see cref="Update"/> runs on
/// each Move and in each tick while the player walks, and does something only when the player's cell changed.
/// With World:AnnounceAll (a debug switch) every object of the map is sent at Join and nothing is ever left, which tells
/// a rendering problem from a visibility one. Touched only under <see cref="World.Sync"/>.
/// Other players (stage 7, analysis/work/stage7/SPEC.md §3) use the same window and the same options, as a symmetric pair
/// test: A knows B exactly when B knows A (<see cref="KnownPlayers"/> = <see cref="Player.Observers"/>). The pair is tested
/// whenever either one's cell changes (its Move, the tick while it walks); entering sends each the other's spawn block
/// (NewCharacter, <see cref="Player.BuildCreate"/>, its ActiveSpellEffectsUpdate when it has buffs and Mounted when it
/// rides), leaving a Leave both ways. A player leaving the map (<see cref="LeavePlayers"/>: disconnect, logout,
/// cluster change) gets a Leave to everyone who knows it; its own client gets none (its next Join is a new connection).
/// Never the receiver's own id, and never a pair of two connections playing one character (a reconnect ghost): the new
/// client must not get a NewCharacter carrying its own Guid in key 7. With AnnounceAll every player of the map is known
/// until one of them leaves the map. Without a template the map has no grid yet; players then use cells on origin (0, 0)
/// until it comes (<see cref="Refresh"/> tests the pairs again on the real grid).
/// Stage I bots (analysis/work/stageI/bots/SPEC.md §1.2): a bot's tracker knows players only (no world objects are passed or sent for
/// it), pairs with the humans of <see cref="Map.Players"/> and never with another bot; a human's pass meets <see cref="Map.Bots"/> too,
/// so a bot comes and goes on the humans' screens exactly as another player does.
/// </summary>
public sealed class VisibilityTracker(Player player)
{
    private readonly HashSet<WorldObject> _known = [];

    /// <summary>Known harvestables not yet sent in a list.</summary>
    private readonly List<Harvestable> _unlisted = [];

    /// <summary>
    /// The listed harvestables (list 39) this client has been sent on this map, out of range again included: a listed one is
    /// forgotten without a Leave, so the client keeps its id until its next Join.
    /// </summary>
    private readonly HashSet<Harvestable> _listedSent = [];

    /// <summary>The other players this client has a NewCharacter of (and no Leave since).</summary>
    private readonly HashSet<Player> _players = [];

    private Map? _map;

    /// <summary>The grid players use while the map has no template (cells on origin (0, 0)).</summary>
    private CellGrid? _noTemplate;

    /// <summary>
    /// Set during the Join's phase A: the players found then get this one's NewCharacter only from <see cref="AnnounceSelf"/>,
    /// once its items and bars exist.
    /// </summary>
    private bool _joining;

    /// <summary>
    /// From <see cref="Join"/> until <see cref="SendAccessStatus"/> (right after JoinFinished): the buildings entered in that
    /// time, whose AccessStatus waits for that batch (002835: every building of the Join after JoinFinished).
    /// </summary>
    private List<IAccessStatusObject>? _accessPending;

    /// <summary>The objects the client has been told about and not left or dropped since (a harvestable from when it is queued for the next list).</summary>
    public IReadOnlyCollection<WorldObject> Known => _known;

    /// <summary>Whether the client knows <paramref name="o"/> now (its New* event went out and no Leave since).</summary>
    public bool Knows(WorldObject o) => _known.Contains(o);

    /// <summary>
    /// Whether the client holds <paramref name="o"/> now: known and, a listed harvestable, its list sent (not still queued for the next
    /// <see cref="FlushLists"/>, as during a Join until JoinFinished). Events the client looks the object up for go out only then.
    /// </summary>
    public bool KnowsSent(WorldObject o) => _known.Contains(o) && !(o is Harvestable { Listed: true } h && _unlisted.Contains(h));

    /// <summary>
    /// Whether the client may still hold <paramref name="h"/>: known now, or sent in a list since the Join and forgotten
    /// silently since (no Leave for list 39). Such a node must keep its rare state (<see cref="ResourceNodes.Update"/>).
    /// </summary>
    public bool Holds(Harvestable h) => _known.Contains(h) || _listedSent.Contains(h);

    /// <summary>The other players the client has been told about (NewCharacter) and not left since; the same set as <see cref="Player.Observers"/>.</summary>
    public IReadOnlyCollection<Player> KnownPlayers => _players;

    /// <summary>The player's cell at the last pass; null before the first one (no template on the map yet).</summary>
    public (int X, int Z)? Cell { get; private set; }

    /// <summary>The player's cell at the last player pass (on the template's grid, or on origin (0, 0) without one); null off a map.</summary>
    public (int X, int Z)? PlayerCell { get; private set; }

    private VisibilityOptions Options => player.World.Config.Visibility;

    private bool AnnounceAll => player.World.Config.AnnounceAll;

    /// <summary>
    /// Phase A of a Join on <paramref name="map"/> at <paramref name="position"/>: the New* events of the window's objects
    /// (all of the map's with AnnounceAll), before the items; its harvestables wait for <see cref="FlushLists"/>. Nothing
    /// while the map has no template yet: then <see cref="Refresh"/> sends everything when it comes.
    /// </summary>
    public void Join(Map map, Vector2 position)
    {
        _map = map;
        _known.Clear();
        _unlisted.Clear();
        _listedSent.Clear();
        _accessPending = [];
        Cell = null;
        if (map.Grid is not null && !player.IsBot) Pass(position); // stage I: a bot tracks players only
        // Then the players in view (stage 7 SPEC §3: official 611 NewCharacter in phase A); they hear of this one later.
        _joining = true;
        UpdatePlayers(position);
    }

    /// <summary>
    /// The end of the Join, once the player is on its map with items and bars: every player its phase A found gets its
    /// NewCharacter now (stage 7 SPEC §3, "at the same moment" as the other way round, in the same Join).
    /// </summary>
    public void AnnounceSelf()
    {
        if (!_joining) return;
        _joining = false;
        var now = DateTime.UtcNow;
        foreach (var q in _players) SendSpawn(player, q, now);
    }

    /// <summary>Phase B: the cluster-wide objects (NewBuilding and QuestGiverInfoForPlayer of the faction trader), once.</summary>
    public void SendClusterWide()
    {
        if (_map is null) return;
        foreach (var o in _map.ClusterWide) Enter(o);
    }

    /// <summary>
    /// Right after JoinFinished: the AccessStatus (<see cref="IAccessStatusObject.AccessStatus"/>) of every building and island management the Join's phases sent
    /// and the client still knows, in one batch (002835: +85.048-85.050, after the Join response and JoinFinished); from
    /// then on each building's comes right after its NewBuilding (002835: 10-30 ms later).
    /// </summary>
    public void SendAccessStatus()
    {
        if (_accessPending is not { } pending) return;
        _accessPending = null;
        foreach (var b in pending)
            if (b is WorldObject o && _known.Contains(o)) Send(Systems.OpeningTutorial.BuildingAccess(player, b));
    }

    /// <summary>
    /// The harvestables that came into view since the last call, as NewSimpleHarvestableObjectList events of at most 40 (phase C, then each
    /// pass); then the catch-up HarvestStart of each player in view who harvests one of them (stage I: held back until the client holds the
    /// node, <see cref="KnowsSent"/>; a Join's NewCharacter of such a harvester went out in phase A).
    /// </summary>
    public void FlushLists()
    {
        if (_unlisted.Count == 0) return;
        foreach (var list in Harvestable.Lists(_unlisted)) Send(list);
        var listed = _unlisted.ToHashSet();
        _unlisted.Clear();
        foreach (var q in _players)
            if (Harvesting.NodeOf(q) is { } node && listed.Contains(node) && Harvesting.ForNewObserver(q, player) is { } harvest) Send(harvest);
    }

    /// <summary>
    /// The player is at <paramref name="position"/> now (a Move, or the world tick while it walks): when its cell changed,
    /// the objects now out of range are left or dropped, the new ones sent, harvestables included.
    /// </summary>
    public void Update(Vector2 position)
    {
        if (!player.IsBot && _map?.Grid is { } grid && grid.CellOf(position) != Cell)
        {
            var first = Cell is null;
            Pass(position);
            if (first) SendClusterWide();
            FlushLists();
        }
        UpdatePlayers(position);
    }

    /// <summary>
    /// The map's template came in after the player joined (a first Join into a cluster whose data was still loading):
    /// the phases at once, as a late pass; the player pairs again on the template's grid.
    /// </summary>
    public void Refresh(Vector2 position)
    {
        if (_map is null) return;
        Cell = null;
        PlayerCell = null;
        Update(position);
    }

    /// <summary>
    /// The map's template came in (<see cref="World"/> Populate, before the <see cref="Refresh"/> of each player): the cell
    /// on the temporary grid is dropped, so that no pair is tested across the two grids. Until its Refresh the player is
    /// skipped by the others' passes as one still in its Join; the later Refresh of each pair tests it on the real grid.
    /// </summary>
    internal void ForgetPlayerCell() => PlayerCell = null;

    /// <summary>The player's spawn block again to every player who knows it (stageE E3: a dead player, hidden from them, raised in place).</summary>
    public void Reannounce()
    {
        var now = DateTime.UtcNow;
        foreach (var q in _players) SendSpawn(player, q, now);
    }

    /// <summary>
    /// The player leaves its map (disconnect, logout, cluster change, a new Join on the connection), before it is taken
    /// off: everyone who knows it gets Leave {0: its id} and forgets it; its own client gets nothing (stage 7 SPEC §3).
    /// </summary>
    public void LeavePlayers()
    {
        foreach (var q in _players)
        {
            q.Visibility._players.Remove(player);
            q.Session.Send(new LeaveEvent { ObjectId = player.Id });
        }
        _players.Clear();
        PlayerCell = null;
        _joining = false;
    }

    /// <summary>
    /// The player pass (stage 7 SPEC §3): when the player's cell changed, each other player of the map with a cell of its
    /// own is tested once: known and at <see cref="VisibilityOptions.LeaveCells"/> or more → Leave both ways; not known and
    /// within <see cref="VisibilityOptions.EnterCells"/> → the spawn blocks both ways. The other one's cell is its own last
    /// pass, so either side's move decides the pair the same way.
    /// </summary>
    private void UpdatePlayers(Vector2 position)
    {
        if (_map is null) return;
        var cell = (_map.Grid ?? (_noTemplate ??= new CellGrid(Vector2.Zero, Options.CellSize))).CellOf(position);
        if (cell == PlayerCell) return;
        PlayerCell = cell;
        var now = DateTime.UtcNow;
        // Stage I: a human's pass meets the bots too, a bot's only the humans (bots never see bots).
        foreach (var q in player.IsBot ? _map.Players.Values : _map.Players.Values.Concat(_map.Bots.Values))
        {
            // Not itself, not a player still in its own Join (on the map only once it is done), not the same character.
            if (q == player || q.Visibility.PlayerCell is not { } other || q.Character.Id == player.Character.Id) continue;
            var d = CellGrid.Distance(cell, other);
            if (_players.Contains(q))
            {
                if (AnnounceAll || d < Options.LeaveCells) continue;
                _players.Remove(q);
                q.Visibility._players.Remove(player);
                Send(new LeaveEvent { ObjectId = q.Id });
                q.Session.Send(new LeaveEvent { ObjectId = player.Id });
            }
            else if (AnnounceAll || d <= Options.EnterCells)
            {
                _players.Add(q);
                q.Visibility._players.Add(player);
                SendSpawn(q, player, now);
                if (!_joining) SendSpawn(player, q, now);
            }
        }
    }

    /// <summary>
    /// The spawn block of <paramref name="about"/> to <paramref name="viewer"/>'s client: NewCharacter (stage 7 SPEC §1), then its
    /// ActiveSpellEffectsUpdate when it has active spells (stageD effects SPEC §2), then Mounted {7, 9, 11} when it rides (stageD mounts SPEC §4.8),
    /// then a duelist's NewDuellingPost (once per client) and DuelStarted (stageE pvp SPEC §2.4), then a fisher's state (FishingStart or
    /// FishingMiniGame, stageG fishing SPEC §10), a harvest under way (HarvestStart with its original start, a player's or a bot's
    /// show, stage I), then a bot's looping emote (stage I, PlayEmote with its start). None of a dead player the
    /// others have had the Leave of (E3, <see cref="KnockedDownState.Hidden"/>), none to a bot.
    /// </summary>
    private static void SendSpawn(Player about, Player viewer, DateTime now)
    {
        if (viewer.IsBot) return; // stage I: a bot has no client
        if (about.Combat.Down is { Hidden: true }) return; // stageE E3: a dead player the others have had the Leave of (Systems/Death)
        if (about.Bot is { } bot)
        {
            now = about.World.Bots.Time; // stage I: a bot walks on the game clock
            bot.Motion.Refresh(about, about.World.Bots); // and its leg's sample starts where it is now, not where the leg began
        }
        viewer.Session.Send(about.BuildCreate(now));
        if (Systems.SpellEffects.ForNewObserver(about, about.World.GameNow) is { } effects) viewer.Session.Send(effects);
        if (Systems.Mounts.ForNewObserver(about) is { } mounted) viewer.Session.Send(mounted); // stageD mounts SPEC §4.8
        about.World.Duels.ForNewObserver(about, viewer); // stageE pvp SPEC §2.4: a duelist's post and DuelStarted
        if (Systems.Fishing.ForNewObserver(about) is { } fishing) viewer.Session.Send(fishing); // stageG fishing SPEC §10
        if (Harvesting.ForNewObserver(about, viewer) is { } harvest) viewer.Session.Send(harvest); // stage I: a harvest under way, its original start
        if (Bots.BotManager.EmoteFor(about) is { } emote) viewer.Session.Send(emote); // stage I: a bot's looping emote with its start
        if (Systems.Emotes.ForNewObserver(about) is { } own) viewer.Session.Send(own); // stageN qol: a player's looping emote
    }

    private void Pass(Vector2 position)
    {
        var grid = _map!.Grid!;
        var cell = grid.CellOf(position);
        var first = Cell is null;
        Cell = cell;
        if (AnnounceAll)
        {
            if (first)
                foreach (var o in _map.Objects.Values)
                    if (o.Range == AnnounceRange.Window) Enter(o);
            return;
        }
        // A random dungeon floor (stageG dungeons SPEC §2.5, §6.3): its two exits (…0001, …0002) go out in phase A whatever the
        // distance and stay known while the player is on the floor.
        var instance = Data.DungeonLayout.IsInstance(_map.Cluster);
        if (first && instance)
            foreach (var o in _map.Objects.Values)
                if (o is Exit) Enter(o);
        // Out of range first: Leave for the kinds that get one, the landscape-bound ones are only forgotten.
        List<WorldObject>? gone = null;
        foreach (var o in _known)
            if (o.Range == AnnounceRange.Window && !(instance && o is Exit) && CellGrid.Distance(o.Cell, cell) >= Options.LeaveCells) (gone ??= []).Add(o);
        foreach (var o in gone ?? [])
        {
            _known.Remove(o);
            if (o is Harvestable { Listed: true } h) _unlisted.Remove(h);
            // Never the player's own id: that would make the client ignore every later Leave (ciy.a 0x18096dd50 sets +0xc0).
            else if (o.LeaveOnRange && o.Id != player.Id) Send(o.BuildLeave());
        }
        foreach (var o in grid.Around(cell, Options.EnterCells))
            Enter(o);
    }

    /// <summary>
    /// An object put on the map after the player's last pass (a mob spawned, stage 5): sent now when its cell is within
    /// <see cref="VisibilityOptions.EnterCells"/> of the player's (any distance with AnnounceAll), else by a later pass.
    /// </summary>
    public void Show(WorldObject o)
    {
        if (_map is null || o.Map != _map || Cell is not { } cell || o.Range != AnnounceRange.Window) return;
        if (AnnounceAll || CellGrid.Distance(o.Cell, cell) <= Options.EnterCells) Enter(o);
    }

    /// <summary>
    /// An object of the map moved into another cell (a walking mob, stage 5d; <see cref="Map.Relocate"/>): sent when its new
    /// cell is within <see cref="VisibilityOptions.EnterCells"/> of the player's, left (Leave) at
    /// <see cref="VisibilityOptions.LeaveCells"/> or more, as the player's own passes do. Nothing with AnnounceAll.
    /// </summary>
    public void Moved(WorldObject o)
    {
        if (AnnounceAll || _map is null || o.Map != _map || Cell is not { } cell || o.Range != AnnounceRange.Window) return;
        var d = CellGrid.Distance(o.Cell, cell);
        if (_known.Contains(o))
        {
            if (d >= Options.LeaveCells) Forget(o);
        }
        else if (d <= Options.EnterCells) Enter(o);
    }

    /// <summary>An object taken off the map (a mob despawned): a Leave when the client knows it, then it is forgotten.</summary>
    public void Forget(WorldObject o)
    {
        if (!_known.Remove(o)) return;
        if (o is Harvestable { Listed: true } h) _unlisted.Remove(h);
        else if (o.LeaveOnRange && o.Id != player.Id) Send(o.BuildLeave());
    }

    private void Enter(WorldObject o)
    {
        if (!_known.Add(o)) return;
        if (o is Harvestable { Listed: true } h) // an enchanted resource node comes as its own event 40
        {
            _unlisted.Add(h);
            _listedSent.Add(h);
        }
        else
            foreach (var e in o.BuildCreate(player)) Send(e);
        if (o is IAccessStatusObject b)
        {
            if (_accessPending is { } pending) pending.Add(b);
            else Send(Systems.OpeningTutorial.BuildingAccess(player, b));
        }
    }

    private void Send(AlbionEvent e) => player.Session.Send(e);
}
