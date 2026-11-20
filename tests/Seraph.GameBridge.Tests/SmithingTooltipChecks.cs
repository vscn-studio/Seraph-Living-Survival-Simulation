using System.Reflection;
using System.Runtime.Loader;
using System.Text;
using System.Text.RegularExpressions;
using HarmonyLib;
using Seraph.Client;
using Vintagestory.API.Client;
using Vintagestory.API.Common;
using Vintagestory.API.Config;
using Vintagestory.API.Util;

internal static class SmithingTooltipChecks
{
    private static void Require(bool condition, string message) { if (!condition) throw new Exception(message); }

    internal static void Run(string assemblyPath)
    {
        const BindingFlags flags = BindingFlags.NonPublic | BindingFlags.Instance;
        var translation = new TranslationService("en", null!);
        typeof(TranslationService).GetField("entryCache", flags)!.SetValue(translation,
            new Dictionary<string, string> { ["Repaired {0} times"] = "Repaired {0} times", ["Last repaired by {0}"] = "Last repaired by {0}" });
        typeof(TranslationService).GetField("loaded", flags)!.SetValue(translation, true);
        Lang.AvailableLanguages["en"] = translation; Lang.ChangeLanguage("en");
        var smithing = AssemblyLoadContext.Default.LoadFromAssemblyPath(Path.GetFullPath(assemblyPath));
        var behaviorType = smithing.GetType("SmithingPlus.ToolRecovery.CollectibleBehaviorRepairableTool", true)!;
        var configType = smithing.GetType("SmithingPlus.Config.ServerConfig", true)!;
        var config = Activator.CreateInstance(configType)!;
        smithing.GetType("SmithingPlus.Config.ConfigLoader", true)!.GetProperty("Config")!.SetValue(null, config);
        var selectorProperty = configType.GetProperty("RepairableToolSelector")!;
        var originalSelector = (string)selectorProperty.GetValue(config)!;
        configType.GetProperty("ShowRepairSmithName")!.SetValue(config, true);
        var target = behaviorType.GetMethod("GetHeldItemInfo")!;
        var warnings = 0;
        static object? Default(MethodInfo m) => m.ReturnType == typeof(void) ? null
            : m.ReturnType.IsValueType ? Activator.CreateInstance(m.ReturnType) : null;
        var logger = RoutingProxy.Make(typeof(ILogger), (m, _) => { if (m.Name == "Warning") warnings++; return Default(m); });
        var api = (ICoreClientAPI)RoutingProxy.Make(typeof(ICoreClientAPI), (m, _) => m.Name == "get_Logger" ? logger : Default(m));

        string Describe(string code, int repairs)
        {
            var item = new Item { Code = new AssetLocation(code) };
            var stack = new ItemStack(item);
            stack.Attributes.SetInt("brokenCount", repairs); stack.Attributes.SetString("repairSmith", "Test Smith");
            var behavior = (CollectibleBehavior)Activator.CreateInstance(behaviorType, item)!;
            var result = new StringBuilder("Base description\n");
            behavior.GetHeldItemInfo(new DummySlot(stack), result, null!, false);
            return result.ToString();
        }

        var expected = Describe("game:pickaxe-iron", 3);
        Require(expected.Contains("3") && expected.Contains("Test Smith"), "Original repair history fixture is incomplete");
        var pathologicalCode = "game:" + new string('a', 20000) + "b";
        selectorProperty.SetValue(config, "@game:(a+)+");
        try { Describe(pathologicalCode, 0); throw new Exception("Expected original native regex timeout"); }
        catch (RegexMatchTimeoutException) { Console.WriteLine("PASS reproduced SmithingPlus tooltip regex timeout on actual installed assembly"); }

        using (var compatibility = new SmithingTooltipCompatibility(api))
        {
            Require(Harmony.GetPatchInfo(target)?.Transpilers.Any(p => p.owner == SmithingTooltipCompatibility.HarmonyId) == true,
                "Actual SmithingPlus tooltip was not patched");
            Require(Describe(pathologicalCode, 0) == "Base description\n", "Pathological selector produced a false match");
            selectorProperty.SetValue(config, originalSelector);
            Require(Describe("game:pickaxe-iron", 3) == expected, "Repair count or smith name changed");
            Require(Describe("game:pickaxe-iron", 0) == "Base description\n", "Unrepaired tool gained history");
            Require(Describe("game:stone-granite", 3) == "Base description\n", "Non-tool gained repair history");
            string[] selectors = [originalSelector, "game:pickaxe-*", "@game:(pickaxe|axe)-.*", "@game:(?=pickaxe).*", "", "@game:(.*)\\1"];
            string[] codes = ["game:pickaxe-iron", "game:stone-granite", "mod:crossbow-wood", "game:axe-copper", "GAME:AXE-IRON", "game:abcabc"];
            foreach (var selector in selectors)
            foreach (var code in codes)
                Require(SmithingTooltipCompatibility.Match(selector, code) == WildcardUtil.Match(selector, code),
                    "Selector semantics changed: " + selector + " / " + code);
            var started = System.Diagnostics.Stopwatch.StartNew();
            Parallel.For(0, 40000, i => Require(Describe(i % 2 == 0 ? "game:pickaxe-iron" : "game:stone-granite", 0)
                == "Base description\n", "Concurrent search cache changed description"));
            Console.WriteLine($"PASS 40000 concurrent actual tooltip calls in {started.Elapsed.TotalSeconds:0.00}s");
            // Unsupported custom regex syntax falls back with the native timeout;
            // a failed tooltip may not terminate the inventory worker thread.
            const string advancedSlow = "@(?=game:)game:(a+)+";
            Require(!SmithingTooltipCompatibility.Match(advancedSlow, pathologicalCode), "Timed-out custom selector matched");
            Require(!SmithingTooltipCompatibility.Match("@[", "game:axe")
                && !SmithingTooltipCompatibility.Match("@[", "game:axe") && warnings == 2, "Failure logging is not once per selector");
            Require(!Harmony.GetPatchInfo(AccessTools.Method(typeof(WildcardUtil), nameof(WildcardUtil.Match),
                [typeof(string), typeof(string)]))?.Owners.Contains(SmithingTooltipCompatibility.HarmonyId) ?? true,
                "Compatibility changed global matching");
        }
        Require(Harmony.GetPatchInfo(target)?.Owners.Contains(SmithingTooltipCompatibility.HarmonyId) != true, "Tooltip patch survived disposal");
        Console.WriteLine("PASS exact native selector semantics, non-backtracking timeout reproduction, repaired history, custom selector fallback, worker concurrency, scoped patch and disposal");
    }
}
