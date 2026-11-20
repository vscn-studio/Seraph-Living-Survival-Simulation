using Seraph.Core.Framework;

internal static class WaterHeatingChecks
{
    internal static void Boiling()
    {
        var raw = new WaterBatch { VolumeL = 2, TemperatureC = 99, EntericHazardPerL = 2,
            SaltGramsPerL = 10, Turbidity = .1, ChemicalHazardPerL = .01 };
        void Check(bool condition) { if (!condition) throw new Exception("Water heating invariant failed."); }
        Check(WaterHeating.AtTemperature(raw, 99).EntericHazardPerL == 2);
        var boiled = WaterHeating.AtTemperature(raw, 100);
        Check(boiled.EntericHazardPerL == 0 && !boiled.Unverified && boiled.SaltGramsPerL == 10);
        Check(WaterHeating.AtTemperature(boiled, 20).EntericHazardPerL == 0);
        Check(boiled.Mix(raw).EntericHazardPerL == 1);
        var (heated, evaporated) = WaterHeating.Advance(raw, 600, 60);
        Check(heated.TemperatureC == 100 && evaporated > 0 && heated.VolumeL < raw.VolumeL);
        Check(Math.Abs(heated.SaltGramsPerL * heated.VolumeL - 20) < 1e-9);
        Check(Math.Abs(heated.VolumeL + evaporated - 2) < 1e-9);
        Check(heated.ChemicalHazardPerL == 0 && heated.Turbidity > raw.Turbidity);
        Check(WaterHeating.Advance(raw, 99, 60).EvaporatedL == 0);
        var cooled = WaterHeating.Advance(boiled, 20, 10);
        Check(cooled.EvaporatedL == 0 && cooled.Water.TemperatureC is > 20 and < 100 && cooled.Water.EntericHazardPerL == 0);
        Check(WaterHeating.Advance(raw, 600, 10000).Water.VolumeL == 0);
        var one = WaterHeating.Advance(raw, 600, 1).Water;
        for (var i = 0; i < 9; i++) one = WaterHeating.Advance(one, 600, 1).Water;
        Check(Math.Abs(one.VolumeL - WaterHeating.Advance(raw, 600, 10).Water.VolumeL) < 1e-9);
    }

    internal static void BathTreatment()
    {
        var warm = new WaterBatch { VolumeL = 20, TemperatureC = 70, EntericHazardPerL = 1,
            ChemicalHazardPerL = .2, Turbidity = .3,
            PathogensPerL = new Dictionary<string, double> { ["shigella"] = 1 } };
        var oneStep = BathWaterTreatment.Advance(warm, 20, 600);
        var split = BathWaterTreatment.Advance(BathWaterTreatment.Advance(warm, 20, 300), 20, 300);
        void Check(bool condition, string message = "Bath treatment invariant failed.")
        { if (!condition) throw new Exception(message); }
        Check(oneStep.TemperatureC is > 20 and < 70 && oneStep.PathogensPerL["shigella"] < 1
            && oneStep.EntericHazardPerL < 1, "Warm water should cool and reduce pathogens.");
        Check(Math.Abs(oneStep.TemperatureC - split.TemperatureC) < 1e-10
            && Math.Abs(oneStep.PathogensPerL["shigella"] - split.PathogensPerL["shigella"]) < 1e-10,
            "Treatment must be partition independent.");
        Check(oneStep.ChemicalHazardPerL == warm.ChemicalHazardPerL && oneStep.Turbidity == warm.Turbidity,
            "Heating cannot remove chemical or physical contamination.");
        Check(BathWaterTreatment.Advance(warm with { TemperatureC = 20 }, 10, 3600).PathogensPerL["shigella"]
            == warm.PathogensPerL["shigella"], "Cool water must not gain thermal disinfection.");
        var boiling = BathWaterTreatment.Advance(warm with { TemperatureC = 100 }, 20, 1);
        Check(boiling.PathogensPerL.Count == 0 && boiling.EntericHazardPerL == 0,
            "Existing 100 C disinfection semantics must remain exact.");
    }

}
