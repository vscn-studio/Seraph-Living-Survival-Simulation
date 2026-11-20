using System.Reflection;
using System.Runtime.InteropServices;
using System.Text.Json;
using Cairo;
using Seraph.Network;
using Vintagestory.API.Client;
using Vintagestory.API.Config;

internal static class ActivityHudChecks
{
    internal static void Run(Assembly mod, string? nativeGame)
    {
        var type = mod.GetType("Seraph.Client.ActivityHud", true)!;
        if ((bool)type.GetMethod("ShouldReceiveMouseEvents")!.Invoke(
            System.Runtime.CompilerServices.RuntimeHelpers.GetUninitializedObject(type), null)!)
            throw new Exception("Informational activity HUD still receives mouse input.");
        const BindingFlags flags = BindingFlags.Static | BindingFlags.NonPublic;
        var boundsMethod = type.GetMethod("CreateBounds", flags)!;
        var scaleBefore = RuntimeEnv.GUIScale;
        var render = nativeGame is not null && OperatingSystem.IsWindows();
        if (render) NativeLibrary.SetDllImportResolver(typeof(Context).Assembly, (name, _, _) =>
            NativeLibrary.Load(System.IO.Path.Combine(nativeGame!, "Lib", name == "nanosvg" ? "nanosvg.dll" : "libcairo-2.dll")));
        var output = TestArtifacts.DirectoryPath("activity-hud-render");
        if (render) Directory.CreateDirectory(output);
        try
        {
            foreach (var (w, h, scale) in new[] { (1920, 1080, 1f), (1450, 900, 1f), (1280, 720, 1f),
                (1920, 1080, 1.5f), (1280, 720, 1.5f), (2560, 1440, 2f) })
            {
                RuntimeEnv.GUIScale = scale;
                var window = ElementBounds.Fixed(0, 0, w / (double)scale, h / (double)scale).WithEmptyParent();
                window.CalcWorldBounds();
                var bounds = ((ElementBounds)boundsMethod.Invoke(null, [w / (double)scale])!).WithParent(window);
                bounds.CalcWorldBounds();
                var hotbar = ElementBounds.Fixed(EnumDialogArea.CenterBottom, 0, 5, 850, 100).WithParent(window);
                hotbar.CalcWorldBounds();
                if (bounds.absX < 0 || bounds.absY < 0 || bounds.absX + bounds.OuterWidth > w
                    || bounds.absY + bounds.OuterHeight > h
                    || (bounds.absX < hotbar.absX + hotbar.OuterWidth && bounds.absX + bounds.OuterWidth > hotbar.absX
                        && bounds.absY + bounds.OuterHeight > hotbar.absY))
                    throw new Exception($"Activity HUD clipped or overlaps native hotbar/offhand: {w}x{h}@{scale}");
                if (!render) continue;
                foreach (var locale in new[] { "zh-cn", "en" })
                {
                    Lang.AvailableLanguages[locale] = new TranslationService(locale, null!, null!,
                        EnumLinebreakBehavior.AfterWord);
                    Lang.ChangeLanguage(locale);
                    var lang = JsonSerializer.Deserialize<Dictionary<string, string>>(File.ReadAllText($"assets/vsslssmod/lang/{locale}.json"))!;
                    var ledger = new ActivityLedger { Activity = "Mining", DemandMultiplier = 4, PaidKcalPerGameHour = 275,
                        StaminaPerSecond = -.002, OxygenPerSecond = -.003, FatiguePerGameHour = .02 };
                    var text = (string)type.GetMethod("FormatLedger", flags)!.Invoke(null,
                        [ledger, lang["activity-mining"], lang["activity-ledger"]])!;
                    var font = (CairoFont)type.GetMethod("LedgerFont", flags)!.Invoke(null, null)!;
                    using var surface = new ImageSurface(Format.Argb32, w, h);
                    using var ctx = new Context(surface);
                    ctx.SetSourceRGB(.08, .11, .13); ctx.Paint();
                    // Geometry guide only: inventory contents and the world require the interactive client.
                    ctx.SetSourceRGB(.22, .20, .17); ctx.Rectangle(hotbar.absX, hotbar.absY, hotbar.OuterWidth, hotbar.OuterHeight); ctx.Fill();
                    using var element = new GuiElementTextBase(null!, text, font, bounds);
                    var height = element.DrawMultilineTextAt(ctx, bounds.absX, bounds.absY);
                    if (height > bounds.InnerHeight) throw new Exception($"Ledger text overflows at {locale}/{scale}: {height}");
                    surface.WriteToPng($"{output}/{locale}-{w}x{h}-scale{scale:0.0}.png");
                }
            }
        }
        finally { RuntimeEnv.GUIScale = scaleBefore; }
        Console.WriteLine("PASS native HUD bounds at six resolutions/scales, including offhand clearance" +
            (render ? "; exported 12 native Cairo text renders (inventory geometry guide, not game screenshots)" : "; Cairo export skipped without native game path"));
    }
}

