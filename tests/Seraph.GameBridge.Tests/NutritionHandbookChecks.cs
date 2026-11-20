using System.Collections;
using System.Reflection;
using System.Runtime.CompilerServices;
using Cairo;
using Path = System.IO.Path;
using Seraph.Client;
using Vintagestory.API.Client;
using Vintagestory.API.Common;
using Vintagestory.API.Config;

internal static class NutritionHandbookChecks
{
    private const BindingFlags All = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static | BindingFlags.Instance;
    internal static void Run(string nativeGame)
    {
        Require(!FoodKnowledgeCompatibility.ShouldList(survival: true, known: false),
            "Foragers Gamble unidentified food must stay out of the nutrition handbook.");
        Require(FoodKnowledgeCompatibility.ShouldList(survival: true, known: true)
            && FoodKnowledgeCompatibility.ShouldList(survival: true, known: null)
            && FoodKnowledgeCompatibility.ShouldList(survival: false, known: false),
            "Known food, absent optional compatibility, or non-survival mode was hidden.");
        System.Runtime.InteropServices.NativeLibrary.SetDllImportResolver(typeof(Context).Assembly, (name, _, _) =>
            System.Runtime.InteropServices.NativeLibrary.Load(Path.Combine(nativeGame, "Lib", name == "nanosvg" ? "nanosvg.dll" : "libcairo-2.dll")));
        var logger = (ILogger)RoutingProxy.Make(typeof(ILogger), (_, _) => null);
        foreach (var locale in new[] { "en", "zh-cn" })
        {
            var translation = new TranslationService(locale, logger, null!, EnumLinebreakBehavior.AfterWord);
            translation.PreLoad(Path.Combine(nativeGame, "assets"));
            foreach (var domain in new[] { "vsslssmod", "game" })
            foreach (var pair in System.Text.Json.JsonSerializer.Deserialize<Dictionary<string, string>>(File.ReadAllText("assets/" + domain + "/lang/" + locale + ".json"))!)
                translation.GetAllEntries()[domain + ":" + pair.Key] = pair.Value;
            Lang.AvailableLanguages[locale] = translation;
        }
        Lang.ChangeLanguage("en");
        RuntimeEnv.GUIScale = 1;
        using var surface = new ImageSurface(Format.Argb32, 2, 2);
        using var pattern = new SurfacePattern(surface);
        var patterns = (IDictionary)typeof(GuiElement).GetField("cachedPatterns", All)!.GetValue(null)!;
        patterns[new AssetLocation("game:64@0.125-gui/backgrounds/soil.png")] = new KeyValuePair<SurfacePattern, ImageSurface>(pattern, surface);
        object? Default(MethodInfo m) => m.ReturnType == typeof(void) ? null : m.ReturnType.IsValueType ? Activator.CreateInstance(m.ReturnType) : null;
        ICoreClientAPI api = null!;
        var width = 1280; var height = 720; var uploads = 0; var iconDraws = 0; var scissors = 0;
        bool failNextIcon = false;
        var mouseX = 0; var mouseY = 0;
        var inputApi = RoutingProxy.Make(typeof(IInputAPI), (m, _) => m.Name switch {
            "get_MouseX" => mouseX, "get_MouseY" => mouseY, _ => Default(m)
        });
        IconUtil icons = null!;
        var loaded = new List<GuiDialog>();
        var gui = RoutingProxy.Make(typeof(IGuiAPI), (m, a) => {
            switch (m.Name)
            {
                case "get_Text": return new TextDrawUtil();
                case "get_Icons": return icons;
                case "get_LoadedGuis": case "get_OpenedGuis": return loaded;
                case "CreateCompo":
                    var bounds = (ElementBounds)a![1]!;
                    bounds.WithParent(ElementBounds.Fixed(0, 0, width, height).WithEmptyParent());
                    bounds.ParentBounds.CalcWorldBounds();
                    return Activator.CreateInstance(typeof(GuiComposer), All, null, [api, bounds, a[0]!], null);
                case "LoadOrUpdateCairoTexture":
                    var image = (ImageSurface)a![0]!;
                    var texture = (LoadedTexture)a[2]!;
                    texture.TextureId = ++uploads; texture.Width = image.Width; texture.Height = image.Height;
                    return null;
                default: return Default(m);
            }
        });
        var render = RoutingProxy.Make(typeof(IRenderAPI), (m, a) => {
            if (m.Name == "get_FrameWidth") return width;
            if (m.Name == "get_FrameHeight") return height;
            if (m.Name == "PushScissor") scissors++;
            if (m.Name == "PopScissor") scissors--;
            if (m.Name == "RenderItemstackToGui")
            {
                Require(a![0] is ItemSlot { Inventory: not null }, "Food previews must provide an inventory to mod renderers.");
                Require(Math.Abs((double)a[3]! - 100) < .01, "Food preview depth must stay within the GUI clipping range.");
                iconDraws++;
                if (failNextIcon) { failNextIcon = false; throw new InvalidOperationException("broken mod renderer fixture"); }
            }
            return Default(m);
        });
        var registry = RoutingProxy.Make(typeof(IClassRegistryAPI), (m, _) => Default(m));
        string? openedLink = null;
        var links = new Dictionary<string, Action<LinkTextComponent>> { ["handbook"] = link => openedLink = link.Href };
        var tasks = new Queue<Action>();
        var events = RoutingProxy.Make(typeof(IClientEventAPI), (m, a) => {
            if (m.Name == "EnqueueMainThreadTask") tasks.Enqueue((Action)a![0]!);
            return Default(m);
        });
        api = (ICoreClientAPI)RoutingProxy.Make(typeof(ICoreClientAPI), (m, _) => m.Name switch {
            "get_Gui" => gui, "get_Render" => render, "get_Logger" => logger, "get_Input" => inputApi,
            "get_ClassRegistry" => registry, "get_LinkProtocols" => links, "get_Event" => events, _ => Default(m)
        });
        icons = new IconUtil(api);
        var bridge = (SeraphClientBridge)RuntimeHelpers.GetUninitializedObject(typeof(SeraphClientBridge));
        var type = typeof(SeraphClientBridge).Assembly.GetType("Seraph.Client.NutritionHandbookDialog", true)!;
        using var dialog = (GuiDialog)Activator.CreateInstance(type, All, null, [api, bridge], null)!;
        Require(dialog.DrawOrder < 0.2, "The native handbook must render above the nutrition handbook.");
        dialog.TryOpen();
        var previewMethod = type.GetMethod("PreviewStack", All)!;
        var obsolete = new Item { Code = new("primitivesurvival:fishfillet-raw"), CreativeInventoryTabs = [] };
        Require(previewMethod.Invoke(null, [obsolete, api]) is null, "Obsolete food with no native handbook stack must not be manufactured into a preview.");
        var validFood = new Item { Code = new("test:fish"), CreativeInventoryTabs = ["food"] };
        Require(previewMethod.Invoke(null, [validFood, api]) is ItemStack, "Normal handbook food must remain listed.");
        var entries = (IList)type.GetField("entries", All)!.GetValue(dialog)!;
        var entryType = type.GetNestedType("Entry", All)!;
        for (var i = 0; i < 200; i++)
        {
            var nutrients = new double[13]; var known = new bool[13];
            nutrients[0] = i % 2 == 0 ? i + 1 : 0; known[0] = true;
            var inventory = new DummyInventory(api);
            var preview = new DummySlot(new ItemStack(new Item { Code = new("test:food-" + i), ItemId = i + 1 }), inventory);
            inventory[0] = preview;
            entries.Add(Activator.CreateInstance(entryType, All, null,
                [preview, "Food " + i, new KeyValuePair<string, string>[] {
                    new("", "Per portion"), new("water", "Water 50 mL"), new("carbohydrate", "Carbohydrate 10 g"),
                    new("protein", "Protein 5 g"), new("calcium", "Calcium 1 %")
                 }, i / 1000d + .01, (double)i, nutrients, known], null));
        }
        void Call(string method) => type.GetMethod(method, All)!.Invoke(dialog, null);
        void Require(bool ok, string message) { if (!ok) throw new Exception(message); }
        int Count() => ((Array)type.GetField("displayed", All)!.GetValue(dialog)!).Length;
        foreach (var locale in new[] { "en", "zh-cn" })
        foreach (var scale in new[] { 1d, 1.25, 1.5 })
        foreach (var size in new[] { (1280, 720), (800, 480), (540, 400) })
        {
            Lang.ChangeLanguage(locale); RuntimeEnv.GUIScale = (float)scale;
            (width, height) = ((int)(size.Item1 * scale), (int)(size.Item2 * scale));
            dialog.ClearComposers(); Call("Compose");
            Require(Count() == 200, "Catalogue lost entries.");
            Require(new ItemStack(new Item { Code = new("game:jamhoneyportion") }).GetName() == (locale == "zh-cn" ? "蜂蜜" : "Honey"),
                "Honey ingredient must have a translated item name in both the catalogue and native tooltip.");
            var composer = dialog.SingleComposer;
            var contentSort = composer.GetToggleButton("nutrition-sort-content");
            var nameSort = composer.GetToggleButton("nutrition-sort-name");
            Require(contentSort.Bounds.fixedX + contentSort.Bounds.fixedWidth < nameSort.Bounds.fixedX
                && nameSort.Bounds.fixedX + nameSort.Bounds.fixedWidth <= composer.Bounds.fixedWidth - 70,
                "Sort buttons overlap the native fixed/movable menu in the title bar.");
            Require(composer.GetDynamicText("food-name-0").GetText() == "Food 0", "Default order must be by name.");
            Require(composer.GetToggleButton("nutrition-sort-name").On && !composer.GetToggleButton("nutrition-sort-content").On,
                "Only the default name sort must appear pressed.");
            void ClickSort(string key)
            {
                var button = composer.GetToggleButton(key);
                var click = new MouseEvent((int)(button.Bounds.renderX + 4), (int)(button.Bounds.renderY + 4), EnumMouseButton.Left, 0);
                dialog.OnMouseDown(click); dialog.OnMouseUp(click);
            }
            ClickSort("nutrition-sort-content");
            ClickSort("nutrition-sort-content");
            Require(composer.GetToggleButton("nutrition-sort-content").On && !composer.GetToggleButton("nutrition-sort-name").On,
                "Repeated clicks must keep the selected sort pressed and the other released.");
            Require(composer.GetDynamicText("food-name-0").GetText() == "Food 199", "All-food content order must use energy descending.");
            type.GetField("category", All)!.SetValue(dialog, "carbohydrate"); Call("Filter");
            Require(composer.GetDynamicText("food-name-0").GetText() == "Food 198", "Selected nutrient must control content order.");
            var emphasized = composer.GetRichtext("food-nutrients-0").Components.OfType<RichTextComponent>()
                .Where(c => c.Font.FontWeight == FontWeight.Bold).ToArray();
            Require(emphasized.Length == 1 && emphasized[0].DisplayText == "Carbohydrate 10 g"
                && emphasized[0].Font.Color[0] == 1 && emphasized[0].Font.Color[1] < .5,
                "Only the selected nutrient text and amount must be bold red.");
            ClickSort("nutrition-sort-name");
            Require(composer.GetToggleButton("nutrition-sort-name").On && !composer.GetToggleButton("nutrition-sort-content").On,
                "Switching sort must transfer the pressed state.");
            Require(composer.GetDynamicText("food-name-0").GetText() == "Food 0", "Name button must restore alphabetical order.");
            type.GetField("category", All)!.SetValue(dialog, "all"); Call("Filter");
            Require(composer.GetRichtext("food-nutrients-0").Components.OfType<RichTextComponent>().All(c => c.Font.FontWeight != FontWeight.Bold),
                "Returning to all foods must clear the previous nutrient highlight.");
            var clip = (ElementBounds)type.GetField("clip", All)!.GetValue(dialog)!;
            var labels = new[] { "all", "water", "carbohydrate", "protein", "fat", "fiber", "electrolytes", "calcium", "iron", "zinc", "a", "c", "bcomplex" };
            var categoryTabs = composer.GetVerticalTab("nutrition-categories");
            var tabBounds = categoryTabs.Bounds;
            var nativeTabs = (GuiTab[])typeof(GuiElementVerticalTabs).GetField("tabs", All)!.GetValue(categoryTabs)!;
            Require(nativeTabs.Length == labels.Length, "Inactive vitamin categories remain visible.");
            var nativeWidths = (int[])typeof(GuiElementVerticalTabs).GetField("tabWidths", All)!.GetValue(categoryTabs)!;
            var tabHeight = (double)typeof(GuiElementVerticalTabs).GetField("tabHeight", All)!.GetValue(categoryTabs)!;
            Require(tabBounds.fixedX < 0 && tabBounds.fixedX + tabBounds.fixedWidth == 0 && clip.fixedX == 16,
                "Categories are embedded in the content instead of attached to the left window edge.");
            Require(tabBounds.absX >= 0 && tabBounds.absX + tabBounds.OuterWidth <= width,
                "External category tabs leave the screen.");
            for (var i = 0; i < labels.Length; i++)
            {
                Require(nativeWidths[i] <= tabBounds.InnerWidth && (i + 1) * tabHeight + i * 2 * scale <= tabBounds.InnerHeight + .01,
                    "Native category labels exceed their width or height.");
                mouseX = (int)(tabBounds.absX + tabBounds.InnerWidth - nativeWidths[i] / 2);
                mouseY = (int)(tabBounds.absY + i * (tabHeight + 2 * scale) + tabHeight / 2);
                var click = new MouseEvent(mouseX, mouseY, EnumMouseButton.Left, 0);
                categoryTabs.OnMouseDownOnElement(api, click);
                Require(click.Handled && categoryTabs.ActiveElement == i && nativeTabs[i].Active
                    && nativeTabs.Count(tab => tab.Active) == 1,
                    "Native category click failed to select exactly one tab.");
                Require((string)type.GetField("category", All)!.GetValue(dialog)! == labels[i],
                    "Category click did not update the nutrient filter.");
            }
            categoryTabs.SetValue(0);
            var input = composer.GetTextInput("nutrition-search");
            input.SetValue("food-199");
            Require(Count() == 1 && ReferenceEquals(input, dialog.SingleComposer.GetTextInput("nutrition-search")), "Search loses focus or cannot match codes.");
            input.SetValue("no matches"); Require(Count() == 0, "Empty search result retained rows.");
            input.SetValue("");
            type.GetField("category", All)!.SetValue(dialog, "carbohydrate"); Call("Filter"); Require(Count() == 100, "Nutrient filter includes absent nutrients.");
            type.GetField("category", All)!.SetValue(dialog, "all"); Call("Filter");
            var scrollbar = composer.GetScrollbar("nutrition-scroll");
            Require(scrollbar.Bounds.fixedX >= clip.fixedX + clip.fixedWidth + 4
                && scrollbar.Bounds.fixedX + scrollbar.Bounds.fixedWidth <= composer.Bounds.fixedWidth - 24,
                "Scrollbar must have a gap from content and an inner margin from the window border.");
            scrollbar.CurrentYPosition = 25; scrollbar.TriggerChanged();
            var before = uploads;
            scrollbar.CurrentYPosition = 26; scrollbar.TriggerChanged();
            Require(uploads == before, "Sub-row scrolling recreated text textures.");
            scrollbar.CurrentYPosition = 3000; scrollbar.TriggerChanged();
            Require(composer.GetDynamicText("food-name-0").GetText() != "Food 0", "Scrolling does not update rows.");
            var elements = (IDictionary)typeof(GuiComposer).GetField("interactiveElements", All)!.GetValue(composer)!;
            var previews = elements.Values.OfType<GuiElement>().Where(e => e.GetType().Name == "NutritionItemSlot").ToArray();
            var visible = previews.First(e => e.Bounds.renderY > clip.renderY && e.Bounds.renderY + e.Bounds.OuterHeight < clip.renderY + clip.OuterHeight);
            var clickX = (int)(visible.Bounds.renderX + 20); var clickY = (int)(visible.Bounds.renderY + 20);
            openedLink = null;
            visible.OnMouseDownOnElement(api, new MouseEvent(clickX, clickY, EnumMouseButton.Right, 0));
            Require(tasks.Count == 0, "Right clicks must not open recipes or move inventory items.");
            visible.OnMouseDownOnElement(api, new MouseEvent(clickX, clickY, EnumMouseButton.Left, 0));
            Require(tasks.Count == 1 && openedLink is null, "Recipe navigation must be deferred until mouse dispatch finishes.");
            var slotIndex = Array.IndexOf(previews, visible);
            var foodName = composer.GetDynamicText("food-name-" + slotIndex).GetText();
            tasks.Dequeue()();
            Require(openedLink == "handbook://item-test:food-" + foodName.Substring("Food ".Length),
                "A scrolled food slot must open its own native recipe page.");
            Require(dialog.IsOpened(), "Opening a handbook recipe must leave the nutrition handbook open.");
            var clipped = previews.First(e => e.Bounds.renderY < clip.renderY);
            clipped.OnMouseDownOnElement(api, new MouseEvent((int)clipped.Bounds.renderX + 10, (int)clip.renderY - 2, EnumMouseButton.Left, 0));
            Require(tasks.Count == 0, "Clipped rows must not receive clicks outside the food list.");
            foreach (var element in previews) element.RenderInteractiveElements(.016f);
            Require(iconDraws > 0, "List icons never render.");
            var beforeFailure = iconDraws;
            failNextIcon = true;
            foreach (var element in previews) element.RenderInteractiveElements(.016f);
            Require(scissors == 0 && iconDraws > beforeFailure + 1,
                "One broken food renderer must release clipping and allow the next food icon to render.");
        }
        dialog.OnGuiClosed();
        Require(dialog.SingleComposer is null, "Closing the handbook retains GUI textures.");
        Console.WriteLine("PASS native Cairo nutrition window in 2 languages x 3 scales x 3 viewport sizes: external left tabs, all category clicks, search, nutrient filters, empty results, virtual scrolling, dynamic icons and disposal (mock GPU)");
    }
}
