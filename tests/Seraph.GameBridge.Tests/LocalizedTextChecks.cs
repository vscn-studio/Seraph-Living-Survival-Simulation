using System.Reflection;
using Vintagestory.API.Config;

internal static class LocalizedTextChecks
{
    internal static void Run(Assembly mod)
    {
        const BindingFlags flags = BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static;
        var method = mod.GetType("Seraph.LocalizedText", true)!.GetMethod("GetL", flags)!;
        string Text(string locale, string key, params object[] args)
            => (string)method.Invoke(null, [locale, "vsslssmod:" + key, args])!;
        foreach (var (locale, value) in new[] { ("en", "Work {0}"), ("zh-cn", "劳动 {0}") })
        {
            var service = new TranslationService(locale, null!);
            typeof(TranslationService).GetField("entryCache", flags)!.SetValue(service,
                new Dictionary<string, string> { ["vsslssmod:test-work"] = value,
                    ["vsslssmod:test-invalid"] = locale == "en" ? "Valid {0}" : "Broken {1}",
                    ["vsslssmod:metric-test"] = "Reserve",
                    ["vsslssmod:metric-help-test"] = "vsslssmod:metric-help-test",
                    ["vsslssmod:tooltip-no-description"] = "No description available." });
            typeof(TranslationService).GetField("loaded", flags)!.SetValue(service, true);
            Lang.AvailableLanguages[locale] = service;
        }
        Lang.ChangeLanguage("en");
        if (Text("zh-cn", "test-work", 3) != "劳动 3" || Text("en", "test-work", 3) != "Work 3")
            throw new Exception("Notification ignored recipient language.");
        if (Text("zh-cn", "test-invalid", 3) != "Valid 3"
            || Text("zh-cn", "unknown-action") != "unknown action")
            throw new Exception("Invalid/missing translation did not produce readable fallback.");
        var help = mod.GetType("Seraph.Client.TooltipText", true)!.GetMethod("Help", flags)!;
        var tooltip = (string)help.Invoke(null, ["metric-help-test", "metric-test"])!;
        if (!tooltip.Contains("Reserve") || !tooltip.Contains("No description available.") || tooltip.Contains("metric-help"))
            throw new Exception("Missing tooltip exposed a developer field instead of a readable description.");
        Console.WriteLine("PASS recipient-language formatting, invalid format English fallback and unknown readable text");
    }
}
