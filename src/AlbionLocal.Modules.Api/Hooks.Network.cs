namespace AlbionLocal.Modules;

/// <summary>
/// Everything a client and the game server say to each other, with no change to the server needed: every operation a client sends (also one
/// the server has no handler for), every event and response it gets. Ids are the protocol's names without a suffix: "CastStart",
/// "HealthUpdate" (the enums OperationCodes and EventCodes, <c>.modules ops</c> lists them). A module with "coreAccess" can read the typed
/// messages with <see cref="NetworkMessageArgs.As{T}"/>; their field names are the client's obfuscated ones and may change with a client
/// update. Prefer a typed hook of another group when there is one. Not fired for bots, which have no client. The movement stream's fast path,
/// the keepalive and the login server are not covered.
/// </summary>
public interface INetworkHooks
{
    /// <summary>
    /// An operation arrived from a player, before its handler: <see cref="RequestArgs.Reject"/> or <see cref="RequestArgs.Respond"/> stop the handler.
    /// Keep them cheap: a client sends several per second.
    /// </summary>
    /// <example>
    /// Refuses casts of plain players during an event:
    /// <code><![CDATA[
    /// Hooks.Network.OnRequest("CastStart", e =>
    /// {
    ///     if (e.Player is { Level: PlayerLevel.Player }) e.Reject(reason: "Casting is off during this event.");
    /// });
    /// ]]></code>
    /// </example>
    IHookRegistration OnRequest(Action<RequestArgs> handler, HookPriority priority = HookPriority.Normal);
    /// <summary>As OnRequest, for one operation (its name such as "CastStart", or a mask with * and ?).</summary>
    IHookRegistration OnRequest(string operation, Action<RequestArgs> handler, HookPriority priority = HookPriority.Normal);

    /// <summary>An event is about to go to one client: <see cref="CancellableArgs.Cancel"/> keeps it from that client, <see cref="OutgoingArgs.Replace"/> sends another. Fires for every event of every client: register for one id where possible.</summary>
    IHookRegistration OnEvent(Action<OutgoingArgs> handler, HookPriority priority = HookPriority.Normal);
    /// <summary>As OnEvent, for one event (its name such as "HealthUpdate", or a mask with * and ?).</summary>
    IHookRegistration OnEvent(string eventName, Action<OutgoingArgs> handler, HookPriority priority = HookPriority.Normal);

    /// <summary>A response is about to go to one client: <see cref="CancellableArgs.Cancel"/> keeps it back, <see cref="OutgoingArgs.Replace"/> sends another.</summary>
    IHookRegistration OnResponse(Action<OutgoingArgs> handler, HookPriority priority = HookPriority.Normal);
    /// <summary>As OnResponse, for the responses to one operation (its name such as "CastStart", or a mask with * and ?).</summary>
    IHookRegistration OnResponse(string operation, Action<OutgoingArgs> handler, HookPriority priority = HookPriority.Normal);

    /// <summary>The operation names this server knows (for a typo's hint and <c>.modules ops</c>).</summary>
    IReadOnlyCollection<string> OperationNames { get; }

    /// <summary>The event names this server knows.</summary>
    IReadOnlyCollection<string> EventNames { get; }
}

/// <summary>A message on its way, for one player.</summary>
public abstract class NetworkMessageArgs(IPlayer? player, string name, int code, object? message) : CancellableArgs
{
    /// <summary>The player of the connection; null before it has joined.</summary>
    public IPlayer? Player { get; } = player;

    /// <summary>The operation's or event's name ("CastStart", "HealthUpdate").</summary>
    public string Name { get; } = name;

    /// <summary>Its number in the protocol.</summary>
    public int Code { get; } = code;

    /// <summary>The parsed message; null for an operation without a message class. Its type lives in the server's protocol assembly: see <see cref="As{T}"/>.</summary>
    public object? Message { get; protected set; } = message;

    /// <summary>The message as <typeparamref name="T"/> (a class of AlbionLocal.Server.Protocol, for a module with "coreAccess"); null when it is another one.</summary>
    public T? As<T>() where T : class => Message as T;
}

/// <summary>An operation from a client (<see cref="INetworkHooks.OnRequest(Action{RequestArgs}, HookPriority)"/>).</summary>
public sealed class RequestArgs(IPlayer? player, string name, int code, object? message, IReadOnlyDictionary<byte, object?> parameters) : NetworkMessageArgs(player, name, code, message)
{
    /// <summary>The raw parameters of the operation, by the protocol's key. Works without "coreAccess".</summary>
    public IReadOnlyDictionary<byte, object?> Parameters { get; } = parameters;

    /// <summary>The return code <see cref="Reject"/> asked for; null when none.</summary>
    public int? RejectCode { get; private set; }

    /// <summary>The response <see cref="Respond"/> gave; null when none.</summary>
    public object? Response { get; private set; }

    /// <summary>
    /// The handler does not run and the client gets a failure with <paramref name="returnCode"/> (-1 by default; the client shows the code's own message
    /// for that operation) and <paramref name="reason"/> as a chat line when it is given.
    /// </summary>
    public void Reject(int returnCode = -1, string? reason = null)
    {
        RejectCode = returnCode;
        Cancel(reason);
    }

    /// <summary>The handler does not run and the client gets <paramref name="response"/> (a response class of the protocol, for a module with "coreAccess").</summary>
    public void Respond(object response)
    {
        Response = response;
        Cancel();
    }

    /// <summary>The handler does not run and the client gets no answer: only for an operation that needs none, or the client may wait for it.</summary>
    public void Drop(string? reason = null) => Cancel(reason);
}

/// <summary>An event or response on its way to one client.</summary>
public sealed class OutgoingArgs(IPlayer? player, string name, int code, object message) : NetworkMessageArgs(player, name, code, message)
{
    /// <summary>
    /// Sends <paramref name="message"/> (a class of the protocol of the same kind, for a module with "coreAccess") instead. The original may be going to other
    /// clients as the same object: never change it, replace it.
    /// </summary>
    public void Replace(object message) => Message = message;
}
