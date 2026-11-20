using System.Diagnostics;
using Seraph.Network;
using Vintagestory.API.Client;
using Vintagestory.API.Common;
using Vintagestory.API.Config;

namespace Seraph.Diagnostics;

internal sealed class PerformanceClient : IRenderer
{
    private readonly ICoreClientAPI api;
    private readonly Func<ServerSnapshot?> snapshot;
    private readonly IClientNetworkChannel channel;
    private readonly PerformanceRecorder recorder;
    private readonly LatencyTracker latency = new();
    private readonly long listener;
    private long lastFrame, lastSnapshot, lastMemory, processBytes;
    private int gen0, gen1, gen2;
    private bool ready, disposed, blocked;
    private string epoch = "", captureId = "";
    private string serverCaptureId = "";
    private double lastRtt, lastQueue;
    private bool serverRecording;
    public double RenderOrder => 1;
    public int RenderRange => int.MaxValue;
    private static double Now => Stopwatch.GetTimestamp() * 1000d / Stopwatch.Frequency;
    internal PerformanceClient(ICoreClientAPI api, Func<ServerSnapshot?> snapshot)
    {
        this.api = api; this.snapshot = snapshot; recorder = new(api, "client");
        channel = api.Network.RegisterChannel("vsslssmod-perf")
            .RegisterMessageType<PerformancePing>().RegisterMessageType<PerformancePong>();
        channel.SetMessageHandler<PerformancePong>(packet =>
        {
            var received = Now;
            api.Event.EnqueueMainThreadTask(() =>
            {
                var applied = Now;
                if (disposed || blocked || recorder.Current is not { } capture || packet is null
                    || api.IsGamePaused || !channel.Connected || packet.CaptureId is null || packet.CaptureId.Length > 32
                    || packet.SessionEpoch != snapshot()?.SessionEpoch
                    || !latency.Complete(packet.Id, packet.SessionEpoch, received, applied, packet.ServerQueueMs, out var rtt)) return;
                lastRtt = rtt; lastQueue = packet.ServerQueueMs; serverRecording = packet.ServerRecording;
                if (packet.CaptureId.Length == 32 && serverCaptureId != packet.CaptureId)
                { serverCaptureId = packet.CaptureId; capture.Add("client.server-capture", 0, isEvent: true, detail: serverCaptureId); }
                capture.Add("network.rtt", rtt, isEvent: rtt >= 100);
                capture.Add("network.server-queue", packet.ServerQueueMs, isEvent: packet.ServerQueueMs >= 50);
                capture.Add("network.client-queue", applied - received, isEvent: applied - received >= 50);
            }, "vsslssmod-performance-pong");
        });
        listener = api.Event.RegisterGameTickListener(_ => Tick(), 100);
        api.Event.RegisterRenderer(this, EnumRenderStage.Done, "vsslssmod-performance");
        api.Event.LevelFinalize += Ready; api.Event.LeaveWorld += Leave;
        api.ChatCommands.Create("slssperf").WithDescription(Lang.Get("vsslssmod:perf-command"))
            .HandleWith(_ => Start())
            .BeginSubCommand("start").WithDescription(Lang.Get("vsslssmod:perf-command"))
                .HandleWith(_ => Start()).EndSubCommand()
            .BeginSubCommand("stop").WithDescription(Lang.Get("vsslssmod:perf-stop-description"))
                .HandleWith(_ => { var path = recorder.Stop("manual"); ResetSamples();
                    return TextCommandResult.Success(Lang.Get("vsslssmod:perf-stopped", path ?? "—")); }).EndSubCommand()
            .BeginSubCommand("status").WithDescription(Lang.Get("vsslssmod:perf-status-description"))
                .HandleWith(_ => TextCommandResult.Success(Lang.Get("vsslssmod:perf-status",
                    recorder.Current is null ? Lang.Get("vsslssmod:perf-idle") : Lang.Get("vsslssmod:perf-recording"),
                    KometTimingBridge.Available ? Lang.Get("vsslssmod:perf-connected") : Lang.Get("vsslssmod:perf-unavailable"),
                    lastRtt, lastQueue, serverRecording ? Lang.Get("vsslssmod:perf-recording") : Lang.Get("vsslssmod:perf-idle"),
                    recorder.LastPath ?? "—"))).EndSubCommand();
    }
    private TextCommandResult Start()
    {
        if (!ready || api.IsGamePaused || snapshot() is null) return TextCommandResult.Error(Lang.Get("vsslssmod:perf-world-required"));
        if (!recorder.Start(60, "manual")) return TextCommandResult.Error(Lang.Get("vsslssmod:perf-busy"));
        ResetSamples(); return TextCommandResult.Success(Lang.Get("vsslssmod:perf-started"));
    }
    private void Ready() { ready = true; KometTimingBridge.Initialize(api); ResetSamples(); }
    private void Leave() { ready = false; recorder.Stop("leave-world"); ResetSamples(); }
    private void ResetSamples()
    {
        lastFrame = lastSnapshot = lastMemory = 0; latency.Clear(); epoch = "";
        serverCaptureId = ""; serverRecording = false;
    }
    private void Tick()
    {
        if (disposed) return;
        if (ready) recorder.Tick();
        var capture = recorder.Current;
        var session = snapshot()?.SessionEpoch;
        var unavailable = !ready || api.IsGamePaused || !channel.Connected || string.IsNullOrEmpty(session);
        if (capture is null || unavailable)
        {
            if (!blocked && capture is not null) capture.Add("client.sampling-suspended", 0, isEvent: true);
            blocked = unavailable; ResetSamples(); return;
        }
        if (blocked || epoch != session || captureId != capture.Id)
        { ResetSamples(); epoch = session!; captureId = capture.Id; }
        blocked = false;
        var now = Now;
        if (latency.Expire(now, out var wait)) capture.Add("network.timeout", wait, isEvent: true);
        var id = latency.Begin(epoch, now);
        if (id != 0) channel.SendPacket(new PerformancePing { Id = id, SessionEpoch = epoch, RecordServer = true, CaptureId = capture.Id });
        var stamp = Stopwatch.GetTimestamp();
        if (lastMemory == 0)
        {
            lastMemory = stamp; processBytes = GC.GetTotalAllocatedBytes(false);
            gen0 = GC.CollectionCount(0); gen1 = GC.CollectionCount(1); gen2 = GC.CollectionCount(2);
        }
        else if (Stopwatch.GetElapsedTime(lastMemory, stamp).TotalSeconds >= 1)
        {
            var bytes = GC.GetTotalAllocatedBytes(false);
            var n0 = GC.CollectionCount(0); var n1 = GC.CollectionCount(1); var n2 = GC.CollectionCount(2);
            capture.Add("process.allocations", Stopwatch.GetElapsedTime(lastMemory, stamp).TotalMilliseconds,
                Math.Max(0, bytes - processBytes), true, $"gc0={n0 - gen0};gc1={n1 - gen1};gc2={n2 - gen2};heapBytes={GC.GetTotalMemory(false)}");
            lastMemory = stamp; processBytes = bytes; gen0 = n0; gen1 = n1; gen2 = n2;
        }
    }
    public void OnRenderFrame(float deltaTime, EnumRenderStage stage)
    {
        // Stopwatch measures the complete frame interval, including time outside rendering. No clamped dt.
        if (disposed || recorder.Current is not { } capture)
        { lastFrame = 0; return; }
        if (!ready || api.IsGamePaused || !channel.Connected)
        { blocked = true; ResetSamples(); return; }
        if (blocked) { lastFrame = 0; return; }
        var now = Stopwatch.GetTimestamp();
        if (lastFrame != 0)
        {
            var gap = Stopwatch.GetElapsedTime(lastFrame, now).TotalMilliseconds;
            capture.Add("client.frame.interval", gap, isEvent: gap >= 50);
        }
        lastFrame = now;
    }
    internal void SnapshotReceived()
    {
        if (blocked || api.IsGamePaused || recorder.Current is not { } capture) { lastSnapshot = 0; return; }
        var now = Stopwatch.GetTimestamp();
        if (lastSnapshot != 0)
        {
            var gap = Stopwatch.GetElapsedTime(lastSnapshot, now).TotalMilliseconds;
            capture.Add("client.snapshot.interval", gap, isEvent: gap >= 2000);
        }
        lastSnapshot = now;
    }
    internal void WaterReceived(long requestId, long receivedAt, bool pending)
    {
        // IDs encode the first send's world elapsed time + 1 and survive retries.
        if (blocked || api.IsGamePaused || recorder.Current is not { } capture || requestId <= 0) return;
        var wait = receivedAt - (requestId - 1);
        if (wait < 0) return;
        capture.Add(pending ? "water.response-pending" : "water.response-ready", wait, isEvent: true);
    }
    public void Dispose()
    {
        if (disposed) return; disposed = true;
        api.Event.LevelFinalize -= Ready; api.Event.LeaveWorld -= Leave;
        api.Event.UnregisterGameTickListener(listener); api.Event.UnregisterRenderer(this, EnumRenderStage.Done);
        recorder.Dispose(); ResetSamples();
    }
}
