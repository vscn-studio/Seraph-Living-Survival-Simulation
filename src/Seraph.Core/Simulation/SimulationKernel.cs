using Seraph.Core.State;
using Seraph.Core.Framework;

namespace Seraph.Core.Simulation;

/// <summary>
/// Deterministic pure shadow transition. No game objects, clocks, random generators or side effects.
/// Every invalid transition returns the original snapshot. This prototype provides no medical diagnosis.
/// </summary>
public sealed class SimulationKernel
{
    private const double LatentHeatJPerKg = 2.4e6;
    private const double StefanBoltzmann = 5.670374419e-8;
    // BP settles food while asleep at one quarter of the awake rate.
    private const double SleepDigestionEfficiency = .25;
    private readonly BodyProfile profile;
    private readonly ModelOptions options;

    public SimulationKernel(BodyProfile profile, ModelOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(profile);
        var error = profile.Validate();
        if (error is not null) throw new ArgumentException(error, nameof(profile));
        this.profile = profile;
        this.options = options ?? new ModelOptions();
        error = this.options.Validate();
        if (error is not null) throw new ArgumentException(error, nameof(options));
    }

    public string? ValidateState(BodyState state) => state.Validate(profile);

    /// <summary>Read-only heat-boundary estimate, using the same solver as settlement.</summary>
    public double? DryHeatPowerW(double skinC, EnvironmentSample environment)
    {
        if (!double.IsFinite(skinC) || skinC is < -80 or > 100 || environment is null || environment.Validate() is not null)
            return null;
        var hc = 4 + 8 * Math.Sqrt(environment.WindSpeedMps);
        return TryDryHeat(skinC, environment, hc, out var watts) ? watts : null;
    }

    /// <summary>Equivalent still-air temperature at reference skin temperature and current clothing.
    /// Matches dry and contact heat exchange, not sweat, humidity discomfort or core temperature.</summary>
    public double? EquivalentTemperatureC(EnvironmentSample environment)
    {
        if (!options.EnableDryHeatExchange) return null;
        var skin = profile.ReferenceSkinTemperatureC;
        var dry = DryHeatPowerW(skin, environment);
        if (dry is null) return null;
        var target = dry.Value + environment.ContactConductanceWPerK * (skin - environment.ContactTemperatureC);
        double? At(double temperature) => DryHeatPowerW(skin, environment with {
            AirTemperatureC = temperature, RadiantTemperatureC = temperature, WindSpeedMps = 0,
            AbsorbedSolarPowerW = 0, ContactConductanceWPerK = 0, WaterImmersionFraction = 0 });
        var low = -80d; var high = 100d;
        if (At(low) is not { } cold || At(high) is not { } hot) return null;
        // Strong water contact can exceed the dry-exchange range represented by
        // the UI. Keep the estimate useful at the finite display boundary.
        if (target > cold) return low;
        if (target < hot) return high;
        for (var i = 0; i < 24; i++)
        {
            var middle = (low + high) / 2;
            if (At(middle) is not { } heat) return null;
            if (Math.Abs(heat - target) < .02) return middle;
            if (heat > target) low = middle; else high = middle;
        }
        return (low + high) / 2;
    }

    public StepResult Advance(BodyState previous, EnvironmentSample environment, StepInputs inputs, double bioSeconds)
    {
        ArgumentNullException.ThrowIfNull(previous);
        var error = previous.Validate(profile);
        if (error is not null) return Rejected(previous, "Invalid previous state: " + error);
        if (environment is null || inputs is null) return Rejected(previous, "Missing explicit step input.");
        error = environment.Validate() ?? inputs.Validate();
        if (error is not null) return Rejected(previous, error);
        if (!double.IsFinite(bioSeconds) || bioSeconds < 0 || bioSeconds > options.MaximumAdvanceBioSeconds)
            return Rejected(previous, "Physiological elapsed time is invalid or exceeds the bounded advance limit.");
        if (bioSeconds == 0) return new(true, previous, new SimulationDiagnostics(), null);
        var count = (int)Math.Ceiling(bioSeconds / options.MaximumSubstepBioSeconds);
        if (count > options.MaximumSubstepsPerAdvance)
            return Rejected(previous, "Advance would exceed the substep budget.");

        var dt = bioSeconds / count;
        var ledger = new Ledger();
        var state = previous;
        var nutrition = inputs.Nutrition;
        for (var index = 0; index < count; index++)
        {
            var candidate = Integrate(state, environment, inputs, dt, ledger, ref nutrition);
            if (candidate is null) return Rejected(previous, "Clothing heat boundary could not be solved within its finite bracket.");
            error = candidate.Validate(profile);
            if (error is not null) return Rejected(previous, "Rejected candidate: " + error);
            state = candidate;
        }
        return new(true, state, ledger.Finish(previous, state, profile, bioSeconds, count, options.ThermalResponseTimeScale), null)
        {
            // Nutrition is an explicit part of the simulation snapshot. Returning
            // the settled copy keeps callers on the same immutable transition as
            // the body state and avoids silently falling back to a second digest.
            Nutrition = nutrition
        };
    }

    /// <summary>Places intake into gut queues only. Rejected excess is returned; no game item is consumed.</summary>
    public StateChangeResult ApplyIntake(BodyState previous, IntakeEvent intake)
    {
        ArgumentNullException.ThrowIfNull(previous);
        var error = previous.Validate(profile);
        if (error is not null) return new(false, previous, 0, 0, 0, 0, "Invalid previous state: " + error);
        if (intake is null || !double.IsFinite(intake.WaterL) || intake.WaterL < 0
            || !double.IsFinite(intake.EnergyKJ) || intake.EnergyKJ < 0
            || !double.IsFinite(intake.ElectrolytesMg) || intake.ElectrolytesMg < 0)
            return new(false, previous, 0, 0, 0, 0, "Invalid external intake.");
        var acceptedWater = Math.Min(intake.WaterL, profile.MaximumGutWaterL - previous.GutWaterL);
        var acceptedEnergy = Math.Min(intake.EnergyKJ, profile.MaximumGutEnergyKJ - previous.GutEnergyKJ);

        var next = previous with
        {
            GutWaterL = Math.Min(profile.MaximumGutWaterL, previous.GutWaterL + acceptedWater),
            GutEnergyKJ = Math.Min(profile.MaximumGutEnergyKJ, previous.GutEnergyKJ + acceptedEnergy),
            ElectrolyteBalance = previous.ElectrolyteBalance
        };
        return new(true, next, acceptedWater, acceptedEnergy,
            intake.WaterL - acceptedWater, intake.EnergyKJ - acceptedEnergy, null);
    }

    private BodyState? Integrate(BodyState old, EnvironmentSample environment, StepInputs inputs, double dt, Ledger ledger, ref NutritionState? nutrition)
    {
        // Legacy saves stored a separate bladder reserve. Return that water to the
        // available reserve during migration; the urine system is no longer simulated.
        var bodyWater = old.BodyWaterL + old.BladderUrineL;
        ledger.LegacyBladderReturnL += old.BladderUrineL;
        const double bladderUrine = 0;
        var plasmaWater = old.PlasmaWaterL;
        var rbc = old.RbcVolumeL;
        var gutWater = old.GutWaterL;
        var gutEnergy = old.GutEnergyKJ;
        var glycogen = old.GlycogenEnergyKJ;
        var fat = old.FatEnergyKJ;
        var muscle = old.MuscleMassG;
        var electrolyte = old.ElectrolyteBalance;

        // Bare Core callers and pre-composition energy queues use the same settlement.
        // No physical mass is invented for an energy-only legacy balance.
        var diet = nutrition ?? new NutritionState();
        var digestion = DietaryMetabolism.Digest(diet, old, profile, dt,
            inputs.DigestionEfficiency * (inputs.IsSleeping ? SleepDigestionEfficiency : 1),
            Math.Max(0, inputs.BowelTransitRoomG - (diet.BowelProducedG - (inputs.Nutrition?.BowelProducedG ?? 0))),
            inputs.DigestionGPerHour, inputs.Digestion, inputs.WaterAbsorptionEfficiency);
        if (nutrition is not null) nutrition = digestion.Nutrition;
        gutWater = Math.Max(0, gutWater - digestion.WaterL); bodyWater += digestion.WaterL;
        gutEnergy = Math.Max(0, gutEnergy - digestion.RemovedEnergyKJ);
        ledger.AbsorbedWaterL += digestion.WaterL;
        ledger.AbsorbedEnergyKJ += digestion.CarbohydrateKJ + digestion.FatKJ;
        ledger.RepairMaterialEnergyKJ += digestion.ProteinKJ;
        var conversionHeatJ = DietaryMetabolism.Deposit(ref glycogen, ref fat, profile, digestion.CarbohydrateKJ, digestion.FatKJ) * 1000;
        ledger.ConversionHeatJ += conversionHeatJ;

        // Whole-blood loss removes each constituent once; body water already includes blood water.
        var bloodVolume = plasmaWater / profile.PlasmaWaterFraction + rbc;
        if (bloodVolume > 0 && inputs.BleedLps > 0)
        {
            var wholeBloodLoss = Math.Min(inputs.BleedLps * dt, bloodVolume);
            var hematocrit = rbc / bloodVolume;
            var remainingRbc = Math.Max(0, rbc - hematocrit * wholeBloodLoss);
            var remainingPlasmaWater = Math.Max(0, plasmaWater
                - profile.PlasmaWaterFraction * (1 - hematocrit) * wholeBloodLoss);
            var lostRbc = rbc - remainingRbc;
            var lostPlasmaWater = plasmaWater - remainingPlasmaWater;
            var lostBodyWater = lostPlasmaWater + profile.RbcWaterFraction * lostRbc;
            rbc = remainingRbc;
            plasmaWater = remainingPlasmaWater;
            bodyWater -= lostBodyWater;
            ledger.WholeBloodLossL += wholeBloodLoss;
            ledger.BloodWaterLossL += lostBodyWater;
            ledger.RbcLossL += lostRbc;
            ledger.PlasmaWaterLossL += lostPlasmaWater;
        }

        if (options.EnablePlasmaRedistribution)
        {
            var nonRbcWater = Math.Max(0, bodyWater - profile.RbcWaterFraction * rbc);
            var nonVascularWater = Math.Max(0, nonRbcWater - plasmaWater);
            var stage = Hydration.Stage(ReserveDisplay.HydrationCapacityG + (bodyWater - profile.ReferenceBodyWaterL) * 1000);
            var target = profile.ReferencePlasmaWaterL * Hydration.BloodMaximumFactor(stage);
            // Beyond an empty usable reserve, protect the physically accessible compartments.
            if (stage == 5) target = Math.Min(target, profile.ReferencePlasmaWaterL * nonRbcWater
                / (profile.ReferenceBodyWaterL - profile.RbcWaterFraction * profile.ReferenceRbcVolumeL));
            target = Math.Clamp(target, 0, nonRbcWater);
            var requestedFlow = Math.Clamp((target - plasmaWater) / 600, -5e-5, 5e-5);
            plasmaWater += Math.Clamp(requestedFlow * dt, -plasmaWater, nonVascularWater);
        }

        var hotError = Math.Max(0, old.CoreTemperatureC - (profile.ReferenceCoreTemperatureC + inputs.FeverTemperatureRiseC)
            + 0.1 * (old.SkinTemperatureC - profile.ReferenceSkinTemperatureC));
        var coldError = Math.Max(0, (profile.ReferenceCoreTemperatureC + inputs.FeverTemperatureRiseC) - old.CoreTemperatureC
            + 0.1 * (profile.ReferenceSkinTemperatureC - old.SkinTemperatureC));
        var hydrationFactor = Hydration.PerformanceFactor(Hydration.Stage(ReserveDisplay.HydrationCapacityG
            + (bodyWater - profile.ReferenceBodyWaterL) * 1000));
        var requestedShiveringPower = Math.Min(options.MaximumShiveringPowerW, 150 * coldError) * inputs.EnergyDemandMultiplier;
        var basalPower = profile.BasalMetabolicPowerW * inputs.BasalMetabolismMultiplier * inputs.EnergyDemandMultiplier;
        var activityEnergyPower = inputs.ActivityPowerW * inputs.EnergyDemandMultiplier;
        var requestedPower = basalPower + requestedShiveringPower + activityEnergyPower;
        var requestedEnergyKJ = requestedPower * dt / 1000;
        var glycogenShare = inputs.ActivityPowerW >= 450 * 4184d / 3600 - .001 ? .8 : inputs.ActivityPowerW > 0 ? .6 : .3;
        var glycogenConsumed = Math.Min(glycogen, requestedEnergyKJ * glycogenShare);
        var fatConsumed = Math.Min(fat, requestedEnergyKJ * (1 - glycogenShare));
        glycogenConsumed += Math.Min(Math.Max(0, glycogen - glycogenConsumed), Math.Max(0, requestedEnergyKJ - fatConsumed - glycogenConsumed));
        fatConsumed += Math.Min(Math.Max(0, fat - fatConsumed), Math.Max(0, requestedEnergyKJ - fatConsumed - glycogenConsumed));
        var muscleFuelKJ = Math.Min(Math.Max(0, muscle - profile.MuscleMassMinG) * DietaryMetabolism.ProteinKJPerG,
            Math.Max(0, requestedEnergyKJ - fatConsumed - glycogenConsumed));
        muscle -= muscleFuelKJ / DietaryMetabolism.ProteinKJPerG;
        ledger.MuscleFuelKJ += muscleFuelKJ;
        ledger.FatConsumedKJ += fatConsumed;
        ledger.GlycogenConsumedKJ += glycogenConsumed;
        fat -= fatConsumed;
        glycogen -= glycogenConsumed;
        var actualEnergyJ = (fatConsumed + glycogenConsumed + muscleFuelKJ) * 1000;
        var actualPower = actualEnergyJ / dt;
        // Priority is basal, then thermoregulation, then voluntary mechanical activity.
        var actualBasalPower = Math.Min(basalPower, actualPower);
        var actualShiveringPower = Math.Min(requestedShiveringPower, Math.Max(0, actualPower - actualBasalPower));
        var actualActivityPower = Math.Min(activityEnergyPower, Math.Max(0, actualPower - actualBasalPower - actualShiveringPower));
        var externalWorkPower = options.ExternalWorkFraction * actualActivityPower;
        ledger.MetabolicEnergyJ += actualEnergyJ;
        ledger.BasalEnergyJ += actualBasalPower * dt;
        ledger.ShiveringEnergyJ += actualShiveringPower * dt;
        ledger.ActivityEnergyJ += actualActivityPower * dt;
        ledger.ExternalWorkJ += externalWorkPower * dt;
        ledger.UnmetMetabolicEnergyJ += Math.Max(0, requestedPower * dt - actualEnergyJ);
        ledger.ResourceLimited |= actualEnergyJ + 1e-9 < requestedEnergyKJ * 1000;

        var hc = 4 + 8 * Math.Sqrt(environment.WindSpeedMps);
        if (!TryDryHeat(old.SkinTemperatureC, environment, hc, out var dryHeatW)) return null;
        var vaporAir = environment.RelativeHumidity * SaturationPressurePa(environment.AirTemperatureC);
        ledger.VaporPressureOutsideFitDomain |= OutsideVaporFitDomain(environment.AirTemperatureC)
            || OutsideVaporFitDomain(old.SkinTemperatureC);
        var exhaleTemperatureC = Math.Min(34, old.CoreTemperatureC);
        var ventilationM3PerSecond = 0.0;
        var respiratoryWaterRequested = 0.0;
        if (options.EnableRespiration)
        {
            // Demand-to-ventilation mapping is a calibration seed; oxygen remains delegated.
            var activityFraction = Math.Clamp(inputs.ActivityPowerW / 400, 0, 1);
            var breathsPerMinute = 14 + 26 * activityFraction;
            ventilationM3PerSecond = breathsPerMinute * 0.5e-3 / 60;
            var exhaleVaporDensity = SaturationPressurePa(exhaleTemperatureC) / (461.5 * (exhaleTemperatureC + 273.15));
            var ambientVaporDensity = vaporAir / (461.5 * (environment.AirTemperatureC + 273.15));
            respiratoryWaterRequested = ventilationM3PerSecond * Math.Max(0, exhaleVaporDensity - ambientVaporDensity) * dt;
            ledger.VaporPressureOutsideFitDomain |= OutsideVaporFitDomain(exhaleTemperatureC);
        }
        var sweatRequested = options.EnableSweating
            ? Math.Min(options.MaximumSweatLPerSecond, 1e-4 * hotError * (1 + .15 * inputs.HeatAdaptation)) * hydrationFactor * dt : 0;
        var insensibleRequested = options.EnableInsensibleWaterLoss ? options.InsensibleWaterLossLPerSecond * dt : 0;
        var fecalWaterRequested = options.EnableFecalWaterLoss
            ? Math.Max(options.MinimumFecalWaterLossLPerSecond, options.FecalWaterLossLPerSecond * hydrationFactor) * dt : 0;
        // Calibrate the existing baseline pathways together, once. Weather/activity still
        // change respiratory loss; explicit component overrides retain their relative rates.
        var neutralRespiration = 14 * .5e-3 / 60 * (SaturationPressurePa(34) / (461.5 * 307.15)
            - .5 * SaturationPressurePa(20) / (461.5 * 293.15));
        var basalScale = options.BasalWaterLossGPerGameHour / (1000 * 3600 * (6e-6 + 1.7e-5 + neutralRespiration));
        insensibleRequested *= basalScale;
        fecalWaterRequested *= basalScale;
        respiratoryWaterRequested *= basalScale;
        var entericRequested = inputs.EntericWaterLossLps * dt;
        var totalWaterRequested = sweatRequested + insensibleRequested + fecalWaterRequested + respiratoryWaterRequested + entericRequested;
        var protectedWater = plasmaWater + profile.RbcWaterFraction * rbc;
        var availableWater = Math.Max(0, bodyWater - protectedWater);
        var waterScale = totalWaterRequested > 0 ? Math.Min(1, availableWater / totalWaterRequested) : 1;
        var sweat = sweatRequested * waterScale;
        var insensible = insensibleRequested * waterScale;
        var fecalWater = fecalWaterRequested * waterScale;
        var respiratoryWater = respiratoryWaterRequested * waterScale;
        var enteric = entericRequested * waterScale;
        bodyWater = Math.Max(protectedWater, bodyWater - (sweat + insensible + fecalWater + respiratoryWater + enteric));
        ledger.EntericWaterLossL += enteric;
        ledger.SweatWaterL += sweat;
        ledger.InsensibleWaterL += insensible;
        ledger.FecalWaterL += fecalWater;
        ledger.RespiratoryWaterL += respiratoryWater;
        ledger.ResourceLimited |= waterScale < 1;
        // Deprecated urine options are ignored. Water leaves through the modeled
        // sweat, respiration, insensible and fecal pathways only.
        // Exactly one salt reserve; the normalized body value is only its projection.
        if (nutrition is not null)
        {
            var reserves = (double[])nutrition.Reserve.Clone();
            reserves[Nutrients.IndexOf("electrolytes")] = Math.Max(0, reserves[Nutrients.IndexOf("electrolytes")] - sweat * .8);
            nutrition = nutrition with { Reserve = reserves };
            electrolyte = reserves[Nutrients.IndexOf("electrolytes")] / Nutrients.All[Nutrients.IndexOf("electrolytes")].Maximum;
        }

        // Both skin water pathways enter one surface film. Evaporation removes film water only.
        // Water density is approximated as 1 kg/L throughout this P1 bookkeeping model.
        var rain = environment.RainFilmInflowKgPerSecond * dt;
        var filmBeforeRunoff = old.SkinFilmWaterKg + sweat + insensible + rain;
        var runoff = Math.Max(0, filmBeforeRunoff - profile.MaximumSkinFilmWaterKg);
        var film = Math.Min(profile.MaximumSkinFilmWaterKg, filmBeforeRunoff);
        var evaporated = 0.0;
        if (options.EnableEvaporation)
        {
            var airResistance = 1 / (0.0165 * hc);
            var totalEvaporativeResistance = airResistance + environment.ClothingEvaporativeResistanceM2PaPerW;
            var maximumEvaporationW = profile.SurfaceAreaM2 * (1 - environment.WaterImmersionFraction)
                * Math.Max(0, SaturationPressurePa(old.SkinTemperatureC) - vaporAir) / totalEvaporativeResistance;
            evaporated = Math.Min(film, maximumEvaporationW * dt / LatentHeatJPerKg);
            film = Math.Max(0, film - evaporated);
        }
        ledger.RainFilmWaterKg += rain;
        ledger.RunoffWaterKg += runoff;
        ledger.EvaporatedWaterKg += evaporated;

        var bloodVolumeAfterLoss = plasmaWater / profile.PlasmaWaterFraction + rbc;
        var neutralConductance = Math.Clamp(15 + 20 * (old.CoreTemperatureC - (profile.ReferenceCoreTemperatureC + inputs.FeverTemperatureRiseC))
            + 1.5 * (old.SkinTemperatureC - profile.ReferenceSkinTemperatureC), 5, 50);
        // Scale continuously with circulating blood. The former 2*ratio-1
        // disabled all active core/skin transfer below half the reference blood.
        var perfusion = Math.Clamp(bloodVolumeAfterLoss / profile.ReferenceBloodVolumeL, 0, 1);
        var coreSkinConductance = 5 + (neutralConductance - 5) * perfusion;
        if (ThermalAdaptation.ColdDemand(old, profile) > .05) coreSkinConductance *= 1 - .10 * inputs.ColdAdaptation;
        var coreSkinW = coreSkinConductance * (old.CoreTemperatureC - old.SkinTemperatureC);
        var respiratoryHeatJ = LatentHeatJPerKg * respiratoryWater
            + 1.2 * 1005 * ventilationM3PerSecond * (exhaleTemperatureC - environment.AirTemperatureC) * dt;
        var evaporativeHeatJ = evaporated * LatentHeatJPerKg;
        var contactHeatW = environment.ContactConductanceWPerK * (old.SkinTemperatureC - environment.ContactTemperatureC);
        ledger.DryHeatOutJ += dryHeatW * dt;
        ledger.RespiratoryHeatOutJ += respiratoryHeatJ;
        ledger.EvaporativeHeatOutJ += evaporativeHeatJ;
        ledger.ContactHeatOutJ += contactHeatW * dt;
        ledger.CoreToSkinHeatJ += coreSkinW * dt;
        var pressure = inputs.IsSleeping ? Math.Max(0, old.SleepPressure - inputs.SleepQuality * dt / inputs.SleepPressureRecoverySeconds)
            : inputs.IsResting ? old.SleepPressure <= .5 ? old.SleepPressure
                : Math.Max(.5, old.SleepPressure - .385 * dt / inputs.SleepPressureRecoverySeconds)
            : Math.Min(1, old.SleepPressure + dt / inputs.SleepPressureRiseSeconds);
        var trainingActivityFraction = Math.Clamp(inputs.ActivityPowerW / 600, 0, 1);
        var resourceStress = requestedEnergyKJ <= 1e-9 ? 0 : Math.Clamp(1 - actualEnergyJ / (requestedEnergyKJ * 1000), 0, 1);
        var completedActivityFraction = inputs.ActivityPowerW <= 1e-9
            ? 0
            : Math.Clamp(actualActivityPower / inputs.ActivityPowerW, 0, 1);
        var stimulus = Math.Clamp(old.MuscleTrainingStimulus + trainingActivityFraction * completedActivityFraction * dt / 1000, 0, 1);
        var proteinAvailable = Math.Max(0, inputs.MuscleProteinAvailableG - ledger.MuscleProteinUsedG);
        var recovery = nutrition is not null ? 0 : inputs.IsSleeping ? inputs.SleepQuality : inputs.ActivityPowerW <= 20 ? .35 : 0;
        var trainingGain = Math.Min(Math.Min(stimulus * 500, profile.MuscleMassMaxG - muscle),
            Math.Min(proteinAvailable, recovery * inputs.MuscleRecoverySupport * hydrationFactor * dt * options.MaximumMuscleGainGPerDay / 86400));
        // Each gram of wet muscle uses 0.2 g of the available protein pool and 5 kJ of stored fuel.
        // The server debits this exact accepted protein amount from nutrition after a successful step.
        trainingGain = Math.Min(trainingGain, (glycogen + fat) / 5);
        var growthGlycogen = Math.Min(glycogen, trainingGain * 5);
        glycogen -= growthGlycogen;
        fat -= trainingGain * 5 - growthGlycogen;
        var growthEnergyJ = trainingGain * 5000;
        ledger.MuscleProteinUsedG += trainingGain;
        ledger.MuscleGrowthEnergyJ += growthEnergyJ;
        ledger.MetabolicEnergyJ += growthEnergyJ;
        stimulus = Math.Max(0, stimulus - trainingGain / 500);
        var muscleLoss = 0d;
        muscle = Math.Clamp(muscle + trainingGain - muscleLoss, profile.MuscleMassMinG, profile.MuscleMassMaxG);
        var coreEnergyChangeJ = actualEnergyJ + growthEnergyJ + conversionHeatJ - externalWorkPower * dt - coreSkinW * dt - respiratoryHeatJ;
        var skinEnergyChangeJ = coreSkinW * dt - dryHeatW * dt - evaporativeHeatJ - contactHeatW * dt;
        var sweatDebt = Math.Clamp(old.SweatDebtL + sweat - evaporated * .5, 0, profile.MaximumGutWaterL);
        return old with
        {
            CoreTemperatureC = old.CoreTemperatureC + coreEnergyChangeJ / (profile.CoreHeatCapacityJPerK * options.ThermalResponseTimeScale),
            SkinTemperatureC = old.SkinTemperatureC + skinEnergyChangeJ / (profile.SkinHeatCapacityJPerK * options.ThermalResponseTimeScale),
            BodyWaterL = bodyWater,
            BladderUrineL = bladderUrine,
            UrineProductionMlPerGameHour = 0,
            PlasmaWaterL = plasmaWater,
            RbcVolumeL = rbc,
            GutWaterL = gutWater,
            SkinFilmWaterKg = film,
            GlycogenEnergyKJ = glycogen,
            FatEnergyKJ = fat,
            MuscleMassG = muscle,
            MuscleTrainingStimulus = stimulus,
            ElectrolyteBalance = electrolyte,
            SweatDebtL = sweatDebt,
            GutEnergyKJ = gutEnergy,
            SleepPressure = pressure,
            ElapsedBioSeconds = old.ElapsedBioSeconds + dt
        };
    }

    private bool TryDryHeat(double skinC, EnvironmentSample environment, double hc, out double dryHeatW)
    {
        dryHeatW = 0;
        if (!options.EnableDryHeatExchange || environment.WaterImmersionFraction >= 1) return true;
        var resistanceM2KPerW = environment.ClothingInsulationClo * 0.155;
        double SurfaceOutflow(double surfaceC) => profile.SurfaceAreaM2 * hc * (surfaceC - environment.AirTemperatureC)
            + 0.95 * StefanBoltzmann * profile.SurfaceAreaM2
            * (Math.Pow(surfaceC + 273.15, 4) - Math.Pow(environment.RadiantTemperatureC + 273.15, 4))
            - environment.AbsorbedSolarPowerW;
        if (resistanceM2KPerW < 1e-9)
        {
            dryHeatW = SurfaceOutflow(skinC) * (1 - environment.WaterImmersionFraction);
            return double.IsFinite(dryHeatW);
        }
        double Residual(double surfaceC) => profile.SurfaceAreaM2 * (skinC - surfaceC) / resistanceM2KPerW - SurfaceOutflow(surfaceC);
        var low = Math.Min(skinC, Math.Min(environment.AirTemperatureC, environment.RadiantTemperatureC)) - 50;
        var high = Math.Max(skinC, Math.Max(environment.AirTemperatureC, environment.RadiantTemperatureC)) + 100;
        if (Residual(low) < 0 || Residual(high) > 0) return false;
        var middle = 0.0;
        for (var iteration = 0; iteration < 32; iteration++)
        {
            middle = (low + high) / 2;
            var residual = Residual(middle);
            if (Math.Abs(residual) <= 0.1) break;
            if (residual > 0) low = middle;
            else high = middle;
        }
        dryHeatW = profile.SurfaceAreaM2 * (skinC - middle) / resistanceM2KPerW * (1 - environment.WaterImmersionFraction);
        return double.IsFinite(dryHeatW);
    }

    private static bool OutsideVaporFitDomain(double temperatureC) => temperatureC is < -40 or > 50;
    private static double SaturationPressurePa(double temperatureC) => 610.94 * Math.Exp(17.625 * temperatureC / (temperatureC + 243.04));
    private static StepResult Rejected(BodyState previous, string error) => new(false, previous, new SimulationDiagnostics(), error);

    private sealed class Ledger
    {
        public double MuscleFuelKJ;
        public double RepairMaterialEnergyKJ;
        public double ConversionHeatJ;
        public double GlycogenConsumedKJ;
        public double FatConsumedKJ;
        public double MuscleProteinUsedG;
        public double MuscleGrowthEnergyJ;
        public double MetabolicEnergyJ;
        public double ExternalWorkJ;
        public double DryHeatOutJ;
        public double RespiratoryHeatOutJ;
        public double EvaporativeHeatOutJ;
        public double ContactHeatOutJ;
        public double CoreToSkinHeatJ;
        public double BasalEnergyJ;
        public double ShiveringEnergyJ;
        public double ActivityEnergyJ;
        public double UnmetMetabolicEnergyJ;
        public double AbsorbedWaterL;
        public double LegacyBladderReturnL;
        public double AbsorbedEnergyKJ;
        public double SweatWaterL;
        public double RespiratoryWaterL;
        public double InsensibleWaterL;
        public double FecalWaterL;
        public double EntericWaterLossL;
        public double WholeBloodLossL;
        public double BloodWaterLossL;
        public double RbcLossL;
        public double PlasmaWaterLossL;
        public double RainFilmWaterKg;
        public double EvaporatedWaterKg;
        public double RunoffWaterKg;
        public bool VaporPressureOutsideFitDomain;
        public bool ResourceLimited;

        public SimulationDiagnostics Finish(BodyState before, BodyState after, BodyProfile profile, double dt, int steps, double thermalScale)
        {
            var heatChange = (after.CoreTemperatureC - before.CoreTemperatureC) * profile.CoreHeatCapacityJPerK
                + (after.SkinTemperatureC - before.SkinTemperatureC) * profile.SkinHeatCapacityJPerK;
            heatChange *= thermalScale;
            var expectedHeatChange = MetabolicEnergyJ + ConversionHeatJ - ExternalWorkJ - DryHeatOutJ
                - RespiratoryHeatOutJ - EvaporativeHeatOutJ - ContactHeatOutJ;
            var bodyWaterChange = after.BodyWaterL - before.BodyWaterL;
            var expectedBodyWaterChange = AbsorbedWaterL + LegacyBladderReturnL - SweatWaterL - InsensibleWaterL
                - FecalWaterL - RespiratoryWaterL - BloodWaterLossL - EntericWaterLossL;
            var totalWaterChange = bodyWaterChange + (after.GutWaterL - before.GutWaterL)
                + (after.BladderUrineL - before.BladderUrineL)
                + (after.SkinFilmWaterKg - before.SkinFilmWaterKg);
            var expectedTotalWaterChange = RainFilmWaterKg - FecalWaterL - RespiratoryWaterL
                - BloodWaterLossL - EvaporatedWaterKg - RunoffWaterKg - EntericWaterLossL;
            var fuelChange = (after.GlycogenEnergyKJ - before.GlycogenEnergyKJ)
                + (after.FatEnergyKJ - before.FatEnergyKJ) + (after.GutEnergyKJ - before.GutEnergyKJ);
            return new SimulationDiagnostics
            {
                GlycogenConsumedKJ = GlycogenConsumedKJ, FatConsumedKJ = FatConsumedKJ,
                MuscleFuelKJ = MuscleFuelKJ, ConversionHeatJ = ConversionHeatJ,
                RepairMaterialEnergyKJ = RepairMaterialEnergyKJ,
                MuscleProteinUsedG = MuscleProteinUsedG, MuscleGrowthEnergyJ = MuscleGrowthEnergyJ,
                AcceptedBioSeconds = dt, Substeps = steps,
                MetabolicEnergyJ = MetabolicEnergyJ, ExternalWorkJ = ExternalWorkJ,
                DryHeatOutJ = DryHeatOutJ, RespiratoryHeatOutJ = RespiratoryHeatOutJ,
                EvaporativeHeatOutJ = EvaporativeHeatOutJ, ContactHeatOutJ = ContactHeatOutJ,
                CoreToSkinHeatJ = CoreToSkinHeatJ, BasalEnergyJ = BasalEnergyJ,
                ShiveringEnergyJ = ShiveringEnergyJ, ActivityEnergyJ = ActivityEnergyJ,
                UnmetMetabolicEnergyJ = UnmetMetabolicEnergyJ,
                AbsorbedWaterL = AbsorbedWaterL, LegacyBladderReturnL = LegacyBladderReturnL,
                UrineWaterL = 0, AbsorbedEnergyKJ = AbsorbedEnergyKJ,
                SweatWaterL = SweatWaterL, InsensibleWaterL = InsensibleWaterL,
                FecalWaterL = FecalWaterL, RespiratoryWaterL = RespiratoryWaterL, EntericWaterLossL = EntericWaterLossL,
                WholeBloodLossL = WholeBloodLossL, BloodWaterLossL = BloodWaterLossL,
                RbcLossL = RbcLossL, PlasmaWaterLossL = PlasmaWaterLossL,
                RainFilmWaterKg = RainFilmWaterKg, EvaporatedWaterKg = EvaporatedWaterKg,
                RunoffWaterKg = RunoffWaterKg,
                HeatBalanceResidualJ = heatChange - expectedHeatChange,
                BodyWaterBalanceResidualL = bodyWaterChange - expectedBodyWaterChange,
                TotalWaterBalanceResidualL = totalWaterChange - expectedTotalWaterChange,
                FuelBalanceResidualKJ = fuelChange + MetabolicEnergyJ / 1000 - MuscleFuelKJ
                    + ConversionHeatJ / 1000 + RepairMaterialEnergyKJ,
                VaporPressureOutsideFitDomain = VaporPressureOutsideFitDomain,
                ResourceLimited = ResourceLimited
            };
        }
    }
}
