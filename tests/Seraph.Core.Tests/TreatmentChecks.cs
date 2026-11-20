using System.Text.Json;
using Seraph.Core.Framework;

internal static class TreatmentChecks
{
    private static void Check(bool condition, string message) { if (!condition) throw new Exception(message); }
    private static readonly TreatmentDefinition Tea = new() { Id = "enteric-tea", Group = "enteric", Pathogens = ["giardia"], Symptoms = ["giardia"] };
    private static FrameworkState Dose(FrameworkState state, TreatmentDefinition def, string receipt, double now, long wound = 0)
    {
        var result = Treatments.Apply(state, def, receipt, "vsshmod:medicine-test", def.Route, now, wound);
        Check(result.Accepted, result.Error ?? "Rejected"); return result.State;
    }
    public static void TimingAndCaps()
    {
        var state = Dose(new(), Tea, "one", 0);
        Check(Treatments.Speed(state, "giardia", 59) == 1 && Treatments.Speed(state, "giardia", 60) == 2, "Onset ignored");
        Check(Treatments.Speed(state, "giardia", 960) == 1, "Expired medicine remained active");
        state = Dose(state, Tea with { Id = "other", Group = "other" }, "two", 60);
        state = Dose(state, Tea with { Id = "third", Group = "third" }, "three", 120);
        state = state with { TreatmentClockBioSeconds = 180 };
        Check(Treatments.Speed(state, "giardia") == 2.5 && Treatments.Speed(state, "norovirus") == 1, "Recovery cap/target failed");
        Check(Math.Abs(Treatments.Relief(state, "giardia") - .6) < 1e-9, "Relief cap failed");
        Check(Treatments.Speed(state with { TreatmentEffectsEnabled = false }, "giardia") == 1, "Disabled clinical effects applied medicine");
        var kernel = new FrameworkKernel();
        var infected = kernel.Expose(new(), Pathogens.Definitions["giardia"], 100, 1).State;
        infected = Dose(infected, Tea, "four", 0) with { TreatmentClockBioSeconds = 64 };
        var progressed = kernel.ProgressDiseases(infected, Pathogens.Definitions, 8).State;
        Check(progressed.Diseases.Single().PhaseSeconds == 12, "Step crossing onset used wrong clock");
    }
    public static void RepeatAndPersistence()
    {
        var state = Dose(new(), Tea, "one", 0);
        var refresh = Dose(state, Tea with { Relief = .2 }, "two", 100);
        Check(refresh.Treatments.Length == 1 && refresh.Treatments[0].Definition.Relief == .4
            && refresh.Treatments[0].EndsBioSeconds == 1000, "Refresh stacked potency or duration");
        Check(refresh.Complaints.Any(c => c.Id == "nausea") && refresh.DrugToxicBurden == 0, "Second dose feedback missing");
        refresh = Dose(refresh, Tea, "three", 101);
        Check(refresh.DrugToxicBurden == .2, "Third dose toxicity missing");
        Check(!Treatments.Apply(refresh, Tea, "three", "test", TreatmentRoute.Oral, 101).Accepted, "Replay accepted");
        Check(!Treatments.Apply(refresh, Tea, "wrong", "test", TreatmentRoute.Topical, 101).Accepted, "Wrong route accepted");
        var loaded = JsonSerializer.Deserialize<FrameworkState>(JsonSerializer.Serialize(refresh))!;
        Check(loaded.Validate() is null && loaded.Treatments[0].EndsBioSeconds == 1001 && loaded.TreatmentDoses.Length == 3, "Save lost effects/doses");
        try { JsonSerializer.Deserialize<FrameworkState>("{}"); throw new Exception("Missing release framework state accepted"); }
        catch (JsonException) { }
    }
    public static void LocalWoundsAndCapacity()
    {
        var kernel = new FrameworkKernel(); var state = new FrameworkState();
        for (var i = 0; i < 2; i++) state = kernel.RecordInjury(state, new("cut" + i, WoundKind.Cut, "general", .5, InjuryEvidence.Verified, 0)).State;
        var salve = new TreatmentDefinition { Id = "salve", Group = "salve", Route = TreatmentRoute.Topical, RequiresWound = true,
            Symptoms = ["pain"], WoundRepair = .5, DelaySeconds = 0 };
        var wound = state.Wounds[0].EventSequence;
        var treated = Dose(state, salve, "one", 0, wound);
        Check(Treatments.Relief(treated, "pain", wound) > 0 && Treatments.Relief(treated, "pain", state.Wounds[1].EventSequence) == 0, "Local relief spread");
        Check(!Treatments.Apply(state, salve, "bad", "test", TreatmentRoute.Topical, 0, 999).Accepted, "Missing wound accepted");
        var exposed = new FrameworkState();
        for (var i = 0; i < 16; i++) exposed = kernel.Expose(exposed, new("p" + i, 1, 1, 1, .5), 1e-12, 1).State;
        Check(exposed.Diseases.Length == 16, "Infection capacity was not expanded");
        var definitions = Enumerable.Range(0, 16).ToDictionary(i => "p" + i, i => new DiseaseDefinition("p" + i, 1, 1, 1, .5));
        exposed = exposed with { Diseases = exposed.Diseases.Select(d => d with { SecondsSinceExposure = 21599 }).ToArray() };
        Check(kernel.ProgressDiseases(exposed, definitions, 1).State.Diseases.Length == 0, "Stale exposure not collected");
    }
    public static void ExposureAndSupport()
    {
        var kernel = new FrameworkKernel();
        var def = new DiseaseDefinition("test", 2, 3, 20, .5);
        var defs = new Dictionary<string, DiseaseDefinition> { ["test"] = def };
        var infected = kernel.Expose(new(), def, 100, 1).State;
        var ongoing = new HashSet<string> { "test" };
        var batched = kernel.ProgressDiseases(infected, defs, 8, ongoing).State;
        var split = infected;
        for (var i = 0; i < 8; i++) split = kernel.ProgressDiseases(split, defs, 1, ongoing).State;
        Check(batched.Diseases.SequenceEqual(split.Diseases), "Exposure changed across stage/tick boundaries");
        Check(batched.Diseases[0].PhaseSeconds == 2.25, "Ongoing exposure did not slow only recovery");
        var stopped = kernel.ProgressDiseases(batched, defs, 4).State;
        Check(stopped.Diseases[0].PhaseSeconds == 6.25, "Past exposure still impaired recovery");
        var body = Seraph.Core.State.BodyState.Create();
        var profile = Seraph.Core.State.BodyProfile.Default;
        var support = Tea with { Id = "rest", Group = "rest", Symptoms = ["fatigue"], SleepSupport = .2, DelaySeconds = 0 };
        var state = new FrameworkState { Fatigue = .7 };
        var treated = Dose(state, support, "rest", 0);
        var quality = Treatments.SleepQuality(treated, .5);
        Check(Math.Abs(quality - .7) < 1e-9, "Sedative failed to support observed sleep quality");
        Check(Treatments.SleepQuality(treated with { TreatmentEffectsEnabled = false }, .5) == .5,
            "Disabled treatment improved observed sleep quality");
        var simulation = new Seraph.Core.Simulation.SimulationKernel(profile);
        var sleepy = body with { SleepPressure = .8 };
        var baseline = simulation.Advance(sleepy, Seraph.Core.State.EnvironmentSample.Neutral,
            new() { IsSleeping = true, SleepQuality = .5 }, 8);
        var medicated = simulation.Advance(sleepy, Seraph.Core.State.EnvironmentSample.Neutral,
            new() { IsSleeping = true, SleepQuality = quality }, 8);
        Check(baseline.Accepted && medicated.Accepted && medicated.State.SleepPressure < baseline.State.SleepPressure, "Sleep support did not improve actual sleep");
        var context = new ActivityContext(0, false, true, false, false, 1, true);
        var normal = kernel.Advance(state, body, profile, Seraph.Core.State.EnvironmentSample.Neutral, context, 8);
        var restored = kernel.Advance(treated, body, profile, Seraph.Core.State.EnvironmentSample.Neutral, context, 8);
        Check(normal.Accepted && restored.Accepted && restored.State.Fatigue < normal.State.Fatigue, "Recovery preparation failed seated rest");
        var skin = Treatments.Complain(new(), "dermatitis", "plant-contact", 0, 1800, .8);
        Check(BodyFunctionResolver.Evaluate(skin, body, profile).Mining == BodyFunctionResolver.Evaluate(new(), body, profile).Mining, "Minor skin discomfort crossed an ordinary action stage");
        var giardia = kernel.Expose(new(), Pathogens.Definitions["giardia"], 100, 1).State;
        giardia = giardia with { Diseases = giardia.Diseases.Select(d => d with { Phase = DiseasePhase.Symptomatic, Burden = .8 }).ToArray() };
        var efficiency = ClinicalEffects.DigestionEfficiency(giardia);
        var fed = body with { GutEnergyKJ = 100, GlycogenEnergyKJ = 100 };
        var healthy = simulation.Advance(fed, Seraph.Core.State.EnvironmentSample.Neutral, new(), 8);
        var impaired = simulation.Advance(fed, Seraph.Core.State.EnvironmentSample.Neutral, new() { DigestionEfficiency = efficiency }, 8);
        Check(healthy.Accepted && impaired.Accepted && impaired.Diagnostics.AbsorbedEnergyKJ < healthy.Diagnostics.AbsorbedEnergyKJ
            && impaired.State.GutEnergyKJ > healthy.State.GutEnergyKJ, "Giardia did not slow conserved digestive transfer");
        var care = Dose(giardia, Tea with { DelaySeconds = 0 }, "giardia-care", 0);
        Check(ClinicalEffects.DigestionEfficiency(care) > efficiency, "Targeted treatment failed to relieve malabsorption");
    }
}
