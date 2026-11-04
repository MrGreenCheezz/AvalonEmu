using AlbionLocal.Modules;
using AlbionLocal.Photon.Serialization;
using AlbionLocal.Server.Entities;
using AlbionLocal.Server.Modules;
using AlbionLocal.Server.Protocol;

namespace AlbionLocal.Server.Network;

/// <summary>
/// The modules' way into a connection (docs/modules/README.md "Network"): one check on the request gate and on each send of <see cref="AlbionSession"/>, the rest here.
/// With nobody listening each costs one count check. The login server has no world and no modules.
/// </summary>
public abstract partial class AlbionSession
{
    /// <summary>The modules of this session's world; null where there are none (the login server).</summary>
    protected virtual ModuleHost? ModuleGate => null;

    /// <summary>The player of this connection as the modules see it; null before the Join.</summary>
    protected virtual Player? ModulePlayer => null;

    /// <summary>Network.OnRequest: true when a module has taken the request (its answer sent here): no handler runs.</summary>
    private bool ModuleRequest(OperationCodes code, AlbionRequest? message, OperationRequest request, int? requestId, bool encrypted)
    {
        if (ModuleGate is not { } modules || !modules.Hooks.Request.Any || ModulePlayer is { IsBot: true }) return false;
        var name = code.ToString();
        if (!modules.Hooks.Request.Wants(name)) return false;
        var args = new RequestArgs(ModulePlayer is { } p ? modules.Wrap(p) : null, name, (int)code, message, request.Parameters);
        modules.Hooks.Request.Raise(name, args);
        if (!args.IsCancelled) return false;
        Photon.Log.Debug(Name, $"<- {code}: taken by module {args.CancelledBy}");
        if (args.CancelReason is { Length: > 0 } why) SendEvent(new SystemMessageEvent { d0p = why, d0q = 1 }, encrypted);
        if (args.Response is AlbionResponse response) Peer.SendOperationResponse(AlbionSerializer.BuildResponse(response, requestId, code), encrypted);
        else if (args.Response is not null) Photon.Log.Error(Name, $"module {args.CancelledBy} answered {code} with a {args.Response.GetType().Name}, not a protocol response: no answer sent");
        else if (args.RejectCode is { } rc) Peer.SendOperationResponse(AlbionSerializer.BuildError(code, (short)rc, requestId, args.CancelReason), encrypted);
        return true;
    }

    private bool _inModuleEvent;

    /// <summary>Network.OnEvent: true when a module keeps <paramref name="ev"/> from this client; it may come back as another event.</summary>
    private bool ModuleEvent(ref AlbionEvent ev)
    {
        if (ModuleGate is not { } modules || !modules.Hooks.OutEvent.Any) return false;
        if (_inModuleEvent) return false; // what a handler sends from here goes straight out, or a message from OnEvent would set off OnEvent for ever
        var (name, code) = ModuleNetwork.Describe(ev);
        if (!modules.Hooks.OutEvent.Wants(name)) return false;
        var args = new OutgoingArgs(ModulePlayer is { } p ? modules.Wrap(p) : null, name, code, ev);
        _inModuleEvent = true;
        modules.Hooks.OutEvent.Raise(name, args);
        _inModuleEvent = false;
        if (args.IsCancelled) return true;
        if (args.Message is AlbionEvent replaced) ev = replaced;
        else Photon.Log.Error(Name, $"a module replaced event {name} with a {args.Message?.GetType().Name}, not a protocol event: the original goes out");
        return false;
    }

    /// <summary>Network.OnResponse: true when a module keeps <paramref name="response"/> back; it may come back as another response.</summary>
    private bool ModuleResponse(OperationCodes code, ref AlbionResponse response)
    {
        if (ModuleGate is not { } modules || !modules.Hooks.OutResponse.Any) return false;
        var name = code.ToString();
        if (!modules.Hooks.OutResponse.Wants(name)) return false;
        var args = new OutgoingArgs(ModulePlayer is { } p ? modules.Wrap(p) : null, name, (int)code, response);
        modules.Hooks.OutResponse.Raise(name, args);
        if (args.IsCancelled) return true;
        if (args.Message is AlbionResponse replaced) response = replaced;
        else Photon.Log.Error(Name, $"a module replaced response {name} with a {args.Message?.GetType().Name}, not a protocol response: the original goes out");
        return false;
    }
}
