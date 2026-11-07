using System.Net;
using AlbionLocal.Photon.Serialization;
using AlbionLocal.Server.Network;

namespace AlbionLocal.SelfTest;

/// <summary>A connection without a socket: records what a session sends, for driving sessions directly.</summary>
public sealed class FakePeer : IPeer
{
    public ushort PeerId => 1;
    public string AppId => "fake";
    public IPEndPoint EndPoint { get; } = new(IPAddress.Loopback, 0);

    public List<OperationResponse> Responses { get; } = [];

    /// <summary>Whether each of <see cref="Responses"/> was sent encrypted.</summary>
    public List<bool> ResponseEncrypted { get; } = [];

    public List<EventData> Events { get; } = [];

    /// <summary>The responses and events in the order they were sent (<see cref="OperationResponse"/> or <see cref="EventData"/>).</summary>
    public List<object> Sequence { get; } = [];

    /// <summary>Events sent with <see cref="SendEventUnsequenced"/> and their channel, kept out of <see cref="Events"/>.</summary>
    public List<(EventData Event, byte Channel)> Unsequenced { get; } = [];

    /// <summary>Reason of the <see cref="Disconnect"/>; null while connected.</summary>
    public byte? DisconnectReason { get; private set; }

    /// <summary>
    /// Called by <see cref="Disconnect"/> as on the UDP path (EnetConnection.Close → PhotonServer →
    /// handler.OnDisconnected): wire it to the session's OnDisconnected to reach its leave/save code.
    /// </summary>
    public Action<string>? Disconnected { get; set; }

    public void SendOperationResponse(OperationResponse resp, bool encrypt)
    {
        Responses.Add(resp);
        ResponseEncrypted.Add(encrypt);
        Sequence.Add(resp);
    }

    /// <summary>
    /// Whether the periodic PlayerCounts (276, every 10 s of the world clock) is recorded; off by default, so that the tests of an exact event
    /// order over a long run of the clock are not interleaved with it (the ops audit, which checks it, turns it on).
    /// </summary>
    public bool KeepPlayerCounts { get; set; }

    public void SendEvent(EventData ev, bool encrypt = false)
    {
        if (!KeepPlayerCounts && ev.Parameters.TryGetValue(AlbionLocal.Server.Protocol.ReservedKeys.EventCode, out var code) && Convert.ToInt32(code) == (int)AlbionLocal.Server.Protocol.EventCodes.PlayerCounts)
            return;
        Events.Add(ev);
        Sequence.Add(ev);
    }

    public void SendEventUnsequenced(EventData ev, byte channel) => Unsequenced.Add((ev, channel));

    /// <summary>Events sent with <see cref="SendEventUnreliable"/> and their channel; they are in <see cref="Events"/> as well, in order.</summary>
    public List<(EventData Event, byte Channel)> Unreliable { get; } = [];

    public void SendEventUnreliable(EventData ev, byte channel)
    {
        Unreliable.Add((ev, channel));
        SendEvent(ev);
    }

    public void Disconnect(byte reason = 4)
    {
        if (DisconnectReason is not null) return; // closed already, as EnetConnection.Disconnect
        DisconnectReason = reason;
        Disconnected?.Invoke("local disconnect");
    }
}
