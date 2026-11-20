using Seraph.Core.Framework;
namespace Seraph.Core.State;

/// <summary>Fixed reference values and calibration seeds, not clinical reference intervals.</summary>
public sealed record BodyProfile
{
    public static BodyProfile Default { get; } = new();

    public double MassKg { get; init; } = 70;
    public double SurfaceAreaM2 { get; init; } = 1.8;
    public double SpecificHeatJPerKgK { get; init; } = 3500;
    public double PeripheralHeatCapacityFraction { get; init; } = 0.10;
    public double ReferenceCoreTemperatureC { get; init; } = 37;
    public double ReferenceSkinTemperatureC { get; init; } = 33;
    public double ReferenceBodyWaterL { get; init; } = 42;
    public double ReferencePlasmaWaterL { get; init; } = 2.53;
    public double ReferenceRbcVolumeL { get; init; } = 2.25;
    public double PlasmaWaterFraction { get; init; } = 0.92;
    public double RbcWaterFraction { get; init; } = 0.70;
    // Match BP's explicit glycogen reserve in grams; gameplay energy is stored in kJ.
    public double InitialGlycogenEnergyKJ { get; init; } = 525 * DietaryMetabolism.CarbKJPerG;
    public double GlycogenCapacityKJ { get; init; } = 750 * DietaryMetabolism.CarbKJPerG;
    public double InitialFatEnergyKJ { get; init; } = 12000 * DietaryMetabolism.FatKJPerG;
    public double FatCapacityKJ { get; init; } = 100000 * DietaryMetabolism.FatKJPerG;
    public double InitialMuscleMassG { get; init; } = 17500;
    public double MuscleMassMinG { get; init; } = 0;
    public double MuscleMassMaxG { get; init; } = 50000;
    public double BasalMetabolicPowerW { get; init; } = 70 * 4184d / 3600;
    public double MaximumBodyWaterL { get; init; } = 60;
    public double MaximumRbcVolumeL { get; init; } = 4;
    public double MaximumGutWaterL { get; init; } = 5;
    public double MaximumGutEnergyKJ { get; init; } = 250000;
    public double MaximumSkinFilmWaterKg { get; init; } = 2;

    public double TotalHeatCapacityJPerK => MassKg * SpecificHeatJPerKgK;
    public double CoreHeatCapacityJPerK => TotalHeatCapacityJPerK * (1 - PeripheralHeatCapacityFraction);
    public double SkinHeatCapacityJPerK => TotalHeatCapacityJPerK * PeripheralHeatCapacityFraction;
    public double ReferenceBloodVolumeL => ReferencePlasmaWaterL / PlasmaWaterFraction + ReferenceRbcVolumeL;

    public string? Validate()
    {
        if (!In(MassKg, 1, 500) || !In(SurfaceAreaM2, 0.1, 10)
            || !In(SpecificHeatJPerKgK, 100, 10000)
            || !In(PeripheralHeatCapacityFraction, 0.01, 0.99))
            return "Invalid mass, area or heat capacity.";
        if (!In(ReferenceCoreTemperatureC, -80, 80) || !In(ReferenceSkinTemperatureC, -80, 80)
            || !In(PlasmaWaterFraction, 0.01, 1) || !In(RbcWaterFraction, 0, 1))
            return "Invalid reference temperature or constituent fraction.";
        if (!In(MaximumBodyWaterL, 0.01, 500) || !In(MaximumRbcVolumeL, 0.01, 50)
            || !In(MaximumGutWaterL, 0.01, 50) || !In(MaximumGutEnergyKJ, 0.01, 1000000)
            || !In(MaximumSkinFilmWaterKg, 0.01, 50)
            || !In(GlycogenCapacityKJ, 0.01, 1000000) || !In(FatCapacityKJ, 0.01, 100000000)
            || !In(MuscleMassMinG, 0, 100000) || !In(MuscleMassMaxG, MuscleMassMinG, 200000))
            return "Invalid storage capacity.";
        if (!In(ReferenceBodyWaterL, 0.01, MaximumBodyWaterL)
            || !In(ReferencePlasmaWaterL, 0, ReferenceBodyWaterL)
            || !In(ReferenceRbcVolumeL, 0.01, MaximumRbcVolumeL)
            || ReferencePlasmaWaterL + RbcWaterFraction * ReferenceRbcVolumeL > ReferenceBodyWaterL
            || ReferenceBodyWaterL - RbcWaterFraction * ReferenceRbcVolumeL <= 0)
            return "Reference fluid compartments are inconsistent.";
        if (!In(InitialGlycogenEnergyKJ, 0, GlycogenCapacityKJ)
            || !In(InitialFatEnergyKJ, 0, FatCapacityKJ)
            || !In(InitialMuscleMassG, MuscleMassMinG, MuscleMassMaxG)
            || !In(BasalMetabolicPowerW, 0, 2000))
            return "Invalid initial fuel stores or basal demand.";
        return null;
    }

    internal static bool In(double value, double minimum, double maximum) =>
        double.IsFinite(value) && value >= minimum && value <= maximum;
}
