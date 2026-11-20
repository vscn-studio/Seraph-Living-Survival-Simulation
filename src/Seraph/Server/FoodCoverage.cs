using System.Text.Json;
using Vintagestory.API.Common;
using Vintagestory.GameContent;

namespace Seraph.Server;

internal sealed partial class ServerCoordinator
{
    // Run after patches and code-generated variants are registered; never scan world chunks.
    private void ExportFoodCoverage()
    {
        try
        {
            var rows = new List<object>();
            foreach (var c in api.World.Items.Cast<CollectibleObject>().Concat(api.World.Blocks))
            {
                if (c?.Code is null) continue;
                try
                {
                    var stack = new ItemStack(c);
                    var props = c.GetNutritionProperties(api.World, stack, null);
                    var liquid = BlockLiquidContainerBase.GetContainableProps(stack);
                    var meal = c is BlockMeal or BlockPie || c.Attributes?["mealContainer"]?.AsBool() == true;
                    var mealProps = c.Attributes?["nutritionPropsWhenInMeal"]?.Exists == true
                        ? c.Attributes["nutritionPropsWhenInMeal"].AsObject<FoodNutritionProperties>() : null;
                    if (props is null && liquid?.NutritionPropsPerLitre is null && !meal
                        && mealProps is null && liquid?.NutritionPropsPerLitreWhenInMeal is null) continue;
                    var mapped = foods.Find(stack);
                    var mealOnly = props is null && liquid?.NutritionPropsPerLitre is null
                        && (mealProps is not null || liquid?.NutritionPropsPerLitreWhenInMeal is not null);
                    var dose = foods.Composition(stack, 1, liquid is not null, api.World, inMeal: mealOnly);
                    rows.Add(new { Code = c.Code.ToString(), Class = c.GetType().FullName,
                        Route = foods.Excluded(c.Code.ToString()) ? "excluded-nonfood"
                            : meal ? "actual-ingredients-at-consumption" : mapped is not null ? mapped.Coverage
                            : dose.Mapped && foods.ReviewedFallback(c.Code.ToString()) ? "reviewed-fallback"
                            : dose.Mapped ? "unreviewed-fallback" : "unsupported-or-dynamic",
                        Reason = foods.Exclusions.GetValueOrDefault(c.Code.ToString()),
                        ResolvedMassG = dose.MassG, ResolvedEnergyKJ = dose.EnergyKJ,
                        PortionBasis = mapped?.PortionBasis,
                        Source = mapped?.Source, Preparation = mapped?.Preparation,
                        PhysicalMicronutrients = mapped?.MicroPer100G.Keys.ToArray() ?? [],
                        KnownReserves = new[] { "calcium", "iron", "zinc", "a", "c", "bcomplex" }
                            .Where(id => dose.Known[Core.Framework.Nutrients.IndexOf(id)]).ToArray(),
                        ItemsPerLitre = liquid?.ItemsPerLitre,
                        PressLitresPerItem = c.Attributes?["juiceableProperties"]?["litresPerItem"].AsDouble(),
                        PressLiquid = c.Attributes?["juiceableProperties"]?["liquidStack"]?["code"].AsString(),
                        NativeSatiety = props?.Satiety, LiquidSatietyPerLitre = liquid?.NutritionPropsPerLitre?.Satiety,
                        MealIngredientSatiety = mealProps?.Satiety,
                        LiquidMealSatietyPerLitre = liquid?.NutritionPropsPerLitreWhenInMeal?.Satiety,
                        PortionG = mapped?.PortionG, Density = mapped?.Density });
                }
                catch (Exception ex) { rows.Add(new { Code = c.Code.ToString(), Route = "runtime-review", Error = ex.GetType().Name }); }
            }
            var path = Path.Combine(api.GetOrCreateDataPath("vsslssmod"), "food-runtime-coverage.json");
            File.WriteAllText(path, JsonSerializer.Serialize(new { Version = 1, CatalogVersion = foods.Version,
                Scope = "Loaded collectible defaults after patches; dynamic stack ingredients still require consumption testing.",
                Foods = rows,
                AnimalMeatYields = (api.World.EntityTypes ?? []).Select(e => new { Entity = e,
                    Harvest = e.Server?.BehaviorsAsJsonObj?.FirstOrDefault(b => b["code"].AsString() == "harvestable") })
                    .Where(e => e.Harvest?["seraphMeatYield"].AsBool() == true)
                    .Select(e => new { Code = e.Entity.Code.ToString(), LiveMassKg = e.Entity.Weight,
                        Drops = System.Text.Json.JsonSerializer.Deserialize<object>(e.Harvest!["drops"].ToString()!) }).ToArray()
            }, new JsonSerializerOptions { WriteIndented = true }));
            api.Logger.Notification("[vsslssmod] Runtime food coverage exported: {0} collectible defaults, {1}", rows.Count, path);
        }
        catch (Exception ex) { api.Logger.Warning("[vsslssmod] Runtime food coverage export failed: {0}", ex.Message); }
    }
}
