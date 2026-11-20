using Seraph.Core.State;

namespace Seraph.Core.Framework;

/// <summary>Game symptom definitions. Thresholds and rates are balance parameters, not medical diagnoses.</summary>
public sealed record ClinicalEffect(string Id, double Severity, bool Beneficial = false);
public static class ClinicalEffects
{
    public static double Dehydration(BodyState body, BodyProfile profile)
        => Math.Max(0, profile.ReferenceBodyWaterL - body.BodyWaterL) / profile.MassKg;

    public static bool HeatStress(FrameworkState state, BodyState body, BodyProfile profile)
    {
        // Equivalent to available reserve < 800 mL. Compare physical water directly so
        // subtracting a large body-water baseline cannot round the exact boundary below 800.
        var lowWater = body.BodyWaterL < profile.ReferenceBodyWaterL
            - (ReserveDisplay.HydrationCapacityG - 800) / 1000;
        return body.CoreTemperatureC >= 39.5
            || lowWater && body.CoreTemperatureC >= 37.5 + DiseaseSyndromes.FeverSetpointC(state) - 37;
    }

    public static ClinicalEffect[] Evaluate(FrameworkState state, BodyState body, BodyProfile profile)
    {
        var effects = new List<ClinicalEffect>();
        void Add(string id, double severity, bool beneficial = false)
        { if (severity > 0) effects.Add(new(id, Math.Clamp(severity, 0, 1), beneficial)); }
        var water = Hydration.Stage(body, profile);
        Add(water >= 2 ? "dehydration" : "thirst", water / 5d);
        Add("hypothermia", body.CoreTemperatureC <= 35 ? (36 - body.CoreTemperatureC) / 5 : 0);
        Add("heat-illness", HeatStress(state, body, profile) ? (body.CoreTemperatureC - 37) / 4 : 0);
        Add("drowsy", body.SleepPressure >= .7 ? (body.SleepPressure - .5) * 2 : 0);
        Add("exhaustion", state.Fatigue >= .6 ? state.Fatigue : 0);
        Add("ketosis", Ketosis.Symptomatic(state.Clinical.Ketosis) ? state.Clinical.Ketosis : 0);
        Add("pain", Treatments.Pain(state, BloodCirculation.LactateCapacityG(body, profile)));
        Add("smoke-inhalation", SmokeInhalation.Severity(state.SmokeDose));
        Add("carbon-monoxide", state.CarbonMonoxideBurden >= .15 ? state.CarbonMonoxideBurden : 0);
        Add("toxic-exposure", Math.Max(state.ToxicBurden, state.DrugToxicBurden) >= .2 ? Math.Max(state.ToxicBurden, state.DrugToxicBurden) : 0);
        Add("chemical-exposure", state.ChemicalBurden >= .2 ? state.ChemicalBurden : 0);
        Add("fever", DiseaseSyndromes.Severity(state, SymptomKit.Fever));
        Add("common-cold", DiseaseSyndromes.Severity(state, SymptomKit.CommonCold));
        Add("food-poisoning", DiseaseSyndromes.Severity(state, SymptomKit.FoodPoisoning));
        Add("dysentery", DiseaseSyndromes.Severity(state, SymptomKit.Dysentery));
        Add("sepsis", state.Syndromes.Sepsis ? 1 : 0);
        Add("insect-carrier", state.Syndromes.VectorAttached ? .5 : 0);
        Add("blood-toxicity", state.Blood.Toxicity);
        Add(state.Blood.LactateG >= BloodCirculation.LactateCapacityG(body, profile)
            ? "overexertion-lactate" : "overexertion-stamina", state.ExertionExhausted ? 1 : 0);
        Add("renal-hyperactivity", state.Blood.RenalHyperactivity ? 1 : 0);
        Add("lactate-overload", Math.Clamp((state.Blood.LactateG / BloodCirculation.LactateCapacityG(body, profile) - .5) / .5, 0, 1));
        Add("blood-loss", body.BloodVolumeL(profile) < profile.ReferenceBloodVolumeL * .85
            ? 1 - body.BloodVolumeL(profile) / profile.ReferenceBloodVolumeL : 0);
        foreach (var disease in state.Diseases.Where(d => d.Burden > 0)) Add(disease.Pathogen, Treatments.Burden(state, disease));
        foreach (var id in state.Complaints.Select(c => c.Id).Distinct().Where(id => id is not ("nausea" or "abdominal-pain"))) Add(id, Treatments.ComplaintSeverity(state, id));
        foreach (var id in new[] { "nausea", "abdominal-pain" })
            Add(id, SymptomSeverity(state, id));
        Add("anemia", Burden("iron"));
        Add("bcomplex-deficiency", Burden("bcomplex"));
        Add("night-vision", Burden("a"));
        Add("bone-weakness", Burden("calcium"));
        Add("vitaminc-deficiency", Burden("c"));
        Add("repair-deficiency", Burden("iron"));
        return effects.ToArray();

        double Burden(string id) => MicroNutrition.Burden(state.Nutrition, id);
    }

    // A single systemic injury rate uses the strongest cause, preventing correlated double charges.
    public static double DamagePerSecond(FrameworkState state, BodyState body, BodyProfile profile, bool thermalOwned)
    {
        var water = Hydration.Stage(body, profile) == 5 ? .5 / 60 : 0;
        var shock = Math.Clamp((.6 - body.BloodVolumeL(profile) / profile.ReferenceBloodVolumeL) * .5, 0, .3);
        var thermal = thermalOwned ? Math.Clamp(Math.Max(32 - body.CoreTemperatureC, body.CoreTemperatureC - 40) * .03, 0, .3) : 0;
        var toxic = Math.Clamp(Math.Max(state.CarbonMonoxideBurden - .45,
            Math.Max(Math.Max(state.ToxicBurden, state.DrugToxicBurden) - .65, state.ChemicalBurden - .7)) * .12, 0, .1);
        return Math.Max(Math.Max(toxic, Math.Max(0, state.Blood.Toxicity - .5) * .06), Math.Max(water, Math.Max(shock, thermal)));
    }

    public static double EntericBurden(FrameworkState state) => state.Diseases
        .Where(d => Pathogens.Enteric(d.Pathogen)).Select(d => Treatments.Burden(state, d)).DefaultIfEmpty().Max();

    // Dysentery has a fixed acute flow. Other enteric illness retains its existing burden-scaled flow;
    // correlated symptoms select the larger flow rather than draining the same water twice.
    public static double EntericWaterLossLPerGameHour(FrameworkState state, double ordinaryRate)
    {
        var dysentery = state.Diseases.Any(d => d.Pathogen == "shigella" && d.Phase == DiseasePhase.Symptomatic)
            ? .5 * (1 - Treatments.Relief(state, "shigella")) : 0;
        return Math.Max(EntericBurden(state) * ordinaryRate, dysentery);
    }

    // Slower transfer leaves fuel and its associated nutrients in the gut, preserving both ledgers.
    public static double DigestionEfficiency(FrameworkState state) => 1 - state.Diseases
        .Where(d => d.Pathogen == "giardia").Select(d => Treatments.Burden(state, d) * .4).DefaultIfEmpty().Max();

    public static double SymptomSeverity(FrameworkState state, string symptom)
    {
        var infection = state.Diseases.Select(d => Treatments.Burden(state, d) * (symptom switch {
            "nausea" => d.Pathogen switch { "norovirus" => .9, "salmonella" or "trichinosis" => .7, "ecoli" => .4, "giardia" => .3, _ => 0 },
            "abdominal-pain" => d.Pathogen switch { "shigella" => .9, "ecoli" => .7, "giardia" => .4, "salmonella" or "trichinosis" => .5, _ => 0 },
            _ => 0
        })).DefaultIfEmpty().Max() * (1 - Treatments.Relief(state, symptom));
        return Math.Max(infection, Math.Max(Treatments.ComplaintSeverity(state, symptom),
            symptom == "nausea" ? Treatments.ComplaintSeverity(state, "plant-poison") * .8 * (1 - Treatments.Relief(state, symptom)) : 0));
    }
}

public static class WoundCare
{
    // Baseline 10.2 mL/game minute per accepted HP, increased by 10% for new wounds.
    public static double InitialBleeding(double acceptedHp) => Math.Clamp(acceptedHp * 11.22 / 60000, 0, .01);
    public static bool Open(WoundKind kind, double bleedLps) => bleedLps > 0 || kind is WoundKind.Cut or WoundKind.Puncture
        or WoundKind.Burn or WoundKind.Frostbite or WoundKind.ElectricBurn or WoundKind.PoisonWound;
    public static long SelectForCare(FrameworkState state) => state.Wounds.Where(w => w.Healing < 1 && w.BandageQuality < 1)
        .OrderByDescending(w => Rate(w)).ThenByDescending(BodyFunctionResolver.Remaining).ThenBy(w => w.EventSequence)
        .Select(w => w.EventSequence).FirstOrDefault();
    // Gameplay thresholds, not medical cleanliness measurements. Recently washed wounds do not loop.
    public static bool NeedsBodyWash(FrameworkState state, double residue = 0) => state.Dirtiness >= .1
        && state.Dirtiness > residue + .001;
    public static bool NeedsWoundWash(FrameworkState state, double bioSeconds, double residue = 0)
        => state.Wounds.Any(w => w.Healing < 1 && Open(w.Kind, w.BleedLps) && w.Contamination >= .1
            && w.Contamination > residue + .001 && w.CleanUntilBioSeconds <= bioSeconds);
    public static bool NeedsWash(FrameworkState state, double bioSeconds, double residue = 0)
        => NeedsBodyWash(state, residue) || NeedsWoundWash(state, bioSeconds, residue);
    public static double Rate(Wound wound, long pressedWound = 0) => wound.BleedLps * (1 - wound.Healing)
        * (1 - wound.BandageQuality) * (pressedWound == wound.EventSequence ? .1 : 1);
    public static long Select(FrameworkState state) => state.Wounds.Where(w => Rate(w) > 0)
        .OrderByDescending(w => Rate(w)).ThenBy(w => w.EventSequence).Select(w => w.EventSequence).FirstOrDefault();
    public static FrameworkState Bandage(FrameworkState state, long woundId, bool antiseptic)
        => state with { Wounds = state.Wounds.Select(w => w.EventSequence != woundId ? w : w with {
            BandageQuality = 1, Contamination = antiseptic ? w.Contamination * .3 : w.Contamination,
            Pathogens = antiseptic ? PathogenLoads.Scale(w.Pathogens, .3) : w.Pathogens }).ToArray() };

    public static FrameworkState BandageAll(FrameworkState state, bool antiseptic)
    {
        var ids = state.Wounds.Where(w => w.Healing < 1 && w.BandageQuality < 1).Select(w => w.EventSequence).ToHashSet();
        return ids.Count == 0 ? state : state with { Wounds = state.Wounds.Select(w => !ids.Contains(w.EventSequence) ? w : w with {
            BandageQuality = 1, Contamination = antiseptic ? w.Contamination * .3 : w.Contamination,
            Pathogens = antiseptic ? PathogenLoads.Scale(w.Pathogens, .3) : w.Pathogens }).ToArray() };
    }
}

public static class SoapCare
{
    public static FrameworkState CleanAll(FrameworkState state, double bioSeconds, double protectionSeconds = 1800)
    {
        if (!double.IsFinite(bioSeconds) || !double.IsFinite(protectionSeconds) || protectionSeconds < 0)
            return state;
        return state with
        {
            Dirtiness = 0,
            CleanRemainingGameHours = Math.Max(state.CleanRemainingGameHours, 24),
            SkinPathogens = PathogenLoads.Scale(state.SkinPathogens, .5),
            Wounds = state.Wounds.Select(w => w.Healing < 1 && WoundCare.Open(w.Kind, w.BleedLps)
                ? w with { Contamination = w.Contamination * .5, Pathogens = PathogenLoads.Scale(w.Pathogens, .5),
                    CleanUntilBioSeconds = Math.Max(w.CleanUntilBioSeconds, bioSeconds + protectionSeconds) }
                : w).ToArray()
        };
    }

}
