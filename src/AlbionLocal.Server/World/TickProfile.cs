using System.Diagnostics;
using System.Globalization;

namespace AlbionLocal.Server;

/// <summary>
/// Where a world tick's time goes: <see cref="World.Update"/> marks the end of each of its sections (<see cref="Mark"/>, one
/// Stopwatch read), <see cref="WorldLoop"/> names the slowest sections of a tick over 20 ms, and .tick shows each section's
/// average and peak since the start or the last .tick reset. Touched only under <see cref="World.Sync"/>.
/// </summary>
public sealed class TickProfile
{
    private sealed class Section
    {
        public long Total, Max;
    }

    private readonly Dictionary<string, Section> _sections = new(StringComparer.Ordinal);
    private readonly List<(string Name, long Ticks)> _tick = [];
    private long _last, _tickStart, _total, _max;

    /// <summary>Ticks measured since the start or the last <see cref="Reset"/>.</summary>
    public long Ticks { get; private set; }

    /// <summary>A tick starts.</summary>
    public void Begin()
    {
        _tick.Clear();
        _last = _tickStart = Stopwatch.GetTimestamp();
    }

    /// <summary>The section <paramref name="name"/> of this tick ends now (it took the time since the last mark).</summary>
    public void Mark(string name)
    {
        var now = Stopwatch.GetTimestamp();
        var took = now - _last;
        _last = now;
        _tick.Add((name, took));
        if (!_sections.TryGetValue(name, out var s)) _sections[name] = s = new Section();
        s.Total += took;
        if (took > s.Max) s.Max = took;
    }

    /// <summary>The last tick's time (ms).</summary>
    public double Last { get; private set; }

    /// <summary>The tick ends.</summary>
    public void End()
    {
        var took = Stopwatch.GetTimestamp() - _tickStart;
        Last = Ms(took);
        _total += took;
        if (took > _max) _max = took;
        Ticks++;
    }

    /// <summary>The <paramref name="count"/> slowest sections of the last tick, as "bots 12.3 ms, mob ai 4.1 ms".</summary>
    public string Slowest(int count = 3) => string.Join(", ", _tick.OrderByDescending(t => t.Ticks).Take(count)
        .Select(t => string.Create(CultureInfo.InvariantCulture, $"{t.Name} {Ms(t.Ticks):0.0} ms")));

    /// <summary>The whole tick's average and peak (ms), then every section's, the slowest on average first.</summary>
    public (double Average, double Max, IReadOnlyList<(string Name, double Average, double Max)> Sections) Summary()
    {
        var n = Math.Max(1, Ticks);
        return (Ms(_total) / n, Ms(_max),
            [.. _sections.Select(s => (s.Key, Ms(s.Value.Total) / n, Ms(s.Value.Max))).OrderByDescending(s => s.Item2)]);
    }

    /// <summary>Starts the averages and peaks anew.</summary>
    public void Reset()
    {
        _sections.Clear();
        _total = _max = 0;
        Ticks = 0;
    }

    private static double Ms(long stopwatchTicks) => stopwatchTicks * 1000.0 / Stopwatch.Frequency;
}
