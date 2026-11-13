using System.Diagnostics;
using System.Globalization;
using AlbionLocal.Photon;

namespace AlbionLocal.Server;

/// <summary>
/// What holds <see cref="World.Sync"/> right now, for <see cref="WorldWatchdog"/> (public issue #28): the world tick
/// (<see cref="WorldLoop"/>) or an operation request (<see cref="Network.AlbionSession.OnRequest"/>), with the time it started. Written
/// under the lock, read without it (a diagnostic, so a torn view between two writers is harmless).
/// </summary>
public static class WorldActivity
{
    /// <summary>A holder: what it is, whose (a session's name; null for the tick) and its start (<see cref="Stopwatch.GetTimestamp"/>).</summary>
    public readonly record struct Entry(string What, string? Who, long Since)
    {
        /// <summary>"who: what", put together only when a report is written.</summary>
        public string Text => Who is null ? What : $"{Who}: {What}";
    }

    // Three fields, not one object: naming the holder allocates nothing on the request path (a Move is a request, ten a second a player).
    private static string? _what, _who;
    private static long _since;

    /// <summary>The holder now; null = nothing known holds the lock.</summary>
    public static Entry? Current => Volatile.Read(ref _what) is { } what ? new Entry(what, Volatile.Read(ref _who), Volatile.Read(ref _since)) : null;

    /// <summary><paramref name="what"/> of <paramref name="who"/> starts; the previous holder, to hand back to <see cref="Restore"/> (a bot's request inside a tick).</summary>
    public static Entry? Enter(string what, string? who = null)
    {
        var previous = Current;
        Volatile.Write(ref _since, Stopwatch.GetTimestamp());
        Volatile.Write(ref _who, who);
        Volatile.Write(ref _what, what);
        return previous;
    }

    /// <summary>The holder before the matching <see cref="Enter"/> again.</summary>
    public static void Restore(Entry? previous)
    {
        Volatile.Write(ref _since, previous?.Since ?? 0);
        Volatile.Write(ref _who, previous?.Who);
        Volatile.Write(ref _what, previous?.What);
    }

    /// <summary><see cref="Enter"/> until the scope's end (<c>using var _ = WorldActivity.Hold(...)</c>).</summary>
    public static Scope Hold(string what, string? who = null) => new(Enter(what, who));

    /// <summary>Hands the previous holder back when disposed.</summary>
    public readonly struct Scope(Entry? previous) : IDisposable
    {
        public void Dispose() => Restore(previous);
    }
}

/// <summary>
/// Public issue #28: a stalled server (process alive, CPU idle, nothing answered) left no trace of what it waited for. This thread (its
/// own, not the pool's) looks every second at the world: when the world lock has been held, or the world has not ticked, or one log
/// line has been in writing for <see cref="Threshold"/> or longer, it writes what holds the lock (the tick and the section it is in
/// after <see cref="TickProfile.After"/>, or the request), how long, whether the logger is stuck and the thread pool's size and queue;
/// again every <see cref="RepeatEvery"/> while it lasts, and once more when the world runs again. While the logger itself is stuck (a
/// paused console blocks every thread that logs) the line goes to "&lt;log file&gt;.watchdog.txt" instead (<see cref="Log.WriteAside"/>).
/// </summary>
public sealed class WorldWatchdog : IDisposable
{
    /// <summary>How long a stall lasts before it is reported [choice: well above the 1-2 s ticks of the report].</summary>
    public static readonly TimeSpan Threshold = TimeSpan.FromSeconds(5);

    /// <summary>How often a stall that goes on is reported again.</summary>
    public static readonly TimeSpan RepeatEvery = TimeSpan.FromSeconds(15);

    private readonly World _world;
    private readonly Func<long> _lastTick;
    private readonly Thread _thread;
    private readonly ManualResetEventSlim _stop = new();

    /// <param name="lastTick">When the last tick ended (<see cref="Stopwatch.GetTimestamp"/>; <see cref="WorldLoop"/>).</param>
    public WorldWatchdog(World world, Func<long> lastTick)
    {
        _world = world;
        _lastTick = lastTick;
        _thread = new Thread(Run) { Name = "world watchdog", IsBackground = true };
    }

    public void Start() => _thread.Start();

    private void Run()
    {
        long? stalledSince = null, reported = null;
        while (!_stop.Wait(1000))
        {
            var now = Stopwatch.GetTimestamp();
            var holder = WorldActivity.Current;
            var message = Describe(Stopwatch.GetElapsedTime(_lastTick(), now), holder?.Text,
                holder is { } h ? Stopwatch.GetElapsedTime(h.Since, now) : TimeSpan.Zero, _world.Profile.After, Log.WritingFor,
                ThreadPool.ThreadCount, ThreadPool.PendingWorkItemCount);
            if (message is null)
            {
                if (reported is not null)
                    Write(string.Create(CultureInfo.InvariantCulture,
                        $"the world runs again after {Stopwatch.GetElapsedTime(stalledSince!.Value, now).TotalSeconds:0.0} s"));
                stalledSince = reported = null;
                continue;
            }
            stalledSince ??= now;
            if (reported is { } last && Stopwatch.GetElapsedTime(last, now) < RepeatEvery) continue;
            reported = now;
            Write(message);
        }
    }

    /// <summary>To the log, or beside it while the logger is stuck (a line written now would wait behind it, this thread too).</summary>
    private static void Write(string message)
    {
        if (Log.WritingFor >= TimeSpan.FromSeconds(1)) Log.WriteAside("watchdog: " + message);
        else Log.Warn("watchdog", message);
    }

    /// <summary>
    /// The report of one look, null when nothing is stalled: the world not ticked for <paramref name="sinceTick"/>, the lock held by
    /// <paramref name="holder"/> for <paramref name="held"/> (a tick: in the section after <paramref name="section"/>), a log line in writing
    /// for <paramref name="logging"/>, the thread pool's <paramref name="poolThreads"/> threads and <paramref name="poolQueued"/> queued items.
    /// </summary>
    public static string? Describe(TimeSpan sinceTick, string? holder, TimeSpan held, string? section, TimeSpan logging, int poolThreads, long poolQueued)
    {
        if (sinceTick < Threshold && held < Threshold && logging < Threshold) return null;
        var where = holder == WorldLoop.TickActivity && section is not null ? $" in the section after '{(section.Length == 0 ? "start" : section)}'" : "";
        var who = holder is null
            ? "nothing known holds the world lock (the world loop gets no thread, or a listener's flush is stuck)"
            : string.Create(CultureInfo.InvariantCulture, $"the world lock is held by {holder}{where} for {held.TotalSeconds:0.0} s");
        var log = logging >= TimeSpan.FromSeconds(1)
            ? string.Create(CultureInfo.InvariantCulture, $"one log line has been in writing for {logging.TotalSeconds:0.0} s (a paused console? press Esc or Enter in it)")
            : "the logger is free";
        return string.Create(CultureInfo.InvariantCulture,
            $"no world tick for {sinceTick.TotalSeconds:0.0} s; {who}; {log}; thread pool {poolThreads} threads, {poolQueued} queued");
    }

    public void Dispose()
    {
        _stop.Set();
        if (_thread.IsAlive) _thread.Join(2000);
    }
}
