using System.Text.Json;
using Seraph.Core.Framework;
using Seraph.Core.Simulation;
using Seraph.Core.State;

internal static class DiseaseChecks
{
    private static void Check(bool value, string message) { if (!value) throw new Exception(message); }
    private static void Near(double a, double b) => Check(Math.Abs(a - b) < 1e-8, $"Expected {a}, got {b}.");
    private static FrameworkState Sick(string id) => FrameworkState.Create("syndrome-test") with {
        Diseases = [new() { Pathogen = id, Phase = DiseasePhase.Symptomatic, Burden = .7, InfectionThreshold = 1 }] };

    public static void Sources()
    {
        var bacterial = new[] { "qfever", "tularemia", "histoplasmosis", "pasteurella", "sporotrichosis", "murinetyphus",
            "malaria", "trichinosis", "anthrax", "tetanus", "salmonella", "shigella" };
        var legacy = new[] { "norovirus", "giardia", "ecoli", "wound-staph", "respiratory", "skin-fungal", "water-enteric", "wound-local" };
        var kernel = new FrameworkKernel(); var state = FrameworkState.Create("catalog");
        foreach (var id in bacterial.Concat(legacy))
        {
            Check(Pathogens.Definitions.TryGetValue(id, out var def) && def.Validate() is null, "Missing disease " + id);
            var result = kernel.Expose(state, def!, 100, 1, "verified-test");
            Check(result.Accepted, result.Error ?? id); state = result.State;
        }
        Check(state.Diseases.Length == 20 && state.Validate() is null, "Disease capacity lost an existing infection.");
        var smoke = DiseaseExposure.Advance(kernel, new(), EnvironmentSample.Neutral with { AirPollution = 1 }, 8, new(), new HashSet<string>());
        Check(smoke.Diseases.Length == 0, "Smoke fabricated pathogens.");
        var ongoing = new HashSet<string>();
        var agents = new Dictionary<string, double> { ["qfever"] = .1, ["anthrax"] = .1, ["histoplasmosis"] = .05 };
        var exposure = DiseaseExposure.Advance(kernel, new(), EnvironmentSample.Neutral with { DiseaseSources = agents }, 8, new(), ongoing);
        Check(exposure.Diseases.Length == 0 && exposure.Barriers.Ambient.Keys.ToHashSet().SetEquals(["qfever", "histoplasmosis"]),
            "Airborne source clocks should run independently of skin dirtiness.");
        var dirty = new FrameworkState { Dirtiness = .5, Barriers = new() { Ambient = agents.Keys.ToDictionary(k => k, k => new AmbientExposureClock(0, 1e-6)) } };
        exposure = DiseaseExposure.Advance(kernel, dirty, EnvironmentSample.Neutral with { DiseaseSources = agents }, 8, new(), new HashSet<string>());
        Check(exposure.Diseases.Length == 0 && exposure.Barriers.Inhaled.Keys.ToHashSet().SetEquals(["qfever", "histoplasmosis"])
            && exposure.Barriers.Skin.Keys.ToHashSet().SetEquals(["anthrax"]), "Identified ambient agents bypassed route barriers.");
        var contact = EnvironmentSample.Neutral with { DiseaseSources = new Dictionary<string, double> {
            ["shigella"] = 1, ["malaria"] = 1 } };
        exposure = DiseaseExposure.Advance(kernel, dirty, contact, 8, new(), new HashSet<string>());
        Check(exposure.Barriers.Ingested.Count == 0 && exposure.Diseases.Length == 0,
            "Contact swallowed enteric agents or fabricated vector bites.");
        Check(DiseaseExposure.AnimalBitePathogen("game:wolf-male") == "pasteurella"
            && DiseaseExposure.AnimalBitePathogen("game:hare-female") == "sporotrichosis"
            && DiseaseExposure.AnimalBitePathogen("mod:werewolf") is null, "Animal source classification mismatch.");
        Check(!Pathogens.Fecal("trichinosis") && Pathogens.Fecal("shigella"), "Trichinella incorrectly shed as fecal disease.");
        var saved = JsonSerializer.Deserialize<FrameworkState>(JsonSerializer.Serialize(state))!;
        Check(saved.Validate() is null && saved.Diseases.Length == 20, "Disease catalog failed persistence.");
    }

    public static void FoodAndWater()
    {
        var profile = BodyProfile.Default; var body = BodyState.Create() with { GutEnergyKJ = 1000, GutWaterL = .5 };
        var f = Sick("salmonella"); var pending = new double[Nutrients.All.Length]; pending[Nutrients.IndexOf("protein")] = 30;
        f = f with { Nutrition = f.Nutrition with { GutMassG = 500, GutFoodWaterG = 300, Pending = pending, StomachWasteG = 170 } };
        var first = DiseaseSyndromes.Settle(f, body, profile, 0);
        Near(700, first.State.Syndromes.BowelContentsG); Near(0, first.Body.GutEnergyKJ); Near(0, first.Body.GutWaterL);
        Near(body.BodyWaterL, first.Body.BodyWaterL); Near(0, first.State.Nutrition.Pending.Sum());
        Near(0, first.State.Nutrition.StomachWasteG);
        var emptyDigest = DietaryMetabolism.Digest(first.State.Nutrition, first.Body, profile, 8, 1, 5000);
        Near(0, first.State.Nutrition.BowelResidueG(emptyDigest.Nutrition));
        Check(first.State.Diseases[0].StomachDumped && !f.Diseases[0].StomachDumped && f.Nutrition.Pending.Sum() == 30, "First stomach diversion mutated its input.");
        var saved = JsonSerializer.Deserialize<FrameworkState>(JsonSerializer.Serialize(first.State))!;
        var meal = first.Body with { GutEnergyKJ = 1000, GutWaterL = .2 };
        var after = saved with { Nutrition = saved.Nutrition with { GutMassG = 500, GutFoodWaterG = 200, Pending = pending, StomachWasteG = 270 } };
        var divided = DiseaseSyndromes.DivertMeal(saved, after, meal, first.Body);
        Near(1125, divided.State.Syndromes.BowelContentsG); Near(.03, divided.Body.GutWaterL); Near(150, divided.Body.GutEnergyKJ);
        Near(4.5, divided.State.Nutrition.Pending.Sum()); Near(75, divided.State.Nutrition.ContentsMassG(divided.Body.GutWaterL));
        Near(40.5, divided.State.Nutrition.StomachWasteG);
        Near(45, divided.State.Nutrition.Pending.Take(4).Sum() + divided.State.Nutrition.StomachWasteG);
        var again = DiseaseSyndromes.Settle(divided.State, divided.Body, profile, 0);
        Near(1125, again.State.Syndromes.BowelContentsG); Near(150, again.Body.GutEnergyKJ);
        var full = DiseaseSyndromes.Transit(again.State, 8, 0); Near(1125, full.State.Syndromes.BowelContentsG); Near(0, full.BowelG);
        var partial = DiseaseSyndromes.Transit(full.State, 8, 10); Near(1115, partial.State.Syndromes.BowelContentsG); Near(10, partial.BowelG);
        Near(.5, ClinicalEffects.EntericWaterLossLPerGameHour(Sick("shigella"), .5));
        Near(.35, ClinicalEffects.EntericWaterLossLPerGameHour(Sick("salmonella"), .5));
        var simulation = new SimulationKernel(profile);
        var water = simulation.Advance(BodyState.Create(), EnvironmentSample.Neutral, new() { EntericWaterLossLps = .5 / 3600 }, 8);
        Check(water.Accepted, water.Error ?? "Enteric step failed");
        var diarrhea = DiseaseSyndromes.Settle(Sick("shigella"), water.State, profile, water.Diagnostics.EntericWaterLossL);
        Near(water.State.BodyWaterL, diarrhea.Body.BodyWaterL);
        Near(water.Diagnostics.EntericWaterLossL * 1000, diarrhea.State.Syndromes.BowelContentsG);
        Check(JsonSerializer.Deserialize<FrameworkState>(JsonSerializer.Serialize(FrameworkState.Create("release-defaults")))!.Syndromes.Validate() is null, "Release syndrome defaults invalid.");
    }

    public static void FeverAndSepsis()
    {
        var p = BodyProfile.Default; var body = BodyState.Create(); var fever = Sick("qfever");
        var target = DiseaseSyndromes.FeverSetpointC(fever); Check(target > 37 && target <= 39, "No febrile thermal target.");
        var kernel = new SimulationKernel(p);
        var normal = kernel.Advance(body, EnvironmentSample.Neutral, new(), 8);
        var sick = kernel.Advance(body, EnvironmentSample.Neutral, new() { FeverTemperatureRiseC = target - 37 }, 8);
        Check(sick.Accepted && sick.Diagnostics.ShiveringEnergyJ > normal.Diagnostics.ShiveringEnergyJ, "Fever did not reach thermoregulation.");
        var safe = DiseaseSyndromes.Settle(fever, body, p, 0); Check(!safe.State.Syndromes.Sepsis, "Safe infection fabricated sepsis.");
        var dehydrated = body with { BodyWaterL = p.ReferenceBodyWaterL - 8 };
        var dangerous = DiseaseSyndromes.Settle(fever, dehydrated, p, 0, 7200);
        Check(!dangerous.State.Syndromes.Sepsis, "Unrelated fever fabricated sepsis.");
        var direct = DiseaseSyndromes.DirectExposure(new(), fever, "pasteurella", .8, "animal-bite");
        Check(!direct.Syndromes.Sepsis && direct.Diseases.Any(d => d.Pathogen == "pasteurella"), "Exposure directly created sepsis.");
        var severe = Sick("wound-staph");
        var first = DiseaseSyndromes.Settle(severe, dehydrated, p, 0, 3600).State;
        Check(!first.Syndromes.Sepsis, "Sepsis onset ignored sustained duration.");
        first = JsonSerializer.Deserialize<FrameworkState>(JsonSerializer.Serialize(first))!;
        var septic = DiseaseSyndromes.Settle(first, dehydrated, p, 0, 3600).State;
        var whole = DiseaseSyndromes.Settle(severe, dehydrated, p, 0, 7200).State;
        Check(septic.Syndromes.Sepsis && whole.Syndromes == septic.Syndromes, "Sepsis duration changed across reload/partitions.");
        Check(ClinicalEffects.Evaluate(septic, dehydrated, p).Any(e => e.Id == "sepsis"), "Sepsis not projected as a symptom.");
        var interrupted = DiseaseSyndromes.Settle(first, body, p, 0, 1).State;
        Check(!DiseaseSyndromes.Settle(interrupted, dehydrated, p, 0, 3600).State.Syndromes.Sepsis,
            "Interrupted danger counted as continuous onset.");
        var recovering = DiseaseSyndromes.Settle(septic with { Diseases = [] }, body, p, 0, 3600).State;
        Check(recovering.Syndromes.Sepsis, "Sepsis cleared without its recovery duration.");
        Check(!DiseaseSyndromes.Settle(recovering, body, p, 0, 3600).State.Syndromes.Sepsis, "Resolved sepsis did not clear.");
        var masked = severe with { Treatments = [new(new() { Id = "test-relief", Group = "relief", Symptoms = ["wound-staph"], Relief = .6 }, "test", 0, 0, 10000)] };
        Check(DiseaseSyndromes.HasSepsisInfection(masked), "Symptom relief hid the underlying septic infection.");
    }

    public static void Vectors()
    {
        var kernel = new FrameworkKernel(); var options = new DiseaseExposureOptions();
        var start = FrameworkState.Create("vectors") with { Dirtiness = 1, Syndromes = new() { VectorThreshold = .00002 } };
        var whole = DiseaseSyndromes.TickVector(kernel, start, 8, true, false, options);
        var half = DiseaseSyndromes.TickVector(kernel, start, 4, true, false, options);
        half = JsonSerializer.Deserialize<FrameworkState>(JsonSerializer.Serialize(half))!;
        var split = DiseaseSyndromes.TickVector(kernel, half, 4, true, false, options);
        Near(whole.Syndromes.VectorHours, split.Syndromes.VectorHours);
        Check(whole.Syndromes.VectorAttached && split.RandomState == whole.RandomState, "Landing changed across partitions/reload.");
        Check(whole.Syndromes.VectorPathogen == split.Syndromes.VectorPathogen, "Landing infection was rerolled on reload.");
        start = start with { Syndromes = new() { VectorAttached = true, VectorHours = 1.999, VectorPathogen = "murinetyphus" } };
        whole = DiseaseSyndromes.TickVector(kernel, start, 8, true, false, options);
        half = DiseaseSyndromes.TickVector(kernel, start, 4, true, false, options);
        split = DiseaseSyndromes.TickVector(kernel, half, 4, true, false, options);
        Check(whole.Diseases.Length == 1 && whole.Diseases[0].Pathogen == split.Diseases[0].Pathogen
            && whole.RandomState == split.RandomState, "Bite disease draw changed with partitions.");
        Near(whole.Syndromes.VectorHazard, split.Syndromes.VectorHazard);
        Near(whole.Diseases[0].CumulativeHazard, split.Diseases[0].CumulativeHazard);
        Near(options.VectorBiteHazard * ImmuneSystem.Susceptibility(start), whole.Diseases[0].CumulativeHazard);
        Check(DiseaseSyndromes.TickVector(kernel, start with { Syndromes = start.Syndromes with { VectorPathogen = null } },
            8, true, false, options).Diseases.Length == 0, "Noninfectious/legacy insect fabricated infection.");
        var guaranteed = options with { VectorInfectiousChance = 1 };
        var landing = FrameworkState.Create("landing") with { Syndromes = new() { VectorThreshold = .000001 } };
        Check(DiseaseSyndromes.TickVector(kernel, landing, 8, true, false, guaranteed).Syndromes.VectorPathogen is null,
            "A vector acquired a pathogen without an explicit carrier source.");
        var vectorSources = new Dictionary<string, double> { ["malaria"] = .2, ["murinetyphus"] = .8 };
        var sourced = DiseaseSyndromes.TickVector(kernel, landing, 8, true, false, guaranteed, vectorSources);
        Check(sourced.Syndromes.VectorPathogen is "malaria" or "murinetyphus",
            "A vector failed to acquire an explicitly present pathogen.");
        Check(DiseaseSyndromes.TickVector(kernel, landing, 8, true, false,
            options with { VectorInfectiousChance = 0 }, vectorSources).Syndromes.VectorPathogen is null,
            "The configured vector acquisition chance was ignored.");
        var washed = DiseaseSyndromes.TickVector(kernel, start, 8, true, true, options);
        Check(!washed.Syndromes.VectorAttached && washed.Diseases.Length == 0 && washed.Syndromes.VectorHours == 0, "Washing failed to prevent bite.");
        var bite = DiseaseSyndromes.TickVectorWithEvents(kernel, start, 8, false, false, options);
        Check(bite.InsectBites == 1, "Actual infectious bite did not produce one observation.");
        var sterile = DiseaseSyndromes.TickVectorWithEvents(kernel,
            start with { Syndromes = start.Syndromes with { VectorPathogen = null } }, 8, false, false, options);
        Check(sterile.InsectBites == 1 && sterile.State.Diseases.Length == 0, "Sterile bite was missed or diagnosed as infection.");
        Check(DiseaseSyndromes.TickVectorWithEvents(kernel, start, 8, false, true, options).InsectBites == 0,
            "Washing removal fabricated a bite.");
        var a = DiseaseSyndromes.TickVectorWithEvents(kernel, start, 2, false, false, options);
        var restored = JsonSerializer.Deserialize<FrameworkState>(JsonSerializer.Serialize(a.State))!;
        var b = DiseaseSyndromes.TickVectorWithEvents(kernel, restored, 6, false, false, options);
        Check(a.InsectBites + b.InsectBites == bite.InsectBites
            && DiseaseSyndromes.TickVectorWithEvents(kernel, b.State, 8, false, false, options).InsectBites == 0,
            "Partition/reload lost a bite or replayed an old event.");
    }

    public static void RespiratoryTransmission()
    {
        var nearby = DiseaseExposure.RespiratoryContactUnits(1, 60, 0, .8, .005);
        var distant = DiseaseExposure.RespiratoryContactUnits(9, 60, 0, .8, .005);
        var ventilated = DiseaseExposure.RespiratoryContactUnits(1, 60, 1, .8, .005);
        var masked = DiseaseExposure.RespiratoryContactUnits(1, 60, 0, .8, .005, .2, .3);
        Check(nearby > 0 && distant == 0 && ventilated < nearby && masked < nearby,
            "Respiratory dose ignored distance, ventilation or declared equipment protection.");
        Check(DiseaseExposure.RespiratoryContactUnits(1, 0, 0, 1, .005) == 0
            && DiseaseExposure.RespiratoryContactUnits(double.NaN, 10, 0, 1, .005) == 0,
            "Invalid or zero-duration respiratory contact produced exposure.");
    }
}
