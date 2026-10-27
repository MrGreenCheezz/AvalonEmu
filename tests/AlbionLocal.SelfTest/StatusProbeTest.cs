using System.Net;
using AlbionLocal.Photon;
using AlbionLocal.Photon.Serialization;
using AlbionLocal.Server;
using AlbionLocal.Server.Content;
using AlbionLocal.Server.Network;
using AlbionLocal.Server.Protocol;
using static AlbionLocal.SelfTest.AlbionTests;

namespace AlbionLocal.SelfTest;

/// <summary>The status probe uses its own Photon connection, without an account login.</summary>
public static class StatusProbeTest
{
    private static void Check(bool condition, string message) => AlbionTests.Check(condition, message);

    public static IEnumerable<(string, Action)> All =>
    [
        ("status probe: real UDP status application receives online byte without logging in", RoundTrip),
    ];

    private static void RoundTrip()
    {
        var dir = NewDir();
        try
        {
            using var characters = Repository(dir);
            PhotonPeer? probe = null;
            using var server = new PhotonServer(new IPEndPoint(IPAddress.Loopback, 0), "status-test", peer =>
            {
                probe = peer;
                return new LoginSession(new PhotonPeerAdapter(peer), new ServerConfig(), characters, StarterKit.Default);
            });
            server.Start();
            using var client = new TestPhotonClient(server.EndPoint, appId: "Albion.StatusServer");
            client.Connect();
            Check(client.EncryptionDone.Task.Wait(10000), "the status application establishes a real Photon connection");
            client.SendOperation(new OperationRequest(AlbionSerializer.PhotonOperationCode, new()), encrypt: false);
            Check(client.Events.TryTake(out var status, 5000) && status.Code == AlbionSerializer.PhotonEventCode
                  && status.Parameters.Count == 2 && status.Parameters[1] is (byte)3
                  && status.Parameters[ReservedKeys.EventCode] is (short)1,
                "status query reaches the server and returns the native online event and wire types over UDP");
            Check(probe?.AppId == "Albion.StatusServer" && client.Responses.Count == 0,
                "the separate status application needs no account login or operation response");
        }
        finally { Directory.Delete(dir, true); }
    }
}
