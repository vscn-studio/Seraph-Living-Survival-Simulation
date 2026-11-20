using Seraph.Core.Framework;
using Seraph.Core.Simulation;
using Seraph.Core.State;

internal static class AdaptationChecks
{
    public static void DemandAndEffects()
    {
        void Check(bool ok, [System.Runtime.CompilerServices.CallerArgumentExpression("ok")] string? expression = null)
        { if (!ok) throw new Exception("Thermal adaptation contract failed: " + expression); }
        void Near(double a, double b)
        { if (Math.Abs(a - b) >= 1e-8) throw new Exception($"Expected {a}, actual {b}"); }
        var profile = BodyProfile.Default;
        var body = BodyState.Create();
        var state = FrameworkState.Create("adaptation");
        var options = new ThermalAdaptationOptions();
        var hot = body with { CoreTemperatureC = 38, SkinTemperatureC = 35 };
        var cold = body with { CoreTemperatureC = 36, SkinTemperatureC = 30 };
        var trained = ThermalAdaptation.Advance(state, hot, profile, 168 * 3600, options);
        Near(1 - Math.Exp(-1), trained.HeatAdaptation); Near(0, trained.ColdAdaptation);
        var split = ThermalAdaptation.Advance(ThermalAdaptation.Advance(state, hot, profile, 84 * 3600, options), hot, profile, 84 * 3600, options);
        Near(trained.HeatAdaptation, split.HeatAdaptation);
        Near(trained.HeatAdaptation / 2, ThermalAdaptation.Advance(trained, body, profile, 72 * 3600, options).HeatAdaptation);
        var fever = state with { Diseases = [new() { Pathogen = "wound-staph", Phase = DiseasePhase.Symptomatic, Burden = .5 }] };
        Near(0, ThermalAdaptation.Advance(fever, hot, profile, 3600, options).HeatAdaptation);
        var physics = new SimulationKernel(profile);
        var ordinaryHot = physics.Advance(hot, EnvironmentSample.Neutral with { AirTemperatureC = 35 }, new(), 1);
        var adaptedHot = physics.Advance(hot, EnvironmentSample.Neutral with { AirTemperatureC = 35 }, new() { HeatAdaptation = 1 }, 1);
        Check(ordinaryHot.Accepted && adaptedHot.Accepted && adaptedHot.Diagnostics.SweatWaterL > ordinaryHot.Diagnostics.SweatWaterL);
        Near(1.15, adaptedHot.Diagnostics.SweatWaterL / ordinaryHot.Diagnostics.SweatWaterL);
        Near(0, adaptedHot.Diagnostics.TotalWaterBalanceResidualL); Near(0, adaptedHot.Diagnostics.HeatBalanceResidualJ);
        var ordinaryCold = physics.Advance(cold, EnvironmentSample.Neutral with { AirTemperatureC = 0 }, new(), 1);
        var adaptedCold = physics.Advance(cold, EnvironmentSample.Neutral with { AirTemperatureC = 0 }, new() { ColdAdaptation = 1 }, 1);
        // The two runs evolve different skin temperatures during the solver substeps.
        Check(Math.Abs(.9 - adaptedCold.Diagnostics.CoreToSkinHeatJ / ordinaryCold.Diagnostics.CoreToSkinHeatJ) < .0001);
        Check(adaptedCold.Diagnostics.ShiveringEnergyJ > 0 && adaptedCold.Diagnostics.MetabolicEnergyJ > 0);
        Near(0, adaptedCold.Diagnostics.FuelBalanceResidualKJ);
    }
}
