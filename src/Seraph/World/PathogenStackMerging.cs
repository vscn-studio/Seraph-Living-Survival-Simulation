using HarmonyLib;
using Vintagestory.API.Common;
using Vintagestory.API.Config;

namespace Seraph.World;

/// <summary>Allows otherwise identical food stacks to merge while conserving per-item pathogen dose.</summary>
internal sealed class PathogenStackMerging : IDisposable
{
    private readonly Harmony harmony = new("vsslssmod.pathogenstackmerge");

    private sealed record MergeState(ItemStack Incoming, int SinkCount);

    internal PathogenStackMerging()
    {
        harmony.Patch(AccessTools.DeclaredMethod(typeof(CollectibleObject), nameof(CollectibleObject.GetMergableQuantity),
                [typeof(ItemStack), typeof(ItemStack), typeof(EnumMergePriority)]),
            postfix: new HarmonyMethod(typeof(PathogenStackMerging), nameof(MergableQuantity)));
        harmony.Patch(AccessTools.DeclaredMethod(typeof(CollectibleObject), nameof(CollectibleObject.TryMergeStacks),
                [typeof(ItemStackMergeOperation)]),
            prefix: new HarmonyMethod(typeof(PathogenStackMerging), nameof(MergeStart)),
            postfix: new HarmonyMethod(typeof(PathogenStackMerging), nameof(MergeDone)));
    }

    private static void MergableQuantity(ItemStack sinkStack, ItemStack sourceStack,
        EnumMergePriority priority, ref int __result)
    {
        if (sinkStack?.Collectible is null || sourceStack?.Collectible is null
            || !HasPathogenData(sinkStack) && !HasPathogenData(sourceStack)) return;

        var sink = sinkStack.Clone();
        var source = sourceStack.Clone();
        sink.Attributes.RemoveAttribute(PathogenMetadata.Key);
        source.Attributes.RemoveAttribute(PathogenMetadata.Key);
        if (!sink.Collectible.Equals(sink, source, GlobalConstants.IgnoredStackAttributes)) return;

        __result = Math.Max(__result, Math.Max(0,
            Math.Min(sinkStack.Collectible.MaxStackSize - sinkStack.StackSize, sourceStack.StackSize)));
    }

    private static bool HasPathogenData(ItemStack stack)
        => stack.Attributes.GetString(PathogenMetadata.Key) is not null;

    private static void MergeStart(ItemStackMergeOperation op, out MergeState? __state)
    {
        __state = null;
        if (op?.SinkSlot.Itemstack is not { } sink || op.SourceSlot.Itemstack is not { } source
            || !HasPathogenData(sink) && !HasPathogenData(source)) return;
        __state = new(source.Clone(), sink.StackSize);
    }

    private static void MergeDone(ItemStackMergeOperation op, MergeState? __state)
    {
        if (__state is null || op.MovedQuantity <= 0 || op.SinkSlot.Itemstack is not { } sink) return;
        var moved = op.MovedQuantity;
        var pathogens = Core.Framework.PathogenLoads.Mix(
            PathogenMetadata.Read(sink), __state.SinkCount,
            PathogenMetadata.Read(__state.Incoming), moved);
        PathogenMetadata.Write(sink, pathogens);
    }

    public void Dispose() => harmony.UnpatchAll("vsslssmod.pathogenstackmerge");
}
