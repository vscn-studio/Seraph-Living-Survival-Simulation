using System.Reflection;
using Seraph.Core.Framework;
using Seraph.Core.Simulation;
using Seraph.Core.State;

internal static class SleepNutritionChecks
{
    internal static void Run(Assembly mod)
    {
        var settle = mod.GetType("Seraph.Server.ServerCoordinator", true)!
            .GetMethod("SettleNutrition", BindingFlags.Static | BindingFlags.NonPublic)!;
        var diet = NutritionState.Starting() with { GutMassG = 30 };
        diet.Pending[Nutrients.IndexOf("protein")] = 30;
        var before = BodyState.Create() with { GutEnergyKJ = 500 };
        var step = new SimulationKernel(BodyProfile.Default).Advance(before, EnvironmentSample.Neutral, new() { Nutrition = diet }, 8);
        NutritionState Advance(NutritionState n) => (NutritionState)settle.Invoke(null, [n, before, step, 8d, 400d])!;
        var preview = Advance(diet);
        var committed = Advance(diet);
        var startingElectrolytes = diet.Get("electrolytes").Reserve;
        if (!preview.Reserve.SequenceEqual(committed.Reserve) || !preview.Pending.SequenceEqual(committed.Pending)
            || diet.Pending[Nutrients.IndexOf("protein")] != 30 || diet.Reserve[Nutrients.IndexOf("protein")] != 0
            || ReferenceEquals(diet.Reserve, preview.Reserve)) throw new Exception("Sleep preview mutated live nutrition.");
        var unpaid = diet.Advance(8, 50, 500, 8d / 86400);
        if (Math.Abs(unpaid.Get("protein").Reserve - preview.Get("protein").Reserve) > 1e-10
            || preview.Get("protein").Pending >= 30 || preview.Get("electrolytes").Reserve >= startingElectrolytes)
            throw new Exception("Sleep settlement omitted digestion, muscle protein payment or calendar depletion.");
        // Preview and live execution consume the same accepted-step deltas, never cumulative totals.
        var meal = new NutritionState { GutMassG = 300, StomachWasteG = 200 };
        meal.Pending[Nutrients.IndexOf("carbohydrate")] = 100;
        var foodStep = new SimulationKernel(BodyProfile.Default).Advance(before, EnvironmentSample.Neutral,
            new() { Nutrition = meal }, 8);
        if (!foodStep.Accepted) throw new Exception(foodStep.Error);
        NutritionState SettleMeal() => (NutritionState)settle.Invoke(null, [meal, before, foodStep, 8d, 400d])!;
        var initial = new InteractionState { BowelUnits = 500, PendingBowelUnits = 20 };
        var previewDiet = SettleMeal(); var liveDiet = SettleMeal();
        var previewBowel = BowelSettlement.Advance(initial, meal.BowelResidueG(previewDiet), 10, 8);
        var liveBowel = BowelSettlement.Advance(initial, meal.BowelResidueG(liveDiet), 10, 8);
        var total = initial.BowelUnits + initial.PendingBowelUnits + meal.BowelResidueG(liveDiet) + 10;
        if (previewBowel != liveBowel || Math.Abs(liveBowel.BowelUnits + liveBowel.PendingBowelUnits - total) > 1e-9
            || initial.BowelUnits != 500 || meal.BowelProducedG != 0 || meal.StomachWasteG != 200)
            throw new Exception("Sleep/live bowel settlement duplicated a source or mutated the preview input.");
        Console.WriteLine("PASS shared sleep nutrition settlement: immutable preview, digestion, protein payment and depletion");
    }
}
