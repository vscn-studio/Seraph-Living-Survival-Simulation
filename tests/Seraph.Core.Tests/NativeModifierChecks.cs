using System.Text.Json;
using Seraph.Core.Framework;
using Seraph.Core.Simulation;
using Seraph.Core.State;

internal static class NativeModifierChecks
{
    private static void Near(double expected, double actual, double tolerance = 1e-7)
    { if (Math.Abs(expected - actual) > tolerance) throw new Exception($"Expected {expected}, got {actual}"); }
    public static void Health()
    {
        var damaged = new VitalState().Change(-10, 2);
        var boosted = damaged.WithExternalMaximum(50);
        Near(150, boosted.EffectiveMaximum); Near(12.5, boosted.EffectiveFor(2));
        Near(5, boosted.WithExternalMaximum(0).EffectiveFor(2));
        var severe = new VitalState().WithExternalMaximum(200).Change(-35, 0);
        var json = JsonSerializer.Serialize(severe);
        if (json.Contains("ExternalMaximumPoints")) throw new Exception("Runtime bonus leaked into saves.");
        var loaded = JsonSerializer.Deserialize<VitalState>(json)!;
        if (loaded.Validate() is not null) throw new Exception("Saved absolute damage rejected without optional modifier.");
        Near(35, loaded.DamageDeficits![0]); Near(25, loaded.WithExternalMaximum(200).EffectiveFor(0));
        Near(35, severe.WithExternalMaximum(0).DamageDeficits![0]);
        Near(100, damaged.WithExternalMaximum(double.NaN).EffectiveMaximum);
    }
    public static void Energy()
    {
        var profile = BodyProfile.Default;
        var kernel = new SimulationKernel(profile, new() { EnableDryHeatExchange = false, MaximumShiveringPowerW = 0 });
        var body = BodyState.Create(profile);
        var input = new StepInputs { ActivityPowerW = 450, EnergyDemandMultiplier = 1 };
        var normal = kernel.Advance(body, EnvironmentSample.Neutral, input, 4);
        var reduced = kernel.Advance(body, EnvironmentSample.Neutral, input with { EnergyDemandMultiplier = .7 }, 4);
        if (!normal.Accepted || !reduced.Accepted) throw new Exception(normal.Error ?? reduced.Error);
        Near(normal.Diagnostics.BasalEnergyJ * .7, reduced.Diagnostics.BasalEnergyJ);
        Near(normal.Diagnostics.ActivityEnergyJ * .7, reduced.Diagnostics.ActivityEnergyJ);
        Near(normal.Diagnostics.GlycogenConsumedKJ * .7, reduced.Diagnostics.GlycogenConsumedKJ);
        Near(normal.Diagnostics.FatConsumedKJ * .7, reduced.Diagnostics.FatConsumedKJ);
        Near(normal.Diagnostics.RespiratoryWaterL, reduced.Diagnostics.RespiratoryWaterL);
        var half = kernel.Advance(body, EnvironmentSample.Neutral, input with { EnergyDemandMultiplier = .7 }, 2);
        var next = kernel.Advance(half.State, EnvironmentSample.Neutral, input with { EnergyDemandMultiplier = .7 }, 2);
        Near(reduced.State.GlycogenEnergyKJ, next.State.GlycogenEnergyKJ);
        Near(reduced.Diagnostics.MetabolicEnergyJ, half.Diagnostics.MetabolicEnergyJ + next.Diagnostics.MetabolicEnergyJ);
    }
    public static void Healing()
    {
        var kernel = new FrameworkKernel(); var profile = BodyProfile.Default; var body = BodyState.Create();
        var old = new FrameworkState { Vitals = new VitalState().Change(-10, 2) };
        var wound = new Wound { EventSequence = 1, Region = "left-arm", IntegrityDamage = 10, Severity = .5 };
        old = old with { Wounds = [wound], EventSequence = 1 };
        Near(kernel.WoundHealingRate(old, wound, body, profile, true) * 2,
            kernel.WoundHealingRate(old, wound, body, profile, true, 2));
        var activity = new ActivityContext(0, false, true, false, false, 1, true);
        var normal = kernel.Advance(old, body, profile, EnvironmentSample.Neutral, activity, 4);
        var boosted = kernel.Advance(old, body, profile, EnvironmentSample.Neutral, activity with { HealingMultiplier = 2 }, 4);
        if (!normal.Accepted || !boosted.Accepted) throw new Exception(normal.Error ?? boosted.Error);
        Near(normal.State.Wounds[0].Healing * 2, boosted.State.Wounds[0].Healing);
        var empty = boosted.State with { Nutrition = new NutritionState() };
        var paid = WoundRecovery.Settle(old, empty, body, profile, 4, true, 2);
        Near(0, paid.ProteinG); Near(0, paid.State.Wounds[0].Healing);
    }
}
