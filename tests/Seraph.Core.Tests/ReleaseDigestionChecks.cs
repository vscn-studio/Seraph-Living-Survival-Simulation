using Seraph.Core.Framework;
using Seraph.Core.State;

internal static class ReleaseDigestionChecks
{
    internal static void Run()
    {
        var profile = BodyProfile.Default;
        Near(525, profile.InitialGlycogenEnergyKJ / DietaryMetabolism.CarbKJPerG);
        Near(750, profile.GlycogenCapacityKJ / DietaryMetabolism.CarbKJPerG);
        Near(12000, profile.InitialFatEnergyKJ / DietaryMetabolism.FatKJPerG);
        var n = NutritionState.Starting() with { GutMassG = 200, StomachWasteG = 10 };
        n.Pending[0] = 100; n.Pending[1] = 50; n.Pending[2] = 20; n.Pending[3] = 20;
        n.Pending[Nutrients.IndexOf("c")] = 30;
        var body = BodyState.Create(profile) with { BodyWaterL = profile.ReferenceBodyWaterL - 3,
            GutEnergyKJ = 150 * DietaryMetabolism.CarbKJPerG + 20 * DietaryMetabolism.FatKJPerG };
        var dry = DietaryMetabolism.Digest(n, body, profile, 3600, 1, 5000);
        var wet = DietaryMetabolism.Digest(n, body with { GutWaterL = 2 }, profile, 3600, 1, 5000);
        Near(dry.Nutrition.Pending[12], wet.Nutrition.Pending[12]);
        Near(dry.Nutrition.Reserve[12], wet.Nutrition.Reserve[12]);
        Near(30, n.Pending[12]); // Caller-owned arrays must remain immutable.
        Near(200, dry.Nutrition.GutMassG + dry.Nutrition.BowelProducedG
            + dry.CarbohydrateKJ / DietaryMetabolism.CarbKJPerG
            + dry.ProteinKJ / DietaryMetabolism.ProteinKJPerG + dry.FatKJ / DietaryMetabolism.FatKJPerG);
        var empty = NutritionState.Starting(); empty.DeficiencyBurden[12] = 1;
        var recovered = DietaryMetabolism.Digest(empty, body with { GutEnergyKJ = 0 }, profile, 12 * 3600, 1, 0);
        Near(0, recovered.Nutrition.DeficiencyBurden[12]);
        Near(50 - MicroNutrition.DailyPointsFor("c") / 2, recovered.Nutrition.Reserve[12], 1e-8);
        // Day-sized sleep settlement must not allocate nutrient arrays on each 8-second substep.
        DietaryMetabolism.Digest(n, body, profile, 86400, 1, 0);
        var allocated = GC.GetAllocatedBytesForCurrentThread();
        var watch = System.Diagnostics.Stopwatch.StartNew();
        var day = DietaryMetabolism.Digest(n, body, profile, 86400, .15, 0);
        watch.Stop(); allocated = GC.GetAllocatedBytesForCurrentThread() - allocated;
        if (allocated > 8_000_000 || watch.Elapsed.TotalSeconds > 3) throw new Exception("Digestion allocation/time regression.");
        Near(30, day.Nutrition.Pending[3] + day.Nutrition.StomachWasteG);
        Console.WriteLine($"PERF bounded digestion day: {allocated} bytes, {watch.Elapsed.TotalMilliseconds:F2} ms");
        MultiDay();
    }

    private static void MultiDay()
    {
        // Four days of measured mixed food: 300 g carbohydrate, 90 g protein,
        // 80 g fat, 30 g fiber and 1.2 L food water daily. Intake times are test
        // events, not a meal schedule or bonus in the model. Additional water
        // replaces measured loss; sleep still pays the same basal demand.
        foreach (var workKcalPerHour in new[] { 0d, 160, 450 })
        foreach (var efficiency in new[] { 1d, .15 })
        {
            var p = BodyProfile.Default;
            var b = BodyState.Create(p);
            var n = NutritionState.Starting();
            var initialEnergy = b.GlycogenEnergyKJ + b.FatEnergyKJ;
            double eatenEnergy = 0, paid = 0, heat = 0, mass = 0, absorbedDry = 0, drank = 0, absorbedWater = 0;
            double peak = 0, dayThreeMass = 0;
            for (var hour = 0; hour < 96; hour++)
            {
                var time = hour % 24;
                if (time is 1 or 6 or 13 or 17)
                {
                    var pending = (double[])n.Pending.Clone();
                    pending[0] += 75; pending[1] += 22.5; pending[2] += 20; pending[3] += 7.5;
                    foreach (var id in MicroNutrition.Groups) pending[Nutrients.IndexOf(id)] += MicroNutrition.DailyPoints / 4;
                    var foodEnergy = (75 * 4 + 22.5 * 4 + 20 * 9) * 4.184;
                    n = n with { Pending = pending, GutMassG = n.GutMassG + 425, GutFoodWaterG = n.GutFoodWaterG + 300 };
                    b = b with { GutEnergyKJ = b.GutEnergyKJ + foodEnergy, GutWaterL = b.GutWaterL + .3 };
                    mass += 425; eatenEnergy += foodEnergy; drank += .3;
                }
                // Drink only the remaining physical deficit including pending water.
                var drink = Math.Max(0, p.ReferenceBodyWaterL - b.BodyWaterL - b.GutWaterL);
                b = b with { GutWaterL = b.GutWaterL + drink }; drank += drink;
                peak = Math.Max(peak, n.ContentsMassG(b.GutWaterL));
                for (var second = 0; second < 3600; second += 8)
                {
                    var flux = DietaryMetabolism.Digest(n, b, p, 8, efficiency, 5000);
                    absorbedDry += n.Pending.Take(3).Sum() - flux.Nutrition.Pending.Take(3).Sum();
                    n = flux.Nutrition;
                    var glycogen = b.GlycogenEnergyKJ; var fat = b.FatEnergyKJ;
                    heat += DietaryMetabolism.Deposit(ref glycogen, ref fat, p, flux.CarbohydrateKJ, flux.FatKJ);
                    // No repair requested in this ledger: unused protein is paid once as fuel.
                    heat += DietaryMetabolism.Deposit(ref glycogen, ref fat, p, n.RepairEnergyKJ, 0);
                    var reserve = (double[])n.Reserve.Clone(); reserve[1] = 0;
                    n = n with { Reserve = reserve, RepairEnergyKJ = 0 };
                    var demand = (70 + (time is >= 8 and < 16 ? workKcalPerHour : 0)) * 4.184 * 8 / 3600;
                    var use = Math.Min(glycogen + fat, demand); var fromGlycogen = Math.Min(glycogen, use);
                    glycogen -= fromGlycogen; fat -= use - fromGlycogen; paid += use;
                    absorbedWater += flux.WaterL;
                    b = b with { GlycogenEnergyKJ = glycogen, FatEnergyKJ = fat,
                        GutEnergyKJ = b.GutEnergyKJ - flux.RemovedEnergyKJ, GutWaterL = b.GutWaterL - flux.WaterL,
                        BodyWaterL = b.BodyWaterL + flux.WaterL - .075 * 8 / 3600 };
                }
                if (hour == 71) dayThreeMass = n.GutMassG;
            }
            Near(initialEnergy + eatenEnergy, b.GlycogenEnergyKJ + b.FatEnergyKJ + b.GutEnergyKJ + paid + heat, 1e-5);
            Near(drank, absorbedWater + b.GutWaterL, 1e-7);
            Near(mass - 4 * 1200, n.GutMassG - n.GutFoodWaterG + absorbedDry + n.BowelProducedG, 1e-6);
            if (peak > 5000 || efficiency == 1 && Math.Abs(n.GutMassG - dayThreeMass) > 1)
                throw new Exception("Ordinary portions accumulate permanently or overflow the stomach.");
            if (efficiency == 1 && n.DeficiencyBurden.Any(v => v > .001)) throw new Exception("Reference intake caused abrupt deficiency.");
        }
        var glycogenFull = BodyProfile.Default.GlycogenCapacityKJ;
        var fatFull = BodyProfile.Default.FatCapacityKJ;
        Near(200, DietaryMetabolism.Deposit(ref glycogenFull, ref fatFull, BodyProfile.Default, 100, 100));
        Console.WriteLine("PASS four-day measured intake, continuous rest/work/sleep demand, hydration, reduced digestion and full-store energy ledger");
    }
    private static void Near(double expected, double actual, double tolerance = 1e-8)
    { if (Math.Abs(expected - actual) > tolerance) throw new Exception($"Expected {expected}, actual {actual}"); }
}
