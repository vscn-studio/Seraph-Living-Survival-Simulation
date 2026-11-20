using Seraph.Core.Framework;
using Seraph.Core.State;
using Seraph.Core.Simulation;

internal static class ClinicalChecks
{
    private static readonly BodyProfile Profile = BodyProfile.Default;
    private static readonly BodyState Body = BodyState.Create();
    private static readonly FrameworkKernel Kernel = new(new() { IntegrateBleeding = true });
    private static void Require(bool value, string message = "Clinical assertion failed.") { if (!value) throw new Exception(message); }
    private static void Near(double expected, double actual) => Require(Math.Abs(expected - actual) < 1e-9, $"Expected {expected}, got {actual}.");
    private static FrameworkState Wounds(int count)
    {
        var state = new FrameworkState();
        for (var i = 0; i < count; i++)
        {
            var result = Kernel.RecordInjury(state, new("hit-" + i, WoundKind.Cut, i % 2 == 0 ? "left-arm" : "right-arm",
                .3, InjuryEvidence.Verified, 0, .001));
            Require(result.Accepted, result.Error ?? "Injury rejected"); state = result.State;
        }
        return state;
    }
    public static void SinglePressure()
    {
        var state = Wounds(2); var selected = WoundCare.Select(state);
        Near(.0002, Kernel.BleedLps(state, 0));
        Near(.00011, Kernel.BleedLps(state, 0, selected));
        Near(.0002, Kernel.BleedLps(state, 1)); // Releasing the key restores loss immediately.
        var bandaged = WoundCare.Bandage(state, selected, true);
        Require(bandaged.Wounds.Count(w => w.BandageQuality > 0) == 1);
        Near(.0001, Kernel.BleedLps(bandaged, 10));
        Near(0, WoundCare.Rate(bandaged.Wounds.Single(w => w.EventSequence == selected)));
        var fullyBandaged = WoundCare.Bandage(bandaged, WoundCare.Select(bandaged), false);
        Near(0, Kernel.BleedLps(fullyBandaged, 10));
        Require(WoundCare.Select(fullyBandaged) == 0, "A stopped wound must not consume more bandages.");
        Require(bandaged.Wounds.Single(w => w.EventSequence != selected).Contamination == state.Wounds.Single(w => w.EventSequence != selected).Contamination);
        var carrying = state with { Wounds = state.Wounds.Select(w => w with {
            Pathogens = new Dictionary<string, double> { ["wound-staph"] = .8 } }).ToArray() };
        Near(.8, WoundCare.Bandage(carrying, selected, false).Wounds.Single(w => w.EventSequence == selected).Pathogens["wound-staph"]);
        Near(.24, WoundCare.Bandage(carrying, selected, true).Wounds.Single(w => w.EventSequence == selected).Pathogens["wound-staph"]);
    }
    public static void PersistentBleeding()
    {
        Near(112.2, WoundCare.InitialBleeding(10) * 60000);
        Near(0, WoundCare.InitialBleeding(0));
        var kernel = new FrameworkKernel(new() { IntegrateBleeding = true, WoundHealingSeconds = 1e8 });
        var state = kernel.RecordInjury(new(), new("severe", WoundKind.Cut, "torso", .5,
            InjuryEvidence.Verified, 0, WoundCare.InitialBleeding(10))).State;
        var first = state.Wounds.Single();
        Near(11.22, kernel.BleedLps(state, 0) * 60000);
        var oldWounds = Wounds(20);
        Near(.0002, kernel.BleedLps(oldWounds, 0));
        Require(kernel.BleedLps(oldWounds, 0, WoundCare.Select(oldWounds)) < .0002,
            "Pressure lost its benefit when old wounds reached the bleeding cap.");
        Near(first.BleedLps * .25, first.NaturalClotFloorLps);
        var activity = new ActivityContext(0, false, false, false, false, 1, true);
        for (var t = 0; t < 5 * 3600; t += 8)
        {
            var next = kernel.Advance(state, Body with { ElapsedBioSeconds = t + 8 }, Profile,
                EnvironmentSample.Neutral, activity, 8);
            Require(next.Accepted, next.Error ?? "Bleeding advance rejected"); state = next.State;
        }
        Require(state.Wounds.Single().BleedLps > first.BleedLps * .9, "Severe untreated wound clotted too quickly.");
        // At the natural floor, further time cannot close an unpaid severe wound.
        state = state with { Wounds = [first with { BleedLps = first.NaturalClotFloorLps }] };
        var floor = kernel.Advance(state, Body, Profile, EnvironmentSample.Neutral, activity, 8).State.Wounds.Single();
        Near(first.NaturalClotFloorLps, floor.BleedLps);
        Near(WoundCare.Rate(first) * .1, WoundCare.Rate(first, first.EventSequence));
        Near(0, WoundCare.Rate(WoundCare.Bandage(state, first.EventSequence, false).Wounds.Single()));
        var healed = first with { Healing = .5 };
        Near(WoundCare.Rate(first) * .5, WoundCare.Rate(healed));
        var mild = first with { BleedLps = .000001, InitialBleedLps = .000001, NaturalClotFloorLps = 0 };
        state = state with { Wounds = [mild] };
        for (var t = 0; t < 160; t += 8)
            state = kernel.Advance(state, Body, Profile, EnvironmentSample.Neutral, activity, 8).State;
        Near(0, state.Wounds.Single().BleedLps);
    }
    public static void CapacityIdentity()
    {
        var state = Wounds(40);
        Require(state.Wounds.Length == 32 && state.Wounds.Select(w => w.EventSequence).Distinct().Count() == 32);
        var after = WoundCare.Bandage(state, WoundCare.Select(state), false);
        Require(after.Wounds.Count(w => w.BandageQuality == 1) == 1, "Aggregated IDs applied one bandage twice.");
    }
    public static void EntericConservation()
    {
        var profile = Profile with { BasalMetabolicPowerW = 0 };
        var options = new ModelOptions { EnableDryHeatExchange = false, EnableRespiration = false, EnableEvaporation = false,
            EnableSweating = false, EnableInsensibleWaterLoss = false, EnableFecalWaterLoss = false,
            EnablePlasmaRedistribution = false, MaximumShiveringPowerW = 0 };
        var kernel = new SimulationKernel(profile, options);
        var result = kernel.Advance(Body, EnvironmentSample.Neutral, new() { EntericWaterLossLps = .001 }, 8);
        Require(result.Accepted, result.Error ?? "Step rejected");
        Near(.008, result.Diagnostics.EntericWaterLossL);
        Near(Body.BodyWaterL - .008, result.State.BodyWaterL);
        Near(0, result.Diagnostics.BodyWaterBalanceResidualL); Near(0, result.Diagnostics.TotalWaterBalanceResidualL);
        Near(Body.PlasmaWaterL, result.State.PlasmaWaterL); Near(Body.RbcVolumeL, result.State.RbcVolumeL);
        var protectedWater = Body.PlasmaWaterL + profile.RbcWaterFraction * Body.RbcVolumeL;
        var depleted = kernel.Advance(Body with { BodyWaterL = protectedWater + .0001 }, EnvironmentSample.Neutral,
            new() { EntericWaterLossLps = .01 }, 8);
        Require(depleted.Accepted && depleted.Diagnostics.ResourceLimited);
        Near(.0001, depleted.Diagnostics.EntericWaterLossL); Near(protectedWater, depleted.State.BodyWaterL);
    }
    public static void DiseaseCourse()
    {
        var definition = new DiseaseDefinition("giardia", 2, 3, 4, .5);
        var definitions = new Dictionary<string, DiseaseDefinition> { [definition.Id] = definition };
        var state = FrameworkState.Create("pathogen-test") with { Nutrition = new() };
        Require(Kernel.Expose(state, definition, 0, 8).State.Diseases.Length == 0);
        Require(Kernel.ProgressDiseases(state, definitions, 8).State.Diseases.Length == 0);
        var one = Kernel.Expose(state, definition, .01, 8).State;
        var split = state;
        for (var i = 0; i < 8; i++) split = Kernel.Expose(split, definition, .01, 1).State;
        Near(one.Diseases[0].CumulativeHazard, split.Diseases[0].CumulativeHazard);
        Near(one.Diseases[0].InfectionThreshold, split.Diseases[0].InfectionThreshold);
        var infected = Kernel.Expose(state, definition, 100, 1).State;
        Require(infected.Diseases.Single().Phase == DiseasePhase.Incubating && ClinicalEffects.EntericBurden(infected) == 0);
        var symptomatic = Kernel.ProgressDiseases(infected, definitions, 2).State;
        Require(symptomatic.Diseases.Single().Phase == DiseasePhase.Symptomatic);
        Near(0, ClinicalEffects.EntericBurden(symptomatic)); // Symptoms ramp instead of jumping at the boundary.
        var recovery = Kernel.ProgressDiseases(symptomatic, definitions, 4).State;
        Require(recovery.Diseases.Single().Phase == DiseasePhase.Recovering && recovery.Diseases.Single().Burden < .5);
        Require(Kernel.ProgressDiseases(recovery, definitions, 3).State.Diseases.Length == 0);
        var batched = Kernel.ProgressDiseases(infected, definitions, 8).State;
        var smallSteps = infected;
        for (var i = 0; i < 8; i++) smallSteps = Kernel.ProgressDiseases(smallSteps, definitions, 1).State;
        Require(batched.Diseases.SequenceEqual(smallSteps.Diseases));
        Require(Pathogens.All.All(p => p.Disease.Validate() is null && Enum.IsDefined(p.Route)),
            "Every named pathogen must retain a valid disease course and route.");
    }
    public static void NutritionSymptoms()
    {
        var frame = new FrameworkState { Nutrition = new() { DeficiencyBurden = Enumerable.Repeat(.8, Nutrients.All.Length).ToArray() } };
        Require(!ClinicalEffects.Evaluate(frame, Body, Profile).Any(e => e.Id == "anemia"));
        var known = (bool[])frame.Nutrition.Known.Clone();
        foreach (var id in new[] { "iron", "bcomplex", "calcium" }) known[Nutrients.IndexOf(id)] = true;
        var deficient = frame with { Nutrition = frame.Nutrition with { Known = known } };
        var effects = ClinicalEffects.Evaluate(deficient, Body, Profile);
        Require(effects.Any(e => e.Id == "anemia") && effects.Any(e => e.Id == "bone-weakness") && effects.Any(e => e.Id == "bcomplex-deficiency"));
        var replenished = deficient with { Nutrition = deficient.Nutrition with {
            Reserve = Enumerable.Repeat(90d, Nutrients.All.Length).ToArray() } };
        Near(.8, ClinicalEffects.Evaluate(replenished, Body, Profile).Single(e => e.Id == "anemia").Severity);
        var recovered = replenished with { Nutrition = DietaryMetabolism.Digest(replenished.Nutrition, Body, Profile, 43200, 1, 5000).Nutrition };
        Require(!ClinicalEffects.Evaluate(recovered, Body, Profile).Any(e => e.Id is "anemia" or "bone-weakness"),
            "Replenished reserves did not clear the existing burden over 12 hours.");
        Near(1, NutrientInteractions.FatigueMultiplier(deficient.Nutrition));
        var normalFunctions = BodyFunctionResolver.Evaluate(frame, Body, Profile);
        var functions = BodyFunctionResolver.Evaluate(deficient, Body, Profile);
        Require(functions.Mobility < normalFunctions.Mobility && functions.Consciousness < normalFunctions.Consciousness && functions.Jump == normalFunctions.Jump,
            "Deficiencies must not reduce jump clearance.");
    }

    public static void Adrenaline()
    {
        var kernel = new FrameworkKernel();
        var body = Body with { SleepPressure = .8, ElapsedBioSeconds = 10 };
        var tired = new FrameworkState { Fatigue = .6 };
        Require(!kernel.ActivateAdrenaline(tired, body, Profile, 10, 1.99, false).Accepted,
            "Sub-threshold injury without bleeding activated adrenaline.");

        var activated = kernel.ActivateAdrenaline(tired, body, Profile, 10, 2, false);
        Require(activated.Accepted && activated.State.AdrenalineLevel == 1);
        Near(.3, activated.State.Fatigue);
        Near(.6, activated.Body.SleepPressure);
        Near(60, activated.State.AdrenalineRemainingSeconds);
        Near(43200, activated.State.AdrenalineCooldownRemainingSeconds);
        Near(1.2, BodyFunctionResolver.Evaluate(activated.State, activated.Body, Profile).Jump);
        Require(!kernel.ActivateAdrenaline(activated.State, activated.Body, Profile, 11, 20, true).Accepted,
            "Active adrenaline was re-triggered.");
        // Sleeping/calendar catch-up cannot expire a real-time combat window.
        var fastBody = activated.Body with { ElapsedBioSeconds = 86400 };
        var calendar = kernel.Advance(activated.State, fastBody, Profile, EnvironmentSample.Neutral,
            new(0, true, false, false, false, 1, true), 8);
        Require(calendar.Accepted && calendar.State.AdrenalineLevel == 1);
        Near(60, calendar.State.AdrenalineRemainingSeconds);
        Near(43200, calendar.State.AdrenalineCooldownRemainingSeconds);
        var ticking = kernel.AdvanceAdrenaline(activated.State, fastBody, 19);
        Near(41, ticking.State.AdrenalineRemainingSeconds);
        Near(43200, ticking.State.AdrenalineCooldownRemainingSeconds);
        Near(1.2, BodyFunctionResolver.Evaluate(ticking.State, fastBody, Profile).Jump);
        // Round-trip a save during the effect. Offline time does not advance the countdown.
        var saved = System.Text.Json.JsonSerializer.Serialize(ticking.State);
        var loaded = System.Text.Json.JsonSerializer.Deserialize<FrameworkState>(saved)!;
        var paused = kernel.AdvanceAdrenaline(loaded, ticking.Body, 0);
        Near(41, paused.State.AdrenalineRemainingSeconds);
        var recovered = kernel.AdvanceAdrenaline(paused.State, paused.Body, 41);
        Require(recovered.State.AdrenalineLevel == 0 && recovered.State.AdrenalineFatigueDebt == 0
            && recovered.State.AdrenalineSleepDebt == 0);
        Near(.6, recovered.State.Fatigue);
        Near(.8, recovered.Body.SleepPressure);
        var again = kernel.AdvanceAdrenaline(recovered.State, recovered.Body, 1);
        Near(.6, again.State.Fatigue);
        Near(.8, again.Body.SleepPressure);
        Require(!kernel.ActivateAdrenaline(again.State, again.Body, Profile, 86400, 5, true).Accepted,
            "Cooldown unexpectedly elapsed in real time.");
        var ready = AdrenalineEffects.AdvanceGameTimeCooldown(again.State, 43200);
        Require(kernel.ActivateAdrenaline(ready, again.Body, Profile, 86400, 0, true).Accepted,
            "Bleeding did not activate adrenaline after half a game day.");
    }

    public static void SystemicDamage()
    {
        Near(0, ClinicalEffects.DamagePerSecond(new(), Body, Profile, true));
        var dry = Body with { BodyWaterL = Profile.ReferenceBodyWaterL - 8 };
        Near(.5 / 60, ClinicalEffects.DamagePerSecond(new(), dry, Profile, true));
        Near(.06, ClinicalEffects.DamagePerSecond(new(), Body with { CoreTemperatureC = 30 }, Profile, true));
        Near(0, ClinicalEffects.DamagePerSecond(new(), Body with { CoreTemperatureC = 30 }, Profile, false));
        Near(.06, ClinicalEffects.DamagePerSecond(new(), dry with { CoreTemperatureC = 30 }, Profile, true));
        var shock = dry with { PlasmaWaterL = 0, RbcVolumeL = 0, CoreTemperatureC = 30 };
        Near(.3, ClinicalEffects.DamagePerSecond(new(), shock, Profile, true));
        Require(ClinicalEffects.Evaluate(new(), Body with { SleepPressure = .9, CoreTemperatureC = 34 }, Profile)
            .Any(e => e.Id == "drowsy"));
    }
    public static void HeatStress()
    {
        var state = FrameworkState.Create("heat-stress");
        foreach (var reserve in new[] { 0d, 799d, 800d, 801d, 8000d })
        foreach (var temperature in new[] { 37.499, 37.5, 38d, 39.499, 39.5 })
        {
            var body = Body with { BodyWaterL = Profile.ReferenceBodyWaterL - (8000 - reserve) / 1000,
                CoreTemperatureC = temperature };
            var expected = temperature >= 39.5 || reserve < 800 && temperature >= 37.5;
            Require(ClinicalEffects.HeatStress(state, body, Profile) == expected,
                $"Heat stress disagrees at {reserve} mL, {temperature} C.");
            Require(ClinicalEffects.Evaluate(state, body, Profile).Any(e => e.Id == "heat-illness") == expected);
        }
        var fever = state with { Diseases = [new() { Pathogen = "qfever", Phase = DiseasePhase.Symptomatic, Burden = .5 }] };
        var threshold = 37.5 + DiseaseSyndromes.FeverSetpointC(fever) - 37;
        var hotDry = Body with { BodyWaterL = Profile.ReferenceBodyWaterL - 7.3, CoreTemperatureC = threshold - .001 };
        Require(!ClinicalEffects.HeatStress(fever, hotDry, Profile));
        Require(ClinicalEffects.HeatStress(fever, hotDry with { CoreTemperatureC = threshold }, Profile));
        Require(ClinicalEffects.HeatStress(fever, Body with { CoreTemperatureC = 39.5 }, Profile));
        Require(!ClinicalEffects.HeatStress(state, hotDry with { BodyWaterL = Profile.ReferenceBodyWaterL }, Profile),
            "Hydration failed to clear an ordinary heat warning.");
        Require(!ClinicalEffects.HeatStress(state, hotDry with { CoreTemperatureC = 37.49 }, Profile));
        Near(0, ClinicalEffects.DamagePerSecond(state, Body with { CoreTemperatureC = 39.5 }, Profile, true));
        Near(.03, ClinicalEffects.DamagePerSecond(state, Body with { CoreTemperatureC = 41 }, Profile, true));
    }
    public static void WashingNeed()
    {
        Require(!WoundCare.NeedsWash(new(), 0));
        Require(!WoundCare.NeedsWash(new() { Dirtiness = .099 }, 1));
        var dirty = new FrameworkState { Dirtiness = .4 };
        Require(WoundCare.NeedsWash(dirty, 10));
        var washed = Kernel.Treat(dirty, "clean", 10).State;
        Require(!WoundCare.NeedsWash(washed, 10));
        var wounded = Kernel.AddWound(new(), WoundKind.Cut, .5, 0, "left-arm").State;
        Require(WoundCare.NeedsWash(wounded, 10));
        var cleaned = Kernel.Treat(wounded, "clean", 10).State;
        Require(!WoundCare.NeedsWash(cleaned, 11));
        Require(WoundCare.NeedsWash(cleaned, 3610));
        Require(!WoundCare.NeedsWash(wounded with { Wounds = wounded.Wounds.Select(w => w with { Healing = 1 }).ToArray() }, 10));
    }

    public static void Cleaning()
    {
        var state = Wounds(2) with { Dirtiness = .8,
            Diseases = [new() { Pathogen = "wound-staph", Phase = DiseasePhase.Symptomatic, InfectionThreshold = 1, Burden = .5 }] };
        var clean = Kernel.Treat(state, "clean", 100);
        Require(clean.Accepted); Near(.16, clean.State.Dirtiness);
        for (var i = 0; i < 2; i++) Near(state.Wounds[i].Contamination * .5, clean.State.Wounds[i].Contamination);
        Require(clean.State.Diseases.SequenceEqual(state.Diseases), "Washing instantly cured established infection.");
        var slower = Kernel.Advance(state, Body, Profile, EnvironmentSample.Neutral, new(0, false, true, false, false, 1, true), 8);
        var healthy = Kernel.Advance(state with { Diseases = [] }, Body, Profile, EnvironmentSample.Neutral, new(0, false, true, false, false, 1, true), 8);
        Require(slower.Accepted && healthy.Accepted && slower.State.Wounds[0].Healing < healthy.State.Wounds[0].Healing);
    }
}
