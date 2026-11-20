namespace Seraph.Core.Framework;

/// <summary>Time-based cooling and thermal pathogen reduction for stored bath water.</summary>
public static class BathWaterTreatment
{
    public static WaterBatch Advance(WaterBatch water, double ambientC, double seconds,
        double coolingHalfLifeSeconds = 5400, double thermalKillPerDegreeSecond = .00002)
    {
        if (water.Validate() is not null || !double.IsFinite(ambientC) || !double.IsFinite(seconds) || seconds < 0
            || !double.IsFinite(coolingHalfLifeSeconds) || coolingHalfLifeSeconds <= 0
            || !double.IsFinite(thermalKillPerDegreeSecond) || thermalKillPerDegreeSecond < 0)
            throw new ArgumentException("Invalid bath-water treatment input.");
        if (water.VolumeL <= 0 || seconds == 0) return water;

        var ambient = Math.Clamp(ambientC, -80, 110);
        var keepTemperatureDelta = Math.Exp(-Math.Log(2) * seconds / coolingHalfLifeSeconds);
        var temperature = ambient + (water.TemperatureC - ambient) * keepTemperatureDelta;
        if (temperature >= 100) return WaterHeating.AtTemperature(water, temperature);
        if (water.TemperatureC >= 100)
            return WaterHeating.AtTemperature(water, 100) with { TemperatureC = temperature };

        // Temperature below 45 C has no extra disinfection effect; chemical and physical
        // contamination remain untouched at every temperature.
        var warmDegreeSeconds = IntegratedWarmth(water.TemperatureC, ambient, seconds, coolingHalfLifeSeconds);
        var microbialKeep = Math.Exp(-thermalKillPerDegreeSecond * warmDegreeSeconds);
        return water with
        {
            TemperatureC = temperature,
            PathogensPerL = PathogenLoads.Scale(water.PathogensPerL, microbialKeep),
            EntericHazardPerL = water.EntericHazardPerL * microbialKeep
        };
    }

    private static double IntegratedWarmth(double start, double ambient, double seconds, double halfLifeSeconds)
    {
        const double threshold = 45;
        var delta = start - ambient;
        if (Math.Abs(delta) < 1e-12) return Math.Max(0, start - threshold) * seconds;
        var tau = halfLifeSeconds / Math.Log(2);
        var end = ambient + delta * Math.Exp(-seconds / tau);
        var primitive = (ambient - threshold) * seconds
            + delta * tau * (1 - Math.Exp(-seconds / tau));
        if (start >= threshold && end >= threshold) return Math.Max(0, primitive);
        if (start < threshold && end < threshold) return 0;

        var crossingRatio = (threshold - ambient) / delta;
        var crossing = Math.Clamp(-tau * Math.Log(crossingRatio), 0, seconds);
        if (start > threshold)
            return Math.Max(0, (ambient - threshold) * crossing
                + delta * tau * (1 - Math.Exp(-crossing / tau)));
        var elapsedAfterCrossing = seconds - crossing;
        return Math.Max(0, (ambient - threshold) * elapsedAfterCrossing
            + delta * tau * (Math.Exp(-crossing / tau) - Math.Exp(-seconds / tau)));
    }
}
