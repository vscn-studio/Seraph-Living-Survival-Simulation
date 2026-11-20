using Seraph.Core.State;
using Seraph.Core.Framework;

namespace Seraph.Core.Simulation;

/// <summary>Already-authorized external inputs. There is no inventory or game-state access in Core.</summary>
public sealed record StepInputs
{
    public NutritionState? Nutrition { get; init; }
    public double BowelTransitRoomG { get; init; } = 5000;
    public double ActivityPowerW { get; init; }
    public double EnergyDemandMultiplier { get; init; } = 1;
    public double HealingMultiplier { get; init; } = 1;
    public double BasalMetabolismMultiplier { get; init; } = 1;
    public double RenalSupport { get; init; }
    public bool IsResting { get; init; }
    public bool IsSleeping { get; init; }
    public double SleepQuality { get; init; } = 1;
    public double BedQuality { get; init; } = .8;
    public double SleepPressureRiseSeconds { get; init; } = 90000;
    public double SleepPressureRecoverySeconds { get; init; } = 28800;
    public double BleedLps { get; init; }
    public double HeatAdaptation { get; init; }
    public double ColdAdaptation { get; init; }
    public double FeverTemperatureRiseC { get; init; }
    public double EntericWaterLossLps { get; init; }
    public double DigestionGPerHour { get; init; } = 1667;
    public DigestionOptions Digestion { get; init; } = new();
    public double DigestionEfficiency { get; init; } = 1;
    public double WaterAbsorptionEfficiency { get; init; } = 1;
    public double MuscleProteinAvailableG { get; init; }
    public double MuscleRecoverySupport { get; init; } = 1;

    public string? Validate() =>
        Digestion is null || Digestion.Validate() is not null || Nutrition?.Validate() is not null || !BodyProfile.In(BowelTransitRoomG, 0, BowelSettlement.MaximumPendingG)
        || !BodyProfile.In(ActivityPowerW, 0, 2000) || !BodyProfile.In(SleepQuality, 0, 1)
        || !BodyProfile.In(EnergyDemandMultiplier, 0, 100) || !BodyProfile.In(HealingMultiplier, 0, 100)
        || !BodyProfile.In(BasalMetabolismMultiplier, .1, 3) || !BodyProfile.In(RenalSupport, 0, 1)
        || !BodyProfile.In(SleepPressureRiseSeconds, 60, 1e8) || !BodyProfile.In(SleepPressureRecoverySeconds, 30, 1e8)
        || !BodyProfile.In(BedQuality, 0, 1)
        || !BodyProfile.In(MuscleProteinAvailableG, 0, 10000)
        || !BodyProfile.In(MuscleRecoverySupport, 0, 1)
        || !BodyProfile.In(HeatAdaptation, 0, 1) || !BodyProfile.In(ColdAdaptation, 0, 1)
        || !BodyProfile.In(FeverTemperatureRiseC, 0, 3)
        || !BodyProfile.In(EntericWaterLossLps, 0, .01)
        || !BodyProfile.In(DigestionGPerHour, 1, 10000) || !BodyProfile.In(DigestionEfficiency, .15, 1)
        || !BodyProfile.In(WaterAbsorptionEfficiency, 0, 1)
        || !BodyProfile.In(BleedLps, 0, 1) ? "Invalid activity, sleep quality or effective blood-loss rate." : null;
}

public sealed record IntakeEvent
{
    public double WaterL { get; init; }
    public double EnergyKJ { get; init; }
    /// <summary>Food and drink electrolyte intake in milligrams of the unified pool.</summary>
    public double ElectrolytesMg { get; init; }
}

public sealed record StateChangeResult(
    bool Accepted, BodyState State, double AcceptedWaterL, double AcceptedEnergyKJ,
    double RejectedWaterL, double RejectedEnergyKJ, string? Error);

public sealed record StepResult(bool Accepted, BodyState State, SimulationDiagnostics Diagnostics, string? Error)
{
    public NutritionState? Nutrition { get; init; }
}

/// <summary>
/// Integrated actual flows. Heat is positive outward; energy stores use kJ, heat uses J.
/// Residuals compare independent before/after stores with the external ledger.
/// </summary>
public sealed record SimulationDiagnostics
{
    public double GlycogenConsumedKJ { get; init; }
    public double FatConsumedKJ { get; init; }
    public double MuscleProteinUsedG { get; init; }
    public double MuscleGrowthEnergyJ { get; init; }
    public double MuscleFuelKJ { get; init; }
    public double DietaryResidueEnergyKJ { get; init; }
    public double RepairMaterialEnergyKJ { get; init; }
    public double ConversionHeatJ { get; init; }
    /// <summary>On-demand projection of the integrated actual flows, not a second integrator.</summary>
    public FluxLedger CreateFluxLedger()
    {
        var ledger = new FluxLedger();
        void Flow(string from, string to, double amount, FluxUnit unit, string cause)
        {
            if (amount < 0) ledger.Transfer(to, from, -amount, unit, cause);
            else ledger.Transfer(from, to, amount, unit, cause);
        }
        Flow("fuel", "body-heat", MetabolicEnergyJ, FluxUnit.Joule, "metabolism");
        Flow("body-heat", "external-work", ExternalWorkJ, FluxUnit.Joule, "activity");
        Flow("body-heat", "environment", DryHeatOutJ, FluxUnit.Joule, "dry-heat");
        Flow("body-heat", "environment", RespiratoryHeatOutJ, FluxUnit.Joule, "respiration");
        Flow("body-heat", "environment", EvaporativeHeatOutJ, FluxUnit.Joule, "evaporation");
        Flow("body-heat", "environment", ContactHeatOutJ, FluxUnit.Joule, "contact");
        Flow("gut-water", "body-water", AbsorbedWaterL, FluxUnit.Litre, "absorption");
        Flow("bladder-urine", "body-water", LegacyBladderReturnL, FluxUnit.Litre, "legacy-save-migration");
        Flow("body-water", "bladder-urine", UrineWaterL, FluxUnit.Litre, "urine-production");
        Flow("body-water", "skin-film", SweatWaterL + InsensibleWaterL, FluxUnit.Litre, "skin-water");
        Flow("body-water", "environment-water", FecalWaterL + RespiratoryWaterL + BloodWaterLossL, FluxUnit.Litre, "external-loss");
        Flow("body-water", "environment-water", EntericWaterLossL, FluxUnit.Litre, "enteric-loss");
        // Model density approximation: 1 kg/L. Blood constituents are subaccounts, not extra water sinks.
        Flow("environment-water", "skin-film", RainFilmWaterKg, FluxUnit.Litre, "rain-density-approximation");
        Flow("skin-film", "environment-water", EvaporatedWaterKg + RunoffWaterKg, FluxUnit.Litre, "film-loss-density-approximation");
        Flow("gut-energy", "fuel-energy", AbsorbedEnergyKJ, FluxUnit.KiloJoule, "absorption");
        Flow("fuel-energy", "metabolism", MetabolicEnergyJ / 1000, FluxUnit.KiloJoule, "energy-use");
        return ledger;
    }
    public double AcceptedBioSeconds { get; init; }
    public int Substeps { get; init; }
    public double MetabolicEnergyJ { get; init; }
    public double ExternalWorkJ { get; init; }
    public double DryHeatOutJ { get; init; }
    public double RespiratoryHeatOutJ { get; init; }
    public double EvaporativeHeatOutJ { get; init; }
    public double ContactHeatOutJ { get; init; }
    public double CoreToSkinHeatJ { get; init; }
    public double BasalEnergyJ { get; init; }
    public double ShiveringEnergyJ { get; init; }
    public double ActivityEnergyJ { get; init; }
    public double UnmetMetabolicEnergyJ { get; init; }
    public double AbsorbedWaterL { get; init; }
    public double LegacyBladderReturnL { get; init; }
    public double UrineWaterL { get; init; }
    public double AbsorbedEnergyKJ { get; init; }
    public double SweatWaterL { get; init; }
    public double RespiratoryWaterL { get; init; }
    public double InsensibleWaterL { get; init; }
    public double FecalWaterL { get; init; }
    public double EntericWaterLossL { get; init; }
    public double WholeBloodLossL { get; init; }
    public double BloodWaterLossL { get; init; }
    public double RbcLossL { get; init; }
    public double PlasmaWaterLossL { get; init; }
    public double RainFilmWaterKg { get; init; }
    public double EvaporatedWaterKg { get; init; }
    public double RunoffWaterKg { get; init; }
    public double HeatBalanceResidualJ { get; init; }
    public double BodyWaterBalanceResidualL { get; init; }
    public double TotalWaterBalanceResidualL { get; init; }
    public double FuelBalanceResidualKJ { get; init; }
    public bool VaporPressureOutsideFitDomain { get; init; }
    public bool ResourceLimited { get; init; }
}
