using System.Globalization;
using Seraph.Core.Framework;
using Vintagestory.API.Client;
using Vintagestory.API.Common;
using Vintagestory.API.Config;
using Vintagestory.GameContent;

namespace Seraph.Client;

/// <summary>Client catalogue using the same server-supplied portions as food consumption.</summary>
internal sealed class NutritionHandbookDialog : GuiDialog
{
    private sealed record Entry(DummySlot Slot, string Name, KeyValuePair<string, string>[] Details, double Water, double Energy, double[] Nutrients, bool[] Known);
    private static readonly string[] Categories = ["all", "water", "carbohydrate", "protein", "fat", "fiber", "electrolytes", "calcium", "iron", "zinc", "a", "c", "bcomplex"];
    private readonly ICoreClientAPI api;
    private readonly List<Entry> entries = [];
    private double guiScale;
    private int frameWidth, frameHeight;
    private string search = "", category = "all";
    private bool sortByContent;
    private GuiElementToggleButton? pressedSort;
    private double scroll, rowHeight;
    private int rowCount, renderedStart = -1;
    private ElementBounds? content, clip;
    private Entry[] displayed = [];

    internal NutritionHandbookDialog(ICoreClientAPI api, SeraphClientBridge bridge) : base(api)
    {
        this.api = api;
        BuildEntries(bridge);
        Compose();
    }

    public override string ToggleKeyCombinationCode => null!;
    public override bool PrefersUngrabbedMouse => true;
    public override bool UnregisterOnClose => true;
    // Keep this below the native handbook (0.2) so recipe pages remain on top.
    public override double DrawOrder => 0.15;
    public override void OnGuiClosed() { pressedSort = null; base.OnGuiClosed(); ClearComposers(); }

    private void BuildEntries(SeraphClientBridge bridge)
    {
        if (bridge.Foods is not { } foods || api.World.Player?.Entity is not { } player) return;
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var collectible in api.World.Items.Cast<CollectibleObject>().Concat(api.World.Blocks))
        {
            var code = collectible.Code?.ToString();
            if (string.IsNullOrEmpty(code) || !seen.Add(code)) continue;
            try
            {
                // Some modded food renderers require the attributes supplied by their handbook stack.
                var stack = PreviewStack(collectible, api);
                if (stack is null) continue;
                if (collectible.GetNutritionProperties(api.World, stack, player) is null && foods.Find(stack) is null) continue;
                if (!FoodKnowledgeCompatibility.IsKnownToPlayer(player, stack)) continue;
                var litres = BlockLiquidContainerBase.GetContainableProps(stack) is { ItemsPerLitre: > 0 };
                var dose = foods.Composition(stack, 1, litres, api.World, player);
                if (!dose.Mapped) continue;
                var values = new List<KeyValuePair<string, string>>
                {
                    new("", Lang.Get(litres ? "vsslssmod:nutrition-per-litre" : "vsslssmod:nutrition-per-portion")),
                    new("water", Lang.Get("vsslssmod:food-water-only", N(dose.WaterL * 1000))),
                    new("energy", Lang.Get("vsslssmod:food-energy-only", N(dose.EnergyKJ / 4.184)))
                };
                for (var i = 0; i < Nutrients.All.Length; i++)
                    if (Nutrients.All[i].Id is not ("d" or "k") && dose.Known[i] && dose.Nutrients[i] > 0)
                        values.Add(new(Nutrients.All[i].Id, Lang.Get("vsslssmod:nutrient-short-" + Nutrients.All[i].Id) + " " + N(dose.Nutrients[i]) + " " + Nutrients.All[i].Unit));
                var inventory = new DummyInventory(api);
                inventory.OnAcquireTransitionSpeed += (_, _, _) => 0;
                var slot = new DummySlot(stack, inventory); inventory[0] = slot;
                entries.Add(new(slot, stack.GetName(), values.ToArray(), dose.WaterL, dose.EnergyKJ,
                    (double[])dose.Nutrients.Clone(), (bool[])dose.Known.Clone()));
            }
            catch (Exception ex) { api.Logger.Debug("[vsslssmod] Nutrition handbook skipped {0}: {1}", code, ex.Message); }
        }
        entries.Sort((a, b) => StringComparer.CurrentCultureIgnoreCase.Compare(a.Name, b.Name));
    }

    private void Compose()
    {
        pressedSort = null;
        guiScale = Math.Max(.25, RuntimeEnv.GUIScale);
        frameWidth = api.Render.FrameWidth; frameHeight = api.Render.FrameHeight;
        var height = Math.Clamp(frameHeight / guiScale - 32, 300, 660);
        var tabHeight = Math.Min(25, (height - 58 - (Categories.Length - 1) * 2) / Categories.Length);
        var tabFont = CairoFont.WhiteDetailText().WithFontSize((float)Math.Min(14, tabHeight - 3));
        var tabs = Categories.Select(id => new GuiTab { Name = Lang.Get("vsslssmod:nutrition-category-" + id) }).ToArray();
        var tabWidth = Math.Ceiling(tabs.Max(tab => tabFont.GetTextExtents(tab.Name).Width) / guiScale) + 14;
        var width = Math.Clamp(frameWidth / guiScale - 32, 420, 920) - tabWidth;
        const double top = 82;
        var visibleHeight = height - top - 42;
        const double listX = 16;
        var listWidth = width - listX - 48;
        var textWidth = listWidth - 62;
        var font = CairoFont.WhiteSmallText();
        double TextHeight(string text) => api.Gui.Text.GetMultilineTextHeight(font, text, textWidth * guiScale) / guiScale;
        var nameHeight = Math.Max(24, entries.Select(e => TextHeight(e.Name)).DefaultIfEmpty(24).Max());
        double SummaryHeight(Entry entry)
        {
            using var measure = new GuiElementRichtext(api, SummaryComponents(entry, font, measure: true),
                ElementBounds.Fixed(0, 0, textWidth, 1).WithEmptyParent());
            measure.CalcHeightAndPositions();
            return measure.TotalHeight / guiScale + 4;
        }
        var nutritionHeight = Math.Max(44, entries.Select(SummaryHeight).DefaultIfEmpty(44).Max());
        rowHeight = Math.Max(66, nameHeight + nutritionHeight + 16);
        rowCount = (int)Math.Ceiling(visibleHeight / rowHeight) + 1;
        // Include the external tabs when centering the complete window on screen.
        var root = ElementStdBounds.AutosizedMainDialog.WithFixedSize(width, height)
            .WithFixedAlignmentOffset(tabWidth / 2, 0);
        var sortFont = CairoFont.WhiteDetailText();
        var sortWidth = Math.Max(64, Math.Ceiling(new[] { "content", "name" }.Max(key =>
            sortFont.GetTextExtents(Lang.Get("vsslssmod:nutrition-sort-" + key)).Width) / guiScale) + 14);
        var title = Lang.Get("vsslssmod:nutrition-handbook-title");
        var titleFont = CairoFont.WhiteSmallText();
        titleFont.UnscaledFontsize *= Math.Min(1, Math.Max(1, width - 82 - 2 * sortWidth - 24) * guiScale
            / Math.Max(1, titleFont.GetTextExtents(title).Width));
        var composer = api.Gui.CreateCompo("vsslssmod-nutrition-handbook", root)
            .AddShadedDialogBG(ElementBounds.Fill)
            .AddDialogTitleBar(title, () => TryClose(), titleFont)
            .AddTextInput(ElementBounds.Fixed(16, 42, width - 32, 30), value => { search = value.Trim(); Filter(); }, CairoFont.TextInput(), "nutrition-search");
        composer.AddToggleButton(Lang.Get("vsslssmod:nutrition-sort-content"), sortFont, _ => Sort(true),
                ElementBounds.Fixed(width - 82 - 2 * sortWidth - 4, 4, sortWidth, 22), "nutrition-sort-content")
            .AddToggleButton(Lang.Get("vsslssmod:nutrition-sort-name"), sortFont.Clone(), _ => Sort(false),
                ElementBounds.Fixed(width - 82 - sortWidth, 4, sortWidth, 22), "nutrition-sort-name");
        composer.AddInteractiveElement(new CategoryTabs(api, tabs, tabFont,
            ElementBounds.Fixed(-tabWidth, 42, tabWidth, height - 58), tabHeight,
            (index, _) => { category = Categories[index]; Filter(); }), "nutrition-categories");
        clip = ElementBounds.Fixed(listX, top, listWidth, visibleHeight);
        content = ElementBounds.Fixed(0, 0, listWidth, visibleHeight);
        composer.BeginClip(clip).BeginChildElements(content);
        for (var i = 0; i < rowCount; i++)
        {
            var rowIndex = i;
            composer.AddInteractiveElement(new NutritionItemSlot(api, ElementBounds.Fixed(2, i * rowHeight + 5, 48, 48),
                () => EntryAtRow(rowIndex)?.Slot, OpenRecipe), "food-slot-" + i);
            composer.AddDynamicText("", font, ElementBounds.Fixed(60, i * rowHeight + 3, textWidth, nameHeight), "food-name-" + i);
            composer.AddRichtext(Array.Empty<RichTextComponentBase>(), ElementBounds.Fixed(60, i * rowHeight + nameHeight + 7, textWidth, nutritionHeight), "food-nutrients-" + i);
        }
        composer.EndChildElements().EndClip()
            .AddVerticalScrollbar(value => { scroll = value / guiScale; RefreshRows(); },
                ElementBounds.Fixed(listX + listWidth + 6, top, 14, visibleHeight), "nutrition-scroll")
            .AddDynamicText("", font, ElementBounds.Fixed(listX, height - 35, listWidth, 25), "nutrition-count");
        SingleComposer = composer.Compose();
        SingleComposer.GetTextInput("nutrition-search").SetPlaceHolderText(Lang.Get("vsslssmod:nutrition-handbook-search"));
        SingleComposer.GetTextInput("nutrition-search").SetValue(search);
        Filter();
    }

    private void Filter()
    {
        if (SingleComposer is null || clip is null) return;
        SingleComposer.GetToggleButton("nutrition-sort-content").SetValue(sortByContent);
        SingleComposer.GetToggleButton("nutrition-sort-name").SetValue(!sortByContent);
        var index = Array.FindIndex(Nutrients.All, n => n.Id == category);
        var matches = entries.Where(entry =>
            (search.Length == 0 || entry.Name.Contains(search, StringComparison.CurrentCultureIgnoreCase)
                || entry.Slot.Itemstack!.Collectible.Code.ToString().Contains(search, StringComparison.OrdinalIgnoreCase))
            && (category == "all" || category == "water" && entry.Water > 0
                || index >= 0 && entry.Known[index] && entry.Nutrients[index] > 0));
        displayed = (sortByContent
            ? matches.OrderByDescending(entry => category == "water" ? entry.Water
                : index >= 0 ? entry.Nutrients[index] : entry.Energy).ThenBy(entry => entry.Name, StringComparer.CurrentCultureIgnoreCase)
            : matches.OrderBy(entry => entry.Name, StringComparer.CurrentCultureIgnoreCase)).ToArray();
        scroll = 0; renderedStart = -1;
        SingleComposer.GetVerticalTab("nutrition-categories").SetValue(Array.IndexOf(Categories, category), false);
        SingleComposer.GetDynamicText("nutrition-count").SetNewText(Lang.Get("vsslssmod:nutrition-count", displayed.Length));
        var scrollbar = SingleComposer.GetScrollbar("nutrition-scroll");
        scrollbar.CurrentYPosition = 0;
        // SetHeights invokes the scroll callback. Never set scrollbar heights from RefreshRows.
        scrollbar.SetHeights((float)(clip.fixedHeight * guiScale), (float)(Math.Max(clip.fixedHeight, displayed.Length * rowHeight) * guiScale));
        RefreshRows();
    }

    private void Sort(bool byContent) { sortByContent = byContent; Filter(); }

    public override void OnMouseDown(MouseEvent args)
    {
        // These controls share the title bar; consume their clicks before movable-window dragging.
        foreach (var key in new[] { "nutrition-sort-content", "nutrition-sort-name" })
        {
            var button = SingleComposer?.GetToggleButton(key);
            if (button?.IsPositionInside(args.X, args.Y) != true) continue;
            pressedSort = button;
            button.OnMouseDownOnElement(api, args);
            args.Handled = true;
            return;
        }
        base.OnMouseDown(args);
    }

    public override void OnMouseUp(MouseEvent args)
    {
        if (pressedSort is { } button)
        {
            pressedSort = null;
            button.OnMouseUp(api, args);
            args.Handled = true;
            return;
        }
        base.OnMouseUp(args);
    }

    private void RefreshRows()
    {
        if (SingleComposer is null || clip is null || content is null) return;
        scroll = Math.Clamp(scroll, 0, Math.Max(0, displayed.Length * rowHeight - clip.fixedHeight));
        var start = (int)(scroll / rowHeight);
        content.fixedY = -(scroll - start * rowHeight);
        content.CalcWorldBounds();
        // Only a viewport of rows owns text textures; sub-row scrolling just moves their bounds.
        if (renderedStart == start) return;
        renderedStart = start;
        for (var i = 0; i < rowCount; i++)
        {
            var entry = EntryAtRow(i);
            SingleComposer.GetDynamicText("food-name-" + i).SetNewText(entry?.Name ?? "");
            SingleComposer.GetRichtext("food-nutrients-" + i).SetNewText(entry is null ? [] : SummaryComponents(entry, CairoFont.WhiteSmallText()));
        }
    }

    // Null/empty means excluded from the native handbook (including obsolete mod items).
    internal static ItemStack? PreviewStack(CollectibleObject collectible, ICoreClientAPI api)
        => collectible.GetHandBookStacks(api)?.FirstOrDefault()?.Clone();

    private RichTextComponentBase[] SummaryComponents(Entry entry, CairoFont font, bool measure = false)
    {
        var result = new List<RichTextComponentBase>();
        foreach (var detail in entry.Details)
        {
            if (result.Count > 0) result.Add(new RichTextComponent(api, " · ", font));
            var valueFont = font;
            if (measure || detail.Key == category)
            {
                valueFont = font.Clone();
                valueFont.FontWeight = Cairo.FontWeight.Bold;
                valueFont.Color = [1, .25, .25, 1];
            }
            result.Add(new RichTextComponent(api, detail.Value, valueFont));
        }
        return result.ToArray();
    }

    private void OpenRecipe(ItemStack stack)
    {
        if (api.LinkProtocols?.TryGetValue("handbook", out var open) != true || open is null) return;
        var page = stack.Collectible.GetCollectibleInterface<IHandBookPageCodeProvider>()?.HandbookPageCodeForStack(api.World, stack)
            ?? GuiHandbookItemStackPage.PageCodeForStack(stack);
        // Defer navigation until the mouse event has finished traversing this composer.
        api.Event.EnqueueMainThreadTask(() =>
        {
            open(new LinkTextComponent("handbook://" + page));
        }, "vsslssmod-nutrition-recipe");
    }

    private Entry? EntryAtRow(int row)
    {
        var index = (int)(scroll / rowHeight) + row;
        return index >= 0 && index < displayed.Length ? displayed[index] : null;
    }

    public override void OnRenderGUI(float deltaTime)
    {
        if (!IsOpened()) return;
        if (frameWidth != api.Render.FrameWidth || frameHeight != api.Render.FrameHeight || Math.Abs(guiScale - RuntimeEnv.GUIScale) > .001)
        { ClearComposers(); Compose(); }
        base.OnRenderGUI(deltaTime);
    }

    private static string N(double value) => value.ToString("0.##", CultureInfo.InvariantCulture);

    private sealed class CategoryTabs : GuiElementVerticalTabs
    {
        internal CategoryTabs(ICoreClientAPI api, GuiTab[] tabs, CairoFont font, ElementBounds bounds,
            double height, Action<int, GuiTab> clicked) : base(api, tabs, font, font.Clone(), bounds, clicked)
        {
            unscaledTabHeight = height;
            unscaledTabSpacing = 2;
        }
    }
}
