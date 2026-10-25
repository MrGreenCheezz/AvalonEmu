using System.Numerics;
using AlbionLocal.Photon.Serialization;
using AlbionLocal.Server;
using AlbionLocal.Server.Bots;
using AlbionLocal.Server.Data;
using AlbionLocal.Server.Entities;
using AlbionLocal.Server.Game;
using AlbionLocal.Server.Network;
using AlbionLocal.Server.Persistence;
using AlbionLocal.Server.Protocol;
using AlbionLocal.Server.Systems;
using static AlbionLocal.SelfTest.AlbionTests;
using static AlbionLocal.SelfTest.MistsTest;

namespace AlbionLocal.SelfTest;

/// <summary>
/// The Mists, phase B (analysis/work/stageL/mists/SPEC.md §8, "SPEC" here; light: one check per item): Knightfall Abbey's layout (B1); then on
/// FakePeer sessions with a manual clock: its portal wave in a zone and the entry (B2), a boss chest and the way out (B3), the standing and the
/// City Trader's gate (B6), a portal wisp (B4), a Drifter of the population pass (B7); Brecilien's static entrance (B5); the official camp
/// free-for-all (B8).
/// </summary>
public static class MistsBTest
{
    public static IEnumerable<(string, Action)> All =>
    [
        ("mists B: Knightfall Abbey's layout (124 room flags, 3 boss rooms 90 m apart, layers), the static entrance's mode ids, the presets", Data),
        ("mists B: a Knightfall Abbey wave, its entry, a boss chest unlocked by its boss, the standing and the trader's gate, the exit 538, a wisp's portal, a Drifter", Abbey),
        ("mists B: Brecilien's static entrance (op 477: 8 m, the push after 3 s, the 300 s block) and the official camp free-for-all (544, Hostile)", Brecilien),
    ];

    private static ClusterStore Store => Client.Clusters!;

    private static void Check(bool cond, string what) => AlbionTests.Check(cond, what);

    private static void Data()
    {
        var data = Store.Mists;
        var mode = data.Modes["MISTS_DUNGEON_SOLO_YELLOW"];
        var map = MistsGenerator.PickMap(data, mode, 0, new Random(3), tier: 6)!;
        var meta = data.DungeonMetas[map.Master];
        var layout = MistsLayout.Read(MistsGenerator.Dungeon(Store, data, mode, map, "UNDEAD", MistsIds.InstanceId(Guid.NewGuid(), mode.Type), new Random(7)).Write());
        var flags = layout.Templates is [{ Rooms: { } f }] ? f : [];
        var bosses = Enumerable.Range(0, flags.Length).Where(i => (flags[i] & MistsGenerator.RoomBoss) != 0).ToList();
        var layers = layout.Templates[0].Layers;
        bool OneSet(byte b) => (b & 7) is 1 or 2 or 4;
        bool OneKind(byte b) => (b & (MistsGenerator.RoomMobs | MistsGenerator.RoomBoss)) is MistsGenerator.RoomMobs or MistsGenerator.RoomBoss;
        Check(map.Tier == 6 && flags.Length == 124 && bosses.Count == 3 && flags.All(b => OneSet(b) && OneKind(b))
              && bosses.All(a => bosses.All(b => a == b || Vector2.Distance(meta.Rooms[a].Centre, meta.Rooms[b].Centre) >= mode.BossRoomRadius))
              && layers.Count == 248 && bosses.All(i => layers.Contains($"R#{i:000}_BOSS")) && layers.Count(l => l.EndsWith("_MOBS", StringComparison.Ordinal)) == 121
              && ClusterNameInfo.Parse(layout.Name) is { Type: 22, Tier: 6 } && layout.Id.StartsWith(MistsIds.DungeonPrefix, StringComparison.Ordinal) && layout.MiniMap.Preset == "Mists",
              $"Knightfall Abbey T{map.Tier}: {flags.Length} room flags, boss rooms {string.Join(", ", bosses)} (90 m apart), one set and one kind each, {layers.Count} layers, {layout.Name}");
        // B (SPEC §8 item 3): a resource zone: "@RESOURCEMISTS@", ClusterType 23, its MistsPlayerSpawnpoint arrivals and wisp spawns.
        var fiber = data.Modes["MISTS_RESOURCE_FIBER"];
        var resource = Store.FromLayout(MistsGenerator.Zone(Store, data, fiber, MistsGenerator.PickMap(data, fiber, 0, new Random(5))!, 0, "UNDEAD",
            MistsIds.InstanceId(Guid.NewGuid(), fiber.Type), new Random(5)));
        Check(resource.Info.Id.StartsWith(MistsIds.ResourcePrefix, StringComparison.Ordinal) && ClusterNameInfo.Parse(resource.Info.File) is { Type: 23 }
              && resource.MistsSpots.Count(x => x.Kind == "MistsSpawnpoint") >= 4 && resource.MistsSpots.Any(x => x.Kind == "mistswispspawn") && fiber.ExitsLeadTo == "MISTS_SOLO_BLACK",
              $"a resource zone {resource.Info.File}: {resource.MistsSpots.Count(x => x.Kind == "MistsSpawnpoint")} arrivals, {resource.MistsSpots.Count(x => x.Kind == "mistswispspawn")} wisp spawn(s)");
        Check(data.StaticMode(0)?.Name == "MISTS_SOLO_YELLOW" && data.StaticMode(1)?.Name == "MISTS_SOLO_YELLOW" && data.StaticMode(2)?.Name == "MISTS_SOLO_BLACK"
              && data.StaticMode(5)?.Name == "MISTS_DUO_BLACK" && data.StaticMode(7) is null
              && LoadTestConfig(Path.Combine(NewDir(), "none.json"), ["--Preset=pve"]).Mists is { StandingFactor: 2, StaticEntrance: true, DrifterNeedsRed: true }
              && LoadTestConfig(Path.Combine(NewDir(), "none.json"), ["--Preset=official"]).Mists.StandingFactor == 1,
              "op 477's mode id names the open mode 0- or 1-based (0/1 yellow, 2 black, 5 duo, 7 none); Mists:StandingFactor 2 under pve, 1 official");
    }

    /// <summary>A world of the record's config with the given mists options and Pvp:Mode, on the real client data.</summary>
    private static (World World, ManualClock Clock, string Dir, SqliteCharacterRepository Characters) World(MistsOptions options, PvpMode pvp, int seed)
    {
        var dir = NewDir();
        var characters = Repository(dir);
        var clock = new ManualClock();
        var anchor = DateTime.UtcNow.Ticks;
        var world = new World(new ServerConfig { JoinByCharacterId = true, Mists = options, PvpMode = pvp }, characters, Client.Items, Client.GameData, Client.Spells)
        {
            Sync = new object(), Clock = clock, Clusters = Store, MapSeed = seed, GameTicks = () => anchor + clock.Now.Ticks,
        };
        world.MobAi = false;
        return (world, clock, dir, characters);
    }

    private static void Done(World world, SqliteCharacterRepository characters, string dir)
    {
        world.Dispose();
        characters.Dispose();
        try { Directory.Delete(dir, true); } catch (IOException) { }
    }

    /// <summary>The answer to a ChangeCluster the build of the next zone defers (through World.Post).</summary>
    private static OperationResponse Answer(World world, ManualClock clock, FakePeer peer, int before)
    {
        world.Mists.Building().Wait(TimeSpan.FromSeconds(60));
        for (var i = 0; i < 300 && peer.Responses.Count == before; i++)
        {
            Thread.Sleep(10);
            Tick(world, clock, 0.01);
        }
        return peer.Responses[^1];
    }

    private static void Abbey()
    {
        var (world, clock, dir, characters) = World(ServerConfig.PveDefaults.Mists, PvpMode.Consensual, 31);
        var t5 = RoyalT5();
        var spot = t5.DungeonMarkers.First(m => m.Type == "SOLO").Position + new Vector2(3, 0);
        var hero = Hero(characters, t5.Id, spot);
        var (s, _, _) = Join(world, hero.Id);
        Enter(world, clock, s, Portal(world, t5.Id, spot, "MISTS_SOLO_YELLOW", 0), 1);
        var open = world.Mists.Zones.Single();
        var (s1, _, _) = Join(world, hero.Id);
        var me = s1.Player!;

        // B2: the first wave comes within initial..final block time x BlockFactor (60..300 s x 0.5): a 324 of MISTS_DUNGEON_SOLO_YELLOW; its entry.
        Check(open.NextWave is { } wave && wave <= open.Created + TimeSpan.FromSeconds(150), $"the first Knightfall Abbey wave within 150 s (at {(open.NextWave - open.Created)?.TotalSeconds:0} s)");
        Tick(world, clock, 151);
        var portal = me.Map!.Objects.Values.OfType<RandomDungeonExit>().FirstOrDefault(e => e.Info.MistsMode == "MISTS_DUNGEON_SOLO_YELLOW");
        var shown = portal?.BuildCreate(me).OfType<NewRandomDungeonExitEvent>().FirstOrDefault();
        Check(portal is not null && shown is { c83: "MISTS_DUNGEON_SOLO_YELLOW", c84: -1, c8r: null } && portal.Tier is >= 5 and <= 7 && portal.Prefab.StartsWith("SHARED_MIST_DUNGEON_ENTRANCE_", StringComparison.Ordinal),
              $"a wave's portal: 324 {{16 MISTS_DUNGEON_SOLO_YELLOW, 17 -1, no 4}}, T{portal?.Tier}, prefab {portal?.Prefab}");
        Move(world, s1, portal!.Position + new Vector2(2, 0));
        Enter(world, clock, s1, portal, 2);
        var (s2, peer2, join2) = Join(world, hero.Id);
        var abbey = world.Mists.Find(join2.a8i);
        var inside = s2.Player!;
        var doors = inside.Map!.Objects.Values.OfType<MistsDungeonExit>().Count();
        Check(abbey is { Mode.Name: "MISTS_DUNGEON_SOLO_YELLOW" } z && join2.a8i?.StartsWith(MistsIds.DungeonPrefix, StringComparison.Ordinal) == true && z.Tier == portal.Tier
              && inside.Character.DungeonReturn?.Cluster == t5.Id && doors == 14,
              $"entered {join2.a8i} (T{abbey?.Tier}), way back {inside.Character.DungeonReturn?.Cluster}, {doors} exits 538");

        // B3: a boss chest stays BossLocked until its boss dies; B6: the kill's fame raises the standing (87 {0 0, 1 total}).
        me = s2.Player!;
        var map = me.Map!;
        var locked = map.Objects.Values.OfType<LootChest>().Where(c => RandomDungeons.IsBossChest(c.SpawnpointName) && c.LockState == RandomDungeons.BossLocked).ToList();
        var chest = locked.FirstOrDefault();
        var boss = chest is null ? null : map.Objects.Values.OfType<Mob>().Where(m => m.Template.Category == "boss" && !m.Dead).MinBy(m => Vector2.Distance(m.Position, chest.Position));
        var early = map.Objects.Values.OfType<LootChest>().Count(c => c.SpawnpointName.Contains("_REWARD", StringComparison.Ordinal));
        Check(locked.Count >= 1 && boss is not null && Vector2.Distance(boss.Position, chest!.Position) < 20 && abbey!.RewardChests.Count > 0 && early == 0,
              $"{locked.Count} boss chest(s) BossLocked, the nearest boss {boss?.Template.Name}; {abbey?.RewardChests.Count} room chest(s) planned, {early} out before their room is cleared");
        Move(world, s2, boss!.Position + new Vector2(2, 0));
        lock (world.Sync) Combat.Damage(me, boss, 1e9, EffectType.Physical, HitOrigin.Direct, 0, world.GameNow);
        var standing = peer2.Events.LastOrDefault(e => Code(e) == (int)EventCodes.UpdateStanding);
        var token = Client.Items.Find("QUESTITEM_TOKEN_MISTS")!.Recipes[0];
        string? low, rich;
        bool capped;
        lock (world.Sync)
        {
            low = world.Mists.TraderRefusal(me, token, 1);
            me.Character.MistStanding = 150_000L * 10000;
            me.Character.MistFavor = 675L * 10000;
            rich = world.Mists.TraderRefusal(me, token, 1);
            // At the standing's cap the favour still comes.
            var (standing0, favour0) = (me.Character.MistStanding, me.Character.MistFavor);
            me.Character.MistStanding = Mists.StandingRanks[^1] * 10000;
            world.Mists.GainStanding(me, 10_000L * 10000);
            capped = me.Character.MistFavor > favour0 && me.Character.MistStanding == Mists.StandingRanks[^1] * 10000;
            (me.Character.MistStanding, me.Character.MistFavor) = (standing0, favour0);
        }
        Check(chest!.LockState == 6 && standing is not null && Convert.ToInt32(standing.Parameters[0]) == 0 && Convert.ToInt64(standing.Parameters[2]) > 0
              && low is not null && rich is null && token.Standing is ("mistcity", 150000) && token.Currencies is [("FAVOR", 675)] && capped,
              $"the boss slain: its chest unlocked (state {chest.LockState}); 87 {{0 MistCity, 2 +{standing?.Parameters.GetValueOrDefault((byte)2)}}}; the token refused below 150 000 ({low}), sold at it; favour past the cap {capped}");

        // B3: an exit 538 leads into a zone of exitsleadtomode (MISTS_SOLO_YELLOW), answered {0, 3, 4, 5}.
        Tick(world, clock, 11);
        var exit = map.Objects.Values.OfType<MistsDungeonExit>().First();
        Move(world, s2, exit.Position);
        var n2 = peer2.Responses.Count;
        lock (world.Sync) s2.OnRequest(Request(new ChangeClusterRequest { ee = exit.Id }, 12), encrypted: false);
        var outward = Answer(world, clock, peer2, n2);
        Check(outward is { ReturnCode: 0 } && outward.Parameters[0] is string to && to.StartsWith(MistsIds.Prefix, StringComparison.Ordinal) && outward.Parameters[4] is "MISTS_SOLO_YELLOW",
              $"the exit 538: {{0 {outward.Parameters.GetValueOrDefault((byte)0)}, 4 {outward.Parameters.GetValueOrDefault((byte)4)}}}");

        // B4: a portal wisp near the player dies after its 4 s (the suicide) and leaves a 324 of its rarity; entering it gives a zone of that rarity.
        var (s3, _, join3) = Join(world, hero.Id);
        me = s3.Player!;
        var next = world.Mists.Find(join3.a8i)!;
        Mob? wisp;
        lock (world.Sync) wisp = world.Mists.SpawnWisp(next, me.Map!, next.Mode, 2, me.PositionAt(DateTime.UtcNow) + new Vector2(3, 0), next.Mode);
        Tick(world, clock, 0.1);
        Tick(world, clock, 4.1);
        var left = me.Map!.Objects.Values.OfType<RandomDungeonExit>().FirstOrDefault(e => e.Info.MistsMode == "MISTS_SOLO_YELLOW" && Vector2.Distance(e.Position, wisp!.Position) < 0.1f);
        Check(wisp is { Dead: true } && left is { Level: 2, Frozen: true }, $"the wisp {wisp?.Template.Name} burst (dead {wisp?.Dead}) into a portal of rarity {left?.Level}");
        var zones = world.Mists.Zones.ToHashSet();
        Enter(world, clock, s3, left!, 3);
        var rare = world.Mists.Zones.FirstOrDefault(z => !zones.Contains(z));
        Check(rare is { Level: 2, Mode.Name: "MISTS_SOLO_YELLOW" }, $"the wisp's portal leads into a zone of rarity {rare?.Level}");

        // B7: in pve a private zone with no Red player gets no Drifter from the population pass; a Red player does (on a route of the zone's walk
        // grid), and it is Hostile to him, never to a Blue one; the Red player Blue again, it goes.
        var (s4, _, _) = Join(world, hero.Id);
        me = s4.Player!;
        var zoneMap = me.Map!;
        var blue = Join(world, Hero(characters, zoneMap.Cluster, me.PositionAt(DateTime.UtcNow) + new Vector2(0, 3)).Id).Session.Player!;
        Player? Drifter() { lock (world.Sync) return zoneMap.Bots.Values.FirstOrDefault(b => b.Bot?.Role == BotRole.Drifter); }
        void Passes(int seconds, Func<bool> until)
        {
            for (var i = 0; i < seconds * 2 && !until(); i++)
            {
                Thread.Sleep(10);
                Tick(world, clock, 0.5);
            }
        }
        lock (world.Sync) world.Bots.Enabled = true;
        Passes(60, () => { lock (world.Sync) return world.Bots.NavOf(zoneMap) is { Usable: true }; });
        Passes(6, () => false);
        var before = Drifter();
        lock (world.Sync) me.Character.Flag = Pvp.Red;
        Passes(30, () => Drifter() is not null);
        var drifter = Drifter();
        Relation red, safe;
        lock (world.Sync) (red, safe) = drifter is null ? (Relation.Neutral, Relation.Neutral) : (BotPvp.Of(drifter, me), BotPvp.Of(drifter, blue));
        lock (world.Sync) me.Character.Flag = Pvp.Blue;
        Passes(6, () => Drifter() is null);
        Check(world.Bots.ZoneOf(zoneMap) == "Mists" && before is null && drifter is { Flag: Pvp.Red } && red == Relation.Hostile && safe != Relation.Hostile && Drifter() is null
              && zoneMap.Objects.Values.OfType<MistsUnrestrictedZone>().Count() == 0,
              $"zone class Mists ({world.Bots.NavOf(zoneMap)?.Summary()}); Blue: no Drifter; Red: {drifter?.Character.Name}, {red} to him, {safe} to the Blue one; Blue again: gone; no 544 under pve");
        Done(world, characters, dir);
    }

    private static void Brecilien()
    {
        // B5: op 477 out of 8 m refused; within: {0 now + 3 s}, the push after 3 s, a second use within 300 s refused.
        var (world, clock, dir, characters) = World(ServerConfig.PveDefaults.Mists with { UnrestrictedZones = true }, PvpMode.Consensual, 33);
        var city = Store.Get("5001");
        var gate = city.Critical.First(m => m.Type == "mistsstaticentrance").Position;
        var hero = Hero(characters, "5001", gate + new Vector2(20, 0));
        var (s, peer, _) = Join(world, hero.Id);
        var entrance = s.Player!.Map!.Objects.Values.OfType<MistsStaticEntrance>().Single();
        lock (world.Sync) s.OnRequest(Request(new MistsUseStaticEntranceRequest { wf = world.GameNow, vx = entrance.Id, vy = 0 }, 1), encrypted: false);
        var far = peer.Responses[^1];
        Move(world, s, gate + new Vector2(3, 0));
        lock (world.Sync) s.OnRequest(Request(new MistsUseStaticEntranceRequest { wf = world.GameNow, vx = entrance.Id, vy = 0 }, 2), encrypted: false);
        var near = peer.Responses[^1];
        var stamp = near.Parameters.GetValueOrDefault((byte)0) is { } v ? Convert.ToInt64(v) : 0;
        world.Mists.Building().Wait(TimeSpan.FromSeconds(60));
        Tick(world, clock, 3.1);
        var push = peer.Responses[^1];
        Check(far.ReturnCode != 0 && near.ReturnCode == 0 && Math.Abs(stamp - (world.GameNow - TimeSpan.FromSeconds(3.1).Ticks + TimeSpan.FromSeconds(3).Ticks)) < TimeSpan.TicksPerSecond / 2
              && push.Parameters[0] is string id && id.StartsWith(MistsIds.Prefix, StringComparison.Ordinal) && push.Parameters[4] is "MISTS_SOLO_YELLOW" && !push.Parameters.ContainsKey(ReservedKeys.RequestId),
              $"477 at 20 m refused ({far.ReturnCode}); at 3 m {{0 the stamp + 3 s}}; then the push {{0 {push.Parameters.GetValueOrDefault((byte)0)}, 4 MISTS_SOLO_YELLOW}}");
        var (s2, _, _) = Join(world, hero.Id);
        var zone = world.Mists.Find(s2.Player!.Map!.Cluster)!;
        lock (world.Sync) world.Mists.Close(zone, "test");
        var (s3, peer3, back) = Join(world, hero.Id);
        lock (world.Sync) s3.OnRequest(Request(new MistsUseStaticEntranceRequest { wf = world.GameNow, vx = s3.Player!.Map!.Objects.Values.OfType<MistsStaticEntrance>().Single().Id, vy = 0 }, 3), encrypted: false);
        Check(back.a8i == "5001" && Vector2.Distance(back.a8j, gate) < 8 && peer3.Responses[^1].ReturnCode != 0 && world.Mists.StaticBlockedUntil(s3.Player) is not null
              && !world.Mists.CampsUnrestricted,
              $"back in 5001 at the entrance ({back.a8j}); a second use within 300 s refused; Consensual keeps the camps restricted (UnrestrictedZones true)");
        Done(world, characters, dir);

        // B8: the official preset: a zone's camps send 544; two Blue players in the circle of a camp whose quest is under way are Hostile.
        var (off, oclock, odir, ochars) = World(new MistsOptions(), PvpMode.Official, 35);
        var t5 = RoyalT5();
        var spot = t5.DungeonMarkers.First(m => m.Type == "SOLO").Position + new Vector2(3, 0);
        var a = Hero(ochars, t5.Id, spot);
        var (sa, _, _) = Join(off, a.Id);
        Enter(off, oclock, sa, Portal(off, t5.Id, spot, "MISTS_SOLO_YELLOW", 0), 1);
        var (sa2, _, _) = Join(off, a.Id);
        var pa = sa2.Player!;
        var circle = pa.Map!.Objects.Values.OfType<MistsUnrestrictedZone>().FirstOrDefault();
        Check(circle is not null && circle.BuildCreate(pa).OfType<NewUnrestrictedPvpZoneEvent>().Single() is { dc4: > 0 } shown && shown.dc4 == circle.Radius,
              $"544 for the camps (radius {circle?.Radius})");
        var b = Hero(ochars, pa.Map.Cluster, circle!.Position + new Vector2(1, 0));
        var pb = Join(off, b.Id).Session.Player!;
        Relation outside;
        lock (off.Sync) outside = Pvp.Of(pa, pb);
        Move(off, sa2, circle.Position);
        Tick(off, oclock, 1);
        Relation inside;
        lock (off.Sync) inside = Pvp.Of(pa, pb);
        Check(outside != Relation.Hostile && inside == Relation.Hostile, $"two Blue players: {outside} with one outside, {inside} both in the circle of a camp under way");
        Done(off, ochars, odir);
    }
}
