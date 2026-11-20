namespace Seraph.Core.Framework;

/// <summary>Coarse material classes used by the virtual aquifer. Porosity values are game model inputs, not field measurements.</summary>
public enum AquiferMaterialClass
{
    Clay,
    Silt,
    FineSand,
    CoarseSand,
    Gravel,
    FracturedRock
}

/// <summary>Gameplay classification derived from dissolved salt concentration.</summary>
public enum GroundwaterType
{
    Fresh,
    Brackish,
    Saline
}

public readonly record struct AquiferMaterialProperties(double DrainablePorosity, double HydraulicConductivityMPerDay)
{
    public string? Validate() => double.IsFinite(DrainablePorosity) && DrainablePorosity is >= 0.01 and <= 0.30
        && double.IsFinite(HydraulicConductivityMPerDay) && HydraulicConductivityMPerDay > 0 ? null : "Invalid aquifer material properties.";
}

public static class AquiferMaterials
{
    /// <summary>Reference intervals for initial gameplay calibration; drainable porosity is not total porosity.</summary>
    public static AquiferMaterialProperties Properties(AquiferMaterialClass material) => material switch
    {
        AquiferMaterialClass.Clay => new(0.03, 0.01),
        AquiferMaterialClass.Silt => new(0.065, 0.08),
        AquiferMaterialClass.FineSand => new(0.15, 1.0),
        AquiferMaterialClass.CoarseSand => new(0.25, 8.0),
        AquiferMaterialClass.Gravel => new(0.25, 30.0),
        AquiferMaterialClass.FracturedRock => new(0.08, 2.5),
        _ => throw new ArgumentOutOfRangeException(nameof(material))
    };
}

/// <summary>A virtual aquifer cell. It never creates, removes or changes a world block.</summary>
public readonly record struct GroundwaterState(
    AquiferMaterialClass Material,
    double StorageL,
    double CapacityL,
    double WaterTableDepthM,
    double FlowLPerDay,
    double SalinityGPerL,
    double TemperatureC,
    double RechargeL,
    double DischargeL,
    long LastStepSeconds)
{
    /// <summary>Equivalent saturated thickness used by the game model, inferred from capacity.</summary>
    public double ThicknessM
    {
        get
        {
            var porosity = AquiferMaterials.Properties(Material).DrainablePorosity;
            return Math.Clamp(CapacityL / (256d * 1000d * porosity), 1, 200);
        }
    }

    public GroundwaterType Type => SalinityGPerL switch
    {
        < 0.5 => GroundwaterType.Fresh,
        < 30 => GroundwaterType.Brackish,
        _ => GroundwaterType.Saline
    };

    public string? Validate()
    {
        var material = AquiferMaterials.Properties(Material);
        return material.Validate() is not null || !Finite(StorageL, 0, 1e12) || !Finite(CapacityL, 1, 1e12)
            || StorageL > CapacityL || !Finite(WaterTableDepthM, 0, ThicknessM) || !Finite(FlowLPerDay, 0, 1e12)
            || !Finite(SalinityGPerL, 0, 400) || !Finite(TemperatureC, -80, 110)
            || !Finite(RechargeL, 0, 1e12) || !Finite(DischargeL, 0, 1e12) || LastStepSeconds < 0
            ? "Invalid groundwater state." : null;
    }

    private static bool Finite(double value, double min, double max)
        => double.IsFinite(value) && value >= min && value <= max;
}

public readonly record struct GroundwaterInputs(
    double ElapsedSeconds,
    double RainRechargeL,
    double RiverRechargeL,
    double EvaporationPotentialL,
    double SpringDischargeL,
    double ExtractionL,
    double AmbientTemperatureC,
    double RelativeHumidity,
    double NeighborWaterTableDepthM,
    double NeighborDistanceM)
{
    public string? Validate() => !double.IsFinite(ElapsedSeconds) || ElapsedSeconds is < 0 or > 86400
        || !NonNegative(RainRechargeL) || !NonNegative(RiverRechargeL) || !NonNegative(EvaporationPotentialL)
        || !NonNegative(SpringDischargeL) || !NonNegative(ExtractionL) || !double.IsFinite(AmbientTemperatureC)
        || !double.IsFinite(RelativeHumidity) || RelativeHumidity is < 0 or > 1
        || !double.IsFinite(NeighborWaterTableDepthM) || NeighborWaterTableDepthM is < 0 or > 1000
        || !double.IsFinite(NeighborDistanceM) || NeighborDistanceM is <= 0 or > 10000
        ? "Invalid groundwater inputs." : null;

    private static bool NonNegative(double value) => double.IsFinite(value) && value >= 0;
}

public readonly record struct GroundwaterStep(
    GroundwaterState State,
    double RechargedL,
    double EvaporatedL,
    double DischargedL,
    double ExtractedL,
    double RejectedExtractionL)
{
    public double NetLocalChangeL => RechargedL - EvaporatedL - DischargedL - ExtractedL;
}

public readonly record struct GroundwaterTransfer(GroundwaterState A, GroundwaterState B, double AToBL, double BToAL);

public static class GroundwaterHydrologyModel
{
    public static GroundwaterState Derive(long worldSeed, int regionX, int regionZ, double surfaceElevationM,
        double rainfallIndex, double climateTemperatureC, AquiferMaterialClass material, double thicknessM = 12)
    {
        if (!double.IsFinite(surfaceElevationM) || !double.IsFinite(rainfallIndex) || rainfallIndex is < 0 or > 1)
            throw new ArgumentOutOfRangeException(nameof(surfaceElevationM));
        if (!double.IsFinite(thicknessM) || thicknessM is <= 0 or > 200) throw new ArgumentOutOfRangeException(nameof(thicknessM));
        var hash = Hash(worldSeed, regionX, regionZ);
        var properties = AquiferMaterials.Properties(material);
        var areaM2 = 256d;
        var capacity = Math.Clamp(areaM2 * thicknessM * properties.DrainablePorosity * 1000d, 1, 1e12);
        var fill = 0.25 + ((hash >> 12) % 5000) / 10000d;
        var storage = capacity * Math.Clamp(fill * (0.65 + rainfallIndex * 0.35), 0.05, 0.98);
        var depth = Math.Clamp(thicknessM * (1 - storage / capacity), 0, thicknessM);
        var coastalSalinity = surfaceElevationM < 0 ? 10d : 0.1d;
        var salinity = coastalSalinity + ((hash >> 32) % 1000) / 1000d;
        return new(material, storage, capacity, depth, 0, salinity,
            Math.Clamp(climateTemperatureC, -80, 110), 0, 0, 0);
    }

    public static GroundwaterStep Advance(GroundwaterState previous, GroundwaterInputs inputs)
    {
        if (previous.Validate() is { } error) throw new ArgumentException(error, nameof(previous));
        if (inputs.Validate() is { } inputError) throw new ArgumentException(inputError, nameof(inputs));
        var properties = AquiferMaterials.Properties(previous.Material);
        var recharge = Math.Min(previous.CapacityL - previous.StorageL, inputs.RainRechargeL + inputs.RiverRechargeL);
        var available = previous.StorageL + recharge;
        var dryness = 1 - inputs.RelativeHumidity;
        var warmth = Math.Clamp((inputs.AmbientTemperatureC + 5) / 40, 0, 2);
        var evaporation = Math.Min(available, Math.Min(inputs.EvaporationPotentialL,
            available * Math.Clamp(0.01 + dryness * warmth * 0.08, 0, 1)));
        var afterEvaporation = available - evaporation;
        var discharged = Math.Min(afterEvaporation, inputs.SpringDischargeL);
        var afterDischarge = afterEvaporation - discharged;
        var extracted = Math.Min(afterDischarge, inputs.ExtractionL);
        var rejected = Math.Max(0, inputs.ExtractionL - extracted);
        var depthTarget = Math.Clamp((1 - afterDischarge / previous.CapacityL) * previous.ThicknessM, 0, previous.ThicknessM);
        var alpha = inputs.ElapsedSeconds <= 0 && (inputs.RainRechargeL > 0 || inputs.RiverRechargeL > 0
            || inputs.SpringDischargeL > 0 || inputs.ExtractionL > 0)
            ? 1 : Math.Clamp(inputs.ElapsedSeconds / 86400d, 0, 1);
        var depth = previous.WaterTableDepthM + (depthTarget - previous.WaterTableDepthM) * alpha;
        var temperature = previous.TemperatureC + (inputs.AmbientTemperatureC - previous.TemperatureC)
            * Math.Clamp(inputs.ElapsedSeconds / 172800d, 0, 1);
        var flow = properties.HydraulicConductivityMPerDay
            * Math.Abs(inputs.NeighborWaterTableDepthM - depth) / inputs.NeighborDistanceM * 10;
        var state = previous with { StorageL = Math.Clamp(afterDischarge - extracted, 0, previous.CapacityL),
            WaterTableDepthM = depth, FlowLPerDay = Math.Clamp(flow, 0, 1e12), TemperatureC = temperature,
            RechargeL = recharge, DischargeL = discharged, LastStepSeconds = checked(previous.LastStepSeconds + (long)inputs.ElapsedSeconds) };
        return new(state, recharge, evaporation, discharged, extracted, rejected);
    }

    /// <summary>Moves water between two loaded cells using a bounded depth gradient. The pair is updated atomically.</summary>
    public static GroundwaterTransfer Exchange(GroundwaterState a, GroundwaterState b, double elapsedSeconds, double distanceM)
    {
        if (a.Validate() is { } aError) throw new ArgumentException(aError, nameof(a));
        if (b.Validate() is { } bError) throw new ArgumentException(bError, nameof(b));
        if (!double.IsFinite(elapsedSeconds) || elapsedSeconds is < 0 or > 86400) throw new ArgumentOutOfRangeException(nameof(elapsedSeconds));
        if (!double.IsFinite(distanceM) || distanceM <= 0) throw new ArgumentOutOfRangeException(nameof(distanceM));
        var headDifference = b.WaterTableDepthM - a.WaterTableDepthM;
        var conductivity = Math.Min(AquiferMaterials.Properties(a.Material).HydraulicConductivityMPerDay,
            AquiferMaterials.Properties(b.Material).HydraulicConductivityMPerDay);
        var requested = Math.Abs(headDifference) / distanceM * conductivity * elapsedSeconds / 86400d * 256;
        var limit = Math.Min(a.CapacityL, b.CapacityL) * 0.2;
        var amount = Math.Min(Math.Max(0, requested), limit);
        // Water moves from the shallower water table (higher hydraulic head) to the deeper one.
        // A positive depth difference therefore transfers A -> B, not B -> A.
        if (headDifference > 0)
        {
            amount = Math.Min(amount, Math.Min(a.StorageL, b.CapacityL - b.StorageL));
            var aStorage = a.StorageL - amount;
            var bStorage = b.StorageL + amount;
            return new(a with { StorageL = aStorage, WaterTableDepthM = DepthFor(a, aStorage), FlowLPerDay = amount * 86400d / Math.Max(elapsedSeconds, 1) },
                b with { StorageL = bStorage, WaterTableDepthM = DepthFor(b, bStorage), FlowLPerDay = amount * 86400d / Math.Max(elapsedSeconds, 1) }, amount, 0);
        }
        amount = Math.Min(amount, Math.Min(b.StorageL, a.CapacityL - a.StorageL));
        var aStorageReverse = a.StorageL + amount;
        var bStorageReverse = b.StorageL - amount;
        return new(a with { StorageL = aStorageReverse, WaterTableDepthM = DepthFor(a, aStorageReverse), FlowLPerDay = amount * 86400d / Math.Max(elapsedSeconds, 1) },
            b with { StorageL = bStorageReverse, WaterTableDepthM = DepthFor(b, bStorageReverse), FlowLPerDay = amount * 86400d / Math.Max(elapsedSeconds, 1) }, 0, amount);
    }

    private static double DepthFor(GroundwaterState state, double storage)
        => Math.Clamp((1 - storage / state.CapacityL) * state.ThicknessM, 0, state.ThicknessM);

    private static ulong Hash(long seed, int x, int z)
    {
        var hash = unchecked((ulong)seed) ^ unchecked((ulong)(x * 73856093)) ^ unchecked((ulong)(z * 19349663));
        hash ^= hash >> 33; hash *= 0xff51afd7ed558ccdUL; hash ^= hash >> 33;
        return hash;
    }
}
