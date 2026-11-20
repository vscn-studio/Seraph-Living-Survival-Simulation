using System.Collections;
using System.Reflection;
using System.Text.Json;
using Seraph;
using Seraph.Core.State;
using Seraph.Core.Simulation;
using Seraph.Network;
using Vintagestory.API.Common;
using Vintagestory.API.Common.Entities;
using Vintagestory.API.Datastructures;
using Vintagestory.API.MathTools;
using Vintagestory.API.Server;
using Vintagestory.GameContent;

internal static class BedRestChecks
{
    private const BindingFlags Flags = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static | BindingFlags.Instance;
    private sealed class BedPlayer : EntityPlayer
    {
        public override IAnimationManager AnimManager { get; set; } = null!;
        public IMountableSeat? Accepted;
        public override bool TryMount(IMountableSeat seat) { Accepted = seat; return base.TryMount(seat); }
    }

    internal static void InitializeTranslations()
    {
        var root = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "../../../../../"));
        foreach (var locale in new[] { "en", "zh-cn" })
        {
            var translation = new Vintagestory.API.Config.TranslationService(locale, null!, null!,
                Vintagestory.API.Client.EnumLinebreakBehavior.AfterWord);
            var entries = new Dictionary<string, string>();
            foreach (var domain in new[] { "vsslssmod", "game" })
            foreach (var pair in JsonSerializer.Deserialize<Dictionary<string, string>>(
                File.ReadAllText(Path.Combine(root, "assets", domain, "lang", locale + ".json")))!)
                entries[domain + ":" + pair.Key] = pair.Value;
            typeof(Vintagestory.API.Config.TranslationService).GetField("entryCache", Flags)!
                .SetValue(translation, entries);
            typeof(Vintagestory.API.Config.TranslationService).GetField("loaded", Flags)!
                .SetValue(translation, true);
            Vintagestory.API.Config.Lang.AvailableLanguages[locale] = translation;
        }
        Vintagestory.API.Config.Lang.ChangeLanguage("zh-cn");
    }

    internal static void Run(bool samplingOnly = false)
    {
        T Proxy<T>(System.Func<MethodInfo, object?[]?, object?> call) => (T)RoutingProxy.Make(typeof(T), call);
        var entity = new BedPlayer { Alive = true };
        var player = FixturePlayer.Make(entity, null!);
        var mode = EnumGameMode.Survival;
        player.WorldData = Proxy<IWorldPlayerData>((m, _) => m.Name switch {
            "get_CurrentGameMode" => mode, "set_FreeMove" => null, _ => throw new Exception(m.Name)
        });
        var bed = new BlockBed { Code = new("game:bed-wood-head-north"), Attributes = JsonObject.FromJson("{\"sleepEfficiency\":0.79166666}") };
        var bedEntity = new BlockEntityBed { Block = bed, Pos = new(0, 0, 0, 0) };
        // Skip world collision/teleport queries on unmount, retaining the native mount and sleep lifecycle.
        typeof(BlockEntityBed).GetField("blockBroken", Flags)!.SetValue(bedEntity, true);
        var claims = Proxy<ILandClaimAPI>((m, _) => m.Name == "TryAccess" ? true : throw new Exception(m.Name));
        var accessor = Proxy<IBlockAccessor>((m, _) => m.Name switch {
            "GetBlockEntity" => bedEntity, "MarkBlockEntityDirty" => null, _ => throw new Exception(m.Name)
        });
        var config = new TreeAttribute(); config.SetString("temporalStormSleeping", "1");
        double hours = 10;
        var calendar = Proxy<IGameCalendar>((m, _) => m.Name switch {
            "get_TotalHours" => (object)hours, "get_HoursPerDay" => 24f, "get_SpeedOfTime" => 30f, _ => throw new Exception(m.Name)
        });
        var world = Proxy<IServerWorldAccessor>((m, _) => m.Name switch {
            "get_Side" => EnumAppSide.Server, "get_Claims" => claims, "get_BlockAccessor" => accessor,
            "get_Config" => config, "get_Calendar" => calendar, "PlayerByUid" => player, "get_ElapsedMilliseconds" => 1000L,
            _ => throw new Exception(m.Name)
        });
        var tasks = new Queue<Action>();
        var events = Proxy<IServerEventAPI>((m, a) => m.Name switch {
            "RegisterGameTickListener" => 1L, "UnregisterGameTickListener" => null,
            "EnqueueMainThreadTask" => Enqueue((Action)a![0]!), _ => throw new Exception(m.Name)
        });
        object? Enqueue(Action task) { tasks.Enqueue(task); return null; }
        var api = Proxy<ICoreServerAPI>((m, _) => m.Name switch {
            "get_World" => world, "get_Side" => EnumAppSide.Server, "get_Event" => events, _ => throw new Exception(m.Name)
        });
        entity.World = world; entity.Api = api; bedEntity.Api = api;
        typeof(CollectibleObject).GetField("api", Flags)!.SetValue(bed, api);
        typeof(Entity).GetProperty("Properties")!.SetValue(entity, new EntityProperties { Server = new EntityServerProperties([], new()) });
        entity.WatchedAttributes.SetString("playerUID", player.PlayerUID);
        entity.WatchedAttributes.SetAttribute("tiredness", new TreeAttribute());
        var tired = new EntityBehaviorTiredness(entity);
        entity.SidedProperties.Behaviors.Add(tired);
        var mod = typeof(SeraphModSystem).Assembly;
        var serverType = mod.GetType("Seraph.Server.ServerCoordinator", true)!;
        var server = Activator.CreateInstance(serverType, [api])!;
        serverType.GetField("capabilities", Flags)!.SetValue(server,
            Seraph.Application.CapabilityRegistry.Create(new(), []));
        var sessionType = serverType.GetNestedType("Session", Flags)!;
        var session = Activator.CreateInstance(sessionType, Flags, null, [player], null)!;
        ((IDictionary)serverType.GetField("sessions", Flags)!.GetValue(server)!).Add(player.PlayerUID, session);
        if (samplingOnly) { FlowSamplingChecks.Run(serverType, server, sessionType, session); return; }
        void Pressure(double pressure)
        {
            sessionType.GetProperty("State")!.SetValue(session, BodyState.Create() with { SleepPressure = pressure });
            serverType.GetMethod("ProjectSleep", Flags)!.Invoke(server, [session]);
        }
        var nativeBridge = mod.GetType("Seraph.Server.NativeCalendarBridge", true)!;
        using var bridge = (IDisposable)Activator.CreateInstance(nativeBridge, Flags, null, [server], null)!;
        var selection = new BlockSelection { Position = bedEntity.Pos, Face = BlockFacing.UP };
        void Mount()
        {
            tired.Tiredness = 12;
            Require(bed.OnBlockInteractStart(world, player, selection), "Native bed interaction failed.");
            while (tasks.TryDequeue(out var task)) task();
            Require(tired.IsSleeping && ReferenceEquals(entity.MountedOn, bedEntity)
                && ReferenceEquals(bedEntity.MountedBy, entity), "Native bed mount did not enter sleeping state.");
        }
        foreach (var pressure in new[] { .13, .2, .34, .8 })
        {
            Pressure(pressure); entity.Accepted = null;
            Mount();
            serverType.GetMethod("ProjectSleep", Flags)!.Invoke(server, [session]);
            Require(tired.IsSleeping, $"Actual native bed immediately woke at sleep pressure {pressure:P0}.");
            var before = tired.Tiredness;
            hours += .5;
            typeof(BlockEntityBed).GetMethod("RestPlayer", Flags)!.Invoke(bedEntity, [.2f]);
            Require(tired.Tiredness < before, "Native bed did not reduce tiredness.");
            Pressure(0);
            Require(tired.IsSleeping, "Low custom pressure prematurely woke native bed sleep.");
            hours += before * (.79166666 - 1d / 12);
            typeof(BlockEntityBed).GetMethod("RestPlayer", Flags)!.Invoke(bedEntity, [.2f]);
            Require(!tired.IsSleeping && entity.MountedOn is null, "Native tiredness failed to wake the bed.");
        }
        Pressure(.8); tired.Tiredness = 8; entity.Accepted = null;
        Require(bed.OnBlockInteractStart(world, player, selection),
            "High sleep pressure did not bypass the native tiredness gate.");
        while (tasks.TryDequeue(out var highPressureTask)) highPressureTask();
        Require(tired.IsSleeping && entity.MountedOn is not null,
            "High sleep pressure did not mount a tired player.");
        entity.TryUnmount();
        Pressure(.2); tired.Tiredness = 8; entity.Accepted = null;
        Require(!bed.OnBlockInteractStart(world, player, selection) && entity.Accepted is null,
            "Low sleep pressure incorrectly bypassed native tiredness.");
        tired.Tiredness = 24;
        serverType.GetMethod("RestoreNativeTiredness", Flags)!.Invoke(server, [session]);
        Require(tired.Tiredness == 12, "Legacy projected tiredness was not bounded to the native day.");
        var timeline = (CalendarSleepTimeline)sessionType.GetProperty("SleepTimeline")!.GetValue(session)!;
        timeline.Reset(false, 1);
        var start = hours;
        hours += .1; Mount();
        hours += .5; entity.TryUnmount();
        hours += .2;
        var periods = timeline.Consume(start, hours);
        Require(periods.Count == 3 && !periods[0].Sleeping && periods[1].Sleeping && !periods[2].Sleeping
            && Math.Abs(periods[1].Hours - .5) < 1e-9 && periods[1].BedQuality > .98,
            "Native sleep/wake hooks lost the interval or bed quality after unmount.");
        NativeSleepClockChecks.Acceleration(config, entity);

        var describe = mod.GetType("Seraph.Client.BedRestText", true)!.GetMethod("Describe", Flags)!;
        var snapshot = new ServerSnapshot { SleepPressure = .8, BedRest = new() {
            EnvironmentEfficiency = 1, PressureRecoveryPerGameHour = .125, FatigueRecoveryPerGameHour = .125
        } };
        var text = (string)describe.Invoke(null, [bed, snapshot])!;
        var hay = new BlockBed { Code = new("game:bed-hay-head-north"), Attributes = JsonObject.FromJson("{\"sleepEfficiency\":0.58333333}") };
        var slower = (string)describe.Invoke(null, [hay, snapshot])!;
        Require(text.Contains("12.4点") && !text.Contains("\n") && text != slower,
            "Localized bed recovery tooltip does not use the actual bed/time constants.");
        sessionType.GetProperty("Environment")!.SetValue(session, EnvironmentSample.Neutral);
        var rest = serverType.GetMethod("ProjectBedRest", Flags)!.Invoke(server, [session]);
        Require(rest is BedRestSnapshot { EnvironmentEfficiency: > 0, PressureRecoveryPerGameHour: > 0, FatigueRecoveryPerGameHour: > 0 },
            "Server snapshot omitted authoritative bed recovery parameters.");
        Pressure(.8);
        var healthy = (BodyState)sessionType.GetProperty("State")!.GetValue(session)!;
        var dehydrated = healthy with { BodyWaterL = BodyProfile.Default.ReferenceBodyWaterL - 6.1 };
        sessionType.GetProperty("State")!.SetValue(session, dehydrated);
        Mount();
        serverType.GetMethod("ProjectSleep", Flags)!.Invoke(server, [session]);
        Require(tired.IsSleeping && ReferenceEquals(entity.MountedOn, bedEntity),
            "Existing dehydration must not block bed rest or time acceleration.");
        Require(player.IngameErrors.Count == 1 && player.IngameErrors[0] == ("vsslssmod-sleep-danger", "当前身体处于危险中，不建议睡觉") && player.SentMessages.Count == 0, "Entering a dangerous sleep did not notify the player.");
        serverType.GetMethod("ProjectSleep", Flags)!.Invoke(server, [session]);
        Require(player.IngameErrors.Count == 1, "Persistent sleep danger spammed the player every tick.");
        var prepare = serverType.GetMethod("PrepareStepInputs", Flags)!;
        var inputs = (Seraph.Core.Simulation.StepInputs)prepare.Invoke(server, [session,
            sessionType.GetProperty("Framework")!.GetValue(session), dehydrated, true, .99d, 0d, 8d])!;
        var seatedInputs = (Seraph.Core.Simulation.StepInputs)prepare.Invoke(server, [session,
            sessionType.GetProperty("Framework")!.GetValue(session), dehydrated, false, .99d, 0d, 8d])!;
        Require(inputs.SleepPressureRecoverySeconds == 14400 && seatedInputs.SleepPressureRecoverySeconds == 28800
            && rest is BedRestSnapshot { PressureRecoveryPerGameHour: .25 },
            "Bed recovery and its tooltip diverged, or bed pacing leaked into K/seated rest.");
        var kernel = new Seraph.Core.Simulation.SimulationKernel(BodyProfile.Default);
        var recovery = kernel.Advance(dehydrated, EnvironmentSample.Neutral, inputs, 8);
        Require(recovery.Accepted && recovery.State.SleepPressure < dehydrated.SleepPressure,
            "Ordinary unaccelerated bed rest no longer recovers sleep pressure.");
        entity.TryUnmount();

        sessionType.GetProperty("State")!.SetValue(session, healthy);
        Mount();
        Require(tired.IsSleeping,
            "Healthy bed sleep cannot accelerate time.");
        sessionType.GetProperty("State")!.SetValue(session, dehydrated);
        serverType.GetMethod("ProjectSleep", Flags)!.Invoke(server, [session]);
        Require(tired.IsSleeping && entity.MountedOn is not null
            && player.IngameErrors.Count == 2, "New danger must notify without stopping sleep or acceleration.");
        sessionType.GetProperty("State")!.SetValue(session, dehydrated with { CoreTemperatureC = 39.5 });
        serverType.GetMethod("ProjectSleep", Flags)!.Invoke(server, [session]);
        Require(tired.IsSleeping && player.IngameErrors.Count == 3 && player.IngameErrors[^1].Message == "当前身体处于危险中，不建议睡觉",
            "An additional danger during an already dangerous sleep was not reported.");
        entity.TryUnmount();
        foreach (var risk in new[] { healthy with { CoreTemperatureC = 35 }, healthy with { CoreTemperatureC = 39 },
            healthy with { RbcVolumeL = healthy.RbcVolumeL * .5 } })
        {
            sessionType.GetProperty("State")!.SetValue(session, risk);
            Mount();
            serverType.GetMethod("ProjectSleep", Flags)!.Invoke(server, [session]);
            Require(tired.IsSleeping && entity.MountedOn is not null,
                "Thermal danger or blood loss blocked sleeping/time acceleration.");
            entity.TryUnmount();
        }
        sessionType.GetProperty("State")!.SetValue(session, healthy);
        var oxygen = new TreeAttribute(); oxygen.SetBool("hasair", false);
        entity.WatchedAttributes.SetAttribute("oxygen", oxygen);
        Mount();
        serverType.GetMethod("ProjectSleep", Flags)!.Invoke(server, [session]);
        Require(tired.IsSleeping && player.IngameErrors[^1].Message == "当前身体处于危险中，不建议睡觉" && player.SentMessages.Count == 0, "Air danger failed to warn or blocked acceleration.");
        Pressure(.12);
        Require(tired.IsSleeping && entity.MountedOn is not null, "Custom 12% pressure still overrides native sleep.");
        entity.TryUnmount();
        entity.WatchedAttributes.RemoveAttribute("oxygen");
        sessionType.GetProperty("State")!.SetValue(session, dehydrated);
        serverType.GetMethod("ProjectSleep", Flags)!.Invoke(server, [session]);

        mode = EnumGameMode.Creative;
        var creativeRest = serverType.GetMethod("ProjectBedRest", Flags)!.Invoke(server, [session]);
        Require(creativeRest is BedRestSnapshot { PressureRecoveryPerGameHour: > 0 }, "Creative bed tooltip has no server data.");
        Mount();
        Require(tired.IsSleeping,
            "Frozen survival physiology interrupted creative mode's native sleep.");
        entity.TryUnmount();
        Require(ReferenceEquals(sessionType.GetProperty("State")!.GetValue(session), dehydrated),
            "Creative bed projection mutated stored physiology.");
        mode = EnumGameMode.Survival;
        NativeSleepClockChecks.Settlement(serverType, server, sessionType, session);
        Console.WriteLine("PASS native bed duration/admission, sleep boundaries, danger warnings and complete calendar settlement");
    }

    private static void Require(bool condition, string reason) { if (!condition) throw new Exception(reason); }
}
