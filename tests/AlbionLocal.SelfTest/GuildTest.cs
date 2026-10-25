using System.Buffers.Binary;
using AlbionLocal.Photon.Serialization;
using AlbionLocal.Server;
using AlbionLocal.Server.Content;
using AlbionLocal.Server.Data;
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
/// Guilds (analysis/work/stageH/guilds/SPEC.md §6, PVE-DESIGN step 13 part 1): two to four players in one World through
/// <see cref="FakePeer"/>s: create and its errors, invite / decline / expiry / accept, ranks by tag, the client's kick rule, the
/// GM hand-over, leave and disband, login and logout, a restart, and the guild in Pvp.Of.
/// </summary>
public static class GuildTest
{
    public static IEnumerable<(string, Action)> All =>
    [
        ("guild: create (103 kind 0, 349 with the GM tag, rc 0 without 255, 106 to an observer, silver), errors 4200/4201/4204/4205", Create),
        ("guild: invite errors 4240/4242/4243/4244/4245/4247, decline (510), expiry, accept (103/349/104 kind 1), NewCharacter 8/9/44", Invite),
        ("guild: ranks by tag (104 kind 4, 349), kick rule, GM hand-over by tag, leave with successor, disband, Pvp.Of G", Ranks),
        ("guild: login (Join 56/58/71, 104/103/349), offline 104, restart keeps guild and roles", LoginAndRestart),
        ("guild pve: presets, levels and perks (fame, silver, gathering, repair), xp from fame, no friendly fire", Perks),
        ("guild pve: a shared kill without a party, the silver tax into the account (103 kind 2), payout, contracts, saved", SharedHunt),
    ];

    private sealed record Member(WorldSession Session, FakePeer Peer, Player Player)
    {
        public Guid Id => Player.Character.Id;
    }

    private sealed class Town : IDisposable
    {
        public readonly string Dir = NewDir();
        public readonly SqliteCharacterRepository Characters;
        public readonly ManualClock Clock = new();
        public readonly ServerConfig Config;
        public World World;

        public Town(ClusterPvp? pvp = null, ServerConfig? config = null)
        {
            Config = config ?? new() { JoinByCharacterId = true, GuildCreateCost = 1000, GuildRejoinCooldownMinutes = 0 };
            Characters = Repository(Dir);
            World = new World(Config, Characters, Items()) { Sync = new object(), Clock = Clock, PvpOverride = pvp };
        }

        public Character New(string name, float x, long silver = 0)
        {
            var c = StarterKit.Default.NewCharacter(name);
            (c.Cluster, c.PositionX, c.PositionY, c.Silver) = ("0000", x, 5, silver * 10000);
            c.Equipment = [];
            c.Inventory = [];
            Check(Characters.Create(c), $"character {name} created");
            return c;
        }

        public Member Join(string name, float x, long silver = 0) => Again(New(name, x, silver).Id);

        public Member Again(Guid id)
        {
            var peer = new FakePeer();
            var session = new WorldSession(peer, World);
            peer.Disconnected = session.OnDisconnected;
            lock (World.Sync) session.OnRequest(Request(new JoinRequest { ss = id }, 1), encrypted: false);
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

    /// <summary>Sends <paramref name="req"/> (request id <paramref name="id"/>) and returns the response it got, null = none.</summary>
    private static OperationResponse? Send(Member c, AlbionRequest req, int? id = null)
    {
        var n = c.Peer.Responses.Count;
        c.Session.OnRequest(Request(req, id), encrypted: false);
        return c.Peer.Responses.Count > n ? c.Peer.Responses[^1] : null;
    }

    private static List<Dictionary<byte, object?>> Of(Member c, EventCodes code, int mark) =>
        [.. Since(c.Peer, mark).Where(e => e.Code == code).Select(e => e.P)];

    private static Guid G(object? v) => new((byte[])v!);

    private static Guid[] Gs(object? v) => [.. ((byte[])v!).Chunk(16).Select(b => new Guid(b))];

    /// <summary>A packed byte[][] (rank 2: [i32 n][n × i32 len] + bytes).</summary>
    private static byte[][] Rows(object? v)
    {
        var b = (byte[])v!;
        var n = BinaryPrimitives.ReadInt32LittleEndian(b);
        var lens = Enumerable.Range(0, n).Select(i => BinaryPrimitives.ReadInt32LittleEndian(b.AsSpan(4 + 4 * i))).ToArray();
        var pos = 4 + 4 * n;
        return [.. lens.Select(l => { var row = b[pos..(pos + l)]; pos += l; return row; })];
    }

    private static CreateGuildRequest CreateReq(string name) =>
        new() { ip = name, iq = 3, ir = 86, _is = 15, it = 11, iu = 4416045, iv = 1.4f, ix = "BANNER_X" };

    private static Guild Found(Town town, Member founder, string name)
    {
        Check(Send(founder, CreateReq(name))?.ReturnCode == 0, $"{founder.Player.Character.Name} founds '{name}'");
        return town.World.Guilds.Of(founder.Id)!;
    }

    private static void Into(Member inviter, Member joiner)
    {
        Check(Send(inviter, new InviteToGuildRequest { sq = joiner.Player.Character.Name })?.ReturnCode == 0
              && Send(joiner, new JoinGuildRequest(), 7)?.ReturnCode == 0, $"{joiner.Player.Character.Name} joins");
    }

    private static void Create()
    {
        using var town = new Town();
        var (a, b) = (town.Join("Anna", 5, silver: 5000), town.Join("Boris", 10));

        // 1. A founds "Iron Wolves": GuildUpdate kind 0, then 349, then rc 0 without 255, then 106; B gets 106 too.
        var (am, bm, seq) = (a.Peer.Events.Count, b.Peer.Events.Count, a.Peer.Sequence.Count);
        var r = Send(a, CreateReq("Iron Wolves"));
        Check(r is { ReturnCode: 0 } && !r.Parameters.ContainsKey(255) && !r.Parameters.ContainsKey(0), "rc 0 without 255 and keys");
        var g = town.World.Guilds.Of(a.Id)!;
        var order = a.Peer.Sequence.Skip(seq).Select(o => o is EventData e ? ((EventCodes)Code(e)).ToString() : "response").Where(n => n.StartsWith("Guild") || n == "response").ToList();
        Check(order.SequenceEqual(["GuildUpdate", "GuildFullAccessTagsUpdated", "response", "GuildMemberWorldUpdate"]), $"order 103, 349, rc 0, 106 (got {string.Join(", ", order)})");
        var u = Of(a, EventCodes.GuildUpdate, am)[0];
        Check(G(u[0]) == g.Id && Gs(u[1]).SequenceEqual([a.Id]) && u[2] is string[] and ["Anna"] && u[3] is bool[] and [true] && u[4] is long[] { Length: 1 }
              && u[5] is byte[] and [8] && u[6] is int[] { Length: 1 } && u[7] is int[] { Length: 1 } && u[8] is int[] { Length: 1 } && u[9] is byte[] { Length: 1 }
              && !u.ContainsKey(10) && u[15] is "Iron Wolves" && u[16] is "" && u[18] is true && u[19] is System.Collections.IDictionary { Count: 0 }
              && Convert.ToInt32(u[20]) == 3 && Convert.ToInt32(u[21]) == 86 && Convert.ToInt32(u[22]) == 15 && Convert.ToInt32(u[23]) == 11
              && Convert.ToInt32(u[24]) == 4416045 && u[25] is 1.4f && u[28] is "" && u[36] is "",
              "103: kind 0, key 1 = [A], role 8, keys 1-9 of one length, 15 name, 16 \"\", 18 true, 19 {}, 20-25 the crest");
        var t = Of(a, EventCodes.GuildFullAccessTagsUpdated, am)[0];
        var ids = Gs(t[1]);
        var gm = Array.IndexOf((string[])t[2]!, "@GUILDROLE_GUILDMASTER");
        Check(ids.Length == 8 && ids.Distinct().Count() == 8 && G(t[0]) == ids[gm] && G(t[0]) != g.Id && ((long[])t[5]!)[gm] == 0xFFFFFFFFF
              && Gs(t[10]).SequenceEqual([a.Id]) && Rows(t[11]) is [[var held]] && held == gm && Rows(t[8]).Length == 8 && Rows(t[9]).Length == 8,
              "349: 8 tags, key 0 = the GM tag ≠ the guild id, its mask all 36 bits, key 10 = [A], key 11 = [[GM index]]");
        Check(Of(b, EventCodes.GuildMemberWorldUpdate, bm) is [var w] && G(w[0]) == g.Id && Convert.ToInt64(w[2]) == a.Player.Id && w[3] is "Iron Wolves",
              "B: 106 {0 guild, 2 A's object, 3 name}");
        Check(a.Player.Character.Silver == 4000 * 10000L, "Guilds:CreateCost taken");

        // 2. Errors.
        b.Player.Character.Silver = 5000 * 10000L;
        Check(Send(b, CreateReq("iron wolves"))?.ReturnCode == Guilds.NameTaken && Send(a, CreateReq("Other"))?.ReturnCode == Guilds.AlreadyInGuild
              && Send(b, CreateReq("a"))?.ReturnCode == Guilds.NameNotAllowed, "the name in another case 4200, A again 4205, \"a\" 4201");
        b.Player.Character.Silver = 10;
        Check(Send(b, CreateReq("Other"))?.ReturnCode == Guilds.NotEnoughSilver && b.Player.Character.Silver == 10, "too poor: 4204, silver unchanged");
    }

    private static void Invite()
    {
        using var town = new Town();
        var (a, b, c) = (town.Join("Anna", 5, silver: 5000), town.Join("Boris", 10), town.Join("Carl", 15));
        town.New("Dora", 20); // exists, not in game
        var g = Found(town, a, "Wolves");
        short Inv(Member from, string name) => Send(from, new InviteToGuildRequest { sq = name })!.ReturnCode;

        // 3. Errors, then A invites "boris" (any case): rc 0 {0 "Boris"}, B gets 105 {1 Anna, 2 Wolves}; again 4245.
        Check(Inv(a, "Nobody") == Guilds.PlayerDoesNotExist && Inv(a, "dora") == Guilds.PlayerNotOnline && Inv(a, "anna") == Guilds.InviteSelf,
              "unknown 4240, offline 4242, oneself 4247");
        var bm = b.Peer.Events.Count;
        var r = Send(a, new InviteToGuildRequest { sq = "boris" })!;
        Check(r.ReturnCode == 0 && r.Parameters[0] is "Boris" && !r.Parameters.ContainsKey(255), "rc 0 {0 Boris}");
        Check(Of(b, EventCodes.InvitedToGuild, bm) is [var inv] && G(inv[0]) == g.Id && inv[1] is "Anna" && inv[2] is "Wolves", "B: 105 {0 guild, 1 Anna, 2 Wolves}");
        Check(Inv(a, "Boris") == Guilds.PlayerInvited, "invited already: 4245");

        // 5. Decline {0: 3}: A gets 510 {0 Boris, 1 3}; a JoinGuild then: rc 1. 6. Expiry: rc 1 after Guilds:InviteSeconds.
        var am = a.Peer.Events.Count;
        Check(Send(b, new DeclineGuildInvitationRequest { jf = 3 }) is null && Of(a, EventCodes.GuildInviteDeclined, am) is [var no] && no[0] is "Boris"
              && Convert.ToInt32(no[1]) == 3, "decline: no response, A gets 510 {0 Boris, 1 3}");
        Check(Send(b, new JoinGuildRequest(), 7)?.ReturnCode == Guilds.Refused, "JoinGuild without an invitation: rc 1");
        Check(Inv(a, "Boris") == 0, "invited again");
        town.Run(town.Config.GuildInviteSeconds + 1);
        Check(Send(b, new JoinGuildRequest(), 7)?.ReturnCode == Guilds.Refused, "expired: rc 1");

        // 4. Accept: rc 0 {255 7, 0 guild, 1 name}; B: 103 with 2 members + 349; A: 104 kind 1 {B, role 1, online} + 349; A sees B's 106.
        Check(Inv(a, "Boris") == 0, "invited a third time");
        (am, bm) = (a.Peer.Events.Count, b.Peer.Events.Count);
        r = Send(b, new JoinGuildRequest(), 7)!;
        Check(r.ReturnCode == 0 && r.Parameters[255] is 7 && G(r.Parameters[0]) == g.Id && r.Parameters[1] is "Wolves", "accept: rc 0 {255 7, 0 guild, 1 Wolves}");
        Check(Of(b, EventCodes.GuildUpdate, bm) is [var u] && Gs(u[1]).Length == 2 && Of(b, EventCodes.GuildFullAccessTagsUpdated, bm).Count == 1,
              "B: 103 kind 0 with both members, 349");
        Check(Of(a, EventCodes.GuildPlayerUpdated, am) is [var add] && G(add[0]) == b.Id && Convert.ToInt32(add[4]) == (int)GuildRole.Recruit && add[2] is true
              && Convert.ToInt32(add[9]) == Guilds.KindAdd && Of(a, EventCodes.GuildFullAccessTagsUpdated, am) is [var t] && Rows(t[11])[1] is [0xFF],
              "A: 104 kind 1 {B, RECRUIT, online}, 349 with B holding no tag");
        Check(Of(a, EventCodes.GuildMemberWorldUpdate, am) is [var w] && Convert.ToInt64(w[2]) == b.Player.Id, "A sees B's 106");
        Check(Inv(b, "Carl") == Guilds.NoInviteRight && Inv(a, "Boris") == Guilds.PlayerInGuild, "a RECRUIT's invite 4243; a member 4244");

        // A new observer gets B's guild in its NewCharacter.
        var e = town.Join("Emil", 12);
        Check(Since(e.Peer, 0).FirstOrDefault(x => x.Code == EventCodes.NewCharacter && Convert.ToInt64(x.P[0]) == b.Player.Id).P is { } nc
              && nc[8] is "Wolves" && G(nc[9]) == g.Id && Convert.ToInt32(nc[44]) == 3, "E: NewCharacter(B) with 8 Wolves, 9 the guild, 44 the schema");
    }

    private static void Ranks()
    {
        using var town = new Town(new ClusterPvp(PvpRule.PvpForced));
        var (a, b, c, d) = (town.Join("Anna", 5, silver: 5000), town.Join("Boris", 10), town.Join("Carl", 15), town.Join("Dora", 20));
        var g = Found(town, a, "Wolves");
        Into(a, b);
        Into(a, c);
        Into(a, d);
        Guid Tag(GuildRole role) => g.Tags[GuildRoles.TagIndex(role)];
        GuildRole Role(Member m) => g.Find(m.Id)!.Role;

        // 7. A makes B an OFFICER by tag: 104 kind 4 {4 = 4} to A and B, then 349 with B's row = the OFFICER index.
        var (am, bm) = (a.Peer.Events.Count, b.Peer.Events.Count);
        Check(Send(a, new AssignGuildAccessTagRequest { cj = "boris", ck = Tag(GuildRole.Officer) }) is null && Role(b) == GuildRole.Officer, "B is OFFICER, no response");
        Check(new[] { (a, am), (b, bm) }.All(x => Of(x.Item1, EventCodes.GuildPlayerUpdated, x.Item2) is [var k] && Convert.ToInt32(k[9]) == Guilds.KindRole
              && Convert.ToInt32(k[4]) == 4 && Of(x.Item1, EventCodes.GuildFullAccessTagsUpdated, x.Item2) is [var t]
              && Rows(t[11])[Array.IndexOf(Gs(t[10]), b.Id)] is [var held] && held == GuildRoles.TagIndex(GuildRole.Officer)),
              "A and B: 104 kind 4 {4 = 4}, then 349 with B → OFFICER");
        Check(Send(b, new KickFromGuildRequest { s1 = "", s2 = c.Id })?.ReturnCode == Guilds.Refused, "an OFFICER (no KICK) kicking C: rc 1");

        // 8. The superset rule: C as THE_RIGHT_HAND kicks the tagless RECRUIT D, not the OFFICER B; B cannot kick the GM.
        Check(Send(a, new AssignGuildAccessTagRequest { cj = "Carl", ck = Tag(GuildRole.TheRightHand) }) is null && Role(c) == GuildRole.TheRightHand, "C is THE_RIGHT_HAND");
        var (dm, om) = (d.Peer.Events.Count, a.Peer.Events.Count);
        Check(Send(c, new KickFromGuildRequest { s1 = "", s2 = d.Id })?.ReturnCode == 0 && town.World.Guilds.Of(d.Id) is null, "C kicks D: rc 0");
        Check(Of(d, EventCodes.GuildPlayerUpdated, dm) is [var self] && G(self[0]) == d.Id && Convert.ToInt32(self[9]) == Guilds.KindLeft
              && Of(a, EventCodes.GuildPlayerUpdated, om).Any(k => G(k[0]) == d.Id && Convert.ToInt32(k[9]) == Guilds.KindKicked),
              "D: 104 kind 3 with its own Guid; A: kind 2");
        Check(Of(a, EventCodes.GuildMemberWorldUpdate, om).Any(w => Convert.ToInt64(w[2]) == d.Player.Id && !w.ContainsKey(0) && w[3] is ""),
              "D's observers: 106 with no guild and key 3 \"\"");
        Check(Send(c, new KickFromGuildRequest { s1 = "", s2 = b.Id })?.ReturnCode == Guilds.Refused
              && Send(b, new KickFromGuildRequest { s1 = "", s2 = a.Id })?.ReturnCode == Guilds.Refused, "C kicking the OFFICER B: rc 1; B kicking the GM: rc 1");

        // 12. Pvp.Of: guild mates Friendly in a black zone (and Neutral by the table in a red one), the outsider D Hostile.
        Check(Pvp.Of(a.Player, b.Player) == Relation.Friendly && Pvp.Of(a.Player, d.Player) == Relation.Hostile
              && Pvp.Of(Pvp.Blue, Pvp.Red, false, true, new ClusterPvp(PvpRule.PvpAllowed)) == Relation.Neutral
              && Pvp.Of(Pvp.Blue, Pvp.Red, false, false, new ClusterPvp(PvpRule.PvpAllowed)) == Relation.Hostile,
              "G: Friendly in black, a Red guild mate Neutral in red (Hostile outside the guild); D Hostile");

        // 13. The GM tag hands the guild over: B GUILDMASTER, A THE_RIGHT_HAND.
        Check(Send(a, new AssignGuildAccessTagRequest { cj = "Boris", ck = g.MasterTag }) is null && Role(b) == GuildRole.Guildmaster && Role(a) == GuildRole.TheRightHand,
              "hand-over: B GUILDMASTER, A THE_RIGHT_HAND");

        // 9. C leaves: rc 0, A gets kind 3 {C}. The GM B leaves with successor A: A GUILDMASTER, B out. A leaves last: the guild is gone.
        am = a.Peer.Events.Count;
        Check(Send(c, new LeaveGuildRequest())?.ReturnCode == 0 && Of(a, EventCodes.GuildPlayerUpdated, am).Any(k => G(k[0]) == c.Id && Convert.ToInt32(k[9]) == Guilds.KindLeft),
              "C leaves: rc 0, A gets 104 kind 3 {C}");
        Check(Send(b, new LeaveGuildRequest())?.ReturnCode == Guilds.Refused, "the GM without a successor: rc 1");
        Check(Send(b, new LeaveGuildRequest { tg = a.Id })?.ReturnCode == 0 && Role(a) == GuildRole.Guildmaster && g.Find(b.Id) is null, "B leaves, A leads");
        Check(Send(a, new LeaveGuildRequest())?.ReturnCode == 0 && town.World.Guilds.Find("Wolves") is null && town.Characters.LoadGuilds().Count == 0,
              "the last member out: the guild is deleted");
        Check(Send(c, CreateReq("wolves")) is { } again && again.ReturnCode != Guilds.NameTaken, "its name is free again");
    }

    private static void LoginAndRestart()
    {
        using var town = new Town();
        var (a, b) = (town.Join("Anna", 5, silver: 5000), town.Join("Boris", 10));
        var g = Found(town, a, "Wolves");
        Into(a, b);
        Check(Send(a, new AssignGuildAccessTagRequest { cj = "Boris", ck = g.Tags[GuildRoles.TagIndex(GuildRole.Officer)] }) is null, "B OFFICER");

        // B disconnects: A gets 104 kind 0 {B, online false}.
        var am = a.Peer.Events.Count;
        lock (town.World.Sync) b.Peer.Disconnect();
        Check(Of(a, EventCodes.GuildPlayerUpdated, am) is [var off] && G(off[0]) == b.Id && !off.ContainsKey(2) && !off.ContainsKey(9), "A: 104 kind 0 {B, offline}");

        // 10. B logs in again: Join 56/58/71, then after JoinFinished 104 kind 0 (self), 103 kind 0, 349; A gets 104 kind 0 {B, online}.
        am = a.Peer.Events.Count;
        var b2 = town.Again(b.Id);
        var join = b2.Peer.Responses[0].Parameters;
        Check(G(join[56]) == g.Id && join[58] is "Wolves" && Convert.ToInt32(join[71]) == 3, "Join: 56 guild, 58 Wolves, 71 the schema");
        var after = b2.Peer.Sequence.SkipWhile(o => o is not EventData e || Code(e) != (int)EventCodes.JoinFinished).OfType<EventData>()
            .Select(e => (EventCodes)Code(e)).Where(c => c.ToString().StartsWith("Guild")).ToList();
        Check(after.SequenceEqual([EventCodes.GuildPlayerUpdated, EventCodes.GuildUpdate, EventCodes.GuildFullAccessTagsUpdated]),
              $"after JoinFinished: 104, 103, 349 (got {string.Join(", ", after)})");
        Check(Of(a, EventCodes.GuildPlayerUpdated, am) is [var on] && G(on[0]) == b.Id && on[2] is true, "A: 104 kind 0 {B, online}");

        // §8: .g reaches every member in game as a System line.
        am = a.Peer.Events.Count;
        lock (town.World.Sync) b2.Session.OnRequest(Request(new SayRequest { aa9 = ".g hello all" }, null), encrypted: false);
        Check(Of(a, EventCodes.SystemMessage, am) is [var line] && line[0] is "[Guild] Boris: hello all", ".g: A gets \"[Guild] Boris: hello all\"");

        // 11. A restart (a new World on the database): the guild, its members, roles and crest; no open invitation.
        town.World.Dispose();
        town.World = new World(town.Config, town.Characters, Items()) { Sync = new object(), Clock = town.Clock };
        var back = town.World.Guilds.Find("wolves");
        Check(back is { Members.Count: 2 } && back.Id == g.Id && back.Find(a.Id)!.Role == GuildRole.Guildmaster && back.Find(b.Id)!.Role == GuildRole.Officer
              && back.Logo == g.Logo && back.Tags.SequenceEqual(g.Tags) && back.Banner == "BANNER_X", "restart: guild, roles, crest, tags and banner kept");
    }

    private static void Perks()
    {
        // The presets: the pve one turns the PvE guild on, the official one keeps it off; Guilds:Levels is checked.
        var pve = LoadTestConfig("no-such-config.json", ["--Preset=pve"]);
        var official = LoadTestConfig("no-such-config.json", ["--Preset=official"]);
        Check(pve is { GuildNoFriendlyFire: true, GuildXpShare: 0.1, GuildShareWithoutParty: true, GuildSilverTax: 0.05, GuildContracts: true, GuildClearXp: 2000 }
              && official is { GuildNoFriendlyFire: false, GuildXpShare: 0, GuildShareWithoutParty: false, GuildSilverTax: 0, GuildContracts: false, GuildClearXp: 0 }
              && LoadTestConfig("no-such-config.json", ["--Guilds:Levels=0,5,9"]).GuildLevels.SequenceEqual([0L, 5, 9]),
              "pve preset: the PvE guild on; official: off; Guilds:Levels as a comma list");
        var refused = false;
        try { LoadTestConfig("no-such-config.json", ["--Guilds:Levels=5,1"]); }
        catch (InvalidDataException) { refused = true; }
        Check(refused, "Guilds:Levels 5,1 refused");

        using var town = new Town(new ClusterPvp(PvpRule.PvpAllowed), new ServerConfig
        {
            JoinByCharacterId = true, GuildCreateCost = 0, GuildRejoinCooldownMinutes = 0, GuildXpShare = 0.1, GuildLevels = [0, 100, 200], GuildNoFriendlyFire = true,
        });
        var (a, b) = (town.Join("Anna", 5), town.Join("Boris", 10));
        var g = Found(town, a, "Wolves");
        Into(a, b);
        var guilds = town.World.Guilds;
        Check(guilds.Level(g) == 1 && guilds.FameBonus(a.Player) == 0 && guilds.RepairPrice(a.Player, 100_000) == 100_000, "level 1: no perks");

        // 200 xp: level 3 (two steps), told to the members; the perks are Rates-like factors.
        var bm = b.Peer.Events.Count;
        lock (town.World.Sync) guilds.AddXp(g, 200);
        Check(guilds.Level(g) == 3 && Of(b, EventCodes.SystemMessage, bm) is [var up] && up[0] is string text && text.Contains("guild level 3"),
              "200 xp: level 3, a system line to the members");
        Check(Math.Abs(guilds.FameBonus(a.Player) - 0.04) < 1e-9 && Math.Abs(guilds.SilverBonus(a.Player) - 0.04) < 1e-9
              && Math.Abs(guilds.GatheringBonus(a.Player) - 0.02) < 1e-9 && guilds.RepairPrice(a.Player, 100_000) == 90_000,
              "level 3: +4 % fame and mob silver, +2 % gathering, -10 % repair");

        // A PvE gain of 100 fame: 104 with the perk, 10.4 of it to the guild (Guilds:XpShare 0.1); a GM grant takes neither.
        var fame = a.Player.Character.Fame;
        lock (town.World.Sync) Destiny.Award(a.Player, 1_000_000, Destiny.Kill(a.Player, 4));
        Check(a.Player.Character.Fame == fame + 1_040_000 && g.Xp == 210, $"PvE fame 100 -> 104, guild xp 200 -> 210 (got {a.Player.Character.Fame - fame}, {g.Xp})");
        lock (town.World.Sync) Destiny.Award(a.Player, 1_000_000, Destiny.Kill(a.Player, 4), journals: false, guild: false);
        Check(a.Player.Character.Fame == fame + 2_040_000 && g.Xp == 210, "a GM grant: no perk, no guild xp");

        // §10.2: two factions in one guild are Hostile by the table in a red zone; Guilds:NoFriendlyFire makes them Neutral.
        (a.Player.Character.Flag, b.Player.Character.Flag) = (1, 2);
        Check(Pvp.Of(1, 2, false, true, new ClusterPvp(PvpRule.PvpAllowed)) == Relation.Hostile && Pvp.Of(a.Player, b.Player) == Relation.Neutral,
              "factions in one guild: Hostile by the table, Neutral with Guilds:NoFriendlyFire");
    }

    private static void SharedHunt()
    {
        using var arena = new Arena(config: new ServerConfig
        {
            JoinByCharacterId = true, GuildCreateCost = 0, GuildRejoinCooldownMinutes = 0, GuildShareWithoutParty = true, GuildSilverTax = 0.05,
            GuildXpShare = 0.1, GuildContracts = true, GuildContractCount = 4,
        });
        var world = arena.World;
        var (sa, pa, a) = arena.Join("Hunter", new InventoryItem("T4_MAIN_SWORD"));
        var (sb, pb, b) = arena.Join("Helper", new InventoryItem("T4_MAIN_SWORD"));
        Guild g;
        lock (world.Sync)
        {
            Check(world.Guilds.Create(a, "Hunters", GuildLogo.Default, "", out _) == 0 && world.Guilds.Invite(a, "Helper", out _, out _) == 0
                  && world.Guilds.Accept(b, out _) == 0, "A founds Hunters, B joins (no party)");
            g = world.Guilds.Of(a.Character.Id)!;
        }
        var ghoul = arena.Spawn("T4_MOB_ROAMING_UNDEAD_GHOUL", new System.Numerics.Vector2(1.5f, 0));
        lock (world.Sync) ghoul.Health.Set(1, Math.Max(world.GameNow, ghoul.Health.Stamp));
        var (am, bm) = (pa.Events.Count, pb.Events.Count);
        CombatTest.Send(sa, new AttackStartRequest { wf = world.GameNow, co = ghoul.Id, cp = 0 });
        arena.Run(0.4);

        // §10.4: B shares the fame as a party member would; the guild gets a tenth of the gains; the kill contract counts once.
        var fa = Since(pa, am).Where(e => e.Code == EventCodes.UpdateFame).Select(e => L(e.P, 2)).ToList();
        var fb = Since(pb, bm).Where(e => e.Code == EventCodes.UpdateFame).Select(e => L(e.P, 2)).ToList();
        Check(ghoul.Dead && fa is [var ga] && fb is [var gb] && ga == gb && ga > 0 && g.Xp == (long)Math.Floor((ga + gb) / 10000.0 * 0.1),
              $"B gets the same share without a party; guild xp = 0.1 of both gains (got {string.Join(",", fa)} / {string.Join(",", fb)}, xp {g.Xp})");
        var contracts = g.Contracts!;
        Check(contracts.Goals.Count == 4 && contracts.Goals.Single(c => c.Kind == "kill") is { Progress: 1 }
              && Since(pa, am).Any(e => e.Code == EventCodes.SystemMessage && e.P[0] is string t && t.StartsWith("[Guild] New guild contracts")),
              "the contracts rolled and told; the kill counted once for two members");

        // The pile is B's to take too; its 5 % tax goes to the account, the members get GuildUpdate kind 2 {11 account, 14 500}.
        var pile = world.Maps.Find("0204")!.Objects.Values.OfType<SilverPile>().Single(p => p.SourceId == ghoul.Id);
        Check(pile.GuildId == g.Id && pile.MayTake(b), "the pile carries the guild: B may take it");
        (am, bm) = (pa.Events.Count, pb.Events.Count);
        CombatTest.Send(sb, new TakeSilverRequest { wf = world.GameNow, ago = pile.Id });
        var (gross, tax, _) = Combat.SilverValue(pile.Amount, pile.Factor, 1, 0.05);
        Check(Since(pb, bm).SingleOrDefault(e => e.Code == EventCodes.TakeSilver).P is { } take && L(take, 3) == gross && L(take, 5) == tax && tax > 0 && g.SilverA7m == tax,
              $"B's TakeSilver: key 5 = 5 % of the gross, into the account (got {g.SilverA7m}, tax {tax})");
        arena.Run(1.1);
        Check(Since(pa, am).Where(e => e.Code == EventCodes.GuildUpdate).Select(e => e.P).ToList() is [var u] && Convert.ToInt32(u[10]) == 2 && L(u, 11) == tax
              && L(u, 14) == 500 && !u.ContainsKey(1),
              "A: 103 kind 2 {11 the account, 14 500}, no member arrays");

        // §10.5: B (RECRUIT) may not pay out; the guild master pays 1 silver to B.
        lock (world.Sync)
        {
            var purse = b.Character.Silver;
            Check(world.Guilds.Payout(b, "Hunter", 1) is not null && world.Guilds.Payout(a, "Helper", tax / 10000 + 1) is not null
                  && world.Guilds.Payout(a, "Helper", 1) is null && b.Character.Silver == purse + 10000 && g.SilverA7m == tax - 10000,
                  "a RECRUIT's payout and one over the account refused; the master pays B 1 silver");
        }

        // The account, the experience and the contracts are saved (the tick's flush, the world's end).
        lock (world.Sync) world.Guilds.Flush();
        Check(arena.Characters.LoadGuilds().Single() is { } saved && saved.SilverA7m == g.SilverA7m && saved.Xp == g.Xp
              && saved.Contracts is { } sc && sc.Period == contracts.Period && sc.Goals.Single(c => c.Kind == "kill").Progress == 1,
              "saved: account, xp and the contracts' progress");

        // The last member out takes the account with it: a deleted guild destroys no silver.
        lock (world.Sync)
        {
            var (left, purse) = (g.SilverA7m, a.Character.Silver);
            Check(left > 0 && world.Guilds.Leave(b, Guid.Empty) == 0 && world.Guilds.Leave(a, Guid.Empty) == 0
                  && world.Guilds.Of(a.Character.Id) is null && a.Character.Silver == purse + left,
                  "the last member's leave pays it the account");
        }
    }
}
