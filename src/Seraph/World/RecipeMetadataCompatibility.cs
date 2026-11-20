using System.Reflection;
using System.Reflection.Emit;
using HarmonyLib;
using Seraph.Server;
using Vintagestory.API.Common;
using Vintagestory.GameContent;

namespace Seraph.World;

/// <summary>Ignore physiological annotations only at recipe/display comparisons, never at inventory equality.</summary>
internal sealed class RecipeMetadataCompatibility : IDisposable
{
    private const string PatchId = "vsslssmod.recipe-metadata";
    private static readonly string[] AnnotationKeys = [WaterMetadata.Key, WaterMetadata.TemperatureUpdatedAtKey,
        PathogenMetadata.Key, CraftedFoodNutrition.Key, "vsslssmodSaltGrams"];
    private static readonly MethodInfo StackEquals = AccessTools.Method(typeof(ItemStack), nameof(ItemStack.Equals),
        [typeof(IWorldAccessor), typeof(ItemStack), typeof(string[])]);
    private static int owners;
    private static Harmony? harmony;
    private static ILogger? diagnosticLogger;
    private const int MaxDiagnosticWarnings = 32;
    private static readonly HashSet<(string Kind, EnumItemClass Class, int Id)> reported = [];
    private static volatile bool diagnosticLimitReached;

    internal RecipeMetadataCompatibility() : this(null) { }

    internal RecipeMetadataCompatibility(ILogger? logger)
    {
        diagnosticLogger ??= logger;
        if (owners++ > 0) return;
        harmony = new(PatchId);
        harmony.Patch(AccessTools.Method(typeof(CookingRecipeIngredient), nameof(CookingRecipeIngredient.GetMatchingStack)),
            transpiler: new HarmonyMethod(typeof(RecipeMetadataCompatibility), nameof(CookingComparisons)));
        harmony.Patch(AccessTools.Method(typeof(JsonItemStack), nameof(JsonItemStack.Matches)),
            transpiler: new HarmonyMethod(typeof(RecipeMetadataCompatibility), nameof(JsonComparisons)));
        // Includes the compiler-generated predicates used by the handbook. Only
        // replace equality calls, retaining the real stack for descriptions, icons,
        // mod hooks and nutrition. No changes to global ignored attributes.
        foreach (var type in HandbookTypes(typeof(CollectibleBehaviorHandbookTextAndExtraInfo)))
            foreach (var method in type.GetMethods(BindingFlags.Public | BindingFlags.NonPublic
                | BindingFlags.Static | BindingFlags.Instance | BindingFlags.DeclaredOnly))
                if (method.GetMethodBody() is not null
                    && PatchProcessor.GetOriginalInstructions(method).Any(i => i.Calls(StackEquals)))
                    harmony.Patch(method, transpiler: new HarmonyMethod(typeof(RecipeMetadataCompatibility), nameof(HandbookComparisons)));
    }

    private static IEnumerable<Type> HandbookTypes(Type type)
    {
        yield return type;
        foreach (var nested in type.GetNestedTypes(BindingFlags.Public | BindingFlags.NonPublic))
            foreach (var child in HandbookTypes(nested)) yield return child;
    }

    // An explicit attribute requirement remains binding. Unsupported/old payloads
    // are not parsed or rewritten by recipe matching.
    internal static ItemStack Comparable(ItemStack input, ItemStack? required)
    {
        ItemStack? copy = null;
        foreach (var key in AnnotationKeys)
            if (input.Attributes.HasAttribute(key) && required?.Attributes.HasAttribute(key) != true)
            {
                copy ??= input.Clone();
                copy.Attributes.RemoveAttribute(key);
            }
        return copy ?? input;
    }

    private static bool CookingEquals(ItemStack input, IWorldAccessor world, ItemStack? required, string[] ignored)
    {
        var result = Comparable(input, required).Equals(world, required, ignored);
        if (!result) Report("cooking", input, required, ignored);
        return result;
    }

    private static bool IngredientEquals(ItemStack required, IWorldAccessor world, ItemStack? input, string[] ignored)
    {
        var candidate = input is null ? null : Comparable(input, required);
        var result = required.Equals(world, candidate, ignored);
        if (!result) Report("json", input, required, ignored);
        return result;
    }

    private static bool HandbookEquals(ItemStack left, IWorldAccessor world, ItemStack? right, string[] ignored)
    {
        var result = Comparable(left, null).Equals(world, right is null ? null : Comparable(right, null), ignored);
        if (!result) Report("handbook", left, right, ignored);
        return result;
    }

    private static void Report(string kind, ItemStack? input, ItemStack? required, string[]? ignored)
    {
        var logger = diagnosticLogger;
        if (logger is null || diagnosticLimitReached || input is null || required is null
            || input.Class != required.Class || input.Id != required.Id) return;
        var sample = (kind, input.Class, input.Id);
        string inputKeys, requiredKeys;
        lock (reported)
        {
            if (diagnosticLimitReached || reported.Contains(sample)) return;
            inputKeys = string.Join(", ", UnknownKeys(input, ignored));
            requiredKeys = string.Join(", ", UnknownKeys(required, ignored));
            if (inputKeys.Length == 0 && requiredKeys.Length == 0) return;
            reported.Add(sample);
            diagnosticLimitReached = reported.Count >= MaxDiagnosticWarnings;
        }
        // Ingredient comparison callbacks do not expose their parent recipe. Keep that
        // limitation explicit instead of reporting the required item code as a recipe.
        logger.Warning("[vsslssmod] Recipe metadata mismatch: recipeType={0}, recipeCode=<unavailable>, input={1}, required={2}, input unknown keys=[{3}], required unknown keys=[{4}]; matching remains strict.",
            kind, input.Collectible.Code, required.Collectible.Code, inputKeys, requiredKeys);
    }

    private static IEnumerable<string> UnknownKeys(ItemStack stack, string[]? ignored)
    {
        foreach (var pair in stack.Attributes)
        {
            if (Array.IndexOf(AnnotationKeys, pair.Key) >= 0
                || (ignored is not null && Array.IndexOf(ignored, pair.Key) >= 0)) continue;
            yield return $"{pair.Key}:{pair.Value.GetType().Name}";
        }
    }

    private static IEnumerable<CodeInstruction> CookingComparisons(IEnumerable<CodeInstruction> instructions)
        => Replace(instructions, nameof(CookingEquals));
    private static IEnumerable<CodeInstruction> JsonComparisons(IEnumerable<CodeInstruction> instructions)
        => Replace(instructions, nameof(IngredientEquals));
    private static IEnumerable<CodeInstruction> HandbookComparisons(IEnumerable<CodeInstruction> instructions)
        => Replace(instructions, nameof(HandbookEquals));

    private static IEnumerable<CodeInstruction> Replace(IEnumerable<CodeInstruction> instructions, string replacement)
    {
        var target = AccessTools.Method(typeof(RecipeMetadataCompatibility), replacement);
        foreach (var instruction in instructions)
        {
            if (instruction.Calls(StackEquals))
            {
                instruction.opcode = OpCodes.Call;
                instruction.operand = target;
            }
            yield return instruction;
        }
    }

    public void Dispose()
    {
        if (--owners != 0) return;
        harmony?.UnpatchAll(PatchId);
        harmony = null;
        diagnosticLogger = null;
        lock (reported)
        {
            reported.Clear();
            diagnosticLimitReached = false;
        }
    }
}
