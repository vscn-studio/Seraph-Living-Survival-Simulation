using System.Reflection;
using System.Runtime.CompilerServices;
using System.Runtime.Loader;
using System.Text.Json;
using HarmonyLib;
using Seraph.Core.Framework;
using Seraph;
using Seraph.Server;
using Vintagestory.API.Common;
using Vintagestory.API.Datastructures;
using Vintagestory.API.MathTools;
using Vintagestory.API.Server;
using Vintagestory.GameContent;

internal static class PreservedFoodChecks
{
    internal static void Run(FoodCatalog catalog)
    {
        ItemStack Stack(string code) => new(new Item { Code = new(code) });
        FoodProfile Profile(string code) => catalog.Find(Stack(code))!;
        double Sodium(FoodProfile p) => p.SodiumMgPer100G * p.PortionG / 100;
        double Potassium(FoodProfile p) => p.PotassiumMgPer100G * p.PortionG / 100;
        double Electrolytes(ItemStack s, double n = 1, bool cooked = false)
            => catalog.Composition(s, n, cooked: cooked).Nutrients[Nutrients.IndexOf("electrolytes")];
        foreach (var kind in new[] { "bloodsausage", "blackpudding" })
        {
            var raw = Profile($"butchering:sausage-{kind}-raw");
            var cooked = Profile($"butchering:sausage-{kind}-cooked");
            var smoked = Profile($"butchering:sausage-{kind}-smoked");
            var blood = Profile("butchering:bloodportion");
            var casing = Profile("butchering:offal-clean");
            var filling = Profile(kind == "bloodsausage" ? "game:redmeat-raw" : "game:fat-rendered");
            var bloodUnits = kind == "bloodsausage" ? 2 : 8;
            var fillingUnits = kind == "bloodsausage" ? 2 : 1;
            Near(Sodium(raw), Sodium(blood) * bloodUnits + Sodium(casing) + Sodium(filling) * fillingUnits);
            Near(Potassium(raw), Potassium(blood) * bloodUnits + Potassium(casing) + Potassium(filling) * fillingUnits);
            foreach (var output in new[] { cooked, smoked })
            {
                Near(Sodium(raw), Sodium(output)); Near(Potassium(raw), Potassium(output));
                foreach (var key in new[] { "calciumMg", "ironMg", "zincMg" })
                    Near(raw.MicroPer100G[key] * raw.PortionG, output.MicroPer100G[key] * output.PortionG);
                var stack = Stack(output.Codes[0]);
                Near(Electrolytes(stack), Electrolytes(stack, .25) * 4);
                Near(Electrolytes(stack), Electrolytes(stack, cooked: true));
                Near(Electrolytes(Stack(raw.Codes[0])), Electrolytes(stack));
            }
            // Smoking the raw sausage is one heating stage, just like cooking it.
            foreach (var key in FoodMicronutrients.CookingRetention.Keys)
                Near(cooked.MicroPer100G[key] * cooked.PortionG, smoked.MicroPer100G[key] * smoked.PortionG);
        }
        foreach (var kind in new[] { "redmeat", "bushmeat", "fish", "poultry" })
        {
            var raw = Profile($"game:{kind}-raw");
            var cooked = Profile($"game:{kind}-cooked");
            var cured = Profile($"game:{kind}-cured");
            var cookedCured = Profile(cured.CookedReference!);
            foreach (var key in FoodMicronutrients.CookingRetention.Keys)
                Near(cookedCured.MicroPer100G[key] * cookedCured.PortionG, cooked.MicroPer100G[key] * cooked.PortionG);
            var saltSodium = raw.PortionG * .02 * .393 * 1000;
            Near(Sodium(cooked), Sodium(raw)); Near(Potassium(cooked), Potassium(raw));
            Near(Sodium(cured), Sodium(raw) + saltSodium); Near(Potassium(cured), Potassium(raw));
            // A cooked reference must not replace the cured food with unsalted meat.
            Near(Electrolytes(Stack(cured.Codes[0])), Electrolytes(Stack(cured.Codes[0]), cooked: true));
            foreach (var state in new[] { "raw", "partbaked", "cooked", "charred" })
            {
                var plain = Profile($"primitivesurvival:smokedmeat-{kind}-{state}");
                var salted = Profile($"primitivesurvival:curedsmokedmeat-{kind}-{state}");
                Near(Sodium(salted), Sodium(plain) + saltSodium); Near(Potassium(salted), Potassium(plain));
            }
            if (kind == "poultry") continue;
            var smoked = Profile($"game:{kind}-smoked");
            Near(Sodium(smoked), Sodium(raw)); Near(Potassium(smoked), Potassium(raw));
            foreach (var code in new[] { $"game:{kind}-cured-smoked", $"butchering:smoked-cured-{kind}" })
            {
                var salted = Profile(code);
                Near(Sodium(salted), Sodium(cured)); Near(Potassium(salted), Potassium(cured));
            }
            var strip = Profile($"primitivesurvival:jerky-{kind}-cured");
            Near(Sodium(strip) * 4, Sodium(cured)); Near(Potassium(strip) * 4, Potassium(cured));
        }
        var prime = Profile("butchering:primemeat-curedhealing");
        var smokedPrime = Profile("butchering:smoked-healing-primemeat");
        Near(Sodium(prime), Sodium(smokedPrime)); Near(Potassium(prime), Potassium(smokedPrime));
        foreach (var grain in new[] { "flax", "sunflower", "rye", "amaranth", "spelt", "rice", "millet", "cassava" })
        {
            var flour = Profile($"game:flour-{grain}");
            foreach (var code in new[] { $"game:dough-{grain}", $"game:bread-{grain}-perfect" })
            {
                Near(Sodium(flour), Sodium(Profile(code))); Near(Potassium(flour), Potassium(Profile(code)));
            }
        }
        // Retained recipe metadata follows the same mass ledger as catalog profiles.
        var input = Stack("pemmican:caramel-raw");
        var recipe = CraftedFoodNutrition.Combine(input.Collectible.Code.ToString(), [(Profile("game:fruit-cherry"), 400)], 1, "raw");
        input.Attributes.SetString(CraftedFoodNutrition.Key, JsonSerializer.Serialize(recipe));
        var dried = Stack("pemmican:caramel-dry");
        CraftedFoodNutrition.Transfer(catalog, input, dried);
        var retained = CraftedFoodNutrition.Read(dried)!;
        Near(Sodium(recipe), Sodium(retained)); Near(Potassium(recipe), Potassium(retained));
        Near(Electrolytes(input), Electrolytes(dried));
        SausageIngredients(catalog);
        Console.WriteLine("PASS preserved food Na/K budgets, curing salt units, potassium, serving fractions and single-stage vitamin retention");
    }

    private static void SausageIngredients(FoodCatalog catalog)
    {
        var items = new[] { "game:redmeat-raw", "game:redmeat-cured", "butchering:offal-clean", "butchering:bloodportion" }
            .Select((code, i) => new Item { Code = new(code), ItemId = i + 100 }).ToArray();
        items[3].Attributes = JsonObject.FromJson("""{"waterTightContainerProps":{"itemsPerLitre":100}}""");
        var bucket = new BlockLiquidContainerTopOpened { Code = new("game:woodbucket"), BlockId = 1000 };
        var world = (IWorldAccessor)RoutingProxy.Make(typeof(IWorldAccessor), (m, a) =>
            m.Name == "GetItem" ? items.Single(i => i.Id == (int)a![0]!) : null);
        var api = (ICoreAPI)RoutingProxy.Make(typeof(ICoreAPI), (m, _) => m.Name == "get_World" ? world : null);
        AccessTools.Field(typeof(CollectibleObject), "api").SetValue(bucket, api);
        ItemStack Make(bool cured)
        {
            var meat = new ItemStack(items[cured ? 1 : 0], 64);
            var casing = new ItemStack(items[2], 64);
            var liquid = new ItemStack(items[3], 1000);
            var held = new ItemStack(bucket);
            bucket.SetContent(held, liquid);
            var recipe = new GridRecipe {
                ResolvedIngredients = [new Ingredient(meat, 2), new Ingredient(casing, 1), new Ingredient(held, 1)],
                Attributes = JsonObject.FromJson("""{"liquidContainerProps":{"requiresLitres":0.2}}""")
            };
            var output = new ItemStack(new Item { Code = new("butchering:sausage-bloodsausage-raw"),
                Attributes = JsonObject.FromJson("""{"transformsWhenSmoked":"butchering:sausage-bloodsausage-smoked"}""") });
            CraftedFoodNutrition.Capture(catalog, [new DummySlot(meat), new DummySlot(casing), new DummySlot(held)], output, recipe);
            if (CraftedFoodNutrition.Read(output) is null) throw new Exception("Butchering sausage did not retain actual ingredients");
            Near(catalog.Composition(output, 1).Nutrients[Nutrients.IndexOf("electrolytes")],
                catalog.Composition(meat, 2).Nutrients[Nutrients.IndexOf("electrolytes")] + catalog.Composition(casing, 1).Nutrients[Nutrients.IndexOf("electrolytes")] + catalog.Composition(liquid, 20).Nutrients[Nutrients.IndexOf("electrolytes")]);
            if (meat.StackSize != 64 || bucket.GetContent(held)!.StackSize != 1000) throw new Exception("Nutrition capture consumed ingredients");
            return output;
        }
        var plain = Make(false); var salted = Make(true);
        Near(catalog.Composition(salted, 1).Nutrients[Nutrients.IndexOf("electrolytes")] - catalog.Composition(plain, 1).Nutrients[Nutrients.IndexOf("electrolytes")], 2 * 250 * .02 * .393 * .8);
        foreach (var input in new[] { plain, salted })
        {
            var cooked = new ItemStack(new Item { Code = new("butchering:sausage-bloodsausage-cooked") });
            CraftedFoodNutrition.Transfer(catalog, input, cooked);
            Near(catalog.Composition(input, 1).Nutrients[Nutrients.IndexOf("electrolytes")], catalog.Composition(cooked, 1).Nutrients[Nutrients.IndexOf("electrolytes")]);
            // The hook replaces the stack directly rather than calling OnTransitionNow.
            var inventory = new InventoryGeneric(1, "test-sausage", null);
            inventory[0].Itemstack = new(new Item { Code = new("butchering:sausage-bloodsausage-smoked") });
            NativeFoodBridge.TransferSmokedComposition(catalog, inventory, [input]);
            var smoked = inventory[0].Itemstack!;
            Near(catalog.Composition(input, 1).Nutrients[Nutrients.IndexOf("electrolytes")], catalog.Composition(smoked, 1).Nutrients[Nutrients.IndexOf("electrolytes")]);
            Near(catalog.Composition(cooked, 1).Nutrients[Nutrients.IndexOf("bcomplex")],
                catalog.Composition(smoked, 1).Nutrients[Nutrients.IndexOf("bcomplex")]);
            var saved = smoked.Attributes.GetString(CraftedFoodNutrition.Key);
            NativeFoodBridge.TransferSmokedComposition(catalog, inventory, [input]);
            if (smoked.Attributes.GetString(CraftedFoodNutrition.Key) != saved) throw new Exception("Smoking credited recipe twice");
        }
        Console.WriteLine("PASS blood sausage raw/cured recipe inputs, actual blood volume and retained cooked/smoked nutrition");
    }

    // Exercise the optional Harmony hook against the installed Butchering DLL.
    internal static void NativeButchering(string dll)
    {
        var assembly = AssemblyLoadContext.Default.LoadFromAssemblyPath(Path.GetFullPath(dll));
        var hookType = assembly.GetType("Butchering.src.common.blockentity.BlockEntityMeatHook", true)!;
        var smoke = AccessTools.DeclaredMethod(hookType, "smokeContents", [typeof(float)]);
        var path = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "../../../../../assets/vsslssmod/config/foods.json"));
        var catalog = JsonSerializer.Deserialize<FoodCatalog>(File.ReadAllText(path))!;
        var owner = (ServerCoordinator)RuntimeHelpers.GetUninitializedObject(typeof(ServerCoordinator));
        AccessTools.Field(typeof(ServerCoordinator), "foods").SetValue(owner, catalog);
        var mod = new SeraphModSystem();
        AccessTools.Field(typeof(SeraphModSystem), "server").SetValue(mod, owner);
        T Proxy<T>(System.Func<MethodInfo, object?[]?, object?> handler) => (T)RoutingProxy.Make(typeof(T), handler);
        var loader = Proxy<IModLoader>((m, _) => m.Name == "GetModSystem" ? mod : null);
        var pit = new BlockEntityFirepit();
        AccessTools.Field(typeof(BlockEntityFirepit), "fuelBurnTime").SetValue(pit, 10f);
        var calendar = Proxy<IGameCalendar>((m, _) => m.Name == "get_ElapsedHours" ? 5d : null);
        var blocks = Proxy<IBlockAccessor>((m, _) => m.Name == "GetBlockEntity" ? pit : null);
        var smokedItem = new Item { Code = new("butchering:sausage-bloodsausage-smoked"), ItemId = 123 };
        var world = Proxy<IServerWorldAccessor>((m, _) => m.Name switch {
            "get_Calendar" => calendar, "get_BlockAccessor" => blocks, "GetItem" => smokedItem, _ => null });
        var api = Proxy<ICoreServerAPI>((m, _) => m.Name switch {
            "get_World" => world, "get_ModLoader" => loader, "get_Side" => EnumAppSide.Server, _ => null });
        using var bridge = new NativeFoodBridge(api);
        if (!Harmony.GetPatchInfo(smoke).Owners.Contains("vsslssmod.food"))
            throw new Exception("Butchering smoking hook was not installed");
        foreach (var cured in new[] { false, true })
        {
            ItemStack Stack(string code) => new(new Item { Code = new(code) });
            var meat = catalog.Find(Stack(cured ? "game:redmeat-cured" : "game:redmeat-raw"))!;
            var blood = catalog.Find(Stack("butchering:bloodportion"))!;
            var casing = catalog.Find(Stack("butchering:offal-clean"))!;
            var input = Stack("butchering:sausage-bloodsausage-raw");
            input.Item.Attributes = JsonObject.FromJson("""{"transformsWhenSmoked":"butchering:sausage-bloodsausage-smoked"}""");
            var profile = CraftedFoodNutrition.Combine(input.Collectible.Code.ToString(),
                [(meat, meat.PortionG * 2), (blood, blood.PortionG * 2), (casing, casing.PortionG)], 1, "raw");
            input.Attributes.SetString(CraftedFoodNutrition.Key, JsonSerializer.Serialize(profile));
            var hook = (BlockEntityDisplay)Activator.CreateInstance(hookType)!;
            hook.Api = api; hook.Pos = new BlockPos(0, 64, 0);
            hook.Inventory[0].Itemstack = input;
            smoke.Invoke(hook, [5f]);
            var output = hook.Inventory[0].Itemstack!;
            if (ReferenceEquals(input, output) || output.Collectible.Code != smokedItem.Code || CraftedFoodNutrition.Read(output) is null)
                throw new Exception("Native Butchering smoking lost the selected ingredients");
            foreach (var id in new[] { "electrolytes", "protein", "iron" })
                Near(catalog.Composition(input, 1).Nutrients[Nutrients.IndexOf(id)],
                    catalog.Composition(output, 1).Nutrients[Nutrients.IndexOf(id)]);
            smoke.Invoke(hook, [5f]);
            if (!ReferenceEquals(output, hook.Inventory[0].Itemstack)) throw new Exception("Finished sausage smoked twice");
        }
        Console.WriteLine("PASS installed Butchering DLL Harmony hook and actual smoking replacement preserve raw/cured sausage nutrition");
    }

    private static void Near(double a, double b)
    {
        if (Math.Abs(a - b) > 1e-7) throw new Exception($"Preserved food nutrition budget {a} != {b}");
    }

    private sealed class Ingredient : CraftingRecipeIngredient
    {
        private readonly ItemStack input;
        internal Ingredient(ItemStack input, int quantity) { this.input = input; Quantity = quantity; }
        public override bool SatisfiesAsIngredient(ItemStack stack, bool checkStackSize = true) => stack.Collectible == input.Collectible;
        public override RecipeIngredientConsumeProperties GetConsumeProperties() => new() { Consume = true, Quantity = Quantity };
    }
}
