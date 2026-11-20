namespace Seraph.Core.Framework;

/// <summary>Infers atmospheric moisture from the available climate and weather inputs.</summary>
public static class HumidityModel
{
    public static double RelativeHumidity(double climateRainfall, double currentRainfall, double outdoorC, double airC)
    {
        if (!double.IsFinite(climateRainfall) || !double.IsFinite(currentRainfall)
            || !double.IsFinite(outdoorC) || !double.IsFinite(airC)) return double.NaN;
        var baseline = .30 + .45 * Math.Clamp(climateRainfall, 0, 1);
        var outdoor = baseline + (0.95 - baseline) * Math.Clamp(currentRainfall, 0, 1);
        // Preserve vapour pressure when room heating changes air temperature.
        static double Saturation(double c) => 610.94 * Math.Exp(17.625 * Math.Clamp(c, -80, 80) / (Math.Clamp(c, -80, 80) + 243.04));
        return Math.Clamp(outdoor * Saturation(outdoorC) / Saturation(airC), 0, 1);
    }
}
