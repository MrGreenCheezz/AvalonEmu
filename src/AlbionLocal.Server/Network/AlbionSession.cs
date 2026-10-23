using AlbionLocal.Photon;
using AlbionLocal.Photon.Serialization;
using AlbionLocal.Server.Content;
using AlbionLocal.Server.Game;
using AlbionLocal.Server.Protocol;

namespace AlbionLocal.Server.Network;

/// <summary>
/// One client connection, created by its listener's factory when the peer connects and holding the
/// connection's state. Albion operation dispatch goes through the session type's <see cref="OpcodeTable"/>
/// (methods marked [Handles(OperationCodes.X, Status = …)]); a request that comes before the session has
/// reached the handler's <see cref="Status"/> is logged and dropped, and so, quietly, is every request of a
/// <see cref="SessionStatus.Transferring"/> session. Unhandled operations that have a
/// response type get an empty success response so the client's callbacks do not hang; they are logged as
/// warnings and counted per operation. Those listed in content/opcode-stubs.json (<see cref="Stubs"/>) get the
/// configured answer instead (a filled response, none for a "silent" or "ignore" one, the bare rc-0 answer for an "ack" one), without
/// the warning, logged at debug level and counted apart (<see cref="OpcodeTable.Stubbed"/>).
/// </summary>
public abstract class AlbionSession : IPhotonPeerHandler
{
    private readonly OpcodeTable _opcodes;

    protected AlbionSession(IPeer peer, string name)
    {
        Peer = peer;
        Name = name;
        _opcodes = OpcodeTable.For(GetType());
    }

    protected IPeer Peer { get; }

    protected string Name { get; }

    /// <summary>The character this connection selected (login) or joined with (game); null before that.</summary>
    public Character? Character { get; protected set; }

    /// <summary>How far this connection has got; the handlers set it (Login → Authed, Join → InWorld).</summary>
    public SessionStatus Status { get; protected set; }

    /// <summary>
    /// The responses of content/opcode-stubs.json for operations without a handler (<see cref="ServerHost"/> sets the
    /// loaded ones); <see cref="OpcodeStubs.None"/> = every such operation gets the plain empty response with a warning.
    /// </summary>
    public OpcodeStubs Stubs { get; init; } = OpcodeStubs.None;

    /// <summary>
    /// Photon operation and event code of the keepalive, which carries no Albion code: the client sends op 2
    /// {0: counter, 1: 0.0} every second (one counter for all its connections), the game server event 2
    /// (WorldSession.UpdateKeepalive). Op 2 is dropped on any session, login or game.
    /// </summary>
    protected const byte KeepaliveCode = 2;

    void IPhotonPeerHandler.OnOperationRequest(PhotonPeer peer, OperationRequest request, bool encrypted) => OnRequest(request, encrypted);

    void IPhotonPeerHandler.OnDisconnected(PhotonPeer peer, string reason) => OnDisconnected(reason);

    /// <summary>One operation request of this connection (Albion operations travel as Photon operation 1).</summary>
    public void OnRequest(OperationRequest request, bool encrypted)
    {
        ProtocolTrace.Write("client->server", "request", request.Code, request.Parameters, encrypted);
        if (request.Code == KeepaliveCode)
        {
            // No response, as on the official server. Taken before the Albion parse, which knows no Photon codes:
            // without key 253 it would come out as op Unused (the login's status query, the game's unhandled warning).
            Log.Trace(Name, $"<- keepalive {request.Parameters.GetValueOrDefault((byte)0)}");
            return;
        }
        AlbionRequest? msg;
        OperationCodes code;
        int? requestId;
        try { msg = AlbionSerializer.ParseRequest(request, out code, out requestId); }
        catch (Exception e)
        {
            Log.Error(Name, $"cannot parse request: {e.Message}\n{MessageDump.Describe(request.Parameters)}");
            return;
        }
        if (Status == SessionStatus.Transferring)
        {
            // The client keeps sending for a moment after a ChangeCluster response (one more Move, 0.1 s later; SPEC
            // stage 3 §4.1) and then drops this connection: no handler, no stub, no unhandled default runs any more.
            Log.Debug(Name, $"<- {code}: the session is transferring, dropped");
            return;
        }
        var handler = _opcodes.Find(code);
        var quiet = handler is { Quiet: true };
        var head = $"<- {code}{(requestId is { } id ? $" #{id}" : "")}{(encrypted ? " (enc)" : "")}";
        if (quiet) Log.Trace(Name, head); else Log.Info(Name, head);
        if (Log.Level <= (quiet ? LogLevel.Trace : LogLevel.Debug))
            Log.Write(quiet ? LogLevel.Trace : LogLevel.Debug, Name, msg is null ? MessageDump.Describe(request.Parameters) : MessageDump.Describe(msg));

        AlbionResponse? resp;
        if (handler is not null)
        {
            if (Status < handler.Status)
            {
                Log.Warn(Name, $"{code} needs status {handler.Status}, the session is {Status}: dropped");
                return;
            }
            RequestId = requestId;
            RequestEncrypted = encrypted;
            try { resp = handler.Invoke(this, msg); }
            catch (Exception e)
            {
                Log.Error(Name, $"{code} handler failed: {e}");
                Peer.SendOperationResponse(AlbionSerializer.BuildError(code, -1, requestId, e.Message), encrypted);
                return;
            }
        }
        else if (Stubs.Respond(code) is { } stub)
        {
            _opcodes.CountStubbed(code);
            if (stub.Response is null)
            {
                Log.Debug(Name, stub.Kind == OpcodeStubs.StubKind.Ignore
                    ? $"stub {code}: ignored (opcode-stubs.json: ignore, no answer needed)"
                    : $"stub {code}: no answer (opcode-stubs.json: silent, as the official server)");
                return;
            }
            Log.Debug(Name, stub.Kind == OpcodeStubs.StubKind.Ack
                ? $"stub {code}: sending the bare answer from opcode-stubs.json{(stub.EchoRequestId ? "" : ", without the request id")}"
                : $"stub {code}: sending {stub.Response.GetType().Name} from opcode-stubs.json");
            resp = stub.Response;
            if (!stub.EchoRequestId) requestId = null;
        }
        else
        {
            _opcodes.CountUnhandled(code);
            if (!AlbionSerializer.ResponseTypes.TryGetValue(code, out var rt))
            {
                Log.Warn(Name, $"unhandled {code} without response type: ignored");
                return;
            }
            Log.Warn(Name, $"unhandled {code}: sending empty {rt.Name}");
            resp = ((AlbionResponse)Activator.CreateInstance(rt)!).FillEmpty();
        }
        if (resp is null) return;
        Peer.SendOperationResponse(AlbionSerializer.BuildResponse(resp, requestId, code), encrypted);
        Log.Debug(Name, $"-> {code} rc {resp.ReturnCode}");
        AfterResponse(code, resp);
    }

    /// <summary>Called once a response has been queued, for events that must follow it.</summary>
    protected virtual void AfterResponse(OperationCodes code, AlbionResponse resp) { }

    /// <summary>Request id (key 255) of the request whose handler runs now; null when it has none. Kept for an answer sent later.</summary>
    protected int? RequestId { get; private set; }

    /// <summary>Whether the request whose handler runs now came encrypted (its response goes out the same way).</summary>
    protected bool RequestEncrypted { get; private set; }

    /// <summary>
    /// A response sent outside its handler call: a deferred answer with the request id the handler kept
    /// (<see cref="RequestId"/>; the handler returned null then), or with <paramref name="requestId"/> null a push the
    /// client did not ask for (the server-initiated ChangeCluster of .tele, SPEC stage 3 §2.6).
    /// </summary>
    protected void SendResponse(OperationCodes code, AlbionResponse resp, int? requestId, bool encrypted)
    {
        if (Peer is BotPeer) return; // stage I: a bot's session has no client (nothing is serialized for it)
        Peer.SendOperationResponse(AlbionSerializer.BuildResponse(resp, requestId, code), encrypted);
        Log.Debug(Name, $"-> {code}{(requestId is { } id ? $" #{id}" : " (no request id)")} rc {resp.ReturnCode}");
    }

    protected void SendEvent(AlbionEvent ev, bool encrypt = false)
    {
        if (Peer is BotPeer) return; // stage I: a bot's session has no client (nothing is serialized for it)
        Peer.SendEvent(AlbionSerializer.BuildEvent(ev), encrypt);
        Log.Debug(Name, $"-> event {ev.GetType().Name}");
    }

    /// <summary>
    /// Ends this connection from the server (a newer Login of its account, a ban): DISCONNECT with reserved byte 1
    /// (DisconnectByServerLogic, the client's 1043; never 2, which makes it reconnect). <see cref="OnDisconnected"/> follows
    /// as for any end of the connection.
    /// </summary>
    public void Close(string why)
    {
        Log.Info(Name, $"peer {Peer.PeerId} closed by the server: {why}");
        Peer.Disconnect(DisconnectByServerLogic);
    }

    /// <summary>DISCONNECT reserved byte of a server-ended connection (client status 1043, NOTES-protocol.md "Logout").</summary>
    public const byte DisconnectByServerLogic = 1;

    /// <summary>The connection is gone (client left, timeout or <see cref="IPeer.Disconnect"/>).</summary>
    public virtual void OnDisconnected(string reason) =>
        Log.Info(Name, $"peer {Peer.PeerId} disconnected: {reason}");
}
