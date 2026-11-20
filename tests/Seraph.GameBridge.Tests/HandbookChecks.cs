using System.Reflection;
using System.Text.Json;
using Cairo;
using Vintagestory.API.Client;
using Vintagestory.API.Common;
using Vintagestory.API.Config;
using Vintagestory.GameContent;
using Path = System.IO.Path;

internal static class HandbookChecks
{
    internal static void Run(string? nativeGame, bool configureCairo = false)
    {
        if (!OperatingSystem.IsWindows() || nativeGame is null)
            throw new Exception("Handbook Cairo verification requires the Windows game directory.");
        if (configureCairo) System.Runtime.InteropServices.NativeLibrary.SetDllImportResolver(typeof(Context).Assembly, (name, _, _) =>
            System.Runtime.InteropServices.NativeLibrary.Load(Path.Combine(nativeGame, "Lib", name == "nanosvg" ? "nanosvg.dll" : "libcairo-2.dll")));
        var paths = Directory.GetFiles("assets/vsslssmod/config/handbook", "*.json").Order().ToArray();
        var originalScale = RuntimeEnv.GUIScale;
        var originalLocale = Lang.CurrentLocale;
        var saved = new[] { "en", "zh-cn" }.ToDictionary(k => k,
            k => Lang.AvailableLanguages.TryGetValue(k, out var service) ? service : null);
        var logger = RoutingProxy.Make(typeof(ILogger), (_, _) => null);
        Context? previewContext = null;
        LinkTextComponent? composingLink = null;
        var uploads = 0;
        var gui = RoutingProxy.Make(typeof(IGuiAPI), (m, args) => {
            if (m.Name != "LoadOrUpdateCairoTexture") throw new Exception("Unexpected handbook GUI operation: " + m.Name);
            // Native links compose their own textures. Capture the normal texture
            // onto the preview without creating an OpenGL context.
            if (previewContext is not null && composingLink is not null && uploads++ == 0)
            {
                previewContext.SetSourceSurface((ImageSurface)args![0]!,
                    (int)composingLink.BoundsPerLine.Min(b => b.X), (int)composingLink.BoundsPerLine.Min(b => b.Y));
                previewContext.Paint();
            }
            return null;
        });
        var client = (ICoreClientAPI)RoutingProxy.Make(typeof(ICoreClientAPI), (m, _) => m.Name switch {
            "get_Logger" => logger, "get_Gui" => gui, _ => throw new Exception(m.Name)
        });
        var layouts = 0;
        var output = TestArtifacts.DirectoryPath("handbook-render");
        try
        {
            foreach (var locale in saved.Keys)
            {
                // Match assets/game/lang/languages.json, including Chinese's native character wrapping.
                var service = new TranslationService(locale, null!, null!, locale == "zh-cn"
                    ? EnumLinebreakBehavior.AfterCharacter : EnumLinebreakBehavior.AfterWord);
                var entries = new Dictionary<string, string>();
                foreach (var domain in new[] { "vsslssmod", "game" })
                foreach (var pair in JsonSerializer.Deserialize<Dictionary<string, string>>(File.ReadAllText($"assets/{domain}/lang/{locale}.json"))!)
                    entries[domain + ":" + pair.Key] = pair.Value;
                typeof(TranslationService).GetField("entryCache", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(service, entries);
                typeof(TranslationService).GetField("loaded", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(service, true);
                Lang.AvailableLanguages[locale] = service;
                Lang.ChangeLanguage(locale);
                if (Lang.Get("handbook-category-vsslssmod") == "handbook-category-vsslssmod") throw new Exception("Native category untranslated.");
                foreach (var scale in new[] { 1f, 1.5f, 2f })
                foreach (var width in new[] { 360d, 550d })
                foreach (var path in paths)
                {
                    RuntimeEnv.GUIScale = scale;
                    var page = JsonUtil.FromString<GuiHandbookTextPage>(File.ReadAllText(path))!;
                    var textKey = page.Text;
                    page.Init(client); // Exercise native language lookup and VTML parser.
                    if (page.Text == textKey || page.GetPageText().Text != Lang.Get(textKey)
                        || page.GetPageText().Title.Contains("handbook-")) throw new Exception("Native page failed to localize: " + path);
                    var components = (RichTextComponentBase[])typeof(GuiHandbookTextPage)
                        .GetField("comps", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(page)!;
                    using var rich = new GuiElementRichtext(null!, components, ElementBounds.Fixed(0, 0, width, 100).WithEmptyParent());
                    rich.CalcHeightAndPositions();
                    if (rich.MaxLineWidth > width * scale + 2 || !double.IsFinite(rich.Bounds.fixedHeight) || rich.Bounds.fixedHeight <= 100)
                        throw new Exception($"Native handbook clipped/empty: {locale}/{scale}/{width}/{path}: {rich.MaxLineWidth}");
                    var links = components.OfType<LinkTextComponent>().ToArray();
                    if (links.Length == 0 || links.Any(l => !paths.Any(p => "handbook://vsslssmod:" + Path.GetFileNameWithoutExtension(p)[3..] == l.Href)))
                        throw new Exception("Native chapter links missing or broken: " + path);
                    // Render the overview and a practical recipe page in each language.
                    if (scale == 1 && width == 550 && (page.pageCode.EndsWith(":start") || page.pageCode.EndsWith(":filter")))
                    {
                        using var surface = new ImageSurface(Format.Argb32, (int)width, (int)Math.Ceiling(rich.Bounds.fixedHeight) + 8);
                        using var ctx = new Context(surface);
                        ctx.SetSourceRGB(.13, .12, .11); ctx.Paint();
                        previewContext = ctx;
                        foreach (var component in components)
                        {
                            composingLink = component as LinkTextComponent;
                            uploads = 0;
                            component.ComposeElements(ctx, surface);
                        }
                        previewContext = null; composingLink = null;
                        surface.WriteToPng($"{output}/{locale}-{page.pageCode.Split(':')[1]}.png");
                    }
                    layouts++;
                }
            }
        }
        finally
        {
            RuntimeEnv.GUIScale = originalScale;
            foreach (var pair in saved)
                if (pair.Value is null) Lang.AvailableLanguages.Remove(pair.Key); else Lang.AvailableLanguages[pair.Key] = pair.Value;
            Lang.ChangeLanguage(originalLocale);
        }
        Console.WriteLine($"PASS {paths.Length} native handbook chapters: localization/search text, native VTML links, {layouts} Cairo layouts (2 languages × 3 scales × 2 widths); 4 rendered previews.");
    }
}
