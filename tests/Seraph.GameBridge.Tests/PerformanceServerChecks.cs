using System.Collections;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Text.Json;
using Seraph.Application;
using Seraph.Diagnostics;
using Seraph.Network;
using Seraph.Server;
using Vintagestory.API.Common;
using Vintagestory.API.Config;
using Vintagestory.API.Server;

internal static class PerformanceServerChecks
{
    internal static void Run()
    {
        const BindingFlags flags = BindingFlags.Instance | BindingFlags.NonPublic;
        var coordinator = (ServerCoordinator)RuntimeHelpers.GetUninitializedObject(typeof(ServerCoordinator));
        var tasks = new Queue<Delegate>();
        var sent = new List<PerformancePong>();
        Delegate? handler = null;
        IServerNetworkChannel? channel = null;
        channel = (IServerNetworkChannel)RoutingProxy.Make(typeof(IServerNetworkChannel), (m, a) =>
        {
            if (m.Name == "RegisterMessageType") return channel;
            if (m.Name == "SetMessageHandler") { handler = (Delegate)a![0]!; return channel; }
            if (m.Name == "SendPacket") { sent.Add((PerformancePong)a![0]!); return null; }
            throw new Exception(m.Name);
        });
        var events = (IServerEventAPI)RoutingProxy.Make(typeof(IServerEventAPI), (m, a) =>
        {
            if (m.Name == "EnqueueMainThreadTask") { tasks.Enqueue((Delegate)a![0]!); return null; }
            if (m.Name == "RegisterGameTickListener") return 1L;
            throw new Exception(m.Name);
        });
        var logger = (ILogger)RoutingProxy.Make(typeof(ILogger), (_, _) => null);
        var api = (ICoreServerAPI)RoutingProxy.Make(typeof(ICoreServerAPI), (m, _) => m.Name switch
        {
            "get_Event" => events, "get_Logger" => logger,
            "get_Network" => RoutingProxy.Make(m.ReturnType, (_, _) => channel),
            _ => throw new Exception(m.Name)
        });
        var player = FixturePlayer.Make(null!, null!);
        void Field(string name, object value) => typeof(ServerCoordinator).GetField(name, flags)!.SetValue(coordinator, value);
        Field("api", api); Field("performanceInbound", new InboundGate(2, 0));
        var sessionsField = typeof(ServerCoordinator).GetField("sessions", flags)!;
        var sessions = (IDictionary)Activator.CreateInstance(sessionsField.FieldType)!; Field("sessions", sessions);
        var sessionType = typeof(ServerCoordinator).GetNestedType("Session", BindingFlags.NonPublic)!;
        var session = Activator.CreateInstance(sessionType, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic, null, [player], null)!;
        sessionType.GetProperty("HelloAccepted")!.SetValue(session, true);
        sessions.Add(player.PlayerUID, session);
        var epoch = (string)sessionType.GetProperty("Epoch")!.GetValue(session)!;
        typeof(ServerCoordinator).GetMethod("StartPerformance", flags)!.Invoke(coordinator, null);
        var recorder = (PerformanceRecorder)typeof(ServerCoordinator).GetField("performance", flags)!.GetValue(coordinator)!;
        void Require(bool ok, string message) { if (!ok) throw new Exception(message); }
        var captureId = Guid.NewGuid().ToString("N");
        void Ping(string sessionEpoch, int version = 1) => handler!.DynamicInvoke(player,
            new PerformancePing { Id = 1, SessionEpoch = sessionEpoch, Version = version, RecordServer = true, CaptureId = captureId });
        Ping(epoch, 99); Require(tasks.Count == 0, "Invalid ping protocol queued");
        Ping("old"); tasks.Dequeue().DynamicInvoke(); Require(sent.Count == 0, "Old epoch received pong");
        Ping(epoch); Require(sent.Count == 0, "Pong bypassed main thread queue");
        tasks.Dequeue().DynamicInvoke();
        Require(sent.Count == 1 && sent[0].ServerQueueMs >= 0 && !sent[0].ServerRecording && recorder.Current is null,
            "Own ping failed or non-admin started server recording");
        Ping(epoch); Require(tasks.Count == 0, "Server ping rate limiter bypassed");
        Field("performanceInbound", new InboundGate(2, 0)); player.Administrator = true;
        Ping(epoch); tasks.Dequeue().DynamicInvoke();
        Require(sent.Count == 2 && sent[1].ServerRecording && sent[1].CaptureId.Length == 32, "Admin recording missing");
        var logPath = GamePaths.CustomLogPath;
        var root = TestArtifacts.DirectoryPath("performance");
        GamePaths.CustomLogPath = root;
        try
        {
            var path = recorder.Stop("test")!;
            ((Task)typeof(PerformanceRecorder).GetField("save", flags)!.GetValue(recorder)!).GetAwaiter().GetResult();
            using var document = JsonDocument.Parse(File.ReadAllText(path));
            Require(document.RootElement.GetProperty("Metrics").GetProperty("server.ping.queue").GetProperty("Count").GetInt64() == 1,
                "Background export missing server timing");
        }
        finally { GamePaths.CustomLogPath = logPath; recorder.Dispose(); }
        Require(PerformanceContext.Get(api) is null, "Disposed server retained context");
        Console.WriteLine("PASS server channel: version/epoch/rate guard, queued response, admin-only recording, capture correlation, actual background JSON export");
    }
}
