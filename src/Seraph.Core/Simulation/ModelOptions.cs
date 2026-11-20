using Seraph.Core.State;

namespace Seraph.Core.Simulation;

/// <summary>P1 calibration and explicit model switches. These are not medical prescriptions.</summary>
public sealed record ModelOptions
{
    public double BasalWaterLossGPerGameHour { get; init; } = 75;
    public double ThermalResponseTimeScale { get; init; } = 1;
    public double MaximumMuscleGainGPerDay { get; init; } = 100;
    // The pure core stays neutral; the game adapter supplies its configured rate.
    public double UrineProductionMlPerGameHour { get; init; }
    public double MaximumSubstepBioSeconds { get; init; } = 0.25;
    public int MaximumSubstepsPerAdvance { get; init; } = 32;
    public double MaximumAdvanceBioSeconds { get; init; } = 8;
    public bool EnableDryHeatExchange { get; init; } = true;
    public bool EnableEvaporation { get; init; } = true;
    public bool EnableSweating { get; init; } = true;
    public bool EnableRespiration { get; init; } = true;
    public bool EnableInsensibleWaterLoss { get; init; } = true;
    public bool EnableFecalWaterLoss { get; init; } = true;
    public bool EnablePlasmaRedistribution { get; init; } = true;
    public double InsensibleWaterLossLPerSecond { get; init; } = 6e-6;
    // All excretory fluid is accounted for with feces in the gameplay abstraction.
    public double FecalWaterLossLPerSecond { get; init; } = 1.7e-5;
    public double MinimumFecalWaterLossLPerSecond { get; init; } = 3e-6;
    // About 0.75 L per game hour at the configured heat-error ceiling.
    public double MaximumSweatLPerSecond { get; init; } = 0.00020833333333333334;
    public double MaximumShiveringPowerW { get; init; } = 300;
    public double ExternalWorkFraction { get; init; } = 0.20;

    public string? Validate()
    {
        if (!BodyProfile.In(ThermalResponseTimeScale, 1, 1000) || !BodyProfile.In(MaximumMuscleGainGPerDay, 0, 1000)
            || !BodyProfile.In(UrineProductionMlPerGameHour, 0, 1000)) return "Invalid calendar calibration.";
        if (!BodyProfile.In(MaximumSubstepBioSeconds, 0.001, 0.25)
            || MaximumSubstepsPerAdvance is < 1 or > 1024
            || !BodyProfile.In(MaximumAdvanceBioSeconds, 0.001, 8)
            || MaximumAdvanceBioSeconds > MaximumSubstepBioSeconds * MaximumSubstepsPerAdvance + 1e-12)
            return "Invalid bounded integration limits.";
        if (!BodyProfile.In(BasalWaterLossGPerGameHour, 0, 10000)
            || !BodyProfile.In(InsensibleWaterLossLPerSecond, 0, 0.01)
            || !BodyProfile.In(FecalWaterLossLPerSecond, 0, 0.01)
            || !BodyProfile.In(MinimumFecalWaterLossLPerSecond, 0, FecalWaterLossLPerSecond)
            || !BodyProfile.In(MaximumSweatLPerSecond, 0, 0.01)
            || !BodyProfile.In(MaximumShiveringPowerW, 0, 2000)
            || !BodyProfile.In(ExternalWorkFraction, 0, 1))
            return "Invalid model rates or work efficiency.";
        return null;
    }
}
