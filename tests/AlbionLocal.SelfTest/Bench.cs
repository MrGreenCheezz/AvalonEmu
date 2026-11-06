using System.Globalization;
using System.Numerics;
using AlbionLocal.Server;
using AlbionLocal.Server.Bots;
using AlbionLocal.Server.Data;
using AlbionLocal.Server.Entities;
using AlbionLocal.Server.Systems;

namespace AlbionLocal.SelfTest;

/// <summary>
/// The tick benchmark (--bench=&lt;bots&gt;), not a test: the pve preset on 1207 (Yellow, mobs and nodes) with four humans spread
/// over the map and that many GM bots (gatherers and patrols by halves), 60 game seconds of 50 ms ticks as fast as they run, then
/// the tick's average and peak and <see cref="AlbionLocal.Server.TickProfile"/>'s sections. The real server runs the same tick on
/// a 50 ms timer, so an average near 50 ms is the limit.
/// </summary>
public static class Bench
{
    private static readonly ClusterPvp Yellow = new(PvpRule.PvpAllowed, PlayersAreLethal: false, WarningType: "yellow");

    public static int Run(int bots, double seconds = 60)
    {
        var options = new BotOptions { Enabled = true, MaxTotal = Math.Max(400, bots), MaxPerMap = Math.Max(40, bots), CrimesAgainstBots = false };
        using var f = new BotTest.Field(options, BotTest.NoPopulation, Yellow, cluster: "1207");
        var nav = f.Nav();
        var template = AlbionTests.Client.Clusters!.Get("1207");
        var spots = template.MobSpawnPoints.Select(p => p.Position).Concat(template.Harvestables.Select(h => h.Position))
            .Select(p => nav.Grid.Nearest(p, 20)).OfType<Vector2>().ToList();
        if (spots.Count == 0) throw new InvalidOperationException("1207: no walkable spot by its mobs and nodes");
        var random = new Random(5);
        for (var i = 0; i < 4; i++) f.Join($"Bench{i}", spots[i * spots.Count / 4]);
        var made = 0;
        lock (f.World.Sync)
            for (var i = 0; i < bots * 3 && made < bots; i++)
                if (f.World.Bots.Spawn(f.Map, made % 2 == 0 ? BotRole.Gatherer : BotRole.Patrol, 4, spots[random.Next(spots.Count)], 0) is not null) made++;
        var mobs = f.Map.Objects.Values.OfType<Mob>().Count();
        Console.WriteLine(string.Create(CultureInfo.InvariantCulture,
            $"bench 1207: {made} bots ({f.Map.Bots.Count} on the map), 4 humans, {mobs} mobs, {seconds:0} game s of 50 ms ticks"));
        f.Run(5); // warm-up: the JIT, the first paths and aggro
        f.World.Profile.Reset();
        var (gen0, gen1, gen2) = (GC.CollectionCount(0), GC.CollectionCount(1), GC.CollectionCount(2));
        var (allocated, paused) = (GC.GetTotalAllocatedBytes(), GC.GetTotalPauseDuration());
        var cpu = ThreadCpu();
        var wall = System.Diagnostics.Stopwatch.StartNew();
        var slow = new List<(double Ms, string Sections)>();
        f.Run(seconds, () => // the slow ticks' sections
        {
            if (f.World.Profile.Last > 20) slow.Add((f.World.Profile.Last, f.World.Profile.Slowest(4)));
        });
        wall.Stop();
        cpu = ThreadCpu() - cpu;
        var (average, max, sections) = f.World.Profile.Summary();
        Console.WriteLine(string.Create(CultureInfo.InvariantCulture,
            $"tick: average {average:0.00} ms, peak {max:0.0} ms over {f.World.Profile.Ticks} ticks ({wall.Elapsed.TotalSeconds:0.0} s wall for {seconds:0} game s); {f.Map.Bots.Count} bots left"));
        Console.WriteLine(string.Create(CultureInfo.InvariantCulture,
            $"cpu: {cpu.TotalMilliseconds / f.World.Profile.Ticks:0.00} ms per tick on the world thread (the wall times above also hold the machine's stalls)"));
        Console.WriteLine(string.Create(CultureInfo.InvariantCulture,
            $"gc: {(GC.GetTotalAllocatedBytes() - allocated) / 1048576.0 / f.World.Profile.Ticks:0.00} MB allocated per tick, collections {GC.CollectionCount(0) - gen0}/{GC.CollectionCount(1) - gen1}/{GC.CollectionCount(2) - gen2} (gen 0/1/2), paused {(GC.GetTotalPauseDuration() - paused).TotalMilliseconds:0} ms, heap {GC.GetTotalMemory(false) / 1048576.0:0} MB, {(System.Runtime.GCSettings.IsServerGC ? "server" : "workstation")} gc, {System.Runtime.GCSettings.LatencyMode}"));
        foreach (var (name, avg, peak) in sections)
            Console.WriteLine(string.Create(CultureInfo.InvariantCulture, $"  {name,-20} {avg,8:0.000} ms  peak {peak,7:0.0} ms"));
        Console.WriteLine($"{slow.Count} tick(s) over 20 ms" + (slow.Count > 0 ? ", the slowest:" : ""));
        foreach (var (ms, what) in slow.OrderByDescending(s => s.Ms).Take(6))
            Console.WriteLine(string.Create(CultureInfo.InvariantCulture, $"  {ms,6:0.0} ms: {what}"));
        return 0;
    }

    [System.Runtime.InteropServices.DllImport("kernel32.dll")]
    private static extern IntPtr GetCurrentThread();

    [System.Runtime.InteropServices.DllImport("kernel32.dll")]
    private static extern bool GetThreadTimes(IntPtr thread, out long creation, out long exit, out long kernel, out long user);

    /// <summary>The CPU time this thread has used (Windows; zero elsewhere).</summary>
    private static TimeSpan ThreadCpu() =>
        OperatingSystem.IsWindows() && GetThreadTimes(GetCurrentThread(), out _, out _, out var kernel, out var user) ? TimeSpan.FromTicks(kernel + user) : TimeSpan.Zero;
}
