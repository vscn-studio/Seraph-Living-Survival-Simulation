using System.Text.Json;
using Seraph.Core.Framework;
using Seraph.Core.State;
using Seraph.Core.Simulation;

internal static class CirculationChecks
{
    public static void Flows()
    {
        var p = BodyProfile.Default; var body = BodyState.Create(); var frame = FrameworkState.Create("circulation");
        var charged = BloodCirculation.Charge(frame, ExertionKind.Sprinting, 1);
        Near(.1, charged.Blood.LactateG);
        var strong = BloodCirculation.Settle(charged, body, body, p, new(), 0, 8, 400, false);
        Near(.1, strong.State.Blood.LactateG);
        var rested = BloodCirculation.Settle(charged, body, body, p, new(), 0, 8, 400, true);
        Near(0, rested.State.Blood.LactateG);
        Near((.1 - rested.State.Blood.LactateG) * .2, rested.State.Blood.WasteMl + rested.BowelG / 1.2);
        var full = BloodCirculation.Settle(frame with { Blood = new() { WasteMl = 1000 } }, body, body, p,
            new() { GlycogenConsumedKJ = 4.184, FatConsumedKJ = 4.184 }, 0, 8, 0, true);
        Near(1000 + 1.3 * .4, full.State.Blood.WasteMl); Near(0, full.BowelG);
        var loss = BloodCirculation.Settle(charged with { Blood = new() { LactateG = 10, WasteMl = 100 } }, body, body, p,
            new() { WholeBloodLossL = p.ReferenceBloodVolumeL / 2 }, 0, 8, 0, false);
        Near(5, loss.State.Blood.LactateG); Near(50, loss.State.Blood.WasteMl);
        if (full.State.Blood.Toxicity <= 0 || ImmuneSystem.Effective(full.State) >= ImmuneSystem.Effective(frame)) throw new Exception("Blood toxins have no immune effect.");
        var one = BloodCirculation.Settle(charged, body, body, p, new(), 0, 8, 400, true);
        var half = BloodCirculation.Settle(charged, body, body, p, new(), 0, 4, 400, true);
        var split = BloodCirculation.Settle(half.State, half.Body, half.Body, p, new(), 0, 4, 400 - half.BowelG, true);
        Near(one.State.Blood.LactateG, split.State.Blood.LactateG); Near(one.State.Blood.WasteMl, split.State.Blood.WasteMl);
        Near(one.BowelG, half.BowelG + split.BowelG);
        var legacy = JsonSerializer.Deserialize<FrameworkState>(JsonSerializer.Serialize(FrameworkState.Create("release-defaults")))!;
        if (legacy.Blood.Validate() is not null || legacy.Validate() is not null) throw new Exception("Release blood defaults invalid.");
        if (JsonSerializer.Deserialize<FrameworkState>(JsonSerializer.Serialize(full.State))!.Blood != full.State.Blood) throw new Exception("Solutes lost on save.");
    }
    public static void Regeneration()
    {
        var p = BodyProfile.Default; var healthy = BodyState.Create(); var f = FrameworkState.Create("regen");
        f.Nutrition.Reserve[1] = 10;
        var body = healthy with { RbcVolumeL = healthy.RbcVolumeL * .8, PlasmaWaterL = healthy.PlasmaWaterL * .8 };
        var paid = BloodCirculation.Settle(f, body, body, p, new(), 0, 8, 400, true);
        if (paid.Body.RbcVolumeL <= body.RbcVolumeL || paid.Body.Validate(p) is not null || paid.State.Validate() is not null) throw new Exception("No valid red-cell recovery.");
        Near(body.BodyWaterL, paid.Body.BodyWaterL);
        var ml = (paid.Body.BloodVolumeL(p) - body.BloodVolumeL(p)) * 1000;
        Near(ml * .17, f.Nutrition.Get("protein").Reserve - paid.State.Nutrition.Get("protein").Reserve);
        Near(ml * .7 * 4.184, paid.EnergyKJ);
        Near(paid.EnergyKJ, body.GlycogenEnergyKJ + body.FatEnergyKJ - paid.Body.GlycogenEnergyKJ - paid.Body.FatEnergyKJ);
        var noProtein = BloodCirculation.Settle(f with { Nutrition = new() }, body, body, p, new(), 0, 8, 400, true);
        Near(body.RbcVolumeL, noProtein.Body.RbcVolumeL);
        var bleeding = BloodCirculation.Settle(f, body, body, p, new() { WholeBloodLossL = .01 }, 0, 8, 400, true);
        Near(body.RbcVolumeL, bleeding.Body.RbcVolumeL);
        var full = BloodCirculation.Settle(f, healthy, healthy, p, new(), 0, 8, 400, true);
        Near(0, full.EnergyKJ);
    }
    public static void WasteSources()
    {
        var body = BodyState.Create(); var p = BodyProfile.Default; var f = FrameworkState.Create("waste");
        var paid = BloodCirculation.Settle(f, body, body, p,
            new() { MuscleFuelKJ = 3.5 * 4.184 }, 2, 8, 0, false);
        Near((3.5 + 7) * .5 * 1.3, paid.State.Blood.WasteMl);
        var refreshed = BloodCirculation.AddWaste(f, body, p, BloodCirculation.WasteCapacityMl(body, p));
        Near(1, refreshed.Blood.Toxicity);
        var diet = f.Nutrition with { PendingWaterWasteMl = 150, AbsorbedWaterWasteMl = 25 };
        var wet = body with { GutWaterL = 1, BodyWaterL = body.BodyWaterL - 2 };
        var whole = DietaryMetabolism.Digest(diet, wet, p, 8, 1, 5000);
        Near(25, whole.Nutrition.AbsorbedWaterWasteMl);
        Near(0, whole.Nutrition.PendingWaterWasteMl);
        Near(1, whole.Nutrition.WaterWasteSchema);
        var half = DietaryMetabolism.Digest(diet, wet, p, 4, 1, 5000);
        var split = DietaryMetabolism.Digest(half.Nutrition,
            wet with { GutWaterL = wet.GutWaterL - half.WaterL, BodyWaterL = wet.BodyWaterL + half.WaterL }, p, 4, 1, 5000);
        Near(whole.Nutrition.AbsorbedWaterWasteMl, split.Nutrition.AbsorbedWaterWasteMl);
        Near(whole.Nutrition.PendingWaterWasteMl, split.Nutrition.PendingWaterWasteMl);
        var full = DietaryMetabolism.Digest(diet, body with { GutWaterL = 1 }, p, 8, 1, 5000);
        Near(25, full.Nutrition.AbsorbedWaterWasteMl); Near(0, full.Nutrition.PendingWaterWasteMl);
        var saved = JsonSerializer.Deserialize<NutritionState>(JsonSerializer.Serialize(split.Nutrition))!;
        Near(split.Nutrition.PendingWaterWasteMl, saved.PendingWaterWasteMl);
        Near(split.Nutrition.AbsorbedWaterWasteMl, saved.AbsorbedWaterWasteMl);
        var old = JsonSerializer.Deserialize<NutritionState>(JsonSerializer.Serialize(new NutritionState()))!;
        Near(0, old.PendingWaterWasteMl); Near(0, old.AbsorbedWaterWasteMl);
        var lactate = f with { Blood = new() { LactateG = 10 } };
        var mixed = BloodCirculation.Settle(lactate, body, body, p, new(), 0, 8, 0, true, restFraction: .25);
        Near(10 - 400d * 2 / 3600, mixed.State.Blood.LactateG);
        var awake = BloodCirculation.Settle(lactate, body, body, p, new(), 0, 8, 0, true,
            realTimeExercise: true);
        var asleep = BloodCirculation.Settle(lactate, body, body, p, new(), 0, 8, 0, true,
            realTimeExercise: true, sleeping: true);
        if (!(asleep.State.Blood.LactateG < awake.State.Blood.LactateG))
            throw new Exception("Sleep did not clear lactate against game time.");
    }
    public static void RenalClearance()
    {
        var p = BodyProfile.Default; var body = BodyState.Create(); var options = new CirculationOptions();
        var initial = FrameworkState.Create("renal");
        var fullReserve = (double[])initial.Nutrition.Reserve.Clone();
        fullReserve[Nutrients.IndexOf("electrolytes")] = Nutrients.All[Nutrients.IndexOf("electrolytes")].Maximum;
        var frame = initial with { Blood = new() { WasteMl = 1200 }, Nutrition = initial.Nutrition with { Reserve = fullReserve } };
        Near(1, BloodCirculation.RenalSupport(frame, body, p, 8, options));
        var boosted = BloodCirculation.Settle(frame, body, body, p, new(), 0, 8, 400, true);
        var ordinary = BloodCirculation.Settle(frame, body, body, p, new(), 0, 8, 400, true, options, 0);
        Near(72, ordinary.State.Blood.FiltrationMlPerGameHour);
        Near(0, boosted.Body.BladderUrineL); Near(0, boosted.Body.UrineProductionMlPerGameHour);
        var legacyBladder = body with { BladderUrineL = .2 };
        Near(.2, BloodCirculation.Settle(frame, legacyBladder, legacyBladder, p, new(), 0, 8, 400, true).Body.BladderUrineL);
        var rawThreshold = BloodCirculation.AddWaste(FrameworkState.Create("raw-threshold"), body, p,
            BloodCirculation.WasteCapacityMl(body, p) * .51);
        if (rawThreshold.Blood.Toxicity >= .5) throw new Exception("Threshold fixture uses mapped toxicity.");
        Near(.5, BloodCirculation.RenalSupport(rawThreshold, body, p, 8, options));
        Near(2 * ordinary.BowelG, boosted.BowelG);
        Near(0, frame.Nutrition.Get("electrolytes").Reserve - boosted.State.Nutrition.Get("electrolytes").Reserve);
        Near(frame.Blood.WasteMl, boosted.State.Blood.WasteMl + boosted.BowelG / 1.2);
        var scarceReserve = (double[])frame.Nutrition.Reserve.Clone(); scarceReserve[Nutrients.IndexOf("electrolytes")] = .001;
        var scarce = frame with { Nutrition = frame.Nutrition with { Reserve = scarceReserve } };
        var limited = BloodCirculation.Settle(scarce, body, body, p, new(), 0, 8, 400, true);
        Near(.001, limited.State.Nutrition.Get("electrolytes").Reserve);
        Near(.001 / Nutrients.All[Nutrients.IndexOf("electrolytes")].Maximum,
            BloodCirculation.RenalSupport(scarce, body, p, 8, options));
        if (limited.BowelG < ordinary.BowelG - 1e-5)
            throw new Exception("Low electrolytes reduced filtration below the ordinary rate.");
        var exhausted = BloodCirculation.Settle(limited.State, body, body, p, new(), 0, 8, 400, true);
        Near(.001 / Nutrients.All[Nutrients.IndexOf("electrolytes")].Maximum,
            BloodCirculation.RenalSupport(exhausted.State, body, p, 8, options));
        if (!exhausted.State.Blood.RenalHyperactivity) throw new Exception("Partial electrolyte reserve lost its proportional renal support.");
        var zeroReserve = (double[])exhausted.State.Nutrition.Reserve.Clone(); zeroReserve[Nutrients.IndexOf("electrolytes")] = 0;
        var depleted = BloodCirculation.Settle(exhausted.State with { Nutrition = exhausted.State.Nutrition with { Reserve = zeroReserve } },
            body, body, p, new(), 0, 8, 400, true);
        Near(0, BloodCirculation.RenalSupport(depleted.State, body, p, 8, options));
        if (depleted.State.Blood.RenalHyperactivity) throw new Exception("Renal boost remained active without electrolyte support.");
        var nonToxic = frame with { Blood = new() };
        Near(0, BloodCirculation.RenalSupport(nonToxic, body, p, 8, options));
        var full = BloodCirculation.Settle(frame, body, body, p, new(), 0, 8, 0, true);
        Near(0, full.BowelG); Near(frame.Nutrition.Get("electrolytes").Reserve, full.State.Nutrition.Get("electrolytes").Reserve);
        var kernel = new SimulationKernel(p, new() { MaximumShiveringPowerW = 0 });
        var normal = kernel.Advance(body, EnvironmentSample.Neutral, new(), 8);
        var renal = kernel.Advance(body, EnvironmentSample.Neutral, new() { BasalMetabolismMultiplier = 1.55 }, 8);
        if (!renal.Accepted || !normal.Accepted) throw new Exception("Renal energy step failed.");
        Near(normal.Diagnostics.BasalEnergyJ * 1.55, renal.Diagnostics.BasalEnergyJ);
        Near(renal.Diagnostics.MetabolicEnergyJ - normal.Diagnostics.MetabolicEnergyJ,
            (renal.Diagnostics.GlycogenConsumedKJ + renal.Diagnostics.FatConsumedKJ
            - normal.Diagnostics.GlycogenConsumedKJ - normal.Diagnostics.FatConsumedKJ) * 1000);
        Near(0, renal.Diagnostics.HeatBalanceResidualJ); Near(0, renal.Diagnostics.FuelBalanceResidualKJ);
        if (boosted.State.Validate() is not null || scarceReserve[Nutrients.IndexOf("electrolytes")] != .001)
            throw new Exception("Renal clearance mutated its input or produced invalid state.");
    }
    private static void Near(double a, double b) { if (Math.Abs(a - b) > 1e-7) throw new Exception($"{a} != {b}"); }
}
