using Vintagestory.API.Common;
using Vintagestory.API.MathTools;

namespace Seraph.World;

/// <summary>Immutable host identity and position captured by the eligibility check.</summary>
internal sealed class ChimneyInstallationPlan(BlockPos position, Block host)
{
    private readonly BlockPos position = position.Copy();
    private readonly ItemStack snapshot = new(host);

    internal bool Execute(IBlockAccessor blocks, BlockChimney chimney, ItemSlot slot, Action notify)
    {
        // A plan cannot overwrite a host changed since inspection, or be replayed.
        if (blocks.GetBlock(position).Id != snapshot.Block.Id || blocks.GetBlockEntity(position) is not null
            || slot.Empty || slot.Itemstack.Collectible.Id != chimney.Id) return false;
        var inventory = slot.Itemstack.Clone();
        try
        {
            blocks.SetBlock(chimney.Id, position);
            if (blocks.GetBlock(position).Id != chimney.Id
                || blocks.GetBlockEntity(position) is not BlockEntityChimney { Api: not null } entity
                || entity.Block?.Id != chimney.Id) throw new InvalidOperationException("Chimney entity was not initialized.");
            entity.SetHostStack(snapshot);
            if (entity.HostStack?.Block.Id != snapshot.Block.Id) throw new InvalidOperationException("Chimney host was not retained.");
            // Engine callbacks may fail. Inventory is changed only after successful initialization.
            notify();
            slot.TakeOut(1);
            slot.MarkDirty();
            return true;
        }
        catch
        {
            slot.Itemstack = inventory;
            blocks.SetBlock(snapshot.Block.Id, position);
            return false;
        }
    }
}

internal static class ChimneyGeometry
{
    internal static bool Opening(Block? block, BlockFacing inward)
    {
        if (block is null || block.Id == 0) return true;
        if (block.IsLiquid() || block.Replaceable < 6000 || block.SideSolid[inward.Index]) return false;

        // Replaceable values alone do not prove that a placed block leaves a
        // vent path. Some decorative/stone shapes report replaceable faces;
        // reject collision geometry crossing the outlet's central plume.
        var boxes = block.CollisionBoxes;
        if (boxes is null || boxes.Length == 0) return true;
        return !boxes.Any(box => box.X1 < .75f && box.X2 > .25f
            && box.Z1 < .75f && box.Z2 > .25f
            && (inward == BlockFacing.DOWN ? box.Y1 < .75f : box.Y2 > .25f));
    }

    internal static bool FullHost(Block? block)
    {
        if (block is null || block.Id == 0 || block is BlockChimney or BlockChimneyPart || block.IsLiquid()
            || BlockFacing.ALLFACES.Any(f => !block.SideSolid[f.Index])) return false;
        // No collision geometry is not evidence of a supporting solid cube.
        var boxes = block.CollisionBoxes;
        if (boxes is null || boxes.Length == 0 || boxes.Length > 64) return false;
        if (boxes.Any(b => !float.IsFinite(b.X1) || !float.IsFinite(b.X2) || !float.IsFinite(b.Y1)
            || !float.IsFinite(b.Y2) || !float.IsFinite(b.Z1) || !float.IsFinite(b.Z2))) return false;
        // Coordinate partitioning tests the union, allowing multi-box full cubes
        // while rejecting invisible cavities and thin face-only shells.
        float[] Edges(System.Func<Cuboidf, float> low, System.Func<Cuboidf, float> high) => boxes
            .SelectMany(b => new[] { Math.Clamp(low(b), 0, 1), Math.Clamp(high(b), 0, 1) })
            .Append(0).Append(1).Distinct().Order().ToArray();
        var xs = Edges(b => b.X1, b => b.X2); var ys = Edges(b => b.Y1, b => b.Y2); var zs = Edges(b => b.Z1, b => b.Z2);
        for (var x = 1; x < xs.Length; x++)
        for (var y = 1; y < ys.Length; y++)
        for (var z = 1; z < zs.Length; z++)
        {
            var px = (xs[x - 1] + xs[x]) / 2; var py = (ys[y - 1] + ys[y]) / 2; var pz = (zs[z - 1] + zs[z]) / 2;
            if (!boxes.Any(b => b.X1 <= px && b.X2 >= px && b.Y1 <= py && b.Y2 >= py && b.Z1 <= pz && b.Z2 >= pz)) return false;
        }
        return true;
    }
}

