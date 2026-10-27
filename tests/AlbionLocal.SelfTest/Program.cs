using System.Collections;
using System.Net;
using System.Numerics;
using System.Security.Cryptography;
using System.Diagnostics;
using System.Text.Json;
using AlbionLocal.Photon;
using AlbionLocal.Photon.Crypto;
using AlbionLocal.Photon.Serialization;
using AlbionLocal.SelfTest;
using AlbionLocal.Server.Protocol;

TestRunOptions options;
try { options = TestRunOptions.Parse(args); }
catch (ArgumentException e) { Console.Error.WriteLine(e.Message); return 2; }
if (options.Help)
{
    Console.WriteLine("SelfTest [--client-gamedata=<folder>] [--only=<text>] [--report=<file.json>] [--lab=all|serialization|transport|session|messages] [--protocol-trace] [-v]");
    return 0;
}
AlbionTests.ClientGameDataOverride = options.ClientGameData;
ProtocolTrace.Enabled = options.Trace;

var tests = new List<(string Name, Action Body)>
{
    ("protocol18 golden bytes", Protocol18Golden),
    ("protocol18 round trip", Protocol18RoundTrip),
    ("dh matches client-style derivation", DhInterop),
    ("udp handshake, encryption, fragmentation", () => EndToEnd(0)),
    ("udp with 20% packet loss", () => EndToEnd(0.2)),
};
tests.AddRange(ConfigurationTest.All);
tests.AddRange(StatusProbeTest.All);
tests.AddRange(ClientDataTest.All);
tests.AddRange(AlbionTests.All);
tests.AddRange(WorldTest.All);
tests.AddRange(PersistenceTest.All);
tests.AddRange(RepositoryContractTest.All);
tests.AddRange(TutorialDataTest.All);
tests.AddRange(TutorialJourneyDataTest.All);
tests.AddRange(TutorialObtainabilityTest.All);
tests.AddRange(TutorialJourneyTest.All);
tests.AddRange(TutorialWorldTest.All);
tests.AddRange(OpeningTutorialTest.All);
tests.AddRange(CharacterEntryTest.All);
tests.AddRange(CommandTest.All);
tests.AddRange(ClusterDataTest.All);
tests.AddRange(WorldObjectTest.All);
tests.AddRange(TransferTest.All);
tests.AddRange(HarvestTest.All);
tests.AddRange(ResourceNodeTest.All);
tests.AddRange(MobSpawnTest.All);
tests.AddRange(CombatTest.All);
tests.AddRange(LootMovesTest.All);
tests.AddRange(CorpseTest.All);
tests.AddRange(EffectsTest.All);
tests.AddRange(EffectsBatch2Test.All);
tests.AddRange(EffectsBatch3Test.All);
tests.AddRange(ConsumablesTest.All);
tests.AddRange(MountsTest.All);
tests.AddRange(MobAiTest.All);
tests.AddRange(MobSpellTest.All);
tests.AddRange(PlayerKnockdownTest.All);
tests.AddRange(MultiplayerTest.All);
tests.AddRange(AccountTest.All);
tests.AddRange(ProgressionTest.All);
tests.AddRange(EconomyTest.All);
tests.AddRange(PartyTest.All);
tests.AddRange(GuildTest.All);
tests.AddRange(AllianceTest.All);
tests.AddRange(RatesTest.All);
tests.AddRange(DuelTest.All);
tests.AddRange(OpenPvpTest.All);
tests.AddRange(FlagToggleTest.All);
tests.AddRange(QolTest.All);
tests.AddRange(VanityTest.All);
tests.AddRange(PvpDeathTest.All);
tests.AddRange(PveTest.All);
tests.AddRange(OpsAuditTest.All);
tests.AddRange(FishingTest.All);
tests.AddRange(RandomDungeonTest.All);
tests.AddRange(JournalTest.All);
tests.AddRange(WorldChestTest.All);
tests.AddRange(BossTest.All);
tests.AddRange(EventTest.All);
tests.AddRange(InvasionTest.All);
tests.AddRange(ExpeditionTest.All);
tests.AddRange(CorruptedTest.All);
tests.AddRange(CorruptedInvasionTest.All);
tests.AddRange(HellgateTest.All);
tests.AddRange(ArenaTest.All);
tests.AddRange(ArenaRulesTest.All);
tests.AddRange(CrystalArenaTest.All);
tests.AddRange(AvalonRoadsTest.All);
tests.AddRange(MistsTest.All);
tests.AddRange(MistsBTest.All);
tests.AddRange(FactionTest.All);
tests.AddRange(GoalTest.All);
tests.AddRange(PvpGoalTest.All);
tests.AddRange(GuildGoalTest.All);
tests.AddRange(BlackMarketTest.All);
tests.AddRange(BotNavTest.All);
tests.AddRange(BotTest.All);
tests.AddRange(BotSocialTest.All);
tests.AddRange(BotPartyTest.All);
tests.AddRange(BotRosterTest.All);
tests.AddRange(BotTravelTest.All);
tests.AddRange(BotGatherTest.All);
tests.AddRange(BotPatrolTest.All);
tests.AddRange(BotStageMTest.All);
tests.AddRange(BotSupportTest.All);
tests.AddRange(IslandTest.All);
tests.AddRange(DefenseTest.All);
tests.AddRange(ChatTest.All);
tests.AddRange(SocialTest.All);
tests.AddRange(MailTest.All);
tests.AddRange(TradeTest.All);
tests.AddRange(FocusTest.All);
tests.AddRange(PvpSpellTest.All);
tests.AddRange(PvpDetailsTest.All);
tests.AddRange(ShrineTest.All);
tests.AddRange(HuntTest.All);
tests.AddRange(StaticDungeonTest.All);
tests.AddRange(WebPanelTest.All);

tests.AddRange(TerritoryTest.All);
tests.AddRange(TerritoryClaimTest.All);
tests.AddRange(CastleTest.All);
tests.AddRange(HideoutTest.All);
tests.AddRange(CompanionTest.All);
tests.AddRange(LabExercises.All);
tests.AddRange(LabTests.All);

Log.Level = options.Verbose ? LogLevel.Trace : LogLevel.Warn;
// As the server's Program.cs: compressed GameTimeStamps count from the Login key 251 value.
AlbionLocal.Server.Protocol.AlbionSerializer.CompressedTimeBase = AlbionLocal.Server.Game.GameTime.StartTicks;
// --only=<text>: just the tests whose name contains it (a quick run while working on one system).
if (options.Lab is { } lab)
    tests = [.. tests.Where(t => LabExercises.InStage(t.Name, lab))];
if (options.Only is { } only)
    tests = [.. tests.Where(t => t.Name.Contains(only, StringComparison.OrdinalIgnoreCase))];
var results = new List<TestResult>();
var failed = 0;
var skipped = 0;
foreach (var (name, body) in tests)
{
    var watch = Stopwatch.StartNew();
    try
    {
        body();
        results.Add(new(name, "passed", watch.Elapsed.TotalMilliseconds));
        Console.WriteLine($"PASS  {name}");
    }
    catch (TestSkippedException e)
    {
        skipped++;
        results.Add(new(name, "skipped", watch.Elapsed.TotalMilliseconds, e.Message));
        Console.WriteLine($"SKIP  {name}\n      {e.Message}");
    }
    catch (Exception e)
    {
        failed++;
        results.Add(new(name, "failed", watch.Elapsed.TotalMilliseconds, $"{e.GetType().Name}: {e.Message}"));
        Console.WriteLine($"FAIL  {name}\n      {e.GetType().Name}: {e.Message}\n{e.StackTrace}");
    }
}
var passed = tests.Count - failed - skipped;
Console.WriteLine($"{passed} passed, {failed} failed, {skipped} skipped; {tests.Count} selected");
if (options.Report is { } report)
{
    try
    {
        Directory.CreateDirectory(Path.GetDirectoryName(report)!);
        File.WriteAllText(report, JsonSerializer.Serialize(new
        {
            schemaVersion = 1, generatedAtUtc = DateTimeOffset.UtcNow,
            passed, failed, skipped, selected = tests.Count, results,
        }, new JsonSerializerOptions { WriteIndented = true }));
    }
    catch (Exception e) when (e is IOException or UnauthorizedAccessException)
    { Console.Error.WriteLine($"cannot write report: {e.Message}"); return 2; }
}
if (tests.Count == 0) { Console.Error.WriteLine("no tests selected"); return 2; }
return failed == 0 ? 0 : 1;

static void Check(bool cond, string what)
{
    if (!cond) throw new Exception("check failed: " + what);
}

static string Hex(ReadOnlySpan<byte> b) => Convert.ToHexString(b).ToLowerInvariant();

static byte[] Typed(object? v)
{
    var w = new ByteWriter();
    Protocol18.WriteTyped(w, v);
    return w.ToArray();
}

static void Protocol18Golden()
{
    Check(Hex(Typed(1)) == "0902", "int 1 = CompressedInt zigzag 2");
    Check(Hex(Typed(-1)) == "0901", "int -1 = zigzag 1");
    Check(Hex(Typed(300)) == "09d804", "int 300 = zigzag 600 varint");
    Check(Hex(Typed(0)) == "1e", "int 0 = IntZero");
    Check(Hex(Typed((short)0x1234)) == "043412", "short little-endian");
    Check(Hex(Typed(1.5f)) == "050000c03f", "float little-endian");
    Check(Hex(Typed("hi")) == "07026869", "string = varint length + utf8");
    Check(Hex(Typed(new byte[] { 1, 2 })) == "43020102", "byte[]");
    Check(Hex(Typed(new[] { true, false, true, true, false, false, false, false, true })) == "42090d01", "bool[] LSB-first");
    Check(Hex(Typed(new[] { 1, -1 })) == "49020201", "int[] = CompressedIntArray");
    Check(Hex(Typed(new[] { new long[] { 5 } })) == "40014a010a", "long[][] = Array of CompressedLongArray");
    Check(Hex(Typed(new Dictionary<int, long> { [1] = 2 })) == "14090a010204", "Dictionary<int,long> typed header");
    Check(Hex(Typed(null)) == "08", "null");

    var resp = new OperationResponse(1, new() { [253] = (short)5 }) { ReturnCode = 0x0102, DebugMessage = null };
    var w = new ByteWriter();
    Protocol18.WriteOperationResponse(w, resp);
    Check(Hex(w.Span) == "01020108" + "01" + "fd" + "040500", "operation response layout");
}

static void Protocol18RoundTrip()
{
    var values = new object?[]
    {
        true, false, (byte)7, (short)-3, 123456789, -987654321L, long.MaxValue, int.MinValue, 3.25f, -1e300d,
        "Ünïcödé", new byte[300], new[] { false, true }, new short[] { 1, -2 }, new[] { 1.5f }, new[] { 2.5d },
        new[] { "a", "" }, new[] { int.MaxValue, 0, -5 }, new[] { long.MinValue, 1L }, new object?[] { 1, "x", null },
        new[] { new[] { "a" }, new[] { "b", "c" } }, new Dictionary<string, string> { ["k"] = "v" },
        new Dictionary<byte, object?> { [1] = 2, [3] = "z" }, new Hashtable { ["h"] = 9 },
    };
    foreach (var v in values)
    {
        var bytes = Typed(v);
        var back = Protocol18.ReadTyped(new ByteReader(bytes));
        Check(Hex(Typed(back)) == Hex(bytes), $"round trip {v?.GetType().Name}");
    }
    var req = new OperationRequest(1, new() { [0] = "user", [253] = (short)5, [255] = 42 });
    var w = new ByteWriter();
    Protocol18.WriteOperationRequest(w, req);
    var r = Protocol18.ReadOperationRequest(new ByteReader(w.ToArray()));
    Check(r.Code == 1 && (string)r.Parameters[0]! == "user" && (short)r.Parameters[253]! == 5 && (int)r.Parameters[255]! == 42, "request");
}

static void DhInterop()
{
    // Client side exactly as g8q: little-endian BigInteger with sign byte, reversed on the wire.
    var p = new BigInteger(Convert.FromHexString(
        "FFFFFFFFFFFFFFFFC90FDAA22168C234C4C6628B80DC1CD129024E088A67CC74020BBEA63B139B22514A08798E3404DD" +
        "EF9519B3CD3A431B302B0A6DF25F14374FE1356D6D51C245E485B576625E7EC6F44C42E9A63A3620FFFFFFFFFFFFFFFF"),
        isUnsigned: true, isBigEndian: true);
    for (var i = 0; i < 1500; i++)
    {
        using var server = new DiffieHellmanCrypto();
        var secret = new BigInteger(RandomNumberGenerator.GetBytes(20), isUnsigned: true);
        var clientPublic = BigInteger.ModPow(22, secret, p).ToByteArray();
        Array.Reverse(clientPublic);
        if (clientPublic[0] == 0) clientPublic = clientPublic[1..];

        var serverKey = (byte[])server.PublicKey.Clone();
        Array.Reverse(serverKey);
        if ((serverKey[^1] & 0x80) != 0) serverKey = [.. serverKey, 0];
        var shared = BigInteger.ModPow(new BigInteger(serverKey), secret, p).ToByteArray();
        Array.Reverse(shared);
        if (shared[0] == 0) shared = shared[1..];
        using var clientAes = Aes.Create();
        clientAes.Key = SHA256.HashData(shared);

        server.DeriveSharedKey(clientPublic);
        var msg = RandomNumberGenerator.GetBytes(i % 70);
        var fromServer = server.Encrypt(msg);
        var decoded = clientAes.DecryptCbc(fromServer, new byte[16], PaddingMode.PKCS7);
        Check(decoded.AsSpan().SequenceEqual(msg), $"client decrypts server data (iteration {i})");
        var fromClient = clientAes.EncryptCbc(msg, new byte[16], PaddingMode.PKCS7);
        Check(server.Decrypt(fromClient).AsSpan().SequenceEqual(msg), $"server decrypts client data (iteration {i})");
    }
}

static void EndToEnd(double dropRate)
{
    using var server = new PhotonServer(new IPEndPoint(IPAddress.Loopback, 0), "e2e", _ => new EchoHandler());
    server.Start();
    using var client = new TestPhotonClient(server.EndPoint, dropRate);
    client.Connect();
    Check(client.InitDone.Task.Wait(10000), "init response");
    Check(client.EncryptionDone.Task.Wait(10000), "encryption established");
    if (ProtocolTrace.Enabled) Console.WriteLine($"SYNTHETIC UDP: Init complete; DH/AES ready; simulated loss {dropRate:P0}");

    var big = new byte[10000];
    Random.Shared.NextBytes(big);
    client.SendOperation(new OperationRequest(1, new() { [0] = big, [255] = 7 }));
    Check(client.Responses.TryTake(out var resp, 15000), "response received");
    Check(resp!.ReturnCode == 0 && (int)resp.Parameters[255]! == 7, "request id echoed");
    Check(((byte[])resp.Parameters[0]!).AsSpan().SequenceEqual(big), "fragmented 10 KB payload intact");
    Check(client.Events.TryTake(out var ev, 15000) && ev!.Code == 1, "event received");
    if (ProtocolTrace.Enabled) Console.WriteLine("SYNTHETIC UDP: encrypted 10 KB request/response reassembled intact; event received");
}

/// <summary>Echoes request parameters back in an encrypted response and raises one event.</summary>
sealed class EchoHandler : IPhotonPeerHandler
{
    public void OnOperationRequest(PhotonPeer peer, OperationRequest request, bool encrypted)
    {
        ProtocolTrace.Write("client->server", "request", request.Code, request.Parameters, encrypted);
        peer.SendOperationResponse(new OperationResponse(request.Code, new(request.Parameters)), encrypted);
        ProtocolTrace.Write("server->client", "response", request.Code, request.Parameters, encrypted, 0);
        peer.SendEvent(new EventData(1, new() { [252] = (short)1 }));
    }

    public void OnDisconnected(PhotonPeer peer, string reason) { }
}
