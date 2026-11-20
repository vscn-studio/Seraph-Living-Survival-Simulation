namespace Seraph.Core.Framework;

/// <summary>
/// Reserve-dependent recovery and support.
/// These are gameplay abstractions, not clinical bioavailability values.
/// </summary>
public static class NutrientInteractions
{
    // Compatibility entry: physical food composition is already absorbable.
    public static double AbsorptionFactor(IReadOnlyList<double> pending, int nutrientIndex, NutritionState? nutrition = null) => 1;

    public static double RecoveryMultiplier(NutritionState nutrition)
    {
        return 1 - .5 * MicroNutrition.Burden(nutrition, "iron");
    }

    public static double FatigueMultiplier(NutritionState nutrition) => 1;

    /// <summary>Known reserves provide up to 20% additional wound bleeding reduction.</summary>
    public static double BleedingMultiplier(NutritionState nutrition)
        => 1 - .2 * Support(nutrition, "protein", "calcium", "c", "zinc");

    /// <summary>Up to 20% extra stamina capacity from balanced available reserves.</summary>
    public static double StaminaBonus(NutritionState nutrition)
    {
        double Reserve(string id)
        {
            var status = nutrition.Get(id);
            return status.Known && status.DailyBudget > 0
                ? Math.Clamp(status.Reserve / status.DailyBudget, 0, 1) : 0;
        }
        return .05 * (Reserve("carbohydrate") + Reserve("protein") + Reserve("fat") + Reserve("electrolytes"));
    }

    /// <summary>
    /// Bounded support for intestinal water uptake. Electrolytes and available
    /// carbohydrate improve retention, while only severe protein/fat deficiency
    /// adds a small burden. This keeps nutrition relevant without making every
    /// micronutrient a direct hydration meter.
    /// </summary>
    public static double WaterAbsorptionSupport(NutritionState nutrition)
    {
        var electrolyte = nutrition.Get("electrolytes").Sufficiency;
        var carbohydrate = nutrition.Get("carbohydrate").Sufficiency;
        var proteinBurden = Burden(nutrition, "protein");
        var fatBurden = Burden(nutrition, "fat");
        return Math.Clamp(.88 + .12 * electrolyte + .06 * carbohydrate
            - .03 * Math.Max(proteinBurden, fatBurden), .7, 1);
    }

    /// <summary>Legacy compatibility hook; inflammation is no longer a tracked nutrient trend.</summary>
    public static double InflammationTrend(NutritionState nutrition)
        => 0;

    public static double Burden(NutritionState nutrition, string id)
    {
        var nutrient = nutrition.Get(id);
        return nutrient.Known ? NutritionBands.DeficiencyEffect(nutrient.Deficiency) : 0;
    }

    /// <summary>Independent nutrient support; each observed nutrient contributes at most its own share.</summary>
    public static double Support(NutritionState nutrition, params ReadOnlySpan<string> ids)
    {
        if (ids.Length == 0) return 0;
        var support = 0.0;
        foreach (var id in ids)
        {
            var status = nutrition.Get(id);
            support += status.Known ? status.Sufficiency * (1 - Burden(nutrition, id)) : 0;
        }
        return support / ids.Length;
    }

}
