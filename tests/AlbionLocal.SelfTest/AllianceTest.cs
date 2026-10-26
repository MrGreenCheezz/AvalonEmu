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
using Microsoft.Data.Sqlite;
using static AlbionLocal.SelfTest.AlbionTests;
using static AlbionLocal.SelfTest.CombatTest;

namespace AlbionLocal.SelfTest;

/// <summary>
/// Alliances (analysis/work/stageP/hideouts/SPEC.md §7 A1/A2): players of guilds in one World through <see cref="FakePeer"/>s: create
/// with the guild account's cost and its errors, the alliance keys of the guild messages, disband and leave, the member and detail infos,
/// allies in Pvp.Of, a restart and the v38 migration.
/// </summary>
public static class AllianceTest
{
    public static IEnumerable<(string, Action)> All =>
    [
        ("alliance: create from the guild account (8320/8321/8323/8325/8326/8327/8328/8329), 103 kind 5 {16, 17}, 106 11/12, disband, cooldown 8330, leave alone", Create),
        ("alliance: member infos 210 and detail info 217 (all arrays one length), NewCharacter 51/52 and Join 79/80", Infos),
        ("alliance: allies Friendly under Pvp:Mode Official in a red zone (Hostile before), leave protection, alliancemember, Alliances:NoFriendlyFire", Allies),
        ("alliance: restart keeps alliance, guilds, founder and cooldown; v37 → v38 migration adds the alliance tables", Persistence),
        ("alliance: invite (8441/8442/8443/8444/8447) → kind-2 mail → accept (212 by mail id) → 210 lists both; decline, cancel 8522/8523, invitation kept over a restart", Membership),
        ("alliance: kick (8562/8563/8564), rejoin cooldown 8486, answer 8482, founder cannot leave with others, MaxGuilds 8445, MaxPlayers 8446/4251", KickAndLimits),
        ("alliance: Alliance chat channel (205 type 25) to both allied guilds, a line reaches allies only, the kicked guild 207 + 208", Chat),
    ];

    internal sealed record Member(WorldSession Session, FakePeer Peer, Player Player)
    {
        public Guid Id => Player.Character.Id;
    }

    internal sealed class Town : IDisposable
    {
        public readonly string Dir = NewDir();
        public readonly SqliteCharacterRepository Characters;
        public readonly ManualClock Clock = new();
        public readonly ServerConfig Config;
        public readonly ClusterPvp? Pvp;
        public World World;

        public Town(ClusterPvp? pvp = null, ServerConfig? config = null)
        {
            Config = config ?? new() { JoinByCharacterId = true, GuildCreateCost = 0, GuildRejoinCooldownMinutes = 0 };
            Pvp = pvp;
            Characters = Repository(Dir);
            World = NewWorld();
        }

        private World NewWorld() => new(Config, Characters, Items()) { Sync = new object(), Clock = Clock, PvpOverride = Pvp };

        public void Restart()
        {
            World.Dispose();
            World = NewWorld();
        }

        public Member Join(string name, float x = 5)
        {
            var c = StarterKit.Default.NewCharacter(name);
            (c.Cluster, c.PositionX, c.PositionY, c.Equipment, c.Inventory) = ("0000", x, 5, [], []);
            Check(Characters.Create(c), $"character {name} created");
            return Again(c.Id);
        }

        public Member Again(Guid id)
        {
            var peer = new FakePeer();
            var session = new WorldSession(peer, World);
            peer.Disconnected = session.OnDisconnected;
            lock (World.Sync) session.OnRequest(Request(new JoinRequest { ss = id }, 1), encrypted: false);
            return new Member(session, peer, session.Player!);
        }

        /// <summary><paramref name="founder"/> founds guild <paramref name="name"/> with <paramref name="silver"/> in its account.</summary>
        public Guild Guild(Member founder, string name, long silver = 0)
        {
            Check(World.Guilds.Create(founder.Player, name, GuildLogo.Default, "", out _) == 0, $"guild {name} founded");
            var g = World.Guilds.Of(founder.Id)!;
            g.SilverA7m = silver * 10000;
            return g;
        }

        /// <summary><paramref name="joiner"/> joins the guild of <paramref name="inviter"/> (as Guilds:JoinRole).</summary>
        public void Into(Member inviter, Member joiner) =>
            Check(World.Guilds.Invite(inviter.Player, joiner.Player.Character.Name, out _, out _) == 0 && World.Guilds.Accept(joiner.Player, out var g) == 0
                  && g is not null && Do(() => World.Guilds.AfterJoin(joiner.Player, g)), $"{joiner.Player.Character.Name} joins");

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

    private static bool Do(Action a)
    {
        a();
        return true;
    }

    private static void Check(bool cond, string what) => AlbionTests.Check(cond, what);

    internal static OperationResponse? Send(Member c, AlbionRequest req, int? id = 3)
    {
        var n = c.Peer.Responses.Count;
        c.Session.OnRequest(Request(req, id), encrypted: false);
        return c.Peer.Responses.Count > n ? c.Peer.Responses[^1] : null;
    }

    internal static List<Dictionary<byte, object?>> Of(Member c, EventCodes code, int mark) =>
        [.. Since(c.Peer, mark).Where(e => e.Code == code).Select(e => e.P)];

    internal static Guid G(object? v) => new((byte[])v!);

    internal static Guid[] Gs(object? v) => [.. ((byte[])v!).Chunk(16).Select(b => new Guid(b))];

    /// <summary>An array of one row as it comes off the wire (an int array may come packed as 4 bytes, a long one as 8).</summary>
    private static bool Rows1(object? v) => v switch
    {
        byte[] b => b.Length is 1 or 4 or 8,
        Array a => a.Length == 1,
        _ => false,
    };

    internal static short Create(Member m, string name, string tag) => Send(m, new AllianceCreateRequest { an = name, ao = tag })!.ReturnCode;

    private static void Create()
    {
        using var town = new Town();
        var (a, b, c, d, o) = (town.Join("Anna", 5), town.Join("Boris", 6), town.Join("Carl", 7), town.Join("Dora", 8), town.Join("Olga", 9));
        var wolves = town.Guild(a, "Wolves", silver: 1_000_000);
        town.Into(a, b); // a RECRUIT: no ALLIANCE_MANAGEMENT
        var bears = town.Guild(d, "Bears", silver: 20_000_000);

        Check(Create(c, "Iron Pact", "IRON") == Alliances.CreateNotInGuild && Create(b, "Iron Pact", "IRON") == Alliances.CreateNoRights,
              "not in a guild 8328; a RECRUIT 8325");
        Check(Create(a, "x", "IRON") == Alliances.CreateNameNotAllowed && Create(a, "Iron Pact", "TOOLONG") == Alliances.CreateInvalidTag
              && Create(a, "Iron Pact", "I") == Alliances.CreateInvalidTag, "bad name 8321, bad tags 8323");
        Check(Create(a, "Iron Pact", "IRON") == Alliances.CreateNotEnoughSilver && wolves.SilverA7m == 1_000_000 * 10000L,
              "1 M in the guild account: 8320, nothing taken");

        // A founds it with 6 M in the account: 5 M taken; A and B get 103 kind 5 {16 name, 17 id}; the observer O gets A's 106 with 11/12.
        wolves.SilverA7m = 6_000_000 * 10000L;
        var (am, bm, om) = (a.Peer.Events.Count, b.Peer.Events.Count, o.Peer.Events.Count);
        var r = Send(a, new AllianceCreateRequest { an = "Iron Pact", ao = "IRON" })!;
        var pact = town.World.Alliances.OfGuild(wolves.Id);
        Check(r.ReturnCode == 0 && pact is { Name: "Iron Pact", Tag: "IRON" } && pact.Founder == wolves.Id && wolves.SilverA7m == 1_000_000 * 10000L,
              "rc 0, the alliance with Wolves as founder, 5 M taken from the guild account");
        Check(new[] { (a, am), (b, bm) }.All(x => Of(x.Item1, EventCodes.GuildUpdate, x.Item2).Any(u => Convert.ToInt32(u[10]) == Alliances.KindAlliance
              && G(u[0]) == wolves.Id && u[16] is "Iron Pact" && G(u[17]) == pact!.Id)), "A and B: 103 kind 5 {0 guild, 16 Iron Pact, 17 the alliance}");
        Check(Of(o, EventCodes.GuildMemberWorldUpdate, om).Any(w => Convert.ToInt64(w[2]) == a.Player.Id && G(w[11]) == pact!.Id && w[12] is "Iron Pact"),
              "the observer: A's 106 with 11 the alliance and 12 its name");
        Check(Create(a, "Other Pact", "OTH") == Alliances.CreateAlreadyInAlliance, "again: 8329");
        Check(Create(d, "iron pact", "BEAR") == Alliances.CreateNameTaken && Create(d, "Bear Pact", "iron") == Alliances.CreateTagTaken,
              "the name 8326 and the tag 8327 in another case");

        // Disband: only the founder guild; kind 5 with "" and no 17; the cooldown keeps Wolves from founding another (8330, key 0 its end).
        Check(Send(d, new AllianceDisbandRequest { ap = pact!.Id })!.ReturnCode == Alliances.Refused, "another guild's disband: rc 1");
        am = a.Peer.Events.Count;
        Check(Send(a, new AllianceDisbandRequest { ap = pact.Id })!.ReturnCode == 0 && town.World.Alliances.OfGuild(wolves.Id) is null
              && town.World.Alliances.Find("Iron Pact") is null, "the founder disbands: rc 0, gone");
        Check(Of(a, EventCodes.GuildUpdate, am).Any(u => Convert.ToInt32(u[10]) == Alliances.KindAlliance && u[16] is "" && !u.ContainsKey(17)),
              "103 kind 5 {16 \"\", no 17}");
        wolves.SilverA7m = 6_000_000 * 10000L;
        r = Send(a, new AllianceCreateRequest { an = "Iron Pact", ao = "IRON" })!;
        Check(r.ReturnCode == Alliances.CreateCooldown && Convert.ToInt64(r.Parameters[0]) > town.World.GameNow, "within Alliances:ChangeCooldownMinutes: 8330 {0 the end}");

        // Leave: the founder alone disbands; a guild without an alliance: rc 1.
        Check(Create(d, "Bear Pact", "BEAR") == 0 && bears.SilverA7m == 15_000_000 * 10000L, "Bears found Bear Pact");
        var bear = town.World.Alliances.OfGuild(bears.Id)!;
        Check(Send(a, new AllianceLeaveRequest { av = bear.Id })!.ReturnCode == Alliances.Refused, "not in it: rc 1");
        Check(Send(d, new AllianceLeaveRequest { av = bear.Id })!.ReturnCode == 0 && town.World.Alliances.All.Count() == 0, "the founder alone leaves: disbanded");
    }

    private static void Infos()
    {
        using var town = new Town();
        var (a, o) = (town.Join("Anna", 5), town.Join("Olga", 6));
        var wolves = town.Guild(a, "Wolves", silver: 5_000_000);
        Check(Create(a, "Iron Pact", "IRON") == 0, "Iron Pact founded");
        var pact = town.World.Alliances.OfGuild(wolves.Id)!;

        var r = Send(a, new AllianceGetMemberInfosRequest { ar = pact.Id })!;
        var p = r.Parameters;
        Check(r.ReturnCode == 0 && G(p[0]) == wolves.Id && p[2] is "IRON" && p[3] is "Iron Pact" && Gs(p[6]).SequenceEqual([wolves.Id]) && p[7] is string[] and ["Wolves"]
              && p[9] is bool[] and [false] && Enumerable.Range(7, 12).All(k => p.ContainsKey((byte)k) && Rows1(p[(byte)k])),
              "210: 0 founder, 2 tag, 3 name, 6-18 one row each (Wolves, not invited)");
        Check(Send(a, new AllianceGetMemberInfosRequest { ar = Guid.NewGuid() })!.ReturnCode == Alliances.Refused, "210 of an unknown alliance: rc 1");

        r = Send(o, new AllianceGetDetailInfoRequest { aq = "iron" })!;
        p = r.Parameters;
        Check(r.ReturnCode == 0 && p[0] is "Iron Pact" && p[1] is "IRON" && Convert.ToInt32(p[2]) == 1 && Convert.ToInt32(p[3]) == 1 && G(p[4]) == pact.Id
              && p[6] is string[] and ["Wolves"] && Gs(p[7]).Length == 1 && Enumerable.Range(8, 14).All(k => p.ContainsKey((byte)k) && Rows1(p[(byte)k])),
              "217 by tag: 0 name, 1 tag, 2 guilds, 3 players, 4 id, 6-21 one row each");
        Check(Send(o, new AllianceGetDetailInfoRequest { aq = "nobody" })!.ReturnCode == Alliances.Refused, "217 of an unknown alliance: rc 1");

        // A newcomer sees A with 51/52; A's relog Join has 79/80.
        var e = town.Join("Emil", 7);
        Check(Since(e.Peer, 0).FirstOrDefault(x => x.Code == EventCodes.NewCharacter && Convert.ToInt64(x.P[0]) == a.Player.Id).P is { } nc
              && nc[51] is "Iron Pact" && G(nc[52]) == pact.Id, "NewCharacter(A): 51 Iron Pact, 52 the alliance");
        lock (town.World.Sync) a.Peer.Disconnect();
        var a2 = town.Again(a.Id);
        var join = a2.Peer.Responses[0].Parameters;
        Check(join[79] is "Iron Pact" && G(join[80]) == pact.Id, "Join: 79 Iron Pact, 80 the alliance");
        var full = Of(a2, EventCodes.GuildUpdate, 0).FirstOrDefault(u => !u.ContainsKey(10));
        Check(full is not null && full[16] is "Iron Pact" && G(full[17]) == pact.Id, "after the login: 103 kind 0 with 16/17");
        var tagged = new Town(config: new() { JoinByCharacterId = true, GuildCreateCost = 0, AlliancePlateText = AlliancePlateText.Tag });
        using (tagged)
        {
            var t = tagged.Join("Tina");
            tagged.Guild(t, "Owls", silver: 5_000_000);
            var tm = t.Peer.Events.Count;
            Check(Create(t, "Owl Pact", "OWL") == 0 && Of(t, EventCodes.GuildUpdate, tm).Any(u => u[16] is "OWL"), "Alliances:PlateText Tag: 16 = the tag");
        }
    }

    private static void Allies()
    {
        using var town = new Town(new ClusterPvp(PvpRule.PvpAllowed)); // a red zone
        var (a, e) = (town.Join("Anna", 5), town.Join("Emil", 6));
        var wolves = town.Guild(a, "Wolves", silver: 5_000_000);
        var bears = town.Guild(e, "Bears");
        lock (town.World.Sync) (a.Player.Character.Flag, e.Player.Character.Flag) = (Pvp.Red, Pvp.Red);
        Check(town.Config.PvpMode == PvpMode.Official && Pvp.Of(a.Player, e.Player) == Relation.Hostile && SpellTargets.OfficialOf(a.Player, e.Player) == Relation.Hostile,
              "Pvp:Mode Official, red zone, two Red players of two guilds: Hostile");
        Check(Create(a, "Iron Pact", "IRON") == 0, "Iron Pact founded");
        var pact = town.World.Alliances.OfGuild(wolves.Id)!;
        lock (town.World.Sync) town.World.Alliances.AddGuild(pact, bears);
        Check(Pvp.Of(a.Player, e.Player) == Relation.Friendly && Pvp.Of(e.Player, a.Player) == Relation.Friendly
              && SpellTargets.OfficialOf(a.Player, e.Player) == Relation.Friendly, "allies: Friendly both ways (the client's own table too)");
        Check(town.World.Alliances.SameAlliance(a.Id, e.Id) && !town.World.Guilds.Same(a.Id, e.Id), "alliancemember true, guildmember false");

        // Bears leave: the leave protection keeps them friendly for 30 s, then Hostile.
        Check(Send(e, new AllianceLeaveRequest { av = pact.Id })!.ReturnCode == 0 && town.World.Alliances.OfGuild(bears.Id) is null, "Bears leave");
        Check(Pvp.Of(a.Player, e.Player) != Relation.Hostile && !town.World.Alliances.SameAlliance(a.Id, e.Id), "within Alliances:LeaveProtectionSeconds: not Hostile");
        town.Run(31);
        Check(Pvp.Of(a.Player, e.Player) == Relation.Hostile, "after it: Hostile");

        // Alliances:NoFriendlyFire: a Hostile ally (two factions in one alliance) is Neutral.
        using var pve = new Town(new ClusterPvp(PvpRule.PvpAllowed), new() { JoinByCharacterId = true, GuildCreateCost = 0, AllianceNoFriendlyFire = true });
        var (x, y) = (pve.Join("Xena"), pve.Join("Yuri"));
        var gx = pve.Guild(x, "Foxes", silver: 5_000_000);
        var gy = pve.Guild(y, "Lynx");
        Check(Create(x, "Fox Pact", "FOX") == 0, "Fox Pact founded");
        lock (pve.World.Sync)
        {
            pve.World.Alliances.AddGuild(pve.World.Alliances.OfGuild(gx.Id)!, gy);
            (x.Player.Character.Flag, y.Player.Character.Flag) = (1, 2); // two factions
        }
        Check(SpellTargets.OfficialOf(x.Player, y.Player) == Relation.Hostile && Pvp.Of(x.Player, y.Player) == Relation.Neutral,
              "two factions in one alliance: the table Hostile, Alliances:NoFriendlyFire Neutral");
        Check(LoadTestConfig("no-such-config.json", ["--Preset=pve"]).AllianceNoFriendlyFire && !LoadTestConfig("no-such-config.json", ["--Preset=official"]).AllianceNoFriendlyFire,
              "Alliances:NoFriendlyFire: pve true, official false");
    }

    /// <summary>The open invitation mail of guild <paramref name="g"/> (kind 2 in its mailbox); null = none.</summary>
    private static Mail? InvitationMail(Town town, Guild g) =>
        town.World.Mail!.MailPage(g.Id, true, MailCategory.All, 0, 50).FirstOrDefault(m => m.Kind == MailKind.AllianceInvitation);

    private static short Invite(Member m, Guid alliance, string guild) => Send(m, new AllianceInviteRequest { _as = alliance, at = guild })!.ReturnCode;

    private static OperationResponse Answer(Member m, long mail, bool accept) => Send(m, new AllianceAnswerInvitationRequest { ak = mail, al = accept })!;

    private static void Membership()
    {
        using var town = new Town();
        var (a, b, e, o) = (town.Join("Anna", 5), town.Join("Boris", 6), town.Join("Emil", 7), town.Join("Olga", 8));
        var wolves = town.Guild(a, "Wolves", silver: 5_000_000);
        town.Into(a, b);
        var bears = town.Guild(e, "Bears");
        var owls = town.Guild(o, "Owls", silver: 5_000_000);
        Check(Create(a, "Iron Pact", "IRON") == 0, "Iron Pact founded");
        var pact = town.World.Alliances.OfGuild(wolves.Id)!;

        Check(Invite(b, pact.Id, "Bears") == Alliances.InviteNoRights && Invite(e, pact.Id, "Owls") == Alliances.InviteNotInAlliance
              && Invite(a, pact.Id, "Nobody") == Alliances.InviteGuildNotFound && Invite(a, pact.Id, "wolves") == Alliances.InviteGuildInAlliance,
              "a RECRUIT 8442, a guild without an alliance 8441, an unknown guild 8443, a guild in an alliance 8444");
        var em = e.Peer.Events.Count;
        Check(Invite(a, pact.Id, "bears") == 0 && Invite(a, pact.Id, "Bears") == Alliances.InviteAlreadyInvited, "invite Bears: rc 0; again 8447");
        var mail = InvitationMail(town, bears);
        Check(mail is { Category: MailCategory.Guild, Sender: "Wolves", Body: "Wolves|Iron Pact" } && Of(e, EventCodes.NewUnreadMails, em).Count > 0,
              "the invitation: a kind-2 mail in Bears' mailbox from Wolves (body inviter|alliance), NewUnreadMails to its master");
        var rows = Send(a, new AllianceGetMemberInfosRequest { ar = pact.Id })!.Parameters;
        Check(Gs(rows[6]).SequenceEqual([wolves.Id, bears.Id]) && rows[9] is bool[] and [false, true], "210: Bears listed as invited");

        // An open invitation survives a restart; then Bears' master accepts with the mail's id.
        town.Restart();
        (a, e) = (town.Again(a.Id), town.Again(e.Id));
        pact = town.World.Alliances.ById(pact.Id)!;
        (wolves, bears) = (town.World.Guilds.ById(wolves.Id)!, town.World.Guilds.ById(bears.Id)!);
        Check(town.World.Alliances.InvitesOf(pact).Single().Mail == mail!.Id, "restart: the invitation is still open");
        Check(Answer(e, mail.Id + 1, true).ReturnCode == Alliances.Refused, "another mail id: rc 1");
        em = e.Peer.Events.Count;
        Check(Answer(e, mail.Id, true).ReturnCode == 0 && town.World.Alliances.OfGuild(bears.Id) == pact && InvitationMail(town, bears) is null,
              "accept: rc 0, Bears in the alliance, the invitation mail gone");
        Check(Of(e, EventCodes.GuildUpdate, em).Any(u => Convert.ToInt32(u[10]) == Alliances.KindAlliance && u[16] is "Iron Pact" && G(u[17]) == pact.Id),
              "Bears' members: 103 kind 5 with the alliance");
        rows = Send(a, new AllianceGetMemberInfosRequest { ar = pact.Id })!.Parameters;
        Check(Gs(rows[6]).SequenceEqual([wolves.Id, bears.Id]) && rows[9] is bool[] and [false, false] && rows[7] is string[] and ["Wolves", "Bears"],
              "210 after the accept: Wolves and Bears, neither invited");

        // Owls: invited and declined (rc 0, mail gone); invited again and cancelled by the founder (8522 for Bears, 8523 when none).
        o = town.Again(o.Id);
        owls = town.World.Guilds.ById(owls.Id)!;
        Check(Convert.ToInt32(Send(o, new AllianceGetDetailInfoRequest { aq = "Iron Pact" })!.Parameters[2]) == 2, "217: 2 guilds");
        Check(Invite(a, pact.Id, "Owls") == 0 && InvitationMail(town, owls) is { } om && Answer(o, om.Id, false).ReturnCode == 0
              && InvitationMail(town, owls) is null && town.World.Alliances.OfGuild(owls.Id) is null, "decline: rc 0, the mail gone, not in it");
        Check(Invite(a, pact.Id, "Owls") == 0 && Send(e, new AllianceCancelInvitationRequest { am = owls.Id })!.ReturnCode == Alliances.CancelNoRights
              && Send(a, new AllianceCancelInvitationRequest { am = owls.Id })!.ReturnCode == 0 && InvitationMail(town, owls) is null
              && Send(a, new AllianceCancelInvitationRequest { am = owls.Id })!.ReturnCode == Alliances.CancelNotFound,
              "cancel: Bears 8522, the founder rc 0 (mail gone), again 8523");
    }

    private static void KickAndLimits()
    {
        using var town = new Town(config: new() { JoinByCharacterId = true, GuildCreateCost = 0, GuildRejoinCooldownMinutes = 0, AllianceMaxGuilds = 2, AllianceMaxPlayers = 4 });
        var (a, e, f, o) = (town.Join("Anna", 5), town.Join("Emil", 6), town.Join("Fritz", 7), town.Join("Olga", 8));
        var wolves = town.Guild(a, "Wolves", silver: 5_000_000);
        var bears = town.Guild(e, "Bears");
        town.Into(e, f); // Bears: 2 members, Fritz a RECRUIT
        town.Guild(o, "Owls");
        Check(Create(a, "Iron Pact", "IRON") == 0, "Iron Pact founded");
        var pact = town.World.Alliances.OfGuild(wolves.Id)!;
        Check(Invite(a, pact.Id, "Bears") == 0, "Bears invited");
        var mail = InvitationMail(town, bears)!;
        Check(Answer(f, mail.Id, true).ReturnCode == Alliances.AnswerNoRights && Answer(e, mail.Id, true).ReturnCode == 0, "a RECRUIT answers: 8482; the master: rc 0");
        Check(Invite(a, pact.Id, "Owls") == Alliances.InviteFullGuilds, "Alliances:MaxGuilds 2: 8445");

        // MaxPlayers 4: Gina makes 4 players; a guild invitation of a fifth is refused (4251).
        var g = town.Join("Gina", 9);
        town.Into(a, g);
        town.Join("Hugo", 10);
        Check(town.World.Guilds.Invite(a.Player, "Hugo", out _, out _) == Guilds.AllianceMaxPlayers, "a guild invitation past Alliances:MaxPlayers: 4251");

        Check(Send(a, new AllianceLeaveRequest { av = pact.Id })!.ReturnCode == Alliances.Refused, "the founder cannot leave while Bears are in it");
        Check(Send(e, new AllianceKickGuildRequest { au = wolves.Id })!.ReturnCode == Alliances.KickNoRights
              && Send(a, new AllianceKickGuildRequest { au = wolves.Id })!.ReturnCode == Alliances.KickFounder
              && Send(a, new AllianceKickGuildRequest { au = Guid.NewGuid() })!.ReturnCode == Alliances.KickGuildNotFound,
              "kick: by Bears 8562, the founder itself 8564, an unknown guild 8563");
        var em = e.Peer.Events.Count;
        Check(Send(a, new AllianceKickGuildRequest { au = bears.Id })!.ReturnCode == 0 && town.World.Alliances.OfGuild(bears.Id) is null
              && Of(e, EventCodes.GuildUpdate, em).Any(u => Convert.ToInt32(u[10]) == Alliances.KindAlliance && u[16] is ""), "kick Bears: rc 0, kind 5 without an alliance");

        // Re-invited: the rejoin cooldown (8486 with key 0).
        Check(Invite(a, pact.Id, "Bears") == 0, "Bears invited again");
        var r = Answer(e, InvitationMail(town, bears)!.Id, true);
        Check(r.ReturnCode == Alliances.AnswerRejoinCooldown && Convert.ToInt64(r.Parameters[0]) > town.World.GameNow, "rejoin within the cooldown: 8486 {0 the end}");

        // MaxPlayers on the invitation: Wolves grows to 4 players, Owls (1 more) is refused.
        Check(Send(a, new AllianceCancelInvitationRequest { am = bears.Id })!.ReturnCode == 0, "the invitation cancelled");
        var i = town.Join("Ida", 11);
        town.Into(a, i);
        var j = town.Join("Jon", 12);
        town.Into(a, j);
        Check(town.World.Alliances.Players(pact) == 4 && Invite(a, pact.Id, "Owls") == Alliances.InviteFullPlayers, "Alliances:MaxPlayers 4 with 4 players: inviting Owls 8446");
    }

    private static void Chat()
    {
        using var town = new ChatTest.ChatTown();
        var (a, e, o) = (town.Login("Anna"), town.Login("Emil"), town.Login("Olga"));
        var w = town.World;
        Check(w.Guilds.Create(a.P, "Wolves", GuildLogo.Default, "", out _) == 0 && w.Guilds.Create(e.P, "Bears", GuildLogo.Default, "", out _) == 0
              && w.Guilds.Create(o.P, "Owls", GuildLogo.Default, "", out _) == 0, "three guilds");
        w.Guilds.Of(a.Id)!.SilverA7m = 5_000_000 * 10000L;
        var ma = a.ChatPeer.Events.Count;
        Check(w.Alliances.Create(a.P, "Iron Pact", "IRON", out _) == 0, "Iron Pact founded");
        var id = ChatTest.IdOf(a, ChatChannelType.Alliance);
        Check(id >= ChatHub.FirstDynamicId && ChatTest.Of(a.ChatPeer, EventCodes.NewChatChannels, ma).Any(n => ChatTest.Ints(n[0]) is [25]), "the founder: 205 {25, id}");
        var pact = w.Alliances.Of(a.Id)!;
        Check(w.Alliances.Invite(a.P, pact.Id, "Bears") == 0 && w.Alliances.Answer(e.P, w.Alliances.InvitesOf(pact).Single().Mail, true, out _) == 0
              && ChatTest.IdOf(e, ChatChannelType.Alliance) == id && ChatTest.IdOf(o, ChatChannelType.Alliance) == -1,
              "Bears accept: 205 {25, the same id}; Owls none");
        ChatTest.JoinChannels(a, ChatChannelType.Alliance);
        ChatTest.JoinChannels(e, ChatChannelType.Alliance);
        var (la, le, lo) = (a.ChatPeer.Events.Count, e.ChatPeer.Events.Count, o.ChatPeer.Events.Count);
        ChatTest.Line(e, id, "allies, to the bank");
        Check(ChatTest.Lines(a, la) is [(var i, "Emil", "allies, to the bank")] && i == id && ChatTest.Lines(e, le).Count == 1 && ChatTest.Lines(o, lo).Count == 0,
              "an Alliance line: to both allied guilds' members, not to Owls");
        var me = e.ChatPeer.Events.Count;
        Check(w.Alliances.Kick(a.P, w.Guilds.Of(e.Id)!.Id) == 0, "Bears kicked");
        Check(Since(e.ChatPeer, me).Where(x => x.Code is EventCodes.LeftChatChannel or EventCodes.RemovedChatChannel).Select(x => (x.Code, Convert.ToInt32(x.P[0])))
              .SequenceEqual([(EventCodes.LeftChatChannel, id), (EventCodes.RemovedChatChannel, id)]), "the kicked guild: 207 + 208 of the Alliance channel");
        (la, le) = (a.ChatPeer.Events.Count, e.ChatPeer.Events.Count);
        ChatTest.Line(a, id, "bye");
        Check(ChatTest.Lines(a, la).Count == 1 && ChatTest.Lines(e, le).Count == 0, "no later Alliance line to the kicked guild");
    }

    private static void Persistence()
    {
        using (var town = new Town())
        {
            var (a, e) = (town.Join("Anna", 5), town.Join("Emil", 6));
            var wolves = town.Guild(a, "Wolves", silver: 5_000_000);
            var bears = town.Guild(e, "Bears");
            Check(Create(a, "Iron Pact", "IRON") == 0, "Iron Pact founded");
            var pact = town.World.Alliances.OfGuild(wolves.Id)!;
            lock (town.World.Sync) town.World.Alliances.AddGuild(pact, bears);
            town.Restart();
            var back = town.World.Alliances.Find("iron pact");
            Check(back is { Tag: "IRON" } && back.Id == pact.Id && back.Founder == wolves.Id && back.Guilds.Select(m => m.Guild).SequenceEqual([wolves.Id, bears.Id])
                  && town.World.Alliances.OfGuild(bears.Id) == back, "restart: the alliance, its guilds in order and its founder");
            town.World.Alliances.Disband(back!, "test");
            town.Restart();
            var a2 = town.Again(a.Id);
            town.World.Guilds.Of(a.Id)!.SilverA7m = 6_000_000 * 10000L;
            Check(town.World.Alliances.All.Count() == 0 && Create(a2, "New Pact", "NEW") == Alliances.CreateCooldown, "disbanded stays gone; the change cooldown survives a restart");
        }

        // v37 → v38: a save without the alliance tables gets them.
        var dir = NewDir();
        var path = Path.Combine(dir, "save.db");
        try
        {
            using (new SqliteCharacterRepository(path)) { }
            using (var c = new SqliteConnection($"Data Source={path};Pooling=False"))
            {
                c.Open();
                using var cmd = c.CreateCommand();
                cmd.CommandText = "DROP TABLE alliance_history; DROP TABLE alliance_invites; DROP TABLE alliance_guilds; DROP TABLE alliances; PRAGMA user_version=37;";
                cmd.ExecuteNonQuery();
            }
            using (var migrated = new SqliteCharacterRepository(path))
                Check(migrated.LoadAlliances().Count == 0 && migrated.LoadAllianceInvites().Count == 0 && migrated.LoadAllianceHistory().Count == 0, "v37 save migrated: empty alliance tables");
            using (var c = new SqliteConnection($"Data Source={path};Pooling=False"))
            {
                c.Open();
                using var cmd = c.CreateCommand();
                cmd.CommandText = "PRAGMA user_version";
                Check(Convert.ToInt64(cmd.ExecuteScalar()) == 42, "schema version 42 (v38 alliances, v39 territory owners, v40 hideouts)");
            }
        }
        finally { Directory.Delete(dir, true); }
    }
}
