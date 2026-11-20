using Seraph.World;
using Vintagestory.API.Common;
using Vintagestory.API.MathTools;

namespace Seraph.Server;

internal static class WaterImmersion
{
    // A bounded central body column approximates wetted surface from actual
    // liquid height, including wading. It never loads chunks or searches nearby
    // pools, and does not confuse lava, ice or a filled vessel with immersion.
    internal static (BlockPos? Source, double Fraction) Sample(IBlockAccessor blocks, EntityAgent entity)
    {
        var bottom = entity.Pos.Y + (entity.CollisionBox?.Y1 ?? 0);
        var top = entity.Pos.Y + (entity.CollisionBox?.Y2 ?? 1.8);
        var height = top - bottom;
        if (!double.IsFinite(bottom) || !double.IsFinite(top) || height is <= 0 or > 4) return (null, 0);
        var origin = entity.Pos.AsBlockPos;
        BlockPos? source = null;
        var submerged = 0d;
        for (var y = (int)Math.Floor(bottom); y < Math.Ceiling(top); y++)
        {
            var pos = new BlockPos(origin.X, y, origin.Z, origin.dimension);
            if (blocks.GetChunkAtBlockPos(pos) is null
                || !WaterInteractionTarget.TryGetWaterAt(blocks, pos, out var fluid, out _)) continue;
            var surface = y + Math.Clamp(fluid.LiquidLevel / 7d, 0, 1);
            var overlap = Math.Max(0, Math.Min(top, surface) - Math.Max(bottom, y));
            if (overlap <= 0) continue;
            source ??= pos;
            submerged += overlap;
        }
        return (source, Math.Clamp(submerged / height, 0, 1));
    }

    // Whole-body 120 W/K boundary, reduced by submerged area and residual wet
    // clothing resistance (10% of dry insulation). Uses the sampled CLO value.
    internal static double Conductance(double fraction, double clo)
        => fraction / (1d / 120 + Math.Max(0, clo) * .155 * .1 / 1.8);
}
