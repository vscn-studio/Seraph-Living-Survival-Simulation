using Seraph.Core.State;

namespace Seraph.Core.Framework;

public sealed record ThermalAdaptationOptions
{
    public double TrainingHours { get; init; } = 168;
    public double DecayHalfLifeHours { get; init; } = 72;
    public string? Validate() => !BodyProfile.In(TrainingHours, 1, 10000)
        || !BodyProfile.In(DecayHalfLifeHours, 1, 10000) ? "Invalid thermal adaptation duration." : null;
}

public static class ThermalAdaptation
{
    public static double HotDemand(BodyState body, BodyProfile profile) => Math.Max(0,
        body.CoreTemperatureC - profile.ReferenceCoreTemperatureC + .1 * (body.SkinTemperatureC - profile.ReferenceSkinTemperatureC));
    public static double ColdDemand(BodyState body, BodyProfile profile) => Math.Max(0,
        profile.ReferenceCoreTemperatureC - body.CoreTemperatureC + .1 * (profile.ReferenceSkinTemperatureC - body.SkinTemperatureC));
    public static FrameworkState Advance(FrameworkState state, BodyState body, BodyProfile profile, double seconds, ThermalAdaptationOptions options)
    {
        double Settle(double value, bool demand) => demand
            ? 1 - (1 - value) * Math.Exp(-seconds / (options.TrainingHours * 3600))
            : value * Math.Exp(-Math.Log(2) * seconds / (options.DecayHalfLifeHours * 3600));
        return state with {
            HeatAdaptation = Settle(state.HeatAdaptation, HotDemand(body, profile) > .05 && DiseaseSyndromes.FeverSetpointC(state) <= 37),
            ColdAdaptation = Settle(state.ColdAdaptation, ColdDemand(body, profile) > .05) };
    }
}
