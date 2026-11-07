using System.Net;
using System.Net.Sockets;
using AlbionLocal.Photon;
using AlbionLocal.Photon.Crypto;
using AlbionLocal.Photon.Enet;
using AlbionLocal.Photon.Serialization;

namespace AlbionLocal.SelfTest;

/// <summary>
/// Minimal Photon client that follows the real client's sequence: CONNECT, classic 41-byte Init,
/// InitEncryption (internal op 0) after the InitResponse, then encrypted operations.
/// Optional packet loss exercises resends.
/// </summary>
public sealed class TestPhotonClient : IDisposable
{
    private readonly UdpClient _udp;
    private readonly EnetConnection _conn;
    private readonly DiffieHellmanCrypto _crypto = new();
    private readonly object _sync = new();
    private readonly CancellationTokenSource _cts = new();
    private readonly Random _rng = new(1234);
    private readonly Task _loop;
    private bool _abandoned;
    private readonly string _appId;

    /// <param name="localPort">The client's own UDP port; 0 = any free one (a socket on a port used before plays a client the OS gave its old port again).</param>
    /// <param name="appId">The Init's application name (the real client's chat peer sends "Albion.ChatServer").</param>
    public TestPhotonClient(IPEndPoint server, double dropRate = 0, int localPort = 0, string appId = "LoadBalancing")
    {
        _appId = appId;
        _udp = new UdpClient(new IPEndPoint(IPAddress.Loopback, localPort));
        _udp.Connect(server);
        DropRate = dropRate;
        _conn = new EnetConnection(isServer: false, d =>
        {
            if (_rng.NextDouble() >= DropRate) _udp.Send(d, d.Length);
        }, "test-client", challenge: Random.Shared.Next(1, int.MaxValue));
        _conn.MessageReceived += OnMessage;
        _conn.Disconnected += reason => Closed.TrySetResult(reason);
        _loop = Task.Run(Loop);
    }

    public double DropRate { get; set; }

    /// <summary>The client's own UDP port.</summary>
    public int LocalPort => ((IPEndPoint)_udp.Client.LocalEndPoint!).Port;
    public TaskCompletionSource InitDone { get; } = new();
    public TaskCompletionSource EncryptionDone { get; } = new();

    /// <summary>Set with the close reason once the connection is closed (by either side).</summary>
    public TaskCompletionSource<string> Closed { get; } = new();
    public System.Collections.Concurrent.BlockingCollection<OperationResponse> Responses { get; } = new();
    public System.Collections.Concurrent.BlockingCollection<EventData> Events { get; } = new();

    /// <summary>The game server's Photon event 2 (keepalive, no key 252), kept out of <see cref="Events"/>.</summary>
    public System.Collections.Concurrent.BlockingCollection<EventData> Keepalives { get; } = new();

    private async Task Loop()
    {
        var flush = Task.Run(async () =>
        {
            while (!_cts.IsCancellationRequested)
            {
                await Task.Delay(10);
                lock (_sync) _conn.Flush();
            }
        });
        while (!_cts.IsCancellationRequested)
        {
            UdpReceiveResult r;
            try { r = await _udp.ReceiveAsync(_cts.Token); }
            catch { break; }
            if (_rng.NextDouble() < DropRate) continue;
            lock (_sync)
            {
                _conn.Receive(r.Buffer);
                _conn.Flush();
            }
        }
        await flush;
    }

    public void Connect()
    {
        lock (_sync)
        {
            _conn.Connected += SendInit;
            _conn.Connect();
            _conn.Flush();
        }
    }

    private void SendInit()
    {
        var init = new byte[41];
        init[0] = 0xF3;
        init[1] = 0;
        init[2] = 1; init[3] = 18;
        init[4] = 0x10;
        System.Text.Encoding.ASCII.GetBytes(_appId).CopyTo(init.AsSpan(9));
        _conn.Send(init, 0, reliable: true);
    }

    private void OnMessage(byte[] data, byte channel)
    {
        var type = (MessageType)(data[1] & 0x7F);
        var body = (data[1] & 0x80) != 0 ? _crypto.Decrypt(data.AsSpan(2)) : data.AsSpan(2).ToArray();
        switch (type)
        {
            case MessageType.InitResponse:
                InitDone.TrySetResult();
                // EstablishEncryption: internal op 0 with ClientKey (1).
                SendMessage(MessageType.InternalOperationRequest,
                    new OperationRequest(0, new() { [1] = _crypto.PublicKey }), encrypt: false);
                break;
            case MessageType.InternalOperationResponse:
                {
                    var resp = Protocol18.ReadOperationResponse(new ByteReader(body));
                    _crypto.DeriveSharedKey((byte[])resp.Parameters[1]!);
                    EncryptionDone.TrySetResult();
                    break;
                }
            case MessageType.OperationResponse:
                Responses.Add(Protocol18.ReadOperationResponse(new ByteReader(body)));
                break;
            case MessageType.Event:
                {
                    var ev = Protocol18.ReadEventData(new ByteReader(body));
                    // The periodic PlayerCounts (276, every 10 s) is kept out as FakePeer does, so the exact event orders hold.
                    if (ev.Parameters.TryGetValue(252, out var code) && Convert.ToInt32(code) == (int)AlbionLocal.Server.Protocol.EventCodes.PlayerCounts) break;
                    (ev.Code == 2 && !ev.Parameters.ContainsKey(252) ? Keepalives : Events).Add(ev);
                    break;
                }
        }
    }

    public void SendOperation(OperationRequest req, bool encrypt = true)
    {
        lock (_sync)
        {
            SendMessage(MessageType.Operation, req, encrypt);
            _conn.Flush();
        }
    }

    private void SendMessage(MessageType type, OperationRequest req, bool encrypt)
    {
        var bw = new ByteWriter();
        Protocol18.WriteOperationRequest(bw, req);
        var w = new ByteWriter();
        w.Byte(0xF3);
        if (encrypt)
        {
            w.Byte((byte)((byte)type | 0x80));
            w.Bytes(_crypto.Encrypt(bw.Span));
        }
        else
        {
            w.Byte((byte)type);
            w.Bytes(bw.Span);
        }
        _conn.Send(w.ToArray(), 0, reliable: true);
    }

    /// <summary>Closes the socket without a DISCONNECT, as a client that went away: the server keeps the connection until it times out.</summary>
    public void Abandon()
    {
        _abandoned = true;
        _cts.Cancel();
        _udp.Dispose();
        try { _loop.Wait(1000); } catch { }
    }

    public void Dispose()
    {
        if (!_abandoned) lock (_sync) _conn.Disconnect();
        _cts.Cancel();
        _udp.Dispose();
        try { _loop.Wait(1000); } catch { }
    }
}
