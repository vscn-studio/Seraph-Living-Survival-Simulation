using System.Reflection;
using System.Text.Json;
using Seraph.Server;
using Vintagestory.API.Common;
using Vintagestory.API.Datastructures;
using Vintagestory.GameContent;

internal static class ReleaseFoodChecks
{
    internal static void Registry(string registryPath)
    {
        var path = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "../../../../../assets/vsslssmod/config/foods.json"));
        var catalog = JsonSerializer.Deserialize<FoodCatalog>(File.ReadAllText(path))!;
        using var report = JsonDocument.Parse(File.ReadAllText(registryPath));
        var count = 0;
        foreach (var row in report.RootElement.GetProperty("Foods").EnumerateArray())
        {
            var code = row.GetProperty("Code").GetString()!;
            if (catalog.Excluded(code) || row.GetProperty("Route").GetString() == "actual-ingredients-at-consumption") continue;
            double Number(string key) => row.TryGetProperty(key, out var value) && value.ValueKind == JsonValueKind.Number ? value.GetDouble() : 0;
            var stack = new ItemStack(new Item { Code = new(code), NutritionProps = new() {
                FoodCategory = EnumFoodCategory.Unknown, Satiety = (float)Math.Max(Number("NativeSatiety"),Number("MealIngredientSatiety")) } });
            var liquid = Number("ItemsPerLitre") > 0 || Number("LiquidSatietyPerLitre") > 0 || Number("LiquidMealSatietyPerLitre") > 0;
            if (liquid) stack.Item.Attributes = JsonObject.FromJson(JsonSerializer.Serialize(new {
                waterTightContainerProps = new { itemsPerLitre = Number("ItemsPerLitre") > 0 ? Number("ItemsPerLitre") : 100 } }));
            if (catalog.Find(stack) is not { PortionG: > 0, PortionMethod: "explicit" })
                throw new Exception("Registered food has no independent portion: " + code);
            foreach (var cooked in new[] { false, true })
            {
                var dose = catalog.Composition(stack, liquid ? .25 : 1, liquid, cooked: cooked);
                if (!dose.Mapped || dose.HasUnmapped || dose.MassG <= 0)
                    throw new Exception("Registered food failed actual intake: " + code);
                foreach (var id in new[] { "calcium", "iron", "zinc", "a", "c", "bcomplex" })
                {
                    var index = Seraph.Core.Framework.Nutrients.IndexOf(id);
                    if (!dose.Known[index] || !double.IsFinite(dose.Nutrients[index]))
                        throw new Exception($"Registered food lost {id}: {code}, cooked={cooked}");
                }
            }
            count++;
        }
        Console.WriteLine($"PASS {count} registered food variants: explicit portions and all six reserve groups in direct and cooked intake");
    }

    internal static void Run(Assembly mod)
    {
        const BindingFlags hidden = BindingFlags.Instance | BindingFlags.NonPublic;
        var path = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "../../../../../assets/vsslssmod/config/foods.json"));
        var catalog = JsonSerializer.Deserialize<FoodCatalog>(File.ReadAllText(path))!;
        if (catalog.Validate() is { } catalogError) throw new Exception("Release food catalog invalid: " + catalogError);
        ConfigFiles(mod);
        FoodCalibrationChecks.Run(catalog);
        PreservedFoodChecks.Run(catalog);
        ItemStack Stack(string code, int id = 1) => new(new Item { Code = new(code), ItemId = id });
        var find = typeof(FoodCatalog).GetMethod("Find", hidden)!;
        FoodProfile Profile(string code) => (FoodProfile?)find.Invoke(catalog, [Stack(code)])
            ?? throw new Exception("Missing focused release food profile: " + code);
        foreach (var code in new[] {
            "butchering:bloodbread-cassava-perfect", "butchering:sausage-blackpudding-cooked",
            "butchering:primemeat-healing", "butchering:smoked-healing-primemeat",
            "game:mushroom-shiitake-normal-north", "game:bread-cassava-perfect", "game:fishchunk-long-9-cooked",
            "game:fruit-saguaro", "game:fruit-breadfruit", "game:fruit-blackcurrant", "game:fruit-lychee",
            "game:fruit-beautyberry", "game:fruit-cloudberry", "game:pickledvegetable-fennel",
            "game:vegetable-bellpepper", "game:vegetable-olive", "game:vegetable-fennel",
            "game:juiceportion-cloudberry", "primitivesurvival:smokedmeat-fish-raw",
            "primitivesurvival:curedsmokedmeat-redmeat-cooked", "primitivesurvival:snakemeat-cooked",
            "primitivesurvival:caviar-cured", "pemmican:caramel-dry", "pemmican:bread-corn-perfect",
            "pemmican:bread-potato-perfect", "pemmican:nutribar-general", "pemmican:nutribar-healing",
            "pemmican:kebab-mushroom", "pemmican:cookedvegetable-pickledpotato-perfect",
            "pemmican:mushroomclean-pickled", "pemmican:mushroompate-raw", "pemmican:mushroompate-dry",
            "pemmican:mushroompatebar", "pemmican:chips-potato", "pemmican:pemmican-bar",
            "pemmican:pemmican-cooked", "pemmican:pemmican-salt", "game:redmeat-vintage",
            "pemmican:cookedchoppedvegetable-pickledpotato-perfect" })
        {
            var profile = Profile(code);
            if (profile.Coverage is "fallback" || string.IsNullOrWhiteSpace(profile.Source)
                || string.IsNullOrWhiteSpace(profile.PortionBasis))
                throw new Exception("Focused food profile lacks composition or provenance: " + code);
        }
        var bar = Profile("pemmican:nutribar-general");
        if (bar.Coverage != "recipe" || bar.MicroPer100G.Count == 0 || bar.Per100G.Count == 0)
            throw new Exception("Nutribar recipe did not retain sourced macro and micronutrient composition.");
        if ((bool)typeof(FoodCatalog).GetMethod("Excluded", hidden)!.Invoke(catalog, ["game:mushroom-deathcap-normal"])!)
            throw new Exception("Native poison effects must not erase mushroom composition.");
        object Dose(ItemStack stack, double amount = 1, bool cooked = false, bool meal = false)
            => typeof(FoodCatalog).GetMethod("Composition", hidden)!.Invoke(catalog,
                [stack, amount, false, null, null, meal, cooked, false])!;
        double Value(object dose, string field) => (double)dose.GetType().GetField(field)!.GetValue(dose)!;
        double Nutrient(object dose, string id) => ((double[])dose.GetType().GetField("Nutrients")!.GetValue(dose)!)[Seraph.Core.Framework.Nutrients.IndexOf(id)];
        void Near(double a, double b) { if (Math.Abs(a-b) > 1e-7) throw new Exception($"Food portion mismatch {a} / {b}"); }
        if (catalog.ReviewedFallbacks.Count != 0) throw new Exception("Released foods still rely on category fallback approval.");
        foreach (var profile in catalog.Foods)
        {
            if (profile.PortionG <= 0 || profile.PortionMethod != "explicit"
                || FoodMicronutrients.Daily.Keys.Any(k => !profile.MicroPer100G.ContainsKey(k)))
                throw new Exception("Incomplete portion or physical micronutrients: " + profile.Codes[0]);
            var code = profile.Codes[0].Replace("*", "test");
            foreach (var cooked in new[] { false, true })
            {
                var dose = Dose(Stack(code), cooked: cooked);
                var known = (bool[])dose.GetType().GetField("Known")!.GetValue(dose)!;
                foreach (var id in new[] { "calcium", "iron", "zinc", "a", "c", "bcomplex" })
                    if (!known[Seraph.Core.Framework.Nutrients.IndexOf(id)] || !double.IsFinite(Nutrient(dose,id)))
                        throw new Exception($"Micronutrient lost at intake: {code}, cooked={cooked}, {id}");
            }
        }
        var rawOrange = Dose(Stack("game:fruit-orange"));
        var cookedOrange = Dose(Stack("game:fruit-orange"), cooked: true);
        Near(Nutrient(rawOrange,"calcium"),Nutrient(cookedOrange,"calcium"));
        Near(Nutrient(rawOrange,"c")*.5,Nutrient(cookedOrange,"c"));
        var rawCurrant = Dose(Stack("game:fruit-blackcurrant"));
        Near(Nutrient(rawCurrant,"c")*.5,Nutrient(Dose(Stack("game:fruit-blackcurrant"), cooked:true),"c"));
        Near(Value(Dose(Stack("game:fishchunk-standard-2-cooked")),"MassG")*4.5,
            Value(Dose(Stack("game:fishchunk-standard-9-cooked")),"MassG"));
        var smokedMeat = Dose(Stack("game:redmeat-smoked"));
        var cookedMeat = Dose(Stack("game:redmeat-cooked"));
        Near(Nutrient(smokedMeat,"iron"),Nutrient(cookedMeat,"iron"));
        if (Value(smokedMeat,"WaterL") >= Value(cookedMeat,"WaterL")) throw new Exception("Smoking failed to remove water.");
        if (Nutrient(Dose(Stack("game:bread-rye-perfect")),"bcomplex") <= 0
            || Nutrient(Dose(Stack("game:redmeat-smoked")),"bcomplex") <= 0)
            throw new Exception("Baking or smoking erased B vitamins.");
        var blood = Stack("butchering:bloodportion");
        blood.Item.Attributes = JsonObject.FromJson("""{"waterTightContainerProps":{"itemsPerLitre":100}}""");
        if (Nutrient(Dose(blood,20),"iron") <= Nutrient(Dose(Stack("game:redmeat-raw")),"iron"))
            throw new Exception("Blood lost its hemoglobin iron.");
        if (Nutrient(Dose(Stack("butchering:bloodbread-rye-perfect")),"iron") <= Nutrient(Dose(Stack("game:bread-rye-perfect")),"iron"))
            throw new Exception("Blood bread omitted blood nutrients.");
        var poison = Stack("game:mushroom-deathcap-normal");
        poison.Item.NutritionProps = new() { Satiety=80, Health=-50, FoodCategory=EnumFoodCategory.Vegetable };
        if (Value(Dose(poison),"PlantPoison") <= 0 || Value(Dose(poison),"MassG") <= 0)
            throw new Exception("Poison and nutrition must coexist.");
        Console.WriteLine($"PASS {catalog.Foods.Length} explicit profiles: all nine physical micros and six reserves, raw and cooked intake");
        var checklist = new List<string>();
        foreach (var grain in new[] { "spelt", "rye", "flax", "rice", "cassava", "amaranth", "sunflower" })
        foreach (var state in new[] { "partbaked", "perfect", "charred" })
            checklist.Add($"butchering:bloodbread-{grain}-{state}");
        foreach (var meatKind in new[] { "redmeat", "bushmeat", "fish", "poultry" })
        foreach (var state in new[] { "raw", "partbaked", "cooked", "charred" })
        foreach (var family in new[] { "smokedmeat", "curedsmokedmeat" })
            checklist.Add($"primitivesurvival:{family}-{meatKind}-{state}");
        foreach (var vegetable in new[] { "carrot", "cabbage", "onion", "turnip", "parsnip", "pumpkin", "bellpepper", "olive", "fennel" })
            checklist.Add($"game:pickledvegetable-{vegetable}");
        checklist.AddRange(catalog.Foods.SelectMany(p => p.Codes).Where(c => c.StartsWith("game:juiceportion-")));
        checklist.AddRange(new[] { "game:fishchunk-long-9-cooked", "game:mushroom-violetwebcap-normal",
            "butchering:primemeat-healing", "butchering:primemeat-curedhealing", "butchering:primemeat-cooked",
            "butchering:smoked-none-primemeat", "butchering:smoked-healing-primemeat",
            "butchering:sausage-bloodsausage-cooked", "butchering:sausage-blackpudding-smoked",
            "pemmican:caramel-raw", "pemmican:caramel-dry", "pemmican:caramel-cooked",
            "pemmican:kebab-cooked", "pemmican:kebab-mushroom", "pemmican:mushroompatebar",
            "pemmican:nutribar-general", "pemmican:nutribar-healing", "primitivesurvival:caviar-cured" });
        foreach (var code in checklist)
        {
            var stack = Stack(code);
            stack.Item.NutritionProps = new() { Satiety = 100, FoodCategory = EnumFoodCategory.Vegetable };
            if (code.Contains("juiceportion")) stack.Item.Attributes = JsonObject.FromJson("""{"waterTightContainerProps":{"itemsPerLitre":100}}""");
            var dose = Dose(stack);
            var known = (bool[])dose.GetType().GetField("Known")!.GetValue(dose)!;
            if (Profile(code).Coverage == "fallback" || Value(dose, "MassG") <= 0 || Value(dose, "EnergyKJ") <= 0
                || !known[Seraph.Core.Framework.Nutrients.IndexOf("calcium")] || !known[Seraph.Core.Framework.Nutrients.IndexOf("iron")])
                throw new Exception("Checklist food lost its actual portion or sourced minerals: " + code);
        }
        foreach (var family in new[] { "caramel", "pemmican" })
        {
            var raw = Dose(Stack($"pemmican:{family}-raw"));
            var dry = Dose(Stack($"pemmican:{family}-" + (family == "caramel" ? "dry" : "bar")));
            Near(Value(raw, "EnergyKJ"), Value(dry, "EnergyKJ"));
            if (Value(dry, "WaterL") >= Value(raw, "WaterL")) throw new Exception("Preserving food failed to remove water.");
        }
        Near(Value(Dose(Stack("game:vegetable-carrot")), "EnergyKJ"), Value(Dose(Stack("game:pickledvegetable-carrot")), "EnergyKJ"));
        Near(Value(Dose(Stack("game:mushroom-fieldmushroom-normal")), "MassG"), 70);
        if (Profile("pemmican:mushroompatebar").Per100G["fat"] <= 10
            || Profile("pemmican:kebab-cooked").Per100G["fiber"] <= 0)
            throw new Exception("Composite food discarded its fat or vegetable ingredients.");
        if (Profile("game:fruit-blackcurrant").MicroPer100G["vitaminCMg"] < 100)
            throw new Exception("Blackcurrant lost its specific vitamin C reference.");
        Console.WriteLine($"PASS {checklist.Count} checklist variants: actual intake, minerals, preserved energy and recipe ingredients");
        foreach (var field in new[] { "MassG", "EnergyKJ", "WaterL" })
        {
            Near(Value(Dose(Stack("game:pineapple")), field), 12 * Value(Dose(Stack("game:fruit-pineapple")), field));
            Near(Value(Dose(Stack("game:redmeat-raw"), cooked:true), field), 4 * Value(Dose(Stack("primitivesurvival:jerky-redmeat-raw"), cooked:true), field));
            var slab = Stack("rusticcookery:meatslab-redmeat-raw"); slab.Attributes.SetInt("pieces", 2);
            Near(Value(Dose(slab), field), 2 * Value(Dose(Stack("game:redmeat-raw")), field));
            Near(Value(Dose(Stack("game:fat-rendered")), field), 4 * Value(Dose(Stack("hideracks:fat-chunk")), field));
        }
        var flour = Dose(Stack("game:flour-rye")); var dough = Dose(Stack("game:dough-rye"));
        Near(Value(dough,"MassG"), Value(flour,"MassG") + 1000);
        Near(Value(dough,"WaterL"), Value(flour,"WaterL") + 1);
        Near(Value(dough,"EnergyKJ"), Value(flour,"EnergyKJ"));
        Near(Value(Dose(Stack("game:bread-rye-perfect")),"EnergyKJ"), Value(flour,"EnergyKJ"));
        var milk = Stack("game:milkportion");
        milk.Item.Attributes = JsonObject.FromJson("""{"waterTightContainerProps":{"itemsPerLitre":100}}""");
        Near(Value(Dose(milk,25),"MassG"),257.5);
        var water = Stack("game:waterportion");
        water.Item.Attributes = milk.Item.Attributes;
        Near(Value(Dose(water,25,meal:true),"WaterL"),.25);
        foreach (var code in new[] { "envelopes:waxstick-plain", "em:causticsodaportion", "game:acidportion", "bodyphys:seasalt" })
        {
            var excluded = Stack(code); excluded.Item.NutritionProps = new() { Satiety = 20, FoodCategory = EnumFoodCategory.Unknown };
            Near(Value(Dose(excluded),"EnergyKJ"),0);
        }

        // Exercise retained stack serialization and resolution, including a fractional serving.
        var bridge = mod.GetType("Seraph.Server.RusticFoodBridge",true)!;
        var meat = Stack("game:redmeat-raw",101); var bowl = Stack("test:bowl",102);
        bridge.GetMethod("Main",BindingFlags.NonPublic|BindingFlags.Static)!.Invoke(null,
            [bowl, new[] { (meat, 6f) }, 6]);
        var world = (IWorldAccessor)RoutingProxy.Make(typeof(IWorldAccessor),(m,_) => m.Name == "GetItem" ? meat.Item : null);
        var result = Activator.CreateInstance(mod.GetType("Seraph.Server.FoodDose",true)!,true)!;
        if (!(bool)bridge.GetMethod("AddRetained",BindingFlags.NonPublic|BindingFlags.Static)!.Invoke(null,[catalog,world,bowl,result])!)
            throw new Exception("Retained main ingredient not selected.");
        Near(Value(result,"EnergyKJ"),Value(Dose(meat,cooked:true),"EnergyKJ"));
        Near(Value(result,"MassG"),Value(Dose(meat,cooked:true),"MassG"));
        var partial = Activator.CreateInstance(result.GetType(),true)!;
        result.GetType().GetMethod("Add",hidden)!.Invoke(partial,[result,.25d]);
        Near(Value(partial,"EnergyKJ")*4,Value(result,"EnergyKJ"));
        var recipes = new RecipeRegistrySystem();
        var loader = (IModLoader)RoutingProxy.Make(typeof(IModLoader), (m, _) => m.Name == "GetModSystem" ? recipes : null);
        var api = (ICoreAPI)RoutingProxy.Make(typeof(ICoreAPI), (m, _) => m.Name == "get_ModLoader" ? loader : null);
        var mealWorld = (IWorldAccessor)RoutingProxy.Make(typeof(IWorldAccessor), (m, _) => m.Name switch {
            "GetItem" => meat.Item, "get_Api" => api, _ => null });
        foreach (var units in new[] { .8d, .9d, 1d })
        {
            bowl.Attributes.GetTreeAttribute("vsslssmod:meal-ingredients-release-v1").SetDouble("ServingUnits", units);
            var perUnit = typeof(FoodCatalog).GetMethod("MealComposition", hidden)!.Invoke(catalog,
                [mealWorld, bowl, Array.Empty<ItemStack>(), false])!;
            Near(Value(perUnit,"EnergyKJ") * units, Value(result,"EnergyKJ"));
            Near(Value(perUnit,"MassG") * units, Value(result,"MassG"));
        }
        // Roux is one actual flour item split across the dish's servings, not a
        // configurable native satiety bonus. Keep it through bowl serialization.
        var flourStack = Stack("game:flour-rye", 103);
        var rouxBowl = Stack("test:roux-bowl", 104);
        var rouxTree = new TreeAttribute(); rouxTree.SetFloat("satiety", 9999);
        rouxBowl.Attributes["rusticcookeryRoux"] = rouxTree;
        var servingFlour = bridge.GetField("servingFlour", BindingFlags.NonPublic | BindingFlags.Static)!;
        servingFlour.SetValue(null, flourStack);
        try
        {
            bridge.GetMethod("Roux", BindingFlags.NonPublic | BindingFlags.Static)!.Invoke(null, [rouxBowl, 9999f, 4]);
        }
        finally { bridge.GetMethod("EndServing", BindingFlags.NonPublic | BindingFlags.Static)!.Invoke(null, [null, null]); }
        if (servingFlour.GetValue(null) is not null) throw new Exception("Roux serving context leaked.");
        var rouxWorld = (IWorldAccessor)RoutingProxy.Make(typeof(IWorldAccessor), (m, _) => m.Name switch {
            "GetItem" => flourStack.Item, "get_Api" => api, _ => null });
        var rouxDose = catalog.MealComposition(rouxWorld, rouxBowl, [], false);
        var flourDose = Dose(flourStack, cooked:true);
        foreach (var field in new[] { "MassG", "EnergyKJ", "WaterL" }) Near(Value(rouxDose,field)*4,Value(flourDose,field));
        foreach (var id in new[] { "calcium", "iron", "zinc", "a", "c", "bcomplex" }) Near(Nutrient(rouxDose,id)*4,Nutrient(flourDose,id));
        if (rouxDose.HasUnmapped) throw new Exception("Retained roux used a native food fallback.");
        var oldBowl = Stack("test:old-roux-bowl"); oldBowl.Attributes["rusticcookeryRoux"] = rouxTree;
        var oldDose = catalog.MealComposition(rouxWorld, oldBowl, [], false);
        if (oldDose.HasUnmapped || !oldDose.Known[Seraph.Core.Framework.Nutrients.IndexOf("bcomplex")])
            throw new Exception("Earlier roux bowls lost flour micronutrients.");
        Console.WriteLine("PASS exact roux flour, serving fractions, vitamin retention, no double credit and earlier bowl composition");
        Console.WriteLine("PASS release cuts, cooked yields, dough/baking, liquid units, exclusions and retained meal fractions");
    }

    private static void ConfigFiles(Assembly mod)
    {
        var folder = TestArtifacts.DirectoryPath("config-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(folder);
        try
        {
            var api = (ICoreAPI)RoutingProxy.Make(typeof(ICoreAPI), (method, _) =>
                method.Name == "GetOrCreateDataPath" ? folder : throw new Exception("Unexpected configuration API: " + method.Name));
            var reader = mod.GetType("Seraph.Server.ReleaseConfigFile", true)!.GetMethod("Read", BindingFlags.NonPublic | BindingFlags.Static)!;
            foreach (var type in new[] { typeof(FoodCatalog), typeof(Seraph.Application.SeraphServerConfig) })
            {
                var read = reader.MakeGenericMethod(type);
                var file = Path.Combine(folder, "config.json");
                if (read.Invoke(null, [api, "config.json"]) is not null) throw new Exception("Missing file must be distinct from corrupt data.");
                foreach (var invalid in new[] { "", "{", "null", "{}", "{\"Version\":1}", "{\"ConfigVersion\":1}" })
                {
                    File.WriteAllText(file, invalid); var bytes = File.ReadAllBytes(file);
                    try { read.Invoke(null, [api, "config.json"]); throw new Exception("Invalid configuration accepted: " + invalid); }
                    catch (TargetInvocationException error) when (error.InnerException is JsonException or InvalidDataException) { }
                    if (!File.ReadAllBytes(file).SequenceEqual(bytes)) throw new Exception("Rejected configuration overwritten.");
                }
                var valid = JsonSerializer.Serialize(Activator.CreateInstance(type), type);
                File.WriteAllText(file, valid);
                if (read.Invoke(null, [api, "config.json"]) is null) throw new Exception("Release configuration not restored.");
                File.Delete(file);
            }
        }
        finally { Directory.Delete(folder, true); }
        Console.WriteLine("PASS corrupt and RC config/food overrides rejected without changing original bytes");
    }
}
