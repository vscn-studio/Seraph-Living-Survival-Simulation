using Seraph.Core.Simulation;
using Seraph.Core.State;

namespace Seraph.Core.Framework;

/// <summary>Blood solutes are separate from water compartments and the native oxygen reserve.</summary>
public sealed record BloodState
{
    public double LactateG { get; init; }
    public double WasteMl { get; init; }
    public double Toxicity { get; init; }
    public double RegenerationMlPerGameHour { get; init; }
    public double FiltrationMlPerGameHour { get; init; }
    public bool RenalHyperactivity { get; init; }
    public double ElectrolytesUsedMgPerGameHour { get; init; }
    public string? Validate() => !BodyProfile.In(Toxicity, 0, 1) || !BodyProfile.In(LactateG, 0, 1e9) || !BodyProfile.In(WasteMl, 0, 1e9)
        || !BodyProfile.In(RegenerationMlPerGameHour, 0, 10000) || !BodyProfile.In(FiltrationMlPerGameHour, 0, 30000)
        || !BodyProfile.In(ElectrolytesUsedMgPerGameHour, 0, 1e8)
        ? "Invalid blood solute or circulation state." : null;
}

public sealed record CirculationOptions
{
    public double RegenerationMlPerGameHour { get; init; } = 180;
    public double FiltrationMlPerGameHour { get; init; } = 72;
    public double LactateClearanceGPerGameHour { get; init; } = 400;
    public double LactateStandingHalfLifeSeconds { get; init; } = 180;
    public double LactateWalkingHalfLifeSeconds { get; init; } = 300;
    public double LactateSeatedHalfLifeSeconds { get; init; } = 150;
    public double RenalToxicityThreshold { get; init; } = .5;
    public double RenalBasalMultiplier { get; init; } = 1.55;
    public double RenalClearanceMultiplier { get; init; } = 2;
    // Retained for config compatibility; extra filtration no longer consumes electrolytes.
    public double RenalElectrolytesMgPerExtraMl { get; init; } = 0;
    public string? Validate() => !BodyProfile.In(RegenerationMlPerGameHour, 0, 10000)
        || !BodyProfile.In(FiltrationMlPerGameHour, 0, 10000) || !BodyProfile.In(LactateClearanceGPerGameHour, 0, 10000)
        || !BodyProfile.In(LactateStandingHalfLifeSeconds, 60, 1800) || !BodyProfile.In(LactateWalkingHalfLifeSeconds, 60, 3600)
        || !BodyProfile.In(LactateSeatedHalfLifeSeconds, 60, 1800)
        || !BodyProfile.In(RenalToxicityThreshold, 0, 1) || !BodyProfile.In(RenalBasalMultiplier, 1, 3)
        || !BodyProfile.In(RenalClearanceMultiplier, 1, 3) || !BodyProfile.In(RenalElectrolytesMgPerExtraMl, 0, 1000)
        ? "Invalid game-hour circulation rates." : null;
}

public readonly record struct CirculationResult(FrameworkState State, BodyState Body, double EnergyKJ, double BowelG);

/// <summary>Paid fuel produces filtered blood waste; exercise lactate is reused during recovery.</summary>
public static class BloodCirculation
{
    public static FrameworkState AddWaste(FrameworkState state, BodyState body, BodyProfile profile, double ml)
    {
        if (!double.IsFinite(ml) || ml < 0) throw new ArgumentOutOfRangeException(nameof(ml));
        var next = state with { Blood = state.Blood with { WasteMl = Math.Min(1e9, state.Blood.WasteMl + ml) } };
        return next with { Blood = next.Blood with { Toxicity = Toxicity(next, body, profile) } };
    }
    public static double LactateCapacityG(BodyState body, BodyProfile profile) => Math.Max(50,
        100 + (body.MuscleMassG - profile.InitialMuscleMassG) / 1000 * 5);
    public static double WasteCapacityMl(BodyState body, BodyProfile profile) => Math.Max(1, body.BloodVolumeL(profile) * 1000 * .25);
    public static double Toxicity(FrameworkState state, BodyState body, BodyProfile profile)
        => Math.Clamp((state.Blood.WasteMl / WasteCapacityMl(body, profile) - .4) / .45, 0, 1);
    public static double Pumping(FrameworkState state, BodyState body, BodyProfile profile)
    {
        var deficit = Math.Clamp((.9 - body.BloodVolumeL(profile) / profile.ReferenceBloodVolumeL) / .6, 0, 1);
        var hydration = Hydration.OrganFactor(Hydration.Stage(body, profile));
        return Math.Min(1 - Math.Pow(deficit, 1.3), (1 - .5 * BodyFunctionResolver.StructuralDamage(state, "torso")) * hydration);
    }
    public static double Filtration(FrameworkState state, BodyState body, BodyProfile profile)
        => Pumping(state, body, profile);

    // Electrolytes fuel the optional filtration boost during high blood-waste
    // load. They scale only the extra clearance; ordinary filtration continues
    // at every reserve level, and the reserve is not directly consumed here.
    public static double RenalSupport(FrameworkState state, BodyState body, BodyProfile profile, double seconds, CirculationOptions options)
    {
        if (seconds <= 0 || state.Blood.WasteMl / WasteCapacityMl(body, profile) <= options.RenalToxicityThreshold) return 0;
        var electrolytes = state.Nutrition.Get("electrolytes");
        var maximum = Nutrients.All[Nutrients.IndexOf("electrolytes")].Maximum;
        if (!electrolytes.Known || maximum <= 0) return 0;
        return Math.Clamp(electrolytes.Reserve / maximum, 0, 1);
    }

    public static double LactateRate(ExertionKind kind, double endurance = 1, double runningFraction = 1)
    {
        // Keep lactate as the slow, sprint-specific reserve. Work, attacks,
        // jumps and firestarting are paid through their own stamina and fuel
        // costs; counting each action as continuous sprinting filled this bar
        // during ordinary play. Endurance affects stamina and work capacity,
        // not the blood-lactate rate itself.
        return kind == ExertionKind.Sprinting ? .1 : 0;
    }

    public static FrameworkState Charge(FrameworkState state, ExertionKind kind, double seconds, double capacity = 100, double endurance = 1, double runningFraction = 1)
    {
        return state with { Blood = state.Blood with { LactateG = Math.Min(capacity, state.Blood.LactateG + LactateRate(kind, endurance, runningFraction) * Math.Max(0, seconds)) } };
    }

    public static FrameworkState RecoverExercise(FrameworkState state, ExertionKind kind, double realSeconds,
        double pumping, CirculationOptions options)
    {
        var halfLife = kind switch { ExertionKind.Sitting => options.LactateSeatedHalfLifeSeconds,
            ExertionKind.Standing => options.LactateStandingHalfLifeSeconds,
            ExertionKind.Walking or ExertionKind.LowPosture => options.LactateWalkingHalfLifeSeconds, _ => 0 };
        if (halfLife <= 0 || realSeconds <= 0) return state;
        var lactate = state.Blood.LactateG * Math.Pow(.5, realSeconds * Math.Clamp(pumping, 0, 1) * AdrenalineEffects.Flow(state) / halfLife);
        return state with { Blood = state.Blood with { LactateG = lactate } };
    }

    public static CirculationResult Settle(FrameworkState state, BodyState before, BodyState body, BodyProfile profile,
        SimulationDiagnostics flows, double healingProteinG, double seconds, double bowelSpaceG, bool resting,
        CirculationOptions? options = null, double? preparedRenalSupport = null, double restFraction = 1,
        bool realTimeExercise = false, bool sleeping = false)
    {
        options ??= new();
        if (options.Validate() is { } error) throw new ArgumentException(error);
        if (!BodyProfile.In(seconds, 0, 8) || !BodyProfile.In(bowelSpaceG, 0, BowelSettlement.CapacityG)) throw new ArgumentOutOfRangeException(nameof(seconds));
        if (seconds == 0) return new(state, body, 0, 0);
        var hours = seconds / 3600;
        // Solutes leave with actual whole-blood loss, before new production.
        var retained = Math.Clamp(1 - flows.WholeBloodLossL / Math.Max(1e-9, before.BloodVolumeL(profile)), 0, 1);
        var lactate = state.Blood.LactateG * retained;
        var waste = state.Blood.WasteMl * retained;
        // Sleep clears lactate against accelerated game time, independently of
        // the real-time recovery pass. A 30-minute game-time half-life lets a
        // normal night substantially restore the sprint reserve.
        if (sleeping)
            lactate *= Math.Pow(.5, seconds * Pumping(state, body, profile) * AdrenalineEffects.Flow(state) / 1800);
        // Calendar-only callers retain their explicit settlement path; the game adapter owns real-time exercise clearance.
        var clear = !sleeping && !realTimeExercise && resting ? Math.Min(lactate, options.LactateClearanceGPerGameHour * Pumping(state, body, profile) * AdrenalineEffects.Flow(state) * hours * Math.Clamp(restFraction, 0, 1)) : 0;
        lactate -= clear;
        waste += clear * .2 + (flows.GlycogenConsumedKJ * .1 + flows.FatConsumedKJ * .3 + flows.MuscleFuelKJ * .5) / 4.184 * 1.3
            + healingProteinG * 3.5 * .5 * 1.3;
        var renalSupport = preparedRenalSupport ?? RenalSupport(state, body, profile, seconds, options);
        if (!BodyProfile.In(renalSupport, 0, 1)) throw new ArgumentOutOfRangeException(nameof(preparedRenalSupport));
        var filtration = options.FiltrationMlPerGameHour * Filtration(state, body, profile) * hours;
        var filtered = Math.Min(waste, Math.Min(filtration, bowelSpaceG / 1.2));
        var extra = Math.Min(Math.Min(Math.Max(0, waste - filtered), Math.Max(0, bowelSpaceG / 1.2 - filtered)),
            filtration * (options.RenalClearanceMultiplier - 1) * renalSupport);
        var electrolytesUsed = 0d;
        filtered += extra;
        waste -= filtered;
        var proteinIndex = Nutrients.IndexOf("protein");
        var protein = state.Nutrition.Get("protein");
        var hct = profile.ReferenceRbcVolumeL / profile.ReferenceBloodVolumeL;
        var targetBloodL = Math.Max(0, profile.ReferenceBloodVolumeL - 2 * MicroNutrition.Burden(state.Nutrition, "iron"));
        var deficitMl = Math.Max(0, Math.Min((targetBloodL * hct - body.RbcVolumeL) / hct,
            targetBloodL - body.BloodVolumeL(profile)) * 1000);
        var regen = Hydration.Stage(body, profile) == 0 && flows.WholeBloodLossL < .00001 && (!state.Vitals.Lethal || state.Downed.Active)
            ? Math.Min(deficitMl, Math.Min(options.RegenerationMlPerGameHour * hours * Pumping(state, body, profile),
                Math.Min(DietaryMetabolism.AvailableProtein(state.Nutrition) / .17, (body.GlycogenEnergyKJ + body.FatEnergyKJ) / (.7 * 4.184)))) : 0;
        // All blood water is already included in total body water: redistribution consumes no external water.
        var waterPerMl = ((1 - hct) * profile.PlasmaWaterFraction + hct * profile.RbcWaterFraction) / 1000;
        regen = Math.Min(regen, Math.Max(0, body.BodyWaterL - body.PlasmaWaterL - profile.RbcWaterFraction * body.RbcVolumeL) / waterPerMl);
        var energy = regen * .7 * 4.184;
        var glycogen = Math.Min(body.GlycogenEnergyKJ, energy);
        var reserves = (double[])state.Nutrition.Reserve.Clone();
        waste += regen * .17 * 3.5 * .5 * 1.3 + (glycogen * .1 + (energy - glycogen) * .3) / 4.184 * 1.3;
        var next = state with { Nutrition = DietaryMetabolism.SpendProtein(state.Nutrition with { Reserve = reserves }, regen * .17), Blood = new() {
            LactateG = lactate, WasteMl = Math.Min(1e9, waste),
            Toxicity = Math.Clamp((waste / WasteCapacityMl(body, profile) - .4) / .45, 0, 1),
            RegenerationMlPerGameHour = regen / hours, FiltrationMlPerGameHour = filtered / hours,
            RenalHyperactivity = renalSupport > 0, ElectrolytesUsedMgPerGameHour = electrolytesUsed * 1000 / hours } };
        return new(next, body with { RbcVolumeL = body.RbcVolumeL + regen / 1000 * hct,
            PlasmaWaterL = body.PlasmaWaterL + regen / 1000 * (1 - hct) * profile.PlasmaWaterFraction,
            RenalFiltrationMlPerGameHour = filtered / hours,
            GlycogenEnergyKJ = Math.Max(0, body.GlycogenEnergyKJ - glycogen),
            FatEnergyKJ = Math.Max(0, body.FatEnergyKJ - (energy - glycogen)) }, energy, filtered * 1.2);
    }
}
