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
/// StageR party (analysis/work/stageR/party/SPEC.md): ready check, party navigation, FW reinforcements flag and join requests,
/// with several players in one World through <see cref="FakePeer"/>s.
/// </summary>
public static class PartyExtrasTest
{
    public static IEnumerable<(string, Action)> All =>
    [
        ("party ready check: 512 errors 20481/20482/20483/20484, 245 to all (leader ready), 513 answers, 20520 outside", ReadyCheck),
        ("party navigation: 547 leader only, 247 to the others, PartyJoined keys 5-7 for a joiner, 548 and a leader change send 248; 235 flag and 246", Navigation),
        ("party join requests: 226 errors, 229 to the leader (also via a member), decline 240, accept 230/232/240, 9120, expiry 504/240", JoinRequests),
    ];

    private sealed record Member(WorldSession Session, FakePeer Peer, Player Player)
    {
        public Guid Id => Player.Character.Id;
    }

    private sealed class Town : IDisposable
    {
        private readonly string _dir = NewDir();
        public readonly ManualClock Clock = new();
        private readonly SqliteCharacterRepository _characters;
        public readonly World World;

        public Town()
        {
            _characters = Repository(_dir);
            World = new World(new ServerConfig { JoinByCharacterId = true }, _characters, Items()) { Sync = new object(), Clock = Clock };
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

        public void Run(double seconds)
        {
            for (var i = 0; i < Math.Round(seconds * 20); i++)
            {
                Clock.Advance(TimeSpan.FromMilliseconds(50));
                lock (World.Sync) World.Update();
            }
        }

        public void Dispose()
        {
            World.Dispose();
            _characters.Dispose();
            try { Directory.Delete(_dir, true); } catch (IOException) { }
        }
    }

    private static void Check(bool cond, string what) => AlbionTests.Check(cond, what);

    private static OperationResponse Call(Member c, AlbionRequest req)
    {
        var n = c.Peer.Responses.Count;
        c.Session.OnRequest(Request(req, 7), encrypted: false);
        Check(c.Peer.Responses.Count == n + 1, $"one response to {req.GetType().Name}");
        return c.Peer.Responses[^1];
    }

    private static List<Dictionary<byte, object?>> Of(Member c, EventCodes code, int mark) =>
        [.. Since(c.Peer, mark).Where(e => e.Code == code).Select(e => e.P)];

    private static Guid G(object? v) => new((byte[])v!);

    private static Guid[] Gs(object? v) => [.. ((byte[])v!).Chunk(16).Select(b => new Guid(b))];

    private static void Into(Member leader, Member joiner) =>
        Check(Call(leader, new PartyInvitePlayerRequest { xp = joiner.Id }).ReturnCode == 0
              && Call(joiner, new PartyAnswerInvitationRequest { wi = true, wk = Parties.NoReason }).ReturnCode == 0,
              $"{joiner.Player.Character.Name} joins {leader.Player.Character.Name}'s party");

    private static short Begin(Member c) => Call(c, new PartyReadyCheckBeginRequest()).ReturnCode;

    private static short Ready(Member c, bool ready) => Call(c, new PartyReadyCheckUpdateRequest { xy = ready }).ReturnCode;

    private static void ReadyCheck()
    {
        using var town = new Town();
        var (a, b, c) = (town.Join("Anna", 5), town.Join("Boris", 10), town.Join("Carl", 15));
        Check(Begin(a) == Parties.ReadyNoParty, "no party: 20481");
        Into(a, b);
        Check(Begin(b) == Parties.ReadyNotLeader && Begin(c) == Parties.ReadyNoParty, "a member: 20482, an outsider: 20481");

        var (am, bm, cm) = (a.Peer.Events.Count, b.Peer.Events.Count, c.Peer.Events.Count);
        Check(Begin(a) == 0, "the leader starts it: rc 0");
        foreach (var (x, m) in new[] { (a, am), (b, bm) })
            Check(Of(x, EventCodes.PartyReadyCheckUpdate, m) is [var e] && Convert.ToInt64(e[0]) > DateTime.UtcNow.Ticks + TimeSpan.FromSeconds(50).Ticks
                  && Convert.ToInt64(e[1]) > Convert.ToInt64(e[0]) && Gs(e[2]) is [var g0, var g1] && g0 == a.Id && g1 == b.Id && e[3] is byte[] and [1, 0],
                  $"{x.Player.Character.Name}: 245 {{0 end ≈ now + 60 s, 1 the wait after it, 2 [Anna, Boris], 3 [1 ready, 0 none]}}");
        Check(c.Peer.Events.Count == cm, "the outsider gets nothing");
        Check(Begin(a) == Parties.ReadyRunning, "a second one while it runs: 20483");

        (am, bm) = (a.Peer.Events.Count, b.Peer.Events.Count);
        Check(Ready(b, false) == 0 && Of(a, EventCodes.PartyReadyCheckUpdate, am) is [var no] && no[3] is byte[] and [1, 2]
              && Of(b, EventCodes.PartyReadyCheckUpdate, bm).Count == 1, "B answers not ready: 245 [1, 2] to both");
        Check(Ready(b, true) == 0 && Of(a, EventCodes.PartyReadyCheckUpdate, am) is [_, var yes] && yes[3] is byte[] and [1, 1], "B changes to ready: 245 [1, 1]");
        Check(Ready(c, true) == Parties.ReadyNotRunning, "an outsider's answer: 20520");

        town.Clock.Advance(TimeSpan.FromSeconds(60));
        Check(Ready(b, true) == Parties.ReadyNotRunning && Begin(a) == Parties.ReadyCooldown, "after 60 s: an answer 20520, a new check 20484 (the wait)");
        town.Clock.Advance(TimeSpan.FromSeconds(60));
        Check(Begin(a) == 0, "after the wait: a new check, rc 0");
    }

    private static void Navigation()
    {
        using var town = new Town();
        var (a, b, c) = (town.Join("Anna", 5), town.Join("Boris", 10), town.Join("Carl", 15));
        Into(a, b);
        var nav = NavFrom(a);
        Check(Call(b, NavFrom(b)).ReturnCode == Parties.LeaderNotLeader, "a member: 9242");

        var (am, bm) = (a.Peer.Events.Count, b.Peer.Events.Count);
        var r = Call(a, nav);
        Check(r.ReturnCode == 0 && r.Parameters[255] is 7, "the leader: rc 0 with the request id");
        Check(Of(b, EventCodes.SetPartyNavigation, bm) is [var e] && e[0] is "1234" && e[1] is true && !e.ContainsKey(2)
              && Of(a, EventCodes.SetPartyNavigation, am).Count == 0, "B gets 247 {0 \"1234\", 1 true}, the leader nothing");

        var cm = c.Peer.Events.Count;
        Into(a, c);
        Check(Of(c, EventCodes.PartyJoined, cm) is [var j] && j[5] is "1234" && j[6] is true, "C joins: PartyJoined keys 5 \"1234\", 6 true");

        (am, bm, cm) = (a.Peer.Events.Count, b.Peer.Events.Count, c.Peer.Events.Count);
        Check(Call(a, new EndPartyNavigationRequest { j7 = a.Id }).ReturnCode == 0
              && Of(b, EventCodes.EndPartyNavigation, bm).Count == 1 && Of(c, EventCodes.EndPartyNavigation, cm).Count == 1
              && Of(a, EventCodes.EndPartyNavigation, am).Count == 0 && town.World.Parties.Of(a.Id)!.Navigation is null, "548: 248 to B and C");

        Check(Call(a, nav).ReturnCode == 0, "set again");
        bm = b.Peer.Events.Count;
        Check(Call(a, new PartyMakeLeaderRequest { xu = c.Id }).ReturnCode == 0 && Of(b, EventCodes.EndPartyNavigation, bm).Count == 1,
              "a leader change ends it: 248");

        // FW reinforcements flag (op 235): the leader (now C) only; 246 {0 party id, 1 flag} to all; PartyJoined key 3.
        var id = town.World.Parties.Of(a.Id)!.Id;
        Check(Call(a, new PartyChangeFactionWarfareRequestReinforcementsSettingRequest { wr = true }).ReturnCode == Parties.KickNotLeader, "235 from a member: 9202");
        am = a.Peer.Events.Count;
        Check(Call(c, new PartyChangeFactionWarfareRequestReinforcementsSettingRequest { wr = true }).ReturnCode == 0
              && Of(a, EventCodes.PartyFactionWarfareReinforcementSettingChangedPlayer, am) is [var fw] && Convert.ToInt32(fw[0]) == id && fw[1] is true
              && Parties.Joined(town.World.Parties.Of(a.Id)!).dii, "235 from the leader: 246 {id, true}, PartyJoined key 3 true");
    }

    private static SetPartyNavigationRequest NavFrom(Member m) => new() { afy = "1234", afz = m.Id, af0 = true };

    private static short RequestJoin(Member from, Member? to, string? name = null) =>
        Call(from, new PartyRequestJoinRequest { xz = to?.Id ?? Guid.Empty, x0 = name }).ReturnCode;

    private static short AnswerJoin(Member leader, Member requester, bool accept) =>
        Call(leader, new PartyAnswerJoinRequestRequest { wl = accept, wn = requester.Id, wo = accept ? Parties.NoReason : Parties.DeclinedViaUi }).ReturnCode;

    private static void JoinRequests()
    {
        using var town = new Town();
        var (a, b, d, e, f, g) = (town.Join("Anna", 5), town.Join("Boris", 10), town.Join("Dora", 15), town.Join("Emil", 20), town.Join("Fritz", 25), town.Join("Gina", 30));
        Into(a, b);
        Check(RequestJoin(d, null, "Nobody") == Parties.PlayerNotFound && RequestJoin(d, d) == Parties.PlayerNotFound, "unknown or oneself: 9000");
        Check(RequestJoin(b, null, "Dora") == Parties.JoinAlreadyInParty, "a requester in a party: 9004");

        // D asks Anna by name: rc 0 {0 "Anna"}; A gets 229 {0 D, 1 "Dora"}; again: 9008.
        var am = a.Peer.Events.Count;
        var r = Call(d, new PartyRequestJoinRequest { x0 = "anna" });
        Check(r.ReturnCode == 0 && r.Parameters[0] is "Anna" && r.Parameters[255] is 7, "D asks Anna: rc 0 {0 Anna}");
        Check(Of(a, EventCodes.PartyJoinRequest, am) is [var jr] && G(jr[0]) == d.Id && jr[1] is "Dora", "A gets 229 {0 D, 1 Dora}");
        Check(RequestJoin(d, a) == Parties.JoinPendingRequest, "a second request: 9008");

        // E asks Boris (a member): the request goes to the leader A. A declines: E gets 240 {0 Anna, 1 false, 3 0}.
        am = a.Peer.Events.Count;
        var bm = b.Peer.Events.Count;
        Check(RequestJoin(e, b) == 0 && Of(a, EventCodes.PartyJoinRequest, am) is [var viaB] && G(viaB[0]) == e.Id && Of(b, EventCodes.PartyJoinRequest, bm).Count == 0,
              "E asks Boris: 229 goes to the leader A, not to B");
        var em = e.Peer.Events.Count;
        Check(AnswerJoin(a, e, false) == 0 && Of(e, EventCodes.PartyJoinRequestAnswer, em) is [var no] && no[0] is "Anna" && !no.ContainsKey(1)
              && town.World.Parties.Of(e.Id) is null, "A declines E: 240 {0 Anna, 1 false}, no party");
        Check(AnswerJoin(b, d, true) == Parties.JoinRequestGone, "B answers D's request (not its own): 9120");

        // A accepts D: D gets 230 with three members and 240 {1 true}; A and B get 232 for D.
        var dm = d.Peer.Events.Count;
        (am, bm) = (a.Peer.Events.Count, b.Peer.Events.Count);
        Check(AnswerJoin(a, d, true) == 0, "A accepts D: rc 0");
        Check(Of(d, EventCodes.PartyJoined, dm) is [var j] && j[9] is string[] and ["Anna", "Boris", "Dora"]
              && Of(d, EventCodes.PartyJoinRequestAnswer, dm) is [var yes] && yes[0] is "Anna" && yes[1] is true,
              "D: 230 with Anna, Boris, Dora and 240 {0 Anna, 1 true}");
        Check(new[] { (a, am), (b, bm) }.All(x => Of(x.Item1, EventCodes.PartyPlayerJoined, x.Item2) is [var pj] && G(pj[1]) == d.Id), "A and B: 232 for D");
        Check(AnswerJoin(a, d, true) == Parties.JoinRequestGone, "answered twice: 9120");

        // F asks the solo G; it expires after 65 s: G gets 504 {0 F, 1 Fritz}, F gets 240 {0 Gina, 2 true, 3 1}.
        Check(RequestJoin(f, g) == 0, "F asks the solo G");
        var (fm, gm) = (f.Peer.Events.Count, g.Peer.Events.Count);
        town.Run(65.1);
        Check(Of(g, EventCodes.PartyJoinRequestAborted, gm) is [var ab] && G(ab[0]) == f.Id && ab[1] is "Fritz"
              && Of(f, EventCodes.PartyJoinRequestAnswer, fm) is [var to] && to[0] is "Gina" && to[2] is true && Convert.ToInt32(to[3]) == Parties.TimedOut,
              "expiry: 504 to G, 240 {timed out} to F");
        // Again, accepted: a new party led by G.
        Check(RequestJoin(f, g) == 0 && AnswerJoin(g, f, true) == 0 && town.World.Parties.Of(f.Id) is { } p && p.Leader == g.Id && p.Members.Count == 2,
              "F asks G again, G accepts: a new party of two led by G");
    }
}
