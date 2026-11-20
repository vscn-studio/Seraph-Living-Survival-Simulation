using Seraph.Core.State;

namespace Seraph.Core.Framework;

/// <summary>One physical water account with staged usable remaining water.</summary>
public static class Hydration
{
    public static int Stage(BodyState body, BodyProfile profile) => Stage(ReserveDisplay.HydrationG(body, profile));
    public static int Stage(double remainingG) => remainingG <= .01 ? 5
        : remainingG > 2000 ? 0 : Math.Clamp(1 + (int)Math.Floor((2000 - remainingG) / 500), 1, 4);
    public static double BloodMaximumFactor(int stage) => stage switch { 1 => .95, 2 => .9, 3 => .85, 4 => .78, 5 => .7, _ => 1 };
    public static double PerformanceFactor(int stage) => stage switch { 1 => .95, 2 => .9, 3 => .82, 4 => .7, 5 => .55, _ => 1 };
    public static double OrganFactor(int stage) => stage switch { 1 => .9, 2 => .75, 3 => .55, 4 => .35, 5 => .15, _ => 1 };
}
