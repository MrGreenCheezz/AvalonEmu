using System.Numerics;
using AlbionLocal.Photon;
using AlbionLocal.Photon.Serialization;
using AlbionLocal.Server;
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
/// The city portals into the Outlands (analysis/work/portals/SPEC.md) on the client's cluster data: Thetford's Conquerors' Hall
/// 0360-HALL-01, its portal town 0301 and Lymhurst's hall 1013-HALL-01, through FakePeer sessions.
/// </summary>
public static class PortalTest
{
    public static IEnumerable<(string, Action)> All =>
    [
        ("portals: Thetford hall -> Thetford Portal 0301 -> back (NewPortalEntrance/Exit, UsePortalEntrance RC 0 + ChangeCluster push, arrivals at the spawnpos, binding kept, refused at Lymhurst's until removed)", ThereAndBack),
    ];

    private const string Hall = "0360-HALL-01", Town = "0301", OtherHall = "1013-HALL-01";
    private const string TownExit = "eec73385-2903-4353-8bde-723b51d2bb4e@0301", OtherTownExit = "0dd514bb-ac9f-4cd1-85d6-2fe4ca7b2948@1301";

    private static T Fill<T>(OperationResponse r) where T : new()
    {
        var m = new T();
        AlbionSerializer.FillFields(m, r.Parameters);
        return m;
    }

    private static bool Near(Vector2 a, float x, float z) => Math.Abs(a.X - x) <= 0.01f && Math.Abs(a.Y - z) <= 0.01f;

    private static Character NewHero(SqliteCharacterRepository characters, string cluster, Vector2 at, PortalBinding? binding = null)
    {
        var c = StarterKit.Default.NewCharacter("P" + Guid.NewGuid().ToString("N")[..10]);
        (c.Cluster, c.PositionX, c.PositionY, c.PortalBinding) = (cluster, at.X, at.Y, binding);
        Check(characters.Create(c), "character created");
        return c;
    }

    private static (WorldSession Session, FakePeer Peer, JoinResponse Join) Join(World world, Guid key)
    {
        var peer = new FakePeer();
        var session = new WorldSession(peer, world);
        session.OnRequest(Request(new JoinRequest { ss = key }, null), encrypted: true);
        Check(peer.Responses is [{ ReturnCode: 0 }], "join answered");
        return (session, peer, Fill<JoinResponse>(peer.Responses[0]));
    }

    private static List<OperationResponse> Send(WorldSession session, FakePeer peer, AlbionRequest request, int requestId)
    {
        var before = peer.Responses.Count;
        session.OnRequest(Request(request, requestId), encrypted: false);
        return peer.Responses.Skip(before).ToList();
    }

    private static void ThereAndBack()
    {
        var store = Client.Clusters!;
        foreach (var id in new[] { Hall, Town, OtherHall, "1301" }) _ = store.Get(id);
        var dir = NewDir();
        var characters = Repository(dir);
        var world = new World(new ServerConfig { JoinByCharacterId = true, Transfer = new TransferOptions { CheckExitCooldown = false } }, characters, Client.Items)
            { Sync = new object(), Clock = new ManualClock(), Clusters = store };

        // The way out: the hall's Realmgate, the window's query, the pass.
        var hero = NewHero(characters, Hall, new Vector2(-16, 16));
        var (s, peer, _) = Join(world, hero.Id);
        var gate = world.Maps.Find(Hall)!.Objects.Values.OfType<PortalEntrance>().Single();
        Check(gate.World is { Kind: PortalKind.Thetford } && gate.Position == new Vector2(-26, 26) && TownExit.StartsWith(store.World.Find(Town)!.PortalExits.Single().Id.ToString(), StringComparison.Ordinal)
              && gate.BuildCreate(s.Player!).Single() is NewPortalEntranceEvent { c8e: var guid } && guid == gate.World.Id && s.Player!.Visibility.Known.Contains(gate),
              $"the hall has Thetford's portal entrance at (-26, 26), in the player's view (got {gate})");
        Check(Send(s, peer, new QueryPortalBindingRequest(), 1) is [{ ReturnCode: 0 } none] && !none.Parameters.ContainsKey(0), "not bound yet: QueryPortalBinding without key 0");
        Check(Send(s, peer, new UsePortalEntranceRequest { aih = gate.Id, aii = OtherTownExit }, 2) is [{ ReturnCode: 11760 }] && s.Status == SessionStatus.InWorld,
              "Lymhurst's portal town from Thetford's Realmgate: 11760");
        var got = Send(s, peer, new UsePortalEntranceRequest { aih = gate.Id, aii = TownExit }, 3);
        Check(got is [{ ReturnCode: 0 } used, { ReturnCode: 0 } push] && used.Parameters[ReservedKeys.OperationCode] is (short)OperationCodes.UsePortalEntrance
              && used.Parameters[ReservedKeys.RequestId] is 3 && !used.Parameters.ContainsKey(0)
              && push.Parameters[0] is Town && push.Parameters[ReservedKeys.OperationCode] is (short)OperationCodes.ChangeCluster && !push.Parameters.ContainsKey(ReservedKeys.RequestId)
              && s.Status == SessionStatus.Transferring && world.Saves.Load(hero.Id) is { Cluster: Town, PortalBinding.City: PortalKind.Thetford },
              $"the pass: a bare RC 0 with the request id, then {{0: \"0301\", 253: 41}} pushed; saved in 0301, bound to Thetford (got {got.Count} responses)");

        // The portal town: arrival at the portal's spawnpos, the way back through its square.
        var (t, townPeer, j) = Join(world, hero.Id);
        var portal = world.Maps.Find(Town)!.Objects.Values.OfType<PortalExit>().Single();
        Check(j.a8i == Town && Near(j.a8j, 10, 0) && Math.Abs(j.a8k - 90) < 0.01f && portal.Size == new Vector2(10, 10) && portal.World.TargetCluster == Hall,
              $"the Join arrives at the portal's spawnpos (10, 0) facing away from it (got {j.a8j} facing {j.a8k})");
        Check(Send(t, townPeer, new ChangeClusterRequest { ee = portal.Id, ef = "", eg = [-1] }, 4) is [{ ReturnCode: 1640 }], "outside the portal's square: 1640");
        t.OnRequest(Request(new MoveRequest { wf = DateTime.UtcNow.Ticks, v6 = [0f, 0f], v8 = [0f, 0f], v9 = 0f }, null), encrypted: false);
        Check(Send(t, townPeer, new ChangeClusterRequest { ee = portal.Id, ef = "", eg = [-1] }, 5) is [{ ReturnCode: 0 } back] && back.Parameters[0] is Hall,
              "inside it: RC 0 {0: \"0360-HALL-01\"}");
        var (_, _, home) = Join(world, hero.Id);
        Check(home.a8i == Hall && Near(home.a8j, -16.2905f, 16.2905f) && Math.Abs(home.a8k - 135) < 0.01f,
              $"back in the hall at the Realmgate's spawnpos (-16.29, 16.29), facing away from it (got {home.a8j} facing {home.a8k})");

        // The binding (stored with the character): shown by the query, shut at another city's Realmgate until it is removed.
        var bound = NewHero(characters, OtherHall, new Vector2(-16, 16), new PortalBinding(PortalKind.Thetford, 0));
        var (o, otherPeer, _) = Join(world, bound.Id);
        var otherGate = world.Maps.Find(OtherHall)!.Objects.Values.OfType<PortalEntrance>().Single();
        Check(Send(o, otherPeer, new QueryPortalBindingRequest(), 6) is [{ ReturnCode: 0 } q] && q.Parameters[0] is "Thetford Portal" && q.Parameters[1] is (byte)5,
              "bound to Thetford: QueryPortalBinding {0: \"Thetford Portal\", 1: 5}");
        Check(otherGate.World.Kind == PortalKind.Lymhurst && Send(o, otherPeer, new UsePortalEntranceRequest { aih = otherGate.Id, aii = OtherTownExit }, 7) is [{ ReturnCode: 11767 }],
              "Lymhurst's Realmgate while bound to Thetford: 11767 (only the portal in Thetford)");
        Check(Send(o, otherPeer, new ResetPortalBindingRequest(), 8) is [{ ReturnCode: 0 }] && o.Player!.Character.PortalBinding is { City: null, RemovedAt: > 0 },
              "ResetPortalBinding: RC 0, not bound, the removal stamped");
        Check(Send(o, otherPeer, new UsePortalEntranceRequest { aih = otherGate.Id, aii = OtherTownExit }, 9) is [{ ReturnCode: 0 }, { ReturnCode: 0 } lym] && lym.Parameters[0] is "1301"
              && world.Saves.Load(bound.Id) is { Cluster: "1301", PortalBinding: { City: PortalKind.Lymhurst, RemovedAt: > 0 } },
              "then Lymhurst's Realmgate opens: into 1301, bound to Lymhurst, the removal time kept");
        world.Dispose();
        characters.Dispose();
        try { Directory.Delete(dir, true); } catch (IOException) { }
    }

    private static void Check(bool cond, string what) => AlbionTests.Check(cond, what);
}
