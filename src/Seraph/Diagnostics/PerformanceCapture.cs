using System.Diagnostics;
using System.Runtime.CompilerServices;
using Vintagestory.API.Common;

namespace Seraph.Diagnostics;

internal sealed class PerformanceContext : IDisposable
{
    private static readonly ConditionalWeakTable<ICoreAPI, PerformanceContext> contexts = new();
    private static int activeCount;
    private readonly ICoreAPI api;
    private PerformanceCapture? current;
    internal PerformanceCapture? Current => Volatile.Read(ref current);
    internal PerformanceContext(ICoreAPI api) { this.api = api; contexts.Add(api, this); }
    internal static PerformanceCapture? Get(ICoreAPI api) => Volatile.Read(ref activeCount) > 0
        && contexts.TryGetValue(api, out var context) ? context.Current : null;
    internal PerformanceCapture Start(string side, string trigger)
    {
        if (Current is { } existing) return existing;
        var capture = new PerformanceCapture(side, trigger);
        Volatile.Write(ref current, capture); Interlocked.Increment(ref activeCount); return capture;
    }
    internal PerformanceReport? Stop(string reason)
    {
        var capture = Interlocked.Exchange(ref current, null);
        if (capture is null) return null;
        Interlocked.Decrement(ref activeCount); return capture.Finish(reason);
    }
    public void Dispose() { Stop("disposed"); contexts.Remove(api); }
}

internal sealed class PerformanceCapture(string side, string trigger)
{
    internal const int MaxMetrics = 128, MaxEvents = 512;
    private readonly object sync = new();
    private readonly Dictionary<string, PerformanceMetric> metrics = new(StringComparer.Ordinal);
    private readonly Queue<PerformanceEvent> events = new();
    private bool finished;
    private int omittedEvents;
    internal string Id { get; } = Guid.NewGuid().ToString("N");
    internal DateTime StartedUtc { get; } = DateTime.UtcNow;
    internal long Started { get; } = Stopwatch.GetTimestamp();
    internal double ElapsedSeconds => Stopwatch.GetElapsedTime(Started).TotalSeconds;
    internal void Add(string name, double ms, long bytes = 0, bool isEvent = false, string? detail = null)
    {
        if (!double.IsFinite(ms) || ms < 0 || bytes < 0) return;
        lock (sync)
        {
            if (finished) return;
            if (!metrics.TryGetValue(name, out var metric))
            {
                if (metrics.Count >= MaxMetrics) return;
                metrics.Add(name, metric = new());
            }
            metric.Count++; metric.TotalMs += ms; metric.MaxMs = Math.Max(metric.MaxMs, ms); metric.AllocatedBytes += bytes;
            if (!isEvent) return;
            if (events.Count == MaxEvents) { events.Dequeue(); omittedEvents++; }
            events.Enqueue(new(ElapsedSeconds * 1000, name, ms, bytes, detail));
        }
    }
    internal PerformanceReport Finish(string reason)
    {
        lock (sync)
        {
            finished = true;
            return new(Id, side, trigger, StartedUtc, ElapsedSeconds, reason, omittedEvents, metrics, events.ToArray());
        }
    }
}

internal sealed class PerformanceMetric
{
    public long Count { get; set; }
    public double TotalMs { get; set; }
    public double MaxMs { get; set; }
    public long AllocatedBytes { get; set; }
}
internal sealed record PerformanceEvent(double AtMs, string Name, double DurationMs, long AllocatedBytes, string? Detail);
internal sealed record PerformanceReport(string Id, string Side, string Trigger, DateTime StartedUtc, double DurationSeconds,
    string StopReason, int OmittedEvents, IReadOnlyDictionary<string, PerformanceMetric> Metrics, PerformanceEvent[] Events)
{
    public string Version => Core.BuildInfo.Version;
    public string TimingSemantics => "Elapsed wall time includes GC/waits/descheduling. Scope bytes are current-thread allocations. Nested scopes overlap: do not sum. Frame/tick gaps exclude observed pause/disconnection and first resumed sample. RTT includes both endpoints and queues, not pure network latency.";
}
