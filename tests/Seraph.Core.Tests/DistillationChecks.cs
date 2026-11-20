using Seraph.Core.Framework;

internal static class DistillationChecks
{
    internal static void Conservation()
    {
        foreach (var salinity in new[] { 0d, 35, 400 })
        {
            var water = new WaterBatch { VolumeL = 3, SaltGramsPerL = salinity, TemperatureC = 100,
                ChemicalHazardPerL = .5, EntericHazardPerL = 1, Turbidity = .4 };
            double salt = 0, output = 0;
            for (var i = 0; i < 300; i++)
            {
                var (remaining, distilled, residue) = WaterDistillation.Separate(water, Math.Min(.01, water.VolumeL));
                salt += residue; output += distilled.VolumeL; water = remaining;
                if (distilled.Validate() is not null || distilled.SaltGramsPerL != 0 || distilled.TotalEntericHazardPerL != 0
                    || distilled.ChemicalHazardPerL != 0 || distilled.Turbidity != 0 || distilled.Source != "distilled")
                    throw new Exception("Distillation did not separate dissolved and biological contamination.");
                if (Math.Abs(salt + water.VolumeL * water.SaltGramsPerL - salinity * 3) > 1e-7)
                    throw new Exception("Salt disappeared during distillation.");
            }
            if (Math.Abs(output - 3) > 1e-8 || Math.Abs(salt - salinity * 3) > 1e-7) throw new Exception("Dry distillation lost mass.");
        }
        var mixed = new WaterBatch { VolumeL = 1, SaltGramsPerL = 35 }.Mix(new() { VolumeL = 1, Source = "distilled" });
        var separated = WaterDistillation.Separate(mixed, 2);
        if (separated.ResidueG != 35) throw new Exception("Mixed seawater lost salt.");
    }
}
