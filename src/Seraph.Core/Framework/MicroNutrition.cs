namespace Seraph.Core.Framework;

/// <summary>Each game day consumes one daily reference amount from the reserve.</summary>
public static class MicroNutrition
{
    public const double DailyPoints = 22;
    public static readonly string[] Groups = ["iron", "zinc", "a", "c", "calcium", "bcomplex"];
    public static double Burden(NutritionState n, string id) => n.Get(id) is { Known: true } s ? s.Deficiency : 0;
    public static bool Deficient(NutritionState n, string id) => Burden(n, id) > 0;
    public static double Gain(double value, double absorbedPoints) => Math.Clamp(value + Math.Max(0, absorbedPoints), 0, 100);
    public static double DailyPointsFor(string id) => id switch
    {
        "a" => 100d / 30,
        "c" or "bcomplex" => 100d / 7,
        _ => DailyPoints
    };
    public static double HalfLifeHours(string id) => id switch
    {
        "a" => 4,
        "c" => 1,
        "bcomplex" => 2,
        _ => 2
    };
    public static double CumulativeAbsorption(double points, double dailyPoints)
    {
        var servings = Math.Max(0, points) / dailyPoints;
        return Math.Min(servings, 1) + .5 * Math.Min(Math.Max(0, servings - 1), 3)
            + .1 * Math.Max(0, servings - 4);
    }
    public static double ImmuneBonus(NutritionState n) => .2 * Groups.Average(id =>
        n.Get(id) is { Known: true } s ? Math.Clamp(s.Reserve / 20, 0, 1) : 0);

    // Split threshold crossings so only time below 20 accumulates deficiency.
    public static (double Reserve, double Burden) Settle(double reserve, double burden, double intake, double hours, double dailyPoints = DailyPoints)
    {
        if (hours <= 0) return (reserve, burden);
        var delta = intake - dailyPoints * hours / 24;
        var end = Math.Clamp(reserve + delta, 0, 100);
        if (reserve < 20 && end >= 20)
        {
            var lowHours = hours * (20 - reserve) / delta;
            burden = Math.Max(0, Math.Min(1, burden + lowHours / 24) - (hours - lowHours) / 12);
        }
        else if (reserve >= 20 && end < 20)
        {
            var highHours = hours * (reserve - 20) / -delta;
            burden = Math.Min(1, Math.Max(0, burden - highHours / 12) + (hours - highHours) / 24);
        }
        else burden = Math.Clamp(burden + hours * (end < 20 ? 1d / 24 : -1d / 12), 0, 1);
        return (end, burden);
    }
}
