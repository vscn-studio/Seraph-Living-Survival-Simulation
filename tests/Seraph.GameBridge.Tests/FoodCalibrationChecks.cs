using System.Text.Json;
using Seraph.Server;
using Vintagestory.API.Common;
using Vintagestory.API.Common.Entities;
using Vintagestory.API.Datastructures;

internal static class FoodCalibrationChecks
{
    internal static void Run(FoodCatalog catalog)
    {
        ItemStack Stack(string code, int count = 1) => new(new Item { Code = new(code) }, count);
        FoodProfile Profile(string code) => catalog.Find(Stack(code))!;
        double Amount(FoodProfile p, string key, double mass) => (p.Per100G.TryGetValue(key, out var v) ? v : p.MicroPer100G[key]) * mass / 100;
        void Near(double a, double b) { if (Math.Abs(a - b) > 1e-7) throw new Exception($"Calibration budget {a} != {b}"); }
        var milk = Profile("game:milkportion"); var meat = Profile("game:redmeat-raw");
        Near(32.445, Amount(milk, "protein", 1030)); Near(250, meat.PortionG);
        Near(46.475, Amount(meat, "protein", meat.PortionG));
        var cooked = Profile("game:redmeat-cooked");
        foreach (var key in new[] { "protein", "fat", "ironMg", "zincMg" })
            Near(Amount(meat, key, meat.PortionG), Amount(cooked, key, cooked.PortionG));
        var powder = Profile("pemmican:longmilk-dry");
        foreach (var key in new[] { "protein", "fat", "calciumMg", "ironMg", "zincMg" })
            Near(Amount(milk, key, 1030), Amount(powder, key, powder.PortionG));
        Near(100, Amount(Profile("pemmican:syrup-sugar"), "carbohydrate", 1040));
        BloodReserveBalance(catalog);

        var original = JsonObject.FromJson("""{"code":"harvestable","drops":[{"code":"redmeat-raw","quantity":{"avg":9,"var":1.5}},{"code":"hide-raw-medium","quantity":{"avg":1,"var":0}}]}""");
        var pig = new EntityProperties { Code = new("game:pig-eurasian-adult-male"), Weight = 150,
            Server = new EntityServerProperties([original], new()) };
        if (!FoodCalibration.CalibrateAnimal(pig, catalog)) throw new Exception("Pig harvest was not calibrated");
        var drops = pig.Server.BehaviorsAsJsonObj[0]["drops"].AsArray()!;
        Near(4500, drops[0]["quantity"]["avg"].AsDouble() * meat.PortionG);
        Near(3, drops[0]["quantity"]["var"].AsDouble());
        Near(1, drops[1]["quantity"]["avg"].AsDouble());
        Near(9, original["drops"].AsArray()![0]["quantity"]["avg"].AsDouble());
        if (FoodCalibration.CalibrateAnimal(pig, catalog)) throw new Exception("Yield calibration applied twice");

        // Lower the former meat budget to one tenth, including large deer. The
        // 250 g food portion and six portions per Rustic Cookery slab stay intact.
        foreach (var (code, weight, portions) in new[] {
            ("game:deer-whitetail-adult-male", 100f, 12d),
            ("game:deer-elk-adult-male", 450f, 54d) })
        {
            var deer = new EntityProperties { Code = new(code), Weight = weight,
                Server = new EntityServerProperties([original], new()) };
            if (!FoodCalibration.CalibrateAnimal(deer, catalog)) throw new Exception("Deer yield was not calibrated");
            var deerDrops = deer.Server.BehaviorsAsJsonObj[0]["drops"].AsArray()!;
            Near(portions, deerDrops[0]["quantity"]["avg"].AsDouble());
            Near(1, deerDrops[1]["quantity"]["avg"].AsDouble());
        }

        // A shared finished code must preserve cheap and rich input choices separately.
        var lean = Stack("game:grain-rice", 64); var rich = Stack("game:grain-flax", 64);
        FoodProfile Make(ItemStack input)
        {
            var output = Stack("pemmican:nutribar-general", 2);
            var recipe = new GridRecipe { ResolvedIngredients = [new MatchingIngredient(input, 2).WithQuantity()] };
            CraftedFoodNutrition.Capture(catalog, [new DummySlot(input)], output, recipe);
            var receipt = CraftedFoodNutrition.Read(output) ?? throw new Exception("Crafting did not retain input identity");
            foreach (var key in new[] { "protein", "fat", "zincMg", "vitaminAUgRAE" })
                Near(Amount(Profile(input.Collectible.Code.ToString()), key, 50), Amount(receipt, key, receipt.PortionG));
            if (input.StackSize != 64) throw new Exception("Metadata capture consumed inventory");
            return receipt;
        }
        var riceBar = Make(lean); var flaxBar = Make(rich);
        if (riceBar.Per100G["fat"] >= flaxBar.Per100G["fat"]) throw new Exception("Shared output upgraded rice into flax");
        var raw = Stack("pemmican:caramel-raw");
        var source = CraftedFoodNutrition.Combine(raw.Collectible.Code.ToString(), [(Profile("game:fruit-cherry"), 400)], 1, "raw");
        raw.Attributes.SetString(CraftedFoodNutrition.Key, JsonSerializer.Serialize(source));
        var dried = Stack("pemmican:caramel-dry"); CraftedFoodNutrition.Transfer(catalog, raw, dried);
        var preserved = CraftedFoodNutrition.Read(dried)!;
        foreach (var key in new[] { "protein", "carbohydrate", "ironMg", "zincMg" })
            Near(Amount(source, key, source.PortionG), Amount(preserved, key, preserved.PortionG));
        Near(Amount(source, "vitaminCMg", source.PortionG) * .5, Amount(preserved, "vitaminCMg", preserved.PortionG));
        if (preserved.PortionG >= source.PortionG) throw new Exception("Drying failed to remove water");
        Console.WriteLine("PASS meat yield, independent zinc, milk/starch/sugar budgets and selected crafting ingredient conservation");
    }

    private static void BloodReserveBalance(FoodCatalog catalog)
    {
        ItemStack Stack(string code) => new(new Item { Code = new(code) });
        FoodProfile Profile(string code) => catalog.Find(Stack(code))!;
        double Iron(ItemStack stack, double quantity = 1) => catalog.Composition(stack, quantity).Nutrients[Seraph.Core.Framework.Nutrients.IndexOf("iron")];
        void Near(double a, double b) { if (Math.Abs(a-b) > 1e-7) throw new Exception($"Blood reserve budget {a} != {b}"); }
        var blood = Stack("butchering:bloodportion");
        blood.Item.Attributes = JsonObject.FromJson("""{"waterTightContainerProps":{"itemsPerLitre":100}}""");
        Near(Profile("butchering:bloodportion").MicroPer100G["ironMg"], 49.57);
        Near(Iron(blood, 20), 11.45067);
        Near(Iron(Stack("butchering:bloodbread-spelt-perfect")), 15.52067);
        Near(Iron(Stack("butchering:sausage-blackpudding-cooked")), 53.09018);
        Near(Iron(Stack("butchering:sausage-bloodsausage-cooked")), 37.8965033333333);
        foreach (var family in new[] { "bloodsausage", "blackpudding" })
        foreach (var prepared in new[] { "cooked", "smoked" })
            Near(Iron(Stack($"butchering:sausage-{family}-raw")), Iron(Stack($"butchering:sausage-{family}-{prepared}")));
        // Combining and splitting must preserve the calibrated yield without lowering
        // iron from ordinary ingredients, or applying the blood scale twice.
        var parts = new[] { (Profile("butchering:bloodportion"), 210d), (Profile("game:redmeat-raw"), 250d) };
        var composed = CraftedFoodNutrition.Combine("pemmican:pemmican-raw", parts, 4, "raw");
        var raw = Stack("pemmican:pemmican-raw");
        raw.Attributes.SetString(CraftedFoodNutrition.Key, JsonSerializer.Serialize(composed));
        Near(Iron(raw, 4), Iron(blood, 20) + Iron(Stack("game:redmeat-raw")));
        var dried = Stack("pemmican:pemmican-bar");
        CraftedFoodNutrition.Transfer(catalog, raw, dried);
        Near(Iron(dried), Iron(raw));
        foreach (var code in new[] { "game:vegetable-pumpkin", "game:fruit-mango", "game:vegetable-bellpepper",
            "game:egg-chicken-raw", "game:milkportion", "game:cheese-cheddar-1slice" })
            if (catalog.Composition(Stack(code), 1).Nutrients[Seraph.Core.Framework.Nutrients.IndexOf("a")] <= 0)
                throw new Exception("Non-carrot vitamin A source missing: " + code);
        if (FoodMicronutrients.ValidReserveScale(new Dictionary<string, double> { ["ironMg"] = double.NaN })
            || FoodMicronutrients.ValidReserveScale(new Dictionary<string, double> { ["ironMg"] = 2 }))
            throw new Exception("Invalid reserve multiplier accepted.");
        Console.WriteLine("PASS blood iron yield, mixed/split/dried portions and non-carrot vitamin A sources");
    }

    private sealed class MatchingIngredient(ItemStack stack, int quantity) : CraftingRecipeIngredient
    {
        public override bool SatisfiesAsIngredient(ItemStack inputStack, bool checkStackSize = true) => inputStack.Collectible == stack.Collectible;
        public override RecipeIngredientConsumeProperties GetConsumeProperties() => new() { Consume = true, Quantity = quantity };
        // Quantity is read by Capture in addition to ConsumeProperties.
        public MatchingIngredient WithQuantity() { Quantity = quantity; return this; }
    }
}
