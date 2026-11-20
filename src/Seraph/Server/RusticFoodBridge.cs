using HarmonyLib;
using Vintagestory.API.Common;
using Vintagestory.API.Datastructures;
using Vintagestory.GameContent;

namespace Seraph.Server;

// Rustic Cookery 1.2.0 stores category satiety in bowls, losing the original ingredient quantities.
// Capture its actual serving transaction before that information is discarded. No satiety bonus
// is converted into food mass. The client and server later resolve the same retained stacks.
internal static class RusticFoodBridge
{
    [ThreadStatic] private static ItemStack? servingFlour;
    internal const string Key = "vsslssmod:meal-ingredients-release-v1";
    internal static void Install(Harmony harmony)
    {
        var type = AccessTools.TypeByName("RusticCookery.RoastNutrition");
        if (type is null) return;
        foreach (var (name, count, callback) in new[] {
            ("SetBowlShare", 5, nameof(Main)), ("SetBowlLiquid", 3, nameof(Liquid)), ("SetBowlFat", 4, nameof(Fat)),
            ("SetBowlRoux", 3, nameof(Roux)) })
        {
            var method = type.GetMethods().SingleOrDefault(m => m.Name == name && m.GetParameters().Length == count)
                ?? throw new NotSupportedException("Unsupported Rustic Cookery serving contract: " + name);
            harmony.Patch(method, postfix: new HarmonyMethod(typeof(RusticFoodBridge), callback));
        }
        var serving = AccessTools.Method(AccessTools.TypeByName("RusticCookery.BlockEntityBraise"), "ServePortion")
            ?? throw new NotSupportedException("Unsupported Rustic Cookery braise serving contract.");
        harmony.Patch(serving, prefix: new HarmonyMethod(typeof(RusticFoodBridge), nameof(BeginServing)),
            finalizer: new HarmonyMethod(typeof(RusticFoodBridge), nameof(EndServing)));
    }
    private static void BeginServing(BlockEntityContainer __instance, out ItemStack? __state)
    {
        __state = servingFlour;
        // Rustic 1.2.0 retains the actual flour in the dish but drops it from the
        // bowl. Scope its identity to this transaction, including exception exits.
        servingFlour = __instance.Inventory[0].Itemstack?.Attributes.GetItemstack("rouxStack")?.Clone();
    }
    private static Exception? EndServing(Exception? __exception, ItemStack? __state)
    {
        servingFlour = __state;
        return __exception;
    }
    private static void Roux(ItemStack bowl, float satiety, int portions)
    {
        if (satiety <= 0) return;
        if (portions <= 0) throw new InvalidDataException("Invalid Rustic Cookery roux serving count.");
        if (servingFlour is null) Root(bowl).SetInt("RouxPortions", portions);
        else Set(bowl, "Roux", [(servingFlour, 1)], portions);
    }
    private static ITreeAttribute Root(ItemStack bowl)
    {
        var root = bowl.Attributes.GetTreeAttribute(Key);
        if (root is not null && (root.GetString("Format") != "seraph.release.meal-ingredients" || root.GetInt("Version") != 1))
            throw new InvalidDataException("Unsupported retained meal ingredient format.");
        if (root is null)
        {
            root = new TreeAttribute(); root.SetString("Format", "seraph.release.meal-ingredients"); root.SetInt("Version", 1);
            root.SetDouble("ServingUnits", 1);
            bowl.Attributes[Key] = root;
        }
        return root;
    }
    private static void Set(ItemStack bowl, string key, IEnumerable<(ItemStack stack, float count)> inputs, int portions)
    {
        if (portions <= 0) throw new InvalidDataException("Invalid Rustic Cookery serving count.");
        var section = new TreeAttribute(); var index = 0;
        foreach (var (stack, count) in inputs)
        {
            if (stack is null || count == 0) continue;
            if (index >= 64 || !float.IsFinite(count) || count < 0) throw new InvalidDataException("Invalid serving ingredients.");
            var entry = new TreeAttribute(); entry.SetItemstack("Stack", stack.Clone()); entry.SetDouble("Quantity", count / portions);
            section[(index++).ToString()] = entry;
        }
        section.SetInt("Count", index); Root(bowl)[key] = section;
    }
    private static void Main(ItemStack bowl, IEnumerable<(ItemStack stack, float count)> dish, int portions)
        => Set(bowl, "Main", dish, portions);
    private static void Liquid(ItemStack bowl, ItemStack? roastLiquid, int portions)
        => Set(bowl, "Liquid", roastLiquid is null ? [] : [(roastLiquid, roastLiquid.StackSize)], portions);
    private static void Fat(IWorldAccessor world, ItemStack bowl, ItemStack? fat, int portions)
    {
        Set(bowl, "Fat", fat is null ? [] : [(fat, fat.StackSize)], portions);
        // Rustic uses the roast's satiety multiplier as the bowl's serving count
        // (e.g. 0.8 for charred). Preserve that initial denominator so eating the
        // whole bowl settles its actual ingredient share exactly once.
        var units = bowl.Block is BlockMeal meal ? meal.GetQuantityServings(world, bowl) : 1;
        if (!float.IsFinite(units) || units <= 0) throw new InvalidDataException("Invalid Rustic bowl serving units.");
        Root(bowl).SetDouble("ServingUnits", units);
    }

    internal static double ServingUnits(ItemStack meal)
    {
        var root = meal.Attributes.GetTreeAttribute(Key);
        if (root is null) return 1;
        var units = root.GetDouble("ServingUnits", double.NaN);
        if (!double.IsFinite(units) || units <= 0 || units > 1000)
            throw new InvalidDataException("Invalid retained meal serving units.");
        return units;
    }

    internal static bool AddRetained(FoodCatalog catalog, IWorldAccessor world, ItemStack meal, FoodDose result)
    {
        var root = meal.Attributes.GetTreeAttribute(Key);
        if (root is null) return false;
        if (root.GetString("Format") != "seraph.release.meal-ingredients" || root.GetInt("Version") != 1)
            throw new InvalidDataException("Unsupported retained meal ingredient format.");
        foreach (var key in new[] { "Main", "Liquid", "Fat", "Roux" })
        {
            var section = root.GetTreeAttribute(key);
            if (section is null) continue;
            var count = section.GetInt("Count", -1);
            if (count is < 0 or > 64) throw new InvalidDataException("Invalid retained ingredient count.");
            for (var i = 0; i < count; i++)
            {
                var entry = section.GetTreeAttribute(i.ToString()) ?? throw new InvalidDataException("Missing retained ingredient.");
                var stack = entry.GetItemstack("Stack"); var quantity = entry.GetDouble("Quantity");
                if (stack is null || !double.IsFinite(quantity) || quantity <= 0 || quantity > 10000
                    || !stack.ResolveBlockOrItem(world)) throw new InvalidDataException("Invalid retained ingredient.");
                result.Add(catalog.Composition(stack, quantity, world: world, inMeal: true, cooked: true));
            }
        }
        return root.HasAttribute("Main");
    }
}
