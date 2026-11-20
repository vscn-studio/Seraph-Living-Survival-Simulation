using Seraph.Core.State;

namespace Seraph.Core.Framework;

/// <summary>Sustained low-glycogen fat use, a bounded gameplay state rather than a diagnosis.
/// Only accepted fuel flows and biological seconds advance it.</summary>
public static class Ketosis
{
    public const double SymptomThreshold = .25;
    public static bool Symptomatic(double level) => level >= SymptomThreshold;
    public static double Performance(double level) => 1 - .15 * Math.Clamp((level - .25) / .75, 0, 1);
    public static double Advance(double old, double glycogenFraction, double glycogenKJ, double fatKJ, double muscleKJ, double seconds)
    {
        if (!BodyProfile.In(old, 0, 1) || !BodyProfile.In(glycogenFraction, 0, 1)
            || !double.IsFinite(glycogenKJ) || glycogenKJ < 0 || !double.IsFinite(fatKJ) || fatKJ < 0
            || !double.IsFinite(muscleKJ) || muscleKJ < 0 || !BodyProfile.In(seconds, 0, 8))
            throw new ArgumentOutOfRangeException(nameof(old));
        var total = glycogenKJ + fatKJ + muscleKJ;
        var target = Math.Clamp((.10 - glycogenFraction) / .10, 0, 1)
            * (total > 0 ? Math.Clamp((fatKJ / total - .50) / .50, 0, 1) : 0);
        var tau = target > old ? 6 * 3600 : 2 * 3600;
        return old + (target - old) * (1 - Math.Exp(-seconds / tau));
    }
}
