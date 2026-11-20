using System.Globalization;
using Cairo;
using Seraph.Network;
using Vintagestory.API.Client;
using Vintagestory.API.Config;

namespace Seraph.Client;

internal sealed partial class CharacterPanelIntegration
{
    private readonly Dictionary<string, (string Text, double Value, double Minimum, double Maximum, string Hover)> metricCache = [];
    private readonly Dictionary<string, float> valueFontSizes = [];
    private readonly Dictionary<string, (double Top, double Height)> metricValueRows = [];
    private readonly Dictionary<string, (GuiComposer Composer, string Text, float Scale, int Direction)> fittedValues = [];
    private readonly Dictionary<string, string> metricHoverCache = [];
    private readonly Dictionary<string, string> inlineHovers = [];
    private readonly Dictionary<string, (GuiComposer Composer, string Source, float Scale)> panelHoverSources = [];
    private readonly Dictionary<GuiElementStatbar, float> barValues = [];
    private const double DetailsTop = 34, DetailsBottom = 10;
    private static readonly string[] StatusKeys = ["bodywater", "musclemass"];
    private static readonly string[] BloodCirculationKeys = ["blood", "heartrate", "breathrate", "bleeding", "bloodwaste", "bloodtoxicity", "bloodregen", "bloodfiltration",
        "bloodclearance", "bloodglucose", "lactate"];
    private static readonly string[] EnergyStatusKeys = ["stamina", "fatigue", "sleeppressure"];
    private static readonly string[] EnergyDemandKeys = ["energybasal", "energycold", "energyactivity", "energyrecovery", "energytotal"];
    private static readonly string[] FunctionKeys = ["consciousness", "mobility", "respiratory", "circulation", "digestion", "immunity", "manipulation", "jump", "filtration", "dodge", "dodgefront", "dodgeback", "fatprotection", "musclemelee", "healingrate", "darkadaptation"];
    private static readonly string[] DiseaseRecoveryKeys = ["dirtiness", "immune", "immunesystem", "healingrate", "whitecells", "pain"];
    private static readonly string[] ThermoregulationKeys = ["coretemp", "skintemp", "feelslike", "radianttemp", "feversetpoint", "clothing", "wetness", "heatproduction", "heatloss", "heatbalance", "sweat", "shivering", "sweatdebt", "heatadaptation", "coldadaptation"];
    private string[] MetricKeys(Panel panel) => panel switch {
        Panel.Status => StatusKeys, Panel.BloodCirculation => BloodCirculationKeys, Panel.DiseaseRecovery => DiseaseRecoveryKeys,
        Panel.EnergyBalance => EnergyStatusKeys, Panel.Thermoregulation => ThermoregulationKeys, _ => FunctionKeys
    };
    private static int FunctionColumns(double width) => width >= 260 ? 2 : 1;
    private double MetricRowHeight() => Math.Max(30, EnvironmentLineHeight() + 6);
    private PanelMetric[] Metrics(Panel panel) => !bridge.HasFreshSnapshot || bridge.Snapshot is not { } snapshot ? []
        : panel is Panel.Status or Panel.BloodCirculation or Panel.EnergyBalance ? snapshot.StatusMetrics
            : panel == Panel.DiseaseRecovery ? snapshot.RecoveryMetrics.Concat(snapshot.StatusMetrics.Where(m => m.Key is "whitecells" or "pain")).ToArray()
            : panel == Panel.Thermoregulation ? snapshot.TemperatureMetrics : snapshot.FunctionMetrics;

    private PanelMetric? Metric(Panel panel, string key) => key == "darkadaptation"
        ? bridge.HasFreshSnapshot ? bridge.DarkAdaptationMetric : null
        : Metrics(panel).FirstOrDefault(m => m.Key == key);

    private void ComposeMetrics(GuiComposer composer, double width, Panel panel, double topOffset = DetailsTop)
    {
        var keys = MetricKeys(panel);
        var columns = panel == Panel.Functions ? FunctionColumns(width) : 1;
        var rows = (int)Math.Ceiling(keys.Length / (double)columns);
        var rowHeight = MetricRowHeight();
        var cellWidth = (width - (columns - 1) * 14) / columns;
        // Reserve enough width for long Chinese labels before fitting the font.
        var labelWidth = Math.Min(columns == 1 ? 170 : 124, cellWidth * (columns == 1 ? .5 : .58));
        for (var i = 0; i < keys.Length; i++)
        {
            var key = keys[i];
            var metric = Metric(panel, key);
            var baseFont = (float)(nativePanelFont?.UnscaledFontsize ?? GuiStyle.SmallFontSize);
            if (panel == Panel.Functions)
            {
                var id = "metric-" + panel + "-" + key;
                var bounds = ElementBounds.Fixed((i / rows) * (cellWidth + 14), topOffset + (i % rows) * rowHeight,
                    cellWidth, rowHeight - 4);
                valueFontSizes[id + "-value"] = baseFont;
                composer.AddDynamicText("", AddedPanelFont(), bounds, id + "-value")
                    .AddCharacterPanelHoverText(MetricHover(key, metric), AddedPanelFont(), TooltipText.PanelWidth, bounds.FlatCopy(), id + "-help");
                continue;
            }
            AddMetricRow(composer, "metric-" + panel + "-" + key, LocalizedText.Get("vsslssmod:metric-" + key),
                MetricHover(key, metric), (i / rows) * (cellWidth + 14), topOffset + (i % rows) * rowHeight,
                cellWidth, labelWidth, rowHeight, baseFont, MetricColor(key), panel == Panel.Functions ? false : metric?.Bar ?? IsBarKey(key));
        }
    }

    private void ComposeBloodCirculation(GuiComposer composer, double width)
    {
        var row = MetricRowHeight();
        var metrics = Metrics(Panel.BloodCirculation);
        var blood = metrics.FirstOrDefault(m => m.Key == "blood");
        AddMetricRow(composer, "metric-" + Panel.BloodCirculation + "-blood", LocalizedText.Get("vsslssmod:metric-blood"),
            MetricHover("blood", blood), 0, DetailsTop, width, Math.Min(132, width * .42), row,
            (float)AddedPanelFont().UnscaledFontsize, MetricColor("blood"), true);
        var keys = BloodCirculationKeys.Where(k => k != "blood").ToArray();
        var line = EnvironmentLineHeight();
        var cellWidth = (width - 14) / 2;
        var rows = (keys.Length + 1) / 2;
        for (var i = 0; i < keys.Length; i++)
        {
            var key = keys[i];
            var id = "blood-inline-" + key;
            var bounds = ElementBounds.Fixed(i / rows * (cellWidth + 14), DetailsTop + row + 4 + i % rows * InlineRowHeight(line), cellWidth, line);
            valueFontSizes[id] = (float)AddedPanelFont().UnscaledFontsize;
            composer.AddDynamicText("", AddedPanelFont(), bounds, id);
            composer.AddCharacterPanelHoverText("", AddedPanelFont(), TooltipText.PanelWidth, bounds.FlatCopy(), id + "-help");
        }
    }

    private void ComposeEnergyBalance(GuiComposer composer, double width)
    {
        ComposeMetrics(composer, width, Panel.EnergyBalance);
        var line = EnvironmentLineHeight();
        var top = DetailsTop + EnergyStatusKeys.Length * MetricRowHeight() + 8;
        var cellWidth = (width - 14) / 2;
        var rows = (EnergyDemandKeys.Length + 1) / 2;
        for (var i = 0; i < EnergyDemandKeys.Length; i++)
        {
            var key = EnergyDemandKeys[i];
            var id = "energy-inline-" + key;
            var bounds = ElementBounds.Fixed(i / rows * (cellWidth + 14), top + i % rows * InlineRowHeight(line), cellWidth, line);
            valueFontSizes[id] = (float)AddedPanelFont().UnscaledFontsize;
            composer.AddDynamicText("", AddedPanelFont(), bounds, id);
            composer.AddCharacterPanelHoverText("", AddedPanelFont(), TooltipText.PanelWidth, bounds.FlatCopy(), id + "-help");
        }
    }

    private void RefreshInlineMetrics(GuiComposer composer, string[] keys, string prefix, PanelMetric[] metrics)
    {
        foreach (var key in keys)
        {
            var metric = metrics.FirstOrDefault(m => m.Key == key);
            var id = prefix + key;
            var text = InlineMetricText(key, metric);
            SetFittedValue(composer, id, text, key);
            var hover = MetricHover(key, metric);
            if (!inlineHovers.TryGetValue(id, out var previous) || previous != hover)
            {
                composer.SetCharacterPanelHoverText(id + "-help", hover);
                inlineHovers[id] = hover;
            }
        }
    }

    private static bool IsBarKey(string key) => key is "health" or "stamina" or "fatigue" or "pain" or "bodywater"
        or "blood" or "oxygen" or "bowel" or "sleeppressure" or "musclemass" or "spo2" or "whitecells" or "ketosis";

    private static double[] MetricColor(string key) => key switch {
        "health" or "pain" or "blood" => GuiStyle.HealthBarColor,
        "inflammation" => [.85, .36, .24],
        "bodywater" or "oxygen" or "wetness" or "humidity" => [.2, .62, .9],
        "stamina" => [.2, .75, .35], "coretemp" or "skintemp" => [.85, .5, .25],
        _ => [.7, .57, .28]
    };

    private CairoFont AddedPanelFont() => (nativePanelFont ?? CairoFont.WhiteSmallText()).Clone();

    private void AddMetricRow(GuiComposer composer, string id, string label, string hover, double x, double y,
        double width, double labelWidth, double rowHeight, float fontSize, double[] color, bool bar)
    {
        // Keep the row spacing; the bar and the visible glyphs share its center.
        var contentHeight = Math.Max(1, rowHeight - 4);
        var labelBounds = ElementBounds.Fixed(x, y, Math.Max(1, labelWidth - 4), contentHeight);
        var helpBounds = labelBounds.FlatCopy();
        var labelFont = AddedPanelFont();
        FitFont(labelFont, label, labelBounds);
        CenterMetricText(labelFont, label, labelBounds, y, contentHeight);
        composer.AddStaticText(label, labelFont, labelBounds)
            .AddCharacterPanelHoverText(hover, AddedPanelFont(), TooltipText.PanelWidth, helpBounds, id + "-help");
        var valueWidth = width - labelWidth;
        ElementBounds valueBounds;
        if (bar)
        {
            var barHeight = Math.Max(1, Math.Min(20, contentHeight - 2));
            var barBounds = ElementBounds.Fixed(x + labelWidth, y + (contentHeight - barHeight) / 2,
                Math.Max(1, valueWidth - 2), barHeight);
            composer.AddStatbar(barBounds, color, id + "-bar");
            var statbar = composer.GetStatbar(id + "-bar");
            statbar.SetLineInterval(float.MaxValue);
            statbar.ShowValueOnHover = false;
            valueBounds = ElementBounds.Fixed(x + labelWidth, y, Math.Max(1, valueWidth - 2), contentHeight);
        }
        else
        {
            valueBounds = ElementBounds.Fixed(x + labelWidth + 2, y, Math.Max(1, valueWidth - 2), contentHeight);
        }
        valueFontSizes[id + "-value"] = fontSize;
        metricValueRows[id + "-value"] = (y, contentHeight);
        // Native dynamic text only centers horizontally. Reposition its glyphs
        // after font fitting as well, including narrow values and trend markers.
        var valueFont = AddedPanelFont().WithWeight(FontWeight.Bold)
            .WithOrientation(EnumTextOrientation.Center);
        composer.AddDynamicText("", valueFont, valueBounds, id + "-value");
    }

    private static void CenterMetricText(CairoFont font, string text, ElementBounds bounds, double top, double height)
    {
        if (text.Length == 0) { bounds.fixedY = top; return; }
        var ink = font.GetTextExtents(text);
        // TextDrawUtil places the baseline at the integer font ascent. Font
        // line boxes include extra descent/leading, so centering those is not
        // enough to center the visible numbers and Chinese/English labels.
        var inkCenter = Math.Floor(font.GetFontExtents().Ascent) + ink.YBearing + ink.Height / 2;
        bounds.fixedY = top + height / 2 - inkCenter / Math.Max(.1, RuntimeEnv.GUIScale);
    }

    private static void FitFont(CairoFont font, string text, ElementBounds bounds)
    {
        if (text.Length == 0) return;
        var extents = font.GetTextExtents(text);
        var scale = RuntimeEnv.GUIScale;
        var ratio = Math.Min(1, Math.Min((bounds.fixedWidth * scale - 2) / Math.Max(1, Math.Max(extents.Width, extents.XAdvance)),
            (bounds.fixedHeight * scale - 2) / Math.Max(1, font.GetFontExtents().Height * font.LineHeightMultiplier)));
        font.UnscaledFontsize *= Math.Max(.01, ratio);
        // Native line wrapping measures word runs separately and can round wider
        // than Cairo's whole-string extents. Validate against the actual lineizer.
        var draw = new TextDrawUtil();
        for (var i = 0; i < 12 && !text.Contains('\n') && draw.Lineize(font, text, bounds.fixedWidth * scale).Length > 1; i++)
            font.UnscaledFontsize *= .97;
    }

    private void SetFittedValue(GuiComposer composer, string id, string text, string? key = null)
    {
        var direction = key is null ? 0 : bridge.Trends.Direction(key);
        if (key is not null && text.Length > 0) text += " " + MetricTrendDisplay.Symbol(direction);
        var next = (composer, text, RuntimeEnv.GUIScale, direction);
        if (fittedValues.TryGetValue(id, out var previous) && previous == next) return;
        var element = composer.GetDynamicText(id);
        var previousSize = element.Font.UnscaledFontsize;
        element.Font.UnscaledFontsize = valueFontSizes[id];
        FitFont(element.Font, text, element.Bounds);
        if (metricValueRows.TryGetValue(id, out var row))
            CenterMetricText(element.Font, text, element.Bounds, row.Top, row.Height);
        element.SetNewText(text, forceRedraw: previous.Direction != direction || Math.Abs(previousSize - element.Font.UnscaledFontsize) > .01);
        fittedValues[id] = next;
    }

    private void SetBarRange(GuiElementStatbar bar, double value, double minimum, double maximum)
    {
        // VS 1.22.7 ComposeValueOverlay uses value/(max-min), without subtracting min.
        // Project only the fill to 0..1; the label retains the actual physical value/unit.
        var fraction = double.IsFinite(value) && double.IsFinite(minimum) && double.IsFinite(maximum) && maximum > minimum
            ? Math.Clamp((value - minimum) / (maximum - minimum), 0, 1) : 0;
        // SetValues always recreates native Cairo overlays, even for identical
        // values. Refresh only when the fill changes by a physical screen pixel.
        var pixels = Math.Max(1, Math.Round(bar.Bounds.InnerWidth));
        var fill = (float)(Math.Round(fraction * pixels) / pixels);
        if (barValues.TryGetValue(bar, out var previous) && previous == fill) return;
        bar.SetValues(fill, 0, 1);
        barValues[bar] = fill;
    }

    private void SetPanelHoverText(GuiComposer composer, string id, string source)
    {
        var next = (composer, source, RuntimeEnv.GUIScale);
        if (panelHoverSources.TryGetValue(id, out var previous) && previous == next) return;
        composer.SetCharacterPanelHoverText(id, source);
        panelHoverSources[id] = next;
    }

    private string MetricHover(string key, PanelMetric? metric)
    {
        var source = TooltipText.Help("metric-help-" + key, "metric-" + key)
            + (metric is { Available: true, Detail.Length: > 0 } ? "\n" + metric.Detail : "")
            + (metric is null || !metric.Available ? "\n" + (!bridge.HasFreshSnapshot ? bridge.ConnectionMessage : LocalizedText.Get("vsslssmod:metric-unavailable")) : "");
        if (metricHoverCache.TryGetValue(key, out var previous) && previous == source) return previous;
        metricHoverCache[key] = source;
        return source;
    }

    private void RefreshMetrics(GuiComposer composer, Panel panel)
    {
        foreach (var key in MetricKeys(panel)) RefreshMetric(composer, panel, key);
    }

    private void RefreshMetric(GuiComposer composer, Panel panel, string key)
    {
        var metric = Metric(panel, key);
        var text = metric is not { Available: true } ? "" : metric.Text.Length > 0 ? metric.Text
            : (metric.Bar && panel == Panel.Status ? $"{N(metric.Value)}/{N(metric.Maximum)}" : N(metric.Value))
                + (metric.Unit.Length > 0 ? metric.Unit == "%" ? metric.Unit : " " + metric.Unit : "");
        if (panel is Panel.Thermoregulation or Panel.DiseaseRecovery or Panel.BloodCirculation) text = InlineMetricText(key, metric);
        if (panel == Panel.Functions) text = FunctionMetricText(key, metric);
        var value = metric is { Available: true } ? metric.Value : 0;
        var max = metric?.Maximum ?? 1;
        var min = metric?.Minimum ?? 0;
        var hover = MetricHover(key, metric);
        var next = (text, value, min, max, hover);
        var id = "metric-" + panel + "-" + key;
        SetFittedValue(composer, id + "-value", text, key);
        if (metricCache.TryGetValue(id, out var previous) && previous == next) return;
        if (previous.Hover != hover) composer.SetCharacterPanelHoverText(id + "-help", hover);
        if (composer.GetElement(id + "-bar") is GuiElementStatbar statbar) SetBarRange(statbar, value, min, max);
        metricCache[id] = next;
    }

    internal static string FunctionMetricText(string key, PanelMetric? metric)
        => LocalizedText.Get("vsslssmod:metric-" + key) + " "
            + (metric is not { Available: true } ? LocalizedText.Get("vsslssmod:character-value-unknown")
                : metric.Text.Length > 0 ? metric.Text : metric.Value.ToString("0", CultureInfo.InvariantCulture) + "%");

    internal static string InlineMetricText(string key, PanelMetric? metric)
    {
        var unit = metric is null || metric.Unit.Length == 0 ? "" : key switch {
            "heatproduction" or "heatloss" or "heatbalance" or "shivering" or "energyactivity" or "energybasal"
                or "energycold" or "energyrecovery" or "energyhealing" or "energybloodregen" or "energytotal" => "kcal/h",
            "sweat" => "L/h",
            "bloodwaste" => "mL", "bleeding" => "mL/min",
            "bloodregen" or "bloodclearance" or "renalfiltration" => "mL/h",
            _ => metric.Unit
        };
        var value = metric is not { Available: true } ? Lang.Get("vsslssmod:character-value-unknown")
            : metric.Text.Length > 0 ? metric.Text
            : metric.Value.ToString("0.#", CultureInfo.InvariantCulture)
                + (unit.Length == 0 ? "" : unit is "%" or "°C" ? unit : " " + unit);
        return LocalizedText.Get("vsslssmod:metric-" + key) + " " + value;
    }

    private static string N(double value) => value.ToString("0.##", CultureInfo.InvariantCulture);
}
