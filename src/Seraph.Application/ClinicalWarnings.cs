using Seraph.Core.Framework;
using Seraph.Core.State;

namespace Seraph.Application;

/// <summary>Presentation stages only; never feed these values back into physiology.</summary>
public sealed record ClinicalWarning(string Key, int Stage, bool Urgent = false, int Count = 1);

public static class ClinicalWarningPolicy
{
    public static ClinicalWarning[] Evaluate(FrameworkState state, BodyState body, BodyProfile profile, ExertionOptions? options = null)
    {
        var result = new List<ClinicalWarning>();
        foreach (var effect in ClinicalEffects.Evaluate(state, body, profile).Where(e => !e.Beneficial))
        {
            // Keep the icon effects, but announce exertion through one staged episode below.
            if (effect.Id is "lactate-overload" or "overexertion-stamina" or "overexertion-lactate") continue;
            if (Pathogens.Definitions.ContainsKey(effect.Id)
                && !state.Diseases.Any(d => d.Pathogen == effect.Id && d.Phase == DiseasePhase.Symptomatic)) continue;
            var stage = effect.Id switch {
                "blood-toxicity" => BloodStage(state.Blood.WasteMl / BloodCirculation.WasteCapacityMl(body, profile)),
                "ketosis" => state.Clinical.Ketosis >= .60 ? 2 : 1,
                "drowsy" => body.SleepPressure >= 1 ? 4 : body.SleepPressure >= .95 ? 3 : body.SleepPressure >= .9 ? 2 : 1,
                "exhaustion" => state.Fatigue >= 1 ? 4 : state.Fatigue >= .9 ? 3 : state.Fatigue >= .8 ? 2 : 1,
                "thirst" or "dehydration" => Hydration.Stage(body, profile),
                "sepsis" or "insect-carrier" or "renal-hyperactivity" or "overexertion-lactate" or "overexertion-stamina" => 1,
                _ => effect.Severity >= .75 ? 4 : effect.Severity >= .5 ? 3 : effect.Severity >= .25 ? 2 : 1
            };
            if (stage > 0) result.Add(new(effect.Id, stage, effect.Id == "sepsis"
                || effect.Id == "blood-toxicity" && stage == 4 || effect.Id == "dehydration" && stage == 5));
        }
        var lactateRatio = state.Blood.LactateG / BloodCirculation.LactateCapacityG(body, profile);
        options ??= new();
        // Lactate is a running debuff, not a general work lock. Only stamina
        // exhaustion contributes to the overexertion episode; high lactate has
        // its own warning so it cannot make crafting or block edits unusable.
        if (state.ExertionExhausted || state.Stamina < options.UnlockStamina || state.Stamina <= .20)
        {
            // Stage zero silently retains the high-water mark until both reserves recover.
            result.Add(new("exertion-stamina", state.ExertionExhausted ? 0 : state.Stamina <= .05 ? 2 : state.Stamina <= .20 ? 1 : 0));
            result.Add(new("exertion-lactate", state.ExertionExhausted ? 0 : lactateRatio >= .95 ? 2 : lactateRatio >= .80 ? 1 : 0));
            result.Add(new("exertion-exhausted", state.ExertionExhausted ? 1 : 0));
        }
        else if (lactateRatio >= options.UnlockLactateRatio)
            result.Add(new("exertion-lactate", lactateRatio >= .95 ? 2 : lactateRatio >= .80 ? 1 : 0));
        // The exact 40% boundary has zero functional toxicity but still reaches the warning threshold.
        if (state.Blood.Toxicity <= 0 && BloodStage(state.Blood.WasteMl / BloodCirculation.WasteCapacityMl(body, profile)) > 0)
            result.Add(new("blood-toxicity", BloodStage(state.Blood.WasteMl / BloodCirculation.WasteCapacityMl(body, profile))));
        return result.ToArray();
    }
    public static int BloodStage(double ratio) => ratio >= .85 ? 4 : ratio >= .7 ? 3 : ratio >= .55 ? 2 : ratio >= .4 ? 1 : 0;
}

/// <summary>Session-only, bounded by the active clinical keys. Uses unpaused real seconds.</summary>
public sealed class ClinicalWarningTracker
{
    private sealed class Episode(int highest) { public int Highest = highest; public double Missing; }
    private readonly Dictionary<string, Episode> episodes = new(StringComparer.Ordinal);
    private readonly Dictionary<string, ClinicalWarning> pending = new(StringComparer.Ordinal);
    private double time, nextDisplay;
    public bool Initialized { get; private set; }

    public void Seed(IEnumerable<ClinicalWarning> current)
    {
        Reset();
        foreach (var warning in current) episodes[warning.Key] = new(warning.Stage);
        Initialized = true;
    }
    public void Reset() { episodes.Clear(); pending.Clear(); time = nextDisplay = 0; Initialized = false; }

    public ClinicalWarning[] Advance(IReadOnlyList<ClinicalWarning> current, double seconds, int insectBites = 0)
    {
        if (!double.IsFinite(seconds) || seconds < 0 || insectBites < 0) throw new ArgumentOutOfRangeException(nameof(seconds));
        if (!Initialized) Seed(current);
        time += seconds;
        var active = current.Select(w => w.Key).ToHashSet(StringComparer.Ordinal);
        foreach (var key in episodes.Keys.ToArray())
        {
            var episode = episodes[key];
            if (active.Contains(key)) { episode.Missing = 0; continue; }
            pending.Remove(key);
            episode.Missing += seconds;
            if (episode.Missing >= 10) episodes.Remove(key);
        }
        foreach (var warning in current)
        {
            if (warning.Stage == 0) { pending.Remove(warning.Key); continue; }
            if (!episodes.TryGetValue(warning.Key, out var episode)) episodes[warning.Key] = episode = new(0);
            if (warning.Stage <= episode.Highest) continue;
            episode.Highest = warning.Stage;
            pending[warning.Key] = warning;
        }
        if (insectBites > 0)
            pending["insect-bite"] = new("insect-bite", 1, Count: Math.Min(1000000,
                insectBites + (pending.GetValueOrDefault("insect-bite")?.Count ?? 0)));
        if (pending.Count == 0 || time < nextDisplay && !pending.Values.Any(w => w.Urgent)) return [];
        var batch = pending.Values.OrderByDescending(w => w.Urgent).ThenByDescending(w => w.Stage)
            .ThenBy(w => w.Key, StringComparer.Ordinal).ToArray();
        pending.Clear(); nextDisplay = time + 2;
        return batch;
    }
}
