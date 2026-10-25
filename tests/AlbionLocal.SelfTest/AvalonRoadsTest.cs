using System.Numerics;
using AlbionLocal.Photon.Serialization;
using AlbionLocal.Server;
using AlbionLocal.Server.Bots;
using AlbionLocal.Server.Content;
using AlbionLocal.Server.Data;
using AlbionLocal.Server.Entities;
using AlbionLocal.Server.Game;
using AlbionLocal.Server.Network;
using AlbionLocal.Server.Persistence;
using AlbionLocal.Server.Protocol;
using AlbionLocal.Server.Systems;
using static AlbionLocal.SelfTest.AlbionTests;

namespace AlbionLocal.SelfTest;

/// <summary>
/// The Roads of Avalon, phase A (analysis/work/stageL/roads/SPEC.md §6, "SPEC" here; the numbers are its assertions): the client tables and
/// spots, the generated network (seeded), NewTunnelExit of a linked open-world portal and an unlinked road spot, the travel through a portal
/// with its charge, window and refusals, the expiry, a restart on the same database, the rescue rule, a road map's fill with a level-6 chest
/// and its guards, and the pve preset's PvP in a road. On FakePeer sessions with a manual clock.
/// </summary>
public static class AvalonRoadsTest
{
    public static IEnumerable<(string, Action)> All =>
    [
        ("roads: tunnelsystem.xml (3 types, 24 exit presets, 18 presets, 12-24 h, 1 h warning, 5 m range), the spots of TNL-001/TNL-201/0204, the exit split, Roads:* config", Data),
        ("roads: the network (royal roads into blue/yellow zones, exits within spots, GROUP open-world links, first-build lifetimes), 448 keys, travel (7 -> 6, key 12, both maps), 1641, 1640, expiry, a level-6 road chest locked until its guards die, restart, rescue, pve PvP", Live),
        ("roads phase B: the road dungeons (fixed exits, levels 2-4, key 10, DUNGEON_RD_TUNNEL), a bot through a portal (a charge, key 12), a portal beat, the 900 s logout, the thrown-out rule, .roads route, 524 on a close", PhaseB),
    ];

    private static ClusterStore Store => Client.Clusters!;

    private static void Check(bool cond, string what) => AlbionTests.Check(cond, what);

    private static int Code(EventData e) => Convert.ToInt32(e.Parameters.GetValueOrDefault(ReservedKeys.EventCode));

    private static readonly long Anchor = DateTime.UtcNow.Ticks;

    private static void Data()
    {
        var data = Store.Roads;
        Check(data.Types.Select(t => (t.Index, t.Name, t.MaxCharges, t.ChargeMin, t.ChargeMax, t.UsedSeconds))
                  .SequenceEqual([(0, "SOLO", 7, 300.0, 300.0, 300.0), (1, "GROUP", 7, 600.0, 600.0, 600.0), (2, "RAID", 20, 1200.0, 1200.0, 1200.0)])
              && data.ExitPresets.Count == 24 && data.Presets.Count == 18 && data.LifetimeMin == 43200 && data.LifetimeMax == 86400 && data.WarningSeconds == 3600
              && data.UsageDistance == 5,
              $"§6.1: SOLO 7/300/300, GROUP 7/600/600, RAID 20/1200/1200, {data.ExitPresets.Count} exit presets, {data.Presets.Count} presets, 43200-86400, 3600, 5 m");
        CriticalMember[] Spots(string id) => [.. Store.Get(id).Critical.Where(m => m.Type == "tunnelexit").OrderBy(m => m.Path, StringComparer.Ordinal)];
        var tnl = Spots("TNL-001");
        var twin = Spots("TNL-201");
        var zone = Spots("0204");
        Check(tnl.Length == 6 && tnl.All(m => m.Tile == "Tunnel_Exit_Inside") && twin.Select(m => m.Path).SequenceEqual(tnl.Select(m => m.Path))
              && zone.Length == 6 && zone.All(m => m.Tile == "Tunnel_Exit") && zone.Select(m => m.Path).SequenceEqual(Enumerable.Range(1, 6).Select(i => $"instanceslot_00/tunnelexit_0{i}"))
              && Store.Archetypes.TunnelPresetOf("TUNNEL_ROYAL") == "TUNNEL_ROYAL" && Store.Archetypes.TunnelPresetOf("SAFEAREA") == "SAFEZONE",
              $"§6.2: TNL-001 6 Tunnel_Exit_Inside spots, TNL-201 the same paths, 0204 6 Tunnel_Exit instanceslot_00/tunnelexit_01-06 (got {tnl.Length}/{twin.Length}/{zone.Length})");
        Check(data.Presets["TUNNEL_ROYAL"].Split(6).SequenceEqual(["OUT_ROYAL_SAFE", "OUT_ROYAL_YELLOW", "OUT_TUNNEL_ROYAL", "INCOMING", "INCOMING", "INCOMING"])
              && data.Presets["TUNNEL_BLACK_MEDIUM"].Split(5).SequenceEqual(["OUT_BLACK_MEDIUM", "OUT_BLACK_MEDIUM", "OUT_TUNNEL_BLACK_MEDIUM", "INCOMING", "INCOMING"])
              && Store.Get("TNL-001").Name.Biome == "Roads",
              "§6.2: the split TUNNEL_ROYAL 6 = [1, 1, 1, 3], TUNNEL_BLACK_MEDIUM 5 = [2, 1, 2]; the RO biome token is Roads");
        var cfg = NewDir();
        var none = Path.Combine(cfg, "albionlocal.json");
        Check(LoadTestConfig(none, ["--Preset=pve"]).Roads == new RoadsOptions { Command = RoadsCommandAccess.All, Rescue = true, LogoutSeconds = null, ThrowOutAfterMinutes = 0 }
              && LoadTestConfig(none, ["--Preset=official"]).Roads == new RoadsOptions() && LoadTestConfig(none, ["--Preset=official", "--Roads:LogoutSeconds=world"]).Roads.LogoutSeconds is null
              && LoadTestConfig(none, ["--Roads:LifetimeMinutes=70-70", "--Roads:Charges:GROUP:MaxCharges=3", "--Roads:Layers=ROYAL"]).Roads is
                  { LifetimeMinutes: (70, 70), Layers: ["ROYAL"] } r && r.Charges["GROUP"].MaxCharges == 3 && r.LayerOn("SAFEZONE") && !r.LayerOn("TUNNEL_DEEP")
              && LoadTestConfig(none, []).Bots.RoadsPatrolGroupSize == (2, 3) && BotSettings.Default.Counts["Roads"][BotRole.Patrol] == 2,
              "Roads:* under pve (.roads for all, the world's logout, never thrown out), official (the record's: 900 s, 60 min), explicit keys win; Bots:Roads:PatrolGroupSize 2-3, Counts.Roads");
        var typo = false;
        try { _ = LoadTestConfig(none, ["--Roads:Layers=ROYALS"]); }
        catch (InvalidDataException) { typo = true; }
        Check(typo, "Roads:Layers: an unknown layer (ROYALS) stops the start");
        try { Directory.Delete(cfg, true); } catch (IOException) { }
    }

    private static (World World, ManualClock Clock, SqliteCharacterRepository Characters) NewWorld(ServerConfig config, string dir, ManualClock clock, bool start = true)
    {
        var characters = Repository(dir);
        var world = new World(config, characters, Client.Items, Client.GameData, Client.Spells)
        {
            Sync = new object(), Clock = clock, Clusters = Store, MapSeed = 5, GameTicks = () => Anchor + clock.Now.Ticks,
        };
        world.MobAi = false;
        if (start) lock (world.Sync) world.Roads.Start();
        return (world, clock, characters);
    }

    private static Character Hero(SqliteCharacterRepository characters, string cluster, Vector2 at, byte flag = 0)
    {
        var c = StarterKit.Default.NewCharacter("R" + Guid.NewGuid().ToString("N")[..10]);
        (c.Cluster, c.PositionX, c.PositionY, c.Flag) = (cluster, at.X, at.Y, flag);
        Check(characters.Create(c), "character created");
        return c;
    }

    private static (WorldSession Session, FakePeer Peer) Join(World world, Guid key)
    {
        var peer = new FakePeer();
        var session = new WorldSession(peer, world);
        lock (world.Sync) session.OnRequest(Request(new JoinRequest { ss = key }, null), encrypted: true);
        Check(peer.Responses is [{ ReturnCode: 0 }], "join answered");
        return (session, peer);
    }

    private static List<EventData> Portals(FakePeer peer, int from = 0) => [.. peer.Events.Skip(from).Where(e => Code(e) == (int)EventCodes.NewTunnelExit)];

    private static long Id(EventData e) => Convert.ToInt64(e.Parameters[0]);

    private static byte[] Keys(EventData e) => [.. e.Parameters.Keys.Where(k => k < 250).Order()];

    private static OperationResponse Use(World world, WorldSession s, FakePeer peer, long portal, int requestId)
    {
        lock (world.Sync) s.OnRequest(Request(new ChangeClusterRequest { ee = portal, ef = "" }, requestId), encrypted: false);
        return peer.Responses[^1];
    }

    /// <summary>A point <paramref name="metres"/> in front of <paramref name="m"/> (along its spawnpos direction).</summary>
    private static Vector2 Before(CriticalMember m, float metres) => TunnelExit.ArrivalAt("", m.Position, m.Direction, new Vector2(metres, 0)).Position;

    private static void Live()
    {
        var dir = NewDir();
        var clock = new ManualClock();
        var config = new ServerConfig { JoinByCharacterId = true, Roads = new RoadsOptions { Seed = 11 }, Transfer = new TransferOptions { CheckExitCooldown = false } };
        var (world, _, characters) = NewWorld(config, dir, clock);
        void Tick(double seconds)
        {
            clock.Advance(TimeSpan.FromSeconds(seconds));
            lock (world.Sync) world.Update();
        }
        var roads = world.Roads;
        var now = world.GameNow;
        var group = Store.Roads.TypeNamed("GROUP")!.Index;

        // §6.3: every royal road has its OUT_ROYAL_SAFE linked into a blue or yellow zone; no cluster over its spots; open-world links GROUP; first-build lifetimes ≤ 24 h.
        var royal = roads.Nodes.Values.Where(n => n.Preset.Name == "TUNNEL_ROYAL").ToList();
        Check(roads.Ready && royal.Count == 44 && royal.All(n => n.Slots.Any(s => s.Preset.Name == "OUT_ROYAL_SAFE" && s.Link?.Other(s).Node.Preset.Name is "SAFEZONE" or "YELLOW"))
              && roads.Nodes.Values.All(n => n.Slots.Count <= n.Preset.MaxExits)
              && roads.Links.Where(l => !l.Out.Node.IsRoad || !l.In.Node.IsRoad).All(l => l.Type == group && l.MaxCharges == 7 && l.Charges == 7)
              && roads.Links.All(l => l.Closes >= now && l.Closes <= now + TimeSpan.FromHours(24).Ticks) && roads.Links.Count > 300,
              $"§6.3: {royal.Count} royal roads each linked from OUT_ROYAL_SAFE into a blue/yellow zone, {roads.Links.Count} links, open-world ones GROUP 7/7, all closing within 24 h");

        // A royal road's link into a zone that lasts a while (0204's when it has one): the zone Z, the road R.
        var link = roads.Links.Where(l => l.Out is { Preset.Name: "OUT_ROYAL_SAFE" } && !l.In.Node.IsRoad && l.Closes > now + TimeSpan.FromHours(2).Ticks)
            .OrderBy(l => l.In.Cluster == "0204" ? 0 : 1).ThenBy(l => l.In.Cluster, StringComparer.Ordinal).First();
        var (zone, road) = (link.In.Cluster, link.Out.Cluster);
        var zoneSpot = AvalonRoads.MemberOf(Store.Get(zone), link.In)!;
        var roadSpot = AvalonRoads.MemberOf(Store.Get(road), link.Out)!;
        var roadie = Hero(characters, road, Before(roadSpot, 3));
        var (rs, rpeer) = Join(world, roadie.Id);
        var hero = Hero(characters, zone, Before(zoneSpot, 3));
        var watcherChar = Hero(characters, zone, Before(zoneSpot, 3) + new Vector2(0.5f, 0));
        var farChar = Hero(characters, zone, Before(zoneSpot, 8));
        var (hs, hpeer) = Join(world, hero.Id);
        var (ws, wpeer) = Join(world, watcherChar.Id);
        var (fs, fpeer) = Join(world, farChar.Id);
        var zoneMap = hs.Player!.Map!;
        var roadMap = rs.Player!.Map!;
        var portal = zoneMap.Objects.Values.OfType<TunnelExit>().Single(p => p.Slot == link.In);
        var inside = roadMap.Objects.Values.OfType<TunnelExit>().Single(p => p.Slot == link.Out);

        // §6.4: the zone portal {0, 1, 3, 4, 5, 6, 7, 8, 9, 10} (2 with a rotation; no 11 while full, no 12 before a pass), key 5 GROUP, key 6 the road;
        // an unlinked road spot {0, 1, 3} (2 with a rotation) without keys 4 and 6; no build anywhere with key 4 and no key 6.
        var shown = Portals(hpeer).Single(e => Id(e) == portal.Id);
        var expected = new byte[] { 0, 1, 3, 4, 5, 6, 7, 8, 9, 10 }.Concat(portal.Direction != 0 ? new byte[] { 2 } : []).Order().ToArray();
        var unlinked = roadMap.Objects.Values.OfType<TunnelExit>().Where(p => p.Slot.Link is null).ToList();
        var roadEvents = Portals(rpeer);
        var all = Portals(hpeer).Concat(roadEvents).ToList();
        Check(Keys(shown).SequenceEqual(expected) && Convert.ToInt32(shown.Parameters[5]) == group && shown.Parameters[6] is string target && target == road
              && Convert.ToInt32(shown.Parameters[9]) == 7 && Convert.ToInt32(shown.Parameters[10]) == 7 && shown.Parameters[3] is "Tunnel_Exit"
              && roadMap.Objects.Values.OfType<TunnelExit>().Count() == roads.NodeOf(road)!.Slots.Count
              && (unlinked.Count == 0 || unlinked.All(p => roadEvents.Where(e => Id(e) == p.Id).All(e => !e.Parameters.ContainsKey(4) && !e.Parameters.ContainsKey(6)
                  && Keys(e).SequenceEqual(new byte[] { 0, 1, 3 }.Concat(p.Direction != 0 ? new byte[] { 2 } : []).Order()))))
              && all.All(e => !e.Parameters.ContainsKey(4) || e.Parameters[6] is string { Length: > 0 }),
              $"§6.4: {zone}'s portal to {road}: keys {string.Join(",", Keys(shown))}; {road} shows every spot ({unlinked.Count} unlinked: 0, 1, 3 only); key 4 only with key 6");

        // §6.5: the click within 5 m: {0: road}, the arrival 5.53 m in front of the inside portal, 7 → 6, key 12 = now + 600 s, both maps sent again.
        var (wMark, rMark) = (wpeer.Events.Count, rpeer.Events.Count);
        var used = Use(world, hs, hpeer, portal.Id, 21);
        Character saved;
        lock (world.Sync) saved = world.LoadCharacter(hero.Id)!; // the snapshot the commit saved (its write may still be under way)
        var arrival = new Vector2(saved.PositionX, saved.PositionY);
        var resent = Portals(wpeer, wMark).LastOrDefault(e => Id(e) == portal.Id);
        var resentInside = Portals(rpeer, rMark).LastOrDefault(e => Id(e) == inside.Id);
        Check(used.ReturnCode == 0 && used.Parameters[0] is string to && to == road && saved.Cluster == road && Math.Abs(Vector2.Distance(arrival, roadSpot.Position) - 5.53f) < 0.01f
              && link.Charges == 6 && link.RecentUntil == world.GameNow + TimeSpan.FromSeconds(600).Ticks && link.NextCharge == world.GameNow + TimeSpan.FromSeconds(600).Ticks
              && resent is not null && Convert.ToInt32(resent.Parameters[9]) == 6 && resent.Parameters.ContainsKey(11) && resent.Parameters.ContainsKey(12)
              && resentInside is not null && Convert.ToInt32(resentInside.Parameters[9]) == 6,
              $"§6.5: {{0: {road}}} (RC {used.ReturnCode}), arrival {Vector2.Distance(arrival, roadSpot.Position):0.00} m before the inside portal, charges {link.Charges}, keys 11/12 resent on both maps");

        // §6.5: 0 charges after the window: 1641 and the player stays; 8 m away: 1640.
        Tick(601);
        lock (world.Sync) roads.SetCharges(link, 0);
        var empty = Use(world, ws, wpeer, portal.Id, 22);
        lock (world.Sync) roads.SetCharges(link, 7);
        var far = Use(world, fs, fpeer, portal.Id, 23);
        Check(empty.ReturnCode == (short)ChangeClusterRefusal.ExitNotConnected && ws.Status == SessionStatus.InWorld && wpeer.Events.Any(e => Code(e) == (int)EventCodes.SystemMessage)
              && far.ReturnCode == (short)ChangeClusterRefusal.NotNearAnExit && fs.Status == SessionStatus.InWorld,
              $"§6.5: no charges, not recently used: 1641 with a chat line (got {empty.ReturnCode}); 8 m from the portal: 1640 (got {far.ReturnCode})");

        // §6.7: the link's end: the zone portal leaves, the inside portal is sent inactive, the royal exit links anew at once.
        (wMark, rMark) = (wpeer.Events.Count, rpeer.Events.Count);
        link.Closes = world.GameNow + TimeSpan.FromSeconds(1).Ticks;
        Tick(2);
        Check(wpeer.Events.Skip(wMark).Any(e => Code(e) == (int)EventCodes.Leave && Convert.ToInt64(e.Parameters[0]) == portal.Id) && !zoneMap.Objects.ContainsKey(portal.Id)
              && Portals(rpeer, rMark).Any(e => Id(e) == inside.Id && !e.Parameters.ContainsKey(4) && !e.Parameters.ContainsKey(6))
              && !roads.Links.Contains(link) && link.Out.Link is { } again && again.Id > link.Id,
              $"§6.7: expired: Leave of the zone portal, the inside portal resent with key 4 false, OUT_ROYAL_SAFE linked again ({link.Out.Link})");

        // §6.6: the road map's fill: ROAD camps, nodes, road chest spots; a level-6 chest's guards are the level-6 entries, locked until they die.
        var slots = roadMap.Objects.Values.OfType<Mob>().Select(m => m.Slot).OfType<MobSpawnSlot>().ToList();
        var nodes = roadMap.Objects.Values.OfType<Harvestable>().Count(); // static ones and the resource layout's (none without a layout session)
        var spots = world.WorldChests.SpotsOf(roadMap).Where(s => s.IsRoad).ToList();
        var spot = spots.Where(s => s.Guards.Count > 0).OrderByDescending(s => s.Guards.Count).FirstOrDefault();
        LootChest? chest = null;
        if (spot is not null) lock (world.Sync) chest = world.WorldChests.SpawnRoad(roadMap, spot, 6);
        Check(slots.Any(s => s.Point.Block?.Faction == "ROAD") && nodes > 0 && spot is not null && chest is { LockState: 2 }
              && spot.Guards.All(g => g.Mob is { Dead: false } m && g.Point.Block!.HasAt(m.Template, 6)),
              $"§6.6: {road} fills with {slots.Count(s => s.Point.Block?.Faction == "ROAD")} ROAD points, {nodes} nodes, {spots.Count} road chest spots; "
              + $"a level-6 chest locked ({chest?.LockState}) with {spot?.Guards.Count} level-6 guards");
        lock (world.Sync)
            foreach (var s in slots.Where(s => s.Mob is { Dead: false } && (s.Point.Block?.Faction ?? s.Mob!.Template.Faction) == "ROAD"
                         && Vector2.Distance(s.Point.Position, chest!.Position) <= chest.Template.AlertRadius).ToList())
                SpawnManager.Despawn(s.Mob!, world, respawn: false);
        Tick(1);
        Check(chest!.LockState == 6, $"§6.6: its guards gone, the chest unlocks (ChestState {chest.LockState})");

        // A map filled before the first build (a fresh database: generated, its links made through Shown) has one portal per spot.
        var dir2 = NewDir();
        var (w2, _, c2) = NewWorld(config, dir2, clock, start: false);
        var early = Join(w2, Hero(c2, road, Before(roadSpot, 3)).Id).Session.Player!.Map!;
        lock (w2.Sync) w2.Roads.Start();
        var earlyPortals = early.Objects.Values.OfType<TunnelExit>().ToList();
        Check(earlyPortals.Count == w2.Roads.NodeOf(road)!.Slots.Count && earlyPortals.Select(p => p.Slot).Distinct().Count() == earlyPortals.Count
              && earlyPortals.Count(p => p.Slot.Link is not null) > 0,
              $"a map loaded before the build gets one portal per spot ({earlyPortals.Count} for {w2.Roads.NodeOf(road)!.Slots.Count} spots)");
        w2.Dispose();
        c2.Dispose();
        try { Directory.Delete(dir2, true); } catch (IOException) { }

        // §6.8: a second world on the same database has the same links.
        var before = roads.Links.Select(l => (l.Id, l.Out.Cluster, l.Out.Path, l.In.Cluster, l.In.Path, l.Type, l.Opened, l.Closes, l.Charges)).ToHashSet();
        world.Dispose();
        characters.Dispose();
        var pve = ServerConfig.PveDefaults with
        {
            JoinByCharacterId = true, Roads = new RoadsOptions { Seed = 11, Command = RoadsCommandAccess.All, Rescue = true }, Bots = ServerConfig.PveDefaults.Bots with { Enabled = false },
        };
        (world, _, characters) = NewWorld(pve, dir, clock);
        roads = world.Roads;
        var after = roads.Links.Select(l => (l.Id, l.Out.Cluster, l.Out.Path, l.In.Cluster, l.In.Path, l.Type, l.Opened, l.Closes, l.Charges)).ToHashSet();
        Check(before.IsSubsetOf(after) && after.Count == before.Count, $"§6.8: the restart takes up the same {before.Count} links (got {after.Count}, {before.Except(after).Count()} missing)");

        // §6.10 (Roads:Rescue, the pve preset's): a deep raid road with a player inside and no link gets one within a tick.
        var raid = roads.Nodes.Values.Where(n => n.Preset.Name == "TUNNEL_DEEP_RAID").OrderBy(n => n.Cluster, StringComparer.Ordinal).First();
        lock (world.Sync)
            foreach (var l in raid.Slots.Select(s => s.Link).OfType<RoadLink>().Distinct().ToList()) roads.Close(l);
        var raidSpot = AvalonRoads.MemberOf(Store.Get(raid.Cluster), raid.Slots[0])!;
        var raider = Hero(characters, raid.Cluster, Before(raidSpot, 3));
        _ = Join(world, raider.Id);
        Tick(1.1);
        Check(raid.Slots.Any(s => s.Link is not null), $"§6.10: {raid} with a player and no link gets one within a tick");

        // §6.9 pve: in a road a Blue player is safe from a Red player but not from a road patrol (roads are black zones, where the pve
        // preset's Bots:PvpRulesByZone gives patrols the official rules: the user's choice of 2026-10-18); a Red one is safe from neither.
        var blue = Join(world, Hero(characters, road, Before(roadSpot, 4)).Id).Session.Player!;
        var red = Join(world, Hero(characters, road, Before(roadSpot, 4.5f), Pvp.Red).Id).Session.Player!;
        var red2 = Join(world, Hero(characters, road, Before(roadSpot, 5), Pvp.Red).Id).Session.Player!;
        var at = Before(roadSpot, 6);
        Player? patrol;
        lock (world.Sync) patrol = world.Bots.Spawn(blue.Map!, BotRole.Patrol, 6, at, 0, group: new BotGroup(new PatrolRoute([at], false)));
        Check(world.Bots.ZoneOf(blue.Map!) == "Roads" && Pvp.Of(red, blue) != Relation.Hostile && Pvp.Of(red, red2) == Relation.Hostile
              && patrol is { Flag: Pvp.Red } && Pvp.Of(patrol, blue) == Relation.Hostile && Pvp.Of(patrol, red) == Relation.Hostile,
              $"§6.9 pve: zone class Roads; Red on Blue {Pvp.Of(red, blue)}, Red on Red {Pvp.Of(red, red2)}, the patrol on Blue {(patrol is null ? "-" : Pvp.Of(patrol, blue))}, on Red {(patrol is null ? "-" : Pvp.Of(patrol, red))}");

        // §5.10: .roads for a player under pve (Roads:Command All); the GM's .roads info is refused to him.
        var replies = new List<string>();
        void Say(string line, AccountLevel level)
        {
            lock (world.Sync) world.Commands.Execute(new Server.Commands.CommandContext(world, blue, level, (t, error) => replies.Add((error ? "error: " : "") + t)), line);
        }
        Say(".roads", AccountLevel.Player);
        Say(".roads info", AccountLevel.Player);
        Check(replies.Count > 2 && replies[0].StartsWith($"{road} (TUNNEL_ROYAL)", StringComparison.Ordinal) && replies.Any(r => r.Contains("nearest entrances", StringComparison.Ordinal))
              && replies[^1].StartsWith("error: ", StringComparison.Ordinal),
              $".roads under pve: this road's portals and the nearest entrances; .roads info refused to a player (got {replies.Count} lines, first '{replies.FirstOrDefault()}', last '{replies.LastOrDefault()}')");
        world.Dispose();
        characters.Dispose();
        try { Directory.Delete(dir, true); } catch (IOException) { }
    }

    /// <summary>
    /// Phase B (SPEC §7, its assertions): under the official preset a royal road with fixed dungeon exits and a link into a zone. The road map's
    /// fixed exits (Roads:FixedDungeons over Dungeons:FixedExits off): one per FIXED marker, a ROADS_FIXED_DUNGEONS level 2-4 drawn once, key 10
    /// true, key 4 DUNGEON_RD_TUNNEL; a bot leaving through a portal takes a charge and sends key 12; the portals as a patrol beat; the 900 s
    /// logout in a road; a character offline 61 min in the road logs in at its respawn city with the ThrownOutAvalonRoads loss (30 minutes:
    /// stays); .roads route marks the first portal; a zone portal closing in view leaves 524 for Roads:TempPortalSeconds.
    /// </summary>
    private static void PhaseB()
    {
        var dir = NewDir();
        var clock = new ManualClock();
        var config = new ServerConfig { JoinByCharacterId = true, Roads = new RoadsOptions { Seed = 11 }, Transfer = new TransferOptions { CheckExitCooldown = false } };
        _ = Store.Dungeons; // the map fill takes the exits from randomdungeons.xml
        var (world, _, characters) = NewWorld(config, dir, clock);
        void Tick(double seconds)
        {
            clock.Advance(TimeSpan.FromSeconds(seconds));
            lock (world.Sync) world.Update();
        }
        var roads = world.Roads;
        var now = world.GameNow;
        var link = roads.Links.Where(l => l.Out is { Preset.Name: "OUT_ROYAL_SAFE" } && !l.In.Node.IsRoad && l.Closes > now + TimeSpan.FromHours(2).Ticks)
            .OrderBy(l => l.Out.Cluster, StringComparer.Ordinal).Take(6).OrderBy(l => Store.Get(l.Out.Cluster).DungeonMarkers.Any(m => m.Type == "FIXED") ? 0 : 1).First();
        var (zone, road) = (link.In.Cluster, link.Out.Cluster);
        var roadSpot = AvalonRoads.MemberOf(Store.Get(road), link.Out)!;
        var zoneSpot = AvalonRoads.MemberOf(Store.Get(zone), link.In)!;
        var (rs, rpeer) = Join(world, Hero(characters, road, Before(roadSpot, 3)).Id);
        var roadMap = rs.Player!.Map!;

        // §7.1: one fixed exit per FIXED marker of the road's active layers, levels 2-4 (single roll), key 10 true, key 4 DUNGEON_RD_TUNNEL.
        var markers = roadMap.Template!.DungeonMarkers.Count(m => m.Type == "FIXED");
        var fixedExits = world.Dungeons.ExitsOf(roadMap).Where(e => e.Info.Fixed).ToList();
        var shown = fixedExits.Select(e => e.BuildCreate(rs.Player!).OfType<NewRandomDungeonExitEvent>().Single()).ToList();
        var group = fixedExits.FirstOrDefault() is { } first ? world.Dungeons.GroupFor(rs.Player!, first) : null;
        // Key 5 true with the marker's own tile in key 6, no key 7 (cjb.aw: a visualization lookup of 0 fails, no tile); key 2 the tile's rotation.
        var placed = fixedExits.Zip(shown).All(x => x.Second.c8s && x.Second.c8t == x.First.Marker!.Tile && x.Second.c8t!.StartsWith("ROADS_FIXED_EXIT_", StringComparison.Ordinal)
            && x.Second.c8u == 0 && MathF.Abs(x.Second.c8p - (x.First.Marker.Direction % 360 + 360) % 360) < 0.01f);
        Check(markers > 0 && fixedExits.Count == markers && fixedExits.All(e => e.Name.Contains("_ROAD_", StringComparison.Ordinal) && e.Level is >= 2 and <= 4
                  && e.Reroll is { SingleRoll: true } && e.SubtypeName is "SOLO" or "VETERAN" or "ELITE")
              && shown.All(e => e.c8x && e.c8r == "DUNGEON_RD_TUNNEL") && placed && group is { ClusterType: "DUNGEON_RD_TUNNEL" },
              $"§7.1: {road} has {fixedExits.Count} road dungeon exits for {markers} fixed markers (levels {string.Join(",", fixedExits.Select(e => e.Level))}), "
              + $"key 10 true, key 4 {shown.FirstOrDefault()?.c8r}, key 5 + own tile {placed} ({shown.FirstOrDefault()?.c8t}), the dungeon {group?.ClusterType}");

        // §7.2: a bot leaves through the road's portal: 7 -> 6 charges, key 12 to the player; at 1 charge no bot may use it.
        lock (world.Sync) roads.SetCharges(link, 7);
        var portal = roadMap.Objects.Values.OfType<TunnelExit>().Single(p => p.Slot == link.Out);
        var nav = world.Bots.Nav.Preload(roadMap.Template);
        Player? bot;
        lock (world.Sync) bot = world.Bots.Spawn(roadMap, BotRole.Patrol, 6, Before(roadSpot, 2), 0, group: new BotGroup(new PatrolRoute([Before(roadSpot, 2)], false)));
        var exits = world.Bots.ExitsOf(roadMap, nav);
        var mark = rpeer.Events.Count;
        bool left;
        lock (world.Sync) left = world.Bots.LeaveThrough(bot!, portal.Position);
        var resent = Portals(rpeer, mark).LastOrDefault(e => Id(e) == portal.Id);
        var charges = link.Charges;
        lock (world.Sync) roads.SetCharges(link, 1);
        var stops = world.Bots.PortalStops(roadMap, nav, null);
        var beat = PatrolRoute.Pick(nav, roadMap.Template, new Random(3), null, stops);
        Check(world.Bots.ZoneOf(roadMap) == "Roads" && exits.Any(e => e.Position == portal.Position) && left && bot!.Map is null && charges == 6
              && resent is not null && Convert.ToInt32(resent.Parameters[9]) == 6 && resent.Parameters.ContainsKey(12)
              && !world.Bots.ExitsOf(roadMap, nav).Any(e => e.Position == portal.Position)
              && stops.Count >= 2 && beat is { Loop: true } && beat.Portals.Count == beat.Stops.Count,
              $"§7.2: a bot through the portal: gone ({left}), 7 -> {charges} resent with key 12 ({resent is not null}); at 1 charge not a bot's exit; a beat of {stops.Count} portal stops, nav {nav.Summary()}");

        // §7.2: a gank group passes as one: at 2 charges the first member takes one (1 left, no bot's exit any more), the second follows free.
        lock (world.Sync) roads.SetCharges(link, 2);
        var gank = new BotGroup(new PatrolRoute([Before(roadSpot, 2)], false));
        var both = new List<bool>();
        lock (world.Sync)
            foreach (var _ in new[] { 0, 1 })
            {
                var member = world.Bots.Spawn(roadMap, BotRole.Patrol, 6, Before(roadSpot, 2), 0, group: gank)!;
                both.Add(world.Bots.LeaveThrough(member, portal.Position, gank));
            }
        Check(both is [true, true] && link.Charges == 1 && world.Bots.ExitsOf(roadMap, nav, gank).Any(e => e.Position == portal.Position),
              $"§7.2: a gank group of 2 through the portal at 2 charges: passed {string.Join(",", both)}, {link.Charges} left, still the group's way inside its window");

        // §7.3: the official logout in a road takes 900 s (elsewhere World:LogoutSeconds).
        Check(world.LogoutTimeIn(roadMap) == TimeSpan.FromSeconds(900) && world.LogoutTimeIn(null) == world.LogoutTime,
              $"§7.3: logout in {road} {world.LogoutTimeIn(roadMap).TotalSeconds} s");

        // §7.3: offline 61 minutes in the road: in the respawn city with the loss (equipment kept, worn; a third of 30 ore gone); 30 minutes: stays.
        Character Away(double minutes)
        {
            var c = StarterKit.Default.NewCharacter("R" + Guid.NewGuid().ToString("N")[..10]);
            var at = Before(roadSpot, 4);
            (c.Cluster, c.PositionX, c.PositionY, c.LastLeftAt) = (road, at.X, at.Y, world.GameNow - TimeSpan.FromMinutes(minutes).Ticks);
            c.Inventory = [new InventoryItem("T4_ORE", 30)];
            Check(characters.Create(c), "character created");
            return c;
        }
        var gone = Away(61);
        var stay = Away(30);
        var (gs, gpeer) = Join(world, gone.Id);
        var (ss, _) = Join(world, stay.Id);
        var back = gs.Player!.Character;
        var ore = back.Inventory.Where(i => i?.Name == "T4_ORE").Sum(i => i!.Count);
        Check(back.Cluster == Death.RespawnCluster(world, back) && back.Cluster != road && ss.Player!.Character.Cluster == road
              && back.Equipment.Where(i => i is not null).Select(i => i!.Name).SequenceEqual(gone.Equipment.Where(i => i is not null).Select(i => i!.Name))
              && back.Equipment.Any(i => i?.Durability is not null) && ore is 20 or 21
              && gpeer.Events.Any(e => Code(e) == (int)EventCodes.SystemMessage),
              $"§7.3: 61 min offline in {road}: joins in {back.Cluster} with its equipment (worn), {ore} of 30 ore and a chat line; 30 min: still in {ss.Player?.Character.Cluster}");

        // §7.3: 61 minutes online in the road (LastLeftAt = its road entry) and a reconnect while the old connection holds it: not thrown out.
        var online = Hero(characters, road, Before(roadSpot, 5)).Id;
        var (os, _) = Join(world, online);
        lock (world.Sync) os.Player!.Character.LastLeftAt = world.GameNow - TimeSpan.FromMinutes(61).Ticks;
        bool selected;
        lock (world.Sync) selected = roads.ThrowOutIfStale(world.LoadCharacter(online)!.Clone());
        var (again, _) = Join(world, online);
        Check(!selected && again.Player!.Character.Cluster == road,
              $"§7.3: online 61 min in {road} and reconnecting: thrown out at SelectCharacter {selected}, joins in {again.Player?.Character.Cluster}");

        // §7.6: .roads route from the zone marks the portal to take there.
        var (zs, zpeer) = Join(world, Hero(characters, zone, Before(zoneSpot, 3)).Id);
        var zoneMap = zs.Player!.Map!;
        var outer = zoneMap.Objects.Values.OfType<TunnelExit>().Single(p => p.Slot == link.In);
        var replies = new List<string>();
        lock (world.Sync)
            world.Commands.Execute(new Server.Commands.CommandContext(world, zs.Player, AccountLevel.GameMaster, (t, error) => replies.Add((error ? "error: " : "") + t)), $".roads route {road}");
        Check(replies.Count >= 2 && replies[0].StartsWith("1 portal(s): ", StringComparison.Ordinal) && replies[1].Contains(">>>", StringComparison.Ordinal),
              $"§7.6: .roads route {road}: '{replies.FirstOrDefault()}' / '{replies.Skip(1).FirstOrDefault()}'");

        // §7.5: the zone portal closing in view leaves NewTunnelExitTemp 524 on its spot, gone after Roads:TempPortalSeconds (5 s).
        mark = zpeer.Events.Count;
        lock (world.Sync) roads.Close(link);
        var temp = zpeer.Events.Skip(mark).FirstOrDefault(e => Code(e) == (int)EventCodes.NewTunnelExitTemp);
        var tempId = temp is null ? -1 : Id(temp);
        Tick(6);
        Check(temp is not null && temp.Parameters[3] is "Tunnel_Exit_Temp" && temp.Parameters[1] is float[] { Length: 2 } xz && xz[0] == outer.Position.X && xz[1] == outer.Position.Y
              && zpeer.Events.Skip(mark).Any(e => Code(e) == (int)EventCodes.Leave && Convert.ToInt64(e.Parameters[0]) == outer.Id)
              && zpeer.Events.Skip(mark).Any(e => Code(e) == (int)EventCodes.Leave && Convert.ToInt64(e.Parameters[0]) == tempId) && !zoneMap.Objects.ContainsKey(tempId),
              $"§7.5: the closing portal leaves 524 Tunnel_Exit_Temp at its spot ({temp is not null}), gone after 5 s");
        world.Dispose();
        characters.Dispose();
        try { Directory.Delete(dir, true); } catch (IOException) { }
    }
}
