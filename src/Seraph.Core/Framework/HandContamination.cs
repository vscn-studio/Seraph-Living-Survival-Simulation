namespace Seraph.Core.Framework;

/// <summary>Small, persistent transfer model for fecal hand contamination and food handling.</summary>
public static class HandContamination
{
    public static FrameworkState AfterDefecation(FrameworkState state, IReadOnlyDictionary<string, double> shed)
        => state with { HandPathogens = PathogenLoads.Add(state.HandPathogens, shed) };

    public static IReadOnlyDictionary<string, double> TransferToFood(FrameworkState state, double transferFraction)
        => PathogenLoads.Scale(state.HandPathogens, Math.Clamp(transferFraction, 0, 1));

    public static FrameworkState AfterWashingProduce(FrameworkState state, WaterBatch water,
        double waterTransferFraction = .25, double cleanWaterRetention = .1, double dirtyWaterRetention = .5)
    {
        var contamination = water.PathogensPerL;
        if (water.EntericHazardPerL > 0)
            contamination = PathogenLoads.WithGenericEnteric(contamination, water.EntericHazardPerL);
        var clean = water.TotalEntericHazardPerL <= .001
            && !water.PathogensPerL.Any(p => Pathogens.Enteric(p.Key) && p.Value > .001);
        var acquired = clean ? PathogenLoads.Empty : PathogenLoads.Scale(contamination, waterTransferFraction);
        return state with { HandPathogens = PathogenLoads.Add(PathogenLoads.Scale(state.HandPathogens,
            clean ? Math.Clamp(cleanWaterRetention, 0, 1) : Math.Clamp(dirtyWaterRetention, 0, 1)), acquired) };
    }
}
