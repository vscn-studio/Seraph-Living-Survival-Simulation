using Seraph.Network;
using Vintagestory.API.Client;
using Vintagestory.API.Config;

namespace Seraph.Client;

internal sealed partial class CharacterPanelIntegration
{
    private const int MaximumImmuneRows = 80;
    private int diseaseVisibleRows = 1;
    private int immuneContentRows = -1;
    private int composedImmuneRows;
    private double immuneScrollOffset;
    private ElementBounds? immuneContent;
    private ElementBounds? immuneClip;

    private PanelMetric[] DiseaseRows() => Metrics(Panel.DiseaseRecovery)
        .Where(m => m.Key.StartsWith("disease-", StringComparison.Ordinal) || m.Key.StartsWith("wound-", StringComparison.Ordinal)
            || m.Key.StartsWith("syndrome-", StringComparison.Ordinal)).Take(MaximumImmuneRows).ToArray();

    // Dynamic labels as well as values move with the clipped content; static composer text would stay behind.
    private void ComposeMetricGrid(GuiComposer composer, double width, Panel panel, double top = DetailsTop)
    {
        var keys = MetricKeys(panel);
        var line = EnvironmentLineHeight();
        var row = InlineRowHeight(line);
        var columns = 2;
        var rows = (keys.Length + columns - 1) / columns;
        var cellWidth = (width - 14 * (columns - 1)) / columns;
        for (var i = 0; i < keys.Length; i++)
        {
            var key = keys[i];
            var id = "metric-" + panel + "-" + key;
            var x = i / rows * (cellWidth + 14);
            var y = top + i % rows * row;
            valueFontSizes[id + "-value"] = (float)AddedPanelFont().UnscaledFontsize;
            composer.AddDynamicText("", AddedPanelFont(), ElementBounds.Fixed(x, y, cellWidth, line), id + "-value");
            composer.AddCharacterPanelHoverText("", AddedPanelFont(), TooltipText.PanelWidth,
                ElementBounds.Fixed(x, y, cellWidth, row - 2), id + "-help");
        }
    }

    private void ComposeImmuneSystem(GuiComposer composer, double width, double height)
    {
        var visibleHeight = height - DetailsTop - DetailsBottom;
        var contentWidth = width - 22;
        immuneClip = ElementBounds.Fixed(0, DetailsTop, contentWidth, visibleHeight);
        immuneContent = ElementBounds.Fixed(0, 0, contentWidth, visibleHeight);
        immuneContentRows = -1;
        composer.BeginClip(immuneClip).BeginChildElements(immuneContent);
        ComposeMetricGrid(composer, contentWidth, Panel.DiseaseRecovery, 0);
        var diseaseTop = ImmuneSummaryHeight(EnvironmentLineHeight());
        composer.AddDynamicText(Lang.Get("vsslssmod:immune-disease-list"), AddedPanelFont(),
            ElementBounds.Fixed(0, diseaseTop, contentWidth, MetricRowHeight()), "immune-disease-title");
        // Row-count changes already trigger composition; do not allocate eighty
        // Cairo text/hover elements when the player has only one visible row.
        composedImmuneRows = Math.Max(1, DiseaseRows().Length);
        for (var i = 0; i < composedImmuneRows; i++)
        {
            var id = "immune-disease-" + i;
            var bounds = ElementBounds.Fixed(0, diseaseTop + (i + 1) * MetricRowHeight(), contentWidth, MetricRowHeight() - 4);
            composer.AddDynamicText("", AddedPanelFont(), bounds, id);
            valueFontSizes[id] = (float)AddedPanelFont().UnscaledFontsize;
            composer.AddCharacterPanelHoverText("", AddedPanelFont(), TooltipText.PanelWidth, bounds.FlatCopy(), id + "-help");
        }
        composer.EndChildElements().EndClip();
        var content = immuneContent;
        composer.AddVerticalScrollbar(value => {
            immuneScrollOffset = value / Math.Max(.1, RuntimeEnv.GUIScale);
            PositionImmuneContent(content, immuneScrollOffset);
        }, ElementBounds.Fixed(width - 16, DetailsTop, 16, visibleHeight), "immune-scroll");
    }

    internal static void PositionImmuneContent(ElementBounds content, double unscaledOffset)
    {
        content.fixedY = -Math.Max(0, unscaledOffset);
        content.MarkDirtyRecursive(); content.CalcWorldBounds();
    }

    private void RefreshImmuneSystem(GuiComposer composer)
    {
        RefreshMetrics(composer, Panel.DiseaseRecovery);
        var diseases = DiseaseRows();
        for (var i = 0; i < composedImmuneRows; i++)
        {
            var disease = i < diseases.Length ? diseases[i] : null;
            var text = disease?.Text ?? (i == 0 ? Lang.Get("vsslssmod:effect-none") : "");
            if (!bridge.HasFreshSnapshot && i == 0) text = Lang.Get("vsslssmod:character-value-unknown");
            var id = "immune-disease-" + i;
            // SetNewText caches unchanged values; fitting resets from the native font each time.
            SetFittedValue(composer, id, text);
            SetPanelHoverText(composer, id + "-help", disease?.Detail ?? "");
        }
        var count = Math.Max(1, diseases.Length);
        if (count == immuneContentRows || immuneContent is null || immuneClip is null) return;
        immuneContentRows = count;
        var total = ImmuneSummaryHeight(EnvironmentLineHeight()) + (1 + count) * MetricRowHeight();
        immuneContent.fixedHeight = total;
        var visible = immuneClip.fixedHeight;
        immuneScrollOffset = Math.Clamp(immuneScrollOffset, 0, Math.Max(0, total - visible));
        var scrollbar = composer.GetScrollbar("immune-scroll");
        var scale = RuntimeEnv.GUIScale;
        var offset = immuneScrollOffset;
        scrollbar.SetHeights((float)(visible * scale), (float)(total * scale));
        scrollbar.CurrentYPosition = (float)(offset * scale);
        immuneScrollOffset = offset;
        PositionImmuneContent(immuneContent, offset);
    }
}
