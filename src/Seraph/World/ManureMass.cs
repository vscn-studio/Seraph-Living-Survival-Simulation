using Vintagestory.API.Common;
using Seraph.Core.Framework;

namespace Seraph.World;

internal static class ManureMass
{
    internal const string Key = "vsslssmod:manureMassG";
    // Historical untagged items keep their original 100 g value.
    internal static double PerItem(ItemStack stack)
    {
        var mass = stack.Attributes.GetDouble(Key, 100);
        return double.IsFinite(mass) && mass > 0 && mass <= BowelSettlement.CapacityG ? mass : 100;
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
