using System.Numerics;
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
/// Parties (analysis/work/stageD/party/SPEC.md §9): two to four players in one World through <see cref="FakePeer"/>s: invite,
/// the error codes, accept, decline, expiry and auto-decline; leave, disband, kick, leader, disconnect and a reconnect; party
/// chat across maps; fame sharing and loot rights on a ghoul in 0204.
/// </summary>
public static class PartyTest
{
    public static IEnumerable<(string, Action)> All =>
    [
        ("party: invite (exact response, 228), errors 9000/9002/9004/9007/9009/9012, decline, expiry, auto-decline, accept (230 wire types, 239), third member (232)", Invites),
        ("party: leave, disband, kick, make leader, disconnect, reconnect without 232, 231 on arrival after a change", LeaveKick),
        ("party: party Say to a member on another map with key 7, not to observers", Chat),
        ("party: fame shared within 50 m (GroupBonus, max(n, mindivisor)), NewLoot key 11, a member opens the bag, an outsider not", FameAndLoot),
        ("party role: PartyJoined key 15 is -1 (none) not 0, op 234 stores and relays 243, a role below -1 is ignored, the role survives a re-send", Roles),
    ];

    private sealed record Member(WorldSession Session, FakePeer Peer, Player Player)
    {
        public Guid Id => Player.Character.Id;
    }

    private sealed class Town : IDisposable
    {
        public readonly string Dir = NewDir();
        public readonly ManualClock Clock = new();
        public readonly SqliteCharacterRepository Characters;
        public readonly World World;

        public Town(int maxSize = 20)
        {
            Characters = Repository(Dir);
            World = new World(new ServerConfig { JoinByCharacterId = true, PartyMaxSize = maxSize }, Characters, Items()) { Sync = new object(), Clock = Clock };
        }

        /// <summary>A character in <paramref name="cluster"/> at <paramref name="x"/>, joined.</summary>
        public Member Join(string name, float x, string cluster = "0000")
        {
            var c = StarterKit.Default.NewCharacter(name);
            (c.Cluster, c.PositionX, c.PositionY) = (cluster, x, 5);
            c.Equipment = [];
            c.Inventory = [];
            Check(Characters.Create(c), $"character {name} created");
            return Again(c.Id);
        }

        /// <summary>A (further) connection that joins character <paramref name="id"/>.</summary>
        public Member Again(Guid id)
        {
            var peer = new FakePeer();
            var session = new WorldSession(peer, World);
            peer.Disconnected = session.OnDisconnected;
            session.OnRequest(Request(new JoinRequest { ss = id }, 1), encrypted: false);
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
            Characters.Dispose();
            try { Directory.Delete(Dir, true); } catch (IOException) { }
        }
    }

    private static void Check(bool cond, string what) => AlbionTests.Check(cond, what);

    /// <summary>Sends <paramref name="req"/> with request id 7 and returns its response.</summary>
    private static OperationResponse Call(Member c, AlbionRequest req)
    {
        var n = c.Peer.Responses.Count;
        c.Session.OnRequest(Request(req, 7), encrypted: false);
        Check(c.Peer.Responses.Count == n + 1, $"one response to {req.GetType().Name}");
        return c.Peer.Responses[^1];
    }

    private static short Invite(Member from, Member? to, string? name = null) =>
        Call(from, new PartyInvitePlayerRequest { xp = to?.Id ?? Guid.Empty, xq = name ?? to?.Player.Character.Name }).ReturnCode;

    private static short Answer(Member c, bool accept, bool auto = false, byte reason = Parties.NoReason) =>
        Call(c, new PartyAnswerInvitationRequest { wi = accept, wj = auto, wk = reason }).ReturnCode;

    private static List<Dictionary<byte, object?>> Of(Member c, EventCodes code, int mark) =>
        [.. Since(c.Peer, mark).Where(e => e.Code == code).Select(e => e.P)];

    private static Guid G(object? v) => new((byte[])v!);

    /// <summary>A invites, B accepts: the party of the two (or B into A's party).</summary>
    private static void Into(Member leader, Member joiner)
    {
        Check(Invite(leader, joiner) == 0 && Answer(joiner, true) == 0, $"{joiner.Player.Character.Name} joins {leader.Player.Character.Name}'s party");
    }

    private static void Invites()
    {
        using var town = new Town(maxSize: 3);
        var (a, b, c, d) = (town.Join("Anna", 5), town.Join("Boris", 10), town.Join("Carl", 300), town.Join("Dora", 310));
        var parties = town.World.Parties;

        // 1. A invites B by Guid: exactly {253, 255, 0 "Boris"} rc 0; B gets 228 {0 A, 1 "Anna"} without key 12; C nothing.
        var (bm, cm) = (b.Peer.Events.Count, c.Peer.Events.Count);
        var r = Call(a, new PartyInvitePlayerRequest { xp = b.Id });
        Check(r.ReturnCode == 0 && r.Parameters.Keys.Order().SequenceEqual(new byte[] { 0, 253, 255 }) && r.Parameters[0] is "Boris" && r.Parameters[255] is 7,
              $"A: {{253, 255, rc 0, 0 \"Boris\"}} (got {string.Join(",", r.Parameters.Keys)})");
        Check(Of(b, EventCodes.PartyInvitation, bm) is [var inv] && G(inv[0]) == a.Id && inv[1] is "Anna" && !inv.ContainsKey(12), "B: PartyInvitation {0 A, 1 Anna}, no key 12 (solo inviter)");
        Check(c.Peer.Events.Count == cm, "C: nothing");

        // 2. Errors.
        Check(Invite(a, null, "Nobody") == Parties.PlayerNotFound && Invite(a, a) == Parties.PlayerNotFound && Invite(a, null, "anna") == Parties.PlayerNotFound,
              "an unknown name and oneself (by Guid or by name, any case): 9000");
        Check(Invite(a, b) == Parties.PendingInvitation && Invite(c, b) == Parties.TargetWaiting, "a duplicate invite: 9009; another inviter while one is open: 9004");

        // 5. B declines (reason 0): 239 {0 "Boris", 1 false, 2 false, 3 0}; no party.
        var am = a.Peer.Events.Count;
        Check(Answer(b, false, reason: Parties.DeclinedViaUi) == 0 && Of(a, EventCodes.PartyInvitationAnswer, am) is [var no]
              && no[0] is "Boris" && !no.ContainsKey(1) && !no.ContainsKey(2) && !no.ContainsKey(3) && parties.Of(a.Id) is null && parties.InviteOf(b.Id) is null,
              "decline: A gets 239 {0 Boris, 1 false, 2 false, 3 0}, no party");
        // Expiry: 65 s later 505 to B and 239 {2 true, 3 1} to A; a late accept → 9080.
        Check(Invite(a, b) == 0, "invited again");
        (am, bm) = (a.Peer.Events.Count, b.Peer.Events.Count);
        town.Run(64.9);
        Check(Of(b, EventCodes.PartyInviteAborted, bm).Count == 0, "not before 65 s");
        town.Run(0.1);
        Check(Of(b, EventCodes.PartyInviteAborted, bm) is [var ab] && G(ab[0]) == a.Id && ab[1] is "Anna"
              && Of(a, EventCodes.PartyInvitationAnswer, am) is [var to] && to[0] is "Boris" && to[2] is true && Convert.ToInt32(to[3]) == Parties.TimedOut,
              "expiry: B gets 505 {0 A, 1 Anna}, A gets 239 {2 true, 3 1}");
        Check(Answer(b, true) == Parties.InvitationGone, "a late accept: 9080");

        // 11. An auto-decline with reason 4 (the client's f8k.as_2): 239 {Boris, false, true, 4} to C, rc 0; with nothing open rc 0, nothing else.
        Check(Invite(c, b) == 0, "C invites B");
        var cmark = c.Peer.Events.Count;
        var resp = Call(b, new PartyAnswerInvitationRequest { wi = false, wj = true, wk = Parties.Busy });
        Check(resp.ReturnCode == 0 && resp.Parameters[255] is 7 && Of(c, EventCodes.PartyInvitationAnswer, cmark) is [var busy]
              && busy[0] is "Boris" && !busy.ContainsKey(1) && busy[2] is true && Convert.ToInt32(busy[3]) == Parties.Busy && parties.InviteOf(b.Id) is null,
              "auto-decline 4: C gets 239 {0 Boris, 2 true, 3 4}, B rc 0 with 255, the invite is gone");
        (bm, cmark) = (b.Peer.Events.Count, c.Peer.Events.Count);
        Check(Answer(b, false, auto: true, reason: Parties.Busy) == 0 && b.Peer.Events.Count == bm && c.Peer.Events.Count == cmark, "the same with nothing open: rc 0, nothing else");

        // 3. B accepts: rc 0; one 230 each with the same id ≠ 0, k4 = A, keys 8-16 of length 2 in their wire types; A gets 239 {1 true}.
        Check(Invite(a, b) == 0, "invited a third time");
        (am, bm) = (a.Peer.Events.Count, b.Peer.Events.Count);
        Check(Answer(b, true) == 0, "accept: rc 0");
        Check(Of(a, EventCodes.PartyJoined, am) is [var ja] && Of(b, EventCodes.PartyJoined, bm) is [var jb], "one PartyJoined each");
        var j = Of(b, EventCodes.PartyJoined, bm)[0];
        var id = Convert.ToInt32(j[0]);
        Check(id != 0 && Convert.ToInt32(Of(a, EventCodes.PartyJoined, am)[0][0]) == id && G(j[4]) == a.Id && !j.ContainsKey(2) && !j.ContainsKey(1),
              "the same party id ≠ 0, key 4 the leader A, no key 2 (FreeLoot) and no key 1");
        Check(j[8] is byte[] { Length: 32 } && j[9] is string[] and ["Anna", "Boris"] && j[10] is byte[] { Length: 2 } && j[11] is short[] { Length: 2 } && j[12] is short[] { Length: 2 }
              && j[13] is byte[] { Length: 2 } && j[14] is int[] { Length: 2 } && j[15] is short[] { Length: 2 } && j[16] is bool[] and [true, true],
              $"keys 8-16: Guid[] as 32 bytes, string[], byte[], Int16[] ×2, packed byte[], Int32[], Int16[], bool[] {{true, true}} (got {string.Join(", ", j.Where(p => p.Key is >= 8 and <= 16).Select(p => p.Value?.GetType().Name))})");
        Check(Of(a, EventCodes.PartyInvitationAnswer, am) is [var yes] && yes[1] is true && yes[0] is "Boris", "A gets 239 {0 Boris, 1 true}");
        Check(Invite(b, c) == Parties.NotLeader && Invite(c, a) == Parties.TargetInParty, "a non-leader invite: 9007; a target in a party: 9002");

        // 4. C joins: A and B get 232 {0 id, 1 C, 2 Carl, 8 true}, C gets 230 with 3 members and no 232.
        (am, bm, cmark) = (a.Peer.Events.Count, b.Peer.Events.Count, c.Peer.Events.Count);
        Into(a, c);
        Check(new[] { a, b }.All(x => Of(x, EventCodes.PartyPlayerJoined, x == a ? am : bm) is [var pj] && Convert.ToInt32(pj[0]) == id && G(pj[1]) == c.Id && pj[2] is "Carl" && pj[8] is true),
              "A and B: 232 {0 id, 1 C, 2 Carl, 8 true}");
        Check(Of(c, EventCodes.PartyJoined, cmark) is [var jc] && jc[9] is string[] and ["Anna", "Boris", "Carl"] && !Of(c, EventCodes.PartyPlayerJoined, cmark).Any(),
              "C: 230 with the three, no 232");
        Check(Invite(a, d) == Parties.PartyFull, "a full party (Party:MaxSize 3): 9012");
    }

    private static void LeaveKick()
    {
        using var town = new Town();
        var (a, b, c) = (town.Join("Anna", 5), town.Join("Boris", 10), town.Join("Carl", 300));
        var parties = town.World.Parties;
        Into(a, b);
        Into(a, c);
        var id = parties.Of(a.Id)!.Id;

        // 6. B leaves: 234 {0 id, 1 B, 2 false, 3 false} to all three, nothing more to B.
        var marks = new[] { a, b, c }.Select(x => x.Peer.Events.Count).ToArray();
        Check(Call(b, new PartyLeaveRequest()).ReturnCode == 0, "B leaves: rc 0");
        foreach (var (x, i) in new[] { a, b, c }.Select((x, i) => (x, i)))
            Check(Of(x, EventCodes.PartyPlayerLeft, marks[i]) is [var left] && Convert.ToInt32(left[0]) == id && G(left[1]) == b.Id && !left.ContainsKey(2) && !left.ContainsKey(3),
                  $"{x.Player.Character.Name}: 234 {{0 id, 1 B}}");
        Check(b.Peer.Events.Count == marks[1] + 1 && parties.Of(b.Id) is null, "B: nothing more");
        // A leaves: A and C get 234, C 231 {1 id}; the party is gone and C's next party has a new id.
        marks = [a.Peer.Events.Count, 0, c.Peer.Events.Count];
        Check(Call(a, new PartyLeaveRequest()).ReturnCode == 0, "A leaves");
        Check(Of(a, EventCodes.PartyPlayerLeft, marks[0]).Count == 1 && Of(c, EventCodes.PartyPlayerLeft, marks[2]).Count == 1
              && Of(c, EventCodes.PartyDisbanded, marks[2]) is [var gone] && Convert.ToInt32(gone[1]) == id && !gone.ContainsKey(2) && !Of(a, EventCodes.PartyDisbanded, marks[0]).Any()
              && parties.Of(c.Id) is null, "A and C get 234, C gets 231 {1 id}, the party is gone");
        Check(Call(a, new PartyLeaveRequest()).ReturnCode == Parties.LeaveFailed, "leave outside a party: 9161");
        Into(c, a);
        Check(parties.Of(c.Id)!.Id != id, "C's new party has a new id");
        Call(c, new PartyLeaveRequest());

        // 7. Kick: a non-leader 9202; the leader kicks C: rc 0 {0 Carl}, 234 {3 true} to C too. MakeLeader B: 235 {0 id, 1 B}.
        Into(a, b);
        Into(a, c);
        id = parties.Of(a.Id)!.Id;
        Check(Call(b, new PartyKickPlayerRequest { xs = c.Id }).ReturnCode == Parties.KickNotLeader, "a non-leader kicks: 9202");
        var cm = c.Peer.Events.Count;
        var kick = Call(a, new PartyKickPlayerRequest { xt = "carl" });
        Check(kick.ReturnCode == 0 && kick.Parameters[0] is "Carl" && Of(c, EventCodes.PartyPlayerLeft, cm) is [var kicked] && G(kicked[1]) == c.Id && kicked[3] is true
              && parties.Of(c.Id) is null, "the leader kicks C by name: rc 0 {0 Carl}, C gets 234 {3 true}");
        var (am, bm) = (a.Peer.Events.Count, b.Peer.Events.Count);
        var lead = Call(a, new PartyMakeLeaderRequest { xu = b.Id });
        Check(lead.ReturnCode == 0 && lead.Parameters[0] is "Boris" && new[] { (a, am), (b, bm) }.All(x => Of(x.Item1, EventCodes.PartyLeaderChanged, x.Item2) is [var lc]
              && Convert.ToInt32(lc[0]) == id && G(lc[1]) == b.Id) && parties.Of(a.Id)!.Leader == b.Id, "MakeLeader B: rc 0 {0 Boris}, 235 {0 id, 1 B} to both");
        Check(Call(a, new PartyMakeLeaderRequest { xu = b.Id }).ReturnCode == Parties.LeaderNotLeader && Call(c, new PartyKickPlayerRequest { xs = a.Id }).ReturnCode == Parties.KickNoParty,
              "MakeLeader by a non-leader 9242; kick outside a party 9201");

        // 12. A reconnect (a second connection of B): B gets 230 again, A and C no 232; the old connection's end keeps B in the party.
        Into(b, c);
        (am, cm) = (a.Peer.Events.Count, c.Peer.Events.Count);
        var b2 = town.Again(b.Id);
        Check(Of(b2, EventCodes.PartyJoined, 0) is [var again] && again[9] is string[] { Length: 3 } && !Of(a, EventCodes.PartyPlayerJoined, am).Any() && !Of(c, EventCodes.PartyPlayerJoined, cm).Any(),
              "reconnect: B's new client gets 230 with the three, A and C no 232 for a member already present");
        b.Peer.Disconnect();
        Check(parties.Of(b.Id)?.Members.Count == 3, "the old connection's end: B stays (another connection plays it)");

        // A disconnect behaves like Leave: 234 to the others; with B leading still, A's leaving changes no leader.
        (bm, cm) = (b2.Peer.Events.Count, c.Peer.Events.Count);
        a.Peer.Disconnect();
        Check(Of(b2, EventCodes.PartyPlayerLeft, bm) is [var dl] && G(dl[1]) == a.Id && Of(c, EventCodes.PartyPlayerLeft, cm).Count == 1 && parties.Of(a.Id) is null
              && parties.Of(b.Id)?.Members.Count == 2, "A disconnects: B and C get 234 {1 A}, the party goes on with two");

        // B in the middle of a cluster change (off its map, a pending transfer): C leaves, the party is gone; B's arrival gets 231.
        id = parties.Of(b.Id)!.Id;
        lock (town.World.Sync) town.World.Maps.Remove(b2.Player, town.Clock.Now);
        b2.Player.Character.PendingTransfer = new PendingTransfer("0000", "0000", 10, 5, 0, 0, "test");
        town.World.Saves.SaveNow(b2.Player.Character); // as the commit does: the arrival Join loads it
        Check(Call(c, new PartyLeaveRequest()).ReturnCode == 0 && parties.Of(b.Id) is null, "C leaves while B is loading: the party is gone");
        var b3 = town.Again(b.Id);
        Check(Of(b3, EventCodes.PartyDisbanded, 0) is [var late] && Convert.ToInt32(late[1]) == id && !Of(b3, EventCodes.PartyJoined, 0).Any(),
              "B arrives: 231 {1 old id}, no 230");
    }

    private static void Chat()
    {
        using var town = new Town();
        var (a, b, o) = (town.Join("Anna", 5), town.Join("Boris", 5, cluster: "0001"), town.Join("Olga", 8));
        Check(a.Player.Observers.Contains(o.Player) && !a.Player.Observers.Contains(b.Player), "O watches A; B is on another map");
        Into(a, b);
        var (am, bm, om) = (a.Peer.Events.Count, b.Peer.Events.Count, o.Peer.Events.Count);
        a.Session.OnRequest(Request(new SayRequest { aa9 = "us", abb = true }, null), encrypted: false);
        Check(Of(a, EventCodes.ChatSay, am) is [var echo] && echo[9] is true && !echo.ContainsKey(7), "A: the echo {9 true}, no key 7");
        Check(Of(b, EventCodes.ChatSay, bm) is [var heard] && heard[0] is "Anna" && Convert.ToInt64(heard[4]) == a.Player.Id && G(heard[5]) == a.Id && heard[9] is true && heard[7] is true,
              "B on another map: ChatSay {0 Anna, 4 A's id, 5 A's Guid, 9 true, 7 true}");
        Check(!Of(o, EventCodes.ChatSay, om).Any(), "the observer O (no member): nothing");
        a.Session.OnRequest(Request(new SayRequest { aa9 = "all" }, null), encrypted: false);
        Check(Of(o, EventCodes.ChatSay, om) is [var said] && !said.ContainsKey(7) && !said.ContainsKey(9) && Of(b, EventCodes.ChatSay, bm).Count == 1, "a Say without key 2: to the observer as before, not to B");
    }

    private static void FameAndLoot()
    {
        using var arena = new Arena(mob => mob.Template.Name == "T4_MOB_ROAMING_UNDEAD_GHOUL" ? [("T4_ORE", 3)] : []);
        var world = arena.World;
        var data = Client.GameData!;
        Check(data.PveFameSharingRadius == 50 && data.GroupBonus(1) == 1 && data.GroupBonus(2) == 1.1 && data.GroupBonus(3) == 1.17 && data.GroupBonus(4) == 1.33
              && data.GroupBonus(20) == 1.4 && data.GroupBonus(25) == 1.4, "gamedata <Fame pvefamesharingradius 50>, GroupBonus 1.1 / 1.17 / 1.33 / 1.4 (and above 20)");
        static long T(float v) => (long)v;
        Check(Combat.Fame(94, 2.15, 0, 1, 1) == (2021000, 1081000) && Combat.Fame(66, 2.15, 3.2, 1) == (443437, 237187)
              && Combat.Fame(66, 2.15, 3.2, 2, 1.1) == (T(66f * 2.15f * 1.1f / 3.2f * 10000f), T(66f * (2.15f - 1f) * 1.1f / 3.2f * 10000f))
              && Combat.Fame(66, 2.15, 1.3, 2, 1.1).Gain == T(66f * 2.15f * 1.1f / 2f * 10000f),
              "fame: one member as stage 5; two on a mindivisor-3.2 mob: d = 3.2, g = 1.1; two on a 1.3 one: d = 2");

        var (sa, pa, a) = arena.Join("Alpha", new InventoryItem("T4_MAIN_SWORD"));
        var (sb, pb, b) = arena.Join("Beta", new InventoryItem("T4_MAIN_SWORD"));
        var (sc, pc, c) = arena.Join("Gamma", new InventoryItem("T4_MAIN_SWORD"));
        var (ca, cb) = (new Member(sa, pa, a), new Member(sb, pb, b));
        Into(ca, cb);
        var party = world.Parties.Of(a.Character.Id)!.Id;

        (Dictionary<byte, object?>? A, Dictionary<byte, object?>? B, LootBag? Bag) Kill(float bAt)
        {
            var ghoul = arena.Spawn("T4_MOB_ROAMING_UNDEAD_GHOUL", new Vector2(1.5f, 0));
            lock (world.Sync)
            {
                b.Teleport(ghoul.Position + new Vector2(bAt, 0), 0, DateTime.UtcNow);
                ghoul.Health.Set(1, Math.Max(world.GameNow, ghoul.Health.Stamp));
            }
            var (ma, mb) = (pa.Events.Count, pb.Events.Count);
            Send(sa, new AttackStartRequest { wf = world.GameNow, co = ghoul.Id, cp = 0 });
            arena.Run(1.5);
            Check(ghoul.Dead, "the ghoul dies");
            var bag = world.Maps.Find("0204")!.Objects.Values.OfType<LootBag>().LastOrDefault(x => x.Mob == ghoul);
            return (Since(pa, ma).Where(e => e.Code == EventCodes.UpdateFame).Select(e => e.P).SingleOrDefault(),
                    Since(pb, mb).Where(e => e.Code == EventCodes.UpdateFame).Select(e => e.P).SingleOrDefault(), bag);
        }

        // 9. B 10 m away: both get fame × 2.15 × 1.1 / 2 (float, truncated).
        var (fa, fb, bag) = Kill(10);
        long gain = T(94f * 2.15f * 1.1f / 2f * 10000f), bonus = T(94f * (2.15f - 1f) * 1.1f / 2f * 10000f);
        Check(fa is not null && fb is not null && L(fa, 2) == gain && L(fb, 2) == gain && L(fa, 3) == bonus && L(fb, 3) == bonus,
              $"B 10 m away: A and B get UpdateFame k2 {gain}, k3 {bonus} (got {(fa is null ? "-" : L(fa, 2))} / {(fb is null ? "-" : L(fb, 2))})");

        // 10. The bag: k10 [A], k11 [party id]; B opens it, C (no party) is refused.
        var newLoot = pa.Events.Select(e => (Code: (EventCodes)CombatTest.Code(e), E: e)).Last(e => e.Code == EventCodes.NewLoot).E;
        var nl = Wire(newLoot);
        Check(bag is not null && nl[10] is byte[] looters && new Guid(looters) == a.Character.Id && nl[11] is int[] ids && ids.SequenceEqual([party]),
              "NewLoot of A's kill: k10 [A], k11 [party id] (Int32[])");
        lock (world.Sync)
        {
            b.Teleport(bag!.Position, 0, DateTime.UtcNow);
            c.Teleport(bag.Position, 0, DateTime.UtcNow);
        }
        var (mb2, mc) = (pb.Events.Count, pc.Events.Count);
        Send(sb, new RegisterToObjectRequest { zg = bag.Id });
        Send(sc, new RegisterToObjectRequest { zg = bag.Id });
        Check(Since(pb, mb2).Any(e => e.Code == EventCodes.AttachItemContainer) && b.OpenBag == bag, "B (A's party) opens the bag: the items and AttachItemContainer");
        Check(!Since(pc, mc).Any(e => e.Code == EventCodes.AttachItemContainer) && c.OpenBag is null, "C (no party) is refused");

        // B 60 m away: A alone gets the solo numbers, B nothing.
        (fa, fb, _) = Kill(60);
        Check(fa is not null && L(fa, 2) == 2021000 && L(fa, 3) == 1081000 && fb is null, "B 60 m away: A alone gets 2021000 / 1081000, B no UpdateFame");

        // Without a party: no key 11.
        Call(cb, new PartyLeaveRequest());
        (_, _, bag) = Kill(60);
        var solo = Wire(pa.Events.Last(e => (EventCodes)CombatTest.Code(e) == EventCodes.NewLoot));
        Check(bag is not null && !solo.ContainsKey(11), "without a party: NewLoot has no key 11");
    }

    /// <summary>
    /// The party role (op 234) and its wire value: the client paints a member's minimap dot with the colour of
    /// <c>itemroles.xml</c> at the index it gets, so the default must be -1 (none) and not 0 — index 0 is expedition TANK,
    /// which has no <c>mapmarkercolor</c> and renders a fully transparent dot (MapLabel.GetMapMarkerColorForPlayer).
    /// </summary>
    private static void Roles()
    {
        using var town = new Town();
        var (a, b) = (town.Join("Anna", 5), town.Join("Boris", 10));
        var parties = town.World.Parties;

        // 1. A fresh party: key 15 (diu, Int16[]) is [-1, -1] for both, never [0, 0].
        var (am, bm) = (a.Peer.Events.Count, b.Peer.Events.Count);
        Into(a, b);
        var ja = Of(a, EventCodes.PartyJoined, am).Single();
        var jb = Of(b, EventCodes.PartyJoined, bm).Single();
        Check(ja[15] is short[] { Length: 2 } ra && ra.SequenceEqual(new short[] { -1, -1 })
              && jb[15] is short[] { Length: 2 } rb && rb.SequenceEqual(new short[] { -1, -1 }),
              $"PartyJoined key 15 = [-1, -1] (no role) (got [{string.Join(",", ja[15] as short[] ?? [])}] / [{string.Join(",", jb[15] as short[] ?? [])}])");

        // 2. B takes PARTY_HEAL (index 8 in itemroles.xml): 243 {0 8, 1 B} to both, no response, the member stores it.
        (am, bm) = (a.Peer.Events.Count, b.Peer.Events.Count);
        Send(b.Session, new PartySetRoleRequest { x1 = 8 });
        Check(Of(a, EventCodes.PartySetRoleFlag, am) is [var fa] && Convert.ToInt32(fa[0]) == 8 && G(fa[1]) == b.Id
              && Of(b, EventCodes.PartySetRoleFlag, bm) is [var fb] && Convert.ToInt32(fb[0]) == 8 && G(fb[1]) == b.Id,
              "op 234 (role 8): PartySetRoleFlag {0 8, 1 B} to both members");
        Check(parties.Of(b.Id)!.Find(b.Id)!.Role == 8, "the member stores role 8");

        // 3. The role is re-sent with the next PartyJoined (a relog or a cluster change), in B's slot.
        var bm2 = b.Peer.Events.Count;
        parties.Joined(b.Player, clusterChange: false);
        var resent = Of(b, EventCodes.PartyJoined, bm2).Single()[15] as short[];
        Check(resent is { Length: 2 } && resent.SequenceEqual(new short[] { -1, 8 }),
              $"PartyJoined after the role: [-1, 8] (got [{string.Join(",", resent ?? [])}])");

        // 4. A value below -1 is not an index: ignored, nothing relayed, the stored role unchanged.
        var (am2, bm3) = (a.Peer.Events.Count, b.Peer.Events.Count);
        Send(b.Session, new PartySetRoleRequest { x1 = -2 });
        Check(a.Peer.Events.Count == am2 && b.Peer.Events.Count == bm3 && parties.Of(b.Id)!.Find(b.Id)!.Role == 8,
              "role -2: no PartySetRoleFlag, the role stays 8");

        // 5. The other member takes a role too: 243 to both, including the one that set it.
        var am3 = a.Peer.Events.Count;
        Send(a.Session, new PartySetRoleRequest { x1 = 6 });
        Check(parties.Of(a.Id)!.Find(a.Id)!.Role == 6 && Of(a, EventCodes.PartySetRoleFlag, am3) is [var own] && Convert.ToInt32(own[0]) == 6,
              "role 6 from A: stored and relayed to A itself as well");
    }
}
