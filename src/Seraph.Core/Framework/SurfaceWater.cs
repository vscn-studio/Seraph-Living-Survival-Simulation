namespace Seraph.Core.Framework;

public sealed record SurfaceWaterOptions
{
    public double PondTurbidity { get; init; } = .30;
    public double LakeTurbidity { get; init; } = .16;
    public double RiverTurbidity { get; init; } = .1;
    // Legacy configuration names retained as barrier-load units/L,
    // not the normalized hazard/L stored by WaterBatch.
    public double PondShigellaPerL { get; init; } = 50;
    public double LakeShigellaPerL { get; init; } = 35;
    public double RiverShigellaPerL { get; init; } = 20;
    public bool Valid => new[] { PondTurbidity, LakeTurbidity, RiverTurbidity }.All(x => double.IsFinite(x) && x is >= 0 and <= 1)
        && new[] { PondShigellaPerL, LakeShigellaPerL, RiverShigellaPerL }.All(x => double.IsFinite(x) && x is >= 0 and <= 100);
}

public static class SurfaceWater
{
    // Added suspended solids modestly raise unidentified exposure. Natural mud
    // is already represented by the source baseline; solids never identify a species.
    public static double TurbidityHazard(double addedTurbidity) => .03 * Math.Clamp(addedTurbidity, 0, 1);
    public static double BaseTurbidity(string? kind, SurfaceWaterOptions? options = null)
    {
        options ??= new SurfaceWaterOptions();
        return kind switch
        {
            "distilled" => 0,
            "rain" => .01,
            "icemelt" => .02,
            "protectedwell" => .02,
            "groundwater" or "spring" => .04,
            "shallowwell" => .07,
            "river" => options.RiverTurbidity,
            "lake" => options.LakeTurbidity,
            "pond" => options.PondTurbidity,
            "swamp" => .50,
            "surface" => .20,
            _ => 0
        };
    }

    public static WaterBatch Apply(WaterBatch batch, string kind, SurfaceWaterOptions options)
    {
        var (dirt, hazard) = kind switch {
            "distilled" => (0d, 0d),
            "rain" => (.01, .002),
            "icemelt" => (.02, .005),
            "protectedwell" => (.02, .005),
            "groundwater" or "spring" => (.04, .01),
            "shallowwell" => (.07, .02),
            "river" => (options.RiverTurbidity, .04),
            "lake" => (options.LakeTurbidity, .08),
            "pond" => (options.PondTurbidity, .15),
            "swamp" => (.50, .25),
            _ => (.20, .08)
        };
        // Natural provenance supplies unidentified microbial load, never an invented species.
        // The legacy Shigella configuration fields remain readable but no longer seed disease.
        return batch with { Source = kind, OriginSource = kind, Turbidity = dirt, BaselineTurbidity = dirt,
            EntericHazardPerL = hazard, PathogensPerL = PathogenLoads.Empty, ChemicalHazardPerL = 0 };
    }

    public static int ClarityTier(double turbidity) => turbidity <= .02 ? 0 : turbidity <= .08 ? 1
        : turbidity <= .20 ? 2 : turbidity <= .40 ? 3 : turbidity <= .70 ? 4 : 5;
}
