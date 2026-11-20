using Vintagestory.API.Common;

namespace Seraph.World;

internal static class BedRestProperties
{
    internal static double Quality(Block? bed)
    {
        if (bed is null) return .45;
        var configured = bed.Attributes?["sleepEfficiency"].AsDouble(double.NaN) ?? double.NaN;
        if (double.IsFinite(configured)) return Math.Clamp(configured / .8, .45, 1);
        var path = bed.Code?.Path ?? "";
        if (path.Contains("comfy", StringComparison.OrdinalIgnoreCase)) return 1;
        if (path.Contains("hay", StringComparison.OrdinalIgnoreCase) || path.Contains("straw", StringComparison.OrdinalIgnoreCase)) return .68;
        if (path.Contains("mattress", StringComparison.OrdinalIgnoreCase)) return .9;
        if (path.Contains("bed", StringComparison.OrdinalIgnoreCase)) return .95;
        return .45;
    }
}
