using Vintagestory.API.Common;
using Vintagestory.API.Config;
using Vintagestory.GameContent;

namespace Seraph.World;

internal sealed partial class NativeWaterTransfer
{
    // Native vessels still own permissions, source removal and stacked-container splitting.
    // This only supplies the accepted mixture and transferred liquid item count.
    internal static ItemStack? SlurryMixture(IWorldAccessor world, ItemStack? current, ItemStack source,
        float desiredL, float capacityL, out int moved)
    {
        moved = 0;
        var props = BlockLiquidContainerBase.GetContainableProps(source);
        if (!LatrineWasteStorage.IsSlurry(source) || props is not { Containable: true }
            || !float.IsFinite(props.ItemsPerLitre) || props.ItemsPerLitre <= 0
            || !float.IsFinite(desiredL) || desiredL <= 0 || !float.IsFinite(capacityL)
            || capacityL <= 0 || capacityL > 10000 || source.StackSize <= 0) return null;
        if (current is not null)
        {
            if (!LatrineWasteStorage.IsSlurry(current) || current.StackSize <= 0
                || !ReferenceEquals(current.Collectible, source.Collectible)) return null;
            var left = current.Clone(); var right = source.Clone();
            foreach (var key in new[] { ManureMass.Key, PathogenMetadata.Key })
            { left.Attributes.RemoveAttribute(key); right.Attributes.RemoveAttribute(key); }
            if (!left.Equals(world, right, GlobalConstants.IgnoredStackAttributes)) return null;
        }
        var oldCount = current?.StackSize ?? 0;
        var count = Math.Min(source.StackSize, Math.Min(Math.Floor(Math.Min(desiredL, capacityL) * props.ItemsPerLitre + 1e-5),
            Math.Floor(capacityL * props.ItemsPerLitre + 1e-5) - oldCount));
        if (count <= 0 || count > int.MaxValue - oldCount) return null;
        var result = (current ?? source).Clone();
        moved = (int)count;
        ManureMass.Mix(result, oldCount, moved, ManureMass.PerItem(source) * moved, PathogenMetadata.Read(source));
        result.StackSize = oldCount + moved;
        return result;
    }
}
