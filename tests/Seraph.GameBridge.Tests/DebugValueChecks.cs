using System.Collections;
using System.Reflection;
using Seraph.Application;
using Seraph.Core.Framework;
using Seraph.Core.State;
using Seraph.Network;
using Seraph.Client;
using Vintagestory.API.Client;
using Seraph.World;
using Vintagestory.API.Common;
using Vintagestory.API.Server;

internal static class DebugValueChecks
{
    internal static void Run()
    {
        const BindingFlags flags = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static;
        T Proxy<T>(System.Func<MethodInfo, object?[]?, object?> handler) => (T)RoutingProxy.Make(typeof(T), handler);
        var inventory = Proxy<IPlayerInventoryManager>((_, _) => null);
        var entity = new EntityPlayer { Alive = true };
        var player = FixturePlayer.Make(entity, inventory);
        var saveType = typeof(IWorldManagerAPI).GetProperty("SaveGame")!.PropertyType;
        var save = RoutingProxy.Make(saveType, (m, _) => m.Name == "get_SavegameIdentifier" ? "debug-world" : throw new Exception(m.Name));
        var manager = Proxy<IWorldManagerAPI>((m, _) => m.Name == "get_SaveGame" ? save : throw new Exception(m.Name));
        var logger = Proxy<ILogger>((_, _) => null);
        var api = Proxy<ICoreServerAPI>((m, _) => m.Name switch {
            "get_WorldManager" => manager, "get_Logger" => logger,
            _ => throw new Exception("Unexpected debug API " + m.Name)
        });
        var type = typeof(BlockBathtub).Assembly.GetType("Seraph.Server.ServerCoordinator", true)!;
        var coordinator = Activator.CreateInstance(type, [api])!;
        var sessionType = type.GetNestedType("Session", flags)!;
        var session = Activator.CreateInstance(sessionType, flags, null, [player], null)!;
        object? Get(string name) => sessionType.GetProperty(name)!.GetValue(session);
        void Set(string name, object? value) => sessionType.GetProperty(name)!.SetValue(session, value);
        Set("HelloAccepted", true); Set("WantsDiagnostics", true); Set("LifeGeneration", 7L);
        long request = 0;
        DebugValueRequest Packet(string field, double value) => new() { SessionEpoch = (string)Get("Epoch")!,
            RequestId = ++request, LifeGeneration = 7, Field = field, Value = value };
        DebugValueResponse? Send(DebugValueRequest packet) => (DebugValueResponse?)type.GetMethod("HandleDebugValue", flags)!.Invoke(coordinator, [session, packet]);
        var before = Get("Interactions");
        Require(Send(Packet("bowel", 150)) is { Accepted: false } && ReferenceEquals(before, Get("Interactions")), "Non-admin edit changed state.");
        player.Administrator = true;
        var first = Packet("bowel", 150);
        Require(Send(first) is { Accepted: true } && ((InteractionState)Get("Interactions")!).BowelUnits == 150,
            "Authorized bowel edit did not reach player state.");
        var checkpoint = (long)Get("CheckpointSequence")!;
        Require(checkpoint == 1 && player.GetModdata("vsslssmod:state") is { Length: > 0 }, "Edited state not checkpointed.");
        var saved = ShadowSaveCodec.Decode(player.GetModdata("vsslssmod:state"), "debug-world", player.PlayerUID);
        Require(saved.Success, "Edited save cannot be decoded.");
        first.Value = 400;
        Require(Send(first) is { Accepted: true } && ((InteractionState)Get("Interactions")!).BowelUnits == 150
            && (long)Get("CheckpointSequence")! == checkpoint, "Replay applied a second mutation.");
        Require(Send(new() { SessionEpoch = "old-world", RequestId = ++request, LifeGeneration = 7, Field = "bowel", Value = 400 }) is null,
            "Old-world request accepted.");
        var oldLife = Packet("bowel", 200); oldLife.LifeGeneration = 6;
        Require(Send(oldLife) is { Accepted: false }, "Old-life edit accepted.");
        foreach (var invalid in new[] { -1d, 5001d, double.NaN, double.PositiveInfinity })
            Require(Send(Packet("bowel", invalid)) is { Accepted: false }, "Invalid bowel value accepted.");
        Require(Send(Packet("arbitrary-save-property", 1)) is { Accepted: false }, "Unknown field accepted.");
        player.FailSave = true;
        Require(Send(Packet("bowel", 300)) is { Accepted: false } && ((InteractionState)Get("Interactions")!).BowelUnits == 150
            && (long)Get("CheckpointSequence")! == checkpoint, "Save failure did not roll back edit.");
        player.FailSave = false;
        Require(Send(Packet("dirtiness", 80)) is { Accepted: true } && ((FrameworkState)Get("Framework")!).Dirtiness == .8,
            "Percentage UI value not converted to internal fraction.");
        Require(Send(Packet("sleep-pressure", 90)) is { Accepted: true } && ((BodyState)Get("State")!).SleepPressure == .9,
            "Body value editor did not update sleep pressure.");
        var activeType = sessionType.GetProperty("LatrineDefecation")!.PropertyType;
        Set("LatrineDefecation", System.Runtime.CompilerServices.RuntimeHelpers.GetUninitializedObject(activeType));
        Require(Send(Packet("bowel", 400)) is { Accepted: false }, "Active latrine action allowed state edits.");
        Set("LatrineDefecation", null);
        var values = (PanelMetric[])type.GetMethod("DebugValues", flags)!.Invoke(null, [session])!;
        foreach (var value in values)
        {
            var v = value.Minimum + (value.Maximum - value.Minimum) * .5;
            Require(Send(Packet(value.Key, v)) is { Accepted: true }, "Advertised editor field rejected: " + value.Key);
        }
        Require(((FrameworkState)Get("Framework")!).Validate() is null
            && ((BodyState)Get("State")!).Validate(BodyProfile.Default) is null
            && ((InteractionState)Get("Interactions")!).Validate() is null, "Editor produced an invalid player state.");
        Set("Deceased", true);
        Require(Send(Packet("bowel", 400)) is { Accepted: false }, "Dead player edit accepted.");
        Set("Deceased", false); Set("WantsDiagnostics", false);
        Require(Send(Packet("bowel", 400)) is { Accepted: false }, "Closed diagnostics edit accepted.");
        // Exercise the real client request builder without initializing game rendering.
        var bridge = (SeraphClientBridge)System.Runtime.CompilerServices.RuntimeHelpers.GetUninitializedObject(typeof(SeraphClientBridge));
        DebugValueRequest? transmitted = null;
        var clientChannel = Proxy<IClientNetworkChannel>((m, a) => {
            if (m.Name == "get_Connected") return true;
            if (m.Name == "SendPacket") { transmitted = (DebugValueRequest)a![0]!; return null; }
            throw new Exception("Unexpected debug channel call " + m.Name);
        });
        var bridgeType = typeof(SeraphClientBridge);
        bridgeType.GetField("channel", flags)!.SetValue(bridge, clientChannel);
        bridgeType.GetProperty("Snapshot")!.SetValue(bridge, new ServerSnapshot { SessionEpoch = "client-world", LifeGeneration = 3,
            CanViewDiagnostics = true, CanEditSettings = true });
        bridgeType.GetProperty("LastReceivedUtc")!.SetValue(bridge, DateTime.UtcNow);
        bridge.ApplyDebugValue("bowel", 150, 3);
        Require(transmitted is { Field: "bowel", Value: 150, SessionEpoch: "client-world", LifeGeneration: 3, RequestId: 1 }
            && transmitted.ProtocolVersion == Protocol.Version, "Client editor did not send an authoritative edit request.");
        transmitted = null;
        bridgeType.GetProperty("LastReceivedUtc")!.SetValue(bridge, DateTime.UtcNow.AddSeconds(-10));
        bridge.ApplyDebugValue("bowel", 400, 3);
        Require(transmitted is null && bridge.DebugValueResponseSequence == 1, "Stale client sent a state edit.");
        Console.WriteLine("PASS authoritative debug edits: privilege, life/epoch, bounded values, replay, checkpoint, rollback and percentage conversion");
    }
    private static void Require(bool condition, string message) { if (!condition) throw new Exception(message); }
}
