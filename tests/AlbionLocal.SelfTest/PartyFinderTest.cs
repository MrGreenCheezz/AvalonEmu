using AlbionLocal.Photon.Serialization;
using AlbionLocal.Server;
using AlbionLocal.Server.Content;
using AlbionLocal.Server.Entities;
using AlbionLocal.Server.Game;
using AlbionLocal.Server.Network;
using AlbionLocal.Server.Persistence;
using AlbionLocal.Server.Protocol;
using AlbionLocal.Server.Systems;
using static AlbionLocal.SelfTest.AlbionTests;
using static AlbionLocal.SelfTest.CombatTest;

namespace AlbionLocal.SelfTest;

/// <summary>
/// StageR party finder (analysis/work/stageR/party/SPEC.md §5): three players in one World through <see cref="FakePeer"/>s list, see,
/// apply, accept, decline and delist, and the feature switch 171 the Login table carries.
/// </summary>
public static class PartyFinderTest
{
    public static IEnumerable<(string, Action)> All =>
    [
        ("party finder: switch 171 on; list 361, see 359/370, apply 365, 372, decline 374, accept → party, 368/369, snapshot 373, delete and fill delist", Flow),
    ];

    private sealed record Member(WorldSession Session, FakePeer Peer, Player Player)
    {
        public Guid Id => Player.Character.Id;
    }

    private sealed class Town : IDisposable
    {
        private readonly string _dir = NewDir();
        private readonly SqliteCharacterRepository _characters;
        public readonly World World;

        public Town()
        {
            _characters = Repository(_dir);
            World = new World(new ServerConfig { JoinByCharacterId = true }, _characters, Items()) { Sync = new object(), Clock = new ManualClock() };
        }

        public Member Join(string name, float x)
        {
            var c = StarterKit.Default.NewCharacter(name);
            (c.Cluster, c.PositionX, c.PositionY) = ("0000", x, 5);
            c.Equipment = [];
            c.Inventory = [];
            AlbionTests.Check(_characters.Create(c), $"character {name} created");
            var peer = new FakePeer();
            var session = new WorldSession(peer, World);
            peer.Disconnected = session.OnDisconnected;
            session.OnRequest(Request(new JoinRequest { ss = c.Id }, 1), encrypted: false);
            return new Member(session, peer, session.Player!);
        }

        public void Dispose()
        {
            World.Dispose();
            _characters.Dispose();
            try { Directory.Delete(_dir, true); } catch (IOException) { }
        }
    }

    private static void Check(bool cond, string what) => AlbionTests.Check(cond, what);

    /// <summary>A request that is answered: its one response.</summary>
    private static OperationResponse Call(Member c, AlbionRequest req)
    {
        var n = c.Peer.Responses.Count;
        c.Session.OnRequest(Request(req, 7), encrypted: false);
        Check(c.Peer.Responses.Count == n + 1, $"one response to {req.GetType().Name}");
        return c.Peer.Responses[^1];
    }

    /// <summary>A request the official server does not answer: no response.</summary>
    private static void Send(Member c, AlbionRequest req)
    {
        var n = c.Peer.Responses.Count;
        c.Session.OnRequest(Request(req, 7), encrypted: false);
        Check(c.Peer.Responses.Count == n, $"no response to {req.GetType().Name}");
    }

    private static List<Dictionary<byte, object?>> Of(Member c, EventCodes code, int mark) =>
        [.. Since(c.Peer, mark).Where(e => e.Code == code).Select(e => e.P)];

    private static Guid G(object? v) => new((byte[])v!);

    private static Guid[] Gs(object? v) => [.. ((byte[])v!).Chunk(16).Select(b => new Guid(b))];

    private static int I(Dictionary<byte, object?> e, byte k) => e.TryGetValue(k, out var v) && v is not null ? Convert.ToInt32(v) : 0;

    private static PartyFinderEnlistNewPartySearchRequest Listing(int max, bool auto) =>
        new() { w5 = 0, w6 = 0, w7 = max, w8 = auto, w9 = "Dungeon run", xa = "bring heals", xb = 0 };

    private static void Flow()
    {
        Check(LoginWorld.Features()[FeatureSwitches.PartyFinder] && !LoginWorld.Features(false)[FeatureSwitches.PartyFinder]
              && !FeatureSwitches.Defaults()[FeatureSwitches.PartyFinder], "Login key 99: [171] PartyFinder on (official), off with Party:Finder false (the client default)");

        using var town = new Town();
        var (a, b, c) = (town.Join("Anna", 5), town.Join("Boris", 10), town.Join("Carl", 15));
        var finder = town.World.Parties.Finder;

        // A watches activity 0: FullUpdate with no listing (key 2 an empty Guid[], the nested arrays left out).
        var am = a.Peer.Events.Count;
        Send(a, new PartyFinderRegisterForUpdatesRequest { xn = 0 });
        Check(Of(a, EventCodes.PartyFinderFullUpdate, am) is [var empty] && empty[2] is byte[] { Length: 0 } && !empty.ContainsKey(4) && !empty.ContainsKey(10),
              "359: FullUpdate {2 empty Guid[]}, nothing nested");

        // A lists its (solo) party: rc 0 with the request id, then Update kind 0 of list 0 keyed by A.
        am = a.Peer.Events.Count;
        var r = Call(a, Listing(3, auto: false));
        Check(r.ReturnCode == 0 && r.Parameters[255] is 7, "361: rc 0 with the request id");
        Check(Of(a, EventCodes.PartyFinderUpdate, am) is [var mine] && I(mine, 0) == PartyFinder.KindNew && I(mine, 1) == 0 && G(mine[2]) == a.Id
              && mine[4] is "Anna" && mine[5] is string[] and ["Anna"] && mine[10] is float[] { Length: 1 } && mine[11] is int[][] and [[-1]] && I(mine, 14) == 3
              && mine[16] is "Dungeon run" && mine[17] is "bring heals",
              "A: 371 {0 new, 1 parties, 2 A, 4 Anna, 5 [Anna], 10 one IP, 11 [[-1]], 14 size 3, 16/17 texts}");

        // B watches: one listing, every per-listing array of length 1 and every per-member row of length 1.
        var bm = b.Peer.Events.Count;
        Send(b, new PartyFinderRegisterForUpdatesRequest { xn = 0 });
        Check(Of(b, EventCodes.PartyFinderFullUpdate, bm) is [var full] && Gs(full[2]) is [var g] && g == a.Id && full[3] is string[] and ["Anna"]
              && full[4] is string[][] and [["Anna"]] && full[5] is int[][] and [[0]] && full[6] is int[][] and [[0]] && full[7] is byte[] && full[8] is byte[]
              && full[9] is float[][] and [{ Length: 1 }] && full[10] is int[][][] and [[[-1]]] && full[11] is byte[] { Length: 4 }
              && full[14] is int[] and [3] && full[15] is string[] and ["Dungeon run"] && full[16] is string[] and ["bring heals"] && full[17] is byte[] { Length: 4 }
              && full[18] is bool[] and [false],
              "B: 370 with A's listing, all arrays of one entry (11/17 packed 4-byte enums)");
        Send(b, new PartyFinderFulltextSearchRequest { xc = 0, xd = "nothing like it" });
        Check(Of(b, EventCodes.PartyFinderFullUpdate, bm) is [_, var none] && none[2] is byte[] { Length: 0 }, "370 search without a match: an empty FullUpdate");

        // C registers as looking for a party (roles tank), then unregisters and registers again.
        var cm = c.Peer.Events.Count;
        r = Call(c, new PartyFinderRegisterApplicantsRequest { xg = [0, 1, 0], xh = 0, xi = 0, xj = 20, xk = "", xl = "" });
        Check(r.ReturnCode == 0 && r.Parameters[255] is 7 && Of(c, EventCodes.PartyFinderUpdate, cm) is [var reg] && I(reg, 0) == PartyFinder.KindNew
              && I(reg, 1) == PartyFinder.ListOwnApplicant && G(reg[2]) == c.Id && reg[11] is int[][] and [[0, 1, 0]] && I(reg, 14) == 20,
              "368: rc 0 with the id, 371 {0 new, 1 own, 2 C, 11 [[0,1,0]], 14 20} to C");
        Check(c.Peer.Sequence[^2] is OperationResponse && c.Peer.Sequence[^1] is EventData, "the response goes before the update, as captured");
        r = Call(c, new PartyFinderUnregisterApplicantsRequest());
        Check(r.ReturnCode == 0 && !r.Parameters.ContainsKey(255) && Of(c, EventCodes.PartyFinderUpdate, cm) is [_, var gone] && I(gone, 0) == PartyFinder.KindClosed
              && finder.ApplicantOf(c.Id) is null, "369: rc 0 without the request id (as captured), 371 kind 3 to C");
        Call(c, new PartyFinderRegisterApplicantsRequest { xg = [0, 1, 0], xh = 0, xj = 20 });

        // B applies: pending 14611, again 14609; A gets 372 with B; A applying to itself 14607; an unknown key 14602.
        am = a.Peer.Events.Count;
        Check(Call(b, new PartyFinderApplyForGroupRequest { wv = a.Id }).ReturnCode == PartyFinder.ApplyPending, "365 B → A: 14611 pending");
        Check(Call(b, new PartyFinderApplyForGroupRequest { wv = a.Id }).ReturnCode == PartyFinder.ApplyAlreadyPending, "again: 14609");
        Check(Call(a, new PartyFinderApplyForGroupRequest { wv = a.Id }).ReturnCode == PartyFinder.ApplyOwnSearch
              && Call(b, new PartyFinderApplyForGroupRequest { wv = Guid.NewGuid() }).ReturnCode == PartyFinder.ApplyGroupNotKnown, "own search 14607, unknown 14602");
        Check(Of(a, EventCodes.PartyFinderApplicantsUpdate, am) is [var apps] && Gs(apps[0]) is [var ap] && ap == b.Id && apps[1] is string[] and ["Boris"]
              && apps[2] is string[][] and [["Boris"]] && apps[3] is int[][] and [[0]] && apps[4] is int[][] and [[0]] && apps[5] is byte[] && apps[6] is byte[]
              && apps[7] is float[][] and [{ Length: 1 }] && apps[8] is int[][][] and [[[-1]]] && apps[9] is byte[] { Length: 4 } && apps[11] is int[] and [3],
              "A: 372 {0 [B], 1 [Boris], 2 [[Boris]], one entry per array}");

        // A declines B: 374 {0 A, 1 Anna, 2/3 false} to B, 372 without applicants to A.
        (am, bm) = (a.Peer.Events.Count, b.Peer.Events.Count);
        Check(Call(a, new PartyFinderAcceptOrDeclineApplyForGroupRequest { wt = b.Id, wu = false }).ReturnCode == 0
              && Of(b, EventCodes.PartyFinderJoinRequestDeclined, bm) is [var dec] && G(dec[0]) == a.Id && dec[1] is "Anna" && !dec.ContainsKey(2) && !dec.ContainsKey(3)
              && Of(a, EventCodes.PartyFinderApplicantsUpdate, am) is [var cleared] && cleared[0] is byte[] { Length: 0 } && !cleared.ContainsKey(2)
              && town.World.Parties.Of(b.Id) is null, "366 decline: 374 to B, an empty 372 to A, no party");
        Check(Call(a, new PartyFinderAcceptOrDeclineApplyForGroupRequest { wt = b.Id, wu = true }).ReturnCode == PartyFinder.ApplyApplicantNotKnown,
              "answered twice: 14601");

        // C applies and A accepts: C joins A's new party (230), its registration closes (kind 3), B sees the listing with two members.
        Check(Call(c, new PartyFinderApplyForGroupRequest { wv = a.Id }).ReturnCode == PartyFinder.ApplyPending, "C applies: 14611");
        (bm, cm) = (b.Peer.Events.Count, c.Peer.Events.Count);
        Check(Call(a, new PartyFinderAcceptOrDeclineApplyForGroupRequest { wt = c.Id, wu = true }).ReturnCode == 0, "366 accept C: rc 0");
        Check(town.World.Parties.Of(c.Id) is { Leader: var lead, Members.Count: 2 } && lead == a.Id && Of(c, EventCodes.PartyJoined, cm).Count == 1
              && Of(c, EventCodes.PartyFinderUpdate, cm).Any(e => I(e, 1) == PartyFinder.ListOwnApplicant && I(e, 0) == PartyFinder.KindClosed)
              && finder.ApplicantOf(c.Id) is null, "C is in A's party (230), its registration closed (371 list 1 kind 3)");
        Check(Of(b, EventCodes.PartyFinderUpdate, bm).LastOrDefault() is { } two && I(two, 0) == PartyFinder.KindChanged && two[5] is string[] and ["Anna", "Carl"]
              && two[10] is float[] { Length: 2 } && two[11] is int[][] and [[-1], [0, 1, 0]],
              "B: 371 kind 1 with [Anna, Carl], two IPs, roles [[-1], [0,1,0]]");

        // B asks for Carl's equipment: 373 {0 Carl}, one entry per equipment slot.
        bm = b.Peer.Events.Count;
        Send(b, new PartyFinderGetEquipmentSnapshotRequest { xf = "carl" });
        Check(Of(b, EventCodes.PartyFinderEquipmentSnapshot, bm) is [var snap] && snap[0] is "Carl" && snap[1] is int[] { Length: var slots } && slots > 0
              && snap[2] is byte[] { Length: var q } && q == slots && snap[4] is string[] { Length: var s4 } && s4 == slots, "367: 373 {0 Carl, 1-6 one entry per slot}");

        // A deletes the search: A (and C) get kind 3, B kind 2.
        (am, bm, cm) = (a.Peer.Events.Count, b.Peer.Events.Count, c.Peer.Events.Count);
        Check(Call(a, new PartyFinderDeletePartySearchRequest()).ReturnCode == 0 && finder.ListingOf(a.Id) is null, "362: rc 0, the listing is gone");
        Check(Of(a, EventCodes.PartyFinderUpdate, am) is [var closed] && I(closed, 0) == PartyFinder.KindClosed
              && Of(c, EventCodes.PartyFinderUpdate, cm) is [var closedC] && I(closedC, 0) == PartyFinder.KindClosed
              && Of(b, EventCodes.PartyFinderUpdate, bm) is [var removed] && I(removed, 0) == PartyFinder.KindRemoved && G(removed[2]) == a.Id,
              "the party gets 371 kind 3 (closes the window), the watcher B kind 2");

        // A lists again with auto accept and size 3: B applies, joins at once (rc 0), and the full party's listing goes.
        Check(Call(a, Listing(3, auto: true)).ReturnCode == 0, "361 again, auto accept, size 3");
        bm = b.Peer.Events.Count;
        Check(Call(b, new PartyFinderApplyForGroupRequest { wv = a.Id }).ReturnCode == 0 && town.World.Parties.Of(b.Id)?.Leader == a.Id
              && town.World.Parties.Of(a.Id)!.Members.Count == 3, "365 to an auto-accept listing: rc 0, B joins (3 members)");
        Check(finder.ListingOf(a.Id) is null && Of(b, EventCodes.PartyFinderUpdate, bm).Any(e => I(e, 0) == PartyFinder.KindClosed),
              "the party is full: the listing goes (kind 3 to the party, B included)");

        // A logs out: nothing of it is left in the finder (its listing would go; its watch ends).
        Check(Call(a, Listing(5, auto: false)).ReturnCode == 0 && finder.ListingOf(a.Id) is not null, "361 once more");
        a.Peer.Disconnect();
        Check(finder.ListingOf(a.Id) is null && finder.SubscriptionOf(a.Id) is null && town.World.Parties.Of(c.Id) is { Leader: var nl } && nl != a.Id,
              "A logs out: its listing and watch are gone, the party has a new leader");
    }
}
