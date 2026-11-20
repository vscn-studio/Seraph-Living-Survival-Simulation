using Seraph.Core.Framework;
using Vintagestory.API.Common;
using Vintagestory.API.MathTools;
using Vintagestory.GameContent;

namespace Seraph.World;

// Capture the actual barrel and contents so a delayed action cannot overwrite a replacement barrel.
internal sealed class LatrineWasteStorage(BlockPos origin, BlockPos pos, BlockEntityBarrel barrel, Item manure, ItemStack? original)
{
    internal int Quantity => original?.StackSize ?? 0;
    internal int Capacity => manure.MaxStackSize;

    internal static bool IsManure(ItemStack? stack) => stack?.Collectible.Code is { } code
        && code.Path == "manure" && (code.Domain is "vsslssmod" or "naturalfertilizer");

    internal static LatrineWasteStorage? Read(IWorldAccessor world, BlockPos latrinePos)
    {
        var pos = FindBarrel(world, latrinePos);
        if (pos is null) return null;
        if (world.BlockAccessor.GetBlock(pos) is not BlockBarrel { Code.Domain: "game" }
            || world.BlockAccessor.GetBlockEntity(pos) is not BlockEntityBarrel { Sealed: false } barrel
            || barrel.Inventory.Count != 2 || !barrel.Inventory[1].Empty) return null;
        var stack = barrel.Inventory[0].Itemstack;
        if (stack is not null && !IsManure(stack)) return null;
        var manure = stack?.Item ?? world.GetItem(new AssetLocation("naturalfertilizer", "manure"))
            ?? world.GetItem(new AssetLocation("vsslssmod", "manure"));
        if (manure is null || manure.MaxStackSize <= 0 || stack is { StackSize: <= 0 }
            || stack?.StackSize > manure.MaxStackSize) return null;
        return new(latrinePos.Copy(), pos, barrel, manure, stack?.Clone());
    }

    private static BlockPos? FindBarrel(IWorldAccessor world, BlockPos origin)
    {
        for (var depth = 1; depth <= 16 && origin.Y - depth >= 0; depth++)
        {
            var candidate = origin.AddCopy(0, -depth, 0);
            if (world.BlockAccessor.GetChunkAtBlockPos(candidate) is null) return null;
            var block = world.BlockAccessor.GetBlock(candidate);
            if (block is BlockBarrel { Code.Domain: "game" }) return candidate;
            if (block.Id != 0 || world.BlockAccessor.GetBlock(candidate, BlockLayersAccess.Fluid).Id != 0) return null;
        }
        return null;
    }

    internal bool Matches(IWorldAccessor world)
    {
        if (FindBarrel(world, origin)?.Equals(pos) != true
            || !ReferenceEquals(world.BlockAccessor.GetBlockEntity(pos), barrel) || barrel.Sealed
            || world.BlockAccessor.GetBlock(pos) is not BlockBarrel { Code.Domain: "game" }
            || !barrel.Inventory[1].Empty) return false;
        var current = barrel.Inventory[0].Itemstack;
        return original is null ? current is null : current is not null && current.StackSize == Quantity
            && ReferenceEquals(current.Collectible, original.Collectible) && current.Attributes.Equals(world, original.Attributes, []);
    }

    internal void Add(IWorldAccessor world, int amount, IReadOnlyDictionary<string, double>? pathogens = null, double? actualMassG = null)
    {
        if (amount <= 0 || amount > Capacity - Quantity || !Matches(world))
            throw new InvalidOperationException("Latrine barrel or contents changed during use.");
        try
        {
            var stack = original?.Clone() ?? new ItemStack(manure);
            ManureMass.Mix(stack, Quantity, amount, actualMassG ?? amount * 100, pathogens);
            stack.StackSize = Quantity + amount;
            barrel.Inventory[0].Itemstack = stack;
            barrel.Inventory[0].MarkDirty();
            barrel.MarkDirty(true);
            WaterQuality.Invalidate(world, pos);
        }
        catch
        {
            barrel.Inventory[0].Itemstack = original?.Clone();
            barrel.MarkDirty(true);
            throw;
        }
    }
}
