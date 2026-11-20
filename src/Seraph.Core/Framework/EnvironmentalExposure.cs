using Seraph.Core.State;

namespace Seraph.Core.Framework;

/// <summary>Converts estimated ambient loads into one bounded exposure signal.</summary>
public static class EnvironmentalExposure
{
    /// <summary>Returns the named ambient toxic-gas input for chemical burden.</summary>
    /// <remarks>
    /// AirPollution is intentionally not folded into this value: it is a combined
    /// display index and can include smoke and particulates, which have their own
    /// dose model. ToxicGas is the separately sampled chemical pathway.
    /// </remarks>
    public static double ChemicalInput(EnvironmentSample environment)
        => environment is null ? 0 : Math.Clamp(environment.ToxicGas, 0, 1);

    /// <summary>Predicts the barrier result for one interaction without mutating a framework state.</summary>
    /// <remarks>Hazard is a gameplay estimate, not a clinical probability.</remarks>
    public static ExposurePreview Preview(FrameworkState state, ExposureRoute route,
        IReadOnlyDictionary<string, double> incoming)
    {
        if (state is null || incoming is null || route is not (ExposureRoute.Skin or ExposureRoute.Airborne or ExposureRoute.Ingestion)
            || !PathogenLoads.Valid(incoming, 1e9))
            return new(0, 0, 0, 0, false);
        var total = incoming.Values.Where(double.IsFinite).Sum(v => Math.Max(0, v));
        var existing = route switch
        {
            ExposureRoute.Skin => state.Barriers.Skin.Values.Sum(),
            ExposureRoute.Airborne => state.Barriers.Inhaled.Values.Sum(),
            ExposureRoute.Ingestion => state.Barriers.Ingested.Values.Sum(),
            _ => 0
        };
        var capacity = route switch
        {
            ExposureRoute.Skin => ExposureBarriers.Capacity(state, route),
            ExposureRoute.Airborne or ExposureRoute.Ingestion => ExposureBarriers.Capacity(state, route),
            _ => 0
        };
        var retained = Math.Min(total, Math.Max(0, capacity - existing));
        var overflow = Math.Max(0, total - retained);
        // Add() retains barrier load first; only overflow immediately enters a disease course.
        var hazard = overflow / ExposureBarriers.UnitsPerHazard;
        return new(total, retained, overflow, hazard, true);
    }

    /// <summary>
    /// Combines independent airborne and surface/room contamination observations
    /// without counting the same environment twice. Both values are gameplay
    /// proxies, not medical measurements.
    /// </summary>
    public static double CombinedLoad(EnvironmentSample environment)
    {
        if (environment is null) return 0;
        var air = Math.Clamp(environment.AirbornePathogens, 0, 1);
        var room = Math.Clamp(environment.EnvironmentalContamination, 0, 1);
        return 1 - (1 - air) * (1 - room);
    }

    public static double RespiratoryHazard(EnvironmentSample environment, double immuneReserve)
    {
        var load = CombinedLoad(environment);
        var immunePenalty = 1 + (1 - Math.Clamp(immuneReserve, 0, 1)) * .75;
        return load * immunePenalty * .00002;
    }
}

public readonly record struct ExposurePreview(double InputUnits, double RetainedUnits,
    double OverflowUnits, double HazardEstimate, bool Available);
