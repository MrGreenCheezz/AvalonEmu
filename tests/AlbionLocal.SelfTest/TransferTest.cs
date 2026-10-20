using System.Net;
using System.Numerics;
using AlbionLocal.Photon;
using AlbionLocal.Photon.Serialization;
using AlbionLocal.Server;
using AlbionLocal.Server.Commands;
using AlbionLocal.Server.Content;
using AlbionLocal.Server.Data;
using AlbionLocal.Server.Entities;
using AlbionLocal.Server.Game;
using AlbionLocal.Server.Network;
using AlbionLocal.Server.Persistence;
using AlbionLocal.Server.Protocol;
using static AlbionLocal.SelfTest.AlbionTests;

namespace AlbionLocal.SelfTest;

/// <summary>
/// Cluster changes (step 13; analysis/work/stage3/SPEC.md, "SPEC" below) on the client's cluster data: the Thetford bank and
/// back through FakePeer sessions on a manual clock, the golden arrivals of SPEC §3 (the captured exit transfers and the
/// Thetford doors; values from the SPEC tables, no capture data here), the deferred answer and .tele, and the transport
/// rule for a CONNECT from a known endpoint. Runs after <see cref="ClusterDataTest"/>, whose clusters the shared store has
/// resolved by then.
/// </summary>
public static class TransferTest
{
    public static IEnumerable<(string, Action)> All =>
    [
        ("transfer: Thetford -> bank 0006 -> back (Join by account, refusals 1640/1642/1647, response {0}, detached session that never saves, op 17, arrival keys, idle map unloaded)", Thetford),
        ("transfer: arrivals and facings equal SPEC §3 (6 captured transfers, the Thetford doors; 1002 -> 1000 through the server), none in an exit square, exit cooldown by cluster type", Golden),
        ("transfer: deferred answer while the target resolves (1669 meanwhile, 1641 on a failed load); .tele push and refusal; .exits", DeferredAndTele),
        ("transfer: travel-point journey Lymhurst -> Thetford as captured (route via 4000, BuyJourney RC 0 {253, 255}, ChangeCluster pushed, arrival at the travel point, last Move's facing; 8040 refusals)", Journey),
        ("transfer: a stale connection playing the same character is detached with the change (the arrival is saved and joined, its end saves nothing)", StaleDuplicate),
        ("transport: a CONNECT with a new challenge from a known endpoint gets a new peer, the old one closes without a DISCONNECT", StaleEndpoint),
    ];

    private static ClusterStore Store => Client.Clusters!;

    private static int Code(EventData e) => Convert.ToInt32(e.Parameters.GetValueOrDefault(ReservedKeys.EventCode));

    private static T Fill<T>(OperationResponse r) where T : new()
    {
        var m = new T();
        AlbionSerializer.FillFields(m, r.Parameters);
        return m;
    }

    private static string Short(Guid id) => id.ToString()[..8];

    private static bool Near(Vector2 a, float x, float z, float tolerance = 0.01f) => Math.Abs(a.X - x) <= tolerance && Math.Abs(a.Y - z) <= tolerance;

    private static bool NearAngle(float a, float b, float tolerance = 0.01f) => Math.Abs((a - b + 540) % 360 - 180) <= tolerance;

    /// <summary>A character without items in <paramref name="cluster"/> at <paramref name="at"/>, stored.</summary>
    private static Character NewHero(SqliteCharacterRepository characters, string cluster, Vector2 at)
    {
        var c = StarterKit.Default.NewCharacter("T" + Guid.NewGuid().ToString("N")[..10]);
        c.Cluster = cluster;
        c.PositionX = at.X;
        c.PositionY = at.Y;
        c.Equipment = [];
        c.Inventory = [];
        Check(characters.Create(c), "character created");
        return c;
    }

    /// <summary>A Join on a new game connection with key 0 = <paramref name="key"/> (a character, or the account as the real client sends).</summary>
    private static (WorldSession Session, FakePeer Peer, JoinResponse Join) Join(World world, Guid key)
    {
        var peer = new FakePeer();
        var session = new WorldSession(peer, world);
        session.OnRequest(Request(new JoinRequest { ss = key }, null), encrypted: true);
        Check(peer.Responses is [{ ReturnCode: 0 }], "join answered");
        return (session, peer, Fill<JoinResponse>(peer.Responses[0]));
    }

    private static void Move(WorldSession session, float x, float z) =>
        session.OnRequest(Request(new MoveRequest { wf = DateTime.UtcNow.Ticks, v6 = [x, z], v8 = [x, z], v9 = 0f }, null), encrypted: false);

    /// <summary>ChangeCluster as the client sends it (plaintext, key 1 "", key 2 [-1]); the new responses.</summary>
    private static List<OperationResponse> Change(WorldSession session, FakePeer peer, long exit, int requestId)
    {
        var before = peer.Responses.Count;
        session.OnRequest(Request(new ChangeClusterRequest { ee = exit, ef = "", eg = [-1] }, requestId), encrypted: false);
        return peer.Responses.Skip(before).ToList();
    }

    private static Exit ExitOf(Map map, string guid) => map.Objects.Values.OfType<Exit>().Single(e => e.World.Id.ToString().StartsWith(guid, StringComparison.Ordinal));

    private static bool Refused(List<OperationResponse> got, short rc, int requestId) =>
        got is [{ } r] && r.ReturnCode == rc && r.Parameters.GetValueOrDefault(ReservedKeys.RequestId) is int id && id == requestId && !r.Parameters.ContainsKey(0);

    /// <summary>
    /// SPEC §11 items 1-8 and 11: two characters, the second selected on the login connection; the Join by the account
    /// (as the real client) plays it. The bank door refuses from (0, 0), an unknown object and the cooldown, then takes the
    /// player into 0006 with {0: "0006"} only. The old session is detached: nothing more is sent on it, a Move is dropped,
    /// its timeout saves nothing. op 17 names this server. The new Join arrives at the bank's arrival point with the keys of
    /// a transfer; 0000, empty, unloads after World:MapIdleUnloadMinutes; the way back builds it anew.
    /// </summary>
    private static void Thetford()
    {
        _ = Store.Get("0000");
        _ = Store.Get("0006");
        var dir = NewDir();
        var characters = Repository(dir);
        var hero = NewHero(characters, "0000", Vector2.Zero);
        var clock = new ManualClock();
        var world = new World(new ServerConfig { JoinByCharacterId = true }, characters, Items()) { Sync = new object(), Clock = clock, Clusters = Store };
        var account = characters.Account.Id;

        var loginPeer = new FakePeer();
        var login = new LoginSession(loginPeer, world.Config, characters, StarterKit.Default) { World = world };
        login.OnRequest(Request(TestLogin(), 1), encrypted: true);
        login.OnRequest(Request(new SelectCharacterRequest { abc = hero.Name }, null), encrypted: true);
        Check(loginPeer.Responses is [{ ReturnCode: 0 }, { ReturnCode: 0 }], "login, select the second character");

        var (a, peerA, ja) = Join(world, account);
        Check(ja.a8b == hero.Id && ja.a8i == "0000" && ja.ba5 == 1 && ja.ba4 > 0 && ja.a94 == "" && ja.bbk == "0000",
              $"Join key 0 = the account: the selected character, the login's first Join (103 = 1, 102 set), no previous cluster, last city 0000 (got {ja.a8c}, {ja.ba5})");
        var map0 = world.Maps.Find("0000")!;
        var bankDoor = ExitOf(map0, "982df85e");
        var playerId = a.Player!.Id;
        Check(Refused(Change(a, peerA, bankDoor.Id, 10), 1640, 10) && a.Status == SessionStatus.InWorld, "at (0, 0) the bank door is out of reach: 1640, request id echoed, still in the world");
        Check(Refused(Change(a, peerA, 987654321, 11), 1642, 11), "no such exit object: 1642");
        Move(a, -10, -13);
        Check(Refused(Change(a, peerA, bankDoor.Id, 12), 1647, 12), "in the square right after the Join: 1647 (city cooldown 1 s)");
        clock.Advance(TimeSpan.FromSeconds(1));
        var sent = peerA.Sequence.Count;
        var keepalives = peerA.Unsequenced.Count;
        var ok = Change(a, peerA, bankDoor.Id, 13);
        Check(ok is [{ ReturnCode: 0 } r] && r.Parameters.Keys.Order().SequenceEqual(new byte[] { 0, ReservedKeys.OperationCode, ReservedKeys.RequestId })
              && r.Parameters[0] is "0006" && r.Parameters[ReservedKeys.OperationCode] is (short)41 && r.Parameters[ReservedKeys.RequestId] is 13,
              $"inside after the cooldown: RC 0, exactly {{0: \"0006\", 253: 41, 255: 13}} (got keys {string.Join(",", ok.SelectMany(x => x.Parameters.Keys))})");
        Check(peerA.Sequence.Count == sent + 1 && !peerA.ResponseEncrypted[^1]
              && peerA.Events.All(e => Code(e) != (int)EventCodes.Leave || Convert.ToInt64(e.Parameters[0]) != playerId),
              "nothing but the response, in plaintext as the request; never a Leave of the own id");
        Check(a.Status == SessionStatus.Transferring && a.Player is null && a.Character is null && map0.Players.Count == 0, "the session is detached, the player off map 0000");
        clock.Advance(TimeSpan.FromSeconds(2));
        world.Update();
        Move(a, -12, -12);
        Check(peerA.Unsequenced.Count == keepalives && peerA.Sequence.Count == sent + 1 && peerA.DisconnectReason is null,
              "the old connection gets no keepalive, a Move on it is dropped, the server never disconnects it");
        var saved = world.Saves.Load(hero.Id)!;
        Check(saved is { Cluster: "0006", PreviousCluster: "0000", PendingTransfer: { From: "0000", To: "0006" } t } && Near(new(saved.PositionX, saved.PositionY), -26.5f, 0.5f)
              && NearAngle(saved.Direction, 90) && t.Via.StartsWith("exit:982df85e", StringComparison.Ordinal),
              $"saved at the commit: 0006, arrival (-26.5, 0.5) facing 90, the pending transfer (got {saved.Cluster} ({saved.PositionX}, {saved.PositionY}))");

        login.OnRequest(Request(new GetGameServerByClusterRequest { mt = "0006" }, 4), encrypted: true);
        Check(loginPeer.Responses[^1] is { ReturnCode: 0 } gs && gs.Parameters[0] is "127.0.0.1:5056" && gs.Parameters[ReservedKeys.RequestId] is 4
              && gs.Parameters[ReservedKeys.OperationCode] is (short)OperationCodes.GetGameServerByCluster && !loginPeer.ResponseEncrypted[^1],
              "GetGameServerByCluster (encrypted) on the login connection: RC 0, this game server, request id echoed, in plaintext as official");

        var (b, peerB, jb) = Join(world, account);
        var response = peerB.Sequence.FindIndex(x => x is OperationResponse);
        Check(jb.a8i == "0006" && Near(jb.a8j, -26.5f, 0.5f) && NearAngle(jb.a8k, 90) && jb.a94 == "0000" && jb.ba5 == 2 && jb.ba3 > 0 && jb.bbk == "0006",
              $"the new Join: key 8 0006, 9 (-26.5, 0.5), 10 = 90, 66 = 0000, 103 = 2, 101 set, 118 = 0006 (got {jb.a8i} {jb.a8j} {jb.a8k} {jb.a94} {jb.ba5})");
        Check(peerB.Sequence.Take(response).OfType<EventData>().Any(e => Code(e) == (int)EventCodes.NewBuilding && Convert.ToInt32(e.Parameters[2]) == 52)
              && b.Character is { PendingTransfer: null }, "phase A has the bank (NewBuilding 52) before the response; the Join cleared the pending transfer");

        Move(b, -30, 0.5f);
        a.OnDisconnected("timeout"); // the old connection's timeout, after the new Join and a Move
        lock (world.Sync) world.Saves.SaveAll();
        world.Saves.Flush();
        Check(characters.Find(hero.Id) is { Cluster: "0006", PositionX: -30 } && peerA.DisconnectReason is null,
              "the old connection's end saves nothing: the record still says 0006 and the new position");

        clock.Advance(world.Config.MapIdleUnload);
        world.Update();
        Check(world.Maps.Find("0000") is null && map0.Unloaded && world.Maps.Find("0006") is { Players.Count: 1 }, "0000 is unloaded after 5 minutes without players, 0006 stays");

        var bankExit = ExitOf(world.Maps.Find("0006")!, "06b25940");
        Move(b, -38, 0.5f);
        Check(Change(b, peerB, bankExit.Id, 20) is [{ ReturnCode: 0 } back] && back.Parameters[0] is "0000", "out of the bank");
        var (_, _, jc) = Join(world, account);
        var map0Again = world.Maps.Find("0000")!;
        Check(jc.a8i == "0000" && Near(jc.a8j, -10, -20.750141f, 0.0001f) && NearAngle(jc.a8k, 180) && jc.a94 == "0006" && jc.ba5 == 3,
              $"back in Thetford in front of the bank door: (-10, -20.750141), the spawnpos pushed 0.75 m off the pillar as official, facing 180, 66 = 0006, 103 = 3 (got {jc.a8j} {jc.a8k})");
        Check(map0Again != map0 && ExitOf(map0Again, "982df85e").Id != bankDoor.Id, "a new map 0000 with new object ids");
        world.Dispose();
        characters.Dispose();
        try { Directory.Delete(dir, true); } catch (IOException) { }
    }

    /// <summary>
    /// SPEC §11 item 12. The arrival (target exit spawnpos in world coordinates, pushed clear of the exit tile's collision
    /// by characters.xml's collisionradius) and facing through every exit transfer the captures hold (103829 and 114814 as
    /// SPEC §3; 002835: 1207 → 1000, Thetford ↔ bank 0006 and market 0007, 0000 → 0204) and the other Thetford
    /// doors (spec/arrivals.py), within 0.01 m and 0.01°. The two bank doors arrive 0.75 m beyond their spawnpos, as
    /// captured: Lymhurst 1001 → 1000 (-20, -20.75), Thetford 0006 → 0000 (-10, -20.750141); every other one exactly at it.
    /// One captured transfer, 1002 → 1000, also goes through the server. No arrival lies in an exit square of its cluster;
    /// the exit cooldown is 1 s in the City clusters and gamedata.xml's 10 s in 0204 and 1207.
    /// </summary>
    private static void Golden()
    {
        var info = Store.World;
        var radius = (float)Client.GameData!.CollisionRadius;
        Check(radius == 0.75f, $"characters.xml DefaultValues collisionradius 0.75 (got {radius})");
        // The bank entrance's grid: a pillar x -2..2, z -10..-7 next to the spawnpos (0, -10.00014); the marketplace
        // entrance's building leaves its spawnpos (5.17, 4.62) clear.
        var bank = Store.Tiles.Collision("SWAMP_GREEN_T1_BANK_ENTRANCE");
        var market = Store.Tiles.Collision("SWAMP_GREEN_MARKETPLACE_ENTRANCE");
        Check(bank is { Width: 30, Height: 30 } && bank.Offset == new Vector2(-15, -15) && bank.Blocks(new(0, -10.00014f), radius) && !bank.Blocks(new(0, -10.75014f), radius)
              && !bank.Blocks(new(0, -10.75f), radius) && bank.Blocks(new(0, -8.5f), 0.1f) && market is { Width: 20 } && !market.Blocks(new(5.170006f, 4.62f), radius)
              && Store.Tiles.TilesWithCollision > 50_000,
              $"tile collisions: the bank pillar blocks within 0.75 m of (0, -10.00014), not at -10.75; the marketplace spawnpos is clear ({Store.Tiles.TilesWithCollision} tiles)");
        var north = info.Find("0204")!.Exits.Single(e => e.TargetCluster == "0000" && e.Target.ToString().StartsWith("8c7d7adb", StringComparison.Ordinal));
        (string From, string Exit, string To, float X, float Z, float Facing)[] rows =
        [
            ("1002", "60fdac76", "1000", -31.214f, 15.974f, 157.2462f),   // 103829 x 2, 114814
            ("1000", "231b66b9", "1002", -21.5f, 90.0f, 88.5679f),        // 103829, 114814
            ("1000", "414bacb4", "1001", -26.5f, 0.5f, 90f),              // 103829
            ("1001", "f06a4de5", "1000", -20f, -20.75f, 180f),            // 103829: the Lymhurst bank door, spawnpos (-20, -20) + 0.75
            ("1000", "3274ad3b", "1207", -150.184f, -366.179f, 356.8208f), // 103829, 114814
            ("1207", "918a05ca", "1000", -20.159f, 58.5f, 182.5156f),     // 103829, 002835
            ("0000", "982df85e", "0006", -26.5f, 0.5f, 90f),              // 002835
            ("0006", "06b25940", "0000", -10f, -20.750141f, 180f),        // 002835: the Thetford bank door, spawnpos (-10, -20.00014) + 0.75
            ("0000", "399399b1", "0007", -21.5f, 90.0f, 88.5679f),        // 002835
            ("0007", "9c1b401a", "0000", -20.38f, 19.83f, 138.2155f),     // 002835
            ("0000", "8c7d7adb", "0204", 50f, -370.49985f, 358.0909f),    // 002835 (north gate)
            ("0006", "5e03f43b", "0007", -0.5f, 57.5f, 0f),               // spec/arrivals.py
            ("0007", "e0a22b80", "0006", -0.5f, 22.5f, 180f),             // spec/arrivals.py
            ("0204", Short(north.Id), "0000", -10.5f, 37.5f, 180f),       // spec/arrivals.py
        ];
        var bad = new List<string>();
        foreach (var (from, exitId, to, x, z, facing) in rows)
        {
            var exit = info.Find(from)!.Exits.Single(e => e.Id.ToString().StartsWith(exitId, StringComparison.Ordinal));
            var arrival = exit.TargetCluster == to ? Transfers.Through(exit, Store.Get(to), radius) : null;
            if (arrival is not { } a || a.Cluster != to || !Near(a.Position, x, z) || !NearAngle(a.Direction, facing))
                bad.Add($"{from} {exitId} -> {to}: {(arrival is { } got ? $"({got.Position.X}, {got.Position.Y}) facing {got.Direction}" : $"no arrival (target {exit.TargetCluster})")}");
        }
        Check(bad.Count == 0, $"{bad.Count} of {rows.Length} arrivals differ: {string.Join("; ", bad)}");

        var arrivals = 0;
        foreach (var id in new[] { "0000", "0006", "0007", "1000", "1001", "1002", "1207" })
        {
            var t = Store.Get(id);
            foreach (var e in t.Exits)
            {
                if (e.SpawnPosition is not { } at) continue;
                arrivals++;
                if (t.Exits.FirstOrDefault(o => Transfers.InSquare(o.Position, Transfers.Size(o), at)) is { } trap)
                    bad.Add($"{id}: the arrival of {Short(e.Guid)} lies in the square of {Short(trap.Guid)}");
            }
        }
        Check(arrivals > 20 && bad.Count == 0, $"no arrival inside an exit square ({arrivals} checked): {string.Join("; ", bad)}");
        TimeSpan Cooldown(string id) => Transfers.ExitCooldown(ClusterName.Parse(info.Find(id)!.File), Client.GameData);
        Check(Client.GameData!.ClusterChangeCooldownSeconds == 10
              && new[] { "0000", "0006", "0007", "1000", "1001", "1002" }.All(id => Cooldown(id) == TimeSpan.FromSeconds(1))
              && Cooldown("0204") == TimeSpan.FromSeconds(10) && Cooldown("1207") == TimeSpan.FromSeconds(10),
              "exit cooldown: 1 s in the CTY clusters, gamedata.xml ClusterChange 10 s in 0204 and 1207");

        // 1002 -> 1000 through the server: in the 15 x 15 square of 60fdac76 (-41.5, 89.5), after the cooldown.
        var dir = NewDir();
        var characters = Repository(dir);
        var clock = new ManualClock();
        var world = new World(new ServerConfig { JoinByCharacterId = true }, characters, Items()) { Sync = new object(), Clock = clock, Clusters = Store };
        var hero = NewHero(characters, "1002", new Vector2(-34.11f, 89.22f));
        var (s, peer, _) = Join(world, hero.Id);
        clock.Advance(TimeSpan.FromSeconds(1));
        Check(Change(s, peer, ExitOf(world.Maps.Find("1002")!, "60fdac76").Id, 7) is [{ ReturnCode: 0 } r] && r.Parameters[0] is "1000", "1002 -> 1000: changed");
        var (_, _, j) = Join(world, hero.Id);
        Check(j.a8i == "1000" && Near(j.a8j, -31.214f, 15.974f) && NearAngle(j.a8k, 157.2462f) && j.a94 == "1002",
              $"the Join arrives at the captured (-31.214, 15.974) facing 157.2462 (got {j.a8j} facing {j.a8k})");
        var (_, _, plainJoin) = Join(world, hero.Id);
        Check(plainJoin.a8i == "1000" && plainJoin.a8k == 0, $"a later Join, with no transfer pending, sends no facing (key 10 0, as before step 13; got {plainJoin.a8k})");
        world.Dispose();
        characters.Dispose();
        try { Directory.Delete(dir, true); } catch (IOException) { }
    }

    /// <summary>
    /// SPEC §11 items 9 and 10, light. A target the store has not resolved (here a load the test completes itself) defers the
    /// answer; a second ChangeCluster meanwhile gets 1669; a failed load is answered with 1641 and the request id kept, a
    /// later one with the change. .tele pushes the ChangeCluster response without a request id, an unknown cluster is an
    /// error line and nothing else; .exits lists the doors with their arrival points.
    /// </summary>
    private static void DeferredAndTele()
    {
        var thetford = Store.Get("0000");
        var bank = Store.Get("0006");
        _ = Store.Get("0007");
        var dir = NewDir();
        var characters = Repository(dir);
        var clock = new ManualClock();
        var loads = new Dictionary<string, TaskCompletionSource<ClusterTemplate>>();
        var world = new World(new ServerConfig { JoinByCharacterId = true }, characters, Items())
        {
            Sync = new object(), Clock = clock, Clusters = Store,
            LoadCluster = id =>
            {
                if (id == "0000") return Task.FromResult(thetford);
                var load = new TaskCompletionSource<ClusterTemplate>(TaskCreationOptions.RunContinuationsAsynchronously);
                lock (loads) loads[id] = load;
                return load.Task;
            },
        };
        void TickUntil(Func<bool> done)
        {
            for (var waited = 0; !done() && waited < 10000; waited += 10)
            {
                Thread.Sleep(10);
                lock (world.Sync) world.Update();
            }
        }
        var hero = NewHero(characters, "0000", new Vector2(-10, -13));
        var (s, peer, _) = Join(world, hero.Id);
        var map = world.Maps.Find("0000")!;
        TickUntil(() => map.Template is not null);
        var door = ExitOf(map, "982df85e").Id;
        clock.Advance(TimeSpan.FromSeconds(1));
        Check(Change(s, peer, door, 5).Count == 0 && s.Status == SessionStatus.InWorld, "target not resolved: no answer yet");
        Check(Refused(Change(s, peer, door, 6), 1669, 6), "a second ChangeCluster meanwhile: 1669");
        var level = Log.Level;
        Log.Level = LogLevel.Error; // the warning about the failed load
        lock (loads) loads["0006"].SetException(new IOException("cluster file being rewritten (test)"));
        TickUntil(() => peer.Responses.Count == 3);
        Log.Level = level;
        Check(Refused(peer.Responses.Skip(2).ToList(), 1641, 5) && s.Status == SessionStatus.InWorld, "a failed load: 1641 with the kept request id");
        Check(Change(s, peer, door, 7).Count == 0, "tried again: deferred again");
        lock (loads) loads["0006"].SetResult(bank);
        TickUntil(() => peer.Responses.Count == 4);
        Check(peer.Responses[^1] is { ReturnCode: 0 } r && r.Parameters[0] is "0006" && r.Parameters[ReservedKeys.RequestId] is 7 && s.Status == SessionStatus.Transferring,
              "the resolved change: RC 0, {0: 0006} with the kept request id");
        world.Dispose();

        // .tele and .exits on a world that reads through the store (cached clusters: at once).
        var plain = new World(new ServerConfig { JoinByCharacterId = true }, characters, Items()) { Sync = new object(), Clock = new ManualClock(), Clusters = Store };
        var (tele, telePeer, _) = Join(plain, NewHero(characters, "0000", Vector2.Zero).Id);
        var (other, otherPeer, _) = Join(plain, NewHero(characters, "0000", new Vector2(-10, -13)).Id);
        var replies = new List<(string Text, bool Error)>();
        CommandContext Ctx(Player p) => new(plain, p, AccountLevel.GameMaster, (t, e) => replies.Add((t, e)));
        var teleId = tele.Character!.Id;
        plain.Commands.Execute(Ctx(tele.Player!), ".tele 0007");
        Check(telePeer.Responses[^1] is { ReturnCode: 0 } push && push.Parameters[0] is "0007" && !push.Parameters.ContainsKey(ReservedKeys.RequestId)
              && push.Parameters[ReservedKeys.OperationCode] is (short)41 && tele.Status == SessionStatus.Transferring
              && plain.Saves.Load(teleId) is { Cluster: "0007", PendingTransfer.Via: "tele" } && replies is [(_, false)],
              $".tele 0007: the ChangeCluster response pushed without a request id, the character saved in 0007 (got {string.Join(" / ", replies)})");
        var (_, _, jt) = Join(plain, teleId);
        Check(jt.a8i == "0007" && jt.a94 == "0000", "and the next Join is in 0007");
        replies.Clear();
        var responses = otherPeer.Responses.Count;
        plain.Commands.Execute(Ctx(other.Player!), ".tele NO-SUCH-9999");
        Check(replies is [(var no, true)] && no.Contains("NO-SUCH-9999") && otherPeer.Responses.Count == responses && other.Status == SessionStatus.InWorld,
              $".tele to an unknown cluster: an error line, nothing sent (got {string.Join(" / ", replies)})");
        replies.Clear();
        plain.Commands.Execute(Ctx(other.Player!), ".exits");
        var lines = replies.SelectMany(x => x.Text.Split('\n')).ToList();
        var doorId = ExitOf(plain.Maps.Find("0000")!, "982df85e").Id;
        Check(lines[0] == "0000: 9 exit(s)" && lines[1].StartsWith($"  #{doorId} 982df85e at (-10, -10) 9x9, 3.0 m -> 0006 (exit 06b25940): arrival (-26.5, 0.5) facing 90", StringComparison.Ordinal),
              $".exits: the bank door nearest, with its arrival (got {string.Join(" / ", lines.Take(2))})");
        plain.Dispose();
        characters.Dispose();
        try { Directory.Delete(dir, true); } catch (IOException) { }
    }

    /// <summary>
    /// The journey of capture 002835 (transfers n = 2): Lymhurst's travel point 7e5a37ea (-30, 51) to
    /// Thetford's cfca05a8 (-2, 36). The client's route rule (no direct world.xml connection: 1000 → 4000 → 0000, cost 10;
    /// Caerleon 3003 only without luggage); then on a session: refusals (an exit instead of a travel point, an unknown
    /// target, the travel point itself, the island kind) answered 8040 with the request id; the journey with the captured
    /// keys gets exactly {253: 201, 255} with RC 0, then the ChangeCluster push {0: "0000"} without 255; the Join after it
    /// arrives at (-10.09854, 28.146809), facing the last Move's direction 310.45593 as official, previous cluster 1000.
    /// </summary>
    private static void Journey()
    {
        var info = Store.World;
        const string lymhurst = "7e5a37ea-e00e-4cc3-a0f4-035e8e654edd@1000", thetford = "cfca05a8-f2cc-4b49-9aa8-e89ed1d2b64e@0000";
        var caerleon = Transfers.TravelpointKey(info.Find("3003")!.Travelpoints[0].Id, "3003");
        Check(Transfers.Route(info, lymhurst, thetford) is { Cost: 10, NoLuggage: false, Clusters: ["1000", "4000", "0000"] }
              && Transfers.Route(info, lymhurst, caerleon) is { Cost: 4, NoLuggage: true, Clusters: ["1000", "3003"] }
              && Transfers.Route(info, lymhurst, "00000000-0000-0000-0000-000000000000@0000") is null,
              "routes: 1000 -> 4000 -> 0000 cost 10 with luggage; Caerleon only without luggage; an unknown point none");

        _ = Store.Get("1000");
        _ = Store.Get("0000");
        var dir = NewDir();
        var characters = Repository(dir);
        var world = new World(new ServerConfig { JoinByCharacterId = true }, characters, Items()) { Sync = new object(), Clock = new ManualClock(), Clusters = Store };
        var hero = NewHero(characters, "1000", new Vector2(-28.5f, 49.5f));
        hero.Silver = 40_000 * 10_000L; // stage 6: the journey charges the client's price
        characters.Save(hero);
        var (s, peer, _) = Join(world, hero.Id);
        var map = world.Maps.Find("1000")!;
        var point = map.Objects.Values.OfType<Travelpoint>().Single(t => t.Spawn.Guid.ToString() == lymhurst[..36]);
        s.OnRequest(Request(new MoveRequest { wf = DateTime.UtcNow.Ticks, v6 = [-28.5f, 49.5f], v7 = 310.45593f, v8 = [-28.5f, 49.5f], v9 = 0f }, null), encrypted: false);
        List<OperationResponse> Buy(long objectId, string target, int kind, int requestId)
        {
            var before = peer.Responses.Count;
            s.OnRequest(Request(new BuyJourneyRequest { df = objectId, dg = target, dh = kind, dj = 369226400 }, requestId), encrypted: false);
            return peer.Responses.Skip(before).ToList();
        }
        bool RefusedJourney(List<OperationResponse> got, int requestId) =>
            got is [{ ReturnCode: 8040 } r] && r.Parameters.Keys.Order().SequenceEqual(new[] { ReservedKeys.OperationCode, ReservedKeys.RequestId })
            && r.Parameters[ReservedKeys.RequestId] is int id && id == requestId && s.Status == SessionStatus.InWorld;
        var exit = map.Objects.Values.OfType<Exit>().First();
        Check(RefusedJourney(Buy(exit.Id, thetford, 0, 1), 1) && RefusedJourney(Buy(point.Id, "nowhere", 0, 2), 2)
              && RefusedJourney(Buy(point.Id, lymhurst, 0, 3), 3) && RefusedJourney(Buy(point.Id, thetford, 1, 4), 4),
              "8040 with the request id for an exit, an unknown target, the travel point itself, the home kind; still in the world");

        var sent = peer.Sequence.Count;
        var got = Buy(point.Id, thetford, 0, 5);
        Check(got is [{ ReturnCode: 0 } bought, { ReturnCode: 0 } push]
              && bought.Parameters.Keys.Order().SequenceEqual(new[] { ReservedKeys.OperationCode, ReservedKeys.RequestId })
              && bought.Parameters[ReservedKeys.OperationCode] is (short)OperationCodes.BuyJourney && bought.Parameters[ReservedKeys.RequestId] is 5
              && push.Parameters.Keys.Order().SequenceEqual(new byte[] { 0, ReservedKeys.OperationCode }) && push.Parameters[0] is "0000"
              && push.Parameters[ReservedKeys.OperationCode] is (short)OperationCodes.ChangeCluster && peer.Sequence.Count == sent + 3
              && peer.Sequence[sent] is EventData money && Convert.ToInt16(money.Parameters[ReservedKeys.EventCode]) == (short)EventCodes.UpdateMoney
              && Convert.ToInt64(money.Parameters[1]) == 40_000 * 10_000L - 369226400,
              $"the journey: UpdateMoney with the client's price taken, then exactly {{253: 201, 255: 5}} RC 0, then {{0: \"0000\", 253: 41}} without 255, nothing else (got {got.Count} responses: {string.Join(", ", peer.Sequence.Skip(sent).Select(x => x is EventData e ? $"event {e.Parameters[ReservedKeys.EventCode]} {string.Join(" ", e.Parameters.Select(p => $"{p.Key}={p.Value}"))}" : $"response rc {((OperationResponse)x).ReturnCode}"))})");
        Check(s.Status == SessionStatus.Transferring && map.Players.Count == 0
              && world.Saves.Load(hero.Id) is { Cluster: "0000", PreviousCluster: "1000", PendingTransfer.Via: var via } && via.StartsWith("journey:7e5a37ea", StringComparison.Ordinal),
              "committed as an exit change: detached, off 1000, saved in 0000 with the pending transfer");
        var (_, _, j) = Join(world, hero.Id);
        Check(j.a8i == "0000" && Near(j.a8j, -10.09854f, 28.146809f, 0.0001f) && NearAngle(j.a8k, 310.45593f, 0.0001f) && j.a94 == "1000",
              $"the Join arrives at Thetford's travel point spawnpos (-10.09854, 28.146809) facing the last Move's 310.45593, 66 = 1000 (got {j.a8j} facing {j.a8k})");
        world.Dispose();
        characters.Dispose();
        try { Directory.Delete(dir, true); } catch (IOException) { }
    }

    /// <summary>
    /// A client that lost its game connection without a DISCONNECT joins again while the server keeps the old one (until
    /// its timeout): both players share the live character. A change on the new connection takes the old player off its
    /// map too, so the save holds the arrival and not the old player's last Move, the next Join arrives there, and the old
    /// connection's end saves nothing.
    /// </summary>
    private static void StaleDuplicate()
    {
        _ = Store.Get("0000");
        _ = Store.Get("0006");
        var dir = NewDir();
        var characters = Repository(dir);
        var clock = new ManualClock();
        var world = new World(new ServerConfig { JoinByCharacterId = true }, characters, Items()) { Sync = new object(), Clock = clock, Clusters = Store };
        var hero = NewHero(characters, "0000", new Vector2(-10, -13));
        var (stale, stalePeer, _) = Join(world, hero.Id);
        Move(stale, 5, 5); // its last Move before the client went away
        var (live, livePeer, _) = Join(world, hero.Id);
        Move(live, -10, -13);
        clock.Advance(TimeSpan.FromSeconds(1));
        Check(Change(live, livePeer, ExitOf(world.Maps.Find("0000")!, "982df85e").Id, 3) is [{ ReturnCode: 0 }], "into the bank from the new connection");
        Check(stale.Status == SessionStatus.Transferring && stale.Player is null && world.Maps.Find("0000")!.Players.Count == 0 && stalePeer.DisconnectReason is null,
              "the old connection's player is off 0000 too, its session detached, not disconnected");
        var saved = world.Saves.Load(hero.Id)!;
        Check(saved.Cluster == "0006" && Near(new(saved.PositionX, saved.PositionY), -26.5f, 0.5f),
              $"saved at the arrival, not at the old player's (5, 5) (got {saved.Cluster} ({saved.PositionX}, {saved.PositionY}))");
        stale.OnDisconnected("timeout");
        var (_, _, j) = Join(world, hero.Id);
        Check(j.a8i == "0006" && Near(j.a8j, -26.5f, 0.5f) && NearAngle(j.a8k, 90), $"the next Join arrives in the bank (got {j.a8i} {j.a8j} facing {j.a8k})");
        lock (world.Sync) world.Saves.SaveAll();
        world.Saves.Flush();
        Check(characters.Find(hero.Id) is { Cluster: "0006" } c && Near(new(c.PositionX, c.PositionY), -26.5f, 0.5f), "the old connection's end saved nothing");
        world.Dispose();
        characters.Dispose();
        try { Directory.Delete(dir, true); } catch (IOException) { }
    }

    /// <summary>
    /// SPEC §4.7: a client socket on the port of a connection the server still has (no DISCONNECT came) sends CONNECT with a
    /// new challenge. It gets a new peer and completes its handshake; the old peer's handler hears of the close as of a
    /// timeout.
    /// </summary>
    private static void StaleEndpoint()
    {
        var handlers = new List<Recorder>();
        using var server = new PhotonServer(new IPEndPoint(IPAddress.Loopback, 0), "stale", _ =>
        {
            var h = new Recorder();
            lock (handlers) handlers.Add(h);
            return h;
        });
        server.Start();
        var old = new TestPhotonClient(server.EndPoint);
        old.Connect();
        Check(old.EncryptionDone.Task.Wait(10000), "first connection established");
        var port = old.LocalPort;
        old.Abandon();
        using var again = new TestPhotonClient(server.EndPoint, localPort: port);
        again.Connect();
        Check(again.EncryptionDone.Task.Wait(10000), "a CONNECT with a new challenge from the same endpoint: handshake and key exchange complete");
        again.SendOperation(new OperationRequest(1, new() { [0] = "x", [255] = 3 }));
        Check(again.Responses.TryTake(out var r, 10000) && r.Parameters[255] is 3, "the new peer answers");
        lock (handlers)
            Check(handlers.Count == 2 && handlers[0].Closed == "replaced by a new connection from the same endpoint" && handlers[1].Closed is null,
                  $"a new peer; the old one closed silently (got {handlers.Count} peers, {handlers[0].Closed})");
    }

    /// <summary>Echoes each request; keeps the close reason.</summary>
    private sealed class Recorder : IPhotonPeerHandler
    {
        public string? Closed;

        public void OnOperationRequest(PhotonPeer peer, OperationRequest request, bool encrypted) =>
            peer.SendOperationResponse(new OperationResponse(request.Code, new(request.Parameters)), encrypted);

        public void OnDisconnected(PhotonPeer peer, string reason) => Closed = reason;
    }

    private static void Check(bool cond, string what) => AlbionTests.Check(cond, what);
}
