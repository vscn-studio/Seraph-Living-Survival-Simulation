using System.Diagnostics;
using Vintagestory.API.Common;

namespace Seraph;

/// <summary>Times work inside callbacks, never the gap between callbacks (pause/loading).</summary>
internal sealed class SlowCallbackProbe(string name, double thresholdMs = SlowCallbackProbe.ThresholdMs)
{
    private readonly string kometName = "vsslssmod:" + name;
    internal const double ThresholdMs = 250;
    internal const double ReportIntervalMs = 30000;
    private double lastReportMs = double.NegativeInfinity;
    private int suppressed;
    private double suppressedMaximumMs;

    internal Measurement Measure(ICoreAPI api) => new(this, api);

    // Explicit monotonic completion time also permits deterministic throttle tests.
    internal void Record(ICoreAPI api, double durationMs, double completedMs)
    {
        if (!double.IsFinite(durationMs) || durationMs < thresholdMs || !double.IsFinite(completedMs)) return;
        if (completedMs - lastReportMs < ReportIntervalMs)
        {
            suppressed++;
            suppressedMaximumMs = Math.Max(suppressedMaximumMs, durationMs);
            return;
        }
        lastReportMs = completedMs;
        api.Logger?.Warning("[vsslssmod] Slow callback {0}: {1:0.0} ms elapsed inside callback; previous suppressed={2}, max={3:0.0} ms. Includes waits/descheduling, not CPU time or callback gaps.",
            name, durationMs, suppressed, suppressedMaximumMs);
        suppressed = 0;
        suppressedMaximumMs = 0;
    }

    internal readonly struct Measurement : IDisposable
    {
        private readonly SlowCallbackProbe probe;
        private readonly ICoreAPI api;
        private readonly long start;
        private readonly Diagnostics.PerformanceCapture? capture;
        private readonly Diagnostics.KometTimingBridge.Scope komet;
        private readonly long allocated;
        internal Measurement(SlowCallbackProbe probe, ICoreAPI api)
        {
            this.probe = probe; this.api = api;
            capture = Diagnostics.PerformanceContext.Get(api);
            komet = Diagnostics.KometTimingBridge.Begin(probe.kometName);
            allocated = capture is null ? 0 : GC.GetAllocatedBytesForCurrentThread();
            start = Stopwatch.GetTimestamp();
        }
        public void Dispose()
        {
            var end = Stopwatch.GetTimestamp();
            var ms = (end - start) * 1000d / Stopwatch.Frequency;
            capture?.Add(probe.kometName, ms, GC.GetAllocatedBytesForCurrentThread() - allocated, ms >= 50);
            komet.Dispose();
            probe.Record(api, ms, end * 1000d / Stopwatch.Frequency);
        }
    }
}
