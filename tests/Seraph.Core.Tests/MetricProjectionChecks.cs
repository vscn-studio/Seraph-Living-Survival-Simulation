using Seraph.Core.Framework;
using Seraph.Core.Simulation;
using Seraph.Core.State;

internal static class MetricProjectionChecks
{
    internal static void Rates()
    {
        var profile = BodyProfile.Default;
        var body = BodyState.Create();
        var frame = new FrameworkKernel().AddWound(FrameworkState.Create("metrics"), WoundKind.Cut, .2, 0).State with { OxygenKnown = true, OxygenAvailability = 1 };
        var kernel = new FrameworkKernel();
        var environment = EnvironmentSample.Neutral;
        foreach (var rest in new[] { false, true })
        {
            var result = kernel.Advance(frame, body, profile, environment, new(0, false, rest, false, false, 1, true), 8);
            Require(result.Accepted);
            Near(result.State.Wounds[0].Healing, 8 * kernel.WoundHealingRate(frame, frame.Wounds[0], body, profile, rest));
            var target = FrameworkKernel.ImmuneSupport(frame, body, profile, environment, Treatments.Pain(result.State));
            Near(result.State.ImmuneReserve, frame.ImmuneReserve + (1 - Math.Exp(-8d / 172800)) * (target - frame.ImmuneReserve));
        }
        Near(FrameworkKernel.ImmuneSupport(frame with { Dirtiness = 1 }, body, profile, environment),
            FrameworkKernel.ImmuneSupport(frame, body, profile, environment));
        var physics = new SimulationKernel(profile);
        var total = physics.Advance(body, environment, new(), 8);
        var first = physics.Advance(body, environment, new(), 3);
        var second = physics.Advance(first.State, environment, new(), 5);
        var actual = new ThermalLedger().Add(total.Diagnostics);
        var split = new ThermalLedger().Add(first.Diagnostics).Add(second.Diagnostics);
        Near(actual.HeatInW, split.HeatInW); Near(actual.HeatOutW, split.HeatOutW);
        Near(actual.NetW, split.NetW); Near(actual.SweatLPerGameHour, split.SweatLPerGameHour);
        var expected = total.Diagnostics.MetabolicEnergyJ - total.Diagnostics.ExternalWorkJ
            - total.Diagnostics.DryHeatOutJ - total.Diagnostics.RespiratoryHeatOutJ
            - total.Diagnostics.EvaporativeHeatOutJ - total.Diagnostics.ContactHeatOutJ;
        Near(actual.NetW * 8, expected);
        var warm = new ThermalLedger().Add(new() { AcceptedBioSeconds = 2, DryHeatOutJ = -20 });
        Require(warm.HeatOutW == -10 && warm.NetW == 10);
    }
    private static void Require(bool v) { if (!v) throw new Exception("Metric rate projection mismatch."); }
    private static void Near(double a, double b) { if (Math.Abs(a - b) >= 1e-8) throw new Exception($"Expected {a:R}, actual {b:R}"); }
}
