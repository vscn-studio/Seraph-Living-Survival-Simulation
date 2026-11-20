using System.Text.Json;
using Vintagestory.API.Common;
using Vintagestory.GameContent;

namespace Seraph.Server;

/// <summary>Keep the selected ingredients for shared mod outputs instead of upgrading them to an average recipe.</summary>
internal static class CraftedFoodNutrition
{
    internal const string Key = "vsslssmod:food-composition-v1";
    internal static bool Supported(string code) => code.StartsWith("pemmican:", StringComparison.Ordinal)
        && code.Split(':')[1].Split('-')[0] is "nutribar" or "caramel" or "pemmican" or "kebab" or "mushroompate" or "mushroompatebar" or "dough" or "bread" or "rusks"
        || code.StartsWith("butchering:sausage-", StringComparison.Ordinal)
            && code.Split('-') is [_, "bloodsausage" or "blackpudding", "raw" or "cooked" or "smoked"];

    internal static FoodProfile? Read(ItemStack stack)
    {
        var text = stack.Attributes.GetString(Key);
        if (text is null || text.Length > 12000) return null;
        try
        {
            var p = JsonSerializer.Deserialize<FoodProfile>(text);
            if (p is null || p.Codes is null || p.Per100G is null || p.MicroPer100G is null || p.Codes.Length != 1 || p.Codes[0] != stack.Collectible.Code.ToString()
                || p.PortionG is <= 0 or > 5000 || !double.IsFinite(p.PortionG)
                || p.WaterGPer100G is < 0 or > 100 || !double.IsFinite(p.WaterGPer100G)
                || p.Per100G.Values.Concat(p.MicroPer100G.Values).Any(v => !double.IsFinite(v) || v < 0)
                || !FoodMicronutrients.ValidReserveScale(p.MicroReserveScale)
                || !double.IsFinite(p.SodiumMgPer100G) || p.SodiumMgPer100G < 0
                || !double.IsFinite(p.PotassiumMgPer100G) || p.PotassiumMgPer100G < 0
                || FoodMicronutrients.Daily.Keys.Any(k => !p.MicroPer100G.ContainsKey(k))) return null;
            return p;
        }
        catch (JsonException) { return null; }
    }

    internal static void Capture(FoodCatalog catalog, ItemSlot[] slots, ItemStack output, IRecipeBase recipe)
    {
        if (!Supported(output.Collectible.Code.ToString()) || recipe is not GridRecipe grid || output.StackSize <= 0) return;
        var ingredients = recipe.RecipeIngredients.ToArray();
        if (ingredients.Length == 0 || ingredients.Any(i => i is null)) return;
        var remaining = ingredients.Select(i => i.ConsumeProperties.Consume ? i.Quantity : 0).ToArray();
        var parts = new List<(FoodProfile Profile, double Mass)>();
        foreach (var slot in slots)
        {
            if (slot.Itemstack is not { } input) continue;
            var available = input.StackSize;
            for (var i = 0; i < remaining.Length && available > 0; i++)
            {
                if (remaining[i] <= 0 || !ingredients[i].SatisfiesAsIngredient(input)) continue;
                var used = Math.Min(remaining[i], available);
                if (catalog.Find(input) is { } p) parts.Add((p, used * p.PortionG));
                else if (input.Collectible is BlockLiquidContainerBase container)
                {
                    var liquid = container.GetContent(input);
                    var props = grid.Attributes?["liquidContainerProps"];
                    var litres = props?["requiresLitres"].AsDouble() ?? 0;
                    if (litres <= 0) litres = ingredients[i].RecipeAttributes?["requiresLitres"].AsDouble() ?? 0;
                    if (liquid is not null && catalog.Find(liquid) is { } lp && litres > 0)
                        parts.Add((lp, litres * 1000 * lp.Density));
                    else if (liquid?.Collectible.Code.ToString() == "game:waterportion")
                    {
                        // Native dough specifies liquid consumption on the resolved ingredient.
                        if (litres <= 0) return;
                        parts.Add((new FoodProfile { PortionG = 1000, WaterGPer100G = 100,
                            MicroPer100G = FoodMicronutrients.Daily.Keys.ToDictionary(k => k, _ => 0d) }, litres * 1000));
                    }
                    else return; // Never silently omit an unrecognized liquid ingredient.
                }
                else if (input.Collectible.Code.ToString() == "game:salt")
                {
                    // Table salt is approximately 39.3% sodium by mass.
                    // Keep it in the physical sodium ledger, not a synthetic dose.
                    parts.Add((new FoodProfile { PortionG = 20, SodiumMgPer100G = 39300,
                        MicroPer100G = FoodMicronutrients.Daily.Keys.ToDictionary(k => k, _ => 0d) }, used * 20));
                }
                else if (input.Collectible.Code.ToString() is not ("game:stick" or "game:paper-parchment")) return;
                available -= used; remaining[i] -= used;
            }
        }
        if (remaining.Any(n => n > 0) || parts.Count == 0) return;
        if (catalog.Find(output) is not { } target) return;
        Write(output, Combine(output.Collectible.Code.ToString(), parts, output.StackSize, target.Preparation));
    }

    internal static FoodProfile Combine(string code, IReadOnlyList<(FoodProfile Profile, double Mass)> parts, int count, string preparation)
    {
        var mass = parts.Sum(p => p.Mass);
        return new FoodProfile {
            Codes = [code], Source = "Actual consumed crafting ingredients", Coverage = "recipe", Confidence = "medium",
            PortionBasis = "Actual ingredient amounts divided by output count; tools and packaging excluded",
            Preparation = preparation, PortionG = mass / count,
            MicroReserveScale = FoodMicronutrients.CombineReserveScales(parts),
            WaterGPer100G = parts.Sum(p => p.Profile.WaterGPer100G * p.Mass) / mass,
            SodiumMgPer100G = parts.Sum(p => p.Profile.SodiumMgPer100G * p.Mass) / mass,
            PotassiumMgPer100G = parts.Sum(p => p.Profile.PotassiumMgPer100G * p.Mass) / mass,
            Per100G = parts.SelectMany(p => p.Profile.Per100G.Keys).Distinct().Where(k => k != "electrolytes").ToDictionary(k => k,
                k => parts.Sum(p => p.Profile.Per100G.GetValueOrDefault(k) * p.Mass) / mass),
            MicroPer100G = FoodMicronutrients.Daily.Keys.ToDictionary(k => k,
                k => parts.Sum(p => p.Profile.MicroPer100G.GetValueOrDefault(k) * p.Mass) / mass)
        };
    }

    internal static void Transfer(FoodCatalog catalog, ItemStack input, ItemStack output)
    {
        if (Read(input) is not { } source || Read(output) is not null || !Supported(output.Collectible.Code.ToString())
            || catalog.Find(output) is not { } target) return;
        // The supported preserve/bake transitions are one input item per output.
        var moisture = Math.Min(source.WaterGPer100G, target.WaterGPer100G);
        var mass = source.PortionG * (100 - source.WaterGPer100G) / (100 - moisture);
        var heat = source.Preparation is "raw" or "dough" or "prepared" or "as-served";
        var ratio = source.PortionG / mass;
        source.Codes = [output.Collectible.Code.ToString()]; source.PortionG = mass;
        source.WaterGPer100G = moisture; source.Preparation = target.Preparation;
        source.SodiumMgPer100G *= ratio; source.PotassiumMgPer100G *= ratio;
        source.Per100G = source.Per100G.ToDictionary(p => p.Key, p => p.Value * ratio);
        source.MicroPer100G = source.MicroPer100G.ToDictionary(p => p.Key,
            p => p.Value * ratio * (heat ? FoodMicronutrients.CookingRetention[p.Key] : 1));
        Write(output, source);
    }

    private static void Write(ItemStack stack, FoodProfile profile)
    {
        profile.EnergyKJPer100G = FoodGroupFallback.EnergyPer100G(profile);
        stack.Attributes.SetString(Key, JsonSerializer.Serialize(profile));
    }
}
