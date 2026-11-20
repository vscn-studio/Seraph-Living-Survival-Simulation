using Seraph.Core.Framework;
using Seraph.Core.Simulation;
using Seraph.Core.State;

internal static class PhysiologyEnhancementChecks
{
    private static readonly BodyProfile Profile = BodyProfile.Default with { BasalMetabolicPowerW = 0 };
    private static readonly ModelOptions Closed = new()
    {
        EnableDryHeatExchange = false,
        EnableRespiration = false,
        EnableEvaporation = false,
        EnableInsensibleWaterLoss = false,
        EnableFecalWaterLoss = false,
        EnablePlasmaRedistribution = false,
        MaximumShiveringPowerW = 0
    };

    public static void MuscleAndElectrolytes()
    {
        var kernel = new SimulationKernel(Profile, Closed with { EnableSweating = true });
        var warm = BodyState.Create(Profile) with { CoreTemperatureC = 39 };
        var step = kernel.Advance(warm, EnvironmentSample.Neutral, new() { ActivityPowerW = 300, Nutrition = NutritionState.Starting() }, 8);
        Require(step.Accepted && step.Nutrition!.Reserve[4] < 50, "Sweat must spend the single salt reserve.");
        var electrolyteMaximum = Nutrients.All[Nutrients.IndexOf("electrolytes")].Maximum;
        Require(Math.Abs(step.State.ElectrolyteBalance - step.Nutrition!.Reserve[4] / electrolyteMaximum) < 1e-9, "Salt projection diverged.");
        var intake = kernel.ApplyIntake(step.State, new IntakeEvent { ElectrolytesMg = 3000 });
        Require(intake.Accepted && intake.State.ElectrolyteBalance == step.State.ElectrolyteBalance,
            "Intake must wait for stomach absorption, not credit a second salt store.");

        var trainingKernel = new SimulationKernel(Profile, Closed);
        var trained = BodyState.Create(Profile);
        for (var i = 0; i < 8; i++) trained = Advance(trainingKernel, trained, 8, new() { ActivityPowerW = 600 });
        Require(trained.MuscleTrainingStimulus > 0 && trained.MuscleMassG == Profile.InitialMuscleMassG,
            "Work must store a training stimulus rather than grant free muscle.");
        var restored = Advance(trainingKernel, trained, 8, new() { IsSleeping = true, MuscleProteinAvailableG = 75 });
        Require(restored.MuscleMassG > trained.MuscleMassG && restored.MuscleTrainingStimulus < trained.MuscleTrainingStimulus,
            "Rest with protein must turn training into muscle.");
        var depleted = BodyState.Create(Profile) with { GlycogenEnergyKJ = 0, FatEnergyKJ = 0 };
        var lossKernel = new SimulationKernel(Profile, Closed);
        var lost = depleted;
        for (var i = 0; i < 8; i++) lost = Advance(lossKernel, lost, 8, new() { ActivityPowerW = 600 });
        Require(lost.MuscleMassG < depleted.MuscleMassG, "Resource-limited activity should lose muscle reserve.");
    }

    public static void TrainingAndFuel()
    {
        var kernel = new SimulationKernel(BodyProfile.Default, Closed);
        var low = BodyState.Create() with { GlycogenEnergyKJ = 0 };
        var rest = kernel.Advance(low, EnvironmentSample.Neutral, new(), 8);
        Require(rest.Accepted && rest.Diagnostics.UnmetMetabolicEnergyJ < 1e-7, "Fat cannot sustain resting metabolism at zero glycogen.");
        var work = kernel.Advance(low, EnvironmentSample.Neutral, new() { ActivityPowerW = 600 }, 8);
        Require(work.Accepted && work.Diagnostics.UnmetMetabolicEnergyJ < 1e-7 && work.Diagnostics.FatConsumedKJ > rest.Diagnostics.FatConsumedKJ, "Available fat did not pay work.");
        var trained = BodyState.Create() with { MuscleTrainingStimulus = .5 };
        var noProtein = kernel.Advance(trained, EnvironmentSample.Neutral, new() { IsSleeping = true }, 8);
        Require(noProtein.State.MuscleMassG == trained.MuscleMassG, "Muscle grows without protein.");
        var grown = kernel.Advance(trained, EnvironmentSample.Neutral, new() { IsSleeping = true, MuscleProteinAvailableG = .1 }, 8);
        var gain = grown.State.MuscleMassG - trained.MuscleMassG;
        Require(grown.Accepted && gain > 0 && gain <= .1 + 1e-8, "Growth exceeded available protein.");
        Require(Math.Abs(grown.Diagnostics.MuscleProteinUsedG - gain) < 1e-8
            && Math.Abs(grown.Diagnostics.MuscleGrowthEnergyJ - gain * 5000) < 1e-5, "Growth did not charge exact protein and energy.");
        Require(Math.Abs(grown.Diagnostics.FuelBalanceResidualKJ) < 1e-7 && Math.Abs(grown.Diagnostics.HeatBalanceResidualJ) < 1e-5,
            "Muscle recovery broke energy conservation.");
        var fattyMeal = new NutritionState { GutMassG = 1000 / DietaryMetabolism.FatKJPerG };
        fattyMeal.Pending[2] = fattyMeal.GutMassG;
        var fatFood = kernel.Advance(BodyState.Create() with { GutEnergyKJ = 1000 }, EnvironmentSample.Neutral,
            new() { Nutrition = fattyMeal }, 8);
        Require(fatFood.State.GlycogenEnergyKJ < BodyState.Create().GlycogenEnergyKJ
            && fatFood.State.FatEnergyKJ > BodyState.Create().FatEnergyKJ, "Dietary fat directly refills glycogen.");
        var healthy = new FrameworkState();
        var normal = BodyFunctionResolver.Evaluate(healthy, BodyState.Create(), BodyProfile.Default);
        var strong = BodyFunctionResolver.Evaluate(healthy, BodyState.Create() with { MuscleMassG = 40000 }, BodyProfile.Default);
        Require(strong.Attack > normal.Attack && strong.Mining == normal.Mining && strong.Bow == normal.Bow,
            "Trained muscle benefits never reach force actions.");
        Require(Math.Abs(strong.Attack - 1.05) < 1e-9 && strong.Mining == 1 && strong.Jump == normal.Jump && strong.Mobility >= normal.Mobility && strong.Mobility <= 1.05,
            "Strength bonus exceeded its scope/cap.");
    }

    public static void SleepAndImmunity()
    {
        var body = BodyState.Create();
        var kernel = new FrameworkKernel();
        var sleep = new ActivityContext(0, true, false, false, false, 1, true)
        { SleepQuality = 1, BedQuality = 1 };
        var good = kernel.Advance(new FrameworkState { ImmuneReserve = .5 }, body, BodyProfile.Default,
            EnvironmentSample.Neutral, sleep, 8);
        var poorBody = body with { CoreTemperatureC = 39 };
        var poorSleep = sleep with { SleepQuality = .4, BedQuality = 0 };
        var poorEnvironment = EnvironmentSample.Neutral with { WindSpeedMps = 12, AirPollution = 1, RoomVentilation = 0 };
        var poor = kernel.Advance(new FrameworkState { ImmuneReserve = .5, Pain = .5, Dirtiness = .5 }, poorBody,
            BodyProfile.Default, poorEnvironment, poorSleep, 8);
        Require(good.Accepted && poor.Accepted, "Framework sleep step rejected.");
        Require(poor.State.SleepEfficiency < good.State.SleepEfficiency, "Poor bed and environment must reduce sleep efficiency.");
        Require(poor.State.ImmuneReserve < good.State.ImmuneReserve, "Severe temperature must reduce slow immune support.");
    }

    public static void ResourceCapacity()
    {
        var healthy = BodyState.Create(Profile);
        var stressed = healthy with { MuscleMassG = Profile.MuscleMassMinG, ElectrolyteBalance = 0, SweatDebtL = Profile.MaximumGutWaterL };
        var baseline = BodyFunctionResolver.Evaluate(new FrameworkState(), healthy, Profile);
        var limited = BodyFunctionResolver.Evaluate(new FrameworkState(), stressed, Profile);
        Require(limited.Endurance < baseline.Endurance && limited.Mining == baseline.Mining * .9,
            "Muscle/electrolyte deficits must affect endurance without penalizing mining twice.");
        Require(Exertion.Apply(1, 1, ExertionKind.Mining, 8, true, false, false, capacity: limited.Endurance).Stamina
            < Exertion.Apply(1, 1, ExertionKind.Mining, 8, true, false, false, capacity: baseline.Endurance).Stamina,
            "Endurance deficit did not reach actual action costs.");
        var exhausted = BodyFunctionResolver.Evaluate(new FrameworkState { Stamina = .2 }, healthy, Profile);
        Require(exhausted.Sprint == baseline.Sprint && exhausted.CanSprint
            && RunningPace.Fraction(.1, 0, false, new()) < RunningPace.Fraction(.2, 0, false, new()),
            "Low stamina must gradually reduce sprint gain without disabling escape movement.");
    }

    public static void OxygenRaisesActionCost()
    {
        var kernel = new FrameworkKernel();
        var body = BodyState.Create();
        var normal = new ActivityContext(600, false, false, false, false, 1, true);
        var hypoxic = normal with { OxygenAvailability = .15 };
        var normalStep = kernel.Advance(new FrameworkState(), body, BodyProfile.Default,
            EnvironmentSample.Neutral, normal, 8);
        var hypoxicStep = kernel.Advance(new FrameworkState(), body, BodyProfile.Default,
            EnvironmentSample.Neutral, hypoxic, 8);
        Require(normalStep.Accepted && hypoxicStep.Accepted, "Action oxygen steps rejected.");
        Require(Exertion.Apply(1, .15, ExertionKind.Sprinting, 8, false, false, false).Stamina < Exertion.Apply(1, 1, ExertionKind.Sprinting, 8, true, false, false).Stamina,
            "Low available oxygen must increase the stamina cost of action work.");
        Require(hypoxicStep.State.Fatigue == normalStep.State.Fatigue,
            "Equal paid work must create equal fatigue; oxygen already affects action costs.");
    }

    public static void EnvironmentalContaminationRaisesExposure()
    {
        var clean = EnvironmentSample.Neutral;
        var contaminated = clean with { EnvironmentalContamination = .6 };
        Require(EnvironmentalExposure.CombinedLoad(clean) == 0, "Neutral environment must have no contamination load.");
        Require(EnvironmentalExposure.CombinedLoad(contaminated) > 0,
            "Environmental contamination must produce an exposure load.");
        var mixed = contaminated with { AirPollution = .5 };
        Require(EnvironmentalExposure.CombinedLoad(mixed) < 1.1,
            "Independent environment loads must remain bounded.");
        Require(EnvironmentalExposure.RespiratoryHazard(mixed, .2)
            > EnvironmentalExposure.RespiratoryHazard(mixed, 1),
            "Low immune reserve must increase environmental exposure hazard.");
        Require((contaminated with { EnvironmentalContamination = 1.1 }).Validate() is not null,
            "Out-of-range environmental contamination must be rejected.");
    }

    private static BodyState Advance(SimulationKernel kernel, BodyState state, double seconds, StepInputs inputs)
    {
        var result = kernel.Advance(state, EnvironmentSample.Neutral, inputs, seconds);
        Require(result.Accepted, result.Error ?? "Simulation step rejected.");
        return result.State;
    }

    private static void Require(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }
}
