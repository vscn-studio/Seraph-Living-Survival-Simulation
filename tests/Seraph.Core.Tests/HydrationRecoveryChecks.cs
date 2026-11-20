using Seraph.Core.Framework;
using Seraph.Core.Simulation;
using Seraph.Core.State;

internal static class HydrationRecoveryChecks
{
    internal static void Recovery()
    {
        var profile = BodyProfile.Default;
        var initial = BodyState.Create(profile) with { BodyWaterL = profile.ReferenceBodyWaterL - 8,
            GutWaterL = 3.836, CoreTemperatureC = 43.2, SkinTemperatureC = 32.8,
            PlasmaWaterL = .9, RbcVolumeL = .9, GutEnergyKJ = 100 * DietaryMetabolism.CarbKJPerG };
        var diet = NutritionState.Starting() with { GutMassG = 100 };
        diet.Pending[0] = 100;
        diet.Pending[Nutrients.IndexOf("electrolytes")] = 3;
        var frame = new FrameworkState { Nutrition = diet };
        Near(.15, BodyFunctionResolver.Digestion(frame, initial, profile));
        Near(1, BodyFunctionResolver.WaterAbsorption(frame));
        // Hold a high sweat demand while isolating absorption from temperature
        // feedback. Reproduce the report's empty reserve and water-filled gut.
        var kernel = new SimulationKernel(profile, new ModelOptions { EnableDryHeatExchange = false,
            EnableEvaporation = false, MaximumShiveringPowerW = 0 });
        var body = initial;
        var absorbed = 0d;
        var lost = 0d;
        for (var elapsed = 0; elapsed < 3600; elapsed += 8)
        {
            var step = kernel.Advance(body, EnvironmentSample.Neutral,
                new() { Nutrition = diet, DigestionEfficiency = BodyFunctionResolver.Digestion(frame, body, profile),
                    WaterAbsorptionEfficiency = BodyFunctionResolver.WaterAbsorption(frame) }, 8);
            Require(step.Accepted, step.Error ?? "Rejected hydration recovery");
            Near(0, step.Diagnostics.TotalWaterBalanceResidualL, 1e-9);
            absorbed += step.Diagnostics.AbsorbedWaterL;
            lost += step.Diagnostics.SweatWaterL + step.Diagnostics.InsensibleWaterL
                + step.Diagnostics.FecalWaterL + step.Diagnostics.RespiratoryWaterL;
            diet = step.Nutrition!;
            body = step.State;
        }
        Near(2, absorbed, 1e-7);
        Near(initial.GutWaterL - absorbed, body.GutWaterL, 1e-8);
        Near(initial.BodyWaterL + absorbed - lost, body.BodyWaterL, 1e-8);
        Require(body.BodyWaterL > initial.BodyWaterL && ReserveDisplay.HydrationG(body, profile) > 0,
            "Water-filled stomach failed to replenish a depleted usable reserve.");

        // Drinking water is independent of the food quota and bowel capacity;
        // dissolved electrolytes must also remain absorbable at 15% digestion.
        var slow = DietaryMetabolism.Digest(frame.Nutrition, initial, profile, 600, .15, 0, 1);
        var normal = DietaryMetabolism.Digest(frame.Nutrition, initial, profile, 600, 1, 5000);
        Near(normal.WaterL, slow.WaterL);
        Near(normal.Nutrition.Pending[4], slow.Nutrition.Pending[4]);
        Require(slow.Nutrition.Pending[0] > normal.Nutrition.Pending[0], "Food limit stopped affecting food.");
        var sick = frame with { Diseases = [new() { Pathogen = "giardia", Phase = DiseasePhase.Symptomatic, Burden = 1 }] };
        var capacity = BodyFunctionResolver.WaterAbsorption(sick);
        Require(capacity < 1 && capacity >= .5, "Local disease no longer limits water absorption.");
        var impaired = DietaryMetabolism.Digest(frame.Nutrition, initial, profile, 600, .15, 0,
            waterAbsorptionEfficiency: capacity);
        Require(impaired.WaterL < slow.WaterL && impaired.WaterL > 0, "Independent water limit is ineffective.");
        var blocked = DietaryMetabolism.Digest(frame.Nutrition, initial, profile, 8, 1, 5000,
            waterAbsorptionEfficiency: 0);
        Near(0, blocked.WaterL);
        var invalid = kernel.Advance(initial, EnvironmentSample.Neutral, new() { WaterAbsorptionEfficiency = double.NaN }, 8);
        Require(!invalid.Accepted, "Invalid water capacity was accepted.");
        Console.WriteLine($"Recovery: absorbed {absorbed:F3} L, lost {lost:F3} L, usable reserve {ReserveDisplay.HydrationG(body, profile):F0} g in one game hour.");
    }

    private static void Near(double a, double b, double tolerance = 1e-8)
        => Require(Math.Abs(a - b) <= tolerance, $"Expected {a}, got {b}.");
    private static void Require(bool condition, string message)
    { if (!condition) throw new Exception(message); }
}
