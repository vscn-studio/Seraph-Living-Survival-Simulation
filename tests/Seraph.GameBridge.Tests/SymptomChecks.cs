using System.Linq.Expressions;
using System.Reflection;
using System.Runtime.CompilerServices;

internal static class SymptomChecks
{
    private const BindingFlags All = BindingFlags.Instance | BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic;
    internal static void Run(Assembly mod)
    {
        var cycleType = mod.GetType("Seraph.Client.EyeClosureCycle", true)!;
        foreach (var severity in new[] { .9, .95, 1d }) foreach (var dt in new[] { .01, 1d / 30, 1d / 144 })
        {
            var cycle = Activator.CreateInstance(cycleType, true)!;
            var advance = cycleType.GetMethod("Advance")!;
            var closure = cycleType.GetProperty("Closure")!;
            double plateau = 0; var seen = false; var reopened = false;
            double? firstClosing = null, firstWarning = null;
            for (double t = 0; t < 70; t += dt)
            {
                advance.Invoke(cycle, [dt, severity]);
                var value = (double)closure.GetValue(cycle)!;
                if ((bool)cycleType.GetProperty("Warning")!.GetValue(cycle)!) firstWarning ??= t + dt;
                if (value > 0) firstClosing ??= t + dt;
                Require(double.IsFinite(value) && value is >= 0 and <= 1, "Invalid eye coverage.");
                if (value == 1) { plateau += dt; seen = true; }
                if (seen && value == 0) { reopened = true; break; }
            }
            var expected = severity == .9 ? .25 : severity == .95 ? .4 : .7;
            var interval = severity == .9 ? 60 : severity == .95 ? 45 : 25;
            Require(firstClosing.HasValue && Math.Abs(firstClosing.Value - interval) <= dt * 1.1,
                "Open-eye interval differs from the staged real-time anchor.");
            Require(firstWarning.HasValue && Math.Abs(firstWarning.Value - (interval - 1.5)) <= dt * 1.1,
                "Pre-closure warning differs from 1.5 seconds.");
            Require(reopened && Math.Abs(plateau - expected) < dt * 2.1, "Closed-eye plateau/time-step mismatch.");
            advance.Invoke(cycle, [double.NaN, 1d]);
            cycleType.GetMethod("Reset")!.Invoke(cycle, null);
            advance.Invoke(cycle, [1000d, 1d]);
            Require((double)closure.GetValue(cycle)! == 0, "Stall generated a catch-up closure.");
            advance.Invoke(cycle, [.1, double.NaN]);
            Require((double)closure.GetValue(cycle)! == 0, "Non-finite severity retained closure.");
        }
        Console.WriteLine("PASS pressure 90/95/100% eye timing at 30/100/144 Hz, bounded stalls and reset");

        // Real Harmony installation and state ownership without starting a game or requiring an OpenGL context.
        var api = Assembly.Load("VintagestoryAPI");
        Type Api(string name) => api.GetType("Vintagestory.API." + name, true)!;
        var uniforms = Activator.CreateInstance(Api("Client.DefaultShaderUniforms"))!;
        var player = Activator.CreateInstance(Api("Common.EntityPlayer"))!;
        var paused = false; var grabbed = true;
        var dialogs = (System.Collections.IList)Activator.CreateInstance(typeof(List<>).MakeGenericType(Api("Client.GuiDialog")))!;
        var gui = RoutingProxy.Make(Api("Client.IGuiAPI"), (method, _) => method.Name == "get_OpenedGuis" ? dialogs : Default(method));
        object? Default(MethodInfo method) => method.ReturnType == typeof(void) ? null
            : method.ReturnType.IsValueType ? Activator.CreateInstance(method.ReturnType) : null;
        var render = RoutingProxy.Make(Api("Client.IRenderAPI"), (method, _) => method.Name == "get_ShaderUniforms" ? uniforms : Default(method));
        var yaw = .4f; var pitch = 3.1f; var yawWrites = 0;
        var input = RoutingProxy.Make(Api("Client.IInputAPI"), (method, args) => {
            if (method.Name == "get_MouseGrabbed") return grabbed;
            if (method.Name == "get_MouseYaw") return yaw;
            if (method.Name == "get_MousePitch") return pitch;
            if (method.Name == "set_MouseYaw") { yaw = (float)args![0]!; yawWrites++; return null; }
            if (method.Name == "set_MousePitch") { pitch = (float)args![0]!; return null; }
            return Default(method);
        });
        var clientPlayerType = Assembly.Load("VintagestoryLib").GetType("Vintagestory.Client.NoObf.ClientPlayer", true)!;
        var clientPlayer = RuntimeHelpers.GetUninitializedObject(clientPlayerType);
        var worldDataField = clientPlayerType.GetField("worlddata", All)!;
        var worldData = RuntimeHelpers.GetUninitializedObject(worldDataField.FieldType);
        worldDataField.FieldType.GetField("entityplayer", All)!.SetValue(worldData, player);
        worldDataField.SetValue(clientPlayer, worldData);
        var calendar = RoutingProxy.Make(Api("Common.IClientGameCalendar"), (method, _) => method.Name switch {
            "get_SpeedOfTime" => 30f, "get_HoursPerDay" => 24f, _ => Default(method)
        });
        var world = RoutingProxy.Make(Api("Client.IClientWorldAccessor"), (method, _) => method.Name switch {
            "get_Player" => clientPlayer, "get_Calendar" => calendar, _ => Default(method)
        });
        var events = RoutingProxy.Make(Api("Client.IClientEventAPI"), (method, _) => Default(method));
        var logger = RoutingProxy.Make(Api("Common.ILogger"), (method, _) => Default(method));
        var core = RoutingProxy.Make(Api("Client.ICoreClientAPI"), (method, _) => method.Name switch {
            "get_IsGamePaused" => paused, "get_Input" => input, "get_Render" => render, "get_Gui" => gui,
            "get_World" => world, "get_Event" => events, "get_Logger" => logger, _ => Default(method)
        });
        var coldStrength = mod.GetType("Seraph.Server.NativeColdVisuals", true)!.GetMethod("Strength", All)!;
        foreach (var (temperature, expected) in new[] { (37d, 0f), (35d, 0f), (34d, .25f), (33d, .5f), (31d, 1f), (25d, 1f) })
            Require((float)coldStrength.Invoke(null, [temperature, true])! == expected, "Native cold threshold mismatch.");
        Require((float)coldStrength.Invoke(null, [30d, false])! == 0
            && (float)coldStrength.Invoke(null, [double.NaN, true])! == 0, "Disabled or invalid cold feedback retained.");
        // Exercise the installed game's renderer, including smoothing and pause. Stay
        // below its shivering threshold here so this headless fixture needs no camera.
        var nativeCold = new Vintagestory.API.Client.FreezingPerceptionEffect((Vintagestory.API.Client.ICoreClientAPI)core);
        var coldPlayer = (Vintagestory.API.Common.EntityPlayer)player;
        var frostUniforms = (Vintagestory.API.Client.DefaultShaderUniforms)uniforms;
        coldPlayer.WatchedAttributes.SetFloat("freezingEffectStrength", .08f);
        nativeCold.OnBeforeGameRender(.5f);
        Require(Math.Abs(frostUniforms.FrostVignetting - .04f) < 1e-6, "Native frost did not consume projected strength.");
        paused = true;
        nativeCold.OnBeforeGameRender(1);
        Require(Math.Abs(frostUniforms.FrostVignetting - .04f) < 1e-6, "Native frost advanced while paused.");
        paused = false;
        coldPlayer.WatchedAttributes.SetFloat("freezingEffectStrength", 0);
        nativeCold.OnBeforeGameRender(1);
        Require(frostUniforms.FrostVignetting == 0, "Native frost failed to clear after warming.");
        Console.WriteLine("PASS native hypothermia thresholds, stock frost renderer, pause and warming recovery");
        var snapshotType = mod.GetType("Seraph.Network.ServerSnapshot", true)!;
        var state = Activator.CreateInstance(snapshotType)!;
        snapshotType.GetProperty("SymptomVisuals")!.SetValue(state, true);
        snapshotType.GetProperty("SleepPressure")!.SetValue(state, 1d);
        var effectType = mod.GetType("Seraph.Network.ClinicalEffectSnapshot", true)!;
        var effect = Activator.CreateInstance(effectType)!;
        effectType.GetProperty("Key")!.SetValue(effect, "drowsy"); effectType.GetProperty("Severity")!.SetValue(effect, 1d);
        var effects = Array.CreateInstance(effectType, 1); effects.SetValue(effect, 0);
        snapshotType.GetProperty("ClinicalEffects")!.SetValue(state, effects);
        var gaugeType = mod.GetType("Seraph.Network.HudGauge", true)!;
        var gauge = Activator.CreateInstance(gaugeType)!;
        gaugeType.GetProperty("Key")!.SetValue(gauge, "fatigue"); gaugeType.GetProperty("Value")!.SetValue(gauge, 1d);
        gaugeType.GetProperty("Available")!.SetValue(gauge, true);
        var gauges = Array.CreateInstance(gaugeType, 1); gauges.SetValue(gauge, 0);
        snapshotType.GetProperty("Gauges")!.SetValue(state, gauges);
        var connected = true;
        var channel = RoutingProxy.Make(Api("Client.IClientNetworkChannel"), (method, _) =>
            method.Name == "get_Connected" ? connected : Default(method));
        var bridgeType = mod.GetType("Seraph.Client.SeraphClientBridge", true)!;
        var bridge = RuntimeHelpers.GetUninitializedObject(bridgeType);
        bridgeType.GetField("api", All)!.SetValue(bridge, core);
        bridgeType.GetField("channel", All)!.SetValue(bridge, channel);
        bridgeType.GetProperty("Snapshot")!.SetValue(bridge, state);
        bridgeType.GetProperty("LastReceivedUtc")!.SetValue(bridge, DateTime.UtcNow.AddSeconds(-30));
        var visualSnapshot = bridgeType.GetMethod("GetSymptomSnapshot", All)!;
        Require(visualSnapshot.Invoke(bridge, null) is null, "Normal stale visuals retained.");
        paused = true;
        Require(ReferenceEquals(visualSnapshot.Invoke(bridge, null), state), "Paused singleplayer lost visual snapshot after five seconds.");
        Require(!(bool)bridgeType.GetProperty("HasFreshSnapshot")!.GetValue(bridge)!, "Presentation exception extended action authority.");
        connected = false;
        Require(visualSnapshot.Invoke(bridge, null) is null, "Paused disconnect retained visual snapshot.");
        connected = true; bridgeType.GetField("connectionError", All)!.SetValue(bridge, "test error");
        Require(visualSnapshot.Invoke(bridge, null) is null, "Paused connection error retained visual snapshot.");
        paused = false;
        Console.WriteLine("PASS paused snapshot retention is visual-only and rejects disconnect/handshake failure");
        var cell = new Cell { Value = state };
        var getter = Expression.Lambda(typeof(Func<>).MakeGenericType(snapshotType),
            Expression.Convert(Expression.Field(Expression.Constant(cell), nameof(Cell.Value)), snapshotType)).Compile();
        var rendererType = mod.GetType("Seraph.Client.SymptomRenderer", true)!;
        using var renderer = (IDisposable)Activator.CreateInstance(rendererType, All, null, [core, getter], null)!;
        var harmonyType = Assembly.Load("0Harmony").GetType("HarmonyLib.Harmony", true)!;
        var patched = (IEnumerable<MethodBase>)harmonyType.GetMethod("GetAllPatchedMethods")!.Invoke(null, null)!;
        Require(patched.Any(m => m.DeclaringType?.FullName == "Vintagestory.Client.NoObf.ClientMain"
            && m.Name == "RenderToDefaultFramebuffer"), "Missing final window composition hook (F4-independent).");
        var managerType = Api("Client.PerceptionEffects");
        var manager = Activator.CreateInstance(managerType, [core])!;
        var tick = managerType.GetMethod("OnBeforeGameRender")!;
        var gate = rendererType.GetMethod("RenderGuis", All)!;
        var id = uniforms.GetType().GetField("PerceptionEffectId")!;
        var intensity = uniforms.GetType().GetField("PerceptionEffectIntensity")!;
        id.SetValue(uniforms, 2); intensity.SetValue(uniforms, .8f);
        for (var i = 0; i < 103; i++) tick.Invoke(manager, [.25f]);
        Require(!(bool)gate.Invoke(null, null)!, "HUD was not hidden during closure.");
        Require((float)intensity.GetValue(uniforms)! == .8f, "Fatigue stacked with/overwrote stronger alcohol.");
        var eyes = rendererType.GetField("eyes", All)!.GetValue(renderer)!;
        var closureProperty = cycleType.GetProperty("Closure")!;
        var closureBeforePause = (double)closureProperty.GetValue(eyes)!;
        paused = true;
        for (var i = 0; i < 100; i++) tick.Invoke(manager, [.25f]);
        Require(!(bool)gate.Invoke(null, null)! && (double)closureProperty.GetValue(eyes)! == closureBeforePause
            && (float)intensity.GetValue(uniforms)! == .8f, "Pause cleared or advanced visual state.");
        var escapeType = Assembly.Load("VintagestoryLib").GetType("Vintagestory.Client.NoObf.GuiDialogEscapeMenu", true)!;
        dialogs.Add(RuntimeHelpers.GetUninitializedObject(escapeType));
        Require((bool)gate.Invoke(null, null)!, "Closed eyes suppressed the pause/settings menu itself.");
        dialogs.Clear();
        paused = false; grabbed = false; tick.Invoke(manager, [.1f]);
        Require(!(bool)gate.Invoke(null, null)! && (float)intensity.GetValue(uniforms)! == .8f,
            "Opening a window cleared symptoms/HUD suppression.");
        rendererType.GetMethod("Reset", All)!.Invoke(renderer, null);
        id.SetValue(uniforms, 0); intensity.SetValue(uniforms, 0f);
        for (var i = 0; i < 103; i++) tick.Invoke(manager, [.25f]);
        Require(!(bool)gate.Invoke(null, null)! && (int)id.GetValue(uniforms)! == 0,
            "Ungrabbed mouse prevented symptoms, or fatigue wrote native distortion.");
        // Config off, stale data and disposal still release only this mod's contributions.
        snapshotType.GetProperty("SymptomVisuals")!.SetValue(state, false); tick.Invoke(manager, [.1f]);
        Require((bool)gate.Invoke(null, null)! && (int)id.GetValue(uniforms)! == 0, "Visual configuration off did not clean up.");
        snapshotType.GetProperty("SymptomVisuals")!.SetValue(state, true); tick.Invoke(manager, [.1f]);
        grabbed = true; cell.Value = null; tick.Invoke(manager, [.1f]);
        Require((bool)gate.Invoke(null, null)! && (int)id.GetValue(uniforms)! == 0, "Stale snapshot retained HUD/warp.");
        cell.Value = state; tick.Invoke(manager, [.1f]);
        snapshotType.GetProperty("SleepBlurSeverity")!.SetValue(state, .8d);
        cell.Value = state; tick.Invoke(manager, [.1f]);
        var blurField = rendererType.GetField("blur", All)!;
        for (var i = 0; i < 8; i++) tick.Invoke(manager, [.25f]);
        Require(Math.Abs((double)blurField.GetValue(renderer)! - .5) < 1e-9, "Legacy saved blur bypassed current maximum.");
        snapshotType.GetProperty("SleepPressure")!.SetValue(state, .5d);
        for (var i = 0; i < 8; i++) tick.Invoke(manager, [.25f]);
        Require((double)blurField.GetValue(renderer)! == 0, "100% to 50% pressure failed to clear saved blur within two seconds.");
        var reliefState = Activator.CreateInstance(snapshotType)!;
        snapshotType.GetProperty("SymptomVisuals")!.SetValue(reliefState, true);
        snapshotType.GetProperty("SleepPressure")!.SetValue(reliefState, 1d);
        snapshotType.GetProperty("SleepBlurSeverity")!.SetValue(reliefState, .8d);
        snapshotType.GetProperty("AlertnessReliefSeconds")!.SetValue(reliefState, 150d);
        cell.Value = reliefState; tick.Invoke(manager, [.1f]);
        Require((double)blurField.GetValue(renderer)! == 0 && (double)closureProperty.GetValue(eyes)! == 0, "Confirmed intake failed to relieve sleep visuals.");
        for (var i = 0; i < 24; i++) tick.Invoke(manager, [.25f]);
        Require(Math.Abs((double)blurField.GetValue(renderer)! - .5) < 1e-9, "Relief did not return to current-load blur.");
        snapshotType.GetProperty("IsSleeping")!.SetValue(reliefState, true); tick.Invoke(manager, [.1f]);
        Require((double)blurField.GetValue(renderer)! == 0, "Actual sleep retained sleep blur.");
        // K-key closed-eye rest owns a persistent eyelid overlay while active,
        // then returns to the normal drowsy cycle after release.
        snapshotType.GetProperty("IsSleeping")!.SetValue(reliefState, false);
        snapshotType.GetProperty("IsClosedEyeResting")!.SetValue(reliefState, true);
        snapshotType.GetProperty("SleepPressure")!.SetValue(reliefState, 0d);
        snapshotType.GetProperty("SymptomVisuals")!.SetValue(reliefState, false);
        rendererType.GetMethod("Reset", All)!.Invoke(renderer, null);
        cell.Value = reliefState;
        var voluntaryYawWrites = yawWrites;
        for (var i = 0; i < 15; i++) tick.Invoke(manager, [.05f]);
        Require(yawWrites == voluntaryYawWrites, "Voluntary K rest turned the view.");
        var closedEyeField = rendererType.GetField("closedEyeClosure", All)!;
        Require((double)closedEyeField.GetValue(renderer)! == 1 && !(bool)gate.Invoke(null, null)!,
            "K rest failed to fully close eyes/hide HUD in 0.75 seconds with zero sleep pressure and symptoms disabled.");
        paused = true;
        tick.Invoke(manager, [.25f]);
        Require((double)closedEyeField.GetValue(renderer)! == 1, "Paused voluntary closure changed.");
        paused = false;
        snapshotType.GetProperty("IsClosedEyeResting")!.SetValue(reliefState, false);
        for (var i = 0; i < 19; i++) tick.Invoke(manager, [.05f]);
        Require((double)closedEyeField.GetValue(renderer)! == 0 && (bool)gate.Invoke(null, null)!,
            "K rest did not fully reopen/release HUD within 0.95 seconds.");
        Console.WriteLine("PASS voluntary K closure reaches full black and hides HUD, independent of symptom config/low sleep pressure, freezes in pause and reopens on release");
        // Exercise the renderer's real closure transition, not a separate turn timer.
        var entityType = Api("Common.Entities.Entity");
        var controls = player.GetType().GetProperty("Controls", All)!.GetValue(player)!;
        var flags = (bool[])controls.GetType().GetProperty("Flags")!.GetValue(controls)!;
        controls.GetType().GetProperty("Forward")!.SetValue(controls, true);
        var walk = controls.GetType().GetField("WalkVector")!.GetValue(controls)!;
        walk.GetType().GetField("X")!.SetValue(walk, .3d); walk.GetType().GetField("Z")!.SetValue(walk, .4d);
        var heldFlags = (bool[])flags.Clone();
        var functionsType = mod.GetType("Seraph.Network.FunctionSnapshot", true)!;
        var functions = Activator.CreateInstance(functionsType)!;
        functionsType.GetProperty("Movement")!.SetValue(functions, true);
        snapshotType.GetProperty("Functions")!.SetValue(state, functions);
        snapshotType.GetProperty("DrowsyMovement")!.SetValue(state, true);
        snapshotType.GetProperty("SleepPressure")!.SetValue(state, 1d);
        cell.Value = state; grabbed = true;
        entityType.GetField("OnGround")!.SetValue(player, true);
        var reset = rendererType.GetMethod("Reset", All)!;
        var warningProperty = cycleType.GetProperty("Warning")!;
        var edgeProperty = cycleType.GetProperty("JustClosed")!;
        void Frame() => tick.Invoke(manager, [.05f]);
        void ToClosed()
        {
            for (var frame = 0; frame < 1400; frame++)
            {
                Frame();
                if ((bool)edgeProperty.GetValue(eyes)!) return;
            }
            throw new Exception("No full-closure transition.");
        }
        reset.Invoke(renderer, null);
        var before = yawWrites; var warned = false;
        for (var frame = 0; frame < 1400; frame++)
        {
            Frame();
            var closure = (double)closureProperty.GetValue(eyes)!;
            if ((bool)warningProperty.GetValue(eyes)!) { warned = true; Require(closure == 0, "Warning overlapped eyelids."); }
            if ((bool)edgeProperty.GetValue(eyes)!) break;
            Require(yawWrites == before, "Turn happened before full closure.");
        }
        Require(warned && yawWrites == before + 1 && (double)closureProperty.GetValue(eyes)! == 1,
            "Warning/full-closure single-turn order failed.");
        var turnDelta = Math.Abs(Math.IEEERemainder(yaw - .4, 2 * Math.PI));
        Require(turnDelta >= Math.PI / 3 - 1e-6 && turnDelta <= Math.PI + 1e-6 && pitch == 3.1f,
            "Turn range or unchanged pitch failed.");
        var pos = player.GetType().GetProperty("Pos")!.GetValue(player)!;
        Require((float)pos.GetType().GetProperty("Yaw")!.GetValue(pos)! == yaw, "Native view/entity yaw diverged.");
        // Mouse input remains effective immediately, including during the fully closed second.
        yaw = 1.234f;
        paused = true; Frame(); paused = false;
        Require(yawWrites == before + 1, "Paused closure repeated a turn.");
        for (var frame = 0; frame < 12; frame++) Frame();
        Require((double)closureProperty.GetValue(eyes)! == 1 && yaw == 1.234f && yawWrites == before + 1,
            "Closed eyes overrode subsequent mouse input or repeated a turn.");
        for (var frame = 0; frame < 40; frame++) Frame();
        Require((double)closureProperty.GetValue(eyes)! == 0 && yaw == 1.234f && yawWrites == before + 1,
            "Opening eyes restored/changed orientation.");
        Require(flags.SequenceEqual(heldFlags) && (double)walk.GetType().GetField("X")!.GetValue(walk)! == .3
            && (double)walk.GetType().GetField("Z")!.GetValue(walk)! == .4, "Turn changed native held keys or movement vectors.");
        ToClosed(); Require(yawWrites == before + 2, "Next closure did not turn once.");
        void Denied(Action enable, Action disable, string reason)
        {
            reset.Invoke(renderer, null); enable(); var count = yawWrites; ToClosed();
            Require(yawWrites == count, reason + " allowed a turn.");
            disable(); for (var frame = 0; frame < 20; frame++) Frame();
            Require(yawWrites == count, reason + " queued a delayed turn.");
        }
        var awake = mod.GetType("Seraph.Server.ServerCoordinator", true)!
            .GetMethod("ClosedEyeAwake", BindingFlags.Static | BindingFlags.NonPublic)!;
        var serverEntity = new Vintagestory.API.Common.EntityPlayer();
        Require((bool)awake.Invoke(null, [serverEntity])!, "Missing native tiredness suppressed automatic turns.");
        var tired = new Vintagestory.API.Datastructures.TreeAttribute();
        serverEntity.WatchedAttributes.SetAttribute("tiredness", tired);
        tired.SetInt("isSleeping", 1);
        Require(!(bool)awake.Invoke(null, [serverEntity])!, "Native sleeping allowed automatic turns.");
        tired.SetInt("isSleeping", 0);
        Require((bool)awake.Invoke(null, [serverEntity])!, "Waking did not restore turn authorization.");
        Denied(() => grabbed = false, () => grabbed = true, "Ungrabbed mouse");
        Denied(() => dialogs.Add(RuntimeHelpers.GetUninitializedObject(escapeType)), () => dialogs.Clear(), "Menu");
        Denied(() => snapshotType.GetProperty("DrowsyMovement")!.SetValue(state, false),
            () => snapshotType.GetProperty("DrowsyMovement")!.SetValue(state, true), "Authority/config");
        Denied(() => functionsType.GetProperty("Movement")!.SetValue(functions, false),
            () => functionsType.GetProperty("Movement")!.SetValue(functions, true), "Movement capability");
        Denied(() => entityType.GetField("OnGround")!.SetValue(player, false),
            () => entityType.GetField("OnGround")!.SetValue(player, true), "Airborne");
        Denied(() => entityType.GetField("Swimming")!.SetValue(player, true),
            () => entityType.GetField("Swimming")!.SetValue(player, false), "Swimming");
        foreach (var field in new[] { "IsFlying", "IsClimbing" })
            Denied(() => controls.GetType().GetField(field)!.SetValue(controls, true),
                () => controls.GetType().GetField(field)!.SetValue(controls, false), field);
        Denied(() => controls.GetType().GetProperty("FloorSitting")!.SetValue(controls, true),
            () => controls.GetType().GetProperty("FloorSitting")!.SetValue(controls, false), "Seated");
        Console.WriteLine("PASS pre-closure warning, single yaw change at full closure, immediate mouse control while closed, no reopening change, native WASD retained, exclusions consume edge");
        reset.Invoke(renderer, null);
        var sync = new Seraph.Network.ServerSnapshot {
            SymptomVisuals = true, DrowsyMovement = true, SleepPressure = 1,
            Functions = new() { Movement = true }, SessionEpoch = "sync-eyes", LifeGeneration = 4,
            SleepEyes = new() { Closing = true, Phase = .75, Hold = .7, Sequence = 100 }
        };
        cell.Value = sync; var syncTurns = yawWrites; Frame();
        Require((double)closureProperty.GetValue(eyes)! == 1 && yawWrites == syncTurns + 1,
            "Native renderer ignored the server's closed-eye phase.");
        cell.Value = new Seraph.Network.ServerSnapshot {
            SymptomVisuals = true, DrowsyMovement = true, SleepPressure = 1,
            Functions = new() { Movement = true }, SessionEpoch = "sync-eyes", LifeGeneration = 4,
            SleepEyes = new() { Closing = true, Phase = .72, Hold = .7, Sequence = 100 }
        };
        Frame(); Require(yawWrites == syncTurns + 1, "Snapshot correction turned the view twice in one closure.");
        cell.Value = new Seraph.Network.ServerSnapshot {
            SymptomVisuals = true, SleepPressure = 0, SessionEpoch = "sync-eyes", LifeGeneration = 5,
            SleepEyes = new()
        };
        Frame(); Require((double)closureProperty.GetValue(eyes)! == 0, "New life retained closed-eye phase.");
        Console.WriteLine("PASS native renderer receives authoritative eye phase, deduplicates closure corrections and resets on new life");
        renderer.Dispose();
        Require((bool)gate.Invoke(null, null)! && (int)id.GetValue(uniforms)! == 0, "Disposal retained HUD/warp.");
        RegionChecks.Run(mod);
        Console.WriteLine("PASS real 1.22.7 visual/HUD hooks, native alcohol untouched, final composition hook, frozen pause, window persistence, config/stale/dispose cleanup");
    }
    private static void Require(bool condition, string message) { if (!condition) throw new Exception(message); }
    public sealed class Cell { public object? Value; }
}
