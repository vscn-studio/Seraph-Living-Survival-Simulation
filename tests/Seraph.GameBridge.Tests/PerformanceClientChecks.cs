using System.Diagnostics;
using System.Reflection;
using System.Runtime.CompilerServices;
using Seraph.Diagnostics;
using Seraph.Network;
using Vintagestory.API.Client;
using Vintagestory.API.Common;

internal static class PerformanceClientChecks
{
    internal static void Run()
    {
        const BindingFlags flags = BindingFlags.Instance | BindingFlags.NonPublic;
        var paused = false;
        var connected = true;
        var packets = new List<PerformancePing>();
        var api = (ICoreClientAPI)RoutingProxy.Make(typeof(ICoreClientAPI), (m, _) => m.Name switch
        {
            "get_IsGamePaused" => paused,
            "get_ModLoader" => RoutingProxy.Make(m.ReturnType, (method, _) => method.Name == "IsModEnabled" ? false : throw new Exception(method.Name)),
            _ => throw new Exception(m.Name)
        });
        var channel = (IClientNetworkChannel)RoutingProxy.Make(typeof(IClientNetworkChannel), (m, a) =>
        {
            if (m.Name == "get_Connected") return connected;
            if (m.Name == "SendPacket") { packets.Add((PerformancePing)a![0]!); return null; }
            throw new Exception(m.Name);
        });
        var recorder = new PerformanceRecorder(api, "test");
        var client = (PerformanceClient)RuntimeHelpers.GetUninitializedObject(typeof(PerformanceClient));
        void Field(string name, object value) => typeof(PerformanceClient).GetField(name, flags)!.SetValue(client, value);
        Field("api", api); Field("channel", channel); Field("recorder", recorder); Field("latency", new LatencyTracker());
        Field("ready", true); Field("snapshot", (Func<ServerSnapshot?>)(() => new() { SessionEpoch = "session" }));
        void Tick() => typeof(PerformanceClient).GetMethod("Tick", flags)!.Invoke(client, null);
        void Require(bool ok, string message) { if (!ok) throw new Exception(message); }
        recorder.Start(60, "test");
        var capture = recorder.Current!;
        Tick();
        Require(packets.Count == 1 && packets[0].CaptureId == capture.Id, "Capture did not send correlated ping");
        Tick(); Require(packets.Count == 1, "Repeated ticks flooded ping channel");
        client.OnRenderFrame(0, EnumRenderStage.Done);
        Field("lastFrame", Stopwatch.GetTimestamp() - Stopwatch.Frequency * 2);
        client.OnRenderFrame(0, EnumRenderStage.Done);
        paused = true;
        client.OnRenderFrame(0, EnumRenderStage.Done);
        Tick(); Require(packets.Count == 1, "Pause sent a ping");
        paused = false; Tick();
        Require(packets.Count == 2 && packets[1].Id > packets[0].Id, "Resume reused pending ping");
        client.OnRenderFrame(0, EnumRenderStage.Done);
        client.SnapshotReceived();
        Field("lastSnapshot", Stopwatch.GetTimestamp() - Stopwatch.Frequency * 3);
        client.SnapshotReceived();
        client.WaterReceived(101, 850, true);
        connected = false; Tick();
        client.OnRenderFrame(0, EnumRenderStage.Done);
        Require(packets.Count == 2, "Disconnected client sent ping");
        var context = (PerformanceContext)typeof(PerformanceRecorder).GetField("context", flags)!.GetValue(recorder)!;
        var report = context.Stop("test")!;
        context.Dispose();
        Require(report.Metrics["client.frame.interval"].Count == 1
            && report.Metrics["client.frame.interval"].MaxMs >= 2000, "Frame lag lost or pause/resume added a false frame");
        Require(report.Metrics["client.snapshot.interval"].MaxMs >= 3000, "Snapshot delay absent");
        Require(report.Metrics["water.response-pending"].TotalMs == 750, "Water response time did not retain first-send timestamp");
        Console.WriteLine("PASS client capture callbacks: measured 2 s frame/3 s snapshot/750 ms water wait, pause/resume/disconnect excluded, correlated ping rate bounded");
    }
}
