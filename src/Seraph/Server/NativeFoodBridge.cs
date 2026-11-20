using HarmonyLib;
using Vintagestory.API.Common;
using Vintagestory.API.Server;
using Vintagestory.GameContent;

namespace Seraph.Server;

/// <summary>Observe confirmed inventory removal, native remaining-serving differences and actual liquid units.</summary>
internal sealed class NativeFoodBridge : IDisposable
{
    private static ICoreServerAPI? serverApi;
    private static readonly HashSet<string> reportedRecipes = new(StringComparer.Ordinal);
    internal NativeFoodBridge(ICoreServerAPI api) : this() { serverApi = api; reportedRecipes.Clear(); }
    private readonly Harmony harmony = new("vsslssmod.food");
    [ThreadStatic] private static DrinkContext? drink;
    private sealed record SolidContext(ItemStack Stack, int Count, FoodDose Dose);
    private sealed record DrinkContext(EntityAgent Entity, FoodDose PerLitre, float UnitsPerLitre);
    private sealed record MealContext(FoodDose Dose, float Remaining);
    internal NativeFoodBridge()
    {
        RusticFoodBridge.Install(harmony);
        // Butchering replaces hook contents directly, bypassing native preservation.
        var hook = AccessTools.TypeByName("Butchering.src.common.blockentity.BlockEntityMeatHook");
        var smoke = hook is null ? null : AccessTools.DeclaredMethod(hook, "smokeContents", [typeof(float)]);
        if (smoke is not null)
            harmony.Patch(smoke, prefix: new(typeof(NativeFoodBridge), nameof(SmokingStart)),
                postfix: new(typeof(NativeFoodBridge), nameof(SmokingStop)));
        harmony.Patch(AccessTools.DeclaredMethod(typeof(CollectibleObject), "OnTransitionNow"),
            postfix: new(typeof(NativeFoodBridge), nameof(PreservedComposition)));
        harmony.Patch(AccessTools.Method(typeof(CollectibleObject), "CarryOverFreshness",
            [typeof(ICoreAPI), typeof(ItemSlot[]), typeof(ItemStack[]), typeof(TransitionableProperties)]),
            postfix: new(typeof(NativeFoodBridge), nameof(ProcessedComposition)));
        harmony.Patch(AccessTools.DeclaredMethod(typeof(CollectibleObject), "tryEatStop"),
            prefix: new HarmonyMethod(typeof(NativeFoodBridge), nameof(SolidStart)),
            postfix: new HarmonyMethod(typeof(NativeFoodBridge), nameof(SolidStop)));
        // Native pickup/give is the one server-side edge shared by ground pickup,
        // inventory transfers and commands. Credit a spoiled item once here so
        // carrying/contact does not depend on a client-only interaction callback.
        harmony.Patch(AccessTools.Method(typeof(EntityPlayer), nameof(EntityPlayer.TryGiveItemStack), [typeof(ItemStack)]),
            postfix: new HarmonyMethod(typeof(NativeFoodBridge), nameof(ItemReceived)));
        harmony.Patch(AccessTools.DeclaredMethod(typeof(BlockMeal), "Consume"),
            prefix: new HarmonyMethod(typeof(NativeFoodBridge), nameof(MealStart)),
            postfix: new HarmonyMethod(typeof(NativeFoodBridge), nameof(MealStop)));
        harmony.Patch(AccessTools.DeclaredMethod(typeof(BlockLiquidContainerBase), "tryEatStop"),
            prefix: new HarmonyMethod(typeof(NativeFoodBridge), nameof(DrinkStart)),
            finalizer: new HarmonyMethod(typeof(NativeFoodBridge), nameof(DrinkStop)));
        harmony.Patch(AccessTools.Method(typeof(BlockLiquidContainerBase), "TryTakeLiquid", [typeof(ItemStack), typeof(float)]),
            postfix: new HarmonyMethod(typeof(NativeFoodBridge), nameof(LiquidRemoved)));
        harmony.Patch(AccessTools.DeclaredMethod(typeof(CollectibleObject), "OnCreatedByCrafting"),
            postfix: new(typeof(NativeFoodBridge), nameof(Crafted)));
        harmony.Patch(AccessTools.DeclaredMethod(typeof(BlockCookingContainer), "DoSmelt"),
            prefix: new(typeof(NativeFoodBridge), nameof(CookingStart)),
            postfix: new(typeof(NativeFoodBridge), nameof(Cooked)));
    }
    private static ServerCoordinator? Owner(EntityAgent entity) => entity.World.Side == EnumAppSide.Server
        && entity.World.Api.ModLoader.GetModSystem<SeraphModSystem>().Server is { } server && server.OwnsPhysiology(entity) ? server : null;
    private static bool SolidStart(float secondsUsed, ItemSlot slot, EntityAgent byEntity, out SolidContext? __state)
    {
        __state = null;
        if (secondsUsed < .95f || slot.Itemstack is not { } stack || Owner(byEntity) is not { } owner
            || stack.Collectible.GetNutritionProperties(byEntity.World, stack, byEntity) is null) return true;
        var dose = owner.FoodComposition(stack, 1);
        if (owner.FoodCapacity(byEntity, dose) < 1 - 1e-6) return false;
        __state = new(stack.Clone(), stack.StackSize, dose); return true;
    }
    private static void SolidStop(ItemSlot slot, EntityAgent byEntity, SolidContext? __state)
    {
        if (__state is null || Owner(byEntity) is not { } owner) return;
        var same = slot.Itemstack?.Collectible == __state.Stack.Collectible;
        var removed = __state.Count - (same ? slot.StackSize : 0);
        // Base tryEatStop consumes exactly one; EatenStack replacement can replace the final item.
        if (removed == 1) owner.CreditFood(byEntity, __state.Dose);
    }
    private static void ItemReceived(EntityPlayer __instance, ItemStack itemstack, bool __result)
    {
        if (!__result || itemstack is null || Owner(__instance) is not { } owner) return;
        owner.CreditSpoilageContact(__instance, itemstack);
    }
    private static void MealStart(BlockMeal __instance, IWorldAccessor world, IPlayer eatingPlayer, ItemSlot inSlot,
        ItemStack[] contentStacks, float remainingServings, bool mulwithStackSize, out MealContext? __state)
    {
        __state = null;
        if (Owner(eatingPlayer.Entity) is not { } owner || inSlot.Itemstack is not { } meal || !float.IsFinite(remainingServings) || remainingServings <= 0) return;
        var dose = owner.MealComposition(world, meal, contentStacks ?? [], mulwithStackSize);
        // Native Consume calculates its returned fraction from the fullness compatibility tree.
        // Bound this fraction using our capacity, without altering remainingServings/recipe contents.
        var multipliers = __instance.GetNutritionHealthMul(null, inSlot, eatingPlayer.Entity);
        // Native satiety multipliers determine the eaten fraction only; they never create ingredient mass.
        var props = BlockMeal.GetContentNutritionProperties(world, inSlot, contentStacks, eatingPlayer.Entity, mulwithStackSize, multipliers[0], multipliers[1]);
        var sat = props.Where(p => p is not null).Sum(p => p.Satiety);
        var hunger = eatingPlayer.Entity.GetBehavior<EntityBehaviorHunger>();
        if (hunger is not null && sat > 0)
            hunger.Saturation = hunger.MaxSaturation - (float)(Math.Max(1, sat) * owner.FoodCapacity(eatingPlayer.Entity, dose));
        __state = new(dose, remainingServings);
    }
    private static void MealStop(IPlayer eatingPlayer, float __result, MealContext? __state)
    {
        if (__state is null || Owner(eatingPlayer.Entity) is not { } owner) return;
        var fraction = __state.Remaining - __result;
        if (float.IsFinite(fraction) && fraction > 0 && fraction <= Math.Min(1, __state.Remaining) + 1e-5)
            owner.CreditFood(eatingPlayer.Entity, __state.Dose, fraction);
        owner.ProjectPhysiology(eatingPlayer.Entity);
    }
    private static bool DrinkStart(BlockLiquidContainerBase __instance, float secondsUsed, ItemSlot slot, EntityAgent byEntity, out DrinkContext? __state)
    {
        __state = drink;
        if (secondsUsed < .95 || slot.Itemstack is null || Owner(byEntity) is not { } owner) return true;
        var liquid = __instance.GetContent(slot.Itemstack);
        if (liquid is null || World.WaterQuality.IsWater(liquid)) return true;
        var props = BlockLiquidContainerBase.GetContainableProps(liquid);
        if (props is null || props.ItemsPerLitre <= 0) return true;
        var dose = owner.FoodComposition(liquid, 1, true);
        var portion = Math.Min(liquid.StackSize / (double)props.ItemsPerLitre, Math.Max(1 / props.ItemsPerLitre, __instance.DrinkPortionSize));
        var actual = new FoodDose(); actual.Add(dose, portion);
        if (owner.FoodCapacity(byEntity, actual) < 1 - 1e-6) return false;
        drink = new(byEntity, dose, props.ItemsPerLitre); return true;
    }
    private static void LiquidRemoved(ItemStack? __result)
    {
        if (drink is { } context && __result is { StackSize: > 0 } && Owner(context.Entity) is { } owner)
            owner.CreditFood(context.Entity, context.PerLitre, __result.StackSize / (double)context.UnitsPerLitre);
    }
    private static Exception? DrinkStop(Exception? __exception, DrinkContext? __state) { drink = __state; return __exception; }
    private static void Crafted(ItemSlot[] allInputSlots, ItemSlot outputSlot, IRecipeBase byRecipe)
    {
        if (outputSlot.Itemstack is not { StackSize: > 0 } output) return;
        try
        {
            if (serverApi?.ModLoader.GetModSystem<SeraphModSystem>()?.Server is { } owner)
                CraftedFoodNutrition.Capture(owner.FoodProfiles, allInputSlots, output, byRecipe);
            TransferCraftedFood(allInputSlots, output, byRecipe);
        }
        catch (Exception ex)
        {
            // A metadata observer must never interrupt another mod's crafting transaction.
            var key = byRecipe?.GetType().FullName ?? "null";
            if (reportedRecipes.Count < 64 && reportedRecipes.Add(key))
                serverApi?.Logger.Warning("[vsslssmod] Crafting food metadata skipped for {0}: {1}", key, ex.Message);
        }
    }
    private static void TransferCraftedFood(ItemSlot[] allInputSlots, ItemStack output, IRecipeBase byRecipe)
    {
        // Toolsmith intentionally passes synthetic GridRecipes without ingredients.
        // Test edibility BEFORE touching the recipe (its property getter can throw).
        if (output.Collectible is not BlockMeal and not BlockPie
            && output.Collectible.NutritionProps is null
            && (serverApi is null || output.Collectible.GetNutritionProperties(serverApi.World, output, null) is null)) return;
        var agents = Core.Framework.PathogenLoads.Empty;
        var ingredients = byRecipe.RecipeIngredients.ToArray();
        if (ingredients.Length == 0 || ingredients.Any(i => i is null)) return;
        var remaining = ingredients.Select(i => i.ConsumeProperties.Consume ? i.Quantity : 0).ToArray();
        foreach (var slot in allInputSlots)
        {
            if (slot.Itemstack is not { } input) continue;
            var available = input.StackSize;
            for (var i = 0; i < remaining.Length && available > 0; i++)
            {
                var ingredient = ingredients[i];
                if (ingredient is null || !ingredient.ConsumeProperties.Consume || remaining[i] <= 0
                    || !ingredient.SatisfiesAsIngredient(input)) continue;
                var used = Math.Min(available, remaining[i]);
                agents = Core.Framework.PathogenLoads.Add(agents, Core.Framework.PathogenLoads.Scale(
                    World.PathogenMetadata.Read(input), used));
                available -= used; remaining[i] -= used;
            }
        }
        if (remaining.Any(n => n > 0)) return; // Incomplete mapping cannot prove the consumed quantities.
        World.PathogenMetadata.Write(output, Core.Framework.PathogenLoads.Scale(agents, 1d / output.StackSize));
    }
    private static void SmokingStart(BlockEntityDisplay __instance, out ItemStack?[]? __state)
    {
        __state = null;
        if (__instance.Api.Side != EnumAppSide.Server) return;
        __state = __instance.Inventory.Select(slot => slot.Itemstack is { } input
            && CraftedFoodNutrition.Read(input) is not null ? input : null).ToArray();
    }
    private static void SmokingStop(BlockEntityDisplay __instance, ItemStack?[]? __state)
    {
        if (__state is null || serverApi?.ModLoader.GetModSystem<SeraphModSystem>()?.Server is not { } owner) return;
        if (TransferSmokedComposition(owner.FoodProfiles, __instance.Inventory, __state)) __instance.MarkDirty(true);
    }
    internal static bool TransferSmokedComposition(FoodCatalog catalog, InventoryBase inventory, ItemStack?[] inputs)
    {
        var changed = false;
        for (var i = 0; i < Math.Min(inputs.Length, inventory.Count); i++)
        {
            if (inputs[i] is not { } input || inventory[i].Itemstack is not { } output
                || ReferenceEquals(input, output) || CraftedFoodNutrition.Read(output) is not null
                || input.ItemAttributes?["transformsWhenSmoked"].AsString() != output.Collectible.Code.ToString()) continue;
            CraftedFoodNutrition.Transfer(catalog, input, output);
            changed |= CraftedFoodNutrition.Read(output) is not null;
        }
        return changed;
    }
    private static void PreservedComposition(ItemSlot slot, ItemStack? __result)
    {
        if (slot.Itemstack is { } input && __result is not null
            && serverApi?.ModLoader.GetModSystem<SeraphModSystem>()?.Server is { } owner)
            CraftedFoodNutrition.Transfer(owner.FoodProfiles, input, __result);
    }
    private static void ProcessedComposition(ItemSlot[] inputSlots, ItemStack[] outStacks)
    {
        if (inputSlots.Length == 1 && outStacks.Length == 1 && inputSlots[0].Itemstack is { } input
            && outStacks[0] is { } output && serverApi?.ModLoader.GetModSystem<SeraphModSystem>()?.Server is { } owner)
            CraftedFoodNutrition.Transfer(owner.FoodProfiles, input, output);
    }
    private sealed record CookingState(ItemStack? Input, ItemStack? Output);
    private static void CookingStart(ItemSlot inputSlot, ItemSlot outputSlot, out CookingState __state)
        => __state = new(inputSlot.Itemstack, outputSlot.Itemstack);
    private static void Sterilize(IWorldAccessor world, ItemStack? output)
    {
        if (output is null) return;
        if (output.Collectible is BlockContainer container)
        {
            var contents = container.GetContents(world, output);
            foreach (var stack in contents ?? []) Sterilize(world, stack);
            container.SetContents(output, contents);
        }
        World.PathogenMetadata.Write(output, Core.Framework.PathogenLoads.Empty);
    }
    private static void Cooked(IWorldAccessor world, ISlotProvider cookingSlotsProvider,
        ItemSlot inputSlot, ItemSlot outputSlot, CookingState __state)
    {
        // Invalid recipes return without changing either slot. Never sterilize an existing meal then.
        if (!ReferenceEquals(outputSlot.Itemstack, __state.Output)) Sterilize(world, outputSlot.Itemstack);
        if (inputSlot.Itemstack is not null && !ReferenceEquals(inputSlot.Itemstack, __state.Input))
        {
            Sterilize(world, inputSlot.Itemstack);
            foreach (var slot in cookingSlotsProvider.Slots) Sterilize(world, slot.Itemstack);
        }
    }
    public void Dispose() { harmony.UnpatchAll("vsslssmod.food"); serverApi = null; reportedRecipes.Clear(); }
}
