using Seraph;
using Vintagestory.API.Common;

internal static class SlowCallbackChecks
{
    internal static void Run()
    {
        var warnings = new List<object?[]>();
        var logger = (ILogger)RoutingProxy.Make(typeof(ILogger), (m, a) => {
            if (m.Name == "Warning") warnings.Add((object?[])a![1]!);
            return null;
        });
        var api = (ICoreAPI)RoutingProxy.Make(typeof(ICoreAPI), (m, _) =>
            m.Name == "get_Logger" ? logger : throw new Exception("Unexpected API access: " + m.Name));
        var probe = new SlowCallbackProbe("test.callback");
        void Require(bool condition, string message) { if (!condition) throw new Exception(message); }
        // Long gaps, including paused games, are not callback duration.
        probe.Record(api, 1, 0);
        probe.Record(api, 1, 100000);
        probe.Record(api, double.NaN, 110000);
        Require(warnings.Count == 0, "Callback gaps or invalid values generated slow warnings.");
        probe.Record(api, 10000, 120000);
        for (var i = 1; i <= 20; i++) probe.Record(api, 500 + i, 120000 + i);
        Require(warnings.Count == 1, "Repeated slow callbacks flooded the log.");
        probe.Record(api, 300, 150000);
        Require(warnings.Count == 2 && (int)warnings[1][2]! == 20 && (double)warnings[1][3]! == 520,
            "Throttled slow work lost its count or peak duration.");
        probe.Record(api, 300, 180000);
        Require((int)warnings[2][2]! == 0 && (double)warnings[2][3]! == 0, "Previous peaks were not reset.");
        // Normal measurements must not allocate or call the game/logger at all.
        using (probe.Measure(api)) { }
        var before = GC.GetAllocatedBytesForCurrentThread();
        for (var i = 0; i < 1000; i++) { using var measurement = probe.Measure(api); }
        Require(GC.GetAllocatedBytesForCurrentThread() == before, "Fast callback measurements allocated.");
        var responsive = new SlowCallbackProbe("test.short-stall", 50);
        var previousCount = warnings.Count;
        responsive.Record(api, 49, 200000);
        responsive.Record(api, 50, 200001);
        Require(warnings.Count == previousCount + 1, "Short-stall probe ignored its configured threshold.");
        Console.WriteLine("PASS slow callback timing: pause gaps excluded, warnings throttled with peaks, normal path allocation-free");
    }
}
