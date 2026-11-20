namespace Seraph.Core.Framework;

/// <summary>Gameplay heat budget: 20 kJ/s maximum, 4.184 kJ/L/K, 2257 kJ/L latent heat.</summary>
public static class WaterHeating
{
    public static (WaterBatch Water, double EvaporatedL) Advance(WaterBatch water, double furnaceC, double seconds)
    {
        if (water.Validate() is not null || !double.IsFinite(furnaceC) || !double.IsFinite(seconds) || seconds < 0)
            throw new ArgumentException("Invalid water heating input.");
        if (water.VolumeL == 0 || seconds == 0) return (water, 0);
        if (furnaceC < water.TemperatureC)
        {
            var cooling = Math.Min(20, (water.TemperatureC - furnaceC) * .1) * seconds / (water.VolumeL * 4.184);
            return (AtTemperature(water, Math.Max(Math.Clamp(furnaceC, -80, 110), water.TemperatureC - cooling)), 0);
        }
        var power = Math.Clamp((furnaceC - water.TemperatureC) * .1, 0, 20);
        var energy = power * seconds;
        var warmup = Math.Max(0, Math.Min(100, furnaceC) - water.TemperatureC) * water.VolumeL * 4.184;
        var used = Math.Min(energy, warmup);
        var temperature = Math.Min(100, water.TemperatureC + used / (water.VolumeL * 4.184));
        var heated = AtTemperature(water, temperature);
        var evaporated = temperature >= 100 && furnaceC > 100 ? Math.Min(water.VolumeL, (energy - used) / 2257) : 0;
        return (Evaporate(heated, evaporated), evaporated);
    }

    public static WaterBatch AtTemperature(WaterBatch water, double temperature) => water with
    {
        TemperatureC = Math.Clamp(temperature, -80, 110),
        PathogensPerL = temperature >= 100 ? PathogenLoads.Empty : water.PathogensPerL,
        EntericHazardPerL = temperature >= 100 ? 0 : water.EntericHazardPerL,
        Unverified = temperature >= 100 ? false : water.Unverified,
        TreatmentStage = temperature >= 100 ? WaterTreatmentStage.Disinfected : water.TreatmentStage
    };

    public static WaterBatch Evaporate(WaterBatch water, double litres)
    {
        var remaining = Math.Max(0, water.VolumeL - Math.Clamp(litres, 0, water.VolumeL));
        if (remaining == 0) return water with { VolumeL = 0 };
        var concentration = water.VolumeL / remaining;
        // Concentrations saturate at the model's dissolved/suspended limits; excess is residue.
        return water with { VolumeL = remaining, SaltGramsPerL = Math.Min(400, water.SaltGramsPerL * concentration),
            PathogensPerL = PathogenLoads.Scale(water.PathogensPerL, concentration),
            EntericHazardPerL = Math.Min(100, water.EntericHazardPerL * concentration),
            ChemicalHazardPerL = 0,
            Turbidity = Math.Min(1, water.Turbidity * concentration),
            BaselineTurbidity = Math.Min(1, water.NaturalTurbidity * concentration) };
    }
}
