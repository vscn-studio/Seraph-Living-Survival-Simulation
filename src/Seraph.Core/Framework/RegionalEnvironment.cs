namespace Seraph.Core.Framework;

/// <summary>Dimensionless game indices. None of these values are ppm or laboratory measurements.</summary>
public readonly record struct AirChemistry(double CO, double PM25, double PM10, double SO2, double NH3, double CH4, double H2S)
{
    public static AirChemistry operator +(AirChemistry a, AirChemistry b) => new(a.CO + b.CO, a.PM25 + b.PM25,
        a.PM10 + b.PM10, a.SO2 + b.SO2, a.NH3 + b.NH3, a.CH4 + b.CH4, a.H2S + b.H2S);
    public AirChemistry Scale(double k) => new(CO * k, PM25 * k, PM10 * k, SO2 * k, NH3 * k, CH4 * k, H2S * k);
    public AirChemistry Clamp() => new(Unit(CO), Unit(PM25), Unit(PM10), Unit(SO2), Unit(NH3), Unit(CH4), Unit(H2S));
    // CO has its own physiological burden and must not also enter the chemical-gas channel.
    public double Toxicity => Unit(SO2 * .3 + H2S * .6 + NH3 * .15);
    public double Particles => Unit(PM25 * .7 + PM10 * .3);
    public double Total => Unit(Toxicity + Particles);
    private static double Unit(double x) => double.IsFinite(x) ? Math.Clamp(x, 0, 1) : 0;
}

public enum BurningFuel { Wood, Charcoal, Coal }

public sealed record RegionalSources
{
    public IReadOnlyDictionary<string, double> Pathogens { get; init; } = PathogenLoads.Empty;
    public AirChemistry Emissions { get; init; }
    public double Fecal { get; init; }
    public double Nitrogen { get; init; }
    public double Phosphorus { get; init; }
    public double Metals { get; init; }
    public double Forest { get; init; }
    public double Vegetation { get; init; }
    /// <summary>Retained for save compatibility. RealSmoke transports the plume; this no longer scrubs regional gas.</summary>
    public double ChimneyScrub { get; init; }
    public double Cultivation { get; init; }
    public double Grazing { get; init; }
    public int Wildlife { get; init; }
}

public sealed record RegionalEnvironment
{
    [System.Text.Json.Serialization.JsonRequired]
    public string Format { get; init; } = "seraph.release.region";
    [System.Text.Json.Serialization.JsonRequired]
    public int Version { get; init; } = 1;
    public IReadOnlyDictionary<string, double> SurfaceAgents { get; init; } = PathogenLoads.Empty;
    public IReadOnlyDictionary<string, double> SoilAgents { get; init; } = PathogenLoads.Empty;
    public IReadOnlyDictionary<string, double> GroundAgents { get; init; } = PathogenLoads.Empty;
    // Environmental chemistry is retained for save compatibility and diagnostics.
    // Crop growth continues to use the native soil and fertilizer rules.
    public double CropGrowthMultiplier => 1;
    public AirChemistry Air { get; init; }
    public double AirbornePathogens { get; init; }
    public double SurfacePathogens { get; init; }
    public double GroundPathogens { get; init; }
    public double SoilPathogens { get; init; }
    public double SoilNitrogen { get; init; }
    public double SoilPhosphorus { get; init; }
    public double SurfaceNitrate { get; init; }
    public double SurfacePhosphate { get; init; }
    public double GroundNitrate { get; init; }
    public double HeavyMetals { get; init; }
    public double Acidity { get; init; }
    public double OrganicMatter { get; init; } = 1;
    public double Erosion { get; init; }
    public double ForestBaseline { get; init; }
    public double ForestLoss { get; init; }
    public double CarbonSink { get; init; }
    public double Ecology { get; init; } = 1;
    public int WildlifeBaseline { get; init; }
    public double WildlifeLoss { get; init; }
    public double GroundwaterStress { get; init; }
    public double SoilDamage => Math.Clamp(Erosion * .3, 0, 1);
    public double RechargeMultiplier => Math.Clamp((.5 + OrganicMatter * .5) * (1 - Erosion * .4) * (1 - GroundwaterStress * .3), .2, 1);
    public double ChemicalWaterRisk(bool groundwater) => 0;
    public bool Valid() => Format == "seraph.release.region" && Version == 1 && PathogenLoads.Valid(SurfaceAgents)
        && PathogenLoads.Valid(SoilAgents) && PathogenLoads.Valid(GroundAgents) && Air == Air.Clamp() && WildlifeBaseline >= 0
        && new[] { AirbornePathogens, SurfacePathogens, GroundPathogens, SoilPathogens, SoilNitrogen, SoilPhosphorus,
            SurfaceNitrate, SurfacePhosphate, GroundNitrate, HeavyMetals, Acidity, OrganicMatter, Erosion,
            ForestBaseline, ForestLoss, CarbonSink, Ecology, WildlifeLoss, GroundwaterStress }
        .All(x => double.IsFinite(x) && x is >= 0 and <= 1);
}

public static class RegionalEnvironmentModel
{
    public const double RegionalIntervalHours = 5d / 60;
    public const double WorldIntervalHours = 1;
    // Emission rates per game hour for an active source. Charcoal is cleaner, coal has sulfur.
    public static AirChemistry Combustion(BurningFuel fuel) => fuel switch
    {
        BurningFuel.Charcoal => new(.08, .035, .025, .004, 0, 0, 0),
        BurningFuel.Coal => new(.11, .09, .07, .08, 0, 0, 0),
        _ => new(.1, .10, .08, .002, 0, .012, 0)
    };

    public static RegionalEnvironment Create(RegionalSources source) => new()
    { ForestBaseline = Unit(source.Forest), WildlifeBaseline = Math.Max(0, source.Wildlife) };

    /// <summary>Exact relaxation for constant sources. The caller supplies only loaded upwind cells.</summary>
    public static RegionalEnvironment Regional(RegionalEnvironment old, RegionalSources source, double hours,
        double rain, double wind, AirChemistry incoming = default, double temperatureC = 15)
    {
        if (!old.Valid() || !double.IsFinite(hours) || hours <= 0) return old;
        var dt = Math.Min(hours, .25);
        rain = Unit(rain); wind = double.IsFinite(wind) ? Math.Clamp(wind, 0, 4) : 0;
        var bio = Positive(source.Fecal);
        var unknownBio = Math.Max(0, bio - source.Pathogens.Values.Sum());
        var wet = .15 + .85 * rain;
        // RealSmoke owns plume transport. Do not independently clear its gas payload by
        // vegetation or a chimney radius; that caused gas to remain where smoke had moved.
        var decay = .25 + wind * .8 + rain * .6;
        var temperatureDecay = Math.Clamp(Math.Pow(2, (temperatureC - 15) / 20), .25, 4);
        var soilDecay = Math.Log(2) / 24 * temperatureDecay;
        var surfaceDecay = Math.Log(2) / 12 * temperatureDecay;
        var groundDecay = Math.Log(2) / 72 * temperatureDecay;
        var biologicalGas = new AirChemistry(0, 0, 0, 0, bio * .018, bio * .009 * wet, bio * .006 * wet);
        var emitted = source.Emissions + biologicalGas;
        var airInput = emitted + incoming.Scale(wind * .2);
        var air = new AirChemistry(
            RelaxAir(old.Air.CO, airInput.CO, decay, dt),
            RelaxAir(old.Air.PM25, airInput.PM25, decay, dt),
            RelaxAir(old.Air.PM10, airInput.PM10, decay, dt),
            RelaxAir(old.Air.SO2, airInput.SO2, decay, dt),
            RelaxAir(old.Air.NH3, airInput.NH3, decay, dt),
            RelaxAir(old.Air.CH4, airInput.CH4, decay, dt),
            RelaxAir(old.Air.H2S, airInput.H2S, decay, dt)).Clamp();
        return old with
        {
            Air = air,
            AirbornePathogens = Relax(old.AirbornePathogens, bio * .016, .3 + wind * .8 + rain * .4, dt),
            SoilPathogens = Relax(old.SoilPathogens, unknownBio * .06, soilDecay, dt),
            SurfacePathogens = Unit(Transport(old.SurfacePathogens, unknownBio * .035, old.SoilPathogens, unknownBio * .06, soilDecay, surfaceDecay, rain * .12, dt)),
            GroundPathogens = Unit(Transport(old.GroundPathogens, 0, old.SoilPathogens, unknownBio * .06, soilDecay, groundDecay, wet * .016, dt)),
            SoilAgents = RelaxAgents(old.SoilAgents, PathogenLoads.Scale(source.Pathogens, .06), soilDecay, dt),
            SurfaceAgents = TransportAgents(old.SurfaceAgents, old.SoilAgents, source.Pathogens, .035, soilDecay, surfaceDecay, rain * .12, dt),
            GroundAgents = TransportAgents(old.GroundAgents, old.SoilAgents, source.Pathogens, 0, soilDecay, groundDecay, wet * .016, dt)
        };
    }

    public static RegionalEnvironment World(RegionalEnvironment old, RegionalSources source, double hours, double extractedL)
    {
        if (!old.Valid() || !double.IsFinite(hours) || hours <= 0) return old;
        var dt = Math.Min(hours, 1);
        var forest = Unit(source.Forest);
        var baseline = Math.Max(old.ForestBaseline, forest);
        var forestLoss = baseline > .01 ? Unit((baseline - forest) / baseline) : 0;
        var grazing = Math.Max(0, Positive(source.Grazing) - Unit(source.Vegetation) * 8 - 2) / 30;
        var tillage = Unit(source.Cultivation);
        var erosion = Relax(old.Erosion, forestLoss * .015 + grazing * .025 + tillage * .008,
            .004 + Unit(source.Vegetation) * .012, dt);
        var organic = Unit(old.OrganicMatter + dt * ((forest + Unit(source.Vegetation)) * .002 * (1 - old.OrganicMatter)
            - (grazing * .002 + tillage * .001 + old.Erosion * .002)));
        var stress = Unit(old.GroundwaterStress * Math.Exp(-dt / 48) + Positive(extractedL) / 10000);
        return old with { ForestBaseline = baseline, ForestLoss = forestLoss,
            Erosion = erosion, OrganicMatter = organic, GroundwaterStress = stress };
    }

    private static IReadOnlyDictionary<string, double> RelaxAgents(IReadOnlyDictionary<string, double> old,
        IReadOnlyDictionary<string, double> source, double decay, double dt)
    {
        var keep = Math.Exp(-decay * dt);
        return PathogenLoads.Add(PathogenLoads.Scale(old, keep), PathogenLoads.Scale(source, (1 - keep) / decay));
    }
    private static double Transport(double old, double direct, double soil, double soilInput,
        double soilDecay, double decay, double transfer, double dt)
    {
        var keep = Math.Exp(-decay * dt);
        var equilibrium = soilInput / soilDecay;
        var fromSoil = equilibrium * (1 - keep) / decay
            + (soil - equilibrium) * (Math.Exp(-soilDecay * dt) - keep) / (decay - soilDecay);
        return old * keep + direct * (1 - keep) / decay + transfer * fromSoil;
    }
    private static IReadOnlyDictionary<string, double> TransportAgents(IReadOnlyDictionary<string, double> old,
        IReadOnlyDictionary<string, double> soil, IReadOnlyDictionary<string, double> source,
        double direct, double soilDecay, double decay, double transfer, double dt)
        => old.Keys.Concat(soil.Keys).Concat(source.Keys).Distinct(StringComparer.Ordinal).ToDictionary(id => id,
            id => Math.Clamp(Transport(old.GetValueOrDefault(id), source.GetValueOrDefault(id) * direct,
                soil.GetValueOrDefault(id), source.GetValueOrDefault(id) * .06, soilDecay, decay, transfer, dt), 0, 100));
    private static double Relax(double old, double source, double decay, double dt)
    { var keep = Math.Exp(-decay * dt); return Unit(old * keep + Positive(source) * (1 - keep) / decay); }
    private static double RelaxAir(double old, double source, double decay, double dt)
    { var keep = Math.Exp(-decay * dt); return old * keep + Positive(source) * (1 - keep) / decay; }
    private static double Unit(double x) => double.IsFinite(x) ? Math.Clamp(x, 0, 1) : 0;
    private static double Positive(double x) => double.IsFinite(x) ? Math.Max(0, x) : 0;
}
