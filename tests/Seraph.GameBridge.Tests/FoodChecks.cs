using System.Reflection;
using Seraph.Core.Framework;
using Seraph.Server;
using Vintagestory.API.Common;
using Vintagestory.API.Datastructures;
using Vintagestory.GameContent;

internal static class FoodChecks
{
    internal static void Run(Assembly mod)
    {
        const BindingFlags flags = BindingFlags.Instance | BindingFlags.NonPublic;
        var catalog = new FoodCatalog();
        var composition = typeof(FoodCatalog).GetMethod("Composition", flags)!;
        var mealComposition = typeof(FoodCatalog).GetMethod("MealComposition", flags)!;
        object Dose(ItemStack stack, double amount = 1, bool litres = false)
            => composition.Invoke(catalog, [stack, amount, litres, null, null, false, false, false])!;
        double Number(object dose, string field) => (double)dose.GetType().GetField(field)!.GetValue(dose)!;
        double Nutrient(object dose, string id) => ((double[])dose.GetType().GetField("Nutrients")!.GetValue(dose)!)[Nutrients.IndexOf(id)];
        double RawNutrient(object dose, string id) => ((double[])dose.GetType().GetField("Nutrients")!.GetValue(dose)!)[Array.FindIndex(Nutrients.All, n => n.Id == id)];
        void Require(bool condition, string reason) { if (!condition) throw new Exception(reason); }
        ItemStack Food(EnumFoodCategory group, string code = "modded:food", float satiety = 100)
            => new(new Item { Code = new(code), NutritionProps = new() { FoodCategory = group, Satiety = satiety } });
        foreach (var group in Enum.GetValues<EnumFoodCategory>())
        {
            var dose = Dose(Food(group));
            Require(Number(dose, "EnergyKJ") > 0 && Number(dose, "MassG") > 0, "Unmapped food got no intake: " + group);
            Require(Math.Abs(Number(dose, "EnergyKJ") - Nutrient(dose, "carbohydrate") * DietaryMetabolism.CarbKJPerG
                - Nutrient(dose, "protein") * DietaryMetabolism.ProteinKJPerG
                - Nutrient(dose, "fat") * DietaryMetabolism.FatKJPerG) < 1e-7, "Food-group macro energy disagrees: " + group);
        }
        foreach (var satiety in new[] { 0f, -1, float.NaN })
            Require(Number(Dose(Food(EnumFoodCategory.Fruit, satiety: satiety)), "EnergyKJ") == 0, "Invalid food creates energy.");
        var liquid = Food(EnumFoodCategory.Dairy);
        liquid.Item.Attributes = JsonObject.FromJson("{\"waterTightContainerProps\":{\"itemsPerLitre\":100,\"nutritionPropsPerLitre\":{\"satiety\":50,\"foodcategory\":\"Dairy\"}}}");
        var perLitre = Dose(liquid, litres: true);
        Require(Number(perLitre, "EnergyKJ") > 0, "Per-litre nutrition produced no energy.");
        Require(Math.Abs(Number(perLitre, "EnergyKJ") - 50 * 4.184) < 1e-7, "Unknown liquid violated 1 satiety = 1 kcal fallback.");
        Require(Math.Abs(Number(Dose(liquid, 25), "EnergyKJ") * 4 - Number(perLitre, "EnergyKJ")) < 1e-7,
            "Fallback liquid units and litres disagree.");
        var knownFallback = (bool[])perLitre.GetType().GetField("Known")!.GetValue(perLitre)!;
        Require(MicroNutrition.Groups.All(id => !knownFallback[Nutrients.IndexOf(id)]), "Food category fabricated micronutrients.");
        var quarter = Dose(liquid, .25, true);
        Require(Math.Abs(Number(quarter, "EnergyKJ") * 4 - Number(perLitre, "EnergyKJ")) < 1e-8, "Liquid fraction settlement wrong.");
        catalog.Foods = [new() { Codes = ["modded:*"], PortionG = 100, EnergyKJPer100G = 100 },
            new() { Codes = ["modded:food"], PortionG = 100, EnergyKJPer100G = 222,
                Per100G = new() { ["carbohydrate"] = 5, ["protein"] = 5, ["fat"] = 2,
                    ["b1"] = 1.2, ["b6"] = 1.7, ["b9"] = 400, ["b12"] = 2.4 } }];
        var exact = Dose(Food(EnumFoodCategory.Grain));
        Require(Number(exact, "EnergyKJ") > 0
            && Math.Abs(Number(exact, "EnergyKJ") - Nutrient(exact, "carbohydrate") * DietaryMetabolism.CarbKJPerG
                - Nutrient(exact, "protein") * DietaryMetabolism.ProteinKJPerG
                - Nutrient(exact, "fat") * DietaryMetabolism.FatKJPerG) < 1e-7
            && Nutrient(exact, "bcomplex") == 0,
            "Exact profile precedence or category micronutrition wrong.");
        Require(Number(exact, "MassG") == 100 && Nutrient(exact, "carbohydrate") == 5
            && Nutrient(exact, "protein") == 5 && Nutrient(exact, "fat") == 2, "Explicit serving/composition was discounted.");
        catalog.Foods = [];
        var unknown = Dose(Food(EnumFoodCategory.Unknown));
        Require(Math.Abs(Number(unknown, "EnergyKJ") - 418.4) < 1e-9 && Number(unknown, "WaterL") == 0
            && Nutrient(unknown, "protein") == 0 && Nutrient(unknown, "fat") == 0
            && MicroNutrition.Groups.All(id => Nutrient(unknown, id) == 0), "Unknown category invented composition.");
        var apple = Dose(Food(EnumFoodCategory.Fruit));
        Require(Math.Abs(Nutrient(apple, "protein") / Number(apple, "MassG") * 100 - .26) < 1e-9
            && Math.Abs(Number(apple, "WaterL") * 100000 / Number(apple, "MassG") - 85.56) < 1e-9,
            "Fruit reference disagrees with USDA NDB 09003.");
        var oldPortion = new FoodProfile { Codes = ["modded:food"], PortionG = 123,
            Per100G = new() { ["carbohydrate"] = 20, ["protein"] = 5, ["fiber"] = 3 },
            MicroPer100G = new() { ["calciumMg"] = 0 } };
        catalog.Foods = [oldPortion];
        var custom = Dose(Food(EnumFoodCategory.Vegetable));
        Require(Number(custom, "MassG") == 123 && Math.Abs(Nutrient(custom, "carbohydrate") - 24.6) < 1e-9,
            "Custom explicit portion changed or composition was discounted.");
        catalog.Foods = [];
        var fallbackDose = Dose(Food(EnumFoodCategory.Grain, "verified-mod:unmapped-food"));
        Require(Number(fallbackDose, "EnergyKJ") > 0 && Number(fallbackDose, "MassG") > 0,
            "An unmapped compatibility food did not use the native food-category fallback.");
        var physical = new FoodProfile { Codes = ["modded:food"], PortionG = 100,
            MicroPer100G = FoodMicronutrients.Daily.ToDictionary(k => k.Key, k => k.Value) };
        catalog.Foods = [physical];
        foreach (var id in new[] { "calcium", "iron", "a", "c", "bcomplex" })
            Require(Math.Abs(Nutrient(Dose(Food(EnumFoodCategory.Grain)), id) - MicroNutrition.DailyPointsFor(id)) < 1e-9,
                "Physical daily intake conversion/category double charge: " + id);
        Require(Math.Abs(MicroNutrition.Gain(50, 22) - 72) < 1e-9, "Daily physical input must add 22 reserve points.");
        Require(Math.Abs(Nutrient(Dose(Food(EnumFoodCategory.Grain), .25), "iron") - MicroNutrition.DailyPointsFor("iron") / 4d) < 1e-9,
            "Solid consumed fraction changed micronutrient units.");
        physical.MicroPer100G["ironMg"] = 0;
        Require(Nutrient(Dose(Food(EnumFoodCategory.Protein)), "iron") == 0, "Measured zero fell through to category estimate.");
        physical.MicroPer100G.Remove("ironMg");
        Require(Nutrient(Dose(Food(EnumFoodCategory.Protein)), "iron") == 0
            && !((bool[])Dose(Food(EnumFoodCategory.Protein)).GetType().GetField("Known")!.GetValue(Dose(Food(EnumFoodCategory.Protein)))!)[Nutrients.IndexOf("iron")],
            "Missing physical datum was filled from a category score.");
        physical.MicroPer100G.Remove("b12Ug");
        Require(Nutrient(Dose(Food(EnumFoodCategory.Grain)), "bcomplex") == 0,
            "Incomplete B group treated missing evidence as zero.");
        physical.Density = 1.03;
        Require(Math.Abs(Nutrient(Dose(liquid, .25, true), "calcium") - 22 * 2.575) < 1e-9,
            "Liquid physical micronutrients lost density/volume conversion.");
        catalog.Foods = [];
        Console.WriteLine("PASS physical micronutrient units, daily curve, fractions, zero/missing, B completeness and release catalog");
        var spiritStack = Food(EnumFoodCategory.Fruit, "game:spiritportion-apple");
        var unknownAlcohol = Dose(spiritStack, .25, true);
        Require(Number(unknownAlcohol, "AlcoholG") == 0, "Unknown alcohol content was guessed.");
        catalog.Foods = [new() { Codes = ["game:spiritportion-apple"], PortionG = 1000, WaterGPer100G = 55,
            AlcoholGPer100G = 40 }];
        var spirit = Dose(spiritStack, .25, true);
        Require(Number(spirit, "AlcoholG") == 100, "Explicit alcohol composition was not applied.");
        spirit.GetType().GetMethod("ScaleNutrition", flags)!.Invoke(spirit, [2d]);
        Require(Number(spirit, "AlcoholG") == 200, "Alcohol mass did not follow meal scaling.");
        var combined = Activator.CreateInstance(spirit.GetType(), true)!;
        spirit.GetType().GetMethod("Add", flags)!.Invoke(combined, [spirit, .25d]);
        Require(Number(combined, "AlcoholG") == 50, "Alcohol mass did not follow actual consumed fraction.");
        catalog.Foods = [];
        var salted = Food(EnumFoodCategory.Protein, "game:redmeat-cured");
        salted.Attributes.SetFloat("vsslssmodSaltGrams", 12);
        Require(Number(Dose(salted, .25), "SaltG") == 3, "Actual salt mass lost.");
        foreach (var (category, id) in new[] { (EnumFoodCategory.Protein, "iron"), (EnumFoodCategory.Vegetable, "a"),
            (EnumFoodCategory.Fruit, "c"), (EnumFoodCategory.Dairy, "calcium"), (EnumFoodCategory.Grain, "bcomplex") })
        {
            Require(Nutrient(Dose(Food(category)), id) == 0, "Category-only fallback fabricated micronutrients.");
            if (id == "iron") Require(Nutrients.IndexOf("iron") != Nutrients.IndexOf("zinc")
                && RawNutrient(Dose(Food(category)), "zinc") == 0, "Iron and zinc must be independent; category fallbacks cannot invent zinc.");
        }
        foreach (var preparation in new[] { "cured", "fermented", "cooked", "baked", "raw" })
        {
            catalog.Foods = [new() { Codes = ["modded:soybean-raw"], PortionG = 100, EnergyKJPer100G = 400, Preparation = preparation }];
            var safe = Dose(Food(EnumFoodCategory.Protein, "modded:soybean-raw"), 10);
            Require(Number(safe, "EntericHazard") == 0 && ((IReadOnlyDictionary<string, double>)safe.GetType().GetField("PathogenDose")!.GetValue(safe)!).Count == 0,
                "Clean food acquired disease from preparation/category alone: " + preparation);
        }
        catalog.Foods = [];
        var toxicMushroom = Food(EnumFoodCategory.Vegetable, "game:mushroom-deathcap");
        toxicMushroom.Item.NutritionProps.Health = -50;
        Require(Number(Dose(toxicMushroom, .25), "PlantPoison") == .25
            && Number(Dose(Food(EnumFoodCategory.Vegetable, "game:mushroom-field")), "PlantPoison") == 0,
            "Mushroom poison did not follow verified source and consumed quantity.");
        var contaminated = Food(EnumFoodCategory.Vegetable);
        contaminated.Attributes.SetString("vsslssmod:pathogens-v1", """{"salmonella":0.4}""");
        var split = contaminated.Clone(); split.StackSize = 1;
        var exposure = (IReadOnlyDictionary<string, double>)Dose(split, 2).GetType().GetField("PathogenDose")!.GetValue(Dose(split, 2))!;
        Require(Math.Abs(exposure["salmonella"] - .8) < 1e-9, "Split produce lost identified ingestion exposure.");
        var liquidAgents = new Dictionary<string, double> { ["salmonella"] = .01 };
        liquid.Attributes.SetString("vsslssmod:pathogens-v1", """{"salmonella":0.01}""");
        var liquidDose = Dose(liquid, .25, true);
        Require(Math.Abs(((IReadOnlyDictionary<string, double>)liquidDose.GetType().GetField("PathogenDose")!.GetValue(liquidDose)!)["salmonella"] - .25) < 1e-9,
            "Liquid metadata concentration was not converted from native item units.");
        var registry = new RecipeRegistrySystem();
        registry.CookingRecipes.Add(new() { Code = "test-cooked", Ingredients = [] });
        var loader = (IModLoader)RoutingProxy.Make(typeof(IModLoader), (m, _) => m.Name == "GetModSystem" ? registry : null);
        var api = (ICoreAPI)RoutingProxy.Make(typeof(ICoreAPI), (m, _) => m.Name == "get_ModLoader" ? loader : null);
        var world = (IWorldAccessor)RoutingProxy.Make(typeof(IWorldAccessor), (m, _) => m.Name == "get_Api" ? api : null);
        var raw = Food(EnumFoodCategory.Protein, "modded:meat-raw");
        IReadOnlyDictionary<string, double> Agents(object dose) => (IReadOnlyDictionary<string, double>)dose.GetType().GetField("PathogenDose")!.GetValue(dose)!;
        Require(Agents(Dose(raw)).GetValueOrDefault("salmonella") > 0 && !Agents(Dose(raw)).ContainsKey("trichinosis")
            && Number(Dose(raw), "EntericHazard") == 0, "Raw meat lost source-specific pathogens or received duplicate generic contamination.");
        var bushmeat = Food(EnumFoodCategory.Protein, "game:bushmeat-raw");
        Require(Agents(Dose(bushmeat)).GetValueOrDefault("trichinosis") > 0, "Raw bushmeat cannot transmit trichinosis.");
        var meal = new ItemStack(new BlockMeal { Code = new("game:meal") });
        meal.Attributes.SetString("recipeCode", "test-cooked");
        var cooked = mealComposition.Invoke(catalog, [world, meal, new[] { raw }, false])!;
        Require(Number(cooked, "EntericHazard") == 0 && Agents(cooked).Count == 0 && Number(cooked, "EnergyKJ") > 0, "Cooked meal inherited raw ingredient hazard.");
        var pie = new ItemStack(new BlockPie { Code = new("game:pie-perfect") });
        var baked = mealComposition.Invoke(catalog, [world, pie, new[] { raw }, true])!;
        Require(Number(baked, "EntericHazard") == 0 && Agents(baked).Count == 0, "Fully baked pie inherited raw ingredient hazard.");
        raw.Attributes.SetString("vsslssmod:pathogens-v1", """{"shigella":0.4}""");
        var recontaminated = mealComposition.Invoke(catalog, [world, meal, new[] { raw }, false])!;
        Require(Agents(recontaminated).GetValueOrDefault("shigella") > 0, "Reading cooked food silently sterilized later contamination.");
        var spoiled = new ItemStack(new SpoiledFood { Code = new("modded:meat-raw"),
            NutritionProps = new() { FoodCategory = EnumFoodCategory.Protein, Satiety = 100 } });
        var rottenCooked = mealComposition.Invoke(catalog, [world, meal, new[] { spoiled }, false])!;
        Require(Number(rottenCooked, "EntericHazard") > 0, "Cooking cleared existing spoilage hazard.");
        Require(Number(composition.Invoke(catalog, [spoiled, 1d, false, world, null, false, false, false])!, "EntericHazard") > 0,
            "Solid spoiled food lost its verified spoilage source.");
        ((SpoiledFood)spoiled.Item).Spoilage = .2f;
        Require(Number(mealComposition.Invoke(catalog, [world, meal, new[] { spoiled }, false])!, "EntericHazard") > 0,
            "Measurable spoilage did not create its nominal disease exposure.");
        ((SpoiledFood)spoiled.Item).Spoilage = .5f;
        Require(Number(mealComposition.Invoke(catalog, [world, meal, new[] { spoiled }, false])!, "EntericHazard") > 0,
            "Heavier spoilage did not retain generic disease exposure.");
        Require(!spoiled.Attributes.GetBool("transitionObserved"), "Composition inspection mutated the eaten ingredient.");
        ProcessingChecks(mod, world);
        SourcedCatalogChecks();
        ReleaseFoodChecks.Run(mod);
        FoodCoverageChecks.Run(mod);
        Console.WriteLine("PASS native food groups, macro energy, explicit profiles, v1 units, liquid portions and cooked dishes");
    }

    private static void SourcedCatalogChecks()
    {
        const BindingFlags flags = BindingFlags.Instance | BindingFlags.NonPublic;
        var path = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "../../../../../assets/vsslssmod/config/foods.json"));
        var catalog = System.Text.Json.JsonSerializer.Deserialize<FoodCatalog>(File.ReadAllText(path))!;
        if (catalog.Foods.Length == 0 || typeof(FoodCatalog).GetMethod("Validate", flags)!.Invoke(catalog, null) is not null)
            throw new Exception("Packaged sourced catalog is empty or invalid.");
        var composition = typeof(FoodCatalog).GetMethod("Composition", flags)!;
        double Number(object dose, string field) => (double)dose.GetType().GetField(field)!.GetValue(dose)!;
        var codes = new HashSet<string>();
        foreach (var profile in catalog.Foods)
        foreach (var code in profile.Codes)
        {
            if (!codes.Add(code) || !(profile.Source.StartsWith("USDA SR Legacy") || profile.Source.StartsWith("Seraph whole-blood composition model"))
                || code.Contains('*') && (code.Count(c => c == '*') != 1 || !code.EndsWith('*')))
                throw new Exception("Sourced catalog contains a duplicate, guessed family or missing provenance.");
            if (code.Contains('*')) continue;
            var stack = new ItemStack(new Item { Code = new(code), NutritionProps = new() {
                FoodCategory = EnumFoodCategory.Vegetable, Satiety = 100 } });
            var dose = composition.Invoke(catalog, [stack, 1d, false, null, null, false, false, false])!;
            var macros = (double[])dose.GetType().GetField("Nutrients")!.GetValue(dose)!;
            var waterG = Number(dose, "WaterL") * 1000;
            var settled = waterG + macros.Take(3).Sum() + macros[3] + Number(dose, "WasteG") + Number(dose, "AlcoholG");
            if (profile.PortionMethod == "explicit" && Math.Abs(Number(dose, "MassG") - profile.PortionG) > 1e-7
                || Math.Abs(settled - Number(dose, "MassG")) > 1e-7 || Number(dose, "EnergyKJ") <= 0 && Number(dose,"AlcoholG") <= 0)
                throw new Exception("Sourced food lost portion, mass conservation or macro energy: " + code);
        }
        var liquid = new ItemStack(new Item { Code = new("aculinaryartillery:eggwhiteportion") });
        var litre = composition.Invoke(catalog, [liquid, 1d, true, null, null, true, false, false])!;
        var quarter = composition.Invoke(catalog, [liquid, .25d, true, null, null, true, false, false])!;
        if (Number(litre, "MassG") != 1030 || Number(quarter, "MassG") != 257.5
            || Math.Abs(Number(quarter, "EnergyKJ") * 4 - Number(litre, "EnergyKJ")) > 1e-8)
            throw new Exception("Sourced meal-only liquid lost its litre conversion.");
        foreach (var legume in new[] { "soybean", "peanut" })
        {
            object Legume(string prefix) => composition.Invoke(catalog,
                [new ItemStack(new Item { Code = new("game:" + prefix + legume), NutritionProps = new() { Satiety = 100, FoodCategory = EnumFoodCategory.Protein } }), 1d, false, null, null, false, false, false])!;
            var current = Legume("legume-");
            var legacy = Legume("vegetable-");
            if (current.GetType().GetField("Source")!.GetValue(current) as string != legacy.GetType().GetField("Source")!.GetValue(legacy) as string
                || Number(current, "MassG") <= 0 || Number(current, "EnergyKJ") != Number(legacy, "EnergyKJ"))
                throw new Exception("Native 1.22 legume code lost its sourced composition: " + legume);
        }
        var unmapped = new ItemStack(new Item { Code = new("compatibility:unknown-recipe"),
            NutritionProps = new() { FoodCategory = EnumFoodCategory.Protein, Satiety = 100 } });
        var fallback = composition.Invoke(catalog, [unmapped, 1d, false, null, null, false, false, false])!;
        if (!(fallback.GetType().GetField("Source")!.GetValue(fallback) as string)!.StartsWith("USDA SR Legacy NDB 01123")
            || Number(fallback, "EnergyKJ") <= 0)
            throw new Exception("Partial precise catalog disabled native compatibility fallback.");
        if (catalog.Find(new ItemStack(new Item { Code = new("pemmican:starchwaterportion") })) is not { PortionG: > 0 })
            throw new Exception("Long-term food starch water lacks an independent composition.");
        Console.WriteLine($"PASS sourced catalog: {catalog.Foods.Length} profiles/{codes.Count} codes, mass balance, liquid meals and fallback");
    }

    private static void ProcessingChecks(Assembly mod, IWorldAccessor world)
    {
        var type = mod.GetType("Seraph.Server.NativeFoodBridge", true)!;
        using var bridge = (IDisposable)Activator.CreateInstance(type, nonPublic: true)!;
        ItemStack Stack(string code, int count) => new(new Item { Code = new(code), CollectibleBehaviors = [] }, count)
            { Attributes = new TreeAttribute() };
        var vegetable = Stack("test:vegetable", 20);
        vegetable.Attributes.SetString("vsslssmod:pathogens-v1", """{"salmonella":0.4}""");
        var tool = Stack("test:tool", 1);
        tool.Attributes.SetString("vsslssmod:pathogens-v1", """{"ecoli":9}""");
        IRecipeIngredient Ingredient(ItemStack match, int quantity, bool consume) =>
            (IRecipeIngredient)RoutingProxy.Make(typeof(IRecipeIngredient), (m, args) => m.Name switch {
                "get_Quantity" => quantity,
                "get_ConsumeProperties" => new RecipeIngredientConsumeProperties { Consume = consume, Quantity = quantity },
                "SatisfiesAsIngredient" => ((ItemStack)args![0]!).Collectible == match.Collectible,
                _ => null
            });
        var recipe = (IRecipeBase)RoutingProxy.Make(typeof(IRecipeBase), (m, _) => m.Name switch {
            "get_RecipeIngredients" => new[] { Ingredient(vegetable, 2, true), Ingredient(tool, 1, false) },
            "get_AverageDurability" => false, _ => null });
        var output = new DummySlot(Stack("test:food", 4));
        output.Itemstack!.Item.NutritionProps = new() { Satiety = 10, FoodCategory = EnumFoodCategory.Vegetable };
        typeof(CollectibleObject).GetField("api", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(output.Itemstack!.Collectible, world.Api);
        // Native RecipeBase.GenerateOutputStack calls this before it consumes ingredients.
        output.Itemstack!.Collectible.OnCreatedByCrafting([new DummySlot(vegetable), new DummySlot(tool)], output, recipe);
        var agents = System.Text.Json.JsonSerializer.Deserialize<Dictionary<string, double>>(
            output.Itemstack.Attributes.GetString("vsslssmod:pathogens-v1"))!;
        if (agents.Count != 1 || Math.Abs(agents["salmonella"] * output.StackSize - .8) > 1e-9 || vegetable.StackSize != 20)
            throw new Exception("Crafting lost pathogen mass, charged unused stock/tools or mutated inventory.");
        var synthetic = new GridRecipe { Name = new("toolsmith:inhandtinkertoolcrafting"), AverageDurability = false };
        var knife = new DummySlot(Stack("toolsmith:knife", 1));
        typeof(CollectibleObject).GetField("api", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(knife.Itemstack!.Collectible, world.Api);
        knife.Itemstack!.Collectible.OnCreatedByCrafting([], knife, synthetic);
        if (knife.StackSize != 1) throw new Exception("Synthetic nonfood recipe changed output.");
        // Even a food-like third-party output with an unsupported recipe must preserve existing metadata.
        output.Itemstack.Collectible.OnCreatedByCrafting([], output, synthetic);
        if (output.Itemstack.Attributes.GetString("vsslssmod:pathogens-v1") != System.Text.Json.JsonSerializer.Serialize(agents))
            throw new Exception("Unsupported recipe erased existing contamination.");
        output.Itemstack.Collectible.OnCreatedByCrafting([], output, recipe);
        if (output.Itemstack.Attributes.GetString("vsslssmod:pathogens-v1") != System.Text.Json.JsonSerializer.Serialize(agents))
            throw new Exception("Incomplete mapping erased existing contamination.");
        const BindingFlags flags = BindingFlags.Static | BindingFlags.NonPublic;
        var input = new DummySlot(vegetable);
        var slots = (ISlotProvider)RoutingProxy.Make(typeof(ISlotProvider), (m, _) => m.Name == "get_Slots" ? new ItemSlot[] { input } : null);
        object?[] startArgs = [input, output, null];
        type.GetMethod("CookingStart", flags)!.Invoke(null, startArgs);
        type.GetMethod("Cooked", flags)!.Invoke(null, [world, slots, input, output, startArgs[2]]);
        if (output.Itemstack.Attributes.GetString("vsslssmod:pathogens-v1") == "{}")
            throw new Exception("An unsuccessful cooking attempt sterilized existing food.");
        output.Itemstack = output.Itemstack.Clone();
        type.GetMethod("Cooked", flags)!.Invoke(null, [world, slots, input, output, startArgs[2]]);
        if (output.Itemstack.Attributes.GetString("vsslssmod:pathogens-v1") != "{}"
            || vegetable.Attributes.GetString("vsslssmod:pathogens-v1") == "{}")
            throw new Exception("Confirmed cooking failed to sterilize output or sterilized unused ingredients.");
        Console.WriteLine("PASS crafting pathogen mass, unused stock/tools, failed cooking and confirmed sterilization");
    }

    private sealed class SpoiledFood : Item
    {
        public float Spoilage { get; set; } = .8f;
        public override TransitionableProperties[] GetTransitionableProperties(IWorldAccessor world, ItemStack itemstack, Vintagestory.API.Common.Entities.Entity forEntity)
            => [new() { Type = EnumTransitionType.Perish }];
        public override TransitionState UpdateAndGetTransitionState(IWorldAccessor world, ItemSlot slot, EnumTransitionType type)
        {
            slot.Itemstack!.Attributes.SetBool("transitionObserved", true);
            return new() { TransitionLevel = Spoilage };
        }
    }
}
