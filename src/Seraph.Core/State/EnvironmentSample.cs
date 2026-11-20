namespace Seraph.Core.State;

[Flags]
public enum EnvironmentSourceQuality
{
    None = 0,
    EstimatedHumidity = 1,
    EstimatedWind = 2,
    EstimatedRadiantTemperature = 4,
    FixedClothing = 8,
    EstimatedContactTemperature = 16,
    AllEstimated = EstimatedHumidity | EstimatedWind | EstimatedRadiantTemperature | FixedClothing | EstimatedContactTemperature
}

/// <summary>Sampled boundary conditions. Quality identifies estimates; it does not make invalid values acceptable.</summary>
public sealed record EnvironmentSample
{
    public static EnvironmentSample Neutral { get; } = new();
    public bool IsValid { get; init; } = true;
    public EnvironmentSourceQuality SourceQuality { get; init; } = EnvironmentSourceQuality.AllEstimated;
    public double AirTemperatureC { get; init; } = 20;
    public double RelativeHumidity { get; init; } = 0.5;
    public double WindSpeedMps { get; init; }
    public double RadiantTemperatureC { get; init; } = 20;
    public double ClothingInsulationClo { get; init; } = 0.6;
    public double ClothingEvaporativeResistanceM2PaPerW { get; init; } = 15;
    public double AbsorbedSolarPowerW { get; init; }
    public double ContactConductanceWPerK { get; init; }
    public double ContactTemperatureC { get; init; } = 20;
    /// <summary>Body surface in water; this portion exchanges with water instead of air or evaporating.</summary>
    public double WaterImmersionFraction { get; init; }
    public double RainFilmInflowKgPerSecond { get; init; }
    /// <summary>Bounded ambient particulate/chemical load used by the disease model.</summary>
    public double AirPollution { get; init; }
    public double AirbornePathogens { get; init; }
    public double CarbonMonoxide { get; init; }
    public double ToxicGas { get; init; }
    /// <summary>Surface and room contamination proxy, separate from airborne load.</summary>
    public double EnvironmentalContamination { get; init; }
    /// <summary>Room air temperature relative to the sampled outdoor climate.</summary>
    public double RoomHeatDeltaC { get; init; }
    /// <summary>0 is stagnant enclosed air; 1 is freely ventilated outdoor air.</summary>
    public double RoomVentilation { get; init; } = 1;
    /// <summary>Verified source-specific exposure snapshot, captured on the world thread.</summary>
    public IReadOnlyDictionary<string, double> DiseaseSources { get; init; } = Framework.PathogenLoads.Empty;
    public bool VectorSuitable { get; init; }
    public bool WashedByEnvironment { get; init; }
    public bool DampSkin { get; init; }
    /// <summary>Per-region uncovered fraction (0 covered, 1 exposed); derived from worn clothing on the server.</summary>
    public IReadOnlyDictionary<string, double> ColdExposureByRegion { get; init; } = new Dictionary<string, double>();

    public string? Validate()
    {
        if (!IsValid) return "Environment sample is explicitly invalid.";
        if (!Framework.PathogenLoads.Valid(DiseaseSources) || ColdExposureByRegion is null || ColdExposureByRegion.Count > 16
            || ColdExposureByRegion.Any(p => !Framework.Anatomy.Known(p.Key) || p.Key == "general" || !BodyProfile.In(p.Value, 0, 1)))
            return "Invalid environmental disease sources or regional cold exposure.";
        if (!BodyProfile.In(AirTemperatureC, -80, 100)
            || !BodyProfile.In(RadiantTemperatureC, -80, 100)
            || !BodyProfile.In(ContactTemperatureC, -80, 100))
            return "Invalid environmental temperature.";
        if (!BodyProfile.In(RelativeHumidity, 0, 1) || !BodyProfile.In(WindSpeedMps, 0, 100)
            || !BodyProfile.In(ClothingInsulationClo, 0, 20)
            || !BodyProfile.In(ClothingEvaporativeResistanceM2PaPerW, 0, 10000)
            || !BodyProfile.In(AbsorbedSolarPowerW, 0, 10000)
            || !BodyProfile.In(ContactConductanceWPerK, 0, 10000)
            || !BodyProfile.In(WaterImmersionFraction, 0, 1)
            || !BodyProfile.In(RainFilmInflowKgPerSecond, 0, 10)
            || !BodyProfile.In(AirPollution, 0, 1)
            || !BodyProfile.In(AirbornePathogens, 0, 1) || !BodyProfile.In(CarbonMonoxide, 0, 1) || !BodyProfile.In(ToxicGas, 0, 1)
            || !BodyProfile.In(EnvironmentalContamination, 0, 1)
            || !BodyProfile.In(RoomHeatDeltaC, -100, 100)
            || !BodyProfile.In(RoomVentilation, 0, 1))
            return "Invalid humidity, wind, clothing or boundary flow.";
        return null;
    }
}
