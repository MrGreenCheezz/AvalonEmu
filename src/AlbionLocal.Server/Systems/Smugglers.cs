using System.Globalization;
using System.Numerics;
using System.Runtime.CompilerServices;
using AlbionLocal.Photon;
using AlbionLocal.Server.Data;
using AlbionLocal.Server.Entities;
using AlbionLocal.Server.Protocol;

namespace AlbionLocal.Server.Systems;

/// <summary>
/// The smugglers (analysis/work/stageR/smugglers/SPEC.md, "SPEC" here), with Smugglers:Crates:
/// <list type="bullet">
/// <item>The stations (SPEC §3.4): every *_SMUGGLER_CRATE_DELIVERY_STATION prefab the resource placement puts on an Outlands cluster's
/// roaming-mob nodes (resourceprefabmappings: weight 10 of the roamingmob prefabs) stands as a <see cref="SmugglerDeliveryStation"/> (614).</item>
/// <item>The piles (SPEC §3.1): the TreasureSpawnpointSmugglerPile fillers of the same placement are the spots; treasuredistribution.xml's
/// &lt;Distribute group="SMUGGLER_PILE"&gt; SMALL / MEDIUM / LARGE of the Outlands and the cluster's tier draw piles on them (maxslotsused 1
/// each, activationtimemin/max 150-450 s ÷ Rates:ChestRespawn, activationchance, the spot free for positioncooldown 60 s after); the first draw
/// at the fill [assume, as the treasures]. A pile is Protected for its protectiontimesec (LARGE 300, MEDIUM 180, SMALL none), then Lootable;
/// its stacksize crates (10, 5, 1) each roll a rarity by the pile's smugglercratepilereward weights; empty, it goes after
/// despawnsafteremptiedinsec (30 s); unemptied after its TreasurePile maxlifetime (86 400 s).</item>
/// <item>The pickup (SPEC §3.2, op 518 / 519): PickupFromPiledObjectStart (616) {0 the player, 1 start, 2 end, 3 the pile, 4 its index} to
/// the player and its observers, the pile's channeling count up (613); after pileitempickupdurationsec (3 s)
/// PickupFromPiledObjectFinished (619) {0, 1 the pile's index, 2 the crate's rarity}, a crate fewer (613 with the new rarities), and the
/// player carries the crate (<see cref="Carriables.Take"/>: SMUGGLERS_CRATE_CARRY_BUFF_1-4). The client's channel class cso/cyw (action
/// state 0x2B; cso.aft 0x180BF25F0, cyw.aft 0x180A066A0). A cancel: 617 {0, 1 the pile's index} [infer key 1].</item>
/// <item>The delivery (SPEC §3.4, op 304 / 305): a carrier within claimcarriableonsmugglersdestinationrange (5 m) of a station:
/// DeliverCarriableObjectStart (336) {0 the player, 1 start, 2 end, 3 the station, 4 the carriable index, 5 rarity} and ReceiveCarriableObjectStart
/// (345) {0 the station, 1 rarity, 2 index} [infer: the station's side, cnc.x8 0x180B7D430]; after deliverduration (3 s)
/// DeliverCarriableObjectFinished (339) {0, 1 index, 2 rarity} and ReceiveCarriableObjectFinished (346) {0 the station, 1 true, 2 rarity,
/// 3 index}; the crate is gone and its mightreward objective pays (pvpchallenge.xml PVPCHALLENGE_SMUGGLERS_CRATE_0-3: the smugglers
/// standing 3 250-8 918 and the FAVOR currency 856-2 349; the might is not kept here). The client's channel class cru/cwv (action state 0x27).</item>
/// <item>The smugglers' standing (StandingType Smugglers 1, standings.xml max 10 000 000): <see cref="Character.SmugglerStanding"/>,
/// UpdateStanding (87) at every gain and after every Join; the den trader's (HIGHLAND_RED_SMUGGLER_SPOT_BLACKBANK) standing gates
/// (<see cref="Mists.TraderRefusal"/>).</item>
/// </list>
/// The dens themselves are ordinary world.xml clusters (BLACKBANK-*, PLAYERCITY_BLACK_SMUGGLERSDEN) with their bank, market, repair shop and
/// meld building (SPEC §2); their market is the Smuggler's Network (<see cref="MarketLocation"/>). Under <see cref="World.Sync"/>.
/// </summary>
public sealed class Smugglers(World world)
{
    /// <summary>StandingType Smugglers (Albion.Common.SimulationObjects.StandingType).</summary>
    public const int StandingType = 1;

    /// <summary>standings.xml smugglers: the points from which each of the 10 ranks starts (max 10 000 000), whole points.</summary>
    public static readonly long[] StandingRanks = [0, 50_000, 150_000, 300_000, 600_000, 1_000_000, 1_800_000, 3_250_000, 5_700_000, 10_000_000];

    /// <summary>The market location of the Smuggler's Network (Smugglers:NetworkMarket true): one order book for every den.</summary>
    public const string NetworkLocation = "SMUGGLERS_NETWORK";

    /// <summary>The prefix of a den's own market location (Smugglers:NetworkMarket false).</summary>
    private const string DenPrefix = "SMUGGLERS:";

    /// <summary>The RC of a refused pile or delivery request (the client resets its action state on any non-zero code [infer from cyv.ai7]).</summary>
    public const short Refused = 1;

    private SmugglerData? _data;
    private bool _dataFailed;

    /// <summary>The client data (read once, at the first use); null without it.</summary>
    public SmugglerData? Data
    {
        get
        {
            if (_data is not null || _dataFailed || world.Clusters is not { } store) return _data;
            try { _data = SmugglerData.Load(store.Folder); }
            catch (Exception e) when (e is InvalidDataException or IOException)
            {
                _dataFailed = true;
                Log.Warn("world", $"smugglers: {e.Message}; no piles or stations");
            }
            return _data;
        }
    }

    private sealed class Spot(Vector2 at, float direction)
    {
        public Vector2 Position { get; } = at;
        public float Direction { get; } = direction;
        public SmugglerCratePile? Pile { get; set; }
        public TimeSpan FreeAt { get; set; }
    }

    private sealed class Slot(string subtype, PileDraw draw)
    {
        public string Subtype { get; } = subtype;
        public PileDraw Draw { get; } = draw;
        public SmugglerCratePile? Pile { get; set; }
        public bool Waiting { get; set; }
    }

    private sealed class MapPiles
    {
        public readonly List<Spot> Spots = [];
        public readonly List<Slot> Slots = [];
        public int Tier;
        public ClusterTemplate? Template;
    }

    /// <summary>A pile pickup or a delivery under way.</summary>
    private sealed record Channel(Map Map, WorldObject Target, TimeSpan Due, bool Deliver);

    private readonly ConditionalWeakTable<Map, MapPiles> _maps = new();
    private readonly Dictionary<Player, Channel> _channels = [];

    private Carriables Carry => world.Carriables;

    /// <summary>Whether a pile pickup or a delivery of <paramref name="p"/> is under way.</summary>
    public bool Busy(Player p) => _channels.ContainsKey(p);

    // ---- the den's market (SPEC §2.3) ----

    /// <summary>
    /// The market location of a den's Smuggler's Market (smugglermarketbuilding) on <paramref name="cluster"/>: Smugglers:NetworkMarket true (the
    /// official rule: "All Smuggler's Market Orders are available across the entirety of the Network", TUTORIAL_SMUGGLERS_MARKET_INTRO_TEXT) one
    /// book for every den; false: each den its own. The cities' markets are not touched either way.
    /// </summary>
    public string MarketLocation(string cluster) => world.Config.Smugglers.NetworkMarket ? NetworkLocation : DenPrefix + cluster;

    /// <summary>Whether <paramref name="location"/> is a smugglers' market (the setup fee is gamedata's smugglersetupfee, 1.5 %).</summary>
    public static bool IsMarket(string location) => location == NetworkLocation || location.StartsWith(DenPrefix, StringComparison.Ordinal);

    // ---- the map's stations and piles ----

    /// <summary>
    /// At an Outlands open-world map's fill (MapSpawner): its stations, its pile spots and slots, and the piles of the first draws, not yet on
    /// the map. Nothing with Smugglers:Crates off, elsewhere, or without the data.
    /// </summary>
    public List<WorldObject> Populate(Map map, ClusterTemplate template)
    {
        var made = new List<WorldObject>();
        if (!world.Config.Smugglers.Crates || template.Resources is not { } resources) return made;
        if (ClusterNameInfo.Parse(template.Info.File) is not { Type: 0 or 20 } cni || !ClusterNameInfo.ContinentNames.TryGetValue(cni.Continent, out var continent)
            || continent != "Outlands" || DungeonLayout.IsInstance(map.Cluster))
            return made;
        var stations = resources.Placements.Where(p => p.Name.EndsWith(SmugglerData.StationSuffix, StringComparison.Ordinal)).ToList();
        var spots = resources.Placements.Where(p => p.Name == SmugglerData.PileSpotPrefab).ToList();
        if (stations.Count == 0 && spots.Count == 0 || Data is not { } data) return made;
        foreach (var s in stations)
            made.Add(new SmugglerDeliveryStation(world.ObjectIds.Next(), SmugglerSpawn.At(map.Cluster, map.CustomSpawns++, s.Name, s.Position, s.NodeDirection, "smugglerscratedeliverystation")));
        var m = new MapPiles { Tier = template.Name.Tier ?? cni.Tier, Template = template };
        foreach (var s in spots) m.Spots.Add(new Spot(s.Position, s.NodeDirection));
        foreach (var subtype in data.Draws.Select(d => d.Subtype).Distinct())
            if (m.Spots.Count > 0 && data.DrawFor(subtype, continent, m.Tier) is { Slots: > 0 } draw)
                for (var i = 0; i < draw.Slots; i++) m.Slots.Add(new Slot(subtype, draw));
        _maps.AddOrUpdate(map, m);
        foreach (var slot in m.Slots)
            if (Draw(map, m, slot, test: false) is { } pile) made.Add(pile);
            else Schedule(map, m, slot);
        Log.Info("world", $"map {map.Cluster}: {stations.Count} smuggler stash(es), {m.Spots.Count} pile spot(s), {m.Slots.Count} pile slot(s); {made.Count(o => o is SmugglerCratePile)} pile(s) now");
        return made;
    }

    private void Schedule(Map map, MapPiles m, Slot slot)
    {
        if (slot.Waiting) return;
        slot.Waiting = true;
        var seconds = slot.Draw.MinSeconds + Math.Max(0, slot.Draw.MaxSeconds - slot.Draw.MinSeconds) * map.Random.NextDouble();
        map.Timers.At(world.Now + TimeSpan.FromSeconds(Math.Max(1, seconds) / world.Rates.ChestRespawn), () =>
        {
            slot.Waiting = false;
            if (map.Unloaded || !_maps.TryGetValue(map, out var live) || live != m || slot.Pile is not null) return;
            if (Draw(map, m, slot, test: false) is { } pile)
            {
                map.AddObject(pile);
                Log.Info("world", $"map {map.Cluster}: {pile} spawned");
            }
            else Schedule(map, m, slot);
        });
    }

    /// <summary>A slot's draw: activationchance, a TreasurePile by weight, a free spot; the pile, not yet on the map. Null = nothing this time.</summary>
    private SmugglerCratePile? Draw(Map map, MapPiles m, Slot slot, bool test)
    {
        if (Data is not { } data || slot.Draw.Entries.Count == 0) return null;
        if (!test && map.Random.NextDouble() >= slot.Draw.Chance) return null;
        var name = DungeonGenerator.Pick(map.Random, [.. slot.Draw.Entries.Select(e => (e.Name, e.Weight))]);
        var free = m.Spots.Where(s => s.Pile is null && s.FreeAt <= world.Now).ToList();
        if (free.Count == 0) return null;
        var spot = free[map.Random.Next(free.Count)];
        if (Make(map, m.Template ?? map.Template, name, spot.Position, spot.Direction) is not { } pile) return null;
        (spot.Pile, slot.Pile) = (pile, pile);
        return pile;
    }

    /// <summary>
    /// A pile of TreasurePile <paramref name="treasurePile"/> at <paramref name="at"/>, not yet on the map: its crates rolled, Protected for its
    /// protection time (then Lootable, 613), gone at its maxlifetime. Null without its definitions or its tile for the cluster's biome.
    /// </summary>
    private SmugglerCratePile? Make(Map map, ClusterTemplate? cluster, string treasurePile, Vector2 at, float direction)
    {
        if (Data is not { } data) return null;
        var (pileType, lifetime) = data.TreasurePiles.GetValueOrDefault(treasurePile, (treasurePile, 86400));
        if (data.Pile(pileType) is not { Kind: "smugglercratepile" } def || def.Rewards.Count == 0)
        {
            Combat.LogOnce("pile:" + treasurePile, $"smuggler pile {treasurePile}: no smugglercratepile {pileType} in piledobjects.xml");
            return null;
        }
        var tile = data.TileFor(def.TileRef, cluster?.Name.Biome, cluster?.Name.TemplateFolder);
        if (tile is null)
        {
            Combat.LogOnce("piletile:" + def.TileRef + cluster?.Name.Biome, $"smuggler pile {def.Name}: no {def.TileRef} tile for {cluster?.Name.Biome}/{cluster?.Name.TemplateFolder}");
            return null;
        }
        var index = world.Clusters?.TreasuresIfLoaded?.ClientIndex("TreasurePile", treasurePile) ?? TryTreasureIndex(treasurePile);
        var now = world.GameNow;
        var pile = new SmugglerCratePile(world.ObjectIds.Next(), SmugglerSpawn.At(map.Cluster, map.CustomSpawns++, tile, at, direction, "smugglercratepile"), def,
            index > 1 ? index : -1, now);
        for (var i = 0; i < def.StackSize; i++)
            pile.Crates.Add(DungeonGenerator.Pick(map.Random, [.. def.Rewards.Where(r => r.Weight > 0).Select(r => (r, r.Weight))]).Rarity);
        if (def.ProtectionSeconds > 0)
        {
            pile.State = SmugglerCratePile.Protected;
            pile.ProtectedUntil = now + SpellCasts.Seconds(def.ProtectionSeconds);
            map.Timers.At(world.Now + TimeSpan.FromSeconds(def.ProtectionSeconds), () =>
            {
                if (pile.Map != map || pile.State != SmugglerCratePile.Protected) return;
                pile.State = SmugglerCratePile.Lootable;
                map.SendToObservers(pile, pile.BuildUpdate(false));
            });
        }
        map.Timers.At(world.Now + TimeSpan.FromSeconds(lifetime), () =>
        {
            if (pile.Map == map && map.Objects.ContainsKey(pile.Id)) Remove(map, pile, "its maxlifetime is up");
        });
        return pile;
    }

    private int TryTreasureIndex(string name)
    {
        try { return world.Clusters?.Treasures.ClientIndex("TreasurePile", name) ?? -1; }
        catch (Exception e) when (e is InvalidDataException or IOException) { return -1; }
    }

    /// <summary>A pile goes: off the map, its spot free after the cooldown, its slot drawing again.</summary>
    private void Remove(Map map, SmugglerCratePile pile, string why)
    {
        foreach (var (p, ch) in _channels.Where(x => x.Value.Target == pile).ToList()) Cancel(p, "the pile is gone");
        map.RemoveObject(pile);
        Log.Info("world", $"{pile} gone: {why}");
        if (!_maps.TryGetValue(map, out var m)) return;
        foreach (var spot in m.Spots.Where(s => s.Pile == pile))
        {
            spot.Pile = null;
            spot.FreeAt = world.Now + TimeSpan.FromSeconds(m.Slots.FirstOrDefault(s => s.Pile == pile)?.Draw.CooldownSeconds ?? 60);
        }
        foreach (var slot in m.Slots.Where(s => s.Pile == pile))
        {
            slot.Pile = null;
            Schedule(map, m, slot);
        }
    }

    /// <summary>A pile of <paramref name="treasurePile"/> put on <paramref name="map"/> after its fill (a GM's .smuggler pile, the self-test), shown to its players.</summary>
    public SmugglerCratePile? Place(Map map, string treasurePile, Vector2 at)
    {
        if (Make(map, map.Template, treasurePile, at, 0) is not { } pile) return null;
        map.AddObject(pile);
        return pile;
    }

    /// <summary>A station put on <paramref name="map"/> after its fill (a GM, the self-test), shown to its players.</summary>
    public SmugglerDeliveryStation Station(Map map, Vector2 at, string tile = "SWAMP_RED" + SmugglerData.StationSuffix)
    {
        var station = new SmugglerDeliveryStation(world.ObjectIds.Next(), SmugglerSpawn.At(map.Cluster, map.CustomSpawns++, tile, at, 0, "smugglerscratedeliverystation"));
        map.AddObject(station);
        return station;
    }

    // ---- the pile pickup (op 518 / 519) ----

    private double Reach(double range) => range + SpellCasts.RangeTolerance + 1;

    /// <summary>
    /// PickupFromPiledObjectStart (op 518 {0 time, 1 the pile}): a Lootable pile with crates on the player's map within pickuprange (5 m) + the
    /// tolerance, the player carrying nothing (PILED_OBJECT_PICKUP_NOT_ALLOWED_WHILE_CARRYING_CARRIABLE_OBJECT), not knocked down, nothing under
    /// way; then 616 and the pile's 613. Returns why it was refused, or null.
    /// </summary>
    public string? StartPile(Player p, long pileId)
    {
        if (p.Map is not { } map || !map.Objects.TryGetValue(pileId, out var o) || o is not SmugglerCratePile pile) return $"no pile #{pileId} here";
        if (p.Combat.Down is { } down) return $"knocked down ({down.Kind})";
        if (Carry.Of(p) is not null) return "carries already";
        if (Busy(p) || Carry.PickingUp(p)) return "a pickup or delivery is under way";
        if (pile.State != SmugglerCratePile.Lootable) return pile.State == SmugglerCratePile.Protected ? "still protected" : "empty";
        if (pile.Crates.Count <= pile.Channeling) return "no crate left";
        var at = p.PositionAt(DateTime.UtcNow);
        var reach = Reach(pile.Definition.PickupRange);
        if (Vector2.Distance(at, pile.Position) > reach) return string.Create(CultureInfo.InvariantCulture, $"{Vector2.Distance(at, pile.Position):0.#} m away (reach {reach:0.#})");
        Mounts.BeforeAction(p, "picks up a crate");
        var seconds = Math.Max(0.1, pile.Definition.PickupSeconds);
        var now = world.GameNow;
        _channels[p] = new Channel(map, pile, world.Now + TimeSpan.FromSeconds(seconds), Deliver: false);
        p.SendAround(new PickupFromPiledObjectStartEvent { ddn = p.Id, dmg = now, dmh = now + SpellCasts.Seconds(seconds), dmi = pile.Id, dmj = pile.Definition.Index });
        pile.Channeling++;
        map.SendToObservers(pile, pile.BuildUpdate(false));
        return null;
    }

    private void FinishPile(Player p, Channel ch)
    {
        _channels.Remove(p);
        var pile = (SmugglerCratePile)ch.Target;
        pile.Channeling = Math.Max(0, pile.Channeling - 1);
        if (pile.Map != ch.Map || pile.Crates.Count == 0 || Data?.Carriable(SmugglerData.Crate) is not { } crate)
        {
            p.SendAround(new PickupFromPiledObjectCancelEvent { ddn = p.Id, dmb = pile.Definition.Index });
            return;
        }
        var rarity = pile.Crates[^1];
        if (!Carry.Take(p, crate, rarity))
        {
            p.SendAround(new PickupFromPiledObjectCancelEvent { ddn = p.Id, dmb = pile.Definition.Index });
            ch.Map.SendToObservers(pile, pile.BuildUpdate(false));
            return;
        }
        pile.Crates.RemoveAt(pile.Crates.Count - 1);
        p.SendAround(new PickupFromPiledObjectFinishedEvent { ddn = p.Id, dmc = pile.Definition.Index, dmd = rarity, Extra = new() { [1] = pile.Definition.Index } });
        if (pile.Crates.Count == 0)
        {
            pile.State = SmugglerCratePile.Empty;
            var map = ch.Map;
            map.Timers.At(world.Now + TimeSpan.FromSeconds(pile.Definition.DespawnSeconds), () =>
            {
                if (pile.Map == map && map.Objects.ContainsKey(pile.Id)) Remove(map, pile, "emptied");
            });
        }
        ch.Map.SendToObservers(pile, pile.BuildUpdate(true));
        Log.Info("world", $"'{p.Character.Name}' took a crate of rarity {rarity} from {pile}");
    }

    // ---- the delivery (op 304 / 305) ----

    /// <summary>
    /// DeliverCarriableObjectStart (op 304 {0 time, 1 the station}): a smugglers' crate carried, a station on the player's map within
    /// claimcarriableonsmugglersdestinationrange (5 m) + the tolerance, not knocked down, nothing under way; then 336 and 345. Returns why
    /// it was refused, or null.
    /// </summary>
    public string? StartDeliver(Player p, long stationId)
    {
        if (p.Map is not { } map || !map.Objects.TryGetValue(stationId, out var o) || o is not SmugglerDeliveryStation station) return $"no smuggler stash #{stationId} here";
        if (p.Combat.Down is { } down) return $"knocked down ({down.Kind})";
        if (Carry.Of(p) is not { } carried) return "carries nothing";
        if (carried.Type.Name != SmugglerData.Crate) return $"the stash takes no {carried.Type.Name}";
        if (Busy(p) || Carry.PickingUp(p)) return "a pickup or delivery is under way";
        var at = p.PositionAt(DateTime.UtcNow);
        var reach = Reach(Data?.DeliverRange ?? 5);
        if (Vector2.Distance(at, station.Position) > reach) return string.Create(CultureInfo.InvariantCulture, $"{Vector2.Distance(at, station.Position):0.#} m away (reach {reach:0.#})");
        var seconds = Math.Max(0.1, carried.Type.DeliverSeconds);
        var now = world.GameNow;
        _channels[p] = new Channel(map, station, world.Now + TimeSpan.FromSeconds(seconds), Deliver: true);
        p.SendAround(new DeliverCarriableObjectStartEvent
        {
            ddn = p.Id, b2v = now, b2w = now + SpellCasts.Seconds(seconds), b2x = station.Id, b2y = carried.Type.Index, b2z = carried.Rarity,
            Extra = new() { [4] = carried.Type.Index },
        });
        map.SendToObservers(station, new ReceiveCarriableObjectStartEvent { ddn = station.Id, dsx = carried.Rarity, dsy = carried.Type.Index });
        return null;
    }

    private void FinishDeliver(Player p, Channel ch)
    {
        _channels.Remove(p);
        var station = ch.Target;
        if (Carry.Release(p) is not { } carried)
        {
            p.SendAround(new DeliverCarriableObjectCancelEvent { ddn = p.Id });
            return;
        }
        p.SendAround(new DeliverCarriableObjectFinishedEvent { ddn = p.Id, b2r = carried.Type.Index, b2s = carried.Rarity, Extra = new() { [1] = carried.Type.Index } });
        ch.Map.SendToObservers(station, new ReceiveCarriableObjectFinishedEvent { ddn = station.Id, dst = true, dsu = carried.Rarity, dsv = carried.Type.Index });
        Reward(p, carried);
    }

    /// <summary>
    /// The delivered crate's mightreward (pvpchallenge.xml): its smugglers standing (whole points; a7m raw ×10 000, capped at the max) and its
    /// favor into the FAVOR currency (<see cref="Character.MistFavor"/>, the one FAVOR wallet) [infer: the objective's favor is FAVOR]. The might
    /// (the season's might, PROGRESSION_SMUGGLERS) is not kept on this server.
    /// </summary>
    private void Reward(Player p, Carriables.Carried carried)
    {
        var objective = carried.Info.MightReward is { } name ? Data?.Objectives.GetValueOrDefault(name) : null;
        if (objective is null)
        {
            Log.Info("world", $"'{p.Character.Name}' delivered {carried.Type.Name} rarity {carried.Rarity}: no mightreward objective");
            return;
        }
        var standing = (long)Math.Round(objective.Standing.GetValueOrDefault("smugglers") * 10000);
        var favor = (long)Math.Round(objective.Favor * 10000);
        p.Character.MistFavor += favor;
        var gained = GainStanding(p, standing);
        Expeditions.Tell(p, string.Create(CultureInfo.InvariantCulture,
            $"Smuggler crate delivered: +{gained / 10000.0:N0} Smuggler Standing (now {p.Character.SmugglerStanding / 10000:N0}, rank {RankOf(p.Character.SmugglerStanding)}), +{favor / 10000.0:N0} favor."));
        Log.Info("world", string.Create(CultureInfo.InvariantCulture,
            $"'{p.Character.Name}' delivered {carried.Type.Name} rarity {carried.Rarity}: {objective.Name}, standing +{gained / 10000.0:0.##}, favor +{favor / 10000.0:0.##}"));
    }

    /// <summary><paramref name="p"/> gains <paramref name="add"/> smugglers standing (a7m raw, capped at 10 000 000 points): UpdateStanding (87) {0 1, 1 the total, 2 the gain}. Returns the gain.</summary>
    public long GainStanding(Player p, long add)
    {
        var c = p.Character;
        if (add <= 0) return 0;
        var before = c.SmugglerStanding;
        c.SmugglerStanding = Math.Min(StandingRanks[^1] * 10000, before + add);
        var gained = c.SmugglerStanding - before;
        if (gained > 0) SendStanding(p, gained);
        return gained;
    }

    /// <summary>The rank (1-10) of <paramref name="raw"/> smugglers standing (a7m raw).</summary>
    public static int RankOf(long raw) => Math.Max(1, StandingRanks.Count(min => raw >= min * 10000));

    private static void SendStanding(Player p, long gained) =>
        p.Session.Send(new UpdateStandingEvent { d5y = StandingType, d5z = p.Character.SmugglerStanding, d50 = gained, d51 = 0 });

    /// <summary>After every Join: the standing (the client keeps it in its own dictionary, no Join key fills it), and a stale carry buff off.</summary>
    public void Joined(Player p)
    {
        if (p.Character.SmugglerStanding > 0) SendStanding(p, 0);
        Carry.Joined(p);
    }

    // ---- cancels and the tick ----

    /// <summary>
    /// PickupFromPiledObjectCancel (op 519) / DeliverCarriableObjectCancel (op 305), a knockdown, a cluster change: the pile pickup or the delivery
    /// under way ends; 617 {0, 1 the pile's index} or 337 {0, 1 the carriable index} [infer key 1] to the player and its observers.
    /// </summary>
    public void Cancel(Player p, string why)
    {
        if (!_channels.Remove(p, out var ch)) return;
        if (ch.Target is SmugglerCratePile pile)
        {
            pile.Channeling = Math.Max(0, pile.Channeling - 1);
            p.SendAround(new PickupFromPiledObjectCancelEvent { ddn = p.Id, dmb = pile.Definition.Index });
            if (pile.Map == ch.Map) ch.Map.SendToObservers(pile, pile.BuildUpdate(false));
        }
        else p.SendAround(new DeliverCarriableObjectCancelEvent { ddn = p.Id, b2q = Carry.Of(p)?.Type.Index ?? 0 });
        Log.Info("world", $"'{p.Character.Name}' {(ch.Deliver ? "delivery" : "pile pickup")} cancelled: {why}");
    }

    /// <summary>Every tick: the carried objects (<see cref="Carriables.Update"/>), the channels due end, those of a gone or knocked-down player are cancelled.</summary>
    public void Update()
    {
        Carry.Update();
        if (_channels.Count == 0) return;
        foreach (var (p, ch) in _channels.ToList())
        {
            if (p.Map != ch.Map || p.Combat.Down is not null) Cancel(p, p.Map != ch.Map ? "left the map" : "knocked down");
            else if (!ch.Map.Objects.ContainsKey(ch.Target.Id)) Cancel(p, "the target is gone");
            else if (world.Now >= ch.Due)
            {
                if (ch.Deliver) FinishDeliver(p, ch);
                else FinishPile(p, ch);
            }
        }
    }
}
