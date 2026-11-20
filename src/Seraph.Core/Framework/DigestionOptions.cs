using Seraph.Core.State;
namespace Seraph.Core.Framework;

/// <summary>Normalized game hours; food and water use separate capacity inputs.</summary>
public sealed record DigestionOptions
{
    public double WaterHalfLifeHours { get; init; } = 1d / 3;
    public double CarbohydrateHalfLifeHours { get; init; } = 1.5;
    public double ProteinHalfLifeHours { get; init; } = 2;
    public double FatHalfLifeHours { get; init; } = 3;
    public double ResidueHalfLifeHours { get; init; } = 3;
    public double MicronutrientHalfLifeHours { get; init; } = 2;
    public double ProteinDigestSlowMul { get; init; } = 2.25;
    public double WaterMlPerHour { get; init; } = 2000;
    public string? Validate() => !BodyProfile.In(WaterHalfLifeHours, .01, 168) || !BodyProfile.In(CarbohydrateHalfLifeHours, .01, 168)
        || !BodyProfile.In(ProteinHalfLifeHours, .01, 168) || !BodyProfile.In(FatHalfLifeHours, .01, 168)
        || !BodyProfile.In(ResidueHalfLifeHours, .01, 168) || !BodyProfile.In(MicronutrientHalfLifeHours, .01, 168)
        || !BodyProfile.In(ProteinDigestSlowMul, 1, 10)
        || !BodyProfile.In(WaterMlPerHour, 1, 10000) ? "Invalid digestion half-life or water capacity." : null;
    public static double Empty(double amount, double hours, double halfLife, double capacity)
        => amount * (1 - Math.Exp(-Math.Log(2) * hours * capacity / halfLife));
}
