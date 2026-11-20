using System.Collections.Concurrent;
using System.Reflection;
using System.Text.Json;
using Cairo;
using Seraph.Core.Framework;
using Seraph.Core.State;
using Seraph.Network;
using Vintagestory.API.Client;
using Vintagestory.API.Config;

internal static class ProgressRedesignChecks
{
    private const BindingFlags All = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static;
    internal static void Run(Assembly mod, string? nativeGame)
    {
        var type = mod.GetType("Seraph.Client.ActionProgressPresentation", true)!;
        var view = Activator.CreateInstance(type, true)!;
        void Update(ServerSnapshot? state, PressureStatus? pressure = null, double dt = .25, bool fresh = true, bool paused = false)
            => type.GetMethod("Update", All)!.Invoke(view, [state, pressure, dt, fresh, paused]);
        T Get<T>(string name) => (T)type.GetProperty(name, All)!.GetValue(view)!;
        var state = new ServerSnapshot { SessionEpoch = "a", Sequence = 1,
            ActionProgress = new() { Action = "drink", ElapsedSeconds = 1, DurationSeconds = 2 } };
        Update(state); Near(Get<double>("Fraction"), .5);
        Update(state); Near(Get<double>("Fraction"), .625); // replay must not rewind
        Update(state, paused: true); Near(Get<double>("Fraction"), .625);
        for (var i = 0; i < 30; i++) Update(state);
        Near(Get<double>("Fraction"), .98);
        state.ActionProgress = null; Update(state); Require(!Get<bool>("Visible"), "Cancel retained ring.");
        state.ActionProgress = new() { Action = "clean", DurationSeconds = 10 };
        Update(state); Near(Get<double>("Fraction"), 0);
        Update(state, fresh: false); Require(!Get<bool>("Visible"), "Stale ring retained.");
        Update(null); Require(!Get<bool>("Visible"), "Disconnected ring retained.");
        state.SessionEpoch = "b"; state.Sequence = 1; Update(state); Near(Get<double>("Fraction"), 0);
        state.ActionProgress = null;
        var pressure = new PressureStatus { Active = true, ActionId = 3, Sequence = 1, ElapsedSeconds = 120 };
        Update(state, pressure); Require(Get<bool>("Activity") && Get<bool>("Visible"), "Pressure activity missing.");
        Near(Get<double>("Fraction"), 0); Near(Get<double>("Seconds"), 120);
        Update(state, pressure); Near(Get<double>("Seconds"), 120.25);
        pressure.Active = false; Update(state, pressure); Require(!Get<bool>("Visible"), "Pressure cancel retained ring.");
        state.ActionProgress = new() { Action = "pressure", DurationSeconds = 1 }; Update(state);
        Require(!Get<bool>("Visible"), "Pressure fabricated finite completion.");
        state.ActionProgress = new() { Action = "drink", DurationSeconds = double.NaN }; Update(state);
        Require(!Get<bool>("Visible"), "Malformed timer shown.");
        var visuals = mod.GetType("Seraph.Client.LoadVisuals", true)!;
        var evaluate = visuals.GetMethod("Evaluate", All)!;
        double Visual(double fatigue, double sleep, string key) => (double)visuals.GetProperty(key)!.GetValue(evaluate.Invoke(null, [fatigue, sleep]))!;
        foreach (var (f, edge) in new[] { (.6, 0d), (.8, .04), (.9, .07), (1d, .10) })
        { Near(Visual(f, 0, "Edge"), edge); Near(Visual(f, 0, "Blur"), 0); }
        foreach (var (p, edge, center, blur) in new[] { (.7, 0d, 0d, 0d), (.8, .08, 0d, .06), (.9, .16, .01, .18), (.95, .24, .02, .32), (1d, .32, .04, .5) })
        { Near(Visual(0, p, "Edge"), edge); Near(Visual(0, p, "Center"), center); Near(Visual(0, p, "Blur"), blur); }
        Near(Visual(1, .95, "Edge"), .24); // strongest, never sum
        Near(Visual(0, .75, "Lid"), 0); Near(Visual(0, 1, "Lid"), .32);
        Near(Visual(0, .85, "Ghost"), 0); Near(Visual(0, 1, "Ghost"), .09);
        object current = Activator.CreateInstance(visuals)!; var maximum = evaluate.Invoke(null, [1d, 1d])!;
        for (var i = 0; i < 8; i++) current = visuals.GetMethod("Approach", All)!.Invoke(current, [maximum, .1])!;
        Near((double)visuals.GetProperty("Blur")!.GetValue(current)!, .5);
        var clear = evaluate.Invoke(null, [0d, .5])!;
        for (var i = 0; i < 20; i++) current = visuals.GetMethod("Approach", All)!.Invoke(current, [clear, .1])!;
        Near((double)visuals.GetProperty("Blur")!.GetValue(current)!, 0);
        CheckPrediction(mod);
        Console.WriteLine("PASS staged visual anchors/fades; action authority, replay, epoch, cancel, stale, pause, 98% cap and indefinite pressure");
        if (nativeGame is not null && OperatingSystem.IsWindows()) NativeBars(mod, nativeGame);
    }
    private static void CheckPrediction(Assembly mod)
    {
        var project = mod.GetType("Seraph.Client.SeraphClientBridge", true)!.GetMethod("ProjectFunctions", All)!;
        var body = BodyState.Create(); var profile = BodyProfile.Default;
        foreach (var fatigue in new[] { 0d, .8, 1 }) foreach (var pressure in new[] { 0d, .85, 1 })
        {
            var f = BodyFunctionResolver.Evaluate(new() { Fatigue = fatigue }, body with { SleepPressure = pressure }, profile);
            var snapshot = new FunctionSnapshot { Movement = true, Manipulation = true, Mining = f.Mining, Bow = f.Bow,
                Sprint = f.Sprint, Jump = f.Jump, LaborLoad = f.LaborLoad, SprintLoad = f.SprintLoad,
                CanUseActiveHand = f.CanUseActiveHand, CanDrawBow = f.CanDrawBow, CanSprint = f.CanSprint, CanJump = f.CanJump };
            foreach (var stamina in new[] { .35, .20, .125, .05 })
            {
                var expected = BodyFunctionResolver.Evaluate(new() { Fatigue = fatigue, Stamina = stamina }, body with { SleepPressure = pressure }, profile);
                var labor = Exertion.LoadLimit(stamina, 0, fatigue, pressure).Value;
                var sprint = Exertion.LoadLimit(stamina, 0, fatigue, pressure, true).Value;
                var actual = (FunctionSnapshot)project.Invoke(null, [snapshot, labor, sprint, false, ""])!;
                Near(actual.Mining, expected.Mining); Near(actual.Bow, expected.Bow); Near(actual.Sprint, expected.Sprint); Near(actual.Jump, expected.Jump);
                Require(actual.CanJump, "Ordinary load prevented predicted takeoff.");
                var exhausted = (FunctionSnapshot)project.Invoke(null, [snapshot, labor, sprint, true, "stamina"])!;
                Require(exhausted.CanJump && exhausted.Jump == actual.Jump, "Exhaustion alone blocked takeoff before applying its cooldown.");
            }
        }
        Console.WriteLine("PASS prediction/authoritative body functions agree across 36 overlapping load states");
    }
    private static void NativeBars(Assembly mod, string game)
    {
        var pending = new ConcurrentQueue<Action>();
        var images = new Dictionary<int, ImageSurface>(); var uploads = 0; var deletes = 0; var nextId = 0;
        ICoreClientAPI api = null!;
        object? Default(MethodInfo m) => m.ReturnType == typeof(void) ? null : m.ReturnType.IsValueType ? Activator.CreateInstance(m.ReturnType) : null;
        var gui = (IGuiAPI)RoutingProxy.Make(typeof(IGuiAPI), (m, args) => {
            if (m.Name == "CreateCompo")
            {
                var bounds = ((ElementBounds)args![1]!).WithEmptyParent(); bounds.ParentBounds.CalcWorldBounds();
                return Activator.CreateInstance(typeof(GuiComposer), All, null, [api, bounds, (string)args[0]!], null);
            }
            if (m.Name == "LoadOrUpdateCairoTexture")
            {
                var surface = (ImageSurface)args![0]!; var texture = (LoadedTexture)args[2]!;
                Require(!texture.Disposed, "Native upload used disposed texture.");
                if (texture.TextureId == 0) texture.TextureId = ++nextId;
                texture.Width = surface.Width; texture.Height = surface.Height; uploads++;
                if (images.Remove(texture.TextureId, out var old)) old.Dispose();
                var copy = new ImageSurface(Format.Argb32, surface.Width, surface.Height);
                using var ctx = new Context(copy); ctx.SetSourceSurface(surface, 0, 0); ctx.Paint(); images[texture.TextureId] = copy;
            }
            if (m.Name == "DeleteTexture") deletes++;
            return Default(m);
        });
        ImageSurface? preview = null;
        var overlays = new List<(double X, double Width, Vintagestory.API.MathTools.Vec4f Tint)>();
        var render = (IRenderAPI)RoutingProxy.Make(typeof(IRenderAPI), (m, args) => {
            if (m.Name == "RenderTexture" && args is { Length: 7 } && args[6] is Vintagestory.API.MathTools.Vec4f tint
                && images.TryGetValue((int)args[0]!, out var pixel) && pixel.Width == 24 && pixel.Height == 24)
                overlays.Add((Convert.ToDouble(args[1]), Convert.ToDouble(args[3]), tint));
            if (m.Name is "RenderTexture" or "Render2DTexture" or "Render2DTexturePremultipliedAlpha"
                && preview is not null && images.TryGetValue((int)args![0]!, out var img))
            {
                using var ctx = new Context(preview);
                ctx.SetSourceSurface(img, (int)Convert.ToDouble(args[1]), (int)Convert.ToDouble(args[2])); ctx.Paint();
            }
            return Default(m);
        });
        var events = (IClientEventAPI)RoutingProxy.Make(typeof(IClientEventAPI), (m, args) => {
            if (m.Name == "EnqueueMainThreadTask") pending.Enqueue((Action)args![0]!);
            return Default(m);
        });
        var world = (IClientWorldAccessor)RoutingProxy.Make(typeof(IClientWorldAccessor), (m, _) => m.Name == "get_Rand" ? new Random(1) : Default(m));
        var input = (IInputAPI)RoutingProxy.Make(typeof(IInputAPI), (m, _) => Default(m));
        api = (ICoreClientAPI)RoutingProxy.Make(typeof(ICoreClientAPI), (m, _) => m.Name switch {
            "get_Gui" => gui, "get_Render" => render, "get_Event" => events, "get_World" => world, "get_Input" => input, _ => Default(m)
        });
        void Drain(int expected)
        {
            if (!SpinWait.SpinUntil(() => pending.Count >= expected, 5000)) throw new Exception("Native statbar background Cairo timed out.");
            while (pending.TryDequeue(out var action)) action();
        }
        var type = mod.GetType("Seraph.Client.NativeProgressDisplay", true)!;
        var create = mod.GetType("Seraph.Client.ClosedEyeRestDisplay", true)!.GetMethod("Create", All)!;
        var oldScale = RuntimeEnv.GUIScale;
        var output = TestArtifacts.DirectoryPath("progress-redesign");
        try
        {
            foreach (var locale in new[] { "en", "zh-cn" })
            foreach (var (w,h,scale) in new[] { (1280,720,1f), (1280,720,1.5f), (1920,1080,1.5f), (2560,1440,2f) })
            {
                RuntimeEnv.GUIScale = scale;
                using var display = (IDisposable)create.Invoke(null, [api])!;
                var lang = JsonSerializer.Deserialize<Dictionary<string,string>>(File.ReadAllText($"assets/vsslssmod/lang/{locale}.json"))!;
                var labels = new[] { "stamina", "fatigue", "sleeppressure" }.Select(k => lang["metric-" + k]).ToArray();
                double?[] values = [72.5, 28.3, 63.1]; string[] text = ["72.5%", "28.3%", "63.1%"];
                void Update(double t) => type.GetMethod("Update", All)!.Invoke(display, [values, labels, text, (double)scale, (w - 340d * scale) / 2, (h - 168d * scale) / 2, t]);
                Update(0); Drain(6);
                var composer = (GuiComposer)type.GetProperty("Composer", All)!.GetValue(display)!;
                for (var row = 0; row < 3; row++)
                {
                    Require(composer.GetStatbar("bar" + row).GetType() == typeof(GuiElementStatbar), "Homemade rest bar survived.");
                    Near(composer.GetStatbar("bar" + row).GetValue(), values[row]!.Value, 1e-4);
                }
                if (locale == "en" && w == 1280 && scale == 1f)
                {
                    var trendType = mod.GetType("Seraph.Client.MetricTrendDisplay", true)!;
                    using var trends = (IDisposable)Activator.CreateInstance(trendType, All, null, [api], null)!;
                    var history = (Seraph.Application.MetricTrends)trendType.GetProperty("History", All)!.GetValue(trends)!;
                    var bar = composer.GetStatbar("bar0");
                    void Bind() => trendType.GetMethod("Bind", All)!.Invoke(trends, [bar, "stamina"]);
                    void Draw() => bar.RenderInteractiveElements(0);
                    history.Record("stamina", 70, 0, 100); history.Record("stamina", 40, 0, 100); Bind();
                    var beforeOverlay = uploads; overlays.Clear(); Draw();
                    Require(uploads == beforeOverlay + 1 && overlays.Count == 1, "Trend did not create exactly one cached marker texture");
                    Near(overlays[0].Width, 10 * scale);
                    Near(overlays[0].X, bar.Bounds.renderX + bar.Bounds.InnerWidth + 4 * scale);
                    Near(overlays[0].Tint.R, 1); Near(overlays[0].Tint.G, 1); Near(overlays[0].Tint.B, 1);
                    history.Clear(); history.Record("stamina", 40, 0, 100); history.Record("stamina", 70, 0, 100);
                    overlays.Clear(); Draw(); Near(overlays[0].Tint.R, 1);
                    history.Clear(); overlays.Clear(); Draw();
                    Require(overlays.Count == 1, "Stable/reset trend must show a horizontal line");
                    for (var frame = 0; frame < 7200; frame++) Draw();
                    Require(uploads == beforeOverlay + 3, "Direction markers uploaded Cairo textures every frame");
                    var symbol = trendType.GetMethod("Symbol", All)!;
                    Require((string)symbol.Invoke(null, [1])! == "▲" && (string)symbol.Invoke(null, [-1])! == "▼"
                        && (string)symbol.Invoke(null, [0])! == "—", "Direction symbols changed");
                    Console.WriteLine("PASS neutral up/down/line markers right of native bars, lifecycle and 7200 frames with three cached textures (mock GPU)");
                }
                preview = new ImageSurface(Format.Argb32, w, h);
                // Full closed-eye black background: these are real native Cairo layers with mock GPU, not a game screenshot.
                using (var ctx = new Context(preview)) { ctx.SetSourceRGB(0,0,0); ctx.Paint(); }
                type.GetMethod("Render", All)!.Invoke(display, [0f]);
                preview.WriteToPng($"{output}/rest-{locale}-{w}x{h}-{scale:0.0}.png"); preview.Dispose(); preview = null;
                var before = uploads;
                for (var frame = 0; frame < 7200; frame++) Update(frame / 60d);
                Require(uploads == before && (int)type.GetProperty("Compositions", All)!.GetValue(display)! == 1
                    && (int)type.GetProperty("ValueUpdates", All)!.GetValue(display)! == 3, "Unchanged 120-second rest rebuilt/uploaded bars.");
                values[0] = 0; text[0] = "0.0%"; Update(121); Drain(2);
                Require((int)type.GetProperty("ValueUpdates", All)!.GetValue(display)! == 4, "Exhaustion rebuilt unchanged bars.");
                display.Dispose(); Update(122); Drain(6); // disposed composer can reopen safely
                values[0] = 40; text[0] = "40.0%"; Update(123);
                var pendingUploads = uploads;
                display.Dispose(); Drain(2); // cancel before the native worker's GPU callbacks arrive
                Require(uploads == pendingUploads, "Late native callback revived a disposed texture.");
            }

        }
        finally { RuntimeEnv.GUIScale = oldScale; preview?.Dispose(); foreach (var img in images.Values) img.Dispose(); }
        File.WriteAllText(System.IO.Path.Combine(output, "native-resource-counts.txt"), $"8 rest locale/size/scale cases; each 7200 unchanged frames: 1 composition, 3 initial bar updates, 0 further uploads. Exhaustion changes exactly 1 bar. Total mock-GPU uploads {uploads}, deletes {deletes}. Real gameplay FPS/hangs not measured.\n");
        Console.WriteLine("PASS actual GuiElementStatbar: 8 rest locale/resolution/scale exports, 120-second unchanged resource counts, exhaustion delta and reopen (native Cairo, mock GPU)");
    }
    private static void Near(double actual, double expected, double epsilon = 1e-9) => Require(Math.Abs(actual-expected) < epsilon, $"Expected {expected}, got {actual}.");
    private static void Require(bool value, string message) { if (!value) throw new Exception(message); }
}
