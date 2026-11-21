using Vintagestory.API.Common;
using Seraph.Core.Framework;

namespace Seraph.World;

internal static class ManureMass
{
    internal const string Key = "vsslssmod:manureMassG";
    // Historical untagged items keep their original 100 g value.
    internal static double PerItem(ItemStack stack)
    {
        var fallback = LatrineWasteStorage.IsSlurry(stack)
            && Vintagestory.GameContent.BlockLiquidContainerBase.GetContainableProps(stack) is { ItemsPerLitre: > 0 } props
            ? 1000d / props.ItemsPerLitre : 100;
        var mass = stack.Attributes.GetDouble(Key, fallback);
        return double.IsFinite(mass) && mass > 0 && mass <= BowelSettlement.CapacityG ? mass : fallback;
    }
    internal static double Total(ItemStack stack) => PerItem(stack) * stack.StackSize;
    internal static void Mix(ItemStack stack, int oldCount, int addedCount, double massG,
        IReadOnlyDictionary<string, double>? pathogens)
    {
        var oldMass = PerItem(stack) * oldCount;
        if (!double.IsFinite(massG) || massG <= 0 || addedCount <= 0) throw new ArgumentOutOfRangeException(nameof(massG));
        if (pathogens is not null) PathogenMetadata.Write(stack,
            PathogenLoads.Mix(PathogenMetadata.Read(stack), oldMass, pathogens, massG));
        stack.Attributes.SetDouble(Key, (oldMass + massG) / (oldCount + addedCount));
    }
}
