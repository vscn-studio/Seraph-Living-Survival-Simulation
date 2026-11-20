using Seraph.Core.Framework;
using Seraph.Core.Simulation;
using Seraph.Core.State;

internal static class ImmersionHeatChecks
{
    internal static void Boundaries()
    {
        var kernel = new SimulationKernel(BodyProfile.Default);
        var body = BodyState.Create() with { SkinFilmWaterKg = 1 };
        var air = EnvironmentSample.Neutral with { ClothingInsulationClo = .3, WindSpeedMps = 2,
            AbsorbedSolarPowerW = 100 };
        var dry = kernel.Advance(body, air, new(), .01);
        foreach (var fraction in new[] { 0, .2, .5, 1d })
        {
            var env = air with { WaterImmersionFraction = fraction, ContactConductanceWPerK = 90 * fraction,
                ContactTemperatureC = 15 };
            var step = kernel.Advance(body, env, new(), .01);
            Require(step.Accepted, step.Error ?? "Rejected immersion");
            Near(dry.Diagnostics.DryHeatOutJ * (1 - fraction), step.Diagnostics.DryHeatOutJ);
            Near(dry.Diagnostics.EvaporativeHeatOutJ * (1 - fraction), step.Diagnostics.EvaporativeHeatOutJ);
            Near(90 * fraction * (body.SkinTemperatureC - 15) * .01, step.Diagnostics.ContactHeatOutJ);
            Near(0, step.Diagnostics.HeatBalanceResidualJ, 1e-6);
            Near(0, step.Diagnostics.TotalWaterBalanceResidualL);
            Require(kernel.EquivalentTemperatureC(env) is { } felt && double.IsFinite(felt), "Immersion broke felt temperature.");
        }
        foreach (var invalid in new[] { -.1, 1.1, double.NaN })
            Require(!kernel.Advance(body, air with { WaterImmersionFraction = invalid }, new(), .01).Accepted,
                "Invalid immersion was accepted.");
    }

    internal static void Cooling()
    {
        var profile = BodyProfile.Default;
        var kernel = new SimulationKernel(profile);
        var initial = BodyState.Create() with { CoreTemperatureC = 43.2, SkinTemperatureC = 32.8,
            BodyWaterL = profile.ReferenceBodyWaterL - 8, GutWaterL = 3.836, PlasmaWaterL = .9, RbcVolumeL = .9 };
        var env = EnvironmentSample.Neutral with { AirTemperatureC = 36.9, RadiantTemperatureC = 36.9,
            RelativeHumidity = .556, WindSpeedMps = .1, ClothingInsulationClo = .36,
            WaterImmersionFraction = 1, ContactConductanceWPerK = 87.5, ContactTemperatureC = 36.8 };
        BodyState Run(BodyState start, EnvironmentSample environment, int seconds, int chunk)
        {
            var state = start;
            var nutrition = NutritionState.Starting();
            for (var t = 0; t < seconds; t += chunk)
            {
                var step = kernel.Advance(state, environment, new() { Nutrition = nutrition, DigestionEfficiency = .15 }, chunk);
                Require(step.Accepted, step.Error ?? "Rejected cooling");
                Near(0, step.Diagnostics.HeatBalanceResidualJ, 1e-6);
                Near(0, step.Diagnostics.TotalWaterBalanceResidualL, 1e-9);
                state = step.State;
                nutrition = step.Nutrition!;
            }
            return state;
        }
        var warm = Run(initial, env, 1800, 8);
        var split = Run(initial, env, 1800, 4);
        Near(warm.CoreTemperatureC, split.CoreTemperatureC);
        Near(warm.SkinTemperatureC, split.SkinTemperatureC);
        Require(warm.CoreTemperatureC < initial.CoreTemperatureC - .1, "Overheated low-blood player did not cool in warm water.");
        Require(warm.BodyWaterL > initial.BodyWaterL, "Cooling scenario did not recover stomach water.");
        var cold = Run(initial, env with { ContactTemperatureC = 15 }, 1800, 8);
        Require(cold.CoreTemperatureC < warm.CoreTemperatureC, "Colder water failed to improve cooling.");
        var hot = Run(BodyState.Create(), env with { ContactTemperatureC = 45 }, 600, 8);
        Require(hot.CoreTemperatureC > 37.1 && hot.SkinTemperatureC > 33, "Hot water became an unconditional cooling source.");
        var limited = new SimulationKernel(profile, new ModelOptions { EnablePlasmaRedistribution = false });
        var lowBlood = initial with { PlasmaWaterL = .4 * profile.ReferencePlasmaWaterL, RbcVolumeL = .4 * profile.ReferenceRbcVolumeL };
        var transfer = limited.Advance(lowBlood, env, new(), .01);
        Require(transfer.Diagnostics.CoreToSkinHeatJ / (.01 * (lowBlood.CoreTemperatureC - lowBlood.SkinTemperatureC)) > 10,
            "Active core/skin transfer was disabled below half blood volume.");
        Console.WriteLine($"Immersion: low-blood overheated core 43.2 -> {warm.CoreTemperatureC:F3} C in 30 game minutes at 36.8 C water; 15 C water -> {cold.CoreTemperatureC:F3} C.");
    }

    private static void Near(double expected, double actual, double tolerance = 1e-8)
        => Require(Math.Abs(expected - actual) <= tolerance, $"Expected {expected}, got {actual}.");
    private static void Require(bool condition, string message)
    { if (!condition) throw new Exception(message); }
}
