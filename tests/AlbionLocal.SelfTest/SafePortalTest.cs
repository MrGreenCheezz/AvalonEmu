using System.Numerics;
using AlbionLocal.Photon;
using AlbionLocal.Photon.Serialization;
using AlbionLocal.Server;
using AlbionLocal.Server.Content;
using AlbionLocal.Server.Entities;
using AlbionLocal.Server.Game;
using AlbionLocal.Server.Network;
using AlbionLocal.Server.Persistence;
using AlbionLocal.Server.Protocol;
using static AlbionLocal.SelfTest.AlbionTests;

namespace AlbionLocal.SelfTest;

/// <summary>
/// The S.A.F.E. portal of the Outlands portal towns (analysis/work/stageR/safe-portal/SPEC.md) on the client's data: Thetford Portal 0301,
/// one of its outlandsteleportationportals.xml destinations, the return portal and the way back, through FakePeer sessions.
/// </summary>
public static class SafePortalTest
{
    public static IEnumerable<(string, Action)> All =>
    [
        ("safe portal: 0301 -> random destination -> return portal -> 0301 (606, op 517 costs, 1672 on a stale cost, paid use with binding keys 127-131, 607, way back clears it)", ThereAndBack),
    ];

    private const string Town = "0301";

    private static T Fill<T>(OperationResponse r) where T : new()
    {
        var m = new T();
        AlbionSerializer.FillFields(m, r.Parameters);
        return m;
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
        _ = store.Get(Town);
        var dir = NewDir();
        var characters = Repository(dir);
        var world = new World(new ServerConfig { JoinByCharacterId = true, Transfer = new TransferOptions { CheckExitCooldown = false } }, characters, Client.Items)
            { Sync = new object(), Clock = new ManualClock(), Clusters = store };
        var data = store.SafePortals;
        Check(data.Destinations.TryGetValue(Town, out var destinations) && destinations.Count > 10 && data.ReturnSeconds == 5400 && data.MinCost == 10_000 * 10_000L,
              "outlandsteleportationportals.xml: 0301's destinations, 5400 s, 10 000 silver at least");
        foreach (var d in destinations) _ = store.Get(d); // resolved now: the use answers at once (else after World.Post)

        var hero = StarterKit.Default.NewCharacter("S" + Guid.NewGuid().ToString("N")[..10]);
        (hero.Cluster, hero.PositionX, hero.PositionY, hero.Silver) = (Town, -31, 72, 1_000_000 * 10_000L);
        Check(characters.Create(hero), "character created");
        var (s, peer, _) = Join(world, hero.Id);
        var portal = world.Maps.Find(Town)!.Objects.Values.OfType<SafePortal>().Single();
        Check(portal.Position == new Vector2(-31, 76) && portal.BuildCreate(s.Player!).Single() is NewOutlandsTeleportationPortalEvent { d7c: var guid } && guid != Guid.Empty
              && s.Player!.Visibility.Known.Contains(portal), $"0301 has its S.A.F.E. portal at (-31, 76) with its world.xml Guid, in view (got {portal})");

        // The window's costs: no use yet, so the travel cost is the baggage cost (at least 10 000 silver).
        var quote = Send(s, peer, new RequestOutlandsTeleportationUsageRequest(), 1);
        Check(quote is [{ ReturnCode: 0 } q] && q.Parameters[1] is long initial && initial >= data.MinCost && q.Parameters[2] is long travel && travel == initial
              && !q.Parameters.ContainsKey(0), $"op 517: {{1 initial, 2 travel}} ≥ 10 000 silver, no uses (got {quote.Count} responses)");
        var cost = (long)quote[0].Parameters[2];
        Check(Send(s, peer, new ChangeClusterRequest { ee = portal.Id, ef = "", ei = cost * 2 }, 2) is [{ ReturnCode: 1672 }] && s.Status == SessionStatus.InWorld,
              "a stale cost: 1672 SilverCostsChanged");

        var used = Send(s, peer, new ChangeClusterRequest { ee = portal.Id, ef = "", ei = cost }, 3);
        var saved = world.Saves.Load(hero.Id)!;
        Check(used is [{ ReturnCode: 0 } go] && go.Parameters[0] is string target && destinations.Contains(target) && s.Status == SessionStatus.Transferring
              && saved is { Cluster: var cl, SafePortal.Binding: { Town: Town } b } && cl == target && b.Cluster == target && saved.Silver == hero.Silver - cost
              && saved.SafePortal!.Uses.Length == 1,
              $"the use: RC 0 {{0: a destination}}, the cost paid, the binding to 0301 saved (got {used.Count} responses, cluster {saved.Cluster})");

        // The destination: the binding in the Join, the personal return portal next to the arrival.
        var (t, tPeer, j) = Join(world, hero.Id);
        var binding = t.Player!.Character.SafePortal!.Binding!;
        var back = world.SafePortals.ReturnPortalOf(hero.Id);
        Check(j.bbt == Town && j.bbu == binding.Cluster && j.bbw == binding.Id && j.bbv == binding.Until && j.bbx == new Vector2(binding.X, binding.Y)
              && back is { Map: not null } && back.Map.Cluster == binding.Cluster && t.Player.Visibility.Known.Contains(back)
              && back.BuildCreate(t.Player).Single() is NewOutlandsTeleportationReturnPortalEvent { d7i: var id, d7j: "OUTLANDS_TELEPORTATION_RETURN_PORTAL" } && id == binding.Id
              && Vector2.Distance(j.a8j, back.Position) < 5,
              $"Join keys 127-131 carry the binding; the return portal stands by the arrival with key 3 = the binding id (got {j.bbt}/{j.bbu}, portal {back})");

        // The way back: into 0301, the binding gone.
        var home = Send(t, tPeer, new ChangeClusterRequest { ee = back!.Id, ef = "", ei = 0 }, 4);
        Check(home is [{ ReturnCode: 0 } h] && h.Parameters[0] is Town && world.Saves.Load(hero.Id) is { Cluster: Town, SafePortal: { Binding: null, Uses.Length: 1 } }
              && world.SafePortals.ReturnPortalOf(hero.Id) is null,
              $"the return portal: RC 0 {{0: \"0301\"}}, the binding cleared, the portal gone (got {home.Count} responses)");
        var (_, _, again) = Join(world, hero.Id);
        Check(again.a8i == Town && string.IsNullOrEmpty(again.bbw) && again.bbv == 0, "back in 0301 without binding keys");
        world.Dispose();
        characters.Dispose();
        try { Directory.Delete(dir, true); } catch (IOException) { }
    }

    private static void Check(bool cond, string what) => AlbionTests.Check(cond, what);
}
