using Seraph.Core.Framework;

internal static class FiltrationChecks
{
    internal static void Treatment()
    {
        var source = new WaterBatch { VolumeL = 10, TemperatureC = 37, SaltGramsPerL = 35,
            Turbidity = .9, EntericHazardPerL = 2, ChemicalHazardPerL = .8 };
        var filtered = WaterFiltration.Process(source, true, true);
        var cloth = WaterFiltration.Process(source, false, true);
        var settled = WaterFiltration.Process(source, false, false);
        foreach (var result in new[] { filtered, cloth, settled })
        {
            Require(result.Validate() is null && result.VolumeL == source.VolumeL && result.TemperatureC == source.TemperatureC
                && result.SaltGramsPerL == source.SaltGramsPerL, "Treatment changed conserved water/salt/heat.");
            Require(result.EntericHazardPerL > 0 && result.Unverified, "Filter incorrectly certifies sterile water.");
        }
        Require(filtered.Turbidity < cloth.Turbidity && cloth.Turbidity < settled.Turbidity
            && filtered.ChemicalHazardPerL == 0 && cloth.ChemicalHazardPerL == 0,
            "Media does not provide distinct treatment.");
        Require(settled.EntericHazardPerL == source.EntericHazardPerL, "Sedimentation invented disinfection.");
        var diverted = source;
        for (var i = 0; i < 20; i++) diverted = WaterFiltration.Process(diverted, false, false);
        Require(diverted == source, "Chaining empty diversion pots changed water quality.");
        var clean = WaterFiltration.Process(source with { EntericHazardPerL = 0, Turbidity = 0, ChemicalHazardPerL = 0 }, true, true);
        Require(clean.EntericHazardPerL == 0 && clean.Turbidity == 0 && clean.ChemicalHazardPerL == 0, "Filtering contaminated clean water.");
    }
    internal static void FlowBounds()
    {
        Require(WaterFiltration.TransferVolume(10, 10, 1, 80, true) == .025, "Wrong nominal flow.");
        Require(WaterFiltration.TransferVolume(10, 0, 1, 80, true) == 0, "Full output loses water.");
        Require(WaterFiltration.TransferVolume(10, 10, 1, 0, true) == 0, "Spent media still works.");
        Require(WaterFiltration.TransferVolume(.01, 10, 1, 80, true) == .01, "Water overdraft.");
        Require(WaterFiltration.TransferVolume(10, .01, 1, 80, true) == .01, "Receiver overflow.");
        Require(WaterFiltration.TransferVolume(10, 10, 1, .01, true) == .01, "Media overdraft.");
        Require(WaterFiltration.TransferVolume(10, 10, double.NaN, 80, true) == 0, "Invalid tick accepted.");
        Require(WaterFiltration.TransferVolume(10, 10, 3600, 80, true) == .05, "Offline catch-up processed unbounded water.");
        Require(WaterFiltration.TransferVolume(10, 10, 1, 0, false, WaterFiltration.DrainFlowLPerSecond) == .1, "Wrong unlined diversion rate.");
        Require(WaterFiltration.TransferVolume(10, 10, 1, 80, true, WaterFiltration.ClothFlowLPerSecond) == .05, "Wrong cloth diversion rate.");
        Require(WaterFiltration.TransferVolume(10, 10, 1, 80, true, double.PositiveInfinity) == 0, "Invalid rate accepted.");
        double Minute(double step)
        {
            double total = 0;
            for (var i = 0; i < (int)(60 / step); i++) total += WaterFiltration.TransferVolume(10, 10, step, 80, true);
            return total;
        }
        Require(Math.Abs(Minute(1) - 1.5) < 1e-9 && Math.Abs(Minute(.25) - 1.5) < 1e-9,
            "Filter rate depends on server tick subdivision.");
    }
    private static void Require(bool condition, string message) { if (!condition) throw new Exception(message); }
}
