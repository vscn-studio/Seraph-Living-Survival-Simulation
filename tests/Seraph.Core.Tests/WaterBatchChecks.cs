using System.Text.Json;
using Seraph.Core.Framework;

internal static class WaterBatchChecks
{
    internal static void LargeReservoir()
    {
        void Check(bool condition, string message)
        { if (!condition) throw new Exception(message); }
        void Near(double actual, double expected)
            => Check(Math.Abs(actual - expected) < 1e-8, $"Water mixture changed conserved quantity: {actual} vs {expected}");

        var cold = new WaterBatch { VolumeL = 10000, TemperatureC = 20, Source = "cold" };
        var hot = new WaterBatch { VolumeL = 2000, TemperatureC = 80, SaltGramsPerL = 3,
            EntericHazardPerL = .6, ChemicalHazardPerL = .3, Turbidity = .9, Source = "hot",
            PathogensPerL = new Dictionary<string, double> { ["norovirus"] = .12 } };
        var mixed = cold;
        for (var i = 0; i < 80; i++) mixed = mixed.Mix(hot with { VolumeL = 25 });
        Check(mixed.Validate() is null && mixed.VolumeL == 12000, "Repeated pipe-sized mixing above 10,000 L failed");
        Near(mixed.TemperatureC * mixed.VolumeL, 20 * 10000 + 80 * 2000);
        Near(mixed.SaltGramsPerL * mixed.VolumeL, 6000);
        Near(mixed.EntericHazardPerL * mixed.VolumeL, 1200);
        Near(mixed.ChemicalHazardPerL, 0);
        Near(mixed.Turbidity * mixed.VolumeL, 1800);
        Near(mixed.PathogensPerL["norovirus"] * mixed.VolumeL, 240);

        const double maximum = 16 * 16 * 4 * 1000d;
        var full = mixed.Mix(cold with { VolumeL = maximum - mixed.VolumeL });
        Check(full.VolumeL == maximum && full.Validate() is null, "Maximum water batch volume rejected");
        var saved = JsonSerializer.Deserialize<WaterBatch>(JsonSerializer.Serialize(full))!;
        Check(saved == full with { PathogensPerL = saved.PathogensPerL }, "Large batch serialization changed quality");
        Near(saved.PathogensPerL["norovirus"] * saved.VolumeL, 240);
        Check(saved.Validate() is null && BathWaterTreatment.Advance(saved, 20, 30).Validate() is null,
            "Large batches cannot survive save/reload or normal water treatment");
        Check(full.Mix(new()).VolumeL == maximum, "Empty addition changed full reservoir");
        foreach (var volume in new[] { maximum + 1, double.NaN, double.PositiveInfinity, -1 })
            Check((cold with { VolumeL = volume }).Validate() is not null, "Invalid batch volume accepted");
        var rejected = false;
        try { full.Mix(cold with { VolumeL = 1 }); }
        catch (ArgumentException) { rejected = true; }
        Check(rejected, "Mixing above the maximum supported reservoir must remain invalid");
    }
}
