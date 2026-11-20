using Seraph.Core.Framework;
using Seraph.Core.State;

internal static class AdrenalineChecks
{
    internal static void Effects()
    {
        var body = BodyState.Create(); var profile = BodyProfile.Default;
        var baseline = FrameworkState.Create("adrenaline") with {
            Wounds = [new() { Kind = WoundKind.Cut, Region = "left-arm", EventSequence = 1,
                Severity = .2, PainIntensity = .8, BleedLps = .001, InitialBleedLps = .001 }],
            Blood = new() { LactateG = 30 }
        };
        var active = baseline with { AdrenalineLevel = 1,
            AdrenalineRemainingSeconds = 60, AdrenalineCooldownRemainingSeconds = 43200 };
        var kernel = new FrameworkKernel(new() { IntegrateBleeding = true });
        var nutrition = new NutritionState { Known = Enumerable.Repeat(true, Nutrients.All.Length).ToArray(),
            Reserve = Nutrients.All.Select(n => n.Maximum).ToArray() };
        var empty = new NutritionState();
        Near(1, NutrientInteractions.BleedingMultiplier(empty));
        Near(.8, NutrientInteractions.BleedingMultiplier(nutrition));
        Near(kernel.BleedLps(baseline with { Nutrition = empty }, 0) * .8,
            kernel.BleedLps(baseline with { Nutrition = nutrition }, 0));
        var partial = nutrition with { Known = (bool[])nutrition.Known.Clone() };
        partial.Known[Nutrients.IndexOf("k")] = false;
        Near(.8, NutrientInteractions.BleedingMultiplier(partial));
        partial.Known[Nutrients.IndexOf("d")] = false;
        Near(.8, NutrientInteractions.BleedingMultiplier(partial));
        partial.Known[Nutrients.IndexOf("zinc")] = false;
        Near(.85, NutrientInteractions.BleedingMultiplier(partial));
        Near(kernel.BleedLps(baseline, 0) * .35, kernel.BleedLps(active, 0));
        Near(kernel.BleedLps(baseline, 0, 1) * .35, kernel.BleedLps(active, 0, 1));
        if (kernel.BleedLps(active, 0, 1) >= kernel.BleedLps(active, 0)) throw new Exception("Pressure lost its benefit.");
        Near(Treatments.Pain(baseline) * .5, Treatments.Pain(active));
        Near(BodyFunctionResolver.Digestion(baseline, body) * .6, BodyFunctionResolver.Digestion(active, body));
        var normal = BodyFunctionResolver.Evaluate(baseline, body, profile);
        var enhanced = BodyFunctionResolver.Evaluate(active, body, profile);
        Near(normal.Mobility * 1.5, enhanced.Mobility);
        Near(1.2, enhanced.MeleeAttackSpeed); Near(normal.Attack, enhanced.Attack);
        Near(normal.Circulation, enhanced.Circulation); Near(normal.RespiratoryCapacity, enhanced.RespiratoryCapacity);
        Near(BloodCirculation.Filtration(baseline, body, profile), BloodCirculation.Filtration(active, body, profile));
        var rest = BloodCirculation.RecoverExercise(active, ExertionKind.Standing, 5, 1, new());
        var split = BloodCirculation.RecoverExercise(active, ExertionKind.Standing, 2, 1, new());
        split = BloodCirculation.RecoverExercise(split, ExertionKind.Standing, 3, 1, new());
        Near(rest.Blood.LactateG, split.Blood.LactateG);
        Near(30, BloodCirculation.RecoverExercise(active, ExertionKind.Running, 5, 1, new()).Blood.LactateG);
        var clean = SmokeInhalation.Advance(0, 0, 10 * AdrenalineEffects.Ventilation(active), .5);
        Near(0, clean);
        var smoke = SmokeInhalation.Advance(0, 1, 10, .5);
        Near(smoke * 1.25, SmokeInhalation.Advance(0, 1, 10 * AdrenalineEffects.Ventilation(active), .5));
        var expired = kernel.AdvanceAdrenaline(active, body, 60);
        Near(Treatments.Pain(baseline), Treatments.Pain(expired.State));
        Near(1, AdrenalineEffects.AttackSpeed(expired.State));
        Near(kernel.BleedLps(baseline, 0), kernel.BleedLps(expired.State, 0));
        Near(body.BloodVolumeL(profile), expired.Body.BloodVolumeL(profile));
        Near(body.GlycogenEnergyKJ, expired.Body.GlycogenEnergyKJ);
        Near(30, expired.State.Blood.LactateG);
        Near(43200, expired.State.AdrenalineCooldownRemainingSeconds);
        Near(3600, AdrenalineEffects.AdvanceGameTimeCooldown(expired.State, 39600).AdrenalineCooldownRemainingSeconds);
    }
    private static void Near(double expected, double actual)
    { if (Math.Abs(expected - actual) > 1e-8) throw new Exception($"Expected {expected}, got {actual}."); }
}
