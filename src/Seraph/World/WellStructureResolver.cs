using Vintagestory.API.Common;
using Vintagestory.API.MathTools;

namespace Seraph.World;

/// <summary>Resolves the only structure that is allowed to expose or draw from a virtual well.</summary>
internal static class WellStructureResolver
{
    internal const int MaxDepth = 64;

    internal readonly record struct Bounds(BlockPos Head, int BottomY, int TopY, int Depth)
    {
        internal BlockPos Bottom => Head.Copy().Set(Head.X, BottomY, Head.Z);
        internal double DepthM => Math.Max(1, Depth);
    }

    internal static bool TryFromHead(IBlockAccessor accessor, BlockPos head, out Bounds bounds)
    {
        bounds = default;
        if (!IsHead(accessor.GetBlock(head))) return false;
        var top = head.Y - 1;
        var bottom = top;
        if (!IsWall(accessor.GetBlock(head.Copy().Set(head.X, top, head.Z)))) return false;
        for (var i = 1; i < MaxDepth; i++)
        {
            if (!IsWall(accessor.GetBlock(head.Copy().Set(head.X, top - i, head.Z)))) break;
            bottom = top - i;
        }
        if (!HasFoundation(accessor, head, bottom)) return false;
        bounds = new Bounds(head.Copy(), bottom, top, top - bottom + 1);
        return true;
    }

    internal static bool TryFromWall(IBlockAccessor accessor, BlockPos wall, out Bounds bounds)
    {
        bounds = default;
        if (!IsWall(accessor.GetBlock(wall))) return false;
        var bottom = wall.Y;
        for (var i = 1; i < MaxDepth && bottom > 0; i++)
        {
            if (!IsWall(accessor.GetBlock(wall.Copy().Set(wall.X, bottom - 1, wall.Z)))) break;
            bottom--;
        }
        var top = wall.Y;
        for (var i = 1; i < MaxDepth && top - bottom + 1 < MaxDepth; i++)
        {
            if (!IsWall(accessor.GetBlock(wall.Copy().Set(wall.X, top + 1, wall.Z)))) break;
            top++;
        }
        var head = wall.Copy().Set(wall.X, top + 1, wall.Z);
        if (!IsHead(accessor.GetBlock(head))) return false;
        if (!HasFoundation(accessor, wall, bottom)) return false;
        bounds = new Bounds(head, bottom, top, top - bottom + 1);
        return true;
    }

    internal static bool IsWall(Block? block) => block?.Code?.Domain == "vsslssmod" && block.Code.Path == "wellwall";
    internal static bool ProtectedIntake(IBlockAccessor accessor, Bounds bounds)
    {
        var terrain = accessor.GetTerrainMapheightAt(bounds.Head);
        if (terrain - bounds.BottomY < 12) return false;
        var consecutive = 0;
        for (var y = bounds.BottomY; y <= Math.Min(terrain, bounds.BottomY + MaxDepth); y++)
        {
            var layer = true;
            foreach (var face in BlockFacing.HORIZONTALS)
            {
                var p = bounds.Head.AddCopy(face.Normali.X, y - bounds.Head.Y, face.Normali.Z);
                if (accessor.GetChunkAtBlockPos(p) is null) return false;
                var path = accessor.GetBlock(p, BlockLayersAccess.Solid).Code?.Path ?? "";
                // Inspect untouched surrounding strata, never the constructed shaft wall.
                if (!path.StartsWith("rock-", StringComparison.Ordinal) && !path.StartsWith("clay-", StringComparison.Ordinal)
                    && path != "clay") { layer = false; break; }
            }
            consecutive = layer ? consecutive + 1 : 0;
            if (consecutive >= 2) return true;
        }
        return false;
    }
    private static bool HasFoundation(IBlockAccessor accessor, BlockPos column, int bottom)
    {
        var block = accessor.GetBlock(column.Copy().Set(column.X, bottom - 1, column.Z), BlockLayersAccess.Solid);
        return block is { Id: > 0, MatterState: EnumMatterState.Solid }
            && block.SideSolid[BlockFacing.UP.Index];
    }
    internal static bool IsHead(Block? block) => block?.Code?.Domain == "vsslssmod" && block.Code.Path == "wellhead";
}
