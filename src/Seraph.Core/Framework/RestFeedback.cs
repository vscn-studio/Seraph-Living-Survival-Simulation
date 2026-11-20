using Seraph.Core.State;

namespace Seraph.Core.Framework;

/// <summary>Bounded gameplay pacing, not a clinical sleep or dehydration model.</summary>
public static class RestFeedback
{
    public const double IntakeReliefSeconds = 150;
    public static double Stress(BodyState body, BodyProfile profile)
    {
        var dehydration = Hydration.Stage(body, profile) / 5d;
        // Available digestive energy counts; unknown micronutrients are never treated as starvation.
        var fuel = Math.Clamp((body.GlycogenEnergyKJ + body.GutEnergyKJ) / profile.GlycogenCapacityKJ, 0, 1);
        var hunger = Math.Clamp((.2 - fuel) / .2, 0, 1);
        return Math.Max(dehydration, hunger);
    }
    public static double FatigueRiseMultiplier(BodyState body, BodyProfile profile) => 1 + Stress(body, profile);
    public static double SleepRiseMultiplier(BodyState body, BodyProfile profile)
    {
        var highPressure = Math.Clamp((body.SleepPressure - .7) / .2, 0, 1);
        highPressure *= highPressure * (3 - 2 * highPressure);
        return 1 + Stress(body, profile) * (.1 + .65 * highPressure);
    }
    public static double FatigueMovementPenalty(double fatigue) => fatigue <= .6 ? 0
        : fatigue <= .8 ? .05 * (fatigue - .6) / .2
        : fatigue <= .9 ? .05 + .05 * (fatigue - .8) / .1
        : .10 + .05 * Math.Clamp((fatigue - .9) / .1, 0, 1);
    public static FrameworkState Advance(FrameworkState state, BodyState body, bool sleeping)
    {
        if (sleeping) return state with { SleepBlurSeverity = 0, AlertnessReliefUntilBioSeconds = 0 };
        return state with { SleepBlurSeverity = Math.Min(.15, Math.Max(0, body.SleepPressure - .9) * 1.5) };
    }
    public static FrameworkState Relieve(FrameworkState state, double bioSeconds) => state with {
        AlertnessReliefUntilBioSeconds = bioSeconds + IntakeReliefSeconds };
}
