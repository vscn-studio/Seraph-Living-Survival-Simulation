using System.Reflection;
using System.Runtime.CompilerServices;
using System.Text.Json;
using Cairo;
using Seraph.Client;
using Vintagestory.API.Client;
using Vintagestory.API.Common;
using Vintagestory.API.Config;
using Vintagestory.API.MathTools;

internal static class PinnedCharacterPanelChecks
{
    private const BindingFlags All = BindingFlags.Instance | BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic;

    internal static void Run(Assembly mod, string? nativeGame)
    {
        var hudType = mod.GetType("Seraph.Client.PinnedCharacterPanelsHud", true)!;
        var scope = hudType.GetField("RenderingReadOnly", All)!;
        var visible = false;
        var draws = 0;
        var posts = 0;
        var fail = false;
        var loaded = new List<GuiDialog>();
        var focusRequests = 0;
        var inputReads = 0;
        var matrixDepth = 0;
        var translatedDepth = 0d;
        var dialogPositions = new Dictionary<string, Vec2i?>();
        ICoreClientAPI api = null!;
        IconUtil icons = null!;
        string? export = null;
        var uploads = 0;
        object? Default(MethodInfo m) => m.ReturnType == typeof(void) ? null
            : m.ReturnType.IsValueType ? Activator.CreateInstance(m.ReturnType) : null;
        var gui = RoutingProxy.Make(typeof(IGuiAPI), (m, args) => {
            switch (m.Name)
            {
                case "get_LoadedGuis": case "get_OpenedGuis": return loaded;
                case "RegisterDialog": loaded.AddRange((GuiDialog[])args![0]!); return null;
                case "TriggerDialogClosed":
                    if (args![0] is GuiDialog { UnregisterOnClose: true } closing) loaded.Remove(closing);
                    return null;
                case "RequestFocus": focusRequests++; return null;
                case "get_Icons": return icons;
                case "GetDialogPosition": return dialogPositions.GetValueOrDefault((string)args![0]!);
                case "SetDialogPosition": dialogPositions[(string)args![0]!] = (Vec2i?)args[1]; return null;
                case "LoadOrUpdateCairoTexture":
                    var surface = (ImageSurface)args![0]!;
                    var texture = (LoadedTexture)args[2]!;
                    texture.TextureId = ++uploads;
                    texture.Width = surface.Width; texture.Height = surface.Height;
                    if (export is not null && surface.Width >= 250) surface.WriteToPng(export);
                    return null;
                default: return Default(m);
            }
        });
        var render = RoutingProxy.Make(typeof(IRenderAPI), (m, args) => {
            switch (m.Name)
            {
                case "get_FrameWidth": return 1920;
                case "get_FrameHeight": return 1080;
                case "GlPushMatrix": matrixDepth++; return null;
                case "GlPopMatrix": matrixDepth--; return null;
                case "GlTranslate": translatedDepth = Convert.ToDouble(args![2]); return null;
                default: return Default(m);
            }
        });
        var input = RoutingProxy.Make(typeof(IInputAPI), (m, _) => { inputReads++; return Default(m); });
        var events = RoutingProxy.Make(typeof(IClientEventAPI), (m, _) => m.Name == "RegisterGameTickListener" ? 1L : Default(m));
        var logger = RoutingProxy.Make(typeof(ILogger), (m, _) => Default(m));
        api = (ICoreClientAPI)RoutingProxy.Make(typeof(ICoreClientAPI), (m, _) => m.Name switch {
            "get_Gui" => gui, "get_Render" => render, "get_Input" => input, "get_Event" => events,
            "get_Logger" => logger, _ => Default(m)
        });
        icons = new IconUtil(api);
        using (var hud = (GuiDialog)Activator.CreateInstance(hudType, api, (Func<bool>)(() => visible),
            (Action<float>)(_ => { Require((bool)scope.GetValue(null)!, "Unscoped read-only render."); draws++; if (fail) throw new InvalidOperationException("probe"); }),
            (Action<float>)(_ => { Require((bool)scope.GetValue(null)!, "Unscoped post render."); posts++; }))!)
        {
            Require(hud.DialogType == EnumDialogType.HUD && !hud.Focusable && !hud.PrefersUngrabbedMouse
                && !hud.DisableMouseGrab && !hud.ShouldReceiveKeyboardEvents() && !hud.ShouldReceiveMouseEvents()
                && !hud.CaptureAllInputs() && !hud.CaptureRawMouse() && hud.ToggleKeyCombinationCode is null,
                "Pinned HUD can capture game input or toggle C/ESC.");
            hud.TryOpen(false);
            hud.OnRenderGUI(.1f); hud.OnFinalizeFrame(.1f);
            Require(draws == 0 && posts == 0 && focusRequests == 0, "Hidden pin drew or requested focus.");
            visible = true;
            hud.OnRenderGUI(.1f); hud.OnFinalizeFrame(.1f);
            Require(draws == 1 && posts == 1 && !(bool)scope.GetValue(null)! && matrixDepth == 0 && translatedDepth == -150,
                "HUD render/finalize scope or native depth leaked.");
            fail = true;
            try { hud.OnRenderGUI(.1f); throw new Exception("Expected probe exception."); }
            catch (InvalidOperationException) { }
            Require(!(bool)scope.GetValue(null)! && matrixDepth == 0, "Exception leaked tooltip or render matrix state.");
            hud.TryClose();
            Require(!loaded.Contains(hud), "Closed pin HUD remained registered.");
        }
        if (nativeGame is null || !OperatingSystem.IsWindows())
        {
            Console.WriteLine("PASS pinned HUD passive input flags, visibility and exception-safe render scope (native Cairo checks skipped)");
            return;
        }

        var previousLocale = Lang.CurrentLocale;
        var previousScale = RuntimeEnv.GUIScale;
        var previousLanguages = new[] { "en", "zh-cn" }.ToDictionary(k => k, k => Lang.AvailableLanguages.GetValueOrDefault(k));
        var output = TestArtifacts.DirectoryPath("pinned-panel-render");
        Directory.CreateDirectory(output);
        try
        {
            foreach (var locale in new[] { "en", "zh-cn" })
            {
                var translation = new TranslationService(locale, (ILogger)logger, null!, EnumLinebreakBehavior.AfterWord);
                translation.PreLoad(System.IO.Path.Combine(nativeGame, "assets"));
                foreach (var entry in JsonSerializer.Deserialize<Dictionary<string, string>>(File.ReadAllText($"assets/vsslssmod/lang/{locale}.json"))!)
                    translation.GetAllEntries()["vsslssmod:" + entry.Key] = entry.Value;
                Lang.AvailableLanguages[locale] = translation;
            }
            var integrationType = mod.GetType("Seraph.Client.CharacterPanelIntegration", true)!;
            var bridge = (SeraphClientBridge)RuntimeHelpers.GetUninitializedObject(typeof(SeraphClientBridge));
            using var integration = (IDisposable)Activator.CreateInstance(integrationType, api, bridge)!;
            var character = new CharacterFixture(api);
            loaded.Add(character);
            integrationType.GetMethod("OnLevelReady", All)!.Invoke(integration, null);
            integrationType.GetMethod("TryRegister", All)!.Invoke(integration, null);
            var keys = (string[])integrationType.GetField("PinnableKeys", All)!.GetValue(null)!;
            var pins = (HashSet<string>)integrationType.GetField("pinnedPanels", All)!.GetValue(integration)!;
            var toggle = integrationType.GetMethod("TogglePinnedPanel", All)!;
            var canRender = integrationType.GetMethod("CanRenderPinnedPanels", All)!;
            Require(keys.Length == 6 && !keys.Any(k => k.Contains("environment") || k.EndsWith("status")), "Pin allowlist includes vanilla/obsolete window.");
            foreach (var invalid in new[] { "playercharacter", "environment", "vsslssmod-character-environment", "thirdparty", "vsslssmod-character-status" })
                toggle.Invoke(integration, [invalid]);
            Require(pins.Count == 0, "Foreign or vanilla composer accepted pin.");

            foreach (var locale in new[] { "en", "zh-cn" })
            foreach (var scale in new[] { 1f, 1.5f, 2f })
            foreach (var width in new[] { 250, 300, 360 })
            {
                Lang.ChangeLanguage(locale); RuntimeEnv.GUIScale = scale;
                var bounds = ElementBounds.Fixed(40, 60, width, 80).WithEmptyParent();
                using var composer = NewComposer(api, bounds, keys[0]);
                integrationType.GetMethod("AddPinnedTitleBar", All)!.Invoke(integration,
                    [composer, keys[0], Lang.Get("vsslssmod:character-page-blood-circulation")]);
                var title = (GuiElementDialogTitleBar)composer.GetElement("panel-title");
                export = $"{output}/{locale}-{width}-scale{scale:0.0}-off.png";
                composer.Compose(false);
                var pin = title.Bounds.absX + title.Bounds.OuterWidth - 67 * scale;
                var pinY = title.Bounds.absY + title.Bounds.InnerHeight / 2;
                var geometry = ((double X, double Y, double Size))title.GetType().GetMethod("PinGeometry", All)!.Invoke(null, [title.Bounds.OuterWidth, title.Bounds.InnerHeight, (double)scale])!;
                Require(Math.Abs(geometry.Y + geometry.Size / 2 - title.Bounds.InnerHeight / 2) < .01,
                    "Pin is not vertically centered in the actual native title bar.");
                Require(geometry.X + geometry.Size <= title.Bounds.OuterWidth - 52 * scale,
                    "Pin overlaps native menu.");
                Require(title.Font.GetTextExtents(Lang.Get("vsslssmod:character-page-blood-circulation")).XAdvance
                    <= title.Bounds.OuterWidth - 94 * scale + .1, "Title overlaps pin.");
                var inputBefore = inputReads;
                scope.SetValue(null, true);
                try { title.RenderInteractiveElements(.1f); }
                finally { scope.SetValue(null, false); }
                Require(inputReads == inputBefore, "Read-only title or tooltip reacted to gameplay mouse position.");
                MouseEvent Mouse(EnumMouseButton button, double x = -1) => new((int)(x < 0 ? pin : x), (int)pinY, button, 0);
                title.OnMouseDown(api, Mouse(EnumMouseButton.Right)); title.OnMouseUp(api, Mouse(EnumMouseButton.Right));
                Require(pins.Count == 0, "Right click toggled pin.");
                title.OnMouseDown(api, Mouse(EnumMouseButton.Left)); title.OnMouseUp(api, Mouse(EnumMouseButton.Left, pin - 40 * scale));
                Require(pins.Count == 0, "Cancelled pin click toggled pin.");
                title.OnMouseDown(api, Mouse(EnumMouseButton.Left)); title.OnFocusLost(); title.OnMouseUp(api, Mouse(EnumMouseButton.Left));
                Require(pins.Count == 0, "Lost-focus click toggled pin.");
                export = $"{output}/{locale}-{width}-scale{scale:0.0}-on.png";
                title.OnMouseDown(api, Mouse(EnumMouseButton.Left)); title.OnMouseUp(api, Mouse(EnumMouseButton.Left));
                Require(pins.SetEquals([keys[0]]) && (bool)canRender.Invoke(integration, null)!, "Left click did not pin closed-C panel.");
                character.TryOpen(false);
                Require(!(bool)canRender.Invoke(integration, null)!, "Pinned panel renders twice while C is open.");
                character.TryClose();
                Require((bool)canRender.Invoke(integration, null)!, "Closing C lost pin.");
                toggle.Invoke(integration, [keys[0]]);
                Require(pins.Count == 0 && !(bool)canRender.Invoke(integration, null)!, "Unpin retained HUD drawing.");
                // Native three-line menu and drag still operate independently from pinning.
                var nativeMenu = typeof(GuiElementDialogTitleBar).GetField("listMenu", All)!.GetValue(title)!;
                title.OnMouseUp(api, Mouse(EnumMouseButton.Left, title.Bounds.absX + title.Bounds.OuterWidth - 45 * scale));
                Require((bool)nativeMenu.GetType().GetProperty("IsOpened")!.GetValue(nativeMenu)!, "Native title menu no longer opens.");
                title.OnFocusLost();
                Require(!(bool)nativeMenu.GetType().GetProperty("IsOpened")!.GetValue(nativeMenu)!, "Native title menu remains over gameplay.");
                typeof(GuiElementDialogTitleBar).GetMethod("SetUpMovableState", All)!.Invoke(title, ["manual"]);
                var startX = bounds.fixedX; var startY = bounds.fixedY;
                var dragX = (int)(title.Bounds.absX + 30 * scale); var dragY = (int)pinY;
                title.OnMouseDown(api, new MouseEvent(dragX, dragY, EnumMouseButton.Left, 0));
                title.OnMouseMove(api, new MouseEvent(dragX + (int)(20 * scale), dragY + (int)(10 * scale)));
                title.OnMouseUp(api, new MouseEvent(dragX + (int)(20 * scale), dragY + (int)(10 * scale), EnumMouseButton.Left, 0));
                Require(title.Movable && Math.Abs(bounds.fixedX - startX - 20) < .01 && Math.Abs(bounds.fixedY - startY - 10) < .01
                    && pins.Count == 0, "Native movable drag was changed by pin button.");
                dialogPositions.Clear();
                export = null;
            }

            RuntimeEnv.GUIScale = 1;
            var probes = new List<RenderProbe>();
            foreach (var key in keys.Append("playercharacter").Append("vsslssmod-character-environment"))
            {
                var composer = NewComposer(api, ElementBounds.Fixed(0, 0, 250, 80).WithEmptyParent(), key);
                var probe = new RenderProbe(api, ElementBounds.Fixed(0, 0, 1, 1));
                composer.AddInteractiveElement(probe, "probe").Compose(false);
                character.Composers[key] = composer; probes.Add(probe);
            }
            // Only selected live composers are borrowed; the HUD must not own/dispose them.
            toggle.Invoke(integration, [keys[1]]);
            var isVisible = integrationType.GetMethod("IsPanelVisible", All)!;
            Require((bool)isVisible.Invoke(integration, [keys[1]])!
                && !(bool)isVisible.Invoke(integration, [keys[0]])!, "Closed-C refresh includes unpinned panels or skips pinned panel.");
            var pinnedHud = (GuiDialog)integrationType.GetField("pinnedHud", All)!.GetValue(integration)!;
            var beforeRenderUploads = uploads;
            pinnedHud.OnRenderGUI(.1f); pinnedHud.OnFinalizeFrame(.1f);
            Require(probes[1].Renders == 1 && probes[1].Posts == 1 && probes.Where((_, i) => i != 1).All(p => p.Renders == 0 && p.Posts == 0), "Overlay rendered unselected or vanilla panel.");
            Require(uploads == beforeRenderUploads, "Static overlay re-uploaded composer textures per frame.");
            character.TryOpen(false); pinnedHud.OnRenderGUI(.1f); pinnedHud.OnFinalizeFrame(.1f);
            Require(probes[1].Renders == 1 && probes[1].Posts == 1, "C and overlay duplicated render.");
            character.TryClose();
            toggle.Invoke(integration, [keys[1]]);
            Require(probes.All(p => !p.Disposed), "Unpin disposed native-owned composers.");
            toggle.Invoke(integration, [keys[2]]);
            integrationType.GetMethod("OnLeaveWorld", All)!.Invoke(integration, null);
            Require(pins.Count == 0 && integrationType.GetField("pinnedHud", All)!.GetValue(integration) is null
                && probes.Take(6).All(p => p.Disposed) && !probes[6].Disposed && probes[7].Disposed,
                "World leave leaked pin/composers or disposed vanilla window.");
            character.Dispose();
            Require(focusRequests == 0, "Pin lifecycle requested focus.");
        }
        finally
        {
            RuntimeEnv.GUIScale = previousScale;
            foreach (var (key, value) in previousLanguages)
                if (value is not null) Lang.AvailableLanguages[key] = value; else Lang.AvailableLanguages.Remove(key);
            Lang.ChangeLanguage(previousLocale);
        }
        Console.WriteLine("PASS six-panel pin allowlist; native title/button bounds, menu, drag and click cancellation at 3 widths x 3 scales x 2 locales; closed-C visibility/render/finalize, no input/focus, duplicate rendering or per-frame uploads, unpin ownership and world cleanup; 36 native Cairo title renders (mock GPU)");
    }

    private static GuiComposer NewComposer(ICoreClientAPI api, ElementBounds bounds, string key)
    {
        // A real CreateCompo receives initialized window bounds; initialize the fixture root too.
        bounds.ParentBounds.CalcWorldBounds();
        return (GuiComposer)Activator.CreateInstance(typeof(GuiComposer), All, null, [api, bounds, key], null)!;
    }
    private static void Require(bool condition, string message) { if (!condition) throw new Exception(message); }

    private sealed class CharacterFixture(ICoreClientAPI api) : GuiDialogCharacterBase(api)
    {
        public override List<GuiTab> Tabs { get; } = [];
        public override List<Action<GuiComposer>> RenderTabHandlers { get; } = [];
        public override event Action ComposeExtraGuis { add { } remove { } }
        public override event Action<int> TabClicked { add { } remove { } }
        public override string ToggleKeyCombinationCode => "characterdialog";
    }

    private sealed class RenderProbe(ICoreClientAPI api, ElementBounds bounds) : GuiElement(api, bounds)
    {
        internal int Renders, Posts;
        internal bool Disposed;
        public override void RenderInteractiveElements(float dt) => Renders++;
        public override void PostRenderInteractiveElements(float dt) => Posts++;
        public override void Dispose() { Disposed = true; base.Dispose(); }
    }
}
