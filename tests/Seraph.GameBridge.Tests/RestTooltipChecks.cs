using System.Reflection;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Cairo;
using Seraph.Network;
using Vintagestory.API.Client;
using Vintagestory.API.Common;
using Vintagestory.API.Config;

internal static class RestTooltipChecks
{
    internal static void Run(Assembly mod, string? nativeGame)
    {
        const BindingFlags flags = BindingFlags.Static | BindingFlags.NonPublic;
        var display = mod.GetType("Seraph.Client.ClosedEyeRestDisplay", true)!;
        var values = display.GetMethod("Values", flags)!;
        var state = new ServerSnapshot { StatusMetrics = [
            new() { Key = "stamina", Available = true, Value = 72.5 },
            new() { Key = "fatigue", Available = true, Value = 28.3 },
            new() { Key = "sleeppressure", Available = true, Value = 63.1 }] };
        var projected = (double?[])values.Invoke(null, [state])!;
        if (!projected.SequenceEqual(new double?[] { 72.5, 28.3, 63.1 })) throw new Exception("Rest HUD changed server values/order.");
        if (((double?[])values.Invoke(null, [new ServerSnapshot()])!).Any(v => v is not null))
            throw new Exception("Rest HUD fabricated available reserves.");
        var tooltip = mod.GetType("Seraph.Client.TooltipText", true)!;
        var item = tooltip.GetMethod("Item", flags)!;
        tooltip.GetMethod("InitializeMainThread", flags)!.Invoke(null, null);
        var workerItem = Task.Factory.StartNew(
            () => (string)item.Invoke(null, ["后台手册文本 <font color=\"#ffcc88\">123 g</font>"])!,
            CancellationToken.None, TaskCreationOptions.LongRunning, TaskScheduler.Default)
            .GetAwaiter().GetResult();
        var workerItemPlain = Regex.Replace(workerItem, "<[^>]+>|\\s+", "");
        if (!workerItemPlain.Contains("后台手册文本", StringComparison.Ordinal)
            || !workerItemPlain.Contains("123g", StringComparison.Ordinal))
            throw new Exception("Worker item tooltip fallback lost text.");
        if (nativeGame is null || !OperatingSystem.IsWindows()) return;
        var wrap = tooltip.GetMethod("Wrap", flags)!;
        var append = tooltip.GetMethod("AppendItem", flags)!;
        var output = TestArtifacts.DirectoryPath("rest-tooltip-render");
        Directory.CreateDirectory(output);
        var originalScale = RuntimeEnv.GUIScale;
        var originalLocale = Lang.CurrentLocale;
        try
        {
            foreach (var scale in new[] { 1f, 1.5f, 2f })
            foreach (var locale in new[] { "zh-cn", "en" })
            {
                RuntimeEnv.GUIScale = scale;
                Lang.AvailableLanguages[locale] = new TranslationService(locale, null!, null!, EnumLinebreakBehavior.AfterWord);
                Lang.ChangeLanguage(locale);
                var lang = JsonSerializer.Deserialize<Dictionary<string, string>>(File.ReadAllText($"assets/vsslssmod/lang/{locale}.json"))!;
                using var font = CairoFont.WhiteSmallText();
                // Handbook descriptions are warmed on a thread-pool worker. The
                // worker path must not enter Cairo, which is thread-affine and
                // otherwise can terminate CoreCLR with an access violation.
                var workerText = Task.Factory.StartNew(
                    () => (string)wrap.Invoke(null, ["后台手册文本 <font color=\"#ffcc88\">123 g</font>", font, 229d])!,
                    CancellationToken.None, TaskCreationOptions.LongRunning, TaskScheduler.Default)
                    .GetAwaiter().GetResult();
                var workerTextPlain = Regex.Replace(workerText, "<[^>]+>|\\s+", "");
                if (!workerTextPlain.Contains("后台手册文本", StringComparison.Ordinal)
                    || !workerTextPlain.Contains("123g", StringComparison.Ordinal))
                    throw new Exception("Worker tooltip fallback lost text.");
                // Test the game's AfterWord lineizer, including Chinese with no spaces and mixed colored nutrients.
                foreach (var text in lang.Where(p => p.Key.StartsWith("metric-help-")).Select(p => p.Value)
                    .Append("<font color=\"#ffcc88\">蛋白质(123.45 g) · Carbohydrate(123.45 g)</font>\n\n免疫系统需要恢复，避免新的污染暴露。"))
                {
                    var formatted = (string)wrap.Invoke(null, [text, font, 229d])!;
                    if (formatted.Contains("\n\n")) throw new Exception("Tooltip retains blank paragraphs.");
                    var plain = Regex.Replace(formatted, "<[^>]+>", "");
                    var nativeLines = new TextDrawUtil().Lineize(font, plain, 245 * scale, EnumLinebreakBehavior.AfterWord);
                    if (nativeLines.Any(line => line.Bounds.Width > 245 * scale + 1))
                        throw new Exception($"Native tooltip line clipped: {locale}/{scale}: {formatted}");
                    if (Regex.Replace(plain, @"\s", "") != Regex.Replace(Regex.Replace(text, "<[^>]+>", ""), @"\s", ""))
                        throw new Exception("Wrapping discarded tooltip content.");
                    var logger = RoutingProxy.Make(typeof(ILogger), (_, _) => null);
                    var client = (ICoreClientAPI)RoutingProxy.Make(typeof(ICoreClientAPI), (m, _) => m.Name == "get_Logger" ? logger : throw new Exception(m.Name));
                    var components = VtmlUtil.Richtextify(client, formatted, font);
                    using var rich = new GuiElementRichtext(null!, components,
                        ElementBounds.Fixed(0, 0, 245, 200).WithEmptyParent());
                    rich.CalcHeightAndPositions();
                    if (rich.MaxLineWidth > 245 * scale + 1) throw new Exception("Colored native rich text clipped.");
                }
                var builder = new StringBuilder("Other mod text\n\n\n");
                append.Invoke(null, [builder, "\n\n本模组信息\n\n下行\n\n"]);
                if (builder.ToString() != "Other mod text\n本模组信息\n下行\n") throw new Exception("Item boundary leaves blank lines.");
                var sample = (string)wrap.Invoke(null, [lang["metric-help-bodywater"] + "\n" + lang["metric-help-bleeding"], font, 284d])!;
                var bounds = ElementBounds.Fixed(12, 12, 300, 200).WithEmptyParent(); bounds.CalcWorldBounds();
                using var hover = new ImageSurface(Format.Argb32, (int)(324 * scale), (int)(220 * scale));
                using var hctx = new Context(hover);
                hctx.SetSourceRGB(.12, .11, .1); hctx.Paint();
                using var element = new GuiElementTextBase(null!, sample, font, bounds);
                if (element.DrawMultilineTextAt(hctx, bounds.absX, bounds.absY) > bounds.InnerHeight)
                    throw new Exception("Compact tooltip exceeds preview bounds.");
                hover.WriteToPng($"{output}/tooltip-{locale}-scale{scale:0.0}.png");
            }
        }
        finally { RuntimeEnv.GUIScale = originalScale; Lang.ChangeLanguage(originalLocale); }
        Console.WriteLine("PASS authoritative rest values/unknowns; native Chinese/English tooltip line bounds and blank-line cleanup at 3 scales; exported 6 Cairo tooltip renders");
    }
}
