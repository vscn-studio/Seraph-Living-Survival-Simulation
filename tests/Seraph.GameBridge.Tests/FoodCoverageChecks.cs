using System.Reflection;
using System.Text.Json;
using Vintagestory.API.Common;
using Vintagestory.API.Datastructures;
using Vintagestory.API.Server;

internal static class FoodCoverageChecks
{
    internal static void Run(Assembly mod)
    {
        var path = TestArtifacts.DirectoryPath("food-coverage-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(path);
        Item Item(string code, string? attributes = null, float? satiety = null) => new() {
            Code = new(code), Attributes = attributes is null ? null : JsonObject.FromJson(attributes),
            NutritionProps = satiety is { } value ? new() { FoodCategory = EnumFoodCategory.Vegetable, Satiety = value } : null
        };
        var items = new[] {
            Item("test:tool", "{}"), Item("test:plain-tool"),
            Item("test:null-ingredient", """{"nutritionPropsWhenInMeal":null}"""),
            Item("test:food", satiety: 100),
            Item("test:meal-only", """{"nutritionPropsWhenInMeal":{"satiety":40,"foodcategory":"Vegetable"}}"""),
            Item("test:liquid-only-in-meal", """{"waterTightContainerProps":{"itemsPerLitre":100,"nutritionPropsPerLitreWhenInMeal":{"satiety":40,"foodcategory":"Dairy"}}}"""),
            Item("test:meal-container", """{"mealContainer":true}"""),
            Item("test:no-satiety-food", satiety: 0)
        };
        var warnings = new List<string>();
        var logger = (ILogger)RoutingProxy.Make(typeof(ILogger), (m, a) => {
            if (m.Name == "Warning") warnings.Add(string.Join("; ", a?.Select(x => x is object[] values
                ? string.Join(", ", values) : x?.ToString()) ?? []));
            return null;
        });
        var world = (IServerWorldAccessor)RoutingProxy.Make(typeof(IServerWorldAccessor), (m, _) => m.Name switch {
            "get_Items" => items, "get_Blocks" => Array.Empty<Block>(), _ => null
        });
        var api = (ICoreServerAPI)RoutingProxy.Make(typeof(ICoreServerAPI), (m, _) => m.Name switch {
            "get_World" => world, "get_Logger" => logger, "GetOrCreateDataPath" => path, _ => null
        });
        try
        {
            var type = mod.GetType("Seraph.Server.ServerCoordinator", true)!;
            var coordinator = Activator.CreateInstance(type, [api])!;
            type.GetField("foods", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(coordinator, new Seraph.Server.FoodCatalog());
            type.GetMethod("ExportFoodCoverage", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(coordinator, null);
            if (warnings.Count != 0) throw new Exception("Runtime food coverage export failed: " + string.Join(", ", warnings));
            using var report = JsonDocument.Parse(File.ReadAllText(Path.Combine(path, "food-runtime-coverage.json")));
            var rows = report.RootElement.GetProperty("Foods").EnumerateArray()
                .ToDictionary(x => x.GetProperty("Code").GetString()!, x => x);
            if (rows.Count != 5 || rows.Keys.Any(k => k.EndsWith("tool") || k.EndsWith("null-ingredient")))
                throw new Exception("Missing JsonObject wrapper was reported as food metadata.");
            foreach (var code in new[] { "test:food", "test:meal-only", "test:liquid-only-in-meal" })
                if (rows[code].GetProperty("Route").GetString() != "unreviewed-fallback")
                    throw new Exception("Food report missed native or meal-only intake: " + code);
            if (rows["test:meal-container"].GetProperty("Route").GetString() != "actual-ingredients-at-consumption"
                || rows["test:no-satiety-food"].GetProperty("Route").GetString() != "unsupported-or-dynamic")
                throw new Exception("Food report hid dynamic meal or unsupported nutrition.");
        }
        finally
        {
            var report = Path.Combine(path, "food-runtime-coverage.json");
            if (File.Exists(report)) File.Delete(report);
            Directory.Delete(path);
        }
        Console.WriteLine("PASS runtime food inventory excludes absent/null metadata and resolves meal-only ingredients");
    }
}
