using System.Diagnostics;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Text.Json;
using Seraph.Client;
using Seraph.Network;
using Vintagestory.API.Client;
using Vintagestory.API.Common;
using Vintagestory.API.Config;

internal static class PanelPerformanceChecks
{
    internal static void Run(Assembly mod, string? nativeGame)
    {
        if (nativeGame is null || !OperatingSystem.IsWindows()) return;
        var locale = Lang.CurrentLocale;
        var previousEnglish = Lang.AvailableLanguages.GetValueOrDefault("en");
        var logger = (ILogger)RoutingProxy.Make(typeof(ILogger), (_, _) => null);
        var english = new TranslationService("en", logger, null!, EnumLinebreakBehavior.AfterWord);
        english.PreLoad(Path.Combine(nativeGame, "assets"));
        foreach (var entry in JsonSerializer.Deserialize<Dictionary<string, string>>(File.ReadAllText("assets/vsslssmod/lang/en.json"))!)
            english.GetAllEntries()["vsslssmod:" + entry.Key] = entry.Value;
        Lang.AvailableLanguages["en"] = english;
        Lang.ChangeLanguage("en");
        try { Check(mod); PanelRefreshChecks.Run(mod); }
        finally {
            if (previousEnglish is null) Lang.AvailableLanguages.Remove("en");
            else Lang.AvailableLanguages["en"] = previousEnglish;
            Lang.ChangeLanguage(locale);
        }
    }

    private static void Check(Assembly mod)
    {
        const BindingFlags all = BindingFlags.Instance | BindingFlags.Static | BindingFlags.NonPublic | BindingFlags.Public;
        var bridge = (SeraphClientBridge)RuntimeHelpers.GetUninitializedObject(typeof(SeraphClientBridge));
        typeof(SeraphClientBridge).GetField("channel", all)!.SetValue(bridge,
            RoutingProxy.Make(typeof(IClientNetworkChannel), (m, _) => m.Name == "get_Connected" ? true : throw new Exception(m.Name)));
        typeof(SeraphClientBridge).GetProperty("Snapshot")!.SetValue(bridge, new ServerSnapshot());
        typeof(SeraphClientBridge).GetProperty("LastReceivedUtc")!.SetValue(bridge, DateTime.UtcNow);
        var events = RoutingProxy.Make(typeof(IClientEventAPI), (m, _) => m.Name == "RegisterGameTickListener" ? 1L : null);
        var api = RoutingProxy.Make(typeof(ICoreClientAPI), (m, _) => m.Name == "get_Event" ? events : throw new Exception(m.Name));
        var type = mod.GetType("Seraph.Client.CharacterPanelIntegration", true)!;
        var panel = Activator.CreateInstance(type, [api, bridge])!;
        var hover = type.GetMethod("MetricHover", all)!;
        var metric = new PanelMetric { Available = true };
        var args = new object[] { "stamina", metric };
        var first = hover.Invoke(panel, args);
        if (!ReferenceEquals(first, hover.Invoke(panel, args))) throw new Exception("Unchanged tooltip bypassed the source cache.");
        var timer = Stopwatch.StartNew();
        for (var i = 0; i < 1000; i++) hover.Invoke(panel, args);
        var warm = timer.Elapsed.TotalMilliseconds / 1000;
        var cache = (System.Collections.IDictionary)type.GetField("metricHoverCache", all)!.GetValue(panel)!;
        var textType = mod.GetType("Seraph.Client.TooltipText", true)!;
        var clearShared = textType.GetMethod("ClearPanelCache", all)!;
        timer.Restart();
        for (var i = 0; i < 20; i++) { cache.Clear(); clearShared.Invoke(null, null); hover.Invoke(panel, args); }
        var cold = timer.Elapsed.TotalMilliseconds / 20;
        metric.Available = false;
        if (ReferenceEquals(first, hover.Invoke(panel, args))) throw new Exception("Availability change retained stale tooltip text.");
        var refresh = type.GetMethod("RefreshCurrent", all)!;
        cache.Clear(); timer.Restart();
        for (var i = 0; i < 1000; i++) refresh.Invoke(panel, null);
        var closed = timer.Elapsed.TotalMilliseconds / 1000;
        if (cache.Count != 0) throw new Exception("Closed character panel refreshed tooltips.");
        var report = $"Tooltip source per call: cold {cold:0.0000} ms, cached {warm:0.0000} ms; closed refresh {closed:0.0000} ms. Hidden source updates do not wrap Cairo text. Isolated adapter microbenchmark, not game FPS.";
        File.WriteAllText(TestArtifacts.FilePath("panel-performance.txt"), report);
        Console.WriteLine("PASS tooltip cache reuse/invalidation and closed-panel skip; " + report);
        ((IDisposable)panel).Dispose();
    }
}
