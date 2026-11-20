using Seraph.Core.Framework;

internal static class HandContaminationChecks
{
    internal static void Transfers()
    {
        var fecal = new Dictionary<string, double> { ["shigella"] = .8 };
        var state = HandContamination.AfterDefecation(new FrameworkState(), fecal);
        var food = HandContamination.TransferToFood(state, .2);
        Check(Math.Abs(food["shigella"] - .16) < 1e-12, "Fecal hand load should reach handled food.");

        var cleanWater = new WaterBatch { VolumeL = 1, EntericHazardPerL = .001 };
        var washedHands = HandContamination.AfterWashingProduce(state, cleanWater);
        Check(Math.Abs(washedHands.HandPathogens["shigella"] - .08) < 1e-12,
            "Clean water should substantially reduce hand residue.");

        var dirtyWater = new WaterBatch { VolumeL = 1, EntericHazardPerL = .5,
            PathogensPerL = new Dictionary<string, double> { ["ecoli"] = .4 } };
        var recontaminated = HandContamination.AfterWashingProduce(new FrameworkState(), dirtyWater);
        Check(recontaminated.HandPathogens.ContainsKey("ecoli")
            && !recontaminated.HandPathogens.ContainsKey("water-enteric"),
            "Identified and generic enteric load from one water source must not double count.");

        var restored = System.Text.Json.JsonSerializer.Deserialize<FrameworkState>(
            System.Text.Json.JsonSerializer.Serialize(state))!;
        Check(restored.HandPathogens["shigella"] == .8,
            "Hand contamination must survive the existing framework save format.");
    }

    private static void Check(bool condition, string message)
    { if (!condition) throw new Exception(message); }
}
