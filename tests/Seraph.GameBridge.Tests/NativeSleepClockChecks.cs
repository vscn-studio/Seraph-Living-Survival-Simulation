using System.Reflection;
using Seraph.Core.Framework;
using Seraph.Core.Simulation;
using Seraph.Core.State;
using Vintagestory.API.Client;
using Vintagestory.API.Common;
using Vintagestory.API.Datastructures;
using Vintagestory.API.Server;
using Vintagestory.GameContent;

internal static class NativeSleepClockChecks
{
    private const BindingFlags Flags = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static | BindingFlags.Instance;

    internal static void Acceleration(TreeAttribute config, EntityPlayer entity)
    {
        T Proxy<T>(System.Func<MethodInfo, object?[]?, object?> call) => (T)RoutingProxy.Make(typeof(T), call);
        float serverBoost = 0, clientBoost = 0;
        IGameCalendar Calendar(Action<float> set) => Proxy<IGameCalendar>((m, a) => {
            if (m.Name == "SetTimeSpeedModifier") { set((float)a![1]!); return null; }
            throw new Exception(m.Name);
        });
        var serverCalendar = Calendar(v => serverBoost = v);
        var clientCalendar = Calendar(v => clientBoost = v);
        var serverWorld = Proxy<IServerWorldAccessor>((m, _) => m.Name switch {
            "get_Calendar" => serverCalendar, "get_Config" => config, _ => throw new Exception(m.Name)
        });
        var serverApi = Proxy<ICoreServerAPI>((m, _) => m.Name switch {
            "get_Side" => EnumAppSide.Server, "get_World" => serverWorld, _ => throw new Exception(m.Name)
        });
        var clientWorld = Proxy<IClientWorldAccessor>((m, _) => m.Name switch {
            "get_Calendar" => clientCalendar, "get_Config" => config, "get_Player" => FixtureClientPlayer.Make(entity),
            _ => throw new Exception(m.Name)
        });
        var clientApi = Proxy<ICoreClientAPI>((m, _) => m.Name switch {
            "get_Side" => EnumAppSide.Client, "get_World" => clientWorld, _ => throw new Exception(m.Name)
        });
        var serverSleep = new ModSleeping();
        var clientSleep = new ModSleeping();
        typeof(ModSleeping).GetField("api", Flags)!.SetValue(serverSleep, serverApi);
        typeof(ModSleeping).GetField("api", Flags)!.SetValue(clientSleep, clientApi);
        typeof(ModSleeping).GetField("capi", Flags)!.SetValue(clientSleep, clientApi);
        typeof(ModSleeping).GetField("renderer", Flags)!.SetValue(clientSleep,
            System.Runtime.CompilerServices.RuntimeHelpers.GetUninitializedObject(typeof(EyesOverlayRenderer)));
        var tick = typeof(ModSleeping).GetMethod("FastTick", Flags)!;
        serverSleep.AllSleeping = clientSleep.AllSleeping = true;
        for (var i = 0; i < 2500; i++)
        {
            tick.Invoke(serverSleep, [.02f]); tick.Invoke(clientSleep, [.02f]);
            Require(serverBoost == clientBoost, "Client and server sleep boosts diverged.");
        }
        Require(serverBoost == 17000, "Native sleep acceleration remained capped.");
        serverSleep.AllSleeping = clientSleep.AllSleeping = false;
        for (var i = 0; i < 500; i++) { tick.Invoke(serverSleep, [.02f]); tick.Invoke(clientSleep, [.02f]); }
        Require(serverBoost == 0 && clientBoost == 0, "Sleep acceleration did not decay after waking.");
        Console.WriteLine("PASS actual client/server native sleep ramps match through 17000 boost and wake decay");
    }

    internal static void Settlement(Type serverType, object server, Type sessionType, object session)
    {
        // Shadow mode runs the same full physiological ledger without mutating native health.
        serverType.GetField("settings", Flags)!.SetValue(server,
            new Seraph.Application.SettingsAuthority(new() { Mode = "shadow" }));
        sessionType.GetProperty("State")!.SetValue(session, BodyState.Create() with { SleepPressure = .9 });
        sessionType.GetProperty("Framework")!.SetValue(session, FrameworkState.Create("sleep-catchup") with { Fatigue = .8 });
        sessionType.GetProperty("Environment")!.SetValue(session, EnvironmentSample.Neutral);
        sessionType.GetProperty("Kernel")!.SetValue(session, new SimulationKernel(BodyProfile.Default));
        var initialBody = sessionType.GetProperty("State")!.GetValue(session);
        var initialFrame = sessionType.GetProperty("Framework")!.GetValue(session);
        var initialInteractions = sessionType.GetProperty("Interactions")!.GetValue(session);
        var settle = serverType.GetMethod("SettleCalendarPeriod", Flags)!;
        var timer = System.Diagnostics.Stopwatch.StartNew();
        Require((bool)settle.Invoke(server, [session, new CalendarSleepPeriod(8, true, 1)])!, "Overnight physiology failed.");
        var after = (BodyState)sessionType.GetProperty("State")!.GetValue(session)!;
        var frame = (FrameworkState)sessionType.GetProperty("Framework")!.GetValue(session)!;
        var bowel = (InteractionState)sessionType.GetProperty("Interactions")!.GetValue(session)!;
        var scheduler = (CalendarTimeScheduler)sessionType.GetProperty("Scheduler")!.GetValue(session)!;
        Require(Math.Abs(after.ElapsedBioSeconds - 28800) < 1e-7 && scheduler.PendingSeconds == 0,
            "Native fast-forward left unsettled sleep time.");
        Require(after.SleepPressure < .1 && frame.Fatigue < .1 && after.BodyWaterL < BodyState.Create().BodyWaterL,
            "Overnight recovery or water consumption was omitted.");
        var vitaminADailyUse = Nutrients.All[Nutrients.IndexOf("a")].DepletionPerDay;
        Require(Math.Abs(frame.Nutrition.Get("a").Reserve - (50 - 8 * vitaminADailyUse / 24)) < 1e-7,
            "Overnight nutrition was skipped or charged twice.");
        Console.WriteLine($"PASS complete eight-hour physiological settlement: {timer.Elapsed.TotalMilliseconds:0.0} ms, no time debt");

        sessionType.GetProperty("State")!.SetValue(session, initialBody);
        sessionType.GetProperty("Framework")!.SetValue(session, initialFrame);
        sessionType.GetProperty("Interactions")!.SetValue(session, initialInteractions);
        scheduler.Reset();
        for (var i = 0; i < 240; i++)
            Require((bool)settle.Invoke(server, [session, new CalendarSleepPeriod(1d / 30, true, 1)])!,
                "Partitioned sleep settlement failed.");
        var partitionBody = (BodyState)sessionType.GetProperty("State")!.GetValue(session)!;
        var partitionFrame = (FrameworkState)sessionType.GetProperty("Framework")!.GetValue(session)!;
        Require(after == partitionBody && frame.Fatigue == partitionFrame.Fatigue
            && bowel == (InteractionState)sessionType.GetProperty("Interactions")!.GetValue(session)!
            && frame.Blood == partitionFrame.Blood && frame.Syndromes == partitionFrame.Syndromes
            && frame.Nutrition.Reserve.SequenceEqual(partitionFrame.Nutrition.Reserve)
            && frame.Nutrition.Pending.SequenceEqual(partitionFrame.Nutrition.Pending),
            "Accelerated sleep differs from the same bounded physiological steps over time.");
        Console.WriteLine("PASS overnight and partitioned settlement match water, fuel, temperature, sleep, fatigue and nutrients");
        FlowSamplingChecks.Run(serverType, server, sessionType, session);
    }

    private static void Require(bool condition, string reason) { if (!condition) throw new Exception(reason); }
}
