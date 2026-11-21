using Seraph.Core.Framework;
using Vintagestory.API.Common;
using Vintagestory.API.MathTools;
using Vintagestory.GameContent;

namespace Seraph.World;

// Capture the actual barrel and contents so a delayed action cannot overwrite a replacement barrel.
internal sealed class LatrineWasteStorage(BlockPos origin, BlockPos pos, BlockEntityBarrel barrel, Item slurry, ItemStack? solid, ItemStack? liquid, int unitsPerLitre)
{
    internal BlockPos Position => pos;
    internal double Quantity => liquid is not null ? (double)liquid.StackSize / unitsPerLitre
        : solid is not null ? ManureMass.Total(solid) / 1000 : 0;
    internal int Capacity => barrel.CapacityLitres;
    // One excretion portion is 1 kg / 1 L. Never use liquid item counts as bowel portions.
    internal int AvailablePortions => Math.Max(0, (int)Math.Floor(Capacity - Quantity + 1e-9));

    internal static bool IsSlurry(ItemStack? stack) => stack?.Collectible.Code is { } code
        && code.Path == "manureslurryportion" && (code.Domain is "vsslssmod" or "naturalfertilizer");
    internal static bool IsManure(ItemStack? stack) => stack?.Collectible.Code is { } code
        && (code.Path is "manure" or "manureslurryportion") && (code.Domain is "vsslssmod" or "naturalfertilizer");

    internal static LatrineWasteStorage? Read(IWorldAccessor world, BlockPos latrinePos)
    {
        var pos = FindBarrel(world, latrinePos);
        if (pos is null) return null;
        if (world.BlockAccessor.GetBlock(pos) is not BlockBarrel { Code.Domain: "game" }
            || world.BlockAccessor.GetBlockEntity(pos) is not BlockEntityBarrel { Sealed: false } barrel
            || barrel.Inventory.Count != 2 || barrel.CapacityLitres is <= 0 or > 10000) return null;
        var solid = barrel.Inventory[0].Itemstack;
        var liquid = barrel.Inventory[1].Itemstack;
        if (solid is not null && (!IsManure(solid) || IsSlurry(solid) || solid.StackSize <= 0 || liquid is not null)
            || liquid is not null && (!IsSlurry(liquid) || liquid.StackSize <= 0)) return null;
        var slurry = liquid?.Item ?? world.GetItem(new AssetLocation("naturalfertilizer", "manureslurryportion"))
            ?? world.GetItem(new AssetLocation("vsslssmod", "manureslurryportion"));
        if (slurry is null) return null;
        var props = BlockLiquidContainerBase.GetContainableProps(new ItemStack(slurry));
        if (props is not { Containable: true } || !float.IsFinite(props.ItemsPerLitre)
            || props.ItemsPerLitre is < 1 or > 10000 || props.ItemsPerLitre != MathF.Floor(props.ItemsPerLitre)) return null;
        var storage = new LatrineWasteStorage(latrinePos.Copy(), pos, barrel, slurry, solid?.Clone(), liquid?.Clone(), (int)props.ItemsPerLitre);
        return storage.Quantity <= storage.Capacity ? storage : null;
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
            || world.BlockAccessor.GetBlock(pos) is not BlockBarrel { Code.Domain: "game" }) return false;
        return Unchanged(barrel.Inventory[0].Itemstack, solid) && Unchanged(barrel.Inventory[1].Itemstack, liquid);

        bool Unchanged(ItemStack? current, ItemStack? original) => original is null ? current is null
            : current is not null && current.StackSize == original.StackSize
                && ReferenceEquals(current.Collectible, original.Collectible) && current.Attributes.Equals(world, original.Attributes, []);
    }

    internal void Add(IWorldAccessor world, int amount, IReadOnlyDictionary<string, double>? pathogens = null, double? actualMassG = null)
    {
        var mass = actualMassG ?? amount * BowelSettlement.PortionG;
        if (amount <= 0 || !double.IsFinite(mass) || mass <= 0 || mass > BowelSettlement.CapacityG || !Matches(world))
            throw new InvalidOperationException("Latrine barrel or contents changed during use.");
        var oldCount = liquid?.StackSize ?? (int)Math.Ceiling(Quantity * unitsPerLitre - 1e-9);
        var addedCount = (int)Math.Ceiling(mass / 1000 * unitsPerLitre - 1e-9);
        if (addedCount <= 0 || oldCount + addedCount > Capacity * unitsPerLitre)
            throw new InvalidOperationException("Latrine barrel is full.");
        try
        {
            var stack = liquid?.Clone() ?? new ItemStack(slurry) { Attributes = solid?.Attributes.Clone() ?? new Vintagestory.API.Datastructures.TreeAttribute() };
            if (solid is not null) stack.Attributes.SetDouble(ManureMass.Key, ManureMass.Total(solid) / oldCount);
            ManureMass.Mix(stack, oldCount, addedCount, mass, pathogens ?? PathogenLoads.Empty);
            stack.StackSize = oldCount + addedCount;
            barrel.Inventory[0].Itemstack = null;
            barrel.Inventory[1].Itemstack = stack;
            barrel.Inventory[0].MarkDirty();
            barrel.Inventory[1].MarkDirty();
            barrel.MarkDirty(true);
            WaterQuality.Invalidate(world, pos);
        }
        catch
        {
            barrel.Inventory[0].Itemstack = solid?.Clone();
            barrel.Inventory[1].Itemstack = liquid?.Clone();
            barrel.MarkDirty(true);
            throw;
        }
    }
}
