using Seraph.Core.Framework;
using Seraph.Core.Simulation;
using Seraph.Core.State;

internal static class HydrationChecks
{
    public static void Settlement()
    {
        var p = BodyProfile.Default; var b = BodyState.Create(p);
        void Check(bool value) { if (!value) throw new Exception("Hydration contract failed."); }
        void Near(double x, double y, double tolerance = 1e-8) => Check(Math.Abs(x-y) <= tolerance);
        foreach (var (water, stage) in new[] { (8000d, 0), (2001d, 0), (2000d, 1), (1500d, 2), (1000d, 3), (500d, 4), (0d, 5) })
        {
            var body = b with { BodyWaterL = p.ReferenceBodyWaterL - (8000 - water) / 1000 };
            Check(Hydration.Stage(body, p) == stage);
            Near(water, ReserveDisplay.HydrationG(body, p));
            Check(ClinicalEffects.DamagePerSecond(new(), body, p, true) > 0 == (stage == 5));
            Near(Hydration.OrganFactor(stage), BodyFunctionResolver.Digestion(new(), body, p));
        }
        Check(ReserveDisplay.WholeScore(.02) == 1 && ReserveDisplay.WholeScore(.01) == 0);
        var options = new ModelOptions { EnableDryHeatExchange = false, EnableSweating = false,
            EnableEvaporation = false, MaximumShiveringPowerW = 0 };
        var kernel = new SimulationKernel(p, options);
        var step = kernel.Advance(b, EnvironmentSample.Neutral, new(), .25);
        Check(step.Accepted);
        Near(75, (b.BodyWaterL - step.State.BodyWaterL) * 1000 * 3600 / .25, 1e-6);
        Near(0, step.Diagnostics.TotalWaterBalanceResidualL);
        Near(0, step.Diagnostics.UrineWaterL);
        Near(step.Diagnostics.UrineWaterL, step.Diagnostics.CreateFluxLedger().Net("bladder-urine", FluxUnit.Litre));
        var filled = kernel.Advance(b with { GutWaterL = 1 }, EnvironmentSample.Neutral, new(), .25);
        Near(0, filled.Diagnostics.AbsorbedWaterL);
        Near(1, filled.State.GutWaterL);
        var almost = b with { BodyWaterL = p.ReferenceBodyWaterL - .00001, GutWaterL = 1 };
        var capped = kernel.Advance(almost, EnvironmentSample.Neutral, new(), .25);
        Check(capped.Accepted); Near(.00001, capped.Diagnostics.AbsorbedWaterL);
        Near(0, capped.Diagnostics.TotalWaterBalanceResidualL);
        var split = kernel.Advance(step.State, EnvironmentSample.Neutral, new(), .25);
        var whole = kernel.Advance(b, EnvironmentSample.Neutral, new(), .5);
        Near(split.State.BodyWaterL, whole.State.BodyWaterL);
        Near(split.State.GutWaterL, whole.State.GutWaterL);
    }

    public static void UrineProduction()
    {
        void Near(double x, double y, double tolerance = 1e-8)
        {
            if (Math.Abs(x - y) > tolerance) throw new Exception($"{x} != {y}");
        }
        var p = BodyProfile.Default;
        var profile = p with { BasalMetabolicPowerW = 0 };
        var options = new ModelOptions { UrineProductionMlPerGameHour = 60,
            EnableDryHeatExchange = false, EnableRespiration = false,
            EnableEvaporation = false, EnableSweating = false, EnableInsensibleWaterLoss = false,
            EnableFecalWaterLoss = false, EnablePlasmaRedistribution = false, MaximumShiveringPowerW = 0 };
        var kernel = new SimulationKernel(profile, options);
        var body = BodyState.Create(profile) with { BladderUrineL = .25 };
        var step = kernel.Advance(body, EnvironmentSample.Neutral, new(), 8);
        if (!step.Accepted) throw new Exception("Legacy bladder migration was rejected: " + step.Error);
        Near(0, step.State.BladderUrineL);
        Near(0, step.State.UrineProductionMlPerGameHour);
        Near(.25, step.State.BodyWaterL - body.BodyWaterL);
        Near(.25, step.Diagnostics.LegacyBladderReturnL);
        Near(0, step.Diagnostics.UrineWaterL);
        Near(0, step.Diagnostics.BodyWaterBalanceResidualL);
        Near(0, step.Diagnostics.TotalWaterBalanceResidualL);

        var deprecatedConfig = new SimulationKernel(profile, options);
        var next = deprecatedConfig.Advance(step.State, EnvironmentSample.Neutral, new(), 8);
        if (!next.Accepted) throw new Exception("Deprecated urine option affected simulation: " + next.Error);
        Near(0, next.Diagnostics.UrineWaterL);
    }
}
