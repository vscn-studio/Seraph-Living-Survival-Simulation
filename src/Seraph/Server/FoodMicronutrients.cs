namespace Seraph.Server;

/// <summary>New intake only: physical daily fractions converted into nutrition reserve points.</summary>
public static class FoodMicronutrients
{
    // Seraph retention parameters for cooking without a matched cooked record.
    // Apply to amounts once; broth retains minerals. These are model coefficients,
    // not measured game yields. Keep aligned with scripts/food_processing.py.
    public static readonly IReadOnlyDictionary<string, double> CookingRetention = new Dictionary<string, double>
    {
        ["calciumMg"] = 1, ["ironMg"] = 1, ["zincMg"] = 1, ["vitaminAUgRAE"] = .8, ["vitaminCMg"] = .5,
        ["b1Mg"] = .7, ["b6Mg"] = .8, ["folateUgDFE"] = .7, ["b12Ug"] = .8
    };
    public const double DailyInputPoints = Seraph.Core.Framework.MicroNutrition.DailyPoints;
    public static bool ValidReserveScale(IReadOnlyDictionary<string, double>? scales) => scales is not null
        && scales.All(p => Daily.ContainsKey(p.Key) && double.IsFinite(p.Value) && p.Value is >= 0 and <= 1);

    internal static Dictionary<string, double> CombineReserveScales(IReadOnlyList<(FoodProfile Profile, double Mass)> parts)
        => parts.SelectMany(p => p.Profile.MicroReserveScale.Keys).Distinct().ToDictionary(key => key, key => {
            var physical = parts.Sum(p => p.Profile.MicroPer100G.GetValueOrDefault(key) * p.Mass);
            var effective = parts.Sum(p => p.Profile.MicroPer100G.GetValueOrDefault(key) * p.Mass
                * p.Profile.MicroReserveScale.GetValueOrDefault(key, 1));
            return physical > 0 ? Math.Clamp(effective / physical, 0, 1) : 1;
        });
    public static readonly IReadOnlyDictionary<string, double> Daily = new Dictionary<string, double>
    {
        ["calciumMg"] = 1000, ["ironMg"] = 12, ["zincMg"] = 11, ["vitaminAUgRAE"] = 900, ["vitaminCMg"] = 90,
        ["b1Mg"] = 1.2, ["b6Mg"] = 1.7, ["folateUgDFE"] = 400, ["b12Ug"] = 2.4
    };
    public static double? PointsPer100G(IReadOnlyDictionary<string, double> values, string id)
    {
        if (id == "bcomplex")
        {
            string[] keys = ["b1Mg", "b6Mg", "folateUgDFE", "b12Ug"];
            return keys.All(values.ContainsKey) ? keys.Sum(k => values[k] / Daily[k]) / 4 * Seraph.Core.Framework.MicroNutrition.DailyPointsFor(id) : null;
        }
        var key = id switch { "calcium" => "calciumMg", "iron" => "ironMg", "zinc" => "zincMg", "a" => "vitaminAUgRAE", "c" => "vitaminCMg", _ => "" };
        return values.TryGetValue(key, out var amount) ? amount / Daily[key] * Seraph.Core.Framework.MicroNutrition.DailyPointsFor(id) : null;
    }
}
