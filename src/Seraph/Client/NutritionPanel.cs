using Cairo;
using Seraph.Core.Framework;
using Seraph.Network;
using Vintagestory.API.Client;
using Vintagestory.API.Config;

namespace Seraph.Client;

internal sealed partial class CharacterPanelIntegration
{
    private GuiComposer? lastNutritionComposer;
    private readonly Dictionary<int, (string Text, double Value, double Maximum)> nutritionCache = [];
    private readonly Dictionary<int, string> nutrientHoverCache = [];
    private string? lastGutVisual;
    private static readonly NutrientDefinition[] DisplayNutrients = Nutrients.All
        .Where(n => n.Id is not ("carbohydrate" or "protein" or "fat" or "fiber" or "d" or "k" or "zinc"))
        .Select(n => n.Id == "iron" ? new NutrientDefinition("ironzinc", "%", n.DailyBudget, n.Color) : n).ToArray();
    internal static double DigestionRadius(double width) => Math.Max(24, (width - Math.Max(10, width * .04)) / 4) * (2d / 3);
    internal static double DigestionHeight(double width) => 2 * DigestionRadius(width) + 8;

    private double ComposeNutritionModel(GuiComposer composer, double width)
    {
        var diagramHeight = DigestionHeight(width);
        var statusTop = DetailsTop + diagramHeight + 10;
        composer.AddDynamicCustomDraw(ElementBounds.Fixed(0, DetailsTop, width, diagramHeight),
            (ctx, _, bounds) => DrawGut(ctx, bounds), "vsslssmod-gut");
        var radius = DigestionRadius(width);
        composer.AddCharacterPanelHoverText("", AddedPanelFont(), TooltipText.PanelWidth,
            ElementBounds.Fixed(width / 4 - radius, DetailsTop + diagramHeight / 2 - radius,
                2 * radius, 2 * radius), "stomach-contents-help");
        return statusTop;
    }
    private void ComposeNutritionDetails(GuiComposer composer, double width, double nutrientTop)
    {
        string[] energyKeys = ["glycogen", "fatenergy", "gutenergy"];
        var rowHeight = MetricRowHeight();
        var fontSize = (float)AddedPanelFont().UnscaledFontsize;
        for (var i = 0; i < energyKeys.Length; i++)
        {
            var position = i;
            AddMetricRow(composer, "energy-" + i, LocalizedText.Get("vsslssmod:metric-" + energyKeys[i]),
                LocalizedText.Get("vsslssmod:metric-help-" + energyKeys[i]), 0, nutrientTop + position * rowHeight,
                width, Math.Min(126, width * .5), rowHeight, fontSize,
                Color(i == 0 ? "#e4b744" : "#80ac56"), true);
        }
        for (var i = 0; i < DisplayNutrients.Length; i++)
        {
            var n = DisplayNutrients[i];
            var position = energyKeys.Length + i;
            AddMetricRow(composer, "nutrient-" + i, LocalizedText.Get("vsslssmod:nutrient-short-" + n.Id), NutrientHelp(i),
                0, nutrientTop + position * rowHeight, width,
                Math.Min(116, width * .48), rowHeight, fontSize, Color(n.Color), true);
        }
    }
    private string NutrientText(int index)
    {
        var gauge = DisplayNutrient(index);
        if (gauge is not { Available: true }) return "";
        var id = DisplayNutrients[index].Id;
        return id is "carbohydrate" or "protein" or "fat" ? $"{N(gauge.Value)}/{N(gauge.Maximum)} g"
            : id == "electrolytes" ? $"{N(gauge.Value)}/{N(gauge.Maximum)} g"
            : ScoreText(gauge.Value, gauge.Maximum);
    }
    internal static string ScoreText(double value, double maximum) => $"{ReserveDisplay.Score(value, maximum):0.##}/100";
    private NutrientGauge? DisplayNutrient(int index)
    {
        if (!bridge.HasFreshSnapshot || bridge.Snapshot is not { } snapshot) return null;
        var id = DisplayNutrients[index].Id;
        if (id != "ironzinc") return snapshot.Nutrients.FirstOrDefault(n => n.Key == id);
        // Preserve the native panel row; the less supplied mineral limits this summary.
        var minerals = snapshot.Nutrients.Where(n => n.Key is "iron" or "zinc").ToArray();
        return minerals.Length == 2 && minerals.All(n => n.Available)
            ? minerals.MinBy(n => n.Value / Math.Max(1, n.Maximum)) : null;
    }
    private string NutrientHelp(int index)
    {
        var def = DisplayNutrients[index];
        var gauge = DisplayNutrient(index);
        return gauge is { Available: true }
            ? LocalizedText.Get("vsslssmod:nutrition-stage-" + ((NutrientStage)gauge.Stage).ToString().ToLowerInvariant())
                + "\n" + LocalizedText.Get("vsslssmod:nutrition-effect-" + def.Id)
            : Lang.Get("vsslssmod:metric-unavailable");
    }
    private void RefreshNutrition(GuiComposer model, GuiComposer composer)
    {
        if (model.GetCustomDraw("vsslssmod-gut") is not { } gut) return;
        if (!ReferenceEquals(lastNutritionComposer, composer))
        { nutritionCache.Clear(); nutrientHoverCache.Clear(); lastNutritionComposer = composer; lastGutVisual = null; }
        var snapshot = bridge.HasFreshSnapshot ? bridge.Snapshot : null;
        SetPanelHoverText(model, "stomach-contents-help", snapshot is null
            ? Lang.Get("vsslssmod:character-value-unknown")
            : string.Join("\n", StomachContents(snapshot).Select(c => Lang.Get("vsslssmod:gut-content-line",
                LocalizedText.Get("vsslssmod:" + (c.Key == "water" ? "gut-water" : c.Key == "residue" ? "gut-residue" : "nutrient-short-" + c.Key)),
                N(c.Grams), "g"))));
        for (var i = 0; i < 3; i++)
        {
            var value = snapshot is null ? 0 : i switch { 0 => snapshot.GlycogenEnergyKJ, 1 => snapshot.FatEnergyKJ, _ => snapshot.GutEnergyKJ };
            var max = snapshot is null ? 1 : i switch { 0 => snapshot.GlycogenCapacityKJ, 1 => snapshot.FatCapacityKJ, _ => snapshot.GutEnergyMaximumKJ };
            var text = snapshot is null ? "" : i switch {
                0 => $"{N(value / DietaryMetabolism.CarbKJPerG)}/{N(max / DietaryMetabolism.CarbKJPerG)} g",
                1 => $"{N(value / (DietaryMetabolism.FatKJPerG * 1000))} kg",
                _ => $"{N(value / 4.184)} kcal"
            };
            if (!nutritionCache.TryGetValue(-i - 1, out var previous) || previous != (text, value, max))
            {
                SetFittedValue(composer, "energy-" + i + "-value", text);
                SetBarRange(composer.GetStatbar("energy-" + i + "-bar"), value, 0, max);
                nutritionCache[-i - 1] = (text, value, max);
            }
        }
        for (var i = 0; i < DisplayNutrients.Length; i++)
        {
            var gauge = DisplayNutrient(i);
            var text = NutrientText(i);
            var value = gauge is { Available: true } ? gauge.Value : 0;
            var maximum = gauge?.Maximum ?? DisplayNutrients[i].Maximum;
            if (!nutritionCache.TryGetValue(i, out var previous) || previous != (text, value, maximum))
            {
                SetFittedValue(composer, "nutrient-" + i + "-value", text);
                SetBarRange(composer.GetStatbar("nutrient-" + i + "-bar"), value, 0, maximum);
                nutritionCache[i] = (text, value, maximum);
            }
            var hover = NutrientHelp(i);
            if (!nutrientHoverCache.TryGetValue(i, out var oldHover) || oldHover != hover)
            {
                SetPanelHoverText(composer, "nutrient-" + i + "-help", hover);
                nutrientHoverCache[i] = hover;
            }
        }
        var visual = GutVisualKey(snapshot, gut.Bounds.InnerWidth);
        if (lastGutVisual != visual) { gut.Redraw(); lastGutVisual = visual; }
    }
    internal static string GutVisualKey(ServerSnapshot? snapshot, double width)
        => snapshot is null ? "offline" : string.Join("|", new[] {
            snapshot.GutMassG.ToString("0", System.Globalization.CultureInfo.InvariantCulture),
            snapshot.GutMaximumG.ToString("0", System.Globalization.CultureInfo.InvariantCulture),
            snapshot.BowelUnits.ToString("0", System.Globalization.CultureInfo.InvariantCulture),
            Math.Round(snapshot.BowelUnits / 5000 * width).ToString(System.Globalization.CultureInfo.InvariantCulture)
        }.Concat(StomachContents(snapshot).Select(c => Math.Round(c.Grams / Math.Max(1, snapshot.GutMaximumG) * width)
            .ToString(System.Globalization.CultureInfo.InvariantCulture))));
    internal static double[] Color(string hex) => [Convert.ToInt32(hex.Substring(1, 2), 16) / 255d,
        Convert.ToInt32(hex.Substring(3, 2), 16) / 255d, Convert.ToInt32(hex.Substring(5, 2), 16) / 255d];
    internal static (string Key, double Grams)[] StomachContents(ServerSnapshot snapshot)
    {
        string[] keys = ["water", "carbohydrate", "protein", "fat", "fiber", "residue"];
        return keys.Select((key, i) => (key, snapshot.StomachCompositionG.Length == 6
            ? Math.Max(0, snapshot.StomachCompositionG[i]) : 0)).ToArray();
    }
    private void DrawGut(Context ctx, ElementBounds bounds)
    {
        var s = bridge.HasFreshSnapshot ? bridge.Snapshot : null;
        var scale = Math.Max(.1, RuntimeEnv.GUIScale);
        var radius = DigestionRadius(bounds.InnerWidth / scale) * scale;
        var centerY = bounds.InnerHeight / 2;
        var centerX = bounds.InnerWidth / 4;
        var secondX = bounds.InnerWidth * 3 / 4;
        ctx.Save();
        for (var diagram = 0; diagram < 2; diagram++)
        {
            var cx = diagram == 0 ? centerX : secondX;
            ctx.SetSourceRGBA(.04, .06, .05, .85); ctx.Arc(cx, centerY, radius, 0, Math.PI * 2); ctx.Fill();
            ctx.Save(); ctx.Arc(cx, centerY, radius - 1, 0, Math.PI * 2); ctx.Clip();
            if (diagram == 0 && s is not null)
            {
                var maximum = Math.Max(1, s.GutMaximumG);
                double y = centerY + radius;
                foreach (var component in StomachContents(s))
                {
                    var height = Math.Min(radius * 2, component.Grams / maximum * radius * 2); y -= height;
                    var color = component.Key == "water" ? new[] { .15, .55, .75 }
                        : component.Key == "residue" ? new[] { .45, .58, .6 } : Color(Nutrients.All[Nutrients.IndexOf(component.Key)].Color);
                    ctx.SetSourceRGB(color[0], color[1], color[2]);
                    ctx.Rectangle(cx - radius, y, radius * 2, height); ctx.Fill();
                }
            }
            else if (diagram == 1 && s is not null)
            {
                var fill = Math.Clamp(s.BowelUnits / 5000, 0, 1) * radius * 2;
                ctx.SetSourceRGB(.52, .33, .16); ctx.Rectangle(cx - radius, centerY + radius - fill, radius * 2, fill); ctx.Fill();
            }
            ctx.Restore(); ctx.SetSourceRGBA(.95, .95, .9, .95); ctx.LineWidth = 2;
            ctx.Arc(cx, centerY, radius, 0, Math.PI * 2); ctx.Stroke();
            var value = diagram == 0
                ? $"{s?.GutMassG ?? 0:0}/{s?.GutMaximumG ?? 5000:0}g"
                : $"{s?.BowelUnits ?? 0:0}/5000g";
            var font = AddedPanelFont().WithWeight(FontWeight.Bold);
            font.UnscaledFontsize = Math.Clamp((float)(radius * .27), 12, 18);
            font.SetupContext(ctx);
            var extents = ctx.TextExtents(value);
            if (extents.Width > radius * 1.8)
            {
                font.UnscaledFontsize *= radius * 1.8 / extents.Width;
                font.SetupContext(ctx);
                extents = ctx.TextExtents(value);
            }
            ctx.SetSourceRGBA(.96, .96, .92, .98);
            ctx.MoveTo(cx - extents.Width / 2 - extents.XBearing, centerY - extents.YBearing - extents.Height / 2);
            ctx.ShowText(value);
        }
        ctx.Restore();
    }
}
