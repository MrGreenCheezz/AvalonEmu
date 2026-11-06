using System.Diagnostics;
using AlbionLocal.Photon;

namespace AlbionLocal.Server;

/// <summary>
/// Runs <see cref="World.Update"/> every <see cref="ServerConfig.TickTime"/> (50 ms) under <see cref="World.Sync"/>,
/// the lock the handlers run under (ARCHITECTURE.md §4.4). That lock also guards the ENet acks, so a tick that
/// holds it longer than 20 ms is logged as a warning. Started by <see cref="ServerHost.Start"/>, stopped first
/// in its Dispose.
/// </summary>
public sealed class WorldLoop(World world) : IDisposable
{
    private static readonly TimeSpan SlowTick = TimeSpan.FromMilliseconds(20);

    private readonly CancellationTokenSource _cts = new();
    private Task? _run;

    /// <summary>Starts the ticks. The timer is made here, so a bad <see cref="ServerConfig.TickTime"/> throws in the caller.</summary>
    public void Start()
    {
        if (_run is not null) return;
        var timer = new PeriodicTimer(world.Config.TickTime);
        _run = Task.Run(() => Run(timer));
    }

    private async Task Run(PeriodicTimer timer)
    {
        try
        {
            while (await timer.WaitForNextTickAsync(_cts.Token))
            {
                TimeSpan took;
                string? slowest = null;
                lock (world.Sync)
                {
                    var start = Stopwatch.GetTimestamp();
                    try { world.Update(); }
                    catch (Exception e) { Log.Error("world", $"tick failed: {e}"); }
                    took = Stopwatch.GetElapsedTime(start);
                    if (took > SlowTick) slowest = world.Profile.Slowest();
                }
                if (took > SlowTick)
                    Log.Warn("world", $"tick took {took.TotalMilliseconds:0} ms (over {SlowTick.TotalMilliseconds:0} ms; the lock also holds back ENet acks): {slowest}");
            }
        }
        catch (OperationCanceledException) { }
        // Nothing ticks after this (no keepalives, no posted actions), so it must not go unnoticed.
        catch (Exception e) { Log.Error("world", $"world loop stopped: {e}"); }
        finally { timer.Dispose(); }
    }

    /// <summary>Stops the loop and waits for a running tick to finish. Safe to call twice.</summary>
    public void Dispose()
    {
        if (_cts.IsCancellationRequested) return;
        _cts.Cancel();
        try { _run?.Wait(1000); }
        catch (AggregateException) { }
    }
}
