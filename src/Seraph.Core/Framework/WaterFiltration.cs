namespace Seraph.Core.Framework;

/// <summary>Mechanical filtration/adsorption; dissolved salt and temperature are conserved.</summary>
public static class WaterFiltration
{
    public const double MediaLifeL = 80;
    public const double ReservoirL = 10;
    public const double FlowLPerSecond = .025;
    public const double ClothFlowLPerSecond = .05;
    public const double DrainFlowLPerSecond = .1;

    public static WaterBatch Process(WaterBatch input, bool layeredFilter, bool cloth)
    {
        if (input.Validate() is { } error) throw new ArgumentException(error, nameof(input));
        if (layeredFilter && !cloth) throw new ArgumentException("A layered filter requires all three layers.");
        // An unlined diversion pot transports water without treating it again.
        if (!layeredFilter && !cloth) return input;
        return input with
        {
            Turbidity = input.Turbidity * (layeredFilter ? .08 : cloth ? .45 : .8),
            BaselineTurbidity = input.NaturalTurbidity * (layeredFilter ? .08 : cloth ? .45 : .8),
            PathogensPerL = PathogenLoads.Scale(input.PathogensPerL, layeredFilter ? .15 : cloth ? .8 : 1),
            EntericHazardPerL = input.EntericHazardPerL * (layeredFilter ? .15 : cloth ? .8 : 1),
            ChemicalHazardPerL = 0,
            // Filtering cannot certify water as sterile, or remove its dissolved salt.
            Unverified = true,
            Source = layeredFilter ? "filtered" : "settled",
            TreatmentStage = layeredFilter ? WaterTreatmentStage.Filtered : input.TreatmentStage
        };
    }

    public static double TransferVolume(double storedL, double freeL, double seconds, double mediaRemainingL, bool requiresMedia,
        double flowLPerSecond = FlowLPerSecond)
    {
        if (new[] { storedL, freeL, seconds, mediaRemainingL, flowLPerSecond }.Any(x => !double.IsFinite(x) || x < 0)) return 0;
        return Math.Max(0, Math.Min(Math.Min(storedL, freeL),
            Math.Min(flowLPerSecond * Math.Min(seconds, 2), requiresMedia ? mediaRemainingL : ReservoirL)));
    }
}
