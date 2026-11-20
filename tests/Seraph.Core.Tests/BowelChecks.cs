using Seraph.Core.Framework;

internal static class BowelChecks
{
    internal static void Conservation()
    {
        static void Near(double a, double b) { if (Math.Abs(a - b) > 1e-7) throw new Exception($"{a} != {b}"); }
        var before = new NutritionState { GutMassG = 1000, GutFoodWaterG = 900, StomachWasteG = 100 };
        var after = before.Advance(8, 100, 100);
        Near(before.BowelResidueG(after), before.StomachWasteG - after.StomachWasteG);
        if (after.StomachWasteG <= 99) throw new Exception("Water-rich meal processed too quickly.");
        Near(new NutritionState().BowelResidueG(new NutritionState()), 0);
        var fiber = new NutritionState { GutMassG = 100 };
        fiber.Pending[Nutrients.IndexOf("fiber")] = 100;
        var digested = fiber.Advance(8, 0, 0);
        Near(fiber.BowelResidueG(digested), fiber.GutMassG - digested.GutMassG);
        var full = new InteractionState { BowelUnits = 5000, PendingBowelUnits = 4500 };
        var queued = BowelSettlement.Advance(full, 100, 0, 8);
        Near(queued.BowelUnits, 5000); Near(queued.PendingBowelUnits, 4600);
        var a = new InteractionState(); var b = a;
        for (var t = 0; t < 3600; t += 8) a = BowelSettlement.Advance(a, .08, 96d / 450, 8);
        for (var t = 0; t < 3600; t++) b = BowelSettlement.Advance(b, .01, 96d / 3600, 1);
        Near(a.BowelUnits + a.PendingBowelUnits, 132);
        Near(a.BowelUnits, b.BowelUnits); Near(a.PendingBowelUnits, b.PendingBowelUnits);
        foreach (var mass in new[] { 500d, 999d, 1000d, 1500d, 1999d, 4999d, 5000d })
        {
            var plan = BowelSettlement.PlanDefecation(mass, 64);
            Near(plan.MassG, mass);
            Near(plan.ProductMassG, plan.Items * 1000);
            Near(plan.DiscardedG, mass % 1000);
            Near(plan.MassG, plan.ProductMassG + plan.DiscardedG);
            Near(new InteractionState { BowelUnits = mass }.AfterDefecation(plan.MassG), 0);
            if (plan.Items != (int)(mass / 1000)) throw new Exception("Wrong portion count");
        }
        Near(BowelSettlement.PlanDefecation(999, 0).DiscardedG, 999);
        var partial = BowelSettlement.PlanDefecation(4999, 2);
        Near(partial.MassG, 2000);
        Near(new InteractionState { BowelUnits = 4999 }.AfterDefecation(partial.MassG), 2999);
        if (BowelSettlement.PlanDefecation(499, 64).Items != 0 || BowelSettlement.PlanDefecation(5000, 0).Items != 0)
            throw new Exception("Capacity/minimum bypass");
    }
}
