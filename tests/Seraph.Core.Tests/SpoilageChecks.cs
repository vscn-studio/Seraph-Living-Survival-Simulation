using Seraph.Core.Framework;
using Seraph.Core.State;

internal static class SpoilageChecks
{
    private static void Check(bool value, string message) { if (!value) throw new Exception(message); }
    private static void Near(double expected, double actual) => Check(Math.Abs(expected - actual) < 1e-8, $"Expected {expected}, got {actual}.");

    public static void Vomiting()
    {
        Near(0, SpoilageResponse.VomitProbability(.29));
        Near(.15, SpoilageResponse.VomitProbability(.3));
        Near(.75, SpoilageResponse.VomitProbability(1));
        var pending = new double[Nutrients.All.Length];
        pending[Nutrients.IndexOf("protein")] = 50;
        var frame = FrameworkState.Create("spoiled-food") with {
            ImmuneReserve = .8,
            Nutrition = NutritionState.Starting() with { GutMassG = 1000, GutFoodWaterG = 300, StomachWasteG = 100, Pending = pending }
        };
        var body = BodyState.Create() with { GutWaterL = .4, GutEnergyKJ = 800 };
        var originalRandom = frame.RandomState;
        var vomited = SpoilageResponse.TryVomit(frame, body, BodyProfile.Default, 1);
        Check(vomited.Triggered, "Fully spoiled food did not trigger deterministic vomiting.");
        Near(400, vomited.State.Nutrition.GutMassG);
        Near(120, vomited.State.Nutrition.GutFoodWaterG);
        Near(40, vomited.State.Nutrition.StomachWasteG);
        Near(20, vomited.State.Nutrition.Pending[Nutrients.IndexOf("protein")]);
        Near(.16, vomited.Body.GutWaterL);
        Near(320, vomited.Body.GutEnergyKJ);
        Near(.6, vomited.State.ImmuneReserve);
        Near(250, vomited.State.Syndromes.BowelContentsG);
        Check(vomited.State.RandomState != originalRandom && frame.Nutrition.GutMassG == 1000,
            "Vomiting failed to persist its random draw or mutated its input.");
        Check(vomited.State.Validate() is null && vomited.Body.Validate(BodyProfile.Default) is null,
            "Vomiting produced invalid saved state.");
    }
}
