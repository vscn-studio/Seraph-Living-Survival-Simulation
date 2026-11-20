using Seraph.Core.State;

namespace Seraph.Core.Framework;

public enum SymptomKit { None, Fever, FoodPoisoning, Dysentery, CommonCold }

public sealed record SyndromeState
{
    /// <summary>Diverted stomach contents and enteric water awaiting bowel transit; grams, not extra body water.</summary>
    public double BowelContentsG { get; init; }
    public bool Sepsis { get; init; }
    public double SepsisDangerHours { get; init; }
    public double SepsisRecoveryHours { get; init; }
    public bool VectorAttached { get; init; }
    public double VectorHours { get; init; }
    public double VectorHazard { get; init; }
    public double VectorThreshold { get; init; }
    public string? VectorPathogen { get; init; }
    public string? Validate() => !BodyProfile.In(BowelContentsG, 0, 1e12) || !BodyProfile.In(VectorHours, 0, 1e9)
        || !BodyProfile.In(VectorHazard, 0, 1e9) || !BodyProfile.In(VectorThreshold, 0, 100)
        || !BodyProfile.In(SepsisDangerHours, 0, 1000) || !BodyProfile.In(SepsisRecoveryHours, 0, 1000)
        || VectorPathogen is not (null or "malaria" or "murinetyphus")
        ? "Invalid syndrome or vector state." : null;
}

public sealed record DiseaseExposureOptions
{
    public double IndirectHazardPerGameHour { get; init; } = .1;
    public double VectorLandingHazardPerGameHour { get; init; } = .05;
    public double VectorMaturationGameHours { get; init; } = 2;
    public double VectorInfectiousChance { get; init; } = .1;
    public double VectorBiteHazard { get; init; } = .2;
    public double BiteHazard { get; init; } = .8;
    public double SepsisHpPerTenGameMinutes { get; init; } = 5;
    public double SepsisOnsetHours { get; init; } = 2;
    public double SepsisRecoveryHours { get; init; } = 2;
    public string? Validate() => !BodyProfile.In(IndirectHazardPerGameHour, 0, 10)
        || !BodyProfile.In(VectorLandingHazardPerGameHour, 0, 10) || !BodyProfile.In(VectorMaturationGameHours, .01, 1000)
        || !BodyProfile.In(BiteHazard, 0, 100) || !BodyProfile.In(SepsisHpPerTenGameMinutes, 0, 100)
        || !BodyProfile.In(VectorInfectiousChance, 0, 1) || !BodyProfile.In(VectorBiteHazard, 0, 100)
        || !BodyProfile.In(SepsisOnsetHours, .01, 1000) || !BodyProfile.In(SepsisRecoveryHours, .01, 1000)
        ? "Invalid game-time disease exposure rates." : null;
}

public readonly record struct SyndromeResult(FrameworkState State, BodyState Body);
/// <summary>Accepted-step observations, never saved or replayed as disease exposure.</summary>
public readonly record struct DiseaseExposureResult(FrameworkState State, int InsectBites);

public static class DiseaseSyndromes
{
    public static SymptomKit Kit(string pathogen) => pathogen switch {
        "salmonella" or "trichinosis" or "norovirus" or "ecoli" or "water-enteric" => SymptomKit.FoodPoisoning,
        "shigella" => SymptomKit.Dysentery,
        "qfever" or "tularemia" or "histoplasmosis" or "pasteurella" or "sporotrichosis"
            or "murinetyphus" or "malaria" or "anthrax" or "tetanus" or "wound-staph" or "wound-local" or "respiratory" => SymptomKit.Fever,
        "common-cold" => SymptomKit.CommonCold,
        _ => SymptomKit.None
    };
    public static double Severity(FrameworkState state, SymptomKit kit) => state.Diseases.Where(d => Kit(d.Pathogen) == kit)
        .Select(d => Treatments.Burden(state, d)).DefaultIfEmpty().Max();
    public static bool FoodPoisoning(FrameworkState state) => state.Diseases.Any(d => Kit(d.Pathogen) == SymptomKit.FoodPoisoning
        && d.Phase == DiseasePhase.Symptomatic);
    public static double FeverSetpointC(FrameworkState state) => 37 + 2 * Severity(state, SymptomKit.Fever);

    // Applies only to the newly accepted meal. Replays are rejected by the existing intake receipt.
    public static SyndromeResult DivertMeal(FrameworkState before, FrameworkState after, BodyState body, BodyState previousBody)
    {
        if (!FoodPoisoning(before)) return new(after, body);
        var nutrition = after.Nutrition; var old = before.Nutrition;
        var water = Math.Max(0, body.GutWaterL - previousBody.GutWaterL) * .85;
        var energy = Math.Max(0, body.GutEnergyKJ - previousBody.GutEnergyKJ) * .85;
        var mass = Math.Max(0, nutrition.GutMassG - old.GutMassG) * .85;
        var foodWater = Math.Max(0, nutrition.GutFoodWaterG - old.GutFoodWaterG) * .85;
        var waste = Math.Max(0, nutrition.StomachWasteG - old.StomachWasteG) * .85;
        var pending = nutrition.Pending.Select((v, i) => v - Math.Max(0, v - old.Pending[i]) * .85).ToArray();
        return new(after with { Nutrition = nutrition with { Pending = pending, GutMassG = nutrition.GutMassG - mass,
            GutFoodWaterG = nutrition.GutFoodWaterG - foodWater,
            StomachWasteG = nutrition.StomachWasteG - waste,
            PendingWaterWasteMl = nutrition.PendingWaterWasteMl * (body.GutWaterL > 0 ? Math.Clamp(1 - water / body.GutWaterL, 0, 1) : 0) },
            Syndromes = after.Syndromes with { BowelContentsG = Math.Min(1e12, after.Syndromes.BowelContentsG + mass - foodWater + water * 1000) } },
            body with { GutWaterL = body.GutWaterL - water, GutEnergyKJ = body.GutEnergyKJ - energy });
    }

    public static bool SepsisAgent(string id) => id is "wound-staph" or "wound-local" or "pasteurella" or "anthrax";
    public static bool HasSepsisInfection(FrameworkState state) => state.Diseases.Any(d => SepsisAgent(d.Pathogen)
        && d.Phase == DiseasePhase.Symptomatic && d.Burden >= .8 * Pathogens.Definitions[d.Pathogen].MaximumBurden);

    public static SyndromeResult Settle(FrameworkState state, BodyState body, BodyProfile profile, double entericWaterLossL,
        double seconds = 0, DiseaseExposureOptions? options = null)
    {
        if (!BodyProfile.In(seconds, 0, 86400)) throw new ArgumentOutOfRangeException(nameof(seconds));
        options ??= new();
        var syndrome = state.Syndromes;
        var first = state.Diseases.Any(d => d.Phase == DiseasePhase.Symptomatic && !d.StomachDumped
            && Kit(d.Pathogen) == SymptomKit.FoodPoisoning);
        if (first)
        {
            var diet = state.Nutrition;
            syndrome = syndrome with { BowelContentsG = Math.Min(1e12, syndrome.BowelContentsG + diet.ContentsMassG(body.GutWaterL)) };
            state = state with { Nutrition = diet with { Pending = new double[diet.Pending.Length], GutMassG = 0,
                GutFoodWaterG = 0, StomachWasteG = 0, PendingWaterWasteMl = 0 },
                Diseases = state.Diseases.Select(d => d.Phase == DiseasePhase.Symptomatic && Kit(d.Pathogen) == SymptomKit.FoodPoisoning
                    ? d with { StomachDumped = true } : d).ToArray() };
            body = body with { GutWaterL = 0, GutEnergyKJ = 0 };
        }
        syndrome = syndrome with { BowelContentsG = Math.Min(1e12, syndrome.BowelContentsG + Math.Max(0, entericWaterLossL) * 1000) };
        var dangerous = Hydration.Stage(body, profile) == 5 || body.BloodVolumeL(profile) <= profile.ReferenceBloodVolumeL * .4
            || BodyFunctionResolver.Evaluate(state, body, profile).Consciousness <= .25;
        var dangerHours = HasSepsisInfection(state) && dangerous
            ? Math.Min(options.SepsisOnsetHours, syndrome.SepsisDangerHours + seconds / 3600) : 0;
        var resolving = !dangerous && state.Diseases.Where(d => SepsisAgent(d.Pathogen))
            .All(d => d.Burden < .3 * Pathogens.Definitions[d.Pathogen].MaximumBurden);
        var recoveryHours = syndrome.Sepsis && resolving
            ? Math.Min(options.SepsisRecoveryHours, syndrome.SepsisRecoveryHours + seconds / 3600) : 0;
        syndrome = syndrome with { SepsisDangerHours = dangerHours, SepsisRecoveryHours = recoveryHours,
            Sepsis = syndrome.Sepsis ? recoveryHours < options.SepsisRecoveryHours : dangerHours >= options.SepsisOnsetHours };
        return new(state with { Syndromes = syndrome }, body);
    }

    public static FrameworkState TickVector(FrameworkKernel kernel, FrameworkState state, double seconds, bool suitable,
        bool washed, DiseaseExposureOptions options, IReadOnlyDictionary<string, double>? infectiousSources = null)
        => TickVectorWithEvents(kernel, state, seconds, suitable, washed, options, infectiousSources).State;

    public static DiseaseExposureResult TickVectorWithEvents(FrameworkKernel kernel, FrameworkState state, double seconds, bool suitable,
        bool washed, DiseaseExposureOptions options, IReadOnlyDictionary<string, double>? infectiousSources = null)
    {
        if (!BodyProfile.In(seconds, 0, 8)) throw new ArgumentOutOfRangeException(nameof(seconds));
        var s = state.Syndromes;
        if (washed) return new(state with { Syndromes = s with { VectorAttached = false, VectorHours = 0, VectorHazard = 0, VectorThreshold = 0, VectorPathogen = null } }, 0);
        var random = new DeterministicRandom(state.RandomState);
        var bites = 0;
        var remaining = seconds;
        while (remaining > 1e-10)
        {
            if (!s.VectorAttached)
            {
                var rate = suitable ? options.VectorLandingHazardPerGameHour * (1 + .5 * state.Dirtiness) / 3600 : 0;
                if (rate <= 0) break;
                if (s.VectorThreshold == 0) s = s with { VectorThreshold = -Math.Log(Math.Max(1e-15, 1 - random.NextUnit())) };
                var until = Math.Max(0, (s.VectorThreshold - s.VectorHazard) / rate);
                var spent = Math.Min(remaining, until);
                s = s with { VectorHazard = s.VectorHazard + rate * spent };
                remaining -= spent;
                if (until > spent + 1e-10) break;
                // A vector can carry only a pathogen present in its explicit local source snapshot.
                var infectiousDraw = random.NextUnit();
                s = s with { VectorAttached = true, VectorHours = 0, VectorHazard = 0, VectorThreshold = 0,
                    VectorPathogen = SelectVectorPathogen(infectiousSources, infectiousDraw, random.NextUnit(), options.VectorInfectiousChance) };
            }
            var duration = Math.Max(0, (options.VectorMaturationGameHours - s.VectorHours) * 3600);
            var elapsed = Math.Min(remaining, duration);
            s = s with { VectorHours = s.VectorHours + elapsed / 3600 };
            remaining -= elapsed;
            if (duration > elapsed + 1e-10) break;
            var pathogen = s.VectorPathogen;
            bites++;
            state = state with { Syndromes = s with { VectorAttached = false, VectorHours = 0, VectorPathogen = null }, RandomState = random.State };
            if (pathogen is not null) state = DirectExposure(kernel, state, pathogen, options.VectorBiteHazard, "insect-bite");
            s = state.Syndromes;
            random = new DeterministicRandom(state.RandomState);
        }
        return new(state with { Syndromes = s, RandomState = random.State }, bites);
    }

    private static string? SelectVectorPathogen(IReadOnlyDictionary<string, double>? sources, double infectionDraw,
        double selectionDraw, double infectiousChance)
    {
        if (sources is null || infectionDraw >= infectiousChance) return null;
        var candidates = sources.Where(p => (p.Key is "malaria" or "murinetyphus") && double.IsFinite(p.Value) && p.Value > 0)
            .OrderBy(p => p.Key, StringComparer.Ordinal).ToArray();
        var total = candidates.Sum(p => p.Value);
        if (total <= 0) return null;
        var selection = selectionDraw * total;
        foreach (var candidate in candidates)
        {
            selection -= candidate.Value;
            if (selection < 0) return candidate.Key;
        }
        return candidates[^1].Key;
    }

    public static (FrameworkState State, double BowelG) Transit(FrameworkState state, double seconds, double bowelSpaceG)
    {
        var mass = Math.Min(state.Syndromes.BowelContentsG, Math.Min(Math.Max(0, bowelSpaceG), seconds * 2));
        return (state with { Syndromes = state.Syndromes with { BowelContentsG = state.Syndromes.BowelContentsG - mass } }, mass);
    }

    public static FrameworkState DirectExposure(FrameworkKernel kernel, FrameworkState state, string pathogen, double hazard, string source)
    {
        var result = kernel.Expose(state, Pathogens.Definitions[pathogen], Math.Clamp(hazard, 0, 100), 1, source);
        if (!result.Accepted) throw new InvalidOperationException(result.Error);
        return result.State;
    }
}
