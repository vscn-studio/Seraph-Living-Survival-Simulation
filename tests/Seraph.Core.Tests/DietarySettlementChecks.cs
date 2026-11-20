using Seraph.Core.Framework;
using Seraph.Core.Simulation;
using Seraph.Core.State;

internal static class DietarySettlementChecks
{
    private static void Near(double expected, double actual, double tolerance = 1e-8)
    { if (Math.Abs(expected - actual) > tolerance) throw new Exception($"Expected {expected:R}, got {actual:R}"); }
    private static void Require(bool condition, string message) { if (!condition) throw new Exception(message); }
    private static readonly BodyProfile Profile = BodyProfile.Default;

    internal static void ReservesAndDeficiency()
    {
        Near(4 * 4.184, DietaryMetabolism.CarbKJPerG); Near(4 * 4.184, DietaryMetabolism.ProteinKJPerG);
        Near(9 * 4.184, DietaryMetabolism.FatKJPerG);
        Near(22, MicroNutrition.Gain(0, 22));
        Near(100, MicroNutrition.Gain(90, 100));
        Require(Nutrients.All.Where(n => n.IsMicro).All(n => n.Maximum == 100),
            "Micronutrient reserves must retain their 100-point saved scale.");
        Near(100d / 30, MicroNutrition.DailyPointsFor("a"));
        Near(100d / 7, MicroNutrition.DailyPointsFor("c"));
        Near(100d / 7, MicroNutrition.DailyPointsFor("bcomplex"));
        Near(4, MicroNutrition.HalfLifeHours("a"));
        Near(1, MicroNutrition.HalfLifeHours("c"));
        Near(2, MicroNutrition.HalfLifeHours("bcomplex"));
        Near(2.5, MicroNutrition.CumulativeAbsorption(4 * MicroNutrition.DailyPointsFor("c"), MicroNutrition.DailyPointsFor("c")));
        Require(MicroNutrition.CumulativeAbsorption(10 * MicroNutrition.DailyPointsFor("a"), MicroNutrition.DailyPointsFor("a")) < 10,
            "Large A/C/B doses bypassed diminishing absorption.");
        var n = NutritionState.Starting();
        foreach (var id in MicroNutrition.Groups) n.Reserve[Nutrients.IndexOf(id)] = 100;
        var body = BodyState.Create(Profile);
        var day = DietaryMetabolism.Digest(n, body, Profile, 86400, 1, 5000).Nutrition;
        var three = DietaryMetabolism.Digest(n, body, Profile, 3 * 86400, 1, 5000).Nutrition;
        foreach (var id in MicroNutrition.Groups)
        {
            var daily = MicroNutrition.DailyPointsFor(id);
            Near(100 - daily, day.Get(id).Reserve);
            Near(100 - daily * 3, three.Get(id).Reserve);
        }
        var low = MicroNutrition.Settle(0, 0, 0, 24); Near(1, low.Burden);
        Near(.5, MicroNutrition.Settle(0, 0, 0, 12).Burden);
        var recovered = MicroNutrition.Settle(80, 1, 0, 12); Near(0, recovered.Burden);
        Near(.5, MicroNutrition.Settle(80, 1, 0, 6).Burden);
        Near(0, MicroNutrition.Settle(100, 0, 0, 12).Burden);
        // Reserve crosses 20 halfway through this day: only the final 12 hours count.
        Near(.5, MicroNutrition.Settle(20 + MicroNutrition.DailyPoints / 2, 0, 0, 24).Burden);
        var unknown = new NutritionState();
        var unknownAfter = DietaryMetabolism.Digest(unknown, body, Profile, 86400, 1, 5000).Nutrition;
        Require(unknownAfter.DeficiencyBurden.All(x => x == 0) && unknownAfter.Known.All(k => !k), "Unknown became deficient or known.");
        var immune = new FrameworkState { Nutrition = n, ImmuneReserve = .8 };
        Near(.96, ImmuneSystem.Effective(immune));
        n.Reserve[Nutrients.IndexOf("iron")] = 10;
        Near(.8 * (1 + .2 * 5.5 / 6), ImmuneSystem.Effective(immune));
        n.DeficiencyBurden[Nutrients.IndexOf("iron")] = .5;
        Near(.75, NutrientInteractions.RecoveryMultiplier(n));
        Require(ClinicalEffects.Evaluate(immune, body, Profile).Single(e => e.Id == "anemia").Severity == .5,
            "Clinical severity did not follow saved burden.");
    }

    internal static void IndependentFlows()
    {
        var dry = new NutritionState { GutMassG = 500, StomachWasteG = 100 };
        for (var i = 0; i < 4; i++) dry.Pending[i] = 100;
        var energy = 100 * (4 + 4 + 9) * 4.184;
        var body = BodyState.Create(Profile) with { GutEnergyKJ = energy, GutWaterL = .2,
            BodyWaterL = Profile.ReferenceBodyWaterL - 1 };
        var halfHour = DietaryMetabolism.Digest(dry, body, Profile, 1800, 1, 5000);
        Near(.12928932188134512, halfHour.WaterL);
        Near(100 * Math.Pow(2, -.5 / 1.5), halfHour.Nutrition.Pending[0]);
        Near(100 * Math.Pow(2, -.5 / 2), halfHour.Nutrition.Pending[1]);
        Near(100 * Math.Pow(2, -.5 / 3), halfHour.Nutrition.Pending[2]);
        Near(100 * Math.Pow(2, -.5 / 3), halfHour.Nutrition.Pending[3]);
        var slow = DietaryMetabolism.Digest(dry, body, Profile, 3600, .5, 5000);
        Near(halfHour.Nutrition.Pending[0], slow.Nutrition.Pending[0]); Near(.175, slow.WaterL);
        var slowWater = DietaryMetabolism.Digest(dry, body, Profile, 3600, .5, 5000, waterAbsorptionEfficiency: .5);
        Near(halfHour.WaterL, slowWater.WaterL);
        var blocked = DietaryMetabolism.Digest(dry, body, Profile, 8, 1, 0);
        Near(100, blocked.Nutrition.Pending[3]); Near(100, blocked.Nutrition.StomachWasteG);
        Require(blocked.Nutrition.Pending[0] < 100 && blocked.WaterL > 0, "Bowel fullness blocked unrelated absorption.");
        var fullWater = DietaryMetabolism.Digest(dry, body with { BodyWaterL = Profile.ReferenceBodyWaterL }, Profile, 8, 1, 0);
        Near(0, fullWater.WaterL);
        Require(fullWater.Nutrition.Pending[0] < 100, "Full body water blocked dry absorption.");
        var waterHeavy = DietaryMetabolism.Digest(dry, body with { GutWaterL = 4, BodyWaterL = Profile.ReferenceBodyWaterL - 4 }, Profile, 3600, 1, 5000);
        Near(2, waterHeavy.WaterL);
        Near(700, halfHour.Nutrition.GutMassG + (body.GutWaterL - halfHour.WaterL) * 1000
            + halfHour.WaterL * 1000 + dry.BowelResidueG(halfHour.Nutrition)
            + Enumerable.Range(0, 3).Sum(i => dry.Pending[i] - halfHour.Nutrition.Pending[i]));
        Near(halfHour.RemovedEnergyKJ, halfHour.CarbohydrateKJ + halfHour.FatKJ + halfHour.ProteinKJ);
        Near(halfHour.ProteinKJ, halfHour.Nutrition.RepairEnergyKJ);

        // Saturated dry throughput, constrained bowel, old saved caloric entitlement.
        var meal = NutritionState.Starting() with { GutMassG = 4500, GutFoodWaterG = 1000, StomachWasteG = 400, RepairEnergyKJ = 37 };
        meal.Pending[0] = 1800; meal.Pending[1] = 500; meal.Pending[2] = 500; meal.Pending[3] = 300;
        meal.Pending[Nutrients.IndexOf("a")] = 20;
        var start = body with { GutEnergyKJ = 12345, GutWaterL = 2, BodyWaterL = Profile.ReferenceBodyWaterL - 2 };
        (NutritionState Nutrition, BodyState Body, double Fuel, double Repair, double AbsorbedWater) Run(double dt)
        {
            var n = meal; var b = start; double fuel = 0, repair = 0, water = 0;
            for (var t = 0d; t < 7200; t += dt)
            {
                var flux = DietaryMetabolism.Digest(n, b, Profile, Math.Min(dt, 7200 - t), 1,
                    Math.Max(0, 120 - n.BowelProducedG));
                n = flux.Nutrition; fuel += flux.CarbohydrateKJ + flux.FatKJ; repair += flux.ProteinKJ; water += flux.WaterL;
                b = b with { GutEnergyKJ = b.GutEnergyKJ - flux.RemovedEnergyKJ,
                    GutWaterL = b.GutWaterL - flux.WaterL, BodyWaterL = b.BodyWaterL + flux.WaterL };
            }
            Near(12345, b.GutEnergyKJ + fuel + repair, 1e-7);
            Near(37 + repair, n.RepairEnergyKJ, 1e-7);
            Near(5500, n.ContentsMassG(b.GutWaterL) + water * 1000 + n.BowelProducedG
                + Enumerable.Range(0, 3).Sum(i => meal.Pending[i] - n.Pending[i]), 1e-6);
            Require(n.BowelProducedG <= 120 + 1e-8 && 3500 - n.Pending.Take(4).Sum() - n.StomachWasteG <= 2000 + 1e-7,
                "Bowel or dry throughput capacity exceeded.");
            return (n, b, fuel, repair, water);
        }
        var small = Run(.5); var normal = Run(8); var sleep = Run(7200);
        // Independent exponentials are exact; competing capped flows use bounded numerical steps.
        Near(small.Body.GutEnergyKJ, normal.Body.GutEnergyKJ, 2);
        Near(normal.Body.GutEnergyKJ, sleep.Body.GutEnergyKJ, 1e-8);
        Near(small.Nutrition.Pending[0], normal.Nutrition.Pending[0], .5);
        Near(small.Nutrition.Get("a").Reserve, normal.Nutrition.Get("a").Reserve, .02);
    }

    internal static void SleepDigestion()
    {
        var profile = Profile with { BasalMetabolicPowerW = 0 };
        var kernel = new SimulationKernel(profile, new ModelOptions
        {
            EnableDryHeatExchange = false, EnableRespiration = false, EnableEvaporation = false,
            EnableSweating = false, EnableInsensibleWaterLoss = false, EnableFecalWaterLoss = false,
            EnablePlasmaRedistribution = false, MaximumShiveringPowerW = 0
        });
        var body = BodyState.Create(profile) with { GutWaterL = 1,
            BodyWaterL = profile.ReferenceBodyWaterL - 1,
            GutEnergyKJ = 100 * DietaryMetabolism.CarbKJPerG };
        var diet = new NutritionState { GutMassG = 100 };
        diet.Pending[0] = 100;
        diet.Pending[12] = 100;
        (BodyState Body, NutritionState Nutrition, double Water) Run(bool sleeping)
        {
            var currentBody = body;
            var currentDiet = diet;
            var water = 0d;
            for (var elapsed = 0; elapsed < 3600; elapsed += 8)
            {
                var result = kernel.Advance(currentBody, EnvironmentSample.Neutral,
                    new() { Nutrition = currentDiet, DigestionGPerHour = 10000, IsSleeping = sleeping }, 8);
                Require(result.Accepted && result.Nutrition is not null,
                    $"Sleep digestion transition was rejected: {result.Error}");
                currentBody = result.State;
                currentDiet = result.Nutrition!;
                water += result.Diagnostics.AbsorbedWaterL;
            }
            return (currentBody, currentDiet, water);
        }
        var awake = Run(false);
        var asleep = Run(true);
        Require(asleep.Nutrition.Pending[0] > awake.Nutrition.Pending[0],
            "Sleeping did not slow dietary absorption.");
        Near(100 * Math.Exp(-Math.Log(2) / 1), awake.Nutrition.Pending[12], 1e-7);
        Near(100 * Math.Exp(-Math.Log(2) * .25 / 1), asleep.Nutrition.Pending[12], 1e-7);
        Near(awake.Water, asleep.Water, 1e-8);
        Near(body.BodyWaterL + body.GutWaterL,
            awake.Body.BodyWaterL + awake.Body.GutWaterL, 1e-8);
        Near(body.BodyWaterL + body.GutWaterL,
            asleep.Body.BodyWaterL + asleep.Body.GutWaterL, 1e-8);
    }
}
