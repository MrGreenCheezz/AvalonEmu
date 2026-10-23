using AlbionLocal.Photon.Serialization;
using AlbionLocal.Server;
using AlbionLocal.Server.Content;
using AlbionLocal.Server.Network;
using AlbionLocal.Server.Protocol;
using static AlbionLocal.SelfTest.AlbionTests;

namespace AlbionLocal.SelfTest;

/// <summary>Synthetic examples only; the four stages also reuse the existing interop tests.</summary>
public static class LabExercises
{
    private static void Check(bool condition, string message) => AlbionTests.Check(condition, message);
    public static IEnumerable<(string, Action)> All =>
    [
        ("lab serialization: synthetic request bytes", Serialization),
        ("lab messages: bytes to status handler and event", StatusHandler),
    ];

    public static bool InStage(string name, string stage) => stage switch
    {
        "all" => InStage(name, "serialization") || InStage(name, "transport")
            || InStage(name, "session") || InStage(name, "messages"),
        "serialization" => name.StartsWith("protocol18 ") || name.StartsWith("lab serialization:"),
        "transport" => name.StartsWith("udp "),
        "session" => name == "dh matches client-style derivation",
        "messages" => name.StartsWith("lab messages:") || name == "albion serializer round trip"
            || name == "albion login → select → join",
        _ => false,
    };

    private static void Serialization()
    {
        var request = new OperationRequest(1, new() { [0] = "hola", [253] = (short)5, [255] = 42 });
        var writer = new ByteWriter();
        Protocol18.WriteOperationRequest(writer, request);
        var bytes = writer.ToArray();
        Console.WriteLine($"SYNTHETIC request: {Convert.ToHexString(bytes).ToLowerInvariant()}");
        var decoded = Protocol18.ReadOperationRequest(new ByteReader(bytes));
        Check(decoded.Code == 1 && (string)decoded.Parameters[0]! == "hola"
            && (short)decoded.Parameters[253]! == 5 && (int)decoded.Parameters[255]! == 42, "synthetic request round trip");
        Console.WriteLine("Photon op 1; Albion op 5 (Login) at key 253; request id 42 at key 255. Not a valid Login payload.");
        ProtocolTrace.Write("client->server", "request", decoded.Code, decoded.Parameters, false);
    }

    private static void StatusHandler()
    {
        var dir = NewDir();
        try
        {
            using var repository = Repository(dir);
            var peer = new FakePeer();
            OpcodeTable.Build(typeof(LoginSession), typeof(WorldSession), typeof(ChatSession));
            var session = new LoginSession(peer, new ServerConfig(), repository, StarterKit.Default);
            var writer = new ByteWriter();
            Protocol18.WriteOperationRequest(writer, new OperationRequest(1));
            var decoded = Protocol18.ReadOperationRequest(new ByteReader(writer.ToArray()));
            session.OnRequest(decoded, encrypted: false);
            Check(peer.Responses.Count == 0 && peer.Events.Count == 1, "status replies with an event, not a response");
            var ev = peer.Events.Single();
            Check(ev.Parameters[1] is byte b && b == LoginSession.OnlineStatus, "online is byte 3");
            Check(ev.Parameters[252] is short s && s == 1, "status event Albion code 1");
            ProtocolTrace.Write("server->client", "event", ev.Code, ev.Parameters, false);
            Console.WriteLine("SYNTHETIC status: bytes -> Protocol18 -> LoginSession.StatusQuery -> event {1: byte 3, 252: short 1}");
        }
        finally { Directory.Delete(dir, recursive: true); }
    }
}
