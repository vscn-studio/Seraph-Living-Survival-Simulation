using Seraph.Network;
using Vintagestory.API.Client;
using Vintagestory.API.Config;

namespace Seraph.Client;

/// <summary>
/// Opens all physiology composers with the native C-key dialog. Vanilla character/skill
/// tabs retain their native input and lifecycle while physiology and environment
/// panels are rendered by this mod inside the same C-key dialog.
/// </summary>
internal sealed partial class CharacterPanelIntegration : IDisposable
{
    private readonly ICoreClientAPI api;
    private readonly SeraphClientBridge bridge;
    private readonly long listener;
    private readonly SlowCallbackProbe refreshProbe = new("client.character.refresh");
    private GuiDialogCharacterBase? dialog;
    private bool registered;
    private bool levelReady;
    private const string BodyKey = "vsslssmod-character-body";
    private const string EnvironmentKey = "vsslssmod-character-environment";
    private enum Panel { Status, BloodCirculation, DiseaseRecovery, EnergyBalance, Functions, Thermoregulation }
    private static string DetailsKey(Panel panel) => "vsslssmod-character-" + panel.ToString().ToLowerInvariant();
    private static readonly string[] OwnedKeys = [BodyKey, EnvironmentKey, DetailsKey(Panel.Status), DetailsKey(Panel.BloodCirculation),
        DetailsKey(Panel.DiseaseRecovery), DetailsKey(Panel.EnergyBalance), DetailsKey(Panel.Functions), DetailsKey(Panel.Thermoregulation)];
    private CairoFont? nativePanelFont;
    private readonly Dictionary<int, (string Line, string Hover)> environmentCache = [];
    private static readonly string[] EnvironmentKeys = ["airtemp", "humidity", "wind", "roomheat", "ventilation", "airpollution", "environmentalcontamination"];
    private (int Width, int Height, float Scale) viewport;
    private readonly NativeCharacterScrollbars scrollbars = new();
    private Seraph.Network.ServerSnapshot? lastPanelSnapshot;
    private bool lastPanelFresh;
    private long lastTrendRevision;
    private readonly Dictionary<string, GuiComposer> retainedPanels = [];
    private (int Width, int Height, float Scale, double MainWidth, double MainHeight, string Font, int Diseases)? panelLayout;
    private (double Left, double Top) nativePanelPosition;
    private readonly List<(int Index, Action<GuiComposer> Original, Action<GuiComposer> Wrapper)> nativeHandlers = [];

    public CharacterPanelIntegration(ICoreClientAPI api, SeraphClientBridge bridge)
    {
        this.api = api;
        this.bridge = bridge;
        api.Event.LevelFinalize += OnLevelReady;
        api.Event.LeaveWorld += OnLeaveWorld;
        listener = api.Event.RegisterGameTickListener(_ => { using var timing = refreshProbe.Measure(api); TryRegister(); RefreshCurrent(); RemoveDuplicateNutritionIfPresent(); }, 500);
    }

    private void OnLevelReady() => levelReady = true;
    private void OnLeaveWorld() { levelReady = false; DetachDialog(); pinnedPanels.Clear(); ClearCaches(); }

    private void TryRegister()
    {
        if (registered || !levelReady) return;
        dialog = api.Gui.LoadedGuis.Find(g => g is GuiDialogCharacterBase) as GuiDialogCharacterBase;
        if (dialog is null || dialog.Tabs.Count != dialog.RenderTabHandlers.Count) return;

        // Do not add physiology tabs or replace any native/third-party page handler.
        for (var i = 0; i < dialog.RenderTabHandlers.Count; i++)
        {
            var original = dialog.RenderTabHandlers[i];
            Action<GuiComposer> wrapper = composer => { original(composer); scrollbars.HideUnused(composer); };
            nativeHandlers.Add((i, original, wrapper));
            dialog.RenderTabHandlers[i] = wrapper;
        }
        dialog.ComposeExtraGuis += ComposeExtraPanels;
        dialog.OnClosed += OnCharacterClosed;
        registered = true;
        UpdatePinnedHud();
        if (((GuiDialog)dialog).IsOpened()) ComposeExtraPanels();
    }

    private void ClearCaches()
    {
        metricCache.Clear(); valueFontSizes.Clear(); metricValueRows.Clear();
        fittedValues.Clear(); metricHoverCache.Clear(); inlineHovers.Clear();
        nutritionCache.Clear(); nutrientHoverCache.Clear(); lastNutritionComposer = null;
        environmentCache.Clear();
        panelHoverSources.Clear(); barValues.Clear(); lastGutVisual = null;
        lastPanelSnapshot = null;
        panelLayout = null;
        nativePanelFont = null;
    }

    private void RemoveOwnedPanels()
    {
        if (dialog is null) return;
        var composers = ((GuiDialog)dialog).Composers;
        foreach (var key in OwnedKeys)
        {
            if (composers[key] is { } previous) { previous.Dispose(); composers.Remove(key); }
            if (retainedPanels.Remove(key, out var retained)) retained.Dispose();
        }
        ClearCaches();
    }

    private void ComposeExtraPanels()
    {
        RemoveDuplicateNutritionIfPresent();
        if (dialog is null) return;
        var main = ((GuiDialog)dialog).Composers["playercharacter"];
        if (main is null) return;
        // Vanilla clears its composer collection on every open/tab change. Our
        // closed panels stay owned here, including panels borrowed by pin HUDs.
        foreach (var (key, retained) in retainedPanels)
            ((GuiDialog)dialog).Composers[key] = retained;
        retainedPanels.Clear();
        var nativeEnvironment = ((GuiDialog)dialog).Composers["environment"];
        nativePanelFont = nativeEnvironment?.GetDynamicText("dyntext")?.Font.Clone() ?? nativePanelFont;
        var currentViewport = (api.Render.FrameWidth, api.Render.FrameHeight, RuntimeEnv.GUIScale);
        var scale = Math.Max(.1, RuntimeEnv.GUIScale);
        var screenWidth = api.Render.FrameWidth / scale;
        var screenHeight = api.Render.FrameHeight / scale;
        const double gap = 10, margin = 16, padding = 12;
        main.Bounds.MarkDirtyRecursive();
        main.Bounds.CalcWorldBounds();
        var nativeWidth = Math.Max(1, main.Bounds.OuterWidth / scale);
        var nativeHeight = Math.Max(1, main.Bounds.OuterHeight / scale);
        var font = AddedPanelFont();
        var layout = (currentViewport.FrameWidth, currentViewport.FrameHeight, RuntimeEnv.GUIScale, nativeWidth, nativeHeight,
            $"{font.Fontname}|{font.UnscaledFontsize}|{font.Slant}|{font.FontWeight}", Math.Max(1, DiseaseRows().Length));
        if (panelLayout == layout && OwnedKeys.Where(k => k != DetailsKey(Panel.Status))
            .All(k => ((GuiDialog)dialog).Composers[k] is not null))
        {
            if (nativeEnvironment is not null) nativeEnvironment.Enabled = false;
            PositionNativePanel(main.Bounds, nativePanelPosition.Left, nativePanelPosition.Top);
            RefreshPanels(); return;
        }
        viewport = currentViewport;
        var panelWidth = Math.Clamp((screenWidth - nativeWidth - 3 * gap - 2 * margin) / 3, 250, 360);
        var groupWidth = nativeWidth + gap + panelWidth + gap + panelWidth + gap + panelWidth;
        var groupLeft = Math.Max(margin, (screenWidth - groupWidth) / 2);
        // Vanilla has already composed LeftMiddle bounds, including cached margins.
        // Move the entire composer and its children; keep the model/slots' local positions.
        RemoveOwnedPanels();
        nativePanelFont = nativeEnvironment?.GetDynamicText("dyntext")?.Font.Clone();
        var heights = MeasurePanelHeights(panelWidth, MetricRowHeight(), EnvironmentLineHeight());
        heights = heights with { Functions = FunctionPanelHeight(nativeWidth, MetricRowHeight()) };
        diseaseVisibleRows = Math.Max(1, DiseaseRows().Length);
        var groupHeight = Math.Max(nativeHeight + heights.Environment + heights.Functions + 2 * gap,
            Math.Max(heights.Body,
                Math.Max(heights.BloodCirculation + gap + heights.Disease,
                    heights.EnergyBalance + gap + heights.Thermoregulation)));
        heights = heights with { Disease = ImmunePanelHeight(heights.Disease, MetricRowHeight(), diseaseVisibleRows,
            Math.Max(1, groupHeight - heights.BloodCirculation - gap)) };
        var groupTop = Math.Max(margin, (screenHeight - groupHeight) / 2);
        PositionNativePanel(main.Bounds, groupLeft, groupTop);
        nativePanelPosition = (groupLeft, groupTop);
        var environmentTop = groupTop + nativeHeight + gap;
        ReplaceEnvironmentPanel(nativeEnvironment, groupLeft, environmentTop,
            nativeWidth, heights.Environment);
        var x = groupLeft + nativeWidth + gap;
        var diseaseX = x + panelWidth + gap;
        var energyX = diseaseX + panelWidth + gap;

        // Preserve column order, align the top panels, and size each panel from its own rows.
        CreatePanel(BodyKey, "character-page-body", x, groupTop, panelWidth, heights.Body,
            (composer, _, width, _) => {
                var statusTop = ComposeNutritionModel(composer, width);
                ComposeMetrics(composer, width, Panel.Status, statusTop);
                ComposeNutritionDetails(composer, width, statusTop + StatusKeys.Length * MetricRowHeight() + 6);
            });
        CreatePanel(DetailsKey(Panel.BloodCirculation), "character-page-blood-circulation", diseaseX,
            groupTop, panelWidth, heights.BloodCirculation,
            (composer, _, width, _) => ComposeBloodCirculation(composer, width));
        CreateMetricPanel(Panel.DiseaseRecovery, "character-page-disease-recovery", diseaseX, groupTop + heights.BloodCirculation + gap,
            panelWidth, heights.Disease);
        CreateMetricPanel(Panel.Functions, "character-page-functions", groupLeft, environmentTop + heights.Environment + gap,
            nativeWidth, heights.Functions);
        CreatePanel(DetailsKey(Panel.EnergyBalance), "character-page-energy-balance", energyX,
            groupTop, panelWidth, heights.EnergyBalance,
            (composer, _, width, _) => ComposeEnergyBalance(composer, width));
        CreateMetricPanel(Panel.Thermoregulation, "character-page-thermoregulation", energyX,
            groupTop + heights.EnergyBalance + gap, panelWidth, heights.Thermoregulation);
        RefreshPanels();
        panelLayout = layout;

        void CreatePanel(string key, string title, double left, double y, double outerWidth, double outerHeight,
            Action<GuiComposer, ElementBounds, double, double> compose)
        {
            var width = Math.Max(1, outerWidth - 2 * padding);
            var innerHeight = Math.Max(1, outerHeight - 2 * padding);
            var background = ElementBounds.Fixed(0, 0, width, innerHeight).WithFixedPadding(padding);
            var bounds = ElementStdBounds.AutosizedMainDialog.WithAlignment(EnumDialogArea.None).WithFixedPosition(left, y);
            var composer = api.Gui.CreateCompo(key, bounds)
                .AddShadedDialogBG(background);
            AddPinnedTitleBar(composer, key, LocalizedText.Get("vsslssmod:" + title));
            composer.BeginChildElements(background);
            compose(composer, background, width, innerHeight);
            ((GuiDialog)dialog).Composers[key] = composer.EndChildElements().Compose();
        }

        void CreateMetricPanel(Panel panel, string title, double left, double y, double width, double panelHeight)
            => CreatePanel(DetailsKey(panel), title, left, y, width, panelHeight,
                (composer, _, innerWidth, innerHeight) => {
                    if (panel == Panel.DiseaseRecovery) ComposeImmuneSystem(composer, innerWidth, innerHeight);
                    else if (panel == Panel.Thermoregulation) ComposeMetricGrid(composer, innerWidth, panel);
                    else ComposeMetrics(composer, innerWidth, panel);
                });
    }

    internal readonly record struct PanelHeights(double Environment, double Body, double BloodCirculation, double Disease, double Functions, double EnergyBalance, double Thermoregulation);

    internal static PanelHeights MeasurePanelHeights(double panelWidth, double rowHeight, double environmentLineHeight)
    {
        const double padding = 24;
        var width = panelWidth - padding;
        double Rows(int count) => DetailsTop + count * rowHeight + DetailsBottom + padding;
        return new(DetailsTop + (EnvironmentKeys.Length + 1) / 2 * InlineRowHeight(environmentLineHeight) + DetailsBottom + padding,
            Rows(StatusKeys.Length) + DigestionHeight(width) + 10
                + 6 + (3 + DisplayNutrients.Length) * rowHeight,
            Rows(1) + DetailsTop + (BloodCirculationKeys.Length - 1) / 2 * InlineRowHeight(environmentLineHeight) + DetailsBottom + padding,
            DetailsTop + ImmuneSummaryHeight(environmentLineHeight) + 2 * rowHeight + DetailsBottom + padding,
            FunctionPanelHeight(panelWidth, rowHeight),
            Rows(3 + (EnergyDemandKeys.Length + 1) / 2),
            DetailsTop + (ThermoregulationKeys.Length + 1) / 2 * InlineRowHeight(environmentLineHeight) + DetailsBottom + padding);
    }

    internal static double ImmunePanelHeight(double minimumHeight, double rowHeight, int count, double columnHeight)
        => Math.Min(columnHeight, minimumHeight + (Math.Max(1, count) - 1) * rowHeight);

    private double EnvironmentLineHeight() => Math.Ceiling(AddedPanelFont().GetFontExtents().Height
        * AddedPanelFont().LineHeightMultiplier / Math.Max(.1, RuntimeEnv.GUIScale)) + 2;
    private static double InlineRowHeight(double lineHeight) => lineHeight + 6;
    private static double ImmuneSummaryHeight(double lineHeight) => Math.Ceiling(DiseaseRecoveryKeys.Length / 2d) * InlineRowHeight(lineHeight);
    private static double FunctionPanelHeight(double outerWidth, double rowHeight)
        => DetailsTop + Math.Ceiling(FunctionKeys.Length / (double)FunctionColumns(outerWidth - 24)) * rowHeight + DetailsBottom + 24;

    internal static void PositionNativePanel(ElementBounds bounds, double left, double top)
    {
        bounds.WithAlignment(EnumDialogArea.None).WithFixedAlignmentOffset(0, 0).WithFixedPosition(left, top);
        // EnumDialogArea.None does not clear margins computed by the former alignment.
        bounds.absMarginX = bounds.absMarginY = 0;
        bounds.MarkDirtyRecursive();
        bounds.CalcWorldBounds();
    }

    private void RemoveDuplicateNutritionIfPresent()
    {
        if (dialog is null) return;
        var composers = ((GuiDialog)dialog).Composers;
        if (composers["playerstats"] is { } duplicateNutrition)
        {
            duplicateNutrition.Dispose(); composers.Remove("playerstats");
        }
    }

    private void RefreshCurrent()
    {
        if (dialog is null || !registered || (!((GuiDialog)dialog).IsOpened() && pinnedPanels.Count == 0)) return;
        if (viewport != (api.Render.FrameWidth, api.Render.FrameHeight, RuntimeEnv.GUIScale)
            || diseaseVisibleRows != Math.Max(1, DiseaseRows().Length))
        {
            ComposeExtraPanels(); return;
        }
        if (ReferenceEquals(lastPanelSnapshot, bridge.Snapshot) && lastPanelFresh == bridge.HasFreshSnapshot && lastTrendRevision == bridge.Trends.History.Revision) return;
        RefreshPanels();
    }

    private void RefreshPanels()
    {
        if (dialog is null) return;
        var composers = ((GuiDialog)dialog).Composers;
        foreach (var panel in new[] { Panel.EnergyBalance, Panel.Functions, Panel.Thermoregulation })
            if (IsPanelVisible(DetailsKey(panel)) && OwnedComposer(DetailsKey(panel)) is { } details) RefreshMetrics(details, panel);
        if (IsPanelVisible(DetailsKey(Panel.BloodCirculation)) && OwnedComposer(DetailsKey(Panel.BloodCirculation)) is { } blood)
        {
            RefreshMetric(blood, Panel.BloodCirculation, "blood");
            RefreshInlineMetrics(blood, BloodCirculationKeys.Where(k => k != "blood").ToArray(), "blood-inline-", Metrics(Panel.BloodCirculation));
        }
        if (IsPanelVisible(DetailsKey(Panel.EnergyBalance)) && OwnedComposer(DetailsKey(Panel.EnergyBalance)) is { } energy)
            RefreshInlineMetrics(energy, EnergyDemandKeys, "energy-inline-", Metrics(Panel.EnergyBalance));
        if (IsPanelVisible(DetailsKey(Panel.DiseaseRecovery)) && OwnedComposer(DetailsKey(Panel.DiseaseRecovery)) is { } immune) RefreshImmuneSystem(immune);
        if (((GuiDialog)dialog).IsOpened()) RefreshCustomEnvironment(OwnedComposer(EnvironmentKey));
        if (IsPanelVisible(BodyKey) && OwnedComposer(BodyKey) is { } body)
        {
            RefreshMetrics(body, Panel.Status);
            RefreshNutrition(body, body);
        }
        lastPanelSnapshot = bridge.Snapshot;
        lastPanelFresh = bridge.HasFreshSnapshot;
        lastTrendRevision = bridge.Trends.History.Revision;
    }

    private void ReplaceEnvironmentPanel(GuiComposer? native, double left, double top,
        double width, double height)
    {
        if (dialog is null || native is null) return;
        var innerWidth = Math.Max(1, width - 24);
        var innerHeight = Math.Max(1, height - 24);
        var background = ElementBounds.Fixed(0, 0, innerWidth, innerHeight).WithFixedPadding(12);
        var bounds = ElementStdBounds.AutosizedMainDialog.WithAlignment(EnumDialogArea.None)
            .WithFixedPosition(left, top);
        // VSEssentials retains this composer and updates its dyntext on a timer.
        // Keep it alive for that integration, but hide it while our stable panel
        // renders the replacement data.
        native.Enabled = false;
        var environment = api.Gui.CreateCompo(EnvironmentKey, bounds)
            .AddShadedDialogBG(background)
            .AddDialogTitleBar(Lang.Get("vsslssmod:character-page-environment"), dialog.OnTitleBarClose)
            .BeginChildElements(background);
        // Each cell keeps its name and current value on the same baseline.
        var lineHeight = EnvironmentLineHeight();
        var rowHeight = InlineRowHeight(lineHeight);
        var rows = (EnvironmentKeys.Length + 1) / 2;
        var cellWidth = (innerWidth - 14) / 2;
        for (var i = 0; i < EnvironmentKeys.Length; i++)
        {
            var x = i / rows * (cellWidth + 14);
            var y = DetailsTop + i % rows * rowHeight;
            environment.AddDynamicText("", AddedPanelFont(), ElementBounds.Fixed(x, y, cellWidth, lineHeight), "environment-line-" + i);
            environment.AddCharacterPanelHoverText("", AddedPanelFont(), TooltipText.PanelWidth,
                ElementBounds.Fixed(x, y, cellWidth, rowHeight - 2), "environment-help-" + i);
        }
        ((GuiDialog)dialog).Composers[EnvironmentKey] = environment.EndChildElements().Compose();
        environmentCache.Clear();
    }

    private void RefreshCustomEnvironment(GuiComposer? composer)
    {
        if (composer is null || composer.GetDynamicText("environment-line-0") is null) return;
        var snapshot = bridge.Snapshot;
        var lines = EnvironmentKeys.Select(key => InlineMetricText(key, bridge.HasFreshSnapshot
            ? snapshot?.TemperatureMetrics.FirstOrDefault(m => m.Key == key) : null)).ToArray();
        for (var i = 0; i < lines.Length; i++)
        {
            var metric = snapshot?.TemperatureMetrics.FirstOrDefault(m => m.Key == EnvironmentKeys[i]);
            var hover = MetricHover(EnvironmentKeys[i], metric);
            var next = (lines[i], hover);
            if (environmentCache.TryGetValue(i, out var previous) && previous == next) continue;
            if (previous.Line != lines[i])
            {
                var element = composer.GetDynamicText("environment-line-" + i);
                var previousSize = element.Font.UnscaledFontsize;
                element.Font.UnscaledFontsize = AddedPanelFont().UnscaledFontsize;
                FitFont(element.Font, lines[i], element.Bounds);
                element.SetNewText(lines[i], forceRedraw: Math.Abs(previousSize - element.Font.UnscaledFontsize) > .01);
            }
            composer.SetCharacterPanelHoverText("environment-help-" + i, hover);
            environmentCache[i] = next;
        }
    }

    public void Dispose()
    {
        scrollbars.Dispose();
        api.Event.UnregisterGameTickListener(listener);
        api.Event.LevelFinalize -= OnLevelReady;
        api.Event.LeaveWorld -= OnLeaveWorld;
        DetachDialog();
    }

    private void DetachDialog()
    {
        pinnedHud?.TryClose();
        pinnedHud?.Dispose();
        pinnedHud = null;
        if (dialog is not null)
        {
            dialog.ComposeExtraGuis -= ComposeExtraPanels;
            dialog.OnClosed -= OnCharacterClosed;
            ((GuiDialog)dialog).Composers["environment"]?.Enabled = true;
            foreach (var (index, original, wrapper) in nativeHandlers)
                if (index < dialog.RenderTabHandlers.Count && dialog.RenderTabHandlers[index] == wrapper)
                    dialog.RenderTabHandlers[index] = original;
            nativeHandlers.Clear();
            RemoveOwnedPanels();
        }
        dialog = null;
        registered = false;
    }
}
