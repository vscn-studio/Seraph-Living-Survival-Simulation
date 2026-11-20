using System.Reflection;
using System.Runtime.CompilerServices;
using Cairo;
using Seraph.Client;
using Seraph.Network;
using Vintagestory.API.Client;
using Vintagestory.API.Config;

internal static class CharacterPanelRenderChecks
{
    internal static void Run(Assembly mod, string? nativeGame)
    {
        if (nativeGame is null || !OperatingSystem.IsWindows()) return;
        const BindingFlags flags = BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public;
        var bridge = (SeraphClientBridge)RuntimeHelpers.GetUninitializedObject(typeof(SeraphClientBridge));
        typeof(SeraphClientBridge).GetField("channel", flags)!.SetValue(bridge,
            RoutingProxy.Make(typeof(IClientNetworkChannel), (method, _) => method.Name == "get_Connected" ? true : throw new Exception(method.Name)));
        typeof(SeraphClientBridge).GetProperty("Snapshot")!.SetValue(bridge,
            new ServerSnapshot { GutMassG = 2050, GutMaximumG = 2500, GutWaterL = .25, BowelUnits = 250,
                StomachCompositionG = [250, 1500, 0, 0, 300, 0],
                Nutrients = [new() { Key = "fiber", Available = true, Pending = 300 },
                    new() { Key = "carbohydrate", Available = true, Pending = 2500 }] });
        typeof(SeraphClientBridge).GetProperty("LastReceivedUtc")!.SetValue(bridge, DateTime.UtcNow);
        var type = mod.GetType("Seraph.Client.CharacterPanelIntegration", true)!;
        var panel = RuntimeHelpers.GetUninitializedObject(type);
        type.GetField("bridge", flags)!.SetValue(panel, bridge);
        var contentsMethod = type.GetMethod("StomachContents", BindingFlags.Static | BindingFlags.NonPublic)!;
        var contents = ((string Key, double Grams)[])contentsMethod.Invoke(null, [bridge.Snapshot])!;
        if (Math.Abs(contents.Sum(c => c.Grams) - 2050) > 1e-9
            || contents.Single(c => c.Key == "fiber").Grams != 300
            || contents.Single(c => c.Key == "water").Grams != 250)
            throw new Exception("Stomach composition lost fiber/water or exceeded the actual mass.");
        var display = (Seraph.Core.Framework.NutrientDefinition[])type.GetField("DisplayNutrients", BindingFlags.Static | BindingFlags.NonPublic)!.GetValue(null)!;
        if (display.Length != 6 || display.Any(n => n.Id is "fiber" or "d" or "k" or "iron" or "zinc"
                or "carbohydrate" or "protein" or "fat")
            || display.Single(n => n.Id == "ironzinc").Unit != "%")
            throw new Exception("Nutrition display retained retired rows or failed to combine iron/zinc.");
        var scoreText = type.GetMethod("ScoreText", BindingFlags.Static | BindingFlags.NonPublic)!;
        if ((string)scoreText.Invoke(null, [4500d, 9000d])! != "50/100")
            throw new Exception("Large nutrition amounts still leak physical units into display scores.");
        var originalSnapshot = bridge.Snapshot;
        typeof(SeraphClientBridge).GetProperty("Snapshot")!.SetValue(bridge, new ServerSnapshot {
            Nutrients = [new() { Key = "iron", Available = true, Value = 30, Maximum = 100 },
                new() { Key = "zinc", Available = true, Value = 3.3, Maximum = 100 }] });
        var ironZinc = (NutrientGauge)type.GetMethod("DisplayNutrient", flags)!.Invoke(panel,
            [Array.FindIndex(display, n => n.Id == "ironzinc")])!;
        if (Math.Abs(ironZinc.Value - 3.3) > 1e-9 || ironZinc.Maximum != 100)
            throw new Exception("Combined iron/zinc failed to use the lower independent reserve.");
        typeof(SeraphClientBridge).GetProperty("Snapshot")!.SetValue(bridge, originalSnapshot);
        var draw = type.GetMethod("DrawGut", flags)!;
        var height = (double)type.GetMethod("DigestionHeight", BindingFlags.Static | BindingFlags.NonPublic)!.Invoke(null, [300d])!;
        var originalScale = RuntimeEnv.GUIScale;
        var output = TestArtifacts.DirectoryPath("character-panel-render");
        Directory.CreateDirectory(output);
        try
        {
            foreach (var scale in new[] { 1f, 1.5f, 2f })
            {
                RuntimeEnv.GUIScale = scale;
                var bounds = ElementBounds.Fixed(0, 0, 300, height).WithEmptyParent();
                bounds.CalcWorldBounds();
                using var surface = new ImageSurface(Format.Argb32, (int)bounds.InnerWidth, (int)bounds.InnerHeight);
                using var context = new Context(surface);
                draw.Invoke(panel, [context, bounds]);
                surface.Flush();
                var pixels = surface.Data;
                var occupied = Enumerable.Range(0, surface.Height).Where(y => Enumerable.Range(0, surface.Width)
                    .Any(x => pixels[y * surface.Stride + x * 4 + 3] != 0)).ToArray();
                if (occupied.Length == 0 || occupied[0] > 5 * scale || surface.Height - 1 - occupied[^1] > 5 * scale)
                    throw new Exception("Digestion diagram retains excessive vertical blank space.");
                surface.WriteToPng($"{output}/digestion-scale{scale:0.0}.png");

                // Exercise the actual Cairo path with no food: successful drinking must
                // change the visible stomach independently of food mass and bowel load.
                var original = bridge.Snapshot;
                byte[] RenderWater(double water)
                {
                    typeof(SeraphClientBridge).GetProperty("Snapshot")!.SetValue(bridge,
                        new ServerSnapshot { GutMassG = water * 1000, GutMaximumG = 2500, GutWaterL = water,
                            StomachCompositionG = [water * 1000, 0, 0, 0, 0, 0] });
                    using var waterSurface = new ImageSurface(Format.Argb32, (int)bounds.InnerWidth, (int)bounds.InnerHeight);
                    using var waterContext = new Context(waterSurface);
                    draw.Invoke(panel, [waterContext, bounds]); waterSurface.Flush();
                    return waterSurface.Data.ToArray();
                }
                var empty = RenderWater(0);
                var drunk = RenderWater(.25);
                var absorbing = RenderWater(.12);
                if (empty.SequenceEqual(drunk) || drunk.SequenceEqual(absorbing))
                    throw new Exception("Stomach display ignores drinking or subsequent water absorption.");
                typeof(SeraphClientBridge).GetProperty("Snapshot")!.SetValue(bridge, original);
            }
            foreach (var width in new[] { 250d, 360d })
            foreach (var scale in new[] { 1f, 1.5f, 2f })
            {
                RuntimeEnv.GUIScale = scale;
                var inline = type.GetMethod("InlineMetricText", BindingFlags.Static | BindingFlags.NonPublic)!;
                var fit = type.GetMethod("FitFont", BindingFlags.Static | BindingFlags.NonPublic)!;
                var environment = new PanelMetric[] {
                    new() { Key = "airtemp", Available = true, Value = -12.5, Unit = "°C" },
                    new() { Key = "humidity", Available = true, Value = 85, Unit = "%" },
                    new() { Key = "wind", Available = true, Value = 3.5, Unit = "m/s" },
                    new() { Key = "roomheat", Available = true, Value = 12, Unit = "°C" },
                    new() { Key = "ventilation", Available = true, Value = 50, Unit = "%" },
                    new() { Key = "airpollution", Available = true, Value = 15, Unit = "%" },
                    new() { Key = "environmentalcontamination", Available = true, Value = 20, Unit = "%" }
                };
                var thermal = new PanelMetric[] {
                    new() { Key = "coretemp", Available = true, Value = 37, Unit = "°C" },
                    new() { Key = "feversetpoint", Available = true, Value = 38.4, Unit = "°C" },
                    new() { Key = "skintemp", Available = true, Value = 32.5, Unit = "°C" },
                    new() { Key = "clothing", Available = true, Text = "-40%" },
                    new() { Key = "wetness", Available = true, Value = 25, Unit = "%" },
                    new() { Key = "heatproduction", Available = true, Value = 275, Unit = "kcal/游戏小时" },
                    new() { Key = "heatloss", Available = true, Value = 300, Unit = "kcal/游戏小时" },
                    new() { Key = "heatbalance", Available = true, Value = -25, Unit = "kcal/游戏小时" },
                    new() { Key = "sweat", Available = true, Value = .1, Unit = "L/游戏小时" },
                    new() { Key = "shivering", Available = true, Value = 50, Unit = "kcal/游戏小时" },
                    new() { Key = "sweatdebt", Available = true, Value = .3, Unit = "L" },
                    new() { Key = "heatadaptation", Available = true, Value = 7.5, Unit = "%" },
                    new() { Key = "coldadaptation", Available = true, Value = 5, Unit = "%" }
                };
                var line = Math.Ceiling(CairoFont.WhiteSmallText().GetFontExtents().Height / scale) + 2;
                var row = line + 6;
                using var surface = new ImageSurface(Format.Argb32, (int)(width * scale), (int)((15 * row + 24) * scale));
                using var ctx = new Context(surface);
                ctx.SetSourceRGB(.15, .13, .11); ctx.Paint();
                var top = 12d;
                Draw("环境（单元测试样例）", 12, top, width - 24); top += row;
                Grid(environment); top += row;
                Draw("体温调节（单元测试样例）", 12, top, width - 24); top += row;
                Grid(thermal);
                surface.WriteToPng($"{output}/inline-{width}-scale{scale:0.0}.png");
                ctx.SetSourceRGB(.15, .13, .11); ctx.Paint(); top = 12;
                Draw("物理存量与刻度（离线排版样例）", 12, top, width - 24); top += row;
                foreach (var (label, text) in new[] {
                    ("糖原储能", "471/706 g"), ("脂肪储能", "10 kg"), ("待吸收能量", "4780 kcal"),
                    ("电解质", "50/100 g"),
                    ("钙", "50/100"), ("铁/锌", "10/100"), ("血量", "5/5 L"),
                    ("血液废物", "30/1250 mL"), ("血乳酸", "12/100 g"),
                    ("肠道负荷", "4000/5000 g"), ("肌肉量", "32/50 kg") })
                {
                    Draw(label, 12, top, (width - 24) / 2);
                    Draw(text, width / 2, top, (width - 24) / 2); top += row;
                }
                Draw("水量：7000/8000 g", 12, top, width - 24);
                surface.WriteToPng($"{output}/reserve-scores-{width}-scale{scale:0.0}.png");

                void Grid(PanelMetric[] metrics)
                {
                    var rows = (metrics.Length + 1) / 2;
                    var cellWidth = (width - 24 - 14) / 2;
                    for (var i = 0; i < metrics.Length; i++)
                    {
                        var text = (string)inline.Invoke(null, [metrics[i].Key, metrics[i]])!;
                        if (metrics[i].Unit.Length > 0 && !text.Contains(metrics[i].Unit.Replace("/游戏小时", "/h")))
                            throw new Exception("Inline metric lost its unit: " + text);
                        Draw(text, 12 + i / rows * (cellWidth + 14), top + i % rows * row, cellWidth);
                    }
                    top += rows * row;
                }
                void Draw(string text, double x, double y, double cellWidth)
                {
                    var bounds = ElementBounds.Fixed(x, y, cellWidth, line).WithEmptyParent();
                    bounds.CalcWorldBounds();
                    var font = CairoFont.WhiteSmallText();
                    fit.Invoke(null, [font, text, bounds]);
                    using var element = new GuiElementTextBase(null!, text, font, bounds);
                    if (element.DrawMultilineTextAt(ctx, bounds.absX, bounds.absY) > bounds.InnerHeight + .1)
                        throw new Exception($"Inline metric wrapped at {width}/{scale}: {text}; height={element.GetMultilineTextHeight()}, bounds={bounds.InnerHeight}, lines={element.textUtil.Lineize(font, text, bounds.InnerWidth).Length}, advance={font.GetTextExtents(text).XAdvance}, width={bounds.InnerWidth}, font={font.UnscaledFontsize}");
                }
            }
        }
        finally { RuntimeEnv.GUIScale = originalScale; }
        Console.WriteLine("PASS actual Cairo digestion: mass-conserving fiber composition and water layer change after empty-stomach drinking and absorption at scales 1/1.5/2; inline environment/thermal grids at widths 250/360 and scales 1/1.5/2 (isolated renders)");
    }
}
