using System.Diagnostics;
using Seraph.Application;
using Seraph.Diagnostics;
using Seraph.Network;
using Vintagestory.API.Common;
using Vintagestory.API.Server;

namespace Seraph.Server;

internal sealed partial class ServerCoordinator
{
    private readonly InboundGate performanceInbound = new(2, 1);
    private PerformanceRecorder? performance;
    private long performanceListener;
    private long lastPerformanceTick;
    private string lastClientCapture = "";
    private void StartPerformance()
    {
        performance = new(api, "server");
        var perfChannel = api.Network.RegisterChannel("vsslssmod-perf")
            .RegisterMessageType<PerformancePing>().RegisterMessageType<PerformancePong>();
        perfChannel.SetMessageHandler<PerformancePing>((player, packet) =>
        {
            if (packet is null || packet.Version != 1 || packet.Id <= 0 || packet.SessionEpoch is null
                || packet.SessionEpoch.Length is 0 or > 64 || packet.CaptureId is null || packet.CaptureId.Length != 32
                || !performanceInbound.TryAccept(player.PlayerUID)) return;
            var received = Stopwatch.GetTimestamp();
            api.Event.EnqueueMainThreadTask(() =>
            {
                if (disposed || !Current(player, out var session) || !session.HelloAccepted || session.Epoch != packet.SessionEpoch) return;
                var queueMs = Stopwatch.GetElapsedTime(received).TotalMilliseconds;
                // Anyone can test their own RTT; only administrators can initiate server file recording.
                if (packet.RecordServer && player.HasPrivilege(Privilege.controlserver)) performance.RenewLease();
                if (performance.Current is { } capture && lastClientCapture != packet.CaptureId)
                { lastClientCapture = packet.CaptureId; capture.Add("server.client-capture", 0, isEvent: true, detail: packet.CaptureId); }
                performance.Current?.Add("server.ping.queue", queueMs, isEvent: queueMs >= 50);
                perfChannel.SendPacket(new PerformancePong { SessionEpoch = session.Epoch, Id = packet.Id,
                    ServerQueueMs = queueMs, ServerRecording = performance.Current is not null,
                    CaptureId = performance.Current?.Id ?? "" }, player);
            }, "vsslssmod-performance-ping");
        });
        performanceListener = api.Event.RegisterGameTickListener(_ =>
        {
            if (disposed || paused) { lastPerformanceTick = 0; return; }
            performance.Tick();
            var now = Stopwatch.GetTimestamp();
            if (performance.Current is { } capture && lastPerformanceTick != 0)
            {
                var gap = Stopwatch.GetElapsedTime(lastPerformanceTick, now).TotalMilliseconds;
                capture.Add("server.tick.interval", gap, isEvent: gap >= 500);
            }
            lastPerformanceTick = performance.Current is null ? 0 : now;
        }, 250);
    }
}
