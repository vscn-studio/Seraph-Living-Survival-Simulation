using System.Text.Json;
using Seraph.Core.Framework;
using Seraph.Core.Simulation;
using Seraph.Core.State;

internal static class BodyFunctionChecks
{
    private static readonly BodyProfile Profile = BodyProfile.Default;
    private static readonly BodyState Body = BodyState.Create();
    private static BodyFunctions Resolve(FrameworkState state, BodyState? body = null)
        => BodyFunctionResolver.Evaluate(state, body ?? Body, Profile);
    private static void Check(bool condition, string reason)
    { if (!condition) throw new Exception(reason); }
    private static void Near(double a, double b)
    { Check(Math.Abs(a - b) < 1e-9, $"Expected {a}, got {b}."); }

    public static void StableAndUseful()
    {
        var healthy = new FrameworkState { OxygenKnown = true };
        var baseline = Resolve(healthy);
        var loaded = Resolve(healthy with { Blood = new() { LactateG = 75 } });
        Near(1, loaded.Manipulation); Near(1, loaded.Mining); Near(1, loaded.Sprint);
        Near(baseline.Mobility, loaded.Mobility);
        Check(loaded.ManipulationReason == "normal" && loaded.BowReason == "normal"
            && loaded.AttackReason == "normal" && loaded.SprintReason == "normal", "Lactate affected non-running actions.");
        var fatigued = Resolve(healthy with { Fatigue = .75 });
        Near(.925, fatigued.Mining); Near(.9625, fatigued.Mobility);
        Check(fatigued.ManipulationReason == "fatigue", "Fatigue slowdown lacked an explanation.");
        Near(.36, baseline.Dodge); Near(.51, CombatFunctions.FacingDodge(baseline.Dodge, 1));
        Near(.26, CombatFunctions.FacingDodge(baseline.Dodge, -1));
        Near(Body.FatEnergyKJ / (9 * 4.184 * 1000) * .01, baseline.FatDamageReduction); Near(0, baseline.MuscleMeleeBonus);
        Near(.5, Resolve(healthy with { Vitals = healthy.Vitals.Change(-15, 2) }).Manipulation);
        Near(Resolve(healthy with { Vitals = healthy.Vitals.Change(-10, 1) }).Circulation,
            Resolve(healthy with { Vitals = healthy.Vitals.Change(-10, 1) }).Filtration);
        Near(Resolve(healthy, Body with { SleepPressure = 1 }).Dodge, baseline.Dodge);
        foreach (var stamina in new[] { .35, .8, 1 })
        foreach (var oxygen in new[] { .1, .2, .6, 1 })
        {
            var reserve = Resolve(healthy with { Stamina = stamina, OxygenAvailability = oxygen });
            Check(reserve == baseline, "Neutral reserves changed unrelated body functions.");
        }
        var overlapping = Resolve(healthy with { Stamina = .05, Fatigue = .8, Blood = new() { LactateG = 75 } },
            Body with { SleepPressure = .95 });
        Near(.45, overlapping.Mining); Near(.9, overlapping.Sprint); Near(1, overlapping.Jump);
        Check(overlapping.ManipulationReason == "stamina", "Overlapping loads did not report the strongest limit.");
        var depletedFuel = Resolve(healthy with { Stamina = .05, Fatigue = .8 }, Body with { GlycogenEnergyKJ = 0 });
        Near(.45, depletedFuel.Mining);
        var unconscious = Resolve(healthy with { HasAir = false, OxygenAvailability = 0 });
        Check(unconscious.Consciousness == 0 && !unconscious.CanUseActiveHand && !unconscious.CanSprint,
            "Actual asphyxia did not affect consciousness and action permission.");
        Check(Resolve(healthy with { Vitals = healthy.Vitals.Change(-20, 0) }).Consciousness == 0,
            "Confirmed head integrity loss has no effect.");
        var leg = Resolve(healthy with { Vitals = healthy.Vitals.Change(-15, 4) });
        Near(.78, leg.Mobility);
        Check(!leg.CanJump && leg.Sprint < leg.Mobility, "Destroyed leg did not restrict takeoff/sprint.");
        var lowBlood = Body with { RbcVolumeL = Body.RbcVolumeL * .45, PlasmaWaterL = Body.PlasmaWaterL * .45 };
        var circulation = Resolve(healthy, lowBlood);
        Check(circulation.Circulation < .4 && circulation.Consciousness < 1 && circulation.Endurance < 1,
            "Severe actual blood loss did not reach the circulation chain.");
        var chest = healthy with { Vitals = healthy.Vitals.Change(-12, 1) };
        var impaired = Resolve(chest);
        Check(impaired.RespiratoryCapacity < baseline.RespiratoryCapacity && impaired.Digestion < 1,
            "Torso integrity did not reach respiratory and digestive functions.");
        Check(Exertion.Apply(1, 1, ExertionKind.Mining, 8, true, false, false,
            respiratoryCapacity: impaired.RespiratoryCapacity).Oxygen
            == Exertion.Apply(1, 1, ExertionKind.Mining, 8, true, false, false).Oxygen,
            "Exercise must not debit the native oxygen reserve twice.");
        var illness = healthy with { Diseases = [new() {
            Pathogen = "giardia", Phase = DiseasePhase.Symptomatic, Burden = .7 }] };
        var body = Body with { GutEnergyKJ = 1000 };
        var kernel = new SimulationKernel(Profile);
        var normal = kernel.Advance(body, EnvironmentSample.Neutral, new(), 8);
        var sick = kernel.Advance(body, EnvironmentSample.Neutral,
            new() { DigestionEfficiency = Resolve(illness, body).Digestion }, 8);
        Check(sick.Accepted && sick.Diagnostics.AbsorbedEnergyKJ < normal.Diagnostics.AbsorbedEnergyKJ,
            "Displayed digestion did not change actual absorption.");
        var adrenal = Resolve(healthy with { AdrenalineLevel = 1, AdrenalineRemainingSeconds = 20 });
        Near(1.25, adrenal.Consciousness); Near(1.5, adrenal.Mobility);
        Near(baseline.Attack, adrenal.Attack); Near(1, adrenal.Sprint);
        var fatiguedMobilityBase = Resolve(healthy with { Fatigue = .8 });
        var fatiguedAdrenal = Resolve(healthy with { Fatigue = .8, AdrenalineLevel = 1, AdrenalineRemainingSeconds = 20 });
        Near(fatiguedMobilityBase.Mobility * 1.5, fatiguedAdrenal.Mobility);
        foreach (var (remaining, expected) in new[] { (.81, 1d), (.8, 1d), (.79, .9), (.6, .9), (.59, .75), (.4, .75), (.39, .5), (.2, .5), (.19, .25) })
            Near(expected, BodyFunctionResolver.ActionStage(remaining));
        Near(0, RestFeedback.FatigueMovementPenalty(.6)); Near(.05, RestFeedback.FatigueMovementPenalty(.8));
        Near(.10, RestFeedback.FatigueMovementPenalty(.9)); Near(.15, RestFeedback.FatigueMovementPenalty(1));
        var handFree = healthy with { Vitals = healthy.Vitals.Change(-8, 2) };
        Near(0, BodyFunctionResolver.StructuralDamage(handFree, "left-hand"));
        var locatedChest = healthy with { Vitals = healthy.Vitals.Change(-12, 1), Wounds = [new() {
            Region = "chest", Severity = .6, IntegrityDamage = 12 }] };
        Near(1, Resolve(locatedChest).Digestion);
        Check(adrenal.RespiratoryReason == "normal" && adrenal.CirculationReason == "normal",
            "Unaffected organ functions claimed adrenaline support.");
    }

    public static void InfectionAndRecovery()
    {
        var kernel = new FrameworkKernel();
        var healthy = FrameworkState.Create("immune-response");
        var weak = healthy with { ImmuneReserve = .2 };
        var def = new DiseaseDefinition("giardia", 10, 10, 10, .5);
        var defs = new Dictionary<string, DiseaseDefinition> { [def.Id] = def };
        var clean = kernel.Expose(healthy, def, .001, 1).State;
        var vulnerable = kernel.Expose(weak, def, .001, 1).State;
        Check(vulnerable.Diseases[0].CumulativeHazard > clean.Diseases[0].CumulativeHazard,
            "Effective immunity did not change accepted infection hazard.");
        Near(clean.Diseases[0].InfectionThreshold, vulnerable.Diseases[0].InfectionThreshold);
        var dirty = kernel.Expose(healthy with { Dirtiness = 1, AirPollution = 1 }, def, .001, 1).State;
        Near(clean.Diseases[0].CumulativeHazard, dirty.Diseases[0].CumulativeHazard);
        Near(ImmuneSystem.Support(healthy, Body, Profile),
            ImmuneSystem.Support(healthy with { Dirtiness = 1 }, Body, Profile));
        Check(ImmuneSystem.Support(healthy with { Fatigue = 1 }, Body, Profile)
            < ImmuneSystem.Support(healthy, Body, Profile), "Rest has no slow immune support.");
        Check(ImmuneSystem.Duration(weak, def, DiseasePhase.Incubating) < def.IncubationSeconds
            && ImmuneSystem.Duration(weak, def, DiseasePhase.Symptomatic) > def.SymptomaticSeconds,
            "Weak immunity incorrectly delayed symptoms or shortened illness.");
        var start = healthy with { Diseases = [new() { Pathogen = def.Id, Phase = DiseasePhase.Recovering,
            InfectionThreshold = 1, PhaseSeconds = 8, Burden = .1 }] };
        var recovered = kernel.ProgressDiseases(start, defs, 3).State;
        var prolonged = kernel.ProgressDiseases(start with { ImmuneReserve = .2 }, defs, 3).State;
        Check(recovered.Diseases.Length == 0 && recovered.ImmuneMemory[def.Id] == .5
            && prolonged.Diseases.Single().Phase == DiseasePhase.Recovering,
            "Effective immunity did not change actual recovery or memory required no recovery.");
        var saved = JsonSerializer.Deserialize<FrameworkState>(JsonSerializer.Serialize(vulnerable))!;
        Near(ImmuneSystem.Effective(vulnerable), ImmuneSystem.Effective(saved));
        Check(saved.RandomState == vulnerable.RandomState
            && saved.Diseases[0].InfectionThreshold == vulnerable.Diseases[0].InfectionThreshold
            && saved.ImmuneMemory.Count == 0, "Reload rerolled infection or granted memory for a microdose.");
        var entire = kernel.ProgressDiseases(start with { ImmuneReserve = .2 }, defs, 8).State;
        var split = kernel.ProgressDiseases(kernel.ProgressDiseases(start with { ImmuneReserve = .2 }, defs, 3).State, defs, 5).State;
        Check(entire.Diseases.Length == split.Diseases.Length, "Disease partitions changed recovery.");
        Near(entire.ImmuneMemory.GetValueOrDefault(def.Id), split.ImmuneMemory.GetValueOrDefault(def.Id));
    }
}
