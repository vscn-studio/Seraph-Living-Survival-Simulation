using Seraph.Core.State;

namespace Seraph.Core.Framework;

public readonly record struct DigestionFlux(NutritionState Nutrition, double WaterL,
    double CarbohydrateKJ, double FatKJ, double ProteinKJ, double RemovedEnergyKJ);

/// <summary>One mass budget with independent food digestion and water absorption; normalized calendar seconds.</summary>
public static class DietaryMetabolism
{
    public const double CarbKJPerG = 4 * 4.184;
    public const double ProteinKJPerG = 4 * 4.184;
    public const double FatKJPerG = 9 * 4.184;
    public const double StomachCapacityG = 5000;

    public static DigestionFlux Digest(NutritionState diet, BodyState body, BodyProfile profile,
        double seconds, double efficiency, double bowelRoomG, double gramsPerHour = 1667, DigestionOptions? settings = null,
        double waterAbsorptionEfficiency = 1)
    {
        if (!double.IsFinite(seconds) || seconds < 0 || !double.IsFinite(efficiency) || efficiency < 0
            || !double.IsFinite(gramsPerHour) || gramsPerHour <= 0 || !double.IsFinite(bowelRoomG) || bowelRoomG < 0
            || !BodyProfile.In(waterAbsorptionEfficiency, 0, 1)
            || settings?.Validate() is not null) throw new ArgumentOutOfRangeException(nameof(seconds));
        if (seconds == 0) return new(diet, 0, 0, 0, 0, 0);
        settings ??= DefaultSettings;
        // Clone once per public settlement. Bounded substeps mutate only these private arrays.
        // Migrate only the obsolete pending mud-to-toxin queue. Already absorbed
        // waste and the body's existing blood burden must not be reset.
        var current = diet with { PendingWaterWasteMl = 0, WaterWasteSchema = 1, Pending = (double[])diet.Pending.Clone(),
            Reserve = (double[])diet.Reserve.Clone(), DeficiencyBurden = (double[])diet.DeficiencyBurden.Clone() };
        var physical = body;
        double waterTotal = 0, carbsTotal = 0, fatTotal = 0, proteinTotal = 0, removedTotal = 0;
        for (var elapsed = 0d; elapsed < seconds; elapsed += 8)
        {
            var step = DigestStep(current, physical, profile, Math.Min(8, seconds - elapsed), efficiency,
                Math.Max(0, bowelRoomG - (current.BowelProducedG - diet.BowelProducedG)), gramsPerHour, settings, waterAbsorptionEfficiency);
            current = step.Nutrition;
            physical = physical with { GutWaterL = Math.Max(0, physical.GutWaterL - step.WaterL),
                BodyWaterL = physical.BodyWaterL + step.WaterL, GutEnergyKJ = Math.Max(0, physical.GutEnergyKJ - step.RemovedEnergyKJ) };
            waterTotal += step.WaterL; carbsTotal += step.CarbohydrateKJ; fatTotal += step.FatKJ;
            proteinTotal += step.ProteinKJ; removedTotal += step.RemovedEnergyKJ;
        }
        return new(current, waterTotal, carbsTotal, fatTotal, proteinTotal, removedTotal);
    }

    private static readonly DigestionOptions DefaultSettings = new();
    private static DigestionFlux DigestStep(NutritionState diet, BodyState body, BodyProfile profile,
        double seconds, double efficiency, double bowelRoomG, double gramsPerHour, DigestionOptions settings, double waterAbsorptionEfficiency)
    {
        var p = diet.Pending;
        var r = diet.Reserve;
        var burden = diet.DeficiencyBurden;
        var dry = p[0] + p[1] + p[2] + p[3] + diet.StomachWasteG;
        var water = body.GutWaterL * 1000;
        var hours = seconds / 3600;
        double Empty(double amount, double halfLife) => DigestionOptions.Empty(amount, hours, halfLife, efficiency);
        // Dehydration, sleep and adrenaline slow food processing, not the water
        // needed to recover from them. Gut disease/injury has its own input.
        var waterProcessed = Math.Min(DigestionOptions.Empty(water, hours, settings.WaterHalfLifeHours, waterAbsorptionEfficiency),
            settings.WaterMlPerHour * waterAbsorptionEfficiency * hours);
        waterProcessed = Math.Min(waterProcessed, Math.Max(0, profile.ReferenceBodyWaterL - body.BodyWaterL) * 1000);
        var carbs = Empty(p[0], settings.CarbohydrateHalfLifeHours);
        var protein = Empty(p[1], settings.ProteinHalfLifeHours);
        var fat = Empty(p[2], settings.FatHalfLifeHours);
        var fiber = Empty(p[3], settings.ResidueHalfLifeHours);
        var waste = Empty(diet.StomachWasteG, settings.ResidueHalfLifeHours);
        // The bowel limits residue only; absorbable nutrients do not become extra waste.
        var residue = fiber + waste;
        var room = residue > 0 ? Math.Min(1, Math.Max(0, bowelRoomG) / residue) : 1;
        fiber *= room; waste *= room;
        // Protein occupies 2.25 g of the BP digestion quota per physical gram.
        // Unused protein quota is deliberately not reassigned to other foods.
        var requested = carbs + protein * settings.ProteinDigestSlowMul + fat + fiber + waste;
        var throughput = requested > 0 ? Math.Min(1, gramsPerHour * efficiency * hours / requested) : 1;
        carbs *= throughput; protein *= throughput; fat *= throughput; fiber *= throughput; waste *= throughput;
        residue = fiber + waste;
        var oldEnergy = p[0] * CarbKJPerG + p[1] * ProteinKJPerG + p[2] * FatKJPerG;
        // Keep the physical queue and its intake energy ledger in lockstep.
        var energyScale = oldEnergy > 0 ? body.GutEnergyKJ / oldEnergy : 0;
        var carbohydrateEnergy = carbs * CarbKJPerG * energyScale;
        var fatEnergy = fat * FatKJPerG * energyScale;
        var proteinEnergy = protein * ProteinKJPerG * energyScale;
        var removedEnergy = (carbs * CarbKJPerG + protein * ProteinKJPerG + fat * FatKJPerG) * energyScale;
        // Explicit energy-only API receipts carry no measured mass; settle their ledger once.
        if (oldEnergy == 0 && body.GutEnergyKJ > 0)
        {
            carbohydrateEnergy = Math.Min(Empty(body.GutEnergyKJ, settings.CarbohydrateHalfLifeHours),
                gramsPerHour * efficiency * hours * CarbKJPerG);
            removedEnergy = carbohydrateEnergy;
        }
        p[0] -= carbs; p[1] -= protein; p[2] -= fat; p[3] -= fiber;
        r[1] += protein;
        // Micros travel with edible dry matter, or with food water in a dry-free drink.
        // Plain drinking water is not a carrier and cannot accelerate an existing meal's micros.
        var fraction = dry > 0 ? Math.Clamp((carbs + protein + fat + fiber + waste) / dry, 0, 1)
            : diet.GutFoodWaterG > 0 ? Math.Clamp(waterProcessed / Math.Max(water, diet.GutFoodWaterG), 0, 1) : 0;
        for (var i = 4; i < p.Length; i++)
        {
            var def = Nutrients.All[i];
            // Trace nutrients remain available after the bulk meal has emptied and absorb
            // over their own slower window instead of disappearing with the first dry-matter pass.
            var id = def.Id;
            var halfLife = def.IsMicro
                ? MicroNutrition.HalfLifeHours(id) * settings.MicronutrientHalfLifeHours / 2
                : settings.MicronutrientHalfLifeHours;
            var capacity = def.IsElectrolyte ? waterAbsorptionEfficiency : efficiency;
            var microFraction = 1 - Math.Exp(-Math.Log(2) * hours * capacity / halfLife);
            var taken = p[i] * (def.IsMicro || def.IsElectrolyte ? microFraction : fraction); p[i] -= taken;
            if (def.IsMicro)
            {
                if (diet.Known[i])
                {
                    var daily = MicroNutrition.DailyPointsFor(id);
                    var accepted = id is "a" or "c" or "bcomplex"
                        ? daily * (MicroNutrition.CumulativeAbsorption(diet.Pending[i], daily)
                            - MicroNutrition.CumulativeAbsorption(diet.Pending[i] - taken, daily))
                        : taken;
                    (r[i], burden[i]) = MicroNutrition.Settle(r[i], burden[i], accepted, hours, daily);
                }
            }
            else if (def.Id == "electrolytes")
            {
                r[i] = Math.Clamp(r[i] + taken - seconds / 3600 * 3.5 / 24, 0, def.Maximum);
            }
            else r[i] = Math.Clamp(r[i] + taken, 0, def.Maximum);
        }
        var removedFoodWater = water > 0 ? diet.GutFoodWaterG * Math.Clamp(waterProcessed / water, 0, 1) : 0;
        var waterWaste = 0d;
        var next = diet with { Pending = p, Reserve = r, DeficiencyBurden = burden,
            GutMassG = Math.Max(0, diet.GutMassG - carbs - protein - fat - fiber - waste - removedFoodWater),
            GutFoodWaterG = Math.Max(0, diet.GutFoodWaterG - removedFoodWater),
            StomachWasteG = Math.Max(0, diet.StomachWasteG - waste), BowelProducedG = diet.BowelProducedG + residue,
            RepairEnergyKJ = diet.RepairEnergyKJ + proteinEnergy,
            FilteredMicroPoints = diet.FilteredMicroPoints,
            PendingWaterWasteMl = Math.Max(0, diet.PendingWaterWasteMl - waterWaste),
            AbsorbedWaterWasteMl = diet.AbsorbedWaterWasteMl + waterWaste };
        return new(next, waterProcessed / 1000, carbohydrateEnergy, fatEnergy, proteinEnergy, removedEnergy);
    }

    /// <summary>Returns conversion heat; rejected storage is dissipated, never recreated as fuel.</summary>
    public static double Deposit(ref double glycogen, ref double fat, BodyProfile profile, double carbEnergy, double fatEnergy)
    {
        var directFat = Math.Min(fatEnergy, Math.Max(0, profile.FatCapacityKJ - fat)); fat += directFat;
        var directCarb = Math.Min(carbEnergy, Math.Max(0, profile.GlycogenCapacityKJ - glycogen)); glycogen += directCarb;
        var overflow = carbEnergy - directCarb;
        var converted = Math.Min(overflow * .75, Math.Max(0, profile.FatCapacityKJ - fat)); fat += converted;
        return Math.Max(0, fatEnergy - directFat + overflow - converted);
    }

    public static double AvailableProtein(NutritionState n) => n.Reserve[1];
    public static NutritionState SpendProtein(NutritionState n, double grams)
    {
        var r = (double[])n.Reserve.Clone();
        var fresh = Math.Min(r[1], grams);
        var energy = r[1] > 0 ? n.RepairEnergyKJ * fresh / r[1] : 0;
        r[1] -= fresh;
        return n with { Reserve = r, RepairEnergyKJ = Math.Max(0, n.RepairEnergyKJ - energy) };
    }

    // Clinical repair and blood regeneration have already had first claim on fresh protein.
    public static (NutritionState Nutrition, BodyState Body, double GrowthEnergyKJ) FinishMaterials(
        NutritionState n, BodyState body, BodyProfile profile, double seconds, double rest, double support, double thermalScale)
    {
        var growth = Math.Min(n.Reserve[1], Math.Min(profile.MuscleMassMaxG - body.MuscleMassG,
            Math.Min(body.MuscleTrainingStimulus * 500, 100 * seconds / 86400 * rest * support)));
        growth = Math.Min(growth, (body.GlycogenEnergyKJ + body.FatEnergyKJ) / 5);
        n = SpendProtein(n, growth);
        var glycogen = body.GlycogenEnergyKJ; var fat = body.FatEnergyKJ;
        var cost = growth * 5; var paidGlycogen = Math.Min(glycogen, cost); glycogen -= paidGlycogen; fat -= cost - paidGlycogen;
        var heat = Deposit(ref glycogen, ref fat, profile, n.RepairEnergyKJ, 0);
        var reserves = (double[])n.Reserve.Clone(); reserves[1] = 0;
        return (n with { Reserve = reserves, RepairEnergyKJ = 0 }, body with {
            GlycogenEnergyKJ = glycogen, FatEnergyKJ = fat, MuscleMassG = body.MuscleMassG + growth,
            MuscleTrainingStimulus = Math.Max(0, body.MuscleTrainingStimulus - growth / 500),
            CoreTemperatureC = body.CoreTemperatureC + (cost + heat) * 1000 / (profile.CoreHeatCapacityJPerK * thermalScale)
        }, cost);
    }
}
