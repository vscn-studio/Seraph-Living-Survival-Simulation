namespace Seraph.Core.Framework;

/// <summary>
/// Pure, server-owned water-cycle state. A cell is virtual storage; it never implies a
/// block edit or a generated water source in the world.
/// </summary>
public readonly record struct GroundwaterCell(
    double StorageL,
    double CapacityL,
    double SalinityGPerL,
    double TemperatureC)
{
    public string? Validate() => !Finite(StorageL, 0, 1e12)
        || !Finite(CapacityL, 1, 1e12)
        || StorageL > CapacityL
        || !Finite(SalinityGPerL, 0, 400)
        || !Finite(TemperatureC, -80, 110)
        ? "Invalid groundwater cell." : null;

    private static bool Finite(double value, double min, double max)
        => double.IsFinite(value) && value >= min && value <= max;
}

public readonly record struct WaterCycleInputs(
    double ElapsedSeconds,
    double RainfallL,
    double InfiltrationFraction,
    double EvaporationPotentialL,
    double SurfaceRechargeL,
    double ExtractionL,
    double AmbientTemperatureC,
    double RelativeHumidity,
    double WindSpeedMps,
    double SoilRetentionFraction)
{
    public string? Validate() => !double.IsFinite(ElapsedSeconds) || ElapsedSeconds is < 0 or > 86400
        || !Finite(RainfallL) || !Finite(EvaporationPotentialL) || !Finite(SurfaceRechargeL)
        || !Finite(ExtractionL) || !Finite(InfiltrationFraction, 0, 1)
        || !Finite(SoilRetentionFraction, 0, 1) || !Finite(RelativeHumidity, 0, 1)
        || !Finite(AmbientTemperatureC, -80, 110) || !Finite(WindSpeedMps, 0, 100)
        ? "Invalid water-cycle inputs." : null;

    private static bool Finite(double value) => double.IsFinite(value) && value >= 0;
    private static bool Finite(double value, double min, double max)
        => double.IsFinite(value) && value >= min && value <= max;
}

public readonly record struct WaterCycleStep(
    GroundwaterCell State,
    double RechargedL,
    double EvaporatedL,
    double ExtractedL,
    double RejectedExtractionL)
{
    public double NetChangeL => RechargedL - EvaporatedL - ExtractedL;
}

public static class WaterCycleModel
{
    public static WaterCycleStep Advance(GroundwaterCell previous, WaterCycleInputs inputs)
    {
        if (previous.Validate() is { } error) throw new ArgumentException(error, nameof(previous));
        if (inputs.Validate() is { } inputError) throw new ArgumentException(inputError, nameof(inputs));

        // RainfallL and surface recharge are already volume estimates for this cell.
        // Retention limits how much surface water remains available to evaporate.
        var recharge = Math.Min(previous.CapacityL - previous.StorageL,
            inputs.RainfallL * inputs.InfiltrationFraction + inputs.SurfaceRechargeL * inputs.SoilRetentionFraction);
        var available = previous.StorageL + recharge;
        var evaporation = Math.Min(available, Math.Min(inputs.EvaporationPotentialL,
            available * EvaporationMultiplier(inputs.RelativeHumidity, inputs.WindSpeedMps, inputs.AmbientTemperatureC)));
        var afterEvaporation = available - evaporation;
        var extracted = Math.Min(afterEvaporation, inputs.ExtractionL);
        var rejected = Math.Max(0, inputs.ExtractionL - extracted);
        var temperature = previous.TemperatureC + (inputs.AmbientTemperatureC - previous.TemperatureC)
            * Math.Clamp(inputs.ElapsedSeconds / 86400d, 0, 1);
        var next = previous with { StorageL = Math.Clamp(afterEvaporation - extracted, 0, previous.CapacityL), TemperatureC = temperature };
        return new(next, recharge, evaporation, extracted, rejected);
    }

    /// <summary>Derives a stable virtual aquifer from world seed and coordinates. No terrain is changed.</summary>
    public static GroundwaterCell DeriveCell(long worldSeed, int x, int y, int z, double annualRainfallIndex,
        bool coastal, double ambientTemperatureC)
    {
        var hash = unchecked((ulong)worldSeed) ^ unchecked((ulong)(x * 73856093))
            ^ unchecked((ulong)(y * 19349663)) ^ unchecked((ulong)(z * 83492791));
        hash ^= hash >> 33; hash *= 0xff51afd7ed558ccdUL; hash ^= hash >> 33;
        var porosity = 0.25 + (hash % 5000) / 10000d;
        var slopePenalty = Math.Clamp(Math.Abs(y) / 400d, 0, .65);
        var rainfall = Math.Clamp(annualRainfallIndex, 0, 1);
        var capacity = 500 + 4500 * porosity * (0.35 + 0.65 * rainfall) * (1 - slopePenalty);
        var fill = 0.15 + ((hash >> 16) % 5000) / 10000d;
        var salinity = coastal ? 2 + ((hash >> 32) % 9000) / 1000d : 0.05 + ((hash >> 32) % 300) / 1000d;
        return new(Math.Clamp(capacity * fill, 0, capacity), capacity, Math.Clamp(salinity, 0, 400),
            Math.Clamp(ambientTemperatureC, -80, 110));
    }

    private static double EvaporationMultiplier(double relativeHumidity, double wind, double temperature)
    {
        var dryness = 1 - Math.Clamp(relativeHumidity, 0, 1);
        var warmth = Math.Clamp((temperature + 10) / 40, 0, 2);
        return Math.Clamp(0.05 + dryness * (0.3 + Math.Clamp(wind, 0, 20) * 0.02) * warmth, 0, 1);
    }
}

public readonly record struct DesalinationResult(
    bool Accepted,
    WaterBatch? Product,
    WaterBatch? Brine,
    double ProcessedL,
    string? Error);

public static class WaterTreatmentModel
{
    /// <summary>
    /// Game treatment contract: sea water is never directly drinkable. A configured heat/time
    /// process yields a smaller fresh batch and a concentrated brine by-product.
    /// </summary>
    public static DesalinationResult Desalinate(WaterBatch source, double processSeconds, double heatC)
    {
        if (source.Validate() is { } error) return new(false, null, null, 0, error);
        if (!double.IsFinite(processSeconds) || processSeconds < 0 || !double.IsFinite(heatC))
            return new(false, null, null, 0, "Invalid treatment progress.");
        if (source.VolumeL <= 0 || source.SaltGramsPerL < 5 || processSeconds < 10 || heatC < 80)
            return new(false, null, null, 0, "Desalination requires salt water, heat and process time.");
        var recovery = Math.Clamp(0.35 + Math.Min(0.45, (processSeconds - 10) / 120d * 0.45), 0.35, 0.8);
        var productVolume = source.VolumeL * recovery;
        var brineVolume = source.VolumeL - productVolume;
        const double productSalt = 0.2;
        var brineSalt = Math.Clamp((source.VolumeL * source.SaltGramsPerL - productVolume * productSalt)
            / Math.Max(brineVolume, double.Epsilon), 0, 400);
        var product = source with { VolumeL = productVolume, SaltGramsPerL = 0.2,
            EntericHazardPerL = Math.Min(source.EntericHazardPerL, 0.01), Turbidity = Math.Min(source.Turbidity, 0.1),
            BaselineTurbidity = Math.Min(source.NaturalTurbidity, .1),
            Source = "desalinated", Unverified = true, TemperatureC = Math.Clamp(heatC, -80, 110) };
        var brine = source with { VolumeL = brineVolume, SaltGramsPerL = brineSalt,
            Source = "brine", Unverified = true };
        return new(true, product, brine, source.VolumeL, null);
    }
}

public readonly record struct RoomThermalInputs(
    double ClimateTemperatureC,
    double HeatSourceStrength,
    bool Enclosed,
    int CoolingWallCount,
    int NonCoolingWallCount,
    double RoomVolumeBlocks,
    double ElapsedGameHours)
{
    public string? Validate() => !double.IsFinite(ClimateTemperatureC)
        || !double.IsFinite(HeatSourceStrength) || HeatSourceStrength < 0
        || CoolingWallCount < 0 || NonCoolingWallCount < 0
        || !double.IsFinite(RoomVolumeBlocks) || RoomVolumeBlocks is <= 0 or > 100000
        || !double.IsFinite(ElapsedGameHours) || ElapsedGameHours is < 0 or > 24
        ? "Invalid room thermal inputs." : null;
}

public static class RoomTemperatureModel
{
    public static double TargetTemperature(RoomThermalInputs inputs, double maxHeatBonusC = 18, double celsiusPerStrength = 1.8)
    {
        if (inputs.Validate() is { } error) throw new ArgumentException(error, nameof(inputs));
        if (!inputs.Enclosed) return inputs.ClimateTemperatureC;
        var totalWalls = inputs.CoolingWallCount + inputs.NonCoolingWallCount;
        var retention = totalWalls == 0 ? 0.5 : Math.Clamp(inputs.NonCoolingWallCount / (double)totalWalls, 0.2, 1);
        return inputs.ClimateTemperatureC + Math.Clamp(inputs.HeatSourceStrength * celsiusPerStrength * retention, 0, maxHeatBonusC);
    }

    public static double Advance(double previousRoomC, RoomThermalInputs inputs, double halfLifeHours = 4,
        double maxHeatBonusC = 18, double celsiusPerStrength = 1.8)
    {
        if (!double.IsFinite(previousRoomC) || !double.IsFinite(halfLifeHours) || halfLifeHours <= 0)
            throw new ArgumentOutOfRangeException(nameof(previousRoomC));
        var target = TargetTemperature(inputs, maxHeatBonusC, celsiusPerStrength);
        var volumeFactor = Math.Max(1, inputs.RoomVolumeBlocks / 27d);
        var alpha = 1 - Math.Exp(-Math.Clamp(inputs.ElapsedGameHours, 0, 24) / (halfLifeHours * volumeFactor));
        return previousRoomC + (target - previousRoomC) * alpha;
    }
}

public interface ISmokeExposureProvider
{
    bool TryGetExposure(string playerUid, out SmokeExposure exposure);
}

public readonly record struct SmokeExposure(double Concentration, double OxygenDemandPerSecond, string Source)
{
    public bool IsValid => double.IsFinite(Concentration) && Concentration >= 0
        && double.IsFinite(OxygenDemandPerSecond) && OxygenDemandPerSecond >= 0
        && !string.IsNullOrWhiteSpace(Source);
}

public sealed class NoSmokeExposureProvider : ISmokeExposureProvider
{
    public bool TryGetExposure(string playerUid, out SmokeExposure exposure)
    {
        exposure = default;
        return false;
    }
}
