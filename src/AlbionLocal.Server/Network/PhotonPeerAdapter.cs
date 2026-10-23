using System.Net;
using AlbionLocal.Photon;
using AlbionLocal.Photon.Serialization;
using AlbionLocal.Server.Protocol;

namespace AlbionLocal.Server.Network;

/// <summary><see cref="IPeer"/> over a connected <see cref="PhotonPeer"/>; every call goes straight through.</summary>
public sealed class PhotonPeerAdapter(PhotonPeer peer) : IPeer
{
    public ushort PeerId => peer.PeerId;
    public string AppId => peer.AppId;
    public IPEndPoint EndPoint => peer.EndPoint;

    public void SendOperationResponse(OperationResponse resp, bool encrypt)
    {
        ProtocolTrace.Write("server->client", "response", resp.Code, resp.Parameters, encrypt, resp.ReturnCode);
        peer.SendOperationResponse(resp, encrypt);
    }

    public void SendEvent(EventData ev, bool encrypt = false)
    {
        ProtocolTrace.Write("server->client", "event", ev.Code, ev.Parameters, encrypt);
        peer.SendEvent(ev, encrypt);
    }

    public void SendEventUnsequenced(EventData ev, byte channel)
    {
        ProtocolTrace.Write("server->client", "event", ev.Code, ev.Parameters, false);
        peer.SendEventUnsequenced(ev, channel);
    }

    public void SendEventUnreliable(EventData ev, byte channel)
    {
        ProtocolTrace.Write("server->client", "event", ev.Code, ev.Parameters, false);
        peer.SendEvent(ev, encrypt: false, reliable: false, channel);
    }

    public void Disconnect(byte reason = 4) => peer.Disconnect(reason);
}
