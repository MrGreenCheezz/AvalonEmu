using System.Diagnostics;
using System.Globalization;
using System.Net;
using AlbionLocal.Photon;
using AlbionLocal.Photon.Serialization;
using AlbionLocal.Server;
using AlbionLocal.Server.Content;
using AlbionLocal.Server.Data;
using AlbionLocal.Server.Game;
using AlbionLocal.Server.Network;
using AlbionLocal.Server.Persistence;
using AlbionLocal.Server.Protocol;

namespace AlbionLocal.SelfTest;

/// <summary>Login chain against in-process login and game servers, checked the way the client reads it.</summary>
public static class AlbionTests
{
    internal static string? ClientGameDataOverride { get; set; }
    public static IEnumerable<(string, Action)> All =>
    [
        ("albion serializer round trip", SerializerRoundTrip),
        ("wire: empty enum array stub is byte[0]", WireEmptyEnumStub),
        ("wire: int enum array packs 4 bytes per element", WirePackedEnum),
        ("wire: byte[][] / Guid[][] / enum[][] header layout", WirePackedNested),
        ("wire: compressed GameTimeStamp", WireCompressedTime),
        ("wire: no integer narrowing, rank-3 empty rows left out", WireArraysAsIs),
        ("albion login → select → join", LoginChain),
        ("stats: calculator matches the official values", StatVectors),
        ("stats: join bars, unlocks, bar update on unequip", StatsInGame),
        ("items: unknown and overflow entries survive saving", UnloadedItemsSurvive),
        ("spells: defaults, change, rejection, saved across a rejoin", SpellSelection),
        ("spells: own progression, unresolved names survive saving", SpellsOwnProgression),
        ("config: defaults, file, command line, unknown key", ConfigLoad),
        ("logout: cancel keeps the session, the countdown ends it", Logout),
        ("session: WorldSession through a FakePeer, object ids shared by the World", WorldSessionFakePeer),
        ("opcodes: handlers bound to their request classes, status gate, unhandled default", Opcodes),
        ("opcode stubs: content file, BuyPremium's code, silent and ack answers, mistakes stop the start", OpcodeStubsContent),
        ("side requests in code: referral season end, status online, GetRankings echoes the list asked for, GetMailInfos an empty mailbox", SideRequests),
    ];

    /// <summary>
    /// The startup check of both session types (ServerHost runs it) and the bad bindings it refuses; then, on a
    /// login session through a FakePeer, the status gate and the empty default reply of an unhandled operation.
    /// </summary>
    private static void Opcodes()
    {
        OpcodeTable.Build(typeof(LoginSession), typeof(WorldSession), typeof(ChatSession));
        var login = OpcodeTable.For(typeof(LoginSession));
        var game = OpcodeTable.For(typeof(WorldSession));
        Check(login.Handlers.Count == 8 && game.Handlers.Count == 227 && OpcodeTable.For(typeof(ChatSession)).Handlers.Count == 6 && game.Find(OperationCodes.Move) is { Quiet: true, Status: SessionStatus.InWorld }
              && game.Find(OperationCodes.QuestGiverRequest) is { Status: SessionStatus.InWorld }
              && game.Find(OperationCodes.SetNextTutorialState) is { Status: SessionStatus.InWorld }
              && game.Find(OperationCodes.UseFunction) is { Status: SessionStatus.InWorld }
              && game.Find(OperationCodes.Join) is { Status: SessionStatus.Connected } && login.Find(OperationCodes.SelectCharacter) is { Status: SessionStatus.Authed }
              && login.Find(OperationCodes.GetGameServerByCluster) is { Status: SessionStatus.Authed } && game.Find(OperationCodes.ChangeCluster) is { Status: SessionStatus.InWorld }
              && game.Find(OperationCodes.HarvestStart) is { Status: SessionStatus.InWorld } && game.Find(OperationCodes.HarvestCancel) is { Status: SessionStatus.InWorld }
              && game.Find(OperationCodes.AttackStart) is { Quiet: true, Status: SessionStatus.InWorld } && game.Find(OperationCodes.CastStart) is { Status: SessionStatus.InWorld }
              && game.Find(OperationCodes.CastCancel) is { Status: SessionStatus.InWorld } && game.Find(OperationCodes.RegisterToObject) is { Status: SessionStatus.InWorld }
              && game.Find(OperationCodes.InventoryMoveGivenItems) is { Status: SessionStatus.InWorld } && game.Find(OperationCodes.DeathRecap) is { Status: SessionStatus.InWorld }
              && game.Find(OperationCodes.TakeSilver) is { Status: SessionStatus.InWorld } && game.Find(OperationCodes.LoadoutReadHeaders) is { Status: SessionStatus.InWorld }
              && game.Find(OperationCodes.Mount) is { Status: SessionStatus.InWorld } && game.Find(OperationCodes.MountCancel) is { Status: SessionStatus.InWorld }
              && game.Find(OperationCodes.PartyInvitePlayer) is { Status: SessionStatus.InWorld } && game.Find(OperationCodes.PartySetRole) is { Status: SessionStatus.InWorld },
              "every handler takes its operation's request class, no operation twice; GetGameServerByCluster on the login connection; HarvestStart/HarvestCancel, "
              + "AttackStart (quiet), CastStart, CastCancel, RegisterToObject, InventoryMoveGivenItems, DeathRecap and TakeSilver in the world");
        try { OpcodeTable.For(typeof(MisboundSession)); Check(false, "a handler on the wrong request class was accepted"); }
        catch (InvalidOperationException e)
        {
            Check(e.Message.Contains("MisboundSession.Move") && e.Message.Contains(nameof(MoveRequest)), $"the error names the handler and its class (got {e.Message})");
        }
        try { OpcodeTable.For(typeof(DuplicateSession)); Check(false, "two handlers for one operation were accepted"); }
        catch (InvalidOperationException e) { Check(e.Message.Contains("two handlers"), $"the error names the duplicate (got {e.Message})"); }

        var dir = NewDir();
        var peer = new FakePeer();
        var characters = Repository(dir);
        var session = new LoginSession(peer, new ServerConfig(), characters, StarterKit.Default);
        var select = Request(new SelectCharacterRequest { abc = "LocalHero" }, null);
        session.OnRequest(select, encrypted: false);
        Check(peer.Responses.Count == 0 && session.Character is null, "SelectCharacter before Login is dropped");
        var before = login.Unhandled.GetValueOrDefault(OperationCodes.GetSurveyLogs);
        session.OnRequest(Request(new GetSurveyLogsRequest(), 4), encrypted: false);
        Check(peer.Responses is [{ ReturnCode: 0 } r] && r.Parameters[ReservedKeys.RequestId] is 4
              && r.Parameters[ReservedKeys.OperationCode] is (short)OperationCodes.GetSurveyLogs
              && login.Unhandled[OperationCodes.GetSurveyLogs] == before + 1,
              "an unhandled operation gets an empty response and is counted");
        session.OnRequest(Request(TestLogin(), 5), encrypted: false);
        session.OnRequest(select, encrypted: false);
        Check(session.Status == SessionStatus.Authed && peer.Responses.Count == 3 && session.Character?.Name == "LocalHero", "after Login it is served");
        characters.Dispose();
        try { Directory.Delete(dir, true); } catch (IOException) { }
    }

    /// <summary>Move bound to Join's request class: the table must refuse it.</summary>
    private sealed class MisboundSession(IPeer peer) : AlbionSession(peer, "bad")
    {
        [Handles(OperationCodes.Move)]
        private AlbionResponse? Move(JoinRequest req) => null;
    }

    /// <summary>Two handlers for Move: the table must refuse the second.</summary>
    private sealed class DuplicateSession(IPeer peer) : AlbionSession(peer, "dup")
    {
        [Handles(OperationCodes.Move)]
        private AlbionResponse? A(MoveRequest req) => null;

        [Handles(OperationCodes.Move)]
        private AlbionResponse? B(MoveRequest req) => null;
    }

    /// <summary>
    /// content/opcode-stubs.json loaded as ServerHost loads it (checked against both handler tables); on a session
    /// without handlers BuyPremium gets its return code (GetArenaRankings is a handler since stageP P7, CrystalArenaTest); a
    /// silent operation (QueryAllianceGuildStats) gets nothing, an ack one the bare {253, 255?} with RC 0; then the mistakes that stop
    /// the start.
    /// </summary>
    private static void OpcodeStubsContent()
    {
        OpcodeTable.Build(typeof(LoginSession), typeof(WorldSession), typeof(ChatSession));
        static string? HandlerOf(OperationCodes code) =>
            (OpcodeTable.For(typeof(LoginSession)).Find(code) ?? OpcodeTable.For(typeof(WorldSession)).Find(code) ?? OpcodeTable.For(typeof(ChatSession)).Find(code))?.Method;
        var stubs = OpcodeStubs.Load(Path.Combine(DataDir(), "..", "content", "opcode-stubs.json"), HandlerOf);
        Check(stubs.KindOf(OperationCodes.GetArenaRankings) is null && stubs.KindOf(OperationCodes.BuyPremium) == OpcodeStubs.StubKind.Response
              && stubs.KindOf(OperationCodes.QueryAllianceGuildStats) == OpcodeStubs.StubKind.Silent
              && stubs.KindOf(OperationCodes.CastStart) is null && stubs.KindOf(OperationCodes.RegisterToObject) is null
              && stubs.KindOf(OperationCodes.GetCharacterStats) == OpcodeStubs.StubKind.Ack && stubs.KindOf(OperationCodes.Move) is null,
              "the content file loads: filled, silent and ack entries");

        var peer = new FakePeer();
        var session = new BareSession(peer) { Stubs = stubs };
        var table = OpcodeTable.For(typeof(BareSession));
        session.OnRequest(Request(new BuyPremiumRequest(), 13), encrypted: false);
        Check(peer.Responses is [{ ReturnCode: 10522 } buy] && buy.Parameters[ReservedKeys.RequestId] is 13
              && table.Stubbed.GetValueOrDefault(OperationCodes.BuyPremium) == 1 && !table.Unhandled.ContainsKey(OperationCodes.BuyPremium),
              "BuyPremium: 10522, the offer is no longer available; counted as stubbed, not as unhandled");
        // Silent: no answer to an operation with a response class (the unhandled default would send its empty response).
        var silent = new OperationRequest(AlbionSerializer.PhotonOperationCode, new() { [ReservedKeys.OperationCode] = (short)OperationCodes.QueryAllianceGuildStats });
        session.OnRequest(silent, encrypted: false);
        Check(peer.Responses.Count == 1 && table.Stubbed.GetValueOrDefault(OperationCodes.QueryAllianceGuildStats) == 1 && !table.Unhandled.ContainsKey(OperationCodes.QueryAllianceGuildStats),
              "QueryAllianceGuildStats: no answer at all, counted as stubbed");
        // Ack: the bare rc-0 answer of an operation without a response class, with the request id or, as official for
        // PartyFinderUnregisterApplicants, without it.
        OperationRequest Bare(OperationCodes code, int? id) => new(AlbionSerializer.PhotonOperationCode,
            id is { } rid ? new() { [ReservedKeys.OperationCode] = (short)code, [ReservedKeys.RequestId] = rid } : new() { [ReservedKeys.OperationCode] = (short)code });
        session.OnRequest(Bare(OperationCodes.GetCharacterStats, 20), encrypted: false);
        session.OnRequest(Bare(OperationCodes.PartyFinderUnregisterApplicants, 21), encrypted: false);
        Check(peer.Responses is [_, { ReturnCode: 0 } stats, { ReturnCode: 0 } unregister]
              && stats.Parameters.Keys.Order().SequenceEqual(new[] { ReservedKeys.OperationCode, ReservedKeys.RequestId }) && stats.Parameters[ReservedKeys.RequestId] is 20
              && stats.Parameters[ReservedKeys.OperationCode] is (short)OperationCodes.GetCharacterStats
              && unregister.Parameters.Keys.SequenceEqual(new[] { ReservedKeys.OperationCode }) && stats.DebugMessage is null,
              "ack: GetCharacterStats gets exactly {253, 255: 20} with RC 0, PartyFinderUnregisterApplicants {253} only");

        void Refused(string text, params string[] words)
        {
            try
            {
                OpcodeStubs.Parse(text, "test.json", HandlerOf);
                Check(false, $"accepted: {text}");
            }
            catch (InvalidDataException e)
            {
                Check(e.Message.StartsWith("test.json: ") && words.All(e.Message.Contains), $"{text}: the error names {string.Join(", ", words)} (got {e.Message})");
            }
        }
        Refused("""{ "GetArenaRanking": {} }""", "unknown operation 'GetArenaRanking' (closest: GetArenaRankings)");
        Refused("""{ "GetCrystalLeagueStatistics": { "Season": 1 } }""", "no field 'Season'", "0 bnx");
        Refused("""{ "BuyPremium": { "ReturnCode": 40000 }, "GetCrystalLeagueStatistics": { "bnx": [1, "x"] } }""", "2 problem(s)", "-32768..32767", "bnx (key 0, int[]): [1]: expected an integer");
        Refused("""{ "GetArenaRankings": {} }""", "GetArenaRankings is handled by WorldSession."); // stageP P7
        Refused("""{ "BuyPremium": { "ReturnCode": 40000 } }""", "BuyPremium.ReturnCode (short): expected an integer -32768..32767");
        Refused("""{ "Move": {} }""", "Move is handled by WorldSession.");
        Refused("""{ "AcceptPopups": {} }""", "no response class"); // stageM: ReportClientError has its handler now (logged at Info)
        Refused("""{ "AcceptPopups": "silent", "QueryAllianceGuildStats": "ack", "GetCharacterStats": "loud" }""", "3 problem(s)",
            "AcceptPopups has no response class", "QueryAllianceGuildStats has the response class QueryAllianceGuildStatsResponse", "unknown kind \"loud\"");
        Refused("""{ "CastStart": "silent" }""", "CastStart is handled by WorldSession.");
        Refused("""{ "Move": "silent" }""", "Move is handled by WorldSession.");
    }

    /// <summary>A session without handlers: every operation takes the unhandled path (a stub or the empty default).</summary>
    private sealed class BareSession(IPeer peer) : AlbionSession(peer, "bare");

    /// <summary>
    /// The side requests answered in code instead of opcode-stubs.json: GetReferralSeasonDetails after Login (the season
    /// end, the next month's first second), the status query (online), GetRankings in the world (keys 0-4 echo the guild
    /// tab asked for, no rows) and GetMailInfos (the character's empty mailbox, keys 1/2 echoed).
    /// </summary>
    private static void SideRequests()
    {
        static DateTime End(DateTime now) => new(LoginSession.ReferralSeasonEnd(now), DateTimeKind.Utc);
        Check(End(new DateTime(2026, 10, 15, 20, 43, 0, DateTimeKind.Utc)) == new DateTime(2026, 11, 1, 0, 0, 1, DateTimeKind.Utc)
              && End(new DateTime(2026, 12, 31, 23, 59, 59, DateTimeKind.Utc)) == new DateTime(2027, 1, 1, 0, 0, 1, DateTimeKind.Utc),
              "referral season end: the next month's first second (official on 2026-10-15: 2026-11-01 00:00:01)");
        var dir = NewDir();
        var characters = Repository(dir);
        var loginPeer = new FakePeer();
        var login = new LoginSession(loginPeer, new ServerConfig(), characters, StarterKit.Default);
        login.OnRequest(Request(TestLogin(), 1), encrypted: false);
        login.OnRequest(Request(new GetReferralSeasonDetailsRequest(), 2), encrypted: false);
        Check(loginPeer.Responses is [_, { ReturnCode: 0 } referral] && referral.Parameters[2] is long end && end > DateTime.UtcNow.Ticks
              && referral.Parameters[0] is int[] { Length: 0 } && referral.Parameters[1] is string[] { Length: 0 },
              "GetReferralSeasonDetails: no rewards, a season end ahead");
        // The status peers' empty request (op 1 without key 253): event 1 with key 1 = byte 3, which the server selection
        // shows as online (1 greyed the region out as in maintenance).
        login.OnRequest(new OperationRequest(AlbionSerializer.PhotonOperationCode, new()), encrypted: false);
        Check(loginPeer.Events is [.., var status] && status.Code == AlbionSerializer.PhotonEventCode && status.Parameters.Count == 2
              && status.Parameters[1] is (byte)3 && status.Parameters[ReservedKeys.EventCode] is (short)1 && loginPeer.Responses.Count == 2,
              "status query: event 1 {1: byte 3 (online), 252: short 1}, no response, as the official status servers");

        var hero = characters.List()[0];
        hero.Equipment = new InventoryItem?[hero.Equipment.Length];
        hero.Inventory = [];
        characters.Save(hero);
        var world = new World(new ServerConfig { JoinByCharacterId = true }, characters, Items()) { Sync = new object() };
        var peer = new FakePeer();
        var session = new WorldSession(peer, world);
        session.OnRequest(Request(new JoinRequest { ss = hero.Id }, 1), encrypted: false);
        // The guild tab as the client asked for it on 2026-10-15 23:09:29 (entity type 1, rows 0-9, no request id), here
        // with this week's list so that the time frame is echoed too.
        session.OnRequest(Request(new GetRankingsRequest { op = 0, oq = "", or = 1, os = 1, ot = 0, ou = 0, ov = 10 }, null), encrypted: false);
        var rankings = new GetRankingsResponse();
        AlbionSerializer.FillFields(rankings, peer.Responses[^1].Parameters);
        Check(peer.Responses is [{ ReturnCode: 0 }, { ReturnCode: 0 }] && rankings is { a3t: 0, a3u: "", a3v: 1, a3w: 1, a3x: 0, a3z: 0, a30: 0, a31: 0 }
              && rankings.a4f is { Length: 0 } && rankings.a34 is { Length: 0 },
              "GetRankings: ranking, filter, time frame, entity type and character filter echoed, no rows");
        // The mailbox as the client asked on 2026-10-16 (only key 1, the mailbox kind, as ByteZero; no request id), and a
        // page with keys 1/2 set: ddp.m takes the answer only with key 0 = the character Guid, keys 1/2 echoed, key 3 an array.
        session.OnRequest(Request(new GetMailInfosRequest(), null), encrypted: false);
        session.OnRequest(Request(new GetMailInfosRequest { n2 = 1, n3 = 2 }, null), encrypted: false);
        var mail = peer.Responses[^2];
        var page = new GetMailInfosResponse();
        AlbionSerializer.FillFields(page, peer.Responses[^1].Parameters);
        Check(mail is { ReturnCode: 0 } && mail.Parameters[0] is byte[] { Length: 16 } owner && new Guid(owner) == hero.Id
              && !mail.Parameters.ContainsKey(ReservedKeys.RequestId) && !mail.Parameters.ContainsKey(1) && !mail.Parameters.ContainsKey(2)
              && mail.Parameters[3] is long[] { Length: 0 } && mail.Parameters[7] is string[] { Length: 0 } && mail.Parameters[8] is byte[] { Length: 0 }
              && page is { a0j: 2, a0k: 1 } && page.a0i == hero.Id && page.a0l is { Length: 0 },
              "GetMailInfos: key 0 the character Guid, keys 1/2 = request keys 2/1, empty arrays (key 3 non-null), no request id");
        world.Dispose();
        characters.Dispose();
        try { Directory.Delete(dir, true); } catch (IOException) { }
    }

    /// <summary>A game session driven directly through <see cref="FakePeer"/> (no UDP), two connections on one World.</summary>
    private static void WorldSessionFakePeer()
    {
        var dir = NewDir();
        var characters = Repository(dir);
        var hero = characters.List()[0];
        hero.Equipment = [new("T4_MAIN_SWORD"), null, null, null, null, null, null, null, null, null];
        hero.Inventory = [new("T4_ORE", 10)];
        characters.Save(hero);
        var world = new World(new ServerConfig { JoinByCharacterId = true }, characters, Items("8610\tT4_MAIN_SWORD\tweapon\tmainhand\t4\t13500", "1000\tT4_ORE\tsimpleitem\t\t4")) { Sync = new object() };
        JoinResponse Join(FakePeer peer)
        {
            new WorldSession(peer, world).OnRequest(Request(new JoinRequest { ss = hero.Id }, 1), encrypted: false);
            Check(peer.Responses is [{ ReturnCode: 0 }], "one join response");
            var jr = new JoinResponse();
            AlbionSerializer.FillFields(jr, peer.Responses[0].Parameters);
            return jr;
        }
        var first = new FakePeer();
        var jr = Join(first);
        int Code(EventData e) => Convert.ToInt32(e.Parameters.GetValueOrDefault(ReservedKeys.EventCode));
        Check(jr.a8b == hero.Id && first.Events.Select(Code).ToArray() is
                  [(int)EventCodes.NewEquipmentItem, (int)EventCodes.NewSimpleItem, (int)EventCodes.UpdateChatSettings, (int)EventCodes.MountCooldownUpdate,
                   (int)EventCodes.JoinFinished, (int)EventCodes.NewUnreadMails, (int)EventCodes.MailOperationPossible, (int)EventCodes.MutePlayerUpdate,
                   (int)EventCodes.FriendRequestInfos, (int)EventCodes.FriendRequestInfos, (int)EventCodes.FriendInfos]
              && jr.a9q?[0] == Convert.ToInt64(first.Events[0].Parameters[0]) && jr.a9t?[0] == Convert.ToInt64(first.Events[1].Parameters[0]),
              "join returns the character, its items are created and listed in the containers; after JoinFinished the mailbox's 201/202 and the login's mute and friends lists (stageJ)");
        var second = Join(new FakePeer());
        Check(new[] { jr.a8a, jr.a9q![0], jr.a9t![0], second.a8a }.Distinct().Count() == 4, "a second connection gets new object ids");
        world.Dispose();
        characters.Dispose();
        try { Directory.Delete(dir, true); } catch (IOException) { }
    }

    private static void Logout()
    {
        var dir = NewDir();
        var host = Host(dir, spells: false, logoutTime: TimeSpan.FromMilliseconds(300));
        var hero = host.Characters.List()[0];
        TestPhotonClient Enter()
        {
            var client = new TestPhotonClient(host.GameServer.EndPoint);
            client.Connect();
            Check(client.EncryptionDone.Task.Wait(10000), "game encryption");
            Check(Call(client, new JoinRequest { ss = hero.Id }, 1).ReturnCode == 0, "join");
            return client;
        }
        var first = Enter();
        first.SendOperation(Request(new LogoutStartRequest(), null), encrypt: false);
        first.SendOperation(Request(new LogoutCancelRequest(), null), encrypt: false);
        Check(!first.Closed.Task.Wait(1000), "a cancelled logout keeps the session");
        // Walking fast when the logout starts: the client stops the player there, the server must too.
        first.SendOperation(Request(new MoveRequest { v6 = [5f, 0f], v8 = [100f, 0f], v9 = 50f }, null));
        first.SendOperation(Request(new LogoutStartRequest(), null), encrypt: false);
        Check(first.Closed.Task.Wait(3000) && first.Closed.Task.Result == "remote disconnect (1)",
              $"after the countdown the server disconnects with DisconnectByServerLogic (got {(first.Closed.Task.IsCompleted ? first.Closed.Task.Result : "nothing")})");
        Character? saved = null;
        Check(SpinWait.SpinUntil(() => (saved = host.Characters.Find(hero.Id)) is { PositionX: >= 5 and < 15 }, 2000),
              $"saved where the logout started (got {saved?.PositionX})");
        Check(!first.Responses.TryTake(out _, 0), "no response to LogoutStart / LogoutCancel");
        first.Dispose();
        Enter().Dispose(); // the next login joins as usual
        Cleanup(host, dir);
    }

    internal static ServerConfig LoadTestConfig(string path, IEnumerable<string> args) =>
        AlbionLocal.Server.Configuration.EditableConfiguration.Parse(File.Exists(path) ? File.ReadAllText(path) : "{}")
            .Validate(Environment.CurrentDirectory, args);

    private static void ConfigLoad()
    {
        var dir = NewDir();
        var path = Path.Combine(dir, "albionlocal.json");
        var none = LoadTestConfig(path, []);
        Check(none.LoginEndPoint.Port == 5055 && none.GameEndPoint.Port == 5056 && none.LogoutTime == TimeSpan.FromSeconds(10)
              && none.SavesPath == new ServerConfig().SavesPath
              && !none.JoinByCharacterId
              && none.ClientGameDataPath == Path.GetFullPath(Path.Combine("..", "game-local", "Albion-Online_Data", "StreamingAssets", "GameData"))
              && none.StarterPath == Path.Combine(Environment.CurrentDirectory, "content", "starter.json"), "no file: today's defaults");
        var shipped = ServerConfig.Load(Path.Combine(DataDir(), "..", "config"), []);
        Check(shipped.Preset == ServerPreset.Official && shipped.TutorialOpening && shipped.Rates == new AlbionLocal.Server.Game.Rates(), "shipped area configuration: official, tutorial enabled, rates one");
        var realItems = Client.Items;
        var shippedKit = StarterKit.Load(Path.Combine(DataDir(), "..", "content", "starter.json"), realItems);
        Check(shippedKit is { Equipment: [null, null, null, null, null, { Name: "T2_BAG" }, { Name: "T2_CAPE" }, { Name: "T2_MOUNT_MULE" }, ..], Inventory.Count: 5 }
              && shippedKit.Inventory.All(i => i is { Name: ['T', '1', '_', ..] }), "content/starter.json: the T2 bag, cape and mule, the five T1 tools, all items known");
        var starter = Path.Combine(dir, "starter.json");
        File.WriteAllText(starter, """{ "Equipment": { "mainhand": "T4_MAIN_SWROD", "head": "T4_ORE", "cape": "T4_CAPE" }, "Inventory": ["T4_ORE", "NO_SUCH"] }""");
        Check(StarterKit.Load(starter, realItems) is { Equipment: [null, null, null, null, null, null, { Name: "T4_CAPE" }, ..], Inventory: [{ Name: "T4_ORE" }, null] }
              && realItems.Closest("T4_MAIN_SWROD") == "T4_MAIN_SWORD", "starter.json: unknown items and items in the wrong slot are skipped");
        File.WriteAllText(path, """
            // comment
            { "Network": { "GamePort": 6000 }, "Paths": { "Saves": "saves" }, "Log": { "Level": "warn" }, }
            """);
        var c = LoadTestConfig(path, ["-v", "--Network:GamePort=6001", "--World:LogoutSeconds=5", "--Log:File=", "--Accounts:LocalAccountLevel=gamemaster"]);
        Check(c.GameEndPoint.Port == 6001 && c.LoginEndPoint.Port == 5055 && c.LogoutTime == TimeSpan.FromSeconds(5) && c.LogLevel == LogLevel.Warn
              && c.DatabasePath == Path.Combine(Environment.CurrentDirectory, "saves", "albionlocal.db"), "file values, command line wins");
        Check(none.LogFile == Path.Combine(Environment.CurrentDirectory, "data", "server.log") && none.LocalAccountLevel == AccountLevel.Admin
              && c.LogFile is null && c.LocalAccountLevel == AccountLevel.GameMaster, "Log:File (\"\" = none) and Accounts:LocalAccountLevel");
        var lan = LoadTestConfig(path, ["--Network:BindAddress=0.0.0.0", "--Network:PublicAddress=192.168.1.10"]);
        Check(none.GameServerAddress == "127.0.0.1:5056" && lan.LoginEndPoint.Address.Equals(IPAddress.Any) && lan.GameServerAddress == "192.168.1.10:6000",
              "Network:BindAddress for the listeners, Network:PublicAddress given to the clients (default: the bind address)");
        try { LoadTestConfig(path, ["--Network:BindAddress=0.0.0.0"]); Check(false, "0.0.0.0 without a public address accepted"); }
        catch (InvalidDataException e) { Check(e.Message.Contains("PublicAddress"), "binding every interface needs Network:PublicAddress"); }
        Check(none.ChatEndPoint.Port == 5057 && none.ChatEnabled && lan.ChatServerAddress == "192.168.1.10:5057", "Network:ChatPort 5057, the chat address as the game's");
        try { LoadTestConfig(path, ["--Network:ChatPort=6000"]); Check(false, "the chat port on the game port accepted"); }
        catch (InvalidDataException e) { Check(e.Message.Contains("Network:ChatPort"), "two listeners on one port stop the start"); }
        var logFile = Path.Combine(dir, "logs", "server.log");
        var console = Console.Out;
        Console.SetOut(TextWriter.Null); // the line below is for the file only
        try
        {
            Log.SetFile(logFile);
            Log.Error("selftest", "into the log file");
        }
        finally
        {
            Log.SetFile(null);
            Console.SetOut(console);
        }
        Check(File.ReadAllText(logFile).Contains("Error [selftest] into the log file"), "log lines are appended to the log file");
        // World:StartCluster is gone: the start cluster is in content/starter.json now.
        try { LoadTestConfig(path, ["--Network:GamPort=1", "--World:StartCluster=1000"]); Check(false, "unknown key accepted"); }
        catch (InvalidDataException e) { Check(e.Message.Contains("GamPort") && e.Message.Contains("StartCluster"), "unknown keys named in the error"); }
        try { Directory.Delete(dir, true); } catch (IOException) { }
    }

    /// <summary>Item spells against an in-process game server with the real data (starter kit, everything unlocked).</summary>
    private static void SpellSelection()
    {
        var (catalog, _) = RealData();
        var spells = Client.Spells!;
        Check(spells.Count == 9326 && spells.IdOf("FOOD_HEALTH_REG_P1") == 4191 && spells.ConsumeSpell("T1_MEAL_SOUP@1") == spells.IdOf("FOOD_HEALTH_REG_P2"),
              "spell ids follow the client numbering (channeling children included)");
        var old = System.Text.Json.JsonSerializer.Deserialize<Character>("""{"Name":"Old","Equipment":["T4_MAIN_SWORD",null]}""")!;
        Check(old.Equipment is [{ Name: "T4_MAIN_SWORD", Count: 1, Active: null }, null], "old saves with bare item names still load");

        var dir = NewDir();
        using var host = Host(dir);
        var hero = host.Characters.List()[0];
        var server = host.GameServer;
        int Code(Dictionary<byte, object?> p) => Convert.ToInt32(p.GetValueOrDefault(ReservedKeys.EventCode));
        short[] Shorts(Dictionary<byte, object?> p, byte key) => p.GetValueOrDefault(key) as short[] ?? [];
        var sword = catalog.Find("T4_MAIN_SWORD")!.Index;
        short heroic = spells.IdOf("HEROICSTRIKE2"), mighty = spells.IdOf("MIGHTYBLOW"), spin = spells.IdOf("SWORD_SPIN");

        (TestPhotonClient Client, Dictionary<byte, object?> Sword) Enter()
        {
            var client = new TestPhotonClient(server.EndPoint);
            client.Connect();
            Check(client.EncryptionDone.Task.Wait(10000), "game encryption");
            Check(Call(client, new JoinRequest { ss = hero.Id }, 1).ReturnCode == 0, "join");
            var items = new List<Dictionary<byte, object?>>();
            while (client.Events.TryTake(out var e, 5000) && Code(e.Parameters) != (int)EventCodes.JoinFinished) items.Add(e.Parameters);
            SkipLoginLists(client);
            var s = items.Single(p => Code(p) == (int)EventCodes.NewEquipmentItem && Convert.ToInt32(p[1]) == sword);
            var bag = items.Single(p => Code(p) == (int)EventCodes.NewEquipmentItem && Convert.ToInt32(p[1]) == catalog.Find("T4_BAG")!.Index);
            Check(Shorts(bag, 9).Length == 0 && Shorts(bag, 10) is [0], "bag: no active slot, the only passive (PASSIVE_MAXLOAD = 0) preselected");
            return (client, s);
        }

        var (first, s1) = Enter();
        Check(Shorts(s1, 9) is [-1, -1, var e] && e == mighty && Shorts(s1, 10) is [-1],
              $"new sword: only the single-choice slot is preselected (got [{string.Join(",", Shorts(s1, 9))}])");
        var swordObj = Convert.ToInt64(s1[0]);

        first.SendOperation(Request(new EquipmentItemChangeSpellRequest { kc = swordObj, kd = true, ke = 0, kf = heroic }, null));
        Check(first.Events.TryTake(out var re, 5000) && Code(re.Parameters) == (int)EventCodes.NewEquipmentItem
              && Shorts(re.Parameters, 9) is [var q, -1, var e2] && q == heroic && e2 == mighty
              && Convert.ToInt64(re.Parameters[8]) == catalog.Find("T4_MAIN_SWORD")!.FullDurabilityRaw,
              "change: the full item is re-sent with the new choice");
        Check(first.Events.TryTake(out var ch, 5000) && Code(ch.Parameters) == (int)EventCodes.CharacterEquipmentChanged
              && ch.Parameters.GetValueOrDefault((byte)9) is true && ch.Parameters[7] is int[] { Length: 14 } k7 && k7[0] == heroic && k7[2] == mighty,
              "change of an equipped item: CharacterEquipmentChanged with key 9 and the new spell slots");
        Check(!first.Responses.TryTake(out _, 300), "no response to EquipmentItemChangeSpell");
        // Through the real path: the world loop writes the change SaveScheduler.ChangeDelay (2 s) later.
        Check(SpinWait.SpinUntil(() => host.Characters.Find(hero.Id)!.Equipment[0]?.Active is ["HEROICSTRIKE2", null, "MIGHTYBLOW"], 5000),
              "choice saved by name");

        first.SendOperation(Request(new EquipmentItemChangeSpellRequest { kc = swordObj, kd = true, ke = 0, kf = spin }, null));
        Check(first.Events.TryTake(out var rej, 5000) && Code(rej.Parameters) == (int)EventCodes.NewEquipmentItem
              && Shorts(rej.Parameters, 9) is [var r0, _, _] && r0 == heroic, "a spell from another slot is rejected and the item re-sent unchanged");
        Check(first.Events.TryTake(out var rch, 5000) && Code(rch.Parameters) == (int)EventCodes.CharacterEquipmentChanged
              && !rch.Parameters.ContainsKey(9), "rejection: CharacterEquipmentChanged without key 9 (no spell cooldown)");
        first.Dispose();

        var (second, s2) = Enter();
        Check(Shorts(s2, 9) is [var a0, -1, var a2] && a0 == heroic && a2 == mighty, "the choice survives a rejoin");
        second.Dispose();
        Cleanup(host, dir);
    }

    /// <summary>
    /// A character with its own progression: defaults from the unlocked-by-default spells, choices of locked
    /// spells trusted (the client checks unlocks itself); an explicit full progression acts as unlock-all.
    /// Saved spell names survive a server without spell data and names the spell table does not know.
    /// </summary>
    private static void SpellsOwnProgression()
    {
        var (catalog, data) = RealData();
        var spells = Client.Spells!;
        var dir = NewDir();
        int Code(Dictionary<byte, object?> p) => Convert.ToInt32(p.GetValueOrDefault(ReservedKeys.EventCode));
        short[] Shorts(Dictionary<byte, object?> p, byte key) => p.GetValueOrDefault(key) as short[] ?? [];
        var sword = catalog.Find("T4_MAIN_SWORD")!.Index;
        short spin = spells.IdOf("SWORD_SPIN"), mighty = spells.IdOf("MIGHTYBLOW"), interrupt = spells.IdOf("INTERRUPT2");
        Check(spells.IsLocked(interrupt) && !spells.IsLocked(spin), "INTERRUPT2 needs a destiny board unlock, SWORD_SPIN does not");

        // One server per case on the same save: each case starts from what the previous server saved.
        // body gets the saved character on demand (pending saves forced out first).
        void Run(bool withSpells, Action<Character> setup, Action<TestPhotonClient, Dictionary<byte, object?>, Character, Func<Character>> body)
        {
            using var host = Host(dir, spells: withSpells);
            var hero = host.Characters.List()[0];
            setup(hero);
            host.Characters.Save(hero);
            using var client = new TestPhotonClient(host.GameServer.EndPoint);
            client.Connect();
            Check(client.EncryptionDone.Task.Wait(10000), "game encryption");
            Check(Call(client, new JoinRequest { ss = hero.Id }, 1).ReturnCode == 0, "join");
            var items = new List<Dictionary<byte, object?>>();
            while (client.Events.TryTake(out var e, 5000) && Code(e.Parameters) != (int)EventCodes.JoinFinished) items.Add(e.Parameters);
            SkipLoginLists(client);
            body(client, items.Single(p => Code(p) == (int)EventCodes.NewEquipmentItem && Convert.ToInt32(p[1]) == sword), hero, () => Saved(host, hero.Id));
        }

        // Fair start: only unlocked-by-default candidates count for the preselection; a locked choice is trusted.
        Run(true, hero => hero.Progression = new Progression(), (client, s, _, saved) =>
        {
            Check(Shorts(s, 9) is [-1, var a1, var a2] && a1 == spin && a2 == mighty && Shorts(s, 10) is [88],
                  $"fair start: single usable candidates preselected (got [{string.Join(",", Shorts(s, 9))}] / [{string.Join(",", Shorts(s, 10))}])");
            client.SendOperation(Request(new EquipmentItemChangeSpellRequest { kc = Convert.ToInt64(s[0]), kd = true, ke = 1, kf = interrupt }, null));
            Check(client.Events.TryTake(out var re, 5000) && Code(re.Parameters) == (int)EventCodes.NewEquipmentItem
                  && Shorts(re.Parameters, 9) is [_, var c1, _] && c1 == interrupt, "own progression: a locked spell choice is accepted");
            Check(client.Events.TryTake(out var ch, 5000) && ch.Parameters.GetValueOrDefault((byte)9) is true, "and starts the spell cooldown");
            Check(saved().Equipment[0]?.Active is [null, "INTERRUPT2", "MIGHTYBLOW"], "saved");
        });

        // An explicit progression holding everything is unlock-all, not a fair start.
        Run(true, hero => { hero.Progression = Progression.UnlockAll(data); hero.Equipment[0] = new InventoryItem("T4_MAIN_SWORD"); },
            (_, s, _, _) => Check(Shorts(s, 9) is [-1, -1, var a2] && a2 == mighty && Shorts(s, 10) is [-1], "saved full progression: unlock-all defaults"));

        // No spell data: the saved names are not sent but written back unchanged after a move.
        Run(false, hero =>
        {
            hero.Progression = null;
            hero.Equipment[0] = new InventoryItem("T4_MAIN_SWORD") { Active = ["HEROICSTRIKE2", "SWORD_SPIN", "MIGHTYBLOW"], Passive = ["PASSIVE_BLEEDCHANCE"] };
        }, (client, s, hero, saved) =>
        {
            Check(Shorts(s, 9).Length == 0, "no spell data: no spells sent");
            client.SendOperation(Request(new InventoryMoveItemRequest { ru = 6, rv = hero.EquipmentContainer, rx = 5, ry = hero.InventoryContainer, rz = 1 }, null));
            Check(SpinWait.SpinUntil(() => saved().Equipment[6] is null, 5000), "cape moved and saved");
            Check(saved().Equipment[0] is { Active: ["HEROICSTRIKE2", "SWORD_SPIN", "MIGHTYBLOW"], Passive: ["PASSIVE_BLEEDCHANCE"] },
                  "no spell data: saved choices kept");
        });

        // A name the spell table does not know goes out as -1 and stays in the save.
        Run(true, hero => hero.Equipment[0] = new InventoryItem("T4_MAIN_SWORD") { Active = ["NO_SUCH_SPELL", null, "MIGHTYBLOW"] }, (client, s, hero, saved) =>
        {
            Check(Shorts(s, 9) is [-1, -1, var a2] && a2 == mighty, "unknown saved name: -1 on the wire");
            client.SendOperation(Request(new InventoryMoveItemRequest { ru = 5, rv = hero.InventoryContainer, rx = 6, ry = hero.EquipmentContainer, rz = 1 }, null));
            Check(SpinWait.SpinUntil(() => saved().Equipment[6] is not null, 5000), "cape back on (needs the previous server's save)");
            Check(saved().Equipment[0]?.Active is ["NO_SUCH_SPELL", null, "MIGHTYBLOW"], "unknown saved name kept");
        });
        try { Directory.Delete(dir, true); } catch (IOException) { }
    }

    /// <summary>A fresh temporary save directory.</summary>
    internal static string NewDir()
    {
        var dir = Path.Combine(Path.GetTempPath(), "albion-selftest-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        return dir;
    }

    /// <summary>A repository in <paramref name="dir"/> holding an explicit legacy character fixture.</summary>
    internal static SqliteCharacterRepository Repository(string dir)
    {
        var characters = new SqliteCharacterRepository(Path.Combine(dir, "albionlocal.db"));
        characters.Create(StarterKit.Default.NewCharacter("LocalHero"));
        return characters;
    }

    /// <summary>The character as saved, once the pending saves are out (what the change delay or a disconnect would do).</summary>
    private static Character Saved(ServerHost host, Guid id)
    {
        lock (host.Sync) host.World.Saves.SaveAll();
        host.World.Saves.Flush();
        return host.Characters.Find(id)!;
    }

    /// <summary>
    /// The server as Program builds it (<see cref="ServerHost"/>), on free loopback ports, saving into
    /// <paramref name="dir"/>, with the real client data (<see cref="Client"/>, loaded once) and content unless a test
    /// turns them off; <paramref name="items"/> = a hand-made item table without game data and spells instead. Without the
    /// cluster data unless <paramref name="clusters"/>: these tests count the events of a Join or a move, which the world
    /// objects of the start cluster would join at a time set by the background load (WorldObjectTest covers them).
    /// </summary>
    internal static ServerHost Host(string dir, bool spells = true, bool gameData = true, ItemStore? items = null, TimeSpan? logoutTime = null, bool clusters = false)
    {
        var host = new ServerHost(new ServerConfig
        {
            LoginEndPoint = new IPEndPoint(IPAddress.Loopback, 0),
            GameEndPoint = new IPEndPoint(IPAddress.Loopback, 0),
            ChatEndPoint = new IPEndPoint(IPAddress.Loopback, 0),
            SavesPath = dir,
            ContentPath = Path.Combine(DataDir(), "..", "content"),
            StarterPath = Path.Combine(dir, "no-starter.json"), // the built-in T4 kit the tests are written for, not the shipped one
            ClientGameDataPath = Path.Combine(dir, "no-client-data"), // never read: the client data is handed in
            LogoutTime = logoutTime ?? TimeSpan.FromSeconds(10),
            JoinByCharacterId = true, // the tests' Joins name the character
        }, items is not null ? new ClientData(items, null, null)
            : Client with { GameData = gameData ? Client.GameData : null, Spells = spells ? Client.Spells : null, Clusters = clusters ? Client.Clusters : null });
        // Gameplay tests supply their own legacy fixture; production startup no longer creates characters.
        if (host.Characters.List().Count == 0)
        {
            var hero = host.Starter.NewCharacter("LocalHero");
            AlbionLocal.Server.Systems.Focus.NewCharacter(hero, host.Config);
            host.Characters.Create(hero);
        }
        host.Start();
        return host;
    }

    private static readonly Lazy<(ClientData Data, TimeSpan Time)> LoadedClient = new(() =>
    {
        var watch = Stopwatch.StartNew();
        // The open-world layout of a fixed day (stage 4b): the Login key 57 of capture 103829 (2026-10-14), so that the
        // dynamic templates and resource nodes do not change with the day the tests run.
        var data = ClientData.Load(ClientGameDataDir(), new ClusterOptions { Session = new LayoutSession(Capture103829Key57, [], true) });
        return (data, watch.Elapsed);
    });

    /// <summary>Login key 57 of capture 103829 (read from its unencrypted Login): 2026-10-14 UTC, also the day of 114814.</summary>
    internal const long Capture103829Key57 = 639275698255601287;

    /// <summary>Login key 57 of capture 002835 (derived from key 251, stage 4b SPEC §6): 2026-10-15 UTC.</summary>
    internal const long Capture002835Key57 = 639276555548388128;

    /// <summary>The client's data as the server reads it at startup (../game-local), loaded once for all tests (the layout of 2026-10-14).</summary>
    internal static ClientData Client => LoadedClient.Value.Data;

    /// <summary>How long <see cref="Client"/> took to load.</summary>
    internal static TimeSpan ClientLoadTime => LoadedClient.Value.Time;

    /// <summary>The default Paths:ClientGameData taken from the server folder (the parent of <see cref="DataDir"/>).</summary>
    internal static string ClientGameDataDir() => ClientGameDataOverride
        ?? Path.GetFullPath(ServerConfig.DefaultClientGameData, Path.Combine(DataDir(), ".."));

    /// <summary>A hand-made item table from rows in the data/items.tsv layout (index, uniquename, kind, slot, tier, durability).</summary>
    internal static ItemStore Items(params string[] rows) => new(rows.Select(TsvTemplate));

    /// <summary>One data/items.tsv row as an item template (the fields the table has; an empty tier or durability is 0).</summary>
    internal static ItemTemplate TsvTemplate(string row)
    {
        var f = row.Split('\t');
        return new ItemTemplate
        {
            Index = short.Parse(f[0], CultureInfo.InvariantCulture), Name = f[1], Kind = f[2], Slot = f[3],
            Tier = int.TryParse(f[4], CultureInfo.InvariantCulture, out var tier) ? tier : 0,
            MaxDurability = f.Length > 5 && double.TryParse(f[5], CultureInfo.InvariantCulture, out var d) ? d : 0,
        };
    }

    private static void Cleanup(ServerHost host, string dir)
    {
        host.Dispose();
        try { Directory.Delete(dir, true); } catch (IOException) { }
    }

    /// <summary>server/data (searched upwards from the test binary by content/opcode-stubs.json): the golden items.tsv, gamedata.json,
    /// spells.json and journal-ids.tsv when they were generated (they are not in the public repository; see <see cref="Golden"/>).</summary>
    internal static string DataDir()
    {
        // From the test binary, then from the working directory (a build with -o elsewhere).
        foreach (var start in new[] { AppContext.BaseDirectory, Environment.CurrentDirectory })
            for (var d = new DirectoryInfo(start); d is not null; d = d.Parent)
                if (File.Exists(Path.Combine(d.FullName, "content", "opcode-stubs.json"))) return Path.Combine(d.FullName, "data");
        throw new Exception("server/content/opcode-stubs.json not found");
    }

    /// <summary>The path of golden file <paramref name="name"/> in <see cref="DataDir"/>:
    /// a missing reference is reported as skipped by the runner, never as a pass.</summary>
    internal static string? Golden(string name)
    {
        var path = Path.Combine(DataDir(), name);
        if (File.Exists(path)) return path;
        throw new TestSkippedException($"golden reference {name} not published/generated");
    }

    /// <summary>The client's item table and game data, as the server runs on them.</summary>
    private static (ItemStore Catalog, GameData Data) RealData() => (Client.Items, Client.GameData!);

    private static EquippedItem?[] Gear(GameData data, params (int Slot, string Name, int Quality)[] items)
    {
        var slots = new EquippedItem?[10];
        foreach (var (slot, name, q) in items)
        {
            var id = data.Find(name);
            Check(id != 0, $"item {name} known");
            slots[slot] = new EquippedItem(id, q, (long)Math.Round(data.Item(id)!.Durability * 10000));
        }
        return slots;
    }

    /// <summary>Gear sets from capture 114814 Joins (no destiny levels needed for the second).</summary>
    private static void StatVectors()
    {
        var (_, data) = RealData();
        var calc = new StatCalculator(data);
        var none = new Dictionary<int, int>();
        var starter = calc.Compute(Gear(data, (0, "T2_2H_BOW", 1), (2, "T2_HEAD_LEATHER_SET1", 1), (3, "T1_ARMOR_LEATHER_SET1", 1),
            (4, "T2_SHOES_CLOTH_SET1", 1), (5, "T6_BAG", 2), (6, "T6_CAPE", 2)), none);
        Check(starter.MaxHealth == 1439 && Math.Abs(starter.HealthRegen - 35.97338f) < 5e-5 && starter.MaxEnergy == 181
              && Math.Abs(starter.EnergyRegen - 2.39404f) < 5e-5 && starter.MountMaxHealth == 0,
              $"T1/T2 set = 1439 / 35.97338 / 181 / 2.39404 (got {starter})");
        var t4 = calc.Compute(Gear(data, (0, "T4_MAIN_FIRESTAFF_CRYSTAL@1", 1), (1, "T4_OFF_HORN_KEEPER", 1), (2, "T5_HEAD_LEATHER_SET2@2", 2),
            (3, "T4_ARMOR_CLOTH_SET2", 4), (4, "T4_SHOES_PLATE_SET2", 4), (5, "T6_BAG", 2), (6, "T6_CAPE", 2), (7, "T5_MOUNT_OX", 1)), none);
        Check(t4.MaxHealth == 1945 && t4.MaxEnergy == 267 && t4.MountMaxHealth == 1262 && Math.Abs(t4.MountHealthRegen - 19.13f) < 5e-5,
              $"T4/T5 set without destiny levels = 1945 / 267, mount 1262 / 19.13 (got {t4})");
        Check(calc.Compute([], none) == new CharacterStats(1200, 12, 120, 1.5f, 0, 0), "naked = characters.xml defaults");
    }

    /// <summary>The default character (T4 starter kit, everything unlocked) against an in-process game server.</summary>
    private static void StatsInGame()
    {
        var (catalog, data) = RealData();
        var dir = NewDir();
        using var host = Host(dir, spells: false);
        var hero = host.Characters.List()[0];
        hero.Inventory = [new("T4_ORE", 10), new("T4_WOOD", 10), new("T5_MOUNT_OX", 1), new("T4_ARMOR_CLOTH_SET1", 1)];
        host.Characters.Save(hero);
        using var client = new TestPhotonClient(host.GameServer.EndPoint);
        client.Connect();
        Check(client.EncryptionDone.Task.Wait(10000), "game encryption");
        var join = Call(client, new JoinRequest { ss = hero.Id }, 1);
        var jr = new JoinResponse();
        AlbionSerializer.FillFields(jr, join.Parameters);
        Check(!join.Parameters.ContainsKey(ReservedKeys.ServerTime), "join has no key 251 (the official Join has none)");

        var levels = Progression.UnlockAll(data).Levels;
        var expected = new StatCalculator(data).Compute(Gear(data, (0, "T4_MAIN_SWORD", 1), (2, "T4_HEAD_PLATE_SET1", 1),
            (3, "T4_ARMOR_PLATE_SET1", 1), (4, "T4_SHOES_PLATE_SET1", 1), (5, "T4_BAG", 1), (6, "T4_CAPE", 1)), levels);
        Check(expected.MaxHealth > 1200 && jr.a8l == expected.MaxHealth && jr.a8m == expected.MaxHealth && jr.a8o == expected.HealthRegen
              && jr.a8r == expected.MaxEnergy && jr.a8t == expected.EnergyRegen,
              $"join bars are the calculated ones ({expected.MaxHealth} / {expected.MaxEnergy}, got {jr.a8m} / {jr.a8r})");
        Check(!join.Parameters.ContainsKey(21) && !join.Parameters.ContainsKey(22), "no mount bar without a mount");

        var events = new List<Dictionary<byte, object?>>();
        while (client.Events.TryTake(out var e, 5000))
        {
            events.Add(e.Parameters);
            if (Convert.ToInt32(e.Parameters.GetValueOrDefault(ReservedKeys.EventCode)) == (int)EventCodes.JoinFinished) break;
        }
        SkipLoginLists(client);
        int Code(Dictionary<byte, object?> p) => Convert.ToInt32(p.GetValueOrDefault(ReservedKeys.EventCode));
        var fai = events.FindIndex(p => Code(p) == (int)EventCodes.FullAchievementInfo);
        // Unlock-all has no fame inside a level, so no 153: 150, empty tracking (154), minimal auto-learn (155).
        // Then MountCooldownUpdate, sent at every Join (stageD mounts SPEC §4.8).
        Check(fai >= 0 && fai == events.Count - 5 && Code(events[^4]) == (int)EventCodes.FullTrackedAchievementInfo
              && events[^4].GetValueOrDefault((byte)1) is int[] { Length: 0 } && Code(events[^3]) == (int)EventCodes.FullAutoLearnAchievementInfo
              && events[^3].GetValueOrDefault((byte)1) is int[] { Length: 0 } && Code(events[^2]) == (int)EventCodes.MountCooldownUpdate
              && Code(events[^1]) == (int)EventCodes.JoinFinished,
              "FullAchievementInfo, empty tracking and auto-learn lists, then MountCooldownUpdate before JoinFinished");
        var ach = new FullAchievementInfoEvent();
        AlbionSerializer.FillFields(ach, events[fai]);
        Check(ach.ddn == jr.a8a && ach.ccy?.Length == 30 && ach.ccz?.Length == 674 && ach.cc0 is { Length: 674 } lv && lv.All(l => l == 100)
              && ach.cc1?.Length == 674 && ach.cc2?.Length == 674,
              "unlock-all: 30 finished, 674 templates at level 100, for the player object");

        // Exactly the events of one move: wait for the expected count, then make sure nothing else follows.
        List<Dictionary<byte, object?>> Move(int fromSlot, Guid from, int toSlot, Guid to, int count)
        {
            client.SendOperation(Request(new InventoryMoveItemRequest { ru = fromSlot, rv = from, rx = toSlot, ry = to, rz = 1 }, null));
            var list = new List<Dictionary<byte, object?>>();
            while (list.Count < count && client.Events.TryTake(out var e, 5000)) list.Add(e.Parameters);
            if (client.Events.TryTake(out var extra, 300)) list.Add(extra.Parameters);
            return list;
        }
        int[] Codes(List<Dictionary<byte, object?>> l) => l.Select(Code).ToArray();
        var eq = hero.EquipmentContainer;
        var inv = hero.InventoryContainer;

        // Swap onto an occupied slot, as captured (114814): put(moved), 96, 90, put(displaced).
        var swap = Move(3, inv, 3, eq, 4);
        Check(Codes(swap) is [(int)EventCodes.InventoryPutItem, (int)EventCodes.RegenerationPlayerComboChanged,
                (int)EventCodes.CharacterEquipmentChanged, (int)EventCodes.InventoryPutItem]
              && Convert.ToInt32(swap[0].GetValueOrDefault((byte)1) ?? 0) == 3 && Convert.ToInt32(swap[3].GetValueOrDefault((byte)1) ?? 0) == 3,
              $"armor swap: put(cloth -> equipment 3), 96, 90, put(plate -> inventory 3) (got {string.Join(",", Codes(swap))})");
        var swapped = Saved(host, hero.Id);
        Check(swapped.Equipment[3]?.Name == "T4_ARMOR_CLOTH_SET1" && swapped.Inventory[3]?.Name == "T4_ARMOR_PLATE_SET1", "swap is saved");
        var back = Move(3, inv, 3, eq, 4); // plate back on: same order
        Check(Codes(back).Length == 4, "plate back on");

        // Mount moves keep the player's bars: no 96 either way (113433).
        var mountOn = Move(2, inv, 7, eq, 2);
        Check(Codes(mountOn) is [(int)EventCodes.InventoryPutItem, (int)EventCodes.CharacterEquipmentChanged],
              $"mount on: put, 90, no 96 (got {string.Join(",", Codes(mountOn))})");
        var mountOff = Move(7, eq, 2, inv, 2);
        Check(Codes(mountOff) is [(int)EventCodes.CharacterEquipmentChanged, (int)EventCodes.InventoryPutItem],
              $"mount off: 90, put, no 96 (got {string.Join(",", Codes(mountOff))})");

        // Unequip the armor: bars drop, 96 goes before 90, then the put.
        var got = Move(3, eq, 5, inv, 3);
        Check(got.Count == 3 && Code(got[0]) == (int)EventCodes.RegenerationPlayerComboChanged
              && Code(got[1]) == (int)EventCodes.CharacterEquipmentChanged && Code(got[2]) == (int)EventCodes.InventoryPutItem,
              "unequip: RegenerationPlayerComboChanged, CharacterEquipmentChanged, InventoryPutItem");
        var combo = new RegenerationPlayerComboChangedEvent();
        AlbionSerializer.FillFields(combo, got[0]);
        var without = new StatCalculator(data).Compute(Gear(data, (0, "T4_MAIN_SWORD", 1), (2, "T4_HEAD_PLATE_SET1", 1),
            (4, "T4_SHOES_PLATE_SET1", 1), (5, "T4_BAG", 1), (6, "T4_CAPE", 1)), levels);
        Check(combo.ddn == jr.a8a && combo.dui == without.MaxHealth && combo.duh == without.MaxHealth && combo.dui < expected.MaxHealth
              && combo.dun == without.MaxEnergy, $"combo carries the bars without armor ({without.MaxHealth}, got {combo.dui})");
        var change = new CharacterEquipmentChangedEvent();
        AlbionSerializer.FillFields(change, got[1]);
        Check(Math.Abs(change.bw3 - DateTime.UtcNow.Ticks) < TimeSpan.TicksPerMinute, "equipment change time is absolute ticks");
        var json = System.Text.Json.JsonSerializer.Serialize(new Character { Progression = Progression.UnlockAll(data) });
        Check(json.Contains("\"Levels\"") && !json.Contains("LevelsById"), "progression saves only its own fields");
        client.Dispose();
        Cleanup(host, dir);
    }

    /// <summary>Entries the server cannot turn into objects (unknown name, beyond the slots) are written back unchanged.</summary>
    private static void UnloadedItemsSurvive()
    {
        var dir = NewDir();
        using var host = Host(dir, spells: false);
        var hero = host.Characters.List()[0];
        hero.Equipment = [new("T4_MAIN_SWORD"), null, new("NO_SUCH_HEAD"), null, null, null, null, null, null, null, new("T4_CAPE")];
        var inventory = new List<InventoryItem?> { new("NO_SUCH_ITEM", 3) };
        while (inventory.Count < 49) inventory.Add(null);
        inventory.Add(new("T4_ORE", 7)); // slot 49: beyond the 48 inventory slots
        hero.Inventory = inventory;
        host.Characters.Save(hero);
        using var client = new TestPhotonClient(host.GameServer.EndPoint);
        client.Connect();
        Check(client.EncryptionDone.Task.Wait(10000), "game encryption");
        var join = Call(client, new JoinRequest { ss = hero.Id }, 1);
        Check(join.ReturnCode == 0, "join with unknown items");
        client.SendOperation(Request(new InventoryMoveItemRequest { ru = 0, rv = hero.EquipmentContainer, rx = 3, ry = hero.InventoryContainer, rz = 1 }, null));
        var moved = SpinWait.SpinUntil(() => Saved(host, hero.Id).Equipment[0] is null, 5000);
        var saved = Saved(host, hero.Id);
        Check(moved && saved.Inventory[3]?.Name == "T4_MAIN_SWORD", "the sword moved and was saved");
        Check(saved.Equipment[2]?.Name == "NO_SUCH_HEAD" && saved.Inventory[0] is { Name: "NO_SUCH_ITEM", Count: 3 },
              "unknown entries stay in their slots");
        Check(saved.Inventory.Skip(48).Any(i => i is { Name: "T4_ORE", Count: 7 }) && saved.Inventory.Skip(48).Any(i => i?.Name == "T4_CAPE"),
              "entries beyond the slots are kept after slot 47");
        client.Dispose();
        Cleanup(host, dir);
    }

    internal static void Check(bool cond, string what)
    {
        if (!cond) throw new Exception("check failed: " + what);
    }

    private static string Hex(byte[] b) => Convert.ToHexString(b).ToLowerInvariant();

    /// <summary>Parameters of a message after a Protocol18 encode/decode, as the client's reader (g71) gets them.</summary>
    private static Dictionary<byte, object?> OnWire(object msg)
    {
        var w = new ByteWriter();
        Protocol18.WriteParameterTable(w, AlbionSerializer.ToParameters(msg));
        return Protocol18.ReadParameterTable(new ByteReader(w.ToArray()));
    }

    private static void WireEmptyEnumStub()
    {
        // The default reply AlbionSession sends for an unhandled ArmoryItemUsageStatistics. Key 0 (Activity[])
        // as Int32[] made the client throw InvalidCastException: its ce+c.e setter 0x180747a80 casts to Byte[].
        var resp = AlbionSerializer.BuildResponse(new ArmoryItemUsageStatisticsResponse().FillEmpty(), 7);
        var w = new ByteWriter();
        Protocol18.WriteOperationResponse(w, resp);
        Check(Hex(w.ToArray()) == "0100000804" + "004300" + "014700" + "fd041202" + "ff090e", $"stub bytes (got {Hex(w.ToArray())})");
        var back = Protocol18.ReadOperationResponse(new ByteReader(w.ToArray()));
        Check(back.Parameters[0] is byte[] { Length: 0 }, "key 0 arrives as byte[0]");
    }

    private static void WirePackedEnum()
    {
        // Official ArmoryItemUsageStatistics key 0: Activity 25, 11, 12 as one byte[12].
        var p = OnWire(new ArmoryItemUsageStatisticsResponse { ak5 = [25, 11, 12] });
        Check(p[0] is byte[] b && Hex(b) == "190000000b0000000c000000", "Activity[] = 4 little-endian bytes per element");
        var back = new ArmoryItemUsageStatisticsResponse();
        AlbionSerializer.FillFields(back, p);
        Check(back.ak5 is [25, 11, 12], "Activity[] unpacked again");
        Check(OnWire(new ClusterInfoUpdateEvent { bzb = [5, 6, 4, 3] })[38] is byte[] f && Hex(f) == "05060403",
              "byte-backed FlaggingStatus[] = 1 byte per element (official bytes)");
        Check(OnWire(new CharacterEquipmentChangedEvent { bw4 = [1, 2] })[2] is int[] and [1, 2], "plain int[] is not packed");
    }

    private static void WirePackedNested()
    {
        var tags = OnWire(new GuildFullAccessTagsUpdatedEvent { ces = [[1], [2], [3]] });
        Check(tags[11] is byte[] t && Hex(t) == "03000000" + "010000000100000001000000" + "010203",
              "byte[][] = [i32 n][n × i32 len] then the bytes (ce.f)");
        var tagsBack = new GuildFullAccessTagsUpdatedEvent();
        AlbionSerializer.FillFields(tagsBack, tags);
        Check(tagsBack.ces is [[1], [2], [3]], "byte[][] unpacked again");

        var g = Guid.Parse("00112233-4455-6677-8899-aabbccddeeff");
        var logs = OnWire(new GetSurveyLogsResponse { a4y = [[g], []] });
        Check(logs[2] is byte[] l && Hex(l) == "02000000" + "01000000" + "00000000" + Hex(g.ToByteArray()),
              "Guid[][] = [i32 n][n × i32 count] then 16-byte Guids (ce.p)");
        var logsBack = new GetSurveyLogsResponse();
        AlbionSerializer.FillFields(logsBack, logs);
        Check(logsBack.a4y is [[var g0], []] && g0 == g, "Guid[][] unpacked again");
        Check(OnWire(new GetSurveyLogsResponse().FillEmpty())[2] is byte[] e && Hex(e) == "00000000", "empty Guid[][] = 00000000");

        // Official DeathRecap key 18 (cdu[][], int enum): header, then 4 bytes per element.
        var recap = OnWire(new DeathRecapResponse { ap0 = [[0], [2, 0, 0, 0, 0], [0, 0, 0]] });
        Check(recap[18] is byte[] r && Hex(r) == "03000000" + "010000000500000003000000" + "00000000" + "02000000" + string.Concat(Enumerable.Repeat("00000000", 7)),
              "enum[][] = [i32 n][n × i32 len] then the elements (ce+d)");
    }

    private static void WireCompressedTime()
    {
        var b = AlbionSerializer.CompressedTimeBase;
        Check(b == GameTime.StartTicks, "compressed time base = Login key 251 (GameTime.StartTicks)");
        // Server → client: ticks go out as Int64 (ce.n setter: Int64 = ticks) and come back unchanged.
        var now = GameTime.Now;
        var ev = OnWire(new CharacterEquipmentChangedEvent { ddn = 5, bw3 = now });
        Check(ev[1] is long l && l == now, "[eo] GameTimeStamp sent as Int64 ticks");
        var evBack = new CharacterEquipmentChangedEvent();
        AlbionSerializer.FillFields(evBack, ev);
        Check(evBack.bw3 == now, "ticks round trip");
        // Client → server: the ce.n getter sends ms since the base, narrowed to Byte/Int16/Int32, else Int64 ticks.
        var t = b + 70_666_944 * TimeSpan.TicksPerMillisecond;
        var place = new PlaceableObjectPlaceRequest();
        foreach (var (wire, want) in new (object, long)[]
                 { ((int)((t - b) / TimeSpan.TicksPerMillisecond), t), ((short)300, b + 3_000_000), ((byte)200, b + 2_000_000), (t, t) })
        {
            AlbionSerializer.FillFields(place, new() { [0] = wire });
            Check(place.x6 == want, $"[eo] GameTimeStamp from {wire.GetType().Name} {wire} (got {place.x6}, want {want})");
        }
        // ce.m getter: Int32[] ms or Int64[] ticks; any narrower integer array is read as ms.
        var cd = new SpellCooldownUpdateEvent();
        AlbionSerializer.FillFields(cd, new() { [1] = new[] { 5, 6 }, [2] = new short[] { 7 } });
        Check(cd.dy8 is [var c0, var c1] && c0 == b + 50_000 && c1 == b + 60_000 && cd.dy9 is [var c2] && c2 == b + 70_000,
              "[eo] GameTimeStamp[] from Int32[] / Int16[] ms");
        var cdBack = new SpellCooldownUpdateEvent();
        AlbionSerializer.FillFields(cdBack, OnWire(new SpellCooldownUpdateEvent { dy8 = [now, t] }));
        Check(cdBack.dy8 is [var d0, var d1] && d0 == now && d1 == t, "[eo] GameTimeStamp[] ticks round trip");
    }

    private static void WireArraysAsIs()
    {
        // c4[] (SpellCooldownUpdate key 3) is only accepted as Int64[], so small values are not narrowed.
        Check(OnWire(new SpellCooldownUpdateEvent { dza = [1, 2] })[3] is long[] and [1, 2], "c4[] stays Int64[]");
        // Rank-3 arrays travel as arrays of arrays; an empty middle row would reach the client as null.
        var full = OnWire(new ArmorySearchResponse { alb = [[[1, 2]], [[3], []]] });
        Check(full[4] is long[][][] and [[[1, 2]], [[3], []]], "a7m[][][] without empty middle rows is sent as is");
        Check(!OnWire(new ArmorySearchResponse { alb = [[[1]], []] }).ContainsKey(4), "a7m[][][] with an empty middle row is left out");
        Check(!OnWire(new PartyFinderFullUpdateEvent { dgp = [[], [[1]]] }).ContainsKey(10), "int[][][] with an empty middle row is left out");
    }

    private static void SerializerRoundTrip()
    {
        var id = Guid.NewGuid();
        var resp = new SelectCharacterResponse { bln = "1.2.3.4:5", blv = id, blw = 99 };
        var p = AlbionSerializer.BuildResponse(resp, 7).Parameters;
        Check((short)p[ReservedKeys.OperationCode]! == (short)OperationCodes.SelectCharacter, "op code in 253");
        Check((int)p[ReservedKeys.RequestId]! == 7, "request id in 255");
        Check(p[8] is byte[] { Length: 16 }, "Guid as byte[16]");
        Check(!p.ContainsKey(6), "zero int omitted");
        var back = new SelectCharacterResponse();
        AlbionSerializer.FillFields(back, p);
        Check(back.bln == "1.2.3.4:5" && back.blv == id && back.blw == 99, "fields restored");
    }

    /// <summary>The Login of the real client's version with name and password (the name's first login sets the password).</summary>
    internal static LoginRequest TestLogin(string name = "user", string password = "password1") =>
        new() { aiu = 1, aiv = 32, aiw = 455, aiy = 345319, uy = name, uz = password };

    internal static OperationRequest Request(AlbionRequest req, int? requestId)
    {
        var p = AlbionSerializer.ToParameters(req);
        p[ReservedKeys.OperationCode] = (short)AlbionSerializer.CodeOf(req);
        if (requestId is { } id) p[ReservedKeys.RequestId] = id;
        return new OperationRequest(AlbionSerializer.PhotonOperationCode, p);
    }

    /// <summary>
    /// After a login's JoinFinished: takes the mail events and the mute and friends lists that follow it (201, 202, 403, 294, 294, 295; stageJ
    /// chat SPEC §2.6, mail SPEC §1.3), so a
    /// test reads the events of its next request.
    /// </summary>
    internal static void SkipLoginLists(TestPhotonClient client)
    {
        while (client.Events.TryTake(out var e, 5000))
            if (Convert.ToInt32(e.Parameters.GetValueOrDefault(ReservedKeys.EventCode)) == (int)EventCodes.FriendInfos) return;
        Check(false, "the login's FriendInfos after JoinFinished");
    }

    internal static OperationResponse Call(TestPhotonClient client, AlbionRequest req, int? requestId)
    {
        client.SendOperation(Request(req, requestId));
        Check(client.Responses.TryTake(out var resp, 10000), $"response to {req.GetType().Name}");
        return resp!;
    }

    private static void LoginChain()
    {
        var dir = NewDir();
        // A two-item table and no game data: the login chain does not depend on the client data.
        using var host = Host(dir, items: Items("8610\tT4_MAIN_SWORD\tweapon\tmainhand\t4\t13500", "1000\tT4_ORE\tsimpleitem\t\t4"));
        var characters = host.Characters;
        characters.Create(host.Starter.NewCharacter("Second"));

        using var client = new TestPhotonClient(host.LoginServer.EndPoint);
        client.Connect();
        Check(client.EncryptionDone.Task.Wait(10000), "login encryption");

        var login = Call(client, TestLogin(), 1);
        Check(login.ReturnCode == 0, "login ok");
        Check(login.Parameters.GetValueOrDefault(ReservedKeys.ServerTime) is long, "key 251 is a long");
        Check((long)login.Parameters[ReservedKeys.ServerTime]! == AlbionSerializer.CompressedTimeBase, "key 251 is the compressed time base");
        Check((int)login.Parameters[ReservedKeys.RequestId]! == 1, "request id echoed");
        var lr = new LoginResponse();
        AlbionSerializer.FillFields(lr, login.Parameters);
        Check(lr.bdd is ["LocalHero", "Second"], "character names");
        Check(!login.Parameters.ContainsKey(78), "no time scaling array in login (needs exactly 5 values when sent)");
        Check(lr.bfv?.Length == FeatureSwitches.Count, $"feature table has {FeatureSwitches.Count} entries (got {lr.bfv?.Length})");
        // Every per-character array must have one entry per character (CreateCharacterRecord).
        foreach (var f in typeof(LoginResponse).GetFields())
        {
            if (f.GetCustomAttributes(typeof(ParamAttribute), false) is not [ParamAttribute { Key: var key }]) continue;
            if (key is >= 9 and <= 48 && f.GetValue(lr) is Array a)
                Check(a.Length == 2, $"array key {key} has 2 entries (got {a.Length})");
        }

        var sel = Call(client, new SelectCharacterRequest { abc = "Second" }, null);
        var sr = new SelectCharacterResponse();
        AlbionSerializer.FillFields(sr, sel.Parameters);
        Check(sel.ReturnCode == 0 && sr.blo == "0000", "cluster in select response");
        Check(!sel.Parameters.ContainsKey(ReservedKeys.RequestId), "no request id when none was sent");

        // Game server on the address from the select response.
        Check(sr.bln == $"127.0.0.1:{host.GameServer.EndPoint.Port}", $"select response names the bound game server (got {sr.bln})");
        var second = characters.Find("Second")!;
        second.Equipment = [new("T4_MAIN_SWORD"), null, null, null, null, null, null, null, null, null];
        second.Inventory = [new("T4_ORE", 10)];
        characters.Save(second);
        var gameClient = new TestPhotonClient(host.GameServer.EndPoint); // disposed below to leave the game
        gameClient.Connect();
        Check(gameClient.EncryptionDone.Task.Wait(10000), "game encryption");
        var join = Call(gameClient, new JoinRequest { ss = second.Id }, 2);
        var jr = new JoinResponse();
        AlbionSerializer.FillFields(jr, join.Parameters);
        Check(join.ReturnCode == 0 && jr.a8b == second.Id && jr.a8c == "Second", "join returns the selected character");
        Check(jr.a8i == sr.blo, "join cluster matches the select response");
        Check(jr.a8l > 0 && jr.a8l <= jr.a8m && jr.a8q > 0 && jr.a8q <= jr.a8r, "join sends health and energy within their maximum");
        Check(Math.Abs(jr.a96 - DateTime.UtcNow.Ticks) < TimeSpan.TicksPerMinute && jr.a97 != 0,
              "join anchors the client clock to UTC ticks and Photon server time");
        Check(!join.Parameters.ContainsKey(70), "no time scaling array (needs exactly 5 values when sent)");
        Check(jr.a9h?.Length == 5 && jr.ba7?.Length == 6 && jr.bba?.Length == 7 && jr.a8g?.Length == 5 && jr.a8h?.Length == 5,
              "join sends the fixed-size arrays with the lengths the official server uses");
        Check(!join.Parameters.ContainsKey(112) && !join.Parameters.ContainsKey(116), "join leaves out arrays the official server never sends");
        // Items go out before the Join response (as on the official server), then the chat tabs and JoinFinished.
        var events = new List<Dictionary<byte, object?>>();
        while (events.Count < 5 && gameClient.Events.TryTake(out var ie, 5000)) events.Add(ie.Parameters);
        SkipLoginLists(gameClient);
        int Code(int i) => Convert.ToInt32(events[i].GetValueOrDefault(ReservedKeys.EventCode));
        Check(events.Count == 5
              && Code(0) == (int)EventCodes.NewEquipmentItem && Convert.ToInt32(events[0][1]) == 8610
              && Convert.ToInt64(events[0][8]) == 135_000_000 && events[0].GetValueOrDefault((byte)9) is short[] && events[0].GetValueOrDefault((byte)10) is short[]
              && Code(1) == (int)EventCodes.NewSimpleItem && Convert.ToInt32(events[1][2]) == 10
              && Code(2) == (int)EventCodes.UpdateChatSettings && events[2][0] is string[] { Length: 7 } && events[2][1] is int[][] { Length: 7 }
              && Code(3) == (int)EventCodes.MountCooldownUpdate && Code(4) == (int)EventCodes.JoinFinished,
              "items are created, then the chat tabs (without them the client shows no chat line), MountCooldownUpdate (every Join) and JoinFinished follow the join response");
        Check(jr.a9p == second.EquipmentContainer && jr.a9s == second.InventoryContainer
              && jr.a9q?.Length == 10 && jr.a9q[0] == Convert.ToInt64(events[0][0])
              && jr.a9t?.Length == 48 && jr.a9t[0] == Convert.ToInt64(events[1][0]),
              "join lists the created item ids in the container slots");

        // InventoryMoveItem (no response; events as captured from the official server).
        var swordId = jr.a9q![0];
        List<(int Code, Dictionary<byte, object?> P)> Move(int fromSlot, Guid from, int toSlot, Guid to, int expected)
        {
            gameClient.SendOperation(Request(new InventoryMoveItemRequest { ru = fromSlot, rv = from, rx = toSlot, ry = to, rz = 7 }, null));
            var got = new List<(int, Dictionary<byte, object?>)>();
            while (got.Count < expected && gameClient.Events.TryTake(out var e, 5000))
                got.Add((Convert.ToInt32(e.Parameters.GetValueOrDefault(ReservedKeys.EventCode)), e.Parameters));
            return got;
        }
        var unequip = Move(0, second.EquipmentContainer, 5, second.InventoryContainer, 2);
        Check(unequip is [(var c1, var eq1), (var c2, var put1)]
              && c1 == (int)EventCodes.CharacterEquipmentChanged && eq1[2] is int[] { Length: 10 } ids && ids[0] == 0
              && c2 == (int)EventCodes.InventoryPutItem && Convert.ToInt64(put1[0]) == swordId && Convert.ToInt32(put1[1]) == 5
              && Convert.ToInt64(put1[3]) == 7,
              "unequip: equipment change, then the sword is put into inventory slot 5 with the revision echoed");
        Check(!gameClient.Responses.TryTake(out _, 300), "no response to InventoryMoveItem");
        var equip = Move(5, second.InventoryContainer, 0, second.EquipmentContainer, 2);
        Check(equip is [(var c3, _), (var c4, var eq2)]
              && c3 == (int)EventCodes.InventoryPutItem && c4 == (int)EventCodes.CharacterEquipmentChanged
              && eq2[2] is int[] ids2 && ids2[0] == 8610 && eq2.GetValueOrDefault((byte)8) is true,
              "equip: put first, then the equipment change with the into-equipment flag");
        var wrong = Move(0, second.InventoryContainer, 2, second.EquipmentContainer, 1);
        Check(wrong is [(var c5, var back)] && c5 == (int)EventCodes.InventoryPutItem
              && Convert.ToInt32(back.GetValueOrDefault((byte)1) ?? 0) == 0 && back[2] is byte[] g && g.SequenceEqual(second.InventoryContainer.ToByteArray()),
              "ore does not fit the head slot and is put back");
        var moved = Saved(host, second.Id);
        Check(moved.Equipment[0]?.Name == "T4_MAIN_SWORD" && moved.Inventory is [{ Name: "T4_ORE" }], "moves are saved to the character");
        // Move is fire-and-forget: a MoveResponse would teleport the player (cyl.ai7).
        // Walk 3 units right at 5.5/s; after the 1 s wait the player has reached the target.
        gameClient.SendOperation(Request(new MoveRequest { v6 = [3.5f, -1.25f], v8 = [6.5f, -1.25f], v9 = 5.5f }, null));
        Check(!gameClient.Responses.TryTake(out _, 1000), "no response to Move");
        Check(gameClient.Keepalives.TryTake(out var ka, 2000) && ka.Parameters.GetValueOrDefault((byte)0) is 1 && ka.Parameters.GetValueOrDefault((byte)1) is 0f,
              "the world loop sends the keepalive event 2 {0: 1, 1: 0.0} about a second after Join");
        gameClient.Dispose();
        Character? left = null;
        var saved = SpinWait.SpinUntil(() => (left = characters.Find(second.Id)) is { PositionX: 6.5f, PositionY: -1.25f }, 5000);
        Check(saved, $"position at the move target is saved on leave (got {left?.PositionX}, {left?.PositionY})");

        using var rejoinClient = new TestPhotonClient(host.GameServer.EndPoint);
        rejoinClient.Connect();
        Check(rejoinClient.EncryptionDone.Task.Wait(10000), "rejoin encryption");
        var rejoin = new JoinResponse();
        AlbionSerializer.FillFields(rejoin, Call(rejoinClient, new JoinRequest { ss = second.Id }, 3).Parameters);
        Check(rejoin.a8j == new System.Numerics.Vector2(6.5f, -1.25f), $"rejoin spawns at the saved position (got {rejoin.a8j})");
        Cleanup(host, dir);
    }
}
