using Seraph.Core.State;

namespace Seraph.Core.Framework;

/// <summary>Accumulates sustained cold exposure by body region and emits bounded frost injuries.</summary>
public static class ColdExposure
{
    public static readonly string[] Regions = ["head-neck", "chest", "abdomen", "left-arm", "right-arm", "left-leg", "right-leg", "left-foot", "right-foot"];

    public static FrameworkState Advance(FrameworkKernel kernel, FrameworkState state, BodyState body,
        BodyProfile profile, EnvironmentSample environment, double seconds)
    {
        if (seconds <= 0 || environment.ColdExposureByRegion.Count == 0) return state;
        var dt = Math.Clamp(seconds, 0, 8);
        var clocks = state.ColdExposureByRegion.ToDictionary(p => p.Key, p => p.Value, StringComparer.Ordinal);
        // Frostbite is a local exposure problem: whole-body mean skin temperature can stay
        // above freezing while uncovered fingers, ears or feet are already dangerously cold.
        // A 0°C day builds risk slowly; severe sub-zero air and wind accelerate it.
        var cold = Math.Clamp((5 - environment.AirTemperatureC) / 20, 0, 1.5);
        var wind = 1 + Math.Clamp(environment.WindSpeedMps / 15, 0, .75);
        var wet = environment.DampSkin ? 1.35 : 1;
        foreach (var region in Regions)
        {
            var exposed = Math.Clamp(environment.ColdExposureByRegion.GetValueOrDefault(region), 0, 1);
            var clock = clocks.GetValueOrDefault(region);
            var rate = cold * wind * exposed * wet / 3600d;
            if (rate > 0) clock += rate * dt;
            else clock = Math.Max(0, clock - dt / 1800d);
            // Warm skin clears the pre-injury clock, while an established injury remains for treatment.
            if (environment.AirTemperatureC >= 10 && body.SkinTemperatureC >= 4)
                clock = Math.Max(0, clock - dt / 900d);
            if (clock >= 1)
            {
                var existing = state.Wounds.Any(w => w.Kind == WoundKind.Frostbite && w.Region == region && w.Healing < 1);
                if (!existing)
                {
                    var injury = kernel.RecordInjury(state, new($"cold:{state.EventSequence + 1}:{region}", WoundKind.Frostbite,
                        region, Math.Clamp(.15 + Math.Min(.35, clock - 1) * .3, .15, .45),
                        InjuryEvidence.GameplayDistribution, body.ElapsedBioSeconds));
                    if (injury.Accepted) state = injury.State;
                }
                clock = Math.Min(clock, .65);
            }
            clocks[region] = Math.Clamp(clock, 0, 10);
        }
        return state with { ColdExposureByRegion = clocks };
    }
}
