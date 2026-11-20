using System.Collections;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.Reflection;
using System.Runtime.CompilerServices;
using Cairo;
using Seraph.Client;
using Seraph.Network;
using Vintagestory.API.Client;
using Vintagestory.API.Common;
using Vintagestory.API.Config;

internal static class PanelRefreshChecks
{
    private const BindingFlags All = BindingFlags.Instance | BindingFlags.Static | BindingFlags.NonPublic | BindingFlags.Public;

    internal static void Run(Assembly mod)
    {
        // Background asset loading belongs to the game; use an isolated cached
        // pattern while exercising actual Cairo text, bars and composers.
        var patterns = (IDictionary)typeof(GuiElement).GetField("cachedPatterns", All)!.GetValue(null)!;
        var dirt = (AssetLocation)typeof(GuiElement).GetField("dirtTextureName", All)!.GetValue(null)!;
        var patternKey = dirt.Clone().WithPathPrefix(.125f + "-").WithPathPrefix("64@");
        var oldPattern = patterns[patternKey];
        using var background = new ImageSurface(Format.Argb32, 8, 8);
        using var pattern = new SurfacePattern(background) { Extend = Extend.Repeat };
        patterns[patternKey] = new KeyValuePair<SurfacePattern, ImageSurface>(pattern, background);
        var uploads = 0;
        var compositions = 0;
        var mouseX = -1000; var mouseY = -1000;
        var pending = new ConcurrentQueue<Action>();
        void Drain(int expected)
        {
            Require(SpinWait.SpinUntil(() => pending.Count >= expected, 5000), "Native Cairo callbacks timed out.");
            while (pending.TryDequeue(out var callback)) callback();
        }
        var scale = RuntimeEnv.GUIScale;
        object? Default(MethodInfo m) => m.ReturnType == typeof(void) ? null
            : m.ReturnType.IsValueType ? Activator.CreateInstance(m.ReturnType) : null;
        ICoreClientAPI api = null!;
        var loaded = new List<GuiDialog>();
        IconUtil icons = null!;
        GuiComposer Composer(string key, ElementBounds bounds)
        {
            if (bounds.ParentBounds is null) bounds.WithEmptyParent();
            bounds.ParentBounds!.CalcWorldBounds();
            return (GuiComposer)Activator.CreateInstance(typeof(GuiComposer), All, null, [api, bounds, key], null)!;
        }
        var gui = RoutingProxy.Make(typeof(IGuiAPI), (m, a) => {
            switch (m.Name)
            {
                case "CreateCompo": compositions++; return Composer((string)a![0]!, (ElementBounds)a[1]!);
                case "get_LoadedGuis": case "get_OpenedGuis": return loaded;
                case "RegisterDialog": loaded.AddRange((GuiDialog[])a![0]!); return null;
                case "get_Icons": return icons;
                case "LoadCairoTexture": return ++uploads;
                case "LoadOrUpdateCairoTexture":
                    var surface = (ImageSurface)a![0]!;
                    var texture = (LoadedTexture)a[2]!;
                    texture.TextureId = ++uploads; texture.Width = surface.Width; texture.Height = surface.Height;
                    return null;
                default: return Default(m);
            }
        });
        var render = RoutingProxy.Make(typeof(IRenderAPI), (m, _) => m.Name switch {
            "get_FrameWidth" => 1920, "get_FrameHeight" => 1080,
            "get_ScissorStack" => Activator.CreateInstance(m.ReturnType), _ => Default(m)
        });
        var input = RoutingProxy.Make(typeof(IInputAPI), (m, _) => m.Name switch {
            "get_MouseX" => mouseX, "get_MouseY" => mouseY, _ => Default(m)
        });
        var events = RoutingProxy.Make(typeof(IClientEventAPI), (m, a) => {
            if (m.Name == "EnqueueMainThreadTask") pending.Enqueue((Action)a![0]!);
            return m.Name == "RegisterGameTickListener" ? 1L : Default(m);
        });
        var logger = RoutingProxy.Make(typeof(ILogger), (m, _) => Default(m));
        api = (ICoreClientAPI)RoutingProxy.Make(typeof(ICoreClientAPI), (m, _) => m.Name switch {
            "get_Gui" => gui, "get_Render" => render, "get_Event" => events, "get_Logger" => logger,
            "get_Input" => input, _ => Default(m)
        });
        icons = new IconUtil(api);
        var bridge = (SeraphClientBridge)RuntimeHelpers.GetUninitializedObject(typeof(SeraphClientBridge));
        using var trends = (IDisposable)Activator.CreateInstance(mod.GetType("Seraph.Client.MetricTrendDisplay", true)!, All, null, [api], null)!;
        typeof(SeraphClientBridge).GetField("<Trends>k__BackingField", All)!.SetValue(bridge, trends);
        typeof(SeraphClientBridge).GetField("channel", All)!.SetValue(bridge,
            RoutingProxy.Make(typeof(IClientNetworkChannel), (m, _) => m.Name == "get_Connected" ? true : Default(m)));
        void Snapshot(ServerSnapshot snapshot)
        {
            typeof(SeraphClientBridge).GetProperty("Snapshot")!.SetValue(bridge, snapshot);
            typeof(SeraphClientBridge).GetProperty("LastReceivedUtc")!.SetValue(bridge, DateTime.UtcNow);
        }
        ServerSnapshot Data(int diseases = 1) => new() {
            GutMassG = 1000, GutMaximumG = 5000, StomachCompositionG = [500, 200, 100, 100, 50, 50],
            StatusMetrics = [new() { Key = "stamina", Available = true, Bar = true, Value = 50, Maximum = 100, Text = "50%" }],
            TemperatureMetrics = [new() { Key = "humidity", Available = true, Value = 66, Unit = "%" }],
            RecoveryMetrics = Enumerable.Range(0, diseases).Select(i => new PanelMetric {
                Key = "disease-" + i, Available = true, Text = "Disease " + i, Detail = "Clinical detail " + i
            }).ToArray()
        };
        Snapshot(Data());
        var type = mod.GetType("Seraph.Client.CharacterPanelIntegration", true)!;
        using var panel = (IDisposable)Activator.CreateInstance(type, [api, bridge])!;
        using var dialog = new CharacterFixture(api);
        dialog.Composers["playercharacter"] = Composer("playercharacter", ElementBounds.Fixed(0, 0, 300, 340).WithEmptyParent()).Compose(false);
        dialog.Composers["environment"] = Composer("environment", ElementBounds.Fixed(0, 0, 300, 100).WithEmptyParent())
            .AddDynamicText("", CairoFont.WhiteSmallText(), ElementBounds.Fixed(0, 0, 280, 20), "dyntext").Compose(false);
        type.GetField("dialog", All)!.SetValue(panel, dialog);
        type.GetField("registered", All)!.SetValue(panel, true);
        dialog.OnClosed += () => type.GetMethod("OnCharacterClosed", All)!.Invoke(panel, null);
        var compose = type.GetMethod("ComposeExtraPanels", All)!;
        var refresh = type.GetMethod("RefreshCurrent", All)!;
        try
        {
            RuntimeEnv.GUIScale = 1;
            dialog.TryOpen(false);
            var timer = Stopwatch.StartNew();
            compose.Invoke(panel, null);
            Drain(((IDictionary)type.GetField("barValues", All)!.GetValue(panel)!).Count * 2);
            var firstOpen = timer.Elapsed.TotalMilliseconds;
            Require((int)type.GetField("composedImmuneRows", All)!.GetValue(panel)! == 1, "Single disease allocated unused rows.");
            var immune = dialog.Composers["vsslssmod-character-diseaserecovery"]!;
            Require(immune.GetDynamicText("immune-disease-0") is not null && immune.GetDynamicText("immune-disease-1") is null,
                "Native composer contains invisible disease rows.");
            var before = uploads; var beforeCompositions = compositions;
            timer.Restart();
            for (var i = 0; i < 100; i++) refresh.Invoke(panel, null);
            var unchanged = timer.Elapsed.TotalMilliseconds / 100;
            Require(before == uploads && beforeCompositions == compositions, "Same snapshot rebuilt native textures/composers.");
            for (var i = 0; i < 30; i++) { Snapshot(Data()); refresh.Invoke(panel, null); }
            Require(before == uploads && beforeCompositions == compositions, "Identical new snapshots re-uploaded bars/gut/text.");

            var energy = dialog.Composers["vsslssmod-character-energybalance"]!;
            TooltipText.ClearPanelCache();
            for (var i = 0; i < 30; i++)
            {
                var changing = Data();
                changing.StatusMetrics[0].Detail = "Live energy detail " + i;
                changing.TemperatureMetrics[0].Detail = "Live weather detail " + i;
                changing.RecoveryMetrics[0].Detail = "Live clinical detail " + i;
                Snapshot(changing); refresh.Invoke(panel, null);
            }
            Require(uploads == before && compositions == beforeCompositions,
                "Hidden changing tooltips or environment details recreated native textures.");
            var wrapCache = (IDictionary?)typeof(TooltipText).GetField("panelCache", All)!.GetValue(null);
            Require(wrapCache is null || wrapCache.Count == 0, "Hidden tooltip measured Cairo text.");
            var hover = (CharacterPanelHoverText)energy.GetElement("metric-EnergyBalance-stamina-help");
            var hoverBounds = hover.Bounds;
            mouseX = (int)(hoverBounds.absX + hoverBounds.OuterWidth / 2);
            mouseY = (int)(hoverBounds.absY + hoverBounds.OuterHeight / 2);
            hover.RenderInteractiveElements(0);
            Require(uploads > before && hover.GetText().Contains("Live energy detail 29"), "Hover did not compose the latest detail.");
            before = uploads;
            for (var i = 0; i < 60; i++) hover.RenderInteractiveElements(.016f);
            Require(uploads == before, "Stationary tooltip rebuilt every frame.");
            hover.SetSource(""); hover.RenderInteractiveElements(0);
            hover.SetSource("Restored detail"); hover.RenderInteractiveElements(0);
            Require(hover.GetText().Contains("Restored detail"), "Cleared tooltip failed to recover.");
            before = uploads;
            PinnedCharacterPanelsHud.RenderingReadOnly = true;
            try { hover.SetSource("Pinned detail"); hover.RenderInteractiveElements(0); }
            finally { PinnedCharacterPanelsHud.RenderingReadOnly = false; }
            Require(uploads == before, "Read-only pinned panel composed hidden tooltip.");
            mouseX = mouseY = -1000;

            var bar = energy.GetStatbar("metric-EnergyBalance-stamina-bar");
            var set = type.GetMethod("SetBarRange", All)!;
            set.Invoke(panel, [bar, 50.001d, 0d, 100d]);
            Require(uploads == before, "Subpixel bar change recreated Cairo overlays.");
            set.Invoke(panel, [bar, 60d, 0d, 100d]);
            Drain(2);
            Require(uploads > before, "Visible bar change did not update native Cairo overlays.");
            before = uploads;
            var changed = Data(); changed.StomachCompositionG = [100, 600, 100, 100, 50, 50];
            Snapshot(changed); refresh.Invoke(panel, null);
            Require(uploads > before, "Changed stomach layers were not drawn.");

            beforeCompositions = compositions;
            Snapshot(Data(20)); refresh.Invoke(panel, null);
            Drain(((IDictionary)type.GetField("barValues", All)!.GetValue(panel)!).Count * 2);
            Require(compositions > beforeCompositions && (int)type.GetField("composedImmuneRows", All)!.GetValue(panel)! == 20,
                "Disease count change did not compose actual rows.");
            immune = dialog.Composers["vsslssmod-character-diseaserecovery"]!;
            Require(immune.GetDynamicText("immune-disease-19") is not null && immune.GetDynamicText("immune-disease-20") is null,
                "Disease count truncated rows or retained spare rows.");
            var content = (ElementBounds)type.GetField("immuneContent", All)!.GetValue(panel)!;
            var clip = (ElementBounds)type.GetField("immuneClip", All)!.GetValue(panel)!;
            Require(content.fixedHeight > clip.fixedHeight, "Long disease list lost scrollable content.");

            typeof(SeraphClientBridge).GetProperty("LastReceivedUtc")!.SetValue(bridge, DateTime.UtcNow.AddSeconds(-10));
            refresh.Invoke(panel, null);
            Drain(((IDictionary)type.GetField("barValues", All)!.GetValue(panel)!).Count * 2);
            Require((int)type.GetField("composedImmuneRows", All)!.GetValue(panel)! == 1, "Expired snapshot retained disease rows.");
            Snapshot(Data()); refresh.Invoke(panel, null);
            Drain(2);
            RuntimeEnv.GUIScale = 1.5f; beforeCompositions = compositions; refresh.Invoke(panel, null);
            Drain(((IDictionary)type.GetField("barValues", All)!.GetValue(panel)!).Count * 2);
            Require(compositions > beforeCompositions, "GUI scale change skipped composition.");
            dialog.TryClose(); before = uploads;
            Require(dialog.Composers["vsslssmod-character-body"] is null
                && ((IDictionary)type.GetField("retainedPanels", All)!.GetValue(panel)!).Count == 7,
                "Closed panel composers were not protected from vanilla ClearComposers.");
            for (var i = 0; i < 100; i++) { Snapshot(Data()); refresh.Invoke(panel, null); }
            Require(uploads == before, "Closed C refreshed unpinned textures.");
            var pins = (HashSet<string>)type.GetField("pinnedPanels", All)!.GetValue(panel)!;
            pins.Add("vsslssmod-character-body");
            var pinnedData = Data(); pinnedData.GutMassG = 1001;
            Snapshot(pinnedData); refresh.Invoke(panel, null);
            Require(uploads > before && (int)type.GetField("composedImmuneRows", All)!.GetValue(panel)! == 1,
                "Retained pinned body stopped refreshing while C was closed.");
            Snapshot(Data()); refresh.Invoke(panel, null); pins.Clear();
            // Reproduce vanilla's reopen lifecycle, clearing native windows
            // before ComposeExtraGuis. Retained panels must survive untouched.
            typeof(GuiDialog).GetMethod("ClearComposers", All)!.Invoke(dialog, null);
            dialog.Composers["playercharacter"] = Composer("playercharacter", ElementBounds.Fixed(0, 0, 300, 340).WithEmptyParent()).Compose(false);
            dialog.Composers["environment"] = Composer("environment", ElementBounds.Fixed(0, 0, 300, 100).WithEmptyParent())
                .AddDynamicText("", CairoFont.WhiteSmallText(), ElementBounds.Fixed(0, 0, 280, 20), "dyntext").Compose(false);
            before = uploads; beforeCompositions = compositions;
            dialog.TryOpen(false); timer.Restart(); compose.Invoke(panel, null);
            var reopen = timer.Elapsed.TotalMilliseconds;
            Require(before == uploads && beforeCompositions == compositions
                && ((IDictionary)type.GetField("retainedPanels", All)!.GetValue(panel)!).Count == 0,
                $"Reopening rebuilt owned composers instead of restoring them: uploads {before}->{uploads}, compositions {beforeCompositions}->{compositions}; layout {type.GetField("panelLayout", All)!.GetValue(panel)}.");
            CheckTooltipCache(mod);
            var report = $"Native Cairo C panels (cached flat background, mock GPU): first composition {firstOpen:0.00} ms, retained reopen (scale 1.5) {reopen:0.00} ms; same-snapshot refresh {unchanged:0.0000} ms. Reopen + 100 same snapshots + 30 identical new snapshots: zero owned texture uploads/recompositions. Disease rows 1 -> 20 -> stale 1; scrolling, scale, changed gut/bar and closed panel checked. Not gameplay FPS; native inventory rebuild excluded from reopen.";
            File.WriteAllText(TestArtifacts.FilePath("panel-refresh-performance.txt"), report);
            Console.WriteLine("PASS " + report);
            Console.WriteLine("PASS changing hidden energy/weather/clinical details: 30 snapshots, zero text wrapping, texture uploads or compositions; latest detail on hover, stationary reuse, clear/recovery and read-only pin checked.");
        }
        finally
        {
            RuntimeEnv.GUIScale = scale;
            if (oldPattern is null) patterns.Remove(patternKey); else patterns[patternKey] = oldPattern;
        }
    }

    private static void CheckTooltipCache(Assembly mod)
    {
        var type = mod.GetType("Seraph.Client.TooltipText", true)!;
        type.GetMethod("ClearPanelCache", All)!.Invoke(null, null);
        var wrap = type.GetMethod("Panel", All)!;
        var font = CairoFont.WhiteSmallText();
        var text = "Detailed clinical effect and recovery conditions. ";
        var first = wrap.Invoke(null, [text, font]);
        Require(ReferenceEquals(first, wrap.Invoke(null, [text, font.Clone()])), "Shared wrap cache missed equivalent font.");
        var cache = (IDictionary)type.GetField("panelCache", All)!.GetValue(null)!;
        wrap.Invoke(null, [text, font.Clone().WithFontSize(20)]);
        Require(cache.Count == 2, "Font change reused incorrect wrapping.");
        var scale = RuntimeEnv.GUIScale;
        try { RuntimeEnv.GUIScale = scale + .25f; wrap.Invoke(null, [text, font]); Require(cache.Count == 3, "Scale missing from wrap key."); }
        finally { RuntimeEnv.GUIScale = scale; }
        for (var i = 0; i < 600; i++) wrap.Invoke(null, [text + i, font]);
        Require(cache.Count == 512, "Live details grew an unbounded Cairo wrap cache.");
    }

    private static void Require(bool condition, string reason) { if (!condition) throw new Exception(reason); }
    private sealed class CharacterFixture(ICoreClientAPI api) : GuiDialogCharacterBase(api)
    {
        public override List<GuiTab> Tabs { get; } = [];
        public override List<Action<GuiComposer>> RenderTabHandlers { get; } = [];
        public override event Action ComposeExtraGuis { add { } remove { } }
        public override event Action<int> TabClicked { add { } remove { } }
        public override string ToggleKeyCombinationCode => "characterdialog";
    }
}
