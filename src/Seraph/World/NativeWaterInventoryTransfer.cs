using Vintagestory.API.Common;
using Vintagestory.GameContent;

namespace Seraph.World;

internal sealed partial class NativeWaterTransfer
{
    private static bool WaterStackMerge(ItemStackMergeOperation op)
    {
        var sink = op.SinkSlot.Itemstack;
        var source = op.SourceSlot.Itemstack;
        if (!WaterQuality.IsWater(sink) || !WaterQuality.IsWater(source)) return true;
        op.MovedQuantity = op.MovableQuantity = 0;
        if (ReferenceEquals(sink, source) || !op.SinkSlot.CanTakeFrom(op.SourceSlot, op.CurrentPriority)) return false;
        var units = BlockLiquidContainerBase.GetContainableProps(source)?.ItemsPerLitre ?? 0;
        if (units <= 0) return false;
        var room = Math.Min(op.SinkSlot.GetRemainingSlotSpace(source), sink!.Collectible.MaxStackSize - sink.StackSize);
        var count = Math.Min(Math.Min(room, source!.StackSize), op.RequestedQuantity);
        if (count <= 0) return false;
        try
        {
            // Prepare quality before changing either slot. Native merging applies a
            // second temperature gate and can overwrite quality in SetTemperature.
            var result = Mixture(op.World.Api, sink, source, count / units,
                (sink.StackSize + room) / units, 15, out var moved);
            if (result is null) return false;
            op.SinkSlot.Itemstack = result;
            source.StackSize -= moved;
            if (source.StackSize == 0) op.SourceSlot.Itemstack = null;
            op.MovedQuantity = op.MovableQuantity = moved;
        }
        catch (InvalidDataException) { /* Preserve unsupported batches unchanged. */ }
        return false;
    }

    // Cooking slots perform their own Equals check before TryPutLiquid, and
    // their occupied-bowl branch passes the bowl's water as the incoming stack.
    // Handle just water here, on both sides, preserving native click directions.
    private static bool DrawFromCookingSlot(ItemSlotWatertight __instance, ItemSlot sourceSlot,
        ref ItemStackMoveOperation op)
    {
        if (sourceSlot.Itemstack?.Collectible is not BlockLiquidContainerBase container
            || !WaterQuality.IsWater(__instance.Itemstack)) return true;
        op.MovedQuantity = 0;
        if (__instance.Inventory.TakeLocked || sourceSlot.Inventory?.PutLocked == true
            || sourceSlot.StackSize <= 0) return false;
        var source = __instance.Itemstack;
        var units = BlockLiquidContainerBase.GetContainableProps(source)?.ItemsPerLitre ?? 0;
        if (units <= 0) return false;
        var vessels = sourceSlot.StackSize;
        var portion = op.ShiftDown ? container.CapacityLitres : container.TransferSizeLitres;
        // Each item in a stacked set of bowls must receive the same whole units.
        var countPerVessel = Math.Min(source!.StackSize / vessels, (int)Math.Floor(portion * units + 1e-5));
        if (countPerVessel <= 0) return false;
        try
        {
            var result = Mixture(op.World.Api, container.GetContent(sourceSlot.Itemstack), source,
                countPerVessel / units, container.CapacityLitres, 15, out var moved);
            if (result is null) return false;
            container.SetContent(sourceSlot.Itemstack, result);
            __instance.TakeOut(moved * vessels);
            op.MovedQuantity = moved * vessels;
            sourceSlot.MarkDirty();
            __instance.MarkDirty();
        }
        catch (InvalidDataException) { }
        return false;
    }

    private static bool PourIntoCookingSlot(ItemSlotWatertight __instance, ItemSlot sourceSlot,
        ref ItemStackMoveOperation op)
    {
        if (sourceSlot.Itemstack?.Collectible is not BlockLiquidContainerBase container
            || !WaterQuality.IsWater(container.GetContent(sourceSlot.Itemstack))) return true;
        op.MovedQuantity = 0;
        if (__instance.Inventory.PutLocked || sourceSlot.Inventory?.TakeLocked == true
            || sourceSlot.StackSize <= 0) return false;
        var source = container.GetContent(sourceSlot.Itemstack);
        var units = BlockLiquidContainerBase.GetContainableProps(source)?.ItemsPerLitre ?? 0;
        if (units <= 0) return false;
        var vessels = sourceSlot.StackSize;
        var portion = op.ShiftDown || op.ActingPlayer?.Entity.Controls.ShiftKey == true
            ? container.CapacityLitres : container.TransferSizeLitres;
        var room = Math.Max(0, (int)Math.Floor(__instance.capacityLitres * units + 1e-5) - __instance.StackSize);
        var countPerVessel = Math.Min(Math.Min(source!.StackSize, room / vessels),
            (int)Math.Floor(portion * units + 1e-5));
        if (countPerVessel <= 0) return false;
        try
        {
            var incoming = source.Clone();
            incoming.StackSize = checked(countPerVessel * vessels);
            var result = Mixture(op.World.Api, __instance.Itemstack, incoming,
                incoming.StackSize / units, __instance.capacityLitres, 15, out var moved);
            if (result is null || moved != incoming.StackSize) return false;
            container.TryTakeContent(sourceSlot.Itemstack, countPerVessel);
            __instance.Itemstack = result;
            op.MovedQuantity = moved;
            sourceSlot.MarkDirty();
            __instance.MarkDirty();
        }
        catch (InvalidDataException) { }
        return false;
    }
}
