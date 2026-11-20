namespace Seraph.Core.Framework;

/// <summary>Water in one lined shaft, separate from the surrounding regional aquifer.</summary>
public static class WellReservoir
{
    // 120 square model pixels inside the exported well wall (16x16 pixels/m²).
    public const double AreaM2 = 120d / 256;
    public static double Capacity(int bottomY, int depth, double surfaceY, double waterTableDepth)
        => Math.Clamp(surfaceY - waterTableDepth - bottomY, 0, Math.Max(0, depth)) * AreaM2 * 1000;

    public static double Height(double litres) => Math.Max(0, litres) / (AreaM2 * 1000);

    public static double InflowPerDay(double capacityL, double storedL, AquiferMaterialClass material)
    {
        if (!double.IsFinite(capacityL) || !double.IsFinite(storedL) || capacityL <= 0 || storedL < 0) return 0;
        var submerged = Height(capacityL);
        var drawdown = Height(Math.Max(0, capacityL - storedL));
        // Calibrated for hand-drawn survival wells, rather than the field-scale
        // aquifer conductivity. Saturated shaft depth adds a bounded intake bonus;
        // the last half metre fills more slowly as the head difference disappears.
        var basePerDay = material switch
        {
            AquiferMaterialClass.Clay => 4.8,
            AquiferMaterialClass.Silt => 6,
            AquiferMaterialClass.FineSand => 8,
            AquiferMaterialClass.CoarseSand => 10,
            AquiferMaterialClass.Gravel => 12,
            AquiferMaterialClass.FracturedRock => 7.2,
            _ => throw new ArgumentOutOfRangeException(nameof(material))
        };
        var depthBonus = 1 + .2 * Math.Clamp(submerged - 1, 0, 4);
        return basePerDay * depthBonus * Math.Clamp(drawdown / .5, 0, 1);
    }

    public static double Incoming(double capacityL, double storedL, double ratePerDay, double elapsedDays, double aquiferL)
        => Math.Min(Math.Max(0, capacityL - storedL), Math.Min(Math.Max(0, aquiferL),
            Math.Max(0, ratePerDay) * Math.Clamp(elapsedDays, 0, 1d / 24)));
}
