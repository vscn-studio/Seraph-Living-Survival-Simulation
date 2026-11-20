using System.Text.Json;
using Seraph.Core.Framework;
using Vintagestory.API.Common;

namespace Seraph.Server;

internal static class FoodGroupFallback
{
    // Public-data references for explicitly labelled fallback composition.
    internal static Dictionary<string, FoodProfile> Defaults()
    {
        using var stream = typeof(FoodCatalog).Assembly.GetManifestResourceStream("Seraph.FoodDefaults")!;
        using var json = JsonDocument.Parse(stream);
        return json.RootElement.GetProperty("CategoryBaselines").Deserialize<Dictionary<string, FoodProfile>>()!;
    }

    internal static Dictionary<string, string> Provenance()
    {
        using var stream = typeof(FoodCatalog).Assembly.GetManifestResourceStream("Seraph.FoodDefaults")!;
        using var json = JsonDocument.Parse(stream);
        return json.RootElement.GetProperty("DataSource").Deserialize<Dictionary<string, string>>()!;
    }

    internal static FoodProfile? Profile(FoodNutritionProperties? props, bool litres,
        IReadOnlyDictionary<string, FoodProfile> baselines)
    {
        if (props is null || !float.IsFinite(props.Satiety) || props.Satiety <= 0) return null;
        if (baselines.TryGetValue(props.FoodCategory.ToString(), out var baseline)) return baseline;
        // Unknown categories carry satiety-derived carbohydrate energy only. There is
        // no evidence for water, protein, fat or micronutrients in this receipt.
        return new() { PortionG = props.Satiety / 4d, PortionMethod = "explicit",
            Source = "Seraph satiety calibration: 1 sat = 1 kcal", Confidence = "low",
            Density = litres ? props.Satiety / 4000d : 1,
            EnergyKJPer100G = 400 * 4.184, Per100G = new() { ["carbohydrate"] = 100 } };
    }

    internal static double EnergyPer100G(FoodProfile profile) =>
        profile.Per100G.GetValueOrDefault("carbohydrate") * DietaryMetabolism.CarbKJPerG
        + profile.Per100G.GetValueOrDefault("protein") * DietaryMetabolism.ProteinKJPerG
        + profile.Per100G.GetValueOrDefault("fat") * DietaryMetabolism.FatKJPerG;
}
