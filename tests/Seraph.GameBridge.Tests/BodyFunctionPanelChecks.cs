using System.Reflection;
using System.Text.Json;
using Cairo;
using Seraph.Network;
using Vintagestory.API.Client;
using Vintagestory.API.Config;

internal static class BodyFunctionPanelChecks
{
    internal static void Run(Assembly mod, string? nativeGame)
    {
        if (nativeGame is null || !OperatingSystem.IsWindows()) return;
        const BindingFlags flags = BindingFlags.Static | BindingFlags.NonPublic;
        var type = mod.GetType("Seraph.Client.CharacterPanelIntegration", true)!;
        var keys = (string[])type.GetField("FunctionKeys", flags)!.GetValue(null)!;
        if (!keys.Contains("jump")) throw new Exception("The function panel omitted actual jump ability.");
        var format = type.GetMethod("FunctionMetricText", flags)!;
        var fit = type.GetMethod("FitFont", flags)!;
        var columnsMethod = type.GetMethod("FunctionColumns", flags)!;
        var originalLocale = Lang.CurrentLocale;
        var originalScale = RuntimeEnv.GUIScale;
        var output = TestArtifacts.DirectoryPath("character-panel-render");
        Directory.CreateDirectory(output);
        try
        {
            foreach (var locale in new[] { "zh-cn", "en" })
            {
                var translations = JsonSerializer.Deserialize<Dictionary<string, string>>(
                    File.ReadAllText($"assets/vsslssmod/lang/{locale}.json"))!;
                var translation = new TranslationService(locale, null!);
                typeof(TranslationService).GetField("entryCache", flags | BindingFlags.Instance)!.SetValue(translation,
                    translations.ToDictionary(kv => "vsslssmod:" + kv.Key, kv => kv.Value));
                typeof(TranslationService).GetField("loaded", flags | BindingFlags.Instance)!.SetValue(translation, true);
                Lang.AvailableLanguages[locale] = translation;
                Lang.ChangeLanguage(locale);
                foreach (var width in new[] { 250d, 300d, 360d })
                foreach (var scale in new[] { 1f, 1.5f, 2f })
                {
                    RuntimeEnv.GUIScale = scale;
                    var columns = (int)columnsMethod.Invoke(null, [width])!;
                    var rows = (keys.Length + columns - 1) / columns;
                    var cellWidth = (width - 14 * (columns - 1)) / columns;
                    using var surface = new ImageSurface(Format.Argb32, (int)((width + 24) * scale), (int)((rows * 30 + 24) * scale));
                    using var context = new Context(surface);
                    context.SetSourceRGB(.15, .13, .11); context.Paint();
                    for (var i = 0; i < keys.Length; i++)
                    {
                        var metric = new PanelMetric { Available = true, Value = 92.4, Text = keys[i] == "healingrate" ? "2.0 HP/h" : "",
                            Detail = translations["function-reason-" + (i < 6 ? "torso" : "nutrition")] };
                        var text = (string)format.Invoke(null, [keys[i], metric])!;
                        if (!text.Contains(keys[i] == "healingrate" ? "2.0 HP/h" : "92%") || text.Contains("vsslssmod:") || text.Contains('\n') || text.Contains(metric.Detail))
                            throw new Exception("Function row lost integer value/translation or retained a cause.");
                        var bounds = ElementBounds.Fixed(12 + (i / rows) * (cellWidth + 14), 12 + (i % rows) * 30,
                            cellWidth, 26).WithEmptyParent(); bounds.CalcWorldBounds();
                        var font = CairoFont.WhiteSmallText(); fit.Invoke(null, [font, text, bounds]);
                        using var element = new GuiElementTextBase(null!, text, font, bounds);
                        if (element.DrawMultilineTextAt(context, bounds.absX, bounds.absY) > bounds.InnerHeight + .1)
                            throw new Exception($"Function row clipped at {width}/{scale}: {text}");
                    }
                    surface.WriteToPng($"{output}/functions-{locale}-{width}-scale{scale:0.0}.png");
                }
            }
        }
        finally { RuntimeEnv.GUIScale = originalScale; Lang.ChangeLanguage(originalLocale); }
        Console.WriteLine("PASS actual function text: organ functions, combat defenses and healing rate, integer values without cause text; exported 18 native Cairo renders across 3 widths/scales in Chinese/English");
    }
}
