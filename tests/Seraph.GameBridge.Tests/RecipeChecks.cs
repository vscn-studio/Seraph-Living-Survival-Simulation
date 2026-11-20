using System.Reflection;
using Vintagestory.API.Common;
using Vintagestory.API.Datastructures;
using Vintagestory.GameContent;

internal static class RecipeChecks
{
    internal static void Run(string root)
    {
        T Proxy<T>(System.Func<MethodInfo, object?[]?, object?> handler) => (T)RoutingProxy.Make(typeof(T), handler);
        var blocks = new List<Block>();
        var items = new List<Item>();
        foreach (var code in new[] { "rock-granite", "rock-andesite", "cobblestone-granite", "cobblestone-andesite",
            "planks-oak", "planks-pine", "log-oak-ud", "log-pine-ud" })
            blocks.Add(new() { Code = new("game:" + code), BlockId = blocks.Count + 1000 });
        foreach (var code in new[] { "chimney", "bathtub", "copperbathtub", "latrine", "wellhead", "wellwall", "filterpot" })
            blocks.Add(new() { Code = new("vsslssmod:" + code), BlockId = blocks.Count + 1000 });
        foreach (var code in new[] { "stone-granite", "stone-andesite", "clay-blue", "clay-fire", "plank-oak", "plank-pine",
            "metalnailsandstrips-copper", "metalnailsandstrips-iron", "chisel-copper", "knife-copper", "axe-copper",
            "metalplate-copper", "stick", "flaxtwine", "rope", "fat", "fat-rendered", "waterportion" })
            items.Add(new() { Code = new("game:" + code), ItemId = items.Count + 1000, Durability = 100 });
        foreach (var code in new[] { "ash", "soap", "soap-powder", "lye" })
            items.Add(new() { Code = new("vsslssmod:" + code), ItemId = items.Count + 1000 });
        foreach (var item in items.Where(i => i.Code.Path is "waterportion" or "lye"))
            item.Attributes = JsonObject.FromJson("{\"waterTightContainerProps\":{\"containable\":true,\"itemsPerLitre\":100}}");
        var fastSearch = new System.Collections.Generic.OrderedDictionary<IRecipeIngredientBase, List<IRecipeBase>>();
        var logger = Proxy<ILogger>((m, a) => m.Name is "Error" or "Warning"
            ? throw new Exception("Native recipe resolution: " + string.Join(" ", a ?? [])) : null);
        var world = Proxy<IWorldAccessor>((m, a) => m.Name switch {
            "get_Blocks" => blocks, "get_Items" => items, "get_Logger" => logger,
            "get_FastSearchRecipesByIngredient" => fastSearch,
            "GetBlock" => blocks.SingleOrDefault(b => b.Code.Equals((AssetLocation)a![0]!)),
            "GetItem" => items.SingleOrDefault(i => i.Code.Equals((AssetLocation)a![0]!)),
            _ => throw new Exception("Unexpected recipe world " + m.Name)
        });
        var events = Proxy<IEventAPI>((m, _) => m.Name == "TriggerMatchesRecipe" ? true : throw new Exception(m.Name));
        var api = Proxy<ICoreAPI>((m, _) => m.Name switch {
            "get_World" => world, "get_Event" => events, _ => throw new Exception("Unexpected recipe API " + m.Name)
        });
        foreach (var collectible in blocks.Cast<CollectibleObject>().Concat(items))
            typeof(CollectibleObject).GetField("api", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(collectible, api);
        var entity = new EntityPlayer { Api = api, World = world };
        var player = FixturePlayer.Make(entity, Proxy<IPlayerInventoryManager>((m, _) => throw new Exception(m.Name)));
        ItemStack Stack(string code, int count = 1)
        {
            var block = blocks.SingleOrDefault(b => b.Code.ToString() == code);
            return block is null ? new(items.Single(i => i.Code.ToString() == code), count) : new(block, count);
        }
        var loaded = new Dictionary<string, GridRecipe[]>();
        RecipeBase.CollectiblePreSearchResultsCache.Clear();
        try
        {
            void Craft(string file, string pattern, string output, int quantity, params (char Key, string Code, int Count)[] inputs)
            {
                if (!loaded.TryGetValue(file, out var recipes))
                {
                    var source = JsonObject.FromJson(File.ReadAllText(Path.Combine(root, "assets/vsslssmod/recipes/grid/" + file + ".json")))
                        .AsObject<GridRecipe[]>()!;
                    foreach (var recipe in source) recipe.OnParsed(world);
                    recipes = source.SelectMany(r => r.GenerateRecipesForAllIngredientCombinations(world)).Cast<GridRecipe>().ToArray();
                    foreach (var recipe in recipes) Require(recipe.Resolve(world, file), file + " did not resolve.");
                    loaded[file] = recipes;
                }
                var layout = pattern.Replace(",", "");
                var slots = Enumerable.Range(0, 9).Select(_ => (ItemSlot)new DummySlot()).ToArray();
                for (var i = 0; i < layout.Length; i++)
                    if (layout[i] != '_')
                    {
                        var input = inputs.Single(x => x.Key == layout[i]);
                        slots[i].Itemstack = Stack(input.Code, input.Count);
                    }
                var matches = recipes.Where(r => r.Matches(player, world, slots, 3)).ToArray();
                Require(matches.Length == 1, file + " must match exactly one native recipe, got " + matches.Length);
                var selected = matches[0];
                Require(selected.Output!.ResolvedItemStack!.Collectible.Code.ToString() == "vsslssmod:" + output
                    && selected.Output.ResolvedItemStack.StackSize == quantity, file + " has the wrong output.");
                var first = Array.FindIndex(slots, s => !s.Empty);
                var saved = slots[first].Itemstack!.Clone();
                slots[first].Itemstack!.StackSize--;
                if (slots[first].StackSize == 0) slots[first].Itemstack = null;
                Require(!recipes.Any(r => r.Matches(player, world, slots, 3)), file + " accepts insufficient materials.");
                slots[first].Itemstack = saved;
                var other = Array.FindLastIndex(slots, s => s.Empty);
                if (other < 0) other = Array.FindIndex(slots, s => s.Itemstack!.Collectible.Code != saved.Collectible.Code);
                (slots[first].Itemstack, slots[other].Itemstack) = (slots[other].Itemstack, slots[first].Itemstack);
                Require(!recipes.Any(r => r.Matches(player, world, slots, 3)), file + " accepts the wrong arrangement.");
                (slots[first].Itemstack, slots[other].Itemstack) = (slots[other].Itemstack, slots[first].Itemstack);
                slots[first].Itemstack = Stack("game:rope", saved.StackSize);
                Require(!recipes.Any(r => r.Matches(player, world, slots, 3)), file + " accepts unrelated materials.");
                slots[first].Itemstack = saved;
                var tools = selected.ResolvedIngredients!.Select((ingredient, index) => (ingredient, index))
                    .Where(x => x.ingredient?.IsTool == true).Select(x => x.index).ToHashSet();
                Require(selected.ConsumeInput(player, slots, 3), file + " failed native consumption.");
                for (var i = 0; i < slots.Length; i++)
                    Require(tools.Contains(i) ? slots[i].StackSize == 1
                        && slots[i].Itemstack!.Collectible.GetRemainingDurability(slots[i].Itemstack) == 99 : slots[i].Empty,
                        file + " consumed the wrong quantity or failed to retain/wear its tool.");
            }
            foreach (var (wood, metal, stone, loose, clay) in new[] {
                ("oak", "copper", "granite", "andesite", "blue"), ("pine", "iron", "andesite", "granite", "fire") })
            {
                foreach (var material in new[] { "rock", "cobblestone" })
                    Craft("chimney", "SLS,LCL,SLS", "chimney", 1, ('S', $"game:{material}-{stone}", 1),
                        ('L', "game:stone-" + loose, 1), ('C', "game:clay-" + clay, 1));
                Craft("bathtub", "___,PNP,PPP", "bathtub", 1, ('P', "game:plank-" + wood, 4), ('N', "game:metalnailsandstrips-" + metal, 2));
                if (wood == "oak")
                {
                    Craft("copperbathtub", "___,PCP,PPP", "copperbathtub", 1,
                        ('P', "game:metalplate-copper", 1), ('C', "game:metalnailsandstrips-copper", 2));
                }
                Craft("latrine", "BB", "latrine", 1, ('B', "game:plank-" + wood, 1));
                Craft("wellhead", "___,STS,CTC", "wellhead", 1, ('S', "game:stick", 1), ('T', "game:flaxtwine", 1), ('C', "game:cobblestone-" + stone, 1));
                Craft("wellwall", "_C_,C_C,_C_", "wellwall", 4, ('C', "game:cobblestone-" + stone, 1));
                Craft("filterpot", "KLA", "filterpot", 1, ('K', "game:knife-copper", 1), ('L', "game:log-" + wood + "-ud", 1), ('A', "game:axe-copper", 1));
            }
            Console.WriteLine("PASS grid recipes: native wildcard expansion, independent stone types, exact quantities, arrangement, outputs and tool wear");
            var soaps = JsonObject.FromJson(File.ReadAllText(Path.Combine(root, "assets/vsslssmod/recipes/barrel/soap.json"))).AsObject<BarrelRecipe[]>()!;
            Require(soaps.Length == 2, "Unexpected soap barrel recipes.");
            foreach (var (recipe, fat) in soaps.Zip(new[] { "game:fat", "game:fat-rendered" }))
            {
                recipe.OnParsed(world);
                Require(recipe.Resolve(world, "soap") && recipe.SealHours == 12, "Soap recipe failed to resolve or has wrong duration.");
                ItemSlot[] slots = [new DummySlot(Stack(fat)), new DummySlot(Stack("vsslssmod:lye", 100))];
                Require(recipe.Matches(slots, out var amount) && amount == 1 && !recipe.TryCraftNow(api, 11.99, slots)
                    && slots[0].StackSize == 1 && slots[1].StackSize == 100, "Soap matched incorrectly or crafted before twelve hours.");
                Require(recipe.TryCraftNow(api, 12, slots) && slots[0].Itemstack!.Collectible.Code.ToString() == "vsslssmod:soap"
                    && slots[0].StackSize == 1 && slots[1].StackSize == 50, "Soap did not consume exactly half a litre and one fat.");
                slots = [new DummySlot(Stack(fat)), new DummySlot(Stack("vsslssmod:lye", 49))];
                Require(!recipe.Matches(slots, out _), "Soap accepts less than half a litre.");
                slots = [new DummySlot(Stack(fat, 2)), new DummySlot(Stack("vsslssmod:lye", 50))];
                Require(!recipe.Matches(slots, out _), "Soap accepts too little lye for the fat batch.");
            }
            var lye = JsonObject.FromJson(File.ReadAllText(Path.Combine(root, "assets/vsslssmod/recipes/barrel/lye.json"))).AsObject<BarrelRecipe>()!;
            lye.OnParsed(world);
            Require(lye.Resolve(world, "lye") && lye.SealHours == 24, "Lye recipe failed to resolve or has wrong duration.");
            ItemSlot[] lyeSlots = [new DummySlot(Stack("vsslssmod:ash", 3)), new DummySlot(Stack("game:waterportion", 500))];
            Require(lye.Matches(lyeSlots, out var portions) && portions == 500 && !lye.TryCraftNow(api, 23.99, lyeSlots), "Lye ratio or sealing time is incorrect.");
            Require(lye.TryCraftNow(api, 24, lyeSlots) && lyeSlots[0].Empty && lyeSlots[1].StackSize == 500
                && lyeSlots[1].Itemstack!.Collectible.Code.ToString() == "vsslssmod:lye", "Lye did not consume three ash and five litres of water.");
            Console.WriteLine("PASS barrel recipes: native liquid portions, 12/24-hour sealing, exact outputs and excess lye preservation");
        }
        finally { RecipeBase.CollectiblePreSearchResultsCache.Clear(); }
    }

    private static void Require(bool condition, string message) { if (!condition) throw new Exception(message); }
}
