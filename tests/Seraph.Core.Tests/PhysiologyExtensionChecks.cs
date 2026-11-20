using Seraph.Core.Framework;
using Seraph.Core.State;

internal static class PhysiologyExtensionChecks
{
    public static void Bladder()
    {
        var body = BodyState.Create().AddUrine(.8).AddUrine(1);
        Near(BodyState.BladderCapacityL, body.BladderUrineL);
        var (partial, released) = body.Urinate(.5);
        Near(.75, released);
        Near(.75, partial.BladderUrineL);
        var (empty, rest) = partial.Urinate();
        Near(.75, rest);
        Near(0, empty.BladderUrineL);
        Require(ReferenceEquals(empty, empty.Urinate(0).State));
        Require(body.Validate(BodyProfile.Default) is null);
        Require((body with { BladderUrineL = 1.51 }).Validate(BodyProfile.Default) is not null);
    }

    public static void Clinical()
    {
        var kernel = new FrameworkKernel();
        var state = FrameworkState.Create("clinical-indicators");
        var body = BodyState.Create();
        var calm = kernel.Advance(state, body, BodyProfile.Default, EnvironmentSample.Neutral,
            new(0, false, true, false, false, 1, true), 8);
        Require(calm.Accepted);
        Near(72, calm.State.Clinical.HeartRateBpm, .1);
        var exertion = kernel.Advance(calm.State with { Pain = .5, Fatigue = .5 }, body, BodyProfile.Default,
            EnvironmentSample.Neutral, new(900, false, false, false, false, .6, true), 8);
        Require(exertion.Accepted);
        Require(exertion.State.Clinical.HeartRateBpm > calm.State.Clinical.HeartRateBpm);
        Require(exertion.State.Clinical.OxygenSaturation < calm.State.Clinical.OxygenSaturation);
        Require(exertion.State.Clinical.Validate() is null);
        Require((state with { Clinical = state.Clinical with { HeartRateBpm = double.NaN } }).Validate() is not null);
    }

    public static void AshFertilizer()
    {
        var ash = AshAndFertilizer.Ash(100);
        var manure = AshAndFertilizer.Manure(100, .5);
        Require(ash.AshG == 100 && ash.ManureG == 0 && ash.Nitrogen > 0);
        Require(manure.ManureG == 100 && manure.AshG == 0 && manure.Nitrogen > 0);
        Require(manure.Nitrogen < AshAndFertilizer.Manure(100).Nitrogen);
        Require(new AshFertilizerDose(-1, 0, 0, 0, 0).Validate() is not null);
    }

    private static void Near(double expected, double actual, double tolerance = 1e-9)
    {
        if (Math.Abs(expected - actual) > tolerance) throw new Exception($"Expected {expected}, got {actual}.");
    }

    private static void Require(bool value)
    {
        if (!value) throw new Exception("Physiology extension assertion failed.");
    }
}
