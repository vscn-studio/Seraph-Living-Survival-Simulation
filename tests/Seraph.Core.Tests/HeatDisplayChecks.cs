using Seraph.Core.Simulation;
using Seraph.Core.State;

internal static class HeatDisplayChecks
{
    internal static void CalendarCooling()
    {
        var profile = BodyProfile.Default;
        var kernel = new SimulationKernel(profile);
        var initial = BodyState.Create() with { CoreTemperatureC = 39, SkinTemperatureC = 35 };
        var environment = EnvironmentSample.Neutral with { AirTemperatureC = 15, RadiantTemperatureC = 15,
            WindSpeedMps = 2, ClothingInsulationClo = .3 };
        var body = initial;
        var netJ = 0d;
        var coreJ = 0d;
        for (var i = 0; i < 900 / 8; i++)
        {
            var step = kernel.Advance(body, environment, new(), 8);
            if (!step.Accepted) throw new Exception(step.Error);
            var d = step.Diagnostics;
            netJ += d.MetabolicEnergyJ + d.ConversionHeatJ - d.ExternalWorkJ - d.DryHeatOutJ
                - d.RespiratoryHeatOutJ - d.EvaporativeHeatOutJ - d.ContactHeatOutJ;
            coreJ += d.MetabolicEnergyJ + d.ConversionHeatJ - d.ExternalWorkJ - d.CoreToSkinHeatJ - d.RespiratoryHeatOutJ;
            body = step.State;
        }
        var storedJ = (body.CoreTemperatureC - initial.CoreTemperatureC) * profile.CoreHeatCapacityJPerK
            + (body.SkinTemperatureC - initial.SkinTemperatureC) * profile.SkinHeatCapacityJPerK;
        if (Math.Abs(storedJ - netJ) > .01 || Math.Abs((body.CoreTemperatureC - initial.CoreTemperatureC)
                * profile.CoreHeatCapacityJPerK - coreJ) > .01)
            throw new Exception("Calendar cooling no longer matches physical core/skin heat capacities.");
        if (body.CoreTemperatureC > 38.5 || body.CoreTemperatureC < 36 || netJ >= 0)
            throw new Exception($"Overheated player did not cool safely within fifteen game minutes: {body.CoreTemperatureC:0.000} C.");
        Console.WriteLine($"Cooling scenario: 39 C -> {body.CoreTemperatureC:0.000} C in 14m56s game time; net {netJ / 4184:0.0} kcal.");
    }

    internal static void SameBoundary()
    {
        var kernel = new SimulationKernel(BodyProfile.Default);
        var body = BodyState.Create();
        foreach (var (air, wind, sun) in new[] { (0d, 0d, 0d), (20d, 4d, 0d), (40d, 2d, 500d) })
        foreach (var clo in new[] { 0, .6, 2d })
        {
            var env = EnvironmentSample.Neutral with { AirTemperatureC = air, RadiantTemperatureC = air,
                WindSpeedMps = wind, AbsorbedSolarPowerW = sun, ClothingInsulationClo = clo };
            var predicted = kernel.DryHeatPowerW(body.SkinTemperatureC, env);
            var actual = kernel.Advance(body, env, new(), .01);
            if (!actual.Accepted || predicted is null || Math.Abs(predicted.Value - actual.Diagnostics.DryHeatOutJ / .01) > 1e-8)
                throw new Exception("Display heat boundary differs from actual heat ledger.");
        }
        var naked = kernel.DryHeatPowerW(body.SkinTemperatureC, EnvironmentSample.Neutral with { ClothingInsulationClo = 0 });
        var dressed = kernel.DryHeatPowerW(body.SkinTemperatureC, EnvironmentSample.Neutral);
        if (!(naked > dressed && dressed > 0)) throw new Exception("Clothing loss reduction has wrong sign.");
        if (kernel.DryHeatPowerW(double.NaN, EnvironmentSample.Neutral) is not null
            || kernel.DryHeatPowerW(33, EnvironmentSample.Neutral with { WindSpeedMps = -1 }) is not null)
            throw new Exception("Display accepted invalid thermal inputs.");
    }
}
