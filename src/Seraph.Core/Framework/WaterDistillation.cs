namespace Seraph.Core.Framework;

public static class WaterDistillation
{
    public const double SaltGramsPerItem = 20;
    public static (WaterBatch Remaining, WaterBatch Distilled, double ResidueG) Separate(WaterBatch input, double consumedL)
    {
        if (input.Validate() is not null || !double.IsFinite(consumedL) || consumedL < 0 || consumedL > input.VolumeL)
            throw new ArgumentException("Invalid distillation transfer.");
        var remaining = WaterHeating.Evaporate(input, consumedL);
        var residue = Math.Max(0, input.VolumeL * input.SaltGramsPerL - remaining.VolumeL * remaining.SaltGramsPerL);
        return (remaining, new WaterBatch { VolumeL = consumedL, TemperatureC = 20, Source = "distilled", OriginSource = "distilled",
            Unverified = false, TreatmentStage = WaterTreatmentStage.Disinfected }, residue);
    }
}
