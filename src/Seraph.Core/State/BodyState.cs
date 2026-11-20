namespace Seraph.Core.State;

/// <summary>
/// Immutable shadow state. Body water includes plasma and red-cell water;
/// gut and surface water are separate stores. Protection bounds are numerical, not diagnostic.
/// </summary>
public sealed record BodyState
{
    public double CoreTemperatureC { get; init; }
    public double SkinTemperatureC { get; init; }
    public double BodyWaterL { get; init; }
    /// <summary>Plasma water is a subaccount of BodyWaterL, not an additional store.</summary>
    public double PlasmaWaterL { get; init; }
    public double RbcVolumeL { get; init; }
    public double GutWaterL { get; init; }
    public double BladderUrineL { get; init; }
    public double RenalFiltrationMlPerGameHour { get; init; }
    public double UrineProductionMlPerGameHour { get; init; }
    public double SkinFilmWaterKg { get; init; }
    public double GlycogenEnergyKJ { get; init; }
    public double FatEnergyKJ { get; init; }
    public double MuscleMassG { get; init; }
    public double MuscleTrainingStimulus { get; init; }
    /// <summary>0.5 is neutral; sweat and illness move this toward depletion.</summary>
    public double ElectrolyteBalance { get; init; }
    public double SweatDebtL { get; init; }
    public double GutEnergyKJ { get; init; }
    public double SleepPressure { get; init; }
    public double ElapsedBioSeconds { get; init; }

    public static BodyState Create(BodyProfile? profile = null)
    {
        profile ??= BodyProfile.Default;
        var error = profile.Validate();
        if (error is not null) throw new ArgumentException(error, nameof(profile));
        return new BodyState
        {
            CoreTemperatureC = profile.ReferenceCoreTemperatureC,
            SkinTemperatureC = profile.ReferenceSkinTemperatureC,
            BodyWaterL = profile.ReferenceBodyWaterL,
            PlasmaWaterL = profile.ReferencePlasmaWaterL,
            RbcVolumeL = profile.ReferenceRbcVolumeL,
            GlycogenEnergyKJ = profile.InitialGlycogenEnergyKJ,
            FatEnergyKJ = profile.InitialFatEnergyKJ,
            MuscleMassG = profile.InitialMuscleMassG,
            ElectrolyteBalance = .5,
            SleepPressure = 0.3
        };
    }

    public double BloodVolumeL(BodyProfile profile) => PlasmaWaterL / profile.PlasmaWaterFraction + RbcVolumeL;

    public string? Validate(BodyProfile profile)
    {
        ArgumentNullException.ThrowIfNull(profile);
        var profileError = profile.Validate();
        if (profileError is not null) return "Invalid body profile: " + profileError;
        if (!BodyProfile.In(CoreTemperatureC, -80, 80) || !BodyProfile.In(SkinTemperatureC, -80, 80))
            return "Temperature exceeds finite numerical protection bounds (-80..80 C).";
        if (!BodyProfile.In(BodyWaterL, 0, profile.MaximumBodyWaterL)
            || !BodyProfile.In(PlasmaWaterL, 0, BodyWaterL)
            || !BodyProfile.In(RbcVolumeL, 0, profile.MaximumRbcVolumeL)
            || PlasmaWaterL + profile.RbcWaterFraction * RbcVolumeL > BodyWaterL + 1e-9)
            return "Invalid or inconsistent fluid compartments.";
        if (!BodyProfile.In(GutWaterL, 0, profile.MaximumGutWaterL)
            || !BodyProfile.In(BladderUrineL, 0, BladderCapacityL)
            || !BodyProfile.In(RenalFiltrationMlPerGameHour, 0, 1e7)
            || !BodyProfile.In(UrineProductionMlPerGameHour, 0, 1e7)
            || !BodyProfile.In(SkinFilmWaterKg, 0, profile.MaximumSkinFilmWaterKg)
            || !BodyProfile.In(GutEnergyKJ, 0, profile.MaximumGutEnergyKJ)
            || !BodyProfile.In(GlycogenEnergyKJ, 0, profile.GlycogenCapacityKJ)
            || !BodyProfile.In(FatEnergyKJ, 0, profile.FatCapacityKJ)
            || !BodyProfile.In(MuscleMassG, profile.MuscleMassMinG, profile.MuscleMassMaxG)
            || !BodyProfile.In(MuscleTrainingStimulus, 0, 1)
            || !BodyProfile.In(ElectrolyteBalance, 0, 1)
            || !BodyProfile.In(SweatDebtL, 0, profile.MaximumGutWaterL))
            return "Invalid or over-capacity water/fuel store.";
        if (!BodyProfile.In(SleepPressure, 0, 1) || !BodyProfile.In(ElapsedBioSeconds, 0, 1e15))
            return "Invalid sleep pressure or physiological elapsed time.";
        return null;
    }

    public const double BladderCapacityL = 1.5;

    public (BodyState State, double UrineL) Urinate(double fraction = 1)
    {
        if (!double.IsFinite(fraction) || fraction is <= 0 or > 1) return (this, 0);
        var urine = BladderUrineL * fraction;
        return (this with { BladderUrineL = BladderUrineL - urine }, urine);
    }

    public BodyState AddUrine(double litres)
    {
        if (!double.IsFinite(litres) || litres <= 0) return this;
        return this with { BladderUrineL = Math.Min(BladderCapacityL, BladderUrineL + litres) };
    }

}
