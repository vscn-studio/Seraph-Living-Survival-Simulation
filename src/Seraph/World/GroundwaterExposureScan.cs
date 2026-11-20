using Vintagestory.API.Common;
using Vintagestory.API.MathTools;

namespace Seraph.World;

internal static class GroundwaterExposureScan
{
    internal const int CellsPerPass = 86;
    internal static void Visit(IBlockAccessor blocks, BlockPos origin, ref int cursor, Action<BlockPos> visit)
    {
        // Four 250 ms callbacks cover the former once-per-second 7x7x7 sweep.
        // Excavation events still reveal immediately. Never request unloaded chunks.
        for (var n = 0; n < CellsPerPass; n++)
        {
            var index = cursor; cursor = (cursor + 1) % 343;
            var pos = origin.AddCopy(index / 49 - 3, index % 7 - 3, index / 7 % 7 - 3);
            if (pos.Y < 1 || blocks.GetChunkAtBlockPos(pos) is null) continue;
            if (blocks.GetTerrainMapheightAt(pos) - (pos.Y + .5) < -1) continue;
            if (blocks.GetBlock(pos, BlockLayersAccess.Solid).Id == 0) visit(pos);
        }
    }
}
