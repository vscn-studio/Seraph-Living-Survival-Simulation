using Seraph.Core.Framework;
using Seraph.Core.State;
using Seraph.Core.Simulation;

internal static class NutritionChecks
{
    public static void ContentsMass()
    {
        var food = NutritionState.Starting() with { GutMassG = 100, GutFoodWaterG = 80, StomachWasteG = 20 };
        Near(100, food.ContentsMassG(.08));
        Near(350, food.ContentsMassG(.33));
        Near(20, food.ContentsMassG(0));
        var body = Rested with { GutWaterL = .08, BodyWaterL = Profile.ReferenceBodyWaterL - 1 };
        var flux = DietaryMetabolism.Digest(food, body, Profile, 8, 1, 5000);
        Near(100, flux.Nutrition.ContentsMassG(.08 - flux.WaterL) + flux.WaterL * 1000 + food.BowelResidueG(flux.Nutrition));
        Near(250, new NutritionState().ContentsMassG(.25));
        Require((food with { GutFoodWaterG = 101 }).Validate() is not null, "Food water exceeds mass.");
        Require((food with { GutFoodWaterG = double.NaN }).Validate() is not null, "Nonfinite mass accepted.");
    }

    private static readonly BodyProfile Profile = BodyProfile.Default;
    private static readonly BodyState Rested = BodyState.Create() with { SleepPressure = 0 };
    private static FrameworkState Healthy(double days = 1) => new()
    {
        OxygenKnown = true,
        Nutrition = new()
        {
            Reserve = Nutrients.All.Select(n => n.DailyBudget * days).ToArray(),
            Known = Enumerable.Repeat(true, Nutrients.All.Length).ToArray()
        }
    };
    private static PlayerCondition Condition(FrameworkState state, BodyState? body = null)
        => PlayerCondition.Evaluate(state, body ?? Rested, Profile);

    public static void EnergyCompartments()
    {
        var body = Rested with { GlycogenEnergyKJ = 0, FatEnergyKJ = Profile.FatCapacityKJ, GutEnergyKJ = 1000 };
        var energy = EnergyReserves.From(body, Profile);
        Near(Profile.FatCapacityKJ, energy.StoredKJ); Near(1000, energy.PendingKJ); Near(0, energy.GlycogenFraction);
        Near(1, Condition(Healthy(), body).RecoveryMultiplier);
        Near(1, Condition(Healthy(), body).StaminaRecoveryMultiplier);
    }

    public static void AdequateReserves()
    {
        var nutrition = Healthy(2).Nutrition.Advance(8, 0, 0);
        Require(nutrition.DeficiencyBurden.All(b => b == 0), "Nonempty stores create deficiencies.");
        var empty = Healthy(0).Nutrition.Advance(8, 0, 0);
        foreach (var id in MicroNutrition.Groups) Near(8d / 86400, empty.Get(id).Deficiency);
        Near(1.2, ImmuneSystem.Effective(Healthy()));
        Near(1, ImmuneSystem.Effective(Healthy(0)));
    }

    public static void AbsorptionConservation()
    {
        var before = NutritionState.Starting() with { GutMassG = 500, StomachWasteG = 100 };
        before.Pending[0] = 200; before.Pending[1] = 100; before.Pending[2] = 80; before.Pending[3] = 20;
        before.Pending[Nutrients.IndexOf("a")] = 3;
        var energy = 200 * DietaryMetabolism.CarbKJPerG + 100 * DietaryMetabolism.ProteinKJPerG + 80 * DietaryMetabolism.FatKJPerG;
        var flux = DietaryMetabolism.Digest(before, Rested with { GutEnergyKJ = energy }, Profile, 8, 1, 5000);
        var after = flux.Nutrition;
        Require(after.Validate() is null, "Invalid digestion state.");
        var carbs = before.Pending[0] - after.Pending[0]; var protein = before.Pending[1] - after.Pending[1];
        var fat = before.Pending[2] - after.Pending[2]; var fiber = before.Pending[3] - after.Pending[3];
        var waste = before.StomachWasteG - after.StomachWasteG;
        Near(500, after.GutMassG + carbs + protein + fat + fiber + waste);
        Near(fiber + waste, before.BowelResidueG(after));
        Near(protein, after.Reserve[1]);
        Near(flux.RemovedEnergyKJ, flux.CarbohydrateKJ + flux.FatKJ + flux.ProteinKJ);
        Near(MicroNutrition.Gain(20, 6), MicroNutrition.Gain(MicroNutrition.Gain(20, 2), 4));
        Require(after.Pending[Nutrients.IndexOf("a")] > 0, "All micronutrients absorbed on first tick.");
        var blocked = DietaryMetabolism.Digest(before, Rested, Profile, 8, 1, 0).Nutrition;
        Near(before.Pending[3], blocked.Pending[3]); Near(before.StomachWasteG, blocked.StomachWasteG);
        Require(blocked.Pending[0] < before.Pending[0], "Full bowel blocked independent carbohydrate absorption.");
        Near(500, before.GutMassG);
    }

    public static void FiberPassage()
    {
        var before = NutritionState.Starting() with { GutMassG = 30 };
        before.Pending[Nutrients.IndexOf("fiber")] = 30;
        var after = before;
        for (var i = 0; i < 900; i++) after = DietaryMetabolism.Digest(after, Rested, Profile, 8, 1, 5000).Nutrition;
        Near(30 * Math.Pow(2, -2d / 3), after.StomachFiberG, 1e-9);
        Near(30, after.StomachFiberG + before.BowelResidueG(after));
        Near(0, after.Get("fiber").Reserve);
        var full = DietaryMetabolism.Digest(before, Rested, Profile, 8, 1, 0).Nutrition;
        Near(30, full.StomachFiberG); Near(30, full.GutMassG);
        Require(after.Validate() is null, "Invalid fiber passage.");
        var deficient = NutritionState.Starting(); deficient.DeficiencyBurden[3] = 1;
        Require(deficient.Assess() == NutritionState.Starting().Assess(), "Fiber became a required body reserve.");
    }

    public static void UnknownNutrition()
    {
        var unknown = new NutritionState { Reserve = Nutrients.All.Select(n => n.Maximum).ToArray(),
            DeficiencyBurden = Enumerable.Repeat(1.0, Nutrients.All.Length).ToArray() };
        Near(0, unknown.Assess().Coverage);
        Near(0, unknown.Assess().Sufficiency);
        Near(0, NutrientInteractions.Burden(unknown, "does-not-exist"));
        Near(0, NutrientInteractions.Burden(unknown, "iron"));
        Near(1, Condition(Healthy() with { Nutrition = unknown }).RecoveryMultiplier);
        Near(1, Condition(Healthy() with { Nutrition = unknown }).StaminaRecoveryMultiplier);
        Require(!unknown.Get("does-not-exist").Known, "Unknown ID cannot become known.");
    }

    public static void InputBoundaries()
    {
        var state = Healthy().Nutrition;
        Require(ReferenceEquals(state, state.Advance(0, 0, 0)), "Zero time changes nutrition.");
        foreach (var dt in new[] { -1.0, 9, double.NaN, double.PositiveInfinity })
            Throws(() => state.Advance(dt, 0, 0));
        Throws(() => state.Advance(8, 101, 100));
        Throws(() => state.Advance(8, -1, 100));
        Throws(() => state.Advance(0, 1, 100));
        Require(state.Advance(8, Math.BitIncrement(100), 100).Reserve.SequenceEqual(state.Advance(8, 100, 100).Reserve),
            "A rounding bit must not reject physical flux or add nutrients.");
        Require(state.Validate() is null, "Rejected input mutated the original state.");
    }

    public static void PositiveEffects()
    {
        var sufficient = Condition(Healthy());
        Near(1, sufficient.RecoveryMultiplier); Near(1, sufficient.StaminaRecoveryMultiplier); Near(1, sufficient.FatigueRecoveryMultiplier);
        Require(sufficient == Condition(Healthy(2)), "Normal reserves change continuously.");
        Near(1, Condition(Healthy(3)).RecoveryMultiplier); // high reserves do not cause deficiency
        for (var i = 0; i < 100; i++) Near(1.2, ImmuneSystem.Effective(Healthy()));
        var missing = Healthy(); missing.Nutrition.Reserve[Nutrients.IndexOf("c")] = 0;
        Near(1 + .2 * 5 / 6, ImmuneSystem.Effective(missing));
        var pending = Healthy(0) with { Nutrition = Healthy(0).Nutrition with { Pending = Nutrients.All.Select(n => n.Maximum).ToArray() } };
        Near(1, ImmuneSystem.Effective(pending));
    }

    public static void StressGates()
    {
        var frame = Healthy();
        foreach (var body in new[] { Rested with { BodyWaterL = Profile.ReferenceBodyWaterL - 7 },
            Rested with { CoreTemperatureC = 35 }, Rested with { RbcVolumeL = 1 }, Rested with { GlycogenEnergyKJ = 0 } })
            Near(1, Condition(frame, body).StaminaRecoveryMultiplier);
        var wounded = frame with { Wounds = [new() { EventSequence = 1, Region = "torso", Severity = .8 }] };
        Require(ImmuneSystem.Effective(wounded) < ImmuneSystem.Effective(frame), "Organ damage no longer affects immunity.");
        Near(1.2, ImmuneSystem.Effective(frame));
    }

    public static void DeficiencyPenalties()
    {
        var frame = Healthy();
        foreach (var id in MicroNutrition.Groups) { frame.Nutrition.Reserve[Nutrients.IndexOf(id)] = 0; }
        var next = frame.Nutrition.Advance(8, 0, 0);
        Near(0, MicroNutrition.ImmuneBonus(next));
        Near(1, Condition(frame).StaminaRecoveryMultiplier);
        Require(Math.Abs(next.Get("c").Deficiency - 8d / 86400) < 1e-12, "Short shortage did not accumulate its elapsed deficiency burden.");
    }

    public static void CompositionTrend()
    {
        var kernel = new FrameworkKernel();
        var activity = new ActivityContext(0, false, true, false, false, 1, true);
        var old = Healthy() with { NutritionTrend = .5 };
        var lean = kernel.Advance(old, Rested with { FatEnergyKJ = 0 }, Profile, EnvironmentSample.Neutral, activity, 8);
        var fat = kernel.Advance(old, Rested with { FatEnergyKJ = Profile.FatCapacityKJ }, Profile, EnvironmentSample.Neutral, activity, 8);
        Require(lean.Accepted && fat.Accepted, lean.Error ?? fat.Error ?? "Trend step rejected.");
        Near(lean.State.NutritionTrend, fat.State.NutritionTrend);
        Require(lean.State.NutritionTrend > old.NutritionTrend, "Supported composition did not improve the trend.");
        var unknown = kernel.Advance(new() { NutritionTrend = .5 }, Rested, Profile, EnvironmentSample.Neutral, activity, 8);
        Require(unknown.State.NutritionTrend < .5, "Unknown composition inherits fat-based nutrition credit.");
    }

    public static void FrameworkRecovery()
    {
        var supported = Healthy() with { Fatigue = .2, EventSequence = 1, Wounds = [new() { EventSequence = 1, Severity = .3 }] };
        var control = supported with { Nutrition = new() };
        var activity = new ActivityContext(0, true, true, false, false, 1, true);
        var kernel = new FrameworkKernel();
        var good = kernel.Advance(supported, Rested, Profile, EnvironmentSample.Neutral, activity, 8);
        var baseline = kernel.Advance(control, Rested, Profile, EnvironmentSample.Neutral, activity, 8);
        Require(good.Accepted && baseline.Accepted, good.Error ?? baseline.Error ?? "Recovery rejected.");
        Near(good.State.Fatigue, baseline.State.Fatigue);
        Near(good.State.Wounds[0].Healing, baseline.State.Wounds[0].Healing);
        Require(ImmuneSystem.Effective(good.State) > ImmuneSystem.Effective(baseline.State), "Five reserves must support actual immunity.");
    }

    public static void ActivityCosts()
    {
        var activity = new ActivityContext(300, false, false, false, false, 1, true);
        var kernel = new FrameworkKernel();
        var good = kernel.Advance(Healthy(), Rested, Profile, EnvironmentSample.Neutral, activity, 8);
        var baseline = kernel.Advance(new(), Rested, Profile, EnvironmentSample.Neutral, activity, 8);
        Require(good.Accepted && baseline.Accepted, "Activity step rejected.");
        Near(good.State.Stamina, baseline.State.Stamina);
        Near(good.State.Fatigue, baseline.State.Fatigue);
        Near(300, good.State.LastActivityPowerW);
        Near(BodyState.Create().GlycogenEnergyKJ, Rested.GlycogenEnergyKJ);
    }

    public static void OxygenObservation()
    {
        var old = Healthy();
        var result = new FrameworkKernel().Advance(old, Rested, Profile, EnvironmentSample.Neutral,
            new(0, true, true, false, false, 1, false), 8);
        Require(result.Accepted && !result.State.OxygenKnown, "Oxygen evidence must expire when observation is unavailable.");
        Near(Condition(old).RecoveryMultiplier, Condition(result.State).RecoveryMultiplier);
    }

    public static void PhysicalAbsorptionBoundary()
    {
        foreach (var rate in new[] { .1, .3, 1.0, 3.7 })
        foreach (var gut in new[] { .01, .1, .3, .7, 1.1, 2.3, 7.9 })
        foreach (var dt in new[] { .1, .3, 1.0, 3.7, 8.0 })
        {
            var body = Rested with { GutEnergyKJ = gut };
            var physical = new SimulationKernel(Profile)
                .Advance(body, EnvironmentSample.Neutral, new() { DigestionGPerHour = rate * 1000 }, dt);
            Require(physical.Accepted, physical.Error ?? "Physical step failed.");
            var nutrition = Healthy().Nutrition.Advance(dt, physical.Diagnostics.AbsorbedEnergyKJ, body.GutEnergyKJ);
            Require(nutrition.Validate() is null, "Accepted physical absorption rejected by nutrition.");
        }
    }

    public static void MicronutrientRatesAndVitaminDIndependence()
    {
        var state = NutritionState.Starting();
        var after = DietaryMetabolism.Digest(state, Rested, Profile, 86400, 1, 5000).Nutrition;
        foreach (var id in MicroNutrition.Groups)
        {
            Near(50, state.Get(id).Reserve);
            Near(MicroNutrition.DailyPointsFor(id), state.Get(id).Reserve - after.Get(id).Reserve, 1e-8);
        }
        Near(3.5, state.Get("electrolytes").Reserve - after.Get("electrolytes").Reserve, 1e-9);
        Near(0, after.Reserve[0]); Near(0, after.Reserve[1]); Near(0, after.Reserve[2]);
        var deficient = Healthy(); deficient.Nutrition.Reserve[Nutrients.IndexOf("d")] = 0;
        deficient.Nutrition.DeficiencyBurden[Nutrients.IndexOf("d")] = 1;
        Require(Condition(Healthy()) == Condition(deficient), "D directly changed recovery.");
        Require(BodyFunctionResolver.Evaluate(Healthy(), Rested, Profile) == BodyFunctionResolver.Evaluate(deficient, Rested, Profile), "D directly changed functions.");
        var food = state with { GutMassG = 100 }; food.Pending[0] = 100; food.Pending[Nutrients.IndexOf("calcium")] = 2;
        var flux = DietaryMetabolism.Digest(food, Rested, Profile, 8, 1, 5000);
        foreach (var known in new[] { true, false })
        foreach (var reserve in new[] { 0d, 22.5, 45 })
        {
            var legacy = food with { Reserve = (double[])food.Reserve.Clone(), Known = (bool[])food.Known.Clone() };
            legacy.Reserve[Nutrients.IndexOf("d")] = reserve;
            legacy.Known[Nutrients.IndexOf("d")] = known;
            var actual = DietaryMetabolism.Digest(legacy, Rested, Profile, 8, 1, 5000);
            Near(flux.Nutrition.Get("calcium").Pending, actual.Nutrition.Get("calcium").Pending);
            Near(flux.Nutrition.Get("calcium").Reserve, actual.Nutrition.Get("calcium").Reserve);
            Near(1, NutrientInteractions.AbsorptionFactor(legacy.Pending, Nutrients.IndexOf("calcium"), legacy));
            Near(reserve, actual.Nutrition.Get("d").Reserve);
        }
    }

    public static void JumpFloor()
    {
        var depletedNutrition = Healthy(0) with { Fatigue = .899, Stamina = .051 };
        Array.Fill(depletedNutrition.Nutrition.DeficiencyBurden, 1);
        Require(BodyFunctionResolver.Evaluate(depletedNutrition, Rested, Profile).Jump == 1,
            "Non-threshold fatigue, stamina, and deficiencies reduced jumping.");

        foreach (var limited in new[] {
            Healthy(2) with { Fatigue = .9 },
            Healthy(2) with { Stamina = .05 },
            Healthy(2) with { Wounds = [new Wound { EventSequence = 1, Region = "left-leg", Severity = 2d / 15 }] }
        })
        {
            var functions = BodyFunctionResolver.Evaluate(limited, Rested, Profile);
            Near(limited.Wounds.Length > 0 ? .89 : 1, functions.Jump);
            Require(functions.CanJump, "Threshold should prevent one-block clearance without disabling jump input.");
        }

        Near(1, BodyFunctionResolver.Evaluate(Healthy(0), Rested, Profile).Jump);
        Near(1, BodyFunctionResolver.Evaluate(Healthy(2), Rested, Profile).Jump);

        var legZero = Healthy() with { Vitals = new VitalState { Integrity = [20, 20, 15, 15, 0, 15] } };
        var disabled = BodyFunctionResolver.Evaluate(legZero, Rested, Profile);
        Require(disabled.Jump < .55 && !disabled.CanJump, "A destroyed leg retained the native jump action.");
    }

    private static void Require(bool condition, string message) { if (!condition) throw new InvalidOperationException(message); }
    private static void Near(double expected, double actual, double tolerance = 1e-12)
        => Require(double.IsFinite(actual) && Math.Abs(expected - actual) <= tolerance, $"Expected {expected:G17}, got {actual:G17}.");
    private static void Throws(Action action)
    {
        try { action(); } catch (ArgumentOutOfRangeException) { return; }
        throw new InvalidOperationException("Invalid nutrition input was accepted.");
    }
}
