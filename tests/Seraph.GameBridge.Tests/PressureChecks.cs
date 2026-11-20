using System.Collections;
using System.Reflection;
using Seraph;
using Seraph.Application;
using Seraph.Core.Framework;
using Seraph.Core.State;
using Seraph.Network;
using Vintagestory.API.Common;
using Vintagestory.API.Server;

internal static class PressureChecks
{
    internal static void Run()
    {
        const BindingFlags flags = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static;
        T Proxy<T>(System.Func<MethodInfo, object?[]?, object?> f) => (T)RoutingProxy.Make(typeof(T), f);
        var slot = new DummySlot();
        var inventory = Proxy<IPlayerInventoryManager>((m, _) => m.Name == "get_ActiveHotbarSlot" ? slot : throw new Exception(m.Name));
        var entity = new EntityPlayer { Alive = true };
        var player = FixturePlayer.Make(entity, inventory);
        entity.WatchedAttributes.SetString("playerUID", player.PlayerUID);
        player.WorldData = Proxy<IWorldPlayerData>((m, _) => m.Name == "get_CurrentGameMode" ? EnumGameMode.Survival : throw new Exception(m.Name));
        long milliseconds = 1000;
        var calendar = Proxy<IGameCalendar>((m, _) => m.Name switch { "get_TotalHours" => (object)10d, "get_HoursPerDay" => 24f, _ => throw new Exception(m.Name) });
        var air = new Block { Code = new("game:air"), BlockId = 0 };
        var accessor = Proxy<IBlockAccessor>((m, _) => m.Name switch { "GetBlock" => air, "GetBlockEntity" => null, _ => throw new Exception(m.Name) });
        var world = Proxy<IServerWorldAccessor>((m, _) => m.Name switch {
            "get_BlockAccessor" => accessor, "get_ElapsedMilliseconds" => milliseconds, "get_Calendar" => calendar, "PlayerByUid" => player,
            _ => throw new Exception("Pressure world: " + m.Name)
        });
        entity.World = world;
        var logger = Proxy<ILogger>((_, _) => null);
        var api = Proxy<ICoreServerAPI>((m, _) => m.Name switch { "get_World" => world, "get_Logger" => logger, _ => throw new Exception(m.Name) });
        var type = typeof(SeraphModSystem).Assembly.GetType("Seraph.Server.ServerCoordinator", true)!;
        var server = Activator.CreateInstance(type, [api])!;
        type.GetField("capabilities", flags)!.SetValue(server, CapabilityRegistry.Create(new(), []));
        var st = type.GetNestedType("Session", flags)!;
        var session = Activator.CreateInstance(st, flags, null, [player], null)!;
        ((IDictionary)type.GetField("sessions", flags)!.GetValue(server)!).Add(player.PlayerUID, session);
        object? Call(string name, params object?[] args) => type.GetMethod(name, flags)!.Invoke(server, args);
        T Get<T>(string name) => (T)st.GetProperty(name)!.GetValue(session)!;
        void Set(string name, object? value) => st.GetProperty(name)!.SetValue(session, value);
        TextCommandResult Begin() => (TextCommandResult)Call("BeginPressure", session)!;
        PressureStatus Status() => (PressureStatus)Call("PressureProjection", session)!;
        bool Held() => st.GetProperty("Pressure")!.GetValue(session) is not null;
        void Require(bool b, string m) { if (!b) throw new Exception(m); }
        var kernel = new FrameworkKernel(new() { IntegrateBleeding = true });
        type.GetField("frameworkKernel", flags)!.SetValue(server, kernel);
        var frame = kernel.AddWound(FrameworkState.Create(player.PlayerUID), WoundKind.Cut, .3, .001).State;
        frame = kernel.AddWound(frame, WoundKind.Cut, .2, .0005).State;
        Set("Framework", frame);
        Require(Begin().Status == EnumCommandStatus.Success, "Valid P start rejected.");
        var status = Status();
        Require(status.Active && Math.Abs(status.BeforeMlPerMinute - 90) < 1e-7
            && Math.Abs(status.EffectiveMlPerMinute - 36) < 1e-7, "P must suppress only the largest wound by 90%.");
        var body = Get<BodyState>("State");
        Require(ReferenceEquals(body, Get<BodyState>("State")), "Starting pressure changed body reserves.");
        // Failed replacement must retain the accepted action.
        Require(((TextCommandResult)Call("BeginHeldDrink", player, session)!).Status == EnumCommandStatus.Error && Held(), "Failed drinking cancelled pressure.");
        Call("ValidatePressure", session); Require(Held(), "Valid pressure interrupted.");
        Set("Framework", WoundCare.Bandage(frame, frame.Wounds[0].EventSequence, false));
        Call("ValidatePressure", session);
        Require(Held() && Math.Abs(Status().EffectiveMlPerMinute - 3) < 1e-7, "Clotted wound did not retarget.");
        entity.Pos.X += 1; Call("ValidatePressure", session);
        Require(!Held() && Status().Reason == "movement", "Displacement did not interrupt P.");
        Set("Framework", frame);
        Require(Begin().Status == EnumCommandStatus.Success, "Restart failed.");
        milliseconds += 751; Call("ValidatePressure", session);
        Require(!Held() && Status().Reason == "timeout", "Lease expiry failed.");
        Require(Begin().Status == EnumCommandStatus.Success, "Restart after timeout failed.");
        entity.WatchedAttributes.SetInt("onHurtCounter", 1); Call("ValidatePressure", session);
        Require(!Held() && Status().Reason == "hurt", "Hurt interruption failed.");
        Require(Begin().Status == EnumCommandStatus.Success, "Restart after hurt failed.");
        entity.ServerControls.RightMouseDown = true; Call("ValidatePressure", session);
        Require(!Held() && Status().Reason == "conflict", "Native use failed to interrupt pressure.");
        entity.ServerControls.RightMouseDown = false;
        // Actual network dispatch, independent start throttle, idempotency and old-token protection.
        Set("HelloAccepted", true);
        var channel = Proxy<IServerNetworkChannel>((m, _) => m.Name == "SendPacket" ? null : throw new Exception(m.Name));
        type.GetField("channel", flags)!.SetValue(server, channel);
        Set("Environment", EnvironmentSample.Neutral);
        long sequence = 0;
        void Packet(string action, long token) => Call("OnWorldAction", player, new WorldActionRequest {
            SessionEpoch = Get<string>("Epoch"), Sequence = ++sequence, Action = action, ActionId = token });
        Set("LastWorldActionSeconds", milliseconds / 1000d);
        Packet("pressure-start", 1); Require(Held(), "Shared throttle swallowed pressure start.");
        var original = st.GetProperty("Pressure")!.GetValue(session);
        Packet("pressure-start", 1); Require(ReferenceEquals(original, st.GetProperty("Pressure")!.GetValue(session)), "Replay reset held pressure.");
        Packet("pressure-stop", 999); Require(Held(), "Old stop token cancelled pressure.");
        milliseconds += 500; Packet("pressure-hold", 1); Call("ValidatePressure", session); Require(Held(), "Valid heartbeat failed.");
        Packet("pressure-stop", 1); Require(!Held(), "Release failed.");
        Packet("pressure-start", 5); Require(Held(), "New accepted start failed.");
        Packet("pressure-stop", 1); Require(Held(), "Prior action cancelled the new action.");
        Call("CancelCompetingActions", session); Require(!Held(), "Accepted replacement failed to cancel P.");
        Require(player.SentMessages.Count == 0, "Pressure wrote chat instead of native warnings.");
        Console.WriteLine("PASS sustained P: real whole-body reduction, one wound, retarget, failed replacement, hurt/movement/timeout/native-use interruptions, network throttle/replay/token protection");
    }
}
