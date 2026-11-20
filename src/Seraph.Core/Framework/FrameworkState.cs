using Seraph.Core.State;

namespace Seraph.Core.Framework;

// Append only: numeric values are part of the save contract. Structural diagnoses require explicit evidence.
public enum WoundKind { Generic, Cut, Puncture, Burn, Contusion, Fracture, Sprain, Frostbite, ElectricBurn, PoisonWound }
public enum InjuryEvidence { LegacyUnknown, Unlocated, GeometricEstimate, GameplayDistribution, Verified, CapacityAggregate }
public sealed record DownedState
{
    public bool Active { get; init; }
    public double ProjectedHealth { get; init; } = .1;
    public string? Validate() => BodyProfile.In(ProjectedHealth, 0.001, VitalState.MaximumWithMuscle) ? null : "Invalid downed health projection.";
}

public enum DiseasePhase { Exposed, Incubating, Symptomatic, Recovering }

public sealed record Wound
{
    public long EventSequence { get; init; }
    public WoundKind Kind { get; init; }
    public string Region { get; init; } = "general";
    public double Severity { get; init; }
    public InjuryEvidence Evidence { get; init; }
    public double OccurredBioSeconds { get; init; }
    public double? TissueDamage { get; init; }
    /// <summary>Actual native HP loss; null preserves legacy severity-based wounds.</summary>
    public double? IntegrityDamage { get; init; }
    public double? PainIntensity { get; init; }
    public PainPools? PainPools { get; init; }
    public double BleedLps { get; init; }
    // Litres per biological second. Legacy wounds retain a zero natural floor.
    public double InitialBleedLps { get; init; }
    public double NaturalClotFloorLps { get; init; }
    public double BandageQuality { get; init; }
    public double Contamination { get; init; }
    public IReadOnlyDictionary<string, double> Pathogens { get; init; } = PathogenLoads.Empty;
    public double Healing { get; init; }
    public double PressureUntilBioSeconds { get; init; }
    public double CleanUntilBioSeconds { get; init; }
}

public sealed record DiseaseState
{
    public string Pathogen { get; init; } = "";
    public DiseasePhase Phase { get; init; }
    public double CumulativeHazard { get; init; }
    public double InfectionThreshold { get; init; }
    public double PhaseSeconds { get; init; }
    public bool StomachDumped { get; init; }
    public double Burden { get; init; }
    public double SecondsSinceExposure { get; init; }
    public string Source { get; init; } = "legacy-exposure";
}

public sealed record ChronicEffect(string SourceEvent, string Kind, double Severity);

public sealed record ClinicalIndicators
{
    public double HeartRateBpm { get; init; } = 72;
    public double OxygenSaturation { get; init; } = .98;
    public double BloodGlucoseMmol { get; init; } = 5.2;
    public double WhiteCellReserve { get; init; } = 1;
    public double Ketosis { get; init; }

    public string? Validate() => !BodyProfile.In(HeartRateBpm, 25, 240)
        || !BodyProfile.In(OxygenSaturation, 0, 1)
        || !BodyProfile.In(BloodGlucoseMmol, 1, 30)
        || !BodyProfile.In(WhiteCellReserve, 0, 1)
        || !BodyProfile.In(Ketosis, 0, 1) ? "Invalid clinical indicators." : null;
}

public static class ClinicalIndicatorsModel
{
    public static ClinicalIndicators Advance(ClinicalIndicators old, FrameworkState frame, BodyState body, BodyProfile profile,
        double seconds, double oxygenAvailability, double activityPowerW)
    {
        var dt = Math.Clamp(seconds, 0, 8);
        var dehydration = Math.Clamp(1 - body.BodyWaterL / Math.Max(1, profile.ReferenceBodyWaterL), 0, 1);
        var stress = Math.Clamp(frame.Pain * .35 + frame.Fatigue * .25 + Math.Max(0, activityPowerW - 100) / 900 + dehydration * .3, 0, 1);
        var infection = frame.Diseases.Length == 0 ? 0 : frame.Diseases.Max(d => d.Burden);
        var carbohydrate = frame.Nutrition.Get("carbohydrate");
        var glucoseTarget = Math.Clamp(5.2 + (carbohydrate.Known ? carbohydrate.Reserve / 1000 : 0), 2.5, 12);
        var alpha = 1 - Math.Exp(-dt / 180d);
        var hr = old.HeartRateBpm + (72 + stress * 100 + infection * 20 - old.HeartRateBpm) * alpha;
        var sat = Math.Clamp(old.OxygenSaturation + (Math.Clamp(oxygenAvailability, 0, 1) - old.OxygenSaturation) * alpha, 0, 1);
        var wbc = Math.Clamp(old.WhiteCellReserve + ((1 - infection * .5) - old.WhiteCellReserve) * alpha, 0, 1);
        return old with { HeartRateBpm = hr, OxygenSaturation = sat, BloodGlucoseMmol = old.BloodGlucoseMmol + (glucoseTarget - old.BloodGlucoseMmol) * alpha,
            WhiteCellReserve = wbc };
    }
}

/// <summary>Versioned, bounded game abstractions. These are not clinical diagnoses.</summary>
public sealed record FrameworkState
{
    [System.Text.Json.Serialization.JsonRequired]
    public int Version { get; init; } = 1;
    public long EventSequence { get; init; }
    public ulong RandomState { get; init; } = 1;
    public double Constitution { get; init; } = 1;
    public double Stamina { get; init; } = 1;
    public bool ExertionExhausted { get; init; }
    public string ExertionReason { get; init; } = "";
    public RecoveryStage ExertionRecoveryStage { get; init; }
    public double Fatigue { get; init; }
    public double SleepBlurSeverity { get; init; }
    public double AlertnessReliefUntilBioSeconds { get; init; }
    /// <summary>Short emergency response. The level is derived from the active window and never a permanent buff.</summary>
    public double AdrenalineLevel { get; init; }
    public double AdrenalineFatigueDebt { get; init; }
    public double AdrenalineSleepDebt { get; init; }
    // Combat windows use loaded, unpaused real seconds.
    public double AdrenalineRemainingSeconds { get; init; }
    public double AdrenalineCooldownRemainingSeconds { get; init; }
    public double Pain { get; init; }
    public double ExerciseLoad { get; init; }
    public double MuscleSoreness { get; init; }
    public double Dirtiness { get; init; }
    public double CleanRemainingGameHours { get; init; }
    public double BathedRemainingGameHours { get; init; }
    public double HeatAdaptation { get; init; }
    public double ColdAdaptation { get; init; }
    /// <summary>Cold exposure clocks by anatomical region; values above one create a frost injury.</summary>
    public IReadOnlyDictionary<string, double> ColdExposureByRegion { get; init; } = new Dictionary<string, double>();
    public double NutritionTrend { get; init; }
    /// <summary>Slow reserve for pathogen response. 1 is well supported, 0 is depleted.</summary>
    public double ImmuneReserve { get; init; } = 1;
    /// <summary>Accumulated ambient pollution exposure, independent of a disease diagnosis.</summary>
    public double AirPollution { get; init; }
    public double SmokeDose { get; init; }
    public double CarbonMonoxideBurden { get; init; }
    public double ToxicBurden { get; init; }
    public double ChemicalBurden { get; init; }
    /// <summary>Sleep surface support; retained as a gameplay quality signal.</summary>
    public double BedQuality { get; init; } = .8;
    public double SleepEfficiency { get; init; } = 1;
    public double EnvironmentNoise { get; init; }
    public double LastActivityPowerW { get; init; }
    public double OxygenAvailability { get; init; } = 1;
    public bool HasAir { get; init; } = true;
    public bool OxygenKnown { get; init; }
    public double PerfusionTrend { get; init; } = 1;
    public double BreathDemandPerMinute { get; init; } = 14;
    public double TotalIntakeWaterL { get; init; }
    public double TotalIntakeEnergyKJ { get; init; }
    public double TotalObservedSaturation { get; init; }
    public Wound[] Wounds { get; init; } = [];
    public DiseaseState[] Diseases { get; init; } = [];
    public ExposureBarrierState Barriers { get; init; } = new();
    public IReadOnlyDictionary<string, double> InfectionThresholdHistory { get; init; } = PathogenLoads.Empty;
    public IReadOnlyDictionary<string, double> ImmuneMemory { get; init; } = PathogenLoads.Empty;
    public IReadOnlyDictionary<string, double> SkinPathogens { get; init; } = PathogenLoads.Empty;
    public IReadOnlyDictionary<string, double> HandPathogens { get; init; } = PathogenLoads.Empty;
    public ChronicEffect[] ChronicEffects { get; init; } = [];
    public string[] RecentReceipts { get; init; } = [];
    public string[] RecentInjuryEvents { get; init; } = [];
    public bool TreatmentEffectsEnabled { get; init; } = true;
    public double TreatmentClockBioSeconds { get; init; }
    public double DrugToxicBurden { get; init; }
    public double AlcoholBurden { get; init; }
    public ActiveTreatment[] Treatments { get; init; } = [];
    public TreatmentDose[] TreatmentDoses { get; init; } = [];
    public Complaint[] Complaints { get; init; } = [];
    public SyndromeState Syndromes { get; init; } = new();
    public BloodState Blood { get; init; } = new();
    [System.Text.Json.Serialization.JsonRequired]
    public NutritionState Nutrition { get; init; } = new();
    [System.Text.Json.Serialization.JsonRequired]
    public VitalState Vitals { get; init; } = new();
    public DownedState Downed { get; init; } = new();
    public ClinicalIndicators Clinical { get; init; } = new();

    public static FrameworkState Create(string identity)
    {
        var seed = DeterministicRandom.Seed(identity);
        var random = new DeterministicRandom(seed);
        return new() { Constitution = 0.95 + random.NextUnit() * 0.1, RandomState = random.State, Nutrition = NutritionState.Starting() };
    }

    private string? TreatmentsValidation() => Seraph.Core.Framework.Treatments.Validate(this);

    public string? Validate()
    {
        if (Barriers is null || Barriers.Validate() is not null || InfectionThresholdHistory is null || InfectionThresholdHistory.Count > 32
            || InfectionThresholdHistory.Any(p => !TreatmentDefinition.Key(p.Key) || !BodyProfile.In(p.Value, double.Epsilon, 100))) return "Invalid barrier or threshold history.";
        if (!PathogenLoads.Valid(ImmuneMemory, .5) || !PathogenLoads.Valid(SkinPathogens)
            || !PathogenLoads.Valid(HandPathogens)) return "Invalid pathogen memory or skin load.";
        if (ColdExposureByRegion is null || ColdExposureByRegion.Count > 16
            || ColdExposureByRegion.Any(p => !Anatomy.Known(p.Key) || p.Key == "general" || !BodyProfile.In(p.Value, 0, 10)))
            return "Invalid regional cold exposure.";
        if (Syndromes is null || Syndromes.Validate() is not null) return "Invalid syndrome state.";
        if (Downed is null || Downed.Validate() is not null) return "Invalid downed state.";
        if (Blood is null || Blood.Validate() is not null) return "Invalid blood state.";
        if (TreatmentsValidation() is { } treatmentError) return treatmentError;
        if (Nutrition is null || Vitals is null || Clinical is null) return "Missing nutrition/vital state.";
        if (Clinical.Validate() is { } clinicalError) return clinicalError;
        if ((Nutrition.Validate() ?? Vitals.Validate()) is { } stateError) return stateError;
        if (Version != 1 || EventSequence < 0 || RandomState == 0) return "Unsupported framework state or event sequence.";
        if (!Enum.IsDefined(ExertionRecoveryStage) || ExertionReason is not ("" or "stamina" or "lactate"))
            return "Invalid exertion recovery state.";
        if (!BodyProfile.In(Constitution, 0.8, 1.2) || !BodyProfile.In(Stamina, 0, 1)
            || !BodyProfile.In(ExerciseLoad, 0, 1) || !BodyProfile.In(MuscleSoreness, 0, 1)
            || !BodyProfile.In(SleepBlurSeverity, 0, 1) || !BodyProfile.In(AlertnessReliefUntilBioSeconds, 0, 1e15)
            || !BodyProfile.In(AdrenalineLevel, 0, 1)
            || !BodyProfile.In(AdrenalineFatigueDebt, 0, 1) || !BodyProfile.In(AdrenalineSleepDebt, 0, 1)
            || !BodyProfile.In(AdrenalineRemainingSeconds, 0, 60) || !BodyProfile.In(AdrenalineCooldownRemainingSeconds, 0, 43200)
            || !BodyProfile.In(Fatigue, 0, 1) || !BodyProfile.In(Pain, 0, 1) || !BodyProfile.In(Dirtiness, 0, 1)
            || !BodyProfile.In(CleanRemainingGameHours, 0, 1000) || !BodyProfile.In(BathedRemainingGameHours, 0, 1000)
            || !BodyProfile.In(HeatAdaptation, 0, 1) || !BodyProfile.In(ColdAdaptation, 0, 1)
            || !BodyProfile.In(NutritionTrend, 0, 1) || !BodyProfile.In(ImmuneReserve, 0, 1)
            || !BodyProfile.In(SmokeDose, 0, SmokeInhalation.MaximumDose) || !BodyProfile.In(AirPollution, 0, 1) || !BodyProfile.In(BedQuality, 0, 1)
            || !BodyProfile.In(CarbonMonoxideBurden, 0, 1) || !BodyProfile.In(ToxicBurden, 0, 1) || !BodyProfile.In(ChemicalBurden, 0, 1)
            || !BodyProfile.In(SleepEfficiency, 0, 1) || !BodyProfile.In(EnvironmentNoise, 0, 1)
            || !BodyProfile.In(OxygenAvailability, 0, 1)
            || !BodyProfile.In(PerfusionTrend, 0, 1) || !BodyProfile.In(LastActivityPowerW, 0, 2000)
            || !BodyProfile.In(BreathDemandPerMinute, 0, 120)) return "Invalid framework scalar.";
        if (!BodyProfile.In(TotalIntakeWaterL, 0, 1e12) || !BodyProfile.In(TotalIntakeEnergyKJ, 0, 1e15)
            || !BodyProfile.In(TotalObservedSaturation, 0, 1e15)) return "Invalid intake totals.";
        if (Wounds is null || Diseases is null || ChronicEffects is null || RecentReceipts is null || RecentInjuryEvents is null
            || Wounds.Length > 32 || Diseases.Length > 32 || ChronicEffects.Length > 16 || RecentReceipts.Length > 256 || RecentInjuryEvents.Length > 128)
            return "Framework collection exceeds bounds.";
        foreach (var w in Wounds)
            if (w is null || !Enum.IsDefined(w.Kind) || w.EventSequence <= 0 || w.EventSequence > EventSequence
                || !PathogenLoads.Valid(w.Pathogens) || w.Region is null || w.Region.Length is 0 or > 48 || !BodyProfile.In(w.Severity, 0, 1)
                || !BodyProfile.In(w.BleedLps, 0, 0.01) || !BodyProfile.In(w.InitialBleedLps, 0, .01)
                || !BodyProfile.In(w.NaturalClotFloorLps, 0, w.InitialBleedLps)
                || !BodyProfile.In(w.Contamination, 0, 1) || !BodyProfile.In(w.BandageQuality, 0, 1)
                || !BodyProfile.In(w.Healing, 0, 1) || !BodyProfile.In(w.PressureUntilBioSeconds, 0, 1e15)
                || !BodyProfile.In(w.CleanUntilBioSeconds, 0, 1e15) || !Enum.IsDefined(w.Evidence)
                || !BodyProfile.In(w.OccurredBioSeconds, 0, 1e15)
                || w.IntegrityDamage is { } hp && !BodyProfile.In(hp, 0, 1000001)
                || w.TissueDamage is { } tissue && !BodyProfile.In(tissue, 0, 1)
                || w.PainPools is { Valid: false }
                || w.PainIntensity is { } pain && !BodyProfile.In(pain, 0, 1)) return "Invalid wound.";
        foreach (var d in Diseases)
            if (d is null || d.Pathogen is null || d.Pathogen.Length is 0 or > 64 || !Enum.IsDefined(d.Phase)
                || !BodyProfile.In(d.CumulativeHazard, 0, 1e12) || !BodyProfile.In(d.InfectionThreshold, double.Epsilon, 100)
                || !TreatmentDefinition.Key(d.Source) || !BodyProfile.In(d.SecondsSinceExposure, 0, 1e15)
                || !BodyProfile.In(d.PhaseSeconds, 0, 1e15) || !BodyProfile.In(d.Burden, 0, 1)) return "Invalid disease.";
        if (Diseases.Select(d => d.Pathogen).Distinct(StringComparer.Ordinal).Count() != Diseases.Length) return "Duplicate pathogen.";
        foreach (var c in ChronicEffects)
            if (c is null || string.IsNullOrWhiteSpace(c.SourceEvent) || c.SourceEvent.Length > 96
                || string.IsNullOrWhiteSpace(c.Kind) || c.Kind.Length > 64 || !BodyProfile.In(c.Severity, 0, 1)) return "Invalid sourced chronic effect.";
        if (RecentReceipts.Any(r => string.IsNullOrWhiteSpace(r) || r.Length > 128)
            || RecentReceipts.Distinct(StringComparer.Ordinal).Count() != RecentReceipts.Length) return "Invalid receipt history.";
        if (RecentInjuryEvents.Any(r => string.IsNullOrWhiteSpace(r) || r.Length > 128)
            || RecentInjuryEvents.Distinct(StringComparer.Ordinal).Count() != RecentInjuryEvents.Length) return "Invalid injury history.";
        return null;
    }
}

/// <summary>Stable across runtimes. Never uses string.GetHashCode or wall-clock seeds.</summary>
public struct DeterministicRandom(ulong seed)
{
    public ulong State { get; private set; } = seed == 0 ? 1 : seed;
    public double NextUnit()
    {
        var x = State;
        x ^= x >> 12; x ^= x << 25; x ^= x >> 27;
        State = x;
        return (unchecked(x * 2685821657736338717UL) >> 11) * (1.0 / 9007199254740992.0);
    }
    public static ulong Seed(string text)
    {
        ulong hash = 14695981039346656037;
        foreach (var ch in text) { hash ^= ch; hash = unchecked(hash * 1099511628211); }
        return hash == 0 ? 1 : hash;
    }
}
