using Seraph.Core.Framework;
using Seraph.Core.Simulation;
using Seraph.Core.State;

internal static class NutritionBalanceChecks
{
    internal static void BandsAndDisplay()
    {
        foreach (var n in Nutrients.All)
        {
            var normal = new NutrientStatus(true, n.Maximum * .3, 0, n.DailyBudget, 0);
            var higher = normal with { Reserve = n.Maximum * .79 };
            Check(normal.Sufficiency == higher.Sufficiency && normal.Sufficiency == 1, "Normal band still changes with every intake.");
            Check((normal with { Reserve = n.Maximum * .2 }).Sufficiency == 1, "Nonempty reserve incorrectly penalized.");
            Check((normal with { Reserve = n.Maximum * .05 }).Sufficiency == 1, "Nonempty reserve incorrectly penalized.");
            Check((normal with { Reserve = 0 }).Sufficiency == 0, "Empty reserve grants support.");
            Near(100, ReserveDisplay.Score(n.Maximum, n.Maximum));
        }
        Near(.36, NutritionBands.DeficiencyEffect(.36));
        Near(.34, NutritionBands.DeficiencyEffect(.34));
        Check(NutritionBands.Stage(.01) == NutrientStage.Empty
            && NutritionBands.Stage(.02) == NutrientStage.Normal
            && NutritionBands.Stage(80) == NutrientStage.Normal
            && NutritionBands.Stage(80.01) == NutrientStage.Abundant,
            "Exhaustion/normal/high thresholds disagree.");
        var profile = BodyProfile.Default;
        var body = BodyState.Create();
        Near(8000, ReserveDisplay.HydrationG(body, profile));
        Near(7000, ReserveDisplay.HydrationG(body with { BodyWaterL = body.BodyWaterL - 1, GutWaterL = 1 }, profile));
        Near(8000, ReserveDisplay.HydrationG(body with { BodyWaterL = body.BodyWaterL + .1 }, profile));
        Near(0, ReserveDisplay.HydrationG(body with { BodyWaterL = body.BodyWaterL - 9 }, profile));
        Near(25, ReserveDisplay.Score(100, 400));
    }

    internal static void DigestionPace()
    {
        var profile = BodyProfile.Default with { BasalMetabolicPowerW = 0 };
        var kernel = new SimulationKernel(profile, new() {
            EnableDryHeatExchange = false, EnableRespiration = false, EnableEvaporation = false,
            EnableSweating = false, EnableInsensibleWaterLoss = false, EnableFecalWaterLoss = false,
            MaximumShiveringPowerW = 0
        });
        foreach (var meal in new[] { 100d, 1000d, 5000d })
        {
            BodyState Run(double step, int hours)
            {
                var body = BodyState.Create(profile) with { GutEnergyKJ = meal, GlycogenEnergyKJ = 0, FatEnergyKJ = 0 };
                var paid = 0d;
                for (var t = 0d; t < hours * 3600; t += step)
                {
                    var result = kernel.Advance(body, EnvironmentSample.Neutral, new(), Math.Min(step, hours * 3600 - t));
                    Check(result.Accepted, result.Error ?? "Digestion rejected.");
                    paid += result.Diagnostics.AbsorbedEnergyKJ;
                    body = result.State;
                }
                Near(meal, paid + body.GutEnergyKJ, 1e-7);
                return body;
            }
            var hour = Run(8, 1);
            Check(hour.GutEnergyKJ >= meal * .6, "Small meal disappeared too quickly.");
            Check(meal - hour.GutEnergyKJ <= 1000 * 4 * 4.184 + .001, "Energy-only queue bypassed carbohydrate throughput cap.");
            Near(hour.GutEnergyKJ, Run(3, 1).GutEnergyKJ, .001);
            Check(Run(8, 6).GutEnergyKJ < meal * .1, "Ordinary digestion stalled.");
        }
    }

    private static void Check(bool condition, string reason) { if (!condition) throw new Exception(reason); }
    private static void Near(double expected, double actual, double tolerance = 1e-9)
        => Check(Math.Abs(expected - actual) <= tolerance, $"Expected {expected:R}, actual {actual:R}");
}
