using System.Reflection;
using System.Globalization;
using HarmonyLib;
using Vintagestory.API.Client;
using Vintagestory.API.Common;
using Vintagestory.API.Config;
using Vintagestory.API.Datastructures;
using Vintagestory.GameContent;

internal static class RecipeMetadataChecks
{
    internal static void Run(Assembly mod, string gamePath)
    {
        T Proxy<T>(System.Func<MethodInfo, object?[]?, object?> handler) => (T)RoutingProxy.Make(typeof(T), handler);
        var items = new[] { "salt", "redmeat-raw", "redmeat-cured", "vegetable-carrot", "pickledvegetable-carrot",
            "waterportion", "brineportion", "saltwaterportion", "flour-flax", "dough-flax", "oilportion-flax" }
            .Select((code, i) => new Item { Code = new("game:" + code), ItemId = i + 100 }).ToList();
        foreach (var item in items.Where(i => i.Code.Path.EndsWith("portion") || i.Code.Path == "oilportion-flax"))
            item.Attributes = JsonObject.FromJson("{\"waterTightContainerProps\":{\"containable\":true,\"itemsPerLitre\":100}}");
        var recipeWarnings = new List<string>();
        var logger = Proxy<ILogger>((m, a) =>
        {
            if (m.Name == "Error") throw new Exception("Recipe resolution: " + string.Join(" ", a ?? []));
            if (m.Name == "Warning" && a is { Length: > 0 } && a[0] is string format)
            {
                // Reflection exposes a params object[] argument as one nested array.
                var values = a.Skip(1).SelectMany(value => value is object[] nested ? nested : new object[] { value! }).ToArray();
                recipeWarnings.Add(string.Format(CultureInfo.InvariantCulture, format, values));
            }
            return null;
        });
        var fastSearch = new System.Collections.Generic.OrderedDictionary<IRecipeIngredientBase, List<IRecipeBase>>();
        var bucket = new BlockLiquidContainerTopOpened { Code = new("game:woodbucket"), BlockId = 1000 };
        var blocks = new List<Block> { bucket };
        var world = Proxy<Vintagestory.API.Server.IServerWorldAccessor>((m, a) => m.Name switch {
            "get_Items" => items, "get_Blocks" => blocks, "get_Logger" => logger,
            "get_FastSearchRecipesByIngredient" => fastSearch,
            "GetItem" => items.SingleOrDefault(i => a![0] is int id ? i.Id == id : i.Code.Equals((AssetLocation)a[0]!)),
            "GetBlock" => blocks.SingleOrDefault(i => a![0] is int id ? i.Id == id : i.Code.Equals((AssetLocation)a[0]!)),
            _ => throw new Exception("Unexpected world " + m.Name)
        });
        var events = Proxy<IEventAPI>((m, _) => m.Name == "TriggerMatchesRecipe" ? true : null);
        var api = Proxy<ICoreAPI>((m, _) => m.Name switch { "get_World" => world, "get_Event" => events, _ => null });
        foreach (var item in items.Cast<CollectibleObject>().Concat(blocks)) AccessTools.Field(typeof(CollectibleObject), "api").SetValue(item, api);
        ItemStack Stack(string code, int count = 1) => new(items.Single(i => i.Code.Path == code), count);
        void Tag(ItemStack stack)
        {
            stack.Attributes.SetString("vsslssmod:pathogens-v1", "{\"shigella\":0.2}");
            stack.Attributes.SetDouble("vsslssmod:manureMassG", 10);
            if (stack.Collectible.Code.Path == "waterportion")
            {
                mod.GetType("Seraph.World.WaterMetadata")!.GetMethod("Write")!.Invoke(null,
                    [stack, new Seraph.Core.Framework.WaterBatch { VolumeL = 1, TemperatureC = 22 }]);
                stack.Attributes.SetDouble("vsslssmod:water-temperature-updated-at", 123);
            }
        }
        BarrelRecipe LoadBarrel(string name, string output)
        {
            var recipe = JsonObject.FromJson(File.ReadAllText(Path.Combine(gamePath, "assets/survival/recipes/barrel/" + name + ".json")))
                .AsObject<BarrelRecipe>()!;
            recipe.OnParsed(world);
            var selected = recipe.GenerateRecipesForAllIngredientCombinations(world).Cast<BarrelRecipe>()
                .Single(r => r.Output!.Code.Path == output);
            Require(selected.Resolve(world, name), name + " resolve");
            return selected;
        }
        RecipeBase.CollectiblePreSearchResultsCache.Clear();
        try
        {
            var meat = LoadBarrel("curedmeat", "redmeat-cured");
            var carrot = LoadBarrel("pickledvegetable", "pickledvegetable-carrot");
            foreach (var tagged in new[] { false, true })
            {
                ItemSlot[] meatSlots = [new DummySlot(Stack("redmeat-raw")), new DummySlot(Stack("salt", 2))];
                ItemSlot[] carrotSlots = [new DummySlot(Stack("vegetable-carrot")), new DummySlot(Stack("brineportion", 200))];
                if (tagged) foreach (var slot in meatSlots.Concat(carrotSlots)) Tag(slot.Itemstack!);
                Require(meat.Matches(meatSlots, out var count) && count == 1, "cured meat match tagged=" + tagged);
                Require(carrot.Matches(carrotSlots, out count) && count == 1, "pickled carrot match tagged=" + tagged);
                Require(!meat.TryCraftNow(api, 479, meatSlots) && !carrot.TryCraftNow(api, 335, carrotSlots), "early barrel completion");
                var brineAttributes = carrotSlots[1].Itemstack!.Attributes.ToJsonToken().ToString();
                Require(meat.TryCraftNow(api, 480, meatSlots) && meatSlots[0].Itemstack!.Collectible.Code.Path == "redmeat-cured"
                    && meatSlots[0].StackSize == 1 && meatSlots[1].Empty, "meat consumption");
                Require(carrot.TryCraftNow(api, 336, carrotSlots) && carrotSlots[0].Itemstack!.Collectible.Code.Path == "pickledvegetable-carrot"
                    && carrotSlots[0].StackSize == 1 && carrotSlots[1].StackSize == 100, "carrot consumption");
                Require(carrotSlots[1].Itemstack!.Attributes.ToJsonToken().ToString() == brineAttributes, "leftover brine metadata lost");
            }
            Console.WriteLine("PASS native curing and pickling: tagged inputs match, sealing times, consumption and leftover metadata");
            foreach (var liquid in new[] { "waterportion", "saltwaterportion" })
                Require(!carrot.Matches([new DummySlot(Stack("vegetable-carrot")), new DummySlot(Stack(liquid, 100))], out _), "wrong pickling liquid");
            Require(!carrot.Matches([new DummySlot(Stack("vegetable-carrot")), new DummySlot(Stack("brineportion", 99))], out _), "insufficient brine");
            Require(!meat.Matches([new DummySlot(Stack("redmeat-raw")), new DummySlot(Stack("salt"))], out _), "insufficient salt");
            var oil = JsonObject.FromJson(File.ReadAllText(Path.Combine(gamePath, "assets/survival/recipes/cooking/oil.json")))
                .AsObject<CookingRecipe[]>()!.Single(r => r.Code == "linseed");
            oil.Resolve(world, "linseed");
            var water = Stack("waterportion", 100);
            var ingredient = oil.Ingredients!.Single(i => i.Code == "water");
            Require(ingredient.GetMatchingStack(water) is not null, "plain linseed water");
            Tag(water);
            var saved = water.Attributes.ToJsonToken().ToString();
            Require(ingredient.GetMatchingStack(water) is null, "test must reproduce unpatched oil failure");
            using var handbook = new BarrelHandbookFixture(gamePath, [meat, carrot]);
            var cured = Stack("redmeat-cured"); Tag(cured);
            var pickled = Stack("pickledvegetable-carrot"); Tag(pickled);
            Require(handbook.Components(cured).Count == 0 && handbook.Components(pickled).Count == 0,
                "test must reproduce unpatched handbook recipe disappearance");
            var type = mod.GetType("Seraph.World.RecipeMetadataCompatibility", true)!;
            using var bridge = (IDisposable)Activator.CreateInstance(type,
                BindingFlags.Instance | BindingFlags.NonPublic, null, [logger], null)!;
            Require(ingredient.GetMatchingStack(water) is not null, "tagged linseed water");
            Require(water.Attributes.ToJsonToken().ToString() == saved, "recipe matching mutated real water");
            var flour = Stack("flour-flax"); Tag(flour);
            ItemStack[] oilInputs = [water, flour, flour.Clone(), flour.Clone()];
            Require(oil.Matches(oilInputs) && oil.GetQuantityServings(oilInputs) == 1, "complete linseed recipe failed");
            water.StackSize = 99;
            Require(!oil.Matches(oilInputs), "oil accepts insufficient water"); water.StackSize = 100;
            Require(!oil.Matches([Stack("saltwaterportion", 100), flour, flour.Clone(), flour.Clone()]), "oil accepts seawater");
            var unrelated = Stack("salt");
            unrelated.Attributes.SetString("thirdparty:identity", "special");
            for (var i = 0; i < 1000; i++)
                Require(ingredient.GetMatchingStack(unrelated) is null, "unrelated cooking ingredient matched");
            Require(recipeWarnings.Count == 0, "unrelated collectibles generated metadata warnings");
            water.Attributes.SetString("thirdparty:identity", "special");
            water.Attributes.SetInt("timeFrozen", 1);
            Require(ingredient.GetMatchingStack(water) is null, "foreign attributes ignored");
            water.Attributes.SetString("thirdparty:other", "different key set");
            Require(ingredient.GetMatchingStack(water) is null, "repeat comparison loosened matching");
            Require(recipeWarnings.Count == 1
                && recipeWarnings[0].Contains("recipeCode=<unavailable>", StringComparison.Ordinal)
                && recipeWarnings[0].Contains("thirdparty:identity:String", StringComparison.Ordinal)
                && !recipeWarnings[0].Contains("timeFrozen", StringComparison.Ordinal)
                && !recipeWarnings[0].Contains("recipeCode=game:waterportion", StringComparison.Ordinal),
                "metadata mismatch diagnostic must show the unknown key and value type without mislabeling an item as a recipe");
            water.Attributes.RemoveAttribute("thirdparty:identity");
            water.Attributes.RemoveAttribute("thirdparty:other");
            water.Attributes.RemoveAttribute("timeFrozen");
            var required = new JsonItemStack { Type = EnumItemClass.Item, Code = new("game:waterportion"), ResolvedItemStack = Stack("waterportion") };
            Require(required.Matches(world, water), "JSON liquid requirement failed");
            required.ResolvedItemStack.Attributes.SetString("vsslssmod:pathogens-v1", "{}");
            Require(!required.Matches(world, water), "explicit pathogen requirement bypassed");
            required.ResolvedItemStack.Attributes.SetString("vsslssmod:pathogens-v1", water.Attributes.GetString("vsslssmod:pathogens-v1"));
            Require(required.Matches(world, water), "explicit matching pathogen requirement failed");
            var expectedCooking = ingredient.ValidStacks[0].ResolvedItemstack!;
            expectedCooking.Attributes.SetString("vsslssmod:pathogens-v1", "{}");
            Require(ingredient.GetMatchingStack(water) is null, "explicit cooking requirement bypassed");
            expectedCooking.Attributes.RemoveAttribute("vsslssmod:pathogens-v1");
            Require(!Stack("waterportion").Equals(world, water, GlobalConstants.IgnoredStackAttributes), "inventory equality was loosened");
            var dough = JsonObject.FromJson(File.ReadAllText(Path.Combine(gamePath, "assets/survival/recipes/grid/dough.json")))
                .AsObject<GridRecipe[]>()![0];
            dough.OnParsed(world);
            dough = (GridRecipe)dough.GenerateRecipesForAllIngredientCombinations(world).Single();
            Require(dough.Resolve(world, "dough"), "dough resolve");
            var entity = new EntityPlayer { Api = api, World = world };
            var player = FixturePlayer.Make(entity, Proxy<IPlayerInventoryManager>((_, _) => null));
            var held = new ItemStack(bucket); var fullWater = water.Clone(); fullWater.StackSize = 200;
            bucket.SetContent(held, fullWater);
            ItemSlot[] doughSlots = [new DummySlot(held), new DummySlot(flour)];
            Require(dough.Matches(player, world, doughSlots, 2), "tagged dough match");
            Require(dough.ConsumeInput(player, doughSlots, 2) && doughSlots[1].Empty && doughSlots[0].StackSize == 1,
                "dough consumed container or retained flour");
            var remaining = bucket.GetContent(doughSlots[0].Itemstack!)!;
            Require(remaining.StackSize == 100 && remaining.Attributes.ToJsonToken().ToString() == saved, "dough water consumption or metadata loss");
            var shortWater = water.Clone(); shortWater.StackSize = 99; bucket.SetContent(held, shortWater);
            doughSlots[1].Itemstack = flour.Clone();
            Require(!dough.Matches(player, world, doughSlots, 2), "dough accepts insufficient water");
            bucket.SetContent(held, Stack("brineportion", 200));
            Require(!dough.Matches(player, world, doughSlots, 2), "dough accepts brine");
            foreach (var output in new[] { cured, pickled })
            {
                var before = output.Attributes.ToJsonToken().ToString();
                var components = handbook.Components(output);
                Require(components.OfType<SlideshowItemstackTextComponent>().Count() == 3,
                    "native handbook failed to show both ingredients and output: " + output.Collectible.Code);
                Require(output.Attributes.ToJsonToken().ToString() == before, "handbook changed stored food");
                foreach (var component in components) component.Dispose();
            }
            Console.WriteLine("PASS native oil, dough consumption and curing/pickling handbook; required attributes, quantities, liquids and real metadata preserved");
            for (var i = 0; i < 64; i++)
            {
                var item = new Item { Code = new("test:ingredient-" + i), ItemId = 2000 + i };
                AccessTools.Field(typeof(CollectibleObject), "api").SetValue(item, api);
                ingredient.ValidStacks[0].ResolvedItemStack = new ItemStack(item);
                var input = new ItemStack(item);
                input.Attributes.SetString("thirdparty:identity", "special");
                Require(ingredient.GetMatchingStack(input) is null, "diagnostic cap loosened matching");
            }
            Require(recipeWarnings.Count == 32, "expected 32 sampled warnings across all comparisons");
            Console.WriteLine("PASS recipe diagnostics: 1000 unrelated comparisons silent, repeated item sampled once, 32-warning limit");
        }
        finally { RecipeBase.CollectiblePreSearchResultsCache.Clear(); }
    }
    private static void Require(bool condition, string message) { if (!condition) throw new Exception(message); }

    private sealed class BarrelHandbookFixture : IDisposable
    {
        private readonly ICoreClientAPI client;
        private readonly ITranslationService? previous;
        private readonly string originalLocale = Lang.CurrentLocale;
        private const string locale = "en";
        private readonly float originalScale = RuntimeEnv.GUIScale;
        internal BarrelHandbookFixture(string gamePath, List<BarrelRecipe> recipes)
        {
            System.Runtime.InteropServices.NativeLibrary.SetDllImportResolver(typeof(Cairo.Context).Assembly, (name, _, _) =>
                System.Runtime.InteropServices.NativeLibrary.Load(Path.Combine(gamePath, "Lib", name == "nanosvg" ? "nanosvg.dll" : "libcairo-2.dll")));
            Lang.AvailableLanguages.TryGetValue(locale, out previous);
            var translation = new TranslationService(locale, null!, null!);
            AccessTools.Field(typeof(TranslationService), "entryCache").SetValue(translation, new Dictionary<string, string>());
            AccessTools.Field(typeof(TranslationService), "loaded").SetValue(translation, true);
            Lang.AvailableLanguages[locale] = translation;
            Lang.ChangeLanguage(locale);
            RuntimeEnv.GUIScale = 1;
            var registry = new RecipeRegistrySystem { BarrelRecipes = recipes };
            var loader = (IModLoader)RoutingProxy.Make(typeof(IModLoader), (m, _) => m.Name == "GetModSystem" ? registry : null);
            var calendar = (IClientGameCalendar)RoutingProxy.Make(typeof(IClientGameCalendar), (m, _) => m.Name == "get_HoursPerDay" ? 24f : null);
            var world = (IClientWorldAccessor)RoutingProxy.Make(typeof(IClientWorldAccessor), (m, _) => m.Name == "get_Calendar" ? calendar : null);
            var classes = (IClassRegistryAPI)RoutingProxy.Make(typeof(IClassRegistryAPI), (_, _) => null);
            client = (ICoreClientAPI)RoutingProxy.Make(typeof(ICoreClientAPI), (m, _) => m.Name switch {
                "get_ModLoader" => loader, "get_World" => world, "get_ClassRegistry" => classes, "get_Side" => EnumAppSide.Client, _ => null
            });
        }
        internal List<RichTextComponentBase> Components(ItemStack stack)
            => (List<RichTextComponentBase>)AccessTools.Method(typeof(CollectibleBehaviorHandbookTextAndExtraInfo), "BuildBarrelRecipesText")
                .Invoke(new CollectibleBehaviorHandbookTextAndExtraInfo(stack.Collectible), [client, stack,
                    new ActionConsumable<string>(_ => true)])!;
        public void Dispose()
        {
            if (previous is null) Lang.AvailableLanguages.Remove(locale); else Lang.AvailableLanguages[locale] = previous;
            Lang.ChangeLanguage(originalLocale);
            RuntimeEnv.GUIScale = originalScale;
        }
    }
}
