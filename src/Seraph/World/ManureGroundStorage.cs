using System.Reflection;
using Seraph.Core.Framework;
using Vintagestory.API.Common;
using Vintagestory.API.MathTools;
using Vintagestory.GameContent;

namespace Seraph.World;

// Native ground storage plus optional Natural Fertilizer storage; no external assets or DLLs are distributed.
internal sealed class ManureGroundStorage(Item manure, Block storage, MethodInfo configure, int capacity)
{
    internal int Capacity { get; } = capacity;
    internal int BlockId => storage.Id;

    internal static ManureGroundStorage? Resolve(IWorldAccessor world)
    {
        var item = world.GetItem(new AssetLocation("naturalfertilizer", "manure"));
        var block = world.GetBlock(new AssetLocation("naturalfertilizer", "manurestack"));
        if (item is null || block?.GetType().FullName != "naturalfertilizer.BlockManureStack") return null;
        var behavior = item.CollectibleBehaviors?.FirstOrDefault(b =>
            b.GetType().FullName == "naturalfertilizer.CollectibleBehaviorManureStack");
        var props = behavior?.GetType().GetProperty("ManureStackProps")?.GetValue(behavior);
        var size = props?.GetType().GetField("StackingCapacity")?.GetValue(props);
        var type = block.GetType().Assembly.GetType("naturalfertilizer.BlockEntityManureStack");
        var method = type?.GetMethod("DetermineStorageProperties", [typeof(ItemSlot)]);
        if (size is not int max || max <= 0 || item.MaxStackSize <= 0 || method is null
            || type is null || !typeof(BlockEntityContainer).IsAssignableFrom(type)) return null;
        return new(item, block, method, Math.Min(max, item.MaxStackSize));
    }

    internal static ManureGroundStorage? ResolveFallback(IWorldAccessor world)
    {
        var item = world.GetItem(new AssetLocation("vsslssmod", "manure"));
        var block = world.GetBlock(new AssetLocation("game", "groundstorage"));
        var props = item?.GetBehavior<CollectibleBehaviorGroundStorable>()?.StorageProps;
        if (item is null || block is not BlockGroundStorage || props?.Layout != EnumGroundStorageLayout.Messy12) return null;
        // Capacity is the verified VS 1.22.7 Messy12 layout, not an invented stack of enlarged models.
        return new(item, block, typeof(BlockEntityGroundStorage).GetMethod("DetermineStorageProperties", [typeof(ItemSlot)])!,
            Math.Min(12, item.MaxStackSize));
    }

    // Refuse foreign/mixed layouts instead of replacing their contents.
    internal static int InventoryQuantity(InventoryBase inventory, Item manure)
    {
        if (inventory.Count == 0) return -1;
        for (var i = 1; i < inventory.Count; i++) if (!inventory[i].Empty) return -1;
        var stack = inventory[0].Itemstack;
        return stack is null ? 0 : ReferenceEquals(stack.Collectible, manure) && stack.StackSize > 0 ? stack.StackSize : -1;
    }

    internal bool TryRead(IWorldAccessor world, BlockPos pos, out int quantity)
    {
        var block = world.BlockAccessor.GetBlock(pos, BlockLayersAccess.Solid);
        quantity = 0;
        if (block.Id == 0) return true;
        if (block.Id != storage.Id || world.BlockAccessor.GetBlockEntity(pos) is not BlockEntityContainer entity
            || !configure.DeclaringType!.IsInstanceOfType(entity)) return false;
        quantity = InventoryQuantity(entity.Inventory, manure);
        return quantity >= 0 && quantity <= Capacity;
    }

    internal void Add(IWorldAccessor world, BlockPos pos, int originalBlock, int expectedQuantity, int amount, IReadOnlyDictionary<string, double>? pathogens = null, double? actualMassG = null)
    {
        if (amount <= 0 || !TryRead(world, pos, out var count) || count != expectedQuantity
            || amount > Capacity - count || world.BlockAccessor.GetBlock(pos, BlockLayersAccess.Solid).Id != originalBlock)
            throw new InvalidOperationException("Manure storage changed during defecation.");
        var created = originalBlock == 0;
        BlockEntityContainer? entity = null;
        ItemStack?[]? previous = null;
        try
        {
            if (created) world.BlockAccessor.SetBlock(storage.Id, pos);
            entity = world.BlockAccessor.GetBlockEntity(pos) as BlockEntityContainer;
            if (entity is null || !configure.DeclaringType!.IsInstanceOfType(entity))
                throw new InvalidOperationException("Manure storage entity is unavailable.");
            if (!created) previous = entity.Inventory.Select(slot => slot.Itemstack?.Clone()).ToArray();
            var stack = entity.Inventory[0].Itemstack?.Clone() ?? new ItemStack(manure);
            ManureMass.Mix(stack, count, amount, actualMassG ?? amount * 100, pathogens);
            stack.StackSize = checked(count + amount);
            entity.Inventory[0].Itemstack = stack;
            configure.Invoke(entity, [null]);
            entity.MarkDirty(true);
        }
        catch
        {
            // Restore the original inventory in place; replacing the block would discard existing manure.
            if (created) world.BlockAccessor.SetBlock(0, pos);
            else if (entity is not null && previous is not null)
            {
                for (var i = 0; i < previous.Length; i++) entity.Inventory[i].Itemstack = previous[i];
                configure.Invoke(entity, [null]);
                entity.MarkDirty(true);
            }
            throw;
        }
    }

    internal static bool ContainsManure(BlockEntity? entity)
        => entity is BlockEntityContainer container && container.Inventory.Any(slot =>
            slot.Itemstack is { StackSize: > 0 } stack && stack.Collectible.Code?.Domain is "naturalfertilizer" or "vsslssmod"
            && stack.Collectible.Code.Path == "manure");
}
