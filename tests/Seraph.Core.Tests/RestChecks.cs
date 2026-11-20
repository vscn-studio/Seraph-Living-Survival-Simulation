using Seraph.Core.Framework;
using Seraph.Core.State;
using Seraph.Core.Simulation;

internal static class RestChecks
{
    private static void Require(bool ok, string reason) { if (!ok) throw new Exception(reason); }
    public static void Stress()
    {
        var p = BodyProfile.Default; var b = BodyState.Create();
        var dry = b with { BodyWaterL = p.ReferenceBodyWaterL - 8 };
        var hungry = b with { GlycogenEnergyKJ = 0, GutEnergyKJ = 0 };
        Require(RestFeedback.Stress(b, p) == 0, "Baseline must be neutral.");
        Require(Math.Abs(RestFeedback.FatigueRiseMultiplier(dry, p) - 2) < 1e-10, "Severe dehydration must double fatigue rise.");
        Require(RestFeedback.FatigueRiseMultiplier(hungry, p) == 2, "Low available fuel must increase fatigue rise.");
        Require(RestFeedback.FatigueRiseMultiplier(hungry with { GutEnergyKJ = p.GlycogenCapacityKJ * .2 }, p) == 1,
            "Digestive fuel ignored, or unknown micronutrients became starvation.");
        Require(RestFeedback.SleepRiseMultiplier(dry, p) < 1.11 && RestFeedback.SleepRiseMultiplier(dry with { SleepPressure = .95 }, p) > 1.7,
            "Low sleep pressure must have much weaker stress feedback.");
        var f = new FrameworkKernel(); var state = new FrameworkState { Fatigue = .4 };
        var activity = new ActivityContext(0, false, false, false, false, 1, true);
        var normal = f.Advance(state, b, p, EnvironmentSample.Neutral, activity, 8);
        var stressed = f.Advance(state, dry, p, EnvironmentSample.Neutral, activity, 8);
        Require(normal.Accepted && stressed.Accepted && stressed.State.Fatigue == normal.State.Fatigue && normal.State.Fatigue == state.Fatigue, "Idle time must not create labour fatigue.");
        var kernel = new SimulationKernel(p);
        var normalSleep = kernel.Advance(b, EnvironmentSample.Neutral, new(), 8);
        var drySleep = kernel.Advance(dry, EnvironmentSample.Neutral, new(), 8);
        Require(normalSleep.Accepted && drySleep.Accepted && Math.Abs(drySleep.State.SleepPressure - normalSleep.State.SleepPressure) < 1e-12, "Awake sleep pressure must follow elapsed time.");
        Require(RestFeedback.FatigueRiseMultiplier(dry with { GlycogenEnergyKJ = 0 }, p) <= 2, "Correlated stress stacked twice.");
    }
    public static void PersistentBlur()
    {
        var b = BodyState.Create() with { SleepPressure = .91 };
        var s = RestFeedback.Advance(new(), b, false);
        Require(s.SleepBlurSeverity is > 0 and < .02, "Initial blur too strong.");
        var peak = RestFeedback.Advance(s, b with { SleepPressure = 1 }, false);
        Require(Math.Abs(peak.SleepBlurSeverity - .15) < 1e-12, "Maximum blur exceeded the new cap.");
        Require(RestFeedback.Advance(new() { SleepBlurSeverity = .95 }, b with { SleepPressure = .5 }, false).SleepBlurSeverity == 0, "Legacy high blur latched at 50% pressure.");
        var low = RestFeedback.Advance(peak, b with { SleepPressure = .2 }, false);
        Require(low.SleepBlurSeverity == 0, "Current sleep pressure did not clear blur.");
        var relieved = RestFeedback.Relieve(low, 100);
        Require(relieved.SleepBlurSeverity == 0 && relieved.AlertnessReliefUntilBioSeconds == 100 + RestFeedback.IntakeReliefSeconds, "Intake changed pressure feedback or relief duration.");
        var sleep = RestFeedback.Advance(relieved, b, true);
        Require(sleep.SleepBlurSeverity == 0 && sleep.AlertnessReliefUntilBioSeconds == 0, "Real sleep failed to clear feedback.");
        Require((new FrameworkState { SleepBlurSeverity = double.NaN }).Validate() is not null
            && (new FrameworkState { AlertnessReliefUntilBioSeconds = double.PositiveInfinity }).Validate() is not null, "Nonfinite feedback persisted.");
    }
    public static void Movement()
    {
        var p = BodyProfile.Default; var b = BodyState.Create();
        double Mobility(double fatigue) => BodyFunctionResolver.Evaluate(new() { Fatigue = fatigue }, b, p).Mobility;
        Require(Math.Abs(Mobility(.6) - 1) < 1e-10 && Math.Abs(Mobility(.8) - .95) < 1e-10
            && Math.Abs(Mobility(.9) - .9) < 1e-10 && Math.Abs(Mobility(1) - .85) < 1e-10,
            "Fatigue movement thresholds or cap incorrect.");
        // Fatigue has its own movement stage; it does not duplicate the endurance penalty.
        Require(Math.Abs(BodyFunctionResolver.Evaluate(new() { Fatigue = 1 }, b, p).Endurance - 1) < 1e-10,
            "Fatigue was charged again through endurance.");
    }
}
