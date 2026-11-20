using Seraph.Core.Framework;
using Vintagestory.API.Common;
using Vintagestory.API.Server;
using Vintagestory.API.Common.Entities;
using Vintagestory.GameContent;
using Vintagestory.API.Config;
using Vintagestory.API.MathTools;
using Seraph.World;

namespace Seraph.Server;

public sealed class FoodProfile
{
    public string[] Codes { get; set; } = [];
    /// <summary>Dataset and record provenance; portion calibration is recorded separately.</summary>
    public string Source { get; set; } = "Custom composition";
    public string Confidence { get; set; } = "medium";
    public string Preparation { get; set; } = "as-served";
    [System.Text.Json.Serialization.JsonRequired]
    public string Coverage { get; set; } = "substitution";
    [System.Text.Json.Serialization.JsonRequired]
    public string PortionBasis { get; set; } = "Custom explicit edible portion";
    public string? CookedReference { get; set; }
    public double CookedQuantityScale { get; set; } = 1;
    public double Density { get; set; } = 1;
    public string PortionMethod { get; set; } = "explicit";
    public double PortionG { get; set; }
    public double WaterGPer100G { get; set; }
    public double? AlcoholGPer100G { get; set; }
    public double EnergyKJPer100G { get; set; }
    public double SodiumMgPer100G { get; set; }
    public double PotassiumMgPer100G { get; set; }
    /// <summary>Explicit configured hazard per 100 g; zero adds no generic contamination.</summary>
    public double EntericHazardPer100G { get; set; }
    public Dictionary<string, double> Per100G { get; set; } = [];
    /// <summary>Physical composition per 100 g. Missing values are unknown, explicit zero is measured zero.</summary>
    public Dictionary<string, double> MicroPer100G { get; set; } = [];
    /// <summary>Gameplay reserve yield by physical nutrient key; missing entries mean one.
    /// Mix by nutrient mass and apply only at intake; physical composition stays intact.</summary>
    public Dictionary<string, double> MicroReserveScale { get; set; } = [];
}
public sealed class FoodCatalog
{
    [System.Text.Json.Serialization.JsonRequired]
    public string Format { get; set; } = "seraph.release.foods";
    [System.Text.Json.Serialization.JsonRequired]
    public int Version { get; set; } = 1;
    public Dictionary<string, string> Exclusions { get; set; } = new();
    public Dictionary<string, string> ReviewedFallbacks { get; set; } = new();
    internal bool Excluded(string code) => Exclusions.Keys.Any(pattern => Matches(code, pattern));
    internal bool ReviewedFallback(string code) => ReviewedFallbacks.Keys.Any(pattern => Matches(code, pattern));
    public Dictionary<string, string> DataSource { get; set; } = FoodGroupFallback.Provenance();
    public Dictionary<string, FoodProfile> CategoryBaselines { get; set; } = FoodGroupFallback.Defaults();
    public FoodProfile[] Foods { get; set; } = [];
    internal string? Validate()
    {
        if (Format != "seraph.release.foods" || Version != 1 || Foods is null || Foods.Length > 2048) return "Unsupported or oversized food catalog.";
        if (Exclusions is null || ReviewedFallbacks is null || Exclusions.Count > 256 || ReviewedFallbacks.Count > 2048
            || Exclusions.Concat(ReviewedFallbacks).Any(p => string.IsNullOrWhiteSpace(p.Key) || p.Key.Length > 128
                || string.IsNullOrWhiteSpace(p.Value) || p.Value.Length > 1024)) return "Invalid food coverage rules.";
        if (DataSource is null || DataSource.Count > 32 || DataSource.Any(p => p.Key.Length > 128 || p.Value is null || p.Value.Length > 1024))
            return "Invalid food data provenance.";
        if (CategoryBaselines is null || CategoryBaselines.Count != 5
            || new[] { "Fruit", "Vegetable", "Grain", "Protein", "Dairy" }.Any(k => !CategoryBaselines.ContainsKey(k)))
            return "Missing category reference composition.";
        foreach (var p in Foods.Concat(CategoryBaselines.Values))
            if (p is null || p.Codes is null || p.Codes.Length is 0 or > 64
                || p.Codes.Any(c => string.IsNullOrWhiteSpace(c) || c.Length > 128 || !c.Contains(':'))
                || !double.IsFinite(p.Density) || p.Density is <= 0 or > 5
                || p.PortionMethod is not ("explicit" or "satiety-energy")
                || !double.IsFinite(p.PortionG) || p.PortionG is < 0 or > 5000
                || p.PortionG == 0 && p.PortionMethod == "explicit"
                || !double.IsFinite(p.WaterGPer100G) || p.WaterGPer100G is < 0 or > 100
                || p.AlcoholGPer100G is { } alcohol && (!double.IsFinite(alcohol) || alcohol is < 0 or > 100)
                || !double.IsFinite(p.EnergyKJPer100G) || p.EnergyKJPer100G is < 0 or > 5000
                || !double.IsFinite(p.SodiumMgPer100G) || p.SodiumMgPer100G is < 0 or > 100000
                || !double.IsFinite(p.PotassiumMgPer100G) || p.PotassiumMgPer100G is < 0 or > 100000
                || !double.IsFinite(p.EntericHazardPer100G) || p.EntericHazardPer100G is < 0 or > 100
                || p.Per100G is null || p.Per100G.Any(kv => !Nutrients.All.Any(n => n.Id == kv.Key)
                    || !double.IsFinite(kv.Value) || kv.Value is < 0 or > 1e7)
                || p.MicroPer100G is null || p.MicroPer100G.Any(kv => !FoodMicronutrients.Daily.ContainsKey(kv.Key)
                    || !double.IsFinite(kv.Value) || kv.Value is < 0 or > 1e7)
                || !FoodMicronutrients.ValidReserveScale(p.MicroReserveScale)
                || p.Source is null || p.Source.Length is 0 or > 256
                || !double.IsFinite(p.CookedQuantityScale) || p.CookedQuantityScale is <= 0 or > 100
                || p.Coverage is not ("reference" or "recipe" or "substitution" or "fallback")
                || string.IsNullOrWhiteSpace(p.PortionBasis) || p.PortionBasis.Length > 1024
                || p.Confidence is not ("high" or "medium" or "low")
                || p.Preparation is null || p.Preparation.Length is 0 or > 64) return "Invalid food composition entry.";
        return null;
    }
    internal FoodProfile? Find(ItemStack stack)
    {
        if (CraftedFoodNutrition.Read(stack) is { } crafted) return crafted;
        var code = stack.Collectible.Code.ToString();
        // A concrete code always wins over a family fallback. Longer prefixes win among
        // fallbacks, so e.g. fruit-pineapple is not swallowed by fruit-*.
        return Foods.Select((profile, index) => new { profile, index, score = profile.Codes
                .Where(pattern => Matches(code, pattern))
                .Select(pattern => pattern.EndsWith('*') ? pattern.Length - 1 : 10000 + pattern.Length)
                .DefaultIfEmpty(-1).Max() })
            .Where(x => x.score >= 0)
            .OrderByDescending(x => x.score).ThenBy(x => x.index)
            .Select(x => x.profile).FirstOrDefault();
    }
    internal FoodDose Composition(ItemStack stack, double quantity, bool litres = false,
        IWorldAccessor? world = null, Entity? entity = null, bool inMeal = false, bool cooked = false, bool multi = false)
    {
        var dose = new FoodDose { Code = stack.Collectible.Code.ToString() };
        if (!double.IsFinite(quantity) || quantity <= 0 || quantity > 10000) return dose;
        if (inMeal && dose.Code == "game:waterportion")
        {
            var units = BlockLiquidContainerBase.GetContainableProps(stack)?.ItemsPerLitre ?? 100;
            if (!float.IsFinite(units) || units <= 0) return dose;
            dose.WaterL = litres ? quantity : quantity / units; dose.MassG = dose.WaterL * 1000;
            dose.Mapped = true; dose.Source = "Actual cooking water volume"; return dose;
        }
        if (Excluded(dose.Code)) return dose;
        if (dose.Code.StartsWith("rusticcookery:meatslab-", StringComparison.Ordinal))
        {
            // Verified SlabSlicing contract: six pieces when whole, explicit remaining pieces after cutting.
            var pieces = stack.Attributes.GetInt("pieces", 6);
            if (pieces is < 1 or > 6) throw new InvalidDataException("Invalid meat slab pieces.");
            quantity *= pieces / 6d;
        }
        var native = stack.Collectible.GetNutritionProperties(world!, stack, entity!);
        var liquid = BlockLiquidContainerBase.GetContainableProps(stack);
        dose.PathogenDose = PathogenLoads.Scale(World.PathogenMetadata.Read(stack),
            quantity * (litres && liquid is { ItemsPerLitre: > 0 } ? liquid.ItemsPerLitre : 1));
        if (litres || liquid is not null)
        {
            var perLitre = inMeal ? liquid?.NutritionPropsPerLitreWhenInMeal : null;
            perLitre ??= liquid?.NutritionPropsPerLitre;
            if (perLitre is not null) native = perLitre;
            else if (native is not null && liquid is { ItemsPerLitre: > 0 })
            { native = native.Clone(); native.Satiety *= liquid.ItemsPerLitre; }
        }
        else if (inMeal)
            native = stack.ItemAttributes?["nutritionPropsWhenInMeal"]?.AsObject<FoodNutritionProperties>() ?? native;
        // Native ingestion owns the HP damage. Track only the resulting temporary discomfort here.
        if (stack.Collectible.Code.Domain == "game" && stack.Collectible.Code.Path.StartsWith("mushroom-", StringComparison.Ordinal)
            && native is { Health: < 0 } && float.IsFinite(native.Health))
            dose.PlantPoison = Math.Clamp(-native.Health * quantity / 50, 0, 1);
        var fallback = FoodGroupFallback.Profile(native, litres, CategoryBaselines);
        var mapped = Find(stack);
        var p = mapped ?? fallback;
        if (cooked && p?.CookedReference is { } reference && Foods.FirstOrDefault(f => f.Codes.Contains(reference)) is { } prepared)
        { quantity *= p.CookedQuantityScale; p = prepared; }
        if (p is null) { dose.HasUnmapped = true; return dose; }
        var densityEnergy = FoodGroupFallback.EnergyPer100G(p);
        // Explicit edible servings take precedence; unidentified portions retain 1 satiety = 1 kcal.
        var portion = p.PortionG > 0 ? p.PortionG : native is { Satiety: > 0 } && float.IsFinite(native.Satiety)
            && densityEnergy > 0 ? native.Satiety * 4.184 * 100 / densityEnergy : 0;
        // Unidentified compositions have no measured edible mass/density. Their declared
        // surrogate serving is derived from native energy, including per-litre receipts.
        var mass = mapped is null ? quantity * portion / (!litres && liquid is { ItemsPerLitre: > 0 } ? liquid.ItemsPerLitre : 1)
            : litres ? quantity * 1000 * p.Density
            : liquid is { ItemsPerLitre: > 0 } ? quantity / liquid.ItemsPerLitre * 1000 * p.Density : quantity * portion;
        if (mass <= 0 || !double.IsFinite(mass)) { dose.HasUnmapped = true; return dose; }
        dose.MassG = mass; dose.WaterL = mass * p.WaterGPer100G / 100000; dose.EnergyKJ = mass * p.EnergyKJPer100G / 100;
        dose.AlcoholG = p.AlcoholGPer100G is { } alcohol ? mass * alcohol / 100 : 0;
        // A food category is nutrition evidence, not evidence of infectious raw meat
        // (legumes and other safe protein foods share that native category).
        var animalMeat = stack.Collectible.Code.Path.Split('-').Any(t => t is "meat" or "redmeat" or "bushmeat" or "poultry" or "fish" or "fishchunk");
        var rawMeat = !cooked && (p.Preparation.Equals("raw", StringComparison.OrdinalIgnoreCase)
            || stack.Collectible.Code.Path.Split('-').Contains("raw"))
            && animalMeat;
        if (rawMeat)
        {
            dose.PathogenDose = PathogenLoads.Add(dose.PathogenDose, new Dictionary<string, double> { ["salmonella"] = quantity * .08 });
            if (stack.Collectible.Code.Path.Contains("bushmeat", StringComparison.Ordinal))
                dose.PathogenDose = PathogenLoads.Add(dose.PathogenDose, new Dictionary<string, double> { ["trichinosis"] = quantity * .08 });
        }
        dose.EntericHazard = cooked ? 0 : mass / 100 * p.EntericHazardPer100G;
        if (!inMeal && world is not null)
        {
            dose.Spoilage = SpoilageFraction(world, [stack]);
            dose.EntericHazard += SpoilageHazard(dose.Spoilage);
        }
        dose.Source = p.Source; dose.Confidence = p.Confidence; dose.Preparation = p.Preparation;
        for (var i = 0; i < dose.Nutrients.Length; i++)
        {
            if (Nutrients.All[i].IsMicro || Nutrients.All[i].Id == "electrolytes") continue;
            if (p.Per100G.TryGetValue(Nutrients.All[i].Id, out var value))
            { dose.Nutrients[i] = value * mass / 100; dose.Known[i] = true; }
        }
        var physicalValues = new Dictionary<string, double>(p.MicroPer100G);
        foreach (var key in physicalValues.Keys.ToArray())
            physicalValues[key] *= p.MicroReserveScale.GetValueOrDefault(key, 1);
        // A matched cooked reference already includes preparation. Otherwise retain
        // minerals and model vitamin losses instead of deleting all five reserves.
        if (cooked && p.Preparation is "raw" or "milled" or "dough" or "juice" or "ground" or "pickled" or "soaked")
            foreach (var key in physicalValues.Keys.ToArray())
                physicalValues[key] *= FoodMicronutrients.CookingRetention.GetValueOrDefault(key, 1);
        foreach (var id in new[] { "calcium", "iron", "zinc", "a", "c", "bcomplex" })
        {
            var i = Nutrients.IndexOf(id);
            var physical = mapped is not null
                ? FoodMicronutrients.PointsPer100G(physicalValues, id) : null;
            if (physical is { } points)
            { dose.Nutrients[i] = points * mass / 100; dose.Known[i] = true; }
        }
        var salt = stack.Attributes.GetFloat("vsslssmodSaltGrams", -1);
        if (salt < 0) salt = stack.ItemAttributes?["vsslssmodSaltGrams"]?.AsFloat(-1) ?? -1;
        if (salt < 0 && stack.Collectible.Code.Path.Split('-').Contains("salt")
            && p.SodiumMgPer100G + p.PotassiumMgPer100G <= 0) salt = 20;
        // Cured profiles already carry their declared retained salt allowance. Derive a
        // 2% salt addition only for legacy/custom profiles without sodium data;
        // otherwise the same salt would be counted once in the profile and again
        // from the item code.
        if (salt < 0 && stack.Collectible.Code.Path.Contains("-cured", StringComparison.Ordinal)
            && p.SodiumMgPer100G + p.PotassiumMgPer100G <= 0)
        {
            // `mass` includes the consumed stack quantity; derive salt per item
            // so the cured-food salt fraction is not multiplied twice.
            var itemMass = mass / Math.Max(1, quantity);
            salt = (float)Math.Min(40, itemMass * .02);
        }
        if (salt < 0) salt = 0;
        var hasPhysicalElectrolytes = p.SodiumMgPer100G + p.PotassiumMgPer100G > 0;
        var electrolytes = hasPhysicalElectrolytes
            ? (p.SodiumMgPer100G + p.PotassiumMgPer100G) / 1000 * mass / 100 * .8
            : p.Per100G.GetValueOrDefault("electrolytes") * mass / 100;
        var addedSalt = Math.Max(0, salt) * quantity;
        // Explicit salt metadata describes an additional recipe-level addition;
        // physical sodium/potassium remains the base food composition.
        dose.Nutrients[4] = electrolytes + addedSalt / 2.54 * .8;
        dose.SaltG = addedSalt;
        dose.Known[4] = true;
        // Normalize rounded/custom compositions to physical dry mass before splitting indigestible matter.
        var dry = Math.Max(0, mass - dose.WaterL * 1000);
        var macro = dose.Nutrients.Take(4).Sum();
        var macroScale = macro > dry && macro > 0 ? dry / macro : 1;
        for (var i = 0; i < 4; i++) dose.Nutrients[i] *= macroScale;
        var indigestible = Math.Max(0, dry - dose.Nutrients.Take(3).Sum() - dose.AlcoholG);
        dose.Nutrients[3] = Math.Min(indigestible, dose.Nutrients[3]);
        dose.WasteG = indigestible - dose.Nutrients[3];
        dose.EnergyKJ = dose.Nutrients[0] * DietaryMetabolism.CarbKJPerG + dose.Nutrients[1] * DietaryMetabolism.ProteinKJPerG
            + dose.Nutrients[2] * DietaryMetabolism.FatKJPerG;
        ApplySpoilageRetention(dose);
        dose.Mapped = true; return dose;
    }
    internal FoodDose MealComposition(IWorldAccessor world, ItemStack meal, ItemStack[] contents, bool mulwithStackSize)
    {
        var dose = new FoodDose();
        var ingredients = world.Api.GetCookingRecipe(meal.Attributes.GetString("recipeCode"))?.Ingredients?.Select(i => i.Clone()).ToList();
        var cooked = ingredients is not null || meal.Block is BlockPie && meal.Collectible.LastCodePart() is "perfect" or "charred";
        var retained = RusticFoodBridge.AddRetained(this, world, meal, dose);
        foreach (var stack in retained ? Array.Empty<ItemStack>() : contents)
        {
            if (stack is null) continue;
            var quantity = mulwithStackSize ? stack.StackSize : 1;
            var liquid = BlockLiquidContainerBase.GetContainableProps(stack);
            var test = stack.Clone(); test.StackSize = (int)(liquid?.ItemsPerLitre ?? 1);
            var ingredient = !mulwithStackSize ? ingredients?.FirstOrDefault(i => i.Matches(test)) : null;
            if (ingredient is not null)
            {
                quantity = ingredient.GetMatchingStack(test)?.StackSize ?? 1;
                if (--ingredient.MaxQuantity == 0) ingredients!.Remove(ingredient);
            }
            if (liquid is not null && (!float.IsFinite(liquid.ItemsPerLitre) || liquid.ItemsPerLitre <= 0))
            { dose.HasUnmapped = true; continue; }
            var amount = liquid is null ? quantity : mulwithStackSize ? quantity / (double)liquid.ItemsPerLitre
                : quantity * (ingredient?.PortionSizeLitres ?? 1);
            dose.Add(Composition(stack, amount, liquid is not null, world, inMeal: true, cooked: cooked, multi: cooked && contents.Count(s => s is not null) > 1));
        }
        // New Rustic servings retain the exact flour. Earlier bowls only retained
        // roux presence: use the documented one-flour/six-servings recipe analogue,
        // never convert a configurable satiety bonus into carbohydrate or food mass.
        var retainedRoot = meal.Attributes.GetTreeAttribute(RusticFoodBridge.Key);
        if (retainedRoot?.HasAttribute("Roux") != true
            && meal.Attributes.GetTreeAttribute("rusticcookeryRoux") is { } roux && roux.GetFloat("satiety") > 0)
        {
            var portions = retainedRoot?.GetInt("RouxPortions", 6) ?? 6;
            if (portions <= 0) throw new InvalidDataException("Invalid Rustic roux portion count.");
            var flour = new ItemStack(new Item { Code = new AssetLocation("reference:rustic-roux-flour") });
            dose.Add(Composition(flour, 1d / portions, cooked: true));
        }
        // A cooked dish is the product being eaten; its raw ingredients are not eaten again.
        // Freshly cooked meals/pies have no raw-meat hazard. Spoilage remains a separate risk.
        if (cooked)
        {
            dose.EntericHazard = 0;
            dose.Preparation = "cooked";
        }
        // Native meal health multipliers are not spoilage probabilities (pie returns satiety loss).
        // Observe the final dish's perish transition instead; inspect a clone so a tooltip/preflight
        // cannot turn the player's stack into rot while calculating an intake dose.
        dose.Spoilage = SpoilageFraction(world, contents.Where(s => s is not null).Append(meal));
        dose.EntericHazard += SpoilageHazard(dose.Spoilage);
        ApplySpoilageRetention(dose);
        dose.PathogenDose = PathogenLoads.Add(dose.PathogenDose, World.PathogenMetadata.Read(meal));
        var servingUnits = RusticFoodBridge.ServingUnits(meal);
        if (servingUnits != 1)
        {
            var perNativeServing = new FoodDose(); perNativeServing.Add(dose, 1 / servingUnits);
            return perNativeServing;
        }
        return dose;
    }
    internal static double SpoilageFraction(IWorldAccessor world, IEnumerable<ItemStack> stacks)
    {
        var perishables = stacks
            .Where(s => s.Collectible.GetTransitionableProperties(world, s, null)?.Any(p => p.Type == EnumTransitionType.Perish) == true);
        var spoilage = perishables.Select(s => s.Collectible.UpdateAndGetTransitionState(world,
            new DummySlot(s.Clone()), EnumTransitionType.Perish)?.TransitionLevel ?? 0).DefaultIfEmpty().Max();
        return float.IsFinite(spoilage) ? Math.Clamp(spoilage, 0, 1) : 0;
    }
    // Any measurable spoilage adds a 10% nominal enteric infection chance
    // before the player's immune/barrier modifiers. More advanced spoilage
    // affects nutrient retention and vomiting separately.
    private static double SpoilageHazard(double spoilage)
    {
        return spoilage > 0 ? -Math.Log(.9) : 0;
    }
    private static void ApplySpoilageRetention(FoodDose dose)
    {
        // TransitionLevel is the spoiled fraction: retain the remaining freshness
        // share of modeled nutrients and energy while preserving physical mass/water.
        var retention = 1 - dose.Spoilage;
        dose.EnergyKJ *= retention;
        for (var i = 0; i < dose.Nutrients.Length; i++) dose.Nutrients[i] *= retention;
    }
    private static bool Matches(string code, string pattern)
    {
        // Precise profiles win; the separate native food-group fallback covers other domains.
        return pattern.EndsWith('*') ? code.StartsWith(pattern[..^1], StringComparison.Ordinal) : code == pattern;
    }
}

internal sealed class FoodDose
{
    public double AlcoholG;
    public double SaltG;
    public double WasteG;
    public double MassG;
    public double WaterL;
    public double EnergyKJ;
    public double EntericHazard;
    public double Spoilage;
    public IReadOnlyDictionary<string, double> PathogenDose = PathogenLoads.Empty;
    public double PlantPoison;
    public double[] Nutrients = new double[Core.Framework.Nutrients.All.Length];
    public bool[] Known = new bool[Core.Framework.Nutrients.All.Length];
    public string Code = "";
    public bool Mapped;
    public bool HasUnmapped;
    public string Source = "";
    public string Confidence = "";
    public string Preparation = "";
    internal void ScaleNutrition(double multiplier)
    {
        if (!double.IsFinite(multiplier) || multiplier <= 0) return;
        MassG *= multiplier; WaterL *= multiplier; EnergyKJ *= multiplier; WasteG *= multiplier;
        AlcoholG *= multiplier; SaltG *= multiplier;
        for (var i = 0; i < Nutrients.Length; i++) Nutrients[i] *= multiplier;
    }
    internal void Add(FoodDose other, double multiplier = 1)
    {
        WasteG += other.WasteG * multiplier;
        AlcoholG += other.AlcoholG * multiplier; SaltG += other.SaltG * multiplier;
        MassG += other.MassG * multiplier; WaterL += other.WaterL * multiplier; EnergyKJ += other.EnergyKJ * multiplier;
        EntericHazard += other.EntericHazard * multiplier;
        Spoilage = Math.Max(Spoilage, other.Spoilage);
        PathogenDose = PathogenLoads.Add(PathogenDose, PathogenLoads.Scale(other.PathogenDose, multiplier));
        PlantPoison += other.PlantPoison * multiplier;
        for (var i = 0; i < Nutrients.Length; i++) { Nutrients[i] += other.Nutrients[i] * multiplier; Known[i] |= other.Known[i]; }
        Mapped |= other.Mapped;
        HasUnmapped |= other.HasUnmapped;
        if (string.IsNullOrEmpty(Source)) Source = other.Source;
        if (string.IsNullOrEmpty(Confidence) || Confidence == "low") Confidence = other.Confidence;
        if (string.IsNullOrEmpty(Preparation)) Preparation = other.Preparation;
        Code = other.Code;
    }
}

internal sealed partial class ServerCoordinator
{
    private FoodCatalog foods = null!;
    internal FoodCatalog FoodProfiles => foods;
    private void LoadFoods()
    {
        foods = ReleaseConfigFile.Read<FoodCatalog>(api, "vsslssmod.Foods.json")
            ?? System.Text.Json.JsonSerializer.Deserialize<FoodCatalog>(api.Assets.Get(new AssetLocation("vsslssmod:config/foods.json")).ToText())
            ?? throw new InvalidDataException("Missing food composition catalog.");
        if (foods.Validate() is { } error) throw new InvalidDataException(error);
        if (System.Text.Json.JsonSerializer.Serialize(foods).Length > 1048576) throw new InvalidDataException("Food catalog exceeds network budget.");
        api.Logger.Notification("[vsslssmod] Independent composition catalog loaded: {0} profiles.", foods.Foods.Length);
    }
    internal FoodDose FoodComposition(ItemStack stack, double quantity, bool litres = false)
        => foods.Composition(stack, quantity, litres, api.World);
    internal FoodDose MealComposition(IWorldAccessor world, ItemStack stack, ItemStack[] contents, bool mulwithStackSize)
        => foods.MealComposition(world, stack, contents, mulwithStackSize);
    internal double FoodCapacity(EntityAgent entity, FoodDose dose)
    {
        if (!OwnsPhysiology(entity)) return 1;
        var s = SessionFor(entity);
        if (s.Deceased || !entity.Alive || paused) return 0;
        double ratio = 1;
        if (dose.MassG > 0) ratio = Math.Min(ratio, (DietaryMetabolism.StomachCapacityG - s.Framework.Nutrition.ContentsMassG(s.State.GutWaterL)) / dose.MassG);
        if (dose.EnergyKJ > 0) ratio = Math.Min(ratio, (s.Profile.MaximumGutEnergyKJ - s.State.GutEnergyKJ) / dose.EnergyKJ);
        if (dose.WaterL > 0) ratio = Math.Min(ratio, (s.Profile.MaximumGutWaterL - s.State.GutWaterL) / dose.WaterL);
        return Math.Clamp(ratio, 0, 1);
    }
    internal void CreditSpoilageContact(EntityAgent entity, ItemStack stack)
    {
        if (!OwnsPhysiology(entity) || !entity.Alive || stack.Collectible.GetTransitionableProperties(api.World, stack, null)
            ?.Any(p => p.Type == EnumTransitionType.Perish) != true) return;
        var spoilage = FoodCatalog.SpoilageFraction(api.World, [stack]);
        var gain = SpoilageResponse.DirtinessGain(spoilage, eaten: false);
        if (gain <= 0) return;
        var s = SessionFor(entity);
        var key = "contact:" + stack.Collectible.Code;
        var now = api.World.ElapsedMilliseconds / 1000d;
        if (s.SpoilageEvents.TryGetValue(key, out var last) && now - last < 10 && now >= last) return;
        s.SpoilageEvents[key] = now;
        s.Framework = s.Framework with { Dirtiness = Math.Clamp(s.Framework.Dirtiness + gain, 0, 1) };
        SendSnapshot(s);
    }
    internal void CreditFood(EntityAgent entity, FoodDose dose, double fraction = 1)
    {
        if (!OwnsPhysiology(entity) || !entity.Alive || !double.IsFinite(fraction) || fraction <= 0 || fraction > 100) return;
        var s = SessionFor(entity);
        if (s.Deceased) return;
        var old = s.Framework.Nutrition;
        if (!dose.Mapped)
        {
            s.Framework = RestFeedback.Relieve(s.Framework, s.State.ElapsedBioSeconds) with { Nutrition = old with { UnmappedPortions = old.UnmappedPortions + 1, LastFood = dose.Code } };
            SendSnapshot(s); return;
        }
        // Preflight adapters bound the serving BEFORE native inventory consumption. Reject rather than silently clip.
        var actual = new FoodDose(); actual.Add(dose, fraction);
        if (FoodCapacity(entity, actual) < 1 - 1e-6)
        {
            api.Logger.Error("[vsslssmod] Food dose exceeded preflight capacity ({0}); not credited.", dose.Code); return;
        }
        var receipt = new IntakeReceipt(s.Epoch + ":food:" + (s.Framework.EventSequence + 1), dose.Code,
            actual.WaterL, actual.EnergyKJ, 0, true, true,
            actual.Nutrients[Nutrients.IndexOf("electrolytes")]);
        var pending = (double[])old.Pending.Clone(); var known = (bool[])old.Known.Clone();
        for (var i = 0; i < pending.Length; i++) { pending[i] += actual.Nutrients[i]; known[i] |= actual.Known[i]; }
        var nutrition = old with { Pending = pending, Known = known, GutMassG = old.GutMassG + actual.MassG,
            StomachWasteG = old.StomachWasteG + actual.WasteG,
            GutFoodWaterG = old.GutFoodWaterG + Math.Min(actual.MassG, actual.WaterL * 1000),
            TotalFoodG = old.TotalFoodG + actual.MassG, LastFood = dose.Code,
            UnmappedPortions = old.UnmappedPortions + (dose.HasUnmapped ? 1 : 0) };
        if (nutrition.Validate() is { } error) { SetFault(s, SessionFaultKind.PhysiologyStepRejected, error); return; }
        var beforeIntake = s.Framework; var beforeBody = s.State;
        if (!ApplyIntake(s, receipt)) return;
        var frame = RestFeedback.Relieve(s.Framework, s.State.ElapsedBioSeconds) with { Nutrition = nutrition };
        frame = BloodCirculation.AddWaste(frame, s.State, s.Profile, actual.AlcoholG + actual.SaltG * .2);
        var dirtinessGain = SpoilageResponse.DirtinessGain(actual.Spoilage, eaten: true);
        if (dirtinessGain > 0)
        {
            var key = actual.Code.Length <= 96 ? actual.Code : actual.Code[..96];
            var now = api.World.ElapsedMilliseconds / 1000d;
            if (!s.SpoilageEvents.TryGetValue(key, out var last) || now - last >= 10 || now < last)
            {
                s.SpoilageEvents[key] = now;
                frame = frame with { Dirtiness = Math.Clamp(frame.Dirtiness + dirtinessGain, 0, 1) };
            }
        }
        if (settings.Config.EnableClinicalEffects && actual.PlantPoison > 0)
            frame = Treatments.Complain(frame, "plant-poison", actual.Code.Length <= 96 ? actual.Code : "poisonous-mushroom",
                s.State.ElapsedBioSeconds, 27000 + Math.Min(1, actual.PlantPoison) * 81000, Math.Min(1, actual.PlantPoison));
        if (settings.Config.EnableClinicalEffects)
            frame = ExposeIngestion(frame, actual.PathogenDose, actual.EntericHazard, 1);
        if (settings.Config.EnableClinicalEffects)
        {
            var diverted = DiseaseSyndromes.DivertMeal(beforeIntake, frame, s.State, beforeBody);
            frame = diverted.State; s.State = diverted.Body;
        }
        if (settings.Config.EnableClinicalEffects && actual.Spoilage >= .3)
        {
            var vomit = SpoilageResponse.TryVomit(frame, s.State, s.Profile, actual.Spoilage);
            frame = vomit.State; s.State = vomit.Body;
            if (vomit.Triggered) TriggerVomit(s);
        }
        s.Framework = frame;
        ProjectPhysiology(entity); SendSnapshot(s);
    }

    private void TriggerVomit(Session s)
    {
        var entity = s.Player.Entity;
        entity.AnimManager?.StartAnimation(VomitPose.Create());
        s.VomitAnimationRemaining = 2.4;
        s.VomitParticleRemaining = 0;
        SpawnVomitParticles(s);
        s.Player.SendMessage(GlobalConstants.GeneralChatGroup, L(s.Player, "spoiled-vomit"), EnumChatType.Notification);
    }

    private void AdvanceVomitEffect(Session s, double seconds)
    {
        if (s.VomitAnimationRemaining <= 0) return;
        s.VomitAnimationRemaining = Math.Max(0, s.VomitAnimationRemaining - seconds);
        s.VomitParticleRemaining -= seconds;
        if (s.VomitParticleRemaining <= 0 && s.VomitAnimationRemaining > 0)
        {
            SpawnVomitParticles(s);
            s.VomitParticleRemaining = .3;
        }
        if (s.VomitAnimationRemaining == 0) s.Player.Entity.AnimManager?.StopAnimation(VomitPose.Code);
    }

    private void SpawnVomitParticles(Session s)
    {
        var entity = s.Player.Entity;
        var pos = entity.Pos.XYZ.AddCopy(entity.LocalEyePos.X, entity.LocalEyePos.Y - .18, entity.LocalEyePos.Z);
        var forward = entity.Pos.GetViewVector();
        var particle = new SimpleParticleProperties(10, 18, ColorUtil.ToRgba(158, 173, 48, 220), pos,
            pos.AddCopy(.08, .08, .08), new Vec3f((float)forward.X * .35f, -.55f, (float)forward.Z * .35f),
            new Vec3f((float)forward.X * .7f, -.9f, (float)forward.Z * .7f), .45f, 0, .08f, .12f,
            EnumParticleModel.Quad) { WithTerrainCollision = true, ShouldDieInLiquid = true };
        api.World.SpawnParticles(particle);
    }
}
