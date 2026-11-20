using Seraph.Core.State;

namespace Seraph.Core.Framework;

/// <summary>Numeric source snapshots only: this model never queries world entities or blocks.</summary>
public static class DiseaseExposure
{
    public static double RespiratoryContactUnits(double distance, double seconds, double ventilation,
        double sourceBurden, double sourceRatePerSecond, double sourceProtection = 1, double receiverProtection = 1)
    {
        if (!double.IsFinite(distance) || !double.IsFinite(seconds) || !double.IsFinite(ventilation)
            || !double.IsFinite(sourceBurden) || !double.IsFinite(sourceRatePerSecond)
            || !double.IsFinite(sourceProtection) || !double.IsFinite(receiverProtection)
            || distance < 0 || seconds < 0 || sourceRatePerSecond < 0) return 0;
        var range = Math.Clamp(1 - distance / 8, 0, 1);
        var airRetention = Math.Clamp(1 - ventilation * .65, .15, 1);
        return sourceRatePerSecond * seconds * range * airRetention
            * Math.Clamp(sourceBurden, 0, 1) * Math.Clamp(sourceProtection, 0, 1)
            * Math.Clamp(receiverProtection, 0, 1);
    }

    public static FrameworkState AdvanceWounds(FrameworkKernel kernel, FrameworkState state,
        double seconds, double hazardPerSecond, ISet<string> ongoing)
    {
        foreach (var load in state.Wounds.Where(w => w.Healing < 1 && WoundCare.Open(w.Kind, w.BleedLps))
            .SelectMany(w => w.Pathogens).Where(p => Pathogens.Wound(p.Key)).GroupBy(p => p.Key))
        {
            var result = kernel.Expose(state, Pathogens.Definitions[load.Key],
                Math.Min(100, load.Sum(p => p.Value) * hazardPerSecond), seconds, "contaminated-wound");
            if (result.Accepted) { state = result.State; ongoing.Add(load.Key); }
        }
        return state;
    }

    public static string? AnimalBitePathogen(string code)
    {
        var tokens = code.Split([':', '-', '_', '/'], StringSplitOptions.RemoveEmptyEntries);
        if (tokens.Any(t => t is "wolf" or "hyena" or "bear" or "fox" or "boar")) return "pasteurella";
        return tokens.Any(t => t is "hare" or "rabbit" or "raccoon" or "squirrel" or "rat" or "mouse") ? "sporotrichosis" : null;
    }

    public static FrameworkState Advance(FrameworkKernel kernel, FrameworkState state, EnvironmentSample environment,
        double seconds, DiseaseExposureOptions options, ISet<string> ongoing)
        => AdvanceWithEvents(kernel, state, environment, seconds, options, ongoing).State;

    public static DiseaseExposureResult AdvanceWithEvents(FrameworkKernel kernel, FrameworkState state, EnvironmentSample environment,
        double seconds, DiseaseExposureOptions options, ISet<string> ongoing)
    {
        // Ambient contact is a persisted hazard clock, not one disease record per positive trace.
        // Only real, identified sources can land; soot and wet skin do not invent species.
        var clocks = state.Barriers.Ambient.ToDictionary(p => p.Key, p => p.Value);
        var sources = environment.DiseaseSources.Where(p => p.Value > 0 && Pathogens.Definitions.ContainsKey(p.Key)
            && (Pathogens.Airborne(p.Key) || p.Key == "skin-fungal" || Pathogens.Wound(p.Key)))
            .OrderBy(p => p.Key, StringComparer.Ordinal).ToArray();
        var events = new List<(double Time, string Pathogen, double Dose)>();
        foreach (var source in sources)
        {
            var rate = options.IndirectHazardPerGameHour * (Pathogens.Airborne(source.Key) ? 1 : state.Dirtiness) / 3600;
            if (rate <= 0) continue;
            var exists = clocks.TryGetValue(source.Key, out var clock);
            var random = new DeterministicRandom(exists ? clock!.RandomState : DeterministicRandom.Seed($"{state.RandomState}:ambient:{source.Key}"));
            if (!exists) clock = new(0, -Math.Log(Math.Max(1e-15, 1 - random.NextUnit())), random.State);
            var t = 0d;
            while (t < seconds)
            {
                var until = Math.Max(0, (clock!.Threshold - clock.Hazard) / rate);
                if (until > seconds - t) { clock = clock with { Hazard = clock.Hazard + rate * (seconds - t) }; break; }
                t += until;
                events.Add((t, source.Key, ExposureBarriers.ContactUnits(Math.Min(100, source.Value), 1)));
                clock = new(0, -Math.Log(Math.Max(1e-15, 1 - random.NextUnit())), random.State);
            }
            clocks[source.Key] = clock!;
        }
        state = state with { Barriers = state.Barriers with { Ambient = clocks } };
        var time = 0d;
        foreach (var contact in events.OrderBy(e => e.Time).ThenBy(e => e.Pathogen, StringComparer.Ordinal))
        {
            state = ExposureBarriers.Advance(kernel, state, contact.Time - time);
            time = contact.Time;
            var route = Pathogens.Airborne(contact.Pathogen) ? ExposureRoute.Airborne : ExposureRoute.Skin;
            state = ExposureBarriers.Add(kernel, state, route,
                new Dictionary<string, double> { [contact.Pathogen] = contact.Dose }, "environment-" + contact.Pathogen);
            ongoing.Add(contact.Pathogen);
        }
        state = ExposureBarriers.Advance(kernel, state, seconds - time);
        return DiseaseSyndromes.TickVectorWithEvents(kernel, state, seconds, environment.VectorSuitable,
            environment.WashedByEnvironment, options, environment.DiseaseSources);
    }
}
