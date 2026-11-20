using Seraph.Core.Framework;
using Seraph.Core.Simulation;
using Seraph.Core.State;

internal static class ThermalHydrationChecks
{
    internal static void Run()
    {
        var profile = BodyProfile.Default;
        var options = new ModelOptions { EnableRespiration = false, EnableInsensibleWaterLoss = false,
            EnableFecalWaterLoss = false, EnableEvaporation = false, MaximumShiveringPowerW = 0 };
        var kernel = new SimulationKernel(profile, options);
        var state = BodyState.Create(profile);
        var plain = kernel.Advance(state, EnvironmentSample.Neutral with { AirTemperatureC = 45, RadiantTemperatureC = 45 }, new(), 8);
        var sunny = kernel.Advance(state, EnvironmentSample.Neutral with { AirTemperatureC = 45, RadiantTemperatureC = 45,
            AbsorbedSolarPowerW = 500 }, new(), 8);
        Check(plain.Accepted && sunny.Accepted && sunny.State.SkinTemperatureC > plain.State.SkinTemperatureC,
            "Solar load did not warm the skin boundary.");

        var sweat = 0d;
        for (var i = 0; i < 450; i++)
        {
            var step = kernel.Advance(state, EnvironmentSample.Neutral with { AirTemperatureC = 50, RadiantTemperatureC = 50 }, new(), 8);
            Check(step.Accepted, step.Error ?? "Sweat cap step rejected.");
            sweat += step.Diagnostics.SweatWaterL;
            state = step.State;
        }
        Check(sweat <= .75 + 1e-8, $"Sweat exceeded 0.75 L/game hour: {sweat:R}.");

        var supported = NutritionState.Starting();
        var deficient = supported with { Reserve = (double[])supported.Reserve.Clone(), DeficiencyBurden = (double[])supported.DeficiencyBurden.Clone() };
        deficient.Reserve[Nutrients.IndexOf("electrolytes")] = 0;
        deficient.Reserve[Nutrients.IndexOf("carbohydrate")] = 0;
        deficient.DeficiencyBurden[Nutrients.IndexOf("protein")] = 1;
        deficient.DeficiencyBurden[Nutrients.IndexOf("fat")] = 1;
        var full = NutrientInteractions.WaterAbsorptionSupport(supported);
        var low = NutrientInteractions.WaterAbsorptionSupport(deficient);
        Check(full <= 1 && full >= .9 && low < full && low >= .7,
            "Nutrition water support was not bounded or directional.");
        Console.WriteLine($"PASS solar heating, 0.75 L sweat cap and bounded nutrition water support ({sweat:F3} L)");
    }

    private static void Check(bool condition, string message)
    {
        if (!condition) throw new Exception(message);
    }
}
