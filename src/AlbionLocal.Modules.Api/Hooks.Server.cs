namespace AlbionLocal.Modules;

/// <summary>The server's life.</summary>
public interface IServerHooks
{
    /// <summary>The server has started (all modules loaded, listeners open). A module loaded later (a reload) does not get it.</summary>
    IHookRegistration OnStarted(Action<ServerArgs> handler, HookPriority priority = HookPriority.Normal);

    /// <summary>The server is stopping: last chance to save. Players are still in game.</summary>
    IHookRegistration OnStopping(Action<ServerArgs> handler, HookPriority priority = HookPriority.Normal);

    /// <summary>Every world tick (50 ms by default). Prefer <see cref="IModuleTimers"/> for anything periodic.</summary>
    IHookRegistration OnTick(Action<TickArgs> handler, HookPriority priority = HookPriority.Normal);
}

/// <summary>Args of a hook that has nothing to tell.</summary>
public sealed class ServerArgs;

/// <summary>A world tick: the world clock and the time since the last tick, in seconds.</summary>
public sealed class TickArgs(double seconds, double delta)
{
    /// <summary>The world clock, in seconds.</summary>
    public double Seconds { get; } = seconds;
    /// <summary>The time since the last tick, in seconds.</summary>
    public double DeltaSeconds { get; } = delta;
}
