using System.Reflection;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using Cairo;
using Seraph.Core.Framework;
using Seraph.World;
using Vintagestory.API.Client;
using Vintagestory.API.Common;
using Vintagestory.API.Common.Entities;
using Vintagestory.API.Config;
using Vintagestory.API.MathTools;
using Vintagestory.GameContent;

internal static class MapHoverRenderChecks
{
    private const BindingFlags All = BindingFlags.Instance | BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic;
    internal static void Run()
    {
        static void Require(bool ok, string message) { if (!ok) throw new Exception(message); }
        static object? Default(MethodInfo m) => m.ReturnType == typeof(void) ? null : m.ReturnType.IsValueType ? Activator.CreateInstance(m.ReturnType) : null;
        T Proxy<T>(System.Func<MethodInfo, object?[]?, object?> f) => (T)RoutingProxy.Make(typeof(T), f);
        var originalScale = RuntimeEnv.GUIScale; var originalLocale = Lang.CurrentLocale;
        var mouseX = 180; var mouseY = 180; long now = 1000; var uploads = 0; var tooltipDraws = 0;
        var player = new EntityPlayer();
        var clientPlayer = FixtureClientPlayer.Make(player);
        BindNativePlayerEntity(clientPlayer, player);
        var accessor = Proxy<IBlockAccessor>((m, _) => m.Name == "GetRainMapHeightAt" ? 100 : Default(m));
        var world = Proxy<IClientWorldAccessor>((m, _) => m.Name switch {
            "get_ElapsedMilliseconds" => now, "get_Player" => clientPlayer, "get_BlockAccessor" => accessor,
            "get_DefaultSpawnPosition" => new EntityPos(), _ => Default(m)
        });
        var input = Proxy<IInputAPI>((m, _) => m.Name switch {
            "get_MouseX" => mouseX, "get_MouseY" => mouseY,
            "get_KeyboardKeyStateRaw" => new bool[256], _ => Default(m)
        });
        var gui = Proxy<IGuiAPI>((m, a) => {
            if (m.Name == "LoadOrUpdateCairoTexture")
            {
                var surface = (ImageSurface)a![0]!; var texture = (LoadedTexture)a[2]!;
                texture.TextureId = ++uploads; texture.Width = surface.Width; texture.Height = surface.Height;
                return null;
            }
            return m.Name == "LoadCairoTexture" ? ++uploads : Default(m);
        });
        var render = Proxy<IRenderAPI>((m, a) => {
            if (m.Name == "get_ScissorStack") return Activator.CreateInstance(m.ReturnType);
            if (m.Name == "Render2DTexture" && Convert.ToSingle(a![5]) == 500) tooltipDraws++;
            return m.Name switch { "get_FrameWidth" => 1920, "get_FrameHeight" => 1080, _ => Default(m) };
        });
        var logger = Proxy<ILogger>((_, _) => null);
        var events = Proxy<IClientEventAPI>((m, a) => { if (m.Name == "EnqueueMainThreadTask") ((Action)a![0]!)(); return Default(m); });
        var api = Proxy<ICoreClientAPI>((m, _) => m.Name switch {
            "get_World" => world, "get_Input" => input, "get_Render" => render, "get_Gui" => gui,
            "get_Event" => events, "get_Logger" => logger, _ => Default(m)
        });
        try
        {
            var edgeMethod = typeof(PollutionMapLayer).GetMethod("GridEdges", All)!;
            var edges = ((System.Collections.IEnumerable)edgeMethod.Invoke(null, [new[]
            {
                (0, 0), (1, 0), (0, 1), (3, 0)
            }])!).Cast<object>().Select(edge => (
                X: (int)edge.GetType().GetField("Item1")!.GetValue(edge)!,
                Z: (int)edge.GetType().GetField("Item2")!.GetValue(edge)!,
                Vertical: (bool)edge.GetType().GetField("Item3")!.GetValue(edge)!)).ToHashSet();
            Require(edges.Count == 14, $"Sparse grid lost or duplicated edge segments: expected 14, got {edges.Count}.");
            Require(edges.Contains((1, 0, true)) && edges.Contains((1, 1, true))
                && edges.Contains((1, 0, false)) && edges.Contains((1, 1, false)),
                "Adjacent cells or the boundary around an interior hole lost a grid segment.");
            Require(edges.Contains((3, 0, true)) && edges.Contains((4, 0, true)),
                "A disjoint sampled region lost its vertical grid segments.");
            foreach (var locale in new[] { "zh-cn", "en" })
            foreach (var scale in new[] { 1f, 1.5f, 2f })
            {
                RuntimeEnv.GUIScale = scale;
                var translation = new TranslationService(locale, null!, null!, locale == "zh-cn" ? EnumLinebreakBehavior.AfterCharacter : EnumLinebreakBehavior.AfterWord);
                var entries = JsonSerializer.Deserialize<Dictionary<string, string>>(File.ReadAllText($"assets/vsslssmod/lang/{locale}.json"))!;
                typeof(TranslationService).GetField("entryCache", All)!.SetValue(translation, entries.ToDictionary(p => "vsslssmod:" + p.Key, p => p.Value));
                typeof(TranslationService).GetField("loaded", All)!.SetValue(translation, true);
                Lang.AvailableLanguages[locale] = translation; Lang.ChangeLanguage(locale);
                var dialog = (MapDialog)RuntimeHelpers.GetUninitializedObject(typeof(MapDialog)); dialog.Init(api);
                var manager = (WorldMapManager)RuntimeHelpers.GetUninitializedObject(typeof(WorldMapManager)); manager.worldMapDlg = dialog;
                var layer = new PollutionMapLayer(api, manager);
                var bounds = ElementBounds.Fixed(0, 28, 640, 360).WithEmptyParent(); bounds.CalcWorldBounds();
                var map = new GuiElementMap([layer, new OtherLayer(api)], api, dialog, bounds, false)
                    { CurrentBlockViewBounds = new Cuboidd(0, 0, 0, 64, 1, 64) };
                var composer = (GuiComposer)Activator.CreateInstance(typeof(GuiComposer), All, null,
                    [api, ElementBounds.Fixed(0, 0, 640, 400).WithEmptyParent(), "map-hover-render"], null)!;
                composer.Bounds.CalcWorldBounds();
                composer.AddHoverText("", CairoFont.WhiteDetailText(), 350, bounds.FlatCopy(), "hoverText");
                composer.AddInteractiveElement(map, "mapElem").Compose(false); dialog.SingleComposer = composer;
                map.CurrentBlockViewBounds = new Cuboidd(0, 0, 0, 64, 1, 64);
                var tooltip = composer.GetHoverText("hoverText");
                layer.OnLoaded();
                void Frame(float dt = 0) { dialog.OnRenderGUI(dt); dialog.OnFinalizeFrame(dt); }
                dialog.OnMouseMove(new MouseEvent(mouseX, mouseY));
                Require(tooltip.GetText().Contains("Other waypoint") && tooltip.GetText().Contains(Lang.Get("vsslssmod:pollution-map-unsampled")),
                    "Native dialog mouse dispatch did not compose shared layer hover text.");
                tooltip.SetNewText("native coordinate only");
                dialog.OnMouseMove(new MouseEvent(mouseX, mouseY));
                Require(tooltip.GetText().Contains("Other waypoint")
                    && tooltip.GetText().Contains(Lang.Get("vsslssmod:pollution-map-unsampled")),
                    "Native map callback did not rebuild pollution details after a tooltip update.");
                tooltipDraws = 0; Frame();
                Require(tooltip.IsNowShown && tooltipDraws > 0, "Full map did not actually draw its native hover texture.");
                Require(tooltip.GetText().Contains("Other waypoint")
                    && tooltip.GetText().Contains(Lang.Get("vsslssmod:pollution-map-unsampled")),
                    "Shared native tooltip lost other map-layer text or pollution data.");
                var before = uploads;
                for (var i = 0; i < 60; i++) Frame(.016f);
                Require(uploads == before, "Stationary map recompiles Cairo every frame.");
                now += 200; map.CurrentBlockViewBounds.Translate(32, 0, 0); layer.OnTick(0); Frame();
                Require(uploads > before, "Panning under a stationary pointer did not update the actual tooltip.");
                var view = map.CurrentBlockViewBounds;
                var sampleWorld = new Vec3d();
                map.TranslateViewPosToWorldPos(new Vec2f(mouseX - (float)map.Bounds.renderX,
                    mouseY - (float)map.Bounds.renderY), ref sampleWorld);
                var sampleX = (int)Math.Floor(sampleWorld.X / 32);
                var sampleZ = (int)Math.Floor(sampleWorld.Z / 32);
                var samples = Enumerable.Range(0, 4).Select(i => new {
                    Snapshot = new { Chunk = new { X = sampleX + i, Z = sampleZ, Dimension = 0 }, At = now,
                        Load = new PollutionLoad(1, 0, 0, 0, 0), Manure = 1, Fires = 0, Carrion = 0,
                        Animals = new Dictionary<string, int>(), TemperatureC = 20d, FreshWater = 1, SaltWater = 0,
                        SourceCount = 1, Sources = new RegionalSources(), Region = new RegionalEnvironment() },
                    AgeSeconds = 2d, HazardPerL = .5d, ServingL = .25d
                }).ToArray();
                before = uploads; layer.OnDataFromServer(JsonSerializer.SerializeToUtf8Bytes(samples));
                Require(uploads > before, "Incoming data did not update the actual native tooltip.");
                Frame();
                Require(tooltip.IsNowShown && tooltip.GetText().Contains("20.0"),
                    $"Received sample did not reach the visible tooltip. visible={tooltip.IsVisible}, shown={tooltip.IsNowShown}, text={tooltip.GetText()}");
                Require((double)typeof(GuiElementHoverText).GetField("hoverHeight", All)!.GetValue(tooltip)! <= 1080, "Sample tooltip exceeded the tested screen height.");
                var oldComposer = composer;
                var replacement = new GuiElementMap(map.mapLayers, api, dialog, bounds.FlatCopy(), false);
                composer = (GuiComposer)Activator.CreateInstance(typeof(GuiComposer), All, null,
                    [api, ElementBounds.Fixed(0, 0, 640, 400).WithEmptyParent(), "map-hover-recomposed"], null)!;
                composer.Bounds.CalcWorldBounds();
                composer.AddHoverText("", CairoFont.WhiteDetailText(), 350, bounds.FlatCopy(), "hoverText");
                composer.AddInteractiveElement(replacement, "mapElem").Compose(false);
                replacement.CurrentBlockViewBounds = map.CurrentBlockViewBounds.Clone();
                dialog.SingleComposer = composer; tooltip = composer.GetHoverText("hoverText");
                layer.OnTick(0);
                Frame();
                Require(tooltip.IsNowShown && tooltip.GetText().Contains("20.0"),
                    "Replaced native composer remained empty with the same cursor/world bounds/time.");
                oldComposer.Dispose();
                // The native dialog shows its coordinate tooltip in the title/header
                // area too; pollution layer details must not leak there.
                mouseX = 10; mouseY = 10; dialog.OnMouseMove(new MouseEvent(mouseX, mouseY)); Frame();
                Require(tooltip.IsNowShown && !tooltip.GetText().Contains(Lang.Get("vsslssmod:pollution-map-title")),
                    "Pollution details leaked outside the map viewport or native tooltip was disabled.");
                mouseX = 180; mouseY = 180; dialog.State(true, false, EnumDialogType.Dialog); Frame();
                Require(!tooltip.IsVisible, "Hover remained enabled after a modal/focus change.");
                dialog.State(true, true, EnumDialogType.HUD); Frame();
                Require(!tooltip.GetText().Contains(Lang.Get("vsslssmod:pollution-map-title")),
                    "Pollution map details appeared in HUD/minimap mode.");
                layer.Dispose(); composer.Dispose();
            }
        }
        finally { RuntimeEnv.GUIScale = originalScale; Lang.ChangeLanguage(originalLocale); }
        Console.WriteLine("PASS native map mouse dispatch and Cairo hover in EN/ZH at 3 scales; other layers preserved, stationary/pan/data updates, focus/HUD/header guards, sparse grid adjacency/hole/disjoint cells and no per-frame Cairo");
    }

    private static void BindNativePlayerEntity(FixtureClientPlayer player, EntityPlayer entity)
    {
        var playerType = player.GetType().BaseType!;
        var dataField = playerType.GetField("worlddata", All)
            ?? throw new MissingFieldException(playerType.FullName, "worlddata");
        var data = dataField.GetValue(player);
        if (data is null)
        {
            var create = dataField.FieldType.GetMethod("CreateNew", BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic)
                ?? throw new MissingMethodException(dataField.FieldType.FullName, "CreateNew");
            data = create.Invoke(null, null);
            dataField.SetValue(player, data);
        }
        if (data is null) throw new InvalidOperationException("Client player world data was not initialized.");
        var dataType = data.GetType();
        var property = dataType.GetProperty("EntityPlayer", All);
        if (property?.CanWrite == true) { property.SetValue(data, entity); return; }
        var field = dataType.GetField("EntityPlayer", All)
            ?? dataType.GetField("<EntityPlayer>k__BackingField", All)
            ?? throw new MissingFieldException(dataType.FullName, "EntityPlayer");
        field.SetValue(data, entity);
    }

    private sealed class OtherLayer(ICoreAPI api) : MapLayer(api, null!)
    {
        public override string Title => "Other";
        public override string LayerGroupCode => "other";
        public override EnumMapAppSide DataSide => EnumMapAppSide.Client;
        public override void OnMouseMoveClient(MouseEvent args, GuiElementMap map, StringBuilder hover) => hover.AppendLine("Other waypoint");
    }

    private sealed class MapDialog : GuiDialogWorldMap
    {
        private MapDialog() : base(null!, null!, null!, null!) { }
        internal void Init(ICoreClientAPI api)
        {
            capi = api; Composers = new DlgComposers(this);
            typeof(GuiDialogWorldMap).GetField("hoveredWorldPos", All)!.SetValue(this, new Vec3d());
            State(true, true, EnumDialogType.Dialog);
        }
        internal void State(bool open, bool focus, EnumDialogType type) { opened = open; focused = focus; dialogType = type; }
    }
}


