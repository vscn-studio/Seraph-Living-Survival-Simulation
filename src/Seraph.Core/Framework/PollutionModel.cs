namespace Seraph.Core.Framework;

public readonly record struct PollutionLoad(double Fecal, double Animals, double Carrion, double Smoke, double Chemical)
{
    public static PollutionLoad operator +(PollutionLoad a, PollutionLoad b) =>
        new(a.Fecal + b.Fecal, a.Animals + b.Animals, a.Carrion + b.Carrion, a.Smoke + b.Smoke, a.Chemical + b.Chemical);
    public PollutionLoad Scale(double weight) => new(Fecal * weight, Animals * weight, Carrion * weight, Smoke * weight, Chemical * weight);
}

public readonly record struct PollutionLevels(double Water, double Smoke, double BiologicalAir)
{
    public double Air => Math.Clamp(Smoke + BiologicalAir, 0, 1);
}

/// <summary>Normalized gameplay loads, not laboratory concentrations or measured species counts.</summary>
public static class PollutionModel
{
    public static PollutionLevels Levels(PollutionLoad load)
    {
        var bio = Positive(load.Fecal) * .16 + Positive(load.Animals) * .012 + Positive(load.Carrion) * .12;
        return new(Saturate(bio), Saturate(Positive(load.Smoke) * .12), Saturate(bio * .35));
    }

    public static double WaterHazard(PollutionLoad load, double temperatureC, bool groundwater, bool salt,
        double freshHazard, double fecalHazard)
    {
        // Chemicals contribute to the displayed total, but cannot create pathogens.
        var levels = Levels(load with { Chemical = 0 });
        var temperature = double.IsFinite(temperatureC) ? Math.Clamp(temperatureC, -80, 110) : 15;
        // Freezing slows exposure; warm water raises it. Neither salt nor an aquifer sterilizes water.
        var growth = Math.Clamp(.45 + (temperature + 2) * .035, .2, 1.8);
        var attenuation = groundwater ? .25 : 1;
        var baseline = Positive(freshHazard) * (groundwater ? .35 : 1) * (salt ? .45 : 1);
        return Math.Clamp((baseline + levels.Water * Positive(fecalHazard) * attenuation) * growth, 0, 100);
    }

    public static double DoseRisk(double hazardPerL, double litres) =>
        1 - Math.Exp(-Positive(hazardPerL) * Positive(litres));
    private static double Saturate(double value) => 1 - Math.Exp(-Math.Min(100, value));
    private static double Positive(double value) => double.IsFinite(value) ? Math.Max(0, value) : 0;
}
