using Vintagestory.API.Common;
using Vintagestory.API.MathTools;
using Vintagestory.GameContent;

namespace Seraph.World;

/// <summary>Water target resolution shared by world actions and the block-info HUD.</summary>
internal static class WaterInteractionTarget
{
    internal const double InteractionRange = 6;
    internal static BlockPos? Find(IWorldAccessor world, EntityPlayer entity, double range = InteractionRange)
    {
        // Natural water is only actionable before the first solid obstruction.
        // Never claim a bank/container interaction for water behind that block.
        if (FindWaterInLookDirection(world, entity, range) is { } surface) return surface;
        return FindLiquidContainerInLookDirection(world, entity, range);
    }

    internal static bool IsWaterBucket(IWorldAccessor world, BlockPos pos) =>
        world.BlockAccessor.GetBlock(pos, BlockLayersAccess.Solid) is BlockBucket bucket
        && WaterQuality.IsWater(bucket.GetContent(pos));

    internal static bool IsWaterContainer(IWorldAccessor world, BlockPos pos)
    {
        if (world.BlockAccessor.GetBlock(pos, BlockLayersAccess.Solid) is not BlockLiquidContainerBase container
            || container.GetContent(pos) is not { } content || !WaterQuality.IsWater(content)) return false;
        return world.BlockAccessor.GetBlockEntity(pos) is not BlockEntityBarrel { Sealed: true };
    }

    internal static bool IsSurfaceWater(IWorldAccessor world, BlockPos pos)
    {
        // A container's contents are handled by its own right-click flow.
        if (world.BlockAccessor.GetBlock(pos, BlockLayersAccess.Solid) is BlockLiquidContainerBase) return false;
        return TryGetWaterAt(world.BlockAccessor, pos, out _, out _);
    }

    /// <summary>
    /// Resolves the water cell represented by the vanilla block-info selection.
    /// A selected plant or object keeps its native information. Only terrain
    /// beneath water permits a fluid hit before the selected surface.
    /// </summary>
    internal static BlockPos? FindForHover(IWorldAccessor world, EntityPlayer entity,
        BlockSelection? selection, double range = InteractionRange)
    {
        if (selection?.Position is { } selected)
        {
            var solidPos = selected.Copy();
            if (selection.DidOffset) solidPos.Add(selection.Face.Opposite, 1);
            var solid = world.BlockAccessor.GetBlock(solidPos, BlockLayersAccess.Solid);
            if (solid.Id != 0 && !IsWaterBlock(solid) && !IsWaterBed(solid)) return null;
            var eye = entity.Pos.XYZ.AddCopy(entity.LocalEyePos);
            var target = selection.FullPosition ?? selected.ToVec3d().Add(0.5, 0.5, 0.5);
            return TryFindWaterAlongRay(world, eye, target, out _, out var water) ? water : null;
        }
        return FindWaterInLookDirection(world, entity, range);
    }

    private static bool IsWaterBed(Block block) => block.EntityClass is null
        && block.CreativeInventoryTabs?.Contains("terrain") == true
        && block.BlockMaterial is EnumBlockMaterial.Stone or EnumBlockMaterial.Soil
            or EnumBlockMaterial.Gravel or EnumBlockMaterial.Sand;

    /// <summary>Resolves a water cell along a view ray using fluid-layer samples.</summary>
    internal static BlockPos? FindSurfaceWaterAlongLook(IWorldAccessor world, EntityPlayer entity, double range = InteractionRange)
        => FindWaterInLookDirection(world, entity, range);

    private static BlockPos? FindWaterInLookDirection(IWorldAccessor world, EntityPlayer entity,
        double range)
    {
        var eye = entity.Pos.XYZ.AddCopy(entity.LocalEyePos);
        var to = eye.AheadCopy(Math.Max(0.5, range), entity.Pos.Pitch, entity.Pos.Yaw);
        return TryFindWaterAlongRay(world, eye, to, out _, out var water) ? water : null;
    }

    /// <summary>
    /// Finds a liquid container on the view ray for bucket interactions.
    /// </summary>
    private static BlockPos? FindLiquidContainerInLookDirection(IWorldAccessor world, EntityPlayer entity, double range)
    {
        var eye = entity.Pos.XYZ.AddCopy(entity.LocalEyePos);
        var to = eye.AheadCopy(Math.Max(0.5, range), entity.Pos.Pitch, entity.Pos.Yaw);
        return TryFindLiquidContainerAlongRay(world, eye, to, out var container) ? container : null;
    }

    /// <summary>
    /// Resolves the cell represented by a vanilla block selection to nearby
    /// fluid-layer water. Vanilla hover selections commonly point at the solid
    /// block immediately below the fluid layer.
    /// </summary>
    internal static BlockPos? FindSurfaceWaterCell(IWorldAccessor world, BlockPos origin)
    {
        // Prefer the requested fluid cell, then the cells immediately above and
        // below it. This prevents a nearby unrelated pool from winning merely
        // because the search happened to enumerate it first.
        foreach (var y in new[] { 0, 1, -1, 2, -2 })
            foreach (var x in new[] { 0, -1, 1 })
                foreach (var z in new[] { 0, -1, 1 })
                {
                    var candidate = origin.AddCopy(x, y, z);
                    if (TryGetWaterAt(world.BlockAccessor, candidate, out _, out var water)) return water;
                }
        return null;
    }

    /// <summary>
    /// Visits each crossed voxel in distance order, including short intersections
    /// near corners that fixed-distance samples can skip.
    /// </summary>
    internal static bool TryFindWaterAlongRay(IWorldAccessor world, Vec3d eye, Vec3d to,
        out Block waterBlock, out BlockPos waterPos)
    {
        waterBlock = null!;
        waterPos = null!;
        foreach (var pos in RayCells(eye, to))
        {
            var solid = world.BlockAccessor.GetBlock(pos, BlockLayersAccess.Solid);
            if (solid.Id != 0 && !IsWaterBlock(solid)) return false;
            if (TryGetWaterAt(world.BlockAccessor, pos, out waterBlock, out waterPos)) return true;
        }
        return false;
    }

    private static bool TryFindLiquidContainerAlongRay(IWorldAccessor world, Vec3d eye, Vec3d to, out BlockPos containerPos)
    {
        containerPos = null!;
        foreach (var pos in RayCells(eye, to))
        {
            if (world.BlockAccessor.GetBlock(pos, BlockLayersAccess.Solid) is BlockLiquidContainerBase)
            {
                containerPos = pos;
                return true;
            }
            if (world.BlockAccessor.GetBlock(pos, BlockLayersAccess.Solid).Id != 0) return false;
        }
        return false;
    }

    internal static bool TryGetWaterAt(IBlockAccessor accessor, BlockPos pos, out Block waterBlock, out BlockPos waterPos)
    {
        waterBlock = null!;
        waterPos = null!;
        var fluid = accessor.GetBlock(pos, BlockLayersAccess.Fluid);
        if (!IsWaterBlock(fluid)) return false;
        waterBlock = fluid;
        waterPos = pos.Copy();
        return true;
    }

    internal static bool IsWaterBlock(Block? block)
    {
        if (block is null || block.Id == 0 || block is BlockLiquidContainerBase) return false;
        var path = block.Code?.Path?.ToLowerInvariant() ?? "";
        if (path.Contains("lava", StringComparison.Ordinal) || path.Contains("ice", StringComparison.Ordinal)) return false;
        var waterCode = path is "water" or "saltwater" or "boilingwater"
            || path.StartsWith("boilingwater-", StringComparison.Ordinal)
            || path.StartsWith("water-", StringComparison.Ordinal) || path.StartsWith("saltwater-", StringComparison.Ordinal)
            || block.LiquidCode is "water" or "saltwater" or "boilingwater";
        if (!waterCode && block is not BlockWater) return false;
        // A name containing "water" alone also matches water lilies and other
        // solid objects. Require a water code family, liquid code or water type.
        return true;
    }

    // Grid-boundary traversal (3D DDA): t is the fraction of the whole segment.
    // No coordinate packing, fixed sampling interval or duplicate-cell cache.
    internal static IEnumerable<BlockPos> RayCells(Vec3d start, Vec3d end)
    {
        if (!double.IsFinite(start.X + start.Y + start.Z + end.X + end.Y + end.Z)) yield break;
        var cell = new BlockPos((int)Math.Floor(start.X), (int)Math.Floor(start.Y), (int)Math.Floor(start.Z));
        var target = new BlockPos((int)Math.Floor(end.X), (int)Math.Floor(end.Y), (int)Math.Floor(end.Z));
        var dx = end.X - start.X;
        var dy = end.Y - start.Y;
        var dz = end.Z - start.Z;
        double Boundary(double origin, int coordinate, double delta) => delta == 0
            ? double.PositiveInfinity : (coordinate + (delta > 0 ? 1 : 0) - origin) / delta;
        var tx = Boundary(start.X, cell.X, dx);
        var ty = Boundary(start.Y, cell.Y, dy);
        var tz = Boundary(start.Z, cell.Z, dz);
        var remaining = 1L + Math.Abs((long)target.X - cell.X)
            + Math.Abs((long)target.Y - cell.Y) + Math.Abs((long)target.Z - cell.Z);
        while (remaining-- > 0)
        {
            yield return cell.Copy();
            if (cell.Equals(target)) yield break;
            // Axes already at the endpoint cell must not step past the endpoint.
            if (cell.X == target.X) tx = double.PositiveInfinity;
            if (cell.Y == target.Y) ty = double.PositiveInfinity;
            if (cell.Z == target.Z) tz = double.PositiveInfinity;
            var next = Math.Min(tx, Math.Min(ty, tz));
            // Cross tied planes together; cells touched only at an edge have no
            // interior intersection and should not obstruct the ray.
            if (tx == next) { cell.X += Math.Sign(dx); tx += 1 / Math.Abs(dx); }
            if (ty == next) { cell.Y += Math.Sign(dy); ty += 1 / Math.Abs(dy); }
            if (tz == next) { cell.Z += Math.Sign(dz); tz += 1 / Math.Abs(dz); }
        }
    }
}
