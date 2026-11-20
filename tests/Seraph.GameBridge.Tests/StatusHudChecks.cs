using System.Reflection;
using System.Text.Json;
using Cairo;
using Seraph.Client;
using Seraph.Network;
using Vintagestory.API.Client;
using Vintagestory.API.Common;
using Vintagestory.API.Common.Entities;
using Vintagestory.API.Config;
using Vintagestory.API.MathTools;
using Vintagestory.Client.NoObf;

internal static class StatusHudChecks
{
    private const BindingFlags All = BindingFlags.Instance | BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic;
    private static void Require(bool condition, string message) { if (!condition) throw new Exception(message); }
    internal static void Run(string nativeGame)
    {
        System.Runtime.InteropServices.NativeLibrary.SetDllImportResolver(typeof(Context).Assembly, (name, _, _) =>
            System.Runtime.InteropServices.NativeLibrary.Load(System.IO.Path.Combine(nativeGame, "Lib", name == "nanosvg" ? "nanosvg.dll" : "libcairo-2.dll")));
        string[] keys = ["blood", "sleep", "defecation"];
        StatusIconSnapshot[] values = [new() { Key = "blood", Available = true, Active = true },
            new() { Key = "sleep", Available = true }, new() { Key = "defecation", Active = true }];
        Require(StatusIconLayout.ActiveMask(keys, values, false) == 1, "Normal/unavailable icons remain visible");
        Require(StatusIconLayout.ActiveMask(keys, values, true) == 0, "Stale icons remain visible");
        var obstacles = new List<StatusIconLayout.Obstacle> { new(1650, 20, 256, 256) };
        var layout = StatusIconLayout.Place(3, 1920, 1080, obstacles);
        Require(layout.Visible && layout.Top == 286 && layout.Columns == 2, "Minimap not avoided");
        obstacles.Add(new(1650, 285, 256, 50));
        Require(StatusIconLayout.Place(3, 1920, 1080, obstacles).Top == 345, "Stacked HUD not avoided");
        obstacles.Reverse();
        Require(StatusIconLayout.Place(3, 1920, 1080, obstacles).Top == 345, "Obstacle order changes placement");
        obstacles.Clear(); obstacles.Add(new(10, 20, 250, 250));
        Require(StatusIconLayout.Place(3, 1920, 1080, obstacles).Top == 80, "Left minimap moved right icons");
        obstacles.Clear(); obstacles.Add(new(690, 10, 256, 256));
        layout = StatusIconLayout.Place(16, 960, 540, obstacles);
        Require(layout.Visible && layout.Columns == 4 && layout.Top + layout.Height <= 526, "Large GUI scale clips active icons");
        obstacles.Clear(); obstacles.Add(new(0, 0, 960, 540));
        Require(!StatusIconLayout.Place(16, 960, 540, obstacles).Visible, "Full overlay leaves overlapping icons");
        var positive = false; var negative = false;
        for (var t = 0d; t < 5; t += .01)
        { var offset = StatusIconLayout.Shake(t); Require(Math.Abs(offset) <= 2, "Shake exceeds small amplitude"); positive |= offset > 0; negative |= offset < 0; }
        Require(positive && negative && StatusIconLayout.Shake(1) == 0 && StatusIconLayout.Shake(4) == 0, "Shake lacks bidirectional motion or rest");
        NativeChecks();
        Console.WriteLine("PASS status HUD: active-only compact slots; map/stacked HUD avoidance; scale/overflow; small intermittent shake; native texture reuse and aligned hitboxes");
    }

    private static void NativeChecks()
    {
        static object? Default(MethodInfo method) => method.ReturnType == typeof(void) ? null : method.ReturnType.IsValueType ? Activator.CreateInstance(method.ReturnType) : null;
        var scale = RuntimeEnv.GUIScale; var locale = Lang.CurrentLocale;
        var savedEnglish = Lang.AvailableLanguages.GetValueOrDefault("en");
        var translation = new TranslationService("en", null!, null!, EnumLinebreakBehavior.AfterWord);
        var entries = JsonSerializer.Deserialize<Dictionary<string, string>>(File.ReadAllText("assets/vsslssmod/lang/en.json"))!;
        typeof(TranslationService).GetField("entryCache", All)!.SetValue(translation, entries.ToDictionary(p => "vsslssmod:" + p.Key, p => p.Value));
        typeof(TranslationService).GetField("loaded", All)!.SetValue(translation, true);
        Lang.AvailableLanguages["en"] = translation; Lang.ChangeLanguage("en"); RuntimeEnv.GUIScale = 1;
        var uploads = 0; var compositions = 0; var mouseX = 0; var mouseY = 0; var paused = false; var fresh = true; var darkStage = 0;
        var dialogs = new List<GuiDialog>();
        var window = ElementBounds.Fixed(0, 0, 1920, 1080).WithEmptyParent(); window.CalcWorldBounds();
        ICoreClientAPI api = null!;
        GuiComposer Composer(string name, ElementBounds bounds)
        { bounds.ParentBounds = window; return (GuiComposer)Activator.CreateInstance(typeof(GuiComposer), All, null, [api, bounds, name], null)!; }
        var gui = (IGuiAPI)RoutingProxy.Make(typeof(IGuiAPI), (m, a) =>
        {
            // Native GuiAPI includes every composer in this area, even disabled ones.
            if (m.Name == "GetDialogBoundsInArea") return dialogs.SelectMany(d => d.Composers.Values)
                .Where(c => c.Bounds.Alignment == (EnumDialogArea)a![0]!).Select(c => c.Bounds).ToList();
            if (m.Name == "CreateCompo") { compositions++; return Composer((string)a![0]!, (ElementBounds)a[1]!); }
            if (m.Name == "LoadOrUpdateCairoTexture")
            { var texture = (LoadedTexture)a![2]!; var surface = (ImageSurface)a[0]!; texture.TextureId = ++uploads; texture.Width = surface.Width; texture.Height = surface.Height; return null; }
            return m.Name switch { "get_OpenedGuis" => dialogs, "get_WindowBounds" => window, "LoadCairoTexture" => ++uploads, _ => Default(m) };
        });
        var input = RoutingProxy.Make(typeof(IInputAPI), (m, _) => m.Name switch { "get_MouseX" => mouseX, "get_MouseY" => mouseY, _ => Default(m) });
        ElementBounds? sparkleBounds = null; var sparkleDraws = 0;
        var render = RoutingProxy.Make(typeof(IRenderAPI), (m, a) => {
            if (m.Name == "Render2DTexturePremultipliedAlpha" && sparkleBounds is { } b)
            {
                var x = Convert.ToDouble(a![1]); var y = Convert.ToDouble(a[2]);
                var w = Convert.ToDouble(a[3]); var h = Convert.ToDouble(a[4]); var tint = (Vec4f)a[6]!;
                Require(x >= b.renderX && y >= b.renderY && x + w <= b.renderX + b.InnerWidth
                    && y + h <= b.renderY + b.InnerHeight, "Glint spills into map or adjacent icon.");
                Require(tint.R == tint.G && tint.G == tint.B && tint.B == tint.A && tint.A is > 0 and <= .8f,
                    "Glint is not a bounded premultiplied white fade.");
                sparkleDraws++;
            }
            return m.Name switch { "get_FrameWidth" => 1920, "get_FrameHeight" => 1080, _ => Default(m) };
        });
        var events = RoutingProxy.Make(typeof(IClientEventAPI), (m, _) => Default(m));
        var logger = RoutingProxy.Make(typeof(ILogger), (m, _) => Default(m));
        var player = FixtureClientPlayer.Make(new EntityPlayer());
        var world = RoutingProxy.Make(typeof(IClientWorldAccessor), (m, _) => m.Name switch {
            "get_Player" => player, "get_DefaultSpawnPosition" => new EntityPos(), _ => Default(m) });
        api = (ICoreClientAPI)RoutingProxy.Make(typeof(ICoreClientAPI), (m, _) => m.Name switch {
            "get_World" => world, "get_Gui" => gui, "get_Input" => input, "get_Render" => render, "get_Event" => events, "get_Logger" => logger, "get_IsGamePaused" => paused, _ => Default(m) });
        using var hud = new SurvivalHud(api, () => fresh, () => darkStage);
        var art = (Dictionary<string, ImageSurface>)typeof(SurvivalHud).GetField("statusArt", All)!.GetValue(hud)!;
        foreach (var key in new[] { "blood", "sleep", "defecation", "bug", "heat-illness" }) art[key] = new ImageSurface(Format.Argb32, 48, 48);
        foreach (var key in new[] { "dark-adaptation-close", "dark-adaptation-open" })
            art[key] = new ImageSurface("assets/vsslssmod/textures/gui/status/hansjack/" + key + ".png");
        void Refresh(params StatusIconSnapshot[] icons)
        {
            typeof(SurvivalHud).GetField("current", All)!.SetValue(hud, new ServerSnapshot { StatusIcons = icons });
            typeof(SurvivalHud).GetMethod("RefreshStatusIcons", All)!.Invoke(hud, null);
        }
        try
        {
            typeof(SurvivalHud).GetMethod("ComposeStatusIcons", All)!.Invoke(hud, null);
            Require(!hud.SingleComposer.Enabled, "No conditions displayed empty HUD");
            var blood = new StatusIconSnapshot { Key = "blood", Available = true, Active = true };
            var sleep = new StatusIconSnapshot { Key = "sleep", Available = true, Active = true };
            Refresh(blood, sleep, new() { Key = "defecation", Available = true });
            var hover = typeof(SurvivalHud).GetMethod("IconHover", All)!;
            var bug = new StatusIconSnapshot { Key = "bug", Available = true, Active = true, Detail = "Attached for 3 hours" };
            var heat = new StatusIconSnapshot { Key = "heat-illness", Available = true, Active = true, Detail = "Duplicated clinical description" };
            Refresh(bug, heat);
            var bugText = (string)hover.Invoke(hud, ["bug"])!;
            var heatText = (string)hover.Invoke(hud, ["heat-illness"])!;
            Require(bugText.Contains("Attached for 3 hours") && !bugText.Contains("Active"), "Bug hover lost live detail or repeats active state");
            Require(!heatText.Contains("Duplicated clinical description") && heatText.Contains("cool down"), "Heat hover repeats clinical detail or loses advice");
            var longText = string.Concat(Enumerable.Repeat("过热需要休息补水", 20));
            var wrapped = TooltipText.Panel(longText, CairoFont.WhiteDetailText());
            Require(wrapped.Contains('\n') && wrapped.Replace("\n", "") == longText, "CJK hover wrapping loses text or fails to wrap");
            Refresh(blood, sleep, new() { Key = "defecation", Available = true });
            Require(hud.SingleComposer.GetElement("icon-blood") is not null && hud.SingleComposer.GetElement("icon-defecation") is null, "Native composer retains inactive slot");
            using (var coordinates = new Coordinates(api))
            using (var topMap = new Overlay(api))
            {
                coordinates.SingleComposer = Composer("coordinates-fixture", ElementBounds.Fixed(EnumDialogArea.RightTop, 0, 0, 190, 48))
                    .AddDynamicText("", CairoFont.WhiteSmallishText(), ElementBounds.Fixed(0, 0, 190, 48), "text").Compose();
                topMap.SingleComposer = Composer("top-map-fixture", ElementBounds.Fixed(EnumDialogArea.RightTop, 0, 0, 256, 256));
                topMap.SingleComposer.Bounds.CalcWorldBounds();
                var tick = typeof(HudElementCoordinates).GetMethod("Every250ms", All)!;
                foreach (var withMap in new[] { false, true })
                {
                    dialogs.Clear(); dialogs.Add(hud); dialogs.Add(coordinates);
                    if (withMap) dialogs.Add(topMap);
                    tick.Invoke(coordinates, [.25f]); hud.PositionStatusIcons(0);
                    var coordinateY = coordinates.SingleComposer.Bounds.renderY;
                    var statusY = hud.SingleComposer.Bounds.renderY;
                    for (var frame = 0; frame < 600; frame++)
                    {
                        if (frame % 15 == 0) tick.Invoke(coordinates, [.25f]);
                        hud.PositionStatusIcons(1f / 60);
                        Require(coordinates.SingleComposer.Bounds.renderY == coordinateY && hud.SingleComposer.Bounds.renderY == statusY,
                            "Native coordinate HUD and status icons chase each other vertically");
                    }
                    Refresh(); tick.Invoke(coordinates, [.25f]);
                    Require(coordinates.SingleComposer.Bounds.renderY == coordinateY, "Hidden status composer displaces native coordinates");
                    Refresh(blood, sleep);
                    Require(hud.SingleComposer.Bounds.renderY == statusY, "Restored status icons change vertical layout");
                }
                dialogs.Clear(); hud.PositionStatusIcons(0);
                Console.WriteLine("PASS native coordinate HUD: stable positions across 1200 frames, minimap and hidden/restored status icons");
            }
            var beforeUploads = uploads; var beforeCompositions = compositions;
            using var map = new Overlay(api);
            map.SingleComposer = Composer("minimap-fixture", ElementBounds.Fixed(1650, 20, 256, 256)); map.SingleComposer.Bounds.CalcWorldBounds();
            dialogs.Add(map);
            for (var i = 0; i < 120; i++) hud.PositionStatusIcons(1f / 60);
            Require(hud.SingleComposer.Bounds.absY >= 286, "Native bounds overlap minimap");
            Require(uploads == beforeUploads && compositions == beforeCompositions, "Animation or map avoidance recreated textures");
            var iconBounds = hud.SingleComposer.GetElement("icon-blood").Bounds;
            var hoverBounds = hud.SingleComposer.GetElement("icon-help-blood").Bounds;
            Require(iconBounds.absX == hoverBounds.absX && iconBounds.absY == hoverBounds.absY, "Moved tooltip hitbox detached from icon");
            mouseX = (int)iconBounds.absX + 20; mouseY = (int)iconBounds.absY + 20;
            hud.PositionStatusIcons(.1f); Require(hud.SingleComposer.Bounds.fixedOffsetX == -14, "Hovered icons still shake");
            mouseX = mouseY = 0; paused = true; hud.PositionStatusIcons(.1f);
            Require(hud.SingleComposer.Bounds.fixedOffsetX == -14, "Paused icons still shake"); paused = false;
            dialogs.Clear(); hud.PositionStatusIcons(0); Require(hud.SingleComposer.Bounds.absY == 80, "Closed map retained gap");
            for (var i = 0; i < 20; i++) Refresh(blood, sleep);
            Require(uploads == beforeUploads && compositions == beforeCompositions, "Identical snapshots recompose icons");
            Refresh(sleep); Require(hud.SingleComposer.GetElement("icon-blood") is null && hud.SingleComposer.GetElement("icon-sleep").Bounds.fixedX == 0, "Recovered condition left a blank slot");
            Refresh(); Require(!hud.SingleComposer.Enabled, "Recovered all conditions leaves icons");
            Refresh(sleep); fresh = false; hud.PositionStatusIcons(.1f); Require(!hud.SingleComposer.Enabled, "Disconnected snapshot still displays warnings");
            fresh = true; Refresh(); darkStage = 1; hud.PositionStatusIcons(.016f);
            Require(hud.SingleComposer.Enabled && hud.SingleComposer.GetElement("icon-darkadaptation") is not null,
                "Local gray eye did not enter status layout without a server condition.");
            using (var preview = new ImageSurface(Format.Argb32, 192, 96))
            {
                using var ctx = new Context(preview); ctx.SetSourceRGB(.04, .04, .04); ctx.Paint();
                for (var stage = 1; stage <= 2; stage++)
                {
                    darkStage = stage; hud.PositionStatusIcons(0);
                    ctx.Save(); ctx.Translate((stage - 1) * 96, 0);
                    var b = ElementBounds.Fixed(0, 0, 96, 96).WithEmptyParent(); b.CalcWorldBounds();
                    typeof(SurvivalHud).GetMethod("DrawStatusIcon", All)!.Invoke(hud, [ctx, b, "darkadaptation"]);
                    ctx.Restore();
                }
                var output = TestArtifacts.DirectoryPath("status-hud-render");
                preview.WriteToPng(System.IO.Path.Combine(output, "dark-adaptation-stages.png"));
            }
            beforeUploads = uploads; beforeCompositions = compositions;
            dialogs.Add(map);
            var sparkles = (DarkAdaptationSparkles)typeof(SurvivalHud).GetField("darkSparkles", All)!.GetValue(hud)!;
            var darkBounds = hud.SingleComposer.GetElement("icon-darkadaptation").Bounds;
            sparkleBounds = darkBounds;
            sparkles.Render(darkBounds, .016f, false); var sparkleUploads = uploads;
            for (var i = 0; i < 7200; i++)
            { hud.PositionStatusIcons(1f / 60); sparkles.Render(darkBounds, 1f / 60, false); }
            Require(uploads == sparkleUploads && sparkleUploads == beforeUploads + 1 && compositions == beforeCompositions,
                "Eye sparkles or map avoidance upload textures each frame.");
            Require(sparkleDraws > 7200, "White sparkle draw calls missing.");
            Require(hud.SingleComposer.Bounds.absY >= 286, "Adapted eye overlaps minimap.");
            var sparkleTime = typeof(DarkAdaptationSparkles).GetField("time", All)!;
            var beforeTime = (double)sparkleTime.GetValue(sparkles)!;
            sparkles.Render(darkBounds, 10f, true);
            Require((double)sparkleTime.GetValue(sparkles)! == beforeTime, "Paused glints advance time.");
            darkStage = 0; hud.PositionStatusIcons(.016f); Require(!hud.SingleComposer.Enabled, "Eye retained after adaptation faded.");
            darkStage = 2; hud.PositionStatusIcons(.016f);
            typeof(SurvivalHud).GetField("current", All)!.SetValue(hud, new ServerSnapshot { IsClosedEyeResting = true });
            hud.PositionStatusIcons(.016f); Require(!hud.SingleComposer.Enabled, "Closed eyes retained adaptation HUD.");
            Console.WriteLine("PASS local dark adaptation HUD: gray/open PNGs, thresholds without server status, minimap avoidance, 7200 sparkle frames with one upload, pause and closed-eye cleanup");
        }
        finally
        {
            hud.ReleaseStatusArt(); RuntimeEnv.GUIScale = scale;
            if (savedEnglish is not null) Lang.AvailableLanguages["en"] = savedEnglish;
            Lang.ChangeLanguage(locale);
        }
    }
    private sealed class Overlay(ICoreClientAPI api) : HudElement(api)
    { public override bool IsOpened() => true; }
    private sealed class Coordinates(ICoreClientAPI api) : HudElementCoordinates(api)
    { public override bool IsOpened() => true; }
}
