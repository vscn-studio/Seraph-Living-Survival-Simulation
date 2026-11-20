using System.Reflection;
using System.Runtime.InteropServices;
using System.Text.Json;
using Cairo;
using Seraph.Client;
using Seraph.Network;
using Vintagestory.API.Client;
using Vintagestory.API.Common;
using Vintagestory.API.Config;
using Vintagestory.API.MathTools;
using Vintagestory.Common;
using Path = System.IO.Path;

internal static class BodyHudChecks
{
    private const BindingFlags All = BindingFlags.Instance | BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic;
    private static void Require(bool condition, string message) { if (!condition) throw new Exception(message); }

    internal static void Run(string nativeGame)
    {
        NativeLibrary.SetDllImportResolver(typeof(Context).Assembly, (name, _, _) =>
            NativeLibrary.Load(Path.Combine(nativeGame, "Lib", name == "nanosvg" ? "nanosvg.dll" : "libcairo-2.dll")));
        NativeLibrary.SetDllImportResolver(Assembly.Load("SkiaSharp"), (name, _, _) =>
            name.Contains("SkiaSharp", StringComparison.OrdinalIgnoreCase)
                ? NativeLibrary.Load(Path.Combine(nativeGame, "Lib", "libSkiaSharp.dll")) : IntPtr.Zero);
        static object? Default(MethodInfo m) => m.ReturnType == typeof(void) ? null
            : m.ReturnType.IsValueType ? Activator.CreateInstance(m.ReturnType) : null;
        var width = 1920; var height = 1080; var grabbed = true; var uploads = 0; var focus = 0;
        var positionReads = 0; var positionWrites = 0;
        var mouseX = 0; var mouseY = 0;
        var loaded = new List<GuiDialog>();
        var assetsRead = new HashSet<string>();
        var window = ElementBounds.Fixed(0, 0, width, height).WithEmptyParent(); window.CalcWorldBounds();
        ICoreClientAPI api = null!; IconUtil icons = null!;
        ImageSurface? panelImage = null;
        var logger = (ILogger)RoutingProxy.Make(typeof(ILogger), (m, _) => Default(m));
        var assets = (IAssetManager)RoutingProxy.Make(typeof(IAssetManager), (m, a) => {
            if (m.Name != "Get") return Default(m);
            var location = (AssetLocation)a![0]!; assetsRead.Add(location.Path);
            return new Asset(File.ReadAllBytes(Path.Combine("assets", location.Domain, location.Path)), location, null!);
        });
        var gui = (IGuiAPI)RoutingProxy.Make(typeof(IGuiAPI), (m, a) => {
            switch (m.Name)
            {
                case "CreateCompo":
                    var bounds = (ElementBounds)a![1]!; bounds.ParentBounds = window;
                    return (GuiComposer)Activator.CreateInstance(typeof(GuiComposer), All, null, [api, bounds, (string)a[0]!], null)!;
                case "get_LoadedGuis": case "get_OpenedGuis": return loaded;
                case "RegisterDialog": loaded.AddRange((GuiDialog[])a![0]!); return null;
                case "RequestFocus": focus++; return null;
                case "get_Icons": return icons;
                case "GetDialogPosition": positionReads++; return new Vec2i(999, 999);
                case "SetDialogPosition": positionWrites++; return null;
                case "LoadCairoTexture":
                    var bodySurface = (ImageSurface)a![0]!;
                    if (bodySurface.Width == (int)(BodyStatusHud.Width * RuntimeEnv.GUIScale)
                        && bodySurface.Height == (int)(BodyStatusHud.Height * RuntimeEnv.GUIScale))
                    {
                        panelImage?.Dispose(); panelImage = new(Format.Argb32, bodySurface.Width, bodySurface.Height);
                        using var ctx = new Context(panelImage); ctx.SetSourceSurface(bodySurface, 0, 0); ctx.Paint();
                    }
                    return ++uploads;
                case "LoadOrUpdateCairoTexture":
                    var surface = (ImageSurface)a![0]!; var texture = (LoadedTexture)a[2]!;
                    texture.TextureId = ++uploads; texture.Width = surface.Width; texture.Height = surface.Height;
                    return null;
                default: return Default(m);
            }
        });
        var scissors = new Stack<ElementBounds>();
        var render = (IRenderAPI)RoutingProxy.Make(typeof(IRenderAPI), (m, a) => m.Name switch {
            "get_FrameWidth" => width, "get_FrameHeight" => height, "get_ScissorStack" => scissors,
            "BitmapCreateFromPng" => new BitmapExternal((byte[])a![0]!, ((byte[])a[0]!).Length, logger),
            _ => Default(m)
        });
        var input = RoutingProxy.Make(typeof(IInputAPI), (m, _) => m.Name switch { "get_MouseGrabbed" => grabbed, "get_MouseX" => mouseX, "get_MouseY" => mouseY, _ => Default(m) });
        var events = RoutingProxy.Make(typeof(IClientEventAPI), (m, _) => m.Name == "RegisterGameTickListener" ? 1L : Default(m));
        api = (ICoreClientAPI)RoutingProxy.Make(typeof(ICoreClientAPI), (m, _) => m.Name switch {
            "get_Gui" => gui, "get_Render" => render, "get_Input" => input, "get_Event" => events,
            "get_Logger" => logger, "get_Assets" => assets, _ => Default(m)
        });
        icons = new IconUtil(api);
        var previousScale = RuntimeEnv.GUIScale; var previousLocale = Lang.CurrentLocale;
        var languages = new[] { "en", "zh-cn" }.ToDictionary(k => k, k => Lang.AvailableLanguages.GetValueOrDefault(k));
        var snapshot = new ServerSnapshot { BodyRegions = new[] { "head-neck", "torso", "right-arm", "left-arm", "right-leg", "left-leg" }
            .Select(key => new BodyRegionSnapshot { Region = key, Integrity = 15, Maximum = 15 }).ToArray() };
        var output = TestArtifacts.DirectoryPath("body-hud-render");
        try
        {
            foreach (var locale in languages.Keys)
            {
                var translation = new TranslationService(locale, logger, null!, EnumLinebreakBehavior.AfterWord);
                translation.PreLoad(Path.Combine(nativeGame, "assets"));
                foreach (var entry in JsonSerializer.Deserialize<Dictionary<string, string>>(File.ReadAllText($"assets/vsslssmod/lang/{locale}.json"))!)
                    translation.GetAllEntries()["vsslssmod:" + entry.Key] = entry.Value;
                Lang.AvailableLanguages[locale] = translation;
            }
            foreach (var locale in languages.Keys)
            foreach (var scale in new[] { 1f, 1.5f, 2f })
            {
                Lang.ChangeLanguage(locale); RuntimeEnv.GUIScale = scale;
                width = 1920; height = 1080;
                window.fixedWidth = width / scale; window.fixedHeight = height / scale; window.CalcWorldBounds();
                using var hud = new BodyStatusHud(api);
                foreach (var state in snapshot.BodyRegions) { state.Integrity = 15; state.Wound = state.Pain = false; }
                hud.Update(snapshot, false);
                Require(!hud.IsOpened(), "Healthy character diagram should stay hidden by default");
                hud.ToggleVisibility();
                Require(hud.IsOpened() && !hud.Focusable && !hud.DisableMouseGrab && !hud.PrefersUngrabbedMouse
                    && !hud.ShouldReceiveKeyboardEvents() && !hud.ShouldReceiveMouseEvents()
                    && !hud.CaptureAllInputs() && !hud.CaptureRawMouse(), "Body diagram captures gameplay input");
                var composer = hud.SingleComposer;
                var body = composer.GetCustomDraw("body").Bounds;
                var hover = (GuiElementHoverText)composer.GetElement("body-help");
                Require(composer.GetElement("title") is null && positionReads == 0 && positionWrites == 0, "Old window state survived");
                Require(Math.Abs(body.renderX - BodyStatusHud.LeftMargin * scale) < 1
                    && Math.Abs(body.renderY + body.InnerHeight / 2 - height / 2d) < 1, "Body not centered at left edge");
                void Point(double x, double y)
                {
                    mouseX = (int)Math.Round(body.renderX + x * body.InnerWidth / 256);
                    mouseY = (int)Math.Round(body.renderY + y * body.InnerHeight / 640);
                }
                var anchors = new[] { ("head-neck", 128d, 87d), ("torso", 127d, 237d), ("right-arm", 45d, 289d),
                    ("left-arm", 210d, 288d), ("right-leg", 89d, 464d), ("left-leg", 166d, 464d) };
                grabbed = false;
                foreach (var (region, x, y) in anchors)
                {
                    Point(x, y);
                    Require(BodyRenderer.HitTest(api, body, mouseX, mouseY) == region, "Wrong anatomical hover region: " + region);
                    hud.RefreshHover();
                    hover.RenderInteractiveElements(.016f);
                    Require(hover.IsVisible && hover.IsNowShown, "Native region tooltip did not render");
                    var text = (string)typeof(BodyStatusHud).GetField("hoverText", All)!.GetValue(hud)!;
                    Require(text.Contains(Lang.Get("vsslssmod:body-region-" + region)) && text.Contains("15 / 15")
                        && !text.Contains("vsslssmod:"), "Missing localized region information");
                }
                foreach (var (x, y) in new[] { (0d, 0d), (128d, 600d), (-20d, 200d), (250d, 300d) })
                {
                    Point(x, y); hud.RefreshHover();
                    hover.RenderInteractiveElements(.016f);
                    Require(!hover.IsVisible && !hover.IsNowShown, "Transparent gap or outside diagram triggers tooltip");
                }
                Point(127, 237); grabbed = true; hud.RefreshHover();
                hover.RenderInteractiveElements(.016f);
                Require(!hover.IsVisible && !hover.IsNowShown, "Captured gameplay cursor triggers tooltip");
                grabbed = false; hud.RefreshHover();
                var stableUploads = uploads;
                for (var i = 0; i < 120; i++) { hud.Update(snapshot, false); hud.RefreshHover(); }
                Require(uploads == stableUploads, "Stable diagram or tooltip uploads every frame");
                Require(panelImage is not null, "Body surface was not captured");
                panelImage!.Flush();
                Require(panelImage.Data[3] == 0, "Background is not transparent");
                panelImage.WriteToPng($"{output}/{locale}-{scale:0.0}-healthy.png");
                var torso = snapshot.BodyRegions[1]; torso.Integrity = 13; torso.Wound = true; torso.Pain = true;
                hud.Update(snapshot, false); hud.RefreshHover();
                var mildPixels = panelImage.Data.ToArray();
                torso.Integrity = 12.5;
                stableUploads = uploads; hud.Update(snapshot, false);
                Require(uploads == stableUploads, "Same color stage reuploads body numbers");
                hud.RefreshHover();
                var latest = (string)typeof(BodyStatusHud).GetField("hoverText", All)!.GetValue(hud)!;
                Require(latest.Contains(torso.Integrity.ToString("0.##"))
                    && latest.Contains(Lang.Get("vsslssmod:body-hover-wounded"))
                    && latest.Contains(Lang.Get("vsslssmod:body-hover-pain")), "Stationary tooltip did not update exact details");
                using (var compare = new ImageSurface(Format.Argb32, panelImage.Width, panelImage.Height))
                {
                    using (var ctx = new Context(compare)) BodyRenderer.Draw(api, ctx, body, snapshot, true);
                    compare.Flush(); Require(compare.Data.SequenceEqual(mildPixels), "Exact values still drawn on body");
                }
                var values = new[] { 15d, 13, 10, 6, 2, 0 };
                for (var i = 0; i < values.Length; i++) snapshot.BodyRegions[i].Integrity = values[i];
                hud.Update(snapshot, false);
                panelImage.WriteToPng($"{output}/{locale}-{scale:0.0}-stages.png");
                hud.Update(snapshot, true); hud.RefreshHover();
                Require(((string)typeof(BodyStatusHud).GetField("hoverText", All)!.GetValue(hud)!).Contains(Lang.Get("vsslssmod:body-stage-unknown")),
                    "Stale tooltip still shows measurements");
                hud.ToggleVisibility(); Require(!hud.IsOpened() && !hover.IsVisible, "Hide left tooltip visible");
                hud.ToggleVisibility(); Require(hud.IsOpened(), "Diagram cannot be restored");
                hud.Update(null, true); Require(!hud.IsOpened(), "Missing snapshot did not close diagram");
                hud.Update(snapshot, false);
                width = 1280; height = 720;
                window.fixedWidth = width / scale; window.fixedHeight = height / scale; window.MarkDirtyRecursive(); window.CalcWorldBounds();
                composer.Bounds.MarkDirtyRecursive(); composer.Bounds.CalcWorldBounds();
                Require(Math.Abs(body.renderY + body.InnerHeight / 2 - height / 2d) < 1, "Resize lost vertical center");
                Point(127, 237); hud.RefreshHover(); Require(hover.IsVisible, "Resized hitbox detached from body");
                using var automaticHud = new BodyStatusHud(api);
                foreach (var state in snapshot.BodyRegions) { state.Integrity = 15; state.Wound = state.Pain = false; }
                automaticHud.Update(snapshot, false);
                Require(!automaticHud.IsOpened(), "Healthy diagram appeared without an explicit request");
                snapshot.BodyRegions[1].Integrity = 14;
                automaticHud.Update(snapshot, false);
                Require(automaticHud.IsOpened(), "Injury did not show the body diagram automatically");
                snapshot.BodyRegions[1].Integrity = 15;
                automaticHud.Update(snapshot, false);
                Require(!automaticHud.IsOpened(), "Healed body diagram stayed visible");
                using var survivor = new SurvivalHud(api);
                var down = new MouseEvent(10, 10, EnumMouseButton.Left, 0);
                survivor.OnMouseDown(down);
                var up = new MouseEvent(10, 10, EnumMouseButton.Left, 0);
                survivor.OnMouseUp(up);
                Require(!down.Handled && !up.Handled,
                    "Informational survival HUD consumed inventory click input");
                survivor.TryOpen(false); survivor.ToggleVisibility();
                Require(survivor.IsOpened(), "Diagram toggle closes other HUD elements");
            }
            foreach (var (value, stage) in new[] { (100d, BodyStage.Healthy), (99d, BodyStage.Mild), (75d, BodyStage.Moderate),
                (50d, BodyStage.Severe), (25d, BodyStage.Critical), (0d, BodyStage.Depleted) })
                Require(BodyRenderer.Stage(new() { Integrity = value, Maximum = 100 }) == stage, "Color threshold ignored actual maximum");
            Require(BodyRenderer.Stage(new() { Integrity = 15, Maximum = 15, Pain = true }) == BodyStage.Mild, "Pain painted healthy");
            Require(BodyRenderer.Stage(new() { Integrity = double.NaN }) == BodyStage.Unknown
                && BodyRenderer.Stage(new() { Integrity = 0, Maximum = 0 }) == BodyStage.Unknown, "Invalid data painted depleted");
            Require(focus == 0, "HUD requested keyboard focus");
            Require(!assetsRead.Any(p => p.Contains("heart") || p.Contains("lung")), "Removed organ overlays still load");
        }
        finally
        {
            panelImage?.Dispose(); BodyRenderer.Release(api); RuntimeEnv.GUIScale = previousScale;
            foreach (var (key, value) in languages) if (value is null) Lang.AvailableLanguages.Remove(key); else Lang.AvailableLanguages[key] = value;
            Lang.ChangeLanguage(previousLocale);
        }
        Console.WriteLine("PASS compact body HUD: transparent/numeral-free Cairo rendering, fixed left-center placement, six alpha-mask hit regions, staged colors, native localized tooltips, stationary refresh/stale/hide/resize, passive input and texture reuse; 2 locales x 3 scales (mock GPU)");
    }
}
