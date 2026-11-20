using System.Text;
using Vintagestory.API.Common;
using Vintagestory.API.Config;

namespace Seraph.World;

/// <summary>Only manure's mass and pathogen concentrations are mixed; unrelated attributes still block merging.</summary>
public sealed class ItemManure : Item
{
    public override int GetMergableQuantity(ItemStack sinkStack, ItemStack sourceStack, EnumMergePriority priority)
    {
        if (!ReferenceEquals(sinkStack.Collectible, this) || !ReferenceEquals(sourceStack.Collectible, this)) return 0;
        var left = sinkStack.Clone(); var right = sourceStack.Clone();
        foreach (var key in new[] { ManureMass.Key, PathogenMetadata.Key })
        { left.Attributes.RemoveAttribute(key); right.Attributes.RemoveAttribute(key); }
        return Equals(left, right, GlobalConstants.IgnoredStackAttributes)
            ? Math.Max(0, Math.Min(MaxStackSize - sinkStack.StackSize, sourceStack.StackSize)) : 0;
    }

    public override void TryMergeStacks(ItemStackMergeOperation op)
    {
        var incoming = op.SourceSlot.Itemstack?.Clone();
        var oldCount = op.SinkSlot.StackSize;
        base.TryMergeStacks(op);
        if (op.MovedQuantity <= 0 || incoming is null || op.SinkSlot.Itemstack is null) return;
        ManureMass.Mix(op.SinkSlot.Itemstack, oldCount, op.MovedQuantity,
            ManureMass.PerItem(incoming) * op.MovedQuantity, PathogenMetadata.Read(incoming));
    }

    public override void GetHeldItemInfo(ItemSlot inSlot, StringBuilder dsc, IWorldAccessor world, bool withDebugInfo)
    {
        base.GetHeldItemInfo(inSlot, dsc, world, withDebugInfo);
        if (inSlot.Itemstack is { } stack)
            dsc.AppendLine(LocalizedText.Get("vsslssmod:manure-mass", ManureMass.PerItem(stack).ToString("0.##"),
                ManureMass.Total(stack).ToString("0.##")));
    }
}
