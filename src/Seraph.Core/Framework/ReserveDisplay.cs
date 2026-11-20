using Seraph.Core.State;

namespace Seraph.Core.Framework;

/// <summary>Read-only projections; never write display scores back to physical stores.</summary>
public static class ReserveDisplay
{
    public const double HydrationCapacityG = 8000;
    public static double Score(double value, double maximum) => maximum > 0 ? Math.Clamp(value / maximum * 100, 0, 100) : 0;
    public static double WholeScore(double score) => score <= .01 ? 0 : Math.Max(1, Math.Round(Math.Clamp(score, 0, 100), MidpointRounding.AwayFromZero));
    // Usable hydration, rather than total body water. Water still in the gut is excluded.
    public static double HydrationG(BodyState body, BodyProfile profile)
        => Math.Clamp(HydrationCapacityG + (body.BodyWaterL - profile.ReferenceBodyWaterL) * 1000, 0, HydrationCapacityG);
}
