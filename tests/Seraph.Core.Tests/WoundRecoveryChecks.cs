using Seraph.Core.Framework;
using Seraph.Core.State;
using Seraph.Core.Simulation;

internal static class WoundRecoveryChecks
{
    public static void AllKinds()
    {
        var kernel = new FrameworkKernel();
        var profile = BodyProfile.Default;
        foreach (var kind in new[] { WoundKind.Cut, WoundKind.Puncture, WoundKind.Contusion, WoundKind.Burn,
            WoundKind.Frostbite, WoundKind.ElectricBurn, WoundKind.PoisonWound, WoundKind.Generic })
        {
            var frame = FrameworkState.Create("wound-kinds") with { Dirtiness = 1,
                Vitals = new VitalState().Change(-3, 4),
                SkinPathogens = new Dictionary<string, double> { ["wound-staph"] = 3 } };
            frame.Nutrition.Reserve[1] = 10;
            var bleed = kind is WoundKind.Cut or WoundKind.Puncture ? .0005 : 0;
            frame = kernel.RecordInjury(frame, new("wound", kind, "left-leg", .2, InjuryEvidence.Verified, 0,
                bleed, IntegrityDamage: 3)).State;
            if (!WoundCare.Open(kind, bleed) && frame.Wounds[0].Pathogens.Count != 0) Fail("Closed trauma acquired skin pathogens.");
            if (WoundCare.SelectForCare(frame) == 0) Fail("Nonbleeding wound cannot be tended.");
            var dressed = WoundCare.Bandage(frame, frame.Wounds[0].EventSequence, true);
            if (WoundCare.Rate(dressed.Wounds[0]) != 0 || WoundCare.SelectForCare(dressed) != 0) Fail("Care replay or ineffective hemostasis.");
            if (kernel.WoundHealingRate(dressed, dressed.Wounds[0], BodyState.Create(), profile, true)
                <= kernel.WoundHealingRate(frame, frame.Wounds[0], BodyState.Create(), profile, true)) Fail("Dressing has no repair effect.");
            var body = BodyState.Create();
            var advanced = kernel.Advance(frame, body, profile, EnvironmentSample.Neutral, new(0, false, true, false, false, 1, true), 8);
            if (!advanced.Accepted) Fail(advanced.Error!);
            if (bleed > 0 && advanced.State.Wounds[0].BleedLps >= bleed) Fail("No natural coagulation.");
            var paid = WoundRecovery.Settle(frame, advanced.State, body, profile, 8, false);
            var hp = paid.State.Vitals.Health - frame.Vitals.Health;
            if (hp <= 0 || paid.State.Vitals.Integrity[5] != 15) Fail("Closure did not repair the injured region only.");
            Near(hp * 2, paid.ProteinG); Near(hp * 41.84, paid.EnergyKJ);
            Near(paid.ProteinG, frame.Nutrition.Get("protein").Reserve - paid.State.Nutrition.Get("protein").Reserve);
            Near(paid.EnergyKJ, body.GlycogenEnergyKJ + body.FatEnergyKJ - paid.Body.GlycogenEnergyKJ - paid.Body.FatEnergyKJ);
            var starved = advanced.State with { Nutrition = new NutritionState() };
            var unpaid = WoundRecovery.Settle(frame, starved, body, profile, 8, false);
            Near(frame.Wounds[0].Healing, unpaid.State.Wounds[0].Healing); Near(frame.Vitals.Health, unpaid.State.Vitals.Health);
            // A wound proposed to disappear must survive if payment is unavailable.
            var finished = WoundRecovery.Settle(frame, starved with { Wounds = [] }, body, profile, 8, false);
            Near(frame.Wounds[0].Healing, finished.State.Wounds.Single().Healing);
            Near(frame.Wounds[0].BleedLps, finished.State.Wounds.Single().BleedLps);
        }
        var almost = FrameworkState.Create("closure-payment") with { EventSequence = 1, Wounds = [new Wound {
            EventSequence = 1, Severity = .2, Healing = .999999, BleedLps = .001, IntegrityDamage = 3, Region = "left-leg" }],
            Nutrition = new NutritionState(), Vitals = new VitalState().Change(-.01, 4) };
        var proposal = kernel.Advance(almost, BodyState.Create(), profile, EnvironmentSample.Neutral,
            new(0, false, true, false, false, 1, true), 8).State;
        var retained = WoundRecovery.Settle(almost, proposal, BodyState.Create(), profile, 8, false);
        Near(almost.Wounds[0].Healing, retained.State.Wounds.Single().Healing);
        Near(proposal.Wounds.Single().BleedLps, retained.State.Wounds.Single().BleedLps);
        if (WoundCare.Rate(retained.State.Wounds.Single()) <= 0) Fail("Unpaid final closure silently stopped bleeding.");
        var wound = new Wound { Severity = 1, Healing = .2, IntegrityDamage = 10, Region = "left-leg" };
        var original = FrameworkState.Create("partition") with { Wounds = [wound], Vitals = new VitalState().Change(-8, 4) };
        original.Nutrition.Reserve[1] = 10;
        WoundRecoveryResult Run(FrameworkState f, BodyState b, double target) => WoundRecovery.Settle(f,
            f with { Wounds = [f.Wounds[0] with { Healing = target }] }, b, profile, 8, false);
        var all = Run(original, BodyState.Create(), .4);
        var half = Run(original, BodyState.Create(), .3);
        var split = Run(half.State, half.Body, .4);
        Near(all.State.Vitals.Health, split.State.Vitals.Health);
        Near(all.Body.GlycogenEnergyKJ, split.Body.GlycogenEnergyKJ);
        Near(all.State.Nutrition.Get("protein").Reserve, split.State.Nutrition.Get("protein").Reserve);
    }
    private static void Near(double a, double b) { if (Math.Abs(a - b) > 1e-7) Fail($"{a} != {b}"); }
    private static void Fail(string message) => throw new Exception(message);
}
