using System.Buffers.Binary;
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
/// The guild windows of stageR (analysis/work/stageR/guild/SPEC.md): the finder and applications, the identity and texts with tax and
/// upkeep flag, and the history logs, each with players in one World through <see cref="FakePeer"/>s and a restart where state is kept.
/// </summary>
public static class GuildFinderTest
{
    public static IEnumerable<(string, Action)> All =>
    [
        ("guild finder: recruitment info (184/185, 7400), name search, filtered list, apply (kind-1 mail, 7240), accept / decline (GUILDAPPLDECLINED), instant join (4361/4360), welcome mail, restart", Finder),
        ("guild identity: codex (kind 0 keys 34-36), banner (28), optional stats (19400/19401), welcome mail text, crest for gold with cooldown, tax (kind 4), upkeep (kind 3), name resolve, restart", Identity),
        ("guild logs: deposit / withdrawal rows (bhv 2/3, signed amounts, amount filter), member events with filter, large amount as event 418, empty chest logs", Logs),
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
        public readonly ServerConfig Config = new() { JoinByCharacterId = true, GuildCreateCost = 1000, GuildRejoinCooldownMinutes = 0 };
        public World World;

        public Town()
        {
            Characters = Repository(Dir);
            World = new World(Config, Characters, Items()) { Sync = new object(), Clock = Clock };
        }

        public Member Join(string name, float x, long silver = 0)
        {
            var c = StarterKit.Default.NewCharacter(name);
            (c.Cluster, c.PositionX, c.PositionY, c.Silver) = ("0000", x, 5, silver * 10000);
            c.Equipment = [];
            c.Inventory = [];
            Check(Characters.Create(c), $"character {name} created");
            var peer = new FakePeer();
            var session = new WorldSession(peer, World);
            peer.Disconnected = session.OnDisconnected;
            lock (World.Sync) session.OnRequest(Request(new JoinRequest { ss = c.Id }, 1), encrypted: false);
            return new Member(session, peer, session.Player!);
        }

        public void Restart()
        {
            World.Dispose();
            World = new World(Config, Characters, Items()) { Sync = new object(), Clock = Clock };
        }

        public void Dispose()
        {
            World.Dispose();
            Characters.Dispose();
            try { Directory.Delete(Dir, true); } catch (IOException) { }
        }
    }

    private static void Check(bool cond, string what) => AlbionTests.Check(cond, what);

    private static OperationResponse? Send(Member c, AlbionRequest req, int? id = null)
    {
        var n = c.Peer.Responses.Count;
        c.Session.OnRequest(Request(req, id), encrypted: false);
        return c.Peer.Responses.Count > n ? c.Peer.Responses[^1] : null;
    }

    private static List<Dictionary<byte, object?>> Of(Member c, EventCodes code, int mark) =>
        [.. Since(c.Peer, mark).Where(e => e.Code == code).Select(e => e.P)];

    private static Guid G(object? v) => new((byte[])v!);

    private static Guid[] Gs(object? v) => v is null ? [] : [.. ((byte[])v).Chunk(16).Select(b => new Guid(b))];

    /// <summary>An int enum array on the wire: 4 bytes per element, little-endian.</summary>
    private static int[] Ints(object? v) => v is byte[] b ? [.. b.Chunk(4).Select(x => BinaryPrimitives.ReadInt32LittleEndian(x))] : v as int[] ?? [];

    private static Guild Found(Town town, Member founder, string name)
    {
        Check(Send(founder, new CreateGuildRequest { ip = name, iq = 3, ir = 86, _is = 15, it = 11, iu = 4416045, iv = 1.4f, ix = "BANNER_X" })?.ReturnCode == 0, $"'{name}' founded");
        return town.World.Guilds.Of(founder.Id)!;
    }

    private static void Into(Member inviter, Member joiner) =>
        Check(Send(inviter, new InviteToGuildRequest { sq = joiner.Player.Character.Name })?.ReturnCode == 0
              && Send(joiner, new JoinGuildRequest(), 7)?.ReturnCode == 0, $"{joiner.Player.Character.Name} joins");

    private static UpdateGuildRecruitmentInfoRequest Recruit(byte join) =>
        new() { ahj = 8, ahl = "We hunt", ahm = join, aho = "abc", ahp = 2, ahq = 18, ahr = 22, ahs = 128 };

    private static void Finder()
    {
        using var town = new Town();
        var (a, b, c, d) = (town.Join("Anna", 5, silver: 5000), town.Join("Boris", 10), town.Join("Carl", 15), town.Join("Dora", 20));
        var g = Found(town, a, "Wolves");
        var mail = town.World.Mail!;

        // 1. Recruitment info: set by the GM (no response), read by anyone (aip), 7400 for no guild.
        Check(Send(a, Recruit(Guilds.JoinApplication)) is null && g.Profile.Recruitment is { Join: Guilds.JoinApplication, Description: "We hunt" }, "184: stored, no response");
        var ri = Send(b, new RequestGuildRecruitmentInfoRequest { aae = g.Id, aaf = 1 }, 5)!;
        Check(ri.ReturnCode == 0 && ri.Parameters[255] is 5 && G(ri.Parameters[0]) == g.Id && Convert.ToInt32(ri.Parameters[1]) == 1 && ri.Parameters[4] is "We hunt"
              && Convert.ToInt32(ri.Parameters[7]) == 1 && ri.Parameters[9] is "abc" && Convert.ToInt32(ri.Parameters[10]) == 2,
              "185: rc 0 {0 guild, 1 echo, 4 description, 7 Application, 9 discord, 10 home}");
        Check(Send(b, new RequestGuildRecruitmentInfoRequest { aae = Guid.NewGuid() }, 5)?.ReturnCode == Guilds.RecruitmentNoGuild, "185 of an unknown guild: 7400");

        // 2. The finder: by name (any case, part of it), filtered by language (8 matches, 11 does not).
        var ns = Send(b, new RequestGuildFinderNameSearchRequest { aab = "wol" }, 6)!;
        Check(Gs(ns.Parameters[0]).SequenceEqual([g.Id]) && ns.Parameters[1] is string[] and ["Wolves"], "186: 'wol' finds Wolves");
        var fl = Send(b, new RequestGuildFinderFilteredListRequest { zy = 0, zz = 10, z4 = 8 }, 7)!;
        Check(Gs(fl.Parameters[2]).SequenceEqual([g.Id]) && fl.Parameters[3] is string[] and ["Wolves"] && fl.Parameters[16] is string[] and ["We hunt"]
              && fl.Parameters[25] is bool[] { Length: 1 }, "183 (English): one row, name and description, arrays of one length");
        Check(Gs(Send(b, new RequestGuildFinderFilteredListRequest { zy = 0, zz = 10, z4 = 11 }, 7)!.Parameters.GetValueOrDefault((byte)2)).Length == 0, "183 (German): no row");
        Check(Gs(Send(b, new RequestGuildFinderRecommendedListRequest { aac = 8 }, 8)!.Parameters.GetValueOrDefault((byte)3)).SequenceEqual([g.Id]), "187: Wolves recommended");

        // 3. An application guild: "Join" refused 4361; Apply → a kind-1 mail in the guild's mailbox, NewUnreadMails to A; again 7240.
        Check(Send(c, new InstantJoinGuildRequest { rc = g.Id }, 9)?.ReturnCode == Guilds.InstantNotOpen, "109 to an application guild: 4361");
        var am = a.Peer.Events.Count;
        Check(Send(b, new ApplyToGuildRequest { ba = g.Id, bb = 2, bc = "let me in", bd = 1 }, 9)?.ReturnCode == 0, "181: rc 0");
        var page = mail.MailPage(g.Id, true, MailCategory.All, 0, 50);
        Check(page is [var app] && app.Kind == MailKind.GuildApplication && app.Category == MailCategory.Guild && app.Sender == "Boris" && app.SenderId == b.Id
              && app.Body == "let me in" && Of(a, EventCodes.NewUnreadMails, am).Count > 0, "the guild's mailbox: kind 1 from Boris with the message; A told");
        var appId = page[0].Id;
        Check(Send(b, new ApplyToGuildRequest { ba = g.Id, bc = "again" }, 9)?.ReturnCode == Guilds.ApplyAlready, "applying twice: 7240");
        var read = Send(a, new ReadMailRequest { y5 = true, y6 = appId })!;
        Check(read.ReturnCode == 0 && read.Parameters[8] is System.Collections.IDictionary { Count: 1 }, "ReadMail of the application: key 8 with TF");

        // 4. Answers: an outsider 7287; A accepts → B a member (103 kind 0), the mail and the application gone.
        Check(Send(c, new AnswerGuildApplicationRequest { aw = appId, ax = true }, 10)?.ReturnCode == Guilds.AnswerNoPermission, "an outsider answering: 7287");
        var bm = b.Peer.Events.Count;
        Check(Send(a, new AnswerGuildApplicationRequest { aw = appId, ax = true }, 10)?.ReturnCode == 0 && town.World.Guilds.Of(b.Id) == g
              && Of(b, EventCodes.GuildUpdate, bm).Count == 1 && mail.Mail(appId) is null, "accept: B joins (103), the mail is gone");
        Check(Send(a, new AnswerGuildApplicationRequest { aw = appId, ax = true }, 10)?.ReturnCode == Guilds.AnswerUnable, "the same mail again: 7283");

        // 5. D applies, A declines: D gets the system mail GUILDAPPLDECLINED from the guild.
        Check(Send(d, new ApplyToGuildRequest { ba = g.Id, bc = "" }, 9)?.ReturnCode == 0, "D applies");
        var dApp = mail.MailPage(g.Id, true, MailCategory.All, 0, 50)[0].Id;
        Check(Send(a, new AnswerGuildApplicationRequest { aw = dApp, ax = false }, 10)?.ReturnCode == 0 && town.World.Guilds.Of(d.Id) is null
              && mail.MailPage(d.Id, false, MailCategory.All, 0, 50) is [{ Subject: "GUILDAPPLDECLINED", Kind: MailKind.System, Sender: "Wolves" }],
              "decline: D outside, its mail GUILDAPPLDECLINED (kind 3) from Wolves");

        // 6. An open guild with a welcome text: C joins at once (rc 0), gets the welcome mail; a second join 4360.
        Check(Send(a, new ChangeNewGuildMemberMailRequest { ez = "Hello recruit" }) is null && Send(a, Recruit(Guilds.JoinOpen)) is null, "welcome text, OpenJoin");
        var cm = c.Peer.Events.Count;
        Check(Send(c, new InstantJoinGuildRequest { rc = g.Id, rd = 2 }, 11)?.ReturnCode == 0 && town.World.Guilds.Of(c.Id) == g
              && Of(c, EventCodes.GuildUpdate, cm).Count == 1, "109: rc 0, C a member with its 103");
        Check(mail.MailPage(c.Id, false, MailCategory.All, 0, 50).Any(m => m.Subject == "Welcome to Wolves" && m.Body == "Hello recruit"), "C has the welcome mail");
        Check(Send(c, new InstantJoinGuildRequest { rc = g.Id }, 11)?.ReturnCode == Guilds.InstantInGuild, "joining again: 4360");

        // 7. A restart keeps the recruitment info and the welcome text.
        town.Restart();
        Check(town.World.Guilds.ById(g.Id)?.Profile is { Recruitment.Join: Guilds.JoinOpen, WelcomeMail: "Hello recruit" }, "restart: recruitment and welcome text kept");
    }

    private static void Identity()
    {
        using var town = new Town();
        var (a, b) = (town.Join("Anna", 5, silver: 5000), town.Join("Boris", 10));
        var g = Found(town, a, "Wolves");
        Into(a, b);

        // 1. Codex: rc 0, B gets 103 kind 0 with 34 the text, 35/36 the editor; a RECRUIT refused (rc 1).
        var bm = b.Peer.Events.Count;
        Check(Send(a, new SetGuildCodexRequest { afv = "Be nice" }, 3)?.ReturnCode == 0, "236: rc 0");
        Check(Of(b, EventCodes.GuildUpdate, bm) is [var u] && u[34] is "Be nice" && G(u[35]) == a.Id && u[36] is "Anna", "B: 103 with 34 codex, 35/36 Anna");
        Check(Send(b, new SetGuildCodexRequest { afv = "mine" }, 3)?.ReturnCode == Guilds.Refused && g.Profile.Codex == "Be nice", "a RECRUIT's codex: rc 1");

        // 2. Banner: no response, key 28 to B.
        bm = b.Peer.Events.Count;
        Check(Send(a, new GuildBannerChangeRequest { pq = "BANNER_Y" }) is null && g.Banner == "BANNER_Y" && Of(b, EventCodes.GuildUpdate, bm) is [var ub] && ub[28] is "BANNER_Y",
              "483: banner stored, B's 103 key 28");

        // 3. Optional stats: set (rc 0; a RECRUIT 19401, an unknown stat 19400), read with GuildAge = the founding time.
        Check(Send(a, new GuildSetOptionalStatsRequest { pt = [1, 4] }, 4)?.ReturnCode == 0 && Send(b, new GuildSetOptionalStatsRequest { pt = [1] }, 4)?.ReturnCode == Guilds.StatsNoPermission
              && Send(a, new GuildSetOptionalStatsRequest { pt = [99] }, 4)?.ReturnCode == Guilds.StatsInternal, "485: rc 0, 19401, 19400");
        var os = Send(b, new GuildGetOptionalStatsRequest { pr = g.Id }, 4)!;
        Check(G(os.Parameters[0]) == g.Id && Ints(os.Parameters[1]).SequenceEqual([1, 4]) && os.Parameters[2] is long[] v && v[0] == g.Created.Ticks && v[1] == 0,
              "484: {0 guild, 1 [GuildAge, 2v2 kills], 2 [founded, 0]}");

        // 4. Welcome mail text back.
        Send(a, new ChangeNewGuildMemberMailRequest { ez = "Hi" });
        Check(Send(a, new GetNewGuildMemberMailRequest(), 5)?.Parameters[1] is "Hi", "480: the text");

        // 5. Crest: without gold nothing changes; with 3000 gold it changes, B gets kind 0 with the new symbol, the gold is gone; again: cooldown.
        var logo = new ChangeGuildLogoRequest { eq = 1, er = 2, es = 3, et = 4, eu = 5, ev = 1, ew = 0 };
        Check(Send(a, logo) is null && g.Logo.Symbol == 86, "358 without gold: refused (no response)");
        a.Player.Character.Gold = 3000 * 10000L;
        bm = b.Peer.Events.Count;
        Check(Send(a, logo) is null && g.Logo.Symbol == 2 && a.Player.Character.Gold == 0 && Of(b, EventCodes.GuildUpdate, bm) is [var ul] && Convert.ToInt32(ul[21]) == 2,
              "358 with 3000 gold: the crest, B's 103 key 21");
        a.Player.Character.Gold = 3000 * 10000L;
        Check(Send(a, new ChangeGuildLogoRequest { eq = 1, er = 9, es = 3, et = 4, eu = 5, ev = 1 }) is null && g.Logo.Symbol == 2 && a.Player.Character.Gold == 3000 * 10000L, "a second change within the cooldown: refused, no gold taken");

        // 6. Tax and upkeep flag: GuildUpdate kinds 4 and 3 to B.
        bm = b.Peer.Events.Count;
        Send(a, new ChangeGuildTaxRequest { ey = 1500 });
        Send(a, new ChangeGuildPayUpkeepFlagRequest { ex = true });
        var ups = Of(b, EventCodes.GuildUpdate, bm);
        Check(g.TaxA7m == 1500 && ups.Any(x => Convert.ToInt32(x[10]) == 4 && Convert.ToInt64(x[14]) == 1500) && ups.Any(x => Convert.ToInt32(x[10]) == 3 && x[13] is true),
              "137: 103 kind 4 {14 1500}; 136: kind 3 {13 true}");
        Check(Send(b, new ChangeGuildTaxRequest { ey = 100 }) is null && g.TaxA7m == 1500, "a RECRUIT's tax change: refused");

        // 7. Names resolve to the character, the guild, or Guid.Empty.
        var rs = Send(a, new ResolveGuildOrPlayerNameRequest { aau = ["boris", "nobody"], aav = ["wolves"], aaw = [] }, 6)!;
        Check(Gs(rs.Parameters[0]).SequenceEqual([b.Id, Guid.Empty]) && Gs(rs.Parameters[1]).SequenceEqual([g.Id]), "203: [Boris, none], [Wolves]");

        // 8. A restart keeps codex, banner, stats, tax and flag.
        town.Restart();
        Check(town.World.Guilds.ById(g.Id) is { Banner: "BANNER_Y", TaxA7m: 1500, Profile: { Codex: "Be nice", PayUpkeep: true, OptionalStats: [1, 4] } }, "restart: all kept");
    }

    private static void Logs()
    {
        using var town = new Town();
        var (a, b, c) = (town.Join("Anna", 5, silver: 5000), town.Join("Boris", 10), town.Join("Carl", 15));
        var g = Found(town, a, "Wolves");
        Into(a, b);

        // 1. A deposits 100 silver and takes 40 back: two silver rows, newest first, signed amounts; the amount filter.
        Send(a, new DepositToGuildAccountRequest { jr = 100 * 10000L });
        Send(a, new WithdrawalFromAccountRequest { ai6 = 40 * 10000L });
        var lr = Send(a, new GetGuildAccountLogsRequest { mv = g.Id, mw = GuildLogType.Silver, mx = 0, my = 101, m2 = 7 }, 6)!;
        Check(lr.ReturnCode == 0 && lr.Parameters[0] is string[] and ["Anna", "Anna"] && Ints(lr.Parameters[1]).SequenceEqual([GuildLogReason.Withdrawal, GuildLogReason.Deposit])
              && lr.Parameters[3] is long[] and [-400000, 1000000] && lr.Parameters[4] is long[] { Length: 2 }, "159 silver: [withdrawal −40, deposit +100]");
        Check(Send(a, new GetGuildAccountLogsRequest { mv = g.Id, mw = GuildLogType.Silver, my = 101, m0 = 1 }, 6)!.Parameters[3] is long[] and [1000000], "deposits only");

        // 2. Member events: invited (A, Boris) and a role change (A, "Boris;@GUILDROLE_OFFICER"); the role-change filter.
        Send(a, new AssignGuildAccessTagRequest { cj = "Boris", ck = g.Tags[GuildRoles.TagIndex(GuildRole.Officer)] });
        var ev = Send(a, new GetGuildAccountLogsRequest { mv = g.Id, mw = GuildLogType.Event, my = 101 }, 6)!;
        Check(Ints(ev.Parameters[1]).SequenceEqual([GuildLogReason.AssignedRole, GuildLogReason.Invited]) && ev.Parameters[2] is string[] and ["Boris;@GUILDROLE_OFFICER", "Boris"],
              "159 events: [role Boris;OFFICER, invited Boris]");
        Check(Ints(Send(a, new GetGuildAccountLogsRequest { mv = g.Id, mw = GuildLogType.Event, my = 101, mz = 3 }, 6)!.Parameters[1]).SequenceEqual([GuildLogReason.AssignedRole]),
              "role changes only");
        Check(Send(c, new GetGuildAccountLogsRequest { mv = g.Id, mw = GuildLogType.Event, my = 101 }, 6)!.Parameters.GetValueOrDefault((byte)0) is null or string[] { Length: 0 },
              "an outsider: no rows");

        // 3. The large amount: one GuildAccountLogEvent 418 {0 0, 1 total, rows}; chest logs: empty arrays.
        var am = a.Peer.Events.Count;
        Check(Send(a, new GetGuildAccountLogsLargeAmountRequest { m3 = g.Id, m4 = GuildLogType.Event }) is null
              && Of(a, EventCodes.GuildAccountLogEvent, am) is [var big] && Convert.ToInt32(big[1]) == 2 && big[2] is string[] { Length: 2 }, "160: 418 with the 2 rows");
        Check(Send(a, new GetChestLogsRequest { mh = g.Id }, 7) is { ReturnCode: 0 } cl && cl.Parameters[0] is string[] { Length: 0 }, "157: empty arrays");

        // 4. The rows are in the character database (kept over a restart).
        Check(((IGuildBoardStore)town.Characters).GuildLogs(g.Id, GuildLogType.Silver, 0).Count == 2, "the rows are saved");
    }
}
