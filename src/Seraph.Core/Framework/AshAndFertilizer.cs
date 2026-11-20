using Seraph.Core.State;

namespace Seraph.Core.Framework;

public readonly record struct AshFertilizerDose(double AshG, double ManureG, double Nitrogen, double Phosphorus, double Potassium)
{
    public string? Validate() => !BodyProfile.In(AshG, 0, 100000) || !BodyProfile.In(ManureG, 0, 100000)
        || !BodyProfile.In(Nitrogen, 0, 1000) || !BodyProfile.In(Phosphorus, 0, 1000) || !BodyProfile.In(Potassium, 0, 1000)
        ? "Invalid ash or fertilizer dose." : null;
}

public static class AshAndFertilizer
{
    public static AshFertilizerDose Ash(double grams) => new(Math.Clamp(grams, 0, 100000), 0,
        Math.Clamp(grams * .004, 0, 1000), Math.Clamp(grams * .001, 0, 1000), Math.Clamp(grams * .002, 0, 1000));

    public static AshFertilizerDose Manure(double grams, double pathogenLoad = 0)
    {
        var mass = Math.Clamp(grams, 0, 100000);
        var reduction = 1 - Math.Clamp(pathogenLoad, 0, 1) * .5;
        return new(0, mass, mass * .012 * reduction, mass * .006 * reduction, mass * .01 * reduction);
    }
}
