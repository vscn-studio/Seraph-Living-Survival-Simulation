using System.Reflection;
using System.Runtime.Loader;
using System.Text.Json;
using Seraph;
using Seraph.Diagnostics;
using Seraph.Network;
using Vintagestory.API.Common;

internal static class PerformanceChecks
{
    private static void Require(bool value, string message) { if (!value) throw new Exception(message); }
    internal static void Run(string? kometPath)
    {
        SlowCallbackChecks.Run();
        Require(!KometTimingBridge.Available, "Komet unexpectedly required at startup");
        using (KometTimingBridge.Begin("vsslssmod:test")) { }
        var tracker = new LatencyTracker();
        var id = tracker.Begin("one", 1000);
        Require(id > 0 && tracker.Begin("one", 1100) == 0, "Concurrent ping accepted");
        Require(!tracker.Complete(id, "old", 1150, 1160, 2, out _), "Old epoch accepted");
        Require(!tracker.Complete(id, "one", 900, 1160, 2, out _), "Negative RTT accepted");
        Require(!tracker.Complete(id, "one", 1150, 1160, double.NaN, out _), "Invalid server queue accepted");
        Require(tracker.Complete(id, "one", 1150, 1160, 2, out var rtt) && rtt == 150, "RTT includes client apply queue or mismatched clocks");
        Require(!tracker.Complete(id, "one", 1151, 1161, 2, out _), "Duplicate pong accepted");
        Require(tracker.Begin("one", 1999) == 0, "Ping rate limit bypassed");
        id = tracker.Begin("one", 2000);
        Require(!tracker.Expire(6999, out _) && tracker.Expire(7000, out var elapsed) && elapsed == 5000, "Timeout boundary wrong");
        Require(!tracker.Complete(id, "one", 7001, 7002, 0, out _), "Late timeout response accepted");
        var next = tracker.Begin("one", 7001); tracker.Clear();
        var afterClear = tracker.Begin("two", 8000);
        Require(afterClear > next && !tracker.Complete(next, "one", 8010, 8011, 0, out _), "Disconnect/reset reused request IDs");
        Console.WriteLine("PASS monotonic latency: rate bound, single pending, timeout, replay, epoch, pause/disconnect reset");

        var capture = new PerformanceCapture("test", "unit");
        capture.Add("invalid", double.NaN);
        Parallel.For(0, 1000, _ => capture.Add("work", 2, 10, true));
        var report = capture.Finish("test");
        capture.Add("late", 1);
        Require(report.Metrics.Count == 1 && report.Metrics["work"].Count == 1000
            && report.Metrics["work"].TotalMs == 2000 && report.Metrics["work"].AllocatedBytes == 10000, "Lost concurrent samples or modified finished report");
        Require(report.Events.Length == 512 && report.OmittedEvents == 488, "Event memory not bounded");
        var bounded = new PerformanceCapture("test", "unit");
        for (var i = 0; i < 500; i++) bounded.Add(i.ToString(), 1);
        Require(bounded.Finish("test").Metrics.Count == 128, "Metric memory not bounded");
        using var json = JsonDocument.Parse(JsonSerializer.Serialize(report));
        Require(json.RootElement.GetProperty("Events").GetArrayLength() == 512, "Export lost bounded events");
        var api = (ICoreAPI)RoutingProxy.Make(typeof(ICoreAPI), (m, _) => throw new Exception("Unexpected API call: " + m.Name));
        using (var context = new PerformanceContext(api))
        {
            Require(PerformanceContext.Get(api) is null, "Idle context collects");
            context.Start("test", "unit");
            var probe = new SlowCallbackProbe("scope", double.MaxValue);
            using (probe.Measure(api)) { GC.KeepAlive(new byte[1024]); }
            var scoped = context.Stop("test")!;
            Require(scoped.Metrics["vsslssmod:scope"].AllocatedBytes >= 1024, "Current thread allocations absent");
            Require(PerformanceContext.Get(api) is null, "Stopped context still collects");
            context.Start("test", "restart");
            Require(context.Stop("test")!.Metrics.Count == 0, "Old recording leaked into new capture");
        }
        using (var context = new PerformanceContext(api)) { } // Same API can be used after disposal.
        var ping = new PerformancePing { Id = 13, SessionEpoch = "epoch", RecordServer = true };
        using var buffer = new MemoryStream(); ProtoBuf.Serializer.Serialize(buffer, ping); buffer.Position = 0;
        var copy = ProtoBuf.Serializer.Deserialize<PerformancePing>(buffer);
        Require(copy.Id == 13 && copy.SessionEpoch == "epoch" && copy.RecordServer && copy.Version == 1, "Ping serialization changed");
        using var pongBuffer = new MemoryStream();
        ProtoBuf.Serializer.Serialize(pongBuffer, new PerformancePong { Id = 13, SessionEpoch = "epoch", ServerQueueMs = 17.5, ServerRecording = true });
        pongBuffer.Position = 0; var pong = ProtoBuf.Serializer.Deserialize<PerformancePong>(pongBuffer);
        Require(pong.Id == 13 && pong.ServerQueueMs == 17.5 && pong.ServerRecording, "Pong serialization changed");
        Console.WriteLine("PASS bounded concurrent capture, frozen JSON export, lifecycle, scope allocations, protocol round trip");
        PerformanceClientChecks.Run();
        PerformanceServerChecks.Run();
        if (kometPath is null) return;
        var assembly = AssemblyLoadContext.Default.LoadFromAssemblyPath(Path.GetFullPath(kometPath));
        var debug = assembly.GetType("Komet.Api.KometDebug", true)!;
        KometTimingBridge.Bind(debug);
        var property = debug.GetProperty("Capturing")!;
        property.SetValue(null, true);
        try
        {
            using (KometTimingBridge.Begin("vsslssmod:test.outer"))
            { using var nested = KometTimingBridge.Begin("vsslssmod:test.inner"); }
            var before = GC.GetAllocatedBytesForCurrentThread();
            for (var i = 0; i < 1000; i++) { using var scope = KometTimingBridge.Begin("vsslssmod:test.outer"); }
            Require(GC.GetAllocatedBytesForCurrentThread() == before, "Komet active hot path allocated after warmup");
            var timings = (Array)debug.GetMethod("TakeTimings", BindingFlags.Static | BindingFlags.NonPublic)!.Invoke(null, null)!;
            var calls = timings.Cast<object>().ToDictionary(t => (string)t.GetType().GetField("Item1")!.GetValue(t)!,
                t => (long)t.GetType().GetField("Item3")!.GetValue(t)!);
            Require(calls["vsslssmod:test.outer"] == 1001 && calls["vsslssmod:test.inner"] == 1, "Real Komet did not book nested calls");
        }
        finally { property.SetValue(null, false); }
        SlowCallbackChecks.Run();
        Console.WriteLine("PASS installed Komet public timing API: actual booked counts, nested scopes, active and idle allocation-free after warmup");
    }
}
