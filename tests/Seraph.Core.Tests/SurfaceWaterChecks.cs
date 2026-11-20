using Seraph.Core.Framework;

internal static class SurfaceWaterChecks
{
    internal static void Sources()
    {
        var options = new SurfaceWaterOptions();
        foreach (var (kind, dirt, hazard) in new[] { ("distilled", 0d, 0d), ("rain", .01, .002),
            ("icemelt", .02, .005), ("protectedwell", .02, .005), ("groundwater", .04, .01),
            ("spring", .04, .01), ("shallowwell", .07, .02), ("river", .10, .04),
            ("lake", .16, .08), ("pond", .30, .15), ("swamp", .50, .25), ("surface", .20, .08) })
        {
            var batch = SurfaceWater.Apply(new() { VolumeL = 1, SaltGramsPerL = 3 }, kind, options);
            if (batch.Turbidity != dirt || batch.EntericHazardPerL != hazard || batch.PathogensPerL.Count != 0
                || batch.Source != kind || batch.OriginSource != kind || batch.Validate() is not null)
                throw new Exception("Water source baseline or provenance failed: " + kind);
            if (SurfaceWater.BaseTurbidity(kind, options) != dirt)
                throw new Exception("Displayed source baseline diverged from applied turbidity: " + kind);
            var filtered = WaterFiltration.Process(batch, true, true);
            if (Math.Abs(filtered.TotalEntericHazardPerL - hazard * .15) > 1e-10 || filtered.SaltGramsPerL != 3)
                throw new Exception("Filtering lost dose or removed salt.");
            var boiled = WaterHeating.AtTemperature(batch, 100);
            if (boiled.TotalEntericHazardPerL != 0 || boiled.Turbidity != dirt || boiled.SaltGramsPerL != 3)
                throw new Exception("Boiling changed solids/salt or retained microbes.");
            if (batch.PotentialBloodWasteMl(.25) != 0 || boiled.PotentialBloodWasteMl(.25) != 0)
                throw new Exception("Suspended mud became blood toxicity.");
            var mixed = batch.Mix(new() { VolumeL = 3, Source = "distilled" });
            if (Math.Abs(mixed.TotalEntericHazardPerL - hazard / 4) > 1e-10
                || Math.Abs(mixed.Turbidity - dirt / 4) > 1e-10 || mixed.TemperatureC != batch.TemperatureC)
                throw new Exception("Reservoir dilution did not conserve concentrations.");
        }
        var custom = SurfaceWater.Apply(new() { VolumeL = 1 }, "pond", options with { PondShigellaPerL = 10 });
        if (custom.TotalEntericHazardPerL != .15 || custom.PathogensPerL.Count != 0)
            throw new Exception("Legacy species preset invented a named pathogen.");
        if (SurfaceWater.BaseTurbidity("pond", options with { PondTurbidity = .36 }) != .36)
            throw new Exception("Configured water baseline was not reflected in its source display.");
        var dirty = custom with { Turbidity = .5 };
        var clean = SurfaceWater.Apply(new() { VolumeL = 1 }, "rain", options);
        var blend = dirty.Mix(clean);
        var treated = WaterFiltration.Process(blend, true, true);
        if (Math.Abs(blend.AddedTurbidity - .1) > 1e-10 || Math.Abs(treated.AddedTurbidity - .008) > 1e-10
            || clean.AddedTurbidity != 0 || WaterHeating.AtTemperature(clean, 60).AddedTurbidity != 0)
            throw new Exception("Mixing, filtration or temperature fabricated/lost pollution turbidity.");
        foreach (var (dirt, tier) in new[] { (0d, 0), (.02, 0), (.08, 1), (.20, 2), (.40, 3), (.70, 4), (1d, 5) })
            if (SurfaceWater.ClarityTier(dirt) != tier) throw new Exception("Clarity boundary changed.");
        if ((options with { PondShigellaPerL = double.NaN }).Valid) throw new Exception("Invalid water presets accepted.");
    }
}
